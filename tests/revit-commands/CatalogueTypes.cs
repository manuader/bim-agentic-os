using Bim.Core;
using Bim.Revit;
using System.Text.Json;
using System.Text.Json.Nodes;
// Material and layered type creation contract oracle (P9-06): material.create, wall_type.create and floor_type.create.
// Covers the schemas, the typed validators, the guarded client path (fake transport) and the Autodesk-free executor
// rules, readback checks and readback JSON in runtimes/revit-native/CommandsTypes.cs. No Revit is involved: a passing
// run is unit-tested evidence only; CommandsExecutorTypes.cs is compiled, never run.
static class CatalogueTypesChecks
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string TypeUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000300";
    const string MaterialUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000b01";
    const string AssetUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000b02";
    const string StoneUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000a20";
    const string InsulationUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000a21";
    const string BlockUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000a22";
    const string PlasterUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000a23";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";
    const string StoneName = "Stone - Patagonian slate";
    const string WallName = "Cabin wall - stone on block";
    const string FloorName = "Cabin floor - timber on slab";
    static readonly string[] Commands = { "material.create", "wall_type.create", "floor_type.create" };

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static JsonObject Envelope(string command, JsonNode args, string path = FixturePath) => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-types",
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
        ["command"] = command,
        ["args"] = args.DeepClone(),
    };
    static JsonObject Layer(string function, string material, double mm) => new() { ["function"] = function, ["material"] = material, ["thickness_mm"] = mm };
    static JsonArray Layers(params JsonObject[] layers) => new(layers.Select(layer => (JsonNode?)layer).ToArray());
    // Exterior to interior: stone cladding, a membrane, insulation, concrete block (structure), plaster.
    static JsonArray WallLayers() => Layers(Layer("finish1", StoneName, 30), Layer("membrane", "Membrane", 0), Layer("insulation", "Insulation - Rigid", 50), Layer("structure", "Concrete Masonry Units", 190), Layer("finish2", "Plaster", 15));
    static JsonObject Wall() => new() { ["name"] = WallName, ["base_type"] = "Generic - 200mm", ["layers"] = WallLayers() };
    // Top to bottom: timber boards, insulation, concrete slab.
    static JsonObject Floor() => new() { ["name"] = FloorName, ["base_type"] = "Generic 150mm", ["layers"] = Layers(Layer("finish1", "Wood - Flooring", 20), Layer("insulation", "Insulation - Rigid", 40), Layer("structure", "Concrete - Cast-in-Place", 150)) };
    static JsonObject Stone() => new()
    {
        ["name"] = StoneName,
        ["color"] = new JsonObject { ["r"] = 96, ["g"] = 92, ["b"] = 88 },
        ["transparency"] = 0,
        ["shininess"] = 20,
        ["smoothness"] = 40,
        ["material_class"] = "Stone",
        ["appearance_asset"] = "Stone - Slate",
    };
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
    static RevitCommandException? Thrown(Action action)
    {
        try { action(); return null; }
        catch (RevitCommandException error) { return error; }
    }

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        Catalogue();
        MaterialBinding();
        LayeredBinding("wall_type.create", Wall);
        LayeredBinding("floor_type.create", Floor);
        TypeSpecifics();
        ClientPath();
        Rules();
        LayeredReadbacks();
        MaterialReadbacks();
        ReadbackJson();
    }

    static void Catalogue()
    {
        var names = RevitCommandCatalogue.TypeCommands.Select(spec => spec.Name).ToArray();
        Check(names.SequenceEqual(Commands), "type catalogue lists material.create, wall_type.create and floor_type.create in order", string.Join(",", names));
        Check(RevitCommandCatalogue.TypeCommands.All(spec => RevitCommandCatalogue.All.Contains(spec)), "every type command is in the catalogue enumeration");
        Check(RevitCommandCatalogue.All.Select(spec => spec.Name).Distinct().Count() == RevitCommandCatalogue.All.Count && RevitCommandCatalogue.All.Select(spec => spec.ToolName).Distinct().Count() == RevitCommandCatalogue.All.Count, "command and tool names stay distinct with the type commands");
        Check(RevitCommandCatalogue.TypeCommands.Select(spec => spec.ToolName).SequenceEqual(new[] { "revit_material_create", "revit_wall_type_create", "revit_floor_type_create" }), "type tool names are revit_material_create, revit_wall_type_create and revit_floor_type_create");
        Check(RevitCommandCatalogue.TypeCommands.All(spec => spec.Writes), "the three type commands write");
        Check(RevitCommandCatalogue.TypeCommands.All(spec => !RevitCommandCatalogue.IsDestructive(spec.Name)), "type commands only add a material or type: none is destructive");
        foreach (var name in names) Check(ReferenceEquals(RevitCommandCatalogue.Find(name), RevitCommandCatalogue.TypeCommands.Single(spec => spec.Name == name)), "Find resolves type command " + name);
        Check(RevitCommandCatalogue.TypeCommands.All(spec => spec.Description.Length > 20 && spec.Description.EndsWith('.') && spec.Description.Contains("enabled test copies", StringComparison.Ordinal)), "every type command has a plain-English description stating the copies-only rule");
        Check(RevitCommandCatalogue.Find("wall_type.create")!.Description.Contains("exterior to interior", StringComparison.Ordinal) && RevitCommandCatalogue.Find("wall_type.create")!.Description.Contains("millimetres", StringComparison.Ordinal)
            && RevitCommandCatalogue.Find("wall_type.create")!.Description.Contains("curtain", StringComparison.Ordinal), "wall_type.create description states the layer order, units and that curtain/stacked bases are refused");
        Check(RevitCommandCatalogue.Find("floor_type.create")!.Description.Contains("top to bottom", StringComparison.Ordinal) && RevitCommandCatalogue.Find("floor_type.create")!.Description.Contains("millimetres", StringComparison.Ordinal), "floor_type.create description states the layer order and units");
        Check(RevitCommandCatalogue.Find("material.create")!.Description.Contains("identical", StringComparison.Ordinal), "material.create description states the identical-existing rule");
        foreach (var spec in RevitCommandCatalogue.TypeCommands)
        {
            string? problem = null;
            try { using var schema = JsonDocument.Parse(spec.ArgsSchemaJson); RevitCommandSchema.CheckSupported(schema.RootElement); if (schema.RootElement.GetProperty("type").GetString() != "object") problem = "not an object schema"; }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { problem = error.Message; }
            Check(problem is null, spec.Name + " args schema is valid JSON using only enforced keywords", problem ?? "");
        }
        using (var wall = JsonDocument.Parse(RevitCommandCatalogue.Find("wall_type.create")!.ArgsSchemaJson))
            Check(wall.RootElement.GetProperty("properties").GetProperty("base_type").GetProperty("description").GetString()!.Contains("revit_query_wall_types", StringComparison.Ordinal), "wall_type.create base_type points at revit_query_wall_types");
        using (var floor = JsonDocument.Parse(RevitCommandCatalogue.Find("floor_type.create")!.ArgsSchemaJson))
            Check(floor.RootElement.GetProperty("properties").GetProperty("base_type").GetProperty("description").GetString()!.Contains("revit_query_floor_types", StringComparison.Ordinal), "floor_type.create base_type points at revit_query_floor_types");
        using (var material = JsonDocument.Parse(RevitCommandCatalogue.Find("material.create")!.ArgsSchemaJson))
            Check(string.Join(",", material.RootElement.GetProperty("required").EnumerateArray().Select(item => item.GetString())) == "name", "material.create needs only a name; every property is optional");
        var copies = new[] { @"C:\Repo\.bim\native-runs" };
        foreach (var spec in RevitCommandCatalogue.TypeCommands)
            Check(PermissionPolicy.RequiredLevel(spec.Name, FixturePath, copies) == PermissionLevel.WriteCopies && PermissionPolicy.RequiredLevel(spec.Name, RealPath, copies) == PermissionLevel.WriteRealProject, "permission level of " + spec.Name + " is write-copies on a copy and write-real-project elsewhere");
        Check(RevitCommandCatalogue.Find("query.floor_types") is { Writes: false } && RevitCommandCatalogue.Find("query.wall_types") is { Writes: false }, "the existing read-only wall and floor type queries supply base types");
    }

    static void MaterialBinding()
    {
        var stone = Accepts("stone material accepted", Envelope("material.create", Stone()));
        Check(stone?.Args is MaterialCreateArgs { Name: StoneName, Color: { Red: 96, Green: 92, Blue: 88 }, Transparency: 0, Shininess: 20, Smoothness: 40, MaterialClass: "Stone", AppearanceAsset: "Stone - Slate" },
            "material.create binds name, colour, transparency, shininess, smoothness, class and appearance asset");
        var bare = Accepts("material with only a name accepted", Envelope("material.create", new JsonObject { ["name"] = "Sheet metal - corrugated" }));
        Check(bare?.Args is MaterialCreateArgs { Name: "Sheet metal - corrugated", Color: null, Transparency: null, Shininess: null, Smoothness: null, MaterialClass: null, AppearanceAsset: null }, "omitted material properties bind as null (not requested, not checked)");
        Accepts("material limits accepted", Envelope("material.create", new JsonObject { ["name"] = "Glass", ["color"] = new JsonObject { ["r"] = 255, ["g"] = 0, ["b"] = 255 }, ["transparency"] = 100, ["shininess"] = 128, ["smoothness"] = 100 }));
        Accepts("appearance asset by UniqueId accepted", Envelope("material.create", With(Stone(), "appearance_asset", AssetUid)));
        Check(RevitTypeRules.MaxTransparency == 100 && RevitTypeRules.MaxShininess == 128 && RevitTypeRules.MaxSmoothness == 100, "material ranges: transparency 0-100, shininess 0-128, smoothness 0-100");

        Rejects("material without name", Envelope("material.create", With(Stone(), "name", null)), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("material name with a bracket", Envelope("material.create", With(Stone(), "name", "Stone [slate]")), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("material name with a trailing space", Envelope("material.create", With(Stone(), "name", "Stone ")), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("empty material name", Envelope("material.create", With(Stone(), "name", "")), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("colour channel above 255", Envelope("material.create", With(Stone(), "color", new JsonObject { ["r"] = 256, ["g"] = 0, ["b"] = 0 })), RevitCommandCodes.ArgsInvalid, "args.color.r");
        Rejects("negative colour channel", Envelope("material.create", With(Stone(), "color", new JsonObject { ["r"] = 0, ["g"] = -1, ["b"] = 0 })), RevitCommandCodes.ArgsInvalid, "args.color.g");
        Rejects("negative red channel", Envelope("material.create", With(Stone(), "color", new JsonObject { ["r"] = -255, ["g"] = 0, ["b"] = 0 })), RevitCommandCodes.ArgsInvalid, "args.color.r");
        Rejects("negative blue channel", Envelope("material.create", With(Stone(), "color", new JsonObject { ["r"] = 0, ["g"] = 0, ["b"] = -1 })), RevitCommandCodes.ArgsInvalid, "args.color.b");
        Rejects("fractional colour channel", Envelope("material.create", With(Stone(), "color", new JsonObject { ["r"] = 0, ["g"] = 0, ["b"] = 12.5 })), RevitCommandCodes.ArgsInvalid, "args.color.b");
        Rejects("colour without blue", Envelope("material.create", With(Stone(), "color", new JsonObject { ["r"] = 0, ["g"] = 0 })), RevitCommandCodes.ArgsInvalid, "args.color.b");
        Rejects("colour with alpha", Envelope("material.create", With(Stone(), "color", new JsonObject { ["r"] = 0, ["g"] = 0, ["b"] = 0, ["a"] = 1 })), RevitCommandCodes.ArgsInvalid, "args.color.a");
        Rejects("colour as hex text", Envelope("material.create", With(Stone(), "color", "#605c58")), RevitCommandCodes.ArgsInvalid, "args.color");
        Rejects("transparency above 100", Envelope("material.create", With(Stone(), "transparency", 101)), RevitCommandCodes.ArgsInvalid, "args.transparency");
        Rejects("negative transparency", Envelope("material.create", With(Stone(), "transparency", -1)), RevitCommandCodes.ArgsInvalid, "args.transparency");
        Rejects("shininess above 128", Envelope("material.create", With(Stone(), "shininess", 129)), RevitCommandCodes.ArgsInvalid, "args.shininess");
        Rejects("smoothness above 100", Envelope("material.create", With(Stone(), "smoothness", 101)), RevitCommandCodes.ArgsInvalid, "args.smoothness");
        Rejects("huge smoothness", Envelope("material.create", With(Stone(), "smoothness", 9_000_000_000)), RevitCommandCodes.ArgsInvalid, "args.smoothness");
        Rejects("fractional transparency", Envelope("material.create", With(Stone(), "transparency", 0.5)), RevitCommandCodes.ArgsInvalid, "args.transparency");
        Rejects("empty material class", Envelope("material.create", With(Stone(), "material_class", "")), RevitCommandCodes.ArgsInvalid, "args.material_class");
        Rejects("material class with a leading space", Envelope("material.create", With(Stone(), "material_class", " Stone")), RevitCommandCodes.ArgsInvalid, "args.material_class");
        Rejects("empty appearance asset", Envelope("material.create", With(Stone(), "appearance_asset", "")), RevitCommandCodes.ArgsInvalid, "args.appearance_asset");
        Rejects("material with a texture path (not offered)", Envelope("material.create", With(Stone(), "texture", @"C:\textures\slate.jpg")), RevitCommandCodes.ArgsInvalid, "args.texture");
    }

    static void LayeredBinding(string command, Func<JsonObject> sample)
    {
        var what = command == "wall_type.create" ? "wall type" : "floor type";
        var parsed = Accepts(what + " accepted", Envelope(command, sample()));
        var layers = parsed?.Args switch { WallTypeCreateArgs wall => wall.Layers, FloorTypeCreateArgs floor => floor.Layers, _ => null };
        Check(layers is { Count: > 0 } && layers[0].Function == "finish1" && layers.All(layer => layer.Thickness.Millimetres >= 0) && layers.Count == sample()["layers"]!.AsArray().Count, command + " binds the layers in request order with thickness in millimetres");
        Accepts(what + " single structure layer accepted", Envelope(command, With(sample(), "layers", Layers(Layer("structure", "Concrete", 200)))));
        Accepts(what + " material by UniqueId accepted", Envelope(command, With(sample(), "layers", Layers(Layer("structure", BlockUid, 190.5)))));
        Accepts(what + " membrane outside the core accepted", Envelope(command, With(sample(), "layers", Layers(Layer("membrane", "Membrane", 0), Layer("structure", "Concrete", 200), Layer("membrane", "Membrane", 0)))));
        Accepts(what + " two structure layers around insulation accepted (cavity build-up)", Envelope(command, With(sample(), "layers", Layers(Layer("structure", "Brick", 110), Layer("insulation", "Insulation", 50), Layer("structure", "Block", 140)))));
        Accepts(what + " 32 layers accepted", Envelope(command, With(sample(), "layers", Layers(Enumerable.Range(0, 32).Select(i => Layer(i == 16 ? "structure" : "finish2", "Wood", 10)).ToArray()))));

        Rejects(what + " without name", Envelope(command, With(sample(), "name", null)), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects(what + " without base type", Envelope(command, With(sample(), "base_type", null)), RevitCommandCodes.ArgsInvalid, "args.base_type");
        Rejects(what + " without layers", Envelope(command, With(sample(), "layers", null)), RevitCommandCodes.ArgsInvalid, "args.layers");
        Rejects(what + " with no layer", Envelope(command, With(sample(), "layers", new JsonArray())), RevitCommandCodes.ArgsInvalid, "args.layers");
        Rejects(what + " with 33 layers", Envelope(command, With(sample(), "layers", Layers(Enumerable.Range(0, 33).Select(_ => Layer("structure", "Wood", 10)).ToArray()))), RevitCommandCodes.ArgsInvalid, "args.layers");
        Rejects(what + " unknown layer function", Envelope(command, With(sample(), "layers", Layers(Layer("core", "Wood", 10)))), RevitCommandCodes.ArgsInvalid, "args.layers[0].function");
        Rejects(what + " membrane with a thickness", Envelope(command, With(sample(), "layers", Layers(Layer("structure", "Wood", 100), Layer("membrane", "Membrane", 5)))), RevitCommandCodes.LengthInvalid, "args.layers[1].thickness_mm");
        Rejects(what + " layer thinner than Revit's minimum (0.1 mm)", Envelope(command, With(sample(), "layers", Layers(Layer("structure", "Wood", 100), Layer("finish2", "Paint", 0.1)))), RevitCommandCodes.LengthInvalid, "args.layers[1].thickness_mm");
        Rejects(what + " structure layer of 1 mm (under the minimum)", Envelope(command, With(sample(), "layers", Layers(Layer("structure", "Wood", 1)))), RevitCommandCodes.LengthInvalid, "args.layers[0].thickness_mm");
        Accepts(what + " layer at the minimum thickness accepted", Envelope(command, With(sample(), "layers", Layers(Layer("structure", "Wood", 100), Layer("finish2", "Paint", RevitTypeRules.MinLayerThicknessMm)))));
        Rejects(what + " structure layer of zero thickness", Envelope(command, With(sample(), "layers", Layers(Layer("finish1", "Tile", 20), Layer("structure", "Wood", 0)))), RevitCommandCodes.LengthInvalid, "args.layers[1].thickness_mm");
        Rejects(what + " negative layer thickness", Envelope(command, With(sample(), "layers", Layers(Layer("structure", "Wood", -10)))), RevitCommandCodes.LengthInvalid, "args.layers[0].thickness_mm");
        Rejects(what + " layer thicker than the Revit limit", Envelope(command, With(sample(), "layers", Layers(Layer("structure", "Wood", 9_144_001)))), RevitCommandCodes.LengthInvalid, "args.layers[0].thickness_mm");
        RejectsText(what + " layer thickness overflowing to infinity", Envelope(command, With(sample(), "layers", Layers(Layer("structure", "Wood", 150)))).ToJsonString().Replace("\"thickness_mm\":150", "\"thickness_mm\":1e400"), RevitCommandCodes.LengthInvalid, "args.layers[0].thickness_mm");
        Rejects(what + " only membrane layers", Envelope(command, With(sample(), "layers", Layers(Layer("membrane", "Membrane", 0)))), RevitCommandCodes.ArgsInvalid, "args.layers");
        Rejects(what + " without a structure layer", Envelope(command, With(sample(), "layers", Layers(Layer("finish1", "Stone", 30), Layer("insulation", "Insulation", 50)))), RevitCommandCodes.ArgsInvalid, "args.layers");
        Rejects(what + " membrane between structure layers", Envelope(command, With(sample(), "layers", Layers(Layer("structure", "Brick", 110), Layer("membrane", "Membrane", 0), Layer("structure", "Block", 140)))), RevitCommandCodes.ArgsInvalid, "args.layers[1]");
        Rejects(what + " layer without material", Envelope(command, With(sample(), "layers", Layers(new JsonObject { ["function"] = "structure", ["thickness_mm"] = 150 }))), RevitCommandCodes.ArgsInvalid, "args.layers[0].material");
        Rejects(what + " layer thickness as string", Envelope(command, With(sample(), "layers", Layers(new JsonObject { ["function"] = "structure", ["material"] = "Wood", ["thickness_mm"] = "150" }))), RevitCommandCodes.ArgsInvalid, "args.layers[0].thickness_mm");
        Rejects(what + " layer with unexpected member", Envelope(command, With(sample(), "layers", Layers(With(Layer("structure", "Wood", 150), "wraps", true)))), RevitCommandCodes.ArgsInvalid, "args.layers[0].wraps");
        Rejects(what + " name with a colon", Envelope(command, With(sample(), "name", "Cabin: wall")), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects(what + " name with a leading space", Envelope(command, With(sample(), "name", " Cabin")), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects(what + " with unexpected member", Envelope(command, With(sample(), "function", "exterior")), RevitCommandCodes.ArgsInvalid, "args.function");
    }

    static void TypeSpecifics()
    {
        var wall = Accepts("cabin wall type accepted", Envelope("wall_type.create", Wall()));
        Check(wall?.Args is WallTypeCreateArgs { Name: WallName, BaseType: "Generic - 200mm", Layers.Count: 5 } w
            && w.Layers[0] is { Function: "finish1", Material: StoneName } s && Near(s.Thickness.Millimetres, 30)
            && w.Layers[1] is { Function: "membrane" } m && m.Thickness.Millimetres == 0
            && w.Layers[3] is { Function: "structure", Material: "Concrete Masonry Units" } b && Near(b.Thickness.Millimetres, 190)
            && w.Layers[4] is { Function: "finish2", Material: "Plaster" } p && Near(p.Thickness.Millimetres, 15),
            "wall_type.create binds name, base type and the layers exterior to interior");
        var floor = Accepts("cabin floor type accepted", Envelope("floor_type.create", Floor()));
        Check(floor?.Args is FloorTypeCreateArgs { Name: FloorName, BaseType: "Generic 150mm", Layers.Count: 3 } f && f.Layers[2] is { Function: "structure" } slab && Near(slab.Thickness.Millimetres, 150),
            "floor_type.create binds name, base type and the layers top to bottom");
        Check(RevitTypeRules.MinLayerThicknessMm == 1.2 && RevitTypeRules.MinLayerThicknessMm > 304.8 / 256, "the minimum layer thickness is 1.2 mm, above Revit's 1/256 ft (1.190625 mm) minimum");
        Rejects("roof type layer thinner than Revit's minimum (0.1 mm)", Envelope("roof_type.create", new JsonObject { ["name"] = "Cabin roof", ["base_type"] = "Basic Roof - Generic 300mm", ["layers"] = Layers(Layer("finish1", "Tile", 0.1), Layer("structure", "Wood", 150)) }), RevitCommandCodes.LengthInvalid, "args.layers[0].thickness_mm");
        var cmd = Envelope("wall_type.create", Wall());
        cmd["command"] = "floor_type.create";
        Check(Accepts("a wall request body is a valid floor request body", cmd)?.Args is FloorTypeCreateArgs, "wall and floor type commands share one argument shape but bind to distinct types");
    }

    static void ClientPath()
    {
        var policy = new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" });
        var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
        foreach (var (command, args) in new (string, JsonObject)[] { ("material.create", Stone()), ("wall_type.create", Wall()), ("floor_type.create", Floor()) })
        {
            var forwarded = new List<string>();
            string? Transport(string pipe, string line) { forwarded.Add(line); return null; }
            RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args).ToJsonString(), policy, Transport, noLinks);
            Check(forwarded.Count == 1 && forwarded[0] == RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(Envelope(command, args).ToJsonString())), command + " on an enabled copy is forwarded as the canonical wire line");
            var refused = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args, RealPath).ToJsonString(), policy, Transport, noLinks);
            Check(refused.IsError && !refused.Forwarded && JsonNode.Parse(refused.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentNotEnabled && forwarded.Count == 1, command + " on a real project is document_not_enabled and never forwarded", refused.Line);
        }
    }

    static void Rules()
    {
        Check(RevitTypeRules.CoreRange(new[] { "finish1", "membrane", "insulation", "structure", "finish2" }, "args.layers") == (3, 3), "the core is the run of structure layers: one structure layer");
        Check(RevitTypeRules.CoreRange(new[] { "structure", "insulation", "structure", "finish2" }, "args.layers") == (0, 2), "the core spans from the first to the last structure layer");
        Check(Thrown(() => RevitTypeRules.CoreRange(new[] { "finish1", "insulation" }, "args.layers")) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.layers" } none && none.Detail!.Contains("structure", StringComparison.Ordinal), "no structure layer is refused");
        Check(Thrown(() => RevitTypeRules.CoreRange(new[] { "structure", "finish2", "membrane", "structure" }, "args.layers")) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.layers[2]" }, "a membrane inside the core is refused at its index");
        Check(CommandRulesTypes.PickExisting(new[] { "stone", "Stone" }, "Stone") == 1 && CommandRulesTypes.PickExisting(new[] { "stone", "Stone" }, "stone") == 0, "an existing name is picked by ordinal match first, even when another differs only in case");
        Check(CommandRulesTypes.PickExisting(new[] { "Brick", "stone" }, "STONE") == 1, "without an ordinal match the case-insensitive match is picked");
        Check(CommandRulesTypes.PickExisting(new[] { "Brick", "stone" }, "Slate") is null && CommandRulesTypes.PickExisting(Array.Empty<string>(), "Slate") is null, "no match picks nothing (a new element is created)");
        var walls = new[] { ("Cabin wall", "Curtain"), ("cabin wall", "Basic") };
        Check(CommandRulesTypes.PickExisting(walls.Select(wall => wall.Item1).ToArray(), "cabin wall") is { } basic && walls[basic].Item2 == "Basic", "a basic \"cabin wall\" is picked by ordinal match over a curtain \"Cabin wall\"");
        Check(CommandRulesTypes.PickExisting(walls.Select(wall => wall.Item1).ToArray(), "Cabin wall") is { } curtainPick && CommandRulesTypes.WallKindProblem(walls[curtainPick].Item2) is not null, "requesting \"Cabin wall\" picks the curtain type, which is then refused");
        Check(CommandRulesTypes.WallKindProblem("Basic") is null, "a basic wall type can be duplicated with layers");
        Check(CommandRulesTypes.WallKindProblem("Curtain") is { } curtain && curtain.Contains("curtain", StringComparison.Ordinal), "a curtain wall type is refused, naming curtain");
        Check(CommandRulesTypes.WallKindProblem("Stacked") is { } stacked && stacked.Contains("stacked", StringComparison.Ordinal), "a stacked wall type is refused, naming stacked");
        Check(CommandRulesTypes.WallKindProblem("Unknown") is not null && CommandRulesTypes.WallKindProblem("SomethingNew") is not null, "any other wall kind fails closed");
    }

    static IReadOnlyList<ExpectedTypeLayer> Expected() => new[]
    {
        new ExpectedTypeLayer("finish1", StoneUid, 30), new ExpectedTypeLayer("membrane", InsulationUid, 0), new ExpectedTypeLayer("structure", BlockUid, 190), new ExpectedTypeLayer("finish2", PlasterUid, 15),
    };
    static ObservedLayeredType Observed(string name = WallName) => new(TypeUid, name, new[]
    {
        new ObservedTypeLayer("finish1", StoneUid, StoneName, 30), new ObservedTypeLayer("membrane", InsulationUid, "Membrane", 0),
        new ObservedTypeLayer("structure", BlockUid, "Concrete Masonry Units", 190.00004), new ObservedTypeLayer("finish2", PlasterUid, "Plaster", 15),
    }, 235, 2, 1, 2);
    static ObservedLayeredType Replace(int index, ObservedTypeLayer layer)
    {
        var observed = Observed();
        var layers = observed.Layers!.ToArray();
        layers[index] = layer;
        return observed with { Layers = layers };
    }

    static void LayeredReadbacks()
    {
        var observed = Observed();
        Check(CommandReadbackTypes.LayeredType("wall type", WallName, Expected(), observed) is null, "layered type readback matches (thickness within 1e-3 mm)");
        Check(CommandReadbackTypes.LayeredType("floor type", WallName, Expected(), observed with { Name = WallName.ToUpperInvariant() }) is { } name && name.Contains("floor type name", StringComparison.Ordinal), "another name (even only in case) is a mismatch naming the kind of type");
        Check(CommandReadbackTypes.LayeredType("wall type", WallName, Expected(), observed with { Layers = null }) is { } none && none.Contains("no layers", StringComparison.Ordinal), "a type without a compound structure is a mismatch");
        Check(CommandReadbackTypes.LayeredType("wall type", WallName, Expected(), observed with { Layers = observed.Layers!.Take(3).ToArray() }) is { } count && count.Contains("3 layers", StringComparison.Ordinal), "another layer count is a mismatch");
        Check(CommandReadbackTypes.LayeredType("wall type", WallName, Expected(), Replace(0, new ObservedTypeLayer("finish2", StoneUid, StoneName, 30))) is { } function && function.Contains("layer 0", StringComparison.Ordinal) && function.Contains("function", StringComparison.Ordinal), "a layer with another function is a mismatch naming the layer");
        Check(CommandReadbackTypes.LayeredType("wall type", WallName, Expected(), Replace(2, new ObservedTypeLayer("structure", PlasterUid, "Plaster", 190))) is { } material && material.Contains("material", StringComparison.Ordinal), "a layer of another material is a mismatch");
        Check(CommandReadbackTypes.LayeredType("wall type", WallName, Expected(), Replace(2, new ObservedTypeLayer("structure", BlockUid.ToUpperInvariant(), "Concrete Masonry Units", 190))) is null, "material UniqueIds compare ignoring hex case");
        Check(CommandReadbackTypes.LayeredType("wall type", WallName, Expected(), Replace(2, new ObservedTypeLayer("structure", null, null, 190))) is not null, "a layer without a material is a mismatch");
        Check(CommandReadbackTypes.LayeredType("wall type", WallName, Expected(), Replace(3, new ObservedTypeLayer("finish2", PlasterUid, "Plaster", 15.01))) is { } thickness && thickness.Contains("thickness", StringComparison.Ordinal), "a layer of another thickness is a mismatch");
        Check(CommandReadbackTypes.LayeredType("wall type", WallName, Expected(), observed with { ExteriorShellLayers = 0, InteriorShellLayers = 0 }) is { } core && core.Contains("core", StringComparison.Ordinal), "the same layers with every layer in the core (shell counts 0/0) are a mismatch naming the core");
        Check(CommandReadbackTypes.LayeredType("wall type", WallName, Expected(), observed with { InteriorShellLayers = 0 }) is { } interior && interior.Contains("core", StringComparison.Ordinal), "another interior shell count is a core mismatch");
        Check(CommandReadbackTypes.LayeredType("wall type", WallName, Expected(), observed with { ExteriorShellLayers = null, InteriorShellLayers = null }) is not null, "unknown shell counts are a mismatch");
        Check(CommandReadbackTypes.LayeredType("wall type", WallName, Expected(), observed with { StructuralMaterialIndex = 3 }) is { } structural && structural.Contains("structural material", StringComparison.Ordinal), "another structural material layer is a mismatch");
        Check(CommandReadbackTypes.LayeredType("wall type", WallName, Expected().Reverse().ToArray(), observed) is not null, "the requested order is the order read back (exterior to interior)");
    }

    static ObservedMaterial Slate() => new(MaterialUid, StoneName, new RgbColor(96, 92, 88), 0, 20, 40, "Stone", "Stone", AssetUid, "Stone - Slate");
    static MaterialCreateArgs StoneArgs() => new(StoneName, new RgbColor(96, 92, 88), 0, 20, 40, "Stone", "Stone - Slate");

    static void MaterialReadbacks()
    {
        var args = StoneArgs();
        Check(CommandReadbackTypes.Material(args, AssetUid, Slate()) is null, "material readback matches every requested property");
        Check(CommandReadbackTypes.Material(args, AssetUid.ToUpperInvariant(), Slate()) is null, "appearance asset UniqueIds compare ignoring hex case");
        var bare = new MaterialCreateArgs(StoneName, null, null, null, null, null, null);
        Check(CommandReadbackTypes.Material(bare, null, Slate() with { Color = null, MaterialClass = null, AppearanceAssetUid = null, AppearanceAssetName = null, Transparency = 37 }) is null, "properties that were not requested are not checked");
        Check(CommandReadbackTypes.Material(args, AssetUid, Slate() with { Name = StoneName.ToLowerInvariant() }) is { } name && name.Contains("name", StringComparison.Ordinal), "a material of another name (even only in case) is a mismatch");
        Check(CommandReadbackTypes.Material(args, AssetUid, Slate() with { Color = new RgbColor(96, 92, 89) }) is { } color && color.Contains("color", StringComparison.Ordinal), "another colour is a mismatch");
        Check(CommandReadbackTypes.Material(args, AssetUid, Slate() with { Color = null }) is not null, "a material without a valid colour is a mismatch when a colour was requested");
        Check(CommandReadbackTypes.Material(args, AssetUid, Slate() with { Transparency = 1 }) is { } transparency && transparency.Contains("transparency", StringComparison.Ordinal), "another transparency is a mismatch");
        Check(CommandReadbackTypes.Material(args, AssetUid, Slate() with { Shininess = 21 }) is { } shininess && shininess.Contains("shininess", StringComparison.Ordinal), "another shininess is a mismatch");
        Check(CommandReadbackTypes.Material(args, AssetUid, Slate() with { Smoothness = 41 }) is { } smoothness && smoothness.Contains("smoothness", StringComparison.Ordinal), "another smoothness is a mismatch");
        Check(CommandReadbackTypes.Material(args, AssetUid, Slate() with { MaterialClass = "stone" }) is { } materialClass && materialClass.Contains("class", StringComparison.Ordinal), "another material class (ordinal) is a mismatch");
        Check(CommandReadbackTypes.Material(args, AssetUid, Slate() with { MaterialClass = null }) is not null, "an empty material class is a mismatch when a class was requested");
        Check(CommandReadbackTypes.Material(args, AssetUid, Slate() with { AppearanceAssetUid = MaterialUid }) is { } asset && asset.Contains("appearance asset", StringComparison.Ordinal), "another appearance asset is a mismatch");
        Check(CommandReadbackTypes.Material(args, AssetUid, Slate() with { AppearanceAssetUid = null }) is not null, "no appearance asset is a mismatch when one was requested");
    }

    static void ReadbackJson()
    {
        var created = JsonNode.Parse(CommandReadbackTypes.LayeredTypeJson("wall_type", Observed(), existing: false))!["wall_type"]!;
        Check(created["unique_id"]!.GetValue<string>() == TypeUid && created["name"]!.GetValue<string>() == WallName && created["existing"]!.GetValue<bool>() == false && created["width_mm"]!.GetValue<double>() == 235,
            "wall type readback JSON reports UniqueId, name, whether it already existed and the total width", created.ToJsonString());
        var layers = created["layers"]!.AsArray();
        Check(layers.Count == 4 && layers[0]!["function"]!.GetValue<string>() == "finish1" && layers[0]!["material_unique_id"]!.GetValue<string>() == StoneUid && layers[0]!["material_name"]!.GetValue<string>() == StoneName
            && layers[1]!["thickness_mm"]!.GetValue<double>() == 0 && layers[2]!["function"]!.GetValue<string>() == "structure", "layered type readback JSON lists the layers in order with function, material and thickness", created.ToJsonString());
        Check(created["core"]!["first"]!.GetValue<int>() == 2 && created["core"]!["last"]!.GetValue<int>() == 2 && created["structural_material_index"]!.GetValue<int>() == 2, "layered type readback JSON reports the core (first and last core layer, from the shell counts) and the structural material layer", created.ToJsonString());
        Check(JsonNode.Parse(CommandReadbackTypes.LayeredTypeJson("floor_type", Observed(FloorName), existing: true))!["floor_type"]!["existing"]!.GetValue<bool>(), "an identical existing floor type is reported under floor_type with existing true");
        var bareType = JsonNode.Parse(CommandReadbackTypes.LayeredTypeJson("floor_type", Observed() with { Layers = null, WidthMm = null, ExteriorShellLayers = null, InteriorShellLayers = null, StructuralMaterialIndex = null }, existing: false))!["floor_type"]!;
        Check(bareType["layers"]!.AsArray().Count == 0 && bareType["width_mm"] is null && bareType["core"] is null, "a type without layers reads back an empty layer list and null width", bareType.ToJsonString());

        var material = JsonNode.Parse(CommandReadbackTypes.MaterialJson(Slate(), existing: false))!["material"]!;
        Check(material["unique_id"]!.GetValue<string>() == MaterialUid && material["name"]!.GetValue<string>() == StoneName && material["existing"]!.GetValue<bool>() == false
            && material["color"]!["r"]!.GetValue<int>() == 96 && material["color"]!["g"]!.GetValue<int>() == 92 && material["color"]!["b"]!.GetValue<int>() == 88
            && material["transparency"]!.GetValue<int>() == 0 && material["shininess"]!.GetValue<int>() == 20 && material["smoothness"]!.GetValue<int>() == 40
            && material["class"]!.GetValue<string>() == "Stone" && material["category"]!.GetValue<string>() == "Stone"
            && material["appearance_asset"]!["unique_id"]!.GetValue<string>() == AssetUid && material["appearance_asset"]!["name"]!.GetValue<string>() == "Stone - Slate",
            "material readback JSON reports UniqueId, name, existing, colour, transparency, shininess, smoothness, class, category and appearance asset", material.ToJsonString());
        var plain = JsonNode.Parse(CommandReadbackTypes.MaterialJson(Slate() with { Color = null, MaterialClass = null, MaterialCategory = null, AppearanceAssetUid = null, AppearanceAssetName = null }, existing: true))!["material"]!;
        var unnamed = JsonNode.Parse(CommandReadbackTypes.MaterialJson(Slate() with { AppearanceAssetName = null }, existing: false))!["material"]!;
        Check(unnamed["appearance_asset"]!["unique_id"]!.GetValue<string>() == AssetUid && unnamed["appearance_asset"]!["name"] is null, "an appearance asset without a name reads back its UniqueId and a null name", unnamed.ToJsonString());
        Check(plain["existing"]!.GetValue<bool>() && plain["color"] is null && plain["class"] is null && plain["category"] is null && plain["appearance_asset"] is null, "absent colour, class, category and appearance asset read back as null", plain.ToJsonString());
    }
}
