#Requires -Version 7.2
# P10-04 release packaging checks (plain assertions, no Pester).
# Builds the release layout twice from clean, separate temp build trees and requires byte-identical manifests and
# files; checks layout, version stamp (single source scripts/package.version, git fallback), forbidden content
# (Revit/Autodesk assemblies, Revit models, credentials), never-overwrite and manifest tamper detection.
# Every failure is printed as a GitHub annotation because CI logs are not downloadable.
param([string]$Dotnet)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot)
$packager = Join-Path $root 'scripts/package.ps1'
$verifier = Join-Path $root 'scripts/package-verify.ps1'
$common = Join-Path $root 'scripts/package-common.ps1'
$versionFile = Join-Path $root 'scripts/package.version'
$pwsh = (Get-Process -Id $PID).Path
$failures = [Collections.Generic.List[string]]::new()
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { $failures.Add($Message) } }
function Complete {
    if ($failures.Count) {
        foreach ($failure in $failures) {
            $line = ($failure -replace '[\r\n]+', ' ')
            if ($line.Length -gt 900) { $line = $line.Substring(0, 900) }
            if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host ('::error title=packaging::' + ($line -replace '%', '%25')) }
            Write-Host "FAIL $line"
        }
        Write-Error "packaging checks failed: $($failures.Count) failure(s); first: $($failures[0])"
        exit 1
    }
    'PASS packaging: reproducible rebuild (byte-identical manifest and files), layout, version stamp, forbidden-content refusal, never-overwrite, tamper detection'
    exit 0
}
function Invoke-Script([string]$Script, [string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    $output = & $pwsh -NoProfile -File $Script @Arguments 2>&1 | ForEach-Object { "$_" }
    return [pscustomobject]@{ Code = $LASTEXITCODE; Output = ($output -join "`n") }
}
function Get-Tail([string]$Text) { $t = $Text -replace '[\r\n]+', ' | '; if ($t.Length -gt 600) { $t.Substring($t.Length - 600) } else { $t } }
function Get-Inventory([string]$Directory) {
    $base = [IO.Path]::GetFullPath($Directory)
    $rows = foreach ($file in Get-ChildItem -LiteralPath $base -Recurse -File -Force) {
        $relative = [IO.Path]::GetRelativePath($base, $file.FullName).Replace('\', '/')
        $relative + ' ' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return [string[]]@($rows | Sort-Object -Culture ([Globalization.CultureInfo]::InvariantCulture) -CaseSensitive)
}

foreach ($required in $packager, $verifier, $common, $versionFile) {
    Assert (Test-Path -LiteralPath $required -PathType Leaf) "packaging prerequisite missing: $([IO.Path]::GetRelativePath($root, $required))"
}
if ($failures.Count) { Complete }
if (!$Dotnet) { $Dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source }
if (!$Dotnet) { $failures.Add('dotnet SDK not found; pass -Dotnet'); Complete }

$work = Join-Path ([IO.Path]::GetTempPath()) ('bim package test ' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    # --- Version source: single file, strict format, git fallback ---
    . $common
    $expectedVersion = ([IO.File]::ReadAllText($versionFile)).Trim()
    Assert ($expectedVersion -match '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') "scripts/package.version is not MAJOR.MINOR.PATCH[-pre]: '$expectedVersion'"
    Assert ((Resolve-PackageVersion -VersionFile $versionFile -Root $root) -eq $expectedVersion) 'Resolve-PackageVersion must return the scripts/package.version content'
    $fallback = Resolve-PackageVersion -VersionFile (Join-Path $work 'missing.version') -Root $root
    Assert ($fallback -match '^0\.0\.0-g[0-9a-f]{12}$') "missing version file must fall back to 0.0.0-g<12-hex git sha>, got '$fallback'"
    foreach ($bad in @('1.0', 'v1.0.0', '1.0.0; rm -rf /', '', '1.0.0+meta')) {
        $badFile = Join-Path $work ('bad-' + [Guid]::NewGuid().ToString('N') + '.version')
        [IO.File]::WriteAllText($badFile, $bad)
        $threw = $false
        try { [void](Resolve-PackageVersion -VersionFile $badFile -Root $root) } catch { $threw = $true }
        Assert $threw "invalid version '$bad' must be refused"
    }

    # --- Forbidden content scanner refuses vendor assemblies, Revit models and credentials ---
    $dirty = Join-Path $work 'forbidden'
    foreach ($name in 'RevitAPI.dll', 'sub/RevitAPIUI.dll', 'AdWindows.dll', 'Nice3point.Revit.Api.RevitAPI.dll', 'model.rvt', 'family.rfa', 'secret.pfx', '.env', 'keys/id_rsa', 'cert.pem') {
        $path = Join-Path $dirty $name
        New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
        [IO.File]::WriteAllText($path, 'x')
    }
    $pemLike = Join-Path $dirty 'config.json'
    # Assembled at runtime so the repository's static credential scan does not flag this test itself.
    [IO.File]::WriteAllText($pemLike, '{"k":"' + ('-----BEGIN ' + 'PRIVATE KEY-----') + '"}')
    $clean = Join-Path $dirty 'ok.txt'
    [IO.File]::WriteAllText($clean, 'fine')
    $found = @(Find-PackageForbidden -Directory $dirty)
    foreach ($name in 'RevitAPI.dll', 'sub/RevitAPIUI.dll', 'AdWindows.dll', 'Nice3point.Revit.Api.RevitAPI.dll', 'model.rvt', 'family.rfa', 'secret.pfx', '.env', 'keys/id_rsa', 'cert.pem', 'config.json') {
        Assert ($found -contains $name) "forbidden scanner missed $name (found: $($found -join ', '))"
    }
    Assert ($found -notcontains 'ok.txt') 'forbidden scanner flagged a clean file'

    # --- Two independent clean builds ---
    $outA = Join-Path $work 'release A'
    $outB = Join-Path $work 'release B'
    $runA = Invoke-Script $packager @('-OutputDirectory', $outA, '-Dotnet', $Dotnet)
    Assert ($runA.Code -eq 0) "package build A exit $($runA.Code): $(Get-Tail $runA.Output)"
    $runB = Invoke-Script $packager @('-OutputDirectory', $outB, '-Dotnet', $Dotnet)
    Assert ($runB.Code -eq 0) "package build B exit $($runB.Code): $(Get-Tail $runB.Output)"
    if ($failures.Count) { Complete }

    $manifestA = Join-Path $outA 'manifest.json'
    $manifestB = Join-Path $outB 'manifest.json'
    Assert (Test-Path -LiteralPath $manifestA -PathType Leaf) 'manifest.json missing from package'
    if ($failures.Count) { Complete }
    $bytesA = [IO.File]::ReadAllBytes($manifestA); $bytesB = [IO.File]::ReadAllBytes($manifestB)
    Assert ([Convert]::ToBase64String($bytesA) -eq [Convert]::ToBase64String($bytesB)) 'rebuild is not reproducible: manifest.json bytes differ between two clean builds'
    $invA = Get-Inventory $outA; $invB = Get-Inventory $outB
    $diff = @(Compare-Object $invA $invB | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" })
    Assert ($diff.Count -eq 0) "rebuild is not reproducible; differing files: $($diff -join '; ')"
    Assert ($bytesA.Length -gt 0 -and $bytesA[0] -ne 0xEF) 'manifest.json must be non-empty UTF-8 without BOM'
    Assert (!([Text.Encoding]::UTF8.GetString($bytesA).Contains("`r"))) 'manifest.json must use LF line endings'

    # --- Manifest shape and agreement with the files on disk ---
    $manifest = Get-Content -LiteralPath $manifestA -Raw | ConvertFrom-Json
    Assert ($manifest.schema -eq 'bim-agentic-os/package-manifest/v1') "manifest schema: '$($manifest.schema)'"
    Assert ($manifest.version -eq $expectedVersion) "manifest version '$($manifest.version)' != scripts/package.version '$expectedVersion'"
    Assert ([string]$manifest.source_commit -match '^[0-9a-f]{40}$') "manifest source_commit must be a 40-hex git sha: '$($manifest.source_commit)'"
    $paths = [string[]]@($manifest.files | ForEach-Object { [string]$_.path })
    $sorted = [string[]]$paths.Clone(); [Array]::Sort($sorted, [StringComparer]::Ordinal)
    Assert (($paths -join "`n") -ceq ($sorted -join "`n")) 'manifest files must be sorted ordinally by path'
    Assert ($paths -notcontains 'manifest.json') 'manifest must not list itself'
    foreach ($entry in $manifest.files) {
        $file = Join-Path $outA ([string]$entry.path)
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { $failures.Add("manifest lists missing file $($entry.path)"); continue }
        Assert ((Get-Item -LiteralPath $file).Length -eq [long]$entry.size) "size mismatch for $($entry.path)"
        Assert ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ceq [string]$entry.sha256) "sha256 mismatch for $($entry.path)"
    }
    $onDisk = [string[]]@($invA | ForEach-Object { $_.Substring(0, $_.LastIndexOf(' ')) } | Where-Object { $_ -ne 'manifest.json' })
    [Array]::Sort($onDisk, [StringComparer]::Ordinal)
    Assert ((@($onDisk) -join "`n") -ceq ($paths -join "`n")) "manifest file list differs from package contents: disk=[$($onDisk -join ', ')] manifest=[$($paths -join ', ')]"

    # --- Layout: CLI, MCP server, add-in manifest; no apphost, no symbols, no Revit assemblies ---
    foreach ($required in 'VERSION', 'cli/Bim.Cli.dll', 'cli/Bim.Cli.runtimeconfig.json', 'cli/Bim.Cli.deps.json', 'cli/Bim.Core.dll', 'mcp/Bim.Mcp.dll', 'mcp/Bim.Mcp.runtimeconfig.json', 'revit/Bim.addin.template') {
        Assert ($paths -contains $required) "package layout missing $required"
    }
    Assert (([IO.File]::ReadAllText((Join-Path $outA 'VERSION'))) -ceq ($expectedVersion + "`n")) 'VERSION file must contain the version and one LF'
    foreach ($path in $paths) {
        Assert ($path -notmatch '(?i)\.pdb$') "symbols must not be packaged: $path"
        Assert ($path -notmatch '(?i)(^|/)(Bim\.Cli|Bim\.Mcp)(\.exe)?$') "platform apphost must not be packaged: $path"
        Assert ($path -notmatch '(?i)(^|/)(Revit[^/]*\.dll|AdWindows\.dll|UIFramework[^/]*\.dll|Nice3point[^/]*)$') "Revit/Autodesk assembly packaged: $path"
    }
    Assert (@(Find-PackageForbidden -Directory $outA).Count -eq 0) "package contains forbidden content: $(@(Find-PackageForbidden -Directory $outA) -join ', ')"
    $addin = [IO.File]::ReadAllText((Join-Path $outA 'revit/Bim.addin.template'))
    Assert ($addin.Contains('<FullClassName>Bim.Revit.App</FullClassName>') -and !$addin.Contains("`r")) 'add-in manifest template must be the repository template with LF line endings'
    $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $outA 'cli/Bim.Cli.dll')).ProductVersion
    Assert ([string]$productVersion -like "$expectedVersion*") "cli/Bim.Cli.dll product version '$productVersion' is not stamped with '$expectedVersion'"
    $mcpProduct = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $outA 'mcp/Bim.Mcp.dll')).ProductVersion
    Assert ([string]$mcpProduct -like "$expectedVersion*") "mcp/Bim.Mcp.dll product version '$mcpProduct' is not stamped with '$expectedVersion'"

    # --- The packaged CLI runs from the release layout ---
    $ErrorActionPreference = 'Continue'
    $cliOut = & $Dotnet (Join-Path $outA 'cli/Bim.Cli.dll') '--help' 2>&1 | ForEach-Object { "$_" }
    $cliCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    # An unknown command makes the CLI answer with its JSON failure envelope, which proves the runtime loaded.
    Assert ($cliCode -eq 1 -and (($cliOut -join "`n") -match '"status":"failed"')) "packaged CLI did not start from the release layout (exit $cliCode): $(Get-Tail ($cliOut -join "`n"))"

    # --- Verifier: clean package passes; tamper, missing, extra and forbidden files are detected ---
    $ok = Invoke-Script $verifier @('-PackageDirectory', $outA)
    Assert ($ok.Code -eq 0) "package-verify rejected a clean package: $(Get-Tail $ok.Output)"
    $cases = @(
        @{ Name = 'tamper'; Expect = 'sha256 mismatch'; Act = { param($d) $f = Join-Path $d 'cli/Bim.Cli.runtimeconfig.json'; $b = [IO.File]::ReadAllBytes($f); $b[0] = $b[0] -bxor 1; [IO.File]::WriteAllBytes($f, $b) } },
        @{ Name = 'missing'; Expect = 'missing file'; Act = { param($d) Remove-Item -LiteralPath (Join-Path $d 'mcp/Bim.Mcp.dll') } },
        @{ Name = 'extra'; Expect = 'unlisted file'; Act = { param($d) [IO.File]::WriteAllText((Join-Path $d 'cli/extra.txt'), 'x') } },
        @{ Name = 'forbidden'; Expect = 'forbidden'; Act = { param($d) [IO.File]::WriteAllText((Join-Path $d 'revit/RevitAPI.dll'), 'x') } },
        @{ Name = 'version'; Expect = 'VERSION'; Act = { param($d) [IO.File]::WriteAllText((Join-Path $d 'VERSION'), "9.9.9`n") } }
    )
    foreach ($case in $cases) {
        $copy = Join-Path $work ('case ' + $case.Name)
        Copy-Item -LiteralPath $outA -Destination $copy -Recurse
        & $case.Act $copy
        $run = Invoke-Script $verifier @('-PackageDirectory', $copy)
        Assert ($run.Code -ne 0) "package-verify accepted a $($case.Name) package"
        Assert ($run.Output -match [regex]::Escape($case.Expect)) "package-verify $($case.Name) must report '$($case.Expect)': $(Get-Tail $run.Output)"
    }

    # --- Never overwrite: an existing output (non-empty or empty) is refused before any build and left intact ---
    $before = [Convert]::ToBase64String([IO.File]::ReadAllBytes($manifestA))
    $again = Invoke-Script $packager @('-OutputDirectory', $outA, '-Dotnet', $Dotnet)
    Assert ($again.Code -ne 0) 'package.ps1 overwrote an existing release directory'
    Assert ($again.Output -match 'exists') "overwrite refusal must say the output exists: $(Get-Tail $again.Output)"
    Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($manifestA)) -eq $before) 'refused overwrite changed the existing manifest'
    $empty = Join-Path $work 'empty existing'
    New-Item -ItemType Directory -Force $empty | Out-Null
    $intoEmpty = Invoke-Script $packager @('-OutputDirectory', $empty, '-Dotnet', $Dotnet)
    Assert ($intoEmpty.Code -ne 0) 'package.ps1 must refuse an existing empty directory too'
    Assert (@(Get-ChildItem -LiteralPath $empty -Force).Count -eq 0) 'refused package left files in an existing directory'
    Assert (@(Get-ChildItem -LiteralPath $work -Force -Directory | Where-Object { $_.Name -like '*.partial-*' }).Count -eq 0) 'package.ps1 left a partial staging directory'
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
Complete
