using System.Text.Json;
namespace Bim.Core;

// P2-09 selective drift resolution. From a P2-07 resolution (MirrorDriftResolve.Plan) and a reviewer's choice of a
// subset of its proposals, each with adopt or restore, Select returns what that choice means and nothing else:
//   operations    the BIM-IR operations (create/update/delete, the adopt proposal's element) of every adopted
//                 proposal, in proposal order, plus the map entries an adopt adds (create) or removes (delete);
//   restore_plan  the resolution's restore plan filtered to the steps of every restored proposal, in the original run
//                 order, with the original operation ids, args, inputs and dependencies (so it passes the same
//                 catalogue schema and PlanRunner keeps its idempotency), bound to the same Core revision and hash;
//   invalidated   the key -> UniqueId map entries (key: the committed element id, "D" format) a chosen restore will
//                 make stale: a replace deletes the element and creates a new one, a create re-creates a missing
//                 element under a new UniqueId; each names the step whose created[0] gives the new UniqueId.
// A proposal id is the proposal's key, the one P2-07 Requires lists: its UniqueId, or "core:<id>" for a committed
// element without one; ids match ignoring case. Choosing an unsupported side, an unknown id, a malformed or duplicate
// choice, or a choice whose Requires coupling is not chosen with the same action is refused with a typed code
// (MirrorDriftSelectCodes, the first word of the ArgumentException message; MirrorDriftSelectException carries the
// proposal and the missing keys). Unchosen proposals are listed as omitted. Nothing is applied (applied false,
// requires approval). Pure and deterministic whatever the order of the choices (selection_version 1, never persisted);
// vendor-free; no BIM-IR or persisted format change.

public static class MirrorDriftSelectCodes
{
    public const string ResolutionInvalid = "drift_select_resolution_invalid";
    public const string ChoiceInvalid = "drift_select_choice_invalid";
    public const string ChoiceDuplicate = "drift_select_choice_duplicate";
    public const string ProposalUnknown = "drift_select_proposal_unknown";
    public const string ActionUnsupported = "drift_select_action_unsupported";
    public const string RequiresIncomplete = "drift_select_requires_incomplete";
}

public sealed class MirrorDriftSelectException(string code, string? proposalId, IReadOnlyList<string> missing, string detail)
    : ArgumentException(code + (detail.Length > 0 ? " " + detail : ""))
{
    public string Code { get; } = code;
    public string? ProposalId { get; } = proposalId;
    // For requires_incomplete: the required proposal keys not chosen with the same action.
    public IReadOnlyList<string> Missing { get; } = missing;
}

// Action: MirrorDriftResolveActions.Adopt or Restore.
public sealed record MirrorDriftChoice(string ProposalId, string Action);

// Key: the committed element id ("D") of the map entry; ByOperation: the restore step whose created[0] is the new UniqueId.
public sealed record MirrorDriftMapInvalidation(string Key, string UniqueId, string ProposalId, string Mode, string ByOperation);

public sealed record MirrorDriftMapEntry(string Key, string UniqueId, string ProposalId);

public sealed record MirrorDriftSelection(string DocumentUid, Guid CoreProjectId, int CoreRevision, IReadOnlyList<MirrorDriftChoice> Choices, IReadOnlyList<string> Omitted,
    IReadOnlyList<Operation> Operations, IReadOnlyList<MirrorDriftMapEntry> MapAdds, IReadOnlyList<MirrorDriftMapEntry> MapRemovals,
    RevitCommandPlan RestorePlan, IReadOnlyList<MirrorDriftMapInvalidation> Invalidated)
{
    public const int Version = 1;
    public bool Applied => false;
    public bool RequiresApproval => true;

    // {"selection_version":1,"document_unique_id","core_project_id","core_revision","applied":false,"requires_approval":true,
    //  "choices":[{"proposal_id","action"}],"omitted":[..],"operations":[{"action","element":{..}}],
    //  "map_adds":[{"key","unique_id","proposal_id"}],"map_removals":[..],
    //  "invalidated_map":[{"key","unique_id","proposal_id","mode","by_operation","select":"created[0]"}],"restore_plan":<RevitCommandPlan JSON>}
    public string ToJson() => MirrorJson.Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteNumber("selection_version", Version);
        writer.WriteString("document_unique_id", DocumentUid);
        writer.WriteString("core_project_id", CoreProjectId.ToString("D"));
        writer.WriteNumber("core_revision", CoreRevision);
        writer.WriteBoolean("applied", Applied);
        writer.WriteBoolean("requires_approval", RequiresApproval);
        writer.WriteStartArray("choices");
        foreach (var choice in Choices)
        {
            writer.WriteStartObject();
            writer.WriteString("proposal_id", choice.ProposalId);
            writer.WriteString("action", choice.Action);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        MirrorJson.Strings(writer, "omitted", Omitted);
        writer.WriteStartArray("operations");
        foreach (var operation in Operations)
        {
            writer.WriteStartObject();
            writer.WriteString("action", operation.Action);
            var element = operation.Element;
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
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        Entries(writer, "map_adds", MapAdds);
        Entries(writer, "map_removals", MapRemovals);
        writer.WriteStartArray("invalidated_map");
        foreach (var item in Invalidated)
        {
            writer.WriteStartObject();
            writer.WriteString("key", item.Key);
            writer.WriteString("unique_id", item.UniqueId);
            writer.WriteString("proposal_id", item.ProposalId);
            writer.WriteString("mode", item.Mode);
            writer.WriteString("by_operation", item.ByOperation);
            writer.WriteString("select", "created[0]");
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("restore_plan");
        writer.WriteRawValue(RestorePlan.ToJson());
        writer.WriteEndObject();
    });

    static void Entries(Utf8JsonWriter writer, string name, IReadOnlyList<MirrorDriftMapEntry> entries)
    {
        writer.WriteStartArray(name);
        foreach (var entry in entries)
        {
            writer.WriteStartObject();
            writer.WriteString("key", entry.Key);
            writer.WriteString("unique_id", entry.UniqueId);
            writer.WriteString("proposal_id", entry.ProposalId);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}

public static class MirrorDriftSelect
{
    // The proposal id P2-07 Requires uses: the UniqueId, or "core:<id>" for an element without one.
    public static string ProposalId(MirrorDriftProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return string.IsNullOrEmpty(proposal.UniqueId) ? "core:" + (proposal.CoreId?.ToString("D") ?? "") : proposal.UniqueId;
    }

    public static MirrorDriftSelection Select(MirrorDriftResolution resolution, IReadOnlyList<MirrorDriftChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (choices is null) throw Fail(MirrorDriftSelectCodes.ChoiceInvalid, null, "choices are required");
        var proposals = resolution.Proposals;
        var source = resolution.RestorePlan;

        // ---- The resolution must be a P2-07 resolution: proposals and plan steps agree ----
        if (proposals is null || source?.Steps is null || source.Document is null || source.Preconditions is null
            || proposals.Any(proposal => proposal?.Adopt?.Requires is null || proposal.Restore?.Requires is null || proposal.Restore.Operations is null))
            throw Invalid("proposals, their sides and the restore plan are required");
        var ids = proposals.Select(ProposalId).ToArray();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ids.Length; i++)
            if (ids[i].Length == 0 || ids[i] == "core:" || !index.TryAdd(ids[i], i)) throw Invalid("proposal ids must be present and distinct: " + ids[i]);
        if (source.Steps.Any(step => step is null || step.OperationId is null || step.DependsOn is null || step.Inputs is null || step.Inputs.Any(input => input is null))) throw Invalid("restore plan steps are malformed");
        var steps = new Dictionary<string, RevitCommandStep>(StringComparer.Ordinal);
        foreach (var step in source.Steps) if (!steps.TryAdd(step.OperationId, step)) throw Invalid("duplicate restore operation " + step.OperationId);
        var owner = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < proposals.Count; i++)
        {
            var (adopt, restore) = (proposals[i].Adopt, proposals[i].Restore);
            var adoptOk = adopt.Status == MirrorDriftResolveStatus.Unsupported
                || adopt.Status == MirrorDriftResolveStatus.Proposed && adopt.Element is { } element && element.GeometryMetres is not null && element.Dependencies is not null && element.Provenance is not null
                    && adopt.Action is MirrorDriftAdoptActions.Create or MirrorDriftAdoptActions.Update or MirrorDriftAdoptActions.Delete;
            var restoreOk = restore.Status == MirrorDriftResolveStatus.Proposed ? restore.Operations.Count > 0 && restore.Mode is not null
                : restore.Status == MirrorDriftResolveStatus.Unsupported && restore.Operations.Count == 0;
            if (!adoptOk || !restoreOk) throw Invalid("proposal " + ids[i] + " has a malformed adopt or restore side");
            foreach (var operation in restore.Operations)
                if (operation is null || !steps.ContainsKey(operation) || !owner.TryAdd(operation, i)) throw Invalid("restore of " + ids[i] + " names operation " + operation + " the plan lacks or another proposal owns");
            foreach (var key in adopt.Requires.Concat(restore.Requires))
                if (key is null || !index.ContainsKey(key)) throw Invalid("proposal " + ids[i] + " requires " + key + ", which is no proposal");
        }
        if (owner.Count != steps.Count) throw Invalid("restore plan steps " + string.Join(",", steps.Keys.Where(key => !owner.ContainsKey(key)).Order(StringComparer.Ordinal)) + " belong to no proposal");

        // ---- Choices: shape, then known ids, then one choice per proposal; checks in a fixed order ----
        foreach (var choice in choices)
            if (choice is null || string.IsNullOrWhiteSpace(choice.ProposalId) || choice.Action is not (MirrorDriftResolveActions.Adopt or MirrorDriftResolveActions.Restore))
                throw Fail(MirrorDriftSelectCodes.ChoiceInvalid, choice?.ProposalId, "a choice needs a proposal id and the action adopt or restore");
        var unknown = choices.Where(choice => !index.ContainsKey(choice.ProposalId)).Select(choice => choice.ProposalId).Order(StringComparer.Ordinal).FirstOrDefault();
        if (unknown is not null) throw Fail(MirrorDriftSelectCodes.ProposalUnknown, unknown, unknown + " is not a proposal of this resolution");
        var chosen = new Dictionary<int, string>();
        foreach (var group in choices.GroupBy(choice => index[choice.ProposalId]).OrderBy(group => group.Key))
        {
            if (group.Count() > 1) throw Fail(MirrorDriftSelectCodes.ChoiceDuplicate, ids[group.Key], ids[group.Key] + " is chosen more than once");
            chosen[group.Key] = group.Single().Action;
        }
        var order = chosen.Keys.Order().ToArray();
        foreach (var i in order)
        {
            var status = chosen[i] == MirrorDriftResolveActions.Adopt ? proposals[i].Adopt.Status : proposals[i].Restore.Status;
            if (status != MirrorDriftResolveStatus.Proposed)
            {
                var code = chosen[i] == MirrorDriftResolveActions.Adopt ? proposals[i].Adopt.Code : proposals[i].Restore.Code;
                throw Fail(MirrorDriftSelectCodes.ActionUnsupported, ids[i], ids[i] + " has no proposed " + chosen[i] + " (" + (code ?? "") + ")");
            }
        }
        // Requires: every coupled proposal is chosen with the same action.
        foreach (var i in order)
        {
            var action = chosen[i];
            var requires = action == MirrorDriftResolveActions.Adopt ? proposals[i].Adopt.Requires : proposals[i].Restore.Requires;
            var missing = requires.Where(key => !chosen.TryGetValue(index[key], out var other) || other != action).Select(key => ids[index[key]])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (missing.Length > 0)
                throw new MirrorDriftSelectException(MirrorDriftSelectCodes.RequiresIncomplete, ids[i], missing, ids[i] + " " + action + " requires " + string.Join(",", missing) + " " + action + " too");
        }

        // ---- BIM-IR operations and map changes, in proposal order ----
        var operations = new List<Operation>();
        var adds = new List<MirrorDriftMapEntry>();
        var removals = new List<MirrorDriftMapEntry>();
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var invalidated = new List<MirrorDriftMapInvalidation>();
        foreach (var i in order)
        {
            var proposal = proposals[i];
            if (chosen[i] == MirrorDriftResolveActions.Adopt)
            {
                var adopt = proposal.Adopt;
                var element = adopt.Element!;
                operations.Add(new Operation(adopt.Action!, element));
                if (adopt.Action == MirrorDriftAdoptActions.Create && !string.IsNullOrEmpty(adopt.MapUniqueId)) adds.Add(new MirrorDriftMapEntry(element.Id.ToString("D"), adopt.MapUniqueId, ids[i]));
                if (adopt.Action == MirrorDriftAdoptActions.Delete && !string.IsNullOrEmpty(proposal.UniqueId)) removals.Add(new MirrorDriftMapEntry(element.Id.ToString("D"), proposal.UniqueId, ids[i]));
                continue;
            }
            var restore = proposal.Restore;
            kept.UnionWith(restore.Operations);
            // A replace deletes the mapped element and creates another; a create re-creates a missing mapped one.
            if (restore.Mode is MirrorDriftRestoreModes.Replace or MirrorDriftRestoreModes.Create && proposal.CoreId is { } coreId && !string.IsNullOrEmpty(proposal.UniqueId))
                invalidated.Add(new MirrorDriftMapInvalidation(coreId.ToString("D"), proposal.UniqueId, ids[i], restore.Mode!, restore.Operations[restore.Operations.Count - 1]));
        }

        // ---- The restore plan filtered to the chosen steps, original order and ids ----
        var selected = source.Steps.Where(step => kept.Contains(step.OperationId)).ToArray();
        foreach (var step in selected)
            foreach (var dependency in step.DependsOn.Concat(step.Inputs.Select(input => input.FromOperation)))
                if (dependency is null || !kept.Contains(dependency))
                    throw Invalid("step " + step.OperationId + " needs " + dependency + ", which no chosen restore requires");
        var plan = new RevitCommandPlan(source.Version, source.ProjectId, source.ExpectedRevision, source.AfterHash, source.Document,
            source.Preconditions.Where(item => item is not null && (item.CheckOperation is null || kept.Contains(item.CheckOperation))).ToArray(), selected, Array.Empty<RevitUnsupportedElement>());
        return new MirrorDriftSelection(resolution.DocumentUid, resolution.CoreProjectId, resolution.CoreRevision,
            order.Select(i => new MirrorDriftChoice(ids[i], chosen[i])).ToArray(), Enumerable.Range(0, ids.Length).Where(i => !chosen.ContainsKey(i)).Select(i => ids[i]).ToArray(),
            operations, adds, removals, plan, invalidated);
    }

    static MirrorDriftSelectException Invalid(string detail) => new(MirrorDriftSelectCodes.ResolutionInvalid, null, Array.Empty<string>(), detail);

    static MirrorDriftSelectException Fail(string code, string? proposalId, string detail) => new(code, proposalId, Array.Empty<string>(), detail);
}
