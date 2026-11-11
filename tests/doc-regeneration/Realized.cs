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

    sealed record S(string Key, bool Passed, bool Blocked, string? Status, string[] Created);

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
            ["request"] = new { command = "doc.create" },
            ["response"] = step.Status is null ? null : new Dictionary<string, object?>
            {
                ["status"] = step.Status, ["code"] = step.Status == "succeeded" ? "ok" : "refused", ["field"] = null, ["detail"] = null,
                ["created"] = step.Created, ["modified"] = Array.Empty<string>(), ["deleted"] = Array.Empty<string>(),
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
            ["schema_version"] = 1, ["run"] = 1, ["document_path"] = "C:/x/BIM-Agentic-Test.rvt", ["copy_path"] = null,
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
        var buildCode = Code(() => map = DocRealized.FromRecord(fixture, record, assignments));
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
        var again = Code(() => canonical = DocRealized.Canonical(DocRealized.FromRecord(fixture, record, reordered)));
        Check(again is null && canonical == expectedJson, "the same input in another member order gives the same bytes", again ?? canonical);
        var unsorted = new Dictionary<string, string>(StringComparer.Ordinal) { [tag] = Uid("tag"), [door] = Uid("door"), [wall] = Uid("wall"), [plan] = Uid("plan") };
        Check(Code(() => canonical = DocRealized.Canonical(unsorted)) is null && canonical == expectedJson, "Canonical sorts any map it is given", canonical);
        Check(Code(() => canonical = DocRealized.Canonical(new Dictionary<string, string>())) is null && canonical == "{}", "an empty map is {}", canonical);
        Check(Code(() => canonical = DocRealized.FromRecord(fixture, record, "{}") is { Count: 0 } empty ? DocRealized.Canonical(empty) : "nonempty") is null && canonical == "{}",
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
        var updateCode = Code(() => { full = DocRealized.FromRecord(fixture, fullRecord, fullAssign); updatePlan = DocUpdate.Plan(fixture, fixture, bindings, full); });
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
        Refuses("a response without created", DocRealizedCodes.RecordInvalid, "steps[0].response.created", () => DocRealized.Steps(Edit(one, root => Drop(root["steps"]![0]!["response"]!, "created"))));
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
            () => DocRealized.FromRecord(fixture, record, Assign(("wall:" + K(Role("south")), "doc-up-wall"))));
        Refuses("an element source not in canonical D form", DocRealizedCodes.SourceInvalid, "element:" + Role("south").ToString("D").ToUpperInvariant(),
            () => DocRealized.FromRecord(fixture, record, Assign(("element:" + Role("south").ToString("D").ToUpperInvariant(), "doc-up-wall"))));
        Refuses("an element source in N form", DocRealizedCodes.SourceInvalid, "element:" + K(Role("south")),
            () => DocRealized.FromRecord(fixture, record, Assign(("element:" + K(Role("south")), "doc-up-wall"))));
        Refuses("an empty doc key", DocRealizedCodes.SourceInvalid, "doc:", () => DocRealized.FromRecord(fixture, record, Assign(("doc:", "doc-up-plan"))));
        var stranger = DocRegeneration.ElementSource(Id("stranger"));
        Refuses("an element the fixture does not have", DocRealizedCodes.SourceUnknown, stranger, () => DocRealized.FromRecord(fixture, record, Assign((stranger, "doc-up-wall"))));
        Refuses("a doc key the default set does not have", DocRealizedCodes.SourceUnknown, "doc:plan-nowhere", () => DocRealized.FromRecord(fixture, record, Assign(("doc:plan-nowhere", "doc-up-plan"))));
        Refuses("a step the record does not have", DocRealizedCodes.StepUnknown, wall, () => DocRealized.FromRecord(fixture, record, Assign((wall, "doc-up-missing"))));
        Refuses("a step that failed", DocRealizedCodes.StepNotSucceeded, wall, () => DocRealized.FromRecord(fixture, record, Assign((wall, "doc-up-failed"))));
        Refuses("a step that was blocked", DocRealizedCodes.StepNotSucceeded, wall, () => DocRealized.FromRecord(fixture, record, Assign((wall, "doc-up-blocked"))));
        Refuses("a step whose reply succeeded but did not pass", DocRealizedCodes.StepNotSucceeded, wall, () => DocRealized.FromRecord(fixture, record, Assign((wall, "doc-up-refused-pass"))));
        Refuses("a step that created nothing", DocRealizedCodes.StepNoCreated, wall, () => DocRealized.FromRecord(fixture, record, Assign((wall, "q-levels"))));
        var later = string.CompareOrdinal(wall, door) > 0 ? wall : door;
        Refuses("two sources on one step (the later source, ordinal, is refused)", DocRealizedCodes.UniqueIdDuplicate, later,
            () => DocRealized.FromRecord(fixture, record, Assign((wall, "doc-up-door"), (door, "doc-up-door"))));
        var twins = new[] { new DocRealizedStep("a", true, new[] { Uid("same") }), new DocRealizedStep("b", true, new[] { Uid("same").ToUpperInvariant() }) };
        Refuses("two steps reporting one UniqueId (ignoring case)", DocRealizedCodes.UniqueIdDuplicate, later,
            () => DocRealized.Build(fixture, twins, new Dictionary<string, string> { [door] = "a", [wall] = "b" }));
        Refuses("Build refuses duplicate step keys", DocRealizedCodes.StepDuplicate, "a",
            () => DocRealized.Build(fixture, new[] { new DocRealizedStep("a", true, new[] { Uid("x") }), new DocRealizedStep("a", true, new[] { Uid("y") }) }, new Dictionary<string, string> { [wall] = "a" }));
        Refuses("Build refuses a created entry that is not a UniqueId", DocRealizedCodes.UniqueIdInvalid, wall,
            () => DocRealized.Build(fixture, new[] { new DocRealizedStep("a", true, new[] { "bad" }) }, new Dictionary<string, string> { [wall] = "a" }));
        Refuses("Canonical refuses a source that is neither doc: nor element:", DocRealizedCodes.SourceInvalid, "x",
            () => DocRealized.Canonical(new Dictionary<string, string> { ["x"] = Uid("x") }));
        Refuses("Canonical refuses a value that is not a UniqueId", DocRealizedCodes.UniqueIdInvalid, wall,
            () => DocRealized.Canonical(new Dictionary<string, string> { [wall] = "nope" }));
        Refuses("Canonical refuses one UniqueId on two sources", DocRealizedCodes.UniqueIdDuplicate, later,
            () => DocRealized.Canonical(new Dictionary<string, string> { [door] = Uid("d"), [wall] = Uid("d").ToUpperInvariant() }));
        Check(Code(() => DocRealized.Steps(null!)) == "null" && Code(() => DocRealized.Assignments(null!)) == "null"
            && Code(() => DocRealized.Build(null!, parsed, expected)) == "null" && Code(() => DocRealized.Build(fixture, null!, expected)) == "null"
            && Code(() => DocRealized.Build(fixture, parsed, null!)) == "null" && Code(() => DocRealized.Canonical(null!)) == "null",
            "null inputs are refused with ArgumentNullException");
    }
}
