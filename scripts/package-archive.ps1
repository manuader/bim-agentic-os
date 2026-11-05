#Requires -Version 7.2
# P10-08 release archive over a P10-04 release folder (scripts/package.ps1 output). Built-in .NET only.
#   Create:  -PackageDirectory <release> -ArchivePath <x.zip> [-ChecksumsPath <SHA256SUMS>]
#            Refuses a folder that fails package-verify. Writes a deterministic zip: entries are manifest.json plus every
#            manifest-listed file, in ordinal path order, each with a fixed timestamp (2000-01-01T00:00:00), fixed
#            external attributes (regular file 0644) and Optimal deflate. Then writes SHA256SUMS ('<sha256>  <name>' + LF,
#            default next to the archive). Never overwrites.
#   Verify:  -Verify -ArchivePath <x.zip> [-ChecksumsPath ...]
#   Extract: -Extract -ArchivePath <x.zip> -DestinationDirectory <new dir> [-ChecksumsPath ...]
#            Both check, before anything is written: the archive hash against SHA256SUMS, safe relative entry names
#            without case-insensitive duplicates, manifest.json present and well formed, every entry listed in the
#            manifest with matching size and SHA-256, nothing listed missing, ordinal entry order; then they expand into
#            a staging directory that must pass package-verify (forbidden content, VERSION) before it is renamed into
#            place (Extract) or deleted (Verify). The destination must not exist.
#            The archive is first copied (and hashed) into a private temp directory; verification and extraction read
#            only that copy, and every entry is re-hashed and size-bounded while it is written, against the rows
#            captured at verification (manifest.json included), so a rewrite between verify and copy is refused.
#            Entry names must also be portable: no file/folder clash (case-insensitive), no Windows reserved device
#            names (CON, PRN, AUX, NUL, COM1-9, LPT1-9, CONIN$, CONOUT$, any extension), no directory entries.
#            The -Extract output is a verified P10-04 release folder: pass it to scripts/install-addin.ps1
#            -PackageDirectory (which runs package-verify again) to install the add-in.
# Relative paths resolve against the PowerShell location. Directories created by a refused run are removed.
# Reproducibility: byte-identical for the same release folder on the same OS with the same pwsh/.NET runtime
# (deflate output changed between runtimes, e.g. .NET 9 zlib-ng); create prints the runtime it used. Time zone and
# culture do not affect the bytes. Optimal deflate is kept because the archive mostly carries assemblies.
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
. "$PSScriptRoot/package-archive-common.ps1"

$exitCode = 0
$result = $null
$id = [Guid]::NewGuid().ToString('N')
try {
    $archive = Resolve-ArchivePath $ArchivePath
    $sums = if ($ChecksumsPath) { Resolve-ArchivePath $ChecksumsPath } else { Join-Path ([IO.Path]::GetDirectoryName($archive)) 'SHA256SUMS' }
    if ($PSCmdlet.ParameterSetName -eq 'Create') {
        $package = (Resolve-ArchivePath $PackageDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
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

        New-TrackedDirectory ([IO.Path]::GetDirectoryName($archive))
        New-TrackedDirectory ([IO.Path]::GetDirectoryName($sums))
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
        if (@($opened.Rows).Count -ne $paths.Length) { throw 'staged archive entry count differs from the package manifest' }
        foreach ($item in @($script:Disposables)) { $item.Dispose() }
        $script:Disposables.Clear()
        if (Test-Path -LiteralPath $archive) { Stop-ArchiveRefused 'archive_exists' "$archive appeared during archiving" }
        [IO.File]::Move($partialZip, $archive)
        try { [IO.File]::Move($partialSums, $sums) }
        catch { [IO.File]::Delete($archive); throw }
        $result = "ARCHIVED $archive sha256 $hash ($($paths.Length) entries) checksums $sums; byte-identical only for the same OS and runtime: $script:RuntimeText"
    } else {
        if (!(Test-Path -LiteralPath $archive -PathType Leaf)) { Stop-ArchiveRefused 'archive_missing' "no archive at $archive" }
        if ($Extract) {
            $destination = (Resolve-ArchivePath $DestinationDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
            if (Test-Path -LiteralPath $destination) { Stop-ArchiveRefused 'destination_exists' "$destination already exists; extraction never overwrites" }
        }
        $opened = Open-VerifiedArchive $archive $sums
        if ($Extract) {
            $parent = [IO.Path]::GetDirectoryName($destination)
            $staging = "$destination.partial-$id"
            $script:Cleanup.Add($staging)
            New-TrackedDirectory $parent
            Expand-VerifiedArchive $opened $staging
            if (Test-Path -LiteralPath $destination) { Stop-ArchiveRefused 'destination_exists' "$destination appeared during extraction" }
            [IO.Directory]::Move($staging, $destination)
            $result = "EXTRACTED $archive -> $destination (verified against SHA256SUMS and manifest.json)"
        } else {
            $staging = Join-Path $opened.Private 'package'
            Expand-VerifiedArchive $opened $staging
            $result = "VERIFIED $archive (SHA256SUMS, manifest.json entries, package-verify)"
        }
    }
} catch {
    if ($script:Refusal) { [Console]::Error.WriteLine("package-archive refused: $($script:Refusal.Code): $($script:Refusal.Detail)") }
    else { [Console]::Error.WriteLine("package-archive failed: $($_.Exception.Message)") }
    $exitCode = 1
} finally {
    Close-ArchiveState -Failed:($exitCode -ne 0)
}
if ($exitCode) { exit $exitCode }
$result
# Explicit success code so in-process callers (& script) do not read a stale $LASTEXITCODE.
exit 0
