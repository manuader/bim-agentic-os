using Bim.Cad;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Temporary probe: prints consumer digests of the existing DXF fixtures on origin/main.
internal static class ConsumerProbe
{
    private static JsonNode? Round(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                {
                    var copy = new JsonObject();
                    foreach (var pair in o) copy[pair.Key] = Round(pair.Value);
                    return copy;
                }
            case JsonArray a:
                {
                    var copy = new JsonArray();
                    foreach (var item in a) copy.Add(Round(item));
                    return copy;
                }
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                {
                    var rounded = Math.Round(v.GetValue<double>(), 6);
                    return JsonValue.Create(rounded == 0 ? 0.0 : rounded);
                }
            case null:
                return null;
            default:
                return node.DeepClone();
        }
    }

    private static string Digest(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Round(JsonNode.Parse(json))!.ToJsonString())))[..16];

    private static string Safe(Func<string> json)
    {
        try { return Digest(json()); }
        catch (CadDraftException error) { return "E-" + error.Code; }
        catch (DxfFormatException error) { return "E-" + error.Code; }
        catch (Exception error) { return "E-" + error.GetType().Name; }
    }

    public static void Run(Action<bool, string, string> check, string fixtures)
    {
        var projectId = Guid.Parse("6f0c6a52-4d3b-4a8e-9b51-2f6f9d0e7a11");
        var actual = new List<string>();
        foreach (var file in new[] { "blocks.dxf", "brief-plan.dxf", "draft-plan.dxf", "room-tags.dxf", "simple-plan.dxf", "walls.dxf" })
        {
            var d = DxfReader.ReadFile(Path.Combine(fixtures, file));
            var options = new CadToBimIrOptions(projectId) { MillimetresPerUnit = d.MetresPerUnit is null ? 1 : null };
            actual.Add(file + "=" + Safe(() => JsonSerializer.Serialize(CadToBimIr.FromDxf(d, options))) + "/" + Safe(() => JsonSerializer.Serialize(BriefExtract.FromDxf(d))));
        }
        check(false, "probe consumer digests", string.Join(" ", actual));
    }
}
