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
// P5-14: ComposedRules is true when TypologyPipelineOptions.ComposedRules planned the candidates with the composed
// TypologyRules; Rules is then that rule set (null when the programme or request was refused before a brief existed).
// Both stay false/null otherwise, and the canonical JSON then has no rules member.
public sealed record TypologyPipelineResult(TypologyBriefResult Typology, PrototypePipelineResult Pipeline, bool ComposedRules = false, TypologyRuleSet? Rules = null)
{
    public const int CurrentVersion = 1;
    public PrototypePipelineStatus Status => Pipeline.Status;
    public IReadOnlyList<PlanConstraint> Refusals => Pipeline.Refusals;
    public IReadOnlyList<PrototypePipelineAlternative> Alternatives => Pipeline.Alternatives;
    public IReadOnlyList<PrototypePipelineAlternative> Accepted => Pipeline.Accepted;
    public PrototypePipelineAlternative? Best => Pipeline.Best;
}
