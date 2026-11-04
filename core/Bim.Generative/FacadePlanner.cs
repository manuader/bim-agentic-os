using System.Globalization;
using Bim.Core;
namespace Bim.Generative;

// Places facade windows on a feasible space plan for the illumination and ventilation ratio rules of a code skill pack.
//
// Rules: the pack is resolved on its own (as SkillRoomValidator does); its room-targeted rules on window_area_m2 or
// ventilation_area_m2 are the ratio rules. A room is matched by its name after SkillRoomValidator.NormalizeName (accents
// stripped, case-insensitive glob), as in P5-02 validation. A room no ratio rule matches is not required to have a
// window (the CABA pack has none for baths, kitchens, laundries, halls or corridors). A matched rule is applied only when
// it is ratio_min over area_m2 with a numeric threshold and no applies_if condition; otherwise the room fails with
// facade_rule_unsupported, it never passes silently. Several matched rules of one kind take the largest threshold.
//
// Geometry: the exterior parts of a room side are the parts not covered by a coincident side (1e-7 m) of another room of
// the same level, so they lie on the building outline and shared walls are never used. Each part offers its length less
// EndClearanceMetres at both ends, in whole millimetres (rounded down). The required width is the largest of the minimum
// width, illumination area / window height and ventilation area / (window height x openable fraction), rounded up to a
// whole millimetre. Parts are used in order of usable length (longest first), then side (south, east, north, west), then
// lower start coordinate; each holds one window centred on the part, of the width still missing (at most its usable
// length and at least the minimum width), until the required width is placed. A room with no exterior part fails with
// facade_no_exterior_edge; one whose parts cannot hold the width fails with facade_edge_too_short and gets no window.
// Rooms are processed in ordinal instance id order, so the result does not depend on the order of the plan's rooms.
public static class FacadePlanner
{
    public const string IlluminationProperty = "window_area_m2";
    public const string VentilationProperty = "ventilation_area_m2";
    public const string AreaProperty = "area_m2";
    const double Epsilon = 1e-7;
    // Slack (millimetres) of the whole-millimetre rounding, so 2.0000000000000004 m is 2000 mm, not 2001 mm.
    const double MillimetreSlack = 1e-6;

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
        refusals.AddRange(PlanRefusals(plan));
        if (refusals.Count > 0 || resolution is null || !resolution.Succeeded)
            return new FacadeLayout(FacadeStatus.Refused, plan.BriefName ?? "", plan.Seed, pack.Id ?? "", options, Array.Empty<FacadeRoom>(), Array.Empty<FacadeWindow>(), refusals);

        var ratioRules = resolution.Rules.Where(rule => rule.Rule.Target.RoomPattern is not null
            && rule.Rule.Check.Property is IlluminationProperty or VentilationProperty).ToArray();
        var rooms = new List<FacadeRoom>();
        var windows = new List<FacadeWindow>();
        var checks = new List<PlanConstraint>();
        var ordered = plan.Rooms.OrderBy(room => room.InstanceId, StringComparer.Ordinal).ToArray();
        foreach (var room in ordered)
            PlaceRoom(room, ordered, ratioRules, options, rooms, windows, checks);
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

    static void PlaceRoom(PlannedRoom room, IReadOnlyList<PlannedRoom> all, IReadOnlyList<SkillEffectiveRule> ratioRules, FacadeOptions options,
        List<FacadeRoom> rooms, List<FacadeWindow> windows, List<PlanConstraint> checks)
    {
        var name = room.Name ?? "";
        var rect = room.Rect;
        var area = rect.Width * rect.Depth;
        var normalized = SkillRoomValidator.NormalizeName(name);
        var matched = ratioRules.Where(rule => rule.Rule.Target.MatchesRoom(normalized)).ToArray();
        var illuminationRules = matched.Where(rule => rule.Rule.Check.Property == IlluminationProperty).OrderBy(rule => rule.QualifiedId, StringComparer.Ordinal).ToArray();
        var ventilationRules = matched.Where(rule => rule.Rule.Check.Property == VentilationProperty).OrderBy(rule => rule.QualifiedId, StringComparer.Ordinal).ToArray();
        var ruleIds = illuminationRules.Concat(ventilationRules).Select(rule => rule.QualifiedId).ToArray();
        var parts = ExteriorParts(room, all);
        var exterior = parts.Sum(part => part.End - part.Start);

        FacadeRoom Record(FacadeRoomStatus status, string? reason, double? illumination, double? ventilation, double litArea, double openArea, string detail) =>
            new(room.InstanceId, name, room.Level, status, reason, area, illumination, ventilation, illumination * area, ventilation * area,
                litArea, openArea, exterior, ruleIds, detail);

        if (matched.Length == 0)
        {
            var detail = "no illumination or ventilation ratio rule of the pack targets the room name '" + normalized + "'";
            rooms.Add(Record(FacadeRoomStatus.NotRequired, null, null, null, 0, 0, detail));
            checks.Add(new PlanConstraint(FacadeCodes.NotRequired, room.InstanceId, false, true, detail));
            return;
        }
        var unsupported = matched.Where(rule => Threshold(rule) is null).OrderBy(rule => rule.QualifiedId, StringComparer.Ordinal).ToArray();
        if (unsupported.Length > 0)
        {
            var detail = "rules " + string.Join(", ", unsupported.Select(rule => rule.QualifiedId))
                + " cannot size a window (only ratio_min over area_m2 with a numeric threshold and no applies_if condition is applied)";
            rooms.Add(Record(FacadeRoomStatus.Failed, FacadeCodes.RuleUnsupported, Max(illuminationRules), Max(ventilationRules), 0, 0, detail));
            checks.Add(new PlanConstraint(FacadeCodes.RuleUnsupported, room.InstanceId, true, false, detail));
            return;
        }
        var illuminationRatio = Max(illuminationRules);
        var ventilationRatio = Max(ventilationRules);
        var height = options.HeadHeightMetres - options.SillHeightMetres;
        var needed = Math.Max(options.MinWindowWidthMetres, Math.Max((illuminationRatio ?? 0) * area / height, (ventilationRatio ?? 0) * area / (height * options.OpenableFraction)));
        var neededMm = CeilingMm(needed);
        var minimumMm = CeilingMm(options.MinWindowWidthMetres);
        var required = "window width " + Format(neededMm / 1000.0) + " m (illumination " + Format((illuminationRatio ?? 0) * area) + " m2, ventilation "
            + Format((ventilationRatio ?? 0) * area) + " m2, window height " + Format(height) + " m, openable fraction " + Format(options.OpenableFraction) + ")";
        if (parts.Count == 0)
        {
            var detail = "no side of the room lies on the building outline; needs " + required;
            rooms.Add(Record(FacadeRoomStatus.Failed, FacadeCodes.NoExteriorEdge, illuminationRatio, ventilationRatio, 0, 0, detail));
            checks.Add(new PlanConstraint(FacadeCodes.NoExteriorEdge, room.InstanceId, true, false, detail));
            return;
        }
        var candidates = parts.Select(part => (Part: part, Usable: FloorMm(part.End - part.Start - 2 * options.EndClearanceMetres)))
            .OrderByDescending(candidate => candidate.Usable).ThenBy(candidate => (int)candidate.Part.Side).ThenBy(candidate => candidate.Part.Start).ToArray();
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
            var holds = candidates.Where(candidate => candidate.Usable >= minimumMm).Sum(candidate => candidate.Usable);
            var detail = "exterior parts of " + Format(exterior) + " m hold " + Format(holds / 1000.0) + " m of windows after " + Format(options.EndClearanceMetres)
                + " m end clearances and the " + Format(options.MinWindowWidthMetres) + " m minimum width; needs " + required;
            rooms.Add(Record(FacadeRoomStatus.Failed, FacadeCodes.EdgeTooShort, illuminationRatio, ventilationRatio, 0, 0, detail));
            checks.Add(new PlanConstraint(FacadeCodes.EdgeTooShort, room.InstanceId, true, false, detail));
            return;
        }
        double lit = 0, open = 0;
        for (var index = 0; index < chosen.Count; index++)
        {
            var (part, widthMm) = chosen[index];
            var width = widthMm / 1000.0;
            var start = part.Start + (part.End - part.Start - width) / 2;
            var end = start + width;
            var horizontal = part.Side is FacadeSide.South or FacadeSide.North;
            var windowArea = width * height;
            var ventilationArea = windowArea * options.OpenableFraction;
            lit += windowArea;
            open += ventilationArea;
            windows.Add(new FacadeWindow("window:" + room.InstanceId + "/" + Text(index + 1), room.InstanceId, room.Level, part.Side,
                horizontal ? start : part.Line, horizontal ? part.Line : start, horizontal ? end : part.Line, horizontal ? part.Line : end,
                width, options.SillHeightMetres, options.HeadHeightMetres, windowArea, ventilationArea, ruleIds));
        }
        var placed = Text(chosen.Count) + " window" + (chosen.Count == 1 ? "" : "s") + " (" + string.Join(", ", chosen.Select(item => SideName(item.Part.Side) + " " + Format(item.WidthMm / 1000.0) + " m")) + ")";
        rooms.Add(Record(FacadeRoomStatus.Compliant, null, illuminationRatio, ventilationRatio, lit, open, placed + " for " + required));
        if (illuminationRatio is { } illumination)
            checks.Add(new PlanConstraint(FacadeCodes.Illumination, room.InstanceId, true, true, "window area " + Format(lit) + " m2 / room area " + Format(area) + " m2 = "
                + Format(lit / area) + " >= " + Format(illumination) + " (" + string.Join(", ", illuminationRules.Select(rule => rule.QualifiedId)) + ")"));
        if (ventilationRatio is { } ventilation)
            checks.Add(new PlanConstraint(FacadeCodes.Ventilation, room.InstanceId, true, true, "ventilation area " + Format(open) + " m2 / room area " + Format(area) + " m2 = "
                + Format(open / area) + " >= " + Format(ventilation) + " (" + string.Join(", ", ventilationRules.Select(rule => rule.QualifiedId)) + ")"));
    }

    // A side part on the building outline: Line is the side's fixed coordinate, Start..End the range along it.
    readonly record struct ExteriorPart(FacadeSide Side, double Line, double Start, double End);

    static List<ExteriorPart> ExteriorParts(PlannedRoom room, IReadOnlyList<PlannedRoom> all)
    {
        var r = room.Rect;
        var parts = new List<ExteriorPart>();
        foreach (var side in new[] { FacadeSide.South, FacadeSide.East, FacadeSide.North, FacadeSide.West })
        {
            var horizontal = side is FacadeSide.South or FacadeSide.North;
            var line = side switch { FacadeSide.South => r.Y, FacadeSide.North => r.Top, FacadeSide.West => r.X, _ => r.Right };
            var (low, high) = horizontal ? (r.X, r.Right) : (r.Y, r.Top);
            var covered = new List<(double Start, double End)>();
            foreach (var other in all)
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
            foreach (var (start, end) in covered)
            {
                if (start - cursor > Epsilon) parts.Add(new ExteriorPart(side, line, cursor, start));
                cursor = Math.Max(cursor, end);
            }
            if (high - cursor > Epsilon) parts.Add(new ExteriorPart(side, line, cursor, high));
        }
        return parts;
    }

    // The ratio a rule requires, or null when the rule cannot size a window.
    static double? Threshold(SkillEffectiveRule rule)
    {
        var check = rule.Rule.Check;
        if (check.Op != SkillCheckOp.RatioMin || !string.Equals(check.Over, AreaProperty, StringComparison.Ordinal)) return null;
        if (SkillRoomValidator.ParseCitation(rule.Rule.Citation).AppliesIf is not null) return null;
        if (rule.Threshold is not { Kind: SkillValueKind.Number } threshold || !double.IsFinite(threshold.Number) || threshold.Number < 0) return null;
        return threshold.Number;
    }

    static double? Max(IReadOnlyList<SkillEffectiveRule> rules)
    {
        double? best = null;
        foreach (var rule in rules)
            if (Threshold(rule) is { } value && (best is null || value > best.Value)) best = value;
        return best;
    }

    static long CeilingMm(double metres) => (long)Math.Ceiling(metres * 1000 - MillimetreSlack);

    static long FloorMm(double metres) => metres <= 0 ? 0 : (long)Math.Floor(metres * 1000 + MillimetreSlack);

    static List<PlanConstraint> OptionRefusals(FacadeOptions options)
    {
        var refusals = new List<PlanConstraint>();
        if (!(double.IsFinite(options.SillHeightMetres) && options.SillHeightMetres >= 0))
            refusals.Add(Refusal(FacadeCodes.OptionsInvalid, "sill_height_m", "sill height " + Format(options.SillHeightMetres) + " m must be finite and >= 0"));
        if (!(double.IsFinite(options.HeadHeightMetres) && options.HeadHeightMetres > options.SillHeightMetres && options.HeadHeightMetres <= FacadeOptions.MaxHeadHeightMetres))
            refusals.Add(Refusal(FacadeCodes.OptionsInvalid, "head_height_m", "head height " + Format(options.HeadHeightMetres) + " m must be finite, above the sill and at most "
                + Format(FacadeOptions.MaxHeadHeightMetres) + " m"));
        if (!(double.IsFinite(options.EndClearanceMetres) && options.EndClearanceMetres >= 0 && options.EndClearanceMetres <= SpacePlanner.MaxFootprintMetres))
            refusals.Add(Refusal(FacadeCodes.OptionsInvalid, "end_clearance_m", "end clearance " + Format(options.EndClearanceMetres) + " m must be finite and >= 0"));
        if (!(double.IsFinite(options.OpenableFraction) && options.OpenableFraction > 0 && options.OpenableFraction <= 1))
            refusals.Add(Refusal(FacadeCodes.OptionsInvalid, "openable_fraction", "openable fraction " + Format(options.OpenableFraction) + " must be in (0, 1]"));
        if (!(double.IsFinite(options.MinWindowWidthMetres) && options.MinWindowWidthMetres > 0 && options.MinWindowWidthMetres <= SpacePlanner.MaxFootprintMetres))
            refusals.Add(Refusal(FacadeCodes.OptionsInvalid, "min_window_width_m", "minimum window width " + Format(options.MinWindowWidthMetres) + " m must be finite and positive"));
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
                && double.IsFinite(rect.Width * rect.Depth)))
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
