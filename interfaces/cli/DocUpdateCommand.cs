#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
// P4-10 --probe-dependents process-pipe-name [--accept uid]... (instead of --dependents; --accept only with it): the
// dependents come from Revit instead of a file. DocUpdateProbe (interfaces/cli/DocUpdateProbe.cs) runs
// DocUpdateDependents.Plan over the process pipe, which sends only element.delete dry runs, and the file holds exactly
// JsonSerializer.Serialize(result.Plan), the replanned update (the same canonical plan DocUpdate.Plan gives with those
// dependents). accepted is the --accept UniqueIds (untracked dependents a human confirmed; invalid ones are
// accepted_invalid before the policy is read or anything is sent; more than MaxAccepted --accept values are
// doc_update_input_bound_exceeded, field "accepted", before any input is read). Precedence with --probe-dependents:
// usage, output path, pipe name (explicit_process_pipe_required, field pipe_name), the --accept bound, inputs read and
// parsed, accepted_invalid, the first plan (its library refusals), then the enabled-roots policy, read lazily and once,
// just before the first dry run (a plan without deletes never reads it; its RevitCommandException is reported like a
// dry run's refusal, with the key and operation_id of that dry run). In probe mode every refusal prints
// {"status":"failed","code"[,"key"][,"operation_id"][,"field"][,"detail"]}, members present only when set: "field" names
// the field or input concerned, "detail" is the human detail (e.g. an exception type, a UniqueId); a second-plan refusal
// has no key and no operation_id. This covers every DocUpdateDependents failure (dependent_unknown, dependent_kept,
// dependent_planned, dependent_shared, dependents_changed, replan_internal, a second-plan DocReplace code, or a dry run's
// own failure such as document_not_enabled, pipe_connect_timeout, no_response, response_invalid); exit 1, no file. A
// stale target Revit already deleted (reference_not_found on args.element_uids) is a Missing probe, not a refusal.
// Success adds "probed" (dry runs of the first pass) and "dependents" (UniqueIds collected) before "applied":false.
// Without --probe-dependents nothing changes: no policy, no pipe, the P4-08 bytes, summary and refusal envelopes
// ({"status","code"[,"detail"]}, detail = the field or input name as before).
internal static class DocUpdateCommand
{
    public const string Usage = "usage: doc-update --before before.json --after after.json --realized realized.json --bindings bindings.json --output plan.json [--dependents dependents.json | --probe-dependents process-pipe-name [--accept uid]...]";
    public const string UsageCode = "doc_update_usage";
    public const string ProjectInvalid = "doc_update_project_invalid";
    public const string RealizedInvalid = "doc_update_realized_invalid";
    public const string BindingsInvalid = "doc_update_bindings_invalid";
    public const string DependentsInvalid = "doc_update_dependents_invalid";
    public const string BoundExceeded = "doc_update_input_bound_exceeded";
    public const string InternalError = "doc_update_internal_error";
    public const int MaxEntries = 100_000;
    // At most this many --accept values (P4-10).
    public const int MaxAccepted = 10_000;

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

    // Field: the field or input concerned (P4-08 prints it as "detail"); Detail: a human detail (probe mode only).
    sealed class Refusal(string code, string? field = null, string? detail = null) : Exception(code)
    {
        public string Code { get; } = code;
        public string? Field { get; } = field;
        public string? Detail { get; } = detail;
    }

    public static void Run(string[] args)
    {
        if (Execute(args, Console.Out, DocUpdateRevit.Real) != 0) Environment.ExitCode = 1;
    }

    // The verb on an explicit stdout and Revit access (the CLI passes Console.Out and DocUpdateRevit.Real); returns the
    // exit code. revit is used only with --probe-dependents; an internal error's type and message go to Console.Error.
    internal static int Execute(string[] args, TextWriter stdout, DocUpdateRevit revit)
    {
        string? pipe = null;
        // Plain mode keeps the P4-08 envelope {"status","code"[,"detail"]} (detail = field, else detail); probe mode
        // prints field and detail as separate members.
        int Fail(string code, string? field, string? detail, string? key = null, string? operation = null)
        {
            if (pipe is null)
            {
                var single = field ?? detail;
                stdout.WriteLine(single is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail = single }));
                return 1;
            }
            var envelope = new JsonObject { ["status"] = "failed", ["code"] = code };
            if (key is not null) envelope["key"] = key;
            if (operation is not null) envelope["operation_id"] = operation;
            if (field is not null) envelope["field"] = field;
            if (detail is not null) envelope["detail"] = detail;
            stdout.WriteLine(envelope.ToJsonString());
            return 1;
        }
        if (!TryParse(args, out var paths, out var accepted)) return Fail(UsageCode, null, null);
        var output = paths["--output"];
        if (GltfExportCommand.OutputProblem(output, [".json"], out _) is string refused) return Fail(refused, null, null);
        pipe = paths.TryGetValue("--probe-dependents", out var pipeName) ? pipeName : null;
        if (pipe is not null && DocUpdateProbe.PipeProblem(pipe) is { } pipeRefused) return Fail(pipeRefused.Code, pipeRefused.Field, pipeRefused.Detail);
        if (accepted.Count > MaxAccepted) return Fail(BoundExceeded, "accepted", null);
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
            DocUpdatePlan plan;
            DocUpdateDependentsResult? probed = null;
            if (pipe is not string probePipe) plan = Library(() => DocUpdate.Plan(before, after, bindings, realized, dependents));
            else
            {
                probed = Library(() => DocUpdateProbe.Plan(before, after, bindings, realized, probePipe, accepted, revit));
                if (probed.Failed is { } failed) return Fail(failed.Code, failed.Field, failed.Detail, failed.Key, failed.OperationId);
                if (probed.Plan is null) return Fail(InternalError, null, null);
                plan = probed.Plan;
            }
            var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(plan));
            object summary = probed is null
                ? new
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
                }
                : new
                {
                    status = "succeeded",
                    output,
                    bytes = bytes.LongLength,
                    sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                    regenerated = plan.Regeneration.Regenerated.Count,
                    steps = plan.Replacement.Commands.Steps.Count,
                    forced = plan.Forced.Count,
                    retired = plan.Retired.Count,
                    probed = probed.Probes.Count,
                    dependents = probed.Dependents.Values.Sum(list => list.Count),
                    applied = false,
                };
            var line = JsonSerializer.Serialize(summary);
            try { GltfExporter.WriteNew(bytes, output); }
            catch (GltfExportException e) { return Fail(e.Code, null, null); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Fail("output_io_error", null, e.GetType().Name); }
            stdout.WriteLine(line);
            return 0;
        }
        // Plain mode: the P4-08 detail is the field (a RevitCommandException's own detail was never printed there).
        catch (Refusal refusal) { return Fail(refusal.Code, refusal.Field, pipe is null ? null : refusal.Detail); }
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
            Console.Error.WriteLine("doc-update internal error: " + error.GetType().FullName + ": " + error.Message);
            throw new Refusal(InternalError);
        }
    }

    // The five required flags and either the optional --dependents or the optional --probe-dependents (never both), each
    // once, plus any number of --accept only with --probe-dependents; every flag with a non-empty value that is not
    // flag-shaped ("--..."), in any order; nothing else.
    static bool TryParse(string[] args, out Dictionary<string, string> paths, out List<string> accepted)
    {
        paths = new Dictionary<string, string>(StringComparer.Ordinal);
        accepted = new List<string>();
        if (args.Length < 11 || args.Length % 2 == 0) return false;
        for (var i = 1; i < args.Length; i += 2)
        {
            var value = args[i + 1];
            if (value.Length == 0 || value.StartsWith("--", StringComparison.Ordinal)) return false;
            if (args[i] == "--accept") { accepted.Add(value); continue; }
            if (!(Required.Contains(args[i], StringComparer.Ordinal) || args[i] is "--dependents" or "--probe-dependents") || !paths.TryAdd(args[i], value)) return false;
        }
        if (paths.ContainsKey("--dependents") && paths.ContainsKey("--probe-dependents")) return false;
        if (accepted.Count > 0 && !paths.ContainsKey("--probe-dependents")) return false;
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

}
