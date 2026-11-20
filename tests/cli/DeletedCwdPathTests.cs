using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

// P10-30: the CLI side of P10-28/P10-29. Under a deleted working directory Path.GetFullPath of a relative path throws
// FileNotFoundException on Linux/macOS. Each verb below (audit-verify, diff-intent, doc-realized, doc-update,
// generate-prototypes, generate-typology, plan-layouts) is run in process through the real CLI entry point (Bim.Cli.dll's
// Program, loaded into this process), once per path argument, with that one argument relative and every other path
// absolute, while the working directory is deleted. Every other caller of the shared output rule (export-gltf, cad-draft,
// cad-brief, cad-plan-brief, mirror-drift, mirror-drift-select, bcf-export) is run with a relative output path. Each case must print the verb's typed path refusal (exit 1, empty
// stderr), never Program's generic catch {"status":"failed","code":<exception message>} nor a *_internal_error line,
// and writes no output. Measured facts are printed as ::warning:: lines only for a verb with a failing case.
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
        try { Cases(scratch, cliDll, Check, skip); Remainder(scratch, cliDll, Check, skip); }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static void Cases(string scratch, string cliDll, Action<bool, string, string> Check, Action<string, string> skip)
    {
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
        // P10-30 review: every other caller of the shared output rule (GltfExportCommand.OutputProblem), relative output only.
        var dxf = Path.Combine(scratch, "dummy.dxf");
        File.WriteAllText(dxf, "0\nEOF\n", new UTF8Encoding(false));
        var store = Path.Combine(scratch, "store");
        Directory.CreateDirectory(store);
        cases.Add(("export-gltf", "output", () => new[] { "export-gltf", dummy, "relative-output.glb" }, Failed("output_path_invalid")));
        cases.Add(("cad-draft", "output", () => new[] { "cad-draft", dxf, RelativeOutput }, Failed("output_path_invalid")));
        cases.Add(("cad-brief", "output", () => new[] { "cad-brief", dxf, RelativeOutput }, Failed("output_path_invalid")));
        cases.Add(("cad-plan-brief", "output", () => new[] { "cad-plan-brief", dummy, RelativeOutput }, Failed("output_path_invalid")));
        cases.Add(("mirror-drift", "output", () => new[] { "mirror-drift", store, dummy, dummy, RelativeOutput }, Failed("output_path_invalid")));
        cases.Add(("mirror-drift-select", "output", () => new[] { "mirror-drift-select", store, dummy, dummy, dummy, RelativeOutput }, Failed("output_path_invalid")));
        cases.Add(("bcf-export", "output", () => new[] { "bcf-export", dummy, "relative-output.bcfzip" }, Failed("bcf_export_output_invalid", "output_path_invalid")));

        RunCases(scratch, cliDll, cases, Check, skip);
    }

    static void RunCases(string scratch, string cliDll, List<(string Verb, string Argument, Func<string[]> Arguments, string Expected)> cases, Action<bool, string, string> Check, Action<string, string> skip)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var (verb, argument, _, _) in cases)
                skip("deleted cwd path: " + verb + " " + argument, "Windows cannot delete a directory that is a process's current directory");
            return;
        }

        Assembly? cli = null;
        try { cli = Assembly.LoadFrom(cliDll); }
        catch (Exception error) when (error is IOException or BadImageFormatException) { Check(false, "the Release CLI assembly loads", error.Message); }
        if (cli is null) return;
        var entry = cli.EntryPoint;
        Check(entry is not null, "the Release CLI assembly has an entry point", cliDll);
        if (entry is null) return;
        var original = Environment.CurrentDirectory;
        var exitBefore = Environment.ExitCode;
        var facts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var failing = new HashSet<string>(StringComparer.Ordinal);
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
            var typed = exit == 1 && line == expected && error.Length == 0 && !line.Contains("internal_error", StringComparison.Ordinal);
            if (!typed) failing.Add(verb);
            Check(exit == 1 && line == expected && error.Length == 0 && !line.Contains("internal_error", StringComparison.Ordinal),
                label + ": a relative path under a deleted working directory is the typed refusal " + expected + " with nothing on stderr", measured);
            Check(Environment.CurrentDirectory == original, label + ": the working directory is restored (finally)", Environment.CurrentDirectory + " != " + original);
            var outputs = Directory.GetFiles(scratch, "out-*.json");
            Check(outputs.Length == 0, label + ": no output file is written", string.Join(",", outputs));
        }
        Check(Environment.ExitCode == exitBefore, "Environment.ExitCode is restored (finally)",
            Environment.ExitCode.ToString(CultureInfo.InvariantCulture) + ", before " + exitBefore.ToString(CultureInfo.InvariantCulture));
        // Probe facts: one annotation per verb with a failing case, listing every measured case of that verb.
        foreach (var (verb, list) in facts)
            if (failing.Contains(verb)) Console.WriteLine("::warning::P10-30 probe " + Environment.OSVersion.Platform + " " + verb + ": " + string.Join(" || ", list).ReplaceLineEndings(" "));
    }

    // ---- P10-32: the deleted-cwd remainder ----
    // (1) doc-update --probe-dependents (probe mode prints field/detail as separate members) with one relative path argument
    // under a deleted working directory, through the same in-process runner as P10-30, pinned as measured.
    // (2) A relative input/output path while the working directory exists but is not searchable: "cwd" mode chmods the
    // working directory itself to 000, "parent" mode chmods its parent to 000 (the working directory keeps 0700). Each case
    // must print a typed refusal (exit 1, a snake_case code, never Program's generic catch nor *_internal_error, empty
    // stderr) and write nothing. Linux/macOS only; a counted SKIP on Windows, and when a mode-000 directory stays
    // listable (root or CAP_DAC_OVERRIDE). Modes, the working directory, Console and ExitCode are restored in a finally.
    const string Pipe = "bim-agentic-os-2025-1";

    static void Remainder(string scratch, string cliDll, Action<bool, string, string> Check, Action<string, string> skip)
    {
        var dummy = Path.Combine(scratch, "remainder-dummy.json");
        File.WriteAllText(dummy, "{}\n", new UTF8Encoding(false));
        var counter = 0;
        string Output() => Path.Combine(scratch, "out-remainder-" + (++counter).ToString(CultureInfo.InvariantCulture) + ".json");
        string[] DocUpdate(string name, bool probe)
        {
            var values = new Dictionary<string, string> { ["--before"] = dummy, ["--after"] = dummy, ["--realized"] = dummy, ["--bindings"] = dummy, ["--output"] = Output() };
            values[name] = name == "--output" ? RelativeOutput : RelativeInput;
            var args = new List<string> { "doc-update", "--before", values["--before"], "--after", values["--after"], "--realized", values["--realized"], "--bindings", values["--bindings"], "--output", values["--output"] };
            if (probe) args.AddRange(new[] { "--probe-dependents", Pipe });
            return args.ToArray();
        }
        string Probe(string code, string? field) => field is null
            ? JsonSerializer.Serialize(new { status = "failed", code })
            : JsonSerializer.Serialize(new { status = "failed", code, field });

        // (1) deleted cwd, probe mode. The output path is checked before the pipe is taken, so it keeps the plain envelope.
        var probeCases = new List<(string Verb, string Argument, Func<string[]> Arguments, string Expected)>();
        foreach (var flag in new[] { "--before", "--after", "--realized", "--bindings", "--output" })
        {
            var name = flag;
            probeCases.Add(("doc-update --probe-dependents", name, () => DocUpdate(name, true),
                name == "--output" ? Probe("output_path_invalid", null) : Probe("input_io_error", name[2..])));
        }
        RunCases(scratch, cliDll, probeCases, Check, skip);

        // (2) unsearchable cwd: one relative input and one relative output per verb (both doc-update modes).
        var cases = new List<(string Verb, string Argument, Func<string[]> Arguments)>
        {
            ("audit-verify", "log", () => new[] { "audit-verify", RelativeInput }),
            ("audit-verify", "--write-anchor", () => new[] { "audit-verify", dummy, "--write-anchor", RelativeOutput }),
            ("diff-intent", "--project", () => new[] { "diff-intent", "--project", RelativeInput, "--intent", dummy }),
            ("doc-realized", "--record", () => new[] { "doc-realized", "--record", RelativeInput, "--fixture", dummy, "--assignments", dummy, "--document", "uid-1", "--output", Output() }),
            ("doc-realized", "--output", () => new[] { "doc-realized", "--record", dummy, "--fixture", dummy, "--assignments", dummy, "--document", "uid-1", "--output", RelativeOutput }),
            ("doc-update", "--before", () => DocUpdate("--before", false)),
            ("doc-update", "--output", () => DocUpdate("--output", false)),
            ("doc-update --probe-dependents", "--before", () => DocUpdate("--before", true)),
            ("doc-update --probe-dependents", "--output", () => DocUpdate("--output", true)),
        };
        foreach (var verb in new[] { "generate-prototypes", "generate-typology", "plan-layouts" })
        {
            var name = verb;
            cases.Add((name, "request", () => new[] { name, RelativeInput, Output() }));
            cases.Add((name, "output", () => new[] { name, dummy, RelativeOutput }));
        }
        cases.Add(("export-gltf", "output", () => new[] { "export-gltf", dummy, "relative-output.glb" }));
        cases.Add(("bcf-export", "output", () => new[] { "bcf-export", dummy, "relative-output.bcfzip" }));
        foreach (var mode in new[] { "cwd", "parent" })
            RunUnsearchable(scratch, cliDll, mode, cases, Check, skip);
    }

    static void RunUnsearchable(string scratch, string cliDll, string mode, List<(string Verb, string Argument, Func<string[]> Arguments)> cases,
        Action<bool, string, string> Check, Action<string, string> skip)
    {
        string Label(string verb, string argument) => "unsearchable " + mode + ": " + verb + " " + argument;
        if (OperatingSystem.IsWindows())
        {
            foreach (var (verb, argument, _) in cases) skip("deleted cwd path: " + Label(verb, argument), "Unix permission bits (chmod 000) only");
            return;
        }
        var parent = Path.Combine(scratch, "locked-parent-" + mode);
        var cwd = Path.Combine(parent, "cwd");
        Directory.CreateDirectory(cwd);
        var locked = mode == "cwd" ? cwd : parent;
        const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        // Behavioural privilege check (the P10-30 chmod rule): a mode-000 directory still listable means root/CAP_DAC_OVERRIDE.
        bool denied;
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try { Directory.GetFileSystemEntries(locked); denied = false; }
        catch (UnauthorizedAccessException) { denied = true; }
        finally { File.SetUnixFileMode(locked, Owner); }
        if (!denied)
        {
            foreach (var (verb, argument, _) in cases) skip("deleted cwd path: " + Label(verb, argument), "a mode-000 directory is still listable (privileged runner)");
            return;
        }

        Assembly? cli = null;
        try { cli = Assembly.LoadFrom(cliDll); }
        catch (Exception error) when (error is IOException or BadImageFormatException) { Check(false, "the Release CLI assembly loads", error.Message); }
        var entry = cli?.EntryPoint;
        if (entry is null) { Check(false, "the Release CLI assembly has an entry point", cliDll); return; }
        var original = Environment.CurrentDirectory;
        var exitBefore = Environment.ExitCode;
        var measuredAll = new List<string>();
        foreach (var (verb, argument, arguments) in cases)
        {
            var label = Label(verb, argument);
            var args = arguments();
            var previousOut = Console.Out;
            var previousError = Console.Error;
            var previousExit = Environment.ExitCode;
            var capturedOut = new StringWriter();
            var capturedError = new StringWriter();
            var exit = -1;
            var thrown = "";
            var probe = "";
            try
            {
                Directory.SetCurrentDirectory(cwd);
                File.SetUnixFileMode(locked, UnixFileMode.None);
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
                File.SetUnixFileMode(locked, Owner);
                Directory.SetCurrentDirectory(original);
            }
            var line = capturedOut.ToString().TrimEnd('\r', '\n');
            var error = (capturedError.ToString().ReplaceLineEndings(" ").Trim() + thrown).Trim();
            var measured = probe + "; exit " + exit.ToString(CultureInfo.InvariantCulture) + " " + line + (error.Length > 0 ? " stderr " + error : "");
            measuredAll.Add(verb + " " + argument + " -> " + measured);
            string? code = null;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("status", out var status) && status.ValueEquals("failed")
                    && document.RootElement.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String) code = codeElement.GetString();
            }
            catch (JsonException) { }
            var typed = exit == 1 && error.Length == 0 && code is not null && code.Length > 0 && code.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
                && !code.Contains("internal_error", StringComparison.Ordinal);
            Check(typed, label + ": a typed refusal (exit 1, snake_case code, not Program's generic catch nor an internal error, empty stderr)", measured);
            Check(Environment.CurrentDirectory == original, label + ": the working directory is restored (finally)", Environment.CurrentDirectory + " != " + original);
            var written = Directory.GetFiles(scratch, "out-remainder-*.json").Concat(Directory.GetFiles(cwd)).Concat(Directory.GetFiles(parent)).ToArray();
            Check(written.Length == 0, label + ": no output file is written", string.Join(",", written));
        }
        Check(Environment.ExitCode == exitBefore, "unsearchable " + mode + ": Environment.ExitCode is restored (finally)",
            Environment.ExitCode.ToString(CultureInfo.InvariantCulture) + ", before " + exitBefore.ToString(CultureInfo.InvariantCulture));
        // P10-32 probe: every measured case of this mode, one annotation.
        Console.WriteLine("::warning::P10-32 probe " + Environment.OSVersion.Platform + " unsearchable " + mode + ": " + string.Join(" || ", measuredAll).ReplaceLineEndings(" "));
    }
}
