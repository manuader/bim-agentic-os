using Bim.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
// P2-05 query.journal contract oracle: the read-only catalogue query that exposes the P2-03 external edit journal
// (the raw pipe Operation journal.read) through the catalogue, the MCP tool list and the permission tables. Covers
// the schema and the typed argument rules (peek, after_sequence >= 0, max_entries 1..256, refused before the pipe),
// that the bound arguments are exactly the journal.read options, that the catalogue reply body is byte-identical to the
// journal.read reply after its status and verification members (same tracker semantics: peek keeps the baseline, a
// read moves it, paging), and the guarded client path (fake transport). No Revit is involved: a passing run is
// unit-tested evidence only; CommandsExecutorJournal.cs is compiled, never run.
static class CatalogueJournalChecks
{
    const string Doc = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string L1 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000137";
    const string W1 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00001001";
    const string W2 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00001002";
    const string W3 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00001003";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";
    const string RawPrefix = "{\"status\":\"succeeded\",\"verification\":\"unverified\",";

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static JsonObject Envelope(JsonNode args, string path = FixturePath) => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-journal",
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Doc },
        ["command"] = "query.journal",
        ["args"] = args.DeepClone(),
    };

    static RevitCommandException? Thrown(Action action)
    {
        try { action(); return null; }
        catch (RevitCommandException error) { return error; }
        catch (Exception error) { return new RevitCommandException("unexpected " + error.GetType().Name, null, error.Message); }
    }
    static JournalQueryArgs? Bound(string name, JsonNode args)
    {
        try
        {
            var parsed = RevitCommandProtocol.Parse(Envelope(args).ToJsonString()).Args as JournalQueryArgs;
            Check(parsed is not null, name, "bound to another args type");
            return parsed;
        }
        catch (Exception error) { Check(false, name, error.GetType().Name + " " + (error is RevitCommandException typed ? typed.Code + " at " + typed.Field : error.Message)); return null; }
    }
    static void Rejects(string name, JsonNode args, string field)
    {
        var error = Thrown(() => RevitCommandProtocol.Parse(Envelope(args).ToJsonString()));
        Check(error is not null && error.Code == RevitCommandCodes.ArgsInvalid && error.Field == field, name, error is null ? "accepted" : error.Code + " at " + error.Field + " (" + error.Detail + ")");
    }

    // A fixed clock so two journals fed the same changes serialize identically.
    static MirrorJournalTracker Tracker()
    {
        var start = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        var ticks = 0;
        return new MirrorJournalTracker(new MirrorJournal(() => start.AddSeconds(ticks++)));
    }
    static MirrorObservedElement Wall(string uid, double length) =>
        new(MirrorCategories.Wall, uid) { LevelUid = L1, Start = new MirrorPoint(0, 0), End = new MirrorPoint(length, 0), HeightMm = 3000, ThicknessMm = 200, WallKind = "Basic", LocationLineIsCenterline = true, BaseOffsetMm = 0 };
    static MirrorSnapshot Snapshot(params MirrorObservedElement[] elements) =>
        MirrorSnapshotMapper.Map(new MirrorObservation(Doc, new[] { new MirrorObservedLevel(L1, "Level 1", 0) }, elements));
    static void Edit(MirrorJournalTracker tracker, string[] added, string[]? modified = null, string transaction = "Wall") =>
        tracker.Journal.Record(new MirrorJournalChange(Doc, "TransactionCommitted", new[] { transaction }, added, modified ?? Array.Empty<string>(), Array.Empty<long>()) { DocumentPath = FixturePath });

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        Catalogue();
        Arguments();
        OptionsMirror();
        ReplyMirror();
        Golden();
        Semantics();
        DriftInput();
        ClientPath();
        ReadOnlySource();
    }

    static void Catalogue()
    {
        Check(RevitCommandCatalogue.Journal.Select(spec => spec.Name).SequenceEqual(new[] { "query.journal" }), "journal catalogue part lists query.journal only", string.Join(",", RevitCommandCatalogue.Journal.Select(spec => spec.Name)));
        Check(RevitCommandCatalogue.Mirror.Select(spec => spec.Name).SequenceEqual(new[] { "query.snapshot", "query.journal" }), "the mirror catalogue part is query.snapshot then query.journal", string.Join(",", RevitCommandCatalogue.Mirror.Select(spec => spec.Name)));
        var spec = RevitCommandCatalogue.Find("query.journal");
        Check(spec is { Writes: false, ToolName: "revit_query_journal" } && RevitCommandCatalogue.All.Contains(spec) && ReferenceEquals(spec, RevitCommandCatalogue.Journal.FirstOrDefault()), "query.journal is a read-only catalogue command with tool revit_query_journal that Find resolves");
        Check(RevitCommandCatalogue.All.Select(s => s.Name).Distinct().Count() == RevitCommandCatalogue.All.Count && RevitCommandCatalogue.All.Select(s => s.ToolName).Distinct().Count() == RevitCommandCatalogue.All.Count, "command and tool names stay distinct with query.journal");
        Check(!RevitCommandCatalogue.IsDestructive("query.journal"), "query.journal is not destructive");
        Check(spec is not null && spec.Description.EndsWith('.') && spec.Description.Contains("Read-only", StringComparison.Ordinal) && spec.Description.Contains("journal.read", StringComparison.Ordinal)
            && spec.Description.Contains("peek", StringComparison.Ordinal) && spec.Description.Contains("after_sequence", StringComparison.Ordinal) && spec.Description.Contains("max_entries", StringComparison.Ordinal),
            "query.journal description says Read-only, names journal.read and explains peek, after_sequence and max_entries", spec?.Description ?? "");
        // The baseline is shared with journal.read and every other client: say so first, and recommend peek.
        Check(spec is not null && spec.Description.Contains("A read without peek consumes the baseline shared with journal.read and every other client; pass peek: true unless you own that baseline.", StringComparison.Ordinal)
            && spec.Description.IndexOf("consumes the baseline", StringComparison.Ordinal) < spec.Description.IndexOf("Arguments:", StringComparison.Ordinal),
            "query.journal description warns, before the arguments, that a read without peek consumes the baseline shared with journal.read and recommends peek: true", spec?.Description ?? "");
        string? problem = null;
        try
        {
            using var schema = JsonDocument.Parse(spec?.ArgsSchemaJson ?? "null");
            RevitCommandSchema.CheckSupported(schema.RootElement);
            var root = schema.RootElement;
            if (root.GetProperty("type").GetString() != "object") problem = "not an object schema";
            else if (root.TryGetProperty("required", out var required) && required.GetArrayLength() > 0) problem = "every argument must be optional";
            else if (string.Join(",", root.GetProperty("properties").EnumerateObject().Select(p => p.Name + ":" + p.Value.GetProperty("type").GetString())) != "peek:boolean,after_sequence:integer,max_entries:integer") problem = "properties " + root.GetProperty("properties").GetRawText();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) { problem = error.Message; }
        Check(problem is null, "query.journal args schema: optional peek boolean, after_sequence and max_entries integers, only enforced keywords", problem ?? "");
        var copies = new[] { @"C:\Repo\.bim\native-runs" };
        Check(PermissionPolicy.RequiredLevel("query.journal", FixturePath, copies) == PermissionLevel.Read && PermissionPolicy.RequiredLevel("query.journal", RealPath, copies) == PermissionLevel.Read, "query.journal requires read on a copy and on a real project");
        Check(MirrorJournalReadOptions.DefaultMaxEntries == 256, "the max_entries cap is journal.read's 256");
    }

    static void Arguments()
    {
        Check(Bound("query.journal with {} binds", new JsonObject()) is { AfterSequence: null, Peek: false, MaxEntries: MirrorJournalReadOptions.DefaultMaxEntries }, "{} binds the journal.read defaults: baseline, no peek, 256 entries");
        Check(Bound("query.journal with every argument binds", new JsonObject { ["peek"] = true, ["after_sequence"] = 7, ["max_entries"] = 3 }) is { AfterSequence: 7, Peek: true, MaxEntries: 3 }, "peek, after_sequence and max_entries bind as given");
        Check(Bound("after_sequence 0 binds", new JsonObject { ["after_sequence"] = 0 }) is { AfterSequence: 0 }, "after_sequence 0 (from the start) is accepted");
        Check(Bound("max_entries 1 binds", new JsonObject { ["max_entries"] = 1 }) is { MaxEntries: 1 } && Bound("max_entries 256 binds", new JsonObject { ["max_entries"] = 256 }) is { MaxEntries: 256 }, "max_entries bounds 1 and 256 are accepted");
        Check(Bound("peek false binds", new JsonObject { ["peek"] = false }) is { Peek: false }, "peek false is accepted");
        Rejects("negative after_sequence refused", new JsonObject { ["after_sequence"] = -1 }, "args.after_sequence");
        Rejects("fractional after_sequence refused", new JsonObject { ["after_sequence"] = 1.5 }, "args.after_sequence");
        Rejects("after_sequence beyond Int64 refused", JsonNode.Parse("""{"after_sequence":1e30}""")!, "args.after_sequence");
        Rejects("text after_sequence refused", new JsonObject { ["after_sequence"] = "3" }, "args.after_sequence");
        Rejects("max_entries 0 refused", new JsonObject { ["max_entries"] = 0 }, "args.max_entries");
        Rejects("max_entries 257 refused", new JsonObject { ["max_entries"] = 257 }, "args.max_entries");
        Rejects("negative max_entries refused", new JsonObject { ["max_entries"] = -5 }, "args.max_entries");
        Rejects("max_entries beyond Int32 refused", new JsonObject { ["max_entries"] = 4294967296L }, "args.max_entries");
        Rejects("fractional max_entries refused", new JsonObject { ["max_entries"] = 2.5 }, "args.max_entries");
        Rejects("text peek refused", new JsonObject { ["peek"] = "true" }, "args.peek");
        Rejects("null peek refused", new JsonObject { ["peek"] = null }, "args.peek");
        Rejects("unknown member refused", new JsonObject { ["document"] = "x" }, "args.document");
        Rejects("journal.read's internal max_ids is not a catalogue argument", new JsonObject { ["max_ids"] = 10 }, "args.max_ids");
    }

    // The catalogue arguments are exactly journal.read's Request.Journal options: same accepted set, same values.
    static void OptionsMirror()
    {
        foreach (var raw in new[] { "{}", """{"peek":true}""", """{"after_sequence":0}""", """{"after_sequence":42,"peek":true,"max_entries":1}""", """{"max_entries":256}""", """{"peek":false,"max_entries":17}""" })
        {
            var bound = Bound("query.journal binds " + raw, JsonNode.Parse(raw)!);
            MirrorJournalReadOptions? fromCatalogue = null;
            try { fromCatalogue = bound?.Options; } catch (Exception error) { Check(false, "query.journal options for " + raw, error.Message); }
            Check(fromCatalogue is not null && fromCatalogue == MirrorJournalReadOptions.Parse(raw), "query.journal " + raw + " gives the same options as journal.read", fromCatalogue + " vs " + MirrorJournalReadOptions.Parse(raw));
        }
        foreach (var raw in new[] { """{"after_sequence":-1}""", """{"max_entries":0}""", """{"max_entries":257}""", """{"peek":"yes"}""", """{"max_ids":5}""", """{"x":1}""" })
        {
            var rawRefused = false;
            try { MirrorJournalReadOptions.Parse(raw); } catch (ArgumentException) { rawRefused = true; }
            var catalogueRefused = Thrown(() => RevitCommandProtocol.Parse(Envelope(JsonNode.Parse(raw)!).ToJsonString())) is { Code: RevitCommandCodes.ArgsInvalid };
            Check(rawRefused && catalogueRefused, "journal.read and query.journal both refuse " + raw, "raw " + rawRefused + ", catalogue " + catalogueRefused);
        }
    }

    // Two trackers fed the same changes: the journal.read reply and the query.journal readback body agree byte for byte.
    static void ReplyMirror()
    {
        var raw = Tracker();
        var catalogue = Tracker();
        var options = new[]
        {
            ("first read (no baseline)", "{}"),
            ("peek after two edits", """{"peek":true}"""),
            ("paged peek", """{"peek":true,"max_entries":1}"""),
            ("page two", """{"peek":true,"after_sequence":1,"max_entries":1}"""),
            ("read that moves the baseline", "{}"),
            ("empty read", "{}"),
        };
        var step = 0;
        foreach (var (name, json) in options)
        {
            if (step == 1) foreach (var tracker in new[] { raw, catalogue }) { Edit(tracker, new[] { W2 }); Edit(tracker, new[] { W3 }, new[] { W1 }, "Move"); }
            var snapshot = step == 0 ? Snapshot(Wall(W1, 5000)) : Snapshot(Wall(W1, 6000), Wall(W2, 4000), Wall(W3, 3000));
            string? rawReply = null, body = null;
            try
            {
                rawReply = RevitJournalQuery.RawRead(raw, true, snapshot, FixturePath, MirrorJournalReadOptions.Parse(json));
                var args = RevitCommandProtocol.Parse(Envelope(JsonNode.Parse(json)!).ToJsonString()).Args as JournalQueryArgs;
                body = args is null ? null : RevitJournalQuery.Read(catalogue, true, snapshot, FixturePath, args);
            }
            catch (Exception error) { Check(false, "reply mirror " + name, error.GetType().Name + " " + error.Message); step++; continue; }
            Check(rawReply is not null && body is not null && body.StartsWith('{') && rawReply == RawPrefix + body[1..], "reply mirror " + name + ": query.journal readback is the journal.read reply after status and verification", rawReply + " | " + body);
            step++;
        }
    }

    // Independent oracle for the journal.read reply bytes: a hand-written literal for a fixed tracker and clock (one
    // external edit, a peek after a baseline read of the same snapshot, empty document path so nothing is escaped), and
    // the member order of the P2-04 fixture tests/mirror-journal/fixtures/drift/journal-read-reply.json.
    const string GoldenReply = "{\"status\":\"succeeded\",\"verification\":\"unverified\",\"attached\":true,\"dropped\":0,\"skipped_add_in\":0,\"failures\":0,\"unidentified\":0,"
        + "\"report\":{\"journal_version\":1,\"document_unique_id\":\"60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b\",\"document_path\":\"\",\"baseline_sequence\":0,\"current_sequence\":1,\"after_sequence\":0,"
        + "\"baseline_existed\":true,\"peek\":true,\"journal_complete\":true,\"failures_since_baseline\":0,\"has_more\":false,\"next_after_sequence\":1,"
        + "\"entries\":[{\"sequence\":1,\"recorded_at\":\"2026-11-04T12:00:00.000Z\",\"document_unique_id\":\"60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b\",\"document_path\":\"\",\"operation\":\"TransactionCommitted\",\"origin\":\"external\","
        + "\"transaction_names\":[\"Wall\"],\"added\":[\"60f91daf-3dd7-4283-a86d-24137b73f3da-00001002\"],\"modified\":[],\"deleted_element_ids\":[],\"added_total\":1,\"modified_total\":0,\"deleted_total\":0,\"truncated\":false}],"
        + "\"diff\":{\"diff_version\":1,\"document_unique_id\":\"60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b\",\"added\":[],\"removed\":[],\"changed\":[]}}}";

    static (string? Raw, string? Body) GoldenReads()
    {
        string? ReadOnce(bool raw)
        {
            var tracker = Tracker();
            var snapshot = Snapshot();
            tracker.Report(snapshot, "");
            tracker.Journal.Record(new MirrorJournalChange(Doc, "TransactionCommitted", new[] { "Wall" }, new[] { W2 }, Array.Empty<string>(), Array.Empty<long>()));
            return raw ? RevitJournalQuery.RawRead(tracker, true, snapshot, "", new MirrorJournalReadOptions(Peek: true))
                : RevitJournalQuery.Read(tracker, true, snapshot, "", new JournalQueryArgs(null, true, MirrorJournalReadOptions.DefaultMaxEntries));
        }
        try { return (ReadOnce(true), ReadOnce(false)); }
        catch (Exception error) { Check(false, "golden reads", error.GetType().Name + " " + error.Message); return (null, null); }
    }

    static string Keys(JsonNode? node) => node is JsonObject obj ? string.Join(",", obj.Select(member => member.Key)) : "not an object";

    static void Golden()
    {
        var (raw, body) = GoldenReads();
        Check(raw == GoldenReply, "the journal.read reply equals the hand-written golden byte for byte", raw ?? "null");
        Check(body is not null && body == "{" + GoldenReply[RawPrefix.Length..], "the query.journal readback equals the golden after status and verification", body ?? "null");
        var root = RevitCommandPolicy.RepositoryRoot(AppContext.BaseDirectory);
        var path = root is null ? null : Path.Combine(root, "tests", "mirror-journal", "fixtures", "drift", "journal-read-reply.json");
        var fixture = path is not null && File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        var golden = JsonNode.Parse(GoldenReply);
        Check(fixture is not null && Keys(golden) == Keys(fixture), "golden reply members are in the P2-04 journal-read-reply.json fixture order", Keys(golden) + " vs " + Keys(fixture));
        Check(fixture is not null && Keys(golden?["report"]) == Keys(fixture["report"]), "golden report members are in the fixture order", Keys(golden?["report"]) + " vs " + Keys(fixture?["report"]));
        var fixtureEntry = fixture?["report"]?["entries"]?.AsArray().FirstOrDefault();
        Check(fixtureEntry is not null && Keys(golden?["report"]?["entries"]?[0]) == Keys(fixtureEntry), "golden entry members are in the fixture order", Keys(golden?["report"]?["entries"]?[0]) + " vs " + Keys(fixtureEntry));
        Check(fixture is not null && Keys(golden?["report"]?["diff"]) == Keys(fixture["report"]?["diff"]), "golden diff members are in the fixture order", Keys(golden?["report"]?["diff"]) + " vs " + Keys(fixture?["report"]?["diff"]));
    }

    // P2-04 drift input: MirrorDrift.ParseJournal takes the journal.read reply (or a bare report). The query.journal
    // readback has neither status nor top-level entries, so on its own it is refused drift_journal_invalid; wrapped by
    // RevitJournalQuery.AsJournalReadReply it parses exactly like the journal.read reply (attached, dropped and failures kept).
    static void DriftInput()
    {
        var (raw, body) = GoldenReads();
        if (raw is null || body is null) { Check(false, "drift input needs the golden reads"); return; }
        string? refused = null;
        try { MirrorDrift.ParseJournal(body); } catch (ArgumentException error) { refused = error.Message.Split(' ')[0]; }
        Check(refused == MirrorDrift.JournalInvalidCode, "MirrorDrift.ParseJournal refuses the bare query.journal readback with drift_journal_invalid", refused ?? "accepted");
        string? wrapped = null;
        try { wrapped = RevitJournalQuery.AsJournalReadReply(body); } catch (Exception error) { Check(false, "AsJournalReadReply", error.GetType().Name + " " + error.Message); }
        Check(wrapped == raw, "AsJournalReadReply turns the readback back into the journal.read reply bytes", wrapped ?? "null");
        try
        {
            var fromRaw = MirrorDrift.ParseJournal(raw);
            var fromQuery = MirrorDrift.ParseJournal(wrapped ?? "");
            Check(fromQuery.Entries.Select(entry => entry.Sequence).SequenceEqual(fromRaw.Entries.Select(entry => entry.Sequence)) && fromQuery.Entries.Count == 1 && fromQuery.Complete == fromRaw.Complete && fromQuery.HasMore == fromRaw.HasMore,
                "MirrorDrift.ParseJournal reads the wrapped query.journal readback like the journal.read reply");
        }
        catch (Exception error) { Check(false, "MirrorDrift.ParseJournal reads the wrapped query.journal readback like the journal.read reply", error.GetType().Name + " " + error.Message); }
        foreach (var bad in new[] { "", "[]", "{\"status\":\"succeeded\"}", "{" })
        {
            string? code = null;
            try { RevitJournalQuery.AsJournalReadReply(bad); } catch (ArgumentException error) { code = error.Message.Split(' ')[0]; } catch (Exception error) { code = "unexpected " + error.GetType().Name; }
            Check(code == RevitJournalQuery.ReadbackInvalidCode, "AsJournalReadReply refuses " + (bad.Length == 0 ? "an empty string" : bad) + " with journal_readback_invalid", code ?? "accepted");
        }
    }

    // Like the query.snapshot collector scan (MirrorSnapshot.cs): the executor source opens no transaction and writes nothing.
    static void ReadOnlySource()
    {
        var root = RevitCommandPolicy.RepositoryRoot(AppContext.BaseDirectory);
        var source = root is null ? null : Path.Combine(root, "runtimes", "revit-native", "CommandsExecutorJournal.cs");
        var text = source is not null && File.Exists(source) ? File.ReadAllText(source) : null;
        var write = text is null ? null : System.Text.RegularExpressions.Regex.Match(text, @"\b(Transaction|TransactionGroup|SubTransaction)\b|\.Set\(|Regenerate|\.Delete\(|\.Create\.|DataStorage|Persist\(|ReadReceipts");
        Check(text is not null && !write!.Success, "the query.journal executor source has no Transaction, TransactionGroup, SubTransaction, .Set(, Regenerate, .Delete(, document creation or receipt store call", text is null ? "source not found" : write!.Value);
        Check(text is not null && text.Contains("RevitJournalQuery.Read(JournalRecorder.Tracker", StringComparison.Ordinal), "the executor reads the recorder's own tracker through RevitJournalQuery.Read", text is null ? "source not found" : "");
    }

    static JsonObject Body(MirrorJournalTracker tracker, MirrorSnapshot snapshot, JsonNode args) =>
        JsonNode.Parse(RevitJournalQuery.Read(tracker, false, snapshot, FixturePath, (JournalQueryArgs)RevitCommandProtocol.Parse(Envelope(args).ToJsonString()).Args))!.AsObject();

    static void Semantics()
    {
        try
        {
            var tracker = Tracker();
            var first = Body(tracker, Snapshot(Wall(W1, 5000)), new JsonObject());
            Check(string.Join(",", first.Select(member => member.Key)) == "attached,dropped,skipped_add_in,failures,unidentified,report", "readback members in a fixed order: attached, dropped, skipped_add_in, failures, unidentified, report", string.Join(",", first.Select(member => member.Key)));
            Check(first["attached"]!.GetValue<bool>() == false && first["report"]!["baseline_existed"]!.GetValue<bool>() == false && first["report"]!["journal_version"]!.GetValue<int>() == MirrorJournal.Version, "first read reports no baseline and journal_version 1");
            Edit(tracker, new[] { W2 });
            Edit(tracker, new[] { W3 });
            Edit(tracker, Array.Empty<string>(), new[] { W1 }, "Move");
            tracker.Journal.Record(new MirrorJournalChange(Doc, "TransactionCommitted", new[] { "BIM Agentic OS wall.create" }, new[] { W2 }, Array.Empty<string>(), Array.Empty<long>()) { DocumentPath = FixturePath });
            var after = Snapshot(Wall(W1, 6000), Wall(W2, 4000), Wall(W3, 3000));
            var peek = RevitJournalQuery.Read(tracker, false, after, FixturePath, new JournalQueryArgs(null, true, 2));
            var again = RevitJournalQuery.Read(tracker, false, after, FixturePath, new JournalQueryArgs(null, true, 2));
            var peeked = JsonNode.Parse(peek)!;
            Check(peek == again, "two peeks answer byte for byte the same (the baseline stays)");
            Check(peeked["skipped_add_in"]!.GetValue<long>() == 1 && peeked["report"]!["entries"]!.AsArray().Count == 2 && peeked["report"]!["has_more"]!.GetValue<bool>() && peeked["report"]!["next_after_sequence"]!.GetValue<long>() == 2 && peeked["report"]!["peek"]!.GetValue<bool>(),
                "max_entries 2 pages the three external edits: two entries, has_more, next_after_sequence 2; the add-in commit is skipped", peek);
            var page2 = JsonNode.Parse(RevitJournalQuery.Read(tracker, false, after, FixturePath, new JournalQueryArgs(2, true, 2)))!;
            Check(page2["report"]!["entries"]!.AsArray().Select(entry => entry!["sequence"]!.GetValue<long>()).SequenceEqual(new[] { 3L }) && !page2["report"]!["has_more"]!.GetValue<bool>() && page2["report"]!["next_after_sequence"]!.GetValue<long>() == 3, "after_sequence 2 continues with the last entry and no more pages");
            var beyond = Thrown(() => RevitJournalQuery.Read(tracker, false, after, FixturePath, new JournalQueryArgs(4, true, 10)));
            Check(beyond is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.after_sequence" }, "an after_sequence past the journal's last sequence is args_invalid at args.after_sequence", beyond is null ? "accepted" : beyond.Code + " at " + beyond.Field);
            var read = JsonNode.Parse(RevitJournalQuery.Read(tracker, false, after, FixturePath, new JournalQueryArgs(null, false, 256)))!;
            Check(read["report"]!["entries"]!.AsArray().Count == 3 && !read["report"]!["peek"]!.GetValue<bool>() && read["report"]!["diff"] is JsonObject, "a read without peek returns every external entry and the snapshot diff");
            var empty = JsonNode.Parse(RevitJournalQuery.Read(tracker, false, after, FixturePath, new JournalQueryArgs(null, true, 256)))!;
            Check(empty["report"]!["baseline_existed"]!.GetValue<bool>() && empty["report"]!["entries"]!.AsArray().Count == 0 && empty["report"]!["baseline_sequence"]!.GetValue<long>() == 3, "the read moved the baseline: the next peek is empty from sequence 3");
            var replay = Thrown(() => RevitJournalQuery.Read(tracker, false, after, FixturePath, new JournalQueryArgs(0, true, 256)));
            Check(replay is null, "after_sequence 0 re-reads from the start after the baseline moved", replay?.Code ?? "");
        }
        catch (Exception error) { Check(false, "query.journal semantics", error.GetType().Name + " " + error.Message); }
    }

    static void ClientPath()
    {
        var policy = new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" });
        var forwarded = new List<string>();
        string? Transport(string pipe, string line) { forwarded.Add(line); return null; }
        var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
        var args = new JsonObject { ["peek"] = true, ["max_entries"] = 10 };
        RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(args, RealPath).ToJsonString(), policy, Transport, noLinks);
        Check(forwarded.Count == 1 && forwarded[0].Contains("query.journal", StringComparison.Ordinal), "query.journal is read-only and forwarded even on a real project");
        string? expected = null;
        try { expected = RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(Envelope(args, RealPath).ToJsonString())); } catch (Exception error) { expected = error.Message; }
        Check(forwarded.Count == 1 && forwarded[0] == expected, "query.journal is forwarded as the canonical wire line");
        foreach (var bad in new[] { new JsonObject { ["max_entries"] = 0 }, new JsonObject { ["after_sequence"] = -2 }, new JsonObject { ["peek"] = 1 } })
        {
            var refused = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(bad, FixturePath).ToJsonString(), policy, Transport, noLinks);
            Check(refused.IsError && !refused.Forwarded && JsonNode.Parse(refused.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.ArgsInvalid && forwarded.Count == 1, "query.journal " + bad.ToJsonString() + " is args_invalid before the pipe", refused.Line);
        }
    }
}
