using Bim.Core;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
// P5-01 domain skill manifest oracle: strict parser refusals with JSON paths, semver ranges, the safe directory loader
// (temp fixtures, symlinks), the resolver (closure, version ranges, conflicts, cycles, priority and precedence of
// defaults, traceability, determinism, 20k-skill scale), the checked-in sample skills, the JSON Schema document and the
// agreement corpus shared with tests/skills/check_schema.py, and (P5-02) the CABA code rule pack with the room validator, (P5-05) the element QA pack with the element validator, (P10-06) the jurisdiction rule-pack registry. Unit-tested only.
int passed = 0, failed = 0, skipped = 0;
// Platform fixtures (symlinks, junctions) may only be skipped on a developer's Windows machine; on CI and on every other
// platform an unavailable fixture is a failure. Skips that are inherent (running as root, FIFOs on Windows) pass allowed.
var fixturesRequired = !OperatingSystem.IsWindows() || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.Ordinal);

void Check(bool condition, string name, string detail = "")
{
    if (condition) { Console.WriteLine("PASS " + name); passed++; return; }
    Console.WriteLine("::error::skills FAIL " + name + (detail.Length > 0 ? " | " + detail : ""));
    failed++;
}

void Skip(string name, string reason, bool allowed = false)
{
    if (fixturesRequired && !allowed) { Check(false, name, "fixture unavailable: " + reason); return; }
    Console.WriteLine("SKIP " + name + " | " + reason);
    skipped++;
}

bool Readable(Action probe)
{
    try { probe(); return true; }
    catch (UnauthorizedAccessException) { return false; }
    catch (IOException) { return false; }
}

string RepoRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
    }
    throw new DirectoryNotFoundException("repository root with AGENTS.md not found above " + AppContext.BaseDirectory);
}

JsonObject Manifest(string id, string version = "1.0.0", int priority = 10) => new()
{
    ["manifest_version"] = 1,
    ["id"] = id,
    ["version"] = version,
    ["kind"] = "qa",
    ["description"] = "Test skill " + id,
    ["priority"] = priority,
};
JsonObject Requirement(string id, string range) => new() { ["id"] = id, ["range"] = range };
JsonObject MinRule(string id, string property, double value) => new()
{
    ["id"] = id,
    ["severity"] = "error",
    ["target"] = new JsonObject { ["element"] = "wall" },
    ["check"] = new JsonObject { ["op"] = "min", ["property"] = property, ["value"] = value },
    ["message"] = "Too small.",
};
JsonObject With(Action<JsonObject> change) { var manifest = Manifest("bim.test.alpha"); change(manifest); return manifest; }
JsonObject WithRule(JsonObject rule) => With(manifest => manifest["rules"] = new JsonArray(rule));
JsonObject WithCheck(JsonObject check) { var rule = MinRule("r1", "thickness_m", 1); rule["check"] = check; return WithRule(rule); }
JsonObject WithParameters(params JsonObject[] parameters) => With(manifest => manifest["parameters"] = new JsonArray(parameters.Select(parameter => (JsonNode)parameter).ToArray()));

SkillException? Refusal(string json)
{
    try { SkillParser.Parse(json); return null; }
    catch (SkillException error) { return error; }
    catch (Exception error) { return new SkillException("unexpected " + error.GetType().Name, null, error.Message); }
}
void RejectsText(string name, string json, string code, string? field = null)
{
    var error = Refusal(json);
    Check(error is not null && error.Code == code && error.Message == code && (field is null || error.Field == field), name,
        error is null ? "accepted" : error.Code + " at " + error.Field + " (" + error.Detail + ")");
}
void Rejects(string name, JsonNode manifest, string code, string? field) => RejectsText(name, manifest.ToJsonString(), code, field);
SkillManifest? Accepts(string name, string json)
{
    try { var parsed = SkillParser.Parse(json); Check(true, name); return parsed; }
    catch (Exception error) { Check(false, name, error.GetType().Name + " " + error.Message + (error is SkillException typed ? " at " + typed.Field + " (" + typed.Detail + ")" : "")); return null; }
}
SkillManifest Build(JsonObject manifest) => SkillParser.Parse(manifest.ToJsonString());
SkillCatalog CatalogOf(params JsonObject[] manifests) => new(manifests.Select(Build).ToArray());
SkillRequest[] Requests(params string[] ids) => ids.Select(id => new SkillRequest(id)).ToArray();
string Issues(SkillResolution resolution) => string.Join("; ", resolution.Issues.Select(issue => issue.Code + "@" + issue.Subject + " (" + issue.Detail + ")"));
string Render(SkillResolution resolution) => string.Join("\n", resolution.Skills.Select(skill => "skill " + skill.Id + "@" + skill.Version)
    .Concat(resolution.Settings.Select(setting => "set " + setting.Key + "=" + setting.Value + " from " + setting.Source + " " + setting.Decision + " over [" + string.Join(",", setting.Overridden) + "]"))
    .Concat(resolution.Parameters.Select(parameter => "param " + parameter.Source + "=" + parameter.Value))
    .Concat(resolution.Rules.Select(rule => "rule " + rule.QualifiedId + " " + rule.Threshold + " " + rule.ThresholdSource))
    .Concat(resolution.Issues.Select(issue => "issue " + issue.Code + "@" + issue.Subject + " " + issue.Detail)));
bool HasIssue(SkillResolution resolution, string code, string subject, string? detailPart = null) =>
    !resolution.Succeeded && resolution.Skills.Count == 0 && resolution.Settings.Count == 0 && resolution.Rules.Count == 0
    && resolution.Issues.Any(issue => issue.Code == code && issue.Subject == subject && issue.Detail.Length > 0 && (detailPart is null || issue.Detail.Contains(detailPart, StringComparison.Ordinal)));
SkillVersion V(string text) => SkillParser.TryParseVersion(text, out var version) ? version : throw new FormatException(text);
bool InRange(string range, string version) => SkillParser.TryParseRange(range, out var parsed) && parsed.Satisfies(V(version));

var temps = new List<string>();
string TempRoot()
{
    var path = Path.Combine(Path.GetTempPath(), "bim-skills-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
    Directory.CreateDirectory(path);
    temps.Add(path);
    return path;
}
void WriteSkill(string root, string directory, string json)
{
    Directory.CreateDirectory(Path.Combine(root, directory));
    File.WriteAllText(Path.Combine(root, directory, "skill.json"), json);
}
SkillException? LoadRefusal(string root)
{
    try { SkillLoader.LoadDirectory(root); return null; }
    catch (SkillException error) { return error; }
    catch (Exception error) { return new SkillException("unexpected " + error.GetType().Name, null, error.Message); }
}
void LoadRejects(string name, string root, string code, string? file, string? field = null)
{
    var error = LoadRefusal(root);
    Check(error is not null && error.Code == code && error.File == file && (field is null || error.Field == field), name,
        error is null ? "accepted" : error.Code + " file " + error.File + " at " + error.Field + " (" + error.Detail + ")");
}

try
{
    var root = RepoRoot();

    // ---- Parser: acceptance and binding ----
    var full = Accepts("full manifest parses", new JsonObject
    {
        ["manifest_version"] = 1,
        ["id"] = "bim.style.sample-pack",
        ["version"] = "2.10.3",
        ["kind"] = "style",
        ["description"] = "Sample",
        ["priority"] = -5,
        ["requires"] = new JsonArray(Requirement("bim.qa.base", "^1.2.0"), Requirement("bim.typology.house", ">=1.0.0 <3.0.0")),
        ["conflicts_with"] = new JsonArray("bim.style.other"),
        ["parameters"] = new JsonArray(
            new JsonObject { ["name"] = "depth", ["type"] = "number", ["default"] = 0.5, ["min"] = 0, ["max"] = 1, ["description"] = "Depth." },
            new JsonObject { ["name"] = "count", ["type"] = "integer", ["default"] = 3, ["min"] = 1 },
            new JsonObject { ["name"] = "flag", ["type"] = "boolean", ["default"] = true },
            new JsonObject { ["name"] = "finish", ["type"] = "string", ["default"] = "matte" }),
        ["defaults"] = new JsonObject { ["wall.exterior.thickness_m"] = 0.25, ["facade.finish"] = "brick", ["roof.flat"] = false },
        ["rules"] = new JsonArray(
            new JsonObject { ["id"] = "a1", ["severity"] = "warning", ["target"] = new JsonObject { ["room"] = "Bed*" }, ["check"] = new JsonObject { ["op"] = "max", ["property"] = "area_m2", ["parameter"] = "depth" }, ["message"] = "M", ["citation"] = "C" },
            new JsonObject { ["id"] = "a2", ["severity"] = "info", ["target"] = new JsonObject { ["element"] = "door" }, ["check"] = new JsonObject { ["op"] = "equals", ["property"] = "finish", ["parameter"] = "finish" }, ["message"] = "M" },
            new JsonObject { ["id"] = "a3", ["severity"] = "error", ["target"] = new JsonObject { ["element"] = "door" }, ["check"] = new JsonObject { ["op"] = "in", ["property"] = "operation", ["values"] = new JsonArray("swing", 2, true) }, ["message"] = "M" },
            new JsonObject { ["id"] = "a4", ["severity"] = "error", ["target"] = new JsonObject { ["room"] = "*" }, ["check"] = new JsonObject { ["op"] = "ratio_min", ["property"] = "window_area_m2", ["over"] = "area_m2", ["value"] = 0.1 }, ["message"] = "M" }),
    }.ToJsonString());
    Check(full is { ManifestVersion: 1, Id: "bim.style.sample-pack", Kind: SkillKind.Style, Priority: -5, Description: "Sample" } && full.Version == new SkillVersion(2, 10, 3),
        "root members bind id, version 2.10.3, kind style and negative priority");
    Check(full is { Requires.Count: 2, ConflictsWith.Count: 1 } && full.Requires[0].Id == "bim.qa.base" && full.Requires[0].Range.Text == "^1.2.0"
        && full.Requires[1].Range.Comparators.Count == 2 && full.ConflictsWith[0] == "bim.style.other", "requires bind ids and ranges; conflicts bind ids");
    Check(full is { Parameters.Count: 4 } && full.Parameters[0] is { Name: "depth", Type: SkillParameterType.Number, Min: 0, Max: 1, Description: "Depth." } && full.Parameters[0].Default == SkillValue.Of(0.5)
        && full.Parameters[1] is { Type: SkillParameterType.Integer, Min: 1, Max: null } && full.Parameters[1].Default == SkillValue.Of(3)
        && full.Parameters[2].Default == SkillValue.Of(true) && full.Parameters[3].Default == SkillValue.Of("matte"), "parameters bind typed defaults and optional ranges");
    Check(full is { Defaults.Count: 3 } && full.Defaults[0] == new SkillDefault("wall.exterior.thickness_m", SkillValue.Of(0.25))
        && full.Defaults[1].Value == SkillValue.Of("brick") && full.Defaults[2].Value == SkillValue.Of(false), "defaults bind dotted keys to number, text and boolean in document order");
    Check(full is { Rules.Count: 4 } && full.Rules[0] is { Id: "a1", Severity: SkillSeverity.Warning, Citation: "C", Target: { RoomPattern: "Bed*", ElementKind: null }, Check: { Op: SkillCheckOp.Max, Property: "area_m2", Parameter: "depth", Value: null } }
        && full.Rules[1].Check is { Op: SkillCheckOp.EqualTo, Parameter: "finish" } && full.Rules[1].Target.ElementKind == "door" && full.Rules[1].Citation is null
        && full.Rules[2].Check is { Op: SkillCheckOp.In, Values.Count: 3 } && full.Rules[2].Check.Values![1] == SkillValue.Of(2)
        && full.Rules[3].Check is { Op: SkillCheckOp.RatioMin, Over: "area_m2", Property: "window_area_m2" } && full.Rules[3].Check.Value == SkillValue.Of(0.1),
        "rules bind severity, target, every check op, threshold or parameter, and citation");
    var minimal = Accepts("minimal manifest parses", Manifest("bim.test.alpha").ToJsonString());
    Check(minimal is { Requires.Count: 0, ConflictsWith.Count: 0, Parameters.Count: 0, Defaults.Count: 0, Rules.Count: 0 }, "optional arrays and defaults default to empty");

    // ---- Parser: refusals with JSON paths ----
    RejectsText("input over 65536 characters is refused", "{" + new string(' ', SkillParser.MaxInputChars) + "}", SkillCodes.InputTooLarge);
    RejectsText("comments are refused", "{/*c*/\"manifest_version\":1}", SkillCodes.JsonInvalid);
    RejectsText("trailing commas are refused", Manifest("bim.test.alpha").ToJsonString().TrimEnd('}') + ",}", SkillCodes.JsonInvalid);
    RejectsText("non-object root is refused", "[]", SkillCodes.TypeInvalid);
    var deep = new StringBuilder("{\"manifest_version\":1,\"defaults\":");
    for (var index = 0; index < 20; index++) deep.Append("{\"a\":");
    RejectsText("nesting beyond the depth limit is refused", deep.Append('1').Append('}', 21).ToString(), SkillCodes.JsonInvalid);
    Rejects("missing manifest_version is refused", With(manifest => manifest.Remove("manifest_version")), SkillCodes.VersionUnsupported, "manifest_version");
    Rejects("manifest_version 2 is refused", With(manifest => manifest["manifest_version"] = 2), SkillCodes.VersionUnsupported, "manifest_version");
    Rejects("string manifest_version is refused", With(manifest => manifest["manifest_version"] = "1"), SkillCodes.VersionUnsupported, "manifest_version");
    Rejects("unknown root member is refused", With(manifest => manifest["name"] = "x"), SkillCodes.MemberUnknown, "name");
    RejectsText("duplicate root member is refused", Manifest("bim.test.alpha").ToJsonString().TrimEnd('}') + ",\"priority\":3}", SkillCodes.MemberDuplicate, "priority");
    RejectsText("duplicate defaults key is refused", With(manifest => manifest["defaults"] = new JsonObject { ["wall.height_m"] = 3 }).ToJsonString().Replace("{\"wall.height_m\":3}", "{\"wall.height_m\":3,\"wall.height_m\":4}", StringComparison.Ordinal),
        SkillCodes.MemberDuplicate, "defaults.wall.height_m");
    foreach (var name in SkillParser.RootRequired.Where(name => name != "manifest_version"))
        Rejects("missing " + name + " is refused", With(manifest => manifest.Remove(name)), SkillCodes.MemberMissing, name);
    Rejects("fractional priority is refused", With(manifest => manifest["priority"] = 1.5), SkillCodes.TypeInvalid, "priority");
    Rejects("string priority is refused", With(manifest => manifest["priority"] = "1"), SkillCodes.TypeInvalid, "priority");
    Rejects("blank description is refused", With(manifest => manifest["description"] = "  "), SkillCodes.ValueInvalid, "description");
    Rejects("unknown kind is refused", With(manifest => manifest["kind"] = "jurisdiction"), SkillCodes.ValueInvalid, "kind");
    foreach (var id in new[] { "qa", "Bim.qa.x", "bim.qa.", ".bim.qa", "bim..qa", "bim.1qa", "bim.qa_x", "bim.qa.x/y", "bim." + new string('a', 130) })
        Rejects("skill id '" + id + "' is refused", With(manifest => manifest["id"] = id), SkillCodes.IdInvalid, "id");
    Check(SkillParser.IsId("bim.qa.room-basics") && SkillParser.IsId("ar.caba.code-2024") && !SkillParser.IsId("bim.qa\n"), "reverse-DNS ids with digits and dashes are accepted; a trailing newline is not");
    foreach (var version in new[] { "1.0", "01.0.0", "1.0.0-beta", "1.0.0+build", "v1.0.0", "1.0.0.0", "1234567890.0.0", " 1.0.0" })
        Rejects("version '" + version + "' is refused", With(manifest => manifest["version"] = version), SkillCodes.SemverInvalid, "version");
    Rejects("requires must be an array", With(manifest => manifest["requires"] = new JsonObject()), SkillCodes.TypeInvalid, "requires");
    Rejects("requirement without range is refused", With(manifest => manifest["requires"] = new JsonArray(new JsonObject { ["id"] = "bim.qa.base" })), SkillCodes.MemberMissing, "requires[0].range");
    Rejects("requirement with unknown member is refused", With(manifest => manifest["requires"] = new JsonArray(new JsonObject { ["id"] = "bim.qa.base", ["range"] = "*", ["optional"] = true })), SkillCodes.MemberUnknown, "requires[0].optional");
    Rejects("requirement with bad id is refused", With(manifest => manifest["requires"] = new JsonArray(Requirement("base", "*"))), SkillCodes.IdInvalid, "requires[0].id");
    foreach (var range in new[] { "^1", "1.x", ">= 1.0.0", "1.0.0  2.0.0", "1.0.0 ", "!1.0.0", "^^1.0.0", "=>1.0.0", "1.0.0 || 2.0.0", "**" })
        Rejects("range '" + range + "' is refused", With(manifest => manifest["requires"] = new JsonArray(Requirement("bim.qa.base", range))), SkillCodes.RangeInvalid, "requires[0].range");
    Rejects("self requirement is refused", With(manifest => manifest["requires"] = new JsonArray(Requirement("bim.test.alpha", "*"))), SkillCodes.SelfReference, "requires[0].id");
    Rejects("duplicate requirement is refused", With(manifest => manifest["requires"] = new JsonArray(Requirement("bim.qa.base", "*"), Requirement("bim.qa.base", "^1.0.0"))), SkillCodes.EntryDuplicate, "requires[1].id");
    Rejects("self conflict is refused", With(manifest => manifest["conflicts_with"] = new JsonArray("bim.test.alpha")), SkillCodes.SelfReference, "conflicts_with[0]");
    Rejects("duplicate conflict is refused", With(manifest => manifest["conflicts_with"] = new JsonArray("bim.qa.x", "bim.qa.x")), SkillCodes.EntryDuplicate, "conflicts_with[1]");
    Rejects("requiring and conflicting with one skill is refused", With(manifest => { manifest["requires"] = new JsonArray(Requirement("bim.qa.x", "*")); manifest["conflicts_with"] = new JsonArray("bim.qa.x"); }),
        SkillCodes.SelfReference, "conflicts_with[0]");
    Rejects("conflict with bad id is refused", With(manifest => manifest["conflicts_with"] = new JsonArray(7)), SkillCodes.TypeInvalid, "conflicts_with[0]");
    Rejects("too many requirements are refused", With(manifest => manifest["requires"] = new JsonArray(Enumerable.Range(0, SkillParser.MaxRequires + 1).Select(index => (JsonNode)Requirement("bim.dep.d" + index.ToString(CultureInfo.InvariantCulture), "*")).ToArray())),
        SkillCodes.TooManyItems, "requires");
    foreach (var key in new[] { "wall", "Wall.height", "wall..height", "wall.height.", "wall.1height", "wall.height-m", "wall.height m" })
        Rejects("defaults key '" + key + "' is refused", With(manifest => manifest["defaults"] = new JsonObject { [key] = 1 }), SkillCodes.KeyInvalid, "defaults." + key);
    Rejects("defaults must be an object", With(manifest => manifest["defaults"] = new JsonArray()), SkillCodes.TypeInvalid, "defaults");
    Rejects("object default value is refused", With(manifest => manifest["defaults"] = new JsonObject { ["wall.height_m"] = new JsonObject() }), SkillCodes.TypeInvalid, "defaults.wall.height_m");
    Rejects("null default value is refused", With(manifest => manifest["defaults"] = new JsonObject { ["wall.height_m"] = null }), SkillCodes.TypeInvalid, "defaults.wall.height_m");
    Rejects("blank text default value is refused", With(manifest => manifest["defaults"] = new JsonObject { ["wall.finish"] = "" }), SkillCodes.ValueInvalid, "defaults.wall.finish");
    Rejects("too many defaults are refused", With(manifest =>
    {
        var map = new JsonObject();
        for (var index = 0; index <= SkillParser.MaxDefaults; index++) map["k.k" + index.ToString(CultureInfo.InvariantCulture)] = index;
        manifest["defaults"] = map;
    }), SkillCodes.TooManyItems, "defaults");

    var number = new JsonObject { ["name"] = "depth", ["type"] = "number", ["default"] = 1 };
    JsonObject Param(Action<JsonObject> change) { var parameter = (JsonObject)number.DeepClone(); change(parameter); return parameter; }
    Rejects("duplicate parameter name is refused", WithParameters(Param(_ => { }), Param(_ => { })), SkillCodes.EntryDuplicate, "parameters[1].name");
    Rejects("parameter name pattern is enforced", WithParameters(Param(parameter => parameter["name"] = "Depth")), SkillCodes.ValueInvalid, "parameters[0].name");
    Rejects("unknown parameter type is refused", WithParameters(Param(parameter => parameter["type"] = "length")), SkillCodes.ValueInvalid, "parameters[0].type");
    Rejects("parameter without default is refused", WithParameters(Param(parameter => parameter.Remove("default"))), SkillCodes.MemberMissing, "parameters[0].default");
    Rejects("number parameter with text default is refused", WithParameters(Param(parameter => parameter["default"] = "1")), SkillCodes.TypeInvalid, "parameters[0].default");
    Rejects("integer parameter with fractional default is refused", WithParameters(Param(parameter => { parameter["type"] = "integer"; parameter["default"] = 1.5; })), SkillCodes.TypeInvalid, "parameters[0].default");
    Rejects("boolean parameter with number default is refused", WithParameters(Param(parameter => parameter["type"] = "boolean")), SkillCodes.TypeInvalid, "parameters[0].default");
    Rejects("string parameter with number default is refused", WithParameters(Param(parameter => parameter["type"] = "string")), SkillCodes.TypeInvalid, "parameters[0].default");
    Rejects("range on a boolean parameter is refused", WithParameters(Param(parameter => { parameter["type"] = "boolean"; parameter["default"] = true; parameter["min"] = 0; })), SkillCodes.BoundsInvalid, "parameters[0].min");
    Rejects("inverted parameter range is refused", WithParameters(Param(parameter => { parameter["min"] = 2; parameter["max"] = 1; })), SkillCodes.BoundsInvalid, "parameters[0].max");
    Rejects("default below min is refused", WithParameters(Param(parameter => parameter["min"] = 1.5)), SkillCodes.DefaultOutOfRange, "parameters[0].default");
    Rejects("default above max is refused", WithParameters(Param(parameter => parameter["max"] = 0.5)), SkillCodes.DefaultOutOfRange, "parameters[0].default");
    Rejects("integer parameter with fractional bound is refused", WithParameters(Param(parameter => { parameter["type"] = "integer"; parameter["max"] = 2.5; })), SkillCodes.TypeInvalid, "parameters[0].max");
    Rejects("unknown parameter member is refused", WithParameters(Param(parameter => parameter["unit"] = "m")), SkillCodes.MemberUnknown, "parameters[0].unit");
    Check(Accepts("default equal to both bounds is accepted", WithParameters(Param(parameter => { parameter["min"] = 1; parameter["max"] = 1; })).ToJsonString()) is not null, "inclusive parameter bounds");

    var baseRule = MinRule("r1", "thickness_m", 1);
    JsonObject Rule(Action<JsonObject> change) { var rule = (JsonObject)baseRule.DeepClone(); change(rule); return rule; }
    Rejects("duplicate rule id is refused", With(manifest => manifest["rules"] = new JsonArray(Rule(_ => { }), Rule(_ => { }))), SkillCodes.EntryDuplicate, "rules[1].id");
    Rejects("rule id pattern is enforced", WithRule(Rule(rule => rule["id"] = "R 1")), SkillCodes.IdInvalid, "rules[0].id");
    Rejects("unknown severity is refused", WithRule(Rule(rule => rule["severity"] = "fatal")), SkillCodes.ValueInvalid, "rules[0].severity");
    Rejects("rule without message is refused", WithRule(Rule(rule => rule.Remove("message"))), SkillCodes.MemberMissing, "rules[0].message");
    Rejects("rule with unknown member is refused", WithRule(Rule(rule => rule["enabled"] = true)), SkillCodes.MemberUnknown, "rules[0].enabled");
    Rejects("blank citation is refused", WithRule(Rule(rule => rule["citation"] = " ")), SkillCodes.ValueInvalid, "rules[0].citation");
    Rejects("target with both selectors is refused", WithRule(Rule(rule => rule["target"] = new JsonObject { ["element"] = "wall", ["room"] = "*" })), SkillCodes.ValueInvalid, "rules[0].target");
    Rejects("empty target is refused", WithRule(Rule(rule => rule["target"] = new JsonObject())), SkillCodes.ValueInvalid, "rules[0].target");
    Rejects("unknown target selector is refused", WithRule(Rule(rule => rule["target"] = new JsonObject { ["level"] = "0" })), SkillCodes.MemberUnknown, "rules[0].target.level");
    Rejects("room pattern with regex syntax is refused", WithRule(Rule(rule => rule["target"] = new JsonObject { ["room"] = "bed(room)?" })), SkillCodes.ValueInvalid, "rules[0].target.room");
    Rejects("element kind pattern is enforced", WithRule(Rule(rule => rule["target"] = new JsonObject { ["element"] = "Wall" })), SkillCodes.ValueInvalid, "rules[0].target.element");
    Rejects("check without op is refused", WithCheck(new JsonObject { ["property"] = "x", ["value"] = 1 }), SkillCodes.MemberMissing, "rules[0].check.op");
    Rejects("unknown check op is refused", WithCheck(new JsonObject { ["op"] = "between", ["property"] = "x", ["value"] = 1 }), SkillCodes.ValueInvalid, "rules[0].check.op");
    Rejects("check without property is refused", WithCheck(new JsonObject { ["op"] = "min", ["value"] = 1 }), SkillCodes.MemberMissing, "rules[0].check.property");
    Rejects("check with value and parameter is refused", WithCheck(new JsonObject { ["op"] = "min", ["property"] = "x", ["value"] = 1, ["parameter"] = "p" }), SkillCodes.ValueInvalid, "rules[0].check.value");
    Rejects("check without value or parameter is refused", WithCheck(new JsonObject { ["op"] = "max", ["property"] = "x" }), SkillCodes.MemberMissing, "rules[0].check.value");
    Rejects("min with text value is refused", WithCheck(new JsonObject { ["op"] = "min", ["property"] = "x", ["value"] = "1" }), SkillCodes.TypeInvalid, "rules[0].check.value");
    Rejects("min with over is refused", WithCheck(new JsonObject { ["op"] = "min", ["property"] = "x", ["over"] = "y", ["value"] = 1 }), SkillCodes.MemberUnknown, "rules[0].check.over");
    Rejects("in with value is refused", WithCheck(new JsonObject { ["op"] = "in", ["property"] = "x", ["value"] = 1 }), SkillCodes.MemberUnknown, "rules[0].check.value");
    Rejects("in without values is refused", WithCheck(new JsonObject { ["op"] = "in", ["property"] = "x" }), SkillCodes.MemberMissing, "rules[0].check.values");
    Rejects("in with empty values is refused", WithCheck(new JsonObject { ["op"] = "in", ["property"] = "x", ["values"] = new JsonArray() }), SkillCodes.ValueInvalid, "rules[0].check.values");
    Rejects("in with duplicate values is refused", WithCheck(new JsonObject { ["op"] = "in", ["property"] = "x", ["values"] = new JsonArray("a", "b", "a") }), SkillCodes.EntryDuplicate, "rules[0].check.values[2]");
    Rejects("in with object value is refused", WithCheck(new JsonObject { ["op"] = "in", ["property"] = "x", ["values"] = new JsonArray(new JsonObject()) }), SkillCodes.TypeInvalid, "rules[0].check.values[0]");
    Rejects("ratio_min without over is refused", WithCheck(new JsonObject { ["op"] = "ratio_min", ["property"] = "x", ["value"] = 1 }), SkillCodes.MemberMissing, "rules[0].check.over");
    Rejects("ratio_min over itself is refused", WithCheck(new JsonObject { ["op"] = "ratio_min", ["property"] = "x", ["over"] = "x", ["value"] = 1 }), SkillCodes.ValueInvalid, "rules[0].check.over");
    Rejects("check referencing an unknown parameter is refused", WithCheck(new JsonObject { ["op"] = "min", ["property"] = "x", ["parameter"] = "missing" }), SkillCodes.ParameterUnknown, "rules[0].check.parameter");
    var flagParameter = new JsonObject { ["name"] = "flag", ["type"] = "boolean", ["default"] = true };
    var withFlag = WithCheck(new JsonObject { ["op"] = "min", ["property"] = "x", ["parameter"] = "flag" });
    withFlag["parameters"] = new JsonArray(flagParameter.DeepClone());
    Rejects("min over a boolean parameter is refused", withFlag, SkillCodes.ParameterTypeMismatch, "rules[0].check.parameter");
    var equalsFlag = WithCheck(new JsonObject { ["op"] = "equals", ["property"] = "x", ["parameter"] = "flag" });
    equalsFlag["parameters"] = new JsonArray(flagParameter.DeepClone());
    Check(Accepts("equals over a boolean parameter is accepted", equalsFlag.ToJsonString()) is not null, "equals accepts any parameter type");
    Rejects("too many rules are refused", With(manifest => manifest["rules"] = new JsonArray(Enumerable.Range(0, SkillParser.MaxRules + 1).Select(index => (JsonNode)MinRule("r" + index.ToString(CultureInfo.InvariantCulture), "x", 1)).ToArray())),
        SkillCodes.TooManyItems, "rules");
    Rejects("too many in values are refused", WithCheck(new JsonObject { ["op"] = "in", ["property"] = "x", ["values"] = new JsonArray(Enumerable.Range(0, SkillParser.MaxInValues + 1).Select(index => (JsonNode)JsonValue.Create(index)!).ToArray()) }),
        SkillCodes.TooManyItems, "rules[0].check.values");
    var negativeZero = SkillParser.Parse(With(manifest => manifest["defaults"] = new JsonObject { ["wall.offset_m"] = 0 }).ToJsonString().Replace("\"wall.offset_m\":0", "\"wall.offset_m\":-0", StringComparison.Ordinal));
    Check(negativeZero.Defaults[0].Value == SkillValue.Of(0.0) && negativeZero.Defaults[0].Value.ToString() == "0", "-0 is read as 0", negativeZero.Defaults[0].Value.ToString());

    // ---- Semver and ranges ----
    Check(V("1.2.3") < V("1.10.0") && V("2.0.0") > V("1.99.99") && V("0.0.1") <= V("0.0.1") && V("1.2.3").ToString() == "1.2.3", "versions compare numerically per component");
    Check(InRange("*", "0.0.0") && InRange("*", "99.1.2"), "* matches every version");
    Check(InRange("1.2.3", "1.2.3") && !InRange("1.2.3", "1.2.4") && InRange("=1.2.3", "1.2.3"), "bare and = versions are exact");
    Check(InRange("^1.2.3", "1.2.3") && InRange("^1.2.3", "1.99.0") && !InRange("^1.2.3", "2.0.0") && !InRange("^1.2.3", "1.2.2"), "^1.2.3 is >=1.2.3 <2.0.0");
    Check(InRange("^0.2.3", "0.2.9") && !InRange("^0.2.3", "0.3.0") && InRange("^0.0.3", "0.0.3") && !InRange("^0.0.3", "0.0.4"), "^0.x and ^0.0.x lock the left-most non-zero component");
    Check(InRange("~1.2.3", "1.2.9") && !InRange("~1.2.3", "1.3.0") && !InRange("~1.2.3", "1.2.2"), "~1.2.3 is >=1.2.3 <1.3.0");
    Check(InRange("^0.0.0", "0.0.0") && !InRange("^0.0.0", "0.0.1") && !InRange("^0.0.0", "0.1.0") && !InRange("^0.0.0", "1.0.0"), "^0.0.0 matches only 0.0.0");
    Check(InRange("~0.2.3", "0.2.3") && InRange("~0.2.3", "0.2.99") && !InRange("~0.2.3", "0.3.0") && !InRange("~0.2.3", "0.2.2") && InRange("~0.0.1", "0.0.9") && !InRange("~0.0.1", "0.1.0"),
        "~0.x locks major and minor");
    Check(InRange(">=1.0.0 <2.0.0", "1.5.0") && !InRange(">=1.0.0 <2.0.0", "2.0.0") && InRange(">1.0.0", "1.0.1") && !InRange(">1.0.0", "1.0.0") && InRange("<=1.0.0", "1.0.0") && !InRange("<1.0.0", "1.0.0"),
        "comparators combine with AND");

    // ---- Room globs and invariant text ----
    var bedroom = new SkillTarget(null, "bedroom*");
    Check(bedroom.MatchesRoom("Bedroom 2") && bedroom.MatchesRoom("bedroom") && !bedroom.MatchesRoom("Main bedroom") && new SkillTarget(null, "*").MatchesRoom("")
        && new SkillTarget(null, "*bath*").MatchesRoom("En-suite Bathroom") && !new SkillTarget("wall", null).MatchesRoom("wall"), "room globs are case-insensitive with * wildcards; element targets match no room");
    var previousCulture = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        Check(SkillValue.Of(0.3).ToString() == "0.3" && SkillParser.Parse(With(manifest => manifest["defaults"] = new JsonObject { ["wall.t"] = 0.125 }).ToJsonString()).Defaults[0].Value.Number == 0.125,
            "values print and parse invariantly under de-DE");
    }
    finally { CultureInfo.CurrentCulture = previousCulture; }

    // ---- Checked-in sample skills ----
    var skillsRoot = Path.Combine(root, "skills");
    SkillCatalog? samples = null;
    try { samples = SkillLoader.LoadDirectory(skillsRoot); }
    catch (SkillException error) { Check(false, "skills/ loads", error.Code + " " + error.File + " at " + error.Field + " (" + error.Detail + ")"); }
    Check(samples is { Skills.Count: 8 } && samples.Skills[0].Id == "ar.caba.codigo-edificacion" && samples.Skills[1].Id == "bim.qa.element-basics" && samples.Skills[2].Id == "bim.qa.room-basics"
        && samples.Skills[3].Id == "bim.style.compact-modern" && samples.Skills[4].Id == "bim.style.warm-masonry" && samples.Skills[5].Id == "bim.typology.dwelling"
        && samples.Skills[6].Id == "bim.typology.vivienda-unifamiliar" && samples.Skills[7].Id == "xx.example.codigo-ejemplo",
        "skills/ holds the CABA code pack, the element and room qa packs, the two style packs, the typology sample, the P5-07 vivienda unifamiliar programme pack and the P10-06 stub jurisdiction pack in ordinal order",
        samples is null ? "" : string.Join(",", samples.Skills.Select(skill => skill.Id)));
    Check(samples?.Find("bim.qa.room-basics") is { Kind: SkillKind.Qa, Priority: 50 } qa && qa.Rules.Select(rule => rule.Check.Op).Distinct().Count() == 4
        && samples.Find("bim.typology.dwelling") is { Kind: SkillKind.Typology, Priority: 20 } dwelling && dwelling.Rules.Any(rule => rule.Check.Op == SkillCheckOp.RatioMin),
        "sample skills are one qa and one typology and together use every check op");
    Check(samples is not null && samples.Skills.Where(skill => skill.Id.StartsWith("bim.", StringComparison.Ordinal)).All(skill => skill.Rules.All(rule => rule.Citation is null || rule.Citation.Contains("not normative", StringComparison.Ordinal))
        && skill.Description.Contains("not a building code", StringComparison.OrdinalIgnoreCase)), "bim.* sample skills state they are illustrative, not a building code");
    if (samples is not null)
    {
        var resolved = SkillResolver.Resolve(samples, Requests("bim.typology.dwelling", "bim.qa.room-basics"));
        Check(resolved.Succeeded && resolved.Skills.Select(skill => skill.Id).SequenceEqual(new[] { "bim.qa.room-basics", "bim.typology.dwelling" }), "samples resolve together", Issues(resolved));
        Check(resolved.Setting("room.min_clear_height_m") is { Decision: SkillDecision.Priority, Source: { SkillId: "bim.qa.room-basics", Origin: "default", Key: "room.min_clear_height_m" }, Overridden.Count: 1 } clear
            && clear.Value == SkillValue.Of(2.6) && clear.Overridden[0] == new SkillTrace("bim.typology.dwelling", SkillTrace.DefaultOrigin, "room.min_clear_height_m"),
            "overlapping sample default: qa priority 50 beats typology priority 20 and the loser is recorded", Render(resolved));
        Check(resolved.Setting("wall.exterior.thickness_m") is { Decision: SkillDecision.Only, Overridden.Count: 0, Source.SkillId: "bim.typology.dwelling" } wall && wall.Value == SkillValue.Of(0.3),
            "single-source sample default traces to its skill and key");
        Check(resolved.Settings.Select(setting => setting.Key).SequenceEqual(resolved.Settings.Select(setting => setting.Key).OrderBy(key => key, StringComparer.Ordinal)) && resolved.Settings.Count == 6,
            "effective settings are the union of sample defaults in ordinal key order", Render(resolved));
        Check(resolved.Rules.Count == 7 && resolved.Rules.FirstOrDefault(rule => rule.QualifiedId == "bim.typology.dwelling/bedroom-min-area") is { Threshold: not null, ThresholdSource: { Origin: "parameter", Key: "bedroom_min_area_m2" }, Source: { Origin: "rule", Key: "bedroom-min-area" } } bedroomRule
            && bedroomRule.Threshold == SkillValue.Of(9) && resolved.Rules.First(rule => rule.QualifiedId == "bim.qa.room-basics/door-operation-known").Threshold is null,
            "effective rules trace to skill and rule id; parameter thresholds resolve and trace to the parameter", Render(resolved));
        Check(resolved.Parameters.Select(parameter => parameter.Source + "=" + parameter.Value).SequenceEqual(new[]
            {
                "bim.qa.room-basics:parameter:min_wall_thickness_m=0.05", "bim.qa.room-basics:parameter:strict=false",
                "bim.typology.dwelling:parameter:bedroom_min_area_m2=9", "bim.typology.dwelling:parameter:max_levels=3",
            }), "effective parameters are the sample defaults in skill order, traced to skill and parameter name", Render(resolved));
        var reversed = SkillResolver.Resolve(new SkillCatalog(samples.Skills.Reverse().ToArray()), Requests("bim.qa.room-basics", "bim.typology.dwelling"));
        Check(Render(reversed) == Render(resolved), "sample resolution does not depend on catalog or request order");
    }

    // ---- P5-02 CABA code rule pack and the room validator ----
    const string Caba = "ar.caba.codigo-edificacion";
    var caba = samples?.Find(Caba);
    Check(caba is { Kind: SkillKind.Code, Priority: 100 } && caba.Rules.Count >= 40 && caba.Rules.All(rule => rule.Target.RoomPattern is not null),
        "the CABA pack loads through SkillLoader as a code skill whose rules all target rooms", caba is null ? "missing" : caba.Rules.Count.ToString(CultureInfo.InvariantCulture));
    if (caba is not null)
    {
        var cabaCitations = caba.Rules.Select(rule => (rule, cited: SkillRoomValidator.ParseCitation(rule.Citation))).ToArray();
        Check(cabaCitations.All(item => item.rule.Citation is not null && item.cited.Article.Length > 0 && item.cited.Article.Contains("CE CABA", StringComparison.Ordinal)
                && item.cited.Article.Contains("art. 3.", StringComparison.Ordinal) && item.cited.Source is { Length: > 0 }
                && (item.cited.Confidence == SkillRoomValidator.ConfidenceCited || item.cited.Confidence == SkillRoomValidator.ConfidenceOwnerConfirmed
                    || item.cited.Confidence == SkillRoomValidator.ConfidenceToVerify)
                && item.rule.Citation!.Contains("source_confidence=" + item.cited.Confidence, StringComparison.Ordinal)),
            "every CABA rule cites an article, an explicit source_confidence (cited, owner-confirmed or to-verify) and a source",
            string.Join(", ", cabaCitations.Where(item => item.cited.Source is null).Select(item => item.rule.Id)));
        Check(cabaCitations.Where(item => item.cited.Confidence == SkillRoomValidator.ConfidenceCited).All(item => item.cited.Source!.StartsWith("https://", StringComparison.Ordinal))
            && cabaCitations.Count(item => item.cited.Confidence == SkillRoomValidator.ConfidenceCited) > 0 && cabaCitations.Count(item => item.cited.Confidence == SkillRoomValidator.ConfidenceOwnerConfirmed) > 0,
            "cited CABA rules name an https source; the pack carries cited and owner-confirmed rules");
        // P5-06: the owner (an architect, 2026-11-03) confirmed the heights 2.60 / 2.40 / 2.10 m, x = 10, k = i/3 and the dormitorio
        // third-class assignment. Owner-confirmed rules record who and when; nothing else may claim owner confirmation.
        Check(cabaCitations.Where(item => item.cited.Confidence == SkillRoomValidator.ConfidenceOwnerConfirmed)
                .All(item => item.cited is { ConfirmedBy: "architect (owner)", ConfirmedOn: "2026-11-03" } && item.cited.Source is { Length: > 0 }),
            "every owner-confirmed CABA rule records confirmed_by=architect (owner) and confirmed_on=2026-11-03 and keeps its source",
            string.Join(", ", cabaCitations.Where(item => item.cited.Confidence == SkillRoomValidator.ConfidenceOwnerConfirmed && item.cited.ConfirmedBy is null).Select(item => item.rule.Id)));
        bool OwnerValue(SkillRule rule) => rule.Check switch
        {
            { Op: SkillCheckOp.Min, Property: RoomFacts.ClearHeightKey, Value: { Kind: SkillValueKind.Number, Number: 2.6 or 2.4 or 2.1 } } => true,
            { Op: SkillCheckOp.RatioMin, Property: RoomFacts.WindowAreaKey, Over: RoomFacts.AreaKey, Value: { Kind: SkillValueKind.Number, Number: 0.1 } } => true,
            { Op: SkillCheckOp.RatioMin, Property: RoomFacts.VentilationAreaKey, Over: RoomFacts.AreaKey, Value: { Kind: SkillValueKind.Number, Number: var ratio } } => Math.Abs(ratio - 1.0 / 30) < 1e-12,
            _ => false,
        };
        // Third-class rooms other than the dormitorio (escritorio, estudio, biblioteca): the owner confirmed the values but not their class assignment.
        bool ClassPending(SkillRule rule) => rule.Target.RoomPattern is "escritorio*" or "estudio*" or "biblioteca*";
        var ownerNotConfirmed = cabaCitations.Where(item => OwnerValue(item.rule) && !ClassPending(item.rule) && item.cited.Confidence != SkillRoomValidator.ConfidenceOwnerConfirmed).Select(item => item.rule.Id).ToArray();
        Check(ownerNotConfirmed.Length == 0 && cabaCitations.Count(item => OwnerValue(item.rule) && !ClassPending(item.rule)) >= 36,
            "every CABA rule carrying an owner-confirmed value (heights 2.6/2.4/2.1, illumination 0.1, ventilation 1/30) for a first-class room, dormitorio/bedroom or corridor is owner-confirmed",
            string.Join(", ", ownerNotConfirmed));
        var unexpectedToVerify = cabaCitations.Where(item => item.cited.Confidence == SkillRoomValidator.ConfidenceToVerify && !(OwnerValue(item.rule) && ClassPending(item.rule))).Select(item => item.rule.Id).ToArray();
        Check(unexpectedToVerify.Length == 0 && cabaCitations.Where(item => ClassPending(item.rule) && OwnerValue(item.rule)).All(item => item.cited.Confidence == SkillRoomValidator.ConfidenceToVerify),
            "only the escritorio/estudio/biblioteca height and opening rules, whose class assignment the owner did not confirm, stay to-verify", string.Join(", ", unexpectedToVerify));
        Check(cabaCitations.Where(item => item.cited.Confidence != SkillRoomValidator.ConfidenceOwnerConfirmed).All(item => item.cited is { ConfirmedBy: null, ConfirmedOn: null }),
            "cited and to-verify CABA rules carry no confirmation members");
        Check(caba.Rules.All(rule => RoomFacts.Properties.Contains(rule.Check.Property) && (rule.Check.Over is null || RoomFacts.Properties.Contains(rule.Check.Over))),
            "every CABA rule property is a room fact the validator knows");
        Check(new[] { "area_m2", "min_side_m", "clear_height_m", "window_area_m2", "ventilation_area_m2" }.All(property => caba.Rules.Any(rule => rule.Check.Property == property))
            && caba.Rules.Any(rule => rule.Check.Op == SkillCheckOp.RatioMin), "the CABA pack covers minimum areas, sides, clear heights and illumination and ventilation ratios");

        var cabaResolution = SkillResolver.Resolve(samples!, Requests(Caba));
        Check(cabaResolution.Succeeded, "the CABA pack resolves on its own", Issues(cabaResolution));
        double? Threshold(string ruleId) => cabaResolution.Rules.FirstOrDefault(rule => rule.QualifiedId == Caba + "/" + ruleId)?.Threshold?.Number;
        string Confidence(string ruleId) => SkillRoomValidator.ParseCitation(caba.Rules.First(rule => rule.Id == ruleId).Citation).Confidence;
        Check(Threshold("estar-area-min") == 9 && Threshold("estar-side-min") == 2.8 && Threshold("local-unico-area-min") == 18 && Threshold("dormitorio-area-min") == 7.5
            && Threshold("dormitorio-side-min") == 2.5 && Threshold("cocina-area-min") == 3 && Threshold("cocina-side-min") == 1.5 && Threshold("bano-tipo-i-area-min") == 3
            && Threshold("bano-tipo-ii-area-min") == 2.5 && Threshold("bano-area-min") == 1.4 && Threshold("bano-side-min") == 0.9 && Threshold("retrete-side-min") == 0.75,
            "CABA minimum areas and sides match RT-030809-020309-01 section 2", Render(cabaResolution));
        Check(new[] { "estar-area-min", "dormitorio-side-min", "cocina-area-min", "bano-tipo-i-side-min", "corredor-side-min" }.All(id => Confidence(id) == SkillRoomValidator.ConfidenceCited)
            && new[] { "estar-height-min", "local-unico-height-min", "dormitorio-height-min", "bedroom-height-min", "corredor-height-min", "pasillo-height-min",
                "dormitorio-illumination-ratio", "dormitorio-ventilation-ratio", "living-illumination-ratio", "monoambiente-ventilation-ratio" }.All(id => Confidence(id) == SkillRoomValidator.ConfidenceOwnerConfirmed)
            && new[] { "escritorio-height-min", "estudio-illumination-ratio", "biblioteca-ventilation-ratio" }.All(id => Confidence(id) == SkillRoomValidator.ConfidenceToVerify),
            "RT-sourced areas and sides are cited; owner-confirmed heights and opening ratios are owner-confirmed; unconfirmed class assignments stay to-verify");
        Check(cabaResolution.Rules.FirstOrDefault(rule => rule.QualifiedId == Caba + "/corredor-side-min") is { ThresholdSource: { Origin: "parameter", Key: "corredor_lado_min_m" } } corridorRule
            && corridorRule.Threshold == SkillValue.Of(1.0), "the corridor width is a traceable parameter defaulting to 1.00 m");
        var together = SkillResolver.Resolve(samples!, Requests(Caba, "bim.qa.room-basics", "bim.typology.dwelling"));
        Check(together.Succeeded && together.Rules.Count == caba.Rules.Count + 7, "the CABA pack composes with the sample skills without conflicts", Issues(together));

        string Show(RoomValidationReport report) => string.Join("\n", report.Findings.Select(finding => finding.RoomId + " " + finding.RuleId + " " + finding.Outcome + " "
            + finding.Property + " measured=" + (finding.Measured?.ToString("R", CultureInfo.InvariantCulture) ?? "-") + " required=" + (finding.Required?.ToString("R", CultureInfo.InvariantCulture) ?? "-")
            + " " + finding.SourceConfidence + " " + finding.Severity + " | " + finding.Article + " | " + finding.Detail)) + "\nunmatched " + string.Join(",", report.UnmatchedRoomIds);
        RoomFinding? Find(RoomValidationReport report, string roomId, string ruleId) => report.Findings.FirstOrDefault(finding => finding.RoomId == roomId && finding.RuleId == Caba + "/" + ruleId);
        RoomValidationReport Run(params RoomFacts[] rooms) => SkillRoomValidator.Validate(cabaResolution, rooms);

        // A fully modelled compliant bedroom: 3.00 x 3.00 m, 2.60 m clear, 1.20 m2 window, 0.40 m2 ventilation.
        var goodRoom = RoomFacts.Rectangle("r-good", "Dormitorio 1", Length.FromMetres(3), Length.FromMetres(3), Length.FromMetres(2.6), Area.FromSquareMetres(1.2), Area.FromSquareMetres(0.4));
        var goodReport = Run(goodRoom);
        Check(goodReport.Findings.Count == 5 && goodReport.Findings.All(finding => finding.Outcome == RoomCheckOutcome.Pass) && goodReport.Compliant && goodReport.Failures.Count == 0
            && goodReport.UnmatchedRoomIds.Count == 0, "a compliant bedroom passes every dormitorio rule", Show(goodReport));
        Check(Find(goodReport, "r-good", "dormitorio-area-min") is { Measured: 9, Required: 7.5, Severity: SkillSeverity.Error, SourceConfidence: "cited", Property: "area_m2", RoomName: "Dormitorio 1" } goodArea
            && goodArea.Article.StartsWith("CE CABA (Ley 6.100) art. 3.8.9.1", StringComparison.Ordinal) && goodArea.Source!.StartsWith("https://documentosboletinoficial.buenosaires.gob.ar/", StringComparison.Ordinal),
            "a finding carries rule id, article, source, confidence, measured and required values", Show(goodReport));

        // Each failing rule, one at a time.
        RoomFinding? Only(RoomFacts room, string ruleId)
        {
            var report = Run(room);
            var failures = report.Findings.Where(finding => finding.Outcome == RoomCheckOutcome.Fail).ToArray();
            return failures.Length == 1 && failures[0].RuleId == Caba + "/" + ruleId && !report.Compliant ? failures[0] : null;
        }
        Check(Only(new RoomFacts("r1", "Dormitorio", 7.25, 2.5, 2.6, 1.2, 0.4), "dormitorio-area-min") is { Measured: 7.25, Required: 7.5, Outcome: RoomCheckOutcome.Fail },
            "a bedroom below 7.50 m2 fails only the area rule");
        Check(Only(new RoomFacts("r1", "Dormitorio", 9.6, 2.4, 2.6, 1.2, 0.4), "dormitorio-side-min") is { Measured: 2.4, Required: 2.5 }, "a bedroom narrower than 2.50 m fails only the side rule");
        Check(Only(new RoomFacts("r1", "Dormitorio", 9, 3, 2.3, 1.2, 0.4), "dormitorio-height-min") is { Measured: 2.3, Required: 2.4, SourceConfidence: "owner-confirmed", ConfirmedBy: "architect (owner)", ConfirmedOn: "2026-11-03" },
            "a bedroom lower than 2.40 m fails only the height rule and says the value is owner-confirmed, by whom and when");
        Check(Only(new RoomFacts("r1", "Bedroom", 9, 3, 2.3, 1.2, 0.4), "bedroom-height-min") is { SourceConfidence: "owner-confirmed" }
            && Find(goodReport, "r-good", "dormitorio-area-min") is { ConfirmedBy: null, ConfirmedOn: null },
            "an English bedroom alias finding is owner-confirmed; a cited finding has no confirmation members");
        Check(Only(new RoomFacts("r1", "Dormitorio", 9, 3, 2.6, 0.8, 0.4), "dormitorio-illumination-ratio") is { Required: 0.1, Severity: SkillSeverity.Warning } light
            && Math.Abs(light.Measured!.Value - 0.8 / 9) < 1e-12, "a bedroom window below A/10 fails only the illumination ratio, measured as window over floor area");
        Check(Only(new RoomFacts("r1", "Dormitorio", 9, 3, 2.6, 1.2, 0.2), "dormitorio-ventilation-ratio") is { Severity: SkillSeverity.Warning }, "a bedroom ventilation below A/30 fails only the ventilation ratio");
        Check(Only(new RoomFacts("r1", "Estar", 8.5, 2.9, 2.7, 1.2, 0.4), "estar-area-min") is { Required: 9 }, "a living room below 9 m2 fails");
        Check(Only(new RoomFacts("r1", "Monoambiente", 17.5, 3.5, 2.7, 2, 0.7), "monoambiente-area-min") is { Required: 18 }, "a studio below 18 m2 fails");
        Check(Only(new RoomFacts("r1", "Estar", 12, 2.7, 2.7, 1.2, 0.4), "estar-side-min") is { Required: 2.8 }, "a living room narrower than 2.80 m fails");
        Check(Only(new RoomFacts("r1", "Living", 12, 3, 2.5, 1.2, 0.4), "living-height-min") is { Required: 2.6 }, "an English living room alias lower than 2.60 m fails");
        Check(Only(new RoomFacts("r1", "Cocina", 2.9, 1.5), "cocina-area-min") is { Required: 3 }, "a kitchen below 3 m2 fails");
        Check(Only(new RoomFacts("r1", "Kitchen", 3.5, 1.4), "kitchen-side-min") is { Required: 1.5 }, "a kitchen narrower than 1.50 m fails");
        Check(Only(new RoomFacts("r1", "Corredor", 3, 0.97, 2.4), "corredor-side-min") is { Required: 1 }, "a corridor narrower than the 1.00 m parameter fails");
        Check(Only(new RoomFacts("r1", "Retrete", 1, 0.7), "retrete-side-min") is { Required: 0.75 }, "a water closet narrower than 0.75 m fails");
        var typeOne = Run(new RoomFacts("b1", "Bano tipo I", 2.88, 1.6));
        Check(Find(typeOne, "b1", "bano-tipo-i-area-min") is { Outcome: RoomCheckOutcome.Fail, Required: 3 } && Find(typeOne, "b1", "bano-area-min") is { Outcome: RoomCheckOutcome.Pass }
            && Find(typeOne, "b1", "bano-tipo-ii-area-min") is null, "a type I bathroom below 3 m2 fails its own rule but passes the generic bathroom floor; type II rules do not apply", Show(typeOne));

        // Boundary values pass exactly at the threshold and fail just below it.
        var boundary = Run(new RoomFacts("edge", "Dormitorio", 7.5, 2.5, 2.4, 0.75, 0.25));
        Check(boundary.Compliant && boundary.Findings.Count == 5, "a bedroom exactly at 7.50 m2, 2.50 m, 2.40 m, A/10 and A/30 passes", Show(boundary));
        var below = Run(new RoomFacts("edge", "Dormitorio", 7.5 - 1e-6, 2.5 - 1e-6, 2.4 - 1e-6, 0.75 - 1e-6, 0.25 - 1e-6));
        Check(below.Findings.Count == 5 && below.Findings.All(finding => finding.Outcome == RoomCheckOutcome.Fail), "one micro-unit below every threshold fails", Show(below));
        var thirtieth = Run(new RoomFacts("edge", "Estar", 12, 3, 2.6, 1.2, 0.4));
        Check(Find(thirtieth, "edge", "estar-ventilation-ratio") is { Outcome: RoomCheckOutcome.Pass }, "ventilation of exactly A/30 passes despite 1/30 being inexact in binary", Show(thirtieth));
        var corridorEdge = Run(new RoomFacts("c", "Pasillo", 2, 1.0, 2.1));
        Check(corridorEdge.Compliant && corridorEdge.Findings.Count == 2, "a corridor exactly 1.00 m wide and 2.10 m high passes", Show(corridorEdge));

        // Rules that need data the model lacks are not_evaluable with a reason, never a pass.
        var partialRoom = Run(new RoomFacts("p", "Dormitorio", 9, 3));
        Check(partialRoom.Findings.Count == 5 && Find(partialRoom, "p", "dormitorio-area-min")?.Outcome == RoomCheckOutcome.Pass
            && new[] { "dormitorio-height-min", "dormitorio-illumination-ratio", "dormitorio-ventilation-ratio" }.All(id => Find(partialRoom, "p", id) is { Outcome: RoomCheckOutcome.NotEvaluable, Measured: null } finding
                && finding.Detail.Contains(finding.Property, StringComparison.Ordinal) && finding.Required is not null)
            && partialRoom.NotEvaluable.Count == 3 && !partialRoom.Compliant && partialRoom.Failures.Count == 0,
            "missing height, window and ventilation are not_evaluable with the missing property named; the report is not compliant", Show(partialRoom));
        var zeroArea = Run(new RoomFacts("z", "Dormitorio", 0, 0, 2.6, 1, 0.4));
        Check(Find(zeroArea, "z", "dormitorio-illumination-ratio") is { Outcome: RoomCheckOutcome.NotEvaluable } zeroLight && zeroLight.Detail.Contains("area_m2", StringComparison.Ordinal)
            && Find(zeroArea, "z", "dormitorio-area-min")?.Outcome == RoomCheckOutcome.Fail, "a ratio over a zero floor area is not_evaluable, not a pass", Show(zeroArea));
        var nothing = Run(new RoomFacts("n", "Dormitorio"));
        Check(nothing.Findings.Count == 5 && nothing.Findings.All(finding => finding.Outcome == RoomCheckOutcome.NotEvaluable), "a room with no measurements is entirely not_evaluable", Show(nothing));

        // Units: millimetre inputs equal metre inputs; invalid measurements are refused.
        var millimetres = RoomFacts.Rectangle("mm", "Dormitorio", Length.FromMillimetres(2500), Length.FromMillimetres(3000), Length.FromMillimetres(2400));
        Check(millimetres.AreaSquareMetres == 7.5 && millimetres.MinSideMetres == 2.5 && millimetres.ClearHeightMetres == 2.4 && millimetres.WindowAreaSquareMetres is null
            && Find(Run(millimetres), "mm", "dormitorio-area-min")?.Outcome == RoomCheckOutcome.Pass, "millimetre lengths convert to metres and square metres before checking");
        Check(RoomFacts.Rectangle("x", "Cocina", Length.FromMetres(1.5), Length.FromMetres(2)).MinSideMetres == 1.5 && RoomFacts.Rectangle("x", "Cocina", Length.FromMetres(2), Length.FromMetres(1.5)).MinSideMetres == 1.5,
            "the shortest side does not depend on orientation");
        bool Refused(Func<object> make) { try { make(); return false; } catch (ArgumentException) { return true; } }
        Check(Refused(() => new RoomFacts("x", "Dormitorio", -1)) && Refused(() => new RoomFacts("x", "Dormitorio", double.NaN)) && Refused(() => new RoomFacts("x", "Dormitorio", 9, double.PositiveInfinity))
            && Refused(() => new RoomFacts("", "Dormitorio")) && Refused(() => new RoomFacts("x", "Dormitorio", ventilationAreaSquareMetres: -0.1)),
            "negative, non-finite measurements and blank ids are refused");
        Check(Refused(() => SkillRoomValidator.Validate(cabaResolution, new[] { new RoomFacts("dup", "Dormitorio"), new RoomFacts("dup", "Cocina") })), "duplicate room ids are refused");

        // Names: case and accents are ignored; unmatched rooms are listed.
        var named = Run(new RoomFacts("a", "BAÑO principal", 3, 1.5), new RoomFacts("b", "Lavadero", 2, 1), new RoomFacts("c", "Escritorio", 8, 2.6, 2.5, 1, 0.3));
        Check(SkillRoomValidator.NormalizeName("Baño Señorial ÁÉÍÓÚ") == "Bano Senorial AEIOU" && Find(named, "a", "bano-area-min") is { Outcome: RoomCheckOutcome.Pass }
            && named.UnmatchedRoomIds.SequenceEqual(new[] { "b" }) && named.Findings.All(finding => finding.RoomId != "b") && Find(named, "c", "escritorio-area-min") is not null,
            "room names match case- and accent-insensitively; a room no rule targets is listed as unmatched", Show(named));

        // Determinism: input order and repetition do not change the report; findings are ordered by room then rule.
        var mixed = new[] { goodRoom, new RoomFacts("r-bad", "Estar", 8, 2.5), new RoomFacts("a-kit", "Cocina", 3, 1.5), new RoomFacts("m-x", "Deposito", 1, 1) };
        var first = Run(mixed);
        Check(Show(first) == Show(Run(mixed.Reverse().ToArray())) && Show(first) == Show(Run(mixed)) && first.Findings.Select(finding => finding.RoomId + "|" + finding.RuleId)
            .SequenceEqual(first.Findings.Select(finding => finding.RoomId + "|" + finding.RuleId).OrderBy(key => key, StringComparer.Ordinal)), "the report is deterministic and ordered by room id then rule id", Show(first));
        var previous = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-AR"); Check(Show(Run(mixed)) == Show(first), "the report does not depend on the current culture"); }
        finally { CultureInfo.CurrentCulture = previous; }
        Check(Show(SkillRoomValidator.Validate(caba, mixed)) == Show(first), "validating the manifest directly equals validating its resolution");

        // BIM-IR rooms: a compiled room's space gives area, shortest side and clear height from its bounding walls; openings are not modelled.
        var compiledRoomId = Guid.Parse("6d9a8f3e-1b7c-4c2a-9f00-5e4d3c2b1a01");
        var project = RoomCompiler.Compile(Guid.Parse("6d9a8f3e-1b7c-4c2a-9f00-5e4d3c2b1a02"), 0,
            new RoomIntent(compiledRoomId, 3.2, 2.8, 2.7, 0.2, 0, 0, "m", new Provenance("test", "approved")));
        var spaceId = RoomCompiler.Id(compiledRoomId, "space");
        var fromModel = RoomFacts.FromProject(project, new Dictionary<Guid, string> { [spaceId] = "Dormitorio principal" });
        Check(fromModel.Count == 1 && fromModel[0] is { Name: "Dormitorio principal", MinSideMetres: 2.8, ClearHeightMetres: 2.7, WindowAreaSquareMetres: null, VentilationAreaSquareMetres: null }
            && fromModel[0].Id == spaceId.ToString("D") && Math.Abs(fromModel[0].AreaSquareMetres!.Value - 3.2 * 2.8) < 1e-12, "BIM-IR spaces become room facts with area, shortest side and wall clear height");
        var modelReport = SkillRoomValidator.Validate(cabaResolution, fromModel);
        Check(modelReport.Failures.Count == 0 && modelReport.NotEvaluable.Count == 2 && modelReport.NotEvaluable.All(finding => finding.Property is "window_area_m2" or "ventilation_area_m2"),
            "a compiled BIM-IR bedroom passes dimensions and reports openings as not_evaluable", Show(modelReport));
        var unnamed = RoomFacts.FromProject(project, new Dictionary<Guid, string>());
        Check(unnamed.Count == 1 && unnamed[0].Name.Length == 0 && SkillRoomValidator.Validate(cabaResolution, unnamed).UnmatchedRoomIds.SequenceEqual(new[] { spaceId.ToString("D") }),
            "a BIM-IR space without a name is unmatched, never silently passed");

        // The validator is generic over room rules: max, unsupported ops, unknown properties and element targets.
        var generic = SkillParser.Parse(new JsonObject
        {
            ["manifest_version"] = 1, ["id"] = "bim.test.rooms", ["version"] = "1.0.0", ["kind"] = "qa", ["description"] = "Generic room rules", ["priority"] = 1,
            ["rules"] = new JsonArray(
                new JsonObject { ["id"] = "max-area", ["severity"] = "info", ["target"] = new JsonObject { ["room"] = "*" }, ["check"] = new JsonObject { ["op"] = "max", ["property"] = "area_m2", ["value"] = 10 }, ["message"] = "Big." },
                new JsonObject { ["id"] = "equals", ["severity"] = "info", ["target"] = new JsonObject { ["room"] = "*" }, ["check"] = new JsonObject { ["op"] = "equals", ["property"] = "area_m2", ["value"] = 10 }, ["message"] = "Eq.", ["citation"] = "Somewhere art. 1" },
                new JsonObject { ["id"] = "unknown", ["severity"] = "info", ["target"] = new JsonObject { ["room"] = "*" }, ["check"] = new JsonObject { ["op"] = "min", ["property"] = "volume_m3", ["value"] = 1 }, ["message"] = "Vol." },
                new JsonObject { ["id"] = "wall", ["severity"] = "info", ["target"] = new JsonObject { ["element"] = "wall" }, ["check"] = new JsonObject { ["op"] = "min", ["property"] = "thickness_m", ["value"] = 1 }, ["message"] = "Wall." })
        }.ToJsonString());
        var genericReport = SkillRoomValidator.Validate(generic, new[] { new RoomFacts("g", "Any", 12) });
        RoomFinding? GenericFinding(string id) => genericReport.Findings.FirstOrDefault(finding => finding.RuleId == "bim.test.rooms/" + id);
        Check(genericReport.Findings.Count == 3 && GenericFinding("max-area") is { Outcome: RoomCheckOutcome.Fail, Measured: 12, Required: 10, Article: "", SourceConfidence: "to-verify", Source: null }
            && GenericFinding("equals") is { Outcome: RoomCheckOutcome.NotEvaluable, Article: "Somewhere art. 1", SourceConfidence: "to-verify" } && GenericFinding("unknown") is { Outcome: RoomCheckOutcome.NotEvaluable } unknown
            && unknown.Detail.Contains("volume_m3", StringComparison.Ordinal) && GenericFinding("wall") is null,
            "max is checked; unsupported ops and unknown properties are not_evaluable; element rules are skipped; a citation without confidence is to-verify", Show(genericReport));
        Check(SkillRoomValidator.ParseCitation("Art. 9; source_confidence=cited; source=https://example.org/a;b") is { Article: "Art. 9", Confidence: "cited", Source: "https://example.org/a;b" }
            && SkillRoomValidator.ParseCitation("Art. 9; source_confidence=maybe") is { Confidence: "to-verify", Source: null } && SkillRoomValidator.ParseCitation(null) is { Article: "", Confidence: "to-verify" },
            "citations parse into article, confidence and source; an unknown confidence is to-verify");
        // P5-06: owner-confirmed is a third confidence; who and when are members in any order; unknown values stay to-verify.
        var ownerForward = SkillRoomValidator.ParseCitation("Art. 9; source_confidence=owner-confirmed; confirmed_by=architect (owner); confirmed_on=2026-11-03; source=https://x.org/a;b");
        var ownerBackward = SkillRoomValidator.ParseCitation("Art. 9; source=https://x.org/a;b; confirmed_on=2026-11-03; confirmed_by=architect (owner); source_confidence=owner-confirmed");
        Check(SkillRoomValidator.ConfidenceOwnerConfirmed == "owner-confirmed" && ownerForward == ownerBackward
            && ownerForward is { Article: "Art. 9", Confidence: "owner-confirmed", ConfirmedBy: "architect (owner)", ConfirmedOn: "2026-11-03", Source: "https://x.org/a;b", AppliesIf: null },
            "an owner-confirmed citation parses with confirmed_by and confirmed_on in either member order", ownerForward + " / " + ownerBackward);
        Check(SkillRoomValidator.ParseCitation("Art. 9; source_confidence=owner-approved; confirmed_by=someone; confirmed_on=2026-11-03") is { Confidence: "to-verify", ConfirmedBy: null, ConfirmedOn: null }
            && SkillRoomValidator.ParseCitation("Art. 9; source_confidence=owner-confirmd; confirmed_by=someone; confirmed_on=2026-11-03") is { Confidence: "to-verify", ConfirmedBy: null, ConfirmedOn: null }
            && SkillRoomValidator.ParseCitation("Art. 9; source_confidence=Owner-Confirmed; confirmed_by=someone; confirmed_on=2026-11-03") is { Confidence: "to-verify", ConfirmedBy: null }
            && SkillRoomValidator.ParseCitation("Art. 9; source_confidence=cited") is { Confidence: "cited", ConfirmedBy: null, ConfirmedOn: null },
            "an unknown, misspelt or differently cased confidence is still to-verify and carries no confirmation members");
        // S1 (P5-06 review): confirmation members belong to owner-confirmed only, and owner-confirmed needs both who and when.
        Check(SkillRoomValidator.ParseCitation("Art. 9; source_confidence=cited; confirmed_by=someone; confirmed_on=2026-11-03; source=https://x.org") is { Confidence: "cited", ConfirmedBy: null, ConfirmedOn: null, Source: "https://x.org" }
            && SkillRoomValidator.ParseCitation("Art. 9; source_confidence=to-verify; confirmed_by=someone") is { Confidence: "to-verify", ConfirmedBy: null, ConfirmedOn: null }
            && SkillRoomValidator.ParseCitation("Art. 9; source_confidence=owner-confirmed; confirmed_on=2026-11-03") is { Confidence: "to-verify", ConfirmedBy: null, ConfirmedOn: null }
            && SkillRoomValidator.ParseCitation("Art. 9; source_confidence=owner-confirmed; confirmed_by=architect (owner)") is { Confidence: "to-verify", ConfirmedBy: null, ConfirmedOn: null }
            && SkillRoomValidator.ParseCitation("Art. 9; source_confidence=owner-confirmed; confirmed_by=; confirmed_on=2026-11-03") is { Confidence: "to-verify", ConfirmedBy: null }
            && SkillRoomValidator.ParseCitation("Art. 9; source_confidence=owner-confirmed; confirmd_by=architect (owner); confirmed_on=2026-11-03") is { Confidence: "to-verify", ConfirmedBy: null, ConfirmedOn: null }
            && SkillRoomValidator.ParseCitation("Art. 9; source_confidence=owner-confirmed; Confirmed_By=architect (owner); confirmed_on=2026-11-03") is { Confidence: "to-verify", ConfirmedBy: null },
            "confirmation members are null unless the confidence is owner-confirmed; owner-confirmed without confirmed_by or confirmed_on (missing, empty, misspelt or miscased member) is to-verify");
        // N2 (P5-06 review): a repeated member makes the citation ambiguous, so its confidence is to-verify.
        Check(SkillRoomValidator.ParseCitation("Art. 9; source_confidence=cited; source_confidence=cited; source=https://x.org") is { Confidence: "to-verify", ConfirmedBy: null }
            && SkillRoomValidator.ParseCitation("Art. 9; source_confidence=to-verify; source=https://x.org; source_confidence=cited") is { Confidence: "to-verify" }
            && SkillRoomValidator.ParseCitation("Art. 9; source_confidence=owner-confirmed; confirmed_by=a; confirmed_by=b; confirmed_on=2026-11-03") is { Confidence: "to-verify", ConfirmedBy: null, ConfirmedOn: null }
            && SkillRoomValidator.ParseCitation("Art. 9; source_confidence=owner-confirmed; confirmed_by=a; confirmed_on=2026-11-03; confirmed_on=2026-11-04") is { Confidence: "to-verify", ConfirmedOn: null }
            && SkillRoomValidator.ParseCitation("Art. 9; source_confidence=cited; source=https://x.org; source=https://y.org") is { Confidence: "to-verify" }
            && SkillRoomValidator.ParseCitation("Art. 9; source_confidence=cited; applies_if=area_m2<=16; applies_if=area_m2<=20") is { Confidence: "to-verify" },
            "a citation repeating any member (source_confidence, confirmed_by, confirmed_on, source, applies_if) is to-verify");

        // ---- Review fixes (P5-02 review B1, S1-S6) ----
        // B1: compliance needs every room classified and every applicable rule evaluated.
        var unnamedReport = SkillRoomValidator.Validate(cabaResolution, unnamed);
        Check(unnamedReport.Findings.Count == 0 && !unnamedReport.Compliant && !unnamedReport.Complete, "a report whose only room is unmatched is neither compliant nor complete", Show(unnamedReport));
        Check(!Run(goodRoom, new RoomFacts("lav", "Lavadero", 2, 1)).Compliant && Run(goodRoom).Complete && !partialRoom.Complete,
            "an unmatched room or a not_evaluable rule breaks compliance; a fully evaluated room is complete");
        var diario = Run(new RoomFacts("cd", "Comedor diario", 8, 2.8, 2.6, 1, 0.3));
        Check(Find(diario, "cd", "comedor-area-min") is { Outcome: RoomCheckOutcome.Fail, Required: 9 } && !diario.Compliant && diario.UnmatchedRoomIds.Count == 0,
            "comedor diario is a first-class comedor: below 9 m2 it fails and the report is not compliant", Show(diario));

        // S1: room-name classification, pinned per name.
        string[] RuleIds(string name) => Run(new RoomFacts("q", name)).Findings.Select(finding => finding.RuleId[(Caba.Length + 1)..]).ToArray();
        Check(RuleIds("Comedor estar").Contains("comedor-area-min") && RuleIds("Comedor-living").Contains("comedor-area-min") && RuleIds("Comedor 1").Contains("comedor-area-min")
            && RuleIds("Comedor diario").Contains("comedor-area-min"), "comedor names (estar, -living, numbered, diario) are first class");
        var cocinaComedor = RuleIds("Cocina comedor");
        Check(cocinaComedor.Contains("cocina-comedor-area-min") && cocinaComedor.Contains("cocina-comedor-side-min") && cocinaComedor.Contains("cocina-comedor-height-min")
            && cocinaComedor.Contains("cocina-area-min") && RuleIds("Cocina-comedor").Contains("cocina-comedor-guion-area-min"),
            "a cocina comedor is judged as a first-class room, not only as a kitchen", string.Join(",", cocinaComedor));
        Check(Only(new RoomFacts("r1", "Cocina comedor", 8, 2.8, 2.7, 1, 0.3), "cocina-comedor-area-min") is { Required: 9 }, "a cocina comedor below 9 m2 fails the first-class area rule");
        Check(RuleIds("Dormitorio de servicio").Contains("dormitorio-area-min"), "dormitorio de servicio is a third-class dormitorio");
        Check(RuleIds("Studio").Contains("studio-area-min") && !RuleIds("Studio").Any(id => id.StartsWith("estudio", StringComparison.Ordinal))
            && RuleIds("Estudio").Contains("estudio-area-min") && !RuleIds("Estudio").Any(id => id.StartsWith("studio", StringComparison.Ordinal)),
            "Studio is a single first-class room while Estudio is third class");
        Check(SkillRoomValidator.NormalizeName("  Baño  principal \t") == "Bano principal" && RuleIds("  Dormitorio 1 ").Contains("dormitorio-area-min"),
            "names are trimmed and non-breaking or repeated spaces collapse before matching");
        Check(RuleIds("Bano tipo 1").Contains("bano-tipo-1-area-min") && RuleIds("Baño tipo 2").Contains("bano-tipo-2-area-min") && Threshold("bano-tipo-1-area-min") == 3
            && Threshold("bano-tipo-2-area-min") == 2.5, "numeric bathroom type aliases carry the type I and type II minimums");

        // S2: runtime parameter overrides, bounds-checked against the manifest parameter.
        var narrowCorridor = new RoomFacts("cor", "Corredor", 3, 0.96, 2.4);
        var overrides = new Dictionary<string, double> { [Caba + "/corredor_lado_min_m"] = 0.95 };
        Check(Find(SkillRoomValidator.Validate(cabaResolution, new[] { narrowCorridor }, overrides), "cor", "corredor-side-min") is { Outcome: RoomCheckOutcome.Pass, Required: 0.95 }
            && Find(Run(narrowCorridor), "cor", "corredor-side-min") is { Outcome: RoomCheckOutcome.Fail, Required: 1 }, "an override of 0.95 m passes a 0.96 m corridor that the 1.00 m default fails");
        Check(Refused(() => SkillRoomValidator.Validate(cabaResolution, new[] { narrowCorridor }, new Dictionary<string, double> { [Caba + "/corredor_lado_min_m"] = 0.94 }))
            && Refused(() => SkillRoomValidator.Validate(cabaResolution, new[] { narrowCorridor }, new Dictionary<string, double> { [Caba + "/corredor_lado_min_m"] = double.NaN }))
            && Refused(() => SkillRoomValidator.Validate(cabaResolution, new[] { narrowCorridor }, new Dictionary<string, double> { [Caba + "/nope"] = 1 }))
            && Refused(() => SkillRoomValidator.Validate(caba, new[] { narrowCorridor }, new Dictionary<string, double> { ["corredor_lado_min_m"] = 1 })),
            "overrides below the parameter minimum, non-finite, unknown or unqualified are refused");
        Check(caba.Rules.First(rule => rule.Id == "corredor-side-min").Message is var corridorMessage && !corridorMessage.Contains("1.0", StringComparison.Ordinal) && corridorMessage.Contains("corredor_lado_min_m", StringComparison.Ordinal),
            "the corridor message names the parameter, not a literal width");

        // S3: area-dependent heights are not evaluable above 16 m2.
        var bigBedroom = Run(new RoomFacts("big", "Dormitorio", 20, 4, 2.45, 2.5, 0.8));
        Check(Find(bigBedroom, "big", "dormitorio-height-min") is { Outcome: RoomCheckOutcome.NotEvaluable } bigHeight && bigHeight.Detail.Contains("area_m2", StringComparison.Ordinal)
            && bigHeight.Detail.Contains("16", StringComparison.Ordinal) && !bigBedroom.Compliant, "a 20 m2 bedroom at 2.45 m is not evaluable for height and not compliant", Show(bigBedroom));
        Check(Find(Run(new RoomFacts("e16", "Dormitorio", 16, 4, 2.4, 2, 0.6)), "e16", "dormitorio-height-min")?.Outcome == RoomCheckOutcome.Pass
            && Find(Run(new RoomFacts("c18", "Corredor", 18, 1.2, 2.2)), "c18", "corredor-height-min")?.Outcome == RoomCheckOutcome.NotEvaluable
            && Find(Run(new RoomFacts("na", "Dormitorio", null, 3, 2.6)), "na", "dormitorio-height-min")?.Outcome == RoomCheckOutcome.NotEvaluable
            && Find(Run(new RoomFacts("s20", "Estar", 20, 4, 2.6, 2, 0.7)), "s20", "estar-height-min")?.Outcome == RoomCheckOutcome.Pass,
            "the 16 m2 boundary is evaluated; corridors above 16 m2 and rooms of unknown area are not; first-class heights apply at any area");

        // S4: model clear height is null when any bounding wall is undecodable, and flagged approximate otherwise.
        Check(fromModel[0].ClearHeightApproximate && Find(modelReport, spaceId.ToString("D"), "dormitorio-height-min") is { Outcome: RoomCheckOutcome.Pass } approxHeight
            && approxHeight.Detail.Contains("approximat", StringComparison.Ordinal) && !new RoomFacts("m", "Dormitorio", 9, 3, 2.6).ClearHeightApproximate,
            "a BIM-IR clear height is flagged as an approximation from wall height");
        var brokenWallId = RoomCompiler.Id(compiledRoomId, "south");
        var broken = project with { Elements = project.Elements.Select(element => element.Id == brokenWallId ? element with { GeometryMetres = new[] { 0.0, 0, 3, 0, 2.7, 0 } } : element).ToArray() };
        var brokenFacts = RoomFacts.FromProject(broken, new Dictionary<Guid, string> { [spaceId] = "Dormitorio" });
        Check(brokenFacts.Count == 1 && brokenFacts[0].ClearHeightMetres is null && brokenFacts[0].AreaSquareMetres is not null
            && Find(SkillRoomValidator.Validate(cabaResolution, brokenFacts), spaceId.ToString("D"), "dormitorio-height-min")?.Outcome == RoomCheckOutcome.NotEvaluable,
            "one undecodable bounding wall makes the model clear height unknown");

        // S5: no unverified flexibility claims. P5-06: the owner confirmed the dormitorio third-class assignment; the other third-class
        // rooms keep their assignment marked to confirm.
        Check(caba.Rules.Where(rule => rule.Id.StartsWith("dormitorio", StringComparison.Ordinal)).All(rule => !rule.Message.Contains("6.25", StringComparison.Ordinal) && !rule.Message.Contains("flexib", StringComparison.OrdinalIgnoreCase))
            && caba.Rules.First(rule => rule.Id == "dormitorio-height-min").Citation!.Contains("tercera clase taken from the RT-030809-020309-01 sec. 2 table, confirmed by the owner", StringComparison.Ordinal)
            && caba.Rules.First(rule => rule.Id == "bedroom-height-min").Citation!.Contains("confirmed by the owner", StringComparison.Ordinal)
            && new[] { "escritorio", "estudio", "biblioteca" }.SelectMany(room => new[] { "-height-min", "-illumination-ratio", "-ventilation-ratio" }.Select(suffix => room + suffix))
                .All(id => caba.Rules.First(rule => rule.Id == id).Citation!.Contains("(class assignment tercera clase taken from the RT-030809-020309-01 sec. 2 table, to confirm)", StringComparison.Ordinal)),
            "dormitorio rules make no flexibility claim; the dormitorio class assignment is owner-confirmed and the other third-class assignments stay flagged to confirm");

        // S6: tolerance edges and citation member order.
        Check(Find(Run(new RoomFacts("t1", "Dormitorio", 7.5 - 5e-9, 3)), "t1", "dormitorio-area-min")?.Outcome == RoomCheckOutcome.Pass
            && Find(Run(new RoomFacts("t2", "Dormitorio", 7.5 - 1e-7, 3)), "t2", "dormitorio-area-min")?.Outcome == RoomCheckOutcome.Fail, "7.5 - 5e-9 passes within tolerance; 7.5 - 1e-7 fails");
        var forward = SkillRoomValidator.ParseCitation("Art. 9; source_confidence=cited; applies_if=area_m2<=16; source=https://x.org/a;b");
        var backward = SkillRoomValidator.ParseCitation("Art. 9; source=https://x.org/a;b; applies_if=area_m2<=16; source_confidence=cited");
        Check(forward == backward && forward is { Article: "Art. 9", Confidence: "cited", Source: "https://x.org/a;b", AppliesIf: "area_m2<=16" }, "citation members parse the same in any order",
            forward + " / " + backward);
    }

    // ---- Loader with temp fixtures ----
    LoadRejects("missing root is refused", Path.Combine(Path.GetTempPath(), "bim-skills-missing-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)), SkillCodes.RootMissing, ".");
    var empty = TempRoot();
    Check(SkillLoader.LoadDirectory(empty).Skills.Count == 0, "empty root loads an empty catalog");
    var good = TempRoot();
    WriteSkill(good, "bim.test.beta", Manifest("bim.test.beta").ToJsonString());
    WriteSkill(good, "bim.test.alpha", Manifest("bim.test.alpha").ToJsonString());
    File.WriteAllText(Path.Combine(good, "README.txt"), "not a skill");
    File.WriteAllText(Path.Combine(good, "bim.test.alpha", "notes.md"), "supporting file");
    var loaded = SkillLoader.LoadDirectory(good);
    Check(loaded.Skills.Select(skill => skill.Id).SequenceEqual(new[] { "bim.test.alpha", "bim.test.beta" }) && loaded.Find("bim.test.beta") is not null && loaded.Find("bim.test.gamma") is null,
        "loader reads every skill directory in ordinal order and ignores plain files");
    var badName = TempRoot();
    WriteSkill(badName, "Not_An_Id", Manifest("bim.test.alpha").ToJsonString());
    LoadRejects("directory name that is not a skill id is refused", badName, SkillCodes.IdInvalid, "Not_An_Id");
    var noManifest = TempRoot();
    Directory.CreateDirectory(Path.Combine(noManifest, "bim.test.alpha"));
    LoadRejects("skill directory without skill.json is refused", noManifest, SkillCodes.ManifestMissing, "bim.test.alpha/skill.json");
    var mismatch = TempRoot();
    WriteSkill(mismatch, "bim.test.alpha", Manifest("bim.test.beta").ToJsonString());
    LoadRejects("manifest id differing from its directory is refused", mismatch, SkillCodes.IdMismatch, "bim.test.alpha/skill.json", "id");
    var large = TempRoot();
    WriteSkill(large, "bim.test.alpha", Manifest("bim.test.alpha").ToJsonString() + new string(' ', SkillLoader.MaxManifestBytes));
    LoadRejects("manifest over 65536 bytes is refused before parsing", large, SkillCodes.InputTooLarge, "bim.test.alpha/skill.json");
    var badUtf8 = TempRoot();
    Directory.CreateDirectory(Path.Combine(badUtf8, "bim.test.alpha"));
    var utf8 = Encoding.UTF8.GetBytes(With(manifest => manifest["description"] = "X").ToJsonString());
    utf8[Array.IndexOf(utf8, (byte)'X')] = 0xFF;
    File.WriteAllBytes(Path.Combine(badUtf8, "bim.test.alpha", "skill.json"), utf8);
    LoadRejects("manifest that is not UTF-8 is refused", badUtf8, SkillCodes.JsonInvalid, "bim.test.alpha/skill.json");
    var invalid = TempRoot();
    WriteSkill(invalid, "bim.test.alpha", With(manifest => manifest["rules"] = new JsonArray(MinRule("r1", "x", 1), MinRule("r1", "y", 2))).ToJsonString());
    LoadRejects("parser refusal keeps its code and JSON path and gains the file", invalid, SkillCodes.EntryDuplicate, "bim.test.alpha/skill.json", "rules[1].id");

    var emptyFile = TempRoot();
    WriteSkill(emptyFile, "bim.test.alpha", "");
    LoadRejects("empty skill.json is refused as zero-length without opening it", emptyFile, SkillCodes.NotRegularFile, "bim.test.alpha/skill.json");

    // Byte order mark: the loader strips exactly one leading UTF-8 BOM; the string parser refuses a leading U+FEFF.
    var bomBytes = new byte[] { 0xEF, 0xBB, 0xBF };
    var manifestBytes = Encoding.UTF8.GetBytes(Manifest("bim.test.alpha").ToJsonString());
    var bom = TempRoot();
    Directory.CreateDirectory(Path.Combine(bom, "bim.test.alpha"));
    File.WriteAllBytes(Path.Combine(bom, "bim.test.alpha", "skill.json"), bomBytes.Concat(manifestBytes).ToArray());
    Check(LoadRefusal(bom) is null && SkillLoader.LoadDirectory(bom).Find("bim.test.alpha") is not null, "loader strips one leading UTF-8 byte order mark", LoadRefusal(bom)?.Code ?? "");
    var doubleBom = TempRoot();
    Directory.CreateDirectory(Path.Combine(doubleBom, "bim.test.alpha"));
    File.WriteAllBytes(Path.Combine(doubleBom, "bim.test.alpha", "skill.json"), bomBytes.Concat(bomBytes).Concat(manifestBytes).ToArray());
    LoadRejects("a second byte order mark is refused", doubleBom, SkillCodes.JsonInvalid, "bim.test.alpha/skill.json");
    RejectsText("parser refuses a leading U+FEFF", "﻿" + Manifest("bim.test.alpha").ToJsonString(), SkillCodes.JsonInvalid);

    // Hidden entries: a hidden directory is not a skill id and is refused; hidden plain files are ignored like any file.
    var hidden = TempRoot();
    WriteSkill(hidden, "bim.test.alpha", Manifest("bim.test.alpha").ToJsonString());
    Directory.CreateDirectory(Path.Combine(hidden, ".git"));
    LoadRejects("a stray hidden directory is refused as id_invalid", hidden, SkillCodes.IdInvalid, ".git");
    var hiddenFile = TempRoot();
    WriteSkill(hiddenFile, "bim.test.alpha", Manifest("bim.test.alpha").ToJsonString());
    File.WriteAllText(Path.Combine(hiddenFile, ".DS_Store"), "x");
    Check(SkillLoader.LoadDirectory(hiddenFile).Skills.Count == 1, "hidden plain files at the root are ignored");

    // Links: symbolic links and junctions are never followed, including dangling ones and links to plain files.
    var outside = TempRoot();
    WriteSkill(outside, "bim.test.alpha", Manifest("bim.test.alpha").ToJsonString());
    File.WriteAllText(Path.Combine(outside, "README.txt"), "outside");
    void LinkCase(string name, Action<string> arrange, string file)
    {
        var linkRoot = TempRoot();
        try { arrange(linkRoot); }
        catch (Exception failure) when (failure is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Skip(name, failure.GetType().Name + ": " + failure.Message);
            return;
        }
        LoadRejects(name, linkRoot, SkillCodes.PathUnsafe, file);
    }
    LinkCase("symlinked skill directory escaping the root is refused",
        at => Directory.CreateSymbolicLink(Path.Combine(at, "bim.test.alpha"), Path.Combine(outside, "bim.test.alpha")), "bim.test.alpha");
    LinkCase("symlinked skill.json is refused", at =>
    {
        Directory.CreateDirectory(Path.Combine(at, "bim.test.alpha"));
        File.CreateSymbolicLink(Path.Combine(at, "bim.test.alpha", "skill.json"), Path.Combine(outside, "bim.test.alpha", "skill.json"));
    }, "bim.test.alpha/skill.json");
    LinkCase("dangling symlinked skill.json is refused", at =>
    {
        Directory.CreateDirectory(Path.Combine(at, "bim.test.alpha"));
        File.CreateSymbolicLink(Path.Combine(at, "bim.test.alpha", "skill.json"), Path.Combine(outside, "missing.json"));
    }, "bim.test.alpha/skill.json");
    LinkCase("dangling symlink at the root is refused", at => File.CreateSymbolicLink(Path.Combine(at, "bim.test.broken"), Path.Combine(outside, "missing")), "bim.test.broken");
    LinkCase("symlinked plain file at the root is refused", at => File.CreateSymbolicLink(Path.Combine(at, "README.txt"), Path.Combine(outside, "README.txt")), "README.txt");
    if (OperatingSystem.IsWindows())
    {
        LinkCase("junction skill directory is refused", at =>
        {
            var link = Path.Combine(at, "bim.test.alpha");
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/c", "mklink", "/J", link, Path.Combine(outside, "bim.test.alpha") }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("cmd.exe did not start");
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 || !Directory.Exists(link)) throw new IOException("mklink /J failed: " + output);
        }, "bim.test.alpha");
    }

    if (!OperatingSystem.IsWindows())
    {
        // A FIFO reports size 0 and opening it for reading blocks until a writer appears; the loader must not open it.
        var mkfifo = new[] { "/usr/bin/mkfifo", "/bin/mkfifo" }.FirstOrDefault(File.Exists);
        if (mkfifo is null) Skip("FIFO skill.json is refused without blocking", "mkfifo not found", allowed: true);
        else
        {
            var fifoRoot = TempRoot();
            Directory.CreateDirectory(Path.Combine(fifoRoot, "bim.test.alpha"));
            using (var process = Process.Start(mkfifo, Path.Combine(fifoRoot, "bim.test.alpha", "skill.json"))) process?.WaitForExit();
            var pending = Task.Run(() => LoadRefusal(fifoRoot));
            if (!pending.Wait(TimeSpan.FromSeconds(20))) Check(false, "FIFO skill.json is refused without blocking", "loader blocked on the FIFO");
            else Check(pending.Result is { Code: SkillCodes.NotRegularFile, File: "bim.test.alpha/skill.json" }, "FIFO skill.json is refused without blocking",
                pending.Result is null ? "accepted" : pending.Result.Code + " file " + pending.Result.File);
        }

        // Unreadable entries are read_failed with the file set; root ignores permissions, so probe first.
        var owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var lockedRoot = TempRoot();
        WriteSkill(lockedRoot, "bim.test.alpha", Manifest("bim.test.alpha").ToJsonString());
        new DirectoryInfo(lockedRoot).UnixFileMode = UnixFileMode.None;
        try
        {
            if (Readable(() => Directory.EnumerateFileSystemEntries(lockedRoot).Any())) Skip("unreadable skills root is read_failed", "permissions not enforced for " + Environment.UserName + " (root)", allowed: true);
            else LoadRejects("unreadable skills root is read_failed", lockedRoot, SkillCodes.ReadFailed, ".");
        }
        finally { new DirectoryInfo(lockedRoot).UnixFileMode = owner; }
        var lockedFile = TempRoot();
        WriteSkill(lockedFile, "bim.test.alpha", Manifest("bim.test.alpha").ToJsonString());
        var lockedManifest = Path.Combine(lockedFile, "bim.test.alpha", "skill.json");
        File.SetUnixFileMode(lockedManifest, UnixFileMode.None);
        try
        {
            if (Readable(() => File.ReadAllBytes(lockedManifest))) Skip("unreadable skill.json is read_failed", "permissions not enforced for " + Environment.UserName + " (root)", allowed: true);
            else LoadRejects("unreadable skill.json is read_failed", lockedFile, SkillCodes.ReadFailed, "bim.test.alpha/skill.json");
        }
        finally { File.SetUnixFileMode(lockedManifest, owner); }
    }

    // ---- Resolver ----
    var alpha = Manifest("bim.test.alpha");
    var beta = Manifest("bim.test.beta");
    beta["requires"] = new JsonArray(Requirement("bim.test.alpha", "^1.0.0"));
    var gamma = Manifest("bim.test.gamma", "2.1.0");
    gamma["requires"] = new JsonArray(Requirement("bim.test.beta", ">=1.0.0 <2.0.0"));
    var chain = CatalogOf(gamma, alpha, beta);
    var closure = SkillResolver.Resolve(chain, Requests("bim.test.gamma"));
    Check(closure.Succeeded && closure.Skills.Select(skill => skill.Id).SequenceEqual(new[] { "bim.test.alpha", "bim.test.beta", "bim.test.gamma" }), "dependency closure is complete and requirements come first", Issues(closure));
    var sideA = Manifest("bim.test.zeta");
    var sideB = Manifest("bim.test.eta");
    var top = Manifest("bim.test.top");
    top["requires"] = new JsonArray(Requirement("bim.test.zeta", "*"), Requirement("bim.test.eta", "*"));
    var diamond = SkillResolver.Resolve(CatalogOf(top, sideA, sideB), Requests("bim.test.top"));
    Check(diamond.Succeeded && diamond.Skills.Select(skill => skill.Id).SequenceEqual(new[] { "bim.test.eta", "bim.test.zeta", "bim.test.top" }), "independent requirements are ordered by ordinal id", Issues(diamond));
    Check(SkillResolver.Resolve(chain, Array.Empty<SkillRequest>()) is { Succeeded: true, Skills.Count: 0, Settings.Count: 0 }, "an empty request resolves to nothing");
    Check(HasIssue(SkillResolver.Resolve(chain, Requests("bim.test.missing")), SkillCodes.SkillMissing, "bim.test.missing"), "unknown requested skill is skill_missing");
    Check(HasIssue(SkillResolver.Resolve(CatalogOf(gamma, beta), Requests("bim.test.gamma")), SkillCodes.DependencyMissing, "bim.test.alpha", "required by bim.test.beta"), "absent dependency is dependency_missing naming its requirer");
    Check(HasIssue(SkillResolver.Resolve(chain, new[] { new SkillRequest("bim.test.gamma", "^1.0.0") }), SkillCodes.VersionMismatch, "bim.test.gamma", "required by request"), "request range not satisfied is version_mismatch");
    var betaNeedsTwo = Manifest("bim.test.beta");
    betaNeedsTwo["requires"] = new JsonArray(Requirement("bim.test.alpha", "^2.0.0"));
    var mismatched = SkillResolver.Resolve(CatalogOf(alpha, betaNeedsTwo), Requests("bim.test.beta"));
    Check(HasIssue(mismatched, SkillCodes.VersionMismatch, "bim.test.alpha", "1.0.0 does not satisfy ^2.0.0 required by bim.test.beta"), "dependency range not satisfied is version_mismatch", Issues(mismatched));
    Check(HasIssue(SkillResolver.Resolve(chain, new[] { new SkillRequest("bim.test.alpha", "^1.0.0"), new SkillRequest("bim.test.alpha", "<1.0.0") }), SkillCodes.VersionMismatch, "bim.test.alpha", "<1.0.0"),
        "every range on a skill must hold");
    Check(SkillResolver.Resolve(chain, new[] { new SkillRequest("bim.test.alpha", "~1.0.0"), new SkillRequest("bim.test.beta", "1.0.0") }).Succeeded, "satisfied request ranges resolve");
    Check(HasIssue(SkillResolver.Resolve(chain, new[] { new SkillRequest("alpha") }), SkillCodes.RequestInvalid, "alpha"), "request with a bad id is request_invalid");
    Check(HasIssue(SkillResolver.Resolve(chain, new[] { new SkillRequest("bim.test.alpha", "latest") }), SkillCodes.RequestInvalid, "bim.test.alpha"), "request with a bad range is request_invalid");

    var cycleA = Manifest("bim.cycle.a");
    var cycleB = Manifest("bim.cycle.b");
    var cycleC = Manifest("bim.cycle.c");
    cycleA["requires"] = new JsonArray(Requirement("bim.cycle.b", "*"));
    cycleB["requires"] = new JsonArray(Requirement("bim.cycle.c", "*"));
    cycleC["requires"] = new JsonArray(Requirement("bim.cycle.a", "*"));
    var cyclic = SkillResolver.Resolve(CatalogOf(cycleC, cycleB, cycleA), Requests("bim.cycle.b"));
    Check(HasIssue(cyclic, SkillCodes.DependencyCycle, "bim.cycle.a", "bim.cycle.a -> bim.cycle.b -> bim.cycle.c -> bim.cycle.a") && cyclic.Issues.Count(issue => issue.Code == SkillCodes.DependencyCycle) == 1,
        "a three-skill cycle is reported once, rotated to its smallest id", Issues(cyclic));
    var pairA = Manifest("bim.pair.a");
    var pairB = Manifest("bim.pair.b");
    pairA["requires"] = new JsonArray(Requirement("bim.pair.b", "*"));
    pairB["requires"] = new JsonArray(Requirement("bim.pair.a", "*"));
    var pairCycle = SkillResolver.Resolve(CatalogOf(pairB, pairA), Requests("bim.pair.b"));
    Check(HasIssue(pairCycle, SkillCodes.DependencyCycle, "bim.pair.a", "bim.pair.a -> bim.pair.b -> bim.pair.a") && pairCycle.Issues.Count == 1, "a two-skill cycle is reported once", Issues(pairCycle));

    var left = Manifest("bim.style.left");
    var right = Manifest("bim.style.right");
    left["conflicts_with"] = new JsonArray("bim.style.right");
    var conflicting = SkillResolver.Resolve(CatalogOf(left, right), Requests("bim.style.right", "bim.style.left"));
    Check(HasIssue(conflicting, SkillCodes.SkillConflict, "bim.style.left", "conflict with bim.style.right"), "a declared conflict between resolved skills is skill_conflict", Issues(conflicting));
    var early = Manifest("bim.style.early");
    var late = Manifest("bim.style.late");
    late["conflicts_with"] = new JsonArray("bim.style.early");
    var lateConflict = SkillResolver.Resolve(CatalogOf(early, late), Requests("bim.style.early", "bim.style.late"));
    Check(HasIssue(lateConflict, SkillCodes.SkillConflict, "bim.style.late", "conflict with bim.style.early"), "a conflict declared only by the ordinal-later skill is found", Issues(lateConflict));
    right["conflicts_with"] = new JsonArray("bim.style.left");
    Check(SkillResolver.Resolve(CatalogOf(left, right), Requests("bim.style.left", "bim.style.right")).Issues.Count(issue => issue.Code == SkillCodes.SkillConflict) == 1, "a mutual conflict is reported once");
    Check(SkillResolver.Resolve(CatalogOf(left, right), Requests("bim.style.left")).Succeeded, "a conflict with an unrequested skill is not an issue");
    var many = SkillResolver.Resolve(CatalogOf(left, right), Requests("bim.style.left", "bim.style.right", "bim.style.absent"));
    Check(many.Issues.Any(issue => issue.Code == SkillCodes.SkillMissing) && many.Issues.Any(issue => issue.Code == SkillCodes.SkillConflict), "every refusal is collected, not only the first", Issues(many));

    // Overlapping defaults.
    JsonObject Defaults(string id, int priority, double value, string key = "wall.exterior.thickness_m")
    {
        var manifest = Manifest(id, "1.0.0", priority);
        manifest["defaults"] = new JsonObject { [key] = value, ["own." + id.Replace("bim.", "", StringComparison.Ordinal).Replace(".", "_", StringComparison.Ordinal).Replace("-", "_", StringComparison.Ordinal)] = priority };
        return manifest;
    }
    var high = Defaults("bim.code.high", 90, 0.3);
    var low = Defaults("bim.style.low", 10, 0.2);
    var priorityWins = SkillResolver.Resolve(CatalogOf(low, high), Requests("bim.style.low", "bim.code.high"), new[] { "bim.style.low", "bim.code.high" });
    Check(priorityWins.Setting("wall.exterior.thickness_m") is { Decision: SkillDecision.Priority, Source.SkillId: "bim.code.high", Overridden.Count: 1 } winner && winner.Value == SkillValue.Of(0.3)
        && winner.Overridden[0].SkillId == "bim.style.low", "higher priority wins even when precedence lists the lower one first", Render(priorityWins));
    Check(priorityWins.Setting("own.code_high") is { Decision: SkillDecision.Only } && priorityWins.Setting("own.style_low") is { Decision: SkillDecision.Only } && priorityWins.Settings.Count == 3, "non-overlapping keys pass through");
    var tieA = Defaults("bim.style.tie-a", 10, 0.25);
    var tieB = Defaults("bim.style.tie-b", 10, 0.35);
    var tie = SkillResolver.Resolve(CatalogOf(tieA, tieB), Requests("bim.style.tie-a", "bim.style.tie-b"));
    Check(HasIssue(tie, SkillCodes.DefaultConflict, "wall.exterior.thickness_m", "bim.style.tie-a=0.25, bim.style.tie-b=0.35"), "equal priority with different values is default_conflict", Issues(tie));
    var tieOrdered = SkillResolver.Resolve(CatalogOf(tieA, tieB), Requests("bim.style.tie-a", "bim.style.tie-b"), new[] { "bim.style.tie-b", "bim.style.tie-a" });
    Check(tieOrdered.Setting("wall.exterior.thickness_m") is { Decision: SkillDecision.Precedence, Source.SkillId: "bim.style.tie-b" } ordered && ordered.Value == SkillValue.Of(0.35)
        && ordered.Overridden.SequenceEqual(new[] { new SkillTrace("bim.style.tie-a", "default", "wall.exterior.thickness_m") }), "explicit precedence breaks an equal-priority tie and records the loser", Render(tieOrdered));
    var tieFlipped = SkillResolver.Resolve(CatalogOf(tieA, tieB), Requests("bim.style.tie-a", "bim.style.tie-b"), new[] { "bim.style.tie-a", "bim.style.tie-b" });
    Check(tieFlipped.Setting("wall.exterior.thickness_m") is { Source.SkillId: "bim.style.tie-a" } flipped && flipped.Value == SkillValue.Of(0.25), "reversing precedence flips the tie winner");
    var tieC = Defaults("bim.style.tie-c", 10, 0.45);
    var partial = SkillResolver.Resolve(CatalogOf(tieA, tieB, tieC), Requests("bim.style.tie-a", "bim.style.tie-b", "bim.style.tie-c"), new[] { "bim.style.tie-b", "bim.style.tie-a" });
    Check(HasIssue(partial, SkillCodes.DefaultConflict, "wall.exterior.thickness_m"), "precedence that leaves a tied skill unranked is still a conflict", Issues(partial));
    var sameA = Defaults("bim.style.same-a", 10, 0.25);
    var sameB = Defaults("bim.style.same-b", 10, 0.25);
    var agreement = SkillResolver.Resolve(CatalogOf(sameB, sameA), Requests("bim.style.same-b", "bim.style.same-a"));
    Check(agreement.Setting("wall.exterior.thickness_m") is { Decision: SkillDecision.Agreement, Source.SkillId: "bim.style.same-a", Overridden.Count: 1 } agreed && agreed.Value == SkillValue.Of(0.25),
        "equal priority with equal values agrees; the ordinal-first skill is the source", Render(agreement));
    var textVsNumber = Manifest("bim.style.text", "1.0.0", 10);
    textVsNumber["defaults"] = new JsonObject { ["wall.exterior.thickness_m"] = "0.25" };
    Check(HasIssue(SkillResolver.Resolve(CatalogOf(sameA, textVsNumber), Requests("bim.style.same-a", "bim.style.text")), SkillCodes.DefaultConflict, "wall.exterior.thickness_m"), "a text and a number are different values");
    Check(HasIssue(SkillResolver.Resolve(CatalogOf(tieA, tieB), Requests("bim.style.tie-a", "bim.style.tie-b"), new[] { "bim.style.tie-a", "bim.style.nobody" }), SkillCodes.PrecedenceUnknown, "bim.style.nobody"),
        "precedence naming an unresolved skill is precedence_unknown");
    Check(HasIssue(SkillResolver.Resolve(CatalogOf(tieA, tieB), Requests("bim.style.tie-a", "bim.style.tie-b"), new[] { "bim.style.tie-a", "bim.style.tie-b", "bim.style.tie-a" }), SkillCodes.PrecedenceDuplicate, "bim.style.tie-a"),
        "precedence listing a skill twice is precedence_duplicate");
    var three = Defaults("bim.qa.mid", 50, 0.28);
    var stacked = SkillResolver.Resolve(CatalogOf(low, three, high), Requests("bim.style.low", "bim.qa.mid", "bim.code.high"));
    Check(stacked.Setting("wall.exterior.thickness_m") is { Source.SkillId: "bim.code.high" } stack && stack.Overridden.Select(trace => trace.SkillId).SequenceEqual(new[] { "bim.qa.mid", "bim.style.low" }),
        "overridden contributions are listed strongest first", Render(stacked));
    var shuffled = SkillResolver.Resolve(CatalogOf(high, low, three), Requests("bim.code.high", "bim.style.low", "bim.qa.mid", "bim.qa.mid"));
    Check(Render(shuffled) == Render(stacked), "resolution is identical for shuffled catalog and repeated, reordered requests");
    var ruled = Manifest("bim.qa.ruled");
    ruled["parameters"] = new JsonArray(new JsonObject { ["name"] = "limit", ["type"] = "integer", ["default"] = 4 });
    ruled["rules"] = new JsonArray(MinRule("literal", "x", 2), new JsonObject
    {
        ["id"] = "parametric", ["severity"] = "info", ["target"] = new JsonObject { ["room"] = "*" },
        ["check"] = new JsonObject { ["op"] = "max", ["property"] = "x", ["parameter"] = "limit" }, ["message"] = "M",
    });
    var ruledResolution = SkillResolver.Resolve(CatalogOf(ruled), Requests("bim.qa.ruled"));
    Check(ruledResolution.Rules.Select(rule => rule.QualifiedId).SequenceEqual(new[] { "bim.qa.ruled/literal", "bim.qa.ruled/parametric" })
        && ruledResolution.Rules[0] is { ThresholdSource: null } literal && literal.Threshold == SkillValue.Of(2)
        && ruledResolution.Rules[1].Threshold == SkillValue.Of(4) && ruledResolution.Rules[1].ThresholdSource == new SkillTrace("bim.qa.ruled", "parameter", "limit")
        && ruledResolution.Parameters.Single() is { SkillId: "bim.qa.ruled", Name: "limit" } limit && limit.Value == SkillValue.Of(4),
        "rules keep declaration order with literal or parameter thresholds", Render(ruledResolution));

    // ---- Catalog and code-built manifests ----
    SkillException? catalogError = null;
    try { _ = new SkillCatalog(new[] { Build(alpha), Build(Manifest("bim.test.alpha", "2.0.0")) }); }
    catch (SkillException duplicate) { catalogError = duplicate; }
    Check(catalogError is { Code: SkillCodes.IdDuplicate, Field: "id" } && catalogError.Detail!.Contains("bim.test.alpha", StringComparison.Ordinal), "a catalog refuses two skills with one id", catalogError?.Code ?? "accepted");
    var anyRange = SkillParser.TryParseRange("*", out var star) ? star : throw new FormatException("*");
    SkillManifest Coded(string id, params string[] requires) => new(SkillManifest.CurrentVersion, id, new SkillVersion(1, 0, 0), SkillKind.Qa, "Coded " + id, 0,
        requires.Select(required => new SkillRequirement(required, anyRange)).ToArray(), Array.Empty<string>(), Array.Empty<SkillParameter>(), Array.Empty<SkillDefault>(), Array.Empty<SkillRule>());
    var twice = SkillResolver.Resolve(new SkillCatalog(new[] { Coded("bim.coded.top", "bim.coded.base", "bim.coded.base"), Coded("bim.coded.base") }), Requests("bim.coded.top"));
    Check(twice.Succeeded && twice.Skills.Select(skill => skill.Id).SequenceEqual(new[] { "bim.coded.base", "bim.coded.top" }), "a dependency listed twice in code counts as one edge", Issues(twice));
    var orphanRule = new SkillRule("orphan", SkillSeverity.Error, new SkillTarget("wall", null), new SkillCheck(SkillCheckOp.Min, "x", Parameter: "missing"), "M");
    var orphanResolution = SkillResolver.Resolve(new SkillCatalog(new[] { Coded("bim.coded.orphan") with { Rules = new[] { orphanRule } } }), Requests("bim.coded.orphan"));
    Check(HasIssue(orphanResolution, SkillCodes.ParameterUnknown, "bim.coded.orphan/orphan", "missing"), "a code-built rule naming a missing parameter is a typed refusal", Issues(orphanResolution));

    // ---- Agreement corpus (shared with tests/skills/check_schema.py) ----
    var corpus = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "tests", "skills", "corpus", "cases.json")))!.AsObject();
    var validCases = corpus["valid"]!.AsArray();
    var invalidCases = corpus["invalid"]!.AsArray();
    Check(validCases.Count >= 5 && invalidCases.Count >= 60 && invalidCases.Any(node => (bool)node!["schema"]!) && invalidCases.Any(node => !(bool)node!["schema"]!),
        "corpus has valid, schema-expressible and parser-only cases");
    foreach (var node in validCases) Accepts("corpus valid: " + (string)node!["name"]!, node!["manifest"]!.ToJsonString());
    foreach (var node in invalidCases)
    {
        var corpusCase = node!.AsObject();
        var refusal = Refusal((string?)corpusCase["text"] ?? corpusCase["manifest"]!.ToJsonString());
        Check(refusal is not null && refusal.Code == (string)corpusCase["code"]! && refusal.Field == (string?)corpusCase["field"],
            "corpus refusal (" + ((bool)corpusCase["schema"]! ? "schema" : "parser-only") + "): " + (string)corpusCase["name"]!,
            refusal is null ? "accepted" : refusal.Code + " at " + refusal.Field + " (" + refusal.Detail + ")");
    }

    // ---- JSON Schema document ----
    var schemaPath = Path.Combine(root, "docs", "contracts", "skill.schema.json");
    using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
    var schemaRoot = schema.RootElement;
    string[] Keys(JsonElement element) => element.EnumerateObject().Select(property => property.Name).ToArray();
    string[] Strings(JsonElement element) => element.EnumerateArray().Select(item => item.GetString()!).ToArray();
    JsonElement Def(string name) => schemaRoot.GetProperty("$defs").GetProperty(name);
    JsonElement Props(string name) => Def(name).GetProperty("properties");
    string Pattern(string name) => Def(name).GetProperty("pattern").GetString()!;
    Check(schemaRoot.GetProperty("$schema").GetString() == "https://json-schema.org/draft/2020-12/schema" && schemaRoot.GetProperty("additionalProperties").ValueKind == JsonValueKind.False,
        "schema is draft 2020-12 and closed at the root");
    Check(Keys(schemaRoot.GetProperty("properties")).SequenceEqual(SkillParser.RootMembers) && Strings(schemaRoot.GetProperty("required")).SequenceEqual(SkillParser.RootRequired)
        && schemaRoot.GetProperty("properties").GetProperty("manifest_version").GetProperty("const").GetInt32() == SkillManifest.CurrentVersion, "schema root members, required list and version match the parser");
    Check(Strings(schemaRoot.GetProperty("properties").GetProperty("kind").GetProperty("enum")).SequenceEqual(SkillParser.Kinds)
        && Strings(Props("rule").GetProperty("severity").GetProperty("enum")).SequenceEqual(SkillParser.Severities)
        && Strings(Props("parameter").GetProperty("type").GetProperty("enum")).SequenceEqual(SkillParser.ParameterTypes)
        && Strings(Props("thresholdCheck").GetProperty("op").GetProperty("enum")).Concat(new[] { Props("inCheck").GetProperty("op").GetProperty("const").GetString()!, Props("ratioCheck").GetProperty("op").GetProperty("const").GetString()! })
            .OrderBy(op => op, StringComparer.Ordinal).SequenceEqual(SkillParser.CheckOps.OrderBy(op => op, StringComparer.Ordinal)), "schema kinds, severities, parameter types and check ops match the parser");
    Check(Keys(Props("requirement")).SequenceEqual(SkillParser.RequirementMembers) && Keys(Props("parameter")).SequenceEqual(SkillParser.ParameterMembers) && Keys(Props("rule")).SequenceEqual(SkillParser.RuleMembers)
        && Keys(Props("target")).SequenceEqual(SkillParser.TargetMembers) && Keys(Props("thresholdCheck")).SequenceEqual(SkillParser.ThresholdCheckMembers)
        && Keys(Props("inCheck")).SequenceEqual(SkillParser.InCheckMembers) && Keys(Props("ratioCheck")).SequenceEqual(SkillParser.RatioCheckMembers), "schema object members match the parser member lists");
    Check(Strings(Def("requirement").GetProperty("required")).SequenceEqual(new[] { "id", "range" }) && Strings(Def("parameter").GetProperty("required")).SequenceEqual(new[] { "name", "type", "default" })
        && Strings(Def("rule").GetProperty("required")).SequenceEqual(new[] { "id", "severity", "target", "check", "message" }) && Strings(Def("inCheck").GetProperty("required")).SequenceEqual(new[] { "op", "property", "values" }),
        "schema required lists match the parser");
    Check(Pattern("skillId") == "^" + SkillParser.IdPatternBody + "$" && Pattern("semver") == "^" + SkillParser.SemverPatternBody + "$" && Pattern("settingKey") == "^" + SkillParser.KeyPatternBody + "$"
        && Pattern("name") == "^" + SkillParser.NamePatternBody + "$" && Props("rule").GetProperty("id").GetProperty("pattern").GetString() == "^" + SkillParser.RuleIdPatternBody + "$"
        && Props("target").GetProperty("room").GetProperty("pattern").GetString() == "^" + SkillParser.RoomPatternBody + "$"
        && schemaRoot.GetProperty("properties").GetProperty("defaults").GetProperty("propertyNames").GetProperty("$ref").GetString() == "#/$defs/settingKey", "schema patterns are the parser patterns");
    var properties = schemaRoot.GetProperty("properties");
    Check(properties.GetProperty("requires").GetProperty("maxItems").GetInt32() == SkillParser.MaxRequires && properties.GetProperty("conflicts_with").GetProperty("maxItems").GetInt32() == SkillParser.MaxConflicts
        && properties.GetProperty("parameters").GetProperty("maxItems").GetInt32() == SkillParser.MaxParameters && properties.GetProperty("rules").GetProperty("maxItems").GetInt32() == SkillParser.MaxRules
        && properties.GetProperty("defaults").GetProperty("maxProperties").GetInt32() == SkillParser.MaxDefaults && Props("inCheck").GetProperty("values").GetProperty("maxItems").GetInt32() == SkillParser.MaxInValues
        && Def("text").GetProperty("maxLength").GetInt32() == SkillParser.MaxStringChars && Def("range").GetProperty("maxLength").GetInt32() == SkillParser.MaxRangeChars, "schema size limits match the parser");
    Check(new[] { "requirement", "parameter", "rule", "target", "thresholdCheck", "inCheck", "ratioCheck" }.All(name => Def(name).GetProperty("additionalProperties").ValueKind == JsonValueKind.False)
        && Def("target").GetProperty("maxProperties").GetInt32() == 1 && Def("target").GetProperty("minProperties").GetInt32() == 1, "every schema object is closed and targets take exactly one selector");
    Check(Def("int32").GetProperty("maximum").GetInt64() == int.MaxValue && Def("int32").GetProperty("minimum").GetInt64() == int.MinValue
        && Def("int32").GetProperty("description").GetString()!.Contains("integer token", StringComparison.Ordinal)
        && properties.GetProperty("manifest_version").GetProperty("description").GetString()!.Contains("integer token", StringComparison.Ordinal), "schema integers are 32-bit and document the integer-token rule");
    Check(Pattern("range") == "^" + SkillParser.RangePatternBody + "$", "schema range pattern is the parser range pattern");
    var typed = Def("parameter").GetProperty("allOf").EnumerateArray().Select(branch => branch.GetProperty("if").GetProperty("properties").GetProperty("type").GetProperty("const").GetString()!).ToArray();
    Check(typed.SequenceEqual(SkillParser.ParameterTypes), "schema types each parameter default and range by its declared type", string.Join(",", typed));

    // ---- P5-03 contrasting style packs (tests/skills/StyleSkillTests.cs) ----
    StyleSkillTests.Run(root, Check);

    // ---- P5-05 element QA recipe pack and element validator (tests/skills/ElementSkillTests.cs) ----
    ElementSkillTests.Run(root, Check);

    // ---- P10-06 jurisdiction rule-pack registry (tests/skills/JurisdictionTests.cs) ----
    JurisdictionTests.Run(root, Check, TempRoot);

    // ---- P5-07 vivienda unifamiliar typology programme pack and resolver (tests/skills/TypologySkillTests.cs) ----
    TypologySkillTests.Run(root, Check);

    // ---- Scale: 20000-skill chain and cycle built in code (no recursion) ----
    const int Scale = 20000;
    string ChainId(int index) => "bim.chain.s" + index.ToString("D5", CultureInfo.InvariantCulture);
    var chainSkills = Enumerable.Range(0, Scale).Select(index => index + 1 < Scale ? Coded(ChainId(index), ChainId(index + 1)) : Coded(ChainId(index))).ToArray();
    var clock = Stopwatch.StartNew();
    var longChain = SkillResolver.Resolve(new SkillCatalog(chainSkills), Requests(ChainId(0)));
    Check(longChain.Succeeded && longChain.Skills.Count == Scale && longChain.Skills[0].Id == ChainId(Scale - 1) && longChain.Skills[Scale - 1].Id == ChainId(0),
        "a 20000-skill chain resolves, deepest requirement first", Issues(longChain));
    var cycleSkills = chainSkills.ToArray();
    cycleSkills[Scale - 1] = Coded(ChainId(Scale - 1), ChainId(0));
    var longCycle = SkillResolver.Resolve(new SkillCatalog(cycleSkills), Requests(ChainId(Scale / 2)));
    var cycleIssue = longCycle.Issues.FirstOrDefault(issue => issue.Code == SkillCodes.DependencyCycle);
    Check(longCycle.Issues.Count == 1 && cycleIssue is not null && cycleIssue.Subject == ChainId(0)
        && cycleIssue.Detail.StartsWith(ChainId(0) + " -> " + ChainId(1) + " -> ", StringComparison.Ordinal) && cycleIssue.Detail.EndsWith(" -> " + ChainId(Scale - 1) + " -> " + ChainId(0), StringComparison.Ordinal),
        "a 20000-skill cycle is reported once", longCycle.Issues.Count.ToString(CultureInfo.InvariantCulture) + " issues; " + (cycleIssue?.Detail.Length ?? 0).ToString(CultureInfo.InvariantCulture) + " chars");
    Check(clock.Elapsed < TimeSpan.FromSeconds(30), "20000-skill resolution stays fast", clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
}
catch (Exception error)
{
    Console.WriteLine("::error::skills FAIL unexpected " + error.GetType().Name + ": " + error.Message);
    failed++;
}
finally
{
    foreach (var temp in temps)
    {
        try { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

Console.WriteLine(JsonSerializer.Serialize(new { passed, failed, skipped, skills_contract = failed == 0 }));
Environment.ExitCode = failed == 0 && passed > 0 ? 0 : 1;
