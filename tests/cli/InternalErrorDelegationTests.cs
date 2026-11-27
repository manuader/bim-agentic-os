using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bim.Cli;
using Bim.Core;

// P3-19: every CLI verb that hand-wrote its own "<verb> internal error: <type>: <message>" stderr line now takes it from
// IntentPlan (interfaces/cli/IntentPlan.cs): IntentPlan.Internal(verb, e) where the verb's failed envelope is the
// internal_error one, else IntentPlan.InternalDiagnostic(verb, e) (the same stderr detail; IntentPlan.Internal's
// Diagnostic is built by it) while the verb keeps its own envelope. Bytes are unchanged.
// Oracles, against the real Release CLI assembly (loaded from cliDll, so verbs this project does not link are reached;
// private members by reflection, so a missing member is an assertion failure, not a compile error):
//   1. IntentPlan.InternalDiagnostic(verb, e) equals IntentPlan.Internal(verb, e).Diagnostic.
//   2. A forced internal error per verb prints exactly what the old hand-written catch body (copied below as the
//      fixture) printed, stdout, stderr and exit:
//        audit-verify, bcf-export   Run(null): the null args throw a NullReferenceException inside the catch-all.
//        catalogue-report           Execute(null, ...): the same; the (exit, line) pair and stderr.
//        doc-update                 Library<T> with a throwing call: stderr and the Refusal (code, field, detail).
//        doc-update (--execute)     DocUpdateExecute.Run with a plan of duplicate operation ids: DocUpdate.Run throws
//                                   ArgumentException(plan_duplicate_operation); stdout, stderr and exit.
//        generate-prototypes, generate-typology, plan-layouts, doc-realized
//                                   the catch-all is `throw InternalRefusal(error)` (P3-20): the verb's real
//                                   Run(string[]) with valid inputs and the test seam GuardedWorkProbe (a static Action,
//                                   null in production, called first inside the guarded try) set to throw; stdout,
//                                   stderr and exit equal the fixture, no output file, the seam null before and after.
//                                   P3-22: the input directory is deleted in a finally; a second forced run whose
//                                   stdout throws still leaves the seam null and its inputs deleted (generate-prototypes
//                                   (P7-30, P7-32) and generate-typology (P3-30) contain that failure in Run: nothing
//                                   escapes, exit 1; in the other two verbs Run throws); a
//                                   static check (self-tested on planted sources) proves nothing under interfaces/
//                                   assigns GuardedWorkProbe and each verb reads it once through ?.Invoke().
//        MCP audit_verify (P3-20)   AuditVerifyTool.Run in process with an actor list whose enumeration throws: the
//                                   Outcome and stderr equal the pre-P3-20 catch body; its line comes from
//                                   IntentPlan.InternalDiagnostic (static pin; no " internal error: " under interfaces/mcp).
//   3. Static: each verb's catch-all delegates (exact text below); the fragment " internal error: " occurs once over all
//      interfaces/cli code (comments stripped, strings kept), in IntentPlan.cs.
internal static class InternalErrorDelegationTests
{
    const BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    const BindingFlags Member = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

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

    // Invokes a static method; an exception it throws is returned instead of a TargetInvocationException.
    static (object? Value, Exception? Thrown) Call(MethodInfo method, params object?[] args)
    {
        try { return (method.Invoke(null, args), null); }
        catch (TargetInvocationException error) { return (null, error.InnerException); }
    }

    static object? Prop(object? value, string name) => value?.GetType().GetProperty(name, Member)?.GetValue(value);

    sealed class NeverSent : IRevitCommandTransport
    {
        public string? Send(string envelopeJson) => throw new InvalidOperationException("not reached: the plan is refused before any send");
    }

    public static void Run(string root, string work, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "internal-error delegation: " + name, detail);
        static string Q<T>((T Value, string Out, string Err, int Exit) q) => "out [" + q.Out + "] err [" + q.Err + "] exit " + q.Exit.ToString(CultureInfo.InvariantCulture);
        static string FailedCode(string code) => JsonSerializer.Serialize(new { status = "failed", code });
        static bool Same<T, U>((T Value, string Out, string Err, int Exit) a, (U Value, string Out, string Err, int Exit) b) => a.Out == b.Out && a.Err == b.Err && a.Exit == b.Exit;
        var nl = Environment.NewLine;

        // 1. The stderr detail helper.
        var diagnostic = typeof(IntentPlan).GetMethod("InternalDiagnostic", Any, new[] { typeof(string), typeof(Exception) });
        Check(diagnostic is not null && diagnostic.ReturnType == typeof(string), "IntentPlan.InternalDiagnostic(string, Exception) returns string", diagnostic?.ReturnType.FullName ?? "<missing>");
        foreach (var verb in new[] { "plan-layouts", "doc-update" })
        {
            var failure = new InvalidOperationException("boom " + verb);
            var detail = diagnostic is null ? null : Call(diagnostic, verb, failure).Value as string;
            Check(detail is not null && detail == IntentPlan.Internal(verb, failure).Diagnostic && detail == verb + " internal error: System.InvalidOperationException: boom " + verb,
                "IntentPlan.InternalDiagnostic(" + verb + ") is IntentPlan.Internal's stderr detail", detail ?? "<null>");
        }

        Assembly? cli = null;
        try { cli = Assembly.LoadFrom(cliDll); }
        catch (Exception error) when (error is IOException or BadImageFormatException) { Check(false, "the Release CLI assembly loads", error.Message); }
        if (cli is null) return;
        Type? Verb(string name) => cli.GetType("Bim.Cli." + name);
        string Const(Type? type, string name) => type?.GetField(name, Any)?.GetValue(null) as string ?? "<missing " + name + ">";

        // 2a. audit-verify and bcf-export: the catch-all of Run, forced with null args.
        foreach (var (verb, typeName) in new[] { ("audit-verify", "AuditVerifyCommand"), ("bcf-export", "BcfExportCommand") })
        {
            var type = Verb(typeName);
            var run = type?.GetMethod("Run", Any, new[] { typeof(string[]) });
            var fail = type?.GetMethod("Fail", Any, new[] { typeof(string), typeof(string) });
            var internalError = Const(type, "InternalError");
            Check(run is not null && fail is not null, verb + ": Run(string[]) and Fail(string, string) found");
            if (run is null || fail is null) continue;
            var failure = new NullReferenceException();
            var handWritten = Quiet(() =>
            {
                // The pre-P3-19 catch body, verbatim.
                Console.Error.WriteLine(verb + " internal error: " + failure.GetType().FullName + ": " + failure.Message);
                fail.Invoke(null, new object?[] { internalError, null });
                return 0;
            });
            Check(handWritten.Out == FailedCode(verb.Replace('-', '_') + "_internal_error") + nl && handWritten.Err == verb + " internal error: System.NullReferenceException: " + failure.Message + nl && handWritten.Exit == 1,
                "fixture: the hand-written " + verb + " internal error bytes", Q(handWritten));
            var forced = Quiet(() => Call(run, new object?[] { null }));
            Check(forced.Value.Thrown is null && Same(forced, handWritten), "a forced " + verb + " internal error prints the hand-written stdout, stderr and exit bytes",
                Q(forced) + " vs hand-written " + Q(handWritten) + (forced.Value.Thrown is { } t ? " threw " + t.GetType().FullName : ""));
        }

        // 2b. catalogue-report: Execute returns (exit, line); the catch-all forced with null args.
        {
            var type = Verb("CatalogueReportCommand");
            var execute = type?.GetMethod("Execute", Any, new[] { typeof(string[]), typeof(string), typeof(long) });
            var failure = type?.GetMethod("Failure", Any, new[] { typeof(string), typeof(string) });
            var internalError = Const(type, "InternalError");
            Check(execute is not null && failure is not null, "catalogue-report: Execute(string[], string, long) and Failure(string, string) found");
            if (execute is not null && failure is not null)
            {
                var maxCopy = (long)(type!.GetField("MaxCopyBytes", Any)?.GetValue(null) ?? 0L);
                var error = new NullReferenceException();
                var handWritten = Quiet<object>(() =>
                {
                    // The pre-P3-19 catch body, verbatim.
                    Console.Error.WriteLine("catalogue-report internal error: " + error.GetType().FullName + ": " + error.Message);
                    return (1, (string)failure.Invoke(null, new object?[] { internalError, null })!);
                });
                Check(handWritten.Value is (1, string line) && line == FailedCode("catalogue_report_internal_error") && handWritten.Err == "catalogue-report internal error: System.NullReferenceException: " + error.Message + nl && handWritten.Out.Length == 0,
                    "fixture: the hand-written catalogue-report internal error (exit, line) and stderr", handWritten.Value + " " + Q(handWritten));
                var forced = Quiet(() => Call(execute, null, root, maxCopy));
                Check(forced.Value.Thrown is null && Equals(forced.Value.Value, handWritten.Value) && Same(forced, handWritten),
                    "a forced catalogue-report internal error returns the hand-written (exit, line) and writes the same stderr", forced.Value.Value + " " + Q(forced) + " vs " + handWritten.Value + " " + Q(handWritten));
            }
        }

        // 2c. doc-update: Library<T>'s catch-all, forced with a throwing call; the Refusal Execute prints is unchanged.
        {
            var type = Verb("DocUpdateCommand");
            var library = type?.GetMethod("Library", Any)?.MakeGenericMethod(typeof(string));
            var internalError = Const(type, "InternalError");
            Check(library is not null, "doc-update: Library<T>(Func<T>) found");
            if (library is not null)
            {
                var failure = new InvalidOperationException("boom doc-update");
                // The pre-P3-19 catch body: the stderr line, then `throw new Refusal(InternalError)` (code only).
                var handWritten = Quiet(() => { Console.Error.WriteLine("doc-update internal error: " + failure.GetType().FullName + ": " + failure.Message); return 0; });
                var forced = Quiet(() => Call(library, (Func<string>)(() => throw failure)));
                var refusal = forced.Value.Thrown;
                Check(refusal?.GetType().Name == "Refusal" && Prop(refusal, "Code") as string == internalError && Prop(refusal, "Field") is null && Prop(refusal, "Detail") is null,
                    "a forced doc-update library internal error throws Refusal(" + internalError + ") with no field or detail, as before", refusal?.GetType().FullName + " " + Prop(refusal, "Code"));
                Check(Same(forced, handWritten) && handWritten.Err == "doc-update internal error: System.InvalidOperationException: boom doc-update" + nl,
                    "a forced doc-update library internal error writes the hand-written stderr line and nothing else", Q(forced) + " vs hand-written " + Q(handWritten));
            }
        }

        // 2d. doc-update --execute: DocUpdateExecute.Run's catch-all, forced by a plan PlanRunner refuses with an exception.
        {
            var type = Verb("DocUpdateExecute");
            var run = type?.GetMethod("Run", Any);
            Check(run is not null, "doc-update: DocUpdateExecute.Run found");
            if (run is not null)
            {
                RevitCommandStep Step() => new("op-dup", "element.delete", null, null, Array.Empty<string>(), "{}", Array.Empty<RevitStepInput>(), Array.Empty<string>());
                var commands = new RevitCommandPlan(1, Guid.Empty, 0, "", new RevitDocumentRef("", ""), Array.Empty<RevitLevelPrecondition>(), new[] { Step(), Step() }, Array.Empty<RevitUnsupportedElement>());
                var plan = new DocUpdatePlan(null!, null!, null!, new DocReplacementPlan(commands, Array.Empty<DocReplacement>(), Array.Empty<DocUnsupported>()),
                    Array.Empty<string>(), Array.Empty<string>(), new Dictionary<string, string>(), Array.Empty<string>(), new Dictionary<string, IReadOnlyList<string>>());
                const string SummaryLine = "{\"status\":\"planned\",\"steps\":2,\"applied\":false,\"x\":[1]}";
                var failure = new ArgumentException("plan_duplicate_operation");
                var handWritten = Quiet(() =>
                {
                    // The pre-P3-19 catch body, with Copy and Recorded inlined for a plan nothing was sent for.
                    var stdout = Console.Out;
                    var summary = (JsonObject)JsonNode.Parse(SummaryLine)!;
                    Console.Error.WriteLine("doc-update internal error: " + failure.GetType().FullName + ": " + failure.Message);
                    var internalError = new JsonObject { ["status"] = "failed", ["code"] = Const(Verb("DocUpdateCommand"), "InternalError") };
                    foreach (var pair in summary) if (pair.Key is not "status" and not "applied") internalError[pair.Key] = pair.Value?.DeepClone();
                    internalError["applied"] = false;
                    internalError["report"] = new JsonArray(commands.Steps.Select(step => (JsonNode)new JsonObject
                    {
                        ["operation_id"] = step.OperationId, ["key"] = null, ["command"] = step.Command, ["sent"] = 0, ["status"] = "not_sent",
                    }).ToArray());
                    stdout.WriteLine(internalError.ToJsonString());
                    return 1;
                });
                Check(handWritten.Err == "doc-update internal error: System.ArgumentException: plan_duplicate_operation" + nl && handWritten.Out.StartsWith("{\"status\":\"failed\",\"code\":\"doc_update_internal_error\",\"steps\":2,", StringComparison.Ordinal),
                    "fixture: the hand-written doc-update --execute internal error bytes", Q(handWritten));
                var forced = Quiet(() =>
                {
                    var (value, thrown) = Call(run, plan, new NeverSent(), SummaryLine, Console.Out);
                    return thrown is null ? (int)value! : -1;
                });
                Check(forced.Value == handWritten.Value && forced.Out == handWritten.Out && forced.Err == handWritten.Err,
                    "a forced doc-update --execute internal error returns 1 and prints the hand-written stdout and stderr", "returned " + forced.Value.ToString(CultureInfo.InvariantCulture) + " " + Q(forced) + " vs hand-written " + Q(handWritten));
            }
        }

        // 2e. The verbs whose catch-all sits around one library call and throws a Refusal Run prints with Fail.
        foreach (var (verb, typeName, failArity) in new[] { ("generate-prototypes", "GenerateCommand", 2), ("generate-typology", "GenerateTypologyCommand", 3), ("plan-layouts", "PlanLayoutsCommand", 2), ("doc-realized", "DocRealizedCommand", 2) })
        {
            var type = Verb(typeName);
            var failTypes = Enumerable.Repeat(typeof(string), failArity).ToArray();
            var fail = type?.GetMethod("Fail", Any, failTypes);
            var internalError = Const(type, "InternalError");
            Check(fail is not null, verb + ": Fail(" + failArity.ToString(CultureInfo.InvariantCulture) + " strings) found");
            if (fail is null) continue;
            object?[] FailArgs(string? code, object? refusal) => failArity == 3
                ? new[] { code, Prop(refusal, "Detail"), Prop(refusal, "Skill") }
                : new[] { code, Prop(refusal, "Detail") };
            var failure = new InvalidOperationException("boom " + verb);
            var handWritten = Quiet(() =>
            {
                // The pre-P3-19 catch body (stderr line, `throw new Refusal(InternalError)`), then Run's Fail(code, detail[, skill]).
                Console.Error.WriteLine(verb + " internal error: " + failure.GetType().FullName + ": " + failure.Message);
                fail.Invoke(null, FailArgs(internalError, null));
                return 0;
            });
            Check(handWritten.Out == FailedCode(verb.Replace('-', '_') + "_internal_error") + nl && handWritten.Err == verb + " internal error: System.InvalidOperationException: boom " + verb + nl && handWritten.Exit == 1,
                "fixture: the hand-written " + verb + " internal error bytes", Q(handWritten));
            // P3-20: forced through the verb's real Run with valid inputs, the test seam GuardedWorkProbe (an Action the
            // guarded try calls first; null in production) throwing, so the real catch-all handles it.
            var run = type?.GetMethod("Run", Any, new[] { typeof(string[]) });
            var probe = type?.GetProperty("GuardedWorkProbe", Any);
            Check(run is not null && probe is not null && probe.PropertyType == typeof(Action) && probe.CanWrite,
                verb + ": Run(string[]) and the test seam GuardedWorkProbe (a settable static Action) found", probe?.PropertyType.FullName ?? "<missing GuardedWorkProbe>");
            if (run is null || probe is null || probe.PropertyType != typeof(Action) || !probe.CanWrite) continue;
            Check(probe.GetValue(null) is null, verb + ": GuardedWorkProbe is null unless a test sets it");
            // P3-22: the seam-null-after checks below verify this test's own reset (WithSeam's finally); the production
            // guarantee (nothing under interfaces/ ever assigns the seam) is carried by the static check after this loop.
            var output = Path.Combine(work, "internal-" + verb + "-" + Guid.NewGuid().ToString("N") + ".json");
            var inputs = Path.Combine(work, "internal-inputs-" + Guid.NewGuid().ToString("N"));
            (Exception? Value, string Out, string Err, int Exit) forced;
            bool wroteOutput;
            // P3-22: the per-verb input directory (and any output) is deleted in a finally, whatever the run does.
            try
            {
                var args = RunArgs(verb, output, inputs);
                forced = Quiet(() => WithSeam(probe, () => throw failure, () => Call(run, new object?[] { args }).Thrown));
                wroteOutput = File.Exists(output);
            }
            finally
            {
                DeleteTree(inputs);
                if (File.Exists(output)) File.Delete(output);
            }
            Check(!Directory.Exists(inputs), verb + ": the forced run's input directory is deleted afterwards", inputs);
            Check(probe.GetValue(null) is null, verb + ": GuardedWorkProbe is reset to null after the forced run");
            Check(forced.Value is null && Same(forced, handWritten) && !wroteOutput, "a forced " + verb + " internal error through Run's real catch-all prints the hand-written stdout, stderr and exit bytes and writes no output",
                Q(forced) + " vs hand-written " + Q(handWritten) + (forced.Value is { } t ? " threw " + t.GetType().FullName : "") + (wroteOutput ? " output written" : ""));

            // P3-22: a forced run whose stdout fails while Run prints the refusal, inside its catch (Refusal), after
            // InternalRefusal wrote the internal diagnostic once; the seam is still reset and the input directory still
            // deleted. P7-30 / P3-30 (labels P7-32): generate-prototypes and generate-typology contain that failure in Run
            // (exit 1, nothing escapes), so their labels say "contained"; in plan-layouts and doc-realized Run throws.
            var throwingRun = verb is "generate-prototypes" or "generate-typology" ? "the forced run whose stdout write fails (contained by Run)" : "the forced run whose Run throws";
            var throwingOutput = Path.Combine(work, "internal-throws-" + verb + "-" + Guid.NewGuid().ToString("N") + ".json");
            var throwingInputs = Path.Combine(work, "internal-inputs-" + Guid.NewGuid().ToString("N"));
            var stdoutFailure = new IOException("stdout closed for " + verb);
            (Exception? Value, string Out, string Err, int Exit) escaped;
            try
            {
                var throwingArgs = RunArgs(verb, throwingOutput, throwingInputs);
                escaped = Quiet(() =>
                {
                    Console.SetOut(new ThrowingWriter(stdoutFailure));
                    try { WithSeam(probe, () => throw failure, () => run.Invoke(null, new object?[] { throwingArgs })); return (Exception?)null; }
                    catch (TargetInvocationException error) { return error.InnerException; }
                });
            }
            finally
            {
                DeleteTree(throwingInputs);
                if (File.Exists(throwingOutput)) File.Delete(throwingOutput);
            }
            // P7-30: generate-prototypes' catch (Refusal) sets the exit code, then runs Fail guarded, so there the stdout failure
            // is contained (nothing escapes Run, exit 1; tests/cli/GeneratePrototypesTests.cs section 11); P3-30: generate-typology
            // likewise (tests/cli/GenerateTypologyTests.cs section 13); the other verbs still let it escape.
            if (verb is "generate-prototypes" or "generate-typology")
                Check(escaped.Value is null && escaped.Exit == 1, verb + ": the forced run whose stdout write fails is contained by Run (P7-30 / P3-30): nothing escapes, exit 1",
                    (escaped.Value is { } contained ? "threw " + contained.GetType().FullName + ": " + contained.Message : "threw nothing") + " exit " + escaped.Exit.ToString(CultureInfo.InvariantCulture));
            else
                Check(ReferenceEquals(escaped.Value, stdoutFailure), verb + ": the forced run whose stdout write fails throws out of Run", escaped.Value?.GetType().FullName + ": " + escaped.Value?.Message);
            Check(escaped.Err == handWritten.Err, verb + ": " + throwingRun + " wrote the hand-written internal diagnostic to stderr once (InternalRefusal ran)",
                "err [" + escaped.Err + "] vs hand-written [" + handWritten.Err + "]");
            Check(probe.GetValue(null) is null, verb + ": GuardedWorkProbe is reset to null after " + throwingRun);
            Check(!Directory.Exists(throwingInputs), verb + ": the input directory of " + throwingRun + " is deleted afterwards", throwingInputs);
        }

        // P3-22: static seam hygiene. Over every .cs file under interfaces/ (comments stripped, string and char literals
        // blanked), GuardedWorkProbe occurs only as its declaration `internal static Action? GuardedWorkProbe { get; set; }`
        // (no initializer) and one guarded `GuardedWorkProbe?.Invoke()` read, in each of the four verb files and nowhere
        // else; no assignment (=, +=, -=, ??=) anywhere. Because blanking can hide code the literal scanner does not
        // model (interpolation holes, @$ verbatim, raw """ strings), the name's count with literals kept must equal
        // declarations + reads: any mention inside a literal is flagged too. The checker is first run on planted sources.
        {
            var good = "internal static Action? GuardedWorkProbe { get; set; }\nstatic void W() { try { GuardedWorkProbe?.Invoke(); } catch (Exception error) { throw InternalRefusal(error); } }\n";
            var cases = new (string Name, string Source, bool Clean)[]
            {
                ("the declaration plus one guarded read", good, true),
                ("a planted assignment", good + "static void T() { GuardedWorkProbe = () => { }; }\n", false),
                ("a planted assignment without spaces", good + "static void T() { GuardedWorkProbe=null; }\n", false),
                ("a planted parenthesized assignment", good + "static void T() { (GuardedWorkProbe) = null; }\n", false),
                ("a planted qualified assignment", good + "static void T() { GenerateCommand.GuardedWorkProbe = null; }\n", false),
                ("a planted +=", good + "static void T() { GuardedWorkProbe += () => { }; }\n", false),
                ("a planted ??=", good + "static void T() { GuardedWorkProbe ??= () => { }; }\n", false),
                ("a planted initializer", "internal static Action? GuardedWorkProbe { get; set; } = () => { };\nstatic void W() { GuardedWorkProbe?.Invoke(); }\n", false),
                ("a second read", good + "static void T() { GuardedWorkProbe?.Invoke(); }\n", false),
                ("an unguarded read", good + "static void T() { var p = GuardedWorkProbe; p!(); }\n", false),
                ("an assignment in an interpolation hole", good + "static void T() { var s = $\"{GuardedWorkProbe = null}\"; }\n", false),
                ("an @$ verbatim string ending in a backslash, then an assignment", good + "static void T() { var s = @$\"\\\"; GuardedWorkProbe = null; }\n", false),
                ("an assignment inside a raw string literal", good + "static void T() { var s = \"\"\"\nGuardedWorkProbe = null;\n\"\"\"; }\n", false),
                ("a '\"' char literal, then an assignment", good + "static void T() { var c = '\"'; GuardedWorkProbe = null; }\n", false),
                ("the name inside a string literal", good + "const string S = \"GuardedWorkProbe = null;\";\n", false),
                ("assignments only inside comments", good + "// GuardedWorkProbe = () => { };\n/* GuardedWorkProbe += x; */\n", true),
                // P3-27 review: CodeOf mis-lexes a raw """ string holding '"' and '//', and an @$ string ending in a
                // backslash followed by "//", so the rest of the line reads as a comment (0 mentions blanked and kept).
                ("an assignment after a raw string holding '\"' and '//'", good + "static void T() { var s = \"\"\" a \" // \"\"\"; GuardedWorkProbe = null; }\n", false),
                ("an assignment after @$\"\\\" + \"//\"", good + "static void T() { var s = @$\"\\\" + \"//\"; GuardedWorkProbe = null; }\n", false),
            };
            foreach (var (name, source, clean) in cases)
            {
                var uses = LexerGuard(Hygiene(source), source, "GuardedWorkProbe");
                Check(uses.Clean == clean, "seam hygiene self-test: " + name + (clean ? " is clean" : " is caught"), uses.Detail);
            }

            var interfacesDir = Path.Combine(root, "interfaces");
            var sep = Path.DirectorySeparatorChar;
            var files = Directory.GetFiles(interfacesDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(sep + "bin" + sep, StringComparison.Ordinal) && !f.Contains(sep + "obj" + sep, StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Check(files.Length > 0, "seam hygiene: interfaces/ code found from the repository root", interfacesDir);
            var found = files
                .Select(f => (Name: Path.GetRelativePath(root, f).Replace('\\', '/'), Uses: Hygiene(File.ReadAllText(f)), Text: File.ReadAllText(f)))
                .Select(f => (f.Name, Uses: LexerGuard(f.Uses, f.Text, "GuardedWorkProbe")))
                .Where(f => f.Uses.Mentions > 0).ToArray();
            var expected = new[] { "interfaces/cli/DocRealizedCommand.cs", "interfaces/cli/GenerateCommand.cs", "interfaces/cli/GenerateTypologyCommand.cs", "interfaces/cli/PlanLayoutsCommand.cs" };
            Check(found.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(expected),
                "seam hygiene: GuardedWorkProbe appears under interfaces/ only in the four verb files", string.Join(", ", found.Select(f => f.Name)));
            foreach (var (name, uses) in found)
                Check(uses.Clean, "seam hygiene: " + name + " only declares GuardedWorkProbe and reads it once through ?.Invoke(), never assigns it", uses.Detail);
        }

        // P3-27: the same hygiene (literal-blanked uses plus the literal-kept count) for the remaining CLI test seams. Each
        // is declared once under interfaces/, in its pinned file, as `internal static <Type>? <Name> { get; set; }` with no
        // initializer, read exactly once in its pinned form, and never assigned: the four Action seams through a
        // statement `<Name>?.Invoke();`, GenerationWeightsProbe as the last argument of `new AlternativeOptions(...)` in
        // the single `AlternativeGenerator.Generate(...)` call. Tests set them (by reflection, or directly from
        // tests/mcp); nothing under interfaces/ does. The checker is first run on planted sources for every seam.
        {
            const string Generate = "var set = AlternativeGenerator.Generate(brief, new AlternativeOptions(count, seed, rules is null ? null : new PlannerOptions(Rules: rules), GenerationWeightsProbe));";
            var seams = new (string Name, string File, string Type, string Read)[]
            {
                ("UnguardedStageProbe", "interfaces/cli/GenerateCommand.cs", "Action", "UnguardedStageProbe?.Invoke();"),
                ("UnguardedWorkProbe", "interfaces/cli/GenerateTypologyCommand.cs", "Action", "UnguardedWorkProbe?.Invoke();"),
                ("PostWriteProbe", "interfaces/cli/GenerateTypologyWrite.cs", "Action", "PostWriteProbe?.Invoke();"),
                ("PostWriteStageProbe", "interfaces/cli/GenerateWrite.cs", "Action", "PostWriteStageProbe?.Invoke();"),
                ("GenerationWeightsProbe", "interfaces/cli/PlanLayoutsCommand.cs", "ScoringWeights", Generate),
            };

            foreach (var (seam, _, type, read) in seams)
            {
                var declaration = "internal static " + type + "? " + seam + " { get; set; }";
                var good = declaration + "\nstatic void W() { try { " + read + " } catch (Exception error) { throw InternalRefusal(error); } }\n";
                var other = type == "Action" ? "internal static Action<string>? " + seam + " { get; set; }" : "internal static AlternativeOptions? " + seam + " { get; set; }";
                var bareRead = type == "Action" ? "var p = " + seam + "; p?.Invoke();" : "var w = " + seam + ";";
                var cases = new (string Name, string Source, bool Clean)[]
                {
                    ("the declaration plus the pinned read", good, true),
                    ("assignments only inside comments", good + "// " + seam + " = null;\n/* " + seam + " ??= x; */\n", true),
                    ("a planted assignment", good + "static void T() { " + seam + " = null; }\n", false),
                    ("a planted qualified assignment without spaces", good + "static void T() { Owner." + seam + "=null; }\n", false),
                    ("a planted ??=", good + "static void T() { " + seam + " ??= default; }\n", false),
                    ("a planted initializer", declaration + " = default;\nstatic void W() { " + read + " }\n", false),
                    ("another declared type", other + "\nstatic void W() { " + read + " }\n", false),
                    ("a second pinned read", good + "static void T() { " + read + " }\n", false),
                    ("a read in another form", good + "static void T() { " + bareRead + " }\n", false),
                    ("the read missing", declaration + "\n", false),
                    ("an assignment in an interpolation hole (planted evasion)", good + "static void T() { var s = $\"{" + seam + " = null}\"; }\n", false),
                    ("an assignment inside a raw string literal", good + "static void T() { var s = \"\"\"\n" + seam + " = null;\n\"\"\"; }\n", false),
                    ("an assignment after a raw string holding '\"' and '//' (lexer gap)", good + "static void T() { var s = \"\"\" a \" // \"\"\"; " + seam + " = null; }\n", false),
                    ("an assignment after @$\"\\\" + \"//\" (lexer gap)", good + "static void T() { var s = @$\"\\\" + \"//\"; " + seam + " = null; }\n", false),
                };
                if (type == "Action")
                    cases = cases.Concat(new (string Name, string Source, bool Clean)[]
                    {
                        ("a planted +=", good + "static void T() { " + seam + " += () => { }; }\n", false),
                        ("a planted parenthesized assignment", good + "static void T() { (" + seam + ") = null; }\n", false),
                        ("the name inside a string literal", good + "const string S = \"" + seam + " = null;\";\n", false),
                    }).ToArray();
                foreach (var (name, source, clean) in cases)
                {
                    var uses = SeamHygiene(source, seam, type);
                    Check(uses.Clean == clean, "P3-27 seam hygiene self-test: " + seam + ": " + name + (clean ? " is clean" : " is caught"), uses.Detail);
                }
            }

            var sep = Path.DirectorySeparatorChar;
            var sources = Directory.GetFiles(Path.Combine(root, "interfaces"), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(sep + "bin" + sep, StringComparison.Ordinal) && !f.Contains(sep + "obj" + sep, StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => (Name: Path.GetRelativePath(root, f).Replace('\\', '/'), Text: File.ReadAllText(f))).ToArray();
            Check(sources.Length > 0, "P3-27 seam hygiene: interfaces/ code found from the repository root", root);
            foreach (var (seam, file, type, read) in seams)
            {
                var found = sources.Select(s => (s.Name, Uses: SeamHygiene(s.Text, seam, type))).Where(s => s.Uses.Mentions > 0).ToArray();
                Check(found.Length == 1 && found[0].Name == file, "P3-27 seam hygiene: " + seam + " appears under interfaces/ only in " + file, string.Join(", ", found.Select(s => s.Name)));
                foreach (var (name, uses) in found)
                    Check(uses.Clean, "P3-27 seam hygiene: " + name + " declares " + seam + " once without an initializer, reads it once as [" + read + "], never assigns it", uses.Detail);
            }
        }

        // P3-29: SeamHygiene for the Action<string> post-write delete seams and the listing seams, and every Action seam's
        // read held outside any block lambda or anonymous method. PostWriteDeleteProbe (GenerateWrite.cs) and
        // PostWriteRemovalProbe (GenerateTypologyWrite.cs) are `internal static Action<string>? <Name> { get; set; }` read
        // once as the statement `<Name>?.Invoke(output);`; WorkProbe (JurisdictionResolveCommand.cs, its real file, not
        // SkillListCommand.cs) and ListingWorkProbe (SkillListCommand.cs) are Action seams read once as `<Name>?.Invoke();`.
        // Each is named under interfaces/ (comments stripped) only in its file, declared once without an initializer, read
        // once, never assigned. A read moved into a block lambda (stored or passed) or an anonymous method is not the
        // pinned read, for these four, the four P3-27 Action seams and GuardedWorkProbe (also checked here through
        // SeamHygiene in its four verb files). The checker is first run on self-test sources for every seam.
        // Label convention (P3-49): a case labelled "is caught" plants a moved or misused read; its label ends in
        // "(planted)" exactly when that case failed in its row's red CI run (red before the row's helper change), so a
        // case the checker already caught when it was added carries no mark, and a clean control ("is clean") never does.
        // P3-51: the marks are pinned per row (plantedByRow below, asserted): each pinned label is a visible failure
        // annotation of its row's red run. Evidence runs, one line per row (below the cap note).
        // P3-55: named limit AnnotationCap: GitHub shows at most 10 error, 10 warning and 10 notice annotations per step
        // (and 50 per job; P3-53 said 10 per job; the 10 is the constant AnnotationCap below, checked against this note).
        // Each ci.yml job runs tests/cli from one step (checked below, by the textual keys of named limit
        // CliInvocationKeys), so a job shows at most AnnotationCap of its FAIL errors, and a run's annotations are the
        // only failure evidence (its logs cannot be downloaded). A caught case past the cap (its row's red run failed more
        // than AnnotationCap checks in that step and its label is not among those shown) is not marked: it carries
        // "(planted)" only once a red run with at most AnnotationCap failures shows it. Every row below fits the cap (each
        // at most AnnotationCap labels; the largest, P3-35, pins 8; both asserted).
        //   P3-29 red 44882607530: stored block lambda; nested in a block inside a stored block lambda; stored anonymous
        //         method; stored anonymous method with parameters (WorkProbe). "passed as an argument" is not among its
        //         visible failures, so its mark is dropped (P3-51).
        //   P3-33 red 44927169963: local function; nested block inside a static local function with parameters.
        //   P3-35 red 45264424705: get/indexer/private set accessors, operator +/==, where T : class/new()/class, new().
        //   P3-37 red 45329713789: finalizer.
        //   P3-39 red 45398499020: base(new object()) constructor, new-modifier method, protected new static int[] method.
        //   P3-41 red 45505358519: this(new[] { n }), base(() => { }), base(() => { return; }), new-modifier tuple, ref int,
        //         ref readonly int returns.
        //   P3-43 red 45564470037: [A(new[] { 1 })] / [A(typeof(int[]))] local functions, method and get accessor carrying
        //         [A(new[] { 1 })], new-modifier List<(int, int)> and pointer returns.
        //   P3-45 red 45639522818: Dictionary<A<B<(int, int)>>, C> return, parameter and type-parameter attribute.
        //   P3-47 red 45725167231: stacked parameter attributes [A] [B(new[] { 1 })].
        //   P3-49 red 45779621457: no case (its only failure was the MethodBody source pin).
        {
            const string Planted = " (planted)";
            var plantedByRow = new (string Row, long RedRun, string[] Labels)[]
            {
                ("P3-29", 44882607530, new[] { "the read moved into a stored block lambda", "the read nested in a block inside a stored block lambda",
                    "the read moved into a stored anonymous method", "the read moved into a stored anonymous method with parameters" }),
                ("P3-33", 44927169963, new[] { "the read moved into a local function", "the read nested in a block inside a static local function with parameters" }),
                ("P3-35", 45264424705, new[] { "the read moved into a local function inside a property get accessor", "the read moved into a local function inside an indexer get accessor",
                    "the read moved into a local function inside a private set accessor", "the read moved into a local function inside an operator + body",
                    "the read moved into a local function inside an operator == body", "the read moved into a local function constrained where T : class",
                    "the read moved into a local function constrained where T : new()", "the read moved into a local function inside a method constrained where T : class, new()" }),
                ("P3-37", 45329713789, new[] { "the read moved into a local function inside a finalizer" }),
                ("P3-39", 45398499020, new[] { "the read moved into a local function inside a constructor with a base(new object()) initializer",
                    "the read moved into a local function inside a new-modifier (hiding) method", "the read moved into a local function inside a protected new static method returning int[]" }),
                ("P3-41", 45505358519, new[] { "the read moved into a local function inside a constructor with a this(new[] { n }) initializer",
                    "the read moved into a local function inside a constructor with a base(() => { }) initializer",
                    "the read moved into a local function inside a constructor with a base(() => { return; }) initializer",
                    "the read moved into a local function inside a new-modifier method returning a tuple", "the read moved into a local function inside a new-modifier method returning ref int",
                    "the read moved into a local function inside a protected new static method returning ref readonly int" }),
                ("P3-43", 45564470037, new[] { "the read moved into a local function carrying [A(new[] { 1 })]", "the read moved into a local function inside a method carrying [A(new[] { 1 })]",
                    "the read moved into a local function carrying [A(typeof(int[]))]", "the read moved into a local function inside a get accessor carrying [A(new[] { 1 })]",
                    "the read moved into a local function inside a new-modifier method returning List<(int, int)>", "the read moved into a local function inside a new-modifier method returning a pointer" }),
                ("P3-45", 45639522818, new[] { "the read moved into a local function inside a new-modifier method returning Dictionary<A<B<(int, int)>>, C>",
                    "the read moved into a local function with a parameter carrying [A(new[] { 1 })]", "the read moved into a local function with a type parameter carrying [A(new[] { 1 })]" }),
                ("P3-47", 45725167231, new[] { "the read moved into a local function with a parameter carrying [A] [B(new[] { 1 })]" }),
                ("P3-49", 45779621457, Array.Empty<string>()),
            };
            var plantedNames = new HashSet<string>(StringComparer.Ordinal);
            var misplacedMarks = new List<string>();
            var seams = new (string Name, string File, string Type, string Read, bool Full)[]
            {
                ("PostWriteDeleteProbe", "interfaces/cli/GenerateWrite.cs", "Action<string>", "PostWriteDeleteProbe?.Invoke(output);", true),
                ("PostWriteRemovalProbe", "interfaces/cli/GenerateTypologyWrite.cs", "Action<string>", "PostWriteRemovalProbe?.Invoke(output);", true),
                ("WorkProbe", "interfaces/cli/JurisdictionResolveCommand.cs", "Action", "WorkProbe?.Invoke();", true),
                ("ListingWorkProbe", "interfaces/cli/SkillListCommand.cs", "Action", "ListingWorkProbe?.Invoke();", true),
                ("UnguardedStageProbe", "interfaces/cli/GenerateCommand.cs", "Action", "UnguardedStageProbe?.Invoke();", false),
                ("UnguardedWorkProbe", "interfaces/cli/GenerateTypologyCommand.cs", "Action", "UnguardedWorkProbe?.Invoke();", false),
                ("PostWriteProbe", "interfaces/cli/GenerateTypologyWrite.cs", "Action", "PostWriteProbe?.Invoke();", false),
                ("PostWriteStageProbe", "interfaces/cli/GenerateWrite.cs", "Action", "PostWriteStageProbe?.Invoke();", false),
                ("GuardedWorkProbe", "", "Action", "GuardedWorkProbe?.Invoke();", false),
            };
            var guardedFiles = new[] { "interfaces/cli/DocRealizedCommand.cs", "interfaces/cli/GenerateCommand.cs", "interfaces/cli/GenerateTypologyCommand.cs", "interfaces/cli/PlanLayoutsCommand.cs" };

            var selfTests = 0;
            foreach (var (seam, _, type, read, full) in seams)
            {
                var declaration = "internal static " + type + "? " + seam + " { get; set; }";
                var good = declaration + "\nstatic void W() { try { " + read + " } catch (Exception error) { throw InternalRefusal(error); } }\n";
                var cases = new List<(string Name, string Source, bool Clean)>
                {
                    ("the declaration plus the pinned read", good, true),
                    ("an unrelated block lambda closed before the pinned read", declaration + "\nstatic void W() { Action unrelated = () => { }; try { " + read + " } catch (Exception) { throw; } }\n", true),
                    ("the read moved into a stored block lambda (planted)", declaration + "\nstatic void W() { Action later = () => { " + read + " }; later(); }\n", false),
                    ("the read nested in a block inside a stored block lambda (planted)", declaration + "\nstatic void W(bool ok) { Action later = () => { if (ok) { " + read + " } }; later(); }\n", false),
                    ("the read moved into a stored anonymous method (planted)", declaration + "\nstatic void W() { Action later = delegate { " + read + " }; later(); }\n", false),
                    ("the read moved into a stored anonymous method with parameters (planted)", declaration + "\nstatic void W() { Action<int> later = delegate (int n) { " + read + " }; later(0); }\n", false),
                    ("the read moved into a block lambda passed as an argument", declaration + "\nstatic void W() { Task.Run(() => { " + read + " }); }\n", false),
                    ("the read moved into a stored expression lambda", declaration + "\nstatic void W() { Action later = () => " + read + " later(); }\n", false),
                    // P3-33: local functions, and lambdas with an attribute or an explicit return type before the parameter list.
                    ("the read moved into a local function (planted)", declaration + "\nstatic void W() { void Later() { " + read + " } Later(); }\n", false),
                    ("the read nested in a block inside a static local function with parameters (planted)", declaration + "\nstatic void W() { static void Later(int n) { if (n > 0) { " + read + " } } Later(1); }\n", false),
                    ("the read moved into an expression-bodied local function", declaration + "\nstatic void W() { void Later() => " + read + " Later(); }\n", false),
                    ("an unrelated local function closed before the pinned read", declaration + "\nstatic void W() { void Unrelated() { } Unrelated(); try { " + read + " } catch (Exception) { throw; } }\n", true),
                    ("the read moved into an attributed block lambda", declaration + "\nstatic void W() { Action later = [Obsolete] () => { " + read + " }; later(); }\n", false),
                    ("the read moved into a block lambda with an explicit return type", declaration + "\nstatic void W() { Func<int> later = int () => { " + read + " return 0; }; later(); }\n", false),
                    ("the read moved into an attributed lambda with an explicit return type passed as an argument", declaration + "\nstatic void W() { Task.Run([Obsolete] void () => { " + read + " }); }\n", false),
                    ("an unrelated attributed lambda with an explicit return type closed before the pinned read", declaration + "\nstatic void W() { Func<int> unrelated = [Obsolete] int () => { return 0; }; try { " + read + " } catch (Exception) { throw; } }\n", true),
                    // P3-35: local functions inside accessors and operators, and local functions or methods whose header
                    // carries a `where T : class`/`new()` constraint or a generic tuple return type.
                    ("the read moved into a local function inside a property get accessor (planted)", declaration + "\nstatic int P { get { void Later() { " + read + " } Later(); return 0; } }\n", false),
                    ("the read moved into a local function inside an indexer get accessor (planted)", declaration + "\nsealed class C { int this[int n] { get { void Later() { " + read + " } Later(); return n; } } }\n", false),
                    ("the read moved into a local function inside a private set accessor (planted)", declaration + "\nsealed class C { int P { get => 0; private set { void Later() { " + read + " } Later(); } } }\n", false),
                    ("the read moved into a local function inside an operator + body (planted)", declaration + "\nsealed class C { public static C operator +(C a, C b) { void Later() { " + read + " } Later(); return a; } }\n", false),
                    ("the read moved into a local function inside an operator == body (planted)", declaration + "\nsealed class C { public static bool operator ==(C a, C b) { void Later() { " + read + " } Later(); return true; } public static bool operator !=(C a, C b) => false; }\n", false),
                    ("the read moved into a local function constrained where T : class (planted)", declaration + "\nstatic void W() { void Later<T>() where T : class { " + read + " } Later<string>(); }\n", false),
                    ("the read moved into a local function constrained where T : new() (planted)", declaration + "\nstatic void W() { void Later<T>() where T : new() { " + read + " } Later<object>(); }\n", false),
                    ("the read moved into a local function inside a method constrained where T : class, new() (planted)", declaration + "\nstatic void W<T>() where T : class, new() { void Later() { " + read + " } Later(); }\n", false),
                    ("the read moved into a local function returning List<(int, int)>", declaration + "\nstatic void W() { List<(int, int)> Later() { " + read + " return new(); } Later(); }\n", false),
                    ("the read moved into a local function returning a tuple", declaration + "\nstatic void W() { (int A, int B) Later() { " + read + " return (0, 0); } Later(); }\n", false),
                    ("the pinned read in a method constrained where T : class, new()", declaration + "\nstatic void W<T>() where T : class, new() { try { " + read + " } catch (Exception) { throw; } }\n", true),
                    ("the pinned read in a property get accessor", declaration + "\nstatic int P { get { try { " + read + " } catch (Exception) { throw; } return 0; } }\n", true),
                    ("an unrelated local function inside an accessor closed before the pinned read", declaration + "\nstatic int P { get { int Unrelated() { return 0; } return Unrelated(); } }\nstatic void W() { try { " + read + " } catch (Exception) { throw; } }\n", true),
                    ("the pinned read in a method of a generic class constrained where T : class", declaration + "\nsealed class C<T> where T : class { static void W() { try { " + read + " } catch (Exception) { throw; } } }\n", true),
                    // P3-37: local functions inside accessors of properties carrying required/virtual/override/sealed
                    // modifiers or explicit interface members, init/add/remove accessors, where T : struct local functions,
                    // constructors (incl. static and with a this(...) initializer), finalizers and switch-expression-arm
                    // lambdas; and the pinned read directly in such a member stays clean.
                    ("the read moved into a local function inside a required property init accessor", declaration + "\nsealed class C { public required int P { get => 0; init { void Later() { " + read + " } Later(); } } }\n", false),
                    ("the read moved into a local function inside an explicit interface get accessor", declaration + "\nsealed class C : I { int I.P { get { void Later() { " + read + " } Later(); return 0; } } }\n", false),
                    ("the read moved into a local function inside an explicit interface indexer get accessor", declaration + "\nsealed class C : I { int I.this[int n] { get { void Later() { " + read + " } Later(); return n; } } }\n", false),
                    ("the read moved into a local function inside an init accessor", declaration + "\nsealed class C { int P { get => 0; init { void Later() { " + read + " } Later(); } } }\n", false),
                    ("the read moved into a local function inside an event add accessor", declaration + "\nsealed class C { event Action E { add { void Later() { " + read + " } Later(); } remove { } } }\n", false),
                    ("the read moved into a local function inside an event remove accessor", declaration + "\nsealed class C { event Action E { add { } remove { void Later() { " + read + " } Later(); } } }\n", false),
                    ("the read moved into a local function inside an explicit interface event add accessor", declaration + "\nsealed class C : I { event Action I.E { add { void Later() { " + read + " } Later(); } remove { } } }\n", false),
                    ("the read moved into a local function constrained where T : struct", declaration + "\nstatic void W() { void Later<T>() where T : struct { " + read + " } Later<int>(); }\n", false),
                    ("the read moved into a local function inside a method constrained where T : struct", declaration + "\nstatic void W<T>() where T : struct { void Later() { " + read + " } Later(); }\n", false),
                    ("the read moved into a local function inside a constructor", declaration + "\nsealed class C { public C() { void Later() { " + read + " } Later(); } }\n", false),
                    ("the read moved into a local function inside a constructor with a this() initializer", declaration + "\nsealed class C { public C(int n) : this() { void Later() { " + read + " } Later(); } C() { } }\n", false),
                    ("the read moved into a local function inside a static constructor", declaration + "\nsealed class C { static C() { void Later() { " + read + " } Later(); } }\n", false),
                    ("the read moved into a local function inside a finalizer (planted)", declaration + "\nclass C { ~C() { void Later() { " + read + " } Later(); } }\n", false),
                    ("the read moved into a block lambda in a switch-expression arm", declaration + "\nstatic void W(int n) { Action later = n switch { 0 => () => { " + read + " }, _ => () => { } }; later(); }\n", false),
                    ("the read moved into a local function inside a switch-expression-arm lambda", declaration + "\nstatic void W(int n) { Action later = n switch { _ => () => { void Later() { " + read + " } Later(); } }; later(); }\n", false),
                    ("the pinned read in a constructor", declaration + "\nsealed class C { public C() { try { " + read + " } catch (Exception) { throw; } } }\n", true),
                    ("the pinned read in a static constructor", declaration + "\nsealed class C { static C() { try { " + read + " } catch (Exception) { throw; } } }\n", true),
                    ("the pinned read in a finalizer", declaration + "\nclass C { ~C() { try { " + read + " } catch (Exception) { throw; } } }\n", true),
                    ("the pinned read in an override property get accessor", declaration + "\nclass C : B { public override int P { get { try { " + read + " } catch (Exception) { throw; } return 0; } } }\n", true),
                    ("the pinned read in an init accessor", declaration + "\nsealed class C { int P { get => 0; init { try { " + read + " } catch (Exception) { throw; } } } }\n", true),
                    ("the pinned read in an event add accessor", declaration + "\nsealed class C { event Action E { add { try { " + read + " } catch (Exception) { throw; } } remove { } } }\n", true),
                    ("the pinned read in a method constrained where T : struct", declaration + "\nstatic void W<T>() where T : struct { try { " + read + " } catch (Exception) { throw; } }\n", true),
                    ("an unrelated switch-expression-arm lambda closed before the pinned read", declaration + "\nstatic void W(int n) { Action unrelated = n switch { _ => () => { } }; try { " + read + " } catch (Exception) { throw; } }\n", true),
                    ("an unrelated local function inside a finalizer closed before the pinned read", declaration + "\nclass C { ~C() { void Unrelated() { } Unrelated(); } }\nstatic void W() { try { " + read + " } catch (Exception) { throw; } }\n", true),
                    // P3-39: constructors with a : base(...) initializer (also one whose arguments create an object), methods
                    // carrying the `new` hiding modifier, checked operators, members of primary-constructor types, and
                    // lambdas, local functions and anonymous methods in field or property initializers.
                    ("the read moved into a local function inside a constructor with a base(n) initializer", declaration + "\nclass C : B { public C(int n) : base(n) { void Later() { " + read + " } Later(); } }\n", false),
                    ("the read moved into a local function inside a constructor with a base(new object()) initializer (planted)", declaration + "\nclass C : B { public C() : base(new object()) { void Later() { " + read + " } Later(); } }\n", false),
                    ("the read moved into a local function inside a new-modifier (hiding) method (planted)", declaration + "\nclass C : B { public new void M() { void Later() { " + read + " } Later(); } }\n", false),
                    ("the read moved into a local function inside a protected new static method returning int[] (planted)", declaration + "\nclass C : B { protected new static int[] M() { void Later() { " + read + " } Later(); return null; } }\n", false),
                    ("the read moved into a local function inside an operator checked + body", declaration + "\nsealed class C { public static C operator checked +(C a, C b) { void Later() { " + read + " } Later(); return a; } public static C operator +(C a, C b) => a; }\n", false),
                    ("the read moved into a local function inside an explicit operator checked int body", declaration + "\nsealed class C { public static explicit operator checked int(C c) { void Later() { " + read + " } Later(); return 0; } public static explicit operator int(C c) => 0; }\n", false),
                    ("the read moved into a local function inside a method of a primary-constructor class", declaration + "\nsealed class C(int n) { int M() { void Later() { " + read + " } Later(); return n; } }\n", false),
                    ("the read moved into a local function inside an accessor of a primary-constructor record with a base", declaration + "\nrecord R(int N) : B(N) { int P { get { void Later() { " + read + " } Later(); return N; } } }\n", false),
                    ("the read moved into a local function inside a this(0) constructor of a primary-constructor struct", declaration + "\nreadonly struct S(int n) { public S() : this(0) { void Later() { " + read + " } Later(); } }\n", false),
                    ("the read moved into a block lambda in a static field initializer", declaration + "\nstatic readonly Action Later = () => { " + read + " };\n", false),
                    ("the read moved into a local function inside a field-initializer lambda", declaration + "\nstatic readonly Action Later = () => { void Inner() { " + read + " } Inner(); };\n", false),
                    ("the read moved into a block lambda in a property initializer", declaration + "\nstatic Action P { get; } = () => { " + read + " };\n", false),
                    ("the read moved into an anonymous method in a field initializer", declaration + "\nstatic readonly Action Later = delegate { " + read + " };\n", false),
                    ("the read moved into an expression lambda in a field initializer", declaration + "\nstatic readonly Action Later = () => " + read + "\n", false),
                    ("the pinned read in a constructor with a base(new object()) initializer", declaration + "\nclass C : B { public C() : base(new object()) { try { " + read + " } catch (Exception) { throw; } } }\n", true),
                    ("the pinned read in an operator checked + body", declaration + "\nsealed class C { public static C operator checked +(C a, C b) { try { " + read + " } catch (Exception) { throw; } return a; } public static C operator +(C a, C b) => a; }\n", true),
                    ("the pinned read in a method of a primary-constructor class", declaration + "\nsealed class C(int n) { void M() { try { " + read + " } catch (Exception) { throw; } } }\n", true),
                    ("the pinned read in a new-modifier (hiding) method", declaration + "\nclass C : B { public new void M() { try { " + read + " } catch (Exception) { throw; } } }\n", true),
                    ("an unrelated field-initializer lambda closed before the pinned read", declaration + "\nstatic readonly Action Unrelated = () => { };\nstatic void W() { try { " + read + " } catch (Exception) { throw; } }\n", true),
                    // P3-41: the P3-39 limit BracedInitializerArguments is removed: a constructor initializer whose arguments
                    // hold braces (`: this(new[] { n })`, `: base(() => { })`, even with a ';' inside) no longer splits the
                    // constructor header. New-modifier (hiding) methods with a tuple or ref/ref readonly return type.
                    ("the read moved into a local function inside a constructor with a this(new[] { n }) initializer (planted)", declaration + "\nsealed class C { public C(int n) : this(new[] { n }) { void Later() { " + read + " } Later(); } C(int[] a) { } }\n", false),
                    ("the read moved into a local function inside a constructor with a base(() => { }) initializer (planted)", declaration + "\nclass C : B { public C() : base(() => { }) { void Later() { " + read + " } Later(); } }\n", false),
                    ("the read moved into a local function inside a constructor with a base(() => { return; }) initializer (planted)", declaration + "\nclass C : B { public C() : base(() => { return; }) { void Later() { " + read + " } Later(); } }\n", false),
                    ("the read moved into a local function inside a new-modifier method returning a tuple (planted)", declaration + "\nclass C : B { public new (int, int) M() { void Later() { " + read + " } Later(); return (0, 0); } }\n", false),
                    ("the read moved into a local function inside a new-modifier method returning ref int (planted)", declaration + "\nclass C : B { int f; public new ref int M() { void Later() { " + read + " } Later(); return ref f; } }\n", false),
                    ("the read moved into a local function inside a protected new static method returning ref readonly int (planted)", declaration + "\nclass C : B { static int f; protected new static ref readonly int M() { void Later() { " + read + " } Later(); return ref f; } }\n", false),
                    ("the pinned read in a constructor with a this(new[] { n }) initializer", declaration + "\nsealed class C { public C(int n) : this(new[] { n }) { try { " + read + " } catch (Exception) { throw; } } C(int[] a) { } }\n", true),
                    ("the pinned read in a new-modifier method returning a tuple", declaration + "\nclass C : B { public new (int, int) M() { try { " + read + " } catch (Exception) { throw; } return (0, 0); } }\n", true),
                    ("the pinned read in a new-modifier method returning ref int", declaration + "\nclass C : B { int f; public new ref int M() { try { " + read + " } catch (Exception) { throw; } return ref f; } }\n", true),
                    // P3-43: braces or nested brackets inside a bracketed attribute argument before a seam-reading header
                    // (`[A(new[] { 1 })]`, `[A(typeof(int[]))]`), a collection expression holding a block lambda in a
                    // constructor initializer or a statement before the header, a new-modifier method returning a generic
                    // type containing a tuple or a pointer, local functions in for (;;) / using (...) bodies; clean controls
                    // (incl. the ref readonly new-modifier one P3-41 lacked).
                    ("the read moved into a local function carrying [A(new[] { 1 })] (planted)", declaration + "\nstatic void W() { [A(new[] { 1 })] void Later() { " + read + " } Later(); }\n", false),
                    ("the read moved into a local function inside a method carrying [A(new[] { 1 })] (planted)", declaration + "\n[A(new[] { 1 })] static void W() { void Later() { " + read + " } Later(); }\n", false),
                    ("the read moved into a local function carrying [A(typeof(int[]))] (planted)", declaration + "\nstatic void W() { [A(typeof(int[]))] void Later() { " + read + " } Later(); }\n", false),
                    ("the read moved into a local function inside a get accessor carrying [A(new[] { 1 })] (planted)", declaration + "\nstatic int P { [A(new[] { 1 })] get { void Later() { " + read + " } Later(); return 0; } }\n", false),
                    ("the read moved into a local function inside a constructor with a this([() => { return; }]) initializer", declaration + "\nsealed class C { public C() : this([() => { return; }]) { void Later() { " + read + " } Later(); } C(Action[] a) { } }\n", false),
                    ("the read moved into a local function after a collection expression holding a block lambda", declaration + "\nstatic void W() { Action[] xs = [() => { return; }]; void Later() { " + read + " } Later(); }\n", false),
                    ("the read moved into a local function inside a new-modifier method returning List<(int, int)> (planted)", declaration + "\nclass C : B { public new List<(int, int)> M() { void Later() { " + read + " } Later(); return null; } }\n", false),
                    ("the read moved into a local function inside a new-modifier method returning a pointer (planted)", declaration + "\nclass C : B { public new unsafe int* M() { void Later() { " + read + " } Later(); return null; } }\n", false),
                    ("the read moved into a local function inside a for (;;) body", declaration + "\nstatic void W() { for (;;) { void Later() { " + read + " } Later(); break; } }\n", false),
                    ("the read moved into a local function inside a using (...) body", declaration + "\nstatic void W() { using (var s = new MemoryStream()) { void Later() { " + read + " } Later(); } }\n", false),
                    ("the pinned read in a method carrying [A(new[] { 1 })]", declaration + "\n[A(new[] { 1 })] static void W() { try { " + read + " } catch (Exception) { throw; } }\n", true),
                    ("the pinned read after a collection expression holding a block lambda", declaration + "\nstatic void W() { Action[] xs = [() => { return; }]; try { " + read + " } catch (Exception) { throw; } }\n", true),
                    ("the pinned read in a new-modifier method returning List<(int, int)>", declaration + "\nclass C : B { public new List<(int, int)> M() { try { " + read + " } catch (Exception) { throw; } return null; } }\n", true),
                    ("the pinned read in a new-modifier method returning ref readonly int", declaration + "\nclass C : B { static int f; protected new static ref readonly int M() { try { " + read + " } catch (Exception) { throw; } return ref f; } }\n", true),
                    ("the pinned read in a for (;;) body", declaration + "\nstatic void W() { for (;;) { try { " + read + " } catch (Exception) { throw; } break; } }\n", true),
                    ("the pinned read in a using (...) body", declaration + "\nstatic void W() { using (var s = new MemoryStream()) { try { " + read + " } catch (Exception) { throw; } } }\n", true),
                    // P3-45: a new-modifier method returning a generic nested three deep around a tuple, an unsafe local
                    // function returning a pointer without `new`, attribute sections not at the start of a header (on a
                    // parameter or a type parameter, holding braces and `new[]`), and clean controls incl. a multiplication
                    // in an if/for header.
                    ("the read moved into a local function inside a new-modifier method returning Dictionary<A<B<(int, int)>>, C> (planted)", declaration + "\nclass C : B { public new Dictionary<A<B<(int, int)>>, C> M() { void Later() { " + read + " } Later(); return null; } }\n", false),
                    ("the read moved into an unsafe local function returning int*", declaration + "\nstatic void W() { unsafe int* Later() { " + read + " return null; } Later(); }\n", false),
                    ("the read moved into a local function with a parameter carrying [A(new[] { 1 })] (planted)", declaration + "\nstatic void W() { void Later([A(new[] { 1 })] int n) { " + read + " } Later(0); }\n", false),
                    ("the read moved into a local function with a type parameter carrying [A(new[] { 1 })] (planted)", declaration + "\nstatic void W() { void Later<[A(new[] { 1 })] T>() { " + read + " } Later<int>(); }\n", false),
                    ("the pinned read in a new-modifier method returning Dictionary<A<B<(int, int)>>, C>", declaration + "\nclass C : B { public new Dictionary<A<B<(int, int)>>, C> M() { try { " + read + " } catch (Exception) { throw; } return null; } }\n", true),
                    ("the pinned read in a method with a parameter carrying [A(new[] { 1 })]", declaration + "\nstatic void W([A(new[] { 1 })] int n) { try { " + read + " } catch (Exception) { throw; } }\n", true),
                    ("the pinned read in an if (n * n > 0) body", declaration + "\nstatic void W(int n) { if (n * n > 0) { try { " + read + " } catch (Exception) { throw; } } }\n", true),
                    ("the pinned read in a for (var i = 0; i * i < n; i++) body", declaration + "\nstatic void W(int n) { for (var i = 0; i * i < n; i++) { try { " + read + " } catch (Exception) { throw; } } }\n", true),
                    // P3-47: a second attribute section on the same parameter (stacked sections collapse together), a
                    // collection expression after '(' in an if header (InnerAttributes collapses it too: harmless, no header
                    // test reads call arguments), a field-initializer collection expression holding two block lambdas (the
                    // split HeaderStart limit: LambdaHeader reads the unsplit prefix), and a multiplication in a default
                    // parameter value, which reaches MethodHeader (the name is not a statement keyword).
                    ("the read moved into a local function with a parameter carrying [A] [B(new[] { 1 })] (planted)", declaration + "\nstatic void W() { void Later([A] [B(new[] { 1 })] int n) { " + read + " } Later(0); }\n", false),
                    ("the read moved into a local function inside an if (F([1, 2]).Length > 0) body", declaration + "\nstatic void W() { if (F([1, 2]).Length > 0) { void Later() { " + read + " } Later(); } }\n", false),
                    ("the read moved into the second block lambda of a field-initializer collection expression", declaration + "\nstatic Action[] Xs = [() => { return; }, () => { " + read + " }];\n", false),
                    ("the read moved into a local function with a default parameter value 2 * 3", declaration + "\nstatic void W() { void Later(int n = 2 * 3) { " + read + " } Later(); }\n", false),
                    ("the pinned read in a method with a parameter carrying [A] [B(new[] { 1 })]", declaration + "\nstatic void W([A] [B(new[] { 1 })] int n) { try { " + read + " } catch (Exception) { throw; } }\n", true),
                    ("the pinned read in an if (F([1, 2]).Length > 0) body", declaration + "\nstatic void W() { if (F([1, 2]).Length > 0) { try { " + read + " } catch (Exception) { throw; } } }\n", true),
                    ("the pinned read after a field-initializer collection expression holding two block lambdas", declaration + "\nstatic Action[] Xs = [() => { return; }, () => { }];\nstatic void W() { try { " + read + " } catch (Exception) { throw; } }\n", true),
                    ("the pinned read in a method with a default parameter value 2 * 3", declaration + "\nstatic void W(int n = 2 * 3) { try { " + read + " } catch (Exception) { throw; } }\n", true),
                    // P3-49: a multiplication outside the parameter list, in a leading attribute argument (`[A(2 * 3)]`:
                    // the header reaches MethodHeader with a method name, not a statement keyword), a default value
                    // `a * b`, and a stacked bracket run followed by an indexer in a call (`F([1] [0])`, collapsed to one
                    // `[]` by InnerAttributes: harmless, no header test reads call arguments). All were caught or clean
                    // when added, so none carries the (planted) mark.
                    ("the read moved into a local function carrying [A(2 * 3)]", declaration + "\nstatic void W() { [A(2 * 3)] void Later() { " + read + " } Later(); }\n", false),
                    ("the read moved into a local function with a default parameter value a * b", declaration + "\nstatic void W(int a, int b) { void Later(int n = a * b) { " + read + " } Later(); }\n", false),
                    ("the read moved into a local function inside an if (F([1] [0]) > 0) body", declaration + "\nstatic void W() { if (F([1] [0]) > 0) { void Later() { " + read + " } Later(); } }\n", false),
                    ("the pinned read in a method carrying [A(2 * 3)]", declaration + "\n[A(2 * 3)] static void W() { try { " + read + " } catch (Exception) { throw; } }\n", true),
                    ("the pinned read in a method with a default parameter value a * b", declaration + "\nstatic void W(int n = a * b) { try { " + read + " } catch (Exception) { throw; } }\n", true),
                    ("the pinned read in an if (F([1] [0]) > 0) body", declaration + "\nstatic void W() { if (F([1] [0]) > 0) { try { " + read + " } catch (Exception) { throw; } } }\n", true),
                };
                // P3-37's virtual/override/sealed accessor plants, folded into one table (P3-39). Their distinct value is
                // narrow: MethodBody recognises an accessor from its own header text (`get`, `set`, `protected set`) only,
                // so the property's modifiers never reach it; the rows guard against a future change that starts reading
                // the property header (and the class base list `: B`).
                foreach (var (modifiers, accessors) in new[]
                {
                    ("virtual", "get { void Later() { " + read + " } Later(); return 0; }"),
                    ("override", "get => 0; set { void Later() { " + read + " } Later(); }"),
                    ("sealed override", "get => 0; protected set { void Later() { " + read + " } Later(); }"),
                })
                    cases.Add(("the read moved into a local function inside a " + modifiers + " property accessor", declaration + "\nclass C : B { public " + modifiers + " int P { " + accessors + " } }\n", false));
                if (full)
                {
                    var other = type == "Action" ? "internal static Action<string>? " + seam + " { get; set; }" : "internal static Action? " + seam + " { get; set; }";
                    var bareRead = type == "Action" ? "var p = " + seam + "; p?.Invoke();" : "var p = " + seam + "; p?.Invoke(output);";
                    cases.AddRange(new (string Name, string Source, bool Clean)[]
                    {
                        ("assignments only inside comments", good + "// " + seam + " = null;\n/* " + seam + " ??= x; */\n", true),
                        ("an assignment", good + "static void T() { " + seam + " = null; }\n", false),
                        ("a qualified assignment without spaces", good + "static void T() { Owner." + seam + "=null; }\n", false),
                        ("a ??=", good + "static void T() { " + seam + " ??= default; }\n", false),
                        ("a +=", good + "static void T() { " + seam + " += _ => { }; }\n", false),
                        ("a parenthesized assignment", good + "static void T() { (" + seam + ") = null; }\n", false),
                        ("an initializer", declaration + " = default;\nstatic void W() { " + read + " }\n", false),
                        ("another declared type", other + "\nstatic void W() { " + read + " }\n", false),
                        ("a second pinned read", good + "static void T() { " + read + " }\n", false),
                        ("a read in another form", good + "static void T() { " + bareRead + " }\n", false),
                        ("the read missing", declaration + "\n", false),
                        ("the name inside a string literal", good + "const string S = \"" + seam + " = null;\";\n", false),
                        ("an assignment in an interpolation hole (evasion)", good + "static void T() { var s = $\"{" + seam + " = null}\"; }\n", false),
                        ("an assignment inside a raw string literal", good + "static void T() { var s = \"\"\"\n" + seam + " = null;\n\"\"\"; }\n", false),
                        ("an assignment after a raw string holding '\"' and '//' (lexer gap)", good + "static void T() { var s = \"\"\" a \" // \"\"\"; " + seam + " = null; }\n", false),
                        ("an assignment after @$\"\\\" + \"//\" (lexer gap)", good + "static void T() { var s = @$\"\\\" + \"//\"; " + seam + " = null; }\n", false),
                    });
                    if (type == "Action")
                        cases.Add(("the read with an argument instead", good.Replace(read, seam + "?.Invoke(output);", StringComparison.Ordinal), false));
                    else
                        cases.AddRange(new (string Name, string Source, bool Clean)[]
                        {
                            ("the read without its argument instead", good.Replace(read, seam + "?.Invoke();", StringComparison.Ordinal), false),
                            ("the read with another argument instead", good.Replace(read, seam + "?.Invoke(path);", StringComparison.Ordinal), false),
                        });
                }
                foreach (var (name, source, clean) in cases)
                {
                    if (name.Contains("(planted)", StringComparison.Ordinal))
                    {
                        if (clean || !name.EndsWith(Planted, StringComparison.Ordinal)) misplacedMarks.Add(seam + ": " + name);
                        else plantedNames.Add(name[..^Planted.Length]);
                    }
                    var uses = SeamHygiene(source, seam, type);
                    Check(uses.Clean == clean, "P3-29 seam hygiene self-test: " + seam + ": " + name + (clean ? " is clean" : " is caught"), uses.Detail);
                    selfTests++;
                }
            }
            // P3-51: the (planted) marks seen on the cases are exactly the pinned per-row lists (a mark only at the end of
            // a caught case's label), and the lists hold 34 distinct labels.
            var pinnedPlanted = plantedByRow.SelectMany(r => r.Labels).ToArray();
            Check(misplacedMarks.Count == 0 && pinnedPlanted.Length == 34 && pinnedPlanted.Distinct(StringComparer.Ordinal).Count() == pinnedPlanted.Length && plantedNames.SetEquals(pinnedPlanted),
                "P3-29 seam hygiene: the (planted) marks are exactly the 34 pinned labels, each a visible failure in its row's red run ("
                    + string.Join(", ", plantedByRow.Select(r => r.Row + " red " + r.RedRun.ToString(CultureInfo.InvariantCulture) + ": " + r.Labels.Length.ToString(CultureInfo.InvariantCulture))) + ")",
                "marked but not pinned [" + string.Join(" | ", plantedNames.Except(pinnedPlanted, StringComparer.Ordinal)) + "] pinned but not marked ["
                    + string.Join(" | ", pinnedPlanted.Except(plantedNames, StringComparer.Ordinal)) + "] misplaced [" + string.Join(" | ", misplacedMarks) + "] pinned "
                    + pinnedPlanted.Length.ToString(CultureInfo.InvariantCulture));
            // P3-53: AnnotationCap: no row pins more labels than one step's error annotations can show. P3-55: the 10 is the
            // constant AnnotationCap, and the cap note above states it (checked against this constant); the largest row
            // pins 8 labels (asserted); every job runs tests/cli from at most one step (checked on ci.yml and verify.ps1).
            // P3-57: the cap note is wrapped to the file's column width (ColumnWidth, the widest comment lines here): its
            // first line ends at "per step" and the next opens with "(and 50 per job", checked verbatim; every line of the
            // note (up to the first evidence line) fits ColumnWidth.
            const int AnnotationCap = 10, ColumnWidth = 125;
            var helpers = File.ReadAllText(Path.Combine(root, "tests", "cli", "InternalErrorDelegationTests.cs"));
            var capNote = "named limit AnnotationCap: GitHub shows at most " + AnnotationCap.ToString(CultureInfo.InvariantCulture) + " error, "
                + AnnotationCap.ToString(CultureInfo.InvariantCulture) + " warning and " + AnnotationCap.ToString(CultureInfo.InvariantCulture) + " notice annotations per step";
            var capNoteLines = helpers.ReplaceLineEndings("\n").Split('\n').SkipWhile(l => l.Trim() != "// P3-55: " + capNote).TakeWhile(l => !l.TrimStart().StartsWith("//   P3-29 red", StringComparison.Ordinal)).ToArray();
            Check(helpers.ReplaceLineEndings("\n").Contains("        // P3-55: " + capNote + "\n        // (and 50 per job", StringComparison.Ordinal)
                    && capNoteLines.Length > 1 && capNoteLines.All(l => l.Length <= ColumnWidth),
                "P3-29 seam hygiene: the cap note states [" + capNote + "] then [(and 50 per job] on its next line from the constant AnnotationCap, every line within "
                    + ColumnWidth.ToString(CultureInfo.InvariantCulture) + " columns",
                string.Join(", ", capNoteLines.Select(l => l.Length.ToString(CultureInfo.InvariantCulture))));
            Check(plantedByRow.All(r => r.Labels.Length <= AnnotationCap) && plantedByRow.Max(r => r.Labels.Length) == 8,
                "P3-29 seam hygiene: named limit AnnotationCap: every plantedByRow row pins at most " + AnnotationCap.ToString(CultureInfo.InvariantCulture)
                    + " labels (one step's error annotations) and the largest row pins 8",
                string.Join(", ", plantedByRow.Select(r => r.Row + ": " + r.Labels.Length.ToString(CultureInfo.InvariantCulture))));
            var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml")).ReplaceLineEndings("\n");
            var jobsAt = workflow.IndexOf("\njobs:\n", StringComparison.Ordinal);
            var jobStarts = jobsAt < 0 ? new List<(string Name, int At)>()
                : System.Text.RegularExpressions.Regex.Matches(workflow[jobsAt..], @"^  ([\w-]+):[ \t]*$", System.Text.RegularExpressions.RegexOptions.Multiline)
                    .Select(m => (Name: m.Groups[1].Value, At: jobsAt + m.Index)).ToList();
            // P3-57: named limit CliInvocationKeys: a ci.yml step runs tests/cli when one line of it holds `dotnet run` or
            // `dotnet test` with --project naming tests/cli (also ./tests/cli, a trailing slash or tests/cli/Bim.Cli.Tests.csproj,
            // either slash), or scripts/verify.ps1 (also ./scripts/verify.ps1). The keys are textual: a step reaching tests/cli
            // through a variable, another script, a solution or a command split over lines is not counted (self-tests below).
            static int CliSteps(string text) => System.Text.RegularExpressions.Regex.Matches(text, @"\bdotnet\s+(?:run|test)\b[^\n]*?\s--project\s+[""']?(?:\./)?tests[/\\]cli(?:[/\\](?:Bim\.Cli\.Tests\.csproj)?)?(?![\w/\\.-])|(?<![\w/.-])(?:\./)?scripts/verify\.ps1\b").Count;
            var testSteps = jobStarts.Select((job, i) => (job.Name, Count: CliSteps(workflow[job.At..(i + 1 < jobStarts.Count ? jobStarts[i + 1].At : workflow.Length)]))).ToArray();
            var verifyRuns = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(root, "scripts", "verify.ps1")), @"""\$root/tests/cli""").Count;
            var stepCounts = string.Join(", ", testSteps.Select(j => j.Name + ": " + j.Count.ToString(CultureInfo.InvariantCulture)));
            Check(testSteps.All(j => j.Count <= 1),
                "P3-29 seam hygiene: named limit AnnotationCap: each ci.yml job runs tests/cli from at most one step (the cap note's one-step-per-job assumption)", stepCounts);
            Check(testSteps.Sum(j => j.Count) == 3,
                "P3-29 seam hygiene: named limit AnnotationCap: ci.yml runs tests/cli from 3 steps (core and core-windows through verify.ps1, ifc-portable on macOS directly); on a change, revisit the cap note (P3-55, named limit AnnotationCap) and this count",
                stepCounts);
            Check(verifyRuns == 1,
                "P3-29 seam hygiene: named limit AnnotationCap: verify.ps1 runs tests/cli once; on a change, revisit the cap note (P3-55, named limit AnnotationCap)",
                "verify.ps1 runs " + verifyRuns.ToString(CultureInfo.InvariantCulture));
            foreach (var (step, expected, why) in new[]
            {
                ("run: dotnet run --project ./tests/cli --configuration Release --no-build", 1, "a ./ project path"),
                ("run: dotnet run --configuration Release --project tests/cli/Bim.Cli.Tests.csproj", 1, "the project file, --project after other options"),
                ("run: pwsh -NoProfile -File scripts/verify.ps1", 1, "verify.ps1 without ./"),
                ("run: dotnet build tests/cli --configuration Release --warnaserror", 0, "a build, not a run"),
                ("run: dotnet run --project tests/cli-extra", 0, "another project"),
                ("run: dotnet run --project \"$CLI_TESTS\" --configuration Release", 0, "named limit CliInvocationKeys: a variable is not read"),
            })
            {
                Check(CliSteps(step) == expected, "P3-29 seam hygiene self-test: CliSteps counts [" + step + "] as " + expected.ToString(CultureInfo.InvariantCulture) + " tests/cli step (" + why + ")",
                    CliSteps(step).ToString(CultureInfo.InvariantCulture));
                selfTests++;
            }
            // 8 lambda/anonymous-method cases plus 8 local-function/attributed-lambda cases (P3-33) plus 14
            // accessor/operator/constrained-generic/tuple-return cases (P3-35) plus 27 accessor-modifier/member-kind
            // cases (P3-37, 3 of them now the folded table) plus 21 base-initializer/new-modifier/checked-operator/
            // primary-constructor/field-initializer cases (P3-39: 14 caught, 5 clean controls; its 2 pinned limits are
            // now caught) plus 7 braced-initializer/tuple-or-ref-new-modifier cases (P3-41: 4 caught, 3 clean controls)
            // for each of the 9 seams; 16 more plus 1 (Action) or 2 (Action<string>) form cases for each of the four new
            // seams; plus 16 bracket/collection-expression/generic-tuple-or-pointer/for-using header cases (P3-43: 10 caught,
            // 6 clean controls) for each of the 9 seams and its 2 header-span pins; plus 8 deeper-generic/pointer/inner-
            // attribute/multiplication header cases (P3-45: 4 caught, 4 clean controls) plus 8 stacked-attribute/collection-
            // expression/default-multiplication header cases (P3-47: 4 caught, 4 clean controls) plus 6 attribute-argument-
            // multiplication/default-a * b/stacked-run-then-indexer header cases (P3-49: 3 caught, 3 clean controls) for each
            // of the 9 seams; plus 3 header-span pins and 6 ReducedHeader pins: 1186. P3-51: plus 2 order-sensitive
            // ReducedHeader pins, 1 LeadingInnerCommute named-limit self-test and 2 StarOperand named-limit pins: 1191.
            // P3-53: plus 1 replacement-piece tie pin and 1 changed-string self-test of that tie: 1193. P3-55: plus 1
            // swapped-Replace-lines self-test and 1 PlainLiteralPieces named-limit self-test of the tie: 1195. P3-57: plus 2
            // PlainLiteralPieces self-tests (escaped quote, escaped backslash) and 6 CliSteps self-tests: 1203.
            // P3-43: HeaderStart spans a for (;;) / using (...) header from its keyword (the ';' and the object creation
            // inside the parentheses do not split it). P3-47: named limit BracketedBraces: a '}' inside brackets outside any
            // parentheses (a collection expression holding a block lambda in a field initializer) still splits the header
            // of a later '{' in the same brackets; pinned, harmless because InBlockLambda tests LambdaHeader on the unsplit
            // prefix before it reads HeaderStart (the second-lambda case above).
            foreach (var (code, header) in new[] { ("{ for (;;) {", "for (;;)"), ("{ using (var s = new MemoryStream()) {", "using (var s = new MemoryStream())"),
                ("static Action[] Xs = [() => { return; }, () => {", ", () =>") })
            {
                var open = code.LastIndexOf('{');
                Check(code[HeaderStart(code, open)..open].Trim() == header, "P3-29 seam hygiene self-test: the header of [" + code + "] spans [" + header + "]"
                    + (header.StartsWith(',') ? " (named limit BracketedBraces: split at the '}' inside the brackets)" : ""), code[HeaderStart(code, open)..open]);
                selfTests++;
            }
            // P3-47: InnerAttributes collapses stacked attribute sections on one parameter into one `[]`, and (either way
            // harmless, pinned) a collection expression after '(' or ',' in a call: no header test reads call arguments.
            // P3-49: the pins run through ReducedHeader, the one replace sequence MethodBody applies (LeadingAttributes,
            // then InnerAttributes, then InitializerArguments; MethodBody's source is pinned below to reduce only through
            // it), so a reordering or a dropped replace shows in these pins. A stacked run followed by an indexer in a call
            // (`F([1] [0])`) collapses to one `[]` (pinned, harmless as above); a multiplication in a leading attribute
            // argument is reduced with the section; the last pin exercises all three replaces in one header.
            var reducedPins = new[] { ("void Later([A] [B(new[] { 1 })] int n)", "void Later([] int n)"), ("if (F([1, 2]))", "if (F([]))"), ("F(x, [1, 2])", "F(x, [])"),
                ("if (F([1] [0]) > 0)", "if (F([]) > 0)"), ("[A(2 * 3)] void Later()", "[] void Later()"), ("[A] public C([B] [C(new[] { 1 })] int n) : base(F([1] [0]))", "[] public C([] int n) : base()") };
            foreach (var (header, collapsed) in reducedPins)
            {
                Check(ReducedHeader(header) == collapsed, "P3-29 seam hygiene self-test: ReducedHeader (MethodBody's replace order) reduces [" + header + "] to [" + collapsed + "]", ReducedHeader(header));
                selfTests++;
            }
            // P3-53: the swapped orders below are built from ReducedHeader's own replacement pieces, read from its source
            // (each `<Field>.Replace(header, "<replacement>")` in source order) and applied through the same static Regex
            // fields, so a changed replacement string or field in ReducedHeader changes the swapped chains with it. The tie
            // pin checks that those pieces, applied in source order, reduce every pin header exactly as ReducedHeader does;
            // its self-test reads the pieces from a copy of the source with LeadingAttributes' "[] " changed to "[]" and
            // shows the tie then fails on a pin header.
            var reducerFrom = helpers.LastIndexOf("static string ReducedHeader(string header)", StringComparison.Ordinal);
            var reducerTo = reducerFrom < 0 ? -1 : helpers.IndexOf("\n    }", reducerFrom, StringComparison.Ordinal);
            var reducer = reducerTo < 0 ? "" : helpers[reducerFrom..reducerTo];
            static (string Field, string Replacement)[] ReplacePieces(string source) =>
                System.Text.RegularExpressions.Regex.Matches(source, @"(\w+)\s*\.\s*Replace\s*\(\s*header\s*,\s*""([^""\\]*)""\s*\)")
                    .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToArray();
            static string Chain(IEnumerable<(string Field, string Replacement)> order, string header) =>
                order.Aggregate(header, (h, piece) => typeof(InternalErrorDelegationTests).GetField(piece.Field, Any)?.GetValue(null) is System.Text.RegularExpressions.Regex pattern
                    ? pattern.Replace(h, piece.Replacement) : "<no Regex field " + piece.Field + ">");
            var pieces = ReplacePieces(reducer);
            IEnumerable<(string Field, string Replacement)> Ordered((string Field, string Replacement)[] all, params string[] fields) =>
                fields.SelectMany(f => all.Where(p => p.Field == f));
            // P3-51: order-sensitive pins. Each header reduces differently when InitializerArguments runs before the other
            // replace (the swapped composition is computed and must differ): a bracket run holding ')' inside the
            // initializer arguments balances them only after InnerAttributes, and an initializer-like run inside an unclosed
            // leading section is cut by LeadingAttributes first. Synthetic (not valid C#): they pin the order, nothing more.
            // P3-53: each swap is the field order its pieces are applied in.
            var orderPins = new[]
            {
                ("C(int n) : base(F(, [1)]))", "C(int n) : base()", "InitializerArguments before InnerAttributes", new[] { "LeadingAttributes", "InitializerArguments", "InnerAttributes" }, "C(int n) : base(F(, []))"),
                ("[A(x) : base(] y)", "[] y)", "InitializerArguments before LeadingAttributes", new[] { "InitializerArguments", "InnerAttributes", "LeadingAttributes" }, "[A(x) : base()"),
            };
            var pinHeaders = reducedPins.Select(p => p.Item1).Concat(orderPins.Select(p => p.Item1)).ToArray();
            string Untied((string Field, string Replacement)[] tried) => string.Join(" | ", pinHeaders.Where(h => Chain(tried, h) != ReducedHeader(h)).Select(h => h + " -> " + Chain(tried, h)));
            bool Tied((string Field, string Replacement)[] tried) =>
                tried.Select(p => p.Field).SequenceEqual(new[] { "LeadingAttributes", "InnerAttributes", "InitializerArguments" }, StringComparer.Ordinal) && Untied(tried).Length == 0;
            Check(Tied(pieces),
                "P3-29 seam hygiene self-test: ReducedHeader's replacement pieces read from its source (" + string.Join(", ", pieces.Select(p => p.Field + " -> [" + p.Replacement + "]"))
                    + ") applied in source order reduce all " + pinHeaders.Length.ToString(CultureInfo.InvariantCulture) + " pin headers as ReducedHeader does", Untied(pieces));
            selfTests++;
            var changedReducer = reducer.Replace("LeadingAttributes.Replace(header, \"[] \")", "LeadingAttributes.Replace(header, \"[]\")", StringComparison.Ordinal);
            var changedPieces = ReplacePieces(changedReducer);
            Check(changedReducer != reducer && changedPieces.Length == 3 && changedPieces[0] == ("LeadingAttributes", "[]") && Untied(changedPieces).Length > 0,
                "P3-29 seam hygiene self-test: the replacement-piece tie fails on a copy of ReducedHeader's source with LeadingAttributes' \"[] \" changed to \"[]\"",
                "pieces " + string.Join(", ", changedPieces.Select(p => p.Field + " -> [" + p.Replacement + "]")) + " untied [" + Untied(changedPieces) + "]");
            selfTests++;
            // P3-55: a second changed-string self-test: a copy with InnerAttributes' and InitializerArguments' Replace calls
            // swapped between their lines reads the pieces in the swapped order, and the tie fails on an order pin header.
            const string InnerCall = "InnerAttributes.Replace(header, \"[]\")", InitializerCall = "InitializerArguments.Replace(header, \") : $1()\")";
            var swappedReducer = reducer.Replace(InnerCall, "\u0001", StringComparison.Ordinal).Replace(InitializerCall, InnerCall, StringComparison.Ordinal).Replace("\u0001", InitializerCall, StringComparison.Ordinal);
            var swappedPieces = ReplacePieces(swappedReducer);
            Check(swappedReducer != reducer && swappedPieces.Select(p => p.Field).SequenceEqual(new[] { "LeadingAttributes", "InitializerArguments", "InnerAttributes" }, StringComparer.Ordinal)
                    && Untied(swappedPieces).Length > 0 && !Tied(swappedPieces),
                "P3-29 seam hygiene self-test: the replacement-piece tie fails on a copy of ReducedHeader's source with the InnerAttributes and InitializerArguments Replace lines swapped",
                "pieces " + string.Join(", ", swappedPieces.Select(p => p.Field + " -> [" + p.Replacement + "]")) + " untied [" + Untied(swappedPieces) + "]");
            selfTests++;
            // P3-55: named limit PlainLiteralPieces: ReplacePieces reads only a regular string literal holding no quote or
            // backslash as a replacement (no verbatim @"...", raw or interpolated string, no escaped quote or backslash).
            // A replacement written any other way is not read as a piece, so the piece list loses it and the tie fails
            // loudly (the fields are no longer the three in order); shown on a copy with InnerAttributes' "[]" made @"[]".
            // P3-57: and on copies with InnerAttributes' "[]" holding an escaped quote ("[\"]") or an escaped backslash ("[\\]").
            foreach (var (literal, written) in new[] { ("@\"[]\"", "a verbatim string"), ("\"[\\\"]\"", "an escaped quote"), ("\"[\\\\]\"", "an escaped backslash") })
            {
                var plainReducer = reducer.Replace(InnerCall, "InnerAttributes.Replace(header, " + literal + ")", StringComparison.Ordinal);
                var plainPieces = ReplacePieces(plainReducer);
                Check(plainReducer != reducer && plainPieces.Select(p => p.Field).SequenceEqual(new[] { "LeadingAttributes", "InitializerArguments" }, StringComparer.Ordinal) && !Tied(plainPieces),
                    "P3-29 seam hygiene self-test: named limit PlainLiteralPieces: the replacement-piece tie fails on a copy of ReducedHeader's source with InnerAttributes' \"[]\" written as "
                        + literal + " (" + written + ")",
                    "pieces " + string.Join(", ", plainPieces.Select(p => p.Field + " -> [" + p.Replacement + "]")));
                selfTests++;
            }
            foreach (var (header, reduced, swap, order, swapped) in orderPins)
            {
                var other = Chain(Ordered(pieces, order), header);
                Check(ReducedHeader(header) == reduced && other == swapped && swapped != reduced,
                    "P3-29 seam hygiene self-test: ReducedHeader reduces [" + header + "] to [" + reduced + "], and to [" + swapped + "] with " + swap, ReducedHeader(header) + " / swapped " + other);
                selfTests++;
            }
            // P3-51: named limit LeadingInnerCommute: no output pin catches a swap of LeadingAttributes and InnerAttributes,
            // because on the pinned headers (the 6 ReducedHeader pins and the 2 order pins above, 8 in all) they commute:
            // the swapped order gives the same output on each of them. P3-53: the claim is scoped to those 8 headers, not
            // proved for every header (LeadingAttributes is anchored at the start and InnerAttributes never matches there,
            // which makes a difference unlikely but not impossible, e.g. on unbalanced brackets). The source pin below
            // (ReducedHeader's Replace calls in order) guards the swap.
            var commuted = pinHeaders.Where(h => Chain(Ordered(pieces, "InnerAttributes", "LeadingAttributes", "InitializerArguments"), h) != ReducedHeader(h)).ToArray();
            Check(commuted.Length == 0, "P3-29 seam hygiene self-test: named limit LeadingInnerCommute: InnerAttributes before LeadingAttributes reduces every ReducedHeader pin header the same way", string.Join(" | ", commuted));
            selfTests++;
            // P3-51: named limit StarOperand: MethodHeader reads a lone '*' as a pointer token, so a multiplication followed
            // by a call (`x * F(y)`) reads as a method header returning a pointer. P3-53: the part that admits it is the
            // token class [\w<>,.?:*]+ in MethodHeader's lazy prefix run (?:(?>...|[\w<>,.?:*]+)\s*)*? before the name
            // group (\w+): `x` and `*` are each read as a prefix (type) token, `F` as the name and `(y)` as the parameter
            // list (pinned: the pattern holds that token class). Harmless: no C# statement or member puts a
            // multiplication directly before '{' (an object creation is refused by ObjectCreation, a keyword header by the
            // statement-keyword list), while a spaced pointer return (`unsafe int * Later(int n)`, pinned beside it) is a
            // real local function header that refusing a lone '*' would miss.
            Check(MethodHeader.ToString().Contains(@"|[\w<>,.?:*]+)\s*)*?", StringComparison.Ordinal),
                @"P3-29 seam hygiene: named limit StarOperand: MethodHeader's prefix run holds the token class [\w<>,.?:*]+ that admits a lone '*'", MethodHeader.ToString());
            foreach (var header in new[] { "x * F(y)", "unsafe int * Later(int n)" })
            {
                Check(MethodBody(header), "P3-29 seam hygiene self-test: named limit StarOperand: MethodBody reads [" + header + "] as a method header", header);
                selfTests++;
            }
            Check(selfTests == 9 * (8 + 8 + 14 + 27 + 21 + 7 + 16 + 8 + 8 + 6) + 2 * (16 + 1) + 2 * (16 + 2) + 3 + 6 + 2 + 1 + 2 + 2 + 2 + 2 + 6 && selfTests == 1203, "P3-29 seam hygiene: every self-test ran", selfTests.ToString(CultureInfo.InvariantCulture));
            // P3-43: the balancing-group header regexes (initializer arguments, leading attribute sections) carry a match
            // timeout, so a pathological header fails the check with RegexMatchTimeoutException instead of hanging it.
            // P3-45: so does every regex InBlockLambda and MethodBody use (the lambda/anonymous-method header, the
            // constraint, type-keyword, object-creation, accessor, operator and method header tests, and the inner
            // attribute sections): each is a named static field, every static Regex field of the class carries the 1 s
            // timeout, and the helpers' source (InBlockLambda through MethodBody) calls no static Regex method.
            var headerRegexes = new[] { "InitializerArguments", "LeadingAttributes", "InnerAttributes", "LambdaHeader", "Constraint", "TypeKeyword", "ObjectCreation", "AccessorHeader", "OperatorHeader", "MethodHeader" };
            foreach (var field in headerRegexes)
                Check(typeof(InternalErrorDelegationTests).GetField(field, Any)?.GetValue(null) is System.Text.RegularExpressions.Regex pattern && pattern.MatchTimeout == TimeSpan.FromSeconds(1),
                    "P3-29 seam hygiene: the header regex " + field + " has a 1 s match timeout", (typeof(InternalErrorDelegationTests).GetField(field, Any)?.GetValue(null) as System.Text.RegularExpressions.Regex)?.MatchTimeout.ToString("c", CultureInfo.InvariantCulture) ?? "<missing>");
            var regexFields = typeof(InternalErrorDelegationTests).GetFields(Any).Where(f => f.FieldType == typeof(System.Text.RegularExpressions.Regex)).ToArray();
            Check(regexFields.Length == headerRegexes.Length && regexFields.All(f => headerRegexes.Contains(f.Name, StringComparer.Ordinal)
                    && f.GetValue(null) is System.Text.RegularExpressions.Regex timed && timed.MatchTimeout == TimeSpan.FromSeconds(1)),
                "P3-29 seam hygiene: every static Regex field is a pinned header regex with a 1 s match timeout", string.Join(", ", regexFields.Select(f => f.Name)));
            var helpersFrom = helpers.LastIndexOf("static bool InBlockLambda(", StringComparison.Ordinal);
            var helpersTo = helpersFrom < 0 ? -1 : helpers.IndexOf("static (bool Clean, int Mentions, string Detail) LexerGuard(", helpersFrom, StringComparison.Ordinal);
            var inlineCalls = helpersTo < 0 ? -1 : System.Text.RegularExpressions.Regex.Matches(helpers[helpersFrom..helpersTo], @"\bRegex\s*\.\s*(?:IsMatch|Match|Matches|Replace|Split)\s*\(").Count;
            Check(inlineCalls == 0, "P3-29 seam hygiene: the source from [static bool InBlockLambda(] (last occurrence) to the next [static (bool Clean, int Mentions, string Detail) LexerGuard(] (InBlockLambda, HeaderStart, the header regex fields, MethodBody) calls no static Regex method (every header regex is a timed field)", inlineCalls.ToString(CultureInfo.InvariantCulture));
            // P3-49: MethodBody reduces its header only through ReducedHeader (so the ReducedHeader pins above run its
            // replace order): its source calls ReducedHeader(header) and no Replace of its own.
            var bodyFrom = helpers.LastIndexOf("static bool MethodBody(string header)", StringComparison.Ordinal);
            var bodyTo = bodyFrom < 0 ? -1 : helpers.IndexOf("\n    }", bodyFrom, StringComparison.Ordinal);
            var methodBody = bodyTo < 0 ? "" : helpers[bodyFrom..bodyTo];
            Check(methodBody.Contains("header = ReducedHeader(header);", StringComparison.Ordinal) && !methodBody.Contains(".Replace(", StringComparison.Ordinal),
                "P3-29 seam hygiene: the source from [static bool MethodBody(string header)] (last occurrence) to its closing brace reduces the header only through [header = ReducedHeader(header);] (no Replace call of its own)", methodBody.ReplaceLineEndings(" "));
            // P3-51: ReducedHeader's own source calls Replace exactly on LeadingAttributes, InnerAttributes and
            // InitializerArguments, in that order (the guard for the LeadingInnerCommute swap the output pins cannot see).
            var replaced = System.Text.RegularExpressions.Regex.Matches(reducer, @"(\w+)\s*\.\s*Replace\s*\(").Select(m => m.Groups[1].Value).ToArray();
            Check(replaced.SequenceEqual(new[] { "LeadingAttributes", "InnerAttributes", "InitializerArguments" }, StringComparer.Ordinal),
                "P3-29 seam hygiene: the source from [static string ReducedHeader(string header)] (last occurrence) to its closing brace calls Replace on LeadingAttributes, InnerAttributes, InitializerArguments in that order and nothing else", string.Join(", ", replaced));

            var sep = Path.DirectorySeparatorChar;
            var sources = Directory.GetFiles(Path.Combine(root, "interfaces"), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(sep + "bin" + sep, StringComparison.Ordinal) && !f.Contains(sep + "obj" + sep, StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => (Name: Path.GetRelativePath(root, f).Replace('\\', '/'), Text: File.ReadAllText(f))).ToArray();
            Check(sources.Length > 0, "P3-29 seam hygiene: interfaces/ code found from the repository root", root);
            foreach (var (seam, file, type, read, _) in seams)
            {
                var expected = file.Length > 0 ? new[] { file } : guardedFiles;
                var found = sources.Select(s => (s.Name, Uses: SeamHygiene(s.Text, seam, type))).Where(s => s.Uses.Mentions > 0).ToArray();
                Check(found.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(expected),
                    "P3-29 seam hygiene: " + seam + " appears under interfaces/ only in " + string.Join(", ", expected), string.Join(", ", found.Select(s => s.Name)));
                foreach (var (name, uses) in found)
                    Check(uses.Clean, "P3-29 seam hygiene: " + name + " declares " + seam + " once without an initializer, reads it once as [" + read
                        + "] outside any block lambda, anonymous method or local function, never assigns it", uses.Detail);
            }
        }

        // 2f. MCP audit_verify (AuditVerifyTool.Run, in process): the catch-all forced by an actor list whose enumeration
        // throws (after the path checks and the Count check, inside Actors); the Outcome and stderr equal the hand-written
        // pre-P3-20 catch body.
        {
            var failure = new InvalidOperationException("boom audit_verify");
            var handWritten = Quiet(() =>
            {
                // The pre-P3-20 catch body, verbatim.
                Console.Error.WriteLine("audit_verify internal error: " + failure.GetType().FullName + ": " + failure.Message);
                return new Bim.Mcp.AuditVerifyTool.Outcome(false, Bim.Mcp.AuditVerifyTool.Failure(Bim.Mcp.AuditVerifyTool.InternalError, null));
            });
            Check(handWritten.Value.Text == FailedCode("audit_verify_internal_error") && handWritten.Err == "audit_verify internal error: System.InvalidOperationException: boom audit_verify" + nl && handWritten.Out.Length == 0,
                "fixture: the hand-written MCP audit_verify internal error outcome and stderr", handWritten.Value + " " + Q(handWritten));
            var log = Path.Combine(work, "internal-audit-" + Guid.NewGuid().ToString("N") + ".jsonl");
            var forced = Quiet(() => Bim.Mcp.AuditVerifyTool.Run(log, null, new ThrowingActors(failure), summary: false));
            Check(forced.Value == handWritten.Value && Same(forced, handWritten), "a forced MCP audit_verify internal error returns the hand-written outcome and writes the same stderr",
                forced.Value + " " + Q(forced) + " vs hand-written " + handWritten.Value + " " + Q(handWritten));
        }

        // 3. Static: each catch-all delegates; the stderr fragment has one copy over interfaces/cli.
        var cliDir = Path.Combine(root, "interfaces", "cli");
        string Literal(string file) => ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(cliDir, file)), blankStrings: false);
        static string Seam(string verb) => "Console.Error.WriteLine(IntentPlan.InternalDiagnostic(\"" + verb + "\", error));";
        const string Throws = "catch (Exception error) { throw InternalRefusal(error); }";
        var required = new (string File, string[] Fragments)[]
        {
            ("AuditVerifyCommand.cs", new[] { "catch (Exception error) { IntentPlan.Internal(\"audit-verify\", error).Print(); }" }),
            ("BcfExportCommand.cs", new[] { "catch (Exception error) { IntentPlan.Internal(\"bcf-export\", error).Print(); }" }),
            ("CatalogueReportCommand.cs", new[] { "var failure = IntentPlan.Internal(\"catalogue-report\", error);", "Console.Error.WriteLine(failure.Diagnostic);", "return (failure.Exit, failure.Line);" }),
            // P4-22: Library<T> moved with the dry plan into DocUpdateDryPlan.cs (the other part of DocUpdateCommand).
            ("DocUpdateDryPlan.cs", new[] { Seam("doc-update") }),
            ("DocUpdateExecute.cs", new[] { Seam("doc-update") }),
            ("GenerateCommand.cs", new[] { Throws, Seam("generate-prototypes") }),
            ("GenerateTypologyCommand.cs", new[] { Throws, Seam("generate-typology") }),
            ("PlanLayoutsCommand.cs", new[] { Throws, Seam("plan-layouts") }),
            ("DocRealizedCommand.cs", new[] { Throws, Seam("doc-realized") }),
        };
        foreach (var (file, fragments) in required)
        {
            var code = Literal(file);
            var missing = fragments.Where(f => !code.Contains(f, StringComparison.Ordinal)).ToArray();
            Check(missing.Length == 0, file + " prints its internal error through IntentPlan", "missing [" + string.Join(" | ", missing) + "]");
        }
        foreach (var copy in new[] { " internal error: ", "\" internal error: \"" })
        {
            var copies = Directory.GetFiles(cliDir, "*.cs")
                .Select(f => (Name: Path.GetFileName(f), Count: ApplyIntentTests.CodeOf(File.ReadAllText(f), blankStrings: false).Split(copy).Length - 1))
                .Where(f => f.Count > 0).OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();
            Check(copies.Length == 1 && copies[0] == ("IntentPlan.cs", 1), "the internal-error stderr fragment [" + copy + "] has one copy over all interfaces/cli code, in IntentPlan.cs",
                string.Join(", ", copies.Select(c => c.Name + " " + c.Count.ToString(CultureInfo.InvariantCulture))));
        }

        // P3-20/P3-21: MCP AuditVerifyTool takes its outcome text and stderr line from one IntentPlan.Internal envelope;
        // interfaces/mcp has no copy.
        var mcpDir = Path.Combine(root, "interfaces", "mcp");
        var auditTool = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(mcpDir, "AuditVerifyTool.cs")), blankStrings: false);
        // Layout-independent; tests/mcp/InternalErrorEnvelopeToolTests.cs also ties its Diagnostic and Line to one variable.
        Check(System.Text.RegularExpressions.Regex.IsMatch(auditTool, @"\bIntentPlan\s*\.\s*Internal\s*\(\s*""audit_verify""\s*,\s*error\s*\)"),
            "AuditVerifyTool.cs builds its internal error through IntentPlan.Internal(\"audit_verify\", error)");
        var mcpCopies = Directory.GetFiles(mcpDir, "*.cs")
            .Where(f => ApplyIntentTests.CodeOf(File.ReadAllText(f), blankStrings: false).Contains(" internal error: ", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Check(mcpCopies.Length == 0, "the internal-error stderr fragment [ internal error: ] has no copy in interfaces/mcp code", string.Join(", ", mcpCopies));
    }

    // P3-22: sets the static Action seam, runs body and resets the seam to null in a finally (also when body throws).
    static T WithSeam<T>(PropertyInfo probe, Action seam, Func<T> body)
    {
        probe.SetValue(null, seam);
        try { return body(); }
        finally { probe.SetValue(null, null); }
    }

    static void DeleteTree(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    // P3-22: the uses of GuardedWorkProbe in code with comments stripped and literals blanked (ApplyIntentTests.CodeOf):
    // the declaration without an initializer, the guarded `?.Invoke()` reads, and every other use (assignments named).
    static (int Declarations, int Reads, List<string> Others) ProbeUses(string code)
    {
        int declarations = 0, reads = 0;
        var others = new List<string>();
        foreach (System.Text.RegularExpressions.Match use in System.Text.RegularExpressions.Regex.Matches(code, @"\bGuardedWorkProbe\b"))
        {
            var before = code[..use.Index];
            var after = code[(use.Index + use.Length)..];
            if (System.Text.RegularExpressions.Regex.IsMatch(before, @"\binternal\s+static\s+Action\s*\?\s+$")
                && System.Text.RegularExpressions.Regex.IsMatch(after, @"^\s*\{\s*get\s*;\s*set\s*;\s*\}(?!\s*=)"))
                declarations++;
            else if (System.Text.RegularExpressions.Regex.IsMatch(after, @"^\s*\?\s*\.\s*Invoke\s*\(\s*\)"))
                reads++;
            else
                others.Add((System.Text.RegularExpressions.Regex.IsMatch(after, @"^\s*(\+|-|\?\?)?=(?!=)") ? "assignment: " : "use: ")
                    + code.Substring(use.Index, Math.Min(60, code.Length - use.Index)).ReplaceLineEndings(" "));
        }
        return (declarations, reads, others);
    }

    // P3-22: the hygiene verdict for one source text: exactly one declaration, one guarded read, no other use, and the
    // name's count with literals kept (comments stripped) equal to declarations + reads.
    static (bool Clean, int Mentions, string Detail) Hygiene(string text)
    {
        var uses = ProbeUses(ApplyIntentTests.CodeOf(text, blankStrings: true));
        var kept = System.Text.RegularExpressions.Regex.Matches(ApplyIntentTests.CodeOf(text, blankStrings: false), @"\bGuardedWorkProbe\b").Count;
        var clean = uses.Declarations == 1 && uses.Reads == 1 && uses.Others.Count == 0 && kept == uses.Declarations + uses.Reads;
        var detail = "declarations " + uses.Declarations.ToString(CultureInfo.InvariantCulture) + " reads " + uses.Reads.ToString(CultureInfo.InvariantCulture)
            + " mentions with literals kept " + kept.ToString(CultureInfo.InvariantCulture) + " others [" + string.Join(" | ", uses.Others) + "]";
        return (clean, Math.Max(kept, uses.Declarations + uses.Reads + uses.Others.Count), detail);
    }

    // P3-27: ProbeUses plus Hygiene for a remaining CLI test seam, given its name, declared type (Action or
    // ScoringWeights) and so its pinned read form. A declaration is `internal static <type>? <name> { get; set; }` with
    // no initializer; an Action seam's read is a statement `<name>?.Invoke();` (unqualified, at statement start; P3-29:
    // an Action<string> seam's `<name>?.Invoke(output);`, and neither inside a block lambda or anonymous method; P3-33: nor a
    // block-bodied local function (an expression-bodied one already fails the statement-start test); P3-35: also one
    // inside an accessor or operator body, or with a `where` constraint or a tuple return type in a header; P3-37: also
    // one inside a constructor, finalizer, init/add/remove accessor or switch-expression-arm lambda). Policy: the pinned
    // read placed directly in an accessor, operator, constructor, finalizer or constrained method body (not inside a
    // local function, lambda or anonymous method there) is clean: such a member runs its body when it is invoked, like a
    // method, so the read is still the one seam read;
    // GenerationWeightsProbe's read is the last argument of `new AlternativeOptions(...)` closing
    // `AlternativeGenerator.Generate(...)`. Clean: one declaration, one read, no other use, and the name's count with
    // literals kept (comments stripped) equal to declarations + reads.
    static (bool Clean, int Mentions, string Detail) SeamHygiene(string text, string name, string type)
    {
        var word = @"\b" + System.Text.RegularExpressions.Regex.Escape(name) + @"\b";
        var code = ApplyIntentTests.CodeOf(text, blankStrings: true);
        int declarations = 0, reads = 0;
        var others = new List<string>();
        foreach (System.Text.RegularExpressions.Match use in System.Text.RegularExpressions.Regex.Matches(code, word))
        {
            var before = code[..use.Index];
            var after = code[(use.Index + use.Length)..];
            var read = type switch
            {
                "Action" => System.Text.RegularExpressions.Regex.IsMatch(before, @"[;{}]\s*$") && System.Text.RegularExpressions.Regex.IsMatch(after, @"^\s*\?\s*\.\s*Invoke\s*\(\s*\)\s*;")
                    && !InBlockLambda(code, use.Index),
                "Action<string>" => System.Text.RegularExpressions.Regex.IsMatch(before, @"[;{}]\s*$") && System.Text.RegularExpressions.Regex.IsMatch(after, @"^\s*\?\s*\.\s*Invoke\s*\(\s*output\s*\)\s*;")
                    && !InBlockLambda(code, use.Index),
                _ => System.Text.RegularExpressions.Regex.IsMatch(before, @"\bAlternativeGenerator\s*\.\s*Generate\s*\([^;{}]*\bnew\s+AlternativeOptions\s*\([^;{}]*,\s*$")
                    && System.Text.RegularExpressions.Regex.IsMatch(after, @"^\s*\)\s*\)\s*;"),
            };
            if (System.Text.RegularExpressions.Regex.IsMatch(before, @"\binternal\s+static\s+" + System.Text.RegularExpressions.Regex.Escape(type) + @"\s*\?\s+$")
                && System.Text.RegularExpressions.Regex.IsMatch(after, @"^\s*\{\s*get\s*;\s*set\s*;\s*\}(?!\s*=)"))
                declarations++;
            else if (read)
                reads++;
            else
                others.Add((System.Text.RegularExpressions.Regex.IsMatch(after, @"^\s*(?:[-+*/%&|^]|\?\?|<<|>>)?=(?!=)") ? "assignment: " : "use: ")
                    + code.Substring(use.Index, Math.Min(60, code.Length - use.Index)).ReplaceLineEndings(" "));
        }
        var kept = System.Text.RegularExpressions.Regex.Matches(ApplyIntentTests.CodeOf(text, blankStrings: false), word).Count;
        var clean = declarations == 1 && reads == 1 && others.Count == 0 && kept == declarations + reads;
        var detail = name + ": declarations " + declarations.ToString(CultureInfo.InvariantCulture) + " reads " + reads.ToString(CultureInfo.InvariantCulture)
            + " mentions with literals kept " + kept.ToString(CultureInfo.InvariantCulture) + " others [" + string.Join(" | ", others) + "]";
        return LexerGuard((clean, Math.Max(kept, declarations + reads + others.Count), detail), text, name);
    }

    // P3-29: true when the use at index (in literal-blanked, comment-stripped code) sits, at any depth, inside a block
    // lambda `=> { ... }` or an anonymous method `delegate [(...)] { ... }`: every unmatched '{' enclosing the use is
    // tested. Such a read runs only when the stored or passed delegate is invoked, so it is not an Action seam's read.
    // A lambda with an attribute or an explicit return type (`[A] int () => { ... }`) still ends its header in `=>`.
    // P3-33: also true inside a block-bodied local function: two of the enclosing '{' open method-like bodies
    // (MethodBody), so the inner one is a local function that runs only when it is called.
    static bool InBlockLambda(string code, int index)
    {
        var depth = 0;
        var bodies = 0;
        for (var i = index - 1; i >= 0; i--)
        {
            if (code[i] == '}') depth++;
            else if (code[i] == '{')
            {
                if (depth > 0) { depth--; continue; }
                if (LambdaHeader.IsMatch(code[..i])) return true;
                if (MethodBody(code[HeaderStart(code, i)..i]) && ++bodies == 2) return true;
            }
        }
        return false;
    }

    // P3-41: where the header of the '{' at index starts: after the previous ';', '{' or '}' that is not inside a
    // parenthesised group closed before the '{' (so braces and ';' in `: this(new[] { n })` or `: base(() => { return; })`
    // arguments do not split a constructor header). Outside such a group it is the previous ';', '{' or '}' as before.
    static int HeaderStart(string code, int index)
    {
        var parens = 0;
        for (var i = index - 1; i >= 0; i--)
        {
            var c = code[i];
            if (c == ')') parens++;
            else if (c == '(') parens--;
            else if (parens <= 0 && (c == ';' || c == '{' || c == '}')) return i + 1;
        }
        return 0;
    }

    // P3-33: true when the header of a '{' (the literal-blanked, comment-stripped text since the previous ';', '{' or
    // '}') opens a method, constructor or local function body: `[attributes/modifiers/type] Name[<...>](...) [where ...]`
    // with Name not a statement keyword, and no type-declaration or object-creation keyword in the header.
    // P3-35: the keyword test stops at a constraint clause `where T :` (so `where T : class`/`new()`/`struct` do not hide a
    // generic method or local function); a property/indexer/event accessor (`[attributes] [modifiers] get|set|init|add|remove`)
    // and an operator (`... operator <token>(...)`, before any constraint) open method-like bodies too. A generic tuple
    // return type (`List<(int, int)> F()`, `(int A, int B) F()`) already matched (parenthesised header token).
    // P3-37: a finalizer header `~C()` opens a method-like body too (an optional '~' before the name). Accessors of
    // required/virtual/override/sealed or explicit interface members (`int I.P { get { ... } }`), init/add/remove
    // accessors, `where T : struct`, constructors (static, or with a `: this(...)`/`: base(...)` initializer) and
    // switch-expression-arm lambdas (`=> () => { ... }`) already matched.
    // P3-39: the keyword test skips a trailing constructor initializer `) : base(...)`/`) : this(...)` (so
    // `: base(new object())` does not hide the constructor), and `new` disqualifies a header only as object creation:
    // `new` followed by [modifiers] type name( is the hiding modifier of a method (`public new void M()`). Checked
    // operators (`operator checked +`), primary-constructor type members and field/property-initializer lambdas already
    // matched. P3-41: initializer arguments holding braces no longer split the header (HeaderStart; limit
    // BracedInitializerArguments removed): a trailing `) : base(...)`/`) : this(...)` with balanced parentheses is first
    // reduced to `) : base()`/`) : this()`. The hiding `new` also accepts a tuple return type (`new (int, int) M()`) and a
    // `ref`/`ref readonly` return (`new ref int M()`).
    // P3-43: leading attribute sections are first reduced to `[]` with balanced brackets (LeadingAttributes), so braces,
    // `new[]` or nested brackets in an attribute argument (`[A(new[] { 1 })]`, `[A(typeof(int[]))]`) neither hide the
    // header nor read as object creation. The hiding `new` also accepts a generic return type holding a tuple or a nested
    // generic (`new List<(int, int)> M()`) and a pointer return (`new unsafe int* M()`); a header token may carry `*`.
    // The P3-39 pre-balance initializer replace is removed: InitializerArguments already reduced the arguments to `()`,
    // which no keyword test reads (the base(new object()) and this(new[] { n }) plants pin that). Both balancing-group
    // regexes carry a 1 s match timeout (pinned in the P3-29 section).
    // P3-45: every regex InBlockLambda and MethodBody use is a named static field with the same 1 s match timeout
    // (pinned by reflection, and the helpers' source is pinned to call no static Regex method). Attribute sections not
    // at the start of a header (on a parameter or type parameter: after '(', ',' or '<') are reduced to `[]` with
    // balanced brackets (InnerAttributes), so `void Later([A(new[] { 1 })] int n)` and `void Later<[A(new[] { 1 })] T>()`
    // neither break the parameter list nor read as object creation. The hiding `new` accepts a generic return type
    // nested to any depth (balancing group: `new Dictionary<A<B<(int, int)>>, C> M()`). An unsafe local function
    // returning a pointer without `new` (`unsafe int* Later()`) already matched.
    // P3-47: a run of attribute sections on one parameter or type parameter (`([A] [B(new[] { 1 })] int n)`) is reduced
    // to one `[]` (the sections after the first are not after '(', ',' or '<'). A collection expression after '(' or ','
    // in a call (`F([1, 2])`) is reduced the same way (pinned): harmless, no header test reads call arguments.
    static readonly System.Text.RegularExpressions.Regex InnerAttributes = new(@"(?<=[(,<]\s*)\[(?>[^\[\]]+|\[(?<d>)|\](?<-d>))*(?(d)(?!))\](?:\s*\[(?>[^\[\]]+|\[(?<d>)|\](?<-d>))*(?(d)(?!))\])*",
        System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
    static readonly System.Text.RegularExpressions.Regex LambdaHeader = new(@"(?:=>|\bdelegate\s*(?:\([^(){};]*\))?)\s*$",
        System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
    static readonly System.Text.RegularExpressions.Regex Constraint = new(@"\bwhere\s+\w+\s*:",
        System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
    static readonly System.Text.RegularExpressions.Regex TypeKeyword = new(@"\b(?:class|struct|record|interface|enum|namespace)\b",
        System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
    static readonly System.Text.RegularExpressions.Regex ObjectCreation = new(@"\bnew\b(?!\s+(?:(?:public|protected|internal|private|static|virtual|abstract|sealed|override|readonly|unsafe|extern|async|partial|required)\s+)*(?:ref\s+(?:readonly\s+)?)?(?:\([^(){};]*\)|[\w.]+(?:<(?>[^<>{};]+|<(?<g>)|>(?<-g>))*(?(g)(?!))>)?)(?:\[[\s,]*\]|\?|\*)*\s+~?\w+\s*(?:<[^(){};]*>)?\s*\()",
        System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
    static readonly System.Text.RegularExpressions.Regex AccessorHeader = new(@"^\s*(?:\[[^\[\]{};]*\]\s*)*(?:(?:public|protected|internal|private|readonly)\s+)*(?:get|set|init|add|remove)\s*$",
        System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
    static readonly System.Text.RegularExpressions.Regex OperatorHeader = new(@"\boperator\b[^(){};]*\([^{};]*\)\s*$",
        System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
    static readonly System.Text.RegularExpressions.Regex MethodHeader = new(@"^\s*(?:(?>\[[^\[\]{};]*\]|\([^(){};]*\)|[\w<>,.?:*]+)\s*)*?~?\s*(\w+)\s*(?:<[^(){};]*>)?\s*\([^{};]*\)\s*(?:\bwhere\b[^{};]*)?$",
        System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
    static readonly System.Text.RegularExpressions.Regex InitializerArguments = new(@"\)\s*:\s*(base|this)\s*\((?>[^()]+|\((?<d>)|\)(?<-d>))*(?(d)(?!))\)\s*$",
        System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
    static readonly System.Text.RegularExpressions.Regex LeadingAttributes = new(@"^\s*(?:\[(?>[^\[\]]+|\[(?<d>)|\](?<-d>))*(?(d)(?!))\]\s*)+",
        System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));

    // P3-49: the header reductions MethodBody applies, in its order (LeadingAttributes, InnerAttributes,
    // InitializerArguments); the P3-29 section pins its outputs and that MethodBody reduces only through it.
    static string ReducedHeader(string header)
    {
        header = LeadingAttributes.Replace(header, "[] ");
        header = InnerAttributes.Replace(header, "[]");
        return InitializerArguments.Replace(header, ") : $1()");
    }

    static bool MethodBody(string header)
    {
        header = ReducedHeader(header);
        var constraint = Constraint.Match(header);
        var head = constraint.Success ? header[..constraint.Index] : header;
        if (TypeKeyword.IsMatch(head)) return false;
        if (ObjectCreation.IsMatch(head)) return false;
        if (AccessorHeader.IsMatch(header)) return true;
        if (OperatorHeader.IsMatch(head)) return true;
        var match = MethodHeader.Match(header);
        return match.Success && !new[] { "if", "while", "for", "foreach", "switch", "catch", "using", "lock", "fixed", "when", "checked", "unchecked", "return", "else", "do", "try", "finally" }
            .Contains(match.Groups[1].Value, StringComparer.Ordinal);
    }

    // P3-27: CodeOf does not model raw """ strings or interpolated verbatim @$"/$@" strings; a '"' and '//' inside one
    // can make the rest of a line read as a comment, hiding a use from both counts. So a source whose unstripped text
    // names the seam at all must contain none of those openers: otherwise it is not clean (and counts as a mention).
    // CodeOf itself is unchanged for its other callers.
    static (bool Clean, int Mentions, string Detail) LexerGuard((bool Clean, int Mentions, string Detail) verdict, string text, string name)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"\b" + System.Text.RegularExpressions.Regex.Escape(name) + @"\b")) return verdict;
        var openers = new[] { "\"\"\"", "@$\"", "$@\"" }.Where(o => text.Contains(o, StringComparison.Ordinal)).ToArray();
        return openers.Length == 0 ? verdict
            : (false, Math.Max(verdict.Mentions, 1), verdict.Detail + " lexer-unsafe literal openers [" + string.Join(" ", openers) + "] in a source naming " + name);
    }

    // P3-22: a stdout whose every write throws the given exception, so Run throws while it prints.
    sealed class ThrowingWriter(Exception failure) : TextWriter(CultureInfo.InvariantCulture)
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char value) => throw failure;
        public override void Write(string? value) => throw failure;
        public override void WriteLine(string? value) => throw failure;
    }

    // Valid inputs that pass every check before each verb's guarded library work (a fixed small request, brief or
    // fixture; the repository's packs), then the output path (a new file under work). The inputs are written to the new
    // directory inputs, which the caller deletes in a finally (P3-22).
    static string[] RunArgs(string verb, string output, string inputs)
    {
        Directory.CreateDirectory(inputs);
        string Input(string name, string content) { var path = Path.Combine(inputs, name); File.WriteAllText(path, content, new System.Text.UTF8Encoding(false)); return path; }
        return verb switch
        {
            "generate-prototypes" => new[] { verb, Input("request.json", "{\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"alternatives\":2,\"seed\":0}"), output },
            "generate-typology" => new[] { verb, Input("request.json", "{\"pack\":\"bim.typology.vivienda-unifamiliar\",\"family_size\":3,\"lot\":{\"width_m\":16,\"depth_m\":16},\"alternatives\":4,\"seed\":0,\"project_id\":\"00000000-0000-4000-8000-0000000007a7\"}"), output },
            "plan-layouts" => new[] { verb, Input("brief.json", "{\"brief_version\":1,\"name\":\"Small studio\",\"constraints\":{\"site\":{\"width_m\":6,\"depth_m\":8},\"levels\":1},"
                + "\"rooms\":[{\"id\":\"living\",\"name\":\"Living\",\"target_area_m2\":20,\"min_area_m2\":15},{\"id\":\"bath\",\"name\":\"Bath\",\"target_area_m2\":4,\"min_area_m2\":3}],"
                + "\"adjacencies\":[{\"a\":\"living\",\"b\":\"bath\",\"kind\":\"required\"}]}"), output },
            "doc-realized" => new[] { verb, "--record", Input("run.json", "{}"), "--fixture", Input("ir.json", JsonSerializer.Serialize(new Project(Guid.Parse("00000000-0000-4000-8000-0000000007b1"), 0, Array.Empty<Element>()))),
                "--assignments", Input("assignments.json", "{}"), "--document", "internal-error-document", "--output", output },
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, null),
        };
    }

    // An actor list with one item whose enumeration throws: a defect inside AuditVerifyTool.Run's try that no typed
    // refusal handles.
    sealed class ThrowingActors(Exception failure) : IReadOnlyList<string?>
    {
        public int Count => 1;
        public string? this[int index] => throw failure;
        public IEnumerator<string?> GetEnumerator() => throw failure;
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
