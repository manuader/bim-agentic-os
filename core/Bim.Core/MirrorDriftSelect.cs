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
        var source = resolution.RestorePlan;
        var plan = new RevitCommandPlan(source.Version, source.ProjectId, source.ExpectedRevision, source.AfterHash, source.Document, Array.Empty<RevitLevelPrecondition>(), Array.Empty<RevitCommandStep>(), Array.Empty<RevitUnsupportedElement>());
        return new MirrorDriftSelection(resolution.DocumentUid, resolution.CoreProjectId, resolution.CoreRevision, Array.Empty<MirrorDriftChoice>(), Array.Empty<string>(),
            Array.Empty<Operation>(), Array.Empty<MirrorDriftMapEntry>(), Array.Empty<MirrorDriftMapEntry>(), plan, Array.Empty<MirrorDriftMapInvalidation>());
    }
}
