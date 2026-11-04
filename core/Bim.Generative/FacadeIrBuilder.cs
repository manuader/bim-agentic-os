using System.Globalization;
using Bim.Core;
namespace Bim.Generative;

// Facade windows into the prototype BIM-IR (P7-06).
//
// Prototype: Intent(plan) turns a feasible space plan into a LayoutIntent (one level "l<n>" per plan level at elevation
// n x wall height, one wall type, every room an axis rectangle keyed by RoomKey(instance) in instance id order, no
// openings); Prototype compiles it with IntentCompiler, so walls lie on the room rectangles' lines (shared stretches
// once, unshared stretches counter-clockwise around their room) and Compiler.Validate is clean.
//
// AddWindows(prototype, layout, circulation?, options?):
// 1. Refuse a project Compiler.Validate rejects, a layout that is not Complete (refused, or a room failed: P7-07 rejects
//    such alternatives on the layout itself), invalid options, and a project that already holds an element with a window
//    element id (windows added twice).
// 2. For each window in layout order: the host is the one wall of the window's level (a wall whose dependencies hold
//    LevelId(project, level)) that is axis-aligned on the window's line (1e-7 m) and whose axis span holds the window
//    stretch. None: facade_ir_host_missing; several: facade_ir_host_ambiguous. The stretch must stay inside the host's
//    clear span (axis span less half the thickness of the thickest perpendicular wall of the level joined at each end,
//    as IntentCompiler) and the head at or below the host height, else facade_ir_outside_wall.
// 3. Entrance door: when the circulation model has an entrance on the window's level, line and orientation and the
//    window comes within EntranceClearanceMetres of it (overlap above 1e-7 m), the window is moved along its host to
//    just clear the door on the west/south side or on the east/north side, the smaller move first (moves within 1e-9 m
//    tie to the lower coordinate). A position must stay inside the host's usable span: the clear span, and the P7-05 end
//    clearance (layout.Options.EndClearanceMetres) from both host ends. A window (moved or not) must not overlap a window
//    already hosted on the same wall by more than 1e-7 m. No position: facade_ir_entrance_overlap (door conflict) or
//    facade_ir_window_overlap.
// 4. Each hosted window becomes a "window" element [ax,ay,bx,by,sill,head - sill] (ends on the host axis, ordered in the
//    host direction, the host's own line coordinate) with dependencies [level, host] and the options' provenance. The
//    window keeps its width, sill and head, so its illumination and ventilation areas are unchanged by a move.
// Every check detail uses invariant formatting; the result depends only on the inputs (not on room order or culture).
public static class FacadeIrBuilder
{
    const double Epsilon = 1e-7;
    const double TieTolerance = 1e-9;

    // Intent room key of a plan instance: the instance id with '#' (several instances of one room) written as '-'.
    public static string RoomKey(string instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.Replace('#', '-');
    }

    public static string LevelKey(int level) => "l" + level.ToString(CultureInfo.InvariantCulture);

    public static Guid LevelId(Guid projectId, int level) => IntentCompiler.Id(projectId, IntentCompiler.LevelKey(LevelKey(level)));

    public static Guid SpaceId(Guid projectId, string instance) => IntentCompiler.Id(projectId, IntentCompiler.SpaceKey(RoomKey(instance)));

    public static Guid WindowId(Guid projectId, string windowId) => IntentCompiler.Id(projectId, IntentCompiler.OpeningKey(windowId));

    // The layout intent of a feasible plan. ArgumentException for an infeasible or empty plan, invalid options, or an
    // instance whose key is not an intent key or collides with another instance's key.
    public static LayoutIntent Intent(SpacePlan plan, FacadeIrOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        options ??= new FacadeIrOptions();
        if (OptionProblem(options, walls: true) is { } problem) throw new ArgumentException(FacadeIrCodes.OptionsInvalid + ": " + problem);
        if (!plan.IsFeasible || plan.Rooms is null || plan.Rooms.Count == 0 || plan.Rooms.Any(room => room is null || room.Rect is null || room.InstanceId is null))
            throw new ArgumentException(FacadeIrCodes.LayoutInvalid + ": the plan must be feasible with rooms");
        var rooms = plan.Rooms.OrderBy(room => room.InstanceId, StringComparer.Ordinal).ToArray();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var room in rooms)
        {
            var key = RoomKey(room.InstanceId);
            if (!IntentCompiler.IsKey(key) || !keys.Add(key)) throw new ArgumentException(FacadeIrCodes.LayoutInvalid + ": instance " + room.InstanceId + " has no unique intent key");
            if (room.Level < 0) throw new ArgumentException(FacadeIrCodes.LayoutInvalid + ": instance " + room.InstanceId + " has a negative level");
        }
        var levelCount = Math.Max(plan.Levels, rooms.Max(room => room.Level) + 1);
        var levels = Enumerable.Range(0, levelCount)
            .Select(level => new IntentLevel(LevelKey(level), "Level " + level.ToString(CultureInfo.InvariantCulture), level * options.WallHeightMetres)).ToArray();
        var type = new IntentWallType("wall", "Wall", options.WallThicknessMetres, options.WallHeightMetres);
        var intentRooms = rooms.Select(room => new IntentRoom(RoomKey(room.InstanceId), string.IsNullOrWhiteSpace(room.Name) ? room.InstanceId : room.Name, LevelKey(room.Level), type.Key,
            room.Rect.X, room.Rect.Y, room.Rect.Width, room.Rect.Depth)).ToArray();
        return new LayoutIntent(LayoutIntent.CurrentVersion, options.Provenance ?? FacadeIrOptions.DefaultProvenance, levels, new[] { type }, intentRooms, Array.Empty<IntentOpening>());
    }

    // The prototype BIM-IR of a feasible plan (IntentCompiler.Compile of Intent). IntentException when IntentCompiler
    // refuses the rectangles (for example a room not larger than the wall thickness).
    public static Project Prototype(Guid projectId, int revision, SpacePlan plan, FacadeIrOptions? options = null) =>
        IntentCompiler.Compile(projectId, revision, Intent(plan, options));

    public static FacadeIrResult AddWindows(Project prototype, FacadeLayout layout, CirculationModel? circulation = null, FacadeIrOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(prototype);
        ArgumentNullException.ThrowIfNull(layout);
        options ??= new FacadeIrOptions();
        var refusals = new List<PlanConstraint>();
        if (OptionProblem(options, walls: false) is { } optionProblem) refusals.Add(Refusal(FacadeIrCodes.OptionsInvalid, "options", optionProblem));
        if (IsNull(prototype.Elements) || prototype.Elements.Any(element => element is null || element.GeometryMetres is null || element.Dependencies is null || element.Kind is null))
            refusals.Add(Refusal(FacadeIrCodes.ProjectInvalid, "project", "elements, kinds, geometry and dependencies are required"));
        else if (Compiler.Validate(prototype) is { Length: > 0 } issues)
            refusals.Add(Refusal(FacadeIrCodes.ProjectInvalid, "project", "Compiler.Validate: " + string.Join(", ", issues.Select(issue => issue.Code).Distinct().OrderBy(code => code, StringComparer.Ordinal))));
        if (layout.Status != FacadeStatus.Complete || IsNull(layout.Windows) || IsNull(layout.Options))
        {
            var unmet = layout.Checks?.Where(check => check is not null && !check.Satisfied).Select(check => check.Code + "@" + check.Subject) ?? Array.Empty<string>();
            refusals.Add(Refusal(FacadeIrCodes.LayoutInvalid, "layout", "the facade layout is " + layout.Status.ToString().ToLowerInvariant() + ": " + string.Join(", ", unmet)));
        }
        else if (layout.Windows.Any(window => window is null || window.Id is null || window.Instance is null)
            || layout.Windows.Select(window => window.Id).Distinct(StringComparer.Ordinal).Count() != layout.Windows.Count)
            refusals.Add(Refusal(FacadeIrCodes.LayoutInvalid, "layout", "window ids must be present and unique"));
        if (refusals.Count == 0)
        {
            var existing = prototype.Elements.Select(element => element.Id).ToHashSet();
            foreach (var window in layout.Windows)
                if (existing.Contains(WindowId(prototype.Id, window.Id)))
                    refusals.Add(Refusal(FacadeIrCodes.ElementExists, window.Id, "the project already holds element " + WindowId(prototype.Id, window.Id).ToString("D")));
        }
        if (refusals.Count > 0) return new FacadeIrResult(FacadeIrStatus.Refused, null, Array.Empty<FacadeIrWindow>(), refusals);

        var provenance = options.Provenance ?? FacadeIrOptions.DefaultProvenance;
        var endClearance = double.IsFinite(layout.Options.EndClearanceMetres) && layout.Options.EndClearanceMetres > 0 ? layout.Options.EndClearanceMetres : 0;
        var walls = prototype.Elements.Where(element => element.Kind == "wall" && GeometryCodec.Problem(element) is null).Select(element => new Wall(element)).ToArray();
        var checks = new List<PlanConstraint>();
        var hosted = new List<FacadeIrWindow>();
        var elements = new List<Element>();
        var placed = new List<(Guid Host, double Low, double High)>();
        var failed = false;
        foreach (var window in layout.Windows)
        {
            var horizontal = window.Side is FacadeSide.South or FacadeSide.North;
            var line = horizontal ? window.Y0 : window.X0;
            double low = horizontal ? window.X0 : window.Y0, high = horizontal ? window.X1 : window.Y1;
            void Fail(string code, string detail) { checks.Add(new PlanConstraint(code, window.Id, true, false, detail)); failed = true; }
            var levelId = LevelId(prototype.Id, window.Level);
            var onLevel = walls.Where(wall => wall.Element.Dependencies.Contains(levelId)).ToArray();
            var candidates = onLevel.Where(wall => wall.Horizontal == horizontal && Math.Abs(wall.Line - line) <= Epsilon
                && wall.Low - Epsilon <= low && high <= wall.High + Epsilon).ToArray();
            var where = (horizontal ? "y = " : "x = ") + Num(line) + " from " + Num(low) + " to " + Num(high) + " on level " + window.Level.ToString(CultureInfo.InvariantCulture);
            if (candidates.Length == 0) { Fail(FacadeIrCodes.HostMissing, "no wall of level " + LevelKey(window.Level) + " holds " + where); continue; }
            if (candidates.Length > 1)
            {
                Fail(FacadeIrCodes.HostAmbiguous, Count(candidates.Length) + " walls hold " + where + ": " + string.Join(", ", candidates.Select(wall => wall.Element.Id.ToString("D")).OrderBy(id => id, StringComparer.Ordinal)));
                continue;
            }
            var host = candidates[0];
            var hostId = host.Element.Id.ToString("D");
            double Inset(double at) => onLevel.Where(other => other.Horizontal != host.Horizontal && Math.Abs(other.Line - at) <= Epsilon
                && other.Low - Epsilon <= host.Line && host.Line <= other.High + Epsilon).Select(other => other.Thickness / 2).DefaultIfEmpty(0).Max();
            double clearLow = host.Low + Inset(host.Low), clearHigh = host.High - Inset(host.High);
            if (low < clearLow - Epsilon || high > clearHigh + Epsilon)
            {
                Fail(FacadeIrCodes.OutsideWall, "the window " + where + " leaves the clear span " + Num(clearLow) + " to " + Num(clearHigh) + " of wall " + hostId);
                continue;
            }
            if (window.HeadHeightMetres > host.Height + Epsilon)
            {
                Fail(FacadeIrCodes.OutsideWall, "the window head " + Num(window.HeadHeightMetres) + " m is above the " + Num(host.Height) + " m height of wall " + hostId);
                continue;
            }
            // Positions to try: where P7-05 put it, or (door conflict) just past either jamb, the smaller move first.
            var width = high - low;
            var positions = new List<double> { low };
            var conflict = false;
            if (circulation?.Entrance is { } door && door.Level == window.Level && Door(door) is { } span && span.Horizontal == horizontal && Math.Abs(span.Line - line) <= Epsilon)
            {
                var clearance = options.EntranceClearanceMetres;
                double forbiddenLow = span.Low - clearance, forbiddenHigh = span.High + clearance;
                if (Math.Min(high, forbiddenHigh) - Math.Max(low, forbiddenLow) > Epsilon)
                {
                    conflict = true;
                    double usableLow = Math.Max(clearLow, host.Low + endClearance), usableHigh = Math.Min(clearHigh, host.High - endClearance);
                    var before = forbiddenLow - width;
                    var after = forbiddenHigh;
                    var fits = new[] { before, after }.Where(start => start >= usableLow - Epsilon && start + width <= usableHigh + Epsilon).ToList();
                    fits.Sort((p, q) =>
                    {
                        double moveA = Math.Abs(p - low), moveB = Math.Abs(q - low);
                        return Math.Abs(moveA - moveB) <= TieTolerance ? p.CompareTo(q) : moveA.CompareTo(moveB);
                    });
                    positions = fits;
                }
            }
            double? chosen = null;
            foreach (var start in positions)
            {
                var end = start + width;
                if (placed.Any(other => other.Host == host.Element.Id && Math.Min(end, other.High) - Math.Max(start, other.Low) > Epsilon)) continue;
                chosen = start;
                break;
            }
            if (chosen is not { } from)
            {
                if (conflict)
                    Fail(FacadeIrCodes.EntranceOverlap, "the window " + where + " is within " + Num(options.EntranceClearanceMetres) + " m of the entrance door and no move along wall " + hostId
                        + " keeps it inside " + Num(Math.Max(clearLow, host.Low + endClearance)) + " to " + Num(Math.Min(clearHigh, host.High - endClearance)) + " and off the other windows");
                else
                    Fail(FacadeIrCodes.WindowOverlap, "the window " + where + " overlaps another window on wall " + hostId);
                continue;
            }
            var to = from == low ? high : from + width;
            placed.Add((host.Element.Id, from, to));
            // Ends on the host axis in the host's direction (Reversed: north and west stretches run toward lower values).
            var (a, b) = host.Reversed ? (to, from) : (from, to);
            var geometry = host.Horizontal
                ? new[] { Z(a), Z(host.Line), Z(b), Z(host.Line), Z(window.SillHeightMetres), Z(window.HeadHeightMetres - window.SillHeightMetres) }
                : new[] { Z(host.Line), Z(a), Z(host.Line), Z(b), Z(window.SillHeightMetres), Z(window.HeadHeightMetres - window.SillHeightMetres) };
            var offset = host.Reversed ? host.Start - to : from - host.Start;
            var shift = from == low ? 0 : from - low;
            var elementId = WindowId(prototype.Id, window.Id);
            elements.Add(new Element(elementId, "window", geometry, new[] { levelId, host.Element.Id }, provenance));
            hosted.Add(new FacadeIrWindow(window.Id, window.Instance, window.Level, elementId, host.Element.Id, offset, window.WidthMetres, window.SillHeightMetres, window.HeadHeightMetres, shift));
            checks.Add(new PlanConstraint(FacadeIrCodes.Window, window.Id, true, true, "hosted on wall " + hostId + " at offset " + Num(offset) + " m, width " + Num(to - from)
                + " m, sill " + Num(window.SillHeightMetres) + " m, head " + Num(window.HeadHeightMetres) + " m"));
            if (shift != 0)
                checks.Add(new PlanConstraint(FacadeIrCodes.Moved, window.Id, false, true, "moved " + Num(shift) + " m along wall " + hostId + " (from " + Num(low) + " to " + Num(from)
                    + ") to keep " + Num(options.EntranceClearanceMetres) + " m from the entrance door"));
        }
        if (failed) return new FacadeIrResult(FacadeIrStatus.Failed, null, hosted, checks);
        var project = prototype with { Elements = prototype.Elements.Concat(elements).ToArray() };
        if (Compiler.Validate(project) is { Length: > 0 } invalid)
        {
            // A safeguard: hosted windows reference existing elements with finite geometry, so this is meant to be unreachable.
            checks.Add(Refusal(FacadeIrCodes.ProjectInvalid, "project", "Compiler.Validate after adding windows: " + string.Join(", ", invalid.Select(issue => issue.Code).Distinct())));
            return new FacadeIrResult(FacadeIrStatus.Failed, null, hosted, checks);
        }
        return new FacadeIrResult(FacadeIrStatus.Complete, project, hosted, checks);
    }

    // P5-02 room facts read back from the BIM-IR: for every plan room (instance id order) its space (SpaceId) gives the
    // clear area and the shortest clear side; its window area sums width x height of the window elements hosted on a wall
    // the space depends on (its bounding walls), its ventilation area that sum x openableFraction (the opening fraction
    // is not carried by BIM-IR). Clear height is not given. ArgumentException for a room without a [x,y,width,depth]
    // space or a fraction outside (0, 1]. A window on a wall shared by two spaces would count for both (P7-05 windows
    // never sit on shared walls).
    public static IReadOnlyList<RoomFacts> ToRoomFacts(Project project, SpacePlan plan, double openableFraction)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);
        if (!(double.IsFinite(openableFraction) && openableFraction > 0 && openableFraction <= 1)) throw new ArgumentException(FacadeIrCodes.OptionsInvalid + ": openable fraction must be in (0, 1]");
        var elements = project.Elements.GroupBy(element => element.Id).ToDictionary(group => group.Key, group => group.First());
        var windows = project.Elements.Where(element => element.Kind == "window" && element.GeometryMetres.Length == 6 && element.GeometryMetres.All(double.IsFinite)).ToArray();
        var facts = new List<RoomFacts>();
        foreach (var room in plan.Rooms.OrderBy(room => room.InstanceId, StringComparer.Ordinal))
        {
            if (!elements.TryGetValue(SpaceId(project.Id, room.InstanceId), out var space) || space.Kind != "space" || GeometryCodec.Problem(space) is not null)
                throw new ArgumentException(FacadeIrCodes.ProjectInvalid + ": no space for " + room.InstanceId);
            var bounding = space.Dependencies.Where(dependency => elements.TryGetValue(dependency, out var wall) && wall.Kind == "wall").ToHashSet();
            var area = 0.0;
            foreach (var window in windows.Where(window => window.Dependencies.Any(bounding.Contains)))
            {
                var g = window.GeometryMetres;
                double dx = g[2] - g[0], dy = g[3] - g[1];
                area += Math.Sqrt(dx * dx + dy * dy) * g[5];
            }
            var s = space.GeometryMetres;
            facts.Add(new RoomFacts(room.InstanceId, room.Name ?? room.InstanceId, s[2] * s[3], Math.Min(s[2], s[3]), null, area, area * openableFraction));
        }
        return facts;
    }

    sealed class Wall
    {
        public Wall(Element element)
        {
            Element = element;
            var g = element.GeometryMetres;
            Horizontal = g[1] == g[3];
            Vertical = g[0] == g[2];
            Line = Horizontal ? g[1] : g[0];
            Start = Horizontal ? g[0] : g[1];
            var end = Horizontal ? g[2] : g[3];
            Reversed = end < Start;
            Low = Math.Min(Start, end);
            High = Math.Max(Start, end);
            Height = g[4];
            Thickness = g[5];
            // A diagonal wall hosts nothing: give it an empty span on no line.
            if (!Horizontal && !Vertical) { Low = double.PositiveInfinity; High = double.NegativeInfinity; Line = double.NaN; }
        }

        public Element Element { get; }
        public bool Horizontal { get; }
        public bool Vertical { get; }
        public double Line { get; }
        public double Start { get; }
        public bool Reversed { get; }
        public double Low { get; }
        public double High { get; }
        public double Height { get; }
        public double Thickness { get; }
    }

    static (bool Horizontal, double Line, double Low, double High)? Door(CirculationDoor door)
    {
        var values = new[] { door.X0, door.Y0, door.X1, door.Y1 };
        if (values.Any(value => !double.IsFinite(value))) return null;
        if (door.Y0 == door.Y1 && door.X0 != door.X1) return (true, door.Y0, Math.Min(door.X0, door.X1), Math.Max(door.X0, door.X1));
        if (door.X0 == door.X1 && door.Y0 != door.Y1) return (false, door.X0, Math.Min(door.Y0, door.Y1), Math.Max(door.Y0, door.Y1));
        return null;
    }

    static string? OptionProblem(FacadeIrOptions options, bool walls)
    {
        if (!(double.IsFinite(options.EntranceClearanceMetres) && options.EntranceClearanceMetres >= 0)) return "entrance clearance must be finite and not negative";
        if (options.Provenance is { } given && (string.IsNullOrWhiteSpace(given.Source) || string.IsNullOrWhiteSpace(given.Approval))) return "provenance source and approval are required";
        if (walls && !(double.IsFinite(options.WallThicknessMetres) && options.WallThicknessMetres > 0)) return "wall thickness must be finite and positive";
        if (walls && !(double.IsFinite(options.WallHeightMetres) && options.WallHeightMetres > 0)) return "wall height must be finite and positive";
        return null;
    }

    // A null check that does not change the nullable flow state (inputs are annotated non-null but may come from JSON).
    static bool IsNull(object? value) => value is null;

    static PlanConstraint Refusal(string code, string subject, string detail) => new(code, subject, true, false, detail);

    // -0 and 0 are one quantity (as IntentCompiler).
    static double Z(double value) => value == 0 ? 0.0 : value;

    static string Num(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
