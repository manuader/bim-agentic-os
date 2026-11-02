namespace Bim.Core;

// Stub: semantic checks on a project brief (not yet implemented).
public static class BriefValidator
{
    public static BriefIssue[] Validate(ProjectBrief brief) => brief is null ? Array.Empty<BriefIssue>() : Array.Empty<BriefIssue>();

    public static bool HasErrors(IEnumerable<BriefIssue> issues) => issues.Any(issue => issue.Severity == BriefSeverity.Warning);
}
