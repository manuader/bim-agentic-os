using System.Globalization;
using System.Text;
using System.Text.Json;
using Bim.Core;
namespace Bim.Generative;

// Canonical prototype v1 JSON, in the style of AlternativeSetSerializer: compact, fixed member order, approvals,
// refusals and candidates in request/generation order, numbers as shortest round-trip invariant-culture doubles (-0 as
// 0, non-finite as null). The brief is embedded as canonical brief v1 JSON and the alternatives as canonical
// alternatives v1 JSON (null when refused). The same result always gives the same bytes, whatever the culture.
public static class PrototypeSerializer
{
    public static string Serialize(PrototypeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var request = result.Request;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("prototype_version", PrototypeResult.CurrentVersion);
            writer.WriteString("status", result.Status switch { PrototypeStatus.Ranked => "ranked", PrototypeStatus.Infeasible => "infeasible", _ => "refused" });
            writer.WriteString("typology", PrototypeTemplates.Code(request.Typology));
            if (request.Lot is { } lot)
            {
                writer.WriteStartObject("lot");
                Number(writer, "width_m", lot.WidthMetres);
                Number(writer, "depth_m", lot.DepthMetres);
                writer.WriteEndObject();
            }
            else writer.WriteNull("lot");
            writer.WriteNumber("seed", request.Seed);
            writer.WriteNumber("requested", request.Alternatives);
            Number(writer, "door_width_m", request.DoorWidthMetres);
            writer.WriteStartArray("fixed_rooms");
            foreach (var room in request.FixedRooms ?? Array.Empty<FixedRoom>())
            {
                if (room is null) { writer.WriteNullValue(); continue; }
                writer.WriteStartObject();
                Text(writer, "room", room.RoomId);
                // A missing rectangle (refused as fixed_room_invalid) writes null coordinates.
                var rect = room.Rect;
                Number(writer, "x_m", rect?.X ?? double.NaN);
                Number(writer, "y_m", rect?.Y ?? double.NaN);
                Number(writer, "width_m", rect?.Width ?? double.NaN);
                Number(writer, "depth_m", rect?.Depth ?? double.NaN);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("fixed_walls");
            foreach (var wall in request.FixedWalls ?? Array.Empty<FixedWall>())
            {
                if (wall is null) { writer.WriteNullValue(); continue; }
                writer.WriteStartObject();
                Text(writer, "id", wall.Id);
                Number(writer, "x0_m", wall.X0);
                Number(writer, "y0_m", wall.Y0);
                Number(writer, "x1_m", wall.X1);
                Number(writer, "y1_m", wall.Y1);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("refusals");
            foreach (var refusal in result.Refusals)
            {
                writer.WriteStartObject();
                writer.WriteString("code", refusal.Code);
                writer.WriteString("subject", refusal.Subject);
                writer.WriteString("detail", refusal.Detail);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("candidates");
            foreach (var candidate in result.Candidates)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", candidate.Index);
                writer.WriteString("strategy", candidate.Strategy);
                writer.WriteNumber("seed", candidate.Seed);
                if (candidate.Region is { } region)
                {
                    writer.WriteStartObject("region");
                    Number(writer, "x_m", region.X);
                    Number(writer, "y_m", region.Y);
                    Number(writer, "width_m", region.Width);
                    Number(writer, "depth_m", region.Depth);
                    writer.WriteEndObject();
                }
                else writer.WriteNull("region");
                Text(writer, "variant", candidate.Variant);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("brief");
            if (result.Brief is { } brief) writer.WriteRawValue(BriefSerializer.Serialize(brief));
            else writer.WriteNullValue();
            writer.WritePropertyName("alternatives");
            if (result.Alternatives is { } set) writer.WriteRawValue(AlternativeSetSerializer.Serialize(set));
            else writer.WriteNullValue();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void Text(Utf8JsonWriter writer, string name, string? value)
    {
        // Text that is blank or holds a lone surrogate (refused as an id) is written as null, so serializing never throws.
        if (value is null || !BriefParser.IsText(value)) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    static void Number(Utf8JsonWriter writer, string name, double value)
    {
        writer.WritePropertyName(name);
        if (double.IsFinite(value)) writer.WriteRawValue(value == 0 ? "0" : value.ToString("R", CultureInfo.InvariantCulture));
        else writer.WriteNullValue();
    }
}
