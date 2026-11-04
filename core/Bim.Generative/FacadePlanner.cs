using Bim.Core;
namespace Bim.Generative;

public static class FacadePlanner
{
    public const string IlluminationProperty = "window_area_m2";
    public const string VentilationProperty = "ventilation_area_m2";
    public const string AreaProperty = "area_m2";

    public static FacadeLayout Place(SpacePlan plan, SkillManifest pack, FacadeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(pack);
        options ??= new FacadeOptions();
        return new FacadeLayout(FacadeStatus.Refused, plan.BriefName, plan.Seed, pack.Id, options, Array.Empty<FacadeRoom>(), Array.Empty<FacadeWindow>(), Array.Empty<PlanConstraint>());
    }

    public static IReadOnlyList<RoomFacts> ToRoomFacts(FacadeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Array.Empty<RoomFacts>();
    }
}
