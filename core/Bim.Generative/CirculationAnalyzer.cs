using System.Globalization;
using Bim.Core;
namespace Bim.Generative;

// Builds the circulation model of a feasible space plan. Steps:
// 1. Refuse the options (door width not positive and finite, an empty or undefined entrance edge list, a blank pattern,
//    an entrance room that is neither a room id nor an instance id of the plan) and the plan (infeasible, without
//    rooms, a room rectangle not positive and finite, an instance id given twice, no lot) as a Refused model.
// 2. Lot: the brief site when it is positive and finite, else the plan footprint; its south-west corner is (0, 0).
// 3. Doors: for every pair of rooms on one level whose common edge is at least the door width long (1e-7 m tolerance,
//    as the planner), one door of the door width centred on that common edge. Doors are ordered by level, then the
//    instance ids of their rooms (ordinal), and named "door:<from>/<to>".
// 4. Entrance (level 0 only, the lot edge being at ground level): candidates are the EntranceRoomId room(s) when given;
//    otherwise the non-bedroom rooms matching EntrancePatterns, then the other non-bedroom rooms, each group by area
//    descending and instance id; the first candidate with a side of at least the door width on one of EntranceEdges
//    (in the given order) gets the entrance door centred on that side. No candidate fails circulation_entrance.
// 5. Routes: a shortest-path search over door midpoints (states: a door crossed into a room; from a state every other
//    door of that room is reachable in a straight line, rooms being convex rectangles), ties broken by state order, so
//    the result is deterministic. It runs twice: through any room, and never passing through a bedroom (a bedroom may
//    only end a route). Each room takes its bedroom-free route when one exists, else its shortest route at all; the
//    length ends at the room centre. Rooms off level 0 are unreachable: no stairs are modelled.
// 6. Checks: circulation_entrance (plan), circulation_reachable for every room and circulation_bedroom_access for every
//    bedroom (a bedroom-free route exists). Valid when all hold.
// Rooms are processed in instance id order, so the model does not depend on the order of the plan's rooms.
public static class CirculationAnalyzer
{
    const double Epsilon = 1e-7;
    const int EntranceLevel = 0;

    public static CirculationModel Analyze(ProjectBrief brief, SpacePlan plan, CirculationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(plan);
        options ??= new CirculationOptions();
        var name = AlternativeRanker.BriefName(brief);
        var door = options.DoorWidthMetres ?? plan.DoorWidthMetres;
        var refusals = OptionRefusals(options);
        if (options.DoorWidthMetres is null && !(double.IsFinite(door) && door > 0))
            refusals.Add(Refusal(CirculationCodes.OptionsInvalid, "door_width_m", "the plan's door width " + Format(door) + " m is not positive and finite"));
        if (!plan.IsFeasible || plan.Rooms.Count == 0)
            refusals.Add(Refusal(CirculationCodes.PlanInvalid, "plan", plan.IsFeasible ? "a feasible plan without rooms has no circulation" : "an infeasible plan has no circulation"));
        else
        {
            foreach (var room in plan.Rooms)
            {
                ArgumentNullException.ThrowIfNull(room);
                var rect = room.Rect;
                if (rect is null || !(double.IsFinite(rect.X) && double.IsFinite(rect.Y) && double.IsFinite(rect.Width) && rect.Width > 0 && double.IsFinite(rect.Depth) && rect.Depth > 0))
                    refusals.Add(Refusal(CirculationCodes.PlanInvalid, room.InstanceId ?? "", "room rectangle must be finite with positive width and depth"));
            }
            foreach (var duplicate in plan.Rooms.GroupBy(room => room.InstanceId ?? "", StringComparer.Ordinal).Where(group => group.Count() > 1).OrderBy(group => group.Key, StringComparer.Ordinal))
                refusals.Add(Refusal(CirculationCodes.PlanInvalid, duplicate.Key, "instance id given " + Text(duplicate.Count()) + " times"));
            if (options.EntranceRoomId is { } wanted && !string.IsNullOrWhiteSpace(wanted)
                && !plan.Rooms.Any(room => string.Equals(room.RoomId, wanted, StringComparison.Ordinal) || string.Equals(room.InstanceId, wanted, StringComparison.Ordinal)))
                refusals.Add(Refusal(CirculationCodes.OptionsInvalid, "entrance_room", "'" + wanted + "' is neither a room id nor an instance id of the plan"));
        }
        var (lotWidth, lotDepth) = Lot(brief, plan);
        if (!(double.IsFinite(lotWidth) && lotWidth > 0 && double.IsFinite(lotDepth) && lotDepth > 0))
            refusals.Add(Refusal(CirculationCodes.PlanInvalid, "lot", "neither a positive brief site nor a plan footprint gives the lot"));
        if (refusals.Count > 0)
            return new CirculationModel(CirculationStatus.Refused, name, plan.Seed, door, null, null, Array.Empty<CirculationDoor>(), Array.Empty<CirculationRoute>(), refusals);

        var bedroomPatterns = options.BedroomPatterns ?? CirculationOptions.DefaultBedroomPatterns;
        var entrancePatterns = options.EntrancePatterns ?? CirculationOptions.DefaultEntrancePatterns;
        var edges = options.EntranceEdges ?? CirculationOptions.DefaultEntranceEdges;
        var rooms = plan.Rooms.OrderBy(room => room.InstanceId, StringComparer.Ordinal).ToArray();
        var bedroom = rooms.Select(room => MatchesAny(bedroomPatterns, room)).ToArray();

        // Doors between rooms.
        var built = new List<Door>();
        for (var i = 0; i < rooms.Length; i++)
            for (var j = i + 1; j < rooms.Length; j++)
            {
                if (rooms[i].Level != rooms[j].Level) continue;
                if (Common(rooms[i].Rect, rooms[j].Rect) is not { } segment || segment.Length < door - Epsilon) continue;
                built.Add(DoorOn(segment, door, i, j, rooms[i].Level));
            }
        var doors = built.OrderBy(d => d.Level).ThenBy(d => rooms[d.A].InstanceId, StringComparer.Ordinal).ThenBy(d => rooms[d.B].InstanceId, StringComparer.Ordinal).ToArray();

        // Entrance.
        var candidates = new List<int>();
        if (options.EntranceRoomId is { } explicitRoom)
            candidates.AddRange(Enumerable.Range(0, rooms.Length).Where(k => rooms[k].Level == EntranceLevel
                && (string.Equals(rooms[k].RoomId, explicitRoom, StringComparison.Ordinal) || string.Equals(rooms[k].InstanceId, explicitRoom, StringComparison.Ordinal))));
        else
        {
            var groundRooms = Enumerable.Range(0, rooms.Length).Where(k => rooms[k].Level == EntranceLevel && !bedroom[k]).ToArray();
            var named = groundRooms.Where(k => MatchesAny(entrancePatterns, rooms[k])).ToArray();
            candidates.AddRange(BySize(named, rooms));
            candidates.AddRange(BySize(groundRooms.Except(named).ToArray(), rooms));
        }
        var entranceRoom = -1;
        LotEdge? entranceEdge = null;
        Segment? entranceSide = null;
        foreach (var candidate in candidates)
        {
            foreach (var edge in edges)
                if (Side(rooms[candidate].Rect, edge, lotWidth, lotDepth) is { } side && side.Length >= door - Epsilon)
                {
                    (entranceRoom, entranceEdge, entranceSide) = (candidate, edge, side);
                    break;
                }
            if (entranceRoom >= 0) break;
        }
        Door? entrance = entranceSide is { } chosen ? DoorOn(chosen, door, -1, entranceRoom, EntranceLevel) : null;

        // Routes and checks.
        var any = entrance is null ? null : new Search(rooms, bedroom, doors, entrance, entranceRoom, avoidBedrooms: false);
        var clear = entrance is null ? null : new Search(rooms, bedroom, doors, entrance, entranceRoom, avoidBedrooms: true);
        var used = new HashSet<int>();
        var routes = new List<CirculationRoute>();
        var checks = new List<PlanConstraint>();
        if (entrance is not null && entranceEdge is { } placedEdge)
            checks.Add(new PlanConstraint(CirculationCodes.Entrance, "plan", true, true, "entrance door of " + Format(door) + " m on the " + EdgeName(placedEdge) + " lot edge of "
                + rooms[entranceRoom].InstanceId + " at (" + Format(entrance.MidX) + ", " + Format(entrance.MidY) + ")"));
        else
            checks.Add(new PlanConstraint(CirculationCodes.Entrance, "plan", true, false, (options.EntranceRoomId is { } given ? "entrance room '" + given + "'" : "no non-bedroom room")
                + " on level 0 has a side of at least " + Format(door) + " m on the lot edges " + string.Join(", ", edges.Select(EdgeName))));
        for (var k = 0; k < rooms.Length; k++)
        {
            var room = rooms[k];
            var free = clear?.Best(k);
            var found = free ?? any?.Best(k);
            if (found is not null)
            {
                foreach (var crossed in found.Doors) used.Add(crossed);
                routes.Add(new CirculationRoute(room.InstanceId, bedroom[k], true, free is not null, found.Length, found.Rooms.Select(r => rooms[r].InstanceId).ToArray(),
                    found.Doors.Select(d => d < 0 ? CirculationModel.EntranceDoorId : DoorId(rooms, doors[d])).ToArray()));
                checks.Add(new PlanConstraint(CirculationCodes.Reachable, room.InstanceId, true, true, "reached from the entrance in " + Format(found.Length) + " m through "
                    + Text(found.Doors.Count) + (found.Doors.Count == 1 ? " door" : " doors")));
            }
            else
            {
                routes.Add(new CirculationRoute(room.InstanceId, bedroom[k], false, false, double.PositiveInfinity, Array.Empty<string>(), Array.Empty<string>()));
                var why = room.Level != EntranceLevel ? "on level " + Text(room.Level) + "; no stairs are modelled, so only level 0 rooms are reachable from the entrance"
                    : entrance is null ? "there is no entrance on the lot edge"
                    : "no chain of shared walls of at least " + Format(door) + " m joins it to the entrance room " + rooms[entranceRoom].InstanceId;
                checks.Add(new PlanConstraint(CirculationCodes.Reachable, room.InstanceId, true, false, why));
            }
            if (!bedroom[k]) continue;
            if (free is not null) checks.Add(new PlanConstraint(CirculationCodes.BedroomAccess, room.InstanceId, true, true, "reached without passing through another bedroom"));
            else if (found is not null)
                checks.Add(new PlanConstraint(CirculationCodes.BedroomAccess, room.InstanceId, true, false, "reachable only through bedroom "
                    + string.Join(", ", found.Rooms.Take(found.Rooms.Count - 1).Where(r => bedroom[r]).Select(r => rooms[r].InstanceId))));
            else checks.Add(new PlanConstraint(CirculationCodes.BedroomAccess, room.InstanceId, true, false, "the bedroom is not reachable from the entrance"));
        }
        var doorRecords = doors.Select((d, index) => Record(rooms, d, DoorId(rooms, d), used.Contains(index))).ToArray();
        var entranceRecord = entrance is null ? null : Record(rooms, entrance, CirculationModel.EntranceDoorId, used.Contains(-1));
        var status = checks.All(check => check.Satisfied) ? CirculationStatus.Valid : CirculationStatus.Invalid;
        return new CirculationModel(status, name, plan.Seed, door, entranceRecord, entranceEdge, doorRecords, routes, checks);
    }

    // Plan-independent option checks, shared with CirculationRanker.
    internal static List<PlanConstraint> OptionRefusals(CirculationOptions options)
    {
        var refusals = new List<PlanConstraint>();
        if (options.DoorWidthMetres is { } width && !(double.IsFinite(width) && width > 0))
            refusals.Add(Refusal(CirculationCodes.OptionsInvalid, "door_width_m", "door width must be positive and finite"));
        if (options.EntranceEdges is { } edges)
        {
            if (edges.Count == 0) refusals.Add(Refusal(CirculationCodes.OptionsInvalid, "entrance_edges", "at least one lot edge is required"));
            for (var i = 0; i < edges.Count; i++)
                if (!Enum.IsDefined(edges[i])) refusals.Add(Refusal(CirculationCodes.OptionsInvalid, "entrance_edges[" + Text(i) + "]", "undefined lot edge " + Text((int)edges[i])));
        }
        foreach (var (field, patterns) in new[] { ("bedroom_patterns", options.BedroomPatterns), ("entrance_patterns", options.EntrancePatterns) })
            if (patterns is not null)
                for (var i = 0; i < patterns.Count; i++)
                    if (string.IsNullOrWhiteSpace(patterns[i])) refusals.Add(Refusal(CirculationCodes.OptionsInvalid, field + "[" + Text(i) + "]", "pattern must not be blank"));
        if (options.EntranceRoomId is { } room && string.IsNullOrWhiteSpace(room)) refusals.Add(Refusal(CirculationCodes.OptionsInvalid, "entrance_room", "entrance room id must not be blank"));
        return refusals;
    }

    internal static string EdgeName(LotEdge edge) => edge switch
    {
        LotEdge.South => "south",
        LotEdge.East => "east",
        LotEdge.North => "north",
        LotEdge.West => "west",
        _ => "undefined",
    };

    internal static (double Width, double Depth) Lot(ProjectBrief brief, SpacePlan plan)
    {
        if (brief.Constraints?.Site is { } site && site.WidthMetres is not null && site.DepthMetres is not null
            && double.IsFinite(site.WidthMetres.Value) && site.WidthMetres.Value > 0 && double.IsFinite(site.DepthMetres.Value) && site.DepthMetres.Value > 0)
            return (site.WidthMetres.Value, site.DepthMetres.Value);
        return plan.Footprint is { } footprint ? (footprint.WidthMetres, footprint.DepthMetres) : (double.NaN, double.NaN);
    }

    // A segment on an axis-aligned line: a vertical one is x = Line, y in [Low, High]; a horizontal one y = Line.
    readonly record struct Segment(bool Vertical, double Line, double Low, double High)
    {
        public double Length => High - Low;
    }

    // A door of the given width centred on a segment; A is -1 (the exterior) for the entrance.
    sealed record Door(int A, int B, int Level, bool Vertical, double Line, double Mid, double Half)
    {
        public double MidX => Vertical ? Line : Mid;
        public double MidY => Vertical ? Mid : Line;
    }

    static Door DoorOn(Segment segment, double width, int a, int b, int level) => new(a, b, level, segment.Vertical, segment.Line, (segment.Low + segment.High) / 2, width / 2);

    static CirculationDoor Record(PlannedRoom[] rooms, Door door, string id, bool used)
    {
        var from = door.A < 0 ? CirculationModel.Exterior : rooms[door.A].InstanceId;
        var to = rooms[door.B].InstanceId;
        return door.Vertical
            ? new CirculationDoor(id, from, to, door.Level, door.Line, door.Mid - door.Half, door.Line, door.Mid + door.Half, used)
            : new CirculationDoor(id, from, to, door.Level, door.Mid - door.Half, door.Line, door.Mid + door.Half, door.Line, used);
    }

    static string DoorId(PlannedRoom[] rooms, Door door) => "door:" + rooms[door.A].InstanceId + "/" + rooms[door.B].InstanceId;

    // The common edge of two rectangles that touch along an edge, or null.
    static Segment? Common(PlanRect a, PlanRect b)
    {
        if (Math.Abs(a.Right - b.X) < Epsilon) return Overlap(true, b.X, a.Y, a.Top, b.Y, b.Top);
        if (Math.Abs(b.Right - a.X) < Epsilon) return Overlap(true, a.X, a.Y, a.Top, b.Y, b.Top);
        if (Math.Abs(a.Top - b.Y) < Epsilon) return Overlap(false, b.Y, a.X, a.Right, b.X, b.Right);
        if (Math.Abs(b.Top - a.Y) < Epsilon) return Overlap(false, a.Y, a.X, a.Right, b.X, b.Right);
        return null;
    }

    static Segment? Overlap(bool vertical, double line, double low0, double high0, double low1, double high1)
    {
        var low = Math.Max(low0, low1);
        var high = Math.Min(high0, high1);
        return high > low ? new Segment(vertical, line, low, high) : null;
    }

    // The side of a room lying on a lot edge, or null.
    static Segment? Side(PlanRect rect, LotEdge edge, double lotWidth, double lotDepth) => edge switch
    {
        LotEdge.South when Math.Abs(rect.Y) < Epsilon => new Segment(false, rect.Y, rect.X, rect.Right),
        LotEdge.North when Math.Abs(rect.Top - lotDepth) < Epsilon => new Segment(false, rect.Top, rect.X, rect.Right),
        LotEdge.West when Math.Abs(rect.X) < Epsilon => new Segment(true, rect.X, rect.Y, rect.Top),
        LotEdge.East when Math.Abs(rect.Right - lotWidth) < Epsilon => new Segment(true, rect.Right, rect.Y, rect.Top),
        _ => null,
    };

    static IEnumerable<int> BySize(int[] indices, PlannedRoom[] rooms) =>
        indices.OrderByDescending(k => rooms[k].Rect.Area).ThenBy(k => rooms[k].InstanceId, StringComparer.Ordinal);

    // A found route: rooms entered in order and the doors crossed (-1 is the entrance door).
    sealed record Walk(double Length, IReadOnlyList<int> Rooms, IReadOnlyList<int> Doors);

    // Shortest paths over states: state 0 is the entrance door crossed into the entrance room; state 1 + 2d is door d
    // crossed into its B room, 2 + 2d door d crossed into its A room. From a state in room R every other door of R is a
    // straight walk, allowed only when R may be passed through. The unvisited state with the smallest distance (lowest
    // index on ties) is settled next and distances only improve strictly, so the result is deterministic.
    sealed class Search
    {
        readonly PlannedRoom[] rooms;
        readonly Door[] doors;
        readonly Door entrance;
        readonly int[] stateRoom;
        readonly int[] stateDoor;
        readonly double[] distance;
        readonly int[] previous;

        public Search(PlannedRoom[] rooms, bool[] bedroom, Door[] doors, Door entrance, int entranceRoom, bool avoidBedrooms)
        {
            this.rooms = rooms;
            this.doors = doors;
            this.entrance = entrance;
            var count = 1 + 2 * doors.Length;
            stateRoom = new int[count];
            stateDoor = new int[count];
            stateRoom[0] = entranceRoom;
            stateDoor[0] = -1;
            for (var d = 0; d < doors.Length; d++)
            {
                stateRoom[1 + 2 * d] = doors[d].B;
                stateDoor[1 + 2 * d] = d;
                stateRoom[2 + 2 * d] = doors[d].A;
                stateDoor[2 + 2 * d] = d;
            }
            var incident = new List<int>[rooms.Length];
            for (var r = 0; r < rooms.Length; r++) incident[r] = new List<int>();
            for (var d = 0; d < doors.Length; d++)
            {
                incident[doors[d].A].Add(d);
                incident[doors[d].B].Add(d);
            }
            distance = Enumerable.Repeat(double.PositiveInfinity, count).ToArray();
            previous = Enumerable.Repeat(-1, count).ToArray();
            var done = new bool[count];
            distance[0] = 0;
            while (true)
            {
                var current = -1;
                for (var s = 0; s < count; s++)
                    if (!done[s] && double.IsFinite(distance[s]) && (current < 0 || distance[s] < distance[current])) current = s;
                if (current < 0) break;
                done[current] = true;
                var here = stateRoom[current];
                if (avoidBedrooms && bedroom[here]) continue;
                var at = At(current);
                foreach (var d in incident[here])
                {
                    if (d == stateDoor[current]) continue;
                    var next = doors[d].A == here ? 1 + 2 * d : 2 + 2 * d;
                    var length = distance[current] + Distance(at.MidX, at.MidY, doors[d].MidX, doors[d].MidY);
                    if (length < distance[next])
                    {
                        distance[next] = length;
                        previous[next] = current;
                    }
                }
            }
        }

        Door At(int state) => stateDoor[state] < 0 ? entrance : doors[stateDoor[state]];

        // The shortest route ending at the centre of the room, or null when no state enters it.
        public Walk? Best(int target)
        {
            var rect = rooms[target].Rect;
            var cx = rect.X + rect.Width / 2;
            var cy = rect.Y + rect.Depth / 2;
            var best = -1;
            var bestLength = double.PositiveInfinity;
            for (var s = 0; s < stateRoom.Length; s++)
            {
                if (stateRoom[s] != target || !double.IsFinite(distance[s])) continue;
                var at = At(s);
                var length = distance[s] + Distance(at.MidX, at.MidY, cx, cy);
                if (best < 0 || length < bestLength)
                {
                    best = s;
                    bestLength = length;
                }
            }
            if (best < 0) return null;
            var states = new List<int>();
            for (var s = best; s >= 0; s = previous[s]) states.Add(s);
            states.Reverse();
            return new Walk(bestLength, states.Select(s => stateRoom[s]).ToArray(), states.Select(s => stateDoor[s]).ToArray());
        }
    }

    static double Distance(double x0, double y0, double x1, double y1) => Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));

    static bool MatchesAny(IReadOnlyList<string> patterns, PlannedRoom room) =>
        patterns.Any(pattern => Glob(pattern, room.RoomId ?? "") || Glob(pattern, room.Name ?? ""));

    // '*' any run, '?' one character, case-insensitive (invariant), as SpacePlanner matches rule name patterns.
    static bool Glob(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t]))) { p++; t++; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    static PlanConstraint Refusal(string code, string subject, string detail) => new(code, subject, true, false, detail);

    internal static string Format(double value) => double.IsFinite(value) ? SpacePlanSerializer.Format(value) : "non-finite";

    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
