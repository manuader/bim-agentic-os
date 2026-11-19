#nullable enable
using System.Text.Json;
using Bim.Cli;

namespace Bim.Mcp;

// P10-27: the read-only MCP tool jurisdiction_resolve, the MCP face of the P10-21 CLI verb `jurisdiction-resolve --code c |
// --country cc --municipality m [--lock skills.lock.json] [--allow-stub]` (interfaces/cli/JurisdictionResolveCommand.cs).
// It has no registry, lock or selection call of its own: it turns its arguments into the verb's argument list (code →
// --code, country → --country, municipality → --municipality, lock_path → --lock, allow_stub true → --allow-stub; false
// or absent adds nothing) and runs the verb's whole handler JurisdictionResolveCommand.Resolve, compiled into this
// server from the CLI source (Bim.Mcp.csproj links JurisdictionResolveCommand.cs; the verb is read-only, so no writer
// code comes with it). The skills directory is the verb's (package-first through SkillPackLookup, else the repository
// of the server binary), and a relative lock_path resolves against the server's working directory, as on the CLI.
// Success and every refusal: the text is the CLI stdout line without its newline, byte for byte (the envelope's Line),
// isError exactly when the CLI exits 1: skills_lock_path_invalid (detail lock), jurisdiction_resolve_repository_not_found,
// the registry's load and selection issues unchanged, jurisdiction_resolve_internal_error (its IntentPlan diagnostic on
// the server's stderr, as on the CLI).
// Usage-equivalent arguments are JSON-RPC -32602 (UsageMessage), never a tool result: whatever the verb's own parser
// refuses as jurisdiction_resolve_usage (no selector, code with country or municipality, only one of country and
// municipality, a blank or "--"-prefixed value), and a string that is not valid text (a lone surrogate escape, which no
// CLI argument can carry), and an argument name given twice in the arguments object (the verb refuses a repeated option
// as usage; checked first, before any value is read). Null, wrongly typed and unknown arguments are -32602 from
// McpServer.CallTool.
// Precedence: CallTool's checks, then the duplicate-name and valid-text checks here, then the verb's handler. With the
// test seam JurisdictionResolveCommand.WorkProbe set (tests only), the handler's internal error precedes its usage
// refusal, since the probe runs before the verb parses its arguments; the duplicate and valid-text -32602 still come first.
internal static class JurisdictionResolveTool
{
    public const string Name = "jurisdiction_resolve";
    public const string CodeParameter = "code";
    public const string CountryParameter = "country";
    public const string MunicipalityParameter = "municipality";
    public const string LockParameter = "lock_path";
    public const string AllowStubParameter = "allow_stub";
    public const string UsageMessage = "invalid_params: jurisdiction_resolve_usage: give code, or country and municipality (not both forms); every string non-blank and not starting with --";

    static readonly (string Parameter, string Option)[] Options =
        [(CodeParameter, "--code"), (CountryParameter, "--country"), (MunicipalityParameter, "--municipality"), (LockParameter, "--lock")];

    // McpServer's handler: the arguments (present ones checked typed by CallTool) to the verb's handler.
    public static (string Text, bool IsError) Execute(JsonElement arguments)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
            if (!names.Add(property.Name)) throw new McpInvalidParamsException(UsageMessage);
        var args = new List<string> { JurisdictionResolveCommand.Verb };
        foreach (var (parameter, option) in Options)
        {
            if (!arguments.TryGetProperty(parameter, out var value)) continue;
            args.Add(option);
            args.Add(Text(value) ?? throw new McpInvalidParamsException(UsageMessage));
        }
        if (arguments.TryGetProperty(AllowStubParameter, out var allowStub) && allowStub.GetBoolean()) args.Add("--allow-stub");
        var envelope = JurisdictionResolveCommand.Resolve([.. args]);
        if (envelope == JurisdictionResolveCommand.UsageRefusal) throw new McpInvalidParamsException(UsageMessage);
        if (envelope.Diagnostic is { } detail) Console.Error.WriteLine(detail);
        return (envelope.Line, !envelope.Succeeded);
    }

    static string? Text(JsonElement element)
    {
        try { return element.GetString(); }
        catch (InvalidOperationException) { return null; }
    }
}
