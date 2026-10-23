# ADR0007 — Cross-store receipt recovery (backend realized state vs Core ProjectStore)

Status: accepted — option A; the owner approved option A in chat on 2026-10-23 (America/Buenos_Aires). Other options stay recorded as rejected for now.

## Context

Two independent durable stores record the same correction, and no code commits them together ([HANDOFF](../../HANDOFF.md) §8, [STATE](../../agent/STATE.md) last paragraph).

- Core intended state: `ProjectStore.Commit` in [Model.cs](../../../core/Bim.Core/Model.cs), reached only through the CLI `commit-intent` ([Program.cs](../../../interfaces/cli/Program.cs)).
- IFC realized state: `resize_compiled` in [worker.py](../../../runtimes/ifc/worker.py), whose receipt lives in the IFC file's `BimExecution` pset (`set_state`).
- Native realized state: the ExtensibleStorage `Payload` written by `BatchExecutor.Run` ([BatchExecutor.cs](../../../runtimes/revit-native/BatchExecutor.cs)) and, for the fixture, `NativeRoomFixture.Persist` ([NativeRoomFixture.cs](../../../runtimes/revit-native/NativeRoomFixture.cs)). That payload holds a complete Core `Project` (revision+1), the logical/native map and receipts, so it is a third copy of intent.

Verified fact: no orchestrator calls a backend and then `ProjectStore.Commit`. `EvaluationService`/`IfcExecution.cs` start the worker and never open a ProjectStore, and `scripts/demo-room.ps1` commits intent with no backend. The intended order "backend first, then Core" is a convention the caller follows, not code. A "crash between the stores" therefore also covers a caller that never runs the second step.

Current write order and crash points (C = crash after this step):

1. IFC `resize_compiled`: `validate_request` → open IFC, read `state` → receipt/revision checks → in-memory `alter` → `set_state` (receipt, `compiled_revision`, `compiled_hash`=sha(CommittedAfterJson)) → exclusive-create `room.pending.ifc` (C1: empty staging file left, because `finally` does not run on a hard kill. The next call fails closed with `existing staging artifact`, and someone must clean it up by hand) → `f.write(temporary)`, `validate`, `verify_compiled`, `inspect` (C2: same as C1, with partial staging bytes) → `os.replace(temporary,path)` (C3: IFC is at r+1 with its receipt, Core is still at r. **This is the main disagreement.**) The temp file is never fsynced before the replace.
2. Core `Commit`, under the `lock` file: `ReadState`, idempotency by key, the CAS on Id/Revision/BeforeHash/AfterHash, `Validate`, and `Diff==plan` → `File.WriteAllText checkpoint-{r}.json` (C4: a non-atomic, possibly torn checkpoint that nothing reads) → `File.AppendAllText journal.jsonl {key,plan,state="prepared"}` (C5: a prepared line that is never followed by a committed marker, with no fsync. The code comment says "recovery can reconcile by hashes", but no code reads the journal) → `Write`: `project.tmp` + `Flush(true)` (C6: tmp left over. The next `FileMode.Create` overwrites it) → `File.Move(tmp,project.json,true)` publishes the revision and receipt together (C7: after this, both stores agree).
3. Native `BatchExecutor.Run`: TransactionGroup { ops Transaction, `Readback`, receipt Transaction } → `Assimilate`. The document is modified only in memory. The change becomes durable only when `Document.Save` runs (`FixtureCheckpoint.Run`, `NativeRoomFixture.Checkpoint`) (C8: crash before Save loses the native change. Whether Revit's own backup recovers it is unverified) (C9: after Save, before Core commit, the disagreement matches C3).

Constraints that may not be reopened ([HANDOFF](../../HANDOFF.md) §5, [CYCLE_SCOPE](../../agent/CYCLE_SCOPE.md) §4): exact committed snapshot bytes carry revision+1; latest retry succeeds; a historical retry after a later correction fails closed without writes; any change to the persisted BIM-IR or record schema needs a new ADR and explicit approval.

Useful invariant (true by construction, but not yet asserted across stores by any test): after C7, `Compiler.Hash(store.Read())` equals the IFC `compiled_hash`, and `Revision` equals `compiled_revision`, because both hash `JsonSerializer.Serialize(after with {Revision=r+1})`. On the native path, `Compiler.Hash(nativeState.Project)` should equal `Compiler.Hash(store.Read())`. Divergence can therefore be detected without new fields.

## Options

**A. The backend receipt is the source of truth. Core replays on startup or on the next command.**
Keep the order backend → Core. Add a read-only `reconcile` step that compares (Core revision, hash) with the backend's (`compiled_revision`, `compiled_hash`) or the native `Project`. It acts in only one case: Core is exactly one revision behind, a retained compiled request matches the backend receipt (`compiled_fingerprint`, `plan_after_hash`), and `Commit(after, plan, OperationId)` succeeds unchanged. Every other combination fails closed with a typed code (`core_ahead_of_backend`, `backend_gap_gt_1`, `staging_artifact_present`, `receipt_packet_mismatch`).
- Schema: Core `State`, BIM-IR and the IFC/Revit receipts stay unchanged. It needs the adapter packet (`IfcCompiledRequest`) kept durably beside the backend artifact. `execute` already writes `compiled-request.json`, but only at the end and not in `resize_compiled`. The owner should confirm that a retained adapter artifact is not a persisted-record change. HANDOFF §5 says adapter transport needs no migration.
- Retry: latest retry is the existing path (backend returns `False`, `Commit` returns the stored project), so it is preserved. Historical retry: the replay runs only when Core sits at the packet's `ExpectedRevision`, so after a later correction it fails closed (the existing `corrupt compiled retry state` / `revision_conflict` / `retry_superseded`).
- Windows/OneDrive: no new rename. It inherits C1–C7. Replay is safe to repeat, so a torn or failed publish just runs again.
- Work: IFC: keep the packet before `os.replace`, add `reconcile` in C# (CLI verb), and add a helper that removes a staging file only when it is not the published file and the hash checks pass. Revit: the same comparison against the ExtensibleStorage `Project`, allowed only after Save and the reopened `VerifyReceipt` (C8 is not recoverable by A). Desired = native Project with Revision−1, plan = `Diff(core, desired)`, which must equal the receipt hash. Estimate: small.

**B. A Core-side write-ahead intent journal with two phases and a recovery pass.**
Before calling the backend, Core appends `{key, plan, packetSha, state:"intent"}` with fsync. After the backend acknowledges, `Commit` writes `prepared`, then publishes, then writes `committed`. On startup, recovery scans for intents that have no `committed` record, asks the backend's receipt, and either commits or marks the intent `aborted`.
- Schema: changes the persisted `journal.jsonl` record (new states and fields) and gives it meaning for the first time. This counts as a persisted-record change and **needs approval**. `project.json` stays unchanged.
- Retry: latest retry becomes resumable even after C1/C2, because the intent identifies the staging owner. Historical retry: recovery must refuse intents whose `ExpectedRevision` is below Core's revision, which keeps the fail-closed rule. That rule has to be specified and tested explicitly.
- Windows/OneDrive: `AppendAllText` gives no atomicity or fsync guarantee, so the reader must tolerate a torn last line and needs `FileStream`+`Flush(true)` for appends. OneDrive can lock or conflict-copy `journal.jsonl` during sync. One more file adds one more chance of a sharing violation.
- Work: IFC: the worker reports the receipt, and C# recovery queries the IFC state. Revit: recovery needs a live Revit session to read ExtensibleStorage, so recovery is blocked while Revit is closed. Estimate: medium–large.

**C. An outbox in the Core store (Core first, then realization).**
`Commit` publishes revision r+1 and an outbox entry `{key, packet, realized:false}` in the same atomic `project.json`. A dispatcher sends pending entries to the backend in order, then marks them realized with another atomic publish.
- Schema: adds a field to `ProjectStore.State`, a persisted record change that **needs approval**. Migration covers existing `project.json` files with no outbox.
- Retry: latest retry is delivered again by the dispatcher, and the backend stays idempotent by OperationId. Historical retry: dispatch must be strictly ordered. An out-of-order or stale entry must fail closed at the backend's revision check, which the code already enforces. This reverses the meaning: Core intent may run ahead of realized state. That matches README "intent commits … do not synchronize Revit", but users would see "committed, not realized".
- Windows/OneDrive: one atomic file per step, so C6/C7 caveats apply twice per correction. Larger snapshots make torn writes during sync more costly.
- Work: IFC: dispatcher + worker invocation from C#. Revit: the dispatcher must queue until a matching document is open via Named Pipes, plus a realized-ack. Estimate: large.

## Recommendation

Option A: keep backend → Core order, retain the adapter packet, add a fail-closed `reconcile` that replays only the latest one-revision gap, and change no schema. If the owner later wants Core to be authoritative or wants automatic C1/C2 cleanup, B is the follow-up and needs its own approval. C is deferred.

Caveats common to all options (unverified on this machine): .NET `File.Move(...,true)` and Python `os.replace` both map to `MoveFileEx` with replace-existing on Windows, but Windows does not document this as atomic or durable. Neither path flushes the directory. OneDrive can briefly hold handles (sharing violation) or create conflict copies, and §7 notes placeholder ReparsePoints. Recommendation: refuse to run reconcile when a conflict-copy or placeholder sits next to `project.json`/`room.ifc`. Revit `Save` atomicity on OneDrive is unknown.

## Test plan (deterministic; no timing, no process kill required)

Use named injection points like the existing `force_failure`, so each test can stop after a given step: IFC `after_staging_create` (C1), `after_staging_write` (C2), `after_replace` (C3). Core `after_checkpoint` (C4), `after_journal` (C5), `after_tmp` (C6). Each test records SHA-256 of `room.ifc`, `project.json`, `journal.jsonl` before and after.

1. C3 → reconcile: Core revision == `compiled_revision`, `Hash(Core)` == `compiled_hash`, `Receipts[OperationId]` == `plan_after_hash`, IFC bytes unchanged. A second reconcile is a no-op (all hashes identical).
2. C1/C2 → reconcile fails closed `staging_artifact_present`, and both stores are byte-identical. After the verified cleanup, a latest retry of the pair succeeds.
3. C4/C5/C6 → `project.json` is unchanged at r, and a retry `Commit` with the same key succeeds. A torn or duplicate `prepared` line and a leftover `project.tmp` do not break it.
4. Historical: two corrections applied to both stores, then reconcile or replay of packet 1 → fails closed, with zero byte changes in either store.
5. Latest retry after recovery: rerun backend+Core with the same packet/key. The backend returns `False`, `Commit` returns the identical project, bytes unchanged.
6. Forged states: Core ahead of IFC, gap > 1, packet fingerprint ≠ receipt → each returns its typed code with no writes.
7. Native (Revit-gated, isolated fixture only): C9 via `BatchExecutor.Run` + checkpoint Save, then reconcile → Core matches `Hash(nativeState.Project)`. For C8 (close without Save), reopen and confirm that reconcile does not commit Core. Not executable in CI. No native claim follows from IFC results.

## Implementation notes

- IFC reconcile is implemented (`IfcReconcile.cs`, CLI `reconcile`, worker `--receipt`). The refusal when a OneDrive conflict copy or placeholder sits next to `project.json`/`room.ifc` is deferred. `retain_packet` runs before staging validation, so a later operation that fails can overwrite the retained packet. Reconcile then fails closed (`receipt_packet_mismatch`) and recovery is manual.

## Unverified

- Windows rename and fsync semantics, and OneDrive behaviour, for these exact calls. Revit's own crash recovery for C8.
- Byte-identity of `JsonSerializer.Serialize(Deserialize(AfterJson))` with `AfterJson`, which A's replay depends on. This needs a test.
- Whether `checkpoint-{r}.json` or `journal.jsonl` have consumers outside this repo. None were found in it.
- Effort estimates are judgement, not measurement.

**Owner decision needed:** approve A (no schema change, adapter packet retained), or approve the persisted-record change that B (journal format) or C (`ProjectStore.State` outbox) requires.
