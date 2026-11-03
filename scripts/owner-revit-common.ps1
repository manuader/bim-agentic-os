# Helpers shared by the owner scripts that launch a NEW child Revit with the BIM Agentic OS add-in
# (owner-native-session.ps1 and owner-open-project.ps1). Dot-source it. Nothing here kills, closes or attaches to a
# Revit process; a manifest under %APPDATA% is removed only when it is byte-identical to one this repository staged.
# Headless coverage: tests/integration/test-owner-open-project.ps1.

$OwnerManifestName = 'Bim.Agentic.Fixture.addin'
$OwnerAddInId = 'A38C9DBF-CE60-48BA-98AD-4F131C0F15D3'

function Get-OwnerSha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

# The repository-local SDK when present (owner machine), else dotnet on PATH.
function Get-OwnerDotnet([string]$Root) {
 $local = Join-Path $Root '.tools/dotnet/dotnet.exe'
 if (Test-Path -LiteralPath $local) { return $local }
 (Get-Command dotnet -ErrorAction Stop).Source
}

# Release builds with --warnaserror, one log per project in $LogDirectory. Throws on the first failure.
function Invoke-OwnerReleaseBuild([string]$Root, [string]$Dotnet, [string]$LogDirectory, [string[]]$Projects, [Collections.IDictionary]$Results) {
 foreach ($project in $Projects) {
  $buildLog = Join-Path $LogDirectory ('build-' + [IO.Path]::GetFileNameWithoutExtension($project) + '.log')
  & $Dotnet build (Join-Path $Root $project) --configuration Release --warnaserror *>&1 | Tee-Object -FilePath $buildLog | Out-Host
  if ($LASTEXITCODE -ne 0) { if ($null -ne $Results) { $Results[$project] = 'failed' }; throw "Build failed: $project; see $buildLog" }
  if ($null -ne $Results) { $Results[$project] = 'passed' }
 }
}

function Get-OwnerInstalledManifest { Join-Path $env:APPDATA "Autodesk/Revit/Addins/2025/$OwnerManifestName" }

# Staged manifests this repository wrote: native session runs and open-project sessions.
function Get-OwnerStagedManifests([string]$Root) {
 @(
  @(Get-ChildItem (Join-Path $Root '.bim/native-runs') -Directory -ErrorAction SilentlyContinue) +
  @(Get-ChildItem (Join-Path $Root '.bim/open-project/sessions') -Directory -ErrorAction SilentlyContinue) |
   ForEach-Object { Join-Path $_.FullName $OwnerManifestName } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
 )
}

# A manifest left installed by an earlier run is removed when it is byte-identical to one this repository staged;
# returns that staged path, or $null when nothing was installed. Anything else is refused and kept.
function Remove-OwnerLeftoverManifest([string]$Root, [string]$Manifest) {
 if (!(Test-Path -LiteralPath $Manifest)) { return $null }
 $existing = Get-OwnerSha $Manifest
 $owned = @(Get-OwnerStagedManifests $Root | Where-Object { (Get-OwnerSha $_) -eq $existing })
 if (!$owned.Count) { throw "Existing $Manifest is not a staged manifest from this repo; review it manually" }
 Remove-Item -LiteralPath $Manifest
 $owned[0]
}

# Removes the installed manifest only when it is byte-identical to $Staged. True when it removed it.
function Remove-OwnerManifestIfOwned([string]$Manifest, [string]$Staged) {
 if ((Test-Path -LiteralPath $Manifest) -and (Test-Path -LiteralPath $Staged) -and (Get-OwnerSha $Manifest) -eq (Get-OwnerSha $Staged)) { Remove-Item -LiteralPath $Manifest; return $true }
 $false
}

# Same manifest shape as tests/integration/launch-native-fixture.ps1 (same AddInId, never loads into a running session).
function Write-OwnerManifest([string]$Path, [string]$Assembly, [string]$Name) {
 $escaped = [Security.SecurityElement]::Escape($Assembly)
 "<RevitAddIns><AddIn Type=`"Application`"><Name>$Name</Name><Assembly>$escaped</Assembly><AddInId>$OwnerAddInId</AddInId><FullClassName>Bim.Revit.App</FullClassName><VendorId>BIMOS</VendorId><AllowLoadIntoExistingSession>false</AllowLoadIntoExistingSession></AddIn></RevitAddIns>" | Set-Content -LiteralPath $Path
}

# ---- Owner project mode (owner-open-project.ps1) ----

# A new session directory .bim/open-project/sessions/<stamp> with the staged add-in (Bim.Revit.dll + Bim.Core.dll),
# its manifest and the project config the add-in reads through BIM_AGENTIC_PROJECT_CONFIG. Never reuses a directory.
function New-OwnerProjectSession([string]$Root, [string]$Stamp, [string]$AddinAssembly, [string]$DocumentPath) {
 $directory = [IO.Path]::GetFullPath((Join-Path $Root ".bim/open-project/sessions/$Stamp"))
 if (Test-Path -LiteralPath $directory) { throw "Session directory already exists: $directory" }
 $core = Join-Path (Split-Path $AddinAssembly) 'Bim.Core.dll'
 foreach ($path in @($AddinAssembly, $core)) { if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required input missing: $path" } }
 New-Item -ItemType Directory -Path $directory | Out-Null
 $package = New-Item -ItemType Directory -Path (Join-Path $directory 'addin')
 Copy-Item -LiteralPath $AddinAssembly -Destination $package.FullName
 Copy-Item -LiteralPath $core -Destination $package.FullName
 $assembly = Join-Path $package.FullName ([IO.Path]::GetFileName($AddinAssembly))
 $manifest = Join-Path $directory $OwnerManifestName
 Write-OwnerManifest -Path $manifest -Assembly $assembly -Name 'BIM Agentic OS Project'
 $config = Join-Path $directory 'project-config.json'
 $result = Join-Path $directory 'project-result.json'
 [ordered]@{ ConfirmOwnerProject = $true; SessionDirectory = $directory; DocumentPath = $DocumentPath; ResultPath = $result } | ConvertTo-Json | Set-Content -LiteralPath $config
 [pscustomobject]@{ Directory = $directory; Assembly = $assembly; Manifest = $manifest; Config = $config; Result = $result }
}

# Best effort: running Revit processes whose main window title names the document (Revit shows the active
# document's name without extension). Conservative: any title containing the name counts.
function Find-OwnerRevitShowingDocument([object[]]$Processes, [string]$DocumentPath) {
 $name = [IO.Path]::GetFileNameWithoutExtension($DocumentPath)
 @($Processes | Where-Object { $_.MainWindowTitle -and ([string]$_.MainWindowTitle).IndexOf($name, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
}

# Workshared (central or local) models keep a <name>_backup folder beside the file; project mode refuses them.
function Test-OwnerWorkshared([string]$DocumentPath) {
 $backup = Join-Path (Split-Path $DocumentPath) ([IO.Path]::GetFileNameWithoutExtension($DocumentPath) + '_backup')
 Test-Path -LiteralPath $backup -PathType Container
}

# True when the file cannot be opened without sharing (another process holds it). Opens for read only, writes nothing.
function Test-OwnerFileInUse([string]$DocumentPath) {
 try { $stream = [IO.File]::Open($DocumentPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None); $stream.Dispose(); return $false }
 catch [IO.IOException] { return $true }
}

# The earlier session recorded in latest.json when its child Revit is still alive on the same document, else $null.
function Get-OwnerLiveSession([string]$LatestPath, [string]$DocumentPath, [scriptblock]$IsAlive) {
 if (!(Test-Path -LiteralPath $LatestPath -PathType Leaf)) { return $null }
 try { $latest = Get-Content -LiteralPath $LatestPath -Raw | ConvertFrom-Json } catch { return $null }
 if (!$latest -or !$latest.PSObject.Properties['pid'] -or !$latest.PSObject.Properties['document_path']) { return $null }
 if (![string]::Equals([IO.Path]::GetFullPath([string]$latest.document_path), [IO.Path]::GetFullPath($DocumentPath), [StringComparison]::OrdinalIgnoreCase)) { return $null }
 if (!(& $IsAlive ([int]$latest.pid))) { return $null }
 $latest
}

function Write-OwnerLatest([string]$Path, $Record) {
 New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
 $temporary = $Path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
 try { $Record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $temporary; Move-Item -LiteralPath $temporary -Destination $Path -Force }
 finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}

# $null when the bootstrap result proves this child opened the requested document with its own pipe, else the reason.
function Test-OwnerProjectResult($Result, [int]$ChildId, [string]$DocumentPath) {
 function Value([string]$name) { if ($Result.PSObject.Properties[$name]) { $Result.$name } else { $null } }
 if ((Value 'status') -ne 'succeeded') { return "el complemento rechazó el proyecto: $(Value 'code')" }
 if ((Value 'mode') -ne 'project') { return 'el resultado no es del modo proyecto' }
 if ((Value 'processId') -ne $ChildId) { return "el resultado es de otro proceso ($(Value 'processId')), no del Revit nuevo ($ChildId)" }
 if ((Value 'pipeName') -ne ('bim-agentic-os-2025-' + $ChildId)) { return "el canal $(Value 'pipeName') no es el del Revit nuevo" }
 $opened = [string](Value 'documentPath')
 if (!$opened -or ![string]::Equals([IO.Path]::GetFullPath($opened), [IO.Path]::GetFullPath($DocumentPath), [StringComparison]::OrdinalIgnoreCase)) { return "Revit abrió otro archivo: $opened" }
 if ([string]::IsNullOrWhiteSpace([string](Value 'documentUniqueId'))) { return 'falta el UniqueId de ProjectInformation' }
 $null
}
