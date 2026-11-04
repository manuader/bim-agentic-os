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
            && Near(lr.RequiredWindowAreaSquareMetres ?? -1, 2.4) && Near(lr.RequiredVentilationAreaSquareMetres ?? -1, 0.8) && Near(lr.WindowAreaSquareMetres, 2.4)
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
            && Near(t.ExteriorLengthMetres, 1) && Near(t.RequiredWindowAreaSquareMetres ?? -1, 2.1) && t.WindowAreaSquareMetres == 0 && notchLayout.Windows.Count == 0
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
        var oddMin = defaults with { MinWindowWidthMetres = 2.007 };
        var oddLayout = FacadePlanner.Place(flat, caba, oddMin);
        Check(oddLayout.IsComplete && oddLayout.Windows.Count == 2 && oddLayout.Windows.All(w => Near(w.WidthMetres, 2.007)),
            "a 2.007 m minimum width (2007.0000000000002 mm in binary) is 2007 mm, not 2008 mm", Describe(oddLayout));
        Check(Oracle(flat, oddLayout, oddMin, rules) is null, "odd minimum width facade agrees with the oracle", Oracle(flat, oddLayout, oddMin, rules) ?? "");

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
        Check(conditionalLayout.IsComplete && Room(conditionalLayout, "living") is { Status: FacadeRoomStatus.Compliant, IlluminationRatio: null } cl && cl.RuleIds.SequenceEqual(new[] { PackId + "/living-ventilation-ratio" })
            && conditionalLayout.Windows.FirstOrDefault(w => w.Instance == "living") is { } cw0 && Near(cw0.WidthMetres, 1.334) && cw0.RuleIds.SequenceEqual(new[] { PackId + "/living-ventilation-ratio" }),
            "a rule whose evaluable applies_if the room exceeds (area_m2<=5, 24 m2) does not cover it: the living is sized by ventilation alone (0.8 / 0.6 = 1.334 m)", Describe(conditionalLayout));
        var applies = caba with { Rules = caba.Rules.Select(r => r.Id == "living-illumination-ratio" ? r with { Citation = r.Citation + "; applies_if=area_m2<=30" } : r).ToArray() };
        var appliesLayout = FacadePlanner.Place(flat, applies);
        Check(appliesLayout.IsComplete && FacadeSerializer.Serialize(appliesLayout) == FacadeSerializer.Serialize(layout),
            "applies_if=area_m2<=30 holds for the 24 m2 living (evaluated as P5-02 does): the same windows as without the condition", Describe(appliesLayout));
        var sideCondition = caba with { Rules = caba.Rules.Select(r => r.Id == "living-illumination-ratio" ? r with { Citation = r.Citation + "; applies_if=min_side_m<=4" } : r).ToArray() };
        Check(FacadePlanner.Place(flat, sideCondition) is { Status: FacadeStatus.Complete } sideLayout && Room(sideLayout, "living") is { Status: FacadeRoomStatus.Compliant },
            "applies_if=min_side_m<=4 holds for the 6 x 4 m living");
        var heightCondition = caba with { Rules = caba.Rules.Select(r => r.Id == "living-illumination-ratio" ? r with { Citation = r.Citation + "; applies_if=clear_height_m<=3" } : r).ToArray() };
        var heightLayout = FacadePlanner.Place(flat, heightCondition);
        Check(heightLayout.Status == FacadeStatus.Incomplete && Room(heightLayout, "living") is { Status: FacadeRoomStatus.Failed, Reason: FacadeCodes.RuleUnsupported }
            && heightLayout.Windows.All(w => w.Instance != "living") && Room(heightLayout, "bedroom_1") is { Status: FacadeRoomStatus.Compliant },
            "applies_if on a property the plan does not carry (clear_height_m) cannot be evaluated: facade_rule_unsupported, never silently passed", Describe(heightLayout));
        Check(Room(heightLayout, "living") is { IlluminationRatio: null, RequiredWindowAreaSquareMetres: null } partial && partial.VentilationRatio is { } pv && Near(pv, 1.0 / 30)
            && Near(partial.RequiredVentilationAreaSquareMetres ?? -1, 0.8) && partial.WindowAreaSquareMetres == 0,
            "an unsupported room reports the illumination ratio as unknown (null) and keeps only the supported ventilation ratio", Describe(heightLayout));
        var general = caba.Rules.First(r => r.Id == "living-illumination-ratio");
        var stricter = caba with
        {
            Rules = caba.Rules.Append(general with { Id = "living-illumination-small", Check = general.Check with { Value = SkillValue.Of(0.125) }, Citation = general.Citation + "; applies_if=area_m2<=10" }).ToArray(),
        };
        var stricterLayout = FacadePlanner.Place(flat, stricter);
        Check(stricterLayout.IsComplete && Room(stricterLayout, "living") is { Status: FacadeRoomStatus.Compliant, IlluminationRatio: 0.1 }
            && stricterLayout.Windows.FirstOrDefault(w => w.Instance == "living") is { } sw && Near(sw.WidthMetres, 2)
            && sw.RuleIds.SequenceEqual(new[] { PackId + "/living-illumination-ratio", PackId + "/living-ventilation-ratio" }),
            "a general 0.1 rule plus a stricter 0.125 rule with applies_if area_m2<=10: the 24 m2 living is sized at 0.1 (2.00 m)", Describe(stricterLayout));
        var small = Plan("small", 1, 3, 3, P("living", "Living s", 0, 0, 3, 3));
        var smallLayout = FacadePlanner.Place(small, stricter);
        Check(smallLayout.IsComplete && Room(smallLayout, "living") is { IlluminationRatio: 0.125 } && smallLayout.Windows.FirstOrDefault(w => w.Instance == "living") is { } ssw
            && Near(ssw.WidthMetres, 0.938) && ssw.RuleIds.SequenceEqual(new[] { PackId + "/living-illumination-ratio", PackId + "/living-illumination-small", PackId + "/living-ventilation-ratio" }),
            "a 9 m2 living meets the condition and takes the stricter 0.125 (9 x 0.125 / 1.2 = 0.938 m)", Describe(smallLayout));

        // ---- thresholds from a pack parameter, and run-time parameter overrides ----
        var parameterised = caba with
        {
            Parameters = caba.Parameters.Append(new SkillParameter("living_ilum_ratio", SkillParameterType.Number, SkillValue.Of(0.125), 0, 1, "test parameter")).ToArray(),
            Rules = caba.Rules.Select(r => r.Id == "living-illumination-ratio" ? r with { Check = r.Check with { Value = null, Parameter = "living_ilum_ratio" } } : r).ToArray(),
        };
        var paramLayout = FacadePlanner.Place(flat, parameterised);
        Check(paramLayout.IsComplete && Room(paramLayout, "living") is { IlluminationRatio: 0.125 } && paramLayout.Windows.FirstOrDefault(w => w.Instance == "living") is { } pw
            && Near(pw.WidthMetres, 2.5) && Near(pw.X0, 1.75), "a parameter threshold 0.125 (not the manifest literal 0.1): 24 x 0.125 / 1.2 = 2.50 m", Describe(paramLayout));
        var overridden = FacadePlanner.Place(flat, parameterised, null, new Dictionary<string, double> { [PackId + "/living_ilum_ratio"] = 0.2 });
        Check(overridden.IsComplete && Room(overridden, "living") is { IlluminationRatio: 0.2 } && overridden.Windows.FirstOrDefault(w => w.Instance == "living") is { } ow
            && Near(ow.WidthMetres, 4) && Near(ow.X0, 1), "a run-time override 0.2 of the parameter: 4.00 m", Describe(overridden));
        Refused("an override naming no pack parameter", FacadePlanner.Place(flat, parameterised, null, new Dictionary<string, double> { [PackId + "/nope"] = 0.2 }), FacadeCodes.OptionsInvalid);
        Refused("an override outside the parameter range", FacadePlanner.Place(flat, parameterised, null, new Dictionary<string, double> { [PackId + "/living_ilum_ratio"] = 2 }), FacadeCodes.OptionsInvalid);
        Refused("an override without a skill prefix", FacadePlanner.Place(flat, parameterised, null, new Dictionary<string, double> { ["living_ilum_ratio"] = 0.2 }), FacadeCodes.OptionsInvalid);

        // ---- party walls (medianeras): only listed footprint edges take openings ----
        var northOnly = defaults with { OpeningEdges = new[] { LotEdge.North } };
        var party = FacadePlanner.Place(flat, caba, northOnly);
        Check(party.Status == FacadeStatus.Incomplete && party.Windows.Count == 0 && Room(party, "living") is { Status: FacadeRoomStatus.Failed, Reason: FacadeCodes.LotLineOnly } pl
            && Near(pl.ExteriorLengthMetres, 10) && Room(party, "bedroom_1") is { Status: FacadeRoomStatus.Failed, Reason: FacadeCodes.LotLineOnly }
            && party.Violations.Any(c => c.Code == FacadeCodes.LotLineOnly && c.Subject == "living" && c.Hard && c.Detail.Length > 0),
            "with only the north lot edge open, the south/west/east lot-line sides are party walls: facade_lot_line_only", Describe(party));
        Check(Oracle(flat, party, northOnly, rules) is null, "party-wall facade agrees with the oracle", Oracle(flat, party, northOnly, rules) ?? "");
        var sides = defaults with { OpeningEdges = new[] { LotEdge.West, LotEdge.East } };
        var sideLots = FacadePlanner.Place(flat, caba, sides);
        Check(sideLots.IsComplete && sideLots.Windows.FirstOrDefault(w => w.Instance == "living") is { Side: FacadeSide.West, X0: 0, X1: 0 } lw && Near(lw.Y0, 1) && Near(lw.Y1, 3)
            && sideLots.Windows.FirstOrDefault(w => w.Instance == "bedroom_1") is { Side: FacadeSide.East, X0: 10 } bw && Near(bw.Y0, 1.333),
            "with the south lot edge closed, living opens west and the bedroom east", Describe(sideLots));
        Check(Oracle(flat, sideLots, sides, rules) is null, "west/east facade agrees with the oracle", Oracle(flat, sideLots, sides, rules) ?? "");
        Refused("an undefined opening edge", FacadePlanner.Place(flat, caba, defaults with { OpeningEdges = new[] { (LotEdge)9 } }), FacadeCodes.OptionsInvalid);
        Refused("a repeated opening edge", FacadePlanner.Place(flat, caba, defaults with { OpeningEdges = new[] { LotEdge.North, LotEdge.North } }), FacadeCodes.OptionsInvalid);

        // ---- an internal void (courtyard) enclosed by the level's rooms ----
        var court = Plan("court", 1, 9, 9, P("hall_s", "Hall s", 0, 0, 9, 3), P("hall_n", "Hall n", 0, 6, 9, 3), P("bedroom_w", "Bedroom w", 0, 3, 3, 3), P("living_e", "Living e", 6, 3, 3, 3));
        var courtLayout = FacadePlanner.Place(court, caba);
        Check(courtLayout.IsComplete && courtLayout.Windows.FirstOrDefault(w => w.Instance == "bedroom_w") is { Side: FacadeSide.East, X0: 3, FacesInternalVoid: true } cw && Near(cw.WidthMetres, 0.75)
            && courtLayout.Windows.FirstOrDefault(w => w.Instance == "living_e") is { Side: FacadeSide.East, X0: 9, FacesInternalVoid: false },
            "the west bedroom's east window opens on the courtyard (internal void), the living's east window on the outer boundary", Describe(courtLayout));
        Check(Oracle(court, courtLayout, defaults, rules) is null, "courtyard facade agrees with the oracle", Oracle(court, courtLayout, defaults, rules) ?? "");
        var closed = defaults with { OpeningEdges = Array.Empty<LotEdge>() };
        var courtOnly = FacadePlanner.Place(court, caba, closed);
        Check(courtOnly.IsComplete && courtOnly.Windows.Count == 2 && courtOnly.Windows.All(w => w.FacesInternalVoid)
            && courtOnly.Windows.FirstOrDefault(w => w.Instance == "living_e") is { Side: FacadeSide.West, X0: 6 },
            "with every lot edge closed, both rooms open on the courtyard, which is not a lot line", Describe(courtOnly));
        Check(Oracle(court, courtOnly, closed, rules) is null, "courtyard-only facade agrees with the oracle", Oracle(court, courtOnly, closed, rules) ?? "");
        Check(FacadeSerializer.Serialize(courtLayout).Contains("\"faces_internal_void\":true", StringComparison.Ordinal)
            && FacadeSerializer.Serialize(courtOnly).Contains("\"opening_edges\":[]", StringComparison.Ordinal)
            && FacadeSerializer.Serialize(layout).Contains("\"opening_edges\":[\"south\",\"east\",\"north\",\"west\"]", StringComparison.Ordinal),
            "canonical JSON carries the void flag and the opening edges");

        // ---- bounded sizing: degenerate options are refused, a huge requirement is too short, never an overflow ----
        var huge = Plan("huge", 1, 1000, 1000, P("living", "Living", 0, 0, 1000, 1000));
        var hugeLayout = FacadePlanner.Place(huge, caba);
        Check(Room(hugeLayout, "living") is { Status: FacadeRoomStatus.Failed, Reason: FacadeCodes.EdgeTooShort } && hugeLayout.Windows.Count == 0,
            "a 1000 x 1000 m living needs 83 km of window: facade_edge_too_short, no window", Describe(hugeLayout));
        var extreme = new FacadeOptions(0, FacadeOptions.MinWindowHeightMetres, 0, FacadeOptions.MinOpenableFraction, 0.001);
        var widest = Plan("widest", 1, 10000, 10000, P("living", "Living", 0, 0, 10000, 10000));
        var widestLayout = FacadePlanner.Place(widest, caba, extreme);
        Check(widestLayout.Status == FacadeStatus.Incomplete && Room(widestLayout, "living") is { Status: FacadeRoomStatus.Failed, Reason: FacadeCodes.EdgeTooShort } && widestLayout.Windows.Count == 0,
            "the largest plan with the smallest allowed window height and openable fraction is facade_edge_too_short", Describe(widestLayout));
        Check(layout.Checks.Concat(slotLayout.Checks).Where(c => c.Code is FacadeCodes.Illumination or FacadeCodes.Ventilation).All(c => c.Satisfied && c.Hard),
            "illumination and ventilation checks of compliant rooms are satisfied by the placed windows");
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
            ("openable fraction 1e-20", defaults with { OpenableFraction = 1e-20 }), ("openable fraction below 0.01", defaults with { OpenableFraction = 0.009 }),
            ("head one ulp above the sill", defaults with { HeadHeightMetres = Math.BitIncrement(0.9) }), ("window 0.05 m tall", defaults with { HeadHeightMetres = 0.95 }),
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
                var habitable = alternative.Plan.Rooms.Where(r => rules.Any(rule => Glob(rule.Pattern, Normalize(r.Name)))).ToArray();
                var unexplained = habitable.Where(r => ExteriorRuns(alternative.Plan, r).Count > 0 && Room(placed, r.InstanceId) is not ({ Status: FacadeRoomStatus.Compliant }
                    or { Status: FacadeRoomStatus.Failed, Reason: FacadeCodes.EdgeTooShort or FacadeCodes.LotLineOnly })).Select(r => r.InstanceId).ToArray();
                Check(habitable.Length >= 2 && unexplained.Length == 0 && habitable.All(r => Room(placed, r.InstanceId) is { Status: not FacadeRoomStatus.NotRequired, Detail.Length: > 0 }),
                    "prototype " + typology + " alternative " + Text(alternative.Index) + ": every habitable room with an exterior side is compliant or too short, with a detail",
                    string.Join(",", unexplained) + " | " + Describe(placed));
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
        var rasters = new Dictionary<int, Func<double, double, bool>>();
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
            if (PartyWall(plan, window.Side, line, options)) problems.Add(window.Id + " is on a closed lot edge (party wall)");
            var outward = 0.1;
            var (px, py) = window.Side switch
            {
                FacadeSide.South => ((window.X0 + window.X1) / 2, line - outward),
                FacadeSide.North => ((window.X0 + window.X1) / 2, line + outward),
                FacadeSide.West => (line - outward, (window.Y0 + window.Y1) / 2),
                _ => (line + outward, (window.Y0 + window.Y1) / 2),
            };
            if (!rasters.TryGetValue(room.Level, out var inVoid)) rasters[room.Level] = inVoid = VoidRaster(plan, room.Level);
            if (inVoid(px, py) != window.FacesInternalVoid) problems.Add(window.Id + " internal void flag " + window.FacesInternalVoid + " is wrong");
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
            var allRuns = ExteriorRuns(plan, room, options);
            var runs = allRuns.Where(run => !run.PartyWall).Select(run => run.Length).ToList();
            // Lenient (any run that might hold the minimum width) and strict (runs that surely hold it) usable lengths.
            var lenient = runs.Select(run => run - 2 * c).Where(u => u >= options.MinWindowWidthMetres - Margin).Sum();
            var strict = runs.Select(run => run - 2 * c).Where(u => u >= options.MinWindowWidthMetres + Margin).Select(u => u - Margin).Sum();
            if (record.Status == FacadeRoomStatus.Compliant)
            {
                if (mine.Length == 0) problems.Add(room.InstanceId + " compliant without a window");
                if (lit / area < illumination * (1 - 1e-12)) problems.Add(room.InstanceId + " illumination " + R(lit / area) + " < " + R(illumination));
                if (lit * options.OpenableFraction / area < ventilation * (1 - 1e-12)) problems.Add(room.InstanceId + " ventilation " + R(lit * options.OpenableFraction / area) + " < " + R(ventilation));
            }
            else if (record.Status == FacadeRoomStatus.Failed)
            {
                if (mine.Length > 0) problems.Add(room.InstanceId + " failed but has windows");
                if (record.Reason == FacadeCodes.NoExteriorEdge && allRuns.Count > 0) problems.Add(room.InstanceId + " has exterior runs " + string.Join(",", allRuns.Select(run => R(run.Length))) + " but no_exterior_edge");
                else if (record.Reason == FacadeCodes.LotLineOnly && (allRuns.Count == 0 || runs.Count > 0)) problems.Add(room.InstanceId + " lot_line_only but has " + Text(runs.Count) + " open runs of " + Text(allRuns.Count));
                else if (record.Reason == FacadeCodes.EdgeTooShort && (runs.Count == 0 || lenient > needWidth + Margin * (runs.Count + 1))) problems.Add(room.InstanceId + " usable " + R(lenient) + " > " + R(needWidth) + " but edge_too_short");
                else if (record.Reason is not (FacadeCodes.NoExteriorEdge or FacadeCodes.EdgeTooShort or FacadeCodes.RuleUnsupported or FacadeCodes.LotLineOnly)) problems.Add(room.InstanceId + " reason " + record.Reason);
            }
            else problems.Add(room.InstanceId + " has pack rules but is " + record.Status);
            if (record.Status != FacadeRoomStatus.Compliant && record.Reason != FacadeCodes.RuleUnsupported && strict >= needWidth + Margin)
                problems.Add(room.InstanceId + " could hold " + R(needWidth) + " m in " + R(strict) + " m but is " + record.Status);
        }
        return problems.Count == 0 ? null : string.Join("; ", problems.Take(6));

        static bool horizontalOf(FacadeWindow w) => w.Side is FacadeSide.South or FacadeSide.North;
    }

    // Whether a room side on this line lies on a footprint edge the options close (a party wall).
    static bool PartyWall(SpacePlan plan, FacadeSide side, double line, FacadeOptions options)
    {
        var open = options.OpeningEdges ?? new[] { LotEdge.South, LotEdge.East, LotEdge.North, LotEdge.West };
        var footprint = plan.Footprint!;
        var (edge, at) = side switch
        {
            FacadeSide.South => (LotEdge.South, 0.0),
            FacadeSide.North => (LotEdge.North, footprint.DepthMetres),
            FacadeSide.West => (LotEdge.West, 0.0),
            _ => (LotEdge.East, footprint.WidthMetres),
        };
        return Math.Abs(line - at) < 1e-7 && !open.Contains(edge);
    }

    // Point-sampled raster of one level (2.5 cm cells over the rooms' bounding box plus 0.5 m): a cell is occupied when its
    // centre lies inside a room; free cells reached by a flood fill from the raster border are outside, the other free cells
    // are internal voids. The returned test tells whether a point lies in a void cell.
    static Func<double, double, bool> VoidRaster(SpacePlan plan, int level)
    {
        const double cell = 0.025, frame = 0.5;
        var rooms = plan.Rooms.Where(r => r.Level == level).Select(r => r.Rect).ToArray();
        var (x0, y0) = (rooms.Min(r => r.X) - frame, rooms.Min(r => r.Y) - frame);
        var nx = (int)Math.Ceiling((rooms.Max(r => r.Right) + frame - x0) / cell);
        var ny = (int)Math.Ceiling((rooms.Max(r => r.Top) + frame - y0) / cell);
        var occupied = new bool[nx, ny];
        foreach (var r in rooms)
            for (var i = Math.Max(0, (int)((r.X - x0) / cell) - 1); i < Math.Min(nx, (int)((r.Right - x0) / cell) + 2); i++)
                for (var j = Math.Max(0, (int)((r.Y - y0) / cell) - 1); j < Math.Min(ny, (int)((r.Top - y0) / cell) + 2); j++)
                {
                    var (cx, cy) = (x0 + (i + 0.5) * cell, y0 + (j + 0.5) * cell);
                    if (r.X < cx && cx < r.Right && r.Y < cy && cy < r.Top) occupied[i, j] = true;
                }
        var outside = new bool[nx, ny];
        var stack = new Stack<(int, int)>();
        for (var i = 0; i < nx; i++)
            for (var j = 0; j < ny; j++)
                if ((i == 0 || j == 0 || i == nx - 1 || j == ny - 1) && !occupied[i, j]) { outside[i, j] = true; stack.Push((i, j)); }
        while (stack.Count > 0)
        {
            var (i, j) = stack.Pop();
            foreach (var (a, b) in new[] { (i - 1, j), (i + 1, j), (i, j - 1), (i, j + 1) })
                if (a >= 0 && b >= 0 && a < nx && b < ny && !occupied[a, b] && !outside[a, b]) { outside[a, b] = true; stack.Push((a, b)); }
        }
        return (x, y) =>
        {
            var (i, j) = ((int)Math.Floor((x - x0) / cell), (int)Math.Floor((y - y0) / cell));
            return i >= 0 && j >= 0 && i < nx && j < ny && !occupied[i, j] && !outside[i, j];
        };
    }

    // The maximal runs of a room's sides that touch no other room of the level, sampled at the midpoints of steps of at
    // most 0.5 mm, so each run is within 1 mm of its true length; PartyWall marks runs on a closed footprint edge.
    static List<(double Length, bool PartyWall)> ExteriorRuns(SpacePlan plan, PlannedRoom room, FacadeOptions? options = null)
    {
        options ??= new FacadeOptions();
        var runs = new List<(double Length, bool PartyWall)>();
        foreach (var side in new[] { FacadeSide.South, FacadeSide.East, FacadeSide.North, FacadeSide.West })
        {
            var horizontal = side is FacadeSide.South or FacadeSide.North;
            var (lo, hi) = horizontal ? (room.Rect.X, room.Rect.Right) : (room.Rect.Y, room.Rect.Top);
            var n = (int)Math.Ceiling((hi - lo) / 0.0005);
            var step = (hi - lo) / n;
            var line = side switch { FacadeSide.South => room.Rect.Y, FacadeSide.North => room.Rect.Top, FacadeSide.West => room.Rect.X, _ => room.Rect.Right };
            var party = PartyWall(plan, side, line, options);
            var run = 0;
            for (var k = 0; k < n; k++)
            {
                if (!OnPartition(plan, room, side, lo + (k + 0.5) * step)) { run++; continue; }
                if (run > 0) runs.Add((run * step, party));
                run = 0;
            }
            if (run > 0) runs.Add((run * step, party));
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
