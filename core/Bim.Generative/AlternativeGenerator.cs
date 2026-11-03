using Bim.Core;
namespace Bim.Generative;

// Generates Count alternatives of a brief: refuses a count outside MinCount..MaxCount or invalid weights before any
// planning, then plans seed BaseSeed + i for i = 0..Count-1 (32-bit wrap-around, so the seeds are distinct) with
// SpacePlanner and hands the plans to AlternativeRanker. The planner and the ranker are deterministic and the work is
// sequential, so the same brief and options always give the same set. An infeasible brief yields an infeasible set
// whose alternatives carry the planner's reasons; nothing is thrown for a valid request.
public static class AlternativeGenerator
{
    public const int MinCount = 1;
    public const int MaxCount = 64;

    public static AlternativeSet Generate(ProjectBrief brief, AlternativeOptions options)
    {
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(options);
        var weights = options.Weights ?? new ScoringWeights();
        var refusals = AlternativeRanker.Refusals(options.Count, weights);
        if (refusals.Count > 0)
            return new AlternativeSet(AlternativeSetStatus.Refused, AlternativeRanker.BriefName(brief), options.Count, weights, refusals, Array.Empty<Alternative>());
        var plans = new SpacePlan[options.Count];
        for (var index = 0; index < plans.Length; index++) plans[index] = SpacePlanner.Plan(brief, unchecked(options.BaseSeed + index), options.Planner);
        return AlternativeRanker.Rank(brief, plans, weights);
    }
}
