using Bim.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
// P10-03 oracle: permission policy (read / write-copies / write-real-project per actor, real projects denied unless
// explicitly enabled) and the append-only SHA-256 hash-chained JSON Lines audit log with its tamper verifier.
// Pure contract tests with an injected clock; no Revit is involved.
const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
const string CopiesRoot = @"C:\Repo\.bim\native-runs";
const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
const string RealPath = @"C:\Projects\Real\Tower.rvt";
const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
int passed = 0, failed = 0;
var work = Path.Combine(Path.GetTempPath(), "audit-tests-" + Guid.NewGuid().ToString("N"));

void Check(bool condition, string name, string detail = "")
{
    if (condition) { Console.WriteLine("PASS " + name); passed++; return; }
    Console.WriteLine("::error::audit FAIL " + name + (detail.Length > 0 ? " | " + detail : ""));
    failed++;
}

RevitCommandEnvelope Envelope(string command, string path, string operationId = "op-1")
{
    JsonNode commandArgs = command == "wall.create"
        ? new JsonObject { ["level"] = "Level 1", ["type"] = "Generic - 200mm", ["height_mm"] = 3000, ["curve"] = new JsonObject { ["kind"] = "line", ["start"] = new JsonArray(0, 0), ["end"] = new JsonArray(5000, 0) } }
        : new JsonObject();
    var json = new JsonObject
    {
        ["protocol_version"] = 1,
        ["operation_id"] = operationId,
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
        ["command"] = command,
        ["args"] = commandArgs,
    };
    return RevitCommandProtocol.Parse(json.ToJsonString());
}

Func<DateTimeOffset> Clock()
{
    var next = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
    return () => { var now = next; next = next.AddSeconds(1); return now; };
}

string? PolicyCode(Action action)
{
    try { action(); return null; }
    catch (PermissionPolicyException error) { return error.Code; }
}
string? AuditCode(Action action)
{
    try { action(); return null; }
    catch (AuditLogException error) { return error.Code; }
}
string Describe(PermissionDecision decision) => (decision.Allowed ? "allow " : "deny ") + decision.Reason + " required " + decision.Required;
string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

var copies = PermissionPolicy.DefaultCopiesRoots(@"C:\Repo");
var grants = new[]
{
    new PermissionGrant("reader", PermissionLevel.Read),
    new PermissionGrant("copier", PermissionLevel.WriteCopies),
    new PermissionGrant("owner", PermissionLevel.WriteRealProject),
};

try
{
    // ---- Permission levels and classification ----
    Check(PermissionLevel.Read < PermissionLevel.WriteCopies && PermissionLevel.WriteCopies < PermissionLevel.WriteRealProject, "levels are ordered read < write-copies < write-real-project");
    var policy = PermissionPolicy.Create(grants, Array.Empty<string>(), copies);
    var query = Envelope("query.levels", RealPath);
    var copyWrite = Envelope("wall.create", FixturePath);
    var realWrite = Envelope("wall.create", RealPath);
    Check(policy.Classify(query) == PermissionLevel.Read, "a catalogue query is classified read, even on a real project");
    Check(policy.Classify(copyWrite) == PermissionLevel.WriteCopies, "a write under an enabled test root is write-copies");
    Check(policy.Classify(realWrite) == PermissionLevel.WriteRealProject, "a write outside every test root is write-real-project");
    // Every catalogue command (RevitCommandCatalogue.All: v1, v2 and later additions), not a fixed list.
    foreach (var spec in RevitCommandCatalogue.All)
    {
        var expected = spec.Writes ? PermissionLevel.WriteCopies : PermissionLevel.Read;
        Check(PermissionPolicy.RequiredLevel(spec.Name, FixturePath, copies) == expected, "catalogue " + spec.Name + " on a copy requires " + expected);
        var onReal = spec.Writes ? PermissionLevel.WriteRealProject : PermissionLevel.Read;
        Check(PermissionPolicy.RequiredLevel(spec.Name, RealPath, copies) == onReal, "catalogue " + spec.Name + " on a real project requires " + onReal);
    }
    // P1-02: the generic element queries are read on a copy and on a real project.
    foreach (var command in new[] { "query.elements", "query.inspect" })
    {
        Check(RevitCommandCatalogue.Elements.Any(spec => spec.Name == command && !spec.Writes), "element query catalogue has read-only " + command);
        Check(PermissionPolicy.RequiredLevel(command, FixturePath, copies) == PermissionLevel.Read && PermissionPolicy.RequiredLevel(command, RealPath, copies) == PermissionLevel.Read, command + " requires read on a copy and on a real project");
    }
    // chat-roof-gable: query.materials is read anywhere; roof_type.create writes copies only.
    foreach (var (command, onCopy, onReal) in new (string, PermissionLevel, PermissionLevel)[] { ("query.materials", PermissionLevel.Read, PermissionLevel.Read), ("roof_type.create", PermissionLevel.WriteCopies, PermissionLevel.WriteRealProject) })
    {
        Check(RevitCommandCatalogue.Materials.Any(spec => spec.Name == command), "materials catalogue has " + command);
        Check(PermissionPolicy.RequiredLevel(command, FixturePath, copies) == onCopy && PermissionPolicy.RequiredLevel(command, RealPath, copies) == onReal, command + " requires " + onCopy + " on a copy and " + onReal + " on a real project");
    }
    // wall.set_profile changes an existing wall: a destructive write, copies only.
    Check(RevitCommandCatalogue.WallProfile.Any(spec => spec.Name == "wall.set_profile" && spec.Writes) && RevitCommandCatalogue.IsDestructive("wall.set_profile"), "wall profile catalogue has destructive write wall.set_profile");
    Check(PermissionPolicy.RequiredLevel("wall.set_profile", FixturePath, copies) == PermissionLevel.WriteCopies && PermissionPolicy.RequiredLevel("wall.set_profile", RealPath, copies) == PermissionLevel.WriteRealProject, "wall.set_profile requires write-copies on a copy and write-real-project on a real project");
    // P9-07: roof.create_extrusion only adds a roof and its reference plane: an additive write, copies only.
    Check(RevitCommandCatalogue.RoofExtrusion.Any(spec => spec.Name == "roof.create_extrusion" && spec.Writes) && !RevitCommandCatalogue.IsDestructive("roof.create_extrusion"), "roof extrusion catalogue has additive write roof.create_extrusion");
    Check(PermissionPolicy.RequiredLevel("roof.create_extrusion", FixturePath, copies) == PermissionLevel.WriteCopies && PermissionPolicy.RequiredLevel("roof.create_extrusion", RealPath, copies) == PermissionLevel.WriteRealProject, "roof.create_extrusion requires write-copies on a copy and write-real-project on a real project");
    // P1-09: element.move changes existing elements (destructive), element.copy only adds; both copies only.
    foreach (var (command, destructive) in new[] { ("element.move", true), ("element.copy", false) })
    {
        Check(RevitCommandCatalogue.Transform.Any(spec => spec.Name == command && spec.Writes) && RevitCommandCatalogue.IsDestructive(command) == destructive, "transform catalogue has " + command + (destructive ? " (destructive write)" : " (additive write)"));
        Check(PermissionPolicy.RequiredLevel(command, FixturePath, copies) == PermissionLevel.WriteCopies && PermissionPolicy.RequiredLevel(command, RealPath, copies) == PermissionLevel.WriteRealProject, command + " requires write-copies on a copy and write-real-project on a real project");
    }
    // chat-materials-types: query.fill_patterns is read anywhere; the material and type writes need copies, and
    // material.set / element.change_type are destructive.
    foreach (var (command, writes, destructive) in new[] { ("query.fill_patterns", false, false), ("material.create", true, false), ("material.set", true, true), ("wall_type.create", true, false), ("floor_type.create", true, false), ("element.change_type", true, true) })
    {
        Check(RevitCommandCatalogue.MaterialTypes.Any(spec => spec.Name == command && spec.Writes == writes) && RevitCommandCatalogue.IsDestructive(command) == destructive, "material type catalogue has " + command + (writes ? " (write" + (destructive ? ", destructive)" : ")") : " (read-only)"));
        Check(PermissionPolicy.RequiredLevel(command, FixturePath, copies) == (writes ? PermissionLevel.WriteCopies : PermissionLevel.Read) && PermissionPolicy.RequiredLevel(command, RealPath, copies) == (writes ? PermissionLevel.WriteRealProject : PermissionLevel.Read), command + " permission levels follow read/write");
    }
    // chat-export-save: view.export_image is read anywhere (it does not change the model); document.save is a
    // destructive write (it overwrites the saved file), copies or owner-enabled folders only.
    Check(RevitCommandCatalogue.ExportSave.Any(spec => spec.Name == "view.export_image" && !spec.Writes) && !RevitCommandCatalogue.IsDestructive("view.export_image"), "export catalogue has read-only view.export_image");
    Check(PermissionPolicy.RequiredLevel("view.export_image", FixturePath, copies) == PermissionLevel.Read && PermissionPolicy.RequiredLevel("view.export_image", RealPath, copies) == PermissionLevel.Read, "view.export_image requires read on a copy and on a real project");
    Check(RevitCommandCatalogue.ExportSave.Any(spec => spec.Name == "document.save" && spec.Writes) && RevitCommandCatalogue.IsDestructive("document.save"), "save catalogue has destructive write document.save");
    Check(PermissionPolicy.RequiredLevel("document.save", FixturePath, copies) == PermissionLevel.WriteCopies && PermissionPolicy.RequiredLevel("document.save", RealPath, copies) == PermissionLevel.WriteRealProject, "document.save requires write-copies on a copy and write-real-project on a real project");
    // P2-05: query.journal reads the in-memory edit journal: read on a copy and on a real project, allowed for a reader.
    Check(RevitCommandCatalogue.Journal.Any(spec => spec.Name == "query.journal" && !spec.Writes) && !RevitCommandCatalogue.IsDestructive("query.journal"), "journal catalogue has read-only query.journal");
    Check(PermissionPolicy.RequiredLevel("query.journal", FixturePath, copies) == PermissionLevel.Read && PermissionPolicy.RequiredLevel("query.journal", RealPath, copies) == PermissionLevel.Read, "query.journal requires read on a copy and on a real project");
    Check(PermissionPolicy.Create(grants, Array.Empty<string>(), copies).Evaluate("reader", "query.journal", RealPath) is { Allowed: true, Reason: PermissionCodes.ReadAllowed }, "a reader may run query.journal on a real project");
    // P9-05: the five catalogue v2 commands keep the v1 read/write split.
    var v2Levels = new (string Command, PermissionLevel Copy, PermissionLevel Real)[]
    {
        ("query.roof_types", PermissionLevel.Read, PermissionLevel.Read),
        ("roof.create_footprint", PermissionLevel.WriteCopies, PermissionLevel.WriteRealProject),
        ("stair.create", PermissionLevel.WriteCopies, PermissionLevel.WriteRealProject),
        ("curtain_wall.create", PermissionLevel.WriteCopies, PermissionLevel.WriteRealProject),
        ("floor.create_opening", PermissionLevel.WriteCopies, PermissionLevel.WriteRealProject),
    };
    foreach (var (command, onCopy, onReal) in v2Levels)
    {
        Check(RevitCommandCatalogue.V2.Any(spec => spec.Name == command), "catalogue v2 has " + command);
        Check(PermissionPolicy.RequiredLevel(command, FixturePath, copies) == onCopy && PermissionPolicy.RequiredLevel(command, RealPath, copies) == onReal, "v2 " + command + " requires " + onCopy + " on a copy and " + onReal + " on a real project");
        var v2Policy = PermissionPolicy.Create(grants, Array.Empty<string>(), copies);
        var onCopyByReader = v2Policy.Evaluate("reader", command, FixturePath);
        var onRealByCopier = v2Policy.Evaluate("copier", command, RealPath);
        var onCopyByCopier = v2Policy.Evaluate("copier", command, FixturePath);
        if (onCopy == PermissionLevel.Read)
            Check(onCopyByReader.Allowed && onRealByCopier.Allowed && onRealByCopier.Reason == PermissionCodes.ReadAllowed, "v2 read " + command + " is allowed for a reader on any document", Describe(onRealByCopier));
        else
            Check(!onCopyByReader.Allowed && onCopyByReader.Reason == PermissionCodes.LevelInsufficient
                && onCopyByCopier.Allowed && onCopyByCopier.Reason == PermissionCodes.WriteCopiesAllowed
                && !onRealByCopier.Allowed && onRealByCopier.Reason == PermissionCodes.RealProjectNotEnabled,
                "v2 write " + command + ": reader denied, write-copies allowed on a copy, real project not enabled", Describe(onCopyByReader) + " / " + Describe(onRealByCopier));
    }
    // P9-02: the structure commands keep the read/write split: queries read anywhere, creators write copies only.
    foreach (var (command, writes) in new[] { ("query.grids", false), ("query.structural_types", false), ("grid.create", true), ("structural_column.create", true), ("structural_beam.create", true), ("foundation.create_isolated", true), ("foundation.create_wall", true) })
    {
        Check(RevitCommandCatalogue.Structure.Any(spec => spec.Name == command && spec.Writes == writes), "structure catalogue has " + (writes ? "write " : "read-only ") + command);
        var structurePolicy = PermissionPolicy.Create(grants, Array.Empty<string>(), copies);
        if (writes)
            Check(!structurePolicy.Evaluate("reader", command, FixturePath).Allowed && structurePolicy.Evaluate("copier", command, FixturePath).Allowed
                && structurePolicy.Evaluate("copier", command, RealPath) is { Allowed: false, Reason: PermissionCodes.RealProjectNotEnabled }, "structure write " + command + ": reader denied, write-copies allowed on a copy, real project not enabled");
        else
            Check(structurePolicy.Evaluate("reader", command, RealPath) is { Allowed: true, Reason: PermissionCodes.ReadAllowed }, "structure read " + command + " is allowed for a reader on a real project");
    }
    // P4-02: the documentation commands keep the read/write split: the title block query reads anywhere, creators write copies only.
    foreach (var (command, writes) in new[] { ("query.title_blocks", false), ("dimension.create_linear", true), ("tag.create", true), ("schedule.create", true), ("sheet.create", true), ("sheet.place_view", true) })
    {
        Check(RevitCommandCatalogue.Documentation.Any(spec => spec.Name == command && spec.Writes == writes), "documentation catalogue has " + (writes ? "write " : "read-only ") + command);
        var documentationPolicy = PermissionPolicy.Create(grants, Array.Empty<string>(), copies);
        if (writes)
            Check(!documentationPolicy.Evaluate("reader", command, FixturePath).Allowed && documentationPolicy.Evaluate("copier", command, FixturePath).Allowed
                && documentationPolicy.Evaluate("copier", command, RealPath) is { Allowed: false, Reason: PermissionCodes.RealProjectNotEnabled }, "documentation write " + command + ": reader denied, write-copies allowed on a copy, real project not enabled");
        else
            Check(documentationPolicy.Evaluate("reader", command, RealPath) is { Allowed: true, Reason: PermissionCodes.ReadAllowed }, "documentation read " + command + " is allowed for a reader on a real project");
    }
    // P1-05: element.set_parameter and element.delete are writes (dry runs included): copies only, never read level.
    foreach (var command in new[] { "element.set_parameter", "element.delete" })
    {
        Check(RevitCommandCatalogue.Modify.Any(spec => spec.Name == command && spec.Writes) && RevitCommandCatalogue.IsDestructive(command), "modify catalogue has destructive write " + command);
        var modifyPolicy = PermissionPolicy.Create(grants, Array.Empty<string>(), copies);
        Check(modifyPolicy.Evaluate("reader", command, FixturePath) is { Allowed: false, Reason: PermissionCodes.LevelInsufficient } && modifyPolicy.Evaluate("copier", command, FixturePath) is { Allowed: true, Reason: PermissionCodes.WriteCopiesAllowed }
            && modifyPolicy.Evaluate("copier", command, RealPath) is { Allowed: false, Reason: PermissionCodes.RealProjectNotEnabled } && modifyPolicy.Evaluate("owner", command, RealPath) is { Allowed: false, Reason: PermissionCodes.RealProjectNotEnabled },
            "modify write " + command + ": reader denied, write-copies allowed on a copy, real project not enabled even for the owner grant");
    }
    // Family commands: family.load and family_instance.place are additive writes, copies only.
    foreach (var command in new[] { "family.load", "family_instance.place" })
    {
        Check(RevitCommandCatalogue.Families.Any(spec => spec.Name == command && spec.Writes) && !RevitCommandCatalogue.IsDestructive(command), "family catalogue has additive write " + command);
        var familyPolicy = PermissionPolicy.Create(grants, Array.Empty<string>(), copies);
        Check(familyPolicy.Evaluate("reader", command, FixturePath) is { Allowed: false, Reason: PermissionCodes.LevelInsufficient } && familyPolicy.Evaluate("copier", command, FixturePath) is { Allowed: true, Reason: PermissionCodes.WriteCopiesAllowed }
            && familyPolicy.Evaluate("copier", command, RealPath) is { Allowed: false, Reason: PermissionCodes.RealProjectNotEnabled }, "family write " + command + ": reader denied, write-copies allowed on a copy, real project not enabled");
    }
    Check(PermissionPolicy.RequiredLevel("bogus.command", FixturePath, copies) is null, "an unknown command has no required level");
    var forged = realWrite with { Spec = RevitCommandCatalogue.Find("query.levels")! };
    Check(policy.Classify(forged) == PermissionLevel.WriteRealProject, "classification follows the catalogue entry of the command name, not a forged spec");
    var forgedDecision = PermissionPolicy.Create(grants, Array.Empty<string>(), copies).Evaluate("reader", forged);
    Check(!forgedDecision.Allowed && forgedDecision.Reason == PermissionCodes.RealProjectNotEnabled, "a forged read spec on a write command is still denied", Describe(forgedDecision));
    var unknownCommand = query with { Command = "bogus.command" };
    var unknownDecision = policy.Evaluate("owner", unknownCommand);
    Check(!unknownDecision.Allowed && unknownDecision.Reason == PermissionCodes.CommandUnknown && unknownDecision.Required is null, "a command missing from the catalogue is denied command_unknown", Describe(unknownDecision));

    // ---- Reader ----
    var d = policy.Evaluate("reader", query);
    Check(d.Allowed && d.Reason == PermissionCodes.ReadAllowed && d.Required == PermissionLevel.Read, "reader may run a query on any document", Describe(d));
    d = policy.Evaluate("reader", copyWrite);
    Check(!d.Allowed && d.Reason == PermissionCodes.LevelInsufficient && d.Required == PermissionLevel.WriteCopies, "reader may not write a test copy", Describe(d));
    d = policy.Evaluate("reader", realWrite);
    Check(!d.Allowed && d.Reason == PermissionCodes.RealProjectNotEnabled && d.Required == PermissionLevel.WriteRealProject, "reader write to a real project is denied real_project_not_enabled", Describe(d));

    // ---- Copier ----
    d = policy.Evaluate("copier", query);
    Check(d.Allowed && d.Reason == PermissionCodes.ReadAllowed, "write-copies actor may read", Describe(d));
    d = policy.Evaluate("copier", copyWrite);
    Check(d.Allowed && d.Reason == PermissionCodes.WriteCopiesAllowed && d.Required == PermissionLevel.WriteCopies, "write-copies actor may write a test copy", Describe(d));
    d = policy.Evaluate("copier", realWrite);
    Check(!d.Allowed && d.Reason == PermissionCodes.RealProjectNotEnabled, "write-copies actor may not write a real project that is not enabled", Describe(d));

    // ---- Owner level without and with enablement ----
    d = policy.Evaluate("owner", realWrite);
    Check(!d.Allowed && d.Reason == PermissionCodes.RealProjectNotEnabled, "write-real-project level alone does not allow a real project without enablement", Describe(d));
    d = policy.Evaluate("owner", copyWrite);
    Check(d.Allowed && d.Reason == PermissionCodes.WriteCopiesAllowed, "write-real-project actor may write a test copy", Describe(d));
    var enabled = PermissionPolicy.Create(grants, new[] { RealPath }, copies);
    d = enabled.Evaluate("owner", realWrite);
    Check(d.Allowed && d.Reason == PermissionCodes.RealProjectEnabled && d.Required == PermissionLevel.WriteRealProject, "write-real-project actor may write an explicitly enabled real project", Describe(d));
    d = enabled.Evaluate("copier", realWrite);
    Check(!d.Allowed && d.Reason == PermissionCodes.LevelInsufficient, "an enabled real project still needs the write-real-project level", Describe(d));
    d = enabled.Evaluate("reader", realWrite);
    Check(!d.Allowed && d.Reason == PermissionCodes.LevelInsufficient, "reader may not write an enabled real project", Describe(d));
    d = enabled.Evaluate("reader", Envelope("query.wall_types", RealPath));
    Check(d.Allowed && d.Reason == PermissionCodes.ReadAllowed, "reader may query an enabled real project", Describe(d));
    d = enabled.Evaluate("owner", Envelope("wall.create", @"C:\Projects\Real\Tower-other.rvt"));
    Check(!d.Allowed && d.Reason == PermissionCodes.RealProjectNotEnabled, "enablement names one document: a sibling is not enabled", Describe(d));
    d = enabled.Evaluate("owner", Envelope("wall.create", @"C:\Projects\Real\Tower.rvt\inner.rvt"));
    Check(!d.Allowed && d.Reason == PermissionCodes.RealProjectNotEnabled, "enablement is not a directory root: a path below it is not enabled", Describe(d));
    d = enabled.Evaluate("owner", Envelope("wall.create", @"c:/projects/REAL/tower.RVT"));
    Check(d.Allowed && d.Reason == PermissionCodes.RealProjectEnabled, "drive paths match the enablement case- and separator-insensitively like the copies guard", Describe(d));
    var unixEnabled = PermissionPolicy.Create(grants, new[] { "/srv/projects/Tower.rvt" }, Array.Empty<string>());
    d = unixEnabled.Evaluate("owner", Envelope("wall.create", "/srv/projects/Tower.rvt"));
    Check(d.Allowed && d.Reason == PermissionCodes.RealProjectEnabled, "a '/'-rooted enabled document is allowed", Describe(d));
    d = unixEnabled.Evaluate("owner", Envelope("wall.create", "/srv/projects/tower.rvt"));
    Check(!d.Allowed && d.Reason == PermissionCodes.RealProjectNotEnabled, "'/'-rooted enablement compares case-sensitively", Describe(d));
    Check(enabled.IsRealProjectEnabled(RealPath) && !policy.IsRealProjectEnabled(RealPath), "IsRealProjectEnabled reflects the enablement list");

    // ---- Actors ----
    d = policy.Evaluate("stranger", query);
    Check(!d.Allowed && d.Reason == PermissionCodes.ActorUnknown, "an actor without a grant is denied even a read", Describe(d));
    foreach (var (label, badActor) in new[] { ("empty", ""), ("blank", "   "), ("control", "ab\ncd"), ("too long", new string('a', PermissionPolicy.MaxActorChars + 1)), ("lone surrogate", "a" + (char)0xD800) })
    {
        d = policy.Evaluate(badActor, query);
        Check(!d.Allowed && d.Reason == PermissionCodes.ActorInvalid, "actor " + label + " is actor_invalid", Describe(d));
    }
    Check(PermissionPolicy.IsActor("claude-code:session-1") && PermissionPolicy.IsActor(new string('a', PermissionPolicy.MaxActorChars)), "actor ids up to 128 printable characters are valid");
    Check(policy.LevelOf("copier") == PermissionLevel.WriteCopies && policy.LevelOf("stranger") is null, "LevelOf returns the granted level or null");
    d = policy.Evaluate("Reader", query);
    Check(!d.Allowed && d.Reason == PermissionCodes.ActorUnknown, "actor ids compare ordinally (case-sensitive)", Describe(d));

    // ---- String overload and document paths ----
    d = policy.Evaluate("owner", "query.levels", "relative\\Tower.rvt");
    Check(!d.Allowed && d.Reason == PermissionCodes.DocumentPathInvalid, "a relative document path is denied document_path_invalid even for a read", Describe(d));
    d = policy.Evaluate("owner", "wall.create", @"C:\Repo\..\Projects\Tower.rvt");
    Check(!d.Allowed && d.Reason == PermissionCodes.DocumentPathInvalid, "a path with .. segments is denied document_path_invalid", Describe(d));
    d = policy.Evaluate("copier", "wall.create", FixturePath);
    Check(d.Allowed && d.Reason == PermissionCodes.WriteCopiesAllowed, "the string overload decides like the envelope overload", Describe(d));
    d = policy.Evaluate("copier", "wall.create", @"C:\Repo\.bim\native-runs-x\Tower.rvt");
    Check(!d.Allowed && d.Reason == PermissionCodes.RealProjectNotEnabled, "a sibling of the test root ('native-runs-x') is a real project", Describe(d));

    // ---- Determinism ----
    var reversed = PermissionPolicy.Create(grants.Reverse(), new[] { RealPath }, copies);
    var all = new[] { "reader", "copier", "owner", "stranger" };
    var envelopes = new[] { query, copyWrite, realWrite };
    Check(all.All(a => envelopes.All(env => enabled.Evaluate(a, env) == reversed.Evaluate(a, env))), "decisions are deterministic and independent of grant order");
    var guardPolicy = new RevitCommandPolicy(new[] { CopiesRoot });
    Check(RevitCopiesGuard.Check(true, RealPath, guardPolicy) == RevitCommandCodes.DocumentNotEnabled && RevitCopiesGuard.Check(true, FixturePath, guardPolicy) is null, "the copies guard keeps its own decision (unchanged)");

    // ---- Owner-file roots are not test copies ----
    var ownerRoots = RevitCommandPolicy.FromAllowlist("# owner enabled\n" + @"C:\Owner\Enabled" + "\n" + @"C:\Projects\Copies\Tower-copy.rvt" + "\n", CopiesRoot);
    const string OwnerDoc = @"C:\Owner\Enabled\Tower.rvt";
    Check(RevitCopiesGuard.Check(true, OwnerDoc, ownerRoots) is null, "precondition: the copies guard allows a write under an owner-file root");
    Check(copies.Count == 1 && RevitCopiesGuard.IsUnder(FixturePath, copies[0]) && policy.CopiesRoots.SequenceEqual(copies), "DefaultCopiesRoots is only <repository>/.bim/native-runs");
    d = policy.Evaluate("copier", Envelope("wall.create", OwnerDoc));
    Check(!d.Allowed && d.Reason == PermissionCodes.RealProjectNotEnabled && d.Required == PermissionLevel.WriteRealProject, "a write under an owner-file root is a real-project write: denied for a write-copies actor", Describe(d));
    d = policy.Evaluate("copier", Envelope("wall.create", @"C:\Projects\Copies\Tower-copy.rvt"));
    Check(!d.Allowed && d.Reason == PermissionCodes.RealProjectNotEnabled, "an owner-file exact document is not a test copy either", Describe(d));
    var ownerEnabled = PermissionPolicy.Create(grants, new[] { OwnerDoc }, copies);
    Check(ownerEnabled.Evaluate("owner", Envelope("wall.create", OwnerDoc)) is { Allowed: true, Reason: PermissionCodes.RealProjectEnabled } && !ownerEnabled.Evaluate("copier", Envelope("wall.create", OwnerDoc)).Allowed, "it needs write-real-project plus an exact enablement");
    var extraRoot = PermissionPolicy.Create(grants, Array.Empty<string>(), new[] { @"C:\Scratch\copies" });
    Check(extraRoot.Evaluate("copier", Envelope("wall.create", @"C:\Scratch\copies\a.rvt")) is { Allowed: true, Reason: PermissionCodes.WriteCopiesAllowed } && extraRoot.Classify(copyWrite) == PermissionLevel.WriteRealProject, "only the explicitly passed copies roots are write-copies");
    Check(PolicyCode(() => PermissionPolicy.Create(grants, Array.Empty<string>(), new[] { "copies" })) == PermissionCodes.PolicyInvalid && PolicyCode(() => PermissionPolicy.Create(grants, Array.Empty<string>(), new[] { @"D:\" })) == PermissionCodes.PolicyInvalid, "a relative or too-broad copies root is policy_invalid");

    // ---- Policy construction ----
    Check(PolicyCode(() => PermissionPolicy.Create(new[] { new PermissionGrant("a", PermissionLevel.Read), new PermissionGrant("a", PermissionLevel.WriteCopies) }, Array.Empty<string>(), copies)) == PermissionCodes.PolicyInvalid, "a duplicate actor grant is policy_invalid");
    Check(PolicyCode(() => PermissionPolicy.Create(new[] { new PermissionGrant("a", (PermissionLevel)99) }, Array.Empty<string>(), copies)) == PermissionCodes.PolicyInvalid, "an undefined level is policy_invalid");
    Check(PolicyCode(() => PermissionPolicy.Create(new[] { new PermissionGrant("bad\tactor", PermissionLevel.Read) }, Array.Empty<string>(), copies)) == PermissionCodes.PolicyInvalid, "an invalid actor id in a grant is policy_invalid");
    Check(PolicyCode(() => PermissionPolicy.Create(new[] { new PermissionGrant(PermissionAuditGate.InvalidActor, PermissionLevel.WriteRealProject) }, Array.Empty<string>(), copies)) == PermissionCodes.PolicyInvalid, "the gate's invalid-actor placeholder cannot be granted");
    Check(PolicyCode(() => PermissionPolicy.Create(grants, new[] { "Tower.rvt" }, copies)) == PermissionCodes.PolicyInvalid, "a relative enablement is policy_invalid");
    Check(PolicyCode(() => PermissionPolicy.Create(grants, new[] { @"C:\" }, copies)) == PermissionCodes.PolicyInvalid, "a drive-root enablement is policy_invalid");
    Check(PolicyCode(() => PermissionPolicy.Create(grants, new[] { "/" }, copies)) == PermissionCodes.PolicyInvalid, "a '/' enablement is policy_invalid");
    Check(PolicyCode(() => PermissionPolicy.Create(grants, new[] { RealPath }, copies)) is null && enabled.RealProjectEnablements.SequenceEqual(new[] { RealPath }), "a valid enablement is kept verbatim");

    // ---- Audit log: format pinned by a known answer ----
    Directory.CreateDirectory(work);
    var logPath = Path.Combine(work, "a", "audit.jsonl");
    var log = AuditLog.Open(logPath, Clock());
    Check(log.Head == new AuditAnchor(0, Genesis) && File.Exists(logPath) && new FileInfo(logPath).Length == 0, "a new log starts empty at the genesis anchor");
    var first = log.Append("copier", "op-1", "wall.create", FixturePath, new PermissionDecision(true, PermissionCodes.WriteCopiesAllowed, PermissionLevel.WriteCopies));
    var body = "{\"seq\":1,\"utc\":\"2026-11-02T10:00:00.0000000Z\",\"actor\":\"copier\",\"operation_id\":\"op-1\",\"command\":\"wall.create\",\"document\":\"C:\\\\Repo\\\\.bim\\\\native-runs\\\\run-1\\\\BIM-Agentic-Test.rvt\",\"decision\":\"allow\",\"reason\":\"write_copies_allowed\",\"prev_hash\":\"" + Genesis + "\"}";
    var expectedHash = Sha(body);
    var expectedLine = body[..^1] + ",\"hash\":\"" + expectedHash + "\"}\n";
    var firstBytes = File.ReadAllBytes(logPath);
    Check(Encoding.UTF8.GetString(firstBytes) == expectedLine, "entry line is the canonical JSON with hash = SHA-256 of the entry without hash, LF-terminated", Encoding.UTF8.GetString(firstBytes));
    Check(first == new AuditEntry(1, "2026-11-02T10:00:00.0000000Z", "copier", "op-1", "wall.create", FixturePath, "allow", "write_copies_allowed", Genesis, expectedHash), "Append returns the entry it wrote");
    Check(AuditLogFormat.ComputeHash(first) == expectedHash && AuditLogFormat.Line(first) == expectedLine[..^1], "ComputeHash and Line expose the format");
    Check(firstBytes.Length > 0 && firstBytes[0] != 0xEF, "no byte order mark");
    var second = log.Append("reader", "op-2", "wall.create", RealPath, new PermissionDecision(false, PermissionCodes.RealProjectNotEnabled, PermissionLevel.WriteRealProject));
    var third = log.Append("reader", "op-3", "query.levels", RealPath, true, PermissionCodes.ReadAllowed);
    Check(second.Seq == 2 && second.PrevHash == first.Hash && second.Decision == "deny" && second.Utc == "2026-11-02T10:00:01.0000000Z", "second entry chains to the first, records deny and the next clock tick");
    Check(third.Seq == 3 && third.PrevHash == second.Hash && log.Head == new AuditAnchor(3, third.Hash), "head anchor follows the last entry");
    var ok = AuditLogFormat.Verify(logPath);
    Check(ok.Ok && ok.Code == AuditCodes.Ok && ok.Entries.Count == 3 && ok.Head == log.Head && ok.Entries[1] == second, "an untouched log verifies with all entries", ok.Code + " " + ok.Detail);
    Check(AuditLogFormat.Verify(logPath, new AuditAnchor(2, second.Hash)).Ok && AuditLogFormat.Verify(logPath, log.Head).Ok, "an anchor at or before the head verifies");
    var lines = File.ReadAllText(logPath).Split('\n');
    Check(lines.Length == 4 && lines[3].Length == 0 && !File.ReadAllText(logPath).Contains('\r', StringComparison.Ordinal), "three LF-terminated lines, no CR on any platform");

    // Determinism: same clock and inputs give byte-identical files.
    var twinPath = Path.Combine(work, "b", "audit.jsonl");
    var twin = AuditLog.Open(twinPath, Clock());
    twin.Append("copier", "op-1", "wall.create", FixturePath, true, PermissionCodes.WriteCopiesAllowed);
    twin.Append("reader", "op-2", "wall.create", RealPath, false, PermissionCodes.RealProjectNotEnabled);
    twin.Append("reader", "op-3", "query.levels", RealPath, true, PermissionCodes.ReadAllowed);
    Check(File.ReadAllBytes(twinPath).SequenceEqual(File.ReadAllBytes(logPath)), "same clock and inputs produce a byte-identical log");

    // Known answer for escaping: & + " and a non-ASCII character.
    var escapedPath = Path.Combine(work, "escape", "audit.jsonl");
    var escapedLog = AuditLog.Open(escapedPath, Clock());
    var oddDocument = "/srv/Café & \"Co\"+1/T.rvt";
    var escaped = escapedLog.Append("copier", "op-1", "wall.create", oddDocument, true, PermissionCodes.WriteCopiesAllowed);
    var escapedBody = "{\"seq\":1,\"utc\":\"2026-11-02T10:00:00.0000000Z\",\"actor\":\"copier\",\"operation_id\":\"op-1\",\"command\":\"wall.create\",\"document\":\"/srv/Caf\\u00E9 \\u0026 \\u0022Co\\u0022\\u002B1/T.rvt\",\"decision\":\"allow\",\"reason\":\"write_copies_allowed\",\"prev_hash\":\"" + Genesis + "\"}";
    var escapedText = File.ReadAllText(escapedPath);
    Check(escapedText == escapedBody[..^1] + ",\"hash\":\"" + Sha(escapedBody) + "\"}\n" && escaped.Hash == Sha(escapedBody), "& + \" and non-ASCII are written as upper-case \\uXXXX escapes and hashed as written", escapedText);
    var escapedCheck = AuditLogFormat.Verify(escapedPath);
    Check(escapedCheck.Ok && escapedCheck.Entries[0].Document == oddDocument && escapedText.All(c => c < 128), "escaped entries round-trip and the file is pure ASCII");

    // Exclusive append handle and sequential writers.
    using (var holder = new FileStream(twinPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        Check(AuditCode(() => twin.Append("owner", "op-4", "floor.create", RealPath, true, PermissionCodes.RealProjectEnabled)) == AuditCodes.Locked, "append while another handle holds the file exclusively is audit_locked");
    }
    Check(File.ReadAllBytes(twinPath).SequenceEqual(File.ReadAllBytes(logPath)), "a locked append writes nothing");
    var twin4 = twin.Append("owner", "op-4", "floor.create", RealPath, true, PermissionCodes.RealProjectEnabled);
    Check(twin4.Seq == 4, "after the lock is released the same instance appends seq 4");
    var twinSecond = AuditLog.Open(twinPath, Clock());
    var twin5 = twinSecond.Append("owner", "op-5", "floor.create", RealPath, true, PermissionCodes.RealProjectEnabled);
    Check(AuditCode(() => twin.Append("owner", "op-6", "floor.create", RealPath, true, PermissionCodes.RealProjectEnabled)) == AuditCodes.ChangedExternally, "the first writer refuses after a second writer appended");
    var twinCheck = AuditLogFormat.Verify(twinPath);
    Check(twin5.Seq == 5 && twinCheck.Ok && twinCheck.Entries.Select(x => x.Seq).SequenceEqual(new long[] { 1, 2, 3, 4, 5 }), "two writers on one file produce one chain without a duplicate seq", twinCheck.Code);
    Check(twinCheck.VerifiedBytes == new FileInfo(twinPath).Length, "VerifiedBytes of a valid log is its length");

    // Reopen continues the chain; the earlier instance sees the external append and refuses.
    var reopened = AuditLog.Open(logPath, () => new DateTimeOffset(2026, 11, 2, 7, 0, 5, TimeSpan.FromHours(-3)), log.Head);
    Check(reopened.Head == log.Head, "reopening with the head anchor restores the head");
    var fourth = reopened.Append("owner", "op-4", "floor.create", RealPath, true, PermissionCodes.RealProjectEnabled);
    Check(fourth.Seq == 4 && fourth.PrevHash == third.Hash && fourth.Utc == "2026-11-02T10:00:05.0000000Z", "reopened log appends seq 4 chained to 3 and records the clock in UTC", fourth.Utc);
    var before = File.ReadAllBytes(logPath);
    Check(AuditCode(() => log.Append("owner", "op-5", "floor.create", RealPath, true, PermissionCodes.RealProjectEnabled)) == AuditCodes.ChangedExternally && File.ReadAllBytes(logPath).SequenceEqual(before), "a stale instance refuses to append after another writer and writes nothing");
    Check(AuditCode(() => reopened.Append("ow\nner", "op-5", "floor.create", RealPath, true, PermissionCodes.RealProjectEnabled)) == AuditCodes.InputInvalid, "a control character in the actor is input_invalid");
    Check(AuditCode(() => reopened.Append("owner", "", "floor.create", RealPath, true, PermissionCodes.RealProjectEnabled)) == AuditCodes.InputInvalid, "an empty operation id is input_invalid");
    Check(AuditCode(() => reopened.Append("owner", "op-5", "floor.create", "a" + (char)0xD800, true, PermissionCodes.RealProjectEnabled)) == AuditCodes.InputInvalid, "a lone surrogate in the document is input_invalid");
    Check(AuditCode(() => reopened.Append("owner", "op-5", "floor.create", RealPath, true, "")) == AuditCodes.InputInvalid, "an empty reason is input_invalid");
    Check(File.ReadAllBytes(logPath).SequenceEqual(before) && AuditLogFormat.Verify(logPath).Ok, "refused appends leave the log byte-identical and valid");
    Check(AuditCode(() => AuditLog.Open("relative.jsonl", Clock())) == AuditCodes.InputInvalid, "a relative log path is input_invalid");

    // ---- Tamper detection ----
    var good = File.ReadAllText(logPath);
    var goodLines = good.Split('\n')[..^1];
    var tamperIndex = 0;
    AuditVerification VerifyText(string text, AuditAnchor? anchor = null)
    {
        var path = Path.Combine(work, "tamper-" + (tamperIndex++) + ".jsonl");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
        return AuditLogFormat.Verify(path, anchor);
    }
    string Join(IEnumerable<string> parts) => string.Concat(parts.Select(p => p + "\n"));
    void Detects(string name, string text, string code, long? line, AuditAnchor? anchor = null)
    {
        var v = VerifyText(text, anchor);
        Check(!v.Ok && v.Code == code && v.Line == line, name, v.Code + " at " + v.Line + " (" + v.Detail + ")");
    }
    var edited = (string[])goodLines.Clone();
    edited[1] = edited[1].Replace("\"actor\":\"reader\"", "\"actor\":\"owner\"", StringComparison.Ordinal);
    Detects("an edited field is hash_mismatch at that entry", Join(edited), AuditCodes.HashMismatch, 2);
    var flipped = (string[])goodLines.Clone();
    flipped[1] = flipped[1].Replace("\"decision\":\"deny\"", "\"decision\":\"allow\"", StringComparison.Ordinal);
    Detects("a flipped decision is hash_mismatch", Join(flipped), AuditCodes.HashMismatch, 2);
    var rehashed = (string[])goodLines.Clone();
    var forgedEntry = second with { Actor = "owner" };
    rehashed[1] = AuditLogFormat.Line(forgedEntry with { Hash = AuditLogFormat.ComputeHash(forgedEntry) });
    Detects("an edited entry with a recomputed hash breaks the chain at the next entry", Join(rehashed), AuditCodes.ChainBroken, 3);
    var lastForged = fourth with { Reason = "read_allowed" };
    var lastRehashed = (string[])goodLines.Clone();
    lastRehashed[3] = AuditLogFormat.Line(lastForged with { Hash = AuditLogFormat.ComputeHash(lastForged) });
    Check(VerifyText(Join(lastRehashed)).Ok, "limit: a rehashed last entry is undetectable without an anchor");
    Detects("a rehashed last entry is anchor_mismatch against the kept head", Join(lastRehashed), AuditCodes.AnchorMismatch, 4, new AuditAnchor(4, fourth.Hash));
    Detects("a removed middle entry is sequence_broken", Join(goodLines.Where((_, i) => i != 1)), AuditCodes.SequenceBroken, 2);
    Detects("a removed first entry is sequence_broken", Join(goodLines.Skip(1)), AuditCodes.SequenceBroken, 1);
    Detects("reordered entries are sequence_broken", Join(new[] { goodLines[0], goodLines[2], goodLines[1], goodLines[3] }), AuditCodes.SequenceBroken, 2);
    Detects("a duplicated entry is sequence_broken", Join(goodLines.Append(goodLines[3])), AuditCodes.SequenceBroken, 5);
    var skipped = third with { Seq = 2, PrevHash = first.Hash };
    var renumbered = new[] { goodLines[0], AuditLogFormat.Line(skipped with { Hash = AuditLogFormat.ComputeHash(skipped) }) };
    Check(VerifyText(Join(renumbered)).Ok, "limit: a fully rewritten tail is self-consistent without an anchor");
    Detects("a rewritten shorter tail is truncated against the kept head", Join(renumbered), AuditCodes.Truncated, null, new AuditAnchor(4, fourth.Hash));
    Detects("a rewritten tail of equal length is anchor_mismatch", Join(renumbered), AuditCodes.AnchorMismatch, 2, new AuditAnchor(2, second.Hash));
    var badPrev = (string[])goodLines.Clone();
    badPrev[0] = badPrev[0].Replace("\"prev_hash\":\"0000", "\"prev_hash\":\"1111", StringComparison.Ordinal);
    Detects("a first entry not chained to genesis is chain_broken", Join(badPrev), AuditCodes.ChainBroken, 1);
    Check(VerifyText(Join(goodLines.Take(3))).Ok, "limit: whole trailing entries removed are invisible without an anchor");
    Detects("whole trailing entries removed are truncated against the kept head", Join(goodLines.Take(3)), AuditCodes.Truncated, null, new AuditAnchor(4, fourth.Hash));
    Detects("an emptied log is truncated against the kept head", "", AuditCodes.Truncated, null, new AuditAnchor(4, fourth.Hash));
    Detects("a torn final line is torn_tail", good[..^10], AuditCodes.TornTail, 4);
    Detects("a final line missing only its LF is torn_tail", good[..^1], AuditCodes.TornTail, 4);
    var tornWithCompletePrefix = VerifyText(good[..^10]);
    Check(tornWithCompletePrefix.Entries.Count == 3 && tornWithCompletePrefix.Head == new AuditAnchor(3, third.Hash), "torn_tail still reports the complete entries before it");
    Detects("a CRLF line ending is entry_not_canonical", good.Replace("\n", "\r\n", StringComparison.Ordinal), AuditCodes.EntryNotCanonical, 1);
    Detects("extra whitespace is entry_not_canonical", Join(goodLines.Select((l, i) => i == 2 ? "{ " + l[1..] : l)), AuditCodes.EntryNotCanonical, 3);
    Detects("an escaped-differently string is entry_not_canonical", Join(goodLines.Select((l, i) => i == 0 ? l.Replace("\"actor\":\"copier\"", "\"actor\":\"\\u0063opier\"", StringComparison.Ordinal) : l)), AuditCodes.EntryNotCanonical, 1);
    Detects("a garbage line is entry_malformed", good + "garbage\n", AuditCodes.EntryMalformed, 5);
    Detects("a blank line is entry_malformed", Join(goodLines.Take(2).Append("").Concat(goodLines.Skip(2))), AuditCodes.EntryMalformed, 3);
    var extraMember = (string[])goodLines.Clone();
    extraMember[0] = extraMember[0][..^1] + ",\"note\":\"x\"}";
    Detects("an extra member is entry_malformed", Join(extraMember), AuditCodes.EntryMalformed, 1);
    var missingMember = (string[])goodLines.Clone();
    missingMember[0] = missingMember[0].Replace("\"reason\":\"write_copies_allowed\",", "", StringComparison.Ordinal);
    Detects("a missing member is entry_malformed", Join(missingMember), AuditCodes.EntryMalformed, 1);
    var wrongType = (string[])goodLines.Clone();
    wrongType[0] = wrongType[0].Replace("\"seq\":1,", "\"seq\":\"1\",", StringComparison.Ordinal);
    Detects("a string seq is entry_malformed", Join(wrongType), AuditCodes.EntryMalformed, 1);
    var badDecision = (string[])goodLines.Clone();
    badDecision[0] = badDecision[0].Replace("\"decision\":\"allow\"", "\"decision\":\"maybe\"", StringComparison.Ordinal);
    Detects("an unknown decision value is entry_malformed", Join(badDecision), AuditCodes.EntryMalformed, 1);
    var badUtc = (string[])goodLines.Clone();
    badUtc[0] = badUtc[0].Replace("2026-11-02T10:00:00.0000000Z", "2026-11-02 10:00", StringComparison.Ordinal);
    Detects("a malformed utc is entry_malformed", Join(badUtc), AuditCodes.EntryMalformed, 1);
    var upperHash = (string[])goodLines.Clone();
    upperHash[0] = upperHash[0].Replace(first.Hash, first.Hash.ToUpperInvariant(), StringComparison.Ordinal);
    Detects("an upper-case hash is entry_malformed", Join(upperHash), AuditCodes.EntryMalformed, 1);
    var invalidUtf8Path = Path.Combine(work, "invalid-utf8.jsonl");
    File.WriteAllBytes(invalidUtf8Path, Encoding.UTF8.GetBytes(good).Concat(new byte[] { 0xFF, 0xFE, 0x0A }).ToArray());
    var invalidUtf8 = AuditLogFormat.Verify(invalidUtf8Path);
    Check(!invalidUtf8.Ok && invalidUtf8.Code == AuditCodes.EntryMalformed && invalidUtf8.Line == 5, "invalid UTF-8 is entry_malformed", invalidUtf8.Code + " at " + invalidUtf8.Line);
    var bomPath = Path.Combine(work, "bom.jsonl");
    File.WriteAllBytes(bomPath, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(good)).ToArray());
    var bom = AuditLogFormat.Verify(bomPath);
    Check(!bom.Ok && bom.Line == 1 && (bom.Code == AuditCodes.EntryMalformed || bom.Code == AuditCodes.EntryNotCanonical), "a byte order mark is refused at line 1", bom.Code + " at " + bom.Line);
    Detects("an anchor with an invalid hash is anchor_mismatch", good, AuditCodes.AnchorMismatch, null, new AuditAnchor(0, "nothex"));
    Check(VerifyText("").Ok && VerifyText("").Head == new AuditAnchor(0, Genesis) && VerifyText("", new AuditAnchor(0, Genesis)).Ok, "an empty log verifies at the genesis anchor");
    var missing = AuditLogFormat.Verify(Path.Combine(work, "nope.jsonl"));
    Check(!missing.Ok && missing.Code == AuditCodes.FileMissing, "verifying a missing file is audit_file_missing", missing.Code);

    // ---- Open refuses damaged logs and never repairs them ----
    var tornPath = Path.Combine(work, "torn.jsonl");
    var tornBytes = Encoding.UTF8.GetBytes(good[..^10]);
    File.WriteAllBytes(tornPath, tornBytes);
    Check(AuditCode(() => AuditLog.Open(tornPath, Clock())) == AuditCodes.TornTail && File.ReadAllBytes(tornPath).SequenceEqual(tornBytes), "Open refuses a torn tail and leaves the bytes untouched");
    var editedPath = Path.Combine(work, "edited.jsonl");
    File.WriteAllText(editedPath, Join(edited));
    Check(AuditCode(() => AuditLog.Open(editedPath, Clock())) == AuditCodes.HashMismatch, "Open refuses an edited log");
    var shortPath = Path.Combine(work, "short.jsonl");
    File.WriteAllText(shortPath, Join(goodLines.Take(3)));
    Check(AuditCode(() => AuditLog.Open(shortPath, Clock(), new AuditAnchor(4, fourth.Hash))) == AuditCodes.Truncated, "Open refuses a log shorter than the kept head");
    var absentPath = Path.Combine(work, "absent", "audit.jsonl");
    Check(AuditCode(() => AuditLog.Open(absentPath, Clock(), new AuditAnchor(4, fourth.Hash))) == AuditCodes.Truncated && !File.Exists(absentPath), "Open of a missing log with a non-genesis anchor is truncated and creates nothing");
    var thrown = Assert(() => AuditLog.Open(tornPath, Clock()));
    Check(thrown is { Code: AuditCodes.TornTail, Line: 4 } && thrown.Message.Contains(AuditCodes.TornTail, StringComparison.Ordinal), "the refusal carries the code and line");
    var tornCheck = AuditLogFormat.Verify(tornPath);
    Check(tornCheck.VerifiedBytes == Encoding.UTF8.GetByteCount(Join(goodLines.Take(3))), "VerifiedBytes of a torn log covers only its complete entries");

    // Open continues from the bytes it verified, not from a later look at the file.
    var racePath = Path.Combine(work, "race.jsonl");
    File.WriteAllText(racePath, Join(goodLines.Take(2)));
    var raced = AuditLog.Open(racePath, Clock(), null, () => File.AppendAllText(racePath, goodLines[2] + "\n"));
    Check(raced.Head == new AuditAnchor(2, second.Hash), "Open's head is the verified head");
    Check(AuditCode(() => raced.Append("owner", "op-9", "floor.create", RealPath, true, PermissionCodes.RealProjectEnabled)) == AuditCodes.ChangedExternally && AuditLogFormat.Verify(racePath).Entries.Count == 3, "a file that grew between verify and open is refused on the first append");

    // ---- Audited permission gate ----
    var gatePath = Path.Combine(work, "gate", "audit.jsonl");
    var gateLog = AuditLog.Open(gatePath, Clock());
    var actor = "copier";
    var gate = new PermissionAuditGate(enabled, gateLog, () => actor);
    var g1 = gate.Authorize(copyWrite);
    var g2 = gate.Authorize(Envelope("wall.create", RealPath, "op-real"));
    actor = "owner";
    var g3 = gate.Authorize(Envelope("wall.create", RealPath, "op-real"));
    actor = "bad\nactor";
    var g4 = gate.Authorize(query);
    Check(g1.Allowed && !g2.Allowed && g2.Reason == PermissionCodes.LevelInsufficient && g3.Allowed && !g4.Allowed && g4.Reason == PermissionCodes.ActorInvalid, "the gate returns the policy decisions", string.Join(", ", new[] { g1, g2, g3, g4 }.Select(Describe)));
    var gateCheck = AuditLogFormat.Verify(gatePath);
    var entries = gateCheck.Entries;
    Check(gateCheck.Ok && entries.Count == 4, "every authorization, allowed or denied, is audited", gateCheck.Code + " " + entries.Count);
    Check(entries.Count == 4 && entries[0] is { Actor: "copier", OperationId: "op-1", Command: "wall.create", Decision: "allow", Reason: PermissionCodes.WriteCopiesAllowed } && entries[0].Document == FixturePath, "allowed entry carries actor, operation id, command, document and reason");
    Check(entries.Count == 4 && entries[1] is { Actor: "copier", OperationId: "op-real", Decision: "deny", Reason: PermissionCodes.LevelInsufficient } && entries[2] is { Actor: "owner", Decision: "allow", Reason: PermissionCodes.RealProjectEnabled }, "denied and real-project entries record the injected actor");
    Check(entries.Count == 4 && entries[3] is { Decision: "deny", Reason: PermissionCodes.ActorInvalid } && entries[3].Actor == PermissionAuditGate.InvalidActor, "an invalid actor is recorded under a fixed placeholder", entries.Count == 4 ? entries[3].Actor : "");
    var gateBefore = File.ReadAllBytes(gatePath);
    actor = "copier";
    Check(enabled.Evaluate("copier", copyWrite).Allowed, "precondition: the policy allows copier on the test copy");
    File.AppendAllText(gatePath, "x");
    PermissionDecision? withheld = null;
    Check(AuditCode(() => withheld = gate.Authorize(copyWrite)) == AuditCodes.ChangedExternally && withheld is null, "an allowed decision is withheld when the audit cannot be appended");
    File.WriteAllBytes(gatePath, gateBefore);
    // ---- P10-09: audit log verification (typed tamper codes) and deterministic decision summary ----
    {
        var verifyDir = Path.Combine(work, "verify");
        Directory.CreateDirectory(verifyDir);
        var times = new Queue<DateTimeOffset>(new[]
        {
            new DateTimeOffset(2026, 11, 3, 1, 0, 0, TimeSpan.FromHours(3)), // 2026-11-02T22:00Z: UTC day, not local
            new DateTimeOffset(2026, 11, 2, 23, 59, 59, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 3, 20, 0, 0, TimeSpan.FromHours(-5)), // 2026-11-04T01:00Z
        });
        var verifyPath = Path.Combine(verifyDir, "audit.jsonl");
        var vlog = AuditLog.Open(verifyPath, () => times.Dequeue());
        vlog.Append("copier", "op-1", "wall.create", FixturePath, true, PermissionCodes.WriteCopiesAllowed);
        vlog.Append("reader", "op-2", "query.levels", RealPath, true, PermissionCodes.ReadAllowed);
        vlog.Append("copier", "op-3", "wall.create", RealPath, false, PermissionCodes.LevelInsufficient);
        vlog.Append("owner", "op-4", "wall.create", RealPath, true, PermissionCodes.RealProjectEnabled);
        vlog.Append("Copier", "op-5", "query.levels", RealPath, false, PermissionCodes.ActorUnknown);
        var head = vlog.Head;
        var clean = File.ReadAllBytes(verifyPath);
        var vLines = Encoding.UTF8.GetString(clean).Split('\n')[..5];
        byte[] JoinLines(IEnumerable<string> parts) => Encoding.UTF8.GetBytes(string.Concat(parts.Select(part => part + "\n")));
        var known = new[] { "copier", "Copier", "owner", "reader" };
        string Findings(AuditVerifyReport report) => string.Join(";", report.Findings.Select(f => f.Code + "@" + (f.Line?.ToString() ?? "-")));

        const string ExpectedSummary =
            "{\"entries\":5,\"allow\":3,\"deny\":2," +
            "\"by_actor\":[{\"actor\":\"Copier\",\"entries\":1,\"allow\":0,\"deny\":1},{\"actor\":\"copier\",\"entries\":2,\"allow\":1,\"deny\":1},{\"actor\":\"owner\",\"entries\":1,\"allow\":1,\"deny\":0},{\"actor\":\"reader\",\"entries\":1,\"allow\":1,\"deny\":0}]," +
            "\"by_command\":[{\"command\":\"query.levels\",\"entries\":2,\"allow\":1,\"deny\":1},{\"command\":\"wall.create\",\"entries\":3,\"allow\":2,\"deny\":1}]," +
            "\"by_day\":[{\"day\":\"2026-11-02\",\"entries\":2,\"allow\":2,\"deny\":0},{\"day\":\"2026-11-03\",\"entries\":2,\"allow\":1,\"deny\":1},{\"day\":\"2026-11-04\",\"entries\":1,\"allow\":0,\"deny\":1}]," +
            "\"by_actor_command_day\":[{\"actor\":\"Copier\",\"command\":\"query.levels\",\"day\":\"2026-11-04\",\"entries\":1,\"allow\":0,\"deny\":1},{\"actor\":\"copier\",\"command\":\"wall.create\",\"day\":\"2026-11-02\",\"entries\":1,\"allow\":1,\"deny\":0},{\"actor\":\"copier\",\"command\":\"wall.create\",\"day\":\"2026-11-03\",\"entries\":1,\"allow\":0,\"deny\":1},{\"actor\":\"owner\",\"command\":\"wall.create\",\"day\":\"2026-11-03\",\"entries\":1,\"allow\":1,\"deny\":0},{\"actor\":\"reader\",\"command\":\"query.levels\",\"day\":\"2026-11-02\",\"entries\":1,\"allow\":1,\"deny\":0}]}";

        // Clean log with a known actor set: ok, and the summary groups by actor, command and UTC day.
        var okReport = AuditVerifier.Verify(verifyPath, new AuditVerifyOptions(known, head));
        Check(okReport.Ok && okReport.Code == AuditVerifyCodes.Ok && okReport.ChainCode == AuditCodes.Ok && okReport.Lines == 5 && okReport.Findings.Count == 0 && okReport.VerifiedEntries.Count == 5, "verify: a clean log with known actors and its head anchor is ok", okReport.Code + " " + Findings(okReport));
        var summary = AuditSummary.ToJson(okReport.VerifiedEntries);
        Check(summary == ExpectedSummary, "summary: decisions per actor, command, UTC day and actor/command/day as ordinal-sorted JSON", summary);
        Check(okReport.ToJson() == "{\"ok\":true,\"code\":\"ok\",\"chain_code\":\"ok\",\"lines\":5,\"findings\":[],\"summary\":" + ExpectedSummary + "}", "verify: report JSON is the fixed shape with the summary", okReport.ToJson());
        var shuffled = okReport.VerifiedEntries.Reverse().ToArray();
        Check(AuditSummary.ToJson(shuffled) == ExpectedSummary, "summary: independent of entry order");
        var savedCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            Check(AuditSummary.ToJson(okReport.VerifiedEntries) == ExpectedSummary && AuditVerifier.VerifyBytes(clean, new AuditVerifyOptions(known, head)).ToJson() == okReport.ToJson(), "summary and report are culture invariant (ar-SA)");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = savedCulture; }
        Check(AuditSummary.ToJson(Array.Empty<AuditEntry>()) == "{\"entries\":0,\"allow\":0,\"deny\":0,\"by_actor\":[],\"by_command\":[],\"by_day\":[],\"by_actor_command_day\":[]}", "summary: empty log");
        Check(AuditVerifier.VerifyBytes(ReadOnlySpan<byte>.Empty).Ok, "verify: an empty log is ok");
        Check(File.ReadAllBytes(verifyPath).SequenceEqual(clean) && AuditLogFormat.Verify(verifyPath, head).Ok, "verify: the verifier never changes the P10-03 file and the format still verifies");

        // Unknown actor: an actor outside the supplied known set, at its line; no set means no actor check.
        var unknown = AuditVerifier.VerifyBytes(clean, new AuditVerifyOptions(new[] { "copier", "owner", "reader" }, head));
        Check(!unknown.Ok && unknown.Code == AuditVerifyCodes.ActorUnknown && Findings(unknown) == "audit_actor_unknown@5" && unknown.ChainCode == AuditCodes.Ok, "verify: unknown actor detected with its line", Findings(unknown));
        Check(unknown.Findings.Count == 1 && unknown.Findings[0].Detail == "actor Copier is not in the known actor set", "verify: unknown actor detail names the actor", unknown.Findings.Count == 1 ? unknown.Findings[0].Detail : "");
        Check(unknown.ToJson().StartsWith("{\"ok\":false,\"code\":\"audit_actor_unknown\",\"chain_code\":\"ok\",\"lines\":5,\"findings\":[{\"code\":\"audit_actor_unknown\",\"line\":5,\"detail\":\"actor Copier is not in the known actor set\"}],\"summary\":{\"entries\":5,", StringComparison.Ordinal), "verify: unknown actor report JSON", unknown.ToJson());
        Check(AuditVerifier.VerifyBytes(clean).Ok, "verify: without a known actor set no actor is unknown");
        Check(AuditVerifier.VerifyBytes(clean, new AuditVerifyOptions(Array.Empty<string>())) is { Ok: false } none && none.Findings.Count == 5 && none.Findings.All(f => f.Code == AuditVerifyCodes.ActorUnknown), "verify: an empty known actor set makes every actor unknown");

        // Truncation: whole trailing entries need the head anchor; a torn final line is detected without it.
        var dropped = JoinLines(vLines[..4]);
        var truncated = AuditVerifier.VerifyBytes(dropped, new AuditVerifyOptions(known, head));
        Check(truncated.Code == AuditVerifyCodes.Truncated && Findings(truncated) == "audit_truncated@-", "verify: removed trailing entry detected against the head anchor", Findings(truncated));
        Check(AuditVerifier.VerifyBytes(dropped, new AuditVerifyOptions(known)).Ok, "verify: documented limit - removed trailing entries are undetectable without an anchor");
        var torn = clean[..(clean.Length - 20)];
        var tornReport = AuditVerifier.VerifyBytes(torn, new AuditVerifyOptions(known));
        Check(tornReport.Code == AuditVerifyCodes.Truncated && Findings(tornReport) == "audit_truncated@5" && tornReport.ChainCode == AuditCodes.TornTail, "verify: a torn final line is truncation at its line", Findings(tornReport));
        Check(AuditVerifier.VerifyBytes(clean, new AuditVerifyOptions(known, new AuditAnchor(7, head.Hash))).Code == AuditVerifyCodes.Truncated, "verify: an anchor beyond the log is truncation");

        // Reorder: swapped entries are reported as reordered at each misplaced line, not as edits.
        var swapped = AuditVerifier.VerifyBytes(JoinLines(new[] { vLines[0], vLines[2], vLines[1], vLines[3], vLines[4] }), new AuditVerifyOptions(known, head));
        Check(swapped.Code == AuditVerifyCodes.Reordered && Findings(swapped) == "audit_reordered@2;audit_reordered@3" && swapped.ChainCode == AuditCodes.SequenceBroken, "verify: swapped entries detected as reordered", Findings(swapped));
        Check(swapped.VerifiedEntries.Count == 1 && swapped.ToJson().Contains("\"summary\":{\"entries\":1,", StringComparison.Ordinal), "verify: the summary covers only the chain-verified prefix", swapped.ToJson());
        var rotated = AuditVerifier.VerifyBytes(JoinLines(new[] { vLines[4], vLines[0], vLines[1], vLines[2], vLines[3] }));
        Check(rotated.Code == AuditVerifyCodes.Reordered && rotated.Findings.Count == 5 && rotated.Findings.All(f => f.Code == AuditVerifyCodes.Reordered), "verify: a rotated log is reordered at every line", Findings(rotated));

        // Edited entry: a changed field without a recomputed hash, or a non-canonical rewrite, is edited at its line.
        var editedLines = (string[])vLines.Clone();
        editedLines[1] = editedLines[1].Replace("\"actor\":\"reader\"", "\"actor\":\"readex\"", StringComparison.Ordinal);
        var editedReport = AuditVerifier.VerifyBytes(JoinLines(editedLines), new AuditVerifyOptions(known.Append("readex").ToArray(), head));
        Check(editedReport.Code == AuditVerifyCodes.EntryEdited && Findings(editedReport) == "audit_entry_edited@2" && editedReport.ChainCode == AuditCodes.HashMismatch, "verify: an edited field is detected at its line", Findings(editedReport));
        var decisionLines = (string[])vLines.Clone();
        decisionLines[2] = decisionLines[2].Replace("\"decision\":\"deny\"", "\"decision\":\"allow\"", StringComparison.Ordinal);
        Check(Findings(AuditVerifier.VerifyBytes(JoinLines(decisionLines), new AuditVerifyOptions(known, head))) == "audit_entry_edited@3", "verify: a flipped decision is an edited entry");
        var spacedLines = (string[])vLines.Clone();
        spacedLines[3] = spacedLines[3].Replace("\"seq\":4,", "\"seq\": 4,", StringComparison.Ordinal);
                var entries5 = okReport.VerifiedEntries;
        var rehashedEntry = entries5[1] with { Actor = "owner", Hash = "" };
        rehashedEntry = rehashedEntry with { Hash = AuditLogFormat.ComputeHash(rehashedEntry) };
        var rehashedLines = (string[])vLines.Clone();
        rehashedLines[1] = AuditLogFormat.Line(rehashedEntry);
        var rehashedReport = AuditVerifier.VerifyBytes(JoinLines(rehashedLines), new AuditVerifyOptions(known, head));
        Check(rehashedReport.Code == AuditVerifyCodes.ChainBroken && Findings(rehashedReport) == "audit_chain_broken@3" && rehashedReport.ChainCode == AuditCodes.ChainBroken, "verify: an edit with a recomputed hash breaks the next link", Findings(rehashedReport));
        // Whole-file rewrite with every hash recomputed: only the anchor kept elsewhere detects it.
        var rewritten = new List<string>();
        var previousHash = Genesis;
        foreach (var entry in entries5)
        {
            var copy = entry with { Actor = entry.Seq == 2 ? "owner" : entry.Actor, PrevHash = previousHash, Hash = "" };
            copy = copy with { Hash = AuditLogFormat.ComputeHash(copy) };
            rewritten.Add(AuditLogFormat.Line(copy));
            previousHash = copy.Hash;
        }
        var forgedReport = AuditVerifier.VerifyBytes(JoinLines(rewritten), new AuditVerifyOptions(known, head));
        Check(forgedReport.Code == AuditVerifyCodes.AnchorMismatch && Findings(forgedReport) == "audit_anchor_mismatch@5", "verify: a fully rehashed rewrite is an anchor mismatch", Findings(forgedReport));
        Check(AuditVerifier.VerifyBytes(JoinLines(rewritten), new AuditVerifyOptions(known)).Ok, "verify: documented limit - a fully rehashed rewrite is undetectable without an anchor");

        // Removed middle entry, duplicated entry, malformed line, missing file.
        var gap = AuditVerifier.VerifyBytes(JoinLines(new[] { vLines[0], vLines[1], vLines[3], vLines[4] }), new AuditVerifyOptions(known, head));
        Check(gap.Code == AuditVerifyCodes.EntriesMissing && Findings(gap) == "audit_entries_missing@3", "verify: a removed middle entry is missing entries at the gap", Findings(gap));
        var duplicate = AuditVerifier.VerifyBytes(JoinLines(new[] { vLines[0], vLines[1], vLines[1], vLines[2], vLines[3], vLines[4] }), new AuditVerifyOptions(known));
        Check(duplicate.Code == AuditVerifyCodes.SequenceBroken && Findings(duplicate) == "audit_sequence_broken@3", "verify: a duplicated entry is a broken sequence", Findings(duplicate));
        var garbled = AuditVerifier.VerifyBytes(JoinLines(new[] { vLines[0], "not json", vLines[2], vLines[3], vLines[4] }), new AuditVerifyOptions(known));
        Check(garbled.Code == AuditVerifyCodes.EntryMalformed && Findings(garbled) == "audit_entry_malformed@2", "verify: a malformed line is reported at its line", Findings(garbled));
        var multi = AuditVerifier.VerifyBytes(JoinLines(new[] { vLines[0], editedLines[1], vLines[2], vLines[3] }), new AuditVerifyOptions(new[] { "copier", "owner", "readex" }, head));
        Check(Findings(multi) == "audit_entry_edited@2;audit_truncated@-" && multi.Code == AuditVerifyCodes.EntryEdited, "verify: every finding is reported in line order, anchor findings last", Findings(multi));
        var missingReport = AuditVerifier.Verify(Path.Combine(verifyDir, "absent.jsonl"));
        Check(missingReport.Code == AuditVerifyCodes.FileMissing && Findings(missingReport) == "audit_file_missing@-", "verify: a missing file is typed", Findings(missingReport));
        Check(AuditVerifier.VerifyBytes(clean, new AuditVerifyOptions(known, new AuditAnchor(-1, head.Hash))).Code == AuditVerifyCodes.AnchorMismatch, "verify: an invalid anchor is an anchor mismatch");
        // Review fixes. 1: an anchor mismatch makes every entry untrusted, so the summary counts none of them.
        Check(forgedReport.VerifiedEntries.Count == 0 && forgedReport.ToJson().Contains("\"summary\":{\"entries\":0,", StringComparison.Ordinal), "verify: a forged log under an anchor mismatch contributes nothing to the summary", forgedReport.ToJson());
        // 2: a malformed last line under the head anchor is malformed, not also truncated.
        var badTail = AuditVerifier.VerifyBytes(JoinLines(new[] { vLines[0], vLines[1], vLines[2], vLines[3], "not json" }), new AuditVerifyOptions(known, head));
        Check(Findings(badTail) == "audit_entry_malformed@5" && badTail.Code == AuditVerifyCodes.EntryMalformed, "verify: a malformed last line with the head anchor is only malformed", Findings(badTail));
        var badUtf8 = JoinLines(vLines[..4]).Concat(new byte[] { 0xC3, 0x28, (byte)'\n' }).ToArray();
        Check(Findings(AuditVerifier.VerifyBytes(badUtf8, new AuditVerifyOptions(known, head))) == "audit_entry_malformed@5", "verify: an invalid UTF-8 last line with the head anchor is only malformed", Findings(AuditVerifier.VerifyBytes(badUtf8, new AuditVerifyOptions(known, head))));
        // 3: a reorder combined with a gap reports both, each at its own line.
        var mixed = AuditVerifier.VerifyBytes(JoinLines(new[] { vLines[0], vLines[2], vLines[4], vLines[3] }), new AuditVerifyOptions(known, head));
        Check(Findings(mixed) == "audit_entries_missing@2;audit_reordered@4" && mixed.Code == AuditVerifyCodes.EntriesMissing, "verify: seqs 1,3,5,4 are a gap at line 2 and a reorder at line 4", Findings(mixed) + " " + mixed.Code);
        // 4: reformatting that keeps the content hash is not canonical, not edited.
        var crlf = AuditVerifier.VerifyBytes(Encoding.UTF8.GetBytes(string.Concat(vLines.Select(crlfLine => crlfLine + "\r\n"))), new AuditVerifyOptions(known, head));
        Check(Findings(crlf) == "audit_entry_not_canonical@1;audit_entry_not_canonical@2;audit_entry_not_canonical@3;audit_entry_not_canonical@4;audit_entry_not_canonical@5" && crlf.Code == "audit_entry_not_canonical" && crlf.ChainCode == AuditCodes.EntryNotCanonical, "verify: a CRLF log is not canonical on every line and never edited", Findings(crlf));
        var bomBytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(clean).ToArray();
        Check(Findings(AuditVerifier.VerifyBytes(bomBytes, new AuditVerifyOptions(known, head))) == "audit_entry_not_canonical@1", "verify: a leading BOM is not canonical at line 1", Findings(AuditVerifier.VerifyBytes(bomBytes, new AuditVerifyOptions(known, head))));
        Check(Findings(AuditVerifier.VerifyBytes(JoinLines(spacedLines), new AuditVerifyOptions(known, head))) == "audit_entry_not_canonical@4", "verify: whitespace inside a line is not canonical, not edited");
        // Nits: Code is the most severe finding (documented order), not the first by line.
        var severity = AuditVerifier.VerifyBytes(JoinLines(editedLines), new AuditVerifyOptions(new[] { "Copier", "owner", "readex", "reader" }, head));
        Check(Findings(severity) == "audit_actor_unknown@1;audit_entry_edited@2;audit_actor_unknown@3" && severity.Code == AuditVerifyCodes.EntryEdited, "verify: report code is the most severe finding", Findings(severity) + " " + severity.Code);
        var badDecisionEntries = new[] { entries5[0] with { Decision = "maybe" } };
        string? summaryError = null;
        try { AuditSummary.ToJson(badDecisionEntries); } catch (ArgumentException error) { summaryError = error.GetType().Name; }
        Check(summaryError is not null, "summary: a decision other than allow or deny is rejected");
        var directoryReport = AuditVerifier.Verify(verifyDir);
        Check(directoryReport.Code == AuditVerifyCodes.FileUnreadable && Findings(directoryReport) == "audit_file_unreadable@-", "verify: a directory path is unreadable", Findings(directoryReport));
        var surrogateLines = (string[])vLines.Clone();
        surrogateLines[0] = surrogateLines[0].Replace("\"actor\":\"copier\"", "\"actor\":\"\\ud800\"", StringComparison.Ordinal);
        AuditVerifyReport? surrogate = null;
        string? surrogateError = null;
        try { surrogate = AuditVerifier.VerifyBytes(JoinLines(surrogateLines), new AuditVerifyOptions(known, head)); } catch (Exception error) { surrogateError = error.GetType().Name; }
        Check(surrogateError is null && surrogate is not null && Findings(surrogate) == "audit_entry_malformed@1", "verify: a lone escaped surrogate is malformed without an exception", surrogateError ?? (surrogate is null ? "" : Findings(surrogate)));
    }
}
catch (Exception error)
{
    Console.WriteLine("::error::audit crashed: " + error);
    failed++;
}
finally
{
    if (Directory.Exists(work)) Directory.Delete(work, true);
}
Console.WriteLine(JsonSerializer.Serialize(new { passed, failed, audit_contract = failed == 0 }));
Environment.ExitCode = failed == 0 && passed > 0 ? 0 : 1;

static AuditLogException? Assert(Action action)
{
    try { action(); return null; }
    catch (AuditLogException error) { return error; }
}
