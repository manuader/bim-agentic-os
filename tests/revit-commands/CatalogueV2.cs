using Bim.Core;
using Bim.Revit;
using System.Text.Json;
using System.Text.Json.Nodes;
// P9-01 catalogue v2 contract oracle: roofs, stairs, curtain walls and floor openings. Covers the schemas, the typed
// validators, the guarded client path (fake transport) and the Autodesk-free executor rules and readback checks that
// runtimes/revit-native/CommandsV2.cs supplies. No Revit is involved: a passing run is unit-tested evidence only; the
// executor in CommandsExecutorV2.cs is compiled, never verified in Revit.
static class CatalogueV2Checks
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string FloorUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0002a1c4";
    const string LevelUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000137";
    const string TopUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000138";
    const string TypeUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000200";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static JsonObject Envelope(string command, JsonNode args, string path = FixturePath) => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-v2",
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
        ["command"] = command,
        // A JsonNode has one parent: each envelope gets its own copy so one args object can build several envelopes.
        ["args"] = args.DeepClone(),
    };
    static JsonArray Pair(double x, double y) => new(x, y);
    static JsonArray Outline(params (double X, double Y)[] points) => new(points.Select(p => (JsonNode?)Pair(p.X, p.Y)).ToArray());
    static readonly (double, double)[] Rectangle = { (0, 0), (8000, 0), (8000, 6000), (0, 6000) };
    static JsonObject Roof(double? slope = 30) { var roof = new JsonObject { ["level"] = "Level 2", ["type"] = "Basic Roof - Generic 300mm", ["boundary"] = Outline(Rectangle) }; if (slope is { } s) roof["slope_deg"] = s; return roof; }
    static JsonObject Stair() => new() { ["base_level"] = "Level 1", ["top_level"] = "Level 2", ["run"] = new JsonObject { ["start"] = Pair(0, 0), ["end"] = Pair(4000, 0) }, ["width_mm"] = 1100 };
    static JsonObject LineCurve(double x0, double y0, double x1, double y1) => new() { ["kind"] = "line", ["start"] = Pair(x0, y0), ["end"] = Pair(x1, y1) };
    static JsonObject Curtain(JsonNode? curve = null, JsonObject? grid = null) { var wall = new JsonObject { ["level"] = "Level 1", ["type"] = "Curtain Wall 1", ["height_mm"] = 4000, ["curve"] = curve ?? LineCurve(0, 0, 5000, 0) }; if (grid is not null) wall["grid"] = grid; return wall; }
    static JsonObject Opening() => new() { ["host_floor_uid"] = FloorUid, ["profile"] = Outline((1000, 1000), (2000, 1000), (2000, 2500), (1000, 2500)) };
    static Point2 P(double x, double y) => new(Length.FromMillimetres(x), Length.FromMillimetres(y));
    static bool Near(double a, double b, double tolerance = 1e-6) => Math.Abs(a - b) <= tolerance;

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
    static string? CodeOf(Action action)
    {
        try { action(); return null; }
        catch (RevitCommandException error) { return error.Code; }
    }
    static JsonObject With(JsonObject args, string member, JsonNode? value) { if (value is null) args.Remove(member); else args[member] = value; return args; }

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        Catalogue();
        Roofs();
        Stairs();
        CurtainWalls();
        FloorOpenings();
        ClientPath();
        ExecutorRules();
        Readbacks();
    }

    static void Catalogue()
    {
        var v2 = RevitCommandCatalogue.V2.Select(spec => spec.Name).ToArray();
        Check(v2.SequenceEqual(new[] { "query.roof_types", "roof.create_footprint", "stair.create", "curtain_wall.create", "floor.create_opening" }), "catalogue v2 lists query.roof_types and the four new creators in order", string.Join(",", v2));
        Check(RevitCommandCatalogue.V1.Select(spec => spec.Name).SequenceEqual(new[] { "query.levels", "query.wall_types", "query.floor_types", "query.family_symbols", "wall.create", "opening.place", "floor.create", "room.create" }), "catalogue v1 is unchanged by v2");
        Check(RevitCommandCatalogue.All.Take(13).SequenceEqual(RevitCommandCatalogue.V1.Concat(RevitCommandCatalogue.V2)), "All starts with v1 then v2 (later catalogue parts follow)");
        Check(RevitCommandCatalogue.V1.Concat(RevitCommandCatalogue.V2).Select(spec => spec.Name).Distinct().Count() == 13 && RevitCommandCatalogue.V1.Concat(RevitCommandCatalogue.V2).Select(spec => spec.ToolName).Distinct().Count() == 13, "command and tool names stay distinct across v1 and v2");
        Check(RevitCommandCatalogue.V2.All(spec => System.Text.RegularExpressions.Regex.IsMatch(spec.ToolName, @"\Arevit_[a-z_]+\z")), "v2 tool names are revit_<command with . replaced by _>");
        Check(RevitCommandCatalogue.V2.Count(spec => spec.Writes) == 4 && RevitCommandCatalogue.Find("query.roof_types") is { Writes: false }, "v2 query is read-only and the four creators write");
        foreach (var name in v2) Check(ReferenceEquals(RevitCommandCatalogue.Find(name), RevitCommandCatalogue.V2.Single(spec => spec.Name == name)), "Find resolves v2 command " + name);
        Check(RevitCommandCatalogue.V2.All(spec => spec.Description.Length > 20 && spec.Description.EndsWith('.') && (!spec.Writes || spec.Description.Contains("enabled test copies", StringComparison.Ordinal))), "every v2 command has a plain-English description; writes state the copies-only rule");
        foreach (var spec in RevitCommandCatalogue.V2)
        {
            string? problem = null;
            try { using var schema = JsonDocument.Parse(spec.ArgsSchemaJson); RevitCommandSchema.CheckSupported(schema.RootElement); if (schema.RootElement.GetProperty("type").GetString() != "object") problem = "not an object schema"; }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { problem = error.Message; }
            Check(problem is null, spec.Name + " args schema is valid JSON using only enforced keywords", problem ?? "");
        }
        var copies = new[] { @"C:\Repo\.bim\native-runs" };
        foreach (var spec in RevitCommandCatalogue.V2)
            Check(PermissionPolicy.RequiredLevel(spec.Name, FixturePath, copies) == (spec.Writes ? PermissionLevel.WriteCopies : PermissionLevel.Read) && (!spec.Writes || PermissionPolicy.RequiredLevel(spec.Name, RealPath, copies) == PermissionLevel.WriteRealProject), "permission level of " + spec.Name + " follows its catalogue entry");
        Check(RevitCommandCodes.ClientSide.Contains(RevitCommandCodes.LevelsNotDistinct) && RevitCommandCodes.LevelsNotDistinct == "levels_not_distinct", "levels_not_distinct is a client-side code");
        Check(Accepts("query.roof_types with {} accepted", Envelope("query.roof_types", new JsonObject()))?.Args is EmptyQueryArgs, "query.roof_types binds empty arguments");
        Rejects("query.roof_types with an argument", Envelope("query.roof_types", new JsonObject { ["x"] = 1 }), RevitCommandCodes.ArgsInvalid, "args.x");
    }

    static void Roofs()
    {
        var hip = Accepts("hip footprint roof accepted", Envelope("roof.create_footprint", Roof()));
        Check(hip?.Args is RoofFootprintCreateArgs { Boundary.Count: 4, Slope: { } slope } roof && Near(slope.Degrees, 30) && roof.BaseOffset.Millimetres == 0 && roof.Level == "Level 2" && roof.Type == "Basic Roof - Generic 300mm" && Near(roof.Boundary[2].Y.Millimetres, 6000),
            "hip roof binds four vertices, slope 30 degrees and default base offset 0");
        Check(Accepts("flat footprint roof accepted", Envelope("roof.create_footprint", Roof(null)))?.Args is RoofFootprintCreateArgs { Slope: null }, "omitted slope binds a flat roof");
        Check(Accepts("roof with negative base offset accepted", Envelope("roof.create_footprint", With(Roof(), "base_offset_mm", -500)))?.Args is RoofFootprintCreateArgs { BaseOffset.Millimetres: -500 }, "roof base offset binds -500 mm");
        Check(Accepts("explicitly closed footprint accepted", Envelope("roof.create_footprint", With(Roof(), "boundary", Outline((0, 0), (8000, 0), (8000, 6000), (0, 6000), (0, 0)))))?.Args is RoofFootprintCreateArgs { Boundary.Count: 4 }, "repeated closing footprint vertex is dropped");
        Accepts("concave L-shaped footprint accepted", Envelope("roof.create_footprint", With(Roof(), "boundary", Outline((0, 0), (8000, 0), (8000, 3000), (4000, 3000), (4000, 6000), (0, 6000)))));
        foreach (var (label, value) in new (string, double)[] { ("zero", 0), ("negative", -5), ("90", 90), ("over 90", 120), ("89.9 on an 8 m footprint", 89.9), ("just above the 85 degree cap", 85.001) })
            Rejects("roof slope " + label + " is angle_invalid", Envelope("roof.create_footprint", Roof(value)), RevitCommandCodes.AngleInvalid, "args.slope_deg");
        Accepts("roof slope 85 on an 8 m footprint accepted", Envelope("roof.create_footprint", Roof(85)));
        Rejects("85 degree roof on a 20 km footprint rises above the Revit limit", Envelope("roof.create_footprint", With(Roof(85), "boundary", Outline((0, 0), (2e7, 0), (2e7, 2e7), (0, 2e7)))), RevitCommandCodes.AngleInvalid, "args.slope_deg");
        Accepts("flat roof on a 20 km footprint accepted (no rise)", Envelope("roof.create_footprint", With(Roof(null), "boundary", Outline((0, 0), (2e7, 0), (2e7, 2e7), (0, 2e7)))));
        Check(Near(RevitCommandGeometryV2.MinimumWidthMm(new[] { P(0, 0), P(8000, 0), P(8000, 6000), P(0, 6000) }), 6000, 1e-9), "minimum width of an 8 x 6 m rectangle is 6000 mm");
        Check(Near(RevitCommandGeometryV2.MinimumWidthMm(new[] { P(0, 0), P(8000, 0), P(8000, 3000), P(4000, 3000), P(4000, 6000), P(0, 6000) }), 6000, 1e-9), "minimum width of an L footprint is taken over its convex hull");
        Check(Near(RevitCommandGeometryV2.MinimumWidthMm(new[] { P(0, 0), P(4000, 0), P(0, 3000) }), 2400, 1e-9), "minimum width of a 3-4-5 triangle is its smallest height (2400 mm)");
        Check(RevitCommandGeometryV2.MaxRoofSlopeDeg == 85, "roof slope cap is 85 degrees");
        RejectsText("roof slope overflowing to infinity", Envelope("roof.create_footprint", Roof()).ToJsonString().Replace("\"slope_deg\":30", "\"slope_deg\":1e400"), RevitCommandCodes.AngleInvalid, "args.slope_deg");
        Rejects("roof slope as string", Envelope("roof.create_footprint", With(Roof(), "slope_deg", "30")), RevitCommandCodes.ArgsInvalid, "args.slope_deg");
        Rejects("roof base offset beyond the Revit limit", Envelope("roof.create_footprint", With(Roof(), "base_offset_mm", 9_144_001)), RevitCommandCodes.LengthInvalid, "args.base_offset_mm");
        Rejects("roof without type", Envelope("roof.create_footprint", With(Roof(), "type", null)), RevitCommandCodes.ArgsInvalid, "args.type");
        Rejects("roof without level", Envelope("roof.create_footprint", With(Roof(), "level", null)), RevitCommandCodes.ArgsInvalid, "args.level");
        Rejects("roof with unexpected member", Envelope("roof.create_footprint", With(Roof(), "pitch", 30)), RevitCommandCodes.ArgsInvalid, "args.pitch");
        Rejects("roof footprint with two vertices", Envelope("roof.create_footprint", With(Roof(), "boundary", Outline((0, 0), (1000, 0)))), RevitCommandCodes.ArgsInvalid, "args.boundary");
        Rejects("roof footprint collapsing to two vertices", Envelope("roof.create_footprint", With(Roof(), "boundary", Outline((0, 0), (1000, 0), (0, 0)))), RevitCommandCodes.BoundaryTooFewPoints, "args.boundary");
        Rejects("bow-tie roof footprint", Envelope("roof.create_footprint", With(Roof(), "boundary", Outline((0, 0), (2000, 2000), (2000, 0), (0, 2000)))), RevitCommandCodes.BoundarySelfIntersecting);
        Rejects("roof footprint with a repeated vertex", Envelope("roof.create_footprint", With(Roof(), "boundary", Outline((0, 0), (4000, 0), (4000, 0), (4000, 3000)))), RevitCommandCodes.BoundaryEdgeTooShort, "args.boundary[1]");
        RejectsText("roof footprint vertex overflowing to infinity", Envelope("roof.create_footprint", Roof()).ToJsonString().Replace("[8000,0]", "[1e400,0]"), RevitCommandCodes.LengthInvalid, "args.boundary[1][0]");
    }

    static void Stairs()
    {
        var stair = Accepts("straight stair accepted", Envelope("stair.create", Stair()));
        Check(stair?.Args is StairCreateArgs { BaseLevel: "Level 1", TopLevel: "Level 2" } s && Near(s.RunEnd.X.Millimetres, 4000) && Near(s.Width.Millimetres, 1100) && s.RunStart.Y.Millimetres == 0, "stair binds base and top level, run line and width");
        Rejects("stair with the same base and top level", Envelope("stair.create", With(Stair(), "top_level", "Level 1")), RevitCommandCodes.LevelsNotDistinct, "args.top_level");
        Check(Accepts("stair with base and top level differing only in case accepted (references are ordinal)", Envelope("stair.create", With(Stair(), "top_level", "level 1"))) is not null, "level references compare ordinally");
        Rejects("stair run of zero length", Envelope("stair.create", With(Stair(), "run", new JsonObject { ["start"] = Pair(0, 0), ["end"] = Pair(0, 0) })), RevitCommandCodes.CurveTooShort, "args.run");
        Rejects("stair run of 1 mm", Envelope("stair.create", With(Stair(), "run", new JsonObject { ["start"] = Pair(0, 0), ["end"] = Pair(1, 0) })), RevitCommandCodes.CurveTooShort, "args.run");
        foreach (var (label, value) in new (string, double)[] { ("zero", 0), ("negative", -900), ("under 1.2 mm", 1), ("above the Revit limit", 9_144_001) })
            Rejects("stair width " + label, Envelope("stair.create", With(Stair(), "width_mm", value)), RevitCommandCodes.LengthInvalid, "args.width_mm");
        Rejects("stair without top level", Envelope("stair.create", With(Stair(), "top_level", null)), RevitCommandCodes.ArgsInvalid, "args.top_level");
        Rejects("stair without width", Envelope("stair.create", With(Stair(), "width_mm", null)), RevitCommandCodes.ArgsInvalid, "args.width_mm");
        Rejects("stair run without end", Envelope("stair.create", With(Stair(), "run", new JsonObject { ["start"] = Pair(0, 0) })), RevitCommandCodes.ArgsInvalid, "args.run.end");
        Rejects("stair run with unexpected member", Envelope("stair.create", With(Stair(), "run", new JsonObject { ["start"] = Pair(0, 0), ["end"] = Pair(4000, 0), ["kind"] = "line" })), RevitCommandCodes.ArgsInvalid, "args.run.kind");
        Rejects("stair run coordinate beyond 1e8 mm", Envelope("stair.create", With(Stair(), "run", new JsonObject { ["start"] = Pair(2e8, 0), ["end"] = Pair(4000, 0) })), RevitCommandCodes.LengthInvalid, "args.run.start[0]");
        Rejects("empty stair base level", Envelope("stair.create", With(Stair(), "base_level", "")), RevitCommandCodes.ArgsInvalid, "args.base_level");
    }

    static void CurtainWalls()
    {
        var gridded = Accepts("curtain wall with grid accepted", Envelope("curtain_wall.create", Curtain(grid: new JsonObject { ["vertical_spacing_mm"] = 1000, ["horizontal_spacing_mm"] = 1500 })));
        Check(gridded?.Args is CurtainWallCreateArgs { Curve: LineWallCurve, VerticalSpacing: { } v, HorizontalSpacing: { } h } c && Near(v.Millimetres, 1000) && Near(h.Millimetres, 1500) && Near(c.Height.Millimetres, 4000) && c.BaseOffset.Millimetres == 0 && c.Type == "Curtain Wall 1",
            "curtain wall binds line curve, height, default offset and both grid spacings");
        Check(Accepts("curtain wall without grid accepted", Envelope("curtain_wall.create", Curtain()))?.Args is CurtainWallCreateArgs { VerticalSpacing: null, HorizontalSpacing: null }, "omitted grid binds no extra grid lines");
        Check(Accepts("curtain wall with vertical grid only accepted", Envelope("curtain_wall.create", Curtain(grid: new JsonObject { ["vertical_spacing_mm"] = 1250 })))?.Args is CurtainWallCreateArgs { VerticalSpacing: not null, HorizontalSpacing: null }, "one grid direction is enough");
        Check(Accepts("curved curtain wall (three-point arc) accepted", Envelope("curtain_wall.create", Curtain(new JsonObject { ["kind"] = "arc", ["start"] = Pair(0, 0), ["end"] = Pair(2000, 0), ["through"] = Pair(1000, 1000) })))?.Args is CurtainWallCreateArgs { Curve: ArcThroughWallCurve }, "curtain wall accepts the wall.create arc forms");
        Accepts("curved curtain wall (centre arc) accepted", Envelope("curtain_wall.create", Curtain(new JsonObject { ["kind"] = "arc", ["center"] = Pair(0, 0), ["radius_mm"] = 3000, ["start_angle_deg"] = 0, ["end_angle_deg"] = 90 }, new JsonObject { ["vertical_spacing_mm"] = 900 })));
        Rejects("collinear curved curtain wall", Envelope("curtain_wall.create", Curtain(new JsonObject { ["kind"] = "arc", ["start"] = Pair(0, 0), ["end"] = Pair(2000, 0), ["through"] = Pair(1000, 0) })), RevitCommandCodes.ArcCollinear, "args.curve");
        Rejects("zero-length curtain wall", Envelope("curtain_wall.create", Curtain(LineCurve(0, 0, 0, 0))), RevitCommandCodes.CurveTooShort, "args.curve");
        Rejects("curtain wall height zero", Envelope("curtain_wall.create", With(Curtain(), "height_mm", 0)), RevitCommandCodes.LengthInvalid, "args.height_mm");
        Rejects("curtain wall height above the Revit limit", Envelope("curtain_wall.create", With(Curtain(), "height_mm", 9_144_001)), RevitCommandCodes.LengthInvalid, "args.height_mm");
        Rejects("curtain wall base offset beyond the Revit limit", Envelope("curtain_wall.create", With(Curtain(), "base_offset_mm", -9_144_001)), RevitCommandCodes.LengthInvalid, "args.base_offset_mm");
        Rejects("curtain wall cannot be structural", Envelope("curtain_wall.create", With(Curtain(), "structural", true)), RevitCommandCodes.ArgsInvalid, "args.structural");
        Rejects("curtain wall without type", Envelope("curtain_wall.create", With(Curtain(), "type", null)), RevitCommandCodes.ArgsInvalid, "args.type");
        Rejects("empty curtain grid", Envelope("curtain_wall.create", Curtain(grid: new JsonObject())), RevitCommandCodes.ArgsInvalid, "args.grid");
        Rejects("curtain grid with unexpected member", Envelope("curtain_wall.create", Curtain(grid: new JsonObject { ["u_spacing_mm"] = 1000 })), RevitCommandCodes.ArgsInvalid, "args.grid.u_spacing_mm");
        foreach (var (label, value) in new (string, double)[] { ("zero", 0), ("negative", -1000), ("under 1.2 mm", 1) })
            Rejects("vertical grid spacing " + label, Envelope("curtain_wall.create", Curtain(grid: new JsonObject { ["vertical_spacing_mm"] = value })), RevitCommandCodes.LengthInvalid, "args.grid.vertical_spacing_mm");
        Rejects("vertical grid spacing leaving no line inside a 5000 mm wall", Envelope("curtain_wall.create", Curtain(grid: new JsonObject { ["vertical_spacing_mm"] = 4999 })), RevitCommandCodes.LengthInvalid, "args.grid.vertical_spacing_mm");
        Rejects("vertical grid spacing making more than 500 lines", Envelope("curtain_wall.create", Curtain(grid: new JsonObject { ["vertical_spacing_mm"] = 5 })), RevitCommandCodes.LengthInvalid, "args.grid.vertical_spacing_mm");
        Rejects("horizontal grid spacing taller than the wall", Envelope("curtain_wall.create", Curtain(grid: new JsonObject { ["horizontal_spacing_mm"] = 4500 })), RevitCommandCodes.LengthInvalid, "args.grid.horizontal_spacing_mm");
        Accepts("vertical grid spacing of 10 mm on a 5000 mm wall (499 lines) accepted", Envelope("curtain_wall.create", Curtain(grid: new JsonObject { ["vertical_spacing_mm"] = 10 })));
        Check(RevitCommandGeometryV2.GridLineCount(5000, 1000) == 4 && RevitCommandGeometryV2.GridPositions(5000, 1000).SequenceEqual(new[] { 1000.0, 2000, 3000, 4000 }), "grid lines sit at whole spacings strictly inside the extent (none at the ends)");
        Check(RevitCommandGeometryV2.GridLineCount(5000, 1250) == 3 && RevitCommandGeometryV2.GridLineCount(5000, 2499) == 2 && RevitCommandGeometryV2.GridLineCount(5000, 2500) == 1 && RevitCommandGeometryV2.GridLineCount(5000, 4999) == 0 && RevitCommandGeometryV2.GridLineCount(1, 0.5) == 0, "grid line count keeps 1.2 mm clear of the far end");
        Check(Near(RevitCommandGeometryV2.CurveLengthMm(new LineWallCurve(P(0, 0), P(3000, 4000))), 5000) && Near(RevitCommandGeometryV2.CurveLengthMm(new ArcCenterWallCurve(P(0, 0), Length.FromMillimetres(1000), Angle.FromDegrees(0), Angle.FromDegrees(90))), Math.PI * 500, 1e-9), "curve length of a line and a centre arc");
        var semicircle = (WallCurve)((CurtainWallCreateArgs)RevitCommandProtocol.Parse(Envelope("curtain_wall.create", Curtain(new JsonObject { ["kind"] = "arc", ["start"] = Pair(0, 0), ["end"] = Pair(2000, 0), ["through"] = Pair(1000, 1000) })).ToJsonString()).Args).Curve;
        Check(Near(RevitCommandGeometryV2.CurveLengthMm(semicircle), Math.PI * 1000, 1e-6), "curve length of a three-point semicircle is pi r");
    }

    static void FloorOpenings()
    {
        var opening = Accepts("floor opening accepted", Envelope("floor.create_opening", Opening()));
        Check(opening?.Args is FloorOpeningCreateArgs { HostFloorUid: FloorUid, Profile.Count: 4 } o && Near(o.Profile[2].Y.Millimetres, 2500), "floor opening binds host floor UniqueId and four profile vertices");
        Check(Accepts("explicitly closed opening profile accepted", Envelope("floor.create_opening", With(Opening(), "profile", Outline((1000, 1000), (2000, 1000), (2000, 2500), (1000, 2500), (1000, 1000)))))?.Args is FloorOpeningCreateArgs { Profile.Count: 4 }, "repeated closing profile vertex is dropped");
        Accepts("triangular opening accepted", Envelope("floor.create_opening", With(Opening(), "profile", Outline((0, 0), (1000, 0), (500, 800)))));
        Rejects("floor opening host not a UniqueId", Envelope("floor.create_opening", With(Opening(), "host_floor_uid", "Floor 1")), RevitCommandCodes.ArgsInvalid, "args.host_floor_uid");
        Rejects("floor opening without host", Envelope("floor.create_opening", With(Opening(), "host_floor_uid", null)), RevitCommandCodes.ArgsInvalid, "args.host_floor_uid");
        Rejects("floor opening without profile", Envelope("floor.create_opening", With(Opening(), "profile", null)), RevitCommandCodes.ArgsInvalid, "args.profile");
        Rejects("floor opening profile with two vertices", Envelope("floor.create_opening", With(Opening(), "profile", Outline((0, 0), (1000, 0)))), RevitCommandCodes.ArgsInvalid, "args.profile");
        Rejects("self-intersecting opening profile", Envelope("floor.create_opening", With(Opening(), "profile", Outline((0, 0), (2000, 2000), (2000, 0), (0, 2000)))), RevitCommandCodes.BoundarySelfIntersecting);
        Rejects("opening profile with an edge under 1.2 mm", Envelope("floor.create_opening", With(Opening(), "profile", Outline((0, 0), (1000, 0), (1000.5, 0.5), (1000, 1000)))), RevitCommandCodes.BoundaryEdgeTooShort, "args.profile[1]");
        Rejects("opening profile with area under 1.44 mm2", Envelope("floor.create_opening", With(Opening(), "profile", Outline((0, 0), (1.4, 0), (0.7, 1.2124)))), RevitCommandCodes.BoundaryZeroArea, "args.profile");
        Rejects("floor opening with unexpected member", Envelope("floor.create_opening", With(Opening(), "cut_through", true)), RevitCommandCodes.ArgsInvalid, "args.cut_through");
    }

    static void ClientPath()
    {
        var policy = new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" });
        var forwarded = new List<string>();
        string? Transport(string pipe, string line) { forwarded.Add(line); return Answer(line); }
        var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
        foreach (var (command, args) in new (string, JsonObject)[] { ("roof.create_footprint", Roof()), ("stair.create", Stair()), ("curtain_wall.create", Curtain()), ("floor.create_opening", Opening()) })
        {
            var before = forwarded.Count;
            var ok = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args).ToJsonString(), policy, Transport, noLinks);
            Check(!ok.IsError && forwarded.Count == before + 1 && forwarded[^1] == RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(Envelope(command, args).ToJsonString())), command + " on an enabled copy is forwarded as the canonical wire line", ok.Line);
            var refused = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args, RealPath).ToJsonString(), policy, Transport, noLinks);
            Check(refused.IsError && !refused.Forwarded && JsonNode.Parse(refused.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentNotEnabled && forwarded.Count == before + 1, command + " on a real project is document_not_enabled and never forwarded", refused.Line);
            var linked = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args).ToJsonString(), policy, Transport, new RevitFileSystemProbe(path => path.EndsWith("run-1", StringComparison.Ordinal), _ => null));
            Check(linked.IsError && JsonNode.Parse(linked.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentLinkRefused && forwarded.Count == before + 1, command + " through a linked directory is document_link_refused");
        }
        var query = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("query.roof_types", new JsonObject(), RealPath).ToJsonString(), policy, Transport, noLinks);
        Check(!query.IsError && query.Forwarded, "query.roof_types is read-only and allowed on a real project", query.Line);
        var invalid = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("stair.create", With(Stair(), "top_level", "Level 1")).ToJsonString(), policy, Transport, noLinks);
        Check(invalid.IsError && !invalid.Forwarded && invalid.Line == RevitCommandResponse.Failure(RevitCommandCodes.LevelsNotDistinct, "args.top_level", "base_level and top_level must name different levels").ToJson(), "an invalid v2 command returns the typed failure before the pipe", invalid.Line);
        var a = RevitCommandProtocol.Parse("""{"protocol_version":1,"operation_id":"op-r","document":{"path":"C:\\Repo\\.bim\\native-runs\\r\\a.rvt","unique_id":"60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b"},"command":"roof.create_footprint","args":{"level":"L2","type":"R","slope_deg":30,"boundary":[[0,0],[8000,0],[8000,6000]]}}""");
        var b = RevitCommandProtocol.Parse("""{"args":{"boundary":[[0,0e0],[8.0e3,0],[8000,6000.0]],"slope_deg":3e1,"type":"R","level":"L2"},"command":"roof.create_footprint","document":{"unique_id":"60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b","path":"C:\\Repo\\.bim\\native-runs\\r\\a.rvt"},"operation_id":"op-r","protocol_version":1}""");
        Check(a.CanonicalJson == b.CanonicalJson && a.RequestSha256 == b.RequestSha256, "v2 requests use the same canonical form and hash as v1", a.CanonicalJson + " vs " + b.CanonicalJson);
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
        Check(CodeOf(() => CommandRulesV2.CheckCurtainWallType("Curtain")) is null, "a Curtain wall type is accepted by curtain_wall.create");
        foreach (var kind in new[] { "Basic", "Stacked" })
            Check(CodeOf(() => CommandRulesV2.CheckCurtainWallType(kind)) == RevitCommandCodes.ArgsInvalid, "a " + kind + " wall type is args_invalid for curtain_wall.create (it exists, it is the wrong kind)");
        Check(CommandRulesV2.Created(TypeUid, new[] { LevelUid, TopUid }, new[] { TopUid, TypeUid }).SequenceEqual(new[] { TypeUid, LevelUid, TopUid }), "created lists the primary element first, then each child once");
        RevitCommandException? workshared = null;
        try { CommandAdmission.CheckWrite(FixturePath, new CommandDocumentFacts(FixturePath, Uid, false, false, true, false), () => new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" }), new RevitFileSystemProbe(_ => false, _ => null)); }
        catch (RevitCommandException error) { workshared = error; }
        Check(workshared is { Code: RevitCommandCodes.DocumentNotEnabled } && workshared.Detail is { } detail && detail.Contains("workshared", StringComparison.Ordinal) && !detail.Contains("v1", StringComparison.Ordinal), "the workshared refusal applies to every catalogue command, not only v1", workshared?.Detail ?? "accepted");
        Check(CodeOf(() => CommandRulesV2.CheckStairLevels(LevelUid, 0, TopUid, 3000)) is null, "stair between two levels 3000 mm apart is allowed");
        Check(CodeOf(() => CommandRulesV2.CheckStairLevels(LevelUid, 0, LevelUid, 0)) == RevitCommandCodes.LevelsNotDistinct, "stair whose references resolve to one level is levels_not_distinct");
        Check(CodeOf(() => CommandRulesV2.CheckStairLevels(LevelUid, 3000, TopUid, 0)) == RevitCommandCodes.LevelsNotDistinct && CodeOf(() => CommandRulesV2.CheckStairLevels(LevelUid, 3000, TopUid, 3001)) == RevitCommandCodes.LevelsNotDistinct, "stair top level must be above the base level by more than 1.2 mm");
        Check(!CommandRulesV2.IsUGridLine(vertical: true) && CommandRulesV2.IsUGridLine(vertical: false), "on a curtain wall vertical lines are V grid lines and horizontal lines are U grid lines (unverified in Revit; the readback checks orientation)");
        var line = new LineWallCurve(P(0, 0), P(5000, 0));
        Check(CommandGeometry.Near(CommandRulesV2.PointAlong(line, 1000), new PlanPoint(1000, 0)), "point along a line wall");
        var quarter = new ArcCenterWallCurve(P(0, 0), Length.FromMillimetres(1000), Angle.FromDegrees(0), Angle.FromDegrees(90));
        var half = CommandRulesV2.PointAlong(quarter, Math.PI * 250);
        Check(Near(half.X, 1000 * Math.Sqrt(0.5), 1e-9) && Near(half.Y, 1000 * Math.Sqrt(0.5), 1e-9), "point along a centre arc runs counter-clockwise from the start angle");
        var semicircle = new ArcThroughWallCurve(P(0, 0), P(2000, 0), P(1000, 1000), P(1000, 0), Length.FromMillimetres(1000), Angle.FromDegrees(180), false);
        var top = CommandRulesV2.PointAlong(semicircle, Math.PI * 500);
        Check(Near(top.X, 1000, 1e-9) && Near(top.Y, 1000, 1e-9), "point along a clockwise three-point arc passes through its top");
        var lower = new ArcThroughWallCurve(P(0, 0), P(2000, 0), P(1000, -1000), P(1000, 0), Length.FromMillimetres(1000), Angle.FromDegrees(180), true);
        var bottom = CommandRulesV2.PointAlong(lower, Math.PI * 500);
        Check(Near(bottom.X, 1000, 1e-9) && Near(bottom.Y, -1000, 1e-9), "point along a counter-clockwise three-point arc passes below the chord");
        var args = (CurtainWallCreateArgs)RevitCommandProtocol.Parse(Envelope("curtain_wall.create", With(Curtain(grid: new JsonObject { ["vertical_spacing_mm"] = 2000, ["horizontal_spacing_mm"] = 1500 }), "base_offset_mm", 100)).ToJsonString()).Args;
        var grid = CommandRulesV2.CurtainGrid(args, 3000);
        Check(grid.Vertical.Count == 2 && CommandGeometry.Near(grid.Vertical[0], new PlanPoint(2000, 0)) && CommandGeometry.Near(grid.Vertical[1], new PlanPoint(4000, 0))
            && grid.HorizontalZMm.Count == 2 && Near(grid.HorizontalZMm[0], 4600) && Near(grid.HorizontalZMm[1], 6100) && Near(grid.VerticalZMm, 5100) && CommandGeometry.Near(grid.HorizontalAt, new PlanPoint(2500, 0)),
            "curtain grid plan: vertical lines along the wall at mid-height, horizontal lines up from level + offset at mid-length");
        var plain = CommandRulesV2.CurtainGrid((CurtainWallCreateArgs)RevitCommandProtocol.Parse(Envelope("curtain_wall.create", Curtain()).ToJsonString()).Args, 0);
        Check(plain.Vertical.Count == 0 && plain.HorizontalZMm.Count == 0, "no grid argument plans no grid lines");
    }

    static void Readbacks()
    {
        ObservedRoofEdge Edge(bool slopes, double? degrees) => new(slopes, degrees);
        var footprint = new[] { P(0, 0), P(8000, 0), P(8000, 6000), P(0, 6000) };
        var sketch = new[] { new PlanPoint(8000, 0), new PlanPoint(8000, 6000), new PlanPoint(0, 6000), new PlanPoint(0, 0) };
        var hip = new ObservedRoof("60f91daf-3dd7-4283-a86d-24137b73f3da-00000300", LevelUid, TypeUid, 0, new[] { Edge(true, 30), Edge(true, 30), Edge(true, 30), Edge(true, 30.0000000001) }, 30, 1, sketch);
        Check(CommandReadbackV2.Roof(LevelUid, TypeUid, footprint, 30, 0, hip) is null, "hip roof readback matches (sketch vertices may start elsewhere)");
        Check(CommandReadbackV2.Roof(TopUid, TypeUid, footprint, 30, 0, hip) is { } wrongLevel && wrongLevel.Contains("level", StringComparison.Ordinal), "roof on another level is a mismatch");
        Check(CommandReadbackV2.Roof(LevelUid, TopUid, footprint, 30, 0, hip) is { } wrongType && wrongType.Contains("type", StringComparison.Ordinal), "roof of another type is a mismatch");
        Check(CommandReadbackV2.Roof(LevelUid, TypeUid, footprint, 30, 0, hip with { Footprint = sketch.Take(3).ToArray() }) is { } wrongEdges && wrongEdges.Contains("edges", StringComparison.Ordinal), "roof sketch with a different edge count is a mismatch");
        Check(CommandReadbackV2.Roof(LevelUid, TypeUid, footprint, 30, 0, hip with { Footprint = new[] { new PlanPoint(9000, 0), new PlanPoint(9000, 6000), new PlanPoint(0, 6000), new PlanPoint(0, 0) } }) is { } wrongArea && wrongArea.Contains("area", StringComparison.Ordinal), "roof sketch of another area is a mismatch");
        Check(CommandReadbackV2.Roof(LevelUid, TypeUid, footprint, 30, 0, hip with { Footprint = new[] { new PlanPoint(8000, 0), new PlanPoint(6000, 6000), new PlanPoint(-2000, 6000), new PlanPoint(0, 0) } }) is { } wrongVertex && wrongVertex.Contains("vertex", StringComparison.Ordinal), "roof sketch of the same area but other vertices is a mismatch");
        Check(CommandReadbackV2.Roof(LevelUid, TypeUid, footprint, 30, 0, hip with { Profiles = 2 }) is not null, "a roof sketch with two loops is a mismatch");
        Check(CommandReadbackV2.Roof(LevelUid, TypeUid, footprint, 30, 0, hip with { Edges = hip.Edges.Take(3).ToArray() }) is not null, "a sloped-edge list shorter than the sketch is a mismatch");
        Check(CommandReadbackV2.Roof(LevelUid, TypeUid, footprint, 35, 0, hip with { SlopeParameterDeg = 35 }) is not null, "roof edge slope mismatch is reported");
        Check(CommandReadbackV2.Roof(LevelUid, TypeUid, footprint, 30, 0, hip with { SlopeParameterDeg = 35 }) is { } wrongParameter && wrongParameter.Contains("Slope parameter", StringComparison.Ordinal), "the roof Slope parameter is checked independently of the edges");
        Check(CommandReadbackV2.Roof(LevelUid, TypeUid, footprint, 30, 0, hip with { SlopeParameterDeg = null }) is not null, "an unreadable roof Slope parameter is a mismatch for a sloped roof");
        Check(CommandReadbackV2.Roof(LevelUid, TypeUid, footprint, 30, 0, hip with { Edges = new[] { Edge(true, 30), Edge(false, null), Edge(true, 30), Edge(true, 30) } }) is not null, "a hip roof edge that does not define slope is a mismatch");
        Check(CommandReadbackV2.Roof(LevelUid, TypeUid, footprint, null, 0, hip with { Edges = new[] { Edge(false, null), Edge(false, null), Edge(false, null), Edge(false, null) }, SlopeParameterDeg = null }) is null, "flat roof readback matches (no Slope parameter needed)");
        Check(CommandReadbackV2.Roof(LevelUid, TypeUid, footprint, null, 0, hip) is not null, "a flat roof whose edges define slope is a mismatch");
        Check(CommandReadbackV2.Roof(LevelUid, TypeUid, footprint, 30, -500, hip) is not null && CommandReadbackV2.Roof(LevelUid, TypeUid, footprint, 30, 0, hip with { BaseOffsetMm = null }) is not null, "roof base offset mismatch or unreadable offset is reported");

        PlanPoint runStart = new(0, 0), runEnd = new(4000, 0);
        var stairs = new ObservedStairs("60f91daf-3dd7-4283-a86d-24137b73f3da-00000400", LevelUid, TopUid, new double?[] { 1100 }, 17, 17, runStart, runEnd);
        Check(CommandReadbackV2.Stairs(LevelUid, TopUid, 1100, runStart, runEnd, stairs) is null, "stair readback matches");
        Check(CommandReadbackV2.Stairs(LevelUid, TopUid, 1100, runStart, runEnd, stairs with { TopLevelUid = LevelUid }) is not null && CommandReadbackV2.Stairs(LevelUid, TopUid, 1100, runStart, runEnd, stairs with { BaseLevelUid = null }) is not null, "stair level mismatch or unreadable level is reported");
        Check(CommandReadbackV2.Stairs(LevelUid, TopUid, 1100, runStart, runEnd, stairs with { RunWidthsMm = new double?[] { 1100, 1100 } }) is { } runs && runs.Contains("run", StringComparison.Ordinal), "a stair with two runs is a mismatch");
        Check(CommandReadbackV2.Stairs(LevelUid, TopUid, 1100, runStart, runEnd, stairs with { RunWidthsMm = new double?[] { 1000 } }) is not null && CommandReadbackV2.Stairs(LevelUid, TopUid, 1100, runStart, runEnd, stairs with { RunWidthsMm = new double?[] { null } }) is not null, "stair run width mismatch or unreadable width is reported");
        Check(CommandReadbackV2.Stairs(LevelUid, TopUid, 1100, runStart, runEnd, stairs with { Risers = 0, DesiredRisers = 0 }) is not null, "a stair without risers is a mismatch");
        Check(CommandReadbackV2.Stairs(LevelUid, TopUid, 1100, runStart, runEnd, stairs with { Risers = 12 }) is { } shortStair && shortStair.Contains("top level", StringComparison.Ordinal), "a stair with fewer actual than desired risers does not reach the top level");
        Check(CommandReadbackV2.Stairs(LevelUid, TopUid, 1100, runStart, runEnd, stairs with { Risers = null }) is not null && CommandReadbackV2.Stairs(LevelUid, TopUid, 1100, runStart, runEnd, stairs with { DesiredRisers = null }) is not null, "an unreadable actual or desired riser count is a mismatch");
        Check(CommandReadbackV2.Stairs(LevelUid, TopUid, 1100, runStart, runEnd, stairs with { RunStart = runEnd, RunEnd = runStart }) is not null, "a run going the other way (top step at the requested start) is a mismatch");
        Check(CommandReadbackV2.Stairs(LevelUid, TopUid, 1100, runStart, runEnd, stairs with { RunEnd = new PlanPoint(3000, 0) }) is not null && CommandReadbackV2.Stairs(LevelUid, TopUid, 1100, runStart, runEnd, stairs with { RunStart = null }) is not null, "a shortened or unreadable run path is a mismatch");

        var expected = new ExpectedCurtainGrid(new[] { new PlanPoint(2000, 0), new PlanPoint(4000, 0) }, 2000, new[] { 1500.0 }, new PlanPoint(2500, 0));
        ObservedGridLine Vertical(double x) => new("60f91daf-3dd7-4283-a86d-24137b73f3da-00000501", new PlanPoint(x, 0), 0, new PlanPoint(x, 0), 4000);
        var horizontal = new ObservedGridLine("60f91daf-3dd7-4283-a86d-24137b73f3da-00000502", new PlanPoint(0, 0), 1500, new PlanPoint(5000, 0), 1500);
        Check(CommandReadbackV2.CurtainGrid(expected, new[] { Vertical(2000), Vertical(4000) }, new[] { horizontal }) is null, "curtain grid readback matches");
        Check(CommandReadbackV2.CurtainGrid(expected, new[] { Vertical(2000) }, new[] { horizontal }) is { } count && count.Contains("vertical", StringComparison.Ordinal), "a missing vertical grid line is a mismatch");
        Check(CommandReadbackV2.CurtainGrid(expected, new[] { Vertical(2000), Vertical(4100) }, new[] { horizontal }) is not null, "a vertical grid line in the wrong place is a mismatch");
        Check(CommandReadbackV2.CurtainGrid(expected, new[] { Vertical(2000), horizontal }, new[] { horizontal }) is not null, "a horizontal line where a vertical one was asked is a mismatch (detects a swapped U/V mapping)");
        Check(CommandReadbackV2.CurtainGrid(expected, new[] { Vertical(2000), Vertical(4000) }, new[] { horizontal with { EndZMm = 1600 } }) is not null && CommandReadbackV2.CurtainGrid(expected, new[] { Vertical(2000), Vertical(4000) }, new[] { horizontal with { StartZMm = 1400, EndZMm = 1400 } }) is not null, "a sloping or misplaced horizontal grid line is a mismatch");
        Check(CommandReadbackV2.CurtainGrid(expected, new[] { Vertical(2000), Vertical(4000) }, new[] { Vertical(2500) }) is not null, "a vertical line where a horizontal one was asked is a mismatch");

        var profile = new[] { P(1000, 1000), P(2000, 1000), P(2000, 2500), P(1000, 2500) };
        var observedOpening = new ObservedFloorOpening("60f91daf-3dd7-4283-a86d-24137b73f3da-00000600", FloorUid, false, new[] { new PlanPoint(2000, 1000), new PlanPoint(2000, 2500), new PlanPoint(1000, 2500), new PlanPoint(1000, 1000) });
        Check(CommandReadbackV2.FloorOpening(FloorUid, profile, observedOpening) is null, "floor opening readback matches (vertex order may start elsewhere)");
        Check(CommandReadbackV2.FloorOpening(FloorUid, profile, observedOpening with { HostUid = null }) is not null && CommandReadbackV2.FloorOpening(FloorUid, profile, observedOpening with { HostUid = TypeUid }) is not null, "an opening without host or on another element is a mismatch");
        Check(CommandReadbackV2.FloorOpening(FloorUid, profile, observedOpening with { IsRectBoundary = true }) is not null, "a rectangular-boundary opening is a mismatch for a profile opening");
        Check(CommandReadbackV2.FloorOpening(FloorUid, profile, observedOpening with { Vertices = observedOpening.Vertices.Take(3).ToArray() }) is not null, "an opening with a different vertex count is a mismatch");
        Check(CommandReadbackV2.FloorOpening(FloorUid, profile, observedOpening with { Vertices = new[] { new PlanPoint(2000, 1000), new PlanPoint(2000, 2600), new PlanPoint(1000, 2600), new PlanPoint(1000, 1000) } }) is { } area && area.Contains("area", StringComparison.Ordinal), "an opening of another area is a mismatch");

        var types = JsonNode.Parse(CommandReadbackV2.RoofTypesJson(new[] { new RoofTypeRow("Z roof", TypeUid, 300), new RoofTypeRow("A roof", LevelUid, null) }))!;
        Check(types["roof_types"]!.AsArray().Select(row => row!["name"]!.GetValue<string>()).SequenceEqual(new[] { "A roof", "Z roof" }) && types["roof_types"]![0]!["width_mm"] is null && types["roof_types"]![1]!["width_mm"]!.GetValue<double>() == 300, "roof types readback is sorted by name with width_mm or null", types.ToJsonString());
        var roofJson = JsonNode.Parse(CommandReadbackV2.RoofJson(hip))!["roof"]!;
        Check(roofJson["slope_parameter_deg"]!.GetValue<double>() == 30 && roofJson["footprint"]!.AsArray().Count == 4 && Near(roofJson["area_mm2"]!.GetValue<double>(), 4.8e7), "roof readback JSON reports the Slope parameter, the sketch footprint and its area", roofJson.ToJsonString());
        Check(roofJson["unique_id"]!.GetValue<string>() == hip.UniqueId && roofJson["level_unique_id"]!.GetValue<string>() == LevelUid && roofJson["type_unique_id"]!.GetValue<string>() == TypeUid && roofJson["base_offset_mm"]!.GetValue<double>() == 0
            && roofJson["edges"]!.AsArray().Count == 4 && roofJson["edges"]![0]!["defines_slope"]!.GetValue<bool>() && roofJson["edges"]![0]!["slope_deg"]!.GetValue<double>() == 30, "roof readback JSON reports level, type, offset and each footprint edge", roofJson.ToJsonString());
        var stairsJson = JsonNode.Parse(CommandReadbackV2.StairsJson(stairs, new[] { TypeUid }))!["stairs"]!;
        Check(stairsJson["desired_risers"]!.GetValue<int>() == 17 && stairsJson["run"]!["end"]![0]!.GetValue<double>() == 4000 && stairsJson["railing_unique_ids"]!.AsArray().Single()!.GetValue<string>() == TypeUid, "stairs readback JSON reports desired risers, the run path and the railings created with it", stairsJson.ToJsonString());
        Check(stairsJson["base_level_unique_id"]!.GetValue<string>() == LevelUid && stairsJson["top_level_unique_id"]!.GetValue<string>() == TopUid && stairsJson["run_widths_mm"]!.AsArray().Single()!.GetValue<double>() == 1100 && stairsJson["risers"]!.GetValue<int>() == 17, "stairs readback JSON reports levels, run widths and risers", stairsJson.ToJsonString());
        var wall = new ObservedWall("60f91daf-3dd7-4283-a86d-24137b73f3da-00000700", LevelUid, TypeUid, false, new PlanPoint(0, 0), new PlanPoint(5000, 0), 5000, null, null, 4000, 0, null);
        var curtainJson = JsonNode.Parse(CommandReadbackV2.CurtainWallJson(wall, 5, 3, new[] { "60f91daf-3dd7-4283-a86d-24137b73f3da-00000501" }, 24, 38))!;
        Check(curtainJson["curtain_grid"]!["panels"]!.GetValue<int>() == 24 && curtainJson["curtain_grid"]!["mullions"]!.GetValue<int>() == 38, "curtain wall readback JSON counts the panels and mullions Revit made (their UniqueIds are in created)", curtainJson.ToJsonString());
        Check(curtainJson["wall"]!["unique_id"]!.GetValue<string>() == wall.UniqueId && curtainJson["wall"]!["curve"]!["kind"]!.GetValue<string>() == "line" && curtainJson["curtain_grid"]!["vertical_lines"]!.GetValue<int>() == 5 && curtainJson["curtain_grid"]!["horizontal_lines"]!.GetValue<int>() == 3
            && curtainJson["curtain_grid"]!["added_grid_line_unique_ids"]!.AsArray().Count == 1, "curtain wall readback JSON embeds the wall readback and the grid totals", curtainJson.ToJsonString());
        var openingJson = JsonNode.Parse(CommandReadbackV2.FloorOpeningJson(observedOpening))!["floor_opening"]!;
        Check(openingJson["host_floor_unique_id"]!.GetValue<string>() == FloorUid && openingJson["profile"]!.AsArray().Count == 4 && Near(openingJson["area_mm2"]!.GetValue<double>(), 1.5e6) && !openingJson["rect_boundary"]!.GetValue<bool>(), "floor opening readback JSON reports host, profile and area", openingJson.ToJsonString());
    }
}
