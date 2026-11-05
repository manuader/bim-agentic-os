using System.Globalization;
using System.Text.Json;
namespace Bim.Core;

// Materials, layered wall/floor types and type changes (chat-materials-types): read-only query.fill_patterns and the
// writes material.create, material.set, wall_type.create, floor_type.create and element.change_type, so a project can
// get materials the template lacks (stone, dark sheet metal, wood lining), layered wall and floor types built from
// them, and existing walls, floors, roofs and family instances switched to those types. Same envelope
// (protocol_version 1), schema subset, unit rules, copies-only guard and operation_id receipts as catalogue v1; earlier
// commands are unchanged. Vendor-free like every RevitCommands*.cs file; the add-in executor
// (CommandsExecutorMaterialTypes.cs) consumes these types.

// A color as 0-255 red, green and blue channels.
public readonly record struct RgbColor(byte Red, byte Green, byte Blue);
// Target: "model", "drafting" or "all" (RevitMaterialTypeRules.PatternTargets).
public sealed record FillPatternQueryArgs(string Target) : RevitCommandArgs;
// A new material Name with the shaded Color; BaseMaterial (name or UniqueId) is duplicated instead of creating a
// blank material, so the copy keeps its render appearance asset. Null members are left as Revit sets them.
public sealed record MaterialCreateArgs(string Name, RgbColor Color, int? Transparency, int? Shininess, int? Smoothness, string? MaterialClass,
    string? BaseMaterial, string? SurfacePattern, string? CutPattern) : RevitCommandArgs;
// Changes to the existing material Material (name or UniqueId); at least one member is not null.
public sealed record MaterialSetArgs(string Material, RgbColor? Color, int? Transparency, int? Shininess, int? Smoothness, string? SurfacePattern, string? CutPattern) : RevitCommandArgs;
// One compound structure layer: Function one of RevitMaterialTypeRules.Functions, Material a material name or UniqueId,
// Thickness 0 for a membrane and greater than 0 otherwise.
public sealed record HostTypeLayerArg(string Function, string Material, Length Thickness);
// A new basic wall type duplicated from BaseType under Name, Layers from exterior to interior.
public sealed record WallTypeCreateArgs(string Name, string BaseType, IReadOnlyList<HostTypeLayerArg> Layers) : RevitCommandArgs;
// A new floor type duplicated from BaseType under Name, Layers from top to bottom.
public sealed record FloorTypeCreateArgs(string Name, string BaseType, IReadOnlyList<HostTypeLayerArg> Layers) : RevitCommandArgs;
// Give the element ElementUid the type Type (name, "Family: Type" or UniqueId among the types valid for it).
public sealed record ElementChangeTypeArgs(string ElementUid, string Type) : RevitCommandArgs;

public static class RevitMaterialTypeRules
{
    public const int MaxLayers = 32;
    public const int MaxTransparency = 100;
    public const int MaxShininess = 128;
    public const int MaxSmoothness = 100;
    // Thinnest non-membrane layer accepted: Revit's minimum compound structure layer thickness is 1/256 ft
    // (1.190625 mm); 1.2 mm keeps a margin over it after unit conversion (also applied by roof_type.create).
    public const double MinLayerThicknessMm = 1.2;
    public static readonly string TooThin = "layer thinner than " + MinLayerThicknessMm.ToString(CultureInfo.InvariantCulture) + " mm (Revit's minimum layer thickness is 1/256 ft, 1.190625 mm)";
    public const string Membrane = "membrane";
    public const string Structure = "structure";
    public const string ThermalAir = "thermal_air";
    // Revit MaterialFunctionAssignment values offered, in lower case; thermal_air is the name Revit's dialog shows
    // ("Thermal/Air Layer") for the insulation function and reads back as insulation.
    public static readonly IReadOnlyList<string> Functions = new[] { "finish1", "finish2", "substrate", Structure, "insulation", ThermalAir, Membrane };
    public static readonly IReadOnlyList<string> PatternTargets = new[] { "model", "drafting", "all" };

    public static string Canonical(string function) => function == ThermalAir ? "insulation" : function;

    // The core is the run from the first to the last structure layer; everything outside it is shell. Revit refuses a
    // core holding a membrane, and a layered type needs a structure layer.
    public static (int Exterior, int Interior) Shells(IReadOnlyList<string> functions, string field)
    {
        var first = -1;
        var last = -1;
        for (var i = 0; i < functions.Count; i++)
        {
            if (functions[i] != Structure) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field, "a layered type needs at least one structure layer (the core)");
        for (var i = first; i <= last; i++)
            if (functions[i] == Membrane) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field + "[" + i + "]", "a membrane cannot lie between structure layers (inside the core)");
        return (first, functions.Count - 1 - last);
    }
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of every part exists (the v1 schema helpers are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> MaterialTypeSpecs = new(BuildMaterialTypes);

    public static IReadOnlyList<RevitCommandSpec> MaterialTypes => MaterialTypeSpecs.Value;

    // The material type commands that change existing elements (MCP destructiveHint).
    static bool MaterialTypeDestructive(string command) => command is "material.set" or "element.change_type";

    static IReadOnlyList<RevitCommandSpec> BuildMaterialTypes()
    {
        const string Color = """{"type":"array","description":"[r, g, b], each an integer from 0 to 255","items":{"type":"integer"},"minItems":3,"maxItems":3}""";
        const string Transparency = """{"type":"integer","description":"Transparency from 0 (opaque) to 100"}""";
        const string Shininess = """{"type":"integer","description":"Shininess from 0 to 128"}""";
        const string Smoothness = """{"type":"integer","description":"Smoothness from 0 to 100"}""";
        var surface = Reference("Fill pattern for the surface (model view) foreground pattern: name, target: name (model: or drafting:) or UniqueId from revit_query_fill_patterns");
        var cut = Reference("Fill pattern for the cut foreground pattern: name, target: name (model: or drafting:) or UniqueId from revit_query_fill_patterns");
        var patterns = Fill("""{"type":"object","properties":{"target":{"type":"string","description":"Which fill patterns to list: model, drafting or all; default all","enum":[@0@]}},"additionalProperties":false}""",
            string.Join(",", RevitMaterialTypeRules.PatternTargets.Select(target => "\"" + target + "\"")));
        var create = Fill("""{"type":"object","properties":{"name":@0@,"color":@1@,"transparency":@2@,"shininess":@3@,"smoothness":@4@,"material_class":@5@,"base_material":@6@,"surface_pattern":@7@,"cut_pattern":@8@},"required":["name","color"],"additionalProperties":false}""",
            Text("Name of the new material; none of \\\\ : { } [ ] | ; < > ? ` ~ and no leading or trailing space. If a material of this name already exists with the requested values it is returned unchanged; with other values the request is refused", 256),
            Color, Transparency, Shininess, Smoothness, Text("Material class, for example Metal, Masonry, Wood or Stone; optional", 256),
            Reference("Material to duplicate (keeps its render appearance asset): name or UniqueId from revit_query_materials; optional, default a new blank material"),
            surface, cut);
        var set = Fill("""{"type":"object","properties":{"material":@0@,"color":@1@,"transparency":@2@,"shininess":@3@,"smoothness":@4@,"surface_pattern":@5@,"cut_pattern":@6@},"required":["material"],"additionalProperties":false}""",
            Reference("Material to change: name or UniqueId from revit_query_materials"), Color, Transparency, Shininess, Smoothness, surface, cut);
        var functions = string.Join(",", RevitMaterialTypeRules.Functions.Select(function => "\"" + function + "\""));
        var layer = Fill("""{"type":"object","description":"One layer of the build-up","properties":{"function":{"type":"string","description":"Layer function: finish1, finish2, substrate, structure (the core), insulation, thermal_air (Revit's Thermal/Air Layer, the same function as insulation) or membrane","enum":[@0@]},"material":@1@,"thickness_mm":{"type":"number","description":"Layer thickness in millimetres: exactly 0 for a membrane, otherwise at least 1.2 (Revit minimum) and at most 9144000"}},"required":["function","material","thickness_mm"],"additionalProperties":false}""",
            functions, Reference("Material: name or UniqueId from revit_query_materials"));
        string HostType(string kind, string query, string order) => Fill("""{"type":"object","properties":{"name":@0@,"base_type":@1@,"layers":{"type":"array","description":"Layers from @2@, 1 to @3@; the structure layers form the core","items":@4@,"minItems":1,"maxItems":@3@}},"required":["name","base_type","layers"],"additionalProperties":false}""",
            Text("Name of the new " + kind + " type; none of \\\\ : { } [ ] | ; < > ? ` ~ and no leading or trailing space. If a " + kind + " type of this name already exists with exactly these layers it is returned unchanged; with other layers the request is refused", 256),
            Reference(char.ToUpperInvariant(kind[0]) + kind[1..] + " type to duplicate: name or UniqueId from " + query), order,
            RevitMaterialTypeRules.MaxLayers.ToString(CultureInfo.InvariantCulture), layer);
        var change = Fill("""{"type":"object","properties":{"element_uid":@0@,"type":@1@},"required":["element_uid","type"],"additionalProperties":false}""",
            UniqueIdSchema, Reference("New type: name, Family: Type or UniqueId of a type valid for the element (same category), for example from revit_query_wall_types"));

        const string Layers = "function (finish1, finish2, substrate, structure, insulation, thermal_air = Revit's Thermal/Air Layer read back as insulation, membrane), material and thickness in millimetres (a membrane is 0 mm); the structure layers form the core and a membrane may not lie inside it";
        return new[]
        {
            new RevitCommandSpec("query.fill_patterns", false, "List the fill patterns in the document (target model, drafting or all) with name, UniqueId, target and whether it is a solid fill, to pick stone, wood or metal surface and cut patterns for materials. Read-only.", patterns, BindFillPatterns),
            new RevitCommandSpec("material.create", true, "Create a material with a name and a shaded color [r, g, b] (Material.Color, the color of shaded views; the material is set not to use its render appearance for shading), optional transparency 0-100, shininess 0-128, smoothness 0-100, material class and surface and cut fill patterns; "
                + "with base_material the existing material is duplicated instead, so it keeps its render appearance asset (textures are not authored). An existing material of that name is returned with existing true when its color and the other requested values match (base_material is not compared); otherwise the request is refused. Allowed only on enabled test copies.", create, BindMaterialCreate),
            new RevitCommandSpec("material.set", true, "Change an existing material (by name or UniqueId): shaded color [r, g, b] (also stops shading from the render appearance), transparency, shininess, smoothness and surface or cut fill pattern; at least one change; the values before and after are read back. Every element using the material changes. Allowed only on enabled test copies.", set, BindMaterialSet),
            new RevitCommandSpec("wall_type.create", true, "Create a basic wall type by duplicating an existing basic wall type under a new name and setting its layers from exterior to interior: " + Layers + ". An existing type of that name is returned only when its layers are identical. Allowed only on enabled test copies.", HostType("wall", "revit_query_wall_types", "exterior to interior"), args => BindHostType(args, wall: true)),
            new RevitCommandSpec("floor_type.create", true, "Create a floor type by duplicating an existing floor type under a new name and setting its layers from top to bottom: " + Layers + ". An existing type of that name is returned only when its layers are identical. Allowed only on enabled test copies.", HostType("floor", "revit_query_floor_types", "top to bottom"), args => BindHostType(args, wall: false)),
            new RevitCommandSpec("element.change_type", true, "Change the type of an existing wall, floor, roof or family instance (by UniqueId) to another type of the same category, for example a wall to a new layered wall type; a basic wall keeps a basic wall type (no curtain or stacked switch); group members are refused; the previous and new type are read back. Allowed only on enabled test copies.", change, BindChangeType),
        };
    }

    static RevitCommandArgs BindFillPatterns(JsonElement args) =>
        new FillPatternQueryArgs(args.TryGetProperty("target", out var target) ? target.GetString()! : "all");

    static RgbColor BindColor(JsonElement color, string field)
    {
        var channels = new byte[3];
        var index = 0;
        foreach (var channel in color.EnumerateArray())
        {
            if (!channel.TryGetInt64(out var value) || value < 0 || value > 255) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field + "[" + index + "]", "a color channel is an integer from 0 to 255");
            channels[index++] = (byte)value;
        }
        return new RgbColor(channels[0], channels[1], channels[2]);
    }

    static int? BindRange(JsonElement args, string name, int max)
    {
        if (!args.TryGetProperty(name, out var member)) return null;
        if (!member.TryGetInt64(out var value) || value < 0 || value > max) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args." + name, name + " is an integer from 0 to " + max.ToString(CultureInfo.InvariantCulture));
        return (int)value;
    }

    static string? Optional(JsonElement args, string name) => args.TryGetProperty(name, out var member) ? member.GetString() : null;

    static RevitCommandArgs BindMaterialCreate(JsonElement args)
    {
        var name = args.GetProperty("name").GetString()!;
        RevitDocumentationRules.CheckName(name, "args.name", "material name");
        return new MaterialCreateArgs(name, BindColor(args.GetProperty("color"), "args.color"),
            BindRange(args, "transparency", RevitMaterialTypeRules.MaxTransparency), BindRange(args, "shininess", RevitMaterialTypeRules.MaxShininess), BindRange(args, "smoothness", RevitMaterialTypeRules.MaxSmoothness),
            Optional(args, "material_class"), Optional(args, "base_material"), Optional(args, "surface_pattern"), Optional(args, "cut_pattern"));
    }

    static RevitCommandArgs BindMaterialSet(JsonElement args)
    {
        RgbColor? color = args.TryGetProperty("color", out var value) ? BindColor(value, "args.color") : null;
        var bound = new MaterialSetArgs(args.GetProperty("material").GetString()!, color,
            BindRange(args, "transparency", RevitMaterialTypeRules.MaxTransparency), BindRange(args, "shininess", RevitMaterialTypeRules.MaxShininess), BindRange(args, "smoothness", RevitMaterialTypeRules.MaxSmoothness),
            Optional(args, "surface_pattern"), Optional(args, "cut_pattern"));
        if (bound is { Color: null, Transparency: null, Shininess: null, Smoothness: null, SurfacePattern: null, CutPattern: null })
            throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args", "give at least one of color, transparency, shininess, smoothness, surface_pattern or cut_pattern");
        return bound;
    }

    static RevitCommandArgs BindHostType(JsonElement args, bool wall)
    {
        var name = args.GetProperty("name").GetString()!;
        RevitDocumentationRules.CheckName(name, "args.name", wall ? "wall type name" : "floor type name");
        var layers = new List<HostTypeLayerArg>();
        var index = 0;
        foreach (var layer in args.GetProperty("layers").EnumerateArray())
        {
            var field = "args.layers[" + index + "].thickness_mm";
            var function = layer.GetProperty("function").GetString()!;
            var thickness = RevitUnits.Millimetres(Number(layer.GetProperty("thickness_mm")), field, allowNegative: false, allowZero: true);
            if (function == RevitMaterialTypeRules.Membrane)
            {
                if (thickness.Millimetres != 0) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, "a membrane layer has no thickness in Revit; give 0");
            }
            else if (thickness.Millimetres == 0) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, "only a membrane layer may be 0 mm thick");
            else if (thickness.Millimetres < RevitMaterialTypeRules.MinLayerThicknessMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, RevitMaterialTypeRules.TooThin);
            if (thickness.Millimetres > RevitCommandGeometry.MaxRevitLengthMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, "thickness above 9144000 mm (Revit's 30000 ft limit)");
            layers.Add(new HostTypeLayerArg(function, layer.GetProperty("material").GetString()!, thickness));
            index++;
        }
        RevitMaterialTypeRules.Shells(layers.Select(layer => layer.Function).ToArray(), "args.layers");
        var baseType = args.GetProperty("base_type").GetString()!;
        return wall ? new WallTypeCreateArgs(name, baseType, layers) : new FloorTypeCreateArgs(name, baseType, layers);
    }

    static RevitCommandArgs BindChangeType(JsonElement args) =>
        new ElementChangeTypeArgs(args.GetProperty("element_uid").GetString()!, args.GetProperty("type").GetString()!);
}
