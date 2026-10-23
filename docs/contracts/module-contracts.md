# Module contracts — v0.1

The evaluation-only DTO and evidence boundary is specified in [evaluation.md](evaluation.md). It leaves the existing BIM-IR and RPC schemas unchanged.

## Deterministic core

`Project(Guid Id, int Revision, Element[] Elements)` is intended semantic state. `Element` carries stable logical UUID, kind, geometry in metres, prerequisite IDs and provenance. Current wall geometry is `[x1,y1,x2,y2,height,thickness]`; this compact representation is version-0 prototype data, not a full geometry schema.

`Compiler.Validate(Project) -> ValidationIssue[]` rejects invalid geometry/identity/dependencies. `Compiler.Diff(before, after) -> ExecutionPlan` requires matching project and revision, emits create/update/delete operations and transitive dirty IDs. `BeforeHash`/`AfterHash` bind the serialized intended snapshots; this version's hashes depend on array order and are not a canonical semantic hash.

`ProjectStore.Commit(desired, plan, key) -> Project` compares optimistic revision/hash and publishes revision+receipt atomically. It commits intended state only; it must not claim native execution. Reusing a key for another payload is a conflict. Snapshot schema migrations, power-loss testing and journal replay remain gaps.

## Runtime boundary

`RoomCompiler.Compile(projectId, revision, RoomIntent) -> Project` uses clear interior dimensions and fixed west/south exterior anchors. Roles derive stable UUIDs from room ID. `NativeBatchContract.Preflight(before, after, plan)` validates both snapshots and requires the exact recomputed diff. `NativeBatchRequest(DocumentIdentity, IdempotencyKey, Before, After, Plan)` is carried by `Request` with `Operation="batch"`, exact saved `DocumentPath`, empty `Metres` and optional `Batch` payload.

The batch currently supports levels/walls only and requires an isolated `BIM-Agentic-Test*` non-workshared document plus exact `ProjectInformation.UniqueId`. It persists mapping, intended revision and receipts inside native ExtensibleStorage transactions. It rejects native deletes (untracked cascades), spaces and document kinds. This is a guarded development slice, not production-project authoring or a validated Test A room.

No Autodesk object crosses into core or a background worker. Requests target an exact saved document path and execute in `ExternalEvent` context, serialized. Every mutation needs explicit units, transaction status and readback. Unsupported commands fail visibly; unsupported documentation blocks complete-delivery status. A batch must preflight the entire operation set before mutation and roll back on native failure. Durability must live with native identity, not only a transient RPC response.

## Validation and delivery

Independent QA consumes native readback, intended invariants and deliverable contract. Generator success, host compilation and accepted API calls are separate evidence levels. A delivery is not validated until all required native elements and affected documents are checked. Current Test A/B gates remain open until saved/reopened RVT evidence exists.

## Agent work contracts

Each task owns named files and public signatures; implementation and review use clean contexts. Workers commit only their worktree changes. Controller integrates reviewed commits and reruns the full available gate. Review findings, unresolved blockers and exact evidence are persisted in the plan ledger and feature tracker.

Native transport routing: each Revit process listens on bim-agentic-os-2025-PID. The fixture result binds pipeName to processId; callers must supply that explicit endpoint. The CLI rejects missing/global endpoints before I/O. Routing does not replace exact saved document path and ProjectInformation.UniqueId checks.


## Experimental fixture checkpoint

`Request(Operation="fixture.checkpoint", Batch=existingResizeBatch, Checkpoint=FixtureCheckpointRequest(ParkingPath, ExpectedRevision, ExpectedMapping))` verifies an existing receipt without applying a batch. Authorization requires the child-process opt-in config, matching bootstrap result PID/pipe/path/document identity, exactly one owned fixture document and a new direct-child parking path. `BatchReceiptGuard` validates the batch/hash/revision/exact mapping; `BatchExecutor.VerifyReceipt` reads ExtensibleStorage and native geometry without mutation.

The checkpoint saves the fixture, switches to newly owned parking, closes/reopens the fixture, closes parking and reloads native storage for comparison. A failure reports its stage and saved/closed/reopened flags; it does not promise rollback of file operations. A dispatched timeout has unknown completion and must not be automatically retried. Documentation remains unsupported. Target build and pure guard tests do not establish native save/reopen success; see the native evidence record for execution status.

Compiler.Validate now emits dependency_cycle for cyclic logical dependencies. Iterative prerequisite removal runs in O(V+E) without recursion; missing references and duplicate identities remain independently reported. Diff rejects a cyclic input snapshot and intended-state publication rejects cyclic commits. This is graph validation, not a complete dimensional or native BIM QA contract.

## Compiler-to-IFC adapter (narrow rectangle slice)

`adapters/ifc/Bim.Ifc.Compiler` owns an adapter request, separate from saved Core records. It carries exact default C# serialized before/after/plan strings, raw UTF-8 SHA256 bindings, room/operation identity, six physical logical-role UUIDs and the three unsupported documentation UUIDs. Export recomputes the exact Core diff and reconstructs the canonical RoomCompiler output; malformed, stale, cyclic, unsupported and tampered inputs are rejected. Python independently validates the packet and dependency closure before output mutation. Python JSON reserialization is not a Core hash oracle.

Supported physical scope is one canonical rectangle, level/four walls/space, width-only corrections with fixed anchors/depth/height/provenance and200mm walls; minimum dimensions accommodate adapter-owned door/window fixture extras. Plan/schedule/sheet nodes remain in intended state and dirty closure but have no authored native documentation. Extras are explicitly not Core-authored elements. The deterministic join policy extends horizontal walls over corners and leaves vertical walls between inner faces, converting metre centreline geometry into millimetre IFC bodies.

The committed snapshot increments revision after applying a same-revision plan. Its hash must bind that committed revision, while the receipt separately binds the original plan payload. The next correction must validate against the committed snapshot; a first resize alone cannot establish this invariant. Retry requires a matching payload and consistent reopened persistent state/identity/geometry. Full A/B execution requires an actual correction; a low-level no-op must preserve file bytes and cannot fabricate a rollback observation. Staged failures must preserve the original persisted model; crash/power-loss durability remains outside this proof.

Core UUIDs are persisted in `BimCompiledIdentity`; IFC GlobalIds and STEP identities remain stable through corrections. Before/after physical readback is checked against intended boxes and independent world triangulation/schema validation. CLI `ifc-export before.json after.json plan.json room-guid operation-id` emits the adapter envelope. The evaluator uses the same export service for its default5→5.4m case; custom widths are tested separately without weakening its fixed master A/B oracle.
