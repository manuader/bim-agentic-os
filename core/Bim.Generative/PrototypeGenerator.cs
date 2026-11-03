using System.Globalization;
using Bim.Core;
namespace Bim.Generative;

// Apartment prototype generator. Steps:
// 1. Refuse an invalid request before any planning, listing every problem: undefined typology, lot side outside
//    MinLotSideMetres..MaxLotSideMetres (or non-finite), alternatives outside MinAlternatives..MaxAlternatives, door
//    width not positive and finite, invalid weights, and approvals that cannot hold: an approved room that is not a
//    template room, approved twice, off the 1/8192 m grid or not positive and finite, outside the lot, overlapping
//    another approved room, or below the brief range or the prototype minimums (PrototypeTemplates.Rules: CABA rooms,
//    CABA corridor side for the hall, template laundry side); an approved wall that is unnamed, duplicated, diagonal,
//    zero-length, off-grid or outside the lot, or more than MaxFixedWalls; and approval sets no layout can satisfy
//    (approved forbidden pair touching, approved required pair below the door width, approved area above the budget,
//    approved wall through an approved room).
// 2. Build the template brief on the lot and plan Alternatives whole_lot candidates with SpacePlanner (seeds
//    Seed + i, CABA rules, the brief site as footprint). A whole-lot plan knows nothing of the approvals; a feasible one
//    is then checked against them: an approved room whose rectangle differs in any bit (fixed_room_moved) or an
//    approved wall not covered by room edges (fixed_wall_missing) makes it infeasible, with that reason.
// 3. When rooms are approved, plan as many around_fixed candidates (seeds Seed + Alternatives + i): the approved rooms
//    keep their rectangles; the other rooms form a sub-brief (their adjacencies among themselves, the remaining area
//    budget) planned by SpacePlanner inside one of the MaxRegions largest maximal free rectangles of the lot (corners on
//    the lot edges and the approved room edges, area at least the free rooms' minimum sum), taken in turn. The
//    sub-layout's bounding box is tried at the region's four corners, each with four mirrorings (16 variants in a fixed
//    order); the first variant whose composed layout meets every hard check of the full brief wins, else the variant
//    with fewest violations is reported infeasible with its reasons. Checks of a composed layout: area range and CABA
//    minimum area and side, in lot, no overlap, area budget, required/forbidden adjacency (including pairs with an
//    approved room), approved rooms and walls; preferred adjacency is soft. All coordinates stay on the grid, so
//    translated and mirrored rectangles are exact and an approved rectangle is reproduced bit for bit.
// 4. Rank every candidate with AlternativeRanker (deduplication by geometry, six criteria, stable tie-break). The
//    planner, the ranker and every step here are deterministic and sequential, so the same request always gives the
//    same result and the same canonical JSON (PrototypeSerializer).
// Gaps: the free rooms of an around_fixed candidate share one rectangle; there are no corridors, doors or walls other
// than room edges, and only level 0 is planned.
public static class PrototypeGenerator
{
    public const int MinAlternatives = 1;
    public const int MaxAlternatives = 32;
    public const double MinLotSideMetres = 3;
    public const double MaxLotSideMetres = 100;
    public const int MaxFixedWalls = 64;
    public const int MaxRegions = 4;
    const int MaxFixedRooms = 64;
    const int MaxWallIdChars = 64;
    const double GridScale = 8192;
    const double Epsilon = 1e-7;
    static readonly string[] Corners = { "sw", "se", "nw", "ne" };
    static readonly string[] Mirrors = { "none", "x", "y", "xy" };

    public static PrototypeResult Generate(PrototypeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fixedRooms = request.FixedRooms ?? Array.Empty<FixedRoom>();
        var fixedWalls = request.FixedWalls ?? Array.Empty<FixedWall>();
        var weights = request.Weights ?? new ScoringWeights();
        var door = request.DoorWidthMetres;
        var refusals = new List<PlanConstraint>();
        var typologyValid = Enum.IsDefined(request.Typology);
        if (!typologyValid) refusals.Add(Refusal(PrototypeCodes.TypologyInvalid, "typology", "typology " + Text((int)request.Typology) + " is not 1, 2 or 3 bedrooms"));
        var lot = request.Lot;
        var lotValid = lot is not null && LotSide(lot.WidthMetres) && LotSide(lot.DepthMetres);
        if (!lotValid)
            refusals.Add(Refusal(PrototypeCodes.LotInvalid, "lot", lot is null ? "a lot is required"
                : "lot " + Format(lot.WidthMetres) + " x " + Format(lot.DepthMetres) + " m; each side must be finite and between " + Format(MinLotSideMetres) + " and " + Format(MaxLotSideMetres) + " m"));
        if (request.Alternatives < MinAlternatives || request.Alternatives > MaxAlternatives)
            refusals.Add(Refusal(PrototypeCodes.CountInvalid, "alternatives", Text(request.Alternatives) + " alternatives requested; between " + Text(MinAlternatives) + " and " + Text(MaxAlternatives)
                + " per strategy are generated"));
        if (!(double.IsFinite(door) && door > 0)) refusals.Add(Refusal(PrototypeCodes.OptionsInvalid, "door_width_m", "positive finite door width required"));
        refusals.AddRange(AlternativeRanker.Refusals(AlternativeGenerator.MinCount, weights));
        var brief = typologyValid && lotValid && lot is not null ? PrototypeTemplates.Brief(request.Typology, lot) : null;
        var plannerRules = lot is not null ? PrototypeTemplates.Rules(lot) : PrototypeTemplates.CabaRules;
        var rules = plannerRules.Rules;
        var validRooms = CheckFixedRooms(brief, lot, fixedRooms, rules, refusals);
        var validWalls = CheckFixedWalls(lot, fixedWalls, refusals);
        if (brief is not null) CheckApprovalSet(brief, door, validRooms, validWalls, refusals);
        if (refusals.Count > 0 || brief is null || lot is null)
            return new PrototypeResult(PrototypeStatus.Refused, request, brief, refusals, Array.Empty<PrototypeCandidate>(), null);

        var count = request.Alternatives;
        var plans = new List<SpacePlan>();
        var candidates = new List<PrototypeCandidate>();
        var wholeLot = new PlannerOptions(door, null, plannerRules);
        for (var index = 0; index < count; index++)
        {
            var seed = unchecked(request.Seed + index);
            var plan = SpacePlanner.Plan(brief, seed, wholeLot);
            if (plan.IsFeasible)
            {
                var approvals = Approvals(plan.Rooms, fixedRooms, fixedWalls);
                plan = approvals.Any(check => !check.Satisfied) ? Infeasible(brief, seed, lot, door, approvals)
                    : plan with { Constraints = plan.Constraints.Concat(approvals).ToArray() };
            }
            plans.Add(plan);
            candidates.Add(new PrototypeCandidate(index, PrototypeStrategies.WholeLot, seed, null, null));
        }
        if (fixedRooms.Count > 0)
        {
            var fixedIds = new HashSet<string>(fixedRooms.Select(room => room.RoomId), StringComparer.Ordinal);
            var free = brief.Rooms.Where(room => !fixedIds.Contains(room.Id)).ToArray();
            var freeMinimum = free.Sum(room => Minimums(room, rules).Area);
            var regions = Regions(lot, fixedRooms, freeMinimum);
            var sub = SubBrief(brief, free, fixedRooms);
            for (var index = 0; index < count; index++)
            {
                var seed = unchecked(request.Seed + count + index);
                var region = regions.Count == 0 ? null : regions[index % regions.Count];
                var (plan, variant) = AroundFixed(brief, sub, free.Length, region, freeMinimum, seed, lot, door, fixedRooms, fixedWalls, plannerRules);
                plans.Add(plan);
                candidates.Add(new PrototypeCandidate(count + index, PrototypeStrategies.AroundFixed, seed, free.Length == 0 ? null : region, variant));
            }
        }
        var set = AlternativeRanker.Rank(brief, plans, weights);
        var status = set.Status == AlternativeSetStatus.Ranked ? PrototypeStatus.Ranked : PrototypeStatus.Infeasible;
        return new PrototypeResult(status, request, brief, Array.Empty<PlanConstraint>(), candidates, set);
    }

    // ---- request checks ----

    // Returns the approved rooms that passed the per-room checks (template rooms only when the brief is known).
    static List<FixedRoom> CheckFixedRooms(ProjectBrief? brief, PrototypeLot? lot, IReadOnlyList<FixedRoom> fixedRooms, IReadOnlyList<PlannerRule> rules, List<PlanConstraint> refusals)
    {
        if (fixedRooms.Count > MaxFixedRooms)
        {
            refusals.Add(Refusal(PrototypeCodes.FixedRoomInvalid, "fixed_rooms", Text(fixedRooms.Count) + " approved rooms; at most " + Text(MaxFixedRooms) + " are accepted"));
            return new List<FixedRoom>();
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var valid = new List<FixedRoom>();
        for (var index = 0; index < fixedRooms.Count; index++)
        {
            var room = fixedRooms[index];
            var path = "fixed_rooms[" + Text(index) + "]";
            if (room is null || !BriefParser.IsText(room.RoomId))
            {
                refusals.Add(Refusal(PrototypeCodes.FixedRoomInvalid, path, "an approved room needs a room id"));
                continue;
            }
            var subject = room.RoomId;
            var rect = room.Rect;
            if (rect is null || !(OnGrid(rect.X) && OnGrid(rect.Y) && OnGrid(rect.Width) && OnGrid(rect.Depth) && rect.X >= 0 && rect.Y >= 0 && rect.Width > 0 && rect.Depth > 0))
            {
                refusals.Add(Refusal(PrototypeCodes.FixedRoomInvalid, subject, "the approved rectangle must be finite, non-negative, of positive size and on the 1/8192 m grid"));
                continue;
            }
            if (!seen.Add(subject))
            {
                refusals.Add(Refusal(PrototypeCodes.FixedRoomDuplicate, subject, "room approved more than once"));
                continue;
            }
            if (lot is not null && double.IsFinite(lot.WidthMetres) && double.IsFinite(lot.DepthMetres) && (rect.Right > lot.WidthMetres || rect.Top > lot.DepthMetres))
                refusals.Add(Refusal(PrototypeCodes.FixedRoomOutsideLot, subject, "approved rectangle " + Describe(rect) + " exceeds lot " + Format(lot.WidthMetres) + " x " + Format(lot.DepthMetres) + " m"));
            if (brief is not null)
            {
                var spec = brief.Rooms.FirstOrDefault(candidate => candidate.Id == subject);
                if (spec is null)
                {
                    refusals.Add(Refusal(PrototypeCodes.FixedRoomUnknown, subject, "not a room of the " + PrototypeTemplates.Code(PrototypeTypology(brief)) + " template ("
                        + string.Join(", ", brief.Rooms.Select(candidate => candidate.Id)) + ")"));
                    continue;
                }
                var (minArea, minSide, source) = Minimums(spec, rules);
                var area = rect.Area;
                var shortSide = Math.Min(rect.Width, rect.Depth);
                var problems = new List<string>();
                if (area < minArea) problems.Add("area " + Format(area) + " m2 below minimum " + Format(minArea) + " m2 (" + source + ")");
                if (spec.MaxAreaSquareMetres is { } max && area > max.Value + SpacePlanner.AreaToleranceSquareMetres) problems.Add("area " + Format(area) + " m2 above maximum " + Format(max.Value) + " m2");
                if (shortSide < minSide) problems.Add("short side " + Format(shortSide) + " m below minimum " + Format(minSide) + " m (" + source + ")");
                if (problems.Count > 0) refusals.Add(Refusal(PrototypeCodes.FixedRoomNoncompliant, subject, string.Join("; ", problems)));
            }
            valid.Add(room);
        }
        for (var i = 0; i < valid.Count; i++)
            for (var j = i + 1; j < valid.Count; j++)
                if (Overlap(valid[i].Rect, valid[j].Rect))
                    refusals.Add(Refusal(PrototypeCodes.FixedRoomOverlap, valid[i].RoomId + "/" + valid[j].RoomId, "approved rectangles " + Describe(valid[i].Rect) + " and " + Describe(valid[j].Rect) + " overlap"));
        return valid;
    }

    // Returns the approved walls that passed the per-wall checks.
    static List<FixedWall> CheckFixedWalls(PrototypeLot? lot, IReadOnlyList<FixedWall> walls, List<PlanConstraint> refusals)
    {
        var valid = new List<FixedWall>();
        if (walls.Count > MaxFixedWalls)
        {
            refusals.Add(Refusal(PrototypeCodes.FixedWallInvalid, "fixed_walls", Text(walls.Count) + " approved walls; at most " + Text(MaxFixedWalls) + " are accepted"));
            return valid;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < walls.Count; index++)
        {
            var wall = walls[index];
            var path = "fixed_walls[" + Text(index) + "]";
            if (wall is null || !BriefParser.IsText(wall.Id) || wall.Id.Length > MaxWallIdChars)
            {
                refusals.Add(Refusal(PrototypeCodes.FixedWallInvalid, path, "an approved wall needs an id of at most " + Text(MaxWallIdChars) + " characters"));
                continue;
            }
            var subject = wall.Id;
            if (!seen.Add(subject)) { refusals.Add(Refusal(PrototypeCodes.FixedWallInvalid, subject, "wall id approved more than once")); continue; }
            if (!(OnGrid(wall.X0) && OnGrid(wall.Y0) && OnGrid(wall.X1) && OnGrid(wall.Y1) && wall.X0 >= 0 && wall.Y0 >= 0 && wall.X1 >= 0 && wall.Y1 >= 0))
            {
                refusals.Add(Refusal(PrototypeCodes.FixedWallInvalid, subject, "wall ends must be finite, non-negative and on the 1/8192 m grid"));
                continue;
            }
            var vertical = wall.X0 == wall.X1;
            var horizontal = wall.Y0 == wall.Y1;
            if (vertical == horizontal)
            {
                refusals.Add(Refusal(PrototypeCodes.FixedWallInvalid, subject, vertical ? "zero-length wall" : "walls must be horizontal or vertical"));
                continue;
            }
            if (lot is not null && double.IsFinite(lot.WidthMetres) && double.IsFinite(lot.DepthMetres)
                && (Math.Max(wall.X0, wall.X1) > lot.WidthMetres || Math.Max(wall.Y0, wall.Y1) > lot.DepthMetres))
            {
                refusals.Add(Refusal(PrototypeCodes.FixedWallInvalid, subject, "wall exceeds lot " + Format(lot.WidthMetres) + " x " + Format(lot.DepthMetres) + " m"));
                continue;
            }
            valid.Add(wall);
        }
        return valid;
    }

    // Approval sets no layout can satisfy, refused before planning: two approved rooms of a forbidden pair touching, two
    // approved rooms of a required pair sharing less than the door width, approved area above the brief budget, and an
    // approved wall running through the interior of an approved room.
    static void CheckApprovalSet(ProjectBrief brief, double door, List<FixedRoom> rooms, List<FixedWall> walls, List<PlanConstraint> refusals)
    {
        var placed = new Dictionary<string, PlanRect>(StringComparer.Ordinal);
        foreach (var room in rooms) placed.TryAdd(room.RoomId, room.Rect);
        var seen = new HashSet<(string, string, BriefAdjacencyKind)>();
        foreach (var adjacency in brief.Adjacencies)
        {
            var key = string.CompareOrdinal(adjacency.A, adjacency.B) < 0 ? (adjacency.A, adjacency.B, adjacency.Kind) : (adjacency.B, adjacency.A, adjacency.Kind);
            if (!seen.Add(key) || !placed.TryGetValue(adjacency.A, out var a) || !placed.TryGetValue(adjacency.B, out var b)) continue;
            var shared = Shared(a, b);
            var subject = adjacency.A + "/" + adjacency.B;
            if (adjacency.Kind == BriefAdjacencyKind.Forbidden && shared > Epsilon)
                refusals.Add(Refusal(PrototypeCodes.FixedAdjacencyForbidden, subject, "approved rooms of a forbidden pair share " + Format(shared) + " m of wall"));
            if (adjacency.Kind == BriefAdjacencyKind.Required && double.IsFinite(door) && door > 0 && shared < door)
                refusals.Add(Refusal(PrototypeCodes.FixedAdjacencyRequired, subject, "approved rooms of a required pair share " + Format(shared) + " m of wall, below the " + Format(door) + " m door width"));
        }
        if (brief.Constraints.TotalAreaBudgetSquareMetres is { } budget)
        {
            double area = 0;
            foreach (var room in rooms) area += room.Rect.Area;
            if (area > budget.Value + SpacePlanner.AreaToleranceSquareMetres)
                refusals.Add(Refusal(PrototypeCodes.FixedAreaOverBudget, "fixed_rooms", "approved rooms total " + Format(area) + " m2, above the " + Format(budget.Value) + " m2 budget"));
        }
        foreach (var wall in walls)
            foreach (var room in rooms)
            {
                var r = room.Rect;
                var horizontal = wall.Y0 == wall.Y1;
                var crosses = horizontal
                    ? wall.Y0 > r.Y + Epsilon && wall.Y0 < r.Top - Epsilon && Math.Min(Math.Max(wall.X0, wall.X1), r.Right) - Math.Max(Math.Min(wall.X0, wall.X1), r.X) > Epsilon
                    : wall.X0 > r.X + Epsilon && wall.X0 < r.Right - Epsilon && Math.Min(Math.Max(wall.Y0, wall.Y1), r.Top) - Math.Max(Math.Min(wall.Y0, wall.Y1), r.Y) > Epsilon;
                if (crosses) refusals.Add(Refusal(PrototypeCodes.FixedWallCrossesRoom, wall.Id + "/" + room.RoomId, "approved wall runs through the interior of approved room " + room.RoomId + " " + Describe(r)));
            }
    }

    static ApartmentTypology PrototypeTypology(ProjectBrief brief) => (ApartmentTypology)brief.Rooms.Count(room => room.Id.StartsWith("bedroom_", StringComparison.Ordinal));

    // ---- around approved rooms ----

    static (SpacePlan Plan, string? Variant) AroundFixed(ProjectBrief brief, ProjectBrief? sub, int freeCount, PlanRect? region, double freeMinimum, int seed, PrototypeLot lot,
        double door, IReadOnlyList<FixedRoom> fixedRooms, IReadOnlyList<FixedWall> walls, PlannerRules plannerRules)
    {
        var rules = plannerRules.Rules;
        var fixedPlaced = fixedRooms.ToDictionary(room => room.RoomId, room => room.Rect, StringComparer.Ordinal);
        if (freeCount == 0 || sub is null)
        {
            var only = Compose(brief, fixedPlaced, new Dictionary<string, PlanRect>(StringComparer.Ordinal));
            var checks = Assess(brief, only, lot, door, rules).Concat(Approvals(only, fixedRooms, walls)).ToArray();
            return (Result(brief, seed, lot, door, only, checks, Score(brief, only, door)), "fixed_only");
        }
        if (region is null)
            return (Infeasible(brief, seed, lot, door, new[] { Refusal(PrototypeCodes.FixedRegionMissing, "plan", "no free rectangle of the lot next to the approved rooms holds the other rooms' minimum area of "
                + Format(freeMinimum) + " m2") }), null);
        var subPlan = SpacePlanner.Plan(sub, seed, new PlannerOptions(door, new PlanFootprint(region.Width, region.Depth), plannerRules));
        if (!subPlan.IsFeasible) return (Infeasible(brief, seed, lot, door, subPlan.Violations), null);
        var x0 = subPlan.Rooms.Min(room => room.Rect.X);
        var y0 = subPlan.Rooms.Min(room => room.Rect.Y);
        var x1 = subPlan.Rooms.Max(room => room.Rect.Right);
        var y1 = subPlan.Rooms.Max(room => room.Rect.Top);
        var width = x1 - x0;
        var depth = y1 - y0;
        PlanConstraint[]? bestChecks = null;
        string? bestVariant = null;
        var bestCount = int.MaxValue;
        for (var corner = 0; corner < Corners.Length; corner++)
        {
            var ax = corner % 2 == 0 ? region.X : region.Right - width;
            var ay = corner < 2 ? region.Y : region.Top - depth;
            for (var mirror = 0; mirror < Mirrors.Length; mirror++)
            {
                var flipX = mirror == 1 || mirror == 3;
                var flipY = mirror == 2 || mirror == 3;
                var freePlaced = new Dictionary<string, PlanRect>(StringComparer.Ordinal);
                foreach (var room in subPlan.Rooms)
                {
                    var r = room.Rect;
                    var x = ax + (flipX ? x1 - r.Right : r.X - x0);
                    var y = ay + (flipY ? y1 - r.Top : r.Y - y0);
                    freePlaced[room.RoomId] = new PlanRect(x, y, r.Width, r.Depth);
                }
                var rooms = Compose(brief, fixedPlaced, freePlaced);
                var checks = Assess(brief, rooms, lot, door, rules).Concat(Approvals(rooms, fixedRooms, walls)).ToArray();
                var violations = checks.Count(check => check.Hard && !check.Satisfied);
                var variant = "corner " + Corners[corner] + " mirror " + Mirrors[mirror];
                if (violations == 0)
                    return (new SpacePlan(SpacePlanStatus.Feasible, brief.Name, seed, 1, new PlanFootprint(lot.WidthMetres, lot.DepthMetres), door, rooms, checks, Score(brief, rooms, door)), variant);
                if (violations < bestCount) { bestCount = violations; bestChecks = checks; bestVariant = variant; }
            }
        }
        return (Infeasible(brief, seed, lot, door, bestChecks ?? Array.Empty<PlanConstraint>()), bestVariant);
    }

    // Rooms in brief order, each from the approved or the free placement.
    static PlannedRoom[] Compose(ProjectBrief brief, IReadOnlyDictionary<string, PlanRect> fixedPlaced, IReadOnlyDictionary<string, PlanRect> freePlaced)
    {
        var rooms = new List<PlannedRoom>();
        foreach (var room in brief.Rooms)
        {
            if (fixedPlaced.TryGetValue(room.Id, out var approved)) rooms.Add(new PlannedRoom(room.Id, room.Id, room.Name, 1, 0, approved));
            else if (freePlaced.TryGetValue(room.Id, out var placed)) rooms.Add(new PlannedRoom(room.Id, room.Id, room.Name, 1, 0, placed));
        }
        return rooms.ToArray();
    }

    static ProjectBrief? SubBrief(ProjectBrief brief, BriefRoom[] free, IReadOnlyList<FixedRoom> fixedRooms)
    {
        if (free.Length == 0) return null;
        var ids = new HashSet<string>(free.Select(room => room.Id), StringComparer.Ordinal);
        BriefValue<double>? budget = null;
        if (brief.Constraints.TotalAreaBudgetSquareMetres is { } total)
        {
            var remaining = total.Value - fixedRooms.Sum(room => room.Rect.Area);
            if (remaining > 0) budget = new BriefValue<double>(remaining, total.Provenance);
        }
        return new ProjectBrief(brief.Version, brief.Name, new BriefConstraints(brief.Constraints.Jurisdiction, null, new BriefValue<int>(1), budget),
            free, brief.Adjacencies.Where(adjacency => ids.Contains(adjacency.A) && ids.Contains(adjacency.B)).ToArray());
    }

    // Maximal axis-aligned rectangles of the lot (floored to the grid) free of approved rooms, with corners on the lot
    // edges and approved room edges, holding at least the free rooms' minimum area; the MaxRegions largest, ties by
    // y, x, top, right.
    static List<PlanRect> Regions(PrototypeLot lot, IReadOnlyList<FixedRoom> fixedRooms, double minimumArea)
    {
        var width = FloorGrid(lot.WidthMetres);
        var depth = FloorGrid(lot.DepthMetres);
        var xs = new SortedSet<double> { 0, width };
        var ys = new SortedSet<double> { 0, depth };
        foreach (var room in fixedRooms)
        {
            foreach (var edge in new[] { room.Rect.X, room.Rect.Right }) if (edge > 0 && edge < width) xs.Add(edge);
            foreach (var edge in new[] { room.Rect.Y, room.Rect.Top }) if (edge > 0 && edge < depth) ys.Add(edge);
        }
        var x = xs.ToArray();
        var y = ys.ToArray();
        bool Free(int i, int j, int k, int l) => fixedRooms.All(room => !Overlap(room.Rect, new PlanRect(x[i], y[k], x[j] - x[i], y[l] - y[k])));
        var regions = new List<PlanRect>();
        for (var i = 0; i < x.Length; i++)
            for (var j = i + 1; j < x.Length; j++)
                for (var k = 0; k < y.Length; k++)
                    for (var l = k + 1; l < y.Length; l++)
                    {
                        if (!Free(i, j, k, l)) continue;
                        if ((i > 0 && Free(i - 1, j, k, l)) || (j < x.Length - 1 && Free(i, j + 1, k, l)) || (k > 0 && Free(i, j, k - 1, l)) || (l < y.Length - 1 && Free(i, j, k, l + 1))) continue;
                        var region = new PlanRect(x[i], y[k], x[j] - x[i], y[l] - y[k]);
                        if (region.Area >= minimumArea) regions.Add(region);
                    }
        return regions.OrderByDescending(region => region.Area).ThenBy(region => region.Y).ThenBy(region => region.X).ThenBy(region => region.Top).ThenBy(region => region.Right)
            .Take(MaxRegions).ToList();
    }

    // ---- layout checks ----

    // Hard checks of a composed layout against the full brief, in the planner's codes and order: per room area_min
    // (brief and CABA minimums), area_max, side_min, in_site; no_overlap; area_budget; adjacencies (preferred soft).
    static List<PlanConstraint> Assess(ProjectBrief brief, IReadOnlyList<PlannedRoom> rooms, PrototypeLot lot, double door, IReadOnlyList<PlannerRule> rules)
    {
        var constraints = new List<PlanConstraint>();
        double totalArea = 0;
        foreach (var spec in brief.Rooms)
        {
            var placed = rooms.FirstOrDefault(room => room.RoomId == spec.Id);
            if (placed is null)
            {
                constraints.Add(new PlanConstraint(PlanCodes.AreaMin, spec.Id, true, false, "room missing from the layout"));
                continue;
            }
            var rect = placed.Rect;
            var area = rect.Area;
            var shortSide = Math.Min(rect.Width, rect.Depth);
            totalArea += area;
            var (minArea, minSide, source) = Minimums(spec, rules);
            constraints.Add(new PlanConstraint(PlanCodes.AreaMin, spec.Id, true, area >= minArea, "area " + Format(area) + " m2, minimum " + Format(minArea) + " m2 (" + source + ")"));
            if (spec.MaxAreaSquareMetres is { } max)
                constraints.Add(new PlanConstraint(PlanCodes.AreaMax, spec.Id, true, area <= max.Value + SpacePlanner.AreaToleranceSquareMetres, "area " + Format(area) + " m2, maximum " + Format(max.Value) + " m2"));
            if (minSide > 0)
                constraints.Add(new PlanConstraint(PlanCodes.SideMin, spec.Id, true, shortSide >= minSide, "short side " + Format(shortSide) + " m, minimum " + Format(minSide) + " m (" + source + ")"));
            var inside = rect.X >= 0 && rect.Y >= 0 && rect.Right <= lot.WidthMetres && rect.Top <= lot.DepthMetres && rect.Width > 0 && rect.Depth > 0;
            constraints.Add(new PlanConstraint(PlanCodes.InSite, spec.Id, true, inside, "rectangle " + Describe(rect) + " on level 0 within lot " + Format(lot.WidthMetres) + " x " + Format(lot.DepthMetres) + " m"));
        }
        var overlaps = 0;
        for (var i = 0; i < rooms.Count; i++)
            for (var j = i + 1; j < rooms.Count; j++)
                if (Overlap(rooms[i].Rect, rooms[j].Rect)) overlaps++;
        constraints.Add(new PlanConstraint(PlanCodes.NoOverlap, "level 0", true, overlaps == 0, Text(overlaps) + " overlapping room pairs"));
        if (brief.Constraints.TotalAreaBudgetSquareMetres is { } budget)
            constraints.Add(new PlanConstraint(PlanCodes.AreaBudget, "plan", true, totalArea <= budget.Value + SpacePlanner.AreaToleranceSquareMetres,
                "total area " + Format(totalArea) + " m2, budget " + Format(budget.Value) + " m2"));
        var seen = new HashSet<(string, string, BriefAdjacencyKind)>();
        foreach (var adjacency in brief.Adjacencies)
        {
            var key = string.CompareOrdinal(adjacency.A, adjacency.B) < 0 ? (adjacency.A, adjacency.B, adjacency.Kind) : (adjacency.B, adjacency.A, adjacency.Kind);
            if (!seen.Add(key)) continue;
            var subject = adjacency.A + "/" + adjacency.B;
            var a = rooms.FirstOrDefault(room => room.RoomId == adjacency.A);
            var b = rooms.FirstOrDefault(room => room.RoomId == adjacency.B);
            var shared = a is null || b is null ? 0 : Shared(a.Rect, b.Rect);
            if (adjacency.Kind == BriefAdjacencyKind.Forbidden)
                constraints.Add(new PlanConstraint(PlanCodes.AdjacencyForbidden, subject, true, shared <= Epsilon, "longest shared edge " + Format(shared) + " m"));
            else
            {
                var met = a is not null && b is not null && shared >= door;
                var detail = (met ? "2" : "0") + " of 2 instances share a wall of at least " + Format(door) + " m; shortest " + Format(shared) + " m";
                constraints.Add(adjacency.Kind == BriefAdjacencyKind.Required ? new PlanConstraint(PlanCodes.AdjacencyRequired, subject, true, met, detail)
                    : new PlanConstraint(PlanCodes.AdjacencyPreferred, subject, false, met, detail));
            }
        }
        return constraints;
    }

    // Approval checks of a layout: every approved room with its exact rectangle, every approved wall on room edges.
    static List<PlanConstraint> Approvals(IReadOnlyList<PlannedRoom> rooms, IReadOnlyList<FixedRoom> fixedRooms, IReadOnlyList<FixedWall> walls)
    {
        var checks = new List<PlanConstraint>();
        foreach (var approved in fixedRooms)
        {
            var placed = rooms.FirstOrDefault(room => room.RoomId == approved.RoomId);
            var kept = placed is not null && Same(placed.Rect, approved.Rect);
            checks.Add(new PlanConstraint(PrototypeCodes.FixedRoomMoved, approved.RoomId, true, kept, placed is null ? "approved room missing from the layout"
                : "approved " + Describe(approved.Rect) + ", layout " + Describe(placed.Rect)));
        }
        foreach (var wall in walls)
        {
            var covered = Covered(rooms, wall);
            checks.Add(new PlanConstraint(PrototypeCodes.FixedWallMissing, wall.Id, true, covered, "approved wall " + Format(wall.X0) + "," + Format(wall.Y0) + " to " + Format(wall.X1) + "," + Format(wall.Y1)
                + (covered ? " lies on room edges" : " is not covered by room edges")));
        }
        return checks;
    }

    static bool Covered(IReadOnlyList<PlannedRoom> rooms, FixedWall wall)
    {
        var horizontal = wall.Y0 == wall.Y1;
        var line = horizontal ? wall.Y0 : wall.X0;
        var from = horizontal ? Math.Min(wall.X0, wall.X1) : Math.Min(wall.Y0, wall.Y1);
        var to = horizontal ? Math.Max(wall.X0, wall.X1) : Math.Max(wall.Y0, wall.Y1);
        var spans = new List<(double Start, double End)>();
        foreach (var room in rooms)
        {
            var r = room.Rect;
            if (horizontal && (Math.Abs(r.Y - line) < Epsilon || Math.Abs(r.Top - line) < Epsilon)) spans.Add((r.X, r.Right));
            if (!horizontal && (Math.Abs(r.X - line) < Epsilon || Math.Abs(r.Right - line) < Epsilon)) spans.Add((r.Y, r.Top));
        }
        var reach = from;
        foreach (var (start, end) in spans.OrderBy(span => span.Start).ThenBy(span => span.End))
        {
            if (start > reach + Epsilon) break;
            reach = Math.Max(reach, end);
        }
        return reach >= to - Epsilon;
    }

    // ---- shared measures ----

    // The planner's effective minimums: brief minimum area raised by every matching rule's minimum area and minimum
    // side squared; minimum side the largest matching rule side.
    static (double Area, double Side, string Source) Minimums(BriefRoom room, IReadOnlyList<PlannerRule> rules)
    {
        var minArea = room.MinAreaSquareMetres?.Value ?? 0;
        double minSide = 0;
        var sources = new List<string> { "brief" };
        foreach (var rule in rules)
        {
            if (!Matches(rule, room)) continue;
            if (rule.MinAreaSquareMetres is { } area && area > minArea) minArea = area;
            if (rule.MinSideMetres is { } side && side > minSide) minSide = side;
            if (rule.Reference is { } reference) sources.Add(reference);
        }
        if (minSide * minSide > minArea) minArea = minSide * minSide;
        return (minArea, minSide, string.Join("; ", sources));
    }

    static bool Matches(PlannerRule rule, BriefRoom room) =>
        (rule.RoomId is null || string.Equals(rule.RoomId, room.Id, StringComparison.Ordinal)) && (rule.NamePattern is null || Glob(rule.NamePattern, room.Name));

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

    static double Shared(PlanRect a, PlanRect b)
    {
        if (Math.Abs(a.Right - b.X) < Epsilon || Math.Abs(b.Right - a.X) < Epsilon) return Math.Max(0, Math.Min(a.Top, b.Top) - Math.Max(a.Y, b.Y));
        if (Math.Abs(a.Top - b.Y) < Epsilon || Math.Abs(b.Top - a.Y) < Epsilon) return Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X));
        return 0;
    }

    static bool Overlap(PlanRect a, PlanRect b) => Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X) > Epsilon && Math.Min(a.Top, b.Top) - Math.Max(a.Y, b.Y) > Epsilon;

    static bool Same(PlanRect a, PlanRect b) => Bits(a.X) == Bits(b.X) && Bits(a.Y) == Bits(b.Y) && Bits(a.Width) == Bits(b.Width) && Bits(a.Depth) == Bits(b.Depth);

    static long Bits(double value) => BitConverter.DoubleToInt64Bits(value == 0 ? 0 : value);

    // Bounded by the largest lot side, so value * GridScale stays exact and huge values are refused as invalid.
    static bool OnGrid(double value) => double.IsFinite(value) && Math.Abs(value) <= MaxLotSideMetres && Math.Floor(value * GridScale) == value * GridScale;

    // The planner's soft score (SpacePlanner.Score) over a composed layout in brief order: 0.5 preferred adjacency (met
    // fraction over the brief's preferred pairs, door-width wall; 1 without pairs) + 0.3 mean aspect quality
    // min(1, short / long / 0.5) + 0.2 mean target fit max(0, 1 - |area - target| / target) (1 without a target),
    // rounded to 6 decimals.
    static double Score(ProjectBrief brief, IReadOnlyList<PlannedRoom> rooms, double door)
    {
        double aspect = 0, fit = 0, preferred = 0;
        var pairs = 0;
        foreach (var room in rooms)
        {
            var longSide = Math.Max(room.Rect.Width, room.Rect.Depth);
            aspect += longSide > 0 ? Math.Min(1, Math.Min(room.Rect.Width, room.Rect.Depth) / longSide / 0.5) : 0;
            var target = brief.Rooms.First(spec => spec.Id == room.RoomId).TargetAreaSquareMetres?.Value;
            fit += target is { } t ? Math.Max(0, 1 - Math.Abs(room.AreaSquareMetres - t) / t) : 1;
        }
        var seen = new HashSet<(string, string)>();
        foreach (var adjacency in brief.Adjacencies)
        {
            if (adjacency.Kind != BriefAdjacencyKind.Preferred) continue;
            var key = string.CompareOrdinal(adjacency.A, adjacency.B) < 0 ? (adjacency.A, adjacency.B) : (adjacency.B, adjacency.A);
            if (!seen.Add(key)) continue;
            var a = rooms.FirstOrDefault(room => room.RoomId == adjacency.A);
            var b = rooms.FirstOrDefault(room => room.RoomId == adjacency.B);
            pairs++;
            if (a is not null && b is not null && Shared(a.Rect, b.Rect) >= door) preferred++;
        }
        var count = Math.Max(1, rooms.Count);
        return Math.Round(0.5 * (pairs == 0 ? 1 : preferred / pairs) + 0.3 * (aspect / count) + 0.2 * (fit / count), 6, MidpointRounding.ToEven);
    }

    static double FloorGrid(double value) => Math.Floor(value * GridScale) / GridScale;

    static bool LotSide(double value) => double.IsFinite(value) && value >= MinLotSideMetres && value <= MaxLotSideMetres;

    static SpacePlan Result(ProjectBrief brief, int seed, PrototypeLot lot, double door, PlannedRoom[] rooms, PlanConstraint[] checks, double score) =>
        checks.Any(check => check.Hard && !check.Satisfied) ? Infeasible(brief, seed, lot, door, checks)
            : new SpacePlan(SpacePlanStatus.Feasible, brief.Name, seed, 1, new PlanFootprint(lot.WidthMetres, lot.DepthMetres), door, rooms, checks, score);

    // An infeasible candidate keeps only its unsatisfied hard checks; they become the alternative's reasons.
    static SpacePlan Infeasible(ProjectBrief brief, int seed, PrototypeLot lot, double door, IEnumerable<PlanConstraint> checks) =>
        new(SpacePlanStatus.Infeasible, brief.Name, seed, 1, new PlanFootprint(lot.WidthMetres, lot.DepthMetres), door, Array.Empty<PlannedRoom>(),
            checks.Where(check => check.Hard && !check.Satisfied).ToArray(), 0);

    static PlanConstraint Refusal(string code, string subject, string detail) => new(code, subject, true, false, detail);

    static string Describe(PlanRect rect) => Format(rect.X) + "," + Format(rect.Y) + " " + Format(rect.Width) + " x " + Format(rect.Depth) + " m";

    static string Format(double value) => double.IsFinite(value) ? SpacePlanSerializer.Format(value) : "non-finite";

    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
