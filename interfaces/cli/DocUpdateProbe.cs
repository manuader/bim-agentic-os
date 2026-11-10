#nullable enable
using Bim.Core;

namespace Bim.Cli;

// P4-10 stub (red commit): the transport half of doc-update --probe-dependents, not implemented yet.
internal sealed record DocUpdateRevit(Func<RevitCommandPolicy> Policy, Func<string, string, string?> Pipe)
{
    public static DocUpdateRevit Real { get; } = new(() => RevitCommandPolicy.None, (_, _) => null);
}

internal static class DocUpdateProbe
{
    public static (string Code, string? Field)? PipeProblem(string pipeName) => null;

    public static DocUpdateDependentsResult Plan(Project before, Project after, RevitBindings bindings, IReadOnlyDictionary<string, string> realized,
        string pipeName, IReadOnlyCollection<string> accepted, DocUpdateRevit revit) => throw new NotImplementedException("P4-10 probe not implemented");
}
