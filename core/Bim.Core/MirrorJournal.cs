using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Core;

// P2-03 external edit journal. Vendor-free: the add-in's Application.DocumentChanged recorder (JournalRecorder.cs in
// the native runtime) converts the event's added/modified element ids to UniqueIds, keeps deleted element ids as numbers
// (a deleted element can no longer be resolved to its UniqueId), and hands one MirrorJournalChange per event to
// MirrorJournal.Record. The journal is a bounded, append-only, in-memory diagnostic: it is never written to the Core
// store, the document or any persisted format; ToJson is a separately versioned diagnostic reply (journal_version 1).
//
// A document is identified by its ProjectInformation UniqueId and its path: two copies of one file share every UniqueId,
// so the path keeps their entries and baselines apart (an unsaved document has the path "").

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
    public string DocumentPath { get; init; } = "";
}

// One recorded entry. Id lists are distinct and sorted (ordinal); totals are the true counts, so Truncated says a list
// holds fewer ids than the event reported, or a transaction name or the name list was cut.
public sealed record MirrorJournalEntry(long Sequence, DateTimeOffset RecordedAt, string DocumentUid, string Operation, string Origin,
    IReadOnlyList<string> TransactionNames, IReadOnlyList<string> AddedUids, IReadOnlyList<string> ModifiedUids, IReadOnlyList<long> DeletedIds,
    int AddedTotal, int ModifiedTotal, int DeletedTotal, string DocumentPath = "", bool NamesTruncated = false)
{
    public bool Truncated => NamesTruncated || AddedTotal > AddedUids.Count || ModifiedTotal > ModifiedUids.Count || DeletedTotal > DeletedIds.Count;
    public int IdCount => AddedUids.Count + ModifiedUids.Count + DeletedIds.Count;

    internal bool Matches(string? documentUid, string? documentPath) =>
        (documentUid is null || string.Equals(DocumentUid, documentUid, StringComparison.OrdinalIgnoreCase))
        && (documentPath is null || string.Equals(DocumentPath, documentPath, StringComparison.OrdinalIgnoreCase));

    internal void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("sequence", Sequence);
        writer.WriteString("recorded_at", RecordedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        writer.WriteString("document_unique_id", DocumentUid);
        writer.WriteString("document_path", DocumentPath);
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
// Two bounds: at most Capacity entries and at most IdBudget ids over all retained entries; past either, the oldest
// entries are dropped and counted (the newest entry is always kept). Sequences are never reused, so a reader can tell
// from FirstRetainedSequence whether entries after its baseline were lost. Add-in changes are skipped by default
// (counted in SkippedAddIn, no sequence consumed) because the add-in already reports them in its command receipts.
public sealed class MirrorJournal
{
    public const int Version = 1;
    public const int DefaultCapacity = 1024;
    public const int DefaultIdBudget = 200_000;
    public const int MaxIdsPerList = 2000;
    public const int MaxNames = 64;
    public const int MaxNameLength = 256;

    // Every transaction, transaction group and edit scope name the add-in opens (App.cs, CommandsExecutor*.cs,
    // BatchExecutor.cs, NativeRoomFixture.cs; a source-scan test keeps this list complete). Ordinal and case-sensitive:
    // a user transaction can only match by copying them.
    static readonly string[] AddInPrefixes = { "BIM Agentic OS ", "Isolated native Room " };
    static readonly HashSet<string> AddInNames = new(StringComparer.Ordinal)
    {
        "BIM Agentic typed batch", "Apply typed operations", "Persist logical map and receipt",
        "Preflight wall-hosted family resources", "Compile native Room A", "Apply native width diff",
    };

    readonly object gate = new();
    readonly Func<DateTimeOffset> clock;
    readonly Queue<MirrorJournalEntry> entries = new();
    readonly bool recordAddIn;
    long lastSequence, dropped, skippedAddIn, failures, unidentified, retainedIds;

    public MirrorJournal(Func<DateTimeOffset> clock, int capacity = DefaultCapacity, bool recordAddIn = false, int idBudget = DefaultIdBudget)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (capacity < 1) throw new ArgumentException("journal_capacity_invalid");
        if (idBudget < 1) throw new ArgumentException("journal_id_budget_invalid");
        this.clock = clock;
        Capacity = capacity;
        IdBudget = idBudget;
        this.recordAddIn = recordAddIn;
    }

    public int Capacity { get; }
    public int IdBudget { get; }
    public long LastSequence { get { lock (gate) return lastSequence; } }
    public long Dropped { get { lock (gate) return dropped; } }
    public long SkippedAddIn { get { lock (gate) return skippedAddIn; } }
    public long Failures { get { lock (gate) return failures; } }
    public long Unidentified { get { lock (gate) return unidentified; } }
    public long RetainedIds { get { lock (gate) return retainedIds; } }
    // The oldest sequence still held, or LastSequence + 1 when the journal is empty.
    public long FirstRetainedSequence { get { lock (gate) return entries.Count > 0 ? entries.Peek().Sequence : lastSequence + 1; } }

    public static bool IsAddInTransaction(string? name) =>
        !string.IsNullOrEmpty(name) && (AddInNames.Contains(name) || AddInPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)));

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
        var allNames = (change.TransactionNames ?? Array.Empty<string>()).Where(name => name is not null).ToArray();
        var names = allNames.Take(MaxNames).Select(name => name.Length > MaxNameLength ? name[..MaxNameLength] : name).ToArray();
        var namesTruncated = allNames.Length > MaxNames || allNames.Take(MaxNames).Any(name => name.Length > MaxNameLength);
        var operation = string.IsNullOrWhiteSpace(change.Operation) ? "unknown" : change.Operation;
        lock (gate)
        {
            if (origin == MirrorJournalOrigins.AddIn && !recordAddIn) { skippedAddIn++; return null; }
            var entry = new MirrorJournalEntry(++lastSequence, clock(), change.DocumentUid, operation, origin, names,
                added.Take(MaxIdsPerList).ToArray(), modified.Take(MaxIdsPerList).ToArray(), deleted.Take(MaxIdsPerList).ToArray(),
                Math.Max(change.AddedTotal, added.Length), Math.Max(change.ModifiedTotal, modified.Length), Math.Max(change.DeletedTotal, deleted.Length),
                change.DocumentPath ?? "", namesTruncated);
            entries.Enqueue(entry);
            retainedIds += entry.IdCount;
            while (entries.Count > 1 && (entries.Count > Capacity || retainedIds > IdBudget))
            {
                retainedIds -= entries.Dequeue().IdCount;
                dropped++;
            }
            return entry;
        }
    }

    // A recorder fault (for example an element that could not be read) is counted, never thrown into Revit.
    public void RecordFailure() { lock (gate) failures++; }

    // An event from a project document without a ProjectInformation UniqueId cannot be attributed; it is counted.
    public void RecordUnidentified() { lock (gate) unidentified++; }

    public IReadOnlyList<MirrorJournalEntry> Entries(string? documentUid = null, long afterSequence = 0, string? documentPath = null)
    {
        lock (gate) return entries.Where(entry => entry.Sequence > afterSequence && entry.Matches(documentUid, documentPath)).ToArray();
    }

    // {"journal_version":1,"capacity","last_sequence","dropped","skipped_add_in","failures","unidentified","id_budget",
    //  "retained_ids","entries":[..]}
    public string ToJson(string? documentUid = null, long afterSequence = 0, string? documentPath = null)
    {
        long last, drop, skipped, fail, unknown, ids;
        IReadOnlyList<MirrorJournalEntry> selected;
        lock (gate)
        {
            last = lastSequence; drop = dropped; skipped = skippedAddIn; fail = failures; unknown = unidentified; ids = retainedIds;
            selected = Entries(documentUid, afterSequence, documentPath);
        }
        return MirrorJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("journal_version", Version);
            writer.WriteNumber("capacity", Capacity);
            writer.WriteNumber("last_sequence", last);
            writer.WriteNumber("dropped", drop);
            writer.WriteNumber("skipped_add_in", skipped);
            writer.WriteNumber("failures", fail);
            writer.WriteNumber("unidentified", unknown);
            writer.WriteNumber("id_budget", IdBudget);
            writer.WriteNumber("retained_ids", ids);
            writer.WriteStartArray("entries");
            foreach (var entry in selected) entry.Write(writer);
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    static string[] Uids(IReadOnlyList<string>? uids) =>
        (uids ?? Array.Empty<string>()).Where(uid => !string.IsNullOrWhiteSpace(uid)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}

// journal.read options. AfterSequence (default: the baseline) pages or re-reads entries; Peek answers without moving the
// baseline, so a retried or paged read sees the same diff. MaxEntries / MaxIds cap one reply (at least one entry).
public sealed record MirrorJournalReadOptions(long? AfterSequence = null, bool Peek = false, int MaxEntries = MirrorJournalReadOptions.DefaultMaxEntries, int MaxIds = MirrorJournalReadOptions.DefaultMaxIds)
{
    public const int DefaultMaxEntries = 256;
    public const int DefaultMaxIds = 20_000;
    public const string InvalidCode = "journal_options_invalid";

    // The journal.read request member: null / empty, or {"after_sequence": integer >= 0, "peek": bool, "max_entries": 1..256}.
    public static MirrorJournalReadOptions Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Null) return new();
            if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException(InvalidCode + " options must be an object");
            var options = new MirrorJournalReadOptions();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in root.EnumerateObject())
            {
                if (!seen.Add(member.Name)) throw new ArgumentException(InvalidCode + " duplicate " + member.Name);
                var value = member.Value;
                options = member.Name switch
                {
                    "after_sequence" when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var after) && after >= 0 => options with { AfterSequence = after },
                    "peek" when value.ValueKind is JsonValueKind.True or JsonValueKind.False => options with { Peek = value.GetBoolean() },
                    "max_entries" when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var max) && max >= 1 && max <= DefaultMaxEntries => options with { MaxEntries = max },
                    _ => throw new ArgumentException(InvalidCode + " " + member.Name),
                };
            }
            return options;
        }
        catch (JsonException) { throw new ArgumentException(InvalidCode + " not JSON"); }
    }

    internal void Validate()
    {
        if (MaxEntries < 1 || MaxIds < 1 || AfterSequence < 0) throw new ArgumentException(InvalidCode);
    }
}

// What changed since the last snapshot of one document: the external journal entries recorded after AfterSequence
// (the baseline unless the client gave one) and the diff of the new snapshot against the baseline snapshot.
// JournalComplete is false when entries after AfterSequence were dropped by the journal's bounds or the recorder
// failed since the baseline (the diff is still complete; it never depends on the journal). HasMore says the reply was
// capped; continue with after_sequence = NextAfterSequence (and peek, so the baseline stays put).
public sealed record MirrorJournalReport(string DocumentUid, string DocumentPath, long BaselineSequence, long CurrentSequence, bool BaselineExisted, bool JournalComplete,
    IReadOnlyList<MirrorJournalEntry> Entries, MirrorSnapshotDiff Diff)
{
    public long AfterSequence { get; init; }
    public bool HasMore { get; init; }
    public long NextAfterSequence { get; init; }
    public bool Peek { get; init; }
    public long FailuresSinceBaseline { get; init; }

    // {"journal_version":1,"document_unique_id","document_path","baseline_sequence","current_sequence","after_sequence",
    //  "baseline_existed","peek","journal_complete","failures_since_baseline","has_more","next_after_sequence",
    //  "entries":[..],"diff":{..}}
    public string ToJson() => MirrorJson.Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteNumber("journal_version", MirrorJournal.Version);
        writer.WriteString("document_unique_id", DocumentUid);
        writer.WriteString("document_path", DocumentPath);
        writer.WriteNumber("baseline_sequence", BaselineSequence);
        writer.WriteNumber("current_sequence", CurrentSequence);
        writer.WriteNumber("after_sequence", AfterSequence);
        writer.WriteBoolean("baseline_existed", BaselineExisted);
        writer.WriteBoolean("peek", Peek);
        writer.WriteBoolean("journal_complete", JournalComplete);
        writer.WriteNumber("failures_since_baseline", FailuresSinceBaseline);
        writer.WriteBoolean("has_more", HasMore);
        writer.WriteNumber("next_after_sequence", NextAfterSequence);
        writer.WriteStartArray("entries");
        foreach (var entry in Entries) entry.Write(writer);
        writer.WriteEndArray();
        writer.WritePropertyName("diff");
        Diff.Write(writer);
        writer.WriteEndObject();
    });
}

// Holds the journal and, per document (UniqueId and path), the last snapshot reported, the journal sequence and the
// recorder failure count at that moment. Baselines live in memory only, for the life of the add-in session.
public sealed class MirrorJournalTracker
{
    readonly object gate = new();
    readonly Dictionary<(string Uid, string Path), (MirrorSnapshot Snapshot, long Sequence, long Failures)> baselines = new(new DocumentKeyComparer());

    public MirrorJournalTracker(MirrorJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        Journal = journal;
    }

    public MirrorJournal Journal { get; }

    public bool HasBaseline(string documentUid, string documentPath = "") { lock (gate) return baselines.ContainsKey((documentUid, documentPath ?? "")); }

    // Reports the change since the last report for the snapshot's document and, unless options.Peek, makes this
    // snapshot the new baseline.
    public MirrorJournalReport Report(MirrorSnapshot current, string documentPath = "", MirrorJournalReadOptions? options = null)
    {
        if (current is null) throw new ArgumentException("snapshot_required");
        options ??= new MirrorJournalReadOptions();
        options.Validate();
        var path = documentPath ?? "";
        lock (gate)
        {
            var key = (current.DocumentUid, path);
            var existed = baselines.TryGetValue(key, out var baseline);
            var since = existed ? baseline.Sequence : 0;
            var sequence = Journal.LastSequence;
            var failures = Journal.Failures;
            var after = options.AfterSequence ?? since;
            if (after > sequence) throw new ArgumentException("journal_after_sequence_invalid");
            var diff = MirrorSnapshotDiffer.Compare(existed ? baseline.Snapshot : null, current);
            var candidates = Journal.Entries(current.DocumentUid, after, path).Where(entry => entry.Sequence <= sequence).ToArray();
            var page = new List<MirrorJournalEntry>();
            var ids = 0L;
            foreach (var entry in candidates)
            {
                if (page.Count > 0 && (page.Count >= options.MaxEntries || ids + entry.IdCount > options.MaxIds)) break;
                page.Add(entry);
                ids += entry.IdCount;
            }
            var hasMore = page.Count < candidates.Length;
            var failuresSince = failures - (existed ? baseline.Failures : 0);
            var complete = Journal.FirstRetainedSequence <= after + 1 && failuresSince == 0;
            if (!options.Peek) baselines[key] = (current, sequence, failures);
            return new MirrorJournalReport(current.DocumentUid, path, since, sequence, existed, complete, page, diff)
            {
                AfterSequence = after,
                HasMore = hasMore,
                NextAfterSequence = hasMore ? page[^1].Sequence : sequence,
                Peek = options.Peek,
                FailuresSinceBaseline = failuresSince,
            };
        }
    }

    sealed class DocumentKeyComparer : IEqualityComparer<(string Uid, string Path)>
    {
        public bool Equals((string Uid, string Path) x, (string Uid, string Path) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Uid, y.Uid) && StringComparer.OrdinalIgnoreCase.Equals(x.Path, y.Path);
        public int GetHashCode((string Uid, string Path) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Uid), StringComparer.OrdinalIgnoreCase.GetHashCode(key.Path));
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
