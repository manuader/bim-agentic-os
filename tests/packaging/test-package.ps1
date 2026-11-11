#Requires -Version 7.2
# P10-04 release packaging checks (plain assertions, no Pester).
# Builds the release layout twice from clean, separate temp build trees and requires byte-identical manifests and
# files; checks layout, version stamp (single source scripts/package.version, git fallback), forbidden content
# (Revit/Autodesk assemblies, Revit models, credentials), never-overwrite and manifest tamper detection.
# P10-07: runs tests/packaging/test-install-addin.ps1 against the built package (temp Addins folders only).
# P10-08: runs tests/packaging/test-package-archive.ps1 against both clean builds (archives must be byte-identical).
# P10-13: sbom.json (shipped assemblies with SHA-256, NuGet references of the shipped csproj closure, build-only
# references such as Nice3point never listed) is listed in manifest.json; package-verify refuses a missing/edited sbom.
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
    'PASS packaging: reproducible rebuild (byte-identical manifest and files), layout, version stamp, forbidden-content refusal, never-overwrite, tamper detection, add-in install/uninstall, release archive, SBOM'
    exit 0
}
function Invoke-Script([string]$Script, [string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    $output = & $pwsh -NoProfile -File $Script @Arguments 2>&1 | ForEach-Object { "$_" }
    return [pscustomobject]@{ Code = $LASTEXITCODE; Output = ($output -join "`n") }
}
# Runs a script with TEMP/TMP/TMPDIR pointing at $TempDirectory (the child inherits them), then restores them.
function Invoke-ScriptWithTemp([string]$TempDirectory, [string]$Script, [string[]]$Arguments) {
    $saved = @{}
    foreach ($name in 'TEMP', 'TMP', 'TMPDIR') { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process'); [Environment]::SetEnvironmentVariable($name, $TempDirectory, 'Process') }
    try { return Invoke-Script $Script $Arguments }
    finally { foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') } }
}
function Get-BuildTrees([string]$Directory) { @(Get-ChildItem -LiteralPath $Directory -Force -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'bim-package-src-*' }) }
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

    # --- P10-13 SBOM: NuGet references from the shipped project closure; build-only references never shipped ---
    try {
        $sbomSrc = Join-Path $work 'sbom src'
        $sbomFiles = [ordered]@{
            'Directory.Build.props' = '<Project><ItemGroup><PackageReference Include="Root.Analyzer" Version="1.0.0" PrivateAssets="all" IncludeAssets="analyzers;build" /></ItemGroup></Project>'
            'app/App.csproj' = '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../lib/Lib.csproj" /><PackageReference Include="Zeta.Lib" Version="[2.0.0]" /><PackageReference Include="Alpha.Json"><Version>1.2.3</Version></PackageReference><PackageReference Include="Nice3point.Revit.Toolkit" Version="2025.0.0" /></ItemGroup></Project>'
            'lib/Lib.csproj' = '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Alpha.Json" Version="1.2.3" /><PackageReference Include="Nice3point.Revit.Api.RevitAPI" Version="[2025.0.2]" ExcludeAssets="runtime" PrivateAssets="all" /><PackageReference Include="Build.Only" Version="3.0.0"><ExcludeAssets>Runtime</ExcludeAssets></PackageReference></ItemGroup></Project>'
            'other/Other.csproj' = '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Not.Shipped" Version="9.9.9" /></ItemGroup></Project>'
            'nuget/alpha.json/1.2.3/alpha.json.nuspec' = '<?xml version="1.0"?><package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>Alpha.Json</id><version>1.2.3</version><license type="expression">MIT</license></metadata></package>'
        }
        foreach ($name in $sbomFiles.Keys) {
            $path = Join-Path $sbomSrc $name
            New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
            [IO.File]::WriteAllText($path, $sbomFiles[$name])
        }
        $refs = @(Get-PackageNuGetReferences -Root $sbomSrc -Projects @('app/App.csproj') -PackagesRoot (Join-Path $sbomSrc 'nuget'))
        $refText = ($refs | ForEach-Object { "$($_.Name)@$($_.Version) $($_.License) [$(@($_.Projects) -join ',')]" }) -join '; '
        Assert ($refText -ceq 'Alpha.Json@1.2.3 MIT [app/App.csproj,lib/Lib.csproj]; Zeta.Lib@2.0.0 NOASSERTION [app/App.csproj]') "SBOM NuGet references must be the runtime references of the shipped project closure, sorted, licence from the nuspec: got '$refText'"
        foreach ($excluded in 'Nice3point', 'Build.Only', 'Root.Analyzer', 'Not.Shipped') {
            Assert (!($refText -match [regex]::Escape($excluded))) "SBOM must not list build-only or unshipped reference $excluded as shipped: '$refText'"
        }
        foreach ($bad in @('<PackageReference Include="Floating" Version="1.*" />', '<PackageReference Include="Ranged" Version="[1.0,2.0)" />', '<PackageReference Include="Unversioned" />', '<PackageReference Include="Prop" Version="$(PkgVersion)" />')) {
            $badSrc = Join-Path $work ('sbom bad ' + [Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Force $badSrc | Out-Null
            [IO.File]::WriteAllText((Join-Path $badSrc 'App.csproj'), "<Project Sdk=`"Microsoft.NET.Sdk`"><ItemGroup>$bad</ItemGroup></Project>")
            $threw = $false
            try { [void](Get-PackageNuGetReferences -Root $badSrc -Projects @('App.csproj') -PackagesRoot (Join-Path $badSrc 'nuget')) } catch { $threw = $true }
            Assert $threw "an unpinned or unversioned shipped reference must be refused: $bad"
        }
        $repoRefs = @(Get-PackageNuGetReferences -Root $root -Projects @('interfaces/cli/Bim.Cli.csproj', 'interfaces/mcp/Bim.Mcp.csproj', 'runtimes/revit-native/Bim.Revit.csproj') -PackagesRoot (Join-Path $work 'no packages'))
        Assert (@($repoRefs | Where-Object { $_.Name -match '(?i)^(Nice3point|Revit|Autodesk)' }).Count -eq 0) "repository Nice3point build-only references listed as shipped: $(@($repoRefs | ForEach-Object Name) -join ', ')"

        $sbomPkg = Join-Path $work 'sbom pkg'
        New-Item -ItemType Directory -Force (Join-Path $sbomPkg 'cli') | Out-Null
        [IO.File]::WriteAllText((Join-Path $sbomPkg 'cli/Alpha.Json.dll'), 'not a real assembly')
        [IO.File]::WriteAllText((Join-Path $sbomPkg 'cli/readme.txt'), 'not an assembly')
        $textA = New-PackageSbomText -Directory $sbomPkg -Version '1.2.3' -Commit ('a' * 40) -References $refs
        $textB = New-PackageSbomText -Directory $sbomPkg -Version '1.2.3' -Commit ('a' * 40) -References $refs
        Assert ($textA -ceq $textB -and $textA.EndsWith("`n") -and !$textA.Contains("`r")) 'SBOM text must be deterministic, LF-only and end with one LF'
        $sbomUnit = $textA | ConvertFrom-Json
        Assert ($sbomUnit.schema -ceq 'bim-agentic-os/package-sbom/v1' -and $sbomUnit.version -ceq '1.2.3' -and $sbomUnit.source_commit -ceq ('a' * 40)) "SBOM header wrong: $textA"
        $asm = @($sbomUnit.assemblies)
        $expectedHash = (Get-FileHash -LiteralPath (Join-Path $sbomPkg 'cli/Alpha.Json.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
        Assert ($asm.Count -eq 1 -and $asm[0].path -ceq 'cli/Alpha.Json.dll' -and $asm[0].name -ceq 'Alpha.Json' -and $asm[0].sha256 -ceq $expectedHash -and $asm[0].license -ceq 'MIT' -and $asm[0].version -ceq 'NOASSERTION') "SBOM assemblies must list every shipped .dll with name, version, licence and sha256 (licence of its NuGet reference): $textA"
        Assert (@($sbomUnit.nuget_references).Count -eq 2) "SBOM must carry the NuGet references: $textA"
    } catch { $failures.Add("SBOM unit checks threw: $($_.Exception.Message)") }

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
    foreach ($required in 'VERSION', 'cli/Bim.Cli.dll', 'cli/Bim.Cli.runtimeconfig.json', 'cli/Bim.Cli.deps.json', 'cli/Bim.Core.dll', 'cli/Bim.Gltf.dll', 'cli/Bim.Cad.dll', 'mcp/Bim.Mcp.dll', 'mcp/Bim.Mcp.runtimeconfig.json', 'revit/Bim.addin.template') {
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
    $headCommit = [string](& git -C $root rev-parse HEAD)
    Assert ([string]$manifest.source_commit -ceq $headCommit) "manifest source_commit '$($manifest.source_commit)' != HEAD '$headCommit'"
    foreach ($dll in 'cli/Bim.Cli.dll', 'mcp/Bim.Mcp.dll') {
        $productVersion = [string][Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $outA $dll)).ProductVersion
        Assert ($productVersion -ceq "$expectedVersion+$headCommit") "$dll product version '$productVersion' != '$expectedVersion+$headCommit'"
    }

    # --- P10-13 SBOM in the real package: listed in the manifest, every shipped assembly hashed, no build-only refs ---
    Assert ($paths -ccontains 'sbom.json') 'package must contain sbom.json listed in manifest.json'
    if ($paths -ccontains 'sbom.json') {
        try {
            $sbomBytes = [IO.File]::ReadAllBytes((Join-Path $outA 'sbom.json'))
            Assert ($sbomBytes.Length -gt 0 -and $sbomBytes[0] -ne 0xEF -and !([Text.Encoding]::UTF8.GetString($sbomBytes).Contains("`r"))) 'sbom.json must be UTF-8 without BOM and LF-only'
            $sbom = [Text.Encoding]::UTF8.GetString($sbomBytes) | ConvertFrom-Json
            Assert ($sbom.schema -ceq 'bim-agentic-os/package-sbom/v1') "sbom schema: '$($sbom.schema)'"
            Assert ($sbom.version -ceq $expectedVersion -and $sbom.source_commit -ceq $manifest.source_commit) "sbom version/source_commit must match the manifest: '$($sbom.version)' '$($sbom.source_commit)'"
            $shippedAssemblies = [string[]]@($paths | Where-Object { $_ -match '(?i)\.(dll|exe)$' })
            $sbomAssemblies = [string[]]@(@($sbom.assemblies) | ForEach-Object { [string]$_.path })
            Assert ($shippedAssemblies.Count -gt 0 -and ($sbomAssemblies -join "`n") -ceq ($shippedAssemblies -join "`n")) "sbom assemblies must be exactly the shipped assemblies in ordinal order: sbom=[$($sbomAssemblies -join ', ')] package=[$($shippedAssemblies -join ', ')]"
            $byPath = @{}; foreach ($entry in $manifest.files) { $byPath[[string]$entry.path] = [string]$entry.sha256 }
            foreach ($component in @($sbom.assemblies)) {
                Assert ([string]$component.sha256 -ceq $byPath[[string]$component.path]) "sbom sha256 for $($component.path) differs from the manifest"
                Assert ([string]$component.license -cmatch '\A[A-Za-z0-9.+()-]+( (AND|OR|WITH) [A-Za-z0-9.+()-]+)*\z') "sbom licence for $($component.path) is not an SPDX id or NOASSERTION: '$($component.license)'"
                Assert ([string]$component.name -cnotmatch '(?i)^(Nice3point|Revit|Autodesk|AdWindows|UIFramework)') "vendor/build-only assembly listed as shipped: $($component.path)"
            }
            $cliComponent = @(@($sbom.assemblies) | Where-Object { $_.path -ceq 'cli/Bim.Cli.dll' })
            Assert ($cliComponent.Count -eq 1 -and $cliComponent[0].name -ceq 'Bim.Cli' -and $cliComponent[0].version -ceq $expectedVersion) "sbom must describe cli/Bim.Cli.dll as Bim.Cli $expectedVersion"
            $expectedRefs = @(Get-PackageNuGetReferences -Root $root -Projects @('interfaces/cli/Bim.Cli.csproj', 'interfaces/mcp/Bim.Mcp.csproj') -PackagesRoot (Join-Path $work 'no packages'))
            $sbomRefs = @($sbom.nuget_references)
            Assert ((($sbomRefs | ForEach-Object { "$($_.name)@$($_.version)" }) -join ',') -ceq (($expectedRefs | ForEach-Object { "$($_.Name)@$($_.Version)" }) -join ',')) "sbom nuget_references must be the shipped csproj references: sbom=[$(($sbomRefs | ForEach-Object name) -join ', ')]"
            Assert (@($sbomRefs | Where-Object { $_.name -match '(?i)^(Nice3point|Revit|Autodesk)' }).Count -eq 0) 'sbom lists a Nice3point/Revit reference as shipped'
        } catch { $failures.Add("sbom.json checks threw: $($_.Exception.Message)") }
    }

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

    # --- P10-07 add-in install/uninstall (temp Addins folders only), including this real package ---
    $install = Invoke-Script (Join-Path $PSScriptRoot 'test-install-addin.ps1') @('-PackageDirectory', $outA)
    $installFailures = @($install.Output -split "`n" | Where-Object { $_ -like 'FAIL *' })
    foreach ($line in $installFailures) { $failures.Add('install-addin: ' + $line.Substring(5)) }
    if ($install.Code -ne 0 -and $installFailures.Count -eq 0) { $failures.Add("install-addin tests exit $($install.Code): $(Get-Tail $install.Output)") }
    Assert ($install.Code -ne 0 -or $install.Output -match 'PASS install-addin') "install-addin tests did not report PASS: $(Get-Tail $install.Output)"

    # --- P10-08 deterministic release archive with SHA256SUMS, over both clean builds ---
    $archive = Invoke-Script (Join-Path $PSScriptRoot 'test-package-archive.ps1') @('-PackageDirectory', $outA, '-SecondPackageDirectory', $outB)
    $archiveFailures = @($archive.Output -split "`n" | Where-Object { $_ -like 'FAIL *' })
    foreach ($line in $archiveFailures) { $failures.Add('package-archive: ' + $line.Substring(5)) }
    if ($archive.Code -ne 0 -and $archiveFailures.Count -eq 0) { $failures.Add("package-archive tests exit $($archive.Code): $(Get-Tail $archive.Output)") }
    Assert ($archive.Code -ne 0 -or $archive.Output -match 'PASS package-archive') "package-archive tests did not report PASS: $(Get-Tail $archive.Output)"
    $cases = @(
        @{ Name = 'tamper'; Expect = 'sha256 mismatch'; Act = { param($d) $f = Join-Path $d 'cli/Bim.Cli.runtimeconfig.json'; $b = [IO.File]::ReadAllBytes($f); $b[0] = $b[0] -bxor 1; [IO.File]::WriteAllBytes($f, $b) } },
        @{ Name = 'missing'; Expect = 'missing file'; Act = { param($d) Remove-Item -LiteralPath (Join-Path $d 'mcp/Bim.Mcp.dll') } },
        @{ Name = 'extra'; Expect = 'unlisted file'; Act = { param($d) [IO.File]::WriteAllText((Join-Path $d 'cli/extra.txt'), 'x') } },
        @{ Name = 'forbidden'; Expect = 'forbidden'; Act = { param($d) [IO.File]::WriteAllText((Join-Path $d 'revit/RevitAPI.dll'), 'x') } },
        @{ Name = 'version'; Expect = 'VERSION'; Act = { param($d) [IO.File]::WriteAllText((Join-Path $d 'VERSION'), "9.9.9`n") } },
        @{ Name = 'duplicate'; Expect = 'duplicate'; Act = { param($d) $f = Join-Path $d 'manifest.json'; $lines = [Collections.Generic.List[string]]([IO.File]::ReadAllText($f).Split("`n")); $i = $lines.FindIndex({ param($l) $l.Contains('"path": "VERSION"') }); $lines.Insert($i, $lines[$i]); [IO.File]::WriteAllText($f, ($lines -join "`n")) } },
        @{ Name = 'commit'; Expect = 'source_commit'; Act = { param($d) $f = Join-Path $d 'manifest.json'; [IO.File]::WriteAllText($f, ([IO.File]::ReadAllText($f) -replace '"source_commit": "[0-9a-f]{40}"', '"source_commit": "abc"')) } },
        @{ Name = 'dirty'; Expect = 'source_dirty'; Act = { param($d) $f = Join-Path $d 'manifest.json'; [IO.File]::WriteAllText($f, ([IO.File]::ReadAllText($f) -replace '"source_dirty": (true|false)', '"source_dirty": "no"')) } },
        # P10-13: a missing or edited sbom.json is refused even when manifest.json is re-hashed to match it.
        @{ Name = 'sbom missing'; Expect = 'sbom_missing'; Act = { param($d) Remove-Item -LiteralPath (Join-Path $d 'sbom.json'); Update-CaseManifest $d } },
        @{ Name = 'sbom edited'; Expect = 'sha256 mismatch: sbom.json'; Act = { param($d) Edit-CaseSbom $d { param($t) $t -replace '"version": "[^"]*"', '"version": "9.9.9"' } } },
        @{ Name = 'sbom hash rehashed'; Expect = 'sbom_mismatch'; Act = { param($d) Edit-CaseSbom $d { param($t) ([regex]'"sha256": "[0-9a-f]{64}"').Replace($t, ('"sha256": "' + ('0' * 64) + '"'), 1) }; Update-CaseManifest $d } },
        @{ Name = 'sbom noncanonical rehashed'; Expect = 'sbom_mismatch'; Act = { param($d) Edit-CaseSbom $d { param($t) $t.Replace("`n", "`r`n") }; Update-CaseManifest $d } },
        @{ Name = 'sbom build-only rehashed'; Expect = 'sbom_build_only'; Act = { param($d) Edit-CaseSbom $d { param($t) $t.Replace('"nuget_references": []', '"nuget_references": [' + "`n" + '    { "name": "Nice3point.Revit.Api.RevitAPI", "version": "2025.0.2", "license": "MIT", "projects": ["runtimes/revit-native/Bim.Revit.csproj"] }' + "`n" + '  ]') }; Update-CaseManifest $d } },
        @{ Name = 'sbom unlisted assembly'; Expect = 'sbom_mismatch'; Act = { param($d) Copy-Item -LiteralPath (Join-Path $d 'cli/Bim.Core.dll') -Destination (Join-Path $d 'cli/Extra.Component.dll'); Update-CaseManifest $d } }
    )
    function Update-CaseManifest([string]$Directory) {
        $m = [IO.File]::ReadAllText((Join-Path $Directory 'manifest.json')) | ConvertFrom-Json
        Write-PackageText (Join-Path $Directory 'manifest.json') (New-PackageManifestText $Directory ([string]$m.version) ([string]$m.source_commit) ([bool]$m.source_dirty))
    }
    function Edit-CaseSbom([string]$Directory, [scriptblock]$Change) {
        $f = Join-Path $Directory 'sbom.json'
        $before = [IO.File]::ReadAllText($f)
        $after = [string](& $Change $before)
        if ($after -ceq $before) { throw "test setup: sbom edit changed nothing in $f" }
        Write-PackageText $f $after
    }
    foreach ($case in $cases) {
        $copy = Join-Path $work ('case ' + $case.Name)
        Copy-Item -LiteralPath $outA -Destination $copy -Recurse
        try { & $case.Act $copy } catch { $failures.Add("package-verify $($case.Name) setup failed: $($_.Exception.Message)"); continue }
        $run = Invoke-Script $verifier @('-PackageDirectory', $copy)
        Assert ($run.Code -ne 0) "package-verify accepted a $($case.Name) package"
        Assert ($run.Output -match [regex]::Escape($case.Expect)) "package-verify $($case.Name) must report '$($case.Expect)': $(Get-Tail $run.Output)"
    }

    # --- Symlinks/reparse points anywhere in the tree are refused (never followed or hashed through) ---
    $linkTargets = Join-Path $work 'link targets'
    New-Item -ItemType Directory -Force (Join-Path $linkTargets 'dir') | Out-Null
    [IO.File]::WriteAllText((Join-Path $linkTargets 'dir/inner.txt'), 'x')
    [IO.File]::WriteAllText((Join-Path $linkTargets 'RevitAPI.dll'), 'x')
    Copy-Item -LiteralPath (Join-Path $outA 'cli/Bim.Cli.runtimeconfig.json') -Destination (Join-Path $linkTargets 'same.json')
    $linkCases = @(
        @{ Name = 'directory symlink'; Path = 'cli/linked'; Target = (Join-Path $linkTargets 'dir') },
        @{ Name = 'file symlink to RevitAPI.dll'; Path = 'cli/ok.dll'; Target = (Join-Path $linkTargets 'RevitAPI.dll') },
        @{ Name = 'listed file replaced by identical symlink'; Path = 'cli/Bim.Cli.runtimeconfig.json'; Target = (Join-Path $linkTargets 'same.json') }
    )
    foreach ($case in $linkCases) {
        $copy = Join-Path $work ('link ' + [Guid]::NewGuid().ToString('N'))
        Copy-Item -LiteralPath $outA -Destination $copy -Recurse
        $linkPath = Join-Path $copy $case.Path
        if (Test-Path -LiteralPath $linkPath) { Remove-Item -LiteralPath $linkPath }
        try { New-Item -ItemType SymbolicLink -Path $linkPath -Target $case.Target -ErrorAction Stop | Out-Null }
        catch { Write-Host "SKIP symlink case '$($case.Name)': symlinks cannot be created here: $($_.Exception.Message)"; continue }
        $run = Invoke-Script $verifier @('-PackageDirectory', $copy)
        Assert ($run.Code -ne 0) "package-verify accepted a package containing a $($case.Name)"
        Assert ($run.Output -match 'symlink') "package-verify must report the $($case.Name) as a symlink: $(Get-Tail $run.Output)"
    }

    # --- Failure paths clean up: no output, no staging sibling, no build tree left in temp ---
    $fakes = Join-Path $work 'fake dotnet'
    New-Item -ItemType Directory -Force $fakes | Out-Null
    $fakeFail = Join-Path $fakes 'fail.ps1'
    [IO.File]::WriteAllText($fakeFail, "[IO.File]::WriteAllText((Join-Path `$PSScriptRoot 'fail.invoked'), 'x')`nexit 1`n")
    $fakeLeak = Join-Path $fakes 'leak.ps1'
    [IO.File]::WriteAllText($fakeLeak, @'
$all = @($args | ForEach-Object { "$_" })
$out = $null
for ($i = 0; $i -lt $all.Count; $i++) {
    if ($all[$i] -match '^--?output:?$' -and $i + 1 -lt $all.Count) { $out = $all[$i + 1] }
    elseif ($all[$i] -match '^--?output:(.+)$') { $out = $Matches[1] }
}
if (!$out) { [Console]::Error.WriteLine('fake dotnet: no --output in ' + ($all -join ' ')); exit 3 }
New-Item -ItemType Directory -Force $out | Out-Null
[IO.File]::WriteAllText((Join-Path $out 'RevitAPI.dll'), 'x')
exit 0
'@)
    foreach ($case in @(@{ Name = 'exit'; Fake = $fakeFail }, @{ Name = 'leak'; Fake = $fakeLeak })) {
        $temp = Join-Path $work ('temp ' + $case.Name)
        New-Item -ItemType Directory -Force $temp | Out-Null
        $failOut = Join-Path $work ('failed ' + $case.Name)
        $run = Invoke-ScriptWithTemp $temp $packager @('-OutputDirectory', $failOut, '-Dotnet', $case.Fake)
        Assert ($run.Code -ne 0) "package.ps1 succeeded with failing fake dotnet ($($case.Name))"
        if ($case.Name -eq 'exit') { Assert (Test-Path -LiteralPath (Join-Path $fakes 'fail.invoked')) 'fake dotnet (exit) was never invoked' }
        else { Assert ($run.Output -match 'forbidden') "leaked RevitAPI.dll must be refused as forbidden: $(Get-Tail $run.Output)" }
        Assert (!(Test-Path -LiteralPath $failOut)) "failed package ($($case.Name)) left its output directory"
        Assert (@(Get-ChildItem -LiteralPath $work -Force -Directory | Where-Object { $_.Name -like "failed $($case.Name).partial-*" }).Count -eq 0) "failed package ($($case.Name)) left a .partial staging directory"
        Assert ((Get-BuildTrees $temp).Count -eq 0) "failed package ($($case.Name)) left a bim-package-src-* build tree in temp"
    }

    # --- Build files inherited from the temp parent (Directory.Build.targets/.rsp, Directory.Packages.props) are ignored ---
    $evil = Join-Path $work 'hostile temp'
    New-Item -ItemType Directory -Force $evil | Out-Null
    [IO.File]::WriteAllText((Join-Path $evil 'Directory.Build.targets'), '<Project><Target Name="BimInjected" AfterTargets="Publish"><WriteLinesToFile File="$(PublishDir)INJECTED.txt" Lines="injected" /></Target></Project>')
    [IO.File]::WriteAllText((Join-Path $evil 'Directory.Packages.props'), '<Project><PropertyGroup><Company>INJECTED</Company></PropertyGroup></Project>')
    [IO.File]::WriteAllText((Join-Path $evil 'Directory.Build.rsp'), '-p:Product=INJECTED')
    $outC = Join-Path $work 'release C'
    $runC = Invoke-ScriptWithTemp $evil $packager @('-OutputDirectory', $outC, '-Dotnet', $Dotnet)
    Assert ($runC.Code -eq 0) "package build under a hostile temp parent exit $($runC.Code): $(Get-Tail $runC.Output)"
    if ($runC.Code -eq 0) {
        Assert (@(Get-ChildItem -LiteralPath $outC -Recurse -File -Filter 'INJECTED.txt').Count -eq 0) 'Directory.Build.targets from the temp parent was imported (INJECTED.txt in package)'
        Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $outC 'manifest.json'))) -eq [Convert]::ToBase64String($bytesA)) 'build files inherited from the temp parent changed the package (manifest differs from clean build)'
    }
    Assert ((Get-BuildTrees $evil).Count -eq 0) 'successful package left a bim-package-src-* build tree in temp'

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
