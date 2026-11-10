#nullable enable
using System.Globalization;
using System.Text.Json;
using Bim.Core;

namespace Bim.Cli;

// P3-08 `apply-edits --store store-directory --edits edits.json [--expected-revision n]`: the P3-05 incremental diff of
// the store head, committed through ProjectStore.Commit (core/Bim.Core/Model.cs) with the plan's expected head.
//   store-directory  an initialized ProjectStore; its project.json (the persisted {Project, Receipts} state) is read
//                    through BoundedInput.ReadText with FileShare.ReadWrite | FileShare.Delete (the store replaces it by
//                    temp + move). The store state is therefore bounded at 8 MiB like every CLI input (input_too_large
//                    beyond; each commit adds one receipt of about 140 bytes). Null projects, elements, geometry,
//                    dependencies or receipts are refused (apply_edits_store_invalid).
//   edits.json       the diff-edits edits format, parsed by the shared interfaces/cli/EditsInput.cs.
//   --expected-revision n  optional; the head revision the caller planned against (a non-negative decimal integer). A
//                    store at any other revision is refused before anything is diffed. Passing it makes a retry safe:
//                    a retry of a commit that already landed is refused as stale instead of applying the edits again.
// The plan is IncrementalDiff.Diff(head, edits).Plan, so its ExpectedRevision and BeforeHash are the head that was read;
// ProjectStore.Commit re-checks both under its file lock, so a store that moved between the read and the commit is
// refused as stale too. The idempotency key is unique per run ("apply-edits:<rev>:<random guid>"): two runs racing on
// the same head with the same edits never share a receipt, so exactly one commits and the other is refused as stale
// (or store_unavailable while the winner holds the lock); a run never reports another run's commit as its own.
// An empty edit set (or edits that change nothing) is not refused: it commits a new revision with an empty plan.
// Success: stdout {"status":"succeeded","scope":"intended_state_only","native_verification":"unverified","revision":<head+1>,
// "plan":<plan>}, where "plan" is byte-identical to diff-edits' stdout on the head project and the same edits file; exit 0.
// Every refusal prints {"status":"failed","code":<code>} and exits 1:
//   apply_edits_usage, input_* (store project.json or edits), apply_edits_store_invalid, apply_edits_edits_invalid,
//   apply_edits_stale_head, the library's edit_invalid, edit_unknown_id, edit_duplicate_id, edit_create_exists,
//   edit_bound_exceeded and invalid_ir, and apply_edits_store_unavailable (Commit's store_locked: the commit lock is held
//   by another committer or cannot be opened). Nothing is written on any of these: every check runs before
//   ProjectStore.Commit, and Commit refuses store_locked, revision_conflict, idempotency_conflict and invalid_ir before
//   its first write.
//   apply_edits_commit_incomplete: an I/O or access failure inside Commit after the lock was taken. Commit may have
//   written checkpoint-<rev>.json (the unchanged head), the "prepared" journal line and project.tmp, while project.json
//   still holds the old head: the ADR0007 crash case, recovered by reconciling journal and checkpoint by hashes (tests
//   pin the case project.tmp is a directory). Not a "nothing written" refusal.
//   apply_edits_internal_error: a defect (stderr gets its type and message), including Commit's plan_mismatch (the
//   incremental plan disagreeing with the full diff, refused by Commit before its first write).
// Precedence: usage, then both files are read (store first), then the store is parsed, then the edits, then the expected
// revision, then the library, then the commit; the first failure is the only one reported.
internal static class ApplyEditsCommand
{
    public const string Usage = "usage: apply-edits --store store-directory --edits edits.json [--expected-revision n]";
    public const string UsageCode = "apply_edits_usage";
    public const string StoreInvalid = "apply_edits_store_invalid";
    public const string EditsInvalid = "apply_edits_edits_invalid";
    public const string StaleHead = "apply_edits_stale_head";
    public const string StoreUnavailable = "apply_edits_store_unavailable";
    public const string CommitIncomplete = "apply_edits_commit_incomplete";
    public const string InternalError = "apply_edits_internal_error";

    public static void Run(string[] args)
    {
        if (!TryParse(args, out var storePath, out var editsPath, out var expectedRevision)) { Fail(UsageCode); return; }
        string stateText, editsText;
        try
        {
            stateText = BoundedInput.ReadText(Path.Combine(storePath, "project.json"), FileShare.ReadWrite | FileShare.Delete);
            editsText = BoundedInput.ReadText(editsPath);
        }
        catch (InputRejectedException error) { Fail(error.Message); return; }
        if (ParseHead(stateText) is not { } head) { Fail(StoreInvalid); return; }
        if (EditsInput.Parse(editsText) is not { } edits) { Fail(EditsInvalid); return; }
        if (expectedRevision is { } expected && expected != head.Revision) { Fail(StaleHead); return; }
        IncrementalDiffResult result;
        try { result = IncrementalDiff.Diff(head, edits); }
        catch (ArgumentException error) when (EditsInput.LibraryCodes.Contains(error.Message, StringComparer.Ordinal)) { Fail(error.Message); return; }
        catch (InvalidOperationException error) when (error.Message == "invalid_ir") { Fail("invalid_ir"); return; }
        catch (Exception error) { Internal(error); return; }
        var plan = result.Plan;
        var key = string.Create(CultureInfo.InvariantCulture, $"apply-edits:{plan.ExpectedRevision}:{Guid.NewGuid():N}");
        Project committed;
        try { committed = new ProjectStore(storePath).Commit(result.After, plan, key); }
        catch (InvalidOperationException error) when (error.Message is "revision_conflict" or "idempotency_conflict") { Fail(StaleHead); return; }
        catch (InvalidOperationException error) when (error.Message == "invalid_ir") { Fail("invalid_ir"); return; }
        // The incremental plan disagreeing with the full diff is a defect, refused by Commit before its first write.
        catch (InvalidOperationException error) when (error.Message == "plan_mismatch") { Internal(error); return; }
        catch (IOException error) when (error.Message == ProjectStore.StoreLocked) { Fail(StoreUnavailable); return; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("apply-edits commit incomplete: " + error.GetType().FullName + ": " + error.Message);
            Fail(CommitIncomplete);
            return;
        }
        catch (Exception error) { Internal(error); return; }
        // Commit returns the head unchanged, without writing, only for a key it already holds; a fresh random key never
        // matches, so this is a defensive check that this call wrote the revision it reports.
        if (committed.Revision != plan.ExpectedRevision + 1) { Fail(StaleHead); return; }
        Console.WriteLine(JsonSerializer.Serialize(new { status = "succeeded", scope = "intended_state_only", native_verification = "unverified", revision = committed.Revision, plan }));
    }

    // --store <s> and --edits <e> once each, optional --expected-revision <n> once, each with a non-empty value that is
    // not flag-shaped ("--..."), in any order; nothing else. n is decimal digits only, within int.
    static bool TryParse(string[] args, out string storePath, out string editsPath, out int? expectedRevision)
    {
        storePath = ""; editsPath = ""; expectedRevision = null;
        if (args.Length != 5 && args.Length != 7) return false;
        string? store = null, edits = null, revision = null;
        for (var i = 1; i < args.Length; i += 2)
        {
            var value = args[i + 1];
            if (value.Length == 0 || value.StartsWith("--", StringComparison.Ordinal)) return false;
            if (args[i] == "--store" && store is null) store = value;
            else if (args[i] == "--edits" && edits is null) edits = value;
            else if (args[i] == "--expected-revision" && revision is null) revision = value;
            else return false;
        }
        if (store is null || edits is null) return false;
        if (revision is not null)
        {
            if (!int.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return false;
            expectedRevision = parsed;
        }
        storePath = store; editsPath = edits;
        return true;
    }

    static Project? ParseHead(string text)
    {
        ProjectStore.State? state;
        try { state = JsonSerializer.Deserialize<ProjectStore.State>(text); }
        catch (JsonException) { return null; }
        var project = state?.Project;
        // Commit dereferences the receipts and the index dereferences these; a null one is a malformed store, not a crash.
        if (state?.Receipts is null || project?.Elements is null || project.Elements.Any(e => e is null || e.GeometryMetres is null || e.Dependencies is null)) return null;
        return project;
    }

    static void Internal(Exception error)
    {
        Console.Error.WriteLine("apply-edits internal error: " + error.GetType().FullName + ": " + error.Message);
        Fail(InternalError);
    }

    static void Fail(string code)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", code }));
        Environment.ExitCode = 1;
    }
}
