using Bim.Core;
using System.Text.Json;
// P2-02 oracle: typed semantic graph derived from BIM-IR Kind + Dependencies (no schema change),
// relation queries, deterministic transitive closure and 10k+ chains without recursion.
int passed = 0, failed = 0;
var provenance = new Provenance("graph-tests", "unit");

void Check(bool condition, string name, string detail = "")
{
    if (condition) { Console.WriteLine("PASS " + name); passed++; return; }
    Console.WriteLine("::error::graph FAIL " + name + (detail.Length > 0 ? " | " + detail : ""));
    failed++;
}
// Guid ordering compares the first int field first, so G(n) sorts by n.
Guid G(int n) => new(n, 0, 0, new byte[8]);
Element E(int n, string kind, params int[] dependencies) => new(G(n), kind, Array.Empty<double>(), dependencies.Select(G).ToArray(), provenance);
Project P(params Element[] elements) => new(G(999_999), 0, elements);
string Show(IEnumerable<Guid> ids) => string.Join(",", ids.Select(id => id.ToString("N")[..8]));
bool Same(IEnumerable<Guid> actual, params int[] expected) => actual.SequenceEqual(expected.Select(G));
string? Code(Action action)
{
    try { action(); return null; }
    catch (ArgumentException error) { return error.Message; }
    catch (InvalidOperationException error) { return error.Message; }
    catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
}

try
{
    // ---- Edge typing from existing Kind + Dependencies ----
    Check(SemanticGraph.Classify("wall", "level") == GraphEdgeKind.OnLevel, "dependency on a level is on-level");
    Check(SemanticGraph.Classify("plan", "level") == GraphEdgeKind.OnLevel, "plan view on a level is on-level");
    Check(SemanticGraph.Classify("door", "wall") == GraphEdgeKind.HostedBy, "door on wall is hosted-by");
    Check(SemanticGraph.Classify("window", "wall") == GraphEdgeKind.HostedBy, "window on wall is hosted-by");
    Check(SemanticGraph.Classify("opening", "wall") == GraphEdgeKind.HostedBy, "opening on wall is hosted-by");
    Check(SemanticGraph.Classify("space", "wall") == GraphEdgeKind.Bounds, "wall bounding a space is bounds");
    Check(SemanticGraph.Classify("room", "wall") == GraphEdgeKind.Bounds, "wall bounding a room is bounds");
    Check(SemanticGraph.Classify("plan", "wall") == GraphEdgeKind.DependsOn, "plan on wall stays generic dependency");
    Check(SemanticGraph.Classify("door", "space") == GraphEdgeKind.DependsOn, "door on non-wall stays generic dependency");
    Check(SemanticGraph.Classify("sheet", "schedule") == GraphEdgeKind.DependsOn, "sheet on schedule stays generic dependency");
    Check(SemanticGraph.Classify("Door", "Wall") == GraphEdgeKind.DependsOn, "kind matching is exact (BIM-IR kinds are lower-case)");

    // ---- Room slice (RoomCompiler output, unchanged) ----
    var roomId = Guid.Parse("5b1c1f5e-79a0-4b3f-9d10-2f8d6f0d0a01");
    var room = RoomCompiler.Compile(Guid.Parse("8f3f3c4e-0d6a-4b52-a7d8-5a3d1c2b9e01"), 3,
        new RoomIntent(roomId, 4, 3, 2.8, 0.2, 0, 0, "m", provenance));
    var hashBefore = Compiler.Hash(room);
    var graph = SemanticGraph.Build(room);
    Check(Compiler.Hash(room) == hashBefore, "building the graph does not mutate the project");
    Guid R(string role) => RoomCompiler.Id(roomId, role);
    var level = R("level"); var space = R("space");
    var walls = new[] { "south", "east", "north", "west" }.Select(R).ToArray();
    Check(graph.Edges.Count == 17, "room slice has 17 edges (one per declared dependency)", graph.Edges.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    int Count(GraphEdgeKind kind) => graph.Edges.Count(edge => edge.Kind == kind);
    Check(Count(GraphEdgeKind.OnLevel) == 5 && Count(GraphEdgeKind.Bounds) == 4 && Count(GraphEdgeKind.HostedBy) == 0 && Count(GraphEdgeKind.DependsOn) == 8,
        "room slice edge kinds: 5 on-level, 4 bounds, 0 hosted-by, 8 depends-on",
        string.Join(",", Enum.GetValues<GraphEdgeKind>().Select(kind => kind + "=" + Count(kind))));
    Check(graph.Edges.SequenceEqual(graph.Edges.OrderBy(edge => edge.Dependent).ThenBy(edge => edge.Prerequisite)), "edges are ordered by dependent then prerequisite");
    Check(graph.Nodes.SequenceEqual(room.Elements.Select(element => element.Id).Order()), "nodes are every element id in Guid order");
    Check(graph.KindOf(space) == "space" && graph.KindOf(level) == "level" && graph.Contains(level) && !graph.Contains(Guid.NewGuid()), "kind lookup and membership");
    Check(graph.OnLevel(level).SequenceEqual(walls.Append(R("plan")).Order()), "elements on the level are the four walls and the plan", Show(graph.OnLevel(level)));
    Check(graph.Boundaries(space).SequenceEqual(walls.Order()), "what bounds the space: its four walls", Show(graph.Boundaries(space)));
    Check(walls.All(wall => graph.Bounded(wall).SequenceEqual(new[] { space })), "each wall bounds exactly the space");
    Check(walls.All(wall => graph.LevelOf(wall).SequenceEqual(new[] { level })), "each wall is on the level");
    Check(graph.LevelOf(space).Count == 0 && graph.Boundaries(walls[0]).Count == 0 && graph.Hosts(walls[0]).Count == 0, "absent relations are empty, not errors");
    var edgeSpaceSouth = graph.EdgesOf(space, GraphDirection.Prerequisites).Single(edge => edge.Prerequisite == walls[0]);
    Check(edgeSpaceSouth == new GraphEdge(space, walls[0], GraphEdgeKind.Bounds), "space->south wall edge reads 'south wall bounds space'");

    // Affected closure agrees with the Compiler.Diff dirty closure for a level change.
    var moved = room with { Elements = room.Elements.Select(element => element.Id == level ? element with { GeometryMetres = new[] { 1.5 } } : element).ToArray() };
    var plan = Compiler.Diff(room, moved);
    var affectedLevel = graph.Affected(level);
    Check(affectedLevel.Order().SequenceEqual(plan.DirtyIds), "affected(level) equals Diff dirty closure", Show(affectedLevel) + " vs " + Show(plan.DirtyIds));
    Check(affectedLevel.Count == room.Elements.Length && affectedLevel[0] == level, "level change affects the whole slice, seed first");
    var south = walls[0];
    var affectedSouth = graph.Affected(south);
    var expectedSouth = new[] { south }.Concat(new[] { space, R("plan") }.Order()).Concat(new[] { R("schedule"), R("sheet") }.Order()).ToArray();
    // BFS layers: seed; {space, plan}; then {schedule (via space), sheet (via plan)} - each layer Guid-ordered.
    Check(affectedSouth.SequenceEqual(expectedSouth), "affected(south wall) is breadth-first with Guid-ordered layers", Show(affectedSouth));
    var southMoved = room with { Elements = room.Elements.Select(element => element.Id == south ? element with { GeometryMetres = new[] { 0.1, 0.1, 4.3, 0.1, 3.0, 0.2 } } : element).ToArray() };
    Check(affectedSouth.Order().SequenceEqual(Compiler.Diff(room, southMoved).DirtyIds), "affected(south wall) equals Diff dirty closure");
    // AffectedBy: plan-level closure over before and after, so created and deleted elements count too.
    // 101 level, 102 wall A, 103 wall B, 104 door on B, 105 space bounded by A, 106 sheet on space; 107 door on A and 108 its tag are created.
    Element Wall(int n, double height) => new(G(n), "wall", new[] { 0, 0, 4, 0, height, 0.2 }, new[] { G(101) }, provenance);
    var editBefore = P(E(101, "level"), Wall(102, 3), Wall(103, 3), E(104, "door", 103), E(105, "space", 102), E(106, "sheet", 105));
    var created = P(editBefore.Elements.Append(E(107, "door", 102)).ToArray());
    var createPlan = Compiler.Diff(editBefore, created);
    Check(Same(SemanticGraph.AffectedBy(createPlan, editBefore, created), 107) && Same(createPlan.DirtyIds, 107), "created door: affected-by equals Diff dirty ids", Show(SemanticGraph.AffectedBy(createPlan, editBefore, created)));
    var deleted = P(editBefore.Elements.Where(element => element.Id != G(103) && element.Id != G(104)).ToArray());
    var deletePlan = Compiler.Diff(editBefore, deleted);
    Check(Same(SemanticGraph.AffectedBy(deletePlan, editBefore, deleted), 103, 104) && Same(deletePlan.DirtyIds, 103, 104), "deleted wall and its door: affected-by equals Diff dirty ids", Show(SemanticGraph.AffectedBy(deletePlan, editBefore, deleted)));
    var mixed = P(E(101, "level"), Wall(102, 3.5), E(105, "space", 102), E(106, "sheet", 105), E(107, "door", 102), E(108, "tag", 107));
    var mixedPlan = Compiler.Diff(editBefore, mixed);
    var mixedAffected = SemanticGraph.AffectedBy(mixedPlan, editBefore, mixed);
    Check(mixedAffected.Order().SequenceEqual(mixedPlan.DirtyIds) && mixedPlan.DirtyIds.Length == 7, "create + update + delete: affected-by equals Diff dirty ids", Show(mixedAffected) + " vs " + Show(mixedPlan.DirtyIds));
    Check(Same(mixedAffected, 102, 103, 104, 107, 108, 105, 106), "affected-by order: changed elements Guid-ordered, then dependents by distance", Show(mixedAffected));
    Check(Same(SemanticGraph.AffectedBy(Compiler.Diff(editBefore, editBefore), editBefore, editBefore)), "no-op plan affects nothing");
    Check(Code(() => SemanticGraph.AffectedBy(createPlan, editBefore, deleted)) == "plan_mismatch", "affected-by refuses a plan that is not the deterministic diff", Code(() => SemanticGraph.AffectedBy(createPlan, editBefore, deleted)) ?? "accepted");
    Check(graph.Prerequisites(R("sheet")).Order().SequenceEqual(room.Elements.Select(element => element.Id).Where(id => id != R("sheet")).Order()), "sheet transitively requires everything else");

    // ---- Hosted elements on a wall ----
    // 1 level, 2 wall, 3 door, 4 window, 5 opening, 6 space, 7 tag on door, 8 second wall, 9 door on second wall
    var hosted = SemanticGraph.Build(P(E(1, "level"), E(2, "wall", 1), E(3, "door", 2, 1), E(4, "window", 2), E(5, "opening", 2),
        E(6, "space", 2, 8), E(7, "tag", 3), E(8, "wall", 1), E(9, "door", 8)));
    Check(Same(hosted.Hosts(G(2)), 3, 4, 5), "what wall 2 hosts: door, window, opening", Show(hosted.Hosts(G(2))));
    Check(Same(hosted.Hosts(G(8)), 9), "what wall 8 hosts: its door");
    Check(hosted.HostOf(G(3)) == G(2) && hosted.HostOf(G(9)) == G(8) && hosted.HostOf(G(7)) is null && hosted.HostOf(G(2)) is null, "host lookup");
    var twoHosts = SemanticGraph.Build(P(E(1, "level"), E(2, "wall", 1), E(8, "wall", 1), E(3, "door", 8, 2)));
    Check(twoHosts.HostOf(G(3)) is null, "door naming two walls has an ambiguous host: null, not the lowest Guid", twoHosts.HostOf(G(3))?.ToString() ?? "null");
    Check(Same(twoHosts.Neighbours(G(3), GraphDirection.Prerequisites, GraphEdgeKind.HostedBy), 2, 8) && Same(twoHosts.Hosts(G(8)), 3), "both candidate hosts stay visible as hosted-by edges");
    Check(Same(hosted.OnLevel(G(1)), 2, 3, 8), "on level 1: both walls and the door that also names the level");
    Check(Same(hosted.Boundaries(G(6)), 2, 8), "space 6 bounded by walls 2 and 8");
    Check(Same(hosted.Neighbours(G(2), GraphDirection.Dependents), 3, 4, 5, 6), "dependents of wall 2", Show(hosted.Neighbours(G(2), GraphDirection.Dependents)));
    Check(Same(hosted.Neighbours(G(2), GraphDirection.Dependents, GraphEdgeKind.HostedBy), 3, 4, 5), "dependents of wall 2 filtered to hosted-by");
    Check(Same(hosted.Neighbours(G(2), GraphDirection.Dependents, GraphEdgeKind.Bounds), 6), "dependents of wall 2 filtered to bounds");
    Check(Same(hosted.Neighbours(G(2), GraphDirection.Prerequisites), 1), "prerequisites of wall 2");
    Check(Same(hosted.Neighbours(G(2), GraphDirection.Both), 1, 3, 4, 5, 6), "both directions of wall 2");
    Check(Same(hosted.Neighbours(G(3), GraphDirection.Prerequisites, GraphEdgeKind.OnLevel), 1), "door 3 prerequisites filtered to on-level");
    Check(hosted.EdgesOf(G(3), GraphDirection.Prerequisites).SequenceEqual(new[] { new GraphEdge(G(3), G(1), GraphEdgeKind.OnLevel), new GraphEdge(G(3), G(2), GraphEdgeKind.HostedBy) }), "door 3 typed outgoing edges in Guid order");
    Check(hosted.EdgesOf(G(7), GraphDirection.Prerequisites).SequenceEqual(new[] { new GraphEdge(G(7), G(3), GraphEdgeKind.DependsOn) }), "tag keeps the generic dependency edge");
    // Wall 8: outgoing 8->1, incoming 6->8 and 9->8; unsorted concatenation would put 8->1 first.
    Check(hosted.EdgesOf(G(8), GraphDirection.Both).SequenceEqual(new[] { new GraphEdge(G(6), G(8), GraphEdgeKind.Bounds), new GraphEdge(G(8), G(1), GraphEdgeKind.OnLevel), new GraphEdge(G(9), G(8), GraphEdgeKind.HostedBy) }),
        "both-direction edges of wall 8 are ordered by dependent then prerequisite", string.Join(";", hosted.EdgesOf(G(8), GraphDirection.Both)));
    Check(hosted.EdgesOf(G(3), GraphDirection.Both).SequenceEqual(new[] { new GraphEdge(G(3), G(1), GraphEdgeKind.OnLevel), new GraphEdge(G(3), G(2), GraphEdgeKind.HostedBy), new GraphEdge(G(7), G(3), GraphEdgeKind.DependsOn) }),
        "both-direction edges of door 3 mix outgoing and incoming in order");
    Check(hosted.EdgesOf(G(2), GraphDirection.Both, GraphEdgeKind.OnLevel).SequenceEqual(new[] { new GraphEdge(G(2), G(1), GraphEdgeKind.OnLevel) }), "edges of wall 2 filtered by kind");
    Check(Same(hosted.Affected(G(2)), 2, 3, 4, 5, 6, 7), "changing wall 2 affects its hosted elements, the space and the door tag", Show(hosted.Affected(G(2))));
    Check(Same(hosted.Affected(G(6), G(3)), 3, 6, 7), "multi-seed closure: seeds Guid-ordered then dependents");
    Check(Same(hosted.Affected(G(3), G(3)), 3, 7), "duplicate seeds are collapsed");
    Check(Same(hosted.Closure(new[] { G(1) }, GraphDirection.Dependents, new[] { GraphEdgeKind.OnLevel }), 1, 2, 3, 8), "closure restricted to on-level edges");
    Check(Same(hosted.Closure(new[] { G(1) }, GraphDirection.Dependents, new[] { GraphEdgeKind.OnLevel }, includeSeeds: false), 2, 3, 8), "closure without seeds");
    Check(Same(hosted.Closure(new[] { G(1) }, GraphDirection.Dependents, new[] { GraphEdgeKind.OnLevel, GraphEdgeKind.HostedBy }), 1, 2, 3, 8, 4, 5, 9), "closure over on-level and hosted-by", Show(hosted.Closure(new[] { G(1) }, GraphDirection.Dependents, new[] { GraphEdgeKind.OnLevel, GraphEdgeKind.HostedBy })));
    Check(hosted.Closure(new[] { G(1) }, GraphDirection.Dependents, Array.Empty<GraphEdgeKind>()).SequenceEqual(new[] { G(1) }), "closure with no edge kinds is just the seeds");
    Check(Same(hosted.Prerequisites(G(7)), 3, 1, 2), "prerequisites of tag: door, then level and wall", Show(hosted.Prerequisites(G(7))));
    Check(Same(hosted.Closure(new[] { G(9) }, GraphDirection.Both), 9, 8, 1, 6, 2, 3, 4, 5, 7), "undirected closure reaches the connected component", Show(hosted.Closure(new[] { G(9) }, GraphDirection.Both)));
    Check(Same(hosted.Affected(Array.Empty<Guid>())), "empty seed set gives empty closure");

    // ---- Determinism independent of element / dependency order and duplicates ----
    var shuffled = SemanticGraph.Build(P(E(9, "door", 8), E(7, "tag", 3, 3), E(6, "space", 8, 2), E(5, "opening", 2), E(4, "window", 2, 2),
        E(3, "door", 1, 2), E(8, "wall", 1), E(2, "wall", 1), E(1, "level")));
    Check(shuffled.Edges.SequenceEqual(hosted.Edges), "edges are identical regardless of element and dependency order; duplicate dependencies collapse");
    Check(shuffled.Affected(G(1)).SequenceEqual(hosted.Affected(G(1))) && shuffled.Closure(new[] { G(9) }, GraphDirection.Both).SequenceEqual(hosted.Closure(new[] { G(9) }, GraphDirection.Both)), "closures are identical regardless of input order");
    // The persisted form (ProjectStore JSON) carries everything the graph needs: no hidden in-memory state.
    var persisted = JsonSerializer.Deserialize<Project>(JsonSerializer.Serialize(room))!;
    Check(SemanticGraph.Build(persisted).Edges.SequenceEqual(graph.Edges), "graph rebuilt from the persisted project JSON has the same typed edges");

    // ---- Cycles (Validate rejects them, but the graph view still terminates) ----
    var cyclic = SemanticGraph.Build(P(E(1, "wall", 2), E(2, "wall", 1), E(3, "door", 1)));
    Check(Same(cyclic.Affected(G(1)), 1, 2, 3) && Same(cyclic.Prerequisites(G(3)), 1, 2), "closure terminates on a dependency cycle");
    Check(Same(cyclic.Neighbours(G(1), GraphDirection.Both), 2, 3), "two-cycle neighbour listed once");
    var self = SemanticGraph.Build(P(E(1, "tag", 1)));
    Check(Same(self.Affected(G(1)), 1) && Same(self.Prerequisites(G(1))), "self dependency neither repeats the seed nor loops");

    // ---- Deep chains and wide fans (no recursion) ----
    const int Deep = 20_000;
    var chain = SemanticGraph.Build(new Project(G(999_999), 0, Enumerable.Range(1, Deep).Select(i => i == 1 ? E(1, "level") : E(i, "tag", i - 1)).Reverse().ToArray()));
    var downstream = chain.Affected(G(1));
    Check(downstream.Count == Deep && downstream.SequenceEqual(Enumerable.Range(1, Deep).Select(G)), "20k chain: affected(root) walks the whole chain in order", downstream.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    var upstream = chain.Prerequisites(G(Deep));
    Check(upstream.Count == Deep - 1 && upstream.SequenceEqual(Enumerable.Range(1, Deep - 1).Reverse().Select(G)), "20k chain: prerequisites(tail) walks back to the root in order");
    Check(Same(chain.Closure(new[] { G(Deep / 2) }, GraphDirection.Both, new[] { GraphEdgeKind.OnLevel }), Deep / 2), "20k chain: off-kind filter stops at the seed");
    const int Wide = 10_000;
    var fan = SemanticGraph.Build(new Project(G(999_999), 0, new[] { E(50_000, "level") }.Concat(Enumerable.Range(1, Wide).Reverse().Select(i => E(i, "wall", 50_000))).ToArray()));
    Check(fan.OnLevel(G(50_000)).SequenceEqual(Enumerable.Range(1, Wide).Select(G)), "10k walls on one level, Guid-ordered");
    Check(fan.Affected(G(50_000)).Count == Wide + 1, "10k fan closure");

    // ---- Refusals ----
    Check(Code(() => SemanticGraph.Build(P(E(1, "level"), E(1, "wall")))) == "duplicate_id", "duplicate ids are refused", Code(() => SemanticGraph.Build(P(E(1, "level"), E(1, "wall")))) ?? "accepted");
    Check(Code(() => SemanticGraph.Build(P(E(2, "wall", 1)))) == "missing_dependency", "dangling dependency is refused", Code(() => SemanticGraph.Build(P(E(2, "wall", 1)))) ?? "accepted");
    Check(Code(() => hosted.Neighbours(G(42), GraphDirection.Both)) == "unknown_element", "unknown element query is refused");
    Check(Code(() => hosted.Affected(G(1), G(42))) == "unknown_element", "unknown closure seed is refused");
    Check(Code(() => hosted.KindOf(G(42))) == "unknown_element", "unknown kind lookup is refused");
    Check(Code(() => hosted.Neighbours(G(1), (GraphDirection)7)) == "invalid_direction", "invalid direction is refused");
    Check(SemanticGraph.Build(P()).Edges.Count == 0 && SemanticGraph.Build(P()).Nodes.Count == 0, "empty project gives empty graph");
}
catch (Exception error)
{
    Console.WriteLine("::error::graph FAIL unexpected " + error.GetType().Name + " | " + error.Message);
    failed++;
}

Console.WriteLine(JsonSerializer.Serialize(new { passed, failed, graph_contract = failed == 0 }));
Environment.ExitCode = failed == 0 && passed > 0 ? 0 : 1;
