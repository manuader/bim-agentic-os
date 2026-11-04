namespace Bim.Core;

// P2-07 drift resolution plan (stub; implementation follows the failing oracle).

public static class MirrorDriftResolveActions
{
    public const string Adopt = "adopt";
    public const string Restore = "restore";
    public const string None = "none";
}

public static class MirrorDriftResolveStatus
{
    public const string Proposed = "proposed";
    public const string Unsupported = "unsupported";
}

public static class MirrorDriftAdoptActions
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";
}

public static class MirrorDriftRestoreModes
{
    public const string Create = "create";
    public const string Delete = "delete";
    public const string SetParameter = "set_parameter";
    public const string Replace = "replace";
}

public static class MirrorDriftResolveCodes
{
    public const string ReportInvalid = "drift_resolve_report_invalid";
    public const string ReportTruncated = "drift_resolve_report_truncated";
    public const string ReportStale = "drift_resolve_report_stale";
    public const string DocumentMismatch = "drift_resolve_document_mismatch";
    public const string KindUnsupported = "kind_unsupported";
    public const string KindChanged = "kind_changed";
    public const string GeometryInvalid = "geometry_invalid";
    public const string LevelUnbound = "level_unbound";
    public const string WallTypeUnbound = "wall_type_unbound";
    public const string SymbolUnbound = "symbol_unbound";
    public const string HostUnresolved = "host_unresolved";
    public const string CommandInvalid = "command_invalid";
    public const string DependencyUnmapped = "dependency_unmapped";
    public const string DependentsRemain = "dependents_remain";
    public const string IdConflict = "id_conflict";
}

public sealed record MirrorDriftAdopt(string Status, string? Action, Element? Element, string? MapUniqueId, IReadOnlyList<string> Requires, string? Code, string? Detail);

public sealed record MirrorDriftRestore(string Status, string? Mode, IReadOnlyList<string> Operations, IReadOnlyList<string> Requires, string? Code, string? Detail);

public sealed record MirrorDriftProposal(string FindingKind, string UniqueId, Guid? CoreId, Guid? SnapshotId, string ElementKind, string Classification, string Recommended,
    MirrorDriftAdopt Adopt, MirrorDriftRestore Restore);

public sealed record MirrorDriftResolution(string DocumentUid, Guid CoreProjectId, int CoreRevision, bool EvidenceIncomplete, IReadOnlyList<MirrorDriftProposal> Proposals, RevitCommandPlan RestorePlan)
{
    public const int Version = 1;
    public bool Applied => false;
    public string ToJson() => MirrorJson.Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteNumber("resolution_version", Version);
        writer.WriteEndObject();
    });
}

public static class MirrorDriftResolve
{
    public static readonly Provenance AdoptProvenance = new("revit-mirror", "proposed");

    public static MirrorDriftResolution Plan(MirrorDriftReport report, MirrorSnapshot snapshot, MirrorCommittedModel committed, RevitBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(committed);
        ArgumentNullException.ThrowIfNull(bindings);
        var plan = new RevitCommandPlan(PlanToCommands.Version, committed.Project.Id, committed.Project.Revision, "", bindings.Document,
            Array.Empty<RevitLevelPrecondition>(), Array.Empty<RevitCommandStep>(), Array.Empty<RevitUnsupportedElement>());
        return new MirrorDriftResolution(report.DocumentUid, report.CoreProjectId, report.CoreRevision, report.EvidenceIncomplete, Array.Empty<MirrorDriftProposal>(), plan);
    }
}
