using Bim.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Revit;

// Materials and layered roof types (chat-roof-gable): executor decisions, readback checks and readback JSON, in
// millimetres. Autodesk-free on purpose: tests/revit-commands source-links this file. CommandsExecutorMaterials.cs
// reads the Revit facts (converted with UnitUtils) and calls the Revit API.

// Class and Category are Material.MaterialClass / MaterialCategory, null when empty.
public sealed record MaterialRow(string Name, string UniqueId, string? MaterialClass, string? MaterialCategory);
// What a layer must be after roof_type.create: function (RevitMaterialRules.Functions), the resolved material's
// UniqueId and the thickness in millimetres.
public sealed record ExpectedRoofLayer(string Function, string MaterialUid, double ThicknessMm);
// A compound structure layer as Revit reports it; Function is CommandRulesMaterials.FunctionName of its
// MaterialFunctionAssignment; MaterialUid/MaterialName are null when the layer has no material.
public sealed record ObservedRoofLayer(string Function, string? MaterialUid, string? MaterialName, double WidthMm);
// Layers null: the type has no compound structure. WidthMm: CompoundStructure.GetWidth.
public sealed record ObservedRoofType(string UniqueId, string Name, IReadOnlyList<ObservedRoofLayer>? Layers, double? WidthMm);

public static class CommandRulesMaterials
{
    // Request function -> Revit MaterialFunctionAssignment member name.
    public static string RevitFunction(string function) => function switch
    {
        "finish1" => "Finish1",
        "finish2" => "Finish2",
        "substrate" => "Substrate",
        "structure" => "Structure",
        "insulation" => "Insulation",
        "membrane" => "Membrane",
        _ => throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.layers", "unknown layer function " + function),
    };

    // Revit MaterialFunctionAssignment member name -> readback function: the request name for the six offered
    // functions, else the member name in lower case (which never equals a requested function).
    public static string FunctionName(string revitFunction) => revitFunction switch
    {
        "Finish1" => "finish1",
        "Finish2" => "finish2",
        "Substrate" => "substrate",
        "Structure" => "structure",
        "Insulation" => "insulation",
        "Membrane" => "membrane",
        _ => revitFunction.ToLowerInvariant(),
    };
}

public static class CommandReadbackMaterials
{
    static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    // Null when the type is the requested one: exactly this name (ordinal) and these layers in this order, each with
    // the same function, material and thickness (1e-3 mm). Used both after a create and to decide whether an
    // existing type of that name is identical.
    public static string? RoofType(string name, IReadOnlyList<ExpectedRoofLayer> expected, ObservedRoofType observed)
    {
        if (!string.Equals(observed.Name, name, StringComparison.Ordinal)) return "roof type name is '" + observed.Name + "', expected '" + name + "'";
        if (observed.Layers is not { } layers) return "roof type has no layers (no compound structure)";
        if (layers.Count != expected.Count) return "roof type has " + layers.Count + " layers, expected " + expected.Count;
        for (var i = 0; i < layers.Count; i++)
        {
            var (layer, want) = (layers[i], expected[i]);
            if (layer.Function != want.Function) return "roof type layer " + i + " function is " + layer.Function + ", expected " + want.Function;
            if (!string.Equals(layer.MaterialUid, want.MaterialUid, StringComparison.OrdinalIgnoreCase)) return "roof type layer " + i + " material is " + (layer.MaterialUid ?? "none") + ", expected " + want.MaterialUid;
            if (!CommandGeometry.Near(layer.WidthMm, want.ThicknessMm)) return "roof type layer " + i + " thickness is " + F(layer.WidthMm) + " mm, expected " + F(want.ThicknessMm);
        }
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

    // {materials: [{name, unique_id, class, category}]}, sorted by name then UniqueId (ordinal).
    public static string MaterialsJson(IEnumerable<MaterialRow> rows) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartArray("materials");
        foreach (var row in rows.OrderBy(row => row.Name, StringComparer.Ordinal).ThenBy(row => row.UniqueId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("name", row.Name);
            writer.WriteString("unique_id", row.UniqueId);
            Text(writer, "class", row.MaterialClass);
            Text(writer, "category", row.MaterialCategory);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    // {roof_type: {unique_id, name, existing, width_mm, layers: [{function, material_unique_id, material_name,
    // thickness_mm}]}}, layers exterior to interior as read from the type's compound structure.
    public static string RoofTypeJson(ObservedRoofType type, bool existing) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject("roof_type");
        writer.WriteString("unique_id", type.UniqueId);
        writer.WriteString("name", type.Name);
        writer.WriteBoolean("existing", existing);
        if (type.WidthMm is { } width && double.IsFinite(width)) writer.WriteNumber("width_mm", width);
        else writer.WriteNull("width_mm");
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
}
