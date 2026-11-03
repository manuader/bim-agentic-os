using Bim.Core;
using Bim.Revit;
using System.Text.Json;
using System.Text.Json.Nodes;
// P9-02 structure commands contract oracle: query.grids, query.structural_types, grid.create,
// structural_column.create, structural_beam.create, foundation.create_isolated and foundation.create_wall. Covers
// the schemas, the typed validators, the guarded client path (fake transport) and the Autodesk-free executor rules
// and readback checks in runtimes/revit-native/CommandsStructure.cs. No Revit is involved: a passing run is
// unit-tested evidence only; CommandsExecutorStructure.cs is compiled, never run.
static class CatalogueStructureChecks
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string WallUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0002a1c3";
    const string Level1Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000137";
    const string Level2Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000138";
    const string TypeUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000c01";
    const string OtherTypeUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000c02";
    const string ElementUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000d01";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";
    static readonly string[] StructureCommands = { "query.grids", "query.structural_types", "grid.create", "structural_column.create", "structural_beam.create", "foundation.create_isolated", "foundation.create_wall" };
    static readonly string[] Writers = { "grid.create", "structural_column.create", "structural_beam.create", "foundation.create_isolated", "foundation.create_wall" };

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static JsonObject Envelope(string command, JsonNode args, string path = FixturePath) => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-structure",
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
        ["command"] = command,
        ["args"] = args.DeepClone(),
    };
    static JsonArray Pair(double x, double y) => new(x, y);
    static JsonObject Line(double x0, double y0, double x1, double y1) => new() { ["start"] = Pair(x0, y0), ["end"] = Pair(x1, y1) };
    static JsonObject Grid() => new() { ["line"] = Line(0, 0, 0, 12000), ["name"] = "A" };
    static JsonObject Column() => new() { ["type"] = "W10x49", ["point"] = Pair(6000, 4000), ["base_level"] = "Level 1", ["top_level"] = "Level 2" };
    static JsonObject Beam() => new() { ["level"] = "Level 2", ["type"] = "W12x26", ["line"] = Line(0, 0, 6000, 0) };
    static JsonObject Isolated() => new() { ["level"] = "Level 1", ["type"] = "1800 x 1200 x 450mm", ["point"] = Pair(6000, 4000) };
    static JsonObject WallFooting() => new() { ["host_wall_uid"] = WallUid, ["type"] = "Bearing Footing - 900 x 300" };
    static JsonObject Sample(string command) => command switch
    {
        "query.grids" => new JsonObject(),
        "query.structural_types" => new JsonObject { ["category"] = "column" },
        "grid.create" => Grid(),
        "structural_column.create" => Column(),
        "structural_beam.create" => Beam(),
        "foundation.create_isolated" => Isolated(),
        "foundation.create_wall" => WallFooting(),
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };
    static PlanPoint P(double x, double y) => new(x, y);
    static bool Near(double a, double b, double tolerance = 1e-6) => Math.Abs(a - b) <= tolerance;

    static RevitCommandException? Refusal(string json)
    {
        try { RevitCommandProtocol.Parse(json); return null; }
        catch (RevitCommandException error) { return error; }
        catch (Exception error) { return new RevitCommandException("unexpected " + error.GetType().Name, null, error.Message); }
    }
    static void RejectsText(string name, string json, string code, string? field = null)
    {
        var error = Refusal(json);
        Check(error is not null && error.Code == code && (field is null || error.Field == field), name, error is null ? "accepted" : error.Code + " at " + error.Field + " (" + error.Detail + ")");
    }
    static void Rejects(string name, JsonNode envelope, string code, string? field = null) => RejectsText(name, envelope.ToJsonString(), code, field);
    static RevitCommandEnvelope? Accepts(string name, JsonNode envelope)
    {
        try { var parsed = RevitCommandProtocol.Parse(envelope.ToJsonString()); Check(true, name); return parsed; }
        catch (RevitCommandException error) { Check(false, name, error.Code + " at " + error.Field + " (" + error.Detail + ")"); return null; }
    }
    static RevitCommandException? Thrown(Action action)
    {
        try { action(); return null; }
        catch (RevitCommandException error) { return error; }
    }
    static JsonObject With(JsonObject args, string member, JsonNode? value) { if (value is null) args.Remove(member); else args[member] = value; return args; }

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        Catalogue();
        Queries();
        Grids();
        Columns();
        Beams();
        Foundations();
        ClientPath();
        ExecutorRules();
        Readbacks();
        ReadbackJson();
    }

    static void Catalogue()
    {
        var names = RevitCommandCatalogue.Structure.Select(spec => spec.Name).ToArray();
        Check(names.SequenceEqual(StructureCommands), "structure catalogue lists the two queries and five creators in order", string.Join(",", names));
        Check(RevitCommandCatalogue.All.SequenceEqual(RevitCommandCatalogue.V1.Concat(RevitCommandCatalogue.V2).Concat(RevitCommandCatalogue.Views).Concat(RevitCommandCatalogue.Mirror).Concat(RevitCommandCatalogue.Elements).Concat(RevitCommandCatalogue.Structure)),
            "All is v1, v2, views, snapshot, element queries, then the structure commands");
        Check(RevitCommandCatalogue.All.Select(spec => spec.Name).Distinct().Count() == RevitCommandCatalogue.All.Count && RevitCommandCatalogue.All.Select(spec => spec.ToolName).Distinct().Count() == RevitCommandCatalogue.All.Count, "command and tool names stay distinct with the structure commands");
        Check(RevitCommandCatalogue.Structure.All(spec => spec.ToolName == "revit_" + spec.Name.Replace('.', '_') && System.Text.RegularExpressions.Regex.IsMatch(spec.ToolName, @"\Arevit_[a-z0-9_]{1,58}\z")), "structure tool names are revit_<command with . replaced by _> within 64 characters");
        Check(RevitCommandCatalogue.Structure.Where(spec => spec.Writes).Select(spec => spec.Name).SequenceEqual(Writers), "the five structure creators write; the two queries are read-only");
        foreach (var name in names) Check(ReferenceEquals(RevitCommandCatalogue.Find(name), RevitCommandCatalogue.Structure.Single(spec => spec.Name == name)), "Find resolves structure command " + name);
        Check(RevitCommandCatalogue.Structure.All(spec => spec.Description.Length > 20 && spec.Description.EndsWith('.') && (spec.Writes ? spec.Description.Contains("enabled test copies", StringComparison.Ordinal) : spec.Description.Contains("Read-only", StringComparison.Ordinal))),
            "every structure command has a plain-English description; writes state the copies-only rule and queries say Read-only");
        foreach (var spec in RevitCommandCatalogue.Structure)
        {
            string? problem = null;
            try { using var schema = JsonDocument.Parse(spec.ArgsSchemaJson); RevitCommandSchema.CheckSupported(schema.RootElement); if (schema.RootElement.GetProperty("type").GetString() != "object") problem = "not an object schema"; }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { problem = error.Message; }
            Check(problem is null, spec.Name + " args schema is valid JSON using only enforced keywords", problem ?? "");
        }
        var copies = new[] { @"C:\Repo\.bim\native-runs" };
        foreach (var spec in RevitCommandCatalogue.Structure)
            Check(PermissionPolicy.RequiredLevel(spec.Name, FixturePath, copies) == (spec.Writes ? PermissionLevel.WriteCopies : PermissionLevel.Read)
                && PermissionPolicy.RequiredLevel(spec.Name, RealPath, copies) == (spec.Writes ? PermissionLevel.WriteRealProject : PermissionLevel.Read), "permission level of " + spec.Name + " follows its catalogue entry");
        Check(RevitStructureRules.Categories.SequenceEqual(new[] { "column", "framing", "foundation", "wall_foundation" }), "structural type categories are column, framing, foundation and wall_foundation");
    }

    static void Queries()
    {
        Check(Accepts("query.grids with {} accepted", Envelope("query.grids", new JsonObject()))?.Args is EmptyQueryArgs, "query.grids binds empty arguments");
        Rejects("query.grids with an argument", Envelope("query.grids", new JsonObject { ["x"] = 1 }), RevitCommandCodes.ArgsInvalid, "args.x");
        foreach (var category in RevitStructureRules.Categories)
            Check(Accepts("query.structural_types " + category + " accepted", Envelope("query.structural_types", new JsonObject { ["category"] = category }))?.Args is StructuralTypeQueryArgs { Category: { } bound } && bound == category, "query.structural_types binds category " + category);
        Rejects("query.structural_types without category", Envelope("query.structural_types", new JsonObject()), RevitCommandCodes.ArgsInvalid, "args.category");
        Rejects("query.structural_types unknown category", Envelope("query.structural_types", new JsonObject { ["category"] = "door" }), RevitCommandCodes.ArgsInvalid, "args.category");
    }

    static void Grids()
    {
        var grid = Accepts("grid accepted", Envelope("grid.create", Grid()));
        Check(grid?.Args is GridCreateArgs { Name: "A" } g && Near(g.Start.X.Millimetres, 0) && Near(g.End.Y.Millimetres, 12000), "grid binds the line in millimetres and the name");
        Check(Accepts("grid without name accepted", Envelope("grid.create", With(Grid(), "name", null)))?.Args is GridCreateArgs { Name: null }, "grid name is optional");
        Rejects("grid line of zero length", Envelope("grid.create", With(Grid(), "line", Line(5, 5, 5, 5))), RevitCommandCodes.CurveTooShort, "args.line");
        Rejects("grid line of 1 mm", Envelope("grid.create", With(Grid(), "line", Line(0, 0, 1, 0))), RevitCommandCodes.CurveTooShort, "args.line");
        Rejects("grid line longer than the Revit limit", Envelope("grid.create", With(Grid(), "line", Line(0, 0, 9_144_001, 0))), RevitCommandCodes.LengthInvalid, "args.line");
        Rejects("grid line coordinate beyond 1e8 mm", Envelope("grid.create", With(Grid(), "line", Line(0, -2e8, 0, 0))), RevitCommandCodes.LengthInvalid, "args.line.start[1]");
        Rejects("grid line without end", Envelope("grid.create", With(Grid(), "line", new JsonObject { ["start"] = Pair(0, 0) })), RevitCommandCodes.ArgsInvalid, "args.line.end");
        Rejects("grid without line", Envelope("grid.create", With(Grid(), "line", null)), RevitCommandCodes.ArgsInvalid, "args.line");
        Rejects("grid arc is not offered", Envelope("grid.create", With(Grid(), "line", new JsonObject { ["start"] = Pair(0, 0), ["end"] = Pair(1000, 0), ["through"] = Pair(500, 500) })), RevitCommandCodes.ArgsInvalid, "args.line.through");
        foreach (var forbidden in new[] { "A:B", "A{B", "A|B", "A;B", "A<B", "A?B", "A~B" })
            Rejects("grid name with a Revit-forbidden character " + forbidden[1], Envelope("grid.create", With(Grid(), "name", forbidden)), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("empty grid name", Envelope("grid.create", With(Grid(), "name", "")), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("grid name with leading or trailing space", Envelope("grid.create", With(Grid(), "name", " A ")), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("grid name with a control character", Envelope("grid.create", With(Grid(), "name", "A\u0001")), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("grid name over 256 characters", Envelope("grid.create", With(Grid(), "name", new string('g', 257))), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("grid with a level member", Envelope("grid.create", With(Grid(), "level", "Level 1")), RevitCommandCodes.ArgsInvalid, "args.level");
    }

    static void Columns()
    {
        var column = Accepts("column accepted", Envelope("structural_column.create", Column()));
        Check(column?.Args is StructuralColumnCreateArgs { Type: "W10x49", BaseLevel: "Level 1", TopLevel: "Level 2" } c && Near(c.Point.X.Millimetres, 6000) && Near(c.Point.Y.Millimetres, 4000) && c.BaseOffset.Millimetres == 0 && c.TopOffset.Millimetres == 0,
            "column binds type, plan point, base and top levels with zero default offsets");
        var offsets = Accepts("column with offsets accepted", Envelope("structural_column.create", With(With(Column(), "base_offset_mm", -300), "top_offset_mm", 150)));
        Check(offsets?.Args is StructuralColumnCreateArgs o && Near(o.BaseOffset.Millimetres, -300) && Near(o.TopOffset.Millimetres, 150), "column binds negative and positive offsets in millimetres");
        Check(Accepts("column on one level with offsets accepted (resolution checks height)", Envelope("structural_column.create", With(With(Column(), "top_level", Level1Uid), "base_level", Level2Uid))) is not null, "a column between two level UniqueIds binds");
        Rejects("column with the same base and top level", Envelope("structural_column.create", With(Column(), "top_level", "Level 1")), RevitCommandCodes.LevelsNotDistinct, "args.top_level");
        Rejects("column base offset beyond the Revit limit", Envelope("structural_column.create", With(Column(), "base_offset_mm", 9_144_001)), RevitCommandCodes.LengthInvalid, "args.base_offset_mm");
        Rejects("column top offset beyond the Revit limit", Envelope("structural_column.create", With(Column(), "top_offset_mm", -9_144_001)), RevitCommandCodes.LengthInvalid, "args.top_offset_mm");
        RejectsText("column offset overflowing to infinity", Envelope("structural_column.create", With(Column(), "top_offset_mm", 5)).ToJsonString().Replace("\"top_offset_mm\":5", "\"top_offset_mm\":1e400"), RevitCommandCodes.LengthInvalid, "args.top_offset_mm");
        Rejects("column point beyond 1e8 mm", Envelope("structural_column.create", With(Column(), "point", Pair(2e8, 0))), RevitCommandCodes.LengthInvalid, "args.point[0]");
        Rejects("column point with three coordinates", Envelope("structural_column.create", With(Column(), "point", new JsonArray(0, 0, 0))), RevitCommandCodes.ArgsInvalid, "args.point");
        Rejects("column without type", Envelope("structural_column.create", With(Column(), "type", null)), RevitCommandCodes.ArgsInvalid, "args.type");
        Rejects("column without top level", Envelope("structural_column.create", With(Column(), "top_level", null)), RevitCommandCodes.ArgsInvalid, "args.top_level");
        Rejects("column offset as text", Envelope("structural_column.create", With(Column(), "base_offset_mm", "0")), RevitCommandCodes.ArgsInvalid, "args.base_offset_mm");
        Rejects("slanted column is not offered", Envelope("structural_column.create", With(Column(), "top_point", Pair(0, 0))), RevitCommandCodes.ArgsInvalid, "args.top_point");
    }

    static void Beams()
    {
        var beam = Accepts("beam accepted", Envelope("structural_beam.create", Beam()));
        Check(beam?.Args is StructuralBeamCreateArgs { Level: "Level 2", Type: "W12x26" } b && Near(b.End.X.Millimetres, 6000) && Near(b.Start.Y.Millimetres, 0), "beam binds level, type and the line in millimetres");
        Rejects("beam line of zero length", Envelope("structural_beam.create", With(Beam(), "line", Line(10, 10, 10, 10))), RevitCommandCodes.CurveTooShort, "args.line");
        Rejects("beam line longer than the Revit limit", Envelope("structural_beam.create", With(Beam(), "line", Line(-5e6, 0, 5e6, 0))), RevitCommandCodes.LengthInvalid, "args.line");
        Rejects("beam line coordinate not a number", Envelope("structural_beam.create", With(Beam(), "line", new JsonObject { ["start"] = new JsonArray("0", 0), ["end"] = Pair(6000, 0) })), RevitCommandCodes.ArgsInvalid, "args.line.start[0]");
        Rejects("beam without level", Envelope("structural_beam.create", With(Beam(), "level", null)), RevitCommandCodes.ArgsInvalid, "args.level");
        Rejects("beam with an empty type", Envelope("structural_beam.create", With(Beam(), "type", "")), RevitCommandCodes.ArgsInvalid, "args.type");
        Rejects("curved beam is not offered", Envelope("structural_beam.create", With(Beam(), "curve", Line(0, 0, 1, 1))), RevitCommandCodes.ArgsInvalid, "args.curve");
    }

    static void Foundations()
    {
        var isolated = Accepts("isolated foundation accepted", Envelope("foundation.create_isolated", Isolated()));
        Check(isolated?.Args is IsolatedFoundationCreateArgs { Level: "Level 1", Type: "1800 x 1200 x 450mm" } f && Near(f.Point.X.Millimetres, 6000) && Near(f.Point.Y.Millimetres, 4000), "isolated foundation binds level, type and plan point");
        Rejects("isolated foundation without point", Envelope("foundation.create_isolated", With(Isolated(), "point", null)), RevitCommandCodes.ArgsInvalid, "args.point");
        Rejects("isolated foundation point beyond 1e8 mm", Envelope("foundation.create_isolated", With(Isolated(), "point", Pair(0, -1e9))), RevitCommandCodes.LengthInvalid, "args.point[1]");
        Rejects("isolated foundation with a host member", Envelope("foundation.create_isolated", With(Isolated(), "host_wall_uid", WallUid)), RevitCommandCodes.ArgsInvalid, "args.host_wall_uid");
        var wall = Accepts("wall foundation accepted", Envelope("foundation.create_wall", WallFooting()));
        Check(wall?.Args is WallFoundationCreateArgs { HostWallUid: WallUid, Type: "Bearing Footing - 900 x 300" }, "wall foundation binds host wall UniqueId and type");
        Rejects("wall foundation host that is not a UniqueId", Envelope("foundation.create_wall", With(WallFooting(), "host_wall_uid", "Wall 1")), RevitCommandCodes.ArgsInvalid, "args.host_wall_uid");
        Rejects("wall foundation without type", Envelope("foundation.create_wall", With(WallFooting(), "type", null)), RevitCommandCodes.ArgsInvalid, "args.type");
        Rejects("wall foundation with a point", Envelope("foundation.create_wall", With(WallFooting(), "point", Pair(0, 0))), RevitCommandCodes.ArgsInvalid, "args.point");
    }

    static void ClientPath()
    {
        var policy = new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" });
        var forwarded = new List<string>();
        string? Transport(string pipe, string line) { forwarded.Add(line); return Answer(line); }
        var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
        foreach (var command in Writers)
        {
            var args = Sample(command);
            var before = forwarded.Count;
            var ok = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args).ToJsonString(), policy, Transport, noLinks);
            Check(!ok.IsError && forwarded.Count == before + 1 && forwarded[^1] == RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(Envelope(command, args).ToJsonString())), command + " on an enabled copy is forwarded as the canonical wire line", ok.Line);
            var refused = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args, RealPath).ToJsonString(), policy, Transport, noLinks);
            Check(refused.IsError && !refused.Forwarded && JsonNode.Parse(refused.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentNotEnabled && forwarded.Count == before + 1, command + " on a real project is document_not_enabled and never forwarded", refused.Line);
            var linked = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args).ToJsonString(), policy, Transport, new RevitFileSystemProbe(path => path.EndsWith("run-1", StringComparison.Ordinal), _ => null));
            Check(linked.IsError && JsonNode.Parse(linked.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentLinkRefused && forwarded.Count == before + 1, command + " through a linked directory is document_link_refused");
        }
        foreach (var query in new[] { "query.grids", "query.structural_types" })
        {
            var read = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(query, Sample(query), RealPath).ToJsonString(), policy, Transport, noLinks);
            Check(!read.IsError && read.Forwarded, query + " is read-only and allowed on a real project", read.Line);
        }
        var invalid = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("structural_column.create", With(Column(), "top_level", "Level 1")).ToJsonString(), policy, Transport, noLinks);
        Check(invalid.IsError && !invalid.Forwarded && JsonNode.Parse(invalid.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.LevelsNotDistinct && JsonNode.Parse(invalid.Line)!["field"]!.GetValue<string>() == "args.top_level", "an invalid structure command returns the typed failure before the pipe", invalid.Line);
        var a = RevitCommandProtocol.Parse("""{"protocol_version":1,"operation_id":"op-b","document":{"path":"C:\\Repo\\.bim\\native-runs\\r\\a.rvt","unique_id":"60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b"},"command":"structural_beam.create","args":{"level":"Level 2","type":"W12x26","line":{"start":[0,0],"end":[6000,0]}}}""");
        var b = RevitCommandProtocol.Parse("""{"args":{"line":{"end":[6.0e3,-0],"start":[0e0,0]},"type":"W12x26","level":"Level 2"},"command":"structural_beam.create","document":{"unique_id":"60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b","path":"C:\\Repo\\.bim\\native-runs\\r\\a.rvt"},"operation_id":"op-b","protocol_version":1}""");
        Check(a.CanonicalJson == b.CanonicalJson && a.RequestSha256 == b.RequestSha256, "structure requests use the same canonical form and hash as v1", a.CanonicalJson + " vs " + b.CanonicalJson);
    }

    static string Answer(string wireLine)
    {
        using var wire = JsonDocument.Parse(wireLine);
        var commandText = wire.RootElement.GetProperty("Command").GetRawText();
        using var command = JsonDocument.Parse(commandText);
        var receipt = new RevitCommandReceipt(command.RootElement.GetProperty("operation_id").GetString()!, command.RootElement.GetProperty("command").GetString()!,
            command.RootElement.GetProperty("document").GetProperty("unique_id").GetString()!, CommandWire.Sha256(commandText), false);
        return RevitCommandResponse.Success(receipt, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null, Array.Empty<string>()).ToJson();
    }

    static void ExecutorRules()
    {
        Check(CommandRulesStructure.BuiltInCategory("column") == "OST_StructuralColumns" && CommandRulesStructure.BuiltInCategory("framing") == "OST_StructuralFraming" && CommandRulesStructure.BuiltInCategory("foundation") == "OST_StructuralFoundation" && CommandRulesStructure.BuiltInCategory("wall_foundation") is null,
            "structural categories map to their Revit BuiltInCategory; wall foundation types are not family symbols");
        Check(Thrown(() => CommandRulesStructure.BuiltInCategory("door")) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.category" }, "an unknown structural category is args_invalid");
        Check(CommandRulesStructure.ColumnStructuralType == "Column" && CommandRulesStructure.BeamStructuralType == "Beam" && CommandRulesStructure.FootingStructuralType == "Footing", "Revit StructuralType names are Column, Beam and Footing");

        Check(Thrown(() => CommandRulesStructure.CheckGridNameFree("A", new[] { "B", "a" })) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.name" }, "a grid name differing only in case from an existing grid is args_invalid (fail closed)");
        Check(Thrown(() => CommandRulesStructure.CheckGridNameFree("A", new[] { "B", "AA" })) is null && Thrown(() => CommandRulesStructure.CheckGridNameFree(null, new[] { "A" })) is null, "a free grid name and no grid name never collide");

        Check(Near(CommandRulesStructure.ColumnHeightMm(Level1Uid, 0, 0, Level2Uid, 4000, 0), 4000), "column height is top level plus top offset minus base level plus base offset");
        Check(Near(CommandRulesStructure.ColumnHeightMm(Level1Uid, 0, -300, Level2Uid, 4000, 150), 4450), "column height counts both offsets");
        Check(Thrown(() => CommandRulesStructure.ColumnHeightMm(Level1Uid, 0, 0, Level1Uid.ToUpperInvariant(), 0, 3000)) is { Code: RevitCommandCodes.LevelsNotDistinct, Field: "args.top_level" }, "a name and a UniqueId of one level are levels_not_distinct");
        Check(Thrown(() => CommandRulesStructure.ColumnHeightMm(Level2Uid, 4000, 0, Level1Uid, 0, 0)) is { Code: RevitCommandCodes.LevelsNotDistinct, Field: "args.top_level" }, "a top level below the base level is levels_not_distinct");
        Check(Thrown(() => CommandRulesStructure.ColumnHeightMm(Level1Uid, 0, 0, Level2Uid, 4000, -3999)) is { Code: RevitCommandCodes.LevelsNotDistinct, Field: "args.top_level" }, "a column top less than 1.2 mm above its base is levels_not_distinct");
        Check(Thrown(() => CommandRulesStructure.ColumnHeightMm(Level1Uid, 0, -5_000_000, Level2Uid, 4000, 5_000_000)) is { Code: RevitCommandCodes.LengthInvalid, Field: "args.top_offset_mm" }, "a column taller than the Revit limit is length_invalid");

        Check(Thrown(() => CommandRulesStructure.CheckWallFoundationHost("Basic", 0)) is null, "a basic wall without a foundation can host a wall foundation");
        Check(Thrown(() => CommandRulesStructure.CheckWallFoundationHost("Curtain", 0)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.host_wall_uid" }, "a curtain wall cannot host a wall foundation");
        Check(Thrown(() => CommandRulesStructure.CheckWallFoundationHost("Stacked", 0)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.host_wall_uid" }, "a stacked wall cannot host a wall foundation");
        Check(Thrown(() => CommandRulesStructure.CheckWallFoundationHost("Basic", 1)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.host_wall_uid" } existing && existing.Detail!.Contains("already", StringComparison.Ordinal), "a wall that already has a wall foundation is args_invalid");
    }

    static void Readbacks()
    {
        var grid = new ObservedGrid(ElementUid, "A", false, P(0, 0), P(0, 12000));
        Check(CommandReadbackStructure.Grid(P(0, 0), P(0, 12000), "A", grid) is null, "grid readback matches");
        Check(CommandReadbackStructure.Grid(P(0, 0), P(0, 12000), null, grid with { Name = "1" }) is null, "an automatic grid name is accepted when none was requested");
        Check(CommandReadbackStructure.Grid(P(0, 0), P(0, 12000), "A", grid with { Name = "B" }) is { } name && name.Contains("name", StringComparison.Ordinal), "a grid name other than the requested one is a mismatch");
        Check(CommandReadbackStructure.Grid(P(0, 0), P(0, 12000), "A", grid with { Curved = true }) is { } curved && curved.Contains("curved", StringComparison.Ordinal), "a curved grid is a mismatch");
        Check(CommandReadbackStructure.Grid(P(0, 0), P(0, 12000), "A", grid with { End = P(0, 11000) }) is not null, "a grid end elsewhere is a mismatch");
        Check(CommandReadbackStructure.Grid(P(0, 0), P(0, 12000), "A", grid with { Start = P(0, 12000), End = P(0, 0) }) is not null, "a reversed grid is a mismatch (the bubble end follows the start)");

        var column = new ObservedColumn(ElementUid, TypeUid, "Column", false, P(6000, 4000), Level1Uid, Level2Uid, 0, 0);
        Check(CommandReadbackStructure.Column(TypeUid, P(6000, 4000), Level1Uid, Level2Uid, 0, 0, column) is null, "column readback matches");
        Check(CommandReadbackStructure.Column(OtherTypeUid, P(6000, 4000), Level1Uid, Level2Uid, 0, 0, column) is { } type && type.Contains("type", StringComparison.Ordinal), "a column of another type is a mismatch");
        Check(CommandReadbackStructure.Column(TypeUid, P(6000, 4000), Level1Uid, Level2Uid, 0, 0, column with { StructuralType = "NonStructural" }) is { } kind && kind.Contains("structural type", StringComparison.Ordinal), "a non-structural column is a mismatch");
        Check(CommandReadbackStructure.Column(TypeUid, P(6000, 4000), Level1Uid, Level2Uid, 0, 0, column with { Slanted = true }) is { } slanted && slanted.Contains("slanted", StringComparison.Ordinal), "a slanted column is a mismatch");
        Check(CommandReadbackStructure.Column(TypeUid, P(6000, 4000), Level1Uid, Level2Uid, 0, 0, column with { Point = P(6000, 4100) }) is not null, "a column elsewhere in plan is a mismatch");
        Check(CommandReadbackStructure.Column(TypeUid, P(6000, 4000), Level1Uid, Level2Uid, 0, 0, column with { Point = null }) is not null, "an unreadable column location is a mismatch");
        Check(CommandReadbackStructure.Column(TypeUid, P(6000, 4000), Level1Uid, Level2Uid, 0, 0, column with { BaseLevelUid = Level2Uid }) is { } baseLevel && baseLevel.Contains("base level", StringComparison.Ordinal), "a column on another base level is a mismatch");
        Check(CommandReadbackStructure.Column(TypeUid, P(6000, 4000), Level1Uid, Level2Uid, 0, 0, column with { TopLevelUid = null }) is { } topLevel && topLevel.Contains("top level", StringComparison.Ordinal), "an unreadable column top level is a mismatch");
        Check(CommandReadbackStructure.Column(TypeUid, P(6000, 4000), Level1Uid, Level2Uid, -300, 0, column) is { } baseOffset && baseOffset.Contains("base offset", StringComparison.Ordinal), "a base offset that did not take is a mismatch");
        Check(CommandReadbackStructure.Column(TypeUid, P(6000, 4000), Level1Uid, Level2Uid, 0, 150, column with { TopOffsetMm = null }) is { } topOffset && topOffset.Contains("top offset", StringComparison.Ordinal), "an unreadable top offset is a mismatch");
        Check(CommandReadbackStructure.Column(TypeUid, P(6000, 4000), Level1Uid, Level2Uid, -300, 150, column with { BaseOffsetMm = -300.0004, TopOffsetMm = 150.0004 }) is null, "offsets within the 1e-3 mm tolerance match");

        var beam = new ObservedBeam(ElementUid, TypeUid, "Beam", Level2Uid, P(0, 0), P(6000, 0));
        Check(CommandReadbackStructure.Beam(TypeUid, Level2Uid, P(0, 0), P(6000, 0), beam) is null, "beam readback matches");
        Check(CommandReadbackStructure.Beam(TypeUid, Level2Uid, P(0, 0), P(6000, 0), beam with { StructuralType = "Brace" }) is { } brace && brace.Contains("structural type", StringComparison.Ordinal), "a brace is a mismatch for a beam");
        Check(CommandReadbackStructure.Beam(TypeUid, Level1Uid, P(0, 0), P(6000, 0), beam) is { } level && level.Contains("level", StringComparison.Ordinal), "a beam on another reference level is a mismatch");
        Check(CommandReadbackStructure.Beam(TypeUid, Level2Uid, P(0, 0), P(6000, 0), beam with { LevelUid = null }) is not null, "an unreadable beam reference level is a mismatch");
        Check(CommandReadbackStructure.Beam(TypeUid, Level2Uid, P(0, 0), P(6000, 0), beam with { Start = P(6000, 0), End = P(0, 0) }) is not null, "a reversed beam is a mismatch");
        Check(CommandReadbackStructure.Beam(TypeUid, Level2Uid, P(0, 0), P(6000, 0), beam with { End = null }) is not null, "an unreadable beam location curve is a mismatch");
        Check(CommandReadbackStructure.Beam(OtherTypeUid, Level2Uid, P(0, 0), P(6000, 0), beam) is not null, "a beam of another type is a mismatch");

        var footing = new ObservedFooting(ElementUid, TypeUid, "Footing", Level1Uid, P(6000, 4000));
        Check(CommandReadbackStructure.Footing(TypeUid, Level1Uid, P(6000, 4000), footing) is null, "isolated foundation readback matches");
        Check(CommandReadbackStructure.Footing(TypeUid, Level1Uid, P(6000, 4000), footing with { StructuralType = "NonStructural" }) is not null, "a non-structural foundation is a mismatch");
        Check(CommandReadbackStructure.Footing(TypeUid, Level2Uid, P(6000, 4000), footing) is { } footingLevel && footingLevel.Contains("level", StringComparison.Ordinal), "a foundation on another level is a mismatch");
        Check(CommandReadbackStructure.Footing(TypeUid, Level1Uid, P(6000, 4000), footing with { Point = P(6001, 4000) }) is not null, "a foundation elsewhere in plan is a mismatch");
        Check(CommandReadbackStructure.Footing(OtherTypeUid, Level1Uid, P(6000, 4000), footing) is not null, "a foundation of another type is a mismatch");

        var wallFoundation = new ObservedWallFoundation(ElementUid, TypeUid, WallUid);
        Check(CommandReadbackStructure.WallFoundation(TypeUid, WallUid, wallFoundation) is null, "wall foundation readback matches");
        Check(CommandReadbackStructure.WallFoundation(TypeUid, WallUid, wallFoundation with { WallUid = null }) is { } host && host.Contains("wall", StringComparison.Ordinal), "a wall foundation without its host wall is a mismatch");
        Check(CommandReadbackStructure.WallFoundation(OtherTypeUid, WallUid, wallFoundation) is { } wallType && wallType.Contains("type", StringComparison.Ordinal), "a wall foundation of another type is a mismatch");
    }

    static void ReadbackJson()
    {
        var grids = JsonNode.Parse(CommandReadbackStructure.GridsJson(new[] { new GridRow("B", OtherTypeUid, false, P(6000, 0), P(6000, 12000)), new GridRow("A", TypeUid, true, P(0, 0), P(0, 12000)) }))!["grids"]!.AsArray();
        Check(grids.Select(row => row!["name"]!.GetValue<string>()).SequenceEqual(new[] { "A", "B" }) && grids[0]!["curved"]!.GetValue<bool>() && grids[1]!["end"]![1]!.GetValue<double>() == 12000 && grids[1]!["unique_id"]!.GetValue<string>() == OtherTypeUid,
            "grids readback is sorted by name with UniqueId, curved flag and plan end points", grids.ToJsonString());
        var types = JsonNode.Parse(CommandReadbackStructure.StructuralTypesJson("column", new[] { new StructuralTypeRow("W-Wide Flange-Column", "W10x49", TypeUid, false), new StructuralTypeRow("Concrete-Rectangular-Column", "300 x 450mm", OtherTypeUid, true) }))!;
        var rows = types["structural_types"]!.AsArray();
        Check(types["category"]!.GetValue<string>() == "column" && rows.Select(row => row!["family"]!.GetValue<string>()).SequenceEqual(new[] { "Concrete-Rectangular-Column", "W-Wide Flange-Column" }) && rows[1]!["reference"]!.GetValue<string>() == "W-Wide Flange-Column: W10x49" && !rows[1]!["active"]!.GetValue<bool>(),
            "structural types readback is sorted by family then type with the Family: Type reference and active flag", types.ToJsonString());
        var wallTypes = JsonNode.Parse(CommandReadbackStructure.StructuralTypesJson("wall_foundation", new[] { new StructuralTypeRow(null, "Bearing Footing - 900 x 300", TypeUid, true) }))!["structural_types"]![0]!;
        Check(wallTypes["family"] is null && wallTypes["reference"]!.GetValue<string>() == "Bearing Footing - 900 x 300", "a wall foundation type has no family and is referenced by its name", wallTypes.ToJsonString());

        var grid = JsonNode.Parse(CommandReadbackStructure.GridJson(new ObservedGrid(ElementUid, "A", false, P(0, 0), P(0, 12000))))!["grid"]!;
        Check(grid["unique_id"]!.GetValue<string>() == ElementUid && grid["name"]!.GetValue<string>() == "A" && !grid["curved"]!.GetValue<bool>() && grid["start"]![0]!.GetValue<double>() == 0 && grid["end"]![1]!.GetValue<double>() == 12000, "grid readback JSON reports UniqueId, name, curved flag and end points", grid.ToJsonString());
        var column = JsonNode.Parse(CommandReadbackStructure.ColumnJson(new ObservedColumn(ElementUid, TypeUid, "Column", false, P(6000, 4000), Level1Uid, Level2Uid, -300, null)))!["structural_column"]!;
        Check(column["type_unique_id"]!.GetValue<string>() == TypeUid && column["structural_type"]!.GetValue<string>() == "Column" && !column["slanted"]!.GetValue<bool>() && column["point"]![0]!.GetValue<double>() == 6000
            && column["base_level_unique_id"]!.GetValue<string>() == Level1Uid && column["top_level_unique_id"]!.GetValue<string>() == Level2Uid && column["base_offset_mm"]!.GetValue<double>() == -300 && column["top_offset_mm"] is null,
            "column readback JSON reports type, structural type, slant, point, levels and offsets (null when unreadable)", column.ToJsonString());
        var beam = JsonNode.Parse(CommandReadbackStructure.BeamJson(new ObservedBeam(ElementUid, TypeUid, "Beam", Level2Uid, P(0, 0), null)))!["structural_beam"]!;
        Check(beam["level_unique_id"]!.GetValue<string>() == Level2Uid && beam["structural_type"]!.GetValue<string>() == "Beam" && beam["start"]![0]!.GetValue<double>() == 0 && beam["end"] is null, "beam readback JSON reports level, structural type and end points (null when unreadable)", beam.ToJsonString());
        var footing = JsonNode.Parse(CommandReadbackStructure.FootingJson(new ObservedFooting(ElementUid, TypeUid, "Footing", Level1Uid, P(6000, 4000))))!["foundation"]!;
        Check(footing["structural_type"]!.GetValue<string>() == "Footing" && footing["level_unique_id"]!.GetValue<string>() == Level1Uid && footing["point"]![1]!.GetValue<double>() == 4000, "isolated foundation readback JSON reports structural type, level and point", footing.ToJsonString());
        var wallFoundation = JsonNode.Parse(CommandReadbackStructure.WallFoundationJson(new ObservedWallFoundation(ElementUid, TypeUid, WallUid)))!["wall_foundation"]!;
        Check(wallFoundation["unique_id"]!.GetValue<string>() == ElementUid && wallFoundation["type_unique_id"]!.GetValue<string>() == TypeUid && wallFoundation["host_wall_unique_id"]!.GetValue<string>() == WallUid, "wall foundation readback JSON reports type and host wall", wallFoundation.ToJsonString());
    }
}
