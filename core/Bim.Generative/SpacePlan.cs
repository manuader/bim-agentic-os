namespace Bim.Generative;

// Space plan v1: rectangular rooms laid out from a validated project brief (Bim.Core.ProjectBrief). Lengths are
// metres, areas square metres, levels 0-based. Every room rectangle is axis-aligned with its origin at the south-west
// corner of the footprint, which itself sits at (0, 0). A plan is either feasible (every hard constraint satisfied)
// or infeasible (no rooms, only the violated constraints). Rectangle coordinates lie on a 1/8192 m grid, so they are
// exact doubles: a room's Right (X + Width) is bit-identical to the X of the neighbour it shares that edge with. SpacePlanSerializer writes the canonical form.
public enum SpacePlanStatus { Feasible, Infeasible }

public sealed record PlanFootprint(double WidthMetres, double DepthMetres);

public sealed record PlanRect(double X, double Y, double Width, double Depth)
{
    public double Right => X + Width;
    public double Top => Y + Depth;
    public double Area => Width * Depth;
}

// One planned instance of a brief room. InstanceId is the room id for a single room and "<id>#<n>" (n 1-based) when
// the brief asks for several; '#' is outside the room id alphabet, so instance ids never collide with room ids.
public sealed record PlannedRoom(string InstanceId, string RoomId, string Name, int Instance, int Level, PlanRect Rect)
{
    public double AreaSquareMetres => Rect.Area;
}

// Hard constraints decide feasibility; soft ones (preferred adjacency) only feed the score. Subject is an instance id,
// a room pair "a/b" as stated in the brief, "level <n>", "plan", or the input path of a refused value.
public sealed record PlanConstraint(string Code, string Subject, bool Hard, bool Satisfied, string Detail);

public sealed record SpacePlan(SpacePlanStatus Status, string BriefName, int Seed, int Levels, PlanFootprint? Footprint, double DoorWidthMetres,
    IReadOnlyList<PlannedRoom> Rooms, IReadOnlyList<PlanConstraint> Constraints, double Score)
{
    public const int CurrentVersion = 1;
    public bool IsFeasible => Status == SpacePlanStatus.Feasible;
    public IReadOnlyList<PlanConstraint> Violations => Constraints.Where(constraint => !constraint.Satisfied).ToArray();
    public IReadOnlyList<PlanConstraint> HardViolations => Constraints.Where(constraint => constraint.Hard && !constraint.Satisfied).ToArray();
}

// A minimum-dimension rule for the rooms it selects: RoomId matches a room id exactly, NamePattern matches the room
// name case-insensitively with '*' (any run) and '?' (one character); when both are given both must match. A rule
// raises the room's minimum area and/or minimum side; the brief's own minimum still applies. Reference is free text
// naming where the rule comes from (a rule pack, a regulation); the planner does not interpret it.
public sealed record PlannerRule(string? RoomId = null, string? NamePattern = null, double? MinAreaSquareMetres = null, double? MinSideMetres = null,
    string? Reference = null);

public sealed record PlannerRules(IReadOnlyList<PlannerRule> Rules)
{
    public static PlannerRules Empty { get; } = new(Array.Empty<PlannerRule>());
}

// Footprint is the buildable rectangle when the brief has no site; with a site it must fit inside the site.
public sealed record PlannerOptions(double DoorWidthMetres = 0.9, PlanFootprint? Footprint = null, PlannerRules? Rules = null)
{
    public const double DefaultDoorWidthMetres = 0.9;
}

public static class PlanCodes
{
    // Refusals of the input (no layout attempted).
    public const string BriefInvalid = "brief_invalid";
    public const string OptionsInvalid = "options_invalid";
    public const string RuleInvalid = "rule_invalid";
    public const string FootprintMissing = "footprint_missing";
    public const string FootprintInvalid = "footprint_invalid";
    public const string AreaRangeConflict = "area_range_conflict";
    public const string LevelAreaInsufficient = "level_area_insufficient";
    public const string BudgetInsufficient = "budget_insufficient";
    // More levels, room instances or footprint size than SpacePlanner.MaxLevels / MaxInstances / MaxFootprintMetres.
    public const string PlanTooLarge = "plan_too_large";
    // The seeded search ended without a layout meeting every hard constraint (not a proof that none exists); the
    // violated constraints of its best layout follow.
    public const string SearchExhausted = "search_exhausted";
    // Constraints checked on the layout.
    public const string AreaMin = "area_min";
    public const string AreaMax = "area_max";
    public const string SideMin = "side_min";
    public const string InSite = "in_site";
    public const string NoOverlap = "no_overlap";
    public const string AreaBudget = "area_budget";
    public const string AdjacencyRequired = "adjacency_required";
    public const string AdjacencyForbidden = "adjacency_forbidden";
    public const string AdjacencyPreferred = "adjacency_preferred";
}
