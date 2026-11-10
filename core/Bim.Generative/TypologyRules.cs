using System.Globalization;
using Bim.Core;
namespace Bim.Generative;

// P5-13 composed pack rules constrain the typology planner (opt-in; PrototypeTemplates and the TypologyBrief output are
// unchanged). TypologyRules.From turns the effective rules of a composed skill resolution (for example
// ar.caba.codigo-edificacion + bim.typology.vivienda-unifamiliar + a style or a stricter code pack) into the SpacePlanner
// rules of a typology brief:
// 1. Candidate rules are the effective rules with severity error, op min, property area_m2 or min_side_m and a room
//    target (warnings and info are recommendations, not planner constraints; ratios, element rules and other properties
//    are not planner rules). Every candidate is checked first: a room glob holding '?' is typology_rule_pattern_unsupported
//    (SkillTarget reads '?' literally, the planner as a one-character wildcard; the parser accepts such globs and
//    manifests built in code can hold them) and a threshold that is not a positive finite number is
//    typology_rule_threshold_invalid (subject skill/rule; SkillValue already refuses non-finite numbers, so in practice
//    zero, negative or non-numeric thresholds).
// 2. Candidates whose room glob matches no brief room name are dropped. The rest are grouped by (skill id, room glob) in
//    resolution order (skills in resolver order, rules in pack order); each group is one PlannerRule with that glob, the
//    largest area and largest side minimum of the group, and Reference "<skill id> <rule id> <rule id>", a parameter
//    threshold adding " (<parameter> <value>)" after its rule id. That is the PrototypeTemplates.CabaRules format, so a
//    pack whose minimum rules equal CabaRules gives exactly CabaRules. The planner applies the largest minimum of every
//    matching rule, so a stricter composed pack wins over the code pack on the same rooms.
// 3. The room-id rules of PrototypeTemplates.Rules(lot) follow in their order. The hall (named Hall, which corridor globs
//    do not match) takes the largest candidate side minimum whose glob matches a corridor name (CorridorNames; the first
//    in resolution order on ties), traced to that rule and its parameter: the CABA pack's 1.00 m keeps the template rule
//    and so the code's 0.95 m on parcels narrower than 8.66 m; another value is used as is. Without one the hall keeps
//    the template side under a template reference that cites no pack. The laundry keeps its template side.
// 4. A brief room with a programme maximum below the largest matching area minimum or the square of the largest matching
//    side minimum is typology_minimum_conflict (subject the room kind), so it is refused before planning instead of
//    coming back infeasible. A side whose square lies above the programme minimum but within its maximum is accepted: the
//    planner raises the room's minimum area inside the programme range, which stays satisfiable.
// Traces give one entry per pack rule and per template rule minimum: the planner rule index, the property, the threshold,
// the skill rule (null for a template rule), the parameter source and the planner rule reference.
// TypologyRules.Generate runs the same request path as TypologyBrief.Generate(resolution, request) (TypologyBrief.Plan)
// with these rules instead of the fixed PrototypeTemplates.Rules(lot): the programme and request refusals are identical;
// the rule set is derived, and its refusals reported, only when they all pass. Same seeds, ranking and determinism.
// Composed area minimums above a programme minimum stay the P5-07 refusal typology_minimum_conflict, so composed area
// rules reach the planner only at or below the programme minimum; side rules (and the area their square implies) are what
// change plans.
public sealed record TypologyRuleTrace(int PlannerRuleIndex, string Property, double Threshold, SkillTrace? Rule, SkillTrace? ThresholdSource, string Reference);

// Rules holds the planner rules even when refused (only the accepted candidates); Refusals lists every refused rule.
public sealed record TypologyRuleSet(PlannerRules Rules, IReadOnlyList<TypologyRuleTrace> Traces, IReadOnlyList<PlanConstraint> Refusals)
{
    public bool Succeeded => Refusals.Count == 0;
}

// Rules is null when the programme or request was refused before the brief existed.
public sealed record TypologyRulesResult(TypologyBriefResult Result, TypologyRuleSet? Rules);

public static class TypologyRuleCodes
{
    // A composed minimum rule whose room glob holds '?' (subject skill/rule).
    public const string PatternUnsupported = "typology_rule_pattern_unsupported";
    // A composed minimum rule whose threshold is not a positive finite number (subject skill/rule).
    public const string ThresholdInvalid = "typology_rule_threshold_invalid";
}

public static class TypologyRules
{
    public const string AreaProperty = "area_m2";
    public const string SideProperty = "min_side_m";
    // Room names a composed corridor rule must match to give the hall its minimum side (the CABA corredor*, pasillo* and
    // corridor* globs match them; the typology hall is named Hall, which they do not).
    public static IReadOnlyList<string> CorridorNames { get; } = Array.AsReadOnly(new[] { "corredor", "pasillo", "corridor" });

    const string Caba = "ar.caba.codigo-edificacion";
    const double Epsilon = 1e-9;

    sealed class Group(string skillId, string pattern)
    {
        public string SkillId { get; } = skillId;
        public string Pattern { get; } = pattern;
        public double? Area { get; set; }
        public double? Side { get; set; }
        public List<string> Parts { get; } = new();
        public List<SkillEffectiveRule> Rules { get; } = new();
    }

    public static TypologyRuleSet From(SkillResolution resolution, ProjectBrief brief, PrototypeLot lot)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(lot);
        var names = brief.Rooms.Select(room => room.Name).ToArray();
        var refusals = new List<PlanConstraint>();
        var groups = new List<Group>();
        var byKey = new Dictionary<string, Group>(StringComparer.Ordinal);
        SkillEffectiveRule? corridor = null;
        foreach (var effective in resolution.Rules)
        {
            var rule = effective.Rule;
            var check = rule.Check;
            if (rule.Severity != SkillSeverity.Error || check.Op != SkillCheckOp.Min || (check.Property != AreaProperty && check.Property != SideProperty)) continue;
            if (rule.Target.RoomPattern is not { } pattern) continue;
            var subject = effective.QualifiedId;
            var rejected = false;
            if (pattern.Contains('?', StringComparison.Ordinal))
            {
                refusals.Add(Refusal(TypologyRuleCodes.PatternUnsupported, subject, "room glob " + pattern + " holds '?', which the planner reads as a one-character wildcard"));
                rejected = true;
            }
            if (effective.Threshold is not { Kind: SkillValueKind.Number } threshold || !(double.IsFinite(threshold.Number) && threshold.Number > 0))
            {
                refusals.Add(Refusal(TypologyRuleCodes.ThresholdInvalid, subject, check.Property + " minimum " + (effective.Threshold?.ToString() ?? "missing") + "; a positive finite number is required"));
                rejected = true;
            }
            if (rejected || effective.Threshold is null) continue;
            var value = effective.Threshold.Number;
            if (check.Property == SideProperty && CorridorNames.Any(rule.Target.MatchesRoom) && (corridor is null || value > corridor.Threshold!.Number)) corridor = effective;
            if (!names.Any(rule.Target.MatchesRoom)) continue;
            var key = effective.SkillId + "\n" + pattern;
            if (!byKey.TryGetValue(key, out var group))
            {
                group = new Group(effective.SkillId, pattern);
                byKey[key] = group;
                groups.Add(group);
            }
            if (check.Property == AreaProperty) group.Area = Math.Max(group.Area ?? 0, value);
            else group.Side = Math.Max(group.Side ?? 0, value);
            group.Parts.Add(rule.Id + (effective.ThresholdSource is { } source ? " (" + source.Key + " " + SpacePlanSerializer.Format(value) + ")" : ""));
            group.Rules.Add(effective);
        }

        var rules = new List<PlannerRule>();
        var traces = new List<TypologyRuleTrace>();
        foreach (var group in groups)
        {
            var reference = group.SkillId + " " + string.Join(" ", group.Parts);
            foreach (var effective in group.Rules)
                traces.Add(new TypologyRuleTrace(rules.Count, effective.Rule.Check.Property, effective.Threshold!.Number, effective.Source, effective.ThresholdSource, reference));
            rules.Add(new PlannerRule(NamePattern: group.Pattern, MinAreaSquareMetres: group.Area, MinSideMetres: group.Side, Reference: reference));
        }
        foreach (var template in PrototypeTemplates.Rules(lot).Rules.Where(rule => rule.RoomId is not null))
        {
            var planned = template;
            SkillTrace? source = null;
            SkillTrace? thresholdSource = null;
            if (template.RoomId == "hall")
            {
                if (corridor is not null)
                {
                    source = corridor.Source;
                    thresholdSource = corridor.ThresholdSource;
                    var side = corridor.Threshold!.Number;
                    // The CABA pack's corridor value 1.00 m is the general case of RT-030809-020309-01 sec. 2; with it the
                    // template keeps the code's narrow-parcel 0.95 m below 8.66 m. Any other composed value is used as is.
                    if (!(corridor.SkillId == Caba && side == PrototypeTemplates.CorridorMinSideMetres))
                        planned = new PlannerRule(RoomId: "hall", MinSideMetres: side, Reference: corridor.SkillId + " " + corridor.Rule.Id
                            + (corridor.ThresholdSource is { } parameter ? " (" + parameter.Key + " " + side.ToString("0.00", CultureInfo.InvariantCulture) + " m)" : ""));
                }
                else
                {
                    var narrow = lot.WidthMetres < PrototypeTemplates.NarrowParcelWidthMetres;
                    planned = new PlannerRule(RoomId: "hall", MinSideMetres: narrow ? PrototypeTemplates.NarrowParcelCorridorMinSideMetres : PrototypeTemplates.CorridorMinSideMetres,
                        Reference: "prototype template hall minimum side (no composed corridor rule)");
                }
            }
            var reference = planned.Reference ?? "";
            if (planned.MinAreaSquareMetres is { } area) traces.Add(new TypologyRuleTrace(rules.Count, AreaProperty, area, source, thresholdSource, reference));
            if (planned.MinSideMetres is { } minimum) traces.Add(new TypologyRuleTrace(rules.Count, SideProperty, minimum, source, thresholdSource, reference));
            rules.Add(planned);
        }

        // A rule minimum (area, or the square of a side) above a programme room's maximum cannot be planned: refuse it as
        // the P5-07 programme conflict instead of letting the planner come back infeasible.
        foreach (var room in brief.Rooms)
        {
            if (room.MaxAreaSquareMetres is not { } maximum) continue;
            double area = 0, side = 0;
            string areaSource = "", sideSource = "";
            foreach (var rule in rules)
            {
                if (!Matches(rule, room)) continue;
                if (rule.MinAreaSquareMetres is { } a && a > area) { area = a; areaSource = rule.Reference ?? ""; }
                if (rule.MinSideMetres is { } b && b > side) { side = b; sideSource = rule.Reference ?? ""; }
            }
            var conflicts = new List<string>();
            if (area > maximum.Value + Epsilon)
                conflicts.Add("minimum area " + SpacePlanSerializer.Format(area) + " m2 (" + areaSource + ") above programme maximum " + SpacePlanSerializer.Format(maximum.Value) + " m2");
            if (side * side > maximum.Value + Epsilon)
                conflicts.Add("minimum side " + SpacePlanSerializer.Format(side) + " m (" + sideSource + ") needs " + SpacePlanSerializer.Format(side * side) + " m2, above programme maximum "
                    + SpacePlanSerializer.Format(maximum.Value) + " m2");
            if (conflicts.Count > 0) refusals.Add(Refusal(SkillTypologyCodes.MinimumConflict, room.Id, string.Join("; ", conflicts)));
        }
        return new TypologyRuleSet(new PlannerRules(rules.AsReadOnly()), traces.AsReadOnly(), refusals.AsReadOnly());
    }

    public static TypologyRulesResult Generate(SkillResolution resolution, TypologyBriefRequest request)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(request);
        TypologyRuleSet? ruleSet = null;
        var result = TypologyBrief.Plan(SkillTypology.Resolve(resolution, request.FamilySize, request.Include), request, (brief, lot) => ruleSet = From(resolution, brief, lot));
        return new TypologyRulesResult(result, ruleSet);
    }

    static bool Matches(PlannerRule rule, BriefRoom room) =>
        (rule.RoomId is null || string.Equals(rule.RoomId, room.Id, StringComparison.Ordinal)) && (rule.NamePattern is null || new SkillTarget(null, rule.NamePattern).MatchesRoom(room.Name));

    static PlanConstraint Refusal(string code, string subject, string detail) => new(code, subject, true, false, detail);
}
