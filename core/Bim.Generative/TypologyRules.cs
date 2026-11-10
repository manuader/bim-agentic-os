using Bim.Core;
namespace Bim.Generative;

// P5-13 composed pack rules for the typology planner (stub; implementation follows the red test run).
public sealed record TypologyRuleTrace(int PlannerRuleIndex, string Property, double Threshold, SkillTrace? Rule, SkillTrace? ThresholdSource, string Reference);

public sealed record TypologyRuleSet(PlannerRules Rules, IReadOnlyList<TypologyRuleTrace> Traces, IReadOnlyList<PlanConstraint> Refusals)
{
    public bool Succeeded => Refusals.Count == 0;
}

public sealed record TypologyRulesResult(TypologyBriefResult Result, TypologyRuleSet? Rules);

public static class TypologyRuleCodes
{
    public const string PatternUnsupported = "typology_rule_pattern_unsupported";
    public const string ThresholdInvalid = "typology_rule_threshold_invalid";
}

public static class TypologyRules
{
    public const string AreaProperty = "area_m2";
    public const string SideProperty = "min_side_m";

    public static TypologyRuleSet From(SkillResolution resolution, ProjectBrief brief, PrototypeLot lot)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(lot);
        return new TypologyRuleSet(PrototypeTemplates.Rules(lot), Array.Empty<TypologyRuleTrace>(), Array.Empty<PlanConstraint>());
    }

    public static TypologyRulesResult Generate(SkillResolution resolution, TypologyBriefRequest request)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(request);
        var result = TypologyBrief.Generate(resolution, request);
        return new TypologyRulesResult(result, result.Brief is null ? null : From(resolution, result.Brief, request.Lot));
    }
}
