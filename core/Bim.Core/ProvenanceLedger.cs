using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Bim.Core;

// P2-06 append-only provenance ledger, a new standalone JSON format (no BIM-IR change):
//   {"provenance_ledger_version":1,"entries":[entry, ...]}
// entry = {"seq":n,"element_id":"<guid D, lower-case>","source":{"kind":k, <kind fields in fixed order>},
//          "prev_hash":"<64 hex>","hash":"<64 hex>"}
// written compactly by Utf8JsonWriter with its default encoder (non-ASCII and HTML-sensitive characters as \uXXXX),
// so the text is byte-stable and culture independent. hash = lowercase hex SHA-256 of the UTF-8 bytes of the entry
// object without its hash member; prev_hash is the previous entry's hash, 64 zeros for seq 1; seq runs 1, 2, ...
// The replay key of an entry is (element_id, kind, key fields): intent_id; operation_id; document_unique_id +
// journal_sequence + recorded_at; file_sha256 + entity_handle. Appending the same record again is a no-op
// (replayed); the same key with other content is provenance_conflict. There is no edit or delete operation.
// Load refuses, with the 1-based entry number: malformed JSON or members, unknown kinds, invalid field values,
// broken sequence, broken chain, wrong hash, repeated or conflicting keys, and any text that is not byte-identical
// to the canonical serialization of what it parsed to. Limits: the chain is unkeyed, so whoever rewrites the whole
// file can recompute every hash, and dropping trailing entries leaves a valid shorter ledger; a caller that needs
// that guarantee keeps HeadHash and Count outside the file.
public sealed record ProvenanceEntry(long Sequence, Guid ElementId, ProvenanceSource Source, string PrevHash, string Hash);

public sealed record ProvenanceAppendResult(string Code, ProvenanceEntry? Entry, string? Detail)
{
    public bool Ok => Code is ProvenanceCodes.Appended or ProvenanceCodes.Replayed;
}

// Sequence is the 1-based entry number of the problem when it is at one entry.
public sealed record ProvenanceLoadResult(string Code, ProvenanceLedger? Ledger, long? Sequence, string? Detail)
{
    public bool Ok => Code == ProvenanceCodes.Ok;
}

public sealed record ProvenanceQueryResult(string Code, IReadOnlyList<ProvenanceEntry> Entries)
{
    public bool Ok => Code == ProvenanceCodes.Ok;
}

public static class ProvenanceLedgerFormat
{
    public const int Version = 1;
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";
    static readonly UTF8Encoding Utf8 = new(false, true);

    public static string ComputeHash(ProvenanceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) WriteEntry(writer, entry, includeHash: false);
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    // Writes entries as given, without validating them (Load is the validator).
    public static string Write(IEnumerable<ProvenanceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("provenance_ledger_version", Version);
            writer.WriteStartArray("entries");
            foreach (var entry in entries) WriteEntry(writer, entry, includeHash: true);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Utf8.GetString(buffer.ToArray());
    }

    static void WriteEntry(Utf8JsonWriter writer, ProvenanceEntry entry, bool includeHash)
    {
        writer.WriteStartObject();
        writer.WriteNumber("seq", entry.Sequence);
        writer.WriteString("element_id", entry.ElementId.ToString("D"));
        writer.WriteStartObject("source");
        writer.WriteString("kind", entry.Source.Kind);
        entry.Source.WriteFields(writer);
        writer.WriteEndObject();
        writer.WriteString("prev_hash", entry.PrevHash);
        if (includeHash) writer.WriteString("hash", entry.Hash);
        writer.WriteEndObject();
    }

    internal static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}

public sealed record ProvenanceLimits(int MaxEntries = ProvenanceLimits.DefaultMaxEntries, int MaxJsonChars = ProvenanceLimits.DefaultMaxJsonChars)
{
    public const int DefaultMaxEntries = 100_000;
    public const int DefaultMaxJsonChars = 64 * 1024 * 1024;
    public static readonly ProvenanceLimits Default = new();
}

public sealed class ProvenanceLedger
{
    public ProvenanceLedger(ProvenanceLimits? limits = null) { Limits = limits ?? ProvenanceLimits.Default; }
    public ProvenanceLimits Limits { get; }
    readonly List<ProvenanceEntry> entries = new();
    readonly Dictionary<string, ProvenanceEntry> byKey = new(StringComparer.Ordinal);
    readonly Dictionary<Guid, List<ProvenanceEntry>> byElement = new();

    public int Count => entries.Count;
    public string HeadHash => entries.Count == 0 ? ProvenanceLedgerFormat.GenesisHash : entries[^1].Hash;

    public IReadOnlyList<ProvenanceEntry> Entries() => entries.ToArray();

    static string Key(Guid elementId, ProvenanceSource source) => elementId.ToString("D") + "\n" + source.Kind + "\n" + source.KeyPart;

    // Null when valid, else the offending member.
    static string? Invalid(Guid elementId, ProvenanceSource? source) =>
        elementId == Guid.Empty ? "element_id" : source is null ? "source" : source.InvalidField(elementId);

    public ProvenanceAppendResult Append(Guid elementId, ProvenanceSource? source)
    {
        if (Invalid(elementId, source) is { } field) return new(ProvenanceCodes.RecordInvalid, null, field);
        var key = Key(elementId, source!);
        if (byKey.TryGetValue(key, out var existing))
            return existing.Source == source
                ? new(ProvenanceCodes.Replayed, existing, null)
                : new(ProvenanceCodes.Conflict, existing, "seq " + existing.Sequence.ToString(CultureInfo.InvariantCulture));
        var entry = new ProvenanceEntry(entries.Count + 1, elementId, source!, HeadHash, "");
        entry = entry with { Hash = ProvenanceLedgerFormat.ComputeHash(entry) };
        Add(key, entry);
        return new(ProvenanceCodes.Appended, entry, null);
    }

    void Add(string key, ProvenanceEntry entry)
    {
        entries.Add(entry);
        byKey.Add(key, entry);
        if (!byElement.TryGetValue(entry.ElementId, out var list)) byElement.Add(entry.ElementId, list = new List<ProvenanceEntry>());
        list.Add(entry);
    }

    public IReadOnlyList<ProvenanceEntry> ByElement(Guid elementId) =>
        byElement.TryGetValue(elementId, out var list) ? list.ToArray() : Array.Empty<ProvenanceEntry>();

    // Every entry of that kind, or only those whose SourceId (intent_id, operation_id, document_unique_id,
    // file_sha256) equals sourceId (ordinal), in append order.
    public ProvenanceQueryResult BySource(string? kind, string? sourceId = null)
    {
        if (!ProvenanceKinds.IsKnown(kind)) return new(ProvenanceCodes.KindUnknown, Array.Empty<ProvenanceEntry>());
        return new(ProvenanceCodes.Ok, entries.Where(entry => entry.Source.Kind == kind && (sourceId is null || string.Equals(entry.Source.SourceId, sourceId, StringComparison.Ordinal))).ToArray());
    }

    public string Serialize() => ProvenanceLedgerFormat.Write(entries);

    public static ProvenanceLoadResult Load(string? json, ProvenanceLimits? limits = null)
    {
        if (json is null) return Fail(ProvenanceCodes.JsonInvalid, null, "input");
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException error) { return Fail(ProvenanceCodes.JsonInvalid, null, error.Message); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail(ProvenanceCodes.JsonInvalid, null, "root");
            if (Members(root, new[] { "provenance_ledger_version", "entries" }) is { } bad) return Fail(ProvenanceCodes.JsonInvalid, null, bad);
            var version = root.GetProperty("provenance_ledger_version");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != ProvenanceLedgerFormat.Version)
                return Fail(ProvenanceCodes.VersionUnsupported, null, version.GetRawText());
            var list = root.GetProperty("entries");
            if (list.ValueKind != JsonValueKind.Array) return Fail(ProvenanceCodes.JsonInvalid, null, "entries");
            var ledger = new ProvenanceLedger();
            long index = 0;
            foreach (var item in list.EnumerateArray())
            {
                index++;
                var parsed = ParseEntry(item);
                if (parsed.Code is not null) return Fail(parsed.Code, index, parsed.Detail);
                var entry = parsed.Entry!;
                if (Invalid(entry.ElementId, entry.Source) is { } field) return Fail(ProvenanceCodes.RecordInvalid, index, field);
                if (entry.Sequence != index) return Fail(ProvenanceCodes.SequenceBroken, index, "seq " + entry.Sequence.ToString(CultureInfo.InvariantCulture));
                if (entry.PrevHash != ledger.HeadHash) return Fail(ProvenanceCodes.ChainBroken, index, "prev_hash");
                if (entry.Hash != ProvenanceLedgerFormat.ComputeHash(entry)) return Fail(ProvenanceCodes.HashMismatch, index, "hash");
                var key = Key(entry.ElementId, entry.Source);
                if (ledger.byKey.TryGetValue(key, out var existing))
                    return Fail(existing.Source == entry.Source ? ProvenanceCodes.DuplicateEntry : ProvenanceCodes.Conflict, index,
                        "seq " + existing.Sequence.ToString(CultureInfo.InvariantCulture));
                ledger.Add(key, entry);
            }
            if (!string.Equals(ledger.Serialize(), json, StringComparison.Ordinal)) return Fail(ProvenanceCodes.NotCanonical, null, null);
            return new(ProvenanceCodes.Ok, ledger, null, null);
        }
    }

    static ProvenanceLoadResult Fail(string code, long? index, string? detail) => new(code, null, index, detail);

    // Null when the object has exactly these members, each once; else the first unknown, repeated or missing name.
    static string? Members(JsonElement element, IReadOnlyList<string> names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) return property.Name;
        foreach (var name in names) if (!seen.Contains(name)) return name;
        return null;
    }

    static readonly string[] EntryMembers = { "seq", "element_id", "source", "prev_hash", "hash" };

    static (string? Code, ProvenanceEntry? Entry, string? Detail) ParseEntry(JsonElement item)
    {
        static (string?, ProvenanceEntry?, string?) Bad(string detail) => (ProvenanceCodes.RecordInvalid, null, detail);
        if (item.ValueKind != JsonValueKind.Object) return Bad("entry");
        if (Members(item, EntryMembers) is { } bad) return Bad(bad);
        var seq = item.GetProperty("seq");
        if (seq.ValueKind != JsonValueKind.Number || !seq.TryGetInt64(out var sequence)) return Bad("seq");
        var element = item.GetProperty("element_id");
        if (element.ValueKind != JsonValueKind.String || !Guid.TryParseExact(element.GetString(), "D", out var elementId)) return Bad("element_id");
        var source = item.GetProperty("source");
        if (source.ValueKind != JsonValueKind.Object) return Bad("source");
        string? kind = null;
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in source.EnumerateObject())
        {
            if (property.Name == "kind")
            {
                if (kind is not null || property.Value.ValueKind != JsonValueKind.String) return Bad("kind");
                kind = property.Value.GetString();
                continue;
            }
            if (fields.ContainsKey(property.Name)) return Bad(property.Name);
            fields[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String when property.Name != "journal_sequence" => property.Value.GetString(),
                JsonValueKind.Number when property.Name == "journal_sequence" && property.Value.TryGetInt64(out var value) && value >= 0 => value.ToString(CultureInfo.InvariantCulture),
                _ => null,
            };
        }
        if (kind is null) return Bad("kind");
        if (!ProvenanceKinds.IsKnown(kind)) return (ProvenanceCodes.KindUnknown, null, "kind");
        // A mistyped member is kept as null, so Create reports it by name (an extra member is reported first).
        var created = ProvenanceSource.Create(kind, fields);
        if (!created.Ok) return (created.Code, null, created.Detail);
        var prev = item.GetProperty("prev_hash");
        if (prev.ValueKind != JsonValueKind.String || !ProvenanceLedgerFormat.IsHash(prev.GetString())) return Bad("prev_hash");
        var hash = item.GetProperty("hash");
        if (hash.ValueKind != JsonValueKind.String || !ProvenanceLedgerFormat.IsHash(hash.GetString())) return Bad("hash");
        return (null, new ProvenanceEntry(sequence, elementId, created.Source!, prev.GetString()!, hash.GetString()!), null);
    }
}
