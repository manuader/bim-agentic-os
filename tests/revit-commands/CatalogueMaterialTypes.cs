using Bim.Core;
using Bim.Revit;
using System.Text.Json;
using System.Text.Json.Nodes;
// Materials, layered wall/floor types and type changes contract oracle (chat-materials-types): query.fill_patterns,
// material.create, material.set, wall_type.create, floor_type.create and element.change_type. Covers the schemas, the
// typed binders, the guarded client path (fake transport) and the Autodesk-free executor rules, readback checks and
// readback JSON in runtimes/revit-native/CommandsMaterialTypes.cs. No Revit is involved: a passing run is unit-tested
// evidence only; CommandsExecutorMaterialTypes.cs is compiled, never run.
static class CatalogueMaterialTypesChecks
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string MaterialUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000b01";
    const string StoneUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000b02";
    const string WoodUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000b03";
    const string AirUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000b04";
    const string PatternUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000c01";
    const string CutPatternUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000c02";
    const string AssetUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000d01";
    const string TypeUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000e01";
    const string OtherTypeUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000e02";
    const string WallUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0002a001";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";
    const string WallName = "Muro piedra 600 + revestimiento lenga";

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static JsonObject Envelope(string command, JsonNode args, string path = FixturePath) => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-material-types",
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
        ["command"] = command,
        ["args"] = args.DeepClone(),
    };
    static JsonArray Rgb(params int[] channels) => new(channels.Select(channel => (JsonNode?)channel).ToArray());
    static JsonObject ChapaNegra() => new() { ["name"] = "Chapa negra", ["color"] = Rgb(45, 47, 50), ["base_material"] = "Metal Deck", ["shininess"] = 40, ["smoothness"] = 60, ["material_class"] = "Metal" };
    static JsonObject Piedra() => new() { ["name"] = "Piedra local", ["color"] = Rgb(128, 124, 116), ["material_class"] = "Masonry", ["surface_pattern"] = "Masonry - Stone", ["cut_pattern"] = "Masonry - Stone" };
    static JsonObject Layer(string function, string material, double mm) => new() { ["function"] = function, ["material"] = material, ["thickness_mm"] = mm };
    static JsonArray Layers(params JsonObject[] layers) => new(layers.Select(layer => (JsonNode?)layer).ToArray());
    // Exterior to interior: carved stone (the core), a ventilated batten gap, the lenga lining.
    static JsonArray StoneLayers() => Layers(Layer("structure", "Piedra local", 550), Layer("thermal_air", "Air", 25), Layer("finish2", "Madera lenga", 25));
    static JsonObject StoneWall() => new() { ["name"] = WallName, ["base_type"] = "Generic - 200mm", ["layers"] = StoneLayers() };
    // Top to bottom: lenga boards, membrane, concrete plinth slab.
    static JsonObject WoodFloor() => new() { ["name"] = "Piso lenga sobre platea", ["base_type"] = "Generic 150mm", ["layers"] = Layers(Layer("finish1", "Madera lenga", 22), Layer("membrane", "Vapor Retarder", 0), Layer("structure", "Concrete, Cast-in-Place gray", 150)) };
    static JsonObject With(JsonObject args, string member, JsonNode? value) { if (value is null) args.Remove(member); else args[member] = value; return args; }
    static bool Near(double a, double b, double tolerance = 1e-9) => Math.Abs(a - b) <= tolerance;

    static RevitCommandException? Refusal(string json)
    {
        try { RevitCommandProtocol.Parse(json); return null; }
        catch (RevitCommandException error) { return error; }
        catch (Exception error) { return new RevitCommandException("unexpected " + error.GetType().Name, null, error.Message); }
    }
    static void Rejects(string name, JsonNode envelope, string code, string? field = null)
    {
        var error = Refusal(envelope.ToJsonString());
        Check(error is not null && error.Code == code && (field is null || error.Field == field), name, error is null ? "accepted" : error.Code + " at " + error.Field + " (" + error.Detail + ")");
    }
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
        TypeBinding();
        ChangeTypeBinding();
        ClientPath();
        Rules();
        MaterialReadback();
        HostTypeReadback();
        ChangeTypeReadback();
        ReadbackJson();
    }

    static readonly string[] Names = { "query.fill_patterns", "material.create", "material.set", "wall_type.create", "floor_type.create", "element.change_type" };

    static void Catalogue()
    {
        var names = RevitCommandCatalogue.MaterialTypes.Select(spec => spec.Name).ToArray();
        Check(names.SequenceEqual(Names), "material type catalogue lists query.fill_patterns, material.create, material.set, wall_type.create, floor_type.create, element.change_type", string.Join(",", names));
        var earlier = RevitCommandCatalogue.V1.Concat(RevitCommandCatalogue.V2).Concat(RevitCommandCatalogue.Views).Concat(RevitCommandCatalogue.Mirror).Concat(RevitCommandCatalogue.Elements)
            .Concat(RevitCommandCatalogue.Structure).Concat(RevitCommandCatalogue.Documentation).Concat(RevitCommandCatalogue.Modify).Concat(RevitCommandCatalogue.Materials)
            .Concat(RevitCommandCatalogue.Families).Concat(RevitCommandCatalogue.Site).Concat(RevitCommandCatalogue.WallProfile).ToArray();
        Check(RevitCommandCatalogue.All.Take(earlier.Length).SequenceEqual(earlier) && RevitCommandCatalogue.All.Skip(earlier.Length).SequenceEqual(RevitCommandCatalogue.MaterialTypes),
            "All is every earlier part through wall.set_profile, then the material type commands");
        Check(RevitCommandCatalogue.All.Select(spec => spec.Name).Distinct().Count() == RevitCommandCatalogue.All.Count && RevitCommandCatalogue.All.Select(spec => spec.ToolName).Distinct().Count() == RevitCommandCatalogue.All.Count, "command and tool names stay distinct with the material type commands");
        Check(RevitCommandCatalogue.MaterialTypes.Select(spec => spec.ToolName).SequenceEqual(new[] { "revit_query_fill_patterns", "revit_material_create", "revit_material_set", "revit_wall_type_create", "revit_floor_type_create", "revit_element_change_type" }), "material type tool names follow revit_<command>");
        Check(RevitCommandCatalogue.MaterialTypes.Where(spec => !spec.Writes).Select(spec => spec.Name).SequenceEqual(new[] { "query.fill_patterns" }), "only query.fill_patterns is read-only");
        foreach (var name in Names) Check(ReferenceEquals(RevitCommandCatalogue.Find(name), RevitCommandCatalogue.MaterialTypes.Single(spec => spec.Name == name)), "Find resolves material type command " + name);
        Check(RevitCommandCatalogue.IsDestructive("material.set") && RevitCommandCatalogue.IsDestructive("element.change_type"), "material.set and element.change_type change existing elements: destructive");
        Check(!RevitCommandCatalogue.IsDestructive("material.create") && !RevitCommandCatalogue.IsDestructive("wall_type.create") && !RevitCommandCatalogue.IsDestructive("floor_type.create") && !RevitCommandCatalogue.IsDestructive("query.fill_patterns"), "material.create, wall_type.create and floor_type.create only add: not destructive");
        Check(RevitCommandCatalogue.MaterialTypes.All(spec => spec.Description.Length > 20 && spec.Description.EndsWith('.') && (!spec.Writes || spec.Description.Contains("enabled test copies", StringComparison.Ordinal))), "every material type command has a plain-English description; writes state the copies-only rule");
        Check(RevitCommandCatalogue.Find("wall_type.create")!.Description.Contains("exterior to interior", StringComparison.Ordinal) && RevitCommandCatalogue.Find("floor_type.create")!.Description.Contains("top to bottom", StringComparison.Ordinal)
            && RevitCommandCatalogue.Find("wall_type.create")!.Description.Contains("thermal_air", StringComparison.Ordinal), "wall and floor type descriptions state the layer order and the thermal_air alias");
        Check(RevitCommandCatalogue.Find("material.create")!.Description.Contains("base_material", StringComparison.Ordinal) && RevitCommandCatalogue.Find("material.create")!.Description.Contains("appearance", StringComparison.Ordinal)
            && RevitCommandCatalogue.Find("material.create")!.Description.Contains("shaded", StringComparison.Ordinal), "material.create states that a base material keeps its render appearance and the color is the shaded color");
        foreach (var spec in RevitCommandCatalogue.MaterialTypes)
        {
            string? problem = null;
            try { using var schema = JsonDocument.Parse(spec.ArgsSchemaJson); RevitCommandSchema.CheckSupported(schema.RootElement); if (schema.RootElement.GetProperty("type").GetString() != "object") problem = "not an object schema"; }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { problem = error.Message; }
            Check(problem is null, spec.Name + " args schema is valid JSON using only enforced keywords", problem ?? "");
        }
        var copies = new[] { @"C:\Repo\.bim\native-runs" };
        foreach (var spec in RevitCommandCatalogue.MaterialTypes)
            Check(PermissionPolicy.RequiredLevel(spec.Name, FixturePath, copies) == (spec.Writes ? PermissionLevel.WriteCopies : PermissionLevel.Read)
                && PermissionPolicy.RequiredLevel(spec.Name, RealPath, copies) == (spec.Writes ? PermissionLevel.WriteRealProject : PermissionLevel.Read), "permission level of " + spec.Name + " follows its catalogue entry");
    }

    static void MaterialBinding()
    {
        var chapa = Accepts("dark metal duplicated from Metal Deck accepted", Envelope("material.create", ChapaNegra()));
        Check(chapa?.Args is MaterialCreateArgs { Name: "Chapa negra", BaseMaterial: "Metal Deck", Shininess: 40, Smoothness: 60, MaterialClass: "Metal", Transparency: null, SurfacePattern: null, CutPattern: null } args && args.Color == new RgbColor(45, 47, 50),
            "material.create binds name, color, base material, shininess, smoothness and class; omitted members are null");
        var piedra = Accepts("stone with surface and cut patterns accepted", Envelope("material.create", Piedra()));
        Check(piedra?.Args is MaterialCreateArgs { BaseMaterial: null, SurfacePattern: "Masonry - Stone", CutPattern: "Masonry - Stone" } stone && stone.Color == new RgbColor(128, 124, 116), "material.create binds the surface and cut fill patterns");
        Accepts("transparency 0 and 100 accepted", Envelope("material.create", With(With(Piedra(), "transparency", 100), "smoothness", 0)));
        Accepts("color bounds 0 and 255 accepted", Envelope("material.create", With(Piedra(), "color", Rgb(0, 255, 0))));
        Accepts("shininess 128 accepted", Envelope("material.create", With(Piedra(), "shininess", 128)));
        Accepts("pattern by UniqueId accepted", Envelope("material.create", With(Piedra(), "surface_pattern", PatternUid)));

        Rejects("material without name", Envelope("material.create", With(Piedra(), "name", null)), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("material without color", Envelope("material.create", With(Piedra(), "color", null)), RevitCommandCodes.ArgsInvalid, "args.color");
        Rejects("material name with a colon", Envelope("material.create", With(Piedra(), "name", "Piedra: local")), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("material name with a trailing space", Envelope("material.create", With(Piedra(), "name", "Piedra ")), RevitCommandCodes.ArgsInvalid, "args.name");
        Rejects("color channel above 255", Envelope("material.create", With(Piedra(), "color", Rgb(256, 0, 0))), RevitCommandCodes.ArgsInvalid, "args.color[0]");
        Rejects("negative color channel", Envelope("material.create", With(Piedra(), "color", Rgb(0, 0, -1))), RevitCommandCodes.ArgsInvalid, "args.color[2]");
        Rejects("fractional color channel", Envelope("material.create", With(Piedra(), "color", new JsonArray(1.5, 0, 0))), RevitCommandCodes.ArgsInvalid, "args.color[0]");
        Rejects("color with two channels", Envelope("material.create", With(Piedra(), "color", Rgb(1, 2))), RevitCommandCodes.ArgsInvalid, "args.color");
        Rejects("color with four channels", Envelope("material.create", With(Piedra(), "color", Rgb(1, 2, 3, 4))), RevitCommandCodes.ArgsInvalid, "args.color");
        Rejects("transparency above 100", Envelope("material.create", With(Piedra(), "transparency", 101)), RevitCommandCodes.ArgsInvalid, "args.transparency");
        Rejects("negative transparency", Envelope("material.create", With(Piedra(), "transparency", -1)), RevitCommandCodes.ArgsInvalid, "args.transparency");
        Rejects("huge transparency", Envelope("material.create", With(Piedra(), "transparency", 1e12)), RevitCommandCodes.ArgsInvalid, "args.transparency");
        Rejects("shininess above 128", Envelope("material.create", With(Piedra(), "shininess", 129)), RevitCommandCodes.ArgsInvalid, "args.shininess");
        Rejects("smoothness above 100", Envelope("material.create", With(Piedra(), "smoothness", 101)), RevitCommandCodes.ArgsInvalid, "args.smoothness");
        Rejects("empty material class", Envelope("material.create", With(Piedra(), "material_class", "")), RevitCommandCodes.ArgsInvalid, "args.material_class");
        Rejects("material texture member is not offered", Envelope("material.create", With(Piedra(), "texture", "stone.jpg")), RevitCommandCodes.ArgsInvalid, "args.texture");

        var darken = Accepts("material.set with a color accepted", Envelope("material.set", new JsonObject { ["material"] = "Chapa negra", ["color"] = Rgb(30, 31, 33) }));
        Check(darken?.Args is MaterialSetArgs { Material: "Chapa negra", Transparency: null, SurfacePattern: null, CutPattern: null, Shininess: null, Smoothness: null } set && set.Color == new RgbColor(30, 31, 33), "material.set binds the material and only the requested change");
        var pattern = Accepts("material.set with only a surface pattern accepted", Envelope("material.set", new JsonObject { ["material"] = MaterialUid, ["surface_pattern"] = "Wood 1", ["transparency"] = 0 }));
        Check(pattern?.Args is MaterialSetArgs { Color: null, SurfacePattern: "Wood 1", Transparency: 0 }, "material.set binds a surface pattern and transparency without a color");
        Rejects("material.set without a change", Envelope("material.set", new JsonObject { ["material"] = "Chapa negra" }), RevitCommandCodes.ArgsInvalid, "args");
        Rejects("material.set without material", Envelope("material.set", new JsonObject { ["color"] = Rgb(1, 2, 3) }), RevitCommandCodes.ArgsInvalid, "args.material");
        Rejects("material.set color above 255", Envelope("material.set", new JsonObject { ["material"] = "Chapa negra", ["color"] = Rgb(1, 2, 300) }), RevitCommandCodes.ArgsInvalid, "args.color[2]");
        Rejects("material.set cannot rename", Envelope("material.set", new JsonObject { ["material"] = "Chapa negra", ["name"] = "Chapa" }), RevitCommandCodes.ArgsInvalid, "args.name");

        Check(Accepts("query.fill_patterns with {} accepted", Envelope("query.fill_patterns", new JsonObject()))?.Args is FillPatternQueryArgs { Target: "all" }, "query.fill_patterns defaults to all targets");
        Check(Accepts("query.fill_patterns model accepted", Envelope("query.fill_patterns", new JsonObject { ["target"] = "model" }))?.Args is FillPatternQueryArgs { Target: "model" }, "query.fill_patterns binds the model target");
        Check(Accepts("query.fill_patterns drafting accepted", Envelope("query.fill_patterns", new JsonObject { ["target"] = "drafting" }))?.Args is FillPatternQueryArgs { Target: "drafting" }, "query.fill_patterns binds the drafting target");
        Rejects("query.fill_patterns unknown target", Envelope("query.fill_patterns", new JsonObject { ["target"] = "cut" }), RevitCommandCodes.ArgsInvalid, "args.target");
        Rejects("query.fill_patterns unexpected member", Envelope("query.fill_patterns", new JsonObject { ["name"] = "Stone" }), RevitCommandCodes.ArgsInvalid, "args.name");
        Check(RevitMaterialTypeRules.PatternTargets.SequenceEqual(new[] { "model", "drafting", "all" }), "fill pattern targets are model, drafting and all");
    }

    static void TypeBinding()
    {
        var wall = Accepts("stone wall with lenga lining accepted", Envelope("wall_type.create", StoneWall()));
        Check(wall?.Args is WallTypeCreateArgs { Name: WallName, BaseType: "Generic - 200mm", Layers.Count: 3 } w
            && w.Layers[0] is { Function: "structure", Material: "Piedra local" } stone && Near(stone.Thickness.Millimetres, 550)
            && w.Layers[1] is { Function: "thermal_air" } air && Near(air.Thickness.Millimetres, 25)
            && w.Layers[2] is { Function: "finish2", Material: "Madera lenga" } wood && Near(wood.Thickness.Millimetres, 25),
            "wall_type.create binds name, base type and the layers exterior to interior in millimetres");
        var floor = Accepts("lenga floor on a concrete slab accepted", Envelope("floor_type.create", WoodFloor()));
        Check(floor?.Args is FloorTypeCreateArgs { Name: "Piso lenga sobre platea", Layers.Count: 3 } f && f.Layers[1] is { Function: "membrane" } membrane && membrane.Thickness.Millimetres == 0,
            "floor_type.create binds the layers top to bottom with a 0 mm membrane");
        Check(RevitMaterialTypeRules.Functions.SequenceEqual(new[] { "finish1", "finish2", "substrate", "structure", "insulation", "thermal_air", "membrane" }), "layer functions add thermal_air to the roof functions");
        Check(RevitMaterialTypeRules.Canonical("thermal_air") == "insulation" && RevitMaterialTypeRules.Canonical("structure") == "structure", "thermal_air is Revit's insulation (Thermal/Air Layer) function");
        Accepts("32 wall layers accepted", Envelope("wall_type.create", With(StoneWall(), "layers", Layers(Enumerable.Range(0, 32).Select(_ => Layer("structure", "Piedra local", 10)).ToArray()))));
        foreach (var command in new[] { "wall_type.create", "floor_type.create" })
        {
            JsonObject Args() => command == "wall_type.create" ? StoneWall() : WoodFloor();
            Rejects(command + " with 33 layers", Envelope(command, With(Args(), "layers", Layers(Enumerable.Range(0, 33).Select(_ => Layer("structure", "Piedra local", 10)).ToArray()))), RevitCommandCodes.ArgsInvalid, "args.layers");
            Rejects(command + " without base type", Envelope(command, With(Args(), "base_type", null)), RevitCommandCodes.ArgsInvalid, "args.base_type");
            Rejects(command + " without layers", Envelope(command, With(Args(), "layers", new JsonArray())), RevitCommandCodes.ArgsInvalid, "args.layers");
            Rejects(command + " unknown function", Envelope(command, With(Args(), "layers", Layers(Layer("core", "Piedra local", 10)))), RevitCommandCodes.ArgsInvalid, "args.layers[0].function");
            Rejects(command + " without a structure layer", Envelope(command, With(Args(), "layers", Layers(Layer("finish1", "Madera lenga", 25), Layer("insulation", "Air", 50)))), RevitCommandCodes.ArgsInvalid, "args.layers");
            Rejects(command + " membrane between structure layers", Envelope(command, With(Args(), "layers", Layers(Layer("structure", "Piedra local", 200), Layer("membrane", "Vapor Retarder", 0), Layer("structure", "Piedra local", 200)))), RevitCommandCodes.ArgsInvalid, "args.layers[1]");
            Rejects(command + " membrane with thickness", Envelope(command, With(Args(), "layers", Layers(Layer("structure", "Piedra local", 200), Layer("membrane", "Vapor Retarder", 2)))), RevitCommandCodes.LengthInvalid, "args.layers[1].thickness_mm");
            Rejects(command + " zero-thickness structure", Envelope(command, With(Args(), "layers", Layers(Layer("structure", "Piedra local", 0)))), RevitCommandCodes.LengthInvalid, "args.layers[0].thickness_mm");
            Rejects(command + " layer beyond the Revit limit", Envelope(command, With(Args(), "layers", Layers(Layer("structure", "Piedra local", 9_144_001)))), RevitCommandCodes.LengthInvalid, "args.layers[0].thickness_mm");
            Rejects(command + " name with a bracket", Envelope(command, With(Args(), "name", "Muro [piedra]")), RevitCommandCodes.ArgsInvalid, "args.name");
            Rejects(command + " unexpected layer member", Envelope(command, With(Args(), "layers", Layers(With(Layer("structure", "Piedra local", 100), "wraps", true)))), RevitCommandCodes.ArgsInvalid, "args.layers[0].wraps");
        }
        Check(RevitMaterialTypeRules.Shells(new[] { "structure", "thermal_air", "finish2" }, "args.layers") == (0, 2), "a stone core outermost leaves two interior shell layers");
        Check(RevitMaterialTypeRules.Shells(new[] { "finish1", "membrane", "structure" }, "args.layers") == (2, 0), "a finish and a membrane above the slab are two exterior shell layers");
        Check(RevitMaterialTypeRules.Shells(new[] { "finish1", "structure", "insulation", "structure", "finish2" }, "args.layers") == (1, 1), "the core spans from the first to the last structure layer");
        Check(Thrown(() => RevitMaterialTypeRules.Shells(new[] { "finish1" }, "args.layers")) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.layers" }, "no structure layer is args_invalid");
    }

    static void ChangeTypeBinding()
    {
        var change = Accepts("element.change_type accepted", Envelope("element.change_type", new JsonObject { ["element_uid"] = WallUid, ["type"] = WallName }));
        Check(change?.Args is ElementChangeTypeArgs { ElementUid: WallUid, Type: WallName }, "element.change_type binds the element UniqueId and the type reference");
        Accepts("element.change_type to a type UniqueId accepted", Envelope("element.change_type", new JsonObject { ["element_uid"] = WallUid, ["type"] = TypeUid }));
        Rejects("element.change_type with a name as element", Envelope("element.change_type", new JsonObject { ["element_uid"] = "Wall 1", ["type"] = WallName }), RevitCommandCodes.ArgsInvalid, "args.element_uid");
        Rejects("element.change_type without type", Envelope("element.change_type", new JsonObject { ["element_uid"] = WallUid }), RevitCommandCodes.ArgsInvalid, "args.type");
        Rejects("element.change_type with an empty type", Envelope("element.change_type", new JsonObject { ["element_uid"] = WallUid, ["type"] = "" }), RevitCommandCodes.ArgsInvalid, "args.type");
    }

    static void ClientPath()
    {
        var policy = new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" });
        var forwarded = new List<string>();
        string? Transport(string pipe, string line) { forwarded.Add(line); return null; }
        var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
        RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("wall_type.create", StoneWall()).ToJsonString(), policy, Transport, noLinks);
        Check(forwarded.Count == 1 && forwarded[0] == RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(Envelope("wall_type.create", StoneWall()).ToJsonString())), "wall_type.create on an enabled copy is forwarded as the canonical wire line");
        foreach (var (command, args) in new (string, JsonObject)[] { ("material.create", Piedra()), ("material.set", new JsonObject { ["material"] = "Chapa negra", ["color"] = Rgb(1, 2, 3) }), ("element.change_type", new JsonObject { ["element_uid"] = WallUid, ["type"] = WallName }), ("floor_type.create", WoodFloor()) })
        {
            var refused = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args, RealPath).ToJsonString(), policy, Transport, noLinks);
            Check(refused.IsError && !refused.Forwarded && JsonNode.Parse(refused.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentNotEnabled && forwarded.Count == 1, command + " on a real project is document_not_enabled and never forwarded", refused.Line);
        }
        RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("query.fill_patterns", new JsonObject { ["target"] = "model" }, RealPath).ToJsonString(), policy, Transport, noLinks);
        Check(forwarded.Count == 2 && forwarded[1].Contains("query.fill_patterns", StringComparison.Ordinal), "query.fill_patterns is read-only and forwarded on a real project");
    }

    static void Rules()
    {
        foreach (var (function, revit) in new[] { ("finish1", "Finish1"), ("finish2", "Finish2"), ("substrate", "Substrate"), ("structure", "Structure"), ("insulation", "Insulation"), ("thermal_air", "Insulation"), ("membrane", "Membrane") })
            Check(CommandRulesMaterialTypes.RevitFunction(function) == revit, "layer function " + function + " is Revit MaterialFunctionAssignment." + revit);
        Check(CommandRulesMaterialTypes.PatternTarget("Model") == "model" && CommandRulesMaterialTypes.PatternTarget("Drafting") == "drafting", "Revit FillPatternTarget reads back as model or drafting");
        Check(CommandRulesMaterialTypes.Includes("all", "model") && CommandRulesMaterialTypes.Includes("all", "drafting") && CommandRulesMaterialTypes.Includes("model", "model") && !CommandRulesMaterialTypes.Includes("model", "drafting") && !CommandRulesMaterialTypes.Includes("drafting", "model"), "the target filter keeps matching patterns, all keeps both");
        Check(CommandRulesMaterialTypes.PatternName("model", "Masonry - Stone") == "model: Masonry - Stone", "a fill pattern can also be named target: name when the same name exists for both targets");
        Check(CommandRulesMaterialTypes.ExpectedLayers(new[] { new HostTypeLayerArg("structure", "Piedra local", Length.FromMillimetres(550)), new HostTypeLayerArg("thermal_air", "Air", Length.FromMillimetres(25)) }, new[] { StoneUid, AirUid })
            .SequenceEqual(new[] { new ExpectedRoofLayer("structure", StoneUid, 550), new ExpectedRoofLayer("insulation", AirUid, 25) }), "expected layers use the canonical function (thermal_air reads back as insulation)");

        foreach (var kind in new[] { "wall", "floor", "roof", "family_instance" })
            Check(Thrown(() => CommandRulesMaterialTypes.CheckChangeTypeKind(kind, "X")) is null, "element.change_type supports " + kind);
        Check(Thrown(() => CommandRulesMaterialTypes.CheckChangeTypeKind(null, "Level")) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.element_uid" } other && other.Detail!.Contains("Level", StringComparison.Ordinal), "element.change_type refuses other element classes, naming the class");
        Check(Thrown(() => CommandRulesMaterialTypes.CheckWallKind("Basic", "Basic")) is null, "a basic wall can take another basic wall type");
        Check(Thrown(() => CommandRulesMaterialTypes.CheckWallKind("Basic", "Curtain")) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.type" } kind2 && kind2.Detail!.Contains("Curtain", StringComparison.Ordinal), "a basic wall cannot become a curtain wall through change_type");
        Check(Thrown(() => CommandRulesMaterialTypes.CheckWallKind("Stacked", "Basic")) is { Field: "args.type" }, "a stacked wall cannot become a basic wall through change_type");
        var incompatible = CommandRulesMaterialTypes.Incompatible("Generic 150mm", "Floors", "Walls");
        Check(incompatible is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.type" } && incompatible.Detail!.Contains("Floors", StringComparison.Ordinal) && incompatible.Detail.Contains("Walls", StringComparison.Ordinal), "a type of another category is args_invalid naming both categories");
        Check(Thrown(() => CommandRulesMaterialTypes.CheckBaseWallKind("Basic")) is null && Thrown(() => CommandRulesMaterialTypes.CheckBaseWallKind("Curtain")) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.base_type" }, "wall_type.create duplicates only a basic wall type");
    }

    static ObservedMaterial Material() => new(MaterialUid, "Chapa negra", new RgbColor(45, 47, 50), 0, 40, 60, "Metal", "Metal", false,
        new PatternRef(PatternUid, "Metal - Standing Seam", "model"), null, AssetUid, "Metal - Steel");
    static ExpectedMaterial Expected() => new("Chapa negra", new RgbColor(45, 47, 50), null, 40, 60, "Metal", PatternUid, null);

    static void MaterialReadback()
    {
        Check(CommandReadbackMaterialTypes.Material(Expected(), Material()) is null, "material readback matches name, color, shininess, smoothness, class and surface pattern");
        Check(CommandReadbackMaterialTypes.Material(Expected() with { Color = null, Shininess = null, Smoothness = null, MaterialClass = null, SurfacePatternUid = null }, Material() with { UseRenderAppearanceForShading = true }) is null, "members not requested are not checked");
        Check(CommandReadbackMaterialTypes.Material(Expected(), Material() with { Name = "CHAPA NEGRA" }) is { } name && name.Contains("name", StringComparison.Ordinal), "another name (even only in case) is a mismatch");
        Check(CommandReadbackMaterialTypes.Material(Expected(), Material() with { Color = new RgbColor(45, 47, 51) }) is { } color && color.Contains("color", StringComparison.Ordinal), "another color is a mismatch");
        Check(CommandReadbackMaterialTypes.Material(Expected(), Material() with { Color = null }) is not null, "an unreadable color is a mismatch when one was requested");
        Check(CommandReadbackMaterialTypes.Material(Expected(), Material() with { UseRenderAppearanceForShading = true }) is { } shading && shading.Contains("render appearance", StringComparison.Ordinal), "a requested color with shading from the render appearance is a mismatch (the color would not show)");
        Check(CommandReadbackMaterialTypes.Material(Expected() with { Transparency = 20 }, Material()) is { } transparency && transparency.Contains("transparency", StringComparison.Ordinal), "another transparency is a mismatch");
        Check(CommandReadbackMaterialTypes.Material(Expected(), Material() with { Shininess = 41 }) is { } shininess && shininess.Contains("shininess", StringComparison.Ordinal), "another shininess is a mismatch");
        Check(CommandReadbackMaterialTypes.Material(Expected(), Material() with { Smoothness = 0 }) is { } smoothness && smoothness.Contains("smoothness", StringComparison.Ordinal), "another smoothness is a mismatch");
        Check(CommandReadbackMaterialTypes.Material(Expected(), Material() with { MaterialClass = "Generic" }) is { } cls && cls.Contains("class", StringComparison.Ordinal), "another class is a mismatch");
        Check(CommandReadbackMaterialTypes.Material(Expected(), Material() with { SurfacePattern = null }) is { } surface && surface.Contains("surface pattern", StringComparison.Ordinal), "a missing surface pattern is a mismatch");
        Check(CommandReadbackMaterialTypes.Material(Expected() with { CutPatternUid = CutPatternUid }, Material()) is { } cut && cut.Contains("cut pattern", StringComparison.Ordinal), "a missing cut pattern is a mismatch");
        Check(CommandReadbackMaterialTypes.Material(Expected() with { CutPatternUid = CutPatternUid }, Material() with { CutPattern = new PatternRef(CutPatternUid.ToUpperInvariant(), "Stone", "drafting") }) is null, "pattern UniqueIds compare ignoring case");
    }

    static ObservedHostType Wall() => new(TypeUid, WallName, new[]
    {
        new ObservedRoofLayer("structure", StoneUid, "Piedra local", 550), new ObservedRoofLayer("insulation", AirUid, "Air", 25.00004), new ObservedRoofLayer("finish2", WoodUid, "Madera lenga", 25),
    }, 600, 0, 2);
    static ExpectedRoofLayer[] WallLayers() => new[] { new ExpectedRoofLayer("structure", StoneUid, 550), new ExpectedRoofLayer("insulation", AirUid, 25), new ExpectedRoofLayer("finish2", WoodUid, 25) };

    static void HostTypeReadback()
    {
        Check(CommandReadbackMaterialTypes.HostType("wall type", WallName, WallLayers(), (0, 2), Wall()) is null, "wall type readback matches layers, thicknesses (1e-3 mm) and the core boundaries");
        Check(CommandReadbackMaterialTypes.HostType("wall type", WallName, WallLayers(), (0, 2), Wall() with { Name = WallName.ToUpperInvariant() }) is { } name && name.Contains("wall type name", StringComparison.Ordinal), "another name is a mismatch naming the kind");
        Check(CommandReadbackMaterialTypes.HostType("floor type", WallName, WallLayers(), (0, 2), Wall() with { Layers = null }) is { } none && none.Contains("floor type has no layers", StringComparison.Ordinal), "no compound structure is a mismatch");
        Check(CommandReadbackMaterialTypes.HostType("wall type", WallName, WallLayers(), (0, 2), Wall() with { Layers = Wall().Layers!.Take(2).ToArray() }) is { } count && count.Contains("2 layers", StringComparison.Ordinal), "another layer count is a mismatch");
        Check(CommandReadbackMaterialTypes.HostType("wall type", WallName, WallLayers().Reverse().ToArray(), (0, 2), Wall()) is { } order && order.Contains("layer 0", StringComparison.Ordinal), "layers in another order are a mismatch");
        Check(CommandReadbackMaterialTypes.HostType("wall type", WallName, WallLayers(), (0, 2), Wall() with { Layers = new[] { Wall().Layers![0], Wall().Layers![1], new ObservedRoofLayer("finish2", WoodUid, "Madera lenga", 25.01) } }) is { } thickness && thickness.Contains("thickness", StringComparison.Ordinal), "a layer of another thickness is a mismatch");
        Check(CommandReadbackMaterialTypes.HostType("wall type", WallName, WallLayers(), (0, 2), Wall() with { ExteriorShell = 1, InteriorShell = 1 }) is { } core && core.Contains("shell", StringComparison.Ordinal), "other core boundaries are a mismatch");
    }

    static void ChangeTypeReadback()
    {
        Check(CommandReadbackMaterialTypes.ChangeType(TypeUid, TypeUid) is null, "the element's type read back is the requested type");
        Check(CommandReadbackMaterialTypes.ChangeType(TypeUid, TypeUid.ToUpperInvariant()) is null, "type UniqueIds compare ignoring case");
        Check(CommandReadbackMaterialTypes.ChangeType(TypeUid, OtherTypeUid) is { } other && other.Contains(OtherTypeUid, StringComparison.Ordinal), "another type is a mismatch naming it");
        Check(CommandReadbackMaterialTypes.ChangeType(TypeUid, null) is not null, "an element that no longer exists or has no type is a mismatch");
    }

    static void ReadbackJson()
    {
        var patterns = JsonNode.Parse(CommandReadbackMaterialTypes.FillPatternsJson(new[]
        {
            new FillPatternRow("Wood 1", WoodUid, "model", false), new FillPatternRow("<Solid fill>", AirUid, "drafting", true), new FillPatternRow("Masonry - Stone", StoneUid, "model", false), new FillPatternRow("Diagonal up", PatternUid, "drafting", false),
        }, "model"))!["fill_patterns"]!.AsArray();
        Check(patterns.Count == 2 && patterns[0]!["name"]!.GetValue<string>() == "Masonry - Stone" && patterns[1]!["name"]!.GetValue<string>() == "Wood 1" && patterns[0]!["unique_id"]!.GetValue<string>() == StoneUid
            && patterns[0]!["target"]!.GetValue<string>() == "model" && patterns[0]!["is_solid"]!.GetValue<bool>() == false, "fill pattern readback keeps the requested target, sorted by name", patterns.ToJsonString());
        var all = JsonNode.Parse(CommandReadbackMaterialTypes.FillPatternsJson(new[] { new FillPatternRow("Wood 1", WoodUid, "model", false), new FillPatternRow("<Solid fill>", AirUid, "drafting", true), new FillPatternRow("Diagonal up", PatternUid, "drafting", false) }, "all"))!["fill_patterns"]!.AsArray();
        Check(all.Select(row => row!["name"]!.GetValue<string>()).SequenceEqual(new[] { "<Solid fill>", "Diagonal up", "Wood 1" }) && all[0]!["is_solid"]!.GetValue<bool>(), "all targets are listed sorted by target (drafting, model) then name");

        var created = JsonNode.Parse(CommandReadbackMaterialTypes.MaterialJson(Material(), existing: false))!["material"]!;
        Check(created["unique_id"]!.GetValue<string>() == MaterialUid && created["name"]!.GetValue<string>() == "Chapa negra" && created["existing"]!.GetValue<bool>() == false
            && created["color"]!.ToJsonString() == "[45,47,50]" && created["transparency"]!.GetValue<int>() == 0 && created["shininess"]!.GetValue<int>() == 40 && created["smoothness"]!.GetValue<int>() == 60
            && created["class"]!.GetValue<string>() == "Metal" && created["use_render_appearance_for_shading"]!.GetValue<bool>() == false,
            "material readback JSON reports UniqueId, name, existing, color, transparency, shininess, smoothness, class and shading source", created.ToJsonString());
        Check(created["surface_pattern"]!["unique_id"]!.GetValue<string>() == PatternUid && created["surface_pattern"]!["name"]!.GetValue<string>() == "Metal - Standing Seam" && created["surface_pattern"]!["target"]!.GetValue<string>() == "model" && created["cut_pattern"] is null,
            "material readback JSON reports the surface and cut patterns, null when none", created.ToJsonString());
        Check(created["appearance_asset"]!["unique_id"]!.GetValue<string>() == AssetUid && created["appearance_asset"]!["name"]!.GetValue<string>() == "Metal - Steel", "material readback JSON reports the render appearance asset", created.ToJsonString());
        var bare = JsonNode.Parse(CommandReadbackMaterialTypes.MaterialJson(Material() with { Color = null, AppearanceAssetUid = null, AppearanceAssetName = null, MaterialClass = null, SurfacePattern = null }, existing: true))!["material"]!;
        Check(bare["existing"]!.GetValue<bool>() && bare["color"] is null && bare["appearance_asset"] is null && bare["class"] is null && bare["surface_pattern"] is null, "a material without color, class, pattern or appearance asset reads back nulls", bare.ToJsonString());
        var set = JsonNode.Parse(CommandReadbackMaterialTypes.MaterialSetJson(Material() with { Color = new RgbColor(120, 120, 120) }, Material()))!;
        Check(set["material"]!["color"]!.ToJsonString() == "[45,47,50]" && set["before"]!["color"]!.ToJsonString() == "[120,120,120]" && set["material"]!["existing"] is null, "material.set readback reports the material after and before the change", set.ToJsonString());

        var wall = JsonNode.Parse(CommandReadbackMaterialTypes.HostTypeJson("wall_type", Wall(), existing: false))!["wall_type"]!;
        Check(wall["unique_id"]!.GetValue<string>() == TypeUid && wall["name"]!.GetValue<string>() == WallName && wall["existing"]!.GetValue<bool>() == false && wall["width_mm"]!.GetValue<double>() == 600
            && wall["exterior_shell_layers"]!.GetValue<int>() == 0 && wall["interior_shell_layers"]!.GetValue<int>() == 2, "wall type readback JSON reports UniqueId, name, existing, width and core boundaries", wall.ToJsonString());
        var layers = wall["layers"]!.AsArray();
        Check(layers.Count == 3 && layers[0]!["function"]!.GetValue<string>() == "structure" && layers[0]!["material_unique_id"]!.GetValue<string>() == StoneUid && layers[2]!["material_name"]!.GetValue<string>() == "Madera lenga"
            && layers[1]!["function"]!.GetValue<string>() == "insulation", "wall type readback JSON lists the layers exterior to interior", wall.ToJsonString());
        Check(JsonNode.Parse(CommandReadbackMaterialTypes.HostTypeJson("floor_type", Wall(), existing: true))!["floor_type"]!["existing"]!.GetValue<bool>(), "floor type readback JSON is under floor_type");

        var change = JsonNode.Parse(CommandReadbackMaterialTypes.ChangeTypeJson(WallUid, "Walls", new ObservedTypeRef(OtherTypeUid, "Generic - 200mm", "Basic Wall"), new ObservedTypeRef(TypeUid, WallName, "Basic Wall")))!["element"]!;
        Check(change["unique_id"]!.GetValue<string>() == WallUid && change["category"]!.GetValue<string>() == "Walls" && change["type_unique_id"]!.GetValue<string>() == TypeUid && change["type_name"]!.GetValue<string>() == WallName
            && change["family_name"]!.GetValue<string>() == "Basic Wall" && change["previous_type_unique_id"]!.GetValue<string>() == OtherTypeUid && change["previous_type_name"]!.GetValue<string>() == "Generic - 200mm" && change["unchanged"]!.GetValue<bool>() == false,
            "element.change_type readback JSON reports the new and previous type", change.ToJsonString());
        Check(JsonNode.Parse(CommandReadbackMaterialTypes.ChangeTypeJson(WallUid, null, new ObservedTypeRef(TypeUid, WallName, null), new ObservedTypeRef(TypeUid, WallName, null)))!["element"]!["unchanged"]!.GetValue<bool>(), "a request for the current type reads back unchanged true");
    }
}
