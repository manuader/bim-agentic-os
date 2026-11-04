using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bim.Core;
using Bim.Generative;

// P7-05 facade openings oracle: windows only on room sides that lie on the building outline (never on a wall shared with
// another room of the level), inside the exterior part less the end clearances, sized so every habitable room meets the
// CABA pack illumination (window_area_m2 / area_m2 >= 1/10) and ventilation (ventilation_area_m2 / area_m2 >= 1/30)
// ratio rules, each window citing the rule ids it is sized for. Exact hand-computed windows on crafted plans (single
// window, a tie between sides, two windows on two short sides, a minimum width bump, ventilation governing, sill/head
// options, an upper level), typed failures (interior room, exterior part too short, unsupported rule), wet and other
// non-habitable rooms skipped, refusals, a P5-02 SkillRoomValidator cross-check, and an independent geometric oracle
// over ranked 1/2/3-bedroom prototypes that samples the room sides itself and reads the thresholds from the pack JSON.
// Canonical determinism across runs, room orders and cultures. Unit-tested evidence only.
static class FacadeTests
{
    const double Tolerance = 1e-9;
    // Sampling and whole-millimetre rounding slack of the oracle's usable-length bounds (metres).
    const double Margin = 3e-3;
    const string PackId = "ar.caba.codigo-edificacion";

    sealed record OracleRule(string QualifiedId, string Pattern, string Property, double Value);

    public static void Run(Action<bool, string, string> check, string root)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "facade: " + name, detail);
        var packPath = Path.Combine(root, "skills", PackId, "skill.json");
        var packJson = File.ReadAllText(packPath);
        var caba = SkillParser.Parse(packJson);
        var rules = OracleRules(packJson);
        var defaults = new FacadeOptions();
        void Refused(string label, FacadeLayout refused, string code) =>
            Check(refused.Status == FacadeStatus.Refused && refused.Rooms.Count == 0 && refused.Windows.Count == 0 && refused.Checks.Count > 0
                && refused.Checks.All(c => c.Hard && !c.Satisfied) && refused.Checks.Any(c => c.Code == code && c.Detail.Length > 0), "refuses " + label, Describe(refused));

        // ---- the pack values the windows are sized for (read from the JSON, not through the planner) ----
        Check(rules.Count(r => r.Property == "window_area_m2") >= 10 && rules.Where(r => r.Property == "window_area_m2").All(r => r.Value == 0.1),
            "every pack illumination ratio rule is x = 10 (window area >= room area / 10)", Text(rules.Count));
        Check(rules.Where(r => r.Property == "ventilation_area_m2").All(r => Math.Abs(r.Value - 1.0 / 30) < 1e-15)
            && rules.Count(r => r.Property == "ventilation_area_m2") == rules.Count(r => r.Property == "window_area_m2"),
            "every pack ventilation ratio rule is k = i/3 (ventilation area >= room area / 30), one per illumination rule");
        Check(defaults.SillHeightMetres == 0.9 && defaults.HeadHeightMetres == 2.1 && defaults.EndClearanceMetres == 0.3 && defaults.OpenableFraction == 0.5
            && defaults.MinWindowWidthMetres == 0.6, "documented defaults: sill 0.90 m, head 2.10 m, end clearance 0.30 m, openable 0.5, minimum width 0.60 m");

        // ---- flat: living and bedroom on the south facade, wet and service rooms behind ----
        var flatRooms = new[]
        {
            P("living", "Living", 0, 0, 6, 4), P("bedroom_1", "Bedroom 1", 6, 0, 4, 4), P("bathroom", "Bathroom", 0, 4, 3, 2), P("hall", "Hall", 3, 4, 3, 2),
            P("kitchen", "Kitchen", 6, 4, 4, 2),
        };
        var flat = Plan("flat", 3, 10, 6, flatRooms);
        var layout = FacadePlanner.Place(flat, caba);
        Check(layout.Status == FacadeStatus.Complete && layout.IsComplete && layout.Violations.Count == 0 && layout.BriefName == "flat" && layout.Seed == 3 && layout.PackId == PackId,
            "flat facade is complete", Describe(layout));
        Check(layout.Rooms.Select(r => r.Instance).SequenceEqual(new[] { "bathroom", "bedroom_1", "hall", "kitchen", "living" }), "rooms are in instance id order", Describe(layout));
        Check(layout.Windows.Select(w => w.Id).SequenceEqual(new[] { "window:bedroom_1/1", "window:living/1" }), "one window for each habitable room, in room order", Describe(layout));
        var living = layout.Windows.FirstOrDefault(w => w.Instance == "living");
        Check(living is { Side: FacadeSide.South, Level: 0 } && living.Y0 == 0 && living.Y1 == 0 && Near(living.X0, 2) && Near(living.X1, 4) && Near(living.WidthMetres, 2)
            && living.SillHeightMetres == 0.9 && living.HeadHeightMetres == 2.1 && Near(living.AreaSquareMetres, 2.4) && Near(living.VentilationAreaSquareMetres, 1.2),
            "living 24 m2: a 2.00 m window centred on the longest exterior side (south, 5.4 m usable), 2.4 m2 = 24 / 10", Describe(layout));
        Check(living is not null && living.RuleIds.SequenceEqual(new[] { PackId + "/living-illumination-ratio", PackId + "/living-ventilation-ratio" }),
            "the living window cites the living illumination and ventilation rule ids", living is null ? "" : string.Join(",", living.RuleIds));
        var bed = layout.Windows.FirstOrDefault(w => w.Instance == "bedroom_1");
        Check(bed is { Side: FacadeSide.South } && bed.Y0 == 0 && Near(bed.WidthMetres, 1.334) && Near(bed.X0, 7.333) && Near(bed.X1, 8.667)
            && bed.RuleIds.SequenceEqual(new[] { PackId + "/bedroom-illumination-ratio", PackId + "/bedroom-ventilation-ratio" }),
            "bedroom 16 m2: 1.6 / 1.2 m rounded up to 1.334 m, south wins the tie with east, centred", Describe(layout));
        Check(layout.Rooms.Where(r => r.Instance is "bathroom" or "hall" or "kitchen").All(r => r.Status == FacadeRoomStatus.NotRequired && r.Reason is null && r.RuleIds.Count == 0
            && r.WindowAreaSquareMetres == 0) && layout.Checks.Count(c => c.Code == FacadeCodes.NotRequired && !c.Hard && c.Satisfied) == 3,
            "bathroom (wet), hall and kitchen have no pack ratio rule: no window, soft facade_not_required", Describe(layout));
        Check(Room(layout, "living") is { Status: FacadeRoomStatus.Compliant, Reason: null } lr && lr.IlluminationRatio == 0.1 && Near(lr.VentilationRatio ?? 0, 1.0 / 30)
            && Near(lr.RequiredWindowAreaSquareMetres, 2.4) && Near(lr.RequiredVentilationAreaSquareMetres, 0.8) && Near(lr.WindowAreaSquareMetres, 2.4)
            && Near(lr.VentilationAreaSquareMetres, 1.2) && Near(lr.ExteriorLengthMetres, 10) && lr.AreaSquareMetres == 24,
            "living room record: ratios, required and achieved areas, 10 m of exterior sides (south and west)", Describe(layout));
        Check(layout.Checks.Any(c => c.Code == FacadeCodes.Illumination && c.Subject == "living" && c.Hard && c.Satisfied && c.Detail.Length > 0)
            && layout.Checks.Any(c => c.Code == FacadeCodes.Ventilation && c.Subject == "bedroom_1" && c.Hard && c.Satisfied), "compliant rooms carry satisfied illumination and ventilation checks");
        Check(Oracle(flat, layout, defaults, rules) is null, "flat facade agrees with the independent oracle", Oracle(flat, layout, defaults, rules) ?? "");

        // P5-02 cross-check: the room facts of the layout pass the pack's own ratio rules through SkillRoomValidator.
        var facts = FacadePlanner.ToRoomFacts(layout);
        var report = SkillRoomValidator.Validate(caba, facts);
        var ratioFindings = report.Findings.Where(f => f.Property is RoomFacts.WindowAreaKey or RoomFacts.VentilationAreaKey).ToArray();
        Check(facts.Count == 5 && ratioFindings.Length == 4 && ratioFindings.All(f => f.Outcome == RoomCheckOutcome.Pass && f.SourceConfidence == SkillRoomValidator.ConfidenceOwnerConfirmed),
            "SkillRoomValidator passes the four owner-confirmed ratio rules on the layout's room facts",
            string.Join("; ", report.Findings.Select(f => f.RoomId + " " + f.RuleId + " " + f.Outcome + " " + f.Detail)));

        // ---- an interior room gets no window and is reported ----
        var gridRooms = new List<PlannedRoom>();
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
            {
                var id = i == 1 && j == 1 ? "bedroom_centre" : i == 0 && j == 0 ? "bedroom_corner" : "hall_" + Text(i) + Text(j);
                var name = id == "bedroom_centre" ? "Bedroom centre" : id == "bedroom_corner" ? "Dormitorio esquina" : "Hall " + Text(i) + Text(j);
                gridRooms.Add(P(id, name, 3 * i, 3 * j, 3, 3));
            }
        var grid = Plan("grid", 1, 9, 9, gridRooms.ToArray());
        var gridLayout = FacadePlanner.Place(grid, caba);
        Check(gridLayout.Status == FacadeStatus.Incomplete && !gridLayout.IsComplete && Room(gridLayout, "bedroom_centre") is { Status: FacadeRoomStatus.Failed, Reason: FacadeCodes.NoExteriorEdge } centre
            && centre.ExteriorLengthMetres == 0 && centre.WindowAreaSquareMetres == 0 && gridLayout.Windows.All(w => w.Instance != "bedroom_centre"),
            "the centre bedroom of a 3 x 3 grid has no exterior side: facade_no_exterior_edge, no window", Describe(gridLayout));
        Check(gridLayout.Violations.Count == 1 && gridLayout.Violations[0] is { Code: FacadeCodes.NoExteriorEdge, Subject: "bedroom_centre", Hard: true } v && v.Detail.Length > 0,
            "the interior room is the only violation", Describe(gridLayout));
        Check(gridLayout.Windows.SingleOrDefault(w => w.Instance == "bedroom_corner") is { Side: FacadeSide.South } corner && corner.Y0 == 0 && Near(corner.WidthMetres, 0.75)
            && Near(corner.X0, 1.125) && corner.RuleIds.SequenceEqual(new[] { PackId + "/dormitorio-illumination-ratio", PackId + "/dormitorio-ventilation-ratio" }),
            "a Spanish-named corner bedroom (Dormitorio) of 9 m2 gets a 0.75 m window citing the dormitorio rules", Describe(gridLayout));
        Check(Oracle(grid, gridLayout, defaults, rules) is null, "grid facade agrees with the oracle", Oracle(grid, gridLayout, defaults, rules) ?? "");

        // ---- an exterior part shorter than the clearances plus the minimum width ----
        var notch = Plan("notch", 1, 9, 9, P("hall_w", "Hall w", 0, 0, 3, 8), P("bedroom_t", "Bedroom t", 3, 1, 3, 7), P("hall_e", "Hall e", 6, 0, 3, 8),
            P("hall_n", "Hall n", 3, 8, 3, 1), P("hall_s", "Hall s", 4, 0, 2, 1));
        var notchLayout = FacadePlanner.Place(notch, caba);
        Check(notchLayout.Status == FacadeStatus.Incomplete && Room(notchLayout, "bedroom_t") is { Status: FacadeRoomStatus.Failed, Reason: FacadeCodes.EdgeTooShort } t
            && Near(t.ExteriorLengthMetres, 1) && Near(t.RequiredWindowAreaSquareMetres, 2.1) && t.WindowAreaSquareMetres == 0 && notchLayout.Windows.Count == 0
            && notchLayout.Violations.Any(c => c.Code == FacadeCodes.EdgeTooShort && c.Subject == "bedroom_t" && c.Hard && c.Detail.Length > 0),
            "a bedroom whose only exterior part is 1 m (0.4 m usable) is facade_edge_too_short with no window", Describe(notchLayout));
        Check(Oracle(notch, notchLayout, defaults, rules) is null, "notch facade agrees with the oracle", Oracle(notch, notchLayout, defaults, rules) ?? "");

        // ---- two short exterior sides share the required width ----
        var slot = Plan("slot", 1, 5, 12, P("hall_w", "Hall w", 0, 0, 1, 12), P("living", "Living", 1, 0, 3, 12), P("hall_e", "Hall e", 4, 0, 1, 12));
        var slotLayout = FacadePlanner.Place(slot, caba);
        var slotWindows = slotLayout.Windows.Where(w => w.Instance == "living").ToArray();
        Check(slotLayout.IsComplete && slotWindows.Length == 2 && slotWindows[0] is { Id: "window:living/1", Side: FacadeSide.South } s1 && s1.Y0 == 0 && Near(s1.X0, 1.3) && Near(s1.X1, 3.7)
            && slotWindows[1] is { Id: "window:living/2", Side: FacadeSide.North } s2 && s2.Y0 == 12 && Near(s2.X0, 2.2) && Near(s2.X1, 2.8)
            && Near(Room(slotLayout, "living")?.WindowAreaSquareMetres ?? 0, 3.6),
            "a 3 x 12 m living needs 3.00 m: 2.40 m south (all its usable length) plus 0.60 m north", Describe(slotLayout));
        Check(Oracle(slot, slotLayout, defaults, rules) is null, "slot facade agrees with the oracle", Oracle(slot, slotLayout, defaults, rules) ?? "");
        var wideMin = defaults with { MinWindowWidthMetres = 0.7 };
        var bumped = FacadePlanner.Place(slot, caba, wideMin);
        Check(bumped.IsComplete && bumped.Windows.Count == 2 && Near(bumped.Windows[0].WidthMetres, 2.4) && Near(bumped.Windows[1].WidthMetres, 0.7) && Near(bumped.Windows[1].X0, 2.15),
            "the 0.60 m remainder is raised to a 0.70 m minimum width", Describe(bumped));
        Check(Oracle(slot, bumped, wideMin, rules) is null, "bumped facade agrees with the oracle", Oracle(slot, bumped, wideMin, rules) ?? "");

        // ---- options: ventilation governs with a small openable fraction; sill and head change the height ----
        var vent = defaults with { OpenableFraction = 0.25 };
        var ventLayout = FacadePlanner.Place(flat, caba, vent);
        Check(ventLayout.IsComplete && ventLayout.Windows.FirstOrDefault(w => w.Instance == "living") is { } vl && Near(vl.WidthMetres, 2.667) && Near(vl.X0, 1.6665)
            && Near(vl.VentilationAreaSquareMetres, 2.667 * 1.2 * 0.25) && ventLayout.Options == vent,
            "openable fraction 0.25: ventilation 0.8 m2 / 0.3 m2 per metre governs, 2.667 m", Describe(ventLayout));
        Check(Oracle(flat, ventLayout, vent, rules) is null, "ventilation-governed facade agrees with the oracle", Oracle(flat, ventLayout, vent, rules) ?? "");
        var tall = defaults with { SillHeightMetres = 1.0, HeadHeightMetres = 2.0 };
        var tallLayout = FacadePlanner.Place(flat, caba, tall);
        Check(tallLayout.IsComplete && tallLayout.Windows.FirstOrDefault(w => w.Instance == "living") is { SillHeightMetres: 1.0, HeadHeightMetres: 2.0 } tl && Near(tl.WidthMetres, 2.4)
            && Near(tl.X0, 1.8), "sill 1.0 m and head 2.0 m: a 1.0 m tall window needs 2.40 m", Describe(tallLayout));
        Check(Oracle(flat, tallLayout, tall, rules) is null, "sill/head facade agrees with the oracle", Oracle(flat, tallLayout, tall, rules) ?? "");
        var clear = defaults with { EndClearanceMetres = 0 };
        Check(FacadePlanner.Place(notch, caba, clear) is { } open && Room(open, "bedroom_t") is { Status: FacadeRoomStatus.Failed, Reason: FacadeCodes.EdgeTooShort },
            "without clearances the 1 m part still cannot hold 1.75 m");

        // ---- upper level: rooms of another level are not neighbours ----
        var twoLevel = new SpacePlan(SpacePlanStatus.Feasible, "two-level", 1, 2, new PlanFootprint(10, 4), 0.9,
            new[] { P("living", "Living", 0, 0, 6, 4), P("bedroom_up", "Bedroom up", 6, 0, 4, 4, 1) }, Array.Empty<PlanConstraint>(), 0);
        var upLayout = FacadePlanner.Place(twoLevel, caba);
        Check(upLayout.IsComplete && Room(upLayout, "living") is { } ul && Near(ul.ExteriorLengthMetres, 20)
            && upLayout.Windows.FirstOrDefault(w => w.Instance == "bedroom_up") is { Level: 1, Side: FacadeSide.South } up && Near(up.WidthMetres, 1.334),
            "a level-1 room beside a level-0 room shares no wall with it; its window is on level 1", Describe(upLayout));
        Check(Oracle(twoLevel, upLayout, defaults, rules) is null, "two-level facade agrees with the oracle", Oracle(twoLevel, upLayout, defaults, rules) ?? "");

        // ---- pack handling ----
        var qa = SkillParser.Parse(File.ReadAllText(Path.Combine(root, "skills", "bim.qa.element-basics", "skill.json")));
        var qaLayout = FacadePlanner.Place(flat, qa);
        Check(qaLayout.IsComplete && qaLayout.Windows.Count == 0 && qaLayout.Rooms.All(r => r.Status == FacadeRoomStatus.NotRequired) && qaLayout.PackId == qa.Id,
            "a pack without room ratio rules requires no window", Describe(qaLayout));
        var conditional = caba with { Rules = caba.Rules.Select(r => r.Id == "living-illumination-ratio" ? r with { Citation = r.Citation + "; applies_if=area_m2<=5" } : r).ToArray() };
        var conditionalLayout = FacadePlanner.Place(flat, conditional);
        Check(conditionalLayout.Status == FacadeStatus.Incomplete && Room(conditionalLayout, "living") is { Status: FacadeRoomStatus.Failed, Reason: FacadeCodes.RuleUnsupported }
            && conditionalLayout.Windows.All(w => w.Instance != "living") && Room(conditionalLayout, "bedroom_1") is { Status: FacadeRoomStatus.Compliant },
            "a matched ratio rule with an applies_if condition is facade_rule_unsupported, never silently passed", Describe(conditionalLayout));
        SkillParser.TryParseRange("*", out var anyRange);
        var dependent = caba with { Requires = new[] { new SkillRequirement("ar.missing.pack", anyRange) } };
        Refused("a pack that does not resolve on its own", FacadePlanner.Place(flat, dependent), FacadeCodes.PackInvalid);

        // ---- refusals ----
        Refused("an infeasible plan", FacadePlanner.Place(flat with { Status = SpacePlanStatus.Infeasible }, caba), FacadeCodes.PlanInvalid);
        Refused("a plan without rooms", FacadePlanner.Place(flat with { Rooms = Array.Empty<PlannedRoom>() }, caba), FacadeCodes.PlanInvalid);
        Refused("overlapping rooms", FacadePlanner.Place(flat with { Rooms = flatRooms.Append(P("bedroom_x", "Bedroom x", 1, 1, 3, 3)).ToArray() }, caba), FacadeCodes.PlanInvalid);
        Refused("a duplicate instance id", FacadePlanner.Place(flat with { Rooms = flatRooms.Append(P("living", "Living", 0, 6, 6, 4)).ToArray() }, caba), FacadeCodes.PlanInvalid);
        Refused("a degenerate rectangle", FacadePlanner.Place(flat with { Rooms = new[] { P("living", "Living", 0, 0, 0, 4) } }, caba), FacadeCodes.PlanInvalid);
        foreach (var (label, bad) in new[]
        {
            ("head at the sill", defaults with { HeadHeightMetres = 0.9 }), ("negative sill", defaults with { SillHeightMetres = -0.1 }),
            ("NaN sill", defaults with { SillHeightMetres = double.NaN }), ("head above 10 m", defaults with { HeadHeightMetres = 10.5 }),
            ("negative clearance", defaults with { EndClearanceMetres = -0.01 }), ("infinite clearance", defaults with { EndClearanceMetres = double.PositiveInfinity }),
            ("openable fraction 0", defaults with { OpenableFraction = 0 }), ("openable fraction 1.5", defaults with { OpenableFraction = 1.5 }),
            ("minimum width 0", defaults with { MinWindowWidthMetres = 0 }), ("NaN minimum width", defaults with { MinWindowWidthMetres = double.NaN }),
        })
            Refused("options: " + label, FacadePlanner.Place(flat, caba, bad), FacadeCodes.OptionsInvalid);

        // ---- determinism: runs, room order, culture ----
        var canonical = FacadeSerializer.Serialize(layout);
        Check(canonical.StartsWith("{\"facade_version\":1,\"status\":\"complete\"", StringComparison.Ordinal) && canonical.Contains("\"window:living/1\"", StringComparison.Ordinal)
            && canonical.Contains(PackId + "/living-illumination-ratio", StringComparison.Ordinal) && canonical.Contains("\"sill_height_m\":0.9", StringComparison.Ordinal),
            "canonical JSON names the version, status, windows, rule ids and options", canonical.Length > 400 ? canonical[..400] : canonical);
        Check(FacadeSerializer.Serialize(FacadePlanner.Place(flat, caba)) == canonical, "same plan, byte-identical output");
        Check(FacadeSerializer.Serialize(FacadePlanner.Place(flat with { Rooms = Enumerable.Reverse(flatRooms).ToArray() }, caba)) == canonical, "room order does not change the output");
        Check(FacadeSerializer.Serialize(gridLayout) == FacadeSerializer.Serialize(FacadePlanner.Place(grid with { Rooms = gridRooms.OrderByDescending(r => r.InstanceId, StringComparer.Ordinal).ToArray() }, caba)),
            "grid output is independent of room order");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "de-DE", "ar-SA", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                Check(FacadeSerializer.Serialize(FacadePlanner.Place(flat, caba)) == canonical && FacadeSerializer.Serialize(FacadePlanner.Place(slot, caba)) == FacadeSerializer.Serialize(slotLayout),
                    "output is culture-invariant under " + name);
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Check(FacadeSerializer.Serialize(FacadePlanner.Place(flat, dependent)).Contains("\"status\":\"refused\"", StringComparison.Ordinal), "a refusal serializes as refused");

        // ---- prototypes: every ranked 1/2/3-bedroom layout against the independent oracle ----
        int layouts = 0, compliant = 0, failedRooms = 0, windows = 0;
        foreach (var (typology, lot) in new[] { (ApartmentTypology.OneBedroom, new PrototypeLot(8.66, 20)), (ApartmentTypology.TwoBedroom, new PrototypeLot(8.66, 20)),
            (ApartmentTypology.ThreeBedroom, new PrototypeLot(8, 20)), (ApartmentTypology.TwoBedroom, new PrototypeLot(12, 30)) })
        {
            var result = PrototypeGenerator.Generate(new PrototypeRequest(typology, lot, 4, Seed: 0));
            Check(result.Status == PrototypeStatus.Ranked && result.Alternatives is not null, "prototype " + typology + " on " + F(lot.WidthMetres) + " x " + F(lot.DepthMetres) + " is ranked");
            foreach (var alternative in result.Alternatives?.Ranked ?? Array.Empty<Alternative>())
            {
                var placed = FacadePlanner.Place(alternative.Plan, caba);
                layouts++;
                compliant += placed.Rooms.Count(r => r.Status == FacadeRoomStatus.Compliant);
                failedRooms += placed.Rooms.Count(r => r.Status == FacadeRoomStatus.Failed);
                windows += placed.Windows.Count;
                var problem = Oracle(alternative.Plan, placed, defaults, rules);
                Check(placed.Status != FacadeStatus.Refused && problem is null, "prototype " + typology + " alternative " + Text(alternative.Index) + " agrees with the oracle", problem ?? Describe(placed));
                Check(FacadeSerializer.Serialize(FacadePlanner.Place(alternative.Plan with { Rooms = Enumerable.Reverse(alternative.Plan.Rooms).ToArray() }, caba)) == FacadeSerializer.Serialize(placed),
                    "prototype " + typology + " alternative " + Text(alternative.Index) + " is deterministic under room reordering");
            }
        }
        Check(layouts >= 8 && compliant >= layouts * 2 && windows >= compliant, "prototype layouts get windows for their habitable rooms",
            "layouts " + Text(layouts) + " compliant rooms " + Text(compliant) + " failed rooms " + Text(failedRooms) + " windows " + Text(windows));
    }

    // Independent oracle. For every window: on a side of its own room, axis-aligned, width from its coordinates at least
    // the minimum, heights and areas from the options, and the window grown by the end clearance on both ends stays on
    // the room side and touches no side of another room of the level (sampled: no partition, only the building outline);
    // windows of a room do not overlap; rule ids are the pack rules whose room pattern matches the accent-stripped name.
    // For every room: no pack rule -> not required and no window; compliant -> sum of window width x height over the room
    // area meets the largest matched illumination ratio and the openable share meets the ventilation ratio; failed with
    // no exterior edge -> no sampled point of any side lies on the outline; failed too short -> the sampled exterior runs,
    // less clearances, cannot hold the required width; a room the sampling shows can hold it with margin is compliant.
    static string? Oracle(SpacePlan plan, FacadeLayout layout, FacadeOptions options, IReadOnlyList<OracleRule> rules)
    {
        var problems = new List<string>();
        var height = options.HeadHeightMetres - options.SillHeightMetres;
        var c = options.EndClearanceMetres;
        var byId = plan.Rooms.ToDictionary(r => r.InstanceId, StringComparer.Ordinal);
        if (layout.Rooms.Count != plan.Rooms.Count) problems.Add("rooms " + Text(layout.Rooms.Count) + " != " + Text(plan.Rooms.Count));
        foreach (var window in layout.Windows)
        {
            if (!byId.TryGetValue(window.Instance, out var room)) { problems.Add(window.Id + " has no room"); continue; }
            var rect = room.Rect;
            if (window.Level != room.Level) problems.Add(window.Id + " level");
            var horizontal = window.Side is FacadeSide.South or FacadeSide.North;
            var line = window.Side switch { FacadeSide.South => rect.Y, FacadeSide.North => rect.Top, FacadeSide.West => rect.X, _ => rect.Right };
            var (lo, hi) = horizontal ? (rect.X, rect.Right) : (rect.Y, rect.Top);
            var (s, e) = horizontal ? (window.X0, window.X1) : (window.Y0, window.Y1);
            if (horizontal ? !(window.Y0 == line && window.Y1 == line) : !(window.X0 == line && window.X1 == line)) problems.Add(window.Id + " is not on the " + window.Side + " side of " + room.InstanceId);
            if (!(e > s) || Math.Abs(e - s - window.WidthMetres) > Tolerance || window.WidthMetres < options.MinWindowWidthMetres - Tolerance) problems.Add(window.Id + " width " + R(window.WidthMetres));
            if (window.SillHeightMetres != options.SillHeightMetres || window.HeadHeightMetres != options.HeadHeightMetres) problems.Add(window.Id + " heights");
            if (Math.Abs(window.AreaSquareMetres - (e - s) * height) > Tolerance || Math.Abs(window.VentilationAreaSquareMetres - (e - s) * height * options.OpenableFraction) > Tolerance)
                problems.Add(window.Id + " areas");
            if (s - c < lo - Tolerance || e + c > hi + Tolerance) problems.Add(window.Id + " leaves the room side with its end clearances");
            const int samples = 400;
            var (first, last) = (Math.Max(lo, s - c) + Tolerance, Math.Min(hi, e + c) - Tolerance);
            for (var k = 0; k <= samples; k++)
            {
                var t = first + (last - first) * k / samples;
                if (OnPartition(plan, room, window.Side, t)) { problems.Add(window.Id + " touches another room's wall at " + R(t)); break; }
            }
            var expected = Expected(rules, room.Name);
            if (!window.RuleIds.SequenceEqual(expected)) problems.Add(window.Id + " cites " + string.Join(",", window.RuleIds) + " not " + string.Join(",", expected));
        }
        foreach (var group in layout.Windows.GroupBy(w => w.Instance))
        {
            var list = group.ToArray();
            for (var i = 0; i < list.Length; i++)
                for (var j = i + 1; j < list.Length; j++)
                    if (list[i].Side == list[j].Side && Math.Min(list[i].X1, list[j].X1) - Math.Max(list[i].X0, list[j].X0) > -Tolerance
                        && Math.Min(list[i].Y1, list[j].Y1) - Math.Max(list[i].Y0, list[j].Y0) > -Tolerance) problems.Add(list[i].Id + " overlaps " + list[j].Id);
        }
        foreach (var room in plan.Rooms)
        {
            var record = layout.Rooms.FirstOrDefault(r => r.Instance == room.InstanceId);
            if (record is null) { problems.Add(room.InstanceId + " missing"); continue; }
            var mine = layout.Windows.Where(w => w.Instance == room.InstanceId).ToArray();
            var matched = rules.Where(r => Glob(r.Pattern, Normalize(room.Name))).ToArray();
            var area = room.Rect.Width * room.Rect.Depth;
            if (matched.Length == 0)
            {
                if (record.Status != FacadeRoomStatus.NotRequired || mine.Length > 0) problems.Add(room.InstanceId + " has no pack rule but is " + record.Status);
                continue;
            }
            var illumination = matched.Where(r => r.Property == "window_area_m2").Select(r => r.Value).DefaultIfEmpty(0).Max();
            var ventilation = matched.Where(r => r.Property == "ventilation_area_m2").Select(r => r.Value).DefaultIfEmpty(0).Max();
            var lit = mine.Sum(w => (horizontalOf(w) ? w.X1 - w.X0 : w.Y1 - w.Y0) * height);
            var needWidth = Math.Max(options.MinWindowWidthMetres, Math.Max(illumination * area / height, ventilation * area / (height * options.OpenableFraction)));
            var runs = ExteriorRuns(plan, room);
            // Lenient (any run that might hold the minimum width) and strict (runs that surely hold it) usable lengths.
            var lenient = runs.Select(run => run - 2 * c).Where(u => u >= options.MinWindowWidthMetres - Margin).Sum();
            var strict = runs.Select(run => run - 2 * c).Where(u => u >= options.MinWindowWidthMetres + Margin).Select(u => u - Margin).Sum();
            if (record.Status == FacadeRoomStatus.Compliant)
            {
                if (mine.Length == 0) problems.Add(room.InstanceId + " compliant without a window");
                if (lit / area < illumination - Tolerance) problems.Add(room.InstanceId + " illumination " + R(lit / area) + " < " + R(illumination));
                if (lit * options.OpenableFraction / area < ventilation - Tolerance) problems.Add(room.InstanceId + " ventilation " + R(lit * options.OpenableFraction / area) + " < " + R(ventilation));
            }
            else if (record.Status == FacadeRoomStatus.Failed)
            {
                if (mine.Length > 0) problems.Add(room.InstanceId + " failed but has windows");
                if (record.Reason == FacadeCodes.NoExteriorEdge && runs.Count > 0) problems.Add(room.InstanceId + " has exterior runs " + string.Join(",", runs.Select(R)) + " but no_exterior_edge");
                else if (record.Reason == FacadeCodes.EdgeTooShort && (runs.Count == 0 || lenient > needWidth + Margin * (runs.Count + 1))) problems.Add(room.InstanceId + " usable " + R(lenient) + " > " + R(needWidth) + " but edge_too_short");
                else if (record.Reason is not (FacadeCodes.NoExteriorEdge or FacadeCodes.EdgeTooShort or FacadeCodes.RuleUnsupported)) problems.Add(room.InstanceId + " reason " + record.Reason);
            }
            else problems.Add(room.InstanceId + " has pack rules but is " + record.Status);
            if (record.Status != FacadeRoomStatus.Compliant && record.Reason != FacadeCodes.RuleUnsupported && strict >= needWidth + Margin)
                problems.Add(room.InstanceId + " could hold " + R(needWidth) + " m in " + R(strict) + " m but is " + record.Status);
        }
        return problems.Count == 0 ? null : string.Join("; ", problems.Take(6));

        static bool horizontalOf(FacadeWindow w) => w.Side is FacadeSide.South or FacadeSide.North;
    }

    // Lengths of the maximal runs of a room's sides that touch no other room of the level, sampled at the midpoints of
    // steps of at most 0.5 mm, so each run is within 1 mm of its true length.
    static List<double> ExteriorRuns(SpacePlan plan, PlannedRoom room)
    {
        var runs = new List<double>();
        foreach (var side in new[] { FacadeSide.South, FacadeSide.East, FacadeSide.North, FacadeSide.West })
        {
            var horizontal = side is FacadeSide.South or FacadeSide.North;
            var (lo, hi) = horizontal ? (room.Rect.X, room.Rect.Right) : (room.Rect.Y, room.Rect.Top);
            var n = (int)Math.Ceiling((hi - lo) / 0.0005);
            var step = (hi - lo) / n;
            var run = 0;
            for (var k = 0; k < n; k++)
            {
                if (!OnPartition(plan, room, side, lo + (k + 0.5) * step)) { run++; continue; }
                if (run > 0) runs.Add(run * step);
                run = 0;
            }
            if (run > 0) runs.Add(run * step);
        }
        return runs;
    }

    // Whether the point at coordinate t along the given side of the room lies on a side of another room of its level.
    static bool OnPartition(SpacePlan plan, PlannedRoom room, FacadeSide side, double t)
    {
        var r = room.Rect;
        foreach (var other in plan.Rooms)
        {
            if (ReferenceEquals(other, room) || other.InstanceId == room.InstanceId || other.Level != room.Level) continue;
            var o = other.Rect;
            var touches = side switch
            {
                FacadeSide.South => Math.Abs(o.Top - r.Y) < 1e-7 && o.X < t && t < o.Right,
                FacadeSide.North => Math.Abs(o.Y - r.Top) < 1e-7 && o.X < t && t < o.Right,
                FacadeSide.West => Math.Abs(o.Right - r.X) < 1e-7 && o.Y < t && t < o.Top,
                _ => Math.Abs(o.X - r.Right) < 1e-7 && o.Y < t && t < o.Top,
            };
            if (touches) return true;
        }
        return false;
    }

    static string[] Expected(IReadOnlyList<OracleRule> rules, string name)
    {
        var normalized = Normalize(name);
        var matched = rules.Where(r => Glob(r.Pattern, normalized)).ToArray();
        return matched.Where(r => r.Property == "window_area_m2").Select(r => r.QualifiedId).OrderBy(id => id, StringComparer.Ordinal)
            .Concat(matched.Where(r => r.Property == "ventilation_area_m2").Select(r => r.QualifiedId).OrderBy(id => id, StringComparer.Ordinal)).ToArray();
    }

    // The pack's room ratio rules read straight from the JSON (op ratio_min over area_m2).
    static IReadOnlyList<OracleRule> OracleRules(string json)
    {
        using var document = JsonDocument.Parse(json);
        var id = document.RootElement.GetProperty("id").GetString()!;
        var list = new List<OracleRule>();
        foreach (var rule in document.RootElement.GetProperty("rules").EnumerateArray())
        {
            if (!rule.GetProperty("target").TryGetProperty("room", out var room)) continue;
            var check = rule.GetProperty("check");
            if (check.GetProperty("op").GetString() != "ratio_min" || !check.TryGetProperty("over", out var over) || over.GetString() != "area_m2") continue;
            var property = check.GetProperty("property").GetString()!;
            if (property is not ("window_area_m2" or "ventilation_area_m2")) continue;
            list.Add(new OracleRule(id + "/" + rule.GetProperty("id").GetString(), room.GetString()!, property, check.GetProperty("value").GetDouble()));
        }
        return list;
    }

    static string Normalize(string name)
    {
        var builder = new StringBuilder();
        foreach (var ch in name.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) builder.Append(ch);
        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }

    static bool Glob(string pattern, string text) =>
        Regex.IsMatch(text, @"\A" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal) + @"\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1));

    static PlannedRoom P(string id, string name, double x, double y, double width, double depth, int level = 0) => new(id, id, name, 1, level, new PlanRect(x, y, width, depth));

    static SpacePlan Plan(string name, int seed, double width, double depth, params PlannedRoom[] rooms) =>
        new(SpacePlanStatus.Feasible, name, seed, 1, new PlanFootprint(width, depth), 0.9, rooms, Array.Empty<PlanConstraint>(), 0);

    static FacadeRoom? Room(FacadeLayout layout, string instance) => layout.Rooms.FirstOrDefault(r => r.Instance == instance);

    static bool Near(double value, double expected) => Math.Abs(value - expected) <= Tolerance;

    static string Describe(FacadeLayout layout) => layout.Status + " | " + string.Join(" ", layout.Windows.Select(w => w.Id + "@" + w.Side + "(" + R(w.X0) + "," + R(w.Y0) + "-" + R(w.X1) + "," + R(w.Y1) + ")"))
        + " | " + string.Join(" ", layout.Rooms.Select(r => r.Instance + ":" + r.Status + (r.Reason is null ? "" : "/" + r.Reason))) + " | " + string.Join("; ", layout.Violations.Select(v => v.Code + "@" + v.Subject).Take(4));

    static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
