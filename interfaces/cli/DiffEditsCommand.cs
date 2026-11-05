#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using Bim.Core;

namespace Bim.Cli;

// P3-07 `diff-edits --project project.json --edits edits.json` over the P3-05 incremental diff
// (core/Bim.Core/IncrementalDiff.cs). Read-only: it prints the ExecutionPlan of the project with the edit set applied
// and never writes the project, the edits or anything else (callers commit through `commit-intent` themselves).
//   project.json  a Project, parsed exactly as the `diff` verb parses it (default System.Text.Json options), so the
//                 printed plan is byte-identical to `diff project.json applied.json` and to
//                 JsonSerializer.Serialize(IncrementalDiff.Diff(project, edits).Plan).
//   edits.json    a JSON array of ElementEdit {"Action":"create"|"update"|"delete","Id":guid,"Element":Element|null}
//                 (the record's default serialization); unknown members are refused.
// Bounded input: both files go through BoundedInput.ReadText (8 MiB cap, regular files only, links refused; its input_*
// codes); at most IncrementalDiff.MaxEdits edits (edit_bound_exceeded, the library's check).
// Success: stdout is the plan JSON, exit 0. Every refusal prints {"status":"failed","code":<code>} and exits 1 (the CLI
// convention): diff_edits_usage, input_*, diff_edits_project_invalid (not a Project or null elements, geometry or
// dependencies), diff_edits_edits_invalid (not an array of edits), the library's edit_invalid, edit_unknown_id,
// edit_duplicate_id, edit_create_exists, edit_bound_exceeded and invalid_ir, and diff_edits_internal_error.
internal static class DiffEditsCommand
{
    public const string Usage = "usage: diff-edits --project project.json --edits edits.json";
    public const string UsageCode = "diff_edits_usage";
    public const string ProjectInvalid = "diff_edits_project_invalid";
    public const string EditsInvalid = "diff_edits_edits_invalid";
    public const string InternalError = "diff_edits_internal_error";
    static readonly string[] LibraryCodes =
    {
        IncrementalDiff.InvalidEdit, IncrementalDiff.UnknownId, IncrementalDiff.DuplicateId, IncrementalDiff.CreateExists, IncrementalDiff.BoundExceeded,
    };
    static readonly JsonSerializerOptions EditsOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static void Run(string[] args)
    {
        if (!TryParse(args, out var projectPath, out var editsPath)) { Fail(UsageCode); return; }
        string projectText, editsText;
        try
        {
            projectText = BoundedInput.ReadText(projectPath);
            editsText = BoundedInput.ReadText(editsPath);
        }
        catch (InputRejectedException error) { Fail(error.Message); return; }
        if (ParseProject(projectText) is not { } project) { Fail(ProjectInvalid); return; }
        if (ParseEdits(editsText) is not { } edits) { Fail(EditsInvalid); return; }
        ExecutionPlan plan;
        try { plan = IncrementalDiff.Diff(project, edits).Plan; }
        catch (ArgumentException error) when (LibraryCodes.Contains(error.Message, StringComparer.Ordinal)) { Fail(error.Message); return; }
        catch (InvalidOperationException error) when (error.Message == "invalid_ir") { Fail("invalid_ir"); return; }
        // Anything else is a defect, never a message passed off as a code.
        catch (Exception) { Fail(InternalError); return; }
        Console.WriteLine(JsonSerializer.Serialize(plan));
    }

    // Exactly --project <p> and --edits <e>, each once with a non-empty value, in either order; nothing else.
    static bool TryParse(string[] args, out string projectPath, out string editsPath)
    {
        projectPath = ""; editsPath = "";
        if (args.Length != 5) return false;
        string? project = null, edits = null;
        for (var i = 1; i < args.Length; i += 2)
        {
            var value = args[i + 1];
            if (value.Length == 0) return false;
            if (args[i] == "--project" && project is null) project = value;
            else if (args[i] == "--edits" && edits is null) edits = value;
            else return false;
        }
        if (project is null || edits is null) return false;
        projectPath = project; editsPath = edits;
        return true;
    }

    static Project? ParseProject(string text)
    {
        Project? project;
        try { project = JsonSerializer.Deserialize<Project>(text); }
        catch (JsonException) { return null; }
        // Compiler.Validate and the index dereference these; a null one is a malformed project, not a crash.
        if (project?.Elements is null || project.Elements.Any(e => e is null || e.GeometryMetres is null || e.Dependencies is null)) return null;
        return project;
    }

    // Null entries and null element fields are left to the library (edit_invalid).
    static ElementEdit[]? ParseEdits(string text)
    {
        try { return JsonSerializer.Deserialize<ElementEdit[]>(text, EditsOptions); }
        catch (JsonException) { return null; }
    }

    static void Fail(string code)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", code }));
        Environment.ExitCode = 1;
    }
}
