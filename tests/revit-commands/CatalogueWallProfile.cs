using Bim.Core;
using Bim.Revit;
using System.Text.Json;
using System.Text.Json.Nodes;
// wall.set_profile contract oracle: replace the elevation profile of a straight wall (gable end walls under a gable
// roof on Revit 2025.2, where Wall.AddAttachment does not exist). Covers the schema, the typed binder (closed
// non-self-intersecting [u, z] polygon, 3 to 200 vertices), the guarded client path (fake transport) and the
// Autodesk-free executor rules and readback in runtimes/revit-native/CommandsWallProfile.cs. No Revit is involved: a
// passing run is unit-tested evidence only; CommandsExecutorWallProfile.cs is compiled, never run.
static class CatalogueWallProfileChecks
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string WallUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0002a001";
    const string SketchUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0002a0ff";
    const string DoorUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0002d001";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static JsonObject Envelope(JsonNode args, string path = FixturePath) => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-profile",
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
        ["command"] = "wall.set_profile",
        ["args"] = args.DeepClone(),
    };
    static JsonArray Profile(params double[][] vertices) => new(vertices.Select(v => (JsonNode?)new JsonArray(v.Select(x => (JsonNode?)x).ToArray())).ToArray());
    static double[] P(params double[] values) => values;
    // Gable end wall 6000 long, base 0, eaves 2700, ridge 5200 at the middle.
    static JsonArray GableProfile() => Profile(P(0, 0), P(6000, 0), P(6000, 2700), P(3000, 5200), P(0, 2700));
    static JsonObject Gable() => new() { ["wall_uid"] = WallUid, ["profile"] = GableProfile() };
    static JsonObject With(JsonObject args, string member, JsonNode? value) { if (value is null) args.Remove(member); else args[member] = value; return args; }
    static ProfilePointMm[] Pts(params double[][] vertices) => vertices.Select(v => new ProfilePointMm(v[0], v[1])).ToArray();
    static ProfilePointMm[] GablePts() => Pts(P(0, 0), P(6000, 0), P(6000, 2700), P(3000, 5200), P(0, 2700));
    static ProfileEdgeMm[] Edges(IReadOnlyList<ProfilePointMm> loop, bool reverse = false, int rotate = 0)
    {
        var points = reverse ? loop.Reverse().ToArray() : loop.ToArray();
        points = points.Skip(rotate).Concat(points.Take(rotate)).ToArray();
        return points.Select((point, i) => new ProfileEdgeMm(point, points[(i + 1) % points.Length], true)).ToArray();
    }
    static ObservedWallProfile Observed(IReadOnlyList<ProfileEdgeMm> edges, int loops = 1) =>
        new(WallUid, SketchUid, edges, loops, new PlanPoint(0, 0), new PlanPoint(6000, 0), 5200, 0, 5200);

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

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        Catalogue();
        Binding();
        ClientPath();
        WallRules();
        InsertRules();
        Readback();
        ReadbackJson();
    }

    static void Catalogue()
    {
        Check(RevitCommandCatalogue.WallProfile.Select(spec => spec.Name).SequenceEqual(new[] { "wall.set_profile" }), "wall profile catalogue lists wall.set_profile");
        var spec = RevitCommandCatalogue.WallProfile.Single();
        Check(RevitCommandCatalogue.All.Contains(spec) && ReferenceEquals(RevitCommandCatalogue.Find("wall.set_profile"), spec), "wall.set_profile is in the catalogue enumeration and Find resolves it");
        Check(RevitCommandCatalogue.All.Select(s => s.Name).Distinct().Count() == RevitCommandCatalogue.All.Count && RevitCommandCatalogue.All.Select(s => s.ToolName).Distinct().Count() == RevitCommandCatalogue.All.Count, "command and tool names stay distinct with wall.set_profile");
        Check(spec.ToolName == "revit_wall_set_profile" && spec.Writes, "wall.set_profile is the write tool revit_wall_set_profile");
        Check(RevitCommandCatalogue.IsDestructive("wall.set_profile"), "wall.set_profile replaces an existing wall's shape: destructive");
        Check(spec.Description.EndsWith('.') && spec.Description.Contains("enabled test copies", StringComparison.Ordinal) && spec.Description.Contains("base offset", StringComparison.OrdinalIgnoreCase)
            && spec.Description.Contains("straight", StringComparison.Ordinal) && spec.Description.Contains("door", StringComparison.Ordinal),
            "the description states the copies-only rule, straight walls only, the base offset semantics of z and the hosted door/window refusal", spec.Description);
        string? problem = null;
        try { using var schema = JsonDocument.Parse(spec.ArgsSchemaJson); RevitCommandSchema.CheckSupported(schema.RootElement); if (schema.RootElement.GetProperty("type").GetString() != "object") problem = "not an object schema"; }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { problem = error.Message; }
        Check(problem is null, "wall.set_profile args schema is valid JSON using only enforced keywords", problem ?? "");
        var copies = new[] { @"C:\Repo\.bim\native-runs" };
        Check(PermissionPolicy.RequiredLevel("wall.set_profile", FixturePath, copies) == PermissionLevel.WriteCopies && PermissionPolicy.RequiredLevel("wall.set_profile", RealPath, copies) == PermissionLevel.WriteRealProject,
            "wall.set_profile is write-copies on a copy and write-real-project elsewhere");
        Check(RevitWallProfileRules.MinVertices == 3 && RevitWallProfileRules.MaxVertices == 200, "profile vertex caps are 3 to 200");
    }

    static void Binding()
    {
        var gable = Accepts("gable end wall profile accepted", Envelope(Gable()));
        Check(gable?.Args is WallSetProfileArgs { WallUid: WallUid, Profile.Count: 5 } g && g.Profile[3].U.Millimetres == 3000 && g.Profile[3].Z.Millimetres == 5200 && g.Profile[2].Z.Millimetres == 2700,
            "wall.set_profile binds the wall UniqueId and typed [u, z] millimetre vertices in order");
        Check(Accepts("closing vertex repeated accepted", Envelope(With(Gable(), "profile", Profile(P(0, 0), P(6000, 0), P(6000, 2700), P(3000, 5200), P(0, 2700), P(0, 0)))))?.Args is WallSetProfileArgs { Profile.Count: 5 },
            "a repeated closing vertex is dropped (the closing edge is implicit)");
        Check(Accepts("triangle accepted", Envelope(With(Gable(), "profile", Profile(P(0, 0), P(6000, 0), P(3000, 2500)))))?.Args is WallSetProfileArgs { Profile.Count: 3 }, "three vertices are the minimum");
        Check(Accepts("negative z accepted", Envelope(With(Gable(), "profile", Profile(P(0, -300), P(6000, -300), P(6000, 2700), P(0, 2700)))))?.Args is WallSetProfileArgs { Profile: var below } && below[0].Z.Millimetres == -300,
            "z may be below the base level (a wall reaching under it)");
        Rejects("two vertices", Envelope(With(Gable(), "profile", Profile(P(0, 0), P(6000, 0)))), RevitCommandCodes.ArgsInvalid, "args.profile");
        var many = new JsonArray();
        for (var i = 0; i < 201; i++) { var a = 2 * Math.PI * i / 201; many.Add(new JsonArray(3000 + 3000 * Math.Cos(a), 3000 + 3000 * Math.Sin(a))); }
        Rejects("201 vertices", Envelope(With(Gable(), "profile", many)), RevitCommandCodes.ArgsInvalid, "args.profile");
        many.RemoveAt(200);
        Check(Accepts("200 vertices accepted", Envelope(With(Gable(), "profile", many)))?.Args is WallSetProfileArgs { Profile.Count: 200 }, "the 200-vertex cap is accepted");
        Rejects("no profile", Envelope(With(Gable(), "profile", null)), RevitCommandCodes.ArgsInvalid, "args.profile");
        Rejects("no wall", Envelope(With(Gable(), "wall_uid", null)), RevitCommandCodes.ArgsInvalid, "args.wall_uid");
        Rejects("wall not a UniqueId", Envelope(With(Gable(), "wall_uid", "Basic Wall")), RevitCommandCodes.ArgsInvalid, "args.wall_uid");
        Rejects("unexpected argument", Envelope(With(Gable(), "level", "Level 1")), RevitCommandCodes.ArgsInvalid, "args.level");
        Rejects("vertex with three coordinates", Envelope(With(Gable(), "profile", Profile(P(0, 0), P(6000, 0, 0), P(3000, 2500)))), RevitCommandCodes.ArgsInvalid, "args.profile[1]");
        var text = Gable(); text["profile"]![2]![1] = "2700";
        Rejects("coordinate as string", Envelope(text), RevitCommandCodes.ArgsInvalid, "args.profile[2][1]");
        Rejects("z beyond Revit's limit", Envelope(With(Gable(), "profile", Profile(P(0, 0), P(6000, 0), P(3000, 9_144_001)))), RevitCommandCodes.LengthInvalid, "args.profile[2][1]");
        Rejects("u beyond Revit's limit", Envelope(With(Gable(), "profile", Profile(P(0, 0), P(-9_144_001, 0), P(3000, 2000)))), RevitCommandCodes.LengthInvalid, "args.profile[1][0]");
        Rejects("self-intersecting bow tie", Envelope(With(Gable(), "profile", Profile(P(0, 0), P(6000, 2700), P(6000, 0), P(0, 2700)))), RevitCommandCodes.BoundarySelfIntersecting);
        Rejects("collinear vertices", Envelope(With(Gable(), "profile", Profile(P(0, 0), P(3000, 0), P(6000, 0)))), RevitCommandCodes.BoundarySelfIntersecting);
        Rejects("edge shorter than 1.2 mm", Envelope(With(Gable(), "profile", Profile(P(0, 0), P(6000, 0), P(6000, 0.5), P(0, 2700)))), RevitCommandCodes.BoundaryEdgeTooShort);
    }

    static void ClientPath()
    {
        var policy = new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" });
        var forwarded = new List<string>();
        string? Transport(string pipe, string line) { forwarded.Add(line); return null; }
        var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
        var ok = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(Gable()).ToJsonString(), policy, Transport, noLinks);
        Check(forwarded.Count == 1 && forwarded[0] == RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(Envelope(Gable()).ToJsonString())), "wall.set_profile on an enabled copy is forwarded as the canonical wire line", ok.Line);
        var refused = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(Gable(), RealPath).ToJsonString(), policy, Transport, noLinks);
        Check(refused.IsError && !refused.Forwarded && JsonNode.Parse(refused.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentNotEnabled && forwarded.Count == 1, "wall.set_profile on a real project is document_not_enabled and never forwarded", refused.Line);
        var bowTie = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(With(Gable(), "profile", Profile(P(0, 0), P(6000, 2700), P(6000, 0), P(0, 2700)))).ToJsonString(), policy, Transport, noLinks);
        Check(bowTie.IsError && !bowTie.Forwarded && JsonNode.Parse(bowTie.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.BoundarySelfIntersecting && forwarded.Count == 1, "a self-intersecting profile is refused before the pipe", bowTie.Line);
        var a = RevitCommandProtocol.Parse(Envelope(Gable()).ToJsonString());
        var b = RevitCommandProtocol.Parse(new JsonObject { ["args"] = new JsonObject { ["profile"] = GableProfile(), ["wall_uid"] = WallUid }, ["command"] = "wall.set_profile", ["operation_id"] = "op-profile", ["protocol_version"] = 1, ["document"] = new JsonObject { ["unique_id"] = Uid, ["path"] = FixturePath } }.ToJsonString());
        Check(a.CanonicalJson == b.CanonicalJson && a.RequestSha256 == b.RequestSha256, "wall.set_profile requests use the v1 canonical form and hash (vertex order kept)");
    }

    static void WallRules()
    {
        Check(Thrown(() => CommandRulesWallProfile.CheckWall("Basic", isArc: false, canHaveProfileSketch: true)) is null, "a straight basic wall that can have a profile sketch is accepted");
        Check(Thrown(() => CommandRulesWallProfile.CheckWall("Basic", isArc: true, canHaveProfileSketch: true)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.wall_uid" } curved && curved.Detail!.Contains("straight", StringComparison.Ordinal),
            "a curved wall is args_invalid at args.wall_uid (straight walls only)");
        Check(Thrown(() => CommandRulesWallProfile.CheckWall("Curtain", isArc: false, canHaveProfileSketch: true)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.wall_uid" }, "a curtain wall is args_invalid");
        Check(Thrown(() => CommandRulesWallProfile.CheckWall("Stacked", isArc: false, canHaveProfileSketch: true)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.wall_uid" }, "a stacked wall is args_invalid");
        Check(Thrown(() => CommandRulesWallProfile.CheckWall("Basic", isArc: false, canHaveProfileSketch: false)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.wall_uid" } noSketch && noSketch.Detail!.Contains("profile", StringComparison.Ordinal),
            "a wall Revit says cannot have a profile sketch is args_invalid");
        Check(Thrown(() => CommandRulesWallProfile.CheckSpan(GablePts(), 6000)) is null, "a profile spanning u 0 to the wall length is accepted");
        Check(Thrown(() => CommandRulesWallProfile.CheckSpan(Pts(P(0.6, 0), P(5999.4, 0), P(3000, 2000)), 6000)) is null, "the span is compared within 1 mm");
        Check(Thrown(() => CommandRulesWallProfile.CheckSpan(Pts(P(0, 0), P(5000, 0), P(5000, 2700), P(0, 2700)), 6000)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.profile" } shortSpan && shortSpan.Detail!.Contains("6000", StringComparison.Ordinal),
            "a profile shorter than the wall is args_invalid naming the wall length (the location line is kept)");
        Check(Thrown(() => CommandRulesWallProfile.CheckSpan(Pts(P(-500, 0), P(6000, 0), P(6000, 2700), P(-500, 2700)), 6000)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.profile" }, "a profile reaching before the location-line start is args_invalid");
        var requested = CommandRulesWallProfile.Requested(new WallSetProfileArgs(WallUid, new[] { new ProfileVertex(Length.FromMillimetres(0), Length.FromMillimetres(0)), new ProfileVertex(Length.FromMillimetres(6000), Length.FromMillimetres(0)), new ProfileVertex(Length.FromMillimetres(3000), Length.FromMillimetres(2500)) }));
        Check(requested.SequenceEqual(Pts(P(0, 0), P(6000, 0), P(3000, 2500))), "requested profile points are the typed vertices in millimetres");
    }

    static void InsertRules()
    {
        var door = new WallInsertExtent(DoorUid, "Doors", 1000, 1900, 0, 2100);
        Check(CommandRulesWallProfile.Contains(GablePts(), door), "a door inside the gable profile is contained");
        Check(CommandRulesWallProfile.Contains(GablePts(), door with { UMin = 0, UMax = 900 }), "an opening touching the profile edge (within 1 mm) is contained");
        Check(!CommandRulesWallProfile.Contains(GablePts(), door with { UMin = 500, UMax = 1400, ZMin = 2000, ZMax = 4800 }), "a window crossing the sloped gable edge is not contained");
        Check(!CommandRulesWallProfile.Contains(GablePts(), door with { UMin = 7000, UMax = 7900 }), "an opening beyond the wall end is not contained");
        // A notch whose vertex pokes into the opening although every opening corner lies inside the profile.
        var notched = Pts(P(0, 0), P(6000, 0), P(6000, 2700), P(1450, 2700), P(1450, 1500), P(1400, 2700), P(0, 2700));
        Check(!CommandRulesWallProfile.Contains(notched, door), "a profile edge entering the opening's interior is not contained, even with every corner inside");
        Check(Thrown(() => CommandRulesWallProfile.CheckInserts(GablePts(), new[] { door })) is null, "a profile keeping every hosted opening is accepted");
        Check(Thrown(() => CommandRulesWallProfile.CheckInserts(Pts(P(0, 0), P(6000, 0), P(6000, 1800), P(0, 1800)), new[] { door })) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.profile" } cut
            && cut.Detail!.Contains(DoorUid, StringComparison.Ordinal) && cut.Detail.Contains("Doors", StringComparison.Ordinal),
            "a profile that would cut a hosted door is args_invalid naming its UniqueId and category");
        Check(Thrown(() => CommandRulesWallProfile.CheckInserts(GablePts(), Array.Empty<WallInsertExtent>())) is null, "a wall without hosted openings takes any valid profile");
    }

    static void Readback()
    {
        var requested = GablePts();
        Check(CommandReadbackWallProfile.Profile(requested, new PlanPoint(0, 0), new PlanPoint(6000, 0), Observed(Edges(requested))) is null, "the sketch outline equal to the request matches");
        Check(CommandReadbackWallProfile.Profile(requested, new PlanPoint(0, 0), new PlanPoint(6000, 0), Observed(Edges(requested, reverse: true, rotate: 2))) is null, "the outline matches whatever its start vertex and orientation");
        var shuffled = Edges(requested).Reverse().Select((edge, i) => i % 2 == 0 ? new ProfileEdgeMm(edge.B, edge.A, true) : edge).ToArray();
        Check(CommandReadbackWallProfile.Profile(requested, new PlanPoint(0, 0), new PlanPoint(6000, 0), Observed(shuffled)) is null, "edges in any order and direction are chained into the loop");
        var jitter = Pts(P(0.4, -0.3), P(6000.6, 0), P(5999.5, 2700.8), P(3000, 5199.2), P(0, 2700));
        Check(CommandReadbackWallProfile.Profile(requested, new PlanPoint(0, 0), new PlanPoint(6000, 0), Observed(Edges(jitter)) with { TopMm = 5200.4 }) is null, "vertices within 1 mm match");
        var split = Pts(P(0, 0), P(3000, 0), P(6000, 0), P(6000, 2700), P(3000, 5200), P(0, 2700));
        Check(CommandReadbackWallProfile.Profile(requested, new PlanPoint(0, 0), new PlanPoint(6000, 0), Observed(Edges(split))) is null, "an edge Revit split at a collinear vertex still matches");
        Check(CommandReadbackWallProfile.Profile(requested, new PlanPoint(0, 0), new PlanPoint(6000, 0), Observed(Edges(Pts(P(0, 0), P(6000, 0), P(6000, 2700), P(3000, 5000), P(0, 2700))))) is { } ridge && ridge.Contains("profile", StringComparison.Ordinal),
            "a ridge 200 mm low is a mismatch");
        Check(CommandReadbackWallProfile.Profile(requested, new PlanPoint(0, 0), new PlanPoint(6000, 0), Observed(Edges(Pts(P(0, 0), P(6000, 0), P(6000, 2700), P(0, 2700))))) is not null, "the unchanged rectangle is a mismatch");
        Check(CommandReadbackWallProfile.Profile(requested, new PlanPoint(0, 0), new PlanPoint(6000, 0), Observed(Edges(requested)) with { SketchUid = null }) is { } noSketch && noSketch.Contains("sketch", StringComparison.Ordinal), "no profile sketch is a mismatch");
        Check(CommandReadbackWallProfile.Profile(requested, new PlanPoint(0, 0), new PlanPoint(6000, 0), Observed(Edges(requested).Take(4).ToArray())) is { } open && open.Contains("closed", StringComparison.Ordinal), "an open outline is a mismatch");
        Check(CommandReadbackWallProfile.Profile(requested, new PlanPoint(0, 0), new PlanPoint(6000, 0), Observed(Edges(requested).Concat(Edges(Pts(P(1000, 500), P(2000, 500), P(2000, 1500)))).ToArray(), loops: 2)) is { } two && two.Contains("loop", StringComparison.Ordinal),
            "a second loop (an opening in the sketch) is a mismatch");
        Check(CommandReadbackWallProfile.Profile(requested, new PlanPoint(0, 0), new PlanPoint(6000, 0), Observed(Edges(requested).Select((e, i) => i == 3 ? e with { Straight = false } : e).ToArray())) is { } arc && arc.Contains("straight", StringComparison.Ordinal),
            "a non-straight sketch edge is a mismatch");
        Check(CommandReadbackWallProfile.Profile(requested, new PlanPoint(0, 0), new PlanPoint(6000, 0), Observed(Edges(requested)) with { End = new PlanPoint(5000, 0) }) is { } moved && moved.Contains("location", StringComparison.Ordinal), "a moved location line is a mismatch");
        Check(CommandReadbackWallProfile.Profile(requested, new PlanPoint(0, 0), new PlanPoint(6000, 0), Observed(Edges(requested)) with { TopMm = 2700 }) is { } top && top.Contains("bounding box", StringComparison.Ordinal), "a bounding box top at the eaves instead of the ridge is a mismatch");
        Check(CommandReadbackWallProfile.Profile(requested, new PlanPoint(0, 0), new PlanPoint(6000, 0), Observed(Edges(requested)) with { TopMm = 5220 }) is null, "the bounding box top is compared within 25 mm (Revit pads boxes)");
        Check(CommandReadbackWallProfile.Profile(requested, new PlanPoint(0, 0), new PlanPoint(6000, 0), Observed(Edges(requested)) with { TopMm = null }) is not null, "no bounding box is a mismatch");
    }

    static void ReadbackJson()
    {
        var json = JsonNode.Parse(CommandReadbackWallProfile.ProfileJson(Observed(Edges(GablePts(), rotate: 1))))!["wall_profile"]!;
        var profile = json["profile_mm"]!.AsArray();
        Check(json["unique_id"]!.GetValue<string>() == WallUid && json["sketch_unique_id"]!.GetValue<string>() == SketchUid && json["loop_count"]!.GetValue<int>() == 1 && profile.Count == 5
            && profile.Any(v => v![0]!.GetValue<double>() == 3000 && v[1]!.GetValue<double>() == 5200) && json["top_mm"]!.GetValue<double>() == 5200
            && json["base_offset_mm"]!.GetValue<double>() == 0 && json["unconnected_height_mm"]!.GetValue<double>() == 5200
            && json["location_start_mm"]![0]!.GetValue<double>() == 0 && json["location_end_mm"]![0]!.GetValue<double>() == 6000,
            "wall profile readback reports UniqueId, sketch, loop count, the read-back [u, z] outline, top above the base level, base offset, height and location line", json.ToJsonString());
        var open = JsonNode.Parse(CommandReadbackWallProfile.ProfileJson(Observed(Edges(GablePts()).Take(3).ToArray()) with { SketchUid = null, TopMm = null }))!["wall_profile"]!;
        Check(open["sketch_unique_id"] is null && open["profile_mm"] is null && open["top_mm"] is null, "an unreadable sketch, an open outline and no box read back as null", open.ToJsonString());
    }
}
