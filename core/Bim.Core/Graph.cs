namespace Bim.Core;

// Red stub for P2-02 TDD: compiles with the final public surface, answers wrongly.
public enum GraphEdgeKind { DependsOn, OnLevel, HostedBy, Bounds }
public enum GraphDirection { Prerequisites, Dependents, Both }
public sealed record GraphEdge(Guid Dependent, Guid Prerequisite, GraphEdgeKind Kind);

public sealed class SemanticGraph
{
    static readonly IReadOnlyList<Guid> NoIds = Array.Empty<Guid>();
    public IReadOnlyList<Guid> Nodes { get; } = NoIds;
    public IReadOnlyList<GraphEdge> Edges { get; } = Array.Empty<GraphEdge>();
    public static GraphEdgeKind Classify(string dependentKind, string prerequisiteKind) => dependentKind.Length + prerequisiteKind.Length > 0 ? GraphEdgeKind.DependsOn : GraphEdgeKind.Bounds;
    public static SemanticGraph Build(Project project) { ArgumentNullException.ThrowIfNull(project); return new(); }
    public bool Contains(Guid id) => id == Guid.Empty;
    public string KindOf(Guid id) => id.ToString();
    public IReadOnlyList<GraphEdge> EdgesOf(Guid id, GraphDirection direction, GraphEdgeKind? kind = null) => Edges;
    public IReadOnlyList<Guid> Neighbours(Guid id, GraphDirection direction, GraphEdgeKind? kind = null) => NoIds;
    public IReadOnlyList<Guid> OnLevel(Guid levelId) => NoIds;
    public IReadOnlyList<Guid> LevelOf(Guid id) => NoIds;
    public IReadOnlyList<Guid> Hosts(Guid wallId) => NoIds;
    public Guid? HostOf(Guid id) => null;
    public IReadOnlyList<Guid> Boundaries(Guid spaceId) => NoIds;
    public IReadOnlyList<Guid> Bounded(Guid wallId) => NoIds;
    public IReadOnlyList<Guid> Closure(IEnumerable<Guid> seeds, GraphDirection direction, IEnumerable<GraphEdgeKind>? kinds = null, bool includeSeeds = true) => NoIds;
    public IReadOnlyList<Guid> Affected(params Guid[] changed) => NoIds;
    public IReadOnlyList<Guid> Prerequisites(Guid id) => NoIds;
}
