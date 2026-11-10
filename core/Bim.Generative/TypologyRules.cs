using System.Globalization;
using Bim.Core;
namespace Bim.Generative;

// P5-13 composed pack rules constrain the typology planner (opt-in; TypologyBrief and PrototypeTemplates are unchanged).
// TypologyRules.From turns the effective rules of a composed skill resolution (for example ar.caba.codigo-edificacion +
// bim.typology.vivienda-unifamiliar + a style or a stricter code pack) into the SpacePlanner rules of a typology brief:
// 1. Candidate rules are the effective rules with severity error, op min, property area_m2 or min_side_m and a room
//    target (warnings and info are recommendations, not planner constraints; ratios, element rules and other properties
//    are not planner rules). Every candidate is checked first: a room glob holding '?' (a literal for SkillTarget, a
//    one-character wildcard for the planner) is typology_rule_pattern_unsupported and a threshold that is not a positive
//    finite number is typology_rule_threshold_invalid (subject skill/rule); refusals are hard and listed together.
// 2. Candidates whose room glob matches no brief room name are dropped. The rest are grouped by (skill id, room glob) in
//    resolution order (skills in resolver order, rules in pack order); each group is one PlannerRule with that glob, the
//    largest area and largest side minimum of the group, and Reference "<skill id> <rule id> <rule id>", a parameter
//    threshold adding " (<parameter> <value>)" after its rule id. That is the PrototypeTemplates.CabaRules format, so a
//    pack whose minimum rules equal CabaRules gives exactly CabaRules. The planner applies the largest minimum of every
//    matching rule, so a stricter composed pack wins over the code pack on the same rooms.
// 3. The template room-id rules of PrototypeTemplates.Rules(lot) follow: the hall corridor side (the typology hall is
//    named Hall, which the CABA corredor*/pasillo*/corridor* globs do not match) and the laundry side.
// Traces give one entry per pack rule and per template rule minimum: the planner rule index, the property, the threshold,
// the skill rule (null for a template rule), the parameter source and the planner rule reference.
// TypologyRules.Generate is TypologyBrief.Generate(resolution, request) with these rules instead of the fixed
// PrototypeTemplates.Rules(lot): same programme and request refusals in the same order, then the rule refusals; same
// seeds, ranking and determinism. Composed area minimums above a programme minimum stay the P5-07 refusal
// typology_minimum_conflict, so composed area rules reach the planner only at or below the programme minimum; side rules
// (and the area their square implies) are what change plans.
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
            if (rejected || effective.Threshold is null || !names.Any(rule.Target.MatchesRoom)) continue;
            var value = effective.Threshold.Number;
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
            var reference = template.Reference ?? "";
            if (template.MinAreaSquareMetres is { } area) traces.Add(new TypologyRuleTrace(rules.Count, AreaProperty, area, null, null, reference));
            if (template.MinSideMetres is { } side) traces.Add(new TypologyRuleTrace(rules.Count, SideProperty, side, null, null, reference));
            rules.Add(template);
        }
        return new TypologyRuleSet(new PlannerRules(rules.AsReadOnly()), traces.AsReadOnly(), refusals.AsReadOnly());
    }

    public static TypologyRulesResult Generate(SkillResolution resolution, TypologyBriefRequest request)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(request);
        var programme = SkillTypology.Resolve(resolution, request.FamilySize, request.Include);
        request = Canonical(request);
        var refusals = programme.Issues.Select(issue => Refusal(issue.Code, issue.Subject, issue.Detail)).ToList();
        var lot = request.Lot;
        if (!(lot is not null && LotSide(lot.WidthMetres) && LotSide(lot.DepthMetres)))
            refusals.Add(Refusal(PrototypeCodes.LotInvalid, "lot", lot is null ? "a lot is required"
                : "lot " + SpacePlanSerializer.Format(lot.WidthMetres) + " x " + SpacePlanSerializer.Format(lot.DepthMetres) + " m; each side must be finite and between "
                    + SpacePlanSerializer.Format(PrototypeGenerator.MinLotSideMetres) + " and " + SpacePlanSerializer.Format(PrototypeGenerator.MaxLotSideMetres) + " m"));
        if (request.Alternatives < PrototypeGenerator.MinAlternatives || request.Alternatives > PrototypeGenerator.MaxAlternatives)
            refusals.Add(Refusal(PrototypeCodes.CountInvalid, "alternatives", Text(request.Alternatives) + " alternatives requested; between " + Text(PrototypeGenerator.MinAlternatives)
                + " and " + Text(PrototypeGenerator.MaxAlternatives) + " are generated"));
        var door = request.DoorWidthMetres;
        if (!(double.IsFinite(door) && door > 0)) refusals.Add(Refusal(PrototypeCodes.OptionsInvalid, "door_width_m", "positive finite door width required"));
        var weights = request.Weights ?? new ScoringWeights();
        refusals.AddRange(AlternativeRanker.Refusals(AlternativeGenerator.MinCount, weights));
        if (refusals.Count > 0 || lot is null) return new TypologyRulesResult(Refused(request, programme, refusals), null);

        var brief = TypologyBrief.Brief(programme, lot);
        var ruleSet = From(resolution, brief, lot);
        if (!ruleSet.Succeeded) return new TypologyRulesResult(Refused(request, programme, ruleSet.Refusals), ruleSet);
        var options = new PlannerOptions(door, null, ruleSet.Rules);
        var plans = new List<SpacePlan>();
        var candidates = new List<PrototypeCandidate>();
        for (var index = 0; index < request.Alternatives; index++)
        {
            var seed = unchecked(request.Seed + index);
            plans.Add(SpacePlanner.Plan(brief, seed, options));
            candidates.Add(new PrototypeCandidate(index, PrototypeStrategies.WholeLot, seed, null, null));
        }
        var set = AlternativeRanker.Rank(brief, plans, weights);
        var status = set.Status == AlternativeSetStatus.Ranked ? PrototypeStatus.Ranked : PrototypeStatus.Infeasible;
        return new TypologyRulesResult(new TypologyBriefResult(status, request, programme, brief, Array.Empty<PlanConstraint>(), candidates, set), ruleSet);
    }

    static TypologyBriefResult Refused(TypologyBriefRequest request, TypologyProgramme programme, IReadOnlyList<PlanConstraint> refusals) =>
        new(PrototypeStatus.Refused, request, programme, null, refusals, Array.Empty<PrototypeCandidate>(), null);

    static TypologyBriefRequest Canonical(TypologyBriefRequest request) => request.Include is null ? request
        : request with { Include = Array.AsReadOnly(request.Include.Distinct(StringComparer.Ordinal).OrderBy(kind => kind, StringComparer.Ordinal).ToArray()) };

    static bool LotSide(double value) => double.IsFinite(value) && value >= PrototypeGenerator.MinLotSideMetres && value <= PrototypeGenerator.MaxLotSideMetres;

    static PlanConstraint Refusal(string code, string subject, string detail) => new(code, subject, true, false, detail);

    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
