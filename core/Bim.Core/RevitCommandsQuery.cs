using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Core;

// P1-02 generic element queries: query.elements (filter by category and/or level) and query.inspect (every readable
// parameter of one element). Read-only catalogue entries with the same envelope (protocol_version 1), schema subset
// and guards as the rest of the catalogue; no transaction and no receipt. Vendor-free like every RevitCommands*.cs
// file: the add-in collector (CommandsExecutorQuery.cs in the native add-in) reads Revit facts into the neutral
// records below, and RevitElementQueryJson (unit-tested) decides ordering, bounds, truncation and the reply JSON.

// Category: the text the caller gave (a friendly name or an OST_ name); BuiltInCategory: the Revit BuiltInCategory
// name it stands for. Level: a level name or UniqueId. Parameters: parameter names to read on each element (empty:
// the defaults of RevitElementQueryRules.DefaultParameters). Limit: most elements returned.
public sealed record ElementQueryArgs(string? Category, string? BuiltInCategory, string? Level, IReadOnlyList<string> Parameters, int Limit) : RevitCommandArgs;
public sealed record ElementInspectArgs(string UniqueId) : RevitCommandArgs;

public static class RevitElementQueryRules
{
    public const int MaxLimit = 5000;
    public const int DefaultLimit = 1000;
    public const int MaxParameterNames = 16;
    public const int MaxInspectParameters = 2000;
    // Longer string values (and display texts) are cut to this many characters and flagged value_truncated.
    public const int MaxStringChars = 1024;
    // Whole reply (UTF-8 bytes): rows (or inspected parameters) stop being added before it would be exceeded.
    public const int MaxReplyBytes = 4 * 1024 * 1024;

    // Friendly category names and the Revit BuiltInCategory each stands for. Any other category is given by its
    // BuiltInCategory name (OST_...).
    public static IReadOnlyList<(string Name, string BuiltInCategory)> Categories { get; } = new[]
    {
        ("wall", "OST_Walls"), ("door", "OST_Doors"), ("window", "OST_Windows"), ("floor", "OST_Floors"),
        ("ceiling", "OST_Ceilings"), ("roof", "OST_Roofs"), ("room", "OST_Rooms"), ("stair", "OST_Stairs"),
        ("railing", "OST_StairsRailing"), ("column", "OST_Columns"), ("structural_column", "OST_StructuralColumns"),
        ("structural_framing", "OST_StructuralFraming"), ("structural_foundation", "OST_StructuralFoundation"),
        ("furniture", "OST_Furniture"), ("casework", "OST_Casework"), ("plumbing_fixture", "OST_PlumbingFixtures"),
        ("lighting_fixture", "OST_LightingFixtures"), ("generic_model", "OST_GenericModel"),
        ("specialty_equipment", "OST_SpecialityEquipment"), ("mechanical_equipment", "OST_MechanicalEquipment"),
        ("curtain_panel", "OST_CurtainWallPanels"), ("curtain_mullion", "OST_CurtainWallMullions"),
        ("level", "OST_Levels"), ("grid", "OST_Grids"),
    };

    // BuiltInParameter names read when query.elements names no parameter: Mark and Comments.
    public static IReadOnlyList<string> DefaultParameters { get; } = new[] { "ALL_MODEL_MARK", "ALL_MODEL_INSTANCE_COMMENTS" };

    public static string BuiltInCategory(string category, string field)
    {
        if (category.StartsWith("OST_", StringComparison.Ordinal)) return category;
        foreach (var (name, builtIn) in Categories)
            if (name == category) return builtIn;
        throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field, "unknown category " + category);
    }

    // The element ids to read, deterministically: distinct, ascending, at most limit. Truncated when more matched.
    public static (IReadOnlyList<long> Selected, bool Truncated) Page(IEnumerable<long> matched, int limit)
    {
        if (limit < 1 || limit > MaxLimit) throw new ArgumentOutOfRangeException(nameof(limit));
        var ordered = matched.Distinct().OrderBy(id => id).ToArray();
        return (ordered.Take(limit).ToArray(), ordered.Length > limit);
    }
}

public static class RevitParameterStorage
{
    public const string String = "string";
    public const string Integer = "integer";
    public const string Double = "double";
    public const string ElementId = "element_id";
    public const string None = "none";
    public static IReadOnlyList<string> All { get; } = new[] { String, Integer, Double, ElementId, None };
}

// Typed units of double values. Lengths, areas, volumes and angles are always these; any other measurable value is in
// the document's display unit for it, named by its Revit unit type id. Values without a unit are Revit's raw number.
public static class RevitParameterUnits
{
    public const string Millimetres = "mm";
    public const string SquareMetres = "m2";
    public const string CubicMetres = "m3";
    public const string Degrees = "deg";
    // Any other spec: the add-in reports a measurable value in the document's display unit (named by its unit type
    // id) and anything else as Revit's raw number without a unit.
    public const string Display = "display";

    // Routes a parameter data type id (ForgeTypeId text: <vendor>.spec.aec:<name>-<version>) to its typed unit. The
    // trailing schema version is ignored; any other schema (sub-discipline specs such as spec.aec.structural, string
    // specs) or name is Display. Null for no spec (empty or null id). Core names no vendor, so the one-word vendor
    // namespace is matched by shape.
    static readonly System.Text.RegularExpressions.Regex AecSpec = new(@"\A[a-z]+\.spec\.aec:(?<name>[A-Za-z]+)(-[0-9]+(\.[0-9]+)*)?\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static string? ForSpec(string? specTypeId)
    {
        if (string.IsNullOrEmpty(specTypeId)) return null;
        var match = AecSpec.Match(specTypeId);
        return !match.Success ? Display : match.Groups["name"].Value switch
        {
            "length" => Millimetres,
            "area" => SquareMetres,
            "volume" => CubicMetres,
            "angle" => Degrees,
            _ => Display,
        };
    }
}

// Where a parameter was read: on the element itself or on its ElementType.
public static class RevitParameterSources
{
    public const string Instance = "instance";
    public const string Type = "type";
}

// One parameter as Revit reports it. Id: the parameter's ElementId value (negative for built-in parameters). BuiltIn:
// the BuiltInParameter name or null. Source: instance or type. Spec: the data type id (ForgeTypeId text) or null.
// Exactly the value member of StorageType is read when HasValue: Text, Integer, Number (in Unit, see
// RevitParameterUnits) or ElementIdValue with the referenced element's UniqueId (null when none). Display:
// AsValueString(), Revit's formatted text in the document's units, or null.
public sealed record ObservedParameter(string Name, long Id, string? BuiltIn, string Source, string StorageType, string? Spec, bool HasValue, bool ReadOnly, bool Shared)
{
    public string? Text { get; init; }
    public long? Integer { get; init; }
    public double? Number { get; init; }
    public string? Unit { get; init; }
    public long? ElementIdValue { get; init; }
    public string? ElementUid { get; init; }
    public string? Display { get; init; }
}

// A level or type reference: name and UniqueId.
public sealed record ObservedReference(string? Name, string UniqueId);

// One element. Category: Category.Name (localized); BuiltInCategory: its BuiltInCategory name. Level: Element.LevelId,
// else the level parameter of the element (null when it has none). Family/Type: its ElementType's family name and
// name, TypeUid its UniqueId. Parameters: what query.elements read; Missing: requested names the element (and its
// type) do not have. query.inspect reports its parameters separately and leaves both empty.
public sealed record ObservedQueryElement(long Id, string UniqueId, string? Category, string? BuiltInCategory)
{
    public ObservedReference? Level { get; init; }
    public string? FamilyName { get; init; }
    public string? TypeName { get; init; }
    public string? TypeUid { get; init; }
    public IReadOnlyList<ObservedParameter> Parameters { get; init; } = Array.Empty<ObservedParameter>();
    public IReadOnlyList<string> Missing { get; init; } = Array.Empty<string>();
}

// Reply JSON of both queries: members in a fixed order, elements by ascending id, inspect parameters by name, then
// instance before type, then id; strings cut at MaxStringChars; the whole reply within MaxReplyBytes.
public static class RevitElementQueryJson
{
    public const string TruncatedLimit = "limit";
    public const string TruncatedBytes = "bytes";

    // matched: how many elements passed the filter; truncatedByLimit: paging (RevitElementQueryRules.Page) cut them at
    // the limit. truncated is that or the byte budget; truncated_reason says which (bytes wins), null when neither.
    public static string Elements(ElementQueryArgs args, ObservedReference? level, int matched, bool truncatedByLimit, IEnumerable<ObservedQueryElement> rows)
    {
        var ordered = rows.OrderBy(row => row.Id).ToArray();
        if (ordered.Length > args.Limit) throw new ArgumentException("more rows than the limit", nameof(rows));
        if (ordered.Select(row => row.Id).Distinct().Count() != ordered.Length) throw new ArgumentException("duplicate element id", nameof(rows));
        if (matched < ordered.Length) throw new ArgumentOutOfRangeException(nameof(matched));
        void Reply(Utf8JsonWriter writer, int returned, bool truncated, string? reason, IReadOnlyList<byte[]> items)
        {
            writer.WriteStartObject();
            WriteNullable(writer, "category", args.Category);
            WriteNullable(writer, "built_in_category", args.BuiltInCategory);
            writer.WritePropertyName("level");
            WriteReference(writer, level);
            writer.WriteNumber("limit", args.Limit);
            writer.WriteNumber("matched", matched);
            writer.WriteNumber("returned", returned);
            writer.WriteBoolean("truncated", truncated);
            WriteNullable(writer, "truncated_reason", reason);
            writer.WriteStartArray("elements");
            foreach (var item in items) writer.WriteRawValue(item, skipInputValidation: true);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Bounded(ordered.Select(row => Bytes(writer =>
        {
            writer.WriteStartObject();
            WriteElement(writer, row);
            writer.WriteStartArray("parameters");
            foreach (var parameter in row.Parameters) WriteParameter(writer, parameter);
            writer.WriteEndArray();
            writer.WriteStartArray("missing_parameters");
            foreach (var name in row.Missing) writer.WriteStringValue(name);
            writer.WriteEndArray();
            writer.WriteEndObject();
        })), ordered.Length, truncatedByLimit, Reply);
    }

    public static IReadOnlyList<ObservedParameter> InspectOrder(IEnumerable<ObservedParameter> parameters) =>
        parameters.OrderBy(parameter => parameter.Name, StringComparer.Ordinal)
            .ThenBy(parameter => parameter.Source == RevitParameterSources.Instance ? 0 : 1)
            .ThenBy(parameter => parameter.Id).ToArray();

    public static string Inspect(ObservedQueryElement element, IEnumerable<ObservedParameter> parameters)
    {
        var ordered = InspectOrder(parameters);
        var candidates = ordered.Take(RevitElementQueryRules.MaxInspectParameters).ToArray();
        void Reply(Utf8JsonWriter writer, int returned, bool truncated, string? reason, IReadOnlyList<byte[]> items)
        {
            writer.WriteStartObject();
            writer.WriteStartObject("element");
            WriteElement(writer, element);
            writer.WriteEndObject();
            writer.WriteNumber("parameter_count", ordered.Count);
            writer.WriteNumber("returned", returned);
            writer.WriteBoolean("truncated", truncated);
            WriteNullable(writer, "truncated_reason", reason);
            writer.WriteStartArray("parameters");
            foreach (var item in items) writer.WriteRawValue(item, skipInputValidation: true);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Bounded(candidates.Select(parameter => Bytes(writer => WriteParameter(writer, parameter))), candidates.Length, ordered.Count > candidates.Length, Reply);
    }

    // Adds serialized items (lazily, in order) while the whole reply stays within MaxReplyBytes. The fixed part is
    // measured with the most items and the longer truncation members it can have, so the final reply never exceeds it.
    static string Bounded(IEnumerable<byte[]> items, int most, bool truncatedByLimit, Action<Utf8JsonWriter, int, bool, string?, IReadOnlyList<byte[]>> reply)
    {
        var used = Bytes(writer => reply(writer, most, false, TruncatedLimit, Array.Empty<byte[]>())).Length;
        var kept = new List<byte[]>();
        var cut = false;
        foreach (var item in items)
        {
            var added = item.Length + (kept.Count > 0 ? 1 : 0);
            if (used + added > RevitElementQueryRules.MaxReplyBytes) { cut = true; break; }
            used += added;
            kept.Add(item);
        }
        var reason = cut ? TruncatedBytes : truncatedByLimit ? TruncatedLimit : null;
        var bytes = Bytes(writer => reply(writer, kept.Count, cut || truncatedByLimit, reason, kept));
        if (bytes.Length > RevitElementQueryRules.MaxReplyBytes) throw new InvalidOperationException("reply over the byte budget");
        return Encoding.UTF8.GetString(bytes);
    }

    static byte[] Bytes(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) body(writer);
        return buffer.ToArray();
    }

    static void WriteElement(Utf8JsonWriter writer, ObservedQueryElement element)
    {
        writer.WriteNumber("id", element.Id);
        writer.WriteString("unique_id", element.UniqueId);
        WriteNullable(writer, "category", element.Category);
        WriteNullable(writer, "built_in_category", element.BuiltInCategory);
        writer.WritePropertyName("level");
        WriteReference(writer, element.Level);
        WriteNullable(writer, "family", element.FamilyName);
        WriteNullable(writer, "type", element.TypeName);
        WriteNullable(writer, "type_unique_id", element.TypeUid);
    }

    static void WriteReference(Utf8JsonWriter writer, ObservedReference? reference)
    {
        if (reference is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        WriteNullable(writer, "name", reference.Name);
        writer.WriteString("unique_id", reference.UniqueId);
        writer.WriteEndObject();
    }

    static void WriteParameter(Utf8JsonWriter writer, ObservedParameter parameter)
    {
        if (!RevitParameterStorage.All.Contains(parameter.StorageType, StringComparer.Ordinal)) throw new ArgumentException("unknown storage type " + parameter.StorageType, nameof(parameter));
        if (parameter.Source != RevitParameterSources.Instance && parameter.Source != RevitParameterSources.Type) throw new ArgumentException("unknown parameter source " + parameter.Source, nameof(parameter));
        var truncated = false;
        string? Cut(string? text)
        {
            if (text is null || text.Length <= RevitElementQueryRules.MaxStringChars) return text;
            truncated = true;
            // Never split a surrogate pair.
            var length = char.IsHighSurrogate(text[RevitElementQueryRules.MaxStringChars - 1]) ? RevitElementQueryRules.MaxStringChars - 1 : RevitElementQueryRules.MaxStringChars;
            return text[..length];
        }
        writer.WriteStartObject();
        writer.WriteString("name", parameter.Name);
        writer.WriteNumber("id", parameter.Id);
        WriteNullable(writer, "built_in", parameter.BuiltIn);
        writer.WriteString("source", parameter.Source);
        writer.WriteString("storage_type", parameter.StorageType);
        WriteNullable(writer, "spec", parameter.Spec);
        writer.WriteBoolean("has_value", parameter.HasValue);
        writer.WriteBoolean("read_only", parameter.ReadOnly);
        writer.WriteBoolean("shared", parameter.Shared);
        writer.WritePropertyName("value");
        string? unit = null, referenced = null;
        if (!parameter.HasValue) writer.WriteNullValue();
        else switch (parameter.StorageType)
        {
            case RevitParameterStorage.String: WriteNullableValue(writer, Cut(parameter.Text)); break;
            case RevitParameterStorage.Integer:
                if (parameter.Integer is { } integer) writer.WriteNumberValue(integer); else writer.WriteNullValue();
                break;
            case RevitParameterStorage.Double:
                if (parameter.Number is { } number && double.IsFinite(number))
                {
                    writer.WriteNumberValue(number == 0 ? 0.0 : number);
                    unit = parameter.Unit;
                }
                else writer.WriteNullValue();
                break;
            case RevitParameterStorage.ElementId:
                if (parameter.ElementIdValue is { } id) { writer.WriteNumberValue(id); referenced = parameter.ElementUid; }
                else writer.WriteNullValue();
                break;
            default: writer.WriteNullValue(); break;
        }
        WriteNullable(writer, "unit", unit);
        WriteNullable(writer, "value_unique_id", referenced);
        WriteNullable(writer, "display", Cut(parameter.Display));
        writer.WriteBoolean("value_truncated", truncated);
        writer.WriteEndObject();
    }

    static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    static void WriteNullableValue(Utf8JsonWriter writer, string? value)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of every part exists (the v1 schema helpers are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> ElementSpecs = new(BuildElements);

    // P1-02: the generic element queries, after query.snapshot.
    public static IReadOnlyList<RevitCommandSpec> Elements => ElementSpecs.Value;

    static IReadOnlyList<RevitCommandSpec> BuildElements()
    {
        var friendly = string.Join(",", RevitElementQueryRules.Categories.Select(category => "\"" + category.Name + "\""));
        var category = Fill("""{"description":"Element category: a common name, or any Revit BuiltInCategory name such as OST_Walls","oneOf":[{"type":"string","description":"Common category name","enum":[@0@]},{"type":"string","description":"Revit BuiltInCategory name","minLength":5,"maxLength":80,"pattern":"^OST_[A-Za-z][A-Za-z0-9_]*$"}]}""", friendly);
        var query = Fill("""{"type":"object","description":"Give category, level or both","properties":{"category":@0@,"level":@1@,"parameters":{"type":"array","description":"Parameter names (as shown in Revit) to read on each element, instance first then type; at most 16; default Mark and Comments","items":@2@,"minItems":1,"maxItems":@3@},"limit":{"type":"integer","description":"Most elements returned, 1 to 5000, lowest element ids first; default 1000; the reply says when more matched"}},"additionalProperties":false}""",
            category, Reference("Level: name or UniqueId from revit_query_levels; elements whose level (or level parameter) is this level"), Text("Parameter name", 256), RevitElementQueryRules.MaxParameterNames.ToString(CultureInfo.InvariantCulture));
        var inspect = Fill("""{"type":"object","properties":{"unique_id":@0@},"required":["unique_id"],"additionalProperties":false}""", UniqueIdSchema);
        return new[]
        {
            new RevitCommandSpec("query.elements", false, "List the elements of a category and/or on a level, lowest element id first and at most 5000, with element id, UniqueId, category, level, family and type name and a few parameters (Mark and Comments, or up to 16 named ones); the reply says when it was truncated. Read-only.", query, BindElementQuery),
            new RevitCommandSpec("query.inspect", false, "Inspect one element by UniqueId: every readable instance and type parameter with its storage type, value, typed unit (lengths in mm, areas in m2, volumes in m3, angles in degrees) and Revit's display text. Read-only.", inspect, args => new ElementInspectArgs(args.GetProperty("unique_id").GetString()!)),
        };
    }

    static RevitCommandArgs BindElementQuery(JsonElement args)
    {
        var category = args.TryGetProperty("category", out var categoryElement) ? categoryElement.GetString() : null;
        var level = args.TryGetProperty("level", out var levelElement) ? levelElement.GetString() : null;
        if (category is null && level is null) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args", "give category, level or both");
        var names = new List<string>();
        if (args.TryGetProperty("parameters", out var parameters))
        {
            var index = 0;
            foreach (var name in parameters.EnumerateArray().Select(item => item.GetString()!))
            {
                if (names.Contains(name, StringComparer.Ordinal)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.parameters[" + index + "]", "parameter " + name + " is listed twice");
                names.Add(name);
                index++;
            }
        }
        var limit = RevitElementQueryRules.DefaultLimit;
        if (args.TryGetProperty("limit", out var limitElement))
        {
            if (!limitElement.TryGetInt32(out limit) || limit < 1 || limit > RevitElementQueryRules.MaxLimit)
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.limit", "limit must be a whole number from 1 to " + RevitElementQueryRules.MaxLimit.ToString(CultureInfo.InvariantCulture));
        }
        return new ElementQueryArgs(category, category is null ? null : RevitElementQueryRules.BuiltInCategory(category, "args.category"), level, names, limit);
    }
}
