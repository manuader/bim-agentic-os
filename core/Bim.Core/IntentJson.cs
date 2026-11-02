using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Core;

// Canonical layout intent v1 JSON.
// Parse is strict structure only: bounded size, depth and list lengths, no comments or trailing commas, unknown and
// duplicate members refused, JSON types and enumerations checked, text non-blank. Keys, references, ranges and
// geometry are IntentCompiler.Validate's.
// Serialize is canonical: compact, members in schema order, every list sorted by key (ordinal), a shared-wall pair
// sorted, numbers as shortest round-trip doubles with -0 written as 0. Serialize(Parse(Serialize(x))) == Serialize(x).
//
// {"intent_version":1,"provenance":{"source":s,"approval":s},
//  "levels":[{"key","name","elevation_m"}], "wall_types":[{"key","name","thickness_m","height_m"}],
//  "rooms":[{"key","name","level","wall_type","x_m","y_m","width_m","depth_m"}],
//  "openings":[{"key","kind":"door"|"window", ("room","side":"south"|"east"|"north"|"west") | ("between":[a,b]),
//               "offset_m","width_m","height_m","sill_m"}]}
public static class IntentJson
{
    public const int MaxInputChars = 1048576;
    public const int MaxDepth = 8;

    static readonly string[] RootMembers = { "intent_version", "provenance", "levels", "wall_types", "rooms", "openings" };
    static readonly string[] ProvenanceMembers = { "source", "approval" };
    static readonly string[] LevelMembers = { "key", "name", "elevation_m" };
    static readonly string[] WallTypeMembers = { "key", "name", "thickness_m", "height_m" };
    static readonly string[] RoomMembers = { "key", "name", "level", "wall_type", "x_m", "y_m", "width_m", "depth_m" };
    static readonly string[] OpeningMembers = { "key", "kind", "room", "side", "between", "offset_m", "width_m", "height_m", "sill_m" };

    public static string Side(IntentSide side) => side switch
    {
        IntentSide.South => "south",
        IntentSide.East => "east",
        IntentSide.North => "north",
        IntentSide.West => "west",
        _ => throw new ArgumentOutOfRangeException(nameof(side)),
    };

    public static string Kind(IntentOpeningKind kind) => kind switch
    {
        IntentOpeningKind.Door => "door",
        IntentOpeningKind.Window => "window",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static LayoutIntent Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxInputChars) throw new IntentException(IntentCodes.InputTooLarge, null, "at most 1048576 characters");
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth }); }
        catch (JsonException error) { throw new IntentException(IntentCodes.JsonInvalid, null, error.Message); }
        catch (ArgumentException error) { throw new IntentException(IntentCodes.JsonInvalid, null, error.Message); }
        catch (InvalidOperationException error) { throw new IntentException(IntentCodes.JsonInvalid, null, error.Message); }
        using (document)
        {
            try { return Root(document.RootElement); }
            catch (InvalidOperationException error) { throw new IntentException(IntentCodes.JsonInvalid, null, error.Message); }
        }
    }

    static LayoutIntent Root(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new IntentException(IntentCodes.TypeInvalid, null, "object required");
        // Version first, so a future version is reported as such rather than as unknown members.
        if (!root.TryGetProperty("intent_version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != LayoutIntent.CurrentVersion)
            throw new IntentException(IntentCodes.VersionUnsupported, "intent_version", "1 required");
        var members = Members(root, "", RootMembers);
        const string ProvenancePath = "provenance";
        var provenanceMembers = Members(Required(members, "", ProvenancePath), ProvenancePath, ProvenanceMembers);
        var provenance = new Provenance(Field(provenanceMembers, ProvenancePath, "source", Text), Field(provenanceMembers, ProvenancePath, "approval", Text));
        return new LayoutIntent(number, provenance,
            Items(Required(members, "", "levels"), "levels", LayoutIntent.MaxLevels, Level),
            Items(Required(members, "", "wall_types"), "wall_types", LayoutIntent.MaxWallTypes, WallType),
            Items(Required(members, "", "rooms"), "rooms", LayoutIntent.MaxRooms, Room),
            Items(Required(members, "", "openings"), "openings", LayoutIntent.MaxOpenings, Opening));
    }

    static IntentLevel Level(JsonElement element, string path)
    {
        var m = Members(element, path, LevelMembers);
        return new IntentLevel(Field(m, path, "key", Text), Field(m, path, "name", Text), Field(m, path, "elevation_m", Number));
    }

    static IntentWallType WallType(JsonElement element, string path)
    {
        var m = Members(element, path, WallTypeMembers);
        return new IntentWallType(Field(m, path, "key", Text), Field(m, path, "name", Text), Field(m, path, "thickness_m", Number), Field(m, path, "height_m", Number));
    }

    static IntentRoom Room(JsonElement element, string path)
    {
        var m = Members(element, path, RoomMembers);
        return new IntentRoom(Field(m, path, "key", Text), Field(m, path, "name", Text), Field(m, path, "level", Text), Field(m, path, "wall_type", Text),
            Field(m, path, "x_m", Number), Field(m, path, "y_m", Number), Field(m, path, "width_m", Number), Field(m, path, "depth_m", Number));
    }

    static IntentOpening Opening(JsonElement element, string path)
    {
        var m = Members(element, path, OpeningMembers);
        var key = Field(m, path, "key", Text);
        var kindPath = Join(path, "kind");
        var kind = Text(Required(m, path, "kind"), kindPath) switch
        {
            "door" => IntentOpeningKind.Door,
            "window" => IntentOpeningKind.Window,
            _ => throw new IntentException(IntentCodes.ValueInvalid, kindPath, "door or window"),
        };
        IntentHost host;
        if (m.TryGetValue("between", out var between))
        {
            if (m.ContainsKey("room") || m.ContainsKey("side")) throw new IntentException(IntentCodes.ValueInvalid, path, "either room and side, or between");
            var betweenPath = Join(path, "between");
            if (between.ValueKind != JsonValueKind.Array) throw new IntentException(IntentCodes.TypeInvalid, betweenPath, "array required");
            if (between.GetArrayLength() != 2) throw new IntentException(IntentCodes.ValueInvalid, betweenPath, "exactly two room keys");
            host = new IntentSharedWall(Text(between[0], betweenPath + "[0]"), Text(between[1], betweenPath + "[1]"));
        }
        else
        {
            var room = Field(m, path, "room", Text);
            var sidePath = Join(path, "side");
            var side = Text(Required(m, path, "side"), sidePath) switch
            {
                "south" => IntentSide.South,
                "east" => IntentSide.East,
                "north" => IntentSide.North,
                "west" => IntentSide.West,
                _ => throw new IntentException(IntentCodes.ValueInvalid, sidePath, "south, east, north or west"),
            };
            host = new IntentRoomSide(room, side);
        }
        return new IntentOpening(key, kind, host, Field(m, path, "offset_m", Number), Field(m, path, "width_m", Number),
            Field(m, path, "height_m", Number), Field(m, path, "sill_m", Number));
    }

    static T[] Items<T>(JsonElement element, string path, int max, Func<JsonElement, string, T> read)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new IntentException(IntentCodes.TypeInvalid, path, "array required");
        if (element.GetArrayLength() > max) throw new IntentException(IntentCodes.LimitExceeded, path, "at most " + max.ToString(CultureInfo.InvariantCulture) + " items");
        var items = new List<T>();
        foreach (var item in element.EnumerateArray()) items.Add(read(item, path + "[" + items.Count.ToString(CultureInfo.InvariantCulture) + "]"));
        return items.ToArray();
    }

    static Dictionary<string, JsonElement> Members(JsonElement element, string path, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new IntentException(IntentCodes.TypeInvalid, path.Length == 0 ? null : path, "object required");
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var at = Join(path, property.Name);
            if (members.ContainsKey(property.Name)) throw new IntentException(IntentCodes.MemberDuplicate, at, "duplicate member");
            if (Array.IndexOf(allowed, property.Name) < 0) throw new IntentException(IntentCodes.MemberUnknown, at, "unexpected member");
            members[property.Name] = property.Value;
        }
        return members;
    }

    static JsonElement Required(Dictionary<string, JsonElement> members, string path, string name) =>
        members.TryGetValue(name, out var value) ? value : throw new IntentException(IntentCodes.MemberMissing, Join(path, name), "required");

    static T Field<T>(Dictionary<string, JsonElement> members, string path, string name, Func<JsonElement, string, T> read) =>
        read(Required(members, path, name), Join(path, name));

    static string Join(string path, string name) => path.Length == 0 ? name : path + "." + name;

    static string Text(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String) throw new IntentException(IntentCodes.TypeInvalid, path, "string required");
        var text = element.GetString()!;
        if (!BriefParser.IsText(text)) throw new IntentException(IntentCodes.ValueInvalid, path, "non-blank string of at most 2048 UTF-16 code units");
        return text;
    }

    static double Number(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new IntentException(IntentCodes.TypeInvalid, path, "finite number required");
        // -0 and 0 are the same quantity; one representation keeps the canonical form unique.
        return number == 0 ? 0.0 : number;
    }

    public static string Serialize(LayoutIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(intent.Provenance);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("intent_version", intent.Version);
            writer.WriteStartObject("provenance");
            writer.WriteString("source", intent.Provenance.Source);
            writer.WriteString("approval", intent.Provenance.Approval);
            writer.WriteEndObject();
            writer.WriteStartArray("levels");
            foreach (var level in Sorted(intent.Levels, level => level.Key))
            {
                writer.WriteStartObject();
                writer.WriteString("key", level.Key);
                writer.WriteString("name", level.Name);
                WriteNumber(writer, "elevation_m", level.ElevationMetres);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("wall_types");
            foreach (var type in Sorted(intent.WallTypes, type => type.Key))
            {
                writer.WriteStartObject();
                writer.WriteString("key", type.Key);
                writer.WriteString("name", type.Name);
                WriteNumber(writer, "thickness_m", type.ThicknessMetres);
                WriteNumber(writer, "height_m", type.HeightMetres);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("rooms");
            foreach (var room in Sorted(intent.Rooms, room => room.Key))
            {
                writer.WriteStartObject();
                writer.WriteString("key", room.Key);
                writer.WriteString("name", room.Name);
                writer.WriteString("level", room.Level);
                writer.WriteString("wall_type", room.WallType);
                WriteNumber(writer, "x_m", room.XMetres);
                WriteNumber(writer, "y_m", room.YMetres);
                WriteNumber(writer, "width_m", room.WidthMetres);
                WriteNumber(writer, "depth_m", room.DepthMetres);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("openings");
            foreach (var opening in Sorted(intent.Openings, opening => opening.Key))
            {
                writer.WriteStartObject();
                writer.WriteString("key", opening.Key);
                writer.WriteString("kind", Kind(opening.Kind));
                WriteHost(writer, opening.Host);
                WriteNumber(writer, "offset_m", opening.OffsetMetres);
                WriteNumber(writer, "width_m", opening.WidthMetres);
                WriteNumber(writer, "height_m", opening.HeightMetres);
                WriteNumber(writer, "sill_m", opening.SillMetres);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void WriteHost(Utf8JsonWriter writer, IntentHost host)
    {
        if (host is IntentRoomSide side)
        {
            writer.WriteString("room", side.Room);
            writer.WriteString("side", Side(side.Side));
            return;
        }
        if (host is IntentSharedWall shared)
        {
            var ordered = string.CompareOrdinal(shared.RoomA, shared.RoomB) <= 0;
            writer.WriteStartArray("between");
            writer.WriteStringValue(ordered ? shared.RoomA : shared.RoomB);
            writer.WriteStringValue(ordered ? shared.RoomB : shared.RoomA);
            writer.WriteEndArray();
            return;
        }
        throw new ArgumentException("unsupported opening host", nameof(host));
    }

    // Ordinal by key; equal keys (an invalid intent) keep input order, so output is still well defined.
    static IEnumerable<T> Sorted<T>(IReadOnlyList<T> items, Func<T, string> key)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.OrderBy(key, StringComparer.Ordinal);
    }

    static void WriteNumber(Utf8JsonWriter writer, string name, double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("non-finite number in layout intent", nameof(value));
        writer.WriteNumber(name, value == 0 ? 0.0 : value);
    }
}
