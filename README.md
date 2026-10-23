# BIM Agentic OS

Implementation governed by [master specification](BIM_Agentic_OS_PROMPT_DEFINITIVO.md) and the narrower [current cycle](docs/agent/CYCLE_SCOPE.md). Start with [persistent state](docs/agent/STATE.md) and [environment evidence](docs/agent/ENVIRONMENT.md). Initial kernel, not a completed BIM authoring product.

## Executable modules

- C# .NET 8 semantic IR, explicit metre boundary, deterministic diff and transitive documentation invalidation.
- File-backed intended-state snapshots, optimistic revisions, checkpoints, prepared journal and retry receipts published atomically with revisions. This is not yet the realized native model mirror. Power-loss and journal recovery testing remain pending.
- CLI shares the core (`diff`, `validate`, `init`, `plan`, `commit-intent`) and sends explicit native requests (`native`). Intent commits persist semantic state only; they do not synchronize Revit. No natural-language parser yet. A stdio MCP server (`interfaces/mcp`) exposes diff/validate/room/plan with output byte-equal to the CLI; it is written and reviewed but not yet compiled (see [board](tasks.md)).
- Typed `RoomIntent` compiles a clear 5 x 4 m room and a +400 mm width revision, preserving identities and fixed exterior anchors. `room` emits semantic state with space/documentation dependencies; native execution explicitly blocks those unsupported output kinds. `scripts/demo-room.ps1` exercises CLI intent/diff/persistence/idempotency.
- Revit 2025 add-in source: same-user Windows named pipe, ExternalEvent serialized execution, document path guard, level creation, wall creation/axis modification, inspection, deletion and transaction rollback. Native requests are NOT yet idempotent; never blindly retry mutations. Wall creation currently uses the document default wall type; 200 mm construction is not guaranteed.
- Separate typed `batch` operation supports level/wall primitives with exact matching wall thickness, persistent ExtensibleStorage identity mapping/receipts and transaction-group readback. Create/retry/resize/forced rollback and saved-checkpoint reopen were verified in Revit25.2 for an isolated fixture. Primitive requests above remain non-idempotent. Native deletes and unsupported complete-room documents are blocked in the batch.
- Optional IFC4 fixture runtime: real walls, hosted door/windows and Space, save/reopen, incremental+400mm, persisted receipts/retry/conflict and staged-failure rollback, checked with EXPRESS and triangulated geometry. The bounded C# process bridge shares the evaluation oracle. See [IFC setup and limits](docs/ifc.md); this fixed fixture does not execute arbitrary BIM-IR.

## Reproduce

Install official .NET 8 SDK into `.tools/dotnet` or use system `dotnet`.

```powershell
./scripts/test.ps1
./scripts/verify.ps1
./scripts/audit-dependencies.ps1 -Dotnet ./.tools/dotnet/dotnet.exe
./scripts/demo-room.ps1
# SDK-free fallback with host references (does not prove net8 compatibility):
./scripts/test-roslyn.ps1
./scripts/check-native-source.ps1
./scripts/benchmark-roslyn.ps1
./.tools/dotnet/dotnet build runtimes/revit-native/Bim.Revit.csproj
./.tools/dotnet/dotnet run --project interfaces/cli -- diff examples/before.json examples/after.json
```

Revit API assemblies are referenced from the installed Autodesk product, not distributed. Replace Assembly in `runtimes/revit-native/Bim.addin.template` with the absolute built DLL path and install as `.addin` in the user's Revit 2025 Addins folder. Revit restart is required. Use a dedicated saved test document. The existing Casa Ángel project is not a test fixture.

Native request JSON example (absolute path must match active saved document):

```json
{"DocumentPath":"C:\\BimTests\\fixture.rvt","Operation":"level.create","UniqueId":null,"Metres":[0]}
```

## Acceptance status

Core and CLI are `unit-tested` (50 core assertions,58 evaluator assertions); installed RevitAPI25.2.0.38 and SDK8.0.415 supported the recorded builds. Level/wall batches and saved-checkpoint reopen are `verified-in-revit`; the saved fixture contains only that primitive scope. Full native Test A/B is `blocked`: Revit hosted door/windows, Room and complete documentation are missing. The fixed IFC fixture is `integration-tested (ifc)` on Windows and Ubuntu CI with four real tests and eight runtime checks. See [evidence](docs/evidence.md), [capabilities](docs/features.json) and [native procedure](docs/agent/NATIVE_VM.md).

The evaluation oracle is separate from backend acceptance. `pwsh -File scripts/evaluate.ps1 -Backend revit` (or `ifc`) emits JSON with mandatory criterion outcomes and metrics. With the optional environment installed, IFC actually executes:41 criteria and eight backend checks pass; nine native-only criteria remain unsupported. It returns exit2 with backend acceptance true and product acceptance false. Revit's full fixture remains blocked. Offline synthetic observations cannot issue product acceptance. Unknown metrics remain null. See [evaluation contract](docs/contracts/evaluation.md).
