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
//                                   the catch-all is `throw InternalRefusal(error)`: InternalRefusal(e) and then the
//                                   verb's Fail (what Run does with that Refusal), stdout, stderr and exit.
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
            var seam = type?.GetMethod("InternalRefusal", Any, new[] { typeof(Exception) });
            Check(seam is not null && seam.ReturnType.Name == "Refusal", verb + ": InternalRefusal(Exception) returns the verb's Refusal", seam?.ReturnType.FullName ?? "<missing>");
            if (seam is null) continue;
            var forced = Quiet(() =>
            {
                var (refusal, thrown) = Call(seam, failure);
                if (thrown is not null || refusal is null) return "InternalRefusal threw " + thrown?.GetType().FullName;
                var code = Prop(refusal, "Code") as string;
                fail.Invoke(null, FailArgs(code, refusal));
                return "";
            });
            Check(forced.Value.Length == 0 && Same(forced, handWritten), "a forced " + verb + " internal error prints the hand-written stdout, stderr and exit bytes",
                forced.Value + " " + Q(forced) + " vs hand-written " + Q(handWritten));
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
            ("DocUpdateCommand.cs", new[] { Seam("doc-update") }),
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
    }
}
