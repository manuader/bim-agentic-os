using Bim.Core;
using System.Text.Json;
// P2-03 external edit journal oracle: the Revit-free journal (core/Bim.Core/MirrorJournal*.cs) that the add-in's
// Application.DocumentChanged recorder (runtimes/revit-native/Journal*.cs) fills, and the diff of a new
// query.snapshot against the last one. No Revit is involved: a passing run is unit-tested evidence only.
int passed = 0, failed = 0;
void Check(bool condition, string name, string detail = "")
{
    if (condition) { Console.WriteLine("PASS " + name); passed++; return; }
    Console.WriteLine("::error::mirror-journal FAIL " + name + (detail.Length > 0 ? " | " + detail : ""));
    failed++;
}
string? Code(Action action)
{
    try { action(); return null; }
    catch (ArgumentException error) { return error.Message.Split(' ')[0]; }
    catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
}

const string Doc = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
const string OtherDoc = "11111111-2222-3333-4444-555555555555-00000001";
const string L1 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000137";
const string W1 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00001001";
const string W2 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00001002";
const string W3 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00001003";
const string D1 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00002001";
const string F1 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00004001";
const string Committed = "TransactionCommitted";

var start = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
var clockTicks = 0;
DateTimeOffset Clock() => start.AddSeconds(clockTicks++);
MirrorJournalChange Change(string[] names, string[]? added = null, string[]? modified = null, long[]? deleted = null, string doc = Doc, string operation = Committed, bool inside = false) =>
    new(doc, operation, names, added ?? Array.Empty<string>(), modified ?? Array.Empty<string>(), deleted ?? Array.Empty<long>(), inside);

MirrorPoint P(double x, double y) => new(x, y);
MirrorObservedElement Wall(string uid, double ax, double ay, double bx, double by, double height = 3000) =>
    new(MirrorCategories.Wall, uid) { LevelUid = L1, Start = P(ax, ay), End = P(bx, by), HeightMm = height, ThicknessMm = 200, WallKind = "Basic", LocationLineIsCenterline = true, BaseOffsetMm = 0 };
MirrorObservedElement Door(string uid, string host, double x, double y) =>
    new(MirrorCategories.Door, uid) { LevelUid = L1, HostUid = host, Location = P(x, y), WidthMm = 900, HeightMm = 2100 };
MirrorSnapshot Snap(string doc, params MirrorObservedElement[] elements) =>
    MirrorSnapshotMapper.Map(new MirrorObservation(doc, new[] { new MirrorObservedLevel(L1, "Level 1", 0) }, elements));

try
{
    // ---- Journal entries: sequence, injected time, normalized ids, origin ----
    var journal = new MirrorJournal(Clock, capacity: 4);
    Check(journal.Capacity == 4 && journal.LastSequence == 0 && journal.Entries().Count == 0 && journal.FirstRetainedSequence == 1, "new journal is empty and starts before sequence 1");
    var first = journal.Record(Change(new[] { "Move" }, added: new[] { W2, W1, W1, "", " " }, modified: new[] { D1 }, deleted: new long[] { 900, 12, 900 }));
    Check(first is { Sequence: 1, Origin: MirrorJournalOrigins.External, Operation: Committed } && first.RecordedAt == start && first.DocumentUid == Doc,
        "first external change gets sequence 1, the injected clock time and origin external", first?.ToString() ?? "null");
    Check(first is not null && first.AddedUids.SequenceEqual(new[] { W1, W2 }) && first.ModifiedUids.SequenceEqual(new[] { D1 }) && first.DeletedIds.SequenceEqual(new long[] { 12, 900 }),
        "added uids are deduplicated, blank ones dropped, sorted; deleted element ids deduplicated and sorted",
        first is null ? "" : string.Join(",", first.AddedUids) + " | " + string.Join(",", first.DeletedIds));
    Check(first is { AddedTotal: 2, ModifiedTotal: 1, DeletedTotal: 2, Truncated: false } && first.TransactionNames.SequenceEqual(new[] { "Move" }), "totals count distinct ids and names keep Revit order");
    var second = journal.Record(Change(new[] { "Undo Move" }, modified: new[] { W1 }, operation: "TransactionUndone"));
    Check(second is { Sequence: 2, Origin: MirrorJournalOrigins.External } && second.RecordedAt == start.AddSeconds(1), "sequence and clock advance per recorded entry");

    // ---- Add-in's own transactions are identified and skipped by default ----
    Check(MirrorJournal.IsAddInTransaction("BIM Agentic OS wall.create") && MirrorJournal.IsAddInTransaction("BIM Agentic OS command receipt") && MirrorJournal.IsAddInTransaction("BIM Agentic typed batch")
        && MirrorJournal.IsAddInTransaction("Apply typed operations") && MirrorJournal.IsAddInTransaction("Persist logical map and receipt") && MirrorJournal.IsAddInTransaction("Isolated native Room A"),
        "every transaction name the add-in opens is recognised");
    Check(!MirrorJournal.IsAddInTransaction("Wall") && !MirrorJournal.IsAddInTransaction("bim agentic os wall.create") && !MirrorJournal.IsAddInTransaction("Move BIM Agentic OS") && !MirrorJournal.IsAddInTransaction(""),
        "user transaction names (including case variants and embedded text) are not add-in transactions");
    Check(MirrorJournal.Origin(Change(new[] { "BIM Agentic OS wall.create", "BIM Agentic OS command receipt" })) == MirrorJournalOrigins.AddIn, "committed transactions all named by the add-in are add-in origin");
    Check(MirrorJournal.Origin(Change(new[] { "BIM Agentic OS wall.create", "Wall" })) == MirrorJournalOrigins.External, "a commit mixing a user transaction is external");
    Check(MirrorJournal.Origin(Change(Array.Empty<string>())) == MirrorJournalOrigins.External, "a change with no transaction names is external");
    Check(MirrorJournal.Origin(Change(new[] { "BIM Agentic OS wall.create" }, operation: "TransactionUndone")) == MirrorJournalOrigins.External, "the user undoing an add-in transaction is an external edit");
    Check(MirrorJournal.Origin(Change(new[] { "Anything" }, operation: "TransactionGroupRolledBack", inside: true)) == MirrorJournalOrigins.AddIn, "any change raised inside the add-in dispatch is add-in origin");
    var own = journal.Record(Change(new[] { "BIM Agentic OS wall.create" }, added: new[] { W3 }));
    Check(own is null && journal.SkippedAddIn == 1 && journal.LastSequence == 2, "add-in changes are skipped (counted, no sequence consumed) by default");
    var keeping = new MirrorJournal(Clock, recordAddIn: true);
    Check(keeping.Record(Change(new[] { "BIM Agentic OS wall.create" }, added: new[] { W3 })) is { Origin: MirrorJournalOrigins.AddIn, Sequence: 1 } && keeping.SkippedAddIn == 0, "recordAddIn keeps add-in entries with origin add-in");

    // ---- Bounds: capacity ring, id and name caps, validation ----
    journal.Record(Change(new[] { "A" }, doc: OtherDoc, added: new[] { F1 }));
    journal.Record(Change(new[] { "B" }, modified: new[] { W2 }));
    journal.Record(Change(new[] { "C" }, deleted: new long[] { 5 }));
    Check(journal.LastSequence == 5 && journal.Entries().Count == 4 && journal.Dropped == 1 && journal.FirstRetainedSequence == 2 && journal.Entries()[0].Sequence == 2,
        "a full journal drops its oldest entry and counts it", "last " + journal.LastSequence + " dropped " + journal.Dropped + " first " + journal.FirstRetainedSequence);
    Check(journal.Entries(Doc).Select(entry => entry.Sequence).SequenceEqual(new long[] { 2, 4, 5 }) && journal.Entries(OtherDoc.ToUpperInvariant()).Single().Sequence == 3,
        "entries filter by document unique id ignoring case");
    Check(journal.Entries(Doc, afterSequence: 4).Single().Sequence == 5, "entries filter by sequence after a baseline");
    var many = Enumerable.Range(0, MirrorJournal.MaxIdsPerList + 10).Select(i => "uid-" + i.ToString("D6")).ToArray();
    var big = new MirrorJournal(Clock).Record(Change(Enumerable.Range(0, MirrorJournal.MaxNames + 3).Select(i => new string('n', i == 0 ? MirrorJournal.MaxNameLength + 50 : 3)).ToArray(), added: many) with { });
    var longName = new MirrorJournal(Clock).Record(Change(new[] { new string('n', MirrorJournal.MaxNameLength + 1) }));
    Check(longName is { NamesTruncated: true, Truncated: true }, "a truncated transaction name marks the entry truncated");
    Check(new MirrorJournal(Clock).Record(Change(new[] { "Short" })) is { NamesTruncated: false, Truncated: false }, "short names leave the entry complete");
    Check(big is not null && big.AddedUids.Count == MirrorJournal.MaxIdsPerList && big.AddedTotal == MirrorJournal.MaxIdsPerList + 10 && big.Truncated
        && big.AddedUids[0] == "uid-000000" && big.TransactionNames.Count == MirrorJournal.MaxNames && big.TransactionNames[0].Length == MirrorJournal.MaxNameLength,
        "id lists and transaction names are capped and the entry says it is truncated with the true total");
    var preCapped = new MirrorJournal(Clock).Record(Change(new[] { "Paste" }, added: new[] { W1 }) with { AddedTotal = 50000, DeletedTotal = 7 });
    Check(preCapped is { AddedTotal: 50000, DeletedTotal: 7, Truncated: true }, "a recorder that capped ids itself reports the true totals");
    Check(Code(() => journal.Record(Change(new[] { "X" }, doc: " "))) == "journal_document_required", "a change without a document unique id is refused");
    Check(Code(() => new MirrorJournal(Clock, capacity: 0)) == "journal_capacity_invalid", "a non-positive capacity is refused");
    Check(new MirrorJournal(Clock).Record(Change(new[] { "X" }, operation: "")) is { Operation: "unknown" }, "a missing operation is recorded as unknown");
    journal.RecordFailure();
    Check(journal.Failures == 1, "recorder failures are counted instead of thrown into Revit");
    journal.RecordUnidentified();
    Check(journal.Unidentified == 1, "events from documents without a unique id are counted");

    // ---- Total id budget across the journal, oldest evicted first ----
    Check(Code(() => new MirrorJournal(Clock, idBudget: 0)) == "journal_id_budget_invalid", "a non-positive id budget is refused");
    var budget = new MirrorJournal(Clock, capacity: 100, idBudget: 5);
    for (var i = 0; i < 4; i++) budget.Record(Change(new[] { "Edit " + i }, modified: new[] { W1 }, deleted: new long[] { i }));
    Check(budget.Entries().Select(entry => entry.Sequence).SequenceEqual(new long[] { 3, 4 }) && budget.Dropped == 2 && budget.RetainedIds == 4 && budget.FirstRetainedSequence == 3,
        "entries beyond the total id budget are evicted oldest first", $"retained {string.Join(",", budget.Entries().Select(e => e.Sequence))} ids {budget.RetainedIds} dropped {budget.Dropped}");
    budget.Record(Change(new[] { "Paste" }, added: new[] { "a", "b", "c", "d", "e", "f", "g" }));
    Check(budget.Entries().Select(entry => entry.Sequence).SequenceEqual(new long[] { 5 }) && budget.RetainedIds == 7, "the newest entry is kept even when it alone exceeds the budget");

    // ---- Diagnostic JSON (journal_version 1, never persisted) ----
    var single = new MirrorJournal(() => start);
    single.Record(Change(new[] { "Move" }, added: new[] { W1 }, modified: new[] { D1 }, deleted: new long[] { 12 }) with { DocumentPath = @"C:\Fixtures\A.rvt" });
    var expectedEntry = "{\"sequence\":1,\"recorded_at\":\"2026-11-03T12:00:00.000Z\",\"document_unique_id\":\"" + Doc + "\",\"document_path\":\"C:\\\\Fixtures\\\\A.rvt\",\"operation\":\"TransactionCommitted\",\"origin\":\"external\",\"transaction_names\":[\"Move\"],\"added\":[\"" + W1 + "\"],\"modified\":[\"" + D1 + "\"],\"deleted_element_ids\":[12],\"added_total\":1,\"modified_total\":1,\"deleted_total\":1,\"truncated\":false}";
    var journalJson = single.ToJson();
    Check(journalJson == "{\"journal_version\":1,\"capacity\":" + MirrorJournal.DefaultCapacity + ",\"last_sequence\":1,\"dropped\":0,\"skipped_add_in\":0,\"failures\":0,\"unidentified\":0,\"id_budget\":" + MirrorJournal.DefaultIdBudget + ",\"retained_ids\":3,\"entries\":[" + expectedEntry + "]}",
        "journal JSON is canonical", journalJson);
    using (var parsed = JsonDocument.Parse(journal.ToJson(Doc, 2))) Check(parsed.RootElement.GetProperty("entries").GetArrayLength() == 2, "journal JSON honours document and sequence filters");

    // ---- Snapshot diff ----
    var before = Snap(Doc, Wall(W1, 0, 0, 5000, 0), Wall(W2, 5000, 0, 5000, 4000), Door(D1, W1, 1000, 0), new MirrorObservedElement(MirrorCategories.Floor, F1) { LevelUid = L1 });
    var same = MirrorSnapshotDiffer.Compare(before, Snap(Doc, Door(D1, W1, 1000, 0), new MirrorObservedElement(MirrorCategories.Floor, F1) { LevelUid = L1 }, Wall(W2, 5000, 0, 5000, 4000), Wall(W1, 0, 0, 5000, 0)));
    Check(same.IsEmpty && same.Added.Count == 0 && same.Removed.Count == 0 && same.Changed.Count == 0, "the same model observed in another order has an empty diff");
    var noise = MirrorSnapshotDiffer.Compare(before, Snap(Doc, Wall(W1, 0, 0, 5000.0000001, 0), Wall(W2, 5000, 0, 5000, 4000), Door(D1, W1, 1000, 0), new MirrorObservedElement(MirrorCategories.Floor, F1) { LevelUid = L1 }));
    Check(noise.IsEmpty, "sub-micrometre conversion noise is not a change");
    // The diff tolerance itself (1e-3 mm): values the mapper does not normalize (0.4 um) are equal, 2 um is a change.
    var atTolerance = MirrorSnapshotDiffer.Compare(Snap(Doc, Wall(W1, 0, 0, 1000, 0)), Snap(Doc, Wall(W1, 0, 0, 1000.0004, 0)));
    Check(atTolerance.IsEmpty, "1000.0004 mm against 1000 mm is within the diff tolerance", atTolerance.ToJson());
    var overTolerance = MirrorSnapshotDiffer.Compare(Snap(Doc, Wall(W1, 0, 0, 1000, 0)), Snap(Doc, Wall(W1, 0, 0, 1000.002, 0)));
    Check(overTolerance.Changed.SingleOrDefault()?.Fields.Select(field => field.Field).SequenceEqual(new[] { "bx" }) == true, "1000.002 mm against 1000 mm is a change of bx", overTolerance.ToJson());
    // External edits: W1 lengthened and taller, W2 deleted (door rehosting not involved), W3 added, the floor removed.
    var after = Snap(Doc, Wall(W1, 0, 0, 6000, 0, height: 3200), Wall(W3, 0, 0, 0, 4000), Door(D1, W1, 1000, 0));
    var diff = MirrorSnapshotDiffer.Compare(before, after);
    Check(diff.DocumentUid == Doc && diff.Added.Select(item => (item.UniqueId, item.Category)).SequenceEqual(new[] { (W3, MirrorCategories.Wall) }), "a new wall is added", string.Join(",", diff.Added));
    Check(diff.Removed.Select(item => (item.UniqueId, item.Category)).SequenceEqual(new[] { (W2, MirrorCategories.Wall), (F1, MirrorCategories.Floor) }.OrderBy(item => item.Item1, StringComparer.Ordinal)),
        "a deleted wall and a deleted unmapped floor are removed", string.Join(",", diff.Removed));
    var w1 = diff.Changed.SingleOrDefault(item => item.UniqueId == W1);
    Check(w1 is not null && w1.Category == MirrorCategories.Wall && w1.Fields.Select(field => field.Field).SequenceEqual(new[] { "bx", "height" })
        && w1.Fields[0].Before == "5" && w1.Fields[0].After == "6" && w1.Fields[1].Before == "3" && w1.Fields[1].After == "3.2",
        "a moved and raised wall lists exactly its changed named fields in metres", w1 is null ? "missing" : string.Join(";", w1.Fields));
    var d1 = diff.Changed.SingleOrDefault(item => item.UniqueId == D1);
    Check(d1 is null, "an untouched door on a changed host keeps its geometry", d1 is null ? "" : string.Join(";", d1.Fields));
    Check(diff.Changed.Count == 1, "only the changed wall is changed", string.Join(",", diff.Changed.Select(item => item.UniqueId)));

    // Mapped <-> unmapped transitions are changes, not add/remove.
    var curved = new MirrorObservedElement(MirrorCategories.Wall, W2) { LevelUid = L1, Curved = true, HeightMm = 3000, ThicknessMm = 200, WallKind = "Basic", LocationLineIsCenterline = true, BaseOffsetMm = 0 };
    var toCurved = MirrorSnapshotDiffer.Compare(before, Snap(Doc, Wall(W1, 0, 0, 5000, 0), curved, Door(D1, W1, 1000, 0), new MirrorObservedElement(MirrorCategories.Floor, F1) { LevelUid = L1 }));
    var w2 = toCurved.Changed.SingleOrDefault(item => item.UniqueId == W2);
    Check(w2 is not null && toCurved.Added.Count == 0 && toCurved.Removed.Count == 0 && w2.Fields.Select(field => field.Field).SequenceEqual(new[] { "mapped", "kind", "geometry", "dependencies", "reason", "detail" })
        && w2.Fields[0].Before == "true" && w2.Fields[0].After == "false" && w2.Fields[4].Before is null && w2.Fields[4].After == MirrorReasons.CurvedWall,
        "a straight wall made curved changes from mapped to unmapped with its reason", w2 is null ? "missing" : string.Join(";", w2.Fields));
    var first2 = MirrorSnapshotDiffer.Compare(null, before);
    Check(first2.Added.Count == 5 && first2.Removed.Count == 0 && first2.Changed.Count == 0 && first2.Added[0].UniqueId == L1, "without a previous snapshot everything (levels included) is added, ordered by unique id",
        string.Join(",", first2.Added.Select(item => item.UniqueId[^4..])));
    Check(Code(() => MirrorSnapshotDiffer.Compare(before, Snap(OtherDoc, Wall(W1, 0, 0, 5000, 0)))) == "diff_document_mismatch", "snapshots of different documents are not compared");
    var diffJson = MirrorSnapshotDiffer.Compare(Snap(Doc, Wall(W1, 0, 0, 5000, 0)), Snap(Doc, Wall(W1, 0, 0, 5000, 0, height: 3200))).ToJson();
    Check(diffJson == "{\"diff_version\":1,\"document_unique_id\":\"" + Doc + "\",\"added\":[],\"removed\":[],\"changed\":[{\"unique_id\":\"" + W1 + "\",\"category\":\"wall\",\"fields\":[{\"field\":\"height\",\"before\":\"3\",\"after\":\"3.2\"}]}]}",
        "diff JSON is canonical", diffJson);

    // ---- Tracker: journal since the last snapshot plus the snapshot diff ----
    var trackerClock = 0;
    var tracker = new MirrorJournalTracker(new MirrorJournal(() => start.AddMinutes(trackerClock++), capacity: 3));
    Check(!tracker.HasBaseline(Doc), "no baseline before the first report");
    tracker.Journal.Record(Change(new[] { "Wall" }, added: new[] { W1 }));
    var r1 = tracker.Report(before);
    Check(r1 is { BaselineExisted: false, BaselineSequence: 0, CurrentSequence: 1, JournalComplete: true } && r1.Entries.Count == 1 && r1.Diff.Added.Count == 5 && tracker.HasBaseline(Doc.ToUpperInvariant()),
        "the first report lists every journal entry, diffs against nothing and sets the baseline", $"{r1.BaselineExisted} {r1.BaselineSequence} {r1.CurrentSequence} {r1.JournalComplete} {r1.Entries.Count}");
    tracker.Journal.Record(Change(new[] { "Drag" }, modified: new[] { W1 }));
    tracker.Journal.Record(Change(new[] { "Other" }, doc: OtherDoc, modified: new[] { F1 }));
    tracker.Journal.Record(Change(new[] { "Delete" }, deleted: new long[] { 4242 }));
    var r2 = tracker.Report(after);
    Check(r2 is { BaselineExisted: true, BaselineSequence: 1, CurrentSequence: 4, JournalComplete: true } && r2.Entries.Select(entry => entry.Sequence).SequenceEqual(new long[] { 2, 4 }) && r2.Diff.Changed.Single().UniqueId == W1,
        "the next report lists this document's entries since the baseline and the diff against the last snapshot", $"{r2.BaselineSequence} {r2.CurrentSequence} {r2.JournalComplete} {string.Join(",", r2.Entries.Select(e => e.Sequence))}");
    var r3 = tracker.Report(after);
    Check(r3.Entries.Count == 0 && r3.Diff.IsEmpty && r3.BaselineSequence == 4, "a report right after another is empty");
    for (var i = 0; i < 4; i++) tracker.Journal.Record(Change(new[] { "Nudge " + i }, modified: new[] { W1 }));
    var r4 = tracker.Report(after);
    Check(!r4.JournalComplete && r4.Entries.Count == 3, "a report whose entries were partly dropped says the journal is incomplete");
    Check(Code(() => tracker.Report(null!)) == "snapshot_required", "a null snapshot is refused");
    var reportJson = new MirrorJournalTracker(new MirrorJournal(() => start)).Report(Snap(Doc)).ToJson();
    using (var parsed = JsonDocument.Parse(reportJson))
    {
        var root = parsed.RootElement;
        Check(root.GetProperty("journal_version").GetInt32() == 1 && root.GetProperty("document_unique_id").GetString() == Doc && root.GetProperty("baseline_sequence").GetInt64() == 0
            && root.GetProperty("current_sequence").GetInt64() == 0 && !root.GetProperty("baseline_existed").GetBoolean() && root.GetProperty("journal_complete").GetBoolean()
            && root.GetProperty("entries").GetArrayLength() == 0 && root.GetProperty("diff").GetProperty("added").GetArrayLength() == 1,
            "report JSON carries the baseline, journal entries and diff", reportJson);
    }

    // ---- Failures since the baseline make the journal incomplete ----
    var failing = new MirrorJournalTracker(new MirrorJournal(() => start));
    var f1 = failing.Report(Snap(Doc));
    failing.Journal.RecordFailure();
    var f2 = failing.Report(Snap(Doc));
    Check(f1.JournalComplete && !f2.JournalComplete && f2.FailuresSinceBaseline == 1, "a recorder failure since the baseline makes the report incomplete", $"{f1.JournalComplete} {f2.JournalComplete} {f2.FailuresSinceBaseline}");
    var f3 = failing.Report(Snap(Doc));
    Check(f3.JournalComplete && f3.FailuresSinceBaseline == 0, "the next report after it is complete again");

    // ---- Two copies of the same file (same unique ids) do not share a baseline or entries ----
    const string PathA = @"C:\Fixtures\A.rvt", PathB = @"C:\Fixtures\B.rvt";
    var copies = new MirrorJournalTracker(new MirrorJournal(() => start));
    copies.Journal.Record(Change(new[] { "Edit A" }, modified: new[] { W1 }) with { DocumentPath = PathA });
    copies.Journal.Record(Change(new[] { "Edit B" }, modified: new[] { W2 }) with { DocumentPath = PathB });
    var a1 = copies.Report(Snap(Doc, Wall(W1, 0, 0, 5000, 0)), PathA);
    var b1 = copies.Report(Snap(Doc, Wall(W1, 0, 0, 6000, 0)), PathB);
    Check(!a1.BaselineExisted && !b1.BaselineExisted && a1.Entries.Single().TransactionNames[0] == "Edit A" && b1.Entries.Single().TransactionNames[0] == "Edit B" && b1.DocumentPath == PathB,
        "each copy has its own baseline and only its own entries");
    var a2 = copies.Report(Snap(Doc, Wall(W1, 0, 0, 5000, 0)), PathA.ToLowerInvariant());
    Check(a2.BaselineExisted && a2.Diff.IsEmpty && copies.HasBaseline(Doc, PathB) && !copies.HasBaseline(Doc, @"C:\Fixtures\C.rvt"), "a copy's baseline is matched by its own path (ignoring case), not the other copy's");
    Check(copies.Journal.Entries(Doc, documentPath: PathA).Single().DocumentPath == PathA, "entries filter by document path");

    // ---- Per-reply caps with continuation through after_sequence ----
    var paged = new MirrorJournalTracker(new MirrorJournal(() => start));
    for (var i = 0; i < 5; i++) paged.Journal.Record(Change(new[] { "Edit " + i }, modified: new[] { W1, W2 }));
    var page1 = paged.Report(Snap(Doc), options: new MirrorJournalReadOptions(MaxEntries: 2));
    Check(page1.Entries.Select(entry => entry.Sequence).SequenceEqual(new long[] { 1, 2 }) && page1.HasMore && page1.NextAfterSequence == 2 && page1.CurrentSequence == 5,
        "a reply holds at most max_entries entries and says where to continue", $"{string.Join(",", page1.Entries.Select(e => e.Sequence))} more {page1.HasMore} next {page1.NextAfterSequence}");
    var page2 = paged.Report(Snap(Doc), options: new MirrorJournalReadOptions(AfterSequence: page1.NextAfterSequence, Peek: true, MaxEntries: 2));
    var page3 = paged.Report(Snap(Doc), options: new MirrorJournalReadOptions(AfterSequence: page2.NextAfterSequence, Peek: true, MaxEntries: 2));
    Check(page2.Entries.Select(entry => entry.Sequence).SequenceEqual(new long[] { 3, 4 }) && page2.HasMore && page3.Entries.Single().Sequence == 5 && !page3.HasMore && page3.NextAfterSequence == 5 && page3.AfterSequence == 4,
        "continuation pages through after_sequence reach the last entry");
    var idPaged = new MirrorJournalTracker(new MirrorJournal(() => start));
    for (var i = 0; i < 3; i++) idPaged.Journal.Record(Change(new[] { "Edit " + i }, modified: new[] { W1, W2 }));
    var idPage = idPaged.Report(Snap(Doc), options: new MirrorJournalReadOptions(MaxIds: 3));
    Check(idPage.Entries.Count == 1 && idPage.HasMore && idPage.NextAfterSequence == 1, "a reply also stops at its id budget (at least one entry)");
    Check(Code(() => paged.Report(Snap(Doc), options: new MirrorJournalReadOptions(AfterSequence: 99))) == "journal_after_sequence_invalid", "an after_sequence beyond the journal is refused");
    Check(Code(() => paged.Report(Snap(Doc), options: new MirrorJournalReadOptions(MaxEntries: 0))) == "journal_options_invalid", "a non-positive max_entries is refused");

    // ---- Retry safety: peek never moves the baseline; after_sequence re-reads a lost reply ----
    var retry = new MirrorJournalTracker(new MirrorJournal(() => start));
    retry.Journal.Record(Change(new[] { "Wall" }, added: new[] { W1 }));
    var peekA = retry.Report(Snap(Doc, Wall(W1, 0, 0, 5000, 0)), options: new MirrorJournalReadOptions(Peek: true));
    var peekB = retry.Report(Snap(Doc, Wall(W1, 0, 0, 5000, 0)), options: new MirrorJournalReadOptions(Peek: true));
    Check(peekA.Peek && peekA.ToJson() == peekB.ToJson() && !retry.HasBaseline(Doc) && peekA.Diff.Added.Count == 2, "a repeated peek returns the same reply and sets no baseline", peekA.ToJson());
    var committed = retry.Report(Snap(Doc, Wall(W1, 0, 0, 5000, 0)));
    retry.Journal.Record(Change(new[] { "Drag" }, modified: new[] { W1 }));
    var lost = retry.Report(Snap(Doc, Wall(W1, 0, 0, 6000, 0)));
    var again = retry.Report(Snap(Doc, Wall(W1, 0, 0, 6000, 0)), options: new MirrorJournalReadOptions(AfterSequence: committed.CurrentSequence, Peek: true));
    Check(lost.Entries.Single().Sequence == 2 && again.Entries.Single().Sequence == 2 && again.JournalComplete, "a client that lost a reply re-reads its entries with after_sequence");
    Check(MirrorJournalReadOptions.Parse(null) == new MirrorJournalReadOptions() && MirrorJournalReadOptions.Parse("{\"after_sequence\":7,\"peek\":true,\"max_entries\":10}") == new MirrorJournalReadOptions(AfterSequence: 7, Peek: true, MaxEntries: 10),
        "read options parse from the journal.read request member");
    foreach (var bad in new[] { "[]", "{\"after_sequence\":-1}", "{\"after_sequence\":1.5}", "{\"peek\":\"yes\"}", "{\"max_entries\":0}", "{\"max_entries\":100000}", "{\"colour\":1}", "not json" })
        Check(Code(() => MirrorJournalReadOptions.Parse(bad)) == "journal_options_invalid", "invalid read options are refused: " + bad);
    using (var parsed = JsonDocument.Parse(page1.ToJson()))
    {
        var root = parsed.RootElement;
        Check(root.GetProperty("document_path").GetString() == "" && root.GetProperty("after_sequence").GetInt64() == 0 && !root.GetProperty("peek").GetBoolean() && root.GetProperty("has_more").GetBoolean()
            && root.GetProperty("next_after_sequence").GetInt64() == 2 && root.GetProperty("failures_since_baseline").GetInt64() == 0, "report JSON carries paging, peek and failure fields", page1.ToJson());
    }

    // ---- Source guards over the native add-in (compiled only in CI) ----
    var repo = RevitCommandPolicy.RepositoryRoot(AppContext.BaseDirectory);
    var native = repo is null ? null : Path.Combine(repo, "runtimes", "revit-native");
    var app = native is null || !File.Exists(Path.Combine(native, "App.cs")) ? null : File.ReadAllText(Path.Combine(native, "App.cs"));
    string Between(string text, string from, string to) { var i = text.IndexOf(from, StringComparison.Ordinal); var j = i < 0 ? -1 : text.IndexOf(to, i + from.Length, StringComparison.Ordinal); return i < 0 || j < 0 ? "" : text[i..j]; }
    var startup = app is null ? "" : Between(app, "public Result OnStartup", "void Expose()");
    var expose = app is null ? "" : Between(app, "void Expose()", "void Withdraw()");
    var withdraw = app is null ? "" : Between(app, "void Withdraw()", "public Result OnShutdown");
    Check(app is not null && !startup.Contains("JournalRecorder.Attach", StringComparison.Ordinal), "OnStartup does not subscribe the journal before the fixture config is validated", app is null ? "App.cs not found" : startup);
    var pipeCheck = expose.IndexOf("PipeAllowed", StringComparison.Ordinal);
    var attachAt = expose.IndexOf("JournalRecorder.Attach", StringComparison.Ordinal);
    Check(pipeCheck >= 0 && attachAt > pipeCheck, "Expose subscribes the journal only after AddInGate.PipeAllowed", expose);
    Check(withdraw.Contains("JournalRecorder.Detach", StringComparison.Ordinal), "Withdraw unsubscribes the journal", withdraw);
    var names = new List<(string File, string Name)>();
    var dynamicNames = new List<string>();
    foreach (var file in native is null || !Directory.Exists(native) ? Array.Empty<string>() : Directory.GetFiles(native, "*.cs"))
    {
        var text = File.ReadAllText(file);
        // new Transaction / TransactionGroup / SubTransaction / StairsEditScope (doc, name ...) and the NativeRoomFixture
        // helper Transaction(d, "name", ...): the name argument must start with a string literal the journal recognises.
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"(?:new\s+(?:Transaction|TransactionGroup|SubTransaction|StairsEditScope)|(?<![\w.])Transaction)\s*\(\s*\w+\s*,\s*(?<arg>[^,)]+)"))
        {
            var arg = match.Groups["arg"].Value.Trim();
            var literal = System.Text.RegularExpressions.Regex.Match(arg, "^\"(?<s>[^\"]*)\"");
            if (literal.Success) names.Add((Path.GetFileName(file), literal.Groups["s"].Value));
            else dynamicNames.Add(Path.GetFileName(file) + ": " + arg);
        }
    }
    Check(names.Count >= 15, "the source scan finds the add-in's transaction names", names.Count.ToString());
    var unknown = names.Where(item => !MirrorJournal.IsAddInTransaction(item.Name)).Select(item => item.File + ": " + item.Name).ToArray();
    Check(unknown.Length == 0, "every literal Transaction / TransactionGroup name in the native runtime is recognised as the add-in's", string.Join("; ", unknown));
    // Only the NativeRoomFixture helper passes a variable name; its callers' literals are scanned above.
    Check(dynamicNames.SequenceEqual(new[] { "NativeRoomFixture.cs: name" }), "no other transaction takes a name the scan cannot see", string.Join("; ", dynamicNames));

    // ---- P2-04 drift between the mirror and the Core store (MirrorDriftChecks.cs) ----
    MirrorDriftChecks.Run(Check);

    // ---- P2-07 drift resolution proposals (MirrorDriftResolveChecks.cs) ----
    MirrorDriftResolveChecks.Run(Check);

    // ---- P2-09 selective drift resolution (MirrorDriftSelectChecks.cs) ----
    MirrorDriftSelectChecks.Run(Check);

    // ---- P2-12 commit adopted drift into Core, no provenance (MirrorDriftAdoptChecks.cs) ----
    MirrorDriftAdoptChecks.Run(Check);

    // ---- Thread safety: concurrent records keep unique, gap-free sequences ----
    var concurrent = new MirrorJournal(() => start, capacity: 10000);
    Parallel.For(0, 2000, i => concurrent.Record(Change(new[] { "T" + i }, modified: new[] { W1 })));
    Check(concurrent.LastSequence == 2000 && concurrent.Entries().Select(entry => entry.Sequence).SequenceEqual(Enumerable.Range(1, 2000).Select(i => (long)i)), "concurrent records keep unique ordered sequences");
}
catch (Exception error)
{
    Check(false, "mirror-journal oracle threw", error.GetType().Name + ": " + error.Message);
}
Console.WriteLine($"mirror-journal: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
