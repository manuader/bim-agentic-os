using Bim.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Revit;

// Materials, layered wall/floor types and type changes (chat-materials-types): executor decisions, readback checks and
// readback JSON. Autodesk-free on purpose: tests/revit-commands source-links this file. CommandsExecutorMaterialTypes.cs
// reads the Revit facts (lengths converted with UnitUtils) and calls the Revit API.

// A fill pattern as listed by query.fill_patterns; Target is "model" or "drafting".
public sealed record FillPatternRow(string Name, string UniqueId, string Target, bool IsSolid);
// A fill pattern a material uses.
public sealed record PatternRef(string UniqueId, string Name, string Target);
// A material as Revit reports it. Color null when Material.Color is not valid; AppearanceAsset* null without a render
// appearance asset.
public sealed record ObservedMaterial(string UniqueId, string Name, RgbColor? Color, int Transparency, int Shininess, int Smoothness, string? MaterialClass, string? MaterialCategory,
    bool UseRenderAppearanceForShading, PatternRef? SurfacePattern, PatternRef? CutPattern, string? AppearanceAssetUid, string? AppearanceAssetName);
// What a material must be after material.create or material.set; a null member is not checked.
public sealed record ExpectedMaterial(string Name, RgbColor? Color, int? Transparency, int? Shininess, int? Smoothness, string? MaterialClass, string? SurfacePatternUid, string? CutPatternUid);
// A wall or floor type as Revit reports it: Layers null without a compound structure; the shell counts are
// CompoundStructure.GetNumberOfShellLayers(Exterior / Interior); StructuralMaterialIndex is
// CompoundStructure.StructuralMaterialIndex (-1 when none or without a compound structure).
public sealed record ObservedHostType(string UniqueId, string Name, IReadOnlyList<ObservedRoofLayer>? Layers, double? WidthMm, int ExteriorShell, int InteriorShell, int StructuralMaterialIndex);
// An element type: FamilyName is ElementType.FamilyName (for example "Basic Wall"), null when empty.
public sealed record ObservedTypeRef(string UniqueId, string Name, string? FamilyName);

public static class CommandRulesMaterialTypes
{
    // Request function -> Revit MaterialFunctionAssignment member name (thermal_air is Insulation).
    public static string RevitFunction(string function) => CommandRulesMaterials.RevitFunction(RevitMaterialTypeRules.Canonical(function));

    // Revit FillPatternTarget member name -> "model" / "drafting".
    public static string PatternTarget(string revitTarget) => revitTarget == "Model" ? "model" : "drafting";

    public static bool Includes(string requested, string target) => requested == "all" || requested == target;

    // The qualified name a fill pattern can also be referenced by, for a name used by both targets.
    public static string PatternName(string target, string name) => target + ": " + name;

    // Expected layers with canonical functions and the resolved material UniqueIds, in request order.
    public static IReadOnlyList<ExpectedRoofLayer> ExpectedLayers(IReadOnlyList<HostTypeLayerArg> layers, IReadOnlyList<string> materialUids) =>
        layers.Select((layer, i) => new ExpectedRoofLayer(RevitMaterialTypeRules.Canonical(layer.Function), materialUids[i], layer.Thickness.Millimetres)).ToArray();

    // kind: "wall", "floor", "roof" or "family_instance" for a supported element, null otherwise; className is the
    // element's Revit class name for the refusal.
    public static void CheckChangeTypeKind(string? kind, string className)
    {
        if (kind is "wall" or "floor" or "roof" or "family_instance") return;
        throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.element_uid", "element.change_type supports walls, floors, roofs and family instances, not a " + className + "; use element.set_parameter for other elements");
    }

    // A wall keeps its kind: switching basic, curtain and stacked walls changes far more than the type.
    public static void CheckWallKind(string fromKind, string toKind)
    {
        if (fromKind == toKind) return;
        throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.type", "the wall is a " + fromKind + " wall and the type is a " + toKind + " wall type; pick a " + fromKind + " wall type");
    }

    public static RevitCommandException Incompatible(string typeName, string? typeCategory, string? elementCategory) =>
        new(RevitCommandCodes.ArgsInvalid, "args.type", "type '" + typeName + "' (" + (typeCategory ?? "no category") + ") cannot be assigned to this element (" + (elementCategory ?? "no category") + "); pick a type of the same category");

    public const string NoEndCap = "NoEndCap";
    public const string NoWrapping = "None";

    // The end and opening wrapping of a new layered type's CompoundStructure, as Revit EndCapCondition and
    // OpeningWrappingCondition member names. hostKind: "wall", "floor" or "roof"; baseEndCap / baseOpeningWrapping: the
    // base type's own structure conditions (null when unread).
    // CompoundStructure.CreateSimpleCompoundStructure starts with a wall end cap, and SetCompoundStructure on a floor type
    // refused it in Revit 2025.2 (owner, 2026-11-04: "Input compound structure has wrong EndCap condition for this element
    // type"); the Revit API says floors and roofs must use EndCapCondition.NoEndCap. A floor or roof therefore always gets
    // NoEndCap and keeps the base type's opening wrapping (None when unread); a wall keeps both of its base wall type's
    // conditions, except NoEndCap (not a wall condition) or an unread one, which become None (no wrapping).
    public static (string EndCap, string OpeningWrapping) Wrapping(string hostKind, string? baseEndCap, string? baseOpeningWrapping) => hostKind switch
    {
        "floor" or "roof" => (NoEndCap, baseOpeningWrapping ?? NoWrapping),
        "wall" => (baseEndCap is null or NoEndCap ? NoWrapping : baseEndCap, baseOpeningWrapping ?? NoWrapping),
        _ => throw new ArgumentOutOfRangeException(nameof(hostKind), hostKind, "host kind is wall, floor or roof"),
    };

    // Which existing element a create request names (index into names), null when none: placeholder, not yet exact first.
    public static int? PickExisting(IReadOnlyList<string> names, string name)
    {
        for (var i = 0; i < names.Count; i++) if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }

    // wall_type.create duplicates a basic wall type (curtain and stacked types have no simple layer structure).
    public static void CheckBaseWallKind(string kind)
    {
        if (kind == "Basic") return;
        throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.base_type", "the wall type is a " + kind + " wall type; pick a basic wall type from revit_query_wall_types");
    }
}

public static class CommandReadbackMaterialTypes
{
    static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    static string C(RgbColor? color) => color is { } c ? "[" + c.Red + ", " + c.Green + ", " + c.Blue + "]" : "unreadable";
    static bool SameUid(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // Null when every requested member matches; the name is always checked (ordinal).
    public static string? Material(ExpectedMaterial expected, ObservedMaterial observed)
    {
        if (!string.Equals(observed.Name, expected.Name, StringComparison.Ordinal)) return "material name is '" + observed.Name + "', expected '" + expected.Name + "'";
        if (expected.Color is { } color)
        {
            if (observed.Color != color) return "material color is " + C(observed.Color) + ", expected " + C(color);
            if (observed.UseRenderAppearanceForShading) return "material shades from its render appearance, so its color " + C(color) + " would not show in shaded views";
        }
        if (expected.Transparency is { } transparency && observed.Transparency != transparency) return "material transparency is " + observed.Transparency + ", expected " + transparency;
        if (expected.Shininess is { } shininess && observed.Shininess != shininess) return "material shininess is " + observed.Shininess + ", expected " + shininess;
        if (expected.Smoothness is { } smoothness && observed.Smoothness != smoothness) return "material smoothness is " + observed.Smoothness + ", expected " + smoothness;
        if (expected.MaterialClass is { } materialClass && !string.Equals(observed.MaterialClass, materialClass, StringComparison.Ordinal)) return "material class is '" + observed.MaterialClass + "', expected '" + materialClass + "'";
        if (expected.SurfacePatternUid is { } surface && !SameUid(observed.SurfacePattern?.UniqueId, surface)) return "material surface pattern is " + (observed.SurfacePattern?.UniqueId ?? "none") + ", expected " + surface;
        if (expected.CutPatternUid is { } cut && !SameUid(observed.CutPattern?.UniqueId, cut)) return "material cut pattern is " + (observed.CutPattern?.UniqueId ?? "none") + ", expected " + cut;
        return null;
    }

    // Null when the type is the requested one: exactly this name (ordinal), these layers in this order (function,
    // material, thickness within 1e-3 mm) and these core boundaries. kind: "wall type" or "floor type".
    public static string? HostType(string kind, string name, IReadOnlyList<ExpectedRoofLayer> expected, (int Exterior, int Interior) shells, ObservedHostType observed)
    {
        if (!string.Equals(observed.Name, name, StringComparison.Ordinal)) return kind + " name is '" + observed.Name + "', expected '" + name + "'";
        if (observed.Layers is not { } layers) return kind + " has no layers (no compound structure)";
        if (layers.Count != expected.Count) return kind + " has " + layers.Count + " layers, expected " + expected.Count;
        for (var i = 0; i < layers.Count; i++)
        {
            var (layer, want) = (layers[i], expected[i]);
            if (layer.Function != want.Function) return kind + " layer " + i + " function is " + layer.Function + ", expected " + want.Function;
            if (!SameUid(layer.MaterialUid, want.MaterialUid)) return kind + " layer " + i + " material is " + (layer.MaterialUid ?? "none") + ", expected " + want.MaterialUid;
            if (!CommandGeometry.Near(layer.WidthMm, want.ThicknessMm)) return kind + " layer " + i + " thickness is " + F(layer.WidthMm) + " mm, expected " + F(want.ThicknessMm);
        }
        if (observed.ExteriorShell != shells.Exterior || observed.InteriorShell != shells.Interior)
            return kind + " has " + observed.ExteriorShell + " exterior and " + observed.InteriorShell + " interior shell layers, expected " + shells.Exterior + " and " + shells.Interior + " (the core is the structure layers)";
        return null;
    }

    // observedTypeUid: the element's type after the change, null when the element or its type is gone.
    public static string? ChangeType(string targetUid, string? observedTypeUid) =>
        SameUid(observedTypeUid, targetUid) ? null : "element type is " + (observedTypeUid ?? "unreadable (element or type missing)") + ", expected " + targetUid;

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

    // {fill_patterns: [{name, unique_id, target, is_solid}]} for the requested target, sorted by target, name, UniqueId.
    public static string FillPatternsJson(IEnumerable<FillPatternRow> rows, string target) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartArray("fill_patterns");
        foreach (var row in rows.Where(row => CommandRulesMaterialTypes.Includes(target, row.Target)).OrderBy(row => row.Target, StringComparer.Ordinal).ThenBy(row => row.Name, StringComparer.Ordinal).ThenBy(row => row.UniqueId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("name", row.Name);
            writer.WriteString("unique_id", row.UniqueId);
            writer.WriteString("target", row.Target);
            writer.WriteBoolean("is_solid", row.IsSolid);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    static void Pattern(Utf8JsonWriter writer, string name, PatternRef? pattern)
    {
        if (pattern is null) { writer.WriteNull(name); return; }
        writer.WriteStartObject(name);
        writer.WriteString("unique_id", pattern.UniqueId);
        writer.WriteString("name", pattern.Name);
        writer.WriteString("target", pattern.Target);
        writer.WriteEndObject();
    }

    static void MaterialObject(Utf8JsonWriter writer, string member, ObservedMaterial material, bool? existing)
    {
        writer.WriteStartObject(member);
        writer.WriteString("unique_id", material.UniqueId);
        writer.WriteString("name", material.Name);
        if (existing is { } value) writer.WriteBoolean("existing", value);
        if (material.Color is { } color)
        {
            writer.WriteStartArray("color");
            writer.WriteNumberValue(color.Red);
            writer.WriteNumberValue(color.Green);
            writer.WriteNumberValue(color.Blue);
            writer.WriteEndArray();
        }
        else writer.WriteNull("color");
        writer.WriteNumber("transparency", material.Transparency);
        writer.WriteNumber("shininess", material.Shininess);
        writer.WriteNumber("smoothness", material.Smoothness);
        Text(writer, "class", material.MaterialClass);
        Text(writer, "category", material.MaterialCategory);
        writer.WriteBoolean("use_render_appearance_for_shading", material.UseRenderAppearanceForShading);
        Pattern(writer, "surface_pattern", material.SurfacePattern);
        Pattern(writer, "cut_pattern", material.CutPattern);
        if (material.AppearanceAssetUid is null) writer.WriteNull("appearance_asset");
        else
        {
            writer.WriteStartObject("appearance_asset");
            writer.WriteString("unique_id", material.AppearanceAssetUid);
            Text(writer, "name", material.AppearanceAssetName);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    // {material: {unique_id, name, existing, color, transparency, shininess, smoothness, class, category,
    // use_render_appearance_for_shading, surface_pattern, cut_pattern, appearance_asset}}.
    public static string MaterialJson(ObservedMaterial material, bool existing) => Json(writer =>
    {
        writer.WriteStartObject();
        MaterialObject(writer, "material", material, existing);
        writer.WriteEndObject();
    });

    // {material: {...after...}, before: {...}} without existing.
    public static string MaterialSetJson(ObservedMaterial before, ObservedMaterial after) => Json(writer =>
    {
        writer.WriteStartObject();
        MaterialObject(writer, "material", after, null);
        MaterialObject(writer, "before", before, null);
        writer.WriteEndObject();
    });

    // {<member>: {unique_id, name, existing, width_mm, exterior_shell_layers, interior_shell_layers, layers: [{function,
    // material_unique_id, material_name, thickness_mm}]}}; member is "wall_type" or "floor_type".
    public static string HostTypeJson(string member, ObservedHostType type, bool existing) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject(member);
        writer.WriteString("unique_id", type.UniqueId);
        writer.WriteString("name", type.Name);
        writer.WriteBoolean("existing", existing);
        if (type.WidthMm is { } width && double.IsFinite(width)) writer.WriteNumber("width_mm", width);
        else writer.WriteNull("width_mm");
        writer.WriteNumber("exterior_shell_layers", type.ExteriorShell);
        writer.WriteNumber("interior_shell_layers", type.InteriorShell);
        writer.WriteStartArray("layers");
        foreach (var layer in type.Layers ?? Array.Empty<ObservedRoofLayer>())
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

    // {element: {unique_id, category, type_unique_id, type_name, family_name, previous_type_unique_id,
    // previous_type_name, unchanged}}.
    public static string ChangeTypeJson(string elementUid, string? category, ObservedTypeRef before, ObservedTypeRef after) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject("element");
        writer.WriteString("unique_id", elementUid);
        Text(writer, "category", category);
        writer.WriteString("type_unique_id", after.UniqueId);
        writer.WriteString("type_name", after.Name);
        Text(writer, "family_name", after.FamilyName);
        writer.WriteString("previous_type_unique_id", before.UniqueId);
        writer.WriteString("previous_type_name", before.Name);
        writer.WriteBoolean("unchanged", SameUid(before.UniqueId, after.UniqueId));
        writer.WriteEndObject();
        writer.WriteEndObject();
    });
}
