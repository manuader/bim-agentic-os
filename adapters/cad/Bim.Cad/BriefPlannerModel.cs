using Bim.Core;
namespace Bim.Cad;

// Reasons a brief draft room (BriefExtract, P6-05) is left out of the planner brief (BriefPlanner, P6-08) unless the
// caller confirms it. All is in ordinal order.
public static class BriefPlannerCodes
{
    // The extraction noted that the stated area differs from the room's closed region area (draft note area-mismatch).
    public const string AreaMismatch = "area-mismatch";
    // The label repeats an earlier label of the draft (draft note duplicate-label on a room that is not the first of its
    // label). The first room of a repeated label is never a duplicate.
    public const string DuplicateLabel = "duplicate-label";
    // The room's draft confidence is below BriefPlannerOptions.MinConfidence.
    public const string LowConfidence = "low-confidence";

    public static IReadOnlyList<string> All { get; } = new[] { AreaMismatch, DuplicateLabel, LowConfidence };
}

// Options. A room is kept when every reason it has is confirmed: by its id in ConfirmedRooms (all its reasons) or by
// the reason code in ConfirmedReasons (that reason for every room). Ids and codes compare ordinally (case-sensitive);
// null collections mean none. MinConfidence must be within 0..1; a confidence equal to it is kept. Name and
// Constraints replace the draft brief's name and constraints when given (the planner needs a site, or a
// PlannerOptions.Footprint).
public sealed record BriefPlannerOptions
{
    public const double DefaultMinConfidence = 0.5;
    public double MinConfidence { get; init; } = DefaultMinConfidence;
    public IReadOnlyCollection<string> ConfirmedRooms { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> ConfirmedReasons { get; init; } = Array.Empty<string>();
    public string? Name { get; init; }
    public BriefConstraints? Constraints { get; init; }
}

// The decision on one draft room. Reasons are every BriefPlannerCodes reason the room has (ordinal order); Confirmed
// the ones the caller confirmed and Unconfirmed the rest. The room is kept exactly when Unconfirmed is empty.
public sealed record BriefPlannerRoom(string Id, string Name, double Confidence, IReadOnlyList<string> Reasons, IReadOnlyList<string> Confirmed, IReadOnlyList<string> Unconfirmed)
{
    public bool IsKept => Unconfirmed.Count == 0;
}

// The planner brief. Brief holds the draft's room records of the kept rooms in draft order (areas and provenance
// unchanged), the draft's adjacencies between kept rooms, and the draft or option name and constraints. Kept and Dropped
// list the decisions in draft order. UnknownConfirmedRooms/UnknownConfirmedReasons are the confirmations that name no
// draft room or no reason code (distinct, ordinal order). Issues is BriefValidator.Validate(Brief); IsValid is true
// when it holds no error, so the brief can go to the P7-01 SpacePlanner.
public sealed record BriefPlannerResult(ProjectBrief Brief, IReadOnlyList<BriefPlannerRoom> Kept, IReadOnlyList<BriefPlannerRoom> Dropped,
    IReadOnlyList<string> UnknownConfirmedRooms, IReadOnlyList<string> UnknownConfirmedReasons, IReadOnlyList<BriefIssue> Issues)
{
    public bool IsValid => !BriefValidator.HasErrors(Issues);
}
