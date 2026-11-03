using Bim.Core;
using Bim.Revit;
using System.Text.Json;
using System.Text.Json.Nodes;
// Site commands contract oracle: query.toposolid_types, toposolid.create and toposolid.create_subdivision. Covers
// the schemas, the typed validators (point caps, plan-distinct and non-collinear points, subdivision outline), the
// guarded client path (fake transport) and the Autodesk-free executor rules and readback checks in
// runtimes/revit-native/CommandsSite.cs. No Revit is involved: a passing run is unit-tested evidence only;
// CommandsExecutorSite.cs is compiled, never run.
static class CatalogueSiteChecks
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string HostUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0002b001";
    const string LevelUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000137";
    const string TypeUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000c01";
    const string OtherUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000c02";
    const string MaterialUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000f01";
    const string ElementUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000d01";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";
    static readonly string[] SiteCommands = { "query.toposolid_types", "toposolid.create", "toposolid.create_subdivision" };
    static readonly string[] Writers = { "toposolid.create", "toposolid.create_subdivision" };

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static JsonObject Envelope(string command, JsonNode args, string path = FixturePath) => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-site",
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
        ["command"] = command,
        ["args"] = args.DeepClone(),
    };
    static JsonArray Pair(double x, double y) => new(x, y);
    static JsonArray Triple(double x, double y, double z) => new(x, y, z);
    static JsonArray Points(params double[][] points) => new(points.Select(p => (JsonNode?)(p.Length == 3 ? Triple(p[0], p[1], p[2]) : new JsonArray(p.Select(v => (JsonNode?)v).ToArray()))).ToArray());
    static JsonArray Outline(params double[][] points) => new(points.Select(p => (JsonNode?)Pair(p[0], p[1])).ToArray());
    static double[] P(params double[] values) => values;
    // A slope: 30 m by 20 m rising 4 m to the north, with a hump.
    static JsonObject Slope() => new()
    {
        ["level"] = "Level 1",
        ["type"] = "Toposolid 2000mm",
        ["points"] = Points(P(0, 0, 0), P(30000, 0, 500), P(30000, 20000, 4500), P(0, 20000, 4000), P(15000, 10000, 2600)),
        ["comment"] = "site: cabin slope",
    };
    static JsonObject Boulder() => new()
    {
        ["level"] = "Level 1",
        ["type"] = "Boulder",
        ["points"] = Points(P(5000, 5000, 900), P(6200, 5100, 950), P(6400, 6000, 1100), P(5600, 6700, 1250), P(4900, 6100, 1050), P(5600, 5800, 1700)),
        ["comment"] = "boulder 1",
    };
    static JsonObject Trail() => new()
    {
        ["host_uid"] = HostUid,
        ["outline"] = Outline(P(0, 9000), P(30000, 9000), P(30000, 10500), P(0, 10500)),
        ["material"] = "Gravel",
        ["subdivide_height_mm"] = 50,
        ["comment"] = "trail",
    };
    static JsonObject Sample(string command) => command switch
    {
        "query.toposolid_types" => new JsonObject(),
        "toposolid.create" => Slope(),
        "toposolid.create_subdivision" => Trail(),
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };
    static bool Near(double a, double b, double tolerance = 1e-6) => Math.Abs(a - b) <= tolerance;
    static BoxMm Box(double x0, double y0, double z0, double x1, double y1, double z1) => new(x0, y0, z0, x1, y1, z1);

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
        Toposolids();
        Subdivisions();
        ClientPath();
        ExecutorRules();
        Readbacks();
        ReadbackJson();
    }

    static void Catalogue()
    {
        var names = RevitCommandCatalogue.Site.Select(spec => spec.Name).ToArray();
        Check(names.SequenceEqual(SiteCommands), "site catalogue lists query.toposolid_types, toposolid.create and toposolid.create_subdivision in order", string.Join(",", names));
        Check(RevitCommandCatalogue.Site.All(spec => RevitCommandCatalogue.All.Contains(spec)), "every site command is in the catalogue enumeration");
        Check(RevitCommandCatalogue.All.Select(spec => spec.Name).Distinct().Count() == RevitCommandCatalogue.All.Count && RevitCommandCatalogue.All.Select(spec => spec.ToolName).Distinct().Count() == RevitCommandCatalogue.All.Count, "command and tool names stay distinct with the site commands");
        Check(RevitCommandCatalogue.Site.Select(spec => spec.ToolName).SequenceEqual(new[] { "revit_query_toposolid_types", "revit_toposolid_create", "revit_toposolid_create_subdivision" }), "site tool names are revit_<command with . replaced by _>");
        Check(RevitCommandCatalogue.Site.Where(spec => spec.Writes).Select(spec => spec.Name).SequenceEqual(Writers), "the two site creators write; the type query is read-only");
        Check(RevitCommandCatalogue.Site.All(spec => !RevitCommandCatalogue.IsDestructive(spec.Name)), "site commands only create elements: none is destructive");
        foreach (var name in names) Check(ReferenceEquals(RevitCommandCatalogue.Find(name), RevitCommandCatalogue.Site.Single(spec => spec.Name == name)), "Find resolves site command " + name);
        Check(RevitCommandCatalogue.Site.All(spec => spec.Description.Length > 20 && spec.Description.EndsWith('.') && (spec.Writes ? spec.Description.Contains("enabled test copies", StringComparison.Ordinal) : spec.Description.Contains("Read-only", StringComparison.Ordinal))),
            "every site command has a plain-English description; writes state the copies-only rule and queries say Read-only");
        foreach (var spec in RevitCommandCatalogue.Site)
        {
            string? problem = null;
            try { using var schema = JsonDocument.Parse(spec.ArgsSchemaJson); RevitCommandSchema.CheckSupported(schema.RootElement); if (schema.RootElement.GetProperty("type").GetString() != "object") problem = "not an object schema"; }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { problem = error.Message; }
            Check(problem is null, spec.Name + " args schema is valid JSON using only enforced keywords", problem ?? "");
        }
        var copies = new[] { @"C:\Repo\.bim\native-runs" };
        foreach (var spec in RevitCommandCatalogue.Site)
            Check(PermissionPolicy.RequiredLevel(spec.Name, FixturePath, copies) == (spec.Writes ? PermissionLevel.WriteCopies : PermissionLevel.Read)
                && PermissionPolicy.RequiredLevel(spec.Name, RealPath, copies) == (spec.Writes ? PermissionLevel.WriteRealProject : PermissionLevel.Read), "permission level of " + spec.Name + " follows its catalogue entry");
        Check(RevitSiteRules.MinToposolidPoints == 3 && RevitSiteRules.MaxToposolidPoints == 2000, "toposolid point caps are 3 to 2000");
        Check(Accepts("query.toposolid_types with {} accepted", Envelope("query.toposolid_types", new JsonObject()))?.Args is EmptyQueryArgs, "query.toposolid_types binds empty arguments");
        Rejects("query.toposolid_types with an argument", Envelope("query.toposolid_types", new JsonObject { ["x"] = 1 }), RevitCommandCodes.ArgsInvalid, "args.x");
    }

    static void Toposolids()
    {
        var slope = Accepts("slope toposolid accepted", Envelope("toposolid.create", Slope()));
        Check(slope?.Args is ToposolidCreateArgs { Level: "Level 1", Type: "Toposolid 2000mm", Comment: "site: cabin slope", Points.Count: 5 } s && Near(s.Points[2].X.Millimetres, 30000) && Near(s.Points[2].Y.Millimetres, 20000) && Near(s.Points[2].Z.Millimetres, 4500),
            "toposolid binds level, type, comment and typed [x, y, z] millimetre points in order");
        Check(Accepts("boulder toposolid accepted", Envelope("toposolid.create", Boulder()))?.Args is ToposolidCreateArgs { Points.Count: 6 }, "a ten-point-class boulder is a small toposolid");
        Check(Accepts("toposolid without comment accepted", Envelope("toposolid.create", With(Slope(), "comment", null)))?.Args is ToposolidCreateArgs { Comment: null }, "comment is optional");
        Check(Accepts("three points accepted", Envelope("toposolid.create", With(Slope(), "points", Points(P(0, 0, 0), P(1000, 0, 0), P(0, 1000, 100)))))?.Args is ToposolidCreateArgs { Points.Count: 3 }, "three non-collinear points are the minimum");
        Check(Accepts("negative elevations accepted", Envelope("toposolid.create", With(Slope(), "points", Points(P(-5000, -5000, -1200), P(5000, -5000, -800), P(0, 5000, -300)))))?.Args is ToposolidCreateArgs { Points: var negative } && Near(negative[0].Z.Millimetres, -1200), "points may lie below the origin");
        Rejects("two points", Envelope("toposolid.create", With(Slope(), "points", Points(P(0, 0, 0), P(1000, 0, 0)))), RevitCommandCodes.ArgsInvalid, "args.points");
        Rejects("no points member", Envelope("toposolid.create", With(Slope(), "points", null)), RevitCommandCodes.ArgsInvalid, "args.points");
        Rejects("no level", Envelope("toposolid.create", With(Slope(), "level", null)), RevitCommandCodes.ArgsInvalid, "args.level");
        Rejects("no type", Envelope("toposolid.create", With(Slope(), "type", null)), RevitCommandCodes.ArgsInvalid, "args.type");
        Rejects("unexpected argument", Envelope("toposolid.create", With(Slope(), "profile", new JsonArray())), RevitCommandCodes.ArgsInvalid, "args.profile");
        Rejects("point with two coordinates", Envelope("toposolid.create", With(Slope(), "points", Points(P(0, 0, 0), P(1000, 0), P(0, 1000, 0)))), RevitCommandCodes.ArgsInvalid, "args.points[1]");
        Rejects("point with four coordinates", Envelope("toposolid.create", With(Slope(), "points", Points(P(0, 0, 0), P(1000, 0, 0, 1), P(0, 1000, 0)))), RevitCommandCodes.ArgsInvalid, "args.points[1]");
        var textCoordinate = Slope(); textCoordinate["points"]![1]![2] = "500";
        Rejects("coordinate as string", Envelope("toposolid.create", textCoordinate), RevitCommandCodes.ArgsInvalid, "args.points[1][2]");
        var huge = Envelope("toposolid.create", Slope()).ToJsonString().Replace("4500", "1e400", StringComparison.Ordinal);
        var hugeError = Refusal(huge);
        Check(hugeError is { Code: RevitCommandCodes.LengthInvalid, Field: "args.points[2][2]" }, "an overflowing z coordinate is length_invalid at its index", hugeError is null ? "accepted" : hugeError.Code + " at " + hugeError.Field);
        Rejects("x beyond 1e8 mm", Envelope("toposolid.create", With(Slope(), "points", Points(P(0, 0, 0), P(100_000_001, 0, 0), P(0, 1000, 0)))), RevitCommandCodes.LengthInvalid, "args.points[1][0]");
        Rejects("empty comment", Envelope("toposolid.create", With(Slope(), "comment", "")), RevitCommandCodes.ArgsInvalid, "args.comment");
        Rejects("comment with a newline", Envelope("toposolid.create", With(Slope(), "comment", "a\nb")), RevitCommandCodes.ArgsInvalid, "args.comment");
        Rejects("comment over 256 characters", Envelope("toposolid.create", With(Slope(), "comment", new string('c', 257))), RevitCommandCodes.ArgsInvalid, "args.comment");
        // Plan-distinct points: two points within 1.2 mm in plan would make a vertical face.
        Rejects("duplicate plan point", Envelope("toposolid.create", With(Slope(), "points", Points(P(0, 0, 0), P(1000, 0, 0), P(0, 1000, 0), P(1000.5, 0.5, 300)))), RevitCommandCodes.ArgsInvalid, "args.points[3]");
        Rejects("duplicate plan point found after sorting", Envelope("toposolid.create", With(Slope(), "points", Points(P(5000, 0, 0), P(0, 0, 0), P(0, 4000, 10), P(4999.9, 0.2, 40)))), RevitCommandCodes.ArgsInvalid, "args.points[3]");
        Check(Accepts("points 1.2 mm apart accepted", Envelope("toposolid.create", With(Slope(), "points", Points(P(0, 0, 0), P(1.2, 0, 0), P(0, 1000, 0), P(1000, 1000, 0)))))?.Args is ToposolidCreateArgs, "points exactly 1.2 mm apart in plan are distinct");
        Rejects("collinear points", Envelope("toposolid.create", With(Slope(), "points", Points(P(0, 0, 0), P(1000, 1000, 50), P(2000, 2000, 100), P(3000, 3000, 0)))), RevitCommandCodes.BoundaryZeroArea, "args.points");
        Rejects("nearly collinear points (1 mm off the line)", Envelope("toposolid.create", With(Slope(), "points", Points(P(0, 0, 0), P(5000, 1, 0), P(10000, 0, 0)))), RevitCommandCodes.BoundaryZeroArea, "args.points");
        Rejects("collinear far from the origin", Envelope("toposolid.create", With(Slope(), "points", Points(P(5e7, 5e7, 0), P(5e7 + 1000, 5e7, 0), P(5e7 + 2000, 5e7, 0)))), RevitCommandCodes.BoundaryZeroArea, "args.points");
        // 2000 points on a 50 x 40 grid at 1 m spacing with one-decimal elevations fit the request cap.
        var grid = new JsonArray();
        for (var i = 0; i < 2000; i++) grid.Add(Triple(i % 50 * 1000, i / 50 * 1000, Math.Round(1000 + i * 0.7, 1)));
        var full = Envelope("toposolid.create", With(Slope(), "points", grid));
        Check(Accepts("2000 points accepted", full)?.Args is ToposolidCreateArgs { Points.Count: 2000 }, "the 2000-point cap is accepted");
        Check(full.ToJsonString().Length <= RevitCommandProtocol.MaxRequestChars, "a 2000-point grid with one-decimal elevations fits the 65536-character request cap", full.ToJsonString().Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var tooMany = (JsonArray)grid.DeepClone(); tooMany.Add(Triple(99999, 99999, 0));
        Rejects("2001 points", Envelope("toposolid.create", With(Slope(), "points", tooMany)), RevitCommandCodes.ArgsInvalid, "args.points");
        Check(Thrown(() => RevitSiteRules.CheckToposolidPoints(new[] { (0.0, 0.0, 0.0), (1000.0, 0.0, 0.0), (0.0, 1000.0, double.NaN) }, "args.points")) is { Code: RevitCommandCodes.LengthInvalid, Field: "args.points[2][2]" }, "a non-finite elevation is refused by the rule itself");
    }

    static void Subdivisions()
    {
        var trail = Accepts("trail subdivision accepted", Envelope("toposolid.create_subdivision", Trail()));
        Check(trail?.Args is ToposolidSubdivisionCreateArgs { HostUid: HostUid, Material: "Gravel", Comment: "trail", Outline.Count: 4 } t && t.SubdivideHeight is { } height && Near(height.Millimetres, 50) && Near(t.Outline[1].X.Millimetres, 30000),
            "subdivision binds host, outline, material, subdivide height and comment");
        var minimal = Accepts("subdivision with host and outline only accepted", Envelope("toposolid.create_subdivision", With(With(With(Trail(), "material", null), "subdivide_height_mm", null), "comment", null)));
        Check(minimal?.Args is ToposolidSubdivisionCreateArgs { Material: null, SubdivideHeight: null, Comment: null }, "material, subdivide height and comment are optional");
        Check(Accepts("zero subdivide height accepted", Envelope("toposolid.create_subdivision", With(Trail(), "subdivide_height_mm", 0)))?.Args is ToposolidSubdivisionCreateArgs { SubdivideHeight: { } zero } && zero.Millimetres == 0, "a zero subdivide height is allowed");
        Rejects("negative subdivide height", Envelope("toposolid.create_subdivision", With(Trail(), "subdivide_height_mm", -10)), RevitCommandCodes.LengthInvalid, "args.subdivide_height_mm");
        Rejects("subdivide height above the Revit limit", Envelope("toposolid.create_subdivision", With(Trail(), "subdivide_height_mm", 9_144_001)), RevitCommandCodes.LengthInvalid, "args.subdivide_height_mm");
        Rejects("host not a UniqueId", Envelope("toposolid.create_subdivision", With(Trail(), "host_uid", "site")), RevitCommandCodes.ArgsInvalid, "args.host_uid");
        Rejects("outline of two points", Envelope("toposolid.create_subdivision", With(Trail(), "outline", Outline(P(0, 0), P(1000, 0)))), RevitCommandCodes.ArgsInvalid, "args.outline");
        Rejects("self-intersecting outline", Envelope("toposolid.create_subdivision", With(Trail(), "outline", Outline(P(0, 0), P(1000, 1000), P(1000, 0), P(0, 1000)))), RevitCommandCodes.BoundarySelfIntersecting);
        Rejects("zero-area outline", Envelope("toposolid.create_subdivision", With(Trail(), "outline", Outline(P(0, 0), P(1000, 0), P(2000, 0)))), RevitCommandCodes.BoundarySelfIntersecting);
        Rejects("outline with [x, y, z] vertices", Envelope("toposolid.create_subdivision", With(Trail(), "outline", Points(P(0, 0, 0), P(1000, 0, 0), P(0, 1000, 0)))), RevitCommandCodes.ArgsInvalid, "args.outline[0]");
        Rejects("empty material", Envelope("toposolid.create_subdivision", With(Trail(), "material", "")), RevitCommandCodes.ArgsInvalid, "args.material");
        Rejects("unexpected argument", Envelope("toposolid.create_subdivision", With(Trail(), "type", "Gravel")), RevitCommandCodes.ArgsInvalid, "args.type");
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
        var read = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("query.toposolid_types", new JsonObject(), RealPath).ToJsonString(), policy, Transport, noLinks);
        Check(!read.IsError && read.Forwarded, "query.toposolid_types is read-only and allowed on a real project", read.Line);
        var invalid = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("toposolid.create", With(Slope(), "points", Points(P(0, 0, 0), P(1000, 0, 0), P(2000, 0, 0)))).ToJsonString(), policy, Transport, noLinks);
        Check(invalid.IsError && !invalid.Forwarded && JsonNode.Parse(invalid.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.BoundaryZeroArea, "collinear toposolid points return the typed failure before the pipe", invalid.Line);
        var a = RevitCommandProtocol.Parse(Envelope("toposolid.create", Slope()).ToJsonString());
        var reordered = Slope();
        var b = RevitCommandProtocol.Parse(new JsonObject { ["args"] = new JsonObject { ["comment"] = reordered["comment"]!.DeepClone(), ["points"] = reordered["points"]!.DeepClone(), ["type"] = "Toposolid 2000mm", ["level"] = "Level 1" }, ["command"] = "toposolid.create", ["operation_id"] = "op-site", ["protocol_version"] = 1, ["document"] = new JsonObject { ["unique_id"] = Uid, ["path"] = FixturePath } }.ToJsonString());
        Check(a.CanonicalJson == b.CanonicalJson && a.RequestSha256 == b.RequestSha256, "site requests use the same canonical form and hash as v1 (point order is kept)", a.CanonicalJson + " vs " + b.CanonicalJson);
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
        var extents = CommandRulesSite.Extents(new[] { (0.0, 0.0, 0.0), (30000.0, 0.0, 500.0), (30000.0, 20000.0, 4500.0), (15000.0, 10000.0, 2600.0) });
        Check(extents == Box(0, 0, 0, 30000, 20000, 4500), "requested extents are the min and max of the points in x, y and z", extents.ToString());
        Check(Thrown(() => CommandRulesSite.CheckSubdivisionHost(isSubdivision: false)) is null, "a toposolid that is not a subdivision can host one");
        Check(Thrown(() => CommandRulesSite.CheckSubdivisionHost(isSubdivision: true)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.host_uid" }, "a subdivision cannot host another subdivision");
        Check(Thrown(() => CommandRulesSite.MaterialParameterIndex(1, "args.material")) is null && CommandRulesSite.MaterialParameterIndex(1, "args.material") == 0, "exactly one writable material parameter is the subdivision material");
        Check(Thrown(() => CommandRulesSite.MaterialParameterIndex(0, "args.material")) is { Code: RevitCommandCodes.TransactionFailed, Field: "args.material" }, "no writable material parameter fails closed");
        Check(Thrown(() => CommandRulesSite.MaterialParameterIndex(2, "args.material")) is { Code: RevitCommandCodes.TransactionFailed, Field: "args.material" }, "two writable material parameters are ambiguous and fail closed");
    }

    static void Readbacks()
    {
        var requested = Box(0, 0, 0, 30000, 20000, 4500);
        var good = new ObservedToposolid(ElementUid, LevelUid, TypeUid, null, Box(0, 0, -2000, 30000, 20000, 4500), "site: cabin slope");
        Check(CommandReadbackSite.Toposolid(LevelUid, TypeUid, requested, "site: cabin slope", good) is null, "a toposolid with the requested type, level, plan extents, top and comment matches");
        Check(CommandReadbackSite.Toposolid(LevelUid.ToUpperInvariant(), TypeUid, requested, null, good) is null, "UniqueIds compare ignoring hex case; no comment requested is not checked");
        Check(CommandReadbackSite.Toposolid(LevelUid, TypeUid, requested, null, good with { Box = Box(-0.8, 0.5, -2000, 30000.9, 19999.2, 4500.6) }) is null, "extents within 1 mm match (bounding boxes are not exact)");
        Check(CommandReadbackSite.Toposolid(LevelUid, OtherUid, requested, null, good) is { } wrongType && wrongType.Contains("type", StringComparison.Ordinal), "a different type is a mismatch");
        Check(CommandReadbackSite.Toposolid(OtherUid, TypeUid, requested, null, good) is { } wrongLevel && wrongLevel.Contains("level", StringComparison.Ordinal), "a different level is a mismatch");
        Check(CommandReadbackSite.Toposolid(LevelUid, TypeUid, requested, null, good with { LevelUid = null }) is not null, "an unreadable level is a mismatch");
        Check(CommandReadbackSite.Toposolid(LevelUid, TypeUid, requested, null, good with { HostUid = OtherUid }) is { } hosted && hosted.Contains("subdivision", StringComparison.Ordinal), "a new toposolid that reports a host is a mismatch");
        Check(CommandReadbackSite.Toposolid(LevelUid, TypeUid, requested, null, good with { Box = null }) is { } noBox && noBox.Contains("bounding box", StringComparison.Ordinal), "no bounding box is a mismatch");
        Check(CommandReadbackSite.Toposolid(LevelUid, TypeUid, requested, null, good with { Box = Box(0, 0, -2000, 28000, 20000, 4500) }) is { } narrow && narrow.Contains("plan", StringComparison.Ordinal), "plan extents more than 1 mm off are a mismatch");
        Check(CommandReadbackSite.Toposolid(LevelUid, TypeUid, requested, null, good with { Box = Box(0, 0, 1000, 30000, 20000, 7500) }) is { } top && top.Contains("top", StringComparison.Ordinal), "a top more than 1 mm off the highest point (for example shifted by the level elevation) is a mismatch");
        Check(CommandReadbackSite.Toposolid(LevelUid, TypeUid, requested, null, good with { Box = Box(0, 0, 4500, 30000, 20000, 4500) }) is { } flat && flat.Contains("bottom", StringComparison.Ordinal), "a bottom not below the lowest point (no thickness) is a mismatch");
        Check(CommandReadbackSite.Toposolid(LevelUid, TypeUid, requested, "site: cabin slope", good with { Comment = "other" }) is { } comment && comment.Contains("Comments", StringComparison.Ordinal), "a different Comments value is a mismatch");
        Check(CommandReadbackSite.Toposolid(LevelUid, TypeUid, requested, "site: cabin slope", good with { Comment = null }) is not null, "an unreadable Comments value is a mismatch when one was requested");

        var sub = new ObservedSubdivision(ElementUid, HostUid, true, MaterialUid, 50, Box(0, 9000, 1500, 30000, 10500, 3200), "trail");
        Check(CommandReadbackSite.Subdivision(HostUid, MaterialUid, 50, "trail", sub) is null, "a subdivision on the host with material, height and comment matches");
        Check(CommandReadbackSite.Subdivision(HostUid, null, null, null, sub with { MaterialUid = null, SubdivideHeightMm = null, Comment = null }) is null, "unrequested material, height and comment are not checked");
        Check(CommandReadbackSite.Subdivision(OtherUid, null, null, null, sub) is { } host && host.Contains("host", StringComparison.Ordinal), "a different host is a mismatch");
        Check(CommandReadbackSite.Subdivision(HostUid, null, null, null, sub with { HostUid = null }) is not null, "a subdivision without host is a mismatch");
        Check(CommandReadbackSite.Subdivision(HostUid, null, null, null, sub with { ListedByHost = false }) is { } listed && listed.Contains("GetSubDivisionIds", StringComparison.Ordinal), "a subdivision the host does not list is a mismatch");
        Check(CommandReadbackSite.Subdivision(HostUid, MaterialUid, null, null, sub with { MaterialUid = OtherUid }) is { } material && material.Contains("material", StringComparison.Ordinal), "a different material is a mismatch");
        Check(CommandReadbackSite.Subdivision(HostUid, null, 50, null, sub with { SubdivideHeightMm = 40 }) is { } height && height.Contains("height", StringComparison.Ordinal), "a different subdivide height is a mismatch");
        Check(CommandReadbackSite.Subdivision(HostUid, null, 50, null, sub with { SubdivideHeightMm = null }) is not null, "an unreadable subdivide height is a mismatch when one was requested");
        Check(CommandReadbackSite.Subdivision(HostUid, null, null, "trail", sub with { Comment = "x" }) is not null, "a different Comments value on a subdivision is a mismatch");
        Check(CommandReadbackSite.Subdivision(HostUid, null, null, null, sub with { Box = null }) is { } subBox && subBox.Contains("bounding box", StringComparison.Ordinal), "a subdivision without bounding box is a mismatch");
    }

    static void ReadbackJson()
    {
        var types = JsonNode.Parse(CommandReadbackSite.ToposolidTypesJson(new[] { new ToposolidTypeRow("Toposolid 2000mm", OtherUid, 2000), new ToposolidTypeRow("Boulder", TypeUid, null) }))!["toposolid_types"]!.AsArray();
        Check(types.Select(row => row!["name"]!.GetValue<string>()).SequenceEqual(new[] { "Boulder", "Toposolid 2000mm" }) && types[0]!["thickness_mm"] is null && types[1]!["thickness_mm"]!.GetValue<double>() == 2000 && types[1]!["unique_id"]!.GetValue<string>() == OtherUid,
            "toposolid types readback is sorted by name with UniqueId and thickness (null when unknown)", types.ToJsonString());
        var topo = JsonNode.Parse(CommandReadbackSite.ToposolidJson(new ObservedToposolid(ElementUid, LevelUid, TypeUid, null, Box(0, 0, -2000, 30000, 20000, 4500), "site"), 5))!["toposolid"]!;
        Check(topo["unique_id"]!.GetValue<string>() == ElementUid && topo["level_unique_id"]!.GetValue<string>() == LevelUid && topo["type_unique_id"]!.GetValue<string>() == TypeUid && topo["comment"]!.GetValue<string>() == "site"
            && topo["point_count"]!.GetValue<int>() == 5 && topo["bounding_box_mm"]!["min"]![2]!.GetValue<double>() == -2000 && topo["bounding_box_mm"]!["max"]![0]!.GetValue<double>() == 30000 && topo["bounding_box_mm"]!["max"]![2]!.GetValue<double>() == 4500,
            "toposolid readback JSON reports UniqueId, level, type, comment, requested point count and the bounding box min/max [x, y, z] in millimetres", topo.ToJsonString());
        var empty = JsonNode.Parse(CommandReadbackSite.ToposolidJson(new ObservedToposolid(ElementUid, null, TypeUid, null, null, null), 3))!["toposolid"]!;
        Check(empty["level_unique_id"] is null && empty["bounding_box_mm"] is null && empty["comment"] is null, "unreadable toposolid values are null", empty.ToJsonString());
        var sub = JsonNode.Parse(CommandReadbackSite.SubdivisionJson(new ObservedSubdivision(ElementUid, HostUid, true, MaterialUid, 50, Box(0, 9000, 1500, 30000, 10500, 3200), "trail")))!["toposolid_subdivision"]!;
        Check(sub["unique_id"]!.GetValue<string>() == ElementUid && sub["host_unique_id"]!.GetValue<string>() == HostUid && sub["listed_by_host"]!.GetValue<bool>() && sub["material_unique_id"]!.GetValue<string>() == MaterialUid
            && sub["subdivide_height_mm"]!.GetValue<double>() == 50 && sub["comment"]!.GetValue<string>() == "trail" && sub["bounding_box_mm"]!["min"]![1]!.GetValue<double>() == 9000,
            "subdivision readback JSON reports UniqueId, host, listing, material, subdivide height, comment and bounding box", sub.ToJsonString());
    }
}
