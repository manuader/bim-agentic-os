using Autodesk.Revit.DB;
using Bim.Core;
namespace Bim.Revit;

// direct_shape.create Revit executor (P9-09): one DirectShape solid (an extruded plan polygon or an axis-aligned box)
// created in the command's one write transaction, read back before commit and again after failure processing. Same
// admission, receipts, transaction group, failure handling and readback-before-receipt as catalogue v1
// (CommandsExecutor.cs Write); every decision that needs no Revit object is in CommandsDirectShape.cs (unit-tested)
// and the outline, height and box rules are checked in Core before the pipe. Evidence level: compiled against the
// Revit 2025 reference assemblies in CI; it has not run in Revit yet.
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/):
//   DirectShape.IsValidCategoryId(ElementId categoryId, Document), DirectShape.CreateElement(Document, ElementId
//     categoryId), DirectShape.ApplicationId / ApplicationDataId, DirectShape.SetShape(IList<GeometryObject>),
//     Element.Name (set and read back)
//   GeometryCreationUtilities.CreateExtrusionGeometry(IList<CurveLoop>, XYZ extrusionDir, double extrusionDist,
//     SolidOptions), SolidOptions(ElementId materialId, ElementId graphicsStyleId), CurveLoop, Line.CreateBound
//   Element.get_Geometry(Options), Solid.Volume, Solid.Faces, Face.MaterialElementId, Category.BuiltInCategory,
//     Element.get_BoundingBox(null)
// Unverified in Revit (owner session required): which of the offered categories DirectShape.IsValidCategoryId accepts
//   in the owner's template (topography in particular; a refused one is args_invalid before anything is created); that
//   the DirectShape's bounding box is the solid's exact extent within 1 mm (no padding); that the faces keep the
//   SolidOptions material (else a requested material reads back as a mismatch and rolls back); that setting Element.Name
//   on a DirectShape is kept.
internal static partial class CommandExecutor
{
    static WritePlan DirectShapeWrite(Document d, DirectShapeCreateArgs args)
    {
        var builtIn = RevitDirectShapeRules.BuiltInCategory(args.Category);
        var categoryId = Enum.TryParse<BuiltInCategory>(builtIn, out var category) ? new ElementId(category) : ElementId.InvalidElementId;
        CommandRulesDirectShape.CheckCategory(categoryId != ElementId.InvalidElementId && DirectShape.IsValidCategoryId(categoryId, d), args.Category, builtIn);
        var level = args.Shape is DirectShapeExtrusion extrusion ? ResolveLevel(d, extrusion.Level, "args.shape.level") : null;
        var material = args.Material is { } reference ? Resolve(Materials(d), candidate => new CommandCandidate(candidate.UniqueId, candidate.Name), reference, "args.material", "material") : null;
        var footprint = CommandRulesDirectShape.Footprint(args, level is null ? 0 : Mm(level.ProjectElevation));
        DirectShape? shape = null;

        // Read from the element's own geometry and box, not from the creation arguments.
        ObservedDirectShape Observe(DirectShape created)
        {
            var solids = new List<Solid>();
            if (created.get_Geometry(new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine }) is { } geometry)
                solids.AddRange(geometry.OfType<Solid>().Where(solid => solid.Volume > 0));
            var faces = solids.SelectMany(solid => solid.Faces.Cast<Face>())
                .Select(face => face.MaterialElementId == ElementId.InvalidElementId ? null : d.GetElement(face.MaterialElementId)?.UniqueId).ToArray();
            double? volume = solids.Count > 0 ? solids.Sum(solid => UnitUtils.ConvertFromInternalUnits(solid.Volume, UnitTypeId.CubicMeters) * 1e9) : null;
            return new ObservedDirectShape(created.UniqueId, created.Category?.BuiltInCategory.ToString(), created.Name, solids.Count, volume, faces, BoxOf(created));
        }

        return new WritePlan(
            () =>
            {
                var loop = new CurveLoop();
                var corners = footprint.Loop.Select(point => new XYZ(U(point.X), U(point.Y), U(footprint.BaseZMm))).ToArray();
                for (var i = 0; i < corners.Length; i++) loop.Append(Line.CreateBound(corners[i], corners[(i + 1) % corners.Length]));
                var options = new SolidOptions(material?.Id ?? ElementId.InvalidElementId, ElementId.InvalidElementId);
                var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, U(footprint.HeightMm), options);
                var created = DirectShape.CreateElement(d, categoryId);
                shape = created;
                created.ApplicationId = CommandRulesDirectShape.ApplicationId;
                created.ApplicationDataId = CommandRulesDirectShape.ApplicationDataId;
                created.SetShape(new List<GeometryObject> { solid });
                if (args.Name is { } name) created.Name = name;
            },
            () => shape is { IsValidObject: true } created
                ? CommandReadbackDirectShape.Shape(args, builtIn, material?.UniqueId, footprint, Observe(created))
                : "the DirectShape no longer exists",
            () => CommandReadbackDirectShape.ShapeJson(Observe(shape!), args),
            () => new[] { shape!.UniqueId },
            Array.Empty<string>);
    }
}
