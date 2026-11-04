using Bim.Core;
namespace Bim.Generative;

// Stub (P7-06 red step): signatures only.
public static class FacadeIrBuilder
{
    public static string RoomKey(string instance) => instance;

    public static Guid LevelId(Guid projectId, int level) => Guid.Empty;

    public static Guid SpaceId(Guid projectId, string instance) => Guid.Empty;

    public static LayoutIntent Intent(SpacePlan plan, FacadeIrOptions? options = null) =>
        new(LayoutIntent.CurrentVersion, FacadeIrOptions.DefaultProvenance, Array.Empty<IntentLevel>(), Array.Empty<IntentWallType>(), Array.Empty<IntentRoom>(), Array.Empty<IntentOpening>());

    public static Project Prototype(Guid projectId, int revision, SpacePlan plan, FacadeIrOptions? options = null) => new(projectId, revision, Array.Empty<Element>());

    public static FacadeIrResult AddWindows(Project prototype, FacadeLayout layout, CirculationModel? circulation = null, FacadeIrOptions? options = null) =>
        new(FacadeIrStatus.Refused, null, Array.Empty<FacadeIrWindow>(), Array.Empty<PlanConstraint>());

    public static IReadOnlyList<RoomFacts> ToRoomFacts(Project project, SpacePlan plan, double openableFraction) => Array.Empty<RoomFacts>();
}
