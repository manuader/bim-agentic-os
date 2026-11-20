#nullable enable
using System.Text.Json;
using Bim.Cli;

namespace Bim.Mcp;

// P5-22: the read-only MCP tool skill_list, the MCP face of the P5-21 CLI verb `skill-list [--skills skills-directory]`
// (interfaces/cli/SkillListCommand.cs). It has no loader, registry or serializer call of its own: it passes its one
// optional argument skills (a skills directory; absent → the verb's package-first default, <package>/skills through
// SkillPackLookup, else <repository>/skills of the server binary) to the verb's shared handler SkillListCommand.List,
// compiled into this server from the CLI source (Bim.Mcp.csproj links SkillListCommand.cs; the verb is read-only, so no
// writer code comes with it). A relative skills path resolves against the server's working directory, as on the CLI.
// Success and every refusal: the text is the CLI stdout line without its newline, byte for byte (the envelope's Line),
// isError exactly when the CLI exits 1: skill_list_repository_not_found, the loader's SkillException unchanged
// (root_missing, json_invalid, ...), a jurisdiction sidecar refusal unchanged, skill_list_internal_error (its IntentPlan
// diagnostic on the server's stderr, as on the CLI).
// Usage-equivalent arguments are JSON-RPC -32602 (UsageMessage), never a tool result: what the verb's parser refuses as
// skill_list_usage (a blank or "--"-prefixed skills value), and a string that is not valid text (a lone surrogate escape,
// which no CLI argument can carry). Null, wrongly typed, unknown and (P10-31) repeated arguments are -32602 from
// McpServer.CallTool (skills given twice: "invalid_params: repeated argument skills", before this tool runs).
internal static class SkillListTool
{
    public const string Name = "skill_list";
    public const string SkillsParameter = "skills";
    public const string UsageMessage = "invalid_params: skill_list_usage: skills, when given, is one non-blank skills directory not starting with --";

    // McpServer's handler: the arguments (present ones checked typed by CallTool) to the verb's shared handler.
    public static (string Text, bool IsError) Execute(JsonElement arguments)
    {
        string? skills = null;
        if (arguments.TryGetProperty(SkillsParameter, out var value))
        {
            skills = Text(value);
            if (string.IsNullOrWhiteSpace(skills) || skills.StartsWith("--", StringComparison.Ordinal)) throw new McpInvalidParamsException(UsageMessage);
        }
        var envelope = SkillListCommand.List(skills);
        SkillListCommand.WriteDiagnostic(envelope); // P5-31: stderr written guarded by the verb (a throwing Console.Error is ignored)
        return (envelope.Line, !envelope.Succeeded);
    }

    static string? Text(JsonElement element)
    {
        try { return element.GetString(); }
        catch (InvalidOperationException) { return null; }
    }
}
