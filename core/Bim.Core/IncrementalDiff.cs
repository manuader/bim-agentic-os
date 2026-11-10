using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Bim.Core;

// P3-05 incremental diff. An edit names one element by id: create/update carry the full new element (whose Id
// must equal the edit Id), delete carries none. Applying an edit set means: updates replace the element in place,
// deletes remove it, creates are appended in edit-set order (or merged by an optional P3-10 createOrder). The result equals
// Compiler.Diff(before, Apply(before, edits)) byte for byte (operations, both hashes and DirtyIds), or the same
// invalid_ir refusal, without calling Compiler.Diff or Compiler.Validate on the whole project.
public sealed record ElementEdit(string Action, Guid Id, Element? Element)
{
    public static ElementEdit Create(Element element) { ArgumentNullException.ThrowIfNull(element); return new("create", element.Id, element); }
    public static ElementEdit Update(Element element) { ArgumentNullException.ThrowIfNull(element); return new("update", element.Id, element); }
    public static ElementEdit Delete(Guid id) => new("delete", id, null);
}

// Work counters for one incremental diff. Serialized: elements JSON-serialized (only created/updated edits).
// Visited: size of the dirty closure (= plan.DirtyIds.Length; a size, not an independent work measure).
// Traversed: dependency entries actually examined: before-dependents of each deleted element (dangling check) and
// of each dirty element (closure), plus the after-dependencies of each dirty element still present (cycle check).
// It is independent of project size for a fixed edit and neighbourhood. HashedBytes: bytes fed to SHA-256 for
// AfterHash. TotalElements: elements in before.
// NOT local, O(project) per diff: assembling After (a reference copy of the element array) and hashing AfterHash
// (streams every element's cached JSON bytes; nothing unedited is re-serialized). Plan hashes are whole-project.
public sealed record IncrementalWork(int Edits, int Serialized, int Visited, int TotalElements, long HashedBytes, long Traversed);

public sealed record IncrementalDiffResult(Project After, ExecutionPlan Plan, IncrementalWork Work);

// Read-only index over a Compiler.Validate-clean project, built once in O(project): element positions, cached
// per-element JSON bytes, reverse dependency lists and BeforeHash. Reusable for any number of diffs; never mutated.
// The index is a snapshot of before: do not mutate before (its Elements array or elements) after Build. A diff
// does not advance the index or persist anything; callers commit result.After (with result.Plan) themselves and
// get the next index from Advance(result, committed) / Advance(result) (IncrementalDiffAdvance.cs) or Build. maxAffected must be >= 0 (edit_invalid otherwise).
public sealed partial class IncrementalDiffIndex
{
    readonly Dictionary<Guid, int> position;
    readonly byte[][] json;
    readonly Dictionary<Guid, List<Guid>> dependents;
    readonly byte[] prefix;
    readonly string beforeHash;
    static readonly byte[] Comma = { (byte)',' };
    static readonly byte[] Suffix = { (byte)']', (byte)'}' };

    public Project Before { get; }

    IncrementalDiffIndex(Project before, Dictionary<Guid, int> position, byte[][] json, Dictionary<Guid, List<Guid>> dependents, byte[] prefix, string beforeHash)
    {
        Before = before; this.position = position; this.json = json; this.dependents = dependents; this.prefix = prefix; this.beforeHash = beforeHash;
    }

    public static IncrementalDiffIndex Build(Project before)
    {
        ArgumentNullException.ThrowIfNull(before);
        if (Compiler.Validate(before).Length > 0) throw new InvalidOperationException("invalid_ir");
        var elements = before.Elements;
        var position = new Dictionary<Guid, int>(elements.Length);
        var json = new byte[elements.Length][];
        var dependents = new Dictionary<Guid, List<Guid>>();
        for (var i = 0; i < elements.Length; i++)
        {
            var element = elements[i];
            position.Add(element.Id, i);
            json[i] = JsonSerializer.SerializeToUtf8Bytes(element);
            foreach (var dependency in element.Dependencies)
            {
                if (!dependents.TryGetValue(dependency, out var list)) dependents.Add(dependency, list = new());
                list.Add(element.Id);
            }
        }
        // Project JSON is {"Id":..,"Revision":..,"Elements":[e0,e1,..]}: the prefix up to '[' plus element bytes.
        var empty = JsonSerializer.Serialize(before with { Elements = Array.Empty<Element>() });
        if (!empty.EndsWith("[]}", StringComparison.Ordinal)) throw new InvalidOperationException("hash_layout");
        var prefix = Encoding.UTF8.GetBytes(empty[..^2]);
        var hash = Hash(prefix, json, out _);
        // The streamed layout must reproduce Compiler.Hash exactly, or no plan built from it could match.
        if (hash != Compiler.Hash(before)) throw new InvalidOperationException("hash_layout");
        return new(before, position, json, dependents, prefix, hash);
    }

    static string Hash(byte[] prefix, IEnumerable<byte[]> segments, out long bytes)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(prefix); bytes = prefix.Length;
        var first = true;
        foreach (var segment in segments)
        {
            if (!first) { sha.AppendData(Comma); bytes++; }
            sha.AppendData(segment); bytes += segment.Length; first = false;
        }
        sha.AppendData(Suffix); bytes += Suffix.Length;
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    static bool WellFormed(Element? element) =>
        element is not null && element.GeometryMetres is not null && element.Dependencies is not null;

    // maxAffected bounds the accepted dirty closure; a larger closure is refused (edit_bound_exceeded) after the
    // closure and its validity checks run, so invalid_ir always takes precedence (the traversal itself is not capped).
    // A caller may then fall back to Compiler.Diff.
    // P3-10 createOrder (optional; null keeps the P3-05 layout byte for byte): survivors keep before order and the
    // creates, stably sorted by createOrder (ties in edit order), are merged in: each goes immediately before the first
    // survivor (its after version) that it compares strictly less than, the rest last. Create and update operations
    // then follow result.After order, as in Compiler.Diff, so the plan is always byte-identical to
    // Compiler.Diff(before, result.After). With IncrementalDiff.OrderOf(target) and survivors in target's relative
    // order (e.g. compile(before) against compile(after)), result.After is target's order. The comparer runs only
    // on after elements, after every refusal check; its exceptions propagate (the sort may wrap them in
    // InvalidOperationException). Cost: an O(c log c) sort of the c creates plus at most one comparison per
    // survivor during the O(project) assembly; serialization and traversal are unchanged.
    public IncrementalDiffResult Diff(IReadOnlyList<ElementEdit> edits, int maxAffected = int.MaxValue, IComparer<Element>? createOrder = null)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (maxAffected < 0) throw new ArgumentException(IncrementalDiff.InvalidEdit);
        if (edits.Count > IncrementalDiff.MaxEdits) throw new ArgumentException(IncrementalDiff.BoundExceeded);
        long traversed = 0;

        // ---- Edit-set shape: one edit per id, update/delete of existing ids, create of new ids ----
        var seen = new HashSet<Guid>();
        var updates = new Dictionary<Guid, Element>();
        var deletes = new HashSet<Guid>();
        var creates = new List<Element>();
        var created = new Dictionary<Guid, Element>();
        foreach (var edit in edits)
        {
            if (edit is null) throw new ArgumentException(IncrementalDiff.InvalidEdit);
            switch (edit.Action)
            {
                case "create" or "update":
                    if (!WellFormed(edit.Element) || edit.Element!.Id != edit.Id) throw new ArgumentException(IncrementalDiff.InvalidEdit);
                    break;
                case "delete":
                    if (edit.Element is not null) throw new ArgumentException(IncrementalDiff.InvalidEdit);
                    break;
                default:
                    throw new ArgumentException(IncrementalDiff.InvalidEdit);
            }
            if (!seen.Add(edit.Id)) throw new ArgumentException(IncrementalDiff.DuplicateId);
            var exists = position.ContainsKey(edit.Id);
            if (edit.Action == "create")
            {
                if (exists) throw new ArgumentException(IncrementalDiff.CreateExists);
                creates.Add(edit.Element!); created.Add(edit.Id, edit.Element!);
            }
            else if (!exists) throw new ArgumentException(IncrementalDiff.UnknownId);
            else if (edit.Action == "update") updates.Add(edit.Id, edit.Element!);
            else deletes.Add(edit.Id);
        }

        // ---- Local IR checks (same refusal as Compiler.Diff: invalid_ir), before any serialization, which rejects non-finite numbers ----
        bool InAfter(Guid id) => created.ContainsKey(id) || (position.ContainsKey(id) && !deletes.Contains(id));
        foreach (var element in updates.Values.Concat(creates))
        {
            if (element.GeometryMetres.Any(x => !double.IsFinite(x))) throw new InvalidOperationException("invalid_ir");
            if (element.Kind == "wall" && !GeometryCodec.IsValidWall(element.GeometryMetres)) throw new InvalidOperationException("invalid_ir");
            if (element.Dependencies.Any(dependency => !InAfter(dependency))) throw new InvalidOperationException("invalid_ir");
        }
        // An unedited element that depended on a deleted one would dangle; edited ones were checked above.
        var dangling = false;
        foreach (var id in deletes)
            if (dependents.TryGetValue(id, out var list))
                foreach (var dependent in list) { traversed++; dangling |= !deletes.Contains(dependent) && !updates.ContainsKey(dependent); }
        if (dangling) throw new InvalidOperationException("invalid_ir");

        // ---- Serialize only edited elements; an update with unchanged JSON is not an operation ----
        var serialized = 0;
        var newJson = new Dictionary<Guid, byte[]>();
        var changed = new List<Guid>();
        foreach (var (id, element) in updates)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(element); serialized++;
            newJson.Add(id, bytes);
            if (!bytes.AsSpan().SequenceEqual(json[position[id]])) changed.Add(id);
        }
        foreach (var element in creates) { newJson.Add(element.Id, JsonSerializer.SerializeToUtf8Bytes(element)); serialized++; }

        // ---- Dirty closure over reverse dependencies (the bound is applied after the cycle check) ----
        // After-edges whose dependent is unedited (or a no-op update) equal before-edges; other after-edges start at
        // a seed, so the before reverse index yields the same closure as Compiler.Diff's union of before and after.
        var dirty = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        void Mark(Guid id)
        {
            if (dirty.Add(id)) queue.Enqueue(id);
        }
        foreach (var id in changed) Mark(id);
        foreach (var element in creates) Mark(element.Id);
        foreach (var id in deletes) Mark(id);
        while (queue.TryDequeue(out var id))
            if (dependents.TryGetValue(id, out var list)) foreach (var dependent in list) { traversed++; Mark(dependent); }

        // ---- Cycle check inside the closure: before is acyclic, so any cycle of after contains a seed, and every
        // element on it transitively depends on that seed, hence lies in dirty. Kahn's algorithm on dirty ∩ after. ----
        Element AfterElement(Guid id) => updates.TryGetValue(id, out var update) ? update
            : created.TryGetValue(id, out var fresh) ? fresh : Before.Elements[position[id]];
        var nodes = dirty.Where(id => !deletes.Contains(id)).ToArray();
        var pending = new Dictionary<Guid, int>(nodes.Length);
        var reverse = new Dictionary<Guid, List<Guid>>(nodes.Length);
        foreach (var id in nodes) { pending.Add(id, 0); reverse.Add(id, new()); }
        foreach (var id in nodes)
        {
            foreach (var dependency in AfterElement(id).Dependencies)
            {
                traversed++;
                if (reverse.TryGetValue(dependency, out var list)) { pending[id]++; list.Add(id); }
            }
        }
        var ready = new Queue<Guid>(nodes.Where(id => pending[id] == 0));
        var processed = 0;
        while (ready.TryDequeue(out var id))
        {
            processed++;
            foreach (var dependent in reverse[id]) if (--pending[dependent] == 0) ready.Enqueue(dependent);
        }
        if (processed != nodes.Length) throw new InvalidOperationException("invalid_ir");
        // invalid_ir always wins over the bound: the bound limits the accepted closure, checked after validity.
        if (dirty.Count > maxAffected) throw new ArgumentException(IncrementalDiff.BoundExceeded);

        // ---- After project (reference copy, no serialization), operations in Compiler.Diff order, AfterHash ----
        var after = new List<Element>(Before.Elements.Length - deletes.Count + creates.Count);
        var segments = new List<byte[]>(after.Capacity);
        for (var i = 0; i < Before.Elements.Length; i++)
        {
            var element = Before.Elements[i];
            if (deletes.Contains(element.Id)) continue;
            if (updates.TryGetValue(element.Id, out var update)) { after.Add(update); segments.Add(newJson[element.Id]); }
            else { after.Add(element); segments.Add(json[i]); }
        }
        foreach (var element in creates) { after.Add(element); segments.Add(newJson[element.Id]); }
        if (createOrder is not null && creates.Count > 0) Place(after, segments, creates, createOrder, newJson);
        var afterHash = Hash(prefix, segments, out var hashedBytes);

        // Compiler.Diff order: creates and updates in after order, then deletes in before order. Without createOrder
        // after is survivors then creates, i.e. updates in before order then creates in edit order (P3-05).
        var operations = new List<Operation>(changed.Count + creates.Count + deletes.Count);
        var changedIds = changed.ToHashSet();
        foreach (var element in after)
        {
            if (created.ContainsKey(element.Id)) operations.Add(new("create", element));
            else if (changedIds.Contains(element.Id)) operations.Add(new("update", element));
        }
        foreach (var id in deletes.OrderBy(id => position[id])) operations.Add(new("delete", Before.Elements[position[id]]));

        var plan = new ExecutionPlan(Before.Id, Before.Revision, beforeHash, afterHash, operations.ToArray(), dirty.Order().ToArray());
        var work = new IncrementalWork(edits.Count, serialized, dirty.Count, Before.Elements.Length, hashedBytes, traversed);
        return Remember(new(Before with { Elements = after.ToArray() }, plan, work), deletes, updates, creates, newJson);
    }

    // Re-lays after (survivors, then the creates in edit order) with the creates merged at their createOrder place.
    static void Place(List<Element> after, List<byte[]> segments, List<Element> creates, IComparer<Element> createOrder, Dictionary<Guid, byte[]> newJson)
    {
        var survivors = after.Count - creates.Count;
        var sorted = creates.OrderBy(element => element, createOrder).ToArray(); // stable: ties keep edit order
        var placed = new List<Element>(after.Count);
        var bytes = new List<byte[]>(after.Count);
        var next = 0;
        for (var i = 0; i < survivors; i++)
        {
            while (next < sorted.Length && createOrder.Compare(sorted[next], after[i]) < 0) { placed.Add(sorted[next]); bytes.Add(newJson[sorted[next].Id]); next++; }
            placed.Add(after[i]); bytes.Add(segments[i]);
        }
        for (; next < sorted.Length; next++) { placed.Add(sorted[next]); bytes.Add(newJson[sorted[next].Id]); }
        after.Clear(); after.AddRange(placed);
        segments.Clear(); segments.AddRange(bytes);
    }
}

public static partial class IncrementalDiff
{
    public const int MaxEdits = 1024;
    public const string InvalidEdit = "edit_invalid";
    public const string UnknownId = "edit_unknown_id";
    public const string DuplicateId = "edit_duplicate_id";
    public const string CreateExists = "edit_create_exists";
    public const string BoundExceeded = "edit_bound_exceeded";

    // One-shot convenience: indexes before (O(project), once) and diffs. Reuse IncrementalDiffIndex for many diffs.
    public static IncrementalDiffResult Diff(Project before, IReadOnlyList<ElementEdit> edits, int maxAffected = int.MaxValue, IComparer<Element>? createOrder = null) =>
        IncrementalDiffIndex.Build(before).Diff(edits, maxAffected, createOrder);

    // P3-10 create placement taken from a target project's element order (e.g. IntentEditSet.After, the compiled
    // after project): elements compare by their position in target; ids absent from target compare equal to each
    // other and after every present id (so with no create in target the default layout results). Never throws on
    // non-null elements; null sorts first. Built in O(target) once; target must not be mutated while it is used.
    public static IComparer<Element> OrderOf(Project target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(target.Elements);
        var rank = new Dictionary<Guid, int>(target.Elements.Length);
        for (var i = 0; i < target.Elements.Length; i++) if (target.Elements[i] is { } element) rank.TryAdd(element.Id, i);
        return new TargetOrder(rank);
    }

    sealed class TargetOrder(Dictionary<Guid, int> rank) : IComparer<Element>
    {
        public int Compare(Element? x, Element? y)
        {
            if (x is null || y is null) return x is null ? (y is null ? 0 : -1) : 1;
            return Rank(x).CompareTo(Rank(y));
        }

        int Rank(Element element) => rank.TryGetValue(element.Id, out var index) ? index : int.MaxValue;
    }
}
