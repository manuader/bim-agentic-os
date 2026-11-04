using System.Text.Json;
namespace Bim.Core;

// P3-04 localized validation. Findings is sorted like LocalValidation.ValidateAll; Affected is plan.DirtyIds
// (every created, updated or deleted element and its transitive dependents in before or after); Revalidated is
// the part of Affected present in after, i.e. the only elements whose rules were evaluated. Returned beside the
// plan, never inside plan JSON, so plan bytes and hashes are unaffected.
public sealed record LocalValidationResult(ValidationIssue[] Findings, Guid[] Affected, Guid[] Revalidated, int TotalElements)
{
    public int RevalidatedCount => Revalidated.Length;
}

// Element rules over a Compiler.Validate-clean project. Every finding names one element.
// INVARIANT (soundness of localization): the rules of an element may read only that element and its
// Prerequisites closure in after (today: its prerequisites and their prerequisites: kinds, geometry, levels),
// never its dependents (SemanticGraph.Hosts, Bounded, OnLevel, Affected or any Dependents traversal) and never
// another unrelated element. A change to anything in the Prerequisites closure is a plan operation the element
// transitively depends on in after, so the element is in plan.DirtyIds. Hence:
// Merge(ValidateAll(before), Validate(before, after, plan)) == ValidateAll(after). A rule that reads dependents
// (for example "a wall hosts at most N doors") would break this and must not be added here.
//   geometry codes    wall/level/space/plan/schedule/sheet geometry that GeometryCodec cannot decode
//   host_missing      door/window/opening with no hosting wall
//   host_ambiguous    ... with more than one hosting wall
//   hosted_geometry   ... whose geometry is not [offset, width] in metres along the host axis
//   host_extent       ... not within its host wall (offset >= 0, width > 0, offset + width <= wall length)
//   host_level        ... on a level other than its host wall's (when both declare one)
//   boundary_detached a bounding wall's footprint (axis widened by half its thickness) misses the space rectangle
//   boundary_level    a space/room and its bounding walls declare different levels
// Levels compare as sets: an element's level key is all levels it depends on (Guid order, joined), so an element on
// several levels matches only an element on exactly the same levels; an element on no level is not compared.
public static class LocalValidation
{
    public const string HostMissing = "host_missing";
    public const string HostAmbiguous = "host_ambiguous";
    public const string HostedGeometry = "hosted_geometry";
    public const string HostExtent = "host_extent";
    public const string HostLevel = "host_level";
    public const string BoundaryDetached = "boundary_detached";
    public const string BoundaryLevel = "boundary_level";
    // Absorbs rounding where RoomCompiler walls meet the space exactly.
    const double Tolerance = 1e-6;

    // Full validation: the rules on every element, sorted by element id, code, then message (ordinal).
    public static ValidationIssue[] ValidateAll(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (Compiler.Validate(project).Length > 0) throw new InvalidOperationException("invalid_ir");
        var graph = SemanticGraph.Build(project);
        return Run(graph, project.Elements.ToDictionary(element => element.Id), graph.Nodes);
    }

    // Localized validation: the rules only on the affected closure present in after.
    // The plan must be the deterministic diff of before and after (same acceptance rule as SemanticDiff).
    public static LocalValidationResult Validate(Project before, Project after, ExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(plan);
        if (JsonSerializer.Serialize(Compiler.Diff(before, after)) != JsonSerializer.Serialize(plan)) throw new InvalidOperationException("plan_mismatch");
        var elements = after.Elements.ToDictionary(element => element.Id);
        var affected = plan.DirtyIds.Order().ToArray();
        var revalidated = affected.Where(elements.ContainsKey).ToArray();
        return new(Run(SemanticGraph.Build(after), elements, revalidated), affected, revalidated, after.Elements.Length);
    }

    // Prior findings of elements outside the affected closure, plus the localized findings, sorted like ValidateAll.
    // previous must be ValidateAll(before) for the same before the local result came from; any other input gives a
    // merged list with no guarantee (it is not checked here; Agrees recomputes it). Prior findings without an
    // element id are dropped (a project Compiler.Diff accepts has none). Revalidated is a subset of Affected.
    public static ValidationIssue[] Merge(IEnumerable<ValidationIssue> previous, LocalValidationResult local)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(local);
        var replaced = local.Affected.ToHashSet();
        return Sort(previous.Where(issue => issue.Id is Guid id && !replaced.Contains(id)).Concat(local.Findings));
    }

    // Proof helper: localized validation merged with full validation of before equals full validation of after.
    public static bool Agrees(Project before, Project after, ExecutionPlan plan)
    {
        var local = Validate(before, after, plan);
        return Merge(ValidateAll(before), local).SequenceEqual(ValidateAll(after));
    }

    static ValidationIssue[] Sort(IEnumerable<ValidationIssue> issues) =>
        issues.OrderBy(issue => issue.Id).ThenBy(issue => issue.Code, StringComparer.Ordinal).ThenBy(issue => issue.Message, StringComparer.Ordinal).ToArray();

    static ValidationIssue[] Run(SemanticGraph graph, Dictionary<Guid, Element> elements, IEnumerable<Guid> ids)
    {
        var issues = new List<ValidationIssue>();
        foreach (var id in ids) Rules(graph, elements, elements[id], issues);
        return Sort(issues);
    }

    static void Rules(SemanticGraph graph, Dictionary<Guid, Element> elements, Element element, List<ValidationIssue> issues)
    {
        if (GeometryCodec.ExpectedArity(element.Kind) is not null && GeometryCodec.Problem(element) is string problem)
            issues.Add(new(problem, element.Id, "Geometry does not decode for kind " + element.Kind));
        if (element.Kind is "door" or "window" or "opening") Hosted(graph, elements, element, issues);
        if (element.Kind is "space" or "room") Bounded(graph, elements, element, issues);
    }

    static void Hosted(SemanticGraph graph, Dictionary<Guid, Element> elements, Element element, List<ValidationIssue> issues)
    {
        var hosts = graph.Neighbours(element.Id, GraphDirection.Prerequisites, GraphEdgeKind.HostedBy);
        if (hosts.Count == 0) { issues.Add(new(HostMissing, element.Id, "No hosting wall")); return; }
        if (hosts.Count > 1) { issues.Add(new(HostAmbiguous, element.Id, "More than one hosting wall")); return; }
        var host = elements[hosts[0]];
        if (element.GeometryMetres.Length != 2) issues.Add(new(HostedGeometry, element.Id, "Hosted geometry must be [offset, width] in metres"));
        else if (GeometryCodec.Problem(host) is null)
        {
            var g = host.GeometryMetres;
            double dx = g[2] - g[0], dy = g[3] - g[1];
            var length = Math.Sqrt(dx * dx + dy * dy);
            double offset = element.GeometryMetres[0], width = element.GeometryMetres[1];
            if (!(offset >= 0 && width > 0 && offset + width <= length + Tolerance))
                issues.Add(new(HostExtent, element.Id, "Opening exceeds host wall " + host.Id.ToString("D")));
        }
        var own = LevelKey(graph, element.Id);
        var hostLevel = LevelKey(graph, host.Id);
        if (own.Length > 0 && hostLevel.Length > 0 && own != hostLevel)
            issues.Add(new(HostLevel, element.Id, "Level differs from host wall " + host.Id.ToString("D")));
    }

    static void Bounded(SemanticGraph graph, Dictionary<Guid, Element> elements, Element element, List<ValidationIssue> issues)
    {
        var levels = new HashSet<string>(StringComparer.Ordinal);
        var own = LevelKey(graph, element.Id);
        if (own.Length > 0) levels.Add(own);
        var footprint = element.Kind == GeometryCodec.Space && GeometryCodec.Problem(element) is null;
        foreach (var wallId in graph.Boundaries(element.Id))
        {
            var wall = elements[wallId];
            if (footprint && GeometryCodec.Problem(wall) is null && !Touches(element.GeometryMetres, wall.GeometryMetres))
                issues.Add(new(BoundaryDetached, element.Id, "Bounding wall " + wallId.ToString("D") + " does not touch the space"));
            var key = LevelKey(graph, wallId);
            if (key.Length > 0) levels.Add(key);
        }
        if (levels.Count > 1) issues.Add(new(BoundaryLevel, element.Id, "Space and bounding walls are on different levels"));
    }

    // Closed rectangles: space [x, x+width] x [y, y+depth]; wall axis bounding box widened by half the thickness.
    static bool Touches(double[] space, double[] wall)
    {
        double sx0 = Math.Min(space[0], space[0] + space[2]), sx1 = Math.Max(space[0], space[0] + space[2]);
        double sy0 = Math.Min(space[1], space[1] + space[3]), sy1 = Math.Max(space[1], space[1] + space[3]);
        var half = wall[5] / 2;
        double wx0 = Math.Min(wall[0], wall[2]) - half, wx1 = Math.Max(wall[0], wall[2]) + half;
        double wy0 = Math.Min(wall[1], wall[3]) - half, wy1 = Math.Max(wall[1], wall[3]) + half;
        return wx0 <= sx1 + Tolerance && sx0 <= wx1 + Tolerance && wy0 <= sy1 + Tolerance && sy0 <= wy1 + Tolerance;
    }

    static string LevelKey(SemanticGraph graph, Guid id) => string.Join(",", graph.LevelOf(id).Select(level => level.ToString("D")));
}
