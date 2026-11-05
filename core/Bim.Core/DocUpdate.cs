namespace Bim.Core;

// P4-07 documentation update pipeline (red stub: the API only).
public sealed record DocUpdatePlan(DocumentationSet Before, DocumentationSet After, DocRegenerationPlan Regeneration, DocReplacementPlan Replacement,
    IReadOnlyList<string> Forced, IReadOnlyList<string> Retired, IReadOnlyDictionary<string, string> Realized);

public sealed record DocUpdateResult(DocUpdatePlan Plan, RevitPlanRunReport Report, IReadOnlyDictionary<string, string>? Realized);

public static class DocUpdateCodes
{
    public const string Retired = "doc_retired";
}

public static class DocUpdate
{
    public static DocUpdatePlan Plan(Project before, Project after, RevitBindings bindings, IReadOnlyDictionary<string, string> realized, IReadOnlyDictionary<string, IReadOnlyList<string>>? dependents = null) =>
        throw new NotImplementedException();

    public static DocUpdateResult Run(DocUpdatePlan plan, IRevitCommandTransport transport) => throw new NotImplementedException();

    public static DocUpdateResult Resume(DocUpdatePlan plan, RevitPlanRunReport report, IRevitCommandTransport transport) => throw new NotImplementedException();

    public static DocUpdateResult Update(Project before, Project after, RevitBindings bindings, IReadOnlyDictionary<string, string> realized, IRevitCommandTransport transport,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? dependents = null) => throw new NotImplementedException();
}
