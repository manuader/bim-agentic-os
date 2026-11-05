using System.Globalization;
using System.Text.Json;
namespace Bim.Core;

// Materials and layered roof types (chat-roof-gable): read-only query.materials and roof_type.create. Same envelope
// (protocol_version 1), schema subset, unit rules, copies-only guard and operation_id receipts as catalogue v1;
// earlier commands are unchanged. Vendor-free like every RevitCommands*.cs file; the add-in executor
// (CommandsExecutorMaterials.cs) consumes these types.

// One compound structure layer: Function is one of RevitMaterialRules.Functions, Material a material name or
// UniqueId from query.materials, Thickness 0 for a membrane and greater than 0 otherwise.
public sealed record RoofTypeLayerArg(string Function, string Material, Length Thickness);
// A new roof type duplicated from BaseType under Name, with Layers from exterior (top) to interior (bottom).
public sealed record RoofTypeCreateArgs(string Name, string BaseType, IReadOnlyList<RoofTypeLayerArg> Layers) : RevitCommandArgs;

public static class RevitMaterialRules
{
    public const int MaxLayers = 32;
    public const string Membrane = "membrane";
    // Revit MaterialFunctionAssignment values offered, in lower case.
    public static readonly IReadOnlyList<string> Functions = new[] { "finish1", "finish2", "substrate", "structure", "insulation", Membrane };
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of every part exists (the v1 schema helpers are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> MaterialSpecs = new(BuildMaterials);

    public static IReadOnlyList<RevitCommandSpec> Materials => MaterialSpecs.Value;

    static IReadOnlyList<RevitCommandSpec> BuildMaterials()
    {
        var functions = string.Join(",", RevitMaterialRules.Functions.Select(function => "\"" + function + "\""));
        var layer = Fill("""{"type":"object","description":"One layer of the roof build-up","properties":{"function":{"type":"string","description":"Layer function: finish1 (outer finish, for example tiles), finish2, substrate (for example boards), structure (for example rafters), insulation or membrane","enum":[@0@]},"material":@1@,"thickness_mm":{"type":"number","description":"Layer thickness in millimetres: exactly 0 for a membrane, otherwise at least 1.2 (Revit minimum) and at most 9144000"}},"required":["function","material","thickness_mm"],"additionalProperties":false}""",
            functions, Reference("Material: name or UniqueId from revit_query_materials"));
        var roofType = Fill("""{"type":"object","properties":{"name":@0@,"base_type":@1@,"layers":{"type":"array","description":"Layers from exterior (top of the roof) to interior (bottom), 1 to @3@; at least one layer thicker than 0","items":@2@,"minItems":1,"maxItems":@3@}},"required":["name","base_type","layers"],"additionalProperties":false}""",
            Text("Name of the new roof type; none of \\\\ : { } [ ] | ; < > ? ` ~ and no leading or trailing space. If a roof type of this name already exists with exactly these layers it is returned unchanged; with other layers the request is refused", 256),
            Reference("Roof type to duplicate (a basic roof with layers): name or UniqueId from revit_query_roof_types"),
            layer, RevitMaterialRules.MaxLayers.ToString(CultureInfo.InvariantCulture));

        return new[]
        {
            new RevitCommandSpec("query.materials", false, "List the materials in the document with name, UniqueId, material class and category, to pick layer materials such as wood or roof tiles. Read-only.", EmptySchema, _ => new EmptyQueryArgs()),
            new RevitCommandSpec("roof_type.create", true, "Create a roof type by duplicating an existing roof type under a new name and setting its layers from exterior to interior: function, material and thickness in millimetres (a membrane is 0 mm); an existing type of that name is returned only when its layers are identical. Allowed only on enabled test copies.", roofType, BindRoofType),
        };
    }

    static RevitCommandArgs BindRoofType(JsonElement args)
    {
        var name = args.GetProperty("name").GetString()!;
        RevitDocumentationRules.CheckName(name, "args.name", "roof type name");
        var layers = new List<RoofTypeLayerArg>();
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
            else if (thickness.Millimetres < RevitMaterialTypeRules.MinLayerThicknessMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, RevitMaterialTypeRules.TooThin);
            if (thickness.Millimetres > RevitCommandGeometry.MaxRevitLengthMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, "thickness above 9144000 mm (Revit's 30000 ft limit)");
            layers.Add(new RoofTypeLayerArg(function, layer.GetProperty("material").GetString()!, thickness));
            index++;
        }
        if (layers.All(layer => layer.Thickness.Millimetres == 0)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.layers", "at least one layer must be thicker than 0 mm");
        return new RoofTypeCreateArgs(name, args.GetProperty("base_type").GetString()!, layers);
    }
}
