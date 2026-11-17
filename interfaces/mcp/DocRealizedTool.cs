#nullable enable
using System.Text.Json;
using Bim.Cli;

namespace Bim.Mcp;

// P4-21: the read-only MCP tool doc_realized, the MCP face of the P4-15 CLI verb `doc-realized --record run.json --fixture
// ir.json --assignments a.json --document <UniqueId> --output realized.json` (interfaces/cli/DocRealizedCommand.cs). It
// has no reader, parser or builder call of its own: it runs DocRealizedCommand.DryRealized, compiled into this server from
// the CLI sources (Bim.Mcp.csproj links DocRealizedCommand.cs with GltfExportCommand.cs, IntentPlan.cs and
// BoundedInput.cs), which reads the inputs with the verb's bounds (record and assignments to DocRealized.MaxRecordBytes,
// the fixture to BoundedInput.MaxBytes), parses the fixture and builds the map with DocRealized.FromRecord and
// DocRealized.Canonical behind the verb's guarded catch, exactly as the verb does before it writes its file.
//   record        the owner -Catalogue run record file;  fixture  the BIM-IR Project file;
//   assignments   the {"doc:<key>"|"element:<id>": "<step key>"} file;  document  the Revit document UniqueId (not a path).
// Success: the text is DocRealized.Canonical(...), whose UTF-8 bytes are exactly the file the CLI writes (isError false).
// Nothing is written: there is no output path (an output argument is -32602), so the CLI's doc_realized_usage and output_*
// refusals do not arise. Every other refusal is the CLI's stdout line without its newline, byte for byte (input_* with the
// input named, realized_fixture_invalid, the library's realized_* codes with their detail, doc_realized_internal_error
// with the IntentPlan diagnostic on the server's stderr), with isError true, in the CLI precedence.
// MCP-only refusal, checked before any input is read (record, fixture, then assignments): a path that is blank, contains
// NUL, is longer than MaxPathChars, is rejected by Path.GetFullPath or is not valid text (a lone surrogate escape, passed
// on as the empty path) is {"status":"failed","code":"doc_realized_path_invalid","detail":"<parameter>"} (the
// diff_intent_path_invalid rule). The document is judged by the library as on the CLI; a document that is not valid text
// is passed on as the empty string (realized_document_mismatch, detail "expected").
// DryRealized writes nothing to Console.Out and does not touch Environment.ExitCode, so no capture is needed.
internal static class DocRealizedTool
{
    public const string Name = "doc_realized";
    public const string PathInvalid = "doc_realized_path_invalid";
    public const int MaxPathChars = 4096;
    public const string RecordParameter = "record";
    public const string FixtureParameter = "fixture";
    public const string AssignmentsParameter = "assignments";
    public const string DocumentParameter = "document";

    // McpServer's handler: the string arguments (checked present and typed by CallTool) to the shared path.
    public static (string Text, bool IsError) Execute(JsonElement arguments)
    {
        var (succeeded, text) = Run(Text(arguments.GetProperty(RecordParameter)) ?? "", Text(arguments.GetProperty(FixtureParameter)) ?? "",
            Text(arguments.GetProperty(AssignmentsParameter)) ?? "", Text(arguments.GetProperty(DocumentParameter)) ?? "");
        return (text, !succeeded);
    }

    public static (bool Succeeded, string Text) Run(string record, string fixture, string assignments, string document)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(document);
        foreach (var (name, path) in new[] { (RecordParameter, record), (FixtureParameter, fixture), (AssignmentsParameter, assignments) })
            if (PathProblem(path)) return (false, Failure(PathInvalid, name));
        return DocRealizedCommand.DryRealized(record, fixture, assignments, document);
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
