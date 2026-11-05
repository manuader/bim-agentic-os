using System.Globalization;
using Bim.Core;
namespace Bim.Generative;

// Stagger snap (P7-08): an opt-in pre-pass that puts room edges whose jog is thinner than the wall thickness onto one
// common line, so the plan reads ordered and FacadeIrBuilder.Prototype (IntentCompiler, walls of that thickness centred on
// the room edges) no longer refuses it with wall_clash. Steps, level by level:
// 1. Refuse invalid options (a non-positive or non-finite wall thickness, a planner rule with a negative or non-finite
//    minimum: prototype_snap_options_invalid). A plan that is not feasible or has no rooms is returned unchanged.
// 2. For each axis (x: west/east edges, spans along y; y: south/north edges, spans along x), join two parallel edges of
//    different rooms when their lines are closer than the wall thickness (equal lines included, so a shared edge moves
//    with both rooms) and their spans come within the wall thickness of each other: exactly the pairs whose wall bodies
//    would overlap. Every joined group moves onto one line: a lot edge (0 or the footprint side) when the group holds
//    one (a line within one 1/8192 m grid step of 0 or of the footprint side, since planned rooms end on the grid floor
//    of a lot side such as 8.66 m; the closest to the lot edge), else the line carrying the longest total edge length,
//    ties to the lower coordinate. Both axes are repeated until a round moves nothing: at most MaxRounds moving rounds,
//    each checked by the next. Targets are existing coordinates, so snapped rectangles stay on the planner's 1/8192 m
//    grid and inside the lot.
// 3. Refuse typed, keeping the plan as it was (refusals sorted by code, then subject): a room whose two opposite edges
//    would meet (prototype_snap_collapsed); an edge moved by at least the wall thickness, which a chain of joined jogs can
//    cause (prototype_snap_move_too_large); a room outside the footprint, or rooms overlapping, after the snap
//    (prototype_snap_outside_lot, prototype_snap_overlap: targets are existing lines and every edge inside a moved band
//    is joined, so both are reachable only when the given plan already leaves its footprint or overlaps); a room the
//    snap shrinks below its minimum area or side (brief minimum raised by the matching planner rules: the CABA rules of
//    PrototypeTemplates.Rules for the plan's footprint unless options give rules) or grows above its brief maximum, the
//    total area grown above the budget, a required pair's shared wall shrunk below the door width or a forbidden pair's
//    grown above zero (prototype_snap_noncompliant; a room already below a minimum may grow but not shrink); no fixed
//    point, or IntentCompiler still reporting wall_clash on the snapped plan (prototype_snap_unresolved: a safeguard, as
//    every pair of parallel wall bodies that could overlap is joined; no known plan reaches it).
// 4. A plan where nothing moved is Unchanged (the same object). A snapped plan keeps every field and constraint of the
//    original and its room order; moved rooms get new rectangles and one soft, satisfied prototype_snap_moved constraint
//    each, listing the edges moved. The result depends only on the plan's geometry, the brief and the options (not on
//    room order or culture).
// Gaps: the original constraints and score are not recomputed (their area details describe the plan before the snap).
// No ranker reads SpacePlan.Score; AlternativeRanker's compliance reads the hard constraints, which all still hold since
// the snap refuses any regression of them. Edges are only moved onto existing lines, never onto a new modular grid.
public enum PrototypeSnapStatus { Unchanged, Snapped, Refused }

// Rules are the minimum-dimension rules the snapped rooms must keep; null means PrototypeTemplates.Rules of the plan's
// footprint (PrototypeTemplates.CabaRules when the plan has no footprint).
public sealed record PrototypeSnapOptions(PlannerRules? Rules = null);

// One moved edge: the room instance, its level, the edge (west, east, south or north) and its line before and after.
public sealed record PrototypeSnapMove(string Instance, int Level, string Edge, double FromMetres, double ToMetres);

// Plan is the snapped plan (Snapped) or the plan as given (Unchanged, Refused); Moves lists every moved edge in plan room
// order (west, east, south, north); Refusals are the typed reasons of a Refused snap.
public sealed record PrototypeSnapResult(PrototypeSnapStatus Status, SpacePlan Plan, IReadOnlyList<PrototypeSnapMove> Moves, IReadOnlyList<PlanConstraint> Refusals);

public static class PrototypeSnapCodes
{
    // Soft, satisfied constraint of a snapped plan: the room's moved edges.
    public const string Moved = "prototype_snap_moved";
    public const string OptionsInvalid = "prototype_snap_options_invalid";
    public const string Collapsed = "prototype_snap_collapsed";
    public const string MoveTooLarge = "prototype_snap_move_too_large";
    public const string OutsideLot = "prototype_snap_outside_lot";
    public const string Overlap = "prototype_snap_overlap";
    public const string Noncompliant = "prototype_snap_noncompliant";
    public const string Unresolved = "prototype_snap_unresolved";
}

public static class PrototypeSnap
{
    public const int MaxRounds = 8;
    const double Epsilon = 1e-7;
    const double TieTolerance = 1e-9;
    // One step of the planner's 1/8192 m grid: rooms on a lot side end on its grid floor (8.66 m lots end at 70942/8192 m).
    const double GridStep = 1.0 / 8192;

    // Plan-independent refusals of the options (the planner rules); the wall thickness is checked by Snap.
    public static IReadOnlyList<PlanConstraint> OptionRefusals(PrototypeSnapOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var refusals = new List<PlanConstraint>();
        if (options.Rules is { } given)
        {
            if (given.Rules is null || given.Rules.Any(rule => rule is null)) refusals.Add(Refusal(PrototypeSnapCodes.OptionsInvalid, "snap.rules", "rules must be present"));
            else
                for (var index = 0; index < given.Rules.Count; index++)
                {
                    var rule = given.Rules[index];
                    if (rule.MinAreaSquareMetres is { } area && !(double.IsFinite(area) && area >= 0) || rule.MinSideMetres is { } side && !(double.IsFinite(side) && side >= 0))
                        refusals.Add(Refusal(PrototypeSnapCodes.OptionsInvalid, "snap.rules[" + Text(index) + "]", "minimum area and side must be finite and not negative"));
                }
        }
        return refusals;
    }

    public static PrototypeSnapResult Snap(ProjectBrief brief, SpacePlan plan, FacadeIrOptions? ir = null, PrototypeSnapOptions? options = null) =>
        Snap(brief, plan, ir, options, MaxRounds, null);

    // Test seam: the number of moving rounds allowed and the IntentCompiler check of the snapped plan (null: the real one).
    internal static PrototypeSnapResult Snap(ProjectBrief brief, SpacePlan plan, FacadeIrOptions? ir, PrototypeSnapOptions? options, int maxRounds,
        Func<SpacePlan, IReadOnlyList<IntentIssue>>? validate)
    {
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(plan);
        ir ??= new FacadeIrOptions();
        options ??= new PrototypeSnapOptions();
        var noMoves = Array.Empty<PrototypeSnapMove>();
        var refusals = OptionRefusals(options).ToList();
        var thickness = ir.WallThicknessMetres;
        if (!(double.IsFinite(thickness) && thickness > 0)) refusals.Add(Refusal(PrototypeSnapCodes.OptionsInvalid, "wall_thickness_m", "wall thickness must be finite and positive"));
        if (refusals.Count > 0) return Refuse(plan, refusals);
        if (!plan.IsFeasible || plan.Rooms is null || plan.Rooms.Count == 0 || plan.Rooms.Any(room => room is null || room.Rect is null || room.InstanceId is null))
            return new PrototypeSnapResult(PrototypeSnapStatus.Unchanged, plan, noMoves, Array.Empty<PlanConstraint>());

        var rooms = plan.Rooms;
        var count = rooms.Count;
        var levels = rooms.Select(room => room.Level).ToArray();
        var west = rooms.Select(room => room.Rect.X).ToArray();
        var east = rooms.Select(room => room.Rect.Right).ToArray();
        var south = rooms.Select(room => room.Rect.Y).ToArray();
        var north = rooms.Select(room => room.Rect.Top).ToArray();
        double? width = plan.Footprint?.WidthMetres, depth = plan.Footprint?.DepthMetres;
        var collapsed = new SortedSet<int>();
        var converged = false;
        // Rounds 0..maxRounds-1 may move edges; round maxRounds only checks the fixed point.
        for (var round = 0; round <= maxRounds && collapsed.Count == 0; round++)
        {
            var movedX = SnapAxis(levels, west, east, south, north, thickness, width, collapsed);
            var movedY = collapsed.Count == 0 && SnapAxis(levels, south, north, west, east, thickness, depth, collapsed);
            if (!movedX && !movedY && collapsed.Count == 0) { converged = true; break; }
        }
        if (collapsed.Count > 0)
            return Refuse(plan, collapsed.Select(i => Refusal(PrototypeSnapCodes.Collapsed, rooms[i].InstanceId,
                "two opposite edges of " + rooms[i].InstanceId + " are joined to one line: the room is thinner than the " + Format(thickness) + " m wall")));
        if (!converged)
            return Refuse(plan, new[] { Refusal(PrototypeSnapCodes.Unresolved, "plan", "the snap moved edges in each of " + Text(maxRounds) + " rounds without reaching a fixed point") });

        var moves = new List<PrototypeSnapMove>();
        var snappedRooms = new PlannedRoom[count];
        var movedConstraints = new List<PlanConstraint>();
        for (var i = 0; i < count; i++)
        {
            var room = rooms[i];
            var r = room.Rect;
            var roomMoves = new List<PrototypeSnapMove>();
            void Moved(string edge, double from, double to) { if (from != to) roomMoves.Add(new PrototypeSnapMove(room.InstanceId, room.Level, edge, from, to)); }
            Moved("west", r.X, west[i]);
            Moved("east", r.Right, east[i]);
            Moved("south", r.Y, south[i]);
            Moved("north", r.Top, north[i]);
            if (roomMoves.Count == 0) { snappedRooms[i] = room; continue; }
            snappedRooms[i] = room with { Rect = new PlanRect(west[i], south[i], east[i] - west[i], north[i] - south[i]) };
            moves.AddRange(roomMoves);
            movedConstraints.Add(new PlanConstraint(PrototypeSnapCodes.Moved, room.InstanceId, false, true,
                string.Join(", ", roomMoves.Select(move => move.Edge + " edge " + Format(move.FromMetres) + " -> " + Format(move.ToMetres) + " m"))));
        }
        if (moves.Count == 0) return new PrototypeSnapResult(PrototypeSnapStatus.Unchanged, plan, noMoves, Array.Empty<PlanConstraint>());

        var problems = new List<PlanConstraint>();
        foreach (var move in moves.Where(move => !(Math.Abs(move.ToMetres - move.FromMetres) < thickness)))
            problems.Add(Refusal(PrototypeSnapCodes.MoveTooLarge, move.Instance, "the " + move.Edge + " edge would move " + Format(Math.Abs(move.ToMetres - move.FromMetres))
                + " m (" + Format(move.FromMetres) + " -> " + Format(move.ToMetres) + "), not less than the " + Format(thickness) + " m wall: a chain of jogs"));
        for (var i = 0; i < count; i++)
        {
            var rect = snappedRooms[i].Rect;
            if (rect.Width <= 0 || rect.Depth <= 0)
                problems.Add(Refusal(PrototypeSnapCodes.Collapsed, rooms[i].InstanceId, "snapped rectangle " + Describe(rect) + " has no area"));
            else if (width is { } w && depth is { } d && (rect.X < -Epsilon || rect.Y < -Epsilon || rect.Right > w + Epsilon || rect.Top > d + Epsilon))
                problems.Add(Refusal(PrototypeSnapCodes.OutsideLot, rooms[i].InstanceId, "snapped rectangle " + Describe(rect) + " leaves the " + Format(w) + " x " + Format(d) + " m footprint"));
        }
        for (var i = 0; i < count; i++)
            for (var j = i + 1; j < count; j++)
                if (levels[i] == levels[j] && Overlap(snappedRooms[i].Rect, snappedRooms[j].Rect))
                    problems.Add(Refusal(PrototypeSnapCodes.Overlap, Pair(rooms[i].InstanceId, rooms[j].InstanceId), "snapped rectangles " + Describe(snappedRooms[i].Rect)
                        + " and " + Describe(snappedRooms[j].Rect) + " overlap"));
        var rules = (options.Rules ?? (plan.Footprint is { } footprint ? PrototypeTemplates.Rules(new PrototypeLot(footprint.WidthMetres, footprint.DepthMetres)) : PrototypeTemplates.CabaRules)).Rules;
        for (var i = 0; i < count; i++)
        {
            var before = rooms[i].Rect;
            var after = snappedRooms[i].Rect;
            if (ReferenceEquals(before, after)) continue;
            var spec = brief.Rooms?.FirstOrDefault(candidate => candidate is not null && candidate.Id == rooms[i].RoomId);
            var (minArea, minSide, source) = Minimums(rooms[i].RoomId, spec?.Name ?? rooms[i].Name ?? rooms[i].RoomId, spec?.MinAreaSquareMetres?.Value, rules);
            var subject = rooms[i].InstanceId;
            if (after.Area < minArea && after.Area < before.Area)
                problems.Add(Refusal(PrototypeSnapCodes.Noncompliant, subject, "area " + Format(before.Area) + " -> " + Format(after.Area) + " m2, below minimum " + Format(minArea) + " m2 (" + source + ")"));
            var shortBefore = Math.Min(before.Width, before.Depth);
            var shortAfter = Math.Min(after.Width, after.Depth);
            if (shortAfter < minSide && shortAfter < shortBefore)
                problems.Add(Refusal(PrototypeSnapCodes.Noncompliant, subject, "short side " + Format(shortBefore) + " -> " + Format(shortAfter) + " m, below minimum " + Format(minSide) + " m (" + source + ")"));
            if (spec?.MaxAreaSquareMetres is { } max && after.Area > max.Value + SpacePlanner.AreaToleranceSquareMetres && after.Area > before.Area)
                problems.Add(Refusal(PrototypeSnapCodes.Noncompliant, subject, "area " + Format(before.Area) + " -> " + Format(after.Area) + " m2, above maximum " + Format(max.Value) + " m2"));
        }
        if (brief.Constraints?.TotalAreaBudgetSquareMetres is { } budget)
        {
            var totalBefore = rooms.Sum(room => room.Rect.Area);
            var totalAfter = snappedRooms.Sum(room => room.Rect.Area);
            var limit = budget.Value + SpacePlanner.AreaToleranceSquareMetres;
            if (totalAfter > limit && totalAfter > totalBefore)
                problems.Add(Refusal(PrototypeSnapCodes.Noncompliant, "plan", "total area " + Format(totalBefore) + " -> " + Format(totalAfter) + " m2, above the " + Format(budget.Value) + " m2 budget"));
        }
        var seen = new HashSet<(string, string, BriefAdjacencyKind)>();
        foreach (var adjacency in brief.Adjacencies ?? Array.Empty<BriefAdjacency>())
        {
            if (adjacency is null || adjacency.Kind == BriefAdjacencyKind.Preferred || adjacency.A is null || adjacency.B is null) continue;
            var key = string.CompareOrdinal(adjacency.A, adjacency.B) < 0 ? (adjacency.A, adjacency.B, adjacency.Kind) : (adjacency.B, adjacency.A, adjacency.Kind);
            if (!seen.Add(key)) continue;
            var sharedBefore = LongestShared(rooms, adjacency.A, adjacency.B);
            var sharedAfter = LongestShared(snappedRooms, adjacency.A, adjacency.B);
            var subject = adjacency.A + "/" + adjacency.B;
            if (adjacency.Kind == BriefAdjacencyKind.Required && sharedAfter < plan.DoorWidthMetres && sharedAfter < sharedBefore)
                problems.Add(Refusal(PrototypeSnapCodes.Noncompliant, subject, "required pair shares " + Format(sharedBefore) + " -> " + Format(sharedAfter) + " m of wall, below the "
                    + Format(plan.DoorWidthMetres) + " m door width"));
            if (adjacency.Kind == BriefAdjacencyKind.Forbidden && sharedAfter > Epsilon && sharedAfter > sharedBefore)
                problems.Add(Refusal(PrototypeSnapCodes.Noncompliant, subject, "forbidden pair shares " + Format(sharedAfter) + " m of wall after the snap"));
        }
        if (problems.Count > 0) return Refuse(plan, problems);

        var snapped = plan with { Rooms = snappedRooms, Constraints = (plan.Constraints ?? Array.Empty<PlanConstraint>()).Concat(movedConstraints).ToArray() };
        IReadOnlyList<IntentIssue> issues;
        try { issues = validate is null ? IntentCompiler.Validate(FacadeIrBuilder.Intent(snapped, ir)) : validate(snapped); }
        catch (ArgumentException) { issues = Array.Empty<IntentIssue>(); } // not a stagger problem: the BIM-IR gate reports it
        var clashes = issues.Where(issue => issue.Code == IntentCodes.WallClash).ToArray();
        if (clashes.Length > 0)
            return Refuse(plan, clashes.Select(issue => Refusal(PrototypeSnapCodes.Unresolved, issue.Key, "IntentCompiler still reports wall_clash after the snap: " + issue.Detail)));
        return new PrototypeSnapResult(PrototypeSnapStatus.Snapped, snapped, moves, Array.Empty<PlanConstraint>());
    }

    // One axis: low[i] and high[i] are the coordinates of room i's two edges across the axis, spanLow/spanHigh its extent
    // along it. Joins the edges as described above and moves every group onto its target line. Returns whether anything
    // moved; adds to collapsed every room whose two edges end up in one group (nothing moves then).
    static bool SnapAxis(int[] levels, double[] low, double[] high, double[] spanLow, double[] spanHigh, double thickness, double? bound, SortedSet<int> collapsed)
    {
        var count = levels.Length;
        double Value(int node) => node % 2 == 0 ? low[node / 2] : high[node / 2];
        var parent = Enumerable.Range(0, 2 * count).ToArray();
        int Find(int node)
        {
            while (parent[node] != node) { parent[node] = parent[parent[node]]; node = parent[node]; }
            return node;
        }
        for (var i = 0; i < count; i++)
            for (var j = i + 1; j < count; j++)
            {
                if (levels[i] != levels[j]) continue;
                if (Math.Min(spanHigh[i], spanHigh[j]) - Math.Max(spanLow[i], spanLow[j]) <= -thickness) continue;
                for (var p = 2 * i; p < 2 * i + 2; p++)
                    for (var q = 2 * j; q < 2 * j + 2; q++)
                    {
                        if (!(Math.Abs(Value(p) - Value(q)) < thickness)) continue;
                        var (rp, rq) = (Find(p), Find(q));
                        if (rp != rq) parent[Math.Max(rp, rq)] = Math.Min(rp, rq);
                    }
            }
        var groups = Enumerable.Range(0, 2 * count).GroupBy(Find).Select(group => group.ToArray()).Where(group => group.Select(Value).Distinct().Count() > 1).ToArray();
        var found = false;
        foreach (var group in groups)
            foreach (var node in group)
                if (node % 2 == 0 && group.Contains(node + 1)) { collapsed.Add(node / 2); found = true; }
        if (found) return false;
        var moved = false;
        foreach (var group in groups)
        {
            var target = Target(group.Select(node => (Value(node), spanHigh[node / 2] - spanLow[node / 2])).ToArray(), bound);
            foreach (var node in group)
            {
                if (Value(node) == target) continue;
                if (node % 2 == 0) low[node / 2] = target;
                else high[node / 2] = target;
                moved = true;
            }
        }
        return moved;
    }

    // A lot edge held by the group (a line within one grid step of 0 or of the footprint side; the closest to it, the
    // lower on a tie), else the line with the longest total edge length (summed in a fixed order), ties within 1e-9 m to
    // the lower coordinate.
    static double Target((double Line, double Length)[] edges, double? bound)
    {
        var lines = edges.Select(edge => edge.Line).Distinct().Order().ToArray();
        double? onEdge = null;
        var nearest = double.PositiveInfinity;
        foreach (var line in lines)
        {
            var distance = Math.Min(Math.Abs(line), bound is { } side ? Math.Abs(line - side) : double.PositiveInfinity);
            if (distance <= GridStep && distance < nearest) { onEdge = line; nearest = distance; }
        }
        if (onEdge is { } lotLine) return lotLine;
        var best = lines[0];
        var bestLength = double.NegativeInfinity;
        foreach (var line in lines)
        {
            var length = edges.Where(edge => edge.Line == line).Select(edge => edge.Length).Order().Sum();
            if (length > bestLength + TieTolerance) { best = line; bestLength = length; }
        }
        return best;
    }

    // Longest shared edge between any instance of room a and any instance of room b on one level (0 when none).
    static double LongestShared(IReadOnlyList<PlannedRoom> rooms, string a, string b)
    {
        double longest = 0;
        foreach (var p in rooms.Where(room => room.RoomId == a))
            foreach (var q in rooms.Where(room => room.RoomId == b && !ReferenceEquals(room, p) && room.Level == p.Level))
                longest = Math.Max(longest, Shared(p.Rect, q.Rect));
        return longest;
    }

    static double Shared(PlanRect a, PlanRect b)
    {
        if (Math.Abs(a.Right - b.X) < Epsilon || Math.Abs(b.Right - a.X) < Epsilon) return Math.Max(0, Math.Min(a.Top, b.Top) - Math.Max(a.Y, b.Y));
        if (Math.Abs(a.Top - b.Y) < Epsilon || Math.Abs(b.Top - a.Y) < Epsilon) return Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X));
        return 0;
    }

    static bool Overlap(PlanRect a, PlanRect b) => Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X) > Epsilon && Math.Min(a.Top, b.Top) - Math.Max(a.Y, b.Y) > Epsilon;

    // The planner's effective minimums (as PrototypeGenerator): brief minimum area raised by every matching rule's minimum
    // area and minimum side squared; minimum side the largest matching rule side.
    static (double Area, double Side, string Source) Minimums(string roomId, string name, double? briefMinimum, IReadOnlyList<PlannerRule> rules)
    {
        var minArea = briefMinimum ?? 0;
        double minSide = 0;
        var sources = new List<string> { "brief" };
        foreach (var rule in rules)
        {
            if (!((rule.RoomId is null || string.Equals(rule.RoomId, roomId, StringComparison.Ordinal)) && (rule.NamePattern is null || Glob(rule.NamePattern, name)))) continue;
            if (rule.MinAreaSquareMetres is { } area && area > minArea) minArea = area;
            if (rule.MinSideMetres is { } side && side > minSide) minSide = side;
            if (rule.Reference is { } reference) sources.Add(reference);
        }
        if (minSide * minSide > minArea) minArea = minSide * minSide;
        return (minArea, minSide, string.Join("; ", sources));
    }

    // Case-insensitive wildcard match, as SpacePlanner: '*' any run (possibly empty), '?' exactly one character.
    static bool Glob(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t]))) { p++; t++; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    static PrototypeSnapResult Refuse(SpacePlan plan, IEnumerable<PlanConstraint> refusals) =>
        new(PrototypeSnapStatus.Refused, plan, Array.Empty<PrototypeSnapMove>(), refusals
            .OrderBy(refusal => refusal.Code, StringComparer.Ordinal).ThenBy(refusal => refusal.Subject, StringComparer.Ordinal).ThenBy(refusal => refusal.Detail, StringComparer.Ordinal).ToArray());

    static PlanConstraint Refusal(string code, string subject, string detail) => new(code, subject, true, false, detail);

    static string Pair(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? a + "/" + b : b + "/" + a;

    static string Describe(PlanRect rect) => Format(rect.X) + "," + Format(rect.Y) + " " + Format(rect.Width) + " x " + Format(rect.Depth) + " m";

    static string Format(double value) => double.IsFinite(value) ? SpacePlanSerializer.Format(value) : "non-finite";

    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
