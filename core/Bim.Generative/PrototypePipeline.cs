using Bim.Core;
namespace Bim.Generative;

// Stub (TDD red): the pipeline is not implemented yet.
public static class PrototypePipeline
{
    public static PrototypePipelineResult Run(PrototypeRequest request, PrototypePipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        return new PrototypePipelineResult(PrototypePipelineStatus.Refused, options, null, null, Array.Empty<PlanConstraint>(), Array.Empty<PrototypePipelineAlternative>());
    }

    public static PrototypePipelineResult Rank(ProjectBrief brief, IReadOnlyList<SpacePlan> plans, PrototypePipelineOptions options, ScoringWeights? weights = null)
    {
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(options);
        _ = weights;
        return new PrototypePipelineResult(PrototypePipelineStatus.Refused, options, null, null, Array.Empty<PlanConstraint>(), Array.Empty<PrototypePipelineAlternative>());
    }
}
