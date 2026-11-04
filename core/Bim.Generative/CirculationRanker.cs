using System.Globalization;
using Bim.Core;
namespace Bim.Generative;

// Ranks space plans with the circulation model as a seventh criterion, composed on top of AlternativeRanker (whose own
// output, six criteria and canonical form are unchanged; this is an opt-in scorer). Steps:
// 1. Refuse the request before looking at any plan: the alternative count (alternatives_count_invalid), each weight
//    including CirculationWeight (weights_invalid "weights.<name>": negative or non-finite), all seven weights zero or
//    their sum not finite (weights_invalid "weights") and the plan-independent circulation options
//    (circulation_options_invalid).
// 2. AlternativeRanker classifies the plans exactly as on its own (infeasible, empty, invalid rectangles, duplicate
//    geometry, non-finite scores keep their status and reasons) and computes the six criteria. When the six given
//    weights are all zero it runs with unit weights only to obtain the criteria; their effective weights are recomputed.
// 3. For every alternative it ranked, CirculationAnalyzer builds the circulation model. With RequireValid a model that
//    is not valid (failed hard checks, or a plan-specific refusal such as an entrance room missing from that plan)
//    makes the alternative infeasible with reasons "<code>@<subject>: <detail>". Rooms off level 0 are soft
//    circulation_unsupported outcomes and never reject a plan. A duplicate keeps pointing at its original even when the
//    original fails circulation: identical room geometry fails identically.
// 4. circulation criterion over the level-0 rooms (only level 0 is analysed): a room has a valid route when it fails no
//    hard check (reachable; bedroom or dry room without passing through a bedroom). raw = mean route length (m) from
//    the entrance door to the room centres, where every room without a valid route counts the penalty length
//    P = sum over level-0 rooms of (width + depth), an upper bound of any shortest route (a shortest route never
//    re-enters a room and crosses each room in at most its width plus depth); normalized = (valid rooms / rooms) *
//    L / (L + raw) with L = sqrt(total level-0 room area), the side of an equal-area square, so 1 means every room at
//    the door and a mean route as long as L scores 0.5; 0 without an entrance or when the model was refused (the
//    detail then names the failed check or refusal codes). Losing a valid route therefore never raises the score for
//    the same geometry. The detail also records the circulation weight and RequireValid.
// 5. Effective weight = given weight / sum of the seven given weights; contribution = weight * normalized; total = sum
//    of the contributions in criterion order, at most 1. Rank by total descending, ties by generation index; then the
//    unranked alternatives in generation order. With CirculationWeight 0 and RequireValid false the six base
//    contributions, totals and ranks are bit-identical to AlternativeRanker's.
public static class CirculationRanker
{
    public static AlternativeSet Rank(ProjectBrief brief, IReadOnlyList<SpacePlan> plans, CirculationScoring? scoring = null)
    {
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(plans);
        foreach (var plan in plans) ArgumentNullException.ThrowIfNull(plan);
        scoring ??= new CirculationScoring();
        var weights = scoring.Weights ?? new ScoringWeights();
        var options = scoring.Options ?? new CirculationOptions();
        var name = AlternativeRanker.BriefName(brief);
        var refusals = Refusals(plans.Count, weights, scoring.CirculationWeight, options);
        if (refusals.Count > 0) return new AlternativeSet(AlternativeSetStatus.Refused, name, plans.Count, weights, refusals, Array.Empty<Alternative>());

        var given = new[] { weights.AreaFit, weights.Adjacency, weights.Proportions, weights.Compactness, weights.Perimeter, weights.Compliance };
        var baseSum = given.Sum();
        var baseSet = AlternativeRanker.Rank(brief, plans, baseSum > 0 ? weights : new ScoringWeights(1, 1, 1, 1, 1, 1));
        var sum = baseSum + scoring.CirculationWeight;
        var circulationWeight = scoring.CirculationWeight / sum;
        var ranked = new List<Alternative>();
        var rejected = new List<Alternative>();
        foreach (var alternative in baseSet.Alternatives)
        {
            if (alternative.Status != AlternativeStatus.Ranked)
            {
                rejected.Add(alternative);
                continue;
            }
            var model = CirculationAnalyzer.Analyze(brief, alternative.Plan, options);
            if (scoring.RequireValid && !model.IsValid)
            {
                var reasons = model.Checks.Where(check => check.Hard && !check.Satisfied).Select(check => check.Code + "@" + check.Subject + ": " + check.Detail).ToArray();
                rejected.Add(alternative with { Status = AlternativeStatus.Infeasible, Rank = 0, Total = 0, Criteria = Array.Empty<CriterionScore>(), Reasons = reasons, DuplicateOfIndex = null });
                continue;
            }
            var criteria = new List<CriterionScore>(7);
            for (var k = 0; k < alternative.Criteria.Count; k++)
            {
                var criterion = alternative.Criteria[k];
                var weight = given[k] / sum;
                criteria.Add(criterion with { Weight = weight, Contribution = weight * criterion.Normalized });
            }
            var circulation = Criterion(alternative.Plan, model, circulationWeight, scoring.CirculationWeight, scoring.RequireValid);
            if (!double.IsFinite(circulation.Raw) || !double.IsFinite(circulation.Normalized))
            {
                rejected.Add(alternative with { Status = AlternativeStatus.Infeasible, Rank = 0, Total = 0, Criteria = Array.Empty<CriterionScore>(),
                    Reasons = new[] { AlternativeCodes.ScoreNotFinite + "@" + CirculationCodes.Criterion + ": " + circulation.Detail }, DuplicateOfIndex = null });
                continue;
            }
            criteria.Add(circulation);
            double total = 0;
            foreach (var criterion in criteria) total += criterion.Contribution;
            ranked.Add(alternative with { Rank = 0, Total = Math.Min(1, total), Criteria = criteria });
        }
        var ordered = ranked.OrderByDescending(alternative => alternative.Total).ThenBy(alternative => alternative.Index)
            .Select((alternative, position) => alternative with { Rank = position + 1 }).ToList();
        ordered.AddRange(rejected.OrderBy(alternative => alternative.Index));
        var status = ranked.Count > 0 ? AlternativeSetStatus.Ranked : AlternativeSetStatus.Infeasible;
        return new AlternativeSet(status, name, plans.Count, weights, Array.Empty<PlanConstraint>(), ordered);
    }

    // Plans seeds BaseSeed + i (i = 0..Count-1, 32-bit wrap-around) as AlternativeGenerator does and ranks them with
    // circulation; an invalid request is refused before any planning.
    public static AlternativeSet Generate(ProjectBrief brief, AlternativeOptions options, CirculationScoring? scoring = null)
    {
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(options);
        scoring ??= new CirculationScoring();
        var weights = scoring.Weights ?? new ScoringWeights();
        var refusals = Refusals(options.Count, weights, scoring.CirculationWeight, scoring.Options ?? new CirculationOptions());
        if (refusals.Count > 0)
            return new AlternativeSet(AlternativeSetStatus.Refused, AlternativeRanker.BriefName(brief), options.Count, weights, refusals, Array.Empty<Alternative>());
        var plans = new SpacePlan[options.Count];
        for (var index = 0; index < plans.Length; index++) plans[index] = SpacePlanner.Plan(brief, unchecked(options.BaseSeed + index), options.Planner);
        return Rank(brief, plans, scoring);
    }

    // The circulation criterion of one analysed plan (see step 4). The detail records the given and effective circulation
    // weight and the RequireValid setting, so sets ranked with different settings serialize differently.
    internal static CriterionScore Criterion(SpacePlan plan, CirculationModel model, double weight, double givenWeight, bool requireValid)
    {
        var ground = plan.Rooms.Where(room => room.Level == 0).ToArray();
        var scale = Math.Sqrt(ground.Sum(room => room.AreaSquareMetres));
        double penalty = 0;
        foreach (var room in ground) penalty += room.Rect.Width + room.Rect.Depth;
        var upper = plan.Rooms.Count - ground.Length;
        var provenance = "; only level 0 is scored" + (upper > 0 ? " (" + Text(upper) + " rooms off level 0 reported circulation_unsupported)" : "")
            + "; circulation weight " + CirculationAnalyzer.Format(givenWeight) + " given (effective " + CirculationAnalyzer.Format(weight) + "), require_valid " + (requireValid ? "true" : "false");
        double raw = 0, normalized = 0;
        string detail;
        var failed = model.Checks.Where(check => check.Hard && !check.Satisfied).ToArray();
        if (model.Status == CirculationStatus.Refused)
            detail = "circulation refused: " + string.Join(", ", failed.Select(check => check.Code + "@" + check.Subject));
        else
        {
            var ids = new HashSet<string>(ground.Select(room => room.InstanceId), StringComparer.Ordinal);
            var routes = model.Routes.Where(route => ids.Contains(route.Instance)).ToArray();
            var valid = routes.Where(route => route.Reachable && !failed.Any(check => check.Subject == route.Instance)).ToArray();
            var invalid = routes.Length - valid.Length;
            if (routes.Length == 0) detail = "no level-0 rooms to score";
            else if (valid.Length == 0 || model.Entrance is null)
            {
                raw = penalty;
                detail = "0 of " + Text(routes.Length) + " level-0 rooms have a valid route; failed checks: " + string.Join(", ", failed.Select(check => check.Code + "@" + check.Subject));
            }
            else
            {
                double length = 0;
                foreach (var route in valid) length += route.LengthMetres;
                raw = (length + invalid * penalty) / routes.Length;
                normalized = (double)valid.Length / routes.Length * (scale / (scale + raw));
                detail = "mean route " + CirculationAnalyzer.Format(raw) + " m from the entrance door (" + model.Entrance.To + ", "
                    + (model.EntranceEdge is { } edge ? CirculationAnalyzer.EdgeName(edge) : "") + " lot edge) to the room centres; " + Text(valid.Length) + " of " + Text(routes.Length)
                    + " level-0 rooms with a valid route" + (invalid > 0 ? ", each of the " + Text(invalid) + " others counted at the penalty sum(width + depth) = " + CirculationAnalyzer.Format(penalty) + " m" : "")
                    + "; normalized (valid / rooms) * L / (L + mean), L = sqrt(level-0 room area) = " + CirculationAnalyzer.Format(scale) + " m";
            }
        }
        var bounded = double.IsFinite(normalized) ? Math.Clamp(normalized, 0, 1) : normalized;
        return new CriterionScore(CirculationCodes.Criterion, raw, bounded, weight, weight * bounded, detail + provenance);
    }

    static List<PlanConstraint> Refusals(int count, ScoringWeights weights, double circulationWeight, CirculationOptions options)
    {
        var refusals = AlternativeRanker.Refusals(count, weights).Where(refusal => refusal.Subject != "weights").ToList();
        if (!double.IsFinite(circulationWeight) || circulationWeight < 0)
            refusals.Add(new PlanConstraint(AlternativeCodes.WeightsInvalid, "weights.circulation", true, false, "weight must be finite and non-negative"));
        var all = new[] { weights.AreaFit, weights.Adjacency, weights.Proportions, weights.Compactness, weights.Perimeter, weights.Compliance, circulationWeight };
        if (all.All(weight => double.IsFinite(weight) && weight >= 0))
        {
            var sum = all.Sum();
            if (!(sum > 0)) refusals.Add(new PlanConstraint(AlternativeCodes.WeightsInvalid, "weights", true, false, "at least one weight must be positive"));
            else if (!double.IsFinite(sum))
                refusals.Add(new PlanConstraint(AlternativeCodes.WeightsInvalid, "weights", true, false, "the sum of weights must be finite"));
        }
        refusals.AddRange(CirculationAnalyzer.OptionRefusals(options));
        return refusals;
    }

    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
