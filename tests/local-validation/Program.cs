using Bim.Core;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
// P3-04 oracle: after a Compiler.Diff only the affected closure (plan.DirtyIds present in after) is
// revalidated, and localized findings plus the prior findings of unaffected elements equal full validation of after.
int passed = 0, failed = 0;
var provenance = new Provenance("local-validation-tests", "unit");
var invariant = CultureInfo.InvariantCulture;

void Check(bool condition, string name, string detail = "")
{
    if (condition) { Console.WriteLine("PASS " + name); passed++; return; }
    Console.WriteLine("::error::local-validation FAIL " + name + (detail.Length > 0 ? " | " + detail : ""));
    failed++;
}
string? Code(Action action)
{
    try { action(); return null; }
    catch (ArgumentException error) { return error.Message; }
    catch (InvalidOperationException error) { return error.Message; }
    catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
}
Guid Id(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes("local-validation:" + name)).Take(16).ToArray());
string Short(Guid? id) => id is Guid value ? value.ToString("N")[..8] : "-";
string ShowIds(IEnumerable<Guid> ids) => string.Join(",", ids.Select(id => Short(id)));
string Show(IEnumerable<ValidationIssue> issues) => "[" + string.Join(";", issues.Select(issue => issue.Code + "@" + Short(issue.Id))) + "]";
string N(int value) => value.ToString(invariant);

var projectId = Id("project");
var upper = Id("level-upper");
Guid RoomOf(int room) => Id("room" + N(room));
Guid Role(int room, string role) => RoomCompiler.Id(RoomOf(room), role);
Guid Door(int room) => Id("door" + N(room));
Guid Window(int room) => Id("window" + N(room));
string[] wallRoles = { "south", "east", "north", "west" };
Guid[] RoomIds(int room) => new[] { "level", "south", "east", "north", "west", "space", "plan", "schedule", "sheet" }.Select(role => Role(room, role)).Append(Door(room)).Append(Window(room)).ToArray();

// Each room: RoomCompiler slice + a door hosted by the south wall and a window hosted by the east wall, both on the room level.
// Room 1's door does not fit its 4.2 m wall (3.5 + 0.9), so the base already has one finding outside room 0.
Project Build(int rooms)
{
    var elements = new List<Element> { new(upper, "level", new[] { 3.0 }, Array.Empty<Guid>(), provenance) };
    for (var room = 0; room < rooms; room++)
    {
        elements.AddRange(RoomCompiler.Compile(projectId, 0, new RoomIntent(RoomOf(room), 4, 3, 2.8, 0.2, room * 10, 0, "m", provenance)).Elements);
        elements.Add(new(Door(room), "door", room == 1 ? new[] { 3.5, 0.9 } : new[] { 1.0, 0.9 }, new[] { Role(room, "south"), Role(room, "level") }, provenance));
        elements.Add(new(Window(room), "window", new[] { 0.5, 1.2 }, new[] { Role(room, "east"), Role(room, "level") }, provenance));
    }
    return new(projectId, 0, elements.ToArray());
}
Project Replace(Project project, Guid id, Func<Element, Element> change) => project with { Elements = project.Elements.Select(element => element.Id == id ? change(element) : element).ToArray() };
Project Remove(Project project, params Guid[] ids) => project with { Elements = project.Elements.Where(element => !ids.Contains(element.Id)).ToArray() };
Project Add(Project project, Element element) => project with { Elements = project.Elements.Append(element).ToArray() };
Element Move(Element wall, double dx, double dy)
{
    var g = wall.GeometryMetres;
    return wall with { GeometryMetres = new[] { g[0] + dx, g[1] + dy, g[2] + dx, g[3] + dy, g[4], g[5] } };
}
bool Has(IEnumerable<ValidationIssue> issues, string code, Guid id) => issues.Any(issue => issue.Code == code && issue.Id == id);

LocalValidationResult Case(string name, Project before, Project after, IEnumerable<Guid> expectedRevalidated)
{
    var plan = Compiler.Diff(before, after);
    var local = LocalValidation.Validate(before, after, plan);
    var full = LocalValidation.ValidateAll(after);
    var merged = LocalValidation.Merge(LocalValidation.ValidateAll(before), local);
    Check(merged.SequenceEqual(full), name + ": localized findings plus prior findings of unaffected elements equal full validation", Show(merged) + " vs " + Show(full));
    var revalidated = local.Revalidated.ToHashSet();
    var restricted = full.Where(issue => issue.Id is Guid id && revalidated.Contains(id)).ToArray();
    Check(local.Findings.SequenceEqual(restricted), name + ": localized findings equal full findings restricted to the revalidated elements", Show(local.Findings) + " vs " + Show(restricted));
    var expected = expectedRevalidated.Order().ToArray();
    Check(local.Revalidated.SequenceEqual(expected), name + ": revalidated elements are exactly the affected closure present in after", ShowIds(local.Revalidated) + " vs " + ShowIds(expected));
    Check(local.Affected.SequenceEqual(plan.DirtyIds), name + ": affected set is the plan dirty closure", ShowIds(local.Affected) + " vs " + ShowIds(plan.DirtyIds));
    Check(local.TotalElements == after.Elements.Length && local.RevalidatedCount == local.Revalidated.Length, name + ": counters report revalidated vs total elements", N(local.RevalidatedCount) + "/" + N(local.TotalElements));
    Check(LocalValidation.Agrees(before, after, plan), name + ": proof helper agrees");
    Check(JsonSerializer.Serialize(LocalValidation.Validate(before, after, plan)) == JsonSerializer.Serialize(local), name + ": localized validation is deterministic");
    return local;
}

try
{
    var basis = Build(2);
    var baseFindings = LocalValidation.ValidateAll(basis);
    Check(basis.Elements.Length == 23, "corpus has two rooms of 11 elements plus a shared upper level", N(basis.Elements.Length));
    Check(baseFindings.Length == 1 && Has(baseFindings, "host_extent", Door(1)),
        "base corpus has exactly one finding: room 1 door overruns its host wall", Show(baseFindings));

    // ---- Rule unit checks on full validation ----
    var single = RoomCompiler.Compile(projectId, 0, new RoomIntent(RoomOf(7), 4, 3, 2.8, 0.2, 1.3, -2.7, "m", provenance));
    Check(LocalValidation.ValidateAll(single).Length == 0, "a compiled room slice is clean (walls touch the space within tolerance)", Show(LocalValidation.ValidateAll(single)));
    var wallA = Role(0, "south"); var wallB = Role(0, "east"); var levelA = Role(0, "level");
    var ambiguous = Replace(basis, Door(0), door => door with { Dependencies = new[] { wallA, wallB, levelA } });
    Check(Has(LocalValidation.ValidateAll(ambiguous), "host_ambiguous", Door(0)), "a door naming two walls is host_ambiguous", Show(LocalValidation.ValidateAll(ambiguous)));
    var orphan = Replace(basis, Door(0), door => door with { Dependencies = new[] { levelA } });
    Check(Has(LocalValidation.ValidateAll(orphan), "host_missing", Door(0)), "a door without a wall is host_missing", Show(LocalValidation.ValidateAll(orphan)));
    var shape = Replace(basis, Door(0), door => door with { GeometryMetres = new[] { 1.0, 0.9, 2.1 } });
    Check(Has(LocalValidation.ValidateAll(shape), "hosted_geometry", Door(0)), "hosted geometry other than [offset, width] is hosted_geometry", Show(LocalValidation.ValidateAll(shape)));
    var negative = Replace(basis, Door(0), door => door with { GeometryMetres = new[] { -0.1, 0.9 } });
    Check(Has(LocalValidation.ValidateAll(negative), "host_extent", Door(0)), "a negative offset is host_extent", Show(LocalValidation.ValidateAll(negative)));
    var flatLevel = Replace(basis, upper, level => level with { GeometryMetres = Array.Empty<double>() });
    Check(Has(LocalValidation.ValidateAll(flatLevel), "level_geometry", upper), "a level without elevation is level_geometry", Show(LocalValidation.ValidateAll(flatLevel)));
    var all = LocalValidation.ValidateAll(Replace(ambiguous, Door(1), door => door with { GeometryMetres = new[] { 9.0, 9.0, 9.0 } }));
    Check(all.SequenceEqual(all.OrderBy(issue => issue.Id).ThenBy(issue => issue.Code, StringComparer.Ordinal).ThenBy(issue => issue.Message, StringComparer.Ordinal)), "findings are ordered by element id, code and message", Show(all));
    Check(Code(() => LocalValidation.ValidateAll(Add(basis, new Element(Id("dangling"), "door", new[] { 1.0, 0.9 }, new[] { Id("nowhere") }, provenance)))) == "invalid_ir", "full validation refuses an IR Compiler.Validate rejects");

    // ---- No-op diff ----
    var noop = Case("no-op diff", basis, basis, Array.Empty<Guid>());
    Check(noop.Findings.Length == 0 && noop.RevalidatedCount == 0 && noop.TotalElements == 23, "no-op diff revalidates nothing");

    // ---- Wall move affecting hosted openings ----
    var shortened = Replace(basis, wallA, wall => { var g = wall.GeometryMetres; return wall with { GeometryMetres = new[] { g[0], g[1], g[0] + 1.5, g[3], g[4], g[5] } }; });
    var southClosure = new[] { wallA, Door(0), Role(0, "space"), Role(0, "plan"), Role(0, "schedule"), Role(0, "sheet") };
    var shortLocal = Case("wall shortened under its door", basis, shortened, southClosure);
    Check(Has(shortLocal.Findings, "host_extent", Door(0)) && shortLocal.RevalidatedCount < shortLocal.TotalElements, "shortened wall: the hosted door overruns it and only 6 of 23 elements are revalidated", Show(shortLocal.Findings));
    var moved = Replace(basis, wallA, wall => Move(wall, 0, -1));
    var movedLocal = Case("wall moved off its space", basis, moved, southClosure);
    Check(Has(movedLocal.Findings, "boundary_detached", Role(0, "space")) && !Has(movedLocal.Findings, "host_extent", Door(0)), "moved wall: the space reports a detached boundary, the door still fits", Show(movedLocal.Findings));

    // ---- Room boundary change ----
    var north = Role(0, "north");
    var raised = Case("north wall raised away from the room", basis, Replace(basis, north, wall => Move(wall, 0, 1)), new[] { north, Role(0, "space"), Role(0, "plan"), Role(0, "schedule"), Role(0, "sheet") });
    Check(Has(raised.Findings, "boundary_detached", Role(0, "space")), "raised north wall: boundary_detached on the space", Show(raised.Findings));
    var narrowed = Case("space narrowed away from the east wall", basis, Replace(basis, Role(0, "space"), space => { var g = space.GeometryMetres; return space with { GeometryMetres = new[] { g[0], g[1], g[2] - 1, g[3] } }; }),
        new[] { Role(0, "space"), Role(0, "plan"), Role(0, "schedule"), Role(0, "sheet") });
    Check(Has(narrowed.Findings, "boundary_detached", Role(0, "space")), "narrowed space: boundary_detached", Show(narrowed.Findings));

    // ---- Delete with dependents ----
    var withoutEast = Remove(basis, wallB, Window(0));
    withoutEast = Replace(withoutEast, Role(0, "space"), space => space with { Dependencies = space.Dependencies.Where(id => id != wallB).ToArray() });
    withoutEast = Replace(withoutEast, Role(0, "plan"), plan => plan with { Dependencies = plan.Dependencies.Where(id => id != wallB).ToArray() });
    var deleted = Case("east wall and its window deleted", basis, withoutEast, new[] { Role(0, "space"), Role(0, "plan"), Role(0, "schedule"), Role(0, "sheet") });
    Check(deleted.Affected.Contains(wallB) && deleted.Affected.Contains(Window(0)) && !deleted.Revalidated.Contains(wallB), "deleted elements are affected but not revalidated");
    var rehosted = Remove(basis, wallB);
    rehosted = Replace(rehosted, Role(0, "space"), space => space with { Dependencies = space.Dependencies.Where(id => id != wallB).ToArray() });
    rehosted = Replace(rehosted, Role(0, "plan"), plan => plan with { Dependencies = plan.Dependencies.Where(id => id != wallB).ToArray() });
    rehosted = Replace(rehosted, Window(0), window => window with { GeometryMetres = new[] { 3.9, 1.2 }, Dependencies = new[] { north, levelA } });
    var rehostedLocal = Case("east wall deleted, window rehosted on the north wall", basis, rehosted, new[] { Window(0), Role(0, "space"), Role(0, "plan"), Role(0, "schedule"), Role(0, "sheet") });
    Check(Has(rehostedLocal.Findings, "host_extent", Window(0)), "rehosted window overruns the north wall", Show(rehostedLocal.Findings));
    var fixedByDelete = Case("deleting the faulty door drops its prior finding", basis, Remove(basis, Door(1)), Array.Empty<Guid>());
    Check(fixedByDelete.Affected.SequenceEqual(new[] { Door(1) }) && LocalValidation.Merge(baseFindings, fixedByDelete).Length == 0, "prior finding of a deleted element is not carried over");

    // ---- Level change ----
    var relevelled = Case("south wall moved to the upper level", basis, Replace(basis, wallA, wall => wall with { Dependencies = new[] { upper } }), southClosure);
    Check(Has(relevelled.Findings, "host_level", Door(0)) && Has(relevelled.Findings, "boundary_level", Role(0, "space")), "level change: door level differs from host, space walls on different levels", Show(relevelled.Findings));
    var raisedLevel = Case("room level elevation changed", basis, Replace(basis, levelA, level => level with { GeometryMetres = new[] { 0.5 } }), RoomIds(0));
    Check(raisedLevel.RevalidatedCount == 11 && raisedLevel.Findings.Length == 0, "level elevation change revalidates the 11 room elements only", N(raisedLevel.RevalidatedCount));

    // ---- Fix and create ----
    var repaired = Case("faulty door repaired", basis, Replace(basis, Door(1), door => door with { GeometryMetres = new[] { 1.0, 0.9 } }), new[] { Door(1) });
    Check(LocalValidation.Merge(baseFindings, repaired).Length == 0, "repairing the door clears the only finding");
    var newDoor = Id("new-door"); var lonely = Id("lonely-door");
    var created = Add(Add(basis, new Element(newDoor, "door", new[] { 5.0, 1.0 }, new[] { Role(1, "north"), Role(1, "level") }, provenance)),
        new Element(lonely, "door", new[] { 0.0, 1.0 }, new[] { Role(1, "level") }, provenance));
    var createdLocal = Case("doors created", basis, created, new[] { newDoor, lonely });
    Check(Has(createdLocal.Findings, "host_extent", newDoor) && Has(createdLocal.Findings, "host_missing", lonely), "created doors are validated", Show(createdLocal.Findings));

    // ---- Combined edit across both rooms ----
    var combined = Replace(Replace(shortened, Door(1), door => door with { GeometryMetres = new[] { 0.2, 0.9 } }), Role(1, "north"), wall => Move(wall, 0, 2));
    Case("combined edit across two rooms", basis, combined, southClosure.Concat(new[] { Door(1), Role(1, "north"), Role(1, "space"), Role(1, "plan"), Role(1, "schedule"), Role(1, "sheet") }));

    // ---- The proof is not vacuous ----
    var shortPlan = Compiler.Diff(basis, shortened);
    var nothing = new LocalValidationResult(Array.Empty<ValidationIssue>(), Array.Empty<Guid>(), Array.Empty<Guid>(), shortened.Elements.Length);
    Check(!LocalValidation.Merge(baseFindings, nothing).SequenceEqual(LocalValidation.ValidateAll(shortened)), "skipping revalidation would disagree with full validation (proof is not vacuous)");
    Check(Code(() => LocalValidation.Validate(basis, shortened, Compiler.Diff(basis, moved))) == "plan_mismatch", "a plan that is not the deterministic diff is refused");
    Check(Code(() => LocalValidation.Agrees(basis, moved, shortPlan)) == "plan_mismatch", "the proof helper refuses a mismatched plan");

    // ---- Seeded random corpus: localized == full after every edit ----
    var random = new Random(20261103);
    var current = Build(12);
    long revalidatedSum = 0, totalSum = 0; var edits = 0; var agreed = 0; var local1 = 0;
    for (var step = 0; step < 300; step++)
    {
        var room = random.Next(12);
        var next = random.Next(6) switch
        {
            0 => Replace(current, Role(room, wallRoles[random.Next(4)]), wall => Move(wall, random.Next(-2, 3) * 0.5, random.Next(-2, 3) * 0.5)),
            1 => current.Elements.Any(element => element.Id == Door(room)) ? Replace(current, Door(room), door => door with { GeometryMetres = new[] { random.Next(0, 10) * 0.5, 0.9 } }) : current,
            2 => Replace(current, Role(room, wallRoles[random.Next(4)]), wall => wall with { Dependencies = new[] { wall.Dependencies[0] == upper ? Role(room, "level") : upper } }),
            3 => Replace(current, Role(room, "space"), space => { var g = space.GeometryMetres; return space with { GeometryMetres = new[] { g[0], g[1], 4 + random.Next(-2, 3) * 0.5, g[3] } }; }),
            4 => Replace(current, Role(room, "level"), level => level with { GeometryMetres = new[] { (double)random.Next(0, 4) } }),
            _ => current.Elements.Any(element => element.Id == Door(room)) ? Remove(current, Door(room))
                : Add(current, new Element(Door(room), "door", new[] { 1.0, 0.9 }, new[] { Role(room, "south"), Role(room, "level") }, provenance)),
        };
        var plan = Compiler.Diff(current, next);
        var result = LocalValidation.Validate(current, next, plan);
        var merged = LocalValidation.Merge(LocalValidation.ValidateAll(current), result);
        var full = LocalValidation.ValidateAll(next);
        edits++;
        if (merged.SequenceEqual(full) && LocalValidation.Agrees(current, next, plan)) agreed++;
        else Check(false, "random edit " + N(step) + " agrees with full validation", Show(merged) + " vs " + Show(full));
        if (result.RevalidatedCount <= 11) local1++;
        revalidatedSum += result.RevalidatedCount; totalSum += result.TotalElements;
        current = next;
    }
    Check(agreed == 300, "300 seeded random edits: localized findings always equal full validation", N(agreed));
    Check(local1 == 300 && revalidatedSum < totalSum / 10, "every single-room edit revalidates at most that room's 11 elements", N(local1) + " " + revalidatedSum.ToString(invariant) + "/" + totalSum.ToString(invariant));

    // ---- Benchmark-ish deterministic counter ----
    var large = Build(50);
    var largeAfter = Replace(large, Role(17, "south"), wall => Move(wall, 0, -0.5));
    var largeResult = LocalValidation.Validate(large, largeAfter, Compiler.Diff(large, largeAfter));
    Check(largeResult.RevalidatedCount == 6 && largeResult.TotalElements == 551, "50-room model: one wall move revalidates 6 of 551 elements", N(largeResult.RevalidatedCount) + "/" + N(largeResult.TotalElements));
    Check(LocalValidation.Agrees(large, largeAfter, Compiler.Diff(large, largeAfter)), "50-room model: localized result equals full validation");
    Console.WriteLine(JsonSerializer.Serialize(new { benchmark = "local_validation", revalidated = largeResult.RevalidatedCount, total = largeResult.TotalElements, random_edits = edits, random_revalidated = revalidatedSum, random_total = totalSum }));
}
catch (Exception error)
{
    Console.WriteLine("::error::local-validation FAIL unexpected " + error.GetType().Name + " | " + error.Message);
    failed++;
}

Console.WriteLine(JsonSerializer.Serialize(new { passed, failed, local_validation_contract = failed == 0 }));
Environment.ExitCode = failed == 0 && passed > 0 ? 0 : 1;
