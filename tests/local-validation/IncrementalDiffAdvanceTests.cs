using Bim.Core;
using System.Globalization;
using System.Text.Json;

// P3-06 oracle: IncrementalDiffIndex.Advance(result) returns the index of result.After, equal in every observable
// (BeforeHash, Before, positions, cached element bytes, reverse dependency keys and lists, and the next Diff) to
// IncrementalDiffIndex.Build(result.After), while carrying over the cached bytes of every untouched element by
// reference (only the edited elements' bytes, serialized once by the diff, are new) and refusing a result that
// this index did not issue with IncrementalDiff.StaleResult. The seeded corpora chain Advance over every step, so
// any drift accumulates.
sealed class IncrementalDiffAdvanceOracle(Action<bool, string, string> check)
{
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    static readonly Provenance Probe = new("advance-probe", "unit");
    IncrementalDiffIndex? index;
    int steps, agreed, staleChecks, probes;
    long reused, carried, total;

    static string N(long value) => value.ToString(Invariant);
    static string Json(object value) => JsonSerializer.Serialize(value);

    // First observable difference between two indexes, or null when they agree on all of them.
    public static string? Differ(IncrementalDiffIndex a, IncrementalDiffIndex b, IEnumerable<Guid>? extra = null)
    {
        if (a.BeforeHash != b.BeforeHash) return "BeforeHash " + a.BeforeHash + " vs " + b.BeforeHash;
        if (Json(a.Before) != Json(b.Before)) return "Before";
        var keysA = a.DependentKeys.Order().ToArray();
        var keysB = b.DependentKeys.Order().ToArray();
        if (!keysA.SequenceEqual(keysB)) return "reverse dependency keys " + N(keysA.Length) + " vs " + N(keysB.Length);
        var ids = a.Before.Elements.Select(element => element.Id).Concat(b.Before.Elements.Select(element => element.Id))
            .Concat(keysA).Concat(extra ?? Array.Empty<Guid>()).ToHashSet();
        foreach (var id in ids)
        {
            if (a.PositionOf(id) != b.PositionOf(id)) return "position of " + id + ": " + N(a.PositionOf(id)) + " vs " + N(b.PositionOf(id));
            if (!a.DependentsOf(id).SequenceEqual(b.DependentsOf(id))) return "dependents of " + id;
            var bytesA = a.CachedBytes(id); var bytesB = b.CachedBytes(id);
            if ((bytesA is null) != (bytesB is null) || (bytesA is not null && !bytesA.AsSpan().SequenceEqual(bytesB))) return "cached bytes of " + id;
        }
        return null;
    }

    // Untouched elements share the predecessor's cached byte array; created/updated ones do not.
    public static string? Sharing(IncrementalDiffIndex prior, IncrementalDiffIndex advanced, IReadOnlyList<ElementEdit> edits)
    {
        var edited = edits.Where(edit => edit.Action != "delete").Select(edit => edit.Id).ToHashSet();
        foreach (var element in advanced.Before.Elements)
        {
            var shared = ReferenceEquals(prior.CachedBytes(element.Id), advanced.CachedBytes(element.Id));
            if (edited.Contains(element.Id) == shared) return (shared ? "edited element shares stale bytes: " : "untouched element re-serialized: ") + element.Id;
        }
        return null;
    }

    public static string? Code(Action action) => IncrementalDiffOracle.Code(action);

    public void Observe(string corpus, int step, Project before, Project after)
    {
        steps++;
        var prior = index ??= IncrementalDiffIndex.Build(before);
        var edits = IncrementalDiffOracle.EditsBetween(before, after, step % 3 == 0);
        IncrementalDiffResult result; IncrementalDiffIndex advanced;
        try { result = prior.Diff(edits); advanced = prior.Advance(result); }
        catch (Exception error)
        {
            check(false, corpus + " step " + N(step) + ": chained diff and advance succeed", error.GetType().Name + ": " + error.Message);
            index = null; return;
        }
        var fresh = IncrementalDiffIndex.Build(prior.Before);
        var built = IncrementalDiffIndex.Build(result.After);
        var deleted = edits.Where(edit => edit.Action == "delete").Select(edit => edit.Id).ToArray();
        var probeTarget = result.After.Elements.Length == 0 ? null : result.After.Elements[step % result.After.Elements.Length];
        var probe = probeTarget is null ? Array.Empty<ElementEdit>() : new[] { ElementEdit.Update(probeTarget with { Provenance = Probe }) };
        var failures = new List<string>();
        if (Differ(prior, fresh) is string chained) failures.Add("chained index differs from Build of its Before: " + chained);
        if (Json(result) != Json(fresh.Diff(edits))) failures.Add("diff from the chained index differs from a fresh index (plan, after or work)");
        if (Json(result.Plan) != Json(Compiler.Diff(prior.Before, result.After))) failures.Add("plan differs from Compiler.Diff");
        if (Differ(advanced, built, deleted) is string drift) failures.Add("advanced index differs from Build(result.After): " + drift);
        if (advanced.BeforeHash != result.Plan.AfterHash || advanced.BeforeHash != Compiler.Hash(result.After)) failures.Add("advanced BeforeHash is not the plan AfterHash");
        if (Json(advanced.Diff(probe)) != Json(built.Diff(probe))) failures.Add("next diff (probe) differs between advanced and built index");
        var work = advanced.Advanced;
        if (work is null || work.Carried != result.Work.Serialized || work.Reused + work.Carried != result.After.Elements.Length || work.TotalElements != result.After.Elements.Length)
            failures.Add("advance work counters " + (work is null ? "missing" : Json(work)) + " vs serialized " + N(result.Work.Serialized));
        if (Sharing(prior, advanced, edits) is string sharing) failures.Add(sharing);
        if (failures.Count == 0) agreed++;
        else check(false, corpus + " step " + N(step) + ": advanced index equals Build(result.After)", string.Join("; ", failures));
        probes++;
        if (step % 10 == 0)
        {
            var codes = new[]
            {
                Code(() => fresh.Advance(result)), Code(() => built.Advance(result)), Code(() => advanced.Advance(result)),
                Code(() => prior.Advance(result with { })), Code(() => prior.Advance(new IncrementalDiffResult(result.After, result.Plan, result.Work))),
            };
            if (codes.All(code => code == IncrementalDiff.StaleResult)) staleChecks++;
            else check(false, corpus + " step " + N(step) + ": results not issued by the index are refused as stale", string.Join(" / ", codes));
        }
        if (work is not null) { reused += work.Reused; carried += work.Carried; total += work.TotalElements; }
        index = advanced;
    }

    public void Finish(string corpus, int expectedSteps)
    {
        check(steps == expectedSteps && agreed == expectedSteps, corpus + ": every chained Advance equals Build(result.After) in every observable and in the next diff", N(agreed) + "/" + N(steps));
        check(staleChecks == (expectedSteps + 9) / 10, corpus + ": stale results (other index, successor, copies, fabricated) are refused", N(staleChecks));
        check(probes == expectedSteps && carried > 0 && reused + carried == total && carried * 4 < total, corpus + ": only edited elements carry new bytes; untouched bytes are reused", N(carried) + " carried, " + N(reused) + " reused of " + N(total));
        Console.WriteLine(JsonSerializer.Serialize(new { benchmark = "incremental_advance_" + corpus, steps, reused, carried, total }));
    }

    public static void Units(Action<bool, string, string> check, Func<int, Project> build, Func<int, string, Guid> role, Func<int, Guid> door, Func<int, Guid> window, Func<string, Guid> id, Provenance provenance)
    {
        Element Get(Project project, Guid target) => project.Elements.Single(element => element.Id == target);
        Element Moved(Element wall, double dy) { var g = wall.GeometryMetres; return wall with { GeometryMetres = new[] { g[0], g[1] + dy, g[2], g[3] + dy, g[4], g[5] } }; }

        var basis = build(2);
        var wall = role(0, "south");
        var level = role(0, "level");
        var root = IncrementalDiffIndex.Build(basis);
        check(root.BeforeHash == Compiler.Hash(basis) && root.Advanced is null, "a built index exposes BeforeHash and has no advance work", "");
        check(root.PositionOf(wall) == Array.FindIndex(basis.Elements, element => element.Id == wall) && root.PositionOf(id("p306-absent")) == -1, "positions are exposed (-1 when absent)", "");
        check(root.DependentsOf(wall).SequenceEqual(basis.Elements.Where(element => element.Dependencies.Contains(wall)).Select(element => element.Id)) && root.DependentsOf(id("p306-absent")).Count == 0,
            "reverse dependencies are exposed in project order", "");

        // ---- Hand-written chain: each step's advanced index equals Build(result.After) ----
        var current = root;
        void Step(string name, ElementEdit[] edits)
        {
            var result = current.Diff(edits);
            var advanced = current.Advance(result);
            var built = IncrementalDiffIndex.Build(result.After);
            var drift = Differ(advanced, built, edits.Select(edit => edit.Id));
            var sharing = Sharing(current, advanced, edits);
            var edited = edits.Count(edit => edit.Action != "delete");
            check(drift is null && sharing is null && advanced.Advanced is { } work && work.Carried == edited && work.Reused == result.After.Elements.Length - edited,
                "chain " + name + ": advanced index equals Build(result.After), only edited elements carry new bytes", (drift ?? "") + (sharing ?? "") + " " + Json(advanced.Advanced!));
            current = advanced;
        }
        Step("empty edit set", Array.Empty<ElementEdit>());
        Step("wall move", new[] { ElementEdit.Update(Moved(Get(current.Before, wall), -0.5)) });
        Step("no-op update", new[] { ElementEdit.Update(Get(current.Before, wall) with { GeometryMetres = Get(current.Before, wall).GeometryMetres.ToArray() }) });
        Step("door deleted", new[] { ElementEdit.Delete(door(1)) });
        Step("wall and dependent door created", new[]
        {
            ElementEdit.Create(new Element(id("p306-wall"), "wall", new[] { 0.1, 1.0, 3.1, 1.0, 2.8, 0.2 }, new[] { level }, provenance)),
            ElementEdit.Create(new Element(id("p306-door"), "door", new[] { 0.5, 0.9 }, new[] { id("p306-wall"), level }, provenance)),
        });
        Step("window rehosted on the new wall", new[] { ElementEdit.Update(Get(current.Before, window(0)) with { Dependencies = new[] { id("p306-wall"), level } }) });
        Step("duplicate dependency entries", new[] { ElementEdit.Update(Get(current.Before, door(0)) with { Dependencies = new[] { wall, level, level } }) });
        Step("east wall deleted, space and plan rewired, new door deleted", new[]
        {
            ElementEdit.Delete(role(0, "east")), ElementEdit.Delete(id("p306-door")),
            ElementEdit.Update(Get(current.Before, role(0, "space")) with { Dependencies = Get(current.Before, role(0, "space")).Dependencies.Where(d => d != role(0, "east")).ToArray() }),
            ElementEdit.Update(Get(current.Before, role(0, "plan")) with { Dependencies = Get(current.Before, role(0, "plan")).Dependencies.Where(d => d != role(0, "east")).ToArray() }),
        });
        Step("created wall deleted after its window moves back", new[]
        {
            ElementEdit.Update(Get(current.Before, window(0)) with { Dependencies = new[] { role(0, "north"), level } }), ElementEdit.Delete(id("p306-wall")),
        });
        check(Json(current.Before) == Json(IncrementalDiffIndex.Build(current.Before).Before) && Differ(root, IncrementalDiffIndex.Build(basis)) is null, "advancing never mutates the predecessor index", "");

        // ---- Advance is repeatable and does not consume the result ----
        var move = new[] { ElementEdit.Update(Moved(Get(basis, wall), 0.5)) };
        var once = root.Diff(move);
        var a1 = root.Advance(once); var a2 = root.Advance(once);
        check(Differ(a1, a2) is null && !ReferenceEquals(a1, a2), "advancing one result twice yields equal independent indexes", "");
        var next = a1.Diff(new[] { ElementEdit.Delete(door(0)) });
        check(Json(next.Plan) == Json(Compiler.Diff(once.After, OneDelete(once.After, door(0)))) && next.Plan.BeforeHash == once.Plan.AfterHash,
            "the advanced index's next plan chains on the previous AfterHash and equals Compiler.Diff", "");

        // ---- Stale results: typed refusal ----
        var other = IncrementalDiffIndex.Build(basis);
        check(Code(() => other.Advance(once)) == IncrementalDiff.StaleResult, "a result issued by another index over the same project is edit_stale_result", "");
        check(Code(() => a1.Advance(once)) == IncrementalDiff.StaleResult, "a result already advanced past (given to the successor) is edit_stale_result", "");
        check(Code(() => a1.Advance(next)) is null && Code(() => root.Advance(next)) == IncrementalDiff.StaleResult, "a successor's result is accepted by the successor and refused by its predecessor", "");
        check(Code(() => root.Advance(once with { After = once.After with { Elements = basis.Elements } })) == IncrementalDiff.StaleResult, "a copied or altered result is edit_stale_result", "");
        var plan = Compiler.Diff(basis, once.After);
        check(Code(() => root.Advance(new IncrementalDiffResult(once.After, plan, once.Work))) == IncrementalDiff.StaleResult, "a fabricated result (Compiler.Diff plan) is edit_stale_result", "");
        check(Code(() => root.Advance(null!)) is not null, "a null result is refused", "");

        // ---- Large model: advance reuses all but the edited element's bytes ----
        foreach (var rooms in new[] { 50, 200 })
        {
            var large = build(rooms);
            var index = IncrementalDiffIndex.Build(large);
            var edits = new[] { ElementEdit.Update(Moved(Get(large, role(17, "south")), -0.5)) };
            var result = index.Diff(edits);
            var advanced = index.Advance(result);
            check(advanced.Advanced is { Carried: 1 } work && work.Reused == large.Elements.Length - 1 && Differ(advanced, IncrementalDiffIndex.Build(result.After)) is null && Sharing(index, advanced, edits) is null,
                N(rooms) + "-room model: advance after one wall move reuses " + N(large.Elements.Length - 1) + " cached element bytes and equals Build", Json(advanced.Advanced!));
            Console.WriteLine(JsonSerializer.Serialize(new { benchmark = "incremental_advance_large", rooms, elements = large.Elements.Length, reused = advanced.Advanced!.Reused, carried = advanced.Advanced.Carried }));
        }
    }

    static Project OneDelete(Project project, Guid id) => project with { Elements = project.Elements.Where(element => element.Id != id).ToArray() };
}
