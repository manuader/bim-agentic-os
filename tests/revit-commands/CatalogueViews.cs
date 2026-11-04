using Bim.Core;
using Bim.Revit;
using System.Text.Json;
using System.Text.Json.Nodes;
// P4-01 view commands contract oracle: query.view_family_types, query.view_templates, view.create_plan,
// view.create_section and view.create_3d. Covers the schemas, the typed validators, the guarded client path (fake
// transport) and the Autodesk-free executor rules and readback checks in runtimes/revit-native/CommandsViews.cs.
// No Revit is involved: a passing run is unit-tested evidence only; CommandsExecutorViews.cs is compiled, never run.
static class CatalogueViewsChecks
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string LevelUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000137";
    const string PlanTypeUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000901";
    const string CeilingTypeUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000902";
    const string SectionTypeUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000903";
    const string ThreeDTypeUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000904";
    const string TemplateUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000a01";
    const string ViewUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000b01";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";
    static readonly string[] ViewCommands = { "query.view_family_types", "query.view_templates", "view.create_plan", "view.create_section", "view.create_3d" };

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static JsonObject Envelope(string command, JsonNode args, string path = FixturePath) => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-views",
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
        ["command"] = command,
        ["args"] = args.DeepClone(),
    };
    static JsonArray Pair(double x, double y) => new(x, y);
    static JsonArray Triple(double x, double y, double z) => new(x, y, z);
    static JsonObject Plan() => new() { ["level"] = "Level 1", ["kind"] = "floor_plan" };
    static JsonObject Section() => new() { ["line"] = new JsonObject { ["start"] = Pair(0, 0), ["end"] = Pair(6000, 0) }, ["depth_mm"] = 3000, ["bottom_elevation_mm"] = -500, ["top_elevation_mm"] = 4000 };
    static JsonObject ThreeD(bool box = true) { var view = new JsonObject(); if (box) view["section_box"] = new JsonObject { ["min"] = Triple(0, 0, 0), ["max"] = Triple(8000, 6000, 3500) }; return view; }
    static Point2 P(double x, double y) => new(Length.FromMillimetres(x), Length.FromMillimetres(y));
    static bool Near(double a, double b, double tolerance = 1e-6) => Math.Abs(a - b) <= tolerance;
    static bool NearV(Vec3 a, Vec3 b, double tolerance = 1e-9) => Near(a.X, b.X, tolerance) && Near(a.Y, b.Y, tolerance) && Near(a.Z, b.Z, tolerance);

    static RevitCommandException? Refusal(string json)
    {
        try { RevitCommandProtocol.Parse(json); return null; }
        catch (RevitCommandException error) { return error; }
        catch (Exception error) { return new RevitCommandException("unexpected " + error.GetType().Name, null, error.Message); }
    }
    static void RejectsText(string name, string json, string code, string? field = null)
    {
        var error = Refusal(json);
        Check(error is not null && error.Code == code && (field is null || error.Field == field), name, error is null ? "accepted" : error.Code + " at " + error.Field + " (" + error.Detail + ")");
    }
    static void Rejects(string name, JsonNode envelope, string code, string? field = null) => RejectsText(name, envelope.ToJsonString(), code, field);
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
        Queries();
        Plans();
        Sections();
        ThreeDViews();
        ClientPath();
        ExecutorRules();
        Readbacks();
    }

    static void Catalogue()
    {
        var names = RevitCommandCatalogue.Views.Select(spec => spec.Name).ToArray();
        Check(names.SequenceEqual(ViewCommands), "view catalogue lists the two queries and three creators in order", string.Join(",", names));
        Check(RevitCommandCatalogue.All.Take(18).SequenceEqual(RevitCommandCatalogue.V1.Concat(RevitCommandCatalogue.V2).Concat(RevitCommandCatalogue.Views)), "All starts with v1, then v2, then the view commands (later catalogue parts follow)");
        Check(RevitCommandCatalogue.All.Select(spec => spec.Name).Distinct().Count() == RevitCommandCatalogue.All.Count && RevitCommandCatalogue.All.Select(spec => spec.ToolName).Distinct().Count() == RevitCommandCatalogue.All.Count, "command and tool names stay distinct across every catalogue part");
        Check(RevitCommandCatalogue.Views.All(spec => spec.ToolName == "revit_" + spec.Name.Replace('.', '_') && System.Text.RegularExpressions.Regex.IsMatch(spec.ToolName, @"\Arevit_[a-z0-9_]+\z")), "view tool names are revit_<command with . replaced by _>");
        Check(RevitCommandCatalogue.Views.Where(spec => spec.Writes).Select(spec => spec.Name).SequenceEqual(new[] { "view.create_plan", "view.create_section", "view.create_3d" }), "the three view creators write; the two queries are read-only");
        foreach (var name in names) Check(ReferenceEquals(RevitCommandCatalogue.Find(name), RevitCommandCatalogue.Views.Single(spec => spec.Name == name)), "Find resolves view command " + name);
        Check(RevitCommandCatalogue.Views.All(spec => spec.Description.Length > 20 && spec.Description.EndsWith('.') && (!spec.Writes || spec.Description.Contains("enabled test copies", StringComparison.Ordinal))), "every view command has a plain-English description; writes state the copies-only rule");
        foreach (var spec in RevitCommandCatalogue.Views)
        {
            string? problem = null;
            try { using var schema = JsonDocument.Parse(spec.ArgsSchemaJson); RevitCommandSchema.CheckSupported(schema.RootElement); if (schema.RootElement.GetProperty("type").GetString() != "object") problem = "not an object schema"; }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { problem = error.Message; }
            Check(problem is null, spec.Name + " args schema is valid JSON using only enforced keywords", problem ?? "");
        }
        var copies = new[] { @"C:\Repo\.bim\native-runs" };
        foreach (var spec in RevitCommandCatalogue.Views)
            Check(PermissionPolicy.RequiredLevel(spec.Name, FixturePath, copies) == (spec.Writes ? PermissionLevel.WriteCopies : PermissionLevel.Read) && (!spec.Writes || PermissionPolicy.RequiredLevel(spec.Name, RealPath, copies) == PermissionLevel.WriteRealProject), "permission level of " + spec.Name + " follows its catalogue entry");
        Check(RevitViewRules.MaxScale == 24000, "view scale cap is 1:24000");
    }

    static void Queries()
    {
        Check(Accepts("query.view_family_types with {} accepted", Envelope("query.view_family_types", new JsonObject()))?.Args is ViewFamilyTypeQueryArgs { Family: null }, "query.view_family_types without a filter lists every family");
        foreach (var family in new[] { "floor_plan", "ceiling_plan", "section", "three_d" })
            Check(Accepts("query.view_family_types family " + family + " accepted", Envelope("query.view_family_types", new JsonObject { ["family"] = family }))?.Args is ViewFamilyTypeQueryArgs { Family: { } bound } && bound == family, "query.view_family_types binds the " + family + " filter");
        Rejects("query.view_family_types unknown family", Envelope("query.view_family_types", new JsonObject { ["family"] = "elevation" }), RevitCommandCodes.ArgsInvalid, "args.family");
        Rejects("query.view_family_types unexpected member", Envelope("query.view_family_types", new JsonObject { ["x"] = 1 }), RevitCommandCodes.ArgsInvalid, "args.x");
        Check(Accepts("query.view_templates with {} accepted", Envelope("query.view_templates", new JsonObject()))?.Args is EmptyQueryArgs, "query.view_templates binds empty arguments");
        Rejects("query.view_templates with an argument", Envelope("query.view_templates", new JsonObject { ["x"] = 1 }), RevitCommandCodes.ArgsInvalid, "args.x");
    }

    static void Plans()
    {
        var floor = Accepts("floor plan accepted", Envelope("view.create_plan", Plan()));
        Check(floor?.Args is ViewPlanCreateArgs { Level: "Level 1", Kind: "floor_plan", Options: { Type: null, Name: null, Template: null, Scale: null } }, "floor plan binds level and kind with no options");
        var full = Accepts("ceiling plan with type, name and scale accepted", Envelope("view.create_plan", new JsonObject { ["level"] = LevelUid, ["kind"] = "ceiling_plan", ["type"] = "Ceiling Plan", ["name"] = "Level 1 - RCP (agent)", ["scale"] = 50 }));
        Check(full?.Args is ViewPlanCreateArgs { Level: LevelUid, Kind: "ceiling_plan", Options: { Type: "Ceiling Plan", Name: "Level 1 - RCP (agent)", Scale: 50, Template: null } }, "ceiling plan binds type, name and scale");
        Check(Accepts("plan with a view template accepted", Envelope("view.create_plan", With(Plan(), "view_template", "Architectural Plan")))?.Args is ViewPlanCreateArgs { Options: { Template: "Architectural Plan", Scale: null } }, "plan binds a view template reference");
        Rejects("plan without level", Envelope("view.create_plan", With(Plan(), "level", null)), RevitCommandCodes.ArgsInvalid, "args.level");
        Rejects("plan without kind", Envelope("view.create_plan", With(Plan(), "kind", null)), RevitCommandCodes.ArgsInvalid, "args.kind");
        Rejects("plan kind section is not a plan", Envelope("view.create_plan", With(Plan(), "kind", "section")), RevitCommandCodes.ArgsInvalid, "args.kind");
        Rejects("plan with unexpected member", Envelope("view.create_plan", With(Plan(), "detail_level", "fine")), RevitCommandCodes.ArgsInvalid, "args.detail_level");
        foreach (var (label, value) in new (string, JsonNode)[] { ("zero", 0), ("negative", -100), ("above 24000", 24001), ("fractional", 50.5), ("string", "1:50") })
            Rejects("plan scale " + label + " is refused", Envelope("view.create_plan", With(Plan(), "scale", value)), RevitCommandCodes.ArgsInvalid, "args.scale");
        Accepts("plan scale 1 accepted", Envelope("view.create_plan", With(Plan(), "scale", 1)));
        Accepts("plan scale 24000 accepted", Envelope("view.create_plan", With(Plan(), "scale", 24000)));
        Rejects("plan scale together with a view template is refused (the template may control scale)", Envelope("view.create_plan", With(With(Plan(), "scale", 100), "view_template", "Architectural Plan")), RevitCommandCodes.ArgsInvalid, "args.scale");
        foreach (var forbidden in new[] { "A:B", "A\\B", "A{B", "A}B", "A[B", "A]B", "A|B", "A;B", "A<B", "A>B", "A?B", "A`B", "A~B" })
            Rejects("view name with a Revit-forbidden character " + forbidden[1], Envelope("view.create_plan", With(Plan(), "name", forbidden)), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("view name with a control character", Envelope("view.create_plan", With(Plan(), "name", "Level\u00011")), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("empty view name", Envelope("view.create_plan", With(Plan(), "name", "")), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("view name of only spaces", Envelope("view.create_plan", With(Plan(), "name", "   ")), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("view name with leading or trailing space", Envelope("view.create_plan", With(Plan(), "name", " Plan ")), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("view name over 256 characters", Envelope("view.create_plan", With(Plan(), "name", new string('n', 257))), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("empty view template reference", Envelope("view.create_plan", With(Plan(), "view_template", "")), RevitCommandCodes.ArgsInvalid, "args.view_template");
        Rejects("empty view type reference", Envelope("view.create_plan", With(Plan(), "type", "")), RevitCommandCodes.ArgsInvalid, "args.type");
    }

    static void Sections()
    {
        var section = Accepts("section accepted", Envelope("view.create_section", Section()));
        Check(section?.Args is ViewSectionCreateArgs s && Near(s.End.X.Millimetres, 6000) && Near(s.Depth.Millimetres, 3000) && Near(s.Bottom.Millimetres, -500) && Near(s.Top.Millimetres, 4000) && s.Options == new ViewOptions(null, null, null, null),
            "section binds the line, depth, bottom and top elevations in millimetres");
        Check(Accepts("section with options accepted", Envelope("view.create_section", With(With(Section(), "name", "Section A"), "scale", 20)))?.Args is ViewSectionCreateArgs { Options: { Name: "Section A", Scale: 20 } }, "section binds name and scale options");
        Rejects("section line of zero length", Envelope("view.create_section", With(Section(), "line", new JsonObject { ["start"] = Pair(10, 10), ["end"] = Pair(10, 10) })), RevitCommandCodes.CurveTooShort, "args.line");
        Rejects("section line of 1 mm", Envelope("view.create_section", With(Section(), "line", new JsonObject { ["start"] = Pair(0, 0), ["end"] = Pair(1, 0) })), RevitCommandCodes.CurveTooShort, "args.line");
        Rejects("section line longer than the Revit limit", Envelope("view.create_section", With(Section(), "line", new JsonObject { ["start"] = Pair(0, 0), ["end"] = Pair(9_144_001, 0) })), RevitCommandCodes.LengthInvalid, "args.line");
        Rejects("section line without end", Envelope("view.create_section", With(Section(), "line", new JsonObject { ["start"] = Pair(0, 0) })), RevitCommandCodes.ArgsInvalid, "args.line.end");
        Rejects("section line coordinate beyond 1e8 mm", Envelope("view.create_section", With(Section(), "line", new JsonObject { ["start"] = Pair(2e8, 0), ["end"] = Pair(0, 0) })), RevitCommandCodes.LengthInvalid, "args.line.start[0]");
        foreach (var (label, value) in new (string, double)[] { ("zero", 0), ("negative", -10), ("under 1.2 mm", 1), ("above the Revit limit", 9_144_001) })
            Rejects("section depth " + label, Envelope("view.create_section", With(Section(), "depth_mm", value)), RevitCommandCodes.LengthInvalid, "args.depth_mm");
        Rejects("section top below bottom", Envelope("view.create_section", With(Section(), "top_elevation_mm", -1000)), RevitCommandCodes.LengthInvalid, "args.top_elevation_mm");
        Rejects("section top within 1.2 mm of bottom", Envelope("view.create_section", With(Section(), "top_elevation_mm", -499)), RevitCommandCodes.LengthInvalid, "args.top_elevation_mm");
        Rejects("section bottom beyond the Revit limit", Envelope("view.create_section", With(Section(), "bottom_elevation_mm", -9_144_001)), RevitCommandCodes.LengthInvalid, "args.bottom_elevation_mm");
        Rejects("section top beyond the Revit limit", Envelope("view.create_section", With(Section(), "top_elevation_mm", 9_144_001)), RevitCommandCodes.LengthInvalid, "args.top_elevation_mm");
        Rejects("section without depth", Envelope("view.create_section", With(Section(), "depth_mm", null)), RevitCommandCodes.ArgsInvalid, "args.depth_mm");
        Rejects("section without bottom", Envelope("view.create_section", With(Section(), "bottom_elevation_mm", null)), RevitCommandCodes.ArgsInvalid, "args.bottom_elevation_mm");
        Rejects("section with a level member", Envelope("view.create_section", With(Section(), "level", "Level 1")), RevitCommandCodes.ArgsInvalid, "args.level");
        RejectsText("section depth overflowing to infinity", Envelope("view.create_section", Section()).ToJsonString().Replace("\"depth_mm\":3000", "\"depth_mm\":1e400"), RevitCommandCodes.LengthInvalid, "args.depth_mm");
        Rejects("section scale together with a view template", Envelope("view.create_section", With(With(Section(), "scale", 50), "view_template", "Building Section")), RevitCommandCodes.ArgsInvalid, "args.scale");
    }

    static void ThreeDViews()
    {
        var boxed = Accepts("3D view with a section box accepted", Envelope("view.create_3d", ThreeD()));
        Check(boxed?.Args is View3DCreateArgs { SectionBox: { } box } && Near(box.MaxX.Millimetres, 8000) && Near(box.MaxY.Millimetres, 6000) && Near(box.MaxZ.Millimetres, 3500) && box.MinZ.Millimetres == 0, "3D view binds the section box corners in millimetres");
        Check(Accepts("3D view without a section box accepted", Envelope("view.create_3d", ThreeD(false)))?.Args is View3DCreateArgs { SectionBox: null, Options.Name: null }, "omitted section box binds none");
        Check(Accepts("3D view with name and template accepted", Envelope("view.create_3d", With(With(ThreeD(false), "name", "Agent 3D"), "view_template", TemplateUid)))?.Args is View3DCreateArgs { Options: { Name: "Agent 3D", Template: TemplateUid } }, "3D view binds name and template");
        for (var axis = 0; axis < 3; axis++)
        {
            var max = new[] { 8000.0, 6000, 3500 };
            max[axis] = 0;
            Rejects("3D section box with a flat axis " + axis, Envelope("view.create_3d", new JsonObject { ["section_box"] = new JsonObject { ["min"] = Triple(0, 0, 0), ["max"] = Triple(max[0], max[1], max[2]) } }), RevitCommandCodes.LengthInvalid, "args.section_box.max[" + axis + "]");
        }
        Rejects("3D section box with min above max", Envelope("view.create_3d", new JsonObject { ["section_box"] = new JsonObject { ["min"] = Triple(0, 7000, 0), ["max"] = Triple(8000, 6000, 3500) } }), RevitCommandCodes.LengthInvalid, "args.section_box.max[1]");
        Rejects("3D section box corner with two coordinates", Envelope("view.create_3d", new JsonObject { ["section_box"] = new JsonObject { ["min"] = Pair(0, 0), ["max"] = Triple(8000, 6000, 3500) } }), RevitCommandCodes.ArgsInvalid, "args.section_box.min");
        Rejects("3D section box without max", Envelope("view.create_3d", new JsonObject { ["section_box"] = new JsonObject { ["min"] = Triple(0, 0, 0) } }), RevitCommandCodes.ArgsInvalid, "args.section_box.max");
        Rejects("3D section box coordinate beyond 1e8 mm", Envelope("view.create_3d", new JsonObject { ["section_box"] = new JsonObject { ["min"] = Triple(0, 0, -2e8), ["max"] = Triple(8000, 6000, 3500) } }), RevitCommandCodes.LengthInvalid, "args.section_box.min[2]");
        Rejects("3D section box extent beyond the Revit limit", Envelope("view.create_3d", new JsonObject { ["section_box"] = new JsonObject { ["min"] = Triple(-5e6, 0, 0), ["max"] = Triple(5e6, 6000, 3500) } }), RevitCommandCodes.LengthInvalid, "args.section_box.max[0]");
        Rejects("3D view perspective is not offered", Envelope("view.create_3d", With(ThreeD(), "perspective", true)), RevitCommandCodes.ArgsInvalid, "args.perspective");
        Rejects("3D view scale above 24000", Envelope("view.create_3d", With(ThreeD(), "scale", 30000)), RevitCommandCodes.ArgsInvalid, "args.scale");
    }

    static void ClientPath()
    {
        var policy = new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" });
        var forwarded = new List<string>();
        string? Transport(string pipe, string line) { forwarded.Add(line); return Answer(line); }
        var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
        foreach (var (command, args) in new (string, JsonObject)[] { ("view.create_plan", Plan()), ("view.create_section", Section()), ("view.create_3d", ThreeD()) })
        {
            var before = forwarded.Count;
            var ok = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args).ToJsonString(), policy, Transport, noLinks);
            Check(!ok.IsError && forwarded.Count == before + 1 && forwarded[^1] == RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(Envelope(command, args).ToJsonString())), command + " on an enabled copy is forwarded as the canonical wire line", ok.Line);
            var refused = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args, RealPath).ToJsonString(), policy, Transport, noLinks);
            Check(refused.IsError && !refused.Forwarded && JsonNode.Parse(refused.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentNotEnabled && forwarded.Count == before + 1, command + " on a real project is document_not_enabled and never forwarded", refused.Line);
            var linked = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args).ToJsonString(), policy, Transport, new RevitFileSystemProbe(path => path.EndsWith("run-1", StringComparison.Ordinal), _ => null));
            Check(linked.IsError && JsonNode.Parse(linked.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentLinkRefused && forwarded.Count == before + 1, command + " through a linked directory is document_link_refused");
        }
        foreach (var query in new[] { "query.view_family_types", "query.view_templates" })
        {
            var read = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(query, new JsonObject(), RealPath).ToJsonString(), policy, Transport, noLinks);
            Check(!read.IsError && read.Forwarded, query + " is read-only and allowed on a real project", read.Line);
        }
        var invalid = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("view.create_plan", With(Plan(), "scale", 0)).ToJsonString(), policy, Transport, noLinks);
        Check(invalid.IsError && !invalid.Forwarded && JsonNode.Parse(invalid.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.ArgsInvalid && JsonNode.Parse(invalid.Line)!["field"]!.GetValue<string>() == "args.scale", "an invalid view command returns the typed failure before the pipe", invalid.Line);
        var a = RevitCommandProtocol.Parse("""{"protocol_version":1,"operation_id":"op-s","document":{"path":"C:\\Repo\\.bim\\native-runs\\r\\a.rvt","unique_id":"60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b"},"command":"view.create_section","args":{"line":{"start":[0,0],"end":[6000,0]},"depth_mm":3000,"bottom_elevation_mm":0,"top_elevation_mm":4000}}""");
        var b = RevitCommandProtocol.Parse("""{"args":{"top_elevation_mm":4.0e3,"bottom_elevation_mm":-0,"depth_mm":3e3,"line":{"end":[6000.0,0],"start":[0,0e0]}},"command":"view.create_section","document":{"unique_id":"60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b","path":"C:\\Repo\\.bim\\native-runs\\r\\a.rvt"},"operation_id":"op-s","protocol_version":1}""");
        Check(a.CanonicalJson == b.CanonicalJson && a.RequestSha256 == b.RequestSha256, "view requests use the same canonical form and hash as v1", a.CanonicalJson + " vs " + b.CanonicalJson);
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
        Check(CommandRulesViews.ViewFamily("floor_plan") == "FloorPlan" && CommandRulesViews.ViewFamily("ceiling_plan") == "CeilingPlan" && CommandRulesViews.ViewFamily("section") == "Section" && CommandRulesViews.ViewFamily("three_d") == "ThreeDimensional", "command families map to Revit ViewFamily names");
        Check(CommandRulesViews.ViewType("floor_plan") == "FloorPlan" && CommandRulesViews.ViewType("ceiling_plan") == "CeilingPlan" && CommandRulesViews.ViewType("section") == "Section" && CommandRulesViews.ViewType("three_d") == "ThreeD", "command families map to Revit ViewType names");
        Check(Thrown(() => CommandRulesViews.ViewFamily("legend")) is not null, "an unknown family is refused");

        var types = new[]
        {
            new ViewTypeCandidate(PlanTypeUid, "Floor Plan", "FloorPlan"),
            new ViewTypeCandidate(CeilingTypeUid, "Ceiling Plan", "CeilingPlan"),
            new ViewTypeCandidate(SectionTypeUid, "Building Section", "Section"),
            new ViewTypeCandidate("60f91daf-3dd7-4283-a86d-24137b73f3da-00000905", "Wall Section", "Section"),
            new ViewTypeCandidate(ThreeDTypeUid, "3D View", "ThreeDimensional"),
        };
        Check(CommandRulesViews.PickType(types, null, PlanTypeUid, "FloorPlan", "args.type") == 0, "no type: the document default of the family is used");
        Check(CommandRulesViews.PickType(types, null, null, "CeilingPlan", "args.type") == 1, "no type and no default: the only type of the family is used");
        Check(CommandRulesViews.PickType(types, null, CeilingTypeUid, "FloorPlan", "args.type") == 0, "a default of another family is ignored");
        Check(Thrown(() => CommandRulesViews.PickType(types, null, null, "Section", "args.type")) is { Code: RevitCommandCodes.ReferenceNotFound, Field: "args.type" }, "no type, no default and two section types is reference_not_found asking for args.type");
        Check(Thrown(() => CommandRulesViews.PickType(types.Where(type => type.ViewFamily != "ThreeDimensional").ToArray(), null, null, "ThreeDimensional", "args.type")) is { Code: RevitCommandCodes.ReferenceNotFound }, "a document without a 3D view family type is reference_not_found");
        Check(CommandRulesViews.PickType(types, "Wall Section", SectionTypeUid, "Section", "args.type") == 3, "a named type wins over the default");
        Check(CommandRulesViews.PickType(types, SectionTypeUid.ToUpperInvariant(), null, "Section", "args.type") == 2, "a type UniqueId resolves case-insensitively");
        Check(Thrown(() => CommandRulesViews.PickType(types, "Building Section", null, "FloorPlan", "args.type")) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.type" } wrong && wrong.Detail!.Contains("Section", StringComparison.Ordinal), "a type of another view family is args_invalid naming its family");
        Check(Thrown(() => CommandRulesViews.PickType(types, "Elevation", null, "FloorPlan", "args.type")) is { Code: RevitCommandCodes.ReferenceNotFound, Field: "args.type" }, "an unknown type name is reference_not_found");

        Check(Thrown(() => CommandRulesViews.CheckTemplate("FloorPlan", "FloorPlan")) is null, "a floor plan template fits a floor plan");
        Check(Thrown(() => CommandRulesViews.CheckTemplate("Section", "FloorPlan")) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.view_template" }, "a section template on a floor plan is args_invalid");
        Check(Thrown(() => CommandRulesViews.CheckNameFree("Level 1", new[] { "Level 2", "level 1" })) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.name" }, "a name differing only in case from an existing view or template is args_invalid (fail closed: Revit name rules are not ordinal)");
        Check(Thrown(() => CommandRulesViews.CheckNameFree("Level 1", new[] { "Level 2", "Level 10" })) is null, "a name no existing view or template uses is free");
        Check(Thrown(() => CommandRulesViews.CheckNameFree("Level 1", new[] { "Level 2", "Level 1" })) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.name" }, "a name already used by a view of the same type is args_invalid");
        Check(Thrown(() => CommandRulesViews.CheckNameFree(null, new[] { "Level 1" })) is null, "no requested name never collides");
        Check(Thrown(() => CommandRulesViews.CheckScaleAllowed(hasDefaultTemplate: false, scale: 50)) is null && Thrown(() => CommandRulesViews.CheckScaleAllowed(hasDefaultTemplate: true, scale: null)) is null, "a scale is allowed when the view family type applies no default template, and no scale is always allowed");
        Check(Thrown(() => CommandRulesViews.CheckScaleAllowed(hasDefaultTemplate: true, scale: 50)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.scale" } scaleRefusal && scaleRefusal.Detail!.Contains("template", StringComparison.Ordinal), "a scale with a view family type that applies a default template is args_invalid on args.scale");

        var args = (ViewSectionCreateArgs)RevitCommandProtocol.Parse(Envelope("view.create_section", Section()).ToJsonString()).Args;
        var frame = CommandRulesViews.Section(args);
        Check(NearV(frame.Origin, new Vec3(3000, 0, 0)) && NearV(frame.BasisX, new Vec3(-1, 0, 0)) && NearV(frame.BasisY, new Vec3(0, 0, 1)) && NearV(frame.BasisZ, new Vec3(0, 1, 0)),
            "section box frame: origin at the line midpoint, Y up, Z the looking direction (left of the line; Revit takes the view direction from BasisZ), X = Y cross Z (against the line)");
        Check(NearV(frame.Min, new Vec3(-3000, -500, 0)) && NearV(frame.Max, new Vec3(3000, 4000, 3000)), "section box: half the line either side, bottom to top elevation, cut plane at z 0 and the far clip depth ahead of it");
        Check(NearV(CommandRulesViews.LookingDirection(args), new Vec3(0, 1, 0)), "a section drawn from start to end looks to the left of the line (+Y for a line along +X)");
        var diagonal = (ViewSectionCreateArgs)RevitCommandProtocol.Parse(Envelope("view.create_section", With(Section(), "line", new JsonObject { ["start"] = Pair(1000, 1000), ["end"] = Pair(4000, 5000) })).ToJsonString()).Args;
        var tilted = CommandRulesViews.Section(diagonal);
        Check(NearV(tilted.BasisX, new Vec3(-0.6, -0.8, 0)) && NearV(tilted.BasisZ, new Vec3(-0.8, 0.6, 0)) && NearV(tilted.Origin, new Vec3(2500, 3000, 0)) && Near(tilted.Max.X, 2500) && NearV(CommandRulesViews.LookingDirection(diagonal), new Vec3(-0.8, 0.6, 0)), "a diagonal section frame is orthonormal and centred on its line");
        SectionOrientations();
        ThreeDOrientations();
    }

    static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    static Vec3 Plus(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    static Vec3 Times(Vec3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    static Vec3 Neg(Vec3 a) => Times(a, -1);
    static Vec3 UnitV(double x, double y, double z) { var n = Math.Sqrt(x * x + y * y + z * z); return new Vec3(x / n, y / n, z / n); }
    static string Num(double value) => value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
    static string Show(Vec3 v) => "(" + Num(v.X) + ", " + Num(v.Y) + ", " + Num(v.Z) + ")";

    // What Revit 2025 makes of the BoundingBoxXYZ handed to ViewSection.CreateSection. API reference: "The view
    // direction of the resulting section will be sectionBox.Transform.BasisZ and the up direction will be
    // sectionBox.Transform.BasisY. The right hand direction will be computed so that (right, up, view direction) form a
    // left handed coordinate system", i.e. right = BasisZ x BasisY (BasisX is not used), and "the far clip distance will
    // be equal to the difference of the z-coordinates of Min and Max". View.ViewDirection points toward the viewer
    // (right x up = -BasisZ). Observed on the owner's Revit 2025.2 (2026-11-04): BasisZ (0.707, -0.707, 0) gave
    // RightDirection (-0.707, -0.707, 0), exactly this model.
    static ObservedSection RevitCreatesSection(SectionFrame f)
    {
        var right = Cross(f.BasisZ, f.BasisY);
        Vec3 Project(Vec3 local) => Plus(Plus(Plus(f.Origin, Times(f.BasisX, local.X)), Times(f.BasisY, local.Y)), Times(f.BasisZ, local.Z));
        return new ObservedSection(right, f.BasisY, Cross(right, f.BasisY), f.Origin, Project(f.Min), Project(f.Max), Math.Abs(f.Max.Z - f.Min.Z), true);
    }

    static ViewSectionCreateArgs SectionAlong(double x0, double y0, double x1, double y1) =>
        (ViewSectionCreateArgs)RevitCommandProtocol.Parse(Envelope("view.create_section", With(Section(), "line", new JsonObject { ["start"] = Pair(x0, y0), ["end"] = Pair(x1, y1) })).ToJsonString()).Args;

    // Every compass direction, the diagonals and two oblique lines: the box Revit receives must give a section whose
    // right direction runs start -> end and that looks to the left of the line, and the readback must accept it.
    static void SectionOrientations()
    {
        var directions = new (string Name, double X, double Y)[]
        {
            ("east", 1, 0), ("north-east", 1, 1), ("north", 0, 1), ("north-west", -1, 1), ("west", -1, 0), ("south-west", -1, -1), ("south", 0, -1), ("south-east", 1, -1),
            ("3:4 oblique", 3, 4), ("-8:3 oblique", -8, 3),
        };
        foreach (var (name, dx, dy) in directions)
        {
            var d = UnitV(dx, dy, 0);
            var args = SectionAlong(1000, 2000, 1000 + 7000 * d.X, 2000 + 7000 * d.Y);
            var frame = CommandRulesViews.Section(args);
            var orthonormal = Near(Dot(frame.BasisX, frame.BasisX), 1) && Near(Dot(frame.BasisY, frame.BasisY), 1) && Near(Dot(frame.BasisZ, frame.BasisZ), 1)
                && Near(Dot(frame.BasisX, frame.BasisY), 0) && Near(Dot(frame.BasisY, frame.BasisZ), 0) && Near(Dot(frame.BasisX, frame.BasisZ), 0);
            Check(orthonormal && NearV(Cross(frame.BasisX, frame.BasisY), frame.BasisZ) && NearV(frame.BasisY, new Vec3(0, 0, 1)),
                "section box frame for a line to the " + name + " is an orthonormal right-handed transform with Y up", Show(frame.BasisX) + " " + Show(frame.BasisY) + " " + Show(frame.BasisZ));
            var revit = RevitCreatesSection(frame);
            var left = new Vec3(-d.Y, d.X, 0);
            Check(NearV(revit.Right, d) && NearV(Neg(revit.ViewDirection), left) && NearV(CommandRulesViews.LookingDirection(args), left),
                "a section drawn to the " + name + " has Revit's right direction along start -> end and looks to the left of the line",
                "right " + Show(revit.Right) + ", toward viewer " + Show(revit.ViewDirection) + ", line " + Show(d));
            var mismatch = CommandReadbackViews.Section(args, revit);
            Check(mismatch is null, "the section Revit creates for a line to the " + name + " passes the readback", mismatch ?? "");
            var reversed = SectionAlong(1000 + 7000 * d.X, 2000 + 7000 * d.Y, 1000, 2000);
            var back = RevitCreatesSection(CommandRulesViews.Section(reversed));
            Check(NearV(back.Right, Neg(d)) && NearV(back.ViewDirection, Neg(revit.ViewDirection)) && CommandReadbackViews.Section(reversed, back) is null,
                "reversing the " + name + " line mirrors the section (right and looking direction flip) and still passes the readback", Show(back.Right) + " " + Show(back.ViewDirection));
        }
        // Owner report (Revit 2025.2, 2026-11-04): line [80550,8550] -> [90450,18450] failed as "section right direction
        // is (-0.707,-0.707,0), expected (0.707,0.707,0)" with the previous frame (BasisZ toward the viewer).
        var owner = SectionAlong(80550, 8550, 90450, 18450);
        var created = RevitCreatesSection(CommandRulesViews.Section(owner));
        var ownerMismatch = CommandReadbackViews.Section(owner, created);
        Check(ownerMismatch is null && NearV(created.Right, UnitV(1, 1, 0)) && NearV(created.ViewDirection, UnitV(1, -1, 0)), "the owner's diagonal section line gives right (0.707, 0.707, 0), looks north-west and passes the readback", ownerMismatch ?? Show(created.Right));
        var flipped = created with { Right = Neg(created.Right), ViewDirection = Neg(created.ViewDirection) };
        Check(CommandReadbackViews.Section(owner, flipped) is { } refused && refused.Contains("right direction", StringComparison.Ordinal), "a section Revit flipped (looking to the right of the line) is still refused");
    }

    static JsonObject ThreeDWith(string member, JsonNode value) => With(ThreeD(false), member, value);

    // view.create_3d orientation: view_from presets and eye_direction, the frame handed to View3D.SetOrientation and the
    // readback of forward/up.
    static void ThreeDOrientations()
    {
        var c = 1 / Math.Sqrt(3);
        var s2 = Math.Sqrt(2.0 / 3);
        var presets = new (string Name, Vec3 Eye)[]
        {
            ("ne", new(c, c, c)), ("nw", new(-c, c, c)), ("se", new(c, -c, c)), ("sw", new(-c, -c, c)),
            ("n", new(0, s2, c)), ("s", new(0, -s2, c)), ("e", new(s2, 0, c)), ("w", new(-s2, 0, c)), ("top", new(0, 0, 1)),
        };
        var schema = JsonNode.Parse(RevitCommandCatalogue.Find("view.create_3d")!.ArgsSchemaJson)!;
        var listed = schema["properties"]?["view_from"]?["enum"]?.AsArray().Select(value => value!.GetValue<string>()).ToArray() ?? Array.Empty<string>();
        Check(listed.SequenceEqual(presets.Select(p => p.Name)), "view.create_3d schema lists the view_from presets ne, nw, se, sw, n, s, e, w, top", string.Join(",", listed));
        Check(schema["properties"]?["eye_direction"]?["type"]?.GetValue<string>() == "array", "view.create_3d schema offers eye_direction as an [x, y, z] array");
        foreach (var (name, eye) in presets)
        {
            var bound = Accepts("3D view from " + name + " accepted", Envelope("view.create_3d", ThreeDWith("view_from", name)))?.Args as View3DCreateArgs;
            Check(bound?.Eye is { } e && NearV(new Vec3(e.X, e.Y, e.Z), eye), "view_from " + name + " binds the unit eye direction " + Show(eye), bound?.Eye is { } got ? Show(new Vec3(got.X, got.Y, got.Z)) : "none");
            var frame = CommandRulesViews.Orientation(new ViewDirection3(eye.X, eye.Y, eye.Z), new Vec3(4000, 3000, 1500), 20000);
            var vertical = name == "top";
            var upOk = Near(Dot(frame.Up, frame.Up), 1) && Near(Dot(frame.Up, frame.Forward), 0)
                && (vertical ? NearV(frame.Up, new Vec3(0, 1, 0)) : frame.Up.Z > 0 && Near(Dot(frame.Up, Cross(new Vec3(0, 0, 1), frame.Forward)), 0));
            Check(NearV(frame.Forward, Neg(eye)) && upOk && NearV(frame.Eye, Plus(new Vec3(4000, 3000, 1500), Times(eye, 20000)), 1e-6),
                "orientation from " + name + ": forward looks from the eye toward the target, up is the vertical tilted toward the view (project north for top), the eye sits on the eye direction",
                "eye " + Show(frame.Eye) + " up " + Show(frame.Up) + " forward " + Show(frame.Forward));
        }
        var se = CommandRulesViews.Orientation(new ViewDirection3(c, -c, c), new Vec3(0, 0, 0), 1000);
        Check(NearV(se.Forward, new Vec3(-c, c, -c)) && NearV(se.Up, UnitV(-1, 1, 2)), "view_from se reproduces Revit's default isometric (forward (-0.577, 0.577, -0.577), up (-0.408, 0.408, 0.816))", Show(se.Up));
        var explicitEye = Accepts("3D view with eye_direction accepted", Envelope("view.create_3d", ThreeDWith("eye_direction", Triple(0, -3, 4))))?.Args as View3DCreateArgs;
        Check(explicitEye?.Eye is { } x && NearV(new Vec3(x.X, x.Y, x.Z), new Vec3(0, -0.6, 0.8)), "eye_direction is normalised to a unit direction from the target toward the eye");
        var below = CommandRulesViews.Orientation(new ViewDirection3(0, 0, -1), new Vec3(0, 0, 0), 1000);
        Check(NearV(below.Forward, new Vec3(0, 0, 1)) && NearV(below.Up, new Vec3(0, 1, 0)), "an eye straight below looks up with project north up");
        Check(Accepts("3D view with a section box and a view_from accepted", Envelope("view.create_3d", With(ThreeD(), "view_from", "nw")))?.Args is View3DCreateArgs { SectionBox: not null, Eye: not null }, "view_from combines with a section box");
        Check(Accepts("3D view without orientation accepted", Envelope("view.create_3d", ThreeD(false)))?.Args is View3DCreateArgs { Eye: null }, "no view_from or eye_direction keeps Revit's default orientation");
        Rejects("3D view_from and eye_direction together", Envelope("view.create_3d", With(ThreeDWith("view_from", "ne"), "eye_direction", Triple(1, 1, 1))), RevitCommandCodes.ArgsInvalid, "args.eye_direction");
        Rejects("3D view_from unknown preset", Envelope("view.create_3d", ThreeDWith("view_from", "bottom")), RevitCommandCodes.ArgsInvalid, "args.view_from");
        Rejects("3D eye_direction zero", Envelope("view.create_3d", ThreeDWith("eye_direction", Triple(0, 0, 0))), RevitCommandCodes.ArgsInvalid, "args.eye_direction");
        Rejects("3D eye_direction too short to normalise", Envelope("view.create_3d", ThreeDWith("eye_direction", Triple(1e-12, 0, 0))), RevitCommandCodes.ArgsInvalid, "args.eye_direction");
        Rejects("3D eye_direction with two values", Envelope("view.create_3d", ThreeDWith("eye_direction", Pair(1, 1))), RevitCommandCodes.ArgsInvalid, "args.eye_direction");
        RejectsText("3D eye_direction overflowing to infinity", Envelope("view.create_3d", ThreeDWith("eye_direction", Triple(1, 2, 3))).ToJsonString().Replace("[1,2,3]", "[1e400,2,3]"), RevitCommandCodes.ArgsInvalid, "args.eye_direction[0]");

        Vec3 X = new(1, 0, 0), Y = new(0, 1, 0), Z = new(0, 0, 1);
        var nw = new ViewDirection3(-c, c, c);
        var nwFrame = CommandRulesViews.Orientation(nw, new Vec3(0, 0, 0), 1000);
        var observed = new Observed3D(false, false, X, Y, Z, null, null, nwFrame.Forward, nwFrame.Up);
        Check(CommandReadbackViews.ThreeD(null, observed, nw) is null, "a 3D view oriented as requested passes the readback");
        Check(CommandReadbackViews.ThreeD(null, observed with { Forward = new Vec3(-c, c, -c) }, nw) is { } forward && forward.Contains("forward direction", StringComparison.Ordinal), "a 3D view looking from another side is a mismatch naming the forward direction");
        Check(CommandReadbackViews.ThreeD(null, observed with { Up = Neg(nwFrame.Up) }, nw) is { } up && up.Contains("up direction", StringComparison.Ordinal), "a 3D view upside down is a mismatch naming the up direction");
        Check(CommandReadbackViews.ThreeD(null, observed with { Forward = new Vec3(-c, c, -c) }) is null, "without a requested orientation the default orientation is not checked");
        // Sign convention (Revit 2025 API: ViewOrientation3D.ForwardDirection is the direction the view looks, from the
        // eye toward the target). "ne" puts the eye at +X +Y above the model, so the view looks south-west and down and
        // shows the north and east faces; Revit's default isometric forward (-0.577, 0.577, -0.577) is an eye at the
        // south-east (+X -Y), looking north-west.
        var target = new Vec3(4000, 3000, 1500);
        var ne = CommandRulesViews.Orientation(new ViewDirection3(c, c, c), target, 20000);
        var toTarget = Plus(target, Neg(ne.Eye));
        Check(ne.Eye.X > target.X && ne.Eye.Y > target.Y && ne.Eye.Z > target.Z && NearV(Times(toTarget, 1 / Math.Sqrt(Dot(toTarget, toTarget))), ne.Forward, 1e-9) && ne.Forward.X < 0 && ne.Forward.Y < 0,
            "view_from ne places the eye north-east of and above the target and forward points from the eye toward the target", "eye " + Show(ne.Eye) + " forward " + Show(ne.Forward));
        var json = JsonNode.Parse(CommandReadbackViews.ThreeDJson(new ObservedView(ViewUid, "ThreeD", ThreeDTypeUid, "{3D}", 100, null, false, null), observed with { Forward = new Vec3(-c, c, -c) }))!["view"]!["three_d"]!;
        Check(json["eye_direction"]?.AsArray().Select(value => value!.GetValue<double>()).Zip(new[] { c, -c, c }, (a, b) => Math.Abs(a - b) < 1e-9).All(ok => ok) == true,
            "3D readback JSON reports eye_direction = -forward (Revit's default isometric reads back as an eye at the south-east)", json.ToJsonString());
    }

    static void Readbacks()
    {
        var none = new ViewOptions(null, null, null, null);
        var plan = new ObservedView(ViewUid, "FloorPlan", PlanTypeUid, "Level 1 (1)", 100, null, false, LevelUid);
        Check(CommandReadbackViews.View("FloorPlan", PlanTypeUid, none, null, LevelUid, plan) is null, "floor plan readback matches with no options");
        Check(CommandReadbackViews.View("CeilingPlan", PlanTypeUid, none, null, LevelUid, plan) is { } kind && kind.Contains("view type", StringComparison.Ordinal), "a view of another view type is a mismatch");
        Check(CommandReadbackViews.View("FloorPlan", CeilingTypeUid, none, null, LevelUid, plan) is { } type && type.Contains("family type", StringComparison.Ordinal), "a view of another view family type is a mismatch");
        Check(CommandReadbackViews.View("FloorPlan", PlanTypeUid, none, null, "60f91daf-3dd7-4283-a86d-24137b73f3da-00000138", plan) is { } level && level.Contains("level", StringComparison.Ordinal), "a plan on another level is a mismatch");
        Check(CommandReadbackViews.View("FloorPlan", PlanTypeUid, none, null, LevelUid, plan with { LevelUid = null }) is not null, "an unreadable plan level is a mismatch");
        Check(CommandReadbackViews.View("FloorPlan", PlanTypeUid, none, null, LevelUid, plan with { IsTemplate = true }) is not null, "a created view that is a template is a mismatch");
        Check(CommandReadbackViews.View("FloorPlan", PlanTypeUid, none with { Name = "Agent Plan" }, null, LevelUid, plan) is { } name && name.Contains("name", StringComparison.Ordinal), "a view whose name differs from the requested one is a mismatch");
        Check(CommandReadbackViews.View("FloorPlan", PlanTypeUid, none with { Name = "Level 1 (1)" }, null, LevelUid, plan) is null, "a requested name read back is a match");
        Check(CommandReadbackViews.View("FloorPlan", PlanTypeUid, none with { Scale = 50 }, null, LevelUid, plan) is { } scale && scale.Contains("scale", StringComparison.Ordinal), "a scale that did not take is a mismatch");
        Check(CommandReadbackViews.View("FloorPlan", PlanTypeUid, none with { Scale = 50 }, null, LevelUid, plan with { Scale = null }) is not null, "an unreadable scale is a mismatch when a scale was requested");
        Check(CommandReadbackViews.View("FloorPlan", PlanTypeUid, none with { Template = "Architectural Plan" }, TemplateUid, LevelUid, plan) is { } template && template.Contains("template", StringComparison.Ordinal), "a template that was not applied is a mismatch");
        Check(CommandReadbackViews.View("FloorPlan", PlanTypeUid, none with { Template = "Architectural Plan" }, TemplateUid, LevelUid, plan with { TemplateUid = TemplateUid }) is null, "an applied template reads back as a match");
        Check(CommandReadbackViews.View("FloorPlan", PlanTypeUid, none, null, LevelUid, plan with { TemplateUid = TemplateUid }) is null, "a template the view family type applies by default is not a mismatch when none was requested");
        Check(CommandReadbackViews.View("ThreeD", ThreeDTypeUid, none, null, null, plan with { ViewType = "ThreeD", TypeUid = ThreeDTypeUid, LevelUid = null }) is null, "a 3D view has no level to check");

        var args = (ViewSectionCreateArgs)RevitCommandProtocol.Parse(Envelope("view.create_section", Section()).ToJsonString()).Args;
        var section = new ObservedSection(new Vec3(1, 0, 0), new Vec3(0, 0, 1), new Vec3(0, -1, 0), new Vec3(3000, 0, 0), new Vec3(0, 0, -500), new Vec3(6000, 0, 4000), 3000, true);
        Check(CommandReadbackViews.Section(args, section) is null, "section readback matches");
        Check(CommandReadbackViews.Section(args, section with { CropMin = new Vec3(6000, 0, -500), CropMax = new Vec3(0, 0, 4000) }) is null, "section crop corners may come in either order along the line");
        Check(CommandReadbackViews.Section(args, section with { ViewDirection = new Vec3(0, 1, 0), Right = new Vec3(-1, 0, 0) }) is { } flipped && flipped.Contains("direction", StringComparison.Ordinal), "a section looking the other way is a mismatch");
        Check(CommandReadbackViews.Section(args, section with { Up = new Vec3(0, 1, 0) }) is not null, "a section whose up direction is not vertical is a mismatch");
        Check(CommandReadbackViews.Section(args, section with { CropMax = new Vec3(5000, 0, 4000) }) is { } width && width.Contains("along", StringComparison.Ordinal), "a crop region shorter than the line is a mismatch");
        Check(CommandReadbackViews.Section(args, section with { CropMax = new Vec3(6000, 0, 3000) }) is { } height && height.Contains("elevation", StringComparison.Ordinal), "a crop region with another top elevation is a mismatch");
        Check(CommandReadbackViews.Section(args, section with { Origin = new Vec3(3000, 500, 0) }) is { } plane && plane.Contains("cut plane", StringComparison.Ordinal), "a cut plane off the requested line is a mismatch");
        Check(CommandReadbackViews.Section(args, section with { Origin = new Vec3(1000, 0, 1200) }) is null, "a view origin elsewhere on the cut plane is a match");
        Check(CommandReadbackViews.Section(args, section with { FarClipMm = 2000 }) is { } depth && depth.Contains("far clip", StringComparison.Ordinal), "a far clip offset other than the depth is a mismatch");
        Check(CommandReadbackViews.Section(args, section with { FarClipMm = null }) is not null, "an unreadable far clip offset is a mismatch");
        Check(CommandReadbackViews.Section(args, section with { CropActive = false }) is not null, "an inactive crop box is a mismatch for a section");

        var box = new ViewBox(Length.FromMillimetres(0), Length.FromMillimetres(0), Length.FromMillimetres(0), Length.FromMillimetres(8000), Length.FromMillimetres(6000), Length.FromMillimetres(3500));
        Vec3 X = new(1, 0, 0), Y = new(0, 1, 0), Z = new(0, 0, 1);
        var iso = new Observed3D(false, true, X, Y, Z, new Vec3(0, 0, 0), new Vec3(8000, 6000, 3500), new Vec3(-0.577, 0.577, -0.577), new Vec3(-0.408, 0.408, 0.816));
        Check(CommandReadbackViews.ThreeD(box, iso) is null, "3D view with section box readback matches");
        Check(CommandReadbackViews.ThreeD(box, iso with { IsPerspective = true }) is { } perspective && perspective.Contains("perspective", StringComparison.Ordinal), "a perspective view is a mismatch for an isometric request");
        Check(CommandReadbackViews.ThreeD(box, iso with { SectionBoxActive = false }) is not null, "an inactive section box is a mismatch when one was requested");
        Check(CommandReadbackViews.ThreeD(box, iso with { BoxMax = new Vec3(8000, 6000, 3000) }) is { } corner && corner.Contains("section box", StringComparison.Ordinal), "a section box of another size is a mismatch");
        Check(CommandReadbackViews.ThreeD(box, iso with { BoxBasisX = new Vec3(0, 1, 0), BoxBasisY = new Vec3(-1, 0, 0) }) is not null, "a rotated section box is a mismatch");
        Check(CommandReadbackViews.ThreeD(box, iso with { BoxMin = null }) is not null, "an unreadable section box is a mismatch when one was requested");
        Check(CommandReadbackViews.ThreeD(null, iso with { SectionBoxActive = false, BoxMin = null, BoxMax = null, BoxBasisX = null, BoxBasisY = null, BoxBasisZ = null }) is null, "3D view without a section box readback matches");
        Check(CommandReadbackViews.ThreeD(null, iso) is not null, "an active section box is a mismatch when none was requested");

        var familyJson = JsonNode.Parse(CommandReadbackViews.ViewFamilyTypesJson(new[] { new ViewFamilyTypeRow("Wall Section", SectionTypeUid, "Section", false), new ViewFamilyTypeRow("Floor Plan", PlanTypeUid, "FloorPlan", true), new ViewFamilyTypeRow("Building Section", "60f91daf-3dd7-4283-a86d-24137b73f3da-00000905", "Section", true) }))!;
        var familyRows = familyJson["view_family_types"]!.AsArray();
        Check(familyRows.Select(row => row!["name"]!.GetValue<string>()).SequenceEqual(new[] { "Floor Plan", "Building Section", "Wall Section" }) && familyRows[0]!["view_family"]!.GetValue<string>() == "FloorPlan" && familyRows[0]!["is_default"]!.GetValue<bool>() && !familyRows[2]!["is_default"]!.GetValue<bool>() && familyRows[2]!["unique_id"]!.GetValue<string>() == SectionTypeUid,
            "view family types readback is sorted by family then name, with family, UniqueId and default flag", familyJson.ToJsonString());
        var templateJson = JsonNode.Parse(CommandReadbackViews.ViewTemplatesJson(new[] { new ViewTemplateRow("Z plan", TemplateUid, "FloorPlan"), new ViewTemplateRow("A section", ViewUid, "Section") }))!;
        Check(templateJson["view_templates"]!.AsArray().Select(row => row!["name"]!.GetValue<string>()).SequenceEqual(new[] { "A section", "Z plan" }) && templateJson["view_templates"]![1]!["view_type"]!.GetValue<string>() == "FloorPlan", "view templates readback is sorted by name with the view type", templateJson.ToJsonString());
        var viewJson = JsonNode.Parse(CommandReadbackViews.ViewJson(plan))!["view"]!;
        Check(viewJson["unique_id"]!.GetValue<string>() == ViewUid && viewJson["view_type"]!.GetValue<string>() == "FloorPlan" && viewJson["type_unique_id"]!.GetValue<string>() == PlanTypeUid && viewJson["name"]!.GetValue<string>() == "Level 1 (1)" && viewJson["scale"]!.GetValue<int>() == 100 && viewJson["view_template_unique_id"] is null && viewJson["level_unique_id"]!.GetValue<string>() == LevelUid,
            "plan readback JSON reports UniqueId, view type, family type, name, scale, template and level", viewJson.ToJsonString());
        var sectionJson = JsonNode.Parse(CommandReadbackViews.SectionJson(plan with { ViewType = "Section", LevelUid = null }, section))!["view"]!;
        Check(sectionJson["section"]!["view_direction"]!.AsArray().Select(value => value!.GetValue<double>()).SequenceEqual(new[] { 0.0, -1, 0 }) && sectionJson["section"]!["far_clip_mm"]!.GetValue<double>() == 3000 && sectionJson["section"]!["crop_max"]![2]!.GetValue<double>() == 4000 && sectionJson["section"]!["crop_box_active"]!.GetValue<bool>() && sectionJson["level_unique_id"] is null,
            "section readback JSON embeds the view and its frame, crop corners and far clip", sectionJson.ToJsonString());
        var threeJson = JsonNode.Parse(CommandReadbackViews.ThreeDJson(plan with { ViewType = "ThreeD", LevelUid = null }, iso))!["view"]!;
        Check(!threeJson["three_d"]!["perspective"]!.GetValue<bool>() && threeJson["three_d"]!["section_box_active"]!.GetValue<bool>() && threeJson["three_d"]!["section_box"]!["max"]![0]!.GetValue<double>() == 8000 && threeJson["three_d"]!["forward_direction"]!.AsArray().Count == 3,
            "3D readback JSON embeds the view, perspective flag, section box and orientation", threeJson.ToJsonString());
        var inactive = JsonNode.Parse(CommandReadbackViews.ThreeDJson(plan with { ViewType = "ThreeD", LevelUid = null }, iso with { SectionBoxActive = false }))!["view"]!;
        Check(inactive["three_d"]!["section_box"] is null && !inactive["three_d"]!["section_box_active"]!.GetValue<bool>(), "3D readback JSON reports a null section box when the box is inactive, even if Revit returns its extents", inactive.ToJsonString());
        Check(CommandReadbackViews.ThreeD(null, iso with { SectionBoxActive = false }) is null, "an inactive section box with readable extents matches a request without a box");
        var plain = JsonNode.Parse(CommandReadbackViews.ThreeDJson(plan with { ViewType = "ThreeD", LevelUid = null }, iso with { SectionBoxActive = false, BoxMin = null, BoxMax = null }))!["view"]!;
        Check(plain["three_d"]!["section_box"] is null, "3D readback JSON reports a null section box when there is none", plain.ToJsonString());
    }
}
