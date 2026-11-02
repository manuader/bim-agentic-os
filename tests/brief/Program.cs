using Bim.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
// P6-03 project brief contract oracle: strict parser refusals with JSON paths, every validator branch, canonical
// serializer determinism and round trip, the checked-in example and the JSON Schema document. Unit-tested evidence only.
int passed = 0, failed = 0;

void Check(bool condition, string name, string detail = "")
{
    if (condition) { Console.WriteLine("PASS " + name); passed++; return; }
    Console.WriteLine("::error::brief FAIL " + name + (detail.Length > 0 ? " | " + detail : ""));
    failed++;
}

string RepoRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
    }
    throw new DirectoryNotFoundException("repository root with AGENTS.md not found above " + AppContext.BaseDirectory);
}

JsonObject Minimal() => new()
{
    ["brief_version"] = 1,
    ["name"] = "Test brief",
    ["constraints"] = new JsonObject(),
    ["rooms"] = new JsonArray(new JsonObject { ["id"] = "living", ["name"] = "Living" }, new JsonObject { ["id"] = "kitchen", ["name"] = "Kitchen" }),
};
JsonObject Prov(string source, double confidence) => new() { ["source"] = source, ["confidence"] = confidence };
JsonObject Sourced(JsonNode value, JsonObject provenance) => new() { ["value"] = value, ["provenance"] = provenance };
JsonObject Adj(string a, string b, string kind) => new() { ["a"] = a, ["b"] = b, ["kind"] = kind };
JsonObject RoomAt(JsonObject brief, int index) => (JsonObject)brief["rooms"]![index]!;

BriefException? Refusal(string json)
{
    try { BriefParser.Parse(json); return null; }
    catch (BriefException error) { return error; }
    catch (Exception error) { return new BriefException("unexpected " + error.GetType().Name, null, error.Message); }
}
void RejectsText(string name, string json, string code, string? field = null)
{
    var error = Refusal(json);
    Check(error is not null && error.Code == code && error.Message == code && (field is null || error.Field == field), name,
        error is null ? "accepted" : error.Code + " at " + error.Field + " (" + error.Detail + ")");
}
void Rejects(string name, JsonNode brief, string code, string field) => RejectsText(name, brief.ToJsonString(), code, field);
ProjectBrief? Accepts(string name, string json)
{
    try { var parsed = BriefParser.Parse(json); Check(true, name); return parsed; }
    catch (Exception error) { Check(false, name, error.GetType().Name + " " + error.Message + (error is BriefException typed ? " at " + typed.Field + " (" + typed.Detail + ")" : "")); return null; }
}
BriefIssue[] IssuesOf(JsonNode brief)
{
    try { return BriefValidator.Validate(BriefParser.Parse(brief.ToJsonString())); }
    catch (BriefException error) { return new[] { new BriefIssue("parse " + error.Code, error.Field ?? "", BriefSeverity.Error, error.Detail ?? "") }; }
}
string Describe(IEnumerable<BriefIssue> issues) => string.Join("; ", issues.Select(issue => issue.Code + "@" + issue.Field + "/" + issue.Severity));
void Flags(string name, JsonNode brief, string code, string field, BriefSeverity severity = BriefSeverity.Error)
{
    var issues = IssuesOf(brief);
    Check(issues.Any(issue => issue.Code == code && issue.Field == field && issue.Severity == severity && issue.Detail.Length > 0), name, Describe(issues));
}
void Clean(string name, JsonNode brief)
{
    var issues = IssuesOf(brief);
    Check(issues.Length == 0, name, Describe(issues));
}
JsonObject With(Action<JsonObject> change) { var brief = Minimal(); change(brief); return brief; }

try
{
    var root = RepoRoot();
    // ---- Checked-in example ----
    var examplePath = Path.Combine(root, "examples", "brief", "apartment-2br.json");
    Check(File.Exists(examplePath), "example examples/brief/apartment-2br.json exists", examplePath);
    var exampleText = File.ReadAllText(examplePath);
    var example = Accepts("example parses", exampleText);
    var exampleIssues = example is null ? Array.Empty<BriefIssue>() : BriefValidator.Validate(example);
    Check(example is not null && exampleIssues.Length == 0 && !BriefValidator.HasErrors(exampleIssues), "example validates with no issues", Describe(exampleIssues));
    Check(example is { Version: 1, Rooms.Count: 7, Adjacencies.Count: 5 } && example.Name == "Two-bedroom apartment, CABA", "example binds version, name, 7 rooms and 5 adjacencies");
    Check(example?.Constraints.Jurisdiction is { Value: "CABA", Provenance: { Source: BriefSource.User, Confidence: 1 } }, "example jurisdiction CABA carries user provenance");
    Check(example?.Constraints.TotalAreaBudgetSquareMetres is { Value: 75, Provenance: { Source: BriefSource.Document, Confidence: 0.8, Page: 2 } } budget
        && budget.Provenance!.Reference!.Contains("75 m2", StringComparison.Ordinal), "example budget 75 m2 carries document provenance with page and reference");
    Check(example?.Constraints.Site is { WidthMetres.Value: 8.66, DepthMetres.Value: 20 } && example.Constraints.Levels is { Value: 1, Provenance: null }, "example site 8.66 x 20 m and bare levels 1");
    var laundry = example?.Rooms.FirstOrDefault(room => room.Id == "laundry");
    Check(laundry is { Provenance.Source: BriefSource.Inferred, TargetAreaSquareMetres: { Value: 3, Provenance.Confidence: 0.5 }, MinAreaSquareMetres: { Value: 2, Provenance: null } },
        "inferred laundry room keeps room-level and value-level provenance; bare min has none");
    Check(example?.Rooms.First(room => room.Id == "bathroom") is { EffectiveCount: 1, Count: { Value: 1, Provenance.Source: BriefSource.User } }
        && example.Rooms.First(room => room.Id == "kitchen") is { EffectiveCount: 1, Count: null, MaxAreaSquareMetres.Value: 10 }, "count binds when given and defaults to 1");
    Check(example?.Adjacencies[4] is { A: "kitchen", B: "bedroom_main", Kind: BriefAdjacencyKind.Forbidden, Provenance.Confidence: 0.9 }, "forbidden adjacency binds kind and provenance");

    // ---- Canonical serializer ----
    if (example is not null)
    {
        var canonical = BriefSerializer.Serialize(example);
        var reparsed = BriefParser.Parse(canonical);
        Check(BriefSerializer.Serialize(reparsed) == canonical, "canonical form is round-trip stable", canonical);
        Check(BriefSerializer.Serialize(example) == canonical, "serializer is deterministic across calls");
        Check(BriefValidator.Validate(reparsed).Length == 0 && reparsed.Rooms.Count == example.Rooms.Count && reparsed.Rooms.Select(room => room.Id).SequenceEqual(example.Rooms.Select(room => room.Id)),
            "round trip preserves rooms, order and validity");
        Check(!canonical.Contains('\n', StringComparison.Ordinal) && canonical.StartsWith("{\"brief_version\":1,\"name\":", StringComparison.Ordinal),
            "canonical form is compact and starts with brief_version then name", canonical[..Math.Min(80, canonical.Length)]);
        Check(canonical.Contains("\"levels\":1,", StringComparison.Ordinal) && canonical.Contains("\"min_area_m2\":2,", StringComparison.Ordinal)
            && canonical.Contains("\"jurisdiction\":{\"value\":\"CABA\",\"provenance\":{\"source\":\"user\",\"confidence\":1,", StringComparison.Ordinal),
            "values without provenance are bare; sourced values keep value then provenance", canonical);
        var reordered = JsonNode.Parse(exampleText)!.AsObject();
        var shuffled = new JsonObject();
        foreach (var name in new[] { "adjacencies", "rooms", "constraints", "name", "brief_version" }) shuffled[name] = reordered[name]!.DeepClone();
        Check(BriefSerializer.Serialize(BriefParser.Parse(shuffled.ToJsonString())) == canonical, "member order does not change the canonical form");
    }
    var negativeZero = With(brief => RoomAt(brief, 0)["target_area_m2"] = Sourced(JsonValue.Create(12.0), Prov("document", -0.0)));
    var negativeZeroText = negativeZero.ToJsonString().Replace("\"confidence\":0", "\"confidence\":-0", StringComparison.Ordinal);
    var zeroCanonical = BriefSerializer.Serialize(BriefParser.Parse(negativeZeroText));
    Check(zeroCanonical.Contains("\"confidence\":0}", StringComparison.Ordinal) && !zeroCanonical.Contains("-0", StringComparison.Ordinal), "-0 is written as 0", zeroCanonical);
    var minimalCanonical = BriefSerializer.Serialize(BriefParser.Parse(Minimal().ToJsonString()));
    Check(minimalCanonical == "{\"brief_version\":1,\"name\":\"Test brief\",\"constraints\":{},\"rooms\":[{\"id\":\"living\",\"name\":\"Living\"},{\"id\":\"kitchen\",\"name\":\"Kitchen\"}],\"adjacencies\":[]}",
        "minimal brief canonical form omits absent optionals and writes empty adjacencies", minimalCanonical);
    Check(BriefSerializer.Serialize(BriefParser.Parse(minimalCanonical)) == minimalCanonical, "minimal canonical form is round-trip stable");

    // A brief built in code (the space planner path) serializes and parses back to the same canonical form.
    var built = new ProjectBrief(ProjectBrief.CurrentVersion, "Built in code",
        new BriefConstraints(new BriefValue<string>("CABA"), new BriefSite(new BriefValue<double>(10), new BriefValue<double>(30, new BriefProvenance(BriefSource.Document, 0.7, "plano.dwg", 3))),
            new BriefValue<int>(2), new BriefValue<double>(120.5)),
        new[]
        {
            new BriefRoom("studio", "Studio", new BriefValue<int>(2), new BriefValue<double>(30), new BriefValue<double>(25), new BriefValue<double>(35), new BriefValue<int>(1)),
            new BriefRoom("terrace", "Terrace \"roof\" ñ", Provenance: new BriefProvenance(BriefSource.Inferred, 0.25, "render")),
        },
        new[] { new BriefAdjacency("studio", "terrace", BriefAdjacencyKind.Preferred, new BriefProvenance(BriefSource.User, 1)) });
    var builtIssues = BriefValidator.Validate(built);
    Check(builtIssues.Length == 0, "brief built in code validates", Describe(builtIssues));
    var builtText = BriefSerializer.Serialize(built);
    var builtBack = Accepts("brief built in code serializes to parseable JSON", builtText);
    Check(builtBack is not null && BriefSerializer.Serialize(builtBack) == builtText && builtBack.Rooms[1].Name == "Terrace \"roof\" ñ"
        && builtBack.Constraints.Site is { DepthMetres.Provenance: { Reference: "plano.dwg", Page: 3, Confidence: 0.7 } } && builtBack.Rooms[0] is { EffectiveCount: 2, PreferredLevel.Value: 1 },
        "built brief round-trips values, escapes and provenance", builtText);
    Check(CodeOfSerialize(built with { Constraints = new BriefConstraints(TotalAreaBudgetSquareMetres: new BriefValue<double>(double.NaN)) }) == nameof(ArgumentException), "serializer refuses non-finite numbers");

    // ---- Parser refusals ----
    RejectsText("non-JSON brief", "not json", BriefCodes.JsonInvalid);
    RejectsText("trailing comma", "{\"brief_version\":1,}", BriefCodes.JsonInvalid);
    RejectsText("comment", "{\"brief_version\":1 /* c */}", BriefCodes.JsonInvalid);
    RejectsText("array root", "[]", BriefCodes.TypeInvalid);
    RejectsText("too large input", "{\"name\":\"" + new string('x', BriefParser.MaxInputChars) + "\"}", BriefCodes.InputTooLarge);
    RejectsText("nesting deeper than the bound", new string('[', BriefParser.MaxDepth + 2) + new string(']', BriefParser.MaxDepth + 2), BriefCodes.JsonInvalid);
    RejectsText("lone surrogate string", Minimal().ToJsonString().Replace("Test brief", "bad \\ud800 name", StringComparison.Ordinal), BriefCodes.JsonInvalid);
    Rejects("missing brief_version", With(brief => brief.Remove("brief_version")), BriefCodes.VersionUnsupported, "brief_version");
    Rejects("brief_version 2", With(brief => brief["brief_version"] = 2), BriefCodes.VersionUnsupported, "brief_version");
    Rejects("brief_version as string", With(brief => brief["brief_version"] = "1"), BriefCodes.VersionUnsupported, "brief_version");
    Rejects("unknown root member", With(brief => brief["colour"] = "red"), BriefCodes.MemberUnknown, "colour");
    RejectsText("duplicate root member", "{\"name\":\"x\"," + Minimal().ToJsonString()[1..], BriefCodes.MemberDuplicate, "name");
    Rejects("missing name", With(brief => brief.Remove("name")), BriefCodes.MemberMissing, "name");
    Rejects("missing constraints", With(brief => brief.Remove("constraints")), BriefCodes.MemberMissing, "constraints");
    Rejects("missing rooms", With(brief => brief.Remove("rooms")), BriefCodes.MemberMissing, "rooms");
    Rejects("blank name", With(brief => brief["name"] = "  "), BriefCodes.ValueInvalid, "name");
    Rejects("over-long name", With(brief => brief["name"] = new string('n', BriefParser.MaxStringChars + 1)), BriefCodes.ValueInvalid, "name");
    Rejects("name as number", With(brief => brief["name"] = 5), BriefCodes.TypeInvalid, "name");
    Rejects("rooms as object", With(brief => brief["rooms"] = new JsonObject()), BriefCodes.TypeInvalid, "rooms");
    Rejects("constraints as array", With(brief => brief["constraints"] = new JsonArray()), BriefCodes.TypeInvalid, "constraints");
    Rejects("room as string", With(brief => brief["rooms"]!.AsArray().Add((JsonNode)"bath")), BriefCodes.TypeInvalid, "rooms[2]");
    Rejects("unknown room member", With(brief => RoomAt(brief, 1)["colour"] = "red"), BriefCodes.MemberUnknown, "rooms[1].colour");
    Rejects("missing room id", With(brief => RoomAt(brief, 0).Remove("id")), BriefCodes.MemberMissing, "rooms[0].id");
    Rejects("missing room name", With(brief => RoomAt(brief, 1).Remove("name")), BriefCodes.MemberMissing, "rooms[1].name");
    Rejects("room id with upper case and space", With(brief => RoomAt(brief, 0)["id"] = "Living Room"), BriefCodes.ValueInvalid, "rooms[0].id");
    Rejects("room id over 64 characters", With(brief => RoomAt(brief, 0)["id"] = new string('a', 65)), BriefCodes.ValueInvalid, "rooms[0].id");
    Rejects("area as string", With(brief => RoomAt(brief, 0)["target_area_m2"] = "20"), BriefCodes.TypeInvalid, "rooms[0].target_area_m2");
    Rejects("area as null", With(brief => RoomAt(brief, 0)["min_area_m2"] = null), BriefCodes.TypeInvalid, "rooms[0].min_area_m2");
    RejectsText("area overflowing double", Minimal().ToJsonString().Replace("\"name\":\"Living\"", "\"name\":\"Living\",\"max_area_m2\":1e400", StringComparison.Ordinal), BriefCodes.TypeInvalid, "rooms[0].max_area_m2");
    Rejects("fractional count", With(brief => RoomAt(brief, 0)["count"] = 1.5), BriefCodes.TypeInvalid, "rooms[0].count");
    Rejects("fractional preferred level", With(brief => RoomAt(brief, 0)["preferred_level"] = 0.5), BriefCodes.TypeInvalid, "rooms[0].preferred_level");
    Rejects("sourced value without provenance", With(brief => RoomAt(brief, 0)["target_area_m2"] = new JsonObject { ["value"] = 20 }), BriefCodes.MemberMissing, "rooms[0].target_area_m2.provenance");
    Rejects("sourced value without value", With(brief => RoomAt(brief, 0)["target_area_m2"] = new JsonObject { ["provenance"] = Prov("user", 1) }), BriefCodes.MemberMissing, "rooms[0].target_area_m2.value");
    Rejects("unknown member in sourced value", With(brief => RoomAt(brief, 0)["target_area_m2"] = new JsonObject { ["value"] = 20, ["provenance"] = Prov("user", 1), ["unit"] = "m2" }), BriefCodes.MemberUnknown, "rooms[0].target_area_m2.unit");
    Rejects("sourced value of wrong type", With(brief => RoomAt(brief, 0)["target_area_m2"] = Sourced("20", Prov("user", 1))), BriefCodes.TypeInvalid, "rooms[0].target_area_m2.value");
    Rejects("unknown provenance source", With(brief => RoomAt(brief, 0)["target_area_m2"] = Sourced(20, Prov("guess", 0.5))), BriefCodes.ValueInvalid, "rooms[0].target_area_m2.provenance.source");
    Rejects("provenance without confidence", With(brief => RoomAt(brief, 0)["provenance"] = new JsonObject { ["source"] = "user" }), BriefCodes.MemberMissing, "rooms[0].provenance.confidence");
    Rejects("confidence as string", With(brief => RoomAt(brief, 0)["provenance"] = new JsonObject { ["source"] = "user", ["confidence"] = "high" }), BriefCodes.TypeInvalid, "rooms[0].provenance.confidence");
    Rejects("unknown provenance member", With(brief => { var provenance = Prov("document", 0.5); provenance["file"] = "a.pdf"; RoomAt(brief, 0)["provenance"] = provenance; }), BriefCodes.MemberUnknown, "rooms[0].provenance.file");
    Rejects("fractional page", With(brief => { var provenance = Prov("document", 0.5); provenance["page"] = 1.5; RoomAt(brief, 0)["provenance"] = provenance; }), BriefCodes.TypeInvalid, "rooms[0].provenance.page");
    Rejects("blank reference", With(brief => { var provenance = Prov("document", 0.5); provenance["reference"] = ""; RoomAt(brief, 0)["provenance"] = provenance; }), BriefCodes.ValueInvalid, "rooms[0].provenance.reference");
    Rejects("unknown constraint", With(brief => brief["constraints"]!["height_limit_m"] = 30), BriefCodes.MemberUnknown, "constraints.height_limit_m");
    Rejects("site without depth", With(brief => brief["constraints"]!["site"] = new JsonObject { ["width_m"] = 10 }), BriefCodes.MemberMissing, "constraints.site.depth_m");
    Rejects("jurisdiction as number", With(brief => brief["constraints"]!["jurisdiction"] = 1), BriefCodes.TypeInvalid, "constraints.jurisdiction");
    Rejects("levels fractional inside sourced value", With(brief => brief["constraints"]!["levels"] = Sourced(1.5, Prov("user", 1))), BriefCodes.TypeInvalid, "constraints.levels.value");
    Rejects("adjacencies as object", With(brief => brief["adjacencies"] = new JsonObject()), BriefCodes.TypeInvalid, "adjacencies");
    Rejects("unknown adjacency kind", With(brief => brief["adjacencies"] = new JsonArray(Adj("living", "kitchen", "near"))), BriefCodes.ValueInvalid, "adjacencies[0].kind");
    Rejects("adjacency without b", With(brief => { var adjacency = Adj("living", "kitchen", "required"); adjacency.Remove("b"); brief["adjacencies"] = new JsonArray(adjacency); }), BriefCodes.MemberMissing, "adjacencies[0].b");
    Rejects("unknown adjacency member", With(brief => { var adjacency = Adj("living", "kitchen", "required"); adjacency["weight"] = 2; brief["adjacencies"] = new JsonArray(Adj("living", "kitchen", "preferred"), adjacency); }), BriefCodes.MemberUnknown, "adjacencies[1].weight");
    RejectsText("duplicate member inside a room", Minimal().ToJsonString().Replace("\"id\":\"kitchen\"", "\"id\":\"kitchen\",\"id\":\"cook\"", StringComparison.Ordinal), BriefCodes.MemberDuplicate, "rooms[1].id");
    Check(BriefParser.Parse(Minimal().ToJsonString()).Adjacencies.Count == 0, "absent adjacencies parse as empty");
    Check(CodeOfParse(null!) == nameof(ArgumentNullException), "null input is ArgumentNullException");

    // ---- Validator ----
    Clean("minimal brief has no issues", Minimal());
    Flags("empty rooms", With(brief => brief["rooms"] = new JsonArray()), BriefCodes.RoomsEmpty, "rooms");
    Flags("duplicate room id", With(brief => RoomAt(brief, 1)["id"] = "living"), BriefCodes.RoomIdDuplicate, "rooms[1].id");
    Flags("zero target area", With(brief => RoomAt(brief, 0)["target_area_m2"] = 0), BriefCodes.AreaNonPositive, "rooms[0].target_area_m2");
    Flags("negative min area", With(brief => RoomAt(brief, 1)["min_area_m2"] = -4), BriefCodes.AreaNonPositive, "rooms[1].min_area_m2");
    Flags("negative sourced max area", With(brief => RoomAt(brief, 1)["max_area_m2"] = Sourced(-1, Prov("document", 0.5))), BriefCodes.AreaNonPositive, "rooms[1].max_area_m2");
    Flags("min area above max area", With(brief => { RoomAt(brief, 0)["min_area_m2"] = 20; RoomAt(brief, 0)["max_area_m2"] = 15; }), BriefCodes.AreaRangeInverted, "rooms[0].min_area_m2");
    Clean("min area equal to max area", With(brief => { RoomAt(brief, 0)["min_area_m2"] = 15; RoomAt(brief, 0)["max_area_m2"] = 15; RoomAt(brief, 0)["target_area_m2"] = 15; }));
    Flags("target below min", With(brief => { RoomAt(brief, 0)["min_area_m2"] = 20; RoomAt(brief, 0)["target_area_m2"] = 18; }), BriefCodes.AreaTargetOutsideRange, "rooms[0].target_area_m2");
    Flags("target above max", With(brief => { RoomAt(brief, 0)["max_area_m2"] = 20; RoomAt(brief, 0)["target_area_m2"] = Sourced(22, Prov("inferred", 0.4)); }), BriefCodes.AreaTargetOutsideRange, "rooms[0].target_area_m2");
    Flags("zero count", With(brief => RoomAt(brief, 0)["count"] = 0), BriefCodes.CountInvalid, "rooms[0].count");
    Flags("zero levels", With(brief => brief["constraints"]!["levels"] = 0), BriefCodes.LevelsInvalid, "constraints.levels");
    Flags("preferred level beyond levels", With(brief => { brief["constraints"]!["levels"] = 2; RoomAt(brief, 1)["preferred_level"] = 2; }), BriefCodes.LevelOutOfRange, "rooms[1].preferred_level");
    Clean("preferred level on the top level", With(brief => { brief["constraints"]!["levels"] = 2; RoomAt(brief, 1)["preferred_level"] = 1; }));
    Flags("negative preferred level", With(brief => RoomAt(brief, 0)["preferred_level"] = -1), BriefCodes.LevelOutOfRange, "rooms[0].preferred_level");
    Flags("zero site width", With(brief => brief["constraints"]!["site"] = new JsonObject { ["width_m"] = 0, ["depth_m"] = 20 }), BriefCodes.SiteDimensionNonPositive, "constraints.site.width_m");
    Flags("negative site depth", With(brief => brief["constraints"]!["site"] = new JsonObject { ["width_m"] = 10, ["depth_m"] = Sourced(-20, Prov("document", 0.9)) }), BriefCodes.SiteDimensionNonPositive, "constraints.site.depth_m");
    Flags("zero budget", With(brief => brief["constraints"]!["total_area_budget_m2"] = 0), BriefCodes.BudgetNonPositive, "constraints.total_area_budget_m2");
    var overBudget = With(brief =>
    {
        brief["constraints"]!["total_area_budget_m2"] = Sourced(50, Prov("document", 0.8));
        RoomAt(brief, 0)["min_area_m2"] = 20;
        RoomAt(brief, 1)["min_area_m2"] = 8;
        RoomAt(brief, 1)["count"] = 4;
    });
    Flags("sum of minimum areas times count above budget", overBudget, BriefCodes.BudgetExceeded, "constraints.total_area_budget_m2");
    Clean("sum of minimum areas equal to budget", With(brief =>
    {
        brief["constraints"]!["total_area_budget_m2"] = 52;
        RoomAt(brief, 0)["min_area_m2"] = 20;
        RoomAt(brief, 1)["min_area_m2"] = 8;
        RoomAt(brief, 1)["count"] = 4;
    }));
    Clean("target areas alone do not count against the budget", With(brief =>
    {
        brief["constraints"]!["total_area_budget_m2"] = 10;
        RoomAt(brief, 0)["target_area_m2"] = 30;
    }));
    Flags("confidence above 1", With(brief => RoomAt(brief, 0)["target_area_m2"] = Sourced(20, Prov("document", 1.2))), BriefCodes.ConfidenceOutOfRange, "rooms[0].target_area_m2.provenance.confidence");
    Flags("negative confidence on a room", With(brief => RoomAt(brief, 1)["provenance"] = Prov("inferred", -0.1)), BriefCodes.ConfidenceOutOfRange, "rooms[1].provenance.confidence");
    Flags("confidence above 1 on jurisdiction", With(brief => brief["constraints"]!["jurisdiction"] = Sourced("CABA", Prov("user", 2))), BriefCodes.ConfidenceOutOfRange, "constraints.jurisdiction.provenance.confidence");
    Flags("confidence above 1 on an adjacency", With(brief => { var adjacency = Adj("living", "kitchen", "required"); adjacency["provenance"] = Prov("user", 1.5); brief["adjacencies"] = new JsonArray(adjacency); }),
        BriefCodes.ConfidenceOutOfRange, "adjacencies[0].provenance.confidence");
    Clean("confidence bounds 0 and 1 are valid", With(brief => { RoomAt(brief, 0)["provenance"] = Prov("inferred", 0); RoomAt(brief, 1)["provenance"] = Prov("user", 1); }));
    Flags("page 0", With(brief => { var provenance = Prov("document", 0.5); provenance["page"] = 0; RoomAt(brief, 0)["provenance"] = provenance; }), BriefCodes.PageInvalid, "rooms[0].provenance.page");
    Flags("adjacency to unknown room", With(brief => brief["adjacencies"] = new JsonArray(Adj("living", "garage", "required"))), BriefCodes.AdjacencyRoomUnknown, "adjacencies[0].b");
    Flags("adjacency from unknown room", With(brief => brief["adjacencies"] = new JsonArray(Adj("living", "kitchen", "preferred"), Adj("attic", "kitchen", "preferred"))), BriefCodes.AdjacencyRoomUnknown, "adjacencies[1].a");
    Flags("room adjacent to itself", With(brief => brief["adjacencies"] = new JsonArray(Adj("kitchen", "kitchen", "required"))), BriefCodes.AdjacencySelf, "adjacencies[0]");
    Flags("required and forbidden on the same pair", With(brief => brief["adjacencies"] = new JsonArray(Adj("living", "kitchen", "required"), Adj("living", "kitchen", "forbidden"))),
        BriefCodes.AdjacencyContradictory, "adjacencies[1]");
    Flags("forbidden then required on the reversed pair", With(brief => brief["adjacencies"] = new JsonArray(Adj("kitchen", "living", "forbidden"), Adj("living", "kitchen", "required"))),
        BriefCodes.AdjacencyContradictory, "adjacencies[1]");
    Flags("preferred and forbidden on the same pair", With(brief => brief["adjacencies"] = new JsonArray(Adj("living", "kitchen", "preferred"), Adj("kitchen", "living", "forbidden"))),
        BriefCodes.AdjacencyContradictory, "adjacencies[1]");
    var repeated = With(brief => brief["adjacencies"] = new JsonArray(Adj("living", "kitchen", "required"), Adj("kitchen", "living", "required")));
    Flags("repeated adjacency is a warning", repeated, BriefCodes.AdjacencyDuplicate, "adjacencies[1]", BriefSeverity.Warning);
    Check(!BriefValidator.HasErrors(IssuesOf(repeated)), "a warning alone is not an error");
    Clean("distinct pairs with different kinds", With(brief =>
    {
        brief["rooms"]!.AsArray().Add((JsonNode)new JsonObject { ["id"] = "bath", ["name"] = "Bath" });
        brief["adjacencies"] = new JsonArray(Adj("living", "kitchen", "required"), Adj("kitchen", "bath", "forbidden"), Adj("living", "bath", "preferred"));
    }));
    var several = IssuesOf(With(brief =>
    {
        RoomAt(brief, 1)["id"] = "living";
        RoomAt(brief, 0)["min_area_m2"] = -1;
        brief["adjacencies"] = new JsonArray(Adj("living", "nowhere", "required"));
    }));
    Check(several.Select(issue => issue.Code).SequenceEqual(new[] { BriefCodes.AreaNonPositive, BriefCodes.RoomIdDuplicate, BriefCodes.AdjacencyRoomUnknown }), "issues are all reported in document order", Describe(several));
    var invalidBuilt = BriefValidator.Validate(new ProjectBrief(2, " ", new BriefConstraints(), new[] { new BriefRoom("Bad Id", "") }, Array.Empty<BriefAdjacency>()));
    Check(invalidBuilt.Select(issue => issue.Code).SequenceEqual(new[] { BriefCodes.VersionUnsupported, BriefCodes.NameBlank, BriefCodes.RoomIdInvalid, BriefCodes.NameBlank }),
        "validator also guards briefs built in code (version, blank names, room id format)", Describe(invalidBuilt));
    Check(BriefValidator.Validate(built with { Constraints = built.Constraints with { TotalAreaBudgetSquareMetres = new BriefValue<double>(double.PositiveInfinity) } })
        .Any(issue => issue.Code == BriefCodes.BudgetNonPositive), "non-finite budget built in code is flagged");

    // ---- JSON Schema document ----
    var schemaPath = Path.Combine(root, "docs", "contracts", "brief.schema.json");
    Check(File.Exists(schemaPath), "docs/contracts/brief.schema.json exists", schemaPath);
    using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
    var schemaRoot = schema.RootElement;
    string[] Keys(JsonElement element) => element.EnumerateObject().Select(property => property.Name).ToArray();
    string[] Strings(JsonElement element) => element.EnumerateArray().Select(item => item.GetString()!).ToArray();
    JsonElement Def(string name) => schemaRoot.GetProperty("$defs").GetProperty(name);
    Check(schemaRoot.GetProperty("$schema").GetString() == "https://json-schema.org/draft/2020-12/schema" && schemaRoot.GetProperty("additionalProperties").ValueKind == JsonValueKind.False,
        "schema is draft 2020-12 and closed at the root");
    Check(Keys(schemaRoot.GetProperty("properties")).SequenceEqual(new[] { "brief_version", "name", "constraints", "rooms", "adjacencies" })
        && Strings(schemaRoot.GetProperty("required")).SequenceEqual(new[] { "brief_version", "name", "constraints", "rooms" })
        && schemaRoot.GetProperty("properties").GetProperty("brief_version").GetProperty("const").GetInt32() == ProjectBrief.CurrentVersion, "schema root members, required list and version match the parser");
    Check(Keys(Def("room").GetProperty("properties")).SequenceEqual(new[] { "id", "name", "count", "target_area_m2", "min_area_m2", "max_area_m2", "preferred_level", "provenance" })
        && Def("room").GetProperty("properties").GetProperty("id").GetProperty("pattern").GetString() == "^[a-z0-9][a-z0-9_-]{0,63}$", "schema room members and id pattern match the parser");
    Check(Keys(Def("constraints").GetProperty("properties")).SequenceEqual(new[] { "jurisdiction", "site", "levels", "total_area_budget_m2" })
        && Keys(Def("site").GetProperty("properties")).SequenceEqual(new[] { "width_m", "depth_m" }), "schema constraint and site members match the parser");
    Check(Keys(Def("adjacency").GetProperty("properties")).SequenceEqual(new[] { "a", "b", "kind", "provenance" })
        && Strings(Def("adjacency").GetProperty("properties").GetProperty("kind").GetProperty("enum")).SequenceEqual(Enum.GetValues<BriefAdjacencyKind>().Select(BriefSerializer.Kind)),
        "schema adjacency members and kinds match the parser");
    Check(Keys(Def("provenance").GetProperty("properties")).SequenceEqual(new[] { "source", "confidence", "reference", "page" })
        && Strings(Def("provenance").GetProperty("required")).SequenceEqual(new[] { "source", "confidence" })
        && Strings(Def("provenance").GetProperty("properties").GetProperty("source").GetProperty("enum")).SequenceEqual(Enum.GetValues<BriefSource>().Select(BriefSerializer.Source))
        && Def("provenance").GetProperty("properties").GetProperty("confidence").GetProperty("maximum").GetDouble() == 1, "schema provenance members, sources and confidence range match");
    Check(new[] { "constraints", "site", "room", "adjacency", "provenance" }.All(name => Def(name).GetProperty("additionalProperties").ValueKind == JsonValueKind.False), "every schema object is closed");
}
catch (Exception error)
{
    Console.WriteLine("::error::brief FAIL unexpected " + error.GetType().Name + ": " + error.Message);
    failed++;
}

Console.WriteLine(JsonSerializer.Serialize(new { passed, failed, brief_contract = failed == 0 }));
Environment.ExitCode = failed == 0 && passed > 0 ? 0 : 1;

static string? CodeOfSerialize(ProjectBrief brief)
{
    try { BriefSerializer.Serialize(brief); return null; }
    catch (Exception error) { return error.GetType().Name; }
}

static string? CodeOfParse(string json)
{
    try { BriefParser.Parse(json); return null; }
    catch (Exception error) { return error.GetType().Name; }
}
