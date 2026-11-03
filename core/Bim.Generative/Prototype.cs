using Bim.Core;
namespace Bim.Generative;

// Apartment prototypes v1: a typology template (1, 2 or 3 bedrooms) turned into a project brief on a given lot,
// planned into ranked layouts with SpacePlanner and AlternativeRanker under the CABA minimums of the
// ar.caba.codigo-edificacion pack, keeping the rooms and walls the user approved. PrototypeTemplates builds the brief
// and the rules, PrototypeGenerator plans and ranks, PrototypeSerializer writes the canonical form. Lengths are metres
// on level 0, with the lot's south-west corner at (0, 0), as in SpacePlan.
public enum ApartmentTypology { OneBedroom = 1, TwoBedroom = 2, ThreeBedroom = 3 }

public enum PrototypeStatus { Ranked, Infeasible, Refused }

public sealed record PrototypeLot(double WidthMetres, double DepthMetres);

// An approved room: the exact rectangle of one template room (room id) the user accepted. Every ranked layout holds
// that room with this rectangle bit for bit; a layout that would move it is rejected with fixed_room_moved. The
// rectangle must lie on the planner's 1/8192 m grid (every SpacePlan rectangle does), inside the lot, and meet the
// brief range and the CABA minimums of its room.
public sealed record FixedRoom(string RoomId, PlanRect Rect);

// An approved wall: an axis-aligned segment on the 1/8192 m grid inside the lot. Every ranked layout has room edges
// covering the whole segment; a layout that does not is rejected with fixed_wall_missing.
public sealed record FixedWall(string Id, double X0, double Y0, double X1, double Y1);

// Alternatives is the number of candidates per strategy (whole-lot replans and, when rooms are approved, as many plans
// around the approved rooms); seeds are Seed + i (32-bit wrap-around). Weights default to ScoringWeights().
public sealed record PrototypeRequest(ApartmentTypology Typology, PrototypeLot Lot, int Alternatives = 8, int Seed = 0,
    IReadOnlyList<FixedRoom>? FixedRooms = null, IReadOnlyList<FixedWall>? FixedWalls = null, ScoringWeights? Weights = null,
    double DoorWidthMetres = PlannerOptions.DefaultDoorWidthMetres);

// One planned candidate; Index is its position in the alternative set (AlternativeSet.Alternatives[k].Index). Region is
// the free rectangle an around_fixed candidate was planned in and Variant the placement chosen inside it (null for
// whole_lot candidates and when nothing was placed).
public sealed record PrototypeCandidate(int Index, string Strategy, int Seed, PlanRect? Region, string? Variant);

// Brief is null only when the typology or the lot was refused; Alternatives is null for a refused request.
public sealed record PrototypeResult(PrototypeStatus Status, PrototypeRequest Request, ProjectBrief? Brief, IReadOnlyList<PlanConstraint> Refusals,
    IReadOnlyList<PrototypeCandidate> Candidates, AlternativeSet? Alternatives)
{
    public const int CurrentVersion = 1;
}

public static class PrototypeStrategies
{
    // The full template planned on the whole lot (no knowledge of approvals; checked against them afterwards).
    public const string WholeLot = "whole_lot";
    // Approved rooms kept as given; the other rooms planned in a free rectangle of the lot next to them.
    public const string AroundFixed = "around_fixed";
}

public static class PrototypeCodes
{
    // Refusals of the request (nothing planned).
    public const string TypologyInvalid = "prototype_typology_invalid";
    public const string LotInvalid = "prototype_lot_invalid";
    public const string CountInvalid = "prototype_count_invalid";
    public const string OptionsInvalid = "prototype_options_invalid";
    public const string FixedRoomInvalid = "fixed_room_invalid";
    public const string FixedRoomUnknown = "fixed_room_unknown";
    public const string FixedRoomDuplicate = "fixed_room_duplicate";
    public const string FixedRoomOutsideLot = "fixed_room_outside_lot";
    public const string FixedRoomOverlap = "fixed_room_overlap";
    public const string FixedRoomNoncompliant = "fixed_room_noncompliant";
    public const string FixedWallInvalid = "fixed_wall_invalid";
    // Approval sets no layout can satisfy.
    public const string FixedAdjacencyForbidden = "fixed_adjacency_forbidden";
    public const string FixedAdjacencyRequired = "fixed_adjacency_required";
    public const string FixedAreaOverBudget = "fixed_area_over_budget";
    public const string FixedWallCrossesRoom = "fixed_wall_crosses_room";
    // Checks of each candidate layout (rejection reasons when unsatisfied).
    public const string FixedRoomMoved = "fixed_room_moved";
    public const string FixedWallMissing = "fixed_wall_missing";
    // No free rectangle next to the approved rooms can hold the other rooms' minimum areas.
    public const string FixedRegionMissing = "fixed_region_missing";
}
