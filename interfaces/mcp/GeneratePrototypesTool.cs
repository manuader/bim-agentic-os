#nullable enable
using System.Text.Json;
using Bim.Cli;

namespace Bim.Mcp;

// P7-21: the read-only MCP tool generate_prototypes, the MCP face of the P7-09/P7-12 CLI verb `generate-prototypes
// request.json output.json` (interfaces/cli/GenerateCommand.cs). It has no request parser, project id derivation, pack
// lookup or pipeline call of its own: it runs GenerateCommand.Document, compiled into this server from the CLI sources
// (Bim.Mcp.csproj links GenerateCommand.cs with its SkillPackLookup.cs, OutputProblem.cs, IntentPlan.cs and
// BoundedInput.cs), which reads the request file through BoundedInput and runs the verb's own Parse (strict members, int32
// literals, finite numbers, lone surrogates refused), deterministic default project id (UUIDv8 of the canonical request),
// pack load (SkillPackLookup.Load(id, packageFirst: true)) and guarded PrototypePipeline.Run, exactly as the verb does
// before it writes its file.
//   request  the generate-prototypes request JSON file, read like the CLI reads its request.json.
// Success: the text is the canonical PrototypePipelineSerializer document, whose UTF-8 bytes are exactly the file the CLI
// writes (ranked or infeasible; isError false). Nothing is written: there is no output path (an output argument is
// -32602), so the CLI's generate_prototypes_usage and output_* refusals do not arise. Every other refusal is the CLI's
// stdout line without its newline, byte for byte (input_*, generate_prototypes_request_invalid,
// generate_prototypes_repository_not_found, generate_prototypes_pack_unavailable / _pack_invalid,
// generate_prototypes_refused with the library's typed refusals, generate_prototypes_internal_error with the IntentPlan
// diagnostic on the server's stderr), with isError true, in the CLI precedence (request read, parse, pack, pipeline).
// MCP-only refusal, checked before the request is read: a request path that is blank, contains NUL, is longer than
// MaxPathChars, is rejected by Path.GetFullPath or is not valid text (a lone surrogate) is
// {"status":"failed","code":"generate_prototypes_path_invalid","detail":"request"} (the diff_intent_path_invalid rule).
// Packs are found from the server binary like generate_typology's (the release package beside mcp/, else the repository).
// Document writes nothing to Console.Out and does not touch Environment.ExitCode, so no capture is needed.
internal static class GeneratePrototypesTool
{
    public const string Name = "generate_prototypes";
    public const string RequestParameter = "request";
    public const string PathInvalid = "generate_prototypes_path_invalid";
    public const int MaxPathChars = 4096;

    // McpServer's handler: the string argument (checked present and typed by CallTool) to the shared path.
    public static (string Text, bool IsError) Execute(JsonElement arguments)
    {
        var (succeeded, text) = Run(Text(arguments.GetProperty(RequestParameter)) ?? "");
        return (text, !succeeded);
    }

    public static (bool Succeeded, string Text) Run(string requestPath)
    {
        ArgumentNullException.ThrowIfNull(requestPath);
        if (PathProblem(requestPath)) return (false, Failure(PathInvalid, RequestParameter));
        return GenerateCommand.Document(requestPath);
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
