using Bim.Core;
namespace Bim.Generative;

// Prototype pipeline v1 (P7-07): one opt-in entry point that chains the existing generative stages, prototype (P7-03)
// -> circulation (P7-04) -> facade openings (P7-05) -> BIM-IR windows (P7-06), and ranks the alternatives that pass
// every gate. PrototypePipeline runs it, PrototypePipelineSerializer writes the canonical form. Nothing here changes
// the stages it composes (PrototypeGenerator output and digests are unchanged), the BIM-IR schema or any persisted
// format; the BIM-IR projects it returns are proposals that stay in memory unless the caller commits them.
public enum PrototypePipelineStatus { Ranked, Infeasible, Refused }

// The gate that rejected an alternative. Prototype: AlternativeRanker did not rank the plan (infeasible, duplicate
// geometry, empty, invalid rectangle, non-finite score). Circulation: the P7-04 circulation model is not valid (an
// unreachable room, a bedroom or dry room reachable only through a bedroom, no entrance) or the circulation score is not
// finite. Facade: the P7-05 facade layout is not complete, i.e. some habitable room cannot meet its illumination or
// ventilation ratio (no exterior edge, edge too short, party walls only, unsupported rule). FacadeIr: the prototype
// BIM-IR could not be compiled (IntentCompiler refusal such as wall_clash) or a window could not be hosted (P7-06).
public enum PrototypePipelineGate { Prototype, Circulation, Facade, FacadeIr }

// One typed reason of a rejection: the gate, and the code, subject and detail of the failed check of that stage (a
// PlanConstraint, an IntentIssue or the stage's reason string split into code@subject: detail).
public sealed record PrototypePipelineRejection(PrototypePipelineGate Gate, string Code, string Subject, string Detail);

// Pack is the code skill pack the facade is sized for (ar.caba.codigo-edificacion for the CABA prototypes); Parameters
// are its run-time overrides ("<skill id>/<parameter>", as FacadePlanner). ProjectId and Revision identify the BIM-IR
// prototype of every alternative (each accepted alternative gets its own project with that identity; the caller picks
// one to commit). CirculationWeight is the seventh ranking criterion's weight (CirculationRanker), always with
// RequireValid: the circulation gate. Circulation, Facade and Ir are the stage options (defaults when null). Snap (P7-08,
// opt-in, off when null) runs PrototypeSnap on every plan before the gates, with the Ir wall thickness: snapped plans go
// through the gates in place of the generated ones, a refused snap rejects its alternative at the prototype gate with the
// prototype_snap_* reasons. With Snap null the result and its canonical JSON are exactly those of P7-07.
public sealed record PrototypePipelineOptions(SkillManifest Pack, Guid ProjectId, int Revision = 0,
    double CirculationWeight = CirculationScoring.DefaultCirculationWeight, CirculationOptions? Circulation = null, FacadeOptions? Facade = null,
    FacadeIrOptions? Ir = null, IReadOnlyDictionary<string, double>? Parameters = null, PrototypeSnapOptions? Snap = null);

// One alternative through the pipeline. Index and Seed are its generation position and seed; Strategy the prototype
// strategy (null when ranking caller-supplied plans). Rank is 1-based among the accepted alternatives and 0 for a
// rejected one; Total and Criteria are the CirculationRanker total and seven criteria (0 and empty when rejected).
// Rejections are empty exactly when the alternative is accepted, and all name the one gate that stopped it. Circulation is
// set from the circulation gate on, Facade from the facade gate on, Ir only for an accepted alternative (its Project is
// the prototype BIM-IR with the windows).
public sealed record PrototypePipelineAlternative(int Index, int Seed, string? Strategy, int Rank, double Total, IReadOnlyList<CriterionScore> Criteria,
    IReadOnlyList<PrototypePipelineRejection> Rejections, SpacePlan Plan, CirculationModel? Circulation, FacadeLayout? Facade, FacadeIrResult? Ir)
{
    public bool Accepted => Rejections.Count == 0;
    public PrototypePipelineGate? RejectedAt => Rejections.Count == 0 ? null : Rejections[0].Gate;
    public Project? Project => Ir?.Project;
}

// Status is Ranked when at least one alternative passed every gate, Infeasible when all were rejected, Refused when the
// request or the options were refused (Refusals lists every problem found; Alternatives is empty). Prototype is the
// PrototypeGenerator result (null when the pipeline ranked caller-supplied plans); Ranking the CirculationRanker set the
// gates started from, unchanged (null when refused before ranking): its ranks are CirculationRanker's raw ranks, counted
// before the facade and BIM-IR gates, so they can differ from the pipeline ranks of Alternatives. Alternatives are accepted ones by rank, then the rejected
// ones in generation order.
public sealed record PrototypePipelineResult(PrototypePipelineStatus Status, PrototypePipelineOptions Options, PrototypeResult? Prototype, AlternativeSet? Ranking,
    IReadOnlyList<PlanConstraint> Refusals, IReadOnlyList<PrototypePipelineAlternative> Alternatives)
{
    public const int CurrentVersion = 1;
    public IReadOnlyList<PrototypePipelineAlternative> Accepted => Alternatives.Where(alternative => alternative.Accepted).ToArray();
    public PrototypePipelineAlternative? Best => Alternatives.FirstOrDefault(alternative => alternative.Accepted);
}

// The result of the BIM-IR compile step of the facade_ir gate (PrototypePipeline.CompilePrototype): the project, or the
// typed reasons it could not be compiled.
internal sealed record PrototypePipelineCompile(Project? Project, IReadOnlyList<PrototypePipelineRejection> Rejections);

public static class PrototypePipelineCodes
{
    // Refusal: an empty project id or a negative revision (IntentCompiler could not compile any prototype).
    public const string ProjectInvalid = "pipeline_project_invalid";
    // facade_ir rejection: IntentCompiler failed with an exception other than IntentException (an internal invariant such
    // as intent_stretch_unbounded); the detail holds its message.
    public const string IrCompileFailed = "pipeline_ir_compile_failed";
    // facade rejection fallback: the layout is not complete but lists no failed check (a safeguard).
    public const string FacadeIncomplete = "pipeline_facade_incomplete";

    public static string GateName(PrototypePipelineGate gate) => gate switch
    {
        PrototypePipelineGate.Prototype => "prototype",
        PrototypePipelineGate.Circulation => "circulation",
        PrototypePipelineGate.Facade => "facade",
        _ => "facade_ir",
    };
}
