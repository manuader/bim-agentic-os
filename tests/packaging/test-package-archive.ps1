#Requires -Version 7.2
# P10-08 release archive checks (plain assertions, no Pester, no dotnet needed for the synthetic cases).
# scripts/package-archive.ps1 zips a verified P10-04 release folder deterministically (fixed entry timestamps,
# ordinal entry order) and writes SHA256SUMS next to it; -Extract/-Verify refuse, with a typed code, an archive
# whose checksum or entries disagree with SHA256SUMS or the packaged manifest.json. Synthetic packages use the
# P10-04 manifest helpers; -PackageDirectory/-SecondPackageDirectory add checks against real packages built by
# scripts/package.ps1 (tests/packaging/test-package.ps1 passes its two clean builds).
# Failures are printed as 'FAIL ...' lines (and GitHub annotations) because CI logs are not downloadable.
param([string]$PackageDirectory, [string]$SecondPackageDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot)
$archiver = Join-Path $root 'scripts/package-archive.ps1'
$archiveCommon = Join-Path $root 'scripts/package-archive-common.ps1'
$common = Join-Path $root 'scripts/package-common.ps1'
$templateSource = Join-Path $root 'runtimes/revit-native/Bim.addin.template'
$pwsh = (Get-Process -Id $PID).Path
$failures = [Collections.Generic.List[string]]::new()
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { $failures.Add($Message) } }
function Complete {
    if ($failures.Count) {
        foreach ($failure in $failures) {
            $line = ($failure -replace '[\r\n]+', ' ')
            if ($line.Length -gt 900) { $line = $line.Substring(0, 900) }
            if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host ('::error title=package-archive::' + ($line -replace '%', '%25')) }
            Write-Host "FAIL $line"
        }
        Write-Error "package-archive checks failed: $($failures.Count) failure(s); first: $($failures[0])"
        exit 1
    }
    'PASS package-archive: deterministic zip (fixed timestamps, ordinal entries, byte-identical rebuilds), SHA256SUMS, typed refusals for checksum, entry, manifest and path tampering, verified extract'
    exit 0
}
function Get-Tail([string]$Text) { $t = $Text -replace '[\r\n]+', ' | '; if ($t.Length -gt 500) { $t.Substring($t.Length - 500) } else { $t } }
function Invoke-Archiver([string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    $output = & $pwsh -NoProfile -File $archiver @Arguments 2>&1 | ForEach-Object { "$_" }
    return [pscustomobject]@{ Code = $LASTEXITCODE; Output = ($output -join "`n") }
}
# Runs a script block with process environment variables overridden (child processes inherit them), then restores them.
function Invoke-WithEnvironment([hashtable]$Environment, [scriptblock]$Body) {
    $saved = @{}
    foreach ($name in @($Environment.Keys)) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process'); [Environment]::SetEnvironmentVariable($name, [string]$Environment[$name], 'Process') }
    try { return (& $Body) }
    finally { foreach ($name in @($saved.Keys)) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') } }
}
function Get-TempEnvironment([string]$Directory) { @{ TEMP = $Directory; TMP = $Directory; TMPDIR = $Directory } }
function Get-ArchiveTempLeftovers([string]$Directory) { @(Get-ChildItem -LiteralPath $Directory -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'bim-archive-*' }) }
function Get-B64([string]$Path) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($Path)) }
function Get-Bytes([string]$Text) { return , [Text.UTF8Encoding]::new($false).GetBytes($Text) }
function Get-Inventory([string]$Directory) {
    $paths = [string[]]@(@(Get-PackageFiles $Directory) + 'manifest.json')
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    return [string[]]@($paths | ForEach-Object { $_ + ' ' + (Get-FileHash -LiteralPath (Join-Path $Directory $_) -Algorithm SHA256).Hash.ToLowerInvariant() })
}
function Get-Partials([string]$Directory) { @(Get-ChildItem -LiteralPath $Directory -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*.partial-*' }) }

Add-Type -AssemblyName System.IO.Compression
$fixedTime = [DateTime]::new(2000, 1, 1, 0, 0, 0)

function New-SyntheticPackage([string]$Directory, [switch]$Forbidden) {
    foreach ($sub in 'revit', 'cli/sub') { New-Item -ItemType Directory -Force (Join-Path $Directory $sub) | Out-Null }
    Write-PackageText (Join-Path $Directory 'VERSION') "1.2.3`n"
    Write-PackageText (Join-Path $Directory 'revit/Bim.addin.template') (([IO.File]::ReadAllText($templateSource)) -replace "`r`n", "`n")
    Write-PackageText (Join-Path $Directory 'cli/a.txt') 'alpha'
    Write-PackageText (Join-Path $Directory 'cli/B.txt') 'bravo'
    Write-PackageText (Join-Path $Directory 'cli/sub/z.json') '{}'
    Write-PackageText (Join-Path $Directory '_first.txt') 'underscore sorts between upper and lower case'
    $binary = [byte[]]::new(70000)
    for ($i = 0; $i -lt $binary.Length; $i++) { $binary[$i] = [byte](($i * 31 + ($i -shr 7)) % 256) }
    [IO.File]::WriteAllBytes((Join-Path $Directory 'cli/data.bin'), $binary)
    if ($Forbidden) { Write-PackageText (Join-Path $Directory 'revit/RevitAPI.dll') 'x' }
    Write-PackageText (Join-Path $Directory 'manifest.json') (New-PackageManifestText $Directory '1.2.3' ('a' * 40) $false)
    return [IO.Path]::GetFullPath($Directory)
}
function Read-ZipEntries([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read)
        try {
            foreach ($entry in $zip.Entries) {
                $buffer = [IO.MemoryStream]::new()
                $entryStream = $entry.Open()
                try { $entryStream.CopyTo($buffer) } finally { $entryStream.Dispose() }
                [pscustomobject]@{ Name = $entry.FullName; Bytes = $buffer.ToArray(); Time = $entry.LastWriteTime.DateTime }
            }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
}
function Get-DirectoryEntries([string]$Directory) {
    foreach ($path in @(@(Get-PackageFiles $Directory) + 'manifest.json')) {
        [pscustomobject]@{ Name = $path; Bytes = [IO.File]::ReadAllBytes((Join-Path $Directory $path)); Time = $fixedTime }
    }
}
# Writes entries as a zip; sorted ordinally by name unless -KeepOrder.
function Write-TestZip([string]$Path, [object[]]$Entries, [switch]$KeepOrder) {
    $items = [object[]]@($Entries)
    if (!$KeepOrder) {
        $keys = [string[]]@($items | ForEach-Object { $_.Name })
        [Array]::Sort($keys, [StringComparer]::Ordinal)
        $used = [Collections.Generic.HashSet[int]]::new()
        $items = [object[]]@(foreach ($key in $keys) {
                for ($i = 0; $i -lt $items.Length; $i++) { if (!$used.Contains($i) -and $items[$i].Name -ceq $key) { [void]$used.Add($i); $items[$i]; break } }
            })
    }
    New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($item in $items) {
                $entry = $zip.CreateEntry($item.Name, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new($fixedTime, [TimeSpan]::Zero)
                $entryStream = $entry.Open()
                try { $entryStream.Write($item.Bytes, 0, $item.Bytes.Length) } finally { $entryStream.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
}
function Write-Sums([string]$ZipPath) {
    $hash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-PackageText (Join-Path (Split-Path $ZipPath) 'SHA256SUMS') ($hash + '  ' + (Split-Path $ZipPath -Leaf) + "`n")
}
function Set-Entry([object[]]$List, [string]$Name, [byte[]]$Bytes) {
    return [object[]]@($List | ForEach-Object { if ($_.Name -ceq $Name) { [pscustomobject]@{ Name = $_.Name; Bytes = $Bytes; Time = $_.Time } } else { $_ } })
}
function Add-Entry([object[]]$List, [string]$Name, [byte[]]$Bytes) { return [object[]](@($List) + [pscustomobject]@{ Name = $Name; Bytes = $Bytes; Time = $fixedTime }) }
function Remove-Entry([object[]]$List, [string]$Name) { return [object[]]@($List | Where-Object { $_.Name -cne $Name }) }

Assert (Test-Path -LiteralPath $archiver -PathType Leaf) 'scripts/package-archive.ps1 is missing'
Assert (Test-Path -LiteralPath $common -PathType Leaf) 'scripts/package-common.ps1 is missing'
Assert (Test-Path -LiteralPath $archiveCommon -PathType Leaf) 'scripts/package-archive-common.ps1 is missing'
if ($failures.Count) { Complete }
. $common

$work = Join-Path ([IO.Path]::GetTempPath()) ('bim archive test ' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $package = New-SyntheticPackage (Join-Path $work 'package')
    Assert (@(Test-PackageManifest $package).Count -eq 0) "synthetic package must verify: $(@(Test-PackageManifest $package) -join '; ')"
    $zipName = 'bim-agentic-os-1.2.3.zip'

    # --- Create: archive plus SHA256SUMS ---
    $zipA = Join-Path $work "release A/$zipName"
    New-Item -ItemType Directory -Force (Split-Path $zipA) | Out-Null
    $runA = Invoke-Archiver @('-PackageDirectory', $package, '-ArchivePath', $zipA)
    Assert ($runA.Code -eq 0) "archive create exit $($runA.Code): $(Get-Tail $runA.Output)"
    $sumsA = Join-Path (Split-Path $zipA) 'SHA256SUMS'
    Assert (Test-Path -LiteralPath $zipA -PathType Leaf) 'archive create did not write the archive'
    Assert (Test-Path -LiteralPath $sumsA -PathType Leaf) 'archive create did not write SHA256SUMS next to the archive'
    if ($failures.Count) { Complete }
    Assert ((Get-Partials (Split-Path $zipA)).Count -eq 0) 'archive create left a .partial file'
    # Deflate output is only stable for one runtime, so create must state the runtime it was produced with.
    Assert ($runA.Output.Contains("pwsh $($PSVersionTable.PSVersion)") -and $runA.Output.Contains(".NET $([Environment]::Version)")) "archive create must report its pwsh and .NET runtime versions: $(Get-Tail $runA.Output)"

    # --- Determinism: rebuild from the same folder and from a copy with different file timestamps ---
    $copy = Join-Path $work 'package copy'
    Copy-Item -LiteralPath $package -Destination $copy -Recurse
    foreach ($file in Get-ChildItem -LiteralPath $copy -Recurse -File -Force) { $file.LastWriteTimeUtc = [DateTime]::new(2031, 5, 6, 7, 8, 9, [DateTimeKind]::Utc) }
    foreach ($case in @(@{ Name = 'release B'; Source = $package }, @{ Name = 'release C'; Source = $copy })) {
        $zip = Join-Path $work "$($case.Name)/$zipName"
        New-Item -ItemType Directory -Force (Split-Path $zip) | Out-Null
        $run = Invoke-Archiver @('-PackageDirectory', $case.Source, '-ArchivePath', $zip)
        Assert ($run.Code -eq 0) "archive rebuild ($($case.Name)) exit $($run.Code): $(Get-Tail $run.Output)"
        if ($run.Code -ne 0) { continue }
        Assert ((Get-B64 $zip) -ceq (Get-B64 $zipA)) "archive rebuild ($($case.Name)) is not byte-identical"
        Assert ((Get-B64 (Join-Path (Split-Path $zip) 'SHA256SUMS')) -ceq (Get-B64 $sumsA)) "SHA256SUMS rebuild ($($case.Name)) is not byte-identical"
    }
    # Same bytes under a far-off time zone (UTC+14) and a Turkish culture (dotted/dotless i casing).
    $driver = Join-Path $work 'culture driver.ps1'
    Write-PackageText $driver @'
param([string]$Culture, [string]$Script, [string]$Package, [string]$Archive)
$info = [Globalization.CultureInfo]::GetCultureInfo($Culture)
[Globalization.CultureInfo]::CurrentCulture = $info
[Globalization.CultureInfo]::CurrentUICulture = $info
"culture $([Globalization.CultureInfo]::CurrentCulture.Name) tz $([TimeZoneInfo]::Local.Id)"
& $Script -PackageDirectory $Package -ArchivePath $Archive
exit $LASTEXITCODE
'@
    $zipD = Join-Path $work "release D/$zipName"
    New-Item -ItemType Directory -Force (Split-Path $zipD) | Out-Null
    $runD = Invoke-WithEnvironment @{ TZ = 'Pacific/Kiritimati' } {
        $ErrorActionPreference = 'Continue'
        $lines = & $pwsh -NoProfile -File $driver -Culture 'tr-TR' -Script $archiver -Package $package -Archive $zipD 2>&1 | ForEach-Object { "$_" }
        [pscustomobject]@{ Code = $LASTEXITCODE; Output = ($lines -join "`n") }
    }
    Assert ($runD.Code -eq 0) "archive rebuild under tr-TR and Pacific/Kiritimati exit $($runD.Code): $(Get-Tail $runD.Output)"
    Assert ($runD.Output -match 'culture tr-TR') "culture driver did not switch to tr-TR: $(Get-Tail $runD.Output)"
    if ($IsLinux) { Assert ($runD.Output -match 'tz Pacific/Kiritimati') "TZ=Pacific/Kiritimati was not applied: $(Get-Tail $runD.Output)" }
    if ($runD.Code -eq 0) {
        Assert ((Get-B64 $zipD) -ceq (Get-B64 $zipA)) 'archive rebuilt under tr-TR and Pacific/Kiritimati is not byte-identical'
        Assert ((Get-B64 (Join-Path (Split-Path $zipD) 'SHA256SUMS')) -ceq (Get-B64 $sumsA)) 'SHA256SUMS rebuilt under tr-TR and Pacific/Kiritimati is not byte-identical'
    }

    # --- Entries: ordinal order, fixed timestamp, exact bytes; SHA256SUMS format ---
    $entries = [object[]]@(Read-ZipEntries $zipA)
    $expected = [string[]]@(@(Get-PackageFiles $package) + 'manifest.json')
    [Array]::Sort($expected, [StringComparer]::Ordinal)
    $names = [string[]]@($entries | ForEach-Object { $_.Name })
    Assert (($names -join '|') -ceq ($expected -join '|')) "archive entries must be the package files plus manifest.json in ordinal order: [$($names -join ', ')] expected [$($expected -join ', ')]"
    foreach ($entry in $entries) {
        Assert ($entry.Time -eq $fixedTime) "entry $($entry.Name) timestamp $($entry.Time.ToString('o')) is not the fixed $($fixedTime.ToString('o'))"
        $source = Join-Path $package $entry.Name
        if (Test-Path -LiteralPath $source -PathType Leaf) { Assert ([Convert]::ToBase64String($entry.Bytes) -ceq (Get-B64 $source)) "entry $($entry.Name) bytes differ from the package file" }
    }
    $sumsBytes = [IO.File]::ReadAllBytes($sumsA)
    $hashA = (Get-FileHash -LiteralPath $zipA -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert ($sumsBytes.Length -gt 0 -and $sumsBytes[0] -ne 0xEF) 'SHA256SUMS must be UTF-8 without BOM'
    Assert ([Text.Encoding]::UTF8.GetString($sumsBytes) -ceq "$hashA  $zipName`n") "SHA256SUMS must be '<sha256>  <archive name>' with one LF: '$([Text.Encoding]::UTF8.GetString($sumsBytes))'"

    # --- Create refusals: existing archive, unverified package ---
    $before = Get-B64 $zipA
    $again = Invoke-Archiver @('-PackageDirectory', $package, '-ArchivePath', $zipA)
    Assert ($again.Code -ne 0) 'archive create overwrote an existing archive'
    Assert ($again.Output -match 'package-archive refused: archive_exists') "existing archive must be refused as archive_exists: $(Get-Tail $again.Output)"
    Assert ((Get-B64 $zipA) -ceq $before) 'refused create changed the existing archive'
    $tampered = Join-Path $work 'package tampered'
    Copy-Item -LiteralPath $package -Destination $tampered -Recurse
    Write-PackageText (Join-Path $tampered 'cli/a.txt') 'alphx'
    $badZip = Join-Path $work "release bad/$zipName"
    New-Item -ItemType Directory -Force (Split-Path $badZip) | Out-Null
    $bad = Invoke-Archiver @('-PackageDirectory', $tampered, '-ArchivePath', $badZip)
    Assert ($bad.Code -ne 0) 'archive create accepted a package that fails package-verify'
    Assert ($bad.Output -match 'package-archive refused: package_invalid') "unverified package must be refused as package_invalid: $(Get-Tail $bad.Output)"
    Assert (!(Test-Path -LiteralPath $badZip) -and !(Test-Path -LiteralPath (Join-Path (Split-Path $badZip) 'SHA256SUMS'))) 'refused create left an archive or SHA256SUMS'
    Assert ((Get-Partials (Split-Path $badZip)).Count -eq 0) 'refused create left a .partial file'

    # --- Verify and extract a good archive ---
    $goodTemp = Join-Path $work 'temp good'
    New-Item -ItemType Directory -Force $goodTemp | Out-Null
    $verify = Invoke-WithEnvironment (Get-TempEnvironment $goodTemp) { Invoke-Archiver @('-Verify', '-ArchivePath', $zipA) }
    Assert ($verify.Code -eq 0) "archive verify rejected a good archive: $(Get-Tail $verify.Output)"
    Assert ((Get-ArchiveTempLeftovers $goodTemp).Count -eq 0) 'archive verify left bim-archive-* in temp'
    $install = Join-Path $work 'install ok/package'
    $extract = Invoke-Archiver @('-Extract', '-ArchivePath', $zipA, '-DestinationDirectory', $install)
    Assert ($extract.Code -eq 0) "archive extract rejected a good archive: $(Get-Tail $extract.Output)"
    if ($extract.Code -eq 0) {
        Assert (@(Test-PackageManifest $install).Count -eq 0) "extracted package fails package-verify: $(@(Test-PackageManifest $install) -join '; ')"
        Assert (((Get-Inventory $install) -join '|') -ceq ((Get-Inventory $package) -join '|')) 'extracted package differs from the source package'
    }
    $existing = Join-Path $work 'install existing'
    New-Item -ItemType Directory -Force $existing | Out-Null
    $intoExisting = Invoke-Archiver @('-Extract', '-ArchivePath', $zipA, '-DestinationDirectory', $existing)
    Assert ($intoExisting.Code -ne 0) 'archive extract wrote into an existing directory'
    Assert ($intoExisting.Output -match 'package-archive refused: destination_exists') "existing destination must be refused as destination_exists: $(Get-Tail $intoExisting.Output)"
    Assert (@(Get-ChildItem -LiteralPath $existing -Force).Count -eq 0) 'refused extract left files in the existing destination'

    # --- Typed refusals: every case is refused by -Verify and -Extract, and nothing is installed ---
    $forbiddenPackage = New-SyntheticPackage (Join-Path $work 'package forbidden') -Forbidden
    $cases = @(
        @{ Name = 'stale checksum'; Code = 'checksum_mismatch'; Sums = 'stale'; Entries = { param($e) Set-Entry $e 'cli/a.txt' (Get-Bytes 'alphx') } },
        @{ Name = 'no checksums'; Code = 'checksums_missing'; Sums = 'none'; Entries = { param($e) $e } },
        @{ Name = 'malformed checksums'; Code = 'checksums_invalid'; Sums = 'malformed'; Entries = { param($e) $e } },
        @{ Name = 'entry same length'; Code = 'entry_mismatch'; Sums = 'fresh'; Entries = { param($e) Set-Entry $e 'cli/a.txt' (Get-Bytes 'alphx') } },
        @{ Name = 'entry other length'; Code = 'entry_mismatch'; Sums = 'fresh'; Entries = { param($e) Set-Entry $e 'cli/B.txt' (Get-Bytes 'bravo!!') } },
        @{ Name = 'binary entry'; Code = 'entry_mismatch'; Sums = 'fresh'; Entries = { param($e) $b = [byte[]]((@($e) | Where-Object { $_.Name -ceq 'cli/data.bin' }).Bytes.Clone()); $b[40000] = $b[40000] -bxor 1; Set-Entry $e 'cli/data.bin' $b } },
        @{ Name = 'unlisted entry'; Code = 'entry_unlisted'; Sums = 'fresh'; Entries = { param($e) Add-Entry $e 'cli/extra.txt' (Get-Bytes 'x') } },
        @{ Name = 'missing entry'; Code = 'entry_missing'; Sums = 'fresh'; Entries = { param($e) Remove-Entry $e 'cli/sub/z.json' } },
        @{ Name = 'missing manifest'; Code = 'manifest_missing'; Sums = 'fresh'; Entries = { param($e) Remove-Entry $e 'manifest.json' } },
        @{ Name = 'invalid manifest'; Code = 'manifest_invalid'; Sums = 'fresh'; Entries = { param($e) Set-Entry $e 'manifest.json' (Get-Bytes 'not json') } },
        @{ Name = 'parent path'; Code = 'entry_path_unsafe'; Sums = 'fresh'; Entries = { param($e) Add-Entry $e '../evil.txt' (Get-Bytes 'x') } },
        @{ Name = 'absolute path'; Code = 'entry_path_unsafe'; Sums = 'fresh'; Entries = { param($e) Add-Entry $e '/abs.txt' (Get-Bytes 'x') } },
        @{ Name = 'backslash path'; Code = 'entry_path_unsafe'; Sums = 'fresh'; Entries = { param($e) Add-Entry $e 'cli\evil.txt' (Get-Bytes 'x') } },
        @{ Name = 'drive-relative path'; Code = 'entry_path_unsafe'; Sums = 'fresh'; Entries = { param($e) Add-Entry $e 'C:evil.txt' (Get-Bytes 'x') } },
        @{ Name = 'dot segment'; Code = 'entry_path_unsafe'; Sums = 'fresh'; Entries = { param($e) Add-Entry $e 'cli/./a' (Get-Bytes 'x') } },
        @{ Name = 'trailing dot'; Code = 'entry_path_unsafe'; Sums = 'fresh'; Entries = { param($e) Add-Entry $e 'cli/a.' (Get-Bytes 'x') } },
        @{ Name = 'directory entry'; Code = 'entry_path_unsafe'; Sums = 'fresh'; Entries = { param($e) Add-Entry $e 'cli/' ([byte[]]::new(0)) } },
        @{ Name = 'reserved CON'; Code = 'entry_path_unsafe'; Sums = 'fresh'; Entries = { param($e) Add-Entry $e 'CON' (Get-Bytes 'x') } },
        @{ Name = 'reserved nul.txt'; Code = 'entry_path_unsafe'; Sums = 'fresh'; Entries = { param($e) Add-Entry $e 'cli/nul.txt' (Get-Bytes 'x') } },
        @{ Name = 'reserved Com1.json'; Code = 'entry_path_unsafe'; Sums = 'fresh'; Entries = { param($e) Add-Entry $e 'cli/sub/Com1.json' (Get-Bytes 'x') } },
        @{ Name = 'reserved lpt9.tar.gz'; Code = 'entry_path_unsafe'; Sums = 'fresh'; Entries = { param($e) Add-Entry $e 'lpt9.tar.gz' (Get-Bytes 'x') } },
        @{ Name = 'reserved AUX dir'; Code = 'entry_path_unsafe'; Sums = 'fresh'; Entries = { param($e) Add-Entry $e 'Aux/readme.txt' (Get-Bytes 'x') } },
        @{ Name = 'reserved PRN'; Code = 'entry_path_unsafe'; Sums = 'fresh'; Entries = { param($e) Add-Entry $e 'prn.dll' (Get-Bytes 'x') } },
        @{ Name = 'reserved conin$'; Code = 'entry_path_unsafe'; Sums = 'fresh'; Entries = { param($e) Add-Entry $e 'conin$' (Get-Bytes 'x') } },
        @{ Name = 'reserved CONOUT$.txt'; Code = 'entry_path_unsafe'; Sums = 'fresh'; Entries = { param($e) Add-Entry $e 'cli/CONOUT$.txt' (Get-Bytes 'x') } },
        @{ Name = 'file over folder'; Code = 'entry_duplicate'; Sums = 'fresh'; Entries = { param($e) Add-Entry $e 'CLI' (Get-Bytes 'x') } },
        @{ Name = 'folder over file'; Code = 'entry_duplicate'; Sums = 'fresh'; Entries = { param($e) Add-Entry $e 'cli/a.txt/inner' (Get-Bytes 'x') } },
        @{ Name = 'duplicate entry'; Code = 'entry_duplicate'; Sums = 'fresh'; Entries = { param($e) Add-Entry $e 'VERSION' (Get-Bytes "1.2.3`n") } },
        @{ Name = 'case duplicate'; Code = 'entry_duplicate'; Sums = 'fresh'; Entries = { param($e) Add-Entry $e 'cli/A.txt' (Get-Bytes 'alpha') } },
        @{ Name = 'reordered'; Code = 'entry_order'; Sums = 'fresh'; Order = 'reverse'; Entries = { param($e) $e } },
        @{ Name = 'forbidden listed'; Code = 'package_invalid'; Sums = 'fresh'; Entries = { param($e) @(Get-DirectoryEntries $forbiddenPackage) } },
        @{ Name = 'not a zip'; Code = 'archive_unreadable'; Sums = 'fresh'; Raw = 'this is not a zip archive' }
    )
    $index = 0
    foreach ($case in $cases) {
        $index++
        $caseDir = Join-Path $work "case $index"
        $zip = Join-Path $caseDir $zipName
        New-Item -ItemType Directory -Force $caseDir | Out-Null
        if ($case.Raw) { Write-PackageText $zip $case.Raw }
        else {
            $mutated = [object[]]@(& $case.Entries $entries)
            if ($case.Order -eq 'reverse') { [Array]::Reverse($mutated); Write-TestZip $zip $mutated -KeepOrder } else { Write-TestZip $zip $mutated }
        }
        switch ($case.Sums) {
            'fresh' { Write-Sums $zip }
            'stale' { Copy-Item -LiteralPath $sumsA -Destination (Join-Path $caseDir 'SHA256SUMS') }
            'malformed' { Write-PackageText (Join-Path $caseDir 'SHA256SUMS') "not-a-hash  $zipName`n" }
        }
        $caseTemp = Join-Path $caseDir 'temp'
        New-Item -ItemType Directory -Force $caseTemp | Out-Null
        $check = Invoke-WithEnvironment (Get-TempEnvironment $caseTemp) { Invoke-Archiver @('-Verify', '-ArchivePath', $zip) }
        Assert ((Get-ArchiveTempLeftovers $caseTemp).Count -eq 0) "refused verify ($($case.Name)) left bim-archive-* in temp: $((Get-ArchiveTempLeftovers $caseTemp).Name -join ', ')"
        Assert ($check.Code -ne 0) "archive verify accepted: $($case.Name)"
        Assert ($check.Output -match "package-archive refused: $($case.Code)") "archive verify ($($case.Name)) must refuse with $($case.Code): $(Get-Tail $check.Output)"
        $dest = Join-Path $caseDir 'install/package'
        $run = Invoke-WithEnvironment (Get-TempEnvironment $caseTemp) { Invoke-Archiver @('-Extract', '-ArchivePath', $zip, '-DestinationDirectory', $dest) }
        Assert ((Get-ArchiveTempLeftovers $caseTemp).Count -eq 0) "refused extract ($($case.Name)) left bim-archive-* in temp: $((Get-ArchiveTempLeftovers $caseTemp).Name -join ', ')"
        Assert ($run.Code -ne 0) "archive extract accepted: $($case.Name)"
        Assert ($run.Output -match "package-archive refused: $($case.Code)") "archive extract ($($case.Name)) must refuse with $($case.Code): $(Get-Tail $run.Output)"
        Assert (!(Test-Path -LiteralPath $dest)) "refused extract ($($case.Name)) created the destination"
        Assert (!(Test-Path -LiteralPath (Join-Path $caseDir 'install'))) "refused extract ($($case.Name)) left the destination parent it created (or a .partial in it)"
        foreach ($escape in (Join-Path $caseDir 'install/evil.txt'), (Join-Path $caseDir 'evil.txt'), (Join-Path $caseDir 'install/package/evil.txt')) {
            Assert (!(Test-Path -LiteralPath $escape)) "refused extract ($($case.Name)) wrote $escape"
        }
    }

    # --- Relative paths resolve against the PowerShell location, not the process working directory ---
    $relative = Join-Path $work 'relative base'
    New-Item -ItemType Directory -Force $relative | Out-Null
    Copy-Item -LiteralPath $package -Destination (Join-Path $relative 'pkg') -Recurse
    Push-Location -LiteralPath $relative
    try {
        $ErrorActionPreference = 'Continue'
        $relCreate = & $archiver -PackageDirectory 'pkg' -ArchivePath 'out/rel.zip' 2>&1 | ForEach-Object { "$_" }
        $relCreateCode = $LASTEXITCODE
        $relExtract = & $archiver -Extract -ArchivePath 'out/rel.zip' -DestinationDirectory 'installed/pkg' 2>&1 | ForEach-Object { "$_" }
        $relExtractCode = $LASTEXITCODE
        $relVerify = & $archiver -Verify -ArchivePath 'out/rel.zip' -ChecksumsPath 'out/SHA256SUMS' 2>&1 | ForEach-Object { "$_" }
        $relVerifyCode = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
    } finally { Pop-Location }
    Assert ($relCreateCode -eq 0) "relative-path create exit ${relCreateCode}: $(Get-Tail ($relCreate -join "`n"))"
    Assert ((Test-Path -LiteralPath (Join-Path $relative 'out/rel.zip') -PathType Leaf) -and (Test-Path -LiteralPath (Join-Path $relative 'out/SHA256SUMS') -PathType Leaf)) 'relative-path create did not write under the PowerShell location'
    Assert ($relExtractCode -eq 0) "relative-path extract exit ${relExtractCode}: $(Get-Tail ($relExtract -join "`n"))"
    Assert (Test-Path -LiteralPath (Join-Path $relative 'installed/pkg/manifest.json') -PathType Leaf) 'relative-path extract did not install under the PowerShell location'
    Assert ($relVerifyCode -eq 0) "relative-path verify exit ${relVerifyCode}: $(Get-Tail ($relVerify -join "`n"))"

    # --- Seam (dot-sourced helpers): an entry rewritten after verification is caught while copying ---
    . $archiveCommon
    $swapZip = Join-Path $work 'swap/rewritten.zip'
    Write-TestZip $swapZip (Set-Entry $entries 'cli/a.txt' (Get-Bytes 'alphx'))
    $script:Refusal = $null
    $message = ''
    $swapStream = $null; $swap = $null
    try {
        $opened = Open-VerifiedArchive $zipA $sumsA
        $swapStream = [IO.File]::OpenRead($swapZip)
        $swap = [IO.Compression.ZipArchive]::new($swapStream, [IO.Compression.ZipArchiveMode]::Read)
        $opened.Zip = $swap
        Expand-VerifiedArchive $opened (Join-Path $work 'swap install')
    } catch { $message = $_.Exception.Message }
    finally {
        if ($swap) { $swap.Dispose() }
        if ($swapStream) { $swapStream.Dispose() }
        Close-ArchiveState
    }
    Assert ($message -match 'package-archive refused: entry_mismatch') "entry data changed after verification must be refused as entry_mismatch while copying: '$message'"
    # Verification and extraction read a private copy, so rewriting the original afterwards changes nothing.
    $moving = Join-Path $work 'moving'
    New-Item -ItemType Directory -Force $moving | Out-Null
    Copy-Item -LiteralPath $zipA -Destination (Join-Path $moving $zipName)
    Copy-Item -LiteralPath $sumsA -Destination (Join-Path $moving 'SHA256SUMS')
    $script:Refusal = $null
    $message = ''
    $movedInstall = Join-Path $work 'moving install'
    try {
        $opened = Open-VerifiedArchive (Join-Path $moving $zipName) (Join-Path $moving 'SHA256SUMS')
        try { [IO.File]::WriteAllBytes((Join-Path $moving $zipName), [IO.File]::ReadAllBytes($swapZip)) }
        catch { $failures.Add("verified archive could not be rewritten (original still held open): $($_.Exception.Message)") }
        Expand-VerifiedArchive $opened $movedInstall
    } catch { $message = $_.Exception.Message }
    finally { Close-ArchiveState }
    Assert ($message -eq '') "extract from the private verified copy failed after the original was rewritten: '$message'"
    if ($message -eq '') { Assert (((Get-Inventory $movedInstall) -join '|') -ceq ((Get-Inventory $package) -join '|')) 'extract after the original was rewritten did not install the verified bytes' }

    # --- Real packages from scripts/package.ps1: archive, extract, byte-identical across two clean builds ---
    if ($PackageDirectory) {
        $realZip = Join-Path $work 'real A/bim-agentic-os.zip'
        New-Item -ItemType Directory -Force (Split-Path $realZip) | Out-Null
        $real = Invoke-Archiver @('-PackageDirectory', $PackageDirectory, '-ArchivePath', $realZip)
        Assert ($real.Code -eq 0) "archive of the real package exit $($real.Code): $(Get-Tail $real.Output)"
        if ($real.Code -eq 0) {
            $realInstall = Join-Path $work 'real install'
            $realExtract = Invoke-Archiver @('-Extract', '-ArchivePath', $realZip, '-DestinationDirectory', $realInstall)
            Assert ($realExtract.Code -eq 0) "extract of the real package archive exit $($realExtract.Code): $(Get-Tail $realExtract.Output)"
            if ($realExtract.Code -eq 0) {
                Assert (@(Test-PackageManifest $realInstall).Count -eq 0) "extracted real package fails package-verify: $(@(Test-PackageManifest $realInstall) -join '; ')"
                Assert (((Get-Inventory $realInstall) -join '|') -ceq ((Get-Inventory $PackageDirectory) -join '|')) 'extracted real package differs from the built package'
            }
            if ($SecondPackageDirectory) {
                $realZipB = Join-Path $work 'real B/bim-agentic-os.zip'
                New-Item -ItemType Directory -Force (Split-Path $realZipB) | Out-Null
                $realB = Invoke-Archiver @('-PackageDirectory', $SecondPackageDirectory, '-ArchivePath', $realZipB)
                Assert ($realB.Code -eq 0) "archive of the second real package exit $($realB.Code): $(Get-Tail $realB.Output)"
                if ($realB.Code -eq 0) {
                    Assert ((Get-B64 $realZipB) -ceq (Get-B64 $realZip)) 'archives of two clean package builds are not byte-identical'
                    Assert ((Get-B64 (Join-Path (Split-Path $realZipB) 'SHA256SUMS')) -ceq (Get-B64 (Join-Path (Split-Path $realZip) 'SHA256SUMS'))) 'SHA256SUMS of two clean package builds differ'
                }
            }
        }
    }
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
Complete
