<#
.SYNOPSIS
Registers (or removes) the BIM Agentic OS MCP stdio server in the Claude Desktop config.

.DESCRIPTION
Adds or replaces only mcpServers.<ServerName> = { command: <absolute dotnet>, args: [<absolute Bim.Mcp.dll>] }.
Every other key and server is preserved. Invalid JSON, duplicate keys, a non-object top level or a non-object
mcpServers is refused and nothing is written. Before any change to an existing file a byte-exact backup
<config>.bak-<UTC stamp> is written next to it; the new content goes to a temp file in the same directory and is
moved over the config. A run that would not change the entry is a no-op: no write and no backup. -DryRun prints the
planned file and writes nothing. -Uninstall removes only that entry (a missing config or entry is a no-op).
A symlinked config is refused. On Windows without -ConfigPath, a single MSIX install folder
(%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude) is preferred over %APPDATA%\Claude; when both hold
candidates the script refuses and lists them. See docs/mcp-setup.md.
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$ServerName = 'bim-agentic-os',
    [string]$Dotnet,
    [string]$McpDll,
    [switch]$Build,
    [switch]$DryRun,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Text.Json
Add-Type -AssemblyName System.Text.Encodings.Web
$root = Split-Path $PSScriptRoot

function Resolve-FullPath([string]$Path) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path) }

function Get-DefaultConfigPath {
    if ($IsWindows) {
        if (!$env:APPDATA) { throw 'APPDATA is not set; pass -ConfigPath explicitly.' }
        $classic = [IO.Path]::GetFullPath((Join-Path (Join-Path $env:APPDATA 'Claude') 'claude_desktop_config.json'))
        # The MSIX (Store/packaged) build virtualizes %APPDATA% under its package folder.
        $packaged = @()
        if ($env:LOCALAPPDATA -and (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Packages') -PathType Container)) {
            $packaged = @(Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue |
                ForEach-Object { Join-Path $_.FullName 'LocalCache/Roaming/Claude' } |
                Where-Object { Test-Path -LiteralPath $_ -PathType Container } |
                ForEach-Object { [IO.Path]::GetFullPath((Join-Path $_ 'claude_desktop_config.json')) } | Sort-Object)
        }
        if ($packaged.Count -eq 0) { $script:ConfigReason = 'classic install location'; return $classic }
        $candidates = @($packaged)
        if (Test-Path -LiteralPath $classic) { $candidates = @($classic) + $candidates }
        if ($candidates.Count -gt 1) { throw ("Several Claude Desktop config locations exist; pass -ConfigPath with the one your Claude Desktop uses (Settings > Developer > Edit Config shows it). Candidates:`n  " + ($candidates -join "`n  ")) }
        $script:ConfigReason = 'MSIX install location'
        return $packaged[0]
    }
    if ($IsMacOS) { $script:ConfigReason = 'macOS default location'; return Join-Path $HOME 'Library/Application Support/Claude/claude_desktop_config.json' }
    throw 'Claude Desktop has no default config location on this OS; pass -ConfigPath explicitly.'
}

function Assert-NoDuplicateKeys([System.Text.Json.JsonElement]$Element, [string]$Where) {
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (!$seen.Add($property.Name)) { throw "duplicate key '$($property.Name)' in $Where" }
            Assert-NoDuplicateKeys $property.Value "$Where.$($property.Name)"
        }
    } elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        $index = 0
        foreach ($item in $Element.EnumerateArray()) { Assert-NoDuplicateKeys $item "$Where[$index]"; $index++ }
    }
}

# Leading commas stop PowerShell from enumerating a returned JsonObject into key/value pairs.
function Read-Config([string]$Path) {
    $text = [IO.File]::ReadAllText($Path)
    try {
        $document = [System.Text.Json.JsonDocument]::Parse($text)
        try { Assert-NoDuplicateKeys $document.RootElement '$' } finally { $document.Dispose() }
        $node = [System.Text.Json.Nodes.JsonNode]::Parse($text)
    } catch {
        throw "Refusing to change ${Path}: not valid JSON ($($_.Exception.Message)). Nothing was written."
    }
    if ($node -isnot [System.Text.Json.Nodes.JsonObject]) { throw "Refusing to change ${Path}: the top level is not a JSON object. Nothing was written." }
    return , $node
}

function Get-Servers($Config, [string]$Path) {
    $servers = $null
    if (!$Config.TryGetPropertyValue('mcpServers', [ref]$servers) -or $null -eq $servers) { return $null }
    if ($servers -isnot [System.Text.Json.Nodes.JsonObject]) { throw "Refusing to change ${Path}: mcpServers is not a JSON object. Nothing was written." }
    return , $servers
}

if ([string]::IsNullOrWhiteSpace($ServerName)) { throw '-ServerName must not be empty.' }
$ConfigReason = 'explicit -ConfigPath'
if (!$ConfigPath) { $ConfigPath = Get-DefaultConfigPath }
$ConfigPath = Resolve-FullPath $ConfigPath
"CONFIG $ConfigPath ($ConfigReason)"
$linkInfo = [IO.FileInfo]::new($ConfigPath)
if ($linkInfo.LinkTarget) { throw "Refusing to change ${ConfigPath}: it is a symbolic link (to $($linkInfo.LinkTarget)). Pass -ConfigPath with the real file. Nothing was written." }
if ((Test-Path -LiteralPath $ConfigPath) -and !(Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { throw "$ConfigPath exists but is not a file." }

$entry = $null
if (!$Uninstall) {
    if (!$Dotnet) {
        $local = Join-Path $root '.tools/dotnet/dotnet.exe'
        if ($IsWindows -and (Test-Path -LiteralPath $local -PathType Leaf)) { $Dotnet = $local }
        else {
            $command = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if (!$command) { throw 'dotnet not found on PATH. Install the .NET 8 SDK or pass -Dotnet <absolute path to dotnet>.' }
            $Dotnet = $command.Source
        }
    }
    $Dotnet = Resolve-FullPath $Dotnet
    if (!(Test-Path -LiteralPath $Dotnet -PathType Leaf)) { throw "dotnet not found: $Dotnet" }
    if (!$McpDll) { $McpDll = Join-Path $root 'interfaces/mcp/bin/Release/net8.0/Bim.Mcp.dll' }
    $McpDll = Resolve-FullPath $McpDll
    if ($Build) {
        if ($DryRun) { "DRY RUN would build $root/interfaces/mcp (Release)" }
        else {
            & $Dotnet build (Join-Path $root 'interfaces/mcp') --configuration Release --warnaserror | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "MCP server build failed with exit code $LASTEXITCODE. Nothing was written." }
        }
    }
    if (!(Test-Path -LiteralPath $McpDll -PathType Leaf)) {
        if ($DryRun -and $Build) { "DRY RUN note: $McpDll does not exist yet; -Build would create it" }
        else { throw "MCP server not found: $McpDll. Build it with -Build (or dotnet build interfaces/mcp --configuration Release), or pass -McpDll. Nothing was written." }
    }
    $entry = [System.Text.Json.Nodes.JsonNode]::Parse((ConvertTo-Json -Compress -InputObject ([ordered]@{ command = $Dotnet; args = @($McpDll) })))
}

$exists = Test-Path -LiteralPath $ConfigPath -PathType Leaf
$config = $null
if ($exists) { $config = Read-Config $ConfigPath }

if ($Uninstall) {
    if (!$exists) { "UNCHANGED $ConfigPath does not exist; nothing to uninstall."; exit 0 }
    $servers = Get-Servers $config $ConfigPath
    if ($null -eq $servers -or !$servers.ContainsKey($ServerName)) { "UNCHANGED $ConfigPath has no mcpServers.$ServerName entry; nothing to uninstall."; exit 0 }
    [void]$servers.Remove($ServerName)
    $action = "removed mcpServers.$ServerName"
} else {
    if (!$exists) { $config = [System.Text.Json.Nodes.JsonObject]::new() }
    $servers = Get-Servers $config $ConfigPath
    if ($null -eq $servers) {
        # Missing or null mcpServers: the indexer adds the key, or replaces the null in place.
        $servers = [System.Text.Json.Nodes.JsonObject]::new()
        $config['mcpServers'] = $servers
    }
    $current = $null
    if ($servers.TryGetPropertyValue($ServerName, [ref]$current) -and $null -ne $current -and $current.ToJsonString() -eq $entry.ToJsonString()) {
        "UNCHANGED $ConfigPath already registers mcpServers.$ServerName = $($entry.ToJsonString())"
        exit 0
    }
    # The indexer replaces an existing entry in place, so server order is kept.
    $servers[$ServerName] = $entry
    $action = "registered mcpServers.$ServerName = $($entry.ToJsonString())"
}

$options = [System.Text.Json.JsonSerializerOptions]::new()
$options.WriteIndented = $true
$options.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
$text = $config.ToJsonString($options) + [Environment]::NewLine

if ($DryRun) {
    "DRY RUN would write $ConfigPath ($action); nothing was written. Planned content:"
    $text
    exit 0
}

if (@(Get-Process -Name 'Claude' -ErrorAction SilentlyContinue).Count) { Write-Warning 'Claude Desktop appears to be running; quit it before restarting so it does not overwrite this change.' }
[void][IO.Directory]::CreateDirectory((Split-Path -Parent $ConfigPath))
if ($exists) {
    $backup = $ConfigPath + '.bak-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
    [IO.File]::Copy($ConfigPath, $backup, $false)
    "BACKUP $backup"
}
$temp = $ConfigPath + '.tmp-' + [Guid]::NewGuid().ToString('N')
try {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
    $stream = [IO.FileStream]::new($temp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    [IO.File]::Move($temp, $ConfigPath, $true)
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
}
"CHANGED $ConfigPath ($action). Restart Claude Desktop to load it."
