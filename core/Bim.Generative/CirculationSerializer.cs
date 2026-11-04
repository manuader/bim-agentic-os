using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Generative;

// Canonical circulation v1 JSON, in the style of SpacePlanSerializer: compact, fixed member order, doors in model order
// (by level and room instance ids), routes by instance id, checks in model order, numbers as shortest round-trip
// invariant-culture doubles (-0 as 0, non-finite as null, so an unreachable route's length is null). The same model
// always gives the same bytes, whatever the current culture. Diagnostic output; not a persisted format.
public static class CirculationSerializer
{
    public static string Serialize(CirculationModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("circulation_version", CirculationModel.CurrentVersion);
            writer.WriteString("status", model.Status switch { CirculationStatus.Valid => "valid", CirculationStatus.Invalid => "invalid", _ => "refused" });
            writer.WriteString("brief", model.BriefName);
            writer.WriteNumber("seed", model.Seed);
            Number(writer, "door_width_m", model.DoorWidthMetres);
            if (model.EntranceEdge is { } edge) writer.WriteString("entrance_edge", CirculationAnalyzer.EdgeName(edge));
            else writer.WriteNull("entrance_edge");
            writer.WritePropertyName("entrance");
            if (model.Entrance is { } entrance) Door(writer, entrance);
            else writer.WriteNullValue();
            writer.WriteStartArray("doors");
            foreach (var door in model.Doors) Door(writer, door);
            writer.WriteEndArray();
            writer.WriteStartArray("routes");
            foreach (var route in model.Routes)
            {
                writer.WriteStartObject();
                writer.WriteString("instance", route.Instance);
                writer.WriteBoolean("bedroom", route.IsBedroom);
                writer.WriteBoolean("reachable", route.Reachable);
                writer.WriteBoolean("avoids_bedrooms", route.AvoidsBedrooms);
                Number(writer, "length_m", route.LengthMetres);
                writer.WriteStartArray("rooms");
                foreach (var room in route.Rooms) writer.WriteStringValue(room);
                writer.WriteEndArray();
                writer.WriteStartArray("doors");
                foreach (var door in route.Doors) writer.WriteStringValue(door);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("checks");
            foreach (var check in model.Checks)
            {
                writer.WriteStartObject();
                writer.WriteString("code", check.Code);
                writer.WriteString("subject", check.Subject);
                writer.WriteBoolean("hard", check.Hard);
                writer.WriteBoolean("satisfied", check.Satisfied);
                writer.WriteString("detail", check.Detail);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void Door(Utf8JsonWriter writer, CirculationDoor door)
    {
        writer.WriteStartObject();
        writer.WriteString("id", door.Id);
        writer.WriteString("from", door.From);
        writer.WriteString("to", door.To);
        writer.WriteNumber("level", door.Level);
        Number(writer, "x0_m", door.X0);
        Number(writer, "y0_m", door.Y0);
        Number(writer, "x1_m", door.X1);
        Number(writer, "y1_m", door.Y1);
        writer.WriteBoolean("used", door.Used);
        writer.WriteEndObject();
    }

    static void Number(Utf8JsonWriter writer, string name, double value)
    {
        writer.WritePropertyName(name);
        if (double.IsFinite(value)) writer.WriteRawValue(value == 0 ? "0" : value.ToString("R", CultureInfo.InvariantCulture));
        else writer.WriteNullValue();
    }
}
