using Bim.Core;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// P4-05 oracle: from a regeneration result (DocRegeneration.Affected or Full) and the realized map (source -> Revit
// UniqueId), DocReplace.Plan emits one ordered catalogue plan that deletes the stale instance of every regenerated key
// (element.delete, the dry-run dependents confirmed) before any re-creation, children before parents on delete and
// parents before children on create; each re-creation sends exactly DocRegeneration.Envelope's args with kept inputs
// from the map and regenerated parents from the new created[0]; it runs through PlanRunner with a fake executor that
// refuses unknown references and unconfirmed dependents, and resumes after a failure or a lost reply without
// re-deleting anything.

// A fake executor over a set of existing UniqueIds: element.delete removes its targets and what Revit would delete with
// them (refused unless confirm_dependents equals that set); a create refuses any top-level UniqueId arg that does not
// exist and adds a new UniqueId. operation_id receipts replay.
sealed class ReplaceFakeTransport : IRevitCommandTransport
{
    public const string Episode = "60f91daf-3dd7-4283-a86d-24137b73f3da-";
    readonly Dictionary<string, (string Sha, string Json)> receipts = new(StringComparer.Ordinal);
    int next = 0x900;

    public ReplaceFakeTransport(IEnumerable<string> existing) => Existing = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Existing { get; }
    // What Revit deletes together with a target (beyond the target), while those still exist.
    public Dictionary<string, string[]> DependentsOf { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<RevitCommandEnvelope> Sent { get; } = new();
    // "delete <uid>" or "create <operation id>", for executed writes only (replays add nothing).
    public List<string> Log { get; } = new();
    // One-shot replies by operation id; the step is not executed.
    public Dictionary<string, string> Replies { get; } = new(StringComparer.Ordinal);
    // One-shot: the write runs and its receipt is stored, but the reply is lost in a malformed line.
    public HashSet<string> GarbleAfterExecute { get; } = new(StringComparer.Ordinal);

    public string? Send(string envelopeJson)
    {
        var envelope = RevitCommandProtocol.Parse(envelopeJson);
        Sent.Add(envelope);
        if (Replies.Remove(envelope.OperationId, out var reply)) return reply;
        string json;
        if (receipts.TryGetValue(envelope.OperationId, out var stored))
        {
            if (stored.Sha != envelope.RequestSha256) return RevitCommandResponse.Failure(RevitCommandCodes.OperationIdConflict, "operation_id", "another request").ToJson();
            var replay = RevitCommandResponse.Parse(stored.Json);
            json = (replay with { Receipt = replay.Receipt! with { Replayed = true } }).ToJson();
        }
        else
        {
            var receipt = new RevitCommandReceipt(envelope.OperationId, envelope.Command, envelope.Document.UniqueId, envelope.RequestSha256, false);
            RevitCommandResponse response;
            if (envelope.Args is ElementDeleteArgs delete)
            {
                var missing = delete.ElementUids.FirstOrDefault(uid => !Existing.Contains(uid));
                if (missing is not null) return RevitCommandResponse.Failure(RevitCommandCodes.ReferenceNotFound, "args.element_uids", missing).ToJson();
                var wouldDelete = delete.ElementUids.SelectMany(uid => DependentsOf.TryGetValue(uid, out var list) ? list : Array.Empty<string>())
                    .Where(Existing.Contains).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(uid => uid, StringComparer.OrdinalIgnoreCase).ToArray();
                var confirmed = delete.ConfirmDependents.OrderBy(uid => uid, StringComparer.OrdinalIgnoreCase).ToArray();
                if (!wouldDelete.SequenceEqual(confirmed, StringComparer.OrdinalIgnoreCase))
                    return RevitCommandResponse.Failure("dependents_unconfirmed", "args.confirm_dependents", string.Join(",", wouldDelete)).ToJson();
                var deleted = delete.ElementUids.Concat(wouldDelete).ToArray();
                foreach (var uid in deleted) { Existing.Remove(uid); Log.Add("delete " + uid); }
                response = RevitCommandResponse.Success(receipt, Array.Empty<string>(), Array.Empty<string>(), deleted, null, Array.Empty<string>());
            }
            else
            {
                using var document = JsonDocument.Parse(envelope.CanonicalJson);
                foreach (var property in document.RootElement.GetProperty("args").EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.String && RevitCommandProtocol.IsUniqueId(property.Value.GetString()) && !Existing.Contains(property.Value.GetString()!))
                        return RevitCommandResponse.Failure(RevitCommandCodes.ReferenceNotFound, "args." + property.Name, property.Value.GetString()).ToJson();
                var created = Episode + (next++).ToString("x8", CultureInfo.InvariantCulture);
                Existing.Add(created);
                Log.Add("create " + envelope.OperationId);
                response = RevitCommandResponse.Success(receipt, new[] { created }, Array.Empty<string>(), Array.Empty<string>(), null, Array.Empty<string>());
            }
            json = response.ToJson();
            receipts[envelope.OperationId] = (envelope.RequestSha256, json);
        }
        return GarbleAfterExecute.Remove(envelope.OperationId) ? "{\"status\":" : json;
    }
}

static class ReplaceTests
{
    static readonly Provenance provenance = new("doc-replace-tests", "unit");
    static readonly RevitDocumentRef document = new(@"C:\Repo\.bim\native-runs\p4-05\copy.rvt", "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0d");
    static Guid Id(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes("doc-replace:" + name)).Take(16).ToArray());
    static readonly Guid projectId = Id("project");
    static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
    static Guid RoomOf(int room) => Id("room" + N(room));
    static Guid Role(int room, string role) => RoomCompiler.Id(RoomOf(room), role);
    static Guid Door(int room) => Id("door" + N(room));
    static readonly string[] wallRoles = { "south", "east", "north", "west" };
    static readonly Regex deleteId = new(@"\Adocdel\.[0-9a-f]{32}\z", RegexOptions.CultureInvariant);
    static readonly Regex createId = new(@"\Adocnew\.[0-9a-f]{32}\z", RegexOptions.CultureInvariant);

    static string? Code(Action action)
    {
        try { action(); return null; }
        catch (RevitCommandException error) { return error.Code; }
        catch (ArgumentNullException) { return "null"; }
        catch (ArgumentException error) { return error.Message; }
        catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
    }

    static string Fake(string source)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        return hex[..8] + "-" + hex[8..12] + "-" + hex[12..16] + "-" + hex[16..20] + "-" + hex[20..32] + "-" + hex[32..40];
    }

    static Project Build(int rooms)
    {
        var elements = new List<Element>();
        for (var room = 0; room < rooms; room++)
        {
            elements.AddRange(RoomCompiler.Compile(projectId, 0, new RoomIntent(RoomOf(room), 4, 3, 2.8, 0.2, room * 10, 0, "m", provenance)).Elements);
            var x = room * 10 + 0.1;
            elements.Add(new(Door(room), "door", new[] { x + 1, 0.1, x + 1.9, 0.1, 0, 2.1 }, new[] { Role(room, "south"), Role(room, "level") }, provenance));
        }
        return new(projectId, 0, elements.ToArray());
    }

    static RevitBindings Bindings(int rooms) => new(document, Enumerable.Range(0, rooms).ToDictionary(room => Role(room, "level"), room => "Level " + N(room)),
        Array.Empty<RevitWallTypeBinding>(), null, null);

    static DocumentationSet Docs(int rooms)
    {
        var views = new List<DocView>();
        var tags = new List<DocTag>();
        var dimensions = new List<DocDimension>();
        var sheets = new List<DocSheet>();
        var placements = new List<DocPlacement>();
        for (var room = 0; room < rooms; room++)
        {
            var r = N(room);
            views.Add(new DocView("plan-" + r, "floor_plan", new[] { Role(room, "level") }, "Room " + r + " plan", 50));
            views.Add(new DocView("section-" + r, "section", new[] { Role(room, "south") }, "Room " + r + " section", null, 3));
            tags.Add(new DocTag("tag-space-" + r, "plan-" + r, Role(room, "space"), 0.5, 0.5, true));
            tags.Add(new DocTag("tag-door-" + r, "plan-" + r, Door(room)));
            dimensions.Add(new DocDimension("dim-width-" + r, "plan-" + r, Role(room, "west"), Role(room, "east"), 0.5));
            sheets.Add(new DocSheet("sheet-" + r, "A10" + r, "Room " + r));
            placements.Add(new DocPlacement("place-plan-" + r, "sheet-" + r, "plan-" + r, 200, 150));
            placements.Add(new DocPlacement("place-section-" + r, "sheet-" + r, "section-" + r, 450, 150));
        }
        views.Add(new DocView("3d-0", "three_d", wallRoles.Select(role => Role(0, role)).ToArray(), "Room 0 3D"));
        var schedules = new List<DocSchedule> { new("schedule-doors", "door", "door", new[] { "Mark", "Width" }, "Mark", "Door Schedule") };
        placements.Add(new DocPlacement("place-doors", "sheet-0", "schedule-doors", 600, 300));
        return new(views, tags, dimensions, schedules, sheets, placements);
    }

    static Project Replace(Project project, Guid id, Func<Element, Element> change) => project with { Elements = project.Elements.Select(element => element.Id == id ? change(element) : element).ToArray() };
    static Element Move(Element wall, double dx, double dy)
    {
        var g = wall.GeometryMetres;
        return wall with { GeometryMetres = new[] { g[0] + dx, g[1] + dy, g[2] + dx, g[3] + dy, g[4], g[5] } };
    }

    // What is realized after a full regeneration of before: every item and every model element.
    static Dictionary<string, string> RealizedOf(Project project, DocumentationSet set)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in set.Views.Select(item => item.Key).Concat(set.Tags.Select(item => item.Key)).Concat(set.Dimensions.Select(item => item.Key))
            .Concat(set.Schedules.Select(item => item.Key)).Concat(set.Sheets.Select(item => item.Key)).Concat(set.Placements.Select(item => item.Key)))
            map[DocRegeneration.ItemSource(key)] = Fake("stale:" + key);
        foreach (var element in project.Elements) map[DocRegeneration.ElementSource(element.Id)] = Fake("element:" + element.Id.ToString("D"));
        return map;
    }

    static string[] ParentsOf(string key, DocumentationSet set) =>
        set.Tags.Where(tag => tag.Key == key).Select(tag => tag.View)
            .Concat(set.Dimensions.Where(dimension => dimension.Key == key).Select(dimension => dimension.View))
            .Concat(set.Placements.Where(placement => placement.Key == key).SelectMany(placement => new[] { placement.Sheet, placement.View })).ToArray();

    static string ArgsOf(string canonicalEnvelope)
    {
        using var parsed = JsonDocument.Parse(canonicalEnvelope);
        return parsed.RootElement.GetProperty("args").GetRawText();
    }

    public static void Run(Checker check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "replace: " + name, detail);
        const int rooms = 2;
        var before = Build(rooms);
        var set = Docs(rooms);
        var bindings = Bindings(rooms);
        // Room 0's south wall moves out and its east wall moves east: its section (and that section's placement), its
        // width dimension and the 3D view of its walls regenerate; the plan the dimension sits in and the sheet stay.
        var after = Replace(Replace(before, Role(0, "south"), wall => Move(wall, 0, -0.5)), Role(0, "east"), wall => Move(wall, 0.3, 0));
        var regeneration = DocRegeneration.Affected(before, after, Compiler.Diff(before, after), set, bindings);
        var regenerated = regeneration.Regenerated.ToHashSet(StringComparer.Ordinal);
        var realized = RealizedOf(before, set);
        var keysText = string.Join(",", regeneration.Regenerated);
        Check(regenerated.Any(key => ParentsOf(key, set).Any(regenerated.Contains)) && regenerated.Any(key => ParentsOf(key, set).Any(parent => !regenerated.Contains(parent)))
            && regeneration.Unsupported.Count == 0 && regeneration.Steps.Count == regenerated.Count,
            "fixture: a regenerated child of a regenerated parent, a regenerated child of a kept parent, nothing unsupported", keysText);

        DocReplacementPlan plan;
        try { plan = DocReplace.Plan(regeneration, realized); }
        catch (Exception error) { Check(false, "plan builds", error.GetType().Name + ": " + error.Message); return; }
        var steps = plan.Commands.Steps;
        int IndexOf(string? operation) => operation is null ? -1 : steps.Select((step, index) => (step, index)).First(pair => pair.step.OperationId == operation).index;

        // ---- Shape ----
        Check(plan.Replacements.Select(item => item.Key).SequenceEqual(regeneration.Regenerated.OrderBy(key => key, StringComparer.Ordinal)), "one replacement per regenerated key, by key", keysText);
        Check(plan.Replacements.All(item => item.StaleUniqueId == realized[DocRegeneration.ItemSource(item.Key)] && item.DeleteOperation is not null && item.CreateOperation is not null),
            "every regenerated key with a realization is deleted and re-created", JsonSerializer.Serialize(plan.Replacements));
        Check(steps.Count == 2 * regenerated.Count && plan.Commands.Preconditions.Count == 0 && plan.Commands.Unsupported.Count == 0, "the plan is the deletes plus the creates, no level preconditions",
            string.Join(",", steps.Select(step => step.Command)));
        var deletes = steps.Where(step => step.Command == DocReplace.DeleteCommand).ToArray();
        Check(deletes.All(step => deleteId.IsMatch(step.OperationId) && step.Inputs.Count == 0)
            && steps.Where(step => step.Command != DocReplace.DeleteCommand).All(step => createId.IsMatch(step.OperationId)), "operation ids are docdel./docnew. plus 32 hex");
        Check(plan.Replacements.All(item => steps[IndexOf(item.DeleteOperation)].ArgsJson == "{\"element_uids\":[\"" + item.StaleUniqueId + "\"]}"),
            "each delete targets exactly its stale UniqueId, no dependents confirmed by default", string.Join(" ", deletes.Select(step => step.ArgsJson)));
        var lastDelete = steps.Select((step, index) => (step, index)).Where(pair => pair.step.Command == DocReplace.DeleteCommand).Max(pair => pair.index);
        var firstCreate = steps.Select((step, index) => (step, index)).Where(pair => pair.step.Command != DocReplace.DeleteCommand).Min(pair => pair.index);
        Check(lastDelete < firstCreate, "every stale instance is deleted before anything is re-created", string.Join(",", steps.Select(step => step.Command)));
        var byKey = plan.Replacements.ToDictionary(item => item.Key, StringComparer.Ordinal);
        var pairs = regenerated.SelectMany(child => ParentsOf(child, set).Where(regenerated.Contains).Select(parent => (child, parent))).ToArray();
        Check(pairs.Length > 0 && pairs.All(pair => IndexOf(byKey[pair.child].DeleteOperation) < IndexOf(byKey[pair.parent].DeleteOperation)), "children are deleted before their parents",
            string.Join(",", pairs.Select(pair => pair.child + "<" + pair.parent)));
        Check(pairs.All(pair => IndexOf(byKey[pair.parent].CreateOperation) < IndexOf(byKey[pair.child].CreateOperation)), "parents are re-created before their children");
        Check(pairs.All(pair => steps[IndexOf(byKey[pair.child].CreateOperation)] is var step
            && step.Inputs.Any(input => input.FromOperation == byKey[pair.parent].CreateOperation && input.Select == "created[0]") && step.DependsOn.Contains(byKey[pair.parent].CreateOperation!)),
            "a regenerated parent reaches its child as created[0] of the parent's re-creation");
        var stale = plan.Replacements.Select(item => item.StaleUniqueId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check(steps.Where(step => step.Command != DocReplace.DeleteCommand).All(step => !stale.Any(uid => step.ArgsJson.Contains(uid, StringComparison.OrdinalIgnoreCase))),
            "no create step carries a stale UniqueId");

        // ---- Run through PlanRunner ----
        var fake = new ReplaceFakeTransport(realized.Values);
        var report = DocReplace.Run(plan, fake);
        Check(report.Status == RevitPlanRunStatus.Completed && report.Completed.Count == steps.Count, "the plan runs to completion through PlanRunner", report.ToJson());
        Check(fake.Sent.Select(envelope => envelope.OperationId).SequenceEqual(steps.Select(step => step.OperationId)), "each step is sent once, in plan order");
        Check(stale.All(uid => !fake.Existing.Contains(uid)) && fake.Log.Take(stale.Count).All(entry => entry.StartsWith("delete ", StringComparison.Ordinal)),
            "every stale instance is gone, deleted first", string.Join(",", fake.Log));
        IReadOnlyDictionary<string, string>? next = null;
        var realizedError = Code(() => next = DocReplace.Realized(plan, realized, report));
        Check(realizedError is null && next is not null && next.Count == realized.Count
            && regenerated.All(key => next[DocRegeneration.ItemSource(key)] == report.Completed.First(step => step.OperationId == byKey[key].CreateOperation).Created[0])
            && realized.Where(pair => !regenerated.Contains(pair.Key["doc:".Length..]) || !pair.Key.StartsWith("doc:", StringComparison.Ordinal)).All(pair => next[pair.Key] == pair.Value),
            "Realized maps each regenerated key to its new UniqueId and keeps everything else", realizedError ?? "");
        // The args sent for a re-creation are exactly DocRegeneration.Envelope's args with the new realization.
        if (next is not null)
            Check(regeneration.Steps.All(step => ArgsOf(fake.Sent.First(envelope => envelope.OperationId == byKey[step.Key].CreateOperation).CanonicalJson)
                == ArgsOf(DocRegeneration.Envelope(regeneration, step, next).CanonicalJson)), "each re-creation sends the regeneration's args with kept inputs from the map and new parents");

        // ---- Determinism ----
        var again = DocReplace.Plan(regeneration, new Dictionary<string, string>(realized.Reverse(), StringComparer.Ordinal));
        Check(again.Commands.ToJson() == plan.Commands.ToJson(), "the same inputs give a byte-identical plan");
        var plan0 = DocRegeneration.ItemSource("plan-0");
        var moved = new Dictionary<string, string>(realized, StringComparer.Ordinal) { [plan0] = Fake("other:plan-0") };
        var movedPlan = DocReplace.Plan(regeneration, moved);
        var dimKey = regenerated.FirstOrDefault(key => ParentsOf(key, set).Contains("plan-0"));
        Check(dimKey is not null && DocReplace.Plan(regeneration, moved).Replacements.First(item => item.Key == dimKey).CreateOperation != byKey[dimKey].CreateOperation
            && movedPlan.Commands.Steps.First(step => step.OperationId == movedPlan.Replacements.First(item => item.Key == dimKey).CreateOperation).ArgsJson.Contains(Fake("other:plan-0"), StringComparison.Ordinal),
            "a kept parent realized elsewhere gives another request and another operation id", dimKey ?? "no child of plan-0");

        // ---- Resume after a failed step ----
        var failAt = byKey[pairs[0].child].CreateOperation!;
        var broken = new ReplaceFakeTransport(realized.Values);
        broken.Replies[failAt] = RevitCommandResponse.Failure(RevitCommandCodes.TransactionFailed, null, "fake failure").ToJson();
        var failed = DocReplace.Run(plan, broken);
        Check(failed.Status == RevitPlanRunStatus.Failed && failed.Failed?.OperationId == failAt && failed.Pending.Count > 0 && failed.Pending[0] == failAt
            && failed.Completed.Count == IndexOf(failAt), "a failed re-creation stops the run and leaves it pending", failed.ToJson());
        var sentBefore = broken.Sent.Count;
        var resumed = DocReplace.Resume(plan, RevitPlanRunReport.Parse(failed.ToJson()), broken);
        Check(resumed.Status == RevitPlanRunStatus.Completed && broken.Sent.Skip(sentBefore).Select(envelope => envelope.OperationId).SequenceEqual(steps.Skip(IndexOf(failAt)).Select(step => step.OperationId)),
            "resume sends only the pending steps and completes", resumed.ToJson());
        Check(broken.Log.Count(entry => entry.StartsWith("delete ", StringComparison.Ordinal)) == stale.Count && Code(() => DocReplace.Realized(plan, realized, resumed)) is null,
            "no stale instance is deleted twice and the resumed run yields a realized map");
        Check(Code(() => DocReplace.Realized(plan, realized, failed)) == DocReplaceCodes.ReportIncomplete, "Realized refuses an incomplete run");
        Check(Code(() => DocReplace.Resume(movedPlan, failed, broken)) == "report_plan_mismatch", "a report resumes only its own plan");

        // ---- Resume after a lost reply of a delete that ran ----
        var lostAt = deletes[1].OperationId;
        var lossy = new ReplaceFakeTransport(realized.Values);
        lossy.GarbleAfterExecute.Add(lostAt);
        var lost = DocReplace.Run(plan, lossy);
        var retried = DocReplace.Resume(plan, lost, lossy);
        Check(lost.Status == RevitPlanRunStatus.Failed && lost.Failed?.OperationId == lostAt && retried.Status == RevitPlanRunStatus.Completed
            && retried.Completed.First(step => step.OperationId == lostAt).Replayed, "a delete whose reply was lost is resent and replays from its receipt", retried.ToJson());

        // ---- Dependents ----
        var sectionKey = pairs.First(pair => set.Views.Any(view => view.Key == pair.parent)).parent;
        var extra = Fake("annotation-outside-the-set");
        var withDependent = new ReplaceFakeTransport(realized.Values.Append(extra));
        withDependent.DependentsOf[realized[DocRegeneration.ItemSource(sectionKey)]] = new[] { extra };
        var unconfirmed = DocReplace.Run(plan, withDependent);
        Check(unconfirmed.Status == RevitPlanRunStatus.Failed && unconfirmed.Failed?.OperationId == byKey[sectionKey].DeleteOperation && unconfirmed.Failed?.Code == "dependents_unconfirmed",
            "an unconfirmed dependent stops the delete", unconfirmed.ToJson());
        var confirmedPlan = DocReplace.Plan(regeneration, realized, new Dictionary<string, IReadOnlyList<string>> { [sectionKey] = new[] { extra } });
        var confirmedDelete = confirmedPlan.Commands.Steps.First(step => step.OperationId == confirmedPlan.Replacements.First(item => item.Key == sectionKey).DeleteOperation);
        var confirmedFake = new ReplaceFakeTransport(realized.Values.Append(extra));
        confirmedFake.DependentsOf[realized[DocRegeneration.ItemSource(sectionKey)]] = new[] { extra };
        Check(confirmedDelete.ArgsJson.Contains("\"confirm_dependents\":[\"" + extra + "\"]", StringComparison.Ordinal) && confirmedDelete.OperationId != byKey[sectionKey].DeleteOperation
            && DocReplace.Run(confirmedPlan, confirmedFake).Status == RevitPlanRunStatus.Completed && !confirmedFake.Existing.Contains(extra),
            "confirmed dependents go into confirm_dependents and the delete runs", confirmedDelete.ArgsJson);

        // ---- Refusals ----
        var keptKey = "plan-0";
        var otherStale = realized[DocRegeneration.ItemSource(pairs[0].child)];
        Check(Code(() => DocReplace.Plan(regeneration, realized, new Dictionary<string, IReadOnlyList<string>> { [keptKey] = new[] { extra } })) == DocReplaceCodes.DependentsUnknownKey,
            "dependents for a key that is not regenerated are refused");
        Check(Code(() => DocReplace.Plan(regeneration, realized, new Dictionary<string, IReadOnlyList<string>> { [sectionKey] = new[] { realized[DocRegeneration.ItemSource(keptKey)] } })) == DocReplaceCodes.DependentKept,
            "a dependent that is a kept item is refused");
        Check(Code(() => DocReplace.Plan(regeneration, realized, new Dictionary<string, IReadOnlyList<string>> { [sectionKey] = new[] { realized[DocRegeneration.ElementSource(Role(0, "south"))] } })) == DocReplaceCodes.DependentKept,
            "a dependent that is a model element is refused");
        Check(Code(() => DocReplace.Plan(regeneration, realized, new Dictionary<string, IReadOnlyList<string>> { [sectionKey] = new[] { otherStale } })) == DocReplaceCodes.DependentPlanned,
            "a dependent that is another regenerated key's stale instance is refused");
        Check(Code(() => DocReplace.Plan(regeneration, new Dictionary<string, string>(realized) { [DocRegeneration.ItemSource(sectionKey)] = realized[DocRegeneration.ElementSource(Role(0, "south"))] })) == DocReplaceCodes.StaleShared,
            "a stale UniqueId shared with a kept source is refused");
        Check(Code(() => DocReplace.Plan(regeneration, new Dictionary<string, string>(realized) { [plan0] = "not-a-unique-id" })) == DocReplaceCodes.RealizedInvalid,
            "a malformed realized UniqueId is refused");
        var withoutParent = new Dictionary<string, string>(realized);
        withoutParent.Remove(plan0);
        Check(Code(() => DocReplace.Plan(regeneration, withoutParent)) == DocReplaceCodes.InputUnresolved, "a kept parent that is not realized is refused");
        Check(Code(() => DocReplace.Plan(regeneration with { Regenerated = regeneration.Regenerated.Append("ghost").ToArray() }, realized)) == DocReplaceCodes.RegenerationInvalid,
            "a regenerated key without a step or unsupported entry is refused");

        // ---- A regenerated key never realized: created only ----
        var neverKey = regenerated.First(key => set.Views.Any(view => view.Key == key) && !pairs.Any(pair => pair.parent == key));
        var fresh = new Dictionary<string, string>(realized);
        fresh.Remove(DocRegeneration.ItemSource(neverKey));
        var freshPlan = DocReplace.Plan(regeneration, fresh);
        var freshItem = freshPlan.Replacements.First(item => item.Key == neverKey);
        Check(freshItem.StaleUniqueId is null && freshItem.DeleteOperation is null && freshItem.CreateOperation is not null && freshPlan.Commands.Steps.Count == steps.Count - 1
            && DocReplace.Run(freshPlan, new ReplaceFakeTransport(fresh.Values)).Status == RevitPlanRunStatus.Completed, "a regenerated key without a realization is only created", neverKey);

        // ---- Unsupported after the edit: deleted, not re-created ----
        var withoutDoor = after with { Elements = after.Elements.Where(element => element.Id != Door(0)).ToArray() };
        var doorless = DocRegeneration.Affected(before, withoutDoor, Compiler.Diff(before, withoutDoor), set, bindings);
        var doorPlan = DocReplace.Plan(doorless, realized);
        var doorItem = doorPlan.Replacements.FirstOrDefault(item => item.Key == "tag-door-0");
        var doorFake = new ReplaceFakeTransport(realized.Values);
        var doorReport = DocReplace.Run(doorPlan, doorFake);
        Check(doorless.Unsupported.Any(item => item.Key == "tag-door-0") && doorItem is not null && doorItem.DeleteOperation is not null && doorItem.CreateOperation is null
            && doorPlan.Unsupported.Any(item => item.Key == "tag-door-0") && doorReport.Status == RevitPlanRunStatus.Completed
            && !DocReplace.Realized(doorPlan, realized, doorReport).ContainsKey("doc:tag-door-0"), "a key that became unsupported is deleted and not re-created", doorReport.ToJson());

        // ---- Full regeneration as input: every kind, strict layer order ----
        var full = DocRegeneration.Full(after, set, bindings);
        var fullPlan = DocReplace.Plan(full, realized);
        string KindOf(RevitCommandStep step) => step.ElementKind ?? "";
        var childKinds = new[] { DocRegeneration.Tag, DocRegeneration.Dimension, DocRegeneration.Placement };
        var fullDeletes = fullPlan.Commands.Steps.Where(step => step.Command == DocReplace.DeleteCommand).ToArray();
        var fullCreates = fullPlan.Commands.Steps.Where(step => step.Command != DocReplace.DeleteCommand).ToArray();
        var lastChildDelete = Array.FindLastIndex(fullDeletes, step => childKinds.Contains(KindOf(step)));
        var firstParentDelete = Array.FindIndex(fullDeletes, step => !childKinds.Contains(KindOf(step)));
        var lastParentCreate = Array.FindLastIndex(fullCreates, step => !childKinds.Contains(KindOf(step)));
        var firstChildCreate = Array.FindIndex(fullCreates, step => childKinds.Contains(KindOf(step)));
        Check(fullDeletes.Length == full.TotalItems && fullCreates.Length == full.Steps.Count && lastChildDelete < firstParentDelete && lastParentCreate < firstChildCreate
            && fullCreates.Where(step => childKinds.Contains(KindOf(step))).All(step => step.Inputs.Count > 0),
            "full regeneration: every tag, dimension and placement deleted before any view, schedule or sheet, created after them", string.Join(",", fullPlan.Commands.Steps.Select(KindOf)));
        var fullFake = new ReplaceFakeTransport(realized.Values);
        var fullReport = DocReplace.Run(fullPlan, fullFake);
        Check(fullReport.Status == RevitPlanRunStatus.Completed && realized.Keys.Where(key => key.StartsWith("doc:", StringComparison.Ordinal)).All(key => !fullFake.Existing.Contains(realized[key])),
            "full replacement runs and leaves no stale documentation", fullReport.ToJson());

        // ---- Nothing regenerated ----
        var none = DocRegeneration.Affected(before, before, Compiler.Diff(before, before), set, bindings);
        var nonePlan = DocReplace.Plan(none, realized);
        var noneFake = new ReplaceFakeTransport(realized.Values);
        Check(nonePlan.Commands.Steps.Count == 0 && DocReplace.Run(nonePlan, noneFake).Status == RevitPlanRunStatus.Completed && noneFake.Sent.Count == 0, "an empty regeneration sends nothing");
    }
}
