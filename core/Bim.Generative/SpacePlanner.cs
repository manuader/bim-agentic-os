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
// 3. Per level, place a building rectangle at the footprint origin with at least the desired total area and tile it
//    with a binary slicing tree: rooms in a permutation order, each node split near half its area (shifted by a gene),
//    cut across its longer side (or flipped by a gene). Coordinates snap to a binary grid of 1/8192 m (about 0.12 mm)
//    after slicing. Grid values, their differences and sums are exact doubles, so a room's X + Width is bit-identical
//    to its neighbour's X, and rooms never overlap.
// 4. Search the genome (permutation, split shifts, cut flips, building width) with seeded threshold accepting from
//    several restarts, minimising hard-constraint deficits first and the soft score second. The budget is fixed, so
//    the same brief, options and seed always give the same plan.
// 5. Build the output rectangles and assess exactly those values against every constraint. Any hard violation gives
//    an infeasible plan with no rooms (code search_exhausted plus the violated constraints); a feasible plan therefore
//    never holds a room below its minimum area or above its maximum area.
// Size caps (MaxLevels, MaxInstances, MaxFootprintMetres) refuse oversized input as plan_too_large before any
// allocation that depends on it, so a valid brief can never make the planner throw, hang or exhaust memory.
public static class SpacePlanner
{
    // Floating-point allowance of the maximum-area and budget checks; minimum areas and sides are checked strictly.
    public const double AreaToleranceSquareMetres = 1e-9;
    public const int MaxLevels = 200;
    public const int MaxInstances = 500;
    public const double MaxFootprintMetres = 10000;
    // Binary grid: 1/8192 m. Multiples of a power of two add and subtract exactly in double precision.
    const double GridScale = 8192;
    // Preferred allocation distance above the minimum (and below the maximum) that absorbs grid snapping; narrowed to
    // half the range when the range is narrower.
    const double AreaMargin = 0.01;
    const double Epsilon = 1e-7;
    // Every allocated area stays strictly positive, so slicing ratios are always defined.
    const double MinimumAllocatedArea = 1e-6;
    // Fixed evaluation budget per restart for large levels.
    const int EvaluationBudget = 120000;
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
        CheckSize(brief, refusals);
        if (refusals.Count > 0) return Infeasible(brief, seed, Math.Min(levels, MaxLevels), null, door, refusals);
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
        // A building rectangle may exceed its desired total by up to one grid row or column.
        var slack = levels * (FloorGrid(footprint.WidthMetres) + FloorGrid(footprint.DepthMetres)) / GridScale;
        Fit(units, levels, usableArea, budget, slack, refusals);
        if (refusals.Count > 0) return Infeasible(brief, seed, levels, footprint, door, refusals);

        var pairs = Pairs(brief, units);
        var boxes = new Box[units.Count];
        for (var level = 0; level < levels; level++)
        {
            var members = Enumerable.Range(0, units.Count).Where(index => units[index].Level == level).ToArray();
            if (members.Length == 0) continue;
            var problem = Problem(units, members, pairs, footprint, door);
            var layout = Search(problem, seed, level);
            if (layout is null)
                return Infeasible(brief, seed, levels, footprint, door, new[] { Violation(PlanCodes.SearchExhausted, "level " + Text(level), "the seeded search produced no finite-cost layout") });
            for (var local = 0; local < members.Length; local++) boxes[members[local]] = layout[local];
        }

        // Grid coordinates make X1 - X0 exact, so Rect.X + Rect.Width reproduces X1 bit for bit.
        var rooms = units.Select((unit, index) => new PlannedRoom(unit.InstanceId, unit.Room.Id, unit.Room.Name, unit.Instance, unit.Level,
            new PlanRect(boxes[index].X0, boxes[index].Y0, boxes[index].X1 - boxes[index].X0, boxes[index].Y1 - boxes[index].Y0))).ToArray();
        var (constraints, score) = Assess(units, rooms, pairs, footprint, levels, door, budget);
        var hardViolations = constraints.Count(constraint => constraint.Hard && !constraint.Satisfied);
        if (hardViolations > 0)
        {
            constraints.Insert(0, Violation(PlanCodes.SearchExhausted, "plan", "the seeded search found no layout meeting every hard constraint; its best layout violates "
                + Text(hardViolations) + " (listed)"));
            return Infeasible(brief, seed, levels, footprint, door, constraints);
        }
        return new SpacePlan(SpacePlanStatus.Feasible, brief.Name, seed, levels, footprint, door, rooms, constraints, Round(score));
    }

    // ---- input ----

    static void CheckSize(ProjectBrief brief, List<PlanConstraint> refusals)
    {
        if (brief.Constraints.Levels is { } levelCount && levelCount.Value > MaxLevels)
            refusals.Add(Violation(PlanCodes.PlanTooLarge, "constraints.levels", Text(levelCount.Value) + " levels requested; at most " + Text(MaxLevels) + " are planned"));
        long instances = 0;
        for (var index = 0; index < brief.Rooms.Count; index++)
        {
            var room = brief.Rooms[index];
            instances += room.EffectiveCount;
            // Compared before any arithmetic, so int.MaxValue cannot overflow a level count.
            if (room.PreferredLevel is { } preferred && preferred.Value >= MaxLevels)
                refusals.Add(Violation(PlanCodes.PlanTooLarge, "rooms[" + Text(index) + "].preferred_level", "level index " + Text(preferred.Value) + " is beyond the " + Text(MaxLevels) + "-level cap"));
        }
        if (instances > MaxInstances)
            refusals.Add(Violation(PlanCodes.PlanTooLarge, "rooms", instances.ToString(CultureInfo.InvariantCulture) + " room instances requested; at most " + Text(MaxInstances) + " are planned"));
    }

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
            refusals.Add(Violation(PlanCodes.FootprintInvalid, "footprint", "width and depth must be finite and at least one 1/8192 m grid step"));
            return null;
        }
        if (footprint.WidthMetres > MaxFootprintMetres || footprint.DepthMetres > MaxFootprintMetres)
        {
            refusals.Add(Violation(PlanCodes.PlanTooLarge, "footprint", "footprint sides are capped at " + Text(MaxFootprintMetres) + " m"));
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
            unit.Desired = Math.Max(unit.Lower, Math.Min(unit.Upper, desired));
        }
    }

    // Preferred levels first; then the remaining instances largest first (longest-processing-time bin packing), each to
    // the level of its first placed required/preferred partner when it still fits there, else to the least loaded level.
    static void AssignLevels(ProjectBrief brief, List<Unit> units, int levels, double usableArea)
    {
        var load = new double[levels];
        var placed = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            if (unit.Room.PreferredLevel is not { } preferred) continue;
            unit.Level = preferred.Value;
            load[unit.Level] += unit.Desired;
            placed.TryAdd(unit.Room.Id, unit.Level);
        }
        var partners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Link(string from, string to)
        {
            if (!partners.TryGetValue(from, out var list)) partners[from] = list = new List<string>();
            list.Add(to);
        }
        foreach (var kind in new[] { BriefAdjacencyKind.Required, BriefAdjacencyKind.Preferred })
            foreach (var adjacency in brief.Adjacencies)
                if (adjacency.Kind == kind) { Link(adjacency.A, adjacency.B); Link(adjacency.B, adjacency.A); }
        var pending = Enumerable.Range(0, units.Count).Where(index => units[index].Level < 0).OrderByDescending(index => units[index].Desired).ThenBy(index => index).ToArray();
        foreach (var index in pending)
        {
            var unit = units[index];
            var chosen = -1;
            if (levels > 1 && partners.TryGetValue(unit.Room.Id, out var list))
            {
                foreach (var partner in list)
                {
                    if (!placed.TryGetValue(partner, out var level)) continue;
                    if (load[level] + unit.Desired <= usableArea) chosen = level;
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
            placed.TryAdd(unit.Room.Id, chosen);
        }
    }

    // Shrinks desired areas so each level fits the footprint and all levels fit the budget (less the grid slack a
    // building rectangle may add). Shrinking only ever lowers an area, so the budget pass never refills a level.
    static void Fit(List<Unit> units, int levels, double usableArea, double? budget, double slack, List<PlanConstraint> refusals)
    {
        for (var level = 0; level < levels; level++)
        {
            var members = units.Where(unit => unit.Level == level).ToList();
            if (members.Count == 0) continue;
            var minimum = members.Sum(unit => unit.MinArea);
            if (minimum > usableArea)
            {
                refusals.Add(Violation(PlanCodes.LevelAreaInsufficient, "level " + Text(level), "minimum areas " + Text(minimum) + " m2 exceed footprint area " + Text(usableArea) + " m2"));
                continue;
            }
            Shrink(members, usableArea);
        }
        if (budget is not { } limit) return;
        // Building rectangles may add up to the grid slack, so the minimums must fit strictly inside budget - slack.
        var total = units.Sum(unit => unit.MinArea);
        var capacity = limit - slack;
        if (total > limit) refusals.Add(Violation(PlanCodes.BudgetInsufficient, "plan", "minimum areas " + Text(total) + " m2 exceed budget " + Text(limit) + " m2"));
        else if (!(capacity > total))
            refusals.Add(Violation(PlanCodes.BudgetInsufficient, "plan", "minimum areas " + Text(total) + " m2 plus grid slack " + Text(slack) + " m2 leave no room within budget " + Text(limit) + " m2"));
        else Shrink(units, capacity);
    }

    // Scales every desired area toward its floor (the margin-raised lower bound, or the minimum when even the floors do
    // not fit) by one common factor in [0, 1); no area ever grows.
    static void Shrink(List<Unit> units, double capacity)
    {
        var desired = units.Sum(unit => unit.Desired);
        if (units.Count == 0 || desired <= capacity) return;
        var floors = units.Sum(unit => Math.Min(unit.Lower, unit.Desired));
        if (floors <= capacity)
        {
            var factor = (capacity - floors) / (desired - floors);
            foreach (var unit in units)
            {
                var floor = Math.Min(unit.Lower, unit.Desired);
                unit.Desired = floor + (unit.Desired - floor) * factor;
            }
            return;
        }
        var minimum = units.Sum(unit => unit.MinArea);
        var scale = desired - minimum > 0 ? Math.Max(0, (capacity - minimum) / (desired - minimum)) : 0;
        foreach (var unit in units) unit.Desired = Math.Max(MinimumAllocatedArea, unit.MinArea + (unit.Desired - unit.MinArea) * scale);
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

    // Null only when no genome had a finite cost; the caller reports that as search_exhausted.
    static Box[]? Search(LevelProblem problem, int seed, int level)
    {
        var count = problem.Areas.Length;
        var random = new SplitMix(seed, level);
        var iterations = Math.Min(300 + 100 * count, Math.Max(300, EvaluationBudget / count));
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
            if (best is null || cost < bestCost) { bestCost = cost; best = current; }
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
        return best is null ? null : Layout(problem, best);
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
        var cost = hard * HardWeight + (1 - score);
        return double.IsFinite(cost) ? cost : double.MaxValue;
    }

    static Box[] Layout(LevelProblem problem, Genome genome)
    {
        var total = Math.Max(problem.Areas.Sum(), MinimumAllocatedArea);
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
        var ratio = total > 0 ? left / total : (double)(cut - low) / (high - low);
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
                    if (best >= problem.Door) met++;
                    else penalty += problem.Door - best + (best <= Epsilon ? 1 + gap : 0);
                }
            if (pair.Kind == BriefAdjacencyKind.Required) hard += penalty;
            else { preferred += (double)met / total; preferredCount++; }
        }
        var count = boxes.Length;
        return (hard, Score(preferredCount == 0 ? 1 : preferred / preferredCount, aspect / count, fit / count));
    }

    // ---- assessment ----

    // Runs on the output rectangles themselves: areas, sides and edges are exactly what the plan reports.
    static (List<PlanConstraint> Constraints, double Score) Assess(List<Unit> units, PlannedRoom[] rooms, List<Pair> pairs, PlanFootprint footprint, int levels, double door, double? budget)
    {
        var constraints = new List<PlanConstraint>();
        var boxes = rooms.Select(room => new Box(room.Rect.X, room.Rect.Y, room.Rect.Right, room.Rect.Top)).ToArray();
        double aspect = 0, fit = 0, totalArea = 0;
        for (var index = 0; index < units.Count; index++)
        {
            var unit = units[index];
            var rect = rooms[index].Rect;
            var box = boxes[index];
            var area = rect.Area;
            var shortSide = Math.Min(rect.Width, rect.Depth);
            totalArea += area;
            aspect += AspectQuality(box);
            fit += TargetFit(area, unit.Target ?? double.NaN);
            constraints.Add(Constraint(PlanCodes.AreaMin, unit.InstanceId, true, area >= unit.MinArea, "area " + Text(area) + " m2, minimum " + Text(unit.MinArea) + " m2 (" + unit.AreaSource + ")"));
            if (unit.MaxArea is { } max)
                constraints.Add(Constraint(PlanCodes.AreaMax, unit.InstanceId, true, area <= max + AreaToleranceSquareMetres, "area " + Text(area) + " m2, maximum " + Text(max) + " m2"));
            if (unit.MinSide > 0)
                constraints.Add(Constraint(PlanCodes.SideMin, unit.InstanceId, true, shortSide >= unit.MinSide,
                    "short side " + Text(shortSide) + " m, minimum " + Text(unit.MinSide) + " m (" + unit.SideSource + ")"));
            var inside = rect.X >= 0 && rect.Y >= 0 && rect.Right <= footprint.WidthMetres && rect.Top <= footprint.DepthMetres
                && rect.Width > 0 && rect.Depth > 0 && unit.Level >= 0 && unit.Level < levels;
            constraints.Add(Constraint(PlanCodes.InSite, unit.InstanceId, true, inside, "rectangle " + Text(box.X0) + "," + Text(box.Y0) + " to " + Text(box.X1) + "," + Text(box.Y1)
                + " on level " + Text(unit.Level) + " within footprint " + Text(footprint.WidthMetres) + " x " + Text(footprint.DepthMetres) + " m"));
        }
        var byLevel = Enumerable.Range(0, levels).Select(level => Enumerable.Range(0, units.Count).Where(index => units[index].Level == level).ToArray()).ToArray();
        for (var level = 0; level < levels; level++)
        {
            var overlaps = 0;
            var members = byLevel[level];
            for (var i = 0; i < members.Length; i++)
                for (var j = i + 1; j < members.Length; j++)
                    if (Overlap(boxes[members[i]], boxes[members[j]])) overlaps++;
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
                    if (best >= door) met++;
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
    // Scaling by a power of two is exact, so these produce exact grid values.
    static double FloorGrid(double value) => Math.Floor(value * GridScale) / GridScale;
    static double CeilGrid(double value) => Math.Ceiling(value * GridScale) / GridScale;
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
        // Allocation stays inside [Lower, Upper]: a margin inside the range absorbs grid snapping.
        public double Margin => MaxArea is { } max ? Math.Min(AreaMargin, (max - MinArea) / 2) : AreaMargin;
        public double Lower => MinArea + Margin;
        public double Upper => MaxArea is { } max ? max - Margin : double.PositiveInfinity;
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
