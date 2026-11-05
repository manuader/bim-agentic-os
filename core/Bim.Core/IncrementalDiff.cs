namespace Bim.Core;

// P3-05 incremental diff from a bounded edit set (stub: red phase).
public sealed record ElementEdit(string Action, Guid Id, Element? Element)
{
    public static ElementEdit Create(Element element) => new("create", element.Id, element);
    public static ElementEdit Update(Element element) => new("update", element.Id, element);
    public static ElementEdit Delete(Guid id) => new("delete", id, null);
}

public sealed record IncrementalWork(int Edits, int Serialized, int Visited, int TotalElements, long HashedBytes);

public sealed record IncrementalDiffResult(Project After, ExecutionPlan Plan, IncrementalWork Work);

public sealed class IncrementalDiffIndex
{
    IncrementalDiffIndex(Project before) { Before = before; }
    public Project Before { get; }
    public static IncrementalDiffIndex Build(Project before) => new(before);
    public IncrementalDiffResult Diff(IReadOnlyList<ElementEdit> edits, int maxAffected = int.MaxValue) => throw new NotImplementedException("incremental_diff");
}

public static class IncrementalDiff
{
    public const int MaxEdits = 1024;
    public const string InvalidEdit = "edit_invalid";
    public const string UnknownId = "edit_unknown_id";
    public const string DuplicateId = "edit_duplicate_id";
    public const string CreateExists = "edit_create_exists";
    public const string BoundExceeded = "edit_bound_exceeded";

    public static IncrementalDiffResult Diff(Project before, IReadOnlyList<ElementEdit> edits, int maxAffected = int.MaxValue) =>
        IncrementalDiffIndex.Build(before).Diff(edits, maxAffected);
}
