# Owner project mode: open ONE real project (an existing .rvt under an enabled root) in a NEW child Revit that exposes
# the BIM Agentic OS command pipe, so Claude can drive it through the CLI/MCP. No copy, no fixture probes.
#   pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-open-project.ps1 -Path 'C:\...\Proyecto.rvt'
#   pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-open-project.ps1 -Stop
# Safety: the document must pass `Bim.Cli open-project-check` (repository .bim/native-runs or a root listed in the
# owner file .bim/revit-enabled-roots.txt, no links, an existing non-backup .rvt) and the add-in checks it again
# (ProjectConfig / OwnerProjectGuard). Revit processes already running are recorded and never touched; the script never
# kills or closes a Revit. The staged manifest stays installed until the child reports its result, exits or times out; do not start another Revit until LISTO.
# The child gets BIM_AGENTIC_PROJECT_CONFIG and never BIM_AGENTIC_TEST_CONFIG. Evidence and the connection record:
# .bim/open-project/sessions/<utc>/ and .bim/open-project/latest.json. Owner guide: docs/owner-open-project.md.
[CmdletBinding(DefaultParameterSetName = 'Open')]
param(
 [Parameter(Mandatory, ParameterSetName = 'Open', Position = 0)][string]$Path,
 [Parameter(Mandatory, ParameterSetName = 'Stop')][switch]$Stop,
 [Parameter(ParameterSetName = 'Open')][string]$RevitExe = 'C:/Program Files/Autodesk/Revit 2025/Revit.exe',
 [Parameter(ParameterSetName = 'Open')][ValidateRange(60, 1800)][int]$LoadWaitSeconds = 900,
 [Parameter(ParameterSetName = 'Open')][switch]$SkipSourceCheck
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion -lt [version]'7.2') { throw 'Se necesita PowerShell 7.2+ (pwsh): ejecute este script con pwsh -File' }
if (!$IsWindows) { throw 'Se necesita Windows con Revit 2025' }
$root = Split-Path $PSScriptRoot
Set-Location $root
. (Join-Path $PSScriptRoot 'owner-revit-common.ps1')
$latestPath = Join-Path $root '.bim/open-project/latest.json'
$manifest = Get-OwnerInstalledManifest
function IsRevitAlive([int]$id) { $process = Get-Process -Id $id -ErrorAction SilentlyContinue; [bool]($process -and $process.ProcessName -eq 'Revit' -and !$process.HasExited) }

if ($Stop) {
 # Cleanup only: remove an installed manifest this repository staged. The child Revit is never closed or killed here.
 $removed = Remove-OwnerLeftoverManifest -Root $root -Manifest $manifest
 Write-Host $(if ($removed) { "Manifiesto de prueba quitado (era $removed)." } else { 'No había manifiesto de prueba instalado.' })
 if (Test-Path -LiteralPath $latestPath -PathType Leaf) {
  $latest = Get-Content -LiteralPath $latestPath -Raw | ConvertFrom-Json
  if (IsRevitAlive ([int]$latest.pid)) { Write-Host ">>> El Revit del proyecto (PID $($latest.pid)) sigue abierto. Guarde con Ctrl+S si quiere conservar los cambios y ciérrelo usted: este script nunca cierra Revit." }
  else { Write-Host "El Revit del proyecto (PID $($latest.pid)) ya está cerrado; el canal $($latest.pipe_name) ya no existe." }
 }
 return
}

$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$logDirectory = Join-Path $root ".bim/open-project/logs/$stamp"
New-Item -ItemType Directory -Force $logDirectory | Out-Null
$log = Join-Path $logDirectory 'open-project.log'
function Say([string]$text) { $line = '[' + [DateTime]::UtcNow.ToString('HH:mm:ssZ') + '] ' + $text; Write-Host $line; Add-Content -LiteralPath $log -Value $line }

try {
 # ---- 1. Cheap checks before building anything ----
 if (![IO.Path]::IsPathFullyQualified($Path)) { throw "Use la ruta completa del .rvt (por ejemplo C:\Users\...\Proyecto.rvt), no '$Path'" }
 $Path = [IO.Path]::GetFullPath($Path)
 if ([IO.Path]::GetExtension($Path) -ne '.rvt') { throw "No es un archivo de proyecto .rvt: $Path" }
 if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "No existe el archivo: $Path" }
 if (!(Test-Path -LiteralPath $RevitExe -PathType Leaf)) { throw "No encuentro Revit en $RevitExe" }
 $protected = @(Get-Process -Name Revit -ErrorAction SilentlyContinue)
 $protectedPids = @($protected | ForEach-Object Id)
 Say ("Revit ya abiertos (nunca se tocan): " + $(if ($protectedPids.Count) { $protectedPids -join ', ' } else { 'ninguno' }))
 $showing = @(Find-OwnerRevitShowingDocument -Processes $protected -DocumentPath $Path)
 if ($showing.Count) { throw ("Parece que el proyecto ya está abierto en otro Revit (PID " + (($showing | ForEach-Object Id) -join ', ') + ", ventana '" + $showing[0].MainWindowTitle + "'). Guárdelo y ciérrelo allí primero: dos Revit sobre el mismo archivo pisan los cambios.") }
 if (Test-OwnerWorkshared -DocumentPath $Path) { throw 'El proyecto parece compartido (workshared: hay una carpeta <nombre>_backup al lado). El modo proyecto no admite modelos compartidos.' }
 if (Test-OwnerFileInUse -DocumentPath $Path) { throw 'Otro programa tiene abierto el archivo (¿Revit, OneDrive sincronizando, una copia de seguridad?). Ciérrelo y vuelva a intentar.' }
 $live = Get-OwnerLiveSession -LatestPath $latestPath -DocumentPath $Path -IsAlive { param($id) IsRevitAlive $id }
 if ($live) { throw "Este proyecto ya está abierto en un Revit del modo proyecto (PID $($live.pid), canal $($live.pipe_name)). Siga usándolo o ciérrelo antes de abrir otro." }

 # ---- 2. Builds (same as owner-native-session.ps1) ----
 if ($SkipSourceCheck) { Say 'Chequeo de fuentes nativas omitido a pedido' }
 else {
  Say 'Paso 1: chequeo de fuentes nativas contra las DLL de Revit instaladas'
  $checkLog = Join-Path $logDirectory 'native-source-check.log'
  & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $root 'scripts/check-native-source.ps1') *>&1 | Tee-Object -FilePath $checkLog | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "Falló el chequeo de fuentes nativas; vea $checkLog" }
 }
 $dotnet = Get-OwnerDotnet $root
 Say "Paso 2: compilación Release con $dotnet"
 Invoke-OwnerReleaseBuild -Root $root -Dotnet $dotnet -LogDirectory $logDirectory -Projects @('runtimes/revit-native/Bim.Revit.csproj', 'interfaces/cli/Bim.Cli.csproj')
 $assembly = (Resolve-Path (Join-Path $root 'runtimes/revit-native/bin/Release/net8.0-windows/Bim.Revit.dll')).Path
 $cli = (Resolve-Path (Join-Path $root 'interfaces/cli/bin/Release/net8.0/Bim.Cli.dll')).Path

 # ---- 3. Enabled roots: the same decision the add-in and every write command apply ----
 $checkText = (& $dotnet $cli open-project-check $Path | Out-String).Trim()
 $check = $checkText | ConvertFrom-Json
 if ($LASTEXITCODE -ne 0 -or !$check.PSObject.Properties['status'] -or $check.status -ne 'succeeded') {
  if ($check.code -eq 'document_not_enabled') { throw "El proyecto no está en una carpeta habilitada. Agregue la carpeta (una ruta completa por línea) a $(Join-Path $root '.bim/revit-enabled-roots.txt') y vuelva a intentar. Detalle: $(if ($check.PSObject.Properties['detail']) { $check.detail })" }
  throw "El proyecto no se puede abrir en modo proyecto: $(if ($check.PSObject.Properties['code']) { $check.code }) ($(if ($check.PSObject.Properties['detail']) { $check.detail } else { $checkText }))"
 }
 Say "Proyecto habilitado por la carpeta $($check.enabled_root)"

 # ---- 4. Stage and launch a NEW child Revit ----
 $leftover = Remove-OwnerLeftoverManifest -Root $root -Manifest $manifest
 if ($leftover) { Say "Quité un manifiesto que quedó de una ejecución anterior ($leftover)" }
 $session = New-OwnerProjectSession -Root $root -Stamp $stamp -AddinAssembly $assembly -DocumentPath $Path
 if ((Get-OwnerSha (Join-Path $session.Directory 'addin/Bim.Revit.dll')) -ne (Get-OwnerSha $assembly)) { throw 'El complemento copiado difiere del compilado' }
 Copy-Item -LiteralPath $log -Destination (Join-Path $session.Directory 'open-project.log')
 $log = Join-Path $session.Directory 'open-project.log'
 # Re-check just before launching: the builds take minutes and the owner may have opened the file meanwhile.
 $showingNow = @(Find-OwnerRevitShowingDocument -Processes @(Get-Process -Name Revit -ErrorAction SilentlyContinue) -DocumentPath $Path)
 if ($showingNow.Count) { throw ("El proyecto se abrió en otro Revit mientras compilaba (PID " + (($showingNow | ForEach-Object Id) -join ', ') + "). Ciérrelo allí y vuelva a intentar.") }
 if (Test-OwnerFileInUse -DocumentPath $Path) { throw 'Otro programa abrió el archivo mientras compilaba. Ciérrelo y vuelva a intentar.' }
 if (Test-Path -LiteralPath $manifest) { throw "Apareció un manifiesto en $manifest durante la preparación; me detengo" }
 New-Item -ItemType Directory -Force (Split-Path $manifest) | Out-Null
 Copy-Item -LiteralPath $session.Manifest -Destination $manifest
 $child = $null
 try {
  $start = [Diagnostics.ProcessStartInfo]::new($RevitExe)
  $start.UseShellExecute = $false
  $null = $start.Environment.Remove('BIM_AGENTIC_TEST_CONFIG')
  $start.Environment['BIM_AGENTIC_PROJECT_CONFIG'] = $session.Config
  $child = [Diagnostics.Process]::Start($start)
  if ($protectedPids -contains $child.Id) { throw 'El proceso nuevo tiene el PID de un Revit que ya estaba abierto; me detengo' }
  [ordered]@{ process_id = $child.Id; document_path = $Path; session_directory = $session.Directory; manifest = $manifest; manifest_sha256 = (Get-OwnerSha $manifest); started_utc = [DateTime]::UtcNow.ToString('o'); protected_pids = $protectedPids } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $session.Directory 'controller-launch.json')
  Say ">>> Se abrió un Revit NUEVO (PID $($child.Id)). Cuando aparezca el aviso de seguridad de 'BIM Agentic OS Project' en ESA ventana, haga clic en 'Load Once' (Cargar una vez). Espero hasta $LoadWaitSeconds s."
  Say '>>> No abra otro Revit mientras tanto. Los Revit que ya estaban abiertos no se tocan.'
  $deadline = [DateTime]::UtcNow.AddSeconds($LoadWaitSeconds)
  while ([DateTime]::UtcNow -lt $deadline -and !(Test-Path -LiteralPath $session.Result)) {
   if ($child.HasExited) { break }
   Start-Sleep -Milliseconds 500
  }
 }
 finally {
  # Revit reads manifests at startup only: once the child loaded the add-in (or failed) the manifest is removed, so a
  # Revit opened later never sees it.
  if (Remove-OwnerManifestIfOwned -Manifest $manifest -Staged $session.Manifest) { Say 'Manifiesto de prueba quitado.' }
 }
 if (!(Test-Path -LiteralPath $session.Result)) {
  if ($child.HasExited) { throw 'El Revit nuevo se cerró antes de abrir el proyecto.' }
  throw "No hubo respuesta del complemento en $LoadWaitSeconds s (¿se eligió 'Load Once'?). El Revit nuevo (PID $($child.Id)) queda abierto; ciérrelo usted."
 }
 Start-Sleep -Milliseconds 500
 $boot = Get-Content -LiteralPath $session.Result -Raw | ConvertFrom-Json
 if (Test-OwnerProjectResult -Result $boot -ChildId $child.Id -DocumentPath $Path) { throw ((Test-OwnerProjectResult -Result $boot -ChildId $child.Id -DocumentPath $Path) + ". El Revit nuevo (PID $($child.Id)) queda abierto sin canal; ciérrelo usted.") }

 # ---- 5. Connection record for Claude ----
 $record = [ordered]@{ pipe_name = $boot.pipeName; pid = $child.Id; document_path = $boot.documentPath; document_unique_id = $boot.documentUniqueId; started_utc = [DateTime]::UtcNow.ToString('o'); session_directory = $session.Directory; revit_build = $boot.revitBuild; protected_revit_pids = $protectedPids }
 Write-OwnerLatest -Path $latestPath -Record $record
 Copy-Item -LiteralPath $latestPath -Destination (Join-Path $session.Directory 'latest.json')
 Say '>>> LISTO. Revit tiene abierto su proyecto y el canal de BIM Agentic OS está activo.'
 Write-Host ''
 Write-Host '=== Copie y pegue esto en el chat con Claude ==='
 Write-Host "Abrí mi proyecto en Revit con owner-open-project. Usá estos datos para las herramientas revit_* (MCP):"
 Write-Host "  pipe_name: $($boot.pipeName)"
 Write-Host "  document.path: $($boot.documentPath)"
 Write-Host "  document.unique_id: $($boot.documentUniqueId)"
 Write-Host "(También están en $latestPath)"
 Write-Host '================================================'
 Write-Host ''
 Write-Host 'Recuerde: los cambios de Claude quedan en el modelo abierto; guárdelos usted con Ctrl+S en ese Revit. Al cerrar ese Revit el canal desaparece.'
}
catch { Say "DETENIDO: $($_.Exception.Message)"; throw }
