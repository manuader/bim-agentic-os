#nullable enable
using System.Globalization;
using System.Text.Json;
using Bim.Cli;

namespace Bim.Mcp;

// P3-14: the read-only MCP tool diff_intent, the MCP face of the P3-12 CLI verb `diff-intent --project project.json
// --intent after.json` (interfaces/cli/DiffIntentCommand.cs). It has no plan logic of its own: it runs the verb's own
// code, DiffIntentCommand.Run, compiled into this server from the CLI sources (Bim.Mcp.csproj links
// DiffIntentCommand.cs, the P3-13 shared helper IntentPlan.cs and their ApplyEditsCommand.cs / EditsInput.cs /
// BoundedInput.cs), so the plan is built through IntentPlan (IntentJson.Parse, IntentEdits.FromStored,
// IncrementalDiff.Diff with IncrementalDiff.OrderOf placement) exactly as the CLI and apply-intent build it.
//   project_path  a Project JSON file (the head), read like the CLI reads --project.
//   intent_path   a canonical layout intent v1 file, read like the CLI reads --intent.
// The text is the CLI stdout line without its newline, byte for byte: the ExecutionPlan JSON on success, or the CLI's
// typed refusal envelope (diff_intent_usage, input_*, diff_intent_project_invalid, diff_intent_intent_invalid,
// diff_intent_intent_refused, invalid_ir, invalid_project_identity, edit_bound_exceeded, the diff's library codes,
// diff_intent_internal_error). isError is true exactly when the CLI would exit 1. A flag-shaped path ("--...") is
// diff_intent_usage, as on the CLI.
// MCP-only refusal, checked before the verb runs (project_path first, then intent_path): a path that is blank, contains
// NUL, is longer than MaxPathChars, is rejected by Path.GetFullPath or is not valid text (a lone surrogate) is
// {"status":"failed","code":"diff_intent_path_invalid","detail":"project"|"intent"} (the audit_verify_path_invalid and
// skills_lock_path_invalid rule). An exception escaping the verb (none is expected) reaches McpServer.CallTool, which prints the CLI top-level
// handler's {"status":"failed","code":message}.
// Read-only: the verb reads both files through BoundedInput and never writes, commits or opens a store; there is no
// apply_intent tool.
// The verb writes its line with Console.WriteLine and sets Environment.ExitCode. Run captures Console.Out into a buffer
// for the call and restores both Console.Out and the exit code afterwards, under a lock. Limit: Console.Out is
// process-wide, so this is correct only while one request runs at a time in the process. The stdio server handles one
// request at a time and the lock serializes callers of Run, but any other thread writing Console.Out during a call
// would have its output captured into the tool text (and would see the exit code set to 0). The stdio server writes JSON-RPC to its own stream (Program.cs),
// so nothing the verb prints reaches the protocol stream. Its stderr detail for diff_intent_internal_error goes to the
// server's stderr, as on the CLI.
internal static class DiffIntentTool
{
    public const string Name = "diff_intent";
    public const string ProjectParameter = "project_path";
    public const string IntentParameter = "intent_path";
    public const string PathInvalid = "diff_intent_path_invalid";
    public const int MaxPathChars = 4096;

    public sealed record Outcome(bool Succeeded, string Text);

    static readonly object Gate = new();

    // McpServer's handler: the two string arguments (checked present and typed by CallTool) to the verb.
    public static (string Text, bool IsError) Execute(JsonElement arguments)
    {
        var outcome = Run(Text(arguments.GetProperty(ProjectParameter)) ?? "", Text(arguments.GetProperty(IntentParameter)) ?? "");
        return (outcome.Text, !outcome.Succeeded);
    }

    public static Outcome Run(string projectPath, string intentPath)
    {
        ArgumentNullException.ThrowIfNull(projectPath);
        ArgumentNullException.ThrowIfNull(intentPath);
        if (PathProblem(projectPath)) return new Outcome(false, Failure(PathInvalid, "project"));
        if (PathProblem(intentPath)) return new Outcome(false, Failure(PathInvalid, "intent"));
        lock (Gate)
        {
            var previousOut = Console.Out;
            var previousExit = Environment.ExitCode;
            using var captured = new StringWriter(CultureInfo.InvariantCulture);
            int exit;
            try
            {
                Console.SetOut(captured);
                Environment.ExitCode = 0;
                DiffIntentCommand.Run([DiffIntentCommand.Verb, "--project", projectPath, "--intent", intentPath]);
                exit = Environment.ExitCode;
            }
            finally
            {
                Console.SetOut(previousOut);
                Environment.ExitCode = previousExit;
            }
            return new Outcome(exit == 0, Line(captured.ToString()));
        }
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

    // The single line the verb printed, without its line terminator.
    static string Line(string printed)
    {
        if (printed.EndsWith(Environment.NewLine, StringComparison.Ordinal)) return printed[..^Environment.NewLine.Length];
        if (printed.EndsWith('\n')) printed = printed[..^1];
        if (printed.EndsWith('\r')) printed = printed[..^1];
        return printed;
    }

    static string? Text(JsonElement element)
    {
        try { return element.GetString(); }
        catch (InvalidOperationException) { return null; }
    }
}
