#nullable enable
using System.Text.Json;
using Bim.Cli;

namespace Bim.Mcp;

// P5-17: the read-only MCP tool generate_typology, the MCP face of the P7-14/P5-16 CLI verb `generate-typology
// request.json output.json` (interfaces/cli/GenerateTypologyCommand.cs). It has no request parser or pipeline call of its
// own: it runs GenerateTypologyCommand.Document, compiled into this server from the CLI sources (Bim.Mcp.csproj links
// GenerateTypologyCommand.cs with its SkillPackLookup.cs, GltfExportCommand.cs and BoundedInput.cs), which reads the
// request file through BoundedInput and runs the verb's own Parse (strict members, weights, skills, precedence), pack
// loads and TypologyPipeline Run / RunComposed path, exactly as the verb does before it writes its file.
//   request_path  the generate-typology request JSON file, read like the CLI reads its request.json.
// Success: the text is the canonical TypologyPipelineSerializer document, whose UTF-8 bytes are exactly the file the CLI
// writes (ranked or infeasible; isError false). Nothing is written: there is no output path, so the CLI's
// generate_typology_usage and output_* refusals do not arise. Every other refusal is the CLI's stdout line without its
// newline, byte for byte (input_*, generate_typology_request_invalid, *_pack_unavailable / *_pack_invalid,
// generate_typology_skill_*, generate_typology_repository_not_found, generate_typology_refused with the typed issues,
// generate_typology_internal_error), with isError true.
// MCP-only refusal, checked before the request is read: a request_path that is blank, contains NUL, is longer than
// MaxPathChars, is rejected by Path.GetFullPath or is not valid text (a lone surrogate) is
// {"status":"failed","code":"generate_typology_path_invalid","detail":"request"} (the diff_intent_path_invalid rule).
// Packs are found from the server binary (SkillPackLookup.Load(id, packageFirst: true) on AppContext.BaseDirectory): the
// repository walk works for a build inside the repository; the release package root is recognised beside a cli/ or (P5-18)
// mcp/ binary directory, so the packaged mcp/ server reads <package>/skills like the packaged CLI, with no repository
// fallback while the package markers are present.
// Document writes nothing to Console.Out (only generate_typology_internal_error's detail goes to stderr, as on the CLI) and
// does not touch Environment.ExitCode, so no capture is needed.
internal static class GenerateTypologyTool
{
    public const string Name = "generate_typology";
    public const string RequestParameter = "request_path";
    public const string PathInvalid = "generate_typology_path_invalid";
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
        if (PathProblem(requestPath)) return (false, Failure(PathInvalid, "request"));
        return GenerateTypologyCommand.Document(requestPath);
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
