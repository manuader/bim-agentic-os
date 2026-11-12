#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Bim.Core;

namespace Bim.Cli;

// P3-13: the one plan path shared by apply-intent (interfaces/cli/ApplyIntentCommand.cs) and diff-intent
// (interfaces/cli/DiffIntentCommand.cs), so the plan apply-intent commits and the plan diff-intent prints are
// byte-equal by construction rather than by two copies kept in step. Both verbs read their files themselves
// (BoundedInput) and parse their head themselves (a store state or a bare Project); everything from the intent text to
// the plan happens here:
//   ParseIntent  IntentJson.Parse; an IntentException is refused with
//                {"status":"failed","code":<verb>_intent_invalid,"reason","field"}.
//   Build        IntentEdits.FromStored(head, intent), then IncrementalDiff.Diff(head, set.Edits,
//                createOrder: IncrementalDiff.OrderOf(set.After)). An IntentCompiler refusal is
//                {"status":"failed","code":<verb>_intent_refused,"refusals":[{"code","subject","detail"}...]};
//                the library's edit_bound_exceeded, invalid_project_identity, invalid_ir and the diff's
//                EditsInput.LibraryCodes are {"status":"failed","code":c}; anything else is <verb>_internal_error
//                (Internal below).
// They are two calls, not one, because apply-intent checks --expected-revision between parsing the intent and
// compiling it (its documented precedence). Every refusal is exit 1.
// P3-15: nothing here writes Console or sets Environment.ExitCode. Each call returns an IntentStep: the value, or the
// refusal as an IntentEnvelope (the stdout line without its newline, the exit status and, for <verb>_internal_error,
// the stderr detail). Failed and Internal build the envelopes ApplyEditsCommand.Fail and ApplyEditsCommand.Internal
// print, byte for byte. The CLI verbs print an envelope with IntentEnvelope.Print; the MCP diff_intent tool returns
// DiffIntentCommand.Plan's envelope directly, without redirecting Console.Out.
// Nothing here reads or writes a file or a store.
internal static class IntentPlan
{
    const string InvalidIdentity = "invalid_project_identity";

    public static IntentStep<LayoutIntent> ParseIntent(string text, string verb, string invalidCode)
    {
        try { return IntentStep<LayoutIntent>.Ok(IntentJson.Parse(text)); }
        catch (IntentException error)
        {
            return Refuse<LayoutIntent>(JsonSerializer.Serialize(new { status = "failed", code = invalidCode, reason = error.Code, field = error.Field }));
        }
        catch (Exception error) { return IntentStep<LayoutIntent>.Refused(Internal(verb, error)); }
    }

    public static IntentStep<IncrementalDiffResult> Build(Project head, LayoutIntent intent, string verb, string refusedCode)
    {
        IntentEditSet set;
        try { set = IntentEdits.FromStored(head, intent); }
        catch (IntentException error)
        {
            var issues = error.Issues.Count > 0 ? error.Issues : new[] { new IntentIssue(error.Code, error.Field ?? "", error.Detail ?? "") };
            return Refuse<IncrementalDiffResult>(JsonSerializer.Serialize(new { status = "failed", code = refusedCode, refusals = issues.Select(i => new { code = i.Code, subject = i.Key, detail = i.Detail }) }));
        }
        catch (ArgumentException error) when (error.Message is IncrementalDiff.BoundExceeded or InvalidIdentity) { return IntentStep<IncrementalDiffResult>.Refused(Failed(error.Message)); }
        catch (InvalidOperationException error) when (error.Message == "invalid_ir") { return IntentStep<IncrementalDiffResult>.Refused(Failed("invalid_ir")); }
        catch (Exception error) { return IntentStep<IncrementalDiffResult>.Refused(Internal(verb, error)); }
        try { return IntentStep<IncrementalDiffResult>.Ok(IncrementalDiff.Diff(head, set.Edits, createOrder: IncrementalDiff.OrderOf(set.After))); }
        catch (ArgumentException error) when (EditsInput.LibraryCodes.Contains(error.Message, StringComparer.Ordinal)) { return IntentStep<IncrementalDiffResult>.Refused(Failed(error.Message)); }
        catch (InvalidOperationException error) when (error.Message == "invalid_ir") { return IntentStep<IncrementalDiffResult>.Refused(Failed("invalid_ir")); }
        catch (Exception error) { return IntentStep<IncrementalDiffResult>.Refused(Internal(verb, error)); }
    }

    // {"status":"failed","code":c}, exit 1: the line ApplyEditsCommand.Fail(c) prints.
    public static IntentEnvelope Failed(string code) => new(JsonSerializer.Serialize(new { status = "failed", code }), 1);

    // {"status":"failed","code":<verb>_internal_error}, exit 1, with the stderr detail ApplyEditsCommand.Internal prints.
    public static IntentEnvelope Internal(string verb, Exception error) =>
        new(JsonSerializer.Serialize(new { status = "failed", code = verb.Replace('-', '_') + "_internal_error" }), 1,
            verb + " internal error: " + error.GetType().FullName + ": " + error.Message);

    static IntentStep<T> Refuse<T>(string line) where T : class => IntentStep<T>.Refused(new IntentEnvelope(line, 1));
}

// One stdout line (without its newline) and the exit status a verb ends with; Diagnostic is the stderr line of a
// <verb>_internal_error refusal, null otherwise.
internal sealed record IntentEnvelope(string Line, int Exit, string? Diagnostic = null)
{
    public bool Succeeded => Exit == 0;

    // The CLI output: stderr detail (if any), then the line; a refusal sets the process exit code, a success leaves it,
    // exactly as ApplyEditsCommand.Fail / Internal and Console.WriteLine of a result did.
    public void Print()
    {
        if (Diagnostic is not null) Console.Error.WriteLine(Diagnostic);
        Console.WriteLine(Line);
        if (Exit != 0) Environment.ExitCode = Exit;
    }
}

// A plan step's value, or the refusal envelope that ends the verb.
internal sealed class IntentStep<T> where T : class
{
    readonly T? value;
    readonly IntentEnvelope? refusal;

    IntentStep(T? value, IntentEnvelope? refusal) { this.value = value; this.refusal = refusal; }

    public static IntentStep<T> Ok(T value) => new(value ?? throw new ArgumentNullException(nameof(value)), null);
    public static IntentStep<T> Refused(IntentEnvelope refusal) => new(null, refusal ?? throw new ArgumentNullException(nameof(refusal)));

    public bool TryGet([NotNullWhen(true)] out T? result, [NotNullWhen(false)] out IntentEnvelope? refused)
    {
        if (refusal is not null) { result = null; refused = refusal; return false; }
        result = value!; refused = null; return true;
    }
}
