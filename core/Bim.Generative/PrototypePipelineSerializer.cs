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
// gives the same bytes, whatever the culture.
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
