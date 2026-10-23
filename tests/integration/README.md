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

