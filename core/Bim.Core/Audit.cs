using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Bim.Core;

// P10-03 append-only audit log: a new JSON Lines file format, one entry per line, LF-terminated, UTF-8 without BOM.
// Each line is the canonical JSON object (members in this fixed order, written by Utf8JsonWriter with default
// escaping and no whitespace):
//   seq, utc, actor, operation_id, command, document, decision, reason, prev_hash, hash
// hash = lowercase hex SHA-256 of the UTF-8 bytes of the same object without the hash member; prev_hash is the
// previous entry's hash, or 64 zeros for seq 1. Seq starts at 1 and increases by one.
// The chain is unkeyed: it detects edits, removals, reordering and torn writes, but whoever can rewrite the whole
// file can recompute it, and removing whole trailing entries leaves a valid shorter chain. Keeping the head anchor
// (seq + hash) outside the file and passing it to Verify/Open closes both gaps for everything up to that anchor.
public static class AuditCodes
{
    public const string Ok = "ok";
    public const string EntryMalformed = "audit_entry_malformed";
    public const string EntryNotCanonical = "audit_entry_not_canonical";
    public const string SequenceBroken = "audit_sequence_broken";
    public const string ChainBroken = "audit_chain_broken";
    public const string HashMismatch = "audit_hash_mismatch";
    public const string TornTail = "audit_torn_tail";
    public const string Truncated = "audit_truncated";
    public const string AnchorMismatch = "audit_anchor_mismatch";
    public const string FileMissing = "audit_file_missing";
    public const string FileUnreadable = "audit_file_unreadable";
    public const string ChangedExternally = "audit_changed_externally";
    public const string InputInvalid = "audit_input_invalid";
}

public sealed record AuditEntry(long Seq, string Utc, string Actor, string OperationId, string Command, string Document, string Decision, string Reason, string PrevHash, string Hash);

public sealed record AuditAnchor(long Seq, string Hash);

// Entries holds the entries verified before the first problem (all of them when Ok). Line is the 1-based line of
// the problem, or null when the problem is not at one line (truncated, a malformed anchor, a missing file).
public sealed record AuditVerification(string Code, long? Line, string? Detail, IReadOnlyList<AuditEntry> Entries)
{
    public bool Ok => Code == AuditCodes.Ok;
    public AuditAnchor Head => Entries.Count == 0 ? new AuditAnchor(0, AuditLogFormat.GenesisHash) : new AuditAnchor(Entries[^1].Seq, Entries[^1].Hash);
}

public sealed class AuditLogException : IOException
{
    public AuditLogException(string code, long? line = null, string? detail = null)
        : base(code + (line is null ? "" : " at line " + line.Value.ToString(CultureInfo.InvariantCulture)) + (detail is null ? "" : ": " + detail))
    {
        Code = code;
        Line = line;
        Detail = detail;
    }

    public string Code { get; }
    public long? Line { get; }
    public string? Detail { get; }
}

public static class AuditLogFormat
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";
    public const string Allow = "allow";
    public const string Deny = "deny";
    public const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    public const int MaxFieldChars = 1024;

    static readonly UTF8Encoding Utf8 = new(false, true);
    static readonly string[] Members = { "seq", "utc", "actor", "operation_id", "command", "document", "decision", "reason", "prev_hash", "hash" };

    public static string ComputeHash(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Hex(SHA256.HashData(Serialize(entry, includeHash: false)));
    }

    // The line text without its LF terminator.
    public static string Line(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Utf8.GetString(Serialize(entry, includeHash: true));
    }

    static byte[] Serialize(AuditEntry entry, bool includeHash)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seq", entry.Seq);
            writer.WriteString("utc", entry.Utc);
            writer.WriteString("actor", entry.Actor);
            writer.WriteString("operation_id", entry.OperationId);
            writer.WriteString("command", entry.Command);
            writer.WriteString("document", entry.Document);
            writer.WriteString("decision", entry.Decision);
            writer.WriteString("reason", entry.Reason);
            writer.WriteString("prev_hash", entry.PrevHash);
            if (includeHash) writer.WriteString("hash", entry.Hash);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    internal static bool IsHash(string? value)
    {
        if (value is null || value.Length != 64) return false;
        foreach (var c in value) if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) return false;
        return true;
    }

    internal static bool IsField(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxFieldChars && PermissionPolicy.IsPlainText(value);

    static bool IsUtc(string value) =>
        DateTime.TryParseExact(value, UtcFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);

    public static AuditVerification Verify(string path, AuditAnchor? anchor = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] bytes;
        try
        {
            if (!File.Exists(path)) return new AuditVerification(AuditCodes.FileMissing, null, null, Array.Empty<AuditEntry>());
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var copy = new MemoryStream();
            file.CopyTo(copy);
            bytes = copy.ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new AuditVerification(AuditCodes.FileUnreadable, null, error.GetType().Name, Array.Empty<AuditEntry>());
        }
        return VerifyBytes(bytes, anchor);
    }

    // Lines are split on the LF byte, which never occurs inside a multi-byte UTF-8 sequence. Complete lines are
    // checked in order (structure, canonical form, seq, chain, hash); then a final line without LF is a torn tail;
    // then the anchor. The first problem is reported.
    public static AuditVerification VerifyBytes(ReadOnlySpan<byte> bytes, AuditAnchor? anchor = null)
    {
        if (anchor is null || anchor is not null) return new AuditVerification(AuditCodes.Ok, null, null, Array.Empty<AuditEntry>());
        var entries = new List<AuditEntry>();
        var previous = GenesisHash;
        long line = 0;
        var start = 0;
        while (start < bytes.Length)
        {
            line++;
            var length = bytes[start..].IndexOf((byte)'\n');
            if (length < 0) return new AuditVerification(AuditCodes.TornTail, line, "final line has no LF terminator", entries.AsReadOnly());
            var raw = bytes.Slice(start, length);
            start += length + 1;
            string text;
            try { text = Utf8.GetString(raw); }
            catch (DecoderFallbackException) { return Fail(AuditCodes.EntryMalformed, line, "invalid UTF-8", entries); }
            if (Parse(text, out var problem) is not { } entry) return Fail(AuditCodes.EntryMalformed, line, problem, entries);
            if (Line(entry) != text) return Fail(AuditCodes.EntryNotCanonical, line, "line differs from the canonical serialization", entries);
            if (entry.Seq != line) return Fail(AuditCodes.SequenceBroken, line, "seq " + entry.Seq.ToString(CultureInfo.InvariantCulture) + " where " + line.ToString(CultureInfo.InvariantCulture) + " expected", entries);
            if (entry.PrevHash != previous) return Fail(AuditCodes.ChainBroken, line, "prev_hash does not match the previous entry", entries);
            if (ComputeHash(entry) != entry.Hash) return Fail(AuditCodes.HashMismatch, line, "hash does not match the entry", entries);
            entries.Add(entry);
            previous = entry.Hash;
        }
        if (anchor is not null)
        {
            if (anchor.Seq < 0 || !IsHash(anchor.Hash) || (anchor.Seq == 0 && anchor.Hash != GenesisHash))
                return Fail(AuditCodes.AnchorMismatch, null, "anchor is not a valid seq and hash", entries);
            if (anchor.Seq > entries.Count)
                return Fail(AuditCodes.Truncated, null, "log has " + entries.Count.ToString(CultureInfo.InvariantCulture) + " entries; anchor expects at least " + anchor.Seq.ToString(CultureInfo.InvariantCulture), entries);
            if (anchor.Seq > 0 && entries[(int)(anchor.Seq - 1)].Hash != anchor.Hash)
                return Fail(AuditCodes.AnchorMismatch, anchor.Seq, "entry hash differs from the anchor", entries);
        }
        return new AuditVerification(AuditCodes.Ok, null, null, entries.AsReadOnly());
    }

    static AuditVerification Fail(string code, long? line, string? detail, List<AuditEntry> entries) => new(code, line, detail, entries.AsReadOnly());

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
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (index >= Members.Length || property.Name != Members[index]) { problem = "members must be exactly " + string.Join(", ", Members) + " in order"; return null; }
                values[property.Name] = property.Value;
                index++;
            }
            if (index != Members.Length) { problem = "members must be exactly " + string.Join(", ", Members) + " in order"; return null; }
            if (values["seq"].ValueKind != JsonValueKind.Number || !values["seq"].TryGetInt64(out var seq) || seq < 1) { problem = "seq must be a positive integer"; return null; }
            var strings = new string[Members.Length];
            for (var i = 1; i < Members.Length; i++)
            {
                var value = values[Members[i]];
                if (value.ValueKind != JsonValueKind.String) { problem = Members[i] + " must be a string"; return null; }
                string? s;
                try { s = value.GetString(); }
                catch (InvalidOperationException) { problem = Members[i] + " is not valid text"; return null; }
                if (!IsField(s)) { problem = Members[i] + " must be 1-1024 characters without control characters"; return null; }
                strings[i] = s!;
            }
            if (!IsUtc(strings[1])) { problem = "utc must be " + UtcFormat; return null; }
            if (strings[6] is not (Allow or Deny)) { problem = "decision must be allow or deny"; return null; }
            if (!IsHash(strings[8]) || !IsHash(strings[9])) { problem = "prev_hash and hash must be 64 lowercase hex characters"; return null; }
            return new AuditEntry(seq, strings[1], strings[2], strings[3], strings[4], strings[5], strings[6], strings[7], strings[8], strings[9]);
        }
    }
}

// One writer per file. Open verifies the whole existing file (and the optional anchor kept elsewhere) and refuses
// any damage, including a torn final line: it reports it and never truncates or repairs. Append writes one
// LF-terminated line in FileMode.Append and flushes it to disk (Flush(true)) before returning; it first checks the
// file length is what this instance last wrote, so another writer or an external edit makes it refuse. A failed
// write faults the instance; reopening then reports the damage.
public sealed class AuditLog
{
    readonly object gate = new();
    readonly Func<DateTimeOffset> clock;
    long length;
    bool faulted;

    AuditLog(string filePath, Func<DateTimeOffset> clock, AuditAnchor head, long length)
    {
        FilePath = filePath;
        this.clock = clock;
        Head = head;
        this.length = length;
    }

    public string FilePath { get; }
    public AuditAnchor Head { get; private set; }

    public static AuditLog Open(string path, Func<DateTimeOffset> clock, AuditAnchor? anchor = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) throw new AuditLogException(AuditCodes.InputInvalid, null, "absolute log path required");
        var full = Path.GetFullPath(path);
        var existing = AuditLogFormat.Verify(full, anchor);
        if (existing.Code == AuditCodes.FileMissing)
        {
            var empty = AuditLogFormat.VerifyBytes(ReadOnlySpan<byte>.Empty, anchor);
            if (!empty.Ok) throw new AuditLogException(empty.Code, empty.Line, empty.Detail);
            var directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using (var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) file.Flush(true);
            return new AuditLog(full, clock, empty.Head, 0);
        }
        if (!existing.Ok) throw new AuditLogException(existing.Code, existing.Line, existing.Detail);
        return new AuditLog(full, clock, existing.Head, new FileInfo(full).Length);
    }

    public AuditEntry Append(string actor, string operationId, string command, string documentPath, PermissionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return Append(actor, operationId, command, documentPath, decision.Allowed, decision.Reason);
    }

    public AuditEntry Append(string actor, string operationId, string command, string documentPath, bool allowed, string reason)
    {
        if (!PermissionPolicy.IsActor(actor)) throw new AuditLogException(AuditCodes.InputInvalid, null, "actor");
        if (!AuditLogFormat.IsField(operationId)) throw new AuditLogException(AuditCodes.InputInvalid, null, "operation_id");
        if (!AuditLogFormat.IsField(command)) throw new AuditLogException(AuditCodes.InputInvalid, null, "command");
        if (!AuditLogFormat.IsField(documentPath)) throw new AuditLogException(AuditCodes.InputInvalid, null, "document");
        if (!AuditLogFormat.IsField(reason)) throw new AuditLogException(AuditCodes.InputInvalid, null, "reason");
        lock (gate)
        {
            if (faulted) throw new AuditLogException(AuditCodes.ChangedExternally, null, "an earlier append failed; reopen the log");
            var utc = clock().UtcDateTime.ToString(AuditLogFormat.UtcFormat, CultureInfo.InvariantCulture);
            var unsigned = new AuditEntry(Head.Seq + 1, utc, actor, operationId, command, documentPath, allowed ? AuditLogFormat.Allow : AuditLogFormat.Deny, reason, Head.Hash, "");
            var entry = unsigned with { Hash = AuditLogFormat.ComputeHash(unsigned) };
            var bytes = Encoding.UTF8.GetBytes(AuditLogFormat.Line(entry) + "\n");
            using var file = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            if (file.Length != length) throw new AuditLogException(AuditCodes.ChangedExternally, null, "log length differs from the last append");
            try
            {
                file.Write(bytes);
                file.Flush(true);
            }
            catch
            {
                faulted = true;
                throw;
            }
            length += bytes.Length;
            Head = new AuditAnchor(entry.Seq, entry.Hash);
            return entry;
        }
    }
}
