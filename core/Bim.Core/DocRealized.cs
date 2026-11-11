namespace Bim.Core;

// P4-13 realized map for catalogue-made elements (stub: the oracle in tests/doc-regeneration/Realized.cs comes first).
public sealed record DocRealizedStep(string Key, bool Succeeded, IReadOnlyList<string> Created);

public static class DocRealizedCodes
{
    public const string RecordInvalid = "realized_record_invalid";
    public const string AssignmentsInvalid = "realized_assignments_invalid";
    public const string BoundExceeded = "realized_bound_exceeded";
    public const string StepDuplicate = "realized_step_duplicate";
    public const string StepUnknown = "realized_step_unknown";
    public const string StepNotSucceeded = "realized_step_not_succeeded";
    public const string StepNoCreated = "realized_step_no_created";
    public const string SourceInvalid = "realized_source_invalid";
    public const string SourceUnknown = "realized_source_unknown";
    public const string UniqueIdInvalid = "realized_unique_id_invalid";
    public const string UniqueIdDuplicate = "realized_unique_id_duplicate";
}

public sealed class DocRealizedException(string code, string? detail = null) : ArgumentException(code)
{
    public string Code { get; } = code;
    public string? Detail { get; } = detail;
}

public static class DocRealized
{
    public const int MaxRecordBytes = 32 * 1024 * 1024;
    public const int MaxSteps = 1000;
    public const int MaxCreated = 10_000;
    public const int MaxAssignments = 100_000;
    public const int MaxStepKeyChars = 128;

    public static IReadOnlyList<DocRealizedStep> Steps(string record) => Array.Empty<DocRealizedStep>();
    public static IReadOnlyDictionary<string, string> Assignments(string json) => new Dictionary<string, string>();
    public static IReadOnlyDictionary<string, string> Build(Project fixture, IReadOnlyList<DocRealizedStep> steps, IReadOnlyDictionary<string, string> assignments) => new Dictionary<string, string>();
    public static IReadOnlyDictionary<string, string> FromRecord(Project fixture, string record, string assignments) => Build(fixture, Steps(record), Assignments(assignments));
    public static string Canonical(IReadOnlyDictionary<string, string> realized) => "";
}
