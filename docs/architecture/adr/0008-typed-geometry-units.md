# ADR0008 — Typed geometry, units and capability contracts
Status: proposed; awaiting owner (architect) decision. No schema or code change is authorized by this document.

## Context

[CYCLE_SCOPE](../../agent/CYCLE_SCOPE.md) §1.3 asks for typed `Project`, `Element`, `Geometry`, `Units`, `Capability`, `Operation`, `ExecutionPlan`, `ValidationIssue`, `SemanticDiff`, `CommandResult` per §6 and §9 of the [master prompt](../../../BIM_Agentic_OS_PROMPT_DEFINITIVO.md) ("never mix meters, feet, millimeters ... implicitly"; "convert to Revit internal units at a well-tested boundary"). [BLOCKERS](../../agent/BLOCKERS.md) classifies current geometry arrays as prototype contracts and requires this ADR before any expansion. [HANDOFF](../../HANDOFF.md) §5: exact committed snapshot bytes (including revision+1) are what later `BeforeHash` values match; Python does not recanonicalize them.

Current shape ([Model.cs](../../../core/Bim.Core/Model.cs), [RoomSlice.cs](../../../core/Bim.Core/RoomSlice.cs)):

- `Element(Guid Id, string Kind, double[] GeometryMetres, Guid[] Dependencies, Provenance Provenance)`; `Project(Guid Id, int Revision, Element[] Elements)`; `Operation(string Action, Element Element)`; `ExecutionPlan(ProjectId, ExpectedRevision, BeforeHash, AfterHash, Operations, DirtyIds)`; `ValidationIssue(Code, Id?, Message)`; `CommandResult(Status, Verification, Plan, TotalMs)`. No `Geometry`, `Units`, `Capability` or `SemanticDiff` type exists.
- `Length` (readonly record struct, `Metres`, derived `Feet = Metres/0.3048`) exists but no record uses it; its only consumer is the `"metres to feet"` check in [Bim.Tests](../../../tests/Bim.Tests/Program.cs).
- `Compiler.Hash` = SHA-256 of default `JsonSerializer.Serialize(Project)`. `Compiler.Diff` detects updates by comparing serialized element strings. `ProjectStore.Commit` requires byte-equal `JsonSerializer.Serialize(Diff(...))` vs the submitted plan (`plan_mismatch`) and stores `Receipts[key] = AfterHash`; a retried key must reproduce that hash (`idempotency_conflict`).
- Per-kind array layout is implicit: wall `[ax,ay,bx,by,height,thickness]` (axis coordinates), level `[elevation]`, space `[x,y,width,depth]` (interior corner + clear size), plan/schedule/sheet `[]`. `RoomIntent` carries `*Metres` fields plus `string Units` that must equal `"m"`.

## Problem cases (observed in code)

1. **Three unit systems, no type boundary.** Core is metres; the IFC worker multiplies by 1000 and authors `IfcSIUnit LENGTHUNIT MILLI METRE` ([compiled_request.py](../../../runtimes/ifc/compiled_request.py), [worker.py](../../../runtimes/ifc/worker.py)); evaluation observations are millimetres by convention ("All lengths are millimetres" in [EvaluationService.cs](../../../evaluation/Bim.Evaluation/EvaluationService.cs), `units` criterion checks `Units=="mm"`); Revit converts through three duplicated local helpers `U(m)=UnitUtils.ConvertToInternalUnits(m,UnitTypeId.Meters)` in [App.cs](../../../runtimes/revit-native/App.cs), [BatchExecutor.cs](../../../runtimes/revit-native/BatchExecutor.cs), [NativeRoomFixture.cs](../../../runtimes/revit-native/NativeRoomFixture.cs). Units are strings (`"m"`, `"mm"`) checked ad hoc, so a mm value passed into `GeometryMetres` type-checks.
2. **Untyped arrays, inconsistent arity.** `Compiler.Validate` checks arity only for walls; level arity is checked only in `NativeBatchContract.Preflight`, space arity only in `IfcCompiler.Rectangle`. `App.cs` `wall.create` takes 5 values while the IR wall has 6. Wall coordinates are axis points but space coordinates are interior faces; nothing in the type distinguishes position, length or area.
3. **No capability declaration.** `Preflight` hard-codes `Kind is not ("level" or "wall")`; [capabilities.json](../../../capabilities/capabilities.json) lists `revit.wall.create` etc. with `coverage_status`/`native_verified`, but no C# type reads it, and `CommandResult` lacks §9 `capability_id`.
4. **Exactness hazards.** Diff and hashes are bit-exact on doubles; evaluator compares mm with `==` after `NormalizeMillimetres` (1e-6 mm snap). Any typed layer that rounds or rescales on write changes hashes and creates spurious updates.
5. **Serialization hazard (reasoned, not executed).** Embedding `Length` as-is in a record would likely serialize both `Metres` and the derived `Feet` with default System.Text.Json options, changing bytes and leaking feet into persisted state.

## Options

**A — Typed value objects at API boundaries; persisted JSON unchanged.** Add `Length`/`Area`/`Angle` constructors (`FromMillimetres`, `FromMetres`), a `Point2(Length X, Length Y)`, and per-kind decoded views (`WallGeometry`, `LevelGeometry`, `SpaceGeometry`) with a strict `GeometryCodec.Decode(Element)`/`Encode(...)` over the existing `double[]`. `Capability` record loaded from capabilities.json; `SemanticDiff` computed from `ExecutionPlan` + codecs and returned beside it, never inside plan JSON.
- Persisted records / snapshot hashes: byte-identical; `Element`, `Project`, `ExecutionPlan` signatures untouched.
- Idempotency/retry: unchanged; existing receipts and historical committed snapshots stay valid.
- Tests: all existing [Bim.Tests](../../../tests/Bim.Tests/Program.cs) checks (e.g. `"room deterministic"`, `"retry persisted idempotency"`, `"idempotency payload conflict rejected"`, `"west exterior anchor fixed"`), [Ifc.Compiler.Tests](../../../tests/ifc-compiler/Program.cs), Python `test_actual_core_packets`, `test_consecutive_compiled_revisions`, `test_actual_roundtrip_revision_receipts_and_geometry` must pass unedited.
- IFC: wire packet unchanged (`ProtocolVersion 1`, `Units "m"`); `IfcCompiler.Rectangle` moves to the codec. Python keeps its own ×1000.
- Revit: the three `U` helpers collapse to one boundary taking `Length`; same `UnitUtils` call, so native behaviour should not change (unverified until a native A/B rerun). `App.cs` 5-value wall request is decoded explicitly and its arity difference documented.
- Limitation: persisted state still cannot express angles, source units, tolerances or georeferencing (§6).

**B — Versioned schema v2 records with explicit migration and hash-compat rule.** `ProjectV2` with `SchemaVersion: 2` and typed geometry objects per element (lengths stored as metres with an explicit unit tag).
- Persisted records: new shape; v1 `project.json`, `checkpoint-*.json`, `journal.jsonl` remain v1. Rule: hash is defined per schema version; v1 bytes are never rewritten; migration is its own revision (checkpoint v1, publish v2 at revision+1, receipt `migration:v1->v2`).
- Snapshot hashes: every v2 hash differs; `BeforeHash` of the first v2 plan is the v1 hash of the migrated snapshot.
- Idempotency/retry: latest-key retry across the migration must be checked against v1 bytes; historical v1 retries after migration fail closed (consistent with HANDOFF §5).
- Tests: Bim.Tests golden hashes, IFC-compiler packets and the `tests/ifc` packet tests (e.g. `test_actual_core_packets`) change; `compiled_request.py` rejects any element whose key set is not exactly `{Id,Kind,GeometryMetres,Dependencies,Provenance}`, so IFC needs `ProtocolVersion 2`.
- Revit: `BatchExecutor.State`/`NativeRoomFixture` payload in ExtensibleStorage must migrate in-document; behaviour of existing saved documents with a v2 payload is unverified.

**C — Side-car typed geometry keyed by element UUID.** Keep `Project` v1; add `TypedGeometry: Dictionary<Guid, ...>` in `ProjectStore.State` (and native `State`) with its own hash.
- Persisted records: `project.json` gains a sibling field; Project hash unchanged, but a second hash must be bound to receipts or the two can diverge.
- Idempotency/retry: receipts must store both hashes; old receipts lack the side-car hash (needs a compat branch).
- Tests: core tests pass; new cross-consistency tests needed; HANDOFF §8 already records cross-store coordination as a gap, which C widens.
- IFC/Revit: IFC ignores the side-car (unchanged wire); Revit payload gains a field (STJ default tolerates missing members on read; unverified on saved documents). Two sources of truth for the same geometry.

## Recommendation

Option A now: it delivers typed units, typed geometry views, `Capability` and `SemanticDiff` with zero persisted-byte, hash, retry or wire change, and it is fully testable without Revit. Pre-agree B's per-version hash rule as the only path to persisted typed geometry when angles/georeferencing are required (§6), via a later ADR. Reject C (dual truth, wider cross-store gap).

## Migration and test plan (A)

1. Freeze goldens first (red before any code): from the current baseline capture `JsonSerializer.Serialize(IfcCompiler.EvaluationRequest())`, `Compiler.Hash` of the evaluation room before/after, `PlanSha256`, `CommittedAfterJson`, and one `project.json` written by `ProjectStore`. Tests assert byte-identical output after every subsequent step.
2. Red cases: `Length.FromMillimetres(400).Metres==0.4`; `FromMillimetres(double.NaN)` throws; `GeometryCodec.Decode` rejects wall arity 5/7, level arity ≠1, space arity ≠4 with stable codes; `Encode(Decode(e))` returns arrays with equal `BitConverter.DoubleToInt64Bits` per value for every element of `RoomCompiler.Compile` outputs; mm→m→mm round-trip of 5400 returns exactly 5400 after `NormalizeMillimetres`.
3. Type-boundary red case: a test that `Length` is not a property type of any persisted record (reflection over `Project`, `Element`, `ExecutionPlan`, `ProjectStore.State`), guarding problem 5.
4. Capability: `Preflight` driven by the registry must yield the same issue codes in `"documentation and space block native room batch"` and `"native preflight validates new state of retained identities"`.
5. SemanticDiff: for the 5.0→5.4 m resize, reports `+0.400 m` width on space and the three moved walls, and nothing for west/level; plan JSON unchanged.
6. Unedited suites: Bim.Tests, Ifc.Compiler.Tests, NativeRoomGuard/FixtureGuard/CheckpointGuard tests, `tests/ifc` Python, `scripts/verify.ps1`. Revit add-in compile only in CI; native claims require a real Test A/B rerun.

## Unverified

- Default System.Text.Json output for `Length` (problem 5) is inferred from its public getters, not executed.
- ExtensibleStorage behaviour of existing saved Revit documents under B or C payload changes was not executed.
- Revit internal length unit as feet is taken from the existing `Length.Feet` and `UnitUtils` usage; not queried in a live session here. No new Revit API is proposed.
- No performance measurement was made for any option.

**Owner decision needed:** approve A (boundary-only typed contracts, no schema change), choose B or C instead, or defer; and confirm whether B's per-version hash rule is pre-approved for a future ADR.
