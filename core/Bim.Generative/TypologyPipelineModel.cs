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
public sealed record TypologyPipelineResult(TypologyBriefResult Typology, PrototypePipelineResult Pipeline)
{
    public const int CurrentVersion = 1;
    public PrototypePipelineStatus Status => Pipeline.Status;
    public IReadOnlyList<PlanConstraint> Refusals => Pipeline.Refusals;
    public IReadOnlyList<PrototypePipelineAlternative> Alternatives => Pipeline.Alternatives;
    public IReadOnlyList<PrototypePipelineAlternative> Accepted => Pipeline.Accepted;
    public PrototypePipelineAlternative? Best => Pipeline.Best;
}
