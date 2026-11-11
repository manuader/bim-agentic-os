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
            $text = [IO.File]::ReadAllText($file.FullName)
            $forbidden = $text -match '-----BEGIN ([A-Z]+ )?PRIVATE KEY-----|sk-proj-[A-Za-z0-9_-]{20,}|ghp_[A-Za-z0-9]{30,}|AKIA[0-9A-Z]{16}'
        }
        if ($forbidden) { $hits.Add((Get-PackageRelativePath $base $file.FullName)) }
    }
    $array = $hits.ToArray()
    [Array]::Sort($array, [StringComparer]::Ordinal)
    return $array
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
# First licence that resolves across package folders (-PackagesRoot first, then the restore's own packageFolders).
function Resolve-PackageNuspecLicense([string[]]$PackagesRoots, [string]$Name, [string]$Version) {
    foreach ($packagesRoot in @($PackagesRoots | Where-Object { $_ })) {
        $license = Read-PackageNuspecLicense $packagesRoot $Name $Version
        if ($license -cne 'NOASSERTION') { return $license }
    }
    return 'NOASSERTION'
}

# P10-14: restore output of a project (obj/project.assets.json beside it), parsed as nested hashtables; $null if absent.
function Read-PackageAssetsFile([string]$ProjectPath, [string]$Base) {
    $path = Join-Path (Split-Path $ProjectPath) 'obj/project.assets.json'
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    try { return ([IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable -ErrorAction Stop) }
    catch { throw "sbom: restore assets file unreadable: $(Get-PackageRelativePath $Base $path): $($_.Exception.Message)" }
}
# Assembly file names (.dll/.exe leaf names) a package target entry deploys at run time: runtime, native, resource and
# runtimeTargets (RID-specific) assets; NuGet's empty-folder placeholder '_._' and non-assembly files are ignored.
function Get-PackageTargetAssemblies($Entry) {
    $names = [Collections.Generic.List[string]]::new()
    foreach ($kind in 'runtime', 'native', 'resource', 'runtimeTargets') {
        $assets = $null
        foreach ($key in $Entry.Keys) { if ([string]$key -ceq $kind) { $assets = $Entry[$key] } }
        if ($assets -isnot [Collections.IDictionary]) { continue }
        foreach ($assetPath in $assets.Keys) {
            $leaf = ([string]$assetPath).Split('/')[-1]
            if ($leaf -cne '_._' -and $leaf -match '(?i)\.(dll|exe)\z') { $names.Add($leaf) }
        }
    }
    return $names.ToArray()
}
# Canonical assembly file list of a reference: unique ignoring case (first spelling kept), ordinal order.
function Get-PackageReferenceAssemblies($Reference) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $list = [Collections.Generic.List[string]]::new()
    foreach ($name in @($Reference.Assemblies | Where-Object { $_ })) { if ($seen.Add([string]$name)) { $list.Add([string]$name) } }
    $array = $list.ToArray(); [Array]::Sort($array, [StringComparer]::Ordinal)
    return ,$array
}
function Get-PackageReferenceDependency($Reference) { if ([string]$Reference.Dependency -ceq 'transitive') { 'transitive' } else { 'direct' } }
# The NuGet reference a shipped assembly file comes from: the first reference whose assemblies list names the file
# (ignoring case), else (P10-13, no assets data) the reference with the assembly's name; $null when unattributed.
function Find-PackageAssemblyReference([object[]]$References, [string]$FileName) {
    foreach ($ref in $References) { $names = Get-PackageReferenceAssemblies $ref; foreach ($name in $names) { if ($name -ieq $FileName) { return $ref } } }
    $stem = [IO.Path]::GetFileNameWithoutExtension($FileName)
    foreach ($ref in $References) { if ([string]$ref.Name -ieq $stem) { return $ref } }
    return $null
}

# NuGet references that ship with the given projects: their ProjectReference closure plus the root
# Directory.Build.props, runtime references only, vendor packages never. Sorted ordinally by name then version.
# A shipped reference must be pinned to one literal version; floating, ranged, property or missing versions are refused.
function Get-PackageNuGetReferences([string]$Root, [string[]]$Projects, [string]$PackagesRoot) {
    $base = [IO.Path]::GetFullPath($Root)
    $queue = [Collections.Generic.Queue[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($project in $Projects) { $queue.Enqueue([IO.Path]::GetFullPath((Join-Path $base $project))) }
    $files = [Collections.Generic.List[string]]::new()
    $props = Join-Path $base 'Directory.Build.props'
    if (Test-Path -LiteralPath $props -PathType Leaf) { $files.Add($props) }
    while ($queue.Count) {
        $path = $queue.Dequeue()
        if (!$seen.Add($path)) { continue }
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "sbom: project not found: $(Get-PackageRelativePath $base $path)" }
        $files.Add($path)
        $xml = [xml]::new(); $xml.XmlResolver = $null; $xml.Load($path)
        foreach ($reference in $xml.GetElementsByTagName('ProjectReference')) {
            $include = Get-PackageXmlValue $reference 'Include'
            if ($include) { $queue.Enqueue([IO.Path]::GetFullPath((Join-Path (Split-Path $path) $include.Replace('\', '/')))) }
        }
    }
    $byKey = [ordered]@{}
    $declared = @{}
    foreach ($file in $files) {
        $relative = Get-PackageRelativePath $base $file
        $declared[$file] = [Collections.Generic.List[string]]::new()
        $xml = [xml]::new(); $xml.XmlResolver = $null; $xml.Load($file)
        foreach ($reference in $xml.GetElementsByTagName('PackageReference')) {
            $name = Get-PackageXmlValue $reference 'Include'
            if (!$name -or $name -match $script:PackageVendorPattern -or !(Test-PackageReferenceShipped $reference)) { continue }
            $raw = Get-PackageXmlValue $reference 'Version'
            $pinned = if ($raw -cmatch '\A\[([^,\[\]()]+)\]\z') { $Matches[1].Trim() } else { [string]$raw }
            $version = ConvertTo-PackageNuGetVersion $pinned
            if (!$version) { throw "sbom: shipped reference $name in $relative is not pinned to one version: '$raw'" }
            $key = $name.ToLowerInvariant() + '@' + $version.ToLowerInvariant()
            if (!$byKey.Contains($key)) { $byKey[$key] = [pscustomobject]@{ Name = $name; Version = $version; Dependency = 'direct'; Projects = [Collections.Generic.List[string]]::new(); Assemblies = [Collections.Generic.List[string]]::new() } }
            if (!$byKey[$key].Projects.Contains($relative)) { $byKey[$key].Projects.Add($relative) }
            $declared[$file].Add($name)
        }
    }
    # P10-14: walk each shipped project's restore graph (obj/project.assets.json) from its shipped direct references
    # (its own and the root Directory.Build.props ones), so transitive packages are attributed too. Vendor packages
    # are neither listed nor descended; transitive packages without a runtime assembly are not listed. A project
    # without an assets file contributes its direct references only (P10-13 behaviour); any third-party assembly left
    # unattributed is still refused by Test-PackageSbom (sbom_unattributed).
    $packagesRoots = [Collections.Generic.List[string]]::new()
    if ($PackagesRoot) { $packagesRoots.Add($PackagesRoot) }
    foreach ($file in $files) {
        if ($file -ceq $props) { continue }
        $relative = Get-PackageRelativePath $base $file
        $assets = Read-PackageAssetsFile $file $base
        if ($null -eq $assets) { continue }
        if ($assets['packageFolders'] -is [Collections.IDictionary]) { foreach ($folder in $assets['packageFolders'].Keys) { if (!$packagesRoots.Contains([string]$folder)) { $packagesRoots.Add([string]$folder) } } }
        $roots = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($name in $declared[$file]) { [void]$roots.Add($name) }
        if ($declared.ContainsKey($props)) { foreach ($name in $declared[$props]) { [void]$roots.Add($name) } }
        if ($roots.Count -eq 0 -or $assets['targets'] -isnot [Collections.IDictionary]) { continue }
        $frameworks = [string[]]@($assets['targets'].Keys); [Array]::Sort($frameworks, [StringComparer]::Ordinal)
        foreach ($framework in $frameworks) {
            $target = $assets['targets'][$framework]
            if ($target -isnot [Collections.IDictionary]) { continue }
            $index = @{}
            foreach ($entryKey in $target.Keys) { $slash = ([string]$entryKey).LastIndexOf('/'); if ($slash -gt 0) { $index[([string]$entryKey).Substring(0, $slash).ToLowerInvariant()] = [string]$entryKey } }
            $pending = [Collections.Generic.Queue[string]]::new()
            foreach ($name in $roots) { $pending.Enqueue($name) }
            $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            while ($pending.Count) {
                $current = $pending.Dequeue()
                if (!$visited.Add($current) -or !$index.ContainsKey($current.ToLowerInvariant())) { continue }
                $entryKey = $index[$current.ToLowerInvariant()]
                $entry = $target[$entryKey]
                if ($entry -isnot [Collections.IDictionary] -or [string]$entry['type'] -cne 'package') { continue }
                $slash = $entryKey.LastIndexOf('/')
                $name = $entryKey.Substring(0, $slash)
                if ($name -match $script:PackageVendorPattern) { continue }
                $version = ConvertTo-PackageNuGetVersion $entryKey.Substring($slash + 1)
                if (!$version) { throw "sbom: restore assets of $relative list $entryKey with an unreadable version" }
                $key = $name.ToLowerInvariant() + '@' + $version.ToLowerInvariant()
                $isRoot = $roots.Contains($name)
                if ($isRoot -and !$byKey.Contains($key)) {
                    $declaredVersions = @($byKey.Values | Where-Object { $_.Name -ieq $name } | ForEach-Object { $_.Version }) -join ', '
                    throw "sbom: $name restored as $version for $relative but declared as $declaredVersions; pin the shipped reference to the restored version"
                }
                if (!$byKey.Contains($key)) { $byKey[$key] = [pscustomobject]@{ Name = $name; Version = $version; Dependency = 'transitive'; Projects = [Collections.Generic.List[string]]::new(); Assemblies = [Collections.Generic.List[string]]::new() } }
                $item = $byKey[$key]
                if ($item.Dependency -ceq 'transitive' -and !$item.Projects.Contains($relative)) { $item.Projects.Add($relative) }
                foreach ($assembly in (Get-PackageTargetAssemblies $entry)) { $item.Assemblies.Add($assembly) }
                if ($entry['dependencies'] -is [Collections.IDictionary]) { foreach ($dependency in $entry['dependencies'].Keys) { $pending.Enqueue([string]$dependency) } }
            }
        }
    }
    $bySortKey = @{}
    foreach ($entry in $byKey.Values) {
        $entryAssemblies = Get-PackageReferenceAssemblies $entry
        if ($entry.Dependency -ceq 'transitive' -and $entryAssemblies.Length -eq 0) { continue }
        $bySortKey[$entry.Name + "`0" + $entry.Version] = $entry
    }
    $sortKeys = [string[]]@($bySortKey.Keys)
    [Array]::Sort($sortKeys, [StringComparer]::Ordinal)
    $result = [Collections.Generic.List[object]]::new()
    foreach ($sortKey in $sortKeys) {
        $entry = $bySortKey[$sortKey]
        $projectArray = $entry.Projects.ToArray(); [Array]::Sort($projectArray, [StringComparer]::Ordinal)
        $entryAssemblies = Get-PackageReferenceAssemblies $entry
        $result.Add([pscustomobject]@{ Name = $entry.Name; Version = $entry.Version; License = (Resolve-PackageNuspecLicense $packagesRoots.ToArray() $entry.Name $entry.Version); Projects = $projectArray
            Dependency = $entry.Dependency; Assemblies = [string[]]$entryAssemblies })
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
# An assembly takes the licence of the NuGet reference that ships its file (P10-14: the reference's assemblies list,
# else the reference with its name) and then also names that "package" (id@version); an unattributed assembly
# (first-party Bim.*) declares NOASSERTION with no package field, so its line is the P10-13 one. References carry
# "dependency" (direct/transitive) and "assemblies" (shipped file names) after "projects" (additive P10-14 fields).
function New-PackageSbomText([string]$Directory, [string]$Version, [string]$Commit, [object[]]$References) {
    $base = [IO.Path]::GetFullPath($Directory)
    $refs = @($References | Where-Object { $null -ne $_ })
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('{')
    $lines.Add('  "schema": ' + (ConvertTo-PackageJsonString $script:PackageSbomSchema) + ',')
    $lines.Add('  "version": ' + (ConvertTo-PackageJsonString $Version) + ',')
    $lines.Add('  "source_commit": ' + (ConvertTo-PackageJsonString $Commit) + ',')
    $assemblies = [string[]]@(Get-PackageFiles $base | Where-Object { $_ -match '(?i)\.(dll|exe)$' })
    if ($assemblies.Length -eq 0) { $lines.Add('  "assemblies": [],') } else {
        $lines.Add('  "assemblies": [')
        for ($i = 0; $i -lt $assemblies.Length; $i++) {
            $full = Join-Path $base $assemblies[$i]
            $name = [IO.Path]::GetFileNameWithoutExtension($assemblies[$i])
            $owner = Find-PackageAssemblyReference $refs ([IO.Path]::GetFileName($assemblies[$i]))
            $license = if ($owner) { [string]$owner.License } else { 'NOASSERTION' }
            $package = if ($owner) { ', "package": ' + (ConvertTo-PackageJsonString ([string]$owner.Name + '@' + [string]$owner.Version)) } else { '' }
            $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
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
            $refAssemblies = Get-PackageReferenceAssemblies $refs[$i]
            $assemblyText = (@($refAssemblies) | ForEach-Object { ConvertTo-PackageJsonString ([string]$_) }) -join ', '
            $comma = if ($i -lt $refs.Count - 1) { ',' } else { '' }
            $lines.Add('    { "name": ' + (ConvertTo-PackageJsonString ([string]$refs[$i].Name)) + ', "version": ' + (ConvertTo-PackageJsonString ([string]$refs[$i].Version)) +
                ', "license": ' + (ConvertTo-PackageJsonString ([string]$refs[$i].License)) + ', "projects": [' + $projectText + ']' +
                ', "dependency": "' + (Get-PackageReferenceDependency $refs[$i]) + '", "assemblies": [' + $assemblyText + '] }' + $comma)
        }
        $lines.Add('  ]')
    }
    $lines.Add('}')
    return ($lines -join "`n") + "`n"
}

# Typed SBOM problems (sbom_missing, sbom_invalid, sbom_build_only, sbom_mismatch). The sbom is re-rendered from the
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
        if ($dependency -cne 'direct' -and $dependency -cne 'transitive') { $problems.Add("sbom_invalid: nuget reference '$name' dependency '$dependency' is not direct or transitive"); continue }
        $badAssembly = @($refAssemblies | Where-Object { $_ -cnotmatch '\A[A-Za-z0-9_][A-Za-z0-9_.+-]*\.([Dd][Ll][Ll]|[Ee][Xx][Ee])\z' })
        if ($badAssembly.Count) { $problems.Add("sbom_invalid: nuget reference '$name' lists malformed assembly file names: $($badAssembly -join ', ')"); continue }
        $references.Add([pscustomobject]@{ Name = $name; Version = [string]$ref.version; License = [string]$ref.license; Projects = [string[]]@($ref.projects | ForEach-Object { [string]$_ }); Dependency = $dependency; Assemblies = $refAssemblies })
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
    # P10-14: every shipped third-party assembly (not first-party Bim.*) must be attributed to a listed NuGet package.
    $unattributed = [Collections.Generic.List[string]]::new()
    foreach ($assembly in @(Get-PackageFiles $base | Where-Object { $_ -match '(?i)\.(dll|exe)$' })) {
        $leaf = [IO.Path]::GetFileName($assembly)
        if ($leaf -cmatch '\ABim\.') { continue }
        if (!(Find-PackageAssemblyReference $references.ToArray() $leaf)) { $unattributed.Add($assembly) }
    }
    if ($unattributed.Count) { $problems.Add("sbom_unattributed: third-party assemblies not attributed to a NuGet package (direct or transitive) in sbom.json: $($unattributed -join ', ')") }
    $expected = New-PackageSbomText $base ([string]$Manifest.version) ([string]$Manifest.source_commit) $references.ToArray()
    if ($text -cne $expected) {
        $listed = @{}
        foreach ($component in @($sbom.assemblies | Where-Object { $null -ne $_ })) { $listed[[string]$component.path] = [string]$component.sha256 }
        $details = [Collections.Generic.List[string]]::new()
        foreach ($assembly in @(Get-PackageFiles $base | Where-Object { $_ -match '(?i)\.(dll|exe)$' })) {
            if (!$listed.ContainsKey($assembly)) { $details.Add("unlisted assembly $assembly") }
            elseif ($listed[$assembly] -cne (Get-FileHash -LiteralPath (Join-Path $base $assembly) -Algorithm SHA256).Hash.ToLowerInvariant()) { $details.Add("sha256 differs for $assembly") }
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

function Write-PackageText([string]$Path, [string]$Text) {
    [IO.File]::WriteAllBytes($Path, [Text.UTF8Encoding]::new($false).GetBytes($Text))
}

# Returns a list of problems; empty means the directory matches its manifest exactly.
function Test-PackageManifest([string]$Directory) {
    $base = [IO.Path]::GetFullPath($Directory)
    $problems = [Collections.Generic.List[string]]::new()
    foreach ($link in @(Find-PackageLinks $base)) { $problems.Add("symlink or reparse point in package: $link") }
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
    try { foreach ($problem in @(Test-PackageSbom $base $manifest)) { $problems.Add($problem) } }
    catch { $problems.Add("sbom_invalid: sbom.json could not be checked: $($_.Exception.Message)") }
    $listed = [Collections.Generic.List[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($manifest.files)) {
        $path = [string]$entry.path
        if (!$path -or $path -match '(^|/)\.\.(/|$)|^/|\\|^[A-Za-z]:' ) { $problems.Add("manifest path not relative: '$path'"); continue }
        if (!$seen.Add($path)) { $problems.Add("duplicate manifest entry: $path"); continue }
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
