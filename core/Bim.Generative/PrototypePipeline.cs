using System.Globalization;
using System.Runtime.CompilerServices;
using Bim.Core;

[assembly: InternalsVisibleTo("Bim.Generative.Tests")]
namespace Bim.Generative;

// The P7-07 prototype pipeline: an opt-in composition of the existing stages, which it calls unchanged. Steps:
// 1. Refuse the options before ranking, listing every problem (Rank adds CirculationRanker's own refusals: alternative
//    count, ranking weights, all seven weights zero): an empty project id or a negative revision
//    (pipeline_project_invalid), a negative or non-finite circulation weight (weights_invalid "weights.circulation"),
//    the plan-independent circulation options (circulation_options_invalid), the facade options, pack and parameter
//    overrides (FacadePlanner's facade_options_invalid / facade_pack_invalid / overrides, found by placing a fixed
//    one-room probe plan) and the BIM-IR options (facade_ir_options_invalid). Run also lists the PrototypeGenerator
//    refusals of the request (they come first); the generator is called exactly as on its own, so its result, canonical
//    JSON and pinned digests are unchanged.
// 1b. P7-08, only when options.Snap is set: every plan goes through PrototypeSnap.Snap (wall thickness of options.Ir); a
//    snapped plan replaces the generated one in every later step, a refused snap becomes an infeasible plan whose
//    violations are its prototype_snap_* refusals (rejected at the prototype gate). Snap options are refused with the
//    other options (prototype_snap_options_invalid). With Snap null nothing here runs and the output is P7-07's.
// 2. Rank the plans in generation order with CirculationRanker (the six AlternativeRanker criteria with the request's
//    weights plus "circulation" with CirculationWeight, RequireValid true). Its refusals (ranking weights, count) refuse
//    the pipeline.
// 3. Gates, in order, for every alternative:
//    prototype    AlternativeRanker did not rank the plan: the plan's violations when it is infeasible, else the
//                 ranker's reasons (duplicate_geometry, plan_empty, rect_invalid, score_not_finite);
//    circulation  CirculationRanker rejected it: the failed hard checks of CirculationAnalyzer.Analyze (unreachable
//                 room, bedroom or dry room reachable only through a bedroom, no entrance, plan-specific refusal);
//    facade       FacadePlanner.Place is not complete: the failed rooms' reasons (illumination or ventilation ratio
//                 not met: no exterior edge, edge too short, party walls only, unsupported rule, ratio unmet);
//    facade_ir    FacadeIrBuilder.Prototype fails (IntentCompiler issues such as wall_clash or room_too_small, one reason
//                 per issue; a facade_ir_* ArgumentException with its code; any other IntentCompiler exception, such
//                 as its internal intent_stretch_unbounded invariant, as pipeline_ir_compile_failed) or
//                 FacadeIrBuilder.AddWindows (with the circulation model, so windows clear the entrance door) is not
//                 complete: its failed checks.
//    Reasons are typed (gate, code, subject, detail); the stage results computed up to the failing gate are kept.
// 4. Accepted alternatives keep the CirculationRanker order (total descending, ties by generation index) and are ranked
//    1..k with its total and seven criteria; rejected ones follow in generation order with rank 0, total 0 and no
//    criteria. Every stage is deterministic and sequential and nothing depends on culture, so the same input gives the
//    same result and the same canonical JSON (PrototypePipelineSerializer).
// 5. P7-10, only when options.FacadeWeight > 0: every accepted alternative gets an eighth criterion "facade" from its
//    FacadePlanner layout (the CABA ratio minimums of the pack as FacadePlanner resolved them, no new rule values): over
//    the Compliant rooms with a positive illumination or ventilation ratio, a room's margin is
//    min(window area / room area / illumination ratio, ventilation area / room area / ventilation ratio) - 1 over the
//    ratios it has (at least 0: how far its weakest ratio is above the minimum); raw = mean room margin (0 when no room
//    has a ratio), normalized = raw / (1 + raw), so windows exactly at the minimum score 0 and twice the minimum 0.5. The
//    detail lists each room's achieved/required ratios with the pack rule ids. Every weight is then its given weight over
//    the sum of the eight given weights (the six ranking weights, CirculationWeight, FacadeWeight; a sum that is not
//    finite is refused as weights_invalid "weights"), contributions = weight x normalized (base criteria keep their raw
//    and normalized values), total = in-order sum, at most 1, and the accepted alternatives are re-ranked by total
//    descending, ties by generation index. Rejected alternatives, the gates and the CirculationRanker set (Ranking) are
//    unchanged. With FacadeWeight 0 nothing here runs and the output is P7-07's, byte for byte.
// Gaps: the facade criterion measures only the window area margin over the ratio minimums (not orientation, sun or
// view); each accepted alternative gets its own BIM-IR with the same project identity; nothing is committed.
public static class PrototypePipeline
{
    // A one-room plan used only to check the facade and BIM-IR options before ranking.
    static readonly SpacePlan Probe = new(SpacePlanStatus.Feasible, "probe", 0, 1, new PlanFootprint(4, 4), PlannerOptions.DefaultDoorWidthMetres,
        new[] { new PlannedRoom("probe", "probe", "probe", 1, 0, new PlanRect(0, 0, 4, 4)) }, Array.Empty<PlanConstraint>(), 0);

    // Generates the prototype alternatives of the request with PrototypeGenerator and runs them through the gates.
    public static PrototypePipelineResult Run(PrototypeRequest request, PrototypePipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Pack);
        var prototype = PrototypeGenerator.Generate(request);
        var refusals = prototype.Refusals.ToList();
        refusals.AddRange(OptionRefusals(options));
        if (refusals.Count > 0 || prototype.Brief is null || prototype.Alternatives is null)
            return Refused(options, prototype, refusals);
        var plans = prototype.Alternatives.Alternatives.OrderBy(alternative => alternative.Index).Select(alternative => alternative.Plan).ToArray();
        var strategies = new Dictionary<int, string>();
        foreach (var candidate in prototype.Candidates) strategies[candidate.Index] = candidate.Strategy;
        return Evaluate(prototype.Brief, plans, request.Weights, options, prototype, strategies);
    }

    // Runs caller-supplied plans of one brief (for example AlternativeGenerator output) through the same gates.
    public static PrototypePipelineResult Rank(ProjectBrief brief, IReadOnlyList<SpacePlan> plans, PrototypePipelineOptions options, ScoringWeights? weights = null)
    {
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(plans);
        foreach (var plan in plans) ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Pack);
        var refusals = OptionRefusals(options);
        refusals.AddRange(RankingRefusals(plans.Count, weights ?? new ScoringWeights(), options.CirculationWeight));
        if (refusals.Count > 0) return Refused(options, null, refusals);
        return Evaluate(brief, plans, weights, options, null, null);
    }

    static PrototypePipelineResult Evaluate(ProjectBrief brief, IReadOnlyList<SpacePlan> plans, ScoringWeights? weights, PrototypePipelineOptions options,
        PrototypeResult? prototype, IReadOnlyDictionary<int, string>? strategies)
    {
        // P7-08 opt-in pre-pass: snapped plans replace the given ones; a refused snap becomes an infeasible plan carrying
        // its typed reasons, so the prototype gate rejects it.
        if (options.Snap is { } snap)
            plans = plans.Select(plan => PrototypeSnap.Snap(brief, plan, options.Ir, snap)).Select(snapped => snapped.Status == PrototypeSnapStatus.Refused
                ? snapped.Plan with { Status = SpacePlanStatus.Infeasible, Rooms = Array.Empty<PlannedRoom>(), Constraints = snapped.Refusals, Score = 0 }
                : snapped.Plan).ToArray();
        var circulationOptions = options.Circulation ?? new CirculationOptions();
        var ranking = CirculationRanker.Rank(brief, plans, new CirculationScoring(weights, options.CirculationWeight, true, circulationOptions));
        if (ranking.Status == AlternativeSetStatus.Refused) return Refused(options, prototype, ranking.Refusals);
        var given = Given(weights, options);
        if (options.FacadeWeight > 0 && !double.IsFinite(given.Sum()))
            return Refused(options, prototype, new[] { Refusal(AlternativeCodes.WeightsInvalid, "weights", "the sum of weights with the facade weight must be finite") });
        // Classification only (the statuses do not depend on the weights): which plans AlternativeRanker ranks on its own.
        var classified = AlternativeRanker.Rank(brief, plans, new ScoringWeights(1, 1, 1, 1, 1, 1));
        var origins = new Dictionary<int, Alternative>();
        foreach (var alternative in classified.Alternatives) origins[alternative.Index] = alternative;

        var accepted = new List<PrototypePipelineAlternative>();
        var rejected = new List<PrototypePipelineAlternative>();
        foreach (var alternative in ranking.Alternatives)
        {
            var plan = alternative.Plan;
            var strategy = strategies is not null && strategies.TryGetValue(alternative.Index, out var known) ? known : null;
            PrototypePipelineAlternative Reject(IReadOnlyList<PrototypePipelineRejection> reasons, CirculationModel? model, FacadeLayout? layout) =>
                new(alternative.Index, alternative.Seed, strategy, 0, 0, Array.Empty<CriterionScore>(), reasons, plan, model, layout, null);

            var origin = origins[alternative.Index];
            if (origin.Status != AlternativeStatus.Ranked)
            {
                var reasons = !origin.Plan.IsFeasible && origin.Plan.Violations.Count > 0
                    ? origin.Plan.Violations.Select(violation => Reason(PrototypePipelineGate.Prototype, violation)).ToArray()
                    : Parse(PrototypePipelineGate.Prototype, origin.Reasons);
                rejected.Add(Reject(reasons, null, null));
                continue;
            }
            var circulation = CirculationAnalyzer.Analyze(brief, plan, circulationOptions);
            if (alternative.Status != AlternativeStatus.Ranked)
            {
                var failed = circulation.Checks.Where(check => check.Hard && !check.Satisfied).Select(check => Reason(PrototypePipelineGate.Circulation, check)).ToArray();
                rejected.Add(Reject(failed.Length > 0 ? failed : Parse(PrototypePipelineGate.Circulation, alternative.Reasons), circulation, null));
                continue;
            }
            var facade = FacadePlanner.Place(plan, options.Pack, options.Facade, options.Parameters);
            if (!facade.IsComplete)
            {
                var failed = facade.Checks.Where(check => check.Hard && !check.Satisfied).Select(check => Reason(PrototypePipelineGate.Facade, check)).ToArray();
                rejected.Add(Reject(failed.Length > 0 ? failed
                    : new[] { new PrototypePipelineRejection(PrototypePipelineGate.Facade, PrototypePipelineCodes.FacadeIncomplete, "plan", "the facade layout is " + facade.Status.ToString().ToLowerInvariant()) },
                    circulation, facade));
                continue;
            }
            var (compiled, compileRejections) = CompilePrototype(() => FacadeIrBuilder.Prototype(options.ProjectId, options.Revision, plan, options.Ir));
            if (compiled is not { } project)
            {
                rejected.Add(Reject(compileRejections, circulation, facade));
                continue;
            }
            var ir = FacadeIrBuilder.AddWindows(project, facade, circulation, options.Ir);
            if (!ir.IsComplete)
            {
                var failed = ir.Checks.Where(check => check.Hard && !check.Satisfied).Select(check => Reason(PrototypePipelineGate.FacadeIr, check)).ToArray();
                rejected.Add(Reject(failed.Length > 0 ? failed
                    : new[] { new PrototypePipelineRejection(PrototypePipelineGate.FacadeIr, FacadeIrCodes.LayoutInvalid, "plan", "the windows were not added: " + ir.Status.ToString().ToLowerInvariant()) },
                    circulation, facade));
                continue;
            }
            accepted.Add(new PrototypePipelineAlternative(alternative.Index, alternative.Seed, strategy, accepted.Count + 1, alternative.Total, alternative.Criteria,
                Array.Empty<PrototypePipelineRejection>(), plan, circulation, facade, ir));
        }
        // P7-10 opt-in step 5: the facade criterion re-weights and re-ranks the accepted alternatives.
        if (options.FacadeWeight > 0)
        {
            var (scored, unscored) = ApplyFacade(accepted, given, options.Parameters);
            accepted = scored.ToList();
            rejected.AddRange(unscored);
        }
        var ordered = accepted.Concat(rejected.OrderBy(alternative => alternative.Index)).ToArray();
        var status = accepted.Count > 0 ? PrototypePipelineStatus.Ranked : PrototypePipelineStatus.Infeasible;
        return new PrototypePipelineResult(status, options, prototype, ranking, Array.Empty<PlanConstraint>(), ordered);
    }

    // The eight given weights in criterion order: the six ranking weights, the circulation weight and the facade weight.
    static double[] Given(ScoringWeights? weights, PrototypePipelineOptions options)
    {
        var w = weights ?? new ScoringWeights();
        return new[] { w.AreaFit, w.Adjacency, w.Proportions, w.Compactness, w.Perimeter, w.Compliance, options.CirculationWeight, options.FacadeWeight };
    }

    // Step 5 over the accepted alternatives (given = the eight given weights, the facade weight last): each one scored with
    // the facade criterion and re-weighted, then re-ranked 1..k by total descending, ties by generation index.
    internal static (IReadOnlyList<PrototypePipelineAlternative> Accepted, IReadOnlyList<PrototypePipelineAlternative> Rejected) ApplyFacade(
        IReadOnlyList<PrototypePipelineAlternative> accepted, double[] given, IReadOnlyDictionary<string, double>? parameters)
    {
        var scored = accepted.Select(alternative => WithFacade(alternative, given, given[7], parameters)).OrderByDescending(alternative => alternative.Total)
            .ThenBy(alternative => alternative.Index).Select((alternative, position) => alternative with { Rank = position + 1 }).ToArray();
        return (scored, Array.Empty<PrototypePipelineAlternative>());
    }

    // Step 5 for one accepted alternative: its seven criteria re-weighted over the eight given weights plus the facade one.
    static PrototypePipelineAlternative WithFacade(PrototypePipelineAlternative alternative, double[] given, double facadeWeight, IReadOnlyDictionary<string, double>? parameters)
    {
        var sum = given.Sum();
        var criteria = new List<CriterionScore>(alternative.Criteria.Count + 1);
        for (var k = 0; k < alternative.Criteria.Count; k++)
        {
            var weight = given[k] / sum;
            criteria.Add(alternative.Criteria[k] with { Weight = weight, Contribution = weight * alternative.Criteria[k].Normalized });
        }
        criteria.Add(FacadeCriterion(alternative.Facade!, facadeWeight / sum, facadeWeight, parameters));
        double total = 0;
        foreach (var criterion in criteria) total += criterion.Contribution;
        return alternative with { Total = Math.Min(1, total), Criteria = criteria };
    }

    // The facade criterion of a complete layout (step 5): mean margin of the rooms' windows over their ratio minimums.
    internal static CriterionScore FacadeCriterion(FacadeLayout layout, double weight, double givenWeight, IReadOnlyDictionary<string, double>? parameters = null)
    {
        var margins = new List<double>();
        var parts = new List<string>();
        foreach (var room in layout.Rooms.Where(room => room.Status == FacadeRoomStatus.Compliant))
        {
            var factors = new List<double>();
            var texts = new List<string>();
            if (room.IlluminationRatio is { } light && light > 0)
            {
                factors.Add(room.WindowAreaSquareMetres / room.AreaSquareMetres / light);
                texts.Add("illumination " + Format(room.WindowAreaSquareMetres / room.AreaSquareMetres) + " / " + Format(light));
            }
            if (room.VentilationRatio is { } air && air > 0)
            {
                factors.Add(room.VentilationAreaSquareMetres / room.AreaSquareMetres / air);
                texts.Add("ventilation " + Format(room.VentilationAreaSquareMetres / room.AreaSquareMetres) + " / " + Format(air));
            }
            if (factors.Count == 0) continue;
            var margin = Math.Max(0, factors.Min() - 1);
            margins.Add(margin);
            parts.Add(room.Instance + " " + string.Join(", ", texts) + " = +" + Format(margin * 100) + " % (" + string.Join(", ", room.RuleIds) + ")");
        }
        double raw = 0;
        foreach (var margin in margins) raw += margin;
        raw = margins.Count == 0 ? 0 : raw / margins.Count;
        var normalized = raw / (1 + raw);
        var detail = (margins.Count == 0
            ? "no room of the facade layout has an illumination or ventilation ratio minimum"
            : "mean margin " + Format(raw * 100) + " % of the windows over the " + layout.PackId + " ratio minimums (weakest ratio per room) over "
              + margins.Count.ToString(CultureInfo.InvariantCulture) + " rooms: " + string.Join("; ", parts))
            + "; normalized margin / (1 + margin); facade weight " + Format(givenWeight) + " given (effective " + Format(weight) + ")";
        return new CriterionScore(PrototypePipelineCodes.FacadeCriterion, raw, normalized, weight, weight * normalized, detail);
    }

    static string Format(double value) => double.IsFinite(value) ? SpacePlanSerializer.Format(value) : "non-finite";

    // The BIM-IR compile step of the facade_ir gate: the project, or the typed reasons it could not be compiled.
    internal static PrototypePipelineCompile CompilePrototype(Func<Project> compile)
    {
        try
        {
            return new(compile(), Array.Empty<PrototypePipelineRejection>());
        }
        catch (IntentException error)
        {
            return new(null, error.Issues.Count > 0
                ? error.Issues.Select(issue => new PrototypePipelineRejection(PrototypePipelineGate.FacadeIr, issue.Code, issue.Key, issue.Detail)).ToArray()
                : new[] { new PrototypePipelineRejection(PrototypePipelineGate.FacadeIr, error.Code, error.Field ?? "plan", error.Detail ?? error.Message) });
        }
        catch (ArgumentException error) when (IrCode(error.Message) is not null)
        {
            var code = IrCode(error.Message)!;
            var detail = error.Message.Length > code.Length + 2 ? error.Message[(code.Length + 2)..] : error.Message;
            return new(null, new[] { new PrototypePipelineRejection(PrototypePipelineGate.FacadeIr, code, "plan", detail) });
        }
        catch (InvalidOperationException error)
        {
            return new(null, new[] { new PrototypePipelineRejection(PrototypePipelineGate.FacadeIr, PrototypePipelineCodes.IrCompileFailed, "plan",
                "IntentCompiler failed: " + error.Message) });
        }
    }

    // FacadeIrBuilder.Intent reports its problems as ArgumentException("<facade_ir code>: <detail>"); any other
    // ArgumentException is a caller bug and is not swallowed.
    static string? IrCode(string message)
    {
        foreach (var code in new[] { FacadeIrCodes.LayoutInvalid, FacadeIrCodes.OptionsInvalid })
            if (message.StartsWith(code + ": ", StringComparison.Ordinal)) return code;
        return null;
    }

    static List<PlanConstraint> OptionRefusals(PrototypePipelineOptions options)
    {
        var refusals = new List<PlanConstraint>();
        if (options.ProjectId == Guid.Empty) refusals.Add(Refusal(PrototypePipelineCodes.ProjectInvalid, "project_id", "a non-empty project id is required for the BIM-IR prototypes"));
        if (options.Revision < 0) refusals.Add(Refusal(PrototypePipelineCodes.ProjectInvalid, "revision", "the revision must not be negative"));
        if (!double.IsFinite(options.CirculationWeight) || options.CirculationWeight < 0)
            refusals.Add(Refusal(AlternativeCodes.WeightsInvalid, "weights.circulation", "weight must be finite and non-negative"));
        if (!double.IsFinite(options.FacadeWeight) || options.FacadeWeight < 0)
            refusals.Add(Refusal(AlternativeCodes.WeightsInvalid, "weights.facade", "weight must be finite and non-negative"));
        refusals.AddRange(CirculationAnalyzer.OptionRefusals(options.Circulation ?? new CirculationOptions()));
        if (options.Snap is { } snap) refusals.AddRange(PrototypeSnap.OptionRefusals(snap));
        var probe = FacadePlanner.Place(Probe, options.Pack, options.Facade, options.Parameters);
        if (probe.Status == FacadeStatus.Refused) refusals.AddRange(probe.Checks.Where(check => check.Code != FacadeCodes.PlanInvalid));
        try
        {
            _ = FacadeIrBuilder.Intent(Probe, options.Ir);
        }
        catch (ArgumentException error)
        {
            refusals.Add(Refusal(FacadeIrCodes.OptionsInvalid, "ir_options", error.Message));
        }
        return refusals;
    }

    // CirculationRanker's plan-independent refusals other than the circulation weight itself (already an option refusal):
    // the alternative count, each of the six weights, and all seven weights zero or their sum not finite.
    static List<PlanConstraint> RankingRefusals(int count, ScoringWeights weights, double circulationWeight)
    {
        var refusals = AlternativeRanker.Refusals(count, weights).Where(refusal => refusal.Subject != "weights").ToList();
        var all = new[] { weights.AreaFit, weights.Adjacency, weights.Proportions, weights.Compactness, weights.Perimeter, weights.Compliance, circulationWeight };
        if (all.All(weight => double.IsFinite(weight) && weight >= 0))
        {
            var sum = all.Sum();
            if (!(sum > 0)) refusals.Add(Refusal(AlternativeCodes.WeightsInvalid, "weights", "at least one weight must be positive"));
            else if (!double.IsFinite(sum)) refusals.Add(Refusal(AlternativeCodes.WeightsInvalid, "weights", "the sum of weights must be finite"));
        }
        return refusals;
    }

    static PrototypePipelineResult Refused(PrototypePipelineOptions options, PrototypeResult? prototype, IReadOnlyList<PlanConstraint> refusals) =>
        new(PrototypePipelineStatus.Refused, options, prototype, null, refusals, Array.Empty<PrototypePipelineAlternative>());

    static PrototypePipelineRejection Reason(PrototypePipelineGate gate, PlanConstraint check) => new(gate, check.Code, check.Subject, check.Detail);

    // Splits the stages' "<code>@<subject>: <detail>" reason strings; a reason without that shape keeps its text as detail.
    static PrototypePipelineRejection[] Parse(PrototypePipelineGate gate, IReadOnlyList<string> reasons)
    {
        var parsed = reasons.Select(reason =>
        {
            var at = reason.IndexOf('@', StringComparison.Ordinal);
            var colon = at < 0 ? -1 : reason.IndexOf(": ", at, StringComparison.Ordinal);
            return at > 0 && colon > at
                ? new PrototypePipelineRejection(gate, reason[..at], reason[(at + 1)..colon], reason[(colon + 2)..])
                : new PrototypePipelineRejection(gate, "rejected", "plan", reason);
        }).ToArray();
        return parsed.Length > 0 ? parsed : new[] { new PrototypePipelineRejection(gate, "rejected", "plan", "the stage gave no reason") };
    }

    static PlanConstraint Refusal(string code, string subject, string detail) => new(code, subject, true, false, detail);
}
