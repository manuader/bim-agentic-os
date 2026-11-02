using System.Globalization;
using Bim.Core;
namespace Bim.Generative;

// Deterministic rectangular space planner. Steps:
// 1. Refuse invalid input as a typed infeasible plan: brief validator errors, door width, planner rules, footprint
//    (site, or PlannerOptions.Footprint inside the site), rule minimums against brief maximums, per-level and budget
//    area sums.
// 2. Expand rooms into count instances, each with an effective minimum area (brief minimum, rule minimums, rule
//    minimum side squared), the brief maximum and a desired area (target, else range midpoint, else a bound, else the
//    mean of the others) kept 0.01 m2 above the minimum; assign levels (preferred level, else a required partner's,
//    else the least loaded); shrink desired areas toward the minimums to fit each level's footprint and the budget.
// 3. Per level, place a building rectangle at the footprint origin with exactly the desired total area and tile it
//    with a binary slicing tree: rooms in a permutation order, each node split near half its area (shifted by a gene),
//    cut across its longer side (or flipped by a gene). Coordinates snap to a 0.1 mm grid after slicing, so
//    neighbours share exact edges and rooms never overlap.
// 4. Search the genome (permutation, split shifts, cut flips, building width) with seeded threshold accepting from
//    several restarts, minimising hard-constraint deficits first and the soft score second. The budget is fixed, so
//    the same brief, options and seed always give the same plan.
// 5. Assess the best layout against every constraint on the snapped geometry. Any hard violation gives an infeasible
//    plan with no rooms; a feasible plan therefore never holds a room below its minimum area.
public static class SpacePlanner
{
    // Max-area and budget checks allow this much over; minimum areas are never relaxed.
    public const double AreaToleranceSquareMetres = 0.02;
    const double GridScale = 10000;
    const double AreaMargin = 0.01;
    const double Epsilon = 1e-7;
    const double SideTolerance = 1e-9;
    const int Restarts = 8;
    const int WidthSteps = 4;
    const double Threshold = 1.0;
    const double HardWeight = 10;

    public static SpacePlan Plan(ProjectBrief brief, int seed, PlannerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(brief);
        options ??= new PlannerOptions();
        var rules = options.Rules?.Rules ?? Array.Empty<PlannerRule>();
        var door = options.DoorWidthMetres;
        var levels = brief.Constraints.Levels?.Value ?? 1;
        var refusals = new List<PlanConstraint>();
        foreach (var issue in BriefValidator.Validate(brief))
            if (issue.Severity == BriefSeverity.Error) refusals.Add(Violation(PlanCodes.BriefInvalid, issue.Field, issue.Code + ": " + issue.Detail));
        if (refusals.Count > 0) return Infeasible(brief, seed, levels, null, door, refusals);
        // Without a level count the brief may still name preferred levels; plan as many levels as they need.
        if (brief.Constraints.Levels is null) levels = Math.Max(1, brief.Rooms.Max(room => (room.PreferredLevel?.Value ?? 0) + 1));
        if (!Positive(door)) refusals.Add(Violation(PlanCodes.OptionsInvalid, "door_width_m", "positive finite door width required"));
        CheckRules(rules, refusals);
        var footprint = Footprint(brief, options, refusals);
        if (refusals.Count > 0 || footprint is null) return Infeasible(brief, seed, levels, footprint, door, refusals);

        var units = Expand(brief, rules, refusals);
        if (refusals.Count > 0) return Infeasible(brief, seed, levels, footprint, door, refusals);
        var usableArea = FloorGrid(footprint.WidthMetres) * FloorGrid(footprint.DepthMetres);
        SetDesired(units, usableArea * levels);
        AssignLevels(brief, units, levels, usableArea);
        var budget = brief.Constraints.TotalAreaBudgetSquareMetres?.Value;
        Fit(units, levels, usableArea, budget, refusals);
        if (refusals.Count > 0) return Infeasible(brief, seed, levels, footprint, door, refusals);

        var pairs = Pairs(brief, units);
        var boxes = new Box[units.Count];
        for (var level = 0; level < levels; level++)
        {
            var members = Enumerable.Range(0, units.Count).Where(index => units[index].Level == level).ToArray();
            if (members.Length == 0) continue;
            var problem = Problem(units, members, pairs, footprint, door);
            var layout = Search(problem, seed, level);
            for (var local = 0; local < members.Length; local++) boxes[members[local]] = layout[local];
        }

        var (constraints, score) = Assess(units, boxes, pairs, footprint, levels, door, budget);
        if (constraints.Any(constraint => constraint.Hard && !constraint.Satisfied)) return Infeasible(brief, seed, levels, footprint, door, constraints);
        var rooms = units.Select((unit, index) => new PlannedRoom(unit.InstanceId, unit.Room.Id, unit.Room.Name, unit.Instance, unit.Level,
            new PlanRect(boxes[index].X0, boxes[index].Y0, Round(boxes[index].X1 - boxes[index].X0), Round(boxes[index].Y1 - boxes[index].Y0)))).ToArray();
        return new SpacePlan(SpacePlanStatus.Feasible, brief.Name, seed, levels, footprint, door, rooms, constraints, Round(score));
    }

    // ---- input ----

    static void CheckRules(IReadOnlyList<PlannerRule> rules, List<PlanConstraint> refusals)
    {
        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            var field = "rules[" + Text(index) + "]";
            if (rule.RoomId is null && rule.NamePattern is null) refusals.Add(Violation(PlanCodes.RuleInvalid, field, "room_id or name_pattern required"));
            else if ((rule.RoomId is not null && string.IsNullOrWhiteSpace(rule.RoomId)) || (rule.NamePattern is not null && string.IsNullOrWhiteSpace(rule.NamePattern)))
                refusals.Add(Violation(PlanCodes.RuleInvalid, field, "selectors must not be blank"));
            if (rule.MinAreaSquareMetres is null && rule.MinSideMetres is null) refusals.Add(Violation(PlanCodes.RuleInvalid, field, "min_area_m2 or min_side_m required"));
            if (rule.MinAreaSquareMetres is { } area && !Positive(area)) refusals.Add(Violation(PlanCodes.RuleInvalid, field, "min_area_m2 must be positive and finite"));
            if (rule.MinSideMetres is { } side && !Positive(side)) refusals.Add(Violation(PlanCodes.RuleInvalid, field, "min_side_m must be positive and finite"));
        }
    }

    static PlanFootprint? Footprint(ProjectBrief brief, PlannerOptions options, List<PlanConstraint> refusals)
    {
        var site = brief.Constraints.Site;
        PlanFootprint footprint;
        if (options.Footprint is { } explicitFootprint)
        {
            if (site is not null && (explicitFootprint.WidthMetres > site.WidthMetres.Value + Epsilon || explicitFootprint.DepthMetres > site.DepthMetres.Value + Epsilon))
            {
                refusals.Add(Violation(PlanCodes.FootprintInvalid, "footprint", "footprint " + Text(explicitFootprint.WidthMetres) + " x " + Text(explicitFootprint.DepthMetres)
                    + " m exceeds site " + Text(site.WidthMetres.Value) + " x " + Text(site.DepthMetres.Value) + " m"));
                return null;
            }
            footprint = explicitFootprint;
        }
        else if (site is not null) footprint = new PlanFootprint(site.WidthMetres.Value, site.DepthMetres.Value);
        else
        {
            refusals.Add(Violation(PlanCodes.FootprintMissing, "footprint", "the brief has no site and no PlannerOptions.Footprint was given"));
            return null;
        }
        if (!(Positive(footprint.WidthMetres) && Positive(footprint.DepthMetres) && FloorGrid(footprint.WidthMetres) > 0 && FloorGrid(footprint.DepthMetres) > 0))
        {
            refusals.Add(Violation(PlanCodes.FootprintInvalid, "footprint", "width and depth must be finite and at least 0.0001 m"));
            return null;
        }
        return footprint;
    }

    static List<Unit> Expand(ProjectBrief brief, IReadOnlyList<PlannerRule> rules, List<PlanConstraint> refusals)
    {
        var units = new List<Unit>();
        foreach (var room in brief.Rooms)
        {
            var minArea = room.MinAreaSquareMetres?.Value ?? 0;
            var areaSource = "brief";
            double minSide = 0;
            var sideSource = "";
            for (var index = 0; index < rules.Count; index++)
            {
                var rule = rules[index];
                if (!Matches(rule, room)) continue;
                var source = "rules[" + Text(index) + "]" + (rule.Reference is { } reference ? " " + reference : "");
                if (rule.MinAreaSquareMetres is { } area && area > minArea) { minArea = area; areaSource = source; }
                if (rule.MinSideMetres is { } side && side > minSide) { minSide = side; sideSource = source; }
            }
            if (minSide * minSide > minArea) { minArea = minSide * minSide; areaSource = "minimum side of " + sideSource; }
            var maxArea = room.MaxAreaSquareMetres?.Value;
            if (maxArea is { } max && minArea > max)
            {
                refusals.Add(Violation(PlanCodes.AreaRangeConflict, room.Id, "minimum area " + Text(minArea) + " m2 (" + areaSource + ") exceeds maximum " + Text(max) + " m2"));
                continue;
            }
            var target = room.TargetAreaSquareMetres?.Value;
            var briefMin = room.MinAreaSquareMetres?.Value;
            double? baseline = target ?? (briefMin is { } low && maxArea is { } high ? (low + high) / 2 : briefMin ?? maxArea);
            for (var instance = 1; instance <= room.EffectiveCount; instance++)
            {
                var id = room.EffectiveCount == 1 ? room.Id : room.Id + "#" + Text(instance);
                units.Add(new Unit(id, room, instance, minArea, areaSource, maxArea, minSide, sideSource, target) { Desired = baseline ?? double.NaN });
            }
        }
        return units;
    }

    static bool Matches(PlannerRule rule, BriefRoom room) =>
        (rule.RoomId is null || string.Equals(rule.RoomId, room.Id, StringComparison.Ordinal)) && (rule.NamePattern is null || Glob(rule.NamePattern, room.Name));

    // Case-insensitive wildcard match: '*' any run (possibly empty), '?' exactly one character.
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

    // Rooms without any area get the mean desired area of the others, or an equal share of the floor area.
    static void SetDesired(List<Unit> units, double floorArea)
    {
        var known = units.Where(unit => !double.IsNaN(unit.Desired)).Select(unit => unit.Desired).ToArray();
        var fallback = known.Length > 0 ? known.Average() : floorArea / units.Count;
        foreach (var unit in units)
        {
            var desired = double.IsNaN(unit.Desired) ? fallback : unit.Desired;
            unit.Desired = Math.Max(unit.Lower, Math.Min(unit.MaxArea ?? double.PositiveInfinity, desired));
        }
    }

    static void AssignLevels(ProjectBrief brief, List<Unit> units, int levels, double usableArea)
    {
        var load = new double[levels];
        foreach (var unit in units)
        {
            if (unit.Room.PreferredLevel is not { } preferred) continue;
            unit.Level = preferred.Value;
            load[unit.Level] += unit.Desired;
        }
        var ordered = brief.Adjacencies.Where(a => a.Kind == BriefAdjacencyKind.Required).Concat(brief.Adjacencies.Where(a => a.Kind == BriefAdjacencyKind.Preferred)).ToArray();
        foreach (var unit in units)
        {
            if (unit.Level >= 0) continue;
            var chosen = -1;
            if (levels > 1)
            {
                foreach (var adjacency in ordered)
                {
                    var partner = adjacency.A == unit.Room.Id ? adjacency.B : adjacency.B == unit.Room.Id ? adjacency.A : null;
                    if (partner is null) continue;
                    var placed = units.FirstOrDefault(other => other.Room.Id == partner && other.Level >= 0);
                    if (placed is null) continue;
                    if (load[placed.Level] + unit.Desired <= usableArea) chosen = placed.Level;
                    break;
                }
            }
            if (chosen < 0)
            {
                chosen = 0;
                for (var level = 1; level < levels; level++) if (load[level] < load[chosen]) chosen = level;
            }
            unit.Level = chosen;
            load[chosen] += unit.Desired;
        }
    }

    // Shrinks desired areas toward their lower bounds so each level fits the footprint and all levels fit the budget.
    static void Fit(List<Unit> units, int levels, double usableArea, double? budget, List<PlanConstraint> refusals)
    {
        for (var level = 0; level < levels; level++)
        {
            var members = units.Where(unit => unit.Level == level).ToList();
            var minimum = members.Sum(unit => unit.MinArea);
            if (minimum > usableArea)
            {
                refusals.Add(Violation(PlanCodes.LevelAreaInsufficient, "level " + Text(level), "minimum areas " + Text(minimum) + " m2 exceed footprint area " + Text(usableArea) + " m2"));
                continue;
            }
            Shrink(members, usableArea);
        }
        if (budget is not { } limit) return;
        var total = units.Sum(unit => unit.MinArea);
        if (total > limit) refusals.Add(Violation(PlanCodes.BudgetInsufficient, "plan", "minimum areas " + Text(total) + " m2 exceed budget " + Text(limit) + " m2"));
        else Shrink(units, limit);
    }

    static void Shrink(List<Unit> units, double capacity)
    {
        var desired = units.Sum(unit => unit.Desired);
        if (desired <= capacity || units.Count == 0) return;
        var lower = units.Sum(unit => unit.Lower);
        if (lower <= capacity)
        {
            var factor = (capacity - lower) / (desired - lower);
            foreach (var unit in units) unit.Desired = unit.Lower + (unit.Desired - unit.Lower) * factor;
        }
        else
        {
            var share = (capacity - units.Sum(unit => unit.MinArea)) / units.Count;
            foreach (var unit in units) unit.Desired = unit.MinArea + share;
        }
    }

    // Adjacencies deduplicated by unordered pair and kind, in brief order, with their instance indices.
    static List<Pair> Pairs(ProjectBrief brief, List<Unit> units)
    {
        var pairs = new List<Pair>();
        var seen = new HashSet<(string, string, BriefAdjacencyKind)>();
        foreach (var adjacency in brief.Adjacencies)
        {
            var key = string.CompareOrdinal(adjacency.A, adjacency.B) < 0 ? (adjacency.A, adjacency.B, adjacency.Kind) : (adjacency.B, adjacency.A, adjacency.Kind);
            if (!seen.Add(key)) continue;
            int[] Of(string id) => Enumerable.Range(0, units.Count).Where(index => units[index].Room.Id == id).ToArray();
            pairs.Add(new Pair(adjacency.A + "/" + adjacency.B, adjacency.Kind, Of(adjacency.A), Of(adjacency.B)));
        }
        return pairs;
    }

    static LevelProblem Problem(List<Unit> units, int[] members, List<Pair> pairs, PlanFootprint footprint, double door)
    {
        var local = new Dictionary<int, int>();
        for (var index = 0; index < members.Length; index++) local[members[index]] = index;
        var levelPairs = new List<LevelPair>();
        foreach (var pair in pairs)
        {
            var a = pair.A.Where(local.ContainsKey).Select(index => local[index]).ToArray();
            var b = pair.B.Where(local.ContainsKey).Select(index => local[index]).ToArray();
            if (a.Length > 0 && b.Length > 0) levelPairs.Add(new LevelPair(pair.Kind, a, b));
        }
        var selected = members.Select(index => units[index]).ToArray();
        return new LevelProblem(
            selected.Select(unit => unit.Desired).ToArray(),
            selected.Select(unit => unit.MinArea).ToArray(),
            selected.Select(unit => unit.MaxArea ?? double.PositiveInfinity).ToArray(),
            selected.Select(unit => unit.MinSide).ToArray(),
            selected.Select(unit => unit.Target ?? double.NaN).ToArray(),
            levelPairs.ToArray(), footprint.WidthMetres, footprint.DepthMetres, door);
    }

    // ---- layout ----

    static Box[] Search(LevelProblem problem, int seed, int level)
    {
        var count = problem.Areas.Length;
        var random = new SplitMix(seed, level);
        var iterations = 300 + 100 * count;
        var sorted = Enumerable.Range(0, count).OrderByDescending(index => problem.Areas[index]).ThenBy(index => index).ToArray();
        Genome? best = null;
        var bestCost = double.MaxValue;
        for (var restart = 0; restart < Restarts; restart++)
        {
            int[] order;
            if (restart == 0) order = sorted.ToArray();
            else
            {
                order = Enumerable.Range(0, count).ToArray();
                for (var i = count - 1; i > 0; i--)
                {
                    var j = random.Next(i + 1);
                    (order[i], order[j]) = (order[j], order[i]);
                }
            }
            var current = new Genome(order, new int[Math.Max(0, count - 1)], new bool[Math.Max(0, count - 1)], restart % 2 == 0 ? WidthSteps : random.Next(WidthSteps + 1));
            var cost = Cost(problem, current);
            if (cost < bestCost) { bestCost = cost; best = current; }
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var candidate = current.Copy();
                Mutate(candidate, random);
                var candidateCost = Cost(problem, candidate);
                var threshold = Threshold * (iterations - iteration) / iterations;
                if (candidateCost < cost + threshold)
                {
                    current = candidate;
                    cost = candidateCost;
                    if (cost < bestCost) { bestCost = cost; best = current; }
                }
            }
        }
        return Layout(problem, best ?? throw new InvalidOperationException("search produced no layout"));
    }

    static void Mutate(Genome genome, SplitMix random)
    {
        var count = genome.Order.Length;
        var move = random.Next(10);
        if (count >= 2 && move < 5)
        {
            var i = random.Next(count);
            var j = random.Next(count - 1);
            if (j >= i) j++;
            if (move < 4) (genome.Order[i], genome.Order[j]) = (genome.Order[j], genome.Order[i]);
            else
            {
                var value = genome.Order[i];
                if (j > i) Array.Copy(genome.Order, i + 1, genome.Order, i, j - i);
                else Array.Copy(genome.Order, j, genome.Order, j + 1, i - j);
                genome.Order[j] = value;
            }
        }
        else if (count >= 2 && move < 7)
        {
            var node = random.Next(count - 1);
            genome.Flip[node] = !genome.Flip[node];
        }
        else if (count >= 2 && move < 9) genome.Shift[random.Next(count - 1)] = random.Next(3) - 1;
        else genome.Width = Math.Clamp(genome.Width + (random.Next(2) == 0 ? 1 : -1), 0, WidthSteps);
    }

    static double Cost(LevelProblem problem, Genome genome)
    {
        var (hard, score) = Evaluate(problem, Layout(problem, genome));
        return hard * HardWeight + (1 - score);
    }

    static Box[] Layout(LevelProblem problem, Genome genome)
    {
        var total = problem.Areas.Sum();
        var width = FloorGrid(problem.Width);
        var depth = FloorGrid(problem.Depth);
        var narrowest = Math.Min(width, Math.Max(CeilGrid(total / depth), 1 / GridScale));
        var buildingWidth = Math.Min(width, CeilGrid(narrowest + (width - narrowest) * genome.Width / WidthSteps));
        var buildingDepth = Math.Min(depth, CeilGrid(total / buildingWidth));
        var boxes = new Box[problem.Areas.Length];
        var node = 0;
        Split(problem, genome, boxes, 0, boxes.Length, new Box(0, 0, buildingWidth, buildingDepth), ref node);
        for (var index = 0; index < boxes.Length; index++)
        {
            var box = boxes[index];
            boxes[index] = new Box(Snap(box.X0), Snap(box.Y0), Snap(box.X1), Snap(box.Y1));
        }
        return boxes;
    }

    static void Split(LevelProblem problem, Genome genome, Box[] boxes, int low, int high, Box box, ref int node)
    {
        if (high - low == 1) { boxes[genome.Order[low]] = box; return; }
        double total = 0;
        for (var i = low; i < high; i++) total += problem.Areas[genome.Order[i]];
        var balanced = low + 1;
        var bestGap = double.MaxValue;
        double prefix = 0;
        for (var k = low + 1; k < high; k++)
        {
            prefix += problem.Areas[genome.Order[k - 1]];
            var gap = Math.Abs(prefix - total / 2);
            if (gap < bestGap) { bestGap = gap; balanced = k; }
        }
        var index = node++;
        var cut = Math.Clamp(balanced + genome.Shift[index], low + 1, high - 1);
        double left = 0;
        for (var i = low; i < cut; i++) left += problem.Areas[genome.Order[i]];
        var ratio = left / total;
        var vertical = box.X1 - box.X0 >= box.Y1 - box.Y0;
        if (genome.Flip[index]) vertical = !vertical;
        if (vertical)
        {
            var at = box.X0 + (box.X1 - box.X0) * ratio;
            Split(problem, genome, boxes, low, cut, box with { X1 = at }, ref node);
            Split(problem, genome, boxes, cut, high, box with { X0 = at }, ref node);
        }
        else
        {
            var at = box.Y0 + (box.Y1 - box.Y0) * ratio;
            Split(problem, genome, boxes, low, cut, box with { Y1 = at }, ref node);
            Split(problem, genome, boxes, cut, high, box with { Y0 = at }, ref node);
        }
    }

    // Hard deficit (0 when every hard constraint on this level holds) and soft score in [0, 1].
    static (double Hard, double Score) Evaluate(LevelProblem problem, Box[] boxes)
    {
        double hard = 0, aspect = 0, fit = 0;
        for (var index = 0; index < boxes.Length; index++)
        {
            var box = boxes[index];
            var area = box.Area;
            hard += Math.Max(0, problem.MinSide[index] - box.ShortSide) * 10;
            hard += Math.Max(0, problem.MinArea[index] - area) * 10;
            hard += Math.Max(0, area - problem.MaxArea[index] - AreaToleranceSquareMetres);
            aspect += AspectQuality(box);
            fit += TargetFit(area, problem.Target[index]);
        }
        double preferred = 0;
        var preferredCount = 0;
        foreach (var pair in problem.Pairs)
        {
            if (pair.Kind == BriefAdjacencyKind.Forbidden)
            {
                foreach (var a in pair.A)
                    foreach (var b in pair.B)
                    {
                        var shared = Shared(boxes[a], boxes[b]);
                        if (shared > Epsilon) hard += 1 + shared;
                    }
                continue;
            }
            int total = 0, met = 0;
            double penalty = 0;
            foreach (var (side, other) in new[] { (pair.A, pair.B), (pair.B, pair.A) })
                foreach (var a in side)
                {
                    double best = 0, gap = double.MaxValue;
                    foreach (var b in other)
                    {
                        best = Math.Max(best, Shared(boxes[a], boxes[b]));
                        gap = Math.Min(gap, Gap(boxes[a], boxes[b]));
                    }
                    total++;
                    if (best >= problem.Door - SideTolerance) met++;
                    else penalty += problem.Door - best + (best <= Epsilon ? 1 + gap : 0);
                }
            if (pair.Kind == BriefAdjacencyKind.Required) hard += penalty;
            else { preferred += (double)met / total; preferredCount++; }
        }
        var count = boxes.Length;
        return (hard, Score(preferredCount == 0 ? 1 : preferred / preferredCount, aspect / count, fit / count));
    }

    // ---- assessment ----

    static (List<PlanConstraint> Constraints, double Score) Assess(List<Unit> units, Box[] boxes, List<Pair> pairs, PlanFootprint footprint, int levels, double door, double? budget)
    {
        var constraints = new List<PlanConstraint>();
        double aspect = 0, fit = 0, totalArea = 0;
        for (var index = 0; index < units.Count; index++)
        {
            var unit = units[index];
            var box = boxes[index];
            var area = box.Area;
            totalArea += area;
            aspect += AspectQuality(box);
            fit += TargetFit(area, unit.Target ?? double.NaN);
            constraints.Add(Constraint(PlanCodes.AreaMin, unit.InstanceId, true, area >= unit.MinArea, "area " + Text(area) + " m2, minimum " + Text(unit.MinArea) + " m2 (" + unit.AreaSource + ")"));
            if (unit.MaxArea is { } max)
                constraints.Add(Constraint(PlanCodes.AreaMax, unit.InstanceId, true, area <= max + AreaToleranceSquareMetres, "area " + Text(area) + " m2, maximum " + Text(max) + " m2"));
            if (unit.MinSide > 0)
                constraints.Add(Constraint(PlanCodes.SideMin, unit.InstanceId, true, box.ShortSide >= unit.MinSide - SideTolerance,
                    "short side " + Text(box.ShortSide) + " m, minimum " + Text(unit.MinSide) + " m (" + unit.SideSource + ")"));
            var inside = box.X0 >= -Epsilon && box.Y0 >= -Epsilon && box.X1 <= footprint.WidthMetres + Epsilon && box.Y1 <= footprint.DepthMetres + Epsilon
                && box.X1 - box.X0 > 0 && box.Y1 - box.Y0 > 0 && unit.Level >= 0 && unit.Level < levels;
            constraints.Add(Constraint(PlanCodes.InSite, unit.InstanceId, true, inside, "rectangle " + Text(box.X0) + "," + Text(box.Y0) + " to " + Text(box.X1) + "," + Text(box.Y1)
                + " on level " + Text(unit.Level) + " within footprint " + Text(footprint.WidthMetres) + " x " + Text(footprint.DepthMetres) + " m"));
        }
        for (var level = 0; level < levels; level++)
        {
            var overlaps = 0;
            for (var i = 0; i < units.Count; i++)
                for (var j = i + 1; j < units.Count; j++)
                    if (units[i].Level == level && units[j].Level == level && Overlap(boxes[i], boxes[j])) overlaps++;
            constraints.Add(Constraint(PlanCodes.NoOverlap, "level " + Text(level), true, overlaps == 0, Text(overlaps) + " overlapping room pairs"));
        }
        if (budget is { } limit)
            constraints.Add(Constraint(PlanCodes.AreaBudget, "plan", true, totalArea <= limit + AreaToleranceSquareMetres, "total area " + Text(totalArea) + " m2, budget " + Text(limit) + " m2"));
        double preferred = 0;
        var preferredCount = 0;
        foreach (var pair in pairs)
        {
            if (pair.Kind == BriefAdjacencyKind.Forbidden)
            {
                double longest = 0;
                foreach (var a in pair.A)
                    foreach (var b in pair.B)
                        if (units[a].Level == units[b].Level) longest = Math.Max(longest, Shared(boxes[a], boxes[b]));
                constraints.Add(Constraint(PlanCodes.AdjacencyForbidden, pair.Subject, true, longest <= Epsilon, "longest shared edge " + Text(longest) + " m"));
                continue;
            }
            int total = 0, met = 0;
            var worst = double.MaxValue;
            foreach (var (side, other) in new[] { (pair.A, pair.B), (pair.B, pair.A) })
                foreach (var a in side)
                {
                    double best = 0;
                    foreach (var b in other)
                        if (units[a].Level == units[b].Level) best = Math.Max(best, Shared(boxes[a], boxes[b]));
                    total++;
                    if (best >= door - SideTolerance) met++;
                    worst = Math.Min(worst, best);
                }
            var detail = Text(met) + " of " + Text(total) + " instances share a wall of at least " + Text(door) + " m; shortest " + Text(total == 0 ? 0 : worst) + " m";
            if (pair.Kind == BriefAdjacencyKind.Required) constraints.Add(Constraint(PlanCodes.AdjacencyRequired, pair.Subject, true, total > 0 && met == total, detail));
            else
            {
                constraints.Add(Constraint(PlanCodes.AdjacencyPreferred, pair.Subject, false, total > 0 && met == total, detail));
                preferred += total == 0 ? 0 : (double)met / total;
                preferredCount++;
            }
        }
        var count = Math.Max(1, units.Count);
        return (constraints, Score(preferredCount == 0 ? 1 : preferred / preferredCount, aspect / count, fit / count));
    }

    // ---- shared measures ----

    // Weighted soft score in [0, 1]: preferred adjacency, room proportions (aspect up to 2:1 is ideal), target areas.
    static double Score(double preferred, double aspect, double fit) => 0.5 * preferred + 0.3 * aspect + 0.2 * fit;

    static double AspectQuality(Box box)
    {
        var longSide = Math.Max(box.X1 - box.X0, box.Y1 - box.Y0);
        return longSide > 0 ? Math.Min(1, box.ShortSide / longSide / 0.5) : 0;
    }

    static double TargetFit(double area, double target) => double.IsNaN(target) ? 1 : Math.Max(0, 1 - Math.Abs(area - target) / target);

    // Length of the common boundary of two rectangles that touch along an edge; 0 when they meet at most in a corner.
    static double Shared(Box a, Box b)
    {
        if (Math.Abs(a.X1 - b.X0) < Epsilon || Math.Abs(b.X1 - a.X0) < Epsilon) return Math.Max(0, Math.Min(a.Y1, b.Y1) - Math.Max(a.Y0, b.Y0));
        if (Math.Abs(a.Y1 - b.Y0) < Epsilon || Math.Abs(b.Y1 - a.Y0) < Epsilon) return Math.Max(0, Math.Min(a.X1, b.X1) - Math.Max(a.X0, b.X0));
        return 0;
    }

    // Manhattan gap between two rectangles (0 when they touch or overlap); guides the search toward contact.
    static double Gap(Box a, Box b) => Math.Max(0, Math.Max(a.X0, b.X0) - Math.Min(a.X1, b.X1)) + Math.Max(0, Math.Max(a.Y0, b.Y0) - Math.Min(a.Y1, b.Y1));

    static bool Overlap(Box a, Box b) => Math.Min(a.X1, b.X1) - Math.Max(a.X0, b.X0) > Epsilon && Math.Min(a.Y1, b.Y1) - Math.Max(a.Y0, b.Y0) > Epsilon;

    static double Snap(double value) => Math.Round(value * GridScale, MidpointRounding.ToEven) / GridScale;
    static double FloorGrid(double value) => Math.Floor(value * GridScale + 1e-6) / GridScale;
    static double CeilGrid(double value) => Math.Ceiling(value * GridScale - 1e-6) / GridScale;
    static double Round(double value) => Math.Round(value, 6, MidpointRounding.ToEven);
    static bool Positive(double value) => double.IsFinite(value) && value > 0;
    static string Text(double value) => SpacePlanSerializer.Format(value);
    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    static PlanConstraint Constraint(string code, string subject, bool hard, bool satisfied, string detail) => new(code, subject, hard, satisfied, detail);
    static PlanConstraint Violation(string code, string subject, string detail) => new(code, subject, true, false, detail);

    static SpacePlan Infeasible(ProjectBrief brief, int seed, int levels, PlanFootprint? footprint, double door, IEnumerable<PlanConstraint> constraints) =>
        new(SpacePlanStatus.Infeasible, BriefParser.IsText(brief.Name) ? brief.Name : "", seed, levels, footprint, door, Array.Empty<PlannedRoom>(),
            constraints.Where(constraint => !constraint.Satisfied).ToArray(), 0);

    // ---- types ----

    sealed class Unit(string instanceId, BriefRoom room, int instance, double minArea, string areaSource, double? maxArea, double minSide, string sideSource, double? target)
    {
        public string InstanceId { get; } = instanceId;
        public BriefRoom Room { get; } = room;
        public int Instance { get; } = instance;
        public double MinArea { get; } = minArea;
        public string AreaSource { get; } = areaSource;
        public double? MaxArea { get; } = maxArea;
        public double MinSide { get; } = minSide;
        public string SideSource { get; } = sideSource;
        public double? Target { get; } = target;
        // Lowest area the planner allocates: a margin above the minimum absorbs grid snapping.
        public double Lower => MinArea + AreaMargin;
        public double Desired { get; set; }
        public int Level { get; set; } = -1;
    }

    sealed record Pair(string Subject, BriefAdjacencyKind Kind, int[] A, int[] B);

    sealed record LevelPair(BriefAdjacencyKind Kind, int[] A, int[] B);

    sealed record LevelProblem(double[] Areas, double[] MinArea, double[] MaxArea, double[] MinSide, double[] Target, LevelPair[] Pairs, double Width, double Depth, double Door);

    readonly record struct Box(double X0, double Y0, double X1, double Y1)
    {
        public double Area => (X1 - X0) * (Y1 - Y0);
        public double ShortSide => Math.Min(X1 - X0, Y1 - Y0);
    }

    sealed class Genome(int[] order, int[] shift, bool[] flip, int width)
    {
        public int[] Order { get; } = order;
        public int[] Shift { get; } = shift;
        public bool[] Flip { get; } = flip;
        public int Width { get; set; } = width;
        public Genome Copy() => new(Order.ToArray(), Shift.ToArray(), Flip.ToArray(), Width);
    }

    // SplitMix64: a fixed, platform-independent pseudo-random sequence per (seed, level) stream.
    sealed class SplitMix(int seed, int stream)
    {
        ulong state = unchecked((ulong)(long)seed * 0x9E3779B97F4A7C15UL + (ulong)stream * 0xD1B54A32D192ED03UL + 0x632BE59BD9B4E019UL);

        public int Next(int bound)
        {
            unchecked
            {
                state += 0x9E3779B97F4A7C15UL;
                var z = state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z ^= z >> 31;
                return (int)(z % (ulong)bound);
            }
        }
    }
}
