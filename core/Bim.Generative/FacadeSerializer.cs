using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Generative;

// Canonical facade v1 JSON, in the style of CirculationSerializer: compact, fixed member order, rooms by instance id,
// windows in layout order, checks in layout order, numbers as shortest round-trip invariant-culture doubles (-0 as 0,
// non-finite as null), absent ratios as null. The same layout always gives the same bytes, whatever the current
// culture. Diagnostic output; not a persisted format.
public static class FacadeSerializer
{
    public static string Serialize(FacadeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("facade_version", FacadeLayout.CurrentVersion);
            writer.WriteString("status", layout.Status switch { FacadeStatus.Complete => "complete", FacadeStatus.Incomplete => "incomplete", _ => "refused" });
            writer.WriteString("brief", layout.BriefName);
            writer.WriteNumber("seed", layout.Seed);
            writer.WriteString("pack", layout.PackId);
            writer.WriteStartObject("options");
            Number(writer, "sill_height_m", layout.Options.SillHeightMetres);
            Number(writer, "head_height_m", layout.Options.HeadHeightMetres);
            Number(writer, "end_clearance_m", layout.Options.EndClearanceMetres);
            Number(writer, "openable_fraction", layout.Options.OpenableFraction);
            Number(writer, "min_window_width_m", layout.Options.MinWindowWidthMetres);
            writer.WriteStartArray("opening_edges");
            foreach (var edge in layout.Options.OpeningEdges ?? FacadeOptions.DefaultOpeningEdges) writer.WriteStringValue(CirculationAnalyzer.EdgeName(edge));
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteStartArray("rooms");
            foreach (var room in layout.Rooms)
            {
                writer.WriteStartObject();
                writer.WriteString("instance", room.Instance);
                writer.WriteString("name", room.Name);
                writer.WriteNumber("level", room.Level);
                writer.WriteString("status", room.Status switch { FacadeRoomStatus.Compliant => "compliant", FacadeRoomStatus.NotRequired => "not_required", _ => "failed" });
                if (room.Reason is { } reason) writer.WriteString("reason", reason);
                else writer.WriteNull("reason");
                Number(writer, "area_m2", room.AreaSquareMetres);
                Optional(writer, "illumination_ratio", room.IlluminationRatio);
                Optional(writer, "ventilation_ratio", room.VentilationRatio);
                Optional(writer, "required_window_area_m2", room.RequiredWindowAreaSquareMetres);
                Optional(writer, "required_ventilation_area_m2", room.RequiredVentilationAreaSquareMetres);
                Number(writer, "window_area_m2", room.WindowAreaSquareMetres);
                Number(writer, "ventilation_area_m2", room.VentilationAreaSquareMetres);
                Number(writer, "exterior_length_m", room.ExteriorLengthMetres);
                Strings(writer, "rules", room.RuleIds);
                writer.WriteString("detail", room.Detail);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("windows");
            foreach (var window in layout.Windows)
            {
                writer.WriteStartObject();
                writer.WriteString("id", window.Id);
                writer.WriteString("instance", window.Instance);
                writer.WriteNumber("level", window.Level);
                writer.WriteString("side", FacadePlanner.SideName(window.Side));
                Number(writer, "x0_m", window.X0);
                Number(writer, "y0_m", window.Y0);
                Number(writer, "x1_m", window.X1);
                Number(writer, "y1_m", window.Y1);
                Number(writer, "width_m", window.WidthMetres);
                Number(writer, "sill_height_m", window.SillHeightMetres);
                Number(writer, "head_height_m", window.HeadHeightMetres);
                Number(writer, "area_m2", window.AreaSquareMetres);
                Number(writer, "ventilation_area_m2", window.VentilationAreaSquareMetres);
                Strings(writer, "rules", window.RuleIds);
                writer.WriteBoolean("faces_internal_void", window.FacesInternalVoid);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("checks");
            foreach (var check in layout.Checks)
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

    static void Strings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    static void Optional(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number) Number(writer, name, number);
        else writer.WriteNull(name);
    }

    static void Number(Utf8JsonWriter writer, string name, double value)
    {
        writer.WritePropertyName(name);
        if (double.IsFinite(value)) writer.WriteRawValue(value == 0 ? "0" : value.ToString("R", CultureInfo.InvariantCulture));
        else writer.WriteNullValue();
    }
}
