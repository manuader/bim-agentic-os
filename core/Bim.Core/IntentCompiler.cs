using System.Globalization;
using System.Text.RegularExpressions;
namespace Bim.Core;

// P3-01: deterministic compiler from LayoutIntent to the existing BIM-IR records (no schema change).
//
// Identity: every element id is RoomCompiler.Id(projectId, intentKey), i.e. the first 16 bytes of SHA-256 over
// "<projectId:D>:<intentKey>". Intent keys are derived only from user keys, never from names or input order:
//   level:<level>                 the level
//   wall:<room>:<side>[:<n>]      an unshared stretch of one room side; n is the 0-based stretch index along the side
//                                 in increasing coordinate, omitted for the first (usually only) stretch
//   wall:<a>|<b>                  the one wall shared by rooms a and b (ordinal pair; two axis-aligned rectangles
//                                 share at most one stretch)
//   space:<room>                  the room's space
//   opening:<opening>             a door or window
// User keys match [a-z0-9][a-z0-9_-]{0,63}, so ':' and '|' never collide.
//
// Geometry (metres): level [elevation]; wall [ax,ay,bx,by,height,thickness] on the axis line, unshared stretches
// counter-clockwise around their room like RoomCompiler (south +x, east +y, north -x, west -y), shared walls toward +x
// or +y; space [x,y,width,depth] = interior corner and clear size; door/window [ax,ay,bx,by,sill,height] = the opening's
// stretch of the host axis in the host's direction, then sill and height.
// Dependencies: wall -> [level]; space -> [level, bounding walls by key]; opening -> [level, host wall], so
// SemanticGraph types them OnLevel, Bounds and HostedBy.
//
// Element order: levels, walls, spaces, openings, each by key (ordinal). Axis coordinates closer than Tolerance are
// snapped to the smallest of them per level, so 0.1+0.2 abuts 0.3.
public static class IntentCompiler
{
    public const double Tolerance = 1e-6;

    static readonly Regex KeyPattern = new(@"\A[a-z0-9][a-z0-9_-]{0,63}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static bool IsKey(string? value) => value is not null && KeyPattern.IsMatch(value);

    public static Guid Id(Guid projectId, string intentKey) => RoomCompiler.Id(projectId, intentKey);
    public static string LevelKey(string level) => "level:" + level;
    public static string SpaceKey(string room) => "space:" + room;
    public static string OpeningKey(string opening) => "opening:" + opening;

    public static string WallKey(string room, IntentSide side, int segment = 0)
    {
        if (segment < 0) throw new ArgumentOutOfRangeException(nameof(segment));
        var key = "wall:" + room + ":" + IntentJson.Side(side);
        return segment == 0 ? key : key + ":" + segment.ToString(CultureInfo.InvariantCulture);
    }

    public static string SharedWallKey(string roomA, string roomB) =>
        string.CompareOrdinal(roomA, roomB) <= 0 ? "wall:" + roomA + "|" + roomB : "wall:" + roomB + "|" + roomA;

    // Issues sorted by code, key and detail (ordinal), so the list is independent of input order.
    public static IntentIssue[] Validate(LayoutIntent intent)
    {
        var issues = new List<IntentIssue>();
        Analyze(intent, issues);
        return Sorted(issues);
    }

    public static Project Compile(Guid projectId, int revision, LayoutIntent intent)
    {
        if (projectId == Guid.Empty || revision < 0) throw new ArgumentException("invalid_project_identity");
        var issues = new List<IntentIssue>();
        var layout = Analyze(intent, issues);
        if (layout is null || issues.Count > 0)
        {
            var sorted = Sorted(issues);
            throw new IntentException(sorted[0].Code, null, sorted[0].Detail, sorted);
        }
        var provenance = intent.Provenance;
        var elements = new List<Element>();
        Guid LevelId(string level) => Id(projectId, LevelKey(level));
        foreach (var level in layout.Levels)
            elements.Add(new Element(LevelId(level.Key), "level", new[] { Z(level.ElevationMetres) }, Array.Empty<Guid>(), provenance));
        foreach (var wall in layout.Walls)
        {
            var (ax, ay, bx, by) = wall.Points(wall.Start, wall.End);
            elements.Add(new Element(Id(projectId, wall.Key), "wall", new[] { ax, ay, bx, by, Z(wall.Type.HeightMetres), Z(wall.Type.ThicknessMetres) },
                new[] { LevelId(wall.Level) }, provenance));
        }
        foreach (var space in layout.Spaces)
        {
            var t = space.Type.ThicknessMetres;
            var geometry = new[] { Z(space.Left + t / 2), Z(space.Bottom + t / 2), Z(space.Right - space.Left - t), Z(space.Top - space.Bottom - t) };
            var dependencies = new[] { LevelId(space.Room.Level) }.Concat(space.Walls.Select(bounding => Id(projectId, bounding.Key))).ToArray();
            elements.Add(new Element(Id(projectId, SpaceKey(space.Room.Key)), "space", geometry, dependencies, provenance));
        }
        foreach (var hole in layout.Openings)
        {
            var (ax, ay, bx, by) = hole.Host.Points(hole.From, hole.To);
            var opening = hole.Opening;
            elements.Add(new Element(Id(projectId, OpeningKey(opening.Key)), IntentJson.Kind(opening.Kind),
                new[] { ax, ay, bx, by, Z(opening.SillMetres), Z(opening.HeightMetres) },
                new[] { LevelId(hole.Host.Level), Id(projectId, hole.Host.Key) }, provenance));
        }
        return new Project(projectId, revision, elements.ToArray());
    }

    // -0 and 0 are one quantity; a single representation keeps hashes unique.
    static double Z(double value) => value == 0 ? 0.0 : value;

    static IntentIssue[] Sorted(List<IntentIssue> issues) => issues
        .OrderBy(issue => issue.Code, StringComparer.Ordinal).ThenBy(issue => issue.Key, StringComparer.Ordinal).ThenBy(issue => issue.Detail, StringComparer.Ordinal)
        .ToArray();

    sealed class Wall(bool vertical, double line, double start, double end, string? low, string? high, IntentWallType type, string level)
    {
        // Vertical walls lie on x = Line and span y in [Start, End]; horizontal ones on y = Line, x in [Start, End].
        public bool Vertical { get; } = vertical;
        public double Line { get; } = line;
        public double Start { get; } = start;
        public double End { get; } = end;
        // Low is the room below/left of the line, High the room above/right; both are set for a shared wall.
        public string? Low { get; } = low;
        public string? High { get; } = high;
        public IntentWallType Type { get; } = type;
        public string Level { get; } = level;
        public string Key { get; set; } = "";
        public IntentSide Side => Vertical ? (High is null ? IntentSide.East : IntentSide.West) : (High is null ? IntentSide.North : IntentSide.South);
        public bool Shared => Low is not null && High is not null;
        // Unshared north and west stretches run toward decreasing coordinate (counter-clockwise around the room).
        public bool Reversed => !Shared && Side is IntentSide.North or IntentSide.West;
        public bool Bounds(string room) => Low == room || High == room;

        // Endpoints of the stretch [from, to] of this axis, in the wall's direction.
        public (double Ax, double Ay, double Bx, double By) Points(double from, double to)
        {
            var (a, b) = Reversed ? (to, from) : (from, to);
            return Vertical ? (Z(Line), Z(a), Z(Line), Z(b)) : (Z(a), Z(Line), Z(b), Z(Line));
        }
    }

    sealed record Space(IntentRoom Room, IntentWallType Type, double Left, double Bottom, double Right, double Top, Wall[] Walls);

    sealed record Hole(IntentOpening Opening, Wall Host, double From, double To);

    sealed record Layout(IntentLevel[] Levels, Wall[] Walls, Space[] Spaces, Hole[] Openings);

    // One room side on an axis line; High means the room lies above (horizontal line) or right (vertical line) of it.
    sealed record Edge(string Room, double Start, double End, bool High);

    static void AddEdge(SortedDictionary<double, List<Edge>> lines, double line, Edge edge)
    {
        if (!lines.TryGetValue(line, out var list)) lines.Add(line, list = new List<Edge>());
        list.Add(edge);
    }

    static bool Positive(double value) => double.IsFinite(value) && value > 0;

    // Null when the intent is invalid; every problem found is added to issues.
    static Layout? Analyze(LayoutIntent intent, List<IntentIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(intent.Levels);
        ArgumentNullException.ThrowIfNull(intent.WallTypes);
        ArgumentNullException.ThrowIfNull(intent.Rooms);
        ArgumentNullException.ThrowIfNull(intent.Openings);
        if (intent.Levels.Any(item => item is null) || intent.WallTypes.Any(item => item is null) || intent.Rooms.Any(item => item is null) || intent.Openings.Any(item => item is null))
            throw new ArgumentException("null_intent_item");
        void Issue(string code, string? key, string detail) => issues.Add(new IntentIssue(code, key ?? "", detail));

        if (intent.Version != LayoutIntent.CurrentVersion) Issue(IntentCodes.VersionUnsupported, "", "intent_version 1 required");
        if (intent.Provenance is null || string.IsNullOrWhiteSpace(intent.Provenance.Source) || string.IsNullOrWhiteSpace(intent.Provenance.Approval))
            Issue(IntentCodes.ProvenanceInvalid, "", "provenance source and approval required");
        var limited = false;
        void Limit(int count, int max, string name)
        {
            if (count <= max) return;
            Issue(IntentCodes.LimitExceeded, name, "at most " + max.ToString(CultureInfo.InvariantCulture) + " " + name);
            limited = true;
        }
        Limit(intent.Levels.Count, LayoutIntent.MaxLevels, "levels");
        Limit(intent.WallTypes.Count, LayoutIntent.MaxWallTypes, "wall_types");
        Limit(intent.Rooms.Count, LayoutIntent.MaxRooms, "rooms");
        Limit(intent.Openings.Count, LayoutIntent.MaxOpenings, "openings");
        if (limited) return null;

        // Keys are unique per collection; the ordinal-first occurrence is indexed.
        Dictionary<string, T> Index<T>(IEnumerable<T> items, Func<T, string> keyOf, Func<T, string> nameOf)
        {
            var found = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var item in items.OrderBy(keyOf, StringComparer.Ordinal))
            {
                var itemKey = keyOf(item);
                if (!IsKey(itemKey)) Issue(IntentCodes.KeyInvalid, itemKey, "1-64 of [a-z0-9_-], starting with a letter or digit");
                else if (!found.TryAdd(itemKey, item) && !issues.Any(issue => issue.Code == IntentCodes.DuplicateKey && issue.Key == itemKey))
                    Issue(IntentCodes.DuplicateKey, itemKey, "key used more than once");
                if (!BriefParser.IsText(nameOf(item))) Issue(IntentCodes.TextInvalid, itemKey, "name must be non-blank text of at most 2048 UTF-16 code units");
            }
            return found;
        }
        var levels = Index(intent.Levels, item => item.Key, item => item.Name);
        var types = Index(intent.WallTypes, item => item.Key, item => item.Name);
        var rooms = Index(intent.Rooms, item => item.Key, item => item.Name);
        var openings = Index(intent.Openings, item => item.Key, item => item.Key);

        foreach (var level in levels.Values)
            if (!double.IsFinite(level.ElevationMetres)) Issue(IntentCodes.DimensionInvalid, level.Key, "elevation_m must be finite");
        foreach (var type in types.Values)
        {
            if (!Positive(type.ThicknessMetres)) Issue(IntentCodes.DimensionInvalid, type.Key, "thickness_m must be finite and positive");
            if (!Positive(type.HeightMetres)) Issue(IntentCodes.DimensionInvalid, type.Key, "height_m must be finite and positive");
        }
        foreach (var room in rooms.Values)
        {
            if (!double.IsFinite(room.XMetres) || !double.IsFinite(room.YMetres)) Issue(IntentCodes.DimensionInvalid, room.Key, "x_m and y_m must be finite");
            else if (!Positive(room.WidthMetres) || !Positive(room.DepthMetres)) Issue(IntentCodes.DimensionInvalid, room.Key, "width_m and depth_m must be finite and positive");
            else if (!double.IsFinite(room.XMetres + room.WidthMetres) || !double.IsFinite(room.YMetres + room.DepthMetres)) Issue(IntentCodes.DimensionInvalid, room.Key, "room extent must be finite");
            if (room.Level is null || !levels.ContainsKey(room.Level)) Issue(IntentCodes.UnknownLevel, room.Key, "level " + room.Level);
            if (room.WallType is null || !types.ContainsKey(room.WallType)) Issue(IntentCodes.UnknownWallType, room.Key, "wall_type " + room.WallType);
        }
        foreach (var opening in openings.Values)
        {
            if (!Enum.IsDefined(opening.Kind)) Issue(IntentCodes.ValueInvalid, opening.Key, "kind must be door or window");
            if (!double.IsFinite(opening.OffsetMetres) || !double.IsFinite(opening.SillMetres)) Issue(IntentCodes.DimensionInvalid, opening.Key, "offset_m and sill_m must be finite");
            if (!Positive(opening.WidthMetres) || !Positive(opening.HeightMetres)) Issue(IntentCodes.DimensionInvalid, opening.Key, "width_m and height_m must be finite and positive");
            switch (opening.Host)
            {
                case IntentRoomSide side:
                    if (side.Room is null || !rooms.ContainsKey(side.Room)) Issue(IntentCodes.UnknownRoom, opening.Key, "room " + side.Room);
                    if (!Enum.IsDefined(side.Side)) Issue(IntentCodes.ValueInvalid, opening.Key, "side must be south, east, north or west");
                    break;
                case IntentSharedWall shared:
                    if (shared.RoomA is null || !rooms.ContainsKey(shared.RoomA)) Issue(IntentCodes.UnknownRoom, opening.Key, "room " + shared.RoomA);
                    if (shared.RoomB is null || !rooms.ContainsKey(shared.RoomB)) Issue(IntentCodes.UnknownRoom, opening.Key, "room " + shared.RoomB);
                    break;
                default:
                    Issue(IntentCodes.ValueInvalid, opening.Key, "host must be a room side or a shared wall");
                    break;
            }
        }
        if (issues.Count > 0) return null;

        // ---- Walls and spaces, level by level ----
        var walls = new List<Wall>();
        var spaces = new Dictionary<string, Space>(StringComparer.Ordinal);
        var validLevels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var level in levels.Values.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var before = issues.Count;
            var onLevel = rooms.Values.Where(item => item.Level == level.Key).OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
            var xs = Snap(onLevel.SelectMany(item => new[] { item.XMetres, item.XMetres + item.WidthMetres }));
            var ys = Snap(onLevel.SelectMany(item => new[] { item.YMetres, item.YMetres + item.DepthMetres }));
            var rects = onLevel.Select(item => new Space(item, types[item.WallType], xs[item.XMetres], ys[item.YMetres],
                xs[item.XMetres + item.WidthMetres], ys[item.YMetres + item.DepthMetres], Array.Empty<Wall>())).ToArray();
            foreach (var rect in rects)
                if (rect.Right - rect.Left <= rect.Type.ThicknessMetres || rect.Top - rect.Bottom <= rect.Type.ThicknessMetres)
                    Issue(IntentCodes.RoomTooSmall, rect.Room.Key, "axis size must exceed wall thickness " + rect.Type.ThicknessMetres.ToString("R", CultureInfo.InvariantCulture));
            for (var i = 0; i < rects.Length; i++)
                for (var j = i + 1; j < rects.Length; j++)
                {
                    var (p, q) = (rects[i], rects[j]);
                    if (Math.Min(p.Right, q.Right) - Math.Max(p.Left, q.Left) > 0 && Math.Min(p.Top, q.Top) - Math.Max(p.Bottom, q.Bottom) > 0)
                        Issue(IntentCodes.RoomOverlap, p.Room.Key, "overlaps " + q.Room.Key);
                }
            if (issues.Count > before) continue;

            var horizontal = new SortedDictionary<double, List<Edge>>();
            var vertical = new SortedDictionary<double, List<Edge>>();
            foreach (var rect in rects)
            {
                AddEdge(horizontal, rect.Bottom, new Edge(rect.Room.Key, rect.Left, rect.Right, true));
                AddEdge(horizontal, rect.Top, new Edge(rect.Room.Key, rect.Left, rect.Right, false));
                AddEdge(vertical, rect.Left, new Edge(rect.Room.Key, rect.Bottom, rect.Top, true));
                AddEdge(vertical, rect.Right, new Edge(rect.Room.Key, rect.Bottom, rect.Top, false));
            }
            var levelWalls = new List<Wall>();
            foreach (var (lines, isVertical) in new[] { (horizontal, false), (vertical, true) })
                foreach (var (line, edges) in lines)
                {
                    var points = edges.SelectMany(edge => new[] { edge.Start, edge.End }).Distinct().Order().ToArray();
                    Wall? previous = null;
                    for (var k = 0; k + 1 < points.Length; k++)
                    {
                        double from = points[k], to = points[k + 1];
                        var low = edges.FirstOrDefault(edge => !edge.High && edge.Start <= from && edge.End >= to)?.Room;
                        var high = edges.FirstOrDefault(edge => edge.High && edge.Start <= from && edge.End >= to)?.Room;
                        if (low is null && high is null) { previous = null; continue; }
                        if (previous is not null && previous.End == from && previous.Low == low && previous.High == high)
                        {
                            levelWalls.Remove(previous);
                            previous = new Wall(isVertical, line, previous.Start, to, low, high, previous.Type, level.Key);
                            levelWalls.Add(previous);
                            continue;
                        }
                        var owner = low ?? high!;
                        if (low is not null && high is not null && rooms[low].WallType != rooms[high].WallType)
                        {
                            var ordered = string.CompareOrdinal(low, high) <= 0;
                            Issue(IntentCodes.SharedWallConflict, ordered ? low : high, "wall type differs from " + (ordered ? high : low));
                        }
                        previous = new Wall(isVertical, line, from, to, low, high, types[rooms[owner].WallType], level.Key);
                        levelWalls.Add(previous);
                    }
                }
            foreach (var shared in levelWalls.Where(candidate => candidate.Shared)) shared.Key = SharedWallKey(shared.Low!, shared.High!);
            foreach (var group in levelWalls.Where(candidate => !candidate.Shared).GroupBy(candidate => (Room: candidate.Low ?? candidate.High!, candidate.Side)))
            {
                var index = 0;
                foreach (var stretch in group.OrderBy(candidate => candidate.Start)) stretch.Key = WallKey(group.Key.Room, group.Key.Side, index++);
            }

            // Parallel walls on distinct axes overlap in plan when closer than half their thicknesses combined.
            foreach (var orientation in levelWalls.GroupBy(candidate => candidate.Vertical))
            {
                var sorted = orientation.OrderBy(candidate => candidate.Line).ThenBy(candidate => candidate.Key, StringComparer.Ordinal).ToArray();
                var widest = sorted.Max(candidate => candidate.Type.ThicknessMetres);
                for (var i = 0; i < sorted.Length; i++)
                    for (var j = i + 1; j < sorted.Length && sorted[j].Line - sorted[i].Line < widest; j++)
                    {
                        var (p, q) = (sorted[i], sorted[j]);
                        if (p.Line == q.Line || q.Line - p.Line >= (p.Type.ThicknessMetres + q.Type.ThicknessMetres) / 2) continue;
                        if (Math.Min(p.End, q.End) - Math.Max(p.Start, q.Start) <= Tolerance) continue;
                        var ordered = string.CompareOrdinal(p.Key, q.Key) <= 0;
                        var owners = new[] { p.Low, p.High, q.Low, q.High }.OfType<string>().Order(StringComparer.Ordinal).ToArray();
                        Issue(IntentCodes.WallClash, owners[0], (ordered ? p.Key : q.Key) + " clashes with " + (ordered ? q.Key : p.Key));
                    }
            }
            if (issues.Count > before) continue;

            foreach (var rect in rects)
                spaces.Add(rect.Room.Key, rect with { Walls = levelWalls.Where(candidate => candidate.Bounds(rect.Room.Key)).OrderBy(candidate => candidate.Key, StringComparer.Ordinal).ToArray() });
            walls.AddRange(levelWalls);
            validLevels.Add(level.Key);
        }

        // ---- Openings ----
        var holes = new List<Hole>();
        foreach (var opening in openings.Values)
        {
            Wall? host;
            double from, to;
            if (opening.Host is IntentRoomSide side)
            {
                if (!spaces.TryGetValue(side.Room, out var space)) continue; // its level already has issues
                var isVertical = side.Side is IntentSide.East or IntentSide.West;
                var line = side.Side switch { IntentSide.South => space.Bottom, IntentSide.North => space.Top, IntentSide.West => space.Left, _ => space.Right };
                var above = side.Side is IntentSide.South or IntentSide.West;
                var start = (isVertical ? space.Bottom : space.Left) + opening.OffsetMetres;
                var end = start + opening.WidthMetres;
                host = space.Walls.FirstOrDefault(candidate => candidate.Vertical == isVertical && candidate.Line == line && (above ? candidate.High : candidate.Low) == side.Room
                    && candidate.Start - Tolerance <= start && end <= candidate.End + Tolerance);
                if (host is null) { Issue(IntentCodes.OpeningOutsideWall, opening.Key, "not inside one wall of " + side.Room + " " + IntentJson.Side(side.Side)); continue; }
                (from, to) = (start, end);
            }
            else if (opening.Host is IntentSharedWall pair)
            {
                var a = rooms[pair.RoomA];
                var b = rooms[pair.RoomB];
                if (a.Key == b.Key || a.Level != b.Level) { Issue(IntentCodes.NotAdjacent, opening.Key, pair.RoomA + " and " + pair.RoomB + " share no wall"); continue; }
                if (!validLevels.Contains(a.Level)) continue;
                var wallKey = SharedWallKey(a.Key, b.Key);
                host = spaces[a.Key].Walls.FirstOrDefault(candidate => candidate.Key == wallKey);
                if (host is null) { Issue(IntentCodes.NotAdjacent, opening.Key, pair.RoomA + " and " + pair.RoomB + " share no wall"); continue; }
                from = host.Start + opening.OffsetMetres;
                to = from + opening.WidthMetres;
                if (from < host.Start - Tolerance || to > host.End + Tolerance) { Issue(IntentCodes.OpeningOutsideWall, opening.Key, "outside " + wallKey); continue; }
            }
            else continue;
            if (opening.SillMetres < 0 || opening.SillMetres + opening.HeightMetres > host.Type.HeightMetres + Tolerance)
            {
                Issue(IntentCodes.OpeningOutsideWall, opening.Key, "sill and height must stay within the wall height");
                continue;
            }
            holes.Add(new Hole(opening, host, from, to));
        }
        foreach (var group in holes.GroupBy(hole => hole.Host.Key))
        {
            var hosted = group.OrderBy(hole => hole.Opening.Key, StringComparer.Ordinal).ToArray();
            for (var i = 0; i < hosted.Length; i++)
                for (var j = i + 1; j < hosted.Length; j++)
                {
                    var (p, q) = (hosted[i], hosted[j]);
                    var across = Math.Min(p.To, q.To) - Math.Max(p.From, q.From);
                    var up = Math.Min(p.Opening.SillMetres + p.Opening.HeightMetres, q.Opening.SillMetres + q.Opening.HeightMetres) - Math.Max(p.Opening.SillMetres, q.Opening.SillMetres);
                    if (across > Tolerance && up > Tolerance) Issue(IntentCodes.OpeningOverlap, p.Opening.Key, "overlaps " + q.Opening.Key);
                }
        }
        if (issues.Count > 0) return null;

        return new Layout(levels.Values.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray(),
            walls.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray(),
            spaces.Values.OrderBy(item => item.Room.Key, StringComparer.Ordinal).ToArray(),
            holes.OrderBy(item => item.Opening.Key, StringComparer.Ordinal).ToArray());
    }

    // Maps each value to the smallest value of its cluster (consecutive sorted values at most Tolerance apart).
    static Dictionary<double, double> Snap(IEnumerable<double> values)
    {
        var map = new Dictionary<double, double>();
        double representative = 0, previous = 0;
        var first = true;
        foreach (var value in values.Distinct().Order())
        {
            if (first || value - previous > Tolerance) representative = value;
            map[value] = representative;
            previous = value;
            first = false;
        }
        return map;
    }
}
