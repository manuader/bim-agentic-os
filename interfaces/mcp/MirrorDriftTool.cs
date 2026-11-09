#nullable enable
using System.Globalization;
using System.Text;
using System.Text.Json;
using Bim.Cli;
using Bim.Core;

namespace Bim.Mcp;

// P2-08: the one engine behind the CLI verb `mirror-drift store-directory snapshot.json mirror.json output.json`
// (interfaces/cli/MirrorDriftCommand.cs) and the MCP tool mirror_drift (McpServer). It reads the Core store's committed
// project (store-directory/project.json, through BoundedInput like every CLI input; never ProjectStore, never a lock,
// never a write), runs the P2-04 drift report (MirrorDrift.Compare) of a query.snapshot against it with the add-in's
// logical map, then the P2-07 resolution (MirrorDriftResolve.Plan). Nothing is applied: no store commit, no plan run,
// no pipe. The CLI writes the document to a new file; the tool returns it as text, byte-equal.
//
// snapshot: the query.snapshot readback (snapshot_version 1) or the whole catalogue response
// ({"status":"succeeded",...,"readback":<snapshot>}); any other status is drift_snapshot_invalid.
// mirror (strict, unknown members refused): {"document":{"path","unique_id"},"core_project_id","core_revision",
// "map":{"<BIM-IR id or fixture key>":"<UniqueId>"}, optional "levels":{"<BIM-IR level id>":"<Revit level>"},
// "wall_types":[{"thickness_metres","type"}], "door_symbol", "window_symbol", "journal" (a journal.read reply, its report
// or the raw journal), "options":{"length_tolerance_mm","angle_tolerance_deg","journal_after_sequence","max_findings",
// "scope"}}. core_project_id / core_revision name the Core state the map (and the caller's view) belongs to: a store at
// another project or revision is drift_resolve_report_stale.
//
// Success document (UTF-8, compact): {"mirror_drift_version":1,"applied":false,"requires_approval":true,
// "core_store":{"project_id","revision","hash"},"report":<MirrorDriftReport.ToJson>,"resolution":<MirrorDriftResolution.ToJson>}.
// Refusal: {"status":"failed","code":..[,"detail":..]}. Codes: drift_store_path_invalid, input_* (BoundedInput, detail
// "store"), drift_store_invalid (missing or null Project/element members), mirror_drift_request_invalid (also any
// duplicate member), mirror_drift_internal_error (an unexpected exception; detail is its type), drift_input_too_large, drift_resolve_report_stale, and
// the Core codes (drift_snapshot_invalid, drift_document_mismatch, drift_document_required, drift_map_invalid,
// drift_model_invalid, drift_journal_invalid, drift_options_invalid, drift_resolve_*, document_path_invalid,
// document_identity_invalid).
internal static class MirrorDriftTool
{
    public const int Version = 1;
    public const string StorePathInvalid = "drift_store_path_invalid";
    public const string StoreInvalid = "drift_store_invalid";
    public const string RequestInvalid = "mirror_drift_request_invalid";
    public const string InternalError = "mirror_drift_internal_error";
    public const int MaxBindings = 10_000;

    public sealed record Outcome(bool Succeeded, string Text, int Drift, int Proposals);

    static readonly HashSet<string> MirrorMembers = new(StringComparer.Ordinal)
    {
        "document", "core_project_id", "core_revision", "map", "levels", "wall_types", "door_symbol", "window_symbol", "journal", "options",
    };
    static readonly HashSet<string> OptionMembers = new(StringComparer.Ordinal)
    {
        "length_tolerance_mm", "angle_tolerance_deg", "journal_after_sequence", "max_findings", "scope",
    };

    sealed class Refusal(string code, string? detail = null) : Exception(code)
    {
        public string Code { get; } = code;
        public string? Detail { get; } = detail;
    }

    public static string Failure(string code, string? detail) =>
        detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail });

    public static Outcome Run(string storeDirectory, string snapshotJson, string mirrorJson)
    {
        try { return Execute(storeDirectory, snapshotJson, mirrorJson); }
        catch (Exception error) { return new Outcome(false, Describe(error), 0, 0); }
    }

    // The refusal envelope of an exception raised by Analyze (shared with MirrorDriftSelectTool, P2-10).
    internal static string Describe(Exception error)
    {
        switch (error)
        {
            case Refusal refusal: return Failure(refusal.Code, refusal.Detail);
            case InputRejectedException rejected: return Failure(rejected.Message, "store");
            case RevitCommandException command: return Failure(command.Code, Join(command.Field, command.Detail));
            case ArgumentException argument:
                var message = argument.Message;
                var space = message.IndexOf(' ');
                return space < 0 ? Failure(message, null) : Failure(message[..space], message[(space + 1)..]);
            // Anything else is a defect, never a message passed off as a code.
            default: return Failure(InternalError, error.GetType().Name);
        }
    }

    static string? Join(string? field, string? detail) =>
        field is null ? detail : detail is null ? field : field + ": " + detail;

    // The Core state read, the P2-04 report and the P2-07 resolution; throws what Describe maps. P2-10 MirrorDriftSelectTool
    // runs the same analysis before MirrorDriftSelect.Select.
    internal sealed record Analysis(Project Project, MirrorDriftReport Report, MirrorDriftResolution Resolution);

    static Outcome Execute(string storeDirectory, string snapshotJson, string mirrorJson)
    {
        var (project, report, resolution) = Analyze(storeDirectory, snapshotJson, mirrorJson);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("mirror_drift_version", Version);
            writer.WriteBoolean("applied", false);
            writer.WriteBoolean("requires_approval", true);
            writer.WriteStartObject("core_store");
            writer.WriteString("project_id", project.Id.ToString("D"));
            writer.WriteNumber("revision", project.Revision);
            writer.WriteString("hash", Compiler.Hash(project));
            writer.WriteEndObject();
            writer.WritePropertyName("report");
            writer.WriteRawValue(report.ToJson());
            writer.WritePropertyName("resolution");
            writer.WriteRawValue(resolution.ToJson());
            writer.WriteEndObject();
        }
        var text = Encoding.UTF8.GetString(buffer.ToArray());
        return new Outcome(true, text, report.Summary.Drift, resolution.Proposals.Count);
    }

    internal static Analysis Analyze(string storeDirectory, string snapshotJson, string mirrorJson)
    {
        // 1. The Core store, read-only.
        if (string.IsNullOrWhiteSpace(storeDirectory) || storeDirectory.IndexOf('\0') >= 0) throw new Refusal(StorePathInvalid);
        // FileShare.ReadWrite | Delete: a concurrent commit replaces project.json (temp + move) without a sharing violation.
        var project = ParseStore(BoundedInput.ReadText(Path.Combine(storeDirectory, "project.json"), FileShare.ReadWrite | FileShare.Delete));

        // 2. The mirror request.
        var mirror = ParseMirror(mirrorJson);

        // 3. The snapshot (a bare readback or a catalogue response).
        var snapshot = MirrorDrift.ParseSnapshot(SnapshotBody(snapshotJson));

        // 4. Stale: the mirror belongs to another Core state.
        if (mirror.ProjectId != project.Id || mirror.Revision != project.Revision)
            throw new Refusal(MirrorDriftResolveCodes.ReportStale, "store holds project " + project.Id.ToString("D") + " at revision " + project.Revision.ToString(CultureInfo.InvariantCulture)
                + "; the mirror names project " + mirror.ProjectId.ToString("D") + " at revision " + mirror.Revision.ToString(CultureInfo.InvariantCulture));

        // 5. Report and resolution.
        var committed = MirrorCommittedModel.Parse(CommittedJson(mirror.Document.UniqueId, project, mirror.MapJson));
        var report = mirror.Journal is { } journal
            ? MirrorDrift.Compare(snapshot, committed, MirrorDrift.ParseJournal(journal), mirror.Options)
            : MirrorDrift.Compare(snapshot, committed, (IReadOnlyList<MirrorJournalEntry>?)null, mirror.Options);
        if (report.Truncated)
            throw new Refusal(MirrorDriftResolveCodes.ReportTruncated, "the report holds more than options.max_findings = " + report.Options.MaxFindings.ToString(CultureInfo.InvariantCulture)
                + " findings; raise options.max_findings (at most " + MirrorDriftOptions.MaxFindingsLimit.ToString(CultureInfo.InvariantCulture) + ") to resolve it");
        var resolution = MirrorDriftResolve.Plan(report, snapshot, committed, new RevitBindings(mirror.Document, mirror.Levels, mirror.WallTypes, mirror.DoorSymbol, mirror.WindowSymbol));
        return new Analysis(project, report, resolution);
    }

    // The store state ({"Project":{..},"Receipts":{..}}) with every member Core relies on present and non-null: a
    // missing Id or Revision would otherwise deserialize as Guid.Empty or 0.
    static Project ParseStore(string json)
    {
        try
        {
            using (var document = JsonDocument.Parse(json))
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Project", out var projectElement) || projectElement.ValueKind != JsonValueKind.Object)
                    throw new Refusal(StoreInvalid, "project.json has no Project object");
                if (!projectElement.TryGetProperty("Id", out var id) || id.ValueKind != JsonValueKind.String
                    || !projectElement.TryGetProperty("Revision", out var revision) || revision.ValueKind != JsonValueKind.Number
                    || !projectElement.TryGetProperty("Elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
                    throw new Refusal(StoreInvalid, "Project needs Id, Revision and Elements");
                foreach (var element in elements.EnumerateArray())
                    if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("Id", out var elementId) || elementId.ValueKind != JsonValueKind.String)
                        throw new Refusal(StoreInvalid, "every element needs an Id");
            }
            var project = JsonSerializer.Deserialize<ProjectStore.State>(json)?.Project ?? throw new Refusal(StoreInvalid, "project.json has no Project");
            if (project.Id == Guid.Empty || project.Elements is null) throw new Refusal(StoreInvalid, "Project Id and Elements required");
            foreach (var element in project.Elements)
                if (element is null || element.Id == Guid.Empty || element.Kind is null || element.GeometryMetres is null || element.Dependencies is null || element.Provenance is null)
                    throw new Refusal(StoreInvalid, "every element needs Id, Kind, GeometryMetres, Dependencies and Provenance");
            return project;
        }
        catch (JsonException) { throw new Refusal(StoreInvalid, "project.json is not a store state"); }
        catch (InvalidOperationException) { throw new Refusal(StoreInvalid, "project.json value type"); }
    }

    // JsonDocument keeps duplicate members; a request that names one twice is ambiguous and refused.
    static void Unique(JsonElement element, string prefix)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in element.EnumerateObject())
            if (!seen.Add(member.Name)) throw new Refusal(RequestInvalid, "duplicate member " + prefix + member.Name);
    }

    static string SnapshotBody(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var status)) return json;
            if (status.ValueKind != JsonValueKind.String || status.GetString() != RevitCommandResponse.Succeeded)
                throw new ArgumentException(MirrorDrift.SnapshotInvalidCode + " query.snapshot response did not succeed");
            if (!root.TryGetProperty("readback", out var readback) || readback.ValueKind != JsonValueKind.Object)
                throw new ArgumentException(MirrorDrift.SnapshotInvalidCode + " query.snapshot response has no readback object");
            return readback.GetRawText();
        }
        catch (JsonException) { throw new ArgumentException(MirrorDrift.SnapshotInvalidCode + " not JSON"); }
    }

    // MirrorCommittedModel.Parse's own shape, so map validation (duplicate keys, non-string values, fixture keys) is Core's.
    static string CommittedJson(string documentUid, Project project, string mapJson)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("document_unique_id", documentUid);
            writer.WritePropertyName("project");
            writer.WriteRawValue(JsonSerializer.Serialize(project));
            writer.WritePropertyName("map");
            writer.WriteRawValue(mapJson);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    sealed record Mirror(RevitDocumentRef Document, Guid ProjectId, int Revision, string MapJson, IReadOnlyDictionary<Guid, string> Levels,
        IReadOnlyList<RevitWallTypeBinding> WallTypes, string? DoorSymbol, string? WindowSymbol, string? Journal, MirrorDriftOptions Options);

    static Mirror ParseMirror(string json)
    {
        try
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new Refusal(RequestInvalid, "mirror must be an object");
            Unique(root, "");
            foreach (var member in root.EnumerateObject())
                if (!MirrorMembers.Contains(member.Name)) throw new Refusal(RequestInvalid, "unknown member " + member.Name);
            JsonElement Required(string name, JsonValueKind kind) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == kind ? value : throw new Refusal(RequestInvalid, name + " must be " + kind.ToString().ToLowerInvariant());
            string? OptionalString(string name) => !root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null
                : value.ValueKind == JsonValueKind.String ? value.GetString() : throw new Refusal(RequestInvalid, name + " must be a string or null");

            var documentElement = Required("document", JsonValueKind.Object);
            Unique(documentElement, "document.");
            foreach (var member in documentElement.EnumerateObject())
                if (member.Name != "path" && member.Name != "unique_id") throw new Refusal(RequestInvalid, "unknown member document." + member.Name);
            string DocumentText(string name) => documentElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : throw new Refusal(RequestInvalid, "document." + name + " must be a string");
            var document = new RevitDocumentRef(DocumentText("path"), DocumentText("unique_id"));

            if (!Guid.TryParse(Required("core_project_id", JsonValueKind.String).GetString(), out var projectId)) throw new Refusal(RequestInvalid, "core_project_id must be a GUID");
            if (!Required("core_revision", JsonValueKind.Number).TryGetInt32(out var revision) || revision < 0) throw new Refusal(RequestInvalid, "core_revision must be a non-negative integer");
            var map = Required("map", JsonValueKind.Object);
            if (map.EnumerateObject().Count() > MirrorDrift.MaxCommittedElements + MaxBindings) throw new Refusal(MirrorDrift.TooLargeCode, "map");

            var levels = new Dictionary<Guid, string>();
            if (root.TryGetProperty("levels", out var levelsElement) && levelsElement.ValueKind != JsonValueKind.Null)
            {
                if (levelsElement.ValueKind != JsonValueKind.Object) throw new Refusal(RequestInvalid, "levels must be an object");
                Unique(levelsElement, "levels.");
                foreach (var level in levelsElement.EnumerateObject())
                {
                    if (levels.Count >= MaxBindings) throw new Refusal(MirrorDrift.TooLargeCode, "levels");
                    if (!Guid.TryParse(level.Name, out var id) || level.Value.ValueKind != JsonValueKind.String || !levels.TryAdd(id, level.Value.GetString() ?? ""))
                        throw new Refusal(RequestInvalid, "levels maps distinct BIM-IR level ids to Revit level names");
                }
            }

            var wallTypes = new List<RevitWallTypeBinding>();
            if (root.TryGetProperty("wall_types", out var typesElement) && typesElement.ValueKind != JsonValueKind.Null)
            {
                if (typesElement.ValueKind != JsonValueKind.Array) throw new Refusal(RequestInvalid, "wall_types must be an array");
                if (typesElement.GetArrayLength() > MaxBindings) throw new Refusal(MirrorDrift.TooLargeCode, "wall_types");
                foreach (var type in typesElement.EnumerateArray())
                {
                    if (type.ValueKind == JsonValueKind.Object) Unique(type, "wall_types[].");
                    if (type.ValueKind != JsonValueKind.Object || type.EnumerateObject().Any(member => member.Name != "thickness_metres" && member.Name != "type")
                        || !type.TryGetProperty("thickness_metres", out var thickness) || thickness.ValueKind != JsonValueKind.Number || !thickness.TryGetDouble(out var metres) || !double.IsFinite(metres)
                        || !type.TryGetProperty("type", out var name) || name.ValueKind != JsonValueKind.String)
                        throw new Refusal(RequestInvalid, "wall_types entries are {\"thickness_metres\":number,\"type\":string}");
                    wallTypes.Add(new RevitWallTypeBinding(metres, name.GetString() ?? ""));
                }
            }

            string? journal = null;
            if (root.TryGetProperty("journal", out var journalElement) && journalElement.ValueKind != JsonValueKind.Null)
            {
                if (journalElement.ValueKind != JsonValueKind.Object) throw new Refusal(RequestInvalid, "journal must be an object or null");
                journal = journalElement.GetRawText();
            }

            return new Mirror(document, projectId, revision, map.GetRawText(), levels, wallTypes, OptionalString("door_symbol"), OptionalString("window_symbol"), journal, ParseOptions(root));
        }
        catch (JsonException) { throw new Refusal(RequestInvalid, "mirror is not JSON"); }
        catch (InvalidOperationException) { throw new Refusal(RequestInvalid, "mirror value type"); }
    }

    static MirrorDriftOptions ParseOptions(JsonElement root)
    {
        var options = new MirrorDriftOptions();
        if (!root.TryGetProperty("options", out var element) || element.ValueKind == JsonValueKind.Null) return options;
        if (element.ValueKind != JsonValueKind.Object) throw new Refusal(RequestInvalid, "options must be an object");
        Unique(element, "options.");
        foreach (var member in element.EnumerateObject())
        {
            if (!OptionMembers.Contains(member.Name)) throw new Refusal(RequestInvalid, "unknown member options." + member.Name);
            var value = member.Value;
            Refusal Bad() => new(MirrorDriftOptions.InvalidCode, "options." + member.Name);
            options = member.Name switch
            {
                "length_tolerance_mm" => options with { LengthToleranceMm = value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var length) ? length : throw Bad() },
                "angle_tolerance_deg" => options with { AngleToleranceDeg = value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var angle) ? angle : throw Bad() },
                "journal_after_sequence" => options with { JournalAfterSequence = value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var after) ? after : throw Bad() },
                "max_findings" => options with { MaxFindings = value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var max) ? max : throw Bad() },
                _ => options with { Scope = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : throw Bad() },
            };
        }
        return options;
    }
}
