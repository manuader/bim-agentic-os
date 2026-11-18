#nullable enable
using System.Text.Json;
using Bim.Core;

namespace Bim.Cli;

// P4-22: the dry-plan half of the doc-update verb (interfaces/cli/DocUpdateCommand.cs holds the verb itself: its argument
// parser, the output path, the summary, the write, and the --probe-dependents / --execute hand-off to
// interfaces/cli/DocUpdateProbe.cs and interfaces/cli/DocUpdateExecute.cs). This part of DocUpdateCommand reads and
// parses the inputs (ReadInputs: BoundedInput, strict members, duplicate keys refused, the entry bounds), plans with
// DocUpdate.Plan behind the library-code filter (Library), and builds the plain-mode refusal envelope. It references no
// pipe, policy, probe or run, so the read-only MCP server (interfaces/mcp/Bim.Mcp.csproj) compiles it alone for the
// doc_update tool's DryPlan; the CLI compiles both parts. Moved verbatim from DocUpdateCommand.cs: the refusal codes, the
// bounds, the bytes and the precedence are those documented there.
internal static partial class DocUpdateCommand
{
    public const string ProjectInvalid = "doc_update_project_invalid";
    public const string RealizedInvalid = "doc_update_realized_invalid";
    public const string BindingsInvalid = "doc_update_bindings_invalid";
    public const string DependentsInvalid = "doc_update_dependents_invalid";
    public const string BoundExceeded = "doc_update_input_bound_exceeded";
    public const string InternalError = "doc_update_internal_error";
    public const int MaxEntries = 100_000;

    // Every refusal message DocUpdate.Plan can raise as ArgumentException or InvalidOperationException (DocDefaults,
    // Compiler.Diff, DocRegeneration, DocReplace); any other message is an internal error, never passed off as a code.
    static readonly string[] LibraryCodes =
    {
        DocDefaultsCodes.InvalidIr, DocDefaultsCodes.LevelInvalid, DocDefaultsCodes.NoLevel, "revision_conflict", "plan_mismatch", "project_mismatch", "input_mismatch",
        "doc_item_unknown", "args_not_object", "input_invalid", "doc_text_invalid", "doc_number_invalid", "doc_key_invalid", "doc_key_duplicate", "doc_view_kind_invalid",
        "doc_view_elements_invalid", "doc_dimension_references_invalid", "doc_view_unknown", "doc_view_not_plan", "doc_sheet_unknown",
        DocReplaceCodes.RegenerationInvalid, DocReplaceCodes.RealizedInvalid, DocReplaceCodes.StaleShared, DocReplaceCodes.DependentsUnknownKey,
        DocReplaceCodes.DependentsWithoutStale, DocReplaceCodes.DependentsInvalid, DocReplaceCodes.DependentKept, DocReplaceCodes.DependentPlanned,
        DocReplaceCodes.DependentIsTarget, DocReplaceCodes.InputFieldUnsupported, DocReplaceCodes.InputUnresolved, DocReplaceCodes.InputFieldDuplicate,
        DocUpdateDependentsCodes.DependentUnknown, DocUpdateDependentsCodes.DependentsChanged, DocUpdateDependentsCodes.AcceptedInvalid, DocUpdateDependentsCodes.DependentShared,
        DocUpdateDependentsCodes.ReplanInternal,
    };

    // Field: the field or input concerned (P4-08 prints it as "detail"); Detail: a human detail (probe mode only).
    sealed class Refusal(string code, string? field = null, string? detail = null) : Exception(code)
    {
        public string Code { get; } = code;
        public string? Field { get; } = field;
        public string? Detail { get; } = detail;
    }

    // The P4-08 plain-mode envelope {"status":"failed","code"[,"detail"]}.
    static string PlainFailure(string code, string? detail) =>
        detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail });

    sealed record Inputs(Project Before, Project After, Dictionary<string, string> Realized, RevitBindings Bindings, Dictionary<string, IReadOnlyList<string>>? Dependents);

    // The verb's inputs: every file read first (before, after, realized, bindings, then dependents when given), then parsed
    // in that order; the first failure throws its Refusal. The verb and DryPlan both read through here.
    static Inputs ReadInputs(string beforePath, string afterPath, string realizedPath, string bindingsPath, string? dependentsPath)
    {
        var beforeText = Read(beforePath, "before");
        var afterText = Read(afterPath, "after");
        var realizedText = Read(realizedPath, "realized");
        var bindingsText = Read(bindingsPath, "bindings");
        var dependentsText = dependentsPath is null ? null : Read(dependentsPath, "dependents");
        var before = ParseProject(beforeText, "before");
        var after = ParseProject(afterText, "after");
        var realized = ParseRealized(realizedText);
        var bindings = ParseBindings(bindingsText);
        var dependents = dependentsText is null ? null : ParseDependents(dependentsText);
        return new Inputs(before, after, realized, bindings, dependents);
    }

    // P4-20: the dry plan of the read-only MCP tool doc_update (interfaces/mcp/DocUpdateTool.cs), without an output path,
    // a pipe or any write. Succeeded: Text is JsonSerializer.Serialize(DocUpdate.Plan(...)), whose UTF-8 bytes are exactly
    // the file the verb writes for the same input files without --probe-dependents. Otherwise Text is the verb's plain-mode
    // refusal line without its newline (input_*, doc_update_*_invalid, doc_update_input_bound_exceeded, the library codes,
    // doc_update_internal_error with the detail on stderr), in the verb's precedence after the output path.
    internal static (bool Succeeded, string Text) DryPlan(string beforePath, string afterPath, string realizedPath, string bindingsPath, string? dependentsPath)
    {
        try
        {
            var (before, after, realized, bindings, dependents) = ReadInputs(beforePath, afterPath, realizedPath, bindingsPath, dependentsPath);
            return (true, JsonSerializer.Serialize(Library(() => DocUpdate.Plan(before, after, bindings, realized, dependents))));
        }
        catch (Refusal refusal) { return (false, PlainFailure(refusal.Code, refusal.Field)); }
    }

    // A library call: its typed refusals become Refusal, anything else is an internal error (type and message on stderr).
    static T Library<T>(Func<T> call)
    {
        try { return call(); }
        catch (RevitCommandException error) { throw new Refusal(error.Code, error.Field, error.Detail); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException && error is not ArgumentNullException && LibraryCodes.Contains(error.Message, StringComparer.Ordinal))
        {
            throw new Refusal(error.Message);
        }
        // Anything else is a defect, never a message passed off as a code.
        catch (Exception error)
        {
            Console.Error.WriteLine(IntentPlan.InternalDiagnostic("doc-update", error));
            throw new Refusal(InternalError);
        }
    }

    static string Read(string path, string name)
    {
        if (path.IndexOf('\0') >= 0) throw new Refusal("input_path_invalid", name);
        try { return BoundedInput.ReadText(path); }
        catch (InputRejectedException e) { throw new Refusal(e.Message, name); }
    }

    static Project ParseProject(string text, string name)
    {
        Project? project;
        try { project = JsonSerializer.Deserialize<Project>(text); }
        catch (JsonException) { throw new Refusal(ProjectInvalid, name); }
        // A lone surrogate escape ("\ud800") is valid JSON but not text; reading it is a malformed Project, never a crash.
        catch (InvalidOperationException) { throw new Refusal(ProjectInvalid, name); }
        if (project?.Elements is null || project.Elements.Any(e => e is null || e.GeometryMetres is null || e.Dependencies is null))
            throw new Refusal(ProjectInvalid, name);
        return project;
    }

    // A JSON document with duplicate keys refused in every object (System.Text.Json alone keeps the last one); depth is
    // bounded by JsonDocument's default limit of 64 and size by the 8 MiB read.
    static JsonDocument Parse(string text, string code)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException) { throw new Refusal(code); }
        if (HasDuplicateKey(document.RootElement)) { document.Dispose(); throw new Refusal(code); }
        return document;
    }

    static Dictionary<string, string> ParseRealized(string text) => Textual(() => ParseRealizedJson(text), RealizedInvalid);

    static Dictionary<string, string> ParseRealizedJson(string text)
    {
        using var document = Parse(text, RealizedInvalid);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new Refusal(RealizedInvalid);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (map.Count == MaxEntries) throw new Refusal(BoundExceeded, "realized");
            if (property.Value.ValueKind != JsonValueKind.String) throw new Refusal(RealizedInvalid);
            map[property.Name] = property.Value.GetString()!;
        }
        return map;
    }

    static RevitBindings ParseBindings(string text) => Textual(() => ParseBindingsJson(text), BindingsInvalid);

    static RevitBindings ParseBindingsJson(string text)
    {
        using var document = Parse(text, BindingsInvalid);
        var root = document.RootElement;
        Members(root, BindingsInvalid, "document", "levels", "wall_types", "door_symbol", "window_symbol");
        if (!root.TryGetProperty("document", out var documentElement) || documentElement.ValueKind != JsonValueKind.Object) throw new Refusal(BindingsInvalid);
        Members(documentElement, BindingsInvalid, "path", "unique_id");
        var revitDocument = new RevitDocumentRef(Text(documentElement, "path"), Text(documentElement, "unique_id"));

        var levels = new Dictionary<Guid, string>();
        if (!root.TryGetProperty("levels", out var levelsElement) || levelsElement.ValueKind != JsonValueKind.Object) throw new Refusal(BindingsInvalid);
        foreach (var level in levelsElement.EnumerateObject())
        {
            if (!Guid.TryParseExact(level.Name, "D", out var id) || level.Value.ValueKind != JsonValueKind.String || !levels.TryAdd(id, level.Value.GetString()!))
                throw new Refusal(BindingsInvalid);
            if (levels.Count > MaxEntries) throw new Refusal(BoundExceeded, "bindings");
        }

        var wallTypes = new List<RevitWallTypeBinding>();
        var thicknesses = new HashSet<double>();
        if (root.TryGetProperty("wall_types", out var typesElement) && typesElement.ValueKind != JsonValueKind.Null)
        {
            if (typesElement.ValueKind != JsonValueKind.Array) throw new Refusal(BindingsInvalid);
            foreach (var type in typesElement.EnumerateArray())
            {
                Members(type, BindingsInvalid, "thickness_metres", "type");
                if (!type.TryGetProperty("thickness_metres", out var thickness) || thickness.ValueKind != JsonValueKind.Number || !thickness.TryGetDouble(out var metres) || !double.IsFinite(metres))
                    throw new Refusal(BindingsInvalid);
                // Equal thicknesses keep input order through RevitBindings' stable sort, so their order would change bytes.
                if (!thicknesses.Add(metres)) throw new Refusal(BindingsInvalid);
                wallTypes.Add(new RevitWallTypeBinding(metres, Text(type, "type")));
                if (wallTypes.Count > MaxEntries) throw new Refusal(BoundExceeded, "bindings");
            }
        }
        return new RevitBindings(revitDocument, levels, wallTypes, OptionalText(root, "door_symbol"), OptionalText(root, "window_symbol"));
    }

    static Dictionary<string, IReadOnlyList<string>> ParseDependents(string text) => Textual(() => ParseDependentsJson(text), DependentsInvalid);

    static Dictionary<string, IReadOnlyList<string>> ParseDependentsJson(string text)
    {
        using var document = Parse(text, DependentsInvalid);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new Refusal(DependentsInvalid);
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var total = 0;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array) throw new Refusal(DependentsInvalid);
            var uids = new List<string>();
            foreach (var uid in property.Value.EnumerateArray())
            {
                if (uid.ValueKind != JsonValueKind.String) throw new Refusal(DependentsInvalid);
                if (++total > MaxEntries) throw new Refusal(BoundExceeded, "dependents");
                uids.Add(uid.GetString()!);
            }
            map[property.Name] = uids.ToArray();
        }
        return map;
    }

    // A lone surrogate escape ("\ud800") in a member name or string value is valid JSON but not text: JsonElement.GetString
    // and JsonProperty.Name throw InvalidOperationException on it. It is the input's typed refusal (code), never a crash.
    static T Textual<T>(Func<T> parse, string code)
    {
        try { return parse(); }
        catch (InvalidOperationException) { throw new Refusal(code); }
    }

    // element is an object whose members are all in allowed.
    static void Members(JsonElement element, string code, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new Refusal(code);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) throw new Refusal(code);
    }

    static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new Refusal(BindingsInvalid);

    static string? OptionalText(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new Refusal(BindingsInvalid);
    }

    static bool HasDuplicateKey(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                    if (!names.Add(property.Name) || HasDuplicateKey(property.Value)) return true;
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) if (HasDuplicateKey(item)) return true;
                return false;
            default:
                return false;
        }
    }
}
