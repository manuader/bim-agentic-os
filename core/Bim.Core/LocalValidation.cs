namespace Bim.Core;

// P3-04 localized validation (signatures only; implementation follows).
public sealed record LocalValidationResult(ValidationIssue[] Findings, Guid[] Affected, Guid[] Revalidated, int TotalElements)
{
    public int RevalidatedCount => Revalidated.Length;
}

public static class LocalValidation
{
    public static ValidationIssue[] ValidateAll(Project project) => throw new NotImplementedException();
    public static LocalValidationResult Validate(Project before, Project after, ExecutionPlan plan) => throw new NotImplementedException();
    public static ValidationIssue[] Merge(IEnumerable<ValidationIssue> previous, LocalValidationResult local) => throw new NotImplementedException();
    public static bool Agrees(Project before, Project after, ExecutionPlan plan) => throw new NotImplementedException();
}
