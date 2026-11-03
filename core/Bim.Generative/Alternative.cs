namespace Bim.Generative;

// Alternatives v1: N space plans of one brief from distinct deterministic seeds, deduplicated by geometry, scored by a
// weighted multi-criteria function and ranked. AlternativeGenerator plans the seeds, AlternativeRanker scores and ranks
// any list of plans, AlternativeSetSerializer writes the canonical form.
public enum AlternativeStatus { Ranked, Duplicate, Infeasible }

// Ranked: at least one feasible, unique alternative. Infeasible: every alternative was rejected. Refused: the request
// itself was invalid (count or weights) and nothing was planned.
public enum AlternativeSetStatus { Ranked, Infeasible, Refused }

// Relative weights of the six criteria (AlternativeRanker documents each). Every weight must be finite and >= 0 and
// their sum positive; the ranker divides each by the sum, so only the ratios matter. Defaults sum to 1.
public sealed record ScoringWeights(double AreaFit = 0.30, double Adjacency = 0.25, double Proportions = 0.15, double Compactness = 0.10,
    double Perimeter = 0.10, double Compliance = 0.10);

// One criterion of one alternative. Raw is the measured value in its own unit, Normalized its score in [0, 1] (higher
// is better), Weight the effective weight (given weight / sum of weights) and Contribution = Weight * Normalized.
public sealed record CriterionScore(string Criterion, double Raw, double Normalized, double Weight, double Contribution, string Detail);

// Index is the generation position (0-based) and the stable tie-break. Rank is 1-based for ranked alternatives and 0
// otherwise. Total is the in-order sum of the criteria contributions, at most 1 (0 when not ranked). Reasons explain a rejection:
// the plan's violations for an infeasible alternative, the earlier alternative for a duplicate.
public sealed record Alternative(int Index, int Seed, AlternativeStatus Status, int Rank, double Total, IReadOnlyList<CriterionScore> Criteria,
    IReadOnlyList<string> Reasons, int? DuplicateOfIndex, SpacePlan Plan);

// Alternatives are ordered by rank first, then the unranked ones in generation order.
public sealed record AlternativeSet(AlternativeSetStatus Status, string BriefName, int Requested, ScoringWeights Weights, IReadOnlyList<PlanConstraint> Refusals,
    IReadOnlyList<Alternative> Alternatives)
{
    public const int CurrentVersion = 1;
    public IReadOnlyList<Alternative> Ranked => Alternatives.Where(alternative => alternative.Status == AlternativeStatus.Ranked).ToArray();
}

// Generation request: Count alternatives with seeds BaseSeed + i (i = 0..Count-1, wrapping in 32-bit arithmetic, so
// the seeds are always distinct), planned with Planner options and ranked with Weights (defaults when null).
public sealed record AlternativeOptions(int Count, int BaseSeed = 0, PlannerOptions? Planner = null, ScoringWeights? Weights = null);

public static class AlternativeCodes
{
    // Refusals of the request.
    public const string CountInvalid = "alternatives_count_invalid";
    public const string WeightsInvalid = "weights_invalid";
    // Rejection reasons of single alternatives.
    public const string DuplicateGeometry = "duplicate_geometry";
    public const string PlanEmpty = "plan_empty";
    public const string RectInvalid = "rect_invalid";
    public const string ScoreNotFinite = "score_not_finite";
}
