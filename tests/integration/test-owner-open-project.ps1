# Headless checks for scripts/owner-open-project.ps1: the helpers it shares through scripts/owner-revit-common.ps1
# (staged session layout, leftover manifest ownership, "already open elsewhere" signals, result provenance and the
# latest.json record). No Revit, no process launch, no manifest installed under %APPDATA%.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path (Split-Path (Split-Path $PSScriptRoot)) 'scripts/owner-revit-common.ps1')
$work = Join-Path ([IO.Path]::GetTempPath()) ('owner-open-project-' + [guid]::NewGuid())
New-Item -ItemType Directory $work | Out-Null
$passed = 0
function Check([bool]$condition, [string]$name) { if (!$condition) { throw "FAIL $name" }; Write-Output "PASS $name"; $script:passed++ }
function Throws([scriptblock]$block) { try { & $block | Out-Null; return $null } catch { return $_.Exception.Message } }
try {
 $repo = New-Item -ItemType Directory (Join-Path $work 'repo')
 $root = $repo.FullName
 $build = New-Item -ItemType Directory (Join-Path $work 'build')
 Set-Content (Join-Path $build 'Bim.Revit.dll') 'addin'
 Set-Content (Join-Path $build 'Bim.Core.dll') 'core'
 $projects = New-Item -ItemType Directory (Join-Path $work 'ARQ.ARG')
 $document = Join-Path $projects 'BIM-Agentic-Test.rvt'
 Set-Content $document 'rvt'

 # ---- Session staging: add-in copy, manifest and project config under .bim/open-project/sessions/<stamp> ----
 $session = New-OwnerProjectSession -Root $root -Stamp '20261103T120000Z' -AddinAssembly (Join-Path $build 'Bim.Revit.dll') -DocumentPath $document
 Check ($session.Directory -eq [IO.Path]::GetFullPath((Join-Path $root '.bim/open-project/sessions/20261103T120000Z'))) 'session lives under the repository .bim/open-project/sessions (the add-in finds the enabled roots from there)'
 Check ((Test-Path -LiteralPath (Join-Path $session.Directory 'addin/Bim.Revit.dll')) -and (Test-Path -LiteralPath (Join-Path $session.Directory 'addin/Bim.Core.dll'))) 'the add-in and Bim.Core are staged inside the session'
 $manifestText = Get-Content -LiteralPath $session.Manifest -Raw
 Check ($manifestText -match '<AllowLoadIntoExistingSession>false</AllowLoadIntoExistingSession>' -and $manifestText -match '<FullClassName>Bim.Revit.App</FullClassName>' -and $manifestText.Contains([Security.SecurityElement]::Escape((Join-Path $session.Directory 'addin/Bim.Revit.dll')))) 'manifest points at the staged copy and never loads into a running session'
 Check ((Split-Path -Leaf $session.Manifest) -eq $OwnerManifestName) 'staged manifest has the installed manifest name'
 $config = Get-Content -LiteralPath $session.Config -Raw | ConvertFrom-Json
 Check ($config.ConfirmOwnerProject -eq $true -and $config.SessionDirectory -eq $session.Directory -and $config.DocumentPath -eq $document -and $config.ResultPath -eq $session.Result) 'project config confirms the mode and names the document, the session and the result'
 Check (!(Test-Path -LiteralPath $session.Result)) 'staging fabricates no result'
 Check ($null -ne (Throws { New-OwnerProjectSession -Root $root -Stamp '20261103T120000Z' -AddinAssembly (Join-Path $build 'Bim.Revit.dll') -DocumentPath $document })) 'an existing session directory is never reused'

 # ---- Leftover installed manifest: only one staged by this repository may be removed ----
 $addins = New-Item -ItemType Directory (Join-Path $work 'Addins/2025')
 $installed = Join-Path $addins $OwnerManifestName
 Check ($null -eq (Remove-OwnerLeftoverManifest -Root $root -Manifest $installed)) 'no installed manifest: nothing to remove'
 Copy-Item -LiteralPath $session.Manifest -Destination $installed
 Check ((Remove-OwnerLeftoverManifest -Root $root -Manifest $installed) -eq $session.Manifest -and !(Test-Path -LiteralPath $installed)) 'a leftover manifest staged by an open-project session is removed'
 $run = New-Item -ItemType Directory (Join-Path $root '.bim/native-runs/run-1')
 Set-Content (Join-Path $run $OwnerManifestName) '<RevitAddIns>fixture</RevitAddIns>'
 Copy-Item -LiteralPath (Join-Path $run $OwnerManifestName) -Destination $installed
 Check ((Remove-OwnerLeftoverManifest -Root $root -Manifest $installed) -eq (Join-Path $run $OwnerManifestName) -and !(Test-Path -LiteralPath $installed)) 'a leftover manifest staged by a native session run is removed'
 Set-Content $installed '<RevitAddIns>someone else</RevitAddIns>'
 Check ($null -ne (Throws { Remove-OwnerLeftoverManifest -Root $root -Manifest $installed }) -and (Test-Path -LiteralPath $installed)) 'a manifest this repository did not stage is refused and kept'
 Remove-Item -LiteralPath $installed

 # ---- Signals that the document is open in another Revit ----
 $revits = @([pscustomobject]@{ Id = 11; MainWindowTitle = 'Autodesk Revit 2025.2 - [bim-agentic-test - Floor Plan: Level 1]' }, [pscustomobject]@{ Id = 12; MainWindowTitle = 'Autodesk Revit 2025.2 - [Tower - 3D View: {3D}]' }, [pscustomobject]@{ Id = 13; MainWindowTitle = '' })
 $showing = @(Find-OwnerRevitShowingDocument -Processes $revits -DocumentPath $document)
 Check ($showing.Count -eq 1 -and $showing[0].Id -eq 11) 'a running Revit whose window title shows the document name (any case) is reported'
 Check (@(Find-OwnerRevitShowingDocument -Processes $revits -DocumentPath (Join-Path $projects 'Cabana.rvt')).Count -eq 0) 'Revit windows showing other documents are not reported'
 Check (!(Test-OwnerWorkshared -DocumentPath $document)) 'a plain document is not workshared'
 New-Item -ItemType Directory (Join-Path $projects 'BIM-Agentic-Test_backup') | Out-Null
 Check (Test-OwnerWorkshared -DocumentPath $document) 'a <name>_backup folder beside the document marks it workshared'
 Check (!(Test-OwnerFileInUse -DocumentPath $document)) 'a file nobody holds is not in use'
 if ($IsWindows) {
  $holder = [IO.File]::Open($document, 'Open', 'Read', 'Read')
  try { Check (Test-OwnerFileInUse -DocumentPath $document) 'a file another process holds open is in use (Windows share modes)' } finally { $holder.Dispose() }
 }

 # ---- Previous session still alive on the same document ----
 $latest = Join-Path $root '.bim/open-project/latest.json'
 Check ($null -eq (Get-OwnerLiveSession -LatestPath $latest -DocumentPath $document -IsAlive { $true })) 'no latest.json: no live session'
 Write-OwnerLatest -Path $latest -Record ([ordered]@{ pipe_name = 'bim-agentic-os-2025-77'; pid = 77; document_path = $document; document_unique_id = 'u'; started_utc = 'x' })
 Check ((Get-OwnerLiveSession -LatestPath $latest -DocumentPath $document.ToUpperInvariant() -IsAlive { param($id) $id -eq 77 }).pid -eq 77) 'a live child Revit of an earlier session on the same document is reported'
 Check ($null -eq (Get-OwnerLiveSession -LatestPath $latest -DocumentPath $document -IsAlive { $false })) 'an earlier session whose child exited is not live'
 Check ($null -eq (Get-OwnerLiveSession -LatestPath $latest -DocumentPath (Join-Path $projects 'Other.rvt') -IsAlive { $true })) 'an earlier session on another document does not block this one'
 Check (@(Get-ChildItem -LiteralPath (Split-Path $latest) -Filter '*.tmp').Count -eq 0) 'latest.json is written without leaving temporary files'

 # ---- Result provenance ----
 function Result([hashtable]$change = @{}) {
  $values = [ordered]@{ status = 'succeeded'; mode = 'project'; processId = 4242; pipeName = 'bim-agentic-os-2025-4242'; documentPath = $document; documentUniqueId = '60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b' }
  foreach ($key in $change.Keys) { $values[$key] = $change[$key] }
  [pscustomobject]$values
 }
 Check ($null -eq (Test-OwnerProjectResult -Result (Result) -ChildId 4242 -DocumentPath $document)) 'a result from this child, its pipe and the requested document is accepted'
 Check ((Test-OwnerProjectResult -Result (Result @{ status = 'failed'; code = 'document_not_enabled' }) -ChildId 4242 -DocumentPath $document) -match 'document_not_enabled') 'a failed bootstrap reports its code'
 Check ($null -ne (Test-OwnerProjectResult -Result (Result) -ChildId 4243 -DocumentPath $document)) 'a result from another process is refused'
 Check ($null -ne (Test-OwnerProjectResult -Result (Result @{ pipeName = 'bim-agentic-os-2025-1' }) -ChildId 4242 -DocumentPath $document)) 'a result naming another pipe is refused'
 Check ($null -ne (Test-OwnerProjectResult -Result (Result @{ documentPath = (Join-Path $projects 'Other.rvt') }) -ChildId 4242 -DocumentPath $document)) 'a result for another document is refused'
 Check ($null -ne (Test-OwnerProjectResult -Result (Result @{ mode = 'fixture' }) -ChildId 4242 -DocumentPath $document)) 'a fixture result is not a project result'
 Check ($null -ne (Test-OwnerProjectResult -Result (Result @{ documentUniqueId = '' }) -ChildId 4242 -DocumentPath $document)) 'a result without the ProjectInformation UniqueId is refused'
 Write-Output "PASS owner open-project helpers ($passed checks); no Revit launched"
} finally {
 $cleanup = [IO.Path]::GetFullPath($work)
 $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
 if (!$cleanup.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase) -or !([IO.Path]::GetFileName($cleanup)).StartsWith('owner-open-project-', [StringComparison]::Ordinal)) { throw 'Unsafe temporary cleanup target' }
 Remove-Item -LiteralPath $cleanup -Recurse -Force
}
