using Bim.Core;
using System.Globalization;
using System.Text.Json;
// P3-01 oracle: layout intent AST (levels, wall types, rooms, shared walls, openings) compiled deterministically to the
// existing BIM-IR records, typed validation issues, graph edge typing, minimal Compiler.Diff and canonical JSON.
int passed = 0, failed = 0;
var provenance = new Provenance("intent-tests", "unit");
var projectId = Guid.Parse("3d0c8a5e-6f1b-4c2d-9e7a-1b2c3d4e5f60");
var inv = CultureInfo.InvariantCulture;

void Check(bool condition, string name, string detail = "")
{
    if (condition) { Console.WriteLine("PASS " + name); passed++; return; }
    Console.WriteLine("::error::intent FAIL " + name + (detail.Length > 0 ? " | " + detail : ""));
    failed++;
}
string? Code(Action action)
{
    try { action(); return null; }
    catch (IntentException error) { return error.Code; }
    catch (ArgumentException error) { return "argument:" + error.Message; }
    catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
}
IntentLevel Lv(string key, double elevation) => new(key, key.ToUpperInvariant(), elevation);
IntentWallType Ty(string key, double thickness, double height) => new(key, "Type " + key, thickness, height);
IntentRoom Rm(string key, double x, double y, double width, double depth, string level = "l1", string type = "std") => new(key, "Room " + key, level, type, x, y, width, depth);
IntentOpening Op(string key, IntentOpeningKind kind, IntentHost host, double offset, double width, double height, double sill) => new(key, kind, host, offset, width, height, sill);
IntentRoomSide Side(string room, IntentSide side) => new(room, side);
IntentSharedWall Between(string a, string b) => new(a, b);
var defaultLevels = new[] { Lv("l1", 0), Lv("l2", 3) };
var defaultTypes = new[] { Ty("std", 0.2, 2.8), Ty("ext", 0.3, 3.0) };
LayoutIntent Layout(IntentRoom[] rooms, IntentOpening[]? openings = null, IntentLevel[]? levels = null, IntentWallType[]? types = null) =>
    new(LayoutIntent.CurrentVersion, provenance, levels ?? defaultLevels, types ?? defaultTypes, rooms, openings ?? Array.Empty<IntentOpening>());
Guid Id(string key) => IntentCompiler.Id(projectId, key);
Element? Find(Project project, string key) => project.Elements.FirstOrDefault(element => element.Id == Id(key));
bool Near(double[] actual, params double[] expected) => actual.Length == expected.Length && actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) <= 1e-9);
string Show(double[]? values) => values is null ? "missing" : "[" + string.Join(",", values.Select(value => value.ToString("R", inv))) + "]";
bool Geometry(Project project, string key, params double[] expected) => Find(project, key) is { } element && Near(element.GeometryMetres, expected);
string GeometryOf(Project project, string key) => Show(Find(project, key)?.GeometryMetres);
bool Has(LayoutIntent intent, string code, string? key = null) => IntentCompiler.Validate(intent).Any(issue => issue.Code == code && (key is null || issue.Key == key));
string ShowIssues(LayoutIntent intent) => string.Join(";", IntentCompiler.Validate(intent).Select(issue => issue.Code + "@" + issue.Key));
string N(int value) => value.ToString(inv);

// Level l1: a (4x3) and b (3x3) side by side, c (4x2.5) north of a. Level l2: u alone, same footprint as a.
// Axis lines: x=4 carries a|b (y 0..3) and c's exterior east (3..5.5); y=3 carries a|c (x 0..4) and b's north (4..7).
var baseRooms = new[] { Rm("a", 0, 0, 4, 3), Rm("b", 4, 0, 3, 3), Rm("c", 0, 3, 4, 2.5), Rm("u", 0, 0, 4, 3, "l2") };
var baseOpenings = new[]
{
    Op("d1", IntentOpeningKind.Door, Between("b", "a"), 1, 0.9, 2.1, 0),
    Op("d2", IntentOpeningKind.Door, Side("c", IntentSide.South), 1.5, 0.9, 2.1, 0),
    Op("w1", IntentOpeningKind.Window, Side("b", IntentSide.South), 0.5, 1.2, 1.2, 0.9),
    Op("w2", IntentOpeningKind.Window, Side("b", IntentSide.East), 1, 1, 1.2, 0.9),
    Op("w3", IntentOpeningKind.Window, Side("a", IntentSide.West), 1, 1, 1.2, 0.9),
};
var baseLayout = Layout(baseRooms, baseOpenings);

try
{
    // ---- Identity scheme ----
    Check(IntentCompiler.Id(projectId, "space:a") == RoomCompiler.Id(projectId, "space:a"), "ids are RoomCompiler.Id(project, intent key): SHA-256 of a stable key");
    Check(IntentCompiler.LevelKey("l1") == "level:l1" && IntentCompiler.SpaceKey("a") == "space:a" && IntentCompiler.OpeningKey("d1") == "opening:d1",
        "level, space and opening intent keys");
    Check(IntentCompiler.Id(projectId, "space:a") == Guid.Parse("254784a6-edd3-3254-203a-b0172d136dd3"), "golden id pins SHA-256(\"<project:D>:<key>\")[0..16] as Guid bytes");
    Check(IntentCompiler.WallKey("a", IntentSide.South) == "wall:a:south" && IntentCompiler.WallKey("a", IntentSide.East, "b") == "wall:a:east>b",
        "unshared stretch keys are room and side, plus the neighbour whose shared stretch precedes them", IntentCompiler.WallKey("a", IntentSide.East, "b"));
    Check(IntentCompiler.SharedWallKey("b", "a") == "wall:a|b" && IntentCompiler.SharedWallKey("a", "b") == "wall:a|b", "shared wall key is the ordinal room pair", IntentCompiler.SharedWallKey("b", "a"));

    // ---- Valid multi-room compilation ----
    Check(IntentCompiler.Validate(baseLayout).Length == 0, "base layout validates", ShowIssues(baseLayout));
    var project = IntentCompiler.Compile(projectId, 5, baseLayout);
    Check(project.Id == projectId && project.Revision == 5, "project id and revision are the caller's");
    Check(Compiler.Validate(project).Length == 0, "compiled project passes Compiler.Validate", string.Join(",", Compiler.Validate(project).Select(issue => issue.Code)));
    int Kind(Project p, string kind) => p.Elements.Count(element => element.Kind == kind);
    Check(Kind(project, "level") == 2 && Kind(project, "wall") == 14 && Kind(project, "space") == 4 && Kind(project, "door") == 2 && Kind(project, "window") == 3 && project.Elements.Length == 25,
        "2 levels, 14 walls (10 on l1 incl. 2 shared, 4 on l2), 4 spaces, 2 doors, 3 windows",
        string.Join(",", project.Elements.GroupBy(element => element.Kind).Select(group => group.Key + "=" + N(group.Count()))));
    var expectedKeys = new[]
    {
        "level:l1", "level:l2",
        "wall:a:south", "wall:a:west", "wall:a|b", "wall:a|c", "wall:b:south", "wall:b:east", "wall:b:north", "wall:c:west", "wall:c:north", "wall:c:east",
        "wall:u:south", "wall:u:east", "wall:u:north", "wall:u:west",
        "space:a", "space:b", "space:c", "space:u", "opening:d1", "opening:d2", "opening:w1", "opening:w2", "opening:w3",
    };
    Check(project.Elements.Select(element => element.Id).ToHashSet().SetEquals(expectedKeys.Select(Id)), "element ids are exactly the expected intent keys");
    Check(Find(project, "wall:a:east") is null && Find(project, "wall:b:west") is null && Find(project, "wall:a:north") is null && Find(project, "wall:c:south") is null,
        "fully shared sides emit no per-room wall");
    Check(project.Elements.All(element => element.Provenance == provenance), "every element carries the intent provenance");

    // ---- Shared walls are single elements ----
    Check(Geometry(project, "wall:a|b", 4, 0, 4, 3, 2.8, 0.2), "shared wall a|b is one axis wall [4,0]->[4,3]", GeometryOf(project, "wall:a|b"));
    Check(Geometry(project, "wall:a|c", 0, 3, 4, 3, 2.8, 0.2), "shared wall a|c is one axis wall [0,3]->[4,3]", GeometryOf(project, "wall:a|c"));
    var walls = project.Elements.Where(element => element.Kind == "wall").ToArray();
    bool CollinearOverlap(double[] p, double[] q)
    {
        var pv = p[0] == p[2]; var qv = q[0] == q[2];
        if (pv != qv) return false;
        if (pv ? p[0] != q[0] : p[1] != q[1]) return false;
        double pl = pv ? Math.Min(p[1], p[3]) : Math.Min(p[0], p[2]), ph = pv ? Math.Max(p[1], p[3]) : Math.Max(p[0], p[2]);
        double ql = qv ? Math.Min(q[1], q[3]) : Math.Min(q[0], q[2]), qh = qv ? Math.Max(q[1], q[3]) : Math.Max(q[0], q[2]);
        return Math.Min(ph, qh) - Math.Max(pl, ql) > 1e-9;
    }
    var level1Walls = walls.Where(wall => wall.Dependencies.SequenceEqual(new[] { Id("level:l1") })).ToArray();
    Check(level1Walls.Length == 10 && !level1Walls.SelectMany((p, i) => level1Walls.Skip(i + 1).Select(q => (p, q))).Any(pair => CollinearOverlap(pair.p.GeometryMetres, pair.q.GeometryMetres)),
        "no two walls on a level overlap collinearly");

    // ---- Geometry convention [ax,ay,bx,by,height,thickness]; exterior walls run counter-clockwise ----
    Check(Geometry(project, "wall:a:south", 0, 0, 4, 0, 2.8, 0.2), "a south runs west to east", GeometryOf(project, "wall:a:south"));
    Check(Geometry(project, "wall:a:west", 0, 3, 0, 0, 2.8, 0.2), "a west runs north to south", GeometryOf(project, "wall:a:west"));
    Check(Geometry(project, "wall:b:east", 7, 0, 7, 3, 2.8, 0.2), "b east runs south to north", GeometryOf(project, "wall:b:east"));
    Check(Geometry(project, "wall:b:north", 7, 3, 4, 3, 2.8, 0.2), "b north runs east to west and only over b", GeometryOf(project, "wall:b:north"));
    Check(Geometry(project, "wall:c:east", 4, 3, 4, 5.5, 2.8, 0.2), "c east is the unshared rest of axis x=4", GeometryOf(project, "wall:c:east"));
    Check(Geometry(project, "level:l1", 0) && Geometry(project, "level:l2", 3), "levels carry elevation", GeometryOf(project, "level:l2"));
    Check(Geometry(project, "space:a", 0.1, 0.1, 3.8, 2.8) && Geometry(project, "space:c", 0.1, 3.1, 3.8, 2.3),
        "space is interior corner + clear size [x,y,width,depth]", GeometryOf(project, "space:a") + " " + GeometryOf(project, "space:c"));
    Check(Geometry(project, "space:u", 0.1, 0.1, 3.8, 2.8), "same footprint on another level is independent", GeometryOf(project, "space:u"));
    Check(Geometry(project, "opening:d1", 4, 1, 4, 1.9, 0, 2.1), "door on shared wall: [ax,ay,bx,by,sill,height] along host", GeometryOf(project, "opening:d1"));
    Check(Geometry(project, "opening:w1", 4.5, 0, 5.7, 0, 0.9, 1.2), "window offset from the west end of b south", GeometryOf(project, "opening:w1"));
    Check(Geometry(project, "opening:w2", 7, 1, 7, 2, 0.9, 1.2), "window offset from the south end of b east", GeometryOf(project, "opening:w2"));
    Check(Geometry(project, "opening:w3", 0, 2, 0, 1, 0.9, 1.2), "opening follows its host direction (a west runs north to south)", GeometryOf(project, "opening:w3"));
    Check(Geometry(project, "opening:d2", 1.5, 3, 2.4, 3, 0, 2.1), "door on a room side that is shared resolves onto the shared wall", GeometryOf(project, "opening:d2"));

    // ---- Dependencies ----
    Guid[] Deps(string key) => Find(project, key)?.Dependencies ?? Array.Empty<Guid>();
    Check(walls.All(wall => wall.Dependencies.Length == 1), "every wall depends on exactly its level");
    Check(Deps("wall:u:south").SequenceEqual(new[] { Id("level:l2") }), "l2 walls depend on l2");
    Check(Deps("space:a").SequenceEqual(new[] { Id("level:l1"), Id("wall:a:south"), Id("wall:a:west"), Id("wall:a|b"), Id("wall:a|c") }),
        "space depends on its level then its bounding walls in key order", string.Join(",", Deps("space:a")));
    Check(Deps("space:b").ToHashSet().SetEquals(new[] { Id("level:l1"), Id("wall:b:south"), Id("wall:b:east"), Id("wall:b:north"), Id("wall:a|b") }), "space b bounded by its walls and the shared wall");
    Check(Deps("opening:d1").SequenceEqual(new[] { Id("level:l1"), Id("wall:a|b") }) && Deps("opening:w2").SequenceEqual(new[] { Id("level:l1"), Id("wall:b:east") }),
        "openings depend on their level and host wall");
    var onLevel1 = project.Elements.Count(element => element.Dependencies.Contains(Id("level:l1")));
    Check(onLevel1 == 18, "everything on l1 depends on it (10 walls, 3 spaces, 5 openings)", N(onLevel1));

    // ---- Semantic graph typing ----
    var graph = SemanticGraph.Build(project);
    int Edges(GraphEdgeKind kind) => graph.Edges.Count(edge => edge.Kind == kind);
    Check(Edges(GraphEdgeKind.HostedBy) == 5 && Edges(GraphEdgeKind.Bounds) == 16 && Edges(GraphEdgeKind.OnLevel) == 23 && Edges(GraphEdgeKind.DependsOn) == 0,
        "graph edges: 5 hosted-by, 16 bounds, 23 on-level, no generic",
        string.Join(",", Enum.GetValues<GraphEdgeKind>().Select(kind => kind + "=" + N(Edges(kind)))));
    Check(graph.HostOf(Id("opening:d1")) == Id("wall:a|b") && graph.HostOf(Id("opening:d2")) == Id("wall:a|c") && graph.HostOf(Id("opening:w3")) == Id("wall:a:west"), "HostOf resolves each opening's wall");
    Check(graph.Hosts(Id("wall:b:south")).SequenceEqual(new[] { Id("opening:w1") }), "wall Hosts lists its openings");
    Check(graph.Neighbours(Id("wall:a|b"), GraphDirection.Dependents, GraphEdgeKind.Bounds).ToHashSet().SetEquals(new[] { Id("space:a"), Id("space:b") }), "shared wall bounds both spaces");
    Check(graph.Neighbours(Id("space:a"), GraphDirection.Prerequisites, GraphEdgeKind.Bounds).Count == 4, "space a is bounded by four walls");
    Check(graph.LevelOf(Id("space:u")).SequenceEqual(new[] { Id("level:l2") }) && graph.LevelOf(Id("opening:w1")).SequenceEqual(new[] { Id("level:l1") }), "LevelOf resolves spaces and openings");
    Check(graph.OnLevel(Id("level:l2")).Count == 5, "l2 has u's 4 walls and space", N(graph.OnLevel(Id("level:l2")).Count));

    // ---- Determinism: order and names never matter ----
    var hash = Compiler.Hash(project);
    var reversed = new LayoutIntent(1, provenance, defaultLevels.Reverse().ToArray(), defaultTypes.Reverse().ToArray(), baseRooms.Reverse().ToArray(), baseOpenings.Reverse().ToArray());
    Check(Compiler.Hash(IntentCompiler.Compile(projectId, 5, reversed)) == hash, "reversed input order gives byte-identical hash");
    T[] Rotate<T>(IEnumerable<T> items) { var list = items.ToArray(); return list.Skip(1).Concat(list.Take(1)).ToArray(); }
    var shuffled = new LayoutIntent(1, provenance, Rotate(defaultLevels), Rotate(defaultTypes), Rotate(baseRooms), Rotate(Rotate(baseOpenings)));
    Check(!shuffled.Rooms.SequenceEqual(baseRooms) && !shuffled.Levels.SequenceEqual(defaultLevels) && !shuffled.WallTypes.SequenceEqual(defaultTypes) && !shuffled.Openings.SequenceEqual(baseOpenings),
        "the shuffle permutes every collection");
    Check(JsonSerializer.Serialize(IntentCompiler.Compile(projectId, 5, shuffled)) == JsonSerializer.Serialize(project), "shuffled input gives byte-identical project JSON");
    var renamed = new LayoutIntent(1, provenance, defaultLevels.Select(level => level with { Name = "Renamed " + level.Key }).ToArray(),
        defaultTypes.Select(type => type with { Name = "Renamed " + type.Key }).ToArray(), baseRooms.Select(room => room with { Name = "Renamed " + room.Key }).ToArray(), baseOpenings);
    Check(Compiler.Hash(IntentCompiler.Compile(projectId, 5, renamed)) == hash, "renaming levels, types and rooms changes nothing compiled");
    Check(IntentCompiler.Compile(Guid.Parse("00000000-0000-0000-0000-0000000000aa"), 5, baseLayout).Elements.Select(element => element.Id).Intersect(project.Elements.Select(element => element.Id)).Any() == false,
        "ids are scoped by project id");

    // ---- Minimal diff: widening b touches only b's moved walls, space and the opening on the moved wall ----
    var wider = Layout(baseRooms.Select(room => room.Key == "b" ? room with { WidthMetres = 3.5 } : room).ToArray(), baseOpenings);
    var after = IntentCompiler.Compile(projectId, 5, wider);
    var plan = Compiler.Diff(project, after);
    var updated = plan.Operations.Select(operation => operation.Element.Id).ToHashSet();
    var expectedUpdates = new[] { "wall:b:south", "wall:b:east", "wall:b:north", "space:b", "opening:w2" }.Select(Id).ToHashSet();
    Check(plan.Operations.All(operation => operation.Action == "update"), "width change creates and deletes nothing", string.Join(",", plan.Operations.Select(operation => operation.Action)));
    Check(updated.SetEquals(expectedUpdates), "width change updates exactly b south/east/north, space b and window w2",
        string.Join(",", expectedKeys.Where(key => updated.Contains(Id(key)))));
    Check(after.Elements.Select(element => element.Id).SequenceEqual(project.Elements.Select(element => element.Id)), "every element keeps its identity and position");
    Check(Geometry(after, "wall:b:east", 7.5, 0, 7.5, 3, 2.8, 0.2) && Geometry(after, "space:b", 4.1, 0.1, 3.3, 2.8) && Geometry(after, "opening:w1", 4.5, 0, 5.7, 0, 0.9, 1.2),
        "moved wall and space follow; window measured from the fixed west end stays", GeometryOf(after, "space:b"));
    Check(!plan.DirtyIds.Contains(Id("wall:a|b")) && !plan.DirtyIds.Contains(Id("space:a")) && !plan.DirtyIds.Contains(Id("space:c")) && !plan.DirtyIds.Contains(Id("level:l1")),
        "unaffected rooms and the level stay clean in the dirty closure");
    Check(plan.DirtyIds.Contains(Id("opening:w1")), "an opening on an updated wall is dirty (closure) though not updated");
    var semantic = SemanticDiff.Compute(project, after, plan);
    var spaceChange = semantic.Elements.Single(change => change.Id == Id("space:b"));
    Check(spaceChange.Changes.Length == 1 && spaceChange.Changes[0].Field == "width" && Math.Abs((spaceChange.Changes[0].DeltaMetres ?? 0) - 0.5) < 1e-9,
        "semantic diff explains space b as width +0.5 m", string.Join(",", spaceChange.Changes.Select(change => change.Field + change.Describe())));

    // ---- Partial adjacency splits a side into segments ----
    var notched = Layout(new[] { Rm("a", 0, 0, 4, 3), Rm("b", 4, 1, 3, 1) }, new[]
    {
        Op("d1", IntentOpeningKind.Door, Side("a", IntentSide.East), 1.15, 0.7, 2.1, 0),
        Op("w1", IntentOpeningKind.Window, Side("a", IntentSide.East), 2.2, 0.6, 1, 1),
    });
    Check(IntentCompiler.Validate(notched).Length == 0, "notched layout validates", ShowIssues(notched));
    var notchedProject = IntentCompiler.Compile(projectId, 0, notched);
    Check(Geometry(notchedProject, "wall:a:east", 4, 0, 4, 1, 2.8, 0.2) && Geometry(notchedProject, "wall:a|b", 4, 1, 4, 2, 2.8, 0.2) && Geometry(notchedProject, "wall:a:east>b", 4, 2, 4, 3, 2.8, 0.2),
        "a east splits into a:east, a|b and a:east>b", GeometryOf(notchedProject, "wall:a:east") + GeometryOf(notchedProject, "wall:a|b") + GeometryOf(notchedProject, "wall:a:east>b"));
    Check(Find(notchedProject, "wall:b:west") is null && notchedProject.Elements.Count(element => element.Kind == "wall") == 9, "b west is fully shared; 9 walls in all");
    Check(Find(notchedProject, "opening:d1")?.Dependencies.Last() == Id("wall:a|b") && Find(notchedProject, "opening:w1")?.Dependencies.Last() == Id("wall:a:east>b"),
        "room-side openings resolve to the segment that contains them");
    Check(Has(Layout(new[] { Rm("a", 0, 0, 4, 3), Rm("b", 4, 1, 3, 1) }, new[] { Op("d1", IntentOpeningKind.Door, Side("a", IntentSide.East), 1.05, 0.9, 2.1, 0) }), IntentCodes.OpeningOutsideWall, "d1"),
        "an opening reaching into the joined perpendicular wall's half thickness is outside the clear span");
    // Reverse lookup from element id to intent key over every key the two layouts could produce.
    Plan DiffOf(LayoutIntent source, LayoutIntent target)
    {
        var left = IntentCompiler.Compile(projectId, 0, source); var right = IntentCompiler.Compile(projectId, 0, target);
        var diff = Compiler.Diff(left, right);
        var roomKeys = source.Rooms.Concat(target.Rooms).Select(room => room.Key).Distinct().ToArray();
        var candidates = roomKeys.SelectMany(room => Enum.GetValues<IntentSide>().SelectMany(side => roomKeys.Select(other => IntentCompiler.WallKey(room, side, other)).Append(IntentCompiler.WallKey(room, side)))
                .Concat(roomKeys.Select(other => IntentCompiler.SharedWallKey(room, other))).Append(IntentCompiler.SpaceKey(room)))
            .Concat(source.Levels.Concat(target.Levels).Select(level => IntentCompiler.LevelKey(level.Key)))
            .Concat(source.Openings.Concat(target.Openings).Select(opening => IntentCompiler.OpeningKey(opening.Key)))
            .Distinct().ToDictionary(Id);
        string Name(Guid id) => candidates.TryGetValue(id, out var key) ? key : id.ToString("N");
        string[] Keys(string action) => diff.Operations.Where(operation => operation.Action == action).Select(operation => Name(operation.Element.Id)).Order(StringComparer.Ordinal).ToArray();
        return new Plan(Keys("create"), Keys("update"), Keys("delete"), diff);
    }
    // ---- Stretch identity when a neighbour moves ----
    var notchRooms = new[] { Rm("a", 0, 0, 4, 3), Rm("b", 4, 1, 3, 1) };
    var slid = DiffOf(Layout(notchRooms), Layout(new[] { notchRooms[0], notchRooms[1] with { YMetres = 1.5 } }));
    Check(slid.Created.Length == 0 && slid.Deleted.Length == 0 && slid.Updated.Contains("wall:a:east") && slid.Updated.Contains("wall:a|b") && slid.Updated.Contains("wall:a:east>b"),
        "sliding b keeps every stretch identity (a:east, a|b, a:east>b only move)", slid.Show());
    var cornered = DiffOf(Layout(notchRooms), Layout(new[] { notchRooms[0], notchRooms[1] with { YMetres = 0 } }));
    Check(cornered.Created.Length == 0 && cornered.Deleted.SequenceEqual(new[] { "wall:a:east" }) && cornered.Updated.Contains("wall:a:east>b") && cornered.Updated.Contains("wall:a|b"),
        "when b reaches a's corner the corner stretch is deleted, never reused for another stretch", cornered.Show());
    // ---- A width change that changes which walls are shared ----
    var widenedC = DiffOf(baseLayout, Layout(baseRooms.Select(room => room.Key == "c" ? room with { WidthMetres = 7 } : room).ToArray(), baseOpenings));
    Check(widenedC.Created.SequenceEqual(new[] { "wall:b|c" }) && widenedC.Deleted.SequenceEqual(new[] { "wall:b:north" }),
        "widening c to 7 m creates wall:b|c and deletes wall:b:north", widenedC.Show());
    Check(widenedC.Updated.SequenceEqual(new[] { "space:b", "space:c", "wall:c:east", "wall:c:north" }.Order(StringComparer.Ordinal)),
        "and updates only c's moved walls and the two re-bounded spaces", widenedC.Show());
    // ---- Snapping stays local ----
    var far = DiffOf(baseLayout, Layout(baseRooms.Append(Rm("far", 3.9999995, 100, 4, 3)).ToArray(), baseOpenings));
    Check(far.Updated.Length == 0 && far.Deleted.Length == 0 && far.Created.SequenceEqual(new[] { "space:far", "wall:far:east", "wall:far:north", "wall:far:south", "wall:far:west" }),
        "a far room near-equal to a grid line moves nothing else", far.Show());
    var straddle = Layout(new[] { Rm("a", 0, 0, 4, 3), Rm("b", 4, 1, 3, 1) }, new[] { Op("w1", IntentOpeningKind.Window, Side("a", IntentSide.East), 0.5, 1, 1, 1) });
    Check(Has(straddle, IntentCodes.OpeningOutsideWall, "w1"), "an opening across a segment boundary is outside its wall", ShowIssues(straddle));

    // ---- Floating point: abutting sides within tolerance share one wall ----
    var thin = new[] { Ty("std", 0.01, 2.5) };
    var abut = Layout(new[] { Rm("a", 0.1, 0, 0.2, 1), Rm("b", 0.3, 0, 0.5, 1) }, types: thin);
    Check(IntentCompiler.Validate(abut).Length == 0, "0.1+0.2 abuts 0.3 without clash", ShowIssues(abut));
    var abutProject = IntentCompiler.Compile(projectId, 0, abut);
    Check(Find(abutProject, "wall:a|b") is not null && Find(abutProject, "wall:a:east") is null && Find(abutProject, "wall:b:west") is null, "tolerance-equal axes form one shared wall");

    // ---- Validation: typed issues ----
    Check(Has(Layout(new[] { Rm("a", 0, 0, 4, 3), Rm("x", 3, 1, 2, 1) }), IntentCodes.RoomOverlap, "a"), "overlapping rooms are reported");
    Check(IntentCompiler.Validate(Layout(new[] { Rm("a", 0, 0, 4, 3), Rm("x", 4, 3, 2, 2) })).Length == 0, "rooms touching at a corner are valid");
    Check(Has(Layout(new[] { Rm("a", 0, 0, 4, 3), Rm("x", 4.05, 0, 2, 3) }), IntentCodes.WallClash), "parallel walls closer than their thickness clash");
    Check(Has(Layout(new[] { Rm("a", 0, 0, 4, 3), Rm("b", 4.05, 3, 3, 3) }), IntentCodes.WallClash), "perpendicular wall bodies overlapping off the axes clash (b north-east)");
    Check(Has(Layout(new[] { Rm("a", 0, 0, 4, 3), Rm("b", 4.05, -3, 3, 3) }), IntentCodes.WallClash), "perpendicular wall bodies overlapping off the axes clash (b south-east)");
    Check(IntentCompiler.Validate(Layout(new[] { Rm("a", 0, 0, 4, 3), Rm("b", 4.3, 3, 3, 3) })).Length == 0, "walls whose bodies stay apart are valid");
    Check(IntentCompiler.Validate(Layout(new[] { Rm("a", 0, 0, 4, 3), Rm("b", 1, 3, 2, 2) })).Length == 0, "a T join where b's axes meet a's north axis is valid");
    Check(Has(Layout(new[] { Rm("a", 0, 0, 4, 3), Rm("b", 4, 0, 3, 3, type: "ext") }), IntentCodes.SharedWallConflict, "a"), "a shared stretch with two wall types conflicts");
    Check(Has(Layout(new[] { Rm("a", 0, 0, 4, 3, "l9") }), IntentCodes.UnknownLevel, "a"), "unknown level");
    Check(Has(Layout(new[] { Rm("a", 0, 0, 4, 3, type: "nope") }), IntentCodes.UnknownWallType, "a"), "unknown wall type");
    Check(Has(Layout(new[] { Rm("a", 0, 0, 4, 3) }, new[] { Op("d1", IntentOpeningKind.Door, Side("zz", IntentSide.South), 1, 0.9, 2.1, 0) }), IntentCodes.UnknownRoom, "d1"), "opening on an unknown room");
    Check(Has(Layout(new[] { Rm("a", 0, 0, 4, 3), Rm("b", 5, 0, 3, 3) }, new[] { Op("d1", IntentOpeningKind.Door, Between("a", "b"), 1, 0.9, 2.1, 0) }), IntentCodes.NotAdjacent, "d1"),
        "shared-wall opening between rooms that share no wall");
    Check(Has(Layout(new[] { Rm("a", 0, 0, 4, 3), Rm("u", 0, 0, 4, 3, "l2") }, new[] { Op("d1", IntentOpeningKind.Door, Between("a", "u"), 1, 0.9, 2.1, 0) }), IntentCodes.NotAdjacent, "d1"),
        "rooms on different levels are never adjacent");
    Check(Has(Layout(new[] { Rm("a", 0, 0, 4, 3) }, new[] { Op("d1", IntentOpeningKind.Door, Between("a", "a"), 1, 0.9, 2.1, 0) }), IntentCodes.NotAdjacent, "d1"), "a room is not adjacent to itself");
    var oneRoom = new[] { Rm("a", 0, 0, 4, 3) };
    IntentOpening Window(string key, double offset, double width, double height, double sill) => Op(key, IntentOpeningKind.Window, Side("a", IntentSide.South), offset, width, height, sill);
    Check(Has(Layout(oneRoom, new[] { Window("w1", 3.5, 1, 1, 1) }), IntentCodes.OpeningOutsideWall, "w1"), "opening past the wall end");
    Check(Has(Layout(oneRoom, new[] { Window("w1", -0.1, 1, 1, 1) }), IntentCodes.OpeningOutsideWall, "w1"), "opening before the wall start");
    Check(Has(Layout(oneRoom, new[] { Window("w1", 1, 1, 2, 1) }), IntentCodes.OpeningOutsideWall, "w1"), "opening above the wall top");
    Check(Has(Layout(oneRoom, new[] { Window("w1", 1, 1, 1, -0.5) }), IntentCodes.OpeningOutsideWall, "w1"), "opening below the level");
    Check(IntentCompiler.Validate(Layout(oneRoom, new[] { Window("w1", 0.1, 3.8, 2.8, 0) })).Length == 0, "an opening filling the clear span exactly is inside");
    Check(Has(Layout(oneRoom, new[] { Window("w1", 0, 4, 2.8, 0) }), IntentCodes.OpeningOutsideWall, "w1"), "an opening over the full axis span runs into the corner walls");
    Check(Has(Layout(oneRoom, new[] { Op("d1", IntentOpeningKind.Door, Side("a", IntentSide.South), 1, 0.9, 2.1, 0.1) }), IntentCodes.DoorSillInvalid, "d1"), "a door must sit on the level (sill 0)");
    Check(Has(Layout(oneRoom, new[] { Window("w1", 1, 1, 1, 1), Window("w2", 1.5, 1, 1, 1) }), IntentCodes.OpeningOverlap, "w1"), "overlapping openings on one wall");
    Check(IntentCompiler.Validate(Layout(oneRoom, new[] { Window("w1", 1, 1, 1, 1), Window("w2", 2, 1, 1, 1) })).Length == 0, "abutting openings do not overlap");
    Check(IntentCompiler.Validate(Layout(oneRoom, new[] { Op("d1", IntentOpeningKind.Door, Side("a", IntentSide.South), 1, 0.9, 2.0, 0), Window("w1", 1, 0.9, 0.5, 2.1) })).Length == 0,
        "a window above a door is not an overlap");
    Check(Has(Layout(new[] { Rm("a", 0, 0, double.NaN, 3) }), IntentCodes.DimensionInvalid, "a"), "non-finite room width");
    Check(Has(Layout(new[] { Rm("a", double.PositiveInfinity, 0, 4, 3) }), IntentCodes.DimensionInvalid, "a"), "non-finite room position");
    Check(Has(Layout(new[] { Rm("a", 0, 0, 0, 3) }), IntentCodes.DimensionInvalid, "a"), "zero room width");
    Check(Has(Layout(new[] { Rm("a", 0, 0, 4, -3) }), IntentCodes.DimensionInvalid, "a"), "negative room depth");
    Check(Has(Layout(new[] { Rm("a", double.MaxValue, 0, double.MaxValue, 3) }), IntentCodes.DimensionInvalid, "a"), "room extent overflowing to infinity");
    Check(Has(Layout(oneRoom, types: new[] { Ty("std", 0, 2.8) }), IntentCodes.DimensionInvalid, "std"), "zero wall thickness");
    Check(Has(Layout(oneRoom, types: new[] { Ty("std", 0.2, double.NaN) }), IntentCodes.DimensionInvalid, "std"), "non-finite wall height");
    Check(Has(Layout(oneRoom, levels: new[] { Lv("l1", double.NegativeInfinity) }), IntentCodes.DimensionInvalid, "l1"), "non-finite elevation");
    Check(Has(Layout(oneRoom, new[] { Window("w1", 1, 0, 1, 1) }), IntentCodes.DimensionInvalid, "w1"), "zero opening width");
    Check(Has(Layout(oneRoom, new[] { Window("w1", 1, 1, double.NaN, 1) }), IntentCodes.DimensionInvalid, "w1"), "non-finite opening height");
    Check(Has(Layout(oneRoom, new[] { Window("w1", double.NaN, 1, 1, 1) }), IntentCodes.DimensionInvalid, "w1"), "non-finite opening offset");
    Check(Has(Layout(new[] { Rm("a", 0, 0, 0.15, 3) }), IntentCodes.RoomTooSmall, "a"), "room narrower than its wall thickness");
    Check(Has(Layout(new[] { Rm("a", 0, 0, 4, 3), Rm("a", 10, 0, 4, 3) }), IntentCodes.DuplicateKey, "a"), "duplicate room key");
    Check(Has(Layout(oneRoom, levels: new[] { Lv("l1", 0), Lv("l1", 3) }), IntentCodes.DuplicateKey, "l1"), "duplicate level key");
    Check(Has(Layout(oneRoom, types: new[] { Ty("std", 0.2, 2.8), Ty("std", 0.1, 2.8) }), IntentCodes.DuplicateKey, "std"), "duplicate wall type key");
    Check(Has(Layout(oneRoom, new[] { Window("w1", 0.5, 1, 1, 1), Window("w1", 2.5, 1, 1, 1) }), IntentCodes.DuplicateKey, "w1"), "duplicate opening key");
    Check(Has(Layout(new[] { Rm("Room A", 0, 0, 4, 3) }), IntentCodes.KeyInvalid, "Room A"), "key outside [a-z0-9_-]");
    Check(Has(Layout(new[] { Rm("a", 0, 0, 4, 3) with { Name = " " } }), IntentCodes.TextInvalid, "a"), "blank name");
    Check(Has(Layout(oneRoom) with { Provenance = new Provenance("", "unit") }, IntentCodes.ProvenanceInvalid), "blank provenance");
    Check(Has(Layout(oneRoom) with { Version = 2 }, IntentCodes.VersionUnsupported), "unsupported version");
    Check(Has(Layout(Enumerable.Range(0, LayoutIntent.MaxRooms + 1).Select(i => Rm("r" + N(i), 10 * i, 0, 4, 3)).ToArray()), IntentCodes.LimitExceeded), "room count is bounded");
    var dupA = Layout(new[] { Rm("a", 0, 0, double.NaN, 3), Rm("a", 10, 0, 4, 3) });
    var dupB = dupA with { Rooms = dupA.Rooms.Reverse().ToArray() };
    Check(JsonSerializer.Serialize(IntentCompiler.Validate(dupA)) == JsonSerializer.Serialize(IntentCompiler.Validate(dupB)) && Has(dupA, IntentCodes.DuplicateKey, "a") && Has(dupA, IntentCodes.DimensionInvalid, "a") && Has(dupB, IntentCodes.DimensionInvalid, "a"),
        "duplicates: every entry is checked and the issues do not depend on order", ShowIssues(dupA) + " vs " + ShowIssues(dupB));
    var crossDup = Layout(new[] { Rm("l1", 0, 0, 4, 3), Rm("l1", 10, 0, 4, 3) }, levels: new[] { Lv("l1", 0), Lv("l1", 3) });
    Check(IntentCompiler.Validate(crossDup).Count(issue => issue.Code == IntentCodes.DuplicateKey) == 2, "duplicates are reported per collection", ShowIssues(crossDup));
    var invalid = Layout(new[] { Rm("a", 0, 0, 4, 3), Rm("x", 3, 1, 2, 1), Rm("y", 0, 0, 4, 3, "l9") }, new[] { Window("w1", 3.5, 1, 1, 1) });
    var invalidReversed = invalid with { Rooms = invalid.Rooms.Reverse().ToArray() };
    Check(JsonSerializer.Serialize(IntentCompiler.Validate(invalid)) == JsonSerializer.Serialize(IntentCompiler.Validate(invalidReversed)) && IntentCompiler.Validate(invalid).Length > 0,
        "issue list is independent of input order", ShowIssues(invalid));
    var refusal = Code(() => IntentCompiler.Compile(projectId, 0, Layout(new[] { Rm("a", 0, 0, 4, 3), Rm("x", 3, 1, 2, 1) })));
    Check(refusal == IntentCodes.RoomOverlap, "Compile refuses an invalid layout with the first issue code", refusal ?? "accepted");
    try { IntentCompiler.Compile(projectId, 0, Layout(new[] { Rm("a", 0, 0, 4, 3), Rm("x", 3, 1, 2, 1) })); Check(false, "Compile refusal carries issues"); }
    catch (IntentException error) { Check(error.Issues.Count > 0 && error.Issues[0].Code == IntentCodes.RoomOverlap, "Compile refusal carries issues"); }
    Check(Code(() => IntentCompiler.Compile(Guid.Empty, 0, baseLayout))?.StartsWith("argument:", StringComparison.Ordinal) == true, "empty project id is refused");
    Check(Code(() => IntentCompiler.Compile(projectId, -1, baseLayout))?.StartsWith("argument:", StringComparison.Ordinal) == true, "negative revision is refused");
    Check(Code(() => IntentCompiler.Validate(null!))?.StartsWith("argument:", StringComparison.Ordinal) == true, "null intent is refused");

    // ---- Canonical JSON ----
    var tiny = new LayoutIntent(1, provenance, new[] { Lv("l1", 0) }, new[] { Ty("std", 0.2, 2.8) }, new[] { Rm("a", 0, 0, 4, 3) },
        new[] { Op("d1", IntentOpeningKind.Door, Side("a", IntentSide.South), 1, 0.9, 2.1, 0), Op("w1", IntentOpeningKind.Window, Between("z", "a"), 0.5, 1, 1.2, 0.9) });
    const string TinyJson = "{\"intent_version\":1,\"provenance\":{\"source\":\"intent-tests\",\"approval\":\"unit\"},"
        + "\"levels\":[{\"key\":\"l1\",\"name\":\"L1\",\"elevation_m\":0}],"
        + "\"wall_types\":[{\"key\":\"std\",\"name\":\"Type std\",\"thickness_m\":0.2,\"height_m\":2.8}],"
        + "\"rooms\":[{\"key\":\"a\",\"name\":\"Room a\",\"level\":\"l1\",\"wall_type\":\"std\",\"x_m\":0,\"y_m\":0,\"width_m\":4,\"depth_m\":3}],"
        + "\"openings\":[{\"key\":\"d1\",\"kind\":\"door\",\"room\":\"a\",\"side\":\"south\",\"offset_m\":1,\"width_m\":0.9,\"height_m\":2.1,\"sill_m\":0},"
        + "{\"key\":\"w1\",\"kind\":\"window\",\"between\":[\"a\",\"z\"],\"offset_m\":0.5,\"width_m\":1,\"height_m\":1.2,\"sill_m\":0.9}]}";
    var tinyJson = IntentJson.Serialize(tiny);
    Check(tinyJson == TinyJson, "canonical JSON golden (member order, sorted pair, shortest numbers)", tinyJson);
    var baseJson = IntentJson.Serialize(baseLayout);
    Check(IntentJson.Serialize(reversed) == baseJson && IntentJson.Serialize(shuffled) == baseJson, "canonical JSON sorts every list by key");
    var reparsed = IntentJson.Parse(baseJson);
    Check(IntentJson.Serialize(reparsed) == baseJson, "Serialize(Parse(Serialize(x))) == Serialize(x)");
    Check(Compiler.Hash(IntentCompiler.Compile(projectId, 5, reparsed)) == hash, "parsed intent compiles to the same hash");
    Check(reparsed.Openings.Single(opening => opening.Key == "d1").Host == Between("a", "b") && reparsed.Openings.Single(opening => opening.Key == "w2").Host == Side("b", IntentSide.East),
        "hosts round-trip as typed records");
    Check(IntentJson.Serialize(tiny with { Levels = new[] { Lv("l1", -0.0) } }).Contains("\"elevation_m\":0}", StringComparison.Ordinal), "-0 is written as 0");
    string? Refuse(string json) { try { IntentJson.Parse(json); return null; } catch (IntentException error) { return error.Code + "@" + error.Field; } catch (Exception error) { return "unexpected " + error.GetType().Name; } }
    string Patch(string from, string to) => TinyJson.Replace(from, to, StringComparison.Ordinal);
    var refused = Refuse(Patch("\"depth_m\":3}", "\"depth_m\":3,\"colour\":\"red\"}"));
    Check(refused == "member_unknown@rooms[0].colour", "unknown member refused with its path", refused ?? "accepted");
    refused = Refuse(Patch("\"x_m\":0,", "\"x_m\":0,\"x_m\":1,"));
    Check(refused == "member_duplicate@rooms[0].x_m", "duplicate member refused", refused ?? "accepted");
    refused = Refuse(Patch("\"wall_type\":\"std\",", ""));
    Check(refused == "member_missing@rooms[0].wall_type", "missing member refused", refused ?? "accepted");
    refused = Refuse(Patch("\"width_m\":4", "\"width_m\":\"4\""));
    Check(refused == "type_invalid@rooms[0].width_m", "string for number refused", refused ?? "accepted");
    refused = Refuse(Patch("\"intent_version\":1", "\"intent_version\":2"));
    Check(refused == "version_unsupported@intent_version", "future version refused", refused ?? "accepted");
    refused = Refuse(Patch("\"side\":\"south\"", "\"side\":\"up\""));
    Check(refused == "value_invalid@openings[0].side", "unknown side refused", refused ?? "accepted");
    refused = Refuse(Patch("\"kind\":\"door\"", "\"kind\":\"arch\""));
    Check(refused == "value_invalid@openings[0].kind", "unknown opening kind refused", refused ?? "accepted");
    refused = Refuse(Patch("\"between\":[\"a\",\"z\"]", "\"between\":[\"a\",\"z\"],\"room\":\"a\",\"side\":\"south\""));
    Check(refused == "value_invalid@openings[1]", "an opening with both a room side and a shared wall is refused", refused ?? "accepted");
    refused = Refuse(Patch("\"room\":\"a\",\"side\":\"south\",", ""));
    Check(refused == "member_missing@openings[0].room", "an opening without a host is refused", refused ?? "accepted");
    refused = Refuse(Patch("\"between\":[\"a\",\"z\"]", "\"between\":[\"a\",\"z\",\"b\"]"));
    Check(refused == "value_invalid@openings[1].between", "a shared wall names exactly two rooms", refused ?? "accepted");
    refused = Refuse(Patch("\"name\":\"L1\"", "\"name\":\"\""));
    Check(refused == "value_invalid@levels[0].name", "blank text refused", refused ?? "accepted");
    refused = Refuse(Patch("\"width_m\":4", "\"width_m\":1e400"));
    Check(refused == "type_invalid@rooms[0].width_m", "number overflowing double refused", refused ?? "accepted");
    refused = Refuse(Patch("\"sill_m\":0}", "\"sill_m\":0,}"));
    Check(refused?.StartsWith("json_invalid", StringComparison.Ordinal) == true, "trailing comma refused", refused ?? "accepted");
    refused = Refuse("// comment\n" + TinyJson);
    Check(refused?.StartsWith("json_invalid", StringComparison.Ordinal) == true, "comments refused", refused ?? "accepted");
    refused = Refuse(Patch("\"intent_version\":1,", "\"intent_version\":1,\"x\":" + new string('[', 64) + new string(']', 64) + ","));
    Check(refused?.StartsWith("json_invalid", StringComparison.Ordinal) == true, "nesting depth is bounded", refused ?? "accepted");
    refused = Refuse(TinyJson + new string(' ', IntentJson.MaxInputChars));
    Check(refused == "input_too_large@", "input size is bounded", refused ?? "accepted");
    refused = Refuse("[]");
    Check(refused == "type_invalid@", "root must be an object", refused ?? "accepted");
    var manyLevels = "[" + string.Join(",", Enumerable.Range(0, LayoutIntent.MaxLevels + 1).Select(i => "{\"key\":\"l" + N(i) + "\",\"name\":\"L\",\"elevation_m\":0}")) + "]";
    refused = Refuse(System.Text.RegularExpressions.Regex.Replace(TinyJson, "\"levels\":\\[[^\\]]*\\]", "\"levels\":" + manyLevels));
    Check(refused == "limit_exceeded@levels", "list length is bounded while parsing", refused ?? "accepted");
    Check(Code(() => IntentJson.Serialize(tiny with { Rooms = new[] { Rm("a", 0, 0, double.NaN, 3) } }))?.StartsWith("argument:", StringComparison.Ordinal) == true, "non-finite numbers are not serialized");
}
catch (Exception error)
{
    Console.WriteLine("::error::intent FAIL unexpected " + error.GetType().Name + " | " + error.Message);
    failed++;
}

Console.WriteLine(JsonSerializer.Serialize(new { passed, failed, intent_contract = failed == 0 }));
Environment.ExitCode = failed == 0 && passed > 0 ? 0 : 1;

sealed record Plan(string[] Created, string[] Updated, string[] Deleted, ExecutionPlan Execution)
{
    public string Show() => "create " + string.Join(",", Created) + " update " + string.Join(",", Updated) + " delete " + string.Join(",", Deleted);
}
