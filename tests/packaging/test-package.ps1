#Requires -Version 7.2
# P10-04 release packaging checks (plain assertions, no Pester).
# Builds the release layout twice from clean, separate temp build trees and requires byte-identical manifests and
# files; checks layout, version stamp (single source scripts/package.version, git fallback), forbidden content
# (Revit/Autodesk assemblies, Revit models, credentials), never-overwrite and manifest tamper detection.
# P10-07: runs tests/packaging/test-install-addin.ps1 against the built package (temp Addins folders only).
# P10-08: runs tests/packaging/test-package-archive.ps1 against both clean builds (archives must be byte-identical).
# P10-13: sbom.json (shipped assemblies with SHA-256, NuGet references of the shipped csproj closure, build-only
# references such as Nice3point never listed) is listed in manifest.json; package-verify refuses a missing/edited sbom.
# P10-14: transitive NuGet packages from the shipped projects' obj/project.assets.json are listed (direct/transitive,
# shipped assembly/native file names); every shipped third-party binary carries its package or is refused
# (sbom_unattributed, sbom_attribution_ambiguous); first-party = project entries of the folder's *.deps.json.
# P7-12: skills/ (every tracked skills file) ships next to cli/; the packaged generate-prototypes, copied outside any
# repository, reads its pack from there byte-equal to the repository run, and refuses repository_not_found without it;
# a tracked skills/ entry that is not a regular file (symlink, submodule) is refused (skills_entry_not_regular).
# P10-16: a PE/ELF/Mach-O file (first bytes) outside .dll/.exe/.so[.N]/.dylib is refused (sbom_binary_unclassified).
# P10-17: a manifest-listed file that cannot be read is manifest_file_unreadable (never an exception); a FIFO or socket
# is refused by file type before any open (package_special_file), with every such run time-bounded so a regression
# fails instead of hanging; mkfifo/socket cases are counted SKIPs where unavailable (Windows).
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
# Like Invoke-Script but bounded: a child still running after $TimeoutSeconds (e.g. blocked opening a FIFO) is killed
# and reported as TimedOut instead of hanging the whole test run.
function Invoke-ScriptBounded([string]$Script, [string[]]$Arguments, [int]$TimeoutSeconds = 90) {
    $info = [Diagnostics.ProcessStartInfo]::new($pwsh)
    foreach ($argument in @('-NoProfile', '-File', $Script) + @($Arguments)) { $info.ArgumentList.Add($argument) }
    $info.UseShellExecute = $false; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill($true) } catch { }
            [void]$process.WaitForExit(10000)
            return [pscustomobject]@{ Code = -1; TimedOut = $true; Output = "timed out after $TimeoutSeconds s (killed)" }
        }
        $process.WaitForExit()
        return [pscustomobject]@{ Code = $process.ExitCode; TimedOut = $false; Output = ($stdout.Result + "`n" + $stderr.Result) }
    } finally { $process.Dispose() }
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

    # --- P10-13 review: version normalisation, licence refusal, case-insensitive MSBuild attributes, verified references ---
    try {
        $reviewSrc = Join-Path $work 'sbom review src'
        $reviewFiles = [ordered]@{
            'app/App.csproj' = '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Short.Ver" Version="1.0" /><PackageReference Include="Four.Part" Version="[1.2.3.0]" /><PackageReference include="Lower.Ship" version="4.0" /><PackageReference include="Lower.Build" version="4.0" excludeassets="RUNTIME" /><PackageReference Include="Lower.Inc" Version="1.0.0" includeassets="compile" /></ItemGroup></Project>'
            'nuget/short.ver/1.0.0/short.ver.nuspec' = '<?xml version="1.0"?><package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>Short.Ver</id><version>1.0</version><license type="expression">MIT</license></metadata></package>'
            'nuget/four.part/1.2.3/four.part.nuspec' = '<?xml version="1.0"?><package xmlns="http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd"><metadata><id>Four.Part</id><version>1.2.3.0</version><license type="expression">Apache-2.0</license></metadata></package>'
            'nuget/lower.ship/4.0.0/lower.ship.nuspec' = '<?xml version="1.0"?><package><metadata><id>Lower.Ship</id><version>4.0</version><license type="expression">BSD-3-Clause</license></metadata></package>'
            'missing/App.csproj' = '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Missing.Lic" Version="2.0" /></ItemGroup></Project>'
        }
        foreach ($name in $reviewFiles.Keys) {
            $path = Join-Path $reviewSrc $name
            New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
            [IO.File]::WriteAllText($path, $reviewFiles[$name])
        }
        $reviewRefs = @(Get-PackageNuGetReferences -Root $reviewSrc -Projects @('app/App.csproj') -PackagesRoot (Join-Path $reviewSrc 'nuget'))
        $reviewText = ($reviewRefs | ForEach-Object { "$($_.Name)@$($_.Version) $($_.License)" }) -join '; '
        Assert ($reviewText -ceq 'Four.Part@1.2.3 Apache-2.0; Lower.Ship@4.0.0 BSD-3-Clause; Short.Ver@1.0.0 MIT') "NuGet versions must be normalised (1.0 -> 1.0.0, 1.2.3.0 -> 1.2.3) before the nuspec lookup, and lowercase MSBuild attributes honoured: got '$reviewText'"
        $threw = $null
        try { Assert-PackageReferenceLicenses -References $reviewRefs -Allowlist @() } catch { $threw = $_.Exception.Message }
        Assert ($null -eq $threw) "references with resolved licences must be accepted: $threw"
        $missingRefs = @(Get-PackageNuGetReferences -Root $reviewSrc -Projects @('missing/App.csproj') -PackagesRoot (Join-Path $reviewSrc 'nuget'))
        $threw = $null
        try { Assert-PackageReferenceLicenses -References $missingRefs -Allowlist @() } catch { $threw = $_.Exception.Message }
        Assert ($threw -match 'sbom_license_unknown' -and $threw -match 'Missing\.Lic') "a shipped reference whose licence resolves to NOASSERTION (no nuspec) must be refused with sbom_license_unknown: '$threw'"
        $threw = $null
        try { Assert-PackageReferenceLicenses -References $missingRefs -Allowlist @('Missing.Lic') } catch { $threw = $_.Exception.Message }
        Assert ($null -eq $threw) "an allowlisted reference may keep NOASSERTION: $threw"
        Assert (([IO.File]::ReadAllText($packager)) -match 'Assert-PackageReferenceLicenses') 'package.ps1 must refuse shipped references with unknown licences (Assert-PackageReferenceLicenses)'

        # A re-hashed manifest must not make an edited reference list acceptable.
        function New-RefPackage([string]$Directory, [object[]]$References) {
            New-Item -ItemType Directory -Force (Join-Path $Directory 'cli') | Out-Null
            Write-PackageText (Join-Path $Directory 'VERSION') "1.2.3`n"
            foreach ($ref in $References) { Write-PackageText (Join-Path $Directory "cli/$($ref.Name).dll") "not a real assembly $($ref.Name)" }
            Write-PackageText (Join-Path $Directory 'sbom.json') (New-PackageSbomText $Directory '1.2.3' ('a' * 40) $References)
            Write-PackageText (Join-Path $Directory 'manifest.json') (New-PackageManifestText $Directory '1.2.3' ('a' * 40) $false)
        }
        $alpha = [pscustomobject]@{ Name = 'Alpha.Json'; Version = '1.2.3'; License = 'MIT'; Projects = [string[]]@('app/App.csproj') }
        $zeta = [pscustomobject]@{ Name = 'Zeta.Lib'; Version = '2.0.0'; License = 'Apache-2.0'; Projects = [string[]]@('app/App.csproj') }
        $refCases = @(
            @{ Name = 'one ref licence'; Refs = @($alpha); Edit = { param($d, $t) $t.Replace('"name": "Alpha.Json", "version": "1.2.3", "license": "MIT"', '"name": "Alpha.Json", "version": "1.2.3", "license": "BSD-3-Clause"') } },
            @{ Name = 'two refs licence'; Refs = @($alpha, $zeta); Edit = { param($d, $t) $t.Replace('"name": "Zeta.Lib", "version": "2.0.0", "license": "Apache-2.0"', '"name": "Zeta.Lib", "version": "2.0.0", "license": "MIT"') } },
            @{ Name = 'two refs order'; Refs = @($alpha, $zeta); Edit = { param($d, $t) New-PackageSbomText $d '1.2.3' ('a' * 40) @($zeta, $alpha) } },
            @{ Name = 'duplicate ref'; Refs = @($alpha); Edit = { param($d, $t) New-PackageSbomText $d '1.2.3' ('a' * 40) @($alpha, $alpha) } }
        )
        foreach ($case in $refCases) {
            $pkg = Join-Path $work ('sbom refs ' + $case.Name)
            New-RefPackage $pkg $case.Refs
            $clean = Invoke-Script $verifier @('-PackageDirectory', $pkg)
            Assert ($clean.Code -eq 0) "synthetic package with references ($($case.Name)) must verify before the edit: $(Get-Tail $clean.Output)"
            $sbomPath = Join-Path $pkg 'sbom.json'
            $before = [IO.File]::ReadAllText($sbomPath)
            $after = [string](& $case.Edit $pkg $before)
            Assert ($after -cne $before) "test setup: $($case.Name) edit changed nothing"
            Write-PackageText $sbomPath $after
            Write-PackageText (Join-Path $pkg 'manifest.json') (New-PackageManifestText $pkg '1.2.3' ('a' * 40) $false)
            $run = Invoke-Script $verifier @('-PackageDirectory', $pkg)
            Assert ($run.Code -ne 0 -and $run.Output -match 'sbom_mismatch') "package-verify must refuse an sbom with edited references ($($case.Name)) even with a re-hashed manifest: $(Get-Tail $run.Output)"
        }
    } catch { $failures.Add("SBOM review checks threw: $($_.Exception.Message)") }

    # --- P10-14 transitive NuGet attribution: every shipped third-party binary (.dll/.exe, native .so/.dylib) is
    # attributed to its package (direct or transitive) from each shipped entry project's obj/project.assets.json;
    # first-party files are the project entries of the published folder's *.deps.json and stay NOASSERTION ---
    try {
        $tSrc = Join-Path $work 'sbom transitive src'
        $tAlt = Join-Path $work 'sbom transitive alt nuget'
        $tFiles = [ordered]@{
            'app/App.csproj' = '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../lib/Lib.csproj" /><PackageReference Include="Alpha.Json" Version="1.2.3" /><PackageReference Include="Nice3point.Revit.Api.RevitAPI" Version="[2025.0.2]" ExcludeAssets="runtime" PrivateAssets="all" /></ItemGroup></Project>'
            'lib/Lib.csproj' = '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Delta.Lib" Version="3.0.0" /><PackageReference Include="Build.Tool" Version="1.0.0" ExcludeAssets="runtime" /></ItemGroup></Project>'
            'other/Other.csproj' = '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Omega.Tool" Version="1.0.0" /></ItemGroup></Project>'
            'nuget/alpha.json/1.2.3/alpha.json.nuspec' = '<?xml version="1.0"?><package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>Alpha.Json</id><version>1.2.3</version><license type="expression">MIT</license></metadata></package>'
            'nuget/beta.core/2.1.0/beta.core.nuspec' = '<?xml version="1.0"?><package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>Beta.Core</id><version>2.1.0</version><licenseUrl>https://licenses.nuget.org/MIT%20OR%20Apache-2.0</licenseUrl></metadata></package>'
            'nuget/beta.core/2.0.0/beta.core.nuspec' = '<?xml version="1.0"?><package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>Beta.Core</id><version>2.0.0</version><licenseUrl>https://licenses.nuget.org/Apache-2.0</licenseUrl></metadata></package>'
            'nuget/gamma.native/1.0.0/gamma.native.nuspec' = '<?xml version="1.0"?><package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>Gamma.Native</id><version>1.0.0</version><license type="file">LICENSE.txt</license><licenseUrl>https://example.invalid/license</licenseUrl></metadata></package>'
            'nuget/sigma.native/1.0.0/sigma.native.nuspec' = '<?xml version="1.0"?><package><metadata><id>Sigma.Native</id><version>1.0.0</version><license type="expression">MIT</license></metadata></package>'
            'nuget/tau.win/1.0.0/tau.win.nuspec' = '<?xml version="1.0"?><package><metadata><id>Tau.Win</id><version>1.0.0</version><license type="expression">MIT</license></metadata></package>'
            'nuget/omega.tool/1.0.0/omega.tool.nuspec' = '<?xml version="1.0"?><package><metadata><id>Omega.Tool</id><version>1.0.0</version><license type="expression">MIT</license></metadata></package>'
            # The restore's own packageFolders are preferred over -PackagesRoot: this Zlib copy must not win.
            'nuget/delta.lib/3.0.0/delta.lib.nuspec' = '<?xml version="1.0"?><package><metadata><id>Delta.Lib</id><version>3.0.0</version><license type="expression">Zlib</license></metadata></package>'
        }
        foreach ($name in $tFiles.Keys) {
            $path = Join-Path $tSrc $name
            New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
            [IO.File]::WriteAllText($path, $tFiles[$name])
        }
        New-Item -ItemType Directory -Force (Join-Path $tAlt 'delta.lib/3.0.0') | Out-Null
        [IO.File]::WriteAllText((Join-Path $tAlt 'delta.lib/3.0.0/delta.lib.nuspec'), '<?xml version="1.0"?><package><metadata><id>Delta.Lib</id><version>3.0.0</version><license type="expression">BSD-3-Clause</license></metadata></package>')
        $none = [ordered]@{}
        $runtime = { param([string[]]$Paths) $d = [ordered]@{}; foreach ($p in $Paths) { $d[$p] = [ordered]@{} }; $d }
        $appAssets = [ordered]@{
            version = 3
            targets = [ordered]@{
                'net8.0' = [ordered]@{
                    'Alpha.Json/1.2.3' = [ordered]@{ type = 'package'; dependencies = [ordered]@{ 'Beta.Core' = '[2.0.0, )'; 'Meta.Only' = '1.0.0' }; compile = (& $runtime 'lib/net8.0/Alpha.Json.dll'); runtime = (& $runtime 'lib/net8.0/Alpha.Json.dll'); resource = [ordered]@{ 'lib/net8.0/en/Alpha.Json.resources.dll' = [ordered]@{ locale = 'en' } } }
                    'Beta.Core/2.1.0' = [ordered]@{ type = 'package'; dependencies = [ordered]@{ 'gamma.native' = '1.0.0' }; runtime = (& $runtime 'lib/net8.0/Beta.Core.dll', 'lib/net8.0/Beta.Core.Extensions.dll') }
                    'Gamma.Native/1.0.0' = [ordered]@{ type = 'package'; runtimeTargets = [ordered]@{ 'runtimes/win-x64/native/gamma.dll' = [ordered]@{ assetType = 'native'; rid = 'win-x64' }; 'runtimes/linux-x64/native/libgamma.so' = [ordered]@{ assetType = 'native'; rid = 'linux-x64' }; 'runtimes/linux-x64/native/readme.txt' = [ordered]@{ assetType = 'native'; rid = 'linux-x64' } } }
                    'Meta.Only/1.0.0' = [ordered]@{ type = 'package'; compile = (& $runtime 'lib/net8.0/_._'); runtime = (& $runtime 'lib/net8.0/_._') }
                    'Nice3point.Revit.Api.RevitAPI/2025.0.2' = [ordered]@{ type = 'package'; dependencies = [ordered]@{ 'Vendor.Transitive' = '1.0.0' }; compile = (& $runtime 'ref/RevitAPI.dll') }
                    'Vendor.Transitive/1.0.0' = [ordered]@{ type = 'package'; runtime = (& $runtime 'lib/net8.0/Vendor.Transitive.dll') }
                    'Lib/1.0.0' = [ordered]@{ type = 'project'; dependencies = [ordered]@{ 'Delta.Lib' = '3.0.0'; 'Build.Tool' = '1.0.0' } }
                    'Delta.Lib/3.0.0' = [ordered]@{ type = 'package'; dependencies = [ordered]@{ 'Beta.Core' = '2.0.0'; 'Sigma.Native' = '1.0.0' }; runtime = (& $runtime 'lib/net8.0/Delta.Lib.dll') }
                    'Build.Tool/1.0.0' = [ordered]@{ type = 'package'; dependencies = [ordered]@{ 'Hidden.Dep' = '1.0.0' }; runtime = (& $runtime 'lib/net8.0/Build.Tool.dll') }
                    'Hidden.Dep/1.0.0' = [ordered]@{ type = 'package'; runtime = (& $runtime 'lib/net8.0/Hidden.Dep.dll') }
                    'Sigma.Native/1.0.0' = [ordered]@{ type = 'package'; runtimeTargets = [ordered]@{ 'runtimes/linux-x64/native/libsigma.so.1' = [ordered]@{ assetType = 'native'; rid = 'linux-x64' } } }
                }
                'net8.0/win-x64' = [ordered]@{
                    'Alpha.Json/1.2.3' = [ordered]@{ type = 'package'; dependencies = [ordered]@{ 'Tau.Win' = '1.0.0' }; runtime = (& $runtime 'lib/net8.0/Alpha.Json.dll') }
                    'Tau.Win/1.0.0' = [ordered]@{ type = 'package'; runtime = (& $runtime 'lib/net8.0/Tau.Win.dll') }
                }
            }
            # Windows-style key (backslashes, trailing separator) for the folder holding Delta.Lib's nuspec.
            packageFolders = [ordered]@{ ($tAlt.Replace('/', '\') + '\') = $none; ((Join-Path $work 'no packages') + '/') = $none }
        }
        # lib is not an entry project: its own restore (a different Beta.Core, an extra package) must not be read.
        $libAssets = [ordered]@{ version = 3; targets = [ordered]@{ 'net8.0' = [ordered]@{
            'Delta.Lib/3.0.0' = [ordered]@{ type = 'package'; dependencies = [ordered]@{ 'Beta.Core' = '2.0.0'; 'Lib.Only' = '9.9.9' }; runtime = (& $runtime 'lib/net8.0/Delta.Lib.dll') }
            'Beta.Core/2.0.0' = [ordered]@{ type = 'package'; runtime = (& $runtime 'lib/net8.0/Beta.Core.dll') }
            'Lib.Only/9.9.9' = [ordered]@{ type = 'package'; runtime = (& $runtime 'lib/net8.0/Lib.Only.dll') } } } }
        $otherAssets = [ordered]@{ version = 3; targets = [ordered]@{ 'net8.0' = [ordered]@{
            'Omega.Tool/1.0.0' = [ordered]@{ type = 'package'; dependencies = [ordered]@{ 'Beta.Core' = '2.0.0' }; runtime = (& $runtime 'lib/net8.0/Omega.Tool.exe') }
            'Beta.Core/2.0.0' = [ordered]@{ type = 'package'; runtime = (& $runtime 'lib/net8.0/Beta.Core.dll', 'lib/net8.0/Beta.Core.Extensions.dll') } } } }
        foreach ($pair in @(@('app', $appAssets), @('lib', $libAssets), @('other', $otherAssets))) {
            New-Item -ItemType Directory -Force (Join-Path $tSrc "$($pair[0])/obj") | Out-Null
            [IO.File]::WriteAllText((Join-Path $tSrc "$($pair[0])/obj/project.assets.json"), ($pair[1] | ConvertTo-Json -Depth 20))
        }

        $tRefs = @(Get-PackageNuGetReferences -Root $tSrc -Projects @('app/App.csproj', 'other/Other.csproj') -PackagesRoot (Join-Path $tSrc 'nuget'))
        $tText = ($tRefs | ForEach-Object { "$($_.Name)@$($_.Version) $($_.License) $($_.Dependency) [$(@($_.Projects) -join ',')] {$(@($_.Assemblies) -join ',')} <$(@($_.Natives) -join ',')>" }) -join '; '
        $tWant = 'Alpha.Json@1.2.3 MIT direct [app/App.csproj] {Alpha.Json.dll,Alpha.Json.resources.dll} <>; ' +
            'Beta.Core@2.0.0 Apache-2.0 transitive [other/Other.csproj] {Beta.Core.Extensions.dll,Beta.Core.dll} <>; ' +
            'Beta.Core@2.1.0 MIT OR Apache-2.0 transitive [app/App.csproj] {Beta.Core.Extensions.dll,Beta.Core.dll} <>; ' +
            'Delta.Lib@3.0.0 BSD-3-Clause direct [lib/Lib.csproj] {Delta.Lib.dll} <>; ' +
            'Gamma.Native@1.0.0 NOASSERTION transitive [app/App.csproj] {gamma.dll} <libgamma.so>; ' +
            'Omega.Tool@1.0.0 MIT direct [other/Other.csproj] {Omega.Tool.exe} <>; ' +
            'Sigma.Native@1.0.0 MIT transitive [app/App.csproj] {} <libsigma.so.1>; ' +
            'Tau.Win@1.0.0 MIT transitive [app/App.csproj] {Tau.Win.dll} <>'
        Assert ($tText -ceq $tWant) "P10-14: SBOM references must come from each entry project's restore graph (RID targets, resource/.exe/native assets, per-entry versions, licence from expression, percent-encoded licenses.nuget.org URL, restore packageFolders first): got '$tText' want '$tWant'"
        foreach ($excluded in 'Meta.Only', 'Nice3point', 'Vendor.Transitive', 'Build.Tool', 'Hidden.Dep', 'Lib.Only') { Assert (!($tText -match [regex]::Escape($excluded))) "P10-14: $excluded (no runtime asset, build-only, reachable only through a build-only reference, or only in a non-entry project's restore) must not be listed as shipped: '$tText'" }
        $threw = $null
        try { Assert-PackageReferenceLicenses -References $tRefs -Allowlist @() } catch { $threw = $_.Exception.Message }
        Assert ($threw -match 'sbom_license_unknown' -and $threw -match 'Gamma\.Native@1\.0\.0' -and $threw -notmatch 'Beta\.Core') "P10-14: a transitive package whose licence is not an SPDX expression must be refused like a direct one: '$threw'"

        # A shipped reference declared at one version but restored at another is refused.
        $mSrc = Join-Path $work 'sbom transitive mismatch'
        New-Item -ItemType Directory -Force (Join-Path $mSrc 'app/obj') | Out-Null
        [IO.File]::WriteAllText((Join-Path $mSrc 'app/App.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Alpha.Json" Version="1.2.3" /></ItemGroup></Project>')
        [IO.File]::WriteAllText((Join-Path $mSrc 'app/obj/project.assets.json'), ([ordered]@{ version = 3; targets = [ordered]@{ 'net8.0' = [ordered]@{ 'Alpha.Json/1.3.0' = [ordered]@{ type = 'package'; runtime = (& $runtime 'lib/net8.0/Alpha.Json.dll') } } } } | ConvertTo-Json -Depth 20))
        $threw = $null
        try { [void](Get-PackageNuGetReferences -Root $mSrc -Projects @('app/App.csproj') -PackagesRoot (Join-Path $mSrc 'nuget')) } catch { $threw = $_.Exception.Message }
        Assert ($threw -match 'Alpha\.Json' -and $threw -match '1\.3\.0' -and $threw -match '1\.2\.3') "P10-14: a shipped reference restored at a version other than its declared one must be refused: '$threw'"

        # Synthetic package: cli from app (Beta.Core 2.1.0), mcp from other (Beta.Core 2.0.0); deps.json names the
        # first-party project files and which Beta.Core version each folder loads.
        $tPkg = Join-Path $work 'sbom transitive pkg'
        New-Item -ItemType Directory -Force $tPkg | Out-Null
        Write-PackageText (Join-Path $tPkg 'VERSION') "1.2.3`n"
        $tBinaries = 'cli/Alpha.Json.dll', 'cli/en/Alpha.Json.resources.dll', 'cli/Beta.Core.dll', 'cli/Beta.Core.Extensions.dll', 'cli/Delta.Lib.dll', 'cli/Tau.Win.dll',
            'cli/runtimes/win-x64/native/gamma.dll', 'cli/runtimes/linux-x64/native/libgamma.so', 'cli/runtimes/linux-x64/native/libsigma.so.1', 'cli/runtimes/linux-x64/native/libown.so', 'cli/Bim.Cli.dll', 'cli/Bim.Core.dll',
            'mcp/Omega.Tool.exe', 'mcp/Beta.Core.dll', 'mcp/Beta.Core.Extensions.dll', 'mcp/Bim.Mcp.dll'
        foreach ($file in $tBinaries) { New-Item -ItemType Directory -Force (Split-Path (Join-Path $tPkg $file)) | Out-Null; Write-PackageText (Join-Path $tPkg $file) "not a real binary $file" }
        # $AppNatives: first-party native assets (runtimeTargets) of the app project entry, e.g. a P/Invoke library it builds.
        function New-TestDeps([string]$App, [string[]]$Projects, [string]$BetaVersion, [string[]]$AppNatives = @()) {
            $targets = [ordered]@{}; $libraries = [ordered]@{}
            foreach ($project in @($App) + $Projects) { $targets["$project/1.2.3"] = [ordered]@{ runtime = [ordered]@{ "$project.dll" = [ordered]@{} } }; $libraries["$project/1.2.3"] = [ordered]@{ type = 'project'; serviceable = $false; sha512 = '' } }
            if ($AppNatives.Count) {
                $nativeTargets = [ordered]@{}
                foreach ($native in $AppNatives) { $nativeTargets[$native] = [ordered]@{ assetType = 'native'; rid = $native.Split('/')[1] } }
                $targets["$App/1.2.3"]['runtimeTargets'] = $nativeTargets
            }
            if ($BetaVersion) {
                $targets["Beta.Core/$BetaVersion"] = [ordered]@{ runtime = [ordered]@{ 'lib/net8.0/Beta.Core.dll' = [ordered]@{}; 'lib/net8.0/Beta.Core.Extensions.dll' = [ordered]@{} } }
                $libraries["Beta.Core/$BetaVersion"] = [ordered]@{ type = 'package'; serviceable = $true; sha512 = 'sha512-x'; path = "beta.core/$BetaVersion" }
            }
            return ([ordered]@{ runtimeTarget = [ordered]@{ name = '.NETCoreApp,Version=v8.0'; signature = '' }; compilationOptions = [ordered]@{}; targets = [ordered]@{ '.NETCoreApp,Version=v8.0' = $targets }; libraries = $libraries } | ConvertTo-Json -Depth 20)
        }
        Write-PackageText (Join-Path $tPkg 'cli/Bim.Cli.deps.json') (New-TestDeps 'Bim.Cli' @('Bim.Core') '2.1.0' @('runtimes/linux-x64/native/libown.so'))
        Write-PackageText (Join-Path $tPkg 'mcp/Bim.Mcp.deps.json') (New-TestDeps 'Bim.Mcp' @() '2.0.0')
        function Write-TestSbom([string]$Directory, [object[]]$References) {
            Write-PackageText (Join-Path $Directory 'sbom.json') (New-PackageSbomText $Directory '1.2.3' ('a' * 40) $References)
            Write-PackageText (Join-Path $Directory 'manifest.json') (New-PackageManifestText $Directory '1.2.3' ('a' * 40) $false)
        }
        Write-TestSbom $tPkg $tRefs
        $tSbom = [IO.File]::ReadAllText((Join-Path $tPkg 'sbom.json'))
        $tParsed = $tSbom | ConvertFrom-Json
        $tAsm = (@($tParsed.assemblies) | ForEach-Object { "$($_.path)=$($_.license)@$(if ($_.PSObject.Properties['package']) { $_.package } else { '-' })" }) -join '; '
        $tAsmWant = 'cli/Alpha.Json.dll=MIT@Alpha.Json@1.2.3; cli/Beta.Core.Extensions.dll=MIT OR Apache-2.0@Beta.Core@2.1.0; cli/Beta.Core.dll=MIT OR Apache-2.0@Beta.Core@2.1.0; ' +
            'cli/Bim.Cli.dll=NOASSERTION@-; cli/Bim.Core.dll=NOASSERTION@-; cli/Delta.Lib.dll=BSD-3-Clause@Delta.Lib@3.0.0; cli/Tau.Win.dll=MIT@Tau.Win@1.0.0; ' +
            'cli/en/Alpha.Json.resources.dll=MIT@Alpha.Json@1.2.3; cli/runtimes/win-x64/native/gamma.dll=NOASSERTION@Gamma.Native@1.0.0; ' +
            'mcp/Beta.Core.Extensions.dll=Apache-2.0@Beta.Core@2.0.0; mcp/Beta.Core.dll=Apache-2.0@Beta.Core@2.0.0; mcp/Bim.Mcp.dll=NOASSERTION@-; mcp/Omega.Tool.exe=MIT@Omega.Tool@1.0.0'
        Assert ($tAsm -ceq $tAsmWant) "P10-14: every shipped third-party assembly must carry the package version its folder loads (deps.json) and its licence; first-party project files stay NOASSERTION without a package: got '$tAsm' want '$tAsmWant'"
        $tRefLine = @($tSbom.Split("`n") | Where-Object { $_.StartsWith('    { "name": "Beta.Core", "version": "2.1.0", ') })
        Assert ($tRefLine.Count -eq 1 -and $tRefLine[0].Contains('"dependency": "transitive", "assemblies": ["Beta.Core.Extensions.dll", "Beta.Core.dll"], "natives": [] }')) "P10-14: an sbom reference must record direct/transitive and its shipped assembly and native file names: '$($tRefLine -join ' ')'"
        $tRefLine = @($tSbom.Split("`n") | Where-Object { $_.StartsWith('    { "name": "Gamma.Native", ') })
        Assert ($tRefLine.Count -eq 1 -and $tRefLine[0].Contains('"assemblies": ["gamma.dll"], "natives": ["libgamma.so"] }')) "P10-14: native libraries must be recorded on their reference: '$($tRefLine -join ' ')'"
        $tClean = Invoke-Script $verifier @('-PackageDirectory', $tPkg)
        Assert ($tClean.Code -eq 0) "P10-14: a package whose third-party binaries are all attributed must verify: $(Get-Tail $tClean.Output)"

        # Refusals with a canonical sbom and a re-hashed manifest: unattributed managed, native and Bim.*-named
        # non-project files, a reference that no longer lists a shipped file, and two package versions claiming one file.
        $tCases = @(
            @{ Name = 'stray dll'; Expect = 'sbom_unattributed'; Path = 'cli/Stray.Thing.dll'; Act = { param($d) Write-PackageText (Join-Path $d 'cli/Stray.Thing.dll') 'stray'; Write-TestSbom $d $tRefs } },
            @{ Name = 'stray native'; Expect = 'sbom_unattributed'; Path = 'cli/runtimes/linux-x64/native/libstray.so'; Act = { param($d) Write-PackageText (Join-Path $d 'cli/runtimes/linux-x64/native/libstray.so') 'stray'; Write-TestSbom $d $tRefs } },
            @{ Name = 'stray dylib'; Expect = 'sbom_unattributed'; Path = 'cli/runtimes/osx-x64/native/libstray.dylib'; Act = { param($d) New-Item -ItemType Directory -Force (Join-Path $d 'cli/runtimes/osx-x64/native') | Out-Null; Write-PackageText (Join-Path $d 'cli/runtimes/osx-x64/native/libstray.dylib') 'stray'; Write-TestSbom $d $tRefs } },
            @{ Name = 'Bim-named third party'; Expect = 'sbom_unattributed'; Path = 'cli/Bim.ThirdParty.dll'; Act = { param($d) Write-PackageText (Join-Path $d 'cli/Bim.ThirdParty.dll') 'stray'; Write-TestSbom $d $tRefs } },
            @{ Name = 'dropped from list'; Expect = 'sbom_unattributed'; Path = 'cli/Beta.Core.dll'; Act = { param($d)
                $f = Join-Path $d 'sbom.json'; $t = [IO.File]::ReadAllText($f)
                $old = '"version": "2.1.0", "license": "MIT OR Apache-2.0", "projects": ["app/App.csproj"], "dependency": "transitive", "assemblies": ["Beta.Core.Extensions.dll", "Beta.Core.dll"]'
                if (!$t.Contains($old)) { throw 'test setup: Beta.Core 2.1.0 reference line not found' }
                Write-PackageText $f $t.Replace($old, '"version": "2.1.0", "license": "MIT OR Apache-2.0", "projects": ["app/App.csproj"], "dependency": "transitive", "assemblies": ["Beta.Core.Extensions.dll"]')
                Write-PackageText (Join-Path $d 'manifest.json') (New-PackageManifestText $d '1.2.3' ('a' * 40) $false) } },
            @{ Name = 'ambiguous version'; Expect = 'sbom_attribution_ambiguous'; Path = 'mcp/Beta.Core.dll'; Act = { param($d) Write-PackageText (Join-Path $d 'mcp/Bim.Mcp.deps.json') (New-TestDeps 'Bim.Mcp' @() ''); Write-TestSbom $d $tRefs } }
        )
        foreach ($case in $tCases) {
            $copy = Join-Path $work ('sbom transitive case ' + $case.Name)
            Copy-Item -LiteralPath $tPkg -Destination $copy -Recurse
            try { & $case.Act $copy } catch { $failures.Add("P10-14 $($case.Name) setup failed: $($_.Exception.Message)"); continue }
            $run = Invoke-Script $verifier @('-PackageDirectory', $copy)
            Assert ($run.Code -ne 0 -and $run.Output -match $case.Expect -and $run.Output -match [regex]::Escape($case.Path) -and $run.Output -notmatch 'Bim\.Cli\.dll|Bim\.Core\.dll|Bim\.Mcp\.dll') "P10-14: package-verify must refuse $($case.Name) with $($case.Expect) naming $($case.Path), never a first-party file: $(Get-Tail $run.Output)"
        }

        # P10-15: manifest.json and sbom.json must agree on the shipped binaries in both directions (every manifest
        # .dll/.exe has an sbom assemblies line, every manifest .so/.dylib is named by a listed reference or is
        # first-party, and every sbom assemblies line has a manifest entry), typed sbom_manifest_mismatch naming the path
        # in the message of its direction. The manifest-only cases edit manifest.json alone (the file and its sbom line
        # stay, or the entry has no file), so they fail only if the check reads manifest.json rather than the disk; other
        # codes (unlisted file, missing file, sbom_unattributed) may also appear.
        $unlined = 'without an sbom.json line'; $orphaned = 'without a manifest.json entry'
        $xCases = @(
            @{ Name = 'manifest dll without sbom line'; Path = 'cli/Tau.Win.dll'; Direction = $unlined; Act = { param($d) Remove-TestJsonLine (Join-Path $d 'sbom.json') '    { "path": "cli/Tau.Win.dll", '; Update-TestManifest $d } },
            @{ Name = 'manifest exe without sbom line'; Path = 'mcp/Omega.Tool.exe'; Direction = $unlined; Act = { param($d) Remove-TestJsonLine (Join-Path $d 'sbom.json') '    { "path": "mcp/Omega.Tool.exe", '; Update-TestManifest $d } },
            @{ Name = 'manifest uppercase DLL without sbom line'; Path = 'cli/Upper.Thing.DLL'; Direction = $unlined; Act = { param($d)
                Write-PackageText (Join-Path $d 'cli/Upper.Thing.DLL') 'upper'; Write-TestSbom $d $tRefs
                Remove-TestJsonLine (Join-Path $d 'sbom.json') '    { "path": "cli/Upper.Thing.DLL", '; Update-TestManifest $d } },
            @{ Name = 'manifest native without sbom name'; Path = 'cli/runtimes/linux-x64/native/libsigma.so.1'; Direction = $unlined; Act = { param($d)
                $f = Join-Path $d 'sbom.json'; $t = [IO.File]::ReadAllText($f)
                $old = '"dependency": "transitive", "assemblies": [], "natives": ["libsigma.so.1"] }'
                if (!$t.Contains($old)) { throw 'test setup: Sigma.Native natives not found' }
                Write-PackageText $f $t.Replace($old, '"dependency": "transitive", "assemblies": [], "natives": [] }'); Update-TestManifest $d } },
            @{ Name = 'manifest dylib without sbom name'; Path = 'cli/runtimes/osx-x64/native/libstray.dylib'; Direction = $unlined; Act = { param($d)
                New-Item -ItemType Directory -Force (Join-Path $d 'cli/runtimes/osx-x64/native') | Out-Null
                Write-PackageText (Join-Path $d 'cli/runtimes/osx-x64/native/libstray.dylib') 'stray'; Write-TestSbom $d $tRefs } },
            @{ Name = 'first-party native dropped from deps.json'; Path = 'cli/runtimes/linux-x64/native/libown.so'; Direction = $unlined; Act = { param($d)
                Write-PackageText (Join-Path $d 'cli/Bim.Cli.deps.json') (New-TestDeps 'Bim.Cli' @('Bim.Core') '2.1.0'); Write-TestSbom $d $tRefs } },
            @{ Name = 'manifest entry removed only'; Path = 'cli/Tau.Win.dll'; Direction = $orphaned; Act = { param($d) Remove-TestJsonLine (Join-Path $d 'manifest.json') '    { "path": "cli/Tau.Win.dll", ' } },
            @{ Name = 'manifest phantom entry only'; Path = 'cli/Phantom.dll'; Direction = $unlined; Act = { param($d)
                $f = Join-Path $d 'manifest.json'; $lines = [Collections.Generic.List[string]]([IO.File]::ReadAllText($f).Split("`n"))
                $i = $lines.FindIndex([Predicate[string]]{ param($l) $l.StartsWith('    { "path": "cli/Tau.Win.dll", ', [StringComparison]::Ordinal) })
                if ($i -lt 0 -or !$lines[$i].EndsWith(',')) { throw 'test setup: cli/Tau.Win.dll manifest entry not found' }
                $lines.Insert($i, $lines[$i].Replace('"path": "cli/Tau.Win.dll"', '"path": "cli/Phantom.dll"'))
                Write-PackageText $f ($lines -join "`n") } },
            @{ Name = 'sbom line without manifest entry'; Path = 'cli/Ghost.Thing.dll'; Direction = $orphaned; Act = { param($d)
                $f = Join-Path $d 'sbom.json'; $lines = [Collections.Generic.List[string]]([IO.File]::ReadAllText($f).Split("`n"))
                $i = $lines.FindIndex([Predicate[string]]{ param($l) $l.StartsWith('    { "path": "cli/Tau.Win.dll", ', [StringComparison]::Ordinal) })
                if ($i -lt 0) { throw 'test setup: cli/Tau.Win.dll sbom line not found' }
                $lines.Insert($i + 1, $lines[$i].Replace('"path": "cli/Tau.Win.dll", "name": "Tau.Win"', '"path": "cli/Ghost.Thing.dll", "name": "Ghost.Thing"'))
                Write-PackageText $f ($lines -join "`n"); Update-TestManifest $d } },
            @{ Name = 'binary deleted with manifest regenerated'; Path = 'cli/Delta.Lib.dll'; Direction = $orphaned; Act = { param($d) Remove-Item -LiteralPath (Join-Path $d 'cli/Delta.Lib.dll'); Update-TestManifest $d } }
        )
        function Update-TestManifest([string]$Directory) { Write-PackageText (Join-Path $Directory 'manifest.json') (New-PackageManifestText $Directory '1.2.3' ('a' * 40) $false) }
        # Removes the one array-element line of a canonical json file (sbom.json or manifest.json) starting with $Prefix.
        function Remove-TestJsonLine([string]$File, [string]$Prefix) {
            $lines = [Collections.Generic.List[string]]([IO.File]::ReadAllText($File).Split("`n"))
            $i = $lines.FindIndex([Predicate[string]]{ param($l) $l.StartsWith($Prefix, [StringComparison]::Ordinal) })
            if ($i -lt 1) { throw "test setup: no line starting '$Prefix' in $File" }
            # Removing the last array element drops the separating comma from the element before it.
            if (!$lines[$i].EndsWith(',')) {
                if (!$lines[$i - 1].EndsWith(',')) { throw "test setup: line before '$Prefix' in $File is not an array element" }
                $lines[$i - 1] = $lines[$i - 1].Substring(0, $lines[$i - 1].Length - 1)
            }
            $lines.RemoveAt($i)
            Write-PackageText $File ($lines -join "`n")
        }
        foreach ($case in $xCases) {
            $copy = Join-Path $work ('sbom manifest case ' + $case.Name)
            Copy-Item -LiteralPath $tPkg -Destination $copy -Recurse
            try { & $case.Act $copy } catch { $failures.Add("P10-15 $($case.Name) setup failed: $($_.Exception.Message)"); continue }
            $run = Invoke-Script $verifier @('-PackageDirectory', $copy)
            $want = 'sbom_manifest_mismatch: [^\r\n]*' + [regex]::Escape($case.Direction) + '(: |: [^\r\n]*, )' + [regex]::Escape($case.Path) + '(,|\r|\n|$)'
            Assert ($run.Code -ne 0 -and $run.Output -match $want) "P10-15: package-verify must refuse $($case.Name) with sbom_manifest_mismatch '$($case.Direction)' naming $($case.Path): $(Get-Tail $run.Output)"
        }
        # An sbom assemblies entry with no (or a null) path is a named sbom_invalid problem, never an empty-name orphan.
        foreach ($nameless in @(@{ Name = 'Nameless.Thing'; Path = '' }, @{ Name = 'Null.Thing'; Path = '"path": null, ' })) {
            $copy = Join-Path $work ('sbom manifest case no path ' + $nameless.Name)
            Copy-Item -LiteralPath $tPkg -Destination $copy -Recurse
            $f = Join-Path $copy 'sbom.json'; $lines = [Collections.Generic.List[string]]([IO.File]::ReadAllText($f).Split("`n"))
            $i = $lines.FindIndex([Predicate[string]]{ param($l) $l.StartsWith('    { "path": "cli/Tau.Win.dll", "name": "Tau.Win", ', [StringComparison]::Ordinal) })
            if ($i -lt 0) { $failures.Add("P10-15 no path $($nameless.Name) setup failed: cli/Tau.Win.dll sbom line not found"); continue }
            $lines.Insert($i + 1, $lines[$i].Replace('"path": "cli/Tau.Win.dll", "name": "Tau.Win"', $nameless.Path + '"name": "' + $nameless.Name + '"'))
            Write-PackageText $f ($lines -join "`n"); Update-TestManifest $copy
            $run = Invoke-Script $verifier @('-PackageDirectory', $copy)
            Assert ($run.Code -ne 0 -and $run.Output -match ('sbom_invalid: [^\r\n]*<missing path> \(name ''' + [regex]::Escape($nameless.Name) + '''\)')) "P10-15: package-verify must refuse an sbom assemblies entry without a path as sbom_invalid naming <missing path> (name '$($nameless.Name)'): $(Get-Tail $run.Output)"
            Assert ($run.Output -notmatch '(?m)without a manifest\.json entry:([^\r\n]*,)? ?(,|\r?$)') "P10-15: a pathless sbom assemblies entry must not be reported as an empty-name orphan: $(Get-Tail $run.Output)"
        }
        # P10-16: package-verify reads the first bytes of every shipped file and refuses a PE (MZ), ELF (\x7fELF) or
        # Mach-O (FEEDFACE/FEEDFACF/CAFEBABE in either byte order) file whose extension is outside the binary pattern
        # (.dll/.exe/.so[.N]/.dylib), typed sbom_binary_unclassified naming the path, so a renamed native payload cannot
        # bypass sbom attribution. CAFEBABE is also the Java class magic: a .class file is refused the same way (the
        # package ships no Java). Fixtures are a few synthetic header bytes, with a canonical sbom and re-hashed manifest.
        $magic = @{
            PE = [byte[]](0x4D, 0x5A, 0x90, 0x00); ELF = [byte[]](0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00)
            MachO32 = [byte[]](0xFE, 0xED, 0xFA, 0xCE); MachO64 = [byte[]](0xFE, 0xED, 0xFA, 0xCF); MachO32LE = [byte[]](0xCE, 0xFA, 0xED, 0xFE)
            MachO64LE = [byte[]](0xCF, 0xFA, 0xED, 0xFE); Fat = [byte[]](0xCA, 0xFE, 0xBA, 0xBE); FatLE = [byte[]](0xBE, 0xBA, 0xFE, 0xCA)
        }
        function Write-TestBytes([string]$Directory, [string]$Path, [byte[]]$Bytes) {
            $full = Join-Path $Directory $Path
            New-Item -ItemType Directory -Force (Split-Path $full) | Out-Null
            [IO.File]::WriteAllBytes($full, [byte[]]($Bytes + [Text.Encoding]::ASCII.GetBytes(' synthetic payload')))
        }
        $uCases = @(
            @{ Name = 'renamed so to bin'; Path = 'cli/runtimes/linux-x64/native/libhidden.bin'; Bytes = $magic.ELF },
            @{ Name = 'renamed dll to dat'; Path = 'cli/Hidden.Payload.dat'; Bytes = $magic.PE },
            @{ Name = 'extensionless ELF'; Path = 'mcp/createdump'; Bytes = $magic.ELF },
            @{ Name = 'uppercase extension PE'; Path = 'cli/Upper.Payload.BIN'; Bytes = $magic.PE },
            @{ Name = 'dll with suffix'; Path = 'cli/Sneaky.dll.txt'; Bytes = $magic.PE },
            @{ Name = 'Mach-O FEEDFACE'; Path = 'cli/runtimes/osx-x64/native/libmach32.bin'; Bytes = $magic.MachO32 },
            @{ Name = 'Mach-O FEEDFACF'; Path = 'cli/runtimes/osx-x64/native/libmach64.bin'; Bytes = $magic.MachO64 },
            @{ Name = 'Mach-O CEFAEDFE'; Path = 'cli/runtimes/osx-x64/native/libmach32le.dat'; Bytes = $magic.MachO32LE },
            @{ Name = 'Mach-O CFFAEDFE'; Path = 'cli/runtimes/osx-x64/native/libmach64le.dat'; Bytes = $magic.MachO64LE },
            @{ Name = 'fat CAFEBABE'; Path = 'skills/payload.json'; Bytes = $magic.Fat },
            @{ Name = 'fat BEBAFECA'; Path = 'cli/runtimes/osx-x64/native/libfatle'; Bytes = $magic.FatLE },
            @{ Name = 'Java class CAFEBABE'; Path = 'cli/Payload.class'; Bytes = $magic.Fat },
            # Deliberate: PE detection is the two-byte "MZ" signature, so text whose first two bytes are "MZ" is refused.
            @{ Name = 'text starting MZ'; Path = 'cli/readme.txt'; Bytes = [Text.Encoding]::ASCII.GetBytes('MZ is literally how this readme starts') }
        )
        foreach ($case in $uCases) {
            $copy = Join-Path $work ('sbom unclassified case ' + $case.Name)
            Copy-Item -LiteralPath $tPkg -Destination $copy -Recurse
            try { Write-TestBytes $copy $case.Path $case.Bytes; Write-TestSbom $copy $tRefs } catch { $failures.Add("P10-16 $($case.Name) setup failed: $($_.Exception.Message)"); continue }
            $run = Invoke-Script $verifier @('-PackageDirectory', $copy)
            $want = 'sbom_binary_unclassified: [^\r\n]*(: |, )' + [regex]::Escape($case.Path) + '(,|\r|\n|$)'
            Assert ($run.Code -ne 0 -and $run.Output -match $want) "P10-16: package-verify must refuse $($case.Name) with sbom_binary_unclassified naming $($case.Path): $(Get-Tail $run.Output)"
        }
        # Several unclassified payloads are all named in one problem in ordinal order (uppercase before lowercase, unlike
        # culture order); classified binaries with real magic and text that merely starts like a magic number (or is
        # shorter than it) are accepted.
        $copy = Join-Path $work 'sbom unclassified case several payloads'
        Copy-Item -LiteralPath $tPkg -Destination $copy -Recurse
        Write-TestBytes $copy 'cli/a.bin' $magic.ELF; Write-TestBytes $copy 'cli/B.bin' $magic.ELF; Write-TestBytes $copy 'mcp/b.dat' $magic.PE; Write-TestSbom $copy $tRefs
        $run = Invoke-Script $verifier @('-PackageDirectory', $copy)
        Assert ($run.Code -ne 0 -and $run.Output -match 'sbom_binary_unclassified: [^\r\n]*: cli/B\.bin, cli/a\.bin, mcp/b\.dat(\r|\n|$)') "P10-16: package-verify must name every unclassified payload in ordinal order (cli/B.bin, cli/a.bin, mcp/b.dat): $(Get-Tail $run.Output)"
        $copy = Join-Path $work 'sbom unclassified case classified'
        Copy-Item -LiteralPath $tPkg -Destination $copy -Recurse
        Write-TestBytes $copy 'cli/runtimes/linux-x64/native/libgamma.so' $magic.ELF
        Write-TestBytes $copy 'cli/runtimes/linux-x64/native/libsigma.so.1' $magic.ELF
        Write-TestBytes $copy 'cli/runtimes/win-x64/native/gamma.dll' $magic.PE
        [IO.File]::WriteAllBytes((Join-Path $copy 'cli/short.bin'), [byte[]](0x7F, 0x45))
        [IO.File]::WriteAllBytes((Join-Path $copy 'cli/empty.dat'), [byte[]]@())
        # PE detection is the two-byte "MZ" DOS signature per P10-16, so any file whose first two bytes are literally
        # "MZ" is refused even if it is text (conservative); text that only resembles a magic number is accepted.
        Write-PackageText (Join-Path $copy 'cli/notes.txt') "Mz and then MZ later: this note is plain text`n"
        Write-PackageText (Join-Path $copy 'cli/elfish.txt') "ELF without the 0x7f byte`n"
        Write-TestSbom $copy $tRefs
        $run = Invoke-Script $verifier @('-PackageDirectory', $copy)
        Assert ($run.Code -eq 0 -and $run.Output -notmatch 'sbom_binary_unclassified' -and $run.Output -notmatch 'notes\.txt|elfish\.txt|short\.bin|empty\.dat') "P10-16: classified binaries with real magic and non-binary text must not be reported unclassified: $(Get-Tail $run.Output)"
        Assert ($tClean.Output -notmatch 'sbom_binary_unclassified') "P10-16: a consistent synthetic package must not report sbom_binary_unclassified: $(Get-Tail $tClean.Output)"
        # A dangling file symlink is reported as a symlink and never opened (no PowerShell exception escapes).
        $copy = Join-Path $work 'sbom unclassified case dangling link'
        Copy-Item -LiteralPath $tPkg -Destination $copy -Recurse
        $linkMade = $false
        try { New-Item -ItemType SymbolicLink -Path (Join-Path $copy 'cli/gone.bin') -Target (Join-Path $work 'no such payload target.bin') -ErrorAction Stop | Out-Null; $linkMade = $true }
        catch { Write-Host "SKIP P10-16 dangling symlink case: symlinks cannot be created here: $($_.Exception.Message)" }
        if ($linkMade) {
            $run = Invoke-Script $verifier @('-PackageDirectory', $copy)
            Assert ($run.Code -ne 0 -and $run.Output -match 'symlink or reparse point in package: cli/gone\.bin' -and $run.Output -notmatch '(?i)exception|could not find|FullyQualifiedErrorId|sbom_binary_unreadable') "P10-16: a dangling file symlink must be refused as a symlink without opening it or a PowerShell exception: $(Get-Tail $run.Output)"
        }
        # A regular file that cannot be read is a typed sbom_binary_unreadable problem naming it, not an exception
        # (unlisted here, so no other check opens it). Needs a POSIX mode the runner user cannot read through.
        $copy = Join-Path $work 'sbom unclassified case unreadable'
        Copy-Item -LiteralPath $tPkg -Destination $copy -Recurse
        $locked = Join-Path $copy 'cli/locked.bin'
        Write-TestBytes $copy 'cli/locked.bin' $magic.ELF
        if (!$IsWindows) {
            & chmod 000 $locked
            $stillReadable = $true
            try { [IO.File]::OpenRead($locked).Dispose() } catch { $stillReadable = $false }
            if ($stillReadable) { Write-Host 'SKIP P10-16 unreadable case: a mode-000 file is still readable here (root?)' }
            else {
                try {
                    $found = Find-PackageUnclassifiedBinaries $copy
                    Assert ((@($found.Unreadable) -join ',') -ceq 'cli/locked.bin' -and @($found.Unclassified) -notcontains 'cli/locked.bin') "P10-16: Find-PackageUnclassifiedBinaries must report an unreadable file as Unreadable: unreadable '$(@($found.Unreadable) -join ',')' unclassified '$(@($found.Unclassified) -join ',')'"
                } catch { $failures.Add("P10-16: Find-PackageUnclassifiedBinaries threw on an unreadable file: $($_.Exception.Message)") }
                $run = Invoke-Script $verifier @('-PackageDirectory', $copy)
                Assert ($run.Code -ne 0 -and $run.Output -match 'sbom_binary_unreadable: [^\r\n]*: cli/locked\.bin(\r|\n|$)' -and $run.Output -notmatch '(?i)exception|FullyQualifiedErrorId') "P10-16: package-verify must report an unreadable shipped file as sbom_binary_unreadable naming it, not throw: $(Get-Tail $run.Output)"
            }
            & chmod 644 $locked
        }
        # P10-17: a manifest-listed file that cannot be read to hash it is a typed manifest_file_unreadable problem naming
        # it, never a PowerShell exception, whatever other check would also open it (.bin: the P10-16 magic scan; .json:
        # the forbidden-content scan). Needs a POSIX mode the runner user cannot read through.
        Write-Host "P10-17 pwsh $($PSVersionTable.PSVersion) on $([Runtime.InteropServices.RuntimeInformation]::OSDescription)"
        $p17Run = 0; $p17Skip = 0; $p17FifoSkip = 0
        foreach ($listed in @('cli/listed-locked.bin', 'skills/listed-locked.json', 'cli/Listed.Locked.dll')) {
            if ($IsWindows) { $p17Skip++; Write-Host "SKIP P10-17 listed unreadable $listed`: no POSIX file modes on Windows"; continue }
            $copy = Join-Path $work ('special case unreadable ' + ($listed -replace '[/.]', ' '))
            Copy-Item -LiteralPath $tPkg -Destination $copy -Recurse
            $locked = Join-Path $copy $listed
            try { Write-TestBytes $copy $listed ([Text.Encoding]::ASCII.GetBytes('listed')); Write-TestSbom $copy $tRefs } catch { $failures.Add("P10-17 listed unreadable $listed setup failed: $($_.Exception.Message)"); continue }
            & chmod 000 $locked
            $stillReadable = $true
            try { [IO.File]::OpenRead($locked).Dispose() } catch { $stillReadable = $false }
            if ($stillReadable) { $p17Skip++; Write-Host "SKIP P10-17 listed unreadable $listed`: a mode-000 file is still readable here (root?)" }
            else {
                $p17Run++
                $run = Invoke-ScriptBounded $verifier @('-PackageDirectory', $copy)
                $want = 'manifest_file_unreadable: [^\r\n]*(: |, )' + [regex]::Escape($listed) + '(,|\r|\n|$)'
                Assert (!$run.TimedOut -and $run.Code -eq 1 -and $run.Output -match $want -and $run.Output -notmatch '(?i)exception|FullyQualifiedErrorId' -and $run.Output -notmatch ('sha256 mismatch: ' + [regex]::Escape($listed))) "P10-17: package-verify must report a manifest-listed unreadable file as manifest_file_unreadable naming $listed, not throw: $(Get-Tail $run.Output)"
                # An unreadable listed .dll stops the sbom check, but no misleading sbom_* problem is added on top.
                if ($listed -like '*.dll') { Assert ($run.Output -notmatch 'sbom_') "P10-17: an unreadable listed .dll must be reported only as manifest_file_unreadable, without an sbom_* problem: $(Get-Tail $run.Output)" }
            }
            & chmod 644 $locked
        }
        # Non-regular files are refused by file type (lstat-style metadata) before anything opens them: opening a FIFO
        # for read blocks until a writer appears, so each run is bounded and a hang is a failure. Fixtures sit where each
        # existing check would open them: .bin (P10-16 magic scan), .json (forbidden-content scan), .dll (sbom hashing),
        # and a manifest-listed path (manifest hashing: a regular file listed, then replaced by a FIFO).
        $mkfifo = if (!$IsWindows) { Get-Command mkfifo -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source }
        $fifoCases = @(
            @{ Name = 'unlisted bin'; Paths = @('cli/pipe.bin'); Listed = $false },
            @{ Name = 'unlisted json'; Paths = @('skills/pipe.json'); Listed = $false },
            @{ Name = 'unlisted dll'; Paths = @('cli/Pipe.Thing.dll'); Listed = $false },
            @{ Name = 'manifest listed'; Paths = @('cli/listed-pipe.dat'); Listed = $true },
            @{ Name = 'several ordinal'; Paths = @('cli/a.pipe', 'cli/B.pipe'); Listed = $false; Expect = 'cli/B\.pipe \(fifo\), cli/a\.pipe \(fifo\)' }
        )
        foreach ($case in $fifoCases) {
            if (!$mkfifo) { $p17Skip++; $p17FifoSkip++; Write-Host "SKIP P10-17 fifo $($case.Name): mkfifo is not available here"; continue }
            $copy = Join-Path $work ('special case fifo ' + $case.Name)
            Copy-Item -LiteralPath $tPkg -Destination $copy -Recurse
            $made = [Collections.Generic.List[string]]::new()
            try {
                if ($case.Listed) { foreach ($path in $case.Paths) { Write-TestBytes $copy $path ([byte[]]@()) }; Write-TestSbom $copy $tRefs }
                foreach ($path in $case.Paths) {
                    $full = Join-Path $copy $path
                    New-Item -ItemType Directory -Force (Split-Path $full) | Out-Null
                    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Force }
                    & $mkfifo $full
                    if ($LASTEXITCODE -ne 0) { throw "mkfifo $path exited $LASTEXITCODE" }
                    $made.Add($full)
                }
            } catch { $failures.Add("P10-17 fifo $($case.Name) setup failed: $($_.Exception.Message)"); foreach ($full in $made) { Remove-Item -LiteralPath $full -Force -ErrorAction SilentlyContinue }; continue }
            if ($case.Name -ceq 'unlisted bin') {
                # The file type classifier (POSIX test via /bin/sh) recognises a FIFO without opening it; UnixMode is printed only as a diagnostic.
                $fifoItem = Get-Item -LiteralPath $made[0] -Force
                Write-Host "P10-17 fifo UnixMode '$($fifoItem.UnixMode)' UnixStat.ItemType '$($fifoItem.UnixStat.ItemType)'"
                Assert ((Get-PackageSpecialFileKind $fifoItem) -ceq 'fifo') "P10-17: Get-PackageSpecialFileKind must classify a FIFO as fifo: '$(Get-PackageSpecialFileKind $fifoItem)'"
                Assert ((Get-PackagePosixFileKind $made[0]) -ceq 'fifo') "P10-17: the POSIX fallback must classify a FIFO as fifo: '$(Get-PackagePosixFileKind $made[0])'"
                Assert ($null -eq (Get-PackagePosixFileKind (Join-Path $copy 'cli/Bim.Cli.deps.json'))) 'P10-17: the POSIX fallback must not classify a regular file as special'
            }
            $p17Run++
            $run = Invoke-ScriptBounded $verifier @('-PackageDirectory', $copy)
            $named = if ($case.Expect) { $case.Expect } else { [regex]::Escape($case.Paths[0]) + ' \(fifo\)' }
            Assert (!$run.TimedOut -and $run.Code -eq 1 -and $run.Output -match ('package_special_file: [^\r\n]*(: |, )' + $named + '(,|\r|\n|$)') -and $run.Output -notmatch '(?i)exception|FullyQualifiedErrorId') "P10-17: package-verify must refuse fifo $($case.Name) as package_special_file naming $($case.Paths -join ', ') without opening it (timed out: $($run.TimedOut)): $(Get-Tail $run.Output)"
            # Unlink the FIFOs so no later recursive traversal of the work tree can open one.
            foreach ($full in $made) { Remove-Item -LiteralPath $full -Force -ErrorAction SilentlyContinue }
        }
        # A Unix domain socket file (BCL Socket.Bind) is refused the same way; opening one fails rather than blocks.
        if ($IsWindows) { $p17Skip++; Write-Host 'SKIP P10-17 socket: Windows AF_UNIX sockets are reparse points, already refused as links' }
        else {
            $copy = Join-Path $work 'special case socket'
            Copy-Item -LiteralPath $tPkg -Destination $copy -Recurse
            $full = Join-Path $copy 'cli/sock.bin'
            $socket = [Net.Sockets.Socket]::new([Net.Sockets.AddressFamily]::Unix, [Net.Sockets.SocketType]::Stream, [Net.Sockets.ProtocolType]::Unspecified)
            try {
                # sun_path holds ~104-108 bytes and the CI work tree is deeper, so bind a path relative to the process
                # working directory (the .NET one, not $PWD), restored right after.
                $bound = $false; $savedDirectory = [Environment]::CurrentDirectory
                try { [Environment]::CurrentDirectory = (Split-Path $full); $socket.Bind([Net.Sockets.UnixDomainSocketEndPoint]::new('sock.bin')); $bound = $true }
                catch { $p17Skip++; Write-Host "SKIP P10-17 socket: cannot bind a Unix domain socket at $full`: $($_.Exception.Message)" }
                finally { [Environment]::CurrentDirectory = $savedDirectory }
                if ($bound -and !(Test-Path -LiteralPath $full)) { $failures.Add("P10-17 socket setup failed: bound socket file not at $full"); $bound = $false }
                if ($bound) {
                    $p17Run++
                    $run = Invoke-ScriptBounded $verifier @('-PackageDirectory', $copy)
                    Assert (!$run.TimedOut -and $run.Code -eq 1 -and $run.Output -match 'package_special_file: [^\r\n]*(: |, )cli/sock\.bin \(socket\)(,|\r|\n|$)' -and $run.Output -notmatch '(?i)exception|FullyQualifiedErrorId') "P10-17: package-verify must refuse a socket file as package_special_file naming cli/sock.bin (socket): $(Get-Tail $run.Output)"
                }
            } finally { $socket.Dispose(); Remove-Item -LiteralPath $full -Force -ErrorAction SilentlyContinue }
        }
        # P10-17 review: a symlink whose target is a FIFO outside the package (as a manifest-listed path, as sbom.json and
        # as manifest.json) is refused as a link and nothing is read through it: verification stops at link problems.
        $outsideFifoDir = Join-Path $work 'p17 outside fifo'
        New-Item -ItemType Directory -Force $outsideFifoDir | Out-Null
        $outsideFifo = Join-Path $outsideFifoDir 'target.fifo'
        $outsideMade = $false
        if ($mkfifo) { & $mkfifo $outsideFifo; $outsideMade = ($LASTEXITCODE -eq 0) }
        foreach ($linkCase in @(@{ Path = 'cli/listed-link.dat'; Listed = $true }, @{ Path = 'sbom.json'; Listed = $false }, @{ Path = 'manifest.json'; Listed = $false })) {
            if (!$outsideMade) { $p17Skip++; $p17FifoSkip++; Write-Host "SKIP P10-17 symlink to fifo $($linkCase.Path): mkfifo is not available here"; continue }
            $copy = Join-Path $work ('special case link to fifo ' + ($linkCase.Path -replace '[/.]', ' '))
            Copy-Item -LiteralPath $tPkg -Destination $copy -Recurse
            $linkFull = Join-Path $copy $linkCase.Path
            try {
                if ($linkCase.Listed) { Write-TestBytes $copy $linkCase.Path ([byte[]]@()); Write-TestSbom $copy $tRefs }
                if (Test-Path -LiteralPath $linkFull) { Remove-Item -LiteralPath $linkFull -Force }
                New-Item -ItemType SymbolicLink -Path $linkFull -Target $outsideFifo -ErrorAction Stop | Out-Null
            } catch { $p17Skip++; Write-Host "SKIP P10-17 symlink to fifo $($linkCase.Path): $($_.Exception.Message)"; continue }
            $p17Run++
            $run = Invoke-ScriptBounded $verifier @('-PackageDirectory', $copy)
            Assert (!$run.TimedOut -and $run.Code -eq 1 -and $run.Output -match ('symlink or reparse point in package: ' + [regex]::Escape($linkCase.Path) + '(\r|\n|$)') -and $run.Output -notmatch '(?i)exception|FullyQualifiedErrorId') "P10-17: package-verify must refuse a symlink to a FIFO at $($linkCase.Path) as a link without reading through it (timed out: $($run.TimedOut)): $(Get-Tail $run.Output)"
            Remove-Item -LiteralPath $linkFull -Force -ErrorAction SilentlyContinue
        }
        if ($outsideMade) { Remove-Item -LiteralPath $outsideFifo -Force -ErrorAction SilentlyContinue }
        # P10-17 review: a file type check that cannot run (shell missing, non-zero exit, unexpected output) is a typed
        # package_special_file_check_failed problem that stops verification, never an exception (shell injected in-process).
        if ($IsWindows) { $p17Skip++; Write-Host 'SKIP P10-17 type check failures: no POSIX type check on Windows' }
        else {
            $copy = Join-Path $work 'special case check failures'
            Copy-Item -LiteralPath $tPkg -Destination $copy -Recurse
            $falseTool = Get-Command false -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
            $echoTool = Get-Command echo -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
            $savedShell = Get-Variable -Name PackageShellPath -Scope Script -ValueOnly -ErrorAction SilentlyContinue
            foreach ($shellCase in @(@{ Name = 'missing shell'; Path = (Join-Path $work 'no such shell') }, @{ Name = 'non-zero exit'; Path = $falseTool }, @{ Name = 'unexpected output'; Path = $echoTool })) {
                if (!$shellCase.Path) { $p17Skip++; Write-Host "SKIP P10-17 type check $($shellCase.Name): tool not found"; continue }
                $p17Run++
                $script:PackageShellPath = $shellCase.Path
                try {
                    $checked = @(Test-PackageManifest $copy)
                    Assert (($checked -join "`n") -match 'package_special_file_check_failed: ' -and ($checked -join "`n") -notmatch 'sbom_|manifest_file_unreadable') "P10-17: a $($shellCase.Name) type check must be a typed package_special_file_check_failed problem that stops verification: $($checked -join ' | ')"
                } catch { $failures.Add("P10-17: Test-PackageManifest threw on a $($shellCase.Name) type check: $($_.Exception.Message)") }
                finally { $script:PackageShellPath = $savedShell }
            }
            # Batches are bounded by argv bytes as well as count (macOS ARG_MAX): 1100 paths of ~4000 bytes each.
            $longNull = '/dev/' + ('./' * 1995) + 'null'
            try {
                $many = Get-PackagePosixFileKinds ([string[]]@(1..1100 | ForEach-Object { $longNull }))
                Assert (@($many).Count -eq 1100 -and @($many | Where-Object { $_ -ceq 'character device' }).Count -eq 1100) "P10-17: the type check must classify 1100 long paths in argv-bounded batches: $(@($many | Where-Object { $_ -ceq 'character device' }).Count) of $(@($many).Count)"
            } catch { $failures.Add("P10-17: the type check threw on 1100 long paths (argv not bounded by bytes?): $($_.Exception.Message)") }
            Assert ((Get-PackagePosixFileKind '/dev/null') -ceq 'character device') "P10-17: /dev/null must classify as a character device: '$(Get-PackagePosixFileKind '/dev/null')'"
        }
        Write-Host "P10-17 unreadable/special-file cases: $p17Run run, $p17Skip skipped"
        if ($IsLinux -and $env:GITHUB_ACTIONS -eq 'true') { Assert ($p17Skip -eq 0) "P10-17: no unreadable/fifo/socket case may be skipped on the Linux CI runner (skipped: $p17Skip, fifo: $p17FifoSkip)" }
        Assert ($tClean.Output -notmatch 'package_special_file|manifest_file_unreadable') "P10-17: a consistent synthetic package must not report package_special_file or manifest_file_unreadable: $(Get-Tail $tClean.Output)"
        Assert ($tClean.Output -notmatch 'sbom_manifest_mismatch') "P10-15: a consistent synthetic package (including a first-party native named only by its deps.json project entry) must not report sbom_manifest_mismatch: $(Get-Tail $tClean.Output)"

        # Without an assets file the P10-13 behaviour is unchanged: direct references only, no recorded files.
        $refsNoAssets = @(Get-PackageNuGetReferences -Root $sbomSrc -Projects @('app/App.csproj') -PackagesRoot (Join-Path $sbomSrc 'nuget'))
        $noAssetsText = ($refsNoAssets | ForEach-Object { "$($_.Name):$($_.Dependency):$(@($_.Assemblies).Count):$(@($_.Natives).Count)" }) -join ','
        Assert ($noAssetsText -ceq 'Alpha.Json:direct:0:0,Zeta.Lib:direct:0:0') "P10-14: without project.assets.json references stay direct with no recorded files: $noAssetsText"
    } catch { $failures.Add("P10-14 transitive SBOM checks threw: $($_.Exception.Message)") }

    # --- P7-12 review: a tracked skills/ entry that is not a regular file (mode other than 100644/100755, e.g. a
    # symlink 120000 or a submodule 160000) is refused before any build; the index entry is staged with --cacheinfo so
    # no file system symlink is needed on any OS ---
    try {
        $modeRepo = Join-Path $work 'skills mode repo'
        foreach ($name in 'scripts/package.ps1', 'scripts/package-common.ps1', 'scripts/package.version') {
            $target = Join-Path $modeRepo $name
            New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
            Copy-Item -LiteralPath (Join-Path $root $name) -Destination $target
        }
        foreach ($name in 'Directory.Build.props', 'global.json', '.editorconfig', 'interfaces/cli/Placeholder.cs', 'skills/zz.ok.pack/skill.json') {
            $target = Join-Path $modeRepo $name
            New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
            [IO.File]::WriteAllText($target, "x`n")
        }
        $ErrorActionPreference = 'Continue'
        & git -C $modeRepo init -q 2>&1 | Out-Null
        & git -C $modeRepo add -A 2>&1 | Out-Null
        $blob = [string](& git -C $modeRepo hash-object -w (Join-Path $modeRepo 'skills/zz.ok.pack/skill.json'))
        & git -C $modeRepo update-index --add --cacheinfo "120000,$($blob.Trim()),skills/zz.link.pack/skill.json" 2>&1 | Out-Null
        & git -C $modeRepo -c user.name=test -c user.email=test@example.invalid -c commit.gpgsign=false commit -q -m fixture 2>&1 | Out-Null
        $staged = [string]((& git -C $modeRepo ls-files -s -- skills/zz.link.pack/skill.json) -join ' ')
        $ErrorActionPreference = 'Stop'
        Assert ($staged.StartsWith('120000 ', [StringComparison]::Ordinal)) "skills mode fixture must stage a 120000 entry: '$staged'"
        $modeOut = Join-Path $work 'skills mode out'
        $modeRun = Invoke-Script (Join-Path $modeRepo 'scripts/package.ps1') @('-OutputDirectory', $modeOut, '-Dotnet', $Dotnet)
        Assert ($modeRun.Code -ne 0 -and $modeRun.Output -match 'skills_entry_not_regular' -and $modeRun.Output -match [regex]::Escape('skills/zz.link.pack/skill.json') -and !(Test-Path -LiteralPath $modeOut)) "package.ps1 must refuse a tracked skills/ symlink entry (skills_entry_not_regular, no output): exit $($modeRun.Code) $(Get-Tail $modeRun.Output)"
    } catch { $failures.Add("P7-12 skills mode checks threw: $($_.Exception.Message)") }

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
            # P10-14: the real package ships only first-party Bim.* assemblies today; they stay NOASSERTION with no package
            # attribution, so the P10-13 assembly line format is unchanged.
            foreach ($component in @($sbom.assemblies)) {
                Assert ([string]$component.name -cmatch '\ABim\.' -and [string]$component.license -ceq 'NOASSERTION' -and !$component.PSObject.Properties['package']) "P10-14: real package assembly $($component.path) must be first-party Bim.* with NOASSERTION and no package (got name '$($component.name)' licence '$($component.license)')"
            }
            Assert ($sbomRefs.Count -eq 0) "P10-14: the real package has no NuGet references today: [$(($sbomRefs | ForEach-Object name) -join ', ')]"
        } catch { $failures.Add("sbom.json checks threw: $($_.Exception.Message)") }
    }

    # --- The packaged CLI runs from the release layout ---
    $ErrorActionPreference = 'Continue'
    $cliOut = & $Dotnet (Join-Path $outA 'cli/Bim.Cli.dll') '--help' 2>&1 | ForEach-Object { "$_" }
    $cliCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    # An unknown command makes the CLI answer with its JSON failure envelope, which proves the runtime loaded.
    Assert ($cliCode -eq 1 -and (($cliOut -join "`n") -match '"status":"failed"')) "packaged CLI did not start from the release layout (exit $cliCode): $(Get-Tail ($cliOut -join "`n"))"

    # --- P7-12 skills/ ships next to cli/ (every tracked skills file, byte-identical, listed in manifest.json) and the
    # packaged generate-prototypes reads its pack from there, outside any repository, byte-equal to the repository run ---
    try {
        $ErrorActionPreference = 'Continue'
        $skillsRaw = (& git -C $root ls-files -z -- skills) -join "`n"
        $ErrorActionPreference = 'Stop'
        $trackedSkills = [string[]]@($skillsRaw.Split([char]0) | Where-Object { $_ })
        [Array]::Sort($trackedSkills, [StringComparer]::Ordinal)
        $packagedSkills = [string[]]@($paths | Where-Object { $_.StartsWith('skills/', [StringComparison]::Ordinal) })
        Assert ($trackedSkills.Count -gt 0 -and $trackedSkills -ccontains 'skills/ar.caba.codigo-edificacion/skill.json') "repository skills/ must be tracked (git ls-files skills): [$($trackedSkills -join ', ')]"
        Assert (($packagedSkills -join "`n") -ceq ($trackedSkills -join "`n")) "package skills/ must be exactly the tracked skills files, listed in manifest.json: package=[$($packagedSkills -join ', ')] tracked=[$($trackedSkills -join ', ')]"
        foreach ($skill in $trackedSkills) {
            $packagedFile = Join-Path $outA $skill
            if (Test-Path -LiteralPath $packagedFile -PathType Leaf) {
                Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($packagedFile)) -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $root $skill)))) "packaged $skill must be byte-identical to the repository file"
            }
        }

        function Test-UnderRepository([string]$Directory) {
            for ($dir = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($Directory)); $dir; $dir = $dir.Parent) { if (Test-Path -LiteralPath (Join-Path $dir.FullName 'global.json') -PathType Leaf) { return $true } }
            return $false
        }
        $outsideBase = @($env:RUNNER_TEMP, [IO.Path]::GetTempPath(), [Environment]::GetFolderPath('LocalApplicationData'), [Environment]::GetFolderPath('UserProfile'), $(if (!$IsWindows) { '/tmp' })) |
            Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Container) -and !(Test-UnderRepository $_) } | Select-Object -First 1
        if (!$outsideBase) { Write-Host 'SKIP packaging generate-prototypes: no writable directory outside a repository for the package copy' }
        else {
            $outsidePackage = Join-Path $outsideBase ('bim-package-generate-' + [Guid]::NewGuid().ToString('N'))
            try {
                Copy-Item -LiteralPath $outA -Destination $outsidePackage -Recurse
                $request = Join-Path $work 'generate-request.json'
                Write-PackageText $request '{"typology":2,"lot":{"width_m":8.66,"depth_m":20},"alternatives":2,"seed":0}'
                function Invoke-Generate([string]$Dll, [string]$Output) {
                    $ErrorActionPreference = 'Continue'
                    $text = & $Dotnet $Dll 'generate-prototypes' $request $Output 2>&1 | ForEach-Object { "$_" }
                    return [pscustomobject]@{ Code = $LASTEXITCODE; Output = ($text -join "`n") }
                }
                $packagedOut = Join-Path $work 'generate-packaged.json'
                $packaged = Invoke-Generate (Join-Path $outsidePackage 'cli/Bim.Cli.dll') $packagedOut
                Assert ($packaged.Code -eq 0 -and (Test-Path -LiteralPath $packagedOut -PathType Leaf) -and $packaged.Output -match '"status":"succeeded"') "packaged generate-prototypes outside a repository must read skills/<pack>/skill.json from the package (exit $($packaged.Code)): $(Get-Tail $packaged.Output)"
                $repoCli = Join-Path $root 'interfaces/cli/bin/Release/net8.0/Bim.Cli.dll'
                if (!(Test-Path -LiteralPath $repoCli -PathType Leaf)) {
                    # On CI (RUNNER_TEMP set) scripts/verify.ps1 always builds the Release CLI first: missing is a failure there.
                    if ($env:RUNNER_TEMP) { $failures.Add("packaging generate-prototypes byte-equality: repository Release CLI not built on CI ($repoCli)") }
                    else { Write-Host "SKIP packaging generate-prototypes byte-equality: repository CLI not built ($repoCli)" }
                }
                else {
                    $repoOut = Join-Path $work 'generate-repository.json'
                    $repoRun = Invoke-Generate $repoCli $repoOut
                    Assert ($repoRun.Code -eq 0 -and (Test-Path -LiteralPath $repoOut -PathType Leaf)) "repository generate-prototypes failed (exit $($repoRun.Code)): $(Get-Tail $repoRun.Output)"
                    if ((Test-Path -LiteralPath $packagedOut -PathType Leaf) -and (Test-Path -LiteralPath $repoOut -PathType Leaf)) {
                        Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($packagedOut)) -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes($repoOut)) -and $packaged.Output -ceq $repoRun.Output) "packaged generate-prototypes must be byte-equal to the repository run (file and stdout): packaged '$(Get-Tail $packaged.Output)' repository '$(Get-Tail $repoRun.Output)'"
                    }
                }
                Remove-Item -LiteralPath (Join-Path $outsidePackage 'skills') -Recurse -Force -ErrorAction SilentlyContinue
                $noSkillsOut = Join-Path $work 'generate-no-skills.json'
                $noSkills = Invoke-Generate (Join-Path $outsidePackage 'cli/Bim.Cli.dll') $noSkillsOut
                Assert ($noSkills.Code -eq 1 -and $noSkills.Output -match '"code":"generate_prototypes_repository_not_found"' -and !(Test-Path -LiteralPath $noSkillsOut)) "a package without skills/ outside a repository must refuse generate_prototypes_repository_not_found (exit $($noSkills.Code)): $(Get-Tail $noSkills.Output)"
            } finally { if (Test-Path -LiteralPath $outsidePackage) { Remove-Item -LiteralPath $outsidePackage -Recurse -Force -ErrorAction SilentlyContinue } }
        }
    } catch { $failures.Add("P7-12 packaged skills/generate-prototypes checks threw: $($_.Exception.Message)") }

    # --- Verifier: clean package passes; tamper, missing, extra and forbidden files are detected ---
    $ok = Invoke-Script $verifier @('-PackageDirectory', $outA)
    Assert ($ok.Code -eq 0) "package-verify rejected a clean package: $(Get-Tail $ok.Output)"
    Assert ($ok.Output -notmatch 'sbom_manifest_mismatch') "P10-15: the real package's manifest.json and sbom.json must agree on every shipped binary: $(Get-Tail $ok.Output)"
    Assert ($ok.Output -notmatch 'sbom_binary_unclassified') "P10-16: the real package must ship no PE/ELF/Mach-O payload outside the binary pattern: $(Get-Tail $ok.Output)"
    try {
        Assert (Test-PackageExecutableMagic (Join-Path $outA 'cli/Bim.Cli.dll')) 'P10-16: the real cli/Bim.Cli.dll must be detected as an executable image (MZ)'
        Assert (!(Test-PackageExecutableMagic (Join-Path $outA 'cli/Bim.Cli.runtimeconfig.json'))) 'P10-16: the real cli/Bim.Cli.runtimeconfig.json must not be detected as an executable image'
    } catch { $failures.Add("P10-16 real-package magic checks threw: $($_.Exception.Message)") }

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
        @{ Name = 'sbom build-only rehashed'; Expect = 'sbom_build_only'; Act = { param($d) Add-CaseSbomReference $d ([pscustomobject]@{ Name = 'Nice3point.Revit.Api.RevitAPI'; Version = '2025.0.2'; License = 'MIT'; Projects = [string[]]@('runtimes/revit-native/Bim.Revit.csproj') }); Update-CaseManifest $d } },
        @{ Name = 'sbom unlisted assembly'; Expect = 'sbom_mismatch'; Act = { param($d) Copy-Item -LiteralPath (Join-Path $d 'cli/Bim.Core.dll') -Destination (Join-Path $d 'cli/Extra.Component.dll'); Update-CaseManifest $d } },
        # P10-15: a shipped binary in manifest.json without its sbom.json line, and an sbom.json line whose binary has no
        # manifest.json entry, are refused (sbom_manifest_mismatch naming the path) even with a re-hashed manifest.
        @{ Name = 'sbom line dropped rehashed'; Expect = 'sbom_manifest_mismatch'; Direction = 'without an sbom.json line'; Path = 'cli/Bim.Core.dll'; Act = { param($d) Edit-CaseSbom $d { param($t) $t -replace '(?m)^    \{ "path": "cli/Bim\.Core\.dll", [^\n]*,\n', '' }; Update-CaseManifest $d } },
        # P10-16: a real shipped assembly renamed outside the binary pattern, with a re-hashed manifest, is refused.
        @{ Name = 'real dll renamed to dat rehashed'; Expect = 'sbom_binary_unclassified'; Direction = 'so sbom attribution cannot cover them'; Path = 'cli/Bim.Gltf.dat'; Act = { param($d) Move-Item -LiteralPath (Join-Path $d 'cli/Bim.Gltf.dll') -Destination (Join-Path $d 'cli/Bim.Gltf.dat'); Update-CaseManifest $d } },
        @{ Name = 'manifest entry dropped rehashed'; Expect = 'sbom_manifest_mismatch'; Direction = 'without a manifest.json entry'; Path = 'cli/Bim.Gltf.dll'; Act = { param($d) Remove-Item -LiteralPath (Join-Path $d 'cli/Bim.Gltf.dll'); Update-CaseManifest $d } }
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
    # Injects a reference through a parsed edit and re-renders canonically, so it holds whatever references are shipped.
    function Add-CaseSbomReference([string]$Directory, $Reference) {
        $f = Join-Path $Directory 'sbom.json'
        $parsed = [IO.File]::ReadAllText($f) | ConvertFrom-Json
        $list = [Collections.Generic.List[object]]::new()
        foreach ($ref in @($parsed.nuget_references | Where-Object { $null -ne $_ })) { $list.Add([pscustomobject]@{ Name = [string]$ref.name; Version = [string]$ref.version; License = [string]$ref.license; Projects = [string[]]@($ref.projects) }) }
        $list.Add($Reference)
        $sorted = [object[]]@($list | Sort-Object -Property @{ Expression = { [string]$_.Name + "`0" + [string]$_.Version } } -CaseSensitive -Culture ([Globalization.CultureInfo]::InvariantCulture))
        Write-PackageText $f (New-PackageSbomText $Directory ([string]$parsed.version) ([string]$parsed.source_commit) $sorted)
        if (!([IO.File]::ReadAllText($f).Contains([string]$Reference.Name))) { throw "test setup: $($Reference.Name) was not injected into $f" }
    }
    foreach ($case in $cases) {
        $copy = Join-Path $work ('case ' + $case.Name)
        Copy-Item -LiteralPath $outA -Destination $copy -Recurse
        try { & $case.Act $copy } catch { $failures.Add("package-verify $($case.Name) setup failed: $($_.Exception.Message)"); continue }
        $run = Invoke-Script $verifier @('-PackageDirectory', $copy)
        Assert ($run.Code -ne 0) "package-verify accepted a $($case.Name) package"
        Assert ($run.Output -match [regex]::Escape($case.Expect)) "package-verify $($case.Name) must report '$($case.Expect)': $(Get-Tail $run.Output)"
        if ($case.ContainsKey('Path')) { Assert ($run.Output -match ([regex]::Escape($case.Expect) + ': [^\r\n]*' + [regex]::Escape($case.Direction) + '(: |: [^\r\n]*, )' + [regex]::Escape($case.Path) + '(,|\r|\n|$)')) "package-verify $($case.Name) must name $($case.Path) '$($case.Direction)' in its '$($case.Expect)' problem: $(Get-Tail $run.Output)" }
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

    # --- P10-18: a temp directory reached through a symlink (macOS TMPDIR /var/folders/... with /var -> /private/var)
    # still packages its own Bim.* project references as first-party: exit 0 and the clean build's manifest/sbom bytes ---
    if ($IsWindows) { Write-Host 'SKIP P10-18 symlinked temp: directory symlinks need privileges on Windows (temp is not symlinked there)' } else {
        $physicalTemp = Join-Path $work 'physical temp'
        $linkedTemp = Join-Path $work 'linked temp'
        New-Item -ItemType Directory -Force $physicalTemp | Out-Null
        New-Item -ItemType SymbolicLink -Path $linkedTemp -Target $physicalTemp | Out-Null
        $outD = Join-Path $work 'release D'
        $runD = Invoke-ScriptWithTemp $linkedTemp $packager @('-OutputDirectory', $outD, '-Dotnet', $Dotnet)
        Assert ($runD.Code -eq 0) "P10-18: package build with TMPDIR behind a symlink exit $($runD.Code): $(Get-Tail $runD.Output)"
        if ($runD.Code -eq 0) {
            Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $outD 'manifest.json'))) -eq [Convert]::ToBase64String($bytesA)) 'P10-18: a symlinked temp directory changed the package (manifest differs from clean build)'
            Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $outD 'sbom.json'))) -eq [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $outA 'sbom.json')))) 'P10-18: a symlinked temp directory changed sbom.json'
        }
        Assert ((Get-BuildTrees $physicalTemp).Count -eq 0) 'P10-18: package under a symlinked temp left a bim-package-src-* build tree'
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
