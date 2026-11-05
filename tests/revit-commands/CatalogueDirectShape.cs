using Bim.Core;
using Bim.Revit;
using System.Text.Json;
using System.Text.Json.Nodes;
// direct_shape.create contract oracle (P9-09): DirectShape solids for site and landscape elements, either an extruded
// closed plan polygon on a level or an axis-aligned box. Covers the schema, the typed binder and its rules (3 to 256
// vertices, closed simple polygon, height and coordinate limits, box corners, category list, optional material and
// name), the guarded client path (fake transport), command.batch steps and the Autodesk-free executor rules and
// readback in runtimes/revit-native/CommandsDirectShape.cs. Expected values are computed by hand in the comments, not
// with the implementation. No Revit is involved: a passing run is unit-tested evidence only;
// CommandsExecutorDirectShape.cs is compiled, not run.
static class CatalogueDirectShapeChecks
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string ShapeUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0002f001";
    const string MaterialUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000f01";
    const string OtherMaterialUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000f02";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static JsonObject Envelope(JsonNode args, string path = FixturePath, string command = "direct_shape.create", string operationId = "op-direct-shape") => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = operationId,
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
        ["command"] = command,
        ["args"] = args.DeepClone(),
    };
    static JsonArray Pair(double a, double b) => new(a, b);
    static JsonArray Triple(double a, double b, double c) => new(a, b, c);
    static JsonArray Outline(params (double X, double Y)[] points) => new(points.Select(p => (JsonNode?)Pair(p.X, p.Y)).ToArray());
    // A 1200 x 800 boulder block, 900 mm high, on Level 1.
    static JsonObject Extrusion(JsonArray? outline = null, double height = 900) => new()
    {
        ["kind"] = "extrusion",
        ["level"] = "Level 1",
        ["outline"] = outline ?? Outline((0, 0), (1200, 0), (1200, 800), (0, 800)),
        ["height_mm"] = height,
    };
    static JsonObject BoxShape(JsonArray min, JsonArray max) => new() { ["kind"] = "box", ["min"] = min, ["max"] = max };
    static JsonObject Boulder() => new() { ["category"] = "site", ["shape"] = Extrusion() };
    static JsonObject With(JsonObject args, string member, JsonNode? value) { if (value is null) args.Remove(member); else args[member] = value; return args; }
    static JsonObject WithShape(JsonObject shape) => With(Boulder(), "shape", shape);
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
    static DirectShapeCreateArgs? Accepts(string name, JsonNode envelope)
    {
        try
        {
            var parsed = RevitCommandProtocol.Parse(envelope.ToJsonString());
            Check(parsed.Args is DirectShapeCreateArgs, name, parsed.Args.GetType().Name);
            return parsed.Args as DirectShapeCreateArgs;
        }
        catch (RevitCommandException error) { Check(false, name, error.Code + " at " + error.Field + " (" + error.Detail + ")"); return null; }
        catch (Exception error) { Check(false, name, error.GetType().Name + ": " + error.Message); return null; }
    }
    static DirectShapeCreateArgs Parsed(JsonObject args) => (DirectShapeCreateArgs)RevitCommandProtocol.Parse(Envelope(args).ToJsonString()).Args;

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        Catalogue();
        Binding();
        OutlineRules();
        Limits();
        ClientPath();
        Batch();
        Rules();
        Readback();
        ReadbackJson();
    }

    static void Catalogue()
    {
        Check(RevitCommandCatalogue.DirectShapes.Select(spec => spec.Name).SequenceEqual(new[] { "direct_shape.create" }), "direct shape catalogue lists direct_shape.create");
        var spec = RevitCommandCatalogue.DirectShapes.FirstOrDefault();
        Check(spec is not null && RevitCommandCatalogue.All.Contains(spec) && ReferenceEquals(RevitCommandCatalogue.Find("direct_shape.create"), spec), "direct_shape.create is in the catalogue enumeration and Find resolves it");
        if (spec is null) return;
        var all = RevitCommandCatalogue.All.ToList();
        var at = all.IndexOf(spec);
        Check(at > 0 && all[at - 1].Name == "roof.create_extrusion" && ReferenceEquals(all[^1], RevitCommandCatalogue.Find("command.batch")), "direct_shape.create follows roof.create_extrusion; command.batch stays last");
        Check(RevitCommandCatalogue.All.Select(s => s.Name).Distinct().Count() == RevitCommandCatalogue.All.Count && RevitCommandCatalogue.All.Select(s => s.ToolName).Distinct().Count() == RevitCommandCatalogue.All.Count, "command and tool names stay distinct with direct_shape.create");
        Check(spec.ToolName == "revit_direct_shape_create" && spec.Writes && !RevitCommandCatalogue.IsDestructive(spec.Name), "direct_shape.create is the additive write tool revit_direct_shape_create");
        Check(RevitCommandCatalogue.BatchStepCommands.Contains(spec), "direct_shape.create can be a command.batch step");
        Check(spec.Description.EndsWith('.') && spec.Description.Contains("enabled test copies", StringComparison.Ordinal) && spec.Description.Contains("site", StringComparison.Ordinal)
            && spec.Description.Contains("box", StringComparison.Ordinal) && spec.Description.Contains("extrusion", StringComparison.Ordinal) && spec.Description.Contains("256", StringComparison.Ordinal),
            "the description states the copies-only rule, the site use, both shapes and the vertex cap", spec.Description);
        string? problem = null;
        try { using var schema = JsonDocument.Parse(spec.ArgsSchemaJson); RevitCommandSchema.CheckSupported(schema.RootElement); if (schema.RootElement.GetProperty("type").GetString() != "object") problem = "not an object schema"; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) { problem = error.Message; }
        Check(problem is null, "direct_shape.create args schema is valid JSON using only enforced keywords", problem ?? "");
        var categories = JsonNode.Parse(spec.ArgsSchemaJson)!["properties"]!["category"]!["enum"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        Check(categories.SequenceEqual(new[] { "generic_model", "site", "planting", "entourage", "topography" }) && RevitDirectShapeRules.Categories.SequenceEqual(categories),
            "the schema offers exactly generic_model, site, planting, entourage and topography", string.Join(",", categories));
        Check(RevitDirectShapeRules.BuiltInCategory("generic_model") == "OST_GenericModel" && RevitDirectShapeRules.BuiltInCategory("site") == "OST_Site" && RevitDirectShapeRules.BuiltInCategory("planting") == "OST_Planting"
            && RevitDirectShapeRules.BuiltInCategory("entourage") == "OST_Entourage" && RevitDirectShapeRules.BuiltInCategory("topography") == "OST_Topography",
            "each category maps to its BuiltInCategory name");
        var copies = new[] { @"C:\Repo\.bim\native-runs" };
        Check(PermissionPolicy.RequiredLevel("direct_shape.create", FixturePath, copies) == PermissionLevel.WriteCopies && PermissionPolicy.RequiredLevel("direct_shape.create", RealPath, copies) == PermissionLevel.WriteRealProject,
            "direct_shape.create is write-copies on a copy and write-real-project elsewhere");
        Check(RevitDirectShapeRules.MinVertices == 3 && RevitDirectShapeRules.MaxVertices == 256, "3 to 256 outline vertices");
    }

    static void Binding()
    {
        var boulder = Accepts("extruded boulder accepted", Envelope(Boulder()));
        Check(boulder is { Category: "site", Material: null, Name: null, Shape: DirectShapeExtrusion { Level: "Level 1", Outline.Count: 4 } e } && Near(e.Height.Millimetres, 900) && Near(e.BaseOffset.Millimetres, 0)
            && e.Outline[2].X.Millimetres == 1200 && e.Outline[2].Y.Millimetres == 800,
            "category, level, outline, height bind in millimetres; base offset defaults to 0", boulder?.ToString() ?? "");
        var full = Boulder();
        full["shape"]!["base_offset_mm"] = -150;
        full["material"] = "Stone";
        full["name"] = "boulder 3";
        var named = Accepts("material, name and base offset accepted", Envelope(full));
        Check(named is { Material: "Stone", Name: "boulder 3", Shape: DirectShapeExtrusion n } && Near(n.BaseOffset.Millimetres, -150), "material, name and a negative base offset bind");
        var byUid = Accepts("material by UniqueId accepted", Envelope(With(Boulder(), "material", MaterialUid)));
        Check(byUid?.Material == MaterialUid, "a material UniqueId binds as given (resolved in Revit)");
        // A repeated closing vertex is dropped: the outline stays four corners.
        var closed = Accepts("explicitly closed outline accepted", Envelope(WithShape(Extrusion(Outline((0, 0), (1200, 0), (1200, 800), (0, 800), (0, 0))))));
        Check(closed?.Shape is DirectShapeExtrusion { Outline.Count: 4 }, "a repeated closing vertex is dropped");
        var clockwise = Accepts("clockwise outline accepted", Envelope(WithShape(Extrusion(Outline((0, 0), (0, 800), (1200, 800), (1200, 0))))));
        Check(clockwise?.Shape is DirectShapeExtrusion { Outline.Count: 4 }, "a clockwise outline is accepted (the executor orients it)");
        var box = Accepts("box accepted", Envelope(new JsonObject { ["category"] = "planting", ["shape"] = BoxShape(Triple(-500, -500, 100), Triple(500, 300, 700)) }));
        Check(box is { Category: "planting", Shape: DirectShapeBox b } && b.Min.X.Millimetres == -500 && b.Min.Z.Millimetres == 100 && b.Max.Y.Millimetres == 300 && b.Max.Z.Millimetres == 700,
            "box corners bind in millimetres", box?.ToString() ?? "");
        foreach (var category in new[] { "generic_model", "entourage", "topography" })
            Check(Accepts(category + " accepted", Envelope(With(Boulder(), "category", category)))?.Category == category, "category " + category + " is accepted before the pipe (Revit decides per document)");
        // 256 vertices: a regular polygon of radius 10000 mm (edges 245 mm, well apart).
        var ring = Outline(Enumerable.Range(0, 256).Select(i => (10000 * Math.Cos(2 * Math.PI * i / 256), 10000 * Math.Sin(2 * Math.PI * i / 256))).ToArray());
        Check(Accepts("256 vertices accepted", Envelope(WithShape(Extrusion(ring))))?.Shape is DirectShapeExtrusion { Outline.Count: 256 }, "the 256-vertex cap is accepted");
        Check(Accepts("triangle accepted", Envelope(WithShape(Extrusion(Outline((0, 0), (1000, 0), (500, 900))))))?.Shape is DirectShapeExtrusion { Outline.Count: 3 }, "a triangle is accepted");
    }

    static void OutlineRules()
    {
        var ring = Outline(Enumerable.Range(0, 257).Select(i => (10000 * Math.Cos(2 * Math.PI * i / 257), 10000 * Math.Sin(2 * Math.PI * i / 257))).ToArray());
        Rejects("257 vertices", Envelope(WithShape(Extrusion(ring))), RevitCommandCodes.ArgsInvalid, "args.shape.outline");
        Rejects("two vertices", Envelope(WithShape(Extrusion(Outline((0, 0), (1000, 0))))), RevitCommandCodes.ArgsInvalid, "args.shape.outline");
        // Three vertices with the last repeating the first: two distinct vertices.
        Rejects("three vertices closing onto the first", Envelope(WithShape(Extrusion(Outline((0, 0), (1000, 0), (0, 0))))), RevitCommandCodes.BoundaryTooFewPoints, "args.shape.outline");
        // Bow tie: edge 0 (0,0)-(1000,1000) crosses edge 2 (1000,0)-(0,1000) at (500, 500).
        Rejects("self-intersecting bow tie", Envelope(WithShape(Extrusion(Outline((0, 0), (1000, 1000), (1000, 0), (0, 1000))))), RevitCommandCodes.BoundarySelfIntersecting, "args.shape.outline[0]");
        // Collinear vertices fold back: (1000,0)-(2000,0) then (2000,0)-(0,0) runs back along it; no area either.
        Rejects("collinear outline", Envelope(WithShape(Extrusion(Outline((0, 0), (1000, 0), (2000, 0))))), RevitCommandCodes.BoundarySelfIntersecting);
        // Edge 1 from (1000, 0) to (1000, 0.5) is 0.5 mm long.
        Rejects("edge shorter than 1.2 mm", Envelope(WithShape(Extrusion(Outline((0, 0), (1000, 0), (1000, 0.5), (0, 1000))))), RevitCommandCodes.BoundaryEdgeTooShort, "args.shape.outline[1]");
        // Vertex (500, 0.8), the far end of edge 1, lies 0.8 mm from edge 0 (0,0)-(1000,0): within 1.2 mm.
        Rejects("sliver triangle", Envelope(WithShape(Extrusion(Outline((0, 0), (1000, 0), (500, 0.8))))), RevitCommandCodes.BoundarySelfIntersecting);
    }

    static void Limits()
    {
        Rejects("zero height", Envelope(WithShape(Extrusion(height: 0))), RevitCommandCodes.LengthInvalid, "args.shape.height_mm");
        Rejects("negative height", Envelope(WithShape(Extrusion(height: -900))), RevitCommandCodes.LengthInvalid, "args.shape.height_mm");
        Rejects("height 1 mm", Envelope(WithShape(Extrusion(height: 1))), RevitCommandCodes.LengthInvalid, "args.shape.height_mm");
        Check(Accepts("height 1.2 mm accepted", Envelope(WithShape(Extrusion(height: 1.2)))) is not null, "a 1.2 mm height is accepted");
        Rejects("height beyond Revit's limit", Envelope(WithShape(Extrusion(height: 9_144_001))), RevitCommandCodes.LengthInvalid, "args.shape.height_mm");
        Rejects("height as text", Envelope(WithShape(With(Extrusion(), "height_mm", "900"))), RevitCommandCodes.ArgsInvalid);
        Rejects("vertex beyond Revit's limit", Envelope(WithShape(Extrusion(Outline((-9_144_001, 0), (1200, 0), (1200, 800))))), RevitCommandCodes.LengthInvalid, "args.shape.outline[0][0]");
        Rejects("vertex beyond 1e8 mm", Envelope(WithShape(Extrusion(Outline((0, 0), (1200, 2e8), (1200, 800))))), RevitCommandCodes.LengthInvalid, "args.shape.outline[1][1]");
        Rejects("base offset beyond Revit's limit", Envelope(WithShape(With(Extrusion(), "base_offset_mm", 9_144_001))), RevitCommandCodes.LengthInvalid, "args.shape.base_offset_mm");
        Rejects("extrusion without level", Envelope(WithShape(With(Extrusion(), "level", null))), RevitCommandCodes.ArgsInvalid);
        Rejects("extrusion without height", Envelope(WithShape(With(Extrusion(), "height_mm", null))), RevitCommandCodes.ArgsInvalid);
        Rejects("box with equal x", Envelope(WithShape(BoxShape(Triple(0, 0, 0), Triple(0, 800, 900)))), RevitCommandCodes.LengthInvalid, "args.shape.max[0]");
        Rejects("box 1 mm deep", Envelope(WithShape(BoxShape(Triple(0, 0, 0), Triple(1200, 1, 900)))), RevitCommandCodes.LengthInvalid, "args.shape.max[1]");
        Rejects("box upside down", Envelope(WithShape(BoxShape(Triple(0, 0, 900), Triple(1200, 800, 0)))), RevitCommandCodes.LengthInvalid, "args.shape.max[2]");
        Rejects("box corner beyond Revit's limit", Envelope(WithShape(BoxShape(Triple(0, 0, 0), Triple(1200, 800, 9_144_001)))), RevitCommandCodes.LengthInvalid, "args.shape.max[2]");
        Rejects("box corner with two coordinates", Envelope(WithShape(BoxShape(Pair(0, 0), Triple(1200, 800, 900)))), RevitCommandCodes.ArgsInvalid);
        Rejects("box with an extra member", Envelope(WithShape(With(BoxShape(Triple(0, 0, 0), Triple(1200, 800, 900)), "level", "Level 1"))), RevitCommandCodes.ArgsInvalid);
        Rejects("unknown shape kind", Envelope(WithShape(new JsonObject { ["kind"] = "sphere", ["center"] = Triple(0, 0, 0) })), RevitCommandCodes.ArgsInvalid);
        Rejects("no shape", Envelope(With(Boulder(), "shape", null)), RevitCommandCodes.ArgsInvalid, "args.shape");
        Rejects("unknown category", Envelope(With(Boulder(), "category", "furniture")), RevitCommandCodes.ArgsInvalid, "args.category");
        Rejects("BuiltInCategory spelling refused", Envelope(With(Boulder(), "category", "OST_Site")), RevitCommandCodes.ArgsInvalid, "args.category");
        Rejects("no category", Envelope(With(Boulder(), "category", null)), RevitCommandCodes.ArgsInvalid, "args.category");
        Rejects("empty material", Envelope(With(Boulder(), "material", "")), RevitCommandCodes.ArgsInvalid, "args.material");
        Rejects("name over 256 characters", Envelope(With(Boulder(), "name", new string('n', 257))), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("name with a control character", Envelope(With(Boulder(), "name", "boulder\n3")), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("unexpected argument", Envelope(With(Boulder(), "comment", "x")), RevitCommandCodes.ArgsInvalid, "args.comment");
        Check(RevitDirectShapeRules.BuiltInCategory("site") == "OST_Site", "site maps to OST_Site");
        RevitCommandException? unknown = null;
        try { RevitDirectShapeRules.BuiltInCategory("furniture"); }
        catch (RevitCommandException error) { unknown = error; }
        Check(unknown is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.category" }, "an unknown category maps to args_invalid at args.category");
    }

    static void ClientPath()
    {
        var policy = new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" });
        var forwarded = new List<string>();
        string? Transport(string pipe, string line) { forwarded.Add(line); return null; }
        var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
        var ok = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(Boulder()).ToJsonString(), policy, Transport, noLinks);
        Check(forwarded.Count == 1 && ok.Forwarded, "direct_shape.create on an enabled copy is forwarded", ok.Line);
        var refused = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(Boulder(), RealPath).ToJsonString(), policy, Transport, noLinks);
        Check(refused.IsError && !refused.Forwarded && JsonNode.Parse(refused.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentNotEnabled && forwarded.Count == 1, "direct_shape.create on a real project is document_not_enabled and never forwarded", refused.Line);
        var bowTie = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(WithShape(Extrusion(Outline((0, 0), (1000, 1000), (1000, 0), (0, 1000))))).ToJsonString(), policy, Transport, noLinks);
        Check(bowTie.IsError && !bowTie.Forwarded && JsonNode.Parse(bowTie.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.BoundarySelfIntersecting && forwarded.Count == 1, "a self-intersecting outline is refused before the pipe", bowTie.Line);
        var flat = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(WithShape(Extrusion(height: 0))).ToJsonString(), policy, Transport, noLinks);
        Check(flat.IsError && !flat.Forwarded && JsonNode.Parse(flat.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.LengthInvalid && forwarded.Count == 1, "a zero height is refused before the pipe", flat.Line);
        try
        {
            var a = RevitCommandProtocol.Parse(Envelope(Boulder()).ToJsonString());
            var reordered = new JsonObject { ["shape"] = new JsonObject { ["height_mm"] = 900, ["outline"] = Outline((0, 0), (1200, 0), (1200, 800), (0, 800)), ["level"] = "Level 1", ["kind"] = "extrusion" }, ["category"] = "site" };
            var b = RevitCommandProtocol.Parse(Envelope(reordered).ToJsonString());
            Check(a.CanonicalJson == b.CanonicalJson && a.RequestSha256 == b.RequestSha256, "direct_shape.create requests use the v1 canonical form and hash (member order ignored, vertex order kept)");
            var other = RevitCommandProtocol.Parse(Envelope(WithShape(Extrusion(Outline((0, 0), (0, 800), (1200, 800), (1200, 0))))).ToJsonString());
            Check(other.RequestSha256 != a.RequestSha256, "a different vertex order is a different request");
        }
        catch (RevitCommandException error) { Check(false, "direct_shape.create canonical form", error.Code + " " + error.Detail); }
    }

    static void Batch()
    {
        JsonObject Step(string operationId, JsonNode args) => new() { ["operation_id"] = operationId, ["command"] = "direct_shape.create", ["args"] = args.DeepClone() };
        var box = new JsonObject { ["category"] = "planting", ["shape"] = BoxShape(Triple(-500, -500, 100), Triple(500, 300, 700)), ["name"] = "planter" };
        var batch = new JsonObject { ["steps"] = new JsonArray(Step("s-0", Boulder()), Step("s-1", box)) };
        try
        {
            var parsed = RevitCommandProtocol.Parse(Envelope(batch, command: "command.batch", operationId: "op-batch-shapes").ToJsonString());
            Check(parsed.Args is CommandBatchArgs { Steps: [{ Command: "direct_shape.create", Args: DirectShapeCreateArgs { Shape: DirectShapeExtrusion } }, { Args: DirectShapeCreateArgs { Name: "planter", Shape: DirectShapeBox } }] },
                "a command.batch of two direct_shape.create steps binds each step's typed args");
        }
        catch (RevitCommandException error) { Check(false, "a command.batch of direct_shape.create steps binds", error.Code + " at " + error.Field + " (" + error.Detail + ")"); }
        var bad = new JsonObject { ["steps"] = new JsonArray(Step("s-0", Boulder()), Step("s-1", WithShape(Extrusion(Outline((0, 0), (1000, 1000), (1000, 0), (0, 1000)))))) };
        Rejects("a self-intersecting step outline is refused under its step", Envelope(bad, command: "command.batch", operationId: "op-batch-bad"), RevitCommandCodes.BoundarySelfIntersecting, "args.steps[1].args.shape.outline[0]");
    }

    static DirectShapeCreateArgs LShape() => Parsed(new JsonObject
    {
        ["category"] = "generic_model",
        ["shape"] = new JsonObject { ["kind"] = "extrusion", ["level"] = "Level 0", ["outline"] = Outline((0, 0), (4000, 0), (4000, 1000), (1000, 1000), (1000, 3000), (0, 3000)), ["height_mm"] = 500 },
    });

    static void Rules()
    {
        // Boulder on a level at 3000 with base offset 200: plan 0..1200 x 0..800, z 3200..4100.
        var raised = Boulder();
        raised["shape"]!["base_offset_mm"] = 200;
        var footprint = CommandRulesDirectShape.Footprint(Parsed(raised), 3000);
        Check(Near(footprint.BaseZMm, 3200) && Near(footprint.HeightMm, 900) && footprint.Loop.Count == 4, "an extrusion sits at the level elevation plus base offset and rises by its height");
        var box = CommandRulesDirectShape.ExpectedBox(footprint);
        Check(Near(box.MinX, 0) && Near(box.MinY, 0) && Near(box.MinZ, 3200) && Near(box.MaxX, 1200) && Near(box.MaxY, 800) && Near(box.MaxZ, 4100), "the expected box is the outline extents from base to base + height", box.ToString());
        // 1200 x 800 = 960000 mm2, x 900 = 864000000 mm3; perimeter 4000; tolerance 4000 x 900 + 2 x 960000 = 5520000.
        Check(Near(CommandRulesDirectShape.AreaMm2(footprint), 960_000) && Near(CommandRulesDirectShape.ExpectedVolumeMm3(footprint), 864_000_000) && Near(CommandRulesDirectShape.PerimeterMm(footprint), 4000)
            && Near(CommandRulesDirectShape.VolumeToleranceMm3(footprint), 5_520_000), "area, volume, perimeter and the 1 mm volume tolerance of the boulder block");
        // Clockwise (0,0),(0,800),(1200,800),(1200,0) becomes (0,0),(1200,0),(1200,800),(0,800).
        var clockwise = CommandRulesDirectShape.Footprint(Parsed(WithShape(Extrusion(Outline((0, 0), (0, 800), (1200, 800), (1200, 0))))), 0);
        Check(clockwise.Loop.SequenceEqual(new[] { new PlanPoint(0, 0), new PlanPoint(1200, 0), new PlanPoint(1200, 800), new PlanPoint(0, 800) }), "a clockwise outline is reversed to counter-clockwise, first vertex kept",
            string.Join(" ", clockwise.Loop));
        var ccw = CommandRulesDirectShape.Footprint(Parsed(Boulder()), 0);
        Check(ccw.Loop.SequenceEqual(new[] { new PlanPoint(0, 0), new PlanPoint(1200, 0), new PlanPoint(1200, 800), new PlanPoint(0, 800) }), "a counter-clockwise outline is kept as given");
        // Box -500..500 x -500..300 x 100..700: loop counter-clockwise from min, base 100, height 600; the level is ignored.
        var boxArgs = Parsed(new JsonObject { ["category"] = "planting", ["shape"] = BoxShape(Triple(-500, -500, 100), Triple(500, 300, 700)) });
        var boxPrint = CommandRulesDirectShape.Footprint(boxArgs, 9999);
        Check(boxPrint.Loop.SequenceEqual(new[] { new PlanPoint(-500, -500), new PlanPoint(500, -500), new PlanPoint(500, 300), new PlanPoint(-500, 300) }) && Near(boxPrint.BaseZMm, 100) && Near(boxPrint.HeightMm, 600)
            && Near(CommandRulesDirectShape.ExpectedVolumeMm3(boxPrint), 480_000_000), "a box is a counter-clockwise rectangle from its min corner, base z min.z and height max.z - min.z (1000 x 800 x 600)");
        // L: 4000 x 1000 + 1000 x 2000 = 6000000 mm2, x 500 = 3e9 mm3; perimeter 4000+1000+3000+2000+1000+3000 = 14000.
        var l = CommandRulesDirectShape.Footprint(LShape(), 0);
        Check(Near(CommandRulesDirectShape.AreaMm2(l), 6_000_000) && Near(CommandRulesDirectShape.ExpectedVolumeMm3(l), 3e9) && Near(CommandRulesDirectShape.PerimeterMm(l), 14_000), "a concave L outline has area 6 m2 and perimeter 14 m");
        RevitCommandException? invalid = null;
        try { CommandRulesDirectShape.CheckCategory(false, "topography", "OST_Topography"); }
        catch (RevitCommandException error) { invalid = error; }
        Check(invalid is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.category" } && invalid.Detail!.Contains("OST_Topography", StringComparison.Ordinal), "a category Revit does not accept for a DirectShape is args_invalid at args.category");
        var accepted = true;
        try { CommandRulesDirectShape.CheckCategory(true, "site", "OST_Site"); }
        catch (RevitCommandException) { accepted = false; }
        Check(accepted, "a category Revit accepts passes");
        Check(CommandRulesDirectShape.UniformMaterial(new[] { MaterialUid, MaterialUid }) == MaterialUid && CommandRulesDirectShape.UniformMaterial(new[] { MaterialUid, OtherMaterialUid }) is null
            && CommandRulesDirectShape.UniformMaterial(new string?[] { MaterialUid, null }) is null && CommandRulesDirectShape.UniformMaterial(Array.Empty<string?>()) is null, "a uniform material needs every face to carry the same one");
    }

    static string?[] Faces(int count, string? uid) => Enumerable.Repeat(uid, count).ToArray();
    // Boulder on a level at 0: box 0..1200 x 0..800 x 0..900, volume 864000000.
    static ObservedDirectShape Observed() => new(ShapeUid, "OST_Site", "", 1, 864_000_000, Faces(6, null), new BoxMm(0, 0, 0, 1200, 800, 900));

    static void Readback()
    {
        var args = Parsed(Boulder());
        var footprint = CommandRulesDirectShape.Footprint(args, 0);
        string? Check1(ObservedDirectShape observed, DirectShapeCreateArgs? with = null, string? material = null) =>
            CommandReadbackDirectShape.Shape(with ?? args, "OST_Site", material, with is null ? footprint : CommandRulesDirectShape.Footprint(with, 0), observed);
        Check(Check1(Observed()) is null, "the boulder read back as requested matches", Check1(Observed()) ?? "");
        Check(Check1(Observed() with { Box = new BoxMm(-0.8, 0.6, -0.9, 1200.9, 799.2, 900.5) }) is null, "a bounding box within 1 mm on every side matches");
        Check(Check1(Observed() with { Box = new BoxMm(0, 0, 0, 1201.5, 800, 900) }) is { } wide && wide.Contains("bounding box", StringComparison.Ordinal), "a bounding box 1.5 mm wider is a mismatch");
        Check(Check1(Observed() with { Box = new BoxMm(0, 0, 25, 1200, 800, 925) }) is not null, "a box 25 mm high is a mismatch");
        Check(Check1(Observed() with { Box = null }) is { } none && none.Contains("bounding box", StringComparison.Ordinal), "no bounding box is a mismatch");
        Check(Check1(Observed() with { CategoryBuiltIn = "OST_GenericModel" }) is { } category && category.Contains("category", StringComparison.Ordinal), "another category is a mismatch");
        Check(Check1(Observed() with { CategoryBuiltIn = null }) is not null, "no category is a mismatch");
        Check(Check1(Observed() with { Solids = 0, VolumeMm3 = null }) is { } empty && empty.Contains("solid", StringComparison.Ordinal), "no solid is a mismatch");
        Check(Check1(Observed() with { Solids = 2 }) is not null, "two solids are a mismatch");
        // Within 5520000 mm3 (a 1 mm face offset) of 864000000.
        Check(Check1(Observed() with { VolumeMm3 = 864_000_000 + 5_000_000 }) is null, "a volume within the 1 mm face-offset tolerance matches");
        Check(Check1(Observed() with { VolumeMm3 = 864_000_000 + 6_000_000 }) is { } volume && volume.Contains("volume", StringComparison.Ordinal), "a volume beyond the tolerance is a mismatch");
        Check(Check1(Observed() with { VolumeMm3 = null }) is not null, "an unreadable volume is a mismatch");
        // The L outline's bounding box equals its full 4000 x 3000 rectangle's; only the volume tells them apart.
        var l = LShape();
        var lObserved = new ObservedDirectShape(ShapeUid, "OST_GenericModel", "", 1, 3e9, Faces(8, null), new BoxMm(0, 0, 0, 4000, 3000, 500));
        Check(CommandReadbackDirectShape.Shape(l, "OST_GenericModel", null, CommandRulesDirectShape.Footprint(l, 0), lObserved) is null, "the concave L read back as requested matches");
        Check(CommandReadbackDirectShape.Shape(l, "OST_GenericModel", null, CommandRulesDirectShape.Footprint(l, 0), lObserved with { VolumeMm3 = 6e9 }) is { } filled && filled.Contains("volume", StringComparison.Ordinal),
            "a filled rectangle where an L was requested is a mismatch (same bounding box)");
        var named = Parsed(With(Boulder(), "name", "boulder 3"));
        Check(Check1(Observed() with { Name = "boulder 3" }, named) is null, "a requested name read back matches");
        Check(Check1(Observed() with { Name = "DirectShape" }, named) is { } name && name.Contains("name", StringComparison.Ordinal), "another name is a mismatch");
        Check(Check1(Observed() with { Name = "anything" }) is null, "the name is not checked when none was requested");
        var stone = Parsed(With(Boulder(), "material", "Stone"));
        Check(Check1(Observed() with { FaceMaterialUids = Faces(6, MaterialUid) }, stone, MaterialUid) is null, "every face carrying the requested material matches");
        Check(Check1(Observed() with { FaceMaterialUids = Faces(5, MaterialUid).Append(OtherMaterialUid).ToArray() }, stone, MaterialUid) is { } mixed && mixed.Contains("material", StringComparison.Ordinal), "a face with another material is a mismatch");
        Check(Check1(Observed(), stone, MaterialUid) is not null, "faces without the requested material are a mismatch");
        Check(Check1(Observed() with { FaceMaterialUids = Faces(6, OtherMaterialUid) }) is null, "the material is not checked when none was requested");
        // A box: -500..500 x -500..300 x 100..700, volume 480000000.
        var boxArgs = Parsed(new JsonObject { ["category"] = "planting", ["shape"] = BoxShape(Triple(-500, -500, 100), Triple(500, 300, 700)) });
        var boxObserved = new ObservedDirectShape(ShapeUid, "OST_Planting", null, 1, 480_000_000, Faces(6, null), new BoxMm(-500, -500, 100, 500, 300, 700));
        Check(CommandReadbackDirectShape.Shape(boxArgs, "OST_Planting", null, CommandRulesDirectShape.Footprint(boxArgs, 0), boxObserved) is null, "a box read back as requested matches");
        Check(CommandReadbackDirectShape.Shape(boxArgs, "OST_Planting", null, CommandRulesDirectShape.Footprint(boxArgs, 0), boxObserved with { Box = new BoxMm(-500, -500, 0, 500, 300, 600) }) is not null, "a box 100 mm low is a mismatch");
    }

    static void ReadbackJson()
    {
        var args = Parsed(With(With(Boulder(), "material", "Stone"), "name", "boulder 3"));
        var json = JsonNode.Parse(CommandReadbackDirectShape.ShapeJson(Observed() with { Name = "boulder 3", FaceMaterialUids = Faces(6, MaterialUid) }, args))!["direct_shape"]!;
        Check(json["unique_id"]!.GetValue<string>() == ShapeUid && json["category"]!.GetValue<string>() == "site" && json["built_in_category"]!.GetValue<string>() == "OST_Site" && json["shape_kind"]!.GetValue<string>() == "extrusion"
            && json["name"]!.GetValue<string>() == "boulder 3" && json["material_unique_id"]!.GetValue<string>() == MaterialUid && json["volume_mm3"]!.GetValue<double>() == 864_000_000
            && json["bounding_box_mm"]!["min"]!.AsArray().Count == 3 && json["bounding_box_mm"]!["max"]![0]!.GetValue<double>() == 1200 && json["bounding_box_mm"]!["max"]![2]!.GetValue<double>() == 900,
            "direct shape readback reports UniqueId, category, built-in category, shape kind, name, material, volume and bounding box", json.ToJsonString());
        var boxArgs = Parsed(new JsonObject { ["category"] = "planting", ["shape"] = BoxShape(Triple(-500, -500, 100), Triple(500, 300, 700)) });
        var bare = JsonNode.Parse(CommandReadbackDirectShape.ShapeJson(Observed() with { CategoryBuiltIn = null, Name = null, VolumeMm3 = null, Box = null, FaceMaterialUids = new string?[] { MaterialUid, OtherMaterialUid } }, boxArgs))!["direct_shape"]!;
        Check(bare["shape_kind"]!.GetValue<string>() == "box" && bare["built_in_category"] is null && bare["name"] is null && bare["material_unique_id"] is null && bare["volume_mm3"] is null && bare["bounding_box_mm"] is null,
            "missing values and a mixed material read back as null", bare.ToJsonString());
    }
}
