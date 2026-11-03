using Bim.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Revit;

// Material and layered type creation (P9-06): executor decisions, readback checks and readback JSON, in millimetres.
// Autodesk-free on purpose: tests/revit-commands source-links this file. CommandsExecutorTypes.cs reads the Revit
// facts (converted with UnitUtils) and calls the Revit API. Layer functions map to Revit with CommandRulesMaterials.

// What a layer must be after wall_type.create / floor_type.create: function (RevitMaterialRules.Functions), the
// resolved material's UniqueId and the thickness in millimetres.
public sealed record ExpectedTypeLayer(string Function, string MaterialUid, double ThicknessMm);
// A compound structure layer as Revit reports it; Function is CommandRulesMaterials.FunctionName of its
// MaterialFunctionAssignment; MaterialUid/MaterialName are null when the layer has no material.
public sealed record ObservedTypeLayer(string Function, string? MaterialUid, string? MaterialName, double WidthMm);
// Layers null: the type has no compound structure. WidthMm: CompoundStructure.GetWidth. ExteriorShellLayers /
// InteriorShellLayers: GetNumberOfShellLayers(Exterior / Interior); StructuralMaterialIndex: StructuralMaterialIndex
// (-1 when none); all null without a compound structure.
public sealed record ObservedLayeredType(string UniqueId, string Name, IReadOnlyList<ObservedTypeLayer>? Layers, double? WidthMm,
    int? ExteriorShellLayers, int? InteriorShellLayers, int? StructuralMaterialIndex);
// A material as Revit reports it: Color null when Material.Color is not valid; MaterialClass/MaterialCategory null
// when empty; the appearance asset null when the material has none.
public sealed record ObservedMaterial(string UniqueId, string Name, RgbColor? Color, int Transparency, int Shininess, int Smoothness,
    string? MaterialClass, string? MaterialCategory, string? AppearanceAssetUid, string? AppearanceAssetName);

public static class CommandRulesTypes
{
    // Null when a wall type of this Revit WallKind (member name) can take a layered compound structure; otherwise
    // why not. Only Basic walls carry layers; curtain and stacked walls are refused, any other kind fails closed.
    public static string? WallKindProblem(string revitKind) => revitKind switch
    {
        "Basic" => null,
        "Curtain" => "is a curtain wall type (panels and mullions, no layers)",
        "Stacked" => "is a stacked wall type (made of other wall types, no layers of its own)",
        _ => "is a wall type of kind " + revitKind + ", not a basic wall",
    };

    // Which existing element a create request names: the first exact (ordinal) name match, else the first match
    // ignoring case (Revit type and material names ignore case), else null (nothing of that name: create it).
    public static int? PickExisting(IReadOnlyList<string> names, string name)
    {
        for (var i = 0; i < names.Count; i++) if (string.Equals(names[i], name, StringComparison.Ordinal)) return i;
        for (var i = 0; i < names.Count; i++) if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }
}

public static class CommandReadbackTypes
{
    static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    // Null when the type is the requested one: exactly this name (ordinal) and these layers in this order, each with
    // the same function, material (UniqueId, hex case ignored) and thickness (1e-3 mm). Used both after a create and
    // to decide whether an existing type of that name is identical. what names the type in messages ("wall type").
    public static string? LayeredType(string what, string name, IReadOnlyList<ExpectedTypeLayer> expected, ObservedLayeredType observed)
    {
        if (!string.Equals(observed.Name, name, StringComparison.Ordinal)) return what + " name is '" + observed.Name + "', expected '" + name + "'";
        if (observed.Layers is not { } layers) return what + " has no layers (no compound structure)";
        if (layers.Count != expected.Count) return what + " has " + layers.Count + " layers, expected " + expected.Count;
        for (var i = 0; i < layers.Count; i++)
        {
            var (layer, want) = (layers[i], expected[i]);
            if (layer.Function != want.Function) return what + " layer " + i + " function is " + layer.Function + ", expected " + want.Function;
            if (!string.Equals(layer.MaterialUid, want.MaterialUid, StringComparison.OrdinalIgnoreCase)) return what + " layer " + i + " material is " + (layer.MaterialUid ?? "none") + ", expected " + want.MaterialUid;
            if (!CommandGeometry.Near(layer.WidthMm, want.ThicknessMm)) return what + " layer " + i + " thickness is " + F(layer.WidthMm) + " mm, expected " + F(want.ThicknessMm);
        }
        // The core is the run of structure layers (RevitTypeRules.CoreRange): that many exterior and interior shell
        // layers around it, and the first structure layer as the structural material layer.
        var (first, last) = RevitTypeRules.CoreRange(expected.Select(layer => layer.Function).ToArray(), "args.layers");
        var interior = expected.Count - 1 - last;
        if (observed.ExteriorShellLayers != first || observed.InteriorShellLayers != interior)
            return what + " core has " + Count(observed.ExteriorShellLayers) + " exterior and " + Count(observed.InteriorShellLayers) + " interior shell layers, expected " + first + " and " + interior + " (core layers " + first + " to " + last + ")";
        if (observed.StructuralMaterialIndex != first) return what + " structural material layer is " + Count(observed.StructuralMaterialIndex) + ", expected " + first;
        return null;
    }

    static string Count(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    static string Rgb(RgbColor? color) => color is null ? "none" : color.Red + "," + color.Green + "," + color.Blue;

    // Null when the material has every requested property: name (ordinal) always; colour, transparency, shininess,
    // smoothness, class (ordinal) and appearance asset (the resolved asset's UniqueId, hex case ignored) only when
    // requested. Used both after a create and to decide whether an existing material of that name is identical.
    public static string? Material(MaterialCreateArgs args, string? appearanceAssetUid, ObservedMaterial observed)
    {
        if (!string.Equals(observed.Name, args.Name, StringComparison.Ordinal)) return "material name is '" + observed.Name + "', expected '" + args.Name + "'";
        if (args.Color is { } color && observed.Color != color) return "material color is " + Rgb(observed.Color) + ", expected " + Rgb(color);
        if (args.Transparency is { } transparency && observed.Transparency != transparency) return "material transparency is " + observed.Transparency + ", expected " + transparency;
        if (args.Shininess is { } shininess && observed.Shininess != shininess) return "material shininess is " + observed.Shininess + ", expected " + shininess;
        if (args.Smoothness is { } smoothness && observed.Smoothness != smoothness) return "material smoothness is " + observed.Smoothness + ", expected " + smoothness;
        if (args.MaterialClass is { } materialClass && !string.Equals(observed.MaterialClass, materialClass, StringComparison.Ordinal)) return "material class is '" + (observed.MaterialClass ?? "") + "', expected '" + materialClass + "'";
        if (args.AppearanceAsset is not null && (appearanceAssetUid is null || !string.Equals(observed.AppearanceAssetUid, appearanceAssetUid, StringComparison.OrdinalIgnoreCase)))
            return "material appearance asset is " + (observed.AppearanceAssetUid ?? "none") + ", expected " + (appearanceAssetUid ?? "the requested asset");
        return null;
    }

    static string Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void Text(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    // {<member>: {unique_id, name, existing, width_mm, core: {first, last}|null, structural_material_index,
    // layers: [{function, material_unique_id, material_name, thickness_mm}]}}, member wall_type or floor_type, layers in compound structure order (exterior or top first).
    public static string LayeredTypeJson(string member, ObservedLayeredType type, bool existing) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject(member);
        writer.WriteString("unique_id", type.UniqueId);
        writer.WriteString("name", type.Name);
        writer.WriteBoolean("existing", existing);
        if (type.WidthMm is { } width && double.IsFinite(width)) writer.WriteNumber("width_mm", width);
        else writer.WriteNull("width_mm");
        // core: {first, last} layer indices from the shell counts, null without layers or shell counts.
        if (type.Layers is { } coreLayers && type.ExteriorShellLayers is { } exterior && type.InteriorShellLayers is { } interiorShell)
        {
            writer.WriteStartObject("core");
            writer.WriteNumber("first", exterior);
            writer.WriteNumber("last", coreLayers.Count - 1 - interiorShell);
            writer.WriteEndObject();
        }
        else writer.WriteNull("core");
        if (type.StructuralMaterialIndex is { } structural) writer.WriteNumber("structural_material_index", structural);
        else writer.WriteNull("structural_material_index");
        writer.WriteStartArray("layers");
        foreach (var layer in type.Layers ?? Array.Empty<ObservedTypeLayer>())
        {
            writer.WriteStartObject();
            writer.WriteString("function", layer.Function);
            Text(writer, "material_unique_id", layer.MaterialUid);
            Text(writer, "material_name", layer.MaterialName);
            writer.WriteNumber("thickness_mm", layer.WidthMm);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    // {material: {unique_id, name, existing, color: {r, g, b}|null, transparency, shininess, smoothness, class,
    // category, appearance_asset: {unique_id, name}|null}}.
    public static string MaterialJson(ObservedMaterial material, bool existing) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject("material");
        writer.WriteString("unique_id", material.UniqueId);
        writer.WriteString("name", material.Name);
        writer.WriteBoolean("existing", existing);
        if (material.Color is { } color)
        {
            writer.WriteStartObject("color");
            writer.WriteNumber("r", color.Red);
            writer.WriteNumber("g", color.Green);
            writer.WriteNumber("b", color.Blue);
            writer.WriteEndObject();
        }
        else writer.WriteNull("color");
        writer.WriteNumber("transparency", material.Transparency);
        writer.WriteNumber("shininess", material.Shininess);
        writer.WriteNumber("smoothness", material.Smoothness);
        Text(writer, "class", material.MaterialClass);
        Text(writer, "category", material.MaterialCategory);
        if (material.AppearanceAssetUid is { } asset)
        {
            writer.WriteStartObject("appearance_asset");
            writer.WriteString("unique_id", asset);
            Text(writer, "name", material.AppearanceAssetName);
            writer.WriteEndObject();
        }
        else writer.WriteNull("appearance_asset");
        writer.WriteEndObject();
        writer.WriteEndObject();
    });
}
