using Bim.Core;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// P4-04 oracle: DocDefaults.From derives a deterministic default documentation set from a BIM-IR project (a floor plan
// per level, room and door tags, door and room schedules, one sheet per level with its plan placed, a schedules sheet),
// bound to the BIM-IR plan, schedule and sheet elements when present; its P4-03 full regeneration emits only commands
// the Revit catalogue accepts; keys are stable across unrelated edits; output is byte-identical for the same input;
// malformed projects are refused with typed codes.
delegate void Checker(bool condition, string name, string detail = "");

static class DefaultsTests
{
    static readonly Provenance provenance = new("doc-defaults-tests", "unit");
    static readonly RevitDocumentRef document = new(@"C:\Repo\.bim\native-runs\p4-04\copy.rvt", "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0c");
    static Guid Id(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes("doc-defaults:" + name)).Take(16).ToArray());
    static readonly Guid projectId = Id("project");
    static Guid RoomOf(int room) => Id("room" + room.ToString(CultureInfo.InvariantCulture));
    static Guid Role(int room, string role) => RoomCompiler.Id(RoomOf(room), role);
    static Guid Door(int room) => Id("door" + room.ToString(CultureInfo.InvariantCulture));
    static Guid Hosted(int room) => Id("hosted" + room.ToString(CultureInfo.InvariantCulture));
    static string K(Guid id) => id.ToString("N");
    static string Json(object value) => JsonSerializer.Serialize(value);

    static string? Code(Action action)
    {
        try { action(); return null; }
        catch (ArgumentNullException) { return "null"; }
        catch (ArgumentException error) { return error.Message; }
        catch (InvalidOperationException error) { return error.Message; }
        catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
    }

    static string Fake(string source)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        return hex[..8] + "-" + hex[8..12] + "-" + hex[12..16] + "-" + hex[16..20] + "-" + hex[20..32] + "-" + hex[32..40];
    }

    // Rooms from RoomCompiler (own level at elevations[room], four walls, space, plan/schedule/sheet elements), an
    // absolute door on the south wall and a hosted door [offset, width] in the north wall of each room; rooms 10 m apart.
    static Project Build(params double[] elevations)
    {
        var elements = new List<Element>();
        for (var room = 0; room < elevations.Length; room++)
        {
            foreach (var element in RoomCompiler.Compile(projectId, 0, new RoomIntent(RoomOf(room), 4, 3, 2.8, 0.2, room * 10, 0, "m", provenance)).Elements)
                elements.Add(element.Kind == "level" ? element with { GeometryMetres = new[] { elevations[room] } } : element);
            var x = room * 10 + 0.1;
            elements.Add(new(Door(room), "door", new[] { x + 1, 0.1, x + 1.9, 0.1, 0, 2.1 }, new[] { Role(room, "south"), Role(room, "level") }, provenance));
            elements.Add(new(Hosted(room), "door", new[] { 1.0, 0.9 }, new[] { Role(room, "north"), Role(room, "level") }, provenance));
        }
        return new(projectId, 0, elements.ToArray());
    }
    static RevitBindings Bindings(Project project) => new(document,
        project.Elements.Where(element => element.Kind == "level").ToDictionary(element => element.Id, element => "Level " + element.Id.ToString("N")[..8]),
        Array.Empty<RevitWallTypeBinding>(), null, null);
    static Project Replace(Project project, Guid id, Func<Element, Element> change) => project with { Elements = project.Elements.Select(element => element.Id == id ? change(element) : element).ToArray() };
    static Project Add(Project project, Element element) => project with { Elements = project.Elements.Append(element).ToArray() };
    static Element Move(Element wall, double dx, double dy)
    {
        var g = wall.GeometryMetres;
        return wall with { GeometryMetres = new[] { g[0] + dx, g[1] + dy, g[2] + dx, g[3] + dy, g[4], g[5] } };
    }
    static IEnumerable<string> Keys(DocumentationSet set) =>
        set.Views.Select(item => item.Key).Concat(set.Tags.Select(item => item.Key)).Concat(set.Dimensions.Select(item => item.Key))
            .Concat(set.Schedules.Select(item => item.Key)).Concat(set.Sheets.Select(item => item.Key)).Concat(set.Placements.Select(item => item.Key));

    // Each step, its inputs filled, validated against its catalogue entry's args schema and re-parsed by the protocol.
    static string CatalogueProblem(DocRegenerationPlan plan)
    {
        foreach (var step in plan.Steps)
        {
            var spec = RevitCommandCatalogue.Find(step.Command);
            if (spec is null || !spec.Writes) return step.Key + ": " + step.Command + " is not a catalogue write";
            var ids = step.Inputs.ToDictionary(input => input.Source, input => Fake(input.Source), StringComparer.Ordinal);
            var args = (JsonObject)JsonNode.Parse(step.ArgsJson)!;
            foreach (var input in step.Inputs) args[input.Field["args.".Length..]] = ids[input.Source];
            using var value = JsonDocument.Parse(args.ToJsonString());
            using var schema = JsonDocument.Parse(spec.ArgsSchemaJson);
            if (RevitCommandSchema.Validate(value.RootElement, schema.RootElement, "args") is { } failure) return step.Key + ": schema " + failure.Field + " " + failure.Detail;
            var error = Code(() =>
            {
                var envelope = DocRegeneration.Envelope(plan, step, ids);
                if (envelope.Command != step.Command || envelope.OperationId != step.OperationId) throw new InvalidOperationException("envelope_mismatch");
            });
            if (error is not null) return step.Key + ": " + error;
        }
        return "";
    }

    public static void Run(Checker check)
    {
        // ---- Shape of the default set ----
        var basis = Build(0, 3, 6);
        var set = DocDefaults.From(basis);
        var levels = new[] { 0, 1, 2 }.Select(room => Role(room, "level")).ToArray();
        check(set.Views.Count == 3 && set.Views.Select(view => view.Key).SequenceEqual(levels.Select(level => "plan-" + K(level)).OrderBy(key => key, StringComparer.Ordinal))
            && set.Views.All(view => view.Kind == "floor_plan" && view.Elements.Count == 1 && view.Scale == 100 && "plan-" + K(view.Elements[0]) == view.Key),
            "defaults: one floor plan per BIM-IR level, keyed by the level id, sorted by key", Json(set.Views));
        var plan1 = set.Views.Single(view => view.Key == "plan-" + K(levels[1]));
        check(plan1.Name == "Level 2 Plan" && plan1.Bound == Role(1, "plan"), "defaults: plans are named by elevation order and bound to the BIM-IR plan element on their level", Json(plan1));
        var expectedTags = new[] { 0, 1, 2 }.SelectMany(room => new[]
        {
            "tag-room-" + K(Role(room, "space")) + "|plan-" + K(levels[room]) + "|" + K(Role(room, "space")),
            "tag-door-" + K(Door(room)) + "|plan-" + K(levels[room]) + "|" + K(Door(room)),
            "tag-door-" + K(Hosted(room)) + "|plan-" + K(levels[room]) + "|" + K(Hosted(room)),
        }).OrderBy(tag => tag, StringComparer.Ordinal).ToArray();
        check(set.Tags.Select(tag => tag.Key + "|" + tag.View + "|" + K(tag.Element)).SequenceEqual(expectedTags)
            && set.Tags.All(tag => tag.OffsetXMetres == 0 && tag.OffsetYMetres == 0 && !tag.Leader && tag.Orientation == "horizontal"),
            "defaults: a tag for every room (space) and door in the plan of the level it stands on", Json(set.Tags));
        check(set.Dimensions.Count == 0, "defaults: no dimensions");
        var doors = set.Schedules.SingleOrDefault(schedule => schedule.Key == "schedule-doors");
        var rooms = set.Schedules.SingleOrDefault(schedule => schedule.Key == "schedule-rooms");
        var roomSchedules = new[] { 0, 1, 2 }.Select(room => Role(room, "schedule")).OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray();
        check(set.Schedules.Count == 2 && doors is { ElementKind: "door", Category: "door", SortBy: "Mark", Name: "Door Schedule", Bound: null } && doors.Fields.SequenceEqual(new[] { "Mark", "Width" })
            && rooms is { ElementKind: "space", Category: "room", SortBy: "Number", Name: "Room Schedule" } && rooms.Fields.SequenceEqual(new[] { "Number", "Name", "Area" }) && rooms.Bound == roomSchedules[0],
            "defaults: door and room schedules; the room schedule is bound to the first BIM-IR schedule of spaces (by id), the door schedule unbound when none schedules doors", Json(set.Schedules));
        var sheet1 = set.Sheets.SingleOrDefault(sheet => sheet.Key == "sheet-" + K(levels[1]));
        var schedulesSheet = set.Sheets.SingleOrDefault(sheet => sheet.Key == "sheet-schedules");
        check(set.Sheets.Count == 4 && sheet1 is { Number: "A102", Name: "Level 2", TitleBlock: null } && sheet1.Bound == Role(1, "sheet")
            && schedulesSheet is { Number: "A900", Name: "Schedules", Bound: null } && set.Sheets.Select(sheet => sheet.Key).SequenceEqual(set.Sheets.Select(sheet => sheet.Key).OrderBy(key => key, StringComparer.Ordinal)),
            "defaults: one sheet per level (numbered by elevation order, bound to the BIM-IR sheet of its plan) and a schedules sheet", Json(set.Sheets));
        var expectedPlacements = levels.Select(level => new DocPlacement("place-plan-" + K(level), "sheet-" + K(level), "plan-" + K(level), 400, 300))
            .Append(new DocPlacement("place-schedule-doors", "sheet-schedules", "schedule-doors", 200, 400))
            .Append(new DocPlacement("place-schedule-rooms", "sheet-schedules", "schedule-rooms", 600, 400)).OrderBy(placement => placement.Key, StringComparer.Ordinal).ToArray();
        check(Json(set.Placements) == Json(expectedPlacements), "defaults: each plan on its level sheet, both schedules on the schedules sheet", Json(set.Placements));

        // ---- P4-03 full regeneration of the defaults: only valid catalogue commands ----
        var bindings = Bindings(basis);
        var full = DocRegeneration.Full(basis, set, bindings);
        check(full.Unsupported.Count == 0 && full.Steps.Count == full.TotalItems && full.TotalItems == Keys(set).Count(),
            "defaults: full regeneration emits one step per item and nothing unsupported", Json(full.Unsupported));
        var problem = CatalogueProblem(full);
        check(problem.Length == 0, "defaults: every step validates against its catalogue args schema and parses as an envelope", problem);
        check(new[] { "view.create_plan", "tag.create", "schedule.create", "sheet.create", "sheet.place_view" }.All(command => full.Steps.Any(step => step.Command == command)),
            "defaults: plans, tags, schedules, sheets and placements all reach the catalogue");
        check(full.Steps.Single(step => step.Key == "schedule-doors").Elements.Count == 6, "defaults: the door schedule reads every door");

        // ---- Determinism ----
        check(Json(DocDefaults.From(Build(0, 3, 6))) == Json(set), "defaults: same input twice gives byte-identical JSON");
        check(Json(DocDefaults.From(basis with { Elements = Enumerable.Reverse(basis.Elements).ToArray() })) == Json(set), "defaults: element order does not change the set");
        check(Json(DocRegeneration.Full(basis, DocDefaults.From(basis), bindings)) == Json(full), "defaults: full regeneration of the defaults is byte-identical for the same input");

        // ---- Stable keys across unrelated edits ----
        var moved = Replace(basis, Role(1, "south"), wall => Move(wall, 0, -0.5));
        check(Json(DocDefaults.From(moved)) == Json(set), "stable: moving a wall leaves the set byte-identical");
        var widened = Replace(basis, Role(0, "space"), space => { var g = space.GeometryMetres; return space with { GeometryMetres = new[] { g[0], g[1], g[2] + 0.5, g[3] } }; });
        check(Json(DocDefaults.From(widened)) == Json(set), "stable: resizing a space leaves the set byte-identical");
        var raised = Replace(basis, Role(0, "level"), level => level with { GeometryMetres = new[] { 9.0 } });
        var raisedSet = DocDefaults.From(raised);
        check(Keys(raisedSet).OrderBy(key => key, StringComparer.Ordinal).SequenceEqual(Keys(set).OrderBy(key => key, StringComparer.Ordinal))
            && raisedSet.Views.Single(view => view.Key == "plan-" + K(levels[0])).Name == "Level 3 Plan",
            "stable: reordering levels by elevation renames plans and sheets but keeps every key", Json(raisedSet.Views));
        var extraDoor = new Element(Id("door-extra"), "door", new[] { 13.0, 0.1, 13.8, 0.1, 0, 2.1 }, new[] { Role(1, "south"), Role(1, "level") }, provenance);
        var withDoor = DocDefaults.From(Add(basis, extraDoor));
        check(Keys(withDoor).Except(Keys(set)).SequenceEqual(new[] { "tag-door-" + K(extraDoor.Id) }) && !Keys(set).Except(Keys(withDoor)).Any(),
            "stable: adding a door adds exactly its tag key", string.Join(",", Keys(withDoor).Except(Keys(set))));
        var plan = Compiler.Diff(basis, moved);
        check(DocRegeneration.Agrees(basis, moved, plan, set, bindings), "defaults: affected-only regeneration of a wall move merges to full regeneration");

        // ---- Edge cases ----
        var loose = new Element(Id("door-loose"), "door", new[] { 30.0, 0.1, 30.9, 0.1, 0, 2.1 }, Array.Empty<Guid>(), provenance);
        var looseSet = DocDefaults.From(Add(basis, loose));
        check(!looseSet.Tags.Any(tag => tag.Element == loose.Id) && Keys(looseSet).SequenceEqual(Keys(set)),
            "edge: a door on no level is not tagged (it is still scheduled)");
        var bare = new Project(projectId, 0, new[] { new Element(Id("level-bare"), "level", new[] { 0.0 }, Array.Empty<Guid>(), provenance) });
        var bareSet = DocDefaults.From(bare);
        check(bareSet.Views.Count == 1 && bareSet.Views[0].Bound is null && bareSet.Sheets.Single(sheet => sheet.Key == "sheet-" + K(Id("level-bare"))).Bound is null && bareSet.Tags.Count == 0
            && bareSet.Schedules.All(schedule => schedule.Bound is null),
            "edge: a level without BIM-IR plan, sheet or schedule elements gives unbound items", Json(bareSet));
        var bareFull = DocRegeneration.Full(bare, bareSet, Bindings(bare));
        check(bareFull.Unsupported.Count == 0 && CatalogueProblem(bareFull).Length == 0, "edge: the defaults of a bare level regenerate to valid catalogue commands", Json(bareFull.Unsupported));

        // ---- Refusals ----
        check(Code(() => DocDefaults.From(null!)) == "null", "refusal: a null project");
        check(Code(() => DocDefaults.From(Add(basis, new Element(Id("dangling"), "door", new[] { 1.0, 0.9 }, new[] { Id("nowhere") }, provenance)))) == DocDefaultsCodes.InvalidIr,
            "refusal: a project Compiler.Validate rejects is invalid_ir");
        check(Code(() => DocDefaults.From(basis with { Elements = null! })) == DocDefaultsCodes.InvalidIr, "refusal: null elements are invalid_ir");
        check(Code(() => DocDefaults.From(basis with { Elements = basis.Elements.Append(null!).ToArray() })) == DocDefaultsCodes.InvalidIr, "refusal: a null element is invalid_ir");
        check(Code(() => DocDefaults.From(Add(basis, new Element(Id("bad-geometry"), "space", null!, Array.Empty<Guid>(), provenance)))) == DocDefaultsCodes.InvalidIr, "refusal: an element with null geometry is invalid_ir");
        check(Code(() => DocDefaults.From(Replace(basis, Role(2, "level"), level => level with { GeometryMetres = Array.Empty<double>() }))) == DocDefaultsCodes.LevelInvalid,
            "refusal: a level without exactly one elevation is doc_defaults_level_invalid");
        check(Code(() => DocDefaults.From(new Project(projectId, 0, Array.Empty<Element>()))) == DocDefaultsCodes.NoLevel, "refusal: a project without levels is doc_defaults_no_level");
    }
}
