using Bim.Core;
using System.Globalization;
using System.Text.Json;
// P3-02 oracle: an ExecutionPlan (or a compiled layout intent) becomes an ordered list of catalogue v1 Revit commands
// with stable operation ids, dependency order (levels, walls, hosted openings, rooms), every command validated by the
// catalogue, unsupported elements reported explicitly and byte-identical JSON for the same input.
int passed = 0, failed = 0;
var inv = CultureInfo.InvariantCulture;
var provenance = new Provenance("plan-to-commands-tests", "unit");
var projectId = Guid.Parse("3d0c8a5e-6f1b-4c2d-9e7a-1b2c3d4e5f60");
var document = new RevitDocumentRef(@"C:\Repo\.bim\native-runs\p3-02\copy.rvt", "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b");

void Check(bool condition, string name, string detail = "")
{
    if (condition) { Console.WriteLine("PASS " + name); passed++; return; }
    Console.WriteLine("::error::plan-to-commands FAIL " + name + (detail.Length > 0 ? " | " + detail : ""));
    failed++;
}
string? Code(Action action)
{
    try { action(); return null; }
    catch (RevitCommandException error) { return error.Code; }
    catch (ArgumentException error) { return "argument:" + error.Message; }
    catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
}
IntentRoom Rm(string key, double x, double y, double width, double depth, string level = "l1") => new(key, "Room " + key, level, "std", x, y, width, depth);
IntentOpening Op(string key, IntentOpeningKind kind, IntentHost host, double offset, double width, double height, double sill) => new(key, kind, host, offset, width, height, sill);
var levels = new[] { new IntentLevel("l1", "L1", 0), new IntentLevel("l2", "L2", 3) };
var types = new[] { new IntentWallType("std", "Generic 200", 0.2, 2.8) };
LayoutIntent Layout(IntentRoom[] rooms, IntentOpening[] openings) => new(LayoutIntent.CurrentVersion, provenance, levels, types, rooms, openings);
Guid Id(string key) => IntentCompiler.Id(projectId, key);

// Level l1: a (4x3) and b (3x3) side by side; level l2: u alone. 11 walls (7 on l1 incl. shared a|b, 4 on l2), 3 spaces.
var rooms = new[] { Rm("a", 0, 0, 4, 3), Rm("b", 4, 0, 3, 3), Rm("u", 0, 0, 4, 3, "l2") };
var openings = new[]
{
    Op("d1", IntentOpeningKind.Door, new IntentSharedWall("b", "a"), 1, 0.9, 2.1, 0),
    Op("w1", IntentOpeningKind.Window, new IntentRoomSide("b", IntentSide.South), 0.5, 1.2, 1.2, 0.9),
    Op("w3", IntentOpeningKind.Window, new IntentRoomSide("a", IntentSide.West), 1, 1, 1.2, 0.9),
};
var intent = Layout(rooms, openings);
var compiled = IntentCompiler.Compile(projectId, 5, intent);
var empty = new Project(projectId, 5, Array.Empty<Element>());
var plan = Compiler.Diff(empty, compiled);
var bindings = RevitBindings.FromIntent(projectId, intent, document, "Single-Flush: 0915 x 2134mm", "Fixed: 0915 x 1220mm");

string Show(RevitCommandPlan result) => "steps " + string.Join(",", result.Steps.Select(step => step.Command)) +
    " unsupported " + string.Join(",", result.Unsupported.Select(item => item.Code + "@" + item.ElementId.ToString("D")));
RevitCommandStep? StepOf(RevitCommandPlan result, string key) => result.Steps.FirstOrDefault(step => step.ElementId == Id(key));
JsonElement ArgsOf(RevitCommandStep step) => JsonDocument.Parse(step.ArgsJson).RootElement;
double Num(JsonElement element, string name) => element.GetProperty(name).GetDouble();
bool Pair(JsonElement element, double x, double y) => element.GetArrayLength() == 2 && Math.Abs(element[0].GetDouble() - x) <= 1e-6 && Math.Abs(element[1].GetDouble() - y) <= 1e-6;
string ShowPair(JsonElement element) => "[" + string.Join(",", element.EnumerateArray().Select(value => value.GetDouble().ToString("R", inv))) + "]";
// Every plan operation is accounted for exactly once: as a step, a level precondition or an unsupported entry.
bool Accounted(ExecutionPlan source, RevitCommandPlan result)
{
    var reported = result.Steps.Where(step => step.ElementId is not null).Select(step => step.ElementId!.Value)
        .Concat(result.Preconditions.Where(item => item.Action is not null).Select(item => item.ElementId))
        .Concat(result.Unsupported.Select(item => item.ElementId)).ToArray();
    return reported.Length == source.Operations.Length && reported.ToHashSet().SetEquals(source.Operations.Select(operation => operation.Element.Id));
}
// Fake created UniqueIds for every write step, so each step can be turned into a full envelope.
Dictionary<string, string> Created(RevitCommandPlan result) => result.Steps.Select((step, i) => (step, i))
    .ToDictionary(pair => pair.step.OperationId, pair => "60f91daf-3dd7-4283-a86d-24137b73f3da-" + (0x100 + pair.i).ToString("x8", inv), StringComparer.Ordinal);

try
{
    // ---- Operation id scheme ----
    Check(PlanToCommands.OperationId(projectId, 5, "ABCDEF", Guid.Parse("254784a6-edd3-3254-203a-b0172d136dd3"), "wall.create") == "op.452976a9c6d33ce68b59edae152ae048",
        "golden operation id pins op. + SHA-256(\"plan-to-commands/1\\n<project:D>\\n<revision>\\n<after hash>\\n<element:D>\\n<command>\")[0..16] hex");
    Check(PlanToCommands.OperationId(projectId, 5, "ABCDEF", null, "query.levels") == "op.0eb148f620fe3d09726167772bf752eb", "golden operation id of a step without element uses an empty element id");
    Check(RevitCommandProtocol.IsOperationId(PlanToCommands.OperationId(projectId, 5, "ABCDEF", null, "query.levels")), "operation ids satisfy the protocol pattern");

    // ---- Valid multi-room translation ----
    var result = PlanToCommands.Translate(plan, bindings);
    Check(result.Unsupported.Count == 0, "base layout has no unsupported element", Show(result));
    Check(result.Steps.Count == 18, "1 query.levels + 11 wall.create + 3 opening.place + 3 room.create", Show(result));
    var commands = result.Steps.Select(step => step.Command).ToArray();
    Check(commands.Length > 0 && commands[0] == "query.levels" && commands.Count(command => command == "query.levels") == 1, "levels come first, as one read-only query.levels check", Show(result));
    int Last(string command) => Array.LastIndexOf(commands, command);
    int First(string command) => Array.IndexOf(commands, command);
    Check(commands.Count(c => c == "wall.create") == 11 && commands.Count(c => c == "opening.place") == 3 && commands.Count(c => c == "room.create") == 3
        && Last("query.levels") < First("wall.create") && Last("wall.create") < First("opening.place") && Last("opening.place") < First("room.create"),
        "dependency order: levels, then walls, then hosted openings, then rooms", Show(result));
    Check(result.Preconditions.Count == 2 && result.Preconditions.All(item => item.Action == "create")
        && result.Preconditions.Single(item => item.ElementId == Id("level:l1")).Level == "L1" && result.Preconditions.Single(item => item.ElementId == Id("level:l2")).Level == "L2"
        && result.Preconditions.Single(item => item.ElementId == Id("level:l2")).ElevationMm == 3000,
        "catalogue v1 cannot create levels: bound levels become explicit preconditions with their elevation",
        string.Join(",", result.Preconditions.Select(item => item.Level + "=" + item.ElevationMm?.ToString("R", inv))));
    Check(Accounted(plan, result), "every plan operation is a step, a precondition or unsupported, exactly once");
    Check(result.Steps.Select(step => step.OperationId).Distinct(StringComparer.Ordinal).Count() == result.Steps.Count && result.Steps.All(step => RevitCommandProtocol.IsOperationId(step.OperationId)),
        "operation ids are unique and protocol-valid");
    Check(result.Steps.All(step => step.OperationId == PlanToCommands.OperationId(projectId, 5, plan.AfterHash, step.ElementId, step.Command)),
        "each operation id is derived from project, revision, plan after-hash, element id and command");
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var ordered = true;
    foreach (var step in result.Steps) { ordered &= step.DependsOn.All(seen.Contains); seen.Add(step.OperationId); }
    Check(ordered, "every dependency names an earlier step");
    Check(result.ProjectId == projectId && result.ExpectedRevision == 5 && result.AfterHash == plan.AfterHash && result.Document == document && result.Version == PlanToCommands.Version,
        "result carries the plan identity and target document");

    // ---- Every command passes the catalogue validators ----
    var created = Created(result);
    var envelopeFailures = new List<string>();
    foreach (var step in result.Steps)
    {
        var problem = Code(() =>
        {
            var envelope = PlanToCommands.Envelope(result, step, created);
            if (envelope.OperationId != step.OperationId || envelope.Command != step.Command || envelope.Document != document) throw new InvalidOperationException("envelope identity");
            var reparsed = RevitCommandProtocol.Parse(envelope.CanonicalJson);
            if (reparsed.CanonicalJson != envelope.CanonicalJson) throw new InvalidOperationException("not canonical");
        });
        if (problem is not null) envelopeFailures.Add(step.Command + ":" + problem);
    }
    Check(envelopeFailures.Count == 0, "every step becomes a protocol envelope accepted by RevitCommandProtocol.Parse", string.Join(";", envelopeFailures));
    Check(result.Steps.All(step => RevitCommandCatalogue.V1.Any(spec => spec.Name == step.Command)), "only catalogue v1 commands are emitted");

    // ---- Walls ----
    var shared = StepOf(result, "wall:a|b");
    Check(shared is not null && shared.Command == "wall.create" && shared.ElementKind == "wall", "shared wall a|b becomes one wall.create");
    if (shared is not null)
    {
        var values = ArgsOf(shared);
        var curve = values.GetProperty("curve");
        Check(values.GetProperty("level").GetString() == "L1" && values.GetProperty("type").GetString() == "Generic 200" && Num(values, "height_mm") == 2800
            && curve.GetProperty("kind").GetString() == "line" && Pair(curve.GetProperty("start"), 4000, 0) && Pair(curve.GetProperty("end"), 4000, 3000),
            "wall args: bound level and type names, height and axis line in millimetres", shared.ArgsJson);
        Check(!values.TryGetProperty("base_offset_mm", out _) && !values.TryGetProperty("structural", out _), "defaulted wall fields are omitted, never written with defaults", shared.ArgsJson);
        Check(shared.DependsOn.SequenceEqual(new[] { result.Steps[0].OperationId }), "a wall depends on the level check");
    }
    var aWest = StepOf(result, "wall:a:west");
    Check(aWest is not null && Pair(ArgsOf(aWest).GetProperty("curve").GetProperty("start"), 0, 3000) && Pair(ArgsOf(aWest).GetProperty("curve").GetProperty("end"), 0, 0),
        "wall direction follows BIM-IR (a west runs north to south)", aWest?.ArgsJson ?? "missing");
    var uSouth = StepOf(result, "wall:u:south");
    Check(uSouth is not null && ArgsOf(uSouth).GetProperty("level").GetString() == "L2", "walls on l2 reference L2", uSouth?.ArgsJson ?? "missing");

    // ---- Openings ----
    var door = StepOf(result, "opening:d1");
    Check(door is not null && door.Command == "opening.place" && door.ElementKind == "door", "door becomes opening.place");
    if (door is not null && shared is not null)
    {
        var values = ArgsOf(door);
        Check(values.GetProperty("category").GetString() == "door" && values.GetProperty("symbol").GetString() == "Single-Flush: 0915 x 2134mm"
            && Math.Abs(Num(values, "position_along_mm") - 1450) <= 1e-6 && !values.TryGetProperty("sill_mm", out _),
            "door args: category, bound symbol, centre distance from host start (1000 + 900/2), no sill", door.ArgsJson);
        Check(values.GetProperty("host_wall_uid").GetString() == PlanToCommands.PlaceholderUniqueId, "the host UniqueId is a documented placeholder until the wall exists", door.ArgsJson);
        Check(door.Inputs.Count == 1 && door.Inputs[0] == new RevitStepInput("args.host_wall_uid", shared.OperationId, "created[0]"),
            "the host is bound to the wall step's first created UniqueId", string.Join(",", door.Inputs));
        Check(door.DependsOn.SequenceEqual(new[] { shared.OperationId }), "an opening depends on its host wall step");
        Check(door.Unmapped.SequenceEqual(new[] { "height_m", "width_m" }), "opening size is reported as not expressible (the family type sets it)", string.Join(",", door.Unmapped));
        var envelope = PlanToCommands.Envelope(result, door, created);
        Check(envelope.Args is OpeningPlaceArgs { HostWallUid: var host } && host == created[shared.OperationId], "Envelope substitutes the created host UniqueId");
        Check(Code(() => PlanToCommands.Envelope(result, door, new Dictionary<string, string>())) == "argument:input_unresolved", "Envelope refuses a step whose input is not resolved");
        Check(Code(() => PlanToCommands.Envelope(result, door, new Dictionary<string, string> { [shared.OperationId] = "not-a-uid" })) == RevitCommandCodes.ArgsInvalid,
            "Envelope revalidates the substituted value");
    }
    var w1 = StepOf(result, "opening:w1");
    Check(w1 is not null && ArgsOf(w1).GetProperty("category").GetString() == "window" && ArgsOf(w1).GetProperty("symbol").GetString() == "Fixed: 0915 x 1220mm"
        && Math.Abs(Num(ArgsOf(w1), "position_along_mm") - 1100) <= 1e-6 && Num(ArgsOf(w1), "sill_mm") == 900,
        "window args: sill in millimetres and centre distance (500 + 1200/2) from host start", w1?.ArgsJson ?? "missing");
    var w3 = StepOf(result, "opening:w3");
    var aWestId = aWest?.OperationId;
    Check(w3 is not null && aWestId is not null && Math.Abs(Num(ArgsOf(w3), "position_along_mm") - 1500) <= 1e-6 && w3.DependsOn.SequenceEqual(new[] { aWestId }),
        "a window on a reversed wall measures from that wall's start (north end)", w3?.ArgsJson ?? "missing");

    // ---- Rooms ----
    var roomA = StepOf(result, "space:a");
    Check(roomA is not null && roomA.Command == "room.create" && roomA.ElementKind == "space", "space a becomes room.create");
    if (roomA is not null)
    {
        var values = ArgsOf(roomA);
        Check(values.GetProperty("level").GetString() == "L1" && Pair(values.GetProperty("point"), 2000, 1500) && !values.TryGetProperty("name", out _) && !values.TryGetProperty("number", out _),
            "room args: level and the interior centre in millimetres; BIM-IR has no name or number", roomA.ArgsJson + " " + ShowPair(values.GetProperty("point")));
        var expected = new[] { "wall:a:south", "wall:a:west", "wall:a:north", "wall:a|b" }.Select(key => StepOf(result, key)?.OperationId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        Check(roomA.DependsOn.Count == 5 && roomA.DependsOn[0] == result.Steps[0].OperationId && roomA.DependsOn.Skip(1).ToHashSet().SetEquals(expected),
            "a room depends on the level check and its bounding wall steps", string.Join(",", roomA.DependsOn));
    }

    // ---- Determinism ----
    var json = result.ToJson();
    Check(json == PlanToCommands.Translate(plan, bindings).ToJson(), "same input gives byte-identical JSON");
    var reversed = plan with { Operations = plan.Operations.Reverse().ToArray() };
    Check(PlanToCommands.Translate(reversed, bindings).ToJson() == json, "operation order in the plan does not change the output");
    Check(PlanToCommands.Translate(compiled, bindings).ToJson() == json, "translating the compiled project equals translating its all-create plan");
    var shuffled = intent with { Rooms = rooms.Reverse().ToArray(), Openings = openings.Reverse().ToArray(), Levels = levels.Reverse().ToArray() };
    Check(PlanToCommands.Translate(IntentCompiler.Compile(projectId, 5, shuffled), RevitBindings.FromIntent(projectId, shuffled, document, "Single-Flush: 0915 x 2134mm", "Fixed: 0915 x 1220mm")).ToJson() == json,
        "intent input order does not change the output");
    using (var parsed = JsonDocument.Parse(json))
    {
        var root = parsed.RootElement;
        Check(string.Join(",", root.EnumerateObject().Select(property => property.Name)) == "bridge_version,catalogue_version,project_id,expected_revision,after_hash,document,preconditions,steps,unsupported",
            "JSON top-level members in fixed order", string.Join(",", root.EnumerateObject().Select(property => property.Name)));
        var first = root.GetProperty("steps")[0];
        Check(string.Join(",", first.EnumerateObject().Select(property => property.Name)) == "operation_id,command,element_id,element_kind,depends_on,args,inputs,unmapped"
            && first.GetProperty("element_id").ValueKind == JsonValueKind.Null && root.GetProperty("catalogue_version").GetInt32() == RevitCommandCatalogue.Version,
            "JSON step members in fixed order; the level check has no element", first.GetRawText());
        Check(root.GetProperty("steps").EnumerateArray().All(step => step.GetProperty("args").GetRawText() == RevitCommandProtocol.Canonical(step.GetProperty("args"))),
            "step args are canonical JSON");
    }

    // ---- Unsupported elements are reported, never dropped ----
    var noWindow = PlanToCommands.Translate(plan, bindings with { WindowSymbol = null });
    Check(noWindow.Steps.Count == 16 && noWindow.Unsupported.Count == 2 && noWindow.Unsupported.All(item => item.Code == PlanToCommandsCodes.SymbolUnbound && item.ElementKind == "window" && item.Action == "create")
        && Accounted(plan, noWindow), "windows without a bound symbol are unsupported (symbol_unbound); the rest still translates", Show(noWindow));

    var noL2 = PlanToCommands.Translate(plan, bindings with { Levels = bindings.Levels.Where(pair => pair.Key != Id("level:l2")).ToDictionary(pair => pair.Key, pair => pair.Value) });
    Check(noL2.Unsupported.Count == 6 && noL2.Unsupported.All(item => item.Code == PlanToCommandsCodes.LevelUnbound)
        && noL2.Unsupported.Any(item => item.ElementId == Id("level:l2") && item.ElementKind == "level") && noL2.Unsupported.Any(item => item.ElementId == Id("space:u"))
        && noL2.Steps.Count == 13 && Accounted(plan, noL2),
        "an unbound level and everything on it are unsupported (level_unbound)", Show(noL2));

    var noType = PlanToCommands.Translate(plan, bindings with { WallTypes = Array.Empty<RevitWallTypeBinding>() });
    Check(noType.Steps.Count == 0 && noType.Unsupported.Count(item => item.Code == PlanToCommandsCodes.WallTypeUnbound) == 11
        && noType.Unsupported.Count(item => item.Code == PlanToCommandsCodes.DependencyUnsupported) == 6 && Accounted(plan, noType),
        "walls without a bound type are unsupported and their openings and rooms cascade (dependency_unsupported); no orphan level check", Show(noType));

    var badLevel = PlanToCommands.Translate(plan, bindings with { Levels = new Dictionary<Guid, string> { [Id("level:l1")] = "L1\u0001", [Id("level:l2")] = "L2" } });
    var refused = badLevel.Unsupported.Where(item => item.Code == PlanToCommandsCodes.CommandInvalid).ToArray();
    Check(refused.Length == 9 && refused.All(item => item.Detail.StartsWith(RevitCommandCodes.ArgsInvalid + "@args.level", StringComparison.Ordinal))
        && badLevel.Unsupported.Count(item => item.Code == PlanToCommandsCodes.DependencyUnsupported) == 3 && Accounted(plan, badLevel),
        "a command the catalogue validator refuses is unsupported (command_invalid) with the catalogue code and field; dependents cascade", Show(badLevel) + " " + string.Join(";", refused.Select(item => item.Detail)));

    // Updates and deletes have no catalogue v1 command.
    var smaller = IntentCompiler.Compile(projectId, 5, Layout(rooms.Where(room => room.Key != "u").ToArray(), openings));
    var deletes = PlanToCommands.Translate(Compiler.Diff(compiled, smaller), bindings);
    Check(deletes.Steps.Count == 0 && deletes.Unsupported.Count == 5 && deletes.Unsupported.All(item => item.Code == PlanToCommandsCodes.ActionUnsupported && item.Action == "delete"),
        "deletes are unsupported (action_unsupported)", Show(deletes));
    var moved = IntentCompiler.Compile(projectId, 5, Layout(rooms.Select(room => room.Key == "u" ? room with { WidthMetres = 5 } : room).ToArray(), openings));
    var updates = PlanToCommands.Translate(Compiler.Diff(compiled, moved), bindings);
    Check(updates.Steps.Count == 0 && updates.Unsupported.Count > 0 && updates.Unsupported.All(item => item.Code == PlanToCommandsCodes.ActionUnsupported && item.Action == "update"),
        "updates are unsupported (action_unsupported)", Show(updates));

    // Openings added to walls that already exist: their UniqueIds are unknown to the plan.
    var walled = IntentCompiler.Compile(projectId, 5, Layout(rooms, Array.Empty<IntentOpening>()));
    var addOpenings = Compiler.Diff(walled, compiled);
    var hostless = PlanToCommands.Translate(addOpenings, bindings);
    Check(hostless.Steps.Count == 0 && hostless.Unsupported.Count == 3 && hostless.Unsupported.All(item => item.Code == PlanToCommandsCodes.HostUnresolved) && Accounted(addOpenings, hostless),
        "an opening whose host wall is not created by the plan is unsupported (host_unresolved)", Show(hostless));

    // Kinds without a catalogue command, and geometry the catalogue refuses.
    var level = new Element(Id("level:l1"), "level", new[] { 0.0 }, Array.Empty<Guid>(), provenance);
    var floor = new Element(Guid.Parse("00000000-0000-0000-0000-0000000000f1"), "floor", new[] { 0.0, 0.0, 1, 1 }, new[] { level.Id }, provenance);
    var tiny = new Element(Guid.Parse("00000000-0000-0000-0000-0000000000a1"), "wall", new[] { 0, 0, 0.0005, 0, 2.8, 0.2 }, new[] { level.Id }, provenance);
    var odd = new Project(projectId, 5, new[] { level, floor, tiny });
    var oddPlan = Compiler.Diff(empty, odd);
    var oddResult = PlanToCommands.Translate(oddPlan, bindings);
    Check(oddResult.Unsupported.Any(item => item.ElementId == floor.Id && item.Code == PlanToCommandsCodes.KindUnsupported && item.ElementKind == "floor"),
        "a kind without a catalogue mapping is unsupported (kind_unsupported)", Show(oddResult));
    Check(oddResult.Unsupported.Any(item => item.ElementId == tiny.Id && item.Code == PlanToCommandsCodes.CommandInvalid && item.Detail.StartsWith(RevitCommandCodes.CurveTooShort, StringComparison.Ordinal)),
        "a 0.5 mm wall is refused by the catalogue geometry rules (curve_too_short)", Show(oddResult));
    Check(oddResult.Steps.Count == 0 && Accounted(oddPlan, oddResult), "nothing to run, everything reported", Show(oddResult));
    var badDoor = new Element(Guid.Parse("00000000-0000-0000-0000-0000000000d1"), "door", new[] { 1.0, 0, 2, 0, 0.5, 2.1 }, new[] { level.Id, Id("wall:x") }, provenance);
    var wallX = new Element(Id("wall:x"), "wall", new[] { 0.0, 0, 4, 0, 2.8, 0.2 }, new[] { level.Id }, provenance);
    var doorPlan = Compiler.Diff(empty, new Project(projectId, 5, new[] { level, wallX, badDoor }));
    var doorResult = PlanToCommands.Translate(doorPlan, bindings);
    Check(doorResult.Unsupported.Count == 1 && doorResult.Unsupported[0].ElementId == badDoor.Id && doorResult.Unsupported[0].Code == PlanToCommandsCodes.GeometryInvalid,
        "a door with a sill cannot be expressed by opening.place (geometry_invalid)", Show(doorResult));

    // ---- Bindings and argument checks ----
    var fromIntent = RevitBindings.FromIntent(projectId, intent, document, null, null);
    Check(fromIntent.Levels.Count == 2 && fromIntent.Levels[Id("level:l1")] == "L1" && fromIntent.WallTypes.Single() == new RevitWallTypeBinding(0.2, "Generic 200"),
        "FromIntent binds levels and wall types by their intent names");
    var twin = intent with { WallTypes = new[] { types[0], new IntentWallType("alt", "Other 200", 0.2, 3.0) } };
    Check(RevitBindings.FromIntent(projectId, twin, document, null, null).WallTypes.Count == 0, "two wall type names with one thickness are ambiguous and stay unbound");
    Check(Code(() => PlanToCommands.Translate(plan, bindings with { Document = new RevitDocumentRef("relative.rvt", document.UniqueId) })) == RevitCommandCodes.DocumentPathInvalid,
        "an invalid target document is refused up front");
    Check(Code(() => PlanToCommands.Translate(plan, bindings with { WallTypes = new[] { new RevitWallTypeBinding(0.2, "A"), new RevitWallTypeBinding(0.2, "B") } })) == "argument:wall_type_binding_duplicate",
        "duplicate wall type thickness bindings are refused");
}
catch (Exception error)
{
    Console.WriteLine("::error::plan-to-commands FAIL unexpected " + error.GetType().Name + " | " + error.Message);
    failed++;
}

Console.WriteLine(JsonSerializer.Serialize(new { passed, failed, plan_to_commands_contract = failed == 0 }));
Environment.ExitCode = failed == 0 && passed > 0 ? 0 : 1;
