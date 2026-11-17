#nullable enable
using System.Globalization;
using System.Text.Json;
using Bim.Cli;

namespace Bim.Mcp;

// P7-20: the read-only MCP tool plan_layouts, the MCP face of the P7-11/P7-13/P7-15 CLI verb `plan-layouts brief.json
// output.json [--alternatives n] [--seed s] [--pack id]` (interfaces/cli/PlanLayoutsCommand.cs). It has no brief parser,
// option parser, pack lookup or planner call of its own: it runs PlanLayoutsCommand.DryPlan, compiled into this server
// from the CLI sources (Bim.Mcp.csproj links PlanLayoutsCommand.cs with its SkillPackLookup.cs, GltfExportCommand.cs,
// IntentPlan.cs and BoundedInput.cs), which validates the option values with the verb's own helpers, reads the brief
// through BoundedInput, parses and validates it, loads the pack with SkillPackLookup.Load(id, packageFirst: true) and
// generates the set exactly as the verb does before it writes its file.
//   brief         the P6-03 brief JSON file, read like the CLI reads brief.json;
//   alternatives  optional integer (the CLI --alternatives, 1..64, default 4);
//   seed          optional integer (the CLI --seed, int32, default 0);
//   pack          optional skill pack id (the CLI --pack).
// The integers are passed to the verb's option validation as their invariant decimal text, so an out-of-range
// alternatives is the CLI's plan_layouts_alternatives_invalid with that text as detail. A JSON number that is not an int32
// (fraction, outside the range) is -32602, like any mistyped argument (known difference: the CLI types it as text).
// Success: the text is the canonical AlternativeSetSerializer JSON, whose UTF-8 bytes are exactly the file the CLI writes
// (ranked or infeasible; isError false). Nothing is written: there is no output path, so the CLI's plan_layouts_usage,
// option_unknown / duplicate / value_missing and output_* refusals do not arise. Every other refusal is the CLI's stdout
// line without its newline, byte for byte (plan_layouts_alternatives_invalid, plan_layouts_pack_id_invalid, input_*,
// plan_layouts_brief_invalid, plan_layouts_site_required, plan_layouts_pack_*, plan_layouts_refused,
// plan_layouts_internal_error with its detail on the server's stderr), with isError true, in the CLI precedence (options,
// brief read, parse, site, validation, pack, planning).
// MCP-only refusal, checked before anything else: a brief path that is blank, contains NUL, is longer than MaxPathChars,
// is rejected by Path.GetFullPath or is not valid text (a lone surrogate) is
// {"status":"failed","code":"plan_layouts_path_invalid","detail":"brief"} (the diff_intent_path_invalid rule). A pack
// string that is not valid text is passed on as the empty id (plan_layouts_pack_id_invalid with detail "").
// Packs are found from the server binary like generate_typology's (the release package beside mcp/, else the repository).
// DryPlan writes nothing to Console.Out and does not touch Environment.ExitCode, so no capture is needed.
internal static class PlanLayoutsTool
{
    public const string Name = "plan_layouts";
    public const string BriefParameter = "brief";
    public const string AlternativesParameter = "alternatives";
    public const string SeedParameter = "seed";
    public const string PackParameter = "pack";
    public const string PathInvalid = "plan_layouts_path_invalid";
    public const int MaxPathChars = 4096;

    // McpServer's handler: the arguments (checked present and typed by CallTool) to the shared path.
    public static (string Text, bool IsError) Execute(JsonElement arguments)
    {
        var alternatives = arguments.TryGetProperty(AlternativesParameter, out var count) ? count.GetInt32() : (int?)null;
        var seed = arguments.TryGetProperty(SeedParameter, out var start) ? start.GetInt32() : (int?)null;
        var pack = arguments.TryGetProperty(PackParameter, out var id) ? Text(id) ?? "" : null;
        var (succeeded, text) = Run(Text(arguments.GetProperty(BriefParameter)) ?? "", alternatives, seed, pack);
        return (text, !succeeded);
    }

    public static (bool Succeeded, string Text) Run(string briefPath, int? alternatives, int? seed, string? pack)
    {
        ArgumentNullException.ThrowIfNull(briefPath);
        if (PathProblem(briefPath)) return (false, Failure(PathInvalid, BriefParameter));
        return PlanLayoutsCommand.DryPlan(briefPath, alternatives?.ToString(CultureInfo.InvariantCulture), seed?.ToString(CultureInfo.InvariantCulture), pack);
    }

    static bool PathProblem(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || path.Length > MaxPathChars) return true;
        try { Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return true; }
        return false;
    }

    static string Failure(string code, string detail)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("status", "failed");
            writer.WriteString("code", code);
            writer.WriteString("detail", detail);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    static string? Text(JsonElement element)
    {
        try { return element.GetString(); }
        catch (InvalidOperationException) { return null; }
    }
}
