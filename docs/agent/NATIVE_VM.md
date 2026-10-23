# Native evaluation on a Windows VM

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

