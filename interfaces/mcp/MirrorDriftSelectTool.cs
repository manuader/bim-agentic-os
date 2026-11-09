#nullable enable
using System.Globalization;
using System.Text.Json;
using Bim.Core;

namespace Bim.Mcp;

// P2-10: the one engine behind the CLI verb `mirror-drift-select store-directory snapshot.json mirror.json choices.json
// output.json` (interfaces/cli/MirrorDriftSelectCommand.cs) and the read-only MCP tool mirror_drift_select (McpServer).
// It runs the P2-08 analysis (MirrorDriftTool.Analyze: read-only store read, P2-04 MirrorDrift.Compare, P2-07
// MirrorDriftResolve.Plan; same store, snapshot and mirror formats and codes as mirror-drift) and then the P2-09
// MirrorDriftSelect.Select with the reviewer's choices. The success text is exactly MirrorDriftSelection.ToJson() (the
// canonical selection JSON: selection_version 1, applied false, requires_approval true, order restore_then_adopt).
// Nothing is applied: no store commit, no plan run, no pipe. The CLI writes the text to a new file; the tool returns it.
//
// choices (strict, unknown or duplicate members refused): {"choices":[{"proposal_id":"<UniqueId or core:<id>>",
// "action":"adopt"|"restore"}, ...]}; at most MaxChoices entries (drift_select_choices_too_large). The JSON shape is
// checked here (drift_select_request_invalid); the values (empty id, unknown action, unknown or duplicate proposal,
// unsupported side, incomplete Requires coupling) are Core's typed drift_select_* codes. The choices are read before the
// store, so a malformed or oversized choice list costs no analysis.
//
// Refusal: {"status":"failed","code":..[,"detail":..][,"proposal_id":..][,"missing":[..]]}: every mirror-drift code
// (MirrorDriftTool.Describe) plus drift_select_request_invalid, drift_select_choices_too_large and the
// MirrorDriftSelectCodes (proposal_id names the refused proposal; missing lists, for drift_select_requires_incomplete,
// the coupled proposals not chosen with the same action).
internal static class MirrorDriftSelectTool
{
    public const string RequestInvalid = "drift_select_request_invalid";
    public const string ChoicesTooLarge = "drift_select_choices_too_large";
    // A resolution has at most one proposal per finding, and a report at most MaxFindingsLimit findings.
    public const int MaxChoices = MirrorDriftOptions.MaxFindingsLimit;

    public sealed record Outcome(bool Succeeded, string Text, int Operations, int RestoreSteps);

    sealed class Refusal(string code, string? detail) : Exception(code)
    {
        public string Code { get; } = code;
        public string? Detail { get; } = detail;
    }

    public static Outcome Run(string storeDirectory, string snapshotJson, string mirrorJson, string choicesJson)
    {
        try
        {
            var choices = ParseChoices(choicesJson);
            var analysis = MirrorDriftTool.Analyze(storeDirectory, snapshotJson, mirrorJson);
            var selection = MirrorDriftSelect.Select(analysis.Resolution, choices);
            return new Outcome(true, selection.ToJson(), selection.Operations.Count, selection.RestorePlan.Steps.Count);
        }
        catch (Refusal refusal) { return new Outcome(false, MirrorDriftTool.Failure(refusal.Code, refusal.Detail), 0, 0); }
        catch (MirrorDriftSelectException error) { return new Outcome(false, Failure(error), 0, 0); }
        catch (Exception error) { return new Outcome(false, MirrorDriftTool.Describe(error), 0, 0); }
    }

    static string Failure(MirrorDriftSelectException error)
    {
        var message = error.Message;
        var detail = message.StartsWith(error.Code + " ", StringComparison.Ordinal) ? message[(error.Code.Length + 1)..] : null;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("status", "failed");
            writer.WriteString("code", error.Code);
            if (detail is not null) writer.WriteString("detail", detail);
            if (error.ProposalId is not null) writer.WriteString("proposal_id", error.ProposalId);
            if (error.Missing.Count > 0)
            {
                writer.WriteStartArray("missing");
                foreach (var key in error.Missing) writer.WriteStringValue(key);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    static IReadOnlyList<MirrorDriftChoice> ParseChoices(string json)
    {
        try
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new Refusal(RequestInvalid, "choices must be an object {\"choices\":[..]}");
            Unique(root, "");
            foreach (var member in root.EnumerateObject())
                if (member.Name != "choices") throw new Refusal(RequestInvalid, "unknown member " + member.Name);
            if (!root.TryGetProperty("choices", out var list) || list.ValueKind != JsonValueKind.Array) throw new Refusal(RequestInvalid, "choices must be an array");
            var count = list.GetArrayLength();
            if (count > MaxChoices)
                throw new Refusal(ChoicesTooLarge, count.ToString(CultureInfo.InvariantCulture) + " choices; at most " + MaxChoices.ToString(CultureInfo.InvariantCulture));
            var choices = new List<MirrorDriftChoice>(count);
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new Refusal(RequestInvalid, "choices[" + choices.Count.ToString(CultureInfo.InvariantCulture) + "] must be an object");
                Unique(item, "choices[].");
                foreach (var member in item.EnumerateObject())
                    if (member.Name != "proposal_id" && member.Name != "action") throw new Refusal(RequestInvalid, "unknown member choices[]." + member.Name);
                string Text(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? "" : throw new Refusal(RequestInvalid, "choices[" + choices.Count.ToString(CultureInfo.InvariantCulture) + "]." + name + " must be a string");
                choices.Add(new MirrorDriftChoice(Text("proposal_id"), Text("action")));
            }
            return choices;
        }
        catch (JsonException) { throw new Refusal(RequestInvalid, "choices is not JSON"); }
        catch (InvalidOperationException) { throw new Refusal(RequestInvalid, "choices value type"); }
    }

    // JsonDocument keeps duplicate members; a choice that names a member twice is ambiguous and refused.
    static void Unique(JsonElement element, string prefix)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in element.EnumerateObject())
            if (!seen.Add(member.Name)) throw new Refusal(RequestInvalid, "duplicate member " + prefix + member.Name);
    }
}
