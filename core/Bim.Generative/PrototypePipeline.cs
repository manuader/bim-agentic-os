using System.Runtime.CompilerServices;
using Bim.Core;

[assembly: InternalsVisibleTo("Bim.Generative.Tests")]
namespace Bim.Generative;

// The P7-07 prototype pipeline: an opt-in composition of the existing stages, which it calls unchanged. Steps:
// 1. Refuse the options before ranking, listing every problem: an empty project id or a negative revision
//    (pipeline_project_invalid), a negative or non-finite circulation weight (weights_invalid "weights.circulation"),
//    the plan-independent circulation options (circulation_options_invalid), the facade options, pack and parameter
//    overrides (FacadePlanner's facade_options_invalid / facade_pack_invalid / overrides, found by placing a fixed
//    one-room probe plan) and the BIM-IR options (facade_ir_options_invalid). Run also lists the PrototypeGenerator
//    refusals of the request (they come first); the generator is called exactly as on its own, so its result, canonical
//    JSON and pinned digests are unchanged.
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
//    facade_ir    FacadeIrBuilder.Prototype throws (IntentCompiler issues such as wall_clash, one reason per issue) or
//                 FacadeIrBuilder.AddWindows (with the circulation model, so windows clear the entrance door) is not
//                 complete: its failed checks.
//    Reasons are typed (gate, code, subject, detail); the stage results computed up to the failing gate are kept.
// 4. Accepted alternatives keep the CirculationRanker order (total descending, ties by generation index) and are ranked
//    1..k with its total and seven criteria; rejected ones follow in generation order with rank 0, total 0 and no
//    criteria. Every stage is deterministic and sequential and nothing depends on culture, so the same input gives the
//    same result and the same canonical JSON (PrototypePipelineSerializer).
// Gaps: the facade is a gate, not a ranking criterion (window quality does not reorder accepted alternatives); each
// accepted alternative gets its own BIM-IR with the same project identity; nothing is committed.
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
        if (refusals.Count > 0) return Refused(options, null, refusals);
        return Evaluate(brief, plans, weights, options, null, null);
    }

    static PrototypePipelineResult Evaluate(ProjectBrief brief, IReadOnlyList<SpacePlan> plans, ScoringWeights? weights, PrototypePipelineOptions options,
        PrototypeResult? prototype, IReadOnlyDictionary<int, string>? strategies)
    {
        var circulationOptions = options.Circulation ?? new CirculationOptions();
        var ranking = CirculationRanker.Rank(brief, plans, new CirculationScoring(weights, options.CirculationWeight, true, circulationOptions));
        if (ranking.Status == AlternativeSetStatus.Refused) return Refused(options, prototype, ranking.Refusals);
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
                    : new[] { new PrototypePipelineRejection(PrototypePipelineGate.Facade, FacadeCodes.RatioUnmet, "plan", "the facade layout is " + facade.Status.ToString().ToLowerInvariant()) },
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
        var ordered = accepted.Concat(rejected.OrderBy(alternative => alternative.Index)).ToArray();
        var status = accepted.Count > 0 ? PrototypePipelineStatus.Ranked : PrototypePipelineStatus.Infeasible;
        return new PrototypePipelineResult(status, options, prototype, ranking, Array.Empty<PlanConstraint>(), ordered);
    }

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
        catch (ArgumentException error)
        {
            return new(null, new[] { new PrototypePipelineRejection(PrototypePipelineGate.FacadeIr, FacadeIrCodes.LayoutInvalid, "plan", error.Message) });
        }
    }

    static List<PlanConstraint> OptionRefusals(PrototypePipelineOptions options)
    {
        var refusals = new List<PlanConstraint>();
        if (options.ProjectId == Guid.Empty) refusals.Add(Refusal(PrototypePipelineCodes.ProjectInvalid, "project_id", "a non-empty project id is required for the BIM-IR prototypes"));
        if (options.Revision < 0) refusals.Add(Refusal(PrototypePipelineCodes.ProjectInvalid, "revision", "the revision must not be negative"));
        if (!double.IsFinite(options.CirculationWeight) || options.CirculationWeight < 0)
            refusals.Add(Refusal(AlternativeCodes.WeightsInvalid, "weights.circulation", "weight must be finite and non-negative"));
        refusals.AddRange(CirculationAnalyzer.OptionRefusals(options.Circulation ?? new CirculationOptions()));
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
