using Bim.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;

// P3-05 oracle: an incremental diff from a bounded edit set equals Compiler.Diff(before, after) byte for byte
// (operations, hashes and DirtyIds) while serializing only edited elements and visiting only the dirty closure.
// The reference "after" is the naive application of the edit set (updates in place, deletes removed, creates
// appended in edit order); the seeded corpora also prove that reference equals the corpus state as an element set.
sealed class IncrementalDiffOracle(Action<bool, string, string> check)
{
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    int steps, agreed, exactOrder, boundChecks, creates, deletes, noOps, injected;
    long visited, serialized, dirty, total, traversed;

    static string N(long value) => value.ToString(Invariant);
    static string Json(object value) => JsonSerializer.Serialize(value);

    public static string? Code(Action action)
    {
        try { action(); return null; }
        catch (ArgumentException error) { return error.Message; }
        catch (InvalidOperationException error) { return error.Message; }
        catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
    }

    // Test-side naive application of an edit set; the meaning the incremental path must reproduce.
    public static Project Apply(Project before, IReadOnlyList<ElementEdit> edits)
    {
        var updates = edits.Where(edit => edit.Action == "update").ToDictionary(edit => edit.Id, edit => edit.Element!);
        var removed = edits.Where(edit => edit.Action == "delete").Select(edit => edit.Id).ToHashSet();
        return before with
        {
            Elements = before.Elements.Where(element => !removed.Contains(element.Id))
                .Select(element => updates.TryGetValue(element.Id, out var update) ? update : element)
                .Concat(edits.Where(edit => edit.Action == "create").Select(edit => edit.Element!)).ToArray()
        };
    }

    // Edit set between two corpus states, deliberately out of project order (deletes, then updates reversed, then
    // creates), optionally with one no-op update of an untouched element.
    public static ElementEdit[] EditsBetween(Project before, Project after, bool withNoOp)
    {
        var old = before.Elements.ToDictionary(element => element.Id);
        var next = after.Elements.Select(element => element.Id).ToHashSet();
        var deletes = before.Elements.Where(element => !next.Contains(element.Id)).Select(element => ElementEdit.Delete(element.Id));
        var changed = after.Elements.Where(element => old.TryGetValue(element.Id, out var prior) && Json(prior) != Json(element)).ToArray();
        var updates = Enumerable.Reverse(changed).Select(ElementEdit.Update);
        var created = after.Elements.Where(element => !old.ContainsKey(element.Id)).Select(ElementEdit.Create);
        var edits = deletes.Concat(updates).Concat(created).ToList();
        if (withNoOp)
        {
            var touched = edits.Select(edit => edit.Id).ToHashSet();
            var untouched = after.Elements.FirstOrDefault(element => !touched.Contains(element.Id) && old.ContainsKey(element.Id));
            if (untouched is not null) edits.Insert(edits.Count / 2, ElementEdit.Update(untouched with { GeometryMetres = untouched.GeometryMetres.ToArray() }));
        }
        return edits.ToArray();
    }

    // Independent recount of the reverse-dependency and dependency entries the incremental path must examine:
    // before-dependents of every deleted element (dangling check) and of every dirty element (closure), plus the
    // after-dependencies of every dirty element still present (cycle check). Duplicate entries count each time.
    public static long ExpectedTraversed(Project before, Project after, IReadOnlyList<ElementEdit> edits, IEnumerable<Guid> dirtyIds)
    {
        var reverse = new Dictionary<Guid, int>();
        foreach (var element in before.Elements)
            foreach (var dependency in element.Dependencies) reverse[dependency] = reverse.GetValueOrDefault(dependency) + 1;
        var afterById = after.Elements.ToDictionary(element => element.Id);
        long count = edits.Where(edit => edit.Action == "delete").Sum(edit => (long)reverse.GetValueOrDefault(edit.Id));
        foreach (var id in dirtyIds)
        {
            count += reverse.GetValueOrDefault(id);
            if (afterById.TryGetValue(id, out var element)) count += element.Dependencies.Length;
        }
        return count;
    }

    // Seeded invalid edit appended to a valid set (cycle, dangling dependency or NaN, rotating): the incremental
    // refusal must be the code Compiler.Diff gives, unbounded and under a zero affected bound (invalid_ir wins).
    void Inject(string corpus, int step, Project before, ElementEdit[] edits)
    {
        var touched = edits.Select(edit => edit.Id).ToHashSet();
        var provenance = before.Elements[0].Provenance;
        Guid Fresh(int salt) => Guid.Parse("00000000-0000-0000-" + salt.ToString("D4", Invariant) + "-" + step.ToString("D12", Invariant));
        ElementEdit? bad = null;
        switch ((step / 5) % 3)
        {
            case 0:
                foreach (var x in before.Elements.Where(element => !touched.Contains(element.Id)))
                    if (before.Elements.FirstOrDefault(element => !touched.Contains(element.Id) && element.Dependencies.Contains(x.Id)) is Element y)
                    { bad = ElementEdit.Update(x with { Dependencies = x.Dependencies.Append(y.Id).ToArray() }); break; }
                break;
            case 1:
                bad = ElementEdit.Create(new Element(Fresh(1), "door", new[] { 1.0, 0.9 }, new[] { Fresh(2) }, provenance));
                break;
            default:
                if (before.Elements.FirstOrDefault(element => !touched.Contains(element.Id)) is Element target)
                    bad = ElementEdit.Update(target with { GeometryMetres = target.GeometryMetres.Select(_ => double.NaN).Append(double.NaN).ToArray() });
                break;
        }
        if (bad is null) { check(false, corpus + " step " + N(step) + ": an invalid edit could be injected", ""); return; }
        var invalid = edits.Append(bad).ToArray();
        var full = Code(() => Compiler.Diff(before, Apply(before, invalid)));
        var index = IncrementalDiffIndex.Build(before);
        var local = Code(() => index.Diff(invalid));
        var tight = Code(() => index.Diff(invalid, 0));
        if (full == "invalid_ir" && local == full && tight == full) injected++;
        else check(false, corpus + " step " + N(step) + ": injected invalid edit is refused like Compiler.Diff, also under a zero bound", full + " / " + local + " / " + tight);
    }

    static bool SameContent(Project a, Project b) =>
        a.Id == b.Id && a.Revision == b.Revision &&
        a.Elements.OrderBy(element => element.Id).Select(Json).SequenceEqual(b.Elements.OrderBy(element => element.Id).Select(Json));

    // One corpus step: derive the edit set, compare the incremental result with the full path.
    public void Observe(string corpus, int step, Project before, Project after)
    {
        steps++;
        var edits = EditsBetween(before, after, step % 3 == 0);
        creates += edits.Count(edit => edit.Action == "create");
        deletes += edits.Count(edit => edit.Action == "delete");
        var reference = Apply(before, edits);
        var expected = Compiler.Diff(before, reference);
        IncrementalDiffResult result;
        try { result = IncrementalDiffIndex.Build(before).Diff(edits); }
        catch (Exception error) { check(false, corpus + " step " + N(step) + ": incremental diff succeeds", error.GetType().Name + ": " + error.Message); return; }
        var ok = SameContent(reference, after)
            && Json(result.Plan) == Json(expected)
            && Json(result.After) == Json(reference)
            && result.Work.Serialized <= edits.Count(edit => edit.Action != "delete")
            && result.Work.Visited <= expected.DirtyIds.Length
            && result.Work.Edits == edits.Length
            && result.Work.TotalElements == before.Elements.Length
            && result.Work.HashedBytes == Encoding.UTF8.GetByteCount(Json(reference))
            && result.Work.Traversed == ExpectedTraversed(before, reference, edits, expected.DirtyIds);
        if (ok) agreed++;
        else check(false, corpus + " step " + N(step) + ": incremental plan equals Compiler.Diff and work stays within the dirty closure",
            "traversed " + N(result.Work.Traversed) + " expected " + N(ExpectedTraversed(before, reference, edits, expected.DirtyIds)) + " visited " + N(result.Work.Visited) + " dirty " + N(expected.DirtyIds.Length) + " serialized " + N(result.Work.Serialized) + " same " + SameContent(reference, after));
        if (Json(reference) == Json(after))
        {
            if (Json(result.Plan) == Json(Compiler.Diff(before, after))) exactOrder++;
            else check(false, corpus + " step " + N(step) + ": incremental plan equals Compiler.Diff of the corpus state itself", "");
        }
        if (edits.Any(edit => edit.Action == "update" && !expected.Operations.Any(operation => operation.Element.Id == edit.Id))) noOps++;
        if (expected.DirtyIds.Length > 0)
        {
            boundChecks++;
            var index = IncrementalDiffIndex.Build(before);
            var refused = Code(() => index.Diff(edits, expected.DirtyIds.Length - 1));
            var accepted = Code(() => index.Diff(edits, expected.DirtyIds.Length));
            if (refused != IncrementalDiff.BoundExceeded || accepted is not null)
                check(false, corpus + " step " + N(step) + ": the affected bound is exactly the dirty closure", refused + " / " + accepted);
        }
        if (step % 5 == 0) Inject(corpus, step, before, edits);
        traversed += result.Work.Traversed;
        visited += result.Work.Visited; serialized += result.Work.Serialized; dirty += expected.DirtyIds.Length; total += result.Work.TotalElements;
    }

    public void Finish(string corpus, int expectedSteps)
    {
        check(steps == expectedSteps && agreed == expectedSteps, corpus + ": every seeded step's incremental plan equals Compiler.Diff (ops, hashes, DirtyIds)", N(agreed) + "/" + N(steps));
        check(exactOrder > 0 && boundChecks > 0, corpus + ": exact-order steps and affected-bound checks were exercised", N(exactOrder) + " " + N(boundChecks));
        check(noOps > 0, corpus + ": no-op updates were exercised and produce no operation", N(noOps));
        check(creates > 0 && deletes > 0, corpus + ": creates and deletes were exercised", N(creates) + " " + N(deletes));
        check(injected == (expectedSteps + 4) / 5, corpus + ": every injected invalid edit (cycle, dangling, NaN) is refused like Compiler.Diff", N(injected));
        check(traversed < total, corpus + ": traversed entries stay below project size summed over steps", N(traversed) + " of " + N(total));
        check(visited <= dirty && visited < total / 2, corpus + ": visited elements are bounded by the dirty closure and stay local on average", N(visited) + " <= " + N(dirty) + " of " + N(total));
        Console.WriteLine(JsonSerializer.Serialize(new { benchmark = "incremental_diff_" + corpus, steps, visited, traversed, serialized, dirty, total, creates, deletes, no_ops = noOps, exact_order = exactOrder }));
    }

    // Unit, refusal and locality cases over the P3-04 room corpus.
    public static void Units(Action<bool, string, string> check, Func<int, Project> build, Func<int, string, Guid> role, Func<int, Guid> door, Func<int, Guid> window, Func<string, Guid> id, Provenance provenance)
    {
        Element Get(Project project, Guid target) => project.Elements.Single(element => element.Id == target);
        Element Moved(Element wall, double dy) { var g = wall.GeometryMetres; return wall with { GeometryMetres = new[] { g[0], g[1] + dy, g[2], g[3] + dy, g[4], g[5] } }; }
        void Same(string name, Project before, ElementEdit[] edits)
        {
            var reference = Apply(before, edits);
            string? error = null; IncrementalDiffResult? result = null;
            try { result = IncrementalDiff.Diff(before, edits); } catch (Exception failure) { error = failure.GetType().Name + ": " + failure.Message; }
            check(result is not null && Json(result.Plan) == Json(Compiler.Diff(before, reference)) && Json(result.After) == Json(reference),
                name + ": incremental plan and after equal Compiler.Diff of the naive application", error ?? "");
        }

        var basis = build(2);
        var wall = role(0, "south");
        var level = role(0, "level");

        // ---- Equality on hand-written edit sets ----
        Same("empty edit set", basis, Array.Empty<ElementEdit>());
        Same("single wall move", basis, new[] { ElementEdit.Update(Moved(Get(basis, wall), -0.5)) });
        Same("door deleted", basis, new[] { ElementEdit.Delete(door(1)) });
        Same("door created", basis, new[] { ElementEdit.Create(new Element(id("p305-door"), "door", new[] { 1.0, 0.9 }, new[] { role(1, "north"), role(1, "level") }, provenance)) });
        Same("create then dependent create in one set", basis, new[]
        {
            ElementEdit.Create(new Element(id("p305-wall"), "wall", new[] { 0.1, 1.0, 3.1, 1.0, 2.8, 0.2 }, new[] { level }, provenance)),
            ElementEdit.Create(new Element(id("p305-door2"), "door", new[] { 0.5, 0.9 }, new[] { id("p305-wall"), level }, provenance)),
        });
        Same("wall and its window deleted, space and plan rewired", basis, new[]
        {
            ElementEdit.Delete(window(0)), ElementEdit.Delete(role(0, "east")),
            ElementEdit.Update(Get(basis, role(0, "space")) with { Dependencies = Get(basis, role(0, "space")).Dependencies.Where(d => d != role(0, "east")).ToArray() }),
            ElementEdit.Update(Get(basis, role(0, "plan")) with { Dependencies = Get(basis, role(0, "plan")).Dependencies.Where(d => d != role(0, "east")).ToArray() }),
        });
        Same("no-op update", basis, new[] { ElementEdit.Update(Get(basis, wall) with { GeometryMetres = Get(basis, wall).GeometryMetres.ToArray() }) });
        Same("updates across two rooms in reverse order", basis, new[] { ElementEdit.Update(Moved(Get(basis, role(1, "north")), 1)), ElementEdit.Update(Moved(Get(basis, wall), -1)) });

        var noop = IncrementalDiff.Diff(basis, Array.Empty<ElementEdit>());
        check(noop.Plan.Operations.Length == 0 && noop.Plan.DirtyIds.Length == 0 && noop.Work.Visited == 0 && noop.Work.Traversed == 0 && noop.Work.Serialized == 0, "empty edit set visits and serializes nothing", Json(noop.Work));
        check(noop.Plan.BeforeHash == Compiler.Hash(basis) && noop.Plan.AfterHash == Compiler.Hash(basis), "empty edit set keeps both hashes equal to Compiler.Hash", "");
        var unchanged = IncrementalDiff.Diff(basis, new[] { ElementEdit.Update(Get(basis, wall) with { GeometryMetres = Get(basis, wall).GeometryMetres.ToArray() }) });
        check(unchanged.Plan.Operations.Length == 0 && unchanged.Plan.DirtyIds.Length == 0 && unchanged.Work.Serialized == 1, "a no-op update serializes only the edited element and dirties nothing", Json(unchanged.Work));

        // ---- Index reuse and determinism ----
        var shared = IncrementalDiffIndex.Build(basis);
        var first = shared.Diff(new[] { ElementEdit.Update(Moved(Get(basis, wall), -0.5)) });
        var second = shared.Diff(new[] { ElementEdit.Delete(door(1)) });
        var again = shared.Diff(new[] { ElementEdit.Update(Moved(Get(basis, wall), -0.5)) });
        check(Json(first) == Json(again) && Json(second.Plan) == Json(Compiler.Diff(basis, Apply(basis, new[] { ElementEdit.Delete(door(1)) }))), "an index is reusable and its diffs are deterministic", "");
        check(Json(shared.Before) == Json(basis), "diffing never mutates the indexed project", "");
        var compatible = LocalValidation.Validate(basis, first.After, first.Plan);
        check(compatible.Affected.SequenceEqual(first.Plan.DirtyIds), "the incremental plan is accepted by P3-04 localized validation (no plan_mismatch)", "");

        // ---- Typed refusals ----
        var stranger = new Element(id("p305-stranger"), "door", new[] { 1.0, 0.9 }, new[] { wall, level }, provenance);
        check(Code(() => IncrementalDiff.Diff(basis, new[] { ElementEdit.Update(stranger) })) == IncrementalDiff.UnknownId, "update of an unknown id is edit_unknown_id", "");
        check(Code(() => IncrementalDiff.Diff(basis, new[] { ElementEdit.Delete(stranger.Id) })) == IncrementalDiff.UnknownId, "delete of an unknown id is edit_unknown_id", "");
        check(Code(() => IncrementalDiff.Diff(basis, new[] { ElementEdit.Create(Get(basis, wall)) })) == IncrementalDiff.CreateExists, "create of an existing id is edit_create_exists", "");
        check(Code(() => IncrementalDiff.Diff(basis, new[] { ElementEdit.Update(Moved(Get(basis, wall), 1)), ElementEdit.Delete(wall) })) == IncrementalDiff.DuplicateId, "two edits of one id are edit_duplicate_id", "");
        check(Code(() => IncrementalDiff.Diff(basis, new[] { ElementEdit.Delete(door(0)), ElementEdit.Create(Get(basis, door(0))) })) == IncrementalDiff.DuplicateId, "delete and re-create of one id is edit_duplicate_id", "");
        var many = Enumerable.Range(0, IncrementalDiff.MaxEdits + 1).Select(i => ElementEdit.Delete(id("p305-many-" + i.ToString(Invariant)))).ToArray();
        check(Code(() => IncrementalDiff.Diff(basis, many)) == IncrementalDiff.BoundExceeded, "more than MaxEdits edits is edit_bound_exceeded", "");
        check(Code(() => IncrementalDiff.Diff(basis, new[] { ElementEdit.Update(Get(basis, level) with { GeometryMetres = new[] { 0.5 } }) }, 10)) == IncrementalDiff.BoundExceeded, "an affected closure larger than the bound is edit_bound_exceeded (11 > 10)", "");
        check(Code(() => IncrementalDiff.Diff(basis, new[] { ElementEdit.Update(Get(basis, level) with { GeometryMetres = new[] { 0.5 } }) }, 11)) is null, "an affected closure equal to the bound is accepted", "");
        check(Code(() => IncrementalDiff.Diff(basis, new[] { ElementEdit.Update(Get(basis, level)) }, -1)) == IncrementalDiff.InvalidEdit, "a negative affected bound is edit_invalid", "");
        check(Code(() => IncrementalDiff.Diff(basis, new[] { new ElementEdit("move", wall, Get(basis, wall)) })) == IncrementalDiff.InvalidEdit, "an unknown action is edit_invalid", "");
        check(Code(() => IncrementalDiff.Diff(basis, new[] { new ElementEdit("update", wall, null) })) == IncrementalDiff.InvalidEdit, "an update without an element is edit_invalid", "");
        check(Code(() => IncrementalDiff.Diff(basis, new[] { new ElementEdit("update", wall, Get(basis, level)) })) == IncrementalDiff.InvalidEdit, "an edit whose element id differs from its target is edit_invalid", "");
        check(Code(() => IncrementalDiff.Diff(basis, new[] { new ElementEdit("delete", wall, Get(basis, wall)) })) == IncrementalDiff.InvalidEdit, "a delete carrying an element is edit_invalid", "");
        check(Code(() => IncrementalDiff.Diff(basis, new ElementEdit[] { null! })) == IncrementalDiff.InvalidEdit, "a null edit is edit_invalid", "");

        // ---- Invalid resulting IR: same invalid_ir refusal as Compiler.Diff on the naive after ----
        void InvalidIr(string name, ElementEdit[] edits, int maxAffected = int.MaxValue)
        {
            var full = Code(() => Compiler.Diff(basis, Apply(basis, edits)));
            var local = Code(() => IncrementalDiff.Diff(basis, edits, maxAffected));
            check(full == "invalid_ir" && local == "invalid_ir", name + ": refused as invalid_ir like Compiler.Diff", full + " / " + local);
        }
        InvalidIr("deleting a wall that still hosts a door", new[] { ElementEdit.Delete(wall) });
        InvalidIr("creating an element with a dangling dependency", new[] { ElementEdit.Create(new Element(id("p305-dangling"), "door", new[] { 1.0, 0.9 }, new[] { id("p305-nowhere") }, provenance)) });
        InvalidIr("updating onto a deleted dependency", new[] { ElementEdit.Delete(window(0)), ElementEdit.Update(Get(basis, door(0)) with { Dependencies = new[] { window(0), level } }) });
        InvalidIr("a level made to depend on its own wall (cycle)", new[] { ElementEdit.Update(Get(basis, level) with { Dependencies = new[] { wall } }) });
        InvalidIr("cycle under tight bound (11-element closure, maxAffected 3)", new[] { ElementEdit.Update(Get(basis, level) with { Dependencies = new[] { wall } }) }, 3);
        InvalidIr("a two-element cycle among created elements", new[]
        {
            ElementEdit.Create(new Element(id("p305-a"), "datum", Array.Empty<double>(), new[] { id("p305-b") }, provenance)),
            ElementEdit.Create(new Element(id("p305-b"), "datum", Array.Empty<double>(), new[] { id("p305-a") }, provenance)),
        });
        InvalidIr("a self dependency", new[] { ElementEdit.Update(Get(basis, door(0)) with { Dependencies = new[] { door(0) } }) });
        InvalidIr("non-finite geometry", new[] { ElementEdit.Update(Get(basis, door(0)) with { GeometryMetres = new[] { double.NaN, 0.9 } }) });
        InvalidIr("invalid wall geometry", new[] { ElementEdit.Update(Get(basis, wall) with { GeometryMetres = new[] { 0.0, 0.0, 0.0, 0.0, 2.8, 0.2 } }) });
        var invalidBase = basis with { Elements = basis.Elements.Append(new Element(id("p305-bad"), "door", new[] { 1.0 }, new[] { id("p305-nowhere") }, provenance)).ToArray() };
        check(Code(() => IncrementalDiffIndex.Build(invalidBase)) == "invalid_ir", "indexing a project Compiler.Validate rejects is invalid_ir", "");

        // ---- Locality: work is bounded by edited + dependents and independent of project size ----
        long? wallMoveTraversed = null;
        foreach (var rooms in new[] { 50, 200 })
        {
            var large = build(rooms);
            var size = large.Elements.Length;
            var index = IncrementalDiffIndex.Build(large);
            var move = new[] { ElementEdit.Update(Moved(Get(large, role(17, "south")), -0.5)) };
            var moved = index.Diff(move);
            check(Json(moved.Plan) == Json(Compiler.Diff(large, Apply(large, move))), N(rooms) + "-room model: one wall move equals Compiler.Diff", "");
            check(moved.Work.Visited == 6 && moved.Work.Serialized == 1 && moved.Work.TotalElements == size && moved.Plan.DirtyIds.Length == 6,
                N(rooms) + "-room model: one wall move visits 6 and serializes 1 of " + N(size) + " elements", Json(moved.Work));
            check(moved.Work.Traversed == ExpectedTraversed(large, moved.After, move, moved.Plan.DirtyIds) && moved.Work.Traversed > 0 && moved.Work.Traversed * 20 < size,
                N(rooms) + "-room model: one wall move examines only the dependency entries around its 6-element closure", Json(moved.Work));
            check(wallMoveTraversed is null || wallMoveTraversed == moved.Work.Traversed, N(rooms) + "-room model: traversed entries for one wall move do not grow with project size", N(wallMoveTraversed ?? -1) + " vs " + N(moved.Work.Traversed));
            wallMoveTraversed = moved.Work.Traversed;
            var relevel = new[] { ElementEdit.Update(Get(large, role(17, "level")) with { GeometryMetres = new[] { 0.5 } }), ElementEdit.Delete(door(3)) };
            var levelled = index.Diff(relevel);
            check(Json(levelled.Plan) == Json(Compiler.Diff(large, Apply(large, relevel))) && levelled.Work.Visited == 12 && levelled.Work.Serialized == 1,
                N(rooms) + "-room model: level change plus door delete visits 11 + 1 and serializes 1", Json(levelled.Work));
            check(moved.Work.HashedBytes == Encoding.UTF8.GetByteCount(Json(Apply(large, move))), N(rooms) + "-room model: hashed bytes counter is the after JSON length (hashing stays linear, serialization does not)", Json(moved.Work));
            Console.WriteLine(JsonSerializer.Serialize(new { benchmark = "incremental_diff_large", rooms, elements = size, visited = moved.Work.Visited, traversed = moved.Work.Traversed, serialized = moved.Work.Serialized, hashed_bytes = moved.Work.HashedBytes }));
        }
    }
}
