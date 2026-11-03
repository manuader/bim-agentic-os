using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Bim.Core;
using Bim.Generative;

// P7-02 alternatives oracle: N deterministic seeds, geometric deduplication, weighted multi-criteria scoring with a
// per-criterion explanation that sums to the total, deterministic ranking with a stable tie-break, typed refusals for
// out-of-range counts and invalid weights, and infeasible briefs reported with reasons. Unit-tested evidence only.
static class AlternativeTests
{
    static readonly string[] CriterionNames = { "area_fit", "adjacency", "proportions", "compactness", "perimeter", "compliance" };

    public static void Run(Action<bool, string, string> check, string root)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, name, detail);
        var example = BriefParser.Parse(File.ReadAllText(Path.Combine(root, "examples", "brief", "apartment-2br.json")));

        // ---- bounds and weights: typed refusals, no planning ----
        Check(AlternativeGenerator.MinCount == 1 && AlternativeGenerator.MaxCount == 64, "alternative count is bounded to 1..64");
        foreach (var count in new[] { 0, -3, 65, int.MaxValue, int.MinValue })
        {
            var timer = Stopwatch.StartNew();
            var refused = AlternativeGenerator.Generate(example, new AlternativeOptions(count));
            timer.Stop();
            Check(refused.Status == AlternativeSetStatus.Refused && refused.Alternatives.Count == 0 && refused.Requested == count
                && refused.Refusals.Any(r => r.Code == AlternativeCodes.CountInvalid && r.Subject == "count" && r.Detail.Length > 0) && timer.Elapsed.TotalSeconds < 1,
                "count " + Text(count) + " is refused as alternatives_count_invalid without planning", Describe(refused));
        }
        foreach (var (label, weights) in new[]
        {
            ("all-zero", new ScoringWeights(0, 0, 0, 0, 0, 0)),
            ("negative", new ScoringWeights(AreaFit: -0.1)),
            ("NaN", new ScoringWeights(Adjacency: double.NaN)),
            ("infinite", new ScoringWeights(Compliance: double.PositiveInfinity)),
        })
        {
            var refused = AlternativeGenerator.Generate(example, new AlternativeOptions(2, Weights: weights));
            Check(refused.Status == AlternativeSetStatus.Refused && refused.Alternatives.Count == 0 && refused.Refusals.Any(r => r.Code == AlternativeCodes.WeightsInvalid),
                label + " weights are refused as weights_invalid", Describe(refused));
        }
        var refusedJson = AlternativeSetSerializer.Serialize(AlternativeGenerator.Generate(example, new AlternativeOptions(65)));
        using (var document = JsonDocument.Parse(refusedJson))
            Check(document.RootElement.GetProperty("status").GetString() == "refused" && document.RootElement.GetProperty("alternatives").GetArrayLength() == 0
                && document.RootElement.GetProperty("refusals")[0].GetProperty("code").GetString() == AlternativeCodes.CountInvalid, "a refused set serializes with its refusal", refusedJson);
        var nullThrows = false;
        try { AlternativeGenerator.Generate(null!, new AlternativeOptions(1)); } catch (ArgumentNullException) { nullThrows = true; }
        Check(nullThrows, "a null brief is a programming error (ArgumentNullException)");

        // ---- apartment-2br: N alternatives, distinct seeds, ranked and explained ----
        const int n = 12;
        var set = AlternativeGenerator.Generate(example, new AlternativeOptions(n, BaseSeed: 0));
        Check(set.Status == AlternativeSetStatus.Ranked && set.Requested == n && set.Alternatives.Count == n && set.Refusals.Count == 0 && set.BriefName == example.Name,
            "apartment-2br yields 12 alternatives with a ranked status", Describe(set));
        var seeds = set.Alternatives.OrderBy(a => a.Index).Select(a => a.Seed).ToArray();
        Check(seeds.SequenceEqual(Enumerable.Range(0, n)) && set.Alternatives.Select(a => a.Index).OrderBy(i => i).SequenceEqual(Enumerable.Range(0, n)),
            "alternative i uses seed base + i, so seeds are distinct", string.Join(",", seeds));
        var ranked = set.Ranked;
        Check(ranked.Count >= 2 && ranked.Select(a => a.Total).Distinct().Count() >= 2, "apartment-2br gives at least two ranked alternatives with different totals", Describe(set));
        Check(ranked.Count >= 1 && ranked.Count + set.Alternatives.Count(a => a.Status == AlternativeStatus.Duplicate) + set.Alternatives.Count(a => a.Status == AlternativeStatus.Infeasible) == n,
            "every alternative is ranked, a duplicate or infeasible", Describe(set));
        Check(ranked.Select(a => a.Rank).SequenceEqual(Enumerable.Range(1, ranked.Count)) && set.Alternatives.Take(ranked.Count).SequenceEqual(ranked)
            && set.Alternatives.Where(a => a.Status != AlternativeStatus.Ranked).All(a => a.Rank == 0),
            "ranked alternatives come first with ranks 1..k; others have rank 0", Describe(set));
        string? orderProblem = null;
        for (var i = 0; i + 1 < ranked.Count; i++)
        {
            var (x, y) = (ranked[i], ranked[i + 1]);
            if (!(x.Total > y.Total || (x.Total == y.Total && x.Index < y.Index))) orderProblem ??= "rank " + Text(x.Rank) + " total " + R(x.Total) + " index " + Text(x.Index) + " before total " + R(y.Total) + " index " + Text(y.Index);
        }
        Check(orderProblem is null, "ranking is monotonic in the total score with the generation index as tie-break", orderProblem ?? "");
        var rest = set.Alternatives.Skip(ranked.Count).Select(a => a.Index).ToArray();
        Check(rest.SequenceEqual(rest.OrderBy(i => i)), "unranked alternatives follow in generation order");
        string? explanationProblem = null;
        foreach (var alternative in ranked)
        {
            explanationProblem ??= ExplanationProblem(alternative);
            if (!alternative.Plan.IsFeasible || alternative.Plan.Seed != alternative.Seed) explanationProblem ??= "ranked plan not feasible or seed mismatch at index " + Text(alternative.Index);
            if (SpacePlanSerializer.Serialize(alternative.Plan) != SpacePlanSerializer.Serialize(SpacePlanner.Plan(example, alternative.Seed)))
                explanationProblem ??= "alternative " + Text(alternative.Index) + " differs from SpacePlanner.Plan with its seed";
        }
        Check(explanationProblem is null, "each ranked alternative is the planner's plan with six criteria whose contributions sum to the total", explanationProblem ?? "");
        Check(ranked.All(a => a.Reasons.Count == 0 && a.DuplicateOfIndex is null), "ranked alternatives carry no rejection reasons");
        string? dedupProblem = null;
        var geometries = ranked.Select(a => Geometry(a.Plan)).ToArray();
        if (geometries.Distinct(StringComparer.Ordinal).Count() != geometries.Length) dedupProblem = "two ranked alternatives share a geometry";
        foreach (var duplicate in set.Alternatives.Where(a => a.Status == AlternativeStatus.Duplicate))
        {
            var original = set.Alternatives.FirstOrDefault(a => a.Index == duplicate.DuplicateOfIndex);
            if (original is null || original.Status != AlternativeStatus.Ranked || original.Index >= duplicate.Index || Geometry(original.Plan) != Geometry(duplicate.Plan)
                || !duplicate.Reasons.Any(r => r.Contains(AlternativeCodes.DuplicateGeometry, StringComparison.Ordinal) && r.Contains("seed " + Text(original.Seed), StringComparison.Ordinal))
                || duplicate.Criteria.Count != 0)
                dedupProblem ??= "duplicate " + Text(duplicate.Index) + " does not point at an earlier ranked alternative with the same geometry";
        }
        Check(dedupProblem is null, "generated duplicates point at the earlier ranked alternative with identical geometry", dedupProblem ?? "");

        // ---- determinism ----
        var canonical = AlternativeSetSerializer.Serialize(set);
        Check(AlternativeSetSerializer.Serialize(AlternativeGenerator.Generate(example, new AlternativeOptions(n, BaseSeed: 0))) == canonical,
            "same brief and options give byte-identical alternative sets");
        Check(AlternativeSetSerializer.Serialize(AlternativeGenerator.Generate(example, new AlternativeOptions(n, 0, new PlannerOptions(), new ScoringWeights()))) == canonical,
            "explicit default planner options and weights reproduce the same bytes");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Check(AlternativeSetSerializer.Serialize(AlternativeGenerator.Generate(example, new AlternativeOptions(n, BaseSeed: 0))) == canonical, "alternative output does not depend on the current culture");
        }
        finally { CultureInfo.CurrentCulture = culture; }
        using (var document = JsonDocument.Parse(canonical))
        {
            var top = document.RootElement;
            var alternatives = top.GetProperty("alternatives");
            var first = alternatives[0];
            Check(top.GetProperty("alternatives_version").GetInt32() == 1 && top.GetProperty("status").GetString() == "ranked" && top.GetProperty("requested").GetInt32() == n
                && top.GetProperty("brief").GetString() == example.Name && alternatives.GetArrayLength() == n && top.GetProperty("weights").GetProperty("area_fit").GetDouble() == new ScoringWeights().AreaFit,
                "canonical JSON carries version, status, requested count, brief, weights and every alternative");
            Check(first.GetProperty("rank").GetInt32() == 1 && first.GetProperty("status").GetString() == "ranked" && first.GetProperty("total").GetDouble() == ranked[0].Total
                && first.GetProperty("criteria").GetArrayLength() == CriterionNames.Length && first.GetProperty("criteria")[0].GetProperty("criterion").GetString() == "area_fit"
                && first.GetProperty("criteria")[0].GetProperty("contribution").GetDouble() == ranked[0].Criteria[0].Contribution
                && first.GetProperty("plan").GetProperty("seed").GetInt32() == ranked[0].Seed, "canonical JSON explains the top alternative exactly and embeds its plan");
            Check(top.GetProperty("summary").GetProperty("ranked").GetInt32() == ranked.Count, "canonical JSON summarises the ranked count");
            Check(!canonical.Contains('\n', StringComparison.Ordinal) && canonical.StartsWith("{\"alternatives_version\":1,\"status\":\"ranked\",", StringComparison.Ordinal), "canonical JSON is compact with fixed member order");
        }

        // ---- crafted scoring: weights change the ranking, dedup and rejection ----
        var crafted = Brief("crafted", 20, 20, new[] { Room("a", 8), Room("b", 8) });
        var slim = Plan(crafted, 1, new PlanRect(0, 0, 1, 8), new PlanRect(1, 0, 1, 8));   // exact areas, 8:1 rooms
        var square = Plan(crafted, 2, new PlanRect(0, 0, 3, 3), new PlanRect(3, 0, 3, 3)); // 9 m2 rooms, square
        var slimAgain = Plan(crafted, 3, new PlanRect(0, 0, 1, 8), new PlanRect(1, 0, 1, 8));
        var failed = new SpacePlan(SpacePlanStatus.Infeasible, "crafted", 4, 1, new PlanFootprint(20, 20), 0.9, Array.Empty<PlannedRoom>(),
            new[] { new PlanConstraint(PlanCodes.SearchExhausted, "plan", true, false, "crafted failure") }, 0);
        var plans = new[] { slim, square, slimAgain, failed };
        var byArea = AlternativeRanker.Rank(crafted, plans, new ScoringWeights(1, 0, 0, 0, 0, 0));
        var byShape = AlternativeRanker.Rank(crafted, plans, new ScoringWeights(0, 0, 1, 0, 0, 0));
        Check(byArea.Status == AlternativeSetStatus.Ranked && byArea.Ranked.Count == 2 && byArea.Ranked[0].Seed == 1 && byArea.Ranked[1].Seed == 2,
            "area-fit weights rank the exact-area plan first", Describe(byArea));
        Check(byShape.Ranked.Count == 2 && byShape.Ranked[0].Seed == 2 && byShape.Ranked[1].Seed == 1, "proportion weights rank the square-room plan first", Describe(byShape));
        var areaCriterion = byArea.Ranked[1].Criteria.First(c => c.Criterion == "area_fit");
        Check(Math.Abs(areaCriterion.Raw - 0.125) < 1e-12 && Math.Abs(areaCriterion.Normalized - 0.875) < 1e-12 && areaCriterion.Weight == 1 && areaCriterion.Contribution == areaCriterion.Normalized,
            "area fit raw is the mean relative deviation from target and normalizes to 1 - raw", R(areaCriterion.Raw) + " " + R(areaCriterion.Normalized));
        var shapeCriterion = byShape.Ranked[1].Criteria.First(c => c.Criterion == "proportions");
        Check(Math.Abs(shapeCriterion.Raw - 8) < 1e-12 && Math.Abs(shapeCriterion.Normalized - 0.25) < 1e-12, "proportions raw is the mean aspect ratio; quality is min(1, 2 / aspect)",
            R(shapeCriterion.Raw) + " " + R(shapeCriterion.Normalized));
        Check(byArea.Ranked.Concat(byShape.Ranked).All(a => ExplanationProblem(a) is null), "crafted explanations sum to their totals",
            string.Join("; ", byArea.Ranked.Concat(byShape.Ranked).Select(ExplanationProblem).Where(p => p is not null)));
        var dup = byArea.Alternatives.FirstOrDefault(a => a.Index == 2);
        Check(dup is { Status: AlternativeStatus.Duplicate, DuplicateOfIndex: 0, Rank: 0 } && dup.Reasons.Any(r => r.Contains(AlternativeCodes.DuplicateGeometry, StringComparison.Ordinal) && r.Contains("seed 1", StringComparison.Ordinal)),
            "a plan with the geometry of an earlier plan is removed as a duplicate and reported", Describe(byArea));
        var rejected = byArea.Alternatives.FirstOrDefault(a => a.Index == 3);
        Check(rejected is { Status: AlternativeStatus.Infeasible, Rank: 0 } && rejected.Criteria.Count == 0 && rejected.Reasons.Any(r => r.Contains(PlanCodes.SearchExhausted, StringComparison.Ordinal)),
            "an infeasible plan is rejected with its violations as reasons", Describe(byArea));
        Check(byArea.Alternatives.Select(a => a.Index).SequenceEqual(new[] { 0, 1, 2, 3 }), "rejected alternatives follow the ranked ones in generation order");
        var tie = AlternativeRanker.Rank(crafted, new[] { square, Plan(crafted, 9, new PlanRect(0, 0, 3, 3), new PlanRect(0, 3, 3, 3)) }, new ScoringWeights(1, 0, 0, 0, 0, 0));
        Check(tie.Ranked.Count == 2 && tie.Ranked[0].Total == tie.Ranked[1].Total && tie.Ranked[0].Index == 0 && tie.Ranked[1].Index == 1, "equal totals keep generation order", Describe(tie));
        var adjacency = Brief("adjacent", 20, 20, new[] { Room("a", 8), Room("b", 8), Room("c", 8) }, new[] { Adj("a", "b", BriefAdjacencyKind.Preferred), Adj("a", "c", BriefAdjacencyKind.Required) });
        var touching = Plan(adjacency, 1, new PlanRect(0, 0, 2, 4), new PlanRect(2, 0, 2, 4), new PlanRect(0, 4, 2, 4));
        var apart = Plan(adjacency, 2, new PlanRect(0, 0, 2, 4), new PlanRect(4, 0, 2, 4), new PlanRect(0, 4, 2, 4));
        var adjacencySet = AlternativeRanker.Rank(adjacency, new[] { apart, touching }, new ScoringWeights(0, 1, 0, 0, 0, 0));
        var adjacencyRaw = adjacencySet.Ranked.Select(a => a.Criteria.First(c => c.Criterion == "adjacency").Raw).ToArray();
        Check(adjacencySet.Ranked.Count == 2 && adjacencySet.Ranked[0].Seed == 1 && adjacencyRaw[0] == 1 && adjacencyRaw[1] == 0,
            "adjacency scores preferred pairs only: the required pair (a hard planner gate) does not dilute it", string.Join(",", adjacencyRaw.Select(R)));
        var rankerRefused = AlternativeRanker.Rank(crafted, Array.Empty<SpacePlan>());
        Check(rankerRefused.Status == AlternativeSetStatus.Refused && rankerRefused.Refusals.Any(r => r.Code == AlternativeCodes.CountInvalid), "ranking zero plans is refused", Describe(rankerRefused));

        // ---- review fixes: compactness fill, preferred-only adjacency, order-free dedup, area fit, rejections ----
        var unit = Brief("unit", 20, 20, new[] { Room("a", 1), Room("b", 1) });
        var touchingPair = Plan(unit, 1, new PlanRect(0, 0, 1, 1), new PlanRect(1, 0, 1, 1));
        var scattered = Plan(unit, 2, new PlanRect(0, 0, 1, 1), new PlanRect(9, 9, 1, 1));
        var compact = AlternativeRanker.Rank(unit, new[] { scattered, touchingPair }, new ScoringWeights(0, 0, 0, 1, 0, 0));
        Check(compact.Ranked.Count == 2 && compact.Ranked[0].Seed == 1 && compact.Ranked[1].Criteria.First(c => c.Criterion == "compactness").Normalized < 0.5,
            "compactness rewards rooms that fill their envelope over scattered rooms", Describe(compact));
        var beds = Brief("beds", 20, 20, new[] { Room("bed", 9, count: 2), Room("hall", 4) });
        var bedsA = Explicit("beds", 1, Placed("bed#1", "bed", 1, 0, 0, 3, 3), Placed("bed#2", "bed", 2, 3, 0, 3, 3), Placed("hall", "hall", 1, 0, 3, 6, 1));
        var bedsSwapped = Explicit("beds", 2, Placed("bed#1", "bed", 1, 3, 0, 3, 3), Placed("bed#2", "bed", 2, 0, 0, 3, 3), Placed("hall", "hall", 1, 0, 3, 6, 1));
        var bedsReordered = Explicit("beds", 3, Placed("hall", "hall", 1, 0, 3, 6, 1), Placed("bed#1", "bed", 1, 0, 0, 3, 3), Placed("bed#2", "bed", 2, 3, 0, 3, 3));
        var bedsSet = AlternativeRanker.Rank(beds, new[] { bedsA, bedsSwapped, bedsReordered });
        Check(bedsSet.Ranked.Count == 1 && bedsSet.Alternatives.Where(a => a.Index > 0).All(a => a.Status == AlternativeStatus.Duplicate && a.DuplicateOfIndex == 0),
            "swapping instances of one room or reordering the room list is still a duplicate", Describe(bedsSet));
        var ranges = Brief("ranges", 20, 20, new[] { Room("r", min: 6, max: 10), Room("s", 8) });
        var inside = Plan(ranges, 1, new PlanRect(0, 0, 3, 3), new PlanRect(3, 0, 2, 4));
        var outside = Plan(ranges, 2, new PlanRect(0, 0, 4, 3), new PlanRect(4, 0, 2, 4));
        var rangeFit = AlternativeRanker.Rank(ranges, new[] { inside, outside }).Alternatives.OrderBy(a => a.Index).Select(a => a.Criteria.First(c => c.Criterion == "area_fit")).ToArray();
        Check(rangeFit.Length == 2 && rangeFit[0].Raw == 0 && rangeFit[0].Normalized == 1 && Math.Abs(rangeFit[1].Raw - 0.1) < 1e-12,
            "a range-only room scores full area fit anywhere inside its range and is penalised only outside", string.Join(",", rangeFit.Select(c => R(c.Raw))));
        var far = Plan(crafted, 1, new PlanRect(0, 0, 4, 6), new PlanRect(4, 0, 2, 4));
        var farFit = AlternativeRanker.Rank(crafted, new[] { far }).Ranked.Select(a => a.Criteria.First(c => c.Criterion == "area_fit")).FirstOrDefault();
        Check(farFit is not null && Math.Abs(farFit.Raw - 0.5) < 1e-12 && Math.Abs(farFit.Normalized - 0.5) < 1e-12, "area fit clamps each room's deviation to 1 before averaging",
            farFit is null ? "" : R(farFit.Raw));
        var degenerate = AlternativeRanker.Rank(crafted, new[] { Plan(crafted, 1, new PlanRect(0, 0, 0, 4), new PlanRect(0, 0, 2, 4)), slim });
        Check(degenerate.Ranked.Count == 1 && degenerate.Alternatives.Any(a => a.Index == 0 && a.Status == AlternativeStatus.Infeasible && a.Reasons.Any(r => r.Contains(AlternativeCodes.RectInvalid, StringComparison.Ordinal))),
            "a room with zero width is rejected as rect_invalid", Describe(degenerate));
        var huge = Plan(unit, 1, new PlanRect(0, 0, 1, 1), new PlanRect(1e308, 0, 1, 1));
        var hugeSet = AlternativeRanker.Rank(unit, new[] { huge, Plan(unit, 2, new PlanRect(0, 0, 1, 1), new PlanRect(1e308, 0, 1, 1)), touchingPair });
        Check(hugeSet.Alternatives.Where(a => a.Index < 2).All(a => a.Status == AlternativeStatus.Infeasible && a.DuplicateOfIndex is null && a.Reasons.Any(r => r.Contains(AlternativeCodes.ScoreNotFinite, StringComparison.Ordinal)))
            && hugeSet.Ranked.Count == 1, "a plan rejected for a non-finite score is never the original of a duplicate", Describe(hugeSet));

        // ---- infeasible briefs: reported, not thrown ----
        var crowded = Brief("crowded", 4, 4, new[] { Room("a", min: 10), Room("b", min: 10) });
        var infeasible = AlternativeGenerator.Generate(crowded, new AlternativeOptions(3));
        Check(infeasible.Status == AlternativeSetStatus.Infeasible && infeasible.Alternatives.Count == 3 && infeasible.Ranked.Count == 0
            && infeasible.Alternatives.All(a => a.Status == AlternativeStatus.Infeasible && a.Rank == 0 && a.Reasons.Any(r => r.Contains(PlanCodes.LevelAreaInsufficient, StringComparison.Ordinal))),
            "an infeasible brief gives an infeasible set whose alternatives carry the planner's reasons", Describe(infeasible));
        Check(AlternativeSetSerializer.Serialize(infeasible).StartsWith("{\"alternatives_version\":1,\"status\":\"infeasible\",", StringComparison.Ordinal)
            && AlternativeSetSerializer.Serialize(infeasible) == AlternativeSetSerializer.Serialize(AlternativeGenerator.Generate(crowded, new AlternativeOptions(3))), "infeasible sets serialize deterministically");

        // ---- the upper bound plans within time ----
        var small = Brief("small", 6, 6, new[] { Room("a", 10), Room("b", 8), Room("c", 6) }, new[] { Adj("a", "b", BriefAdjacencyKind.Required) });
        var watch = Stopwatch.StartNew();
        var full = AlternativeGenerator.Generate(small, new AlternativeOptions(AlternativeGenerator.MaxCount, BaseSeed: int.MaxValue - 10));
        watch.Stop();
        Check(full.Alternatives.Count == 64 && full.Alternatives.Select(a => a.Seed).Distinct().Count() == 64 && full.Ranked.Count >= 2 && full.Ranked.Select(a => a.Total).Distinct().Count() >= 2 && full.Ranked.All(a => ExplanationProblem(a) is null),
            "64 alternatives (seeds wrapping past int.MaxValue) are distinct and explained", Describe(full));
        Check(watch.Elapsed.TotalSeconds < 30, "64 alternatives of a 3-room brief take under 30 s", watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
    }

    // Null when the alternative has the six criteria in order, normalized scores in [0, 1], effective weights summing
    // to 1, contributions equal to weight x normalized, and a total equal to the in-order sum of contributions.
    static string? ExplanationProblem(Alternative alternative)
    {
        var criteria = alternative.Criteria;
        if (!criteria.Select(c => c.Criterion).SequenceEqual(CriterionNames)) return "criteria " + string.Join(",", criteria.Select(c => c.Criterion));
        double sum = 0, weights = 0;
        foreach (var criterion in criteria)
        {
            if (!(criterion.Normalized >= 0 && criterion.Normalized <= 1) || !double.IsFinite(criterion.Raw) || criterion.Detail.Length == 0) return criterion.Criterion + " normalized " + R(criterion.Normalized);
            if (!(criterion.Weight >= 0) || criterion.Contribution != criterion.Weight * criterion.Normalized) return criterion.Criterion + " contribution is not weight x normalized";
            sum += criterion.Contribution;
            weights += criterion.Weight;
        }
        if (Math.Abs(weights - 1) > 1e-12) return "weights sum to " + R(weights);
        // The total is the in-order sum, clamped to 1 only against rounding.
        var expected = Math.Min(1, sum);
        return expected == alternative.Total && sum - alternative.Total < 1e-12 && alternative.Total >= 0 && alternative.Total <= 1 ? null : "contributions sum to " + R(sum) + " but total is " + R(alternative.Total);
    }

    static string Geometry(SpacePlan plan) => string.Join(";", plan.Rooms.Select(r => r.InstanceId + "@" + Text(r.Level) + ":" + R(r.Rect.X) + "," + R(r.Rect.Y) + "," + R(r.Rect.Width) + "," + R(r.Rect.Depth)));

    static string Describe(AlternativeSet set) => set.Status + " " + string.Join(" | ", set.Refusals.Select(r => r.Code + "@" + r.Subject)) + " | "
        + string.Join(" | ", set.Alternatives.Take(8).Select(a => Text(a.Index) + ":" + a.Status + " r" + Text(a.Rank) + " s" + Text(a.Seed) + " t" + R(a.Total) + " " + string.Join(",", a.Reasons.Take(2))));

    static SpacePlan Plan(ProjectBrief brief, int seed, params PlanRect[] rects) =>
        new(SpacePlanStatus.Feasible, brief.Name, seed, 1, new PlanFootprint(brief.Constraints.Site!.WidthMetres.Value, brief.Constraints.Site.DepthMetres.Value), 0.9,
            brief.Rooms.Select((room, index) => new PlannedRoom(room.Id, room.Id, room.Name, 1, 0, rects[index])).ToArray(), Array.Empty<PlanConstraint>(), 0);

    static BriefValue<double> V(double value) => new(value);

    static BriefRoom Room(string id, double? target = null, double? min = null, double? max = null, int? count = null) =>
        new(id, id, count is { } c ? new BriefValue<int>(c) : null, target is { } t ? V(t) : null, min is { } low ? V(low) : null, max is { } high ? V(high) : null);

    static SpacePlan Explicit(string brief, int seed, params PlannedRoom[] rooms) =>
        new(SpacePlanStatus.Feasible, brief, seed, 1, new PlanFootprint(20, 20), 0.9, rooms, Array.Empty<PlanConstraint>(), 0);

    static PlannedRoom Placed(string instance, string room, int index, double x, double y, double width, double depth) =>
        new(instance, room, room, index, 0, new PlanRect(x, y, width, depth));

    static BriefAdjacency Adj(string a, string b, BriefAdjacencyKind kind) => new(a, b, kind);

    static ProjectBrief Brief(string name, double width, double depth, BriefRoom[] rooms, BriefAdjacency[]? adjacencies = null) =>
        new(1, name, new BriefConstraints(null, new BriefSite(V(width), V(depth))), rooms, adjacencies ?? Array.Empty<BriefAdjacency>());

    static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
