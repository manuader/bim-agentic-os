using System.Globalization;
using System.Text.Json;
namespace Bim.Core;

// Material and layered type creation (P9-06): material.create, wall_type.create and floor_type.create. Same envelope
// (protocol_version 1), schema subset, unit rules, copies-only guard and operation_id receipts as catalogue v1;
// earlier commands are unchanged. Vendor-free like every RevitCommands*.cs file; the add-in executor
// (CommandsExecutorTypes.cs) consumes these types. Layer functions are RevitMaterialRules.Functions
// (RevitCommandsMaterials.cs), the same as roof_type.create.

// A colour as 0-255 channels (the byte channels of a Revit Color).
public sealed record RgbColor(int Red, int Green, int Blue);
// A new material named Name. Every other member is optional: null means not requested (left at Revit's default
// and not compared when an existing material of that name is found). AppearanceAsset is an existing appearance asset
// (name or UniqueId) that the new material shares; it is not copied.
public sealed record MaterialCreateArgs(string Name, RgbColor? Color, int? Transparency, int? Shininess, int? Smoothness, string? MaterialClass, string? AppearanceAsset) : RevitCommandArgs;
// One compound structure layer of a wall or floor type: Function is one of RevitMaterialRules.Functions, Material a
// material name or UniqueId from query.materials, Thickness 0 for a membrane and greater than 0 otherwise.
public sealed record TypeLayerArg(string Function, string Material, Length Thickness);
// A new wall type duplicated from the basic wall type BaseType under Name, with Layers from exterior to interior.
public sealed record WallTypeCreateArgs(string Name, string BaseType, IReadOnlyList<TypeLayerArg> Layers) : RevitCommandArgs;
// A new floor type duplicated from the layered floor type BaseType under Name, with Layers from top to bottom.
public sealed record FloorTypeCreateArgs(string Name, string BaseType, IReadOnlyList<TypeLayerArg> Layers) : RevitCommandArgs;

public static class RevitTypeRules
{
    public const int MaxColorChannel = 255;
    public const int MaxTransparency = 100;
    public const int MaxShininess = 128;
    public const int MaxSmoothness = 100;

    // The core of a layered type: from the first to the last structure layer (indices into functions). Revit needs a
    // core and refuses a membrane inside it, so no structure layer, or a membrane between structure layers, is
    // args_invalid (at field, or at field[i] for the membrane).
    public static (int First, int Last) CoreRange(IReadOnlyList<string> functions, string field)
    {
        var first = -1;
        var last = -1;
        for (var i = 0; i < functions.Count; i++)
        {
            if (functions[i] != "structure") continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field, "a layered type needs at least one structure layer (the core)");
        for (var i = first; i <= last; i++)
            if (functions[i] == RevitMaterialRules.Membrane) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field + "[" + i + "]", "a membrane cannot lie between structure layers (inside the core)");
        return (first, last);
    }
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of every part exists (the v1 schema helpers are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> TypeCommandSpecs = new(BuildTypeCommands);

    public static IReadOnlyList<RevitCommandSpec> TypeCommands => TypeCommandSpecs.Value;

    static string TypeLayerSchema(string what)
    {
        var functions = string.Join(",", RevitMaterialRules.Functions.Select(function => "\"" + function + "\""));
        return Fill("""{"type":"object","description":"One layer of the @2@ build-up","properties":{"function":{"type":"string","description":"Layer function: finish1 (outer finish, for example stone cladding or floor boards), finish2 (inner finish, for example plaster), substrate, structure (the core, for example block or a concrete slab), insulation or membrane","enum":[@0@]},"material":@1@,"thickness_mm":{"type":"number","description":"Layer thickness in millimetres: exactly 0 for a membrane, otherwise greater than 0 and at most 9144000"}},"required":["function","material","thickness_mm"],"additionalProperties":false}""",
            functions, Reference("Material: name or UniqueId from revit_query_materials"), what);
    }

    static string LayeredTypeSchema(string what, string order, string query) =>
        Fill("""{"type":"object","properties":{"name":@0@,"base_type":@1@,"layers":{"type":"array","description":"Layers from @3@, 1 to @4@; at least one structure layer (the core runs from the first to the last structure layer and holds no membrane)","items":@2@,"minItems":1,"maxItems":@4@}},"required":["name","base_type","layers"],"additionalProperties":false}""",
            Text("Name of the new " + what + "; none of \\\\ : { } [ ] | ; < > ? ` ~ and no leading or trailing space. If a " + what + " of this name already exists with exactly these layers it is returned unchanged; with other layers the request is refused", 256),
            Reference(what.Substring(0, 1).ToUpperInvariant() + what.Substring(1) + " to duplicate: name or UniqueId from " + query),
            TypeLayerSchema(what), order, RevitMaterialRules.MaxLayers.ToString(CultureInfo.InvariantCulture));

    static string Channel(string name) => Fill("""{"type":"integer","description":"@0@ channel, 0 to 255"}""", name);

    static IReadOnlyList<RevitCommandSpec> BuildTypeCommands()
    {
        var material = Fill("""{"type":"object","properties":{"name":@0@,"color":{"type":"object","description":"Graphics shading colour as 0-255 channels; optional","properties":{"r":@1@,"g":@2@,"b":@3@},"required":["r","g","b"],"additionalProperties":false},"transparency":{"type":"integer","description":"Graphics transparency, 0 (opaque) to 100; optional"},"shininess":{"type":"integer","description":"Graphics shininess, 0 to 128; optional"},"smoothness":{"type":"integer","description":"Graphics smoothness, 0 to 100; optional"},"material_class":@4@,"appearance_asset":@5@},"required":["name"],"additionalProperties":false}""",
            Text("Name of the new material; none of \\\\ : { } [ ] | ; < > ? ` ~ and no leading or trailing space. If a material of this name already exists with exactly the requested properties it is returned unchanged; otherwise the request is refused", 256),
            Channel("Red"), Channel("Green"), Channel("Blue"),
            Text("Material class, for example Stone, Wood, Metal or Concrete; no leading or trailing space; optional", 256),
            Reference("Existing appearance (render) asset of the document to share with the new material, by name or UniqueId; it is not copied, so editing it later changes every material that uses it; optional"));
        return new[]
        {
            new RevitCommandSpec("material.create", true, "Create a material with a name and optional graphics colour (RGB 0-255), transparency, shininess, smoothness, material class and an existing appearance asset; an existing material of that name is returned only when every requested property is identical, otherwise refused. Textures and new appearance assets are not offered. Allowed only on enabled test copies.", material, BindMaterial),
            new RevitCommandSpec("wall_type.create", true, "Create a wall type by duplicating a basic wall type (curtain and stacked wall types are refused) under a new name and setting its layers from exterior to interior: function, material and thickness in millimetres (a membrane is 0 mm); an existing type of that name is returned only when its layers are identical. Allowed only on enabled test copies.",
                LayeredTypeSchema("wall type", "exterior to interior", "revit_query_wall_types"), args => { var (name, baseType, layers) = BindLayeredType(args, "wall type name"); return new WallTypeCreateArgs(name, baseType, layers); }),
            new RevitCommandSpec("floor_type.create", true, "Create a floor type by duplicating a layered floor type under a new name and setting its layers from top to bottom: function, material and thickness in millimetres (a membrane is 0 mm); an existing type of that name is returned only when its layers are identical. Allowed only on enabled test copies.",
                LayeredTypeSchema("floor type", "top to bottom", "revit_query_floor_types"), args => { var (name, baseType, layers) = BindLayeredType(args, "floor type name"); return new FloorTypeCreateArgs(name, baseType, layers); }),
        };
    }

    // An optional integer member in [0, max]; the schema has already checked that it is an integer.
    static int? BoundedInteger(JsonElement parent, string member, int max, string field)
    {
        if (!parent.TryGetProperty(member, out var value)) return null;
        var number = value.GetInt64();
        if (number < 0 || number > max) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field, "must be between 0 and " + max.ToString(CultureInfo.InvariantCulture));
        return (int)number;
    }

    static RevitCommandArgs BindMaterial(JsonElement args)
    {
        var name = args.GetProperty("name").GetString()!;
        RevitDocumentationRules.CheckName(name, "args.name", "material name");
        RgbColor? color = null;
        if (args.TryGetProperty("color", out var rgb))
            color = new RgbColor(BoundedInteger(rgb, "r", RevitTypeRules.MaxColorChannel, "args.color.r")!.Value, BoundedInteger(rgb, "g", RevitTypeRules.MaxColorChannel, "args.color.g")!.Value, BoundedInteger(rgb, "b", RevitTypeRules.MaxColorChannel, "args.color.b")!.Value);
        string? materialClass = args.TryGetProperty("material_class", out var classElement) ? classElement.GetString() : null;
        if (materialClass is not null && materialClass.Trim().Length != materialClass.Length)
            throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.material_class", "material class must not start or end with white space");
        return new MaterialCreateArgs(name, color,
            BoundedInteger(args, "transparency", RevitTypeRules.MaxTransparency, "args.transparency"),
            BoundedInteger(args, "shininess", RevitTypeRules.MaxShininess, "args.shininess"),
            BoundedInteger(args, "smoothness", RevitTypeRules.MaxSmoothness, "args.smoothness"),
            materialClass,
            args.TryGetProperty("appearance_asset", out var asset) ? asset.GetString() : null);
    }

    // name, base_type and layers with the roof_type.create layer rules plus a core (RevitTypeRules.CoreRange).
    static (string Name, string BaseType, IReadOnlyList<TypeLayerArg> Layers) BindLayeredType(JsonElement args, string what)
    {
        var name = args.GetProperty("name").GetString()!;
        RevitDocumentationRules.CheckName(name, "args.name", what);
        var layers = new List<TypeLayerArg>();
        var index = 0;
        foreach (var layer in args.GetProperty("layers").EnumerateArray())
        {
            var field = "args.layers[" + index + "].thickness_mm";
            var function = layer.GetProperty("function").GetString()!;
            var thickness = RevitUnits.Millimetres(Number(layer.GetProperty("thickness_mm")), field, allowNegative: false, allowZero: true);
            if (function == RevitMaterialRules.Membrane)
            {
                if (thickness.Millimetres != 0) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, "a membrane layer has no thickness in Revit; give 0");
            }
            else if (thickness.Millimetres == 0) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, "only a membrane layer may be 0 mm thick");
            if (thickness.Millimetres > RevitCommandGeometry.MaxRevitLengthMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, "thickness above 9144000 mm (Revit's 30000 ft limit)");
            layers.Add(new TypeLayerArg(function, layer.GetProperty("material").GetString()!, thickness));
            index++;
        }
        if (layers.All(layer => layer.Thickness.Millimetres == 0)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.layers", "at least one layer must be thicker than 0 mm");
        RevitTypeRules.CoreRange(layers.Select(layer => layer.Function).ToArray(), "args.layers");
        return (name, args.GetProperty("base_type").GetString()!, layers);
    }
}
