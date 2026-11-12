using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Bim.MirrorJournal.Tests")]
namespace Bim.Core;

// P2-12 commit adopted drift into Core (no provenance). MirrorDriftAdoptCommit: from a P2-09 selection (MirrorDriftSelect.Select) and the
// ProjectStore at the selection's Core revision, Commit applies the selection's adopt operations (create/update/delete,
// in selection order) to the store head and commits them as one Core revision through ProjectStore.Commit, with the
// plan Compiler.Diff(head, desired) and a deterministic idempotency key (Key):
//   "mirror-drift-adopt:<core project id D>:<core revision>:<lower-hex SHA-256 of the canonical selection JSON>"
// (MirrorDriftSelection.ToJson, itself canonical whatever the order of the choices).
// Outcomes (MirrorDriftAdoptOutcomes):
//   committed         one new revision (head revision + 1); the store gains checkpoint-<rev>.json, one "prepared"
//                     journal line and one receipt under the key, exactly as ProjectStore.Commit writes them;
//   replayed          the store already holds a receipt for the key: the current head is returned and nothing is
//                     written (no second revision), even though the head has moved past the selection's revision.
//                     Also when a concurrent identical call lands between this call's receipt check and its
//                     ProjectStore.Commit: Commit then returns the head it holds for the key without writing. Commit
//                     returns `desired with { Revision = n + 1 }` only when it writes, so a returned head that does not
//                     share the desired element array (or is not revision n + 1) is that receipt path (tests pin it).
//                     A replay must pass the original persisted selection: the key hashes its canonical JSON, and a
//                     selection derived again from a later observation (other restore operation ids, other omitted
//                     proposals) is another key and is refused as stale once the head has moved;
//   nothing_to_adopt  the selection has no adopt operations (restore-only or empty): the store is not opened at all.
// Preview (P2-17): the read-only half of Commit, in Commit's order and with Commit's refusals (codes and texts): the
// selection checks, nothing_to_adopt (store never opened), the same store read (ReadState: FileShare.ReadWrite|Delete,
// strict UTF-8), the receipt (would_replay: the current head), then Plan (would_commit: Revision = head + 1, Plan the
// plan Commit would commit). It takes no store lock (a held lock does not block it; Commit is then store_unavailable),
// creates no lock file and writes or touches nothing. A snapshot: the store may move before a Commit, which re-checks all.
// Refusals (MirrorDriftAdoptException, Code = the first word of the message, MirrorDriftAdoptCodes):
//   drift_adopt_selection_invalid  a malformed selection (null parts, an unknown action, two operations on one
//                                  element, a create of an existing element, an update or delete of a missing one, a
//                                  restore plan bound to another project or revision than the selection);
//   drift_adopt_store_invalid      the store's project.json is missing, not UTF-8, not JSON, or has a null project,
//                                  element, geometry, dependencies or receipts (Plan: the same for the head it is
//                                  given); also project.json turning non-JSON before Commit reads it under its lock;
//   drift_adopt_project_mismatch   the head is another Core project than the selection's;
//   drift_adopt_stale_revision     the head is not the selection's revision, or its canonical hash (elements ordered by
//                                  id) is not the selection's restore_plan after_hash; also Commit's revision_conflict
//                                  and idempotency_conflict (the store moved between the read and the commit);
//   drift_adopt_invalid_ir         the adopted model fails Compiler.Validate;
//   drift_adopt_store_unavailable  project.json cannot be read, or Commit's store_locked (the lock is held);
//   drift_adopt_internal_error     Commit's plan_mismatch (the plan disagreeing with its own full diff): a defect,
//                                  refused by Commit before its first write;
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
    public const string InternalError = "drift_adopt_internal_error";
}

public static class MirrorDriftAdoptOutcomes
{
    public const string Committed = "committed";
    public const string Replayed = "replayed";
    public const string NothingToAdopt = "nothing_to_adopt";
    // Preview outcomes: what Commit would report on the store as read.
    public const string WouldCommit = "would_commit";
    public const string WouldReplay = "would_replay";
}

public sealed class MirrorDriftAdoptException(string code, string detail, Exception? inner = null)
    : InvalidOperationException(code + (detail.Length > 0 ? " " + detail : ""), inner)
{
    public string Code { get; } = code;
}

// Revision: the store head after the call (for nothing_to_adopt, the selection's revision). Key: null for
// nothing_to_adopt. Head: the store head (null for nothing_to_adopt). Plan: the committed plan (committed only).
// From Preview: Revision is what Commit would report, Head the head as read, Plan the would_commit plan.
public sealed record MirrorDriftAdoptResult(string Outcome, int Revision, string? Key, Project? Head, ExecutionPlan? Plan);

public static class MirrorDriftAdoptCommit
{
    public const string KeyPrefix = "mirror-drift-adopt:";
    static readonly UTF8Encoding Utf8 = new(false, true);

    public static string Key(MirrorDriftSelection selection)
    {
        CheckSelection(selection);
        var digest = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(selection.ToJson()))).ToLowerInvariant();
        return KeyPrefix + selection.CoreProjectId.ToString("D") + ":" + selection.CoreRevision.ToString(CultureInfo.InvariantCulture) + ":" + digest;
    }

    // The desired project and plan the selection's adopt operations give on this head; refuses a head that is not the
    // selection's project, revision and hash. Pure.
    public static (Project Desired, ExecutionPlan Plan) Plan(Project head, MirrorDriftSelection selection)
    {
        ArgumentNullException.ThrowIfNull(head);
        CheckSelection(selection);
        if (head.Elements is null || head.Elements.Any(element => element is null || element.GeometryMetres is null || element.Dependencies is null))
            throw Fail(MirrorDriftAdoptCodes.StoreInvalid, "the head has null elements, geometry or dependencies");
        if (head.Id != selection.CoreProjectId) throw Fail(MirrorDriftAdoptCodes.ProjectMismatch, "head project " + head.Id.ToString("D") + " is not the selection's " + selection.CoreProjectId.ToString("D"));
        if (head.Revision != selection.CoreRevision)
            throw Fail(MirrorDriftAdoptCodes.StaleRevision, "head revision " + head.Revision.ToString(CultureInfo.InvariantCulture) + " is not the selection's " + selection.CoreRevision.ToString(CultureInfo.InvariantCulture));
        var hash = Compiler.Hash(head with { Elements = head.Elements.OrderBy(element => element.Id.ToString("D"), StringComparer.Ordinal).ToArray() });
        if (hash != selection.RestorePlan.AfterHash) throw Fail(MirrorDriftAdoptCodes.StaleRevision, "head hash " + hash + " is not the selection's " + selection.RestorePlan.AfterHash);

        var existing = head.Elements.Select(element => element.Id).ToHashSet();
        var updates = new Dictionary<Guid, Element>();
        var deletes = new HashSet<Guid>();
        var creates = new List<Element>();
        foreach (var operation in selection.Operations)
        {
            var id = operation.Element.Id;
            var present = existing.Contains(id);
            if (operation.Action == MirrorDriftAdoptActions.Create ? present : !present)
                throw Fail(MirrorDriftAdoptCodes.SelectionInvalid, operation.Action + " of " + id.ToString("D") + (present ? ", which the head already has" : ", which the head lacks"));
            if (operation.Action == MirrorDriftAdoptActions.Create) creates.Add(operation.Element);
            else if (operation.Action == MirrorDriftAdoptActions.Update) updates[id] = operation.Element;
            else deletes.Add(id);
        }
        var elements = head.Elements.Where(element => !deletes.Contains(element.Id)).Select(element => updates.TryGetValue(element.Id, out var updated) ? updated : element).Concat(creates).ToArray();
        var desired = head with { Elements = elements };
        var issues = Compiler.Validate(desired);
        if (issues.Length > 0) throw Fail(MirrorDriftAdoptCodes.InvalidIr, string.Join(",", issues.Select(issue => issue.Code + (issue.Id is { } at ? "@" + at.ToString("D") : ""))));
        ExecutionPlan plan;
        try { plan = Compiler.Diff(head, desired); }
        catch (InvalidOperationException error) when (error.Message == "invalid_ir") { throw Fail(MirrorDriftAdoptCodes.StoreInvalid, "the head fails validation"); }
        return (desired, plan);
    }

    // Read-only: no lock, no write. Outcome would_commit (Revision head + 1, Head the current head, Plan the plan),
    // would_replay (Revision and Head the current head, Plan null) or nothing_to_adopt (as Commit). Commit's refusals.
    public static MirrorDriftAdoptResult Preview(string storeDirectory, MirrorDriftSelection selection)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeDirectory);
        CheckSelection(selection);
        if (selection.Operations.Count == 0) return new(MirrorDriftAdoptOutcomes.NothingToAdopt, selection.CoreRevision, null, null, null);
        var key = Key(selection);
        var state = ReadState(storeDirectory);
        if (state.Receipts.ContainsKey(key)) return new(MirrorDriftAdoptOutcomes.WouldReplay, state.Project.Revision, key, state.Project, null);
        var (_, plan) = Plan(state.Project, selection);
        return new(MirrorDriftAdoptOutcomes.WouldCommit, plan.ExpectedRevision + 1, key, state.Project, plan);
    }

    public static MirrorDriftAdoptResult Commit(string storeDirectory, MirrorDriftSelection selection) => Commit(storeDirectory, selection, null);

    // Test seam: beforeCommit runs after the receipt check and Plan, just before ProjectStore.Commit, and returns the
    // plan to commit (deterministic interleavings: a concurrent identical commit, a store changed under the call).
    internal static MirrorDriftAdoptResult Commit(string storeDirectory, MirrorDriftSelection selection, Func<ExecutionPlan, ExecutionPlan>? beforeCommit)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeDirectory);
        CheckSelection(selection);
        if (selection.Operations.Count == 0) return new(MirrorDriftAdoptOutcomes.NothingToAdopt, selection.CoreRevision, null, null, null);
        var key = Key(selection);
        var state = ReadState(storeDirectory);
        if (state.Receipts.ContainsKey(key)) return new(MirrorDriftAdoptOutcomes.Replayed, state.Project.Revision, key, state.Project, null);
        var (desired, plan) = Plan(state.Project, selection);
        if (beforeCommit is not null) plan = beforeCommit(plan);
        Project committed;
        try { committed = new ProjectStore(storeDirectory).Commit(desired, plan, key); }
        catch (InvalidOperationException error) when (error.Message is "revision_conflict" or "idempotency_conflict") { throw Fail(MirrorDriftAdoptCodes.StaleRevision, "the store moved before the commit (" + error.Message + ")", error); }
        catch (InvalidOperationException error) when (error.Message == "invalid_ir") { throw Fail(MirrorDriftAdoptCodes.InvalidIr, "refused by the store", error); }
        catch (InvalidOperationException error) when (error.Message == "plan_mismatch") { throw Fail(MirrorDriftAdoptCodes.InternalError, "the store's diff disagrees with the plan", error); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException) { throw Fail(MirrorDriftAdoptCodes.StoreInvalid, "project.json is not a store state at commit", error); }
        catch (IOException error) when (error.Message == ProjectStore.StoreLocked) { throw Fail(MirrorDriftAdoptCodes.StoreUnavailable, "the store lock is held", error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw Fail(MirrorDriftAdoptCodes.CommitIncomplete, error.GetType().Name + ": " + error.Message, error); }
        // Commit writes and returns desired with the next revision (same element array), or returns the stored head
        // without writing when the key's receipt is already there (a concurrent identical call won the lock).
        if (!ReferenceEquals(committed.Elements, desired.Elements) || committed.Revision != plan.ExpectedRevision + 1)
            return new(MirrorDriftAdoptOutcomes.Replayed, committed.Revision, key, committed, null);
        return new(MirrorDriftAdoptOutcomes.Committed, committed.Revision, key, committed, plan);
    }

    static ProjectStore.State ReadState(string storeDirectory)
    {
        string text;
        try
        {
            using var stream = new FileStream(Path.Combine(storeDirectory, "project.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Utf8);
            text = reader.ReadToEnd();
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { throw Fail(MirrorDriftAdoptCodes.StoreInvalid, "the store has no project.json", error); }
        catch (DecoderFallbackException error) { throw Fail(MirrorDriftAdoptCodes.StoreInvalid, "project.json is not UTF-8", error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw Fail(MirrorDriftAdoptCodes.StoreUnavailable, error.GetType().Name + ": " + error.Message, error); }
        ProjectStore.State? state;
        try { state = JsonSerializer.Deserialize<ProjectStore.State>(text); }
        catch (JsonException error) { throw Fail(MirrorDriftAdoptCodes.StoreInvalid, "project.json is not a store state", error); }
        var project = state?.Project;
        if (state?.Receipts is null || project?.Elements is null || project.Elements.Any(element => element is null || element.GeometryMetres is null || element.Dependencies is null))
            throw Fail(MirrorDriftAdoptCodes.StoreInvalid, "project.json has a null project, element, geometry, dependencies or receipts");
        return state;
    }

    static void CheckSelection(MirrorDriftSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Operations is null || selection.RestorePlan?.AfterHash is null || selection.Choices is null || selection.Omitted is null
            || selection.MapAdds is null || selection.MapRemovals is null || selection.Invalidated is null || selection.DocumentUid is null)
            throw Fail(MirrorDriftAdoptCodes.SelectionInvalid, "the selection's parts are required");
        if (selection.RestorePlan.ProjectId != selection.CoreProjectId || selection.RestorePlan.ExpectedRevision != selection.CoreRevision)
            throw Fail(MirrorDriftAdoptCodes.SelectionInvalid, "the restore plan is bound to another project or revision than the selection");
        var ids = new HashSet<Guid>();
        foreach (var operation in selection.Operations)
        {
            if (operation?.Element is not { } element || element.Kind is null || element.GeometryMetres is null || element.Dependencies is null || element.Provenance is null
                || operation.Action is not (MirrorDriftAdoptActions.Create or MirrorDriftAdoptActions.Update or MirrorDriftAdoptActions.Delete))
                throw Fail(MirrorDriftAdoptCodes.SelectionInvalid, "an operation needs create, update or delete and a complete element");
            if (!ids.Add(element.Id)) throw Fail(MirrorDriftAdoptCodes.SelectionInvalid, "two operations on " + element.Id.ToString("D"));
        }
    }

    static MirrorDriftAdoptException Fail(string code, string detail, Exception? inner = null) => new(code, detail, inner);
}
