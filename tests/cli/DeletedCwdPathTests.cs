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
    const string Prefix = "deleted cwd path: ";

    public static void Run(string root, string work, string dotnet, string cliDll, Action<bool, string, string> check, Action<string, string> skip)
    {
        // P10-37: the prefix is added here once; a label that already carries it is itself a failure (no doubled prefix).
        void Check(bool condition, string name, string detail = "")
        {
            if (name.StartsWith(Prefix, StringComparison.Ordinal)) check(false, Prefix + "P10-37 a check label carries the prefix once", name);
            check(condition, Prefix + name, detail);
        }
        // P10-39: every skip label carries the prefix exactly once (the P10-37 rule for checks, applied to skips).
        void Skip(string name, string why)
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal) || name.IndexOf(Prefix, Prefix.Length, StringComparison.Ordinal) >= 0)
                check(false, Prefix + "P10-39 a skip label carries the prefix once", name);
            skip(name, why);
        }
        var scratch = Path.Combine(work, "deleted-cwd-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            Cases(scratch, cliDll, Check, Skip); Remainder(scratch, cliDll, Check, Skip); LinuxUnsearchable(scratch, cliDll, Check, Skip); UnsearchableRemainder(root, scratch, cliDll, Check, Skip);
            LateWrites(scratch, cliDll, Check, Skip); SkillsLockUnsearchable(scratch, cliDll, Check, Skip); P10_41(root, scratch, cliDll, Check, Skip);
        }
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
    static void LinuxUnsearchable(string scratch, string cliDll, Action<bool, string, string> Check, Action<string, string> skip)
    {
        var dummy = Path.Combine(scratch, "p10-35-dummy.json");
        File.WriteAllText(dummy, "{}\n", new UTF8Encoding(false));
        var log = Path.Combine(scratch, "p10-35-empty.jsonl");
        File.WriteAllBytes(log, Array.Empty<byte>());
        // (verb, arguments with the relative output, the literal line as measured in run 44892086261; <scratch> stands for the
        // scratch directory). Each equals the same run with an absolute output (P10-32's reference): the input is refused first.
        var cases = new List<(string Verb, string[] Arguments, string Expected)>
        {
            ("generate-prototypes", new[] { "generate-prototypes", dummy, RelativeOutput }, """{"status":"failed","code":"generate_prototypes_request_invalid","detail":"typology"}"""),
            ("generate-typology", new[] { "generate-typology", dummy, RelativeOutput }, """{"status":"failed","code":"generate_typology_request_invalid","detail":"pack"}"""),
            ("plan-layouts", new[] { "plan-layouts", dummy, RelativeOutput }, """{"status":"failed","code":"plan_layouts_brief_invalid","detail":"version_unsupported at brief_version"}"""),
            ("export-gltf", new[] { "export-gltf", dummy, "relative-output.glb" }, """{"status":"failed","code":"bim_ir_invalid","detail":"Elements is null"}"""),
            ("bcf-export", new[] { "bcf-export", dummy, "relative-output.bcfzip" }, """{"status":"failed","code":"bcf_export_input_invalid","detail":"$.kind"}"""),
            ("audit-verify --write-anchor (clean empty log)", new[] { "audit-verify", log, "--write-anchor", RelativeOutput },
                JsonSerializer.Serialize(new { status = "failed", code = "output_io_error", detail = "UnauthorizedAccessException" })),
        };
        const string Reason = "the late-write reason";
        // Check adds the "deleted cwd path: " prefix; skip does not (P10-37: the prefix was doubled on every Check).
        string Label(string what) => "P10-35 unsearchable cwd (Linux): " + what;
        if (!OperatingSystem.IsLinux())
        {
            foreach (var c in cases) skip(Prefix + Label(c.Verb + " output"), "Linux only: macOS GetFullPath refuses the relative output (P10-32), Windows has no Unix permission bits");
            skip(Prefix + Label(Reason), "Linux only");
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
            foreach (var c in cases) skip(Prefix + Label(c.Verb + " output"), "a mode-000 directory is still listable (privileged runner)");
            skip(Prefix + Label(Reason), "a mode-000 directory is still listable (privileged runner)");
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

    // ---- P10-37: the unsearchable-cwd remainder (Linux) ----
    // (1) Parent mode: the working directory keeps 0700 and its parent is mode 000. The five verbs of P10-35 with the same
    // "{}" input and a relative output print a literal line: on Linux GetFullPath accepts, the absolute parent of the output
    // (the working directory) cannot be reached through its mode-000 parent, so the output rule refuses up front.
    // (2) A late write on a named verb: export-gltf with a valid BIM-IR (examples/after.json, absolute) and a relative
    // output under a mode-000 working directory passes the output rule and every input check and fails at the exclusive
    // CreateNew write; the same run with an absolute output writes (precondition). Exit 1, empty stderr, nothing written,
    // modes and the working directory restored in a finally. Linux only (counted SKIP elsewhere), and when a mode-000
    // directory stays listable (root).
    static void UnsearchableRemainder(string root, string scratch, string cliDll, Action<bool, string, string> Check, Action<string, string> skip)
    {
        var dummy = Path.Combine(scratch, "p10-37-dummy.json");
        File.WriteAllText(dummy, "{}\n", new UTF8Encoding(false));
        var project = Path.Combine(root, "examples", "after.json");
        string Failed(string code, string? detail = null) => detail is null
            ? JsonSerializer.Serialize(new { status = "failed", code })
            : JsonSerializer.Serialize(new { status = "failed", code, detail });
        // (mode, label, arguments with the relative output, the literal line)
        var cases = new List<(string Mode, string Label, string[] Arguments, string Expected)>
        {
            ("parent", "generate-prototypes output", new[] { "generate-prototypes", dummy, RelativeOutput }, Failed("output_directory_not_found")),
            ("parent", "generate-typology output", new[] { "generate-typology", dummy, RelativeOutput }, Failed("output_directory_not_found")),
            ("parent", "plan-layouts output", new[] { "plan-layouts", dummy, RelativeOutput }, Failed("output_directory_not_found")),
            ("parent", "export-gltf output", new[] { "export-gltf", dummy, "relative-output.glb" }, Failed("output_directory_not_found")),
            ("parent", "bcf-export output", new[] { "bcf-export", dummy, "relative-output.bcfzip" }, Failed("bcf_export_output_invalid", "output_directory_not_found")),
            ("cwd", "export-gltf output (valid BIM-IR, late write)", new[] { "export-gltf", project, "relative-output.glb" }, Failed("output_io_error", "UnauthorizedAccessException")),
        };
        string Label(string mode, string what) => "P10-37 unsearchable " + mode + " (Linux): " + what;
        const string Reference = "export-gltf valid BIM-IR with an absolute output writes (precondition)";
        if (!OperatingSystem.IsLinux())
        {
            foreach (var c in cases) skip(Prefix + Label(c.Mode, c.Label), "Linux only: macOS GetFullPath refuses the relative output (P10-32), Windows has no Unix permission bits");
            skip(Prefix + Label("cwd", Reference), "Linux only");
            return;
        }
        const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var probeDirectory = Path.Combine(scratch, "p10-37-probe");
        Directory.CreateDirectory(probeDirectory);
        bool denied;
        Chmod(probeDirectory, UnixFileMode.None);
        try { Directory.GetFileSystemEntries(probeDirectory); denied = false; }
        catch (UnauthorizedAccessException) { denied = true; }
        finally { Chmod(probeDirectory, Owner); }
        if (!denied)
        {
            foreach (var c in cases) skip(Prefix + Label(c.Mode, c.Label), "a mode-000 directory is still listable (privileged runner)");
            skip(Prefix + Label("cwd", Reference), "a mode-000 directory is still listable (privileged runner)");
            return;
        }

        Assembly? cli = null;
        try { cli = Assembly.LoadFrom(cliDll); }
        catch (Exception error) when (error is IOException or BadImageFormatException) { Check(false, "the Release CLI assembly loads", error.Message); }
        var entry = cli?.EntryPoint;
        if (entry is null) { Check(false, "the Release CLI assembly has an entry point", cliDll); return; }
        var original = Environment.CurrentDirectory;
        var exitBefore = Environment.ExitCode;

        // Precondition of (2): the input is valid, so the relative run's refusal can only come from the write.
        var refDirectory = Path.Combine(scratch, "p10-37-reference");
        Directory.CreateDirectory(refDirectory);
        var refOutput = Path.Combine(refDirectory, "model.glb");
        var refRun = Invoke(entry, new[] { "export-gltf", project, refOutput });
        var refWrote = File.Exists(refOutput) && new FileInfo(refOutput).Length > 0;
        Directory.Delete(refDirectory, true);
        Check(refRun.Exit == 0 && refWrote && refRun.Error.Length == 0 && refRun.Line.StartsWith("{\"status\":\"succeeded\",\"format\":\"glb\"", StringComparison.Ordinal),
            Label("cwd", Reference), "exit " + refRun.Exit.ToString(CultureInfo.InvariantCulture) + (refWrote ? " wrote " : " ") + refRun.Line + (refRun.Error.Length > 0 ? " stderr " + refRun.Error : ""));

        foreach (var (mode, what, arguments, expected) in cases)
        {
            var label = Label(mode, what);
            var parent = Path.Combine(scratch, "p10-37-" + mode + "-" + Guid.NewGuid().ToString("N"));
            var cwd = Path.Combine(parent, "cwd");
            Directory.CreateDirectory(cwd);
            var locked = mode == "cwd" ? cwd : parent;
            var probe = "";
            (int Exit, string Line, string Error) run;
            try
            {
                run = Invoke(entry, arguments, () =>
                {
                    Directory.SetCurrentDirectory(cwd);
                    Chmod(locked, UnixFileMode.None);
                    try { Path.GetFullPath(RelativeOutput); probe = "GetFullPath accepted"; }
                    catch (Exception failure) { probe = "GetFullPath threw " + failure.GetType().FullName; }
                });
            }
            finally
            {
                Chmod(locked, Owner);
                Directory.SetCurrentDirectory(original);
            }
            var line = run.Line.Replace(scratch, "<scratch>", StringComparison.Ordinal);
            var measured = probe + "; exit " + run.Exit.ToString(CultureInfo.InvariantCulture) + " " + line + (run.Error.Length > 0 ? " stderr " + run.Error : "");
            Check(probe == "GetFullPath accepted", label + ": precondition: Path.GetFullPath of the relative output is accepted (Linux)", measured);
            Check(run.Exit == 1 && line == expected && run.Error.Length == 0 && !line.Contains("internal_error", StringComparison.Ordinal),
                label + ": a relative output is exactly " + expected + " (exit 1, empty stderr)", measured);
            Check(Environment.CurrentDirectory == original, label + ": the working directory is restored (finally)", Environment.CurrentDirectory + " != " + original);
            var written = Directory.GetFiles(cwd).Concat(Directory.GetFiles(parent)).ToArray();
            Check(written.Length == 0, label + ": no output file is written", string.Join(",", written));
            Directory.Delete(parent, true);
        }
        Check(Environment.ExitCode == exitBefore, Label("both", "Environment.ExitCode is restored (finally)"),
            Environment.ExitCode.ToString(CultureInfo.InvariantCulture) + ", before " + exitBefore.ToString(CultureInfo.InvariantCulture));
    }

    // ---- P10-39: the remaining late writes (Linux) and skills-lock through SkillLockTool.PathProblem (Linux, macOS) ----
    // (1) generate-prototypes, generate-typology, plan-layouts and bcf-export with a valid input (absolute) and a relative
    // output under a mode-000 working directory: GetFullPath accepts (Linux), the output rule accepts, the input is read and
    // the result built, and the exclusive CreateNew write meets EACCES: the verb's typed output_io_error with detail
    // UnauthorizedAccessException (bcf-export: bcf_export_output_io_error). Precondition per verb: the same input with an
    // absolute output writes (exit 0, succeeded). Linux only (macOS refuses the relative output up front, P10-32).
    // (2) skills-lock verify with one relative path (the skills directory, or the lock) under a mode-000 working directory
    // or parent. Both paths pass SkillLockTool.PathProblem (shared with the MCP tool skills_lock_verify). Precondition: with
    // the working directory searchable the relative call verifies. Linux: GetFullPath accepts and the engine reports the
    // unreadable path (the P10-37 MCP report for skills; lock_missing for the lock). macOS: GetFullPath throws
    // UnauthorizedAccessException, which PathProblem must refuse as skills_lock_path_invalid (detail "skills"|"lock"), never
    // Program's generic catch. Unix only; counted SKIP on Windows and when a mode-000 directory stays listable (root).
    // Exit 1, empty stderr, nothing written, the lock and pack bytes unchanged; modes, cwd and ExitCode restored.
    static string? PrivilegeSkip(string scratch, string name)
    {
        if (OperatingSystem.IsWindows()) return "Unix permission bits (chmod 000) only";
        var probe = Path.Combine(scratch, name);
        Directory.CreateDirectory(probe);
        bool denied;
        Chmod(probe, UnixFileMode.None);
        try { Directory.GetFileSystemEntries(probe); denied = false; }
        catch (UnauthorizedAccessException) { denied = true; }
        finally { Chmod(probe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        return denied ? null : "a mode-000 directory is still listable (privileged runner)";
    }

    static void LateWrites(string scratch, string cliDll, Action<bool, string, string> Check, Action<string, string> skip)
    {
        string Write(string name, string text) { var path = Path.Combine(scratch, name); File.WriteAllText(path, text, new UTF8Encoding(false)); return path; }
        var prototypes = Write("p10-39-prototypes.json", "{\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"alternatives\":2}");
        var typology = Write("p10-39-typology.json", "{\"pack\":\"bim.typology.vivienda-unifamiliar\",\"family_size\":3,\"lot\":{\"width_m\":16,\"depth_m\":16},\"alternatives\":2,\"seed\":0,\"project_id\":\"00000000-0000-4000-8000-0000000007a7\"}");
        var brief = Write("p10-39-brief.json", "{\"brief_version\":1,\"name\":\"Small studio\",\"constraints\":{\"site\":{\"width_m\":6,\"depth_m\":8},\"levels\":1},"
            + "\"rooms\":[{\"id\":\"living\",\"name\":\"Living\",\"target_area_m2\":20,\"min_area_m2\":15},{\"id\":\"bath\",\"name\":\"Bath\",\"target_area_m2\":4,\"min_area_m2\":3}],"
            + "\"adjacencies\":[{\"a\":\"living\",\"b\":\"bath\",\"kind\":\"required\"}]}");
        var issues = Write("p10-39-issues.json", "{\"kind\":\"bcf-issues\",\"version\":1,\"project\":{\"id\":\"00000000-0000-4000-8000-000000000039\",\"name\":\"P10-39\",\"author\":\"tests\",\"timestamp\":\"2026-11-02T10:00:00Z\"},"
            + "\"issues\":[{\"id\":\"00000000-0000-4000-8000-000000000391\",\"title\":\"late write\",\"severity\":\"info\"}]}");
        const string IoError = """{"status":"failed","code":"output_io_error","detail":"UnauthorizedAccessException"}""";
        // (verb, arguments for an output path, relative output name, the literal line)
        var cases = new List<(string Verb, Func<string, string[]> Arguments, string Relative, string Expected)>
        {
            ("generate-prototypes", output => new[] { "generate-prototypes", prototypes, output }, RelativeOutput, IoError),
            ("generate-typology", output => new[] { "generate-typology", typology, output }, RelativeOutput, IoError),
            ("plan-layouts", output => new[] { "plan-layouts", brief, output, "--alternatives", "1" }, RelativeOutput, IoError),
            ("bcf-export", output => new[] { "bcf-export", issues, output }, "relative-output.bcfzip", """{"status":"failed","code":"bcf_export_output_io_error","detail":"UnauthorizedAccessException"}"""),
        };
        string Label(string what) => "P10-39 late write, unsearchable cwd (Linux): " + what;
        var why = !OperatingSystem.IsLinux() ? "Linux only: macOS GetFullPath refuses the relative output up front (P10-32), Windows has no Unix permission bits" : PrivilegeSkip(scratch, "p10-39-late-probe");
        if (why is not null)
        {
            foreach (var c in cases) skip(Prefix + Label(c.Verb + " valid input, relative output"), why);
            return;
        }
        Assembly? cli = null;
        try { cli = Assembly.LoadFrom(cliDll); }
        catch (Exception error) when (error is IOException or BadImageFormatException) { Check(false, "the Release CLI assembly loads", error.Message); }
        var entry = cli?.EntryPoint;
        if (entry is null) { Check(false, "the Release CLI assembly has an entry point", cliDll); return; }
        var original = Environment.CurrentDirectory;
        var exitBefore = Environment.ExitCode;
        const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        foreach (var (verb, arguments, relative, expected) in cases)
        {
            var label = Label(verb + " valid input, relative output");
            var refDirectory = Path.Combine(scratch, "p10-39-reference-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(refDirectory);
            var refOutput = Path.Combine(refDirectory, relative);
            var refRun = Invoke(entry, arguments(refOutput));
            var refWrote = File.Exists(refOutput) && new FileInfo(refOutput).Length > 0;
            Directory.Delete(refDirectory, true);
            Check(refRun.Exit == 0 && refWrote && refRun.Error.Length == 0 && refRun.Line.StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal),
                label + ": precondition: the same input with an absolute output writes", "exit " + refRun.Exit.ToString(CultureInfo.InvariantCulture) + (refWrote ? " wrote " : " ")
                + refRun.Line.Replace(scratch, "<scratch>", StringComparison.Ordinal) + (refRun.Error.Length > 0 ? " stderr " + refRun.Error : ""));

            var parent = Path.Combine(scratch, "p10-39-late-" + Guid.NewGuid().ToString("N"));
            var cwd = Path.Combine(parent, "cwd");
            Directory.CreateDirectory(cwd);
            var probe = "";
            (int Exit, string Line, string Error) run;
            try
            {
                run = Invoke(entry, arguments(relative), () =>
                {
                    Directory.SetCurrentDirectory(cwd);
                    Chmod(cwd, UnixFileMode.None);
                    try { Path.GetFullPath(relative); probe = "GetFullPath accepted"; }
                    catch (Exception failure) { probe = "GetFullPath threw " + failure.GetType().FullName; }
                });
            }
            finally
            {
                Chmod(cwd, Owner);
                Directory.SetCurrentDirectory(original);
            }
            var line = run.Line.Replace(scratch, "<scratch>", StringComparison.Ordinal);
            var measured = probe + "; exit " + run.Exit.ToString(CultureInfo.InvariantCulture) + " " + line + (run.Error.Length > 0 ? " stderr " + run.Error : "");
            Check(probe == "GetFullPath accepted", label + ": precondition: Path.GetFullPath of the relative output is accepted (Linux)", measured);
            Check(run.Exit == 1 && line == expected && run.Error.Length == 0, label + ": the late write is exactly " + expected + " (exit 1, empty stderr)", measured);
            Check(Environment.CurrentDirectory == original, label + ": the working directory is restored (finally)", Environment.CurrentDirectory + " != " + original);
            var written = Directory.GetFiles(cwd).Concat(Directory.GetFiles(parent)).ToArray();
            Check(written.Length == 0, label + ": no output file is written", string.Join(",", written));
            Directory.Delete(parent, true);
        }
        Check(Environment.ExitCode == exitBefore, Label("Environment.ExitCode is restored (finally)"),
            Environment.ExitCode.ToString(CultureInfo.InvariantCulture) + ", before " + exitBefore.ToString(CultureInfo.InvariantCulture));
    }

    // The P10-37 MCP report (tests/mcp/DeletedCwdPathToolTests.cs LockedSkills37): the CLI verify line equals the MCP text.
    const string LockedSkills39 = """{"status":"failed","code":"skills_invalid","mode":"verify","verified":false,"lock_version":1,"content_hash":"bim-skill-pack-sha256-v1","packs":[{"id":"bim.test.gamma","version":"1.0.0","content_sha256":"9adb9e8f6b09883bb1579edc8fd96f171301b897581b906020a7a5d4aa6b0000","state":"skill_lock_missing","detail":"pack directory not found under the skills root"}],"issues":[{"code":"skills_invalid","subject":".","detail":"root_missing: skills root directory not found"},{"code":"skill_lock_missing","subject":"bim.test.gamma","detail":"pack directory not found under the skills root"}]}""";
    const string LockName39 = "skills.lock.json";
    const string LockMissing39 = """{"status":"failed","code":"lock_missing","subject":"skills.lock.json","detail":"lock file not found","mode":"verify","verified":false,"packs":[],"issues":[{"code":"lock_missing","subject":"skills.lock.json","detail":"lock file not found"}]}""";

    static void SkillsLockUnsearchable(string scratch, string cliDll, Action<bool, string, string> Check, Action<string, string> skip)
    {
        const string Pack = "bim.test.gamma";
        var cases = new List<(string Mode, string Parameter)> { ("cwd", "skills"), ("cwd", "lock"), ("parent", "skills"), ("parent", "lock") };
        string Label(string mode, string what) => "P10-39 skills-lock verify, unsearchable " + mode + ": " + what;
        var why = PrivilegeSkip(scratch, "p10-39-lock-probe");
        if (why is not null)
        {
            foreach (var (mode, parameter) in cases) skip(Prefix + Label(mode, "relative " + parameter), why);
            return;
        }
        Assembly? cli = null;
        try { cli = Assembly.LoadFrom(cliDll); }
        catch (Exception error) when (error is IOException or BadImageFormatException) { Check(false, "the Release CLI assembly loads", error.Message); }
        var entry = cli?.EntryPoint;
        if (entry is null) { Check(false, "the Release CLI assembly has an entry point", cliDll); return; }
        var original = Environment.CurrentDirectory;
        var exitBefore = Environment.ExitCode;
        const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var manifest = new UTF8Encoding(false).GetBytes("{\"manifest_version\":1,\"id\":\"" + Pack + "\",\"version\":\"1.0.0\",\"kind\":\"qa\",\"description\":\"P10-37 lock pack\",\"priority\":10}\n");
        foreach (var (mode, parameter) in cases)
        {
            var label = Label(mode, "relative " + parameter);
            var parent = Path.Combine(scratch, "p10-39-lock-" + mode + "-" + Guid.NewGuid().ToString("N"));
            var cwd = Path.Combine(parent, "cwd");
            Directory.CreateDirectory(cwd);
            // The relative entry lives in the working directory; the other one is absolute outside the locked tree.
            var outside = Path.Combine(scratch, "p10-39-lock-outside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outside);
            var skills = Path.Combine(parameter == "skills" ? cwd : outside, "skills");
            Directory.CreateDirectory(Path.Combine(skills, Pack));
            var packFile = Path.Combine(skills, Pack, "skill.json");
            File.WriteAllBytes(packFile, manifest);
            var lockFile = Path.Combine(parameter == "lock" ? cwd : outside, LockName39);
            File.WriteAllBytes(lockFile, Bim.Core.SkillLockFile.Serialize(Bim.Core.SkillLocking.Pin(skills, new[] { Pack })));
            var lockBytes = File.ReadAllBytes(lockFile);
            string[] args = parameter == "skills" ? new[] { "skills-lock", "verify", "skills", lockFile } : new[] { "skills-lock", "verify", skills, LockName39 };
            var relative = args[parameter == "skills" ? 2 : 3];
            var locked = mode == "cwd" ? cwd : parent;
            var entriesBefore = Directory.GetFileSystemEntries(cwd).Length;

            (int Exit, string Line, string Error, string Probe) Call(bool lockIt)
            {
                var probe = "";
                (int Exit, string Line, string Error) run;
                try
                {
                    run = Invoke(entry, args, () =>
                    {
                        Directory.SetCurrentDirectory(cwd);
                        if (lockIt) Chmod(locked, UnixFileMode.None);
                        try { Path.GetFullPath(relative); probe = "GetFullPath accepted"; }
                        catch (Exception failure) { probe = "GetFullPath threw " + failure.GetType().FullName; }
                    });
                }
                finally
                {
                    Chmod(locked, Owner);
                    Directory.SetCurrentDirectory(original);
                }
                return (run.Exit, run.Line.Replace(scratch, "<scratch>", StringComparison.Ordinal), run.Error, probe);
            }
            var reference = Call(false);
            Check(reference.Exit == 0 && reference.Error.Length == 0 && reference.Line.StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal) && reference.Line.Contains("\"verified\":true", StringComparison.Ordinal),
                label + ": precondition: with the working directory searchable the relative " + parameter + " path verifies", "exit " + reference.Exit.ToString(CultureInfo.InvariantCulture) + " " + reference.Line + " " + reference.Error);
            var locked1 = Call(true);
            var measured = locked1.Probe + "; exit " + locked1.Exit.ToString(CultureInfo.InvariantCulture) + " " + locked1.Line + (locked1.Error.Length > 0 ? " stderr " + locked1.Error : "");
            // macOS: GetFullPath throws, PathProblem refuses the path. Linux: GetFullPath accepts, the engine reports the path.
            var macOS = OperatingSystem.IsMacOS();
            var expectedProbe = macOS ? "GetFullPath threw System.UnauthorizedAccessException" : "GetFullPath accepted";
            var expected = macOS ? JsonSerializer.Serialize(new { status = "failed", code = "skills_lock_path_invalid", detail = parameter })
                : parameter == "skills" ? LockedSkills39 : LockMissing39;
            Check(locked1.Probe == expectedProbe, label + ": precondition: " + expectedProbe + " (" + (macOS ? "macOS" : "Linux") + ")", measured);
            Check(locked1.Exit == 1 && locked1.Line == expected && locked1.Error.Length == 0, label + ": exactly " + expected + " (exit 1, empty stderr)", measured);
            Check(Environment.CurrentDirectory == original, label + ": the working directory is restored (finally)", Environment.CurrentDirectory + " != " + original);
            Check(File.ReadAllBytes(lockFile).AsSpan().SequenceEqual(lockBytes) && File.ReadAllBytes(packFile).AsSpan().SequenceEqual(manifest)
                && Directory.GetFileSystemEntries(cwd).Length == entriesBefore, label + ": the lock and the pack are unchanged and nothing is written", lockFile.Replace(scratch, "<scratch>", StringComparison.Ordinal));
            Directory.Delete(parent, true);
            Directory.Delete(outside, true);
        }
        Check(Environment.ExitCode == exitBefore, Label("both", "Environment.ExitCode is restored (finally)"),
            Environment.ExitCode.ToString(CultureInfo.InvariantCulture) + ", before " + exitBefore.ToString(CultureInfo.InvariantCulture));
    }

    // ---- P10-41: the remaining MCP PathProblem copies and the lock-path verbs under an unsearchable working directory ----
    // 1. Static pin over all eight MCP PathProblem copies (every host): the one Path.GetFullPath try in each copy is followed
    //    by a catch filter that takes ArgumentException, NotSupportedException, PathTooLongException, FileNotFoundException and
    //    UnauthorizedAccessException (the P10-39 SkillLockTool filter); a planted mutant without UnauthorizedAccessException
    //    must be refused by the same reader.
    // 2. macOS, measured through a real McpServer tools/call in process (tests/mcp does not run on macOS; this exe compiles the
    //    same interfaces/mcp sources): the 17 P10-35 cases (one relative path parameter, every other path absolute) under a
    //    mode-000 working directory and under a mode-000 parent; GetFullPath throws UnauthorizedAccessException there, and the
    //    response is isError with exactly {"status":"failed","code":"<tool>_path_invalid","detail":"<parameter>"}. Linux is
    //    covered by tests/mcp (P10-35 Unsearchable), Windows has no Unix permission bits: counted SKIP.
    // 3. macOS and Linux: jurisdiction-resolve --lock and skills-lock pin/update/unpin with a relative lock under a mode-000
    //    working directory or parent, through the real CLI entry point; the searchable reference succeeds, the locked line is
    //    pinned (macOS skills_lock_path_invalid "lock"; Linux as measured), exit 1, empty stderr, the lock bytes unchanged and
    //    nothing written. Every case runs or is a counted SKIP; on a non-root Linux or macOS runner every case runs.
    static void P10_41(string root, string scratch, string cliDll, Action<bool, string, string> Check, Action<string, string> skip)
    {
        PathProblemFilters41(root, Check);
        McpUnsearchable41(scratch, Check, skip);
        LockVerbsUnsearchable41(root, scratch, cliDll, Check, skip);
        RestoreSelfTest43(scratch, Check, skip);
        WriteFailed43SelfTest(Check);
        RestoreProblemsSelfTest45(Check);
        RestoreWiring47(root, Check);
        Contract47(root, Check);
        GenericCatch43(scratch, cliDll, Check);
    }

    static readonly string[] Tools41 = { "AuditVerifyTool", "DiffIntentTool", "DocRealizedTool", "DocUpdateTool", "GeneratePrototypesTool", "GenerateTypologyTool", "PlanLayoutsTool", "SkillLockTool" };
    static readonly string[] Filter41 = { "ArgumentException", "NotSupportedException", "PathTooLongException", "FileNotFoundException", "UnauthorizedAccessException" };

    // Null when the source's PathProblem GetFullPath catch takes every Filter41 type, else what is wrong. P10-43: the reader
    // does not depend on the layout: the try and the catch filter may be split over lines and indented any way (only
    // whitespace and // comment lines may stand between the try and its catch); the filter must have the shape
    // catch (Exception <v>) when (<v> is A or B or ...), checked by a reformat self-test in PathProblemFilters41.
    static readonly System.Text.RegularExpressions.Regex Try41 = new(@"try\s*\{\s*Path\.GetFullPath\(\s*path\s*\)\s*;\s*\}", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    static readonly System.Text.RegularExpressions.Regex Catch41 = new(
        @"\G(?:\s|//[^\n]*\n)*catch\s*\(\s*Exception\s+(?<v>\w+)\s*\)\s*when\s*\(\s*\k<v>\s+is\s+(?<types>[A-Za-z_][\w.]*(?:\s+or\s+[A-Za-z_][\w.]*)*)\s*\)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // P10-47: the parameter list may hold balanced parentheses (a default containing calls). P10-49: parentheses inside
    // string (raw, verbatim, regular) and char defaults are skipped; stated limit: an interpolated string default
    // ($"...") is read as a regular string, so a quote inside one of its holes ends it early.
    static readonly System.Text.RegularExpressions.Regex Declaration45 = new(
        @"\bPathProblem\s*\((?>(?<q>""{3,})[\s\S]*?\k<q>|@""(?:[^""]|"""")*""|""(?:[^""\\\n]|\\.)*""|'(?:[^'\\\n]|\\.)*'|[^()""']|\((?<d>)|\)(?<-d>))*(?(d)(?!))\)\s*\{",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    static string? FilterProblem41(string source)
    {
        var tries = Try41.Matches(source);
        if (tries.Count != 1) return "not exactly one 'try { Path.GetFullPath(path); }' (any layout): " + tries.Count.ToString(CultureInfo.InvariantCulture);
        var at = tries[0].Index;
        // P10-45: brace scoping, not indentation: the try lies inside the body of the last 'PathProblem(<parameters>) {'
        // declaration before it (BlockEnd45 skips braces in strings, chars and comments).
        var declaration = Declaration45.Matches(source).Where(match => match.Index < at).LastOrDefault();
        var close = declaration is null ? -1 : BlockEnd45(source, declaration.Index + declaration.Length - 1);
        if (close < at) return "the GetFullPath try is not inside PathProblem (brace scoping: " + (declaration is null ? "no 'PathProblem(...) {' declaration before it" : close < 0 ? "its body is not closed" : "its body closes before the try") + ")";
        var filter = Catch41.Match(source, at + tries[0].Length);
        if (!filter.Success) return "no 'catch (Exception e) when (e is A or B ...)' filter directly follows the GetFullPath try (only whitespace and // comment lines may stand between)";
        var types = System.Text.RegularExpressions.Regex.Split(filter.Groups["types"].Value, @"\s+or\s+");
        var missing = Filter41.Where(type => !types.Contains(type, StringComparer.Ordinal)).ToArray();
        return missing.Length == 0 ? null : "the filter (" + string.Join(" or ", types) + ") lacks " + string.Join(", ", missing);
    }

    static void PathProblemFilters41(string root, Action<bool, string, string> Check)
    {
        string? skillLock = null;
        foreach (var tool in Tools41)
        {
            var path = Path.Combine(root, "interfaces", "mcp", tool + ".cs");
            string? source = null;
            try { source = File.ReadAllText(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Check(false, "P10-41 static pin: " + tool + ".cs is readable", error.Message); continue; }
            if (tool == "SkillLockTool") skillLock = source;
            var problem = FilterProblem41(source ?? "");
            Check(problem is null, "P10-41 static pin: " + tool + ".PathProblem's GetFullPath catch takes UnauthorizedAccessException (with ArgumentException, NotSupportedException, PathTooLongException, FileNotFoundException)", problem ?? "");
        }
        Check(Tools41.Length == 8 && Tools41.Distinct().Count() == 8, "P10-41 static pin: fixture: eight PathProblem copies", string.Join(",", Tools41));
        // The reader refuses a planted mutant (the SkillLockTool filter without UnauthorizedAccessException) and a missing try.
        if (skillLock is not null)
        {
            var mutant = skillLock.Replace(" or UnauthorizedAccessException)", ")", StringComparison.Ordinal);
            Check(mutant != skillLock && FilterProblem41(mutant) is { } why && why.Contains("UnauthorizedAccessException", StringComparison.Ordinal),
                "P10-41 static pin: the reader refuses a filter without UnauthorizedAccessException (planted mutant)", FilterProblem41(mutant) ?? "accepted");
            Check(FilterProblem41(skillLock.Replace("try { Path.GetFullPath(path); }", "Path.GetFullPath(path);", StringComparison.Ordinal)) is not null,
                "P10-41 static pin: the reader refuses a PathProblem without the GetFullPath try (planted mutant)", "");
            // P10-43 reformat self-test: the same SkillLockTool source with the try and the filter split over lines (a comment
            // line between them, one type per line, other indentation) is still accepted, and the reformatted filter without
            // UnauthorizedAccessException, or testing another variable, is still refused.
            const string OneLine = "catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or FileNotFoundException or UnauthorizedAccessException)";
            var nl = Environment.NewLine;
            var split = "catch (Exception error)" + nl + "            when (error is ArgumentException" + nl + "                or NotSupportedException" + nl + "                or PathTooLongException"
                + nl + "                or FileNotFoundException" + nl + "                or UnauthorizedAccessException" + nl + "            )";
            var reformatted = skillLock.Replace(OneLine, "// P10-43 reformat self-test" + nl + "        " + split, StringComparison.Ordinal)
                .Replace("try { Path.GetFullPath(path); }", "try" + nl + "        {" + nl + "            Path.GetFullPath(path);" + nl + "        }", StringComparison.Ordinal);
            Check(reformatted.Contains(split, StringComparison.Ordinal) && !reformatted.Contains("try { Path.GetFullPath(path); }", StringComparison.Ordinal) && FilterProblem41(reformatted) is null,
                "P10-43 static pin reformat self-test: the reader accepts the SkillLockTool try and filter split over lines",
                FilterProblem41(reformatted) ?? (reformatted.Contains(split, StringComparison.Ordinal) ? "try not reformatted" : "filter not reformatted"));
            var splitMutant = reformatted.Replace(nl + "                or UnauthorizedAccessException", "", StringComparison.Ordinal);
            Check(splitMutant != reformatted && FilterProblem41(splitMutant) is { } splitWhy && splitWhy.Contains("lacks UnauthorizedAccessException", StringComparison.Ordinal),
                "P10-43 static pin reformat self-test: the reader refuses the split filter without UnauthorizedAccessException", FilterProblem41(splitMutant) ?? "accepted");
            Check(FilterProblem41(skillLock.Replace(OneLine, "catch (Exception error) when (other is ArgumentException)", StringComparison.Ordinal)) is not null,
                "P10-43 static pin reformat self-test: the reader refuses a filter that tests another variable", "");
            ScopingSelfTest45(skillLock, Check);
            ScopingSelfTest47(skillLock, Check);
            ScopingSelfTest49(skillLock, Check);
            ScopingSelfTest51(Check);
        }
    }

    // P10-49 self-tests: an interpolation hole's format clause ({x:...}, after a ':' outside parentheses, brackets and
    // braces) is text to the hole's closing brace, so '//', '/*' and a lone quote there are not code; a ':' inside
    // parentheses (a conditional) stays code. A $$ raw hole closed by fewer braces than dollars is reported unclosed.
    // The declaration reader skips parentheses inside string (regular, verbatim, raw) and char parameter defaults.
    static void ScopingSelfTest49(string skillLock, Action<bool, string, string> Check)
    {
        const string Declaration = "public static string? PathProblem(string? path, string name)";
        string Label(string what) => "P10-49 static pin scanner self-test: " + what;
        var q3 = "\"\"\"";
        var blocks = new (string Name, string Text)[]
        {
            ("format clause holding //", "{ a = $\"{x://}\"; }"),
            ("format clause holding a quote char", "{ a = $\"{x:'}\"; }"),
            ("verbatim format clause holding /*", "{ a = $@\"{x:/*}\"; }"),
            ("alignment and format clause", "{ a = $\"{x,-5:'}\"; }"),
            ("$$ raw format clause holding a quote char", "{ a = $$" + q3 + "{{x:'}}" + q3 + "; }"),
            ("parenthesised conditional in a hole", "{ a = $\"{(b ? '}' : c)}\"; }"),
            ("conditional inside a call in a hole", "{ a = $\"{F(b ? \"}\" : c)}\"; }"),
        };
        var wrong = blocks.Where(block => BlockEnd45(block.Text, 0) != block.Text.Length - 1)
            .Select(block => block.Name + " (" + BlockEnd45(block.Text, 0).ToString(CultureInfo.InvariantCulture) + " of " + (block.Text.Length - 1).ToString(CultureInfo.InvariantCulture) + ")").ToArray();
        Check(wrong.Length == 0, Label("BlockEnd45 skips an interpolation hole's format clause as text"), string.Join(" | ", wrong));
        Check(BlockEnd45("{ a = $$" + q3 + "{{x} }" + q3 + "; }", 0) == -1 && BlockEnd45("{ a = $$" + q3 + "{{x:F} }" + q3 + "; }", 0) == -1,
            Label("BlockEnd45 reports a $$ raw hole closed by one brace as unclosed"), "");
        var declaration = "PathProblem(string a = \")\", char b = '(', string c = @\"(\"\"\", string d = \"\\\")\", string e = " + q3 + " ) \" " + q3 + ") {";
        Check(Declaration45.IsMatch(declaration) && !Declaration45.IsMatch("PathProblem(string a = \"(\" {"),
            Label("Declaration45 skips parentheses inside string and char parameter defaults"), declaration);
        var defaults = skillLock.Replace(Declaration, "public static string? PathProblem(string? path, string name = \")\")", StringComparison.Ordinal);
        Check(defaults != skillLock && FilterProblem41(defaults) is null,
            Label("the reader accepts a PathProblem declaration whose string default holds a parenthesis"), FilterProblem41(defaults) ?? (defaults == skillLock ? "not planted" : ""));
    }

    // P10-51 self-tests: brace runs at a raw interpolated literal's holes follow C#: of an opening run of k >= n braces
    // (n dollars) the last n open the hole and the first k - n are text, so k must be below 2n; of the closing run the
    // first n close the hole and the rest are text, so it must also be below 2n. A longer run is reported unclosed.
    static void ScopingSelfTest51(Action<bool, string, string> Check)
    {
        string Label(string what) => "P10-51 static pin scanner self-test: " + what;
        var q3 = "\"\"\"";
        var accepted = new (string Name, string Text)[]
        {
            ("$$ hole with one text brace on each side", "{ a = $$" + q3 + "{{{x}}}" + q3 + "; }"),
            ("$$ point with text braces around two holes", "{ a = $$" + q3 + "The point {{{X}}, {{Y}}} is" + q3 + "; }"),
            ("$$$ hole with two text braces after it", "{ a = $$$" + q3 + "{{{x}}}}}" + q3 + "; }"),
        };
        var wrong = accepted.Where(block => BlockEnd45(block.Text, 0) != block.Text.Length - 1)
            .Select(block => block.Name + " (" + BlockEnd45(block.Text, 0).ToString(CultureInfo.InvariantCulture) + " of " + (block.Text.Length - 1).ToString(CultureInfo.InvariantCulture) + ")").ToArray();
        Check(wrong.Length == 0, Label("BlockEnd45 accepts text braces beside a raw hole up to one fewer than the dollars"), string.Join(" | ", wrong));
        var refused = new (string Name, string Text)[]
        {
            ("$$ hole closed by four braces", "{ a = $$" + q3 + "{{x}}}}" + q3 + "; }"),
            ("$$ hole opened by four braces", "{ a = $$" + q3 + "{{{{x}}" + q3 + "; }"),
            ("$ raw hole closed by two braces", "{ a = $" + q3 + "{x}}" + q3 + "; }"),
            ("$$ format clause hole closed by four braces", "{ a = $$" + q3 + "{{x:F}}}}" + q3 + "; }"),
        };
        var taken = refused.Where(block => BlockEnd45(block.Text, 0) != -1).Select(block => block.Name).ToArray();
        Check(taken.Length == 0, Label("BlockEnd45 reports a raw hole whose opening or closing brace run reaches twice the dollars as unclosed"), string.Join(" | ", taken));
    }

    // P10-45: the "inside PathProblem" check is brace scoping, not indentation. Mutants of the SkillLockTool source:
    // a nested block inside PathProblem whose closing brace sits at four spaces (accepted), braces inside string, char and
    // comment text before the try (accepted), and a PathProblem whose body closes before the try, the try sitting in the
    // next method (refused, whatever the indentation). Plus the scanner itself on a small text.
    static void ScopingSelfTest45(string skillLock, Action<bool, string, string> Check)
    {
        const string Try = "try { Path.GetFullPath(path); }";
        const string Declaration = "public static string? PathProblem(string? path, string name)";
        string Label(string what) => "P10-45 static pin brace scoping self-test: " + what;
        var nested = skillLock.Replace(Try, "if (path.Length > 0)\n    {\n    }\n        " + Try, StringComparison.Ordinal);
        Check(nested != skillLock && FilterProblem41(nested) is null,
            Label("the reader accepts a nested block inside PathProblem closing at four spaces before the try"), FilterProblem41(nested) ?? (nested == skillLock ? "not planted" : ""));
        var texts = skillLock.Replace(Try, "_ = \"}\" + '}' + @\"}\"\"}\" + $\"{{}}\"; /* } */\n        // }\n        " + Try, StringComparison.Ordinal);
        Check(texts != skillLock && FilterProblem41(texts) is null,
            Label("the reader accepts braces in string, char and comment text inside PathProblem before the try"), FilterProblem41(texts) ?? (texts == skillLock ? "not planted" : ""));
        var outside = skillLock.Replace(Declaration, Declaration + " { return null; }\n\tstatic string? Other45(string? path, string name)", StringComparison.Ordinal);
        Check(outside != skillLock && FilterProblem41(outside) is { } why && why.Contains("not inside PathProblem", StringComparison.Ordinal),
            Label("the reader refuses a try in the method after PathProblem's closed body (tab indentation)"), FilterProblem41(outside) ?? (outside == skillLock ? "not planted" : "accepted"));
        const string Block = "{ a = \"}\"; b = '{'; c = @\"x\"\"}\"; /* } */ // }\n { } }";
        Check(BlockEnd45(Block, 0) == Block.Length - 1 && BlockEnd45("{ { }", 0) == -1 && BlockEnd45("x { }", 2) == 4,
            Label("BlockEnd45 skips braces in strings, chars and comments and reports an unclosed block"),
            BlockEnd45(Block, 0).ToString(CultureInfo.InvariantCulture) + " of " + (Block.Length - 1).ToString(CultureInfo.InvariantCulture));
    }

    // P10-45: index of the brace closing the block opened at source[open] == '{', or -1 when it is not closed. Braces in
    // // and /* */ comments, string literals (regular, $, @ and $@/@$ verbatim, "" escapes) and char literals are skipped.
    // P10-47: also C# 11 raw literals (three or more quotes, closed by the same count; $-prefixed raw literals whose holes
    // open with as many braces as dollars) and everything inside interpolation holes, which are scanned as code to their
    // closing brace (so strings, chars, comments and nested interpolated strings there are skipped too). P10-49: a hole's
    // format clause ({x:...}) is text to the hole's closing brace, and a $$ raw hole must close with as many braces as dollars.
    static int BlockEnd45(string source, int open) => open >= 0 && open < source.Length && source[open] == '{' ? CodeEnd47(source, open + 1) : -1;

    // P10-47: index of the first '}' at depth 0 from start (comments and literals skipped), or -1. P10-49: in an
    // interpolation hole a ':' outside parentheses, brackets and braces opens the format clause, which is text to the first
    // '}' (C# reads a top-level ':' in a hole as the format clause; a conditional there must be parenthesised).
    static int CodeEnd47(string source, int start, bool hole = false)
    {
        var depth = 0;
        var parens = 0;
        for (var i = start; i < source.Length; i++)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                var end = source.IndexOf('\n', i);
                if (end < 0) return -1;
                i = end;
            }
            else if (c == '/' && next == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return -1;
                i = end + 1;
            }
            else if (c == '\'')
            {
                for (i++; i < source.Length && source[i] != '\''; i++) if (source[i] == '\\') i++;
                if (i >= source.Length) return -1;
            }
            else if (c == '"' || c == '$' || c == '@')
            {
                var quote = i;
                while (quote < source.Length && (source[quote] == '$' || source[quote] == '@')) quote++;
                if (quote >= source.Length || source[quote] != '"') { i = quote - 1; continue; }
                var prefix = source[i..quote];
                var end = LiteralEnd47(source, quote, prefix.Count(ch => ch == '$'), prefix.Contains('@', StringComparison.Ordinal));
                if (end < 0) return -1;
                i = end;
            }
            else if (c == '(' || c == '[') parens++;
            else if ((c == ')' || c == ']') && parens > 0) parens--;
            else if (hole && c == ':' && depth == 0 && parens == 0) return source.IndexOf('}', i + 1);
            else if (c == '{') depth++;
            else if (c == '}' && depth-- == 0) return i;
        }
        return -1;
    }

    // P10-47: index of the last character of the string literal whose first quote is source[quote], or -1 when unclosed.
    static int LiteralEnd47(string source, int quote, int dollars, bool verbatim)
    {
        int Run(int at, char ch) { var n = 0; while (at + n < source.Length && source[at + n] == ch) n++; return n; }
        var quotes = Run(quote, '"');
        if (quotes >= 3 && !verbatim)
        {
            for (var i = quote + quotes; i < source.Length; i++)
            {
                if (source[i] == '"')
                {
                    var run = Run(i, '"');
                    if (run >= quotes) return i + quotes - 1;
                    i += run - 1;
                }
                else if (dollars > 0 && source[i] == '{')
                {
                    var run = Run(i, '{');
                    if (run < dollars) { i += run - 1; continue; }
                    var close = CodeEnd47(source, i + run, hole: true);
                    if (close < 0 || Run(close, '}') < dollars) return -1;
                    i = close + dollars - 1;
                }
            }
            return -1;
        }
        for (var i = quote + 1; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '\\' && !verbatim) { i++; continue; }
            if (c == '"')
            {
                if (verbatim && i + 1 < source.Length && source[i + 1] == '"') { i++; continue; }
                return i;
            }
            if (dollars > 0 && c == '{')
            {
                if (i + 1 < source.Length && source[i + 1] == '{') { i++; continue; }
                var close = CodeEnd47(source, i + 1, hole: true);
                if (close < 0) return -1;
                i = close;
            }
        }
        return -1;
    }

    // P10-47 self-tests of the scanner and the declaration reader. BlockEnd45 on small texts (each a block whose last
    // character is its closing brace): C# 11 raw literals (three and four quotes, multi-line, a lone brace inside) and
    // $/$$ raw interpolated literals, and strings, chars and comments inside the holes of $, $@ and raw interpolated
    // strings. Then the SkillLockTool source with a raw literal and an interpolation hole holding "}" inside PathProblem
    // before the try (accepted), and with a parameter default containing calls in the PathProblem declaration (accepted).
    static void ScopingSelfTest47(string skillLock, Action<bool, string, string> Check)
    {
        const string Try = "try { Path.GetFullPath(path); }";
        const string Declaration = "public static string? PathProblem(string? path, string name)";
        string Label(string what) => "P10-47 static pin scanner self-test: " + what;
        var q3 = "\"\"\"";
        var blocks = new (string Name, string Text)[]
        {
            ("raw literal with a closing brace", "{ a = " + q3 + " } \" \"\" " + q3 + "; }"),
            ("raw literal with an opening brace", "{ a = " + q3 + " { " + q3 + "; }"),
            ("four-quote raw literal holding three quotes", "{ a = \"" + q3 + " } " + q3 + " }\"" + q3 + "; }"),
            ("multi-line raw literal", "{\n    a = " + q3 + "\n        } {\n        " + q3 + ";\n}"),
            ("$ raw literal with a hole", "{ a = $" + q3 + "x {F(\"}\")} y" + q3 + "; }"),
            ("$$ raw literal with single braces as text and a hole", "{ a = $$" + q3 + "{ {{F(\"}\")}} }" + q3 + "; }"),
            ("string inside a $ hole", "{ a = $\"x{F(\"}\")}y\"; }"),
            ("char and comment inside a $ hole", "{ a = $\"{'}'}{F(/* } */ 1)}\"; }"),
            ("verbatim string inside a $@ hole", "{ a = $@\"x{F(@\"}\"\"\")}\"\"\"; }"),
            ("nested interpolated string inside a hole", "{ a = $\"{$\"{\"}\"}\"}\"; }"),
            ("escaped braces beside a hole", "{ a = $\"{{ {x} }}\"; }"),
        };
        var wrong = blocks.Where(block => BlockEnd45(block.Text, 0) != block.Text.Length - 1)
            .Select(block => block.Name + " (" + BlockEnd45(block.Text, 0).ToString(CultureInfo.InvariantCulture) + " of " + (block.Text.Length - 1).ToString(CultureInfo.InvariantCulture) + ")").ToArray();
        Check(wrong.Length == 0, Label("BlockEnd45 skips braces in raw literals and in strings, chars and comments inside interpolation holes"), string.Join(" | ", wrong));
        Check(BlockEnd45("{ a = " + q3 + " } ", 0) == -1 && BlockEnd45("{ a = $\"{F(\"}\"", 0) == -1,
            Label("BlockEnd45 reports an unclosed raw literal and an unclosed hole as unclosed"), "");
        var raw = skillLock.Replace(Try, "_ = " + q3 + " } " + q3 + " + $\"{\"}\"}\";\n        " + Try, StringComparison.Ordinal);
        Check(raw != skillLock && FilterProblem41(raw) is null,
            Label("the reader accepts a raw literal and an interpolation hole holding \"}\" inside PathProblem before the try"), FilterProblem41(raw) ?? (raw == skillLock ? "not planted" : ""));
        var defaults = skillLock.Replace(Declaration, "public static string? PathProblem(string? path, string name = nameof(System.String))", StringComparison.Ordinal);
        Check(defaults != skillLock && FilterProblem41(defaults) is null,
            Label("the reader accepts a PathProblem declaration whose parameter default contains a call"), FilterProblem41(defaults) ?? (defaults == skillLock ? "not planted" : ""));
        Check(Declaration45.IsMatch("PathProblem(string a = F(G(1), H()), int b = 2) {") && !Declaration45.IsMatch("PathProblem(string a = F(1 {"),
            Label("Declaration45 takes nested balanced parentheses in the parameter list and refuses an unbalanced one"), "");
    }

    // P10-47: the lock-verb restore aggregation wiring, read from this file. Inside LockVerbsUnsearchable41's body (found by
    // Declaration-style balanced parentheses and BlockEnd45) the one RestoreProblems45 call must pass both calls' restore
    // problems, ("reference", reference.Restore) and ("locked", result.Restore), into restoreProblem, which the restore check
    // requires empty, with reference = Call(false) and result = Call(true). Whitespace free. Null when wired, else why.
    static readonly System.Text.RegularExpressions.Regex LockVerbs47 = new(@"\bstatic\s+void\s+LockVerbsUnsearchable41\s*\((?>[^()]|\((?<d>)|\)(?<-d>))*(?(d)(?!))\)\s*\{", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    static string? RestoreWiringProblem47(string source)
    {
        var declarations = LockVerbs47.Matches(source);
        if (declarations.Count != 1) return "not exactly one LockVerbsUnsearchable41 declaration: " + declarations.Count.ToString(CultureInfo.InvariantCulture);
        var open = declarations[0].Index + declarations[0].Length - 1;
        var close = BlockEnd45(source, open);
        if (close < 0) return "LockVerbsUnsearchable41's body is not closed";
        var body = System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(source[open..(close + 1)], @"\s+", " "), @" ?([(),;=]) ?", "$1");
        var calls = System.Text.RegularExpressions.Regex.Matches(body, @"\bRestoreProblems45\(").Count;
        if (calls != 1) return "not exactly one RestoreProblems45 call in LockVerbsUnsearchable41: " + calls.ToString(CultureInfo.InvariantCulture);
        foreach (var wired in new[] { "var restoreProblem=RestoreProblems45((\"reference\",reference.Restore),(\"locked\",result.Restore));", "restoreProblem.Length==0", "var reference=Call(false);", "var result=Call(true);" })
            if (!body.Contains(wired, StringComparison.Ordinal)) return "LockVerbsUnsearchable41 lacks " + wired + " (whitespace free)";
        return null;
    }

    static void RestoreWiring47(string root, Action<bool, string, string> Check)
    {
        string Label(string what) => "P10-47 lock verbs restore wiring: " + what;
        string source;
        try { source = File.ReadAllText(Path.Combine(root, "tests", "cli", "DeletedCwdPathTests.cs")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Check(false, Label("tests/cli/DeletedCwdPathTests.cs is readable"), error.Message); return; }
        Check(RestoreWiringProblem47(source) is null,
            Label("LockVerbsUnsearchable41 aggregates the reference and the locked call's restore problems into the restore check (pinned by the variable names reference, result and restoreProblem: a rename fails this pin)"),
            RestoreWiringProblem47(source) ?? "");
        const string Wired = "RestoreProblems45((\"reference\", reference.Restore), (\"locked\", result.Restore))";
        var mutants = new (string Name, string Text)[]
        {
            ("only the locked call's problems", "RestoreProblems45((\"locked\", result.Restore))"),
            ("the locked call's problems under both names", "RestoreProblems45((\"reference\", result.Restore), (\"locked\", result.Restore))"),
            ("only the reference call's problems", "RestoreProblems45((\"reference\", reference.Restore))"),
        };
        foreach (var (name, text) in mutants)
        {
            var mutant = source.Replace(Wired, text, StringComparison.Ordinal);
            Check(mutant != source && RestoreWiringProblem47(mutant) is not null, Label("a mutant passing " + name + " to RestoreProblems45 is refused"), mutant == source ? "not planted" : RestoreWiringProblem47(mutant) ?? "accepted");
        }
        var unused = source.Replace("restoreProblem.Length == 0", "true", StringComparison.Ordinal);
        Check(unused != source && RestoreWiringProblem47(unused) is not null, Label("a mutant whose restore check ignores the aggregate is refused"), unused == source ? "not planted" : RestoreWiringProblem47(unused) ?? "accepted");
        // P10-51: the declaration reader skips parentheses inside string and char parameter defaults (the name is split so
        // this file's text holds no second declaration).
        var name = "LockVerbs" + "Unsearchable41(";
        var literals = "static void " + name + "string a = \")\", char b = '(', string c = @\"(\"\"\", string d = \"\"\" ) \"\"\") {";
        Check(LockVerbs47.IsMatch(literals) && !LockVerbs47.IsMatch("static void " + name + "string a = \"(\" {"),
            "P10-51 lock verbs restore wiring: LockVerbs47 skips parentheses inside string and char parameter defaults", literals);
        var defaults = source.Replace("static void " + name + "string root,", "static void " + name + "string note = \")\", char open = '(', string root,", StringComparison.Ordinal);
        Check(defaults != source && RestoreWiringProblem47(defaults) is null,
            "P10-51 lock verbs restore wiring: the pin accepts a LockVerbsUnsearchable41 declaration whose string and char defaults hold parentheses",
            defaults == source ? "not planted" : RestoreWiringProblem47(defaults) ?? "");
    }

    // P10-47: the contract wording (docs/contracts/module-contracts.md, the P10-35.. section): the P10-45 paragraph cites its
    // green run and the P10-47 paragraph states the scanner, declaration and wiring results (as the P3-34 paragraph check).
    static void Contract47(string root, Action<bool, string, string> Check)
    {
        string contracts;
        try { contracts = File.ReadAllText(Path.Combine(root, "docs", "contracts", "module-contracts.md")).Replace("\r\n", "\n", StringComparison.Ordinal); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Check(false, "P10-47 contract: docs/contracts/module-contracts.md is readable", error.Message); return; }
        var heading = contracts.IndexOf("## MCP and CLI paths under an unsearchable working directory: remainder and the macOS gap (P10-35", StringComparison.Ordinal);
        var next = heading < 0 ? -1 : contracts.IndexOf("\n## ", heading + 1, StringComparison.Ordinal);
        var section = heading < 0 ? "" : next < 0 ? contracts[heading..] : contracts[heading..next];
        string Paragraph(string start)
        {
            var at = section.IndexOf("\n" + start, StringComparison.Ordinal);
            if (at < 0) return "";
            var end = section.IndexOf('\n', at + 1);
            return end < 0 ? section[(at + 1)..] : section[(at + 1)..end];
        }
        var p45 = Paragraph("P10-45 (test tidy");
        Check(p45.Contains("red 45521954740", StringComparison.Ordinal) && p45.Contains("green 45524195217", StringComparison.Ordinal) && p45.Contains("STAYS RECORDED AS CODE READING ONLY", StringComparison.Ordinal),
            "P10-47 contract: the P10-45 paragraph cites red 45521954740 and green 45524195217 and keeps the file-tool reach record", section.Length == 0 ? "section not found" : p45.Length == 0 ? "P10-45 paragraph not found" : p45);
        var p47 = Paragraph("P10-47 (test tidy");
        Check(p47.Contains("C# 11 raw", StringComparison.Ordinal) && p47.Contains("interpolation holes", StringComparison.Ordinal) && p47.Contains("parameter default", StringComparison.Ordinal)
            && p47.Contains("only the locked call", StringComparison.Ordinal) && p47.Contains("green 45524195217", StringComparison.Ordinal),
            "P10-47 contract: the P10-47 paragraph states raw literals, interpolation holes, parameter defaults with calls and the restore wiring pin", section.Length == 0 ? "section not found" : p47.Length == 0 ? "P10-47 paragraph not found" : p47);
        // P10-49: the P10-45 paragraph points to the P10-47 raw-literal support, the P10-47 paragraph cites its green run, and
        // the P10-49 paragraph states the format clause, string defaults and the $$ closing run.
        Check(p45.Contains("P10-47 adds raw-literal support", StringComparison.Ordinal),
            "P10-49 contract: the P10-45 paragraph cross-references the P10-47 raw-literal support", section.Length == 0 ? "section not found" : p45.Length == 0 ? "P10-45 paragraph not found" : p45);
        Check(p47.Contains("green 45585771329", StringComparison.Ordinal),
            "P10-49 contract: the P10-47 paragraph cites green 45585771329", section.Length == 0 ? "section not found" : p47.Length == 0 ? "P10-47 paragraph not found" : p47);
        var p49 = Paragraph("P10-49 (test tidy");
        Check(p49.Contains("format clause", StringComparison.Ordinal) && p49.Contains("string or char default", StringComparison.Ordinal) && p49.Contains("closing brace run", StringComparison.Ordinal)
            && p49.Contains("variable names", StringComparison.Ordinal) && p49.Contains("green 45585771329", StringComparison.Ordinal),
            "P10-49 contract: the P10-49 paragraph states format clauses, string or char defaults, the $$ closing brace run and the wiring pin's variable names",
            section.Length == 0 ? "section not found" : p49.Length == 0 ? "P10-49 paragraph not found" : p49);
        Check(section.Contains("(P10-35, P10-37, P10-39, P10-41, P10-43, P10-45, P10-47, P10-49, P10-51)", StringComparison.Ordinal), "P10-51 contract: the section heading lists P10-47, P10-49 and P10-51", section.Length == 0 ? "section not found" : section[..section.IndexOf('\n')]);
        // P10-51: the P10-49 paragraph cites its green run and the P10-51 paragraph states the LockVerbs47 literals, the raw
        // hole brace runs and the hole-only parenthesis count.
        Check(p49.Contains("green 45665616001", StringComparison.Ordinal),
            "P10-51 contract: the P10-49 paragraph cites green 45665616001", section.Length == 0 ? "section not found" : p49.Length == 0 ? "P10-49 paragraph not found" : p49);
        var p51 = Paragraph("P10-51 (test tidy");
        Check(p51.Contains("string and char literals", StringComparison.Ordinal) && p51.Contains("brace run", StringComparison.Ordinal) && p51.Contains("twice the dollars", StringComparison.Ordinal)
            && p51.Contains("only inside holes", StringComparison.Ordinal) && p51.Contains("green 45665616001", StringComparison.Ordinal),
            "P10-51 contract: the P10-51 paragraph states the lock-verb declaration literals, the raw hole brace runs and the hole-only parenthesis count",
            section.Length == 0 ? "section not found" : p51.Length == 0 ? "P10-51 paragraph not found" : p51);
    }

    static void McpUnsearchable41(string scratch, Action<bool, string, string> Check, Action<string, string> skip)
    {
        const string RelativeName = "p10-41.json";
        var absent = Path.Combine(scratch, "p10-41-absent.json");
        var cases = new List<(string Tool, string Parameter, string Expected, System.Text.Json.Nodes.JsonObject Arguments)>();
        void Add(string tool, string code, (string Parameter, string Detail)[] paths, System.Text.Json.Nodes.JsonObject? extra = null)
        {
            foreach (var (parameter, detail) in paths)
            {
                var arguments = new System.Text.Json.Nodes.JsonObject();
                foreach (var (name, _) in paths) arguments[name] = name == parameter ? RelativeName : absent;
                if (extra is not null) foreach (var (key, value) in extra) arguments[key] = value?.DeepClone();
                cases.Add((tool, parameter, JsonSerializer.Serialize(new { status = "failed", code, detail }), arguments));
            }
        }
        Add("audit_verify", "audit_verify_path_invalid", new[] { ("log_path", "log"), ("anchor_path", "anchor") });
        Add("diff_intent", "diff_intent_path_invalid", new[] { ("project_path", "project"), ("intent_path", "intent") });
        Add("doc_realized", "doc_realized_path_invalid", new[] { ("record", "record"), ("fixture", "fixture"), ("assignments", "assignments") },
            new System.Text.Json.Nodes.JsonObject { ["document"] = "00000000-0000-0000-0000-000000000000-00000001" });
        Add("doc_update", "doc_update_path_invalid", new[] { ("before", "before"), ("after", "after"), ("realized", "realized"), ("bindings", "bindings"), ("dependents", "dependents") });
        Add("generate_prototypes", "generate_prototypes_path_invalid", new[] { ("request", "request") });
        Add("generate_typology", "generate_typology_path_invalid", new[] { ("request_path", "request") });
        Add("plan_layouts", "plan_layouts_path_invalid", new[] { ("brief", "brief") });
        Add("skills_lock_verify", "skills_lock_path_invalid", new[] { ("skills_path", "skills"), ("lock_path", "lock") });
        Check(cases.Count == 17 && cases.Select(c => c.Tool).Distinct().Count() == 8, "P10-41 MCP unsearchable (macOS): fixture: 17 cases over eight tools", cases.Count.ToString(CultureInfo.InvariantCulture));
        string Label(string mode, string what) => "P10-41 MCP unsearchable " + mode + " (macOS): " + what;
        var modes = new[] { "cwd", "parent" };
        var why = !OperatingSystem.IsMacOS() ? (OperatingSystem.IsLinux() ? "macOS only here: Linux runs these cases in tests/mcp (P10-35 Unsearchable)" : "macOS only: Windows has no Unix permission bits")
            : PrivilegeSkip(scratch, "p10-41-mcp-probe");
        var ran = 0;
        if (why is not null)
        {
            foreach (var mode in modes) foreach (var c in cases) skip(Prefix + Label(mode, c.Tool + " " + c.Parameter), why);
        }
        else
        {
            var server = new Bim.Mcp.McpServer();
            var original = Environment.CurrentDirectory;
            foreach (var mode in modes)
            {
                var parent = Path.Combine(scratch, "p10-41-mcp-" + mode);
                var cwd = Path.Combine(parent, "cwd");
                var locked = mode == "cwd" ? cwd : parent;
                // P10-43: the mode's directories are created inside the try, and the finally restores the owner mode of both,
                // the working directory and removes them even when a CreateDirectory, chmod or case throws.
                var cleanup = "";
                try
                {
                    Directory.CreateDirectory(cwd);
                    var measured = new List<(string Label, bool Ok, string Detail)>();
                    foreach (var (tool, parameter, expected, arguments) in cases)
                    {
                        var request = new System.Text.Json.Nodes.JsonObject { ["jsonrpc"] = "2.0", ["id"] = 41, ["method"] = "tools/call", ["params"] = new System.Text.Json.Nodes.JsonObject { ["name"] = tool, ["arguments"] = arguments.DeepClone() } }.ToJsonString();
                        var capturedError = new StringWriter();
                        var probe = "";
                        string? response = null;
                        var (thrown, restore) = Locked43(cwd, locked, original, Chmod, () =>
                        {
                            try { Path.GetFullPath(RelativeName); probe = "GetFullPath accepted"; }
                            catch (Exception failure) { probe = "GetFullPath threw " + failure.GetType().FullName; }
                            Console.SetError(capturedError);
                            response = server.Handle(request);
                        });
                        if (thrown.Length > 0) thrown = "threw " + thrown;
                        if (restore.Length > 0) thrown += " restore " + restore;
                        string text = "", isError = "";
                        try
                        {
                            var node = response is null ? null : System.Text.Json.Nodes.JsonNode.Parse(response);
                            text = node?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
                            isError = node?["result"]?["isError"]?.ToJsonString() ?? "";
                            if (text.Length == 0) text = response ?? "";
                        }
                        catch (Exception failure) { thrown += " response " + failure.Message; }
                        text = text.Replace(scratch, "<scratch>", StringComparison.Ordinal);
                        var stderr = capturedError.ToString().Trim();
                        var ok = probe == "GetFullPath threw System.UnauthorizedAccessException" && isError == "true" && text == expected && stderr.Length == 0 && thrown.Length == 0;
                        ran++;
                        measured.Add((tool + " " + parameter, ok, probe + "; isError " + isError + " " + text + (stderr.Length > 0 ? " stderr " + stderr : "") + (thrown.Length > 0 ? " " + thrown : "")));
                    }
                    // Every case runs before the first check, so one failing check names every observed outcome.
                    var failing = string.Join(" || ", measured.Where(m => !m.Ok).Select(m => m.Label + " => " + m.Detail));
                    foreach (var (label, ok, detail) in measured)
                        Check(ok, Label(mode, label + ": GetFullPath throws UnauthorizedAccessException, isError, exactly the tool's <tool>_path_invalid line, nothing on stderr"),
                            ok ? "" : detail + " | failing cases: " + failing);
                    Check(Environment.CurrentDirectory == original, Label(mode, "the working directory is restored (finally)"), Environment.CurrentDirectory + " != " + original);
                }
                catch (Exception failure) { Check(false, Label(mode, "the cases run without an exception"), failure.GetType().FullName + ": " + failure.Message); }
                finally { cleanup = Cleanup43(parent, original); }
                Check(cleanup.Length == 0 && !Directory.Exists(parent), Label(mode, "the owner mode, the working directory and the case directory are restored (finally)"), cleanup);
            }
        }
        if (why is null) Check(ran == 34, "P10-41 MCP unsearchable (macOS): every case of both modes ran on a non-root macOS runner", "ran " + ran.ToString(CultureInfo.InvariantCulture) + " of 34");
        if (OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            Check(why is null, "P10-41 MCP unsearchable (macOS): the GitHub Actions macOS runner is non-root, so the cases run", why ?? "");
    }

    // ---- P10-43: restore helpers, their fault-injection self-test and the McpServer generic-catch sweep ----
    const UnixFileMode Owner43 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    // Runs body with the working directory set to cwd and locked made mode 000 through lockWith (Chmod in the cases; the
    // self-test plants one that throws). Whatever throws (SetCurrentDirectory, the lock or the body), every restore step
    // runs on its own in the finally: Console.Error, the owner mode of locked (the real Chmod) and the original working
    // directory. Returns what threw ("" when nothing did) and which restore step threw ("" when every step restored).
    static (string Thrown, string Restore) Locked43(string cwd, string locked, string original, Action<string, UnixFileMode> lockWith, Action body)
    {
        var previousError = Console.Error;
        var thrown = "";
        var restore = "";
        try
        {
            Directory.SetCurrentDirectory(cwd);
            lockWith(locked, UnixFileMode.None);
            body();
        }
        catch (Exception failure) { thrown = failure.GetType().FullName + ": " + failure.Message; }
        finally
        {
            Console.SetError(previousError);
            restore = Restore43(locked, original);
        }
        return (thrown, restore);
    }

    // Each step in its own try: the owner mode of every directory given (in order, so a parent before its child) and the
    // working directory. Returns the steps that threw, "" when all restored.
    static string Restore43(string locked, string original, params string[] more)
    {
        var problems = new List<string>();
        foreach (var directory in more.Prepend(locked))
        {
            // A later directory is skipped when absent (its parent is restored first, so the test is meaningful).
            if (directory != locked && !Directory.Exists(directory)) continue;
            try { Chmod(directory, Owner43); }
            catch (Exception failure) { problems.Add("chmod " + failure.GetType().Name + ": " + failure.Message); }
        }
        try { Directory.SetCurrentDirectory(original); }
        catch (Exception failure) { problems.Add("cwd " + failure.GetType().Name + ": " + failure.Message); }
        return string.Join("; ", problems);
    }

    // A mode's finally: restores the owner mode of parent then parent/cwd (each when present), the working directory, and
    // removes parent. Returns what failed, "" when everything was restored and removed.
    static string Cleanup43(string parent, string original)
    {
        if (!Directory.Exists(parent))
        {
            try { Directory.SetCurrentDirectory(original); return ""; }
            catch (Exception failure) { return "cwd " + failure.GetType().Name + ": " + failure.Message; }
        }
        var problems = Restore43(parent, original, Path.Combine(parent, "cwd"));
        try { Directory.Delete(parent, true); }
        catch (Exception failure) { problems += (problems.Length > 0 ? "; " : "") + "delete " + failure.GetType().Name + ": " + failure.Message; }
        return problems;
    }

    // Fault injection on Linux and macOS non-root runners (Windows: no Unix permission bits; root: mode 000 does not deny):
    // 1. a planted lock that makes the directory mode 000 and then throws: the body does not run, the throw is reported,
    //    the owner mode and the working directory are restored; 2. a CreateDirectory that throws inside the locked body
    //    (a child of the mode-000 parent): the same restore; 3. Cleanup43 after both directories were left mode 000 restores
    //    them and removes the case directory.
    static void RestoreSelfTest43(string scratch, Action<bool, string, string> Check, Action<string, string> skip)
    {
        string Label(string what) => "P10-43 restore self-test: " + what;
        var why = OperatingSystem.IsWindows() ? "Unix permission bits (chmod 000) only" : PrivilegeSkip(scratch, "p10-43-restore-probe");
        if (why is not null)
        {
            foreach (var what in new[] { "planted chmod throw", "CreateDirectory throw", "cleanup of mode-000 directories" }) skip(Prefix + Label(what), why);
            return;
        }
        var original = Environment.CurrentDirectory;
        var errorBefore = Console.Error;
        var parent = Path.Combine(scratch, "p10-43-restore");
        var cwd = Path.Combine(parent, "cwd");
        Directory.CreateDirectory(cwd);
        string Mode(string path)
        {
            if (OperatingSystem.IsWindows()) return "windows";
            try { return File.GetUnixFileMode(path).ToString(); }
            catch (Exception failure) { return failure.GetType().Name; }
        }
        var owner = Owner43.ToString();
        try
        {
            var bodyRan = false;
            var planted = Locked43(cwd, cwd, original, (path, mode) => { Chmod(path, mode); throw new IOException("planted P10-43 chmod"); }, () => bodyRan = true);
            Check(planted.Thrown == "System.IO.IOException: planted P10-43 chmod" && !bodyRan && planted.Restore.Length == 0 && Mode(cwd) == owner
                && Environment.CurrentDirectory == original && ReferenceEquals(Console.Error, errorBefore),
                Label("a chmod that throws after making the directory mode 000: the owner mode, the working directory and Console.Error are restored"),
                planted.Thrown + "; body ran " + bodyRan + "; restore '" + planted.Restore + "'; mode " + Mode(cwd) + "; cwd " + Environment.CurrentDirectory);
            var child = Path.Combine(parent, "child");
            var created = Locked43(cwd, parent, original, Chmod, () => Directory.CreateDirectory(child));
            Check(created.Thrown.StartsWith("System.UnauthorizedAccessException: ", StringComparison.Ordinal) && created.Restore.Length == 0 && Mode(parent) == owner
                && Environment.CurrentDirectory == original && !Directory.Exists(child),
                Label("a CreateDirectory that throws under the mode-000 parent: the owner mode and the working directory are restored, nothing created"),
                created.Thrown + "; restore '" + created.Restore + "'; mode " + Mode(parent) + "; cwd " + Environment.CurrentDirectory);
            Chmod(cwd, UnixFileMode.None);
            Chmod(parent, UnixFileMode.None);
            var cleanup = Cleanup43(parent, original);
            Check(cleanup.Length == 0 && !Directory.Exists(parent) && Environment.CurrentDirectory == original,
                Label("Cleanup43 restores a mode-000 parent and cwd and removes them"), cleanup);
        }
        finally { Cleanup43(parent, original); }
    }

    // The McpServer.CallTool generic catch (catch (Exception e) { text = Failed(e.Message); isError = true; }) is the CLI
    // Program generic catch's twin (interfaces/cli/Program.cs: {"status":"failed","code":<message>}), which the MCP tools'
    // CLI byte equality relies on (diff, validate, room and plan carry Core's failure codes through it). Sweep: every listed
    // tool's Execute is replaced, in a fresh server, by a planted throw (IOException, UnauthorizedAccessException and
    // InvalidOperationException whose message is a path-bearing runtime-style text; McpInvalidParamsException), called
    // through tools/call with its required arguments. Recorded (not changed, CLI parity): each answers isError with exactly
    // {"status":"failed","code":<message>} and nothing on stderr; McpInvalidParamsException answers -32602 with its message.
    // A real reachable case, plan with a missing store, is pinned against the runtime message and the CLI line.
    static void GenericCatch43(string scratch, string cliDll, Action<bool, string, string> Check)
    {
        string Label(string what) => "P10-43 MCP generic catch: " + what;
        var server = new Bim.Mcp.McpServer();
        SweepFixtureSelfTest45(Check);
        var fixture = SweepFixture45(server);
        Check(fixture.Problem is null, Label("P10-45 fixture: McpServer's private instance ToolSpec[] 'tools' field, ToolSpec's string Name, array Parameters, settable Func<JsonElement,(string,bool)> Execute and record <Clone>$ are readable by reflection"),
            fixture.Problem ?? "");
        if (fixture.Problem is not null) return;
        var tools = fixture.Tools!;
        var nameProperty = fixture.Name!;
        var parametersProperty = fixture.Parameters!;
        var executeProperty = fixture.Execute!;
        var clone = fixture.Clone!;
        var listing = System.Text.Json.Nodes.JsonNode.Parse(server.Handle("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}") ?? "null");
        var listed = listing?["result"]?["tools"]?.AsArray().Select(tool => tool?["name"]?.GetValue<string>() ?? "").ToArray() ?? Array.Empty<string>();
        var names = Enumerable.Range(0, tools.Length).Select(i => (string)nameProperty.GetValue(tools.GetValue(i))!).ToArray();
        Check(names.Length > 16 && listed.SequenceEqual(names, StringComparer.Ordinal), Label("fixture: the swept array is the tools/list listing (core and revit_* tools)"),
            names.Length.ToString(CultureInfo.InvariantCulture) + " swept, " + listed.Length.ToString(CultureInfo.InvariantCulture) + " listed");
        System.Text.Json.Nodes.JsonNode? Value(string type) => type switch
        {
            "string" => "p10-43",
            "object" => new System.Text.Json.Nodes.JsonObject(),
            "integer" => 0,
            "boolean" => false,
            "array" => new System.Text.Json.Nodes.JsonArray("p10-43"),
            _ => null,
        };
        var planted = new (string Kind, Func<string, Exception> Make)[]
        {
            ("IOException", message => new IOException(message)),
            ("UnauthorizedAccessException", message => new UnauthorizedAccessException(message)),
            ("InvalidOperationException", message => new InvalidOperationException(message)),
            ("McpInvalidParamsException", message => new Bim.Mcp.McpInvalidParamsException(message)),
        };
        var swept = 0;
        for (var i = 0; i < tools.Length; i++)
        {
            var original = tools.GetValue(i)!;
            var name = names[i];
            var arguments = new System.Text.Json.Nodes.JsonObject();
            foreach (var parameter in (Array)parametersProperty.GetValue(original)!)
            {
                var parameterType = parameter!.GetType();
                if (!(bool)parameterType.GetProperty("Required")!.GetValue(parameter)!) continue;
                arguments[(string)parameterType.GetProperty("Name")!.GetValue(parameter)!] = Value((string)parameterType.GetProperty("Type")!.GetValue(parameter)!);
            }
            var problems = new List<string>();
            // P10-45: the planted Execute counts its calls, so a server that stops reading the swapped array fails loudly.
            var calls = 0;
            foreach (var (kind, make) in planted)
            {
                var message = "Access to the path '" + Path.Combine(scratch, "p10-43-planted", name + ".json") + "' is denied (planted " + kind + ").";
                Func<JsonElement, (string, bool)> throwing = _ => { calls++; throw make(message); };
                var copy = clone.Invoke(original, null)!;
                executeProperty.SetValue(copy, throwing);
                var request = new System.Text.Json.Nodes.JsonObject { ["jsonrpc"] = "2.0", ["id"] = 43, ["method"] = "tools/call", ["params"] = new System.Text.Json.Nodes.JsonObject { ["name"] = name, ["arguments"] = arguments.DeepClone() } }.ToJsonString();
                var previousError = Console.Error;
                var capturedError = new StringWriter();
                string? response = null;
                var thrown = "";
                try
                {
                    tools.SetValue(copy, i);
                    Console.SetError(capturedError);
                    response = server.Handle(request);
                }
                catch (Exception failure) { thrown = " threw " + failure.GetType().FullName + ": " + failure.Message; }
                finally
                {
                    Console.SetError(previousError);
                    tools.SetValue(original, i);
                }
                var node = response is null ? null : System.Text.Json.Nodes.JsonNode.Parse(response);
                bool ok;
                if (kind == "McpInvalidParamsException")
                    ok = node?["error"]?["code"]?.GetValue<int>() == -32602 && node?["error"]?["message"]?.GetValue<string>() == message && node?["result"] is null && node?["id"]?.GetValue<int>() == 43;
                else
                    ok = node?["result"]?["isError"]?.GetValue<bool>() == true && node?["result"]?["content"]?[0]?["text"]?.GetValue<string>() == JsonSerializer.Serialize(new { status = "failed", code = message })
                        && node?["result"]?["content"]?.AsArray().Count == 1 && node?["error"] is null;
                if (!ok || capturedError.ToString().Length > 0 || thrown.Length > 0)
                    problems.Add(kind + " => " + (response ?? "no response") + (capturedError.ToString().Length > 0 ? " stderr " + capturedError.ToString().Trim() : "") + thrown);
            }
            if (calls != planted.Length)
                problems.Add("P10-45 fixture: the planted Execute ran " + calls.ToString(CultureInfo.InvariantCulture) + " of " + planted.Length.ToString(CultureInfo.InvariantCulture) + " times (the server does not call the swapped tools array element)");
            swept++;
            Check(problems.Count == 0, Label(name + ": a planted throw escaping Execute answers isError {\"status\":\"failed\",\"code\":<its message>} (McpInvalidParamsException: -32602), nothing on stderr"),
                string.Join(" || ", problems).Replace(scratch, "<scratch>", StringComparison.Ordinal));
        }
        Check(swept == names.Length && names.SequenceEqual(Enumerable.Range(0, tools.Length).Select(i => (string)nameProperty.GetValue(tools.GetValue(i))!), StringComparer.Ordinal),
            Label("every listed tool was swept and the server's tools are restored"), swept.ToString(CultureInfo.InvariantCulture) + " of " + names.Length.ToString(CultureInfo.InvariantCulture));

        // Real reachable case (every host): plan with a store directory that does not exist. ProjectStore.Read's
        // DirectoryNotFoundException reaches the generic catch, so the code is the runtime message carrying the absolute
        // path, byte-equal to the CLI plan verb's Program generic-catch line (exit 1, empty stderr). Recorded, not changed.
        var missing = Path.Combine(scratch, "p10-43-missing-store");
        string runtime;
        try { File.ReadAllText(Path.Combine(missing, "project.json")); runtime = "read succeeded"; }
        catch (Exception failure) { runtime = failure.Message; }
        var expected = JsonSerializer.Serialize(new { status = "failed", code = runtime });
        var planRequest = new System.Text.Json.Nodes.JsonObject { ["jsonrpc"] = "2.0", ["id"] = 44, ["method"] = "tools/call", ["params"] = new System.Text.Json.Nodes.JsonObject { ["name"] = "plan", ["arguments"] = new System.Text.Json.Nodes.JsonObject { ["store_path"] = missing, ["desired"] = new System.Text.Json.Nodes.JsonObject() } } }.ToJsonString();
        var planNode = System.Text.Json.Nodes.JsonNode.Parse(new Bim.Mcp.McpServer().Handle(planRequest) ?? "null");
        var planText = planNode?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
        Check(runtime.Contains(missing, StringComparison.Ordinal) && planNode?["result"]?["isError"]?.GetValue<bool>() == true && planText == expected,
            Label("plan with a missing store: isError with the runtime message (absolute path) as the code"), (planText + " | runtime " + runtime).Replace(scratch, "<scratch>", StringComparison.Ordinal));
        var desired = Path.Combine(scratch, "p10-43-desired.json");
        File.WriteAllText(desired, "{}\n", new UTF8Encoding(false));
        MethodInfo? entry = null;
        try { entry = Assembly.LoadFrom(cliDll).EntryPoint; }
        catch (Exception error) when (error is IOException or BadImageFormatException) { Check(false, Label("the Release CLI assembly loads"), error.Message); }
        if (entry is null) { Check(false, Label("the Release CLI assembly has an entry point"), cliDll); return; }
        var cli = Invoke(entry, new[] { "plan", missing, desired });
        Check(cli.Exit == 1 && cli.Error.Length == 0 && cli.Line == planText, Label("plan with a missing store: the MCP text equals the CLI plan line (exit 1, empty stderr)"),
            ("exit " + cli.Exit.ToString(CultureInfo.InvariantCulture) + " " + cli.Line + " " + cli.Error + " | mcp " + planText).Replace(scratch, "<scratch>", StringComparison.Ordinal));
    }

    // P10-45: the sweep's reflection dependence on McpServer internals, named one by one so a rename or reshape fails loudly
    // with the missing piece: a private instance field "tools" holding an array; its element type's string Name, array
    // Parameters, writable Func<JsonElement, (string Text, bool IsError)> Execute and the record clone method "<Clone>$"
    // returning that type. Problem is null when every piece is present.
    static (Array? Tools, PropertyInfo? Name, PropertyInfo? Parameters, PropertyInfo? Execute, MethodInfo? Clone, string? Problem) SweepFixture45(object server)
    {
        var type = server.GetType();
        var field = type.GetField("tools", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field is null) return (null, null, null, null, null, "no private instance field 'tools' on " + type.FullName);
        if (field.GetValue(server) is not Array tools) return (null, null, null, null, null, "field 'tools' is not an array: " + field.FieldType.FullName);
        var spec = tools.GetType().GetElementType()!;
        var name = spec.GetProperty("Name");
        if (name is null || name.PropertyType != typeof(string)) return (tools, null, null, null, null, spec.Name + " has no string property 'Name'");
        var parameters = spec.GetProperty("Parameters");
        if (parameters is null || !parameters.PropertyType.IsArray) return (tools, name, null, null, null, spec.Name + " has no array property 'Parameters'");
        var execute = spec.GetProperty("Execute");
        if (execute is null || execute.PropertyType != typeof(Func<JsonElement, (string, bool)>) || !execute.CanWrite)
            return (tools, name, parameters, null, null, spec.Name + " has no writable Func<JsonElement, (string, bool)> property 'Execute'" + (execute is null ? "" : ": " + execute.PropertyType.Name + (execute.CanWrite ? "" : " read-only")));
        var clone = spec.GetMethod("<Clone>$");
        if (clone is null || clone.GetParameters().Length != 0 || !spec.IsAssignableFrom(clone.ReturnType)) return (tools, name, parameters, execute, null, spec.Name + " has no record clone method '<Clone>$' (not a record)");
        return (tools, name, parameters, execute, clone, null);
    }

    sealed record NoExecute45(string Name, string[] Parameters);
    sealed record WrongExecute45(string Name, string[] Parameters, Func<int, int> Execute);
    sealed class Holder45<T>
    {
        readonly T[] tools;
        public Holder45(T[] tools) => this.tools = tools;
        public int Count => tools.Length;
    }

    // P10-45 self-test of SweepFixture45 (every host): an object without the field, a tools element without Execute and one
    // with an Execute of another type are each refused with that piece named; the real McpServer is accepted.
    static void SweepFixtureSelfTest45(Action<bool, string, string> Check)
    {
        var none = SweepFixture45(new object()).Problem ?? "accepted";
        var noExecute = new Holder45<NoExecute45>(new[] { new NoExecute45("n", Array.Empty<string>()) });
        var missing = SweepFixture45(noExecute).Problem ?? "accepted";
        var wrongExecute = new Holder45<WrongExecute45>(new[] { new WrongExecute45("n", Array.Empty<string>(), x => x) });
        var wrong = SweepFixture45(wrongExecute).Problem ?? "accepted";
        Check(noExecute.Count == 1 && wrongExecute.Count == 1 && none.Contains("'tools'", StringComparison.Ordinal) && missing.Contains("'Execute'", StringComparison.Ordinal)
            && wrong.Contains("'Execute': Func`2", StringComparison.Ordinal),
            "P10-45 MCP generic catch: fixture self-test: a missing tools field and a missing or mistyped Execute are refused by name", none + " | " + missing + " | " + wrong);
    }

    // Linux, as measured (see the contract section P10-41): GetFullPath accepts the relative lock; the read of a lock under
    // the mode-000 directory reports it missing, and pin's exclusive write is refused write_failed.
    const string LockMissing41 = """{"status":"failed","code":"lock_missing","subject":"skills.lock.json","detail":"lock file not found"}""";

    // P10-43: null when line is exactly the members status failed, code write_failed, subject skills.lock.json and a string
    // detail that names denied and ends "' is denied." (the .NET UnauthorizedAccessException path message's suffix), else
    // what differs. The rest of the detail (the message's wording before the path) is not pinned.
    static string? WriteFailed43(string line, string denied)
    {
        System.Text.Json.Nodes.JsonObject? node;
        try { node = System.Text.Json.Nodes.JsonNode.Parse(line) as System.Text.Json.Nodes.JsonObject; }
        catch (JsonException error) { return "not JSON: " + error.Message; }
        if (node is null) return "not a JSON object";
        System.Text.Json.Nodes.JsonObject members = node;
        var names = string.Join(",", members.Select(member => member.Key));
        if (names != "status,code,subject,detail") return "members " + names + " (want status,code,subject,detail)";
        string? Text(string name) => members[name] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        if (Text("status") != "failed" || Text("code") != "write_failed") return "status/code " + Text("status") + "/" + Text("code");
        if (Text("subject") != LockName39) return "subject " + Text("subject");
        var detail = Text("detail");
        if (detail is null) return "detail is not a string";
        if (!detail.EndsWith("' is denied.", StringComparison.Ordinal)) return "detail does not end \"' is denied.\": " + detail;
        if (!detail.Contains("'" + denied + "'", StringComparison.Ordinal)) return "detail does not name '" + denied + "': " + detail;
        return null;
    }

    // Self-test of WriteFailed43 (every host): the P10-41 measured shape and a reworded prefix are accepted; another code,
    // subject, suffix, path, member order or a missing detail are refused.
    static void WriteFailed43SelfTest(Action<bool, string, string> Check)
    {
        const string Denied = "<scratch>/p/cwd/skills.lock.json.<guid>.tmp";
        string Line(string code, string subject, string detail) => JsonSerializer.Serialize(new { status = "failed", code, subject, detail });
        var measured = Line("write_failed", LockName39, "Access to the path '" + Denied + "' is denied.");
        var accepted = new[] { measured, Line("write_failed", LockName39, "Permission refused for the path '" + Denied + "' is denied.") };
        var refused = new[]
        {
            Line("write_error", LockName39, "Access to the path '" + Denied + "' is denied."),
            Line("write_failed", "other.json", "Access to the path '" + Denied + "' is denied."),
            Line("write_failed", LockName39, "Access to the path '" + Denied + "' is refused."),
            Line("write_failed", LockName39, "Access to the path '<scratch>/q/cwd' is denied."),
            JsonSerializer.Serialize(new { status = "failed", subject = LockName39, code = "write_failed", detail = "Access to the path '" + Denied + "' is denied." }),
            JsonSerializer.Serialize(new { status = "failed", code = "write_failed", subject = LockName39 }),
            "not json",
        };
        var wrong = accepted.Where(line => WriteFailed43(line, Denied) is not null).Concat(refused.Where(line => WriteFailed43(line, Denied) is null)).ToArray();
        Check(wrong.Length == 0, "P10-43 write_failed matcher self-test: accepts the measured shape and a reworded prefix, refuses another code, subject, suffix, path, member order or a missing detail",
            string.Join(" || ", wrong));
    }

    // P10-45: the restore problems of every call, each named by its call ("" when every call restored).
    static string RestoreProblems45(params (string Call, string Problem)[] calls) =>
        string.Join("; ", calls.Where(call => call.Problem.Length > 0).Select(call => call.Call + " call: " + call.Problem));

    // P10-45 self-test (every host): a problem of the first call is kept when the second call restores, both are kept
    // when both fail, none when both restore.
    static void RestoreProblemsSelfTest45(Action<bool, string, string> Check)
    {
        var first = RestoreProblems45(("reference", "chmod IOException: planted"), ("locked", ""));
        var both = RestoreProblems45(("reference", "chmod IOException: a"), ("locked", "cwd IOException: b"));
        var none = RestoreProblems45(("reference", ""), ("locked", ""));
        Check(first == "reference call: chmod IOException: planted" && both == "reference call: chmod IOException: a; locked call: cwd IOException: b" && none.Length == 0,
            "P10-45 lock verbs restore aggregation self-test: the reference call's problem survives a clean locked call, both are kept, none when both restore",
            first + " | " + both + " | " + none);
    }

    static void LockVerbsUnsearchable41(string root, string scratch, string cliDll, Action<bool, string, string> Check, Action<string, string> skip)
    {
        const string Gamma = "bim.test.gamma";
        const string Delta = "bim.test.delta";
        const string Caba = "ar.caba.codigo-edificacion";
        const string Stub = "xx.example.codigo-ejemplo";
        var verbs = new[] { "jurisdiction-resolve", "skills-lock pin", "skills-lock update", "skills-lock unpin" };
        var modes = new[] { "cwd", "parent" };
        string Label(string mode, string what) => "P10-41 lock verbs, unsearchable " + mode + ": " + what;
        var why = OperatingSystem.IsWindows() ? "Unix permission bits (chmod 000) only" : PrivilegeSkip(scratch, "p10-41-lock-probe");
        var ran = 0;
        if (why is not null)
        {
            foreach (var mode in modes) foreach (var verb in verbs) skip(Prefix + Label(mode, verb + " relative lock"), why);
        }
        else
        {
            Assembly? cli = null;
            try { cli = Assembly.LoadFrom(cliDll); }
            catch (Exception error) when (error is IOException or BadImageFormatException) { Check(false, "the Release CLI assembly loads", error.Message); }
            var entry = cli?.EntryPoint;
            if (entry is null) { Check(false, "the Release CLI assembly has an entry point", cliDll); return; }
            var original = Environment.CurrentDirectory;
            var exitBefore = Environment.ExitCode;
            var repositorySkills = Path.Combine(root, "skills");
            var outside = Path.Combine(scratch, "p10-41-lock-outside");
            var skills = Path.Combine(outside, "skills");
            foreach (var pack in new[] { Gamma, Delta })
            {
                Directory.CreateDirectory(Path.Combine(skills, pack));
                File.WriteAllBytes(Path.Combine(skills, pack, "skill.json"), new UTF8Encoding(false).GetBytes(
                    "{\"manifest_version\":1,\"id\":\"" + pack + "\",\"version\":\"1.0.0\",\"kind\":\"qa\",\"description\":\"P10-41 lock pack\",\"priority\":10}\n"));
            }
            var skillsDigest = string.Join("|", Directory.GetFiles(skills, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Select(f => f + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))));
            var macOS = OperatingSystem.IsMacOS();
            // The scratch directory and any 32-hex-digit name (a temporary lock's Guid) are replaced after serialization, in the
            // measured line and the expected line alike.
            string Normalize(string line) => System.Text.RegularExpressions.Regex.Replace(line.Replace(scratch, "<scratch>", StringComparison.Ordinal), "[0-9a-f]{32}", "<guid>");
            var pathInvalid = JsonSerializer.Serialize(new { status = "failed", code = "skills_lock_path_invalid", detail = "lock" });
            foreach (var mode in modes)
            {
                foreach (var verb in verbs)
                {
                    var label = Label(mode, verb + " relative lock");
                    var parent = Path.Combine(scratch, "p10-41-lock-" + mode + "-" + verb.Replace(' ', '-'));
                    var cwd = Path.Combine(parent, "cwd");
                    Directory.CreateDirectory(cwd);
                    var lockFile = Path.Combine(cwd, LockName39);
                    byte[]? lockBytes = verb switch
                    {
                        "jurisdiction-resolve" => Bim.Core.SkillLockFile.Serialize(Bim.Core.SkillLocking.Pin(repositorySkills, new[] { Caba, Stub })),
                        "skills-lock pin" => null,
                        _ => Bim.Core.SkillLockFile.Serialize(Bim.Core.SkillLocking.Pin(skills, new[] { Gamma, Delta })),
                    };
                    void Reset()
                    {
                        if (File.Exists(lockFile)) File.Delete(lockFile);
                        if (lockBytes is not null) File.WriteAllBytes(lockFile, lockBytes);
                    }
                    string[] args = verb switch
                    {
                        "jurisdiction-resolve" => new[] { "jurisdiction-resolve", "--code", "ar.caba", "--lock", LockName39 },
                        "skills-lock pin" => new[] { "skills-lock", "pin", skills, LockName39, Gamma },
                        "skills-lock update" => new[] { "skills-lock", "update", skills, LockName39 },
                        _ => new[] { "skills-lock", "unpin", skills, LockName39, Gamma },
                    };
                    var locked = mode == "cwd" ? cwd : parent;
                    // P10-45: each call returns its own restore problems, and the check below aggregates both calls'.
                    (int Exit, string Line, string Error, string Probe, string Restore) Call(bool lockIt)
                    {
                        var probe = "";
                        var restore = "";
                        (int Exit, string Line, string Error) run;
                        try
                        {
                            run = Invoke(entry, args, () =>
                            {
                                Directory.SetCurrentDirectory(cwd);
                                if (lockIt) Chmod(locked, UnixFileMode.None);
                                try { Path.GetFullPath(LockName39); probe = "GetFullPath accepted"; }
                                catch (Exception failure) { probe = "GetFullPath threw " + failure.GetType().FullName; }
                            });
                        }
                        finally
                        {
                            // P10-43: each restore step on its own (Restore43), so a throwing chmod still restores the working directory.
                            restore = Restore43(locked, original);
                        }
                        return (run.Exit, Normalize(run.Line), run.Error, probe, restore);
                    }
                    Reset();
                    var reference = Call(false);
                    var referenceWrote = File.Exists(lockFile);
                    Check(reference.Exit == 0 && reference.Error.Length == 0 && reference.Line.StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal) && referenceWrote,
                        label + ": precondition: with the working directory searchable the relative lock succeeds", "exit " + reference.Exit.ToString(CultureInfo.InvariantCulture) + " " + reference.Line + " " + reference.Error);
                    Reset();
                    var entriesBefore = Directory.GetFileSystemEntries(cwd).Length;
                    var result = Call(true);
                    ran++;
                    var measured = result.Probe + "; exit " + result.Exit.ToString(CultureInfo.InvariantCulture) + " " + result.Line + (result.Error.Length > 0 ? " stderr " + result.Error : "");
                    var expectedProbe = macOS ? "GetFullPath threw System.UnauthorizedAccessException" : "GetFullPath accepted";
                    Check(result.Probe == expectedProbe, label + ": precondition: " + expectedProbe + " (" + (macOS ? "macOS" : "Linux") + ")", measured);
                    if (macOS || verb != "skills-lock pin")
                    {
                        var expected = macOS ? pathInvalid : LockMissing41;
                        Check(result.Exit == 1 && result.Line == expected && result.Error.Length == 0, label + ": exactly " + expected + " (exit 1, empty stderr)", measured);
                    }
                    else
                    {
                        // Linux pin: the exclusive write of the temporary lock (cwd mode) or the directory creation (parent mode) is
                        // denied. P10-43: the detail is the runtime's UnauthorizedAccessException message, so only the code, the
                        // subject, the denied path and the "' is denied." suffix are pinned (WriteFailed43), not the full text.
                        var denied = Normalize(mode == "cwd" ? Path.Combine(cwd, LockName39 + "." + new string('0', 32) + ".tmp") : cwd);
                        var problem = WriteFailed43(result.Line, denied);
                        Check(result.Exit == 1 && problem is null && result.Error.Length == 0,
                            label + ": write_failed, subject " + LockName39 + ", a detail naming " + denied + " and ending ' is denied.' (exit 1, empty stderr)", measured + (problem is null ? "" : " | " + problem));
                    }
                    var restoreProblem = RestoreProblems45(("reference", reference.Restore), ("locked", result.Restore));
                    Check(Environment.CurrentDirectory == original && restoreProblem.Length == 0, label + ": the working directory and the owner mode are restored after both calls (finally)",
                        Environment.CurrentDirectory + (Environment.CurrentDirectory == original ? "" : " != " + original) + " " + restoreProblem);
                    var after = File.Exists(lockFile) ? File.ReadAllBytes(lockFile) : null;
                    var unchanged = lockBytes is null ? after is null : after is not null && after.AsSpan().SequenceEqual(lockBytes);
                    Check(unchanged && Directory.GetFileSystemEntries(cwd).Length == entriesBefore && Directory.GetFileSystemEntries(parent).Length == 1,
                        label + ": the lock is unchanged and nothing is written", string.Join(",", Directory.GetFileSystemEntries(cwd).Concat(Directory.GetFileSystemEntries(parent))).Replace(scratch, "<scratch>", StringComparison.Ordinal));
                    Directory.Delete(parent, true);
                }
            }
            var skillsAfter = string.Join("|", Directory.GetFiles(skills, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Select(f => f + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))));
            Check(skillsAfter == skillsDigest, Label("both", "the skills directory is unchanged"), "");
            Directory.Delete(outside, true);
            Check(Environment.ExitCode == exitBefore, Label("both", "Environment.ExitCode is restored (finally)"),
                Environment.ExitCode.ToString(CultureInfo.InvariantCulture) + ", before " + exitBefore.ToString(CultureInfo.InvariantCulture));
            Check(ran == 8, Label("both", "every case of both modes ran on a non-root runner"), "ran " + ran.ToString(CultureInfo.InvariantCulture) + " of 8");
        }
        if (!OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            Check(why is null, "P10-41 lock verbs: the GitHub Actions Linux/macOS runner is non-root, so the cases run", why ?? "");
    }
}
