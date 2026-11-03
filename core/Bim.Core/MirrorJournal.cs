using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Core;

// P2-03 external edit journal. Revit-free: the add-in's Application.DocumentChanged recorder (runtimes/revit-native/
// JournalRecorder.cs) converts the event's added/modified element ids to UniqueIds, keeps deleted element ids as numbers
// (a deleted element can no longer be resolved to its UniqueId), and hands one MirrorJournalChange per event to
// MirrorJournal.Record. The journal is a bounded, append-only, in-memory diagnostic: it is never written to the Core
// store, the document or any persisted format; ToJson is a separately versioned diagnostic reply (journal_version 1).

public static class MirrorJournalOrigins
{
    // A change made by someone or something other than this add-in (the user, another add-in, undo/redo).
    public const string External = "external";
    // A change made by this add-in's own command, batch or fixture transactions.
    public const string AddIn = "add-in";
}

// One DocumentChanged event as the recorder observed it. Operation is the UndoOperation text (TransactionCommitted,
// TransactionUndone, TransactionRedone, TransactionRolledBack, TransactionGroupRolledBack, ...). InsideAddInDispatch is
// true when the event fired while the add-in was executing a request (App.Execute). A recorder that capped the id lists
// itself passes the true totals; 0 means "the list count".
public sealed record MirrorJournalChange(string DocumentUid, string Operation, IReadOnlyList<string> TransactionNames,
    IReadOnlyList<string> AddedUids, IReadOnlyList<string> ModifiedUids, IReadOnlyList<long> DeletedIds, bool InsideAddInDispatch = false)
{
    public int AddedTotal { get; init; }
    public int ModifiedTotal { get; init; }
    public int DeletedTotal { get; init; }
}

// One recorded entry. Id lists are distinct and sorted (ordinal); totals are the true counts, so Truncated says a list
// holds fewer ids than the event reported.
public sealed record MirrorJournalEntry(long Sequence, DateTimeOffset RecordedAt, string DocumentUid, string Operation, string Origin,
    IReadOnlyList<string> TransactionNames, IReadOnlyList<string> AddedUids, IReadOnlyList<string> ModifiedUids, IReadOnlyList<long> DeletedIds,
    int AddedTotal, int ModifiedTotal, int DeletedTotal)
{
    public bool Truncated => AddedTotal > AddedUids.Count || ModifiedTotal > ModifiedUids.Count || DeletedTotal > DeletedIds.Count;

    internal void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("sequence", Sequence);
        writer.WriteString("recorded_at", RecordedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        writer.WriteString("document_unique_id", DocumentUid);
        writer.WriteString("operation", Operation);
        writer.WriteString("origin", Origin);
        MirrorJson.Strings(writer, "transaction_names", TransactionNames);
        MirrorJson.Strings(writer, "added", AddedUids);
        MirrorJson.Strings(writer, "modified", ModifiedUids);
        writer.WriteStartArray("deleted_element_ids");
        foreach (var id in DeletedIds) writer.WriteNumberValue(id);
        writer.WriteEndArray();
        writer.WriteNumber("added_total", AddedTotal);
        writer.WriteNumber("modified_total", ModifiedTotal);
        writer.WriteNumber("deleted_total", DeletedTotal);
        writer.WriteBoolean("truncated", Truncated);
        writer.WriteEndObject();
    }
}

// Bounded append-only journal. Thread-safe (DocumentChanged fires on Revit's main thread, but reads may not).
// When full, the oldest entry is dropped and counted; sequences are never reused, so a reader can tell from
// FirstRetainedSequence whether entries after its baseline were lost. Add-in changes are skipped by default
// (counted in SkippedAddIn, no sequence consumed) because the add-in already reports them in its command receipts.
public sealed class MirrorJournal
{
    public const int Version = 1;
    public const int DefaultCapacity = 1024;
    public const int MaxIdsPerList = 2000;
    public const int MaxNames = 64;
    public const int MaxNameLength = 256;

    // Every transaction or transaction group name the add-in opens (App.cs, CommandsExecutor*.cs, BatchExecutor.cs,
    // NativeRoomFixture.cs). Ordinal, case-sensitive prefixes: a user transaction can only match by copying them.
    static readonly string[] AddInPrefixes = { "BIM Agentic OS ", "BIM Agentic typed batch", "Apply typed operations", "Persist logical map and receipt", "Isolated native Room" };

    readonly object gate = new();
    readonly Func<DateTimeOffset> clock;
    readonly Queue<MirrorJournalEntry> entries = new();
    readonly bool recordAddIn;
    long lastSequence, dropped, skippedAddIn, failures;

    public MirrorJournal(Func<DateTimeOffset> clock, int capacity = DefaultCapacity, bool recordAddIn = false)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (capacity < 1) throw new ArgumentException("journal_capacity_invalid");
        this.clock = clock;
        Capacity = capacity;
        this.recordAddIn = recordAddIn;
    }

    public int Capacity { get; }
    public long LastSequence { get { lock (gate) return lastSequence; } }
    public long Dropped { get { lock (gate) return dropped; } }
    public long SkippedAddIn { get { lock (gate) return skippedAddIn; } }
    public long Failures { get { lock (gate) return failures; } }
    // The oldest sequence still held, or LastSequence + 1 when the journal is empty.
    public long FirstRetainedSequence { get { lock (gate) return entries.Count > 0 ? entries.Peek().Sequence : lastSequence + 1; } }

    public static bool IsAddInTransaction(string? name) =>
        !string.IsNullOrEmpty(name) && AddInPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    // Inside the add-in's dispatch every change is the add-in's (including its rollbacks). Outside it, only a commit
    // whose transaction names are all add-in names is; undo and redo are always the user's, even of add-in work.
    public static string Origin(MirrorJournalChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.InsideAddInDispatch) return MirrorJournalOrigins.AddIn;
        var names = change.TransactionNames ?? Array.Empty<string>();
        return change.Operation == "TransactionCommitted" && names.Count > 0 && names.All(IsAddInTransaction) ? MirrorJournalOrigins.AddIn : MirrorJournalOrigins.External;
    }

    // Records one change; returns the entry, or null when it was an add-in change and add-in changes are skipped.
    public MirrorJournalEntry? Record(MirrorJournalChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (string.IsNullOrWhiteSpace(change.DocumentUid)) throw new ArgumentException("journal_document_required");
        var origin = Origin(change);
        var added = Uids(change.AddedUids);
        var modified = Uids(change.ModifiedUids);
        var deleted = (change.DeletedIds ?? Array.Empty<long>()).Distinct().Order().ToArray();
        var names = (change.TransactionNames ?? Array.Empty<string>()).Where(name => name is not null).Take(MaxNames)
            .Select(name => name.Length > MaxNameLength ? name[..MaxNameLength] : name).ToArray();
        var operation = string.IsNullOrWhiteSpace(change.Operation) ? "unknown" : change.Operation;
        lock (gate)
        {
            if (origin == MirrorJournalOrigins.AddIn && !recordAddIn) { skippedAddIn++; return null; }
            var entry = new MirrorJournalEntry(++lastSequence, clock(), change.DocumentUid, operation, origin, names,
                added.Take(MaxIdsPerList).ToArray(), modified.Take(MaxIdsPerList).ToArray(), deleted.Take(MaxIdsPerList).ToArray(),
                Math.Max(change.AddedTotal, added.Length), Math.Max(change.ModifiedTotal, modified.Length), Math.Max(change.DeletedTotal, deleted.Length));
            entries.Enqueue(entry);
            while (entries.Count > Capacity) { entries.Dequeue(); dropped++; }
            return entry;
        }
    }

    // A recorder fault (for example an element that could not be read) is counted, never thrown into Revit.
    public void RecordFailure() { lock (gate) failures++; }

    public IReadOnlyList<MirrorJournalEntry> Entries(string? documentUid = null, long afterSequence = 0)
    {
        lock (gate)
            return entries.Where(entry => entry.Sequence > afterSequence && (documentUid is null || string.Equals(entry.DocumentUid, documentUid, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    // {"journal_version":1,"capacity","last_sequence","dropped","skipped_add_in","failures","entries":[..]}
    public string ToJson(string? documentUid = null, long afterSequence = 0)
    {
        long last, drop, skipped, fail;
        IReadOnlyList<MirrorJournalEntry> selected;
        lock (gate) { last = lastSequence; drop = dropped; skipped = skippedAddIn; fail = failures; selected = Entries(documentUid, afterSequence); }
        return MirrorJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("journal_version", Version);
            writer.WriteNumber("capacity", Capacity);
            writer.WriteNumber("last_sequence", last);
            writer.WriteNumber("dropped", drop);
            writer.WriteNumber("skipped_add_in", skipped);
            writer.WriteNumber("failures", fail);
            writer.WriteStartArray("entries");
            foreach (var entry in selected) entry.Write(writer);
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    static string[] Uids(IReadOnlyList<string>? uids) =>
        (uids ?? Array.Empty<string>()).Where(uid => !string.IsNullOrWhiteSpace(uid)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}

// What changed since the last snapshot of one document: the external journal entries recorded after it and the diff
// of the new snapshot against it. JournalComplete is false when entries after the baseline were dropped by the
// journal's capacity (the diff is still complete; it never depends on the journal).
public sealed record MirrorJournalReport(string DocumentUid, long BaselineSequence, long CurrentSequence, bool BaselineExisted, bool JournalComplete,
    IReadOnlyList<MirrorJournalEntry> Entries, MirrorSnapshotDiff Diff)
{
    // {"journal_version":1,"document_unique_id","baseline_sequence","current_sequence","baseline_existed",
    //  "journal_complete","entries":[..],"diff":{..}}
    public string ToJson() => MirrorJson.Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteNumber("journal_version", MirrorJournal.Version);
        writer.WriteString("document_unique_id", DocumentUid);
        writer.WriteNumber("baseline_sequence", BaselineSequence);
        writer.WriteNumber("current_sequence", CurrentSequence);
        writer.WriteBoolean("baseline_existed", BaselineExisted);
        writer.WriteBoolean("journal_complete", JournalComplete);
        writer.WriteStartArray("entries");
        foreach (var entry in Entries) entry.Write(writer);
        writer.WriteEndArray();
        writer.WritePropertyName("diff");
        Diff.Write(writer);
        writer.WriteEndObject();
    });
}

// Holds the journal and, per document, the last snapshot reported and the journal sequence at that moment.
// Baselines live in memory only, for the life of the add-in session.
public sealed class MirrorJournalTracker
{
    readonly object gate = new();
    readonly Dictionary<string, (MirrorSnapshot Snapshot, long Sequence)> baselines = new(StringComparer.OrdinalIgnoreCase);

    public MirrorJournalTracker(MirrorJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        Journal = journal;
    }

    public MirrorJournal Journal { get; }

    public bool HasBaseline(string documentUid) { lock (gate) return baselines.ContainsKey(documentUid); }

    // Reports the change since the last report for the snapshot's document and makes this snapshot the new baseline.
    public MirrorJournalReport Report(MirrorSnapshot current)
    {
        if (current is null) throw new ArgumentException("snapshot_required");
        lock (gate)
        {
            var existed = baselines.TryGetValue(current.DocumentUid, out var baseline);
            var since = existed ? baseline.Sequence : 0;
            var diff = MirrorSnapshotDiffer.Compare(existed ? baseline.Snapshot : null, current);
            var sequence = Journal.LastSequence;
            var entries = Journal.Entries(current.DocumentUid, since).Where(entry => entry.Sequence <= sequence).ToArray();
            var complete = Journal.FirstRetainedSequence <= since + 1;
            baselines[current.DocumentUid] = (current, sequence);
            return new MirrorJournalReport(current.DocumentUid, since, sequence, existed, complete, entries, diff);
        }
    }
}

static class MirrorJson
{
    public static string Write(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static void Strings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }
}
