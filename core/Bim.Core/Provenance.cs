using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Bim.Core;

// P2-06 element provenance ledger: where each BIM-IR element came from. A source is one of four closed kinds; a
// kind outside this list is refused (provenance_kind_unknown), never stored. Every refusal is a typed code, not an
// exception. The ledger format and its hash chain are described in ProvenanceLedger.cs.
public static class ProvenanceKinds
{
    // A layout intent compiled into the element: intent_id + the compiler rule that produced it.
    public const string Intent = "intent";
    // A catalogue command that created or changed the element: command name + its envelope operation_id.
    public const string CatalogueCommand = "catalogue_command";
    // A change seen by the external edit journal (MirrorJournal / query.journal): document, journal sequence and
    // time, the element UniqueId and whether it was added or modified.
    public const string ExternalEdit = "external_edit";
    // A CAD import: SHA-256 of the source file + the DXF entity handle.
    public const string CadImport = "cad_import";

    public static readonly IReadOnlyList<string> All = new[] { CadImport, CatalogueCommand, ExternalEdit, Intent };
    public static bool IsKnown(string? kind) => kind is Intent or CatalogueCommand or ExternalEdit or CadImport;
}

public static class ProvenanceCodes
{
    public const string Appended = "appended";
    public const string Replayed = "replayed";
    public const string Ok = "ok";
    public const string KindUnknown = "provenance_kind_unknown";
    public const string RecordInvalid = "provenance_record_invalid";
    public const string Conflict = "provenance_conflict";
    public const string JsonInvalid = "provenance_json_invalid";
    public const string VersionUnsupported = "provenance_version_unsupported";
    public const string SequenceBroken = "provenance_sequence_broken";
    public const string ChainBroken = "provenance_chain_broken";
    public const string HashMismatch = "provenance_hash_mismatch";
    public const string NotCanonical = "provenance_not_canonical";
    public const string DuplicateEntry = "provenance_duplicate_entry";
    public const string TooLarge = "provenance_too_large";
    public const string JournalTruncated = "provenance_journal_truncated";
}

public static class ProvenanceChanges
{
    public const string Added = "added";
    public const string Modified = "modified";
}

// A closed hierarchy: only the four kinds below exist (the constructor is not reachable outside Bim.Core).
public abstract record ProvenanceSource
{
    private protected ProvenanceSource() { }
    public abstract string Kind { get; }
    // The field members in their fixed serialization order (kind excluded).
    internal abstract IReadOnlyList<string> FieldNames { get; }
    // The replay key part (the element id and kind are added by the ledger): same key = same record.
    internal abstract string KeyPart { get; }
    // The id BySource matches: intent_id, operation_id, document_unique_id or file_sha256.
    public abstract string SourceId { get; }
    internal abstract void WriteFields(Utf8JsonWriter writer);
    // Null when valid, else the offending member name.
    // strict (Append) also requires a catalogue command to be in today's catalogue; Load checks its syntax only.
    internal abstract string? InvalidField(Guid elementId, bool strict);

    static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    static readonly Regex Sha256 = new(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant, RegexTimeout);
    static readonly Regex CommandName = new(@"\A[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+\z", RegexOptions.CultureInvariant, RegexTimeout);
    static readonly Regex Handle = new(@"\A[0-9A-F]{1,16}\z", RegexOptions.CultureInvariant, RegexTimeout);
    static readonly Regex LowerUniqueId = new(@"\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}-[0-9a-f]{8}\z", RegexOptions.CultureInvariant, RegexTimeout);
    public const int MaxIdChars = 256;
    public const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    internal static bool IsId(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxIdChars && value.Trim().Length == value.Length && PermissionPolicy.IsPlainText(value);
    // Every surrogate is half of a pair (UTF-16 that UTF-8 can encode).
    internal static bool IsWellFormedUtf16(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; continue; }
            if (char.IsSurrogate(text[i])) return false;
        }
        return true;
    }
    internal static bool IsCommandName(string? value) => value is { Length: <= 128 } && CommandName.IsMatch(value);
    internal static bool IsSha256(string? value) => value is not null && Sha256.IsMatch(value);
    internal static bool IsHandle(string? value) => value is not null && Handle.IsMatch(value);
    internal static bool IsLowerUniqueId(string? value) => value is not null && LowerUniqueId.IsMatch(value);
    internal static bool IsTime(string? value) =>
        value is not null && DateTime.TryParseExact(value, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);
    internal static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    // Builds a source from its kind and string fields (journal_sequence as decimal text). Unknown kind:
    // provenance_kind_unknown; a missing, extra or non-numeric field: provenance_record_invalid. Field values are
    // validated when the record is appended.
    public static ProvenanceSourceResult Create(string? kind, IReadOnlyDictionary<string, string?>? fields)
    {
        if (!ProvenanceKinds.IsKnown(kind)) return new(ProvenanceCodes.KindUnknown, null, "kind");
        if (fields is null) return new(ProvenanceCodes.RecordInvalid, null, "fields");
        string[] names = kind switch
        {
            ProvenanceKinds.Intent => IntentProvenance.Names,
            ProvenanceKinds.CatalogueCommand => CatalogueCommandProvenance.Names,
            ProvenanceKinds.ExternalEdit => ExternalEditProvenance.Names,
            _ => CadImportProvenance.Names,
        };
        foreach (var name in fields.Keys.OrderBy(name => name, StringComparer.Ordinal))
            if (!names.Contains(name, StringComparer.Ordinal)) return new(ProvenanceCodes.RecordInvalid, null, name);
        foreach (var name in names)
            if (!fields.TryGetValue(name, out var value) || value is null) return new(ProvenanceCodes.RecordInvalid, null, name);
        string F(string name) => fields[name]!;
        switch (kind)
        {
            case ProvenanceKinds.Intent: return new(ProvenanceCodes.Ok, new IntentProvenance(F("intent_id"), F("rule_id")), null);
            case ProvenanceKinds.CatalogueCommand: return new(ProvenanceCodes.Ok, new CatalogueCommandProvenance(F("command"), F("operation_id")), null);
            case ProvenanceKinds.CadImport: return new(ProvenanceCodes.Ok, new CadImportProvenance(F("file_sha256"), F("entity_handle")), null);
            default:
                var text = F("journal_sequence");
                if (text.Length == 0 || text.Length > 18 || text.Any(c => c is < '0' or > '9') || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
                    return new(ProvenanceCodes.RecordInvalid, null, "journal_sequence");
                return new(ProvenanceCodes.Ok, new ExternalEditProvenance(F("document_unique_id"), sequence, F("recorded_at"), F("unique_id"), F("change")), null);
        }
    }

    // The external-edit sources of one journal entry: one per added and per modified UniqueId (element id = the
    // mirror's logical id for that UniqueId). Add-in entries are not external edits and give nothing; deleted
    // elements carry no UniqueId in the journal and give nothing. When the entry's added or modified list was capped
    // the code is provenance_journal_truncated and Sources holds only the ids still listed. Ids are lower-cased; an id listed as both added and
    // modified gives one added source.
    public static ProvenanceJournalSources FromJournal(MirrorJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Origin != MirrorJournalOrigins.External) return new(ProvenanceCodes.Ok, Array.Empty<(Guid, ExternalEditProvenance)>());
        // Capped added / modified lists hold fewer UniqueIds than changed: those elements get no source here.
        var truncated = entry.AddedTotal > entry.AddedUids.Count || entry.ModifiedTotal > entry.ModifiedUids.Count;
        var document = entry.DocumentUid.ToLowerInvariant();
        var time = entry.RecordedAt.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);
        var result = new List<(Guid, ExternalEditProvenance)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (uids, change) in new[] { (entry.AddedUids, ProvenanceChanges.Added), (entry.ModifiedUids, ProvenanceChanges.Modified) })
            foreach (var uid in uids)
            {
                var lower = uid.ToLowerInvariant();
                if (!seen.Add(lower)) continue; // an id both added and modified in one entry is added
                result.Add((MirrorSnapshotMapper.Id(document, lower), new ExternalEditProvenance(document, entry.Sequence, time, lower, change)));
            }
        return new(truncated ? ProvenanceCodes.JournalTruncated : ProvenanceCodes.Ok, result);
    }
}

public sealed record ProvenanceJournalSources(string Code, IReadOnlyList<(Guid ElementId, ExternalEditProvenance Source)> Sources)
{
    public bool Ok => Code == ProvenanceCodes.Ok;
}

public sealed record ProvenanceSourceResult(string Code, ProvenanceSource? Source, string? Detail)
{
    public bool Ok => Code == ProvenanceCodes.Ok;
}

public sealed record IntentProvenance(string IntentId, string RuleId) : ProvenanceSource
{
    internal static readonly string[] Names = { "intent_id", "rule_id" };
    public override string Kind => ProvenanceKinds.Intent;
    internal override IReadOnlyList<string> FieldNames => Names;
    internal override string KeyPart => IntentId;
    public override string SourceId => IntentId;
    internal override void WriteFields(Utf8JsonWriter writer) { writer.WriteString("intent_id", IntentId); writer.WriteString("rule_id", RuleId); }
    internal override string? InvalidField(Guid elementId, bool strict) => !IsId(IntentId) ? "intent_id" : !IsId(RuleId) ? "rule_id" : null;
}

public sealed record CatalogueCommandProvenance(string Command, string OperationId) : ProvenanceSource
{
    internal static readonly string[] Names = { "command", "operation_id" };
    public override string Kind => ProvenanceKinds.CatalogueCommand;
    internal override IReadOnlyList<string> FieldNames => Names;
    internal override string KeyPart => OperationId;
    public override string SourceId => OperationId;
    internal override void WriteFields(Utf8JsonWriter writer) { writer.WriteString("command", Command); writer.WriteString("operation_id", OperationId); }
    internal override string? InvalidField(Guid elementId, bool strict) =>
        !IsCommandName(Command) || (strict && RevitCommandCatalogue.Find(Command) is null) ? "command" : !RevitCommandProtocol.IsOperationId(OperationId) ? "operation_id" : null;
}

public sealed record ExternalEditProvenance(string DocumentUid, long JournalSequence, string RecordedAt, string UniqueId, string Change) : ProvenanceSource
{
    internal static readonly string[] Names = { "document_unique_id", "journal_sequence", "recorded_at", "unique_id", "change" };
    public override string Kind => ProvenanceKinds.ExternalEdit;
    internal override IReadOnlyList<string> FieldNames => Names;
    // The journal sequence restarts with the journal, so its recorded time is part of the key.
    internal override string KeyPart => DocumentUid + "\n" + Text(JournalSequence) + "\n" + RecordedAt;
    public override string SourceId => DocumentUid;
    internal override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteString("document_unique_id", DocumentUid);
        writer.WriteNumber("journal_sequence", JournalSequence);
        writer.WriteString("recorded_at", RecordedAt);
        writer.WriteString("unique_id", UniqueId);
        writer.WriteString("change", Change);
    }
    internal override string? InvalidField(Guid elementId, bool strict) =>
        !IsLowerUniqueId(DocumentUid) ? "document_unique_id"
        : JournalSequence < 1 ? "journal_sequence"
        : !IsTime(RecordedAt) ? "recorded_at"
        : !IsLowerUniqueId(UniqueId) ? "unique_id"
        : Change is not (ProvenanceChanges.Added or ProvenanceChanges.Modified) ? "change"
        : MirrorSnapshotMapper.Id(DocumentUid, UniqueId) != elementId ? "element_id"
        : null;
}

public sealed record CadImportProvenance(string FileSha256, string EntityHandle) : ProvenanceSource
{
    internal static readonly string[] Names = { "file_sha256", "entity_handle" };
    public override string Kind => ProvenanceKinds.CadImport;
    internal override IReadOnlyList<string> FieldNames => Names;
    internal override string KeyPart => FileSha256 + "\n" + EntityHandle;
    public override string SourceId => FileSha256;
    internal override void WriteFields(Utf8JsonWriter writer) { writer.WriteString("file_sha256", FileSha256); writer.WriteString("entity_handle", EntityHandle); }
    internal override string? InvalidField(Guid elementId, bool strict) => !IsSha256(FileSha256) ? "file_sha256" : !IsHandle(EntityHandle) ? "entity_handle" : null;
}
