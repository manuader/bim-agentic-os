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
    public const string Verb = "apply-edits";
    public const string Usage = "usage: apply-edits --store store-directory --edits edits.json [--expected-revision n]";
    public const string UsageCode = "apply_edits_usage";
    public const string StoreInvalid = "apply_edits_store_invalid";
    public const string EditsInvalid = "apply_edits_edits_invalid";
    public const string StaleHead = "apply_edits_stale_head";

    // P3-16: the only print. Everything below returns the IntentEnvelope (interfaces/cli/IntentPlan.cs) the verb ends
    // with: the stdout line, the exit status and, for commit_incomplete and internal_error, the stderr detail.
    public static void Run(string[] args) => Execute(args).Print();

    // The apply-edits verb without writing Console or setting Environment.ExitCode (the store is still committed).
    internal static IntentEnvelope Execute(string[] args)
    {
        if (!TryParse(args, "--edits", out var storePath, out var editsPath, out var expectedRevision)) return Fail(UsageCode);
        string stateText, editsText;
        try
        {
            stateText = BoundedInput.ReadText(Path.Combine(storePath, "project.json"), FileShare.ReadWrite | FileShare.Delete);
            editsText = BoundedInput.ReadText(editsPath);
        }
        catch (InputRejectedException error) { return Fail(error.Message); }
        if (ParseHead(stateText) is not { } head) return Fail(StoreInvalid);
        if (EditsInput.Parse(editsText) is not { } edits) return Fail(EditsInvalid);
        if (expectedRevision is { } expected && expected != head.Revision) return Fail(StaleHead);
        IncrementalDiffResult result;
        try { result = IncrementalDiff.Diff(head, edits); }
        catch (ArgumentException error) when (EditsInput.LibraryCodes.Contains(error.Message, StringComparer.Ordinal)) { return Fail(error.Message); }
        catch (InvalidOperationException error) when (error.Message == "invalid_ir") { return Fail("invalid_ir"); }
        catch (Exception error) { return Internal(Verb, error); }
        return Commit(storePath, result, Verb);
    }

    // The commit path shared with apply-intent (P3-11, interfaces/cli/ApplyIntentCommand.cs): ProjectStore.Commit of
    // result.After with result.Plan under a per-run idempotency key "<verb>:<rev>:<random guid>", the refusals above with
    // the verb's prefix (verb "apply-edits" gives exactly the apply_edits_* codes), and the success envelope (exit 0),
    // returned for the caller to print.
    internal static IntentEnvelope Commit(string storePath, IncrementalDiffResult result, string verb)
    {
        var prefix = verb.Replace('-', '_');
        var plan = result.Plan;
        var key = string.Create(CultureInfo.InvariantCulture, $"{verb}:{plan.ExpectedRevision}:{Guid.NewGuid():N}");
        Project committed;
        try { committed = new ProjectStore(storePath).Commit(result.After, plan, key); }
        catch (InvalidOperationException error) when (error.Message is "revision_conflict" or "idempotency_conflict") { return Fail(prefix + "_stale_head"); }
        catch (InvalidOperationException error) when (error.Message == "invalid_ir") { return Fail("invalid_ir"); }
        // The incremental plan disagreeing with the full diff is a defect, refused by Commit before its first write.
        catch (InvalidOperationException error) when (error.Message == "plan_mismatch") { return Internal(verb, error); }
        catch (IOException error) when (error.Message == ProjectStore.StoreLocked) { return Fail(prefix + "_store_unavailable"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Fail(prefix + "_commit_incomplete") with { Diagnostic = verb + " commit incomplete: " + error.GetType().FullName + ": " + error.Message };
        }
        catch (Exception error) { return Internal(verb, error); }
        // Commit returns the head unchanged, without writing, only for a key it already holds; a fresh random key never
        // matches, so this is a defensive check that this call wrote the revision it reports.
        if (committed.Revision != plan.ExpectedRevision + 1) return Fail(prefix + "_stale_head");
        return new IntentEnvelope(JsonSerializer.Serialize(new { status = "succeeded", scope = "intended_state_only", native_verification = "unverified", revision = committed.Revision, plan }), 0);
    }

    // --store <s> and <inputFlag> <e> (--edits here, --intent for apply-intent) once each, optional --expected-revision <n>
    // once, each with a non-empty value that is not flag-shaped ("--..."), in any order; nothing else. n is decimal digits
    // only, within int.
    internal static bool TryParse(string[] args, string inputFlag, out string storePath, out string editsPath, out int? expectedRevision)
    {
        storePath = ""; editsPath = ""; expectedRevision = null;
        if (args.Length != 5 && args.Length != 7) return false;
        string? store = null, edits = null, revision = null;
        for (var i = 1; i < args.Length; i += 2)
        {
            var value = args[i + 1];
            if (value.Length == 0 || value.StartsWith("--", StringComparison.Ordinal)) return false;
            if (args[i] == "--store" && store is null) store = value;
            else if (args[i] == inputFlag && edits is null) edits = value;
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

    internal static Project? ParseHead(string text)
    {
        ProjectStore.State? state;
        try { state = JsonSerializer.Deserialize<ProjectStore.State>(text); }
        catch (JsonException) { return null; }
        var project = state?.Project;
        // Commit dereferences the receipts and the index dereferences these; a null one is a malformed store, not a crash.
        if (state?.Receipts is null || project?.Elements is null || project.Elements.Any(e => e is null || e.GeometryMetres is null || e.Dependencies is null)) return null;
        return project;
    }

    // The refusal envelopes have one copy, in IntentPlan: {"status":"failed","code":c}, exit 1, and the
    // <verb>_internal_error refusal with its "<verb> internal error: <type>: <message>" stderr detail.
    internal static IntentEnvelope Internal(string verb, Exception error) => IntentPlan.Internal(verb, error);

    internal static IntentEnvelope Fail(string code) => IntentPlan.Failed(code);
}
