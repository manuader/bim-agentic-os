using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
namespace Bim.Core;

// P3-06 advance work. Reused: cached element byte arrays carried over by reference from the predecessor index
// (untouched elements, never re-serialized). Carried: created/updated elements whose bytes were taken from the
// diff that produced the result (serialized once there, counted in IncrementalWork.Serialized). Advance itself
// serializes no element. TotalElements: elements in the advanced index.
public sealed record IncrementalAdvanceWork(int Reused, int Carried, int TotalElements);

public static partial class IncrementalDiff
{
    // The result was not issued by this index's own Diff (checked by reference): another index's result, a
    // predecessor's or successor's, a `with` copy or a hand-built IncrementalDiffResult.
    public const string StaleResult = "edit_stale_result";
    // result.After was changed after Diff: an element slot replaced, or an edited element's geometry or
    // dependencies mutated in place.
    public const string ResultMutated = "edit_result_mutated";
    // The committed project is not result.After up to Revision: another Id, or elements other than result.After's
    // element instances in result.After's order.
    public const string CommitMismatch = "edit_commit_mismatch";
}

// Advance(result) returns the index of result.After; Advance(result, committed) returns the index of the committed
// project, which ProjectStore.Commit returns as result.After with Revision + 1 (sharing result.After's element array),
// so the next Diff's plan matches the store. Both are equal in every observable to Build of that project (BeforeHash,
// Before, positions, cached bytes, reverse dependency keys and lists in project order, hence every later Diff), with
// no validation and no element re-serialization: Advance(result) takes BeforeHash from result.Plan.AfterHash;
// Advance(result, committed) rebuilds the {Id,Revision} prefix and re-streams the cached bytes through SHA-256.
// Only a result issued by this very index's Diff is accepted, by reference (edit_stale_result otherwise). Diff keeps
// its own copy of result.After's element slots, of each edited element's geometry and dependencies, and of the old
// dependencies of deleted and updated elements; Advance refuses a result changed since (edit_result_mutated) and
// computes reverse dependencies from those copies. Untouched elements are shared with Before and fall under Build's
// snapshot rule (not checked). The predecessor stays valid and unchanged; one result may be advanced more than once.
// Still O(project): positions, the byte-array table and the reverse-dependency dictionary are rebuilt as reference
// copies (and, with committed, the hash is re-streamed); only the lists of keys touched by the edits are recomputed.
public sealed partial class IncrementalDiffIndex
{
    sealed record Edited(double[] Geometry, Guid[] Dependencies, bool Created);
    sealed record Issued(Element[] Slots, string AfterHash, HashSet<Guid> Deletes, Dictionary<Guid, Guid[]> OldDependencies,
        Dictionary<Guid, Edited> Edits, List<Guid> CreateOrder, Dictionary<Guid, byte[]> NewJson);

    readonly ConditionalWeakTable<IncrementalDiffResult, Issued> issued = new();

    public string BeforeHash => beforeHash;
    public IncrementalAdvanceWork? Advanced { get; private init; }
    internal int PositionOf(Guid id) => position.TryGetValue(id, out var index) ? index : -1;
    internal IReadOnlyList<Guid> DependentsOf(Guid id) => dependents.TryGetValue(id, out var list) ? list.AsReadOnly() : Array.Empty<Guid>();
    internal IEnumerable<Guid> DependentKeys => dependents.Keys;
    internal byte[]? CachedBytes(Guid id) => position.TryGetValue(id, out var index) ? json[index] : null;

    // Copies of each edited element's geometry and dependencies, taken by Diff before any createOrder comparer runs.
    static Dictionary<Guid, Edited> Snapshot(Dictionary<Guid, Element> updates, List<Element> creates)
    {
        var edits = new Dictionary<Guid, Edited>(updates.Count + creates.Count);
        foreach (var (id, element) in updates) edits.Add(id, new(element.GeometryMetres.ToArray(), element.Dependencies.ToArray(), false));
        foreach (var element in creates) edits.Add(element.Id, new(element.GeometryMetres.ToArray(), element.Dependencies.ToArray(), true));
        return edits;
    }

    IncrementalDiffResult Remember(IncrementalDiffResult result, HashSet<Guid> deletes, Dictionary<Guid, Element> updates, List<Element> creates, Dictionary<Guid, byte[]> newJson, Dictionary<Guid, Edited> edits)
    {
        var old = new Dictionary<Guid, Guid[]>(deletes.Count + updates.Count);
        foreach (var id in deletes.Concat(updates.Keys)) old.Add(id, Before.Elements[position[id]].Dependencies.ToArray());
        issued.Add(result, new(result.After.Elements.ToArray(), result.Plan.AfterHash, new(deletes), old, edits, creates.Select(element => element.Id).ToList(), newJson));
        return result;
    }

    public IncrementalDiffIndex Advance(IncrementalDiffResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var state = Accept(result);
        return Next(result.After, state, prefix, state.AfterHash);
    }

    public IncrementalDiffIndex Advance(IncrementalDiffResult result, Project committed)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(committed);
        var state = Accept(result);
        if (committed.Id != result.After.Id || !SameSlots(committed.Elements, state.Slots)) throw new ArgumentException(IncrementalDiff.CommitMismatch);
        // Same layout as Build: {"Id":..,"Revision":..,"Elements":[ then the element bytes.
        var empty = JsonSerializer.Serialize(committed with { Elements = Array.Empty<Element>() });
        if (!empty.EndsWith("[]}", StringComparison.Ordinal)) throw new InvalidOperationException("hash_layout");
        return Next(committed, state, Encoding.UTF8.GetBytes(empty[..^2]), null);
    }

    static bool SameSlots(Element[]? elements, Element[] slots)
    {
        if (elements is null || elements.Length != slots.Length) return false;
        for (var i = 0; i < slots.Length; i++) if (!ReferenceEquals(elements[i], slots[i])) return false;
        return true;
    }

    static bool SameBits(double[]? a, double[] b)
    {
        if (a is null || a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (BitConverter.DoubleToInt64Bits(a[i]) != BitConverter.DoubleToInt64Bits(b[i])) return false;
        return true;
    }

    Issued Accept(IncrementalDiffResult result)
    {
        if (!issued.TryGetValue(result, out var state)) throw new ArgumentException(IncrementalDiff.StaleResult);
        if (!SameSlots(result.After.Elements, state.Slots)) throw new ArgumentException(IncrementalDiff.ResultMutated);
        foreach (var element in state.Slots)
            if (state.Edits.TryGetValue(element.Id, out var edited)
                && (!SameBits(element.GeometryMetres, edited.Geometry) || element.Dependencies is null || !element.Dependencies.AsSpan().SequenceEqual(edited.Dependencies)))
                throw new ArgumentException(IncrementalDiff.ResultMutated);
        return state;
    }

    // afterHash null: re-stream the cached bytes under nextPrefix (no element is serialized).
    IncrementalDiffIndex Next(Project project, Issued state, byte[] nextPrefix, string? afterHash)
    {
        var after = state.Slots;
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
        var keys = new HashSet<Guid>(state.Deletes);
        foreach (var dependencies in state.OldDependencies.Values) keys.UnionWith(dependencies);
        foreach (var edited in state.Edits.Values) keys.UnionWith(edited.Dependencies);
        var updated = state.Edits.Where(pair => !pair.Value.Created).ToArray();
        foreach (var key in keys)
        {
            // Build order: dependents in project order, one entry per occurrence. Survivors keep their relative
            // order and updated elements their slot; creates sit wherever Diff placed them (last in edit order by
            // default, merged by a P3-10 createOrder otherwise), so every entry is ordered by its next position.
            var survivors = dependents.TryGetValue(key, out var old)
                ? old.Where(dependent => !state.Deletes.Contains(dependent) && !state.Edits.ContainsKey(dependent)) : Enumerable.Empty<Guid>();
            var moved = updated.SelectMany(pair => pair.Value.Dependencies.Where(dependency => dependency == key).Select(_ => pair.Key));
            var fresh = state.CreateOrder.SelectMany(id => state.Edits[id].Dependencies.Where(dependency => dependency == key).Select(_ => id));
            var list = survivors.Concat(moved).Concat(fresh).OrderBy(dependent => nextPosition[dependent]).ToList(); // stable: equal positions keep order
            if (list.Count == 0) nextDependents.Remove(key); else nextDependents[key] = list;
        }

        var hash = afterHash ?? Hash(nextPrefix, nextJson, out _);
        return new(project, nextPosition, nextJson, nextDependents, nextPrefix, hash) { Advanced = new(reused, carried, after.Length) };
    }
}
