#Requires -Version 7.2
# P10-07 add-in install/uninstall checks, plus P10-10 install from a release archive (-Archive [-ChecksumsPath]) whose
# private staging folder must be gone after every outcome (plain assertions, no Pester). Never touches a real Revit folder: every
# Addins directory is a temp directory, and APPDATA is pointed at a temp directory for the -RevitUserAddins case.
# Synthetic packages are made with the P10-04 manifest helpers (no dotnet needed); -PackageDirectory adds a check
# against a real package produced by scripts/package.ps1 (tests/packaging/test-package.ps1 passes one).
# Failures are printed as 'FAIL ...' lines (and GitHub annotations) because CI logs are not downloadable.
param([string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot)
$installer = Join-Path $root 'scripts/install-addin.ps1'
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
            if ($env:GITHUB_ACTIONS -eq 'true') { Write-Host ('::error title=install-addin::' + ($line -replace '%', '%25')) }
            Write-Host "FAIL $line"
        }
        Write-Error "install-addin checks failed: $($failures.Count) failure(s); first: $($failures[0])"
        exit 1
    }
    'PASS install-addin: explicit target required, unverified or swapped package refused, assembly path filled, backup and uninstall restore, write-failure rollback, hash ownership, relative paths, idempotent reinstall; archive install via package-archive -Extract into removed private staging, typed archive refusals, -ChecksumsPath, archive -> install -> uninstall'
    exit 0
}
function Get-Tail([string]$Text) { $t = $Text -replace '[\r\n]+', ' | '; if ($t.Length -gt 500) { $t.Substring($t.Length - 500) } else { $t } }
# Runs the installer in a child pwsh with APPDATA pointing at a temp directory, so no default can reach a real folder.
function Invoke-Installer([string[]]$Arguments) {
    $saved = [Environment]::GetEnvironmentVariable('APPDATA', 'Process')
    [Environment]::SetEnvironmentVariable('APPDATA', $script:fakeAppData, 'Process')
    try {
        $ErrorActionPreference = 'Continue'
        $output = & $pwsh -NoProfile -File $installer @Arguments 2>&1 | ForEach-Object { "$_" }
        return [pscustomobject]@{ Code = $LASTEXITCODE; Output = ($output -join "`n") }
    } finally { [Environment]::SetEnvironmentVariable('APPDATA', $saved, 'Process') }
}
function New-SyntheticPackage([string]$Directory, [switch]$WithAssembly) {
    New-Item -ItemType Directory -Force (Join-Path $Directory 'revit') | Out-Null
    Write-PackageText (Join-Path $Directory 'VERSION') "1.2.3`n"
    Write-PackageText (Join-Path $Directory 'revit/Bim.addin.template') (([IO.File]::ReadAllText($templateSource)) -replace "`r`n", "`n")
    if ($WithAssembly) {
        Write-PackageText (Join-Path $Directory 'revit/Bim.Revit.dll') 'not a real assembly'
        # P10-14: first-party binaries are the project entries of their folder's deps.json, as a dotnet build of
        # runtimes/revit-native emits next to Bim.Revit.dll.
        Write-PackageText (Join-Path $Directory 'revit/Bim.Revit.deps.json') '{ "runtimeTarget": { "name": ".NETCoreApp,Version=v8.0" }, "targets": { ".NETCoreApp,Version=v8.0": { "Bim.Revit/1.2.3": { "runtime": { "Bim.Revit.dll": {} } } } }, "libraries": { "Bim.Revit/1.2.3": { "type": "project", "serviceable": false, "sha512": "" } } }'
    }
    Write-PackageText (Join-Path $Directory 'sbom.json') (New-PackageSbomText $Directory '1.2.3' ('a' * 40) @())
    Write-PackageText (Join-Path $Directory 'manifest.json') (New-PackageManifestText $Directory '1.2.3' ('a' * 40) $false)
    return [IO.Path]::GetFullPath($Directory)
}
function Read-Assembly([string]$AddinFile) {
    $xml = [xml][IO.File]::ReadAllText($AddinFile)
    return [pscustomobject]@{ Assembly = [string]$xml.RevitAddIns.AddIn.Assembly; AddInId = [string]$xml.RevitAddIns.AddIn.AddInId; FullClassName = [string]$xml.RevitAddIns.AddIn.FullClassName }
}
function Get-B64([string]$Path) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($Path)) }
function Get-Quoted([string]$Text) { "'" + $Text.Replace("'", "''") + "'" }
function Get-Names([string]$Directory) { if (Test-Path -LiteralPath $Directory) { @(Get-ChildItem -LiteralPath $Directory -Force | ForEach-Object Name | Sort-Object) } else { @() } }
function Invoke-Child([string]$Script, [string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    $output = & $pwsh -NoProfile -File $Script @Arguments 2>&1 | ForEach-Object { "$_" }
    return [pscustomobject]@{ Code = $LASTEXITCODE; Output = ($output -join "`n") }
}
# P10-10: runs a script block with APPDATA and the temp directory (TEMP/TMP/TMPDIR) pointed at test folders, so the
# installer's private staging and package-archive's private copy land where the test can prove they were removed.
function Invoke-WithTemp([scriptblock]$Body, [string]$Temp) {
    if (!$Temp) { $Temp = $script:fakeTemp }
    $names = 'APPDATA', 'TEMP', 'TMP', 'TMPDIR'
    $saved = @{}
    foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
    [Environment]::SetEnvironmentVariable('APPDATA', $script:fakeAppData, 'Process')
    foreach ($name in 'TEMP', 'TMP', 'TMPDIR') { [Environment]::SetEnvironmentVariable($name, $Temp, 'Process') }
    try { return (& $Body) }
    finally { foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') } }
}
function Invoke-ArchiveInstaller([string[]]$Arguments) { Invoke-WithTemp { Invoke-Child $installer $Arguments } }
# The installer prints 'STAGED <private folder>'; that folder and every bim-install-addin-*/bim-archive-* temp entry
# must be gone after the run, whatever its outcome.
function Assert-StagingRemoved($Run, [string]$Label, [switch]$ExpectStaged) {
    $staged = $null
    foreach ($line in ($Run.Output -split "`n")) { if ($line -match '\ASTAGED (.+?)\r?\z') { $staged = $Matches[1] } }
    if ($ExpectStaged) {
        Assert ($null -ne $staged) "${Label}: installer did not report its STAGED folder: $(Get-Tail $Run.Output)"
        if ($staged) { Assert ($staged.StartsWith($script:fakeTemp, [StringComparison]::OrdinalIgnoreCase)) "${Label}: staging folder $staged is not under the temp directory $($script:fakeTemp)" }
    }
    if ($staged) { Assert (!(Test-Path -LiteralPath $staged)) "${Label}: staging folder $staged was left behind" }
    $left = @(Get-ChildItem -LiteralPath $script:fakeTemp -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'bim-install-addin-*' -or $_.Name -like 'bim-archive-*' })
    Assert ($left.Count -eq 0) "${Label}: temp leftovers: $(($left | ForEach-Object Name) -join ', ')"
    foreach ($item in $left) { Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}
# Rewrites a zip entry by entry (the edit block gets a list of Name/Bytes objects) and writes a matching SHA256SUMS,
# so the archive passes the checksum and only the entry checks can refuse it.
function Copy-ZipWith([string]$Source, [string]$Destination, [scriptblock]$Edit) {
    $entries = [Collections.Generic.List[object]]::new()
    $in = [IO.File]::OpenRead($Source)
    try {
        $zip = [IO.Compression.ZipArchive]::new($in, [IO.Compression.ZipArchiveMode]::Read)
        try {
            foreach ($entry in $zip.Entries) {
                $memory = [IO.MemoryStream]::new()
                $stream = $entry.Open()
                try { $stream.CopyTo($memory) } finally { $stream.Dispose() }
                $entries.Add([pscustomobject]@{ Name = $entry.FullName; Bytes = $memory.ToArray() })
            }
        } finally { $zip.Dispose() }
    } finally { $in.Dispose() }
    & $Edit $entries
    New-Item -ItemType Directory -Force (Split-Path $Destination) | Out-Null
    $out = [IO.File]::Create($Destination)
    try {
        $zip = [IO.Compression.ZipArchive]::new($out, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($item in $entries) {
                $stream = $zip.CreateEntry($item.Name).Open()
                try { $stream.Write($item.Bytes, 0, $item.Bytes.Length) } finally { $stream.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $out.Dispose() }
    $hash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path (Split-Path $Destination) 'SHA256SUMS'), "$hash  $([IO.Path]::GetFileName($Destination))`n")
}
Add-Type -AssemblyName System.IO.Compression
$archiver = Join-Path $root 'scripts/package-archive.ps1'

Assert (Test-Path -LiteralPath $installer -PathType Leaf) 'scripts/install-addin.ps1 is missing'
Assert (Test-Path -LiteralPath $common -PathType Leaf) 'scripts/package-common.ps1 is missing'
if ($failures.Count) { Complete }
. $common

$work = Join-Path ([IO.Path]::GetTempPath()) ('bim addin test & ' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
$script:fakeAppData = Join-Path $work 'fake appdata'
New-Item -ItemType Directory -Force $script:fakeAppData | Out-Null
$script:fakeTemp = Join-Path $work 'fake temp'
New-Item -ItemType Directory -Force $script:fakeTemp | Out-Null
$fakeTemp = $script:fakeTemp
try {
    $bare = New-SyntheticPackage (Join-Path $work 'pkg bare')
    $full = New-SyntheticPackage (Join-Path $work 'pkg & full') -WithAssembly
    $bareDll = Join-Path (Join-Path $bare 'revit') 'Bim.Revit.dll'
    $fullDll = Join-Path (Join-Path $full 'revit') 'Bim.Revit.dll'

    # --- An explicit target is required: no silent default to %APPDATA%\Autodesk\Revit ---
    $run = Invoke-Installer @('-PackageDirectory', $full)
    Assert ($run.Code -ne 0) 'install without -AddinsDirectory or -RevitUserAddins must be refused'
    Assert ($run.Output -match 'AddinsDirectory') "refusal without a target must name -AddinsDirectory: $(Get-Tail $run.Output)"
    Assert ((Get-Names $script:fakeAppData).Count -eq 0) 'install without a target wrote under APPDATA'
    $both = Join-Path $work 'addins both'
    $run = Invoke-Installer @('-PackageDirectory', $full, '-AddinsDirectory', $both, '-RevitUserAddins')
    Assert ($run.Code -ne 0) 'install with both -AddinsDirectory and -RevitUserAddins must be refused'
    Assert (!(Test-Path -LiteralPath $both) -and (Get-Names $script:fakeAppData).Count -eq 0) 'ambiguous target refusal wrote files'
    $run = Invoke-Installer @('-AddinsDirectory', (Join-Path $work 'addins nopkg'))
    Assert ($run.Code -ne 0 -and $run.Output -match 'PackageDirectory') "install without -PackageDirectory must be refused naming it: $(Get-Tail $run.Output)"

    # --- Unverified packages are refused through package-verify, before any write ---
    $tampered = Join-Path $work 'pkg tampered'
    Copy-Item -LiteralPath $full -Destination $tampered -Recurse
    [IO.File]::AppendAllText((Join-Path $tampered 'revit/Bim.addin.template'), '<!-- x -->')
    $target = Join-Path $work 'addins tampered'
    $run = Invoke-Installer @('-PackageDirectory', $tampered, '-AddinsDirectory', $target)
    Assert ($run.Code -ne 0) 'install from a tampered package must be refused'
    Assert ($run.Output -match 'package-verify' -and $run.Output -match 'sha256 mismatch') "tampered refusal must carry the package-verify finding: $(Get-Tail $run.Output)"
    Assert (!(Test-Path -LiteralPath (Join-Path $target 'Bim.addin'))) 'tampered package still installed Bim.addin'
    $nomanifest = Join-Path $work 'pkg nomanifest'
    Copy-Item -LiteralPath $full -Destination $nomanifest -Recurse
    Remove-Item -LiteralPath (Join-Path $nomanifest 'manifest.json')
    $run = Invoke-Installer @('-PackageDirectory', $nomanifest, '-AddinsDirectory', $target)
    Assert ($run.Code -ne 0 -and $run.Output -match 'manifest.json missing') "package without manifest must be refused: $(Get-Tail $run.Output)"
    Assert (!(Test-Path -LiteralPath (Join-Path $target 'Bim.addin'))) 'unmanifested package still installed Bim.addin'

    # --- Missing Bim.Revit.dll is refused unless explicitly allowed ---
    $target = Join-Path $work 'addins bare'
    $run = Invoke-Installer @('-PackageDirectory', $bare, '-AddinsDirectory', $target)
    Assert ($run.Code -ne 0 -and $run.Output -match 'Bim\.Revit\.dll') "missing Bim.Revit.dll must be refused naming it: $(Get-Tail $run.Output)"
    Assert (!(Test-Path -LiteralPath (Join-Path $target 'Bim.addin'))) 'refused missing assembly still installed Bim.addin'
    $run = Invoke-Installer @('-PackageDirectory', $bare, '-AddinsDirectory', $target, '-AllowMissingAssembly')
    Assert ($run.Code -eq 0) "install with -AllowMissingAssembly exit $($run.Code): $(Get-Tail $run.Output)"
    Assert ($run.Output -match '(?i)warning') "install with a missing assembly must warn: $(Get-Tail $run.Output)"
    $addin = Join-Path $target 'Bim.addin'
    if (Test-Path -LiteralPath $addin) {
        $read = Read-Assembly $addin
        Assert ($read.Assembly -ceq $bareDll) "assembly path '$($read.Assembly)' != package add-in folder '$bareDll'"
        Assert ($read.AddInId -ceq '7CB23333-1063-4E43-84E9-415EAA1E4BF2' -and $read.FullClassName -ceq 'Bim.Revit.App') 'installed manifest lost the template AddInId/FullClassName'
        Assert (!([IO.File]::ReadAllText($addin).Contains('REPLACE_WITH'))) 'installed manifest still contains the placeholder'
    } else { $failures.Add("install with -AllowMissingAssembly did not create $addin") }
    Assert ((Get-Names $target) -join ',' -ceq 'Bim.addin,Bim.addin.bim-agentic-os-sha256') "fresh install must create only Bim.addin and its ownership hash, found: $((Get-Names $target) -join ', ')"
    $run = Invoke-Installer @('-Uninstall', '-AddinsDirectory', $target)
    Assert ($run.Code -eq 0) "uninstall of a fresh install exit $($run.Code): $(Get-Tail $run.Output)"
    Assert ((Get-Names $target).Count -eq 0) "uninstall of a fresh install must leave the folder empty, found: $((Get-Names $target) -join ', ')"

    # --- Backup of an existing Bim.addin, idempotent reinstall, uninstall restores the original bytes ---
    $target = Join-Path $work 'addins existing'
    New-Item -ItemType Directory -Force $target | Out-Null
    $addin = Join-Path $target 'Bim.addin'
    $original = [Text.Encoding]::UTF8.GetBytes("<?xml version=`"1.0`"?>`r`n<RevitAddIns><!-- someone else's dev build --></RevitAddIns>`r`n")
    [IO.File]::WriteAllBytes($addin, $original)
    [IO.File]::WriteAllText((Join-Path $target 'Other.addin'), 'other add-in')
    $run = Invoke-Installer @('-PackageDirectory', $full, '-AddinsDirectory', $target)
    Assert ($run.Code -eq 0) "install over an existing Bim.addin exit $($run.Code): $(Get-Tail $run.Output)"
    $backups = @(Get-ChildItem -LiteralPath $target -Force -File | Where-Object { $_.Name -notin 'Bim.addin', 'Other.addin', 'Bim.addin.bim-agentic-os-sha256' })
    Assert ($backups.Count -eq 1) "install over an existing Bim.addin must leave exactly one backup, found: $(($backups | ForEach-Object Name) -join ', ')"
    Assert (@($backups | Where-Object { $_.Name -like '*.addin' }).Count -eq 0) 'backup must not end in .addin (Revit would load it)'
    if ($backups.Count -eq 1) { Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($backups[0].FullName)) -ceq [Convert]::ToBase64String($original)) 'backup is not byte-identical to the original Bim.addin' }
    if (Test-Path -LiteralPath $addin) { Assert ((Read-Assembly $addin).Assembly -ceq $fullDll) "assembly path with spaces and '&' not round-tripped: '$((Read-Assembly $addin).Assembly)' != '$fullDll'" }
    $installedBytes = [IO.File]::ReadAllBytes($addin)
    $run = Invoke-Installer @('-PackageDirectory', $full, '-AddinsDirectory', $target)
    Assert ($run.Code -eq 0 -and $run.Output -match 'UNCHANGED') "identical reinstall must be a no-op reporting UNCHANGED: $(Get-Tail $run.Output)"
    Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($addin)) -ceq [Convert]::ToBase64String($installedBytes)) 'identical reinstall changed Bim.addin'
    $override = Join-Path $work 'custom build/Bim.Revit.dll'
    New-Item -ItemType Directory -Force (Split-Path $override) | Out-Null
    [IO.File]::WriteAllText($override, 'x')
    $run = Invoke-Installer @('-PackageDirectory', $full, '-AddinsDirectory', $target, '-AssemblyPath', $override)
    Assert ($run.Code -eq 0) "reinstall with -AssemblyPath exit $($run.Code): $(Get-Tail $run.Output)"
    Assert ((Read-Assembly $addin).Assembly -ceq [IO.Path]::GetFullPath($override)) "-AssemblyPath not used: '$((Read-Assembly $addin).Assembly)'"
    $backupsAfter = @(Get-ChildItem -LiteralPath $target -Force -File | Where-Object { $_.Name -notin 'Bim.addin', 'Other.addin', 'Bim.addin.bim-agentic-os-sha256' })
    Assert ($backupsAfter.Count -eq 1) "reinstall over our own Bim.addin must not add backups, found: $(($backupsAfter | ForEach-Object Name) -join ', ')"
    if ($backupsAfter.Count -eq 1) { Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($backupsAfter[0].FullName)) -ceq [Convert]::ToBase64String($original)) 'reinstall overwrote the backup of the original Bim.addin' }
    $run = Invoke-Installer @('-Uninstall', '-AddinsDirectory', $target)
    Assert ($run.Code -eq 0) "uninstall exit $($run.Code): $(Get-Tail $run.Output)"
    Assert ((Test-Path -LiteralPath $addin) -and [Convert]::ToBase64String([IO.File]::ReadAllBytes($addin)) -ceq [Convert]::ToBase64String($original)) 'uninstall did not restore the original Bim.addin byte-exactly'
    Assert ((Get-Names $target) -join ',' -ceq 'Bim.addin,Other.addin') "uninstall must leave only the restored Bim.addin and unrelated files, found: $((Get-Names $target) -join ', ')"
    Assert ([IO.File]::ReadAllText((Join-Path $target 'Other.addin')) -ceq 'other add-in') 'unrelated add-in manifest was changed'

    # --- Uninstall refuses a Bim.addin it did not install; nothing to uninstall is a no-op ---
    $run = Invoke-Installer @('-Uninstall', '-AddinsDirectory', $target)
    Assert ($run.Code -ne 0) 'uninstall must refuse a Bim.addin not written by install-addin.ps1'
    Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($addin)) -ceq [Convert]::ToBase64String($original)) 'refused uninstall changed the foreign Bim.addin'
    $empty = Join-Path $work 'addins empty'
    $run = Invoke-Installer @('-Uninstall', '-AddinsDirectory', $empty)
    Assert ($run.Code -eq 0 -and $run.Output -match 'UNCHANGED') "uninstall with nothing installed must be an UNCHANGED no-op: $(Get-Tail $run.Output)"
    Assert (!(Test-Path -LiteralPath $empty)) 'no-op uninstall created the Addins directory'

    # --- A package swapped between verification and use is refused (test-only -VerifyScript hook) ---
    $swapPkg = Join-Path $work 'pkg swap'
    Copy-Item -LiteralPath $full -Destination $swapPkg -Recurse
    $swapVerify = Join-Path $work 'swap-verify.ps1'
    [IO.File]::WriteAllText($swapVerify, @"
param([string]`$PackageDirectory)
& (Get-Process -Id `$PID).Path -NoProfile -File $(Get-Quoted (Join-Path $root 'scripts/package-verify.ps1')) -PackageDirectory `$PackageDirectory
`$code = `$LASTEXITCODE
. $(Get-Quoted $common)
[IO.File]::AppendAllText((Join-Path `$PackageDirectory 'revit/Bim.addin.template'), '<!-- swapped after verification -->')
Write-PackageText (Join-Path `$PackageDirectory 'manifest.json') (New-PackageManifestText `$PackageDirectory '1.2.3' ('b' * 40) `$false)
exit `$code
"@)
    $target = Join-Path $work 'addins swap'
    $run = Invoke-Installer @('-PackageDirectory', $swapPkg, '-AddinsDirectory', $target, '-VerifyScript', $swapVerify)
    Assert ($run.Code -ne 0 -and $run.Output -match 'changed during verification') "package swapped after verification must be refused: $(Get-Tail $run.Output)"
    Assert (!(Test-Path -LiteralPath (Join-Path $target 'Bim.addin'))) 'package swapped after verification still installed Bim.addin'
    Assert ([IO.File]::ReadAllText((Join-Path $swapPkg 'revit/Bim.addin.template')).Contains('swapped after verification')) 'swap-verify hook did not run (test setup)'

    # --- A failed write after the backup leaves the original alone and does not block a retry ---
    $target = Join-Path $work 'addins failwrite'
    New-Item -ItemType Directory -Force $target | Out-Null
    $addin = Join-Path $target 'Bim.addin'
    $backupFile = $addin + '.bim-agentic-os-backup'
    [IO.File]::WriteAllBytes($addin, $original)
    $run = Invoke-Installer @('-PackageDirectory', $full, '-AddinsDirectory', $target, '-FailWriteForTest')
    Assert ($run.Code -ne 0) "injected write failure must exit non-zero: $(Get-Tail $run.Output)"
    Assert ((Get-Names $target) -join ',' -ceq 'Bim.addin') "failed write must leave only the original Bim.addin, found: $((Get-Names $target) -join ', ')"
    Assert ((Get-B64 $addin) -ceq [Convert]::ToBase64String($original)) 'failed write changed the original Bim.addin'
    $run = Invoke-Installer @('-PackageDirectory', $full, '-AddinsDirectory', $target)
    Assert ($run.Code -eq 0) "retry after a failed write exit $($run.Code): $(Get-Tail $run.Output)"
    $run = Invoke-Installer @('-Uninstall', '-AddinsDirectory', $target)
    Assert ($run.Code -eq 0 -and (Get-B64 $addin) -ceq [Convert]::ToBase64String($original) -and (Get-Names $target) -join ',' -ceq 'Bim.addin') "uninstall after a retried install must restore the original: $(Get-Tail $run.Output)"
    # A backup byte-identical to the foreign Bim.addin (an interrupted run) is accepted; a different one is refused.
    [IO.File]::WriteAllBytes($backupFile, $original)
    $run = Invoke-Installer @('-PackageDirectory', $full, '-AddinsDirectory', $target)
    Assert ($run.Code -eq 0) "install with a backup identical to the foreign Bim.addin exit $($run.Code): $(Get-Tail $run.Output)"
    $run = Invoke-Installer @('-Uninstall', '-AddinsDirectory', $target)
    Assert ($run.Code -eq 0 -and (Get-B64 $addin) -ceq [Convert]::ToBase64String($original) -and (Get-Names $target) -join ',' -ceq 'Bim.addin') "uninstall after reusing an identical backup must restore the original: $(Get-Tail $run.Output)"
    [IO.File]::WriteAllText($backupFile, 'older backup')
    $run = Invoke-Installer @('-PackageDirectory', $full, '-AddinsDirectory', $target)
    Assert ($run.Code -ne 0 -and $run.Output -match 'backup') "foreign Bim.addin with a different existing backup must be refused: $(Get-Tail $run.Output)"
    Assert ((Get-B64 $addin) -ceq [Convert]::ToBase64String($original) -and [IO.File]::ReadAllText($backupFile) -ceq 'older backup') 'refused install changed Bim.addin or the existing backup'

    # --- Ownership is the recorded content hash, not the marker text ---
    $target = Join-Path $work 'addins edited'
    $addin = Join-Path $target 'Bim.addin'
    $run = Invoke-Installer @('-PackageDirectory', $full, '-AddinsDirectory', $target)
    Assert ($run.Code -eq 0) "install for the edit case exit $($run.Code): $(Get-Tail $run.Output)"
    if (Test-Path -LiteralPath $addin) {
        $edited = [Text.Encoding]::UTF8.GetBytes(([IO.File]::ReadAllText($addin) -replace '<Assembly>[^<]*</Assembly>', '<Assembly>C:\elsewhere\Other.dll</Assembly>'))
        [IO.File]::WriteAllBytes($addin, $edited)
        $run = Invoke-Installer @('-Uninstall', '-AddinsDirectory', $target)
        Assert ($run.Code -ne 0) 'uninstall must refuse an installed Bim.addin edited afterwards (marker alone is not ownership)'
        Assert ((Get-B64 $addin) -ceq [Convert]::ToBase64String($edited)) 'refused uninstall changed the edited Bim.addin'
        $run = Invoke-Installer @('-PackageDirectory', $full, '-AddinsDirectory', $target)
        Assert ($run.Code -eq 0) "install over an edited Bim.addin exit $($run.Code): $(Get-Tail $run.Output)"
        $editBackup = $addin + '.bim-agentic-os-backup'
        Assert ((Test-Path -LiteralPath $editBackup) -and (Get-B64 $editBackup) -ceq [Convert]::ToBase64String($edited)) 'install over an edited Bim.addin must back it up byte-exactly'
        Assert ((Read-Assembly $addin).Assembly -ceq $fullDll) 'install over an edited Bim.addin did not write the package assembly path'
        $run = Invoke-Installer @('-Uninstall', '-AddinsDirectory', $target)
        Assert ($run.Code -eq 0 -and (Get-B64 $addin) -ceq [Convert]::ToBase64String($edited) -and (Get-Names $target) -join ',' -ceq 'Bim.addin') "uninstall must restore the edited Bim.addin: $(Get-Tail $run.Output)"
    } else { $failures.Add('install for the edit case did not create Bim.addin') }

    # --- Relative paths resolve against the caller's location ($PWD), not the process directory ---
    $relRoot = Join-Path $work 'relative'
    $relPkg = New-SyntheticPackage (Join-Path $relRoot 'pkg') -WithAssembly
    $command = "Set-Location -LiteralPath $(Get-Quoted $relRoot); & $(Get-Quoted $installer) -PackageDirectory 'pkg' -AddinsDirectory 'addins'; exit `$LASTEXITCODE"
    $ErrorActionPreference = 'Continue'
    $relOut = (& $pwsh -NoProfile -EncodedCommand ([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))) 2>&1 | ForEach-Object { "$_" }) -join "`n"
    $relCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $relAddin = Join-Path (Join-Path $relRoot 'addins') 'Bim.addin'
    Assert ($relCode -eq 0 -and (Test-Path -LiteralPath $relAddin)) "relative -PackageDirectory/-AddinsDirectory must resolve against `$PWD (exit $relCode): $(Get-Tail $relOut)"
    if (Test-Path -LiteralPath $relAddin) { Assert ((Read-Assembly $relAddin).Assembly -ceq (Join-Path (Join-Path $relPkg 'revit') 'Bim.Revit.dll')) "relative install assembly path '$((Read-Assembly $relAddin).Assembly)'" }

    # --- Symlinked Bim.addin, an Addins path that is a file, uninstall with only a backup present ---
    $target = Join-Path $work 'addins link'
    New-Item -ItemType Directory -Force $target | Out-Null
    $linkTarget = Join-Path $work 'link target.addin'
    [IO.File]::WriteAllText($linkTarget, 'linked')
    $linked = $true
    try { New-Item -ItemType SymbolicLink -Path (Join-Path $target 'Bim.addin') -Target $linkTarget -ErrorAction Stop | Out-Null }
    catch { $linked = $false; Write-Host "SKIP symlinked Bim.addin: symlinks cannot be created here: $($_.Exception.Message)" }
    if ($linked) {
        $run = Invoke-Installer @('-PackageDirectory', $full, '-AddinsDirectory', $target)
        Assert ($run.Code -ne 0 -and $run.Output -match 'symbolic link') "symlinked Bim.addin must be refused on install: $(Get-Tail $run.Output)"
        $run = Invoke-Installer @('-Uninstall', '-AddinsDirectory', $target)
        Assert ($run.Code -ne 0 -and $run.Output -match 'symbolic link') "symlinked Bim.addin must be refused on uninstall: $(Get-Tail $run.Output)"
        Assert ([IO.File]::ReadAllText($linkTarget) -ceq 'linked' -and (Get-Names $target) -join ',' -ceq 'Bim.addin') 'refused symlink case changed files'
    }
    $fileTarget = Join-Path $work 'addins is a file'
    [IO.File]::WriteAllText($fileTarget, 'not a folder')
    $run = Invoke-Installer @('-PackageDirectory', $full, '-AddinsDirectory', $fileTarget)
    Assert ($run.Code -ne 0 -and $run.Output -match 'not a folder') "an Addins path that is a file must be refused on install: $(Get-Tail $run.Output)"
    $run = Invoke-Installer @('-Uninstall', '-AddinsDirectory', $fileTarget)
    Assert ($run.Code -ne 0 -and [IO.File]::ReadAllText($fileTarget) -ceq 'not a folder') "an Addins path that is a file must be refused on uninstall and left intact: $(Get-Tail $run.Output)"
    $target = Join-Path $work 'addins backup only'
    New-Item -ItemType Directory -Force $target | Out-Null
    [IO.File]::WriteAllText((Join-Path $target 'Bim.addin.bim-agentic-os-backup'), 'previous')
    $run = Invoke-Installer @('-Uninstall', '-AddinsDirectory', $target)
    Assert ($run.Code -eq 0 -and $run.Output -match 'RESTORED') "uninstall with only a backup present must restore it: $(Get-Tail $run.Output)"
    Assert ((Get-Names $target) -join ',' -ceq 'Bim.addin' -and [IO.File]::ReadAllText((Join-Path $target 'Bim.addin')) -ceq 'previous') "backup-only uninstall must leave just the restored Bim.addin, found: $((Get-Names $target) -join ', ')"

    # --- -RevitUserAddins resolves under APPDATA only when asked (APPDATA is a temp directory here) ---
    $run = Invoke-Installer @('-PackageDirectory', $full, '-RevitUserAddins', '-RevitYear', '2025')
    Assert ($run.Code -eq 0) "install with -RevitUserAddins exit $($run.Code): $(Get-Tail $run.Output)"
    $userAddin = Join-Path (Join-Path (Join-Path (Join-Path (Join-Path $script:fakeAppData 'Autodesk') 'Revit') 'Addins') '2025') 'Bim.addin'
    Assert (Test-Path -LiteralPath $userAddin -PathType Leaf) "-RevitUserAddins did not install to $userAddin"
    $run = Invoke-Installer @('-Uninstall', '-RevitUserAddins', '-RevitYear', '2025')
    Assert ($run.Code -eq 0 -and !(Test-Path -LiteralPath $userAddin)) "uninstall with -RevitUserAddins did not remove $userAddin : $(Get-Tail $run.Output)"
    $run = Invoke-Installer @('-PackageDirectory', $full, '-RevitUserAddins', '-RevitYear', '25')
    Assert ($run.Code -ne 0) '-RevitYear must be a four-digit year'

    # --- P10-10: -PackageDirectory still installs straight from the folder (no archive staging) ---
    $target = Join-Path $work 'addins folder path'
    $run = Invoke-Installer @('-PackageDirectory', $full, '-AddinsDirectory', $target)
    Assert ($run.Code -eq 0 -and $run.Output -notmatch '(?m)^STAGED ') "-PackageDirectory install must not stage an archive: $(Get-Tail $run.Output)"
    if (Test-Path -LiteralPath (Join-Path $target 'Bim.addin')) { Assert ((Read-Assembly (Join-Path $target 'Bim.addin')).Assembly -ceq $fullDll) '-PackageDirectory install no longer points at the package Bim.Revit.dll' }
    else { $failures.Add('-PackageDirectory install did not create Bim.addin') }

    # --- P10-10: install from a release archive (package-archive -Extract into private staging) ---
    $archivePkg = New-SyntheticPackage (Join-Path $work 'pkg for archive')
    $zip = Join-Path (Join-Path $work 'release & dist') 'bim-1.2.3.zip'
    $zipSums = Join-Path (Split-Path $zip) 'SHA256SUMS'
    $made = Invoke-Child $archiver @('-PackageDirectory', $archivePkg, '-ArchivePath', $zip)
    Assert ($made.Code -eq 0 -and (Test-Path -LiteralPath $zip) -and (Test-Path -LiteralPath $zipSums)) "test setup: package-archive could not create $zip : $(Get-Tail $made.Output)"
    $builtDll = Join-Path (Join-Path $work 'built dll') 'Bim.Revit.dll'
    New-Item -ItemType Directory -Force (Split-Path $builtDll) | Out-Null
    [IO.File]::WriteAllText($builtDll, 'not a real assembly')

    # Argument refusals happen before anything is staged or written.
    $target = Join-Path $work 'addins archive args'
    $run = Invoke-ArchiveInstaller @('-Archive', $zip, '-PackageDirectory', $full, '-AddinsDirectory', $target, '-AssemblyPath', $builtDll)
    Assert ($run.Code -ne 0 -and $run.Output -match 'Archive' -and $run.Output -match 'PackageDirectory') "-Archive with -PackageDirectory must be refused naming both: $(Get-Tail $run.Output)"
    Assert-StagingRemoved $run 'archive+package refusal'
    $run = Invoke-ArchiveInstaller @('-Archive', $zip, '-AddinsDirectory', $target)
    Assert ($run.Code -ne 0 -and $run.Output -match 'AssemblyPath') "-Archive without -AssemblyPath must be refused naming it (the staging folder is removed, so Bim.addin cannot point into it): $(Get-Tail $run.Output)"
    Assert-StagingRemoved $run 'archive without -AssemblyPath'
    $run = Invoke-ArchiveInstaller @('-PackageDirectory', $full, '-ChecksumsPath', $zipSums, '-AddinsDirectory', $target)
    Assert ($run.Code -ne 0 -and $run.Output -match 'ChecksumsPath') "-ChecksumsPath without -Archive must be refused naming it: $(Get-Tail $run.Output)"
    Assert ($run.Output -notmatch '(?m)^STAGED ') "-ChecksumsPath without -Archive must stage nothing: $(Get-Tail $run.Output)"
    Assert-StagingRemoved $run '-ChecksumsPath without -Archive'
    $run = Invoke-ArchiveInstaller @('-Uninstall', '-Archive', $zip, '-AddinsDirectory', $target)
    Assert ($run.Code -ne 0 -and $run.Output -match 'Uninstall') "-Uninstall with -Archive must be refused: $(Get-Tail $run.Output)"
    $run = Invoke-ArchiveInstaller @('-Uninstall', '-ChecksumsPath', $zipSums, '-AddinsDirectory', $target)
    Assert ($run.Code -ne 0 -and $run.Output -match 'Uninstall' -and $run.Output -match 'ChecksumsPath') "-Uninstall with -ChecksumsPath must be refused: $(Get-Tail $run.Output)"
    Assert (!(Test-Path -LiteralPath $target)) "archive argument refusals created $target"

    # Review: staging privacy, a pre-existing staging path, an unusable temp folder, honest cleanup reporting,
    # and the untyped package-archive failure branch.
    $target = Join-Path $work 'addins staging checks'
    $run = Invoke-ArchiveInstaller @('-Archive', $zip, '-AddinsDirectory', $target, '-AssemblyPath', $builtDll, '-ReportStagingForTest')
    Assert ($run.Code -eq 0) "install with -ReportStagingForTest exit $($run.Code): $(Get-Tail $run.Output)"
    Assert-StagingRemoved $run 'staging privacy install' -ExpectStaged
    if ($IsWindows) {
        $user = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $allowed = @($user, 'S-1-5-18', 'S-1-5-32-544')
        if ($run.Output -match '(?m)^STAGING-ACL protected=(\S+) sids=(\S*)\r?$') {
            $sids = @($Matches[2].Split(',', [StringSplitOptions]::RemoveEmptyEntries))
            Assert ($Matches[1] -ceq 'True') "staging ACL must not inherit (protected=$($Matches[1]))"
            Assert ($sids -contains $user) "staging ACL must grant the current user $user, has: $($sids -join ', ')"
            Assert (@($sids | Where-Object { $_ -notin $allowed }).Count -eq 0) "staging ACL must grant only the current user, SYSTEM and Administrators, has: $($sids -join ', ')"
        } else { $failures.Add("install did not report its staging ACL (STAGING-ACL): $(Get-Tail $run.Output)") }
    } else {
        if ($run.Output -match '(?m)^STAGING-MODE (\S+)\r?$') { Assert ($Matches[1] -ceq '448') "staging folder mode must be 0700 (UserRead, UserWrite, UserExecute = 448), got $($Matches[1])" }
        else { $failures.Add("install did not report its staging mode (STAGING-MODE): $(Get-Tail $run.Output)") }
    }
    $run = Invoke-ArchiveInstaller @('-Uninstall', '-AddinsDirectory', $target)
    Assert ($run.Code -eq 0 -and (Get-Names $target).Count -eq 0) "uninstall after the staging privacy install must leave the folder empty: $(Get-Tail $run.Output)"

    $preexisting = Join-Path $fakeTemp 'bim-install-addin-preexisting'
    New-Item -ItemType Directory -Force $preexisting | Out-Null
    [IO.File]::WriteAllText((Join-Path $preexisting 'keep.txt'), 'not ours')
    $run = Invoke-ArchiveInstaller @('-Archive', $zip, '-AddinsDirectory', $target, '-AssemblyPath', $builtDll, '-StagingPathForTest', $preexisting)
    Assert ($run.Code -ne 0 -and $run.Output -match 'staging_exists') "a pre-existing staging folder must be refused with staging_exists: $(Get-Tail $run.Output)"
    Assert ((Test-Path -LiteralPath (Join-Path $preexisting 'keep.txt')) -and [IO.File]::ReadAllText((Join-Path $preexisting 'keep.txt')) -ceq 'not ours') 'a refused pre-existing staging folder must be left untouched'
    Assert (!(Test-Path -LiteralPath (Join-Path $target 'Bim.addin'))) 'pre-existing staging refusal still installed Bim.addin'
    Remove-Item -LiteralPath $preexisting -Recurse -Force

    # A temp path that is a file can never hold the staging folder. A missing temp folder is checked on Unix only:
    # on Windows the child pwsh itself creates a missing TEMP folder at startup, before the installer runs.
    $fileTemp = Join-Path $work 'temp is a file'
    [IO.File]::WriteAllText($fileTemp, 'not a folder')
    $run = Invoke-WithTemp { Invoke-Child $installer @('-Archive', $zip, '-AddinsDirectory', $target, '-AssemblyPath', $builtDll) } $fileTemp
    Assert ($run.Code -ne 0 -and $run.Output -match 'staging_temp_invalid') "a temp path that is a file must be refused with staging_temp_invalid: $(Get-Tail $run.Output)"
    Assert ([IO.File]::ReadAllText($fileTemp) -ceq 'not a folder' -and !(Test-Path -LiteralPath (Join-Path $target 'Bim.addin'))) 'temp-is-a-file refusal changed the file or installed Bim.addin'
    if (!$IsWindows) {
        $missingTemp = Join-Path $work 'no such temp'
        $run = Invoke-WithTemp { Invoke-Child $installer @('-Archive', $zip, '-AddinsDirectory', $target, '-AssemblyPath', $builtDll) } $missingTemp
        Assert ($run.Code -ne 0 -and $run.Output -match 'staging_temp_invalid') "a missing temp folder must be refused with staging_temp_invalid: $(Get-Tail $run.Output)"
        Assert (!(Test-Path -LiteralPath $missingTemp) -and !(Test-Path -LiteralPath (Join-Path $target 'Bim.addin'))) 'missing temp refusal created the temp folder or installed Bim.addin'
    }

    if (Test-Path -LiteralPath (Join-Path $target 'Bim.addin')) { $null = Invoke-ArchiveInstaller @('-Uninstall', '-AddinsDirectory', $target) }
    $run = Invoke-ArchiveInstaller @('-Archive', $zip, '-AddinsDirectory', $target, '-AssemblyPath', $builtDll, '-FailStagingRemovalForTest')
    $left = $null
    foreach ($line in ($run.Output -split "`n")) { if ($line -match '\ASTAGED (.+?)\r?\z') { $left = $Matches[1] } }
    Assert ($run.Code -eq 0 -and $run.Output -match 'INSTALLED') "install whose staging removal fails still installs (exit $($run.Code)): $(Get-Tail $run.Output)"
    Assert ($null -ne $left -and $run.Output -match ('staging folder left at ' + [regex]::Escape([string]$left))) "failed staging removal after install must say 'staging folder left at <path>': $(Get-Tail $run.Output)"
    Assert ($run.Output -notmatch '(?i)staging folder (was )?removed') "failed staging removal must not be reported as removed: $(Get-Tail $run.Output)"
    Assert ($null -ne $left -and (Test-Path -LiteralPath $left)) "test hook: the staging folder should still exist after a failed removal ($left)"
    if ($left -and (Test-Path -LiteralPath $left)) { Remove-Item -LiteralPath $left -Recurse -Force }
    $run = Invoke-ArchiveInstaller @('-Uninstall', '-AddinsDirectory', $target)
    New-Item -ItemType Directory -Force $target | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $target 'Bim.addin'), $original)
    $run = Invoke-ArchiveInstaller @('-Archive', $zip, '-AddinsDirectory', $target, '-AssemblyPath', $builtDll, '-FailWriteForTest', '-FailStagingRemovalForTest')
    $left = $null
    foreach ($line in ($run.Output -split "`n")) { if ($line -match '\ASTAGED (.+?)\r?\z') { $left = $Matches[1] } }
    Assert ($run.Code -ne 0 -and $null -ne $left -and $run.Output -match ('staging folder left at ' + [regex]::Escape([string]$left))) "failed staging removal after a refusal must say 'staging folder left at <path>': $(Get-Tail $run.Output)"
    Assert ($run.Output -notmatch '(?i)staging folder (was )?removed') "failed staging removal after a refusal must not be reported as removed: $(Get-Tail $run.Output)"
    if ($left -and (Test-Path -LiteralPath $left)) { Remove-Item -LiteralPath $left -Recurse -Force }
    Assert-StagingRemoved ([pscustomobject]@{ Output = '' }) 'after cleaning the deliberately left staging folders'
    Remove-Item -LiteralPath $target -Recurse -Force

    $fakeArchiver = Join-Path $work 'fake package-archive.ps1'
    [IO.File]::WriteAllText($fakeArchiver, "param([switch]`$Extract, [string]`$ArchivePath, [string]`$DestinationDirectory, [string]`$ChecksumsPath)`n[Console]::Error.WriteLine('unexpected crash in the archiver.')`nexit 3`n")
    $run = Invoke-ArchiveInstaller @('-Archive', $zip, '-AddinsDirectory', $target, '-AssemblyPath', $builtDll, '-ArchiveScript', $fakeArchiver)
    Assert ($run.Code -ne 0 -and $run.Output -match 'package-archive failed' -and $run.Output -match 'exit 3' -and $run.Output -match 'unexpected crash') "an untyped package-archive failure must be refused with its exit code and output: $(Get-Tail $run.Output)"
    Assert ($run.Output -notmatch '\.\.(\s|$)') "refusal text must not contain a doubled period: $(Get-Tail $run.Output)"
    Assert (!(Test-Path -LiteralPath (Join-Path $target 'Bim.addin'))) 'untyped package-archive failure still installed Bim.addin'
    Assert-StagingRemoved $run 'untyped package-archive failure' -ExpectStaged
    $silentArchiver = Join-Path $work 'silent package-archive.ps1'
    [IO.File]::WriteAllText($silentArchiver, "param([switch]`$Extract, [string]`$ArchivePath, [string]`$DestinationDirectory, [string]`$ChecksumsPath)`nexit 0`n")
    $run = Invoke-ArchiveInstaller @('-Archive', $zip, '-AddinsDirectory', $target, '-AssemblyPath', $builtDll, '-ArchiveScript', $silentArchiver)
    Assert ($run.Code -ne 0 -and $run.Output -match 'no extracted package') "a package-archive success without an extracted package must be refused: $(Get-Tail $run.Output)"
    Assert-StagingRemoved $run 'package-archive success without output' -ExpectStaged

    # End to end on an explicit Addins folder holding a foreign Bim.addin: archive -> install -> reinstall -> uninstall.
    $target = Join-Path $work 'addins archive e2e'
    New-Item -ItemType Directory -Force $target | Out-Null
    $addin = Join-Path $target 'Bim.addin'
    [IO.File]::WriteAllBytes($addin, $original)
    [IO.File]::WriteAllText((Join-Path $target 'Other.addin'), 'other add-in')
    $zipBytes = Get-B64 $zip
    $run = Invoke-ArchiveInstaller @('-Archive', $zip, '-AddinsDirectory', $target, '-AssemblyPath', $builtDll)
    Assert ($run.Code -eq 0 -and $run.Output -match 'INSTALLED') "install from a release archive exit $($run.Code): $(Get-Tail $run.Output)"
    Assert-StagingRemoved $run 'archive install' -ExpectStaged
    if (Test-Path -LiteralPath $addin) {
        $read = Read-Assembly $addin
        Assert ($read.Assembly -ceq [IO.Path]::GetFullPath($builtDll)) "archive install assembly '$($read.Assembly)' != -AssemblyPath '$builtDll'"
        Assert ($read.AddInId -ceq '7CB23333-1063-4E43-84E9-415EAA1E4BF2' -and $read.FullClassName -ceq 'Bim.Revit.App') 'archive install lost the template AddInId/FullClassName'
        Assert ([IO.File]::ReadAllText($addin).Contains('version=1.2.3')) 'archive install marker does not carry the archived package version'
    } else { $failures.Add("install from a release archive did not create $addin") }
    Assert ((Test-Path -LiteralPath ($addin + '.bim-agentic-os-backup')) -and (Get-B64 ($addin + '.bim-agentic-os-backup')) -ceq [Convert]::ToBase64String($original)) 'archive install must back up the foreign Bim.addin byte-exactly'
    Assert ((Get-B64 $zip) -ceq $zipBytes) 'archive install changed the archive'
    $run = Invoke-ArchiveInstaller @('-Archive', $zip, '-AddinsDirectory', $target, '-AssemblyPath', $builtDll)
    Assert ($run.Code -eq 0 -and $run.Output -match 'UNCHANGED') "identical archive reinstall must be UNCHANGED: $(Get-Tail $run.Output)"
    Assert-StagingRemoved $run 'archive reinstall (UNCHANGED)' -ExpectStaged
    $run = Invoke-ArchiveInstaller @('-Uninstall', '-AddinsDirectory', $target)
    Assert ($run.Code -eq 0) "uninstall after archive install exit $($run.Code): $(Get-Tail $run.Output)"
    Assert ((Get-B64 $addin) -ceq [Convert]::ToBase64String($original) -and (Get-Names $target) -join ',' -ceq 'Bim.addin,Other.addin') "uninstall after archive install must restore the original, found: $((Get-Names $target) -join ', ')"

    # Fresh explicit Addins folder: install then uninstall leaves it empty.
    $target = Join-Path $work 'addins archive fresh'
    $run = Invoke-ArchiveInstaller @('-Archive', $zip, '-AddinsDirectory', $target, '-AssemblyPath', $builtDll)
    Assert ($run.Code -eq 0 -and (Get-Names $target) -join ',' -ceq 'Bim.addin,Bim.addin.bim-agentic-os-sha256') "fresh archive install must create only Bim.addin and its hash record (exit $($run.Code)), found: $((Get-Names $target) -join ', '): $(Get-Tail $run.Output)"
    Assert-StagingRemoved $run 'fresh archive install' -ExpectStaged
    $run = Invoke-ArchiveInstaller @('-Uninstall', '-AddinsDirectory', $target)
    Assert ($run.Code -eq 0 -and (Get-Names $target).Count -eq 0) "uninstall of a fresh archive install must leave the folder empty: $(Get-Tail $run.Output)"

    # -ChecksumsPath: SHA256SUMS kept elsewhere than next to the archive.
    $moved = Join-Path (Join-Path $work 'moved archive') 'bim-1.2.3.zip'
    New-Item -ItemType Directory -Force (Split-Path $moved) | Out-Null
    Copy-Item -LiteralPath $zip -Destination $moved
    $movedSums = Join-Path (Join-Path $work 'sums elsewhere') 'release.sha256'
    New-Item -ItemType Directory -Force (Split-Path $movedSums) | Out-Null
    Copy-Item -LiteralPath $zipSums -Destination $movedSums
    $target = Join-Path $work 'addins archive sums'
    $run = Invoke-ArchiveInstaller @('-Archive', $moved, '-AddinsDirectory', $target, '-AssemblyPath', $builtDll)
    Assert ($run.Code -ne 0 -and $run.Output -match 'package-archive refused: checksums_missing') "archive without SHA256SUMS next to it must be refused with checksums_missing: $(Get-Tail $run.Output)"
    Assert (!(Test-Path -LiteralPath (Join-Path $target 'Bim.addin'))) 'archive refused for missing checksums still installed Bim.addin'
    Assert-StagingRemoved $run 'checksums_missing refusal'
    $run = Invoke-ArchiveInstaller @('-Archive', $moved, '-ChecksumsPath', $movedSums, '-AddinsDirectory', $target, '-AssemblyPath', $builtDll)
    Assert ($run.Code -eq 0 -and (Test-Path -LiteralPath (Join-Path $target 'Bim.addin'))) "install with -ChecksumsPath exit $($run.Code): $(Get-Tail $run.Output)"
    Assert-StagingRemoved $run 'install with -ChecksumsPath' -ExpectStaged
    $run = Invoke-ArchiveInstaller @('-Uninstall', '-AddinsDirectory', $target)
    Assert ($run.Code -eq 0 -and (Get-Names $target).Count -eq 0) "uninstall after -ChecksumsPath install must leave the folder empty: $(Get-Tail $run.Output)"

    # P10-08 refusals propagate typed: nothing installed, foreign Bim.addin untouched, staging removed.
    $bad = Join-Path $work 'bad archives'
    $mismatchZip = Join-Path (Join-Path $bad 'checksum') 'bim-1.2.3.zip'
    New-Item -ItemType Directory -Force (Split-Path $mismatchZip) | Out-Null
    Copy-Item -LiteralPath $zip -Destination $mismatchZip
    Copy-Item -LiteralPath $zipSums -Destination (Join-Path (Split-Path $mismatchZip) 'SHA256SUMS')
    [IO.File]::AppendAllText($mismatchZip, 'x')
    $entryZip = Join-Path (Join-Path $bad 'entry') 'bim-1.2.3.zip'
    Copy-ZipWith $zip $entryZip { param($list) foreach ($item in $list) { if ($item.Name -ceq 'revit/Bim.addin.template') { $item.Bytes = [Text.Encoding]::UTF8.GetBytes('<RevitAddIns>tampered</RevitAddIns>') } } }
    $unsafeZip = Join-Path (Join-Path $bad 'unsafe') 'bim-1.2.3.zip'
    Copy-ZipWith $zip $unsafeZip { param($list) $list.Insert(0, [pscustomobject]@{ Name = '../escaped.txt'; Bytes = [Text.Encoding]::UTF8.GetBytes('escaped') }) }
    $cases = @(
        @{ Name = 'checksum'; Zip = $mismatchZip; Code = 'checksum_mismatch' },
        @{ Name = 'entry'; Zip = $entryZip; Code = 'entry_mismatch' },
        @{ Name = 'unsafe'; Zip = $unsafeZip; Code = 'entry_path_unsafe' },
        @{ Name = 'missing'; Zip = (Join-Path $bad 'nothing here.zip'); Code = 'archive_missing' }
    )
    foreach ($case in $cases) {
        $target = Join-Path $work ('addins refused ' + $case.Name)
        New-Item -ItemType Directory -Force $target | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $target 'Bim.addin'), $original)
        $run = Invoke-ArchiveInstaller @('-Archive', $case.Zip, '-AddinsDirectory', $target, '-AssemblyPath', $builtDll)
        Assert ($run.Code -ne 0) "$($case.Name) archive must be refused (exit $($run.Code)): $(Get-Tail $run.Output)"
        Assert ($run.Output -match 'install-addin refused' -and $run.Output -match ('package-archive refused: ' + $case.Code + ':')) "$($case.Name) archive refusal must carry the typed code $($case.Code): $(Get-Tail $run.Output)"
        Assert ($run.Output -notmatch '\.\.(\s|$)') "$($case.Name) archive refusal text must not contain a doubled period: $(Get-Tail $run.Output)"
        Assert ((Get-Names $target) -join ',' -ceq 'Bim.addin' -and (Get-B64 (Join-Path $target 'Bim.addin')) -ceq [Convert]::ToBase64String($original)) "$($case.Name) archive refusal changed the Addins folder, found: $((Get-Names $target) -join ', ')"
        Assert-StagingRemoved $run ("$($case.Name) archive refusal")
    }
    Assert (!(Test-Path -LiteralPath (Join-Path $work 'escaped.txt')) -and !(Test-Path -LiteralPath (Join-Path $bad 'escaped.txt')) -and !(Test-Path -LiteralPath (Join-Path $fakeTemp 'escaped.txt'))) 'unsafe archive entry escaped onto disk'

    # Refusals after extraction (install stage) also remove the staging folder.
    $target = Join-Path $work 'addins archive late'
    New-Item -ItemType Directory -Force $target | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $target 'Bim.addin'), $original)
    $run = Invoke-ArchiveInstaller @('-Archive', $zip, '-AddinsDirectory', $target, '-AssemblyPath', (Join-Path $work 'no such/Bim.Revit.dll'))
    Assert ($run.Code -ne 0 -and $run.Output -match 'Bim\.Revit\.dll not found') "archive install with a missing -AssemblyPath must be refused: $(Get-Tail $run.Output)"
    Assert-StagingRemoved $run 'missing assembly after extraction' -ExpectStaged
    $run = Invoke-ArchiveInstaller @('-Archive', $zip, '-AddinsDirectory', $target, '-AssemblyPath', $builtDll, '-FailWriteForTest')
    Assert ($run.Code -ne 0 -and $run.Output -match 'injected write failure') "archive install with an injected write failure must be refused: $(Get-Tail $run.Output)"
    Assert-StagingRemoved $run 'write failure after extraction' -ExpectStaged
    Assert ((Get-Names $target) -join ',' -ceq 'Bim.addin' -and (Get-B64 (Join-Path $target 'Bim.addin')) -ceq [Convert]::ToBase64String($original)) "late archive refusals changed the Addins folder, found: $((Get-Names $target) -join ', ')"

    # Relative -Archive/-ChecksumsPath/-AssemblyPath/-AddinsDirectory resolve against $PWD.
    $relArchiveRoot = Join-Path $work 'relative archive'
    New-Item -ItemType Directory -Force (Join-Path $relArchiveRoot 'dist') | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $relArchiveRoot 'sums') | Out-Null
    Copy-Item -LiteralPath $zip -Destination (Join-Path $relArchiveRoot 'dist/bim-1.2.3.zip')
    Copy-Item -LiteralPath $zipSums -Destination (Join-Path $relArchiveRoot 'sums/SHA256SUMS')
    Copy-Item -LiteralPath $builtDll -Destination (Join-Path $relArchiveRoot 'Bim.Revit.dll')
    $command = "Set-Location -LiteralPath $(Get-Quoted $relArchiveRoot); & $(Get-Quoted $installer) -Archive 'dist/bim-1.2.3.zip' -ChecksumsPath 'sums/SHA256SUMS' -AssemblyPath 'Bim.Revit.dll' -AddinsDirectory 'addins'; exit `$LASTEXITCODE"
    $relRun = Invoke-WithTemp { $ErrorActionPreference = 'Continue'; $o = (& $pwsh -NoProfile -EncodedCommand ([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))) 2>&1 | ForEach-Object { "$_" }) -join "`n"; [pscustomobject]@{ Code = $LASTEXITCODE; Output = $o } }
    $relArchiveAddin = Join-Path (Join-Path $relArchiveRoot 'addins') 'Bim.addin'
    Assert ($relRun.Code -eq 0 -and (Test-Path -LiteralPath $relArchiveAddin)) "relative -Archive paths must resolve against `$PWD (exit $($relRun.Code)): $(Get-Tail $relRun.Output)"
    if (Test-Path -LiteralPath $relArchiveAddin) { Assert ((Read-Assembly $relArchiveAddin).Assembly -ceq (Join-Path $relArchiveRoot 'Bim.Revit.dll')) "relative archive install assembly path '$((Read-Assembly $relArchiveAddin).Assembly)'" }
    Assert-StagingRemoved $relRun 'relative archive install' -ExpectStaged

    # --- A real release package from scripts/package.ps1 installs (it carries no Bim.Revit.dll) ---
    if ($PackageDirectory) {
        # P10-10: the same real package through package-archive -> install -Archive -> uninstall.
        $realZip = Join-Path (Join-Path $work 'real dist') 'bim-real.zip'
        $made = Invoke-Child $archiver @('-PackageDirectory', $PackageDirectory, '-ArchivePath', $realZip)
        Assert ($made.Code -eq 0) "test setup: package-archive of the real package exit $($made.Code): $(Get-Tail $made.Output)"
        $target = Join-Path $work 'addins real archive'
        $run = Invoke-ArchiveInstaller @('-Archive', $realZip, '-AddinsDirectory', $target, '-AssemblyPath', $builtDll)
        Assert ($run.Code -eq 0 -and (Test-Path -LiteralPath (Join-Path $target 'Bim.addin'))) "install from the real package archive exit $($run.Code): $(Get-Tail $run.Output)"
        Assert-StagingRemoved $run 'real archive install' -ExpectStaged
        if (Test-Path -LiteralPath (Join-Path $target 'Bim.addin')) { Assert ((Read-Assembly (Join-Path $target 'Bim.addin')).Assembly -ceq [IO.Path]::GetFullPath($builtDll)) 'real archive install did not use -AssemblyPath' }
        $run = Invoke-ArchiveInstaller @('-Uninstall', '-AddinsDirectory', $target)
        Assert ($run.Code -eq 0 -and (Get-Names $target).Count -eq 0) "uninstall after the real archive install must leave the folder empty: $(Get-Tail $run.Output)"

        $target = Join-Path $work 'addins real'
        $run = Invoke-Installer @('-PackageDirectory', $PackageDirectory, '-AddinsDirectory', $target, '-AllowMissingAssembly')
        Assert ($run.Code -eq 0) "install from the real package exit $($run.Code): $(Get-Tail $run.Output)"
        $addin = Join-Path $target 'Bim.addin'
        if (Test-Path -LiteralPath $addin) {
            $expected = Join-Path (Join-Path ([IO.Path]::GetFullPath($PackageDirectory)) 'revit') 'Bim.Revit.dll'
            Assert ((Read-Assembly $addin).Assembly -ceq $expected) "real package assembly path '$((Read-Assembly $addin).Assembly)' != '$expected'"
        } else { $failures.Add('install from the real package did not create Bim.addin') }
        $run = Invoke-Installer @('-Uninstall', '-AddinsDirectory', $target)
        Assert ($run.Code -eq 0 -and (Get-Names $target).Count -eq 0) "uninstall after real package install must leave the folder empty: $(Get-Tail $run.Output)"
    }
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
Complete
