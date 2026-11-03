using System.Globalization;
using System.Text.Json;
namespace Bim.Core;

// P2-04 drift between the Revit mirror (a query.snapshot, MirrorMapper.cs) and the Core store's committed BIM-IR for the
// same document. Pure and deterministic: the same inputs in any order give byte-identical JSON (drift_version 1, a
// diagnostic reply that is never persisted). Elements are matched by Revit UniqueId (ignoring case): a committed
// element's UniqueId comes from the logical map the add-in keeps next to the project in ExtensibleStorage (BIM-IR id ->
// UniqueId, BatchExecutor.State.Map); a snapshot element's from the snapshot's sources. Only the kinds query.snapshot
// exports (level, wall, door, window, space) are compared; other committed kinds (plan, schedule, sheet, ...) are
// listed as not_comparable and never counted as drift. Findings:
//   missing  comparable committed element whose UniqueId is not in the snapshot (or that has no UniqueId in the map)
//   extra    mapped snapshot element whose UniqueId no committed element maps to
//   changed  matched pair with field differences: geometry within a length tolerance (mm) plus, for axis kinds, the axis
//            direction within an angle tolerance (degrees); kind exactly; dependencies as UniqueIds split by role
//            (level, host, bounding_walls, dependencies), each compared as a set
// Each finding is classified from the evidence given: external_edit (a journal entry not made by the add-in names the
// element), addin_pending (an add-in journal entry or command receipt newer than Core's committed revision names it and
// no external one does), unknown (no evidence). Snapshot unmapped entries are listed with classification unmapped and
// are never counted as drift, even when a committed element maps to them.

// The Core store's committed state for one document: the project (revision included) and the logical map.
// IgnoredMapKeys lists map keys that are not BIM-IR ids (the native fixtures also store role keys such as "door-symbol").
public sealed record MirrorCommittedModel(string DocumentUid, Project Project, IReadOnlyDictionary<Guid, string> NativeIds)
{
    public IReadOnlyList<string> IgnoredMapKeys { get; init; } = Array.Empty<string>();

    // {"document_unique_id":..,"project":<Project as the Core store persists it>,"map":{"<key>":"<UniqueId>"}}
    // Map keys that are not Guids are ignored and listed; a non-string value is refused.
    public static MirrorCommittedModel Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("document_unique_id", out var uid) || uid.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("project", out var project) || project.ValueKind != JsonValueKind.Object)
                throw new ArgumentException(MirrorDrift.ModelInvalidCode + " committed model needs document_unique_id and project");
            var parsed = JsonSerializer.Deserialize<Project>(project.GetRawText()) ?? throw new ArgumentException(MirrorDrift.ModelInvalidCode + " project");
            var map = new Dictionary<Guid, string>();
            var ignored = new List<string>();
            if (root.TryGetProperty("map", out var mapElement) && mapElement.ValueKind != JsonValueKind.Null)
            {
                if (mapElement.ValueKind != JsonValueKind.Object) throw new ArgumentException(MirrorDrift.ModelInvalidCode + " map");
                foreach (var member in mapElement.EnumerateObject())
                {
                    if (member.Value.ValueKind != JsonValueKind.String) throw new ArgumentException(MirrorDrift.ModelInvalidCode + " map value");
                    if (Guid.TryParse(member.Name, out var id)) { if (!map.TryAdd(id, member.Value.GetString() ?? "")) throw new ArgumentException(MirrorDrift.ModelInvalidCode + " duplicate map key"); }
                    else ignored.Add(member.Name);
                }
            }
            return new MirrorCommittedModel(uid.GetString() ?? "", parsed, map) { IgnoredMapKeys = ignored.Order(StringComparer.Ordinal).ToArray() };
        }
        catch (JsonException) { throw new ArgumentException(MirrorDrift.ModelInvalidCode + " not JSON"); }
    }
}

// A command receipt the add-in wrote (operation id, command, document) with the Core revision it ran against and the
// UniqueIds it touched. CoreRevision >= the committed revision means Core has not absorbed it yet: addin_pending.
// The add-in's stored receipts (CommandsContract.cs) carry no Core revision; the caller that dispatched the command
// (the plan runner or CLI) builds this record from the receipt's operation id and the revision it planned against.
public sealed record MirrorAddInReceipt(string OperationId, string Command, string DocumentUid, int CoreRevision, IReadOnlyList<string> UniqueIds);

// A journal read as evidence: its entries plus what the reply says about its own completeness. CurrentSequence is the
// reply's current (or last) sequence when it states one.
public sealed record MirrorJournalEvidence(IReadOnlyList<MirrorJournalEntry> Entries)
{
    public bool Complete { get; init; } = true;
    public bool HasMore { get; init; }
    public long? CurrentSequence { get; init; }
}

// JournalAfterSequence is the journal sequence at Core's committed revision: entries at or before it are older than the
// commit and are not evidence. Sequences belong to one add-in session (they restart with Revit). MaxFindings caps the
// findings, unmapped and not_comparable lists of one report (totals stay true).
public sealed record MirrorDriftOptions(double LengthToleranceMm = MirrorDriftOptions.DefaultLengthToleranceMm, double AngleToleranceDeg = MirrorDriftOptions.DefaultAngleToleranceDeg,
    long JournalAfterSequence = 0, int MaxFindings = MirrorDriftOptions.DefaultMaxFindings)
{
    public const double DefaultLengthToleranceMm = 1;
    public const double DefaultAngleToleranceDeg = 0.1;
    public const int DefaultMaxFindings = 5000;
    public const int MaxFindingsLimit = 20000;
    public const string InvalidCode = "drift_options_invalid";

    internal void Validate()
    {
        if (!double.IsFinite(LengthToleranceMm) || LengthToleranceMm < 0 || LengthToleranceMm > 1000
            || !double.IsFinite(AngleToleranceDeg) || AngleToleranceDeg < 0 || AngleToleranceDeg > 180
            || JournalAfterSequence < 0 || MaxFindings < 1 || MaxFindings > MaxFindingsLimit)
            throw new ArgumentException(InvalidCode);
    }
}

public static class MirrorDriftKinds
{
    public const string Changed = "changed";
    public const string Extra = "extra";
    public const string Missing = "missing";
}

public static class MirrorDriftClasses
{
    public const string ExternalEdit = "external_edit";
    public const string AddInPending = "addin_pending";
    public const string Unmapped = "unmapped";
    public const string Unknown = "unknown";
    public const string NotComparable = "not_comparable";
}

// Reasons the report's evidence may be incomplete (evidence.notes).
public static class MirrorDriftEvidenceNotes
{
    public const string JournalIncomplete = "journal_incomplete";
    public const string JournalHasMore = "journal_has_more";
    public const string JournalSessionMismatch = "journal_session_mismatch";
    public const string EntryTruncated = "entry_truncated";
}

// One field difference. Core / Revit are fixed-format text (metres with 6 decimals, degrees with 4, or exact text);
// Delta is Revit minus Core in millimetres (3 decimals) or degrees (4 decimals), null for exact fields.
public sealed record MirrorDriftField(string Field, string? Core, string? Revit, string? Delta);

public sealed record MirrorDriftFinding(string Kind, string UniqueId, Guid? CoreId, Guid? SnapshotId, string ElementKind, string Classification,
    IReadOnlyList<string> Evidence, IReadOnlyList<MirrorDriftField> Fields);

public sealed record MirrorDriftUnmapped(string UniqueId, string Category, string Reason, Guid? CoreId);

// A committed element of a kind query.snapshot never exports; reported, never drift.
public sealed record MirrorDriftNotComparable(Guid CoreId, string ElementKind, string UniqueId, string Reason);

public sealed record MirrorDriftSummary(int Missing, int Extra, int Changed, int Unchanged, int Unmapped, int ExternalEdit, int AddInPending, int Unknown, int IgnoredEvidence)
{
    public int NotComparable { get; init; }
    public int IgnoredMapKeys { get; init; }
    public int Drift => Missing + Extra + Changed;
}

public sealed record MirrorDriftReport(string DocumentUid, Guid CoreProjectId, int CoreRevision, MirrorDriftOptions Options, MirrorDriftSummary Summary,
    IReadOnlyList<MirrorDriftFinding> Findings, IReadOnlyList<MirrorDriftUnmapped> Unmapped, bool Truncated)
{
    public const int Version = 1;
    public IReadOnlyList<MirrorDriftNotComparable> NotComparable { get; init; } = Array.Empty<MirrorDriftNotComparable>();
    public IReadOnlyList<string> EvidenceNotes { get; init; } = Array.Empty<string>();
    public bool EvidenceIncomplete => EvidenceNotes.Count > 0;
    public bool HasDrift => Summary.Drift > 0;

    // {"drift_version":1,"document_unique_id","core_project_id","core_revision","options":{..},"summary":{..},
    //  "evidence":{"incomplete","notes":[..]},
    //  "findings":[{"kind","unique_id","core_id","snapshot_id","element_kind","classification","evidence":[..],
    //  "fields":[{"field","core","revit","delta"}]}],"unmapped":[{"unique_id","category","reason","core_id","classification"}],
    //  "not_comparable":[{"core_id","element_kind","unique_id","reason","classification"}]}
    public string ToJson() => MirrorJson.Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteNumber("drift_version", Version);
        writer.WriteString("document_unique_id", DocumentUid);
        writer.WriteString("core_project_id", CoreProjectId.ToString("D"));
        writer.WriteNumber("core_revision", CoreRevision);
        writer.WriteStartObject("options");
        writer.WritePropertyName("length_tolerance_mm");
        writer.WriteRawValue(MirrorDrift.Fixed(Options.LengthToleranceMm, 3));
        writer.WritePropertyName("angle_tolerance_deg");
        writer.WriteRawValue(MirrorDrift.Fixed(Options.AngleToleranceDeg, 4));
        writer.WriteNumber("journal_after_sequence", Options.JournalAfterSequence);
        writer.WriteNumber("max_findings", Options.MaxFindings);
        writer.WriteEndObject();
        writer.WriteStartObject("summary");
        writer.WriteNumber("drift", Summary.Drift);
        writer.WriteNumber("missing", Summary.Missing);
        writer.WriteNumber("extra", Summary.Extra);
        writer.WriteNumber("changed", Summary.Changed);
        writer.WriteNumber("unchanged", Summary.Unchanged);
        writer.WriteNumber("unmapped", Summary.Unmapped);
        writer.WriteNumber("not_comparable", Summary.NotComparable);
        writer.WriteNumber("external_edit", Summary.ExternalEdit);
        writer.WriteNumber("addin_pending", Summary.AddInPending);
        writer.WriteNumber("unknown", Summary.Unknown);
        writer.WriteNumber("ignored_evidence", Summary.IgnoredEvidence);
        writer.WriteNumber("ignored_map_keys", Summary.IgnoredMapKeys);
        writer.WriteBoolean("truncated", Truncated);
        writer.WriteEndObject();
        writer.WriteStartObject("evidence");
        writer.WriteBoolean("incomplete", EvidenceIncomplete);
        MirrorJson.Strings(writer, "notes", EvidenceNotes);
        writer.WriteEndObject();
        writer.WriteStartArray("findings");
        foreach (var finding in Findings)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", finding.Kind);
            writer.WriteString("unique_id", finding.UniqueId);
            Id(writer, "core_id", finding.CoreId);
            Id(writer, "snapshot_id", finding.SnapshotId);
            writer.WriteString("element_kind", finding.ElementKind);
            writer.WriteString("classification", finding.Classification);
            MirrorJson.Strings(writer, "evidence", finding.Evidence);
            writer.WriteStartArray("fields");
            foreach (var field in finding.Fields)
            {
                writer.WriteStartObject();
                writer.WriteString("field", field.Field);
                Text(writer, "core", field.Core);
                Text(writer, "revit", field.Revit);
                Text(writer, "delta", field.Delta);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("unmapped");
        foreach (var entry in Unmapped)
        {
            writer.WriteStartObject();
            writer.WriteString("unique_id", entry.UniqueId);
            writer.WriteString("category", entry.Category);
            writer.WriteString("reason", entry.Reason);
            Id(writer, "core_id", entry.CoreId);
            writer.WriteString("classification", MirrorDriftClasses.Unmapped);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("not_comparable");
        foreach (var entry in NotComparable)
        {
            writer.WriteStartObject();
            writer.WriteString("core_id", entry.CoreId.ToString("D"));
            writer.WriteString("element_kind", entry.ElementKind);
            writer.WriteString("unique_id", entry.UniqueId);
            writer.WriteString("reason", entry.Reason);
            writer.WriteString("classification", MirrorDriftClasses.NotComparable);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    static void Id(Utf8JsonWriter writer, string name, Guid? id)
    {
        if (id is { } value) writer.WriteString(name, value.ToString("D")); else writer.WriteNull(name);
    }

    static void Text(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteString(name, value);
    }
}

public static class MirrorDrift
{
    public const string DocumentRequiredCode = "drift_document_required";
    public const string DocumentMismatchCode = "drift_document_mismatch";
    public const string ModelInvalidCode = "drift_model_invalid";
    public const string MapInvalidCode = "drift_map_invalid";
    public const string TooLargeCode = "drift_input_too_large";
    public const string JournalInvalidCode = "drift_journal_invalid";
    public const string SnapshotInvalidCode = "drift_snapshot_invalid";
    public const string KindNotExported = "kind_not_exported";
    // Bounds on what one comparison accepts; more is refused rather than producing an unbounded report.
    public const int MaxCommittedElements = MirrorSnapshotMapper.MaxElements;
    public const int MaxJournalEntries = 4096;
    public const int MaxReceipts = 2000;

    // Named geometry fields per BIM-IR kind (MirrorMapper.cs layouts); every value is a length in metres. These are
    // exactly the kinds query.snapshot exports, so they are the comparable kinds.
    static readonly IReadOnlyDictionary<string, string[]> FieldNames = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["level"] = new[] { "elevation" },
        ["wall"] = new[] { "ax", "ay", "bx", "by", "height", "thickness" },
        ["door"] = new[] { "ax", "ay", "bx", "by", "sill", "height" },
        ["window"] = new[] { "ax", "ay", "bx", "by", "sill", "height" },
        ["space"] = new[] { "x", "y", "width", "depth" },
    };
    static readonly HashSet<string> AxisKinds = new(StringComparer.Ordinal) { "wall", "door", "window" };

    public static bool IsComparableKind(string? kind) => !ReviewFixes || (kind is not null && FieldNames.ContainsKey(kind));
    static readonly bool ReviewFixes = false;

    // Fixed-format invariant number text; values that round to zero print without a sign.
    internal static string Fixed(double value, int decimals) =>
        (Math.Round(value, decimals, MidpointRounding.AwayFromZero) + 0.0).ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    // The ElementId a Revit UniqueId ends with ("<36-char episode guid>-<element id in hex>"), or null when it has
    // another shape. Used only to match journal deletions of missing elements, which carry element ids because deleted
    // elements have no UniqueId any more; the convention is Revit's, not an API guarantee, so such evidence is labelled
    // ":deleted_id_match".
    public static long? NativeElementId(string? uniqueId)
    {
        if (uniqueId is null || uniqueId.Length < 38 || uniqueId.Length > 36 + 1 + 16 || uniqueId[36] != '-') return null;
        return long.TryParse(uniqueId.AsSpan(37), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    // The entries of a journal reply: the journal.read pipe reply ({"status":"succeeded",..,"report":{journal_version 1}}),
    // its report alone (MirrorJournalReport.ToJson) or the raw journal (MirrorJournal.ToJson).
    public static IReadOnlyList<MirrorJournalEntry> ParseJournalEntries(string json) => ParseJournal(json).Entries;

    // The same, with the reply's completeness: journal_complete false, has_more true, or (raw journal) dropped or
    // failures above zero make the evidence incomplete. A reply read without peek moved the add-in's baseline and is
    // refused, as is a status other than succeeded or an origin other than external / add-in.
    public static MirrorJournalEvidence ParseJournal(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException(JournalInvalidCode + " object required");
            var body = root;
            if (ReviewFixes && root.TryGetProperty("status", out var status))
            {
                if (status.ValueKind != JsonValueKind.String || status.GetString() != "succeeded") throw new ArgumentException(JournalInvalidCode + " status is not succeeded");
                if (!root.TryGetProperty("report", out body) || body.ValueKind != JsonValueKind.Object) throw new ArgumentException(JournalInvalidCode + " report required");
            }
            if (body.TryGetProperty("journal_version", out var version) && (!version.TryGetInt32(out var number) || number != MirrorJournal.Version))
                throw new ArgumentException(JournalInvalidCode + " journal_version");
            if (!body.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new ArgumentException(JournalInvalidCode + " entries array required");
            if (body.TryGetProperty("peek", out var peek) && peek.ValueKind == JsonValueKind.False) throw new ArgumentException(JournalInvalidCode + " journal.read must use peek");
            if (entries.GetArrayLength() > MaxJournalEntries) throw new ArgumentException(TooLargeCode + " journal entries");
            bool Flag(string name, bool fallback) => body.TryGetProperty(name, out var value) ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new ArgumentException(JournalInvalidCode + " " + name),
            } : fallback;
            long Count(string name) => body.TryGetProperty(name, out var value) ? value.GetInt64() : 0;
            long? Sequence(string name) => body.TryGetProperty(name, out var value) ? value.GetInt64() : null;
            var complete = body.TryGetProperty("journal_complete", out _) ? Flag("journal_complete", true) : Count("dropped") == 0 && Count("failures") == 0;
            var result = new List<MirrorJournalEntry>();
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) throw new ArgumentException(JournalInvalidCode + " entry must be an object");
                if (!entry.TryGetProperty("sequence", out var sequenceElement) || !sequenceElement.TryGetInt64(out var sequence) || sequence < 1)
                    throw new ArgumentException(JournalInvalidCode + " sequence");
                string Required(string name) => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : throw new ArgumentException(JournalInvalidCode + " " + name);
                string Optional(string name) => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                string[] Strings(string name)
                {
                    if (!entry.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return Array.Empty<string>();
                    if (value.ValueKind != JsonValueKind.Array) throw new ArgumentException(JournalInvalidCode + " " + name);
                    return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : throw new ArgumentException(JournalInvalidCode + " " + name)).ToArray();
                }
                long[] Ids()
                {
                    if (!entry.TryGetProperty("deleted_element_ids", out var value) || value.ValueKind == JsonValueKind.Null) return Array.Empty<long>();
                    if (value.ValueKind != JsonValueKind.Array) throw new ArgumentException(JournalInvalidCode + " deleted_element_ids");
                    return value.EnumerateArray().Select(item => item.TryGetInt64(out var id) ? id : throw new ArgumentException(JournalInvalidCode + " deleted_element_ids")).ToArray();
                }
                int Total(string name, int fallback) => entry.TryGetProperty(name, out var value) && value.TryGetInt32(out var total) && total >= 0 ? total : fallback;
                var origin = Required("origin");
                if (origin != MirrorJournalOrigins.External && origin != MirrorJournalOrigins.AddIn) throw new ArgumentException(JournalInvalidCode + " origin");
                var recordedText = Optional("recorded_at");
                var recorded = recordedText.Length == 0 ? DateTimeOffset.UnixEpoch
                    : DateTimeOffset.TryParse(recordedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : throw new ArgumentException(JournalInvalidCode + " recorded_at");
                var added = Strings("added");
                var modified = Strings("modified");
                var deleted = Ids();
                var truncated = entry.TryGetProperty("truncated", out var truncatedElement) && truncatedElement.ValueKind == JsonValueKind.True;
                result.Add(new MirrorJournalEntry(sequence, recorded, Required("document_unique_id"), Optional("operation"), origin, Strings("transaction_names"),
                    added, modified, deleted, Total("added_total", added.Length), Total("modified_total", modified.Length), Total("deleted_total", deleted.Length), Optional("document_path"), truncated));
            }
            return new MirrorJournalEvidence(result)
            {
                Complete = complete,
                HasMore = Flag("has_more", false),
                CurrentSequence = Sequence("current_sequence") ?? Sequence("last_sequence"),
            };
        }
        catch (JsonException) { throw new ArgumentException(JournalInvalidCode + " not JSON"); }
        catch (InvalidOperationException) { throw new ArgumentException(JournalInvalidCode + " value type"); }
        catch (FormatException) { throw new ArgumentException(JournalInvalidCode + " number"); }
    }

    // A query.snapshot reply (snapshot_version 1, MirrorSnapshot.ToJson) read back into a MirrorSnapshot.
    public static MirrorSnapshot ParseSnapshot(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("snapshot_version", out var version) || !version.TryGetInt32(out var number) || number != MirrorSnapshotMapper.Version
                || !root.TryGetProperty("document_unique_id", out var uid) || uid.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("elements", out var elementsElement) || elementsElement.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("sources", out var sourcesElement) || sourcesElement.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("unmapped", out var unmappedElement) || unmappedElement.ValueKind != JsonValueKind.Array)
                throw new ArgumentException(SnapshotInvalidCode + " snapshot_version 1 with document_unique_id, elements, sources and unmapped required");
            if (elementsElement.GetArrayLength() + unmappedElement.GetArrayLength() > MirrorSnapshotMapper.MaxElements) throw new ArgumentException(TooLargeCode + " snapshot");
            string Text(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : throw new ArgumentException(SnapshotInvalidCode + " " + name);
            var elements = elementsElement.EnumerateArray().Select(item => JsonSerializer.Deserialize<Element>(item.GetRawText()) ?? throw new ArgumentException(SnapshotInvalidCode + " element")).ToArray();
            var sources = new Dictionary<Guid, string>();
            var categories = new Dictionary<Guid, string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in sourcesElement.EnumerateArray())
            {
                var sourceUid = Text(item, "unique_id");
                if (!Guid.TryParse(Text(item, "id"), out var id) || !sources.TryAdd(id, sourceUid)) throw new ArgumentException(SnapshotInvalidCode + " source id");
                // The mapper never maps a UniqueId twice (ignoring case); a reply that does cannot be matched.
                if (string.IsNullOrWhiteSpace(sourceUid) || !seen.Add(sourceUid.ToLowerInvariant())) throw new ArgumentException(SnapshotInvalidCode + " duplicate or blank unique_id");
                categories[id] = Text(item, "category");
            }
            if (elements.Any(element => element.Kind is null || element.GeometryMetres is null || element.Dependencies is null || !sources.ContainsKey(element.Id))
                || elements.Select(element => element.Id).Distinct().Count() != elements.Length || sources.Count != elements.Length)
                throw new ArgumentException(SnapshotInvalidCode + " every element needs exactly one source");
            var unmapped = unmappedElement.EnumerateArray().Select(item => new MirrorUnmapped(Text(item, "category"), Text(item, "unique_id"), Text(item, "reason"), Text(item, "detail"))).ToArray();
            return new MirrorSnapshot(uid.GetString() ?? "", elements, sources, categories, unmapped);
        }
        catch (JsonException) { throw new ArgumentException(SnapshotInvalidCode + " not JSON"); }
        catch (InvalidOperationException) { throw new ArgumentException(SnapshotInvalidCode + " value type"); }
    }

    sealed class Evidence
    {
        public readonly List<(long Sequence, string Text, bool External)> Journal = new();
        public readonly List<string> Receipts = new();
    }

    // Compare with a parsed journal reply: its completeness is carried into the report's evidence notes.
    public static MirrorDriftReport Compare(MirrorSnapshot snapshot, MirrorCommittedModel committed, MirrorJournalEvidence journal,
        MirrorDriftOptions? options = null, IReadOnlyList<MirrorAddInReceipt>? receipts = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return Compare(snapshot, committed, journal.Entries, options, receipts, journal);
    }

    public static MirrorDriftReport Compare(MirrorSnapshot snapshot, MirrorCommittedModel committed, IReadOnlyList<MirrorJournalEntry>? journal = null,
        MirrorDriftOptions? options = null, IReadOnlyList<MirrorAddInReceipt>? receipts = null) =>
        Compare(snapshot, committed, journal, options, receipts, null);

    static MirrorDriftReport Compare(MirrorSnapshot snapshot, MirrorCommittedModel committed, IReadOnlyList<MirrorJournalEntry>? journal,
        MirrorDriftOptions? options, IReadOnlyList<MirrorAddInReceipt>? receipts, MirrorJournalEvidence? reply)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(committed);
        options ??= new MirrorDriftOptions();
        options.Validate();
        if (string.IsNullOrWhiteSpace(snapshot.DocumentUid) || string.IsNullOrWhiteSpace(committed.DocumentUid)) throw new ArgumentException(DocumentRequiredCode);
        if (!string.Equals(snapshot.DocumentUid, committed.DocumentUid, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException(DocumentMismatchCode);
        if (committed.Project?.Elements is not { } elements) throw new ArgumentException(ModelInvalidCode);
        journal ??= Array.Empty<MirrorJournalEntry>();
        receipts ??= Array.Empty<MirrorAddInReceipt>();
        if (elements.Length > MaxCommittedElements || journal.Count > MaxJournalEntries || receipts.Count > MaxReceipts) throw new ArgumentException(TooLargeCode);
        if (elements.Any(element => element is null) || elements.Select(element => element.Id).Distinct().Count() != elements.Length) throw new ArgumentException(ModelInvalidCode);
        if (journal.Any(entry => entry is not null && entry.Origin != MirrorJournalOrigins.External && entry.Origin != MirrorJournalOrigins.AddIn)) throw new ArgumentException(JournalInvalidCode + " origin");
        var nativeIds = committed.NativeIds ?? new Dictionary<Guid, string>();
        var revision = committed.Project.Revision;
        var coreKinds = elements.ToDictionary(element => element.Id, element => element.Kind);
        var snapshotKinds = snapshot.Elements.ToDictionary(element => element.Id, element => element.Kind);

        // Committed elements by UniqueId; two committed ids on one UniqueId is a corrupt map. Kinds the snapshot never
        // exports still claim their UniqueId (no extra) but are only listed as not comparable.
        var core = new Dictionary<string, (Element Element, string Uid)>(StringComparer.Ordinal);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var unrealized = new List<Element>();
        var notComparable = new List<MirrorDriftNotComparable>();
        foreach (var element in elements)
        {
            var hasUid = nativeIds.TryGetValue(element.Id, out var uid) && !string.IsNullOrWhiteSpace(uid);
            if (hasUid && !claimed.Add(uid!.ToLowerInvariant())) throw new ArgumentException(MapInvalidCode + " two committed elements map to one UniqueId");
            if (!IsComparableKind(element.Kind)) notComparable.Add(new MirrorDriftNotComparable(element.Id, element.Kind ?? "", hasUid ? uid! : "", KindNotExported));
            else if (hasUid) core.Add(uid!.ToLowerInvariant(), (element, uid!));
            else unrealized.Add(element);
        }
        var observed = new Dictionary<string, (Element Element, string Uid)>(StringComparer.Ordinal);
        foreach (var element in snapshot.Elements) observed.TryAdd(snapshot.Sources[element.Id].ToLowerInvariant(), (element, snapshot.Sources[element.Id]));
        var unmappedKeys = new HashSet<string>(snapshot.Unmapped.Where(entry => !string.IsNullOrWhiteSpace(entry.UniqueId)).Select(entry => entry.UniqueId.ToLowerInvariant()), StringComparer.Ordinal);

        // Evidence notes: the reply's own completeness, a commit point the reply's session never reached, truncated entries.
        var notes = new List<string>();
        var journalUsable = true;
        if (reply is not null)
        {
            if (!reply.Complete) notes.Add(MirrorDriftEvidenceNotes.JournalIncomplete);
            if (reply.HasMore) notes.Add(MirrorDriftEvidenceNotes.JournalHasMore);
            if (reply.CurrentSequence is { } current && current < options.JournalAfterSequence) { notes.Add(MirrorDriftEvidenceNotes.JournalSessionMismatch); journalUsable = false; }
        }

        // Evidence by UniqueId (lower case) and, for deletions, by element id.
        var ignored = 0;
        var byUid = new Dictionary<string, Evidence>(StringComparer.Ordinal);
        var byDeleted = new Dictionary<long, List<(long Sequence, string Text, bool External)>>();
        var truncatedSequences = new SortedSet<long>();
        Evidence For(string key) { if (!byUid.TryGetValue(key, out var evidence)) byUid[key] = evidence = new Evidence(); return evidence; }
        foreach (var entry in journal)
        {
            if (entry is null || !string.Equals(entry.DocumentUid, committed.DocumentUid, StringComparison.OrdinalIgnoreCase)) { ignored++; continue; }
            if (!journalUsable || entry.Sequence <= options.JournalAfterSequence) continue;
            if (entry.Truncated) truncatedSequences.Add(entry.Sequence);
            var external = entry.Origin == MirrorJournalOrigins.External;
            var sequence = entry.Sequence.ToString(CultureInfo.InvariantCulture);
            foreach (var uid in (entry.AddedUids ?? Array.Empty<string>()).Concat(entry.ModifiedUids ?? Array.Empty<string>()).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.ToLowerInvariant()).Distinct(StringComparer.Ordinal))
                For(uid).Journal.Add((entry.Sequence, "journal:" + sequence + ":" + entry.Origin, external));
            foreach (var id in (entry.DeletedIds ?? Array.Empty<long>()).Distinct())
            {
                if (!byDeleted.TryGetValue(id, out var list)) byDeleted[id] = list = new();
                list.Add((entry.Sequence, "journal:" + sequence + ":" + entry.Origin + ":deleted_id_match", external));
            }
        }
        notes.AddRange(truncatedSequences.Select(sequence => MirrorDriftEvidenceNotes.EntryTruncated + ":" + sequence.ToString(CultureInfo.InvariantCulture)));
        foreach (var receipt in receipts)
        {
            if (receipt is null || !string.Equals(receipt.DocumentUid, committed.DocumentUid, StringComparison.OrdinalIgnoreCase)) { ignored++; continue; }
            if (receipt.CoreRevision < revision) continue;
            foreach (var uid in (receipt.UniqueIds ?? Array.Empty<string>()).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.ToLowerInvariant()).Distinct(StringComparer.Ordinal))
                For(uid).Receipts.Add("receipt:" + receipt.OperationId);
        }

        // Deletion evidence (element id read from the UniqueId) applies only to missing elements.
        (string Classification, IReadOnlyList<string> Evidence) Classify(string? key, string? uid, bool missing)
        {
            var journalItems = new List<(long Sequence, string Text, bool External)>();
            var receiptItems = new List<string>();
            if (key is not null && byUid.TryGetValue(key, out var evidence)) { journalItems.AddRange(evidence.Journal); receiptItems.AddRange(evidence.Receipts); }
            if (missing && NativeElementId(uid) is { } nativeId && byDeleted.TryGetValue(nativeId, out var deletions)) journalItems.AddRange(deletions);
            var texts = journalItems.OrderBy(item => item.Sequence).ThenBy(item => item.Text, StringComparer.Ordinal).Select(item => item.Text)
                .Concat(receiptItems.Order(StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
            var classification = journalItems.Any(item => item.External) ? MirrorDriftClasses.ExternalEdit
                : journalItems.Count > 0 || receiptItems.Count > 0 ? MirrorDriftClasses.AddInPending : MirrorDriftClasses.Unknown;
            return (classification, texts);
        }

        var findings = new List<MirrorDriftFinding>();
        var unchanged = 0;
        var context = new Context(nativeIds, snapshot.Sources, coreKinds, snapshotKinds, options);
        foreach (var (key, (element, uid)) in core)
        {
            if (observed.TryGetValue(key, out var match))
            {
                var fields = Fields(element, match.Element, context);
                if (fields.Count == 0) { unchanged++; continue; }
                var (classification, evidence) = Classify(key, uid, false);
                findings.Add(new MirrorDriftFinding(MirrorDriftKinds.Changed, match.Uid, element.Id, match.Element.Id, element.Kind, classification, evidence, fields));
            }
            else if (!unmappedKeys.Contains(key))
            {
                var (classification, evidence) = Classify(key, uid, true);
                findings.Add(new MirrorDriftFinding(MirrorDriftKinds.Missing, uid, element.Id, null, element.Kind, classification, evidence, Array.Empty<MirrorDriftField>()));
            }
        }
        foreach (var element in unrealized)
            findings.Add(new MirrorDriftFinding(MirrorDriftKinds.Missing, "", element.Id, null, element.Kind, MirrorDriftClasses.Unknown, new[] { "map:missing" }, Array.Empty<MirrorDriftField>()));
        foreach (var (key, (element, uid)) in observed)
        {
            if (claimed.Contains(key)) continue;
            var (classification, evidence) = Classify(key, uid, false);
            findings.Add(new MirrorDriftFinding(MirrorDriftKinds.Extra, uid, null, element.Id, element.Kind, classification, evidence, Array.Empty<MirrorDriftField>()));
        }

        var sorted = findings.OrderBy(finding => finding.Kind, StringComparer.Ordinal).ThenBy(finding => finding.UniqueId.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(finding => finding.UniqueId, StringComparer.Ordinal).ThenBy(finding => finding.CoreId?.ToString("D") ?? "", StringComparer.Ordinal).ToArray();
        var unmapped = snapshot.Unmapped
            .Select(entry => new MirrorDriftUnmapped(entry.UniqueId ?? "", entry.Category ?? "", entry.Reason ?? "",
                !string.IsNullOrWhiteSpace(entry.UniqueId) && core.TryGetValue(entry.UniqueId.ToLowerInvariant(), out var owner) ? owner.Element.Id : null))
            .OrderBy(entry => entry.UniqueId.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(entry => entry.UniqueId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Category, StringComparer.Ordinal).ThenBy(entry => entry.Reason, StringComparer.Ordinal).ToArray();
        var others = notComparable.OrderBy(entry => entry.ElementKind, StringComparer.Ordinal).ThenBy(entry => entry.CoreId.ToString("D"), StringComparer.Ordinal).ToArray();
        int Count(string kind) => sorted.Count(finding => finding.Kind == kind);
        int Classified(string classification) => sorted.Count(finding => finding.Classification == classification);
        var summary = new MirrorDriftSummary(Count(MirrorDriftKinds.Missing), Count(MirrorDriftKinds.Extra), Count(MirrorDriftKinds.Changed), unchanged, unmapped.Length,
            Classified(MirrorDriftClasses.ExternalEdit), Classified(MirrorDriftClasses.AddInPending), Classified(MirrorDriftClasses.Unknown), ignored)
        {
            NotComparable = others.Length,
            IgnoredMapKeys = committed.IgnoredMapKeys?.Count ?? 0,
        };
        var truncated = sorted.Length > options.MaxFindings || unmapped.Length > options.MaxFindings || others.Length > options.MaxFindings;
        return new MirrorDriftReport(committed.DocumentUid, committed.Project.Id, revision, options, summary,
            sorted.Take(options.MaxFindings).ToArray(), unmapped.Take(options.MaxFindings).ToArray(), truncated)
        {
            NotComparable = others.Take(options.MaxFindings).ToArray(),
            EvidenceNotes = notes.Take(options.MaxFindings).ToArray(),
        };
    }

    sealed record Context(IReadOnlyDictionary<Guid, string> NativeIds, IReadOnlyDictionary<Guid, string> Sources,
        IReadOnlyDictionary<Guid, string> CoreKinds, IReadOnlyDictionary<Guid, string> SnapshotKinds, MirrorDriftOptions Options);

    static string Metres(double value) => Fixed(value, 6);
    static string Joined(double[] values) => string.Join(",", values.Select(Metres));

    // Dependencies as UniqueIds split into the level ones and the rest, each a distinct set sorted ignoring case.
    static (string[] Levels, string[] Others) Roles(Guid[]? ids, Func<Guid, string> name, Func<Guid, string?> kind)
    {
        var levels = new List<string>();
        var others = new List<string>();
        foreach (var id in ids ?? Array.Empty<Guid>()) (kind(id) == "level" ? levels : others).Add(name(id));
        static string[] Set(List<string> values) => values.GroupBy(value => value.ToLowerInvariant(), StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => group.First()).ToArray();
        return (Set(levels), Set(others));
    }

    static IReadOnlyList<MirrorDriftField> Fields(Element core, Element revit, Context context)
    {
        var options = context.Options;
        var fields = new List<MirrorDriftField>();
        var a = core.GeometryMetres ?? Array.Empty<double>();
        var b = revit.GeometryMetres ?? Array.Empty<double>();
        if (!string.Equals(core.Kind, revit.Kind, StringComparison.Ordinal))
        {
            fields.Add(new("kind", core.Kind, revit.Kind, null));
            if (Joined(a) != Joined(b)) fields.Add(new("geometry", Joined(a), Joined(b), null));
        }
        else if (a.Length != b.Length || !FieldNames.TryGetValue(core.Kind, out var names) || names.Length != a.Length)
        {
            // Unknown layout: compare every value with the length tolerance, reported as one field.
            if (a.Length != b.Length || a.Zip(b).Any(pair => Math.Abs(pair.First - pair.Second) * 1000 > options.LengthToleranceMm + 1e-9))
                fields.Add(new("geometry", Joined(a), Joined(b), null));
        }
        else
        {
            for (var i = 0; i < a.Length; i++)
            {
                var deltaMm = (b[i] - a[i]) * 1000;
                if (Math.Abs(deltaMm) > options.LengthToleranceMm + 1e-9) fields.Add(new(names[i], Metres(a[i]), Metres(b[i]), Fixed(deltaMm, 3)));
            }
            if (AxisKinds.Contains(core.Kind) && Direction(a) is { } before && Direction(b) is { } after)
            {
                var delta = after - before;
                while (delta > 180) delta -= 360;
                while (delta <= -180) delta += 360;
                if (Math.Abs(delta) > options.AngleToleranceDeg + 1e-9) fields.Add(new("direction", Fixed(before, 4), Fixed(after, 4), Fixed(delta, 4)));
            }
        }
        var coreRoles = Roles(core.Dependencies, id => context.NativeIds.TryGetValue(id, out var uid) && !string.IsNullOrWhiteSpace(uid) ? uid : "core:" + id.ToString("D"),
            id => context.CoreKinds.TryGetValue(id, out var kind) ? kind : null);
        var revitRoles = Roles(revit.Dependencies, id => context.Sources.TryGetValue(id, out var uid) ? uid : "snapshot:" + id.ToString("D"),
            id => context.SnapshotKinds.TryGetValue(id, out var kind) ? kind : null);
        if (!ReviewFixes)
        {
            var x0 = string.Join(",", (core.Dependencies ?? Array.Empty<Guid>()).Select(id => context.NativeIds.TryGetValue(id, out var uid) ? uid : ""));
            var y0 = string.Join(",", (revit.Dependencies ?? Array.Empty<Guid>()).Select(id => context.Sources.TryGetValue(id, out var uid) ? uid : ""));
            if (x0 != y0) fields.Add(new("dependencies", x0, y0, null));
            return fields;
        }
        void Set(string field, string[] x, string[] y)
        {
            string Text(string[] values) => string.Join(",", values);
            if (!string.Equals(Text(x), Text(y), StringComparison.OrdinalIgnoreCase)) fields.Add(new(field, Text(x), Text(y), null));
        }
        // A Core space may list only its bounding walls (RoomCompiler); then its level is not compared.
        if (!(core.Kind == "space" && coreRoles.Levels.Length == 0)) Set("level", coreRoles.Levels, revitRoles.Levels);
        Set(core.Kind switch { "door" or "window" => "host", "space" => "bounding_walls", _ => "dependencies" }, coreRoles.Others, revitRoles.Others);
        return fields;
    }

    // Axis direction in degrees for [ax,ay,bx,by,..] layouts, or null for a zero-length axis.
    static double? Direction(double[] g)
    {
        double dx = g[2] - g[0], dy = g[3] - g[1];
        if (Math.Sqrt(dx * dx + dy * dy) < 1e-9) return null;
        return Math.Atan2(dy, dx) * 180 / Math.PI;
    }
}
