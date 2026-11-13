using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Bim.Cli;
using Bim.Core;

// P3-16: apply-edits (interfaces/cli/ApplyEditsCommand.cs) returns its stdout line, exit status and stderr detail as an
// IntentEnvelope (interfaces/cli/IntentPlan.cs) instead of writing Console or setting Environment.ExitCode; only Run
// prints it (IntentEnvelope.Print). The refusal JSON has one copy: ApplyEditsCommand.Fail / Internal are
// IntentPlan.Failed / Internal.
// Oracles, in process against the linked CLI sources and against the real Release CLI process:
//   1. Shape (by reflection, so the checks below report instead of failing to compile): Fail, Internal, Commit and
//      Execute return IntentEnvelope; Run returns void.
//   2. Fail(c) and Internal(verb, e) equal IntentPlan.Failed(c) / IntentPlan.Internal(verb, e) and write nothing.
//   3. Commit writes nothing to Console and leaves the exit code: success is the succeeded envelope (exit 0) and the
//      store moves to head + 1; a replayed commit is <verb>_stale_head; project.tmp a directory is
//      <verb>_commit_incomplete with the "<verb> commit incomplete: " stderr detail.
//   4. Execute(args) returns exactly the CLI process's stdout line and exit status (success, usage, input, store, edits,
//      stale head, library code, commit incomplete) and writes nothing; its diagnostic is the process's stderr.
//   5. Static: ApplyEditsCommand.cs code has no Console. or Environment.ExitCode access, Run is Execute(args).Print()
//      (its only print), and the failed/internal-error envelope JSON lives once, in IntentPlan.cs.
internal static class ApplyEditsEnvelopeTests
{
    static readonly Guid ProjectId = Guid.Parse("00000000-0000-0000-0000-0000000000b6");
    static readonly Provenance Source = new("cli apply-edits envelope fixture", "specified");
    const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    static LayoutIntent Base(double windowOffset = 1.0) => new(LayoutIntent.CurrentVersion, Source,
        new[] { new IntentLevel("l0", "Ground", 0) }, new[] { new IntentWallType("a", "A", 0.2, 2.8) },
        new[] { new IntentRoom("r0", "Room r0", "l0", "a", 0, 0, 4, 3), new IntentRoom("r1", "Room r1", "l0", "a", 4, 0, 3, 3) },
        new[] { new IntentOpening("w0", IntentOpeningKind.Window, new IntentRoomSide("r0", IntentSide.South), windowOffset, 1.2, 1.2, 0.9) });

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

    // Invokes ApplyEditsCommand.<name>(arguments) by reflection; the IntentEnvelope it returns, or null with why not.
    static (IntentEnvelope? Envelope, string Why) Invoke(string name, Type[] types, params object[] arguments)
    {
        var method = typeof(ApplyEditsCommand).GetMethod(name, Any, types);
        if (method is null) return (null, "ApplyEditsCommand." + name + " not found");
        try
        {
            var value = method.Invoke(null, arguments);
            return value is IntentEnvelope envelope ? (envelope, "") : (null, "ApplyEditsCommand." + name + " returned " + (method.ReturnType.FullName ?? "?") + ", not an IntentEnvelope");
        }
        catch (TargetInvocationException error) { return (null, "ApplyEditsCommand." + name + " threw " + error.InnerException?.GetType().FullName + ": " + error.InnerException?.Message); }
    }

    static readonly Type[] FailTypes = { typeof(string) };
    static readonly Type[] InternalTypes = { typeof(string), typeof(Exception) };
    static readonly Type[] CommitTypes = { typeof(string), typeof(IncrementalDiffResult), typeof(string) };
    static readonly Type[] ExecuteTypes = { typeof(string[]) };

    public static void Run(string root, string work, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "apply-edits envelope: " + name, detail);
        var scratch = Path.Combine(work, "apply-edits-envelope");
        Directory.CreateDirectory(scratch);
        (int Exit, string Out, string Err) Cli(params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(cliDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000)) { process.Kill(true); return (-1, "<timed out>", ""); }
            process.WaitForExit();
            static string Line(string text) { if (text.EndsWith('\n')) text = text[..^1]; if (text.EndsWith('\r')) text = text[..^1]; return text; }
            return (process.ExitCode, Line(stdout.Result), Line(stderr.Result));
        }
        var counter = 0;
        string Input(string name, string content)
        {
            var path = Path.Combine(scratch, (++counter).ToString(CultureInfo.InvariantCulture) + "-" + name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }
        var head = IntentCompiler.Compile(ProjectId, 0, Base());
        string NewStore()
        {
            var directory = Path.Combine(scratch, "store-" + (++counter).ToString(CultureInfo.InvariantCulture));
            new ProjectStore(directory).Initialize(head);
            return directory;
        }
        static string FailedCode(string code) => JsonSerializer.Serialize(new { status = "failed", code });
        static string E(IntentEnvelope? e) => e is null ? "<null>" : "exit " + e.Exit.ToString(CultureInfo.InvariantCulture) + " line " + (e.Line.Length > 300 ? e.Line[..300] + "..." : e.Line) + " diagnostic " + (e.Diagnostic ?? "<null>");
        static string Q<T>((T Value, string Out, string Err, int Exit) q) => "wrote out [" + q.Out + "] err [" + q.Err + "] exit " + q.Exit.ToString(CultureInfo.InvariantCulture);
        static bool Silent<T>((T Value, string Out, string Err, int Exit) q) => q.Out.Length == 0 && q.Err.Length == 0 && q.Exit == 0;

        // 1. Shape.
        foreach (var (name, types) in new[] { ("Fail", FailTypes), ("Internal", InternalTypes), ("Commit", CommitTypes), ("Execute", ExecuteTypes) })
        {
            var method = typeof(ApplyEditsCommand).GetMethod(name, Any, types);
            Check(method is not null && method.ReturnType == typeof(IntentEnvelope), "ApplyEditsCommand." + name + " returns IntentEnvelope", method?.ReturnType.FullName ?? "<missing>");
        }
        Check(typeof(ApplyEditsCommand).GetMethod("Run", Any, ExecuteTypes)?.ReturnType == typeof(void), "ApplyEditsCommand.Run(string[]) is the void CLI entry point");

        // 2. Fail and Internal are IntentPlan.Failed and IntentPlan.Internal, written nowhere.
        foreach (var code in new[] { ApplyEditsCommand.UsageCode, "input_missing", "apply_intent_store_invalid", "invalid_ir" })
        {
            var q = Quiet(() => Invoke("Fail", FailTypes, code));
            Check(q.Value.Envelope == IntentPlan.Failed(code) && q.Value.Envelope?.Line == FailedCode(code), "Fail(" + code + ") returns IntentPlan.Failed(" + code + ")", q.Value.Why + E(q.Value.Envelope));
            Check(Silent(q), "Fail(" + code + ") writes nothing and leaves the exit code", Q(q));
        }
        foreach (var verb in new[] { ApplyEditsCommand.Verb, "apply-intent" })
        {
            var failure = new InvalidOperationException("boom " + verb);
            var q = Quiet(() => Invoke("Internal", InternalTypes, verb, failure));
            Check(q.Value.Envelope == IntentPlan.Internal(verb, failure)
                && q.Value.Envelope?.Diagnostic == verb + " internal error: System.InvalidOperationException: boom " + verb,
                "Internal(" + verb + ") returns IntentPlan.Internal with the stderr detail", q.Value.Why + E(q.Value.Envelope));
            Check(Silent(q), "Internal(" + verb + ") writes nothing and leaves the exit code", Q(q));
            if (q.Value.Envelope is { } envelope)
            {
                var shown = Quiet(() => { envelope.Print(); return 0; });
                Check(shown.Out == FailedCode(verb.Replace('-', '_') + "_internal_error") + Environment.NewLine && shown.Err == verb + " internal error: System.InvalidOperationException: boom " + verb + Environment.NewLine && shown.Exit == 1,
                    "Internal(" + verb + ").Print() writes the internal_error line, the stderr detail and exit 1", Q(shown));
            }
        }

        // 3. Commit returns its envelope.
        var moved = IntentEdits.FromStored(head, Base(1.5));
        {
            var store = NewStore();
            var result = IncrementalDiff.Diff(head, moved.Edits);
            var expected = JsonSerializer.Serialize(new { status = "succeeded", scope = "intended_state_only", native_verification = "unverified", revision = 1, plan = result.Plan });
            var q = Quiet(() => Invoke("Commit", CommitTypes, store, result, ApplyEditsCommand.Verb));
            Check(q.Value.Envelope is { Exit: 0, Diagnostic: null, Succeeded: true } ok && ok.Line == expected, "Commit returns the succeeded envelope with exit 0", q.Value.Why + E(q.Value.Envelope) + " expected " + expected);
            Check(Silent(q), "Commit success writes nothing to Console and leaves the exit code", Q(q));
            Check(new ProjectStore(store).Read().Revision == 1, "Commit success moved the store to revision 1");
            foreach (var verb in new[] { ApplyEditsCommand.Verb, "apply-intent" })
            {
                var replay = Quiet(() => Invoke("Commit", CommitTypes, store, result, verb));
                Check(replay.Value.Envelope == IntentPlan.Failed(verb.Replace('-', '_') + "_stale_head"), "a replayed Commit (" + verb + ") returns the stale_head envelope", replay.Value.Why + E(replay.Value.Envelope));
                Check(Silent(replay), "a replayed Commit (" + verb + ") writes nothing and leaves the exit code", Q(replay));
            }
            Check(new ProjectStore(store).Read().Revision == 1, "a replayed Commit leaves the store at revision 1");
        }
        {
            var broken = NewStore();
            Directory.CreateDirectory(Path.Combine(broken, "project.tmp"));
            var result = IncrementalDiff.Diff(head, moved.Edits);
            var q = Quiet(() => Invoke("Commit", CommitTypes, broken, result, ApplyEditsCommand.Verb));
            Check(q.Value.Envelope is { Exit: 1 } incomplete && incomplete.Line == FailedCode("apply_edits_commit_incomplete") && incomplete.Diagnostic is { } d && d.StartsWith("apply-edits commit incomplete: System.", StringComparison.Ordinal),
                "Commit failing after it started writing returns apply_edits_commit_incomplete with the stderr detail", q.Value.Why + E(q.Value.Envelope));
            Check(Silent(q), "Commit incomplete writes nothing to Console and leaves the exit code", Q(q));
        }

        // 4. Execute equals the CLI process.
        {
            var editsPath = Input("moved.edits.json", JsonSerializer.Serialize(moved.Edits));
            var unknown = new[] { ElementEdit.Delete(Guid.Parse("00000000-0000-0000-0000-0000000000ff")) };
            string libraryCode;
            try { IncrementalDiff.Diff(head, unknown); libraryCode = "<accepted>"; }
            catch (ArgumentException error) { libraryCode = error.Message; }
            Check(libraryCode == "edit_unknown_id", "fixture: the library refuses a delete of an unknown id as edit_unknown_id", libraryCode);
            var unknownPath = Input("unknown.edits.json", JsonSerializer.Serialize(unknown));
            var badEdits = Input("bad.edits.json", "{\"edits\":");
            // Each case runs on two identical stores: one in process, one through the CLI.
            var cases = new (string Name, Func<string, string> Prepare, Func<string, string[]> Args)[]
            {
                ("a commit", s => s, s => new[] { "--store", s, "--edits", editsPath }),
                ("a commit with --expected-revision 0", s => s, s => new[] { "--expected-revision", "0", "--store", s, "--edits", editsPath }),
                ("usage", s => s, s => new[] { "--store", s }),
                ("a missing edits file", s => s, s => new[] { "--store", s, "--edits", Path.Combine(scratch, "missing.edits.json") }),
                ("an invalid store", s => { File.WriteAllText(Path.Combine(s, "project.json"), "{\"Project\":"); return s; }, s => new[] { "--store", s, "--edits", editsPath }),
                ("invalid edits", s => s, s => new[] { "--store", s, "--edits", badEdits }),
                ("a stale --expected-revision", s => s, s => new[] { "--store", s, "--edits", editsPath, "--expected-revision", "5" }),
                ("a library refusal", s => s, s => new[] { "--store", s, "--edits", unknownPath }),
                ("an incomplete commit", s => { Directory.CreateDirectory(Path.Combine(s, "project.tmp")); return s; }, s => new[] { "--store", s, "--edits", editsPath }),
            };
            foreach (var (name, prepare, args) in cases)
            {
                var inProcess = prepare(NewStore());
                var viaCli = prepare(NewStore());
                var q = Quiet(() => Invoke("Execute", ExecuteTypes, (object)new[] { ApplyEditsCommand.Verb }.Concat(args(inProcess)).ToArray()));
                var cli = Cli(new[] { ApplyEditsCommand.Verb }.Concat(args(viaCli)).ToArray());
                var envelope = q.Value.Envelope;
                Check(envelope is not null && envelope.Line == cli.Out && envelope.Exit == cli.Exit, "Execute returns the CLI stdout line and exit (" + name + ")",
                    q.Value.Why + E(envelope) + " vs CLI exit " + cli.Exit.ToString(CultureInfo.InvariantCulture) + " " + (cli.Out.Length > 300 ? cli.Out[..300] + "..." : cli.Out));
                var sameDiagnostic = name == "an incomplete commit"
                    ? envelope?.Diagnostic is { } d && d.StartsWith("apply-edits commit incomplete: ", StringComparison.Ordinal) && cli.Err.StartsWith("apply-edits commit incomplete: ", StringComparison.Ordinal)
                    : envelope is not null && envelope.Diagnostic is null && cli.Err.Length == 0;
                Check(sameDiagnostic, "Execute's stderr detail matches the CLI stderr (" + name + ")", E(envelope) + " vs CLI stderr [" + cli.Err + "]");
                Check(Silent(q), "Execute writes nothing and leaves the exit code (" + name + ")", Q(q));
            }
        }

        // 5. Static.
        var sourcePath = Path.Combine(root, "interfaces", "cli", "ApplyEditsCommand.cs");
        var helperPath = Path.Combine(root, "interfaces", "cli", "IntentPlan.cs");
        var sourceText = File.ReadAllText(sourcePath);
        var helperText = File.ReadAllText(helperPath);
        var code = ApplyIntentTests.CodeOf(sourceText);
        var found = new[] { "Console.", "Environment.ExitCode" }.Where(api => code.Contains(api, StringComparison.Ordinal)).ToArray();
        Check(found.Length == 0, "ApplyEditsCommand.cs code has no Console or exit-code access (it prints only through IntentEnvelope.Print)", "found [" + string.Join(", ", found) + "]");
        var prints = code.Split(".Print(").Length - 1;
        Check(code.Contains("Execute(args).Print()", StringComparison.Ordinal) && prints == 1, "ApplyEditsCommand.Run is Execute(args).Print(), its only print", "prints " + prints.ToString(CultureInfo.InvariantCulture));
        Check(code.Contains("IntentPlan.Failed(", StringComparison.Ordinal) && code.Contains("IntentPlan.Internal(", StringComparison.Ordinal), "ApplyEditsCommand.Fail / Internal delegate to IntentPlan.Failed / Internal");
        static int Count(string text, string what) => text.Split(what).Length - 1;
        var literalSource = ApplyIntentTests.CodeOf(sourceText, blankStrings: false);
        var literalHelper = ApplyIntentTests.CodeOf(helperText, blankStrings: false);
        foreach (var copy in new[] { "status = \"failed\", code }", "\"_internal_error\"", "\" internal error: \"" })
            Check(Count(literalSource, copy) == 0 && Count(literalHelper, copy) == 1, "the envelope JSON piece " + copy + " has one copy, in IntentPlan.cs",
                "ApplyEditsCommand.cs " + Count(literalSource, copy).ToString(CultureInfo.InvariantCulture) + ", IntentPlan.cs " + Count(literalHelper, copy).ToString(CultureInfo.InvariantCulture));
    }
}
