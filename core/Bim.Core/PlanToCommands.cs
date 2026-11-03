using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Bim.Core;

// P3-02: bridge from an ExecutionPlan (typically Compiler.Diff(empty, IntentCompiler.Compile(...))) to an ordered list
// of catalogue v1 Revit commands (ADR0009). It only consumes RevitCommandCatalogue and RevitCommandProtocol: every
// emitted step is parsed by RevitCommandProtocol.Parse, so it passes the same schema, unit and geometry rules as a
// command sent by Claude. Nothing is dropped silently: every plan operation becomes exactly one step, one level
// precondition or one unsupported entry. Contract: docs/contracts/module-contracts.md, "Plan to catalogue commands".

// A Revit wall type name or UniqueId for every BIM-IR wall of this thickness (BIM-IR walls carry no type name).
public sealed record RevitWallTypeBinding(double ThicknessMetres, string Type);

// What BIM-IR cannot say about the target document: the existing Revit level for each BIM-IR level id (catalogue v1
// has no level command), wall types by thickness, and the door and window family types to place.
public sealed record RevitBindings(RevitDocumentRef Document, IReadOnlyDictionary<Guid, string> Levels, IReadOnlyList<RevitWallTypeBinding> WallTypes,
    string? DoorSymbol, string? WindowSymbol)
{
    // Levels by intent level name, wall types by intent wall type name. A thickness used by wall types with different
    // names is ambiguous and stays unbound, so its walls are reported as wall_type_unbound.
    public static RevitBindings FromIntent(Guid projectId, LayoutIntent intent, RevitDocumentRef document, string? doorSymbol, string? windowSymbol)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var levels = new Dictionary<Guid, string>();
        foreach (var level in intent.Levels.OrderBy(item => item.Key, StringComparer.Ordinal))
            levels.TryAdd(IntentCompiler.Id(projectId, IntentCompiler.LevelKey(level.Key)), level.Name);
        var types = intent.WallTypes.GroupBy(type => type.ThicknessMetres)
            .Where(group => group.Select(type => type.Name).Distinct(StringComparer.Ordinal).Count() == 1)
            .OrderBy(group => group.Key)
            .Select(group => new RevitWallTypeBinding(group.Key, group.First().Name)).ToArray();
        return new RevitBindings(document, levels, types, doorSymbol, windowSymbol);
    }
}

// A value of a step's args that is only known after an earlier step ran: Field (JSON path, "args.<member>") takes the
// UniqueId at Select ("created[0]") of the response of the step FromOperation.
public sealed record RevitStepInput(string Field, string FromOperation, string Select);

// ArgsJson is canonical (RevitCommandProtocol.Canonical). Unmapped names BIM-IR fields the command cannot express.
public sealed record RevitCommandStep(string OperationId, string Command, Guid? ElementId, string? ElementKind, IReadOnlyList<string> DependsOn,
    string ArgsJson, IReadOnlyList<RevitStepInput> Inputs, IReadOnlyList<string> Unmapped);

// A level the steps rely on: it must already exist in the document as Level. Action is the plan action ("create")
// when the level is a plan operation, null when it is only referenced; ElevationMm is known only for plan operations.
// CheckOperation is the query.levels step whose readback PlanToCommands.CheckPreconditions checks it against (null
// when no step was emitted).
public sealed record RevitLevelPrecondition(Guid ElementId, string? Action, string Level, double? ElevationMm, string? CheckOperation);

// A precondition the query.levels readback contradicts.
public sealed record RevitPreconditionIssue(Guid ElementId, string Level, string Code, string Detail);

public sealed record RevitUnsupportedElement(Guid ElementId, string ElementKind, string Action, string Code, string Detail);

public static class PlanToCommandsCodes
{
    public const string ActionUnsupported = "action_unsupported";
    public const string KindUnsupported = "kind_unsupported";
    public const string GeometryInvalid = "geometry_invalid";
    public const string LevelUnbound = "level_unbound";
    public const string WallTypeUnbound = "wall_type_unbound";
    public const string SymbolUnbound = "symbol_unbound";
    public const string HostUnresolved = "host_unresolved";
    public const string DependencyUnsupported = "dependency_unsupported";
    public const string CommandInvalid = "command_invalid";
    // CheckPreconditions against the query.levels readback.
    public const string LevelMissing = "level_missing";
    public const string LevelAmbiguous = "level_ambiguous";
    public const string LevelElevationMismatch = "level_elevation_mismatch";
}

public sealed record RevitCommandPlan(int Version, Guid ProjectId, int ExpectedRevision, string AfterHash, RevitDocumentRef Document,
    IReadOnlyList<RevitLevelPrecondition> Preconditions, IReadOnlyList<RevitCommandStep> Steps, IReadOnlyList<RevitUnsupportedElement> Unsupported)
{
    // Fixed member order, compact, default escaping: the same plan and bindings give byte-identical text.
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("bridge_version", Version);
            writer.WriteNumber("catalogue_version", RevitCommandCatalogue.Version);
            writer.WriteString("project_id", ProjectId.ToString("D"));
            writer.WriteNumber("expected_revision", ExpectedRevision);
            writer.WriteString("after_hash", AfterHash);
            writer.WriteStartObject("document");
            writer.WriteString("path", Document.Path);
            writer.WriteString("unique_id", Document.UniqueId);
            writer.WriteEndObject();
            writer.WriteStartArray("preconditions");
            foreach (var item in Preconditions)
            {
                writer.WriteStartObject();
                writer.WriteString("element_id", item.ElementId.ToString("D"));
                if (item.Action is null) writer.WriteNull("action"); else writer.WriteString("action", item.Action);
                writer.WriteString("level", item.Level);
                if (item.ElevationMm is { } elevation) writer.WriteNumber("elevation_mm", elevation); else writer.WriteNull("elevation_mm");
                if (item.CheckOperation is null) writer.WriteNull("check_operation"); else writer.WriteString("check_operation", item.CheckOperation);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("steps");
            foreach (var step in Steps)
            {
                writer.WriteStartObject();
                writer.WriteString("operation_id", step.OperationId);
                writer.WriteString("command", step.Command);
                if (step.ElementId is { } id) writer.WriteString("element_id", id.ToString("D")); else writer.WriteNull("element_id");
                if (step.ElementKind is null) writer.WriteNull("element_kind"); else writer.WriteString("element_kind", step.ElementKind);
                writer.WriteStartArray("depends_on");
                foreach (var dependency in step.DependsOn) writer.WriteStringValue(dependency);
                writer.WriteEndArray();
                writer.WritePropertyName("args");
                writer.WriteRawValue(step.ArgsJson);
                writer.WriteStartArray("inputs");
                foreach (var input in step.Inputs)
                {
                    writer.WriteStartObject();
                    writer.WriteString("field", input.Field);
                    writer.WriteString("from_operation", input.FromOperation);
                    writer.WriteString("select", input.Select);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("unmapped");
                foreach (var name in step.Unmapped) writer.WriteStringValue(name);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("unsupported");
            foreach (var item in Unsupported)
            {
                writer.WriteStartObject();
                writer.WriteString("element_id", item.ElementId.ToString("D"));
                writer.WriteString("element_kind", item.ElementKind);
                writer.WriteString("action", item.Action);
                writer.WriteString("code", item.Code);
                writer.WriteString("detail", item.Detail);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

public static class PlanToCommands
{
    public const int Version = 2;
    // Stands in for a host wall UniqueId until the wall step has run; RevitStepInput says where the real value comes
    // from. Revit never issues an all-zero UniqueId, so a placeholder sent by mistake fails with reference_not_found.
    public const string PlaceholderUniqueId = "00000000-0000-0000-0000-000000000000-00000000";
    // Metres: the farthest an opening centre may sit off its host axis or beyond its ends, and the least opening width.
    public const double Tolerance = 1e-6;
    // Metres: two wall type bindings, or a binding and a wall thickness, match within this.
    const double BindingTolerance = 1e-9;
    // Millimetres: a readback level elevation may differ this much from the BIM-IR elevation (Revit stores feet).
    public const double ElevationToleranceMm = 0.001;
    static readonly string[] OpeningUnmapped = { "height_m", "width_m" };

    // "op." + the first 16 bytes (lowercase hex) of SHA-256 over the UTF-8 text
    // "plan-to-commands/2\n<projectId:D>\n<expectedRevision>\n<afterHash>\n<elementId:D or empty>\n<command>\n
    //  <document unique_id>\n<document path>\n<canonical args>\n<input operation ids joined by ','>".
    // The canonical args leave out host_wall_uid while it holds PlaceholderUniqueId; the host comes in through the
    // input operation ids instead. So the id changes whenever the request it guards would change (another binding,
    // document, or host wall), and the same plan and bindings give the same ids (a retry replays).
    public static string OperationId(Guid projectId, int expectedRevision, string afterHash, Guid? elementId, string command,
        RevitDocumentRef document, string argsJson, IReadOnlyList<string> inputOperations)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(inputOperations);
        var args = JsonNode.Parse(argsJson) as JsonObject ?? throw new ArgumentException("args_not_object");
        if (args["host_wall_uid"] is JsonValue host && host.TryGetValue<string>(out var uid) && uid == PlaceholderUniqueId) args.Remove("host_wall_uid");
        string identity;
        using (var parsed = JsonDocument.Parse(args.ToJsonString())) identity = RevitCommandProtocol.Canonical(parsed.RootElement);
        var text = "plan-to-commands/" + Version.ToString(CultureInfo.InvariantCulture) + "\n" + projectId.ToString("D") + "\n"
            + expectedRevision.ToString(CultureInfo.InvariantCulture) + "\n" + afterHash + "\n" + (elementId?.ToString("D") ?? "") + "\n" + command + "\n"
            + document.UniqueId + "\n" + document.Path + "\n" + identity + "\n" + string.Join(",", inputOperations);
        return "op." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16).ToLowerInvariant();
    }

    // Every element of the compiled project is a create.
    public static RevitCommandPlan Translate(Project compiled, RevitBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        return Translate(Compiler.Diff(new Project(compiled.Id, compiled.Revision, Array.Empty<Element>()), compiled), bindings);
    }

    public static RevitCommandPlan Translate(ExecutionPlan plan, RevitBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(bindings.Document);
        ArgumentNullException.ThrowIfNull(bindings.Levels);
        ArgumentNullException.ThrowIfNull(bindings.WallTypes);
        if (!RevitCopiesGuard.IsAbsoluteNormalized(bindings.Document.Path)) throw new RevitCommandException(RevitCommandCodes.DocumentPathInvalid, "document.path", "absolute saved path without . or .. segments");
        if (!RevitCommandProtocol.IsUniqueId(bindings.Document.UniqueId)) throw new RevitCommandException(RevitCommandCodes.DocumentIdentityInvalid, "document.unique_id", "ProjectInformation UniqueId required");
        var wallTypes = bindings.WallTypes.ToArray();
        for (var i = 0; i < wallTypes.Length; i++)
            for (var j = i + 1; j < wallTypes.Length; j++)
                if (Math.Abs(wallTypes[i].ThicknessMetres - wallTypes[j].ThicknessMetres) <= BindingTolerance) throw new ArgumentException("wall_type_binding_duplicate");

        var operations = new Dictionary<Guid, Operation>();
        foreach (var operation in plan.Operations)
            if (!operations.TryAdd(operation.Element.Id, operation)) throw new ArgumentException("plan_duplicate_element");
        var ordered = operations.Values.OrderBy(operation => operation.Element.Id.ToString("D"), StringComparer.Ordinal).ToArray();
        string Id(Guid? elementId, string command, string canonical, IReadOnlyList<RevitStepInput> inputs) =>
            OperationId(plan.ProjectId, plan.ExpectedRevision, plan.AfterHash, elementId, command, bindings.Document, canonical, inputs.Select(input => input.FromOperation).ToArray());
        var levelsArgs = CanonicalArgs(Parse(bindings.Document, PendingOperationId, "query.levels", "{}"));
        var levelsCheck = Id(null, "query.levels", levelsArgs, Array.Empty<RevitStepInput>());

        var unsupported = new List<RevitUnsupportedElement>();
        var failed = new HashSet<Guid>();
        var emitted = new Dictionary<Guid, string>();
        var preconditions = new Dictionary<Guid, RevitLevelPrecondition>();
        var referencedLevels = new HashSet<Guid>();
        void Refuse(Operation operation, string code, string detail)
        {
            unsupported.Add(new RevitUnsupportedElement(operation.Element.Id, operation.Element.Kind, operation.Action, code, detail));
            failed.Add(operation.Element.Id);
        }
        bool Finite(Element element, int arity) => element.GeometryMetres is { } geometry && geometry.Length == arity && geometry.All(double.IsFinite);

        // ---- Classify: actions and kinds without a v1 command, and levels (bound to existing document levels) ----
        var walls = new List<Operation>();
        var openings = new List<Operation>();
        var spaces = new List<Operation>();
        foreach (var operation in ordered)
        {
            var element = operation.Element;
            if (operation.Action != "create") { Refuse(operation, PlanToCommandsCodes.ActionUnsupported, "catalogue v1 has no " + operation.Action + " command; only creates translate"); continue; }
            switch (element.Kind)
            {
                case "level":
                    if (!Finite(element, 1)) Refuse(operation, PlanToCommandsCodes.GeometryInvalid, "level geometry must be [elevation]");
                    else if (!bindings.Levels.TryGetValue(element.Id, out var reference)) Refuse(operation, PlanToCommandsCodes.LevelUnbound, "catalogue v1 cannot create levels and no existing Revit level is bound");
                    else preconditions[element.Id] = new RevitLevelPrecondition(element.Id, operation.Action, reference, Mm(element.GeometryMetres[0]), null);
                    break;
                case "wall": walls.Add(operation); break;
                case "door":
                case "window": openings.Add(operation); break;
                case "space": spaces.Add(operation); break;
                default: Refuse(operation, PlanToCommandsCodes.KindUnsupported, "no catalogue v1 command creates a " + element.Kind); break;
            }
        }

        // The Revit level of a wall or space: its first dependency that is a level (bound, or a level operation).
        // Null after reporting the element.
        (Guid Id, string Reference)? Level(Operation operation)
        {
            Guid? level = null;
            foreach (var dependency in operation.Element.Dependencies)
                if (bindings.Levels.ContainsKey(dependency) || (operations.TryGetValue(dependency, out var candidate) && candidate.Element.Kind == "level")) { level = dependency; break; }
            if (level is not { } levelId) { Refuse(operation, PlanToCommandsCodes.LevelUnbound, "level unbound: no bound or planned level among the dependencies"); return null; }
            if (!bindings.Levels.TryGetValue(levelId, out var reference)) { Refuse(operation, PlanToCommandsCodes.LevelUnbound, "level unbound: level " + levelId.ToString("D") + " has no Revit level binding"); return null; }
            if (failed.Contains(levelId)) { Refuse(operation, PlanToCommandsCodes.DependencyUnsupported, "level " + levelId.ToString("D") + " is unsupported"); return null; }
            return (levelId, reference);
        }

        // Canonical args once the catalogue accepts the command; otherwise reports command_invalid and returns null.
        string? Validate(Operation operation, string command, string args)
        {
            try { return CanonicalArgs(Parse(bindings.Document, PendingOperationId, command, args)); }
            catch (RevitCommandException error)
            {
                Refuse(operation, PlanToCommandsCodes.CommandInvalid, error.Code + "@" + (error.Field ?? "") + ": " + (error.Detail ?? ""));
                return null;
            }
        }

        RevitCommandStep Emit(Operation operation, string command, string canonical, IReadOnlyList<string> dependsOn, IReadOnlyList<RevitStepInput> inputs, IReadOnlyList<string> unmapped)
        {
            var operationId = Id(operation.Element.Id, command, canonical, inputs);
            emitted[operation.Element.Id] = operationId;
            return new RevitCommandStep(operationId, command, operation.Element.Id, operation.Element.Kind, dependsOn, canonical, inputs, unmapped);
        }

        // ---- Walls: wall.create with a straight axis ----
        var wallSteps = new List<RevitCommandStep>();
        var wallGeometry = new Dictionary<Guid, double[]>();
        foreach (var operation in walls)
        {
            var g = operation.Element.GeometryMetres;
            if (!Finite(operation.Element, 6) || !GeometryCodec.IsValidWall(g)) { Refuse(operation, PlanToCommandsCodes.GeometryInvalid, "wall geometry must be [ax,ay,bx,by,height,thickness]"); continue; }
            if (Level(operation) is not { } level) continue;
            var type = wallTypes.FirstOrDefault(binding => Math.Abs(binding.ThicknessMetres - g[5]) <= BindingTolerance);
            if (type is null) { Refuse(operation, PlanToCommandsCodes.WallTypeUnbound, "no Revit wall type bound for thickness " + g[5].ToString("R", CultureInfo.InvariantCulture) + " m"); continue; }
            var args = Json(writer =>
            {
                writer.WriteStartObject("curve");
                writer.WriteString("kind", "line");
                Point(writer, "start", g[0], g[1]);
                Point(writer, "end", g[2], g[3]);
                writer.WriteEndObject();
                writer.WriteNumber("height_mm", Mm(g[4]));
                writer.WriteString("level", level.Reference);
                writer.WriteString("type", type.Type);
            });
            if (Validate(operation, "wall.create", args) is not { } canonical) continue;
            wallSteps.Add(Emit(operation, "wall.create", canonical, new[] { levelsCheck }, Array.Empty<RevitStepInput>(), Array.Empty<string>()));
            wallGeometry[operation.Element.Id] = g;
            referencedLevels.Add(level.Id);
        }

        // ---- Hosted openings: opening.place on a wall created by this plan ----
        var openingSteps = new List<RevitCommandStep>();
        foreach (var operation in openings)
        {
            var element = operation.Element;
            var g = element.GeometryMetres;
            var door = element.Kind == "door";
            if (!Finite(element, 6) || !(g[5] > 0) || g[4] < 0) { Refuse(operation, PlanToCommandsCodes.GeometryInvalid, "opening geometry must be [ax,ay,bx,by,sill,height]"); continue; }
            if (Math.Sqrt((g[2] - g[0]) * (g[2] - g[0]) + (g[3] - g[1]) * (g[3] - g[1])) < Tolerance) { Refuse(operation, PlanToCommandsCodes.GeometryInvalid, "opening has no width along its host"); continue; }
            if (door && g[4] != 0) { Refuse(operation, PlanToCommandsCodes.GeometryInvalid, "opening.place gives doors no sill height; sill must be 0"); continue; }
            var symbol = door ? bindings.DoorSymbol : bindings.WindowSymbol;
            if (symbol is null) { Refuse(operation, PlanToCommandsCodes.SymbolUnbound, "no " + element.Kind + " family type bound"); continue; }
            var hosts = element.Dependencies.Where(dependency => operations.TryGetValue(dependency, out var candidate) && candidate.Element.Kind == "wall").ToArray();
            if (hosts.Length == 0) { Refuse(operation, PlanToCommandsCodes.HostUnresolved, "the host wall is not created by this plan, so its UniqueId is unknown"); continue; }
            var host = hosts[0];
            if (!emitted.TryGetValue(host, out var hostOperation)) { Refuse(operation, PlanToCommandsCodes.DependencyUnsupported, "host wall " + host.ToString("D") + " is unsupported"); continue; }
            var h = wallGeometry[host];
            double dx = h[2] - h[0], dy = h[3] - h[1];
            var length = Math.Sqrt(dx * dx + dy * dy);
            double mx = (g[0] + g[2]) / 2 - h[0], my = (g[1] + g[3]) / 2 - h[1];
            var along = (mx * dx + my * dy) / length;
            var off = Math.Abs(mx * dy - my * dx) / length;
            if (off > Tolerance || along < -Tolerance || along > length + Tolerance) { Refuse(operation, PlanToCommandsCodes.GeometryInvalid, "opening centre is not on its host wall axis"); continue; }
            var args = Json(writer =>
            {
                writer.WriteString("category", element.Kind);
                writer.WriteString("host_wall_uid", PlaceholderUniqueId);
                writer.WriteNumber("position_along_mm", Mm(Math.Clamp(along, 0, length)));
                if (!door) writer.WriteNumber("sill_mm", Mm(g[4]));
                writer.WriteString("symbol", symbol);
            });
            if (Validate(operation, "opening.place", args) is not { } canonical) continue;
            openingSteps.Add(Emit(operation, "opening.place", canonical, new[] { hostOperation }, new[] { new RevitStepInput("args.host_wall_uid", hostOperation, "created[0]") }, OpeningUnmapped));
        }

        // ---- Rooms: room.create at the interior centre, after the walls that bound it ----
        var roomSteps = new List<RevitCommandStep>();
        foreach (var operation in spaces)
        {
            var g = operation.Element.GeometryMetres;
            if (!Finite(operation.Element, 4) || !(g[2] > 0) || !(g[3] > 0)) { Refuse(operation, PlanToCommandsCodes.GeometryInvalid, "space geometry must be [x,y,width,depth]"); continue; }
            var bounding = operation.Element.Dependencies.Where(dependency => operations.TryGetValue(dependency, out var candidate) && candidate.Element.Kind == "wall").ToArray();
            if (Level(operation) is not { } level) continue;
            var args = Json(writer =>
            {
                writer.WriteString("level", level.Reference);
                Point(writer, "point", g[0] + g[2] / 2, g[1] + g[3] / 2);
            });
            // The command's own refusal is reported before a missing bounding wall.
            if (Validate(operation, "room.create", args) is not { } canonical) continue;
            var lost = bounding.Where(failed.Contains).ToArray();
            if (lost.Length > 0) { Refuse(operation, PlanToCommandsCodes.DependencyUnsupported, "bounding wall " + lost[0].ToString("D") + " is unsupported"); continue; }
            var dependsOn = new[] { levelsCheck }.Concat(bounding.Select(wall => emitted[wall]).OrderBy(id => id, StringComparer.Ordinal)).ToArray();
            roomSteps.Add(Emit(operation, "room.create", canonical, dependsOn, Array.Empty<RevitStepInput>(), Array.Empty<string>()));
            referencedLevels.Add(level.Id);
        }

        var steps = new List<RevitCommandStep>();
        if (wallSteps.Count > 0 || roomSteps.Count > 0)
            steps.Add(new RevitCommandStep(levelsCheck, "query.levels", null, null, Array.Empty<string>(), levelsArgs, Array.Empty<RevitStepInput>(), Array.Empty<string>()));
        steps.AddRange(wallSteps);
        steps.AddRange(openingSteps);
        steps.AddRange(roomSteps);
        foreach (var level in referencedLevels.Where(level => !preconditions.ContainsKey(level)))
            preconditions[level] = new RevitLevelPrecondition(level, null, bindings.Levels[level], null, null);
        var check = steps.Count > 0 ? levelsCheck : null;
        return new RevitCommandPlan(Version, plan.ProjectId, plan.ExpectedRevision, plan.AfterHash, bindings.Document,
            preconditions.Values.OrderBy(item => item.ElementId.ToString("D"), StringComparer.Ordinal).Select(item => item with { CheckOperation = check }).ToArray(), steps,
            unsupported.OrderBy(item => item.ElementId.ToString("D"), StringComparer.Ordinal).ToArray());
    }

    // The full protocol envelope of one step, with its inputs filled from the UniqueIds created by earlier steps
    // (operation id -> created[0]), parsed and validated again by RevitCommandProtocol.Parse.
    public static RevitCommandEnvelope Envelope(RevitCommandPlan plan, RevitCommandStep step, IReadOnlyDictionary<string, string> createdByOperation)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(createdByOperation);
        var args = JsonNode.Parse(step.ArgsJson) as JsonObject ?? throw new ArgumentException("args_not_object");
        foreach (var input in step.Inputs)
        {
            if (!input.Field.StartsWith("args.", StringComparison.Ordinal) || input.Field.Length == "args.".Length || input.Field.IndexOf('.', "args.".Length) >= 0)
                throw new ArgumentException("input_field_unsupported");
            if (input.Select != "created[0]") throw new ArgumentException("input_select_unsupported");
            if (!step.DependsOn.Contains(input.FromOperation, StringComparer.Ordinal)) throw new ArgumentException("input_not_dependency");
            if (!createdByOperation.TryGetValue(input.FromOperation, out var uniqueId)) throw new ArgumentException("input_unresolved");
            args[input.Field["args.".Length..]] = uniqueId;
        }
        return Parse(plan.Document, step.OperationId, step.Command, args.ToJsonString());
    }

    // Checks the plan's level preconditions against the readback of its query.levels step ({"levels": [{name,
    // unique_id, elevation_mm, ...}]}). A reference that is a UniqueId matches unique_id ignoring case, otherwise it
    // matches name exactly, as the executor resolves it. A level must match exactly one row; a precondition with an
    // elevation must also be within ElevationToleranceMm of elevation_mm. Empty when all hold.
    public static IReadOnlyList<RevitPreconditionIssue> CheckPreconditions(RevitCommandPlan plan, string levelsReadbackJson)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var rows = new List<(string Name, string UniqueId, double ElevationMm)>();
        try
        {
            using var readback = JsonDocument.Parse(levelsReadbackJson);
            if (readback.RootElement.ValueKind != JsonValueKind.Object || !readback.RootElement.TryGetProperty("levels", out var levels) || levels.ValueKind != JsonValueKind.Array)
                throw new RevitCommandException(RevitCommandCodes.ResponseInvalid, "readback.levels", "array of levels required");
            foreach (var row in levels.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                    || !row.TryGetProperty("unique_id", out var uid) || uid.ValueKind != JsonValueKind.String
                    || !row.TryGetProperty("elevation_mm", out var elevation) || elevation.ValueKind != JsonValueKind.Number || !elevation.TryGetDouble(out var elevationMm) || !double.IsFinite(elevationMm))
                    throw new RevitCommandException(RevitCommandCodes.ResponseInvalid, "readback.levels", "each level needs name, unique_id and a finite elevation_mm");
                rows.Add((name.GetString()!, uid.GetString()!, elevationMm));
            }
        }
        catch (JsonException error) { throw new RevitCommandException(RevitCommandCodes.ResponseInvalid, "readback", error.Message); }
        var issues = new List<RevitPreconditionIssue>();
        foreach (var precondition in plan.Preconditions)
        {
            var byUniqueId = RevitCommandProtocol.IsUniqueId(precondition.Level);
            var matches = rows.Where(row => byUniqueId ? string.Equals(row.UniqueId, precondition.Level, StringComparison.OrdinalIgnoreCase) : row.Name == precondition.Level).ToArray();
            if (matches.Length == 0) issues.Add(new RevitPreconditionIssue(precondition.ElementId, precondition.Level, PlanToCommandsCodes.LevelMissing, "no level " + precondition.Level + " in the document"));
            else if (matches.Length > 1) issues.Add(new RevitPreconditionIssue(precondition.ElementId, precondition.Level, PlanToCommandsCodes.LevelAmbiguous, matches.Length.ToString(CultureInfo.InvariantCulture) + " levels match " + precondition.Level + "; bind the UniqueId"));
            else if (precondition.ElevationMm is { } expected && !(Math.Abs(matches[0].ElevationMm - expected) <= ElevationToleranceMm))
                issues.Add(new RevitPreconditionIssue(precondition.ElementId, precondition.Level, PlanToCommandsCodes.LevelElevationMismatch,
                    "elevation " + matches[0].ElevationMm.ToString("R", CultureInfo.InvariantCulture) + " mm, BIM-IR " + expected.ToString("R", CultureInfo.InvariantCulture) + " mm"));
        }
        return issues;
    }

    // Operation id used only while the catalogue validates args; the real id is derived from the canonical args.
    const string PendingOperationId = "op.pending";

    static RevitCommandEnvelope Parse(RevitDocumentRef document, string operationId, string command, string args) => RevitCommandProtocol.Parse(Json(writer =>
    {
        writer.WriteNumber("protocol_version", RevitCommandProtocol.Version);
        writer.WriteString("operation_id", operationId);
        writer.WriteStartObject("document");
        writer.WriteString("path", document.Path);
        writer.WriteString("unique_id", document.UniqueId);
        writer.WriteEndObject();
        writer.WriteString("command", command);
        writer.WritePropertyName("args");
        writer.WriteRawValue(args);
    }));

    static string CanonicalArgs(RevitCommandEnvelope envelope)
    {
        using var canonical = JsonDocument.Parse(envelope.CanonicalJson);
        return canonical.RootElement.GetProperty("args").GetRawText();
    }

    static string Json(Action<Utf8JsonWriter> members)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            members(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void Point(Utf8JsonWriter writer, string name, double xMetres, double yMetres)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(Mm(xMetres));
        writer.WriteNumberValue(Mm(yMetres));
        writer.WriteEndArray();
    }

    // Metres to millimetres, rounded to 1e-6 mm so binary noise (1099.9999999999998) does not reach the command; -0 is 0.
    static double Mm(double metres)
    {
        var millimetres = Math.Round(metres * Length.MillimetresPerMetre, 6, MidpointRounding.ToEven);
        return millimetres == 0 ? 0.0 : millimetres;
    }
}
