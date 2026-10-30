# One owner-attended native session: native source check, Release builds, then N isolated Revit A/B runs.
# Each run uses a NEW child Revit process with its own run directory and staged manifest. Revit processes that
# were already running (for example a production model) are recorded and never touched. The unsigned add-in
# prompt needs the owner's Load Once in each child; this script only waits for it, it never answers it.
# -Catalogue (C04) replaces the A/B evaluation with the scripted catalogue v1 smoke of scripts/owner-catalogue-steps.ps1
# through the CLI revit-command verb, records .bim/native-sessions/<utc>/catalogue-run<N>.json and, unless
# -CloseWaitSeconds is given, leaves the child Revit open so the owner can look at the (unsaved) result.
[CmdletBinding()]
param(
 [ValidateRange(1,10)][int]$Runs = 3,
 [string]$TemplatePath = 'C:/ProgramData/Autodesk/RVT 2025/Templates/Default_M_ENU.rte',
 [string]$RevitExe = 'C:/Program Files/Autodesk/Revit 2025/Revit.exe',
 [ValidateRange(60,1800)][int]$LoadWaitSeconds = 900,
 [ValidateRange(30,900)][int]$EvaluationTimeoutSeconds = 300,
 [ValidateRange(0,600)][int]$CloseWaitSeconds = 180,
 [switch]$SkipSourceCheck,
 [switch]$Catalogue,
 [ValidateRange(10,1800)][int]$StepTimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion -lt [version]'7.2') { throw 'PowerShell 7.2+ (pwsh) required: run this script with pwsh -File' }
if (!$IsWindows) { throw 'Windows with Revit 2025 required' }
$root = Split-Path $PSScriptRoot
Set-Location $root
. (Join-Path $PSScriptRoot 'owner-catalogue-steps.ps1')
if ($Catalogue -and !$PSBoundParameters.ContainsKey('Runs')) { $Runs = 1 }
if ($Catalogue -and !$PSBoundParameters.ContainsKey('CloseWaitSeconds')) { $CloseWaitSeconds = 0 }
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$session = Join-Path $root ".bim/native-sessions/$stamp"
New-Item -ItemType Directory -Force $session | Out-Null
$log = Join-Path $session 'session.log'
function Say([string]$text) { $line = '[' + [DateTime]::UtcNow.ToString('HH:mm:ssZ') + '] ' + $text; Write-Host $line; Add-Content -LiteralPath $log -Value $line }
function Sha([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
$summary = [ordered]@{ schema_version = 1; mode = $(if ($Catalogue) { 'catalogue' } else { 'room_ab' }); started_utc = [DateTime]::UtcNow.ToString('o'); session = $session; head = ''; protected_revit_pids = @(); source_check = 'not_run'; builds = [ordered]@{}; runs = @(); finished_utc = $null; outcome = 'incomplete' }
function Save { $summary.finished_utc = [DateTime]::UtcNow.ToString('o'); $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $session 'summary.json') }

try {
 $summary.head = (& git -C $root rev-parse --short HEAD).Trim()
 Say "Repo head $($summary.head); session evidence in $session"
 $summary.protected_revit_pids = @(Get-Process -Name Revit -ErrorAction SilentlyContinue | ForEach-Object Id)
 Say ("Revit processes already running (never touched): " + ($(if ($summary.protected_revit_pids.Count) { $summary.protected_revit_pids -join ', ' } else { 'none' })))
 foreach ($path in @($TemplatePath, $RevitExe)) { if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required input missing: $path" } }

 if ($SkipSourceCheck) { $summary.source_check = 'skipped'; Say 'Native source check skipped by request' }
 else {
  Say 'Step 1: native source check against installed Revit DLLs'
  $checkLog = Join-Path $session 'native-source-check.log'
  & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $root 'scripts/check-native-source.ps1') *>&1 | Tee-Object -FilePath $checkLog | Out-Host
  if ($LASTEXITCODE -ne 0) { $summary.source_check = 'failed'; throw "Native source check failed; see $checkLog" }
  $summary.source_check = 'passed'
  Say 'Native source check PASSED'
 }

 $dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
 if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
 Say "Step 2: Release builds with $dotnet"
 foreach ($project in @('runtimes/revit-native/Bim.Revit.csproj', 'interfaces/cli/Bim.Cli.csproj')) {
  $buildLog = Join-Path $session ('build-' + [IO.Path]::GetFileNameWithoutExtension($project) + '.log')
  & $dotnet build (Join-Path $root $project) --configuration Release --warnaserror *>&1 | Tee-Object -FilePath $buildLog | Out-Host
  if ($LASTEXITCODE -ne 0) { $summary.builds[$project] = 'failed'; throw "Build failed: $project; see $buildLog" }
  $summary.builds[$project] = 'passed'
 }
 $assembly = (Resolve-Path (Join-Path $root 'runtimes/revit-native/bin/Release/net8.0-windows/Bim.Revit.dll')).Path
 $coreDll = Join-Path (Split-Path $assembly) 'Bim.Core.dll'
 $cli = (Resolve-Path (Join-Path $root 'interfaces/cli/bin/Release/net8.0/Bim.Cli.dll')).Path
 $summary.builds['Bim.Revit.dll_sha256'] = Sha $assembly
 $summary.builds['Bim.Core.dll_sha256'] = Sha $coreDll
 Say "Built Bim.Revit.dll SHA256 $($summary.builds['Bim.Revit.dll_sha256'])"
 Save

 $addins = Join-Path $env:APPDATA 'Autodesk/Revit/Addins/2025'
 $manifest = Join-Path $addins 'Bim.Agentic.Fixture.addin'
 if (Test-Path -LiteralPath $manifest) {
  $existing = Sha $manifest
  $owned = @(Get-ChildItem (Join-Path $root '.bim/native-runs') -Directory -ErrorAction SilentlyContinue | ForEach-Object { Join-Path $_.FullName 'Bim.Agentic.Fixture.addin' } | Where-Object { (Test-Path -LiteralPath $_) -and (Sha $_) -eq $existing })
  if (!$owned.Count) { throw "Existing $manifest is not a staged fixture manifest from this repo; review it manually" }
  Remove-Item -LiteralPath $manifest
  Say "Removed leftover fixture manifest from an earlier run ($($owned[0]))"
 }

 for ($i = 1; $i -le $Runs; $i++) {
  $run = [ordered]@{ index = $i; run_directory = ''; child_pid = $null; bootstrap = 'not_started'; evaluation_exit = $null; outcome = $null; verification_state = $null; product_acceptance = $null; counts = $null; total_ms = $null; revit_execute_ms = $null; staged_addin_sha256 = $null; closed = $null; catalogue = $null }
  $summary.runs += $run
  $runDir = Join-Path $root ('.bim/native-runs/' + [guid]::NewGuid())
  $run.run_directory = $runDir
  Say "Run $i/$Runs : preparing $runDir"
  & (Join-Path $root 'tests/integration/launch-native-fixture.ps1') -RunDirectory $runDir -TemplatePath $TemplatePath -AddinAssembly $assembly -RevitExe $RevitExe -ConfirmIsolatedTestProcess | Out-Null
  $staged = Join-Path $runDir 'addin/Bim.Revit.dll'
  $run.staged_addin_sha256 = Sha $staged
  if ($run.staged_addin_sha256 -ne $summary.builds['Bim.Revit.dll_sha256']) { throw 'Staged add-in differs from the build output' }
  if (Test-Path -LiteralPath $manifest) { throw "Manifest appeared at $manifest during the session; stopping" }
  New-Item -ItemType Directory -Force $addins | Out-Null
  $child = $null
  Copy-Item -LiteralPath (Join-Path $runDir 'Bim.Agentic.Fixture.addin') -Destination $manifest
  try {
  $config = Join-Path $runDir 'fixture-config.json'
  $result = Join-Path $runDir 'fixture-result.json'
  $start = [Diagnostics.ProcessStartInfo]::new($RevitExe)
  $start.UseShellExecute = $false
  $start.Environment['BIM_AGENTIC_TEST_CONFIG'] = $config
  $child = [Diagnostics.Process]::Start($start)
  $run.child_pid = $child.Id
  @{ process_id = $child.Id; run_directory = $runDir; manifest = $manifest; manifest_sha256 = (Sha $manifest); started_utc = [DateTime]::UtcNow.ToString('o'); protected_pids = $summary.protected_revit_pids } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDir 'controller-launch.json')
  Save
  Say ">>> Ejecución ${i}: se abrió un Revit nuevo (PID $($child.Id)). Cuando aparezca el aviso de seguridad de 'BIM Agentic OS Fixture' en ESA ventana, haga clic en 'Load Once'. Espero hasta $LoadWaitSeconds s."
  Say '>>> No abra otro Revit hasta que termine esta sesión: mientras el manifiesto de prueba está instalado, un Revit nuevo cargaría el complemento de prueba.'
  $deadline = [DateTime]::UtcNow.AddSeconds($LoadWaitSeconds)
  while ([DateTime]::UtcNow -lt $deadline -and !(Test-Path -LiteralPath $result)) {
   if ($child.HasExited) { break }
   Start-Sleep -Milliseconds 500
  }
   if (!(Test-Path -LiteralPath $result)) { $run.bootstrap = $(if ($child.HasExited) { 'child_exited' } else { 'timed_out' }); throw "Run $i bootstrap $($run.bootstrap); no fixture evidence" }
   Start-Sleep -Milliseconds 500
   $boot = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
   if ($boot.status -ne 'succeeded') { $run.bootstrap = "failed:$($boot.code)"; throw "Run $i bootstrap failed: $($boot.code)" }
   if ($boot.processId -ne $child.Id -or $boot.pipeName -ne ('bim-agentic-os-2025-' + $child.Id)) { $run.bootstrap = 'provenance_mismatch'; throw "Run $i fixture evidence does not match child PID $($child.Id)" }
   $expectedDocument = [IO.Path]::GetFullPath((Join-Path $runDir 'BIM-Agentic-Test.rvt'))
   if (![IO.Path]::GetFullPath([string]$boot.documentPath).Equals($expectedDocument, [StringComparison]::OrdinalIgnoreCase)) { $run.bootstrap = 'document_mismatch'; throw "Run $i fixture document is not this run's own fixture" }
   $run.bootstrap = 'succeeded'
   if ($Catalogue) {
    Say ">>> Ejecución ${i}: Revit está listo (PID $($child.Id)). Ahora el script crea muros, una puerta, una ventana, un suelo y una habitación en el modelo de prueba. No toque Revit hasta que termine."
    $catalogueRequests = Join-Path $runDir 'catalogue-requests'
    $catalogueInvoke = { param($index, $key, $json) Invoke-CatalogueCli -Dotnet $dotnet -CliDll $cli -PipeName $boot.pipeName -RequestDirectory $catalogueRequests -Index $index -Key $key -Json $json -TimeoutSeconds $StepTimeoutSeconds }
    $sequence = Invoke-CatalogueSequence -Stamp $stamp -Run $i -DocumentPath $boot.documentPath -DocumentUid $boot.documentUniqueId -Invoke $catalogueInvoke -Progress { param($line) Say "  $line" } -Alive { !$child.HasExited }
    $cataloguePath = Join-Path $session "catalogue-run$i.json"
    [ordered]@{
     schema_version = 1; session_stamp = $stamp; run = $i; head = $summary.head; child_pid = $child.Id; pipe_name = $boot.pipeName
     document_path = $boot.documentPath; document_unique_id = $boot.documentUniqueId; revit_build = $boot.revitBuild
     result_line = (Format-CatalogueResult $sequence.counts); counts = $sequence.counts; picked = $sequence.picked
     document_saved = $false
     document_note = 'Catalogue v1 has no save command and this session adds none: the changes live only in the open child Revit. Closing it without saving discards them.'
     steps = $sequence.steps
    } | ConvertTo-Json -Depth 24 | Set-Content -LiteralPath $cataloguePath
    $run.catalogue = [ordered]@{ file = $cataloguePath; counts = $sequence.counts }
    $run.outcome = Format-CatalogueResult $sequence.counts
    foreach ($step in @($sequence.steps | Where-Object { !$_.passed })) { Say "  Falló el paso $($step.index) ($($step.description)): $($step.reason)" }
    Say ">>> Ejecución ${i}: $($run.outcome). Detalle: $cataloguePath"
   }
   else {
    Say "Run ${i}: fixture ready in PID $($child.Id); running the A/B evaluation (no automatic retry)"
     try {
      & (Join-Path $root 'tests/integration/native-room-ab.ps1') -RunDirectory $runDir -DocumentPath $boot.documentPath -DocumentIdentity $boot.documentUniqueId -ProcessId $child.Id -PipeName $boot.pipeName -Dotnet $dotnet -CliDll $cli -TimeoutSeconds $EvaluationTimeoutSeconds | Out-Null
      $run.evaluation_exit = $LASTEXITCODE
     } catch { $run.evaluation_exit = 'wrapper_error: ' + $_.Exception.Message; throw }
     $reportPath = Join-Path $runDir 'native-room-report.json'
     if (Test-Path -LiteralPath $reportPath) {
      $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
      $run.outcome = $report.outcome
      $run.verification_state = $report.verification_state
      $run.product_acceptance = $report.product_acceptance
      $run.counts = $report.counts
      $run.total_ms = $report.metrics.total_ms.value
      $run.revit_execute_ms = $report.metrics.revit_execute_ms.value
     }
     Say "Run ${i}: outcome $($run.outcome), product_acceptance $($run.product_acceptance), total_ms $($run.total_ms)"
   }
  }
  finally {
   if ((Test-Path -LiteralPath $manifest) -and (Sha $manifest) -eq (Sha (Join-Path $runDir 'Bim.Agentic.Fixture.addin'))) { Remove-Item -LiteralPath $manifest }
   if ($child -and !$child.HasExited -and $CloseWaitSeconds -gt 0) {
    $null = $child.CloseMainWindow()
    $run.closed = $child.WaitForExit($CloseWaitSeconds * 1000)
    if (!$run.closed) { Say "Run ${i}: child PID $($child.Id) did not close in $CloseWaitSeconds s; left running, close it yourself (not production)" }
   }
   elseif ($child -and !$child.HasExited) { Say ">>> Ejecución ${i}: el Revit de prueba (PID $($child.Id)) queda abierto para que mire el resultado. El modelo de prueba NO está guardado; al cerrarlo, Revit preguntará si guardar: puede elegir No." }
   Save
  }
 }
 if ($Catalogue) {
  $total = [ordered]@{ passed = 0; total = 0 }
  foreach ($done in @($summary.runs | Where-Object { $_.catalogue })) { $total.passed += $done.catalogue.counts.passed; $total.total += $done.catalogue.counts.total }
  $summary.outcome = Format-CatalogueResult $total
  Say ">>> Sesión terminada. $($summary.outcome)"
 }
 else {
  $accepted = @($summary.runs | Where-Object { $_.product_acceptance -eq $true }).Count
  $summary.outcome = "$accepted/$Runs runs product_acceptance=true"
  Say "Session finished: $($summary.outcome)"
 }
}
catch { Say "DETENIDO (STOPPED): $($_.Exception.Message)"; $summary.outcome = 'stopped: ' + $_.Exception.Message; throw }
finally { Save; Say "Summary: $(Join-Path $session 'summary.json')" }
