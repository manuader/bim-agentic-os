#nullable enable
using System.Text.Json;
using Bim.Core;

namespace Bim.Cli;

// P3-13: the one plan path shared by apply-intent (interfaces/cli/ApplyIntentCommand.cs) and diff-intent
// (interfaces/cli/DiffIntentCommand.cs), so the plan apply-intent commits and the plan diff-intent prints are byte-equal by
// construction rather than by two copies kept in step. Both verbs read their files themselves (BoundedInput) and parse their
// head themselves (a store state or a bare Project); everything from the intent text to the plan happens here:
//   ParseIntent  IntentJson.Parse; an IntentException prints {"status":"failed","code":<verb>_intent_invalid,"reason","field"}.
//   Build        IntentEdits.FromStored(head, intent), then IncrementalDiff.Diff(head, set.Edits,
//                createOrder: IncrementalDiff.OrderOf(set.After)). An IntentCompiler refusal prints
//                {"status":"failed","code":<verb>_intent_refused,"refusals":[{"code","subject","detail"}...]}; the library's
//                edit_bound_exceeded, invalid_project_identity, invalid_ir and the diff's EditsInput.LibraryCodes print
//                {"status":"failed","code":c}; anything else is <verb>_internal_error (ApplyEditsCommand.Internal).
// They are two calls, not one, because apply-intent checks --expected-revision between parsing the intent and compiling it
// (its documented precedence). Every refusal exits 1 and the method returns null; the caller then stops. Nothing here reads
// or writes a file or a store.
internal static class IntentPlan
{
    const string InvalidIdentity = "invalid_project_identity";

    public static LayoutIntent? ParseIntent(string text, string verb, string invalidCode)
    {
        try { return IntentJson.Parse(text); }
        catch (IntentException error)
        {
            Refuse(JsonSerializer.Serialize(new { status = "failed", code = invalidCode, reason = error.Code, field = error.Field }));
            return null;
        }
        catch (Exception error) { ApplyEditsCommand.Internal(verb, error); return null; }
    }

    public static IncrementalDiffResult? Build(Project head, LayoutIntent intent, string verb, string refusedCode)
    {
        IntentEditSet set;
        try { set = IntentEdits.FromStored(head, intent); }
        catch (IntentException error)
        {
            var issues = error.Issues.Count > 0 ? error.Issues : new[] { new IntentIssue(error.Code, error.Field ?? "", error.Detail ?? "") };
            Refuse(JsonSerializer.Serialize(new { status = "failed", code = refusedCode, refusals = issues.Select(i => new { code = i.Code, subject = i.Key, detail = i.Detail }) }));
            return null;
        }
        catch (ArgumentException error) when (error.Message is IncrementalDiff.BoundExceeded or InvalidIdentity) { ApplyEditsCommand.Fail(error.Message); return null; }
        catch (InvalidOperationException error) when (error.Message == "invalid_ir") { ApplyEditsCommand.Fail("invalid_ir"); return null; }
        catch (Exception error) { ApplyEditsCommand.Internal(verb, error); return null; }
        try { return IncrementalDiff.Diff(head, set.Edits, createOrder: IncrementalDiff.OrderOf(set.After)); }
        catch (ArgumentException error) when (EditsInput.LibraryCodes.Contains(error.Message, StringComparer.Ordinal)) { ApplyEditsCommand.Fail(error.Message); return null; }
        catch (InvalidOperationException error) when (error.Message == "invalid_ir") { ApplyEditsCommand.Fail("invalid_ir"); return null; }
        catch (Exception error) { ApplyEditsCommand.Internal(verb, error); return null; }
    }

    static void Refuse(string envelope)
    {
        Console.WriteLine(envelope);
        Environment.ExitCode = 1;
    }
}
