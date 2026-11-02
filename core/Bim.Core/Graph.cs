namespace Bim.Core;

// P2-02 semantic graph: a derived, read-only view over BIM-IR. Edge types come only from data that
// already exists (Element.Kind and Element.Dependencies); nothing here is persisted or enters plan/hash JSON.
//
// Every edge points from the element that declares the dependency (Dependent) to the element it names
// (Prerequisite), exactly like Element.Dependencies. The kind says how to read it:
//   DependsOn  generic dependency (Dependent depends on Prerequisite)
//   OnLevel    Dependent is placed on the level Prerequisite (any kind depending on a "level")
//   HostedBy   Dependent (door/window/opening) is hosted by the wall Prerequisite
//   Bounds     the wall Prerequisite bounds the space/room Dependent
public enum GraphEdgeKind { DependsOn, OnLevel, HostedBy, Bounds }

// Prerequisites follows edges Dependent -> Prerequisite (what an element needs);
// Dependents follows them backwards (what needs the element, i.e. what a change affects).
public enum GraphDirection { Prerequisites, Dependents, Both }

public sealed record GraphEdge(Guid Dependent, Guid Prerequisite, GraphEdgeKind Kind);

public sealed class SemanticGraph
{
    readonly Dictionary<Guid, int> index;
    // Node i is ids[i]; ids are Guid-ordered, so index order is Guid order.
    readonly Guid[] ids;
    readonly string[] elementKinds;
    // outgoing[i]: edges with Dependent ids[i], ordered by Prerequisite. incoming[i]: edges with Prerequisite ids[i], ordered by Dependent.
    readonly List<GraphEdge>[] outgoing;
    readonly List<GraphEdge>[] incoming;

    public IReadOnlyList<Guid> Nodes { get; }
    // All edges, one per distinct declared dependency, ordered by Dependent then Prerequisite.
    public IReadOnlyList<GraphEdge> Edges { get; }

    SemanticGraph(Dictionary<Guid, int> index, Guid[] ids, string[] kinds, List<GraphEdge>[] outgoing, List<GraphEdge>[] incoming, List<GraphEdge> edges)
    {
        this.index = index; this.ids = ids; elementKinds = kinds; this.outgoing = outgoing; this.incoming = incoming;
        Nodes = Array.AsReadOnly(ids);
        Edges = edges.AsReadOnly();
    }

    // Kind matching is exact: BIM-IR kinds are lower-case identifiers.
    public static GraphEdgeKind Classify(string dependentKind, string prerequisiteKind)
    {
        if (prerequisiteKind == "level") return GraphEdgeKind.OnLevel;
        if (prerequisiteKind == "wall" && dependentKind is "door" or "window" or "opening") return GraphEdgeKind.HostedBy;
        if (prerequisiteKind == "wall" && dependentKind is "space" or "room") return GraphEdgeKind.Bounds;
        return GraphEdgeKind.DependsOn;
    }

    // Duplicate identities and dangling dependencies make relations ambiguous, so they are refused
    // (same codes as Compiler.Validate). Cycles are tolerated: every traversal is iterative with a visited set.
    public static SemanticGraph Build(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var ids = project.Elements.Select(element => element.Id).Order().ToArray();
        var index = new Dictionary<Guid, int>(ids.Length);
        for (var i = 0; i < ids.Length; i++) if (!index.TryAdd(ids[i], i)) throw new ArgumentException("duplicate_id");
        var kinds = new string[ids.Length];
        var outgoing = new List<GraphEdge>[ids.Length];
        var incoming = new List<GraphEdge>[ids.Length];
        for (var i = 0; i < ids.Length; i++) { outgoing[i] = new(); incoming[i] = new(); }
        foreach (var element in project.Elements) kinds[index[element.Id]] = element.Kind;
        var edges = new List<GraphEdge>();
        foreach (var element in project.Elements)
        {
            var from = index[element.Id];
            foreach (var dependency in element.Dependencies.Distinct().Order())
            {
                if (!index.TryGetValue(dependency, out var to)) throw new ArgumentException("missing_dependency");
                var edge = new GraphEdge(element.Id, dependency, Classify(element.Kind, kinds[to]));
                edges.Add(edge); outgoing[from].Add(edge); incoming[to].Add(edge);
            }
        }
        edges.Sort(CompareEdges);
        foreach (var list in incoming) list.Sort((a, b) => a.Dependent.CompareTo(b.Dependent));
        return new(index, ids, kinds, outgoing, incoming, edges);
    }

    static int CompareEdges(GraphEdge a, GraphEdge b)
    {
        var order = a.Dependent.CompareTo(b.Dependent);
        return order != 0 ? order : a.Prerequisite.CompareTo(b.Prerequisite);
    }

    int Index(Guid id) => index.TryGetValue(id, out var i) ? i : throw new ArgumentException("unknown_element");

    static void CheckDirection(GraphDirection direction)
    {
        if (!Enum.IsDefined(direction)) throw new ArgumentException("invalid_direction");
    }

    public bool Contains(Guid id) => index.ContainsKey(id);

    public string KindOf(Guid id) => elementKinds[Index(id)];

    // Edges touching an element, optionally of one kind, ordered by Dependent then Prerequisite.
    public IReadOnlyList<GraphEdge> EdgesOf(Guid id, GraphDirection direction, GraphEdgeKind? kind = null)
    {
        var i = Index(id);
        CheckDirection(direction);
        IEnumerable<GraphEdge> edges = direction switch
        {
            GraphDirection.Prerequisites => outgoing[i],
            GraphDirection.Dependents => incoming[i],
            _ => outgoing[i].Concat(incoming[i]).Distinct(),
        };
        if (kind is GraphEdgeKind only) edges = edges.Where(edge => edge.Kind == only);
        var result = edges.ToList();
        if (direction == GraphDirection.Both) result.Sort(CompareEdges);
        return result.AsReadOnly();
    }

    // Directly connected elements, optionally through one edge kind, in Guid order without repeats.
    public IReadOnlyList<Guid> Neighbours(Guid id, GraphDirection direction, GraphEdgeKind? kind = null)
    {
        var i = Index(id);
        CheckDirection(direction);
        var result = new List<Guid>();
        if (direction != GraphDirection.Dependents)
            foreach (var edge in outgoing[i]) if (kind is null || edge.Kind == kind) result.Add(edge.Prerequisite);
        if (direction != GraphDirection.Prerequisites)
            foreach (var edge in incoming[i]) if (kind is null || edge.Kind == kind) result.Add(edge.Dependent);
        if (direction == GraphDirection.Both) result = result.Distinct().Order().ToList();
        return result.AsReadOnly();
    }

    // Elements placed on a level.
    public IReadOnlyList<Guid> OnLevel(Guid levelId) => Neighbours(levelId, GraphDirection.Dependents, GraphEdgeKind.OnLevel);
    // Levels an element is placed on.
    public IReadOnlyList<Guid> LevelOf(Guid id) => Neighbours(id, GraphDirection.Prerequisites, GraphEdgeKind.OnLevel);
    // Doors, windows and openings a wall hosts.
    public IReadOnlyList<Guid> Hosts(Guid wallId) => Neighbours(wallId, GraphDirection.Dependents, GraphEdgeKind.HostedBy);
    // The hosting wall, or null; if several walls are named, the lowest Guid.
    public Guid? HostOf(Guid id)
    {
        var hosts = Neighbours(id, GraphDirection.Prerequisites, GraphEdgeKind.HostedBy);
        return hosts.Count == 0 ? null : hosts[0];
    }
    // Walls that bound a space/room.
    public IReadOnlyList<Guid> Boundaries(Guid spaceId) => Neighbours(spaceId, GraphDirection.Prerequisites, GraphEdgeKind.Bounds);
    // Spaces/rooms a wall bounds.
    public IReadOnlyList<Guid> Bounded(Guid wallId) => Neighbours(wallId, GraphDirection.Dependents, GraphEdgeKind.Bounds);

    // Transitive closure from the seeds, breadth-first and iterative (no recursion, so arbitrarily deep chains work).
    // Deterministic order: by distance from the seeds, then Guid order within each distance; seeds come first.
    // kinds restricts which edge kinds are followed (null follows all; empty follows none).
    public IReadOnlyList<Guid> Closure(IEnumerable<Guid> seeds, GraphDirection direction, IEnumerable<GraphEdgeKind>? kinds = null, bool includeSeeds = true)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        CheckDirection(direction);
        var allowed = kinds is null ? null : new HashSet<GraphEdgeKind>(kinds);
        var visited = new bool[ids.Length];
        var layer = new List<int>();
        foreach (var seed in seeds)
        {
            var i = Index(seed);
            if (!visited[i]) { visited[i] = true; layer.Add(i); }
        }
        layer.Sort();
        var seedCount = layer.Count;
        var result = new List<Guid>();
        while (layer.Count > 0)
        {
            var next = new List<int>();
            foreach (var i in layer)
            {
                result.Add(ids[i]);
                if (direction != GraphDirection.Dependents)
                    foreach (var edge in outgoing[i]) Discover(edge, edge.Prerequisite);
                if (direction != GraphDirection.Prerequisites)
                    foreach (var edge in incoming[i]) Discover(edge, edge.Dependent);
            }
            next.Sort();
            layer = next;

            void Discover(GraphEdge edge, Guid other)
            {
                if (allowed is not null && !allowed.Contains(edge.Kind)) return;
                var j = index[other];
                if (!visited[j]) { visited[j] = true; next.Add(j); }
            }
        }
        return (includeSeeds ? result : result.GetRange(seedCount, result.Count - seedCount)).AsReadOnly();
    }

    // Everything affected when the given elements change: the seeds and all transitive dependents
    // (the same set as the Compiler.Diff dirty closure, in closure order).
    public IReadOnlyList<Guid> Affected(params Guid[] changed) => Closure(changed, GraphDirection.Dependents);

    // Everything an element transitively requires, excluding the element itself.
    public IReadOnlyList<Guid> Prerequisites(Guid id) => Closure(new[] { id }, GraphDirection.Prerequisites, includeSeeds: false);
}
