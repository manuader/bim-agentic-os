using Bim.Core;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Answers element.delete dry runs as the add-in does (nothing deleted, no receipt stored, the cascade reported in the
// delete readback, a missing target refused reference_not_found) and passes every other request to a
// ReplaceFakeTransport, which executes it. Calls records every request in send order.
sealed class DependentsFakeTransport : IRevitCommandTransport
{
    readonly Dictionary<string, int> dryRunsByTarget = new(StringComparer.OrdinalIgnoreCase);
    // Operation ids already passed to Inner: the add-in answers those from its receipt store before resolving targets.
    readonly HashSet<string> forwarded = new(StringComparer.Ordinal);

    public DependentsFakeTransport(ReplaceFakeTransport inner) => Inner = inner;
    public ReplaceFakeTransport Inner { get; }
    // "dry <operation id> <target>" for a dry-run delete, "write <command> <operation id>" for anything else.
    public List<string> Calls { get; } = new();
    public List<RevitCommandEnvelope> DryRuns { get; } = new();
    // Before a dry run is answered: its args and how many dry runs of that target this is (1-based).
    public Action<ElementDeleteArgs, int>? OnDryRun { get; set; }
    // Rewrites a dry-run reply (or replaces it with null).
    public Func<RevitCommandEnvelope, string, string?>? Rewrite { get; set; }
    // Answer a missing delete target (dry run or real) as the add-in does: field args.element_uids[i], detail
    // "no element with UniqueId <uid>" (CommandsExecutorModify.DeleteRun / ElementByUid).
    public bool AddInMissingForm { get; set; }

    string? MissingReply(ElementDeleteArgs delete)
    {
        for (var i = 0; i < delete.ElementUids.Count; i++)
            if (!Inner.Existing.Contains(delete.ElementUids[i]))
                return AddInMissingForm
                    ? RevitCommandResponse.Failure(RevitCommandCodes.ReferenceNotFound, "args.element_uids[" + i.ToString(CultureInfo.InvariantCulture) + "]", "no element with UniqueId " + delete.ElementUids[i]).ToJson()
                    : RevitCommandResponse.Failure(RevitCommandCodes.ReferenceNotFound, "args.element_uids", delete.ElementUids[i]).ToJson();
        return null;
    }

    public string? Send(string envelopeJson)
    {
        var envelope = RevitCommandProtocol.Parse(envelopeJson);
        if (envelope.Args is not ElementDeleteArgs { DryRun: true } delete)
        {
            Calls.Add("write " + envelope.Command + " " + envelope.OperationId);
            if (AddInMissingForm && !forwarded.Contains(envelope.OperationId) && envelope.Args is ElementDeleteArgs write && MissingReply(write) is { } refused) return refused;
            forwarded.Add(envelope.OperationId);
            return Inner.Send(envelopeJson);
        }
        Calls.Add("dry " + envelope.OperationId + " " + string.Join(",", delete.ElementUids));
        DryRuns.Add(envelope);
        var target = delete.ElementUids[0];
        dryRunsByTarget[target] = dryRunsByTarget.GetValueOrDefault(target) + 1;
        OnDryRun?.Invoke(delete, dryRunsByTarget[target]);
        var reply = MissingReply(delete);
        if (reply is null)
        {
            var dependents = delete.ElementUids.SelectMany(uid => Inner.DependentsOf.TryGetValue(uid, out var list) ? list : Array.Empty<string>())
                .Where(Inner.Existing.Contains).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(uid => uid, StringComparer.Ordinal).ToArray();
            var confirmed = dependents.OrderBy(uid => uid, StringComparer.OrdinalIgnoreCase).SequenceEqual(delete.ConfirmDependents.OrderBy(uid => uid, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            var receipt = new RevitCommandReceipt(envelope.OperationId, envelope.Command, envelope.Document.UniqueId, envelope.RequestSha256, false);
            reply = RevitCommandResponse.Success(receipt, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Readback(delete.ElementUids, dependents, confirmed), Array.Empty<string>()).ToJson();
        }
        return Rewrite is null ? reply : Rewrite(envelope, reply);
    }

    // The add-in's delete readback (CommandReadbackModify.DeleteJson) for a dry run.
    public static string Readback(IReadOnlyList<string> targets, IReadOnlyList<string> dependents, bool confirmed)
    {
        var text = string.Join("\n", dependents.Select(uid => uid.ToLowerInvariant()).OrderBy(uid => uid, StringComparer.Ordinal));
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("delete");
            writer.WriteBoolean("dry_run", true);
            writer.WriteStartArray("targets");
            foreach (var uid in targets) writer.WriteStringValue(uid);
            writer.WriteEndArray();
            writer.WriteStartArray("dependents");
            foreach (var uid in dependents) writer.WriteStringValue(uid);
            writer.WriteEndArray();
            writer.WriteNumber("dependents_count", dependents.Count);
            writer.WriteString("dependents_sha256", sha);
            writer.WriteBoolean("dependents_listed", true);
            writer.WriteNumber("deleted_count", targets.Count + dependents.Count);
            writer.WriteBoolean("confirmed", confirmed);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

// P4-09 oracle: DocUpdateDependents plans the documentation update once, dry-runs every planned delete through the
// transport (element.delete with dry_run true) to collect what Revit would delete with it, replans with those
// dependents, and dry-runs the replanned deletes again to confirm nothing changed in between. Nothing is written before
// the second plan; an unknown dependent (neither the stale instance of a regenerated item nor accepted by the caller),
// a kept one, or one that changed between the passes is refused typed; the same input gives the same bytes.
static class UpdateDependentsTests
{
    static readonly Provenance provenance = new("doc-update-dependents-tests", "unit");
    static readonly RevitDocumentRef document = new(@"C:\Repo\.bim\native-runs\p4-09\copy.rvt", "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd10");
    static Guid Id(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes("doc-update-dependents:" + name)).Take(16).ToArray());
    static readonly Guid projectId = Id("project");
    static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
    static Guid RoomOf(int room) => Id("room" + N(room));
    static Guid Role(int room, string role) => RoomCompiler.Id(RoomOf(room), role);
    static Guid Door(int room) => Id("door" + N(room));
    static string K(Guid id) => id.ToString("N");
    static string Json(object? value) => JsonSerializer.Serialize(value);

    static string? Code(Action action)
    {
        try { action(); return null; }
        catch (ArgumentNullException) { return "null"; }
        catch (ArgumentException error) { return error.Message; }
        catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
    }

    static string Fake(string source)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        return hex[..8] + "-" + hex[8..12] + "-" + hex[12..16] + "-" + hex[16..20] + "-" + hex[20..32] + "-" + hex[32..40];
    }

    static Project Build(params double[] elevations)
    {
        var elements = new List<Element>();
        for (var room = 0; room < elevations.Length; room++)
        {
            foreach (var element in RoomCompiler.Compile(projectId, 0, new RoomIntent(RoomOf(room), 4, 3, 2.8, 0.2, room * 10, 0, "m", provenance)).Elements)
                elements.Add(element.Kind == "level" ? element with { GeometryMetres = new[] { elevations[room] } } : element);
            var x = room * 10 + 0.1;
            elements.Add(new(Door(room), "door", new[] { x + 1, 0.1, x + 1.9, 0.1, 0, 2.1 }, new[] { Role(room, "south"), Role(room, "level") }, provenance));
        }
        return new(projectId, 0, elements.ToArray());
    }
    static RevitBindings Bindings(Project project) => new(document,
        project.Elements.Where(element => element.Kind == "level").ToDictionary(element => element.Id, element => "Level " + element.Id.ToString("N")[..8]),
        Array.Empty<RevitWallTypeBinding>(), null, null);
    static Project Remove(Project project, Guid id) => project with { Elements = project.Elements.Where(element => element.Id != id).ToArray() };
    static IEnumerable<string> Keys(DocumentationSet set) =>
        set.Views.Select(item => item.Key).Concat(set.Tags.Select(item => item.Key)).Concat(set.Dimensions.Select(item => item.Key))
            .Concat(set.Schedules.Select(item => item.Key)).Concat(set.Sheets.Select(item => item.Key)).Concat(set.Placements.Select(item => item.Key));
    static Dictionary<string, string> RealizedOf(Project project)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in Keys(DocDefaults.From(project))) map[DocRegeneration.ItemSource(key)] = Fake("stale:" + key);
        foreach (var element in project.Elements) map[DocRegeneration.ElementSource(element.Id)] = Fake("element:" + element.Id.ToString("D"));
        return map;
    }

    static string Args(RevitCommandEnvelope envelope)
    {
        using var canonical = JsonDocument.Parse(envelope.CanonicalJson);
        return canonical.RootElement.GetProperty("args").GetRawText();
    }
    static string[] Confirmed(RevitCommandStep step)
    {
        using var args = JsonDocument.Parse(step.ArgsJson);
        return args.RootElement.TryGetProperty("confirm_dependents", out var list) ? list.EnumerateArray().Select(item => item.GetString()!).ToArray() : Array.Empty<string>();
    }
    static string Failure(DocUpdateDependentsResult result) => result.Failed is null ? "none" : result.Failed.Code + " " + result.Failed.Key + " " + result.Failed.Field + " " + result.Failed.Detail;

    public static void Run(Checker check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "update dependents: " + name, detail);
        var before = Build(0, 3);
        var realized = RealizedOf(before);
        var inserted = Build(0, 3, -3);
        var bindings = Bindings(inserted);
        var plan0 = "plan-" + K(Role(0, "level"));
        var place0 = "place-plan-" + K(Role(0, "level"));
        var tagRoom0 = "tag-room-" + K(Role(0, "space"));
        var tagDoor0 = "tag-door-" + K(Door(0));
        var annotation = Fake("annotation:plan0");
        var plan0Stale = realized[DocRegeneration.ItemSource(plan0)];
        var reported = new[] { realized[DocRegeneration.ItemSource(tagRoom0)], realized[DocRegeneration.ItemSource(tagDoor0)], realized[DocRegeneration.ItemSource(place0)], annotation };
        var insertedRealized = new Dictionary<string, string>(realized, StringComparer.Ordinal);
        foreach (var element in inserted.Elements) insertedRealized.TryAdd(DocRegeneration.ElementSource(element.Id), Fake("element:" + element.Id.ToString("D")));

        // A Revit copy holding everything realized plus one annotation the documentation set does not track; deleting
        // room 0's plan takes its tags, its viewport and that annotation.
        DependentsFakeTransport Revit(IReadOnlyDictionary<string, string> map, params string[] dependents)
        {
            var inner = new ReplaceFakeTransport(map.Values.Append(annotation));
            inner.DependentsOf[plan0Stale] = dependents.Length > 0 ? dependents : reported;
            return new DependentsFakeTransport(inner);
        }
        var accepted = new[] { annotation };

        // ---- One call: plan, dry-run every planned delete, replan with the dependents, confirm ----
        var fake = Revit(insertedRealized);
        var result = DocUpdateDependents.Plan(before, inserted, bindings, insertedRealized, fake, accepted);
        var first = DocUpdate.Plan(before, inserted, bindings, insertedRealized);
        var firstDeletes = first.Replacement.Commands.Steps.Where(step => step.Command == DocReplace.DeleteCommand).ToArray();
        var staleByDelete = first.Replacement.Replacements.Where(item => item.DeleteOperation is not null).ToDictionary(item => item.DeleteOperation!, item => item.StaleUniqueId!, StringComparer.Ordinal);
        Check(result.Failed is null && result.Plan is not null, "a level insertion with an accepted untracked annotation plans in one call", Failure(result));
        Check(Json(result.First.Replacement) == Json(first.Replacement) && firstDeletes.Length > 3,
            "the first pass is DocUpdate.Plan without dependents", N(firstDeletes.Length));
        Check(fake.Calls.Count > 0 && fake.Calls.All(call => call.StartsWith("dry ", StringComparison.Ordinal)) && fake.Inner.Sent.Count == 0 && fake.Inner.Log.Count == 0
            && fake.DryRuns.All(envelope => envelope.Command == DocReplace.DeleteCommand && envelope.Args is ElementDeleteArgs { DryRun: true }),
            "planning sends only element.delete with dry_run true and writes nothing", string.Join(" | ", fake.Calls.Where(call => !call.StartsWith("dry ", StringComparison.Ordinal))));
        var collect = fake.DryRuns.Take(firstDeletes.Length).ToArray();
        Check(fake.DryRuns.Count == 2 * firstDeletes.Length
            && collect.Select(envelope => ((ElementDeleteArgs)envelope.Args).ElementUids.Single()).SequenceEqual(firstDeletes.Select(step => staleByDelete[step.OperationId]), StringComparer.Ordinal)
            && collect.All(envelope => Args(envelope) == "{\"dry_run\":true,\"element_uids\":[\"" + ((ElementDeleteArgs)envelope.Args).ElementUids[0] + "\"]}"),
            "every planned delete is dry-run once bare (its stale target only, in plan order), then once more as replanned",
            string.Join(" | ", fake.DryRuns.Select(Args)));
        var plan0Dependents = result.Dependents.GetValueOrDefault(plan0);
        Check(plan0Dependents is not null && plan0Dependents.SequenceEqual(reported.OrderBy(uid => uid, StringComparer.Ordinal), StringComparer.Ordinal)
            && result.Dependents.Where(pair => pair.Key != plan0).All(pair => pair.Value.Count == 0)
            && result.Dependents.Keys.SequenceEqual(first.Replacement.Replacements.Where(item => item.DeleteOperation is not null).Select(item => item.Key), StringComparer.Ordinal),
            "the collected dependents are what the dry runs reported, per planned delete, sorted", Json(result.Dependents));
        var replanned = DocUpdate.Plan(before, inserted, bindings, insertedRealized, result.Dependents);
        Check(result.Plan is not null && Json(result.Plan.Replacement) == Json(replanned.Replacement) && Json(result.Plan.Dependents) == Json(result.Dependents),
            "the second plan is DocUpdate.Plan with the collected dependents, byte-identical");
        var plan0Delete = first.Replacement.Replacements.Single(item => item.Key == plan0).DeleteOperation;
        var plan0Replanned = result.Plan?.Replacement.Replacements.Single(item => item.Key == plan0).DeleteOperation;
        Check(Confirmed(first.Replacement.Commands.Steps.Single(step => step.OperationId == plan0Delete)).Length == 0
            && result.Plan is not null && Confirmed(result.Plan.Replacement.Commands.Steps.Single(step => step.OperationId == plan0Replanned)).SequenceEqual(new[] { annotation }),
            "the replanned delete of the view confirms the untracked annotation (its tags and viewport are deleted earlier)");
        var verify = fake.DryRuns.Skip(firstDeletes.Length).ToArray();
        var secondDeletes = result.Plan?.Replacement.Commands.Steps.Where(step => step.Command == DocReplace.DeleteCommand).ToArray() ?? Array.Empty<RevitCommandStep>();
        Check(verify.Length == secondDeletes.Length && secondDeletes.Length > 0 && verify.Zip(secondDeletes).All(pair =>
            {
                var expected = System.Text.Json.Nodes.JsonNode.Parse(pair.Second.ArgsJson)!.AsObject();
                expected["dry_run"] = true;
                var expectedArgs = (ElementDeleteArgs)RevitCommandProtocol.Parse("{\"protocol_version\":1,\"operation_id\":\"op.x\",\"document\":" + JsonSerializer.Serialize(new { path = document.Path, unique_id = document.UniqueId })
                    + ",\"command\":\"element.delete\",\"args\":" + expected.ToJsonString() + "}").Args;
                var sent = (ElementDeleteArgs)pair.First.Args;
                return sent.DryRun && sent.ElementUids.SequenceEqual(expectedArgs.ElementUids) && sent.ConfirmDependents.SequenceEqual(expectedArgs.ConfirmDependents);
            }),
            "the confirming dry runs send exactly the replanned delete args with dry_run true");
        Check(result.Probes.Count == firstDeletes.Length && result.Probes.All(probe => !probe.Missing)
            && result.Probes.Select(probe => probe.Key).SequenceEqual(firstDeletes.Select(step => first.Replacement.Replacements.Single(item => item.DeleteOperation == step.OperationId).Key)),
            "one probe per planned delete, in plan order", Json(result.Probes.Select(probe => probe.Key)));

        // ---- Update: the replanned plan runs only after both passes ----
        var runFake = Revit(insertedRealized);
        var update = DocUpdateDependents.Update(before, inserted, bindings, insertedRealized, runFake, accepted);
        var firstWrite = runFake.Calls.FindIndex(call => call.StartsWith("write ", StringComparison.Ordinal));
        var lastDry = runFake.Calls.FindLastIndex(call => call.StartsWith("dry ", StringComparison.Ordinal));
        Check(update.Planned.Failed is null && update.Run is not null && update.Run.Report.Status == RevitPlanRunStatus.Completed && update.Run.Realized is not null,
            "Update runs the replanned plan to completion", update.Run?.Report.Failed is { } failedRun ? failedRun.Code + " " + failedRun.Detail : Failure(update.Planned));
        Check(firstWrite > lastDry && lastDry >= 0 && runFake.Calls.Count(call => call.StartsWith("dry ", StringComparison.Ordinal)) == 2 * firstDeletes.Length
            && runFake.Inner.Sent.All(envelope => envelope.Args is not ElementDeleteArgs { DryRun: true }),
            "no write is sent before the second plan: every dry run precedes the first write", N(firstWrite) + " " + N(lastDry));
        Check(update.Run is not null && Json(update.Run.Plan.Replacement) == Json(replanned.Replacement) && !runFake.Inner.Existing.Contains(annotation)
            && runFake.Inner.Log.Contains("delete " + annotation),
            "the run is the replanned plan and removes the confirmed annotation with its view");

        // ---- Unknown dependent: not accepted, not the stale instance of a regenerated item ----
        var unknownFake = Revit(insertedRealized);
        var unknown = DocUpdateDependents.Plan(before, inserted, bindings, insertedRealized, unknownFake);
        Check(unknown.Failed is { Code: DocUpdateDependentsCodes.DependentUnknown } failure && failure.Key == plan0 && failure.Detail == annotation && unknown.Plan is null
            && unknownFake.Inner.Sent.Count == 0 && unknownFake.Calls.All(call => call.StartsWith("dry ", StringComparison.Ordinal)),
            "an untracked dependent the caller did not accept is refused typed (dependent_unknown) and nothing is written", Failure(unknown));
        var unknownRun = Revit(insertedRealized);
        var unknownUpdate = DocUpdateDependents.Update(before, inserted, bindings, insertedRealized, unknownRun);
        Check(unknownUpdate.Run is null && unknownUpdate.Planned.Failed?.Code == DocUpdateDependentsCodes.DependentUnknown && unknownRun.Inner.Sent.Count == 0 && unknownRun.Inner.Log.Count == 0,
            "Update with a refused dependent does not run");

        // ---- A kept element as dependent ----
        var wallUid = insertedRealized[DocRegeneration.ElementSource(Role(0, "west"))];
        var keptFake = Revit(insertedRealized, reported.Append(wallUid).ToArray());
        var kept = DocUpdateDependents.Plan(before, inserted, bindings, insertedRealized, keptFake, accepted.Append(wallUid).ToArray());
        Check(kept.Failed is { Code: DocReplaceCodes.DependentKept, Key: var keptKey } && keptKey == plan0 && kept.Plan is null && keptFake.Inner.Sent.Count == 0,
            "a dependent realized for a kept source (a model wall) is refused dependent_kept even when accepted", Failure(kept));

        // ---- Dependents that change between the dry run and the replanned confirmation ----
        var extra = Fake("annotation:late");
        var grownFake = Revit(insertedRealized);
        grownFake.Inner.Existing.Add(extra);
        grownFake.OnDryRun = (args, count) => { if (count == 2 && args.ElementUids[0] == plan0Stale) grownFake.Inner.DependentsOf[plan0Stale] = reported.Append(extra).ToArray(); };
        var grown = DocUpdateDependents.Plan(before, inserted, bindings, insertedRealized, grownFake, accepted.Append(extra).ToArray());
        Check(grown.Failed is { Code: DocUpdateDependentsCodes.DependentsChanged, Key: var grownKey } && grownKey == plan0 && grown.Plan is null && grownFake.Inner.Sent.Count == 0,
            "a dependent that appears between the passes is refused typed (dependents_changed), nothing written", Failure(grown));
        var shrunkFake = Revit(insertedRealized);
        shrunkFake.OnDryRun = (args, count) => { if (count == 2 && args.ElementUids[0] == plan0Stale) shrunkFake.Inner.Existing.Remove(annotation); };
        var shrunk = DocUpdateDependents.Update(before, inserted, bindings, insertedRealized, shrunkFake, accepted);
        Check(shrunk.Planned.Failed?.Code == DocUpdateDependentsCodes.DependentsChanged && shrunk.Run is null && shrunkFake.Inner.Log.Count == 0 && shrunkFake.Inner.Sent.Count == 0,
            "a dependent that disappears between the passes is refused typed and Update writes nothing", Failure(shrunk.Planned));
        var vanishedFake = Revit(insertedRealized);
        vanishedFake.OnDryRun = (args, count) => { if (count == 2 && args.ElementUids[0] == plan0Stale) vanishedFake.Inner.Existing.Remove(plan0Stale); };
        var vanished = DocUpdateDependents.Plan(before, inserted, bindings, insertedRealized, vanishedFake, accepted);
        Check(vanished.Failed is { Code: DocUpdateDependentsCodes.DependentsChanged, Key: var vanishedKey } && vanishedKey == plan0 && vanished.Plan is null,
            "a delete target that disappears between the passes is refused dependents_changed", Failure(vanished));

        // ---- Malformed or failed dry-run replies ----
        string Plan0Reply(DependentsFakeTransport transport, Func<string, string?> change)
        {
            transport.Rewrite = (envelope, reply) => ((ElementDeleteArgs)envelope.Args).ElementUids[0] == plan0Stale ? change(reply) : reply;
            var outcome = DocUpdateDependents.Plan(before, inserted, bindings, insertedRealized, transport, accepted);
            return outcome.Plan is null && transport.Inner.Sent.Count == 0 && outcome.Failed is { } failed ? failed.Code + (failed.Key == plan0 ? "" : " key " + failed.Key) : "accepted " + Failure(outcome);
        }
        Check(Plan0Reply(Revit(insertedRealized), reply => reply.Replace("\"dependents_count\":4", "\"dependents_count\":5", StringComparison.Ordinal)) == RevitCommandCodes.ResponseInvalid,
            "a dependents count that disagrees with the list is response_invalid");
        Check(Plan0Reply(Revit(insertedRealized), reply => reply.Replace("\"dry_run\":true", "\"dry_run\":false", StringComparison.Ordinal)) == RevitCommandCodes.ResponseInvalid,
            "a reply that is not a dry run is response_invalid");
        Check(Plan0Reply(Revit(insertedRealized), reply => reply.Replace("\"dependents_sha256\":\"", "\"dependents_sha256\":\"0", StringComparison.Ordinal)) == RevitCommandCodes.ResponseInvalid,
            "a dependents hash that disagrees with the list is response_invalid");
        Check(Plan0Reply(Revit(insertedRealized), reply => reply.Replace(plan0Stale + "\"],\"dependents\"", Fake("other") + "\"],\"dependents\"", StringComparison.Ordinal)) == RevitCommandCodes.ResponseInvalid,
            "a reply naming another target is response_invalid");
        Check(Plan0Reply(Revit(insertedRealized), reply => reply.Replace("\"request_sha256\":\"", "\"request_sha256\":\"0", StringComparison.Ordinal)) == RevitCommandCodes.ResponseInvalid,
            "a receipt that does not answer the request is response_invalid");
        Check(Plan0Reply(Revit(insertedRealized), _ => "{\"status\":") == RevitCommandCodes.ResponseInvalid, "a malformed reply is response_invalid");
        Check(Plan0Reply(Revit(insertedRealized), _ => null) == RevitCommandCodes.NoResponse, "no reply is no_response");
        Check(Plan0Reply(Revit(insertedRealized), _ => RevitCommandResponse.Failure(RevitCommandCodes.ArgsInvalid, "args.element_uids", "pinned").ToJson()) == RevitCommandCodes.ArgsInvalid,
            "a refused dry run keeps the executor's code");

        // ---- The stale instance is already gone in Revit: no dependents, and the run reports it gone ----
        var removed = Remove(before, Door(1));
        var tagDoor1 = "tag-door-" + K(Door(1));
        var doorUid = realized[DocRegeneration.ElementSource(Door(1))];
        var tagUid = realized[DocRegeneration.ItemSource(tagDoor1)];
        var goneFake = new DependentsFakeTransport(new ReplaceFakeTransport(realized.Values.Where(uid => uid != doorUid && uid != tagUid)));
        var gone = DocUpdateDependents.Update(before, removed, Bindings(removed), realized, goneFake);
        Check(gone.Planned.Failed is null && gone.Planned.Probes.FirstOrDefault(item => item.Key == tagDoor1) is { Missing: true } probe && probe.Dependents.Count == 0
            && gone.Planned.Probes.Count(item => item.Missing) == 1
            && !gone.Planned.Dependents.ContainsKey(tagDoor1)
            && gone.Run is { Report.Status: RevitPlanRunStatus.Completed } goneRun && goneRun.Plan.Gone.SequenceEqual(new[] { tagDoor1 }) && !goneFake.Inner.Log.Contains("delete " + tagUid),
            "a stale instance Revit already deleted is probed missing, gets no dependents, and the run reports it gone", Failure(gone.Planned));

        // ---- No edit: nothing to dry-run, nothing sent ----
        var quietFake = Revit(realized);
        var quiet = DocUpdateDependents.Update(before, before, Bindings(before), realized, quietFake);
        Check(quiet.Planned.Failed is null && quiet.Planned.Probes.Count == 0 && quiet.Planned.Dependents.Count == 0 && quietFake.Calls.Count == 0
            && quiet.Run is { Report.Status: RevitPlanRunStatus.Completed } && quiet.Planned.Plan!.Replacement.Commands.Steps.Count == 0,
            "a no-edit update dry-runs nothing and sends nothing", string.Join(" | ", quietFake.Calls));

        // ---- Determinism: same input, same bytes, across map order and culture ----
        string Bytes(DocUpdateDependentsResult outcome) => Json(outcome.Plan?.Replacement) + "\n" + Json(outcome.Dependents) + "\n" + Json(outcome.Probes) + "\n" + Json(outcome.Failed)
            + "\n" + Json(outcome.First.Replacement) + "\n" + Json(outcome.Plan?.Realized);
        var baseline = Bytes(result);
        var reversed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in insertedRealized.Reverse()) reversed[pair.Key] = pair.Value;
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        var identical = Bytes(DocUpdateDependents.Plan(before, inserted, bindings, insertedRealized, Revit(insertedRealized), accepted)) == baseline;
        var dryIds = string.Join(",", fake.DryRuns.Select(envelope => envelope.OperationId));
        try
        {
            foreach (var name in new[] { "tr-TR", "de-DE" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
                var other = Revit(reversed);
                identical &= Bytes(DocUpdateDependents.Plan(before, inserted, bindings, reversed, other, accepted.Select(uid => uid.ToUpperInvariant()).ToArray())) == baseline
                    && string.Join(",", other.DryRuns.Select(envelope => envelope.OperationId)) == dryIds;
                identical &= Bytes(DocUpdateDependents.Plan(before, inserted, bindings, reversed, Revit(reversed))) == Bytes(unknown);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
        Check(identical, "the same input gives byte-identical plans, dependents, probes, refusals and dry-run operation ids (reversed map, tr-TR, de-DE)");
        Check(fake.DryRuns.Select(envelope => envelope.OperationId).Distinct(StringComparer.Ordinal).Count() == fake.DryRuns.Count
            && fake.DryRuns.All(envelope => envelope.OperationId.StartsWith("docdry.", StringComparison.Ordinal))
            && result.Plan is not null && fake.DryRuns.All(envelope => result.Plan!.Replacement.Commands.Steps.Concat(first.Replacement.Commands.Steps).All(step => step.OperationId != envelope.OperationId)),
            "dry-run operation ids are distinct, docdry.-prefixed and never a plan step's id", dryIds);

        // ---- Review follow-ups (SF1-SF4, NITs) ----
        var sheet0 = "sheet-" + K(Role(0, "level"));
        var sheet0Stale = realized[DocRegeneration.ItemSource(sheet0)];
        var shared = Fake("viewport:shared");
        var sharedFake = Revit(insertedRealized, reported.Append(shared.ToUpperInvariant()).ToArray());
        sharedFake.Inner.Existing.Add(shared);
        sharedFake.Inner.DependentsOf[sheet0Stale] = new[] { shared };
        var sharedResult = DocUpdateDependents.Plan(before, inserted, bindings, insertedRealized, sharedFake, accepted.Append(shared).ToArray());
        Check(sharedResult.Failed is { Code: "dependent_shared", Key: var sharedKey, Detail: var sharedDetail } && sharedKey == plan0
            && string.Equals(sharedDetail, shared, StringComparison.OrdinalIgnoreCase) && sharedResult.Plan is null && sharedFake.Inner.Sent.Count == 0,
            "an accepted dependent reported by two planned deletes (case ignored) is refused dependent_shared at the later key, nothing written", Failure(sharedResult));
        var sharedRun = Revit(insertedRealized, reported.Append(shared).ToArray());
        sharedRun.Inner.Existing.Add(shared);
        sharedRun.Inner.DependentsOf[sheet0Stale] = new[] { shared };
        var sharedUpdate = DocUpdateDependents.Update(before, inserted, bindings, insertedRealized, sharedRun, accepted.Append(shared).ToArray());
        Check(sharedUpdate.Run is null && sharedUpdate.Planned.Failed?.Code == "dependent_shared" && sharedRun.Inner.Sent.Count == 0 && sharedRun.Inner.Log.Count == 0,
            "Update with a shared dependent writes nothing");

        var tagRoom0Stale = realized[DocRegeneration.ItemSource(tagRoom0)];
        var laterFake = Revit(insertedRealized);
        laterFake.Inner.DependentsOf[tagRoom0Stale] = new[] { plan0Stale };
        var later = DocUpdateDependents.Plan(before, inserted, bindings, insertedRealized, laterFake, accepted);
        Check(later.Failed is { Code: DocReplaceCodes.DependentPlanned, Key: var laterKey, Detail: var laterDetail } && laterKey == tagRoom0 && laterDetail == plan0Stale
            && later.Plan is null && laterFake.Inner.Sent.Count == 0 && laterFake.DryRuns.Count <= firstDeletes.Length,
            "a dependent that is the stale instance of a key deleted later is refused dependent_planned in the first pass, naming key and UniqueId", Failure(later));

        var appearedFake = Revit(insertedRealized);
        appearedFake.OnDryRun = (args, count) =>
        {
            if (args.ElementUids[0] != plan0Stale) return;
            if (count == 1) appearedFake.Inner.Existing.Remove(plan0Stale); else appearedFake.Inner.Existing.Add(plan0Stale);
        };
        var appeared = DocUpdateDependents.Plan(before, inserted, bindings, insertedRealized, appearedFake, accepted);
        Check(appeared.Failed is { Code: DocUpdateDependentsCodes.DependentsChanged, Key: var appearedKey } && appearedKey == plan0 && appeared.Plan is null && appearedFake.Inner.Sent.Count == 0
            && appeared.Probes.Any(item => item.Key == plan0 && item.Missing),
            "a target missing on the first dry run but present on the confirming one is refused dependents_changed", Failure(appeared));

        var confirmRefused = Revit(insertedRealized);
        var plan0Seen = 0;
        confirmRefused.Rewrite = (envelope, reply) => ((ElementDeleteArgs)envelope.Args).ElementUids[0] == plan0Stale && ++plan0Seen == 2
            ? RevitCommandResponse.Failure(RevitCommandCodes.ArgsInvalid, "args.element_uids[0]", "pinned").ToJson() : reply;
        var refusedOnConfirm = DocUpdateDependents.Plan(before, inserted, bindings, insertedRealized, confirmRefused, accepted);
        Check(refusedOnConfirm.Failed is { Code: RevitCommandCodes.ArgsInvalid, Key: var refusedKey } && refusedKey == plan0 && refusedOnConfirm.Plan is null
            && confirmRefused.Inner.Sent.Count == 0 && plan0Seen == 2,
            "an executor refusal on the confirming dry run only keeps its code and nothing is written", Failure(refusedOnConfirm));

        Check(Plan0Reply(Revit(insertedRealized), _ => throw new TimeoutException()) == RevitCommandCodes.PipeConnectTimeout, "a transport timeout is pipe_connect_timeout");
        Check(Plan0Reply(Revit(insertedRealized), _ => throw new IOException("broken pipe")) == RevitCommandCodes.PipeIoError, "any other transport exception is pipe_io_error");
        Check(Plan0Reply(Revit(insertedRealized), reply => reply.Replace("\"replayed\":false", "\"replayed\":true", StringComparison.Ordinal)) == RevitCommandCodes.ResponseInvalid,
            "a replayed receipt for a dry run (which stores none) is response_invalid");

        var caseFake = Revit(insertedRealized);
        caseFake.OnDryRun = (args, count) =>
        {
            if (count == 2 && args.ElementUids[0] == plan0Stale) caseFake.Inner.DependentsOf[plan0Stale] = reported.Select(uid => uid.ToUpperInvariant()).ToArray();
        };
        var cased = DocUpdateDependents.Update(before, inserted, bindings, insertedRealized, caseFake, accepted);
        var upperFake = Revit(insertedRealized, reported.Select(uid => uid.ToUpperInvariant()).ToArray());
        var upper = DocUpdateDependents.Update(before, inserted, bindings, insertedRealized, upperFake, accepted);
        Check(cased.Planned.Failed is null && cased.Run is { Report.Status: RevitPlanRunStatus.Completed } && upper.Planned.Failed is null && upper.Run is { Report.Status: RevitPlanRunStatus.Completed }
            && !upperFake.Inner.Existing.Contains(annotation),
            "dependents reported in another hex case match their stale instances, accepted UniqueIds and the first pass", Failure(cased.Planned) + " / " + Failure(upper.Planned));

        var many = Enumerable.Range(0, RevitModifyRules.MaxConfirmedDependents + 1).Select(index => Fake("many:" + N(index))).ToArray();
        var manyFake = Revit(insertedRealized, many);
        foreach (var uid in many) manyFake.Inner.Existing.Add(uid);
        var tooMany = DocUpdateDependents.Plan(before, inserted, bindings, insertedRealized, manyFake, many);
        Check(tooMany.Failed is { Code: DocReplaceCodes.DependentsInvalid, Key: null } && tooMany.Plan is null && manyFake.Inner.Sent.Count == 0,
            "a second-plan refusal keeps DocReplace's code", Failure(tooMany));

        var addInGone = new DependentsFakeTransport(new ReplaceFakeTransport(realized.Values.Where(uid => uid != doorUid && uid != tagUid))) { AddInMissingForm = true };
        var goneAddIn = DocUpdateDependents.Update(before, removed, Bindings(removed), realized, addInGone);
        Check(goneAddIn.Planned.Failed is null && goneAddIn.Planned.Probes.FirstOrDefault(item => item.Key == tagDoor1) is { Missing: true }
            && goneAddIn.Run is { Report.Status: RevitPlanRunStatus.Completed } addInRun && addInRun.Plan.Gone.SequenceEqual(new[] { tagDoor1 })
            && Json(goneAddIn.Run.Realized) == Json(gone.Run?.Realized),
            "the add-in's missing-target form (args.element_uids[0], no element with UniqueId) is probed missing and run as gone", Failure(goneAddIn.Planned));

        Check(DocUpdateDependentsCodes.DependentShared == "dependent_shared" && DocUpdateDependentsCodes.ReplanInternal == "replan_internal",
            "the new refusal codes are stable strings");

        // ---- Refusals of the inputs ----
        Check(Code(() => DocUpdateDependents.Plan(null!, inserted, bindings, insertedRealized, Revit(insertedRealized))) == "null"
            && Code(() => DocUpdateDependents.Plan(before, inserted, bindings, insertedRealized, null!)) == "null"
            && Code(() => DocUpdateDependents.Update(before, inserted, bindings, null!, Revit(insertedRealized))) == "null", "null inputs are refused");
        var badFake = Revit(insertedRealized);
        Check(Code(() => DocUpdateDependents.Plan(before, inserted, bindings, insertedRealized, badFake, new[] { "not-a-unique-id" })) == DocUpdateDependentsCodes.AcceptedInvalid && badFake.Calls.Count == 0,
            "a malformed accepted UniqueId is refused before anything is sent");
        var badMap = new Dictionary<string, string>(insertedRealized, StringComparer.Ordinal) { ["view:x"] = Fake("x") };
        var badMapFake = Revit(insertedRealized);
        Check(Code(() => DocUpdateDependents.Plan(before, inserted, bindings, badMap, badMapFake)) == DocReplaceCodes.RealizedInvalid && badMapFake.Calls.Count == 0,
            "a first-plan refusal is DocUpdate's, unchanged, and nothing is sent");
    }
}
