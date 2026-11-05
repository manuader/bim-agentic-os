#Requires -Version 7.2
# P10-07 add-in install/uninstall checks (plain assertions, no Pester). Never touches a real Revit folder: every
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
    'PASS install-addin: explicit target required, unverified or swapped package refused, assembly path filled, backup and uninstall restore, write-failure rollback, hash ownership, relative paths, idempotent reinstall'
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
    if ($WithAssembly) { Write-PackageText (Join-Path $Directory 'revit/Bim.Revit.dll') 'not a real assembly' }
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

Assert (Test-Path -LiteralPath $installer -PathType Leaf) 'scripts/install-addin.ps1 is missing'
Assert (Test-Path -LiteralPath $common -PathType Leaf) 'scripts/package-common.ps1 is missing'
if ($failures.Count) { Complete }
. $common

$work = Join-Path ([IO.Path]::GetTempPath()) ('bim addin test & ' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
$script:fakeAppData = Join-Path $work 'fake appdata'
New-Item -ItemType Directory -Force $script:fakeAppData | Out-Null
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

    # --- A real release package from scripts/package.ps1 installs (it carries no Bim.Revit.dll) ---
    if ($PackageDirectory) {
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
