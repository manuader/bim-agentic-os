using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Generative;

// Canonical alternatives v1 JSON, in the style of SpacePlanSerializer: compact, fixed member order, alternatives in set
// order (ranked first, then the rest in generation order), criteria in their fixed order, numbers as shortest
// round-trip invariant-culture doubles (-0 as 0, non-finite as null). A ranked alternative embeds its canonical plan;
// duplicates and infeasible alternatives carry their reasons instead. The same set always gives the same bytes.
public static class AlternativeSetSerializer
{
    public static string Serialize(AlternativeSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("alternatives_version", AlternativeSet.CurrentVersion);
            writer.WriteString("status", set.Status switch { AlternativeSetStatus.Ranked => "ranked", AlternativeSetStatus.Infeasible => "infeasible", _ => "refused" });
            writer.WriteString("brief", set.BriefName);
            writer.WriteNumber("requested", set.Requested);
            writer.WriteStartObject("weights");
            Number(writer, "area_fit", set.Weights.AreaFit);
            Number(writer, "adjacency", set.Weights.Adjacency);
            Number(writer, "proportions", set.Weights.Proportions);
            Number(writer, "compactness", set.Weights.Compactness);
            Number(writer, "perimeter", set.Weights.Perimeter);
            Number(writer, "compliance", set.Weights.Compliance);
            writer.WriteEndObject();
            writer.WriteStartObject("summary");
            writer.WriteNumber("ranked", set.Alternatives.Count(alternative => alternative.Status == AlternativeStatus.Ranked));
            writer.WriteNumber("duplicates", set.Alternatives.Count(alternative => alternative.Status == AlternativeStatus.Duplicate));
            writer.WriteNumber("infeasible", set.Alternatives.Count(alternative => alternative.Status == AlternativeStatus.Infeasible));
            writer.WriteEndObject();
            writer.WriteStartArray("refusals");
            foreach (var refusal in set.Refusals)
            {
                writer.WriteStartObject();
                writer.WriteString("code", refusal.Code);
                writer.WriteString("subject", refusal.Subject);
                writer.WriteString("detail", refusal.Detail);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("alternatives");
            foreach (var alternative in set.Alternatives)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", alternative.Index);
                writer.WriteNumber("seed", alternative.Seed);
                writer.WriteString("status", alternative.Status switch { AlternativeStatus.Ranked => "ranked", AlternativeStatus.Duplicate => "duplicate", _ => "infeasible" });
                writer.WriteNumber("rank", alternative.Rank);
                Number(writer, "total", alternative.Total);
                if (alternative.DuplicateOfIndex is { } original) writer.WriteNumber("duplicate_of_index", original);
                else writer.WriteNull("duplicate_of_index");
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
                writer.WriteStartArray("reasons");
                foreach (var reason in alternative.Reasons) writer.WriteStringValue(reason);
                writer.WriteEndArray();
                if (alternative.Status == AlternativeStatus.Ranked)
                {
                    writer.WritePropertyName("plan");
                    writer.WriteRawValue(SpacePlanSerializer.Serialize(alternative.Plan));
                }
                else writer.WriteNull("plan");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void Number(Utf8JsonWriter writer, string name, double value)
    {
        writer.WritePropertyName(name);
        if (double.IsFinite(value)) writer.WriteRawValue(value == 0 ? "0" : value.ToString("R", CultureInfo.InvariantCulture));
        else writer.WriteNullValue();
    }
}
