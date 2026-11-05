using System.Text.Json;
namespace Bim.Core;

// P4-07 documentation update pipeline from a BIM-IR edit. One entry point (Update, or Plan then Run/Resume) takes the
// BIM-IR before and after, the realized map (source -> Revit UniqueId, as DocReplace: "doc:<key>" for a realized
// documentation item, "element:<BIM-IR id D>" for a realized model element) and the dry-run dependents (per regenerated
// key, as DocReplace.Plan), and:
//   1. derives the default documentation of both states (DocDefaults.From before and after);
//   2. regenerates the affected items of the after set (DocRegeneration.Affected on Compiler.Diff(before, after));
//   3. forces the items whose default definition changed or appeared (a level inserted or raised renumbers plan names
//      and sheet numbers without changing any BIM-IR element those items read, so Affected cannot see it) together with
//      the tags, dimensions and placements of a forced view, schedule or sheet, taking their steps from
//      DocRegeneration.Full(after) (byte-identical to Affected's) and merging them with DocRegeneration.Merge;
//   4. retires every realized key the after set no longer has (an unsupported entry with code doc_retired: DocReplace
//      deletes its stale instance and never re-creates it);
//   5. plans the stale replacement with DocReplace.Plan and runs it through PlanRunner (DocReplace.Run / Resume);
//   6. returns the plan plus, after a completed run, the updated realized map (DocReplace.Realized).
// No edit gives an empty plan (nothing is sent); an item neither affected, forced nor retired is never deleted or
// re-created and keeps its map entry. Deterministic: the same input gives the same plan. Refusals are those of
// DocDefaults, DocRegeneration and DocReplace, unchanged. Vendor-free, BCL only; no BIM-IR or persisted format change.
//
// Before/After: the default sets. Forced: the after keys regenerated for a definition change, sorted (ordinal).
// Retired: the realized before-only keys, sorted. Realized: a copy of the realized map the plan was made against.
public sealed record DocUpdatePlan(DocumentationSet Before, DocumentationSet After, DocRegenerationPlan Regeneration, DocReplacementPlan Replacement,
    IReadOnlyList<string> Forced, IReadOnlyList<string> Retired, IReadOnlyDictionary<string, string> Realized);

// Realized: the updated map when Report completed, else null (resume with DocUpdate.Resume).
public sealed record DocUpdateResult(DocUpdatePlan Plan, RevitPlanRunReport Report, IReadOnlyDictionary<string, string>? Realized);

public static class DocUpdateCodes
{
    // DocUnsupported.Code of a retired key (its item is no longer in the default set of after).
    public const string Retired = "doc_retired";
}

public static class DocUpdate
{
    public static DocUpdatePlan Plan(Project before, Project after, RevitBindings bindings, IReadOnlyDictionary<string, string> realized, IReadOnlyDictionary<string, IReadOnlyList<string>>? dependents = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(realized);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in realized.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (pair.Key is null) throw new ArgumentException(DocReplaceCodes.RealizedInvalid);
            map[pair.Key] = pair.Value;
        }

        var setBefore = DocDefaults.From(before);
        var setAfter = DocDefaults.From(after);
        var regeneration = DocRegeneration.Affected(before, after, Compiler.Diff(before, after), setAfter, bindings);

        var itemsBefore = Items(setBefore);
        var itemsAfter = Items(setAfter);
        var forced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, item) in itemsAfter)
            if (!itemsBefore.TryGetValue(key, out var prior) || prior.Kind != item.Kind || prior.Spec != item.Spec) forced.Add(key);
        // A tag, dimension or placement re-runs with its forced view, schedule or sheet (parents have no parents).
        var parents = forced.ToArray();
        foreach (var (key, item) in itemsAfter)
            if (item.Parents.Any(parent => parents.Contains(parent, StringComparer.Ordinal))) forced.Add(key);
        var forcedKeys = forced.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        if (forcedKeys.Length > 0)
        {
            var full = DocRegeneration.Full(after, setAfter, bindings);
            var supplement = full with
            {
                Regenerated = forcedKeys,
                Steps = full.Steps.Where(step => forced.Contains(step.Key)).ToArray(),
                Unsupported = full.Unsupported.Where(entry => forced.Contains(entry.Key)).ToArray(),
            };
            regeneration = DocRegeneration.Merge(supplement, regeneration);
        }

        var retired = itemsBefore.Where(pair => !itemsAfter.ContainsKey(pair.Key) && map.ContainsKey(DocRegeneration.ItemSource(pair.Key)))
            .Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        if (retired.Length > 0)
        {
            var entries = retired.Select(key => new DocUnsupported(key, itemsBefore[key].Kind, DocUpdateCodes.Retired, "item no longer in the default documentation set", Array.Empty<Guid>()));
            regeneration = regeneration with
            {
                Regenerated = regeneration.Regenerated.Concat(retired).OrderBy(key => key, StringComparer.Ordinal).ToArray(),
                Unsupported = regeneration.Unsupported.Concat(entries).OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray(),
            };
        }

        var replacement = DocReplace.Plan(regeneration, map, dependents);
        return new DocUpdatePlan(setBefore, setAfter, regeneration, replacement, forcedKeys, retired, map);
    }

    public static DocUpdateResult Run(DocUpdatePlan plan, IRevitCommandTransport transport)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(transport);
        return Result(plan, DocReplace.Run(plan.Replacement, transport));
    }

    // Sends only the pending steps of report (PlanRunner.Resume refusals unchanged).
    public static DocUpdateResult Resume(DocUpdatePlan plan, RevitPlanRunReport report, IRevitCommandTransport transport)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(transport);
        return Result(plan, DocReplace.Resume(plan.Replacement, report, transport));
    }

    // The entry point: Plan, then Run.
    public static DocUpdateResult Update(Project before, Project after, RevitBindings bindings, IReadOnlyDictionary<string, string> realized, IRevitCommandTransport transport,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? dependents = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return Run(Plan(before, after, bindings, realized, dependents), transport);
    }

    static DocUpdateResult Result(DocUpdatePlan plan, RevitPlanRunReport report)
    {
        if (report.Status != RevitPlanRunStatus.Completed || report.Pending.Count > 0) return new DocUpdateResult(plan, report, null);
        var updated = DocReplace.Realized(plan.Replacement, plan.Realized, report);
        var sorted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in updated.OrderBy(pair => pair.Key, StringComparer.Ordinal)) sorted[pair.Key] = pair.Value;
        return new DocUpdateResult(plan, report, sorted);
    }

    sealed record Item(string Kind, string Spec, string[] Parents);

    // Every item of a set by key, with its definition serialized (records holding lists do not compare by value).
    static Dictionary<string, Item> Items(DocumentationSet set)
    {
        var items = new Dictionary<string, Item>(StringComparer.Ordinal);
        void Add(string key, string kind, object spec, params string[] parents) => items[key] = new Item(kind, JsonSerializer.Serialize(spec, spec.GetType()), parents);
        foreach (var view in set.Views) Add(view.Key, DocRegeneration.View, view);
        foreach (var schedule in set.Schedules) Add(schedule.Key, DocRegeneration.Schedule, schedule);
        foreach (var sheet in set.Sheets) Add(sheet.Key, DocRegeneration.Sheet, sheet);
        foreach (var tag in set.Tags) Add(tag.Key, DocRegeneration.Tag, tag, tag.View);
        foreach (var dimension in set.Dimensions) Add(dimension.Key, DocRegeneration.Dimension, dimension, dimension.View);
        foreach (var placement in set.Placements) Add(placement.Key, DocRegeneration.Placement, placement, placement.Sheet, placement.View);
        return items;
    }
}
