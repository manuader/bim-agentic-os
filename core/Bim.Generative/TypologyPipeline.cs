using Bim.Core;
namespace Bim.Generative;

// P5-12 typology prototypes through the pipeline gates: an opt-in entry point that composes P5-11 and P7-07 unchanged.
// 1. TypologyBrief.Generate turns the typology pack (a manifest or a skill resolution), the family size and the lot into
//    the typology brief and its whole_lot candidates (seeds Seed + i), exactly as on its own: its result, canonical JSON
//    and pinned digest are unchanged and are kept as TypologyPipelineResult.Typology.
// 2. Refuse before any gate, listing every problem: the typology refusals as they are (programme issues, lot, count,
//    door width, ranking weights), then the PrototypePipeline option refusals (project identity, circulation and facade
//    weights, circulation, snap, facade pack/options/overrides and BIM-IR options). A refused result has no alternatives.
// 3. Otherwise the candidate plans, in generation order, go through PrototypePipeline.Rank with the brief, the options
//    and the request's ranking weights: the same prototype -> circulation -> facade -> BIM-IR window gates, typed
//    rejections, CirculationRanker ranking and the opt-in P7-08 snap and P7-10 facade criterion. Its refusals (ranking
//    weights, count) refuse the result. The only change is that each alternative carries its candidate's strategy.
// Everything is deterministic and sequential and nothing depends on culture, so the same pack + family size + lot + seed
// and options give the same result and the same canonical JSON (TypologyPipelineSerializer).
// P5-14: the SkillResolution overload with TypologyPipelineOptions { ComposedRules: true } runs step 1 as
// TypologyRules.Generate(resolution, request) instead (the P5-13 composed pack rules plan the candidates; the same
// programme and request refusals, then its rule refusals such as typology_rule_pattern_unsupported and
// typology_minimum_conflict) and keeps the rule set in the result (TypologyPipelineResult.Rules, null when the programme
// or request was refused before a brief existed). Steps 2 and 3 are unchanged. With the option off it is Run(resolution).
// Gaps: without the option the planner rules are the fixed PrototypeTemplates.Rules of TypologyBrief; one
// level; each accepted alternative gets its own BIM-IR with the same project identity; nothing is committed. The
// SkillResolution overload does not check that options.Pack is the code pack composed in the resolution: the facade gate
// always uses options.Pack, whatever the resolution holds.
public static class TypologyPipeline
{
    public static TypologyPipelineResult Run(SkillManifest pack, TypologyBriefRequest request, PrototypePipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Pack);
        return Gate(TypologyBrief.Generate(pack, request), options);
    }

    public static TypologyPipelineResult Run(SkillResolution resolution, TypologyBriefRequest request, PrototypePipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Pack);
        return Gate(TypologyBrief.Generate(resolution, request), options);
    }

    public static TypologyPipelineResult Run(SkillResolution resolution, TypologyBriefRequest request, PrototypePipelineOptions options, TypologyPipelineOptions typology)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Pack);
        ArgumentNullException.ThrowIfNull(typology);
        if (!typology.ComposedRules) return Gate(TypologyBrief.Generate(resolution, request), options);
        var composed = TypologyRules.Generate(resolution, request);
        return Gate(composed.Result, options) with { ComposedRules = true, Rules = composed.Rules };
    }

    static TypologyPipelineResult Gate(TypologyBriefResult typology, PrototypePipelineOptions options)
    {
        var refusals = typology.Refusals.ToList();
        refusals.AddRange(PrototypePipeline.OptionRefusals(options));
        if (refusals.Count > 0 || typology.Brief is null || typology.Alternatives is null)
            return new TypologyPipelineResult(typology, new PrototypePipelineResult(PrototypePipelineStatus.Refused, options, null, null, refusals.AsReadOnly(),
                Array.Empty<PrototypePipelineAlternative>()));
        var plans = typology.Alternatives.Alternatives.OrderBy(alternative => alternative.Index).Select(alternative => alternative.Plan).ToArray();
        var pipeline = PrototypePipeline.Rank(typology.Brief, plans, options, typology.Request.Weights);
        var strategies = new Dictionary<int, string>();
        foreach (var candidate in typology.Candidates) strategies[candidate.Index] = candidate.Strategy;
        var alternatives = pipeline.Alternatives
            .Select(alternative => strategies.TryGetValue(alternative.Index, out var strategy) ? alternative with { Strategy = strategy } : alternative).ToArray();
        return new TypologyPipelineResult(typology, pipeline with { Alternatives = alternatives });
    }
}
