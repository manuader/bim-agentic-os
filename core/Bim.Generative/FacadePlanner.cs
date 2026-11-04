using System.Globalization;
using Bim.Core;
namespace Bim.Generative;

// Places facade windows on a feasible space plan for the illumination and ventilation ratio rules of a code skill pack.
//
// Rules: the pack is resolved on its own (as SkillRoomValidator does); its room-targeted rules on window_area_m2 or
// ventilation_area_m2 are the ratio rules. A room is matched by its name after SkillRoomValidator.NormalizeName (accents
// stripped, case-insensitive glob), as in P5-02 validation. A room no ratio rule matches is not required to have a
// window (the CABA pack has none for baths, kitchens, laundries, halls or corridors). A matched rule is applied when it is
// ratio_min over area_m2 with a numeric threshold (a parameter threshold takes the run-time override when given, keyed
// "<skill id>/<parameter>" as in SkillRoomValidator). An applies_if condition on area_m2 or min_side_m (known from the
// rectangle) is evaluated as P5-02 does: when it holds the rule applies, when the room exceeds it the rule is unsupported.
// Any other rule (another op or denominator, another condition property, a non-numeric threshold) fails the room with
// facade_rule_unsupported; it never passes silently. Several matched rules of one kind take the largest threshold.
//
// Geometry: the exterior parts of a room side are the parts not covered by a coincident side (1e-7 m) of another room of
// the same level, so they lie on the building outline and shared walls are never used. A part on a footprint edge line
// that FacadeOptions.OpeningEdges does not list is a party wall and takes no window. A part facing a free region that the
// level's rooms enclose (a courtyard or hole) is an internal void; its windows say so. Each open part offers its length
// less EndClearanceMetres at both ends, in whole millimetres (rounded down). The required width is the smallest whole
// number of millimetres, at least the minimum width, whose window area (width x height) meets the illumination area and
// whose openable share (x openable fraction) meets the ventilation area; a requirement beyond the room's usable length is
// facade_edge_too_short without further sizing, so no width ever overflows. Parts are used in order of usable length
// (longest first), then side (south, east, north, west), then lower start coordinate; each holds one window centred on
// the part, of the width still missing (at most its usable length and at least the minimum width), until the required
// width is placed. The width test and the check of the placed windows against both ratios use a relative slack of 1e-12; a room passes only
// when they meet them. Rooms are processed in ordinal instance id order, so the result does not depend on room order.
public static class FacadePlanner
{
    public const string IlluminationProperty = "window_area_m2";
    public const string VentilationProperty = "ventilation_area_m2";
    public const string AreaProperty = "area_m2";
    public const string MinSideProperty = "min_side_m";
    const double Epsilon = 1e-7;
    // Relative slack of the after-placement ratio check (summing window areas may lose a few ulps).
    const double RatioSlack = 1e-12;

    public static FacadeLayout Place(SpacePlan plan, SkillManifest pack, FacadeOptions? options = null, IReadOnlyDictionary<string, double>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(pack);
        options ??= new FacadeOptions();
        var refusals = OptionRefusals(options);
        SkillResolution? resolution = null;
        try
        {
            resolution = SkillResolver.Resolve(new SkillCatalog(new[] { pack }), new[] { new SkillRequest(pack.Id) });
            if (!resolution.Succeeded)
                refusals.Add(Refusal(FacadeCodes.PackInvalid, pack.Id, "the pack does not resolve on its own: " + string.Join("; ", resolution.Issues.Select(issue => issue.Code + "@" + issue.Subject))));
        }
        catch (SkillException error)
        {
            refusals.Add(Refusal(FacadeCodes.PackInvalid, pack.Id ?? "", error.Message));
        }
        var overrides = new Dictionary<string, double>(StringComparer.Ordinal);
        if (resolution is { Succeeded: true }) refusals.AddRange(OverrideRefusals(resolution, parameters, overrides));
        refusals.AddRange(PlanRefusals(plan));
        if (refusals.Count > 0 || resolution is null || !resolution.Succeeded)
            return new FacadeLayout(FacadeStatus.Refused, plan.BriefName ?? "", plan.Seed, pack.Id ?? "", options, Array.Empty<FacadeRoom>(), Array.Empty<FacadeWindow>(), refusals);

        var ratioRules = resolution.Rules.Where(rule => rule.Rule.Target.RoomPattern is not null
            && rule.Rule.Check.Property is IlluminationProperty or VentilationProperty).ToArray();
        var rooms = new List<FacadeRoom>();
        var windows = new List<FacadeWindow>();
        var checks = new List<PlanConstraint>();
        var ordered = plan.Rooms.OrderBy(room => room.InstanceId, StringComparer.Ordinal).ToArray();
        var footprint = plan.Footprint is { } given && double.IsFinite(given.WidthMetres) && double.IsFinite(given.DepthMetres)
            ? given : new PlanFootprint(ordered.Max(room => room.Rect.Right), ordered.Max(room => room.Rect.Top));
        var voids = ordered.Select(room => room.Level).Distinct().ToDictionary(level => level, level => new VoidMap(ordered.Where(room => room.Level == level).ToArray()));
        var context = new Context(ordered, footprint, voids, ratioRules, overrides, options);
        foreach (var room in ordered)
            PlaceRoom(room, context, rooms, windows, checks);
        var status = rooms.Any(room => room.Status == FacadeRoomStatus.Failed) ? FacadeStatus.Incomplete : FacadeStatus.Complete;
        return new FacadeLayout(status, plan.BriefName ?? "", plan.Seed, pack.Id ?? "", options, rooms, windows, checks);
    }

    // Room facts of the layout for SkillRoomValidator (P5-02): area, window (illumination) area and ventilation area of
    // every room, in layout order. Shortest side and clear height are not carried, so rules on them are not evaluable.
    public static IReadOnlyList<RoomFacts> ToRoomFacts(FacadeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return layout.Rooms.Select(room => new RoomFacts(room.Instance, room.Name, room.AreaSquareMetres,
            windowAreaSquareMetres: room.WindowAreaSquareMetres, ventilationAreaSquareMetres: room.VentilationAreaSquareMetres)).ToArray();
    }

    sealed record Context(IReadOnlyList<PlannedRoom> Rooms, PlanFootprint Footprint, IReadOnlyDictionary<int, VoidMap> Voids, IReadOnlyList<SkillEffectiveRule> RatioRules,
        IReadOnlyDictionary<string, double> Overrides, FacadeOptions Options);

    static void PlaceRoom(PlannedRoom room, Context context, List<FacadeRoom> rooms, List<FacadeWindow> windows, List<PlanConstraint> checks)
    {
        var options = context.Options;
        var name = room.Name ?? "";
        var rect = room.Rect;
        var area = rect.Width * rect.Depth;
        var minSide = Math.Min(rect.Width, rect.Depth);
        var normalized = SkillRoomValidator.NormalizeName(name);
        var matched = context.RatioRules.Where(rule => rule.Rule.Target.MatchesRoom(normalized)).ToArray();
        var illuminationRules = matched.Where(rule => rule.Rule.Check.Property == IlluminationProperty).OrderBy(rule => rule.QualifiedId, StringComparer.Ordinal).ToArray();
        var ventilationRules = matched.Where(rule => rule.Rule.Check.Property == VentilationProperty).OrderBy(rule => rule.QualifiedId, StringComparer.Ordinal).ToArray();
        var ruleIds = illuminationRules.Concat(ventilationRules).Select(rule => rule.QualifiedId).ToArray();
        var parts = ExteriorParts(room, context);
        var exterior = parts.Sum(part => part.End - part.Start);
        double? Ratio(IReadOnlyList<SkillEffectiveRule> rules)
        {
            double? best = null;
            foreach (var rule in rules)
            {
                if (Threshold(rule, area, minSide, context.Overrides) is not { } value) return null;
                if (best is null || value > best.Value) best = value;
            }
            return best;
        }
        var illuminationRatio = Ratio(illuminationRules);
        var ventilationRatio = Ratio(ventilationRules);

        FacadeRoom Record(FacadeRoomStatus status, string? reason, double litArea, double openArea, string detail) =>
            new(room.InstanceId, name, room.Level, status, reason, area, illuminationRatio, ventilationRatio, illuminationRatio * area, ventilationRatio * area,
                litArea, openArea, exterior, ruleIds, detail);
        void Fail(string code, string detail)
        {
            rooms.Add(Record(FacadeRoomStatus.Failed, code, 0, 0, detail));
            checks.Add(new PlanConstraint(code, room.InstanceId, true, false, detail));
        }

        if (matched.Length == 0)
        {
            var detail = "no illumination or ventilation ratio rule of the pack targets the room name '" + normalized + "'";
            rooms.Add(Record(FacadeRoomStatus.NotRequired, null, 0, 0, detail));
            checks.Add(new PlanConstraint(FacadeCodes.NotRequired, room.InstanceId, false, true, detail));
            return;
        }
        var unsupported = matched.Where(rule => Threshold(rule, area, minSide, context.Overrides) is null).OrderBy(rule => rule.QualifiedId, StringComparer.Ordinal).ToArray();
        if (unsupported.Length > 0)
        {
            Fail(FacadeCodes.RuleUnsupported, "rules " + string.Join(", ", unsupported.Select(rule => rule.QualifiedId))
                + " cannot size a window for this room (only ratio_min over area_m2 with a numeric threshold, and an applies_if on area_m2 or min_side_m that the room meets, is applied)");
            return;
        }
        var height = options.HeadHeightMetres - options.SillHeightMetres;
        var fraction = options.OpenableFraction;
        var lightNeed = (illuminationRatio ?? 0) * area;
        var airNeed = (ventilationRatio ?? 0) * area;
        var needed = Math.Max(options.MinWindowWidthMetres, Math.Max(lightNeed / height, airNeed / (height * fraction)));
        var requirement = "illumination " + Format(lightNeed) + " m2 and ventilation " + Format(airNeed) + " m2 with a " + Format(height)
            + " m tall window and openable fraction " + Format(fraction) + " need " + Format(needed) + " m of window";
        if (parts.Count == 0)
        {
            Fail(FacadeCodes.NoExteriorEdge, "no side of the room lies on the building outline; " + requirement);
            return;
        }
        var open = parts.Where(part => !part.PartyWall).ToArray();
        if (open.Length == 0)
        {
            Fail(FacadeCodes.LotLineOnly, "every exterior side of the room (" + Format(exterior) + " m) lies on a closed lot edge (party wall); " + requirement);
            return;
        }
        var minimumMm = (long)Math.Ceiling(options.MinWindowWidthMetres * 1000);
        var candidates = open.Select(part => (Part: part, Usable: FloorMm(part.End - part.Start - 2 * options.EndClearanceMetres)))
            .OrderByDescending(candidate => candidate.Usable).ThenBy(candidate => (int)candidate.Part.Side).ThenBy(candidate => candidate.Part.Start).ToArray();
        var holdsMm = candidates.Where(candidate => candidate.Usable >= minimumMm).Sum(candidate => candidate.Usable);
        string TooShort() => "open exterior parts of " + Format(open.Sum(part => part.End - part.Start)) + " m hold " + Format(holdsMm / 1000.0) + " m of windows after "
            + Format(options.EndClearanceMetres) + " m end clearances and the " + Format(options.MinWindowWidthMetres) + " m minimum width; " + requirement;
        // Bound before any millimetre arithmetic: a requirement beyond every usable millimetre cannot be placed.
        if (!double.IsFinite(needed) || needed * 1000 > holdsMm + 1)
        {
            Fail(FacadeCodes.EdgeTooShort, TooShort());
            return;
        }
        // Same relative slack as the after-placement check, so a decimal threshold stored as a double (0.1 is 0.1000000000000000055)
        // does not add a millimetre to an exact width (24 m2 / 10 with a 1.0 m window is 2.400 m, not 2.401 m).
        bool Meets(long millimetres) => millimetres / 1000.0 * height >= lightNeed * (1 - RatioSlack) && millimetres / 1000.0 * height * fraction >= airNeed * (1 - RatioSlack);
        var neededMm = Math.Max(minimumMm, (long)Math.Ceiling(needed * 1000));
        while (neededMm > minimumMm && Meets(neededMm - 1)) neededMm--;
        while (!Meets(neededMm)) neededMm++;
        var chosen = new List<(ExteriorPart Part, long WidthMm)>();
        var remaining = neededMm;
        foreach (var (part, usable) in candidates)
        {
            if (remaining <= 0) break;
            if (usable < minimumMm) continue;
            var width = Math.Max(minimumMm, Math.Min(usable, remaining));
            chosen.Add((part, width));
            remaining -= width;
        }
        if (remaining > 0)
        {
            Fail(FacadeCodes.EdgeTooShort, TooShort());
            return;
        }
        double lit = 0, air = 0;
        var placedWindows = new List<FacadeWindow>();
        for (var index = 0; index < chosen.Count; index++)
        {
            var (part, widthMm) = chosen[index];
            var width = widthMm / 1000.0;
            var start = part.Start + (part.End - part.Start - width) / 2;
            var end = start + width;
            var horizontal = part.Side is FacadeSide.South or FacadeSide.North;
            var windowArea = width * height;
            var ventilationArea = windowArea * fraction;
            lit += windowArea;
            air += ventilationArea;
            placedWindows.Add(new FacadeWindow("window:" + room.InstanceId + "/" + Text(index + 1), room.InstanceId, room.Level, part.Side,
                horizontal ? start : part.Line, horizontal ? part.Line : start, horizontal ? end : part.Line, horizontal ? part.Line : end,
                width, options.SillHeightMetres, options.HeadHeightMetres, windowArea, ventilationArea, ruleIds, part.FacesVoid));
        }
        var lightMet = illuminationRatio is not { } i || lit / area >= i * (1 - RatioSlack);
        var airMet = ventilationRatio is not { } v || air / area >= v * (1 - RatioSlack);
        if (!lightMet || !airMet)
        {
            Fail(FacadeCodes.RatioUnmet, "placed windows give " + Format(lit / area) + " illumination and " + Format(air / area) + " ventilation over the room area; " + requirement);
            return;
        }
        windows.AddRange(placedWindows);
        var placed = Text(chosen.Count) + " window" + (chosen.Count == 1 ? "" : "s") + " ("
            + string.Join(", ", chosen.Select(item => SideName(item.Part.Side) + " " + Format(item.WidthMm / 1000.0) + " m" + (item.Part.FacesVoid ? " on an internal void" : ""))) + ")";
        rooms.Add(Record(FacadeRoomStatus.Compliant, null, lit, air, placed + "; " + requirement));
        if (illuminationRatio is { } illumination)
            checks.Add(new PlanConstraint(FacadeCodes.Illumination, room.InstanceId, true, lightMet, "window area " + Format(lit) + " m2 / room area " + Format(area) + " m2 = "
                + Format(lit / area) + " >= " + Format(illumination) + " (" + string.Join(", ", illuminationRules.Select(rule => rule.QualifiedId)) + ")"));
        if (ventilationRatio is { } ventilation)
            checks.Add(new PlanConstraint(FacadeCodes.Ventilation, room.InstanceId, true, airMet, "ventilation area " + Format(air) + " m2 / room area " + Format(area) + " m2 = "
                + Format(air / area) + " >= " + Format(ventilation) + " (" + string.Join(", ", ventilationRules.Select(rule => rule.QualifiedId)) + ")"));
    }

    // A side part on the building outline: Line is the side's fixed coordinate, Start..End the range along it. PartyWall:
    // on a closed footprint edge; FacesVoid: the free region in front of it is enclosed by the level's rooms.
    readonly record struct ExteriorPart(FacadeSide Side, double Line, double Start, double End, bool PartyWall, bool FacesVoid);

    static List<ExteriorPart> ExteriorParts(PlannedRoom room, Context context)
    {
        var r = room.Rect;
        var open = context.Options.OpeningEdges ?? FacadeOptions.DefaultOpeningEdges;
        var voids = context.Voids[room.Level];
        var parts = new List<ExteriorPart>();
        foreach (var side in new[] { FacadeSide.South, FacadeSide.East, FacadeSide.North, FacadeSide.West })
        {
            var horizontal = side is FacadeSide.South or FacadeSide.North;
            var line = side switch { FacadeSide.South => r.Y, FacadeSide.North => r.Top, FacadeSide.West => r.X, _ => r.Right };
            var (edge, edgeLine) = side switch
            {
                FacadeSide.South => (LotEdge.South, 0.0),
                FacadeSide.North => (LotEdge.North, context.Footprint.DepthMetres),
                FacadeSide.West => (LotEdge.West, 0.0),
                _ => (LotEdge.East, context.Footprint.WidthMetres),
            };
            var party = Math.Abs(line - edgeLine) <= Epsilon && !open.Contains(edge);
            var (low, high) = horizontal ? (r.X, r.Right) : (r.Y, r.Top);
            var covered = new List<(double Start, double End)>();
            foreach (var other in context.Rooms)
            {
                if (ReferenceEquals(other, room) || other.Level != room.Level) continue;
                var o = other.Rect;
                var facing = side switch { FacadeSide.South => o.Top, FacadeSide.North => o.Y, FacadeSide.West => o.Right, _ => o.X };
                if (Math.Abs(facing - line) > Epsilon) continue;
                var (start, end) = horizontal ? (Math.Max(o.X, low), Math.Min(o.Right, high)) : (Math.Max(o.Y, low), Math.Min(o.Top, high));
                if (end - start > Epsilon) covered.Add((start, end));
            }
            covered.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
            var cursor = low;
            void Add(double start, double end) => parts.Add(new ExteriorPart(side, line, start, end, party, voids.Faces(side, line, (start + end) / 2)));
            foreach (var (start, end) in covered)
            {
                if (start - cursor > Epsilon) Add(cursor, start);
                cursor = Math.Max(cursor, end);
            }
            if (high - cursor > Epsilon) Add(cursor, high);
        }
        return parts;
    }

    // Free cells of the grid made by one level's room coordinates (plus a frame around them) that cannot be reached from the
    // frame are internal voids. An exterior part never has a room cell in front of it, so the cell in front of its midpoint
    // tells the region of the whole part.
    sealed class VoidMap
    {
        readonly double[] xs, ys;
        readonly bool[,] enclosed;

        public VoidMap(IReadOnlyList<PlannedRoom> rooms)
        {
            xs = rooms.SelectMany(room => new[] { room.Rect.X, room.Rect.Right }).Append(rooms.Min(room => room.Rect.X) - 1).Append(rooms.Max(room => room.Rect.Right) + 1)
                .Distinct().OrderBy(value => value).ToArray();
            ys = rooms.SelectMany(room => new[] { room.Rect.Y, room.Rect.Top }).Append(rooms.Min(room => room.Rect.Y) - 1).Append(rooms.Max(room => room.Rect.Top) + 1)
                .Distinct().OrderBy(value => value).ToArray();
            int nx = xs.Length - 1, ny = ys.Length - 1;
            var occupied = new bool[nx, ny];
            foreach (var room in rooms)
            {
                var (i0, i1) = (Array.BinarySearch(xs, room.Rect.X), Array.BinarySearch(xs, room.Rect.Right));
                var (j0, j1) = (Array.BinarySearch(ys, room.Rect.Y), Array.BinarySearch(ys, room.Rect.Top));
                for (var i = i0; i < i1; i++)
                    for (var j = j0; j < j1; j++) occupied[i, j] = true;
            }
            var reached = new bool[nx, ny];
            var queue = new Queue<(int I, int J)>();
            for (var i = 0; i < nx; i++)
                for (var j = 0; j < ny; j++)
                    if ((i == 0 || j == 0 || i == nx - 1 || j == ny - 1) && !occupied[i, j]) { reached[i, j] = true; queue.Enqueue((i, j)); }
            while (queue.Count > 0)
            {
                var (i, j) = queue.Dequeue();
                foreach (var (a, b) in new[] { (i - 1, j), (i + 1, j), (i, j - 1), (i, j + 1) })
                    if (a >= 0 && b >= 0 && a < nx && b < ny && !occupied[a, b] && !reached[a, b]) { reached[a, b] = true; queue.Enqueue((a, b)); }
            }
            enclosed = new bool[nx, ny];
            for (var i = 0; i < nx; i++)
                for (var j = 0; j < ny; j++) enclosed[i, j] = !occupied[i, j] && !reached[i, j];
        }

        public bool Faces(FacadeSide side, double line, double along)
        {
            var horizontal = side is FacadeSide.South or FacadeSide.North;
            var lines = horizontal ? ys : xs;
            var cells = horizontal ? xs : ys;
            var at = Array.BinarySearch(lines, line);
            if (at < 0) return false;
            var across = side is FacadeSide.South or FacadeSide.West ? at - 1 : at;
            var cell = Array.BinarySearch(cells, along);
            cell = cell >= 0 ? cell : ~cell - 1;
            if (across < 0 || cell < 0 || across >= lines.Length - 1 || cell >= cells.Length - 1) return false;
            return horizontal ? enclosed[cell, across] : enclosed[across, cell];
        }
    }

    // The ratio a rule requires for this room, or null when the rule cannot size a window for it.
    static double? Threshold(SkillEffectiveRule rule, double area, double minSide, IReadOnlyDictionary<string, double> overrides)
    {
        var check = rule.Rule.Check;
        if (check.Op != SkillCheckOp.RatioMin || !string.Equals(check.Over, AreaProperty, StringComparison.Ordinal)) return null;
        if (SkillRoomValidator.ParseCitation(rule.Rule.Citation).AppliesIf is { } condition)
        {
            if (!SkillRoomValidator.TryParseCondition(condition, out var property, out var maximum)) return null;
            double? value = property switch { AreaProperty => area, MinSideProperty => minSide, _ => null };
            if (value is not { } known || known > maximum + SkillRoomValidator.RelativeTolerance * Math.Max(1, Math.Abs(maximum))) return null;
        }
        if (rule.Threshold is not { Kind: SkillValueKind.Number } threshold) return null;
        var limit = threshold.Number;
        if (rule.ThresholdSource is { Origin: SkillTrace.ParameterOrigin } source && overrides.TryGetValue(source.SkillId + "/" + source.Key, out var overridden)) limit = overridden;
        return double.IsFinite(limit) && limit >= 0 ? limit : null;
    }

    static long FloorMm(double metres) => metres <= 0 ? 0 : (long)Math.Floor(metres * 1000 + 1e-6);

    // Run-time parameter overrides, validated as SkillRoomValidator does: "<skill id>/<parameter>" naming a numeric
    // parameter of the resolved pack, finite, within its [min, max] and integral for integer parameters.
    static List<PlanConstraint> OverrideRefusals(SkillResolution resolution, IReadOnlyDictionary<string, double>? parameters, Dictionary<string, double> overrides)
    {
        var refusals = new List<PlanConstraint>();
        if (parameters is null) return refusals;
        foreach (var (key, value) in parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var slash = key.IndexOf('/', StringComparison.Ordinal);
            var skill = slash <= 0 ? null : resolution.Skills.FirstOrDefault(candidate => string.Equals(candidate.Id, key[..slash], StringComparison.Ordinal));
            var declared = slash <= 0 ? null : skill?.Parameters.FirstOrDefault(candidate => string.Equals(candidate.Name, key[(slash + 1)..], StringComparison.Ordinal));
            if (declared is null) { refusals.Add(Refusal(FacadeCodes.OptionsInvalid, key, "parameter override names no parameter of the pack (expected <skill id>/<parameter>)")); continue; }
            if (declared.Type is not (SkillParameterType.Number or SkillParameterType.Integer) || !double.IsFinite(value) || (declared.Min is { } min && value < min)
                || (declared.Max is { } max && value > max) || (declared.Type == SkillParameterType.Integer && Math.Floor(value) != value))
            {
                refusals.Add(Refusal(FacadeCodes.OptionsInvalid, key, "parameter override " + Format(value) + " is not numeric within the declared range"));
                continue;
            }
            overrides[key] = value == 0 ? 0.0 : value;
        }
        return refusals;
    }

    static List<PlanConstraint> OptionRefusals(FacadeOptions options)
    {
        var refusals = new List<PlanConstraint>();
        if (!(double.IsFinite(options.SillHeightMetres) && options.SillHeightMetres >= 0))
            refusals.Add(Refusal(FacadeCodes.OptionsInvalid, "sill_height_m", "sill height " + Format(options.SillHeightMetres) + " m must be finite and >= 0"));
        if (!(double.IsFinite(options.HeadHeightMetres) && options.HeadHeightMetres - options.SillHeightMetres >= FacadeOptions.MinWindowHeightMetres
            && options.HeadHeightMetres <= FacadeOptions.MaxHeadHeightMetres))
            refusals.Add(Refusal(FacadeCodes.OptionsInvalid, "head_height_m", "head height " + Format(options.HeadHeightMetres) + " m must be finite, at least "
                + Format(FacadeOptions.MinWindowHeightMetres) + " m above the sill and at most " + Format(FacadeOptions.MaxHeadHeightMetres) + " m"));
        if (!(double.IsFinite(options.EndClearanceMetres) && options.EndClearanceMetres >= 0 && options.EndClearanceMetres <= SpacePlanner.MaxFootprintMetres))
            refusals.Add(Refusal(FacadeCodes.OptionsInvalid, "end_clearance_m", "end clearance " + Format(options.EndClearanceMetres) + " m must be finite and >= 0"));
        if (!(double.IsFinite(options.OpenableFraction) && options.OpenableFraction >= FacadeOptions.MinOpenableFraction && options.OpenableFraction <= 1))
            refusals.Add(Refusal(FacadeCodes.OptionsInvalid, "openable_fraction", "openable fraction " + Format(options.OpenableFraction) + " must be in ["
                + Format(FacadeOptions.MinOpenableFraction) + ", 1]"));
        if (!(double.IsFinite(options.MinWindowWidthMetres) && options.MinWindowWidthMetres > 0 && options.MinWindowWidthMetres <= SpacePlanner.MaxFootprintMetres))
            refusals.Add(Refusal(FacadeCodes.OptionsInvalid, "min_window_width_m", "minimum window width " + Format(options.MinWindowWidthMetres) + " m must be finite and positive"));
        if (options.OpeningEdges is { } edges && (edges.Any(edge => !Enum.IsDefined(edge)) || edges.Distinct().Count() != edges.Count))
            refusals.Add(Refusal(FacadeCodes.OptionsInvalid, "opening_edges", "opening edges must be distinct lot edges (south, east, north, west)"));
        return refusals;
    }

    static List<PlanConstraint> PlanRefusals(SpacePlan plan)
    {
        var refusals = new List<PlanConstraint>();
        if (!plan.IsFeasible || plan.Rooms is null || plan.Rooms.Count == 0)
        {
            refusals.Add(Refusal(FacadeCodes.PlanInvalid, "plan", plan.IsFeasible ? "a feasible plan without rooms has no facade" : "an infeasible plan has no facade"));
            return refusals;
        }
        foreach (var room in plan.Rooms)
        {
            if (room is null) { refusals.Add(Refusal(FacadeCodes.PlanInvalid, "plan", "the plan lists a null room")); continue; }
            var rect = room.Rect;
            if (room.InstanceId is null)
                refusals.Add(Refusal(FacadeCodes.PlanInvalid, "plan", "a room has no instance id"));
            if (rect is null || !(double.IsFinite(rect.X) && double.IsFinite(rect.Y) && double.IsFinite(rect.Width) && rect.Width > 0 && double.IsFinite(rect.Depth) && rect.Depth > 0
                && double.IsFinite(rect.Width * rect.Depth) && double.IsFinite(rect.Right) && double.IsFinite(rect.Top)))
                refusals.Add(Refusal(FacadeCodes.PlanInvalid, room.InstanceId ?? "", "room rectangle must be finite with positive width and depth"));
        }
        if (refusals.Count > 0) return refusals;
        foreach (var duplicate in plan.Rooms.GroupBy(room => room.InstanceId, StringComparer.Ordinal).Where(group => group.Count() > 1).OrderBy(group => group.Key, StringComparer.Ordinal))
            refusals.Add(Refusal(FacadeCodes.PlanInvalid, duplicate.Key, "instance id given " + Text(duplicate.Count()) + " times"));
        var ordered = plan.Rooms.OrderBy(room => room.InstanceId, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < ordered.Length; i++)
            for (var j = i + 1; j < ordered.Length; j++)
            {
                if (ordered[i].Level != ordered[j].Level) continue;
                var (a, b) = (ordered[i].Rect, ordered[j].Rect);
                var width = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
                var depth = Math.Min(a.Top, b.Top) - Math.Max(a.Y, b.Y);
                if (width > Epsilon && depth > Epsilon)
                    refusals.Add(Refusal(FacadeCodes.PlanInvalid, ordered[i].InstanceId + "/" + ordered[j].InstanceId, "rooms overlap by " + Format(width) + " x " + Format(depth) + " m"));
            }
        return refusals;
    }

    static PlanConstraint Refusal(string code, string subject, string detail) => new(code, subject, true, false, detail);

    internal static string SideName(FacadeSide side) => side switch
    {
        FacadeSide.South => "south",
        FacadeSide.East => "east",
        FacadeSide.North => "north",
        _ => "west",
    };

    static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
