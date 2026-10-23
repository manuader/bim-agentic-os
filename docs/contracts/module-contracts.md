# Module contracts — v0.1

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

