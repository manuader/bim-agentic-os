using System.Text;
using System.Text.Json;
namespace Bim.Core;

// Canonical project brief v1 JSON: compact, members in schema order, absent optionals omitted, values without
// provenance written bare, numbers as shortest round-trip doubles with -0 written as 0, default escaping. Arrays keep
// their order. Serialize(Parse(Serialize(b))) equals Serialize(b). Non-finite numbers are refused; validate first.
public static class BriefSerializer
{
    public static string Serialize(ProjectBrief brief)
    {
        ArgumentNullException.ThrowIfNull(brief);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("brief_version", brief.Version);
            writer.WriteString("name", brief.Name);
            writer.WriteStartObject("constraints");
            var constraints = brief.Constraints;
            Write(writer, "jurisdiction", constraints.Jurisdiction, WriteText);
            if (constraints.Site is { } site)
            {
                writer.WriteStartObject("site");
                Write(writer, "width_m", site.WidthMetres, WriteNumber);
                Write(writer, "depth_m", site.DepthMetres, WriteNumber);
                writer.WriteEndObject();
            }
            Write(writer, "levels", constraints.Levels, WriteInteger);
            Write(writer, "total_area_budget_m2", constraints.TotalAreaBudgetSquareMetres, WriteNumber);
            writer.WriteEndObject();
            writer.WriteStartArray("rooms");
            foreach (var room in brief.Rooms)
            {
                writer.WriteStartObject();
                writer.WriteString("id", room.Id);
                writer.WriteString("name", room.Name);
                Write(writer, "count", room.Count, WriteInteger);
                Write(writer, "target_area_m2", room.TargetAreaSquareMetres, WriteNumber);
                Write(writer, "min_area_m2", room.MinAreaSquareMetres, WriteNumber);
                Write(writer, "max_area_m2", room.MaxAreaSquareMetres, WriteNumber);
                Write(writer, "preferred_level", room.PreferredLevel, WriteInteger);
                WriteProvenance(writer, room.Provenance);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("adjacencies");
            foreach (var adjacency in brief.Adjacencies)
            {
                writer.WriteStartObject();
                writer.WriteString("a", adjacency.A);
                writer.WriteString("b", adjacency.B);
                writer.WriteString("kind", Kind(adjacency.Kind));
                WriteProvenance(writer, adjacency.Provenance);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string Source(BriefSource source) => source switch
    {
        BriefSource.User => "user",
        BriefSource.Document => "document",
        BriefSource.Inferred => "inferred",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public static string Kind(BriefAdjacencyKind kind) => kind switch
    {
        BriefAdjacencyKind.Required => "required",
        BriefAdjacencyKind.Preferred => "preferred",
        BriefAdjacencyKind.Forbidden => "forbidden",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    static void Write<T>(Utf8JsonWriter writer, string name, BriefValue<T>? value, Action<Utf8JsonWriter, T> writeValue) where T : notnull
    {
        if (value is null) return;
        writer.WritePropertyName(name);
        if (value.Provenance is null) { writeValue(writer, value.Value); return; }
        writer.WriteStartObject();
        writer.WritePropertyName("value");
        writeValue(writer, value.Value);
        WriteProvenance(writer, value.Provenance);
        writer.WriteEndObject();
    }

    static void WriteProvenance(Utf8JsonWriter writer, BriefProvenance? provenance)
    {
        if (provenance is null) return;
        writer.WriteStartObject("provenance");
        writer.WriteString("source", Source(provenance.Source));
        writer.WritePropertyName("confidence");
        WriteNumber(writer, provenance.Confidence);
        if (provenance.Reference is not null) writer.WriteString("reference", provenance.Reference);
        if (provenance.Page is { } page) writer.WriteNumber("page", page);
        writer.WriteEndObject();
    }

    static void WriteText(Utf8JsonWriter writer, string value) => writer.WriteStringValue(value);

    static void WriteInteger(Utf8JsonWriter writer, int value) => writer.WriteNumberValue(value);

    static void WriteNumber(Utf8JsonWriter writer, double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("non-finite number in brief", nameof(value));
        writer.WriteNumberValue(value == 0 ? 0.0 : value);
    }
}
