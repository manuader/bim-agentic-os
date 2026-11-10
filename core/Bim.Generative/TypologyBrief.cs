using Bim.Core;
namespace Bim.Generative;

// P5-11 typology programme into the prototype planner (stub).
public sealed record TypologyBriefRequest(int FamilySize, PrototypeLot Lot, int Alternatives = 8, int Seed = 0, IReadOnlyCollection<string>? Include = null,
    ScoringWeights? Weights = null, double DoorWidthMetres = PlannerOptions.DefaultDoorWidthMetres);

public sealed record TypologyBriefResult(PrototypeStatus Status, TypologyBriefRequest Request, TypologyProgramme Programme, ProjectBrief? Brief,
    IReadOnlyList<PlanConstraint> Refusals, IReadOnlyList<PrototypeCandidate> Candidates, AlternativeSet? Alternatives);

public static class TypologyBrief
{
    public static ProjectBrief Brief(TypologyProgramme programme, PrototypeLot lot)
    {
        ArgumentNullException.ThrowIfNull(programme);
        ArgumentNullException.ThrowIfNull(lot);
        return programme.Brief ?? throw new ArgumentException("the typology programme was refused", nameof(programme));
    }

    public static TypologyBriefResult Generate(SkillManifest pack, TypologyBriefRequest request)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(request);
        return Stub(SkillTypology.Resolve(pack, request.FamilySize, request.Include), request);
    }

    public static TypologyBriefResult Generate(SkillResolution resolution, TypologyBriefRequest request)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(request);
        return Stub(SkillTypology.Resolve(resolution, request.FamilySize, request.Include), request);
    }

    static TypologyBriefResult Stub(TypologyProgramme programme, TypologyBriefRequest request) =>
        new(PrototypeStatus.Refused, request, programme, null, Array.Empty<PlanConstraint>(), Array.Empty<PrototypeCandidate>(), null);
}
