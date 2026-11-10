namespace Bim.Core;

// P2-12 commit adopted drift into Core (no provenance). From a P2-09 selection (MirrorDriftSelect.Select) and the
// ProjectStore at the selection's Core revision, Commit applies the selection's adopt operations (create/update/delete,
// in selection order) to the store head and commits them as one Core revision through ProjectStore.Commit, with the
// plan Compiler.Diff(head, desired) and a deterministic idempotency key (Key):
//   "mirror-drift-adopt:<core project id D>:<core revision>:<lower-hex SHA-256 of the canonical selection JSON>"
// (MirrorDriftSelection.ToJson, itself canonical whatever the order of the choices).
// Outcomes (MirrorDriftAdoptOutcomes):
//   committed         one new revision (head revision + 1); the store gains checkpoint-<rev>.json, one "prepared"
//                     journal line and one receipt under the key, exactly as ProjectStore.Commit writes them;
//   replayed          the store already holds a receipt for the key: the current head is returned and nothing is
//                     written (no second revision), even though the head has moved past the selection's revision;
//   nothing_to_adopt  the selection has no adopt operations (restore-only or empty): the store is not opened at all.
// Refusals (MirrorDriftAdoptException, Code = the first word of the message, MirrorDriftAdoptCodes):
//   drift_adopt_selection_invalid  a malformed selection (null parts, an unknown action, two operations on one
//                                  element, a create of an existing element, an update or delete of a missing one);
//   drift_adopt_store_invalid      the store's project.json is missing, not JSON or has null project/elements/receipts;
//   drift_adopt_project_mismatch   the head is another Core project than the selection's;
//   drift_adopt_stale_revision     the head is not the selection's revision, or its canonical hash (elements ordered by
//                                  id) is not the selection's restore_plan after_hash; also Commit's revision_conflict
//                                  and idempotency_conflict (the store moved between the read and the commit);
//   drift_adopt_invalid_ir         the adopted model fails Compiler.Validate;
//   drift_adopt_store_unavailable  project.json cannot be read, or Commit's store_locked (the lock is held);
//   drift_adopt_commit_incomplete  an I/O or access failure inside Commit after its lock was taken: Commit may have
//                                  written the checkpoint, the "prepared" journal line and project.tmp while
//                                  project.json still holds the old head (the ADR0007 crash case). NOT nothing-written.
// Every other refusal is raised before ProjectStore.Commit, or by Commit before its first write: nothing is written.
// Precedence: selection, then nothing_to_adopt, then the store read, then the receipt (replay), then project, revision
// and hash, then the operations, then validation, then the commit.
// Not done here: no P2-06 provenance ledger entry is written (P2-11, blocked on ADR0010); the selection's map changes
// (map_adds/map_removals) belong to the add-in's native state and are not written; the restore plan is not run (it is
// bound to the pre-commit revision and runs first, order restore_then_adopt). The operations' elements are committed
// as the selection gives them. Vendor-free; no BIM-IR schema or persisted-format change.

public static class MirrorDriftAdoptCodes
{
    public const string SelectionInvalid = "drift_adopt_selection_invalid";
    public const string StoreInvalid = "drift_adopt_store_invalid";
    public const string ProjectMismatch = "drift_adopt_project_mismatch";
    public const string StaleRevision = "drift_adopt_stale_revision";
    public const string InvalidIr = "drift_adopt_invalid_ir";
    public const string StoreUnavailable = "drift_adopt_store_unavailable";
    public const string CommitIncomplete = "drift_adopt_commit_incomplete";
}

public static class MirrorDriftAdoptOutcomes
{
    public const string Committed = "committed";
    public const string Replayed = "replayed";
    public const string NothingToAdopt = "nothing_to_adopt";
}

public sealed class MirrorDriftAdoptException(string code, string detail, Exception? inner = null)
    : InvalidOperationException(code + (detail.Length > 0 ? " " + detail : ""), inner)
{
    public string Code { get; } = code;
}

// Revision: the store head after the call (for nothing_to_adopt, the selection's revision). Key: null for
// nothing_to_adopt. Head: the store head (null for nothing_to_adopt). Plan: the committed plan (committed only).
public sealed record MirrorDriftAdoptResult(string Outcome, int Revision, string? Key, Project? Head, ExecutionPlan? Plan);

public static class MirrorDriftAdoptCommit
{
    public const string KeyPrefix = "mirror-drift-adopt:";

    // P2-12 red stub: the tests land first.
    public static string Key(MirrorDriftSelection selection) => throw new NotImplementedException("P2-12 not implemented");

    public static (Project Desired, ExecutionPlan Plan) Plan(Project head, MirrorDriftSelection selection) => throw new NotImplementedException("P2-12 not implemented");

    public static MirrorDriftAdoptResult Commit(string storeDirectory, MirrorDriftSelection selection) => throw new NotImplementedException("P2-12 not implemented");
}
