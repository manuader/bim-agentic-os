using Bim.Core;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// P4-07 oracle: DocUpdate takes BIM-IR before/after, the realized map (source -> Revit UniqueId) and dry-run dependents,
// derives the default documentation of both states (DocDefaults), regenerates only the affected items
// (DocRegeneration.Affected, plus the items whose default definition itself changed or appeared, and their children),
// retires the realized items whose key disappeared, plans the stale replacement (DocReplace), runs it through PlanRunner
// and returns the plan plus the updated realized map. A second run with no edit plans and sends nothing; an unrelated
// edit leaves untouched documentation out (no delete, no create, map entry kept); the same input gives the same plan.
static class UpdateTests
{
    static readonly Provenance provenance = new("doc-update-tests", "unit");
    static readonly RevitDocumentRef document = new(@"C:\Repo\.bim\native-runs\p4-07\copy.rvt", "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0e");
    static Guid Id(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes("doc-update:" + name)).Take(16).ToArray());
    static readonly Guid projectId = Id("project");
    static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
    static Guid RoomOf(int room) => Id("room" + N(room));
    static Guid Role(int room, string role) => RoomCompiler.Id(RoomOf(room), role);
    static Guid Door(int room) => Id("door" + N(room));
    static string K(Guid id) => id.ToString("N");
    static string Json(object value) => JsonSerializer.Serialize(value);

    static string? Code(Action action)
    {
        try { action(); return null; }
        catch (ArgumentNullException) { return "null"; }
        catch (ArgumentException error) { return error.Message; }
        catch (InvalidOperationException error) { return "state:" + error.Message; }
        catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
    }

    static string Fake(string source)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        return hex[..8] + "-" + hex[8..12] + "-" + hex[12..16] + "-" + hex[16..20] + "-" + hex[20..32] + "-" + hex[32..40];
    }

    // One RoomCompiler room per elevation (own level), an absolute door on its south wall; rooms 10 m apart.
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
    static Project Replace(Project project, Guid id, Func<Element, Element> change) => project with { Elements = project.Elements.Select(element => element.Id == id ? change(element) : element).ToArray() };
    static Project Remove(Project project, Guid id) => project with { Elements = project.Elements.Where(element => element.Id != id).ToArray() };
    static IEnumerable<string> Keys(DocumentationSet set) =>
        set.Views.Select(item => item.Key).Concat(set.Tags.Select(item => item.Key)).Concat(set.Dimensions.Select(item => item.Key))
            .Concat(set.Schedules.Select(item => item.Key)).Concat(set.Sheets.Select(item => item.Key)).Concat(set.Placements.Select(item => item.Key));

    // What is realized after a full documentation of project: every default item and every model element.
    static Dictionary<string, string> RealizedOf(Project project)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in Keys(DocDefaults.From(project))) map[DocRegeneration.ItemSource(key)] = Fake("stale:" + key);
        foreach (var element in project.Elements) map[DocRegeneration.ElementSource(element.Id)] = Fake("element:" + element.Id.ToString("D"));
        return map;
    }
    static string Sorted(IReadOnlyDictionary<string, string>? map) => map is null ? "null"
        : string.Join("\n", map.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));

    public static void Run(Checker check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "update: " + name, detail);
        var before = Build(0, 3);
        var realized = RealizedOf(before);
        var setBefore = DocDefaults.From(before);
        var tagDoor1 = "tag-door-" + K(Door(1));

        // ---- Unrelated edit: room 1's door slides 0.2 m along its wall ----
        var moved = Replace(before, Door(1), door => door with { GeometryMetres = new[] { door.GeometryMetres[0] + 0.2, door.GeometryMetres[1], door.GeometryMetres[2] + 0.2, door.GeometryMetres[3], door.GeometryMetres[4], door.GeometryMetres[5] } });
        var fake = new ReplaceFakeTransport(realized.Values);
        var result = DocUpdate.Update(before, moved, Bindings(moved), realized, fake);
        var keys = result.Plan.Replacement.Replacements.Select(item => item.Key).ToArray();
        var expected = DocRegeneration.Affected(before, moved, Compiler.Diff(before, moved), DocDefaults.From(moved), Bindings(moved)).Regenerated;
        Check(keys.SequenceEqual(expected, StringComparer.Ordinal) && keys.Contains(tagDoor1) && result.Plan.Forced.Count == 0 && result.Plan.Retired.Count == 0,
            "a door move regenerates exactly what DocDefaults -> Affected names (its tag), nothing forced or retired", string.Join(",", keys));
        Check(result.Report.Status == RevitPlanRunStatus.Completed && result.Report.Pending.Count == 0 && result.Realized is not null
            && fake.Sent.Count == result.Plan.Replacement.Commands.Steps.Count && fake.Sent.Count > 0,
            "the plan runs through PlanRunner with the fake transport and completes", result.Report.Status);
        var untouched = Keys(setBefore).Where(key => !keys.Contains(key, StringComparer.Ordinal)).ToArray();
        Check(untouched.Length > 0 && untouched.Contains("plan-" + K(Role(1, "level"))) && untouched.Contains("tag-room-" + K(Role(1, "space")))
            && untouched.Contains("tag-door-" + K(Door(0))) && untouched.Contains("plan-" + K(Role(0, "level"))),
            "fixture: the plan view and room tag beside the moved door, and room 0, are untouched", string.Join(",", untouched));
        Check(untouched.All(key => !fake.Log.Contains("delete " + realized[DocRegeneration.ItemSource(key)]))
            && untouched.All(key => result.Realized is not null && result.Realized[DocRegeneration.ItemSource(key)] == realized[DocRegeneration.ItemSource(key)])
            && fake.Log.Count(entry => entry.StartsWith("create ", StringComparison.Ordinal)) == keys.Length,
            "untouched documentation is neither deleted nor re-created and keeps its realized UniqueId", string.Join(",", fake.Log));
        var newTag = result.Realized?.GetValueOrDefault(DocRegeneration.ItemSource(tagDoor1));
        Check(newTag is not null && newTag != realized[DocRegeneration.ItemSource(tagDoor1)] && fake.Existing.Contains(newTag)
            && !fake.Existing.Contains(realized[DocRegeneration.ItemSource(tagDoor1)])
            && result.Realized!.Count == realized.Count && realized.Keys.All(result.Realized!.ContainsKey),
            "the updated map holds the re-created tag's UniqueId in place of the stale one, every other entry kept", newTag ?? "missing");

        // ---- Second run with no edit ----
        var quiet = new ReplaceFakeTransport(result.Realized!.Values);
        var second = DocUpdate.Update(moved, moved, Bindings(moved), result.Realized!, quiet);
        Check(second.Plan.Replacement.Commands.Steps.Count == 0 && second.Plan.Replacement.Replacements.Count == 0 && second.Plan.Regeneration.Regenerated.Count == 0
            && quiet.Sent.Count == 0 && second.Report.Status == RevitPlanRunStatus.Completed && Sorted(second.Realized) == Sorted(result.Realized),
            "a second run with no edit yields an empty plan, sends nothing and returns the same map", Json(second.Plan.Regeneration.Regenerated));

        // ---- Determinism ----
        var again = DocUpdate.Update(before, moved, Bindings(moved), new Dictionary<string, string>(realized, StringComparer.Ordinal), new ReplaceFakeTransport(realized.Values));
        Check(Json(again.Plan.Replacement) == Json(result.Plan.Replacement) && Json(again.Plan.Regeneration) == Json(result.Plan.Regeneration)
            && Sorted(again.Realized) == Sorted(result.Realized) && Json(DocUpdate.Plan(before, moved, Bindings(moved), realized).Replacement) == Json(result.Plan.Replacement),
            "the same input gives a byte-identical plan and the same updated map");

        // ---- A level inserted below: every existing level is renumbered (names and sheet numbers change, keys stay) ----
        var inserted = Build(0, 3, -3);
        var plan0 = "plan-" + K(Role(0, "level"));
        var plan1 = "plan-" + K(Role(1, "level"));
        var plan2 = "plan-" + K(Role(2, "level"));
        var place0 = "place-plan-" + K(Role(0, "level"));
        var tagRoom0 = "tag-room-" + K(Role(0, "space"));
        var tagDoor0 = "tag-door-" + K(Door(0));
        var affectedOnly = DocRegeneration.Affected(before, inserted, Compiler.Diff(before, inserted), DocDefaults.From(inserted), Bindings(inserted)).Regenerated;
        Check(!affectedOnly.Contains(plan0), "fixture: Affected alone does not see a renamed default view (its BIM-IR reads are unchanged)", string.Join(",", affectedOnly));
        // A dry run of deleting room 0's plan reports its tags and viewport (deleted earlier as regenerated children) and
        // one annotation the documentation set does not track.
        var annotation = Fake("annotation:plan0");
        var dependents = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [plan0] = new[] { realized[DocRegeneration.ItemSource(tagRoom0)], realized[DocRegeneration.ItemSource(tagDoor0)], realized[DocRegeneration.ItemSource(place0)], annotation },
        };
        var renumberFake = new ReplaceFakeTransport(realized.Values.Append(annotation));
        renumberFake.DependentsOf[realized[DocRegeneration.ItemSource(plan0)]] = dependents[plan0].ToArray();
        var renumbered = DocUpdate.Update(before, inserted, Bindings(inserted), realized, renumberFake, dependents);
        var byKey = renumbered.Plan.Replacement.Replacements.ToDictionary(item => item.Key, StringComparer.Ordinal);
        Check(renumbered.Report.Status == RevitPlanRunStatus.Completed && renumbered.Realized is not null,
            "a level insertion runs to completion with the dry-run dependents confirmed", renumbered.Report.Failed is null ? renumbered.Report.Status : renumbered.Report.Failed.Code + " " + renumbered.Report.Failed.Detail);
        Check(new[] { plan0, plan1, "sheet-" + K(Role(0, "level")), "sheet-" + K(Role(1, "level")) }.All(key => byKey.TryGetValue(key, out var item) && item.DeleteOperation is not null && item.CreateOperation is not null)
            && renumbered.Plan.Forced.Contains(plan0) && renumbered.Plan.Forced.Contains(plan1),
            "renamed views and renumbered sheets are forced: their stale instances are deleted and re-created", string.Join(",", byKey.Keys));
        Check(new[] { place0, tagRoom0, tagDoor0 }.All(key => byKey.TryGetValue(key, out var item) && item.DeleteOperation is not null && item.CreateOperation is not null),
            "children of a forced view are regenerated with it", string.Join(",", byKey.Keys));
        Check(byKey.TryGetValue(plan2, out var fresh) && fresh.StaleUniqueId is null && fresh.DeleteOperation is null && fresh.CreateOperation is not null
            && renumbered.Realized is not null && renumbered.Realized.ContainsKey(DocRegeneration.ItemSource(plan2)) && renumbered.Plan.Forced.Contains(plan2),
            "the new level's plan is created (nothing to delete) and recorded in the updated map");
        Check(!renumberFake.Existing.Contains(annotation) && !byKey.ContainsKey(DocDefaults.SchedulesSheet)
            && renumbered.Realized?[DocRegeneration.ItemSource(DocDefaults.SchedulesSheet)] == realized[DocRegeneration.ItemSource(DocDefaults.SchedulesSheet)],
            "the confirmed dependent goes with its view; the unchanged schedules sheet is kept");

        // ---- An element removed: its tag key disappears and the realized tag is retired ----
        var removed = Remove(before, Door(1));
        var removeFake = new ReplaceFakeTransport(realized.Values);
        var retired = DocUpdate.Update(before, removed, Bindings(removed), realized, removeFake);
        var retiredItem = retired.Plan.Replacement.Replacements.FirstOrDefault(item => item.Key == tagDoor1);
        Check(retired.Plan.Retired.SequenceEqual(new[] { tagDoor1 }) && retiredItem is { DeleteOperation: not null, CreateOperation: null }
            && retired.Plan.Replacement.Unsupported.Any(item => item.Key == tagDoor1 && item.Code == DocUpdateCodes.Retired)
            && removeFake.Log.Contains("delete " + realized[DocRegeneration.ItemSource(tagDoor1)])
            && retired.Realized is not null && !retired.Realized.ContainsKey(DocRegeneration.ItemSource(tagDoor1)),
            "a removed door's realized tag is deleted, not re-created, and leaves the map", Json(retired.Plan.Retired));

        // ---- Resume after a lost reply ----
        var planned = DocUpdate.Plan(before, moved, Bindings(moved), realized);
        var lossy = new ReplaceFakeTransport(realized.Values);
        lossy.GarbleAfterExecute.Add(planned.Replacement.Commands.Steps[0].OperationId);
        var failedRun = DocUpdate.Run(planned, lossy);
        var resumed = DocUpdate.Resume(planned, failedRun.Report, lossy);
        Check(failedRun.Report.Status == RevitPlanRunStatus.Failed && failedRun.Realized is null
            && resumed.Report.Status == RevitPlanRunStatus.Completed && Sorted(resumed.Realized) == Sorted(result.Realized),
            "a failed run returns no map; resuming completes it with the same map as an uninterrupted run", resumed.Report.Status);

        // ---- Refusals ----
        Check(Code(() => DocUpdate.Plan(null!, moved, Bindings(moved), realized)) == "null" && Code(() => DocUpdate.Plan(before, moved, Bindings(moved), null!)) == "null"
            && Code(() => DocUpdate.Run(null!, fake)) == "null", "null inputs are refused");
        var bad = new Dictionary<string, string>(realized, StringComparer.Ordinal) { ["view:x"] = Fake("x") };
        Check(Code(() => DocUpdate.Plan(before, moved, Bindings(moved), bad)) == DocReplaceCodes.RealizedInvalid, "a malformed realized map is refused with DocReplace's code");
        var unknown = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [plan0] = new[] { annotation } };
        Check(Code(() => DocUpdate.Plan(before, moved, Bindings(moved), realized, unknown)) == DocReplaceCodes.DependentsUnknownKey, "dependents for a key the update does not regenerate are refused");
    }
}
