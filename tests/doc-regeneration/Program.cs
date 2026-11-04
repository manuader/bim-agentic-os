using Bim.Core;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
// P4-03 oracle: a documentation set (views, tags, dimensions, schedules, sheets and sheet placements bound to BIM-IR
// ids) plus a Compiler.Diff regenerates only the items whose referenced elements are in the plan's dirty closure (or
// whose view, schedule or sheet is regenerated), each command byte-identical to full regeneration of after, and the
// prior full regeneration with those items replaced equals full regeneration of after.
int passed = 0, failed = 0;
var provenance = new Provenance("doc-regeneration-tests", "unit");
var invariant = CultureInfo.InvariantCulture;
var document = new RevitDocumentRef(@"C:\Repo\.bim\native-runs\p4-03\copy.rvt", "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b");

void Check(bool condition, string name, string detail = "")
{
    if (condition) { Console.WriteLine("PASS " + name); passed++; return; }
    Console.WriteLine("::error::doc-regeneration FAIL " + name + (detail.Length > 0 ? " | " + detail : ""));
    failed++;
}
string? Code(Action action)
{
    try { action(); return null; }
    catch (RevitCommandException error) { return error.Code; }
    catch (ArgumentException error) { return error.Message; }
    catch (InvalidOperationException error) { return error.Message; }
    catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
}
Guid Id(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes("doc-regeneration:" + name)).Take(16).ToArray());
string N(int value) => value.ToString(invariant);
string Keys(IEnumerable<string> keys) => "[" + string.Join(",", keys) + "]";
string Json(object value) => JsonSerializer.Serialize(value);
// A fake Revit UniqueId per input source, distinct per source.
string Fake(string source)
{
    var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    return hex[..8] + "-" + hex[8..12] + "-" + hex[12..16] + "-" + hex[16..20] + "-" + hex[20..32] + "-" + hex[32..40];
}

var projectId = Id("project");
Guid RoomOf(int room) => Id("room" + N(room));
Guid Role(int room, string role) => RoomCompiler.Id(RoomOf(room), role);
Guid Door(int room) => Id("door" + N(room));
string[] wallRoles = { "south", "east", "north", "west" };

// Each room: a RoomCompiler slice (own level at 0 m, four walls, space, plan/schedule/sheet elements) plus a door on
// the south wall with absolute geometry [ax, ay, bx, by, sill, height]. Rooms sit 10 m apart.
Project Build(int rooms)
{
    var elements = new List<Element>();
    for (var room = 0; room < rooms; room++)
    {
        elements.AddRange(RoomCompiler.Compile(projectId, 0, new RoomIntent(RoomOf(room), 4, 3, 2.8, 0.2, room * 10, 0, "m", provenance)).Elements);
        var x = room * 10 + 0.1;
        elements.Add(new(Door(room), "door", new[] { x + 1, 0.1, x + 1.9, 0.1, 0, 2.1 }, new[] { Role(room, "south"), Role(room, "level") }, provenance));
    }
    return new(projectId, 0, elements.ToArray());
}
RevitBindings Bindings(int rooms, params int[] unbound) => new(document,
    Enumerable.Range(0, rooms).Where(room => !unbound.Contains(room)).ToDictionary(room => Role(room, "level"), room => "Level " + N(room)),
    Array.Empty<RevitWallTypeBinding>(), null, null);

// Per room: a floor plan of its level, a section along its south wall, a space tag with leader, a door tag, a width
// dimension (west to east wall), a sheet with the plan and the section placed. Shared: a 3D view of room 0's walls,
// a door schedule and a room schedule, the door schedule placed on sheet 0.
DocumentationSet Docs(int rooms)
{
    var views = new List<DocView>();
    var tags = new List<DocTag>();
    var dimensions = new List<DocDimension>();
    var schedules = new List<DocSchedule>();
    var sheets = new List<DocSheet>();
    var placements = new List<DocPlacement>();
    for (var room = 0; room < rooms; room++)
    {
        var r = N(room);
        views.Add(new DocView("plan-" + r, "floor_plan", new[] { Role(room, "level") }, "Room " + r + " plan", 50));
        views.Add(new DocView("section-" + r, "section", new[] { Role(room, "south") }, "Room " + r + " section", null, 3));
        tags.Add(new DocTag("tag-space-" + r, "plan-" + r, Role(room, "space"), 0.5, 0.5, true));
        tags.Add(new DocTag("tag-door-" + r, "plan-" + r, Door(room)));
        dimensions.Add(new DocDimension("dim-width-" + r, "plan-" + r, Role(room, "west"), Role(room, "east"), 0.5));
        sheets.Add(new DocSheet("sheet-" + r, "A10" + r, "Room " + r));
        placements.Add(new DocPlacement("place-plan-" + r, "sheet-" + r, "plan-" + r, 200, 150));
        placements.Add(new DocPlacement("place-section-" + r, "sheet-" + r, "section-" + r, 450, 150));
    }
    views.Add(new DocView("3d-0", "three_d", wallRoles.Select(role => Role(0, role)).ToArray(), "Room 0 3D"));
    schedules.Add(new DocSchedule("schedule-doors", "door", "door", new[] { "Mark", "Width" }, "Mark", "Door Schedule"));
    schedules.Add(new DocSchedule("schedule-rooms", "space", "room", new[] { "Number", "Name", "Area" }, null, "Room Schedule"));
    placements.Add(new DocPlacement("place-doors", "sheet-0", "schedule-doors", 600, 300));
    return new(views, tags, dimensions, schedules, sheets, placements);
}
DocumentationSet extraDocsForDigest(DocumentationSet set) => set with { Sheets = set.Sheets.Append(new DocSheet("sheet-digest", "Z9", "Digest")).ToArray() };
Project Replace(Project project, Guid id, Func<Element, Element> change) => project with { Elements = project.Elements.Select(element => element.Id == id ? change(element) : element).ToArray() };
Project Remove(Project project, params Guid[] ids) => project with { Elements = project.Elements.Where(element => !ids.Contains(element.Id)).ToArray() };
Project Add(Project project, Element element) => project with { Elements = project.Elements.Append(element).ToArray() };
Element Move(Element wall, double dx, double dy)
{
    var g = wall.GeometryMetres;
    return wall with { GeometryMetres = new[] { g[0] + dx, g[1] + dy, g[2] + dx, g[3] + dy, g[4], g[5] } };
}

// Test-side content of one item's regeneration result, without its operation id.
Dictionary<string, string> Content(DocRegenerationPlan plan)
{
    var content = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var step in plan.Steps)
        content.Add(step.Key, step.ItemKind + "|" + step.Command + "|" + step.ArgsJson + "|" + string.Join(",", step.Inputs.Select(input => input.Field + "=" + input.Source)) + "|" + string.Join(",", step.Elements));
    foreach (var item in plan.Unsupported) content.Add(item.Key, "!" + item.ItemKind + "|" + item.Code + "|" + item.Detail + "|" + string.Join(",", item.Elements));
    return content;
}
// The items of the set a tag, dimension or placement hangs from.
string[] ParentsOf(string key, DocumentationSet set) =>
    set.Tags.Where(tag => tag.Key == key).Select(tag => tag.View)
        .Concat(set.Dimensions.Where(dimension => dimension.Key == key).Select(dimension => dimension.View))
        .Concat(set.Placements.Where(placement => placement.Key == key).SelectMany(placement => new[] { placement.Sheet, placement.View })).ToArray();
bool EnvelopesParse(DocRegenerationPlan plan, out string detail)
{
    foreach (var step in plan.Steps)
    {
        var ids = step.Inputs.ToDictionary(input => input.Source, input => Fake(input.Source), StringComparer.Ordinal);
        string? error = Code(() =>
        {
            var envelope = DocRegeneration.Envelope(plan, step, ids);
            if (envelope.OperationId != step.OperationId || envelope.Command != step.Command) throw new InvalidOperationException("envelope_mismatch");
        });
        if (error is not null) { detail = step.Key + ": " + error; return false; }
    }
    detail = "";
    return true;
}

var regexOperation = new Regex(@"\Adoc\.[0-9a-f]{32}\z", RegexOptions.CultureInvariant);

// Generic checks of one before/after pair; the list names every violated property (empty when all hold).
(DocRegenerationPlan Local, List<string> Problems) Evaluate(Project before, Project after, DocumentationSet set, RevitBindings bindings)
{
    var problems = new List<string>();
    var plan = Compiler.Diff(before, after);
    var local = DocRegeneration.Affected(before, after, plan, set, bindings);
    var full = DocRegeneration.Full(after, set, bindings);
    var prior = DocRegeneration.Full(before, set, bindings);
    var keys = local.Regenerated.ToHashSet(StringComparer.Ordinal);
    if (Json(local.Steps) != Json(full.Steps.Where(step => keys.Contains(step.Key)).ToArray())) problems.Add("affected steps differ from full regeneration restricted to the regenerated items");
    if (Json(local.Unsupported) != Json(full.Unsupported.Where(item => keys.Contains(item.Key)).ToArray())) problems.Add("affected unsupported entries differ from full regeneration restricted to the regenerated items");
    if (local.Steps.Any(step => !keys.Contains(step.Key)) || local.Unsupported.Any(item => !keys.Contains(item.Key))) problems.Add("output names an item that is not regenerated");
    if (local.Steps.Count + local.Unsupported.Count != local.Regenerated.Count) problems.Add("each regenerated item has exactly one step or one unsupported entry");
    var before_ = Content(prior);
    var after_ = Content(full);
    var missed = after_.Keys.Union(before_.Keys).Where(key => (!before_.TryGetValue(key, out var a) || !after_.TryGetValue(key, out var b) || a != b) && !keys.Contains(key)).ToArray();
    if (missed.Length > 0) problems.Add("items whose full regeneration changed but were not regenerated: " + Keys(missed));
    // Strict minimality, from the two full regenerations only: an item regenerated on its own (not because its view,
    // schedule or sheet is) must have a different full result (content without operation id: command, args, inputs,
    // elements, or unsupported code, detail and elements).
    var untouched = local.Regenerated.Where(key => !ParentsOf(key, set).Any(keys.Contains) && before_[key] == after_[key]).ToArray();
    if (untouched.Length > 0) problems.Add("items regenerated with an unchanged full result: " + Keys(untouched));
    var merged = DocRegeneration.Merge(prior, local);
    if (!DocRegeneration.SameContent(merged, full)) problems.Add("prior full regeneration with the regenerated items replaced differs from full regeneration of after");
    if (Json(Content(merged).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()) != Json(after_.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()))
        problems.Add("merged content differs from full content (test-side comparison)");
    if (!DocRegeneration.Agrees(before, after, plan, set, bindings)) problems.Add("proof helper disagrees");
    if (local.TotalItems != full.TotalItems || full.Regenerated.Count != full.TotalItems || local.RegeneratedCount != local.Regenerated.Count) problems.Add("counters");
    if (local.AfterHash != plan.AfterHash || local.Revision != plan.ExpectedRevision || local.ProjectId != plan.ProjectId || full.AfterHash != plan.AfterHash) problems.Add("header");
    if (Json(DocRegeneration.Affected(before, after, plan, set, bindings)) != Json(local)) problems.Add("not deterministic");
    if (!EnvelopesParse(local, out var envelopeDetail)) problems.Add("envelope: " + envelopeDetail);
    return (local, problems);
}

DocRegenerationPlan Case(string name, Project before, Project after, IEnumerable<string> expected, DocumentationSet set, RevitBindings bindings)
{
    var (local, problems) = Evaluate(before, after, set, bindings);
    Check(problems.Count == 0, name + ": affected-only output equals full regeneration on those items and merges to full regeneration of after", string.Join("; ", problems));
    var want = expected.OrderBy(key => key, StringComparer.Ordinal).ToArray();
    Check(local.Regenerated.SequenceEqual(want), name + ": regenerated items are exactly the touched ones", Keys(local.Regenerated) + " vs " + Keys(want));
    var permuted = after with { Elements = Enumerable.Reverse(after.Elements).ToArray() };
    var permutedLocal = DocRegeneration.Affected(before, permuted, Compiler.Diff(before, permuted), set, bindings);
    Check(Json(Content(permutedLocal)) == Json(Content(local)) && permutedLocal.Regenerated.SequenceEqual(local.Regenerated), name + ": result does not depend on element order");
    return local;
}

try
{
    var basis = Build(3);
    var docs = Docs(3);
    var bindings = Bindings(3);
    var full = DocRegeneration.Full(basis, docs, bindings);
    Check(full.TotalItems == 28 && full.Regenerated.Count == 28 && full.Steps.Count == 28 && full.Unsupported.Count == 0,
        "full regeneration of the 3-room corpus emits one step for each of its 28 items", N(full.TotalItems) + "/" + N(full.Steps.Count) + "/" + N(full.Unsupported.Count) + " " + Json(full.Unsupported));
    Check(full.Version == DocRegeneration.Version && full.ProjectId == projectId && full.Revision == 0 && full.AfterHash == Compiler.Hash(basis) && full.Document == document, "full regeneration header names project, revision, hash and document");
    string[] kindOrder = { DocRegeneration.View, DocRegeneration.Schedule, DocRegeneration.Sheet, DocRegeneration.Tag, DocRegeneration.Dimension, DocRegeneration.Placement };
    var expectedOrder = full.Steps.OrderBy(step => Array.IndexOf(kindOrder, step.ItemKind)).ThenBy(step => step.Key, StringComparer.Ordinal).Select(step => step.Key).ToArray();
    Check(full.Steps.Select(step => step.Key).SequenceEqual(expectedOrder) && full.Steps.All(step => Array.IndexOf(kindOrder, step.ItemKind) >= 0),
        "steps run views, schedules, sheets, tags, dimensions, then placements, each by key (ordinal)", Keys(full.Steps.Select(step => step.Key)));
    Check(full.Regenerated.SequenceEqual(full.Regenerated.OrderBy(key => key, StringComparer.Ordinal)), "regenerated keys are sorted ordinally");
    Check(full.Steps.All(step => regexOperation.IsMatch(step.OperationId)) && full.Steps.Select(step => step.OperationId).Distinct().Count() == 28, "operation ids are doc. + 32 hex and distinct");
    Check(Json(DocRegeneration.Full(basis, docs, bindings)) == Json(full), "full regeneration is byte-identical for the same input");
    var nextRevision = DocRegeneration.Full(basis with { Revision = 1 }, docs, bindings);
    Check(nextRevision.Steps.Select(step => step.OperationId).Intersect(full.Steps.Select(step => step.OperationId)).Count() == 0
        && Json(Content(nextRevision)) == Json(Content(full)), "another revision changes every operation id (no stale receipt replay) but not the commands");
    Check(EnvelopesParse(full, out var fullEnvelope), "every full-regeneration step parses through the catalogue once its inputs are filled", fullEnvelope);

    JsonElement Args(string key) => JsonDocument.Parse(full.Steps.Single(step => step.Key == key).ArgsJson).RootElement.Clone();
    DocCommandStep Step(string key) => full.Steps.Single(step => step.Key == key);
    double[] Numbers(JsonElement element) => element.EnumerateArray().Select(item => item.GetDouble()).ToArray();
    bool Near(double[] actual, params double[] expected) => actual.Length == expected.Length && actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) <= 1e-6);
    string Inputs(string key) => string.Join(",", Step(key).Inputs.Select(input => input.Field + "=" + input.Source));

    // ---- Command content of each documentation kind ----
    var plan0 = Args("plan-0");
    Check(Step("plan-0").Command == "view.create_plan" && plan0.GetProperty("kind").GetString() == "floor_plan" && plan0.GetProperty("level").GetString() == "Level 0"
        && plan0.GetProperty("name").GetString() == "Room 0 plan" && plan0.GetProperty("scale").GetInt32() == 50 && Step("plan-0").Inputs.Count == 0,
        "floor plan: view.create_plan of the bound Revit level with name and scale", Step("plan-0").ArgsJson);
    var section0 = Args("section-0");
    Check(Step("section-0").Command == "view.create_section" && Near(Numbers(section0.GetProperty("line").GetProperty("start")), 100, 100) && Near(Numbers(section0.GetProperty("line").GetProperty("end")), 4300, 100)
        && Math.Abs(section0.GetProperty("depth_mm").GetDouble() - 3000) < 1e-6 && Math.Abs(section0.GetProperty("bottom_elevation_mm").GetDouble()) < 1e-6
        && Math.Abs(section0.GetProperty("top_elevation_mm").GetDouble() - 2800) < 1e-6,
        "section: along the wall axis in millimetres, depth, from the wall's level elevation to its top", Step("section-0").ArgsJson);
    var box = Args("3d-0").GetProperty("section_box");
    Check(Step("3d-0").Command == "view.create_3d" && Near(Numbers(box.GetProperty("min")), 0, 0, 0) && Near(Numbers(box.GetProperty("max")), 4400, 3400, 2800),
        "3D view: section box around the referenced walls (axis +- half thickness, level to top)", Step("3d-0").ArgsJson);
    var spaceTag = Args("tag-space-0");
    Check(Step("tag-space-0").Command == "tag.create" && Near(Numbers(spaceTag.GetProperty("head")), 2700, 2200) && spaceTag.GetProperty("leader").GetBoolean()
        && spaceTag.GetProperty("orientation").GetString() == "horizontal"
        && Inputs("tag-space-0") == "args.view_uid=" + DocRegeneration.ItemSource("plan-0") + ",args.element_uid=" + DocRegeneration.ElementSource(Role(0, "space")),
        "space tag: head at the space centre plus offset in millimetres, leader, view and element filled from inputs", Step("tag-space-0").ArgsJson + " " + Inputs("tag-space-0"));
    var doorTag = Args("tag-door-1");
    Check(Near(Numbers(doorTag.GetProperty("head")), 11550, 100) && !doorTag.GetProperty("leader").GetBoolean(), "door tag: head at the opening midpoint", Step("tag-door-1").ArgsJson);
    var width = Args("dim-width-0");
    Check(Step("dim-width-0").Command == "dimension.create_linear" && Near(Numbers(width.GetProperty("line").GetProperty("start")), 100, 1200) && Near(Numbers(width.GetProperty("line").GetProperty("end")), 4300, 1200)
        && width.GetProperty("wall_face").GetString() == "exterior"
        && Inputs("dim-width-0") == "args.view_uid=" + DocRegeneration.ItemSource("plan-0") + ",args.first_uid=" + DocRegeneration.ElementSource(Role(0, "west")) + ",args.second_uid=" + DocRegeneration.ElementSource(Role(0, "east")),
        "dimension: line from the first wall's midpoint (plus offset along it) perpendicular onto the second wall", Step("dim-width-0").ArgsJson + " " + Inputs("dim-width-0"));
    var doors = Args("schedule-doors");
    Check(Step("schedule-doors").Command == "schedule.create" && doors.GetProperty("category").GetString() == "door" && string.Join(",", doors.GetProperty("fields").EnumerateArray().Select(item => item.GetString())) == "Mark,Width"
        && doors.GetProperty("sort_by").GetString() == "Mark" && doors.GetProperty("name").GetString() == "Door Schedule"
        && Step("schedule-doors").Elements.SequenceEqual(new[] { Door(0), Door(1), Door(2) }.Order()),
        "schedule: category, fields, sort and name; its elements are the scheduled BIM-IR elements", Step("schedule-doors").ArgsJson);
    var sheet0 = Args("sheet-0");
    Check(Step("sheet-0").Command == "sheet.create" && sheet0.GetProperty("number").GetString() == "A100" && sheet0.GetProperty("name").GetString() == "Room 0" && !sheet0.TryGetProperty("title_block", out _),
        "sheet: number and name, no title block", Step("sheet-0").ArgsJson);
    var placeDoors = Args("place-doors");
    Check(Step("place-doors").Command == "sheet.place_view" && Near(Numbers(placeDoors.GetProperty("center")), 600, 300)
        && Inputs("place-doors") == "args.sheet_uid=" + DocRegeneration.ItemSource("sheet-0") + ",args.view_uid=" + DocRegeneration.ItemSource("schedule-doors"),
        "placement: sheet.place_view with sheet and view (or schedule) filled from inputs", Step("place-doors").ArgsJson + " " + Inputs("place-doors"));
    var tagIds = Step("tag-space-0").Inputs.ToDictionary(input => input.Source, input => Fake(input.Source), StringComparer.Ordinal);
    var tagEnvelope = DocRegeneration.Envelope(full, Step("tag-space-0"), tagIds);
    Check(tagEnvelope.Args is TagCreateArgs typed && typed.ViewUid == Fake(DocRegeneration.ItemSource("plan-0")) && typed.ElementUid == Fake(DocRegeneration.ElementSource(Role(0, "space"))) && typed.Leader
        && typed.Head is Point2 head && Math.Abs(head.X.Millimetres - 2700) < 1e-6 && tagEnvelope.Document == document,
        "envelope fills the inputs and binds a typed tag.create");
    Check(Code(() => DocRegeneration.Envelope(full, Step("tag-space-0"), new Dictionary<string, string>())) == "input_unresolved", "envelope refuses an unresolved input");
    Check(Code(() => DocRegeneration.Envelope(full, Step("tag-space-0"), tagIds.ToDictionary(pair => pair.Key, pair => "not-a-unique-id"))) == "input_invalid", "envelope refuses an input that is not a UniqueId");
    Check(Code(() => DocRegeneration.Envelope(full, Step("tag-space-0"), tagIds.ToDictionary(pair => pair.Key, pair => (string)null!))) == "input_invalid", "envelope refuses a null UniqueId");
    Check(Code(() => DocRegeneration.Envelope(full, Step("tag-space-0") with { Inputs = new[] { new DocStepInput(null!, DocRegeneration.ItemSource("plan-0")) } }, tagIds)) == "input_field_unsupported", "envelope refuses a null input field");
    Check(Code(() => DocRegeneration.Envelope(full, Step("tag-space-0") with { Inputs = new[] { new DocStepInput("args.view_uid", null!) } }, tagIds)) == "input_unresolved", "envelope refuses a null input source");
    Check(Regex.IsMatch(full.InputDigest, @"\A[0-9a-f]{64}\z") && DocRegeneration.Full(Build(3), Docs(3), Bindings(3)).InputDigest == full.InputDigest
        && DocRegeneration.Full(basis, docs, Bindings(3, 1)).InputDigest != full.InputDigest && DocRegeneration.Full(basis, extraDocsForDigest(docs), bindings).InputDigest != full.InputDigest
        && DocRegeneration.Full(basis with { Revision = 1 }, docs, bindings).InputDigest == full.InputDigest,
        "the header digests the set and bindings (not the model)", full.InputDigest);
    var changedBefore = Replace(basis, Role(0, "south"), wall => Move(wall, 0, -0.5));
    Check(Code(() => DocRegeneration.Merge(full, DocRegeneration.Affected(basis, changedBefore, Compiler.Diff(basis, changedBefore), docs, Bindings(3, 1)))) == "input_mismatch",
        "merge refuses an update made with other bindings than the prior regeneration");

    // ---- Documentation set validation ----
    Check(Code(() => DocRegeneration.Full(basis, docs with { Views = docs.Views.Append(new DocView("plan-0", "floor_plan", new[] { Role(0, "level") })).ToArray() }, bindings)) == "doc_key_duplicate", "duplicate keys are refused");
    Check(Code(() => DocRegeneration.Full(basis, docs with { Sheets = docs.Sheets.Append(new DocSheet(" padded", "Z1", "Z")).ToArray() }, bindings)) == "doc_key_invalid", "keys with surrounding white space are refused");
    Check(Code(() => DocRegeneration.Full(basis, docs with { Tags = docs.Tags.Append(new DocTag("tag-x", "nowhere", Door(0))).ToArray() }, bindings)) == "doc_view_unknown", "a tag in an unknown view is refused");
    Check(Code(() => DocRegeneration.Full(basis, docs with { Tags = docs.Tags.Append(new DocTag("tag-x", "section-0", Door(0))).ToArray() }, bindings)) == "doc_view_not_plan", "a tag outside a plan view is refused");
    Check(Code(() => DocRegeneration.Full(basis, docs with { Dimensions = docs.Dimensions.Append(new DocDimension("dim-x", "plan-0", Role(0, "west"), Role(0, "west"))).ToArray() }, bindings)) == "doc_dimension_references_invalid", "a dimension between an element and itself is refused");
    Check(Code(() => DocRegeneration.Full(basis, docs with { Placements = docs.Placements.Append(new DocPlacement("place-x", "nowhere", "plan-0", 1, 1)).ToArray() }, bindings)) == "doc_sheet_unknown", "a placement on an unknown sheet is refused");
    Check(Code(() => DocRegeneration.Full(basis, docs with { Placements = docs.Placements.Append(new DocPlacement("place-x", "sheet-0", "tag-door-0", 1, 1)).ToArray() }, bindings)) == "doc_view_unknown", "a placement of something that is not a view or schedule is refused");
    Check(Code(() => DocRegeneration.Full(basis, docs with { Views = docs.Views.Append(new DocView("plan-x", "floor_plan", new[] { Role(0, "level"), Role(1, "level") })).ToArray() }, bindings)) == "doc_view_elements_invalid", "a plan view names exactly one level");
    Check(Code(() => DocRegeneration.Full(basis, docs with { Views = docs.Views.Append(new DocView("elev-x", "elevation", new[] { Role(0, "level") })).ToArray() }, bindings)) == "doc_view_kind_invalid", "unknown view kinds are refused");
    Check(Code(() => DocRegeneration.Full(basis, docs with { Tags = docs.Tags.Append(new DocTag("tag-x", "plan-0", Door(0), double.NaN, 0)).ToArray() }, bindings)) == "doc_number_invalid", "non-finite offsets are refused");
    Check(Code(() => DocRegeneration.Full(basis, docs, bindings with { Document = new RevitDocumentRef("relative.rvt", document.UniqueId) })) == RevitCommandCodes.DocumentPathInvalid, "a relative document path is refused");
    Check(Code(() => DocRegeneration.Full(basis, docs with { Sheets = docs.Sheets.Append(new DocSheet("a,b", "Z1", "Z")).ToArray() }, bindings)) == "doc_key_invalid", "keys containing ',' are refused (operation id sources stay unambiguous)");
    Check(Code(() => DocRegeneration.Full(basis, docs with { Sheets = docs.Sheets.Append(new DocSheet("sheet-x", "Z\ud800", "Z")).ToArray() }, bindings)) == "doc_text_invalid", "a sheet number with a lone surrogate is doc_text_invalid");
    Check(Code(() => DocRegeneration.Full(basis, docs with { Views = docs.Views.Append(new DocView("plan-x", "floor_plan", new[] { Role(0, "level") }, "\udc00 plan")).ToArray() }, bindings)) == "doc_text_invalid", "a view name with a lone surrogate is doc_text_invalid");
    Check(Code(() => DocRegeneration.Full(basis, docs with { Tags = docs.Tags.Append(new DocTag("tag-x", "plan-0", Door(0), 0, 0, false, "\ud800")).ToArray() }, bindings)) == "doc_text_invalid", "a tag orientation with a lone surrogate is doc_text_invalid");
    Check(Code(() => DocRegeneration.Full(basis, docs with { Schedules = docs.Schedules.Append(new DocSchedule("schedule-x", "door", "door", new[] { "Mark\ud800" })).ToArray() }, bindings)) == "doc_text_invalid", "a schedule field with a lone surrogate is doc_text_invalid");
    Check(Code(() => DocRegeneration.Full(basis, docs with { Schedules = docs.Schedules.Append(new DocSchedule("schedule-x", "door", "door", new[] { "Mark", null! })).ToArray() }, bindings)) == "doc_text_invalid", "a null schedule field is doc_text_invalid");
    Check(Code(() => DocRegeneration.Full(basis, docs with { Tags = docs.Tags.Append(new DocTag("tag-x", null!, Door(0))).ToArray() }, bindings)) == "doc_view_unknown", "a tag with a null view is doc_view_unknown");
    Check(Code(() => DocRegeneration.Full(basis, docs with { Dimensions = docs.Dimensions.Append(new DocDimension("dim-x", null!, Role(0, "west"), Role(0, "east"))).ToArray() }, bindings)) == "doc_view_unknown", "a dimension with a null view is doc_view_unknown");
    Check(Code(() => DocRegeneration.Full(basis, docs with { Placements = docs.Placements.Append(new DocPlacement("place-x", null!, "plan-0", 1, 1)).ToArray() }, bindings)) == "doc_sheet_unknown", "a placement with a null sheet is doc_sheet_unknown");
    Check(Code(() => DocRegeneration.Full(basis, docs with { Placements = docs.Placements.Append(new DocPlacement("place-x", "sheet-0", null!, 1, 1)).ToArray() }, bindings)) == "doc_view_unknown", "a placement with a null view is doc_view_unknown");
    var scheduleDoors = docs.Schedules.Single(schedule => schedule.Key == "schedule-doors");
    var noDoors = Remove(basis, Door(0), Door(1), Door(2));
    var windowSchedule = DocRegeneration.Full(noDoors, docs with { Schedules = new[] { scheduleDoors with { ElementKind = "window" } }, Placements = Array.Empty<DocPlacement>() }, bindings).Steps.Single(step => step.Key == "schedule-doors");
    var doorSchedule = DocRegeneration.Full(noDoors, docs with { Schedules = new[] { scheduleDoors }, Placements = Array.Empty<DocPlacement>() }, bindings).Steps.Single(step => step.Key == "schedule-doors");
    Check(windowSchedule.ArgsJson == doorSchedule.ArgsJson && windowSchedule.Elements.Count == 0 && doorSchedule.Elements.Count == 0 && windowSchedule.OperationId != doorSchedule.OperationId,
        "the scheduled element kind is part of a schedule's operation id (same args, same empty element list)");
    var tinyBasis = Add(basis, new Element(Id("tiny-wall"), "wall", new[] { 0.0, 0.0, 1e-200, 0.0, 2.8, 0.2 }, new[] { Role(0, "level") }, provenance));
    Check(CodeOf(DocRegeneration.Full(tinyBasis, docs with { Dimensions = docs.Dimensions.Append(new DocDimension("dim-tiny", "plan-0", Id("tiny-wall"), Role(0, "west"))).ToArray() }, bindings), "dim-tiny") == DocRegenerationCodes.GeometryInvalid,
        "a dimension to a wall whose length underflows to zero is geometry_invalid");
    Check(Code(() => DocRegeneration.Full(Add(basis, new Element(Id("dangling"), "door", new[] { 1.0, 0.9 }, new[] { Id("nowhere") }, provenance)), docs, bindings)) == "invalid_ir", "full regeneration refuses an IR Compiler.Validate rejects");
    var other = Replace(basis, Role(0, "south"), wall => Move(wall, 0, -1));
    Check(Code(() => DocRegeneration.Affected(basis, basis, Compiler.Diff(basis, other), docs, bindings)) == "plan_mismatch", "affected regeneration refuses a plan that is not the diff of before and after");

    // ---- Unsupported items are reported, never dropped ----
    var unboundFull = DocRegeneration.Full(basis, docs, Bindings(3, 1));
    string? CodeOf(DocRegenerationPlan plan, string key) => plan.Unsupported.SingleOrDefault(item => item.Key == key)?.Code;
    Check(CodeOf(unboundFull, "plan-1") == DocRegenerationCodes.LevelUnbound && CodeOf(unboundFull, "tag-space-1") == DocRegenerationCodes.DependencyUnsupported
        && CodeOf(unboundFull, "dim-width-1") == DocRegenerationCodes.DependencyUnsupported && CodeOf(unboundFull, "place-plan-1") == DocRegenerationCodes.DependencyUnsupported
        && unboundFull.Steps.Count + unboundFull.Unsupported.Count == 28 && unboundFull.Unsupported.Count == 5,
        "an unbound level makes its plan unsupported and every tag, dimension and placement in it dependency_unsupported", Json(unboundFull.Unsupported));
    var extra = docs with
    {
        Tags = docs.Tags.Append(new DocTag("tag-level", "plan-0", Role(0, "level"), 0, 0, true)).ToArray(),
        Dimensions = docs.Dimensions.Append(new DocDimension("dim-space", "plan-0", Role(0, "west"), Role(0, "space"))).Append(new DocDimension("dim-across", "plan-0", Role(0, "south"), Role(0, "west"))).ToArray(),
    };
    var extraFull = DocRegeneration.Full(basis, extra, bindings);
    Check(CodeOf(extraFull, "tag-level") == DocRegenerationCodes.CommandInvalid, "a leader on an element without a plan centre is refused by the catalogue (command_invalid)", Json(extraFull.Unsupported));
    Check(CodeOf(extraFull, "dim-space") == DocRegenerationCodes.KindMismatch, "a dimension to a non-wall is kind_mismatch", Json(extraFull.Unsupported));
    Check(CodeOf(extraFull, "dim-across") == DocRegenerationCodes.ReferencesNotParallel, "a dimension between perpendicular walls is references_not_parallel", Json(extraFull.Unsupported));

    // ---- Cases with exact regenerated sets ----
    var noop = Case("no-op diff", basis, basis, Array.Empty<string>(), docs, bindings);
    Check(noop.Steps.Count == 0 && noop.Unsupported.Count == 0 && noop.TotalItems == 28, "no-op diff regenerates nothing");

    var south0 = Case("south wall of room 0 moved", basis, Replace(basis, Role(0, "south"), wall => Move(wall, 0, -0.5)),
        new[] { "section-0", "3d-0", "place-section-0" }, docs, bindings);
    Check(!south0.Regenerated.Contains("tag-door-0") && !south0.Regenerated.Contains("tag-space-0") && !south0.Regenerated.Contains("schedule-doors") && !south0.Regenerated.Contains("schedule-rooms") && !south0.Regenerated.Contains("place-doors"),
        "moved wall: the door and space it bounds or hosts are unchanged, so their tags and schedules are untouched (no transitive dirty closure)", Keys(south0.Regenerated));

    var raised = Case("level of room 1 raised", basis, Replace(basis, Role(1, "level"), level => level with { GeometryMetres = new[] { 3.0 } }),
        new[] { "section-1", "place-section-1" }, docs, bindings);
    Check(!raised.Regenerated.Contains("plan-1"), "raised level: the plan view (same level reference) and its tags and dimensions are not regenerated", Keys(raised.Regenerated));
    var raisedSection = JsonDocument.Parse(raised.Steps.Single(step => step.Key == "section-1").ArgsJson).RootElement;
    Check(Math.Abs(raisedSection.GetProperty("bottom_elevation_mm").GetDouble() - 3000) < 1e-6 && Math.Abs(raisedSection.GetProperty("top_elevation_mm").GetDouble() - 5800) < 1e-6,
        "raised level: the section is re-cut at the new elevation", raised.Steps.Single(step => step.Key == "section-1").ArgsJson);

    var deleted = Case("door of room 2 deleted", basis, Remove(basis, Door(2)), new[] { "tag-door-2", "schedule-doors", "place-doors" }, docs, bindings);
    Check(CodeOf(deleted, "tag-door-2") == DocRegenerationCodes.ElementMissing && deleted.Steps.Single(step => step.Key == "schedule-doors").Elements.SequenceEqual(new[] { Door(0), Door(1) }.Order()),
        "deleted door: its tag is reported element_missing and the door schedule is refreshed without it", Json(deleted.Unsupported));

    var rotated = Case("east wall of room 0 rotated", basis, Replace(basis, Role(0, "east"), wall => { var g = wall.GeometryMetres; return wall with { GeometryMetres = new[] { g[0], g[1], g[2] + 0.3, g[3], g[4], g[5] } }; }),
        new[] { "3d-0", "dim-width-0" }, docs, bindings);
    Check(CodeOf(rotated, "dim-width-0") == DocRegenerationCodes.ReferencesNotParallel, "rotated wall: the width dimension is re-dimensioned and reported references_not_parallel", Json(rotated.Unsupported));

    Case("untagged door created in room 1", basis, Add(basis, new Element(Id("door-extra"), "door", new[] { 13.0, 0.1, 13.8, 0.1, 0, 2.1 }, new[] { Role(1, "south"), Role(1, "level") }, provenance)),
        new[] { "schedule-doors", "place-doors" }, docs, bindings);
    var narrowed = Case("space of room 2 narrowed", basis, Replace(basis, Role(2, "space"), space => { var g = space.GeometryMetres; return space with { GeometryMetres = new[] { g[0], g[1], g[2] - 0.5, g[3] } }; }),
        new[] { "tag-space-2" }, docs, bindings);
    Check(Near(Numbers(JsonDocument.Parse(narrowed.Steps.Single(step => step.Key == "tag-space-2").ArgsJson).RootElement.GetProperty("head")), 22450, 2200), "narrowed space: its tag is re-placed at the new centre");
    Case("BIM-IR sheet element provenance changed", basis, Replace(basis, Role(1, "sheet"), sheet => sheet with { Provenance = new Provenance("other", "unit") }), Array.Empty<string>(), docs, bindings);
    Case("raised level with that level unbound", basis, Replace(basis, Role(1, "level"), level => level with { GeometryMetres = new[] { 3.0 } }),
        new[] { "section-1", "place-section-1" }, docs, Bindings(3, 1));
    var flipped = Case("level of room 0 changed to another kind", basis, Replace(basis, Role(0, "level"), level => level with { Kind = "datum" }),
        new[] { "plan-0", "section-0", "3d-0", "tag-space-0", "tag-door-0", "dim-width-0", "place-plan-0", "place-section-0" }, docs, bindings);
    Check(CodeOf(flipped, "plan-0") == DocRegenerationCodes.KindMismatch && CodeOf(flipped, "tag-door-0") == DocRegenerationCodes.DependencyUnsupported, "level kind changed: the plan is kind_mismatch and its tags dependency_unsupported", Json(flipped.Unsupported));
    Case("extra items: deleted wall under a dimension and the 3D view", Build(3), Remove(Replace(Replace(Build(3), Role(0, "space"), space => space with { Dependencies = space.Dependencies.Where(id => id != Role(0, "west")).ToArray() }),
            Role(0, "plan"), plan => plan with { Dependencies = plan.Dependencies.Where(id => id != Role(0, "west")).ToArray() }), Role(0, "west")),
        new[] { "3d-0", "dim-width-0", "dim-space", "dim-across" }, extra, bindings);
    Case("door dependencies reordered (no result changes)", basis, Replace(basis, Door(0), door => door with { Dependencies = new[] { Role(0, "level"), Role(0, "south") } }),
        Array.Empty<string>(), docs, bindings);
    Case("wall thickness and height changed under a plan dimension and tags", basis, Replace(basis, Role(1, "west"), wall => { var g = wall.GeometryMetres; return wall with { GeometryMetres = new[] { g[0], g[1], g[2], g[3], 3.1, 0.25 } }; }),
        Array.Empty<string>(), docs, bindings);
    Case("door sill and height changed", basis, Replace(basis, Door(1), door => { var g = door.GeometryMetres; return door with { GeometryMetres = new[] { g[0], g[1], g[2], g[3], 0.1, 2.2 } }; }),
        Array.Empty<string>(), docs, bindings);
    Case("south wall thickness changed (section unchanged, 3D box changed)", basis, Replace(basis, Role(2, "south"), wall => { var g = wall.GeometryMetres; return wall with { GeometryMetres = new[] { g[0], g[1], g[2], g[3], g[4], 0.3 } }; }),
        Array.Empty<string>(), docs, bindings);
    Case("room 0 south wall thickness changed (3D box changed)", basis, Replace(basis, Role(0, "south"), wall => { var g = wall.GeometryMetres; return wall with { GeometryMetres = new[] { g[0], g[1], g[2], g[3], g[4], 0.3 } }; }),
        new[] { "3d-0" }, docs, bindings);
    var twoLevels = Add(basis, new Element(Id("level-extra"), "level", new[] { 0.0 }, Array.Empty<Guid>(), provenance));
    Case("wall dependencies reordered without moving its resolved level", twoLevels, Replace(twoLevels, Role(0, "south"), wall => wall with { Dependencies = new[] { Role(0, "level"), Id("level-extra") } }),
        Array.Empty<string>(), docs, bindings);
    Check(Json(DocRegeneration.Full(basis, docs, bindings).Steps.Single(step => step.Key == "section-0").Elements) == Json(new[] { Role(0, "south"), Role(0, "level") }),
        "a section lists the wall and the level it reads its elevation from");

    // ---- Hosted openings ([offset, width] along the host wall) ----
    var hostedWindow = new Element(Id("window-hosted"), "window", new[] { 0.5, 1.2 }, new[] { Role(0, "east"), Role(0, "level") }, provenance);
    var orphanWindow = new Element(Id("window-orphan"), "window", new[] { 0.5, 1.2 }, new[] { Role(0, "level") }, provenance);
    var ambiguousWindow = new Element(Id("window-ambiguous"), "window", new[] { 0.5, 1.2 }, new[] { Role(0, "east"), Role(0, "north"), Role(0, "level") }, provenance);
    var hostedBasis = Add(Add(Add(basis, hostedWindow), orphanWindow), ambiguousWindow);
    var hostedDocs = docs with { Tags = docs.Tags.Append(new DocTag("tag-window", "plan-0", hostedWindow.Id)).Append(new DocTag("tag-orphan", "plan-0", orphanWindow.Id)).Append(new DocTag("tag-ambiguous", "plan-0", ambiguousWindow.Id)).ToArray() };
    var hostedFull = DocRegeneration.Full(hostedBasis, hostedDocs, bindings);
    var windowStep = hostedFull.Steps.Single(step => step.Key == "tag-window");
    Check(Near(Numbers(JsonDocument.Parse(windowStep.ArgsJson).RootElement.GetProperty("head")), 4300, 1200) && windowStep.Elements.SequenceEqual(new[] { hostedWindow.Id, Role(0, "east") }),
        "hosted window tag: head on the host wall axis at offset + width / 2, and the host is listed", windowStep.ArgsJson + " " + Json(windowStep.Elements));
    Check(CodeOf(hostedFull, "tag-orphan") == DocRegenerationCodes.HostMissing, "a hosted opening without a host wall is host_missing", Json(hostedFull.Unsupported));
    Check(CodeOf(hostedFull, "tag-ambiguous") == DocRegenerationCodes.HostAmbiguous && hostedFull.Unsupported.Single(item => item.Key == "tag-ambiguous").Elements.SequenceEqual(new[] { ambiguousWindow.Id, Role(0, "east"), Role(0, "north") }),
        "a hosted opening with more than one wall dependency is host_ambiguous and lists every candidate host", Json(hostedFull.Unsupported));
    var movedHost = Case("host wall of a hosted window moved", hostedBasis, Replace(hostedBasis, Role(0, "east"), wall => Move(wall, 0.2, 0)),
        new[] { "3d-0", "dim-width-0", "tag-window" }, hostedDocs, bindings);
    Check(Near(Numbers(JsonDocument.Parse(movedHost.Steps.Single(step => step.Key == "tag-window").ArgsJson).RootElement.GetProperty("head")), 4500, 1200), "moved host: the hosted window tag follows the wall");

    // ---- Optional binding to the BIM-IR plan, schedule and sheet elements ----
    var boundDocs = docs with
    {
        Sheets = docs.Sheets.Select(sheet => sheet.Key == "sheet-0" ? sheet with { Bound = Role(0, "sheet") } : sheet).ToArray(),
        Schedules = docs.Schedules.Select(schedule => schedule.Key == "schedule-rooms" ? schedule with { Bound = Role(1, "schedule") } : schedule).ToArray(),
        Views = docs.Views.Select(view => view.Key == "plan-2" ? view with { Bound = Role(2, "plan") } : view).ToArray(),
    };
    Case("bound BIM-IR sheet element rewired (no result changes)", basis, Replace(basis, Role(0, "sheet"), sheet => sheet with { Dependencies = new[] { Role(0, "plan") } }),
        Array.Empty<string>(), boundDocs, bindings);
    Case("bound BIM-IR sheet element changed kind", basis, Replace(basis, Role(0, "sheet"), sheet => sheet with { Kind = "sheet_retired" }),
        new[] { "sheet-0", "place-plan-0", "place-section-0", "place-doors" }, boundDocs, bindings);
    Case("bound BIM-IR schedule element deleted", basis, Remove(Replace(basis, Role(1, "sheet"), sheet => sheet with { Dependencies = new[] { Role(1, "plan") } }), Role(1, "schedule")),
        new[] { "schedule-rooms" }, boundDocs, bindings);
    Case("bound BIM-IR plan element deleted", basis, Remove(Replace(basis, Role(2, "sheet"), sheet => sheet with { Dependencies = new[] { Role(2, "schedule") } }), Role(2, "plan")),
        new[] { "plan-2", "tag-space-2", "tag-door-2", "dim-width-2", "place-plan-2" }, boundDocs, bindings);
    Case("bound BIM-IR sheet element provenance only", basis, Replace(basis, Role(0, "sheet"), sheet => sheet with { Provenance = new Provenance("other", "unit") }), Array.Empty<string>(), boundDocs, bindings);
    var boundFull = DocRegeneration.Full(basis, boundDocs, bindings);
    Check(boundFull.Steps.Single(step => step.Key == "sheet-0").Elements.SequenceEqual(new[] { Role(0, "sheet") }), "a bound sheet lists its BIM-IR sheet element");
    Check(CodeOf(DocRegeneration.Full(basis, docs with { Sheets = docs.Sheets.Select(sheet => sheet with { Bound = Role(0, "south") }).ToArray() }, bindings), "sheet-1") == DocRegenerationCodes.KindMismatch,
        "a sheet bound to an element that is not a BIM-IR sheet is kind_mismatch");

    // ---- Seeded corpus of chained edits ----
    var random = new Random(4303);
    var current = Build(4);
    var corpusDocs = Docs(4);
    var corpusBindings = Bindings(4, 3);
    int steps = 0, agreed = 0, regeneratedSum = 0, totalSum = 0, emptyEdits = 0;
    var kinds = new SortedSet<string>(StringComparer.Ordinal);
    var firstProblem = "";
    for (var step = 0; step < 300; step++)
    {
        var room = random.Next(4);
        var next = current;
        var kind = random.Next(8);
        double Delta() => Math.Round(random.NextDouble() * 0.6 - 0.3, 3);
        switch (kind)
        {
            case 0: next = Replace(current, Role(room, wallRoles[random.Next(4)]), wall => Move(wall, Delta(), Delta())); break;
            case 1: next = Replace(current, Role(room, "level"), level => level.Kind == "level" ? level with { GeometryMetres = new[] { Math.Round(random.NextDouble() * 6, 2) } } : level); break;
            case 2: next = Replace(current, Role(room, "space"), space => space with { GeometryMetres = new[] { space.GeometryMetres[0], space.GeometryMetres[1], 3 + Math.Round(random.NextDouble() * 2, 2), space.GeometryMetres[3] } }); break;
            case 3: next = current.Elements.Any(element => element.Id == Door(room)) ? Remove(current, Door(room)) : Add(current, Build(4).Elements.Single(element => element.Id == Door(room))); break;
            case 4: next = Replace(current, Role(room, "east"), wall => { var g = wall.GeometryMetres; return wall with { GeometryMetres = new[] { g[0], g[1], Math.Abs(g[2] - g[0]) > 1e-9 ? g[0] : g[0] + 0.3, g[3], g[4], g[5] } }; }); break;
            case 5: next = Replace(current, Door(room), door => { var g = door.GeometryMetres; var shift = Delta(); return door with { GeometryMetres = new[] { g[0] + shift, g[1], g[2] + shift, g[3], g[4], g[5] } }; }); break;
            case 6: next = Replace(current, Role(room, random.Next(2) == 0 ? "sheet" : "schedule"), element => element with { Provenance = new Provenance("edit" + N(step), "unit") }); break;
            default: next = Replace(current, Role(room, "level"), level => level with { Kind = level.Kind == "level" ? "datum" : "level" }); break;
        }
        var (local, problems) = Evaluate(current, next, corpusDocs, corpusBindings);
        steps++;
        if (problems.Count == 0) agreed++;
        else if (firstProblem.Length == 0) firstProblem = "step " + N(step) + " kind " + N(kind) + ": " + string.Join("; ", problems);
        regeneratedSum += local.RegeneratedCount;
        totalSum += local.TotalItems;
        if (local.RegeneratedCount == 0) emptyEdits++;
        foreach (var item in local.Unsupported) kinds.Add(item.Code);
        current = next;
    }
    Check(agreed == steps, "seeded corpus: 300 chained edits, each affected-only output equals full regeneration and merges to it", firstProblem);
    Check(regeneratedSum < totalSum / 3, "seeded corpus stays local on average", N(regeneratedSum) + "/" + N(totalSum));
    Check(emptyEdits > 0 && kinds.Contains(DocRegenerationCodes.ElementMissing) && kinds.Contains(DocRegenerationCodes.ReferencesNotParallel) && kinds.Contains(DocRegenerationCodes.KindMismatch)
        && kinds.Contains(DocRegenerationCodes.DependencyUnsupported) && kinds.Contains(DocRegenerationCodes.LevelUnbound),
        "seeded corpus exercises no-op edits and every unsupported path", N(emptyEdits) + " " + string.Join(",", kinds));
    Console.WriteLine(JsonSerializer.Serialize(new { benchmark = "doc_regeneration_corpus", steps, regenerated = regeneratedSum, total = totalSum, empty_edits = emptyEdits, codes = kinds }));

    // ---- Larger model counter ----
    var large = Build(50);
    var largeDocs = Docs(50);
    var largeAfter = Replace(large, Role(17, "south"), wall => Move(wall, 0, -0.5));
    var largeResult = DocRegeneration.Affected(large, largeAfter, Compiler.Diff(large, largeAfter), largeDocs, Bindings(50));
    Check(largeResult.RegeneratedCount == 2 && largeResult.TotalItems == 404, "50-room set: one wall move regenerates 2 of 404 documentation items (its section and that section's placement)", N(largeResult.RegeneratedCount) + "/" + N(largeResult.TotalItems) + " " + Keys(largeResult.Regenerated));
    Check(DocRegeneration.Agrees(large, largeAfter, Compiler.Diff(large, largeAfter), largeDocs, Bindings(50)), "50-room set: affected-only regeneration merges to full regeneration");
    Console.WriteLine(JsonSerializer.Serialize(new { benchmark = "doc_regeneration", regenerated = largeResult.RegeneratedCount, total = largeResult.TotalItems }));
}
catch (Exception error)
{
    Console.WriteLine("::error::doc-regeneration FAIL unexpected " + error.GetType().Name + " | " + error.Message);
    failed++;
}

// ---- P4-04 default documentation set (tests/doc-regeneration/Defaults.cs) ----
try
{
    DefaultsTests.Run(Check);
}
catch (Exception error)
{
    Console.WriteLine("::error::doc-regeneration FAIL unexpected " + error.GetType().Name + " | " + error.Message);
    failed++;
}

Console.WriteLine(JsonSerializer.Serialize(new { passed, failed, doc_regeneration_contract = failed == 0 }));
Environment.ExitCode = failed == 0 && passed > 0 ? 0 : 1;
