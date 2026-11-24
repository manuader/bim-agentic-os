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
        // SeamHygiene in its four verb files). The checker is first run on planted sources for every seam.
        {
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
                    ("the read nested in a block inside a stored block lambda", declaration + "\nstatic void W(bool ok) { Action later = () => { if (ok) { " + read + " } }; later(); }\n", false),
                    ("the read moved into a stored anonymous method", declaration + "\nstatic void W() { Action later = delegate { " + read + " }; later(); }\n", false),
                    ("the read moved into a stored anonymous method with parameters", declaration + "\nstatic void W() { Action<int> later = delegate (int n) { " + read + " }; later(0); }\n", false),
                    ("the read moved into a block lambda passed as an argument", declaration + "\nstatic void W() { Task.Run(() => { " + read + " }); }\n", false),
                    ("the read moved into a stored expression lambda", declaration + "\nstatic void W() { Action later = () => " + read + " later(); }\n", false),
                    // P3-33: local functions, and lambdas with an attribute or an explicit return type before the parameter list.
                    ("the read moved into a local function (planted)", declaration + "\nstatic void W() { void Later() { " + read + " } Later(); }\n", false),
                    ("the read nested in a block inside a static local function with parameters", declaration + "\nstatic void W() { static void Later(int n) { if (n > 0) { " + read + " } } Later(1); }\n", false),
                    ("the read moved into an expression-bodied local function", declaration + "\nstatic void W() { void Later() => " + read + " Later(); }\n", false),
                    ("an unrelated local function closed before the pinned read", declaration + "\nstatic void W() { void Unrelated() { } Unrelated(); try { " + read + " } catch (Exception) { throw; } }\n", true),
                    ("the read moved into an attributed block lambda (planted)", declaration + "\nstatic void W() { Action later = [Obsolete] () => { " + read + " }; later(); }\n", false),
                    ("the read moved into a block lambda with an explicit return type", declaration + "\nstatic void W() { Func<int> later = int () => { " + read + " return 0; }; later(); }\n", false),
                    ("the read moved into an attributed lambda with an explicit return type passed as an argument", declaration + "\nstatic void W() { Task.Run([Obsolete] void () => { " + read + " }); }\n", false),
                    ("an unrelated attributed lambda with an explicit return type closed before the pinned read", declaration + "\nstatic void W() { Func<int> unrelated = [Obsolete] int () => { return 0; }; try { " + read + " } catch (Exception) { throw; } }\n", true),
                    // P3-35: local functions inside accessors and operators, and local functions or methods whose header
                    // carries a `where T : class`/`new()` constraint or a generic tuple return type.
                    ("the read moved into a local function inside a property get accessor (planted)", declaration + "\nstatic int P { get { void Later() { " + read + " } Later(); return 0; } }\n", false),
                    ("the read moved into a local function inside an indexer get accessor", declaration + "\nsealed class C { int this[int n] { get { void Later() { " + read + " } Later(); return n; } } }\n", false),
                    ("the read moved into a local function inside a private set accessor", declaration + "\nsealed class C { int P { get => 0; private set { void Later() { " + read + " } Later(); } } }\n", false),
                    ("the read moved into a local function inside an operator + body (planted)", declaration + "\nsealed class C { public static C operator +(C a, C b) { void Later() { " + read + " } Later(); return a; } }\n", false),
                    ("the read moved into a local function inside an operator == body", declaration + "\nsealed class C { public static bool operator ==(C a, C b) { void Later() { " + read + " } Later(); return true; } public static bool operator !=(C a, C b) => false; }\n", false),
                    ("the read moved into a local function constrained where T : class (planted)", declaration + "\nstatic void W() { void Later<T>() where T : class { " + read + " } Later<string>(); }\n", false),
                    ("the read moved into a local function constrained where T : new()", declaration + "\nstatic void W() { void Later<T>() where T : new() { " + read + " } Later<object>(); }\n", false),
                    ("the read moved into a local function inside a method constrained where T : class, new()", declaration + "\nstatic void W<T>() where T : class, new() { void Later() { " + read + " } Later(); }\n", false),
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
                    ("the read moved into a local function inside a required property init accessor (planted)", declaration + "\nsealed class C { public required int P { get => 0; init { void Later() { " + read + " } Later(); } } }\n", false),
                    ("the read moved into a local function inside a virtual property get accessor", declaration + "\nclass C { public virtual int P { get { void Later() { " + read + " } Later(); return 0; } } }\n", false),
                    ("the read moved into a local function inside an override property set accessor", declaration + "\nclass C : B { public override int P { get => 0; set { void Later() { " + read + " } Later(); } } }\n", false),
                    ("the read moved into a local function inside a sealed override protected set accessor", declaration + "\nclass C : B { public sealed override int P { get => 0; protected set { void Later() { " + read + " } Later(); } } }\n", false),
                    ("the read moved into a local function inside an explicit interface get accessor (planted)", declaration + "\nsealed class C : I { int I.P { get { void Later() { " + read + " } Later(); return 0; } } }\n", false),
                    ("the read moved into a local function inside an explicit interface indexer get accessor", declaration + "\nsealed class C : I { int I.this[int n] { get { void Later() { " + read + " } Later(); return n; } } }\n", false),
                    ("the read moved into a local function inside an init accessor", declaration + "\nsealed class C { int P { get => 0; init { void Later() { " + read + " } Later(); } } }\n", false),
                    ("the read moved into a local function inside an event add accessor (planted)", declaration + "\nsealed class C { event Action E { add { void Later() { " + read + " } Later(); } remove { } } }\n", false),
                    ("the read moved into a local function inside an event remove accessor", declaration + "\nsealed class C { event Action E { add { } remove { void Later() { " + read + " } Later(); } } }\n", false),
                    ("the read moved into a local function inside an explicit interface event add accessor", declaration + "\nsealed class C : I { event Action I.E { add { void Later() { " + read + " } Later(); } remove { } } }\n", false),
                    ("the read moved into a local function constrained where T : struct (planted)", declaration + "\nstatic void W() { void Later<T>() where T : struct { " + read + " } Later<int>(); }\n", false),
                    ("the read moved into a local function inside a method constrained where T : struct", declaration + "\nstatic void W<T>() where T : struct { void Later() { " + read + " } Later(); }\n", false),
                    ("the read moved into a local function inside a constructor (planted)", declaration + "\nsealed class C { public C() { void Later() { " + read + " } Later(); } }\n", false),
                    ("the read moved into a local function inside a constructor with a this() initializer", declaration + "\nsealed class C { public C(int n) : this() { void Later() { " + read + " } Later(); } C() { } }\n", false),
                    ("the read moved into a local function inside a static constructor", declaration + "\nsealed class C { static C() { void Later() { " + read + " } Later(); } }\n", false),
                    ("the read moved into a local function inside a finalizer (planted)", declaration + "\nclass C { ~C() { void Later() { " + read + " } Later(); } }\n", false),
                    ("the read moved into a block lambda in a switch-expression arm (planted)", declaration + "\nstatic void W(int n) { Action later = n switch { 0 => () => { " + read + " }, _ => () => { } }; later(); }\n", false),
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
                };
                if (full)
                {
                    var other = type == "Action" ? "internal static Action<string>? " + seam + " { get; set; }" : "internal static Action? " + seam + " { get; set; }";
                    var bareRead = type == "Action" ? "var p = " + seam + "; p?.Invoke();" : "var p = " + seam + "; p?.Invoke(output);";
                    cases.AddRange(new (string Name, string Source, bool Clean)[]
                    {
                        ("assignments only inside comments", good + "// " + seam + " = null;\n/* " + seam + " ??= x; */\n", true),
                        ("a planted assignment", good + "static void T() { " + seam + " = null; }\n", false),
                        ("a planted qualified assignment without spaces", good + "static void T() { Owner." + seam + "=null; }\n", false),
                        ("a planted ??=", good + "static void T() { " + seam + " ??= default; }\n", false),
                        ("a planted +=", good + "static void T() { " + seam + " += _ => { }; }\n", false),
                        ("a planted parenthesized assignment", good + "static void T() { (" + seam + ") = null; }\n", false),
                        ("a planted initializer", declaration + " = default;\nstatic void W() { " + read + " }\n", false),
                        ("another declared type", other + "\nstatic void W() { " + read + " }\n", false),
                        ("a second pinned read", good + "static void T() { " + read + " }\n", false),
                        ("a read in another form", good + "static void T() { " + bareRead + " }\n", false),
                        ("the read missing", declaration + "\n", false),
                        ("the name inside a string literal", good + "const string S = \"" + seam + " = null;\";\n", false),
                        ("an assignment in an interpolation hole (planted evasion)", good + "static void T() { var s = $\"{" + seam + " = null}\"; }\n", false),
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
                    var uses = SeamHygiene(source, seam, type);
                    Check(uses.Clean == clean, "P3-29 seam hygiene self-test: " + seam + ": " + name + (clean ? " is clean" : " is caught"), uses.Detail);
                    selfTests++;
                }
            }
            // 8 lambda/anonymous-method cases plus 8 local-function/attributed-lambda cases (P3-33) plus 14
            // accessor/operator/constrained-generic/tuple-return cases (P3-35) plus 27 accessor-modifier/member-kind
            // cases (P3-37) for each of the 9 seams; 16 more plus 1 (Action) or 2 (Action<string>) form cases for each of
            // the four new seams: 583.
            Check(selfTests == 9 * (8 + 8 + 14 + 27) + 2 * (16 + 1) + 2 * (16 + 2) && selfTests == 583, "P3-29 seam hygiene: every self-test ran", selfTests.ToString(CultureInfo.InvariantCulture));

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
                if (System.Text.RegularExpressions.Regex.IsMatch(code[..i], @"(?:=>|\bdelegate\s*(?:\([^(){};]*\))?)\s*$")) return true;
                var start = i == 0 ? 0 : code.LastIndexOfAny(new[] { ';', '{', '}' }, i - 1) + 1;
                if (MethodBody(code[start..i]) && ++bodies == 2) return true;
            }
        }
        return false;
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
    static bool MethodBody(string header)
    {
        var constraint = System.Text.RegularExpressions.Regex.Match(header, @"\bwhere\s+\w+\s*:");
        var head = constraint.Success ? header[..constraint.Index] : header;
        if (System.Text.RegularExpressions.Regex.IsMatch(head, @"\b(?:class|struct|record|interface|enum|namespace|new)\b")) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(header, @"^\s*(?:\[[^\[\]{};]*\]\s*)*(?:(?:public|protected|internal|private|readonly)\s+)*(?:get|set|init|add|remove)\s*$")) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(head, @"\boperator\b[^(){};]*\([^{};]*\)\s*$")) return true;
        var match = System.Text.RegularExpressions.Regex.Match(header,
            @"^\s*(?:(?>\[[^\[\]{};]*\]|\([^(){};]*\)|[\w<>,.?:]+)\s*)*?~?\s*(\w+)\s*(?:<[^(){};]*>)?\s*\([^{};]*\)\s*(?:\bwhere\b[^{};]*)?$");
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
