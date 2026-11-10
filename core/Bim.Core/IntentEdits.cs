using System.Text.Json;
namespace Bim.Core;

// P3-09: a bounded change of a layout intent as the P3-05 element edit set. Before and After are the compiled
// projects (IntentCompiler.Compile with the same project id and revision); Edits is the minimal edit set between them.
public sealed record IntentEditSet(Project Before, Project After, ElementEdit[] Edits);

// IntentEdits.From compiles both intents and returns one edit per element whose compiled form changed: an update
// (full new element) for an id in both whose serialized JSON differs, a create for a new id, a delete for a removed
// id. Elements with the same id and JSON are never in the set. Order: updates in before order, creates in after
// (compiled) order, deletes in before order. The set is exactly the operation set of
// Compiler.Diff(Before, After), so IncrementalDiffIndex.Build(Before).Diff(Edits) gives a plan with the same ids,
// BeforeHash, DirtyIds and operations; it is byte-identical to Compiler.Diff(Before, After) when no created element
// sorts before a surviving one (creates append, compile orders by kind and key), and otherwise byte-identical to
// Compiler.Diff(Before, After in the incremental order), the same elements in another order.
// Refusals: compiler refusals of before, then of after, propagate unchanged (IntentException, ArgumentException
// invalid_project_identity / null_intent_item); maxEdits outside 0..IncrementalDiff.MaxEdits is edit_invalid and
// more edits than maxEdits is edit_bound_exceeded (ArgumentException), checked after both compiles.
// Cost: two compiles and one JSON serialization of every element of both projects, O(project).
public static class IntentEdits
{
    // Typed-helper refusal for an opening key that is not in the intent (unknown rooms use IntentCodes.UnknownRoom).
    public const string UnknownOpening = "unknown_opening";

    public static IntentEditSet From(Guid projectId, int revision, LayoutIntent before, LayoutIntent after, int maxEdits = IncrementalDiff.MaxEdits)
    {
        if (maxEdits < 0 || maxEdits > IncrementalDiff.MaxEdits) throw new ArgumentException(IncrementalDiff.InvalidEdit);
        var compiledBefore = IntentCompiler.Compile(projectId, revision, before);
        var compiledAfter = IntentCompiler.Compile(projectId, revision, after);
        var old = compiledBefore.Elements.ToDictionary(element => element.Id);
        var next = compiledAfter.Elements.ToDictionary(element => element.Id);
        var edits = new List<ElementEdit>();
        foreach (var element in compiledBefore.Elements)
            if (next.TryGetValue(element.Id, out var updated) && JsonSerializer.Serialize(updated) != JsonSerializer.Serialize(element))
                edits.Add(ElementEdit.Update(updated));
        foreach (var element in compiledAfter.Elements)
            if (!old.ContainsKey(element.Id)) edits.Add(ElementEdit.Create(element));
        foreach (var element in compiledBefore.Elements)
            if (!next.ContainsKey(element.Id)) edits.Add(ElementEdit.Delete(element.Id));
        if (edits.Count > maxEdits) throw new ArgumentException(IncrementalDiff.BoundExceeded);
        return new IntentEditSet(compiledBefore, compiledAfter, edits.ToArray());
    }

    // Typed intent changes. Each returns a new intent with one record replaced in place (list order and every other
    // field kept); nothing is validated beyond the key lookup, so an invalid result is refused by the compiler.

    // Sets the room's axis rectangle (south-west axis corner and axis-to-axis size).
    public static LayoutIntent ResizeRoom(LayoutIntent intent, string room, double xMetres, double yMetres, double widthMetres, double depthMetres) =>
        WithRoom(intent, room, item => item with { XMetres = xMetres, YMetres = yMetres, WidthMetres = widthMetres, DepthMetres = depthMetres });

    // Sets the opening's offset along its host and, when host is given, its host.
    public static LayoutIntent MoveOpening(LayoutIntent intent, string opening, double offsetMetres, IntentHost? host = null)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(intent.Openings);
        if (!intent.Openings.Any(item => Is(item, opening))) throw new IntentException(UnknownOpening, null, opening);
        return intent with
        {
            Openings = intent.Openings.Select(item => Is(item, opening) ? item with { OffsetMetres = offsetMetres, Host = host ?? item.Host } : item).ToArray(),
        };
    }

    // Sets the wall type key used by every side of the room.
    public static LayoutIntent ChangeWallType(LayoutIntent intent, string room, string wallType) =>
        WithRoom(intent, room, item => item with { WallType = wallType });

    // Helper calls, unlike item?.Key or an inline null test, leave the declared (non-null) flow state of item unchanged.
    static bool Is(IntentRoom? item, string key) => item is not null && item.Key == key;
    static bool Is(IntentOpening? item, string key) => item is not null && item.Key == key;

    static LayoutIntent WithRoom(LayoutIntent intent, string room, Func<IntentRoom, IntentRoom> change)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(intent.Rooms);
        if (!intent.Rooms.Any(item => Is(item, room))) throw new IntentException(IntentCodes.UnknownRoom, null, room);
        return intent with { Rooms = intent.Rooms.Select(item => Is(item, room) ? change(item) : item).ToArray() };
    }
}
