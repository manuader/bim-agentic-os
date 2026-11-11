#nullable enable
using System.Text.Json;
using Bim.Core;

namespace Bim.Cli;

// P3-12 `diff-intent --project project.json --intent after.json`: the read-only plan apply-intent (P3-11,
// interfaces/cli/ApplyIntentCommand.cs) would commit on that head. It prints the ExecutionPlan and never writes the
// project, the intent or anything else (callers commit through apply-intent themselves).
//   project.json  a Project (the head), read like diff-edits reads it: the default System.Text.Json deserializer, null
//                 elements, geometry or dependencies refused (diff_intent_project_invalid). A store directory's
//                 project.json is a {Project, Receipts} state, not a Project, and is refused the same way.
//   after.json    a canonical layout intent v1 (core/Bim.Core/IntentJson.cs), parsed by IntentJson.Parse (at most
//                 IntentJson.MaxInputChars characters, depth and list bounds, unknown and duplicate members refused).
// Bounded input: both files go through BoundedInput.ReadText (8 MiB cap, regular files only, links refused; its input_*
// codes); at most IncrementalDiff.MaxEdits edits (edit_bound_exceeded, checked after the compile as in apply-intent).
// Plan: exactly apply-intent's path, IntentEdits.FromStored(head, intent) and then
// IncrementalDiff.Diff(head, set.Edits, createOrder: IncrementalDiff.OrderOf(set.After)).Plan, so stdout is byte-equal to
// the "plan" member apply-intent prints on a store whose head is this project, for the same intent, in any head
// element order. It is also byte-identical to Compiler.Diff(head, compile(intent)) whenever the head is in compile order
// (it is after init from a compile and after every apply-intent); on a head in another order survivors keep the head's
// order, so the operation order and AfterHash can differ from that full diff. An intent that changes nothing prints an
// empty plan.
// Success: stdout is the plan JSON, exit 0. Every refusal exits 1:
//   {"status":"failed","code":c} for diff_intent_usage, input_* (project or intent), diff_intent_project_invalid,
//     invalid_ir (the head is not Compiler.Validate-clean, or the diff refuses the result), invalid_project_identity (the
//     head's project id is empty or its revision negative), edit_bound_exceeded, the diff's other library codes and
//     diff_intent_internal_error (a defect; stderr gets its type and message);
//   {"status":"failed","code":"diff_intent_intent_invalid","reason":r,"field":f} when IntentJson.Parse refuses the file
//     (r its IntentCodes code, f its JSON path or null);
//   {"status":"failed","code":"diff_intent_intent_refused","refusals":[{"code","subject","detail"}...]} when
//     IntentCompiler refuses the intent (every issue of its IntentException, subject being the issue's key).
// These are apply-intent's envelopes with the diff_intent_ prefix; diff-intent has no store, so it has no stale-head,
// lock or commit codes.
// Precedence: usage, then both files are read (project first), then the project is parsed, then the intent, then the
// compile and edit set, then the diff; the first failure is the only one reported.
internal static class DiffIntentCommand
{
    public const string Verb = "diff-intent";
    public const string UsageCode = "diff_intent_usage";
    public const string ProjectInvalid = "diff_intent_project_invalid";
    public const string IntentInvalid = "diff_intent_intent_invalid";
    public const string IntentRefused = "diff_intent_intent_refused";
    const string InvalidIdentity = "invalid_project_identity";

    public static void Run(string[] args)
    {
        if (!TryParse(args, out var projectPath, out var intentPath)) { ApplyEditsCommand.Fail(UsageCode); return; }
        string projectText, intentText;
        try
        {
            projectText = BoundedInput.ReadText(projectPath);
            intentText = BoundedInput.ReadText(intentPath);
        }
        catch (InputRejectedException error) { ApplyEditsCommand.Fail(error.Message); return; }
        if (ParseProject(projectText) is not { } head) { ApplyEditsCommand.Fail(ProjectInvalid); return; }
        LayoutIntent intent;
        try { intent = IntentJson.Parse(intentText); }
        catch (IntentException error)
        {
            Refuse(JsonSerializer.Serialize(new { status = "failed", code = IntentInvalid, reason = error.Code, field = error.Field }));
            return;
        }
        catch (Exception error) { ApplyEditsCommand.Internal(Verb, error); return; }
        IntentEditSet set;
        try { set = IntentEdits.FromStored(head, intent); }
        catch (IntentException error)
        {
            var issues = error.Issues.Count > 0 ? error.Issues : new[] { new IntentIssue(error.Code, error.Field ?? "", error.Detail ?? "") };
            Refuse(JsonSerializer.Serialize(new { status = "failed", code = IntentRefused, refusals = issues.Select(i => new { code = i.Code, subject = i.Key, detail = i.Detail }) }));
            return;
        }
        catch (ArgumentException error) when (error.Message is IncrementalDiff.BoundExceeded or InvalidIdentity) { ApplyEditsCommand.Fail(error.Message); return; }
        catch (InvalidOperationException error) when (error.Message == "invalid_ir") { ApplyEditsCommand.Fail("invalid_ir"); return; }
        catch (Exception error) { ApplyEditsCommand.Internal(Verb, error); return; }
        ExecutionPlan plan;
        try { plan = IncrementalDiff.Diff(head, set.Edits, createOrder: IncrementalDiff.OrderOf(set.After)).Plan; }
        catch (ArgumentException error) when (EditsInput.LibraryCodes.Contains(error.Message, StringComparer.Ordinal)) { ApplyEditsCommand.Fail(error.Message); return; }
        catch (InvalidOperationException error) when (error.Message == "invalid_ir") { ApplyEditsCommand.Fail("invalid_ir"); return; }
        catch (Exception error) { ApplyEditsCommand.Internal(Verb, error); return; }
        Console.WriteLine(JsonSerializer.Serialize(plan));
    }

    // Exactly --project <p> and --intent <i>, each once with a non-empty value that is not flag-shaped ("--..."), in
    // either order; nothing else.
    static bool TryParse(string[] args, out string projectPath, out string intentPath)
    {
        projectPath = ""; intentPath = "";
        if (args.Length != 5) return false;
        string? project = null, intent = null;
        for (var i = 1; i < args.Length; i += 2)
        {
            var value = args[i + 1];
            if (value.Length == 0 || value.StartsWith("--", StringComparison.Ordinal)) return false;
            if (args[i] == "--project" && project is null) project = value;
            else if (args[i] == "--intent" && intent is null) intent = value;
            else return false;
        }
        if (project is null || intent is null) return false;
        projectPath = project; intentPath = intent;
        return true;
    }

    // The diff-edits project reader (interfaces/cli/DiffEditsCommand.cs): Compiler.Validate, FromStored and the index
    // dereference these, so a null one is a malformed project, not a crash.
    static Project? ParseProject(string text)
    {
        Project? project;
        try { project = JsonSerializer.Deserialize<Project>(text); }
        catch (JsonException) { return null; }
        if (project?.Elements is null || project.Elements.Any(e => e is null || e.GeometryMetres is null || e.Dependencies is null)) return null;
        return project;
    }

    static void Refuse(string envelope)
    {
        Console.WriteLine(envelope);
        Environment.ExitCode = 1;
    }
}
