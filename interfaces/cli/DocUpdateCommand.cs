#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Core;
using Bim.Gltf;

namespace Bim.Cli;

// P4-08 `doc-update --before b.json --after a.json --realized r.json --bindings bindings.json --output plan.json
// [--dependents dependents.json]`: the P4-07 documentation update plan (core/Bim.Core/DocUpdate.cs) of a BIM-IR edit,
// written to a new file. A dry plan: DocUpdate.Plan only, never Run; no transport, no pipe, no Revit, no store.
//   before/after   Projects, read like `diff-edits --project` (default System.Text.Json; null elements, geometry or
//                  dependencies refused as doc_update_project_invalid, detail "before" or "after").
//   realized       a JSON object {"doc:<key>"|"element:<id D>": "<Revit UniqueId>"} of string values (DocReplace's
//                  realized map; its key and UniqueId rules are the library's realized_invalid).
//   bindings       {"document":{"path","unique_id"},"levels":{"<BIM-IR level id>":"<Revit level>"}, optional
//                  "wall_types":[{"thickness_metres","type"}], "door_symbol", "window_symbol"} (the member names of the
//                  mirror-drift request); the after-state RevitBindings DocRegeneration needs (Revit document, levels).
//                  "document" and "levels" are required objects; an optional member given as null is the same as absent.
//                  Two wall types of one thickness are refused (their order would decide the bytes of the plan).
//   dependents     optional {"<regenerated key>":["<UniqueId>",...]}, the dry-run dependents of DocUpdate.Plan.
// Every object of realized, bindings and dependents refuses duplicate keys and unknown members.
// Output: the file holds exactly JsonSerializer.Serialize(DocUpdate.Plan(before, after, bindings, realized, dependents))
// (default options, compact, UTF-8 without BOM, no trailing newline): byte-identical to the library call. Deterministic:
// the plan sorts its maps and keys, so input member order and culture do not change a byte.
// Bounded input: every file goes through BoundedInput.ReadText (8 MiB, regular files only, links refused; input_* codes
// with detail naming the input); at most MaxEntries realized entries, MaxEntries level bindings, MaxEntries wall types
// and MaxEntries dependent UniqueIds in total (doc_update_input_bound_exceeded, detail "realized", "bindings" or
// "dependents").
// Never-overwrite: the output path follows GltfExportCommand.OutputProblem with extension .json (never an existing file,
// directory or link; checked before any input is read) and is created exclusively (GltfExporter.WriteNew).
// Success prints {"status":"succeeded","output","bytes","sha256","regenerated","steps","forced","retired","applied":false}
// and exits 0. Every refusal prints one {"status":"failed","code"[,"detail"]} envelope, exits 1 and writes no file:
// doc_update_usage, output_*, input_*, doc_update_project_invalid, doc_update_realized_invalid,
// doc_update_bindings_invalid, doc_update_dependents_invalid, doc_update_input_bound_exceeded, the library's codes
// (LibraryCodes: the ArgumentException / InvalidOperationException messages of DocDefaults, Compiler.Diff,
// DocRegeneration and DocReplace, such as invalid_ir, revision_conflict, realized_invalid, input_unresolved,
// dependents_unknown_key; a RevitCommandException's Code with detail = its field, such as document_path_invalid)
// and doc_update_internal_error (a defect: type and message on stderr). The summary counts are taken from the plan
// before the file is written, so nothing can fail after the write.
// Precedence: usage, output path, inputs read (before, after, realized, bindings, dependents), parsed in that order, then
// the library; the first failure is the only one reported.
internal static class DocUpdateCommand
{
    public const string Usage = "usage: doc-update --before before.json --after after.json --realized realized.json --bindings bindings.json --output plan.json [--dependents dependents.json]";
    public const string UsageCode = "doc_update_usage";
    public const string ProjectInvalid = "doc_update_project_invalid";
    public const string RealizedInvalid = "doc_update_realized_invalid";
    public const string BindingsInvalid = "doc_update_bindings_invalid";
    public const string DependentsInvalid = "doc_update_dependents_invalid";
    public const string BoundExceeded = "doc_update_input_bound_exceeded";
    public const string InternalError = "doc_update_internal_error";
    public const int MaxEntries = 100_000;

    static readonly string[] Required = { "--before", "--after", "--realized", "--bindings", "--output" };
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

    sealed class Refusal(string code, string? detail = null) : Exception(code)
    {
        public string Code { get; } = code;
        public string? Detail { get; } = detail;
    }

    public static void Run(string[] args)
    {
        if (!TryParse(args, out var paths)) { Fail(UsageCode, null); return; }
        var output = paths["--output"];
        if (GltfExportCommand.OutputProblem(output, [".json"], out _) is string refused) { Fail(refused, null); return; }
        try
        {
            var beforeText = Read(paths["--before"], "before");
            var afterText = Read(paths["--after"], "after");
            var realizedText = Read(paths["--realized"], "realized");
            var bindingsText = Read(paths["--bindings"], "bindings");
            var dependentsText = paths.TryGetValue("--dependents", out var dependentsPath) ? Read(dependentsPath, "dependents") : null;
            var before = ParseProject(beforeText, "before");
            var after = ParseProject(afterText, "after");
            var realized = ParseRealized(realizedText);
            var bindings = ParseBindings(bindingsText);
            var dependents = dependentsText is null ? null : ParseDependents(dependentsText);
            var (text, plan) = Plan(before, after, bindings, realized, dependents);
            var bytes = new UTF8Encoding(false).GetBytes(text);
            var summary = new
            {
                status = "succeeded",
                output,
                bytes = bytes.LongLength,
                sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                regenerated = plan.Regeneration.Regenerated.Count,
                steps = plan.Replacement.Commands.Steps.Count,
                forced = plan.Forced.Count,
                retired = plan.Retired.Count,
                applied = false,
            };
            try { GltfExporter.WriteNew(bytes, output); }
            catch (GltfExportException e) { Fail(e.Code, null); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Fail("output_io_error", e.GetType().Name); return; }
            Console.WriteLine(JsonSerializer.Serialize(summary));
        }
        catch (Refusal refusal) { Fail(refusal.Code, refusal.Detail); }
    }

    static (string Text, DocUpdatePlan Plan) Plan(Project before, Project after, RevitBindings bindings, Dictionary<string, string> realized, Dictionary<string, IReadOnlyList<string>>? dependents)
    {
        try
        {
            var plan = DocUpdate.Plan(before, after, bindings, realized, dependents);
            return (JsonSerializer.Serialize(plan), plan);
        }
        catch (RevitCommandException error) { throw new Refusal(error.Code, error.Field); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException && error is not ArgumentNullException && LibraryCodes.Contains(error.Message, StringComparer.Ordinal))
        {
            throw new Refusal(error.Message);
        }
        // Anything else is a defect, never a message passed off as a code.
        catch (Exception error)
        {
            Console.Error.WriteLine("doc-update internal error: " + error.GetType().FullName + ": " + error.Message);
            throw new Refusal(InternalError);
        }
    }

    // The five required flags and the optional --dependents, each once with a non-empty value that is not flag-shaped
    // ("--..."), in any order; nothing else.
    static bool TryParse(string[] args, out Dictionary<string, string> paths)
    {
        paths = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args.Length is not (11 or 13)) return false;
        for (var i = 1; i < args.Length; i += 2)
        {
            var value = args[i + 1];
            if (value.Length == 0 || value.StartsWith("--", StringComparison.Ordinal)) return false;
            if (!(Required.Contains(args[i], StringComparer.Ordinal) || args[i] == "--dependents") || !paths.TryAdd(args[i], value)) return false;
        }
        return Required.All(paths.ContainsKey);
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

    static Dictionary<string, string> ParseRealized(string text)
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

    static RevitBindings ParseBindings(string text)
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

    static Dictionary<string, IReadOnlyList<string>> ParseDependents(string text)
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

    static void Fail(string code, string? detail)
    {
        Console.WriteLine(detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail }));
        Environment.ExitCode = 1;
    }
}
