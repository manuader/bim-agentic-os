using Bim.Core;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// P4-13 oracle: DocRealized turns an owner -Catalogue run record (steps[] with key, passed, blocked and the reply's
// created[] UniqueIds) plus a BIM-IR fixture and an assignment {"doc:<key>"|"element:<id D>": "<step key>"} into the
// realized map doc-update needs (source -> the first UniqueId that step created), refusing malformed records, duplicate
// steps, members or UniqueIds, unknown steps or sources, and steps that did not succeed or created nothing, each with a
// typed code; the canonical JSON is the map sorted by key (ordinal), compact, byte-identical for the same input.
static class RealizedTests
{
    static readonly Provenance provenance = new("doc-realized-tests", "unit");
    static readonly RevitDocumentRef document = new(@"C:\Repo\.bim\native-runs\p4-13\copy.rvt", "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd13");
    static Guid Id(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes("doc-realized:" + name)).Take(16).ToArray());
    static readonly Guid projectId = Id("project");
    static readonly Guid roomId = Id("room0");
    static readonly Guid doorId = Id("door0");
    static Guid Role(string role) => RoomCompiler.Id(roomId, role);
    static string K(Guid id) => id.ToString("N");

    static string Uid(string source)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        return hex[..8] + "-" + hex[8..12] + "-" + hex[12..16] + "-" + hex[16..20] + "-" + hex[20..32] + "-" + hex[32..40];
    }

    static Project Fixture()
    {
        var elements = new List<Element>(RoomCompiler.Compile(projectId, 0, new RoomIntent(roomId, 4, 3, 2.8, 0.2, 0, 0, "m", provenance)).Elements);
        elements.Add(new(doorId, "door", new[] { 1.1, 0.1, 1.9, 0.1, 0, 2.1 }, new[] { Role("south"), Role("level") }, provenance));
        return new(projectId, 0, elements.ToArray());
    }

    sealed record S(string Key, bool Passed, bool Blocked, string? Status, string[] Created, string[]? Deleted = null, bool DryRun = false);
    const string RecordDocument = "0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9-0000abcd";

    // A run record as the owner session writes it (members the builder does not read are present and ignored).
    static string Record(params S[] steps)
    {
        var rows = steps.Select((step, i) => new Dictionary<string, object?>
        {
            ["index"] = i + 1,
            ["group"] = "documentacion",
            ["key"] = step.Key,
            ["description"] = "paso " + step.Key,
            ["command"] = "doc.create",
            ["operation_id"] = "op-" + step.Key,
            ["request"] = new { command = "doc.create", args = new { dry_run = step.DryRun } },
            ["response"] = step.Status is null ? null : new Dictionary<string, object?>
            {
                ["status"] = step.Status, ["code"] = step.Status == "succeeded" ? "ok" : "refused", ["field"] = null, ["detail"] = null,
                ["created"] = step.Created, ["modified"] = Array.Empty<string>(), ["deleted"] = step.Deleted ?? Array.Empty<string>(),
                ["readback"] = null, ["receipt"] = null, ["warnings"] = Array.Empty<string>(),
            },
            ["passed"] = step.Passed,
            ["blocked"] = step.Blocked,
            ["reason"] = step.Passed ? null : "fallo",
            ["expected"] = new { Status = "succeeded" },
            ["comparisons"] = Array.Empty<object>(),
        }).ToArray();
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schema_version"] = 1, ["run"] = 1, ["document_unique_id"] = RecordDocument, ["document_path"] = "C:/x/BIM-Agentic-Test.rvt", ["copy_path"] = null,
            ["groups"] = Array.Empty<object>(), ["overall"] = new { total = steps.Length }, ["all_passed"] = steps.All(step => step.Passed),
            ["aborted"] = null, ["steps"] = rows,
        });
    }

    static string Assign(params (string Source, string Step)[] pairs) =>
        "{" + string.Join(",", pairs.Select(pair => JsonSerializer.Serialize(pair.Source) + ":" + JsonSerializer.Serialize(pair.Step))) + "}";

    static string? Code(Action action)
    {
        try { action(); return null; }
        catch (DocRealizedException error) { return error.Message == error.Code ? error.Code + "|" + error.Detail : "message " + error.Message + " != code " + error.Code; }
        catch (ArgumentNullException) { return "null"; }
        catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
    }

    public static void Run(Checker check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "realized: " + name, detail);
        var fixture = Fixture();
        IReadOnlyDictionary<string, string> From(string rec, string asg) => DocRealized.FromRecord(fixture, rec, asg, RecordDocument);
        var set = DocDefaults.From(fixture);
        var planKey = "plan-" + K(Role("level"));
        var tagKey = "tag-door-" + K(doorId);
        var keys = set.Views.Select(item => item.Key).Concat(set.Tags.Select(item => item.Key)).ToArray();
        Check(keys.Contains(planKey) && keys.Contains(tagKey), "fixture: the default set has the level plan and the door tag", string.Join(",", keys));

        var wall = DocRegeneration.ElementSource(Role("south"));
        var door = DocRegeneration.ElementSource(doorId);
        var plan = DocRegeneration.ItemSource(planKey);
        var tag = DocRegeneration.ItemSource(tagKey);
        var steps = new[]
        {
            new S("q-levels", true, false, "succeeded", Array.Empty<string>()),
            new S("doc-up-wall", true, false, "succeeded", new[] { Uid("wall") }),
            new S("doc-up-door", true, false, "succeeded", new[] { Uid("door") }),
            new S("doc-up-plan", true, false, "succeeded", new[] { Uid("plan"), Uid("plan-extra") }),
            new S("doc-up-tag", true, false, "succeeded", new[] { Uid("tag") }),
            new S("doc-up-failed", false, false, "failed", Array.Empty<string>()),
            new S("doc-up-blocked", false, true, null, Array.Empty<string>()),
            new S("doc-up-refused-pass", false, false, "succeeded", new[] { Uid("refused") }),
        };
        var record = Record(steps);
        var assignments = Assign((tag, "doc-up-tag"), (wall, "doc-up-wall"), (plan, "doc-up-plan"), (door, "doc-up-door"));

        // ---- Steps: the run record read strictly ----
        IReadOnlyList<DocRealizedStep> parsed = Array.Empty<DocRealizedStep>();
        var parseCode = Code(() => parsed = DocRealized.Steps(record));
        Check(parseCode is null && parsed.Count == steps.Length && parsed.Select(step => step.Key).SequenceEqual(steps.Select(step => step.Key)),
            "Steps reads every step of the record in order", parseCode ?? string.Join(",", parsed.Select(step => step.Key)));
        Check(parsed.Count == steps.Length && parsed.Select(step => step.Succeeded).SequenceEqual(new[] { true, true, true, true, true, false, false, false })
            && parsed[3].Created.SequenceEqual(new[] { Uid("plan"), Uid("plan-extra") }) && parsed[0].Created.Count == 0,
            "a step succeeded only when passed, not blocked and the reply status is succeeded; created UniqueIds kept in order",
            string.Join(",", parsed.Select(step => step.Key + "=" + step.Succeeded + "/" + step.Created.Count)));

        // ---- Build: the realized map ----
        IReadOnlyDictionary<string, string>? map = null;
        var buildCode = Code(() => map = From(record, assignments));
        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal) { [wall] = Uid("wall"), [door] = Uid("door"), [plan] = Uid("plan"), [tag] = Uid("tag") };
        Check(buildCode is null && map is not null && map.Count == 4 && expected.All(pair => map!.TryGetValue(pair.Key, out var value) && value == pair.Value),
            "each assigned source maps to the first UniqueId its step created", buildCode ?? (map is null ? "null" : string.Join(",", map.Select(pair => pair.Key + "=" + pair.Value))));
        Check(map is not null && map.Keys.SequenceEqual(expected.Keys, StringComparer.Ordinal) && map is System.Collections.ObjectModel.ReadOnlyDictionary<string, string>,
            "the map is a read-only copy sorted by source (ordinal)", map is null ? "null" : string.Join(",", map.Keys));

        // ---- Canonical JSON ----
        var canonical = "";
        var canonicalCode = Code(() => canonical = DocRealized.Canonical(map ?? new Dictionary<string, string>()));
        var expectedJson = "{" + string.Join(",", expected.Select(pair => "\"" + pair.Key + "\":\"" + pair.Value + "\"")) + "}";
        Check(canonicalCode is null && canonical == expectedJson, "canonical JSON is the compact object sorted by key", canonicalCode ?? canonical);
        var reordered = Assign((door, "doc-up-door"), (plan, "doc-up-plan"), (wall, "doc-up-wall"), (tag, "doc-up-tag"));
        var again = Code(() => canonical = DocRealized.Canonical(From(record, reordered)));
        Check(again is null && canonical == expectedJson, "the same input in another member order gives the same bytes", again ?? canonical);
        var unsorted = new Dictionary<string, string>(StringComparer.Ordinal) { [tag] = Uid("tag"), [door] = Uid("door"), [wall] = Uid("wall"), [plan] = Uid("plan") };
        Check(Code(() => canonical = DocRealized.Canonical(unsorted)) is null && canonical == expectedJson, "Canonical sorts any map it is given", canonical);
        Check(Code(() => canonical = DocRealized.Canonical(new Dictionary<string, string>())) is null && canonical == "{}", "an empty map is {}", canonical);
        Check(Code(() => canonical = From(record, "{}") is { Count: 0 } empty ? DocRealized.Canonical(empty) : "nonempty") is null && canonical == "{}",
            "an empty assignment gives an empty map", canonical);

        // ---- doc-update accepts the built map: a record realizing every element and default item ----
        var bindings = new RevitBindings(document,
            fixture.Elements.Where(element => element.Kind == "level").ToDictionary(element => element.Id, element => "Level " + element.Id.ToString("N")[..8]),
            Array.Empty<RevitWallTypeBinding>(), null, null);
        var allKeys = set.Views.Select(item => item.Key).Concat(set.Tags.Select(item => item.Key)).Concat(set.Dimensions.Select(item => item.Key))
            .Concat(set.Schedules.Select(item => item.Key)).Concat(set.Sheets.Select(item => item.Key)).Concat(set.Placements.Select(item => item.Key));
        var sources = fixture.Elements.Select(element => DocRegeneration.ElementSource(element.Id)).Concat(allKeys.Select(DocRegeneration.ItemSource)).ToArray();
        var fullRecord = Record(sources.Select((source, i) => new S("s" + i.ToString(CultureInfo.InvariantCulture), true, false, "succeeded", new[] { Uid("full:" + source) })).ToArray());
        var fullAssign = Assign(sources.Select((source, i) => (source, "s" + i.ToString(CultureInfo.InvariantCulture))).ToArray());
        IReadOnlyDictionary<string, string>? full = null;
        DocUpdatePlan? updatePlan = null;
        var updateCode = Code(() => { full = From(fullRecord, fullAssign); updatePlan = DocUpdate.Plan(fixture, fixture, bindings, full); });
        Check(updateCode is null && full is not null && full.Count == sources.Length && updatePlan is not null && updatePlan.Replacement.Replacements.Count == 0
            && updatePlan.Forced.Count == 0 && updatePlan.Realized.Count == full.Count && full.All(pair => updatePlan!.Realized[pair.Key] == pair.Value),
            "a record realizing every element and default item gives a map DocUpdate.Plan takes unchanged, with nothing to do", updateCode ?? (updatePlan is null ? "no plan" : string.Join(",", updatePlan.Forced)));
        var reparsed = JsonSerializer.Deserialize<Dictionary<string, string>>(expectedJson)!;
        Check(map is not null && reparsed.Count == map.Count && reparsed.All(pair => map![pair.Key] == pair.Value), "the canonical JSON reads back as the same map");

        // ---- Refusals: the run record ----
        void Refuses(string name, string code, string? detail, Action action)
        {
            var got = Code(action);
            Check(got == code + "|" + detail, name, got ?? "accepted");
        }
        static System.Text.Json.Nodes.JsonNode? Drop(System.Text.Json.Nodes.JsonNode node, string name) { node.AsObject().Remove(name); return null; }
        string Edit(string text, Func<System.Text.Json.Nodes.JsonObject, System.Text.Json.Nodes.JsonNode?> change)
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();
            change(root);
            return root.ToJsonString();
        }
        var one = Record(new S("doc-up-wall", true, false, "succeeded", new[] { Uid("wall") }));
        Refuses("a record that is not JSON", DocRealizedCodes.RecordInvalid, "json", () => DocRealized.Steps("not json"));
        Refuses("a record that is not an object", DocRealizedCodes.RecordInvalid, "record", () => DocRealized.Steps("[]"));
        Refuses("a record with a duplicate root member", DocRealizedCodes.RecordInvalid, "record", () => DocRealized.Steps(one.Insert(1, "\"schema_version\":1,")));
        Refuses("a record of another schema version", DocRealizedCodes.RecordInvalid, "schema_version", () => DocRealized.Steps(Edit(one, root => root["schema_version"] = 2)));
        Refuses("a record without schema_version", DocRealizedCodes.RecordInvalid, "schema_version", () => DocRealized.Steps(Edit(one, root => Drop(root, "schema_version"))));
        Refuses("a record without steps", DocRealizedCodes.RecordInvalid, "steps", () => DocRealized.Steps(Edit(one, root => Drop(root, "steps"))));
        Refuses("a record with no step", DocRealizedCodes.RecordInvalid, "steps", () => DocRealized.Steps(Edit(one, root => root["steps"] = new System.Text.Json.Nodes.JsonArray())));
        Refuses("a step that is not an object", DocRealizedCodes.RecordInvalid, "steps[0]", () => DocRealized.Steps(Edit(one, root => root["steps"] = new System.Text.Json.Nodes.JsonArray(1))));
        var dupStep = one.Replace("\"index\":1,", "\"index\":1,\"key\":\"other\",", StringComparison.Ordinal);
        Refuses("a step with a duplicate member", DocRealizedCodes.RecordInvalid, "steps[0]", () => DocRealized.Steps(dupStep));
        Refuses("a step index not 1..n in order", DocRealizedCodes.RecordInvalid, "steps[0].index", () => DocRealized.Steps(Edit(one, root => root["steps"]![0]!["index"] = 2)));
        Refuses("a step key that is empty", DocRealizedCodes.RecordInvalid, "steps[0].key", () => DocRealized.Steps(Edit(one, root => root["steps"]![0]!["key"] = "")));
        Refuses("a step key that is not text", DocRealizedCodes.RecordInvalid, "steps[0].key", () => DocRealized.Steps(Edit(one, root => root["steps"]![0]!["key"] = 5)));
        Refuses("a step key over the bound", DocRealizedCodes.RecordInvalid, "steps[0].key", () => DocRealized.Steps(Edit(one, root => root["steps"]![0]!["key"] = new string('k', DocRealized.MaxStepKeyChars + 1))));
        Refuses("a step without passed", DocRealizedCodes.RecordInvalid, "steps[0].passed", () => DocRealized.Steps(Edit(one, root => Drop(root["steps"]![0]!, "passed"))));
        Refuses("a step whose blocked is not a boolean", DocRealizedCodes.RecordInvalid, "steps[0].blocked", () => DocRealized.Steps(Edit(one, root => root["steps"]![0]!["blocked"] = "no")));
        Refuses("a step both passed and blocked", DocRealizedCodes.RecordInvalid, "steps[0].blocked", () => DocRealized.Steps(Edit(one, root => root["steps"]![0]!["blocked"] = true)));
        Refuses("a response that is not an object", DocRealizedCodes.RecordInvalid, "steps[0].response", () => DocRealized.Steps(Edit(one, root => root["steps"]![0]!["response"] = "ok")));
        var dupResponse = one.Replace("\"status\":\"succeeded\",", "\"status\":\"succeeded\",\"status\":\"failed\",", StringComparison.Ordinal);
        Refuses("a response with a duplicate member", DocRealizedCodes.RecordInvalid, "steps[0].response", () => DocRealized.Steps(dupResponse));
        Refuses("a response without status", DocRealizedCodes.RecordInvalid, "steps[0].response.status", () => DocRealized.Steps(Edit(one, root => Drop(root["steps"]![0]!["response"]!, "status"))));
        Refuses("a created that is not an array", DocRealizedCodes.RecordInvalid, "steps[0].response.created", () => DocRealized.Steps(Edit(one, root => root["steps"]![0]!["response"]!["created"] = "x")));
        Refuses("a created entry that is not a UniqueId", DocRealizedCodes.UniqueIdInvalid, "steps[0].response.created[1]",
            () => DocRealized.Steps(Edit(one, root => root["steps"]![0]!["response"]!["created"] = new System.Text.Json.Nodes.JsonArray(Uid("a"), "not-a-uid"))));
        Refuses("a created entry that is not text", DocRealizedCodes.UniqueIdInvalid, "steps[0].response.created[0]",
            () => DocRealized.Steps(Edit(one, root => root["steps"]![0]!["response"]!["created"] = new System.Text.Json.Nodes.JsonArray(7))));
        Refuses("a step created the same UniqueId twice", DocRealizedCodes.UniqueIdDuplicate, "steps[0].response.created[1]",
            () => DocRealized.Steps(Edit(one, root => root["steps"]![0]!["response"]!["created"] = new System.Text.Json.Nodes.JsonArray(Uid("a"), Uid("a").ToUpperInvariant()))));
        Refuses("two steps with one key", DocRealizedCodes.StepDuplicate, "doc-up-wall",
            () => DocRealized.Steps(Record(new S("doc-up-wall", true, false, "succeeded", new[] { Uid("a") }), new S("doc-up-wall", true, false, "succeeded", new[] { Uid("b") }))));
        Refuses("more steps than the bound", DocRealizedCodes.BoundExceeded, "steps",
            () => DocRealized.Steps(Record(Enumerable.Range(0, DocRealized.MaxSteps + 1).Select(i => new S("s" + i.ToString(CultureInfo.InvariantCulture), true, false, "succeeded", Array.Empty<string>())).ToArray())));
        Refuses("more created UniqueIds in a step than the bound", DocRealizedCodes.BoundExceeded, "created",
            () => DocRealized.Steps(Record(new S("big", true, false, "succeeded", Enumerable.Range(0, DocRealized.MaxCreated + 1).Select(i => Uid("c" + i.ToString(CultureInfo.InvariantCulture))).ToArray()))));
        Refuses("a record over the byte bound", DocRealizedCodes.BoundExceeded, "record", () => DocRealized.Steps(one + new string(' ', DocRealized.MaxRecordBytes)));
        Check(Code(() => DocRealized.Steps(Edit(one, root => root["steps"]![0]!["response"] = null))) is null, "a step never sent (response null) is read, not refused");

        // ---- Refusals: the assignment ----
        Refuses("an assignment that is not JSON", DocRealizedCodes.AssignmentsInvalid, "json", () => DocRealized.Assignments("{"));
        Refuses("an assignment that is not an object", DocRealizedCodes.AssignmentsInvalid, "assignments", () => DocRealized.Assignments("[]"));
        Refuses("an assignment with a duplicate source", DocRealizedCodes.AssignmentsInvalid, wall, () => DocRealized.Assignments(Assign((wall, "doc-up-wall"), (wall, "doc-up-door"))));
        Refuses("an assignment whose step is not text", DocRealizedCodes.AssignmentsInvalid, wall, () => DocRealized.Assignments("{" + JsonSerializer.Serialize(wall) + ":1}"));
        Refuses("an assignment whose step is empty", DocRealizedCodes.AssignmentsInvalid, wall, () => DocRealized.Assignments(Assign((wall, ""))));
        Refuses("an assignment over the bound", DocRealizedCodes.BoundExceeded, "assignments",
            () => DocRealized.Assignments(Assign(Enumerable.Range(0, DocRealized.MaxAssignments + 1).Select(i => ("doc:k" + i.ToString(CultureInfo.InvariantCulture), "s")).ToArray())));

        // ---- Refusals: building ----
        Refuses("a source that is neither doc: nor element:", DocRealizedCodes.SourceInvalid, "wall:" + K(Role("south")),
            () => From(record, Assign(("wall:" + K(Role("south")), "doc-up-wall"))));
        Refuses("an element source not in canonical D form", DocRealizedCodes.SourceInvalid, "element:" + Role("south").ToString("D").ToUpperInvariant(),
            () => From(record, Assign(("element:" + Role("south").ToString("D").ToUpperInvariant(), "doc-up-wall"))));
        Refuses("an element source in N form", DocRealizedCodes.SourceInvalid, "element:" + K(Role("south")),
            () => From(record, Assign(("element:" + K(Role("south")), "doc-up-wall"))));
        Refuses("an empty doc key", DocRealizedCodes.SourceInvalid, "doc:", () => From(record, Assign(("doc:", "doc-up-plan"))));
        var stranger = DocRegeneration.ElementSource(Id("stranger"));
        Refuses("an element the fixture does not have", DocRealizedCodes.SourceUnknown, stranger, () => From(record, Assign((stranger, "doc-up-wall"))));
        Refuses("a doc key the default set does not have", DocRealizedCodes.SourceUnknown, "doc:plan-nowhere", () => From(record, Assign(("doc:plan-nowhere", "doc-up-plan"))));
        Refuses("a step the record does not have", DocRealizedCodes.StepUnknown, wall, () => From(record, Assign((wall, "doc-up-missing"))));
        Refuses("a step that failed", DocRealizedCodes.StepNotSucceeded, wall, () => From(record, Assign((wall, "doc-up-failed"))));
        Refuses("a step that was blocked", DocRealizedCodes.StepNotSucceeded, wall, () => From(record, Assign((wall, "doc-up-blocked"))));
        Refuses("a step whose reply succeeded but did not pass", DocRealizedCodes.StepNotSucceeded, wall, () => From(record, Assign((wall, "doc-up-refused-pass"))));
        Refuses("a step that created nothing", DocRealizedCodes.StepNoCreated, wall, () => From(record, Assign((wall, "q-levels"))));
        var later = string.CompareOrdinal(wall, door) > 0 ? wall : door;
        Refuses("two sources on one step (the later source, ordinal, is refused)", DocRealizedCodes.UniqueIdDuplicate, later,
            () => From(record, Assign((wall, "doc-up-door"), (door, "doc-up-door"))));
        var twins = new[] { new DocRealizedStep("a", true, new[] { Uid("same") }, Array.Empty<string>()), new DocRealizedStep("b", true, new[] { Uid("same").ToUpperInvariant() }, Array.Empty<string>()) };
        Refuses("two steps reporting one UniqueId (ignoring case)", DocRealizedCodes.UniqueIdDuplicate, later,
            () => DocRealized.Build(fixture, twins, new Dictionary<string, string> { [door] = "a", [wall] = "b" }));
        Refuses("Build refuses duplicate step keys", DocRealizedCodes.StepDuplicate, "a",
            () => DocRealized.Build(fixture, new[] { new DocRealizedStep("a", true, new[] { Uid("x") }, Array.Empty<string>()), new DocRealizedStep("a", true, new[] { Uid("y") }, Array.Empty<string>()) }, new Dictionary<string, string> { [wall] = "a" }));
        Refuses("Build refuses a created entry that is not a UniqueId", DocRealizedCodes.UniqueIdInvalid, wall,
            () => DocRealized.Build(fixture, new[] { new DocRealizedStep("a", true, new[] { "bad" }, Array.Empty<string>()) }, new Dictionary<string, string> { [wall] = "a" }));
        Refuses("Canonical refuses a source that is neither doc: nor element:", DocRealizedCodes.SourceInvalid, "x",
            () => DocRealized.Canonical(new Dictionary<string, string> { ["x"] = Uid("x") }));
        Refuses("Canonical refuses a value that is not a UniqueId", DocRealizedCodes.UniqueIdInvalid, wall,
            () => DocRealized.Canonical(new Dictionary<string, string> { [wall] = "nope" }));
        Refuses("Canonical refuses one UniqueId on two sources", DocRealizedCodes.UniqueIdDuplicate, later,
            () => DocRealized.Canonical(new Dictionary<string, string> { [door] = Uid("d"), [wall] = Uid("d").ToUpperInvariant() }));
        // ---- Replies without created (journal.read), deleted UniqueIds, the record's document, bounds, the fixture ----
        System.Text.Json.Nodes.JsonNode StepOf(System.Text.Json.Nodes.JsonObject root, int i) => root["steps"]![i]!;
        var journal = Edit(Record(new S("j-baseline", true, false, "succeeded", Array.Empty<string>()), new S("doc-up-wall", true, false, "succeeded", new[] { Uid("wall") })),
            root => { StepOf(root, 0)["response"] = System.Text.Json.Nodes.JsonNode.Parse("{\"status\":\"succeeded\",\"verification\":\"unverified\",\"attached\":true,\"report\":{}}"); return null; });
        IReadOnlyList<DocRealizedStep> journalSteps = Array.Empty<DocRealizedStep>();
        var journalCode = Code(() => journalSteps = DocRealized.Steps(journal));
        Check(journalCode is null && journalSteps.Count == 2 && journalSteps[0].Succeeded && journalSteps[0].Created.Count == 0 && journalSteps[0].Deleted.Count == 0,
            "a journal.read reply (no created member) is read as a succeeded step that created nothing", journalCode ?? "");
        Refuses("a source assigned to a journal.read step created nothing", DocRealizedCodes.StepNoCreated, wall, () => From(journal, Assign((wall, "j-baseline"))));
        var journalMap = (IReadOnlyDictionary<string, string>?)null;
        Check(Code(() => journalMap = From(journal, Assign((wall, "doc-up-wall")))) is null && journalMap is not null && journalMap[wall] == Uid("wall"),
            "a record with a journal.read step still realizes its other steps");
        DocRealizedRecord? nullCreated = null;
        Check(Code(() => nullCreated = DocRealized.Read(Edit(one, root => StepOf(root, 0)["response"]!["created"] = null))) is null && nullCreated is { Steps.Count: 1 } && nullCreated.Steps[0].Created.Count == 0,
            "created: null is read as nothing created");
        DocRealizedRecord? notJson = null;
        Check(Code(() => notJson = DocRealized.Read(Edit(one, root => { var step = StepOf(root, 0); step["response"] = null; step["raw_response"] = "not json"; step["passed"] = false; step["reason"] = "response is not JSON"; return null; })))
            is null && notJson is { Steps.Count: 1 } && !notJson.Steps[0].Succeeded && notJson.Steps[0].Created.Count == 0,
            "a reply that was not JSON (response null, raw_response kept) is read as a step that did not succeed");
        Refuses("a deleted that is not an array", DocRealizedCodes.RecordInvalid, "steps[0].response.deleted", () => DocRealized.Steps(Edit(one, root => StepOf(root, 0)["response"]!["deleted"] = "x")));
        Refuses("a deleted entry that is not a UniqueId", DocRealizedCodes.UniqueIdInvalid, "steps[0].response.deleted[0]",
            () => DocRealized.Steps(Edit(one, root => StepOf(root, 0)["response"]!["deleted"] = new System.Text.Json.Nodes.JsonArray("bad"))));
        Check(Code(() => DocRealized.Steps(Edit(one, root => StepOf(root, 0)["response"]!["deleted"] = null))) is null, "deleted: null is read as nothing deleted");

        var made = new S("doc-up-wall", true, false, "succeeded", new[] { Uid("wall") });
        var probe = new S("doc-up-probe-wall", true, false, "succeeded", Array.Empty<string>(), Array.Empty<string>(), true);
        var deleting = Record(made, probe, new S("doc-up-delete-wall", true, false, "succeeded", Array.Empty<string>(), new[] { Uid("wall").ToUpperInvariant(), Uid("other") }));
        DocRealizedRecord? deletingRecord = null;
        Check(Code(() => deletingRecord = DocRealized.Read(deleting)) is null && deletingRecord is { Steps.Count: 3 } && deletingRecord.Steps[2].Deleted.SequenceEqual(new[] { Uid("wall").ToUpperInvariant(), Uid("other") })
            && deletingRecord.Steps[1].Deleted.Count == 0, "Steps keeps the UniqueIds a succeeded reply deleted, in order");
        Refuses("a created UniqueId a later succeeded reply deleted (ignoring case)", DocRealizedCodes.UniqueIdDeleted, wall, () => From(deleting, Assign((wall, "doc-up-wall"))));
        Check(Code(() => From(Record(made, probe), Assign((wall, "doc-up-wall")))) is null, "a dry-run delete (its reply lists nothing deleted) does not retire a created UniqueId");
        var rolledBack = Record(made, new S("doc-up-delete-wall", false, false, "failed", Array.Empty<string>(), new[] { Uid("wall") }));
        DocRealizedRecord? rolledRecord = null;
        Check(Code(() => rolledRecord = DocRealized.Read(rolledBack)) is null && rolledRecord is { Steps.Count: 2 } && rolledRecord.Steps[1].Deleted.Count == 0
            && Code(() => From(rolledBack, Assign((wall, "doc-up-wall")))) is null, "a failed reply deletes nothing (its transaction rolled back)");
        Refuses("Build refuses a UniqueId another step deleted", DocRealizedCodes.UniqueIdDeleted, wall,
            () => DocRealized.Build(fixture, new[] { new DocRealizedStep("a", true, new[] { Uid("x") }, Array.Empty<string>()), new DocRealizedStep("b", true, Array.Empty<string>(), new[] { Uid("x") }) }, new Dictionary<string, string> { [wall] = "a" }));

        var docId = "";
        Check(Code(() => docId = DocRealized.Read(record).DocumentUniqueId) is null && docId == RecordDocument, "Read exposes the record's document_unique_id", docId);
        Refuses("a record for another document", DocRealizedCodes.DocumentMismatch, "document_unique_id",
            () => DocRealized.FromRecord(fixture, record, assignments, Uid("another document")));
        Check(Code(() => DocRealized.FromRecord(fixture, record, assignments, RecordDocument.ToUpperInvariant())) is null, "the record's document matches ignoring case");
        Refuses("an expected document that is not a UniqueId", DocRealizedCodes.DocumentMismatch, "expected", () => DocRealized.FromRecord(fixture, record, assignments, "nope"));
        Refuses("a record without document_unique_id", DocRealizedCodes.RecordInvalid, "document_unique_id", () => DocRealized.Read(Edit(one, root => Drop(root, "document_unique_id"))));
        Refuses("a record whose document_unique_id is not a UniqueId", DocRealizedCodes.RecordInvalid, "document_unique_id", () => DocRealized.Read(Edit(one, root => root["document_unique_id"] = "x")));

        var huge = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < 9000; i++) huge["doc:" + i.ToString(CultureInfo.InvariantCulture) + new string('k', 1000)] = Uid("huge" + i.ToString(CultureInfo.InvariantCulture));
        Refuses("a canonical map over the doc-update --realized byte cap", DocRealizedCodes.BoundExceeded, "realized", () => DocRealized.Canonical(huge));

        var broken = fixture with { Elements = fixture.Elements.Select(element => element.Id == doorId ? element with { Dependencies = new[] { Id("missing") } } : element).ToArray() };
        Refuses("an invalid fixture is refused even for an element-only assignment", DocRealizedCodes.FixtureInvalid, DocDefaultsCodes.InvalidIr,
            () => DocRealized.FromRecord(broken, record, Assign((wall, "doc-up-wall")), RecordDocument));
        Refuses("Build refuses a step with a null key", DocRealizedCodes.RecordInvalid, "steps",
            () => DocRealized.Build(fixture, new[] { new DocRealizedStep(null!, true, new[] { Uid("x") }, Array.Empty<string>()) }, new Dictionary<string, string> { [wall] = "a" }));
        Refuses("Build refuses a step with null created", DocRealizedCodes.RecordInvalid, "steps",
            () => DocRealized.Build(fixture, new[] { new DocRealizedStep("a", true, null!, Array.Empty<string>()) }, new Dictionary<string, string> { [wall] = "a" }));
        Refuses("Build refuses a step with null deleted", DocRealizedCodes.RecordInvalid, "steps",
            () => DocRealized.Build(fixture, new[] { new DocRealizedStep("a", true, new[] { Uid("x") }, null!) }, new Dictionary<string, string> { [wall] = "a" }));
        var accented = "";
        Check(Code(() => accented = DocRealized.Canonical(new Dictionary<string, string> { ["doc:pla\u00f1o"] = Uid("n") })) is null && accented.All(c => c < 128) && accented.Contains("\\u00", StringComparison.OrdinalIgnoreCase)
            && JsonSerializer.Deserialize<Dictionary<string, string>>(accented)!.Single().Key == "doc:pla\u00f1o", "a non-ASCII doc key is escaped and reads back unchanged", accented);
        var replay = Record(new S("wall-arc", true, false, "succeeded", new[] { Uid("arc") }), new S("wall-arc-retry", true, false, "succeeded", new[] { Uid("arc") }));
        Refuses("a replayed step reporting the same created element (wall-arc / wall-arc-retry)", DocRealizedCodes.UniqueIdDuplicate, later,
            () => From(replay, Assign((wall, "wall-arc"), (door, "wall-arc-retry"))));

        Check(Code(() => DocRealized.Steps(null!)) == "null" && Code(() => DocRealized.Assignments(null!)) == "null"
            && Code(() => DocRealized.Build(null!, parsed, expected)) == "null" && Code(() => DocRealized.Build(fixture, null!, expected)) == "null"
            && Code(() => DocRealized.Build(fixture, parsed, null!)) == "null" && Code(() => DocRealized.Canonical(null!)) == "null",
            "null inputs are refused with ArgumentNullException");
    }
}
