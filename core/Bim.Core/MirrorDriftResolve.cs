using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Bim.Core;

// P2-07 drift resolution plan. From a P2-04 drift report (MirrorDrift.Compare) of a snapshot against the Core store's
// committed model, Plan proposes, per drifted element, both ways to make the two agree, and recommends one:
//   adopt    a BIM-IR operation taking the mirror's state: update (changed: the snapshot geometry, its dependencies
//            mapped back to committed ids), create (extra: a new element with the snapshot id plus a map entry) or
//            delete (missing). Dependencies on another extra element require adopting it too; a missing element that
//            other committed elements still depend on cannot be deleted (dependents_remain).
//   restore  catalogue commands putting Core's state back in Revit: delete (extra: element.delete), create (missing:
//            wall.create / opening.place / room.create from the committed element), set_parameter (changed only in wall
//            height, level elevation or opening sill: element.set_parameter) or replace (any other change: element.delete
//            then create; the element gets a new UniqueId). Every step is parsed by RevitCommandProtocol.Parse, so the
//            restore plan passes the same catalogue schema and rules as a command sent by Claude; a step the catalogue
//            refuses makes that restore unsupported (command_invalid), never a half replace.
// Unsupported proposals carry a typed code (MirrorDriftResolveCodes). The recommendation is adopt for external_edit and
// addin_pending findings and restore for unknown ones, falling back to the other proposal when the preferred one is
// unsupported, or none. Nothing is applied: the resolution is a proposal for review (applied false, requires approval);
// the restore plan runs only through PlanRunner when someone chooses to. Pure and deterministic (resolution_version 1,
// never persisted); vendor-free; no BIM-IR or persisted format change.

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
    // ArgumentException messages.
    public const string ReportInvalid = "drift_resolve_report_invalid";
    public const string ReportTruncated = "drift_resolve_report_truncated";
    public const string ReportStale = "drift_resolve_report_stale";
    public const string DocumentMismatch = "drift_resolve_document_mismatch";
    // Unsupported proposals.
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
    public const string FieldsMissing = "fields_missing";
}

// Element: the BIM-IR element the operation writes (for delete, the committed element removed). MapUniqueId: the map
// entry a create adds. Requires: keys (UniqueId, or "core:<id>" for an element without one) of other proposals whose
// adopt must be accepted with this one.
public sealed record MirrorDriftAdopt(string Status, string? Action, Element? Element, string? MapUniqueId, IReadOnlyList<string> Requires, string? Code, string? Detail);

// Operations: this proposal's steps in the resolution's restore plan, in run order. Requires: keys of other proposals
// whose restore must be run with this one (an opening placed on a re-created host, a room bounded by re-created walls).
public sealed record MirrorDriftRestore(string Status, string? Mode, IReadOnlyList<string> Operations, IReadOnlyList<string> Requires, string? Code, string? Detail);

public sealed record MirrorDriftProposal(string FindingKind, string UniqueId, Guid? CoreId, Guid? SnapshotId, string ElementKind, string Classification, string Recommended,
    MirrorDriftAdopt Adopt, MirrorDriftRestore Restore);

// RestorePlan holds every proposed restore step (deletes, then parameter sets, then walls, openings and rooms) bound to
// Core's committed revision; its unsupported list names every restore that is not proposed.
public sealed record MirrorDriftResolution(string DocumentUid, Guid CoreProjectId, int CoreRevision, bool EvidenceIncomplete, IReadOnlyList<MirrorDriftProposal> Proposals, RevitCommandPlan RestorePlan)
{
    public const int Version = 1;
    public bool Applied => false;
    public bool RequiresApproval => true;

    // {"resolution_version":1,"document_unique_id","core_project_id","core_revision","applied":false,"requires_approval":true,
    //  "evidence_incomplete","summary":{..},"proposals":[{"finding_kind","unique_id","core_id","snapshot_id","element_kind",
    //  "classification","recommended","adopt":{"status","action","element","map_unique_id","requires","code","detail"},
    //  "restore":{"status","mode","operations","requires","code","detail"}}],"restore_plan":<RevitCommandPlan JSON>}
    public string ToJson() => MirrorJson.Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteNumber("resolution_version", Version);
        writer.WriteString("document_unique_id", DocumentUid);
        writer.WriteString("core_project_id", CoreProjectId.ToString("D"));
        writer.WriteNumber("core_revision", CoreRevision);
        writer.WriteBoolean("applied", Applied);
        writer.WriteBoolean("requires_approval", RequiresApproval);
        writer.WriteBoolean("evidence_incomplete", EvidenceIncomplete);
        writer.WriteStartObject("summary");
        writer.WriteNumber("proposals", Proposals.Count);
        writer.WriteNumber("adopt_proposed", Proposals.Count(proposal => proposal.Adopt.Status == MirrorDriftResolveStatus.Proposed));
        writer.WriteNumber("restore_proposed", Proposals.Count(proposal => proposal.Restore.Status == MirrorDriftResolveStatus.Proposed));
        writer.WriteNumber("recommended_adopt", Proposals.Count(proposal => proposal.Recommended == MirrorDriftResolveActions.Adopt));
        writer.WriteNumber("recommended_restore", Proposals.Count(proposal => proposal.Recommended == MirrorDriftResolveActions.Restore));
        writer.WriteNumber("recommended_none", Proposals.Count(proposal => proposal.Recommended == MirrorDriftResolveActions.None));
        writer.WriteEndObject();
        writer.WriteStartArray("proposals");
        foreach (var proposal in Proposals)
        {
            writer.WriteStartObject();
            writer.WriteString("finding_kind", proposal.FindingKind);
            writer.WriteString("unique_id", proposal.UniqueId);
            Id(writer, "core_id", proposal.CoreId);
            Id(writer, "snapshot_id", proposal.SnapshotId);
            writer.WriteString("element_kind", proposal.ElementKind);
            writer.WriteString("classification", proposal.Classification);
            writer.WriteString("recommended", proposal.Recommended);
            var adopt = proposal.Adopt;
            writer.WriteStartObject("adopt");
            writer.WriteString("status", adopt.Status);
            Text(writer, "action", adopt.Action);
            if (adopt.Element is { } element)
            {
                writer.WriteStartObject("element");
                writer.WriteString("id", element.Id.ToString("D"));
                writer.WriteString("kind", element.Kind);
                writer.WriteStartArray("geometry_metres");
                foreach (var value in element.GeometryMetres) writer.WriteNumberValue(value);
                writer.WriteEndArray();
                MirrorJson.Strings(writer, "dependencies", element.Dependencies.Select(id => id.ToString("D")));
                writer.WriteStartObject("provenance");
                writer.WriteString("source", element.Provenance.Source);
                writer.WriteString("approval", element.Provenance.Approval);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            else writer.WriteNull("element");
            Text(writer, "map_unique_id", adopt.MapUniqueId);
            MirrorJson.Strings(writer, "requires", adopt.Requires);
            Text(writer, "code", adopt.Code);
            Text(writer, "detail", adopt.Detail);
            writer.WriteEndObject();
            var restore = proposal.Restore;
            writer.WriteStartObject("restore");
            writer.WriteString("status", restore.Status);
            Text(writer, "mode", restore.Mode);
            MirrorJson.Strings(writer, "operations", restore.Operations);
            MirrorJson.Strings(writer, "requires", restore.Requires);
            Text(writer, "code", restore.Code);
            Text(writer, "detail", restore.Detail);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("restore_plan");
        writer.WriteRawValue(RestorePlan.ToJson());
        writer.WriteEndObject();
    });

    static void Id(Utf8JsonWriter writer, string name, Guid? id)
    {
        if (id is { } value) writer.WriteString(name, value.ToString("D")); else writer.WriteNull(name);
    }

    static void Text(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteString(name, value);
    }
}

public static class MirrorDriftResolve
{
    // Adopted elements are proposals until a reviewer commits them.
    public static readonly Provenance AdoptProvenance = new("revit-mirror", "proposed");
    public const string OperationPrefix = "drift.";
    const string PendingOperationId = "op.pending";
    // Metres: wall type bindings match a thickness within this; an opening centre may sit this far off its host axis.
    const double BindingTolerance = 1e-9;
    const double Tolerance = 1e-6;
    // Deletes run children first.
    static readonly string[] DeleteOrder = { "door", "window", "space", "wall", "level" };
    // Changed fields element.set_parameter can put back: the BuiltInParameter and the geometry index of Core's value.
    static readonly Dictionary<(string Kind, string Field), (string BuiltIn, int Index)> Parameters = new()
    {
        [("wall", "height")] = ("WALL_USER_HEIGHT_PARAM", 4),
        [("level", "elevation")] = ("LEVEL_ELEV", 0),
        [("door", "sill")] = ("INSTANCE_SILL_HEIGHT_PARAM", 4),
        [("window", "sill")] = ("INSTANCE_SILL_HEIGHT_PARAM", 4),
    };

    sealed record Built(string Command, string Args, RevitStepInput[] Inputs, string[] DependsOn, string[] Requires);
    sealed class Refusal(string code, string detail) : Exception(detail)
    {
        public string Code { get; } = code;
    }

    public static MirrorDriftResolution Plan(MirrorDriftReport report, MirrorSnapshot snapshot, MirrorCommittedModel committed, RevitBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(committed);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(bindings.Document);
        ArgumentNullException.ThrowIfNull(bindings.Levels);
        ArgumentNullException.ThrowIfNull(bindings.WallTypes);
        var document = bindings.Document;
        if (!RevitCopiesGuard.IsAbsoluteNormalized(document.Path)) throw new RevitCommandException(RevitCommandCodes.DocumentPathInvalid, "document.path", "absolute saved path without . or .. segments");
        if (!RevitCommandProtocol.IsUniqueId(document.UniqueId)) throw new RevitCommandException(RevitCommandCodes.DocumentIdentityInvalid, "document.unique_id", "ProjectInformation UniqueId required");
        if (report.Findings is null || committed.Project?.Elements is null || snapshot.Elements is null || snapshot.Sources is null) throw new ArgumentException(MirrorDriftResolveCodes.ReportInvalid);
        if (report.Truncated) throw new ArgumentException(MirrorDriftResolveCodes.ReportTruncated);
        if (!Same(report.DocumentUid, committed.DocumentUid) || !Same(report.DocumentUid, snapshot.DocumentUid) || !Same(report.DocumentUid, document.UniqueId))
            throw new ArgumentException(MirrorDriftResolveCodes.DocumentMismatch);
        if (report.CoreProjectId != committed.Project.Id || report.CoreRevision != committed.Project.Revision) throw new ArgumentException(MirrorDriftResolveCodes.ReportStale);
        var elements = committed.Project.Elements;
        if (elements.Any(element => element is null) || elements.Select(element => element.Id).Distinct().Count() != elements.Length
            || snapshot.Elements.Any(element => element is null) || snapshot.Elements.Select(element => element.Id).Distinct().Count() != snapshot.Elements.Count)
            throw new ArgumentException(MirrorDriftResolveCodes.ReportInvalid);
        var core = elements.ToDictionary(element => element.Id);
        var observed = snapshot.Elements.ToDictionary(element => element.Id);
        var nativeIds = committed.NativeIds ?? new Dictionary<Guid, string>();
        var coreByUid = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, uid) in nativeIds.OrderBy(pair => pair.Key.ToString("D"), StringComparer.Ordinal))
            if (core.ContainsKey(id) && !string.IsNullOrWhiteSpace(uid)) coreByUid.TryAdd(uid, id);

        // Findings checked against the given snapshot and committed model.
        var findings = report.Findings.ToArray();
        foreach (var finding in findings)
        {
            var valid = finding is not null && finding.UniqueId is not null && finding.Kind switch
            {
                MirrorDriftKinds.Changed => finding.CoreId is { } c && core.ContainsKey(c) && finding.SnapshotId is { } s && observed.ContainsKey(s) && SourceIs(s, finding.UniqueId),
                MirrorDriftKinds.Extra => finding.CoreId is null && finding.SnapshotId is { } s && observed.ContainsKey(s) && SourceIs(s, finding.UniqueId),
                MirrorDriftKinds.Missing => finding.CoreId is { } c && core.ContainsKey(c) && finding.SnapshotId is null,
                _ => false,
            };
            if (!valid) throw new ArgumentException(MirrorDriftResolveCodes.ReportInvalid);
        }
        bool SourceIs(Guid snapshotId, string uid) => snapshot.Sources.TryGetValue(snapshotId, out var source) && Same(source, uid);
        if (findings.Select(Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != findings.Length) throw new ArgumentException(MirrorDriftResolveCodes.ReportInvalid);

        var keyByCore = findings.Where(finding => finding.CoreId is not null).ToDictionary(finding => finding.CoreId!.Value, Key);
        var missing = findings.Where(finding => finding.Kind == MirrorDriftKinds.Missing).Select(finding => finding.CoreId!.Value).ToHashSet();
        var extraByUid = new Dictionary<string, MirrorDriftFinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var finding in findings.Where(finding => finding.Kind == MirrorDriftKinds.Extra)) extraByUid[finding.UniqueId] = finding;

        // ---- Adopt: changed and extra first, then missing (whose dependents may be adopted away) ----
        var adopts = new MirrorDriftAdopt[findings.Length];
        var adoptedByCore = new Dictionary<Guid, MirrorDriftAdopt>();
        for (var i = 0; i < findings.Length; i++)
        {
            var finding = findings[i];
            if (finding.Kind == MirrorDriftKinds.Missing) continue;
            var revit = observed[finding.SnapshotId!.Value];
            if (finding.Kind == MirrorDriftKinds.Changed)
            {
                var element = core[finding.CoreId!.Value];
                adopts[i] = !string.Equals(element.Kind, revit.Kind, StringComparison.Ordinal)
                    ? AdoptRefused(MirrorDriftResolveCodes.KindChanged, "Revit reports a " + revit.Kind + " for the BIM-IR " + element.Kind)
                    : MapDependencies(revit) is ({ } dependencies, var requires, null)
                        ? new MirrorDriftAdopt(MirrorDriftResolveStatus.Proposed, MirrorDriftAdoptActions.Update,
                            element with { GeometryMetres = (revit.GeometryMetres ?? Array.Empty<double>()).ToArray(), Dependencies = dependencies, Provenance = AdoptProvenance }, null, requires, null, null)
                        : AdoptRefused(MirrorDriftResolveCodes.DependencyUnmapped, MapDependencies(revit).Problem ?? "");
                adoptedByCore[element.Id] = adopts[i];
            }
            else
            {
                adopts[i] = core.ContainsKey(revit.Id)
                    ? AdoptRefused(MirrorDriftResolveCodes.IdConflict, "the snapshot id " + revit.Id.ToString("D") + " is already a committed element")
                    : MapDependencies(revit) is ({ } dependencies, var requires, null)
                        ? new MirrorDriftAdopt(MirrorDriftResolveStatus.Proposed, MirrorDriftAdoptActions.Create,
                            new Element(revit.Id, revit.Kind, (revit.GeometryMetres ?? Array.Empty<double>()).ToArray(), dependencies, AdoptProvenance), snapshot.Sources[revit.Id], requires, null, null)
                        : AdoptRefused(MirrorDriftResolveCodes.DependencyUnmapped, MapDependencies(revit).Problem ?? "");
            }
        }
        for (var i = 0; i < findings.Length; i++)
        {
            var finding = findings[i];
            if (finding.Kind != MirrorDriftKinds.Missing) continue;
            var id = finding.CoreId!.Value;
            var requires = new List<string>();
            var remain = new List<string>();
            foreach (var dependent in elements.Where(element => element.Dependencies is { } dependencies && dependencies.Contains(id)).OrderBy(element => element.Id.ToString("D"), StringComparer.Ordinal))
            {
                if (missing.Contains(dependent.Id)) requires.Add(keyByCore[dependent.Id]);
                else if (adoptedByCore.TryGetValue(dependent.Id, out var adopted) && adopted is { Status: MirrorDriftResolveStatus.Proposed, Element: { } next } && !next.Dependencies.Contains(id))
                    requires.Add(keyByCore[dependent.Id]);
                else remain.Add(dependent.Id.ToString("D"));
            }
            adopts[i] = remain.Count > 0
                ? AdoptRefused(MirrorDriftResolveCodes.DependentsRemain, "committed elements still depend on it: " + string.Join(",", remain))
                : new MirrorDriftAdopt(MirrorDriftResolveStatus.Proposed, MirrorDriftAdoptActions.Delete, core[id], null, Sorted(requires), null, null);
        }

        // Snapshot dependencies as committed ids: a mapped committed element, or an extra element adopted with this one.
        (Guid[]? Dependencies, string[] Requires, string? Problem) MapDependencies(Element revit)
        {
            var dependencies = new List<Guid>();
            var requires = new List<string>();
            foreach (var id in revit.Dependencies ?? Array.Empty<Guid>())
            {
                if (!snapshot.Sources.TryGetValue(id, out var uid)) return (null, Array.Empty<string>(), "snapshot dependency " + id.ToString("D") + " has no source");
                Guid target;
                if (coreByUid.TryGetValue(uid, out var coreId)) target = coreId;
                else if (extraByUid.TryGetValue(uid, out var extra)) { target = extra.SnapshotId!.Value; requires.Add(extra.UniqueId); }
                else return (null, Array.Empty<string>(), "Revit dependency " + uid + " has no committed element and is not adopted");
                if (!dependencies.Contains(target)) dependencies.Add(target);
            }
            return (dependencies.ToArray(), Sorted(requires), null);
        }

        // ---- Restore ----
        var projectId = committed.Project.Id;
        var revision = committed.Project.Revision;
        var afterHash = Compiler.Hash(committed.Project with { Elements = elements.OrderBy(element => element.Id.ToString("D"), StringComparer.Ordinal).ToArray() });
        string OperationId(string key, string command, string canonical, IEnumerable<string> inputs, IEnumerable<string> dependsOn)
        {
            var text = "mirror-drift-resolve/" + MirrorDriftResolution.Version.ToString(CultureInfo.InvariantCulture) + "\n" + projectId.ToString("D") + "\n" + revision.ToString(CultureInfo.InvariantCulture)
                + "\n" + afterHash + "\n" + document.UniqueId + "\n" + document.Path + "\n" + key.ToLowerInvariant() + "\n" + command + "\n" + canonical + "\n" + string.Join(",", inputs) + "\n" + string.Join(",", dependsOn);
            return OperationPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16).ToLowerInvariant();
        }
        string Canonical(string command, string args)
        {
            try { return CanonicalArgs(Parse(document, PendingOperationId, command, args)); }
            catch (RevitCommandException error) { throw new Refusal(MirrorDriftResolveCodes.CommandInvalid, error.Code + "@" + (error.Field ?? "") + ": " + (error.Detail ?? "")); }
        }

        var restores = new MirrorDriftRestore?[findings.Length];
        var deletes = new List<(int Rank, string Uid, RevitCommandStep Step)>();
        var parameters = new List<(string Uid, string Field, RevitCommandStep Step)>();
        var creates = new List<(int Rank, string Id, RevitCommandStep Step)>();
        var createOps = new Dictionary<Guid, string>();
        MirrorDriftRestore RestoreRefused(string code, string detail) => new(MirrorDriftResolveStatus.Unsupported, null, Array.Empty<string>(), Array.Empty<string>(), code, detail);

        RevitCommandStep DeleteStep(MirrorDriftFinding finding)
        {
            var args = Json(writer =>
            {
                writer.WriteStartArray("element_uids");
                writer.WriteStringValue(finding.UniqueId);
                writer.WriteEndArray();
            });
            var canonical = Canonical(RevitCommandCatalogueNames.Delete, args);
            return new RevitCommandStep(OperationId(Key(finding), RevitCommandCatalogueNames.Delete, canonical, Array.Empty<string>(), Array.Empty<string>()),
                RevitCommandCatalogueNames.Delete, finding.CoreId ?? finding.SnapshotId, finding.ElementKind, Array.Empty<string>(), canonical, Array.Empty<RevitStepInput>(), Array.Empty<string>());
        }

        // Extra elements: delete; changed: set_parameter, or a later replace.
        var replace = new List<int>();
        for (var i = 0; i < findings.Length; i++)
        {
            var finding = findings[i];
            try
            {
                if (finding.Kind == MirrorDriftKinds.Extra)
                {
                    var step = DeleteStep(finding);
                    deletes.Add((Rank(finding.ElementKind), finding.UniqueId, step));
                    restores[i] = new MirrorDriftRestore(MirrorDriftResolveStatus.Proposed, MirrorDriftRestoreModes.Delete, new[] { step.OperationId }, Array.Empty<string>(), null, null);
                }
                else if (finding.Kind == MirrorDriftKinds.Changed)
                {
                    var element = core[finding.CoreId!.Value];
                    var fields = (finding.Fields ?? Array.Empty<MirrorDriftField>()).Select(field => field.Field).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                    if (fields.Contains("kind")) throw new Refusal(MirrorDriftResolveCodes.KindChanged, "Revit reports another kind; the catalogue cannot change an element's kind");
                    if (fields.Contains("geometry")) throw new Refusal(MirrorDriftResolveCodes.GeometryInvalid, "the geometry layout differs from the BIM-IR kind's");
                    if (fields.Length > 0 && fields.All(field => Parameters.ContainsKey((element.Kind, field))))
                    {
                        var steps = new List<RevitCommandStep>();
                        foreach (var field in fields)
                        {
                            var (builtIn, index) = Parameters[(element.Kind, field)];
                            var g = element.GeometryMetres ?? Array.Empty<double>();
                            if (g.Length <= index || !double.IsFinite(g[index])) throw new Refusal(MirrorDriftResolveCodes.GeometryInvalid, "committed " + element.Kind + " has no finite " + field);
                            var args = Json(writer =>
                            {
                                writer.WriteString("element_uid", finding.UniqueId);
                                writer.WriteStartObject("parameter");
                                writer.WriteString("built_in", builtIn);
                                writer.WriteEndObject();
                                writer.WriteStartObject("value");
                                writer.WriteString("kind", RevitParameterValueKinds.Length);
                                writer.WriteNumber("mm", Mm(g[index]));
                                writer.WriteEndObject();
                            });
                            var canonical = Canonical(RevitCommandCatalogueNames.SetParameter, args);
                            steps.Add(new RevitCommandStep(OperationId(Key(finding) + "#" + field, RevitCommandCatalogueNames.SetParameter, canonical, Array.Empty<string>(), Array.Empty<string>()),
                                RevitCommandCatalogueNames.SetParameter, element.Id, element.Kind, Array.Empty<string>(), canonical, Array.Empty<RevitStepInput>(), Array.Empty<string>()));
                        }
                        for (var f = 0; f < fields.Length; f++) parameters.Add((finding.UniqueId, fields[f], steps[f]));
                        restores[i] = new MirrorDriftRestore(MirrorDriftResolveStatus.Proposed, MirrorDriftRestoreModes.SetParameter, steps.Select(step => step.OperationId).ToArray(), Array.Empty<string>(), null, null);
                    }
                    else replace.Add(i);
                }
                else replace.Add(i);
            }
            catch (Refusal refusal) { restores[i] = RestoreRefused(refusal.Code, refusal.Message); }
        }

        // Missing (create) and replaced (delete then create) elements: walls, then openings, then rooms.
        int CreateRank(string kind) => kind switch { "wall" => 0, "door" or "window" => 1, "space" => 2, _ => 3 };
        foreach (var i in replace.OrderBy(index => CreateRank(findings[index].ElementKind)).ThenBy(index => findings[index].CoreId!.Value.ToString("D"), StringComparer.Ordinal))
        {
            var finding = findings[i];
            var element = core[finding.CoreId!.Value];
            try
            {
                var replacing = finding.Kind == MirrorDriftKinds.Changed;
                var built = element.Kind switch
                {
                    "wall" => Wall(element),
                    "door" or "window" => Opening(element),
                    "space" => Room(element),
                    _ => throw new Refusal(MirrorDriftResolveCodes.KindUnsupported, "no catalogue command creates a " + element.Kind),
                };
                var canonical = Canonical(built.Command, built.Args);
                var steps = new List<RevitCommandStep>();
                var dependsOn = built.DependsOn.ToList();
                if (replacing)
                {
                    var delete = DeleteStep(finding);
                    deletes.Add((Rank(finding.ElementKind), finding.UniqueId, delete));
                    steps.Add(delete);
                    dependsOn.Insert(0, delete.OperationId);
                }
                var operationId = OperationId(Key(finding), built.Command, canonical, built.Inputs.Select(input => input.FromOperation), dependsOn);
                var create = new RevitCommandStep(operationId, built.Command, element.Id, element.Kind, dependsOn.ToArray(), canonical, built.Inputs, Array.Empty<string>());
                steps.Add(create);
                creates.Add((CreateRank(element.Kind), element.Id.ToString("D"), create));
                createOps[element.Id] = operationId;
                restores[i] = new MirrorDriftRestore(MirrorDriftResolveStatus.Proposed, replacing ? MirrorDriftRestoreModes.Replace : MirrorDriftRestoreModes.Create,
                    steps.Select(step => step.OperationId).ToArray(), built.Requires, null, null);
            }
            catch (Refusal refusal) { restores[i] = RestoreRefused(refusal.Code, refusal.Message); }
        }

        Built Wall(Element element)
        {
            var g = element.GeometryMetres;
            if (!Finite(element, 6) || !GeometryCodec.IsValidWall(g)) throw new Refusal(MirrorDriftResolveCodes.GeometryInvalid, "wall geometry must be [ax,ay,bx,by,height,thickness]");
            var level = Level(element);
            var type = bindings.WallTypes.FirstOrDefault(binding => binding is not null && Math.Abs(binding.ThicknessMetres - g[5]) <= BindingTolerance)
                ?? throw new Refusal(MirrorDriftResolveCodes.WallTypeUnbound, "no Revit wall type bound for thickness " + g[5].ToString("R", CultureInfo.InvariantCulture) + " m");
            var args = Json(writer =>
            {
                writer.WriteStartObject("curve");
                writer.WriteString("kind", "line");
                Point(writer, "start", g[0], g[1]);
                Point(writer, "end", g[2], g[3]);
                writer.WriteEndObject();
                writer.WriteNumber("height_mm", Mm(g[4]));
                writer.WriteString("level", level);
                writer.WriteString("type", type.Type);
            });
            return new Built("wall.create", args, Array.Empty<RevitStepInput>(), Array.Empty<string>(), Array.Empty<string>());
        }

        Built Opening(Element element)
        {
            var g = element.GeometryMetres;
            var door = element.Kind == "door";
            if (!Finite(element, 6) || !(g[5] > 0) || g[4] < 0) throw new Refusal(MirrorDriftResolveCodes.GeometryInvalid, "opening geometry must be [ax,ay,bx,by,sill,height]");
            if (Math.Sqrt((g[2] - g[0]) * (g[2] - g[0]) + (g[3] - g[1]) * (g[3] - g[1])) < Tolerance) throw new Refusal(MirrorDriftResolveCodes.GeometryInvalid, "opening has no width along its host");
            if (door && g[4] != 0) throw new Refusal(MirrorDriftResolveCodes.GeometryInvalid, "opening.place gives doors no sill height; sill must be 0");
            var symbol = (door ? bindings.DoorSymbol : bindings.WindowSymbol) ?? throw new Refusal(MirrorDriftResolveCodes.SymbolUnbound, "no " + element.Kind + " family type bound");
            var hostId = (element.Dependencies ?? Array.Empty<Guid>()).Cast<Guid?>().FirstOrDefault(id => core.TryGetValue(id!.Value, out var candidate) && candidate.Kind == "wall")
                ?? throw new Refusal(MirrorDriftResolveCodes.HostUnresolved, "no committed host wall among its dependencies");
            var host = core[hostId];
            if (!Finite(host, 6) || !GeometryCodec.IsValidWall(host.GeometryMetres)) throw new Refusal(MirrorDriftResolveCodes.HostUnresolved, "host wall " + hostId.ToString("D") + " has no valid axis");
            var inputs = Array.Empty<RevitStepInput>();
            var dependsOn = Array.Empty<string>();
            var requires = Array.Empty<string>();
            string hostUid;
            if (createOps.TryGetValue(hostId, out var hostOperation))
            {
                hostUid = PlanToCommands.PlaceholderUniqueId;
                inputs = new[] { new RevitStepInput("args.host_wall_uid", hostOperation, "created[0]") };
                dependsOn = new[] { hostOperation };
                requires = new[] { keyByCore[hostId] };
            }
            else if (!missing.Contains(hostId) && nativeIds.TryGetValue(hostId, out var uid) && RevitCommandProtocol.IsUniqueId(uid)) hostUid = uid;
            else throw new Refusal(MirrorDriftResolveCodes.HostUnresolved, "host wall " + hostId.ToString("D") + " is not in Revit and is not re-created");
            var h = host.GeometryMetres;
            double dx = h[2] - h[0], dy = h[3] - h[1];
            var length = Math.Sqrt(dx * dx + dy * dy);
            double mx = (g[0] + g[2]) / 2 - h[0], my = (g[1] + g[3]) / 2 - h[1];
            var along = (mx * dx + my * dy) / length;
            var off = Math.Abs(mx * dy - my * dx) / length;
            if (off > Tolerance || along < -Tolerance || along > length + Tolerance) throw new Refusal(MirrorDriftResolveCodes.GeometryInvalid, "opening centre is not on its host wall axis");
            var args = Json(writer =>
            {
                writer.WriteString("category", element.Kind);
                writer.WriteString("host_wall_uid", hostUid);
                writer.WriteNumber("position_along_mm", Mm(Math.Clamp(along, 0, length)));
                if (!door) writer.WriteNumber("sill_mm", Mm(g[4]));
                writer.WriteString("symbol", symbol);
            });
            return new Built("opening.place", args, inputs, dependsOn, requires);
        }

        Built Room(Element element)
        {
            var g = element.GeometryMetres;
            if (!Finite(element, 4) || !(g[2] > 0) || !(g[3] > 0)) throw new Refusal(MirrorDriftResolveCodes.GeometryInvalid, "space geometry must be [x,y,width,depth]");
            var level = Level(element);
            var bounding = (element.Dependencies ?? Array.Empty<Guid>()).Where(id => core.TryGetValue(id, out var candidate) && candidate.Kind == "wall" && createOps.ContainsKey(id)).Distinct().ToArray();
            var args = Json(writer =>
            {
                writer.WriteString("level", level);
                Point(writer, "point", g[0] + g[2] / 2, g[1] + g[3] / 2);
            });
            return new Built("room.create", args, Array.Empty<RevitStepInput>(), bounding.Select(id => createOps[id]).Order(StringComparer.Ordinal).ToArray(), Sorted(bounding.Select(id => keyByCore[id])));
        }

        // The Revit level of a committed element: its first level dependency, bound by name or UniqueId, else its
        // mapped UniqueId when that level is still in Revit.
        string Level(Element element)
        {
            var levelId = (element.Dependencies ?? Array.Empty<Guid>()).Cast<Guid?>().FirstOrDefault(id => core.TryGetValue(id!.Value, out var candidate) && candidate.Kind == "level")
                ?? throw new Refusal(MirrorDriftResolveCodes.LevelUnbound, "no committed level among its dependencies");
            if (bindings.Levels.TryGetValue(levelId, out var reference)) return reference;
            if (!missing.Contains(levelId) && nativeIds.TryGetValue(levelId, out var uid) && RevitCommandProtocol.IsUniqueId(uid)) return uid;
            throw new Refusal(MirrorDriftResolveCodes.LevelUnbound, "level " + levelId.ToString("D") + " has no Revit level binding and is not in Revit");
        }

        // ---- Proposals, recommendation and the restore plan ----
        var proposals = new List<MirrorDriftProposal>();
        var unsupported = new List<RevitUnsupportedElement>();
        for (var i = 0; i < findings.Length; i++)
        {
            var finding = findings[i];
            var adopt = adopts[i];
            var restore = restores[i] ?? RestoreRefused(MirrorDriftResolveCodes.KindUnsupported, "no restore for " + finding.Kind);
            var preferred = finding.Classification is MirrorDriftClasses.ExternalEdit or MirrorDriftClasses.AddInPending ? MirrorDriftResolveActions.Adopt : MirrorDriftResolveActions.Restore;
            var other = preferred == MirrorDriftResolveActions.Adopt ? MirrorDriftResolveActions.Restore : MirrorDriftResolveActions.Adopt;
            bool Proposed(string action) => (action == MirrorDriftResolveActions.Adopt ? adopt.Status : restore.Status) == MirrorDriftResolveStatus.Proposed;
            var recommended = Proposed(preferred) ? preferred : Proposed(other) ? other : MirrorDriftResolveActions.None;
            proposals.Add(new MirrorDriftProposal(finding.Kind, finding.UniqueId, finding.CoreId, finding.SnapshotId, finding.ElementKind ?? "", finding.Classification ?? "", recommended, adopt, restore));
            if (restore.Status != MirrorDriftResolveStatus.Proposed)
                unsupported.Add(new RevitUnsupportedElement(finding.CoreId ?? finding.SnapshotId ?? Guid.Empty, finding.ElementKind ?? "", MirrorDriftResolveActions.Restore, restore.Code ?? "", restore.Detail ?? ""));
        }
        var ordered = deletes.OrderBy(item => item.Rank).ThenBy(item => item.Uid.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(item => item.Uid, StringComparer.Ordinal).Select(item => item.Step)
            .Concat(parameters.OrderBy(item => item.Uid.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(item => item.Uid, StringComparer.Ordinal).ThenBy(item => item.Field, StringComparer.Ordinal).Select(item => item.Step))
            .Concat(creates.OrderBy(item => item.Rank).ThenBy(item => item.Id, StringComparer.Ordinal).Select(item => item.Step)).ToArray();
        var plan = new RevitCommandPlan(PlanToCommands.Version, projectId, revision, afterHash, document, Array.Empty<RevitLevelPrecondition>(), ordered,
            unsupported.OrderBy(item => item.ElementId.ToString("D"), StringComparer.Ordinal).ThenBy(item => item.Code, StringComparer.Ordinal).ToArray());
        return new MirrorDriftResolution(report.DocumentUid ?? "", report.CoreProjectId, report.CoreRevision, report.EvidenceIncomplete, proposals, plan);
    }

    static MirrorDriftAdopt AdoptRefused(string code, string detail) => new(MirrorDriftResolveStatus.Unsupported, null, null, null, Array.Empty<string>(), code, detail);

    static string Key(MirrorDriftFinding finding) => string.IsNullOrEmpty(finding.UniqueId) ? "core:" + (finding.CoreId?.ToString("D") ?? "") : finding.UniqueId;

    static bool Same(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static string[] Sorted(IEnumerable<string> values) => values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    static int Rank(string? kind)
    {
        var index = Array.IndexOf(DeleteOrder, kind);
        return index < 0 ? DeleteOrder.Length : index;
    }

    static bool Finite(Element element, int arity) => element.GeometryMetres is { } geometry && geometry.Length == arity && geometry.All(double.IsFinite);

    static void Point(Utf8JsonWriter writer, string name, double xMetres, double yMetres)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(Mm(xMetres));
        writer.WriteNumberValue(Mm(yMetres));
        writer.WriteEndArray();
    }

    // Metres to millimetres, rounded to 1e-6 mm so binary noise does not reach the command; -0 is 0.
    static double Mm(double metres)
    {
        var millimetres = Math.Round(metres * Length.MillimetresPerMetre, 6, MidpointRounding.ToEven);
        return millimetres == 0 ? 0.0 : millimetres;
    }

    static RevitCommandEnvelope Parse(RevitDocumentRef document, string operationId, string command, string args) => RevitCommandProtocol.Parse(Json(writer =>
    {
        writer.WriteNumber("protocol_version", RevitCommandProtocol.Version);
        writer.WriteString("operation_id", operationId);
        writer.WriteStartObject("document");
        writer.WriteString("path", document.Path);
        writer.WriteString("unique_id", document.UniqueId);
        writer.WriteEndObject();
        writer.WriteString("command", command);
        writer.WritePropertyName("args");
        writer.WriteRawValue(args);
    }));

    static string CanonicalArgs(RevitCommandEnvelope envelope)
    {
        using var canonical = JsonDocument.Parse(envelope.CanonicalJson);
        return canonical.RootElement.GetProperty("args").GetRawText();
    }

    static string Json(Action<Utf8JsonWriter> members)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            members(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static class RevitCommandCatalogueNames
    {
        public const string Delete = "element.delete";
        public const string SetParameter = "element.set_parameter";
    }
}
