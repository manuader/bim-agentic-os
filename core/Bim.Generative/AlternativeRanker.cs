using System.Globalization;
using Bim.Core;
namespace Bim.Generative;

// Scores and ranks a list of space plans of one brief. Steps:
// 1. Refuse an empty or oversized list (alternatives_count_invalid, 1..AlternativeGenerator.MaxCount) and invalid
//    weights (weights_invalid: any weight negative or non-finite, or all zero) before looking at any plan.
// 2. Reject every infeasible plan (status infeasible, reasons = its violations) and every feasible plan without rooms
//    (plan_empty). A feasible plan whose rooms (instance id, level, x, y, width, depth, compared bit for bit) equal
//    those of an earlier feasible plan is a duplicate (duplicate_geometry naming that plan's seed and index).
// 3. Score each remaining plan with six criteria, each normalized to [0, 1] with higher better:
//    area_fit     raw: mean relative deviation |area - reference| / reference over the instances that have a reference
//                 area (the brief target, else the midpoint of a min/max range); normalized: max(0, 1 - raw); raw 0
//                 when no room has a reference.
//    adjacency    raw: fraction of instance checks met over the brief's required and preferred adjacencies (each
//                 instance of either room must share a wall of at least the plan's door width with an instance of the
//                 other room on its level); normalized: raw; 1 when the brief has no such adjacency.
//    proportions  raw: mean room aspect ratio (long side / short side); normalized: mean per-room quality
//                 min(1, 2 / aspect), so proportions up to 2:1 are ideal.
//    compactness  raw: mean over occupied levels of the room bounding box perimeter / (4 * sqrt(box area)), 1 for a
//                 square envelope; normalized: 1 / raw.
//    perimeter    raw: total room perimeter / total room area (m of wall per m2, a circulation and wall-length proxy);
//                 normalized: sum of 4 * sqrt(room area) (the perimeter of equal-area squares) / total room perimeter.
//    compliance   raw: fraction of the plan's hard planner checks satisfied (area_min/area_max/side_min including rule
//                 minimums such as CABA dimensions, in_site, no_overlap, area_budget, required/forbidden adjacency);
//                 normalized: raw; 1 when the plan lists none. Only feasible plans are ranked, so it is 1 for every
//                 ranked alternative and documents the gate rather than separating them.
//    Each weight is divided by the sum of weights; contribution = weight * normalized; the total is the sum of the
//    contributions in the order above, so the explanation always adds up to the total. A non-finite measure rejects
//    the plan (score_not_finite).
// 4. Rank by total descending, ties by generation index ascending (a stable, deterministic order). Ranked
//    alternatives come first with ranks 1..k, then duplicates and infeasible ones in generation order.
public static class AlternativeRanker
{
    // Edges closer than this coincide (the planner's own tolerance).
    const double Epsilon = 1e-7;
    const double IdealAspect = 2;

    public static AlternativeSet Rank(ProjectBrief brief, IReadOnlyList<SpacePlan> plans, ScoringWeights? weights = null)
    {
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(plans);
        foreach (var plan in plans) ArgumentNullException.ThrowIfNull(plan);
        weights ??= new ScoringWeights();
        var name = BriefName(brief);
        var refusals = Refusals(plans.Count, weights);
        if (refusals.Count > 0) return new AlternativeSet(AlternativeSetStatus.Refused, name, plans.Count, weights, refusals, Array.Empty<Alternative>());

        var given = new[] { weights.AreaFit, weights.Adjacency, weights.Proportions, weights.Compactness, weights.Perimeter, weights.Compliance };
        var sum = given.Sum();
        var effective = given.Select(weight => weight / sum).ToArray();
        var scored = new List<Alternative>();
        var rejected = new List<Alternative>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < plans.Count; index++)
        {
            var plan = plans[index];
            if (!plan.IsFeasible)
            {
                var reasons = plan.Violations.Select(violation => violation.Code + "@" + violation.Subject + ": " + violation.Detail).ToList();
                if (reasons.Count == 0) reasons.Add("infeasible@plan: the plan lists no violations");
                rejected.Add(Rejected(index, plan, AlternativeStatus.Infeasible, reasons, null));
                continue;
            }
            if (plan.Rooms.Count == 0)
            {
                rejected.Add(Rejected(index, plan, AlternativeStatus.Infeasible, new[] { AlternativeCodes.PlanEmpty + "@plan: a feasible plan without rooms cannot be scored" }, null));
                continue;
            }
            var key = GeometryKey(plan);
            if (seen.TryGetValue(key, out var original))
            {
                rejected.Add(Rejected(index, plan, AlternativeStatus.Duplicate, new[] { AlternativeCodes.DuplicateGeometry + "@plan: same room geometry as seed "
                    + Text(plans[original].Seed) + " (index " + Text(original) + ")" }, original));
                continue;
            }
            seen[key] = index;
            var criteria = Score(brief, plan, effective);
            if (criteria.Any(criterion => !double.IsFinite(criterion.Raw) || !double.IsFinite(criterion.Normalized)))
            {
                rejected.Add(Rejected(index, plan, AlternativeStatus.Infeasible, criteria.Where(criterion => !double.IsFinite(criterion.Raw) || !double.IsFinite(criterion.Normalized))
                    .Select(criterion => AlternativeCodes.ScoreNotFinite + "@" + criterion.Criterion + ": " + criterion.Detail).ToArray(), null));
                continue;
            }
            double total = 0;
            foreach (var criterion in criteria) total += criterion.Contribution;
            scored.Add(new Alternative(index, plan.Seed, AlternativeStatus.Ranked, 0, total, criteria, Array.Empty<string>(), null, plan));
        }
        var ordered = scored.OrderByDescending(alternative => alternative.Total).ThenBy(alternative => alternative.Index)
            .Select((alternative, position) => alternative with { Rank = position + 1 }).ToList();
        ordered.AddRange(rejected.OrderBy(alternative => alternative.Index));
        var status = scored.Count > 0 ? AlternativeSetStatus.Ranked : AlternativeSetStatus.Infeasible;
        return new AlternativeSet(status, name, plans.Count, weights, Array.Empty<PlanConstraint>(), ordered);
    }

    internal static List<PlanConstraint> Refusals(int count, ScoringWeights weights)
    {
        var refusals = new List<PlanConstraint>();
        if (count < AlternativeGenerator.MinCount || count > AlternativeGenerator.MaxCount)
            refusals.Add(new PlanConstraint(AlternativeCodes.CountInvalid, "count", true, false, Text(count) + " alternatives requested; between "
                + Text(AlternativeGenerator.MinCount) + " and " + Text(AlternativeGenerator.MaxCount) + " are generated"));
        var named = new (string Name, double Value)[]
        {
            ("area_fit", weights.AreaFit), ("adjacency", weights.Adjacency), ("proportions", weights.Proportions),
            ("compactness", weights.Compactness), ("perimeter", weights.Perimeter), ("compliance", weights.Compliance),
        };
        foreach (var (name, value) in named)
            if (!double.IsFinite(value) || value < 0) refusals.Add(new PlanConstraint(AlternativeCodes.WeightsInvalid, "weights." + name, true, false, "weight must be finite and non-negative"));
        if (named.All(weight => double.IsFinite(weight.Value) && weight.Value >= 0) && !(named.Sum(weight => weight.Value) > 0))
            refusals.Add(new PlanConstraint(AlternativeCodes.WeightsInvalid, "weights", true, false, "at least one weight must be positive"));
        else if (named.All(weight => double.IsFinite(weight.Value) && weight.Value >= 0) && !double.IsFinite(named.Sum(weight => weight.Value)))
            refusals.Add(new PlanConstraint(AlternativeCodes.WeightsInvalid, "weights", true, false, "the sum of weights must be finite"));
        return refusals;
    }

    internal static string BriefName(ProjectBrief brief) => BriefParser.IsText(brief.Name) ? brief.Name : "";

    static Alternative Rejected(int index, SpacePlan plan, AlternativeStatus status, IReadOnlyList<string> reasons, int? duplicateOf) =>
        new(index, plan.Seed, status, 0, 0, Array.Empty<CriterionScore>(), reasons, duplicateOf, plan);

    static string GeometryKey(SpacePlan plan) => string.Join(";", plan.Rooms.Select(room => room.InstanceId + "\u0001" + Text(room.Level) + "\u0001" + Bits(room.Rect.X)
        + "," + Bits(room.Rect.Y) + "," + Bits(room.Rect.Width) + "," + Bits(room.Rect.Depth)));

    static string Bits(double value) => BitConverter.DoubleToInt64Bits(value == 0 ? 0 : value).ToString(CultureInfo.InvariantCulture);

    static CriterionScore[] Score(ProjectBrief brief, SpacePlan plan, double[] weights)
    {
        var rooms = plan.Rooms;
        var specs = new Dictionary<string, BriefRoom>(StringComparer.Ordinal);
        foreach (var room in brief.Rooms) specs.TryAdd(room.Id, room);

        // area_fit
        double deviation = 0;
        var referenced = 0;
        foreach (var room in rooms)
        {
            if (!specs.TryGetValue(room.RoomId, out var spec)) continue;
            double? reference = spec.TargetAreaSquareMetres?.Value
                ?? (spec.MinAreaSquareMetres is { } low && spec.MaxAreaSquareMetres is { } high ? (double?)((low.Value + high.Value) / 2) : null);
            if (reference is not { } value || !(value > 0)) continue;
            deviation += Math.Abs(room.AreaSquareMetres - value) / value;
            referenced++;
        }
        var areaRaw = referenced == 0 ? 0 : deviation / referenced;
        var areaDetail = referenced == 0 ? "no room has a target area or min/max range"
            : "mean relative deviation from the reference area over " + Text(referenced) + " of " + Text(rooms.Count) + " rooms (target, else range midpoint)";

        // adjacency
        var door = plan.DoorWidthMetres;
        int checks = 0, met = 0, pairCount = 0;
        var pairs = new HashSet<(string, string, BriefAdjacencyKind)>();
        foreach (var adjacency in brief.Adjacencies)
        {
            if (adjacency.Kind == BriefAdjacencyKind.Forbidden) continue;
            var key = string.CompareOrdinal(adjacency.A, adjacency.B) < 0 ? (adjacency.A, adjacency.B, adjacency.Kind) : (adjacency.B, adjacency.A, adjacency.Kind);
            if (!pairs.Add(key)) continue;
            pairCount++;
            var a = rooms.Where(room => room.RoomId == adjacency.A).ToArray();
            var b = rooms.Where(room => room.RoomId == adjacency.B).ToArray();
            foreach (var (side, other) in new[] { (a, b), (b, a) })
                foreach (var room in side)
                {
                    checks++;
                    if (other.Any(partner => partner.Level == room.Level && Shared(room.Rect, partner.Rect) >= door)) met++;
                }
        }
        var adjacencyRaw = checks == 0 ? 1 : (double)met / checks;
        var adjacencyDetail = checks == 0 ? "the brief has no required or preferred adjacency"
            : Text(met) + " of " + Text(checks) + " instance checks over " + Text(pairCount) + " required/preferred adjacencies share a wall of at least " + Format(door) + " m";

        // proportions
        double aspectSum = 0, qualitySum = 0;
        foreach (var room in rooms)
        {
            var shortSide = Math.Min(room.Rect.Width, room.Rect.Depth);
            var longSide = Math.Max(room.Rect.Width, room.Rect.Depth);
            var aspect = longSide / shortSide;
            aspectSum += aspect;
            qualitySum += Math.Min(1, IdealAspect / aspect);
        }
        var aspectRaw = aspectSum / rooms.Count;
        var aspectNormalized = qualitySum / rooms.Count;

        // compactness
        double ratioSum = 0;
        var levels = 0;
        foreach (var group in rooms.GroupBy(room => room.Level).OrderBy(group => group.Key))
        {
            var width = group.Max(room => room.Rect.Right) - group.Min(room => room.Rect.X);
            var depth = group.Max(room => room.Rect.Top) - group.Min(room => room.Rect.Y);
            ratioSum += 2 * (width + depth) / (4 * Math.Sqrt(width * depth));
            levels++;
        }
        var compactRaw = ratioSum / levels;

        // perimeter
        double perimeter = 0, area = 0, ideal = 0;
        foreach (var room in rooms)
        {
            perimeter += 2 * (room.Rect.Width + room.Rect.Depth);
            area += room.AreaSquareMetres;
            ideal += 4 * Math.Sqrt(room.AreaSquareMetres);
        }
        var perimeterRaw = perimeter / area;

        // compliance
        var hard = plan.Constraints.Where(constraint => constraint.Hard).ToArray();
        var satisfied = hard.Count(constraint => constraint.Satisfied);
        var complianceRaw = hard.Length == 0 ? 1 : (double)satisfied / hard.Length;

        return new[]
        {
            Criterion("area_fit", areaRaw, Math.Max(0, 1 - areaRaw), weights[0], areaDetail),
            Criterion("adjacency", adjacencyRaw, adjacencyRaw, weights[1], adjacencyDetail),
            Criterion("proportions", aspectRaw, aspectNormalized, weights[2], "mean aspect ratio " + Format(aspectRaw) + " over " + Text(rooms.Count) + " rooms; quality min(1, 2 / aspect)"),
            Criterion("compactness", compactRaw, 1 / compactRaw, weights[3], "mean envelope perimeter / (4 sqrt(area)) over " + Text(levels) + " levels; 1 is a square envelope"),
            Criterion("perimeter", perimeterRaw, ideal / perimeter, weights[4], "room perimeter " + Format(perimeter) + " m over " + Format(area) + " m2; equal-area squares need " + Format(ideal) + " m"),
            Criterion("compliance", complianceRaw, complianceRaw, weights[5], hard.Length == 0 ? "the plan lists no hard planner checks"
                : Text(satisfied) + " of " + Text(hard.Length) + " hard planner checks satisfied (areas, sides incl. rule minimums, site, overlap, budget, adjacency)"),
        };
    }

    // Normalized scores are clamped to [0, 1] against rounding; a non-finite score stays non-finite and rejects the plan.
    static CriterionScore Criterion(string name, double raw, double normalized, double weight, string detail)
    {
        var bounded = double.IsFinite(normalized) ? Math.Clamp(normalized, 0, 1) : normalized;
        return new CriterionScore(name, raw, bounded, weight, weight * bounded, detail);
    }

    // Length of the common boundary of two rectangles touching along an edge; 0 when they meet at most in a corner.
    static double Shared(PlanRect a, PlanRect b)
    {
        if (Math.Abs(a.Right - b.X) < Epsilon || Math.Abs(b.Right - a.X) < Epsilon) return Math.Max(0, Math.Min(a.Top, b.Top) - Math.Max(a.Y, b.Y));
        if (Math.Abs(a.Top - b.Y) < Epsilon || Math.Abs(b.Top - a.Y) < Epsilon) return Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X));
        return 0;
    }

    static string Format(double value) => double.IsFinite(value) ? SpacePlanSerializer.Format(value) : "non-finite";
    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
