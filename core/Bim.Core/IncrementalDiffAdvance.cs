using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("LocalValidation.Tests")]
namespace Bim.Core;

// P3-06 advance work. Reused: cached element byte arrays carried over by reference from the predecessor index
// (untouched elements, never re-serialized). Carried: created/updated elements whose bytes were taken from the
// diff that produced the result (serialized once there, counted in IncrementalWork.Serialized). Advance itself
// serializes nothing. TotalElements: elements in the advanced index (= result.After).
public sealed record IncrementalAdvanceWork(int Reused, int Carried, int TotalElements);

public static partial class IncrementalDiff
{
    public const string StaleResult = "edit_stale_result";
}

// Advance(result) returns the index of result.After, equal in every observable to Build(result.After) (BeforeHash,
// Before, positions, cached bytes, reverse dependency keys and lists in project order, hence every later Diff), without
// re-validating, re-serializing untouched elements or re-hashing (BeforeHash is result.Plan.AfterHash). Only a result
// issued by this very index's Diff is accepted (by reference); any other, including a copy made with `with`, a
// fabricated result or one issued by a predecessor/successor index, is refused with edit_stale_result
// (ArgumentException). The predecessor stays valid and unchanged; one result may be advanced more than once.
// Still O(project): the positions dictionary, byte-array table and reverse-dependency dictionary are rebuilt as
// reference copies; only the reverse-dependency lists of keys touched by the edits are recomputed.
// The caller must not mutate result.After (like Build's snapshot rule).
public sealed partial class IncrementalDiffIndex
{
    sealed record Issued(Element[] After, string AfterHash, HashSet<Guid> Deletes, Dictionary<Guid, Element> Updates, List<Element> Creates, Dictionary<Guid, byte[]> NewJson);

    readonly ConditionalWeakTable<IncrementalDiffResult, Issued> issued = new();

    public string BeforeHash => beforeHash;
    public IncrementalAdvanceWork? Advanced { get; private init; }
    public int PositionOf(Guid id) => position.TryGetValue(id, out var index) ? index : -1;
    public IReadOnlyList<Guid> DependentsOf(Guid id) => dependents.TryGetValue(id, out var list) ? list.AsReadOnly() : Array.Empty<Guid>();
    internal IEnumerable<Guid> DependentKeys => dependents.Keys;
    internal byte[]? CachedBytes(Guid id) => position.TryGetValue(id, out var index) ? json[index] : null;

    IncrementalDiffResult Remember(IncrementalDiffResult result, HashSet<Guid> deletes, Dictionary<Guid, Element> updates, List<Element> creates, Dictionary<Guid, byte[]> newJson)
    {
        issued.Add(result, new(result.After.Elements, result.Plan.AfterHash, deletes, updates, creates, newJson));
        return result;
    }

    public IncrementalDiffIndex Advance(IncrementalDiffResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!issued.TryGetValue(result, out var state)) throw new ArgumentException(IncrementalDiff.StaleResult);
        var after = state.After;
        var nextPosition = new Dictionary<Guid, int>(after.Length);
        var nextJson = new byte[after.Length][];
        int reused = 0, carried = 0;
        for (var i = 0; i < after.Length; i++)
        {
            var id = after[i].Id;
            nextPosition.Add(id, i);
            if (state.NewJson.TryGetValue(id, out var bytes)) { nextJson[i] = bytes; carried++; }
            else { nextJson[i] = json[position[id]]; reused++; }
        }

        // Lists are never mutated once built, so untouched keys share their list with the predecessor.
        var nextDependents = new Dictionary<Guid, List<Guid>>(dependents);
        var keys = new HashSet<Guid>();
        foreach (var id in state.Deletes) { keys.Add(id); keys.UnionWith(Before.Elements[position[id]].Dependencies); }
        foreach (var (id, element) in state.Updates) { keys.UnionWith(Before.Elements[position[id]].Dependencies); keys.UnionWith(element.Dependencies); }
        foreach (var element in state.Creates) keys.UnionWith(element.Dependencies);
        foreach (var key in keys)
        {
            // Build order: dependents in project order, one entry per occurrence. Survivors keep their relative
            // order, updated elements stay in place, creates come last in edit order.
            var survivors = dependents.TryGetValue(key, out var old)
                ? old.Where(dependent => !state.Deletes.Contains(dependent) && !state.Updates.ContainsKey(dependent)) : Enumerable.Empty<Guid>();
            var updated = state.Updates.Values.Where(element => element.Dependencies.Contains(key))
                .SelectMany(element => element.Dependencies.Where(dependency => dependency == key).Select(_ => element.Id));
            var list = survivors.Concat(updated).OrderBy(dependent => position[dependent]).ToList(); // stable: equal positions keep order
            foreach (var element in state.Creates)
                foreach (var dependency in element.Dependencies) if (dependency == key) list.Add(element.Id);
            if (list.Count == 0) nextDependents.Remove(key); else nextDependents[key] = list;
        }

        return new(result.After, nextPosition, nextJson, nextDependents, prefix, state.AfterHash) { Advanced = new(reused, carried, after.Length) };
    }
}
