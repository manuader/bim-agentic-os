using Bim.Core;
using Bim.ParityCensus.Tests;
using System.Text.Json;
using System.Text.Json.Nodes;
// P9-04 oracle: capabilities/revit-parity-census.json lists Revit desktop functions by area, each with the catalogue
// commands that cover it (every name must be in RevitCommandCatalogue.All and every catalogue command must appear),
// one of the eight capability states, evidence files that exist and docs/features.json ids whose status bounds the
// claim through feature_coverage (each command of a verified-in-revit entry must be covered by a cited verified
// feature whose scope quotes verified_scope) and notes that docs/revit-playbook.md does not contradict. The per-area summary
// capabilities/revit-parity-summary.md must equal the one generated here. The rules are first checked against
// small in-memory censuses so each refusal is shown to fire.
int passed = 0, failed = 0;

void Check(bool condition, string name, string detail = "")
{
    if (condition) { Console.WriteLine("PASS " + name); passed++; return; }
    Console.WriteLine("::error::parity-census FAIL " + name + (detail.Length > 0 ? " | " + detail : ""));
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

JsonArray Arr(params string[] values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

// ---- Rules on a small census: one verified wall, one compiled query, one unsupported MEP function.
var fakeCatalogue = new[] { "wall.create", "query.levels" };
var fakeLedger = new Dictionary<string, LedgerFeature>(StringComparer.Ordinal)
{
    ["feat-verified"] = new("feat-verified", "verified-in-revit", "Owner session: wall create 4/4 OK on a test copy."),
    ["feat-compiled"] = new("feat-compiled", "compiled", "Executor compiled only."),
    ["feat-ifc"] = new("feat-ifc", "integration-tested (ifc)", "IFC round trip."),
};
const string FakePlaybook = "Lessons: query the levels first with `query.levels`; curtain_wall.create snaps to joined walls.";
bool FakeExists(string path) => path is "AGENTS.md" or "tests/x/Program.cs" or "runtimes/x/Executor.cs" or "tests/golden/README.md";
JsonObject Entry(string id, string area, string state, string[] commands, string[] features, string[] evidence) => new()
{
    ["id"] = id, ["area"] = area, ["revit_function"] = "Function " + id, ["commands"] = Arr(commands), ["state"] = state,
    ["features"] = Arr(features), ["evidence"] = Arr(evidence),
};
JsonObject Base()
{
    var wall = Entry("arch.wall", "architecture", "verified-in-revit", new[] { "wall.create" }, new[] { "feat-verified" }, new[] { "AGENTS.md" });
    wall["verified_scope"] = "wall create 4/4 OK on a test copy";
    return new JsonObject
    {
        ["schema_version"] = 1,
        ["states"] = Arr(ParityCensus.States),
        ["areas"] = Arr("architecture", "mep"),
        ["feature_coverage"] = new JsonObject
        {
            ["feat-verified"] = new JsonObject { ["commands"] = Arr("wall.create"), ["paths"] = Arr("typed-batch") },
            ["feat-compiled"] = new JsonObject { ["commands"] = Arr("query.levels") },
        },
        ["entries"] = new JsonArray(
            wall,
            Entry("query.levels", "architecture", "compiled", new[] { "query.levels" }, new[] { "feat-compiled" }, new[] { "runtimes/x/Executor.cs" }),
            Entry("mep.duct", "mep", "unsupported", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())),
    };
}
JsonObject At(JsonObject census, int index) => (JsonObject)census["entries"]![index]!;
JsonObject Coverage(JsonObject census) => (JsonObject)census["feature_coverage"]!;
List<string> Run(JsonObject census) => ParityCensus.Validate(census, fakeCatalogue, fakeLedger, FakeExists, FakePlaybook);
JsonObject WithVia()
{
    var census = Base();
    var level = Entry("arch.level", "architecture", "verified-in-revit", Array.Empty<string>(), new[] { "feat-verified" }, new[] { "tests/x/Program.cs" });
    level["via"] = "typed-batch";
    level["verified_scope"] = "wall create 4/4 OK on a test";
    ((JsonArray)census["entries"]!).Add(level);
    return census;
}

var baseErrors = Run(Base());
Check(baseErrors.Count == 0, "a well-formed census passes", string.Join("; ", baseErrors));
Check(Run(WithVia()).Count == 0, "a non-catalogue path (via) covered by a verified feature passes", string.Join("; ", Run(WithVia())));
var neverRunElsewhere = Base();
At(neverRunElsewhere, 2)["notes"] = "Ducts were never run in Revit.";
Check(Run(neverRunElsewhere).Count == 0, "a 'never run in Revit' note passes when the playbook names none of its commands", string.Join("; ", Run(neverRunElsewhere)));
Check(ParityCensus.MentionsCommand(FakePlaybook, "query.levels") && !ParityCensus.MentionsCommand(FakePlaybook, "wall.create") && !ParityCensus.MentionsCommand("Use curtain_wall.creates", "curtain_wall.create"),
    "playbook command matching is by whole command name (curtain_wall.create does not mention wall.create)");

var mutations = new (string Name, Action<JsonObject> Mutate, string Code)[]
{
    ("duplicate id", c => At(c, 1)["id"] = "arch.wall", "id_duplicate:arch.wall"),
    ("malformed id", c => At(c, 1)["id"] = "Levels", "id_invalid:entries[1]"),
    ("command not in the catalogue", c => At(c, 1)["commands"] = Arr("query.levels", "wall.delete"), "command_unknown:query.levels:wall.delete"),
    ("duplicate command in an entry", c => At(c, 1)["commands"] = Arr("query.levels", "query.levels"), "command_duplicate:query.levels"),
    ("catalogue read command absent", c => c["entries"]!.AsArray().RemoveAt(1), "command_uncovered:query.levels"),
    ("catalogue write command absent", c => c["entries"]!.AsArray().RemoveAt(0), "command_uncovered:wall.create"),
    ("state outside the eight", c => At(c, 1)["state"] = "tested", "state_invalid:query.levels"),
    ("state without its (ifc) qualifier", c => At(c, 1)["state"] = "integration-tested", "state_invalid:query.levels"),
    ("states list altered", c => c["states"] = Arr(ParityCensus.States.Take(7).ToArray()), "states_list"),
    ("unknown area", c => At(c, 0)["area"] = "kitchen", "area_unknown:arch.wall"),
    ("missing revit function", c => At(c, 1)["revit_function"] = " ", "revit_function_missing:query.levels"),
    ("evidence path missing", c => At(c, 1)["evidence"] = Arr("tests/missing.cs"), "evidence_not_found:query.levels:tests/missing.cs"),
    ("evidence path escapes the repository", c => At(c, 1)["evidence"] = Arr("../AGENTS.md"), "evidence_not_found:query.levels:../AGENTS.md"),
    ("absolute evidence path", c => At(c, 1)["evidence"] = Arr("/AGENTS.md"), "evidence_not_found:query.levels:/AGENTS.md"),
    ("claimed state without evidence", c => At(c, 1)["evidence"] = Arr(), "evidence_empty:query.levels"),
    ("compiled without a core/runtimes/adapters/interfaces file", c => At(c, 1)["evidence"] = Arr("AGENTS.md"), "compiled_without_source:query.levels"),
    ("unknown feature id", c => At(c, 1)["features"] = Arr("nope"), "feature_unknown:query.levels:nope"),
    ("cited feature that covers none of the entry's commands", c => At(c, 1)["features"] = Arr("feat-compiled", "feat-ifc"), "feature_not_covering:query.levels:feat-ifc"),
    ("verified-in-revit citing only a compiled feature", c => At(c, 0)["features"] = Arr("feat-compiled"), "verified_without_feature:arch.wall"),
    ("verified-in-revit with features = []", c => { At(c, 1)["state"] = "verified-in-revit"; At(c, 1)["features"] = Arr(); }, "verified_without_feature:query.levels"),
    ("query.levels verified with a feature mapped only to wall.create", c => { At(c, 1)["state"] = "verified-in-revit"; At(c, 1)["features"] = Arr("feat-verified"); At(c, 1)["verified_scope"] = "wall create 4/4 OK on a test copy"; }, "verified_command_uncovered:query.levels:query.levels"),
    ("verified scope not quoted by the feature", c => At(c, 0)["verified_scope"] = "wall create 5/5 OK on a test copy", "verified_scope_mismatch:arch.wall"),
    ("verified scope missing", c => At(c, 0).Remove("verified_scope"), "verified_scope_mismatch:arch.wall"),
    ("verified scope shorter than 20 characters", c => At(c, 0)["verified_scope"] = "wall create", "verified_scope_too_short:arch.wall"),
    ("verified scope on an unverified entry", c => At(c, 1)["verified_scope"] = "Executor compiled only", "verified_scope_unexpected:query.levels"),
    ("state above the cited feature covering the command", c => { At(c, 1)["state"] = "unit-tested"; At(c, 1)["evidence"] = Arr("tests/x/Program.cs"); }, "state_exceeds_coverage:query.levels:query.levels"),
    ("state above compiled with no covering feature", c => { At(c, 1)["state"] = "unit-tested"; At(c, 1)["features"] = Arr(); At(c, 1)["evidence"] = Arr("tests/x/Program.cs"); }, "state_exceeds_coverage:query.levels:query.levels"),
    ("unit-tested without a test file", c => { At(c, 1)["state"] = "unit-tested"; At(c, 1)["features"] = Arr(); }, "unit_tested_without_test:query.levels"),
    ("unit-tested with only tests/golden evidence", c => { At(c, 1)["state"] = "unit-tested"; At(c, 1)["evidence"] = Arr("tests/golden/README.md"); }, "unit_tested_without_test:query.levels"),
    ("integration-tested without an ifc feature", c => { At(c, 1)["state"] = "integration-tested (ifc)"; At(c, 1)["features"] = Arr(); }, "integration_without_feature:query.levels"),
    ("unsupported but naming a command", c => { At(c, 2)["commands"] = Arr("query.levels"); }, "unsupported_with_path:mep.duct"),
    ("claimed state with no command or path", c => { At(c, 2)["state"] = "compiled"; At(c, 2)["evidence"] = Arr("runtimes/x/Executor.cs"); }, "state_without_path:mep.duct"),
    ("via next to commands", c => { At(c, 1)["via"] = "typed-batch"; }, "via_with_commands:query.levels"),
    ("via without a feature", c => { At(c, 2)["state"] = "compiled"; At(c, 2)["evidence"] = Arr("runtimes/x/Executor.cs"); At(c, 2)["via"] = "manual pipe"; }, "via_without_feature:mep.duct"),
    ("empty via", c => { At(c, 2)["via"] = ""; }, "via_invalid:mep.duct"),
    ("'never run in Revit' note on a command the playbook used", c => At(c, 1)["notes"] = "Executor never run in Revit.", "notes_contradict_playbook:query.levels:query.levels"),
    ("empty notes", c => At(c, 1)["notes"] = " ", "notes_invalid:query.levels"),
    ("feature coverage missing", c => c.Remove("feature_coverage"), "feature_coverage_missing"),
    ("coverage for an unknown feature", c => Coverage(c)["nope"] = new JsonObject { ["commands"] = Arr("wall.create") }, "coverage_feature_unknown:nope"),
    ("coverage naming an unknown command", c => Coverage(c)["feat-compiled"] = new JsonObject { ["commands"] = Arr("query.levels", "wall.delete") }, "coverage_command_unknown:feat-compiled:wall.delete"),
    ("coverage that covers nothing", c => Coverage(c)["feat-ifc"] = new JsonObject { ["commands"] = Arr() }, "coverage_invalid:feat-ifc"),
    ("wrong schema version", c => c["schema_version"] = 2, "schema_version"),
};
foreach (var (name, mutate, code) in mutations)
{
    var census = Base();
    mutate(census);
    var errors = Run(census);
    Check(errors.Contains(code), "refused: " + name + " (" + code + ")", string.Join("; ", errors));
}
var viaUncovered = WithVia();
At(viaUncovered, 3)["via"] = "other-path";
Check(Run(viaUncovered).Contains("verified_command_uncovered:arch.level:via:other-path"), "refused: verified via path no cited verified feature covers", string.Join("; ", Run(viaUncovered)));

// Summary: counts per area and state; independent of entry order, rows follow the "areas" order.
var summary = ParityCensus.Summary(Base(), fakeCatalogue.Length);
var reordered = Base();
var reversedEntries = reordered["entries"]!.AsArray().Select(node => node!.DeepClone()).Reverse().ToArray();
reordered["entries"] = new JsonArray(reversedEntries);
Check(ParityCensus.Summary(reordered, fakeCatalogue.Length) == summary, "summary does not depend on entry order");
var areasSwapped = Base();
areasSwapped["areas"] = Arr("mep", "architecture");
var swapped = ParityCensus.Summary(areasSwapped, fakeCatalogue.Length);
Check(swapped != summary && swapped.IndexOf("| mep |", StringComparison.Ordinal) < swapped.IndexOf("| architecture |", StringComparison.Ordinal), "summary rows follow the areas order");
Check(summary.Contains("| architecture | 0 | 1 | 0 | 0 | 1 | 0 | 0 | 0 | 2 |\n", StringComparison.Ordinal)
    && summary.Contains("| mep | 0 | 0 | 0 | 0 | 0 | 0 | 1 | 0 | 1 |\n", StringComparison.Ordinal)
    && summary.Contains("| all | 0 | 1 | 0 | 0 | 1 | 0 | 1 | 0 | 3 |\n", StringComparison.Ordinal)
    && summary.Contains("Entries: 3. Catalogue commands referenced: 2 of 2.", StringComparison.Ordinal), "summary counts per area and state", summary);

// ---- The checked-in census against the real catalogue, ledger and files.
var root = RepoRoot();
var catalogue = RevitCommandCatalogue.All.Select(spec => spec.Name).ToArray();
var ledger = new Dictionary<string, LedgerFeature>(StringComparer.Ordinal);
using (var features = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "docs", "features.json"))))
{
    foreach (var item in features.RootElement.GetProperty("features").EnumerateArray())
    {
        var id = item.GetProperty("id").GetString()!;
        var scope = item.TryGetProperty("scope", out var scopeElement) && scopeElement.ValueKind == JsonValueKind.String ? scopeElement.GetString()! : "";
        ledger[id] = new LedgerFeature(id, item.GetProperty("status").GetString()!, scope);
    }
}
var censusPath = Path.Combine(root, "capabilities", "revit-parity-census.json");
var summaryPath = Path.Combine(root, "capabilities", "revit-parity-summary.md");
JsonNode? real = null;
try { real = JsonNode.Parse(File.ReadAllText(censusPath)); }
catch (Exception error) when (error is IOException or JsonException) { Console.WriteLine("census unreadable: " + error.Message); }
Check(real is JsonObject, "capabilities/revit-parity-census.json exists and is a JSON object");
if (real is JsonObject checkedIn)
{
    var playbook = File.ReadAllText(Path.Combine(root, "docs", "revit-playbook.md"));
    var errors = ParityCensus.Validate(checkedIn, catalogue, ledger, path => File.Exists(Path.Combine(root, path)), playbook);
    Check(errors.Count == 0, "census matches the catalogue (" + catalogue.Length + " commands), the feature ledger and the evidence files", string.Join("; ", errors.Take(40)));

    // Revit functions the board requires the census to name (MEP ones stay listed even while unsupported).
    var required = new[]
    {
        "arch.wall.straight", "arch.wall.curved", "arch.wall.edit_profile", "arch.wall.attach_top_base", "arch.door.place", "arch.window.place",
        "arch.floor.sketch", "arch.floor.opening", "arch.roof.footprint", "arch.roof.extrusion", "arch.ceiling.create", "arch.stair.straight_run",
        "arch.railing.sketch", "arch.curtain_wall.create", "arch.room.place", "arch.opening.shaft", "arch.column.architectural", "arch.level.create",
        "struct.grid.create", "struct.column.vertical", "struct.beam.straight", "struct.foundation.isolated", "struct.foundation.wall",
        "content.family.load", "content.component.place", "content.material.create", "content.material.edit", "content.type.change",
        "content.type.wall_layers", "site.toposolid.create", "views.plan.create", "views.section.create", "views.3d.create",
        "doc.dimension.linear", "doc.tag.category", "doc.schedule.create", "doc.sheet.create", "doc.sheet.place_view",
        "file.save", "file.export_image", "mep.duct.create", "mep.pipe.create", "mep.systems.create",
    };
    var ids = (checkedIn["entries"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(entry => ParityCensus.Str(entry["id"])).OfType<string>().ToHashSet(StringComparer.Ordinal);
    var missing = required.Where(id => !ids.Contains(id)).ToArray();
    Check(missing.Length == 0, "census names every required Revit function", string.Join(", ", missing));
    var areas = ParityCensus.Strings(checkedIn["areas"]) ?? new List<string>();
    Check(new[] { "architecture", "structure", "mep", "site", "views", "documentation", "content", "modify", "query", "file", "collaboration" }.All(area => areas.Contains(area, StringComparer.Ordinal)), "census covers the required areas", string.Join(",", areas));

    var expected = ParityCensus.Summary(checkedIn, catalogue.Length);
    if (args.Contains("--write-summary")) File.WriteAllText(summaryPath, expected);
    var actual = File.Exists(summaryPath) ? File.ReadAllText(summaryPath).Replace("\r\n", "\n", StringComparison.Ordinal) : "";
    var expectedLines = expected.Split('\n');
    var actualLines = actual.Split('\n');
    var firstDiff = Enumerable.Range(0, Math.Max(expectedLines.Length, actualLines.Length)).FirstOrDefault(i => i >= expectedLines.Length || i >= actualLines.Length || expectedLines[i] != actualLines[i], -1);
    Check(actual == expected, "capabilities/revit-parity-summary.md equals the generated summary",
        firstDiff < 0 ? "" : "line " + (firstDiff + 1) + " expected '" + (firstDiff < expectedLines.Length ? expectedLines[firstDiff] : "<end>") + "' got '" + (firstDiff < actualLines.Length ? actualLines[firstDiff] : "<end>") + "'");
}

Console.WriteLine(JsonSerializer.Serialize(new { passed, failed, parity_census = failed == 0 }));
Environment.ExitCode = failed == 0 && passed > 0 ? 0 : 1;
