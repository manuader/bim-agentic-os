using Bim.Core;
using Bim.Revit;
using System.Text.Json;
using System.Text.Json.Nodes;
// P4-02 documentation commands contract oracle: query.title_blocks, dimension.create_linear, tag.create,
// schedule.create, sheet.create and sheet.place_view. Covers the schemas, the typed validators, the guarded client
// path (fake transport) and the Autodesk-free executor rules, dimension layout and readback checks in
// runtimes/revit-native/CommandsDocumentation.cs. No Revit is involved: a passing run is unit-tested evidence only;
// CommandsExecutorDocumentation.cs is compiled, never run.
static class CatalogueDocumentationChecks
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string ViewUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000b01";
    const string OtherViewUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000b02";
    const string GridAUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000e01";
    const string GridBUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000e02";
    const string WallUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0002a1c3";
    const string SheetUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000f01";
    const string TitleBlockUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000f02";
    const string ElementUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000d01";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";
    static readonly string[] DocumentationCommands = { "query.title_blocks", "dimension.create_linear", "tag.create", "schedule.create", "sheet.create", "sheet.place_view" };
    static readonly string[] Writers = { "dimension.create_linear", "tag.create", "schedule.create", "sheet.create", "sheet.place_view" };

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static JsonObject Envelope(string command, JsonNode args, string path = FixturePath) => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-documentation",
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
        ["command"] = command,
        ["args"] = args.DeepClone(),
    };
    static JsonArray Pair(double x, double y) => new(x, y);
    static JsonObject Line(double x0, double y0, double x1, double y1) => new() { ["start"] = Pair(x0, y0), ["end"] = Pair(x1, y1) };
    static JsonObject Dimension() => new() { ["view_uid"] = ViewUid, ["first_uid"] = GridAUid, ["second_uid"] = GridBUid };
    static JsonObject Tag() => new() { ["view_uid"] = ViewUid, ["element_uid"] = WallUid, ["head"] = Pair(3000, 500) };
    static JsonObject Schedule() => new() { ["category"] = "door", ["fields"] = new JsonArray("Mark", "Type", "Level"), ["sort_by"] = "Mark", ["name"] = "Door Schedule" };
    static JsonObject Sheet() => new() { ["number"] = "A101", ["name"] = "Ground Floor", ["title_block"] = "A1 metric: A1" };
    static JsonObject Place() => new() { ["sheet_uid"] = SheetUid, ["view_uid"] = ViewUid, ["center"] = Pair(420, 297) };
    static JsonObject Sample(string command) => command switch
    {
        "query.title_blocks" => new JsonObject(),
        "dimension.create_linear" => Dimension(),
        "tag.create" => Tag(),
        "schedule.create" => Schedule(),
        "sheet.create" => Sheet(),
        "sheet.place_view" => Place(),
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };
    static PlanPoint P(double x, double y) => new(x, y);
    static bool Near(double a, double b, double tolerance = 1e-6) => Math.Abs(a - b) <= tolerance;

    static RevitCommandException? Refusal(string json)
    {
        try { RevitCommandProtocol.Parse(json); return null; }
        catch (RevitCommandException error) { return error; }
        catch (Exception error) { return new RevitCommandException("unexpected " + error.GetType().Name, null, error.Message); }
    }
    static void Rejects(string name, JsonNode envelope, string code, string? field = null)
    {
        var error = Refusal(envelope.ToJsonString());
        Check(error is not null && error.Code == code && (field is null || error.Field == field), name, error is null ? "accepted" : error.Code + " at " + error.Field + " (" + error.Detail + ")");
    }
    static RevitCommandEnvelope? Accepts(string name, JsonNode envelope)
    {
        try { var parsed = RevitCommandProtocol.Parse(envelope.ToJsonString()); Check(true, name); return parsed; }
        catch (RevitCommandException error) { Check(false, name, error.Code + " at " + error.Field + " (" + error.Detail + ")"); return null; }
    }
    static RevitCommandException? Thrown(Action action)
    {
        try { action(); return null; }
        catch (RevitCommandException error) { return error; }
    }
    static JsonObject With(JsonObject args, string member, JsonNode? value) { if (value is null) args.Remove(member); else args[member] = value; return args; }

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        Catalogue();
        Dimensions();
        Tags();
        Schedules();
        Sheets();
        Placements();
        ClientPath();
        ExecutorRules();
        Readbacks();
        ReadbackJson();
    }

    static void Catalogue()
    {
        var names = RevitCommandCatalogue.Documentation.Select(spec => spec.Name).ToArray();
        Check(names.SequenceEqual(DocumentationCommands), "documentation catalogue lists the title block query and five creators in order", string.Join(",", names));
        var throughStructure = RevitCommandCatalogue.V1.Concat(RevitCommandCatalogue.V2).Concat(RevitCommandCatalogue.Views).Concat(RevitCommandCatalogue.Mirror).Concat(RevitCommandCatalogue.Elements).Concat(RevitCommandCatalogue.Structure).ToArray();
        Check(RevitCommandCatalogue.All.Take(throughStructure.Length).SequenceEqual(throughStructure) && RevitCommandCatalogue.All.Skip(throughStructure.Length).Take(names.Length).SequenceEqual(RevitCommandCatalogue.Documentation),
            "All is v1, v2, views, snapshot, element queries, structure, then the documentation commands");
        Check(RevitCommandCatalogue.All.Select(spec => spec.Name).Distinct().Count() == RevitCommandCatalogue.All.Count && RevitCommandCatalogue.All.Select(spec => spec.ToolName).Distinct().Count() == RevitCommandCatalogue.All.Count, "command and tool names stay distinct with the documentation commands");
        Check(RevitCommandCatalogue.Documentation.All(spec => spec.ToolName == "revit_" + spec.Name.Replace('.', '_') && System.Text.RegularExpressions.Regex.IsMatch(spec.ToolName, @"\Arevit_[a-z0-9_]{1,58}\z")), "documentation tool names are revit_<command with . replaced by _> within 64 characters");
        Check(RevitCommandCatalogue.Documentation.Where(spec => spec.Writes).Select(spec => spec.Name).SequenceEqual(Writers), "the five documentation creators write; query.title_blocks is read-only");
        foreach (var name in names) Check(ReferenceEquals(RevitCommandCatalogue.Find(name), RevitCommandCatalogue.Documentation.Single(spec => spec.Name == name)), "Find resolves documentation command " + name);
        Check(RevitCommandCatalogue.Documentation.All(spec => spec.Description.Length > 20 && spec.Description.EndsWith('.') && (spec.Writes ? spec.Description.Contains("enabled test copies", StringComparison.Ordinal) : spec.Description.Contains("Read-only", StringComparison.Ordinal))),
            "every documentation command has a plain-English description; writes state the copies-only rule and queries say Read-only");
        foreach (var spec in RevitCommandCatalogue.Documentation)
        {
            string? problem = null;
            try { using var schema = JsonDocument.Parse(spec.ArgsSchemaJson); RevitCommandSchema.CheckSupported(schema.RootElement); if (schema.RootElement.GetProperty("type").GetString() != "object") problem = "not an object schema"; }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { problem = error.Message; }
            Check(problem is null, spec.Name + " args schema is valid JSON using only enforced keywords", problem ?? "");
        }
        var copies = new[] { @"C:\Repo\.bim\native-runs" };
        foreach (var spec in RevitCommandCatalogue.Documentation)
            Check(PermissionPolicy.RequiredLevel(spec.Name, FixturePath, copies) == (spec.Writes ? PermissionLevel.WriteCopies : PermissionLevel.Read)
                && PermissionPolicy.RequiredLevel(spec.Name, RealPath, copies) == (spec.Writes ? PermissionLevel.WriteRealProject : PermissionLevel.Read), "permission level of " + spec.Name + " follows its catalogue entry");
        Check(Accepts("query.title_blocks with {} accepted", Envelope("query.title_blocks", new JsonObject()))?.Args is EmptyQueryArgs, "query.title_blocks binds empty arguments");
        Rejects("query.title_blocks with an argument", Envelope("query.title_blocks", new JsonObject { ["x"] = 1 }), RevitCommandCodes.ArgsInvalid, "args.x");
    }

    static void Dimensions()
    {
        var dimension = Accepts("dimension accepted", Envelope("dimension.create_linear", Dimension()));
        Check(dimension?.Args is DimensionLinearCreateArgs { ViewUid: ViewUid, FirstUid: GridAUid, SecondUid: GridBUid, LineStart: null, LineEnd: null, WallFace: "exterior" }, "dimension binds view, both references, no line and the exterior wall face by default");
        var withLine = Accepts("dimension with a line accepted", Envelope("dimension.create_linear", With(With(Dimension(), "line", Line(0, 1000, 6000, 1000)), "wall_face", "interior")));
        Check(withLine?.Args is DimensionLinearCreateArgs { WallFace: "interior", LineStart: { } s, LineEnd: { } e } && Near(s.Y.Millimetres, 1000) && Near(e.X.Millimetres, 6000), "dimension binds the line in millimetres and the wall face");
        Rejects("dimension of an element to itself", Envelope("dimension.create_linear", With(Dimension(), "second_uid", GridAUid.ToUpperInvariant())), RevitCommandCodes.ArgsInvalid, "args.second_uid");
        Rejects("dimension without second reference", Envelope("dimension.create_linear", With(Dimension(), "second_uid", null)), RevitCommandCodes.ArgsInvalid, "args.second_uid");
        Rejects("dimension view that is not a UniqueId", Envelope("dimension.create_linear", With(Dimension(), "view_uid", "Level 1")), RevitCommandCodes.ArgsInvalid, "args.view_uid");
        Rejects("dimension reference that is not a UniqueId", Envelope("dimension.create_linear", With(Dimension(), "first_uid", "Grid A")), RevitCommandCodes.ArgsInvalid, "args.first_uid");
        Rejects("dimension line of zero length", Envelope("dimension.create_linear", With(Dimension(), "line", Line(5, 5, 5, 5))), RevitCommandCodes.CurveTooShort, "args.line");
        Rejects("dimension line longer than the Revit limit", Envelope("dimension.create_linear", With(Dimension(), "line", Line(0, 0, 9_144_001, 0))), RevitCommandCodes.LengthInvalid, "args.line");
        Rejects("dimension line coordinate beyond 1e8 mm", Envelope("dimension.create_linear", With(Dimension(), "line", Line(0, -2e8, 0, 0))), RevitCommandCodes.LengthInvalid, "args.line.start[1]");
        Rejects("dimension unknown wall face", Envelope("dimension.create_linear", With(Dimension(), "wall_face", "core")), RevitCommandCodes.ArgsInvalid, "args.wall_face");
        Rejects("dimension with an extra member", Envelope("dimension.create_linear", With(Dimension(), "offset_mm", 500)), RevitCommandCodes.ArgsInvalid, "args.offset_mm");
    }

    static void Tags()
    {
        var tag = Accepts("tag accepted", Envelope("tag.create", Tag()));
        Check(tag?.Args is TagCreateArgs { ViewUid: ViewUid, ElementUid: WallUid, Leader: false, Orientation: "horizontal", Head: { } head } && Near(head.X.Millimetres, 3000) && Near(head.Y.Millimetres, 500), "tag binds view, element, head in millimetres, no leader and horizontal by default");
        Check(Accepts("tag with leader and orientation accepted", Envelope("tag.create", With(With(Tag(), "leader", true), "orientation", "vertical")))?.Args is TagCreateArgs { Head: not null, Leader: true, Orientation: "vertical" }, "tag leader and orientation bind");
        Rejects("tag leader without head", Envelope("tag.create", With(With(Tag(), "head", null), "leader", true)), RevitCommandCodes.ArgsInvalid, "args.leader");
        Check(Accepts("tag without head or leader accepted", Envelope("tag.create", With(Tag(), "head", null)))?.Args is TagCreateArgs { Head: null, Leader: false }, "a tag without head places the head at the element without a leader");
        Rejects("tag unknown orientation", Envelope("tag.create", With(Tag(), "orientation", "model")), RevitCommandCodes.ArgsInvalid, "args.orientation");
        Rejects("tag leader not a boolean", Envelope("tag.create", With(Tag(), "leader", "yes")), RevitCommandCodes.ArgsInvalid, "args.leader");
        Rejects("tag head coordinate beyond 1e8 mm", Envelope("tag.create", With(Tag(), "head", Pair(2e8, 0))), RevitCommandCodes.LengthInvalid, "args.head[0]");
        Rejects("tag head with three coordinates", Envelope("tag.create", With(Tag(), "head", new JsonArray(0, 0, 0))), RevitCommandCodes.ArgsInvalid, "args.head");
        Rejects("tag without element", Envelope("tag.create", With(Tag(), "element_uid", null)), RevitCommandCodes.ArgsInvalid, "args.element_uid");
    }

    static void Schedules()
    {
        var schedule = Accepts("schedule accepted", Envelope("schedule.create", Schedule()));
        Check(schedule?.Args is ScheduleCreateArgs { Category: "door", BuiltInCategory: "OST_Doors", SortBy: "Mark", Name: "Door Schedule" } s && s.Fields.SequenceEqual(new[] { "Mark", "Type", "Level" }), "schedule binds category, BuiltInCategory, fields in order, sort field and name");
        Check(Accepts("schedule by BuiltInCategory accepted", Envelope("schedule.create", new JsonObject { ["category"] = "OST_Walls", ["fields"] = new JsonArray("Length") }))?.Args is ScheduleCreateArgs { BuiltInCategory: "OST_Walls", SortBy: null, Name: null }, "schedule takes an OST_ category; sort and name are optional");
        Rejects("schedule unknown category", Envelope("schedule.create", With(Schedule(), "category", "doors")), RevitCommandCodes.ArgsInvalid, "args.category");
        Rejects("schedule without fields", Envelope("schedule.create", With(Schedule(), "fields", new JsonArray())), RevitCommandCodes.ArgsInvalid, "args.fields");
        Rejects("schedule with 33 fields", Envelope("schedule.create", With(Schedule(), "fields", new JsonArray(Enumerable.Range(0, 33).Select(i => (JsonNode?)JsonValue.Create("F" + i)).ToArray()))), RevitCommandCodes.ArgsInvalid, "args.fields");
        Rejects("schedule with a field listed twice", Envelope("schedule.create", With(With(Schedule(), "fields", new JsonArray("Mark", "Type", "Mark")), "sort_by", null)), RevitCommandCodes.ArgsInvalid, "args.fields[2]");
        Rejects("schedule sorted by a field it does not list", Envelope("schedule.create", With(Schedule(), "sort_by", "Width")), RevitCommandCodes.ArgsInvalid, "args.sort_by");
        Rejects("schedule name with a Revit-forbidden character", Envelope("schedule.create", With(Schedule(), "name", "Doors:All")), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("schedule name with a trailing space", Envelope("schedule.create", With(Schedule(), "name", "Doors ")), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("schedule field with a control character", Envelope("schedule.create", With(Schedule(), "fields", new JsonArray("Mark\u0001"))), RevitCommandCodes.ArgsInvalid, "args.fields[0]");
    }

    static void Sheets()
    {
        var sheet = Accepts("sheet accepted", Envelope("sheet.create", Sheet()));
        Check(sheet?.Args is SheetCreateArgs { Number: "A101", Name: "Ground Floor", TitleBlock: "A1 metric: A1" }, "sheet binds number, name and title block");
        Check(Accepts("sheet without title block accepted", Envelope("sheet.create", With(Sheet(), "title_block", null)))?.Args is SheetCreateArgs { TitleBlock: null }, "sheet title block is optional");
        Rejects("sheet without number", Envelope("sheet.create", With(Sheet(), "number", null)), RevitCommandCodes.ArgsInvalid, "args.number");
        Rejects("sheet without name", Envelope("sheet.create", With(Sheet(), "name", null)), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("sheet number with a Revit-forbidden character", Envelope("sheet.create", With(Sheet(), "number", "A{1}")), RevitCommandCodes.ArgsInvalid, "args.number");
        Rejects("sheet number over 64 characters", Envelope("sheet.create", With(Sheet(), "number", new string('9', 65))), RevitCommandCodes.ArgsInvalid, "args.number");
        Rejects("sheet name with a leading space", Envelope("sheet.create", With(Sheet(), "name", " Ground")), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("sheet name blank", Envelope("sheet.create", With(Sheet(), "name", "")), RevitCommandCodes.ArgsInvalid, "args.name");
    }

    static void Placements()
    {
        var place = Accepts("place view accepted", Envelope("sheet.place_view", Place()));
        Check(place?.Args is SheetPlaceViewArgs { SheetUid: SheetUid, ViewUid: ViewUid, Center: { } c } && Near(c.X.Millimetres, 420) && Near(c.Y.Millimetres, 297), "place view binds sheet, view and the sheet point in millimetres");
        Rejects("place view without center", Envelope("sheet.place_view", With(Place(), "center", null)), RevitCommandCodes.ArgsInvalid, "args.center");
        Rejects("place view far off the sheet", Envelope("sheet.place_view", With(Place(), "center", Pair(100001, 0))), RevitCommandCodes.LengthInvalid, "args.center");
        Rejects("place view far below the sheet", Envelope("sheet.place_view", With(Place(), "center", Pair(0, -100001))), RevitCommandCodes.LengthInvalid, "args.center");
        Check(Accepts("place view at +100000 mm accepted", Envelope("sheet.place_view", With(Place(), "center", Pair(100000, 100000))))?.Args is SheetPlaceViewArgs, "a sheet point exactly 100000 mm from the origin is accepted");
        Check(Accepts("place view at -100000 mm accepted", Envelope("sheet.place_view", With(Place(), "center", Pair(-100000, -100000))))?.Args is SheetPlaceViewArgs, "a sheet point exactly -100000 mm from the origin is accepted");
        Rejects("place view sheet that is not a UniqueId", Envelope("sheet.place_view", With(Place(), "sheet_uid", "A101")), RevitCommandCodes.ArgsInvalid, "args.sheet_uid");
    }

    static void ClientPath()
    {
        var policy = new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" });
        var forwarded = new List<string>();
        string? Transport(string pipe, string line) { forwarded.Add(line); return Answer(line); }
        var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
        foreach (var command in Writers)
        {
            var args = Sample(command);
            var before = forwarded.Count;
            var ok = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args).ToJsonString(), policy, Transport, noLinks);
            Check(!ok.IsError && forwarded.Count == before + 1 && forwarded[^1] == RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(Envelope(command, args).ToJsonString())), command + " on an enabled copy is forwarded as the canonical wire line", ok.Line);
            var refused = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args, RealPath).ToJsonString(), policy, Transport, noLinks);
            Check(refused.IsError && !refused.Forwarded && JsonNode.Parse(refused.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentNotEnabled && forwarded.Count == before + 1, command + " on a real project is document_not_enabled and never forwarded", refused.Line);
            var linked = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args).ToJsonString(), policy, Transport, new RevitFileSystemProbe(path => path.EndsWith("run-1", StringComparison.Ordinal), _ => null));
            Check(linked.IsError && JsonNode.Parse(linked.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentLinkRefused && forwarded.Count == before + 1, command + " through a linked directory is document_link_refused");
        }
        var read = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("query.title_blocks", new JsonObject(), RealPath).ToJsonString(), policy, Transport, noLinks);
        Check(!read.IsError && read.Forwarded, "query.title_blocks is read-only and allowed on a real project", read.Line);
        var invalid = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("schedule.create", With(Schedule(), "sort_by", "Width")).ToJsonString(), policy, Transport, noLinks);
        Check(invalid.IsError && !invalid.Forwarded && JsonNode.Parse(invalid.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.ArgsInvalid && JsonNode.Parse(invalid.Line)!["field"]!.GetValue<string>() == "args.sort_by", "an invalid documentation command returns the typed failure before the pipe", invalid.Line);
        var a = RevitCommandProtocol.Parse("""{"protocol_version":1,"operation_id":"op-s","document":{"path":"C:\\Repo\\.bim\\native-runs\\r\\a.rvt","unique_id":"60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b"},"command":"sheet.place_view","args":{"sheet_uid":"60f91daf-3dd7-4283-a86d-24137b73f3da-00000f01","view_uid":"60f91daf-3dd7-4283-a86d-24137b73f3da-00000b01","center":[420,297]}}""");
        var b = RevitCommandProtocol.Parse("""{"args":{"center":[4.2e2,297.0],"view_uid":"60f91daf-3dd7-4283-a86d-24137b73f3da-00000b01","sheet_uid":"60f91daf-3dd7-4283-a86d-24137b73f3da-00000f01"},"command":"sheet.place_view","document":{"unique_id":"60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b","path":"C:\\Repo\\.bim\\native-runs\\r\\a.rvt"},"operation_id":"op-s","protocol_version":1}""");
        Check(a.CanonicalJson == b.CanonicalJson && a.RequestSha256 == b.RequestSha256, "documentation requests use the same canonical form and hash as v1", a.CanonicalJson + " vs " + b.CanonicalJson);
    }

    static string Answer(string wireLine)
    {
        using var wire = JsonDocument.Parse(wireLine);
        var commandText = wire.RootElement.GetProperty("Command").GetRawText();
        using var command = JsonDocument.Parse(commandText);
        var receipt = new RevitCommandReceipt(command.RootElement.GetProperty("operation_id").GetString()!, command.RootElement.GetProperty("command").GetString()!,
            command.RootElement.GetProperty("document").GetProperty("unique_id").GetString()!, CommandWire.Sha256(commandText), false);
        return RevitCommandResponse.Success(receipt, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null, Array.Empty<string>()).ToJson();
    }

    static void ExecutorRules()
    {
        foreach (var viewType in new[] { "FloorPlan", "CeilingPlan", "EngineeringPlan", "AreaPlan" })
            Check(Thrown(() => CommandRulesDocumentation.CheckAnnotationView(viewType, false)) is null, viewType + " views take dimensions and tags");
        foreach (var viewType in new[] { "Section", "ThreeD", "DrawingSheet", "Schedule", "Elevation" })
            Check(Thrown(() => CommandRulesDocumentation.CheckAnnotationView(viewType, false)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.view_uid" } refusal && refusal.Detail!.Contains(viewType, StringComparison.Ordinal), "a " + viewType + " view is refused for dimensions and tags");
        Check(Thrown(() => CommandRulesDocumentation.CheckAnnotationView("FloorPlan", true)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.view_uid" }, "a view template is refused for dimensions and tags");
        Check(CommandRulesDocumentation.TagOrientation("horizontal") == "Horizontal" && CommandRulesDocumentation.TagOrientation("vertical") == "Vertical", "tag orientations map to the Revit TagOrientation names");
        Check(Thrown(() => CommandRulesDocumentation.TagOrientation("model")) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.orientation" }, "an unknown tag orientation is args_invalid");

        // Grids x = 0 and x = 6000 running +Y: normals point -X.
        var gridA = CommandRulesDocumentation.LineDatum(GridAUid, "grid", P(0, 0), P(0, 12000), "args.first_uid");
        var gridB = CommandRulesDocumentation.LineDatum(GridBUid, "grid", P(6000, 0), P(6000, 12000), "args.second_uid");
        Check(Near(gridA.NormalX, -1) && Near(gridA.NormalY, 0) && gridA.Point == P(0, 0) && gridA.Kind == "grid", "a grid datum is its start point with the direction turned +90 degrees");
        var layout = CommandRulesDocumentation.Layout(gridA, gridB, null, null);
        Check(Near(layout.ExpectedMm, 6000) && layout.Start == P(0, 0) && Near(layout.End.X, 6000) && Near(layout.End.Y, 0), "the default dimension line runs from the first reference straight across to the second", layout.ToString());
        var reversed = CommandRulesDocumentation.LineDatum(GridBUid, "grid", P(6000, 12000), P(6000, 0), "args.second_uid");
        Check(Near(CommandRulesDocumentation.Layout(gridA, reversed, null, null).ExpectedMm, 6000), "an opposite-running parallel grid measures the same distance");
        var requested = CommandRulesDocumentation.Layout(gridA, gridB, P(-500, 3000), P(6500, 3000));
        Check(Near(requested.ExpectedMm, 6000) && requested.Start == P(-500, 3000) && requested.End == P(6500, 3000), "a requested perpendicular dimension line is kept and measures the reference distance");
        DimensionLayout? shortLine = null;
        var shortRefusal = Thrown(() => shortLine = CommandRulesDocumentation.Layout(gridA, gridB, P(0, 1000), P(100, 1000.001)));
        Check(shortRefusal is null && shortLine is { } kept && Near(kept.ExpectedMm, 6000) && kept.End == P(100, 1000.001), "a 100 mm dimension line off perpendicular by a 0.001 mm readback rounding is accepted", shortRefusal?.Detail ?? "");
        Check(Thrown(() => CommandRulesDocumentation.Layout(gridA, gridB, P(0, 1000), P(100, 1000.01))) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.line" }, "a 100 mm dimension line 0.01 mm off perpendicular is args_invalid");
        Check(Thrown(() => CommandRulesDocumentation.Layout(gridA, gridB, P(0, 0), P(6000, 100))) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.line" }, "a dimension line that is not perpendicular to the references is args_invalid");
        var skew = CommandRulesDocumentation.LineDatum(GridBUid, "grid", P(6000, 0), P(6100, 12000), "args.second_uid");
        Check(Thrown(() => CommandRulesDocumentation.Layout(gridA, skew, null, null)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.second_uid" } notParallel && notParallel.Detail!.Contains("parallel", StringComparison.Ordinal), "references that are not parallel are args_invalid");
        var close = CommandRulesDocumentation.LineDatum(GridBUid, "grid", P(1, 0), P(1, 12000), "args.second_uid");
        Check(Thrown(() => CommandRulesDocumentation.Layout(gridA, close, null, null)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.second_uid" }, "references less than 1.2 mm apart are args_invalid");
        Check(Thrown(() => CommandRulesDocumentation.LineDatum(GridAUid, "grid", P(0, 0), P(0, 1), "args.first_uid")) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.first_uid" }, "a reference line under 1.2 mm is args_invalid");
        var face = CommandRulesDocumentation.FaceDatum(WallUid, "wall_exterior", P(3000, 100), 0, 2, 0, "args.second_uid");
        Check(Near(face.NormalX, 0) && Near(face.NormalY, 1) && face.Kind == "wall_exterior", "a vertical wall face datum keeps its point and the normalised plan normal");
        Check(Thrown(() => CommandRulesDocumentation.FaceDatum(WallUid, "wall_exterior", P(0, 0), 0, 1, 0.1, "args.first_uid")) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.first_uid" }, "a wall face that is not vertical is args_invalid");
        Check(Thrown(() => CommandRulesDocumentation.FaceDatum(WallUid, "wall_exterior", P(0, 0), 0, 0, 0, "args.first_uid")) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.first_uid" }, "a wall face with a zero normal is args_invalid");
        var wallA = CommandRulesDocumentation.FaceDatum(WallUid, "wall_exterior", P(0, 100), 0, -1, 0, "args.first_uid");
        Check(Thrown(() => CommandRulesDocumentation.Layout(wallA, face, null, null)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.second_uid" }, "two wall faces in one plane are args_invalid");
        Check(Near(CommandRulesDocumentation.Layout(wallA, CommandRulesDocumentation.FaceDatum(GridBUid, "wall_interior", P(500, 4100), 0, 1, 0, "args.second_uid"), null, null).ExpectedMm, 4000), "opposite-facing parallel wall faces measure their plan distance");

        Check(Thrown(() => CommandRulesDocumentation.CheckTaggable("OST_Walls", true, false)) is null && Thrown(() => CommandRulesDocumentation.CheckTaggable("OST_Doors", true, false)) is null, "walls and doors can be tagged by category");
        foreach (var untaggable in new[] { "OST_Rooms", "OST_Areas", "OST_MEPSpaces" })
            Check(Thrown(() => CommandRulesDocumentation.CheckTaggable(untaggable, true, false)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.element_uid" }, untaggable + " elements are refused for a tag by category");
        Check(Thrown(() => CommandRulesDocumentation.CheckTaggable("OST_Grids", false, false)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.element_uid" } && Thrown(() => CommandRulesDocumentation.CheckTaggable(null, true, false)) is { Code: RevitCommandCodes.ArgsInvalid }, "annotation elements and elements without category are refused");
        Check(Thrown(() => CommandRulesDocumentation.CheckTaggable("OST_Walls", true, true)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.element_uid" }, "an element type is refused for a tag");

        Check(CommandRulesDocumentation.PickFields(new[] { "Level", "Mark", "Type", "Width" }, new[] { "Mark", "Level" }).SequenceEqual(new[] { 1, 0 }), "schedule fields resolve by name in request order");
        Check(Thrown(() => CommandRulesDocumentation.PickFields(new[] { "Mark" }, new[] { "Mark", "Height" })) is { Code: RevitCommandCodes.ReferenceNotFound, Field: "args.fields[1]" }, "a field the category does not offer is reference_not_found at its index");
        Check(Thrown(() => CommandRulesDocumentation.PickFields(new[] { "Mark", "Comments", "Comments" }, new[] { "Comments" })) is { Code: RevitCommandCodes.ReferenceNotFound, Field: "args.fields[0]" } ambiguous && ambiguous.Detail!.Contains("2", StringComparison.Ordinal), "an ambiguous field name is reference_not_found");
        Check(Thrown(() => CommandRulesDocumentation.PickFields(new[] { "mark" }, new[] { "Mark" })) is { Code: RevitCommandCodes.ReferenceNotFound }, "field names match case-sensitively");

        Check(Thrown(() => CommandRulesDocumentation.CheckSheetNumberFree("A101", new[] { "A100", "a101" })) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.number" }, "a sheet number differing only in case from an existing one is args_invalid (fail closed)");
        Check(Thrown(() => CommandRulesDocumentation.CheckSheetNumberFree("A102", new[] { "A100", "A101" })) is null, "a free sheet number is accepted");

        Check(CommandRulesDocumentation.CheckPlaceable("FloorPlan", false, false, false, null, true) == CommandRulesDocumentation.ViewportKind, "a plan view not yet on a sheet is placed as a viewport");
        Check(CommandRulesDocumentation.CheckPlaceable("Schedule", false, true, false, null, false) == CommandRulesDocumentation.ScheduleKind, "a schedule is placed as a schedule sheet instance");
        Check(Thrown(() => CommandRulesDocumentation.CheckPlaceable("FloorPlan", false, false, false, "A101", false)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.view_uid" } placed && placed.Detail!.Contains("A101", StringComparison.Ordinal), "a view already on a sheet is refused, naming the sheet");
        Check(Thrown(() => CommandRulesDocumentation.CheckPlaceable("Section", false, false, false, null, false)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.view_uid" }, "a view Revit refuses to add (CanAddViewToSheet false) is args_invalid");
        Check(Thrown(() => CommandRulesDocumentation.CheckPlaceable("FloorPlan", true, false, false, null, true)) is { Code: RevitCommandCodes.ArgsInvalid }, "a view template cannot be placed");
        Check(Thrown(() => CommandRulesDocumentation.CheckPlaceable("DrawingSheet", false, false, false, null, true)) is { Code: RevitCommandCodes.ArgsInvalid }, "a sheet cannot be placed on a sheet");
        Check(Thrown(() => CommandRulesDocumentation.CheckPlaceable("Schedule", false, true, true, null, true)) is { Code: RevitCommandCodes.ArgsInvalid }, "a title block revision schedule cannot be placed");
        foreach (var browser in new[] { "ProjectBrowser", "Internal" })
            Check(Thrown(() => CommandRulesDocumentation.CheckPlaceable(browser, false, false, false, null, true)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.view_uid" } refused && refused.Detail!.Contains(browser, StringComparison.Ordinal), "a " + browser + " view cannot be placed on a sheet");
        Check(Thrown(() => CommandRulesDocumentation.CheckPlaceable("Legend", false, false, false, "A101", true)) is null && CommandRulesDocumentation.CheckPlaceable("Legend", false, false, false, "A101", true) == CommandRulesDocumentation.ViewportKind, "a legend already on a sheet may be placed again as a viewport");
        Check(Thrown(() => CommandRulesDocumentation.CheckPlaceable("Legend", false, false, false, "A101", false)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.view_uid" }, "a legend Revit refuses to add (CanAddViewToSheet false) is args_invalid");
    }

    static void Readbacks()
    {
        var gridA = CommandRulesDocumentation.LineDatum(GridAUid, "grid", P(0, 0), P(0, 12000), "args.first_uid");
        var gridB = CommandRulesDocumentation.LineDatum(GridBUid, "grid", P(6000, 0), P(6000, 12000), "args.second_uid");
        var layout = CommandRulesDocumentation.Layout(gridA, gridB, null, null);
        var dimension = new ObservedDimension(ElementUid, ViewUid, new[] { GridAUid, GridBUid }, 6000, 1, 0);
        Check(CommandReadbackDocumentation.Dimension(ViewUid, gridA, gridB, layout, dimension) is null, "dimension readback matches");
        Check(CommandReadbackDocumentation.Dimension(ViewUid, gridA, gridB, layout, dimension with { ReferenceUids = new[] { GridBUid, GridAUid.ToUpperInvariant() }, DirectionX = -1 }) is null, "dimension references in either order and a reversed line direction match");
        Check(CommandReadbackDocumentation.Dimension(ViewUid, gridA, gridB, layout, dimension with { ValueMm = 6000.0004 }) is null, "a dimension value within the 1e-3 mm tolerance matches");
        Check(CommandReadbackDocumentation.Dimension(ViewUid, gridA, gridB, layout, dimension with { ValueMm = 5999 }) is { } value && value.Contains("value", StringComparison.Ordinal), "a dimension value 1 mm off is a mismatch");
        Check(CommandReadbackDocumentation.Dimension(ViewUid, gridA, gridB, layout, dimension with { ValueMm = null }) is not null, "an unreadable or multi-segment dimension value is a mismatch");
        Check(CommandReadbackDocumentation.Dimension(ViewUid, gridA, gridB, layout, dimension with { ViewUid = OtherViewUid }) is { } view && view.Contains("view", StringComparison.Ordinal), "a dimension in another view is a mismatch");
        Check(CommandReadbackDocumentation.Dimension(ViewUid, gridA, gridB, layout, dimension with { ReferenceUids = new[] { GridAUid } }) is { } references && references.Contains("references", StringComparison.Ordinal), "a dimension missing a reference is a mismatch");
        Check(CommandReadbackDocumentation.Dimension(ViewUid, gridA, gridB, layout, dimension with { ReferenceUids = new[] { GridAUid, GridAUid } }) is not null, "a dimension measuring one reference twice is a mismatch");
        Check(CommandReadbackDocumentation.Dimension(ViewUid, gridA, gridB, layout, dimension with { DirectionX = 0, DirectionY = 1 }) is { } direction && direction.Contains("direction", StringComparison.Ordinal), "a dimension line along the references is a mismatch");
        Check(CommandReadbackDocumentation.Dimension(ViewUid, gridA, gridB, layout, dimension with { DirectionX = null }) is not null, "an unreadable dimension line direction is a mismatch");

        var tag = new ObservedTag(ElementUid, ViewUid, new[] { WallUid }, false, "Horizontal", P(3000, 500), false);
        Check(CommandReadbackDocumentation.Tag(ViewUid, WallUid, P(3000, 500), false, "Horizontal", tag) is null, "tag readback matches");
        Check(CommandReadbackDocumentation.Tag(ViewUid, WallUid, P(3000, 500), false, "Horizontal", tag with { Orphaned = true }) is { } orphan && orphan.Contains("orphaned", StringComparison.Ordinal), "an orphaned tag is a mismatch");
        Check(CommandReadbackDocumentation.Tag(ViewUid, WallUid, P(3000, 500), false, "Horizontal", tag with { TaggedUids = Array.Empty<string>() }) is { } nothing && nothing.Contains("nothing", StringComparison.Ordinal), "a tag that tags nothing is a mismatch");
        Check(CommandReadbackDocumentation.Tag(ViewUid, WallUid, P(3000, 500), false, "Horizontal", tag with { TaggedUids = new[] { WallUid, ElementUid } }) is not null, "a tag of two elements is a mismatch");
        Check(CommandReadbackDocumentation.Tag(ViewUid, WallUid, P(3000, 500), true, "Horizontal", tag) is { } leader && leader.Contains("leader", StringComparison.Ordinal), "a tag without the requested leader is a mismatch");
        Check(CommandReadbackDocumentation.Tag(ViewUid, WallUid, P(3000, 500), false, "Vertical", tag) is { } orientation && orientation.Contains("orientation", StringComparison.Ordinal), "a tag in the other orientation is a mismatch");
        Check(CommandReadbackDocumentation.Tag(ViewUid, WallUid, P(3000, 500), false, "Horizontal", tag with { Head = P(3000, 600) }) is { } head && head.Contains("head", StringComparison.Ordinal), "a tag head elsewhere is a mismatch");
        Check(CommandReadbackDocumentation.Tag(ViewUid, WallUid, P(3000, 500), false, "Horizontal", tag with { ViewUid = OtherViewUid }) is not null, "a tag in another view is a mismatch");

        var args = (ScheduleCreateArgs)RevitCommandProtocol.Parse(Envelope("schedule.create", Schedule()).ToJsonString()).Args;
        var schedule = new ObservedSchedule(ElementUid, "Door Schedule", "OST_Doors", false, new[] { "Mark", "Type", "Level" }, new[] { "Mark" });
        Check(CommandReadbackDocumentation.Schedule(args, schedule) is null, "schedule readback matches");
        Check(CommandReadbackDocumentation.Schedule(args, schedule with { Fields = new[] { "Type", "Mark", "Level" } }) is { } order && order.Contains("fields", StringComparison.Ordinal), "schedule fields in another order are a mismatch");
        Check(CommandReadbackDocumentation.Schedule(args, schedule with { Fields = new[] { "Mark", "Type", "Level", "Count" } }) is not null, "an extra schedule field is a mismatch");
        Check(CommandReadbackDocumentation.Schedule(args, schedule with { SortFields = Array.Empty<string>() }) is { } sort && sort.Contains("sorts", StringComparison.Ordinal), "a schedule without the requested sort is a mismatch");
        Check(CommandReadbackDocumentation.Schedule(args, schedule with { BuiltInCategory = "OST_Windows" }) is { } category && category.Contains("category", StringComparison.Ordinal), "a schedule of another category is a mismatch");
        Check(CommandReadbackDocumentation.Schedule(args, schedule with { Name = "Door Schedule 2" }) is { } scheduleName && scheduleName.Contains("name", StringComparison.Ordinal), "a schedule name other than the requested one is a mismatch");
        Check(CommandReadbackDocumentation.Schedule(args with { Name = null, SortBy = null }, schedule with { Name = "Door Schedule 1", SortFields = Array.Empty<string>() }) is null, "an automatic schedule name is accepted when none was requested");
        Check(CommandReadbackDocumentation.Schedule(args with { SortBy = null }, schedule) is not null, "a sort that was not requested is a mismatch");
        Check(CommandReadbackDocumentation.Schedule(args, schedule with { IsTemplate = true }) is not null, "a schedule template is a mismatch");
        Check(CommandReadbackDocumentation.Schedule(args, schedule with { BuiltInCategory = null }) is { } unreadableCategory && unreadableCategory.Contains("unreadable", StringComparison.Ordinal), "an unreadable schedule category is a mismatch");

        var sheet = new ObservedSheet(SheetUid, "A101", "Ground Floor", new[] { TitleBlockUid });
        Check(CommandReadbackDocumentation.Sheet("A101", "Ground Floor", TitleBlockUid, sheet) is null, "sheet readback matches");
        Check(CommandReadbackDocumentation.Sheet("A101", "Ground Floor", null, sheet with { TitleBlockTypeUids = Array.Empty<string>() }) is null, "a sheet without title block matches when none was requested");
        Check(CommandReadbackDocumentation.Sheet("A101", "Ground Floor", null, sheet) is { } extra && extra.Contains("title blocks", StringComparison.Ordinal), "a title block that was not requested is a mismatch");
        Check(CommandReadbackDocumentation.Sheet("A101", "Ground Floor", TitleBlockUid, sheet with { TitleBlockTypeUids = new[] { TitleBlockUid, TitleBlockUid } }) is not null, "two title blocks are a mismatch");
        Check(CommandReadbackDocumentation.Sheet("A101", "Ground Floor", TitleBlockUid, sheet with { Number = "A102" }) is { } number && number.Contains("number", StringComparison.Ordinal), "a sheet number other than the requested one is a mismatch");
        Check(CommandReadbackDocumentation.Sheet("A101", "Ground Floor", TitleBlockUid, sheet with { Name = "Unnamed" }) is { } sheetName && sheetName.Contains("name", StringComparison.Ordinal), "a sheet name other than the requested one is a mismatch");

        var placement = new ObservedPlacement(ElementUid, "viewport", SheetUid, ViewUid, P(420, 297));
        Check(CommandReadbackDocumentation.Placement("viewport", SheetUid, ViewUid, P(420, 297), placement) is null, "viewport readback matches");
        Check(CommandReadbackDocumentation.Placement("viewport", SheetUid, ViewUid, P(420, 297), placement with { Center = P(420.0004, 297) }) is null, "a viewport centre within the 1e-3 mm tolerance matches");
        Check(CommandReadbackDocumentation.Placement("viewport", SheetUid, ViewUid, P(420, 297), placement with { Center = P(421, 297) }) is { } centre && centre.Contains("placed at", StringComparison.Ordinal), "a viewport centred elsewhere is a mismatch");
        Check(CommandReadbackDocumentation.Placement("viewport", SheetUid, ViewUid, P(420, 297), placement with { Center = null }) is not null, "an unreadable viewport centre is a mismatch");
        Check(CommandReadbackDocumentation.Placement("viewport", SheetUid, ViewUid, P(420, 297), placement with { SheetUid = TitleBlockUid }) is { } onSheet && onSheet.Contains("sheet", StringComparison.Ordinal), "a viewport on another sheet is a mismatch");
        Check(CommandReadbackDocumentation.Placement("viewport", SheetUid, ViewUid, P(420, 297), placement with { ViewUid = OtherViewUid }) is not null, "a viewport of another view is a mismatch");
        var scheduleInstance = new ObservedPlacement(ElementUid, "schedule", SheetUid, OtherViewUid, P(20, 580));
        Check(CommandReadbackDocumentation.Placement("schedule", SheetUid, OtherViewUid, P(20, 580), scheduleInstance) is null, "schedule sheet instance readback matches");
        Check(CommandReadbackDocumentation.Placement("schedule", SheetUid, OtherViewUid, P(20, 580), scheduleInstance with { Center = P(20, 581) }) is not null, "a schedule sheet instance placed elsewhere is a mismatch");
        Check(CommandReadbackDocumentation.Placement("schedule", SheetUid, ViewUid, P(420, 297), placement) is { } kind && kind.Contains("schedule", StringComparison.Ordinal), "a viewport where a schedule instance was expected is a mismatch");
    }

    static void ReadbackJson()
    {
        var blocks = JsonNode.Parse(CommandReadbackDocumentation.TitleBlocksJson(new[] { new TitleBlockRow("A1 metric", "A1", TitleBlockUid, false), new TitleBlockRow("A0 metric", "A0", ElementUid, true) }))!["title_blocks"]!.AsArray();
        Check(blocks.Select(row => row!["family"]!.GetValue<string>()).SequenceEqual(new[] { "A0 metric", "A1 metric" }) && blocks[1]!["reference"]!.GetValue<string>() == "A1 metric: A1" && !blocks[1]!["active"]!.GetValue<bool>() && blocks[1]!["unique_id"]!.GetValue<string>() == TitleBlockUid && blocks[0]!["type"]!.GetValue<string>() == "A0",
            "title blocks readback is sorted by family then type with the Family: Type reference and active flag", blocks.ToJsonString());
        var dimension = JsonNode.Parse(CommandReadbackDocumentation.DimensionJson(new ObservedDimension(ElementUid, ViewUid, new[] { GridAUid, GridBUid }, 6000, 1, 0)))!["dimension"]!;
        Check(dimension["unique_id"]!.GetValue<string>() == ElementUid && dimension["view_unique_id"]!.GetValue<string>() == ViewUid && dimension["reference_unique_ids"]!.AsArray().Select(uid => uid!.GetValue<string>()).SequenceEqual(new[] { GridAUid, GridBUid }) && dimension["value_mm"]!.GetValue<double>() == 6000 && dimension["direction"]![0]!.GetValue<double>() == 1,
            "dimension readback JSON reports view, references, value in mm and direction", dimension.ToJsonString());
        var unreadable = JsonNode.Parse(CommandReadbackDocumentation.DimensionJson(new ObservedDimension(ElementUid, null, Array.Empty<string>(), null, null, null)))!["dimension"]!;
        Check(unreadable["value_mm"] is null && unreadable["direction"] is null && unreadable["view_unique_id"] is null, "unreadable dimension values are null", unreadable.ToJsonString());
        var tag = JsonNode.Parse(CommandReadbackDocumentation.TagJson(new ObservedTag(ElementUid, ViewUid, new[] { WallUid }, true, "Horizontal", P(3000, 500), false)))!["tag"]!;
        Check(tag["tagged_unique_ids"]![0]!.GetValue<string>() == WallUid && tag["has_leader"]!.GetValue<bool>() && tag["orientation"]!.GetValue<string>() == "Horizontal" && tag["head"]![1]!.GetValue<double>() == 500 && !tag["orphaned"]!.GetValue<bool>() && tag["view_unique_id"]!.GetValue<string>() == ViewUid,
            "tag readback JSON reports view, tagged element, leader, orientation, head and orphan flag", tag.ToJsonString());
        var schedule = JsonNode.Parse(CommandReadbackDocumentation.ScheduleJson(new ObservedSchedule(ElementUid, "Door Schedule", "OST_Doors", false, new[] { "Mark", "Type" }, new[] { "Mark" })))!["schedule"]!;
        Check(schedule["name"]!.GetValue<string>() == "Door Schedule" && schedule["built_in_category"]!.GetValue<string>() == "OST_Doors" && schedule["fields"]!.AsArray().Count == 2 && schedule["sort_fields"]![0]!.GetValue<string>() == "Mark",
            "schedule readback JSON reports name, category, fields and sort fields", schedule.ToJsonString());
        var sheet = JsonNode.Parse(CommandReadbackDocumentation.SheetJson(new ObservedSheet(SheetUid, "A101", "Ground Floor", new[] { TitleBlockUid })))!["sheet"]!;
        Check(sheet["unique_id"]!.GetValue<string>() == SheetUid && sheet["number"]!.GetValue<string>() == "A101" && sheet["name"]!.GetValue<string>() == "Ground Floor" && sheet["title_block_type_unique_ids"]![0]!.GetValue<string>() == TitleBlockUid,
            "sheet readback JSON reports number, name and title block types", sheet.ToJsonString());
        var placement = JsonNode.Parse(CommandReadbackDocumentation.PlacementJson(new ObservedPlacement(ElementUid, "schedule", SheetUid, ViewUid, P(420, 297))))!["placement"]!;
        Check(placement["kind"]!.GetValue<string>() == "schedule" && placement["sheet_unique_id"]!.GetValue<string>() == SheetUid && placement["view_unique_id"]!.GetValue<string>() == ViewUid && placement["center"]![0]!.GetValue<double>() == 420,
            "placement readback JSON reports kind, sheet, view and sheet point", placement.ToJsonString());
    }
}
