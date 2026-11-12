using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Core;

// P2-13 `mirror-drift-adopt store-directory selection.json`: the P2-12 commit (MirrorDriftAdoptCommit.Commit) of a P2-10
// selection file on the CLI. Oracles, all against the real Release CLI process:
//   1. A canonical selection file (MirrorDriftSelection.ToJson, what mirror-drift-select writes) commits exactly what
//      MirrorDriftAdoptCommit.Commit commits in process on a twin store: project.json, journal.jsonl and the checkpoint
//      are byte-identical, and stdout is {"status":"succeeded","outcome","revision","key"} with the Core key.
//   2. A replay prints outcome replayed and writes nothing; a restore-only selection prints nothing_to_adopt (key null)
//      without opening the store.
//   3. Every refusal (bounded input, non-canonical or malformed selection, every drift_adopt_* the Core raises before
//      writing) prints one {"status":"failed","code"[,"detail"]} envelope, exits 1 and leaves the store byte-identical
//      (file names, bytes and mtimes).
internal static class MirrorDriftAdoptTests
{
    const string P = "60f91daf-3dd7-4283-a86d-24137b73f3da-";
    const string W1 = P + "00001001";
    const string W2 = P + "00001002";
    const string W3 = P + "00001003";
    const string D1 = P + "00002001";
    const string F1 = P + "00004001";
    const string Doc = P + "0001fd0b";
    const string Adopt = MirrorDriftResolveActions.Adopt;
    const string Restore = MirrorDriftResolveActions.Restore;

    public static void Run(string root, string work, string dotnet, string cliDll, byte[] overCap, Action<bool, string, string> check)
    {
        var scratch = Path.Combine(work, "mirror-drift-adopt");
        Directory.CreateDirectory(scratch);
        void Check(bool condition, string name, string detail = "") => check(condition, "mirror-drift-adopt: " + name, detail);
        try { Checks(root, scratch, dotnet, cliDll, overCap, Check); }
        catch (Exception error) { Check(false, "oracle threw", error.GetType().Name + ": " + error.Message); }
    }

    static void Checks(string root, string scratch, string dotnet, string cliDll, byte[] overCap, Action<bool, string, string> report)
    {
        void Check(bool condition, string name, string detail = "") => report(condition, name, detail);
        (int Exit, string Out, string Err) Cli(params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(cliDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000)) { process.Kill(true); return (-1, "<timed out>", ""); }
            process.WaitForExit();
            var text = stdout.Result;
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];
            return (process.ExitCode, text, stderr.Result.ReplaceLineEndings(" ").Trim());
        }
        string R((int Exit, string Out, string Err) r) => "exit " + r.Exit + " " + r.Out + (r.Err.Length > 0 ? " stderr " + r.Err : "");
        var n = 0;
        string Next(string stem) => Path.Combine(scratch, stem + "-" + (++n).ToString(System.Globalization.CultureInfo.InvariantCulture));
        string File_(string content, string stem = "selection") { var path = Next(stem) + ".json"; File.WriteAllText(path, content, new UTF8Encoding(false)); return path; }
        string Store(Project project)
        {
            var directory = Next("store");
            new ProjectStore(directory).Initialize(project);
            foreach (var file in Directory.GetFiles(directory)) File.SetLastWriteTimeUtc(file, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
            return directory;
        }
        SortedDictionary<string, (byte[] Bytes, DateTime Time)> Files(string directory)
        {
            var result = new SortedDictionary<string, (byte[], DateTime)>(StringComparer.Ordinal);
            if (!Directory.Exists(directory)) return result;
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                result[Path.GetRelativePath(directory, file)] = (File.ReadAllBytes(file), File.GetLastWriteTimeUtc(file));
            return result;
        }
        bool Same(SortedDictionary<string, (byte[] Bytes, DateTime Time)> a, SortedDictionary<string, (byte[] Bytes, DateTime Time)> b) =>
            a.Keys.SequenceEqual(b.Keys, StringComparer.Ordinal) && a.All(pair => pair.Value.Bytes.AsSpan().SequenceEqual(b[pair.Key].Bytes) && pair.Value.Time == b[pair.Key].Time);
        string Names(SortedDictionary<string, (byte[] Bytes, DateTime Time)> files) => string.Join(",", files.Keys);
        string Succeeded(string outcome, int revision, string? key) => JsonSerializer.Serialize(new { status = "succeeded", outcome, revision, key });
        string? Code(string output)
        {
            try { using var document = JsonDocument.Parse(output); return document.RootElement.GetProperty("status").GetString() == "failed" ? document.RootElement.GetProperty("code").GetString() : null; }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
        }
        // A refusal: exit 1, one failed envelope with this code, and the store untouched.
        void Refused(string name, string code, string store, string selection)
        {
            var before = Files(store);
            var r = Cli("mirror-drift-adopt", store, selection);
            Check(r.Exit == 1 && Code(r.Out) == code, name + " (exit 1, " + code + ")", R(r));
            Check(Same(before, Files(store)), name + ": the store is byte-identical", Names(Files(store)));
        }

        // Fixture: the P2-07/P2-09 drifted model of tests/mirror-journal (W2 moved, W3 added, D1 deleted; Core at revision 3).
        var dir = Path.Combine(root, "tests", "mirror-journal", "fixtures", "drift");
        string Read(string name) => File.ReadAllText(Path.Combine(dir, name));
        var snake = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };
        var observation = JsonSerializer.Deserialize<MirrorObservation>(Read("observation.json"), snake)!;
        var committed = MirrorCommittedModel.Parse(Read("committed.json"));
        var journal = MirrorDrift.ParseJournalEntries(Read("journal.json"));
        MirrorObservedElement Wall(MirrorObservedElement template, string uid, double ax, double ay, double bx, double by) =>
            template with { UniqueId = uid, Start = new MirrorPoint(ax, ay), End = new MirrorPoint(bx, by) };
        var wall1 = observation.Elements.Single(element => element.UniqueId == W1);
        var wall2 = observation.Elements.Single(element => element.UniqueId == W2);
        var floor = observation.Elements.Single(element => element.UniqueId == F1);
        var drifted = MirrorSnapshotMapper.Map(observation with { Elements = new[] { wall1, Wall(wall2, W2, 5050, 0, 5050, 4000), Wall(wall1, W3, 0, 0, 0, 4000), floor } });
        var bindings = new RevitBindings(new RevitDocumentRef(@"C:\Repo\.bim\native-runs\p2-13\copy.rvt", Doc), new Dictionary<Guid, string>(), new[] { new RevitWallTypeBinding(0.2, "Generic - 200mm") },
            "Single-Flush: 0915 x 2134mm", "Fixed: 1000 x 1200mm");
        var resolution = MirrorDriftResolve.Plan(MirrorDrift.Compare(drifted, committed, journal, new MirrorDriftOptions(JournalAfterSequence: 4)), drifted, committed, bindings);
        MirrorDriftSelection Choose(params (string Id, string Action)[] choices) => MirrorDriftSelect.Select(resolution, choices.Select(item => new MirrorDriftChoice(item.Id, item.Action)).ToArray());
        var head = committed.Project;
        var adoptAll = Choose((D1, Adopt), (W3, Adopt), (W2, Adopt));
        var adoptAllText = adoptAll.ToJson();
        var key = MirrorDriftAdoptCommit.Key(adoptAll);
        Check(head.Revision == 3 && adoptAll.Operations.Count == 3 && key == MirrorDriftAdoptCommit.KeyPrefix + head.Id.ToString("D") + ":3:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(adoptAllText))).ToLowerInvariant(),
            "fixture: three adopt operations at revision 3, key over the canonical selection", key);
        var adoptAllPath = File_(adoptAllText);

        // ---- 1. Commit: same bytes as the in-process Core commit on a twin store ----
        var store = Store(head);
        var twin = Store(head);
        var expected = MirrorDriftAdoptCommit.Commit(twin, adoptAll);
        var first = Cli("mirror-drift-adopt", store, adoptAllPath);
        Check(first.Exit == 0 && first.Out == Succeeded("committed", 4, key), "a canonical selection commits one revision and prints {status,outcome,revision,key}", R(first) + " expected " + Succeeded("committed", 4, key));
        Check(expected.Outcome == "committed" && expected.Revision == 4, "in-process twin commits revision 4", expected.Outcome);
        var committedFiles = Files(store);
        var twinFiles = Files(twin);
        Check(committedFiles.Keys.SequenceEqual(twinFiles.Keys, StringComparer.Ordinal)
            && new[] { "project.json", "journal.jsonl", "checkpoint-3.json" }.All(name => committedFiles.ContainsKey(name) && committedFiles[name].Bytes.AsSpan().SequenceEqual(twinFiles[name].Bytes)),
            "the CLI store is byte-identical to MirrorDriftAdoptCommit.Commit on a twin store (project.json, journal, checkpoint)", Names(committedFiles) + " vs " + Names(twinFiles));
        Check(new ProjectStore(store).Read().Revision == 4, "the store head is revision 4");

        // ---- 2. Replay: prints replayed, writes nothing; also from the selection mirror-drift-select would write again ----
        var settled = Files(store);
        var replay = Cli("mirror-drift-adopt", store, adoptAllPath);
        Check(replay.Exit == 0 && replay.Out == Succeeded("replayed", 4, key), "a replay prints outcome replayed at the existing head", R(replay));
        Check(Same(settled, Files(store)), "a replay writes nothing", Names(Files(store)));
        var permutedPath = File_(Choose((W2, Adopt), (W3, Adopt), (D1, Adopt)).ToJson());
        var replay2 = Cli("mirror-drift-adopt", store, permutedPath);
        Check(replay2.Exit == 0 && replay2.Out == Succeeded("replayed", 4, key) && Same(settled, Files(store)), "the same selection with permuted choices is the same key and replays", R(replay2));
        // BOM-prefixed file decodes like every bounded CLI input and is the same selection.
        var bomPath = Next("selection-bom") + ".json";
        File.WriteAllBytes(bomPath, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(adoptAllText)]);
        var bom = Cli("mirror-drift-adopt", store, bomPath);
        Check(bom.Exit == 0 && bom.Out == Succeeded("replayed", 4, key), "a UTF-8 BOM selection file is the same selection", R(bom));

        // ---- 3. Restore-only and empty selections: nothing_to_adopt, key null, the store never opened ----
        var restoreOnlyPath = File_(Choose((W2, Restore), (W3, Restore), (D1, Restore)).ToJson());
        var quiet = Store(head);
        var quietFiles = Files(quiet);
        var nothing = Cli("mirror-drift-adopt", quiet, restoreOnlyPath);
        Check(nothing.Exit == 0 && nothing.Out == Succeeded("nothing_to_adopt", 3, null) && Same(quietFiles, Files(quiet)), "a restore-only selection prints nothing_to_adopt with key null and writes nothing", R(nothing));
        var missingStore = Path.Combine(scratch, "no-such-store");
        var unopened = Cli("mirror-drift-adopt", missingStore, File_(Choose().ToJson()));
        Check(unopened.Exit == 0 && unopened.Out == Succeeded("nothing_to_adopt", 3, null) && !Directory.Exists(missingStore), "an empty selection never opens (or creates) the store", R(unopened));

        // ---- 4. Core refusals: typed drift_adopt_* codes, store byte-identical ----
        var partialPath = File_(Choose((W3, Adopt)).ToJson());
        Refused("a selection made at revision 3 on a store at revision 4 is stale", MirrorDriftAdoptCodes.StaleRevision, store, partialPath);
        var other = Store(head with { Id = Guid.Parse("c0000000-0000-4000-8000-0000000000aa") });
        Refused("a store of another Core project is refused", MirrorDriftAdoptCodes.ProjectMismatch, other, adoptAllPath);
        var sound = Store(head);
        var update = adoptAll.Operations.Single(operation => operation.Action == "update");
        Refused("two operations on one element are refused", MirrorDriftAdoptCodes.SelectionInvalid, sound, File_((adoptAll with { Operations = new[] { update, update } }).ToJson()));
        Refused("an unknown operation action is refused", MirrorDriftAdoptCodes.SelectionInvalid, sound, File_((adoptAll with { Operations = new[] { update with { Action = "move" } } }).ToJson()));
        Refused("creating an existing element is refused", MirrorDriftAdoptCodes.SelectionInvalid, sound, File_((adoptAll with { Operations = new[] { update with { Action = "create" } } }).ToJson()));
        var dangling = update with { Element = update.Element with { Dependencies = new[] { Guid.Parse("c0000000-0000-4000-8000-0000000000ee") } } };
        Refused("an adopted model failing validation is refused", MirrorDriftAdoptCodes.InvalidIr, sound, File_((adoptAll with { Operations = new[] { dangling } }).ToJson()));
        var garbage = Next("store-garbage");
        Directory.CreateDirectory(garbage);
        File.WriteAllText(Path.Combine(garbage, "project.json"), "{\"Receipts\":{}}");
        Refused("a store without a project is refused", MirrorDriftAdoptCodes.StoreInvalid, garbage, adoptAllPath);
        var locked = Store(head);
        var lockedFiles = Files(locked);
        (int Exit, string Out, string Err) lockedRun;
        using (new FileStream(Path.Combine(locked, "lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) lockedRun = Cli("mirror-drift-adopt", locked, adoptAllPath);
        Check(lockedRun.Exit == 1 && Code(lockedRun.Out) == MirrorDriftAdoptCodes.StoreUnavailable && Same(lockedFiles, Files(locked)), "a held store lock is drift_adopt_store_unavailable and writes nothing", R(lockedRun));
        var afterLock = Cli("mirror-drift-adopt", locked, adoptAllPath);
        Check(afterLock.Exit == 0 && afterLock.Out == Succeeded("committed", 4, key), "the same selection commits once the lock is free", R(afterLock));
        // A refusal envelope carries the Core detail after the code.
        var staleRun = Cli("mirror-drift-adopt", store, partialPath);
        Check(staleRun.Exit == 1 && staleRun.Out.Length > 80 && staleRun.Out.EndsWith("\"}", StringComparison.Ordinal)
            && staleRun.Out.StartsWith("{\"status\":\"failed\",\"code\":\"drift_adopt_stale_revision\",\"detail\":", StringComparison.Ordinal), "a refusal prints {status,code,detail} with the Core detail", R(staleRun));

        // ---- 5. Strict selection file: only the canonical P2-10 JSON is accepted ----
        var fresh = Store(head);
        // Each mutation must change the canonical text, or the refusal below would test nothing.
        string M(string from, string to)
        {
            Check(adoptAllText.Contains(from, StringComparison.Ordinal), "fixture: the selection contains " + from);
            return File_(adoptAllText.Replace(from, to, StringComparison.Ordinal));
        }
        var evidence = adoptAll.EvidenceIncomplete ? "true" : "false";
        Refused("a selection that is not JSON", MirrorDriftAdoptCodes.SelectionInvalid, fresh, File_("{not json"));
        Refused("an empty selection file", MirrorDriftAdoptCodes.SelectionInvalid, fresh, File_(""));
        Refused("a JSON array", MirrorDriftAdoptCodes.SelectionInvalid, fresh, File_("[]"));
        Refused("a trailing newline (not canonical)", MirrorDriftAdoptCodes.SelectionInvalid, fresh, File_(adoptAllText + "\n"));
        Refused("indented JSON (not canonical)", MirrorDriftAdoptCodes.SelectionInvalid, fresh, File_(JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(adoptAllText), new JsonSerializerOptions { WriteIndented = true })));
        Refused("an unknown root member", MirrorDriftAdoptCodes.SelectionInvalid, fresh, File_(adoptAllText[..^1] + ",\"apply\":true}"));
        Refused("a duplicate root member", MirrorDriftAdoptCodes.SelectionInvalid, fresh, File_("{\"core_revision\":3," + adoptAllText[1..]));
        Refused("a missing member", MirrorDriftAdoptCodes.SelectionInvalid, fresh, M("\"evidence_incomplete\":" + evidence + ",", ""));
        Refused("another selection_version", MirrorDriftAdoptCodes.SelectionInvalid, fresh, M("\"selection_version\":1", "\"selection_version\":2"));
        Refused("applied true", MirrorDriftAdoptCodes.SelectionInvalid, fresh, M("\"applied\":false", "\"applied\":true"));
        Refused("another order", MirrorDriftAdoptCodes.SelectionInvalid, fresh, M("\"order\":\"restore_then_adopt\"", "\"order\":\"adopt_then_restore\""));
        Refused("an uppercase core_project_id", MirrorDriftAdoptCodes.SelectionInvalid, fresh, File_(adoptAllText.Replace(head.Id.ToString("D"), head.Id.ToString("D").ToUpperInvariant(), StringComparison.Ordinal)));
        Refused("a string core_revision", MirrorDriftAdoptCodes.SelectionInvalid, fresh, M("\"core_revision\":3", "\"core_revision\":\"3\""));
        Refused("a null operations array", MirrorDriftAdoptCodes.SelectionInvalid, fresh, File_(System.Text.RegularExpressions.Regex.Replace(adoptAllText, "\"operations\":\\[.*?\\],\"map_adds\"", "\"operations\":null,\"map_adds\"")));
        Refused("a restore plan bound to another revision", MirrorDriftAdoptCodes.SelectionInvalid, fresh, M("\"expected_revision\":3", "\"expected_revision\":2"));
        Refused("a deeply nested value", MirrorDriftAdoptCodes.SelectionInvalid, fresh, File_(new string('[', 200) + new string(']', 200)));
        // Non-canonical number forms of the same values (review 5): an exponent on the first geometry number, a 3.0 revision.
        var firstNumber = System.Text.RegularExpressions.Regex.Match(adoptAllText, "\"geometry_metres\":\\[(-?[0-9][0-9.eE+-]*)");
        Check(firstNumber.Success, "fixture: the selection has a geometry number", adoptAllText.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (firstNumber.Success)
            Refused("a non-canonical number form (" + firstNumber.Groups[1].Value + "e0)", MirrorDriftAdoptCodes.SelectionInvalid, fresh,
                File_(adoptAllText[..firstNumber.Groups[1].Index] + firstNumber.Groups[1].Value + "e0" + adoptAllText[(firstNumber.Groups[1].Index + firstNumber.Groups[1].Length)..]));
        Refused("a core_revision written 3.0", MirrorDriftAdoptCodes.SelectionInvalid, fresh, M("\"core_revision\":3", "\"core_revision\":3.0"));
        // Only UTF-8 (optionally with its BOM) is a selection file (review 4): UTF-16 and UTF-32 with a BOM, and invalid UTF-8, are refused.
        string Encoded(string stem, byte[] bytes) { var path = Next(stem) + ".json"; File.WriteAllBytes(path, bytes); return path; }
        Refused("a UTF-16 LE selection with its BOM", MirrorDriftAdoptCodes.SelectionInvalid, fresh, Encoded("selection-utf16", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(adoptAllText)]));
        Refused("a UTF-16 BE selection with its BOM", MirrorDriftAdoptCodes.SelectionInvalid, fresh, Encoded("selection-utf16be", [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(adoptAllText)]));
        Refused("a UTF-32 LE selection with its BOM", MirrorDriftAdoptCodes.SelectionInvalid, fresh, Encoded("selection-utf32", [.. Encoding.UTF32.GetPreamble(), .. Encoding.UTF32.GetBytes(adoptAllText)]));
        var invalidUtf8 = Encoding.UTF8.GetBytes(adoptAllText);
        var docAt = adoptAllText.IndexOf(Doc, StringComparison.Ordinal);
        invalidUtf8[docAt] = 0xFF;
        Refused("a selection with an invalid UTF-8 byte", MirrorDriftAdoptCodes.SelectionInvalid, fresh, Encoded("selection-bad-utf8", invalidUtf8));

        // ---- 6. Bounded input: typed input_* codes with detail selection or store ----
        void Input(string name, string code, string detail, string storeDirectory, string selection)
        {
            var before = Files(storeDirectory);
            var r = Cli("mirror-drift-adopt", storeDirectory, selection);
            Check(r.Exit == 1 && r.Out == JsonSerializer.Serialize(new { status = "failed", code, detail }) && Same(before, Files(storeDirectory)), name + " (" + code + ", detail " + detail + ")", R(r));
        }
        var bounded = Store(head);
        var overPath = Next("selection-big") + ".json";
        File.WriteAllBytes(overPath, overCap);
        Input("a selection over 8 MiB", "input_too_large", "selection", bounded, overPath);
        Input("a missing selection", "input_not_found", "selection", bounded, Path.Combine(scratch, "missing-selection.json"));
        Input("an empty selection path", "input_path_invalid", "selection", bounded, "");
        Input("a directory as selection", "input_not_regular_file", "selection", bounded, scratch);
        var link = Next("selection-link") + ".json";
        var linked = false;
        try { File.CreateSymbolicLink(link, adoptAllPath); linked = true; } catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        if (linked) Input("a symlinked selection", "input_link_rejected", "selection", bounded, link);
        var empty = Next("store-empty");
        Directory.CreateDirectory(empty);
        Input("a store without project.json", "input_not_found", "store", empty, adoptAllPath);
        Input("an empty store path", "input_path_invalid", "store", "", adoptAllPath);
        // Review 1: a blank store path is refused only when there is something to commit; restore-only and empty
        // selections never use the store (as MirrorDriftAdoptCommit.Commit), so a blank store is nothing_to_adopt.
        Input("a whitespace store path", "input_path_invalid", "store", "   ", adoptAllPath);
        foreach (var (label, blank) in new[] { ("an empty", ""), ("a whitespace", "   ") })
        {
            var r = Cli("mirror-drift-adopt", blank, restoreOnlyPath);
            Check(r.Exit == 0 && r.Out == Succeeded("nothing_to_adopt", 3, null), label + " store path with a restore-only selection is nothing_to_adopt", R(r));
        }
        if (linked)
        {
            var linkStore = Next("store-link");
            Directory.CreateDirectory(linkStore);
            File.CreateSymbolicLink(Path.Combine(linkStore, "project.json"), Path.Combine(bounded, "project.json"));
            Input("a store whose project.json is a symlink", "input_link_rejected", "store", linkStore, adoptAllPath);
        }
        Check(new ProjectStore(bounded).Read().Revision == 3, "the bounded-input refusals left the store at revision 3");

        // ---- 7. Usage and static guards ----
        var usage = Cli("mirror-drift-adopt", bounded);
        Check(usage.Exit == 1 && Code(usage.Out) is { } u && u.StartsWith("usage: mirror-drift-adopt store-directory selection.json", StringComparison.Ordinal), "a missing argument prints the usage", R(usage));
        var extra = Cli("mirror-drift-adopt", bounded, adoptAllPath, "extra");
        Check(extra.Exit == 1 && Code(extra.Out) is { } x && x.StartsWith("usage: mirror-drift-adopt", StringComparison.Ordinal) && new ProjectStore(bounded).Read().Revision == 3, "an extra non-flag argument prints the usage and commits nothing", R(extra));
        Check(Cli("no-such-verb").Out.Contains("mirror-drift-adopt store-directory selection.json", StringComparison.Ordinal), "the default usage lists mirror-drift-adopt");

        // ---- 8. P2-19 --preview: the MCP plan mode (mirror_drift_adopt without confirm) on the CLI, read-only ----
        Preview(scratch, head, adoptAllText, adoptAllPath, partialPath, restoreOnlyPath, bomPath, key, Store, File_, Files, Same, Names, Cli, R, Code, Succeeded, Check);
        // P2-15: the engine moved to interfaces/mcp/MirrorDriftAdoptTool.cs (shared with the MCP tool); the guards cover both files.
        var source = Path.Combine(root, "interfaces", "cli", "MirrorDriftAdoptCommand.cs");
        var engine = Path.Combine(root, "interfaces", "mcp", "MirrorDriftAdoptTool.cs");
        Check(File.Exists(source) && File.Exists(engine), "interfaces/cli/MirrorDriftAdoptCommand.cs and interfaces/mcp/MirrorDriftAdoptTool.cs exist");
        if (File.Exists(source) && File.Exists(engine))
        {
            var text = File.ReadAllText(source) + "\n" + File.ReadAllText(engine);
            Check(text.Contains("BoundedInput.ReadText", StringComparison.Ordinal) && !text.Contains("File.ReadAll", StringComparison.Ordinal) && !text.Contains("File.OpenRead", StringComparison.Ordinal),
                "the selection is read only through BoundedInput");
            Check(text.Contains("MirrorDriftAdoptCommit.Commit(", StringComparison.Ordinal) && !text.Contains("new ProjectStore(", StringComparison.Ordinal) && !text.Contains("PlanRunner", StringComparison.Ordinal) && !text.Contains("RevitCommandService", StringComparison.Ordinal),
                "the verb commits only through MirrorDriftAdoptCommit.Commit and never runs a plan or talks to Revit");
            // Review 2: an unexpected exception is reported by its type name only (as MirrorDriftTool), never its message.
            Check(!text.Contains("e.Message", StringComparison.Ordinal) && !text.Contains("error.Message", StringComparison.Ordinal) && text.Contains("GetType().Name", StringComparison.Ordinal),
                "unexpected exceptions are reported by type name only, never by message");
        }
    }

    // P2-19 oracles: `mirror-drift-adopt store selection.json --preview` stdout is byte-equal to the text of the MCP tool
    // mirror_drift_adopt called without confirm (a real McpServer tools/call) on the same store and selection text, in
    // every outcome (would_commit, would_replay, nothing_to_adopt) and every refusal; the store keeps its file names,
    // bytes and mtimes and its directory mtime; a held store lock does not block it (no lock taken); without the flag the
    // P2-15 bytes are unchanged; an unknown or repeated flag is a typed envelope, refused before any input is read.
    delegate (int Exit, string Out, string Err) CliRun(params string[] arguments);
    static void Preview(string scratch, Project head, string adoptAllText, string adoptAllPath, string partialPath, string restoreOnlyPath, string bomPath, string key,
        Func<Project, string> Store, Func<string, string, string> File_, Func<string, SortedDictionary<string, (byte[] Bytes, DateTime Time)>> Files,
        Func<SortedDictionary<string, (byte[] Bytes, DateTime Time)>, SortedDictionary<string, (byte[] Bytes, DateTime Time)>, bool> Same,
        Func<SortedDictionary<string, (byte[] Bytes, DateTime Time)>, string> Names, CliRun Cli, Func<(int Exit, string Out, string Err), string> R,
        Func<string, string?> Code, Func<string, int, string?, string> Succeeded, Action<bool, string, string> report)
    {
        void Check(bool condition, string name, string detail = "") => report(condition, "--preview: " + name, detail);
        var server = new Bim.Mcp.McpServer();
        (bool IsError, string Text) Mcp(string store, string selection)
        {
            var request = new System.Text.Json.Nodes.JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 7, ["method"] = "tools/call",
                ["params"] = new System.Text.Json.Nodes.JsonObject { ["name"] = "mirror_drift_adopt", ["arguments"] = new System.Text.Json.Nodes.JsonObject { ["store_path"] = store, ["selection"] = selection } },
            }.ToJsonString();
            var response = System.Text.Json.Nodes.JsonNode.Parse(server.Handle(request) ?? throw new InvalidOperationException("no MCP response"))!["result"]!;
            return (response["isError"]!.GetValue<bool>(), response["content"]![0]!["text"]!.GetValue<string>());
        }
        DateTime DirTime(string directory) => Directory.Exists(directory) ? Directory.GetLastWriteTimeUtc(directory) : DateTime.MinValue;
        string? Outcome(string output)
        {
            try { using var document = JsonDocument.Parse(output); return document.RootElement.GetProperty("status").GetString() + "/" + (document.RootElement.TryGetProperty("outcome", out var o) ? o.GetString() : null); }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
        }
        // One preview: CLI stdout == MCP text (computed after the CLI run, same store state), exit 0 iff the MCP call is not an error,
        // the store and its directory unchanged by the CLI run.
        // lockHeld: the test holds the store lock (FileShare.None), so the store is snapshotted by the caller around the hold.
        string Equal(string name, string store, string selectionPath, string selectionText, string expectStatus, bool lockHeld = false)
        {
            var before = lockHeld ? null : Files(store);
            var beforeDir = DirTime(store);
            var r = Cli("mirror-drift-adopt", store, selectionPath, "--preview");
            var afterFiles = lockHeld ? null : Files(store);
            var afterDir = DirTime(store);
            var mcp = Mcp(store, selectionText);
            Check(r.Out == mcp.Text && r.Exit == (mcp.IsError ? 1 : 0), name + ": stdout byte-equal to MCP mirror_drift_adopt without confirm", R(r) + " | mcp " + mcp.Text);
            Check(Outcome(r.Out) == expectStatus, name + ": " + expectStatus, R(r));
            if (before is not null && afterFiles is not null)
                Check(Same(before, afterFiles) && beforeDir == afterDir, name + ": store file names, bytes, mtimes and directory mtime unchanged", Names(before) + " -> " + Names(afterFiles));
            else Check(beforeDir == afterDir, name + ": store directory mtime unchanged");
            return r.Out;
        }

        // would_commit: the plan, the confirmed commit afterwards still commits revision 4 (the preview consumed nothing).
        var store = Store(head);
        var plan = Equal("a fresh store", store, adoptAllPath, adoptAllText, "planned/would_commit");
        Check(plan.StartsWith(JsonSerializer.Serialize(new { status = "planned", applied = false, outcome = "would_commit", revision = 4, key })[..^1] + ",\"plan\":{", StringComparison.Ordinal),
            "a fresh store previews {status planned, applied false, would_commit, revision 4, key, plan}", plan.Length > 200 ? plan[..200] : plan);
        Check(plan == Bim.Mcp.MirrorDriftAdoptTool.Run(store, adoptAllText, confirm: false).Text, "the preview is MirrorDriftAdoptTool plan mode in process");
        var commit = Cli("mirror-drift-adopt", store, adoptAllPath);
        Check(commit.Exit == 0 && commit.Out == Succeeded("committed", 4, key), "without --preview the P2-15 bytes: committed revision 4 after the preview", R(commit));
        // would_replay after the commit; the BOM file is the same selection text.
        Equal("a committed store", store, adoptAllPath, adoptAllText, "planned/would_replay");
        Equal("a UTF-8 BOM selection file", store, bomPath, adoptAllText, "planned/would_replay");
        // nothing_to_adopt: the store is never opened or created.
        var never = Path.Combine(scratch, "preview-never");
        Equal("a restore-only selection on a missing store", never, restoreOnlyPath, File.ReadAllText(restoreOnlyPath), "planned/nothing_to_adopt");
        Check(!Directory.Exists(never), "a restore-only preview never creates the store");
        // A held store lock does not block the preview (no lock taken); it still says would_commit.
        var locked = Store(head);
        var lockedBefore = Files(locked);
        var lockedDir = DirTime(locked);
        using (new FileStream(Path.Combine(locked, "lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Equal("a store whose lock is held", locked, adoptAllPath, adoptAllText, "planned/would_commit", lockHeld: true);
        Check(Same(lockedBefore, Files(locked)) && lockedDir == DirTime(locked), "a store whose lock is held: file names, bytes, mtimes and directory mtime unchanged after the hold", Names(Files(locked)));
        // Refusals: the plan-mode text, exit 1, store unchanged.
        Equal("a stale selection", store, partialPath, File.ReadAllText(partialPath), "failed/");
        Check(Code(Cli("mirror-drift-adopt", store, partialPath, "--preview").Out) == MirrorDriftAdoptCodes.StaleRevision, "a stale selection previews drift_adopt_stale_revision");
        var other = Store(head with { Id = Guid.Parse("c0000000-0000-4000-8000-0000000000ab") });
        Equal("another project's store", other, adoptAllPath, adoptAllText, "failed/");
        var fresh = Store(head);
        var notCanonical = File_(adoptAllText + "\n", "selection");
        Equal("a non-canonical selection", fresh, notCanonical, adoptAllText + "\n", "failed/");
        var empty = Path.Combine(scratch, "preview-empty-store");
        Directory.CreateDirectory(empty);
        Equal("a store without project.json", empty, adoptAllPath, adoptAllText, "failed/");
        // Selection-file refusals are the CLI's own bounded-input envelopes (as without the flag).
        var missing = Cli("mirror-drift-adopt", fresh, Path.Combine(scratch, "preview-missing.json"), "--preview");
        Check(missing.Exit == 1 && missing.Out == JsonSerializer.Serialize(new { status = "failed", code = "input_not_found", detail = "selection" }), "a missing selection file is input_not_found (detail selection)", R(missing));

        // Flags: exact-case --preview once, after the two positionals; unknown and repeated flags typed, refused before reading.
        var flags = Store(head);
        var flagFiles = Files(flags);
        void Flag(string name, string code, string? detail, params string[] arguments)
        {
            var r = Cli(["mirror-drift-adopt", .. arguments]);
            var expected = detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail });
            Check(r.Exit == 1 && r.Out == expected && Same(flagFiles, Files(flags)), name + " (" + code + ")", R(r) + " expected " + expected);
        }
        Flag("an unknown flag", "drift_adopt_option_unknown", "--force", flags, adoptAllPath, "--force");
        Flag("a wrong-case --Preview", "drift_adopt_option_unknown", "--Preview", flags, adoptAllPath, "--Preview");
        Flag("--preview=true", "drift_adopt_option_unknown", "--preview=true", flags, adoptAllPath, "--preview=true");
        Flag("an unknown flag with a missing selection is refused before reading", "drift_adopt_option_unknown", "--force", flags, Path.Combine(scratch, "preview-missing.json"), "--force");
        Flag("a repeated --preview", "drift_adopt_option_duplicate", "--preview", flags, adoptAllPath, "--preview", "--preview");
        Flag("an unknown flag after --preview", "drift_adopt_option_unknown", "--force", flags, adoptAllPath, "--preview", "--force");
        var stray = Cli("mirror-drift-adopt", flags, adoptAllPath, "--preview", "extra");
        Check(stray.Exit == 1 && Code(stray.Out) is { } s && s.StartsWith("usage: mirror-drift-adopt store-directory selection.json", StringComparison.Ordinal) && Same(flagFiles, Files(flags)), "a non-flag argument after --preview prints the usage", R(stray));
        var front = Cli("mirror-drift-adopt", "--preview", flags, adoptAllPath);
        Check(front.Exit == 1 && Code(front.Out) is { } f && f.StartsWith("usage: mirror-drift-adopt", StringComparison.Ordinal) && Same(flagFiles, Files(flags)), "--preview before the positionals prints the usage (flags follow store and selection)", R(front));
        Check(new ProjectStore(flags).Read().Revision == 3, "no flag refusal committed anything");
    }
}
