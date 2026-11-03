using Bim.Core;
using Bim.Revit;
using System.Text.Json;
using System.Text.Json.Nodes;
// Materials and layered roof types contract oracle (chat-roof-gable): query.materials and roof_type.create. Covers the
// schemas, the typed validators, the guarded client path (fake transport) and the Autodesk-free executor rules,
// readback checks and readback JSON in runtimes/revit-native/CommandsMaterials.cs. No Revit is involved: a passing
// run is unit-tested evidence only; CommandsExecutorMaterials.cs is compiled, never run.
static class CatalogueMaterialsChecks
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string TypeUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000200";
    const string TileUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000a10";
    const string MembraneUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000a11";
    const string BoardsUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000a12";
    const string RaftersUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000a13";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";
    const string Name = "Cabin roof - tile on boards";

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static JsonObject Envelope(string command, JsonNode args, string path = FixturePath) => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-materials",
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
        ["command"] = command,
        ["args"] = args.DeepClone(),
    };
    static JsonObject Layer(string function, string material, double mm) => new() { ["function"] = function, ["material"] = material, ["thickness_mm"] = mm };
    static JsonArray Layers(params JsonObject[] layers) => new(layers.Select(layer => (JsonNode?)layer).ToArray());
    // Exterior to interior: tiles, a membrane, tongue-and-groove boards, rafters.
    static JsonArray CabinLayers() => Layers(Layer("finish1", "Roofing - Tile", 20), Layer("membrane", "Roofing - Membrane", 0), Layer("substrate", "Wood - Tongue and Groove", 19), Layer("structure", "Wood - Rafters", 150));
    static JsonObject Cabin() => new() { ["name"] = Name, ["base_type"] = "Basic Roof - Generic 300mm", ["layers"] = CabinLayers() };
    static JsonObject With(JsonObject args, string member, JsonNode? value) { if (value is null) args.Remove(member); else args[member] = value; return args; }
    static bool Near(double a, double b, double tolerance = 1e-9) => Math.Abs(a - b) <= tolerance;

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

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        Catalogue();
        Binding();
        ClientPath();
        Rules();
        Readbacks();
        ReadbackJson();
    }

    static void Catalogue()
    {
        var names = RevitCommandCatalogue.Materials.Select(spec => spec.Name).ToArray();
        Check(names.SequenceEqual(new[] { "query.materials", "roof_type.create" }), "materials catalogue lists query.materials then roof_type.create", string.Join(",", names));
        var throughModify = RevitCommandCatalogue.V1.Concat(RevitCommandCatalogue.V2).Concat(RevitCommandCatalogue.Views).Concat(RevitCommandCatalogue.Mirror).Concat(RevitCommandCatalogue.Elements)
            .Concat(RevitCommandCatalogue.Structure).Concat(RevitCommandCatalogue.Documentation).Concat(RevitCommandCatalogue.Modify).ToArray();
        Check(RevitCommandCatalogue.All.Take(throughModify.Length).SequenceEqual(throughModify) && RevitCommandCatalogue.All.Skip(throughModify.Length).Take(names.Length).SequenceEqual(RevitCommandCatalogue.Materials),
            "All is every earlier part through modify, then the materials commands");
        Check(RevitCommandCatalogue.All.Select(spec => spec.Name).Distinct().Count() == RevitCommandCatalogue.All.Count && RevitCommandCatalogue.All.Select(spec => spec.ToolName).Distinct().Count() == RevitCommandCatalogue.All.Count, "command and tool names stay distinct with the materials commands");
        Check(RevitCommandCatalogue.Materials.Select(spec => spec.ToolName).SequenceEqual(new[] { "revit_query_materials", "revit_roof_type_create" }), "materials tool names are revit_query_materials and revit_roof_type_create");
        Check(RevitCommandCatalogue.Find("query.materials") is { Writes: false } && RevitCommandCatalogue.Find("roof_type.create") is { Writes: true }, "query.materials is read-only and roof_type.create writes");
        foreach (var name in names) Check(ReferenceEquals(RevitCommandCatalogue.Find(name), RevitCommandCatalogue.Materials.Single(spec => spec.Name == name)), "Find resolves materials command " + name);
        Check(RevitCommandCatalogue.Materials.All(spec => spec.Description.Length > 20 && spec.Description.EndsWith('.') && (!spec.Writes || spec.Description.Contains("enabled test copies", StringComparison.Ordinal))), "every materials command has a plain-English description; the write states the copies-only rule");
        Check(RevitCommandCatalogue.Find("roof_type.create")!.Description.Contains("exterior to interior", StringComparison.Ordinal) && RevitCommandCatalogue.Find("roof_type.create")!.Description.Contains("millimetres", StringComparison.Ordinal), "roof_type.create description states the layer order and units");
        Check(!RevitCommandCatalogue.IsDestructive("roof_type.create") && !RevitCommandCatalogue.IsDestructive("query.materials"), "roof_type.create only adds a type: not destructive");
        foreach (var spec in RevitCommandCatalogue.Materials)
        {
            string? problem = null;
            try { using var schema = JsonDocument.Parse(spec.ArgsSchemaJson); RevitCommandSchema.CheckSupported(schema.RootElement); if (schema.RootElement.GetProperty("type").GetString() != "object") problem = "not an object schema"; }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { problem = error.Message; }
            Check(problem is null, spec.Name + " args schema is valid JSON using only enforced keywords", problem ?? "");
        }
        var copies = new[] { @"C:\Repo\.bim\native-runs" };
        foreach (var spec in RevitCommandCatalogue.Materials)
            Check(PermissionPolicy.RequiredLevel(spec.Name, FixturePath, copies) == (spec.Writes ? PermissionLevel.WriteCopies : PermissionLevel.Read) && (!spec.Writes || PermissionPolicy.RequiredLevel(spec.Name, RealPath, copies) == PermissionLevel.WriteRealProject), "permission level of " + spec.Name + " follows its catalogue entry");
        Check(Accepts("query.materials with {} accepted", Envelope("query.materials", new JsonObject()))?.Args is EmptyQueryArgs, "query.materials binds empty arguments");
        Rejects("query.materials with an argument", Envelope("query.materials", new JsonObject { ["class"] = "Wood" }), RevitCommandCodes.ArgsInvalid, "args.class");
    }

    static void Binding()
    {
        var cabin = Accepts("cabin roof type accepted", Envelope("roof_type.create", Cabin()));
        Check(cabin?.Args is RoofTypeCreateArgs { Name: Name, BaseType: "Basic Roof - Generic 300mm", Layers.Count: 4 } args
            && args.Layers[0] is { Function: "finish1", Material: "Roofing - Tile" } tile && Near(tile.Thickness.Millimetres, 20)
            && args.Layers[1] is { Function: "membrane" } membrane && membrane.Thickness.Millimetres == 0
            && args.Layers[2] is { Function: "substrate" } boards && Near(boards.Thickness.Millimetres, 19)
            && args.Layers[3] is { Function: "structure", Material: "Wood - Rafters" } rafters && Near(rafters.Thickness.Millimetres, 150),
            "roof_type.create binds name, base type and the layers exterior to interior with thickness in millimetres");
        Check(RevitMaterialRules.Functions.SequenceEqual(new[] { "finish1", "finish2", "substrate", "structure", "insulation", "membrane" }), "layer functions are finish1, finish2, substrate, structure, insulation and membrane");
        Accepts("single structure layer accepted", Envelope("roof_type.create", With(Cabin(), "layers", Layers(Layer("structure", "Concrete", 200)))));
        Accepts("material by UniqueId accepted", Envelope("roof_type.create", With(Cabin(), "layers", Layers(Layer("insulation", TileUid, 100.5)))));
        Accepts("32 layers accepted", Envelope("roof_type.create", With(Cabin(), "layers", Layers(Enumerable.Range(0, 32).Select(_ => Layer("finish2", "Wood", 10)).ToArray()))));

        Rejects("roof type without name", Envelope("roof_type.create", With(Cabin(), "name", null)), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("roof type without base type", Envelope("roof_type.create", With(Cabin(), "base_type", null)), RevitCommandCodes.ArgsInvalid, "args.base_type");
        Rejects("roof type without layers", Envelope("roof_type.create", With(Cabin(), "layers", null)), RevitCommandCodes.ArgsInvalid, "args.layers");
        Rejects("roof type with no layer", Envelope("roof_type.create", With(Cabin(), "layers", new JsonArray())), RevitCommandCodes.ArgsInvalid, "args.layers");
        Rejects("roof type with 33 layers", Envelope("roof_type.create", With(Cabin(), "layers", Layers(Enumerable.Range(0, 33).Select(_ => Layer("finish2", "Wood", 10)).ToArray()))), RevitCommandCodes.ArgsInvalid, "args.layers");
        Rejects("unknown layer function", Envelope("roof_type.create", With(Cabin(), "layers", Layers(Layer("core", "Wood", 10)))), RevitCommandCodes.ArgsInvalid, "args.layers[0].function");
        Rejects("membrane with a thickness", Envelope("roof_type.create", With(Cabin(), "layers", Layers(Layer("structure", "Wood", 100), Layer("membrane", "Roofing - Membrane", 5)))), RevitCommandCodes.LengthInvalid, "args.layers[1].thickness_mm");
        Rejects("structure layer of zero thickness", Envelope("roof_type.create", With(Cabin(), "layers", Layers(Layer("finish1", "Tile", 20), Layer("structure", "Wood", 0)))), RevitCommandCodes.LengthInvalid, "args.layers[1].thickness_mm");
        Rejects("negative layer thickness", Envelope("roof_type.create", With(Cabin(), "layers", Layers(Layer("structure", "Wood", -10)))), RevitCommandCodes.LengthInvalid, "args.layers[0].thickness_mm");
        Rejects("layer thicker than the Revit limit", Envelope("roof_type.create", With(Cabin(), "layers", Layers(Layer("structure", "Wood", 9_144_001)))), RevitCommandCodes.LengthInvalid, "args.layers[0].thickness_mm");
        RejectsText("layer thickness overflowing to infinity", Envelope("roof_type.create", With(Cabin(), "layers", Layers(Layer("structure", "Wood", 150)))).ToJsonString().Replace("\"thickness_mm\":150", "\"thickness_mm\":1e400"), RevitCommandCodes.LengthInvalid, "args.layers[0].thickness_mm");
        Rejects("only membrane layers", Envelope("roof_type.create", With(Cabin(), "layers", Layers(Layer("membrane", "Roofing - Membrane", 0)))), RevitCommandCodes.ArgsInvalid, "args.layers");
        Rejects("layer thickness as string", Envelope("roof_type.create", With(Cabin(), "layers", Layers(new JsonObject { ["function"] = "structure", ["material"] = "Wood", ["thickness_mm"] = "150" }))), RevitCommandCodes.ArgsInvalid, "args.layers[0].thickness_mm");
        Rejects("layer without material", Envelope("roof_type.create", With(Cabin(), "layers", Layers(new JsonObject { ["function"] = "structure", ["thickness_mm"] = 150 }))), RevitCommandCodes.ArgsInvalid, "args.layers[0].material");
        Rejects("layer with empty material", Envelope("roof_type.create", With(Cabin(), "layers", Layers(Layer("structure", "", 150)))), RevitCommandCodes.ArgsInvalid, "args.layers[0].material");
        Rejects("layer with unexpected member", Envelope("roof_type.create", With(Cabin(), "layers", Layers(With(Layer("structure", "Wood", 150), "wraps", true)))), RevitCommandCodes.ArgsInvalid, "args.layers[0].wraps");
        Rejects("roof type name with a colon", Envelope("roof_type.create", With(Cabin(), "name", "Cabin: roof")), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("roof type name with a leading space", Envelope("roof_type.create", With(Cabin(), "name", " Cabin roof")), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("empty roof type name", Envelope("roof_type.create", With(Cabin(), "name", "")), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("roof type with unexpected member", Envelope("roof_type.create", With(Cabin(), "family", "Basic Roof")), RevitCommandCodes.ArgsInvalid, "args.family");
    }

    static void ClientPath()
    {
        var policy = new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" });
        var forwarded = new List<string>();
        string? Transport(string pipe, string line) { forwarded.Add(line); return null; }
        var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
        var onCopy = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("roof_type.create", Cabin()).ToJsonString(), policy, Transport, noLinks);
        Check(forwarded.Count == 1 && forwarded[0] == RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(Envelope("roof_type.create", Cabin()).ToJsonString())), "roof_type.create on an enabled copy is forwarded as the canonical wire line", onCopy.Line);
        var refused = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("roof_type.create", Cabin(), RealPath).ToJsonString(), policy, Transport, noLinks);
        Check(refused.IsError && !refused.Forwarded && JsonNode.Parse(refused.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentNotEnabled && forwarded.Count == 1, "roof_type.create on a real project is document_not_enabled and never forwarded", refused.Line);
        RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("query.materials", new JsonObject(), RealPath).ToJsonString(), policy, Transport, noLinks);
        Check(forwarded.Count == 2 && forwarded[1].Contains("query.materials", StringComparison.Ordinal), "query.materials is read-only and forwarded on a real project");
    }

    static void Rules()
    {
        foreach (var (function, revit) in new[] { ("finish1", "Finish1"), ("finish2", "Finish2"), ("substrate", "Substrate"), ("structure", "Structure"), ("insulation", "Insulation"), ("membrane", "Membrane") })
            Check(CommandRulesMaterials.RevitFunction(function) == revit && CommandRulesMaterials.FunctionName(revit) == function, "layer function " + function + " is Revit MaterialFunctionAssignment." + revit + " both ways");
        Check(CommandRulesMaterials.FunctionName("StructuralDeck") == "structuraldeck" && CommandRulesMaterials.FunctionName("None") == "none", "other Revit layer functions read back under their own lower-case name (never one of the six)");
    }

    static IReadOnlyList<ExpectedRoofLayer> Expected() => new[]
    {
        new ExpectedRoofLayer("finish1", TileUid, 20), new ExpectedRoofLayer("membrane", MembraneUid, 0), new ExpectedRoofLayer("substrate", BoardsUid, 19), new ExpectedRoofLayer("structure", RaftersUid, 150),
    };
    static ObservedRoofType Observed() => new(TypeUid, Name, new[]
    {
        new ObservedRoofLayer("finish1", TileUid, "Roofing - Tile", 20), new ObservedRoofLayer("membrane", MembraneUid, "Roofing - Membrane", 0),
        new ObservedRoofLayer("substrate", BoardsUid, "Wood - Tongue and Groove", 19.00004), new ObservedRoofLayer("structure", RaftersUid, "Wood - Rafters", 150),
    }, 189);

    static ObservedRoofType Replace(int index, ObservedRoofLayer layer)
    {
        var observed = Observed();
        var layers = observed.Layers!.ToArray();
        layers[index] = layer;
        return observed with { Layers = layers };
    }

    static void Readbacks()
    {
        var observed = Observed();
        Check(CommandReadbackMaterials.RoofType(Name, Expected(), observed) is null, "roof type readback matches (thickness within 1e-3 mm)");
        Check(CommandReadbackMaterials.RoofType(Name, Expected(), observed with { Name = Name.ToUpperInvariant() }) is { } name && name.Contains("name", StringComparison.Ordinal), "a roof type of another name (even only in case) is a mismatch");
        Check(CommandReadbackMaterials.RoofType(Name, Expected(), observed with { Layers = null }) is { } none && none.Contains("layers", StringComparison.Ordinal), "a roof type without a compound structure is a mismatch");
        Check(CommandReadbackMaterials.RoofType(Name, Expected(), observed with { Layers = observed.Layers!.Take(3).ToArray() }) is { } count && count.Contains("3 layers", StringComparison.Ordinal), "a roof type with another layer count is a mismatch");
        Check(CommandReadbackMaterials.RoofType(Name, Expected(), Replace(0, new ObservedRoofLayer("finish2", TileUid, "Roofing - Tile", 20))) is { } function && function.Contains("layer 0", StringComparison.Ordinal) && function.Contains("function", StringComparison.Ordinal), "a layer with another function is a mismatch naming the layer");
        Check(CommandReadbackMaterials.RoofType(Name, Expected(), Replace(2, new ObservedRoofLayer("substrate", RaftersUid, "Wood - Rafters", 19))) is { } material && material.Contains("material", StringComparison.Ordinal), "a layer of another material is a mismatch");
        Check(CommandReadbackMaterials.RoofType(Name, Expected(), Replace(2, new ObservedRoofLayer("substrate", null, null, 19))) is not null, "a layer without a material is a mismatch");
        Check(CommandReadbackMaterials.RoofType(Name, Expected(), Replace(3, new ObservedRoofLayer("structure", RaftersUid, "Wood - Rafters", 150.01))) is { } thickness && thickness.Contains("thickness", StringComparison.Ordinal), "a layer of another thickness is a mismatch");
        Check(CommandReadbackMaterials.RoofType(Name, Expected(), Replace(0, observed.Layers![3])) is not null, "layers in another order are a mismatch (exterior to interior)");
        Check(CommandReadbackMaterials.RoofType(Name, Expected().Reverse().ToArray(), observed) is not null, "the requested order is the order read back");
    }

    static void ReadbackJson()
    {
        var materials = JsonNode.Parse(CommandReadbackMaterials.MaterialsJson(new[] { new MaterialRow("Wood - Rafters", RaftersUid, "Wood", "Wood"), new MaterialRow("Roofing - Tile", TileUid, null, "Roofing"), new MaterialRow("Roofing - Tile", BoardsUid, "Ceramic", null) }))!;
        var rows = materials["materials"]!.AsArray();
        Check(rows.Count == 3 && rows.Select(row => row!["name"]!.GetValue<string>()).SequenceEqual(new[] { "Roofing - Tile", "Roofing - Tile", "Wood - Rafters" }) && rows[0]!["unique_id"]!.GetValue<string>() == TileUid && rows[1]!["unique_id"]!.GetValue<string>() == BoardsUid,
            "materials readback is sorted by name then UniqueId", materials.ToJsonString());
        Check(rows[0]!["class"] is null && rows[0]!["category"]!.GetValue<string>() == "Roofing" && rows[2]!["class"]!.GetValue<string>() == "Wood" && rows[1]!["category"] is null, "materials readback reports class and category, null when Revit has none", materials.ToJsonString());
        var created = JsonNode.Parse(CommandReadbackMaterials.RoofTypeJson(Observed(), existing: false))!["roof_type"]!;
        Check(created["unique_id"]!.GetValue<string>() == TypeUid && created["name"]!.GetValue<string>() == Name && created["existing"]!.GetValue<bool>() == false && created["width_mm"]!.GetValue<double>() == 189,
            "roof type readback JSON reports UniqueId, name, whether it already existed and the total width", created.ToJsonString());
        var layers = created["layers"]!.AsArray();
        Check(layers.Count == 4 && layers[0]!["function"]!.GetValue<string>() == "finish1" && layers[0]!["material_unique_id"]!.GetValue<string>() == TileUid && layers[0]!["material_name"]!.GetValue<string>() == "Roofing - Tile"
            && layers[1]!["thickness_mm"]!.GetValue<double>() == 0 && layers[3]!["function"]!.GetValue<string>() == "structure", "roof type readback JSON lists the layers exterior to interior with function, material and thickness", created.ToJsonString());
        Check(JsonNode.Parse(CommandReadbackMaterials.RoofTypeJson(Observed(), existing: true))!["roof_type"]!["existing"]!.GetValue<bool>(), "an identical existing roof type is reported with existing true");
        var bare = JsonNode.Parse(CommandReadbackMaterials.RoofTypeJson(Observed() with { Layers = null, WidthMm = null }, existing: false))!["roof_type"]!;
        Check(bare["layers"]!.AsArray().Count == 0 && bare["width_mm"] is null, "a roof type without layers reads back an empty layer list and null width", bare.ToJsonString());
    }
}
