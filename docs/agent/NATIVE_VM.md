# Native evaluation on a Windows VM

Owner-attended shortcut (one command, from the repo root): `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Runs 3`. It runs the native source check, the Release builds, then N A/B runs, each in a new child Revit with its own staged manifest (installed and removed per run). Already running Revit processes are recorded and never touched. The owner chooses Load Once in each child; the script never answers the prompt. Evidence: `.bim/native-sessions/<utc>/summary.json`.

Catalogue v1 owner smoke (C04, one command from the repo root): `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Catalogue`. Same safety logic (source check, Release builds, one new child Revit with a staged manifest, protected PIDs, manifest cleanup); the owner clicks Load Once once. After the fixture bootstrap it drives 17 steps through `Bim.Cli revit-command` against that child's pipe and fixture (`scripts/owner-catalogue-steps.ps1`): queries for levels, wall/floor types and door/window symbols (references picked from the results), four straight walls closing a 6000x4000 mm rectangle, a centre-arc wall (r 3000 mm, 0-90 degrees) outside it, a door and a window (sill 900 mm), the floor, room "Prueba", a same-operation_id retry (must replay the same element), a same-operation_id request with another radius (`operation_id_conflict`) and a collinear arc (`arc_collinear`, refused by the CLI before the pipe). Messages are in Spanish and end with `Catálogo: X/Y pasos OK`. Evidence: `.bim/native-sessions/<utc>/catalogue-run<N>.json` (request, response, elapsed ms, pass/fail per step, counts) plus `summary.json`; envelopes are kept under the run's `catalogue-requests/`. Catalogue v1 has no save command, so the fixture stays unsaved in the child Revit, which is left open for the owner to inspect (closing it without saving discards the changes). Headless coverage: `tests/integration/test-catalogue-session.ps1` (fake executor in process, then the real CLI against a fake pipe); nothing there runs Revit.

State: blocked for full Test A/B. Existing native primitive probes are verified-in-revit; they do not implement hosted doors/windows, Room or complete documentation. Do not convert their success into full acceptance.

Prerequisites: Windows VM with an authorized installed Revit2025/API25.2, .NET8 SDK, a metric .rte, working directory containing this repository and no production project used as a fixture. License activation and unsigned add-in security decisions are manual-only. No APS/cloud credentials are required or assumed.

Build and prepare a fresh isolated run (commands from repo root; set `$BimTemplate` to an existing metric .rte):

```powershell
$BimDotnet = (Get-Command dotnet).Source
& $BimDotnet build runtimes/revit-native/Bim.Revit.csproj --configuration Release --warnaserror
& $BimDotnet build interfaces/cli/Bim.Cli.csproj --configuration Release --warnaserror
$BimRun = Join-Path $pwd ('.bim/native-runs/'+[guid]::NewGuid())
$BimAssembly = (Resolve-Path runtimes/revit-native/bin/Release/net8.0-windows/Bim.Revit.dll).Path
./tests/integration/launch-native-fixture.ps1 -RunDirectory $BimRun -TemplatePath $BimTemplate -AddinAssembly $BimAssembly -ConfirmIsolatedTestProcess
```

The preparation command never installs a manifest or proves native execution. On an isolated VM, explicitly install only the staged fixture manifest; refuse overwrite:

```powershell
$BimManifest = Join-Path $env:APPDATA 'Autodesk/Revit/Addins/2025/Bim.Agentic.Fixture.addin'
if(Test-Path -LiteralPath $BimManifest) { throw 'Existing manifest must be reviewed; do not overwrite' }
Copy-Item -LiteralPath (Join-Path $BimRun 'Bim.Agentic.Fixture.addin') -Destination $BimManifest
$BimStart = [Diagnostics.ProcessStartInfo]::new('C:/Program Files/Autodesk/Revit 2025/Revit.exe')
$BimStart.UseShellExecute = $false
$BimStart.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$BimStart.Environment['BIM_AGENTIC_TEST_CONFIG'] = (Join-Path $BimRun 'fixture-config.json')
$BimChild = [Diagnostics.Process]::Start($BimStart)
```

Resolve Revit loading/licensing manually. Wait for `fixture-result.json`; bounded wait failure is blocked, not success. Check status succeeded, returned processId equals `$BimChild.Id`, pipeName equals `bim-agentic-os-2025-<childId>`, exact saved path/direct run child, and ProjectInformation identity. Never use a global pipe or a production document.

Then run the existing primitive regression (requires a NEW baseline fixture, not a previous revision2 fixture):

```powershell
$BimFixture = Get-Content (Join-Path $BimRun 'fixture-result.json') -Raw | ConvertFrom-Json
./tests/integration/native-batch-smoke.ps1 -DocumentPath $BimFixture.documentPath -DocumentIdentity $BimFixture.documentUniqueId -PipeName $BimFixture.pipeName -Dotnet $BimDotnet -CliDll (Join-Path $pwd 'interfaces/cli/bin/Release/net8.0/Bim.Cli.dll')
```

Set `$BimPriorEvidence` to the absolute `evidence` directory returned by the smoke above; do not substitute another fixture's results. Then:

```powershell
$BimCli = Join-Path $pwd 'interfaces/cli/bin/Release/net8.0/Bim.Cli.dll'
./tests/integration/native-rollback-probe.ps1 -DocumentPath $BimFixture.documentPath -DocumentIdentity $BimFixture.documentUniqueId -PipeName $BimFixture.pipeName -Dotnet $BimDotnet -CliDll $BimCli -PriorEvidenceDirectory $BimPriorEvidence
./tests/integration/native-checkpoint-probe.ps1 -DocumentPath $BimFixture.documentPath -DocumentIdentity $BimFixture.documentUniqueId -ProcessId $BimChild.Id -PipeName $BimFixture.pipeName -RunDirectory $BimRun -PriorEvidenceDirectory $BimPriorEvidence
```

Checkpoint timeout means unknown completion; do not retry automatically. Preserve raw results/RVT locally, export only redacted evidence. These commands mutate only the explicitly owned fixture and remain separate from the full A/B evaluator's blocked result.

Full A/B evaluation must remain blocked until all mandatory observations and instrumented metrics are implemented and measured by the real backend. The evaluation oracle must reject missing hosted families, Room, documentation and unavailable measurements. IFC output is not native Revit view/sheet evidence. No backend result may be inferred from compilation.

Cleanup: preserve all models/evidence; do not kill or close arbitrary Revit processes. Remove the temporary manifest only after verifying it is byte-identical to this run's owned staged manifest. The user decides any destructive action outside the repository/VM test checkout.

