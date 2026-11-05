using System.Globalization;
using System.Text.Json;
namespace Bim.Core;

// direct_shape.create (P9-09): a DirectShape solid for site and landscape elements Revit has no family or tool for (a
// boulder kept inside the building, a planter, a retaining block, a stand-in for a tree mass). Same envelope
// (protocol_version 1), schema subset, unit rules, copies-only guard and operation_id receipts as the earlier catalogue
// parts, which are unchanged. Vendor-free like every RevitCommands*.cs file; the add-in executor
// (CommandsExecutorDirectShape.cs) consumes these types.
//
// Two shapes, both closed planar solids with vertical sides:
//   extrusion: a closed simple plan polygon of 3 to 256 [x, y] millimetre vertices (the closing edge implicit) drawn at
//     the level's project elevation plus base_offset_mm and extruded straight up by height_mm;
//   box: an axis-aligned box between min and max [x, y, z] millimetre corners, z in the frame of a level's project
//     elevation (query.levels project_elevation_mm, as toposolid.create points).

// Outline: the plan polygon in millimetres, as given (a repeated closing vertex dropped), any orientation.
public abstract record DirectShapeGeometry;
public sealed record DirectShapeExtrusion(string Level, IReadOnlyList<Point2> Outline, Length BaseOffset, Length Height) : DirectShapeGeometry;
public sealed record DirectShapeBox(SitePoint Min, SitePoint Max) : DirectShapeGeometry;
// Category: one of RevitDirectShapeRules.Categories; Material: a material name or UniqueId, or null for Revit's
// default; Name: the DirectShape's name, or null to leave Revit's default.
public sealed record DirectShapeCreateArgs(string Category, DirectShapeGeometry Shape, string? Material, string? Name) : RevitCommandArgs;

public static class RevitDirectShapeRules
{
    public const int MinVertices = 3;
    public const int MaxVertices = 256;
    // Outline items the schema allows: MaxVertices distinct vertices plus an optional repeat of the first one.
    public const int MaxOutlineItems = MaxVertices + 1;
    public const int MaxNameLength = 256;

    // The categories offered, in schema order, with the built-in category each maps to. Revit decides per document
    // whether it accepts a DirectShape of that category (DirectShape.IsValidCategoryId); the executor refuses one it
    // does not accept as args_invalid at args.category before anything is created.
    static readonly (string Name, string BuiltIn)[] CategoryMap =
    {
        ("generic_model", "OST_GenericModel"),
        ("site", "OST_Site"),
        ("planting", "OST_Planting"),
        ("entourage", "OST_Entourage"),
        ("topography", "OST_Topography"),
    };

    public static IReadOnlyList<string> Categories { get; } = CategoryMap.Select(entry => entry.Name).ToArray();

    // The BuiltInCategory enum name (its ToString text) of an offered category; args_invalid at args.category otherwise.
    public static string BuiltInCategory(string category)
    {
        foreach (var (name, builtIn) in CategoryMap)
            if (string.Equals(name, category, StringComparison.Ordinal)) return builtIn;
        throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.category", "expected one of " + string.Join(", ", Categories));
    }

    // A coordinate within Revit's 30000 ft limit; length_invalid at field otherwise (non-finite values and values
    // beyond 1e8 mm are refused first by RevitUnits).
    public static double Coordinate(double value, string field)
    {
        RevitUnits.Millimetres(value, field, allowNegative: true, allowZero: true);
        if (Math.Abs(value) > RevitCommandGeometry.MaxRevitLengthMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, "beyond 9144000 mm (Revit's 30000 ft limit)");
        return value;
    }

    // Extrusion height: at least 1.2 mm (Revit's short-curve tolerance) and at most 9144000 mm.
    public static Length Height(double value, string field)
    {
        var height = RevitUnits.Millimetres(value, field, allowNegative: false, allowZero: false);
        if (!(height.Millimetres >= RevitCommandGeometry.MinSegmentMm)) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, "height under 1.2 mm");
        if (height.Millimetres > RevitCommandGeometry.MaxRevitLengthMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, "height above 9144000 mm (Revit's 30000 ft limit)");
        return height;
    }

    // Box corners: every coordinate within Revit's limit and max at least 1.2 mm and at most 9144000 mm above min on
    // every axis (length_invalid at args.shape.max[k] otherwise).
    public static void CheckBox((double X, double Y, double Z) min, (double X, double Y, double Z) max, string field)
    {
        var lo = new[] { min.X, min.Y, min.Z };
        var hi = new[] { max.X, max.Y, max.Z };
        for (var k = 0; k < 3; k++)
        {
            if (!(hi[k] - lo[k] >= RevitCommandGeometry.MinSegmentMm))
                throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field + ".max[" + k + "]", "max must be at least 1.2 mm greater than min on every axis (x, y, z)");
            if (hi[k] - lo[k] > RevitCommandGeometry.MaxRevitLengthMm)
                throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field + ".max[" + k + "]", "the box is more than 9144000 mm (Revit's 30000 ft limit) across on this axis");
        }
    }

    // Distinct vertices (after a repeated closing vertex is dropped): at most MaxVertices (args_invalid at field); the
    // plan extent in x and in y at most 9144000 mm (length_invalid at field).
    public static void CheckOutline(IReadOnlyList<(double X, double Y)> outline, string field)
    {
        if (outline.Count > MaxVertices) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field, "at most " + MaxVertices + " distinct vertices (a repeat of the first vertex at the end is allowed)");
        if (outline.Max(p => p.X) - outline.Min(p => p.X) > RevitCommandGeometry.MaxRevitLengthMm || outline.Max(p => p.Y) - outline.Min(p => p.Y) > RevitCommandGeometry.MaxRevitLengthMm)
            throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, "the outline is more than 9144000 mm (Revit's 30000 ft limit) across in x or y");
    }

    // The solid's top above the level (base offset + height) within 9144000 mm: length_invalid at field. The level's
    // own elevation is only known in Revit.
    public static void CheckTop(Length baseOffset, Length height, string field)
    {
        if (baseOffset.Millimetres + height.Millimetres > RevitCommandGeometry.MaxRevitLengthMm)
            throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, "base_offset_mm + height_mm is above 9144000 mm (Revit's 30000 ft limit)");
    }

    // Names follow the Revit naming rule of grids, views and sheets: none of RevitViewRules.ForbiddenNameCharacters
    // (args_invalid at field).
    public static void CheckName(string? name, string field)
    {
        if (name is null) return;
        var at = name.IndexOfAny(RevitViewRules.ForbiddenNameCharacters.ToCharArray());
        if (at >= 0) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field, "Revit names cannot contain '" + name[at] + "' (none of " + RevitViewRules.ForbiddenNameCharacters + ")");
    }
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of every part exists (the v1 schema helpers are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> DirectShapeSpecs = new(BuildDirectShape);

    public static IReadOnlyList<RevitCommandSpec> DirectShapes => DirectShapeSpecs.Value;

    static IReadOnlyList<RevitCommandSpec> BuildDirectShape()
    {
        const string Point3 = """{"type":"array","description":"[x, y, z] in millimetres; z is an elevation in the frame of a level's project_elevation_mm (revit_query_levels); each within 9144000","items":{"type":"number"},"minItems":3,"maxItems":3}""";
        const string Vertex = """{"type":"array","description":"[x, y] in millimetres, each within 9144000","items":{"type":"number"},"minItems":2,"maxItems":2}""";
        var extrusion = Fill("""{"type":"object","description":"A closed plan polygon extruded straight up from the level (plus base_offset_mm) by height_mm","properties":{"kind":{"type":"string","enum":["extrusion"]},"level":@0@,"outline":{"type":"array","description":"Closed simple plan polygon of @1@ to @2@ distinct [x, y] millimetre vertices in order (either direction); the closing edge is implicit (repeating the first vertex at the end is allowed); collinear vertices are allowed; at most 9144000 across in x and y; edges at least 1.2 mm long; it must not cross or touch itself","items":@3@,"minItems":@1@,"maxItems":@4@},"base_offset_mm":{"type":"number","description":"Bottom of the solid above the level's elevation in millimetres; may be negative; at most 9144000 either way; default 0"},"height_mm":{"type":"number","description":"Height of the solid in millimetres, at least 1.2; base_offset_mm + height_mm at most 9144000"}},"required":["kind","level","outline","height_mm"],"additionalProperties":false}""",
            Reference("Level: name or UniqueId from revit_query_levels; the polygon is drawn at its elevation plus base_offset_mm"),
            RevitDirectShapeRules.MinVertices.ToString(CultureInfo.InvariantCulture), RevitDirectShapeRules.MaxVertices.ToString(CultureInfo.InvariantCulture), Vertex, RevitDirectShapeRules.MaxOutlineItems.ToString(CultureInfo.InvariantCulture));
        var box = Fill("""{"type":"object","description":"An axis-aligned box between two corners","properties":{"kind":{"type":"string","enum":["box"]},"min":@0@,"max":@1@},"required":["kind","min","max"],"additionalProperties":false}""",
            Point3.Replace("[x, y, z] in millimetres", "Lowest corner [x, y, z] in millimetres", StringComparison.Ordinal),
            Point3.Replace("[x, y, z] in millimetres", "Highest corner [x, y, z] in millimetres, at least 1.2 mm and at most 9144000 mm above min on every axis", StringComparison.Ordinal));
        var categories = string.Join(",", RevitDirectShapeRules.Categories.Select(name => "\"" + name + "\""));
        var schema = Fill("""{"type":"object","properties":{"category":{"type":"string","description":"Revit category of the new element: generic_model (Generic Models), site (Site), planting (Planting), entourage (Entourage) or topography (Topography); refused when Revit does not accept a DirectShape of that category in the document","enum":[@0@]},"shape":{"description":"The solid: an extruded plan polygon or an axis-aligned box","oneOf":[@1@,@2@]},"material":@3@,"name":@4@},"required":["category","shape"],"additionalProperties":false}""",
            categories, extrusion, box, Reference("Material: name or UniqueId of a material in the document (revit_query_materials); optional, else Revit's default"),
            Text("Name of the new DirectShape so it can be identified (for example 'boulder 3'); no backslash, colon, braces, square brackets, |, ;, <, >, ?, ` or ~; optional", RevitDirectShapeRules.MaxNameLength));
        return new[]
        {
            new RevitCommandSpec("direct_shape.create", true,
                "Create a DirectShape solid for a site or landscape element Revit has no family for (a boulder, a planter, a retaining block, a mass stand-in for a tree), in the site, planting, entourage, topography or generic model category, with an optional material and name. "
                + "The shape is either an extrusion (a closed plan polygon of 3 to 256 [x, y] millimetre vertices that must not cross itself, drawn at a level's elevation plus base_offset_mm and extruded straight up by height_mm) or an axis-aligned box between min and max [x, y, z] millimetre corners (z as a level's project elevation). "
                + "Example: a 1200 x 800 x 900 mm boulder block at the origin on Level 1 is {category: site, shape: {kind: extrusion, level: Level 1, outline: [[0,0],[1200,0],[1200,800],[0,800]], height_mm: 900}}. "
                + "The shape is not hosted on or associated with the level: the level only sets the elevation it is drawn at, and moving the level later does not move it. "
                + "The element's bounding box and category are read back. Allowed only on enabled test copies.",
                schema, BindDirectShape),
        };
    }

    static RevitCommandArgs BindDirectShape(JsonElement args)
    {
        var category = args.GetProperty("category").GetString()!;
        RevitDirectShapeRules.BuiltInCategory(category);
        var shape = args.GetProperty("shape");
        DirectShapeGeometry geometry = shape.GetProperty("kind").GetString() == "box" ? BindDirectShapeBox(shape) : BindDirectShapeExtrusion(shape);
        var name = OptionalText(args, "name");
        RevitDirectShapeRules.CheckName(name, "args.name");
        return new DirectShapeCreateArgs(category, geometry, OptionalText(args, "material"), name);
    }

    static DirectShapeExtrusion BindDirectShapeExtrusion(JsonElement shape)
    {
        var vertices = new List<(double X, double Y)>();
        var index = 0;
        foreach (var vertex in shape.GetProperty("outline").EnumerateArray())
        {
            var field = "args.shape.outline[" + index + "]";
            vertices.Add((RevitDirectShapeRules.Coordinate(Number(vertex[0]), field + "[0]"), RevitDirectShapeRules.Coordinate(Number(vertex[1]), field + "[1]")));
            index++;
        }
        var outline = RevitCommandGeometry.CheckBoundary(vertices, "args.shape.outline");
        RevitDirectShapeRules.CheckOutline(outline, "args.shape.outline");
        var baseOffset = Length.FromMillimetres(0);
        if (shape.TryGetProperty("base_offset_mm", out var offset))
            baseOffset = Length.FromMillimetres(RevitDirectShapeRules.Coordinate(Number(offset), "args.shape.base_offset_mm"));
        var height = RevitDirectShapeRules.Height(Number(shape.GetProperty("height_mm")), "args.shape.height_mm");
        RevitDirectShapeRules.CheckTop(baseOffset, height, "args.shape.height_mm");
        return new DirectShapeExtrusion(shape.GetProperty("level").GetString()!, outline.Select(ToPoint).ToArray(), baseOffset, height);
    }

    static DirectShapeBox BindDirectShapeBox(JsonElement shape)
    {
        (double X, double Y, double Z) Corner(string name)
        {
            var corner = shape.GetProperty(name);
            var field = "args.shape." + name;
            return (RevitDirectShapeRules.Coordinate(Number(corner[0]), field + "[0]"), RevitDirectShapeRules.Coordinate(Number(corner[1]), field + "[1]"), RevitDirectShapeRules.Coordinate(Number(corner[2]), field + "[2]"));
        }
        var min = Corner("min");
        var max = Corner("max");
        RevitDirectShapeRules.CheckBox(min, max, "args.shape");
        static SitePoint At((double X, double Y, double Z) p) => new(Length.FromMillimetres(p.X), Length.FromMillimetres(p.Y), Length.FromMillimetres(p.Z));
        return new DirectShapeBox(At(min), At(max));
    }
}
