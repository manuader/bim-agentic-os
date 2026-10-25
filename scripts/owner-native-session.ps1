# One owner-attended native session: native source check, Release builds, then N isolated Revit A/B runs.
# Each run uses a NEW child Revit process with its own run directory and staged manifest. Revit processes that
# were already running (for example a production model) are recorded and never touched. The unsigned add-in
# prompt needs the owner's Load Once in each child; this script only waits for it, it never answers it.
[CmdletBinding()]
param(
 [ValidateRange(1,10)][int]$Runs = 3,
 [string]$TemplatePath = 'C:/ProgramData/Autodesk/RVT 2025/Templates/Default_M_ENU.rte',
 [string]$RevitExe = 'C:/Program Files/Autodesk/Revit 2025/Revit.exe',
 [ValidateRange(60,1800)][int]$LoadWaitSeconds = 900,
 [ValidateRange(30,900)][int]$EvaluationTimeoutSeconds = 300,
 [ValidateRange(0,600)][int]$CloseWaitSeconds = 180,
 [switch]$SkipSourceCheck
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 (pwsh) required: run this script with pwsh -File' }
if (!$IsWindows) { throw 'Windows with Revit 2025 required' }
$root = Split-Path $PSScriptRoot
Set-Location $root
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$session = Join-Path $root ".bim/native-sessions/$stamp"
New-Item -ItemType Directory -Force $session | Out-Null
$log = Join-Path $session 'session.log'
function Say([string]$text) { $line = '[' + [DateTime]::UtcNow.ToString('HH:mm:ssZ') + '] ' + $text; Write-Host $line; Add-Content -LiteralPath $log -Value $line }
function Sha([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
$summary = [ordered]@{ schema_version = 1; started_utc = [DateTime]::UtcNow.ToString('o'); session = $session; head = ''; protected_revit_pids = @(); source_check = 'not_run'; builds = [ordered]@{}; runs = @(); finished_utc = $null; outcome = 'incomplete' }
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
  & pwsh -NoProfile -File (Join-Path $root 'scripts/check-native-source.ps1') *>&1 | Tee-Object -FilePath $checkLog | Out-Host
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
  $run = [ordered]@{ index = $i; run_directory = ''; child_pid = $null; bootstrap = 'not_started'; evaluation_exit = $null; outcome = $null; verification_state = $null; product_acceptance = $null; counts = $null; total_ms = $null; revit_execute_ms = $null; staged_addin_sha256 = $null; closed = $null }
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
  Copy-Item -LiteralPath (Join-Path $runDir 'Bim.Agentic.Fixture.addin') -Destination $manifest
  $config = Join-Path $runDir 'fixture-config.json'
  $result = Join-Path $runDir 'fixture-result.json'
  $start = [Diagnostics.ProcessStartInfo]::new($RevitExe)
  $start.UseShellExecute = $false
  $start.Environment['BIM_AGENTIC_TEST_CONFIG'] = $config
  $child = [Diagnostics.Process]::Start($start)
  $run.child_pid = $child.Id
  @{ process_id = $child.Id; run_directory = $runDir; manifest = $manifest; manifest_sha256 = (Sha $manifest); started_utc = [DateTime]::UtcNow.ToString('o'); protected_pids = $summary.protected_revit_pids } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDir 'controller-launch.json')
  Save
  Say ">>> Run ${i}: Revit child PID $($child.Id) started. Choose 'Load Once' for 'BIM Agentic OS Fixture' in THAT window when the security prompt appears. Waiting up to $LoadWaitSeconds s."
  $deadline = [DateTime]::UtcNow.AddSeconds($LoadWaitSeconds)
  while ([DateTime]::UtcNow -lt $deadline -and !(Test-Path -LiteralPath $result)) {
   if ($child.HasExited) { break }
   Start-Sleep -Milliseconds 500
  }
  try {
   if (!(Test-Path -LiteralPath $result)) { $run.bootstrap = $(if ($child.HasExited) { 'child_exited' } else { 'timed_out' }); throw "Run $i bootstrap $($run.bootstrap); no fixture evidence" }
   Start-Sleep -Milliseconds 500
   $boot = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
   if ($boot.status -ne 'succeeded') { $run.bootstrap = "failed:$($boot.code)"; throw "Run $i bootstrap failed: $($boot.code)" }
   if ($boot.processId -ne $child.Id -or $boot.pipeName -ne ('bim-agentic-os-2025-' + $child.Id)) { $run.bootstrap = 'provenance_mismatch'; throw "Run $i fixture evidence does not match child PID $($child.Id)" }
   $run.bootstrap = 'succeeded'
   Say "Run ${i}: fixture ready in PID $($child.Id); running the A/B evaluation (no automatic retry)"
   & (Join-Path $root 'tests/integration/native-room-ab.ps1') -RunDirectory $runDir -DocumentPath $boot.documentPath -DocumentIdentity $boot.documentUniqueId -ProcessId $child.Id -PipeName $boot.pipeName -Dotnet $dotnet -CliDll $cli -TimeoutSeconds $EvaluationTimeoutSeconds | Out-Null
   $run.evaluation_exit = $LASTEXITCODE
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
  finally {
   if ((Test-Path -LiteralPath $manifest) -and (Sha $manifest) -eq (Sha (Join-Path $runDir 'Bim.Agentic.Fixture.addin'))) { Remove-Item -LiteralPath $manifest }
   if (!$child.HasExited -and $CloseWaitSeconds -gt 0) {
    $null = $child.CloseMainWindow()
    $run.closed = $child.WaitForExit($CloseWaitSeconds * 1000)
    if (!$run.closed) { Say "Run ${i}: child PID $($child.Id) did not close in $CloseWaitSeconds s; left running, close it yourself (not production)" }
   }
   Save
  }
 }
 $accepted = @($summary.runs | Where-Object { $_.product_acceptance -eq $true }).Count
 $summary.outcome = "$accepted/$Runs runs product_acceptance=true"
 Say "Session finished: $($summary.outcome)"
}
catch { Say "STOPPED: $($_.Exception.Message)"; $summary.outcome = 'stopped: ' + $_.Exception.Message; throw }
finally { Save; Say "Summary: $(Join-Path $session 'summary.json')" }
