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
//    is not valid (failed checks, or a plan-specific refusal such as an entrance room missing from that plan) makes the
//    alternative infeasible with reasons "<code>@<subject>: <detail>". A duplicate keeps pointing at its original even
//    when the original fails circulation: identical room geometry fails identically.
// 4. circulation criterion: raw = mean route length (m) from the entrance door to the room centres over the rooms
//    with a valid route (reachable; for a bedroom, without passing through another bedroom); normalized =
//    (valid rooms / rooms) * L / (L + raw) with L = sqrt(total area of the level-0 rooms), the side of an equal-area
//    square, so 1 means every room at the door and a mean route as long as L scores 0.5; 0 when no route is valid.
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
                var reasons = model.Checks.Where(check => !check.Satisfied).Select(check => check.Code + "@" + check.Subject + ": " + check.Detail).ToArray();
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
            var circulation = Criterion(alternative.Plan, model, circulationWeight);
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

    // The circulation criterion of one analysed plan (see step 4).
    public static CriterionScore Criterion(SpacePlan plan, CirculationModel model, double weight)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(model);
        var scale = Math.Sqrt(plan.Rooms.Where(room => room.Level == 0).Sum(room => room.AreaSquareMetres));
        var valid = model.Routes.Where(route => route.Reachable && (!route.IsBedroom || route.AvoidsBedrooms)).ToArray();
        var total = model.Routes.Count;
        double raw = 0, normalized = 0;
        string detail;
        if (valid.Length > 0 && total > 0)
        {
            double length = 0;
            foreach (var route in valid) length += route.LengthMetres;
            raw = length / valid.Length;
            normalized = (double)valid.Length / total * (scale / (scale + raw));
            detail = "mean route " + CirculationAnalyzer.Format(raw) + " m from the entrance door (" + (model.Entrance?.To ?? "") + ", "
                + (model.EntranceEdge is { } edge ? CirculationAnalyzer.EdgeName(edge) : "") + " lot edge) to the room centres over " + Text(valid.Length) + " of " + Text(total)
                + " rooms with a valid route; normalized (valid / rooms) * L / (L + mean), L = sqrt(level-0 room area) = " + CirculationAnalyzer.Format(scale) + " m";
        }
        else detail = model.Entrance is null ? "no entrance on the lot edge, so no room has a valid route" : "0 of " + Text(total) + " rooms have a valid route";
        var bounded = double.IsFinite(normalized) ? Math.Clamp(normalized, 0, 1) : normalized;
        return new CriterionScore(CirculationCodes.Criterion, raw, bounded, weight, weight * bounded, detail);
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
