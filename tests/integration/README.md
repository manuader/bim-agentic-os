# Native acceptance harness protocol

`native-batch-smoke.ps1` is an executable guarded integration probe for the level/wall subset. Requires a built CLI/add-in, loaded add-in in licensed Revit2025, a saved open `BIM-Agentic-Test*.rvt`, exact ProjectInformation.UniqueId and an existing Basic WallType of 200mm width. It tests native creation, persistent retry, +400mm correction, unchanged mappings and tampered-plan rejection. It does not test save/reopen, forced rollback or full documentation. Missing prerequisites exit2, not a passing skip.

```powershell
./tests/integration/native-batch-smoke.ps1 -DocumentPath C:/BimTests/BIM-Agentic-Test.rvt -DocumentIdentity <UniqueId> -PipeName bim-agentic-os-2025-<FixturePID> -Dotnet ./.tools/dotnet/dotnet.exe -CliDll ./interfaces/cli/bin/Release/net8.0/Bim.Cli.dll
```

1. Build add-in against local Revit 2025 DLLs; install manifest, restart authorized Revit. Create and save an isolated fixture RVT; never use the active production project.
2. Connect CLI to the explicit same-user process pipe reported by fixture-result.json; never infer a global endpoint. Create level, inspect returned UniqueId. Create walls; verify Wall class, category, level, axis and converted height independently in Revit.
3. Modify one wall axis; compare UniqueIds of all unchanged objects. Force invalid/zero axis; assert typed failure and unchanged native state after rollback. Reject wrong document path.
4. Complete missing Test A primitives before claiming milestone: 5 x 4 m clear room, 200 mm walls, 3 m height, 900 mm hosted door, two hosted windows, native Room, dimensions, plan, 3D view, schedule and sheet. Explicit family/type preflight is required.
5. Save, close, reopen RVT and inspect categories, hosting, dimensions, room area and schedule. Persist readbacks, screenshots and exact version/timing.
6. Implement semantic-to-native binding and durable native idempotency before Test B retries. Increase width 400 mm with exterior anchor fixed, validate affected documentation and forced-failure rollback. Record full-build versus incremental measured metrics.

These instructions are an integration test plan, not an automated integration test or passing result.

Fixture bootstrap has executed in real Revit25.2; see artifacts/native-bootstrap-result.json. The first smoke was rejected before mutation by the document guard because the legacy global pipe belonged to another process. Process-specific routing replaces that endpoint. Full Test A/B and the protocol steps above remain unverified unless a corresponding evidence artifact explicitly says otherwise.


Executed primitive smoke: artifacts/native-smoke-result.json records real level/wall create, idempotent retry, three-wall width correction, unchanged native mappings, geometry readback and tampered-plan rejection. This does not establish complete Test A/B, documentation or saved/reopened final geometry.


After the primitive smoke, native-rollback-probe.ps1 accepts the same exact DocumentPath/DocumentIdentity/PipeName, absolute Dotnet/CliDll and PriorEvidenceDirectory. It derives a valid semantic level change followed by a wall below native short-curve tolerance, requires that native failure, then replays the successful resize receipt and compares geometry/revision/mappings. Actual local result: artifacts/native-rollback-result.json. Final save/reopen remains pending.


The saved checkpoint probe is `native-checkpoint-probe.ps1`. After smoke in a newly opted-in child, pass exact `DocumentPath`, `DocumentIdentity`, `ProcessId`, `PipeName`, `RunDirectory` and `PriorEvidenceDirectory` from that run. It saves/closes/reopens the fixture, checks identity/mapping/revision/receipt, then replays the original resize. It records one checkpoint roundtrip including parking/save/reopen/readback; this excludes initial planning/patch and all documentation. A dispatched timeout means unknown completion, not rollback; do not automatically retry. The command must not be run against a normal or production Revit session.

Executed save/reopen result: artifacts/native-checkpoint-result.json records real Revit25.2 saved/closed/reopened fixture, preserved revision2/geometry/mappings/receipt and successful post-reopen retry. Full Test A room/documentation is still incomplete.

## Full hosted Room/documentation executable probe

Build the add-in and CLI, prepare a NEW run with `launch-native-fixture.ps1 -ConfirmIsolatedTestProcess`, explicitly install that run's staged manifest, then launch a NEW child Revit process with `BIM_AGENTIC_TEST_CONFIG` set to that run's fixture-config.json. Preserve any foreign manifest and never use the production session. The Revit security prompt requires human Load Once when it appears.

Use the returned fixture-result.json for every argument:

```powershell
./tests/integration/native-room-ab.ps1 -RunDirectory <absolute-new-run> -DocumentPath <result.documentPath> -DocumentIdentity <result.documentUniqueId> -ProcessId <result.processId> -PipeName <result.pipeName> -Dotnet <absolute-dotnet.exe> -CliDll <absolute-Bim.Cli.dll> -TimeoutSeconds 300
```

The wrapper creates options exclusively and never retries a dispatched request. Room A, real hosted door/two windows, dimension, plan/3D, room schedule and sheet are created, saved/closed/reopened and read back. A forced real mutation is rolled back; a400mm correction is then saved/closed/reopened and its persisted retry checked. The independent strict C# A/B criteria are unchanged. Target compilation and synthetic guards do not establish native acceptance; consult the current evidence artifact for actual execution results. Revit2025.2 is the only locally targeted version; Spanish installed family paths are explicit prerequisites.
