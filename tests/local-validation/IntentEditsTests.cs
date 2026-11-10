using Bim.Core;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// P3-09 oracle: a bounded intent change (resize room, move opening, change wall type) on a compiled layout intent
// yields the ElementEdit set E = IntentEdits.From(id, rev, before, after) such that, with B = compile(before) and
// A = compile(after):
//   - E is exactly the operation set of Compiler.Diff(B, A): one update per element whose serialized JSON differs,
//     one create per new id, one delete per removed id, each carrying the compiled element; untouched elements
//     (same id, same JSON) are never in E;
//   - IncrementalDiffIndex.Build(B).Diff(E).Plan equals Compiler.Diff(B, A) in ProjectId, ExpectedRevision,
//     BeforeHash, DirtyIds and the operation set (each operation byte-identical), and is byte-identical to
//     Compiler.Diff(B, A') where A' is A in the incremental after order; result.After serializes like A' and holds
//     exactly A's elements. When that order is A's own (no create, or creates that sort last) the whole plan is
//     byte-identical to Compiler.Diff(B, A), AfterHash included. Creates append (P3-05), while compile orders by
//     kind and key, so a create that sorts before a surviving element gives the same content in another order.
// Seeded chains of random changes of each kind check this per step; refused changes must propagate the compiler's
// refusal (same code and issues).
static class IntentEditsOracle
{
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    static readonly Provenance Provenance = new("intent-edits-tests", "unit");
    static readonly Guid ProjectId = new(SHA256.HashData(Encoding.UTF8.GetBytes("intent-edits:project")).Take(16).ToArray());
    const int Revision = 3;

    static string N(long value) => value.ToString(Invariant);
    static string Json(object? value) => JsonSerializer.Serialize(value);
    static Guid Id(string key) => IntentCompiler.Id(ProjectId, key);

    static IntentRoom Room(string key, string level, string type, double x, double y, double width, double depth) =>
        new(key, "Room " + key, level, type, x, y, width, depth);
    static IntentOpening Window(string key, IntentHost host, double offset, double width = 1.2) =>
        new(key, IntentOpeningKind.Window, host, offset, width, 1.2, 0.9);
    static IntentOpening Door(string key, IntentHost host, double offset) =>
        new(key, IntentOpeningKind.Door, host, offset, 0.9, 2.1, 0);
    static IntentRoomSide Side(string room, IntentSide side) => new(room, side);
    static IntentSharedWall Shared(string a, string b) => new(a, b);

    // A row of three adjacent rooms with one more on top of the first, three isolated rooms of three wall types, and
    // two adjacent rooms on an upper level; doors on shared walls, windows on room sides.
    public static LayoutIntent Base() => new(LayoutIntent.CurrentVersion, Provenance,
        new[] { new IntentLevel("l0", "Ground", 0), new IntentLevel("l1", "Upper", 3) },
        new[] { new IntentWallType("a", "A", 0.2, 2.8), new IntentWallType("b", "B", 0.3, 3.0), new IntentWallType("c", "C", 0.15, 2.6) },
        new[]
        {
            Room("r0", "l0", "a", 0, 0, 4, 3), Room("r1", "l0", "a", 4, 0, 3, 3), Room("r2", "l0", "a", 7, 0, 4, 3), Room("r3", "l0", "a", 0, 3, 4, 3),
            Room("i0", "l0", "a", 20, 0, 4, 3), Room("i1", "l0", "b", 30, 0, 4, 3), Room("i2", "l0", "c", 40, 0, 5, 4),
            Room("u0", "l1", "b", 0, 0, 4, 3), Room("u1", "l1", "b", 4, 0, 4, 3),
        },
        new[]
        {
            Door("d0", Shared("r0", "r1"), 0.8), Door("d1", Shared("r1", "r2"), 0.8), Door("d4", Shared("r0", "r3"), 1.0),
            Window("w0", Side("r0", IntentSide.South), 1.0), Window("w1", Side("r2", IntentSide.East), 0.8, 1.0),
            Window("w2", Side("i0", IntentSide.South), 1.0), Window("w3", Side("i1", IntentSide.North), 1.0),
            Door("d2", Side("i2", IntentSide.West), 0.8), Window("w4", Side("u0", IntentSide.South), 1.0), Door("d3", Shared("u0", "u1"), 0.8),
        });

    static LayoutIntent Reversed(LayoutIntent intent) => intent with
    {
        Levels = intent.Levels.Reverse().ToArray(), WallTypes = intent.WallTypes.Reverse().ToArray(),
        Rooms = intent.Rooms.Reverse().ToArray(), Openings = intent.Openings.Reverse().ToArray(),
    };

    public static string? Code(Action action)
    {
        try { action(); return null; }
        catch (IntentException error) { return error.Code; }
        catch (ArgumentException error) { return error.Message; }
        catch (InvalidOperationException error) { return error.Message; }
        catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
    }

    // The refusal of an action as code plus issues JSON, or null when it succeeds.
    static string? Refusal(Action action)
    {
        try { action(); return null; }
        catch (IntentException error) { return "intent:" + error.Code + ":" + Json(error.Issues); }
        catch (Exception error) { return error.GetType().Name + ":" + error.Message; }
    }

    // Shape of one verified step.
    public sealed record Outcome(int Edits, int Creates, int Deletes, int Untouched, bool ExactOrder);

    // Every oracle on one accepted change; returns the first problem, or null.
    public static string? Verify(LayoutIntent before, LayoutIntent after, out Outcome outcome)
    {
        outcome = new Outcome(0, 0, 0, 0, false);
        var b = IntentCompiler.Compile(ProjectId, Revision, before);
        var a = IntentCompiler.Compile(ProjectId, Revision, after);
        var set = IntentEdits.From(ProjectId, Revision, before, after);
        if (Json(set.Before) != Json(b)) return "set.Before is not compile(before)";
        if (Json(set.After) != Json(a)) return "set.After is not compile(after)";
        var edits = set.Edits;
        if (edits.Length > IncrementalDiff.MaxEdits) return "edit set exceeds IncrementalDiff.MaxEdits";

        // The edit set is exactly the operation set of Compiler.Diff(B, A).
        var expected = Compiler.Diff(b, a);
        var operations = expected.Operations.ToDictionary(operation => operation.Element.Id);
        if (edits.Length != operations.Count) return "edit count " + N(edits.Length) + " vs Compiler.Diff operations " + N(operations.Count);
        if (edits.Select(edit => edit.Id).Distinct().Count() != edits.Length) return "duplicate edit id";
        foreach (var edit in edits)
        {
            if (!operations.TryGetValue(edit.Id, out var operation)) return "edit of " + edit.Id + " is not a Compiler.Diff operation";
            if (operation.Action != edit.Action) return "edit action " + edit.Action + " vs " + operation.Action + " for " + edit.Id;
            if (edit.Action == "delete" ? edit.Element is not null : Json(edit.Element) != Json(operation.Element)) return "edit element differs from the operation for " + edit.Id;
        }

        // Untouched elements (same id and JSON in both compiles) are never edited.
        var afterById = a.Elements.ToDictionary(element => element.Id);
        var edited = edits.Select(edit => edit.Id).ToHashSet();
        var untouched = b.Elements.Where(element => afterById.TryGetValue(element.Id, out var next) && Json(next) == Json(element)).Select(element => element.Id).ToArray();
        if (untouched.Any(edited.Contains)) return "an untouched element is in the edit set";
        if (untouched.Length + edits.Count(edit => edit.Action != "create") != b.Elements.Length) return "touched/untouched partition does not cover before";

        // Deterministic edit order: updates in before order, creates in after order, deletes in before order.
        var beforeIds = b.Elements.Select(element => element.Id).ToHashSet();
        var order = b.Elements.Where(element => edited.Contains(element.Id) && afterById.ContainsKey(element.Id)).Select(element => element.Id)
            .Concat(a.Elements.Where(element => !beforeIds.Contains(element.Id)).Select(element => element.Id))
            .Concat(b.Elements.Where(element => !afterById.ContainsKey(element.Id)).Select(element => element.Id));
        if (!edits.Select(edit => edit.Id).SequenceEqual(order)) return "edit order is not updates, creates, deletes in project order";

        // The incremental plan against the full diff.
        var result = IncrementalDiffIndex.Build(b).Diff(edits);
        var plan = result.Plan;
        if (plan.ProjectId != expected.ProjectId || plan.ExpectedRevision != expected.ExpectedRevision || plan.BeforeHash != expected.BeforeHash)
            return "plan identity or BeforeHash differs";
        if (!plan.DirtyIds.SequenceEqual(expected.DirtyIds)) return "DirtyIds differ";
        string Canonical(IEnumerable<Operation> items) => Json(items.OrderBy(operation => operation.Action, StringComparer.Ordinal).ThenBy(operation => operation.Element.Id).ToArray());
        if (Canonical(plan.Operations) != Canonical(expected.Operations)) return "operation sets differ";
        if (result.After.Elements.Length != a.Elements.Length || result.After.Elements.Any(element => !afterById.ContainsKey(element.Id))) return "incremental after holds other elements than compile(after)";
        // A' built independently of the incremental result: A's survivors in B order, then the creates in edit order.
        var expectedAfter = a with
        {
            Elements = b.Elements.Where(element => afterById.ContainsKey(element.Id)).Select(element => afterById[element.Id])
                .Concat(edits.Where(edit => edit.Action == "create").Select(edit => edit.Element!)).ToArray(),
        };
        if (Json(result.After) != Json(expectedAfter)) return "incremental after is not A's survivors in B order followed by the creates in edit order";
        if (Json(plan) != Json(Compiler.Diff(b, expectedAfter))) return "plan is not byte-identical to Compiler.Diff(B, A')";
        var exactOrder = Json(result.After) == Json(a);
        // Documented condition, both directions: exact order iff no created element sorts before a survivor in A.
        var positions = a.Elements.Select((element, index) => (element.Id, index)).ToArray();
        var lastSurvivor = positions.Where(item => beforeIds.Contains(item.Id)).Select(item => item.index).DefaultIfEmpty(-1).Max();
        var firstCreate = positions.Where(item => !beforeIds.Contains(item.Id)).Select(item => item.index).DefaultIfEmpty(int.MaxValue).Min();
        var predicted = firstCreate > lastSurvivor;
        if (exactOrder != predicted) return "exact order " + exactOrder + " but the documented condition predicts " + predicted;
        if (exactOrder && Json(plan) != Json(expected)) return "same order but plan is not byte-identical to Compiler.Diff(B, A)";

        // Deterministic and independent of intent list order.
        if (Json(IntentEdits.From(ProjectId, Revision, Reversed(before), Reversed(after))) != Json(set)) return "edit set depends on intent list order";

        outcome = new Outcome(edits.Length, edits.Count(edit => edit.Action == "create"), edits.Count(edit => edit.Action == "delete"), untouched.Length, exactOrder);
        return null;
    }

    // FromStored on a stored project (any element order, e.g. a committed incremental After): the edit set is the
    // operation set of Compiler.Diff(stored, compile(after)), and the incremental plan describes the project to commit.
    public static string? VerifyStored(Project stored, LayoutIntent after, out Project committed)
    {
        committed = stored;
        var a = IntentCompiler.Compile(stored.Id, stored.Revision, after);
        var set = IntentEdits.FromStored(stored, after);
        if (Json(set.Before) != Json(stored)) return "stored set.Before is not the stored project";
        if (Json(set.After) != Json(a)) return "stored set.After is not compile(after)";
        var expected = Compiler.Diff(stored, a);
        var operations = expected.Operations.ToDictionary(operation => operation.Element.Id);
        if (set.Edits.Length != operations.Count || set.Edits.Any(edit => !operations.TryGetValue(edit.Id, out var operation) || operation.Action != edit.Action
            || (edit.Action != "delete" && Json(edit.Element) != Json(operation.Element)))) return "stored edit set is not the Compiler.Diff(stored, A) operation set";
        var afterById = a.Elements.ToDictionary(element => element.Id);
        var expectedAfter = a with
        {
            Elements = stored.Elements.Where(element => afterById.ContainsKey(element.Id)).Select(element => afterById[element.Id])
                .Concat(set.Edits.Where(edit => edit.Action == "create").Select(edit => edit.Element!)).ToArray(),
        };
        var result = IncrementalDiffIndex.Build(stored).Diff(set.Edits);
        if (Json(result.After) != Json(expectedAfter)) return "stored incremental after is not survivors in stored order followed by creates";
        if (Json(result.Plan) != Json(Compiler.Diff(stored, expectedAfter))) return "stored plan is not byte-identical to Compiler.Diff(stored, A')";
        if (result.Plan.BeforeHash != Compiler.Hash(stored)) return "stored plan BeforeHash is not the stored hash";
        committed = result.After;
        return null;
    }

    static bool Touch(IntentRoom p, IntentRoom q) =>
        p.Level == q.Level && p.XMetres - IntentCompiler.Tolerance <= q.XMetres + q.WidthMetres && q.XMetres - IntentCompiler.Tolerance <= p.XMetres + p.WidthMetres
        && p.YMetres - IntentCompiler.Tolerance <= q.YMetres + q.DepthMetres && q.YMetres - IntentCompiler.Tolerance <= p.YMetres + p.DepthMetres;

    // Locality of a single-room resize: a room touching neither the old nor the new rectangle (or on another level)
    // keeps its space, its bounding walls and the openings they host out of the edit set. Returns rooms checked.
    public static int Unrelated(LayoutIntent before, LayoutIntent after, string room, out string? problem)
    {
        problem = null;
        var old = before.Rooms.Single(item => item.Key == room);
        var resized = after.Rooms.Single(item => item.Key == room);
        var b = IntentCompiler.Compile(ProjectId, Revision, before);
        var a = IntentCompiler.Compile(ProjectId, Revision, after);
        var edited = IntentEdits.From(ProjectId, Revision, before, after).Edits.Select(edit => edit.Id).ToHashSet();
        var checkedRooms = 0;
        foreach (var other in before.Rooms.Where(item => item.Key != room && !Touch(item, old) && !Touch(item, resized)))
        {
            var space = Id("space:" + other.Key);
            var walls = b.Elements.Concat(a.Elements).Where(element => element.Id == space).SelectMany(element => element.Dependencies.Skip(1)).ToHashSet();
            var hosted = b.Elements.Concat(a.Elements).Where(element => element.Kind is "door" or "window" && walls.Contains(element.Dependencies[1])).Select(element => element.Id);
            var keyed = walls.Append(space).Concat(hosted).ToArray();
            if (keyed.Any(edited.Contains)) { problem = "resizing " + room + " edited an element of unrelated room " + other.Key; return checkedRooms; }
            checkedRooms++;
        }
        return checkedRooms;
    }

    static void Guard(Action<bool, string, string> check, string name, Action body)
    {
        try { body(); }
        catch (Exception error) { check(false, name, "unexpected " + error.GetType().Name + ": " + error.Message); }
    }

    public static void Run(Action<bool, string, string> check)
    {
        Guard(check, "intent edits: units", () => Units(check));
        Guard(check, "intent edits: seeded corpus", () => Corpus(check));
    }

    static void Units(Action<bool, string, string> check)
    {
        var base0 = Base();
        check(Code(() => IntentCompiler.Compile(ProjectId, Revision, base0)) is null, "intent edits: the base layout compiles", Code(() => IntentCompiler.Compile(ProjectId, Revision, base0)) ?? "");
        string Keys(IEnumerable<ElementEdit> edits) => string.Join(",", edits.Select(edit => edit.Action + ":" + edit.Id.ToString("N")[..8]));
        bool Exactly(IntentEditSet set, string action, params string[] keys) =>
            set.Edits.All(edit => edit.Action == action) && set.Edits.Select(edit => edit.Id).ToHashSet().SetEquals(keys.Select(Id)) && set.Edits.Length == keys.Length;

        // No change, and a rename (names never reach BIM-IR), give an empty edit set.
        check(IntentEdits.From(ProjectId, Revision, base0, base0).Edits.Length == 0, "intent edits: an unchanged intent gives no edits", "");
        var renamed = base0 with { Rooms = base0.Rooms.Select(room => room with { Name = "Renamed " + room.Key }).ToArray() };
        check(IntentEdits.From(ProjectId, Revision, base0, renamed).Edits.Length == 0, "intent edits: renaming rooms gives no edits", "");

        // Move opening: exactly one update of that opening.
        var moved = IntentEdits.MoveOpening(base0, "w0", 2.0);
        var moveSet = IntentEdits.From(ProjectId, Revision, base0, moved);
        check(Exactly(moveSet, "update", "opening:w0"), "intent edits: moving window w0 updates exactly opening:w0", Keys(moveSet.Edits));
        check(Verify(base0, moved, out var moveOutcome) is null && moveOutcome.ExactOrder, "intent edits: moved window plan is byte-identical to Compiler.Diff", Verify(base0, moved, out _) ?? "");
        var rehosted = IntentEdits.MoveOpening(base0, "w0", 0.5, Side("r0", IntentSide.West));
        var rehostSet = IntentEdits.From(ProjectId, Revision, base0, rehosted);
        check(Exactly(rehostSet, "update", "opening:w0") && rehostSet.Edits[0].Element!.Dependencies.Contains(Id("wall:r0:west")),
            "intent edits: moving w0 to another host updates only opening:w0, now hosted by r0 west", Keys(rehostSet.Edits));
        check(rehosted.Openings.Single(opening => opening.Key == "w0").Host == Side("r0", IntentSide.West) && moved.Openings.Single(opening => opening.Key == "w0").Host == Side("r0", IntentSide.South),
            "intent edits: MoveOpening keeps the host unless one is given", "");

        // Resize an isolated room: only its own moved walls and its space.
        var wider = IntentEdits.ResizeRoom(base0, "i0", 20, 0, 4.5, 3);
        var widerSet = IntentEdits.From(ProjectId, Revision, base0, wider);
        check(Exactly(widerSet, "update", "wall:i0:south", "wall:i0:east", "wall:i0:north", "space:i0"),
            "intent edits: widening i0 updates exactly its south, east and north walls and its space", Keys(widerSet.Edits));
        check(Verify(base0, wider, out var widerOutcome) is null && widerOutcome.ExactOrder, "intent edits: widened room plan is byte-identical to Compiler.Diff", Verify(base0, wider, out _) ?? "");

        // Change an isolated room's wall type: its four walls and its space (interior shrinks), not its window.
        var retyped = IntentEdits.ChangeWallType(base0, "i1", "c");
        var retypeSet = IntentEdits.From(ProjectId, Revision, base0, retyped);
        check(Exactly(retypeSet, "update", "wall:i1:south", "wall:i1:east", "wall:i1:north", "wall:i1:west", "space:i1"),
            "intent edits: retyping i1 updates exactly its four walls and its space", Keys(retypeSet.Edits));
        check(Verify(base0, retyped, out var retypeOutcome) is null && retypeOutcome.ExactOrder, "intent edits: retyped room plan is byte-identical to Compiler.Diff", Verify(base0, retyped, out _) ?? "");

        // Resize that changes adjacency: r1 grows north past r0/r3's corner, so new stretches are created and r3's
        // former east wall is deleted; created walls sort before spaces, so only the order differs.
        var taller = IntentEdits.ResizeRoom(base0, "r1", 4, 0, 3, 3.5);
        var tallerSet = IntentEdits.From(ProjectId, Revision, base0, taller);
        var creates = tallerSet.Edits.Where(edit => edit.Action == "create").Select(edit => edit.Id).ToHashSet();
        var deletes = tallerSet.Edits.Where(edit => edit.Action == "delete").Select(edit => edit.Id).ToHashSet();
        check(creates.SetEquals(new[] { "wall:r1|r3", "wall:r3:east>r1", "wall:r1:east>r2" }.Select(Id)) && deletes.SetEquals(new[] { Id("wall:r3:east") }),
            "intent edits: growing r1 north creates the r1|r3 wall and two split stretches and deletes r3's old east wall", Keys(tallerSet.Edits));
        var tallerProblem = Verify(base0, taller, out var tallerOutcome);
        check(tallerProblem is null && !tallerOutcome.ExactOrder, "intent edits: adjacency change agrees with Compiler.Diff up to element order", tallerProblem ?? "exact order");
        var untouchedKeys = new[] { "level:l0", "level:l1", "space:r0", "space:r2", "wall:r0:south", "wall:r0|r1", "wall:r1|r2", "opening:d0", "opening:d1", "opening:w0", "space:i0", "wall:u0|u1" };
        check(!tallerSet.Edits.Any(edit => untouchedKeys.Select(Id).Contains(edit.Id)), "intent edits: rooms and walls untouched by the r1 change are not in its edit set", Keys(tallerSet.Edits));

        // Commit path (ProjectStore): the plan describes result.After (incremental order), not set.After (compile order).
        var directory = Path.Combine(Path.GetTempPath(), "intent-edits-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProjectStore(directory);
            store.Initialize(tallerSet.Before);
            var tallerResult = IncrementalDiffIndex.Build(tallerSet.Before).Diff(tallerSet.Edits);
            check(Code(() => store.Commit(tallerSet.After, tallerResult.Plan, "taller-compile-order")) == "revision_conflict" && Json(store.Read()) == Json(tallerSet.Before),
                "intent edits: committing set.After (compile order) with a reordered plan is refused revision_conflict and writes nothing", "");
            var stored = store.Commit(tallerResult.After, tallerResult.Plan, "taller");
            check(stored.Revision == Revision + 1 && Json(store.Read()) == Json(tallerResult.After with { Revision = Revision + 1 }),
                "intent edits: committing result.After with its plan succeeds", N(stored.Revision));
            var moveNext = IntentEdits.MoveOpening(taller, "w0", 2.0);
            // Hazard: chaining from compile(previous intent) indexes compile order, not the stored order.
            var naive = IntentEdits.From(ProjectId, stored.Revision, taller, moveNext);
            var naivePlan = IncrementalDiffIndex.Build(naive.Before).Diff(naive.Edits).Plan;
            check(naivePlan.BeforeHash != Compiler.Hash(stored) && Code(() => store.Commit(naive.After, naivePlan, "naive")) == "revision_conflict",
                "intent edits: a next step indexed on compile(previous) has another BeforeHash than the stored project and is refused", "");
            // Documented path: diff against the stored project.
            var next = IntentEdits.FromStored(store.Read(), moveNext);
            var nextResult = IncrementalDiffIndex.Build(next.Before).Diff(next.Edits);
            check(next.Edits.Length == 1 && next.Edits[0].Id == Id("opening:w0") && Code(() => store.Commit(nextResult.After, nextResult.Plan, "move-next")) is null && store.Read().Revision == Revision + 2,
                "intent edits: FromStored(stored, next) gives the one-edit set and its plan commits", Keys(next.Edits));
            // Fallback: Compiler.Diff(stored, compile(after)) commits compile(after) itself.
            var fallbackAfter = IntentCompiler.Compile(ProjectId, Revision + 2, IntentEdits.MoveOpening(moveNext, "w0", 1.5));
            check(Code(() => store.Commit(fallbackAfter, Compiler.Diff(store.Read(), fallbackAfter), "fallback")) is null && Json(store.Read().Elements) == Json(fallbackAfter.Elements),
                "intent edits: the Compiler.Diff(stored, compile(after)) fallback commits compile(after)", "");
            check(Code(() => IntentEdits.FromStored(store.Read() with { Elements = store.Read().Elements.Skip(1).ToArray() }, moveNext)) == "invalid_ir",
                "intent edits: FromStored refuses a stored project that is not valid IR (invalid_ir)", "");
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }

        // Bound: a typed refusal above maxEdits (and the default IncrementalDiff.MaxEdits), argument checks.
        check(Code(() => IntentEdits.From(ProjectId, Revision, base0, wider, 3)) == IncrementalDiff.BoundExceeded, "intent edits: 4 edits above maxEdits 3 are refused edit_bound_exceeded",
            Code(() => IntentEdits.From(ProjectId, Revision, base0, wider, 3)) ?? "accepted");
        check(Code(() => IntentEdits.From(ProjectId, Revision, base0, wider, 4)) is null, "intent edits: 4 edits within maxEdits 4 are accepted", Code(() => IntentEdits.From(ProjectId, Revision, base0, wider, 4)) ?? "");
        check(Code(() => IntentEdits.From(ProjectId, Revision, base0, wider, -1)) == IncrementalDiff.InvalidEdit && Code(() => IntentEdits.From(ProjectId, Revision, base0, wider, IncrementalDiff.MaxEdits + 1)) == IncrementalDiff.InvalidEdit,
            "intent edits: maxEdits outside 0..MaxEdits is edit_invalid", "");
        var many = base0 with { Rooms = Enumerable.Range(0, 300).Select(i => Room("m" + N(i), "l0", "a", 100 + i * 10, 0, 4, 3)).ToArray(), Openings = Array.Empty<IntentOpening>() };
        var shifted = many with { Rooms = many.Rooms.Select(room => room with { YMetres = 50 }).ToArray() };
        check(Code(() => IntentEdits.From(ProjectId, Revision, many, shifted)) == IncrementalDiff.BoundExceeded, "intent edits: moving 300 rooms (1500 updates) exceeds IncrementalDiff.MaxEdits",
            Code(() => IntentEdits.From(ProjectId, Revision, many, shifted)) ?? "accepted");

        // Compiler refusals propagate unchanged (code and issues); before is compiled first.
        var conflict = IntentEdits.ChangeWallType(base0, "r1", "b");
        check(Refusal(() => IntentEdits.From(ProjectId, Revision, base0, conflict)) == Refusal(() => IntentCompiler.Compile(ProjectId, Revision, conflict))
            && Refusal(() => IntentCompiler.Compile(ProjectId, Revision, conflict))!.StartsWith("intent:" + IntentCodes.SharedWallConflict, StringComparison.Ordinal),
            "intent edits: retyping r1 alone propagates the compiler's shared_wall_conflict", Refusal(() => IntentEdits.From(ProjectId, Revision, base0, conflict)) ?? "accepted");
        var outside = IntentEdits.MoveOpening(base0, "w2", 3.5);
        check(Refusal(() => IntentEdits.From(ProjectId, Revision, base0, outside)) == Refusal(() => IntentCompiler.Compile(ProjectId, Revision, outside))
            && Refusal(() => IntentCompiler.Compile(ProjectId, Revision, outside))!.StartsWith("intent:" + IntentCodes.OpeningOutsideWall, StringComparison.Ordinal),
            "intent edits: an opening moved off its wall propagates opening_outside_wall", Refusal(() => IntentEdits.From(ProjectId, Revision, base0, outside)) ?? "accepted");
        var overlap = IntentEdits.ResizeRoom(base0, "i0", 19, 0, 12, 3);
        check(Refusal(() => IntentEdits.From(ProjectId, Revision, overlap, base0)) == Refusal(() => IntentCompiler.Compile(ProjectId, Revision, overlap))
            && Refusal(() => IntentCompiler.Compile(ProjectId, Revision, overlap))!.StartsWith("intent:" + IntentCodes.RoomOverlap, StringComparison.Ordinal),
            "intent edits: an invalid before intent propagates its refusal (room_overlap)", Refusal(() => IntentEdits.From(ProjectId, Revision, overlap, base0)) ?? "accepted");
        check(Code(() => IntentEdits.From(ProjectId, Revision, overlap, base0, -1)) == IncrementalDiff.InvalidEdit && Code(() => IntentEdits.FromStored(tallerSet.Before, overlap, -1)) == IncrementalDiff.InvalidEdit,
            "intent edits: an invalid maxEdits is edit_invalid before any compile refusal", Code(() => IntentEdits.From(ProjectId, Revision, overlap, base0, -1)) ?? "accepted");
        check(Refusal(() => IntentEdits.From(ProjectId, Revision, overlap, conflict)) == Refusal(() => IntentCompiler.Compile(ProjectId, Revision, overlap))
            && Refusal(() => IntentCompiler.Compile(ProjectId, Revision, overlap)) != Refusal(() => IntentCompiler.Compile(ProjectId, Revision, conflict)),
            "intent edits: with both intents invalid (different codes) the before refusal is reported", Refusal(() => IntentEdits.From(ProjectId, Revision, overlap, conflict)) ?? "accepted");
        check(Refusal(() => IntentEdits.FromStored(tallerSet.Before, conflict)) == Refusal(() => IntentCompiler.Compile(ProjectId, Revision, conflict)),
            "intent edits: FromStored propagates the compiler refusal of after", "");
        check(Code(() => IntentEdits.From(Guid.Empty, Revision, base0, base0)) == "invalid_project_identity" && Code(() => IntentEdits.From(ProjectId, -1, base0, base0)) == "invalid_project_identity",
            "intent edits: project identity refusal propagates", "");
        check(Code(() => IntentEdits.ResizeRoom(base0, "zz", 0, 0, 4, 3)) == IntentCodes.UnknownRoom && Code(() => IntentEdits.ChangeWallType(base0, "zz", "a")) == IntentCodes.UnknownRoom
            && Code(() => IntentEdits.MoveOpening(base0, "zz", 1)) == IntentEdits.UnknownOpening,
            "intent edits: helpers refuse unknown room (unknown_room) and opening (unknown_opening) keys", "");
        var badType = IntentEdits.ChangeWallType(base0, "i0", "zz");
        check(Code(() => IntentEdits.From(ProjectId, Revision, base0, badType)) == IntentCodes.UnknownWallType, "intent edits: an unknown wall type is refused by the compiler (unknown_wall_type)",
            Code(() => IntentEdits.From(ProjectId, Revision, base0, badType)) ?? "accepted");
        check(Json(IntentEdits.ResizeRoom(base0, "i0", 20, 0, 4, 3)) == Json(base0) && IntentEdits.ResizeRoom(base0, "i0", 20, 0, 4, 3).Rooms.Select(room => room.Key).SequenceEqual(base0.Rooms.Select(room => room.Key)),
            "intent edits: helpers keep every other field and the list order", "");
    }

    static void Corpus(Action<bool, string, string> check)
    {
        string[] kinds = { "resize_room", "move_opening", "change_wall_type" };
        var accepted = new int[3];
        var refused = new int[3];
        var propagated = new int[3];
        var exact = new int[3];
        int steps = 0, agreed = 0, withCreates = 0, withDeletes = 0, orderOnly = 0, levelRetypes = 0, storedAgreed = 0, storedPropagated = 0, hazards = 0, localityChecks = 0, unrelatedRooms = 0;
        string? firstStoredProblem = null, firstLocalityProblem = null;
        long edits = 0, untouched = 0, totalBefore = 0;
        string? firstProblem = null, firstRefusalMismatch = null;
        foreach (var seed in new[] { 9101, 9102, 9103 })
        {
            var random = new Random(seed);
            var current = Base();
            var stored = IntentCompiler.Compile(ProjectId, Revision, current);
            for (var step = 0; step < 200; step++)
            {
                var kind = random.Next(3);
                var next = Propose(random, current, kind, ref levelRetypes);
                var compiled = Refusal(() => IntentCompiler.Compile(ProjectId, Revision, next));
                if (compiled is not null)
                {
                    refused[kind]++;
                    var mine = Refusal(() => IntentEdits.From(ProjectId, Revision, current, next));
                    if (mine == compiled && Refusal(() => IntentEdits.FromStored(stored, next)) == compiled) propagated[kind]++;
                    else firstRefusalMismatch ??= kinds[kind] + " seed " + N(seed) + " step " + N(step) + ": " + (mine ?? "accepted") + " vs " + compiled;
                    continue;
                }
                steps++;
                accepted[kind]++;
                string? problem;
                Outcome outcome;
                try { problem = Verify(current, next, out outcome); }
                catch (Exception error) { problem = "unexpected " + error.GetType().Name + ": " + error.Message; outcome = new Outcome(0, 0, 0, 0, false); }
                if (problem is null)
                {
                    agreed++;
                    edits += outcome.Edits; untouched += outcome.Untouched;
                    totalBefore += IntentCompiler.Compile(ProjectId, Revision, current).Elements.Length;
                    if (outcome.Creates > 0) withCreates++;
                    if (outcome.Deletes > 0) withDeletes++;
                    if (outcome.ExactOrder) exact[kind]++; else orderOnly++;
                }
                else firstProblem ??= kinds[kind] + " seed " + N(seed) + " step " + N(step) + ": " + problem;
                // The stored chain: diff the stored project (incremental order) against compile(next).
                if (Compiler.Hash(stored) != Compiler.Hash(IntentCompiler.Compile(ProjectId, Revision, current))) hazards++;
                string? storedProblem;
                Project committed;
                try { storedProblem = VerifyStored(stored, next, out committed); }
                catch (Exception error) { storedProblem = "unexpected " + error.GetType().Name + ": " + error.Message; committed = stored; }
                if (storedProblem is null) storedAgreed++;
                else firstStoredProblem ??= kinds[kind] + " seed " + N(seed) + " step " + N(step) + ": " + storedProblem;
                stored = committed;
                if (kind == 0)
                {
                    var changedRoom = current.Rooms.Zip(next.Rooms).FirstOrDefault(pair => Json(pair.First) != Json(pair.Second)).First;
                    if (changedRoom is null) localityChecks++; // a resize that fell back to no change edits nothing
                    else try { unrelatedRooms += Unrelated(current, next, changedRoom.Key, out var locality); if (locality is null) localityChecks++; else firstLocalityProblem ??= locality; }
                    catch (Exception error) { firstLocalityProblem ??= "unexpected " + error.GetType().Name + ": " + error.Message; }
                }
                current = next;
            }
        }
        var counts = string.Join(",", kinds.Select((name, i) => name + "=" + N(accepted[i]) + "/" + N(refused[i])));
        check(steps > 0 && agreed == steps, "intent edits corpus: every accepted change yields the Compiler.Diff edit set and an agreeing incremental plan", N(agreed) + "/" + N(steps) + " " + (firstProblem ?? ""));
        check(accepted.All(count => count >= 25), "intent edits corpus: at least 25 accepted changes of each kind", counts);
        check(refused.All(count => count >= 3) && propagated.SequenceEqual(refused), "intent edits corpus: every refused change propagates the compiler refusal unchanged", counts + " " + (firstRefusalMismatch ?? ""));
        check(exact.All(count => count >= 10), "intent edits corpus: plans byte-identical to Compiler.Diff(compile(before), compile(after)) for each kind", string.Join(",", exact.Select(count => N(count))));
        check(withCreates >= 5 && withDeletes >= 5 && orderOnly >= 1, "intent edits corpus: adjacency changes create and delete walls", N(withCreates) + "/" + N(withDeletes) + "/" + N(orderOnly));
        check(storedAgreed == steps, "intent edits corpus: FromStored on the stored (incremental-order) chain agrees with Compiler.Diff(stored, compile(after)) on every step", N(storedAgreed) + "/" + N(steps) + " " + (firstStoredProblem ?? ""));
        check(hazards >= 1, "intent edits corpus: the stored chain diverges in order from compile(previous) (non-vacuity)", N(hazards));
        check(firstLocalityProblem is null && localityChecks == accepted[0] && unrelatedRooms >= 100, "intent edits corpus: a single-room resize never edits elements of an unrelated room", N(localityChecks) + " steps, " + N(unrelatedRooms) + " rooms " + (firstLocalityProblem ?? ""));
        check(levelRetypes >= 5, "intent edits corpus: whole-level wall type changes are exercised", N(levelRetypes));
        check(untouched > edits, "intent edits corpus: most elements stay out of the edit sets", N(edits) + " edits, " + N(untouched) + " untouched of " + N(totalBefore));
        Console.WriteLine(JsonSerializer.Serialize(new { benchmark = "intent_edits", steps, agreed, accepted, refused, exact_order = exact, order_only = orderOnly, with_creates = withCreates, stored_agreed = storedAgreed, hazards, unrelated_rooms = unrelatedRooms, with_deletes = withDeletes, edits, untouched, total_before = totalBefore }));
    }

    // One random bounded change of the given kind, built with the typed helpers.
    static LayoutIntent Propose(Random random, LayoutIntent intent, int kind, ref int levelRetypes)
    {
        double Step() => random.Next(-2, 3) * 0.5;
        switch (kind)
        {
            case 0:
            {
                var room = intent.Rooms[random.Next(intent.Rooms.Count)];
                for (var attempt = 0; ; attempt++)
                {
                    double left = room.XMetres, right = room.XMetres + room.WidthMetres, bottom = room.YMetres, top = room.YMetres + room.DepthMetres;
                    if (random.Next(5) == 0) { var dx = Step(); var dy = Step(); left += dx; right += dx; bottom += dy; top += dy; }
                    else
                    {
                        if (random.Next(3) == 0) left += Step();
                        if (random.Next(2) == 0) right += Step();
                        if (random.Next(3) == 0) bottom += Step();
                        if (random.Next(2) == 0) top += Step();
                    }
                    var changed = left != room.XMetres || right != room.XMetres + room.WidthMetres || bottom != room.YMetres || top != room.YMetres + room.DepthMetres;
                    if (attempt < 50 && (!changed || right - left < 2.5 || right - left > 7 || top - bottom < 2.5 || top - bottom > 7)) continue;
                    return IntentEdits.ResizeRoom(intent, room.Key, left, bottom, right - left, top - bottom);
                }
            }
            case 1:
            {
                var opening = intent.Openings[random.Next(intent.Openings.Count)];
                IntentHost? host = null;
                if (opening.Host is IntentRoomSide side && random.Next(4) == 0) host = side with { Side = (IntentSide)random.Next(4) };
                return IntentEdits.MoveOpening(intent, opening.Key, random.Next(1, 31) * 0.1, host);
            }
            default:
            {
                var room = intent.Rooms[random.Next(intent.Rooms.Count)];
                var types = intent.WallTypes.Select(type => type.Key).Where(key => key != room.WallType).ToArray();
                var type = types[random.Next(types.Length)];
                if (random.Next(2) == 0) return IntentEdits.ChangeWallType(intent, room.Key, type);
                levelRetypes++;
                foreach (var other in intent.Rooms.Where(candidate => candidate.Level == room.Level).Select(candidate => candidate.Key).ToArray())
                    intent = IntentEdits.ChangeWallType(intent, other, type);
                return intent;
            }
        }
    }
}
