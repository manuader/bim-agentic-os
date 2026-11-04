using Bim.Core;
using System.Globalization;
using System.Reflection;
// P2-06 element provenance ledger oracle (core/Bim.Core/Provenance*.cs). Pure BCL, no host application involved: a
// passing run is unit-tested evidence only.
int passed = 0, failed = 0;
void Check(bool condition, string name, string detail = "")
{
    if (condition) { Console.WriteLine("PASS " + name); passed++; return; }
    Console.WriteLine("::error::provenance FAIL " + name + (detail.Length > 0 ? " | " + detail : ""));
    failed++;
}
string Show(ProvenanceAppendResult result) => result.Code + (result.Detail is null ? "" : " @" + result.Detail);

const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
const string Doc = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
const string W1 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00001001";
const string W2 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00001002";
var fileHash = string.Concat(Enumerable.Repeat("ab", 32));
var otherHash = string.Concat(Enumerable.Repeat("cd", 32));
var e1 = Guid.Parse("11111111-2222-3333-4444-555555555555");
var e2 = Guid.Parse("22222222-3333-4444-5555-666666666666");
var e3 = Guid.Parse("33333333-4444-5555-6666-777777777777");

try
{
    // ---- Golden bytes: an independent oracle for the canonical form and the SHA-256 chain ----
    const string H1 = "b8d5a9ec92fcc7b6089905ccf8a80649b02ec2ab3f31de43df2b4d0ccfe1de54";
    const string H2 = "ee089e75e45810105508ea2bf2efbd22f3d54b0aed575d00e502c6c9b338a195";
    var golden = "{\"provenance_ledger_version\":1,\"entries\":["
        + "{\"seq\":1,\"element_id\":\"11111111-2222-3333-4444-555555555555\",\"source\":{\"kind\":\"intent\",\"intent_id\":\"layout-1\",\"rule_id\":\"room.rect\"},\"prev_hash\":\"" + Genesis + "\",\"hash\":\"" + H1 + "\"},"
        + "{\"seq\":2,\"element_id\":\"11111111-2222-3333-4444-555555555555\",\"source\":{\"kind\":\"cad_import\",\"file_sha256\":\"" + fileHash + "\",\"entity_handle\":\"1F\"},\"prev_hash\":\"" + H1 + "\",\"hash\":\"" + H2 + "\"}]}";
    var small = new ProvenanceLedger();
    var g1 = small.Append(e1, new IntentProvenance("layout-1", "room.rect"));
    var g2 = small.Append(e1, new CadImportProvenance(fileHash, "1F"));
    Check(g1.Code == ProvenanceCodes.Appended && g2.Code == ProvenanceCodes.Appended, "first appends are appended", Show(g1) + " " + Show(g2));
    Check(g1.Entry is { Sequence: 1, PrevHash: Genesis, Hash: H1 } && g2.Entry is { Sequence: 2, PrevHash: H1, Hash: H2 }, "sequence starts at 1, prev_hash chains from 64 zeros, hash is SHA-256 of the entry without hash",
        g1.Entry?.Hash + " / " + g2.Entry?.Hash);
    Check(small.Serialize() == golden, "serialization equals the golden canonical bytes", small.Serialize());
    Check(small.HeadHash == H2 && small.Count == 2, "head hash and count follow the last entry");
    Check(new ProvenanceLedger().Serialize() == "{\"provenance_ledger_version\":1,\"entries\":[]}", "an empty ledger serializes to an empty entries array");
    var emptyLoad = ProvenanceLedger.Load("{\"provenance_ledger_version\":1,\"entries\":[]}");
    Check(emptyLoad.Ok && emptyLoad.Ledger is { Count: 0 }, "an empty ledger loads", emptyLoad.Code);

    // ---- All four source kinds ----
    var ledger = new ProvenanceLedger();
    var intent = ledger.Append(e1, new IntentProvenance("intent-a", "rule.room"));
    var command = ledger.Append(e2, new CatalogueCommandProvenance("wall.create", "op-0001"));
    var cad = ledger.Append(e3, new CadImportProvenance(fileHash, "2A"));
    Check(intent.Code == ProvenanceCodes.Appended && command.Code == ProvenanceCodes.Appended && cad.Code == ProvenanceCodes.Appended,
        "intent, catalogue command and CAD import sources append", Show(intent) + " " + Show(command) + " " + Show(cad));

    // External edits come from the P2-03 journal: one source per added / modified UniqueId, add-in entries skipped.
    var clock = new DateTimeOffset(2026, 11, 4, 9, 30, 0, 123, TimeSpan.Zero);
    var journal = new MirrorJournal(() => clock, recordAddIn: true);
    var external = journal.Record(new MirrorJournalChange(Doc.ToUpperInvariant(), "TransactionCommitted", new[] { "Move" }, new[] { W1.ToUpperInvariant() }, new[] { W2 }, new long[] { 77 }, false));
    var addIn = journal.Record(new MirrorJournalChange(Doc, "TransactionCommitted", new[] { "BIM Agentic OS wall.create" }, new[] { W2 }, Array.Empty<string>(), Array.Empty<long>(), false));
    Check(external is not null && addIn is { Origin: MirrorJournalOrigins.AddIn }, "journal fixtures recorded");
    var fromJournal = ProvenanceSource.FromJournal(external!);
    Check(fromJournal.Count == 2
        && fromJournal[0].ElementId == MirrorSnapshotMapper.Id(Doc, W1) && fromJournal[0].Source == new ExternalEditProvenance(Doc, 1, "2026-11-04T09:30:00.123Z", W1, ProvenanceChanges.Added)
        && fromJournal[1].ElementId == MirrorSnapshotMapper.Id(Doc, W2) && fromJournal[1].Source == new ExternalEditProvenance(Doc, 1, "2026-11-04T09:30:00.123Z", W2, ProvenanceChanges.Modified),
        "FromJournal gives added then modified UniqueIds (lower-cased) with the mirror element id, sequence and time; deletions carry no UniqueId",
        string.Join("; ", fromJournal.Select(item => item.ElementId + " " + item.Source)));
    Check(ProvenanceSource.FromJournal(addIn!).Count == 0, "add-in journal entries are not external edits");
    foreach (var (id, source) in fromJournal)
    {
        var appended = ledger.Append(id, source);
        Check(appended.Code == ProvenanceCodes.Appended, "external edit source appends (" + source.Change + ")", Show(appended));
    }
    var second = ledger.Append(e1, new CatalogueCommandProvenance("element.set_parameter", "op-0002"));
    Check(second.Code == ProvenanceCodes.Appended && ledger.Count == 6, "an element can gain several sources", Show(second) + " count " + ledger.Count);

    // ---- Queries ----
    var byE1 = ledger.ByElement(e1);
    Check(byE1.Select(entry => entry.Sequence).SequenceEqual(new long[] { 1, 6 }) && byE1[0].Source is IntentProvenance && byE1[1].Source is CatalogueCommandProvenance,
        "ByElement lists an element's sources in append order (origin first)", string.Join(",", byE1.Select(entry => entry.Sequence)));
    Check(ledger.ByElement(Guid.NewGuid()).Count == 0, "ByElement of an unknown element is empty");
    var byIntent = ledger.BySource(ProvenanceKinds.Intent, "intent-a");
    Check(byIntent.Ok && byIntent.Entries.Count == 1 && byIntent.Entries[0].ElementId == e1, "BySource finds the intent's elements", byIntent.Code);
    var byOp = ledger.BySource(ProvenanceKinds.CatalogueCommand, "op-0001");
    Check(byOp.Ok && byOp.Entries.Count == 1 && byOp.Entries[0].ElementId == e2, "BySource finds a command operation's elements", byOp.Code);
    var allCommands = ledger.BySource(ProvenanceKinds.CatalogueCommand);
    Check(allCommands.Ok && allCommands.Entries.Select(entry => entry.Sequence).SequenceEqual(new long[] { 2, 6 }), "BySource without an id lists every source of that kind in order", allCommands.Code);
    var byDoc = ledger.BySource(ProvenanceKinds.ExternalEdit, Doc);
    Check(byDoc.Ok && byDoc.Entries.Count == 2, "BySource finds external edits by document UniqueId", byDoc.Code);
    var byFile = ledger.BySource(ProvenanceKinds.CadImport, fileHash);
    Check(byFile.Ok && byFile.Entries.Count == 1 && byFile.Entries[0].ElementId == e3, "BySource finds CAD imports by file hash", byFile.Code);
    Check(ledger.BySource(ProvenanceKinds.CadImport, otherHash) is { Ok: true, Entries.Count: 0 }, "BySource with an unmatched id is empty");
    var unknownQuery = ledger.BySource("revit_magic", "x");
    Check(unknownQuery.Code == ProvenanceCodes.KindUnknown && unknownQuery.Entries.Count == 0, "BySource refuses an unknown kind", unknownQuery.Code);
    Check(ledger.BySource(null).Code == ProvenanceCodes.KindUnknown, "BySource refuses a null kind");

    // ---- Replay: idempotent; conflicting replay refused ----
    var before = ledger.Serialize();
    var replay = ledger.Append(e1, new IntentProvenance("intent-a", "rule.room"));
    Check(replay.Code == ProvenanceCodes.Replayed && replay.Entry is { Sequence: 1 } && ledger.Count == 6 && ledger.Serialize() == before,
        "replaying the same record returns the stored entry and appends nothing", Show(replay));
    var replayCommand = ledger.Append(e2, new CatalogueCommandProvenance("wall.create", "op-0001"));
    Check(replayCommand.Code == ProvenanceCodes.Replayed && replayCommand.Entry is { Sequence: 2 }, "replaying a command record is idempotent", Show(replayCommand));
    var replayEdit = ledger.Append(fromJournal[0].ElementId, fromJournal[0].Source);
    Check(replayEdit.Code == ProvenanceCodes.Replayed, "replaying a journal record is idempotent", Show(replayEdit));
    var conflictIntent = ledger.Append(e1, new IntentProvenance("intent-a", "rule.other"));
    Check(conflictIntent.Code == ProvenanceCodes.Conflict && conflictIntent.Entry is { Sequence: 1 }, "same intent and element with another rule id is a conflict", Show(conflictIntent));
    var conflictCommand = ledger.Append(e2, new CatalogueCommandProvenance("element.delete", "op-0001"));
    Check(conflictCommand.Code == ProvenanceCodes.Conflict, "same operation_id and element with another command is a conflict", Show(conflictCommand));
    var conflictEdit = ledger.Append(fromJournal[0].ElementId, fromJournal[0].Source with { Change = ProvenanceChanges.Modified });
    Check(conflictEdit.Code == ProvenanceCodes.Conflict, "same journal entry and element with another change is a conflict", Show(conflictEdit));
    Check(ledger.Count == 6 && ledger.Serialize() == before, "refused replays leave the ledger unchanged");
    var sameIntentOtherElement = ledger.Append(e3, new IntentProvenance("intent-a", "rule.room"));
    Check(sameIntentOtherElement.Code == ProvenanceCodes.Appended, "one intent may originate several elements", Show(sameIntentOtherElement));
    var restarted = ledger.Append(fromJournal[0].ElementId, fromJournal[0].Source with { RecordedAt = "2026-11-05T08:00:00.000Z" });
    Check(restarted.Code == ProvenanceCodes.Appended, "the same journal sequence at another time (journal restarted) is a new record", Show(restarted));

    // ---- Malformed records refused with typed codes, nothing appended ----
    var count = ledger.Count;
    var wallId = MirrorSnapshotMapper.Id(Doc, W1);
    var good = new ExternalEditProvenance(Doc, 3, "2026-11-04T09:30:00.123Z", W1, ProvenanceChanges.Added);
    var malformed = new (string Name, Guid Id, ProvenanceSource? Source, string Field)[]
    {
        ("empty element id", Guid.Empty, new IntentProvenance("i", "r"), "element_id"),
        ("null source", e1, null, "source"),
        ("empty intent id", e1, new IntentProvenance("", "r"), "intent_id"),
        ("padded intent id", e1, new IntentProvenance(" i", "r"), "intent_id"),
        ("control character in rule id", e1, new IntentProvenance("i", "r\n"), "rule_id"),
        ("lone surrogate in rule id", e1, new IntentProvenance("i", "r\uD800"), "rule_id"),
        ("overlong intent id", e1, new IntentProvenance(new string('x', 257), "r"), "intent_id"),
        ("null rule id", e1, new IntentProvenance("i", null!), "rule_id"),
        ("command outside the catalogue", e1, new CatalogueCommandProvenance("wall.explode", "op-9"), "command"),
        ("bad operation id", e1, new CatalogueCommandProvenance("wall.create", "-op"), "operation_id"),
        ("upper-case file hash", e1, new CadImportProvenance(fileHash.ToUpperInvariant(), "1F"), "file_sha256"),
        ("short file hash", e1, new CadImportProvenance("abc", "1F"), "file_sha256"),
        ("lower-case handle", e1, new CadImportProvenance(fileHash, "1f"), "entity_handle"),
        ("empty handle", e1, new CadImportProvenance(fileHash, ""), "entity_handle"),
        ("overlong handle", e1, new CadImportProvenance(fileHash, "12345678901234567"), "entity_handle"),
        ("upper-case document uid", wallId, good with { DocumentUid = Doc.ToUpperInvariant() }, "document_unique_id"),
        ("journal sequence 0", wallId, good with { JournalSequence = 0 }, "journal_sequence"),
        ("bad recorded_at", wallId, good with { RecordedAt = "2026-11-04 09:30:00" }, "recorded_at"),
        ("bad unique id", wallId, good with { UniqueId = "wall-1" }, "unique_id"),
        ("deleted change", wallId, good with { Change = "deleted" }, "change"),
        ("element id not the mirror id of the UniqueId", e1, good, "element_id"),
    };
    foreach (var (name, id, source, field) in malformed)
    {
        var refused = ledger.Append(id, source);
        Check(refused.Code == ProvenanceCodes.RecordInvalid && refused.Detail == field && refused.Entry is null, "refuses " + name, Show(refused));
    }
    Check(ledger.Count == count, "refused records append nothing");

    // ---- Source factory: unknown kinds and missing / extra fields ----
    var unknown = ProvenanceSource.Create("manual_sketch", new Dictionary<string, string?> { ["id"] = "x" });
    Check(unknown.Code == ProvenanceCodes.KindUnknown && unknown.Source is null, "Create refuses an unknown source kind", unknown.Code);
    Check(ProvenanceSource.Create(null, new Dictionary<string, string?>()).Code == ProvenanceCodes.KindUnknown, "Create refuses a null kind");
    Check(ProvenanceSource.Create("Intent", new Dictionary<string, string?> { ["intent_id"] = "i", ["rule_id"] = "r" }).Code == ProvenanceCodes.KindUnknown, "kinds are case-sensitive");
    var made = ProvenanceSource.Create(ProvenanceKinds.Intent, new Dictionary<string, string?> { ["rule_id"] = "r", ["intent_id"] = "i" });
    Check(made.Ok && made.Source == new IntentProvenance("i", "r"), "Create builds a known kind", made.Code);
    var madeEdit = ProvenanceSource.Create(ProvenanceKinds.ExternalEdit, new Dictionary<string, string?> { ["document_unique_id"] = Doc, ["journal_sequence"] = "3", ["recorded_at"] = "2026-11-04T09:30:00.123Z", ["unique_id"] = W1, ["change"] = "added" });
    Check(madeEdit.Ok && madeEdit.Source == good, "Create builds an external edit with a decimal journal sequence", madeEdit.Code);
    var missing = ProvenanceSource.Create(ProvenanceKinds.CadImport, new Dictionary<string, string?> { ["file_sha256"] = fileHash });
    Check(missing is { Code: ProvenanceCodes.RecordInvalid, Detail: "entity_handle" }, "Create refuses a missing field", missing.Code + " " + missing.Detail);
    var extra = ProvenanceSource.Create(ProvenanceKinds.CadImport, new Dictionary<string, string?> { ["file_sha256"] = fileHash, ["entity_handle"] = "1F", ["layer"] = "A-WALL" });
    Check(extra is { Code: ProvenanceCodes.RecordInvalid, Detail: "layer" }, "Create refuses an extra field", extra.Code + " " + extra.Detail);
    var badSequence = ProvenanceSource.Create(ProvenanceKinds.ExternalEdit, new Dictionary<string, string?> { ["document_unique_id"] = Doc, ["journal_sequence"] = "-3", ["recorded_at"] = "2026-11-04T09:30:00.123Z", ["unique_id"] = W1, ["change"] = "added" });
    Check(badSequence is { Code: ProvenanceCodes.RecordInvalid, Detail: "journal_sequence" }, "Create refuses a non-decimal journal sequence", badSequence.Code);
    Check(ProvenanceKinds.All.SequenceEqual(new[] { "cad_import", "catalogue_command", "external_edit", "intent" }), "the closed kind list");

    // ---- Round trip and determinism ----
    var text = ledger.Serialize();
    var loaded = ProvenanceLedger.Load(text);
    Check(loaded.Ok && loaded.Ledger is not null && loaded.Ledger.Serialize() == text, "load then serialize is byte-identical", loaded.Code + " " + loaded.Detail);
    Check(loaded.Ledger is not null && loaded.Ledger.Entries().SequenceEqual(ledger.Entries()) && loaded.Ledger.HeadHash == ledger.HeadHash, "loaded entries equal the originals");
    if (loaded.Ledger is { } reloaded)
    {
        Check(reloaded.Append(e1, new IntentProvenance("intent-a", "rule.room")).Code == ProvenanceCodes.Replayed, "replay keys survive a reload");
        Check(reloaded.Append(e1, new IntentProvenance("intent-a", "rule.x")).Code == ProvenanceCodes.Conflict, "conflicts are detected after a reload");
        var next = reloaded.Append(e2, new IntentProvenance("intent-b", "rule.room"));
        Check(next.Entry is not null && next.Entry.Sequence == ledger.Count + 1 && next.Entry.PrevHash == ledger.HeadHash, "appending after a reload continues the chain", Show(next));
        Check(reloaded.ByElement(e1).Count == ledger.ByElement(e1).Count && reloaded.BySource(ProvenanceKinds.ExternalEdit, Doc).Entries.Count == 3, "queries work on a loaded ledger");
    }
    var culture = CultureInfo.CurrentCulture;
    try
    {
        foreach (var name in new[] { "tr-TR", "de-DE", "ar-SA" })
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            var again = new ProvenanceLedger();
            foreach (var entry in ledger.Entries()) again.Append(entry.ElementId, entry.Source);
            Check(again.Serialize() == text, "rebuilding the same appends gives the same bytes under " + name);
        }
    }
    finally { CultureInfo.CurrentCulture = culture; }
    var escaped = new ProvenanceLedger();
    escaped.Append(e1, new IntentProvenance("intent é+<&\"\\", "rule"));
    var escapedText = escaped.Serialize();
    var escapedLoad = ProvenanceLedger.Load(escapedText);
    Check(escapedText.Contains("intent \\u00E9\\u002B\\u003C\\u0026\\u0022\\\\", StringComparison.Ordinal) && escapedLoad.Ok && escapedLoad.Ledger!.Serialize() == escapedText,
        "non-ASCII and HTML-sensitive characters use the fixed escapes and round-trip", escapedText);

    // ---- Load refusals: malformed, unknown kinds, tampering, reordering, non-canonical ----
    string Load(string json) { var result = ProvenanceLedger.Load(json); return result.Code + (result.Sequence is null ? "" : "#" + result.Sequence.Value.ToString(CultureInfo.InvariantCulture)) + (result.Detail is null ? "" : "@" + result.Detail); }
    // Code and entry number only (the detail of chain and order refusals is informative text).
    string Located(string json) => Load(json).Split('@')[0];
    var entries = ledger.Entries();
    ProvenanceEntry Rehash(ProvenanceEntry entry) => entry with { Hash = ProvenanceLedgerFormat.ComputeHash(entry) };
    IReadOnlyList<ProvenanceEntry> Rechain(IEnumerable<ProvenanceEntry> list)
    {
        var result = new List<ProvenanceEntry>();
        var prev = Genesis;
        long seq = 0;
        foreach (var entry in list) { var fixedEntry = Rehash(entry with { Sequence = ++seq, PrevHash = prev }); result.Add(fixedEntry); prev = fixedEntry.Hash; }
        return result;
    }
    Check(ProvenanceLedgerFormat.Write(entries) == text, "Write of the entries equals Serialize");
    Check(ProvenanceLedgerFormat.ComputeHash(entries[0]) == entries[0].Hash, "ComputeHash reproduces a stored hash");
    Check(ProvenanceLedgerFormat.Write(Rechain(entries)) == text, "rechaining an intact ledger changes nothing");
    var refusals = new (string Name, string Json, string Expected)[]
    {
        ("null input", null!, ProvenanceCodes.JsonInvalid),
        ("not JSON", "{\"provenance_ledger_version\":1,", ProvenanceCodes.JsonInvalid),
        ("a JSON array", "[]", ProvenanceCodes.JsonInvalid),
        ("missing entries", "{\"provenance_ledger_version\":1}", ProvenanceCodes.JsonInvalid),
        ("unknown top-level member", "{\"provenance_ledger_version\":1,\"entries\":[],\"x\":1}", ProvenanceCodes.JsonInvalid),
        ("duplicate top-level member", "{\"provenance_ledger_version\":1,\"provenance_ledger_version\":1,\"entries\":[]}", ProvenanceCodes.JsonInvalid),
        ("future version", "{\"provenance_ledger_version\":2,\"entries\":[]}", ProvenanceCodes.VersionUnsupported),
        ("string version", "{\"provenance_ledger_version\":\"1\",\"entries\":[]}", ProvenanceCodes.VersionUnsupported),
        ("whitespace", text.Replace("\"entries\":[", "\"entries\": [", StringComparison.Ordinal), ProvenanceCodes.NotCanonical),
        ("trailing newline", text + "\n", ProvenanceCodes.NotCanonical),
        ("member order", "{\"entries\":[],\"provenance_ledger_version\":1}", ProvenanceCodes.NotCanonical),
    };
    foreach (var (name, json, expected) in refusals)
    {
        var code = Load(json);
        Check(code.Split('#', '@')[0] == expected, "load refuses " + name, code);
    }
    var swapped = new List<ProvenanceEntry>(entries); (swapped[1], swapped[2]) = (swapped[2], swapped[1]);
    Check(Located(ProvenanceLedgerFormat.Write(swapped)) == ProvenanceCodes.SequenceBroken + "#2", "load refuses entries out of order", Load(ProvenanceLedgerFormat.Write(swapped)));
    var gap = entries.Where(entry => entry.Sequence != 3).ToList();
    Check(Load(ProvenanceLedgerFormat.Write(gap)).StartsWith(ProvenanceCodes.SequenceBroken + "#", StringComparison.Ordinal), "load refuses a removed middle entry", Load(ProvenanceLedgerFormat.Write(gap)));
    var edited = new List<ProvenanceEntry>(entries) { [2] = entries[2] with { Source = new CadImportProvenance(otherHash, "2A") } };
    Check(Located(ProvenanceLedgerFormat.Write(edited)) == ProvenanceCodes.HashMismatch + "#3", "load refuses an edited entry whose hash was not recomputed", Load(ProvenanceLedgerFormat.Write(edited)));
    var rehashed = new List<ProvenanceEntry>(entries) { [2] = Rehash(entries[2] with { Source = new CadImportProvenance(otherHash, "2A") }) };
    Check(Located(ProvenanceLedgerFormat.Write(rehashed)) == ProvenanceCodes.ChainBroken + "#4", "load refuses an edited entry whose own hash was recomputed (the next prev_hash breaks)", Load(ProvenanceLedgerFormat.Write(rehashed)));
    var forkedPrev = new List<ProvenanceEntry>(entries) { [1] = Rehash(entries[1] with { PrevHash = otherHash }) };
    Check(Located(ProvenanceLedgerFormat.Write(forkedPrev)) == ProvenanceCodes.ChainBroken + "#2", "load refuses a prev_hash that is not the previous hash", Load(ProvenanceLedgerFormat.Write(forkedPrev)));
    var swappedRechained = Rechain(swapped);
    Check(Load(ProvenanceLedgerFormat.Write(swappedRechained)) == ProvenanceCodes.Ok, "a reordered but fully rechained ledger is a different valid ledger (the chain is unkeyed)");
    var duplicate = Rechain(entries.Append(entries[0]));
    Check(Located(ProvenanceLedgerFormat.Write(duplicate)) == ProvenanceCodes.DuplicateEntry + "#" + duplicate.Count.ToString(CultureInfo.InvariantCulture), "load refuses a repeated record", Load(ProvenanceLedgerFormat.Write(duplicate)));
    var conflicting = Rechain(entries.Append(entries[0] with { Source = new IntentProvenance("intent-a", "rule.other") }));
    Check(Located(ProvenanceLedgerFormat.Write(conflicting)) == ProvenanceCodes.Conflict + "#" + conflicting.Count.ToString(CultureInfo.InvariantCulture), "load refuses a conflicting record", Load(ProvenanceLedgerFormat.Write(conflicting)));
    var invalidStored = Rechain(new[] { entries[0] with { Source = new CadImportProvenance(fileHash, "zz") } });
    Check(Load(ProvenanceLedgerFormat.Write(invalidStored)) == ProvenanceCodes.RecordInvalid + "#1@entity_handle", "load refuses a malformed stored record with its field", Load(ProvenanceLedgerFormat.Write(invalidStored)));
    var emptyElement = Rechain(new[] { entries[0] with { ElementId = Guid.Empty } });
    Check(Load(ProvenanceLedgerFormat.Write(emptyElement)) == ProvenanceCodes.RecordInvalid + "#1@element_id", "load refuses an empty element id", Load(ProvenanceLedgerFormat.Write(emptyElement)));

    // Hand-made entry JSON: unknown kind, missing / extra / mistyped members, non-canonical values.
    string Entry(string source, string elementId = "11111111-2222-3333-4444-555555555555", string seq = "1", string tail = "")
        => "{\"provenance_ledger_version\":1,\"entries\":[{\"seq\":" + seq + ",\"element_id\":\"" + elementId + "\",\"source\":" + source + ",\"prev_hash\":\"" + Genesis + "\",\"hash\":\"" + Genesis + "\"" + tail + "}]}";
    var entryRefusals = new (string Name, string Json, string Expected)[]
    {
        ("unknown kind", Entry("{\"kind\":\"photo_survey\",\"image\":\"x\"}"), ProvenanceCodes.KindUnknown + "#1@kind"),
        ("missing kind", Entry("{\"intent_id\":\"i\",\"rule_id\":\"r\"}"), ProvenanceCodes.RecordInvalid + "#1@kind"),
        ("numeric kind", Entry("{\"kind\":1,\"intent_id\":\"i\",\"rule_id\":\"r\"}"), ProvenanceCodes.RecordInvalid + "#1@kind"),
        ("missing field", Entry("{\"kind\":\"intent\",\"intent_id\":\"i\"}"), ProvenanceCodes.RecordInvalid + "#1@rule_id"),
        ("extra field", Entry("{\"kind\":\"intent\",\"intent_id\":\"i\",\"rule_id\":\"r\",\"note\":\"n\"}"), ProvenanceCodes.RecordInvalid + "#1@note"),
        ("numeric field", Entry("{\"kind\":\"intent\",\"intent_id\":1,\"rule_id\":\"r\"}"), ProvenanceCodes.RecordInvalid + "#1@intent_id"),
        ("string journal sequence", Entry("{\"kind\":\"external_edit\",\"document_unique_id\":\"" + Doc + "\",\"journal_sequence\":\"1\",\"recorded_at\":\"2026-11-04T09:30:00.123Z\",\"unique_id\":\"" + W1 + "\",\"change\":\"added\"}", MirrorSnapshotMapper.Id(Doc, W1).ToString("D")), ProvenanceCodes.RecordInvalid + "#1@journal_sequence"),
        ("fractional journal sequence", Entry("{\"kind\":\"external_edit\",\"document_unique_id\":\"" + Doc + "\",\"journal_sequence\":1.5,\"recorded_at\":\"2026-11-04T09:30:00.123Z\",\"unique_id\":\"" + W1 + "\",\"change\":\"added\"}", MirrorSnapshotMapper.Id(Doc, W1).ToString("D")), ProvenanceCodes.RecordInvalid + "#1@journal_sequence"),
        ("source not an object", Entry("\"intent\""), ProvenanceCodes.RecordInvalid + "#1@source"),
        ("bad element id", Entry("{\"kind\":\"intent\",\"intent_id\":\"i\",\"rule_id\":\"r\"}", "not-a-guid"), ProvenanceCodes.RecordInvalid + "#1@element_id"),
        ("string seq", Entry("{\"kind\":\"intent\",\"intent_id\":\"i\",\"rule_id\":\"r\"}", seq: "\"1\""), ProvenanceCodes.RecordInvalid + "#1@seq"),
        ("extra entry member", Entry("{\"kind\":\"intent\",\"intent_id\":\"i\",\"rule_id\":\"r\"}", tail: ",\"note\":1"), ProvenanceCodes.RecordInvalid + "#1@note"),
        ("bad hash text", Entry("{\"kind\":\"intent\",\"intent_id\":\"i\",\"rule_id\":\"r\"}").Replace("\"hash\":\"" + Genesis, "\"hash\":\"XYZ", StringComparison.Ordinal), ProvenanceCodes.RecordInvalid + "#1@hash"),
        ("entry not an object", "{\"provenance_ledger_version\":1,\"entries\":[1]}", ProvenanceCodes.RecordInvalid + "#1@entry"),
    };
    foreach (var (name, json, expected) in entryRefusals)
        Check(Load(json) == expected, "load refuses " + name, Load(json));
    var lettered = entries.First(entry => entry.ElementId.ToString("D").Any(char.IsLetter)).ElementId.ToString("D");
    var upperGuid = text.Replace("\"element_id\":\"" + lettered + "\"", "\"element_id\":\"" + lettered.ToUpperInvariant() + "\"", StringComparison.Ordinal);
    Check(upperGuid != text && Load(upperGuid) == ProvenanceCodes.NotCanonical, "an upper-case element id is refused as not canonical (its hash still matches the parsed id)", Load(upperGuid));

    // ---- Append-only surface ----
    var mutators = typeof(ProvenanceLedger).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        .Select(method => method.Name).Where(name => name is not ("Append" or "ByElement" or "BySource" or "Entries" or "Serialize" or "Load" or "get_Count" or "get_HeadHash")).OrderBy(name => name, StringComparer.Ordinal).ToArray();
    Check(mutators.Length == 0, "the ledger exposes no edit, delete, insert or clear member", string.Join(",", mutators));
    Check(typeof(ProvenanceLedger).GetProperties(BindingFlags.Public | BindingFlags.Instance).All(property => !property.CanWrite), "the ledger has no writable property");
    var listed = ledger.Entries();
    Check(listed is not ICollection<ProvenanceEntry> collection || collection.IsReadOnly, "Entries is read-only");
}
catch (Exception error)
{
    Check(false, "provenance oracle threw", error.GetType().Name + ": " + error.Message + " " + error.StackTrace);
}
Console.WriteLine($"provenance: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
