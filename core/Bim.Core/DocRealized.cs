using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
namespace Bim.Core;

// P4-13 realized map for catalogue-made elements. The owner -Catalogue session (scripts/owner-catalogue-steps.ps1) writes
// a run record (schema_version 1, steps[] of {index, group, key, command, request, response, passed, blocked, reason,
// ...}; response is null for a step never sent, else the command reply with status and created[] UniqueIds). DocRealized
// turns that record, a BIM-IR fixture and an assignment {"doc:<key>"|"element:<id D>": "<step key>"} into the realized
// map DocUpdate/DocReplace and `doc-update --realized` take: source -> the first UniqueId the assigned step created (the
// catalogue's own convention: "= primer elemento creado").
//
// Steps(record): reads the record strictly, only the members it needs (the writer's other members are ignored). Refused
// with DocRealizedException (an ArgumentException whose Message is the Code, plus Detail):
//   realized_bound_exceeded   record over MaxRecordBytes UTF-8 bytes (detail "record"), over MaxSteps steps ("steps"),
//                             a step over MaxCreated created UniqueIds ("created");
//   realized_record_invalid   not JSON ("json"); root not an object or with a duplicate member ("record"); schema_version
//                             not 1 ("schema_version"); steps missing, not an array or empty ("steps"); a step not an
//                             object or with a duplicate member ("steps[i]"); index not i+1 ("steps[i].index"); key not
//                             a non-empty string of at most MaxStepKeyChars ("steps[i].key"); passed / blocked not
//                             booleans or both true ("steps[i].passed" / "steps[i].blocked"); response missing, not null
//                             or an object, or with a duplicate member ("steps[i].response"); status not a string
//                             ("steps[i].response.status"); created missing or not an array ("steps[i].response.created");
//   realized_unique_id_invalid / realized_unique_id_duplicate   a created entry that is not a Revit UniqueId, or repeats one
//                             of the same step ignoring case (detail "steps[i].response.created[j]");
//   realized_step_duplicate   two steps with one key (detail the key).
// A step Succeeded when passed, not blocked and its reply status is "succeeded"; Created keeps the reply order.
// Assignments(json): a JSON object of non-empty string values (realized_assignments_invalid: "json", "assignments" when
// not an object, else the member name for a duplicate member or a value that is not a non-empty string); over
// MaxAssignments members or MaxRecordBytes bytes is realized_bound_exceeded "assignments".
// Build(fixture, steps, assignments): sources in ordinal order, each checked in this order (detail = the source unless
// said): realized_source_invalid (not "doc:<non-empty key>" nor "element:" + a Guid in lowercase D form, exactly as
// DocRegeneration.ElementSource writes it); realized_source_unknown (the element is not in the fixture, or the key is not
// in DocDefaults.From(fixture)); realized_step_unknown (no step with that key); realized_step_not_succeeded;
// realized_step_no_created; realized_unique_id_invalid (its first created entry is not a UniqueId);
// realized_unique_id_duplicate (that UniqueId, ignoring case, is already realized by an earlier source: two sources on
// one step, or two steps reporting one element). Duplicate step keys are realized_step_duplicate (detail the key), a
// null step realized_record_invalid "steps"; the MaxSteps / MaxAssignments bounds apply. Returns a read-only map sorted
// by source (ordinal). FromRecord = Build(fixture, Steps(record), Assignments(assignments)).
// Canonical(realized): the map as one compact JSON object sorted by key (ordinal), default System.Text.Json escaping:
// byte-identical for the same map whatever its order; refuses a key that is not a valid source (realized_source_invalid),
// a value that is not a UniqueId (realized_unique_id_invalid) or a UniqueId on two sources (realized_unique_id_duplicate),
// detail the key. Deterministic, vendor-free, BCL only; no BIM-IR or persisted format change.
public sealed record DocRealizedStep(string Key, bool Succeeded, IReadOnlyList<string> Created, IReadOnlyList<string> Deleted);
public sealed record DocRealizedRecord(string DocumentUniqueId, IReadOnlyList<DocRealizedStep> Steps);

public static class DocRealizedCodes
{
    public const string RecordInvalid = "realized_record_invalid";
    public const string AssignmentsInvalid = "realized_assignments_invalid";
    public const string BoundExceeded = "realized_bound_exceeded";
    public const string StepDuplicate = "realized_step_duplicate";
    public const string StepUnknown = "realized_step_unknown";
    public const string StepNotSucceeded = "realized_step_not_succeeded";
    public const string StepNoCreated = "realized_step_no_created";
    public const string SourceInvalid = "realized_source_invalid";
    public const string SourceUnknown = "realized_source_unknown";
    public const string UniqueIdInvalid = "realized_unique_id_invalid";
    public const string UniqueIdDuplicate = "realized_unique_id_duplicate";
    public const string UniqueIdDeleted = "realized_unique_id_deleted";
    public const string DocumentMismatch = "realized_document_mismatch";
    public const string FixtureInvalid = "realized_fixture_invalid";
}

public sealed class DocRealizedException(string code, string? detail = null) : ArgumentException(code)
{
    public string Code { get; } = code;
    public string? Detail { get; } = detail;
}

public static class DocRealized
{
    public const int MaxRecordBytes = 32 * 1024 * 1024;
    public const int MaxSteps = 1000;
    public const int MaxCreated = 10_000;
    public const int MaxAssignments = 100_000;
    public const int MaxStepKeyChars = 128;
    public const int MaxCanonicalBytes = 8 * 1024 * 1024;

    const string DocPrefix = "doc:";
    const string ElementPrefix = "element:";

    public static DocRealizedRecord Read(string record) => new("", Steps(record));

    public static IReadOnlyList<DocRealizedStep> Steps(string record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Length > MaxRecordBytes || Encoding.UTF8.GetByteCount(record) > MaxRecordBytes) throw Refuse(DocRealizedCodes.BoundExceeded, "record");
        using var document = Parse(record, DocRealizedCodes.RecordInvalid);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || HasDuplicate(root)) throw Refuse(DocRealizedCodes.RecordInvalid, "record");
        if (!root.TryGetProperty("schema_version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1)
            throw Refuse(DocRealizedCodes.RecordInvalid, "schema_version");
        if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) throw Refuse(DocRealizedCodes.RecordInvalid, "steps");
        var length = steps.GetArrayLength();
        if (length > MaxSteps) throw Refuse(DocRealizedCodes.BoundExceeded, "steps");
        if (length == 0) throw Refuse(DocRealizedCodes.RecordInvalid, "steps");

        var result = new List<DocRealizedStep>(length);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        foreach (var step in steps.EnumerateArray())
        {
            var at = "steps[" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
            if (step.ValueKind != JsonValueKind.Object || HasDuplicate(step)) throw Refuse(DocRealizedCodes.RecordInvalid, at);
            if (!step.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var value) || value != i + 1)
                throw Refuse(DocRealizedCodes.RecordInvalid, at + ".index");
            if (!step.TryGetProperty("key", out var keyElement) || keyElement.ValueKind != JsonValueKind.String || keyElement.GetString() is not { Length: > 0 and <= MaxStepKeyChars } key)
                throw Refuse(DocRealizedCodes.RecordInvalid, at + ".key");
            var passed = Boolean(step, "passed", at);
            var blocked = Boolean(step, "blocked", at);
            if (passed && blocked) throw Refuse(DocRealizedCodes.RecordInvalid, at + ".blocked");
            if (!step.TryGetProperty("response", out var response) || response.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object)
                || (response.ValueKind == JsonValueKind.Object && HasDuplicate(response)))
                throw Refuse(DocRealizedCodes.RecordInvalid, at + ".response");
            string? status = null;
            var created = new List<string>();
            if (response.ValueKind == JsonValueKind.Object)
            {
                if (!response.TryGetProperty("status", out var statusElement) || statusElement.ValueKind != JsonValueKind.String) throw Refuse(DocRealizedCodes.RecordInvalid, at + ".response.status");
                status = statusElement.GetString();
                if (!response.TryGetProperty("created", out var list) || list.ValueKind != JsonValueKind.Array) throw Refuse(DocRealizedCodes.RecordInvalid, at + ".response.created");
                if (list.GetArrayLength() > MaxCreated) throw Refuse(DocRealizedCodes.BoundExceeded, "created");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var j = 0;
                foreach (var entry in list.EnumerateArray())
                {
                    var where = at + ".response.created[" + j.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
                    if (entry.ValueKind != JsonValueKind.String || entry.GetString() is not { } uid || !RevitCommandProtocol.IsUniqueId(uid)) throw Refuse(DocRealizedCodes.UniqueIdInvalid, where);
                    if (!seen.Add(uid)) throw Refuse(DocRealizedCodes.UniqueIdDuplicate, where);
                    created.Add(uid);
                    j++;
                }
            }
            if (!keys.Add(key)) throw Refuse(DocRealizedCodes.StepDuplicate, key);
            result.Add(new DocRealizedStep(key, passed && !blocked && status == "succeeded", new ReadOnlyCollection<string>(created.ToArray()), Array.Empty<string>()));
            i++;
        }
        return new ReadOnlyCollection<DocRealizedStep>(result.ToArray());
    }

    public static IReadOnlyDictionary<string, string> Assignments(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxRecordBytes || Encoding.UTF8.GetByteCount(json) > MaxRecordBytes) throw Refuse(DocRealizedCodes.BoundExceeded, "assignments");
        using var document = Parse(json, DocRealizedCodes.AssignmentsInvalid);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw Refuse(DocRealizedCodes.AssignmentsInvalid, "assignments");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (map.Count == MaxAssignments) throw Refuse(DocRealizedCodes.BoundExceeded, "assignments");
            if (map.ContainsKey(property.Name) || property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not { Length: > 0 } step)
                throw Refuse(DocRealizedCodes.AssignmentsInvalid, property.Name);
            map[property.Name] = step;
        }
        return Sorted(map);
    }

    public static IReadOnlyDictionary<string, string> Build(Project fixture, IReadOnlyList<DocRealizedStep> steps, IReadOnlyDictionary<string, string> assignments)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(assignments);
        if (steps.Count > MaxSteps) throw Refuse(DocRealizedCodes.BoundExceeded, "steps");
        if (assignments.Count > MaxAssignments) throw Refuse(DocRealizedCodes.BoundExceeded, "assignments");
        var byKey = new Dictionary<string, DocRealizedStep>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (step?.Key is null || step.Created is null) throw Refuse(DocRealizedCodes.RecordInvalid, "steps");
            if (!byKey.TryAdd(step.Key, step)) throw Refuse(DocRealizedCodes.StepDuplicate, step.Key);
        }

        var elements = fixture.Elements.Select(element => DocRegeneration.ElementSource(element.Id)).ToHashSet(StringComparer.Ordinal);
        HashSet<string>? items = null;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (source, stepKey) in assignments.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!ValidSource(source)) throw Refuse(DocRealizedCodes.SourceInvalid, source);
            var known = source.StartsWith(ElementPrefix, StringComparison.Ordinal)
                ? elements.Contains(source)
                : (items ??= DocItemKeys(DocDefaults.From(fixture))).Contains(source[DocPrefix.Length..]);
            if (!known) throw Refuse(DocRealizedCodes.SourceUnknown, source);
            if (stepKey is null || !byKey.TryGetValue(stepKey, out var step)) throw Refuse(DocRealizedCodes.StepUnknown, source);
            if (!step.Succeeded) throw Refuse(DocRealizedCodes.StepNotSucceeded, source);
            if (step.Created.Count == 0) throw Refuse(DocRealizedCodes.StepNoCreated, source);
            var uid = step.Created[0];
            if (!RevitCommandProtocol.IsUniqueId(uid)) throw Refuse(DocRealizedCodes.UniqueIdInvalid, source);
            if (!used.Add(uid)) throw Refuse(DocRealizedCodes.UniqueIdDuplicate, source);
            map[source] = uid;
        }
        return Sorted(map);
    }

    public static IReadOnlyDictionary<string, string> FromRecord(Project fixture, string record, string assignments, string expectedDocumentUniqueId)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        return Build(fixture, Steps(record), Assignments(assignments));
    }

    public static string Canonical(IReadOnlyDictionary<string, string> realized)
    {
        ArgumentNullException.ThrowIfNull(realized);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (source, uid) in realized.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!ValidSource(source)) throw Refuse(DocRealizedCodes.SourceInvalid, source);
                if (!RevitCommandProtocol.IsUniqueId(uid)) throw Refuse(DocRealizedCodes.UniqueIdInvalid, source);
                if (!used.Add(uid)) throw Refuse(DocRealizedCodes.UniqueIdDuplicate, source);
                writer.WriteString(source, uid);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static bool ValidSource(string? source)
    {
        if (source is null) return false;
        if (source.StartsWith(DocPrefix, StringComparison.Ordinal)) return source.Length > DocPrefix.Length;
        if (!source.StartsWith(ElementPrefix, StringComparison.Ordinal)) return false;
        return Guid.TryParseExact(source[ElementPrefix.Length..], "D", out var id) && DocRegeneration.ElementSource(id) == source;
    }

    static HashSet<string> DocItemKeys(DocumentationSet set) =>
        set.Views.Select(item => item.Key).Concat(set.Tags.Select(item => item.Key)).Concat(set.Dimensions.Select(item => item.Key))
            .Concat(set.Schedules.Select(item => item.Key)).Concat(set.Sheets.Select(item => item.Key)).Concat(set.Placements.Select(item => item.Key))
            .ToHashSet(StringComparer.Ordinal);

    static ReadOnlyDictionary<string, string> Sorted(Dictionary<string, string> map)
    {
        var sorted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in map.OrderBy(pair => pair.Key, StringComparer.Ordinal)) sorted[pair.Key] = pair.Value;
        return new ReadOnlyDictionary<string, string>(sorted);
    }

    static JsonDocument Parse(string text, string code)
    {
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { throw Refuse(code, "json"); }
    }

    static bool HasDuplicate(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!names.Add(property.Name)) return true;
        return false;
    }

    static bool Boolean(JsonElement step, string name, string at) =>
        step.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw Refuse(DocRealizedCodes.RecordInvalid, at + "." + name);

    static DocRealizedException Refuse(string code, string detail) => new(code, detail);
}
