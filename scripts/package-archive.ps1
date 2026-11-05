#Requires -Version 7.2
# P10-08 release archive over a P10-04 release folder (scripts/package.ps1 output). Built-in .NET only.
#   Create:  -PackageDirectory <release> -ArchivePath <x.zip> [-ChecksumsPath <SHA256SUMS>]
#            Refuses a folder that fails package-verify. Writes a deterministic zip: entries are manifest.json plus every
#            manifest-listed file, in ordinal path order, each with a fixed timestamp (2000-01-01T00:00:00), fixed
#            external attributes (regular file 0644) and Optimal deflate. Then writes SHA256SUMS ('<sha256>  <name>' + LF,
#            default next to the archive). Byte-identical for the same release folder on the same OS. Never overwrites.
#   Verify:  -Verify -ArchivePath <x.zip> [-ChecksumsPath ...]
#   Extract: -Extract -ArchivePath <x.zip> -DestinationDirectory <new dir> [-ChecksumsPath ...]
#            Both check, before anything is written: the archive hash against SHA256SUMS, safe relative entry names
#            without case-insensitive duplicates, manifest.json present and well formed, every entry listed in the
#            manifest with matching size and SHA-256, nothing listed missing, ordinal entry order; then they expand into
#            a staging directory that must pass package-verify (forbidden content, VERSION) before it is renamed into
#            place (Extract) or deleted (Verify). The destination must not exist.
# Refusals are typed: stderr 'package-archive refused: <code>: <detail>' and exit 1. Codes: package_invalid,
# archive_exists, checksums_exists, archive_inside_package, archive_missing, destination_exists, checksums_missing,
# checksums_invalid, checksum_mismatch, archive_unreadable, entry_path_unsafe, entry_duplicate, manifest_missing,
# manifest_invalid, entry_unlisted, entry_missing, entry_mismatch, entry_order.
# The manifest format is unchanged; SHA256SUMS only proves transport integrity (no signing).
[CmdletBinding(DefaultParameterSetName = 'Create')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Create')][string]$PackageDirectory,
    [Parameter(Mandatory, ParameterSetName = 'Extract')][switch]$Extract,
    [Parameter(Mandatory, ParameterSetName = 'Extract')][string]$DestinationDirectory,
    [Parameter(Mandatory, ParameterSetName = 'Verify')][switch]$Verify,
    [Parameter(Mandatory)][string]$ArchivePath,
    [string]$ChecksumsPath
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/package-common.ps1"
Add-Type -AssemblyName System.IO.Compression

$script:EntryTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
$script:EntryAttributes = -2119958528   # 0x81A40000: S_IFREG | 0644 in the upper 16 bits
$script:ManifestLimit = 16MB
$script:Refusal = $null
$script:Disposables = [Collections.Generic.List[IDisposable]]::new()
$script:Cleanup = [Collections.Generic.List[string]]::new()

function Stop-ArchiveRefused([string]$Code, [string]$Detail) {
    $script:Refusal = [pscustomobject]@{ Code = $Code; Detail = $Detail }
    throw "package-archive refused: ${Code}: $Detail"
}
function Add-Disposable($Value) { $script:Disposables.Add($Value); return $Value }
function ConvertTo-Hex([byte[]]$Bytes) { return [Convert]::ToHexString($Bytes).ToLowerInvariant() }

# Relative, forward-slash, no empty/./.. segments, no drive, colon, backslash, control or Windows-reserved characters,
# no segment ending in '.' or ' ' (Windows would silently rename it).
function Test-SafeEntryName([string]$Name) {
    if (!$Name -or $Name.StartsWith('/') -or $Name -match '[\x00-\x1f\\:*?"<>|]') { return $false }
    foreach ($segment in $Name.Split('/')) {
        if ($segment -ceq '' -or $segment -ceq '.' -or $segment -ceq '..' -or $segment.EndsWith('.') -or $segment.EndsWith(' ')) { return $false }
    }
    return $true
}

function Assert-ArchiveChecksum([IO.Stream]$Stream, [string]$Name, [string]$Sums) {
    if (!(Test-Path -LiteralPath $Sums -PathType Leaf)) { Stop-ArchiveRefused 'checksums_missing' "no checksum file at $Sums" }
    $name = $Name
    $expected = $null
    foreach ($raw in [IO.File]::ReadAllText($Sums).Split("`n")) {
        $line = $raw.TrimEnd("`r")
        if ($line -ceq '') { continue }
        if ($line -cnotmatch '\A([0-9a-f]{64}) [ *](.+)\z') { Stop-ArchiveRefused 'checksums_invalid' "malformed line in ${Sums}: '$line'" }
        if ($Matches[2] -ceq $name) {
            if ($expected) { Stop-ArchiveRefused 'checksums_invalid' "$name listed twice in $Sums" }
            $expected = $Matches[1]
        }
    }
    if (!$expected) { Stop-ArchiveRefused 'checksums_missing' "$name is not listed in $Sums" }
    $sha = Add-Disposable ([Security.Cryptography.SHA256]::Create())
    $actual = ConvertTo-Hex $sha.ComputeHash($Stream)
    if ($actual -cne $expected) { Stop-ArchiveRefused 'checksum_mismatch' "$name sha256 $actual != SHA256SUMS $expected" }
}

# Reads at most Limit+1 bytes so a lying or oversized entry cannot exhaust memory or time.
function Get-EntryDigest([IO.Compression.ZipArchiveEntry]$Entry, [long]$Limit) {
    $sha = Add-Disposable ([Security.Cryptography.SHA256]::Create())
    $stream = $Entry.Open()
    $buffer = [byte[]]::new(81920)
    $total = [long]0
    try {
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $total += $read
            if ($total -gt $Limit) { break }
            [void]$sha.TransformBlock($buffer, 0, $read, $null, 0)
        }
        [void]$sha.TransformFinalBlock([byte[]]::new(0), 0, 0)
    } finally { $stream.Dispose() }
    return [pscustomobject]@{ Length = $total; Hash = (ConvertTo-Hex $sha.Hash) }
}

# Checks every entry against the packaged manifest.json; returns the manifest file rows (path, size, sha256).
function Test-ArchiveEntries([IO.Compression.ZipArchive]$Zip) {
    $names = [Collections.Generic.List[string]]::new()
    $byName = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new([StringComparer]::Ordinal)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Zip.Entries) {
        $name = $entry.FullName
        if (!(Test-SafeEntryName $name)) { Stop-ArchiveRefused 'entry_path_unsafe' "unsafe entry name '$name'" }
        if (!$seen.Add($name)) { Stop-ArchiveRefused 'entry_duplicate' "duplicate entry (case-insensitive) '$name'" }
        $byName[$name] = $entry
        $names.Add($name)
    }
    if (!$byName.ContainsKey('manifest.json')) { Stop-ArchiveRefused 'manifest_missing' 'archive has no manifest.json entry' }
    $manifestEntry = $byName['manifest.json']
    $digest = Get-EntryDigest $manifestEntry $script:ManifestLimit
    if ($digest.Length -gt $script:ManifestLimit) { Stop-ArchiveRefused 'manifest_invalid' 'manifest.json is larger than 16 MiB' }
    $reader = [IO.StreamReader]::new($manifestEntry.Open(), [Text.UTF8Encoding]::new($false, $true))
    try { $text = $reader.ReadToEnd() } catch { Stop-ArchiveRefused 'manifest_invalid' "manifest.json is not UTF-8: $($_.Exception.Message)" } finally { $reader.Dispose() }
    try { $manifest = $text | ConvertFrom-Json -ErrorAction Stop } catch { Stop-ArchiveRefused 'manifest_invalid' "manifest.json is not JSON: $($_.Exception.Message)" }
    if ($manifest -isnot [Management.Automation.PSCustomObject] -or $manifest.schema -cne $script:PackageManifestSchema) { Stop-ArchiveRefused 'manifest_invalid' "manifest.json schema is not $script:PackageManifestSchema" }
    if ($manifest.files -isnot [Array]) { Stop-ArchiveRefused 'manifest_invalid' 'manifest.json files is not an array' }
    $rows = [Collections.Generic.List[object]]::new()
    $listed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in $manifest.files) {
        $path = [string]$file.path
        $hash = [string]$file.sha256
        if (!(Test-SafeEntryName $path) -or $path -ceq 'manifest.json') { Stop-ArchiveRefused 'manifest_invalid' "manifest path not safe: '$path'" }
        if (!$listed.Add($path)) { Stop-ArchiveRefused 'manifest_invalid' "duplicate manifest path: $path" }
        if ($file.size -isnot [long] -and $file.size -isnot [int] -or [long]$file.size -lt 0) { Stop-ArchiveRefused 'manifest_invalid' "manifest size for $path is not a non-negative integer" }
        if ($hash -cnotmatch '\A[0-9a-f]{64}\z') { Stop-ArchiveRefused 'manifest_invalid' "manifest sha256 for $path is not 64 lowercase hex" }
        $rows.Add([pscustomobject]@{ Path = $path; Size = [long]$file.size; Sha256 = $hash })
    }
    foreach ($name in $names) { if ($name -cne 'manifest.json' -and !$listed.Contains($name)) { Stop-ArchiveRefused 'entry_unlisted' "entry not in manifest.json: $name" } }
    foreach ($row in $rows) {
        if (!$byName.ContainsKey($row.Path)) { Stop-ArchiveRefused 'entry_missing' "manifest file has no archive entry: $($row.Path)" }
        $entry = $byName[$row.Path]
        if ($entry.Length -ne $row.Size) { Stop-ArchiveRefused 'entry_mismatch' "size of $($row.Path) is $($entry.Length), manifest says $($row.Size)" }
        $actual = Get-EntryDigest $entry $row.Size
        if ($actual.Length -ne $row.Size) { Stop-ArchiveRefused 'entry_mismatch' "decompressed size of $($row.Path) differs from manifest $($row.Size)" }
        if ($actual.Hash -cne $row.Sha256) { Stop-ArchiveRefused 'entry_mismatch' "sha256 of $($row.Path) is $($actual.Hash), manifest says $($row.Sha256)" }
    }
    $sorted = $names.ToArray(); [Array]::Sort($sorted, [StringComparer]::Ordinal)
    if (($sorted -join "`n") -cne ($names -join "`n")) { Stop-ArchiveRefused 'entry_order' 'archive entries are not in ordinal path order' }
    return , $rows.ToArray()
}

function Open-VerifiedArchive([string]$Archive, [string]$Sums, [string]$ListedName) {
    if (!$ListedName) { $ListedName = [IO.Path]::GetFileName($Archive) }
    $stream = Add-Disposable ([IO.FileStream]::new($Archive, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
    Assert-ArchiveChecksum $stream $ListedName $Sums
    $stream.Position = 0
    try {
        $zip = Add-Disposable ([IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true))
        $rows = Test-ArchiveEntries $zip
    } catch {
        if ($script:Refusal) { throw }
        Stop-ArchiveRefused 'archive_unreadable' "$Archive is not a readable zip: $($_.Exception.Message)"
    }
    return [pscustomobject]@{ Zip = $zip; Rows = $rows }
}

function Expand-VerifiedArchive($Opened, [string]$Target) {
    [void][IO.Directory]::CreateDirectory($Target)
    $paths = [string[]]@(@($Opened.Rows | ForEach-Object { $_.Path }) + 'manifest.json')
    foreach ($path in $paths) {
        $entry = $Opened.Zip.GetEntry($path)
        $full = Join-Path $Target $path
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full))
        $in = $entry.Open()
        try {
            $out = [IO.FileStream]::new($full, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $in.CopyTo($out) } finally { $out.Dispose() }
        } finally { $in.Dispose() }
    }
    $problems = @(Test-PackageManifest $Target)
    if ($problems.Count) { Stop-ArchiveRefused 'package_invalid' ("extracted package fails package-verify: " + ($problems -join '; ')) }
}

$exitCode = 0
$result = $null
$id = [Guid]::NewGuid().ToString('N')
try {
    $archive = [IO.Path]::GetFullPath($ArchivePath)
    $sums = if ($ChecksumsPath) { [IO.Path]::GetFullPath($ChecksumsPath) } else { Join-Path ([IO.Path]::GetDirectoryName($archive)) 'SHA256SUMS' }
    if ($PSCmdlet.ParameterSetName -eq 'Create') {
        $package = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        if (!(Test-Path -LiteralPath $package -PathType Container)) { Stop-ArchiveRefused 'package_invalid' "not a directory: $package" }
        $boundary = $package + [IO.Path]::DirectorySeparatorChar
        foreach ($path in $archive, $sums) { if ($path.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { Stop-ArchiveRefused 'archive_inside_package' "$path is inside the package folder" } }
        $problems = @(Test-PackageManifest $package)
        if ($problems.Count) { Stop-ArchiveRefused 'package_invalid' ("package fails package-verify: " + ($problems -join '; ')) }
        if (Test-Path -LiteralPath $archive) { Stop-ArchiveRefused 'archive_exists' "$archive already exists; archiving never overwrites" }
        if (Test-Path -LiteralPath $sums) { Stop-ArchiveRefused 'checksums_exists' "$sums already exists; archiving never overwrites" }
        $manifest = [IO.File]::ReadAllText((Join-Path $package 'manifest.json')) | ConvertFrom-Json
        $paths = [string[]]@(@($manifest.files | ForEach-Object { [string]$_.path }) + 'manifest.json')
        [Array]::Sort($paths, [StringComparer]::Ordinal)

        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($archive))
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($sums))
        $partialZip = "$archive.partial-$id"
        $partialSums = "$sums.partial-$id"
        $script:Cleanup.Add($partialZip); $script:Cleanup.Add($partialSums)
        $stream = [IO.FileStream]::new($partialZip, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try {
            $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
            try {
                foreach ($path in $paths) {
                    $entry = $zip.CreateEntry($path, [IO.Compression.CompressionLevel]::Optimal)
                    $entry.LastWriteTime = $script:EntryTime
                    $entry.ExternalAttributes = $script:EntryAttributes
                    $out = $entry.Open()
                    try {
                        $in = [IO.File]::OpenRead((Join-Path $package $path))
                        try { $in.CopyTo($out) } finally { $in.Dispose() }
                    } finally { $out.Dispose() }
                }
            } finally { $zip.Dispose() }
        } finally { $stream.Dispose() }

        $hashBytes = [Security.Cryptography.SHA256]::Create()
        $readBack = [IO.File]::OpenRead($partialZip)
        try { $hash = ConvertTo-Hex $hashBytes.ComputeHash($readBack) } finally { $readBack.Dispose(); $hashBytes.Dispose() }
        Write-PackageText $partialSums ($hash + '  ' + [IO.Path]::GetFileName($archive) + "`n")
        # Self-check: the staged archive must pass the same verification an installer applies.
        $opened = Open-VerifiedArchive $partialZip $partialSums ([IO.Path]::GetFileName($archive))
        if (@($opened.Rows).Count + 1 -ne $paths.Length) { throw 'staged archive entry count differs from the package manifest' }
        foreach ($item in @($script:Disposables)) { $item.Dispose() }
        $script:Disposables.Clear()
        if (Test-Path -LiteralPath $archive) { Stop-ArchiveRefused 'archive_exists' "$archive appeared during archiving" }
        [IO.File]::Move($partialZip, $archive)
        try { [IO.File]::Move($partialSums, $sums) }
        catch { [IO.File]::Delete($archive); throw }
        $result = "ARCHIVED $archive sha256 $hash ($($paths.Length) entries) checksums $sums"
    } else {
        if (!(Test-Path -LiteralPath $archive -PathType Leaf)) { Stop-ArchiveRefused 'archive_missing' "no archive at $archive" }
        if ($Extract) {
            $destination = [IO.Path]::GetFullPath($DestinationDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
            if (Test-Path -LiteralPath $destination) { Stop-ArchiveRefused 'destination_exists' "$destination already exists; extraction never overwrites" }
        }
        $opened = Open-VerifiedArchive $archive $sums
        if ($Extract) {
            $parent = [IO.Path]::GetDirectoryName($destination)
            $staging = "$destination.partial-$id"
            $script:Cleanup.Add($staging)
            [void][IO.Directory]::CreateDirectory($parent)
            Expand-VerifiedArchive $opened $staging
            if (Test-Path -LiteralPath $destination) { Stop-ArchiveRefused 'destination_exists' "$destination appeared during extraction" }
            [IO.Directory]::Move($staging, $destination)
            $result = "EXTRACTED $archive -> $destination (verified against SHA256SUMS and manifest.json)"
        } else {
            $staging = Join-Path ([IO.Path]::GetTempPath()) "bim-archive-verify-$id"
            $script:Cleanup.Add($staging)
            Expand-VerifiedArchive $opened $staging
            $result = "VERIFIED $archive (SHA256SUMS, manifest.json entries, package-verify)"
        }
    }
} catch {
    if ($script:Refusal) { [Console]::Error.WriteLine("package-archive refused: $($script:Refusal.Code): $($script:Refusal.Detail)") }
    else { [Console]::Error.WriteLine("package-archive failed: $($_.Exception.Message)") }
    $exitCode = 1
} finally {
    for ($i = $script:Disposables.Count - 1; $i -ge 0; $i--) { try { $script:Disposables[$i].Dispose() } catch { } }
    foreach ($path in $script:Cleanup) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
if ($exitCode) { exit $exitCode }
$result
