using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Bim.Core;

// P4-05 stale documentation replacement. A documentation regeneration (DocRegeneration.Affected, or Full) says which
// item keys are regenerated; their earlier realization in the document is stale. Given what is realized now (source ->
// Revit UniqueId, the sources DocRegeneration uses: "doc:<key>" for a realized view, schedule, sheet, tag, dimension
// or placement, "element:<BIM-IR id>" for a realized model element), Plan builds one ordered catalogue RevitCommandPlan
// that first deletes the stale instance of every regenerated key (one element.delete per key, with the dependents a
// dry run reported confirmed), children (placements, dimensions, tags) before parents (sheets, schedules, views), and
// then re-creates every regenerated key with a step, parents before children. An input on a regenerated item is the
// created[0] of its re-creation step; any other input is filled from the realized map at plan time, so no stale or
// placeholder UniqueId is ever sent. A regenerated key whose new result is unsupported is deleted and not re-created.
// The plan runs through PlanRunner (no level preconditions); a failed run resumes with PlanRunner.Resume, sending only
// pending steps (operation ids are deterministic, so a resent step that already ran replays). Realized gives the
// realized map after a completed run. Vendor-free, BCL only; no BIM-IR or persisted format change.

// One regenerated key: its stale realization (null when it had none), the element.delete step that removes it (null
// without a stale instance) and the step that re-creates it (null when the new result is unsupported).
public sealed record DocReplacement(string Key, string ItemKind, string? StaleUniqueId, string? DeleteOperation, string? CreateOperation);

// Commands: deletes then creates, in run order. Replacements: one per regenerated key, by key (ordinal). Unsupported:
// the regeneration's unsupported entries (their keys are deleted, never re-created).
public sealed record DocReplacementPlan(RevitCommandPlan Commands, IReadOnlyList<DocReplacement> Replacements, IReadOnlyList<DocUnsupported> Unsupported);

public static class DocReplaceCodes
{
    // ArgumentException messages.
    public const string RegenerationInvalid = "regeneration_invalid";
    public const string RealizedInvalid = "realized_invalid";
    // A stale UniqueId that is also realized for another source: deleting it would remove something that is kept.
    public const string StaleShared = "stale_shared";
    public const string DependentsUnknownKey = "dependents_unknown_key";
    public const string DependentsWithoutStale = "dependents_without_stale";
    public const string DependentsInvalid = "dependents_invalid";
    // A confirmed dependent that is realized for a kept source (an item not regenerated, or a model element).
    public const string DependentKept = "dependent_kept";
    // A confirmed dependent that is the stale instance of another regenerated key (deleted at its own step).
    public const string DependentPlanned = "dependent_planned";
    // A confirmed dependent that is the key's own stale instance (the delete target itself).
    public const string DependentIsTarget = "dependent_is_target";
    public const string InputFieldUnsupported = "input_field_unsupported";
    public const string InputUnresolved = "input_unresolved";
    public const string InputFieldDuplicate = "input_field_duplicate";
    public const string ReportIncomplete = "report_incomplete";
    public const string CreatedMissing = "created_missing";
}

public static class DocReplace
{
    public const int Version = 1;
    public const string DeleteCommand = "element.delete";
    // Children before parents; the reverse of DocRegeneration's step order.
    static readonly string[] DeleteOrder = { DocRegeneration.Placement, DocRegeneration.Dimension, DocRegeneration.Tag, DocRegeneration.Sheet, DocRegeneration.Schedule, DocRegeneration.View };
    static readonly string[] CreateOrder = { DocRegeneration.View, DocRegeneration.Schedule, DocRegeneration.Sheet, DocRegeneration.Tag, DocRegeneration.Dimension, DocRegeneration.Placement };
    const string PendingOperationId = "op.pending";

    // dependents: per regenerated key, the UniqueIds a dry run of deleting its stale instance reported Revit would delete
    // with it (default none: the executor then refuses a delete that would take anything else).
    public static DocReplacementPlan Plan(DocRegenerationPlan regeneration, IReadOnlyDictionary<string, string> realized, IReadOnlyDictionary<string, IReadOnlyList<string>>? dependents = null)
    {
        ArgumentNullException.ThrowIfNull(regeneration);
        ArgumentNullException.ThrowIfNull(realized);
        dependents ??= new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (regeneration.Regenerated is null || regeneration.Steps is null || regeneration.Unsupported is null || regeneration.Document is null) throw new ArgumentException(DocReplaceCodes.RegenerationInvalid);
        if (regeneration.Steps.Any(step => step is null) || regeneration.Unsupported.Any(item => item is null) || regeneration.Regenerated.Any(key => key is null)) throw new ArgumentException(DocReplaceCodes.RegenerationInvalid);
        var regenerated = regeneration.Regenerated.ToHashSet(StringComparer.Ordinal);
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, kind) in regeneration.Steps.Select(step => (step.Key, step.ItemKind)).Concat(regeneration.Unsupported.Select(item => (item.Key, item.ItemKind))))
            if (key is null || !kinds.TryAdd(key, kind) || !CreateOrder.Contains(kind, StringComparer.Ordinal)) throw new ArgumentException(DocReplaceCodes.RegenerationInvalid);
        // Every regenerated key has exactly one step or one unsupported entry, and nothing else has either.
        if (regenerated.Count != regeneration.Regenerated.Count || kinds.Count != regenerated.Count || !kinds.Keys.All(regenerated.Contains)) throw new ArgumentException(DocReplaceCodes.RegenerationInvalid);

        foreach (var pair in realized)
            if (pair.Key is null || !(pair.Key.StartsWith("doc:", StringComparison.Ordinal) || pair.Key.StartsWith("element:", StringComparison.Ordinal)) || !RevitCommandProtocol.IsUniqueId(pair.Value))
                throw new ArgumentException(DocReplaceCodes.RealizedInvalid);
        var stale = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in regenerated)
            if (realized.TryGetValue(DocRegeneration.ItemSource(key), out var uid)) stale[key] = uid;
        var staleSources = stale.Keys.Select(DocRegeneration.ItemSource).ToHashSet(StringComparer.Ordinal);
        var staleUids = new HashSet<string>(stale.Values, StringComparer.OrdinalIgnoreCase);
        var keptUids = new HashSet<string>(realized.Where(pair => !staleSources.Contains(pair.Key)).Select(pair => pair.Value), StringComparer.OrdinalIgnoreCase);
        if (staleUids.Count != stale.Count || staleUids.Overlaps(keptUids)) throw new ArgumentException(DocReplaceCodes.StaleShared);

        // Delete order first: a dependent may name the stale instance of a key deleted earlier (an honest dry run of a
        // view lists its tags, dimensions and viewports); it is gone by then, so it is dropped from confirm_dependents.
        var deleteOrder = stale.Keys.OrderBy(key => Array.IndexOf(DeleteOrder, kinds[key])).ThenBy(key => key, StringComparer.Ordinal).ToArray();
        var deletePosition = deleteOrder.Select((key, index) => (key, index)).ToDictionary(pair => pair.key, pair => pair.index, StringComparer.Ordinal);
        var staleOwner = stale.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);
        var confirmedByKey = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var pair in dependents)
        {
            if (pair.Key is null || !regenerated.Contains(pair.Key)) throw new ArgumentException(DocReplaceCodes.DependentsUnknownKey);
            if (!stale.ContainsKey(pair.Key)) throw new ArgumentException(DocReplaceCodes.DependentsWithoutStale);
            if (pair.Value is null || pair.Value.Count > RevitModifyRules.MaxConfirmedDependents || pair.Value.Any(uid => !RevitCommandProtocol.IsUniqueId(uid))
                || pair.Value.Distinct(StringComparer.OrdinalIgnoreCase).Count() != pair.Value.Count) throw new ArgumentException(DocReplaceCodes.DependentsInvalid);
            var kept = new List<string>();
            foreach (var uid in pair.Value)
            {
                if (keptUids.Contains(uid)) throw new ArgumentException(DocReplaceCodes.DependentKept);
                if (staleOwner.TryGetValue(uid, out var owner))
                {
                    if (owner == pair.Key) throw new ArgumentException(DocReplaceCodes.DependentIsTarget);
                    if (deletePosition[owner] > deletePosition[pair.Key]) throw new ArgumentException(DocReplaceCodes.DependentPlanned);
                    continue;
                }
                kept.Add(uid);
            }
            confirmedByKey[pair.Key] = kept.OrderBy(uid => uid, StringComparer.Ordinal).ToArray();
        }

        var document = regeneration.Document;
        var steps = new List<RevitCommandStep>();
        var deleteOps = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in deleteOrder)
        {
            var confirmed = confirmedByKey.TryGetValue(key, out var list) ? list : Array.Empty<string>();
            var args = Json(writer =>
            {
                writer.WriteStartArray("element_uids");
                writer.WriteStringValue(stale[key]);
                writer.WriteEndArray();
                if (confirmed.Length > 0)
                {
                    writer.WriteStartArray("confirm_dependents");
                    foreach (var uid in confirmed) writer.WriteStringValue(uid);
                    writer.WriteEndArray();
                }
            });
            var canonical = CanonicalArgs(Parse(document, PendingOperationId, DeleteCommand, args));
            var operationId = "docdel." + Hash(regeneration.ProjectId.ToString("D") + "\n" + regeneration.Revision.ToString(CultureInfo.InvariantCulture) + "\n" + regeneration.AfterHash
                + "\n" + key + "\n" + document.UniqueId + "\n" + document.Path + "\n" + canonical);
            deleteOps[key] = operationId;
            steps.Add(new RevitCommandStep(operationId, DeleteCommand, null, kinds[key], Array.Empty<string>(), canonical, Array.Empty<RevitStepInput>(), Array.Empty<string>()));
        }

        var createOps = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in regeneration.Steps.OrderBy(step => Array.IndexOf(CreateOrder, step.ItemKind)).ThenBy(step => step.Key, StringComparer.Ordinal))
        {
            if (step.Inputs is null || step.ArgsJson is null || step.Command is null || step.OperationId is null) throw new ArgumentException(DocReplaceCodes.RegenerationInvalid);
            JsonObject args;
            try { args = JsonNode.Parse(step.ArgsJson) as JsonObject ?? throw new ArgumentException(DocReplaceCodes.RegenerationInvalid); }
            catch (JsonException) { throw new ArgumentException(DocReplaceCodes.RegenerationInvalid); }
            var inputs = new List<RevitStepInput>();
            var fields = new HashSet<string>(StringComparer.Ordinal);
            var depends = new List<string>();
            if (deleteOps.TryGetValue(step.Key, out var deleteOp)) depends.Add(deleteOp);
            foreach (var input in step.Inputs)
            {
                if (input?.Field is null || !input.Field.StartsWith("args.", StringComparison.Ordinal) || input.Field.Length == "args.".Length || input.Field.IndexOf('.', "args.".Length) >= 0)
                    throw new ArgumentException(DocReplaceCodes.InputFieldUnsupported);
                if (!fields.Add(input.Field)) throw new ArgumentException(DocReplaceCodes.InputFieldDuplicate);
                if (input.Source is null) throw new ArgumentException(DocReplaceCodes.InputUnresolved);
                var member = input.Field["args.".Length..];
                if (input.Source.StartsWith("doc:", StringComparison.Ordinal) && regenerated.Contains(input.Source["doc:".Length..]))
                {
                    // The parent is re-created by this plan: take its created[0]; it must come earlier.
                    if (!createOps.TryGetValue(input.Source["doc:".Length..], out var from)) throw new ArgumentException(DocReplaceCodes.InputUnresolved);
                    inputs.Add(new RevitStepInput(input.Field, from, "created[0]"));
                    if (!depends.Contains(from, StringComparer.Ordinal)) depends.Add(from);
                }
                else if (realized.TryGetValue(input.Source, out var uid)) args[member] = uid;
                else throw new ArgumentException(DocReplaceCodes.InputUnresolved);
            }
            var canonical = CanonicalArgs(Parse(document, PendingOperationId, step.Command, args.ToJsonString()));
            // The regeneration's operation id plus what was filled in and the delete it follows (which names the stale
            // instance): another realization of the inputs, or replacing another instance, is another request, so a
            // re-plan on the realized map never replays a create whose result it has just deleted.
            var operationId = "docnew." + Hash(step.OperationId + "\n" + canonical + "\n" + string.Join(",", inputs.Select(input => input.Field + "=" + input.FromOperation))
                + "\n" + (deleteOp ?? "-"));
            createOps[step.Key] = operationId;
            steps.Add(new RevitCommandStep(operationId, step.Command, null, step.ItemKind, depends.ToArray(), canonical, inputs.ToArray(), Array.Empty<string>()));
        }

        var replacements = regeneration.Regenerated.OrderBy(key => key, StringComparer.Ordinal).Select(key => new DocReplacement(key, kinds[key],
            stale.TryGetValue(key, out var uid) ? uid : null, deleteOps.TryGetValue(key, out var deleteOp) ? deleteOp : null, createOps.TryGetValue(key, out var createOp) ? createOp : null)).ToArray();
        var commands = new RevitCommandPlan(PlanToCommands.Version, regeneration.ProjectId, regeneration.Revision, regeneration.AfterHash, document,
            Array.Empty<RevitLevelPrecondition>(), steps.ToArray(), Array.Empty<RevitUnsupportedElement>());
        return new DocReplacementPlan(commands, replacements, regeneration.Unsupported.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray());
    }

    public static RevitPlanRunReport Run(DocReplacementPlan plan, IRevitCommandTransport transport)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return PlanRunner.Run(plan.Commands, transport);
    }

    // Sends only the pending steps of report (PlanRunner.Resume: report_plan_mismatch, report_invalid).
    public static RevitPlanRunReport Resume(DocReplacementPlan plan, RevitPlanRunReport report, IRevitCommandTransport transport)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return PlanRunner.Resume(plan.Commands, report, transport);
    }

    // The realized map after a completed run: every regenerated key's stale entry removed, and the created[0] of its
    // re-creation step recorded under "doc:<key>"; every other entry kept. The report is checked against the plan first.
    public static IReadOnlyDictionary<string, string> Realized(DocReplacementPlan plan, IReadOnlyDictionary<string, string> realized, RevitPlanRunReport report)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(realized);
        ArgumentNullException.ThrowIfNull(report);
        // Resume of a report without pending steps validates it against the plan and sends nothing.
        if (report.Pending is null || report.Pending.Count > 0 || report.Status != RevitPlanRunStatus.Completed) throw new ArgumentException(DocReplaceCodes.ReportIncomplete);
        PlanRunner.Resume(plan.Commands, report, new NoTransport());
        var done = report.Completed.ToDictionary(step => step.OperationId, StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in realized) result[pair.Key] = pair.Value;
        foreach (var replacement in plan.Replacements)
        {
            result.Remove(DocRegeneration.ItemSource(replacement.Key));
            if (replacement.CreateOperation is not { } operation) continue;
            if (!done.TryGetValue(operation, out var step) || step.Created.Count == 0) throw new ArgumentException(DocReplaceCodes.CreatedMissing);
            result[DocRegeneration.ItemSource(replacement.Key)] = step.Created[0];
        }
        return result;
    }

    sealed class NoTransport : IRevitCommandTransport
    {
        public string? Send(string envelopeJson) => throw new InvalidOperationException("nothing is sent for a completed report");
    }

    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("doc-replace/" + Version.ToString(CultureInfo.InvariantCulture) + "\n" + text)), 0, 16).ToLowerInvariant();

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
}
