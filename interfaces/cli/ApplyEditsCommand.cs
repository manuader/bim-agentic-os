#nullable enable
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bim.Core;

namespace Bim.Cli;

// P3-08 `apply-edits --store store-directory --edits edits.json [--expected-revision n]`: the P3-05 incremental diff of
// the store head, committed through ProjectStore.Commit (core/Bim.Core/Model.cs) with the plan's expected head.
//   store-directory  an initialized ProjectStore; its project.json (the persisted {Project, Receipts} state) is read
//                    through BoundedInput.ReadText with FileShare.ReadWrite | FileShare.Delete (the store replaces it by
//                    temp + move), refusing null projects, elements, geometry, dependencies or receipts.
//   edits.json       the diff-edits edits format (a JSON array of ElementEdit; unknown members and duplicate keys refused
//                    in every object), parsed exactly as DiffEditsCommand does.
//   --expected-revision n  optional; the head revision the caller planned against (a non-negative decimal integer). A
//                    store at any other revision is refused before anything is diffed.
// The plan is IncrementalDiff.Diff(head, edits).Plan, so its ExpectedRevision and BeforeHash are the head that was read;
// ProjectStore.Commit re-checks both under its file lock, so a store that moved between the read and the commit is
// refused as stale too. The idempotency key is derived from the plan ("apply-edits:<rev>:<before>:<after>"): the same
// edits on the same head are one commit.
// Success: stdout {"status":"succeeded","scope":"intended_state_only","native_verification":"unverified","revision":<head+1>,
// "plan":<plan>}, where "plan" is byte-identical to diff-edits' stdout on the head project and the same edits file; exit 0.
// Every refusal prints {"status":"failed","code":<code>} and exits 1: apply_edits_usage, input_* (store project.json or
// edits), apply_edits_store_invalid, apply_edits_edits_invalid, apply_edits_stale_head, the library's edit_invalid,
// edit_unknown_id, edit_duplicate_id, edit_create_exists, edit_bound_exceeded and invalid_ir, apply_edits_store_unavailable
// (the store's commit lock is held by another committer, or the store could not be opened for the commit) and
// apply_edits_internal_error. Nothing is written on any refusal: every check runs before ProjectStore.Commit, and Commit
// itself refuses (revision_conflict, idempotency_conflict, invalid_ir, plan_mismatch, a held lock) before its first write.
// An I/O failure after Commit started writing is the ADR0007 crash case (checkpoint/journal recovery), not a refusal.
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
    public const string InternalError = "apply_edits_internal_error";
    static readonly string[] LibraryCodes =
    {
        IncrementalDiff.InvalidEdit, IncrementalDiff.UnknownId, IncrementalDiff.DuplicateId, IncrementalDiff.CreateExists, IncrementalDiff.BoundExceeded,
    };
    static readonly JsonSerializerOptions EditsOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

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
        if (ParseEdits(editsText) is not { } edits) { Fail(EditsInvalid); return; }
        if (expectedRevision is { } expected && expected != head.Revision) { Fail(StaleHead); return; }
        IncrementalDiffResult result;
        try { result = IncrementalDiff.Diff(head, edits); }
        catch (ArgumentException error) when (LibraryCodes.Contains(error.Message, StringComparer.Ordinal)) { Fail(error.Message); return; }
        catch (InvalidOperationException error) when (error.Message == "invalid_ir") { Fail("invalid_ir"); return; }
        catch (Exception error) { Internal(error); return; }
        var plan = result.Plan;
        var key = string.Create(CultureInfo.InvariantCulture, $"apply-edits:{plan.ExpectedRevision}:{plan.BeforeHash}:{plan.AfterHash}");
        Project committed;
        try { committed = new ProjectStore(storePath).Commit(result.After, plan, key); }
        catch (InvalidOperationException error) when (error.Message is "revision_conflict" or "idempotency_conflict") { Fail(StaleHead); return; }
        catch (InvalidOperationException error) when (error.Message == "invalid_ir") { Fail("invalid_ir"); return; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Fail(StoreUnavailable); return; }
        catch (Exception error) { Internal(error); return; }
        // A receipt already holding this key returns the head unchanged without writing: not a new revision.
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

    // Same rules as DiffEditsCommand.ParseEdits; null entries and null element fields are left to the library (edit_invalid).
    static ElementEdit[]? ParseEdits(string text)
    {
        try
        {
            using (var document = JsonDocument.Parse(text)) if (HasDuplicateKey(document.RootElement)) return null;
            return JsonSerializer.Deserialize<ElementEdit[]>(text, EditsOptions);
        }
        catch (JsonException) { return null; }
    }

    static bool HasDuplicateKey(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                    if (!names.Add(property.Name) || HasDuplicateKey(property.Value)) return true;
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) if (HasDuplicateKey(item)) return true;
                return false;
            default:
                return false;
        }
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
