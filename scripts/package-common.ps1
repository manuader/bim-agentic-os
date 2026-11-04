#Requires -Version 7.2
# P10-04 shared packaging helpers, dot-sourced by package.ps1, package-verify.ps1 and tests/packaging.
# Pure functions over a directory: version resolution, forbidden-content scan, manifest render and verification.

$script:PackageManifestSchema = 'bim-agentic-os/package-manifest/v1'
$script:PackageVersionPattern = '\A[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z][0-9A-Za-z.-]*)?\z'

function Get-PackageCommit([string]$Root) {
    $ErrorActionPreference = 'Continue'
    $sha = & git -C $Root rev-parse HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]$sha -notmatch '\A[0-9a-f]{40}\z') { return $null }
    return [string]$sha
}

# Single version source: scripts/package.version. Without it, fall back to 0.0.0-g<12-hex commit>.
function Resolve-PackageVersion([string]$VersionFile, [string]$Root) {
    if (Test-Path -LiteralPath $VersionFile -PathType Leaf) {
        $version = ([IO.File]::ReadAllText($VersionFile)).Trim()
        if ($version -notmatch $script:PackageVersionPattern) { throw "invalid package version in ${VersionFile}: '$version' (expected MAJOR.MINOR.PATCH[-prerelease])" }
        return $version
    }
    $sha = Get-PackageCommit $Root
    if (!$sha) { throw "version file $VersionFile missing and no git commit available; refusing to stamp an unknown version" }
    return '0.0.0-g' + $sha.Substring(0, 12)
}

function Get-PackageRelativePath([string]$Base, [string]$Path) {
    return [IO.Path]::GetRelativePath($Base, $Path).Replace('\', '/')
}

# Relative paths of every file in the package except the root manifest, sorted ordinally.
function Get-PackageFiles([string]$Directory) {
    $base = [IO.Path]::GetFullPath($Directory)
    $paths = [Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $base -Recurse -File -Force) {
        $relative = Get-PackageRelativePath $base $file.FullName
        if ($relative -cne 'manifest.json') { $paths.Add($relative) }
    }
    $array = $paths.ToArray()
    [Array]::Sort($array, [StringComparer]::Ordinal)
    return $array
}

# Revit/Autodesk assemblies (including Nice3point reference packages), Revit models and credential material are
# never packaged. Names are matched case-insensitively; small text files are also scanned for key material.
function Find-PackageForbidden([string]$Directory) {
    $base = [IO.Path]::GetFullPath($Directory)
    $hits = [Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $base -Recurse -File -Force) {
        $name = $file.Name
        $forbidden = $name -match '(?i)^(Revit[^\\/]*|AdWindows|UIFramework[^\\/]*|Autodesk[^\\/]*|AdskRcConfig[^\\/]*)\.dll$' -or
            $name -match '(?i)^Nice3point' -or
            $name -match '(?i)\.(rvt|rfa|rte|rft|pfx|p12|pem|key|snk|jks|keystore)$' -or
            $name -match '(?i)^(\.env(\..*)?|id_(rsa|dsa|ecdsa|ed25519)(\.pub)?|\.npmrc|\.netrc|\.pypirc)$'
        if (!$forbidden -and $file.Length -le 4MB -and $file.Extension -match '(?i)^\.(json|txt|xml|config|template|addin|md|ini|yml|yaml|props|targets)$') {
            $text = [IO.File]::ReadAllText($file.FullName)
            $forbidden = $text -match '-----BEGIN ([A-Z]+ )?PRIVATE KEY-----|sk-proj-[A-Za-z0-9_-]{20,}|ghp_[A-Za-z0-9]{30,}|AKIA[0-9A-Z]{16}'
        }
        if ($forbidden) { $hits.Add((Get-PackageRelativePath $base $file.FullName)) }
    }
    $array = $hits.ToArray()
    [Array]::Sort($array, [StringComparer]::Ordinal)
    return $array
}

function ConvertTo-PackageJsonString([string]$Value) { return [Text.Json.JsonSerializer]::Serialize($Value) }

# Canonical manifest text: fixed key order, LF endings, ordinal path order, lowercase hex, trailing LF.
function New-PackageManifestText([string]$Directory, [string]$Version, [string]$Commit, [bool]$Dirty) {
    $base = [IO.Path]::GetFullPath($Directory)
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('{')
    $lines.Add('  "schema": ' + (ConvertTo-PackageJsonString $script:PackageManifestSchema) + ',')
    $lines.Add('  "version": ' + (ConvertTo-PackageJsonString $Version) + ',')
    $lines.Add('  "source_commit": ' + (ConvertTo-PackageJsonString $Commit) + ',')
    $lines.Add('  "source_dirty": ' + $(if ($Dirty) { 'true' } else { 'false' }) + ',')
    $lines.Add('  "files": [')
    $files = [string[]]@(Get-PackageFiles $base)
    for ($i = 0; $i -lt $files.Length; $i++) {
        $full = Join-Path $base $files[$i]
        $size = (Get-Item -LiteralPath $full).Length
        $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
        $comma = if ($i -lt $files.Length - 1) { ',' } else { '' }
        $lines.Add('    { "path": ' + (ConvertTo-PackageJsonString $files[$i]) + ', "size": ' + $size.ToString([Globalization.CultureInfo]::InvariantCulture) + ', "sha256": "' + $hash + '" }' + $comma)
    }
    $lines.Add('  ]')
    $lines.Add('}')
    return ($lines -join "`n") + "`n"
}

function Write-PackageText([string]$Path, [string]$Text) {
    [IO.File]::WriteAllBytes($Path, [Text.UTF8Encoding]::new($false).GetBytes($Text))
}

# Returns a list of problems; empty means the directory matches its manifest exactly.
function Test-PackageManifest([string]$Directory) {
    $base = [IO.Path]::GetFullPath($Directory)
    $problems = [Collections.Generic.List[string]]::new()
    foreach ($hit in (Find-PackageForbidden $base)) { $problems.Add("forbidden file in package: $hit") }
    $manifestPath = Join-Path $base 'manifest.json'
    if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { $problems.Add('manifest.json missing'); return $problems.ToArray() }
    try { $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json -ErrorAction Stop }
    catch { $problems.Add("manifest.json unreadable: $($_.Exception.Message)"); return $problems.ToArray() }
    if ($manifest.schema -cne $script:PackageManifestSchema) { $problems.Add("manifest schema '$($manifest.schema)' is not $script:PackageManifestSchema") }
    if ([string]$manifest.version -notmatch $script:PackageVersionPattern) { $problems.Add("manifest version '$($manifest.version)' is invalid") }
    $versionPath = Join-Path $base 'VERSION'
    if (!(Test-Path -LiteralPath $versionPath -PathType Leaf) -or [IO.File]::ReadAllText($versionPath) -cne ([string]$manifest.version + "`n")) {
        $problems.Add("VERSION file does not match manifest version '$($manifest.version)'")
    }
    $listed = [Collections.Generic.List[string]]::new()
    foreach ($entry in @($manifest.files)) {
        $path = [string]$entry.path
        if (!$path -or $path -match '(^|/)\.\.(/|$)|^/|\\|^[A-Za-z]:' ) { $problems.Add("manifest path not relative: '$path'"); continue }
        $listed.Add($path)
        $full = Join-Path $base $path
        if (!(Test-Path -LiteralPath $full -PathType Leaf)) { $problems.Add("missing file listed in manifest: $path"); continue }
        if ((Get-Item -LiteralPath $full).Length -ne [long]$entry.size) { $problems.Add("size mismatch: $path") }
        if ((Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant() -cne [string]$entry.sha256) { $problems.Add("sha256 mismatch: $path") }
    }
    $sorted = $listed.ToArray(); [Array]::Sort($sorted, [StringComparer]::Ordinal)
    if (($sorted -join "`n") -cne ($listed -join "`n")) { $problems.Add('manifest files are not sorted ordinally') }
    $set = [Collections.Generic.HashSet[string]]::new([string[]]$listed.ToArray(), [StringComparer]::Ordinal)
    foreach ($path in @(Get-PackageFiles $base)) { if (!$set.Contains($path)) { $problems.Add("unlisted file not in manifest: $path") } }
    return $problems.ToArray()
}
