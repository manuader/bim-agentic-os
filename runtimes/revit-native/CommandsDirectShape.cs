using Bim.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Revit;

// direct_shape.create executor decisions, readback checks and readback JSON, in millimetres. Autodesk-free on purpose:
// tests/revit-commands source-links this file. CommandsExecutorDirectShape.cs reads the Revit facts (lengths, areas
// and volumes converted with UnitUtils) and calls the Revit API.

// The solid the executor builds: a counter-clockwise plan loop (seen from above) at BaseZMm (model z, the frame of a
// level's project elevation), extruded up by HeightMm.
public sealed record DirectShapeFootprint(IReadOnlyList<PlanPoint> Loop, double BaseZMm, double HeightMm);
// What Revit reports after creation. CategoryBuiltIn: Category.BuiltInCategory text ("OST_Site"), null without a
// category; Name: Element.Name; Solids: the number of solids with volume in the element's geometry; VolumeMm3: their
// total volume; FaceMaterialUids: the UniqueId of every face's material (null for a face without one); Box:
// Element.get_BoundingBox(null).
public sealed record ObservedDirectShape(string UniqueId, string? CategoryBuiltIn, string? Name, int Solids, double? VolumeMm3, IReadOnlyList<string?> FaceMaterialUids, BoxMm? Box);

public static class CommandRulesDirectShape
{
    // The bounding box of a DirectShape built from exact planar faces is compared within 1 mm (unit conversion only).
    public const double ToleranceMm = 1.0;
    public const string ApplicationId = "bim-agentic-os";
    public const string ApplicationDataId = "direct_shape.create";

    // Revit does not accept a DirectShape of the category in this document (DirectShape.IsValidCategoryId false, or the
    // built-in category does not exist in this Revit): refused before anything is created.
    public static void CheckCategory(bool valid, string category, string builtIn)
    {
        if (!valid) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.category", "Revit does not accept a DirectShape of category " + category + " (" + builtIn + ") in this document; use another category such as generic_model");
    }

    static double SignedArea(IReadOnlyList<PlanPoint> loop)
    {
        // Relative to the first vertex so precision holds far from the origin.
        double ox = loop[0].X, oy = loop[0].Y, twice = 0;
        for (var i = 0; i < loop.Count; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % loop.Count];
            twice += (a.X - ox) * (b.Y - oy) - (b.X - ox) * (a.Y - oy);
        }
        return twice / 2;
    }

    // The plan loop counter-clockwise: the outline as given when it already is, else reversed (first vertex kept).
    public static IReadOnlyList<PlanPoint> CounterClockwise(IReadOnlyList<PlanPoint> loop) =>
        SignedArea(loop) >= 0 ? loop.ToArray() : new[] { loop[0] }.Concat(loop.Skip(1).Reverse()).ToArray();

    // levelElevationMm: the extrusion level's project elevation (ignored for a box, whose z is already a model z).
    public static DirectShapeFootprint Footprint(DirectShapeCreateArgs args, double levelElevationMm) => args.Shape switch
    {
        DirectShapeExtrusion extrusion => new DirectShapeFootprint(CounterClockwise(extrusion.Outline.Select(point => new PlanPoint(point.X.Millimetres, point.Y.Millimetres)).ToArray()),
            levelElevationMm + extrusion.BaseOffset.Millimetres, extrusion.Height.Millimetres),
        DirectShapeBox box => new DirectShapeFootprint(new[]
            {
                new PlanPoint(box.Min.X.Millimetres, box.Min.Y.Millimetres), new PlanPoint(box.Max.X.Millimetres, box.Min.Y.Millimetres),
                new PlanPoint(box.Max.X.Millimetres, box.Max.Y.Millimetres), new PlanPoint(box.Min.X.Millimetres, box.Max.Y.Millimetres),
            }, box.Min.Z.Millimetres, box.Max.Z.Millimetres - box.Min.Z.Millimetres),
        _ => throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.shape", "unknown shape"),
    };

    public static BoxMm ExpectedBox(DirectShapeFootprint footprint) => new(footprint.Loop.Min(p => p.X), footprint.Loop.Min(p => p.Y), footprint.BaseZMm,
        footprint.Loop.Max(p => p.X), footprint.Loop.Max(p => p.Y), footprint.BaseZMm + footprint.HeightMm);

    public static double AreaMm2(DirectShapeFootprint footprint) => Math.Abs(SignedArea(footprint.Loop));

    public static double PerimeterMm(DirectShapeFootprint footprint) =>
        Enumerable.Range(0, footprint.Loop.Count).Sum(i => RevitCommandGeometry.Distance(footprint.Loop[i].X, footprint.Loop[i].Y, footprint.Loop[(i + 1) % footprint.Loop.Count].X, footprint.Loop[(i + 1) % footprint.Loop.Count].Y));

    public static double ExpectedVolumeMm3(DirectShapeFootprint footprint) => AreaMm2(footprint) * footprint.HeightMm;

    // Relative slack for floating-point and unit conversion on top of the side offset.
    public const double VolumeRelativeEpsilon = 1e-6;

    // The volume a 1 mm offset of the side faces could add or remove (perimeter x height x 1 mm), plus 1e-6 of the
    // expected volume. Top and bottom are not included: the bounding box already pins z within 1 mm, and including them
    // would let a thin solid's filled bounding rectangle pass for a concave outline.
    public static double VolumeToleranceMm3(DirectShapeFootprint footprint) => ToleranceMm * PerimeterMm(footprint) * footprint.HeightMm + VolumeRelativeEpsilon * ExpectedVolumeMm3(footprint);

    // The one material every face carries, or null when there are no faces or they differ.
    public static string? UniformMaterial(IReadOnlyList<string?> faces) =>
        faces.Count > 0 && faces.All(uid => uid is not null && string.Equals(uid, faces[0], StringComparison.Ordinal)) ? faces[0] : null;
}

// Each check returns null when the readback matches, else a plain-English mismatch; the executor then rolls the
// whole transaction group back and answers transaction_failed with that text.
public static class CommandReadbackDirectShape
{
    static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    static string Box(BoxMm box) => "[" + string.Join(", ", new[] { box.MinX, box.MinY, box.MinZ, box.MaxX, box.MaxY, box.MaxZ }.Select(v => F(Math.Round(v, 3)))) + "]";

    // builtIn: the requested category's BuiltInCategory text; materialUid: the resolved material's UniqueId or null.
    public static string? Shape(DirectShapeCreateArgs args, string builtIn, string? materialUid, DirectShapeFootprint footprint, ObservedDirectShape observed)
    {
        if (!string.Equals(observed.CategoryBuiltIn, builtIn, StringComparison.Ordinal)) return "the DirectShape category is " + (observed.CategoryBuiltIn ?? "none") + ", expected " + builtIn;
        if (observed.Solids != 1) return "the DirectShape geometry holds " + observed.Solids + " solids, expected 1";
        if (observed.Box is not { } box) return "the DirectShape has no bounding box";
        var expected = CommandRulesDirectShape.ExpectedBox(footprint);
        var pairs = new[] { (box.MinX, expected.MinX), (box.MinY, expected.MinY), (box.MinZ, expected.MinZ), (box.MaxX, expected.MaxX), (box.MaxY, expected.MaxY), (box.MaxZ, expected.MaxZ) };
        if (pairs.Any(pair => !(Math.Abs(pair.Item1 - pair.Item2) <= CommandRulesDirectShape.ToleranceMm)))
            return "the DirectShape bounding box " + Box(box) + " differs from the requested " + Box(expected) + " by more than 1 mm";
        var volume = CommandRulesDirectShape.ExpectedVolumeMm3(footprint);
        if (observed.VolumeMm3 is not { } observedVolume || !(Math.Abs(observedVolume - volume) <= CommandRulesDirectShape.VolumeToleranceMm3(footprint)))
            return "the DirectShape volume is " + (observed.VolumeMm3 is { } v ? F(Math.Round(v)) : "unreadable") + " mm3, expected " + F(Math.Round(volume)) + " mm3 (the requested outline times the height)";
        if (args.Name is { } name && !string.Equals(observed.Name, name, StringComparison.Ordinal)) return "the DirectShape name is '" + observed.Name + "', expected '" + name + "'";
        if (materialUid is not null && !string.Equals(CommandRulesDirectShape.UniformMaterial(observed.FaceMaterialUids), materialUid, StringComparison.Ordinal))
            return "the DirectShape faces do not all carry material " + materialUid;
        return null;
    }

    // ---- Readback JSON (snake_case, millimetres) ----

    public static string ShapeJson(ObservedDirectShape observed, DirectShapeCreateArgs args)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("direct_shape");
            writer.WriteString("unique_id", observed.UniqueId);
            writer.WriteString("category", args.Category);
            if (observed.CategoryBuiltIn is null) writer.WriteNull("built_in_category"); else writer.WriteString("built_in_category", observed.CategoryBuiltIn);
            writer.WriteString("shape_kind", args.Shape is DirectShapeBox ? "box" : "extrusion");
            if (observed.Name is null) writer.WriteNull("name"); else writer.WriteString("name", observed.Name);
            if (CommandRulesDirectShape.UniformMaterial(observed.FaceMaterialUids) is { } material) writer.WriteString("material_unique_id", material); else writer.WriteNull("material_unique_id");
            if (observed.VolumeMm3 is { } volume && double.IsFinite(volume)) writer.WriteNumber("volume_mm3", volume); else writer.WriteNull("volume_mm3");
            if (observed.Box is { } box)
            {
                writer.WriteStartObject("bounding_box_mm");
                writer.WriteStartArray("min");
                writer.WriteNumberValue(box.MinX);
                writer.WriteNumberValue(box.MinY);
                writer.WriteNumberValue(box.MinZ);
                writer.WriteEndArray();
                writer.WriteStartArray("max");
                writer.WriteNumberValue(box.MaxX);
                writer.WriteNumberValue(box.MaxY);
                writer.WriteNumberValue(box.MaxZ);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            else writer.WriteNull("bounding_box_mm");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
