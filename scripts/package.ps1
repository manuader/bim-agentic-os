#Requires -Version 7.2
# P10-04 release packaging. Produces a portable, framework-dependent release layout:
#   VERSION                      version stamp (single source: scripts/package.version)
#   cli/                         dotnet publish of interfaces/cli (run with: dotnet cli/Bim.Cli.dll)
#   mcp/                         dotnet publish of interfaces/mcp (run with: dotnet mcp/Bim.Mcp.dll)
#   revit/Bim.addin.template     add-in manifest template only; no Bim.Revit.dll and never any Revit assembly
#   manifest.json                relative path, size and SHA-256 of every other file, sorted ordinally
# Reproducible: tracked sources are copied into a fresh temp tree (no stale bin/obj), compiled with
# Deterministic=true, PathMap of that tree to /_/, no symbols, no platform apphost and an explicit SourceRevisionId.
# Hermetic: the temp parent is not trusted, so Directory.Build.targets, Directory.Packages.props and Directory.Build.rsp
# above the tree are not imported and NuGet uses only a config written into the tree (the tree carries its own
# Directory.Build.props, global.json and root .editorconfig, which stop those upward searches).
# Existing output (even an empty directory) is refused before building; the staged layout is then moved into place by
# one rename after re-checking the path, so only a directory created in that last instant could collide.
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
$trackedRaw = (& git -C $root ls-files -z -- @($sourceDirectories + $rootFiles)) -join "`n"
$trackedCode = $LASTEXITCODE
$dirtyLines = @(& git -C $root status --porcelain --untracked-files=no -- @($sourceDirectories + $rootFiles + $addinTemplate + 'scripts/package.version'))
$ErrorActionPreference = 'Stop'
$tracked = @($trackedRaw.Split([char]0) | Where-Object { $_ })
if ($trackedCode -ne 0 -or $tracked.Count -eq 0) { Stop-Package 'git ls-files failed for the packaged sources' }
foreach ($required in $rootFiles) { if ($tracked -cnotcontains $required) { Stop-Package "$required must be tracked; it bounds the build tree against inherited MSBuild files" } }
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
    $nugetConfig = Join-Path $build 'NuGet.Config'
    Write-PackageText $nugetConfig "<?xml version=`"1.0`" encoding=`"utf-8`"?>`n<configuration>`n  <packageSources>`n    <clear />`n    <add key=`"nuget.org`" value=`"https://api.nuget.org/v3/index.json`" />`n  </packageSources>`n</configuration>`n"
    [void][IO.Directory]::CreateDirectory($staging)
    foreach ($name in $projects.Keys) {
        $arguments = @('publish', (Join-Path $build $projects[$name]), '--configuration', 'Release', '--output', (Join-Path $staging $name), '--nologo', '-noAutoResponse',
            '-p:ImportDirectoryBuildTargets=false', '-p:ImportDirectoryPackagesProps=false', "-p:RestoreConfigFile=$nugetConfig",
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
