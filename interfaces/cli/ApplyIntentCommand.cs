#nullable enable
using Bim.Core;

namespace Bim.Cli;

// P3-11 `apply-intent --store store-directory --intent after.json [--expected-revision n]`: the store head moved to a
// layout intent through the P3-09 edit set and the P3-05 incremental diff, committed like apply-edits.
//   store-directory  an initialized ProjectStore, read and checked exactly as apply-edits reads it
//                    (ApplyEditsCommand.ParseHead; apply_intent_store_invalid).
//   after.json       a canonical layout intent v1 (core/Bim.Core/IntentJson.cs): read through BoundedInput.ReadText (8 MiB,
//                    input_* codes), then IntentJson.Parse (at most IntentJson.MaxInputChars characters, depth and list
//                    bounds, unknown and duplicate members refused).
//   --expected-revision n  as apply-edits: a store at any other revision is refused before anything is compiled; a retry
//                    of a commit that already landed is refused as stale instead of applying the intent again.
// Edits: IntentEdits.FromStored(head, intent) compiles the intent with the head's project id and revision and returns
// one update per element whose compiled form changed, one create per new element and one delete per head element the
// intent no longer produces (every head element absent from the compile, intent-made or not). The plan is
// IncrementalDiff.Diff(head, edits, createOrder: IncrementalDiff.OrderOf(set.After)).Plan: creates sit at their compile
// position, so the plan is byte-identical to the full diff (`diff`) of head against compile(intent) and the committed head is compile(intent)
// in compile order whenever the head is in compile order (it is after init from a compile and after every apply-intent).
// It is byte-identical to `diff-edits --project <head> --edits <the same edits>` whenever no created element sorts before
// a surviving one (diff-edits appends creates; with an earlier create the two plans hold the same operations in another
// order and differ in AfterHash).
// Commit: ApplyEditsCommand.Commit, the commit path apply-edits uses (ProjectStore.Commit with the plan's expected head,
// a per-run idempotency key "apply-intent:<rev>:<random guid>"), with the apply_intent_ prefix on its codes. An intent
// that changes nothing commits a new revision with an empty plan, like an empty apply-edits edit set.
// Success: stdout {"status":"succeeded","scope":"intended_state_only","native_verification":"unverified","revision":<head+1>,
// "plan":<plan>}; exit 0. Every refusal exits 1 with nothing written (except apply_intent_commit_incomplete):
//   {"status":"failed","code":c} for apply_intent_usage, input_* (store project.json or intent), apply_intent_store_invalid,
//     apply_intent_stale_head, apply_intent_store_unavailable, apply_intent_commit_incomplete (the ADR0007 crash case, see
//     ApplyEditsCommand), apply_intent_internal_error (a defect; stderr gets its type and message), invalid_ir (the head
//     is not Compiler.Validate-clean, or the diff or commit refuses the result), invalid_project_identity (the head's
//     project id is empty or its revision negative) and edit_bound_exceeded (more than IncrementalDiff.MaxEdits edits);
//   {"status":"failed","code":"apply_intent_intent_invalid","reason":r,"field":f} when IntentJson.Parse refuses the file:
//     r is its IntentCodes code (json_invalid, input_too_large, version_unsupported, member_*, type_invalid, value_invalid,
//     limit_exceeded), f its JSON path or null;
//   {"status":"failed","code":"apply_intent_intent_refused","refusals":[{"code","subject","detail"}...]} when
//     IntentCompiler refuses the intent: every issue of its IntentException (sorted by code, key, detail), subject being
//     the issue's key.
// Unreachable library refusals: IntentCompiler's null_intent_item (ArgumentException; IntentJson.Parse never yields a
// null list item) and its internal invariant intent_stretch_unbounded (InvalidOperationException) are not mapped to a
// code; if either ever surfaced it would print apply_intent_internal_error (with its type and message on stderr).
// Cost: the compile of the intent is bounded by the intent caps (IntentJson.MaxInputChars, LayoutIntent.MaxLevels,
// MaxWallTypes, MaxRooms and MaxOpenings, checked by IntentJson.Parse before compiling), not by IncrementalDiff.MaxEdits:
// edit_bound_exceeded is checked only after the compile and the edit set are built. Reading, ParseHead and the diff are
// O(store head), as in apply-edits.
// Plan: the intent parse, edit set, diff and their refusal envelopes are interfaces/cli/IntentPlan.cs (P3-13), shared
// with diff-intent, so the plan member here and diff-intent's stdout are one code path.
// Precedence: usage, then both files are read (store first), then the store is parsed, then the intent, then the
// expected revision, then the compile and edit set, then the diff, then the commit; the first failure is the only one.
internal static class ApplyIntentCommand
{
    public const string Verb = "apply-intent";
    public const string Usage = "usage: apply-intent --store store-directory --intent after.json [--expected-revision n]";
    public const string UsageCode = "apply_intent_usage";
    public const string StoreInvalid = "apply_intent_store_invalid";
    public const string IntentInvalid = "apply_intent_intent_invalid";
    public const string IntentRefused = "apply_intent_intent_refused";
    public const string StaleHead = "apply_intent_stale_head";

    public static void Run(string[] args)
    {
        if (!ApplyEditsCommand.TryParse(args, "--intent", out var storePath, out var intentPath, out var expectedRevision)) { ApplyEditsCommand.Fail(UsageCode); return; }
        string stateText, intentText;
        try
        {
            stateText = BoundedInput.ReadText(Path.Combine(storePath, "project.json"), FileShare.ReadWrite | FileShare.Delete);
            intentText = BoundedInput.ReadText(intentPath);
        }
        catch (InputRejectedException error) { ApplyEditsCommand.Fail(error.Message); return; }
        if (ApplyEditsCommand.ParseHead(stateText) is not { } head) { ApplyEditsCommand.Fail(StoreInvalid); return; }
        if (IntentPlan.ParseIntent(intentText, Verb, IntentInvalid) is not { } intent) return;
        if (expectedRevision is { } expected && expected != head.Revision) { ApplyEditsCommand.Fail(StaleHead); return; }
        if (IntentPlan.Build(head, intent, Verb, IntentRefused) is not { } result) return;
        ApplyEditsCommand.Commit(storePath, result, Verb);
    }
}
