using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Bim.Cli;
using Bim.Core;

// P3-17: apply-intent (interfaces/cli/ApplyIntentCommand.cs) and diff-edits (interfaces/cli/DiffEditsCommand.cs) print
// through IntentEnvelope (interfaces/cli/IntentPlan.cs). ApplyIntentCommand.Plan(args) returns the one envelope the verb
// ends with and Run is Plan(args).Print(); DiffEditsCommand.Fail delegates to IntentPlan.Failed, so the failed-envelope
// JSON has one copy among the intent/edits verbs of interfaces/cli.
// Oracles, in process against the linked CLI sources and against the real Release CLI process:
//   1. Shape (by reflection, so the checks below report instead of failing to compile): ApplyIntentCommand.Plan(string[])
//      returns IntentEnvelope; ApplyIntentCommand.Run(string[]) returns void.
//   2. Plan(args) returns exactly the CLI process's stdout line, exit status and stderr (success, usage, a missing input,
//      an invalid store, an invalid intent, a stale --expected-revision, a compiler refusal, an incomplete commit), writes
//      nothing to Console and leaves Environment.ExitCode; the in-process store moves exactly as the CLI's.
//   3. DiffEditsCommand.Fail(c) prints IntentPlan.Failed(c) (its line, newline, exit 1, no stderr): the bytes it printed
//      before delegating.
//   4. Static: ApplyIntentCommand.cs code has no Console. or Environment.ExitCode access and Run is Plan(args).Print(), its
//      only print; DiffEditsCommand.Fail calls IntentPlan.Failed; the failed-envelope fragment `status = "failed", code }`
//      occurs once over the interfaces/cli intent/edits sources (every file that names IntentPlan or IntentEnvelope, plus
//      the four verbs and EditsInput.cs), in IntentPlan.cs.
internal static class ApplyIntentEnvelopeTests
{
    static readonly Guid ProjectId = Guid.Parse("00000000-0000-0000-0000-0000000000b7");
    static readonly Provenance Source = new("cli apply-intent envelope fixture", "specified");
    const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly Type[] ArgsTypes = { typeof(string[]) };

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

    public static void Run(string root, string work, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "apply-intent/diff-edits envelope: " + name, detail);
        var scratch = Path.Combine(work, "apply-intent-envelope");
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
            return (process.ExitCode, stdout.Result, stderr.Result);
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
        static string Cut(string text) => text.Length > 300 ? text[..300] + "..." : text;
        static string E(IntentEnvelope? e) => e is null ? "<null>" : "exit " + e.Exit.ToString(CultureInfo.InvariantCulture) + " line " + Cut(e.Line) + " diagnostic " + (e.Diagnostic ?? "<null>");
        static string Q<T>((T Value, string Out, string Err, int Exit) q) => "wrote out [" + q.Out + "] err [" + q.Err + "] exit " + q.Exit.ToString(CultureInfo.InvariantCulture);
        static bool Silent<T>((T Value, string Out, string Err, int Exit) q) => q.Out.Length == 0 && q.Err.Length == 0 && q.Exit == 0;

        // 1. Shape.
        var plan = typeof(ApplyIntentCommand).GetMethod("Plan", Any, ArgsTypes);
        Check(plan is not null && plan.ReturnType == typeof(IntentEnvelope), "ApplyIntentCommand.Plan(string[]) returns IntentEnvelope", plan?.ReturnType.FullName ?? "<missing>");
        Check(typeof(ApplyIntentCommand).GetMethod("Run", Any, ArgsTypes)?.ReturnType == typeof(void), "ApplyIntentCommand.Run(string[]) is the void CLI entry point");

        // 2. Plan equals the CLI process.
        {
            var moved = IntentJson.Serialize(Base(1.5));
            var movedPath = Input("moved.intent.json", moved);
            var refusedPath = Input("refused.intent.json", IntentJson.Serialize(Base(10.5)));
            string compileRefused;
            try { IntentCompiler.Compile(ProjectId, 0, Base(10.5)); compileRefused = "<accepted>"; }
            catch (IntentException error) { compileRefused = error.Code; }
            Check(compileRefused != "<accepted>", "fixture: the compiler refuses a window beyond its wall", compileRefused);
            var badIntent = Input("bad.intent.json", "{\"version\":");
            var cases = new (string Name, Func<string, string> Prepare, Func<string, string[]> Args)[]
            {
                ("a commit", s => s, s => new[] { "--store", s, "--intent", movedPath }),
                ("a commit with --expected-revision 0", s => s, s => new[] { "--expected-revision", "0", "--store", s, "--intent", movedPath }),
                ("usage", s => s, s => new[] { "--store", s }),
                ("a missing intent file", s => s, s => new[] { "--store", s, "--intent", Path.Combine(scratch, "missing.intent.json") }),
                ("an invalid store", s => { File.WriteAllText(Path.Combine(s, "project.json"), "{\"Project\":"); return s; }, s => new[] { "--store", s, "--intent", movedPath }),
                ("an invalid intent", s => s, s => new[] { "--store", s, "--intent", badIntent }),
                ("a stale --expected-revision", s => s, s => new[] { "--store", s, "--intent", movedPath, "--expected-revision", "5" }),
                ("a compiler refusal", s => s, s => new[] { "--store", s, "--intent", refusedPath }),
                ("an incomplete commit", s => { Directory.CreateDirectory(Path.Combine(s, "project.tmp")); return s; }, s => new[] { "--store", s, "--intent", movedPath }),
            };
            foreach (var (name, prepare, args) in cases)
            {
                var inProcess = prepare(NewStore());
                var viaCli = prepare(NewStore());
                IntentEnvelope? envelope = null;
                var why = "";
                var q = Quiet(() =>
                {
                    if (plan is null) { why = "ApplyIntentCommand.Plan not found; "; return 0; }
                    try { envelope = plan.Invoke(null, new object[] { new[] { ApplyIntentCommand.Verb }.Concat(args(inProcess)).ToArray() }) as IntentEnvelope; }
                    catch (TargetInvocationException error) { why = "Plan threw " + error.InnerException?.GetType().FullName + ": " + error.InnerException?.Message + "; "; }
                    return 0;
                });
                var cli = Cli(new[] { ApplyIntentCommand.Verb }.Concat(args(viaCli)).ToArray());
                // What Print writes for the envelope; the stderr detail names the store's project.tmp, so each side is
                // compared with its own store as <store>.
                var printed = envelope is null ? ((string Diagnostic, string Out)?)null : (Diagnostic: envelope.Diagnostic is { } d ? d + Environment.NewLine : "", Out: envelope.Line + Environment.NewLine);
                Check(envelope is not null && printed?.Out == cli.Out && envelope.Exit == cli.Exit, "Plan returns the CLI stdout bytes and exit (" + name + ")",
                    why + E(envelope) + " vs CLI exit " + cli.Exit.ToString(CultureInfo.InvariantCulture) + " " + Cut(cli.Out));
                var inProcessErr = printed?.Diagnostic.Replace(inProcess, "<store>", StringComparison.Ordinal);
                var cliErr = cli.Err.Replace(viaCli, "<store>", StringComparison.Ordinal);
                var expectsErr = name == "an incomplete commit";
                Check(inProcessErr is not null && inProcessErr == cliErr && (expectsErr ? cliErr.StartsWith("apply-intent commit incomplete: System.", StringComparison.Ordinal) : cliErr.Length == 0),
                    "Plan's stderr detail equals the CLI stderr bytes (" + name + ")", "in process [" + (inProcessErr ?? "<null>") + "] vs CLI stderr [" + cliErr + "]");
                Check(Silent(q), "Plan writes nothing and leaves the exit code (" + name + ")", Q(q));
                if (name.StartsWith("a commit", StringComparison.Ordinal))
                {
                    var revisions = (new ProjectStore(inProcess).Read().Revision, new ProjectStore(viaCli).Read().Revision);
                    Check(revisions == (1, 1), "Plan commits like the CLI (" + name + ")", "in process " + revisions.Item1.ToString(CultureInfo.InvariantCulture) + ", CLI " + revisions.Item2.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        // 3. DiffEditsCommand.Fail prints IntentPlan.Failed's bytes.
        var fail = typeof(DiffEditsCommand).GetMethod("Fail", Any, new[] { typeof(string) });
        Check(fail is not null, "DiffEditsCommand.Fail(string) exists");
        if (fail is not null)
            foreach (var code in new[] { DiffEditsCommand.UsageCode, "input_not_found", DiffEditsCommand.ProjectInvalid, "edit_unknown_id", DiffEditsCommand.InternalError })
            {
                var expected = IntentPlan.Failed(code);
                var q = Quiet(() => { fail!.Invoke(null, new object[] { code }); return 0; });
                Check(q.Out == JsonSerializer.Serialize(new { status = "failed", code }) + Environment.NewLine && q.Out == expected.Line + Environment.NewLine && q.Err.Length == 0 && q.Exit == expected.Exit && q.Exit == 1,
                    "DiffEditsCommand.Fail(" + code + ") prints IntentPlan.Failed(" + code + ") and exits 1", Q(q));
            }

        // 4. Static.
        var cliDir = Path.Combine(root, "interfaces", "cli");
        var applyIntent = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(cliDir, "ApplyIntentCommand.cs")));
        var found = new[] { "Console.", "Environment.ExitCode" }.Where(api => applyIntent.Contains(api, StringComparison.Ordinal)).ToArray();
        Check(found.Length == 0, "ApplyIntentCommand.cs code has no Console or exit-code access (it prints only through IntentEnvelope.Print)", "found [" + string.Join(", ", found) + "]");
        var prints = applyIntent.Split(".Print(").Length - 1;
        Check(applyIntent.Contains("Plan(args).Print()", StringComparison.Ordinal) && prints == 1, "ApplyIntentCommand.Run is Plan(args).Print(), its only print", "prints " + prints.ToString(CultureInfo.InvariantCulture));
        var diffEdits = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(cliDir, "DiffEditsCommand.cs")));
        var failAt = diffEdits.IndexOf("static void Fail(string code)", StringComparison.Ordinal);
        var failBody = failAt < 0 ? "" : diffEdits[failAt..Math.Min(diffEdits.Length, failAt + 200)];
        Check(failBody.Contains("IntentPlan.Failed(code)", StringComparison.Ordinal), "DiffEditsCommand.Fail delegates to IntentPlan.Failed", Cut(failBody));
        const string Fragment = "status = \"failed\", code }";
        var family = new[] { "ApplyEditsCommand.cs", "ApplyIntentCommand.cs", "DiffEditsCommand.cs", "DiffIntentCommand.cs", "EditsInput.cs", "IntentPlan.cs" };
        var counts = Directory.GetFiles(cliDir, "*.cs")
            .Select(f => (Name: Path.GetFileName(f), Code: ApplyIntentTests.CodeOf(File.ReadAllText(f), blankStrings: false)))
            .Where(f => family.Contains(f.Name, StringComparer.Ordinal) || f.Code.Contains("IntentPlan", StringComparison.Ordinal) || f.Code.Contains("IntentEnvelope", StringComparison.Ordinal))
            .Select(f => (f.Name, Count: f.Code.Split(Fragment).Length - 1)).Where(f => f.Count > 0).ToArray();
        Check(counts.Length == 1 && counts[0] == ("IntentPlan.cs", 1), "the failed-envelope JSON " + Fragment + " has one copy over the interfaces/cli intent/edits sources, in IntentPlan.cs",
            string.Join(", ", counts.Select(c => c.Name + " " + c.Count.ToString(CultureInfo.InvariantCulture))));
    }
}
