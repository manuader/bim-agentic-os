namespace Bim.Generative;

// Circulation model v1 of a space plan: candidate doors on every qualifying shared wall and one entrance door on the
// lot edge form a reachability graph, so "reachable" means connected through the room adjacency graph (walls of at
// least the door width), not that a door was designed there. Shortest walking routes from the entrance to every
// level-0 room are checked (every room reachable; bedrooms and dry rooms reachable without passing through a bedroom,
// only wet rooms may sit behind one) and their length is scored. CirculationAnalyzer builds
// the model, CirculationRanker adds it as a seventh, opt-in ranking criterion ("circulation") on top of
// AlternativeRanker without changing that ranker or its output, CirculationSerializer writes the canonical form.
// Lengths are metres in the plan's frame (lot south-west corner at (0, 0)); nothing here is persisted BIM-IR.
public enum CirculationStatus { Valid, Invalid, Refused }

public enum LotEdge { South, East, North, West }

// A door is a segment of DoorWidthMetres on a wall: centred on the common edge of two rooms of one level whose shared
// length is at least the door width, or centred on the side of the entrance room that lies on the lot edge. Doors
// between rooms are candidates (one per qualifying wall), not designed doors. For them From and To are instance ids in
// ordinal order (From < To); the entrance door goes from CirculationModel.Exterior to the entrance room, whatever the
// ordinal order of the two. Used is true when at least one reported route passes through the door.
public sealed record CirculationDoor(string Id, string From, string To, int Level, double X0, double Y0, double X1, double Y1, bool Used)
{
    public double MidX => (X0 + X1) / 2;
    public double MidY => (Y0 + Y1) / 2;
}

// The route to one room instance. Rooms lists the rooms entered from the entrance room to this room (both included);
// Doors the door ids crossed in order (the entrance door first). LengthMetres walks straight lines inside each
// (convex, rectangular) room: entrance door midpoint, each crossed door midpoint, then the room centre. The route
// avoids passing through bedrooms whenever such a route exists (AvoidsBedrooms true); otherwise it is the shortest
// route at all. Equal lengths are broken by the order of the doors (the route whose last door comes first in door
// order wins). An unreachable room (including every room off level 0) has Reachable false, no rooms, no doors and a
// non-finite length.
public sealed record CirculationRoute(string Instance, bool IsBedroom, bool Reachable, bool AvoidsBedrooms, double LengthMetres,
    IReadOnlyList<string> Rooms, IReadOnlyList<string> Doors);

// Hard checks: circulation_entrance (plan), and for every level-0 room circulation_reachable plus either
// circulation_bedroom_access (a bedroom) or circulation_passage (a dry room, neither bedroom nor wet room); both ask for
// a route that does not pass through a bedroom. Soft outcome: circulation_unsupported (Hard false, Satisfied false) for
// every room off level 0, which the model cannot reach without stairs; it never makes the plan invalid. Status is
// Valid when every hard check is satisfied, Invalid otherwise, and Refused when the plan or the options were refused
// (Checks then hold only the refusals). Routes are ordered by instance id (ordinal); doors by level, then From, then To
// (ordinal); the model does not depend on the order of the plan's rooms.
public sealed record CirculationModel(CirculationStatus Status, string BriefName, int Seed, double DoorWidthMetres, CirculationDoor? Entrance, LotEdge? EntranceEdge,
    IReadOnlyList<CirculationDoor> Doors, IReadOnlyList<CirculationRoute> Routes, IReadOnlyList<PlanConstraint> Checks)
{
    public const int CurrentVersion = 1;
    // Pseudo-room on the outside of the entrance door; '@' is outside the room id alphabet.
    public const string Exterior = "@exterior";
    public const string EntranceDoorId = "entrance";
    public bool IsValid => Status == CirculationStatus.Valid;
    public IReadOnlyList<PlanConstraint> Violations => Checks.Where(check => !check.Satisfied).ToArray();
}

// EntranceEdges are the street edges the entrance may open on (the brief has no street, so the default allows all
// four, tried south, east, north, west). EntranceRoomId (a room id or instance id) forces the entrance room; otherwise
// the candidates are the level-0 non-bedroom rooms whose id or name matches an EntrancePatterns glob, then the level-0
// rooms that are neither bedrooms nor wet rooms, each group largest first (ties by instance id); the first candidate
// with a side of at least the door width on an allowed edge gets the entrance. A room is a bedroom when its id or name
// matches a BedroomPatterns glob and a wet room (allowed behind a bedroom, never a fallback entrance) when it matches a
// PassThroughPatterns glob ('*' any run, '?' one character, case-insensitive). DoorWidthMetres defaults to the plan's.
// Lists left null take the defaults below.
public sealed record CirculationOptions(string? EntranceRoomId = null, IReadOnlyList<LotEdge>? EntranceEdges = null, IReadOnlyList<string>? BedroomPatterns = null,
    IReadOnlyList<string>? EntrancePatterns = null, double? DoorWidthMetres = null, IReadOnlyList<string>? PassThroughPatterns = null)
{
    public static IReadOnlyList<string> DefaultPassThroughPatterns { get; } = new[]
        { "*bath*", "*baño*", "*bano*", "*toilet*", "wc*", "*ensuite*", "*en-suite*", "*vestidor*", "*closet*", "*dressing*" };
    public static IReadOnlyList<LotEdge> DefaultEntranceEdges { get; } = new[] { LotEdge.South, LotEdge.East, LotEdge.North, LotEdge.West };
    public static IReadOnlyList<string> DefaultBedroomPatterns { get; } = new[] { "*bedroom*", "*dormitorio*" };
    public static IReadOnlyList<string> DefaultEntrancePatterns { get; } = new[] { "hall*", "*entrance*", "*entry*", "*foyer*", "*lobby*", "*vestibul*", "*palier*" };
}

// Ranking with circulation: the six AlternativeRanker criteria with Weights plus "circulation" with CirculationWeight
// (finite, >= 0; every effective weight is the given weight over the sum of all seven). RequireValid (default) rejects a
// plan whose circulation is not valid as infeasible with the failed hard checks as reasons. The circulation criterion
// detail records CirculationWeight and RequireValid, so sets ranked with different settings serialize differently.
public sealed record CirculationScoring(ScoringWeights? Weights = null, double CirculationWeight = CirculationScoring.DefaultCirculationWeight, bool RequireValid = true,
    CirculationOptions? Options = null)
{
    public const double DefaultCirculationWeight = 0.15;
}

public static class CirculationCodes
{
    // Refusals.
    public const string PlanInvalid = "circulation_plan_invalid";
    public const string OptionsInvalid = "circulation_options_invalid";
    // Checks.
    public const string Entrance = "circulation_entrance";
    public const string Reachable = "circulation_reachable";
    public const string BedroomAccess = "circulation_bedroom_access";
    public const string Passage = "circulation_passage";
    // Soft outcome (not a plan defect): a room off level 0, which this model cannot reach (no stairs are modelled).
    public const string Unsupported = "circulation_unsupported";
    // Ranking criterion name.
    public const string Criterion = "circulation";
}
