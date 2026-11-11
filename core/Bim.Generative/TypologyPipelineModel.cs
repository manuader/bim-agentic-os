using Bim.Core;
namespace Bim.Generative;

// P5-12 typology prototypes through the pipeline gates. TypologyPipeline runs the P5-11 typology candidates
// (TypologyBrief.Generate: a typology pack, a family size and a lot) through the P7-07 gates (prototype -> circulation ->
// facade -> BIM-IR windows) and ranks them as PrototypePipeline does. TypologyPipelineSerializer writes the canonical form.
// Typology is the TypologyBrief result exactly as TypologyBrief.Generate returns it (its brief, candidates and alternative
// set are unchanged); Pipeline is the PrototypePipeline result of its plans (PrototypePipeline.Rank of the brief and the
// plans in generation order, with the request's ranking weights and the given options), its alternatives carrying the
// candidates' strategy (whole_lot). When the typology request or the options are refused, Pipeline is a refused result
// listing every refusal (the typology refusals first, then the option refusals) with no alternatives.
// P5-14: Composed is set (never null) when TypologyPipelineOptions.ComposedRules planned the candidates with the composed
// TypologyRules and null otherwise; ComposedRules and Rules are read from it, so a rule set without the composed-rules
// mode cannot be expressed. Rules is the composed rule set (null when the programme or request was refused before a
// brief existed). Without it the canonical JSON has no rules member.
public sealed record TypologyComposedRules(TypologyRuleSet? Rules);

public sealed record TypologyPipelineResult(TypologyBriefResult Typology, PrototypePipelineResult Pipeline, TypologyComposedRules? Composed = null)
{
    public const int CurrentVersion = 1;
    public bool ComposedRules => Composed is not null;
    public TypologyRuleSet? Rules => Composed?.Rules;
    public PrototypePipelineStatus Status => Pipeline.Status;
    public IReadOnlyList<PlanConstraint> Refusals => Pipeline.Refusals;
    public IReadOnlyList<PrototypePipelineAlternative> Alternatives => Pipeline.Alternatives;
    public IReadOnlyList<PrototypePipelineAlternative> Accepted => Pipeline.Accepted;
    public PrototypePipelineAlternative? Best => Pipeline.Best;
}
