using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Core;

// P10-09 audit log verification and decision summary over the unchanged P10-03 format (Audit.cs). Read only: the
// verifier never writes, repairs or truncates the log.
//
// Unlike AuditLogFormat.Verify, which stops at the first problem, the verifier classifies every problem it can see
// and names the kind of tampering:
//   audit_truncated        a torn final line (no LF), or fewer entries than the head anchor expects
//   audit_reordered        the seq values are exactly the line numbers, but in a different order
//   audit_entries_missing  seq increases but jumps over values (entries removed from the middle or the start)
//   audit_sequence_broken  any other seq damage (duplicated or inserted entries)
//   audit_entry_edited     the line's hash does not match its content, or it is not the canonical serialization
//   audit_chain_broken     prev_hash differs from the hash of entry seq-1 (that entry was rewritten with a new hash)
//   audit_anchor_mismatch  the anchor is invalid, or the entry at the anchor's seq has a different hash
//   audit_actor_unknown    the actor is not in the supplied known actor set (no set: no actor check)
//   audit_entry_malformed  the line is not a structurally valid P10-03 entry
//   audit_file_missing / audit_file_unreadable
// Findings are ordered by line, then code (ordinal); findings without a line (anchor checks) come last. Code is the
// first finding's code, or ok. ChainCode is AuditLogFormat.VerifyBytes's own first-problem code, and the verifier is
// never more lenient than it: should P10-03 reject a log the classification did not flag, its code is reported too.
//
// Limits (inherent to the unkeyed P10-03 chain): without the head anchor kept outside the file, removing whole
// trailing entries and rewriting the whole file with recomputed hashes are NOT detectable. Reordering, removing
// middle entries and editing an entry are detected from the file alone. A whole-file rewrite against the anchor
// shows only as audit_anchor_mismatch; which entry changed cannot be told.
//
// The summary (AuditSummary.ToJson, and "summary" in the report) covers only VerifiedEntries: the prefix P10-03
// verified, so a tampered tail is never counted. Days are UTC calendar days of the entry's utc (yyyy-MM-dd).
public static class AuditVerifyCodes
{
    public const string Ok = AuditCodes.Ok;
    public const string Truncated = AuditCodes.Truncated;
    public const string Reordered = "audit_reordered";
    public const string EntriesMissing = "audit_entries_missing";
    public const string SequenceBroken = AuditCodes.SequenceBroken;
    public const string EntryEdited = "audit_entry_edited";
    public const string ChainBroken = AuditCodes.ChainBroken;
    public const string AnchorMismatch = AuditCodes.AnchorMismatch;
    public const string ActorUnknown = "audit_actor_unknown";
    public const string EntryMalformed = AuditCodes.EntryMalformed;
    public const string FileMissing = AuditCodes.FileMissing;
    public const string FileUnreadable = AuditCodes.FileUnreadable;
}

// KnownActors: the actors allowed to appear (ordinal); null skips the actor check. Anchor: the head kept elsewhere.
public sealed record AuditVerifyOptions(IReadOnlyCollection<string>? KnownActors = null, AuditAnchor? Anchor = null);

public sealed record AuditFinding(string Code, long? Line, string Detail);

public sealed record AuditVerifyReport(string Code, string ChainCode, long Lines, IReadOnlyList<AuditFinding> Findings, IReadOnlyList<AuditEntry> VerifiedEntries)
{
    public bool Ok => Code == AuditVerifyCodes.Ok;

    // {"ok","code","chain_code","lines","findings":[{"code","line","detail"}],"summary":{...}} with no whitespace.
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
            writer.WritePropertyName("summary");
            AuditSummary.Write(writer, VerifiedEntries);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

public static class AuditVerifier
{
    static readonly UTF8Encoding Utf8 = new(false, true);
    static readonly string[] Members = { "seq", "utc", "actor", "operation_id", "command", "document", "decision", "reason", "prev_hash", "hash" };

    public static AuditVerifyReport Verify(string path, AuditVerifyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] bytes;
        try
        {
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
        var findings = new List<AuditFinding>();
        var parsed = new List<(long Line, AuditEntry Entry, bool HashValid)>();
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
            catch (DecoderFallbackException) { findings.Add(new AuditFinding(AuditVerifyCodes.EntryMalformed, line, "invalid UTF-8")); continue; }
            if (Parse(text, out var problem) is not { } entry) { findings.Add(new AuditFinding(AuditVerifyCodes.EntryMalformed, line, problem)); continue; }
            var hashValid = AuditLogFormat.ComputeHash(entry) == entry.Hash;
            if (!hashValid) findings.Add(new AuditFinding(AuditVerifyCodes.EntryEdited, line, "hash does not match the entry content"));
            else if (AuditLogFormat.Line(entry) != text) findings.Add(new AuditFinding(AuditVerifyCodes.EntryEdited, line, "line differs from the canonical serialization"));
            if (known is not null && !known.Contains(entry.Actor)) findings.Add(new AuditFinding(AuditVerifyCodes.ActorUnknown, line, "actor " + entry.Actor + " is not in the known actor set"));
            parsed.Add((line, entry, hashValid));
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
                findings.Add(new AuditFinding(AuditVerifyCodes.ChainBroken, entryLine, "prev_hash differs from the hash of entry seq " + (entry.Seq - 1).ToString(CultureInfo.InvariantCulture)));
        }

        if (anchor is not null)
        {
            var maxSeq = parsed.Count == 0 ? 0 : parsed.Max(p => p.Entry.Seq);
            if (anchor.Seq < 0 || !AuditLogFormat.IsHash(anchor.Hash) || (anchor.Seq == 0 && anchor.Hash != AuditLogFormat.GenesisHash))
                findings.Add(new AuditFinding(AuditVerifyCodes.AnchorMismatch, null, "anchor is not a valid seq and hash"));
            else if (anchor.Seq > maxSeq)
            {
                if (!torn) findings.Add(new AuditFinding(AuditVerifyCodes.Truncated, null, "log ends at seq " + maxSeq.ToString(CultureInfo.InvariantCulture) + "; anchor expects at least " + anchor.Seq.ToString(CultureInfo.InvariantCulture)));
            }
            else if (anchor.Seq > 0 && bySeq.TryGetValue(anchor.Seq, out var at) && at is not null && at.Hash != anchor.Hash)
            {
                var atLine = parsed.First(p => p.Entry.Seq == anchor.Seq).Line;
                findings.Add(new AuditFinding(AuditVerifyCodes.AnchorMismatch, atLine, "entry hash differs from the anchor"));
            }
        }

        var chain = AuditLogFormat.VerifyBytes(bytes, anchor);
        if (!chain.Ok && findings.Count == 0) findings.Add(new AuditFinding(chain.Code, chain.Line, chain.Detail ?? chain.Code));

        var ordered = findings
            .OrderBy(f => f.Line is null ? 1 : 0)
            .ThenBy(f => f.Line ?? 0)
            .ThenBy(f => f.Code, StringComparer.Ordinal)
            .ThenBy(f => f.Detail, StringComparer.Ordinal)
            .ToArray();
        var code = ordered.Length == 0 ? AuditVerifyCodes.Ok : ordered[0].Code;
        return new AuditVerifyReport(code, chain.Code, line, ordered, chain.Entries);
    }

    static void Sequence(List<(long Line, AuditEntry Entry, bool HashValid)> parsed, List<AuditFinding> findings)
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
        var increasing = true;
        for (var i = 1; i < parsed.Count; i++) if (parsed[i].Entry.Seq <= parsed[i - 1].Entry.Seq) increasing = false;
        if (increasing)
        {
            long previousSeq = 0, previousLine = 0;
            var broken = false;
            foreach (var p in parsed)
            {
                var expected = previousSeq + (p.Line - previousLine);
                if (p.Entry.Seq > expected)
                    findings.Add(new AuditFinding(AuditVerifyCodes.EntriesMissing, p.Line, "seq " + Text(p.Entry.Seq) + " where " + Text(expected) + " expected; " + Text(p.Entry.Seq - expected) + " entries missing"));
                else if (p.Entry.Seq < expected && !broken)
                {
                    broken = true;
                    findings.Add(new AuditFinding(AuditVerifyCodes.SequenceBroken, p.Line, "seq " + Text(p.Entry.Seq) + " where " + Text(expected) + " expected"));
                }
                previousSeq = p.Entry.Seq;
                previousLine = p.Line;
            }
            return;
        }
        var first = parsed.First(p => p.Entry.Seq != p.Line);
        findings.Add(new AuditFinding(AuditVerifyCodes.SequenceBroken, first.Line, "seq " + Text(first.Entry.Seq) + " where " + Text(first.Line) + " expected"));
    }

    static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    // The P10-03 entry structure (Audit.cs) without the canonical-form, seq, chain and hash checks.
    static AuditEntry? Parse(string text, out string problem)
    {
        problem = "";
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
