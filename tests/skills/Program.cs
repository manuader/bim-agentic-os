using Bim.Core;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
// P5-01 domain skill manifest oracle: strict parser refusals with JSON paths, semver ranges, the safe directory loader
// (temp fixtures, symlinks), the resolver (closure, version ranges, conflicts, cycles, priority and precedence of
// defaults, traceability, determinism, 20k-skill scale), the checked-in sample skills, the JSON Schema document and the
// agreement corpus shared with tests/skills/check_schema.py. Unit-tested only.
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
    Check(samples is { Skills.Count: 2 } && samples.Skills[0].Id == "bim.qa.room-basics" && samples.Skills[1].Id == "bim.typology.dwelling", "skills/ holds the qa and typology samples in ordinal order",
        samples is null ? "" : string.Join(",", samples.Skills.Select(skill => skill.Id)));
    Check(samples?.Find("bim.qa.room-basics") is { Kind: SkillKind.Qa, Priority: 50 } qa && qa.Rules.Select(rule => rule.Check.Op).Distinct().Count() == 4
        && samples.Find("bim.typology.dwelling") is { Kind: SkillKind.Typology, Priority: 20 } dwelling && dwelling.Rules.Any(rule => rule.Check.Op == SkillCheckOp.RatioMin),
        "sample skills are one qa and one typology and together use every check op");
    Check(samples is not null && samples.Skills.All(skill => skill.Rules.All(rule => rule.Citation is null || rule.Citation.Contains("not normative", StringComparison.Ordinal))
        && skill.Description.Contains("not a building code", StringComparison.OrdinalIgnoreCase)), "sample skills state they are illustrative, not a building code");
    if (samples is not null)
    {
        var resolved = SkillResolver.Resolve(samples, Requests("bim.typology.dwelling", "bim.qa.room-basics"));
        Check(resolved.Succeeded && resolved.Skills.Select(skill => skill.Id).SequenceEqual(new[] { "bim.qa.room-basics", "bim.typology.dwelling" }), "samples resolve together", Issues(resolved));
        Check(resolved.Setting("room.min_clear_height_m") is { Decision: SkillDecision.Priority, Source: { SkillId: "bim.qa.room-basics", Origin: "default", Key: "room.min_clear_height_m" }, Overridden.Count: 1 } clear
            && clear.Value == SkillValue.Of(2.5) && clear.Overridden[0] == new SkillTrace("bim.typology.dwelling", SkillTrace.DefaultOrigin, "room.min_clear_height_m"),
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
