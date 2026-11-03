using System.Text;
using System.Text.Json;
namespace Bim.Cad;

// Canonical JSON for the drawing model (dxf_model_version 1) and wall pairs: compact, fixed member order, layers and
// blocks in model order (sorted by name), entities in file order, numbers as shortest round-trip invariant doubles
// (-0 as 0, non-finite as null). The same model serializes to the same bytes whatever the current culture.
public static class DxfJson
{
    public const int ModelVersion = 1;

    public static string Serialize(DxfDrawing drawing)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("dxf_model_version", ModelVersion);
            writer.WriteString("source", drawing.SourceFile);
            if (drawing.InsUnits is { } code) writer.WriteNumber("insunits", code); else writer.WriteNull("insunits");
            writer.WriteString("units", drawing.Units.ToString().ToLowerInvariant());
            Number(writer, "metres_per_unit", drawing.MetresPerUnit);
            writer.WriteStartArray("layers");
            foreach (var layer in drawing.Layers)
            {
                writer.WriteStartObject();
                writer.WriteString("name", layer.Name);
                writer.WriteNumber("color", layer.Color);
                writer.WriteString("linetype", layer.LineType);
                writer.WriteBoolean("frozen", layer.Frozen);
                writer.WriteBoolean("off", layer.Off);
                writer.WriteBoolean("declared", layer.Declared);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("blocks");
            foreach (var block in drawing.Blocks)
            {
                writer.WriteStartObject();
                writer.WriteString("name", block.Name);
                Point(writer, "base", block.BasePoint);
                writer.WriteString("handle", block.Handle);
                writer.WriteNumber("line", block.Line);
                writer.WriteStartArray("entities");
                foreach (var entity in block.Entities) Entity(writer, entity);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("entities");
            foreach (var entity in drawing.Entities) Entity(writer, entity);
            writer.WriteEndArray();
            writer.WriteStartArray("unsupported");
            foreach (var provenance in drawing.Unsupported)
            {
                writer.WriteStartObject();
                Provenance(writer, provenance);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("warnings");
            foreach (var warning in drawing.Warnings)
            {
                writer.WriteStartObject();
                writer.WriteString("code", warning.Code);
                writer.WriteNumber("line", warning.Line);
                writer.WriteString("message", warning.Message);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    public static string Serialize(IReadOnlyList<WallPair> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        return Write(writer =>
        {
            writer.WriteStartArray();
            foreach (var pair in pairs)
            {
                writer.WriteStartObject();
                Side(writer, "a", pair.A);
                Side(writer, "b", pair.B);
                Number(writer, "thickness", pair.Thickness);
                Number(writer, "overlap", pair.Overlap);
                Number(writer, "angle_deviation_deg", pair.AngleDeviationDegrees);
                Number(writer, "confidence", pair.Confidence);
                Point(writer, "centerline_start", pair.CenterlineStart);
                Point(writer, "centerline_end", pair.CenterlineEnd);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) body(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Side(Utf8JsonWriter writer, string name, WallSide side)
    {
        writer.WriteStartObject(name);
        Provenance(writer, side.Source);
        if (side.SegmentIndex is { } index) writer.WriteNumber("segment", index); else writer.WriteNull("segment");
        Point(writer, "start", side.Start);
        Point(writer, "end", side.End);
        writer.WriteEndObject();
    }

    private static void Provenance(Utf8JsonWriter writer, DxfProvenance p)
    {
        writer.WriteString("type", p.EntityType);
        writer.WriteString("handle", p.Handle);
        writer.WriteString("layer", p.Layer);
        writer.WriteNumber("line", p.Line);
        writer.WriteStartArray("insert_path");
        foreach (var handle in p.InsertPath) writer.WriteStringValue(handle);
        writer.WriteEndArray();
    }

    private static void Entity(Utf8JsonWriter writer, DxfEntity entity)
    {
        writer.WriteStartObject();
        Provenance(writer, entity.Provenance);
        switch (entity)
        {
            case DxfLine line:
                writer.WriteString("kind", "line");
                Point(writer, "start", line.Start);
                Point(writer, "end", line.End);
                break;
            case DxfArc arc:
                writer.WriteString("kind", "arc");
                Point(writer, "center", arc.Center);
                Number(writer, "radius", arc.Radius);
                Number(writer, "start_angle_deg", arc.StartAngleDegrees);
                Number(writer, "end_angle_deg", arc.EndAngleDegrees);
                break;
            case DxfCircle circle:
                writer.WriteString("kind", "circle");
                Point(writer, "center", circle.Center);
                Number(writer, "radius", circle.Radius);
                break;
            case DxfPolyline polyline:
                writer.WriteString("kind", "polyline");
                writer.WriteBoolean("closed", polyline.Closed);
                writer.WriteStartArray("vertices");
                foreach (var vertex in polyline.Vertices)
                {
                    writer.WriteStartArray();
                    Value(writer, vertex.Point.X);
                    Value(writer, vertex.Point.Y);
                    Value(writer, vertex.Bulge);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                break;
            case DxfText text:
                writer.WriteString("kind", text.IsMText ? "mtext" : "text");
                Point(writer, "insertion", text.Insertion);
                Number(writer, "height", text.Height);
                Number(writer, "rotation_deg", text.RotationDegrees);
                writer.WriteString("value", text.Value);
                writer.WriteString("plain_text", text.PlainText);
                break;
            case DxfInsert insert:
                writer.WriteString("kind", "insert");
                writer.WriteString("block", insert.BlockName);
                Point(writer, "insertion", insert.Insertion);
                Number(writer, "scale_x", insert.ScaleX);
                Number(writer, "scale_y", insert.ScaleY);
                Number(writer, "rotation_deg", insert.RotationDegrees);
                break;
            default:
                writer.WriteString("kind", "other");
                break;
        }
        writer.WriteEndObject();
    }

    private static void Point(Utf8JsonWriter writer, string name, Point2 point)
    {
        writer.WriteStartArray(name);
        Value(writer, point.X);
        Value(writer, point.Y);
        writer.WriteEndArray();
    }

    private static void Number(Utf8JsonWriter writer, string name, double? value)
    {
        writer.WritePropertyName(name);
        if (value is { } v) Value(writer, v); else writer.WriteNullValue();
    }

    private static void Value(Utf8JsonWriter writer, double value)
    {
        if (!double.IsFinite(value)) writer.WriteNullValue();
        else writer.WriteNumberValue(value == 0 ? 0.0 : value);
    }
}
