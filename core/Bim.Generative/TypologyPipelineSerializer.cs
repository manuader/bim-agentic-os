using System.Globalization;
using System.Text;
using System.Text.Json;
using Bim.Core;
namespace Bim.Generative;

// Canonical typology pipeline v1 JSON (P5-12), in the style of the other generative serializers: compact, fixed member
// order, numbers as shortest round-trip invariant-culture doubles (-0 as 0, non-finite as null). It writes the request
// (family size, lot, count, seed, requested rooms in their canonical sorted order, door width, ranking weights or null),
// the typology stage (its status, the canonical brief JSON, the candidates and the canonical alternative set JSON, null
// when refused) and the pipeline result as canonical prototype pipeline v1 JSON. P5-14: only when the result was planned
// with composed rules, a rules member follows the request: null, or the rule set (succeeded, the planner rules with room
// id, name pattern, minimum area and side and reference, and the traces with planner rule index, property, threshold,
// pack rule, parameter source and reference); without composed rules the bytes are the P5-12 ones. The same result always gives the same
// bytes, whatever the culture.
public static class TypologyPipelineSerializer
{
    public static string Serialize(TypologyPipelineResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var typology = result.Typology;
        var request = typology.Request;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("typology_pipeline_version", TypologyPipelineResult.CurrentVersion);
            writer.WriteString("status", Status(result.Status));
            writer.WriteString("pack", typology.Programme.PackId);
            writer.WriteStartObject("request");
            writer.WriteNumber("family_size", request.FamilySize);
            if (request.Lot is { } lot)
            {
                writer.WriteStartObject("lot");
                Number(writer, "width_m", lot.WidthMetres);
                Number(writer, "depth_m", lot.DepthMetres);
                writer.WriteEndObject();
            }
            else writer.WriteNull("lot");
            writer.WriteNumber("alternatives", request.Alternatives);
            writer.WriteNumber("seed", request.Seed);
            if (request.Include is { } include)
            {
                writer.WriteStartArray("include");
                foreach (var kind in include) writer.WriteStringValue(kind);
                writer.WriteEndArray();
            }
            else writer.WriteNull("include");
            Number(writer, "door_width_m", request.DoorWidthMetres);
            if (request.Weights is { } weights)
            {
                writer.WriteStartObject("weights");
                Number(writer, "area_fit", weights.AreaFit);
                Number(writer, "adjacency", weights.Adjacency);
                Number(writer, "proportions", weights.Proportions);
                Number(writer, "compactness", weights.Compactness);
                Number(writer, "perimeter", weights.Perimeter);
                Number(writer, "compliance", weights.Compliance);
                writer.WriteEndObject();
            }
            else writer.WriteNull("weights");
            writer.WriteEndObject();
            if (result.ComposedRules)
            {
                writer.WritePropertyName("rules");
                if (result.Rules is { } rules) Rules(writer, rules);
                else writer.WriteNullValue();
            }
            writer.WriteStartObject("typology");
            writer.WriteString("status", typology.Status switch
            {
                PrototypeStatus.Ranked => "ranked",
                PrototypeStatus.Infeasible => "infeasible",
                _ => "refused",
            });
            writer.WritePropertyName("brief");
            if (typology.Brief is { } brief) writer.WriteRawValue(BriefSerializer.Serialize(brief));
            else writer.WriteNullValue();
            writer.WriteStartArray("candidates");
            foreach (var candidate in typology.Candidates)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", candidate.Index);
                writer.WriteString("strategy", candidate.Strategy);
                writer.WriteNumber("seed", candidate.Seed);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("alternatives");
            if (typology.Alternatives is { } set) writer.WriteRawValue(AlternativeSetSerializer.Serialize(set));
            else writer.WriteNullValue();
            writer.WriteEndObject();
            writer.WritePropertyName("pipeline");
            writer.WriteRawValue(PrototypePipelineSerializer.Serialize(result.Pipeline));
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void Rules(Utf8JsonWriter writer, TypologyRuleSet set)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("succeeded", set.Succeeded);
        writer.WriteStartArray("planner_rules");
        foreach (var rule in set.Rules.Rules)
        {
            writer.WriteStartObject();
            Text(writer, "room_id", rule.RoomId);
            Text(writer, "name_pattern", rule.NamePattern);
            Number(writer, "min_area_m2", rule.MinAreaSquareMetres);
            Number(writer, "min_side_m", rule.MinSideMetres);
            Text(writer, "reference", rule.Reference);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("traces");
        foreach (var trace in set.Traces)
        {
            writer.WriteStartObject();
            writer.WriteNumber("planner_rule", trace.PlannerRuleIndex);
            writer.WriteString("property", trace.Property);
            Number(writer, "threshold", trace.Threshold);
            Text(writer, "rule", trace.Rule?.ToString());
            Text(writer, "threshold_source", trace.ThresholdSource?.ToString());
            Text(writer, "reference", trace.Reference);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    static void Text(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    static void Number(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number) Number(writer, name, number);
        else writer.WriteNull(name);
    }

    static string Status(PrototypePipelineStatus status) => status switch
    {
        PrototypePipelineStatus.Ranked => "ranked",
        PrototypePipelineStatus.Infeasible => "infeasible",
        _ => "refused",
    };

    static void Number(Utf8JsonWriter writer, string name, double value)
    {
        writer.WritePropertyName(name);
        if (double.IsFinite(value)) writer.WriteRawValue(value == 0 ? "0" : value.ToString("R", CultureInfo.InvariantCulture));
        else writer.WriteNullValue();
    }
}
