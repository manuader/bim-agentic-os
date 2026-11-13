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

# Symlinks, junctions and other reparse points (files or directories, including the root) are never packaged:
# Get-ChildItem -Recurse neither descends into directory links nor distinguishes file links from their targets.
function Find-PackageLinks([string]$Directory) {
    $base = [IO.Path]::GetFullPath($Directory)
    $hits = [Collections.Generic.List[string]]::new()
    $rootItem = Get-Item -LiteralPath $base -Force
    if ($rootItem.LinkType -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) { $hits.Add('.') }
    foreach ($item in Get-ChildItem -LiteralPath $base -Recurse -Force) {
        if ($item.LinkType -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { $hits.Add((Get-PackageRelativePath $base $item.FullName)) }
    }
    $array = $hits.ToArray()
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
        $isLink = $file.LinkType -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)
        if (!$forbidden -and !$isLink -and $file.Length -le 4MB -and $file.Extension -match '(?i)^\.(json|txt|xml|config|template|addin|md|ini|yml|yaml|props|targets)$') {
            # P10-17: a file that cannot be read is not scanned here; the manifest check reports it (manifest_file_unreadable).
            try { $text = [IO.File]::ReadAllText($file.FullName) } catch { if (Test-PackageReadFailure $_) { continue } else { throw } }
            $forbidden = $text -match '-----BEGIN ([A-Z]+ )?PRIVATE KEY-----|sk-proj-[A-Za-z0-9_-]{20,}|ghp_[A-Za-z0-9]{30,}|AKIA[0-9A-Z]{16}'
        }
        if ($forbidden) { $hits.Add((Get-PackageRelativePath $base $file.FullName)) }
    }
    $array = $hits.ToArray()
    [Array]::Sort($array, [StringComparer]::Ordinal)
    return $array
}

# P10-17: true when an error record is an I/O or access failure reading a file (unwrapping .NET method-call wrappers).
function Test-PackageReadFailure($ErrorRecord) {
    $exception = $ErrorRecord.Exception
    while ($exception -is [Management.Automation.MethodInvocationException] -and $exception.InnerException) { $exception = $exception.InnerException }
    return ($exception -is [IO.IOException] -or $exception -is [UnauthorizedAccessException])
}

# P10-17: non-regular files (FIFO, socket, character or block device) are never packaged. They are recognised by file
# type from metadata alone, never by opening them: opening a FIFO for read blocks until a writer appears. The type comes
# from POSIX `test -p/-S/-c/-b` run by /bin/sh (stat only, symlinks already excluded), one shell per batch of paths;
# PowerShell's UnixMode/UnixStat is not relied on (on the Ubuntu CI runner it did not classify a FIFO). Windows has no such
# file system entries (its AF_UNIX sockets are reparse points, refused by Find-PackageLinks), so nothing is special there.
# The type is checked when verification runs; a file swapped for a FIFO after that check (TOCTOU) is not covered.
# $script:PackageShellPath is the shell used (tests inject failing ones). Batches are bounded by count and by argv bytes
# (~128 KB, well under macOS ARG_MAX). A check that cannot run (shell missing, non-zero exit, unexpected output) throws.
# Returns one kind per input path ('fifo', 'socket', 'character device', 'block device' or $null), in input order.
$script:PackageShellPath = '/bin/sh'
function Get-PackagePosixFileKinds([string[]]$Paths) {
    $kinds = [object[]]::new($Paths.Length)
    if ($IsWindows -or $Paths.Length -eq 0) { return ,$kinds }
    $shell = 'i=0; for f in "$@"; do i=$((i+1)); if [ -p "$f" ]; then echo "$i fifo"; elif [ -S "$f" ]; then echo "$i socket"; elif [ -c "$f" ]; then echo "$i character device"; elif [ -b "$f" ]; then echo "$i block device"; fi; done'
    for ($offset = 0; $offset -lt $Paths.Length; $offset += $count) {
        $count = 0; $bytes = 0
        while ($offset + $count -lt $Paths.Length -and $count -lt 500) {
            $size = [Text.Encoding]::UTF8.GetByteCount($Paths[$offset + $count]) + 1
            if ($count -gt 0 -and $bytes + $size -gt 128KB) { break }
            $bytes += $size; $count++
        }
        $info = [Diagnostics.ProcessStartInfo]::new($script:PackageShellPath)
        foreach ($argument in @('-c', $shell, 'sh') + @($Paths[$offset..($offset + $count - 1)])) { $info.ArgumentList.Add($argument) }
        $info.UseShellExecute = $false; $info.RedirectStandardOutput = $true
        $process = [Diagnostics.Process]::Start($info)
        try { $output = $process.StandardOutput.ReadToEnd(); $process.WaitForExit(); $code = $process.ExitCode } finally { $process.Dispose() }
        if ($code -ne 0) { throw "package file type check ($script:PackageShellPath test) exited $code" }
        foreach ($line in $output.Split("`n", [StringSplitOptions]::RemoveEmptyEntries)) {
            if ($line -cmatch '\A([0-9]+) (fifo|socket|character device|block device)\z') { $kinds[$offset + [int]$Matches[1] - 1] = $Matches[2] }
            else { throw "package file type check ($script:PackageShellPath test) printed an unexpected line: $(if ($line.Length -gt 200) { $line.Substring(0, 200) } else { $line })" }
        }
    }
    return ,$kinds
}
function Get-PackagePosixFileKind([string]$Path) { return (Get-PackagePosixFileKinds @($Path))[0] }
function Get-PackageSpecialFileKind($Item) { return Get-PackagePosixFileKind $Item.FullName }
# Package-relative path -> kind of every non-regular, non-link file under the package (ordinal keys).
function Get-PackageSpecialFileMap([string]$Directory) {
    $base = [IO.Path]::GetFullPath($Directory)
    $map = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    if ($IsWindows) { return ,$map }
    $full = [Collections.Generic.List[string]]::new()
    foreach ($item in Get-ChildItem -LiteralPath $base -Recurse -Force) {
        if ($item.PSIsContainer -or $item.LinkType -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
        $full.Add($item.FullName)
    }
    $kinds = Get-PackagePosixFileKinds $full.ToArray()
    for ($i = 0; $i -lt $full.Count; $i++) { if ($kinds[$i]) { $map[(Get-PackageRelativePath $base $full[$i])] = [string]$kinds[$i] } }
    return ,$map
}
# Package-relative paths of non-regular files with their kind ("cli/x.bin (fifo)"), in ordinal path order.
function Find-PackageSpecialFiles([string]$Directory, $Map = $null) {
    $map = if ($null -ne $Map) { $Map } else { Get-PackageSpecialFileMap $Directory }
    $paths = [string[]]@($map.Keys); [Array]::Sort($paths, [StringComparer]::Ordinal)
    return [string[]]@($paths | Where-Object { $_ } | ForEach-Object { "$_ ($($map[$_]))" })
}
# Lowercase hex SHA-256 of a file (same value as Get-FileHash), throwing the underlying .NET I/O exception on failure.
function Get-PackageFileSha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() } finally { $sha.Dispose() }
    } finally { $stream.Dispose() }
}

# ConvertTo-Json on a single string yields its quoted, escaped JSON form (generic JsonSerializer overloads do not bind).
function ConvertTo-PackageJsonString([string]$Value) { return (ConvertTo-Json -InputObject $Value -Compress) }

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

# --- P10-13 release SBOM (sbom.json, listed in manifest.json like any other file) ---
$script:PackageSbomSchema = 'bim-agentic-os/package-sbom/v1'
$script:PackageSpdxPattern = '\A[A-Za-z0-9.+()-]+( (AND|OR|WITH) [A-Za-z0-9.+()-]+)*\z'
# Vendor reference packages (Nice3point Revit API, Autodesk) are build-only by policy: never listed as shipped.
$script:PackageVendorPattern = '(?i)^(Nice3point|Revit|Autodesk|AdWindows|UIFramework)'

# MSBuild item metadata names are case-insensitive, as attributes (Version="..") or child elements (<Version>..</Version>).
function Get-PackageXmlValue($Element, [string]$Name) {
    foreach ($attribute in $Element.Attributes) { if ($attribute.LocalName -ieq $Name -and $attribute.Value) { return $attribute.Value } }
    foreach ($child in $Element.ChildNodes) { if ($child.NodeType -eq 'Element' -and $child.LocalName -ieq $Name) { return $child.InnerText.Trim() } }
    return $null
}
# NuGet normalised version (as used for global-packages folders): '+metadata' dropped, numeric parts without leading
# zeros, at least three parts and a fourth part only when non-zero (1.0 -> 1.0.0, 1.2.3.0 -> 1.2.3). $null if not a version.
function ConvertTo-PackageNuGetVersion([string]$Version) {
    $core = $Version.Split('+')[0]
    $dash = $core.IndexOf('-')
    $release = if ($dash -ge 0) { $core.Substring(0, $dash) } else { $core }
    $prerelease = if ($dash -ge 0) { $core.Substring($dash) } else { '' }
    if ($release -cnotmatch '\A[0-9]+(\.[0-9]+){0,3}\z' -or ($prerelease -and $prerelease -cnotmatch '\A-[0-9A-Za-z][0-9A-Za-z.-]*\z')) { return $null }
    $parts = [Collections.Generic.List[string]]::new()
    foreach ($part in $release.Split('.')) { $trimmed = $part.TrimStart('0'); $parts.Add($(if ($trimmed) { $trimmed } else { '0' })) }
    while ($parts.Count -lt 3) { $parts.Add('0') }
    if ($parts.Count -eq 4 -and $parts[3] -ceq '0') { $parts.RemoveAt(3) }
    return ($parts -join '.') + $prerelease
}
function Get-PackageAssetTokens([string]$Value) { return @($Value.Split(';') | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ }) }
# A reference ships runtime assets unless it excludes runtime/all or names IncludeAssets without runtime/all.
function Test-PackageReferenceShipped($Element) {
    $exclude = Get-PackageXmlValue $Element 'ExcludeAssets'
    if ($exclude) { $tokens = Get-PackageAssetTokens $exclude; if ($tokens -contains 'runtime' -or $tokens -contains 'all') { return $false } }
    $include = Get-PackageXmlValue $Element 'IncludeAssets'
    if ($null -ne $include) { $tokens = Get-PackageAssetTokens $include; if ($tokens -notcontains 'runtime' -and $tokens -notcontains 'all') { return $false } }
    return $true
}
function Read-PackageNuspecLicense([string]$PackagesRoot, [string]$Name, [string]$Version) {
    if (!$PackagesRoot) { return 'NOASSERTION' }
    $id = $Name.ToLowerInvariant()
    $nuspec = Join-Path $PackagesRoot (Join-Path $id (Join-Path $Version.ToLowerInvariant() "$id.nuspec"))
    if (!(Test-Path -LiteralPath $nuspec -PathType Leaf)) { return 'NOASSERTION' }
    try {
        $xml = [xml]::new(); $xml.XmlResolver = $null; $xml.Load($nuspec)
        foreach ($node in $xml.GetElementsByTagName('license', $xml.DocumentElement.NamespaceURI)) {
            $text = $node.InnerText.Trim()
            if ($node.GetAttribute('type') -ceq 'expression' -and $text -cmatch $script:PackageSpdxPattern) { return $text }
        }
        # P10-14: a legacy licenseUrl counts only when it is a licenses.nuget.org expression URL (what NuGet itself writes
        # for expression licences); any other URL, or a <license type="file">, stays NOASSERTION.
        foreach ($node in $xml.GetElementsByTagName('licenseUrl', $xml.DocumentElement.NamespaceURI)) {
            if ($node.InnerText.Trim() -cmatch '\Ahttps?://licenses\.nuget\.org/([^?#/]+)/?\z') {
                $text = [Uri]::UnescapeDataString($Matches[1]).Trim()
                if ($text -cmatch $script:PackageSpdxPattern) { return $text }
            }
        }
    } catch { }
    return 'NOASSERTION'
}
# First licence that resolves across package folders, in the order given (the caller passes the restore's own
# packageFolders first, then -PackagesRoot).
function Resolve-PackageNuspecLicense([string[]]$PackagesRoots, [string]$Name, [string]$Version) {
    foreach ($packagesRoot in @($PackagesRoots | Where-Object { $_ })) {
        $license = Read-PackageNuspecLicense $packagesRoot $Name $Version
        if ($license -cne 'NOASSERTION') { return $license }
    }
    return 'NOASSERTION'
}

# --- P10-14 transitive attribution helpers ---
# Shipped binaries: managed assemblies (.dll/.exe, listed in sbom "assemblies") and native libraries (.so, versioned
# .so.N, .dylib; recorded per reference as "natives"). Native .dll files count as assemblies, as P10-13 lists them.
$script:PackageAssemblyPattern = '(?i)\.(dll|exe)\z'
$script:PackageNativePattern = '(?i)\.(dylib|so(\.[0-9]+)*)\z'
$script:PackageBinaryPattern = '(?i)\.(dll|exe|dylib|so(\.[0-9]+)*)\z'
# Keys of a parsed JSON object in ordinal order (ConvertFrom-Json -AsHashtable is unordered on pwsh 7.2); none otherwise.
function Get-PackageSortedKeys($Dictionary) {
    if ($Dictionary -isnot [Collections.IDictionary]) { return }
    $keys = [string[]]@($Dictionary.Keys | ForEach-Object { [string]$_ })
    [Array]::Sort($keys, [StringComparer]::Ordinal)
    $keys
}
# A restore packageFolders key as a local directory: trailing separators dropped; on '/' systems a Windows-style key's
# backslashes become '/'.
function ConvertTo-PackageFolderPath([string]$Folder) {
    $path = if ([IO.Path]::DirectorySeparatorChar -eq '/') { $Folder.Replace('\', '/') } else { $Folder }
    $trimmed = $path.TrimEnd('\', '/')
    if ($trimmed) { $trimmed } else { $path }
}
# Restore output of a project (obj/project.assets.json beside it), parsed as nested hashtables; $null if absent.
function Read-PackageAssetsFile([string]$ProjectPath, [string]$Base) {
    $path = Join-Path (Split-Path $ProjectPath) 'obj/project.assets.json'
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    try { return ([IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable -ErrorAction Stop) }
    catch { throw "sbom: restore assets file unreadable: $(Get-PackageRelativePath $Base $path): $($_.Exception.Message)" }
}
# Leaf names of the binaries a target entry (project.assets.json or *.deps.json) deploys at run time: runtime,
# native, resource(s) and runtimeTargets (RID-specific) assets. NuGet's '_._' placeholder and non-binaries are ignored.
function Get-PackageTargetFiles($Entry) {
    $names = [Collections.Generic.List[string]]::new()
    foreach ($key in (Get-PackageSortedKeys $Entry)) {
        if ($key -cnotin 'runtime', 'native', 'resource', 'resources', 'runtimeTargets') { continue }
        foreach ($assetPath in (Get-PackageSortedKeys $Entry[$key])) {
            $leaf = $assetPath.Split('/')[-1]
            if ($leaf -cne '_._' -and $leaf -match $script:PackageBinaryPattern) { $names.Add($leaf) }
        }
    }
    $names.ToArray()
}
# Canonical file list of a reference property (Assemblies or Natives): unique ignoring case (first spelling kept), ordinal.
function Get-PackageReferenceList($Reference, [string]$Property) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $list = [Collections.Generic.List[string]]::new()
    foreach ($name in @($Reference.$Property | Where-Object { $_ })) { if ($seen.Add([string]$name)) { $list.Add([string]$name) } }
    $array = $list.ToArray(); [Array]::Sort($array, [StringComparer]::Ordinal)
    return ,$array
}
function Get-PackageReferenceFiles($Reference) {
    $assemblies = Get-PackageReferenceList $Reference 'Assemblies'
    $natives = Get-PackageReferenceList $Reference 'Natives'
    @($assemblies) + @($natives)
}
function Get-PackageReferenceDependency($Reference) { if ([string]$Reference.Dependency -ceq 'transitive') { 'transitive' } else { 'direct' } }

# What each published folder's *.deps.json says about its binaries: leaf names of project (first-party) files, and
# for package files the lowercase id/version the host loads them from. Keyed by the folder's relative path ('.' = root).
# An unreadable deps.json contributes nothing (fail-safe: its binaries are then unattributed and refused).
function Get-PackageDepsIndex([string]$Directory) {
    $base = [IO.Path]::GetFullPath($Directory)
    $index = @{}
    foreach ($relative in @(Get-PackageFiles $base | Where-Object { $_ -match '(?i)\.deps\.json\z' })) {
        $full = Join-Path $base $relative
        try { $deps = [IO.File]::ReadAllText($full) | ConvertFrom-Json -AsHashtable -ErrorAction Stop } catch { continue }
        if ($deps -isnot [Collections.IDictionary]) { continue }
        $slash = $relative.LastIndexOf('/')
        $folder = if ($slash -lt 0) { '.' } else { $relative.Substring(0, $slash) }
        if (!$index.ContainsKey($folder)) { $index[$folder] = [pscustomobject]@{ FirstParty = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase); Packages = @{} } }
        $info = $index[$folder]
        $types = @{}
        foreach ($key in (Get-PackageSortedKeys $deps['libraries'])) { $library = $deps['libraries'][$key]; if ($library -is [Collections.IDictionary]) { $types[$key.ToLowerInvariant()] = [string]$library['type'] } }
        foreach ($framework in (Get-PackageSortedKeys $deps['targets'])) {
            $target = $deps['targets'][$framework]
            foreach ($key in (Get-PackageSortedKeys $target)) {
                $type = $types[$key.ToLowerInvariant()]
                foreach ($leaf in (Get-PackageTargetFiles $target[$key])) {
                    if ($type -ceq 'project') { [void]$info.FirstParty.Add($leaf) }
                    elseif ($type -ceq 'package') {
                        $leafKey = $leaf.ToLowerInvariant()
                        if (!$info.Packages.ContainsKey($leafKey)) { $info.Packages[$leafKey] = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal) }
                        [void]$info.Packages[$leafKey].Add($key.ToLowerInvariant())
                    }
                }
            }
        }
    }
    return $index
}
# Attribution of one shipped binary (package-relative path). First-party when the deepest enclosing folder's deps.json
# lists the file under a project entry. Otherwise candidates are the references whose assemblies/natives name the file
# (ignoring case); for a .dll/.exe with no such reference, P10-13 references without file lists (no assets data) are
# matched by name. When that folder's deps.json names the package(s) the file loads from, only those id/versions
# count. One candidate attributes the file; several are ambiguous (sbom_attribution_ambiguous); none is unattributed.
function Resolve-PackageFileAttribution([object[]]$References, [string]$RelativePath, $DepsIndex) {
    $leaf = $RelativePath.Split('/')[-1]
    $info = $null; $best = -1
    foreach ($folder in @($DepsIndex.Keys)) {
        $prefix = if ($folder -ceq '.') { '' } else { [string]$folder + '/' }
        if ($RelativePath.StartsWith($prefix, [StringComparison]::Ordinal) -and $prefix.Length -gt $best) { $best = $prefix.Length; $info = $DepsIndex[$folder] }
    }
    if ($info -and $info.FirstParty.Contains($leaf)) { return [pscustomobject]@{ Reference = $null; Ambiguous = $false; FirstParty = $true } }
    $candidates = [Collections.Generic.List[object]]::new()
    foreach ($ref in $References) { foreach ($name in @(Get-PackageReferenceFiles $ref)) { if ($name -ieq $leaf) { $candidates.Add($ref); break } } }
    if ($candidates.Count -eq 0 -and $leaf -match $script:PackageAssemblyPattern) {
        $stem = [IO.Path]::GetFileNameWithoutExtension($leaf)
        foreach ($ref in $References) { if (@(Get-PackageReferenceFiles $ref).Count -eq 0 -and [string]$ref.Name -ieq $stem) { $candidates.Add($ref) } }
    }
    $leafKey = $leaf.ToLowerInvariant()
    if ($info -and $info.Packages.ContainsKey($leafKey)) {
        $loaded = $info.Packages[$leafKey]
        $confirmed = [Collections.Generic.List[object]]::new()
        foreach ($ref in $candidates) { if ($loaded.Contains(([string]$ref.Name + '/' + [string]$ref.Version).ToLowerInvariant())) { $confirmed.Add($ref) } }
        $candidates = $confirmed
    }
    $reference = if ($candidates.Count -eq 1) { $candidates[0] } else { $null }
    return [pscustomobject]@{ Reference = $reference; Ambiguous = ($candidates.Count -gt 1); FirstParty = $false }
}

# ProjectReference closure of one entry project (full paths, entry first).
function Get-PackageProjectClosure([string]$Base, [string]$EntryPath) {
    $queue = [Collections.Generic.Queue[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $files = [Collections.Generic.List[string]]::new()
    $queue.Enqueue($EntryPath)
    while ($queue.Count) {
        $path = $queue.Dequeue()
        if (!$seen.Add($path)) { continue }
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "sbom: project not found: $(Get-PackageRelativePath $Base $path)" }
        $files.Add($path)
        $xml = [xml]::new(); $xml.XmlResolver = $null; $xml.Load($path)
        foreach ($reference in $xml.GetElementsByTagName('ProjectReference')) {
            $include = Get-PackageXmlValue $reference 'Include'
            if ($include) { $queue.Enqueue([IO.Path]::GetFullPath((Join-Path (Split-Path $path) $include.Replace('\', '/')))) }
        }
    }
    return $files.ToArray()
}
# Shipped (runtime, non-vendor) PackageReferences declared in one csproj/props file, each pinned to one version.
function Read-PackageDeclaredReferences([string]$Base, [string]$File) {
    $relative = Get-PackageRelativePath $Base $File
    $result = [Collections.Generic.List[object]]::new()
    $xml = [xml]::new(); $xml.XmlResolver = $null; $xml.Load($File)
    foreach ($reference in $xml.GetElementsByTagName('PackageReference')) {
        $name = Get-PackageXmlValue $reference 'Include'
        if (!$name -or $name -match $script:PackageVendorPattern -or !(Test-PackageReferenceShipped $reference)) { continue }
        $raw = Get-PackageXmlValue $reference 'Version'
        $pinned = if ($raw -cmatch '\A\[([^,\[\]()]+)\]\z') { $Matches[1].Trim() } else { [string]$raw }
        $version = ConvertTo-PackageNuGetVersion $pinned
        if (!$version) { throw "sbom: shipped reference $name in $relative is not pinned to one version: '$raw'" }
        $result.Add([pscustomobject]@{ Name = $name; Version = $version })
    }
    return $result.ToArray()
}
function New-PackageReferenceItem([string]$Name, [string]$Version, [string]$Dependency) {
    [pscustomobject]@{ Name = $Name; Version = $Version; Dependency = $Dependency; Projects = [Collections.Generic.List[string]]::new()
        Assemblies = [Collections.Generic.List[string]]::new(); Natives = [Collections.Generic.List[string]]::new() }
}

# NuGet references that ship with the given entry projects, sorted ordinally by name then version.
# Direct (P10-13): runtime references declared in each entry's ProjectReference closure plus the root
# Directory.Build.props, vendor packages never; each must be pinned to one literal version (floating, ranged, property
# or missing versions are refused); "projects" are the declaring files.
# Transitive (P10-14): each entry project's own restore graph (its obj/project.assets.json, every target framework and
# RID target) is walked from those direct references; the entry's graph is what its published folder loads, so
# non-entry projects' assets files are not read. A direct reference restored at another version than declared is
# refused. Vendor packages are neither listed nor walked (their dependencies are build-only by policy; fail-safe: any
# binary they would ship is refused as unattributed). Each package records the assembly and native file names it
# deploys; transitive packages without any are not listed, and their "projects" are the entry projects reaching them.
# Without an assets file an entry contributes its direct references only, with no file lists.
# Licences come from the nuspec, looked up in the restores' packageFolders first, then -PackagesRoot.
function Get-PackageNuGetReferences([string]$Root, [string[]]$Projects, [string]$PackagesRoot) {
    $base = [IO.Path]::GetFullPath($Root)
    $props = Join-Path $base 'Directory.Build.props'
    $hasProps = Test-Path -LiteralPath $props -PathType Leaf
    $declared = @{}
    $byKey = [ordered]@{}
    $restoreFolders = [Collections.Generic.List[string]]::new()
    foreach ($project in $Projects) {
        $entryPath = [IO.Path]::GetFullPath((Join-Path $base $project))
        $closure = @(Get-PackageProjectClosure $base $entryPath)
        $entryRelative = Get-PackageRelativePath $base $entryPath
        $declaringFiles = [Collections.Generic.List[string]]::new()
        if ($hasProps) { $declaringFiles.Add($props) }
        foreach ($file in $closure) { $declaringFiles.Add($file) }
        $rootVersions = @{}
        foreach ($file in $declaringFiles) {
            if (!$declared.ContainsKey($file)) { $declared[$file] = @(Read-PackageDeclaredReferences $base $file) }
            $relative = Get-PackageRelativePath $base $file
            foreach ($ref in $declared[$file]) {
                $key = $ref.Name.ToLowerInvariant() + '@' + $ref.Version.ToLowerInvariant()
                if (!$byKey.Contains($key)) { $byKey[$key] = New-PackageReferenceItem $ref.Name $ref.Version 'direct' }
                $item = $byKey[$key]
                if ($item.Dependency -ceq 'transitive') { $item.Dependency = 'direct'; $item.Projects.Clear() }
                if (!$item.Projects.Contains($relative)) { $item.Projects.Add($relative) }
                $lower = $ref.Name.ToLowerInvariant()
                if (!$rootVersions.ContainsKey($lower)) { $rootVersions[$lower] = [Collections.Generic.List[string]]::new() }
                if (!$rootVersions[$lower].Contains($ref.Version)) { $rootVersions[$lower].Add($ref.Version) }
            }
        }
        $assets = Read-PackageAssetsFile $entryPath $base
        if ($null -eq $assets) { continue }
        foreach ($folder in (Get-PackageSortedKeys $assets['packageFolders'])) { $path = ConvertTo-PackageFolderPath $folder; if (!$restoreFolders.Contains($path)) { $restoreFolders.Add($path) } }
        $rootNames = [string[]]@($rootVersions.Keys); [Array]::Sort($rootNames, [StringComparer]::Ordinal)
        if ($rootNames.Length -eq 0) { continue }
        foreach ($framework in (Get-PackageSortedKeys $assets['targets'])) {
            $target = $assets['targets'][$framework]
            if ($target -isnot [Collections.IDictionary]) { continue }
            $index = @{}
            foreach ($entryKey in (Get-PackageSortedKeys $target)) { $slash = $entryKey.LastIndexOf('/'); if ($slash -gt 0) { $index[$entryKey.Substring(0, $slash).ToLowerInvariant()] = $entryKey } }
            $pending = [Collections.Generic.Queue[string]]::new()
            foreach ($name in $rootNames) { $pending.Enqueue($name) }
            $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            while ($pending.Count) {
                $current = $pending.Dequeue().ToLowerInvariant()
                if (!$visited.Add($current) -or !$index.ContainsKey($current)) { continue }
                $entryKey = $index[$current]
                $entry = $target[$entryKey]
                if ($entry -isnot [Collections.IDictionary] -or [string]$entry['type'] -cne 'package') { continue }
                $slash = $entryKey.LastIndexOf('/')
                $name = $entryKey.Substring(0, $slash)
                # Vendor packages and everything only they depend on are intentionally not walked (see above).
                if ($name -match $script:PackageVendorPattern) { continue }
                $version = ConvertTo-PackageNuGetVersion $entryKey.Substring($slash + 1)
                if (!$version) { throw "sbom: restore assets of $entryRelative list $entryKey with an unreadable version" }
                if ($rootVersions.ContainsKey($current)) {
                    $other = @($rootVersions[$current] | Where-Object { $_ -ine $version })
                    if ($other.Count) { throw "sbom: $name restored as $version for $entryRelative but declared as $($other -join ', ') in its project closure; pin every shipped reference to the restored version" }
                }
                $key = $current + '@' + $version.ToLowerInvariant()
                if (!$byKey.Contains($key)) { $byKey[$key] = New-PackageReferenceItem $name $version 'transitive' }
                $item = $byKey[$key]
                if ($item.Dependency -ceq 'transitive' -and !$item.Projects.Contains($entryRelative)) { $item.Projects.Add($entryRelative) }
                foreach ($file in (Get-PackageTargetFiles $entry)) { if ($file -match $script:PackageNativePattern) { $item.Natives.Add($file) } else { $item.Assemblies.Add($file) } }
                foreach ($dependency in (Get-PackageSortedKeys $entry['dependencies'])) { $pending.Enqueue($dependency) }
            }
        }
    }
    $licenseRoots = [Collections.Generic.List[string]]::new($restoreFolders)
    if ($PackagesRoot) { $licenseRoots.Add($PackagesRoot) }
    $bySortKey = @{}
    foreach ($entry in $byKey.Values) {
        if ($entry.Dependency -ceq 'transitive' -and @(Get-PackageReferenceFiles $entry).Count -eq 0) { continue }
        $bySortKey[$entry.Name + "`0" + $entry.Version] = $entry
    }
    $sortKeys = [string[]]@($bySortKey.Keys)
    [Array]::Sort($sortKeys, [StringComparer]::Ordinal)
    $result = [Collections.Generic.List[object]]::new()
    foreach ($sortKey in $sortKeys) {
        $entry = $bySortKey[$sortKey]
        $projectArray = $entry.Projects.ToArray(); [Array]::Sort($projectArray, [StringComparer]::Ordinal)
        $entryAssemblies = Get-PackageReferenceList $entry 'Assemblies'
        $entryNatives = Get-PackageReferenceList $entry 'Natives'
        $result.Add([pscustomobject]@{ Name = $entry.Name; Version = $entry.Version; License = (Resolve-PackageNuspecLicense $licenseRoots.ToArray() $entry.Name $entry.Version); Projects = $projectArray
            Dependency = $entry.Dependency; Assemblies = [string[]]$entryAssemblies; Natives = [string[]]$entryNatives })
    }
    return $result.ToArray()
}

# package.ps1 refuses a shipped reference whose licence did not resolve from its nuspec (typed sbom_license_unknown),
# unless its name is on the explicit allowlist passed by the caller.
function Assert-PackageReferenceLicenses([object[]]$References, [string[]]$Allowlist) {
    $unknown = @($References | Where-Object { $null -ne $_ -and [string]$_.License -ceq 'NOASSERTION' -and @($Allowlist) -inotcontains [string]$_.Name } | ForEach-Object { "$($_.Name)@$($_.Version)" })
    if ($unknown.Count) { throw "sbom_license_unknown: shipped NuGet reference licence not resolved from its nuspec (declare a licence expression or allowlist it in scripts/package.ps1): $($unknown -join ', ')" }
}

# Version of a shipped assembly read from its own bytes (managed metadata: AssemblyInformationalVersion without
# '+build' metadata, else the assembly version), so it does not depend on the OS, the file path or Win32 version APIs.
function Get-PackageAssemblyVersion([string]$Path) {
    $pe = $null
    try {
        $bytes = [IO.File]::ReadAllBytes($Path)
        $pe = [Reflection.PortableExecutable.PEReader]::new([Collections.Immutable.ImmutableArray]::Create([byte[]]$bytes))
        if (!$pe.HasMetadata) { return 'NOASSERTION' }
        $md = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        if (!$md.IsAssembly) { return 'NOASSERTION' }
        $assembly = $md.GetAssemblyDefinition()
        foreach ($handle in $assembly.GetCustomAttributes()) {
            $attribute = $md.GetCustomAttribute($handle)
            $ctor = $attribute.Constructor
            if ($ctor.Kind -eq [Reflection.Metadata.HandleKind]::MemberReference) {
                $member = $md.GetMemberReference([Reflection.Metadata.MemberReferenceHandle]$ctor)
                if ($member.Parent.Kind -ne [Reflection.Metadata.HandleKind]::TypeReference) { continue }
                $type = $md.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]$member.Parent)
            } elseif ($ctor.Kind -eq [Reflection.Metadata.HandleKind]::MethodDefinition) {
                $type = $md.GetTypeDefinition($md.GetMethodDefinition([Reflection.Metadata.MethodDefinitionHandle]$ctor).GetDeclaringType())
            } else { continue }
            if ($md.GetString($type.Namespace) -cne 'System.Reflection' -or $md.GetString($type.Name) -cne 'AssemblyInformationalVersionAttribute') { continue }
            $blob = $md.GetBlobReader($attribute.Value)
            if ($blob.ReadUInt16() -ne 1) { continue }
            $value = ([string]$blob.ReadSerializedString()).Split('+')[0].Trim()
            if ($value -cmatch '\A[0-9A-Za-z][0-9A-Za-z.-]*\z') { return $value }
        }
        return $assembly.Version.ToString()
    } catch { return 'NOASSERTION' }
    finally { if ($pe) { $pe.Dispose() } }
}

# Canonical sbom.json: fixed key order, LF endings, assemblies (.dll/.exe) in ordinal path order, references sorted.
# An assembly attributed to a NuGet reference (Resolve-PackageFileAttribution) takes its licence and names that
# "package" (id@version); a first-party, ambiguous or unattributed assembly declares NOASSERTION with no package field,
# so its line is the P10-13 one. References carry "dependency" (direct/transitive), "assemblies" and "natives"
# (shipped file names) after "projects" (additive P10-14 fields).
function New-PackageSbomText([string]$Directory, [string]$Version, [string]$Commit, [object[]]$References) {
    $base = [IO.Path]::GetFullPath($Directory)
    $refs = @($References | Where-Object { $null -ne $_ })
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('{')
    $lines.Add('  "schema": ' + (ConvertTo-PackageJsonString $script:PackageSbomSchema) + ',')
    $lines.Add('  "version": ' + (ConvertTo-PackageJsonString $Version) + ',')
    $lines.Add('  "source_commit": ' + (ConvertTo-PackageJsonString $Commit) + ',')
    $assemblies = [string[]]@(Get-PackageFiles $base | Where-Object { $_ -match $script:PackageAssemblyPattern })
    $depsIndex = Get-PackageDepsIndex $base
    if ($assemblies.Length -eq 0) { $lines.Add('  "assemblies": [],') } else {
        $lines.Add('  "assemblies": [')
        for ($i = 0; $i -lt $assemblies.Length; $i++) {
            $full = Join-Path $base $assemblies[$i]
            $name = [IO.Path]::GetFileNameWithoutExtension($assemblies[$i])
            $owner = (Resolve-PackageFileAttribution $refs $assemblies[$i] $depsIndex).Reference
            $license = if ($owner) { [string]$owner.License } else { 'NOASSERTION' }
            $package = if ($owner) { ', "package": ' + (ConvertTo-PackageJsonString ([string]$owner.Name + '@' + [string]$owner.Version)) } else { '' }
            $hash = Get-PackageFileSha256 $full
            $comma = if ($i -lt $assemblies.Length - 1) { ',' } else { '' }
            $lines.Add('    { "path": ' + (ConvertTo-PackageJsonString $assemblies[$i]) + ', "name": ' + (ConvertTo-PackageJsonString $name) + ', "version": ' + (ConvertTo-PackageJsonString (Get-PackageAssemblyVersion $full)) +
                ', "license": ' + (ConvertTo-PackageJsonString $license) + $package + ', "sha256": "' + $hash + '" }' + $comma)
        }
        $lines.Add('  ],')
    }
    if ($refs.Count -eq 0) { $lines.Add('  "nuget_references": []') } else {
        $lines.Add('  "nuget_references": [')
        for ($i = 0; $i -lt $refs.Count; $i++) {
            $projectText = (@($refs[$i].Projects) | ForEach-Object { ConvertTo-PackageJsonString ([string]$_) }) -join ', '
            $refAssemblies = Get-PackageReferenceList $refs[$i] 'Assemblies'
            $assemblyText = (@($refAssemblies) | ForEach-Object { ConvertTo-PackageJsonString ([string]$_) }) -join ', '
            $refNatives = Get-PackageReferenceList $refs[$i] 'Natives'
            $nativeText = (@($refNatives) | ForEach-Object { ConvertTo-PackageJsonString ([string]$_) }) -join ', '
            $comma = if ($i -lt $refs.Count - 1) { ',' } else { '' }
            $lines.Add('    { "name": ' + (ConvertTo-PackageJsonString ([string]$refs[$i].Name)) + ', "version": ' + (ConvertTo-PackageJsonString ([string]$refs[$i].Version)) +
                ', "license": ' + (ConvertTo-PackageJsonString ([string]$refs[$i].License)) + ', "projects": [' + $projectText + ']' +
                ', "dependency": "' + (Get-PackageReferenceDependency $refs[$i]) + '", "assemblies": [' + $assemblyText + '], "natives": [' + $nativeText + '] }' + $comma)
        }
        $lines.Add('  ]')
    }
    $lines.Add('}')
    return ($lines -join "`n") + "`n"
}

# P10-15 bidirectional manifest/sbom agreement over the shipped binaries, read from manifest.json and sbom.json rather
# than from the files on disk (typed sbom_manifest_mismatch naming each offending path in the message of its direction):
# every manifest.json .dll/.exe has an sbom "assemblies" line with the same path; every manifest.json .so[.N]/.dylib is
# named (ignoring case) in a listed reference's "natives" or is first-party (a project entry of its folder's *.deps.json,
# the one input read from disk, via $DepsIndex), natives having no per-path sbom line; and every sbom "assemblies" path
# is a manifest.json binary entry. An sbom "assemblies" entry without a path is sbom_invalid naming "<missing path>" and
# the entry's name. Reference "assemblies"/"natives" name lists are restore data (every RID and culture) and are not
# required to ship.
function Test-PackageSbomManifestAgreement($Manifest, $Sbom, [object[]]$References, $DepsIndex) {
    $problems = [Collections.Generic.List[string]]::new()
    $shipped = [Collections.Generic.List[string]]::new()
    $shippedSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in @($Manifest.files | Where-Object { $null -ne $_ })) {
        $path = [string]$entry.path
        if ($path -and $path -match $script:PackageBinaryPattern -and $shippedSet.Add($path)) { $shipped.Add($path) }
    }
    $lined = [Collections.Generic.List[string]]::new()
    $linedSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pathless = [Collections.Generic.List[string]]::new()
    foreach ($component in @($Sbom.assemblies | Where-Object { $null -ne $_ })) {
        $path = [string]$component.path
        if (!$path) { $pathless.Add("<missing path> (name '$(if ([string]$component.name) { [string]$component.name } else { '<missing name>' })')"); continue }
        if ($linedSet.Add($path)) { $lined.Add($path) }
    }
    $nativeNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($ref in @($References | Where-Object { $null -ne $_ })) { foreach ($name in @(Get-PackageReferenceList $ref 'Natives')) { [void]$nativeNames.Add([string]$name) } }
    $unlined = [Collections.Generic.List[string]]::new()
    foreach ($path in $shipped) {
        if ($path -match $script:PackageAssemblyPattern) { if (!$linedSet.Contains($path)) { $unlined.Add($path) }; continue }
        if ($nativeNames.Contains($path.Split('/')[-1])) { continue }
        if ((Resolve-PackageFileAttribution @() $path $DepsIndex).FirstParty) { continue }
        $unlined.Add($path)
    }
    $orphans = [Collections.Generic.List[string]]::new()
    foreach ($path in $lined) { if (!$shippedSet.Contains($path)) { $orphans.Add($path) } }
    if ($pathless.Count) { $problems.Add("sbom_invalid: sbom.json assemblies entries without a path: $($pathless -join ', ')") }
    if ($unlined.Count) { $problems.Add("sbom_manifest_mismatch: shipped binaries in manifest.json without an sbom.json line: $($unlined -join ', ')") }
    if ($orphans.Count) { $problems.Add("sbom_manifest_mismatch: sbom.json assemblies lines without a manifest.json entry: $($orphans -join ', ')") }
    return $problems.ToArray()
}

# Typed SBOM problems (sbom_missing, sbom_invalid, sbom_build_only, sbom_mismatch; P10-14 sbom_unattributed for a
# shipped binary (.dll/.exe/.so[.N]/.dylib) that is neither first-party (a project entry of its folder's *.deps.json)
# nor attributed to one listed reference, sbom_attribution_ambiguous when several listed references claim it). The sbom is re-rendered from the
# package files and its own reference list and must match byte for byte, and the references must be canonical
# (ordinal name/version order, unique, sorted projects), with each assembly carrying its same-name reference's
# licence. Reference data itself (versions, licences, projects) cannot be checked without the sources and NuGet
# cache: an sbom whose references were rewritten consistently and whose manifest was re-hashed still verifies.
function Test-PackageSbom([string]$Directory, $Manifest) {
    $base = [IO.Path]::GetFullPath($Directory)
    $problems = [Collections.Generic.List[string]]::new()
    $path = Join-Path $base 'sbom.json'
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { $problems.Add('sbom_missing: sbom.json missing'); return $problems.ToArray() }
    try {
        $text = [Text.UTF8Encoding]::new($false, $true).GetString([IO.File]::ReadAllBytes($path))
        $sbom = $text | ConvertFrom-Json -ErrorAction Stop
    } catch { $problems.Add("sbom_invalid: sbom.json unreadable: $($_.Exception.Message)"); return $problems.ToArray() }
    if ($sbom.schema -cne $script:PackageSbomSchema) { $problems.Add("sbom_invalid: sbom schema '$($sbom.schema)' is not $script:PackageSbomSchema") }
    $references = [Collections.Generic.List[object]]::new()
    foreach ($ref in @($sbom.nuget_references | Where-Object { $null -ne $_ })) {
        $name = [string]$ref.name
        if ($name -match $script:PackageVendorPattern) { $problems.Add("sbom_build_only: build-only vendor reference listed as shipped: $name"); continue }
        if ($name -cnotmatch '\A[A-Za-z0-9_][A-Za-z0-9_.-]*\z' -or [string]$ref.version -cnotmatch '\A[0-9A-Za-z][0-9A-Za-z.+-]*\z' -or [string]$ref.license -cnotmatch $script:PackageSpdxPattern) {
            $problems.Add("sbom_invalid: malformed nuget reference '$name' '$($ref.version)' '$($ref.license)'"); continue
        }
        $dependency = if ($ref.PSObject.Properties['dependency']) { [string]$ref.dependency } else { 'direct' }
        $refAssemblies = [string[]]@($ref.assemblies | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ })
        $refNatives = [string[]]@($ref.natives | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_ })
        if ($dependency -cne 'direct' -and $dependency -cne 'transitive') { $problems.Add("sbom_invalid: nuget reference '$name' dependency '$dependency' is not direct or transitive"); continue }
        $badAssembly = @($refAssemblies | Where-Object { $_ -cnotmatch '\A[A-Za-z0-9_][A-Za-z0-9_.+-]*\.([Dd][Ll][Ll]|[Ee][Xx][Ee])\z' })
        $badNative = @($refNatives | Where-Object { $_ -cnotmatch '\A[A-Za-z0-9_][A-Za-z0-9_.+-]*\z' -or $_ -notmatch $script:PackageNativePattern })
        if ($badAssembly.Count -or $badNative.Count) { $problems.Add("sbom_invalid: nuget reference '$name' lists malformed assembly/native file names: $(@($badAssembly) + @($badNative) -join ', ')"); continue }
        $references.Add([pscustomobject]@{ Name = $name; Version = [string]$ref.version; License = [string]$ref.license; Projects = [string[]]@($ref.projects | ForEach-Object { [string]$_ }); Dependency = $dependency; Assemblies = $refAssemblies; Natives = $refNatives })
    }
    $order = [Collections.Generic.List[string]]::new()
    $unique = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    for ($i = 0; $i -lt $references.Count; $i++) {
        $key = $references[$i].Name + "`0" + $references[$i].Version
        if (!$unique.Add($key)) { $order.Add("duplicate reference $($references[$i].Name)@$($references[$i].Version)") }
        elseif ($i -gt 0 -and [string]::CompareOrdinal($references[$i - 1].Name + "`0" + $references[$i - 1].Version, $key) -ge 0) { $order.Add("$($references[$i].Name)@$($references[$i].Version) out of ordinal name/version order") }
        $projects = [string[]]@($references[$i].Projects)
        $sortedProjects = [string[]]@([Linq.Enumerable]::Distinct($projects, [StringComparer]::Ordinal)); [Array]::Sort($sortedProjects, [StringComparer]::Ordinal)
        if ($projects.Length -eq 0 -or ($projects -join "`n") -cne ($sortedProjects -join "`n")) { $order.Add("projects of $($references[$i].Name) empty, unsorted or duplicated") }
    }
    if ($order.Count) { $problems.Add("sbom_mismatch: nuget_references are not canonical: $($order -join '; ')") }
    foreach ($component in @($sbom.assemblies | Where-Object { $null -ne $_ })) {
        if ([string]$component.name -match $script:PackageVendorPattern) { $problems.Add("sbom_build_only: vendor assembly listed as shipped: $($component.path)") }
    }
    # P10-14: every shipped binary that is not first-party must be attributed to exactly one listed NuGet package.
    $unattributed = [Collections.Generic.List[string]]::new()
    $ambiguous = [Collections.Generic.List[string]]::new()
    $depsIndex = Get-PackageDepsIndex $base
    foreach ($binary in @(Get-PackageFiles $base | Where-Object { $_ -match $script:PackageBinaryPattern })) {
        $attribution = Resolve-PackageFileAttribution $references.ToArray() $binary $depsIndex
        if ($attribution.FirstParty) { continue }
        if ($attribution.Ambiguous) { $ambiguous.Add($binary) } elseif ($null -eq $attribution.Reference) { $unattributed.Add($binary) }
    }
    if ($unattributed.Count) { $problems.Add("sbom_unattributed: third-party binaries not attributed to a NuGet package (direct or transitive) in sbom.json: $($unattributed -join ', ')") 
        if ($IsMacOS) { # TEMP P10-18 diagnostic
            $diag = foreach ($rel in @(Get-PackageFiles $base | Where-Object { $_ -match '(?i)\.deps\.json\z' })) {
                $d = [IO.File]::ReadAllText((Join-Path $base $rel)) | ConvertFrom-Json -AsHashtable
                $libs = (Get-PackageSortedKeys $d['libraries'] | Where-Object { $_ -match '(?i)^bim' } | ForEach-Object { "$_=$($d['libraries'][$_]['type'])" }) -join ','
                $tg = foreach ($fw in (Get-PackageSortedKeys $d['targets'])) { "$fw{" + ((Get-PackageSortedKeys $d['targets'][$fw] | Where-Object { $_ -match '(?i)^bim' } | ForEach-Object { "$_>" + ((Get-PackageSortedKeys $d['targets'][$fw][$_]) -join '+') + ':' + ((Get-PackageSortedKeys $d['targets'][$fw][$_]['runtime']) -join '+') }) -join ',') + '}' }
                "$rel libs[$libs] targets[$($tg -join ' ')]"
            }
            $problems[$problems.Count - 1] += ' DIAG ' + ($diag -join ' || ') + ' TMP ' + [IO.Path]::GetTempPath() + ' BASE ' + $base
        }
    }
    if ($ambiguous.Count) { $problems.Add("sbom_attribution_ambiguous: several listed NuGet packages claim these shipped binaries and no deps.json disambiguates them: $($ambiguous -join ', ')") }
    foreach ($problem in @(Test-PackageSbomManifestAgreement $Manifest $sbom $references.ToArray() $depsIndex)) { $problems.Add($problem) }
    $expected = New-PackageSbomText $base ([string]$Manifest.version) ([string]$Manifest.source_commit) $references.ToArray()
    if ($text -cne $expected) {
        $listed = @{}
        foreach ($component in @($sbom.assemblies | Where-Object { $null -ne $_ })) { $listed[[string]$component.path] = [string]$component.sha256 }
        $details = [Collections.Generic.List[string]]::new()
        foreach ($assembly in @(Get-PackageFiles $base | Where-Object { $_ -match $script:PackageAssemblyPattern })) {
            if (!$listed.ContainsKey($assembly)) { $details.Add("unlisted assembly $assembly") }
            elseif ($listed[$assembly] -cne (Get-PackageFileSha256 (Join-Path $base $assembly))) { $details.Add("sha256 differs for $assembly") }
        }
        if ([string]$sbom.version -cne [string]$Manifest.version -or [string]$sbom.source_commit -cne [string]$Manifest.source_commit) { $details.Add('version/source_commit differ from manifest.json') }
        if ($details.Count -eq 0) {
            $have = $text.Split("`n"); $want = $expected.Split("`n"); $line = 0
            while ($line -lt $have.Length -and $line -lt $want.Length -and $have[$line] -ceq $want[$line]) { $line++ }
            $got = if ($line -lt $have.Length) { $have[$line].Replace("`r", '\r') } else { '<end>' }; $exp = if ($line -lt $want.Length) { $want[$line] } else { '<end>' }
            $details.Add("not the canonical sbom for this package (line $($line + 1): got '$(if ($got.Length -gt 200) { $got.Substring(0, 200) } else { $got })' expected '$(if ($exp.Length -gt 200) { $exp.Substring(0, 200) } else { $exp })')")
        }
        $problems.Add("sbom_mismatch: sbom.json does not describe the package: $($details -join '; ')")
    }
    return $problems.ToArray()
}

# P10-16: shipped files whose first bytes are an executable image (PE "MZ", ELF 7F 45 4C 46, Mach-O FEEDFACE/FEEDFACF
# and fat CAFEBABE, each in both byte orders) but whose extension is outside $script:PackageBinaryPattern escape sbom
# attribution, so they are refused (typed sbom_binary_unclassified naming each path, ordinal order). PE detection is
# the two-byte DOS signature "MZ" alone, so a text file whose first two bytes are literally "MZ" is refused too
# (deliberate, conservative); the byte-reversed "ZM" of some ancient DOS images is not detected. CAFEBABE is also the
# Java class-file magic; a .class file is refused the same way (the package ships no Java). Files shorter than a magic
# number are not executable images of that format. Symlinks and reparse points are never opened (Find-PackageLinks
# already reports them, and opening one could block on a FIFO target or fail on a dangling link); a regular file that
# cannot be read (I/O or access error) is reported as sbom_binary_unreadable naming its path instead of throwing.
$script:PackageExecutableMagics = @(
    [byte[]](0x4D, 0x5A), [byte[]](0x7F, 0x45, 0x4C, 0x46),
    [byte[]](0xFE, 0xED, 0xFA, 0xCE), [byte[]](0xCE, 0xFA, 0xED, 0xFE), [byte[]](0xFE, 0xED, 0xFA, 0xCF), [byte[]](0xCF, 0xFA, 0xED, 0xFE),
    [byte[]](0xCA, 0xFE, 0xBA, 0xBE), [byte[]](0xBE, 0xBA, 0xFE, 0xCA)
)
function Test-PackageExecutableMagic([string]$Path) {
    $head = [byte[]]::new(4)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $read = 0
        while ($read -lt $head.Length) { $n = $stream.Read($head, $read, $head.Length - $read); if ($n -le 0) { break }; $read += $n }
    } finally { $stream.Dispose() }
    foreach ($magic in $script:PackageExecutableMagics) {
        if ($read -lt $magic.Length) { continue }
        $match = $true
        for ($i = 0; $i -lt $magic.Length; $i++) { if ($head[$i] -ne $magic[$i]) { $match = $false; break } }
        if ($match) { return $true }
    }
    return $false
}
# Returns Unclassified (executable images outside the binary pattern) and Unreadable (regular files whose first bytes
# could not be read), both as package-relative paths in ordinal order.
function Find-PackageUnclassifiedBinaries([string]$Directory, $SpecialMap = $null) {
    $base = [IO.Path]::GetFullPath($Directory)
    $unclassified = [Collections.Generic.List[string]]::new()
    $unreadable = [Collections.Generic.List[string]]::new()
    $special = if ($null -ne $SpecialMap) { $SpecialMap } else { Get-PackageSpecialFileMap $base }
    foreach ($relative in @(Get-PackageFiles $base)) {
        if ($relative -match $script:PackageBinaryPattern) { continue }
        $full = Join-Path $base $relative
        $item = Get-Item -LiteralPath $full -Force -ErrorAction SilentlyContinue
        if ($null -eq $item -or $item.LinkType -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
        # P10-17: a FIFO, socket or device is never opened (package_special_file reports it).
        if ($special.ContainsKey($relative)) { continue }
        try { if (Test-PackageExecutableMagic $full) { $unclassified.Add($relative) } }
        catch {
            $exception = $_.Exception
            while ($exception -is [Management.Automation.MethodInvocationException] -and $exception.InnerException) { $exception = $exception.InnerException }
            if ($exception -is [IO.IOException] -or $exception -is [UnauthorizedAccessException]) { $unreadable.Add($relative) } else { throw }
        }
    }
    return [pscustomobject]@{ Unclassified = [string[]]$unclassified.ToArray(); Unreadable = [string[]]$unreadable.ToArray() }
}

function Write-PackageText([string]$Path, [string]$Text) {
    [IO.File]::WriteAllBytes($Path, [Text.UTF8Encoding]::new($false).GetBytes($Text))
}

# Returns a list of problems; empty means the directory matches its manifest exactly.
function Test-PackageManifest([string]$Directory) {
    $base = [IO.Path]::GetFullPath($Directory)
    $problems = [Collections.Generic.List[string]]::new()
    foreach ($link in @(Find-PackageLinks $base)) { $problems.Add("symlink or reparse point in package: $link") }
    # P10-17: every later check reads file contents (manifest.json, VERSION, sbom.json, deps.json, hashes), and a link
    # could lead to a FIFO, so links stop verification here; so do non-regular files, refused by file type before any open.
    if ($problems.Count) { return $problems.ToArray() }
    try { $specialMap = Get-PackageSpecialFileMap $base }
    catch {
        $problems.Add("package_special_file_check_failed: file types could not be checked, so no file is opened: $($_.Exception.Message)")
        return $problems.ToArray()
    }
    $special = [string[]]@(Find-PackageSpecialFiles -Map $specialMap)
    if ($special.Count) {
        $problems.Add("package_special_file: non-regular files (FIFO, socket or device) are never packaged; refused by file type without opening them, remaining checks skipped: $($special -join ', ')")
        return $problems.ToArray()
    }
    foreach ($hit in (Find-PackageForbidden $base)) { $problems.Add("forbidden file in package: $hit") }
    $manifestPath = Join-Path $base 'manifest.json'
    if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { $problems.Add('manifest.json missing'); return $problems.ToArray() }
    try { $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json -ErrorAction Stop }
    catch { $problems.Add("manifest.json unreadable: $($_.Exception.Message)"); return $problems.ToArray() }
    if ($manifest.schema -cne $script:PackageManifestSchema) { $problems.Add("manifest schema '$($manifest.schema)' is not $script:PackageManifestSchema") }
    if ([string]$manifest.version -notmatch $script:PackageVersionPattern) { $problems.Add("manifest version '$($manifest.version)' is invalid") }
    if ([string]$manifest.source_commit -cnotmatch '\A[0-9a-f]{40}\z') { $problems.Add("manifest source_commit '$($manifest.source_commit)' is not a 40-hex git sha") }
    if ($manifest.source_dirty -isnot [bool]) { $problems.Add("manifest source_dirty '$($manifest.source_dirty)' is not a boolean") }
    $versionPath = Join-Path $base 'VERSION'
    if (!(Test-Path -LiteralPath $versionPath -PathType Leaf) -or [IO.File]::ReadAllText($versionPath) -cne ([string]$manifest.version + "`n")) {
        $problems.Add("VERSION file does not match manifest version '$($manifest.version)'")
    }
    # P10-17: a shipped binary that cannot be read stops the sbom check; it is reported once, as manifest_file_unreadable
    # when listed (below), else as sbom_binary_unreadable, never as a misleading sbom_invalid.
    $sbomReadFailure = $null
    try { foreach ($problem in @(Test-PackageSbom $base $manifest)) { $problems.Add($problem) } }
    catch { if (Test-PackageReadFailure $_) { $sbomReadFailure = $_.Exception.Message } else { $problems.Add("sbom_invalid: sbom.json could not be checked: $($_.Exception.Message)") } }
    $magic = Find-PackageUnclassifiedBinaries $base $specialMap
    # The extension list in this message mirrors $script:PackageBinaryPattern; keep them in step.
    if ($magic.Unclassified.Count) { $problems.Add("sbom_binary_unclassified: executable images (PE/ELF/Mach-O by first bytes) whose extension is not .dll/.exe/.so[.N]/.dylib, so sbom attribution cannot cover them: $($magic.Unclassified -join ', ')") }
    if ($magic.Unreadable.Count) { $problems.Add("sbom_binary_unreadable: shipped files whose first bytes could not be read to classify them: $($magic.Unreadable -join ', ')") }
    $listed = [Collections.Generic.List[string]]::new()
    $unreadable = [Collections.Generic.List[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($manifest.files)) {
        $path = [string]$entry.path
        if (!$path -or $path -match '(^|/)\.\.(/|$)|^/|\\|^[A-Za-z]:' ) { $problems.Add("manifest path not relative: '$path'"); continue }
        if (!$seen.Add($path)) { $problems.Add("duplicate manifest entry: $path"); continue }
        $listed.Add($path)
        $full = Join-Path $base $path
        if (!(Test-Path -LiteralPath $full -PathType Leaf)) { $problems.Add("missing file listed in manifest: $path"); continue }
        if ((Get-Item -LiteralPath $full).Length -ne [long]$entry.size) { $problems.Add("size mismatch: $path") }
        # P10-17: a listed file that cannot be read to hash it is typed manifest_file_unreadable, never an exception.
        try { $hash = Get-PackageFileSha256 $full } catch { if (Test-PackageReadFailure $_) { $unreadable.Add($path); continue } else { throw } }
        if ($hash -cne [string]$entry.sha256) { $problems.Add("sha256 mismatch: $path") }
    }
    if ($unreadable.Count) { $problems.Add("manifest_file_unreadable: manifest-listed files could not be read to verify their sha256: $($unreadable -join ', ')") }
    elseif ($sbomReadFailure) { $problems.Add("sbom_binary_unreadable: a shipped binary could not be read to check sbom.json: $sbomReadFailure") }
    $sorted = $listed.ToArray(); [Array]::Sort($sorted, [StringComparer]::Ordinal)
    if (($sorted -join "`n") -cne ($listed -join "`n")) { $problems.Add('manifest files are not sorted ordinally') }
    $set = [Collections.Generic.HashSet[string]]::new([string[]]$listed.ToArray(), [StringComparer]::Ordinal)
    foreach ($path in @(Get-PackageFiles $base)) { if (!$set.Contains($path)) { $problems.Add("unlisted file not in manifest: $path") } }
    return $problems.ToArray()
}
