using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Bim.Cli;
using Bim.Core;

// P3-15: the shared intent plan helper (interfaces/cli/IntentPlan.cs) returns its stdout line and exit status
// (IntentEnvelope) instead of writing Console.Out or setting Environment.ExitCode; the CLI verbs print it
// (IntentEnvelope.Print) and the MCP diff_intent tool returns it directly (DiffIntentCommand.Plan).
// Oracles, in process against the linked CLI sources and against the real Release CLI process:
//   1. IntentPlan.ParseIntent, IntentPlan.Build, IntentPlan.Failed and IntentPlan.Internal write nothing to stdout or
//      stderr and leave Environment.ExitCode unchanged; their envelopes are the bytes the verbs print (the IntentJson.Parse
//      and IntentCompiler refusals, the library codes, and exactly ApplyEditsCommand.Fail / Internal's stdout, stderr and
//      exit for the same code or exception).
//   2. IntentEnvelope.Print writes the line (and the stderr detail of an internal error) and sets exit 1 on a refusal,
//      leaving the exit code alone on success, as ApplyEditsCommand.Fail and Console.WriteLine did.
//   3. DiffIntentCommand.Plan returns, without writing anything, exactly the stdout line and exit status the CLI
//      diff-intent process produces for the same arguments (success, usage, input, project and intent refusals, and the library code invalid_project_identity).
//   4. Static: the IntentPlan class has no Console or Environment.ExitCode access; DiffIntentCommand.Run is
//      Plan(args).Print() and is its only Console write.
internal static class IntentPlanTests
{
    static readonly Guid ProjectId = Guid.Parse("00000000-0000-0000-0000-0000000000b5");
    static readonly Provenance Source = new("cli intent plan envelope fixture", "specified");

    static LayoutIntent Base(double windowOffset = 1.0) => new(LayoutIntent.CurrentVersion, Source,
        new[] { new IntentLevel("l0", "Ground", 0) }, new[] { new IntentWallType("a", "A", 0.2, 2.8) },
        new[] { new IntentRoom("r0", "Room r0", "l0", "a", 0, 0, 4, 3), new IntentRoom("r1", "Room r1", "l0", "a", 4, 0, 3, 3) },
        new[] { new IntentOpening("w0", IntentOpeningKind.Window, new IntentRoomSide("r0", IntentSide.South), windowOffset, 1.2, 1.2, 0.9) });

    // Runs `run` with Console.Out, Console.Error and Environment.ExitCode isolated and restored, and returns what it wrote.
    static (T Value, string Out, string Err, int Exit) Quiet<T>(Func<T> run)
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var previousExit = Environment.ExitCode;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            Environment.ExitCode = 0;
            var value = run();
            return (value, output.ToString(), error.ToString(), Environment.ExitCode);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.ExitCode = previousExit;
        }
    }

    public static void Run(string root, string work, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "intent-plan envelope: " + name, detail);
        var scratch = Path.Combine(work, "intent-plan-envelope");
        Directory.CreateDirectory(scratch);
        (int Exit, string Out) Cli(params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(cliDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000)) { process.Kill(true); return (-1, "<timed out>"); }
            process.WaitForExit();
            _ = stderr.Result;
            var text = stdout.Result;
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];
            return (process.ExitCode, text);
        }
        var counter = 0;
        string Input(string name, string content)
        {
            var path = Path.Combine(scratch, (++counter).ToString(CultureInfo.InvariantCulture) + "-" + name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }
        static string FailedCode(string code) => JsonSerializer.Serialize(new { status = "failed", code });
        static string E(IntentEnvelope? e) => e is null ? "<null>" : "exit " + e.Exit.ToString(CultureInfo.InvariantCulture) + " line " + e.Line + " diagnostic " + (e.Diagnostic ?? "<null>");
        static string Q<T>((T Value, string Out, string Err, int Exit) q) => "wrote out [" + q.Out + "] err [" + q.Err + "] exit " + q.Exit.ToString(CultureInfo.InvariantCulture);
        static bool Silent<T>((T Value, string Out, string Err, int Exit) q) => q.Out.Length == 0 && q.Err.Length == 0 && q.Exit == 0;

        var head = IntentCompiler.Compile(ProjectId, 0, Base());
        var moved = Base(1.5);

        // 1. ParseIntent: a refusal is the diff_intent_intent_invalid envelope, nothing written.
        {
            var text = "{\"version\":";
            string expected;
            try { IntentJson.Parse(text); expected = "<accepted>"; }
            catch (IntentException error) { expected = JsonSerializer.Serialize(new { status = "failed", code = "diff_intent_intent_invalid", reason = error.Code, field = error.Field }); }
            var q = Quiet(() => IntentPlan.ParseIntent(text, DiffIntentCommand.Verb, DiffIntentCommand.IntentInvalid));
            var ok = q.Value.TryGet(out var parsed, out var refusal);
            Check(!ok && parsed is null && refusal is not null && refusal.Line == expected && refusal.Exit == 1 && refusal.Diagnostic is null && !refusal.Succeeded,
                "ParseIntent refuses invalid JSON with the intent_invalid envelope and exit 1", E(refusal) + " expected " + expected);
            Check(Silent(q), "ParseIntent refusal writes nothing and leaves the exit code", Q(q));
            var validText = IntentJson.Serialize(moved);
            var good = Quiet(() => IntentPlan.ParseIntent(validText, DiffIntentCommand.Verb, DiffIntentCommand.IntentInvalid));
            Check(good.Value.TryGet(out var intent, out var none) && none is null && IntentJson.Serialize(intent) == validText, "ParseIntent returns the parsed intent");
            Check(Silent(good), "ParseIntent success writes nothing and leaves the exit code", Q(good));
        }

        // 2. Build: success, compiler refusal and library refusal envelopes, nothing written.
        {
            var set = IntentEdits.FromStored(head, moved);
            var library = JsonSerializer.Serialize(IncrementalDiff.Diff(head, set.Edits, createOrder: IncrementalDiff.OrderOf(set.After)).Plan);
            var q = Quiet(() => IntentPlan.Build(head, moved, DiffIntentCommand.Verb, DiffIntentCommand.IntentRefused));
            Check(q.Value.TryGet(out var result, out var none) && none is null && JsonSerializer.Serialize(result.Plan) == library, "Build returns the library plan");
            Check(Silent(q), "Build success writes nothing and leaves the exit code", Q(q));

            var refusedIntent = Base(10.5);
            string expected;
            try { IntentCompiler.Compile(head.Id, head.Revision, refusedIntent); expected = "<accepted>"; }
            catch (IntentException error) { expected = JsonSerializer.Serialize(new { status = "failed", code = "diff_intent_intent_refused", refusals = error.Issues.Select(i => new { code = i.Code, subject = i.Key, detail = i.Detail }) }); }
            Check(expected != "<accepted>", "fixture: IntentCompiler refuses a window beyond its wall");
            var refused = Quiet(() => IntentPlan.Build(head, refusedIntent, DiffIntentCommand.Verb, DiffIntentCommand.IntentRefused));
            Check(!refused.Value.TryGet(out var nothing, out var refusal) && nothing is null && refusal.Line == expected && refusal.Exit == 1 && refusal.Diagnostic is null,
                "Build refuses a compiler-refused intent with the intent_refused envelope and exit 1", E(refusal) + " expected " + expected);
            Check(Silent(refused), "Build compiler refusal writes nothing and leaves the exit code", Q(refused));

            var anonymous = new Project(Guid.Empty, 0, Array.Empty<Element>());
            var identity = Quiet(() => IntentPlan.Build(anonymous, moved, DiffIntentCommand.Verb, DiffIntentCommand.IntentRefused));
            Check(!identity.Value.TryGet(out _, out var identityRefusal) && identityRefusal.Line == FailedCode("invalid_project_identity") && identityRefusal.Exit == 1,
                "Build refuses an empty project id with invalid_project_identity and exit 1", E(identityRefusal));
            Check(Silent(identity), "Build library refusal writes nothing and leaves the exit code", Q(identity));
        }

        // 3. Failed and Internal: exactly ApplyEditsCommand.Fail / Internal's bytes, returned instead of written.
        foreach (var code in new[] { "diff_intent_usage", "input_missing", "edit_bound_exceeded" })
        {
            var q = Quiet(() => IntentPlan.Failed(code));
            var printed = Quiet(() => { ApplyEditsCommand.Fail(code); return 0; });
            Check(q.Value.Line == FailedCode(code) && q.Value.Exit == 1 && q.Value.Diagnostic is null && Silent(q), "Failed(" + code + ") returns the failed envelope and writes nothing", E(q.Value) + " " + Q(q));
            Check(printed.Out == q.Value.Line + Environment.NewLine && printed.Err.Length == 0 && printed.Exit == q.Value.Exit, "Failed(" + code + ") is byte-equal to ApplyEditsCommand.Fail", Q(printed));
            var shown = Quiet(() => { q.Value.Print(); return 0; });
            Check(shown.Out == printed.Out && shown.Err == printed.Err && shown.Exit == printed.Exit, "Failed(" + code + ").Print() prints what ApplyEditsCommand.Fail printed", Q(shown));
        }
        foreach (var verb in new[] { DiffIntentCommand.Verb, "apply-intent" })
        {
            var failure = new InvalidOperationException("boom " + verb);
            var q = Quiet(() => IntentPlan.Internal(verb, failure));
            var printed = Quiet(() => { ApplyEditsCommand.Internal(verb, failure); return 0; });
            Check(q.Value.Line == FailedCode(verb.Replace('-', '_') + "_internal_error") && q.Value.Exit == 1 && Silent(q), "Internal(" + verb + ") returns the internal_error envelope and writes nothing", E(q.Value) + " " + Q(q));
            Check(q.Value.Diagnostic == verb + " internal error: System.InvalidOperationException: boom " + verb, "Internal(" + verb + ") carries the stderr detail", E(q.Value));
            var shown = Quiet(() => { q.Value.Print(); return 0; });
            Check(shown.Out == printed.Out && shown.Err == printed.Err && shown.Exit == printed.Exit && printed.Exit == 1, "Internal(" + verb + ").Print() prints what ApplyEditsCommand.Internal printed (stdout, stderr, exit)", Q(shown) + " vs " + Q(printed));
        }
        {
            var success = new IntentEnvelope("{\"plan\":1}", 0);
            var shown = Quiet(() => { success.Print(); return 0; });
            Check(success.Succeeded && shown.Out == success.Line + Environment.NewLine && shown.Err.Length == 0 && shown.Exit == 0, "a success envelope prints its line and leaves the exit code", Q(shown));
        }

        // 4. DiffIntentCommand.Plan: the CLI process's stdout line and exit status, returned without writing anything.
        {
            var projectPath = Input("head.project.json", JsonSerializer.Serialize(head));
            var cases = new (string Name, string[] Args)[]
            {
                ("a plan", new[] { "--project", projectPath, "--intent", Input("moved.intent.json", IntentJson.Serialize(moved)) }),
                ("usage", new[] { "--project", projectPath }),
                ("a missing intent file", new[] { "--project", projectPath, "--intent", Path.Combine(scratch, "missing.intent.json") }),
                ("an invalid project", new[] { "--project", Input("bad.project.json", "{\"Id\":"), "--intent", Input("ok.intent.json", IntentJson.Serialize(moved)) }),
                ("an invalid intent", new[] { "--project", projectPath, "--intent", Input("bad.intent.json", "{\"version\":") }),
                ("a refused intent", new[] { "--project", projectPath, "--intent", Input("refused.intent.json", IntentJson.Serialize(Base(10.5))) }),
                ("invalid_project_identity", new[] { "--project", Input("anonymous.project.json", JsonSerializer.Serialize(new Project(Guid.Empty, 0, Array.Empty<Element>()))), "--intent", Input("on-anonymous.intent.json", IntentJson.Serialize(moved)) }),
            };
            foreach (var (name, args) in cases)
            {
                var full = new[] { DiffIntentCommand.Verb }.Concat(args).ToArray();
                var q = Quiet(() => DiffIntentCommand.Plan(full));
                var cli = Cli(full);
                if (name == "invalid_project_identity") Check(cli.Exit == 1 && cli.Out == FailedCode("invalid_project_identity"), "fixture: the CLI refuses an empty project id with the library code invalid_project_identity", "exit " + cli.Exit.ToString(CultureInfo.InvariantCulture) + " " + cli.Out);
                Check(q.Value.Line == cli.Out && q.Value.Exit == cli.Exit, "DiffIntentCommand.Plan returns the CLI stdout line and exit (" + name + ")", E(q.Value) + " vs CLI exit " + cli.Exit.ToString(CultureInfo.InvariantCulture) + " " + cli.Out);
                Check(Silent(q), "DiffIntentCommand.Plan writes nothing and leaves the exit code (" + name + ")", Q(q));
            }
        }

        // 5. Static: no Console or exit-code access in the IntentPlan class; DiffIntentCommand prints only in Run.
        var helperPath = Path.Combine(root, "interfaces", "cli", "IntentPlan.cs");
        var helper = ApplyIntentTests.CodeOf(File.ReadAllText(helperPath));
        var start = helper.IndexOf("static class IntentPlan", StringComparison.Ordinal);
        var end = helper.IndexOf("record IntentEnvelope", StringComparison.Ordinal);
        Check(start >= 0 && end > start, "IntentPlan.cs declares the IntentPlan class before IntentEnvelope", start.ToString(CultureInfo.InvariantCulture) + ".." + end.ToString(CultureInfo.InvariantCulture));
        if (start >= 0 && end > start)
        {
            var body = helper[start..end];
            var found = new[] { "Console.", "Environment.ExitCode", "ApplyEditsCommand.", ".Print(" }.Where(api => body.Contains(api, StringComparison.Ordinal)).ToArray();
            Check(found.Length == 0, "the IntentPlan class writes no Console, sets no exit code and prints nothing itself", "found [" + string.Join(", ", found) + "]");
        }
        var command = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "DiffIntentCommand.cs")));
        Check(command.Contains("Plan(args).Print()", StringComparison.Ordinal) && !command.Contains("Console.", StringComparison.Ordinal) && !command.Contains("Environment.ExitCode", StringComparison.Ordinal)
            && !command.Contains("ApplyEditsCommand.Fail(", StringComparison.Ordinal),
            "DiffIntentCommand.Run prints Plan(args) and has no other Console or exit-code access");
    }
}
