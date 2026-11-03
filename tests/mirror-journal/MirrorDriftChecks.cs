using Bim.Core;
using System.Text.Json;
// P2-04 drift oracle: MirrorDrift.Compare (core/Bim.Core/MirrorDrift.cs) of a query.snapshot against the Core store's
// committed BIM-IR for the same document, classified with journal entries and add-in receipts. Fixtures live in
// tests/mirror-journal/fixtures/drift (observed records, the query.snapshot reply, the committed model, a journal.read
// reply). No Revit is involved: a passing run is unit-tested evidence only.
static class MirrorDriftChecks
{
    const string Doc = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string OtherDoc = "11111111-2222-3333-4444-555555555555-00000001";
    const string W1 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00001001";
    const string W2 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00001002";
    const string W3 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00001003";
    const string D1 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00002001";
    const string F1 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00004001";
    static readonly Guid CoreW2 = Guid.Parse("c0000000-0000-4000-8000-000000000003");
    static readonly Guid CoreD1 = Guid.Parse("c0000000-0000-4000-8000-000000000004");

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, "drift: " + name, detail);
    static string? Code(Action action)
    {
        try { action(); return null; }
        catch (ArgumentException error) { return error.Message.Split(' ')[0]; }
        catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
    }

    static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        try { Checks(); }
        catch (Exception error) { Check(false, "oracle threw", error.GetType().Name + ": " + error.Message); }
    }

    static MirrorObservedElement Wall(MirrorObservedElement template, string uid, double ax, double ay, double bx, double by) =>
        template with { UniqueId = uid, Start = new MirrorPoint(ax, ay), End = new MirrorPoint(bx, by) };

    static void Checks()
    {
        var repo = RevitCommandPolicy.RepositoryRoot(AppContext.BaseDirectory);
        var dir = repo is null ? "" : Path.Combine(repo, "tests", "mirror-journal", "fixtures", "drift");
        Check(Directory.Exists(dir), "fixture directory exists", dir);
        if (!Directory.Exists(dir)) return;
        string Read(string name) => File.ReadAllText(Path.Combine(dir, name));
        var observation = JsonSerializer.Deserialize<MirrorObservation>(Read("observation.json"), Snake)!;
        var committed = MirrorCommittedModel.Parse(Read("committed.json"));
        var journal = MirrorDrift.ParseJournalEntries(Read("journal.json"));
        Check(observation.DocumentUid == Doc && observation.Elements.Count == 4 && observation.Levels.Count == 1, "observation fixture loads", observation.Elements.Count.ToString());
        Check(committed.DocumentUid == Doc && committed.Project.Revision == 3 && committed.Project.Elements.Length == 4 && committed.NativeIds.Count == 4 && committed.IgnoredMapKeys.SequenceEqual(new[] { "door-symbol" }), "committed fixture loads; a non-Guid map key is ignored and listed");
        Check(journal.Count == 5 && journal[2].Origin == MirrorJournalOrigins.AddIn && journal[3].DeletedIds.SequenceEqual(new long[] { 0x2001 }), "journal fixture loads", journal.Count.ToString());
        MirrorSnapshot Snap(params MirrorObservedElement[] elements) => MirrorSnapshotMapper.Map(observation with { Elements = elements });
        var wall1 = observation.Elements.Single(element => element.UniqueId == W1);
        var wall2 = observation.Elements.Single(element => element.UniqueId == W2);
        var door = observation.Elements.Single(element => element.UniqueId == D1);
        var floor = observation.Elements.Single(element => element.UniqueId == F1);
        var baseline = MirrorSnapshotMapper.Map(observation);

        // ---- Identical: no drift ----
        var identical = MirrorDrift.Compare(baseline, committed);
        Check(!identical.HasDrift && identical.Findings.Count == 0 && identical.Summary.Unchanged == 4 && identical.Summary.Unmapped == 1,
            "the committed model realized unchanged in Revit has no drift", identical.ToJson());
        Check(identical.DocumentUid == Doc && identical.CoreRevision == 3 && identical.CoreProjectId == committed.Project.Id && !identical.Truncated, "report carries the document identity and Core revision");
        // The query.snapshot reply read back from JSON compares exactly like the mapped observation.
        var reply = MirrorDrift.ParseSnapshot(Read("snapshot.json"));
        Check(MirrorDrift.Compare(reply, committed).ToJson() == identical.ToJson(), "the query.snapshot reply fixture gives the same report as the mapped observation", MirrorDrift.Compare(reply, committed).ToJson());
        Check(MirrorDrift.Compare(MirrorDrift.ParseSnapshot(baseline.ToJson()), committed).ToJson() == identical.ToJson(), "a snapshot round-tripped through its JSON reply gives the same report");

        // ---- Moved wall beyond tolerance: changed with fields ----
        var moved = MirrorDrift.Compare(Snap(wall1, Wall(wall2, W2, 5050, 0, 5050, 4000), door, floor), committed);
        var w2 = moved.Findings.SingleOrDefault();
        Check(w2 is { Kind: MirrorDriftKinds.Changed, UniqueId: W2, ElementKind: "wall", Classification: MirrorDriftClasses.Unknown } && w2.CoreId == CoreW2 && w2.SnapshotId == MirrorSnapshotMapper.Id(Doc, W2),
            "a wall moved 50 mm is changed, unknown without evidence", moved.ToJson());
        Check(w2 is not null && w2.Fields.Select(field => field.Field).SequenceEqual(new[] { "ax", "bx" })
            && w2.Fields[0] == new MirrorDriftField("ax", "5.000000", "5.050000", "50.000") && w2.Fields[1] == new MirrorDriftField("bx", "5.000000", "5.050000", "50.000"),
            "the moved wall lists exactly ax and bx in fixed-format metres with a millimetre delta", w2 is null ? "" : string.Join(";", w2.Fields));
        Check(moved.Summary is { Changed: 1, Missing: 0, Extra: 0, Unchanged: 3, Unknown: 1, Drift: 1 }, "summary counts the changed wall", moved.Summary.ToString());

        // ---- Within tolerance: unchanged ----
        var nudged = MirrorDrift.Compare(Snap(wall1, Wall(wall2, W2, 5000.5, 0, 5000.5, 4000), door, floor), committed);
        Check(!nudged.HasDrift && nudged.Summary.Unchanged == 4, "a wall moved 0.5 mm is within the 1 mm default tolerance", nudged.ToJson());
        var strict = MirrorDrift.Compare(Snap(wall1, Wall(wall2, W2, 5000.5, 0, 5000.5, 4000), door, floor), committed, options: new MirrorDriftOptions(LengthToleranceMm: 0.1));
        Check(strict.Summary.Changed == 1, "the same 0.5 mm is a change with a 0.1 mm tolerance", strict.ToJson());
        // Angle tolerance: a 50 mm end shift on a 5 m wall (0.57 deg) with a 100 mm length tolerance is only a direction change.
        var turned = MirrorDrift.Compare(Snap(Wall(wall1, W1, 0, 0, 5000, 50), wall2, door, floor), committed, options: new MirrorDriftOptions(LengthToleranceMm: 100));
        // The door hosted on W1 turns with it, so it is a direction change too.
        var w1 = turned.Findings.SingleOrDefault(finding => finding.UniqueId == W1);
        Check(turned.Summary.Changed == 2 && turned.Findings.Any(finding => finding.UniqueId == D1) && w1 is not null && w1.Fields.Select(field => field.Field).SequenceEqual(new[] { "direction" }) && w1.Fields[0].Core == "0.0000" && w1.Fields[0].Delta == "0.5729",
            "a rotation beyond the angle tolerance is a direction change in degrees", turned.ToJson());
        var turnedLoose = MirrorDrift.Compare(Snap(Wall(wall1, W1, 0, 0, 5000, 50), wall2, door, floor), committed, options: new MirrorDriftOptions(LengthToleranceMm: 100, AngleToleranceDeg: 1));
        Check(!turnedLoose.HasDrift, "the same rotation within a 1 degree tolerance is unchanged", turnedLoose.ToJson());

        // ---- Deleted in Revit: missing ----
        var deleted = MirrorDrift.Compare(Snap(wall1, wall2, floor), committed);
        var d1 = deleted.Findings.SingleOrDefault();
        Check(d1 is { Kind: MirrorDriftKinds.Missing, UniqueId: D1, ElementKind: "door", SnapshotId: null, Classification: MirrorDriftClasses.Unknown } && d1.CoreId == CoreD1,
            "a door deleted in Revit is missing", deleted.ToJson());
        var deletedWithJournal = MirrorDrift.Compare(Snap(wall1, wall2, floor), committed, journal, new MirrorDriftOptions(JournalAfterSequence: 4));
        var d1j = deletedWithJournal.Findings.SingleOrDefault();
        Check(d1j is { Classification: MirrorDriftClasses.ExternalEdit } && d1j.Evidence.SequenceEqual(new[] { "journal:7:external:deleted_id_match" }),
            "an external journal deletion of the door's element id classifies it external_edit", deletedWithJournal.ToJson());
        Check(MirrorDrift.NativeElementId(D1) == 0x2001 && MirrorDrift.NativeElementId("not-a-unique-id") is null && MirrorDrift.NativeElementId(Doc) == 0x1fd0b, "the element id is read from the UniqueId suffix");

        // ---- Extra element with an external journal edit; add-in pending; the full drifted fixture ----
        var drifted = Snap(wall1, Wall(wall2, W2, 5050, 0, 5050, 4000), Wall(wall1, W3, 0, 0, 0, 4000), floor);
        var full = MirrorDrift.Compare(drifted, committed, journal, new MirrorDriftOptions(JournalAfterSequence: 4));
        var byId = full.Findings.ToDictionary(finding => finding.UniqueId);
        Check(byId.TryGetValue(W3, out var w3) && w3 is { Kind: MirrorDriftKinds.Extra, CoreId: null, ElementKind: "wall", Classification: MirrorDriftClasses.ExternalEdit } && w3.Evidence.SequenceEqual(new[] { "journal:5:external" }),
            "a wall added in Revit with an external journal entry is extra / external_edit", full.ToJson());
        Check(byId.TryGetValue(W2, out var w2p) && w2p is { Kind: MirrorDriftKinds.Changed, Classification: MirrorDriftClasses.AddInPending } && w2p.Evidence.SequenceEqual(new[] { "journal:6:add-in" }),
            "a wall changed by an add-in journal entry after Core's commit is addin_pending", full.ToJson());
        Check(full.Findings.Select(finding => (finding.Kind, finding.UniqueId)).SequenceEqual(new[] { (MirrorDriftKinds.Changed, W2), (MirrorDriftKinds.Extra, W3), (MirrorDriftKinds.Missing, D1) }),
            "findings are sorted by kind then UniqueId", string.Join(",", full.Findings.Select(finding => finding.Kind + ":" + finding.UniqueId)));
        Check(full.Summary is { Missing: 1, Extra: 1, Changed: 1, Unchanged: 2, Unmapped: 1, ExternalEdit: 2, AddInPending: 1, Unknown: 0, IgnoredEvidence: 1, Drift: 3 },
            "summary of the drifted fixture (the other document's journal entry is ignored)", full.Summary.ToString());
        // Entries at or before Core's committed sequence are not evidence.
        var late = MirrorDrift.Compare(drifted, committed, journal, new MirrorDriftOptions(JournalAfterSequence: 7));
        Check(late.Findings.All(finding => finding.Classification == MirrorDriftClasses.Unknown && finding.Evidence.Count == 0), "journal entries older than Core's commit are not evidence", late.ToJson());
        // Receipts: one on Core's current revision is pending; one on an older revision was absorbed and is not evidence.
        var receipts = new[]
        {
            new MirrorAddInReceipt("op-2", "wall.axis.update", Doc, 3, new[] { W2.ToUpperInvariant() }),
            new MirrorAddInReceipt("op-1", "door.create", Doc, 2, new[] { D1 }),
            new MirrorAddInReceipt("op-9", "wall.create", OtherDoc, 3, new[] { W3 }),
        };
        var withReceipts = MirrorDrift.Compare(drifted, committed, receipts: receipts);
        var r = withReceipts.Findings.ToDictionary(finding => finding.UniqueId);
        Check(r[W2] is { Classification: MirrorDriftClasses.AddInPending } && r[W2].Evidence.SequenceEqual(new[] { "receipt:op-2" }), "a receipt newer than or on Core's committed revision makes the change addin_pending", withReceipts.ToJson());
        Check(r[D1].Classification == MirrorDriftClasses.Unknown && r[W3].Classification == MirrorDriftClasses.Unknown && withReceipts.Summary.IgnoredEvidence == 1,
            "a receipt older than Core's revision is not evidence and another document's receipt is ignored", withReceipts.ToJson());
        // External evidence wins over add-in evidence for the same element.
        var both = MirrorDrift.Compare(drifted, committed, journal.Append(new MirrorJournalEntry(9, DateTimeOffset.UnixEpoch, Doc, "TransactionUndone", MirrorJournalOrigins.External,
            new[] { "Undo" }, Array.Empty<string>(), new[] { W2 }, Array.Empty<long>(), 0, 1, 0)).ToArray(), new MirrorDriftOptions(JournalAfterSequence: 4), receipts);
        var w2b = both.Findings.Single(finding => finding.UniqueId == W2);
        Check(w2b.Classification == MirrorDriftClasses.ExternalEdit && w2b.Evidence.SequenceEqual(new[] { "journal:6:add-in", "journal:9:external", "receipt:op-2" }),
            "external evidence outranks add-in evidence and every piece of evidence is listed in order", string.Join(",", w2b.Evidence));

        // ---- Unmapped: reported, never drift ----
        var curved = wall2 with { Curved = true, Start = null, End = null };
        var unmapped = MirrorDrift.Compare(Snap(wall1, curved, door, floor), committed);
        Check(!unmapped.HasDrift && unmapped.Summary.Unmapped == 2 && unmapped.Summary.Unchanged == 3, "a curved wall (unmapped) and a floor are not drift", unmapped.ToJson());
        Check(unmapped.Unmapped.Select(entry => (entry.UniqueId, entry.Reason, entry.CoreId)).SequenceEqual(new (string, string, Guid?)[] { (W2, MirrorReasons.CurvedWall, CoreW2), (F1, MirrorReasons.NoBimIrKind, null) }),
            "unmapped entries are listed with the committed element they cover", unmapped.ToJson());
        using (var parsed = JsonDocument.Parse(unmapped.ToJson()))
            Check(parsed.RootElement.GetProperty("unmapped").EnumerateArray().All(entry => entry.GetProperty("classification").GetString() == MirrorDriftClasses.Unmapped), "unmapped entries are classified unmapped in JSON");

        // ---- Identity mismatch and invalid input refused ----
        Check(Code(() => MirrorDrift.Compare(baseline, committed with { DocumentUid = OtherDoc })) == MirrorDrift.DocumentMismatchCode, "a snapshot and a committed model of different documents are refused");
        Check(MirrorDrift.Compare(baseline, committed with { DocumentUid = Doc.ToUpperInvariant() }).Summary.Drift == 0, "document identity ignores case");
        Check(Code(() => MirrorDrift.Compare(baseline, committed with { DocumentUid = " " })) == MirrorDrift.DocumentRequiredCode, "a blank document identity is refused");
        var badMap = committed.NativeIds.ToDictionary(pair => pair.Key, pair => pair.Value);
        badMap[CoreD1] = W1;
        Check(Code(() => MirrorDrift.Compare(baseline, committed with { NativeIds = badMap })) == MirrorDrift.MapInvalidCode, "two committed elements on one UniqueId are refused");
        foreach (var bad in new[] { new MirrorDriftOptions(LengthToleranceMm: -1), new MirrorDriftOptions(AngleToleranceDeg: double.NaN), new MirrorDriftOptions(MaxFindings: 0), new MirrorDriftOptions(JournalAfterSequence: -1) })
            Check(Code(() => MirrorDrift.Compare(baseline, committed, options: bad)) == MirrorDriftOptions.InvalidCode, "invalid options are refused: " + bad);
        Check(Code(() => MirrorDrift.Compare(baseline, committed, Enumerable.Repeat(journal[0], MirrorDrift.MaxJournalEntries + 1).ToArray())) == MirrorDrift.TooLargeCode, "an oversized journal is refused");
        Check(Code(() => MirrorDrift.ParseJournalEntries("{\"entries\":[{\"sequence\":0}]}")) == MirrorDrift.JournalInvalidCode && Code(() => MirrorDrift.ParseSnapshot("{}")) == MirrorDrift.SnapshotInvalidCode
            && Code(() => MirrorCommittedModel.Parse("[]")) == MirrorDrift.ModelInvalidCode, "malformed journal, snapshot and committed model JSON are refused");
        // An element committed without a native id is missing with the map as evidence.
        var unrealizedMap = committed.NativeIds.Where(pair => pair.Key != CoreD1).ToDictionary(pair => pair.Key, pair => pair.Value);
        var unrealized = MirrorDrift.Compare(Snap(wall1, wall2, floor), committed with { NativeIds = unrealizedMap });
        Check(unrealized.Findings.SingleOrDefault() is { Kind: MirrorDriftKinds.Missing, UniqueId: "", Classification: MirrorDriftClasses.Unknown } u && u.CoreId == CoreD1 && u.Evidence.SequenceEqual(new[] { "map:missing" }),
            "a committed element with no native id is missing with map evidence", unrealized.ToJson());

        ReviewChecks(observation, committed, journal, Read);

        // ---- Bounded report ----
        var capped = MirrorDrift.Compare(drifted, committed, journal, new MirrorDriftOptions(JournalAfterSequence: 4, MaxFindings: 1));
        Check(capped.Truncated && capped.Findings.Count == 1 && capped.Findings[0].UniqueId == W2 && capped.Summary.Drift == 3, "MaxFindings caps the findings, keeps true totals and says truncated", capped.ToJson());

        // ---- Byte-stable: two runs and permuted inputs ----
        var first = full.ToJson();
        Check(MirrorDrift.Compare(drifted, committed, journal, new MirrorDriftOptions(JournalAfterSequence: 4)).ToJson() == first, "two runs give byte-identical reports");
        var permutedSnapshot = MirrorSnapshotMapper.Map(observation with { Elements = new[] { floor, Wall(wall1, W3, 0, 0, 0, 4000), Wall(wall2, W2, 5050, 0, 5050, 4000), wall1 } });
        var permutedModel = committed with
        {
            Project = committed.Project with { Elements = committed.Project.Elements.Reverse().ToArray() },
            NativeIds = committed.NativeIds.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value),
        };
        var permuted = MirrorDrift.Compare(permutedSnapshot, permutedModel, journal.Reverse().ToArray(), new MirrorDriftOptions(JournalAfterSequence: 4));
        Check(permuted.ToJson() == first, "permuted snapshot, model, map and journal give byte-identical reports", permuted.ToJson());
        var receiptsPermuted = MirrorDrift.Compare(drifted, committed, receipts: receipts.Reverse().ToArray()).ToJson();
        Check(receiptsPermuted == withReceipts.ToJson(), "permuted receipts give byte-identical reports");
        using (var parsed = JsonDocument.Parse(first))
        {
            var root = parsed.RootElement;
            Check(root.GetProperty("drift_version").GetInt32() == 1 && root.GetProperty("document_unique_id").GetString() == Doc && root.GetProperty("core_revision").GetInt32() == 3
                && root.GetProperty("summary").GetProperty("drift").GetInt32() == 3 && root.GetProperty("findings").GetArrayLength() == 3, "report JSON carries version, identity, revision and summary", first);
        }
        Check(first.StartsWith("{\"drift_version\":1,\"document_unique_id\":\"" + Doc + "\",\"core_project_id\":\"a0000000-0000-4000-8000-000000000000\",\"core_revision\":3,\"options\":{\"length_tolerance_mm\":1.000,\"angle_tolerance_deg\":0.1000,\"journal_after_sequence\":4,\"max_findings\":5000},", StringComparison.Ordinal),
            "report JSON header is canonical with fixed number formatting", first);
        Check(first.Contains("{\"kind\":\"changed\",\"unique_id\":\"" + W2 + "\",\"core_id\":\"c0000000-0000-4000-8000-000000000003\",\"snapshot_id\":\"" + MirrorSnapshotMapper.Id(Doc, W2).ToString("D") + "\",\"element_kind\":\"wall\",\"classification\":\"addin_pending\",\"evidence\":[\"journal:6:add-in\"],\"fields\":[{\"field\":\"ax\",\"core\":\"5.000000\",\"revit\":\"5.050000\",\"delta\":\"50.000\"}", StringComparison.Ordinal),
            "a changed finding serializes canonically", first);
    }
    // Review fixes: wrapped journal.read replies, role-based dependency sets, non-exported kinds, evidence completeness.
    static void ReviewChecks(MirrorObservation observation, MirrorCommittedModel committed, IReadOnlyList<MirrorJournalEntry> journal, Func<string, string> read)
    {
        const string P = "60f91daf-3dd7-4283-a86d-24137b73f3da-";
        const string L1 = P + "00000137";
        const string N1 = P + "00003001";
        var coreL = Guid.Parse("c0000000-0000-4000-8000-000000000001");
        var coreW1 = Guid.Parse("c0000000-0000-4000-8000-000000000002");
        var coreN1 = Guid.Parse("c0000000-0000-4000-8000-000000000005");
        var wall1 = observation.Elements.Single(element => element.UniqueId == W1);
        var wall2 = observation.Elements.Single(element => element.UniqueId == W2);
        var door = observation.Elements.Single(element => element.UniqueId == D1);
        var floor = observation.Elements.Single(element => element.UniqueId == F1);
        MirrorSnapshot Snap(params MirrorObservedElement[] elements) => MirrorSnapshotMapper.Map(observation with { Elements = elements });
        MirrorCommittedModel With(params Element[] replace)
        {
            var byId = replace.ToDictionary(element => element.Id);
            var kept = committed.Project.Elements.Select(element => byId.TryGetValue(element.Id, out var next) ? next : element);
            var added = replace.Where(element => committed.Project.Elements.All(existing => existing.Id != element.Id));
            return committed with { Project = committed.Project with { Elements = kept.Concat(added).ToArray() } };
        }
        Element CoreElement(Guid id) => committed.Project.Elements.Single(element => element.Id == id);

        // (1) The real journal.read pipe reply is accepted; status and peek are checked.
        var replyJson = read("journal-read-reply.json");
        var reply = MirrorDrift.ParseJournal(replyJson);
        Check(reply.Entries.Select(entry => entry.Sequence).SequenceEqual(new long[] { 5, 6, 7, 8 }) && reply.Complete && !reply.HasMore && reply.CurrentSequence == 8,
            "the wrapped journal.read reply fixture parses with its completeness", string.Join(",", reply.Entries.Select(entry => entry.Sequence)));
        Check(MirrorDrift.ParseJournalEntries(replyJson).Count == 4, "ParseJournalEntries accepts the wrapped journal.read reply");
        var drifted = Snap(wall1, Wall(wall2, W2, 5050, 0, 5050, 4000), Wall(wall1, W3, 0, 0, 0, 4000), floor);
        var fromReply = MirrorDrift.Compare(drifted, committed, reply, new MirrorDriftOptions(JournalAfterSequence: 4));
        Check(fromReply.ToJson() == MirrorDrift.Compare(drifted, committed, journal, new MirrorDriftOptions(JournalAfterSequence: 4)).ToJson() && !fromReply.EvidenceIncomplete,
            "the wrapped reply classifies like the raw journal and is complete", fromReply.ToJson());
        Check(Code(() => MirrorDrift.ParseJournal(replyJson.Replace("\"status\": \"succeeded\"", "\"status\": \"failed\"", StringComparison.Ordinal))) == MirrorDrift.JournalInvalidCode,
            "a journal.read reply whose status is not succeeded is refused");
        Check(Code(() => MirrorDrift.ParseJournal(replyJson.Replace("\"peek\": true", "\"peek\": false", StringComparison.Ordinal))) == MirrorDrift.JournalInvalidCode,
            "a journal.read reply read without peek is refused");
        // (6) Completeness travels into the report.
        var partial = MirrorDrift.ParseJournal(replyJson.Replace("\"journal_complete\": true", "\"journal_complete\": false", StringComparison.Ordinal).Replace("\"has_more\": false", "\"has_more\": true", StringComparison.Ordinal)
            .Replace("\"truncated\": false", "\"truncated\": true", StringComparison.Ordinal));
        var partialReport = MirrorDrift.Compare(drifted, committed, partial, new MirrorDriftOptions(JournalAfterSequence: 4));
        Check(partialReport.EvidenceIncomplete && partialReport.EvidenceNotes.SequenceEqual(new[] { "journal_incomplete", "journal_has_more", "entry_truncated:5", "entry_truncated:6", "entry_truncated:7" }),
            "journal_complete false, has_more and truncated entries mark the evidence incomplete", string.Join(",", partialReport.EvidenceNotes));
        using (var parsed = JsonDocument.Parse(partialReport.ToJson()))
            Check(parsed.RootElement.GetProperty("evidence").GetProperty("incomplete").GetBoolean() && parsed.RootElement.GetProperty("evidence").GetProperty("notes").GetArrayLength() == 5, "evidence completeness is in the JSON");
        var rawDropped = MirrorDrift.ParseJournal(read("journal.json").Replace("\"dropped\": 0", "\"dropped\": 2", StringComparison.Ordinal));
        Check(!rawDropped.Complete && MirrorDrift.ParseJournal(read("journal.json")).Complete, "a raw journal with dropped entries is incomplete");
        // (9) A commit point beyond the reply's current sequence belongs to another add-in session: no journal evidence.
        var otherSession = MirrorDrift.Compare(drifted, committed, reply, new MirrorDriftOptions(JournalAfterSequence: 20));
        Check(otherSession.EvidenceNotes.SequenceEqual(new[] { "journal_session_mismatch" }) && otherSession.Findings.All(finding => finding.Evidence.Count == 0),
            "a journal_after_sequence past the reply's current sequence is a session mismatch and its entries are not evidence", otherSession.ToJson());
        // (5) Unknown origins are refused.
        Check(Code(() => MirrorDrift.ParseJournal(replyJson.Replace("\"origin\": \"add-in\"", "\"origin\": \"robot\"", StringComparison.Ordinal))) == MirrorDrift.JournalInvalidCode, "a reply entry with an unknown origin is refused");
        Check(Code(() => MirrorDrift.Compare(drifted, committed, new[] { journal[1] with { Origin = "robot" } })) == MirrorDrift.JournalInvalidCode, "an entry with an unknown origin is refused by Compare");
        // (4) Deletion evidence applies only to missing elements.
        var deletesW2 = journal.Append(new MirrorJournalEntry(9, DateTimeOffset.UnixEpoch, Doc, "TransactionCommitted", MirrorJournalOrigins.External, new[] { "Delete" },
            Array.Empty<string>(), Array.Empty<string>(), new long[] { 0x1002 }, 0, 0, 1)).ToArray();
        var w2 = MirrorDrift.Compare(drifted, committed, deletesW2, new MirrorDriftOptions(JournalAfterSequence: 4)).Findings.Single(finding => finding.UniqueId == W2);
        Check(w2.Classification == MirrorDriftClasses.AddInPending && w2.Evidence.SequenceEqual(new[] { "journal:6:add-in" }), "a deleted element id matching a changed element is not evidence for it", string.Join(",", w2.Evidence));

        // (2) Dependencies by role: openings compare level and host, regardless of order.
        var window = new MirrorObservedElement(MirrorCategories.Window, N1) { LevelUid = L1, HostUid = W2, Location = new MirrorPoint(5000, 2000), WidthMm = 1000, HeightMm = 1200, SillMm = 900 };
        var coreWindow = new Element(coreN1, "window", new[] { 5, 1.5, 5, 2.5, 0.9, 1.2 }, new[] { CoreW2, coreL }, new Provenance("intent", "approved"));
        var windowModel = With(coreWindow) with { NativeIds = committed.NativeIds.Append(new KeyValuePair<Guid, string>(coreN1, N1)).ToDictionary(pair => pair.Key, pair => pair.Value) };
        var windowSame = MirrorDrift.Compare(Snap(wall1, wall2, door, floor, window), windowModel);
        Check(!windowSame.HasDrift && windowSame.Summary.Unchanged == 5, "a window whose Core dependencies list host before level is unchanged", windowSame.ToJson());
        var windowMoved = MirrorDrift.Compare(Snap(wall1, wall2, door, floor, window), windowModel with { Project = windowModel.Project with { Elements = windowModel.Project.Elements.Select(element => element.Id == coreN1 ? element with { Dependencies = new[] { coreL, coreW1 } } : element).ToArray() } });
        var hostField = windowMoved.Findings.SingleOrDefault()?.Fields;
        Check(hostField is not null && hostField.Count == 1 && hostField[0] == new MirrorDriftField("host", W1, W2, null), "a window on another host is changed in its host field", windowMoved.ToJson());
        var doorRehosted = MirrorDrift.Compare(MirrorSnapshotMapper.Map(observation), With(CoreElement(CoreD1) with { Dependencies = new[] { CoreW2, coreL } }));
        Check(doorRehosted.Findings.SingleOrDefault()?.Fields.Select(field => field.Field).SequenceEqual(new[] { "host" }) == true, "a door with another host in Core is changed only in host", doorRehosted.ToJson());
        var otherLevel = Guid.Parse("c0000000-0000-4000-8000-0000000000ff");
        var levelChanged = MirrorDrift.Compare(MirrorSnapshotMapper.Map(observation), With(CoreElement(coreW1) with { Dependencies = new[] { otherLevel } }));
        Check(levelChanged.Findings.SingleOrDefault()?.Fields.Select(field => field.Field).SequenceEqual(new[] { "level", "dependencies" }) == true,
            "a wall depending on an unknown element reports its level and dependencies", levelChanged.ToJson());

        // (2)+(3) A pristine RoomCompiler model: spaces list only walls, plan/schedule/sheet are not comparable.
        var roomId = Guid.Parse("d0000000-0000-4000-8000-000000000001");
        var room = RoomCompiler.Compile(Guid.Parse("a0000000-0000-4000-8000-000000000001"), 2, new RoomIntent(roomId, 4, 3, 3, 0.2, 0, 0, "m", new Provenance("intent", "approved")));
        var roles = new[] { "level", "south", "east", "north", "west", "space", "plan", "schedule", "sheet" };
        var uids = roles.Select((role, i) => (role, uid: role == "level" ? L1 : P + (0xa001 + i).ToString("x8"))).ToDictionary(item => item.role, item => item.uid);
        var roomModel = new MirrorCommittedModel(Doc, room, roles.ToDictionary(role => RoomCompiler.Id(roomId, role), role => uids[role]));
        MirrorObservedElement RoomWall(string role, double ax, double ay, double bx, double by) => Wall(wall1, uids[role], ax, ay, bx, by);
        MirrorObservedElement Room(params string[] bounding) => new(MirrorCategories.Room, uids["space"])
        {
            LevelUid = L1,
            Loops = new[] { new MirrorLoop(new[] { new MirrorPoint(200, 200), new MirrorPoint(4200, 200), new MirrorPoint(4200, 3200), new MirrorPoint(200, 3200) }, false, bounding.Select(role => uids[role]).ToArray()) },
        };
        var roomWalls = new[] { RoomWall("south", 100, 100, 4300, 100), RoomWall("east", 4300, 100, 4300, 3300), RoomWall("north", 4300, 3300, 100, 3300), RoomWall("west", 100, 3300, 100, 100) };
        var roomSnapshot = MirrorSnapshotMapper.Map(observation with { Elements = roomWalls.Append(Room("west", "north", "east", "south")).ToArray() });
        var pristine = MirrorDrift.Compare(roomSnapshot, roomModel);
        Check(!pristine.HasDrift && pristine.Summary.Unchanged == 6 && pristine.Summary.NotComparable == 3 && pristine.Summary.Drift == 0,
            "a pristine RoomCompiler model has no drift: the wall-only space matches and plan, schedule and sheet are not comparable", pristine.ToJson());
        Check(pristine.NotComparable.Select(entry => (entry.ElementKind, entry.UniqueId, entry.Reason)).SequenceEqual(new[] { ("plan", uids["plan"], MirrorDrift.KindNotExported), ("schedule", uids["schedule"], MirrorDrift.KindNotExported), ("sheet", uids["sheet"], MirrorDrift.KindNotExported) }),
            "not comparable elements are listed by kind with their UniqueId", pristine.ToJson());
        using (var parsed = JsonDocument.Parse(pristine.ToJson()))
            Check(parsed.RootElement.GetProperty("not_comparable").EnumerateArray().All(entry => entry.GetProperty("classification").GetString() == MirrorDriftClasses.NotComparable), "not comparable entries are classified in JSON");
        // An IntentCompiler-style space (level first, walls in layout order) matches too.
        var spaceId = RoomCompiler.Id(roomId, "space");
        var withLevel = roomModel with { Project = room with { Elements = room.Elements.Select(element => element.Id == spaceId ? element with { Dependencies = new[] { RoomCompiler.Id(roomId, "level") }.Concat(element.Dependencies.Reverse()).ToArray() } : element).ToArray() } };
        Check(!MirrorDrift.Compare(roomSnapshot, withLevel).HasDrift, "a space listing its level and walls in another order is unchanged");
        var openRoom = MirrorDrift.Compare(MirrorSnapshotMapper.Map(observation with { Elements = roomWalls.Append(Room("west", "north", "south")).ToArray() }), roomModel);
        var spaceField = openRoom.Findings.SingleOrDefault()?.Fields;
        Check(spaceField is not null && spaceField.Count == 1 && spaceField[0].Field == "bounding_walls", "a space missing one bounding wall is changed in bounding_walls", openRoom.ToJson());

        // Angle wrap near +-180 and a reversed axis.
        var nearPi = With(CoreElement(CoreW2) with { GeometryMetres = new[] { 5, 0, 0, 0.0001, 3, 0.2 } });
        Check(!MirrorDrift.Compare(Snap(wall1, Wall(wall2, W2, 5000, 0, 0, -0.1), door, floor), nearPi).HasDrift, "axis directions either side of 180 degrees are compared across the wrap");
        var reversed = MirrorDrift.Compare(Snap(wall1, Wall(wall2, W2, 5000, 4000, 5000, 0), door, floor), committed).Findings.Single();
        Check(reversed.Fields.Select(field => field.Field).SequenceEqual(new[] { "ay", "by", "direction" }) && reversed.Fields[2].Delta == "180.0000", "a reversed wall axis is a 180 degree direction change", string.Join(";", reversed.Fields));

        // (11) Case-duplicate UniqueIds in a snapshot reply are refused.
        var baselineJson = MirrorSnapshotMapper.Map(observation).ToJson();
        Check(Code(() => MirrorDrift.ParseSnapshot(baselineJson.Replace("\"unique_id\":\"" + W2 + "\"", "\"unique_id\":\"" + W1.ToUpperInvariant() + "\"", StringComparison.Ordinal))) == MirrorDrift.SnapshotInvalidCode,
            "a snapshot reply with a UniqueId repeated ignoring case is refused");
        // (10) Non-Guid map keys are reported.
        Check(MirrorDrift.Compare(MirrorSnapshotMapper.Map(observation), committed).Summary.IgnoredMapKeys == 1 && Code(() => MirrorCommittedModel.Parse("{\"document_unique_id\":\"x\",\"project\":{},\"map\":{\"a\":1}}")) == MirrorDrift.ModelInvalidCode,
            "ignored map keys are counted and non-string map values refused");

        // Culture independence and more permutations.
        var expected = MirrorDrift.Compare(drifted, committed, reply, new MirrorDriftOptions(LengthToleranceMm: 0.25, AngleToleranceDeg: 0.05, JournalAfterSequence: 4)).ToJson();
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var german = MirrorDrift.Compare(drifted, committed, reply, new MirrorDriftOptions(LengthToleranceMm: 0.25, AngleToleranceDeg: 0.05, JournalAfterSequence: 4)).ToJson();
            Check(german == expected && expected.Contains("\"length_tolerance_mm\":0.250,", StringComparison.Ordinal), "the report is byte-identical under de-DE", german);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
        var random = new Random(1234);
        var all = true;
        for (var round = 0; round < 8; round++)
        {
            var shuffledModel = committed with
            {
                Project = committed.Project with { Elements = committed.Project.Elements.OrderBy(_ => random.Next()).ToArray() },
                NativeIds = committed.NativeIds.OrderBy(_ => random.Next()).ToDictionary(pair => pair.Key, pair => pair.Value),
            };
            var shuffledReply = reply with { Entries = reply.Entries.OrderBy(_ => random.Next()).ToArray() };
            var shuffledSnapshot = MirrorSnapshotMapper.Map(observation with { Elements = new[] { floor, Wall(wall1, W3, 0, 0, 0, 4000), Wall(wall2, W2, 5050, 0, 5050, 4000), wall1 }.OrderBy(_ => random.Next()).ToArray() });
            all &= MirrorDrift.Compare(shuffledSnapshot, shuffledModel, shuffledReply, new MirrorDriftOptions(LengthToleranceMm: 0.25, AngleToleranceDeg: 0.05, JournalAfterSequence: 4)).ToJson() == expected;
        }
        Check(all, "eight random permutations of model, map, journal and snapshot give byte-identical reports");
    }
}
