#Requires -Version 7.2
# P10-08 release archive helpers, dot-sourced by scripts/package-archive.ps1 and tests/packaging (test seam).
# Defines functions and script-scoped state only; nothing runs on load.
. "$PSScriptRoot/package-common.ps1"
Add-Type -AssemblyName System.IO.Compression

$script:EntryTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
$script:EntryAttributes = -2119958528   # 0x81A40000: S_IFREG | 0644 in the upper 16 bits
$script:ManifestLimit = 16MB
$script:Refusal = $null
$script:Disposables = [Collections.Generic.List[IDisposable]]::new()
$script:Cleanup = [Collections.Generic.List[string]]::new()
$script:CreatedDirectories = [Collections.Generic.List[string]]::new()
# Deflate output depends on the runtime's zlib (.NET 9 moved to zlib-ng), so byte-identity holds per OS + runtime.
$script:RuntimeText = "pwsh $($PSVersionTable.PSVersion) .NET $([Environment]::Version) $([Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier)"

function Stop-ArchiveRefused([string]$Code, [string]$Detail) {
    $script:Refusal = [pscustomobject]@{ Code = $Code; Detail = $Detail }
    throw "package-archive refused: ${Code}: $Detail"
}
function Add-Disposable($Value) { $script:Disposables.Add($Value); return $Value }
function ConvertTo-Hex([byte[]]$Bytes) { return [Convert]::ToHexString($Bytes).ToLowerInvariant() }
# Relative paths resolve against the PowerShell location (Set-Location/Push-Location), not the process directory.
function Resolve-ArchivePath([string]$Path) { [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)) }
# Creates a directory and records every level it had to create, so a refusal can remove them again.
function New-TrackedDirectory([string]$Path) {
    $cursor = $Path
    $missing = [Collections.Generic.List[string]]::new()
    while ($cursor -and !(Test-Path -LiteralPath $cursor)) { $missing.Add($cursor); $cursor = [IO.Path]::GetDirectoryName($cursor) }
    [void][IO.Directory]::CreateDirectory($Path)
    foreach ($directory in $missing) { $script:CreatedDirectories.Add($directory) }
}

# Relative, forward-slash, no empty/./.. segments (so no directory entries), no drive, colon, backslash, control or
# Windows-invalid characters, no segment ending in '.' or ' ' (Windows would silently rename it) and no Windows
# reserved device name (CON, PRN, AUX, NUL, COM1-9, LPT1-9, CONIN$, CONOUT$) with or without an extension.
function Test-SafeEntryName([string]$Name) {
    if (!$Name -or $Name.StartsWith('/') -or $Name -match '[\x00-\x1f\\:*?"<>|]') { return $false }
    foreach ($segment in $Name.Split('/')) {
        if ($segment -ceq '' -or $segment -ceq '.' -or $segment -ceq '..' -or $segment.EndsWith('.') -or $segment.EndsWith(' ')) { return $false }
        $stem = $segment.Split('.')[0].TrimEnd(' ')
        if ([regex]::IsMatch($stem, '\A(CON|PRN|AUX|NUL|COM[1-9\u00B9\u00B2\u00B3]|LPT[1-9\u00B9\u00B2\u00B3]|CONIN\$|CONOUT\$)\z', [Text.RegularExpressions.RegexOptions]'IgnoreCase, CultureInvariant')) { return $false }
    }
    return $true
}

function Assert-ArchiveChecksum([string]$Actual, [string]$Name, [string]$Sums) {
    if (!(Test-Path -LiteralPath $Sums -PathType Leaf)) { Stop-ArchiveRefused 'checksums_missing' "no checksum file at $Sums" }
    $expected = $null
    foreach ($raw in [IO.File]::ReadAllText($Sums).Split("`n")) {
        $line = $raw.TrimEnd("`r")
        if ($line -ceq '') { continue }
        if ($line -cnotmatch '\A([0-9a-f]{64}) [ *](.+)\z') { Stop-ArchiveRefused 'checksums_invalid' "malformed line in ${Sums}: '$line'" }
        if ($Matches[2] -ceq $Name) {
            if ($expected) { Stop-ArchiveRefused 'checksums_invalid' "$Name listed twice in $Sums" }
            $expected = $Matches[1]
        }
    }
    if (!$expected) { Stop-ArchiveRefused 'checksums_missing' "$Name is not listed in $Sums" }
    if ($Actual -cne $expected) { Stop-ArchiveRefused 'checksum_mismatch' "$Name sha256 $Actual != SHA256SUMS $expected" }
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

# Returns the entry bytes, or $null when it is longer than Limit.
function Read-EntryBytes([IO.Compression.ZipArchiveEntry]$Entry, [long]$Limit) {
    $memory = [IO.MemoryStream]::new()
    $stream = $Entry.Open()
    $buffer = [byte[]]::new(81920)
    try {
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($memory.Length + $read -gt $Limit) { return $null }
            $memory.Write($buffer, 0, $read)
        }
    } finally { $stream.Dispose() }
    return , $memory.ToArray()
}

# Checks every entry against the packaged manifest.json; returns rows (path, size, sha256) for every entry,
# manifest.json last (its digest from the single read the manifest was parsed from).
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
    $folders = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $names) {
        $parts = $name.Split('/')
        for ($i = 1; $i -lt $parts.Length; $i++) { [void]$folders.Add(($parts[0..($i - 1)] -join '/')) }
    }
    foreach ($name in $names) { if ($folders.Contains($name)) { Stop-ArchiveRefused 'entry_duplicate' "'$name' is both a file and a folder (case-insensitive)" } }
    if (!$byName.ContainsKey('manifest.json')) { Stop-ArchiveRefused 'manifest_missing' 'archive has no manifest.json entry' }
    $manifestBytes = Read-EntryBytes $byName['manifest.json'] $script:ManifestLimit
    if ($null -eq $manifestBytes) { Stop-ArchiveRefused 'manifest_invalid' 'manifest.json is larger than 16 MiB' }
    $manifestSha = Add-Disposable ([Security.Cryptography.SHA256]::Create())
    $manifestRow = [pscustomobject]@{ Path = 'manifest.json'; Size = [long]$manifestBytes.Length; Sha256 = (ConvertTo-Hex $manifestSha.ComputeHash($manifestBytes)) }
    try { $text = [Text.UTF8Encoding]::new($false, $true).GetString($manifestBytes) } catch { Stop-ArchiveRefused 'manifest_invalid' "manifest.json is not UTF-8: $($_.Exception.Message)" }
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
    $rows.Add($manifestRow)
    return , $rows.ToArray()
}

# Copies the archive into a private temp directory while hashing it, checks that hash against SHA256SUMS and
# verifies the entries of the copy, so later changes to the original cannot reach the extraction.
function Open-VerifiedArchive([string]$Archive, [string]$Sums, [string]$ListedName) {
    if (!$ListedName) { $ListedName = [IO.Path]::GetFileName($Archive) }
    $private = Join-Path ([IO.Path]::GetTempPath()) ('bim-archive-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($private)
    $script:Cleanup.Add($private)
    $copyPath = Join-Path $private 'archive.zip'
    $sha = Add-Disposable ([Security.Cryptography.SHA256]::Create())
    $buffer = [byte[]]::new(81920)
    $source = [IO.FileStream]::new($Archive, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $copy = [IO.FileStream]::new($copyPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                [void]$sha.TransformBlock($buffer, 0, $read, $null, 0)
                $copy.Write($buffer, 0, $read)
            }
            [void]$sha.TransformFinalBlock([byte[]]::new(0), 0, 0)
        } finally { $copy.Dispose() }
    } finally { $source.Dispose() }
    Assert-ArchiveChecksum (ConvertTo-Hex $sha.Hash) $ListedName $Sums
    $stream = Add-Disposable ([IO.FileStream]::new($copyPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
    try {
        $zip = Add-Disposable ([IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true))
        $rows = Test-ArchiveEntries $zip
    } catch {
        if ($script:Refusal) { throw }
        Stop-ArchiveRefused 'archive_unreadable' "$Archive is not a readable zip: $($_.Exception.Message)"
    }
    return [pscustomobject]@{ Zip = $zip; Rows = $rows; Private = $private }
}

# Copies one entry, size-bounded and re-hashed, and refuses it unless it equals the row captured at verification.
function Copy-VerifiedEntry([IO.Compression.ZipArchiveEntry]$Entry, $Row, [string]$Full) {
    $sha = Add-Disposable ([Security.Cryptography.SHA256]::Create())
    $buffer = [byte[]]::new(81920)
    $total = [long]0
    $in = $Entry.Open()
    try {
        $out = [IO.FileStream]::new($Full, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            while (($read = $in.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $total += $read
                if ($total -gt $Row.Size) { Stop-ArchiveRefused 'entry_mismatch' "$($Row.Path) is longer than the verified $($Row.Size) bytes" }
                [void]$sha.TransformBlock($buffer, 0, $read, $null, 0)
                $out.Write($buffer, 0, $read)
            }
            [void]$sha.TransformFinalBlock([byte[]]::new(0), 0, 0)
        } finally { $out.Dispose() }
    } finally { $in.Dispose() }
    $hash = ConvertTo-Hex $sha.Hash
    if ($total -ne $Row.Size -or $hash -cne $Row.Sha256) { Stop-ArchiveRefused 'entry_mismatch' "$($Row.Path) changed after verification (sha256 $hash, size $total)" }
}

function Expand-VerifiedArchive($Opened, [string]$Target) {
    [void][IO.Directory]::CreateDirectory($Target)
    try {
        foreach ($row in @($Opened.Rows)) {
            $entry = $Opened.Zip.GetEntry($row.Path)
            if (!$entry) { Stop-ArchiveRefused 'entry_missing' "verified entry disappeared: $($row.Path)" }
            $full = Join-Path $Target $row.Path
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full))
            Copy-VerifiedEntry $entry $row $full
        }
    } catch {
        if ($script:Refusal) { throw }
        Stop-ArchiveRefused 'archive_unreadable' "archive entry could not be read: $($_.Exception.Message)"
    }
    $problems = @(Test-PackageManifest $Target)
    if ($problems.Count) { Stop-ArchiveRefused 'package_invalid' ("extracted package fails package-verify: " + ($problems -join '; ')) }
}

# Disposes open archive streams and removes staging paths, newest first; after a failure also removes the
# directories this run created, deepest first, when they are empty.
function Close-ArchiveState([switch]$Failed) {
    for ($i = $script:Disposables.Count - 1; $i -ge 0; $i--) { try { $script:Disposables[$i].Dispose() } catch { } }
    $script:Disposables.Clear()
    foreach ($path in $script:Cleanup) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
    }
    $script:Cleanup.Clear()
    if ($Failed) {
        foreach ($directory in @($script:CreatedDirectories | Sort-Object -Property Length -Descending)) {
            if ((Test-Path -LiteralPath $directory -PathType Container) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) {
                Remove-Item -LiteralPath $directory -Force -ErrorAction SilentlyContinue
            }
        }
    }
    $script:CreatedDirectories.Clear()
}
