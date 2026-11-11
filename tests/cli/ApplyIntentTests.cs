using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Bim.Core;

// P3-11 `apply-intent --store <dir> --intent <after.json> [--expected-revision <n>]`: the store head's edit set toward a
// canonical layout intent (IntentEdits.FromStored), diffed incrementally with the compile order of the intent
// (IncrementalDiff.OrderOf(set.After)) and committed through apply-edits' commit path. Since P3-13 the intent parse,
// edit set, diff and refusal mapping run in the helper interfaces/cli/IntentPlan.cs shared with diff-intent
// (IntentPlan.ParseIntent, then the --expected-revision check, then IntentPlan.Build), pinned by section 9.
// Oracles, all against the real Release CLI process:
//   1. The printed "plan" is byte-identical to the in-process library plan
//      IncrementalDiff.Diff(head, FromStored(head, intent).Edits, createOrder: OrderOf(set.After)).Plan and to
//      Compiler.Diff(head, set.After); when every create sorts after the surviving elements it is also byte-identical to
//      `diff-edits --project <head> --edits <FromStored edits>`; "revision" is head + 1.
//   2. The store then reads back as compile(intent) at revision head + 1 (compile order, so a chain stays in compile
//      order), with exactly checkpoint-<head>.json and one prepared journal line added.
//   3. Every refusal prints a typed envelope, exits 1 and leaves every store file (names, bytes, mtimes) and the intent
//      file unchanged: apply_intent_usage, input_*, apply_intent_store_invalid, apply_intent_intent_invalid (reason and
//      field equal IntentJson.Parse's), apply_intent_intent_refused (refusals equal IntentCompiler.Compile's issues),
//      apply_intent_stale_head (including a replay with the same --expected-revision), apply_intent_store_unavailable,
//      apply_intent_commit_incomplete (written, ADR0007 crash case), invalid_ir, invalid_project_identity and
//      edit_bound_exceeded.
//   4. A child process under de_DE prints the same bytes as one under the default culture.
//   5. Precedence: store, then intent parse, then a stale --expected-revision, then the compile, edit set and diff
//      (a stale head is reported before a compiler refusal or a library refusal such as invalid_ir).
internal static class ApplyIntentTests
{
    static readonly Guid ProjectId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    static readonly Provenance Source = new("cli apply-intent fixture", "specified");

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
        var scratch = Path.Combine(work, "apply-intent");
        Directory.CreateDirectory(scratch);
        void Check(bool condition, string name, string detail = "") => check(condition, "apply-intent: " + name, detail);

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
        var counter = 0;
        string Input(string name, string content)
        {
            var path = Path.Combine(scratch, (++counter).ToString(CultureInfo.InvariantCulture) + "-" + name);
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
            return path;
        }
        string IntentFile(string name, LayoutIntent intent) => Input(name.Replace(' ', '-') + ".intent.json", IntentJson.Serialize(intent));
        string NewStore(Project project)
        {
            var directory = Path.Combine(scratch, "store-" + (++counter).ToString(CultureInfo.InvariantCulture));
            new ProjectStore(directory).Initialize(project);
            foreach (var file in Directory.GetFiles(directory)) File.SetLastWriteTimeUtc(file, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
            return directory;
        }
        string BaseStore() => NewStore(IntentCompiler.Compile(ProjectId, 0, Base()));
        static SortedDictionary<string, (byte[] Bytes, DateTime Time)> Snapshot(string directory)
        {
            var result = new SortedDictionary<string, (byte[], DateTime)>(StringComparer.Ordinal);
            if (!Directory.Exists(directory)) return result;
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                result[Path.GetRelativePath(directory, file)] = (File.ReadAllBytes(file), File.GetLastWriteTimeUtc(file));
            return result;
        }
        static bool Same(SortedDictionary<string, (byte[] Bytes, DateTime Time)> a, SortedDictionary<string, (byte[] Bytes, DateTime Time)> b) =>
            a.Keys.SequenceEqual(b.Keys) && a.All(kv => b[kv.Key].Bytes.AsSpan().SequenceEqual(kv.Value.Bytes) && b[kv.Key].Time == kv.Value.Time);
        static JsonElement? Json(string text)
        {
            try { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
            catch (JsonException) { return null; }
        }
        static string[] JournalLines(SortedDictionary<string, (byte[] Bytes, DateTime Time)> snapshot) =>
            snapshot.TryGetValue("journal.jsonl", out var journal) ? Encoding.UTF8.GetString(journal.Bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        static string Raw(JsonElement? element, string name) => element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) ? value.GetRawText() : "<missing " + name + ">";
        static ExecutionPlan? PlanOf(string stdout)
        {
            var raw = Raw(Json(stdout), "plan");
            try { return raw.StartsWith('{') ? JsonSerializer.Deserialize<ExecutionPlan>(raw) : null; }
            catch (JsonException) { return null; }
        }
        static string ParseRefusal(string text)
        {
            try { IntentJson.Parse(text); return "<accepted>"; }
            catch (IntentException error) { return JsonSerializer.Serialize(new { status = "failed", code = "apply_intent_intent_invalid", reason = error.Code, field = error.Field }); }
        }
        static string CompileRefusal(Project head, LayoutIntent intent)
        {
            try { IntentCompiler.Compile(head.Id, head.Revision, intent); return "<accepted>"; }
            catch (IntentException error)
            {
                return JsonSerializer.Serialize(new { status = "failed", code = "apply_intent_intent_refused", refusals = error.Issues.Select(i => new { code = i.Code, subject = i.Key, detail = i.Detail }) });
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

        // A successful apply: plan equals the library (and diff-edits when the creates sort last), revision is head + 1,
        // the store holds compile(intent) at head + 1. Returns stdout.
        string Applies(string name, string store, LayoutIntent intent, bool createsLast, string? expectedRevision = null)
        {
            var head = new ProjectStore(store).Read();
            var set = IntentEdits.FromStored(head, intent);
            var library = IncrementalDiff.Diff(head, set.Edits, createOrder: IncrementalDiff.OrderOf(set.After));
            var libraryPlan = JsonSerializer.Serialize(library.Plan);
            Check(libraryPlan == JsonSerializer.Serialize(Compiler.Diff(head, set.After)), "fixture: the ordered incremental plan equals Compiler.Diff(head, compile(intent)) (" + name + ")");
            var intentPath = IntentFile(name, intent);
            if (createsLast)
            {
                var headPath = Input(name.Replace(' ', '-') + ".head.json", JsonSerializer.Serialize(head));
                var editsPath = Input(name.Replace(' ', '-') + ".edits.json", JsonSerializer.Serialize(set.Edits));
                var diffEdits = Cli("diff-edits", "--project", headPath, "--edits", editsPath);
                Check(diffEdits.Exit == 0 && diffEdits.Out == libraryPlan, "fixture: diff-edits on the store head and the FromStored edits equals the library plan (" + name + ")", D(diffEdits));
            }
            var before = Snapshot(store);
            var intentBytes = File.ReadAllBytes(intentPath); var intentTime = File.GetLastWriteTimeUtc(intentPath);
            var arguments = new List<string> { "apply-intent", "--store", store, "--intent", intentPath };
            if (expectedRevision is not null) { arguments.Add("--expected-revision"); arguments.Add(expectedRevision); }
            var r = Cli(arguments.ToArray());
            var output = Json(r.Out);
            Check(r.Exit == 0 && r.Err.Length == 0 && output is { ValueKind: JsonValueKind.Object }, "succeeds with exit 0 and one JSON object (" + name + ")", D(r));
            Check(Raw(output, "status") == "\"succeeded\"" && Raw(output, "scope") == "\"intended_state_only\"" && Raw(output, "native_verification") == "\"unverified\"",
                "prints status succeeded, scope intended_state_only, native_verification unverified (" + name + ")", D(r));
            Check(Raw(output, "plan") == libraryPlan, "printed plan is byte-identical to the library plan and Compiler.Diff(head, compile(intent))" + (createsLast ? " and to diff-edits on the same head and edits" : "") + " (" + name + ")", D(r));
            var newRevision = (head.Revision + 1).ToString(CultureInfo.InvariantCulture);
            Check(Raw(output, "revision") == newRevision, "prints the new revision " + newRevision + " (" + name + ")", D(r));
            var committed = new ProjectStore(store).Read();
            Check(JsonSerializer.Serialize(committed) == JsonSerializer.Serialize(set.After with { Revision = head.Revision + 1 })
                && JsonSerializer.Serialize(committed) == JsonSerializer.Serialize(library.After with { Revision = head.Revision + 1 }),
                "the store head is compile(intent) in compile order at revision " + newRevision + " (" + name + ")", JsonSerializer.Serialize(committed));
            var after = Snapshot(store);
            var checkpoint = "checkpoint-" + head.Revision.ToString(CultureInfo.InvariantCulture) + ".json";
            var added = after.Keys.Except(before.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var expectedAdded = new[] { checkpoint, "journal.jsonl" }.Where(f => !before.ContainsKey(f)).Order(StringComparer.Ordinal).ToArray();
            Check(added.SequenceEqual(expectedAdded) && before.Keys.All(after.ContainsKey) && after.ContainsKey(checkpoint) && after.ContainsKey("journal.jsonl"),
                "the commit adds only " + checkpoint + " and the journal (" + name + ")", string.Join(",", after.Keys));
            var journalBefore = JournalLines(before); var journal = JournalLines(after);
            Check(journal.Length == journalBefore.Length + 1 && journal.Length > 0 && Raw(Json(journal[^1]), "plan") == libraryPlan && Raw(Json(journal[^1]), "state") == "\"prepared\"",
                "the journal gains exactly one prepared line carrying the plan (" + name + ")");
            Check(File.ReadAllBytes(intentPath).AsSpan().SequenceEqual(intentBytes) && File.GetLastWriteTimeUtc(intentPath) == intentTime, "the intent file is unchanged (" + name + ")");
            return r.Out;
        }
        // A refusal: exact stdout, exit 1, the store and the intent file unchanged.
        void Refused(string name, string expected, string store, string? intentPath, params string[] arguments)
        {
            var before = Snapshot(store);
            var watched = intentPath is not null && File.Exists(intentPath);
            var intentBytes = watched ? File.ReadAllBytes(intentPath!) : Array.Empty<byte>(); var intentTime = watched ? File.GetLastWriteTimeUtc(intentPath!) : default;
            var r = Cli(arguments.Length > 0 ? arguments : new[] { "apply-intent", "--store", store, "--intent", intentPath! });
            Check(r.Exit == 1 && r.Out == expected, "refuses " + name + " as " + expected, D(r));
            Check(Same(before, Snapshot(store)), "nothing written to the store when refusing " + name);
            if (watched) Check(File.ReadAllBytes(intentPath!).AsSpan().SequenceEqual(intentBytes) && File.GetLastWriteTimeUtc(intentPath!) == intentTime, "the intent file is unchanged when refusing " + name);
        }

        var baseIntent = Base();
        // Step 1: move w0, remove w1, add window zz (opening:zz sorts after every surviving element): updates, a delete
        // and a create that sorts last, so diff-edits on the same edits prints the same plan.
        var step1 = baseIntent with
        {
            Openings = new[] { Door("d0", new IntentSharedWall("r0", "r1"), 0.8), Window("w0", new IntentRoomSide("r0", IntentSide.South), 1.5), Window("zz", new IntentRoomSide("r0", IntentSide.West), 0.8) },
        };
        // Step 2: a third room east of r1: r1's east wall becomes a shared wall, new walls and a space are created that
        // sort before surviving spaces and openings (merged at their compile position by OrderOf).
        var step2 = step1 with { Rooms = new[] { Room("r0", 0, 0, 4, 3), Room("r1", 4, 0, 3, 3), Room("r2", 7, 0, 4, 3) } };

        // 1. A chain on one store: step 1, then step 2 with --expected-revision 1, then the same intent again (an empty
        //    plan that still commits a revision, like apply-edits with an empty edit set).
        var chain = BaseStore();
        {
            var head0 = new ProjectStore(chain).Read();
            var set1 = IntentEdits.FromStored(head0, step1);
            Check(set1.Edits.Any(e => e.Action == "update") && set1.Edits.Any(e => e.Action == "delete") && set1.Edits.Count(e => e.Action == "create") == 1,
                "fixture: step 1 has updates, a delete and one create", JsonSerializer.Serialize(set1.Edits.Select(e => e.Action)));
        }
        var firstOut = Applies("step 1 move delete add-last", chain, step1, createsLast: true, expectedRevision: "0");
        {
            var head1 = new ProjectStore(chain).Read();
            var set2 = IntentEdits.FromStored(head1, step2);
            var appended = IncrementalDiff.Diff(head1, set2.Edits);
            Check(set2.Edits.Any(e => e.Action == "create") && JsonSerializer.Serialize(appended.After.Elements) != JsonSerializer.Serialize(set2.After.Elements),
                "fixture: step 2 creates elements that sort before survivors (appending them would leave compile order)");
            var head1Path = Input("step2.head.json", JsonSerializer.Serialize(head1));
            var edits2Path = Input("step2.edits.json", JsonSerializer.Serialize(set2.Edits));
            var plan2 = PlanOf(Applies("step 2 add a room", chain, step2, createsLast: false, expectedRevision: "1"));
            // Local, not a rebuild: r1's space is updated in place and the only deletion is r1's east wall, which becomes
            // the shared wall r1|r2; every other wall, the level, the spaces and the openings of r0 and r1 survive.
            var r1East = IntentCompiler.Id(ProjectId, IntentCompiler.WallKey("r1", IntentSide.East));
            var deleted = plan2?.Operations.Where(o => o.Action == "delete").Select(o => o.Element.Id).ToArray() ?? Array.Empty<Guid>();
            Check(plan2 is not null && plan2.Operations.Any(o => o.Action == "update") && deleted.SequenceEqual(new[] { r1East }),
                "step 2 updates at least one element and deletes only r1's east wall (no rebuild of r0/r1)",
                plan2 is null ? "<no plan>" : JsonSerializer.Serialize(plan2.Operations.Select(o => o.Action + ":" + o.Element.Id)));
            // The documented deviation from diff-edits, checked: same operation set, BeforeHash, DirtyIds and identity;
            // only the operation order and AfterHash differ (diff-edits appends the creates).
            var diffEdits = Cli("diff-edits", "--project", head1Path, "--edits", edits2Path);
            var appendedPlan = diffEdits.Exit == 0 ? PlanOf("{\"plan\":" + diffEdits.Out + "}") : null;
            static string OperationSet(ExecutionPlan plan) => JsonSerializer.Serialize(plan.Operations.OrderBy(o => o.Element.Id).ThenBy(o => o.Action, StringComparer.Ordinal).ToArray());
            Check(plan2 is not null && appendedPlan is not null && OperationSet(plan2) == OperationSet(appendedPlan) && plan2.BeforeHash == appendedPlan.BeforeHash
                && plan2.DirtyIds.SequenceEqual(appendedPlan.DirtyIds) && plan2.ProjectId == appendedPlan.ProjectId && plan2.ExpectedRevision == appendedPlan.ExpectedRevision,
                "step 2: diff-edits on the same head and edits has the same operation set, BeforeHash, DirtyIds and identity", D(diffEdits));
            Check(plan2 is not null && appendedPlan is not null && JsonSerializer.Serialize(plan2.Operations) != JsonSerializer.Serialize(appendedPlan.Operations) && plan2.AfterHash != appendedPlan.AfterHash,
                "step 2: only the operation order and AfterHash differ from diff-edits (creates at compile position vs appended)", D(diffEdits));
        }
        var noOp = PlanOf(Applies("step 2 again (no change)", chain, step2, createsLast: true));
        Check(noOp is not null && noOp.Operations.Length == 0 && noOp.DirtyIds.Length == 0 && noOp.BeforeHash == noOp.AfterHash,
            "the no-change apply prints an empty plan: no operations, no dirty ids, BeforeHash equals AfterHash", noOp is null ? "<no plan>" : JsonSerializer.Serialize(noOp));
        Check(new ProjectStore(chain).Read().Revision == 3, "three applies advance the store to revision 3");

        // 2. Replay and stale expected head: refused typed, nothing written (the chain store is at revision 3).
        var step2Path = IntentFile("step2-replay", step2);
        Refused("a replay of the step 2 commit (--expected-revision 1)", FailedCode("apply_intent_stale_head"), chain, step2Path, "apply-intent", "--store", chain, "--intent", step2Path, "--expected-revision", "1");
        foreach (var stale in new[] { "0", "2", "4", "99" })
            Refused("expected revision " + stale + " against head 3", FailedCode("apply_intent_stale_head"), chain, step2Path, "apply-intent", "--store", chain, "--intent", step2Path, "--expected-revision", stale);
        Refused("expected revision before --store", FailedCode("apply_intent_stale_head"), chain, step2Path, "apply-intent", "--expected-revision", "2", "--intent", step2Path, "--store", chain);
        // The store is busy (another holder of its commit lock).
        {
            var before = Snapshot(chain);
            (int Exit, string Out, string Err) busy;
            using (new FileStream(Path.Combine(chain, "lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                busy = Cli("apply-intent", "--store", chain, "--intent", step2Path);
            Check(busy.Exit == 1 && busy.Out == FailedCode("apply_intent_store_unavailable"), "refuses a store whose commit lock is held as apply_intent_store_unavailable", D(busy));
            Check(Same(before, Snapshot(chain)), "nothing written to the store when its lock is held");
        }
        // A commit failing after it started writing (project.tmp is a directory): apply_intent_commit_incomplete, with
        // project.json still the old head and the checkpoint plus one prepared journal line written.
        {
            var broken = BaseStore();
            Directory.CreateDirectory(Path.Combine(broken, "project.tmp"));
            var path = IntentFile("broken", step1);
            var before = Snapshot(broken);
            var r = Cli("apply-intent", "--store", broken, "--intent", path);
            Check(r.Exit == 1 && r.Out == FailedCode("apply_intent_commit_incomplete"), "a commit failing after it started writing is apply_intent_commit_incomplete", D(r));
            var after = Snapshot(broken);
            Check(after["project.json"].Bytes.AsSpan().SequenceEqual(before["project.json"].Bytes) && after.Keys.Except(before.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).SequenceEqual(new[] { "checkpoint-0.json", "journal.jsonl" }),
                "commit_incomplete: project.json still holds the old head; only the checkpoint and the journal were added", string.Join(",", after.Keys));
            Check(r.Err.Contains("apply-intent commit incomplete", StringComparison.Ordinal), "commit_incomplete writes the underlying exception to stderr", r.Err);
        }
        // Still committable afterwards with the right head.
        Applies("after the refusals, back to the base intent with --expected-revision 3", chain, baseIntent, createsLast: false, expectedRevision: "3");

        // 3. Malformed intents: apply_intent_intent_invalid with IntentJson.Parse's reason and field, nothing written.
        var store = BaseStore();
        var storeHead = new ProjectStore(store).Read();
        var canonical = IntentJson.Serialize(baseIntent);
        foreach (var (label, text) in new[]
        {
            ("not JSON", "{"), ("JSON null", "null"), ("an array", "[]"), ("empty file", ""), ("trailing content", canonical + " {}"),
            ("version 2", canonical.Replace("\"intent_version\":1", "\"intent_version\":2", StringComparison.Ordinal)),
            ("an unknown member", canonical.Replace("\"levels\":", "\"levelz\":[],\"levels\":", StringComparison.Ordinal)),
            ("a duplicate member", canonical.Replace("\"levels\":", "\"openings\":[],\"levels\":", StringComparison.Ordinal)),
            ("a missing member", canonical.Replace("\"name\":\"Ground\",", "", StringComparison.Ordinal)),
            ("a string where a number is required", canonical.Replace("\"thickness_m\":0.2", "\"thickness_m\":\"0.2\"", StringComparison.Ordinal)),
            ("an unknown opening kind", canonical.Replace("\"kind\":\"door\"", "\"kind\":\"hatch\"", StringComparison.Ordinal)),
            ("an element edits array instead of an intent", "[{\"Action\":\"delete\",\"Id\":\"00000000-0000-0000-0000-000000000003\"}]"),
            ("more than 1048576 characters", canonical + new string(' ', IntentJson.MaxInputChars + 1 - canonical.Length)),
        })
        {
            var expected = ParseRefusal(text);
            Check(expected != "<accepted>", "fixture: IntentJson.Parse refuses an intent that is " + label, expected);
            Refused("an intent that is " + label, expected, store, Input("bad-" + label.Replace(' ', '-') + ".intent.json", text));
        }
        Check(ParseRefusal(canonical + new string(' ', IntentJson.MaxInputChars + 1 - canonical.Length)).Contains("\"reason\":\"input_too_large\"", StringComparison.Ordinal),
            "fixture: an intent over IntentJson.MaxInputChars is refused with reason input_too_large");

        // 4. Intents the compiler refuses: apply_intent_intent_refused carrying IntentCompiler.Compile's issues.
        foreach (var (label, intent, code) in new (string, LayoutIntent, string)[]
        {
            ("overlapping rooms", baseIntent with { Rooms = new[] { Room("r0", 0, 0, 4, 3), Room("r1", 3, 0, 3, 3) }, Openings = Array.Empty<IntentOpening>() }, IntentCodes.RoomOverlap),
            ("a room on an unknown level", baseIntent with { Rooms = new[] { Room("r0", 0, 0, 4, 3), Room("r1", 4, 0, 3, 3, "l9") } }, IntentCodes.UnknownLevel),
            ("a window outside its wall", baseIntent with { Openings = new[] { Window("w0", new IntentRoomSide("r0", IntentSide.South), 10) } }, IntentCodes.OpeningOutsideWall),
            ("a door between rooms that are not adjacent", baseIntent with { Rooms = new[] { Room("r0", 0, 0, 4, 3), Room("r1", 10, 0, 3, 3) }, Openings = new[] { Door("d0", new IntentSharedWall("r0", "r1"), 0.8) } }, IntentCodes.NotAdjacent),
        })
        {
            var expected = CompileRefusal(storeHead, intent);
            Check(expected.Contains("\"code\":\"" + code + "\"", StringComparison.Ordinal), "fixture: IntentCompiler refuses " + label + " with " + code, expected);
            Refused("an intent with " + label, expected, store, IntentFile("refused-" + label, intent));
        }

        // 5. Library refusals on the store or the edit set: the library's codes, nothing written.
        {
            // A store head that is not Compiler.Validate-clean (a dangling dependency): invalid_ir from FromStored.
            var dangling = BaseStore();
            var level = storeHead.Elements[0];
            var invalidHead = storeHead with { Elements = storeHead.Elements.Append(new Element(Guid.Parse("00000000-0000-0000-0000-0000000000f1"), "plan", Array.Empty<double>(), new[] { Guid.Parse("00000000-0000-0000-0000-0000000000f2"), level.Id }, Source)).ToArray() };
            File.WriteAllText(Path.Combine(dangling, "project.json"), JsonSerializer.Serialize(new ProjectStore.State(invalidHead, new())));
            Check(LibraryRefusal(invalidHead, baseIntent) == "invalid_ir", "fixture: FromStored refuses a head with a dangling dependency as invalid_ir", LibraryRefusal(invalidHead, baseIntent));
            Refused("a store head with a dangling dependency", FailedCode("invalid_ir"), dangling, IntentFile("on-dangling", baseIntent));
            // A store whose project id is empty: the compiler's invalid_project_identity.
            var anonymous = NewStore(new Project(Guid.Empty, 0, Array.Empty<Element>()));
            Check(LibraryRefusal(new Project(Guid.Empty, 0, Array.Empty<Element>()), baseIntent) == "invalid_project_identity", "fixture: FromStored refuses an empty project id as invalid_project_identity");
            Refused("a store whose project id is empty", FailedCode("invalid_project_identity"), anonymous, IntentFile("on-anonymous", baseIntent));
            // More than IncrementalDiff.MaxEdits edits: 205 isolated rooms on an empty head are 1 + 205 * 5 = 1026 creates.
            var empty = new Project(ProjectId, 0, Array.Empty<Element>());
            var many = Intent(Enumerable.Range(0, 205).Select(i => Room("r" + i.ToString("000", CultureInfo.InvariantCulture), i * 10, 0, 4, 3)).ToArray(), Array.Empty<IntentOpening>());
            Check(LibraryRefusal(empty, many) == IncrementalDiff.BoundExceeded, "fixture: 205 new rooms exceed IncrementalDiff.MaxEdits", LibraryRefusal(empty, many));
            Refused("an intent needing more than IncrementalDiff.MaxEdits edits", FailedCode(IncrementalDiff.BoundExceeded), NewStore(empty), IntentFile("too-many", many));
            // An intent from an empty store within the bound commits (204 rooms: 1021 creates).
            var fewer = many with { Rooms = many.Rooms.Take(204).ToArray() };
            Applies("204 new rooms on an empty head", NewStore(empty), fewer, createsLast: true, expectedRevision: "0");
        }

        // 6. Bounded and missing inputs: BoundedInput's codes, then the store's shape; nothing written.
        var okIntent = IntentFile("ok", step1);
        var overPath = Path.Combine(scratch, "over-cap.intent.json"); File.WriteAllBytes(overPath, overCap);
        Refused("an intent over 8 MiB", FailedCode("input_too_large"), store, overPath);
        Refused("a missing intent file", FailedCode("input_not_found"), store, Path.Combine(scratch, "absent.intent.json"));
        var aDirectory = Path.Combine(scratch, "a-directory.json"); Directory.CreateDirectory(aDirectory);
        Refused("a directory as intent", FailedCode("input_not_regular_file"), store, aDirectory);
        var noStore = Path.Combine(scratch, "no-store");
        Refused("a missing store", FailedCode("input_not_found"), noStore, okIntent);
        Check(!Directory.Exists(noStore), "a missing store directory is not created");
        var emptyStore = Path.Combine(scratch, "empty-store"); Directory.CreateDirectory(emptyStore);
        Refused("an uninitialized store directory", FailedCode("input_not_found"), emptyStore, okIntent);
        foreach (var (label, bytes) in new (string, byte[])[]
        {
            ("not JSON", Encoding.UTF8.GetBytes("{")),
            ("a bare project, not a store state", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(storeHead))),
            ("a state with null elements", Encoding.UTF8.GetBytes("{\"Project\":{\"Id\":\"00000000-0000-0000-0000-0000000000a1\",\"Revision\":0,\"Elements\":null},\"Receipts\":{}}")),
            ("a state with null receipts", Encoding.UTF8.GetBytes("{\"Project\":" + JsonSerializer.Serialize(storeHead) + ",\"Receipts\":null}")),
        })
        {
            var bad = BaseStore();
            File.WriteAllBytes(Path.Combine(bad, "project.json"), bytes);
            Refused("a store whose project.json is " + label, FailedCode("apply_intent_store_invalid"), bad, okIntent);
        }
        var overStore = BaseStore();
        File.WriteAllBytes(Path.Combine(overStore, "project.json"), overCap);
        Refused("a store whose project.json is over 8 MiB", FailedCode("input_too_large"), overStore, okIntent);
        // Precedence: a malformed store is reported before a malformed intent; a malformed intent before a stale head.
        var malformedStore = BaseStore();
        File.WriteAllBytes(Path.Combine(malformedStore, "project.json"), Encoding.UTF8.GetBytes("{"));
        Refused("a malformed store and a malformed intent", FailedCode("apply_intent_store_invalid"), malformedStore, Input("bad-and-bad.intent.json", "{"));
        var badAndStale = Input("bad-and-stale.intent.json", "{");
        Refused("a malformed intent and a stale head", ParseRefusal("{"), store, badAndStale, "apply-intent", "--store", store, "--intent", badAndStale, "--expected-revision", "7");
        // A stale head before the compile: a compiler-refused intent and a library-refused (invalid_ir) store head are
        // both reported as apply_intent_stale_head when --expected-revision does not match (the store is at revision 0).
        var overlapping = baseIntent with { Rooms = new[] { Room("r0", 0, 0, 4, 3), Room("r1", 3, 0, 3, 3) }, Openings = Array.Empty<IntentOpening>() };
        var refusedAndStale = IntentFile("refused-and-stale", overlapping);
        Check(CompileRefusal(storeHead, overlapping).Contains("\"code\":\"" + IntentCodes.RoomOverlap + "\"", StringComparison.Ordinal), "fixture: the stale-precedence intent is compiler-refused (room overlap)", CompileRefusal(storeHead, overlapping));
        Refused("a compiler-refused intent and a stale head", FailedCode("apply_intent_stale_head"), store, refusedAndStale, "apply-intent", "--store", store, "--intent", refusedAndStale, "--expected-revision", "7");
        {
            var danglingStale = BaseStore();
            var level = storeHead.Elements[0];
            var invalidHead = storeHead with { Elements = storeHead.Elements.Append(new Element(Guid.Parse("00000000-0000-0000-0000-0000000000f1"), "plan", Array.Empty<double>(), new[] { Guid.Parse("00000000-0000-0000-0000-0000000000f2"), level.Id }, Source)).ToArray() };
            File.WriteAllText(Path.Combine(danglingStale, "project.json"), JsonSerializer.Serialize(new ProjectStore.State(invalidHead, new())));
            Check(LibraryRefusal(invalidHead, baseIntent) == "invalid_ir", "fixture: the stale-precedence store head is refused as invalid_ir", LibraryRefusal(invalidHead, baseIntent));
            var onDanglingStale = IntentFile("on-dangling-stale", baseIntent);
            Refused("an invalid_ir store head and a stale head", FailedCode("apply_intent_stale_head"), danglingStale, onDanglingStale, "apply-intent", "--store", danglingStale, "--intent", onDanglingStale, "--expected-revision", "7");
        }

        // 7. Usage: --store and --intent once each, optional --expected-revision once (a non-negative decimal integer),
        //    nothing else; checked before any file is read.
        foreach (var bad in new[]
        {
            new[] { "apply-intent" }, new[] { "apply-intent", "--store", store }, new[] { "apply-intent", "--intent", okIntent }, new[] { "apply-intent", "--store", store, "--intent" },
            new[] { "apply-intent", "--store", store, "--store", store }, new[] { "apply-intent", "--store", store, "--intent", okIntent, "extra" },
            new[] { "apply-intent", "--store", store, "--edits", okIntent }, new[] { "apply-intent", store, okIntent },
            new[] { "apply-intent", "--store", "", "--intent", okIntent }, new[] { "apply-intent", "--store", "--intent", "--intent", okIntent },
            new[] { "apply-intent", "--store", store, "--intent", okIntent, "--expected-revision" },
            new[] { "apply-intent", "--store", store, "--intent", okIntent, "--expected-revision", "abc" },
            new[] { "apply-intent", "--store", store, "--intent", okIntent, "--expected-revision", "-1" },
            new[] { "apply-intent", "--store", store, "--intent", okIntent, "--expected-revision", "+0" },
            new[] { "apply-intent", "--store", store, "--intent", okIntent, "--expected-revision", "99999999999" },
            new[] { "apply-intent", "--store", store, "--intent", okIntent, "--expected-revision", "0", "--expected-revision", "0" },
        })
            Refused("arguments: " + string.Join(" ", bad.Skip(1).Select(a => a == store ? "<store>" : a == okIntent ? "<intent>" : a)), FailedCode("apply_intent_usage"), store, okIntent, bad);
        Check(new ProjectStore(store).Read().Revision == 0, "after every refusal the refused store is still at revision 0");

        // 8. Culture: a child under de_DE prints the same bytes as the default-culture run, for a success and a refusal.
        {
            var german = false;
            try { german = CultureInfo.GetCultureInfo("de-DE").NumberFormat.NumberDecimalSeparator == ","; }
            catch (CultureNotFoundException) { }
            if (!german) Console.WriteLine("SKIP apply-intent: de-DE culture data unavailable (invariant globalization); culture check not run");
            else
            {
                var env = new Dictionary<string, string> { ["LANG"] = "de_DE.UTF-8", ["LC_ALL"] = "de_DE.UTF-8" };
                var germanStore = BaseStore();
                var r = CliWith(env, "apply-intent", "--store", germanStore, "--intent", IntentFile("german", step1), "--expected-revision", "0");
                Check(r.Exit == 0 && r.Out == firstOut, "under de_DE the step 1 apply prints the same bytes as under the default culture", D(r));
                var refusedIntent = baseIntent with { Openings = new[] { Window("w0", new IntentRoomSide("r0", IntentSide.South), 10.5) } };
                var refused = CliWith(env, "apply-intent", "--store", store, "--intent", IntentFile("german-refused", refusedIntent));
                Check(refused.Exit == 1 && refused.Out == CompileRefusal(storeHead, refusedIntent), "under de_DE a compiler refusal prints the same bytes as in process", D(refused));
            }
        }

        // 9. Static: reads through BoundedInput, commits through the commit path shared with apply-edits, and builds its
        //    plan only through the shared helper interfaces/cli/IntentPlan.cs (P3-13), the one place that parses the intent,
        //    calls IntentEdits.FromStored, diffs with OrderOf placement and maps the typed refusals, so apply-intent's plan
        //    and diff-intent's stdout are the same code path. Comments and string literals are ignored (CodeOf below).
        static string Code(string text) => CodeOf(text);
        var planSteps = new[] { "IntentJson.Parse(", "IntentEdits.FromStored(", "IncrementalDiff.Diff(", "IncrementalDiff.OrderOf(", "IntentException" };
        var sourcePath = Path.Combine(root, "interfaces", "cli", "ApplyIntentCommand.cs");
        Check(File.Exists(sourcePath), "interfaces/cli/ApplyIntentCommand.cs exists");
        if (File.Exists(sourcePath))
        {
            var source = Code(File.ReadAllText(sourcePath));
            Check(source.Contains("BoundedInput.ReadText(", StringComparison.Ordinal) && source.Contains("ApplyEditsCommand.Commit(", StringComparison.Ordinal) && source.Contains("ApplyEditsCommand.ParseHead(", StringComparison.Ordinal)
                && !source.Contains("Compiler.Diff", StringComparison.Ordinal) && !source.Contains("new ProjectStore", StringComparison.Ordinal)
                && new[] { "File.", "FileStream", "FileMode.", "Directory.", "Initialize(" }.All(api => !source.Contains(api, StringComparison.Ordinal)),
                "apply-intent reads through BoundedInput and commits only through apply-edits' shared commit path");
            var parseCall = "IntentPlan.ParseIntent(intentText, Verb, IntentInvalid)";
            var staleCheck = "expected != head.Revision";
            var buildCall = "IntentPlan.Build(head, intent, Verb, IntentRefused)";
            var commitCall = "ApplyEditsCommand.Commit(storePath, result, Verb)";
            var missing = new[] { parseCall, buildCall }.Where(call => !source.Contains(call, StringComparison.Ordinal)).ToArray();
            var inlined = planSteps.Where(step => source.Contains(step, StringComparison.Ordinal)).ToArray();
            Check(missing.Length == 0 && inlined.Length == 0, "apply-intent builds its plan only through the shared IntentPlan helper",
                "missing helper calls [" + string.Join(", ", missing) + "]; plan steps still inline in ApplyIntentCommand.cs [" + string.Join(", ", inlined) + "]");
            var order = new[] { parseCall, staleCheck, buildCall, commitCall }.Select(step => (Step: step, At: source.IndexOf(step, StringComparison.Ordinal))).ToArray();
            Check(order.All(o => o.At >= 0) && order.Zip(order.Skip(1)).All(pair => pair.First.At < pair.Second.At),
                "apply-intent runs IntentPlan.ParseIntent, then the --expected-revision check, then IntentPlan.Build, then the commit",
                string.Join("; ", order.Select(o => o.Step + " at " + o.At)));
        }
        var helperPath = Path.Combine(root, "interfaces", "cli", "IntentPlan.cs");
        Check(File.Exists(helperPath), "interfaces/cli/IntentPlan.cs (the shared apply-intent/diff-intent plan helper) exists", helperPath);
        if (File.Exists(helperPath))
        {
            var helperText = File.ReadAllText(helperPath);
            var helper = Code(helperText);
            var absent = planSteps.Concat(new[] { "IncrementalDiff.OrderOf(set.After)", "EditsInput.LibraryCodes", "IncrementalDiff.BoundExceeded", "ApplyEditsCommand.Internal(" })
                .Where(step => !helper.Contains(step, StringComparison.Ordinal))
                .Concat(CodeOf(helperText, blankStrings: false).Contains("== \"invalid_ir\")", StringComparison.Ordinal) ? Array.Empty<string>() : new[] { "== \"invalid_ir\")" }).ToArray();
            var writes = new[] { "File.", "FileStream", "FileMode.", "Directory.", "ProjectStore", "Commit(", "Initialize(", "BoundedInput" }.Where(api => helper.Contains(api, StringComparison.Ordinal)).ToArray();
            Check(absent.Length == 0 && writes.Length == 0, "IntentPlan parses the intent, calls FromStored, diffs with OrderOf(set.After) and maps every typed refusal, with no file, store or commit access",
                "missing [" + string.Join(", ", absent) + "]; forbidden [" + string.Join(", ", writes) + "]");
        }
        // The helper is the only interfaces/cli source whose code (not comments) runs the plan steps.
        var cliDir = Path.Combine(root, "interfaces", "cli");
        var owners = Directory.GetFiles(cliDir, "*.cs").Where(f => Path.GetFileName(f) != "IntentPlan.cs")
            .Where(f => { var code = Code(File.ReadAllText(f)); return code.Contains("IntentEdits.FromStored(", StringComparison.Ordinal) || code.Contains("IncrementalDiff.OrderOf(", StringComparison.Ordinal); })
            .Select(f => Path.GetFileName(f)).ToArray();
        Check(owners.Length == 0, "no interfaces/cli source other than IntentPlan.cs calls IntentEdits.FromStored or IncrementalDiff.OrderOf", "found in [" + string.Join(", ", owners) + "]");
        Check(Cli("no-such-verb").Out.Contains("apply-intent --store store-directory --intent after.json [--expected-revision n]", StringComparison.Ordinal), "default usage lists apply-intent");
    }

    // The C# source with every comment (whole-line, trailing and block) removed and, unless blankStrings is false, every
    // string and char literal emptied, so a static check can match only code: a library call named in prose or inside a
    // string does not count. Handles regular, verbatim (@"", with "" escapes) and char literals; the checked files use
    // no raw or interpolated literals with holes.
    internal static string CodeOf(string text, bool blankStrings = true)
    {
        var output = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/') { while (i < text.Length && text[i] != '\n') i++; output.Append('\n'); continue; }
            if (c == '/' && next == '*') { var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal); i = close < 0 ? text.Length : close + 1; output.Append(' '); continue; }
            if (c == '"' || c == '\'' || (c == '@' && next == '"'))
            {
                var start = i;
                var verbatim = c == '@';
                if (verbatim) i++;
                var quote = text[i];
                i++;
                while (i < text.Length)
                {
                    if (!verbatim && text[i] == '\\') { i += 2; continue; }
                    if (text[i] == quote)
                    {
                        if (verbatim && i + 1 < text.Length && text[i + 1] == '"') { i += 2; continue; }
                        break;
                    }
                    i++;
                }
                var last = Math.Min(i, text.Length - 1);
                if (blankStrings) output.Append(quote).Append(quote);
                else output.Append(text, start, last - start + 1);
                i = last;
                continue;
            }
            output.Append(c);
        }
        return output.ToString();
    }
}
