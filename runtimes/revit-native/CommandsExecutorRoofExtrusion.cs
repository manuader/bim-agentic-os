using Autodesk.Revit.DB;
using Bim.Core;
namespace Bim.Revit;

// roof.create_extrusion Revit executor (P9-07): a reference plane and an extrusion roof created in the command's one
// write transaction, read back before commit and again after failure processing. Same admission, receipts,
// transaction group, failure handling and readback-before-receipt as catalogue v1 (CommandsExecutor.cs Write);
// every decision that needs no Revit object is in CommandsRoofExtrusion.cs (unit-tested) and the profile rules
// (open, connected, not self-intersecting) are checked in Core before the pipe. Evidence level: compiled against the
// Revit 2025 reference assemblies in CI; nothing here has run in Revit.
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/):
//   Document.Create.NewReferencePlane(XYZ bubbleEnd, XYZ freeEnd, XYZ cutVec, View) with cutVec = BasisZ (a vertical
//     plane through the plan line at the level's elevation), ReferencePlane.GetPlane()
//   Document.Create.NewExtrusionRoof(CurveArray profile, ReferencePlane refPlane, Level level, RoofType roofType,
//     double extrusionStart, double extrusionEnd); the profile curves (Line.CreateBound, Arc.Create(end0, end1,
//     pointOnArc)) lie in the reference plane
//   ExtrusionRoof.GetProfile() (ModelCurveArray; GeometryCurve, SketchPlane) for the independent readback,
//     BuiltInParameter.EXTRUSION_START_PARAM / EXTRUSION_END_PARAM, ROOF_CONSTRAINT_LEVEL_PARAM (Reference Level),
//     Element.get_BoundingBox(null)
// Unverified in Revit (owner session required): that the extents are measured along the reference plane's normal
//   (the executor mirrors them when that normal points opposite the request's left normal; the readback checks the
//   bounding box holds the profile swept to the requested side and is not nearer the other side, so a wrong sign
//   rolls back as transaction_failed; the extents read back through the same normal flag, so they alone cannot show
//   it; when the roof's box holds the profile swept to both sides (near-symmetric extents, thickness padding) the
//   wrong side cannot be detected at all, and a box nearer the other side by at most 2 x 25 mm is accepted); which
//   view NewReferencePlane needs for a vertical plane (a non-template plan view of the reference level, else the
//   active view); that an extrusion roof carries EXTRUSION_START_PARAM / EXTRUSION_END_PARAM (without them the
//   readback reports null extents and rolls back); whether Revit keeps the profile curves exactly (an arc split in two reads back as a mismatch); how
//   the roof type's thickness sits relative to the profile line.
internal static partial class CommandExecutor
{
    static View? ReferencePlaneView(Document d, Level level) =>
        new FilteredElementCollector(d).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .Where(view => !view.IsTemplate && view.GenLevel is { } genLevel && genLevel.Id == level.Id)
            .OrderBy(view => view.ViewType == ViewType.FloorPlan ? 0 : 1).ThenBy(view => view.Name, StringComparer.Ordinal).FirstOrDefault()
        ?? (d.ActiveView is { IsTemplate: false } active ? active : null);

    static WritePlan RoofExtrusionPlan(Document d, RoofExtrusionCreateArgs args)
    {
        var level = ResolveLevel(d, args.Level, "args.level");
        var type = Resolve(RoofTypes(d), roofType => new CommandCandidate(roofType.UniqueId, roofType.Name), args.Type, "args.type", "roof type");
        var view = ReferencePlaneView(d, level) ?? throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "no plan view of the reference level and no active view to create the reference plane in");
        var frame = CommandRulesRoofExtrusion.Frame(args);
        var levelMm = Mm(level.ProjectElevation);
        XYZ ModelOf(ProfileVertex vertex)
        {
            var plan = CommandRulesRoofExtrusion.PlanOf(frame, vertex.U.Millimetres);
            return new XYZ(U(plan.X), U(plan.Y), level.ProjectElevation + U(vertex.Z.Millimetres));
        }
        ExtrusionRoof? roof = null;
        ReferencePlane? plane = null;
        var agrees = true;

        // Read from the roof's own profile, not from the creation arguments.
        ObservedRoofExtrusion Observe(ExtrusionRoof created)
        {
            var edges = new List<ObservedProfileEdge>();
            var parallel = true;
            foreach (ModelCurve curve in created.GetProfile())
            {
                var geometry = curve.GeometryCurve;
                var points = new[] { geometry.GetEndPoint(0), geometry.GetEndPoint(1), geometry.Evaluate(0.5, true) }
                    .Select(point => CommandRulesRoofExtrusion.UzwOf(frame, Mm(point.X), Mm(point.Y), Mm(point.Z), levelMm)).ToArray();
                edges.Add(new ObservedProfileEdge(points[0].Uz, points[1].Uz, points[2].Uz, geometry is Arc, points.Max(point => Math.Abs(point.W))));
                var normal = curve.SketchPlane?.GetPlane().Normal;
                if (normal is null || Math.Abs(Math.Abs(normal.X * frame.Nx + normal.Y * frame.Ny) - 1) > 1e-6) parallel = false;
            }
            // The instance parameters Extrusion Start / Extrusion End; the 2025 API has no ExtrusionRoof properties for them.
            double? start = null, end = null;
            if (LengthParameter(created, BuiltInParameter.EXTRUSION_START_PARAM) is { } revitStart && LengthParameter(created, BuiltInParameter.EXTRUSION_END_PARAM) is { } revitEnd)
                (start, end) = CommandRulesRoofExtrusion.ContractExtents(revitStart, revitEnd, agrees);
            var box = created.get_BoundingBox(null);
            return new ObservedRoofExtrusion(created.UniqueId,
                LevelParameterUid(d, created, BuiltInParameter.ROOF_CONSTRAINT_LEVEL_PARAM) ?? UniqueIdOf(d, created.LevelId), UniqueIdOf(d, created.GetTypeId()),
                plane is { IsValidObject: true } live ? live.UniqueId : null, start, end, edges, parallel,
                box is null ? null : new[] { Mm(box.Min.X), Mm(box.Min.Y), Mm(box.Min.Z) }, box is null ? null : new[] { Mm(box.Max.X), Mm(box.Max.Y), Mm(box.Max.Z) }, levelMm);
        }

        return new WritePlan(
            () =>
            {
                var bubble = new XYZ(U(args.PlaneStart.X.Millimetres), U(args.PlaneStart.Y.Millimetres), level.ProjectElevation);
                var free = new XYZ(U(args.PlaneEnd.X.Millimetres), U(args.PlaneEnd.Y.Millimetres), level.ProjectElevation);
                var created = d.Create.NewReferencePlane(bubble, free, XYZ.BasisZ, view);
                plane = created;
                var normal = created.GetPlane().Normal;
                if (Math.Abs(normal.Z) > 1e-6) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.plane", "the new reference plane is not vertical");
                agrees = normal.X * frame.Nx + normal.Y * frame.Ny > 0;
                var profile = new CurveArray();
                foreach (var segment in args.Profile)
                {
                    profile.Append(segment is RoofProfileArc arc
                        ? Arc.Create(ModelOf(arc.Start), ModelOf(arc.End), ModelOf(arc.Mid))
                        : Line.CreateBound(ModelOf(segment.Start), ModelOf(segment.End)));
                }
                var (start, end) = CommandRulesRoofExtrusion.RevitExtents(args.ExtrusionStart.Millimetres, args.ExtrusionEnd.Millimetres, agrees);
                roof = d.Create.NewExtrusionRoof(profile, created, level, type, U(start), U(end));
            },
            () => roof is { IsValidObject: true } created
                ? CommandReadbackRoofExtrusion.Roof(level.UniqueId, type.UniqueId, args, Observe(created))
                : "the roof no longer exists",
            () => CommandReadbackRoofExtrusion.RoofJson(Observe(roof!), args),
            () => plane is { IsValidObject: true } live ? new[] { roof!.UniqueId, live.UniqueId } : new[] { roof!.UniqueId },
            Array.Empty<string>);
    }
}
