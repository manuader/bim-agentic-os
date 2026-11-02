using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Bim.Core;

// Strict structural reader for project brief v1: bounded size and depth, no comments or trailing commas, unknown and
// duplicate members refused, JSON types and enumerations checked. Ranges and cross-references are BriefValidator's.
public static class BriefParser
{
    public const int MaxInputChars = 262144;
    public const int MaxDepth = 16;
    // Counted in UTF-16 code units: a character outside the Basic Multilingual Plane counts 2.
    public const int MaxStringChars = 2048;

    static readonly Regex RoomIdPattern = new(@"\A[a-z0-9][a-z0-9_-]{0,63}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    static readonly string[] RootMembers = { "brief_version", "name", "constraints", "rooms", "adjacencies" };
    static readonly string[] ConstraintMembers = { "jurisdiction", "site", "levels", "total_area_budget_m2" };
    static readonly string[] SiteMembers = { "width_m", "depth_m" };
    static readonly string[] RoomMembers = { "id", "name", "count", "target_area_m2", "min_area_m2", "max_area_m2", "preferred_level", "provenance" };
    static readonly string[] AdjacencyMembers = { "a", "b", "kind", "provenance" };
    static readonly string[] SourcedMembers = { "value", "provenance" };
    static readonly string[] ProvenanceMembers = { "source", "confidence", "reference", "page" };

    // Room ids: 1-64 of [a-z0-9_-], starting with a letter or digit.
    public static bool IsRoomId(string? value) => value is not null && RoomIdPattern.IsMatch(value);

    // The one text rule for names, jurisdiction and references: non-blank, at most MaxStringChars UTF-16 code units,
    // well-formed UTF-16 (no lone surrogate), so any accepted text can be serialized and read back.
    public static bool IsText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxStringChars) return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])) index++;
            else if (char.IsSurrogate(value[index])) return false;
        }
        return true;
    }

    public static ProjectBrief Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxInputChars) throw new BriefException(BriefCodes.InputTooLarge, null, "at most 262144 characters");
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth }); }
        catch (JsonException error) { throw new BriefException(BriefCodes.JsonInvalid, null, error.Message); }
        // Input that is not valid UTF-16 (a raw lone surrogate) cannot be transcoded.
        catch (ArgumentException error) { throw new BriefException(BriefCodes.JsonInvalid, null, error.Message); }
        catch (InvalidOperationException error) { throw new BriefException(BriefCodes.JsonInvalid, null, error.Message); }
        using (document)
        {
            // Strings that are not valid UTF-16 (a lone surrogate escape) cannot be read.
            try { return ParseRoot(document.RootElement); }
            catch (InvalidOperationException error) { throw new BriefException(BriefCodes.JsonInvalid, null, error.Message); }
        }
    }

    static ProjectBrief ParseRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new BriefException(BriefCodes.TypeInvalid, null, "object required");
        // The version is checked before members so a future version is reported as such, not as unknown members.
        if (!root.TryGetProperty("brief_version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != ProjectBrief.CurrentVersion)
            throw new BriefException(BriefCodes.VersionUnsupported, "brief_version", "1 required");
        var members = Members(root, "", RootMembers);
        var name = Text(Required(members, "", "name"), "name");
        var constraints = Constraints(Required(members, "", "constraints"), "constraints");
        var rooms = Items(Required(members, "", "rooms"), "rooms", Room);
        var adjacencies = members.TryGetValue("adjacencies", out var adjacencyElement)
            ? Items(adjacencyElement, "adjacencies", Adjacency)
            : Array.Empty<BriefAdjacency>();
        return new ProjectBrief(number, name, constraints, rooms, adjacencies);
    }

    static BriefConstraints Constraints(JsonElement element, string path)
    {
        var members = Members(element, path, ConstraintMembers);
        BriefSite? site = null;
        if (members.TryGetValue("site", out var siteElement))
        {
            var sitePath = Join(path, "site");
            var siteMembers = Members(siteElement, sitePath, SiteMembers);
            site = new BriefSite(Sourced(Required(siteMembers, sitePath, "width_m"), Join(sitePath, "width_m"), Number),
                Sourced(Required(siteMembers, sitePath, "depth_m"), Join(sitePath, "depth_m"), Number));
        }
        return new BriefConstraints(Optional(members, path, "jurisdiction", Text), site, Optional(members, path, "levels", Integer),
            Optional(members, path, "total_area_budget_m2", Number));
    }

    static BriefRoom Room(JsonElement element, string path)
    {
        var members = Members(element, path, RoomMembers);
        var idPath = Join(path, "id");
        var id = Text(Required(members, path, "id"), idPath);
        if (!IsRoomId(id)) throw new BriefException(BriefCodes.ValueInvalid, idPath, "1-64 of [a-z0-9_-], starting with a letter or digit");
        return new BriefRoom(id, Text(Required(members, path, "name"), Join(path, "name")),
            Optional(members, path, "count", Integer),
            Optional(members, path, "target_area_m2", Number),
            Optional(members, path, "min_area_m2", Number),
            Optional(members, path, "max_area_m2", Number),
            Optional(members, path, "preferred_level", Integer),
            members.TryGetValue("provenance", out var provenance) ? Provenance(provenance, Join(path, "provenance")) : null);
    }

    static BriefAdjacency Adjacency(JsonElement element, string path)
    {
        var members = Members(element, path, AdjacencyMembers);
        var a = Text(Required(members, path, "a"), Join(path, "a"));
        var b = Text(Required(members, path, "b"), Join(path, "b"));
        var kindPath = Join(path, "kind");
        var kind = Text(Required(members, path, "kind"), kindPath) switch
        {
            "required" => BriefAdjacencyKind.Required,
            "preferred" => BriefAdjacencyKind.Preferred,
            "forbidden" => BriefAdjacencyKind.Forbidden,
            _ => throw new BriefException(BriefCodes.ValueInvalid, kindPath, "required, preferred or forbidden"),
        };
        return new BriefAdjacency(a, b, kind, members.TryGetValue("provenance", out var provenance) ? Provenance(provenance, Join(path, "provenance")) : null);
    }

    static BriefProvenance Provenance(JsonElement element, string path)
    {
        var members = Members(element, path, ProvenanceMembers);
        var sourcePath = Join(path, "source");
        var source = Text(Required(members, path, "source"), sourcePath) switch
        {
            "user" => BriefSource.User,
            "document" => BriefSource.Document,
            "inferred" => BriefSource.Inferred,
            _ => throw new BriefException(BriefCodes.ValueInvalid, sourcePath, "user, document or inferred"),
        };
        var confidence = Number(Required(members, path, "confidence"), Join(path, "confidence"));
        string? reference = members.TryGetValue("reference", out var referenceElement) ? Text(referenceElement, Join(path, "reference")) : null;
        int? page = members.TryGetValue("page", out var pageElement) ? Integer(pageElement, Join(path, "page")) : null;
        return new BriefProvenance(source, confidence, reference, page);
    }

    // A bare value, or {"value": v, "provenance": {...}} with both members present.
    static BriefValue<T> Sourced<T>(JsonElement element, string path, Func<JsonElement, string, T> read) where T : notnull
    {
        if (element.ValueKind != JsonValueKind.Object) return new BriefValue<T>(read(element, path));
        var members = Members(element, path, SourcedMembers);
        var value = read(Required(members, path, "value"), Join(path, "value"));
        return new BriefValue<T>(value, Provenance(Required(members, path, "provenance"), Join(path, "provenance")));
    }

    static BriefValue<T>? Optional<T>(Dictionary<string, JsonElement> members, string path, string name, Func<JsonElement, string, T> read) where T : notnull =>
        members.TryGetValue(name, out var element) ? Sourced(element, Join(path, name), read) : null;

    static T[] Items<T>(JsonElement element, string path, Func<JsonElement, string, T> read)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new BriefException(BriefCodes.TypeInvalid, path, "array required");
        var items = new List<T>();
        foreach (var item in element.EnumerateArray()) items.Add(read(item, path + "[" + items.Count.ToString(CultureInfo.InvariantCulture) + "]"));
        return items.ToArray();
    }

    static Dictionary<string, JsonElement> Members(JsonElement element, string path, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new BriefException(BriefCodes.TypeInvalid, path.Length == 0 ? null : path, "object required");
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var at = Join(path, property.Name);
            if (members.ContainsKey(property.Name)) throw new BriefException(BriefCodes.MemberDuplicate, at, "duplicate member");
            if (Array.IndexOf(allowed, property.Name) < 0) throw new BriefException(BriefCodes.MemberUnknown, at, "unexpected member");
            members[property.Name] = property.Value;
        }
        return members;
    }

    static JsonElement Required(Dictionary<string, JsonElement> members, string path, string name) =>
        members.TryGetValue(name, out var value) ? value : throw new BriefException(BriefCodes.MemberMissing, Join(path, name), "required");

    static string Join(string path, string name) => path.Length == 0 ? name : path + "." + name;

    static string Text(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String) throw new BriefException(BriefCodes.TypeInvalid, path, "string required");
        var text = element.GetString()!;
        if (!IsText(text)) throw new BriefException(BriefCodes.ValueInvalid, path, "non-blank string of at most 2048 UTF-16 code units");
        return text;
    }

    static double Number(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new BriefException(BriefCodes.TypeInvalid, path, "finite number required");
        // -0 and 0 are the same quantity; keep one representation so the canonical form is unique.
        return number == 0 ? 0.0 : number;
    }

    static int Integer(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var number)) throw new BriefException(BriefCodes.TypeInvalid, path, "32-bit integer required");
        return number;
    }
}
