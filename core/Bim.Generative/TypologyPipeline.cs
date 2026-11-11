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
// or request was refused before a brief existed). Step 2 also refuses, after the option refusals,
// typology_pipeline_pack_mismatch when options.Pack (the facade gate's code pack) is not, by id and version, a skill of
// the resolution. In step 3, a snap pre-pass without its own rules (options.Snap { Rules: null }, which would check the
// snapped rooms against the fixed PrototypeTemplates.Rules) checks them against the composed planner rules instead, so
// the pipeline result's options (and their JSON) carry those snap rules. With the option off it is Run(resolution).
// P5-15: RunComposed(pack, request, options, skills, precedence) is the composed-rules path for callers without a
// SkillResolution. A pack that is not of kind typology is refused up front, as Run(SkillManifest, ...) refuses it: the
// TypologyBrief.Generate(pack, request) refusals (typology_kind_mismatch on the pack id, then the request refusals), then
// the option refusals, nothing resolved. Otherwise it builds a catalog of exactly the typology pack, options.Pack (the
// facade gate's code pack) and the extra manifests, requests every one of them ("*") and resolves them with SkillResolver,
// forwarding the optional precedence order (earlier wins among equal priorities; without it equal-priority skills with
// different values for one default key are a default_conflict), then runs Run(resolution, request, options,
// { ComposedRules: true }), so a successful resolution gives exactly that overload's result and bytes (CABA + typology
// keeps the P5-14 pin). Manifests with one id, one version and equal contents (the same reference, a `with` copy or a
// re-parse) count once, the first kept; a null extra is a programming error. Manifests with one id and other contents
// (another version or not) are refused as one id_duplicate (SkillCodes) per id, subject the id, the versions listed in
// SkillVersion order, instead of throwing. A failed resolution refuses the result with each resolution issue passed
// through typed (its own SkillCodes code, subject and detail, hard and unsatisfied), then the request refusals, then the
// PrototypePipeline option refusals; there is no pack mismatch check (the code pack is always composed). Typology is the
// TypologyRules.Generate result of the failed resolution (programme pack id ""): its own Refusals keep the
// typology_resolution_failed summary, and only the pipeline refusals (TypologyPipelineResult.Refusals) drop it, since
// the typed issues replace it. Every refused RunComposed result has Composed set with a null rule set and no alternatives.
// Gaps: without the option the planner rules are the fixed PrototypeTemplates.Rules of TypologyBrief; one
// level; each accepted alternative gets its own BIM-IR with the same project identity; nothing is committed. The
// SkillResolution overload does not check that options.Pack is the code pack composed in the resolution: the facade gate
// always uses options.Pack, whatever the resolution holds (refused in composed-rules mode when its id and version are
// not in the resolution; a same id and version with other contents is not detected).
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
        var pack = options.Pack;
        var extra = resolution.Skills.Any(skill => string.Equals(skill.Id, pack.Id, StringComparison.Ordinal) && skill.Version == pack.Version)
            ? Array.Empty<PlanConstraint>()
            : new[] { new PlanConstraint(TypologyPipelineCodes.PackMismatch, "pack", true, false, "options pack " + pack.Id + " " + pack.Version.ToString()
                + " is not a skill of the resolution whose rules plan the candidates; the facade gate must use a composed code pack") };
        var gated = options.Snap is { Rules: null } snap && composed.Rules is { Succeeded: true } set ? options with { Snap = snap with { Rules = set.Rules } } : options;
        return Gate(composed.Result, gated, extra) with { Composed = new TypologyComposedRules(composed.Rules) };
    }

    public static TypologyPipelineResult RunComposed(SkillManifest pack, TypologyBriefRequest request, PrototypePipelineOptions options, IReadOnlyList<SkillManifest>? skills = null,
        IReadOnlyList<string>? precedence = null)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Pack);
        if (pack.Kind != SkillKind.Typology)
        {
            var refused = TypologyBrief.Generate(pack, request);
            var kindRefusals = refused.Refusals.ToList();
            kindRefusals.AddRange(PrototypePipeline.OptionRefusals(options));
            return new TypologyPipelineResult(refused, new PrototypePipelineResult(PrototypePipelineStatus.Refused, options, null, null, kindRefusals.AsReadOnly(),
                Array.Empty<PrototypePipelineAlternative>()), new TypologyComposedRules(null));
        }
        var manifests = new List<SkillManifest>();
        void Add(SkillManifest manifest)
        {
            if (!manifests.Any(known => SameManifest(known, manifest))) manifests.Add(manifest);
        }
        Add(pack);
        Add(options.Pack);
        if (skills is not null)
        {
            foreach (var skill in skills)
            {
                ArgumentNullException.ThrowIfNull(skill, nameof(skills));
                Add(skill);
            }
        }
        var duplicates = manifests.GroupBy(manifest => manifest.Id, StringComparer.Ordinal).Where(group => group.Count() > 1).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new SkillIssue(SkillCodes.IdDuplicate, group.Key, "skill " + group.Key + " is passed as " + group.Count().ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " manifests with different contents (versions " + string.Join(", ", group.Select(manifest => manifest.Version).Order().Select(version => version.ToString()))
                + "); pass each skill once"))
            .ToArray();
        var resolution = duplicates.Length > 0
            ? new SkillResolution(Array.Empty<SkillManifest>(), Array.Empty<SkillSetting>(), Array.Empty<SkillEffectiveParameter>(), Array.Empty<SkillEffectiveRule>(), duplicates)
            : SkillResolver.Resolve(new SkillCatalog(manifests), manifests.Select(manifest => new SkillRequest(manifest.Id)).ToArray(), precedence);
        if (resolution.Succeeded) return Run(resolution, request, options, new TypologyPipelineOptions(ComposedRules: true));

        var typology = TypologyRules.Generate(resolution, request);
        var refusals = resolution.Issues.Select(issue => new PlanConstraint(issue.Code, issue.Subject, true, false, issue.Detail)).ToList();
        refusals.AddRange(typology.Result.Refusals.Where(refusal => refusal.Code != SkillTypologyCodes.ResolutionFailed));
        refusals.AddRange(PrototypePipeline.OptionRefusals(options));
        return new TypologyPipelineResult(typology.Result, new PrototypePipelineResult(PrototypePipelineStatus.Refused, options, null, null, refusals.AsReadOnly(),
            Array.Empty<PrototypePipelineAlternative>()), new TypologyComposedRules(typology.Rules));
    }

    // Equal manifest contents, member by member (SkillManifest's record equality compares its lists by reference).
    static bool SameManifest(SkillManifest a, SkillManifest b) =>
        ReferenceEquals(a, b) || (a.ManifestVersion == b.ManifestVersion && string.Equals(a.Id, b.Id, StringComparison.Ordinal) && a.Version == b.Version && a.Kind == b.Kind
            && string.Equals(a.Description, b.Description, StringComparison.Ordinal) && a.Priority == b.Priority
            && a.Requires.Select(requirement => requirement.Id + " " + requirement.Range.Text).SequenceEqual(b.Requires.Select(requirement => requirement.Id + " " + requirement.Range.Text), StringComparer.Ordinal)
            && a.ConflictsWith.SequenceEqual(b.ConflictsWith, StringComparer.Ordinal) && a.Parameters.SequenceEqual(b.Parameters) && a.Defaults.SequenceEqual(b.Defaults)
            && a.Rules.Count == b.Rules.Count && a.Rules.Zip(b.Rules).All(pair => SameRule(pair.First, pair.Second)));

    static bool SameRule(SkillRule a, SkillRule b) =>
        (a with { Check = b.Check }) == b && (a.Check with { Values = null }) == (b.Check with { Values = null })
        && (a.Check.Values ?? Array.Empty<SkillValue>()).SequenceEqual(b.Check.Values ?? Array.Empty<SkillValue>()) && (a.Check.Values is null) == (b.Check.Values is null);

    static TypologyPipelineResult Gate(TypologyBriefResult typology, PrototypePipelineOptions options, IReadOnlyList<PlanConstraint>? extra = null)
    {
        var refusals = typology.Refusals.ToList();
        refusals.AddRange(PrototypePipeline.OptionRefusals(options));
        if (extra is not null) refusals.AddRange(extra);
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
