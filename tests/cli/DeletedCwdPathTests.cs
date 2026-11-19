using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

// P10-30: the CLI side of P10-28/P10-29. Under a deleted working directory Path.GetFullPath of a relative path throws
// FileNotFoundException on Linux/macOS. Each verb below (audit-verify, diff-intent, doc-realized, doc-update,
// generate-prototypes, generate-typology, plan-layouts) is run in process through the real CLI entry point (Bim.Cli.dll's
// Program, loaded into this process), once per path argument, with that one argument relative and every other path
// absolute, while the working directory is deleted. Each case must print the verb's typed path refusal (exit 1, empty
// stderr), never Program's generic catch {"status":"failed","code":<exception message>} nor a *_internal_error line.
// The working directory, Console and Environment.ExitCode are restored in a finally. Windows cannot delete a process's
// current directory: every case is a counted SKIP there.
internal static class DeletedCwdPathTests
{
    const string RelativeInput = "relative-input.json";
    const string RelativeOutput = "relative-output.json";

    public static void Run(string root, string work, string dotnet, string cliDll, Action<bool, string, string> check, Action<string, string> skip)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "deleted cwd path: " + name, detail);
        var scratch = Path.Combine(work, "deleted-cwd-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var dummy = Path.Combine(scratch, "dummy.json");
        File.WriteAllText(dummy, "{}\n", new UTF8Encoding(false));
        var counter = 0;
        string Output() => Path.Combine(scratch, "out-" + (++counter).ToString(CultureInfo.InvariantCulture) + ".json");

        // (verb, path argument, arguments, expected line). The expected line is the verb's typed refusal for that argument.
        var cases = new List<(string Verb, string Argument, Func<string[]> Arguments, string Expected)>();
        string Failed(string code, string? detail = null) => detail is null
            ? JsonSerializer.Serialize(new { status = "failed", code })
            : JsonSerializer.Serialize(new { status = "failed", code, detail });

        cases.Add(("audit-verify", "log", () => new[] { "audit-verify", RelativeInput }, Failed("input_io_error")));
        cases.Add(("audit-verify", "--anchor", () => new[] { "audit-verify", dummy, "--anchor", RelativeInput }, Failed("input_io_error")));
        cases.Add(("audit-verify", "--write-anchor", () => new[] { "audit-verify", dummy, "--write-anchor", RelativeOutput }, Failed("output_path_invalid")));
        cases.Add(("diff-intent", "--project", () => new[] { "diff-intent", "--project", RelativeInput, "--intent", dummy }, Failed("input_io_error")));
        cases.Add(("diff-intent", "--intent", () => new[] { "diff-intent", "--project", dummy, "--intent", RelativeInput }, Failed("input_io_error")));
        foreach (var flag in new[] { "--record", "--fixture", "--assignments", "--output" })
        {
            var name = flag;
            cases.Add(("doc-realized", name, () =>
            {
                var values = new Dictionary<string, string> { ["--record"] = dummy, ["--fixture"] = dummy, ["--assignments"] = dummy, ["--output"] = Output() };
                values[name] = name == "--output" ? RelativeOutput : RelativeInput;
                return new[] { "doc-realized", "--record", values["--record"], "--fixture", values["--fixture"], "--assignments", values["--assignments"], "--document", "uid-1", "--output", values["--output"] };
            }, name == "--output" ? Failed("output_path_invalid") : Failed("input_io_error", name[2..])));
        }
        foreach (var flag in new[] { "--before", "--after", "--realized", "--bindings", "--dependents", "--output" })
        {
            var name = flag;
            cases.Add(("doc-update", name, () =>
            {
                var values = new Dictionary<string, string> { ["--before"] = dummy, ["--after"] = dummy, ["--realized"] = dummy, ["--bindings"] = dummy, ["--dependents"] = dummy, ["--output"] = Output() };
                values[name] = name == "--output" ? RelativeOutput : RelativeInput;
                return new[] { "doc-update", "--before", values["--before"], "--after", values["--after"], "--realized", values["--realized"], "--bindings", values["--bindings"], "--dependents", values["--dependents"], "--output", values["--output"] };
            }, name == "--output" ? Failed("output_path_invalid") : Failed("input_io_error", name[2..])));
        }
        foreach (var verb in new[] { "generate-prototypes", "generate-typology", "plan-layouts" })
        {
            var name = verb;
            cases.Add((name, "request", () => new[] { name, RelativeInput, Output() }, Failed("input_io_error")));
            cases.Add((name, "output", () => new[] { name, dummy, RelativeOutput }, Failed("output_path_invalid")));
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var (verb, argument, _, _) in cases)
                skip("deleted cwd path: " + verb + " " + argument, "Windows cannot delete a directory that is a process's current directory");
            return;
        }

        var entry = Assembly.LoadFrom(cliDll).EntryPoint ?? throw new InvalidOperationException("Bim.Cli.dll has no entry point: " + cliDll);
        var original = Environment.CurrentDirectory;
        var exitBefore = Environment.ExitCode;
        var facts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (verb, argument, arguments, expected) in cases)
        {
            var label = verb + " " + argument;
            var args = arguments();
            var gone = Path.Combine(scratch, "gone-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(gone);
            var previousOut = Console.Out;
            var previousError = Console.Error;
            var previousExit = Environment.ExitCode;
            var capturedOut = new StringWriter();
            var capturedError = new StringWriter();
            var exit = -1;
            var thrown = "";
            var probe = "";
            var deleted = false;
            try
            {
                Directory.SetCurrentDirectory(gone);
                Directory.Delete(gone);
                deleted = !Directory.Exists(gone);
                try { probe = "GetFullPath accepted: " + Path.GetFullPath(RelativeInput); }
                catch (Exception failure) { probe = "GetFullPath threw " + failure.GetType().FullName; }
                Environment.ExitCode = 0;
                Console.SetOut(capturedOut);
                Console.SetError(capturedError);
                entry.Invoke(null, new object[] { args });
            }
            catch (Exception failure)
            {
                var inner = failure is TargetInvocationException { InnerException: { } cause } ? cause : failure;
                thrown = " threw " + inner.GetType().FullName + ": " + inner.Message;
            }
            finally
            {
                exit = Environment.ExitCode;
                Environment.ExitCode = previousExit;
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                Directory.SetCurrentDirectory(original);
            }
            var line = capturedOut.ToString().TrimEnd('\r', '\n');
            var error = (capturedError.ToString().ReplaceLineEndings(" ").Trim() + thrown).Trim();
            var measured = "exit " + exit.ToString(CultureInfo.InvariantCulture) + " " + line + (error.Length > 0 ? " stderr " + error : "");
            if (!facts.TryGetValue(verb, out var list)) facts[verb] = list = new List<string>();
            list.Add(argument + " -> " + measured);
            Check(deleted, label + ": the working directory was deleted", gone);
            Check(probe.StartsWith("GetFullPath threw System.IO.FileNotFoundException", StringComparison.Ordinal),
                label + ": precondition: Path.GetFullPath of a relative path under the deleted working directory throws FileNotFoundException", probe);
            Check(exit == 1 && line == expected && error.Length == 0 && !line.Contains("internal_error", StringComparison.Ordinal),
                label + ": a relative path under a deleted working directory is the typed refusal " + expected + " with nothing on stderr", measured);
            Check(Environment.CurrentDirectory == original, label + ": the working directory is restored (finally)", Environment.CurrentDirectory + " != " + original);
            var outputs = Directory.GetFiles(scratch, "out-*.json");
            Check(outputs.Length == 0, label + ": no output file is written", string.Join(",", outputs));
        }
        Check(Environment.ExitCode == exitBefore, "Environment.ExitCode is restored (finally)",
            Environment.ExitCode.ToString(CultureInfo.InvariantCulture) + ", before " + exitBefore.ToString(CultureInfo.InvariantCulture));
        // Probe facts: one annotation per verb with every measured case.
        foreach (var (verb, list) in facts)
            Console.WriteLine("::warning::P10-30 probe " + Environment.OSVersion.Platform + " " + verb + ": " + string.Join(" || ", list).ReplaceLineEndings(" "));
        try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
