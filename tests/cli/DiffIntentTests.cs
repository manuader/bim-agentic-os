using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Bim.Core;

// P3-12 `diff-intent --project <head.json> --intent <after.json>`: the read-only plan apply-intent would commit on that
// head (IntentEdits.FromStored, then IncrementalDiff.Diff with IncrementalDiff.OrderOf(set.After) create placement).
// Oracles, all against the real Release CLI process:
//   1. stdout is byte-identical to the in-process library plan, to Compiler.Diff(head, compile(intent)) and to the "plan"
//      member apply-intent prints on a store whose head is the same project, for the same intent (checked before that
//      apply-intent commits, on every success case: creates last, creates before survivors, no change, an empty head).
//   2. diff-intent writes nothing: the directory holding the project file and the intent file, and the store the
//      project was read from, are byte-, name- and mtime-unchanged after every run, success or refusal.
//   3. Every refusal prints a typed envelope and exits 1: diff_intent_usage, input_*, diff_intent_project_invalid,
//      diff_intent_intent_invalid (reason and field equal IntentJson.Parse's), diff_intent_intent_refused (refusals
//      equal IntentCompiler.Compile's issues), invalid_ir, invalid_project_identity and edit_bound_exceeded; the intent
//      refusals are apply-intent's envelopes on the same head with the diff_intent_ prefix.
//   4. A child process under de_DE prints the same bytes as one under the default culture.
internal static class DiffIntentTests
{
    static readonly Guid ProjectId = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    static readonly Provenance Source = new("cli diff-intent fixture", "specified");

    static IntentRoom Room(string key, double x, double y, double width, double depth, string level = "l0") => new(key, "Room " + key, level, "a", x, y, width, depth);
    static IntentOpening Window(string key, IntentHost host, double offset, double width = 1.2) => new(key, IntentOpeningKind.Window, host, offset, width, 1.2, 0.9);
    static IntentOpening Door(string key, IntentHost host, double offset) => new(key, IntentOpeningKind.Door, host, offset, 0.9, 2.1, 0);
    static LayoutIntent Intent(IntentRoom[] rooms, IntentOpening[] openings) => new(LayoutIntent.CurrentVersion, Source,
        new[] { new IntentLevel("l0", "Ground", 0) }, new[] { new IntentWallType("a", "A", 0.2, 2.8) }, rooms, openings);

    // Two adjacent rooms, a door on the shared wall and a window on each room.
    static LayoutIntent Base() => Intent(
        new[] { Room("r0", 0, 0, 4, 3), Room("r1", 4, 0, 3, 3) },
        new[] { Door("d0", new IntentSharedWall("r0", "r1"), 0.8), Window("w0", new IntentRoomSide("r0", IntentSide.South), 1.0), Window("w1", new IntentRoomSide("r1", IntentSide.North), 0.8, 1.0) });

    public static void Run(string root, string work, string dotnet, string cliDll, byte[] overCap, Action<bool, string, string> check)
    {
        var scratch = Path.Combine(work, "diff-intent");
        Directory.CreateDirectory(scratch);
        void Check(bool condition, string name, string detail = "") => check(condition, "diff-intent: " + name, detail);

        (int Exit, string Out, string Err) CliWith(IReadOnlyDictionary<string, string>? environment, params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            if (environment is not null) foreach (var (key, value) in environment) start.Environment[key] = value;
            start.ArgumentList.Add(cliDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000)) { process.Kill(true); return (-1, "<timed out>", ""); }
            process.WaitForExit();
            var text = stdout.Result;
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];
            return (process.ExitCode, text, stderr.Result.ReplaceLineEndings(" ").Trim());
        }
        (int Exit, string Out, string Err) Cli(params string[] arguments) => CliWith(null, arguments);
        static string D((int Exit, string Out, string Err) r) => "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " " + (r.Out.Length > 500 ? r.Out[..500] + "..." : r.Out) + (r.Err.Length > 0 ? " stderr " + r.Err : "");
        static string FailedCode(string code) => JsonSerializer.Serialize(new { status = "failed", code });
        var stamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var counter = 0;
        // Every input lives in its own directory, so "nothing written" covers the whole directory around it.
        string Input(string name, string content) => InputBytes(name, Encoding.UTF8.GetBytes(content));
        string InputBytes(string name, byte[] content)
        {
            var directory = Path.Combine(scratch, "in-" + (++counter).ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name.Replace(' ', '-'));
            File.WriteAllBytes(path, content);
            File.SetLastWriteTimeUtc(path, stamp);
            return path;
        }
        string IntentFile(string name, LayoutIntent intent) => Input(name + ".intent.json", IntentJson.Serialize(intent));
        string ProjectFile(string name, Project project) => Input(name + ".project.json", JsonSerializer.Serialize(project));
        string NewStore(Project project)
        {
            var directory = Path.Combine(scratch, "store-" + (++counter).ToString(CultureInfo.InvariantCulture));
            new ProjectStore(directory).Initialize(project);
            foreach (var file in Directory.GetFiles(directory)) File.SetLastWriteTimeUtc(file, stamp);
            return directory;
        }
        static SortedDictionary<string, (byte[] Bytes, DateTime Time)> Snapshot(string directory)
        {
            var result = new SortedDictionary<string, (byte[], DateTime)>(StringComparer.Ordinal);
            if (!Directory.Exists(directory)) return result;
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                result[Path.GetRelativePath(directory, file)] = (File.ReadAllBytes(file), File.GetLastWriteTimeUtc(file));
            foreach (var sub in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories))
                result[Path.GetRelativePath(directory, sub) + "/"] = (Array.Empty<byte>(), default);
            return result;
        }
        static bool Same(SortedDictionary<string, (byte[] Bytes, DateTime Time)> a, SortedDictionary<string, (byte[] Bytes, DateTime Time)> b) =>
            a.Keys.SequenceEqual(b.Keys) && a.All(kv => b[kv.Key].Bytes.AsSpan().SequenceEqual(kv.Value.Bytes) && b[kv.Key].Time == kv.Value.Time);
        static string? Parent(string? path) => path is null ? null : Path.GetDirectoryName(Path.GetFullPath(path));
        static JsonElement? Json(string text)
        {
            try { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
            catch (JsonException) { return null; }
        }
        static string Raw(JsonElement? element, string name) => element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) ? value.GetRawText() : "<missing " + name + ">";
        static string ParseRefusal(string text)
        {
            try { IntentJson.Parse(text); return "<accepted>"; }
            catch (IntentException error) { return JsonSerializer.Serialize(new { status = "failed", code = "diff_intent_intent_invalid", reason = error.Code, field = error.Field }); }
        }
        static string CompileRefusal(Project head, LayoutIntent intent)
        {
            try { IntentCompiler.Compile(head.Id, head.Revision, intent); return "<accepted>"; }
            catch (IntentException error)
            {
                return JsonSerializer.Serialize(new { status = "failed", code = "diff_intent_intent_refused", refusals = error.Issues.Select(i => new { code = i.Code, subject = i.Key, detail = i.Detail }) });
            }
        }
        static string LibraryRefusal(Project head, LayoutIntent intent)
        {
            try
            {
                var set = IntentEdits.FromStored(head, intent);
                IncrementalDiff.Diff(head, set.Edits, createOrder: IncrementalDiff.OrderOf(set.After));
                return "succeeded";
            }
            catch (IntentException error) { return "intent:" + error.Code; }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return error.Message; }
        }
        // Runs the CLI and checks that the directories around the given paths (and the given extra directories) are unchanged.
        (int Exit, string Out, string Err) Unwritten(string name, string[] watched, params string[] arguments)
        {
            var befores = watched.Select(Snapshot).ToArray();
            var r = Cli(arguments);
            for (var i = 0; i < watched.Length; i++)
                Check(Same(befores[i], Snapshot(watched[i])), "nothing written (" + name + "): " + Path.GetFileName(watched[i]));
            return r;
        }

        // A successful plan on the head of `store`: equals the library plan, Compiler.Diff and, run before it,
        // apply-intent's plan member on that store; nothing written by diff-intent. Then apply-intent commits (when
        // commit is true) so the store moves on. Returns diff-intent's stdout.
        string Plans(string name, string store, LayoutIntent intent, bool commit = true)
        {
            var head = new ProjectStore(store).Read();
            var set = IntentEdits.FromStored(head, intent);
            var libraryPlan = JsonSerializer.Serialize(IncrementalDiff.Diff(head, set.Edits, createOrder: IncrementalDiff.OrderOf(set.After)).Plan);
            Check(libraryPlan == JsonSerializer.Serialize(Compiler.Diff(head, set.After)), "fixture: the ordered incremental plan equals Compiler.Diff(head, compile(intent)) (" + name + ")");
            var projectPath = ProjectFile(name, head);
            var intentPath = IntentFile(name, intent);
            var r = Unwritten(name, new[] { Parent(projectPath)!, Parent(intentPath)!, store }, "diff-intent", "--project", projectPath, "--intent", intentPath);
            Check(r.Exit == 0 && r.Err.Length == 0, "succeeds with exit 0 and no stderr (" + name + ")", D(r));
            Check(r.Out == libraryPlan, "prints the library plan, byte-identical to Compiler.Diff(head, compile(intent)) (" + name + ")", D(r));
            // Swapped argument order: the same bytes.
            var swapped = Cli("diff-intent", "--intent", intentPath, "--project", projectPath);
            Check(swapped.Exit == 0 && swapped.Out == r.Out, "the same plan with --intent before --project (" + name + ")", D(swapped));
            if (commit)
            {
                var applied = Cli("apply-intent", "--store", store, "--intent", intentPath, "--expected-revision", head.Revision.ToString(CultureInfo.InvariantCulture));
                Check(applied.Exit == 0 && Raw(Json(applied.Out), "status") == "\"succeeded\"", "fixture: apply-intent commits the same intent on the same head (" + name + ")", D(applied));
                Check(Raw(Json(applied.Out), "plan") == r.Out, "diff-intent's stdout is byte-equal to apply-intent's plan member on the same head and intent (" + name + ")", D(applied));
            }
            return r.Out;
        }

        var baseIntent = Base();
        // Step 1: move w0, remove w1, add window zz (sorts after every survivor): updates, a delete and a create last.
        var step1 = baseIntent with
        {
            Openings = new[] { Door("d0", new IntentSharedWall("r0", "r1"), 0.8), Window("w0", new IntentRoomSide("r0", IntentSide.South), 1.5), Window("zz", new IntentRoomSide("r0", IntentSide.West), 0.8) },
        };
        // Step 2: a third room: creates that sort before surviving spaces and openings (placed by OrderOf).
        var step2 = step1 with { Rooms = new[] { Room("r0", 0, 0, 4, 3), Room("r1", 4, 0, 3, 3), Room("r2", 7, 0, 4, 3) } };

        // 1. A chain: each step planned by diff-intent, cross-checked against apply-intent on the same head, then committed.
        var chain = NewStore(IntentCompiler.Compile(ProjectId, 0, baseIntent));
        var firstOut = Plans("step 1 move delete add-last", chain, step1);
        {
            var head1 = new ProjectStore(chain).Read();
            var set2 = IntentEdits.FromStored(head1, step2);
            var appended = IncrementalDiff.Diff(head1, set2.Edits);
            Check(set2.Edits.Any(e => e.Action == "create") && JsonSerializer.Serialize(appended.After.Elements) != JsonSerializer.Serialize(set2.After.Elements),
                "fixture: step 2 creates elements that sort before survivors (appending them would leave compile order)");
            var plan2 = Plans("step 2 add a room", chain, step2);
            Check(plan2 != JsonSerializer.Serialize(appended.Plan), "step 2: the plan places creates by OrderOf, not appended like diff-edits");
        }
        var noOp = Plans("step 2 again (no change)", chain, step2);
        {
            var plan = JsonSerializer.Deserialize<ExecutionPlan>(noOp);
            Check(plan is not null && plan.Operations.Length == 0 && plan.DirtyIds.Length == 0 && plan.BeforeHash == plan.AfterHash,
                "the no-change plan is empty: no operations, no dirty ids, BeforeHash equals AfterHash", noOp);
        }
        Plans("back to the base intent", chain, baseIntent);
        Check(new ProjectStore(chain).Read().Revision == 4, "fixture: four apply-intent commits moved the chain to revision 4 (diff-intent committed nothing)");
        // A head that is not a store head at all (revision 7, never stored) plans the same way without a commit.
        Plans("a bare head at revision 7", NewStore(IntentCompiler.Compile(ProjectId, 7, baseIntent)), step2, commit: false);
        // An empty head within IncrementalDiff.MaxEdits (204 isolated rooms: 1 + 204 * 5 = 1021 creates).
        var empty = new Project(ProjectId, 0, Array.Empty<Element>());
        var many = Intent(Enumerable.Range(0, 205).Select(i => Room("r" + i.ToString("000", CultureInfo.InvariantCulture), i * 10, 0, 4, 3)).ToArray(), Array.Empty<IntentOpening>());
        var fewer = many with { Rooms = many.Rooms.Take(204).ToArray() };
        Plans("204 new rooms on an empty head", NewStore(empty), fewer);

        // A refusal: exact stdout, exit 1, nothing written around the project, the intent or the extra directories.
        void Refused(string name, string expected, string? projectPath, string? intentPath, string[]? extra = null, string[]? arguments = null)
        {
            var watched = new[] { Parent(projectPath), Parent(intentPath) }.Where(p => p is not null).Select(p => p!).Concat(extra ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
            var r = Unwritten(name, watched, arguments ?? new[] { "diff-intent", "--project", projectPath!, "--intent", intentPath! });
            Check(r.Exit == 1 && r.Out == expected, "refuses " + name + " as " + expected, D(r));
        }

        var head0 = IntentCompiler.Compile(ProjectId, 0, baseIntent);
        var headPath = ProjectFile("head0", head0);
        var refusalStore = NewStore(head0);

        // 2. Malformed intents: diff_intent_intent_invalid with IntentJson.Parse's reason and field; apply-intent on the
        //    same head refuses the same file with the same envelope under its own prefix.
        var canonical = IntentJson.Serialize(baseIntent);
        foreach (var (label, text) in new[]
        {
            ("not JSON", "{"), ("JSON null", "null"), ("an array", "[]"), ("empty file", ""), ("trailing content", canonical + " {}"),
            ("version 2", canonical.Replace("\"intent_version\":1", "\"intent_version\":2", StringComparison.Ordinal)),
            ("an unknown member", canonical.Replace("\"levels\":", "\"levelz\":[],\"levels\":", StringComparison.Ordinal)),
            ("a duplicate member", canonical.Replace("\"levels\":", "\"openings\":[],\"levels\":", StringComparison.Ordinal)),
            ("a missing member", canonical.Replace("\"name\":\"Ground\",", "", StringComparison.Ordinal)),
            ("a string where a number is required", canonical.Replace("\"thickness_m\":0.2", "\"thickness_m\":\"0.2\"", StringComparison.Ordinal)),
            ("an element edits array instead of an intent", "[{\"Action\":\"delete\",\"Id\":\"00000000-0000-0000-0000-000000000003\"}]"),
            ("more than 1048576 characters", canonical + new string(' ', IntentJson.MaxInputChars + 1 - canonical.Length)),
        })
        {
            var expected = ParseRefusal(text);
            Check(expected != "<accepted>", "fixture: IntentJson.Parse refuses an intent that is " + label, expected);
            var path = Input("bad-" + label + ".intent.json", text);
            Refused("an intent that is " + label, expected, headPath, path);
            var applied = Cli("apply-intent", "--store", refusalStore, "--intent", path);
            Check(applied.Exit == 1 && applied.Out == expected.Replace("\"diff_intent_intent_invalid\"", "\"apply_intent_intent_invalid\"", StringComparison.Ordinal),
                "apply-intent refuses the same intent that is " + label + " with the same envelope (apply_intent_ prefix)", D(applied));
        }

        // 3. Intents the compiler refuses: diff_intent_intent_refused carrying IntentCompiler.Compile's issues.
        foreach (var (label, intent, code) in new (string, LayoutIntent, string)[]
        {
            ("overlapping rooms", baseIntent with { Rooms = new[] { Room("r0", 0, 0, 4, 3), Room("r1", 3, 0, 3, 3) }, Openings = Array.Empty<IntentOpening>() }, IntentCodes.RoomOverlap),
            ("a room on an unknown level", baseIntent with { Rooms = new[] { Room("r0", 0, 0, 4, 3), Room("r1", 4, 0, 3, 3, "l9") } }, IntentCodes.UnknownLevel),
            ("a window outside its wall", baseIntent with { Openings = new[] { Window("w0", new IntentRoomSide("r0", IntentSide.South), 10) } }, IntentCodes.OpeningOutsideWall),
            ("a door between rooms that are not adjacent", baseIntent with { Rooms = new[] { Room("r0", 0, 0, 4, 3), Room("r1", 10, 0, 3, 3) }, Openings = new[] { Door("d0", new IntentSharedWall("r0", "r1"), 0.8) } }, IntentCodes.NotAdjacent),
        })
        {
            var expected = CompileRefusal(head0, intent);
            Check(expected.Contains("\"code\":\"" + code + "\"", StringComparison.Ordinal), "fixture: IntentCompiler refuses " + label + " with " + code, expected);
            var path = IntentFile("refused-" + label, intent);
            Refused("an intent with " + label, expected, headPath, path);
            var applied = Cli("apply-intent", "--store", refusalStore, "--intent", path);
            Check(applied.Exit == 1 && applied.Out == expected.Replace("\"diff_intent_intent_refused\"", "\"apply_intent_intent_refused\"", StringComparison.Ordinal),
                "apply-intent refuses the same intent with " + label + " with the same envelope (apply_intent_ prefix)", D(applied));
        }

        // 4. Library refusals on the head or the edit set: the library's codes.
        {
            var level = head0.Elements[0];
            var invalidHead = head0 with { Elements = head0.Elements.Append(new Element(Guid.Parse("00000000-0000-0000-0000-0000000000f1"), "plan", Array.Empty<double>(), new[] { Guid.Parse("00000000-0000-0000-0000-0000000000f2"), level.Id }, Source)).ToArray() };
            Check(LibraryRefusal(invalidHead, baseIntent) == "invalid_ir", "fixture: FromStored refuses a head with a dangling dependency as invalid_ir", LibraryRefusal(invalidHead, baseIntent));
            Refused("a head with a dangling dependency", FailedCode("invalid_ir"), ProjectFile("dangling", invalidHead), IntentFile("on-dangling", baseIntent));
            var anonymous = new Project(Guid.Empty, 0, Array.Empty<Element>());
            Check(LibraryRefusal(anonymous, baseIntent) == "invalid_project_identity", "fixture: FromStored refuses an empty project id as invalid_project_identity");
            Refused("a head whose project id is empty", FailedCode("invalid_project_identity"), ProjectFile("anonymous", anonymous), IntentFile("on-anonymous", baseIntent));
            Check(LibraryRefusal(empty, many) == IncrementalDiff.BoundExceeded, "fixture: 205 new rooms exceed IncrementalDiff.MaxEdits", LibraryRefusal(empty, many));
            Refused("an intent needing more than IncrementalDiff.MaxEdits edits", FailedCode(IncrementalDiff.BoundExceeded), ProjectFile("empty", empty), IntentFile("too-many", many));
        }

        // 5. Bounded and missing inputs (BoundedInput's codes), then the project's shape.
        var okIntent = IntentFile("ok", step1);
        Refused("an intent over 8 MiB", FailedCode("input_too_large"), headPath, InputBytes("over-cap.intent.json", overCap));
        Refused("a project over 8 MiB", FailedCode("input_too_large"), InputBytes("over-cap.project.json", overCap), okIntent);
        Refused("a missing intent file", FailedCode("input_not_found"), headPath, Path.Combine(scratch, "absent.intent.json"));
        Refused("a missing project file", FailedCode("input_not_found"), Path.Combine(scratch, "absent.project.json"), okIntent);
        Check(!File.Exists(Path.Combine(scratch, "absent.project.json")) && !File.Exists(Path.Combine(scratch, "absent.intent.json")), "missing inputs are not created");
        var aDirectory = Path.Combine(scratch, "a-directory.json"); Directory.CreateDirectory(aDirectory);
        Refused("a directory as intent", FailedCode("input_not_regular_file"), headPath, aDirectory);
        Refused("a directory as project", FailedCode("input_not_regular_file"), aDirectory, okIntent);
        // A store directory's project.json is a {Project, Receipts} state, not a Project: refused, the store untouched.
        Refused("a store state file as project", FailedCode("diff_intent_project_invalid"), Path.Combine(refusalStore, "project.json"), okIntent, new[] { refusalStore });
        foreach (var (label, text) in new[]
        {
            ("not JSON", "{"), ("JSON null", "null"), ("an array", "[]"), ("empty file", ""),
            ("a project with null elements", "{\"Id\":\"00000000-0000-0000-0000-0000000000b2\",\"Revision\":0,\"Elements\":null}"),
            ("a project with a null element", "{\"Id\":\"00000000-0000-0000-0000-0000000000b2\",\"Revision\":0,\"Elements\":[null]}"),
            ("a project with null geometry", JsonSerializer.Serialize(head0).Replace("\"GeometryMetres\":[", "\"GeometryMetres\":null,\"X\":[", StringComparison.Ordinal)),
        })
            Refused("a project that is " + label, FailedCode("diff_intent_project_invalid"), Input("bad-" + label + ".project.json", text), okIntent);
        // Precedence: a malformed project is reported before a malformed intent.
        Refused("a malformed project and a malformed intent", FailedCode("diff_intent_project_invalid"), Input("bad.project.json", "{"), Input("bad.intent.json", "{"));

        // 6. Usage: --project and --intent once each, nothing else; checked before any file is read.
        foreach (var bad in new[]
        {
            new[] { "diff-intent" }, new[] { "diff-intent", "--project", headPath }, new[] { "diff-intent", "--intent", okIntent }, new[] { "diff-intent", "--project", headPath, "--intent" },
            new[] { "diff-intent", "--project", headPath, "--project", headPath }, new[] { "diff-intent", "--project", headPath, "--intent", okIntent, "extra" },
            new[] { "diff-intent", "--project", headPath, "--edits", okIntent }, new[] { "diff-intent", "--store", refusalStore, "--intent", okIntent }, new[] { "diff-intent", headPath, okIntent },
            new[] { "diff-intent", "--project", "", "--intent", okIntent }, new[] { "diff-intent", "--project", "--intent", "--intent", okIntent },
            new[] { "diff-intent", "--project", headPath, "--intent", okIntent, "--expected-revision", "0" },
        })
            Refused("arguments: " + string.Join(" ", bad.Skip(1).Select(a => a == headPath ? "<project>" : a == okIntent ? "<intent>" : a == refusalStore ? "<store>" : a)), FailedCode("diff_intent_usage"), headPath, okIntent, new[] { refusalStore }, bad);
        Check(new ProjectStore(refusalStore).Read().Revision == 0, "after every refusal the cross-check store is still at revision 0");

        // 7. Culture: a child under de_DE prints the same bytes as the default-culture run, for a success and a refusal.
        {
            var german = false;
            try { german = CultureInfo.GetCultureInfo("de-DE").NumberFormat.NumberDecimalSeparator == ","; }
            catch (CultureNotFoundException) { }
            if (!german) Console.WriteLine("SKIP diff-intent: de-DE culture data unavailable (invariant globalization); culture check not run");
            else
            {
                var env = new Dictionary<string, string> { ["LANG"] = "de_DE.UTF-8", ["LC_ALL"] = "de_DE.UTF-8" };
                var r = CliWith(env, "diff-intent", "--project", headPath, "--intent", IntentFile("german", step1));
                Check(r.Exit == 0 && r.Out == firstOut, "under de_DE the step 1 plan prints the same bytes as under the default culture", D(r));
                var refusedIntent = baseIntent with { Openings = new[] { Window("w0", new IntentRoomSide("r0", IntentSide.South), 10.5) } };
                var refused = CliWith(env, "diff-intent", "--project", headPath, "--intent", IntentFile("german-refused", refusedIntent));
                Check(refused.Exit == 1 && refused.Out == CompileRefusal(head0, refusedIntent), "under de_DE a compiler refusal prints the same bytes as in process", D(refused));
            }
        }

        // 8. Static: reads through BoundedInput, plans through IntentEdits.FromStored and OrderOf, never writes or commits;
        //    default usage lists the verb.
        var sourcePath = Path.Combine(root, "interfaces", "cli", "DiffIntentCommand.cs");
        Check(File.Exists(sourcePath), "interfaces/cli/DiffIntentCommand.cs exists");
        if (File.Exists(sourcePath))
        {
            var source = File.ReadAllText(sourcePath);
            Check(source.Contains("BoundedInput.ReadText(", StringComparison.Ordinal) && source.Contains("IntentJson.Parse(", StringComparison.Ordinal) && source.Contains("IntentEdits.FromStored(", StringComparison.Ordinal)
                && source.Contains("IncrementalDiff.OrderOf(set.After)", StringComparison.Ordinal)
                && new[] { "File.", "FileStream", "FileMode.", "Directory.", "ProjectStore", "Commit(", "Initialize(" }.All(api => !source.Contains(api, StringComparison.Ordinal)),
                "diff-intent reads through BoundedInput, plans with IntentEdits.FromStored and OrderOf, and has no write or commit path");
        }
        Check(Cli("no-such-verb").Out.Contains("diff-intent --project project.json --intent after.json", StringComparison.Ordinal), "default usage lists diff-intent");
    }
}
