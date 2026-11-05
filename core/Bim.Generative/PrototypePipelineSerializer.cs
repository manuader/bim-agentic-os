using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Generative;

// Canonical prototype pipeline v1 JSON, in the style of the other generative serializers: compact, fixed member order,
// alternatives in result order (accepted by rank, then rejected in generation order), criteria in their fixed order,
// numbers as shortest round-trip invariant-culture doubles (-0 as 0, non-finite as null). The prototype result is
// embedded as canonical prototype v1 JSON (null when caller-supplied plans were ranked), each alternative's feasible
// plan, circulation model and facade layout as their canonical v1 JSON (null before their gate), and the hosted windows
// with their BIM-IR element and host ids. The BIM-IR project itself is not written (only its element count): it is the
// deterministic output of FacadeIrBuilder for the plan, layout and circulation written here. The same result always
// gives the same bytes, whatever the culture. The stage options (circulation, facade, BIM-IR, pack parameters) are
// written too, so runs that differ only in an option (for example the BIM-IR wall height) serialize differently. The
// P7-08 snap options are written (options.snap) only when the pre-pass is on, so P7-07 results keep their bytes.
public static class PrototypePipelineSerializer
{
    public static string Serialize(PrototypePipelineResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var options = result.Options;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("pipeline_version", PrototypePipelineResult.CurrentVersion);
            writer.WriteString("status", result.Status switch
            {
                PrototypePipelineStatus.Ranked => "ranked",
                PrototypePipelineStatus.Infeasible => "infeasible",
                _ => "refused",
            });
            writer.WriteString("pack", options.Pack?.Id ?? "");
            writer.WriteString("project_id", options.ProjectId.ToString("D", CultureInfo.InvariantCulture));
            writer.WriteNumber("revision", options.Revision);
            Number(writer, "circulation_weight", options.CirculationWeight);
            Options(writer, options);
            writer.WriteStartObject("summary");
            writer.WriteNumber("accepted", result.Alternatives.Count(alternative => alternative.Accepted));
            foreach (var gate in new[] { PrototypePipelineGate.Prototype, PrototypePipelineGate.Circulation, PrototypePipelineGate.Facade, PrototypePipelineGate.FacadeIr })
                writer.WriteNumber("rejected_at_" + PrototypePipelineCodes.GateName(gate), result.Alternatives.Count(alternative => alternative.RejectedAt == gate));
            writer.WriteEndObject();
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
            writer.WritePropertyName("prototype");
            if (result.Prototype is { } prototype) writer.WriteRawValue(PrototypeSerializer.Serialize(prototype));
            else writer.WriteNullValue();
            writer.WriteStartArray("alternatives");
            foreach (var alternative in result.Alternatives) Alternative(writer, alternative);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // The stage options as given (null members mean the stage defaults), so results of runs with different options never
    // serialize alike; pack parameter overrides in ordinal key order.
    static void Options(Utf8JsonWriter writer, PrototypePipelineOptions options)
    {
        writer.WriteStartObject("options");
        var circulation = options.Circulation ?? new CirculationOptions();
        writer.WriteStartObject("circulation");
        if (circulation.EntranceRoomId is { } room) writer.WriteString("entrance_room", room);
        else writer.WriteNull("entrance_room");
        writer.WritePropertyName("entrance_edges");
        Edges(writer, circulation.EntranceEdges);
        Texts(writer, "bedroom_patterns", circulation.BedroomPatterns);
        Texts(writer, "entrance_patterns", circulation.EntrancePatterns);
        Texts(writer, "pass_through_patterns", circulation.PassThroughPatterns);
        if (circulation.DoorWidthMetres is { } door) Number(writer, "door_width_m", door);
        else writer.WriteNull("door_width_m");
        writer.WriteEndObject();
        var facade = options.Facade ?? new FacadeOptions();
        writer.WriteStartObject("facade");
        Number(writer, "sill_height_m", facade.SillHeightMetres);
        Number(writer, "head_height_m", facade.HeadHeightMetres);
        Number(writer, "end_clearance_m", facade.EndClearanceMetres);
        Number(writer, "openable_fraction", facade.OpenableFraction);
        Number(writer, "min_window_width_m", facade.MinWindowWidthMetres);
        writer.WritePropertyName("opening_edges");
        Edges(writer, facade.OpeningEdges);
        writer.WriteEndObject();
        var ir = options.Ir ?? new FacadeIrOptions();
        writer.WriteStartObject("ir");
        Number(writer, "wall_thickness_m", ir.WallThicknessMetres);
        Number(writer, "wall_height_m", ir.WallHeightMetres);
        Number(writer, "entrance_clearance_m", ir.EntranceClearanceMetres);
        if (ir.Provenance is { } provenance)
        {
            writer.WriteStartObject("provenance");
            writer.WriteString("source", provenance.Source);
            writer.WriteString("approval", provenance.Approval);
            writer.WriteEndObject();
        }
        else writer.WriteNull("provenance");
        writer.WriteEndObject();
        writer.WriteStartObject("parameters");
        foreach (var parameter in (options.Parameters ?? new Dictionary<string, double>()).OrderBy(pair => pair.Key, StringComparer.Ordinal))
            Number(writer, parameter.Key, parameter.Value);
        writer.WriteEndObject();
        // P7-08: written only when the snap pre-pass is on, so P7-07 results keep their exact bytes.
        if (options.Snap is { } snap)
        {
            writer.WriteStartObject("snap");
            if (snap.Rules?.Rules is { } rules)
            {
                writer.WriteStartArray("rules");
                foreach (var rule in rules)
                {
                    writer.WriteStartObject();
                    if (rule?.RoomId is { } roomId) writer.WriteString("room_id", roomId);
                    else writer.WriteNull("room_id");
                    if (rule?.NamePattern is { } pattern) writer.WriteString("name_pattern", pattern);
                    else writer.WriteNull("name_pattern");
                    if (rule?.MinAreaSquareMetres is { } area) Number(writer, "min_area_m2", area);
                    else writer.WriteNull("min_area_m2");
                    if (rule?.MinSideMetres is { } side) Number(writer, "min_side_m", side);
                    else writer.WriteNull("min_side_m");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            else writer.WriteNull("rules");
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    static void Edges(Utf8JsonWriter writer, IReadOnlyList<LotEdge>? edges)
    {
        if (edges is null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray();
        foreach (var edge in edges) writer.WriteStringValue(Enum.IsDefined(edge) ? CirculationAnalyzer.EdgeName(edge) : ((int)edge).ToString(CultureInfo.InvariantCulture));
        writer.WriteEndArray();
    }

    static void Texts(Utf8JsonWriter writer, string name, IReadOnlyList<string>? values)
    {
        if (values is null) { writer.WriteNull(name); return; }
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    static void Alternative(Utf8JsonWriter writer, PrototypePipelineAlternative alternative)
    {
        writer.WriteStartObject();
        writer.WriteNumber("index", alternative.Index);
        writer.WriteNumber("seed", alternative.Seed);
        if (alternative.Strategy is { } strategy) writer.WriteString("strategy", strategy);
        else writer.WriteNull("strategy");
        writer.WriteBoolean("accepted", alternative.Accepted);
        if (alternative.RejectedAt is { } gate) writer.WriteString("rejected_at", PrototypePipelineCodes.GateName(gate));
        else writer.WriteNull("rejected_at");
        writer.WriteNumber("rank", alternative.Rank);
        Number(writer, "total", alternative.Total);
        writer.WriteStartArray("criteria");
        foreach (var criterion in alternative.Criteria)
        {
            writer.WriteStartObject();
            writer.WriteString("criterion", criterion.Criterion);
            Number(writer, "raw", criterion.Raw);
            Number(writer, "normalized", criterion.Normalized);
            Number(writer, "weight", criterion.Weight);
            Number(writer, "contribution", criterion.Contribution);
            writer.WriteString("detail", criterion.Detail);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("rejections");
        foreach (var rejection in alternative.Rejections)
        {
            writer.WriteStartObject();
            writer.WriteString("gate", PrototypePipelineCodes.GateName(rejection.Gate));
            writer.WriteString("code", rejection.Code);
            writer.WriteString("subject", rejection.Subject);
            writer.WriteString("detail", rejection.Detail);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("plan");
        if (alternative.Plan.IsFeasible) writer.WriteRawValue(SpacePlanSerializer.Serialize(alternative.Plan));
        else writer.WriteNullValue();
        writer.WritePropertyName("circulation");
        if (alternative.Circulation is { } circulation) writer.WriteRawValue(CirculationSerializer.Serialize(circulation));
        else writer.WriteNullValue();
        writer.WritePropertyName("facade");
        if (alternative.Facade is { } facade) writer.WriteRawValue(FacadeSerializer.Serialize(facade));
        else writer.WriteNullValue();
        writer.WriteStartArray("windows");
        foreach (var window in alternative.Ir?.Windows ?? Array.Empty<FacadeIrWindow>())
        {
            writer.WriteStartObject();
            writer.WriteString("id", window.Id);
            writer.WriteString("instance", window.Instance);
            writer.WriteNumber("level", window.Level);
            writer.WriteString("element_id", window.ElementId.ToString("D", CultureInfo.InvariantCulture));
            writer.WriteString("host_wall_id", window.HostWallId.ToString("D", CultureInfo.InvariantCulture));
            Number(writer, "offset_m", window.OffsetMetres);
            Number(writer, "width_m", window.WidthMetres);
            Number(writer, "sill_m", window.SillHeightMetres);
            Number(writer, "head_m", window.HeadHeightMetres);
            Number(writer, "shift_m", window.ShiftMetres);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (alternative.Project is { } project) writer.WriteNumber("element_count", project.Elements.Length);
        else writer.WriteNull("element_count");
        writer.WriteEndObject();
    }

    static void Number(Utf8JsonWriter writer, string name, double value)
    {
        writer.WritePropertyName(name);
        if (double.IsFinite(value)) writer.WriteRawValue(value == 0 ? "0" : value.ToString("R", CultureInfo.InvariantCulture));
        else writer.WriteNullValue();
    }
}
