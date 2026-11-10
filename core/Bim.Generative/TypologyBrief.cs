using System.Globalization;
using Bim.Core;
namespace Bim.Generative;

// P5-11 typology programme into the prototype planner. A typology skill pack (P5-07, for example
// bim.typology.vivienda-unifamiliar) and a family size become the planner input without a hand-written brief:
// 1. SkillTypology.Resolve turns the pack (a manifest or a skill resolution) and the family size, with the requested
//    optional rooms, into a TypologyProgramme whose brief holds the programme rooms (ids are the room kinds, counts per
//    family size, minimum/target/maximum areas) and adjacency wishes. Composed packs (for example the CABA code pack in
//    a skill resolution) are checked only as programme minimum conflicts (typology_minimum_conflict); their rules are
//    not otherwise evaluated here, and the planner applies the fixed CABA minimums of PrototypeTemplates.Rules.
// 2. TypologyBrief.Brief keeps those rooms and adjacencies exactly and adds the lot as the brief site on one level (the
//    name gains the lot). No jurisdiction or budget is added.
// 3. Generate refuses before any planning, listing every problem: the programme issues as they are (typed
//    SkillTypologyCodes: unknown room kind, room not offered, family size outside the pack bounds, non-typology pack,
//    invalid settings, minimum conflicts, failed resolution), then the prototype request checks of PrototypeGenerator
//    (PrototypeCodes lot_invalid, count_invalid, options_invalid and the ranker weight refusals). A refusal is hard and
//    unsatisfied; a refused result has no brief, candidates or alternatives.
// 4. Otherwise it plans Alternatives whole_lot candidates with SpacePlanner (seeds Seed + i, 32-bit wrap-around, the
//    prototype rules PrototypeTemplates.Rules(lot): CABA minimum area and side by room name, the CABA corridor side for
//    the hall and the template laundry side) and ranks them with AlternativeRanker, as PrototypeGenerator does for the
//    apartment templates. Everything is deterministic and sequential, so the same pack settings, family size, requested
//    rooms (in any order), lot, seed and options give the same brief and the same canonical alternative set.
// The apartment typology templates, PrototypeGenerator and its canonical output are not changed. Gaps: no approved
// rooms or walls, one level, no circulation or facade gates (PrototypePipeline.Rank can rank the plans with them).
public sealed record TypologyBriefRequest(int FamilySize, PrototypeLot Lot, int Alternatives = 8, int Seed = 0, IReadOnlyCollection<string>? Include = null,
    ScoringWeights? Weights = null, double DoorWidthMetres = PlannerOptions.DefaultDoorWidthMetres);

// Brief is null and Alternatives null when the request is refused; Programme is always the resolved programme. Request
// is the given request with Include copied into an immutable, ordinal-sorted, distinct list.
public sealed record TypologyBriefResult(PrototypeStatus Status, TypologyBriefRequest Request, TypologyProgramme Programme, ProjectBrief? Brief,
    IReadOnlyList<PlanConstraint> Refusals, IReadOnlyList<PrototypeCandidate> Candidates, AlternativeSet? Alternatives);

public static class TypologyBrief
{
    // The planner brief of a resolved programme on the lot. A refused programme throws ArgumentException; the lot is not
    // range-checked here (Generate refuses out-of-range lots).
    public static ProjectBrief Brief(TypologyProgramme programme, PrototypeLot lot)
    {
        ArgumentNullException.ThrowIfNull(programme);
        ArgumentNullException.ThrowIfNull(lot);
        if (!programme.Succeeded || programme.Brief is null) throw new ArgumentException("the typology programme was refused", nameof(programme));
        var source = programme.Brief;
        var lotSource = new BriefProvenance(BriefSource.User, 1, "prototype lot");
        var site = new BriefSite(new BriefValue<double>(lot.WidthMetres, lotSource), new BriefValue<double>(lot.DepthMetres, lotSource));
        var constraints = source.Constraints with { Site = site, Levels = source.Constraints.Levels ?? new BriefValue<int>(1) };
        var name = source.Name + ", lot " + SpacePlanSerializer.Format(lot.WidthMetres) + " x " + SpacePlanSerializer.Format(lot.DepthMetres) + " m";
        return source with { Name = name, Constraints = constraints };
    }

    public static TypologyBriefResult Generate(SkillManifest pack, TypologyBriefRequest request)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(request);
        return Plan(SkillTypology.Resolve(pack, request.FamilySize, request.Include), request, TemplateRules);
    }

    public static TypologyBriefResult Generate(SkillResolution resolution, TypologyBriefRequest request)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(request);
        return Plan(SkillTypology.Resolve(resolution, request.FamilySize, request.Include), request, TemplateRules);
    }

    // The fixed P5-11 planner rules: PrototypeTemplates.Rules(lot), whatever the brief.
    static readonly Func<ProjectBrief, PrototypeLot, TypologyRuleSet> TemplateRules =
        (_, lot) => new(PrototypeTemplates.Rules(lot), Array.Empty<TypologyRuleTrace>(), Array.Empty<PlanConstraint>());

    static TypologyBriefRequest Canonical(TypologyBriefRequest request) => request.Include is null ? request
        : request with { Include = Array.AsReadOnly(request.Include.Distinct(StringComparer.Ordinal).OrderBy(kind => kind, StringComparer.Ordinal).ToArray()) };

    // The shared request path of TypologyBrief.Generate and TypologyRules.Generate: programme and request refusals first;
    // only when there are none is the brief built and the planner rule set asked for (its refusals refuse the result).
    internal static TypologyBriefResult Plan(TypologyProgramme programme, TypologyBriefRequest request, Func<ProjectBrief, PrototypeLot, TypologyRuleSet> rules)
    {
        request = Canonical(request);
        var refusals = programme.Issues.Select(issue => Refusal(issue.Code, issue.Subject, issue.Detail)).ToList();
        var lot = request.Lot;
        var lotValid = lot is not null && LotSide(lot.WidthMetres) && LotSide(lot.DepthMetres);
        if (!lotValid)
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
        if (refusals.Count > 0 || lot is null)
            return new TypologyBriefResult(PrototypeStatus.Refused, request, programme, null, refusals, Array.Empty<PrototypeCandidate>(), null);

        var brief = Brief(programme, lot);
        var ruleSet = rules(brief, lot);
        if (!ruleSet.Succeeded)
            return new TypologyBriefResult(PrototypeStatus.Refused, request, programme, null, ruleSet.Refusals, Array.Empty<PrototypeCandidate>(), null);
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
        return new TypologyBriefResult(status, request, programme, brief, Array.Empty<PlanConstraint>(), candidates, set);
    }

    static bool LotSide(double value) => double.IsFinite(value) && value >= PrototypeGenerator.MinLotSideMetres && value <= PrototypeGenerator.MaxLotSideMetres;

    static PlanConstraint Refusal(string code, string subject, string detail) => new(code, subject, true, false, detail);

    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
