<#
.SYNOPSIS
Installs (or removes) the BIM Agentic OS Revit add-in manifest Bim.addin from a verified P10-04 release package.

.DESCRIPTION
Install: snapshots manifest.json, runs scripts/package-verify.ps1 on -PackageDirectory and refuses the package on any
finding or if manifest.json changed meanwhile; parses the snapshot and checks the SHA-256 of revit/Bim.addin.template
against it after reading; fills the template's assembly placeholder with the absolute path
<package>/revit/Bim.Revit.dll (or -AssemblyPath) and writes <Addins>/Bim.addin through a temp file in the same folder.
Ownership is recorded as the SHA-256 of the written bytes in Bim.addin.bim-agentic-os-sha256 (the marker comment in
the manifest is informational only). A Bim.addin whose bytes do not match that record is foreign: it is first copied
byte-exactly to Bim.addin.bim-agentic-os-backup (not *.addin, so Revit ignores it; an existing backup is reused only
when byte-identical, otherwise the install is refused); a Bim.addin it did write is replaced without a further
backup; an identical result is a no-op (UNCHANGED). If the write fails, this run's backup and hash record are rolled
back so a retry starts clean. Relative paths resolve against the current PowerShell location.
The release package does not ship Bim.Revit.dll yet (it needs the licensed Revit 2025 references to build), so a
missing assembly is refused unless -AllowMissingAssembly is passed (then a WARNING is printed) or -AssemblyPath
points at a built Bim.Revit.dll.
Uninstall: removes only a Bim.addin matching the recorded hash and restores the backup byte-exactly if one exists
(also when only the backup is left); a foreign or edited Bim.addin is refused and left untouched; nothing installed
is a no-op.
Install from a release archive (P10-10): -Archive <x.zip> [-ChecksumsPath <SHA256SUMS>] instead of -PackageDirectory.
The archive is extracted only through scripts/package-archive.ps1 -Extract (checksum, entries against manifest.json,
re-hash on extract, package-verify) into a new private staging folder <temp>/bim-install-addin-<guid> (owner-only
mode on Unix), which is then installed from exactly like -PackageDirectory (package-verify runs again) and removed
on success and on every refusal. A package-archive refusal is reported with its type
('install-addin refused: package-archive refused: <code>: <detail>') and nothing is installed. Because the staging
folder is removed, -Archive requires -AssemblyPath (Bim.addin must not point into it).
Target: there is no default. Pass -AddinsDirectory <folder>, or -RevitUserAddins [-RevitYear 2025] to use
%APPDATA%\Autodesk\Revit\Addins\<year> explicitly. A symlinked Bim.addin, backup or hash record is refused.

.EXAMPLE
pwsh -File scripts/install-addin.ps1 -PackageDirectory out/release -RevitUserAddins -RevitYear 2025 -AssemblyPath C:\build\Bim.Revit.dll
pwsh -File scripts/install-addin.ps1 -Archive dist/bim.zip -AddinsDirectory C:\temp\Addins -AssemblyPath C:\build\Bim.Revit.dll
pwsh -File scripts/install-addin.ps1 -Uninstall -RevitUserAddins -RevitYear 2025
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [string]$PackageDirectory,
    [string]$Archive,
    [string]$ChecksumsPath,
    [string]$AddinsDirectory,
    [switch]$RevitUserAddins,
    [string]$RevitYear = '2025',
    [string]$AssemblyPath,
    [switch]$AllowMissingAssembly,
    [switch]$Uninstall,
    # Test hooks only (tests/packaging/test-install-addin.ps1): a replacement verify script and an injected write failure.
    [Parameter(DontShow)][string]$VerifyScript,
    [Parameter(DontShow)][switch]$FailWriteForTest
)
$ErrorActionPreference = 'Stop'
$marker = '<!-- bim-agentic-os install-addin.ps1'
$placeholder = 'REPLACE_WITH_ABSOLUTE_PATH_TO_Bim.Revit.dll'
function Resolve-FullPath([string]$Path) { [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)) }
function Get-Sha256Hex([byte[]]$Bytes) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant() }
# Private staging folder of an -Archive install; removed before every exit of the install path.
$script:StagingRoot = $null
function Remove-Staging {
    if ($script:StagingRoot -and (Test-Path -LiteralPath $script:StagingRoot)) {
        try { Remove-Item -LiteralPath $script:StagingRoot -Recurse -Force -ErrorAction Stop }
        catch { [Console]::Error.WriteLine("install-addin: WARNING could not remove the staging folder $($script:StagingRoot): $($_.Exception.Message)") }
    }
}
function Stop-Install([string]$Message) { Remove-Staging; [Console]::Error.WriteLine("install-addin refused: $Message"); exit 1 }
# An unexpected terminating error still removes the staging folder before the script stops.
trap { Remove-Staging; break }
function Write-AtomicText([string]$Path, [string]$Text) {
    $temp = $Path + '.tmp-' + [Guid]::NewGuid().ToString('N')
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text)
        $stream = [IO.FileStream]::new($temp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        [IO.File]::Move($temp, $Path, $true)
    } finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
    }
}

# --- Source arguments: -PackageDirectory or -Archive [-ChecksumsPath], install only ---
if ($Uninstall -and ($Archive -or $ChecksumsPath)) { Stop-Install '-Archive/-ChecksumsPath are install inputs; -Uninstall takes only the target folder. Nothing was changed.' }
if ($Archive -and $PackageDirectory) { Stop-Install 'pass either -PackageDirectory or -Archive, not both. Nothing was written.' }
if ($ChecksumsPath -and !$Archive) { Stop-Install '-ChecksumsPath is only used with -Archive. Nothing was written.' }
if ($Archive -and !$AssemblyPath) { Stop-Install '-Archive needs -AssemblyPath <built Bim.Revit.dll>: the archive is extracted into a private staging folder that is removed after the install, so Bim.addin cannot point at an assembly inside it. Nothing was written.' }

# --- Target: explicit only ---
if ($AddinsDirectory -and $RevitUserAddins) { Stop-Install 'pass either -AddinsDirectory or -RevitUserAddins, not both.' }
if ($RevitUserAddins) {
    if ($RevitYear -notmatch '\A20[0-9]{2}\z') { Stop-Install "-RevitYear must be a four-digit Revit year such as 2025, got '$RevitYear'." }
    if (!$env:APPDATA) { Stop-Install 'APPDATA is not set; pass -AddinsDirectory explicitly.' }
    $AddinsDirectory = Join-Path $env:APPDATA 'Autodesk' 'Revit' 'Addins' $RevitYear
} elseif (!$AddinsDirectory) {
    Stop-Install 'no target folder: pass -AddinsDirectory <folder>, or -RevitUserAddins [-RevitYear 2025] for %APPDATA%\Autodesk\Revit\Addins\<year>. There is no default.'
}
$addinsDir = Resolve-FullPath $AddinsDirectory
$addin = Join-Path $addinsDir 'Bim.addin'
$backup = $addin + '.bim-agentic-os-backup'
# Ownership record: SHA-256 of the exact bytes this script wrote. The marker comment is informational only.
$sidecar = $addin + '.bim-agentic-os-sha256'
"TARGET $addin"
if ((Test-Path -LiteralPath $addinsDir) -and !(Test-Path -LiteralPath $addinsDir -PathType Container)) { Stop-Install "$addinsDir exists but is not a folder." }
foreach ($path in $addin, $backup, $sidecar) {
    $info = [IO.FileInfo]::new($path)
    if ($info.LinkTarget) { Stop-Install "$path is a symbolic link (to $($info.LinkTarget)); nothing was changed." }
    if ((Test-Path -LiteralPath $path) -and !(Test-Path -LiteralPath $path -PathType Leaf)) { Stop-Install "$path exists but is not a file; nothing was changed." }
}
$exists = Test-Path -LiteralPath $addin -PathType Leaf
$ours = $false
if ($exists -and (Test-Path -LiteralPath $sidecar -PathType Leaf)) {
    $ours = (Get-Sha256Hex ([IO.File]::ReadAllBytes($addin))) -ceq ([IO.File]::ReadAllText($sidecar)).Trim()
}
function Remove-Sidecar { if (Test-Path -LiteralPath $sidecar -PathType Leaf) { Remove-Item -LiteralPath $sidecar -Force } }

if ($Uninstall) {
    if (!$exists) {
        if (Test-Path -LiteralPath $backup -PathType Leaf) {
            [IO.File]::Move($backup, $addin, $false)
            Remove-Sidecar
            "RESTORED $addin from $backup (no installed Bim.addin was present)"
            exit 0
        }
        Remove-Sidecar
        "UNCHANGED $addin does not exist; nothing to uninstall."
        exit 0
    }
    if (!$ours) { Stop-Install "$addin was not written by install-addin.ps1 (its content does not match the recorded install hash); it was left untouched." }
    if (Test-Path -LiteralPath $backup -PathType Leaf) {
        [IO.File]::Move($backup, $addin, $true)
        "RESTORED $addin from $backup"
    } else {
        Remove-Item -LiteralPath $addin -Force
        "REMOVED $addin"
    }
    Remove-Sidecar
    exit 0
}

# --- Package: must pass package-verify ---
$pwsh = (Get-Process -Id $PID).Path
if ($Archive) {
    # Only package-archive -Extract may produce the folder installed from; it goes into a fresh private staging folder.
    $archiveFile = Resolve-FullPath $Archive
    $archiveArguments = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'package-archive.ps1'), '-Extract', '-ArchivePath', $archiveFile)
    if ($ChecksumsPath) { $archiveArguments += @('-ChecksumsPath', (Resolve-FullPath $ChecksumsPath)) }
    $script:StagingRoot = Join-Path ([IO.Path]::GetTempPath()) ('bim-install-addin-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($script:StagingRoot)
    "STAGED $($script:StagingRoot)"
    if (!$IsWindows) {
        # Owner-only (0700); [IO.UnixFileMode] needs .NET 7+, older runtimes keep the default temp permissions.
        try { [IO.File]::SetUnixFileMode($script:StagingRoot, [IO.UnixFileMode]448) }
        catch { "WARNING could not restrict $($script:StagingRoot) to its owner: $($_.Exception.Message)" }
    }
    $package = Join-Path $script:StagingRoot 'package'
    $archiveArguments += @('-DestinationDirectory', $package)
    $ErrorActionPreference = 'Continue'
    $archiveOutput = @(& $pwsh @archiveArguments 2>&1 | ForEach-Object { "$_" })
    $archiveCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($archiveCode -ne 0) {
        $typed = @($archiveOutput | Where-Object { $_ -cmatch '\Apackage-archive refused: [a-z_]+: ' })
        $reason = if ($typed.Count) { $typed[0] } else { "package-archive failed on $archiveFile (exit $archiveCode): $($archiveOutput -join '; ')" }
        Stop-Install "$reason. Nothing was installed; the staging folder was removed."
    }
    if (!(Test-Path -LiteralPath (Join-Path $package 'manifest.json') -PathType Leaf)) { Stop-Install "package-archive reported success but $package holds no extracted package. Nothing was installed; the staging folder was removed." }
    "EXTRACTED $archiveFile (verified by package-archive against its SHA256SUMS and manifest.json)"
} else {
    if (!$PackageDirectory) { Stop-Install '-PackageDirectory <release folder from scripts/package.ps1> (or -Archive <release zip>) is required to install.' }
    $package = Resolve-FullPath $PackageDirectory
}
if (!(Test-Path -LiteralPath $package -PathType Container)) { Stop-Install "package folder not found: $package" }
$manifestPath = Join-Path $package 'manifest.json'
# Snapshot manifest.json before verification; the snapshot is what gets parsed, and it must still match afterwards.
$manifestBefore = if (Test-Path -LiteralPath $manifestPath -PathType Leaf) { [IO.File]::ReadAllBytes($manifestPath) } else { $null }
$verifier = if ($VerifyScript) { Resolve-FullPath $VerifyScript } else { Join-Path $PSScriptRoot 'package-verify.ps1' }
$ErrorActionPreference = 'Continue'
$verifyOutput = @(& $pwsh -NoProfile -File $verifier -PackageDirectory $package 2>&1 | ForEach-Object { "$_" })
$verifyCode = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($verifyCode -ne 0) { Stop-Install "package-verify rejected $package (exit $verifyCode): $($verifyOutput -join '; '). Nothing was written." }
$manifestAfter = if (Test-Path -LiteralPath $manifestPath -PathType Leaf) { [IO.File]::ReadAllBytes($manifestPath) } else { $null }
if ($null -eq $manifestBefore -or $null -eq $manifestAfter -or [Convert]::ToBase64String($manifestBefore) -cne [Convert]::ToBase64String($manifestAfter)) {
    Stop-Install "manifest.json in $package changed during verification; refusing to install from a package that is being modified. Nothing was written."
}
$manifest = [Text.UTF8Encoding]::new($false).GetString($manifestBefore) | ConvertFrom-Json
$templateEntry = @($manifest.files | Where-Object { [string]$_.path -ceq 'revit/Bim.addin.template' })
if ($templateEntry.Count -ne 1) { Stop-Install 'package has no revit/Bim.addin.template in its manifest.' }
$templateBytes = [IO.File]::ReadAllBytes((Join-Path $package 'revit' 'Bim.addin.template'))
if ((Get-Sha256Hex $templateBytes) -cne [string]$templateEntry[0].sha256) { Stop-Install 'revit/Bim.addin.template changed during verification (it no longer matches the verified manifest). Nothing was written.' }
$template = [Text.UTF8Encoding]::new($false).GetString($templateBytes)
$count = ([regex]::Matches($template, [regex]::Escape($placeholder))).Count
if ($count -ne 1) { Stop-Install "revit/Bim.addin.template must contain the placeholder $placeholder exactly once (found $count)." }

# --- Assembly path ---
$assembly = if ($AssemblyPath) { Resolve-FullPath $AssemblyPath } else { [IO.Path]::GetFullPath((Join-Path $package 'revit' 'Bim.Revit.dll')) }
if (!(Test-Path -LiteralPath $assembly -PathType Leaf)) {
    if (!$AllowMissingAssembly) { Stop-Install "Bim.Revit.dll not found at $assembly. The release package does not ship it yet; build runtimes/revit-native against Revit 2025 and copy Bim.Revit.dll there or pass -AssemblyPath, or pass -AllowMissingAssembly to install the manifest anyway. Nothing was written." }
    "WARNING Bim.Revit.dll not found at $assembly; Revit will report a load error until it is placed there."
}

# --- Render and check the manifest (XML comments may not contain '--') ---
$stamp = ("version=$($manifest.version) source_commit=$($manifest.source_commit)" -replace '-{2,}', '-')
$note = "$marker $stamp -->"
$body = $template.Replace($placeholder, [Security.SecurityElement]::Escape($assembly))
$declarationEnd = if ($body.StartsWith('<?xml')) { $body.IndexOf('?>') } else { -1 }
$text = if ($declarationEnd -ge 0) { $body.Substring(0, $declarationEnd + 2) + "`n" + $note + $body.Substring($declarationEnd + 2) } else { $note + "`n" + $body }
try { $xml = [xml]$text } catch { Stop-Install "rendered Bim.addin is not valid XML: $($_.Exception.Message)" }
if ([string]$xml.RevitAddIns.AddIn.Assembly -cne $assembly) { Stop-Install "rendered Bim.addin assembly '$($xml.RevitAddIns.AddIn.Assembly)' does not match '$assembly'." }

$textBytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
if ($ours -and [Convert]::ToBase64String([IO.File]::ReadAllBytes($addin)) -ceq [Convert]::ToBase64String($textBytes)) { Remove-Staging; "UNCHANGED $addin already installs $assembly"; exit 0 }
[void][IO.Directory]::CreateDirectory($addinsDir)
$createdBackup = $false
if ($exists -and !$ours) {
    if (Test-Path -LiteralPath $backup) {
        # A backup byte-identical to the current foreign Bim.addin is what an interrupted earlier run left; reuse it.
        if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($backup)) -cne [Convert]::ToBase64String([IO.File]::ReadAllBytes($addin))) {
            Stop-Install "$addin was not written by install-addin.ps1 and a different backup already exists at $backup; refusing to overwrite either. Nothing was written."
        }
        "BACKUP $backup (existing, identical to the current Bim.addin)"
    } else {
        [IO.File]::Copy($addin, $backup, $false)
        $createdBackup = $true
        "BACKUP $backup"
    }
}
$previousSidecar = if (Test-Path -LiteralPath $sidecar -PathType Leaf) { [IO.File]::ReadAllText($sidecar) } else { $null }
try {
    Write-AtomicText $sidecar ((Get-Sha256Hex $textBytes) + "`n")
    if ($FailWriteForTest) { throw 'injected write failure (test hook)' }
    Write-AtomicText $addin $text
} catch {
    $reason = $_.Exception.Message
    if ($null -eq $previousSidecar) { Remove-Sidecar } else { Write-AtomicText $sidecar $previousSidecar }
    if ($createdBackup) { Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue }
    Stop-Install "writing $addin failed ($reason); the existing Bim.addin was left as it was and this run's backup was removed."
}
Remove-Staging
"INSTALLED $addin (package $($manifest.version), assembly $assembly). Restart Revit to load it."
exit 0
