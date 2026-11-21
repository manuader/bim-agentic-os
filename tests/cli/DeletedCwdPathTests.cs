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
        try { Cases(scratch, cliDll, Check, skip); Remainder(scratch, cliDll, Check, skip); LinuxUnsearchable(scratch, cliDll, Check, skip); }
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

        // (2) unsearchable cwd: one relative input and one relative output per verb (both doc-update modes). An output
        // case's arguments take the output path, so the same run can be repeated with an absolute output (the reference).
        var log = Path.Combine(scratch, "remainder-empty.jsonl");
        File.WriteAllBytes(log, Array.Empty<byte>());
        string Plain(string code) => JsonSerializer.Serialize(new { status = "failed", code });
        string Detail(string code, string detail) => JsonSerializer.Serialize(new { status = "failed", code, detail });
        var cases = new List<Unsearchable>
        {
            new("audit-verify", "log", _ => new[] { "audit-verify", RelativeInput }, Plain("input_access_denied")),
            new("audit-verify", "--write-anchor", output => new[] { "audit-verify", dummy, "--write-anchor", output }, null),
            new("audit-verify", "--write-anchor (clean empty log)", output => new[] { "audit-verify", log, "--write-anchor", output }, null),
            new("diff-intent", "--project", _ => new[] { "diff-intent", "--project", RelativeInput, "--intent", dummy }, Plain("input_access_denied")),
            new("doc-realized", "--record", _ => new[] { "doc-realized", "--record", RelativeInput, "--fixture", dummy, "--assignments", dummy, "--document", "uid-1", "--output", Output() }, Detail("input_access_denied", "record")),
            new("doc-realized", "--output", output => new[] { "doc-realized", "--record", dummy, "--fixture", dummy, "--assignments", dummy, "--document", "uid-1", "--output", output }, null),
            new("doc-update", "--before", _ => DocUpdate("--before", false), Detail("input_access_denied", "before")),
            new("doc-update", "--output", output => DocUpdate("--output", false).Select(a => a == RelativeOutput ? output : a).ToArray(), null),
            new("doc-update --probe-dependents", "--before", _ => DocUpdate("--before", true), Probe("input_access_denied", "before")),
            new("doc-update --probe-dependents", "--output", output => DocUpdate("--output", true).Select(a => a == RelativeOutput ? output : a).ToArray(), null),
        };
        foreach (var verb in new[] { "generate-prototypes", "generate-typology", "plan-layouts" })
        {
            var name = verb;
            cases.Add(new(name, "request", _ => new[] { name, RelativeInput, Output() }, Plain("input_access_denied")));
            cases.Add(new(name, "output", output => new[] { name, dummy, output }, null));
        }
        cases.Add(new("export-gltf", "output", output => new[] { "export-gltf", dummy, output }, null, "relative-output.glb"));
        cases.Add(new("bcf-export", "output", output => new[] { "bcf-export", dummy, output }, null, "relative-output.bcfzip"));
        foreach (var mode in new[] { "cwd", "parent" })
            RunUnsearchable(scratch, cliDll, mode, cases, Check, skip);
    }

    // Input is the expected refusal of an input case (null: an output case, whose argument is RelativeName).
    sealed record Unsearchable(string Verb, string Argument, Func<string, string[]> Arguments, string? Input, string RelativeName = RelativeOutput);

    // The refusal of a relative output path that Path.GetFullPath (or the parent check) refuses: the verb's envelope of code.
    static string OutputRefusal(string verb, string code) => verb == "bcf-export"
        ? JsonSerializer.Serialize(new { status = "failed", code = "bcf_export_output_invalid", detail = code })
        : JsonSerializer.Serialize(new { status = "failed", code });

    // chmod with the platform guard inside (the callers also return early on Windows).
    static void Chmod(string path, UnixFileMode mode) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode); }

    static (int Exit, string Line, string Error) Invoke(MethodInfo entry, string[] args, Action? before = null)
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var previousExit = Environment.ExitCode;
        var capturedOut = new StringWriter();
        var capturedError = new StringWriter();
        var thrown = "";
        int exit;
        try
        {
            before?.Invoke();
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
        }
        return (exit, capturedOut.ToString().TrimEnd('\r', '\n'), (capturedError.ToString().ReplaceLineEndings(" ").Trim() + thrown).Trim());
    }

    static void RunUnsearchable(string scratch, string cliDll, string mode, List<Unsearchable> cases, Action<bool, string, string> Check, Action<string, string> skip)
    {
        string Label(Unsearchable c) => "unsearchable " + mode + ": " + c.Verb + " " + c.Argument;
        if (OperatingSystem.IsWindows())
        {
            foreach (var c in cases) skip("deleted cwd path: " + Label(c), "Unix permission bits (chmod 000) only");
            return;
        }
        var parent = Path.Combine(scratch, "locked-parent-" + mode);
        var cwd = Path.Combine(parent, "cwd");
        Directory.CreateDirectory(cwd);
        var locked = mode == "cwd" ? cwd : parent;
        const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        // Behavioural privilege check (the tests/cli chmod rule): a mode-000 directory still listable means root/CAP_DAC_OVERRIDE.
        bool denied;
        Chmod(locked, UnixFileMode.None);
        try { Directory.GetFileSystemEntries(locked); denied = false; }
        catch (UnauthorizedAccessException) { denied = true; }
        finally { Chmod(locked, Owner); }
        if (!denied)
        {
            foreach (var c in cases) skip("deleted cwd path: " + Label(c), "a mode-000 directory is still listable (privileged runner)");
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
        var anyFailing = false;
        foreach (var c in cases)
        {
            var label = Label(c);
            var probe = "";
            var fullPathThrew = false;
            (int Exit, string Line, string Error) run;
            try
            {
                run = Invoke(entry, c.Arguments(c.RelativeName), () =>
                {
                    Directory.SetCurrentDirectory(cwd);
                    Chmod(locked, UnixFileMode.None);
                    try { Path.GetFullPath(RelativeInput); probe = "GetFullPath accepted"; }
                    catch (Exception failure) { probe = "GetFullPath threw " + failure.GetType().FullName; fullPathThrew = true; }
                });
            }
            finally
            {
                Chmod(locked, Owner);
                Directory.SetCurrentDirectory(original);
            }
            var (exit, line, error) = run;
            var measured = probe + "; exit " + exit.ToString(CultureInfo.InvariantCulture) + " " + line + (error.Length > 0 ? " stderr " + error : "");
            measuredAll.Add(c.Verb + " " + c.Argument + " -> " + measured);
            string? code = null;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String)
                    code = codeElement.GetString();
            }
            catch (JsonException) { }
            var typed = exit == 1 && error.Length == 0 && code is not null && code.Length > 0 && code.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
                && !code.Contains("internal_error", StringComparison.Ordinal);
            Check(typed, label + ": a typed refusal (exit 1, snake_case code, not Program's generic catch nor an internal error, empty stderr)", measured);

            // The exact line, as measured (run 44824612295): every input is input_access_denied. A relative output is
            // output_path_invalid where GetFullPath throws (macOS: UnauthorizedAccessException in both modes),
            // output_directory_not_found under a mode-000 parent where it does not (Linux: the absolute parent is not
            // searchable). Under a mode-000 working directory on Linux the output path is accepted, so the run equals the
            // same run with an absolute output (the reference) when that refuses, and is the verb's typed output_io_error
            // with detail UnauthorizedAccessException when the reference writes.
            string? expected;
            var reference = "";
            if (c.Input is not null) expected = c.Input;
            else if (fullPathThrew) expected = OutputRefusal(c.Verb, "output_path_invalid");
            else if (mode == "parent") expected = OutputRefusal(c.Verb, "output_directory_not_found");
            else
            {
                var refDirectory = Path.Combine(scratch, "reference-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(refDirectory);
                var refRun = Invoke(entry, c.Arguments(Path.Combine(refDirectory, c.RelativeName)));
                var refWrote = Directory.GetFiles(refDirectory).Length > 0;
                Directory.Delete(refDirectory, true);
                reference = "; reference exit " + refRun.Exit.ToString(CultureInfo.InvariantCulture) + (refWrote ? " wrote" : " ") + refRun.Line;
                expected = refRun.Exit == 1 && !refWrote && refRun.Error.Length == 0 ? refRun.Line
                    : refRun.Exit == 0 && refWrote ? (c.Verb == "bcf-export"
                        ? JsonSerializer.Serialize(new { status = "failed", code = "bcf_export_output_io_error", detail = "UnauthorizedAccessException" })
                        : JsonSerializer.Serialize(new { status = "failed", code = "output_io_error", detail = "UnauthorizedAccessException" }))
                    : null;
            }
            if (c.Verb == "audit-verify" && c.Argument == "--write-anchor (clean empty log)" && !fullPathThrew && mode == "cwd")
                Check(expected == JsonSerializer.Serialize(new { status = "failed", code = "output_io_error", detail = "UnauthorizedAccessException" }),
                    label + ": the reference run writes the anchor, so the relative run reaches the exclusive write", measured + reference);
            var exact = expected is not null && exit == 1 && line == expected && error.Length == 0;
            if (!exact) anyFailing = true;
            Check(exact, label + ": the exact refusal " + (expected ?? "(no expectation: the reference run neither refused nor wrote)"), measured + reference);
            Check(Environment.CurrentDirectory == original, label + ": the working directory is restored (finally)", Environment.CurrentDirectory + " != " + original);
            var written = Directory.GetFiles(scratch, "out-remainder-*.json").Concat(Directory.GetFiles(cwd)).Concat(Directory.GetFiles(parent)).ToArray();
            Check(written.Length == 0, label + ": no output file is written", string.Join(",", written));
        }
        Check(Environment.ExitCode == exitBefore, "unsearchable " + mode + ": Environment.ExitCode is restored (finally)",
            Environment.ExitCode.ToString(CultureInfo.InvariantCulture) + ", before " + exitBefore.ToString(CultureInfo.InvariantCulture));
        // Probe facts: one annotation per mode with a failing case, listing every measured case of that mode.
        if (anyFailing) Console.WriteLine("::warning::P10-32 probe " + Environment.OSVersion.Platform + " unsearchable " + mode + ": " + string.Join(" || ", measuredAll).ReplaceLineEndings(" "));
    }

    // ---- P10-35: a mode-000 working directory on Linux, literal pins and the late write ----
    // On Linux Path.GetFullPath of a relative path is accepted while the working directory itself is mode 000 (getcwd
    // needs no search permission; macOS throws UnauthorizedAccessException, pinned by P10-32 as output_path_invalid). The
    // output rule (GltfExportCommand.OutputProblem) then accepts the relative output: the parent (the working directory)
    // exists to stat through its searchable parent, and File.Exists/Directory.Exists of the output report false on EACCES.
    // So the refusal is never up front: a verb that refuses its input first prints that input refusal (pinned literally
    // below for generate-prototypes, generate-typology, plan-layouts, export-gltf and bcf-export with a "{}" input), and a
    // verb that reaches its exclusive CreateNew write gets EACCES there, the verb's typed output_io_error with detail
    // UnauthorizedAccessException (audit-verify --write-anchor of a clean empty log). The reason is pinned as facts measured
    // in the same locked state. Linux only (counted SKIP elsewhere), skipped when a mode-000 directory stays listable (root).
    const string Measure35 = "P10-35 probe: to be measured";

    static void LinuxUnsearchable(string scratch, string cliDll, Action<bool, string, string> Check, Action<string, string> skip)
    {
        var dummy = Path.Combine(scratch, "p10-35-dummy.json");
        File.WriteAllText(dummy, "{}\n", new UTF8Encoding(false));
        var log = Path.Combine(scratch, "p10-35-empty.jsonl");
        File.WriteAllBytes(log, Array.Empty<byte>());
        // (verb, arguments with the relative output, the literal line). <scratch> stands for the scratch directory.
        var cases = new List<(string Verb, string[] Arguments, string Expected)>
        {
            ("generate-prototypes", new[] { "generate-prototypes", dummy, RelativeOutput }, Measure35),
            ("generate-typology", new[] { "generate-typology", dummy, RelativeOutput }, Measure35),
            ("plan-layouts", new[] { "plan-layouts", dummy, RelativeOutput }, Measure35),
            ("export-gltf", new[] { "export-gltf", dummy, "relative-output.glb" }, Measure35),
            ("bcf-export", new[] { "bcf-export", dummy, "relative-output.bcfzip" }, Measure35),
            ("audit-verify --write-anchor (clean empty log)", new[] { "audit-verify", log, "--write-anchor", RelativeOutput },
                JsonSerializer.Serialize(new { status = "failed", code = "output_io_error", detail = "UnauthorizedAccessException" })),
        };
        const string Reason = "the late-write reason";
        string Label(string what) => "deleted cwd path: P10-35 unsearchable cwd (Linux): " + what;
        if (!OperatingSystem.IsLinux())
        {
            foreach (var c in cases) skip(Label(c.Verb + " output"), "Linux only: macOS GetFullPath refuses the relative output (P10-32), Windows has no Unix permission bits");
            skip(Label(Reason), "Linux only");
            return;
        }
        var parent = Path.Combine(scratch, "p10-35-parent");
        var cwd = Path.Combine(parent, "cwd");
        Directory.CreateDirectory(cwd);
        const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        bool denied;
        Chmod(cwd, UnixFileMode.None);
        try { Directory.GetFileSystemEntries(cwd); denied = false; }
        catch (UnauthorizedAccessException) { denied = true; }
        finally { Chmod(cwd, Owner); }
        if (!denied)
        {
            foreach (var c in cases) skip(Label(c.Verb + " output"), "a mode-000 directory is still listable (privileged runner)");
            skip(Label(Reason), "a mode-000 directory is still listable (privileged runner)");
            return;
        }

        Assembly? cli = null;
        try { cli = Assembly.LoadFrom(cliDll); }
        catch (Exception error) when (error is IOException or BadImageFormatException) { Check(false, "the Release CLI assembly loads", error.Message); }
        var entry = cli?.EntryPoint;
        if (cli is null || entry is null) { Check(false, "the Release CLI assembly has an entry point", cliDll); return; }
        var original = Environment.CurrentDirectory;
        var exitBefore = Environment.ExitCode;
        foreach (var (verb, arguments, expected) in cases)
        {
            (int Exit, string Line, string Error) run;
            try
            {
                run = Invoke(entry, arguments, () =>
                {
                    Directory.SetCurrentDirectory(cwd);
                    Chmod(cwd, UnixFileMode.None);
                });
            }
            finally
            {
                Chmod(cwd, Owner);
                Directory.SetCurrentDirectory(original);
            }
            var line = run.Line.Replace(scratch, "<scratch>", StringComparison.Ordinal);
            var measured = "exit " + run.Exit.ToString(CultureInfo.InvariantCulture) + " " + line + (run.Error.Length > 0 ? " stderr " + run.Error : "");
            Check(run.Exit == 1 && line == expected && run.Error.Length == 0 && !line.Contains("internal_error", StringComparison.Ordinal),
                Label(verb + " output") + ": a relative output under a mode-000 working directory is exactly " + expected + " (exit 1, empty stderr)", measured);
            Check(Environment.CurrentDirectory == original, Label(verb + " output") + ": the working directory is restored (finally)", Environment.CurrentDirectory + " != " + original);
            var written = Directory.GetFiles(cwd).Concat(Directory.GetFiles(parent)).ToArray();
            Check(written.Length == 0, Label(verb + " output") + ": no output file is written", string.Join(",", written));
        }

        // The reason, measured in the same state (working directory = cwd, cwd mode 000, its parent searchable).
        var outputProblem = cli.GetType("Bim.Cli.GltfExportCommand", false)?.GetMethod("OutputProblem", BindingFlags.Static | BindingFlags.NonPublic, null,
            new[] { typeof(string), typeof(IReadOnlyCollection<string>), typeof(string).MakeByRefType() }, null);
        Check(outputProblem is not null, Label(Reason) + ": GltfExportCommand.OutputProblem(path, extensions, out format) is found", cliDll);
        string full = "", fullPath = "", linkTarget = "", rule = "", create = "";
        bool parentExists = false, fileExists = true, directoryExists = true;
        try
        {
            Directory.SetCurrentDirectory(cwd);
            full = Path.Combine(Environment.CurrentDirectory, RelativeOutput);
            Chmod(cwd, UnixFileMode.None);
            try { fullPath = "accepted " + Path.GetFullPath(RelativeOutput); }
            catch (Exception failure) { fullPath = "threw " + failure.GetType().FullName; }
            parentExists = Directory.Exists(cwd);
            fileExists = File.Exists(full);
            directoryExists = Directory.Exists(full);
            try { linkTarget = "accepted " + (new FileInfo(full).LinkTarget ?? "null"); }
            catch (Exception failure) { linkTarget = "threw " + failure.GetType().FullName; }
            try { rule = "returned " + ((string?)outputProblem?.Invoke(null, new object?[] { RelativeOutput, new[] { ".json" }, null }) ?? "null"); }
            catch (Exception failure) { rule = "threw " + (failure is TargetInvocationException { InnerException: { } cause } ? cause : failure).GetType().FullName; }
            try { using (new FileStream(full, FileMode.CreateNew, FileAccess.Write)) { } create = "created"; }
            catch (Exception failure) { create = "threw " + failure.GetType().FullName; }
        }
        finally
        {
            Chmod(cwd, Owner);
            Directory.SetCurrentDirectory(original);
        }
        var facts = "GetFullPath " + fullPath + "; Directory.Exists(cwd) " + parentExists + "; File.Exists " + fileExists + "; Directory.Exists " + directoryExists
            + "; LinkTarget " + linkTarget + "; OutputProblem " + rule + "; CreateNew " + create;
        facts = facts.Replace(scratch, "<scratch>", StringComparison.Ordinal);
        Check(fullPath == "accepted " + full, Label(Reason) + ": Path.GetFullPath of the relative output is accepted (the working directory under it)", facts);
        Check(parentExists && !fileExists && !directoryExists, Label(Reason) + ": the parent exists and File.Exists/Directory.Exists of the output report false", facts);
        Check(linkTarget == "accepted null", Label(Reason) + ": FileInfo.LinkTarget of the output is null (not a link), no exception", facts);
        Check(rule == "returned null", Label(Reason) + ": OutputProblem accepts the relative output (no up-front refusal)", facts);
        Check(create == "threw System.UnauthorizedAccessException", Label(Reason) + ": the exclusive CreateNew write throws UnauthorizedAccessException (the late output_io_error)", facts);
        if (File.Exists(full)) File.Delete(full);
        Check(Environment.CurrentDirectory == original, Label(Reason) + ": the working directory is restored (finally)", Environment.CurrentDirectory + " != " + original);
        Check(Environment.ExitCode == exitBefore, Label("Environment.ExitCode is restored (finally)"),
            Environment.ExitCode.ToString(CultureInfo.InvariantCulture) + ", before " + exitBefore.ToString(CultureInfo.InvariantCulture));
    }
}
