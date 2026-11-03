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
        Check(committed.DocumentUid == Doc && committed.Project.Revision == 3 && committed.Project.Elements.Length == 4 && committed.NativeIds.Count == 4, "committed fixture loads");
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
        Check(d1j is { Classification: MirrorDriftClasses.ExternalEdit } && d1j.Evidence.SequenceEqual(new[] { "journal:7:external:deleted" }),
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
}
