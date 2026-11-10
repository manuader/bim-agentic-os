using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Core;

// P10-09 audit log verification and decision summary over the unchanged P10-03 format (Audit.cs). Read only: the
// verifier never writes, repairs or truncates the log.
//
// Unlike AuditLogFormat.Verify, which stops at the first problem, the verifier classifies every problem it can see
// and names the kind of tampering. Codes, most severe first (the report's Code is the most severe finding, ties
// broken by line order; findings themselves are listed by line, then code, with line-less anchor findings last):
//   audit_file_missing / audit_file_unreadable  no log, or the path cannot be read as a file (e.g. a directory)
//   audit_entry_malformed  the line is not a structurally valid P10-03 entry (not JSON, invalid UTF-8, bad text)
//   audit_entry_edited     the stored hash does not match the entry content
//   audit_chain_broken     prev_hash differs from the hash of entry seq-1 (that entry was rewritten with a new hash)
//   audit_anchor_mismatch  the anchor is invalid, or the entry at the anchor's seq has a different hash
//   audit_truncated        a torn final line (no LF), or fewer entries than the head anchor expects
//   audit_entries_missing  seq jumps over values that occur nowhere in the log (entries removed)
//   audit_reordered        an entry's seq is lower than one before it (or the whole log is a permutation)
//   audit_sequence_broken  a seq occurs more than once (duplicated entry)
//   audit_entry_not_canonical  the content hash is valid but the line is not the canonical bytes (CRLF, BOM, spaces)
//   audit_actor_unknown    the actor is not in the supplied known actor set (no set: no actor check)
// ChainCode is AuditLogFormat.VerifyBytes's own first-problem code, and the verifier is never more lenient than it:
// should P10-03 reject a log the classification did not flag, its code is mapped into this vocabulary and reported.
//
// Limits (inherent to the unkeyed P10-03 chain): without the head anchor kept outside the file, removing whole
// trailing entries and rewriting the whole file with recomputed hashes are NOT detectable. Reordering, removing
// middle entries and editing an entry are detected from the file alone. A whole-file rewrite against the anchor
// shows only as audit_anchor_mismatch; which entry changed cannot be told.
//
// The summary (AuditSummary.ToJson, and "summary" in the report) covers only VerifiedEntries: the prefix P10-03
// verified, and nothing at all on an anchor mismatch (no entry is then trustworthy), so a tampered log is never
// counted. Days are UTC calendar days of the entry's utc (yyyy-MM-dd).
//
// Bounded report: at most MaxFindings findings that have a line are kept (the first in report order); findings without
// a line (anchor or file findings, at most two) are always kept. The number of line findings left out is
// FindingsOmitted, serialized as "findings_omitted" after "findings" only when it is positive, so a report within the
// bound is unchanged. Code is still the most severe finding over all of them, kept or omitted. Work is linear in the
// input apart from sorting the parsed entries: seq gaps are counted arithmetically, never walked.
public static class AuditVerifyCodes
{
    public const string Ok = AuditCodes.Ok;
    public const string Truncated = AuditCodes.Truncated;
    public const string Reordered = "audit_reordered";
    public const string EntriesMissing = "audit_entries_missing";
    public const string SequenceBroken = AuditCodes.SequenceBroken;
    public const string EntryEdited = "audit_entry_edited";
    public const string EntryNotCanonical = AuditCodes.EntryNotCanonical;
    public const string ChainBroken = AuditCodes.ChainBroken;
    public const string AnchorMismatch = AuditCodes.AnchorMismatch;
    public const string ActorUnknown = "audit_actor_unknown";
    public const string EntryMalformed = AuditCodes.EntryMalformed;
    public const string FileMissing = AuditCodes.FileMissing;
    public const string FileUnreadable = AuditCodes.FileUnreadable;

    // Most severe first; the report's Code is the finding earliest in this list.
    public static readonly IReadOnlyList<string> Severity = new[] { FileMissing, FileUnreadable, EntryMalformed, EntryEdited, ChainBroken, AnchorMismatch, Truncated, EntriesMissing, Reordered, SequenceBroken, EntryNotCanonical, ActorUnknown };

    // A P10-03 AuditCodes value in this vocabulary.
    public static string FromChainCode(string code) => code switch
    {
        AuditCodes.HashMismatch => EntryEdited,
        AuditCodes.TornTail => Truncated,
        AuditCodes.FileMissing or AuditCodes.FileUnreadable or AuditCodes.EntryMalformed or AuditCodes.EntryNotCanonical or AuditCodes.SequenceBroken or AuditCodes.ChainBroken or AuditCodes.Truncated or AuditCodes.AnchorMismatch or AuditCodes.Ok => code,
        _ => EntryMalformed,
    };
}

// KnownActors: the actors allowed to appear (ordinal); null skips the actor check. Anchor: the head kept elsewhere.
public sealed record AuditVerifyOptions(IReadOnlyCollection<string>? KnownActors = null, AuditAnchor? Anchor = null);

public sealed record AuditFinding(string Code, long? Line, string Detail);

public sealed record AuditVerifyReport(string Code, string ChainCode, long Lines, IReadOnlyList<AuditFinding> Findings, IReadOnlyList<AuditEntry> VerifiedEntries, long FindingsOmitted = 0)
{
    public bool Ok => Code == AuditVerifyCodes.Ok;

    // {"ok","code","chain_code","lines","findings":[{"code","line","detail"}][,"findings_omitted"],"summary":{...}} with no whitespace.
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", Ok);
            writer.WriteString("code", Code);
            writer.WriteString("chain_code", ChainCode);
            writer.WriteNumber("lines", Lines);
            writer.WriteStartArray("findings");
            foreach (var finding in Findings)
            {
                writer.WriteStartObject();
                writer.WriteString("code", finding.Code);
                if (finding.Line is { } line) writer.WriteNumber("line", line); else writer.WriteNull("line");
                writer.WriteString("detail", finding.Detail);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (FindingsOmitted > 0) writer.WriteNumber("findings_omitted", FindingsOmitted);
            writer.WritePropertyName("summary");
            AuditSummary.Write(writer, VerifiedEntries);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

public static class AuditVerifier
{
    public const int MaxFindings = 10000;

    static readonly UTF8Encoding Utf8 = new(false, true);
    static readonly string[] Members = { "seq", "utc", "actor", "operation_id", "command", "document", "decision", "reason", "prev_hash", "hash" };

    public static AuditVerifyReport Verify(string path, AuditVerifyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] bytes;
        try
        {
            if (Directory.Exists(path)) return Single(AuditVerifyCodes.FileUnreadable, "path is a directory");
            if (!File.Exists(path)) return Single(AuditVerifyCodes.FileMissing, "no audit log at the path");
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var copy = new MemoryStream();
            file.CopyTo(copy);
            bytes = copy.ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Single(AuditVerifyCodes.FileUnreadable, error.GetType().Name);
        }
        return VerifyBytes(bytes, options);
    }

    static AuditVerifyReport Single(string code, string detail) =>
        new(code, code, 0, new[] { new AuditFinding(code, null, detail) }, Array.Empty<AuditEntry>());

    public static AuditVerifyReport VerifyBytes(ReadOnlySpan<byte> bytes, AuditVerifyOptions? options = null)
    {
        var anchor = options?.Anchor;
        HashSet<string>? known = options?.KnownActors is { } actors ? new HashSet<string>(actors, StringComparer.Ordinal) : null;
        var findings = new FindingSink();
        // UnparsedBefore: how many unparsed lines precede the entry's line (a prefix count, so the lines between two
        // entries are counted in O(1)).
        var parsed = new List<(long Line, AuditEntry Entry, long UnparsedBefore)>();
        long unparsedCount = 0, lastUnparsed = 0;
        long line = 0;
        var start = 0;
        var torn = false;
        while (start < bytes.Length)
        {
            line++;
            var length = bytes[start..].IndexOf((byte)'\n');
            if (length < 0)
            {
                torn = true;
                findings.Add(new AuditFinding(AuditVerifyCodes.Truncated, line, "final line has no LF terminator (torn write or cut file)"));
                break;
            }
            var raw = bytes.Slice(start, length);
            start += length + 1;
            string text;
            try { text = Utf8.GetString(raw); }
            catch (DecoderFallbackException) { findings.Add(new AuditFinding(AuditVerifyCodes.EntryMalformed, line, "invalid UTF-8")); unparsedCount++; lastUnparsed = line; continue; }
            // A byte order mark is tolerated for classification only; the line is then not canonical.
            var content = text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
            if (Parse(content, out var problem) is not { } entry) { findings.Add(new AuditFinding(AuditVerifyCodes.EntryMalformed, line, problem)); unparsedCount++; lastUnparsed = line; continue; }
            if (AuditLogFormat.ComputeHash(entry) != entry.Hash) findings.Add(new AuditFinding(AuditVerifyCodes.EntryEdited, line, "hash does not match the entry content"));
            else if (AuditLogFormat.Line(entry) != text) findings.Add(new AuditFinding(AuditVerifyCodes.EntryNotCanonical, line, "content hash is valid but the line differs from the canonical serialization"));
            if (known is not null && !known.Contains(entry.Actor)) findings.Add(new AuditFinding(AuditVerifyCodes.ActorUnknown, line, "actor " + entry.Actor + " is not in the known actor set"));
            parsed.Add((line, entry, unparsedCount));
        }

        Sequence(parsed, findings);

        // Chain links by seq, independent of line order, so reordering alone never reads as an edit. A link is only
        // checked when its predecessor is present exactly once.
        var bySeq = parsed.GroupBy(p => p.Entry.Seq).ToDictionary(g => g.Key, g => g.Count() == 1 ? g.First().Entry : (AuditEntry?)null);
        foreach (var (entryLine, entry, _) in parsed)
        {
            if (bySeq.TryGetValue(entry.Seq, out var self) && self is null) continue;
            string? expected = entry.Seq == 1 ? AuditLogFormat.GenesisHash : bySeq.TryGetValue(entry.Seq - 1, out var before) && before is not null ? before.Hash : null;
            if (expected is not null && entry.PrevHash != expected)
                findings.Add(new AuditFinding(AuditVerifyCodes.ChainBroken, entryLine, "prev_hash differs from the hash of entry seq " + Text(entry.Seq - 1)));
        }

        var anchorMismatch = false;
        if (anchor is not null)
        {
            var maxSeq = parsed.Count == 0 ? 0 : parsed.Max(p => p.Entry.Seq);
            var maxLine = parsed.Count == 0 ? 0 : parsed.First(p => p.Entry.Seq == maxSeq).Line;
            if (anchor.Seq < 0 || !AuditLogFormat.IsHash(anchor.Hash) || (anchor.Seq == 0 && anchor.Hash != AuditLogFormat.GenesisHash))
            {
                anchorMismatch = true;
                findings.Add(new AuditFinding(AuditVerifyCodes.AnchorMismatch, null, "anchor is not a valid seq and hash"));
            }
            else if (anchor.Seq > maxSeq)
            {
                // A torn or malformed line after the highest entry may be the anchored entry itself: that damage is
                // already reported, so it is not also truncation while the log still has enough lines.
                var damagedTail = torn || (lastUnparsed > maxLine && line >= anchor.Seq);
                if (!damagedTail) findings.Add(new AuditFinding(AuditVerifyCodes.Truncated, null, "log ends at seq " + Text(maxSeq) + "; anchor expects at least " + Text(anchor.Seq)));
            }
            else if (anchor.Seq > 0 && bySeq.TryGetValue(anchor.Seq, out var at) && at is not null && at.Hash != anchor.Hash)
            {
                anchorMismatch = true;
                findings.Add(new AuditFinding(AuditVerifyCodes.AnchorMismatch, parsed.First(p => p.Entry.Seq == anchor.Seq).Line, "entry hash differs from the anchor"));
            }
        }

        var chain = AuditLogFormat.VerifyBytes(bytes, anchor);
        if (!chain.Ok && findings.Total == 0) findings.Add(new AuditFinding(AuditVerifyCodes.FromChainCode(chain.Code), chain.Line, chain.Detail ?? chain.Code));
        if (chain.Code == AuditCodes.AnchorMismatch) anchorMismatch = true;

        var ordered = findings.Kept();
        var code = findings.Total == 0 ? AuditVerifyCodes.Ok : findings.BestCode!;
        IReadOnlyList<AuditEntry> verified = anchorMismatch ? Array.Empty<AuditEntry>() : chain.Entries;
        return new AuditVerifyReport(code, chain.Code, line, ordered, verified, findings.Total - ordered.Length);
    }

    // Report order: line findings by line, code, detail; line-less findings last. Keeps the first MaxFindings line
    // findings in that order (compacting whenever twice as many are buffered, so memory stays O(MaxFindings)) and every
    // line-less finding, while counting all of them and tracking the most severe code.
    sealed class FindingSink
    {
        static readonly Comparison<AuditFinding> Order = (a, b) =>
        {
            var c = (a.Line is null ? 1 : 0).CompareTo(b.Line is null ? 1 : 0);
            if (c == 0) c = (a.Line ?? 0).CompareTo(b.Line ?? 0);
            if (c == 0) c = string.CompareOrdinal(a.Code, b.Code);
            if (c == 0) c = string.CompareOrdinal(a.Detail, b.Detail);
            return c;
        };
        readonly List<AuditFinding> lined = new();
        readonly List<AuditFinding> lineless = new();
        public long Total { get; private set; }
        int bestRank = int.MaxValue;
        public string? BestCode { get; private set; }

        public void Add(AuditFinding finding)
        {
            Total++;
            var rank = Rank(finding.Code);
            if (rank < bestRank) { bestRank = rank; BestCode = finding.Code; }
            if (finding.Line is null) { lineless.Add(finding); return; }
            lined.Add(finding);
            if (lined.Count >= 2 * MaxFindings) Compact();
        }

        void Compact()
        {
            lined.Sort(Order);
            if (lined.Count > MaxFindings) lined.RemoveRange(MaxFindings, lined.Count - MaxFindings);
        }

        public AuditFinding[] Kept()
        {
            Compact();
            lineless.Sort(Order);
            return lined.Concat(lineless).ToArray();
        }
    }

    static int Rank(string code)
    {
        for (var i = 0; i < AuditVerifyCodes.Severity.Count; i++) if (AuditVerifyCodes.Severity[i] == code) return i;
        return AuditVerifyCodes.Severity.Count;
    }

    // Permutation of the line numbers: every misplaced line is reordered. Otherwise, walking in line order with the
    // highest seq seen so far: a seq seen before is a duplicate (sequence broken), a lower seq is reordered, and a
    // jump over seqs that occur nowhere in the log is missing entries (less any malformed lines in between, which may
    // hold them).
    static void Sequence(List<(long Line, AuditEntry Entry, long UnparsedBefore)> parsed, FindingSink findings)
    {
        if (parsed.All(p => p.Entry.Seq == p.Line)) return;
        var seqs = parsed.Select(p => p.Entry.Seq).OrderBy(s => s).ToArray();
        var lines = parsed.Select(p => p.Line).OrderBy(l => l).ToArray();
        if (seqs.SequenceEqual(lines))
        {
            foreach (var p in parsed.Where(p => p.Entry.Seq != p.Line))
                findings.Add(new AuditFinding(AuditVerifyCodes.Reordered, p.Line, "seq " + Text(p.Entry.Seq) + " at line " + Text(p.Line)));
            return;
        }
        var present = seqs.Distinct().ToArray();
        var seen = new HashSet<long>();
        long highest = 0, previousUnparsed = 0;
        foreach (var (entryLine, entry, unparsedBefore) in parsed)
        {
            var seq = entry.Seq;
            if (!seen.Add(seq))
                findings.Add(new AuditFinding(AuditVerifyCodes.SequenceBroken, entryLine, "seq " + Text(seq) + " occurs more than once"));
            else if (seq < highest)
                findings.Add(new AuditFinding(AuditVerifyCodes.Reordered, entryLine, "seq " + Text(seq) + " after seq " + Text(highest)));
            else
            {
                // seqs strictly between highest and seq that occur nowhere, less the unparsed lines in between.
                var absent = seq - highest - 1 - (LowerBound(present, seq) - LowerBound(present, highest + 1));
                absent -= unparsedBefore - previousUnparsed;
                if (absent > 0)
                    findings.Add(new AuditFinding(AuditVerifyCodes.EntriesMissing, entryLine, "seq " + Text(seq) + " after seq " + Text(highest) + "; " + Text(absent) + " entries missing"));
                highest = seq;
            }
            previousUnparsed = unparsedBefore;
        }
    }

    // The index of the first element >= value in an ascending array.
    static long LowerBound(long[] sorted, long value)
    {
        int low = 0, high = sorted.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (sorted[middle] < value) low = middle + 1; else high = middle;
        }
        return low;
    }

    static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    // The P10-03 entry structure (Audit.cs) without the canonical-form, seq, chain and hash checks.
    static AuditEntry? Parse(string text, out string problem)
    {
        problem = "";
        // Cheap refusal without an exception for text JSON.Parse would reject anyway: blank lines, or a first
        // non-whitespace ASCII character no JSON value starts with (same "not JSON" problem as the parser's).
        var first = 0;
        while (first < text.Length && text[first] is ' ' or '\t' or '\r' or '\n') first++;
        if (first == text.Length || (text[first] < 0x80 && text[first] is not ('{' or '[' or '"' or '-' or (>= '0' and <= '9') or 't' or 'f' or 'n'))) { problem = "not JSON"; return null; }
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException) { problem = "not JSON"; return null; }
        catch (ArgumentException) { problem = "not JSON"; return null; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { problem = "object required"; return null; }
            var index = 0;
            var values = new JsonElement[Members.Length];
            foreach (var property in root.EnumerateObject())
            {
                if (index >= Members.Length || property.Name != Members[index]) { problem = "members must be exactly " + string.Join(", ", Members) + " in order"; return null; }
                values[index++] = property.Value;
            }
            if (index != Members.Length) { problem = "members must be exactly " + string.Join(", ", Members) + " in order"; return null; }
            if (values[0].ValueKind != JsonValueKind.Number || !values[0].TryGetInt64(out var seq) || seq < 1) { problem = "seq must be a positive integer"; return null; }
            var strings = new string[Members.Length];
            for (var i = 1; i < Members.Length; i++)
            {
                if (values[i].ValueKind != JsonValueKind.String) { problem = Members[i] + " must be a string"; return null; }
                string? s;
                try { s = values[i].GetString(); }
                catch (InvalidOperationException) { problem = Members[i] + " is not valid text"; return null; }
                if (!AuditLogFormat.IsField(s)) { problem = Members[i] + " must be 1-1024 characters without control characters"; return null; }
                strings[i] = s!;
            }
            if (!AuditSummary.TryDay(strings[1], out _)) { problem = "utc must be " + AuditLogFormat.UtcFormat; return null; }
            if (strings[6] is not (AuditLogFormat.Allow or AuditLogFormat.Deny)) { problem = "decision must be allow or deny"; return null; }
            if (!AuditLogFormat.IsHash(strings[8]) || !AuditLogFormat.IsHash(strings[9])) { problem = "prev_hash and hash must be 64 lowercase hex characters"; return null; }
            return new AuditEntry(seq, strings[1], strings[2], strings[3], strings[4], strings[5], strings[6], strings[7], strings[8], strings[9]);
        }
    }
}

// Decisions per actor, per command, per UTC day and per actor/command/day:
// {"entries","allow","deny","by_actor":[{"actor","entries","allow","deny"}],"by_command":[{"command",...}],
//  "by_day":[{"day",...}],"by_actor_command_day":[{"actor","command","day",...}]}, groups sorted by key (ordinal),
// no whitespace, independent of entry order and of the current culture.
public static class AuditSummary
{
    public static string ToJson(IEnumerable<AuditEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) Write(writer, entries);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static bool TryDay(string utc, out string day)
    {
        if (DateTime.TryParseExact(utc, AuditLogFormat.UtcFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
        {
            day = value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }
        day = "";
        return false;
    }

    sealed class Count
    {
        public long Allow;
        public long Deny;
    }

    internal static void Write(Utf8JsonWriter writer, IEnumerable<AuditEntry> entries)
    {
        var rows = entries.Select(entry =>
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (!TryDay(entry.Utc, out var day)) throw new ArgumentException("entry seq " + entry.Seq.ToString(CultureInfo.InvariantCulture) + " has an invalid utc", nameof(entries));
            if (entry.Decision is not (AuditLogFormat.Allow or AuditLogFormat.Deny)) throw new ArgumentException("entry seq " + entry.Seq.ToString(CultureInfo.InvariantCulture) + " has a decision other than allow or deny", nameof(entries));
            return (entry.Actor, entry.Command, Day: day, Allow: entry.Decision == AuditLogFormat.Allow);
        }).ToArray();
        writer.WriteStartObject();
        writer.WriteNumber("entries", rows.Length);
        writer.WriteNumber("allow", rows.LongCount(r => r.Allow));
        writer.WriteNumber("deny", rows.LongCount(r => !r.Allow));
        Group(writer, "by_actor", rows, r => new[] { r.Actor }, new[] { "actor" });
        Group(writer, "by_command", rows, r => new[] { r.Command }, new[] { "command" });
        Group(writer, "by_day", rows, r => new[] { r.Day }, new[] { "day" });
        Group(writer, "by_actor_command_day", rows, r => new[] { r.Actor, r.Command, r.Day }, new[] { "actor", "command", "day" });
        writer.WriteEndObject();
    }

    static void Group(Utf8JsonWriter writer, string name, (string Actor, string Command, string Day, bool Allow)[] rows, Func<(string Actor, string Command, string Day, bool Allow), string[]> key, string[] names)
    {
        var groups = new SortedDictionary<string[], Count>(KeyComparer.Instance);
        foreach (var row in rows)
        {
            var k = key(row);
            if (!groups.TryGetValue(k, out var count)) groups[k] = count = new Count();
            if (row.Allow) count.Allow++; else count.Deny++;
        }
        writer.WriteStartArray(name);
        foreach (var (k, count) in groups)
        {
            writer.WriteStartObject();
            for (var i = 0; i < names.Length; i++) writer.WriteString(names[i], k[i]);
            writer.WriteNumber("entries", count.Allow + count.Deny);
            writer.WriteNumber("allow", count.Allow);
            writer.WriteNumber("deny", count.Deny);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    sealed class KeyComparer : IComparer<string[]>
    {
        public static readonly KeyComparer Instance = new();

        public int Compare(string[]? x, string[]? y)
        {
            for (var i = 0; i < x!.Length; i++)
            {
                var c = string.CompareOrdinal(x[i], y![i]);
                if (c != 0) return c;
            }
            return 0;
        }
    }
}
