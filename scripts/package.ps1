#Requires -Version 7.2
# P10-04 release packaging. Produces a portable, framework-dependent release layout:
#   VERSION                      version stamp (single source: scripts/package.version)
#   cli/                         dotnet publish of interfaces/cli (run with: dotnet cli/Bim.Cli.dll)
#   mcp/                         dotnet publish of interfaces/mcp (run with: dotnet mcp/Bim.Mcp.dll)
#   revit/Bim.addin.template     add-in manifest template only; no Bim.Revit.dll and never any Revit assembly
#   manifest.json                relative path, size and SHA-256 of every other file, sorted ordinally
# Reproducible: tracked sources are copied into a fresh temp tree (no stale bin/obj), compiled with
# Deterministic=true, PathMap of that tree to /_/, no symbols, no platform apphost and an explicit SourceRevisionId.
# Never overwrites: an existing output directory (even empty) is refused; output appears atomically by rename.
param([Parameter(Mandatory)][string]$OutputDirectory, [string]$Dotnet, [string]$VersionFile)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/package-common.ps1"
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot))
if (!$VersionFile) { $VersionFile = Join-Path $PSScriptRoot 'package.version' }
function Stop-Package([string]$Message, [int]$Code = 1) { [Console]::Error.WriteLine("package refused: $Message"); exit $Code }

$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if (Test-Path -LiteralPath $output) { Stop-Package "output '$output' already exists; packaging never overwrites" 2 }
if (!$Dotnet) { $Dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source }
if (!$Dotnet) { Stop-Package 'dotnet SDK not found; pass -Dotnet' }
$version = Resolve-PackageVersion -VersionFile $VersionFile -Root $root
$commit = Get-PackageCommit $root
if (!$commit) { Stop-Package 'source must be a git checkout so the package can record its source commit' }

$projects = [ordered]@{ cli = 'interfaces/cli/Bim.Cli.csproj'; mcp = 'interfaces/mcp/Bim.Mcp.csproj' }
$sourceDirectories = @('core/Bim.Core', 'evaluation/Bim.Evaluation', 'adapters/ifc/Bim.Ifc.Compiler', 'interfaces/cli', 'interfaces/mcp')
$rootFiles = @('Directory.Build.props', 'global.json', '.editorconfig')
$addinTemplate = 'runtimes/revit-native/Bim.addin.template'

$ErrorActionPreference = 'Continue'
$tracked = @(& git -C $root ls-files -- @($sourceDirectories + $rootFiles))
$trackedCode = $LASTEXITCODE
$dirtyLines = @(& git -C $root status --porcelain --untracked-files=no -- @($sourceDirectories + $rootFiles + $addinTemplate))
$ErrorActionPreference = 'Stop'
if ($trackedCode -ne 0 -or $tracked.Count -eq 0) { Stop-Package 'git ls-files failed for the packaged sources' }
$dirty = $dirtyLines.Count -gt 0

$id = [Guid]::NewGuid().ToString('N')
$parent = Split-Path $output
if ($parent) { [void][IO.Directory]::CreateDirectory($parent) }
$staging = "$output.partial-$id"
$build = Join-Path ([IO.Path]::GetTempPath()) "bim-package-src-$id"
try {
    foreach ($relative in $tracked) {
        $target = Join-Path $build $relative
        [void][IO.Directory]::CreateDirectory((Split-Path $target))
        Copy-Item -LiteralPath (Join-Path $root $relative) -Destination $target
    }
    [void][IO.Directory]::CreateDirectory($staging)
    foreach ($name in $projects.Keys) {
        $arguments = @('publish', (Join-Path $build $projects[$name]), '--configuration', 'Release', '--output', (Join-Path $staging $name), '--nologo',
            "-p:Version=$version", "-p:SourceRevisionId=$commit", '-p:Deterministic=true', "-p:PathMap=$build=/_/",
            '-p:UseAppHost=false', '-p:DebugType=none', '-p:DebugSymbols=false', '-p:GenerateDocumentationFile=false',
            '-p:EnableSourceLink=false', '-p:EnableSourceControlManagerQueries=false', '-p:SatelliteResourceLanguages=en')
        & $Dotnet @arguments | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish $($projects[$name]) failed with exit code $LASTEXITCODE" }
    }
    $revit = Join-Path $staging 'revit'
    [void][IO.Directory]::CreateDirectory($revit)
    $template = ([IO.File]::ReadAllText((Join-Path $root $addinTemplate))) -replace "`r`n", "`n"
    Write-PackageText (Join-Path $revit 'Bim.addin.template') $template
    Write-PackageText (Join-Path $staging 'VERSION') ($version + "`n")

    $forbidden = @(Find-PackageForbidden $staging)
    if ($forbidden.Count) { throw "forbidden content would be packaged: $($forbidden -join ', ')" }
    Write-PackageText (Join-Path $staging 'manifest.json') (New-PackageManifestText $staging $version $commit $dirty)
    $problems = @(Test-PackageManifest $staging)
    if ($problems.Count) { throw "staged package failed verification: $($problems -join '; ')" }
    if (Test-Path -LiteralPath $output) { throw "output '$output' appeared during packaging; refusing to overwrite" }
    [IO.Directory]::Move($staging, $output)
} catch {
    [Console]::Error.WriteLine("package failed: $($_.Exception.Message)")
    exit 1
} finally {
    foreach ($path in $staging, $build) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue } }
}
"PACKAGED $version ($commit$(if ($dirty) { ', dirty' })) -> $output"
