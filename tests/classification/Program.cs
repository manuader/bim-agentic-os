using Bim.Core;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
// P10-05 offline classification oracle (core/Bim.Core/Classification.cs): the checked-in Uniclass 2015 EF subset,
// strict loader refusals, IFC IfcClassificationReference fields, explicit unmapped reasons, determinism and selection of
// one dictionary among several (the second one is synthetic). The built-in dictionary is the embedded resource; the
// checked-in file is also read with LoadFile and must be identical. No network, no IFC writer: unit-tested only.
int passed = 0, failed = 0;
void Check(bool condition, string name, string detail = "")
{
    if (condition) { Console.WriteLine("PASS " + name); passed++; return; }
    Console.WriteLine("::error::classification FAIL " + name + (detail.Length > 0 ? " | " + detail : ""));
    failed++;
}
string? Code(Action action)
{
    try { action(); return null; }
    catch (ClassificationException error) { return error.Code; }
    catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
}
string RepoRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
    throw new DirectoryNotFoundException("repository root with AGENTS.md not found above " + AppContext.BaseDirectory);
}

var uniclassPath = Path.Combine(RepoRoot(), "core", "Bim.Core", "Classification", "uniclass2015-ef-subset.json");
var prov = new Provenance("test", "approved");
Guid G(string s) => Guid.Parse(s);
Element E(string id, string kind, double[] g, params Guid[] deps) => new(G(id), kind, g, deps, prov);
// Ids chosen so that list order differs from id order; the oracle below is in id order.
var level = E("0a000000-0000-4000-8000-000000000001", "level", [0]);
var wall = E("0b000000-0000-4000-8000-000000000002", "wall", [0, 0, 4, 0, 3, 0.25], level.Id);
var door = E("0b000000-0000-4000-8000-000000000001", "door", [1, 0, 2, 0, 0, 2.1], wall.Id);
var window = E("0f000000-0000-4000-8000-000000000001", "window", [2.5, 0, 3.5, 0, 0.9, 1.2], wall.Id);
var space = E("0c000000-0000-4000-8000-000000000001", "space", [0.125, 0.125, 3.75, 2.75], wall.Id);
var plan = E("0d000000-0000-4000-8000-000000000001", "plan", [], level.Id);
var project = new Project(G("3f0c1d2e-0000-4000-8000-000000000001"), 1, [window, plan, space, wall, level, door]);

JsonObject Minimal() => new()
{
    ["format"] = "bim.classification-dictionary/1",
    ["id"] = "test-dict",
    ["version"] = "1.0.0",
    ["name"] = "Test",
    ["publisher"] = "Tests",
    ["edition"] = "1",
    ["edition_date"] = "2026-01-15",
    ["source_url"] = "https://example.org/dict",
    ["retrieved"] = "2026-11-03",
    ["licence"] = "synthetic test data",
    ["code_pattern"] = "^T-[0-9]{2}$",
    ["rules"] = new JsonArray(new JsonObject { ["kind"] = "wall", ["code"] = "T-01", ["title"] = "Test wall", ["location"] = "https://example.org/dict/T-01" }),
};
string Mutate(Action<JsonObject> change) { var m = Minimal(); change(m); return m.ToJsonString(); }

try
{
    // ---------------- checked-in Uniclass subset ----------------
    ClassificationDictionary? uniclass = null;
    var loadCode = Code(() => uniclass = ClassificationDictionaryParser.LoadBuiltIn("uniclass2015-ef-subset"));
    Check(loadCode is null && uniclass is not null, "built-in uniclass subset loads from the embedded resource", loadCode ?? "");
    ClassificationDictionary? fromFile = null;
    var fileCode = Code(() => fromFile = ClassificationDictionaryParser.LoadFile(uniclassPath));
    Check(fileCode is null && fromFile is not null && uniclass is not null && JsonSerializer.Serialize(fromFile) == JsonSerializer.Serialize(uniclass), "LoadFile of the checked-in file equals the embedded copy", fileCode ?? "");
    Check(Code(() => ClassificationDictionaryParser.LoadBuiltIn("omniclass-table23")) == ClassificationCodes.UnknownDictionary, "unknown built-in id refused");
    Check(Code(() => ClassificationDictionaryParser.LoadBuiltIn("../uniclass2015-ef-subset")) == ClassificationCodes.UnknownDictionary, "malformed built-in id refused");
    Check(ClassificationDictionaryParser.BuiltInIds.SequenceEqual(new[] { "uniclass2015-ef-subset" }), "built-in ids listed", string.Join(",", ClassificationDictionaryParser.BuiltInIds));
    if (uniclass is not null)
    {
        Check(uniclass.Id == "uniclass2015-ef-subset" && uniclass.Version == "1.0.0", "uniclass id and version", uniclass.Id + " " + uniclass.Version);
        Check(uniclass.Publisher == "NBS" && uniclass.Edition == "EF v1.16 (July 2026)" && uniclass.EditionDate is null, "uniclass edition (month only, so no IfcDate)", uniclass.Edition);
        Check(uniclass.Licence.Contains("CC BY-ND 4.0") && uniclass.Licence.Contains("verify the licence before redistribution"), "uniclass licence note");
        var expected = new Dictionary<string, (string Code, string Title)>
        {
            ["door"] = ("EF_25_30_25", "Doors"), ["floor"] = ("EF_30_20", "Floors"), ["wall"] = ("EF_25_10", "Walls"), ["window"] = ("EF_25_30_97", "Windows"),
        };
        Check(uniclass.Rules.Count == expected.Count, "uniclass rule count", uniclass.Rules.Count.ToString());
        foreach (var (kind, (code, title)) in expected)
        {
            var rule = uniclass.Rule(kind);
            Check(rule is not null && rule.Code == code && rule.Title == title && rule.Location == "https://uniclass.thenbs.com/taxon/" + code.ToLowerInvariant(), "uniclass rule " + kind, rule?.ToString() ?? "missing");
        }
        Check(uniclass.Unmapped.Select(x => x.Kind).SequenceEqual(new[] { "level", "space" }), "uniclass declared exclusions", string.Join(",", uniclass.Unmapped.Select(x => x.Kind)));

        var result = Classification.Map(uniclass, project);
        Check(result.DictionaryId == "uniclass2015-ef-subset" && result.DictionaryVersion == "1.0.0", "result names dictionary and version");
        var mappedIds = result.Mapped.Select(m => m.ElementId).ToArray();
        Check(mappedIds.SequenceEqual(new[] { door.Id, wall.Id, window.Id }), "mapped entries in element id order", string.Join(",", mappedIds));
        var unmappedIds = result.Unmapped.Select(u => u.ElementId).ToArray();
        Check(unmappedIds.SequenceEqual(new[] { level.Id, space.Id, plan.Id }), "unmapped entries in element id order", string.Join(",", unmappedIds));
        Check(result.Mapped.Count + result.Unmapped.Count == project.Elements.Length, "every element is mapped or unmapped");
        var w = result.Mapped.SingleOrDefault(m => m.ElementId == wall.Id);
        Check(w is not null && w.Kind == "wall" && w.Code == "EF_25_10" && w.Title == "Walls", "wall mapped to EF_25_10");
        if (w is not null)
        {
            Check(w.Ifc.Identification == "EF_25_10" && w.Ifc.Name == "Walls" && w.Ifc.Location == "https://uniclass.thenbs.com/taxon/ef_25_10", "wall IfcClassificationReference fields", w.Ifc.ToString());
            var s = w.Ifc.ReferencedSource;
            Check(s.Name == "Uniclass 2015" && s.Source == "NBS" && s.Edition == "EF v1.16 (July 2026)" && s.EditionDate is null && s.Location == "https://uniclass.thenbs.com", "wall IfcClassification fields", s.ToString());
        }
        Check(result.Mapped.SingleOrDefault(m => m.ElementId == door.Id)?.Ifc.Identification == "EF_25_30_25", "door mapped to EF_25_30_25");
        Check(result.Mapped.SingleOrDefault(m => m.ElementId == window.Id)?.Ifc.Identification == "EF_25_30_97", "window mapped to EF_25_30_97");
        var sp = result.Unmapped.SingleOrDefault(u => u.ElementId == space.Id);
        Check(sp is not null && sp.Reason == ClassificationCodes.Excluded && sp.Note.Contains("SL table"), "space excluded with dictionary note", sp?.ToString() ?? "missing");
        var lv = result.Unmapped.SingleOrDefault(u => u.ElementId == level.Id);
        Check(lv is not null && lv.Kind == "level" && lv.Reason == ClassificationCodes.Excluded && lv.Note.Contains("storey"), "level excluded with dictionary note", lv?.ToString() ?? "missing");
        var floor = Classification.Map(uniclass, E("0e000000-0000-4000-8000-000000000002", "floor", []));
        Check(floor.Mapped.Count == 1 && floor.Mapped[0].Code == "EF_30_20" && floor.Mapped[0].Ifc.Name == "Floors" && floor.Mapped[0].Ifc.Location == "https://uniclass.thenbs.com/taxon/ef_30_20", "floor element mapped to EF_30_20", JsonSerializer.Serialize(floor));
        var nullCode = Code(() => Classification.Map(uniclass, new Element[] { wall, null! }));
        Check(nullCode is not null && nullCode.StartsWith("unexpected ArgumentException"), "null element refused", nullCode ?? "accepted");
        var pl = result.Unmapped.SingleOrDefault(u => u.ElementId == plan.Id);
        Check(pl is not null && pl.Kind == "plan" && pl.Reason == ClassificationCodes.NoRule && pl.Note.Contains("plan"), "plan has no rule", pl?.ToString() ?? "missing");

        var reversed = Classification.Map(uniclass, project with { Elements = project.Elements.Reverse().ToArray() });
        Check(JsonSerializer.Serialize(reversed) == JsonSerializer.Serialize(result), "input order does not change the result");
        Check(JsonSerializer.Serialize(Classification.Map(uniclass, project)) == JsonSerializer.Serialize(result), "repeated mapping is identical");
        var one = Classification.Map(uniclass, wall);
        Check(one.Mapped.Count == 1 && one.Unmapped.Count == 0 && one.Mapped[0].Code == "EF_25_10", "single element map");
        var unknown = Classification.Map(uniclass, E("0e000000-0000-4000-8000-000000000001", "mystery", []));
        Check(unknown.Mapped.Count == 0 && unknown.Unmapped.Count == 1 && unknown.Unmapped[0].Reason == ClassificationCodes.NoRule, "unknown kind is unmapped, not dropped");
        var empty = Classification.Map(uniclass, new Project(project.Id, 1, []));
        Check(empty.Mapped.Count == 0 && empty.Unmapped.Count == 0, "empty project maps to empty lists");
        Check(Code(() => Classification.Map(uniclass, new Project(project.Id, 1, [wall, wall]))) == ClassificationCodes.DuplicateElement, "duplicate element id refused");

        // ---------------- catalogue of several dictionaries ----------------
        var synthetic = ClassificationDictionaryParser.Parse(Minimal().ToJsonString());
        var catalog = new ClassificationCatalog([uniclass, synthetic]);
        Check(catalog.Ids.SequenceEqual(new[] { "test-dict", "uniclass2015-ef-subset" }), "catalogue ids sorted", string.Join(",", catalog.Ids));
        var viaTest = catalog.Map("test-dict", project);
        Check(viaTest.DictionaryId == "test-dict" && viaTest.Mapped.Count == 1 && viaTest.Mapped[0].Code == "T-01" && viaTest.Unmapped.Count == 5, "catalogue picks dictionary by id", JsonSerializer.Serialize(viaTest));
        Check(catalog.Map("uniclass2015-ef-subset", project).Mapped.Count == 3, "catalogue picks uniclass by id");
        Check(Code(() => catalog.Get("omniclass")) == ClassificationCodes.UnknownDictionary, "unknown dictionary id refused");
        Check(Code(() => new ClassificationCatalog([uniclass, uniclass])) == ClassificationCodes.DuplicateDictionary, "duplicate dictionary id refused");
    }

    // ---------------- strict loader ----------------
    Check(Code(() => ClassificationDictionaryParser.Parse(Minimal().ToJsonString())) is null, "minimal dictionary parses");
    var withExclusion = Mutate(m => m["unmapped"] = new JsonArray(new JsonObject { ["kind"] = "space", ["note"] = "n" }));
    Check(Code(() => ClassificationDictionaryParser.Parse(withExclusion)) is null, "optional unmapped list parses");
    var cases = new (string Name, string Json, string Expected)[]
    {
        ("missing version", Mutate(m => m.Remove("version")), ClassificationCodes.MissingMember),
        ("numeric version", Mutate(m => m["version"] = 1), ClassificationCodes.ValueInvalid),
        ("non-semver version", Mutate(m => m["version"] = "1.0"), ClassificationCodes.ValueInvalid),
        ("missing licence", Mutate(m => m.Remove("licence")), ClassificationCodes.MissingMember),
        ("empty licence", Mutate(m => m["licence"] = " "), ClassificationCodes.ValueInvalid),
        ("missing rules", Mutate(m => m.Remove("rules")), ClassificationCodes.MissingMember),
        ("wrong format", Mutate(m => m["format"] = "bim.classification-dictionary/2"), ClassificationCodes.FormatUnsupported),
        ("unknown member", Mutate(m => m["extra"] = 1), ClassificationCodes.UnknownMember),
        ("unknown rule member", Mutate(m => m["rules"]![0]!["type"] = "x"), ClassificationCodes.UnknownMember),
        ("bad id", Mutate(m => m["id"] = "Test Dict"), ClassificationCodes.ValueInvalid),
        ("code fails dictionary pattern", Mutate(m => m["rules"]![0]!["code"] = "T-1"), ClassificationCodes.CodeInvalid),
        ("code fails base pattern", Mutate(m => { m["code_pattern"] = "^.*$"; m["rules"]![0]!["code"] = "T-01é"; }), ClassificationCodes.CodeInvalid),
        ("unanchored code pattern", Mutate(m => m["code_pattern"] = "T-[0-9]{2}"), ClassificationCodes.ValueInvalid),
        ("invalid code pattern", Mutate(m => m["code_pattern"] = "^T-[$"), ClassificationCodes.ValueInvalid),
        ("http location", Mutate(m => m["rules"]![0]!["location"] = "http://example.org/x"), ClassificationCodes.ValueInvalid),
        ("relative source url", Mutate(m => m["source_url"] = "dict"), ClassificationCodes.ValueInvalid),
        ("bad edition date", Mutate(m => m["edition_date"] = "July 2026"), ClassificationCodes.ValueInvalid),
        ("month-only edition date", Mutate(m => m["edition_date"] = "2026-07"), ClassificationCodes.ValueInvalid),
        ("impossible edition date", Mutate(m => m["edition_date"] = "2026-02-30"), ClassificationCodes.ValueInvalid),
        ("impossible retrieved date", Mutate(m => m["retrieved"] = "2026-13-01"), ClassificationCodes.ValueInvalid),
        ("lone surrogate in value", Minimal().ToJsonString().Replace("Test wall", "Test \\ud800wall"), ClassificationCodes.InvalidJson),
        ("lone surrogate in member name", Minimal().ToJsonString().Replace("{\"format\"", "{\"\\udc00\":1,\"format\""), ClassificationCodes.InvalidJson),
        ("trailing comma", Minimal().ToJsonString().Replace("]}", "],}"), ClassificationCodes.InvalidJson),
        ("too deep", Mutate(m => m["rules"]![0]!["title"] = JsonNode.Parse("[[[[[[[[[[1]]]]]]]]]]")), ClassificationCodes.InvalidJson),
        ("non-string kind", Mutate(m => m["rules"]![0]!["kind"] = 7), ClassificationCodes.ValueInvalid),
        ("label over 255 characters", Mutate(m => m["rules"]![0]!["title"] = new string('t', 256)), ClassificationCodes.ValueInvalid),
        ("duplicate exclusion kind", Mutate(m => m["unmapped"] = new JsonArray(new JsonObject { ["kind"] = "space", ["note"] = "n" }, new JsonObject { ["kind"] = "space", ["note"] = "again" })), ClassificationCodes.DuplicateKind),
        ("backreference code pattern", Mutate(m => m["code_pattern"] = "^(T)-\\1$"), ClassificationCodes.ValueInvalid),
        ("rules plus exclusions over the cap", Mutate(m => { var r = new JsonArray(); var x = new JsonArray(); for (var i = 0; i < 257; i++) { var kind = "k" + (char)('a' + i % 26) + (char)('a' + i / 26); if (i < 200) r.Add(new JsonObject { ["kind"] = kind, ["code"] = "T-01", ["title"] = "t", ["location"] = "https://example.org/x" }); else x.Add(new JsonObject { ["kind"] = kind, ["note"] = "n" }); } m["rules"] = r; m["unmapped"] = x; }), ClassificationCodes.TooManyItems),
        ("bad kind", Mutate(m => m["rules"]![0]!["kind"] = "Wall"), ClassificationCodes.ValueInvalid),
        ("duplicate rule kind", Mutate(m => m["rules"]!.AsArray().Add(new JsonObject { ["kind"] = "wall", ["code"] = "T-02", ["title"] = "Again", ["location"] = "https://example.org/x" })), ClassificationCodes.DuplicateKind),
        ("rule and exclusion share a kind", Mutate(m => m["unmapped"] = new JsonArray(new JsonObject { ["kind"] = "wall", ["note"] = "n" })), ClassificationCodes.DuplicateKind),
        ("rules not an array", Mutate(m => m["rules"] = "wall"), ClassificationCodes.ValueInvalid),
        ("root not an object", "[]", ClassificationCodes.ValueInvalid),
        ("not json", "{", ClassificationCodes.InvalidJson),
        ("comment", "// c\n" + Minimal().ToJsonString(), ClassificationCodes.InvalidJson),
        ("duplicate top key", Minimal().ToJsonString().Replace("\"id\":\"test-dict\"", "\"id\":\"test-dict\",\"id\":\"other\""), ClassificationCodes.DuplicateKey),
        ("duplicate rule key", Minimal().ToJsonString().Replace("\"code\":\"T-01\"", "\"code\":\"T-01\",\"code\":\"T-02\""), ClassificationCodes.DuplicateKey),
        ("too many rules", Mutate(m => { var a = new JsonArray(); for (var i = 0; i < 257; i++) a.Add(new JsonObject { ["kind"] = "k" + new string('a', i % 20) + (char)('a' + i % 26) + (char)('a' + i / 26), ["code"] = "T-01", ["title"] = "t", ["location"] = "https://example.org/x" }); m["rules"] = a; }), ClassificationCodes.TooManyItems),
        ("oversized input", Mutate(m => m["name"] = new string('n', 70000)), ClassificationCodes.InputTooLarge),
    };
    foreach (var (name, json, expected) in cases)
    {
        var got = Code(() => ClassificationDictionaryParser.Parse(json));
        Check(got == expected, "refuse " + name, "expected " + expected + " got " + (got ?? "accepted"));
    }

    // File loader: size bound, BOM, strict UTF-8, missing file.
    var temp = Path.Combine(Path.GetTempPath(), "bim-classification-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        var big = Path.Combine(temp, "big.json");
        File.WriteAllBytes(big, Encoding.UTF8.GetBytes(Mutate(m => m["licence"] = new string('l', 2000)) + new string(' ', 70000)));
        Check(Code(() => ClassificationDictionaryParser.LoadFile(big)) == ClassificationCodes.InputTooLarge, "file over the byte bound refused");
        var bom = Path.Combine(temp, "bom.json");
        File.WriteAllBytes(bom, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Minimal().ToJsonString())]);
        Check(Code(() => ClassificationDictionaryParser.LoadFile(bom)) is null, "leading BOM accepted");
        var bad = Path.Combine(temp, "bad.json");
        var badBytes = Encoding.UTF8.GetBytes(Minimal().ToJsonString().Replace("Test wall", "Test w?ll"));
        badBytes[Array.IndexOf(badBytes, (byte)'?')] = 0xC3; // truncated two-byte sequence
        File.WriteAllBytes(bad, badBytes);
        Check(Code(() => ClassificationDictionaryParser.LoadFile(bad)) == ClassificationCodes.InvalidJson, "invalid UTF-8 refused");
        Check(Code(() => ClassificationDictionaryParser.LoadFile(Path.Combine(temp, "missing.json"))) == ClassificationCodes.ReadFailed, "missing file refused");
        var doubleBom = Path.Combine(temp, "double-bom.json");
        File.WriteAllBytes(doubleBom, [0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Minimal().ToJsonString())]);
        Check(Code(() => ClassificationDictionaryParser.LoadFile(doubleBom)) == ClassificationCodes.InvalidJson, "second BOM refused");
        Check(Code(() => ClassificationDictionaryParser.LoadFile("")) == ClassificationCodes.ReadFailed, "empty path refused as read failure");
        Check(Code(() => ClassificationDictionaryParser.LoadFile("bad\0name.json")) == ClassificationCodes.ReadFailed, "path with NUL refused as read failure");
    }
    finally { try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
catch (Exception error)
{
    Console.WriteLine("::error::classification FAIL unexpected " + error.GetType().Name + ": " + error.Message);
    failed++;
}

Console.WriteLine(JsonSerializer.Serialize(new { passed, failed, classification_contract = failed == 0 }));
Environment.ExitCode = failed == 0 && passed > 0 ? 0 : 1;
