namespace Bim.Generative;

// Facade openings v1 (P7-05): windows for the habitable rooms of a feasible space plan, sized from the illumination and
// ventilation ratio rules of a code skill pack (ar.caba.codigo-edificacion: window_area_m2 / area_m2 >= 1/x with x = 10,
// ventilation_area_m2 / area_m2 >= 1/30, that is k = i/3). FacadePlanner places them, FacadeSerializer writes the
// canonical form. Lengths are metres in the plan's frame (footprint south-west corner at (0, 0)), heights metres above
// the room's level floor. Nothing here is persisted BIM-IR.
public enum FacadeStatus { Complete, Incomplete, Refused }

// The side of a room rectangle a window sits on.
public enum FacadeSide { South, East, North, West }

// Not required: no illumination or ventilation ratio rule of the pack targets the room name (wet rooms, kitchens, halls
// in the CABA pack). Compliant: the room's windows meet every matched ratio. Failed: the ratio cannot be met (Reason).
public enum FacadeRoomStatus { Compliant, NotRequired, Failed }

// A window is a segment of WidthMetres on one side of its room, on a part of that side that lies on the building outline
// (the boundary of the union of the level's rooms: shared walls between rooms of one level are never used), at least
// EndClearanceMetres away from either end of that exterior part. X0 <= X1 and Y0 <= Y1; one pair is equal. AreaSquareMetres
// is WidthMetres x (HeadHeightMetres - SillHeightMetres) (the illumination area), VentilationAreaSquareMetres that area x
// the openable fraction. RuleIds are the qualified pack rule ids the window is sized for (illumination rules, then
// ventilation rules, each in ordinal order). Id is "window:<instance>/<n>", n 1-based in placement order. FacesInternalVoid
// is true when the window opens on an internal void of the level (a courtyard or hole enclosed by the level's rooms, not
// connected to the outside around the building) and false on the outer boundary.
public sealed record FacadeWindow(string Id, string Instance, int Level, FacadeSide Side, double X0, double Y0, double X1, double Y1, double WidthMetres,
    double SillHeightMetres, double HeadHeightMetres, double AreaSquareMetres, double VentilationAreaSquareMetres, IReadOnlyList<string> RuleIds,
    bool FacesInternalVoid = false);

// One room of the plan. Required ratios are the largest threshold among the applicable matched rules (null when no rule
// of that kind matches, or when a matched rule of that kind is unsupported); required areas are those ratios x the room
// area (null with the ratio). Achieved areas sum the room's windows. ExteriorLength is the total length of the room's
// sides on the building outline (lot-line sides included); Reason is null when Compliant or NotRequired.
public sealed record FacadeRoom(string Instance, string Name, int Level, FacadeRoomStatus Status, string? Reason, double AreaSquareMetres,
    double? IlluminationRatio, double? VentilationRatio, double? RequiredWindowAreaSquareMetres, double? RequiredVentilationAreaSquareMetres,
    double WindowAreaSquareMetres, double VentilationAreaSquareMetres, double ExteriorLengthMetres, IReadOnlyList<string> RuleIds, string Detail);

// Status is Complete when every room is Compliant or NotRequired, Incomplete when some room Failed, Refused when the plan,
// the options or the pack were refused (Checks then hold only the refusals, Rooms and Windows are empty). Rooms are in
// ordinal instance id order and Windows in room order then placement order, so the layout does not depend on the order
// of the plan's rooms. Checks per room: facade_illumination and facade_ventilation (hard, satisfied) for a compliant
// room with that kind of rule, facade_not_required (soft, satisfied), or the failure reason (hard, not satisfied).
public sealed record FacadeLayout(FacadeStatus Status, string BriefName, int Seed, string PackId, FacadeOptions Options, IReadOnlyList<FacadeRoom> Rooms,
    IReadOnlyList<FacadeWindow> Windows, IReadOnlyList<PlanConstraint> Checks)
{
    public const int CurrentVersion = 1;
    public bool IsComplete => Status == FacadeStatus.Complete;
    public IReadOnlyList<PlanConstraint> Violations => Checks.Where(check => !check.Satisfied).ToArray();
}

// Defaults (documented in docs/contracts/module-contracts.md, section P7-05): sill 0.90 m and head 2.10 m above the floor
// (a 1.20 m tall window), 0.30 m end clearance at each end of an exterior wall part (corner or partition junction, wall
// thickness and frame), openable fraction 0.5 of the window area for ventilation (a two-leaf sliding window) and a
// minimum window width of 0.60 m. Widths are whole millimetres, the smallest that meets the ratios. OpeningEdges lists
// the footprint (lot) edges whose lines may take openings: a room side part lying on a footprint edge line that is not
// listed is a party wall (medianera) and gets no window. Null (the default) allows all four; an empty list allows none.
// Room sides inside the footprint (setbacks, courtyards) are never lot lines. The window height (head - sill) must be at
// least MinWindowHeightMetres and the openable fraction at least MinOpenableFraction, so the required width stays bounded.
public sealed record FacadeOptions(double SillHeightMetres = FacadeOptions.DefaultSillHeightMetres, double HeadHeightMetres = FacadeOptions.DefaultHeadHeightMetres,
    double EndClearanceMetres = FacadeOptions.DefaultEndClearanceMetres, double OpenableFraction = FacadeOptions.DefaultOpenableFraction,
    double MinWindowWidthMetres = FacadeOptions.DefaultMinWindowWidthMetres, IReadOnlyList<LotEdge>? OpeningEdges = null)
{
    public static IReadOnlyList<LotEdge> DefaultOpeningEdges { get; } = new[] { LotEdge.South, LotEdge.East, LotEdge.North, LotEdge.West };
    public const double MinWindowHeightMetres = 0.1;
    public const double MinOpenableFraction = 0.01;
    public const double DefaultSillHeightMetres = 0.9;
    public const double DefaultHeadHeightMetres = 2.1;
    public const double DefaultEndClearanceMetres = 0.3;
    public const double DefaultOpenableFraction = 0.5;
    public const double DefaultMinWindowWidthMetres = 0.6;
    // Upper bound of the head height (and so of the sill), to refuse nonsense.
    public const double MaxHeadHeightMetres = 10;
}

public static class FacadeCodes
{
    // Refusals.
    public const string PlanInvalid = "facade_plan_invalid";
    public const string OptionsInvalid = "facade_options_invalid";
    public const string PackInvalid = "facade_pack_invalid";
    // Satisfied checks.
    public const string Illumination = "facade_illumination";
    public const string Ventilation = "facade_ventilation";
    public const string NotRequired = "facade_not_required";
    // Failure reasons of a room.
    // No side of the room lies on the building outline (an interior room).
    public const string NoExteriorEdge = "facade_no_exterior_edge";
    // The exterior parts, less the end clearances, cannot hold the required window width.
    public const string EdgeTooShort = "facade_edge_too_short";
    // A matched ratio rule cannot be applied (an applies_if condition on another property than area_m2 or min_side_m or
    // one the room exceeds, a non-numeric threshold, another op or denominator).
    public const string RuleUnsupported = "facade_rule_unsupported";
    // Every exterior part of the room lies on a footprint edge that FacadeOptions.OpeningEdges does not allow (party walls).
    public const string LotLineOnly = "facade_lot_line_only";
    // The placed windows do not meet a ratio (a safeguard: the sizing is meant to make this unreachable).
    public const string RatioUnmet = "facade_ratio_unmet";
}
