#nullable enable
using System.Text.Json;
using Bim.Cli;

namespace Bim.Mcp;

// P4-20: the read-only MCP tool doc_update, the MCP face of the P4-08 CLI verb `doc-update --before b.json --after a.json
// --realized r.json --bindings bindings.json --output plan.json [--dependents dependents.json]`
// (interfaces/cli/DocUpdateCommand.cs). It has no reader, parser or plan call of its own: it runs DocUpdateCommand.DryPlan,
// compiled into this server from the CLI sources (Bim.Mcp.csproj links DocUpdateCommand.cs with DocUpdateProbe.cs,
// DocUpdateExecute.cs, GltfExportCommand.cs, IntentPlan.cs and BoundedInput.cs), which reads and parses the inputs through
// the verb's own ReadInputs (BoundedInput, strict members, duplicate keys refused, the entry bounds) and plans with
// DocUpdate.Plan behind the verb's library-code filter, exactly as the verb does before it writes its file.
//   before, after  Project JSON files; realized  the realized map file; bindings  the RevitBindings file;
//   dependents     optional dry-run dependents file (the CLI --dependents).
// Success: the text is JsonSerializer.Serialize(DocUpdate.Plan(...)), whose UTF-8 bytes are exactly the file the CLI writes
// (isError false). Nothing is written and nothing is sent: there is no output path, no --probe-dependents pipe, no --accept
// and no --execute (any such argument is -32602), so the CLI's doc_update_usage, output_* and probe refusals do not arise.
// Every other refusal is the CLI's plain-mode stdout line without its newline, byte for byte (input_* with the input named,
// doc_update_project_invalid, doc_update_realized_invalid, doc_update_bindings_invalid, doc_update_dependents_invalid,
// doc_update_input_bound_exceeded, the library codes such as realized_invalid or document_path_invalid with its field,
// doc_update_internal_error with its detail on the server's stderr), with isError true.
// MCP-only refusal, checked before any input is read (before, after, realized, bindings, then dependents): a path that is
// blank, contains NUL, is longer than MaxPathChars, is rejected by Path.GetFullPath or is not valid text (a lone surrogate)
// is {"status":"failed","code":"doc_update_path_invalid","detail":"<parameter>"} (the diff_intent_path_invalid rule).
// DryPlan writes nothing to Console.Out and does not touch Environment.ExitCode, so no capture is needed.
internal static class DocUpdateTool
{
    public const string Name = "doc_update";
    public const string PathInvalid = "doc_update_path_invalid";
    public const int MaxPathChars = 4096;
    public static readonly string[] Required = ["before", "after", "realized", "bindings"];
    public const string DependentsParameter = "dependents";

    // McpServer's handler: the string arguments (checked present and typed by CallTool) to the shared path.
    public static (string Text, bool IsError) Execute(JsonElement arguments)
    {
        var dependents = arguments.TryGetProperty(DependentsParameter, out var element) ? Text(element) ?? "" : null;
        var (succeeded, text) = Run(Text(arguments.GetProperty("before")) ?? "", Text(arguments.GetProperty("after")) ?? "", Text(arguments.GetProperty("realized")) ?? "",
            Text(arguments.GetProperty("bindings")) ?? "", dependents);
        return (text, !succeeded);
    }

    public static (bool Succeeded, string Text) Run(string before, string after, string realized, string bindings, string? dependents)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(realized);
        ArgumentNullException.ThrowIfNull(bindings);
        foreach (var (name, path) in new[] { ("before", before), ("after", after), ("realized", realized), ("bindings", bindings), (DependentsParameter, dependents) })
            if (path is not null && PathProblem(path)) return (false, Failure(PathInvalid, name));
        return DocUpdateCommand.DryPlan(before, after, realized, bindings, dependents);
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
