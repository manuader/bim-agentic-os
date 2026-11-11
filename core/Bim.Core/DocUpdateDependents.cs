using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Bim.Core;

// P4-09 two-pass dependents for documentation updates. DocUpdate.Plan needs, per stale delete, the UniqueIds Revit
// deletes with it (as a dry run reports them), so a caller had to plan, dry-run each planned delete by hand and plan
// again. Plan does it in one call:
//   1. plans with DocUpdate.Plan and no dependents (its refusals are thrown unchanged, before anything is sent);
//   2. dry-runs every planned element.delete through the transport, in plan order, with only its stale target and
//      dry_run true (the add-in rolls it back: nothing is written, no receipt is stored), and reads the dependents from
//      the delete readback (strictly: a dry run of that very target, every dependent listed, count and SHA-256 agreeing);
//   3. checks each dependent (UniqueIds compared ignoring hex case): one realized for a kept source (an item not
//      regenerated, or a model element) is refused dependent_kept; the stale instance of a regenerated item deleted
//      LATER in plan order is refused dependent_planned (only one deleted earlier is gone by then); one that is neither a
//      stale instance nor in accepted (UniqueIds the caller has confirmed, e.g. an untracked annotation a human
//      reviewed) is refused dependent_unknown; an accepted untracked one reported by two planned deletes is refused
//      dependent_shared (the later delete would confirm an element the earlier one already removed). Each refusal
//      names the key and the UniqueId and stops the first pass;
//   4. replans with DocUpdate.Plan and the collected dependents (a refusal keeps DocReplace's dependents code; any
//      other message is replan_internal, a defect);
//   5. dry-runs every replanned delete again, with exactly its planned args plus dry_run true, and refuses
//      dependents_changed when Revit now reports another dependent set (or the target appeared or disappeared).
// The confirm pass runs on the model as it is before the run (nothing deleted yet), so it compares each replanned
// delete's full reported set with its first probe's full set, not with the confirm_dependents the plan sends (those
// leave out what earlier deletes remove). It narrows, but cannot close, the window between confirmation and
// execution: a model change after the confirm pass is caught by the executor itself, which refuses a real delete whose
// dependents differ from confirm_dependents (the run then fails typed and resumes as DocUpdate.Resume).
// Only dry runs are sent before the second plan exists; Update runs that plan (DocUpdate.Run) only when every check
// passed. A target the first dry run answers reference_not_found (Revit already deleted it) gets no dependents and is
// probed Missing; the run then reports it Gone as DocUpdate does. A transport or reply failure is returned typed with
// the code PlanRunner would report (pipe_io_error, no_response, response_invalid, or the executor's own code).
// Deterministic: dry-run operation ids derive from the planned delete's id, dependents are sorted, details are fixed
// or UniqueIds. Vendor-free, BCL only; no BIM-IR or persisted format change.

// One dry-run delete of the first pass: the regenerated key, its stale target, the dry run's operation id and the
// dependents it reported (sorted); Missing when the target no longer exists (no dependents).
public sealed record DocUpdateDependentsProbe(string Key, string StaleUniqueId, string OperationId, IReadOnlyList<string> Dependents, bool Missing);

// Why the two passes stopped: Key and OperationId of the dry run concerned (null for a second-plan refusal).
public sealed record DocUpdateDependentsFailure(string Code, string? Key, string? OperationId, string? Field, string? Detail);

// First: the plan without dependents. Dependents: per probed key (not Missing), what its dry run reported, by key.
// Plan: the replanned update, null when Failed is set.
public sealed record DocUpdateDependentsResult(DocUpdatePlan First, IReadOnlyList<DocUpdateDependentsProbe> Probes,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Dependents, DocUpdatePlan? Plan, DocUpdateDependentsFailure? Failed);

// Run: the run of Planned.Plan, null when planning failed (nothing was written).
public sealed record DocUpdateDependentsRun(DocUpdateDependentsResult Planned, DocUpdateResult? Run);

public static class DocUpdateDependentsCodes
{
    // A reported dependent that is neither the stale instance of a regenerated item nor accepted by the caller.
    public const string DependentUnknown = "dependent_unknown";
    // The confirming dry run of a replanned delete reports other dependents than the first one did.
    public const string DependentsChanged = "dependents_changed";
    // ArgumentException message: an accepted entry that is not a UniqueId.
    public const string AcceptedInvalid = "accepted_invalid";
    // An accepted untracked dependent reported by more than one planned delete.
    public const string DependentShared = "dependent_shared";
    // The second plan refused with something other than a dependents code (a defect: the inputs already planned once).
    public const string ReplanInternal = "replan_internal";
}

public static class DocUpdateDependents
{
    public const int Version = 1;
    const string Collect = "collect";
    const string Confirm = "confirm";
    // The DocReplace refusals the collected dependents can cause in the second plan.
    static readonly string[] ReplanCodes =
    {
        DocReplaceCodes.DependentsUnknownKey, DocReplaceCodes.DependentsWithoutStale, DocReplaceCodes.DependentsInvalid,
        DocReplaceCodes.DependentKept, DocReplaceCodes.DependentPlanned, DocReplaceCodes.DependentIsTarget,
    };

    public static DocUpdateDependentsResult Plan(Project before, Project after, RevitBindings bindings, IReadOnlyDictionary<string, string> realized,
        IRevitCommandTransport transport, IReadOnlyCollection<string>? accepted = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(realized);
        ArgumentNullException.ThrowIfNull(transport);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (accepted is not null)
            foreach (var uid in accepted)
            {
                if (!RevitCommandProtocol.IsUniqueId(uid)) throw new ArgumentException(DocUpdateDependentsCodes.AcceptedInvalid);
                allowed.Add(uid);
            }

        var first = DocUpdate.Plan(before, after, bindings, realized);
        var document = first.Replacement.Commands.Document;
        var stale = new HashSet<string>(first.Replacement.Replacements.Where(item => item.StaleUniqueId is not null).Select(item => item.StaleUniqueId!), StringComparer.OrdinalIgnoreCase);
        var kept = new HashSet<string>(first.Realized.Values.Where(uid => !stale.Contains(uid)), StringComparer.OrdinalIgnoreCase);
        var probes = new List<DocUpdateDependentsProbe>();
        var dependents = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        DocUpdateDependentsResult Fail(string code, string? key, string? operation, string? field, string? detail) =>
            new(first, probes.ToArray(), Copy(dependents), null, new DocUpdateDependentsFailure(code, key, operation, field, detail));

        var firstDeletes = Deletes(first).ToArray();
        // Stale UniqueId -> position of its delete in plan order; an accepted untracked dependent -> the key reporting it.
        var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < firstDeletes.Length; i++) position[firstDeletes[i].Item.StaleUniqueId!] = i;
        var untracked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < firstDeletes.Length; index++)
        {
            var (step, item) = firstDeletes[index];
            var operation = OperationId(Collect, step.OperationId);
            var args = new JsonObject { ["element_uids"] = new JsonArray(JsonValue.Create(item.StaleUniqueId!)), ["dry_run"] = true };
            var (reported, failure) = DryRun(document, operation, args, item.StaleUniqueId!, transport);
            if (failure is not null)
            {
                if (!TargetMissing(failure)) return Fail(failure.Code, item.Key, operation, failure.Field, failure.Detail);
                probes.Add(new DocUpdateDependentsProbe(item.Key, item.StaleUniqueId!, operation, Array.Empty<string>(), true));
                continue;
            }
            var list = reported!;
            foreach (var uid in list)
            {
                if (kept.Contains(uid)) return Fail(DocReplaceCodes.DependentKept, item.Key, operation, "dependents", uid);
                if (position.TryGetValue(uid, out var at))
                {
                    if (at > index) return Fail(DocReplaceCodes.DependentPlanned, item.Key, operation, "dependents", uid);
                    continue;
                }
                if (stale.Contains(uid)) continue;
                if (!allowed.Contains(uid)) return Fail(DocUpdateDependentsCodes.DependentUnknown, item.Key, operation, "dependents", uid);
                if (untracked.ContainsKey(uid)) return Fail(DocUpdateDependentsCodes.DependentShared, item.Key, operation, "dependents", uid);
            }
            foreach (var uid in list) if (!stale.Contains(uid)) untracked[uid] = item.Key;
            probes.Add(new DocUpdateDependentsProbe(item.Key, item.StaleUniqueId!, operation, list, false));
            dependents[item.Key] = list;
        }

        DocUpdatePlan second;
        try { second = DocUpdate.Plan(before, after, bindings, realized, Copy(dependents)); }
        catch (ArgumentException error)
        {
            return ReplanCodes.Contains(error.Message, StringComparer.Ordinal) ? Fail(error.Message, null, null, null, null)
                : Fail(DocUpdateDependentsCodes.ReplanInternal, null, null, null, error.GetType().Name);
        }

        var byKey = probes.ToDictionary(probe => probe.Key, StringComparer.Ordinal);
        var confirmed = 0;
        foreach (var (step, item) in Deletes(second))
        {
            var operation = OperationId(Confirm, step.OperationId);
            if (!byKey.TryGetValue(item.Key, out var probe) || !string.Equals(probe.StaleUniqueId, item.StaleUniqueId, StringComparison.OrdinalIgnoreCase))
                return Fail(DocUpdateDependentsCodes.DependentsChanged, item.Key, operation, "args.element_uids", item.StaleUniqueId);
            confirmed++;
            JsonObject args;
            try { args = JsonNode.Parse(step.ArgsJson) as JsonObject ?? throw new JsonException(); }
            catch (JsonException) { return Fail(DocReplaceCodes.RegenerationInvalid, item.Key, operation, "args", null); }
            args["dry_run"] = true;
            var (reported, failure) = DryRun(document, operation, args, item.StaleUniqueId!, transport);
            if (failure is not null)
            {
                if (TargetMissing(failure) && probe.Missing) continue;
                return TargetMissing(failure)
                    ? Fail(DocUpdateDependentsCodes.DependentsChanged, item.Key, operation, "args.element_uids", item.StaleUniqueId)
                    : Fail(failure.Code, item.Key, operation, failure.Field, failure.Detail);
            }
            if (probe.Missing) return Fail(DocUpdateDependentsCodes.DependentsChanged, item.Key, operation, "args.element_uids", item.StaleUniqueId);
            var now = reported!;
            if (!now.SequenceEqual(probe.Dependents, StringComparer.OrdinalIgnoreCase))
            {
                var differs = now.Except(probe.Dependents, StringComparer.OrdinalIgnoreCase).Concat(probe.Dependents.Except(now, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(uid => uid.ToLowerInvariant(), StringComparer.Ordinal).FirstOrDefault();
                return Fail(DocUpdateDependentsCodes.DependentsChanged, item.Key, operation, "dependents", differs);
            }
        }
        if (confirmed != probes.Count) return Fail(DocUpdateDependentsCodes.DependentsChanged, null, null, "args.element_uids", null);
        return new DocUpdateDependentsResult(first, probes.ToArray(), Copy(dependents), second, null);
    }

    // Plan, then DocUpdate.Run of the second plan when both passes succeeded; nothing is written otherwise.
    public static DocUpdateDependentsRun Update(Project before, Project after, RevitBindings bindings, IReadOnlyDictionary<string, string> realized,
        IRevitCommandTransport transport, IReadOnlyCollection<string>? accepted = null)
    {
        var planned = Plan(before, after, bindings, realized, transport, accepted);
        return new DocUpdateDependentsRun(planned, planned.Plan is { } plan && planned.Failed is null ? DocUpdate.Run(plan, transport) : null);
    }

    // The plan's element.delete steps in run order, with their replacement.
    static IEnumerable<(RevitCommandStep Step, DocReplacement Item)> Deletes(DocUpdatePlan plan)
    {
        var byOperation = plan.Replacement.Replacements.Where(item => item.DeleteOperation is not null && item.StaleUniqueId is not null)
            .ToDictionary(item => item.DeleteOperation!, StringComparer.Ordinal);
        foreach (var step in plan.Replacement.Commands.Steps)
            if (step.Command == DocReplace.DeleteCommand && byOperation.TryGetValue(step.OperationId, out var item)) yield return (step, item);
    }

    static bool TargetMissing(DocUpdateDependentsFailure failure) => failure.Code == RevitCommandCodes.ReferenceNotFound
        && failure.Field is { } field && (field == "args.element_uids" || field.StartsWith("args.element_uids[", StringComparison.Ordinal));

    static ReadOnlyDictionary<string, IReadOnlyList<string>> Copy(SortedDictionary<string, IReadOnlyList<string>> dependents)
    {
        var copy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var pair in dependents) copy[pair.Key] = pair.Value.ToArray();
        return new ReadOnlyDictionary<string, IReadOnlyList<string>>(copy);
    }

    static string OperationId(string pass, string deleteOperation) =>
        "docdry." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("doc-update-dependents/" + Version.ToString(CultureInfo.InvariantCulture) + "\n" + pass + "\n" + deleteOperation)), 0, 16).ToLowerInvariant();

    static DocUpdateDependentsFailure Failure(string code, string? field, string? detail) => new(code, null, null, field, detail);

    // One dry-run element.delete: envelope, transport, strict reply and receipt (as PlanRunner checks them), and the
    // delete readback's dependents, sorted.
    static (string[]? Dependents, DocUpdateDependentsFailure? Failure) DryRun(RevitDocumentRef document, string operation, JsonObject args, string target, IRevitCommandTransport transport)
    {
        RevitCommandEnvelope envelope;
        try
        {
            var request = new JsonObject
            {
                ["protocol_version"] = RevitCommandProtocol.Version,
                ["operation_id"] = operation,
                ["document"] = new JsonObject { ["path"] = document.Path, ["unique_id"] = document.UniqueId },
                ["command"] = DocReplace.DeleteCommand,
                ["args"] = args,
            };
            envelope = RevitCommandProtocol.Parse(request.ToJsonString());
        }
        catch (RevitCommandException error) { return (null, Failure(error.Code, error.Field, error.Code == RevitCommandCodes.EnvelopeInvalid ? "the envelope was refused" : error.Detail)); }
        if (envelope.Args is not ElementDeleteArgs { DryRun: true }) return (null, Failure(RevitCommandCodes.ArgsInvalid, "args.dry_run", "not a dry run"));
        string? line;
        try { line = transport.Send(envelope.CanonicalJson); }
        catch (RevitPipeReadTimeoutException error) { return (null, Failure(RevitCommandCodes.PipeReadTimeout, null, RevitCommandService.ReadTimeoutDetail(envelope, error.Timeout))); }
        catch (TimeoutException error) { return (null, Failure(RevitCommandCodes.PipeConnectTimeout, null, error.GetType().Name)); }
        catch (Exception error) { return (null, Failure(RevitCommandCodes.PipeIoError, null, error.GetType().Name)); }
        if (line is null) return (null, Failure(RevitCommandCodes.NoResponse, null, "the transport returned no reply"));
        RevitCommandResponse response;
        try { response = RevitCommandResponse.Parse(line); }
        catch (RevitCommandException error) { return (null, Failure(RevitCommandCodes.ResponseInvalid, error.Field, PlanRunner.MalformedReplyDetail)); }
        if (!response.IsSuccess)
            return (null, response.Code == RevitCommandCodes.Ok ? Failure(RevitCommandCodes.ResponseInvalid, "code", "a failed reply with code ok") : Failure(response.Code, response.Field, response.Detail));
        var receipt = response.Receipt!;
        var mismatch = receipt.OperationId != envelope.OperationId ? "operation_id"
            : receipt.Command != envelope.Command ? "command"
            : receipt.DocumentUniqueId != envelope.Document.UniqueId ? "document_unique_id"
            : receipt.RequestSha256 != envelope.RequestSha256 ? "request_sha256"
            : null;
        if (mismatch is not null) return (null, Failure(RevitCommandCodes.ResponseInvalid, "receipt", "receipt " + mismatch + " does not match the request"));
        // A dry run stores no receipt, so it is never a replay.
        if (receipt.Replayed) return (null, Failure(RevitCommandCodes.ResponseInvalid, "receipt.replayed", "a dry run cannot be replayed"));
        if (response.Created.Count > 0 || response.Modified.Count > 0 || response.Deleted.Count > 0)
            return (null, Failure(RevitCommandCodes.ResponseInvalid, "deleted", "a dry run reported changes"));
        var dependents = Readback(response.ReadbackJson, target);
        return dependents is null ? (null, Failure(RevitCommandCodes.ResponseInvalid, "readback", "delete dry-run readback is not well formed")) : (dependents, null);
    }

    // {"delete":{"dry_run":true,"targets":[target],"dependents":[...],"dependents_count":n,"dependents_sha256":"...","dependents_listed":true,...}}
    static string[]? Readback(string? json, string target)
    {
        if (json is null) return null;
        try
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("delete", out var delete) || delete.ValueKind != JsonValueKind.Object) return null;
            if (!delete.TryGetProperty("dry_run", out var dryRun) || dryRun.ValueKind != JsonValueKind.True) return null;
            if (!delete.TryGetProperty("dependents_listed", out var listed) || listed.ValueKind != JsonValueKind.True) return null;
            if (!delete.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Array || targets.GetArrayLength() != 1
                || targets[0].ValueKind != JsonValueKind.String || !string.Equals(targets[0].GetString(), target, StringComparison.OrdinalIgnoreCase)) return null;
            if (!delete.TryGetProperty("dependents", out var list) || list.ValueKind != JsonValueKind.Array) return null;
            var uids = new List<string>();
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String || !RevitCommandProtocol.IsUniqueId(entry.GetString())) return null;
                uids.Add(entry.GetString()!);
            }
            if (uids.Distinct(StringComparer.OrdinalIgnoreCase).Count() != uids.Count || uids.Contains(target, StringComparer.OrdinalIgnoreCase)) return null;
            if (!delete.TryGetProperty("dependents_count", out var count) || count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var number) || number != uids.Count) return null;
            var text = string.Join("\n", uids.Select(uid => uid.ToLowerInvariant()).OrderBy(uid => uid, StringComparer.Ordinal));
            var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
            if (!delete.TryGetProperty("dependents_sha256", out var hash) || hash.ValueKind != JsonValueKind.String || hash.GetString() != sha) return null;
            return uids.OrderBy(uid => uid.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(uid => uid, StringComparer.Ordinal).ToArray();
        }
        catch (JsonException) { return null; }
    }
}
