using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Generative;

// Canonical space plan v1 JSON: compact, fixed member order, rooms and constraints in plan order, numbers rounded to
// 6 decimals and written as shortest round-trip invariant-culture doubles (-0 as 0, non-finite as null), default
// escaping. The same plan always serializes to the same bytes, whatever the current culture.
public static class SpacePlanSerializer
{
    public static string Serialize(SpacePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("plan_version", SpacePlan.CurrentVersion);
            writer.WriteString("status", plan.IsFeasible ? "feasible" : "infeasible");
            writer.WriteString("brief", plan.BriefName);
            writer.WriteNumber("seed", plan.Seed);
            writer.WriteNumber("levels", plan.Levels);
            if (plan.Footprint is { } footprint)
            {
                writer.WriteStartObject("footprint");
                Number(writer, "width_m", footprint.WidthMetres);
                Number(writer, "depth_m", footprint.DepthMetres);
                writer.WriteEndObject();
            }
            else writer.WriteNull("footprint");
            Number(writer, "door_width_m", plan.DoorWidthMetres);
            Number(writer, "score", plan.Score);
            writer.WriteStartArray("rooms");
            foreach (var room in plan.Rooms)
            {
                writer.WriteStartObject();
                writer.WriteString("instance", room.InstanceId);
                writer.WriteString("room", room.RoomId);
                writer.WriteString("name", room.Name);
                writer.WriteNumber("index", room.Instance);
                writer.WriteNumber("level", room.Level);
                Number(writer, "x_m", room.Rect.X);
                Number(writer, "y_m", room.Rect.Y);
                Number(writer, "width_m", room.Rect.Width);
                Number(writer, "depth_m", room.Rect.Depth);
                Number(writer, "area_m2", room.AreaSquareMetres);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("constraints");
            foreach (var constraint in plan.Constraints)
            {
                writer.WriteStartObject();
                writer.WriteString("code", constraint.Code);
                writer.WriteString("subject", constraint.Subject);
                writer.WriteBoolean("hard", constraint.Hard);
                writer.WriteBoolean("satisfied", constraint.Satisfied);
                writer.WriteString("detail", constraint.Detail);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // Shortest round-trip text of the value rounded to 6 decimals; shared with constraint details.
    internal static string Format(double value)
    {
        var rounded = Math.Round(value, 6, MidpointRounding.ToEven);
        return rounded == 0 ? "0" : rounded.ToString("R", CultureInfo.InvariantCulture);
    }

    static void Number(Utf8JsonWriter writer, string name, double value)
    {
        writer.WritePropertyName(name);
        if (double.IsFinite(value)) writer.WriteRawValue(Format(value));
        else writer.WriteNullValue();
    }
}
