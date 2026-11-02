using Bim.Core;
namespace Bim.Generative;

// Stub: compiles against the contract but plans nothing yet.
public static class SpacePlanner
{
    public static SpacePlan Plan(ProjectBrief brief, int seed, PlannerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(brief);
        options ??= new PlannerOptions();
        return new SpacePlan(SpacePlanStatus.Infeasible, brief.Name, seed, 1, null, options.DoorWidthMetres, Array.Empty<PlannedRoom>(), Array.Empty<PlanConstraint>(), 0);
    }
}
