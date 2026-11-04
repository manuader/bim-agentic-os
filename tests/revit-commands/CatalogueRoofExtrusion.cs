using Bim.Core;
using Bim.Revit;
using System.Text.Json;
using System.Text.Json.Nodes;
// roof.create_extrusion contract oracle (P9-07): extrusion roofs (barrel / curved sheet-metal roofs) from an open
// profile of lines and arcs in a vertical reference plane, extruded between two extents. Covers the schema, the typed
// binder and its profile rules (connected in order, open, not self-intersecting, 1 to 100 segments, extents), the
// guarded client path (fake transport) and the Autodesk-free executor rules and readback in
// runtimes/revit-native/CommandsRoofExtrusion.cs. Expected values are computed by hand in the comments, not with the
// implementation. No Revit is involved: a passing run is unit-tested evidence only; CommandsExecutorRoofExtrusion.cs
// is compiled, never run.
static class CatalogueRoofExtrusionChecks
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string RoofUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0002e001";
    const string PlaneUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0002e002";
    const string LevelUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000311";
    const string TypeUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000a01";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static JsonObject Envelope(JsonNode args, string path = FixturePath) => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-roof-extrusion",
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
        ["command"] = "roof.create_extrusion",
        ["args"] = args.DeepClone(),
    };
    static JsonArray Pair(double a, double b) => new(a, b);
    static JsonObject L(double u0, double z0, double u1, double z1) => new() { ["kind"] = "line", ["start"] = Pair(u0, z0), ["end"] = Pair(u1, z1) };
    static JsonObject A3(double u0, double z0, double u1, double z1, double tu, double tz) => new() { ["kind"] = "arc", ["start"] = Pair(u0, z0), ["end"] = Pair(u1, z1), ["through"] = Pair(tu, tz) };
    static JsonObject AC(double cu, double cz, double radius, double a0, double a1) => new() { ["kind"] = "arc", ["center"] = Pair(cu, cz), ["radius_mm"] = radius, ["start_angle_deg"] = a0, ["end_angle_deg"] = a1 };
    static JsonArray Profile(params JsonObject[] segments) => new(segments.Select(s => (JsonNode?)s).ToArray());
    // Barrel roof over a 6000 x 10000 cabin: plane along +x at y 0, one arc from (-300, 2700) to (6300, 2700) through
    // the crown (3000, 3800), extruded -300 to 10300 along +y. By hand: the centre is at u 3000 (symmetry); with z_c,
    // 3300^2 + (2700 - z_c)^2 = (3800 - z_c)^2 gives z_c = -1700 and R = 5500 (3300^2 + 4400^2 = 5500^2); the half
    // angle has sine 3300 / 5500 = 0.6, so the sweep is 2 asin(0.6) = 73.7398 degrees; left to right over the top
    // turns clockwise.
    static JsonObject Barrel() => new()
    {
        ["level"] = "Level 1",
        ["type"] = "Chapa curva",
        ["plane"] = new JsonObject { ["start"] = Pair(0, 0), ["end"] = Pair(6000, 0) },
        ["profile"] = Profile(A3(-300, 2700, 6300, 2700, 3000, 3800)),
        ["extrusion_start_mm"] = -300,
        ["extrusion_end_mm"] = 10300,
    };
    static JsonObject With(JsonObject args, string member, JsonNode? value) { if (value is null) args.Remove(member); else args[member] = value; return args; }
    static JsonObject WithProfile(params JsonObject[] segments) => With(Barrel(), "profile", Profile(segments));
    static bool Near(double a, double b, double tolerance = 1e-6) => Math.Abs(a - b) <= tolerance;
    static bool Near(ProfileVertex v, double u, double z, double tolerance = 1e-6) => Near(v.U.Millimetres, u, tolerance) && Near(v.Z.Millimetres, z, tolerance);

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
    static RoofExtrusionCreateArgs? Accepts(string name, JsonNode envelope)
    {
        try
        {
            var parsed = RevitCommandProtocol.Parse(envelope.ToJsonString());
            Check(parsed.Args is RoofExtrusionCreateArgs, name, parsed.Args.GetType().Name);
            return parsed.Args as RoofExtrusionCreateArgs;
        }
        catch (RevitCommandException error) { Check(false, name, error.Code + " at " + error.Field + " (" + error.Detail + ")"); return null; }
        catch (Exception error) { Check(false, name, error.GetType().Name + ": " + error.Message); return null; }
    }

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        Catalogue();
        Binding();
        Connectivity();
        SelfIntersection();
        Limits();
        ClientPath();
        Frame();
        Readback();
        ReadbackJson();
    }

    static void Catalogue()
    {
        Check(RevitCommandCatalogue.RoofExtrusion.Select(spec => spec.Name).SequenceEqual(new[] { "roof.create_extrusion" }), "roof extrusion catalogue lists roof.create_extrusion");
        var spec = RevitCommandCatalogue.RoofExtrusion.FirstOrDefault();
        Check(spec is not null && RevitCommandCatalogue.All.Contains(spec) && ReferenceEquals(RevitCommandCatalogue.Find("roof.create_extrusion"), spec), "roof.create_extrusion is in the catalogue enumeration and Find resolves it");
        if (spec is null) return;
        Check(RevitCommandCatalogue.All.Select(s => s.Name).Distinct().Count() == RevitCommandCatalogue.All.Count && RevitCommandCatalogue.All.Select(s => s.ToolName).Distinct().Count() == RevitCommandCatalogue.All.Count, "command and tool names stay distinct with roof.create_extrusion");
        Check(spec.ToolName == "revit_roof_create_extrusion" && spec.Writes && !RevitCommandCatalogue.IsDestructive(spec.Name), "roof.create_extrusion is the additive write tool revit_roof_create_extrusion");
        Check(RevitCommandCatalogue.BatchStepCommands.Contains(spec), "roof.create_extrusion can be a command.batch step");
        Check(spec.Description.EndsWith('.') && spec.Description.Contains("enabled test copies", StringComparison.Ordinal) && spec.Description.Contains("barrel", StringComparison.Ordinal)
            && spec.Description.Contains("open", StringComparison.Ordinal) && spec.Description.Contains("left", StringComparison.Ordinal),
            "the description states the copies-only rule, a barrel roof example, the open profile and the extrusion side", spec.Description);
        string? problem = null;
        try { using var schema = JsonDocument.Parse(spec.ArgsSchemaJson); RevitCommandSchema.CheckSupported(schema.RootElement); if (schema.RootElement.GetProperty("type").GetString() != "object") problem = "not an object schema"; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) { problem = error.Message; }
        Check(problem is null, "roof.create_extrusion args schema is valid JSON using only enforced keywords", problem ?? "");
        var copies = new[] { @"C:\Repo\.bim\native-runs" };
        Check(PermissionPolicy.RequiredLevel("roof.create_extrusion", FixturePath, copies) == PermissionLevel.WriteCopies && PermissionPolicy.RequiredLevel("roof.create_extrusion", RealPath, copies) == PermissionLevel.WriteRealProject,
            "roof.create_extrusion is write-copies on a copy and write-real-project elsewhere");
        Check(RevitRoofExtrusionRules.MinSegments == 1 && RevitRoofExtrusionRules.MaxSegments == 100 && RevitRoofExtrusionRules.ConnectToleranceMm == 0.5, "1 to 100 segments, connected within 0.5 mm");
    }

    static void Binding()
    {
        var barrel = Accepts("barrel roof accepted", Envelope(Barrel()));
        Check(barrel is { Level: "Level 1", Type: "Chapa curva", Profile.Count: 1 } b && b.PlaneStart.X.Millimetres == 0 && b.PlaneEnd.X.Millimetres == 6000 && b.PlaneEnd.Y.Millimetres == 0
            && Near(b.ExtrusionStart.Millimetres, -300) && Near(b.ExtrusionEnd.Millimetres, 10300),
            "level, type, plane line and extents bind in millimetres");
        Check(barrel?.Profile[0] is RoofProfileArc arc && Near(arc.Start, -300, 2700) && Near(arc.End, 6300, 2700) && Near(arc.Center, 3000, -1700, 1e-6) && Near(arc.Radius.Millimetres, 5500, 1e-6)
            && Near(arc.Mid, 3000, 3800, 1e-6) && Near(arc.Sweep.Degrees, 73.73979529168804, 1e-9) && !arc.CounterClockwise,
            "the three-point barrel arc has centre (3000, -1700), radius 5500, sweep 73.7398 degrees, its midpoint at the crown, clockwise",
            barrel?.Profile[0].ToString() ?? "");
        // Same circle by centre: 53.1301 degrees = atan2(4400, 3300) at (6300, 2700) to 126.8699 at (-300, 2700),
        // counter-clockwise over the top; its midpoint at 90 degrees is (3000, 3800).
        var a0 = Math.Atan2(4400, 3300) * 180 / Math.PI;
        var centred = Accepts("centre arc accepted", Envelope(WithProfile(AC(3000, -1700, 5500, a0, 180 - a0))));
        Check(centred?.Profile[0] is RoofProfileArc c && Near(c.Start, 6300, 2700, 1e-6) && Near(c.End, -300, 2700, 1e-6) && Near(c.Mid, 3000, 3800, 1e-6) && c.CounterClockwise && Near(c.Sweep.Degrees, 73.73979529168804, 1e-9),
            "a centre arc starts at its start angle, runs counter-clockwise and its midpoint is half its sweep", centred?.Profile[0].ToString() ?? "");
        var gable = Accepts("two-line gable profile accepted", Envelope(WithProfile(L(-300, 2600, 3000, 4000), L(3000, 4000, 6300, 2600))));
        Check(gable?.Profile is [RoofProfileLine first, RoofProfileLine second] && Near(first.End, 3000, 4000) && Near(second.Start, 3000, 4000) && Near(second.End, 6300, 2600), "a two-line gable binds two lines in order");
        // Straight eaves tangent to the barrel: the line (0, 2700) -> (-300, 2700) ... drawn from the left eave in.
        var eaves = Accepts("line, arc, line profile accepted", Envelope(WithProfile(L(-600, 2600, -300, 2700), A3(-300, 2700, 6300, 2700, 3000, 3800), L(6300, 2700, 6600, 2600))));
        Check(eaves?.Profile.Count == 3 && eaves.Profile[1] is RoofProfileArc, "mixed lines and arcs bind in order");
    }

    static void Connectivity()
    {
        // A start 0.3 mm (0.18, 0.24) from the previous end is accepted and snapped exactly onto it.
        var snapped = Accepts("start within 0.5 mm accepted", Envelope(WithProfile(L(-300, 2600, 3000, 4000), L(3000.18, 4000.24, 6300, 2600))));
        Check(snapped?.Profile[1].Start == snapped?.Profile[0].End && snapped is not null && Near(snapped.Profile[1].Start, 3000, 4000), "a start within 0.5 mm is snapped onto the previous end");
        var arcSnapped = Accepts("arc start within 0.5 mm accepted", Envelope(WithProfile(L(-600, 2600, -300.4, 2700), A3(-300, 2700, 6300, 2700, 3000, 3800))));
        Check(arcSnapped?.Profile[1] is RoofProfileArc snappedArc && Near(snappedArc.Start, -300.4, 2700) && Near(snappedArc.Mid, 3000, 3800, 1e-6), "an arc start is snapped onto the previous end and keeps its midpoint");
        // 0.6 mm gap (0.36, 0.48).
        Rejects("gap of 0.6 mm", Envelope(WithProfile(L(-300, 2600, 3000, 4000), L(3000.36, 4000.48, 6300, 2600))), RevitCommandCodes.ArgsInvalid, "args.profile[1]");
        Rejects("second segment drawn backwards", Envelope(WithProfile(L(-300, 2600, 3000, 4000), L(6300, 2600, 3000, 4000))), RevitCommandCodes.ArgsInvalid, "args.profile[1]");
        Rejects("segments out of order", Envelope(WithProfile(L(3000, 4000, 6300, 2600), L(-300, 2600, 3000, 4000))), RevitCommandCodes.ArgsInvalid, "args.profile[1]");
        // A centre arc whose computed start (6300, 2700) is not where the line ends (6000, 2700).
        Rejects("centre arc not connected", Envelope(WithProfile(L(7000, 2000, 6000, 2700), AC(3000, -1700, 5500, Math.Atan2(4400, 3300) * 180 / Math.PI, 180 - Math.Atan2(4400, 3300) * 180 / Math.PI))), RevitCommandCodes.ArgsInvalid, "args.profile[1]");
        Rejects("closed triangle", Envelope(WithProfile(L(0, 0, 6000, 0), L(6000, 0, 3000, 2000), L(3000, 2000, 0, 0))), RevitCommandCodes.ArgsInvalid, "args.profile");
        Rejects("closed within 1 mm", Envelope(WithProfile(L(0, 0, 6000, 0), L(6000, 0, 3000, 2000), L(3000, 2000, 0.6, 0.8))), RevitCommandCodes.ArgsInvalid, "args.profile");
        Check(Accepts("open by 1.5 mm accepted", Envelope(WithProfile(L(0, 0, 6000, 0), L(6000, 0, 3000, 2000), L(3000, 2000, 0, 1.5))))?.Profile.Count == 3, "an end 1.5 mm from the start (and from the first segment) is open");
    }

    static void SelfIntersection()
    {
        // Line 2 from (4000, 2000) to (2000, -1000) crosses line 0 (z = 0) at t = 2/3, u = 2666.7.
        Rejects("line crosses a non-adjacent line", Envelope(WithProfile(L(0, 0, 4000, 0), L(4000, 0, 4000, 2000), L(4000, 2000, 2000, -1000))), RevitCommandCodes.ArgsInvalid, "args.profile[2]");
        // Semicircle centre (3000, 0) R 3000 over the top; line (6000, 4000) -> (2000, 1000) meets the circle where
        // 25 t^2 - 48 t + 16 = 0, t = 0.4293, at (4282.7, 2712.1) on the upper half.
        Rejects("line crosses a non-adjacent arc", Envelope(WithProfile(A3(0, 0, 6000, 0, 3000, 3000), L(6000, 0, 6000, 4000), L(6000, 4000, 2000, 1000))), RevitCommandCodes.ArgsInvalid, "args.profile[2]");
        // A horizontal line 0.5 mm above the semicircle's crown (3000, 3000): no crossing, but closer than 1.2 mm.
        Rejects("line 0.5 mm above a non-adjacent arc", Envelope(WithProfile(A3(0, 0, 6000, 0, 3000, 3000), L(6000, 0, 6000, 3000.5), L(6000, 3000.5, -1000, 3000.5))), RevitCommandCodes.ArgsInvalid, "args.profile[2]");
        // The same line 2 mm above the crown; the vertical line x = 6000 is tangent to the circle only at the shared end.
        Check(Accepts("line 2 mm above a non-adjacent arc accepted", Envelope(WithProfile(A3(0, 0, 6000, 0, 3000, 3000), L(6000, 0, 6000, 3002), L(6000, 3002, -1000, 3002))))?.Profile.Count == 3,
            "a line 2 mm from a non-adjacent arc and a tangent neighbour are accepted");
        // Circles centre (2000, 0) R 2000 and centre (3000, 0) R 2000 meet at u 2500, z +-1936.5; both arcs are upper halves.
        Rejects("arc crosses a non-adjacent arc", Envelope(WithProfile(A3(0, 0, 4000, 0, 2000, 2000), L(4000, 0, 5000, 0), A3(5000, 0, 1000, 0, 3000, 2000))), RevitCommandCodes.ArgsInvalid, "args.profile[2]");
        Rejects("line turns back along its neighbour", Envelope(WithProfile(L(0, 0, 4000, 0), L(4000, 0, 2000, 0))), RevitCommandCodes.ArgsInvalid, "args.profile[1]");
        Rejects("line runs back beyond its neighbour's start", Envelope(WithProfile(L(0, 0, 4000, 0), L(4000, 0, -1000, 0))), RevitCommandCodes.ArgsInvalid, "args.profile[1]");
        // Line from (4000, 0) toward (1000, 2500) re-enters the semicircle centre (2000, 0) R 2000: t (15.25 t - 12) = 0,
        // t = 0.7869 at (1639.3, 1967.2) on the upper half, far from the shared end.
        Rejects("line crosses its neighbouring arc again", Envelope(WithProfile(A3(0, 0, 4000, 0, 2000, 2000), L(4000, 0, 1000, 2500))), RevitCommandCodes.ArgsInvalid, "args.profile[1]");
        // An arc that doubles back on the same circle: (4000, 0) back to (2000, 2000) through (3414.2, 1414.2) lies on
        // the first arc's circle (|(1414.2, 1414.2)| = 2000).
        Rejects("arc doubles back on its neighbour's circle", Envelope(WithProfile(A3(0, 0, 4000, 0, 2000, 2000), A3(4000, 0, 2000, 2000, 2000 + 2000 * Math.Cos(Math.PI / 4), 2000 * Math.Sin(Math.PI / 4)))), RevitCommandCodes.ArgsInvalid, "args.profile[1]");
        // A zig-zag of 100 lines, u increasing by 100 each: non-neighbours are at least 100 mm apart.
        var zigzag = Enumerable.Range(0, 100).Select(i => L(i * 100, (i % 2) * 100, (i + 1) * 100, ((i + 1) % 2) * 100)).ToArray();
        Check(Accepts("100 segments accepted", Envelope(WithProfile(zigzag)))?.Profile.Count == 100, "the 100-segment cap is accepted");
        Rejects("101 segments", Envelope(WithProfile(zigzag.Append(L(10000, 0, 10100, 100)).ToArray())), RevitCommandCodes.ArgsInvalid, "args.profile");
        Rejects("no segment", Envelope(WithProfile()), RevitCommandCodes.ArgsInvalid, "args.profile");
    }

    static void Limits()
    {
        Rejects("line shorter than 1.2 mm", Envelope(WithProfile(L(0, 0, 0.5, 0))), RevitCommandCodes.CurveTooShort, "args.profile[0]");
        Rejects("collinear arc", Envelope(WithProfile(A3(0, 0, 6000, 0, 3000, 0))), RevitCommandCodes.ArcCollinear, "args.profile[0]");
        Rejects("centre arc with zero radius", Envelope(WithProfile(AC(3000, 0, 0, 0, 180))), RevitCommandCodes.ArcRadiusInvalid, "args.profile[0].radius_mm");
        Rejects("centre arc sweeping 360", Envelope(WithProfile(AC(3000, 0, 3000, 0, 360))), RevitCommandCodes.ArcSweepInvalid, "args.profile[0]");
        Rejects("z beyond Revit's limit", Envelope(WithProfile(L(0, 0, 6000, 9_144_001))), RevitCommandCodes.LengthInvalid, "args.profile[0].end[1]");
        Rejects("u beyond Revit's limit", Envelope(WithProfile(L(-9_144_001, 0, 6000, 0))), RevitCommandCodes.LengthInvalid, "args.profile[0].start[0]");
        Rejects("plane of zero length", Envelope(With(Barrel(), "plane", new JsonObject { ["start"] = Pair(100, 100), ["end"] = Pair(100.5, 100) })), RevitCommandCodes.CurveTooShort, "args.plane");
        Rejects("no plane", Envelope(With(Barrel(), "plane", null)), RevitCommandCodes.ArgsInvalid, "args.plane");
        Rejects("plane with extra member", Envelope(With(Barrel(), "plane", new JsonObject { ["start"] = Pair(0, 0), ["end"] = Pair(6000, 0), ["z"] = 0 })), RevitCommandCodes.ArgsInvalid, "args.plane.z");
        Rejects("equal extents", Envelope(With(Barrel(), "extrusion_end_mm", -300)), RevitCommandCodes.LengthInvalid, "args.extrusion_end_mm");
        Rejects("reversed extents", Envelope(With(Barrel(), "extrusion_end_mm", -1000)), RevitCommandCodes.LengthInvalid, "args.extrusion_end_mm");
        Rejects("extrusion 1 mm long", Envelope(With(Barrel(), "extrusion_end_mm", -299)), RevitCommandCodes.LengthInvalid, "args.extrusion_end_mm");
        Check(Accepts("extrusion 1.5 mm long accepted", Envelope(With(Barrel(), "extrusion_end_mm", -298.5))) is not null, "an extrusion 1.5 mm long is accepted");
        Rejects("extent beyond Revit's limit", Envelope(With(Barrel(), "extrusion_end_mm", 9_144_001)), RevitCommandCodes.LengthInvalid, "args.extrusion_end_mm");
        Rejects("start beyond Revit's limit", Envelope(With(Barrel(), "extrusion_start_mm", -9_144_001)), RevitCommandCodes.LengthInvalid, "args.extrusion_start_mm");
        Rejects("extent as text", Envelope(With(Barrel(), "extrusion_start_mm", "0")), RevitCommandCodes.ArgsInvalid, "args.extrusion_start_mm");
        Rejects("no extent", Envelope(With(Barrel(), "extrusion_end_mm", null)), RevitCommandCodes.ArgsInvalid, "args.extrusion_end_mm");
        Rejects("no level", Envelope(With(Barrel(), "level", null)), RevitCommandCodes.ArgsInvalid, "args.level");
        Rejects("no type", Envelope(With(Barrel(), "type", null)), RevitCommandCodes.ArgsInvalid, "args.type");
        Rejects("unexpected argument", Envelope(With(Barrel(), "slope_deg", 30)), RevitCommandCodes.ArgsInvalid, "args.slope_deg");
        Rejects("unknown segment kind", Envelope(WithProfile(new JsonObject { ["kind"] = "spline", ["start"] = Pair(0, 0), ["end"] = Pair(6000, 0) })), RevitCommandCodes.ArgsInvalid);
    }

    static void ClientPath()
    {
        var policy = new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" });
        var forwarded = new List<string>();
        string? Transport(string pipe, string line) { forwarded.Add(line); return null; }
        var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
        var ok = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(Barrel()).ToJsonString(), policy, Transport, noLinks);
        Check(forwarded.Count == 1 && ok.Forwarded, "roof.create_extrusion on an enabled copy is forwarded", ok.Line);
        var refused = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(Barrel(), RealPath).ToJsonString(), policy, Transport, noLinks);
        Check(refused.IsError && !refused.Forwarded && JsonNode.Parse(refused.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentNotEnabled && forwarded.Count == 1, "roof.create_extrusion on a real project is document_not_enabled and never forwarded", refused.Line);
        var crossing = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(WithProfile(L(0, 0, 4000, 0), L(4000, 0, 4000, 2000), L(4000, 2000, 2000, -1000))).ToJsonString(), policy, Transport, noLinks);
        Check(crossing.IsError && !crossing.Forwarded && JsonNode.Parse(crossing.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.ArgsInvalid && forwarded.Count == 1, "a self-intersecting profile is refused before the pipe", crossing.Line);
        try
        {
            var a = RevitCommandProtocol.Parse(Envelope(Barrel()).ToJsonString());
            var reordered = new JsonObject { ["extrusion_end_mm"] = 10300, ["extrusion_start_mm"] = -300, ["profile"] = Profile(A3(-300, 2700, 6300, 2700, 3000, 3800)), ["plane"] = new JsonObject { ["end"] = Pair(6000, 0), ["start"] = Pair(0, 0) }, ["type"] = "Chapa curva", ["level"] = "Level 1" };
            var b = RevitCommandProtocol.Parse(Envelope(reordered).ToJsonString());
            Check(a.CanonicalJson == b.CanonicalJson && a.RequestSha256 == b.RequestSha256, "roof.create_extrusion requests use the v1 canonical form and hash (member order ignored, segment order kept)");
        }
        catch (RevitCommandException error) { Check(false, "roof.create_extrusion canonical form", error.Code + " " + error.Detail); }
    }

    static RoofExtrusionCreateArgs Parsed(JsonObject args) => (RoofExtrusionCreateArgs)RevitCommandProtocol.Parse(Envelope(args).ToJsonString()).Args;

    static void Frame()
    {
        // Plane (1000, 2000) -> (4000, 6000): direction (0.6, 0.8), left normal (-0.8, 0.6).
        var diagonal = Parsed(With(Barrel(), "plane", new JsonObject { ["start"] = Pair(1000, 2000), ["end"] = Pair(4000, 6000) }));
        var frame = CommandRulesRoofExtrusion.Frame(diagonal);
        Check(Near(frame.OriginX, 1000) && Near(frame.OriginY, 2000) && Near(frame.Ux, 0.6, 1e-12) && Near(frame.Uy, 0.8, 1e-12) && Near(frame.Nx, -0.8, 1e-12) && Near(frame.Ny, 0.6, 1e-12),
            "the frame is the plane start, its unit direction and the left normal");
        var plan = CommandRulesRoofExtrusion.PlanOf(frame, 5000);
        Check(Near(plan.X, 4000, 1e-9) && Near(plan.Y, 6000, 1e-9), "u 5000 along (0.6, 0.8) from (1000, 2000) is (4000, 6000)");
        // (4000 - 800, 6000 + 600) is 1000 mm along the left normal from u 5000; z 3500 above a level at 3000 is 500.
        var (uz, w) = CommandRulesRoofExtrusion.UzwOf(frame, 3200, 6600, 3500, 3000);
        Check(Near(uz.U, 5000, 1e-9) && Near(uz.Z, 500, 1e-9) && Near(w, 1000, 1e-9), "a model point maps to u along the plane, z above the level and w along the left normal");
        var north = CommandRulesRoofExtrusion.Frame(Parsed(With(Barrel(), "plane", new JsonObject { ["start"] = Pair(0, 0), ["end"] = Pair(0, 5000) })));
        Check(Near(north.Nx, -1, 1e-12) && Near(north.Ny, 0, 1e-12), "a plane running north extrudes toward -x (its left)");
        Check(CommandRulesRoofExtrusion.RevitExtents(-300, 10300, true) == (-300, 10300) && CommandRulesRoofExtrusion.RevitExtents(-300, 10300, false) == (-10300, 300),
            "extents are passed as is when the reference plane normal agrees with the left normal and mirrored otherwise");
        Check(CommandRulesRoofExtrusion.ContractExtents(-10300, 300, false) == (-300, 10300) && CommandRulesRoofExtrusion.ContractExtents(0, 40, true) == (0, 40), "Revit's extents map back to the request's");
        // Box x 0..6000, y -300..10300 seen from plane (0,0)->(6000,0) (normal +y): w from -300 to 10300.
        var barrelFrame = CommandRulesRoofExtrusion.Frame(Parsed(Barrel()));
        Check(CommandRulesRoofExtrusion.NormalRange(barrelFrame, new double[] { -300, -300, 2600 }, new double[] { 6300, 10300, 3900 }) == (-300, 10300), "the box spans w -300 to 10300 along +y");
        var expected = CommandRulesRoofExtrusion.Expected(Parsed(WithProfile(L(-600, 2600, -300, 2700), A3(-300, 2700, 6300, 2700, 3000, 3800))));
        Check(expected.Length == 2 && !expected[0].Arc && Near(expected[0].Mid.U, -450) && Near(expected[0].Mid.Z, 2650) && expected[1].Arc && Near(expected[1].Mid.U, 3000, 1e-6) && Near(expected[1].Mid.Z, 3800, 1e-6)
            && Near(expected[1].A.U, -300) && Near(expected[1].B.U, 6300),
            "expected edges are the segments' ends with the line midpoint and the arc midpoint");
    }

    static ObservedRoofExtrusion Observed(params ObservedProfileEdge[] edges) =>
        new(RoofUid, LevelUid, TypeUid, PlaneUid, -300, 10300, edges, true, new double[] { -300, -300, 2600 }, new double[] { 6300, 10300, 3900 });
    static ProfilePointMm Pt(double u, double z) => new(u, z);
    static ObservedProfileEdge BarrelEdge(double offset = 0) => new(Pt(-300, 2700), Pt(6300, 2700), Pt(3000, 3800), true, offset);

    static void Readback()
    {
        var args = Parsed(Barrel());
        string? Check1(ObservedRoofExtrusion observed) => CommandReadbackRoofExtrusion.Roof(LevelUid, TypeUid, args, observed);
        Check(Check1(Observed(BarrelEdge())) is null, "the barrel roof read back as requested matches");
        Check(Check1(Observed(new ObservedProfileEdge(Pt(6300, 2700), Pt(-300, 2700), Pt(3000, 3800), true, 0))) is null, "a profile curve read back in the other direction matches");
        Check(Check1(Observed(new ObservedProfileEdge(Pt(-299.4, 2700.5), Pt(6300.6, 2699.5), Pt(3000.7, 3799.4), true, 0.8))) is null, "ends, midpoint and plane offset within 1 mm match");
        Check(Check1(Observed(new ObservedProfileEdge(Pt(-300, 2700), Pt(6300, 2700), Pt(3000, 3700), true, 0))) is { } low && low.Contains("profile", StringComparison.Ordinal), "a crown 100 mm low is a mismatch");
        Check(Check1(Observed(new ObservedProfileEdge(Pt(-300, 2700), Pt(6300, 2700), Pt(3000, 2700), false, 0))) is not null, "a line where an arc was requested is a mismatch");
        Check(Check1(Observed(new ObservedProfileEdge(Pt(-300, 2700), Pt(3000, 3800), Pt(1257, 3433), true, 0), new ObservedProfileEdge(Pt(3000, 3800), Pt(6300, 2700), Pt(4743, 3433), true, 0))) is not null, "an arc split in two is a mismatch");
        Check(Check1(Observed(BarrelEdge(5))) is { } off && off.Contains("plane", StringComparison.Ordinal), "a profile curve 5 mm off the plane is a mismatch");
        Check(Check1(Observed(BarrelEdge()) with { PlaneParallel = false }) is { } tilted && tilted.Contains("plane", StringComparison.Ordinal), "a sketch plane not parallel to the requested plane is a mismatch");
        Check(Check1(Observed(BarrelEdge()) with { EndMm = 10000 }) is { } extent && extent.Contains("extrusion", StringComparison.Ordinal), "an extrusion end 300 mm short is a mismatch");
        Check(Check1(Observed(BarrelEdge()) with { StartMm = -300.8, EndMm = 10300.9 }) is null, "extents within 1 mm match");
        Check(Check1(Observed(BarrelEdge()) with { StartMm = null }) is not null, "an unreadable extent is a mismatch");
        Check(Check1(Observed(BarrelEdge()) with { LevelUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000312" }) is { } level && level.Contains("level", StringComparison.Ordinal), "another reference level is a mismatch");
        Check(Check1(Observed(BarrelEdge()) with { TypeUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000a02" }) is { } type && type.Contains("type", StringComparison.Ordinal), "another roof type is a mismatch");
        Check(Check1(Observed(BarrelEdge()) with { PlaneUid = null }) is { } plane && plane.Contains("reference plane", StringComparison.Ordinal), "a missing reference plane is a mismatch");
        Check(Check1(Observed(BarrelEdge()) with { BoxMin = null }) is not null, "no bounding box is a mismatch");
        // Extruded the wrong way: the box spans y -10300 to 300.
        Check(Check1(Observed(BarrelEdge()) with { BoxMin = new double[] { -300, -10300, 2600 }, BoxMax = new double[] { 6300, 300, 3900 } }) is { } wrong && wrong.Contains("wrong way", StringComparison.Ordinal),
            "a roof extruded to the other side of the plane is a mismatch");
        Check(Check1(Observed(BarrelEdge()) with { BoxMin = new double[] { -320, -320, 2580 }, BoxMax = new double[] { 6320, 10320, 3920 } }) is null, "a bounding box padded by 20 mm matches");
        Check(Check1(Observed()) is not null, "a roof without profile curves is a mismatch");
    }

    static void ReadbackJson()
    {
        var args = Parsed(WithProfile(L(-600, 2600, -300, 2700), A3(-300, 2700, 6300, 2700, 3000, 3800)));
        var json = JsonNode.Parse(CommandReadbackRoofExtrusion.RoofJson(Observed(new ObservedProfileEdge(Pt(-600, 2600), Pt(-300, 2700), Pt(-450, 2650), false, 0), BarrelEdge()), args))!["roof_extrusion"]!;
        var profile = json["profile_mm"]!.AsArray();
        Check(json["unique_id"]!.GetValue<string>() == RoofUid && json["level_unique_id"]!.GetValue<string>() == LevelUid && json["type_unique_id"]!.GetValue<string>() == TypeUid
            && json["reference_plane_unique_id"]!.GetValue<string>() == PlaneUid && json["extrusion_start_mm"]!.GetValue<double>() == -300 && json["extrusion_end_mm"]!.GetValue<double>() == 10300
            && json["plane_end_mm"]![0]!.GetValue<double>() == 6000 && profile.Count == 2 && profile[0]!["kind"]!.GetValue<string>() == "line" && profile[0]!["mid"] is null
            && profile[1]!["kind"]!.GetValue<string>() == "arc" && profile[1]!["mid"]![1]!.GetValue<double>() == 3800 && json["bounding_box_mm"]!["max"]![1]!.GetValue<double>() == 10300,
            "roof extrusion readback reports the roof, level, type and reference plane UniqueIds, plane line, extents, the read-back profile and the bounding box", json.ToJsonString());
        var bare = JsonNode.Parse(CommandReadbackRoofExtrusion.RoofJson(Observed() with { PlaneUid = null, StartMm = null, BoxMin = null }, args))!["roof_extrusion"]!;
        Check(bare["reference_plane_unique_id"] is null && bare["extrusion_start_mm"] is null && bare["bounding_box_mm"] is null && bare["profile_mm"]!.AsArray().Count == 0, "missing values read back as null", bare.ToJsonString());
    }
}
