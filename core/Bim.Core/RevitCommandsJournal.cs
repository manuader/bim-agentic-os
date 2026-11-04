using System.Globalization;
using System.Text.Json;
namespace Bim.Core;

// P2-05 query.journal: the P2-03 external edit journal (MirrorJournal.cs, filled by the add-in's DocumentChanged
// recorder) exposed as a read-only catalogue query, so MCP clients and the CLI reach it with the same envelope, guards,
// permission level (read) and audit as every other query instead of the raw pipe Operation journal.read. Same tracker,
// same baselines, same options and the same reply: the readback body is byte-identical to the journal.read reply after
// its "status" and "verification" members (RevitJournalQuery.Body is the one serializer both use). Vendor-free; the
// add-in executor is CommandsExecutorJournal.cs in the native runtime. Nothing is written to the document, the receipt
// store or any persisted format; a read without peek moves the in-memory baseline, exactly as journal.read does.

// The journal.read options as catalogue arguments. AfterSequence null means "from the baseline".
public sealed record JournalQueryArgs(long? AfterSequence, bool Peek, int MaxEntries) : RevitCommandArgs
{
    public MirrorJournalReadOptions Options => new(AfterSequence, Peek, MaxEntries);
}

public static class RevitJournalQuery
{
    public const string Command = "query.journal";
    public const string ReadbackInvalidCode = "journal_readback_invalid";

    const string RawPrefix = "{\"status\":\"succeeded\",\"verification\":\"unverified\",";

    // The journal.read reply for a query.journal readback (status and verification put back in front), for consumers
    // of the raw reply such as MirrorDrift.ParseJournal, which refuses the bare readback (drift_journal_invalid). A
    // string that is not a readback object (not JSON, not an object, a status member, no report object) is refused
    // journal_readback_invalid.
    public static string AsJournalReadReply(string readback)
    {
        if (string.IsNullOrEmpty(readback) || readback[0] != '{') throw new ArgumentException(ReadbackInvalidCode + " object required");
        try
        {
            using var document = JsonDocument.Parse(readback);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException(ReadbackInvalidCode + " object required");
            if (root.TryGetProperty("status", out _)) throw new ArgumentException(ReadbackInvalidCode + " already a reply (status member)");
            if (!root.TryGetProperty("report", out var report) || report.ValueKind != JsonValueKind.Object) throw new ArgumentException(ReadbackInvalidCode + " report object required");
        }
        catch (JsonException) { throw new ArgumentException(ReadbackInvalidCode + " not JSON"); }
        return RawPrefix + readback[1..];
    }

    // {"attached","dropped","skipped_add_in","failures","unidentified","report":{MirrorJournalReport}}
    public static string Body(bool attached, MirrorJournal journal, MirrorJournalReport report)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(report);
        return "{\"attached\":" + (attached ? "true" : "false")
            + ",\"dropped\":" + journal.Dropped.ToString(CultureInfo.InvariantCulture)
            + ",\"skipped_add_in\":" + journal.SkippedAddIn.ToString(CultureInfo.InvariantCulture)
            + ",\"failures\":" + journal.Failures.ToString(CultureInfo.InvariantCulture)
            + ",\"unidentified\":" + journal.Unidentified.ToString(CultureInfo.InvariantCulture)
            + ",\"report\":" + report.ToJson() + "}";
    }

    // The raw journal.read reply (JournalRecorder.Read): status and verification, then Body.
    public static string RawRead(MirrorJournalTracker tracker, bool attached, MirrorSnapshot snapshot, string documentPath, MirrorJournalReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        var report = tracker.Report(snapshot, documentPath, options);
        return RawPrefix + Body(attached, tracker.Journal, report)[1..];
    }

    // The query.journal readback. An after_sequence past the journal's last sequence (only knowable against the live
    // journal) is args_invalid at args.after_sequence; nothing moves when a read is refused.
    public static string Read(MirrorJournalTracker tracker, bool attached, MirrorSnapshot snapshot, string documentPath, JournalQueryArgs args)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(args);
        MirrorJournalReport report;
        try { report = tracker.Report(snapshot, documentPath, args.Options); }
        catch (ArgumentException error) when (error.Message.StartsWith("journal_after_sequence_invalid", StringComparison.Ordinal))
        {
            throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.after_sequence", "after_sequence " + args.AfterSequence?.ToString(CultureInfo.InvariantCulture) + " is past the journal's last sequence " + tracker.Journal.LastSequence.ToString(CultureInfo.InvariantCulture));
        }
        catch (ArgumentException error) when (error.Message.StartsWith(MirrorJournalReadOptions.InvalidCode, StringComparison.Ordinal))
        {
            throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args", error.Message);
        }
        return Body(attached, tracker.Journal, report);
    }
}

public static partial class RevitCommandCatalogue
{
    // Built on first use; the mirror part (Mirror.cs) appends it after query.snapshot.
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> JournalSpecs = new(BuildJournal);

    public static IReadOnlyList<RevitCommandSpec> Journal => JournalSpecs.Value;

    static IReadOnlyList<RevitCommandSpec> BuildJournal()
    {
        var max = MirrorJournalReadOptions.DefaultMaxEntries.ToString(CultureInfo.InvariantCulture);
        var schema = Fill("""{"type":"object","description":"All optional; pass {} for every external edit since the last read.","properties":{"peek":{"type":"boolean","description":"true answers without moving the baseline, so a retried or paged read sees the same diff; default false"},"after_sequence":{"type":"integer","description":"Return the entries recorded after this journal sequence, 0 or more and at most the journal's last sequence (continue a capped reply with its next_after_sequence); default: the baseline of the last read"},"max_entries":{"type":"integer","description":"At most this many entries in one reply, 1 to @0@; default @0@"}},"additionalProperties":false}""", max);
        return new[]
        {
            new RevitCommandSpec(RevitJournalQuery.Command, false, "Read the external edit journal of the open document (the same reply as the pipe operation journal.read): the edits made outside this add-in (by the user, other add-ins, undo or redo) since the last read, as added and modified UniqueIds and deleted element ids per transaction, plus the BIM-IR snapshot diff against the last read; journal_complete false means entries were lost. A read without peek consumes the baseline shared with journal.read and every other client; pass peek: true unless you own that baseline. Arguments: peek (keep the baseline), after_sequence (page or re-read from a sequence) and max_entries (1 to " + max + " per reply; has_more and next_after_sequence continue). Nothing is written to the document. Read-only.", schema, BindJournal),
        };
    }

    // Structure (types, unknown members) is already checked by the schema; ranges are typed rules here.
    static RevitCommandArgs BindJournal(JsonElement args)
    {
        long? after = null;
        if (args.TryGetProperty("after_sequence", out var afterElement))
        {
            if (!afterElement.TryGetInt64(out var value) || value < 0) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.after_sequence", "after_sequence must be an integer, 0 or more");
            after = value;
        }
        var maxEntries = MirrorJournalReadOptions.DefaultMaxEntries;
        if (args.TryGetProperty("max_entries", out var maxElement))
        {
            if (!maxElement.TryGetInt32(out var value) || value < 1 || value > MirrorJournalReadOptions.DefaultMaxEntries)
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.max_entries", "max_entries must be an integer from 1 to " + MirrorJournalReadOptions.DefaultMaxEntries.ToString(CultureInfo.InvariantCulture));
            maxEntries = value;
        }
        var peek = args.TryGetProperty("peek", out var peekElement) && peekElement.GetBoolean();
        return new JournalQueryArgs(after, peek, maxEntries);
    }
}
