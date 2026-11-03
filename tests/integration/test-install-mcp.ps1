# P1-03 headless checks for scripts/install-mcp.ps1 (plain assertions, no Pester).
# Every case runs against a throwaway config under a temp directory whose path contains spaces; the real Claude
# Desktop config is never read or written (each call passes an explicit -ConfigPath). Always: fresh install,
# preservation of unrelated keys/servers, idempotent rerun, backup equals the original bytes, refusal of invalid JSON
# and wrong shapes with the file unchanged, replace of a stale entry, uninstall, dry-run writes nothing.
# With -Dotnet and -McpDll: the registered command/args launch the real built server and answer initialize.
param([string]$Dotnet, [string]$McpDll)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot)
$installer = Join-Path $root 'scripts/install-mcp.ps1'
if (!(Test-Path -LiteralPath $installer -PathType Leaf)) { throw "install-mcp checks failed: installer missing: $installer" }
$pwsh = (Get-Process -Id $PID).Path
$failures = [Collections.Generic.List[string]]::new()
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { $failures.Add($Message) } }

function Invoke-Installer([string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    $output = & $pwsh -NoProfile -File $installer @Arguments 2>&1 | ForEach-Object { "$_" }
    return [pscustomobject]@{ Code = $LASTEXITCODE; Output = ($output -join "`n") }
}
function Get-Bytes([string]$Path) { if (Test-Path -LiteralPath $Path) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($Path)) } else { $null } }
function Get-Backups([string]$Path) { @(Get-ChildItem -LiteralPath (Split-Path $Path) -Filter ((Split-Path $Path -Leaf) + '.bak-*') -File -ErrorAction SilentlyContinue) }
function Get-Leftovers([string]$Path) { @(Get-ChildItem -LiteralPath (Split-Path $Path) -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*.tmp*' }) }
# Leading commas stop PowerShell from enumerating a returned JsonObject into key/value pairs.
function Read-Node([string]$Path) { return , [System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($Path)) }
function Get-Prop($Object, [string]$Name) {
    if ($Object -isnot [System.Text.Json.Nodes.JsonObject]) { return $null }
    $value = $null
    if ($Object.TryGetPropertyValue($Name, [ref]$value)) { return , $value }
    return $null
}
function Compact($Node) { if ($null -eq $Node) { '<missing>' } else { $Node.ToJsonString() } }

$work = Join-Path ([IO.Path]::GetTempPath()) ('install mcp test ' + [Guid]::NewGuid().ToString('N'))
# Defence in depth: every default-location root the installer could consult points into the temp tree.
$savedEnvironment = @{}
foreach ($variable in 'APPDATA', 'LOCALAPPDATA', 'HOME') { $savedEnvironment[$variable] = [Environment]::GetEnvironmentVariable($variable, 'Process') }
$fakeRoots = Join-Path $work 'fake roots'
function Set-FakeRoots([string]$Base) {
    foreach ($pair in @(@('APPDATA', 'AppData/Roaming'), @('LOCALAPPDATA', 'AppData/Local'), @('HOME', 'home'))) {
        $path = Join-Path $Base $pair[1]
        [void][IO.Directory]::CreateDirectory($path)
        [Environment]::SetEnvironmentVariable($pair[0], $path, 'Process')
    }
}
try {
    Set-FakeRoots $fakeRoots
    Assert ((Get-Content -LiteralPath $installer -TotalCount 40) -contains '#Requires -Version 7.2') 'installer must declare #Requires -Version 7.2'
    $tools = Join-Path $work 'fake sdk dir'
    New-Item -ItemType Directory -Force $tools | Out-Null
    $fakeDotnet = Join-Path $tools $(if ($IsWindows) { 'dotnet.exe' } else { 'dotnet' })
    $fakeDll = Join-Path $tools 'Bim Mcp/Bim.Mcp.dll'
    New-Item -ItemType Directory -Force (Split-Path $fakeDll) | Out-Null
    [IO.File]::WriteAllText($fakeDotnet, 'fake')
    [IO.File]::WriteAllText($fakeDll, 'fake')
    $toolArgs = @('-Dotnet', $fakeDotnet, '-McpDll', $fakeDll)

    # --- Fresh install: missing directory and file are created; no backup of nothing ---
    $config = Join-Path $work 'Claude Config/fresh/claude_desktop_config.json'
    $run = Invoke-Installer (@('-ConfigPath', $config) + $toolArgs)
    Assert ($run.Code -eq 0) "fresh install exit $($run.Code): $($run.Output)"
    Assert (Test-Path -LiteralPath $config -PathType Leaf) 'fresh install did not create the config'
    if (Test-Path -LiteralPath $config) {
        $entry = Get-Prop (Get-Prop (Read-Node $config) 'mcpServers') 'bim-agentic-os'
        Assert ($null -ne $entry) 'fresh install: mcpServers.bim-agentic-os missing'
        if ($null -ne $entry) {
            Assert ([string]$entry['command'] -eq [IO.Path]::GetFullPath($fakeDotnet)) "fresh install command: $(Compact $entry['command'])"
            Assert ((Compact $entry['args']) -eq (Compact ([System.Text.Json.Nodes.JsonNode]::Parse((ConvertTo-Json -InputObject @([IO.Path]::GetFullPath($fakeDll)) -Compress))))) "fresh install args: $(Compact $entry['args'])"
        }
    }
    Assert ((Get-Backups $config).Count -eq 0) 'fresh install must not write a backup'
    Assert ((Get-Leftovers $config).Count -eq 0) 'fresh install left a temp file'

    # --- Idempotent rerun: same bytes, no backup ---
    $before = Get-Bytes $config
    $run = Invoke-Installer (@('-ConfigPath', $config) + $toolArgs)
    Assert ($run.Code -eq 0) "rerun exit $($run.Code): $($run.Output)"
    Assert ((Get-Bytes $config) -eq $before) 'rerun changed the config'
    Assert ((Get-Backups $config).Count -eq 0) 'rerun wrote a backup'
    Assert ($run.Output -match 'UNCHANGED') "rerun must report UNCHANGED: $($run.Output)"

    # --- Preserve unrelated keys and servers; backup equals the original bytes ---
    $config = Join-Path $work 'Claude Config/existing/claude_desktop_config.json'
    New-Item -ItemType Directory -Force (Split-Path $config) | Out-Null
    $original = "{`n  `"globalShortcut`": `"Ctrl+Space`",`n  `"ratio`": 1.50,`n  `"stamp`": `"2026-01-02T03:04:05Z`",`n  `"name`": `"caf\u00e9 \u00f1`",`n  `"nested`": { `"flag`": true, `"none`": null, `"list`": [1, `"two`", { `"three`": 3 }] },`n  `"mcpServers`": {`n    `"filesystem`": { `"command`": `"npx`", `"args`": [`"-y`", `"@modelcontextprotocol/server-filesystem`", `"C:\\Users\\me\\Desktop`"], `"env`": { `"DEBUG`": `"1`" } },`n    `"bim-agentic-os`": { `"command`": `"old-dotnet`", `"args`": [`"old.dll`"], `"env`": { `"STALE`": `"x`" } },`n    `"zeta`": { `"command`": `"z`" }`n  }`n}`n"
    [IO.File]::WriteAllText($config, $original, [Text.UTF8Encoding]::new($false))
    $originalBytes = Get-Bytes $config
    $originalNode = Read-Node $config
    $run = Invoke-Installer (@('-ConfigPath', $config) + $toolArgs)
    Assert ($run.Code -eq 0) "install over existing exit $($run.Code): $($run.Output)"
    $after = Read-Node $config
    foreach ($key in 'globalShortcut', 'ratio', 'stamp', 'name', 'nested') {
        Assert ((Compact (Get-Prop $after $key)) -eq (Compact (Get-Prop $originalNode $key))) "key $key changed: $(Compact (Get-Prop $after $key))"
    }
    Assert ((Compact (Get-Prop $after 'ratio')) -eq '1.50') "number text must be preserved: $(Compact (Get-Prop $after 'ratio'))"
    $servers = Get-Prop $after 'mcpServers'
    foreach ($server in 'filesystem', 'zeta') {
        Assert ((Compact (Get-Prop $servers $server)) -eq (Compact (Get-Prop (Get-Prop $originalNode 'mcpServers') $server))) "server $server changed"
    }
    $entry = Get-Prop $servers 'bim-agentic-os'
    Assert ($null -ne $entry -and [string]$entry['command'] -eq [IO.Path]::GetFullPath($fakeDotnet)) "stale entry not replaced: $(Compact $entry)"
    Assert ($null -ne $entry -and $null -eq (Get-Prop $entry 'env')) 'stale entry keys survived the replace'
    Assert (@($servers | ForEach-Object { $_.Key }) -join ',' -eq 'filesystem,bim-agentic-os,zeta') "server order: $(@($servers | ForEach-Object { $_.Key }) -join ',')"
    $backups = Get-Backups $config
    Assert ($backups.Count -eq 1) "install over existing must write exactly one backup, got $($backups.Count)"
    if ($backups.Count -eq 1) { Assert ((Get-Bytes $backups[0].FullName) -eq $originalBytes) 'backup bytes differ from the original' }
    Assert ($run.Output -match [regex]::Escape('BACKUP')) "install must print the backup path: $($run.Output)"
    Assert ((Get-Leftovers $config).Count -eq 0) 'install over existing left a temp file'
    $before = Get-Bytes $config
    $run = Invoke-Installer (@('-ConfigPath', $config) + $toolArgs)
    Assert ($run.Code -eq 0 -and (Get-Bytes $config) -eq $before -and (Get-Backups $config).Count -eq 1) 'rerun over existing config was not a no-op'

    # --- Custom server name only touches that name ---
    $run = Invoke-Installer (@('-ConfigPath', $config, '-ServerName', 'bim dev') + $toolArgs)
    $servers = Get-Prop (Read-Node $config) 'mcpServers'
    Assert ($run.Code -eq 0 -and $null -ne (Get-Prop $servers 'bim dev') -and $null -ne (Get-Prop $servers 'bim-agentic-os')) "custom server name: $($run.Output)"

    # --- Refusals leave the file byte-identical, without backups or temp files ---
    $cases = [ordered]@{
        'invalid JSON' = "{ `"mcpServers`": { `"x`": "
        'empty file' = ''
        'array root' = '[]'
        'mcpServers not an object' = '{ "mcpServers": [] }'
        'duplicate keys' = '{ "a": 1, "a": 2 }'
    }
    $index = 0
    foreach ($case in $cases.GetEnumerator()) {
        $index++
        $bad = Join-Path $work "Claude Config/refuse $index/claude_desktop_config.json"
        New-Item -ItemType Directory -Force (Split-Path $bad) | Out-Null
        [IO.File]::WriteAllText($bad, $case.Value)
        $bytes = Get-Bytes $bad
        foreach ($mode in @(@(), @('-Uninstall'))) {
            $run = Invoke-Installer (@('-ConfigPath', $bad) + $toolArgs + $mode)
            Assert ($run.Code -ne 0) "$($case.Key) $mode accepted: $($run.Output)"
            Assert ((Get-Bytes $bad) -eq $bytes) "$($case.Key) $mode changed the file"
            Assert ((Get-Backups $bad).Count -eq 0 -and (Get-Leftovers $bad).Count -eq 0) "$($case.Key) $mode left a backup or temp file"
        }
    }

    # --- Missing server binary is refused before anything is written ---
    $missing = Join-Path $work 'Claude Config/missing dll/claude_desktop_config.json'
    $run = Invoke-Installer @('-ConfigPath', $missing, '-Dotnet', $fakeDotnet, '-McpDll', (Join-Path $work 'nope/Bim.Mcp.dll'))
    Assert ($run.Code -ne 0 -and !(Test-Path -LiteralPath (Split-Path $missing))) "missing dll accepted or wrote: $($run.Output)"

    # --- Dry run writes nothing ---
    $dry = Join-Path $work 'Claude Config/dry/claude_desktop_config.json'
    $run = Invoke-Installer (@('-ConfigPath', $dry, '-DryRun') + $toolArgs)
    Assert ($run.Code -eq 0 -and !(Test-Path -LiteralPath (Split-Path $dry))) "dry run on a missing config wrote: $($run.Output)"
    Assert ($run.Output -match 'DRY RUN' -and $run.Output -match [regex]::Escape('bim-agentic-os')) "dry run must show the planned change: $($run.Output)"
    $before = Get-Bytes $config
    $backupCount = (Get-Backups $config).Count
    foreach ($mode in @(@('-DryRun', '-ServerName', 'another'), @('-DryRun', '-Uninstall'))) {
        $run = Invoke-Installer (@('-ConfigPath', $config) + $toolArgs + $mode)
        Assert ($run.Code -eq 0 -and (Get-Bytes $config) -eq $before -and (Get-Backups $config).Count -eq $backupCount -and (Get-Leftovers $config).Count -eq 0) "dry run $mode wrote: $($run.Output)"
    }

    # --- Uninstall removes only that entry, with a backup; a second uninstall is a no-op ---
    $beforeBytes = Get-Bytes $config
    $beforeNode = Read-Node $config
    $run = Invoke-Installer @('-ConfigPath', $config, '-Uninstall')
    Assert ($run.Code -eq 0) "uninstall exit $($run.Code): $($run.Output)"
    $after = Read-Node $config
    $servers = Get-Prop $after 'mcpServers'
    Assert ($null -eq (Get-Prop $servers 'bim-agentic-os')) 'uninstall left the entry'
    foreach ($server in 'filesystem', 'zeta', 'bim dev') {
        Assert ((Compact (Get-Prop $servers $server)) -eq (Compact (Get-Prop (Get-Prop $beforeNode 'mcpServers') $server))) "uninstall changed server $server"
    }
    foreach ($key in 'globalShortcut', 'ratio', 'stamp', 'name', 'nested') {
        Assert ((Compact (Get-Prop $after $key)) -eq (Compact (Get-Prop $beforeNode $key))) "uninstall changed key $key"
    }
    $backups = @(Get-Backups $config | Sort-Object Name)
    Assert ($backups.Count -eq $backupCount + 1) "uninstall must write one backup, got $($backups.Count - $backupCount)"
    Assert (@($backups | Where-Object { (Get-Bytes $_.FullName) -eq $beforeBytes }).Count -ge 1) 'uninstall backup differs from the pre-uninstall bytes'
    $before = Get-Bytes $config
    $run = Invoke-Installer @('-ConfigPath', $config, '-Uninstall')
    Assert ($run.Code -eq 0 -and (Get-Bytes $config) -eq $before -and (Get-Backups $config).Count -eq $backups.Count -and $run.Output -match 'UNCHANGED') "second uninstall not a no-op: $($run.Output)"
    $run = Invoke-Installer @('-ConfigPath', (Join-Path $work 'Claude Config/none/claude_desktop_config.json'), '-Uninstall')
    Assert ($run.Code -eq 0 -and !(Test-Path -LiteralPath (Join-Path $work 'Claude Config/none'))) "uninstall without a config must not create one: $($run.Output)"

    # --- Linux has no Claude Desktop default: an explicit -ConfigPath is required ---
    if ($IsLinux) {
        $run = Invoke-Installer (@('-DryRun') + $toolArgs)
        Assert ($run.Code -ne 0) "Linux without -ConfigPath accepted: $($run.Output)"
    }

    # --- mcpServers: null counts as missing on install and as nothing to remove on uninstall ---
    $nullServers = Join-Path $work 'Claude Config/null servers/claude_desktop_config.json'
    New-Item -ItemType Directory -Force (Split-Path $nullServers) | Out-Null
    [IO.File]::WriteAllText($nullServers, '{ "keep": "me", "mcpServers": null }')
    $run = Invoke-Installer @('-ConfigPath', $nullServers, '-Uninstall')
    Assert ($run.Code -eq 0 -and $run.Output -match 'UNCHANGED') "uninstall with mcpServers null: $($run.Output)"
    $run = Invoke-Installer (@('-ConfigPath', $nullServers) + $toolArgs)
    Assert ($run.Code -eq 0) "install with mcpServers null exit $($run.Code): $($run.Output)"
    if ($run.Code -eq 0) {
        $after = Read-Node $nullServers
        Assert ((Compact (Get-Prop $after 'keep')) -eq '"me"' -and $null -ne (Get-Prop (Get-Prop $after 'mcpServers') 'bim-agentic-os')) "install with mcpServers null: $(Compact $after)"
        Assert ((Get-Backups $nullServers).Count -eq 1) 'install with mcpServers null must back up first'
    }

    # --- A symlinked config is refused (file and target untouched); must run on Linux ---
    $linkDirectory = Join-Path $work 'Claude Config/symlink'
    New-Item -ItemType Directory -Force $linkDirectory | Out-Null
    $target = Join-Path $linkDirectory 'real target.json'
    [IO.File]::WriteAllText($target, '{ "mcpServers": {} }')
    $link = Join-Path $linkDirectory 'claude_desktop_config.json'
    $linked = $false
    try { [void][IO.File]::CreateSymbolicLink($link, $target); $linked = $true }
    catch { if (!$IsWindows) { $failures.Add("symlink creation failed on a non-Windows host: $($_.Exception.Message)") } else { 'SKIP symlink case: creation not permitted on this Windows host' } }
    if ($linked) {
        $targetBytes = Get-Bytes $target
        foreach ($mode in @(@(), @('-Uninstall'), @('-DryRun'))) {
            $run = Invoke-Installer (@('-ConfigPath', $link) + $toolArgs + $mode)
            Assert ($run.Code -ne 0 -and $run.Output -match '(?i)symbolic link|symlink') "symlinked config $mode accepted: $($run.Output)"
            Assert ([IO.FileInfo]::new($link).LinkTarget -and (Get-Bytes $target) -eq $targetBytes) "symlinked config $mode changed the link or its target"
            Assert ((Get-Backups $link).Count -eq 0 -and (Get-Leftovers $link).Count -eq 0) "symlinked config $mode left a backup or temp file"
        }
        Remove-Item -LiteralPath $link -Force
        [void][IO.File]::CreateSymbolicLink($link, (Join-Path $linkDirectory 'missing.json'))
        $run = Invoke-Installer (@('-ConfigPath', $link) + $toolArgs)
        Assert ($run.Code -ne 0 -and !(Test-Path -LiteralPath (Join-Path $linkDirectory 'missing.json'))) "dangling symlinked config accepted: $($run.Output)"
    }

    # --- Windows default location: classic %APPDATA% vs MSIX %LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming ---
    if ($IsWindows) {
        $caseIndex = 0
        function New-WindowsRoots([string[]]$PackageNames, [switch]$ClassicConfig) {
            $script:caseIndex++
            $base = Join-Path $work "win roots $script:caseIndex"
            Set-FakeRoots $base
            $classic = Join-Path $env:APPDATA 'Claude/claude_desktop_config.json'
            if ($ClassicConfig) { [void][IO.Directory]::CreateDirectory((Split-Path $classic)); [IO.File]::WriteAllText($classic, '{}') }
            $packaged = @(foreach ($name in $PackageNames) { $dir = Join-Path $env:LOCALAPPDATA "Packages/$name/LocalCache/Roaming/Claude"; [void][IO.Directory]::CreateDirectory($dir); Join-Path $dir 'claude_desktop_config.json' })
            return [pscustomobject]@{ Classic = [IO.Path]::GetFullPath($classic); Packaged = @($packaged | ForEach-Object { [IO.Path]::GetFullPath($_) }) }
        }
        $roots = New-WindowsRoots @()
        $run = Invoke-Installer $toolArgs
        Assert ($run.Code -eq 0 -and (Test-Path -LiteralPath $roots.Classic) -and $run.Output -match [regex]::Escape("CONFIG $($roots.Classic)")) "classic default: $($run.Output)"
        $roots = New-WindowsRoots @('Claude_pzs8sxrjxfjjc')
        $run = Invoke-Installer $toolArgs
        Assert ($run.Code -eq 0 -and (Test-Path -LiteralPath $roots.Packaged[0]) -and !(Test-Path -LiteralPath $roots.Classic) -and $run.Output -match [regex]::Escape("CONFIG $($roots.Packaged[0])")) "MSIX default: $($run.Output)"
        $roots = New-WindowsRoots @('Claude_pzs8sxrjxfjjc') -ClassicConfig
        $run = Invoke-Installer $toolArgs
        Assert ($run.Code -ne 0 -and $run.Output.Contains("CANDIDATE $($roots.Classic)") -and $run.Output.Contains("CANDIDATE $($roots.Packaged[0])") -and !(Test-Path -LiteralPath $roots.Packaged[0]) -and [IO.File]::ReadAllText($roots.Classic) -eq '{}') "classic + MSIX must be refused listing both: $($run.Output)"
        $roots = New-WindowsRoots @('Claude_aaaa', 'Claude_bbbb')
        $run = Invoke-Installer $toolArgs
        Assert ($run.Code -ne 0 -and $run.Output.Contains("CANDIDATE $($roots.Packaged[0])") -and $run.Output.Contains("CANDIDATE $($roots.Packaged[1])") -and !(Test-Path -LiteralPath $roots.Packaged[0]) -and !(Test-Path -LiteralPath $roots.Packaged[1])) "two MSIX packages must be refused: $($run.Output)"
        Set-FakeRoots $fakeRoots
    }

    # --- Optional: the registered command/args launch the real server ---
    if ($Dotnet -and $McpDll) {
        $real = Join-Path $work 'Claude Config/real/claude_desktop_config.json'
        $run = Invoke-Installer @('-ConfigPath', $real, '-Dotnet', $Dotnet, '-McpDll', $McpDll)
        Assert ($run.Code -eq 0) "real install exit $($run.Code): $($run.Output)"
        $entry = Get-Prop (Get-Prop (Read-Node $real) 'mcpServers') 'bim-agentic-os'
        $start = [Diagnostics.ProcessStartInfo]::new([string]$entry['command'])
        foreach ($argument in $entry['args'].AsArray()) { $start.ArgumentList.Add([string]$argument) }
        $start.UseShellExecute = $false
        $start.RedirectStandardInput = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $process = [Diagnostics.Process]::Start($start)
        try {
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"install-mcp-test","version":"0"}}}')
            $process.StandardInput.Close()
            if (!$process.WaitForExit(60000)) { $process.Kill(); throw 'MCP server did not exit after stdin closed' }
            $response = $stdout.GetAwaiter().GetResult()
            $parsed = $response | ConvertFrom-Json
            Assert ($parsed.id -eq 1 -and $parsed.result.serverInfo.name -eq 'bim-agentic-os') "registered server initialize: $response $($stderr.GetAwaiter().GetResult())"
        } finally { $process.Dispose() }
    }
} finally {
    foreach ($variable in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($variable, $savedEnvironment[$variable], 'Process') }
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
if ($failures.Count) { throw ("install-mcp checks failed:`n" + ($failures -join "`n")) }
$scope = if ($Dotnet -and $McpDll) { ' and the registered command launched the real server (initialize answered)' } else { '' }
"PASS install-mcp on temp configs: fresh, preserve, replace, idempotent, backup bytes, refusals, null mcpServers, symlink refusal, Windows classic/MSIX default, dry run, uninstall, spaces$scope"
