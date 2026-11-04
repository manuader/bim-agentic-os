using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bim.ParityCensus.Tests;

// One docs/features.json entry as the census rules need it.
public sealed record LedgerFeature(string Id, string Status, string Scope);

// Rules for capabilities/revit-parity-census.json (P9-04). Every refusal is "code:detail" so the self-tests can
// assert the exact rule that fired.
public static class ParityCensus
{
    // The repository's eight capability states, in this order (the census repeats them in "states").
    public static readonly string[] States = { "described", "compiled", "unit-tested", "integration-tested (ifc)", "verified-in-revit", "blocked", "unsupported", "manual-only" };

    // States that may stand without a catalogue command or a non-catalogue path.
    static readonly HashSet<string> PathlessStates = new(StringComparer.Ordinal) { "described", "blocked", "unsupported", "manual-only" };

    static readonly Regex IdPattern = new(@"\A[a-z0-9]+(\.[a-z0-9_]+)+\z", RegexOptions.CultureInvariant);

    // Evidence ladder: described < compiled < unit-tested < integration-tested (ifc) < verified-in-revit; -1 otherwise.
    public static int Rank(string state)
    {
        var index = Array.IndexOf(States, state);
        return index <= 4 ? index : -1;
    }

    public static List<string> Validate(JsonNode? census, IReadOnlyCollection<string> catalogue, IReadOnlyDictionary<string, LedgerFeature> ledger, Func<string, bool> evidenceExists)
    {
        var errors = new List<string>();
        if (census is not JsonObject root) { errors.Add("census_not_object"); return errors; }
        if (Int(root["schema_version"]) != 1) errors.Add("schema_version");
        var states = Strings(root["states"]);
        if (states is null || !states.SequenceEqual(States, StringComparer.Ordinal)) errors.Add("states_list");
        var areas = Strings(root["areas"]) ?? new List<string>();
        if (areas.Count == 0 || areas.Distinct(StringComparer.Ordinal).Count() != areas.Count) errors.Add("areas_list");
        if (root["entries"] is not JsonArray entries || entries.Count == 0) { errors.Add("entries_missing"); return errors; }

        var catalogueSet = new HashSet<string>(catalogue, StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < entries.Count; index++)
        {
            var at = "entries[" + index.ToString(CultureInfo.InvariantCulture) + "]";
            if (entries[index] is not JsonObject entry) { errors.Add("entry_not_object:" + at); continue; }
            var id = Str(entry["id"]);
            if (id is null || !IdPattern.IsMatch(id)) errors.Add("id_invalid:" + at);
            else if (!ids.Add(id)) errors.Add("id_duplicate:" + id);
            var name = id ?? at;

            var area = Str(entry["area"]);
            if (area is null || !areas.Contains(area, StringComparer.Ordinal)) errors.Add("area_unknown:" + name);
            if (string.IsNullOrWhiteSpace(Str(entry["revit_function"]))) errors.Add("revit_function_missing:" + name);

            var commands = Strings(entry["commands"]);
            if (commands is null) { errors.Add("commands_missing:" + name); commands = new List<string>(); }
            if (commands.Distinct(StringComparer.Ordinal).Count() != commands.Count) errors.Add("command_duplicate:" + name);
            foreach (var command in commands)
            {
                if (catalogueSet.Contains(command)) covered.Add(command);
                else errors.Add("command_unknown:" + name + ":" + command);
            }

            var featureIds = Strings(entry["features"]);
            if (featureIds is null) { errors.Add("features_missing:" + name); featureIds = new List<string>(); }
            var features = new List<LedgerFeature>();
            foreach (var featureId in featureIds)
            {
                if (ledger.TryGetValue(featureId, out var feature)) features.Add(feature);
                else errors.Add("feature_unknown:" + name + ":" + featureId);
            }

            var evidence = Strings(entry["evidence"]);
            if (evidence is null) { errors.Add("evidence_missing:" + name); evidence = new List<string>(); }
            foreach (var path in evidence)
            {
                if (!SafeRelative(path) || !evidenceExists(path)) errors.Add("evidence_not_found:" + name + ":" + path);
            }

            string? via = null;
            if (entry.ContainsKey("via"))
            {
                via = Str(entry["via"]);
                if (string.IsNullOrWhiteSpace(via)) { errors.Add("via_invalid:" + name); via = null; }
            }
            string? scope = null;
            var hasScope = entry.ContainsKey("verified_scope");
            if (hasScope) scope = Str(entry["verified_scope"]);

            var state = Str(entry["state"]);
            if (state is null || Array.IndexOf(States, state) < 0) { errors.Add("state_invalid:" + name); continue; }

            if (state == "unsupported")
            {
                if (commands.Count > 0 || via is not null) errors.Add("unsupported_with_path:" + name);
            }
            else if (evidence.Count == 0) errors.Add("evidence_empty:" + name);
            if (commands.Count == 0 && via is null && !PathlessStates.Contains(state)) errors.Add("state_without_path:" + name);
            if (via is not null && commands.Count > 0) errors.Add("via_with_commands:" + name);
            if (via is not null && features.Count == 0) errors.Add("via_without_feature:" + name);

            var rank = Rank(state);
            var ranks = features.Select(feature => Rank(feature.Status)).Where(value => value >= 0).ToList();
            if (rank > 0 && ranks.Count > 0 && rank > ranks.Max()) errors.Add("state_exceeds_features:" + name);
            if (state == "verified-in-revit")
            {
                var verified = features.Where(feature => feature.Status == "verified-in-revit").ToList();
                if (verified.Count == 0) errors.Add("verified_without_feature:" + name);
                else if (string.IsNullOrWhiteSpace(scope) || !verified.Any(feature => feature.Scope.Contains(scope, StringComparison.Ordinal))) errors.Add("verified_scope_mismatch:" + name);
            }
            else if (hasScope) errors.Add("verified_scope_unexpected:" + name);
            if (state == "integration-tested (ifc)" && !features.Any(feature => feature.Status == state)) errors.Add("integration_without_feature:" + name);
            if (state == "unit-tested" && !evidence.Any(path => path.StartsWith("tests/", StringComparison.Ordinal))) errors.Add("unit_tested_without_test:" + name);
        }
        foreach (var command in catalogue)
        {
            if (!covered.Contains(command)) errors.Add("command_uncovered:" + command);
        }
        return errors;
    }

    // Per-area counts of entries in each state, in the census "areas" order, then all areas; LF line endings.
    public static string Summary(JsonObject census, int catalogueCount)
    {
        var areas = Strings(census["areas"]) ?? new List<string>();
        var entries = (census["entries"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        var referenced = entries.SelectMany(entry => Strings(entry["commands"]) ?? new List<string>()).Distinct(StringComparer.Ordinal).Count();
        var text = new StringBuilder();
        text.Append("# Revit parity census: coverage summary\n\n");
        text.Append("Generated from [revit-parity-census.json](revit-parity-census.json) by `dotnet run --project tests/parity-census -- --write-summary` and checked by the parity-census gate in scripts/verify.ps1. Do not edit by hand.\n\n");
        text.Append("| Area | " + string.Join(" | ", States) + " | total |\n");
        text.Append("|---|" + string.Concat(Enumerable.Repeat("---:|", States.Length + 1)) + "\n");
        foreach (var area in areas.Append("all"))
        {
            var inArea = entries.Where(entry => area == "all" || Str(entry["area"]) == area).ToList();
            var counts = States.Select(state => inArea.Count(entry => Str(entry["state"]) == state).ToString(CultureInfo.InvariantCulture));
            text.Append("| " + area + " | " + string.Join(" | ", counts) + " | " + inArea.Count.ToString(CultureInfo.InvariantCulture) + " |\n");
        }
        text.Append("\nEntries: " + entries.Count.ToString(CultureInfo.InvariantCulture) + ". Catalogue commands referenced: " + referenced.ToString(CultureInfo.InvariantCulture) + " of " + catalogueCount.ToString(CultureInfo.InvariantCulture) + ".\n");
        return text.ToString();
    }

    static bool SafeRelative(string path) =>
        path.Length > 0 && !Path.IsPathRooted(path) && path.IndexOf('\\') < 0 && path.IndexOf(':') < 0 && path.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..");

    public static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static int? Int(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    public static List<string>? Strings(JsonNode? node)
    {
        if (node is not JsonArray array) return null;
        var values = new List<string>();
        foreach (var item in array)
        {
            var text = Str(item);
            if (text is null) return null;
            values.Add(text);
        }
        return values;
    }
}
