using Bim.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
// P2-01 Revit -> BIM-IR snapshot contract oracle: query.snapshot catalogue entry (read-only) and the Revit-free
// mapper in core/Bim.Core/Mirror*.cs (observed records -> BIM-IR elements, stable ids, units, unmapped section,
// canonical JSON). No Revit is involved: a passing run is unit-tested evidence only; the add-in collector is compiled.
static class MirrorSnapshotChecks
{
    const string Doc = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string OtherDoc = "11111111-2222-3333-4444-555555555555-00000001";
    const string L1 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000137";
    const string L2 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000138";
    const string W1 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00001001";
    const string W2 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00001002";
    const string W3 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00001003";
    const string D1 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00002001";
    const string N1 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00003001";
    const string F1 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00004001";
    const string R1 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00005001";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        Catalogue();
        Identity();
        Walls();
        Openings();
        Rooms();
        Unmapped();
        Determinism();
        EmptyModel();
    }

    static MirrorPoint P(double x, double y) => new(x, y);
    static MirrorObservedLevel[] Levels() => new[] { new MirrorObservedLevel(L1, "Level 1", 0), new MirrorObservedLevel(L2, "Level 2", 3000) };
    static MirrorObservedElement Wall(string uid, double ax, double ay, double bx, double by, string? level = L1) =>
        new(MirrorCategories.Wall, uid) { LevelUid = level, Start = P(ax, ay), End = P(bx, by), HeightMm = 3000, ThicknessMm = 200 };
    static MirrorObservedElement Door(string uid, string? host, double x, double y, double width = 900) =>
        new(MirrorCategories.Door, uid) { LevelUid = L1, HostUid = host, Location = P(x, y), WidthMm = width, HeightMm = 2100 };
    static MirrorObservedElement Window(string uid, string? host, double x, double y) =>
        new(MirrorCategories.Window, uid) { LevelUid = L1, HostUid = host, Location = P(x, y), WidthMm = 1200, HeightMm = 1200, SillMm = 900 };
    static MirrorObservedElement Room(string uid, MirrorPoint[] loop, bool curves = false, string[]? bounding = null) =>
        new(MirrorCategories.Room, uid) { LevelUid = L1, Loops = new[] { new MirrorLoop(loop, curves, bounding ?? new[] { W1, W2 }) } };
    static MirrorPoint[] Rectangle() => new[] { P(100, 100), P(2500, 100), P(4900, 100), P(4900, 3900), P(100, 3900) };

    static MirrorObservedElement[] Sample() => new[]
    {
        Wall(W1, 0, 0, 5000, 0),
        Wall(W2, 5000, 0, 5000, 4000),
        new MirrorObservedElement(MirrorCategories.Wall, W3) { LevelUid = L1, Curved = true, HeightMm = 3000, ThicknessMm = 200 },
        Door(D1, W1, 1000, 0),
        Window(N1, W2, 5000, 2000),
        new MirrorObservedElement(MirrorCategories.Floor, F1) { LevelUid = L1 },
        Room(R1, Rectangle()),
    };

    static MirrorSnapshot Map(IEnumerable<MirrorObservedElement> elements, IEnumerable<MirrorObservedLevel>? levels = null, string doc = Doc) =>
        MirrorSnapshotMapper.Map(new MirrorObservation(doc, (levels ?? Levels()).ToArray(), elements.ToArray()));

    static Element? ById(MirrorSnapshot snapshot, string uid) => snapshot.Elements.SingleOrDefault(e => e.Id == MirrorSnapshotMapper.Id(Doc, uid));
    static MirrorUnmapped? Missing(MirrorSnapshot snapshot, string uid) => snapshot.Unmapped.SingleOrDefault(u => u.UniqueId == uid);
    static string G(Element? e) => e is null ? "missing" : string.Join(",", e.GeometryMetres.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
    static Guid Id(string uid) => MirrorSnapshotMapper.Id(Doc, uid);

    static void Catalogue()
    {
        Check(RevitCommandCatalogue.Mirror.Select(spec => spec.Name).SequenceEqual(new[] { "query.snapshot" }), "mirror catalogue part lists query.snapshot only");
        Check(RevitCommandCatalogue.All.SequenceEqual(RevitCommandCatalogue.V1.Concat(RevitCommandCatalogue.V2).Concat(RevitCommandCatalogue.Views).Concat(RevitCommandCatalogue.Mirror)), "All is v1, v2, views, then the snapshot query");
        var spec = RevitCommandCatalogue.Find("query.snapshot");
        Check(spec is { Writes: false, ToolName: "revit_query_snapshot" }, "query.snapshot is read-only with tool revit_query_snapshot");
        Check(spec is not null && spec.Description.EndsWith('.') && spec.Description.Contains("Read-only", StringComparison.Ordinal) && spec.Description.Contains("unmapped", StringComparison.Ordinal), "query.snapshot description states read-only and the unmapped section", spec?.Description ?? "");
        var copies = new[] { @"C:\Repo\.bim\native-runs" };
        Check(PermissionPolicy.RequiredLevel("query.snapshot", FixturePath, copies) == PermissionLevel.Read && PermissionPolicy.RequiredLevel("query.snapshot", RealPath, copies) == PermissionLevel.Read, "query.snapshot requires read on a copy and on a real project");
        JsonObject Envelope(JsonNode args) => new() { ["protocol_version"] = 1, ["operation_id"] = "op-snap", ["document"] = new JsonObject { ["path"] = RealPath, ["unique_id"] = Doc }, ["command"] = "query.snapshot", ["args"] = args };
        try { Check(RevitCommandProtocol.Parse(Envelope(new JsonObject()).ToJsonString()).Args is EmptyQueryArgs, "query.snapshot with {} binds empty arguments"); }
        catch (RevitCommandException error) { Check(false, "query.snapshot with {} binds empty arguments", error.Code + " " + error.Field); }
        try { RevitCommandProtocol.Parse(Envelope(new JsonObject { ["x"] = 1 }).ToJsonString()); Check(false, "query.snapshot with an argument is refused", "accepted"); }
        catch (RevitCommandException error) { Check(error.Code == RevitCommandCodes.ArgsInvalid && error.Field == "args.x", "query.snapshot with an argument is refused", error.Code + " " + error.Field); }
    }

    static void Identity()
    {
        var expected = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("bim-agentic-os/mirror/v1\n" + Doc + "\n" + W1)).Take(16).ToArray());
        Check(MirrorSnapshotMapper.Id(Doc, W1) == expected, "id is the first 16 bytes of SHA-256 over the versioned document and element UniqueIds");
        Check(MirrorSnapshotMapper.Id(Doc.ToUpperInvariant(), W1.ToUpperInvariant()) == expected, "id ignores UniqueId letter case");
        Check(MirrorSnapshotMapper.Id(OtherDoc, W1) != expected && MirrorSnapshotMapper.Id(Doc, W2) != expected, "id differs per document and per element");
        var first = Map(Sample());
        var second = Map(Sample().Reverse());
        Check(first.Elements.Select(e => e.Id).SequenceEqual(second.Elements.Select(e => e.Id)), "ids are the same across calls and input orderings");
        Check(ById(first, W1) is not null && first.Sources[Id(W1)] == W1, "sources map each element id back to its Revit UniqueId");
        Check(Map(Sample(), doc: OtherDoc).Elements.All(e => !first.Elements.Any(f => f.Id == e.Id)), "another document gives other ids");
        Check(first.Elements.All(e => e.Provenance == new Provenance("revit-snapshot", "observed")), "every element has revit-snapshot / observed provenance");
    }

    static void Walls()
    {
        var snapshot = Map(Sample());
        var wall = ById(snapshot, W1);
        Check(wall is { Kind: "wall" } && wall.GeometryMetres.SequenceEqual(new[] { 0, 0, 5, 0, 3, 0.2 }), "straight wall maps to [ax,ay,bx,by,height,thickness] in metres", G(wall));
        Check(wall is not null && wall.Dependencies.SequenceEqual(new[] { Id(L1) }), "wall depends on its level");
        var level = ById(snapshot, L2);
        Check(level is { Kind: "level" } && level.GeometryMetres.SequenceEqual(new[] { 3.0 }) && level.Dependencies.Length == 0, "level maps to [elevation] in metres with no dependencies", G(level));
        var odd = Map(new[] { new MirrorObservedElement(MirrorCategories.Wall, W1) { LevelUid = L1, Start = P(12.5, -7.25), End = P(1234.5, 0.1), HeightMm = 2750.5, ThicknessMm = 123.4 } });
        Check(ById(odd, W1)?.GeometryMetres.SequenceEqual(new[] { Length.FromMillimetres(12.5).Metres, Length.FromMillimetres(-7.25).Metres, Length.FromMillimetres(1234.5).Metres, Length.FromMillimetres(0.1).Metres, Length.FromMillimetres(2750.5).Metres, Length.FromMillimetres(123.4).Metres }) == true, "millimetres convert to metres through Length.FromMillimetres", G(ById(odd, W1)));
        var noise = Map(new[] { new MirrorObservedElement(MirrorCategories.Wall, W1) { LevelUid = L1, Start = P(0.0000001, 0), End = P(4999.9999999, 0), HeightMm = 3000, ThicknessMm = 200 } });
        Check(ById(noise, W1)?.GeometryMetres.SequenceEqual(new[] { 0, 0, 5, 0, 3, 0.2 }) == true, "sub-micrometre conversion noise is normalized like the readback", G(ById(noise, W1)));
        Check(Compiler.Validate(new Project(Guid.NewGuid(), 0, snapshot.Elements.ToArray())).Length == 0, "the mapped elements form a valid BIM-IR project (no duplicate, dangling dependency or bad wall)", JsonSerializer.Serialize(Compiler.Validate(new Project(Guid.NewGuid(), 0, snapshot.Elements.ToArray()))));
    }

    static void Openings()
    {
        var snapshot = Map(Sample());
        var door = ById(snapshot, D1);
        Check(door is { Kind: "door" } && door.GeometryMetres.SequenceEqual(new[] { 0.55, 0, 1.45, 0, 0, 2.1 }), "door maps to its ends on the host axis, sill 0 and height", G(door));
        Check(door is not null && door.Dependencies.SequenceEqual(new[] { Id(L1), Id(W1) }), "door depends on its level then its host wall id");
        var window = ById(snapshot, N1);
        Check(window is { Kind: "window" } && window.GeometryMetres.SequenceEqual(new[] { 5, 1.4, 5, 2.6, 0.9, 1.2 }), "window maps to ends along the host direction, sill and height", G(window));
        Check(window is not null && window.Dependencies.SequenceEqual(new[] { Id(L1), Id(W2) }), "window depends on its level then its host wall id");
        var reversed = Map(new[] { Wall(W1, 5000, 0, 0, 0), Door(D1, W1, 1000, 30) });
        Check(ById(reversed, D1)?.GeometryMetres.SequenceEqual(new[] { 1.45, 0, 0.55, 0, 0, 2.1 }) == true, "opening ends follow the host wall direction and an off-axis point is projected onto the axis", G(ById(reversed, D1)));
        var outside = Map(new[] { Wall(W1, 0, 0, 5000, 0), Door(D1, W1, 4800, 0) });
        Check(Missing(outside, D1)?.Reason == MirrorReasons.OpeningOutsideHost, "an opening that does not fit its host axis is unmapped opening_outside_host", Missing(outside, D1)?.Reason ?? "mapped");
        var orphan = Map(new[] { Wall(W1, 0, 0, 5000, 0), Door(D1, null, 1000, 0), Door(D1.Replace("2001", "2002"), W2, 1000, 0) });
        Check(Missing(orphan, D1)?.Reason == MirrorReasons.HostUnknown && Missing(orphan, D1.Replace("2001", "2002"))?.Reason == MirrorReasons.HostUnknown, "a door without host, or with a host not in the snapshot, is unmapped host_unknown");
        Check(Missing(Map(new[] { Wall(W3, 0, 0, 1, 1) with { Curved = true, Start = null, End = null }, Window(N1, W3, 0, 0) }), N1)?.Reason == MirrorReasons.HostUnknown, "a window hosted by an unmapped (curved) wall is unmapped host_unknown");
        var noSill = Map(new[] { Wall(W1, 0, 0, 5000, 0), Window(N1, W1, 1000, 0) with { SillMm = null } });
        Check(Missing(noSill, N1)?.Reason == MirrorReasons.MissingData, "a window without a sill height is unmapped missing_data");
        var noWidth = Map(new[] { Wall(W1, 0, 0, 5000, 0), Door(D1, W1, 1000, 0) with { WidthMm = null } });
        Check(Missing(noWidth, D1)?.Reason == MirrorReasons.MissingData, "a door without width is unmapped missing_data");
    }

    static void Rooms()
    {
        var snapshot = Map(Sample());
        var room = ById(snapshot, R1);
        Check(room is { Kind: "space" } && room.GeometryMetres.SequenceEqual(new[] { 0.1, 0.1, 4.8, 3.8 }), "rectangular room maps to space [corner x, corner y, width, depth] (collinear vertices merged)", G(room));
        Check(room is not null && room.Dependencies.SequenceEqual(new[] { Id(L1) }.Concat(new[] { Id(W1), Id(W2) }.Order())), "space depends on its level then its mapped bounding walls in id order");
        var lShape = Map(new[] { Room(R1, new[] { P(0, 0), P(4000, 0), P(4000, 2000), P(2000, 2000), P(2000, 4000), P(0, 4000) }) });
        Check(Missing(lShape, R1)?.Reason == MirrorReasons.RoomNotRectangular, "an L-shaped room is unmapped room_not_rectangular");
        var skew = Map(new[] { Room(R1, new[] { P(0, 0), P(4000, 100), P(4000, 3000), P(0, 3000) }) });
        Check(Missing(skew, R1)?.Reason == MirrorReasons.RoomNotRectangular, "a non axis-aligned room is unmapped room_not_rectangular");
        var curved = Map(new[] { Room(R1, Rectangle(), curves: true) });
        Check(Missing(curved, R1)?.Reason == MirrorReasons.RoomNotRectangular, "a room with a curved boundary is unmapped room_not_rectangular");
        var unplaced = Map(new[] { new MirrorObservedElement(MirrorCategories.Room, R1) { LevelUid = L1 } });
        Check(Missing(unplaced, R1)?.Reason == MirrorReasons.RoomUnplaced, "a room without boundary is unmapped room_unplaced");
        var twoLoops = Map(new[] { new MirrorObservedElement(MirrorCategories.Room, R1) { LevelUid = L1, Loops = new[] { new MirrorLoop(Rectangle(), false, new string[0]), new MirrorLoop(new[] { P(1000, 1000), P(1200, 1000), P(1200, 1200), P(1000, 1200) }, false, new string[0]) } } });
        Check(Missing(twoLoops, R1)?.Reason == MirrorReasons.RoomNotRectangular, "a room with an inner loop is unmapped room_not_rectangular");
        var noWalls = Map(new[] { Room(R1, Rectangle(), bounding: new[] { W3, "" }) });
        Check(ById(noWalls, R1)?.Dependencies.SequenceEqual(new[] { Id(L1) }) == true, "bounding elements not mapped as walls are not dependencies");
    }

    static void Unmapped()
    {
        var snapshot = Map(Sample());
        Check(Missing(snapshot, F1) is { Category: "floor", Reason: MirrorReasons.NoBimIrKind }, "floors are unmapped no_bim_ir_kind (BIM-IR has no floor kind)");
        Check(Missing(snapshot, W3) is { Category: "wall", Reason: MirrorReasons.CurvedWall }, "curved walls are unmapped curved_wall_unsupported");
        Check(snapshot.Unmapped.All(u => u.Detail.Length > 10), "every unmapped entry carries a plain-English detail");
        Check(ById(snapshot, F1) is null && ById(snapshot, W3) is null, "unmapped elements never appear among BIM-IR elements");
        var bad = Map(new[]
        {
            new MirrorObservedElement("column", "60f91daf-3dd7-4283-a86d-24137b73f3da-00009001"),
            Wall(W1, 0, 0, 5000, 0) with { HeightMm = null },
            Wall(W2, 0, 0, 0, 0),
            Wall(W3, 0, 0, 100, 0, level: "60f91daf-3dd7-4283-a86d-24137b73f3da-0000ffff"),
            Wall("60f91daf-3dd7-4283-a86d-24137b73f3da-00001004", 0, 0, 100, 0) with { ThicknessMm = double.NaN },
            Wall("", 0, 0, 100, 0),
            Wall("60f91daf-3dd7-4283-a86d-24137b73f3da-00001005", 0, 0, 100, 0, level: null),
        });
        Check(Missing(bad, "60f91daf-3dd7-4283-a86d-24137b73f3da-00009001")?.Reason == MirrorReasons.CategoryUnsupported, "an unknown category is unmapped category_unsupported");
        Check(Missing(bad, W1)?.Reason == MirrorReasons.MissingData, "a wall without height is unmapped missing_data");
        Check(Missing(bad, W2)?.Reason == MirrorReasons.InvalidGeometry, "a zero-length wall is unmapped invalid_geometry");
        Check(Missing(bad, W3)?.Reason == MirrorReasons.LevelUnknown, "a wall on a level not in the snapshot is unmapped level_unknown");
        Check(Missing(bad, "60f91daf-3dd7-4283-a86d-24137b73f3da-00001004")?.Reason == MirrorReasons.InvalidGeometry, "a non-finite thickness is unmapped invalid_geometry");
        Check(Missing(bad, "")?.Reason == MirrorReasons.UniqueIdMissing, "an element without UniqueId is unmapped unique_id_missing");
        Check(Missing(bad, "60f91daf-3dd7-4283-a86d-24137b73f3da-00001005")?.Reason == MirrorReasons.LevelUnknown, "a wall without level is unmapped level_unknown");
        Check(bad.Elements.Count == 2 && bad.Elements.All(e => e.Kind == "level"), "only the levels survive a batch of bad elements");
        var dup = Map(new[] { Wall(W1, 0, 0, 5000, 0), Wall(W1.ToUpperInvariant(), 0, 0, 4000, 0) });
        Check(dup.Unmapped.Count(u => u.Reason == MirrorReasons.DuplicateUniqueId) == 2 && ById(dup, W1) is null, "UniqueIds repeated (ignoring case) are all unmapped duplicate_unique_id");
        var noElevation = Map(Array.Empty<MirrorObservedElement>(), new[] { new MirrorObservedLevel(L1, "Level 1", null) });
        Check(Missing(noElevation, L1) is { Category: "level", Reason: MirrorReasons.MissingData } && noElevation.Elements.Count == 0, "a level without elevation is unmapped missing_data");
        Check(Missing(Map(new[] { Wall(W1, 0, 0, 5000, 0) }, new[] { new MirrorObservedLevel(L1, "Level 1", null) }), W1)?.Reason == MirrorReasons.LevelUnknown, "a wall on an unmapped level is unmapped level_unknown");
        string? refusal = null;
        try { Map(Sample(), doc: " "); } catch (ArgumentException error) { refusal = error.Message; }
        Check(refusal == "document_unique_id_required", "a blank document UniqueId is refused", refusal ?? "accepted");
    }

    static void Determinism()
    {
        var input = Sample();
        var json = Map(input).ToJson();
        var shuffled = input.Skip(3).Concat(input.Take(3)).Reverse().ToArray();
        Check(Map(shuffled, Levels().Reverse()).ToJson() == json, "snapshot JSON is byte-identical for any input order");
        Check(Map(input).ToJson() == json, "snapshot JSON is byte-identical across calls");
        var root = JsonNode.Parse(json)!.AsObject();
        Check(string.Join(",", root.Select(p => p.Key)) == "snapshot_version,document_unique_id,units,elements,sources,unmapped", "snapshot members are snapshot_version, document_unique_id, units, elements, sources, unmapped", string.Join(",", root.Select(p => p.Key)));
        Check(root["snapshot_version"]!.GetValue<int>() == MirrorSnapshotMapper.Version && MirrorSnapshotMapper.Version == 1 && root["units"]!.GetValue<string>() == "m" && root["document_unique_id"]!.GetValue<string>() == Doc, "snapshot header: version 1, metres, document UniqueId");
        var elements = JsonSerializer.Deserialize<Element[]>(root["elements"]!.ToJsonString())!;
        var mapped = Map(input).Elements;
        Check(elements.Length == mapped.Count && elements.Zip(mapped).All(pair => JsonSerializer.Serialize(pair.First) == JsonSerializer.Serialize(pair.Second)), "elements are serialized exactly as BIM-IR Element records (round trip)");
        Check(root["elements"]!.AsArray().Select(e => e!["Kind"]!.GetValue<string>()).SequenceEqual(new[] { "level", "level", "wall", "wall", "door", "window", "space" }), "elements are ordered level, wall, door, window, space");
        var walls = mapped.Where(e => e.Kind == "wall").Select(e => Map(input).Sources[e.Id]).ToArray();
        Check(walls.SequenceEqual(walls.Order(StringComparer.Ordinal)), "elements of a kind are ordered by UniqueId");
        var sources = root["sources"]!.AsArray();
        Check(sources.Count == mapped.Count && sources.Select(s => s!["id"]!.GetValue<string>()).SequenceEqual(mapped.Select(e => e.Id.ToString("D"))) && sources.All(s => s!["unique_id"] is not null && s["category"] is not null), "sources list id, unique_id and category in element order");
        var unmapped = root["unmapped"]!.AsArray();
        Check(unmapped.Select(u => u!["unique_id"]!.GetValue<string>()).SequenceEqual(new[] { F1, W3 }) && unmapped.All(u => u!.AsObject().Select(p => p.Key).SequenceEqual(new[] { "category", "unique_id", "reason", "detail" })), "unmapped entries are category, unique_id, reason, detail ordered by category then UniqueId", unmapped.ToJsonString());
    }

    static void EmptyModel()
    {
        var empty = MirrorSnapshotMapper.Map(new MirrorObservation(Doc, Array.Empty<MirrorObservedLevel>(), Array.Empty<MirrorObservedElement>()));
        Check(empty.Elements.Count == 0 && empty.Unmapped.Count == 0 && empty.Sources.Count == 0, "an empty model maps to no elements and nothing unmapped");
        Check(empty.ToJson() == "{\"snapshot_version\":1,\"document_unique_id\":\"" + Doc + "\",\"units\":\"m\",\"elements\":[],\"sources\":[],\"unmapped\":[]}", "an empty model has the exact empty snapshot JSON", empty.ToJson());
    }
}
