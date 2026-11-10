using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Bim.Core;

// P3-08 `apply-edits --store <dir> --edits <e> [--expected-revision <n>]`: the P3-05 incremental diff committed through
// ProjectStore.Commit with the plan's expected head (revision and BeforeHash), printing the plan and the new revision.
// Oracles, all against the real Release CLI process:
//   1. The printed "plan" is byte-identical to `diff-edits --project <store head> --edits <same file>` and to
//      JsonSerializer.Serialize(IncrementalDiff.Diff(head, edits).Plan) in process; "revision" is head + 1.
//   2. The store then reads back as the library's result.After at revision head + 1, with exactly checkpoint-<head>.json
//      and the journal line added; a chain of applies advances one revision each.
//   3. Every refusal (usage, stale expected head, store busy, malformed/oversized/missing inputs, library refusals)
//      prints exactly {"status":"failed","code":<code>}, exits 1 and leaves every store file (names, bytes, mtimes) and
//      the edits file unchanged. Library refusals carry the same code as diff-edits on the same inputs.
internal static class ApplyEditsTests
{
    public static void Run(string root, string work, string dotnet, string cliDll, byte[] overCap, Action<bool, string, string> check)
    {
        var scratch = Path.Combine(work, "apply-edits");
        Directory.CreateDirectory(scratch);
        void Check(bool condition, string name, string detail = "") => check(condition, "apply-edits: " + name, detail);

        (int Exit, string Out, string Err) Cli(params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
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
        string NewStore(Project project)
        {
            var directory = Path.Combine(scratch, "store-" + (++counter).ToString(CultureInfo.InvariantCulture));
            new ProjectStore(directory).Initialize(project);
            foreach (var file in Directory.GetFiles(directory)) File.SetLastWriteTimeUtc(file, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
            return directory;
        }
        // Every file under the store, by relative path: bytes and mtime.
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
        string LibraryCode(Project project, IReadOnlyList<ElementEdit> edits)
        {
            try { IncrementalDiff.Diff(project, edits); return "succeeded"; }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return e.Message; }
        }

        // A successful apply: plan equals diff-edits and the library, revision is head + 1, the store holds result.After.
        void Applies(string name, string store, IReadOnlyList<ElementEdit> edits, string? expectedRevision = null)
        {
            var head = new ProjectStore(store).Read();
            var headPath = Input(name.Replace(' ', '-') + ".head.json", JsonSerializer.Serialize(head));
            var editsPath = Input(name.Replace(' ', '-') + ".edits.json", JsonSerializer.Serialize(edits));
            var library = IncrementalDiff.Diff(head, edits);
            var libraryPlan = JsonSerializer.Serialize(library.Plan);
            var diffEdits = Cli("diff-edits", "--project", headPath, "--edits", editsPath);
            Check(diffEdits.Exit == 0 && diffEdits.Out == libraryPlan, "fixture: diff-edits on the store head equals the library plan (" + name + ")", D(diffEdits));
            var before = Snapshot(store);
            var editsBytes = File.ReadAllBytes(editsPath); var editsTime = File.GetLastWriteTimeUtc(editsPath);
            var arguments = new List<string> { "apply-edits", "--store", store, "--edits", editsPath };
            if (expectedRevision is not null) { arguments.Add("--expected-revision"); arguments.Add(expectedRevision); }
            var r = Cli(arguments.ToArray());
            var output = Json(r.Out);
            Check(r.Exit == 0 && r.Err.Length == 0 && output is { ValueKind: JsonValueKind.Object }, "succeeds with exit 0 and one JSON object (" + name + ")", D(r));
            Check(Raw(output, "status") == "\"succeeded\"" && Raw(output, "scope") == "\"intended_state_only\"" && Raw(output, "native_verification") == "\"unverified\"",
                "prints status succeeded, scope intended_state_only, native_verification unverified (" + name + ")", D(r));
            Check(Raw(output, "plan") == diffEdits.Out && Raw(output, "plan") == libraryPlan, "printed plan is byte-identical to diff-edits on the same inputs and to the library plan (" + name + ")", D(r));
            var newRevision = (head.Revision + 1).ToString(CultureInfo.InvariantCulture);
            Check(Raw(output, "revision") == newRevision, "prints the new revision " + newRevision + " (" + name + ")", D(r));
            var committed = new ProjectStore(store).Read();
            Check(JsonSerializer.Serialize(committed) == JsonSerializer.Serialize(library.After with { Revision = head.Revision + 1 }),
                "the store head is the library's result.After at revision " + newRevision + " (" + name + ")", JsonSerializer.Serialize(committed));
            var after = Snapshot(store);
            var checkpoint = "checkpoint-" + head.Revision.ToString(CultureInfo.InvariantCulture) + ".json";
            var added = after.Keys.Except(before.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var expectedAdded = new[] { checkpoint, "journal.jsonl" }.Where(f => !before.ContainsKey(f)).Order(StringComparer.Ordinal).ToArray();
            Check(added.SequenceEqual(expectedAdded) && before.Keys.All(after.ContainsKey) && after.ContainsKey(checkpoint) && after.ContainsKey("journal.jsonl"),
                "the commit adds only " + checkpoint + " and the journal (" + name + ")", string.Join(",", after.Keys));
            var journalBefore = JournalLines(before); var journal = JournalLines(after);
            Check(journal.Length == journalBefore.Length + 1 && journal.Length > 0 && Raw(Json(journal[^1]), "plan") == libraryPlan && Raw(Json(journal[^1]), "state") == "\"prepared\"",
                "the journal gains exactly one prepared line carrying the plan (" + name + ")", journalBefore.Length.ToString(CultureInfo.InvariantCulture) + " -> " + journal.Length.ToString(CultureInfo.InvariantCulture));
            Check(File.ReadAllBytes(editsPath).AsSpan().SequenceEqual(editsBytes) && File.GetLastWriteTimeUtc(editsPath) == editsTime, "the edits file is unchanged (" + name + ")");
        }
        // A refusal: exact envelope, exit 1, the store and the edits file unchanged.
        void Refused(string name, string code, string store, string? editsPath, params string[] arguments)
        {
            var before = Snapshot(store);
            var watched = editsPath is not null && File.Exists(editsPath);
            var editsBytes = watched ? File.ReadAllBytes(editsPath!) : Array.Empty<byte>(); var editsTime = watched ? File.GetLastWriteTimeUtc(editsPath!) : default;
            var r = Cli(arguments.Length > 0 ? arguments : new[] { "apply-edits", "--store", store, "--edits", editsPath! });
            Check(r.Exit == 1 && r.Out == FailedCode(code), "refuses " + name + " as " + code, D(r));
            Check(Same(before, Snapshot(store)), "nothing written to the store when refusing " + name);
            if (watched) Check(File.ReadAllBytes(editsPath!).AsSpan().SequenceEqual(editsBytes) && File.GetLastWriteTimeUtc(editsPath!) == editsTime, "the edits file is unchanged when refusing " + name);
        }

        var baseProject = JsonSerializer.Deserialize<Project>(File.ReadAllText(Path.Combine(root, "examples", "before.json")))!;
        var widened = JsonSerializer.Deserialize<Project>(File.ReadAllText(Path.Combine(root, "examples", "after.json")))!.Elements[0];
        var wall = baseProject.Elements[0]; var planDoc = baseProject.Elements[1];
        static Guid G(int n) => Guid.Parse("00000000-0000-0000-0000-" + n.ToString("x12", CultureInfo.InvariantCulture));
        static Element Wall(Guid id, double x, Guid[]? dependencies = null) => new(id, "wall", new[] { x, 0, x + 5, 0, 3, 0.2 }, dependencies ?? Array.Empty<Guid>(), new("cli apply-edits fixture", "specified"));
        static Element PlanDoc(Guid id, params Guid[] dependencies) => new(id, "plan", Array.Empty<double>(), dependencies, new("cli apply-edits fixture", "proposed"));

        // 1. A chain on one store: the examples' wall edit, then mixed create/update/delete with the right expected head,
        //    then an empty edit set; each prints diff-edits' plan and advances one revision.
        var chain = NewStore(baseProject);
        Applies("examples wall update", chain, new[] { ElementEdit.Update(widened) });
        Applies("mixed create update delete with --expected-revision 1", chain,
            new[] { ElementEdit.Create(Wall(G(0x10), 20)), ElementEdit.Delete(planDoc.Id), ElementEdit.Update(wall with { GeometryMetres = new[] { 0, 0, 6, 0, 3, 0.25 } }), ElementEdit.Create(PlanDoc(G(0x11), G(0x10), wall.Id)) }, "1");
        Applies("empty edit set", chain, Array.Empty<ElementEdit>(), "2");
        Check(new ProjectStore(chain).Read().Revision == 3, "three applies advance the store to revision 3");
        // A larger project, edits far apart.
        var big = new List<Element>();
        for (var i = 0; i < 300; i++) { big.Add(Wall(G(0x1000 + i), i * 10)); big.Add(PlanDoc(G(0x2000 + i), G(0x1000 + i))); }
        Applies("300-wall project", NewStore(new Project(baseProject.Id, 0, big.ToArray())),
            new[] { ElementEdit.Update(Wall(G(0x1000 + 5), 51)), ElementEdit.Delete(G(0x2000 + 150)), ElementEdit.Delete(G(0x1000 + 150)), ElementEdit.Create(PlanDoc(G(0x3000), G(0x1000 + 299), G(0x1000))) });

        // 2. Stale expected head: refused typed, nothing written (the chain store is at revision 3).
        var widenedEdits = Input("widened.edits.json", JsonSerializer.Serialize(new[] { ElementEdit.Update(widened with { GeometryMetres = new[] { 0, 0, 7, 0, 3, 0.2 } }) }));
        foreach (var stale in new[] { "0", "2", "4", "99" })
            Refused("expected revision " + stale + " against head 3", "apply_edits_stale_head", chain, widenedEdits, "apply-edits", "--store", chain, "--edits", widenedEdits, "--expected-revision", stale);
        Refused("expected revision before --store", "apply_edits_stale_head", chain, widenedEdits, "apply-edits", "--expected-revision", "1", "--edits", widenedEdits, "--store", chain);
        // The store is busy (another holder of its commit lock): refused typed, nothing written.
        {
            var before = Snapshot(chain);
            (int Exit, string Out, string Err) busy;
            using (new FileStream(Path.Combine(chain, "lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                busy = Cli("apply-edits", "--store", chain, "--edits", widenedEdits);
            Check(busy.Exit == 1 && busy.Out == FailedCode("apply_edits_store_unavailable"), "refuses a store whose commit lock is held as apply_edits_store_unavailable", D(busy));
            Check(Same(before, Snapshot(chain)), "nothing written to the store when its lock is held");
        }
        // Racing runs planned against one head (--expected-revision 0) with the same edits: each run's idempotency key is
        // its own, so exactly one commits and every other is refused (stale head once the winner landed, whether it read
        // the head before or after, store unavailable while the winner holds the lock); none reports the winner's commit
        // as its own. The store ends at head + 1 with one new journal line and one receipt. (Without --expected-revision a
        // run that reads the head after the winner landed legitimately applies its edits on that new head.)
        {
            var raced = NewStore(baseProject);
            var racedEdits = Input("raced.edits.json", JsonSerializer.Serialize(new[] { ElementEdit.Update(widened) }));
            var racedPlan = JsonSerializer.Serialize(IncrementalDiff.Diff(baseProject, new[] { ElementEdit.Update(widened) }).Plan);
            var processes = new List<(Process Process, Task<string> Out)>();
            for (var i = 0; i < 8; i++)
            {
                var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
                foreach (var argument in new[] { cliDll, "apply-edits", "--store", raced, "--edits", racedEdits, "--expected-revision", "0" }) start.ArgumentList.Add(argument);
                var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
                _ = process.StandardError.ReadToEndAsync();
                processes.Add((process, process.StandardOutput.ReadToEndAsync()));
            }
            var results = new List<(int Exit, string Out)>();
            foreach (var (process, output) in processes)
            {
                if (!process.WaitForExit(120000)) { process.Kill(true); results.Add((-1, "<timed out>")); continue; }
                process.WaitForExit();
                results.Add((process.ExitCode, output.Result.TrimEnd('\r', '\n')));
                process.Dispose();
            }
            var winners = results.Where(r => r.Exit == 0).ToArray();
            var losers = results.Where(r => r.Exit != 0).ToArray();
            var detail = string.Join(" ; ", results.Select(r => r.Exit.ToString(CultureInfo.InvariantCulture) + " " + (r.Out.Length > 120 ? r.Out[..120] : r.Out)));
            Check(winners.Length == 1 && Raw(Json(winners[0].Out), "revision") == "1" && Raw(Json(winners[0].Out), "plan") == racedPlan, "8 racing runs on one head: exactly one succeeds, at revision 1 with the plan", detail);
            Check(losers.Length == 7 && losers.All(r => r.Exit == 1 && (r.Out == FailedCode("apply_edits_stale_head") || r.Out == FailedCode("apply_edits_store_unavailable"))),
                "8 racing runs on one head: the other 7 are refused as apply_edits_stale_head or apply_edits_store_unavailable", detail);
            var racedState = Json(File.ReadAllText(Path.Combine(raced, "project.json")));
            var receipts = racedState is { } st && st.TryGetProperty("Receipts", out var rec) && rec.ValueKind == JsonValueKind.Object ? rec.EnumerateObject().Count() : -1;
            Check(new ProjectStore(raced).Read().Revision == 1 && JournalLines(Snapshot(raced)).Length == 1 && receipts == 1,
                "after the race the store is at revision 1 with one journal line and one receipt", "receipts " + receipts.ToString(CultureInfo.InvariantCulture));
        }
        // A commit failing after it started writing (project.tmp is a directory, so publishing the new snapshot fails) is
        // not "nothing written": apply_edits_commit_incomplete. On disk: project.json still holds the old head, the
        // checkpoint of the head and one "prepared" journal line carrying the plan were written (ADR0007 recovery input).
        {
            var broken = NewStore(baseProject);
            Directory.CreateDirectory(Path.Combine(broken, "project.tmp"));
            var brokenEdits = Input("broken.edits.json", JsonSerializer.Serialize(new[] { ElementEdit.Update(widened) }));
            var brokenPlan = JsonSerializer.Serialize(IncrementalDiff.Diff(baseProject, new[] { ElementEdit.Update(widened) }).Plan);
            var before = Snapshot(broken);
            var r = Cli("apply-edits", "--store", broken, "--edits", brokenEdits);
            Check(r.Exit == 1 && r.Out == FailedCode("apply_edits_commit_incomplete"), "a commit failing after it started writing is apply_edits_commit_incomplete, not store_unavailable", D(r));
            var after = Snapshot(broken);
            Check(after["project.json"].Bytes.AsSpan().SequenceEqual(before["project.json"].Bytes) && new ProjectStore(broken).Read().Revision == 0, "commit_incomplete: project.json still holds the old head");
            Check(after.ContainsKey("checkpoint-0.json") && File.ReadAllText(Path.Combine(broken, "checkpoint-0.json")) == JsonSerializer.Serialize(baseProject), "commit_incomplete: checkpoint-0.json holds the unchanged head");
            var lines = JournalLines(after);
            Check(lines.Length == 1 && Raw(Json(lines[0]), "state") == "\"prepared\"" && Raw(Json(lines[0]), "plan") == brokenPlan, "commit_incomplete: one prepared journal line carrying the plan", lines.Length.ToString(CultureInfo.InvariantCulture));
            Check(Directory.Exists(Path.Combine(broken, "project.tmp")) && after.Keys.Except(before.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).SequenceEqual(new[] { "checkpoint-0.json", "journal.jsonl" }),
                "commit_incomplete: only the checkpoint and the journal were added; project.tmp is still the directory", string.Join(",", after.Keys));
            Check(r.Err.Contains("commit incomplete", StringComparison.Ordinal), "commit_incomplete writes the underlying exception to stderr", r.Err);
        }
        // Still committable afterwards with the right head.
        Applies("after the refusals, with --expected-revision 3", chain, new[] { ElementEdit.Update(widened with { GeometryMetres = new[] { 0, 0, 7, 0, 3, 0.2 } }) }, "3");

        // 3. Library refusals: the same code as diff-edits on the same inputs, nothing written.
        var store = NewStore(baseProject);
        var headPath = Input("base.head.json", JsonSerializer.Serialize(new ProjectStore(store).Read()));
        foreach (var (label, code, edits) in new (string, string, ElementEdit[])[]
        {
            ("an unknown action", "edit_invalid", new[] { new ElementEdit("move", wall.Id, wall) }),
            ("an update of an unknown id", "edit_unknown_id", new[] { ElementEdit.Update(Wall(G(0x30), 40)) }),
            ("two edits of one id", "edit_duplicate_id", new[] { ElementEdit.Update(widened), ElementEdit.Delete(wall.Id) }),
            ("a create of an existing id", "edit_create_exists", new[] { ElementEdit.Create(wall) }),
            ("more than IncrementalDiff.MaxEdits edits", "edit_bound_exceeded", Enumerable.Range(0, IncrementalDiff.MaxEdits + 1).Select(i => ElementEdit.Delete(G(0x40000 + i))).ToArray()),
            ("a delete leaving a dangling dependency", "invalid_ir", new[] { ElementEdit.Delete(wall.Id) }),
            ("an edit creating a dependency cycle", "invalid_ir", new[] { ElementEdit.Update(wall with { Dependencies = new[] { planDoc.Id } }) }),
        })
        {
            Check(LibraryCode(baseProject, edits) == code, "fixture: the library refuses " + label + " as " + code, LibraryCode(baseProject, edits));
            var path = Input(label.Replace(' ', '-') + ".edits.json", JsonSerializer.Serialize(edits));
            var diffEdits = Cli("diff-edits", "--project", headPath, "--edits", path);
            Check(diffEdits.Exit == 1 && diffEdits.Out == FailedCode(code), "fixture: diff-edits refuses " + label + " as " + code, D(diffEdits));
            Refused(label, code, store, path);
        }

        // 4. Malformed edits: typed refusal, nothing written.
        foreach (var (label, text) in new[]
        {
            ("not JSON", "{"), ("JSON null", "null"), ("an object", "{\"Action\":\"delete\"}"), ("empty file", ""), ("trailing content", "[] []"),
            ("an unknown member", "[{\"Action\":\"delete\",\"Id\":\"00000000-0000-0000-0000-000000000003\",\"Elemnt\":null}]"),
            ("a bad guid", "[{\"Action\":\"delete\",\"Id\":\"not-a-guid\"}]"),
            ("a duplicate key", "[{\"Action\":\"delete\",\"Id\":\"00000000-0000-0000-0000-000000000003\",\"Id\":\"00000000-0000-0000-0000-000000000003\"}]"),
            ("a duplicate nested key", "[{\"Action\":\"update\",\"Id\":\"00000000-0000-0000-0000-000000000002\",\"Element\":{\"Id\":\"00000000-0000-0000-0000-000000000002\",\"Kind\":\"wall\",\"GeometryMetres\":[0,0,5.4,0,3,0.2],\"Dependencies\":[],\"Provenance\":{\"Source\":\"a\",\"Source\":\"a\",\"Approval\":\"specified\"}}}]"),
        })
        {
            // The same refusal as diff-edits on the same file (the shared EditsInput parser; each verb names its own code).
            var path = Input("bad-" + label.Replace(' ', '-') + ".edits.json", text);
            var diffEdits = Cli("diff-edits", "--project", headPath, "--edits", path);
            Check(diffEdits.Exit == 1 && diffEdits.Out == FailedCode("diff_edits_edits_invalid"), "diff-edits refuses the same edits that are " + label + " as diff_edits_edits_invalid", D(diffEdits));
            Refused("edits that are " + label, "apply_edits_edits_invalid", store, path);
        }
        var nullEntry = Input("null-entry.edits.json", "[null]");
        Check(Cli("diff-edits", "--project", headPath, "--edits", nullEntry).Out == FailedCode("edit_invalid"), "diff-edits refuses a null edit entry as edit_invalid");
        Refused("a null edit entry", "edit_invalid", store, nullEntry);

        // 5. Bounded and missing inputs: BoundedInput's codes, nothing written.
        var okEdits = Input("ok.edits.json", JsonSerializer.Serialize(new[] { ElementEdit.Update(widened) }));
        var overPath = Path.Combine(scratch, "over-cap.edits.json"); File.WriteAllBytes(overPath, overCap);
        Refused("edits over 8 MiB", "input_too_large", store, overPath);
        var missing = Path.Combine(scratch, "absent.edits.json");
        Refused("a missing edits file", "input_not_found", store, missing);
        var aDirectory = Path.Combine(scratch, "a-directory.json"); Directory.CreateDirectory(aDirectory);
        Refused("a directory as edits", "input_not_regular_file", store, aDirectory);
        var noStore = Path.Combine(scratch, "no-store");
        Refused("a missing store", "input_not_found", noStore, okEdits);
        Check(!Directory.Exists(noStore), "a missing store directory is not created");
        var emptyStore = Path.Combine(scratch, "empty-store"); Directory.CreateDirectory(emptyStore);
        Refused("an uninitialized store directory", "input_not_found", emptyStore, okEdits);
        // A store whose project.json is corrupt, oversized or not a store state.
        foreach (var (label, bytes) in new (string, byte[])[]
        {
            ("not JSON", Encoding.UTF8.GetBytes("{")),
            ("a bare project, not a store state", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(baseProject))),
            ("a state with null elements", Encoding.UTF8.GetBytes("{\"Project\":{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Revision\":0,\"Elements\":null},\"Receipts\":{}}")),
            ("a state with null receipts", Encoding.UTF8.GetBytes("{\"Project\":" + JsonSerializer.Serialize(baseProject) + ",\"Receipts\":null}")),
        })
        {
            var bad = NewStore(baseProject);
            File.WriteAllBytes(Path.Combine(bad, "project.json"), bytes);
            Refused("a store whose project.json is " + label, "apply_edits_store_invalid", bad, okEdits);
        }
        var overStore = NewStore(baseProject);
        File.WriteAllBytes(Path.Combine(overStore, "project.json"), overCap);
        Refused("a store whose project.json is over 8 MiB", "input_too_large", overStore, okEdits);

        // 6. Usage: --store and --edits once each, optional --expected-revision once (a non-negative decimal integer),
        //    nothing else; checked before any file is read.
        foreach (var bad in new[]
        {
            new[] { "apply-edits" }, new[] { "apply-edits", "--store", store }, new[] { "apply-edits", "--edits", okEdits }, new[] { "apply-edits", "--store", store, "--edits" },
            new[] { "apply-edits", "--store", store, "--store", store }, new[] { "apply-edits", "--store", store, "--edits", okEdits, "extra" },
            new[] { "apply-edits", "--store", store, "--edits", okEdits, "--max", "3" }, new[] { "apply-edits", store, okEdits },
            new[] { "apply-edits", "--store", "", "--edits", okEdits }, new[] { "apply-edits", "--store", "--edits", "--edits", okEdits },
            new[] { "apply-edits", "--project", store, "--edits", okEdits },
            new[] { "apply-edits", "--store", store, "--edits", okEdits, "--expected-revision" },
            new[] { "apply-edits", "--store", store, "--edits", okEdits, "--expected-revision", "abc" },
            new[] { "apply-edits", "--store", store, "--edits", okEdits, "--expected-revision", "-1" },
            new[] { "apply-edits", "--store", store, "--edits", okEdits, "--expected-revision", "+0" },
            new[] { "apply-edits", "--store", store, "--edits", okEdits, "--expected-revision", " 0" },
            new[] { "apply-edits", "--store", store, "--edits", okEdits, "--expected-revision", "1e3" },
            new[] { "apply-edits", "--store", store, "--edits", okEdits, "--expected-revision", "99999999999" },
            new[] { "apply-edits", "--store", store, "--edits", okEdits, "--expected-revision", "0", "--expected-revision", "0" },
        })
            Refused("arguments: " + string.Join(" ", bad.Skip(1).Select(a => a == store ? "<store>" : a == okEdits ? "<edits>" : a)), "apply_edits_usage", store, okEdits, bad);
        Check(new ProjectStore(store).Read().Revision == 0, "after every refusal the refused store is still at revision 0");

        // 7. Static: the verb reads through BoundedInput, diffs through IncrementalDiff and writes only through ProjectStore.Commit.
        var sourcePath = Path.Combine(root, "interfaces", "cli", "ApplyEditsCommand.cs");
        Check(File.Exists(sourcePath), "interfaces/cli/ApplyEditsCommand.cs exists");
        if (File.Exists(sourcePath))
        {
            var source = File.ReadAllText(sourcePath);
            Check(source.Contains("BoundedInput.ReadText(", StringComparison.Ordinal) && source.Contains("IncrementalDiff", StringComparison.Ordinal) && source.Contains(".Commit(", StringComparison.Ordinal)
                && !source.Contains("Compiler.Diff", StringComparison.Ordinal) && new[] { "File.", "FileStream", "FileMode.", "Directory.", "Initialize(" }.All(api => !source.Contains(api, StringComparison.Ordinal)),
                "apply-edits reads through BoundedInput, diffs through IncrementalDiff and writes only through ProjectStore.Commit");
            Check(source.Contains("Console.Error.WriteLine(", StringComparison.Ordinal) && source.Contains("InternalError", StringComparison.Ordinal), "apply-edits writes the type and message of an internal error to stderr");
        }
        // The edits parser is shared with diff-edits, not copied.
        var diffEditsSource = Path.Combine(root, "interfaces", "cli", "DiffEditsCommand.cs");
        var sharedSource = Path.Combine(root, "interfaces", "cli", "EditsInput.cs");
        if (File.Exists(sourcePath) && File.Exists(diffEditsSource) && File.Exists(sharedSource))
        {
            var verbs = new[] { File.ReadAllText(sourcePath), File.ReadAllText(diffEditsSource) };
            Check(verbs.All(v => v.Contains("EditsInput.Parse(", StringComparison.Ordinal) && v.Contains("EditsInput.LibraryCodes", StringComparison.Ordinal) && !v.Contains("HasDuplicateKey", StringComparison.Ordinal) && !v.Contains("JsonUnmappedMemberHandling", StringComparison.Ordinal))
                && File.ReadAllText(sharedSource).Contains("HasDuplicateKey", StringComparison.Ordinal),
                "diff-edits and apply-edits share interfaces/cli/EditsInput.cs (parser and library codes), neither keeps a copy");
        }
        else Check(false, "interfaces/cli/EditsInput.cs and DiffEditsCommand.cs exist");
        Check(Cli("no-such-verb").Out.Contains("apply-edits --store store-directory --edits edits.json [--expected-revision n]", StringComparison.Ordinal), "default usage lists apply-edits");
    }
}
