namespace Bim.Generative;

// Circulation model v1 of a space plan: doors between adjacent rooms and one entrance door on the lot edge form a
// reachability graph; shortest walking routes from the entrance to every room are checked (every room reachable,
// bedrooms reachable without passing through another bedroom) and their length is scored. CirculationAnalyzer builds
// the model, CirculationRanker adds it as a seventh, opt-in ranking criterion ("circulation") on top of
// AlternativeRanker without changing that ranker or its output, CirculationSerializer writes the canonical form.
// Lengths are metres in the plan's frame (lot south-west corner at (0, 0)); nothing here is persisted BIM-IR.
public enum CirculationStatus { Valid, Invalid, Refused }

public enum LotEdge { South, East, North, West }

// A door is a segment of DoorWidthMetres on a wall: centred on the common edge of two rooms of one level whose shared
// length is at least the door width, or centred on the side of the entrance room that lies on the lot edge. From and
// To are instance ids (ordinal order: From < To); the entrance door goes From CirculationModel.Exterior. Used is true
// when at least one reported route passes through the door.
public sealed record CirculationDoor(string Id, string From, string To, int Level, double X0, double Y0, double X1, double Y1, bool Used)
{
    public double MidX => (X0 + X1) / 2;
    public double MidY => (Y0 + Y1) / 2;
}

// The route to one room instance. Rooms lists the rooms entered from the entrance room to this room (both included);
// Doors the door ids crossed in order (the entrance door first). LengthMetres walks straight lines inside each
// (convex, rectangular) room: entrance door midpoint, each crossed door midpoint, then the room centre. The route
// avoids passing through bedrooms whenever such a route exists (AvoidsBedrooms true); otherwise it is the shortest
// route at all. An unreachable room has Reachable false, no rooms, no doors and a non-finite length.
public sealed record CirculationRoute(string Instance, bool IsBedroom, bool Reachable, bool AvoidsBedrooms, double LengthMetres,
    IReadOnlyList<string> Rooms, IReadOnlyList<string> Doors);

// Checks are hard requirements of the circulation: circulation_entrance (plan), circulation_reachable (every room) and
// circulation_bedroom_access (every bedroom). Status is Valid when every check is satisfied, Invalid otherwise and
// Refused when the plan or the options were refused (Checks then hold only the refusals). Routes are ordered by
// instance id (ordinal), doors by id; the model does not depend on the order of the plan's rooms.
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

// EntranceRoomId (a room id or instance id) forces the entrance room; otherwise the entrance is the largest room on the
// entrance level whose id or name matches an EntrancePatterns entry, else the largest non-bedroom room, with a side of
// at least the door width on one of EntranceEdges (tried in the given order; ties by instance id). A room is a bedroom
// when its id or name matches a BedroomPatterns entry ('*' any run, '?' one character, case-insensitive).
// DoorWidthMetres defaults to the plan's door width. Lists left null take the defaults below.
public sealed record CirculationOptions(string? EntranceRoomId = null, IReadOnlyList<LotEdge>? EntranceEdges = null, IReadOnlyList<string>? BedroomPatterns = null,
    IReadOnlyList<string>? EntrancePatterns = null, double? DoorWidthMetres = null)
{
    public static IReadOnlyList<LotEdge> DefaultEntranceEdges { get; } = new[] { LotEdge.South, LotEdge.East, LotEdge.North, LotEdge.West };
    public static IReadOnlyList<string> DefaultBedroomPatterns { get; } = new[] { "*bedroom*", "*dormitorio*" };
    public static IReadOnlyList<string> DefaultEntrancePatterns { get; } = new[] { "hall*", "*entrance*", "*entry*", "*foyer*", "*lobby*", "*vestibul*", "*palier*" };
}

// Ranking with circulation: the six AlternativeRanker criteria with Weights plus "circulation" with CirculationWeight
// (finite, >= 0; every effective weight is the given weight over the sum of all seven). RequireValid (default) rejects a
// plan whose circulation is not valid as infeasible with the failed checks as reasons.
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
    // Ranking criterion name.
    public const string Criterion = "circulation";
}
