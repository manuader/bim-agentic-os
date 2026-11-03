using Autodesk.Revit.DB;
using Bim.Core;
using RevitElement = Autodesk.Revit.DB.Element;
namespace Bim.Revit;

// wall.set_profile Revit executor: replace the elevation profile of a straight basic wall through its profile sketch,
// keeping the wall (same UniqueId, hosted openings kept). Same admission, receipts, transaction group, failure
// handling and readback-before-receipt as catalogue v1 (CommandsExecutor.cs); every decision that needs no Revit
// object is in CommandsWallProfile.cs (unit-tested). Evidence level: compiled against the Revit 2025 reference
// assemblies in CI; nothing here has run in Revit.
//
// Why: the owner's Revit 2025.2 (25.2.0.38) has no Wall.AddAttachment, so a gable end wall cannot be attached to the
// roof by API; its profile is edited instead. Deleting and recreating the wall with
// Wall.Create(Document, IList<Curve>, ElementId, ElementId, bool) would change its UniqueId and drop hosted openings,
// so it is not used.
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/):
//   Wall.CanHaveProfileSketch(), Wall.SketchId, Wall.CreateProfileSketch() (2022+; inside a transaction)
//   SketchEditScope(Document, string), IsPermitted, Start(ElementId sketchId), Commit(IFailuresPreprocessor), Cancel(),
//     IsActive (2022+); inside the scope a Transaction deletes the sketch's CurveElements (Sketch.GetAllElements()) and
//     draws new lines with Document.Create.NewModelCurve(Curve, SketchPlane) on Sketch.SketchPlane
//   Sketch.Profile (CurveArrArray) for the independent readback; Wall.FindInserts(true, false, false, false) for the
//     hosted doors, windows and openings; Element.get_BoundingBox(null)
// Unverified in Revit (owner session required): that SketchEditScope may start inside the command's TransactionGroup
//   (StairsEditScope is used the same way); that CreateProfileSketch's sketch plane is the wall's vertical plane
//   (checked: a plane that is not vertical or not parallel to the wall fails with transaction_failed); that Revit keeps
//   the location line and the hosted openings when the profile spans the same length; whether Revit then rewrites Base
//   Offset and Unconnected Height from the profile (reported, not checked); how wall joins at the corners clean up
//   against the new outline; that a wall attached to a roof or floor reports CanHaveProfileSketch false.
internal static partial class CommandExecutor
{
    static string WriteWallProfile(Document d, CommandRequest request, ReceiptStore store, WallSetProfileArgs args)
    {
        var envelope = request.Envelope;
        var wall = d.GetElement(args.WallUid) as Wall ?? throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.wall_uid", "no wall with UniqueId " + args.WallUid);
        var curve = (wall.Location as LocationCurve)?.Curve ?? throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.wall_uid", "the wall has no location curve");
        CommandRulesWallProfile.CheckWall(wall.WallType.Kind.ToString(), curve is not Line, wall.CanHaveProfileSketch());
        if (wall.GroupId != ElementId.InvalidElementId) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.wall_uid", "the wall is a group member; edit the group or ungroup it first");
        var line = (Line)curve;
        var level = d.GetElement(wall.LevelId) as Level ?? throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "the wall has no base level");
        // u is measured along the horizontal wall direction from the location-line start; z above the base level.
        var origin = line.GetEndPoint(0);
        var direction = new XYZ(line.Direction.X, line.Direction.Y, 0).Normalize();
        var baseZ = level.ProjectElevation;
        ProfilePointMm UzOf(XYZ point) => new(Mm((point - origin).DotProduct(direction)), Mm(point.Z - baseZ));
        XYZ ModelOf(ProfilePointMm point) => new XYZ(origin.X, origin.Y, baseZ) + direction * U(point.U) + XYZ.BasisZ * U(point.Z);

        var requested = CommandRulesWallProfile.Requested(args);
        CommandRulesWallProfile.CheckSpan(requested, Mm(line.Length));
        var inserts = wall.FindInserts(true, false, false, false).Select(id => d.GetElement(id)).Where(element => element is not null).Select(element => InsertExtent(element!, UzOf)).ToArray();
        CommandRulesWallProfile.CheckInserts(requested, inserts);
        var start = Plan(line.GetEndPoint(0));
        var end = Plan(line.GetEndPoint(1));

        ObservedWallProfile Observe()
        {
            var sketch = wall.SketchId == ElementId.InvalidElementId ? null : d.GetElement(wall.SketchId) as Sketch;
            var edges = new List<ProfileEdgeMm>();
            var loops = 0;
            if (sketch is not null)
            {
                foreach (CurveArray loop in sketch.Profile)
                {
                    loops++;
                    foreach (Curve edge in loop) edges.Add(new ProfileEdgeMm(UzOf(edge.GetEndPoint(0)), UzOf(edge.GetEndPoint(1)), edge is Line));
                }
            }
            var now = (wall.Location as LocationCurve)?.Curve;
            var nan = new PlanPoint(double.NaN, double.NaN);
            return new ObservedWallProfile(wall.UniqueId, sketch?.UniqueId, edges, loops, now is null ? nan : Plan(now.GetEndPoint(0)), now is null ? nan : Plan(now.GetEndPoint(1)),
                wall.get_BoundingBox(null) is { } box ? Mm(box.Max.Z - baseZ) : null,
                LengthParameter(wall, BuiltInParameter.WALL_BASE_OFFSET), LengthParameter(wall, BuiltInParameter.WALL_USER_HEIGHT_PARAM));
        }

        var failures = new CommandFailures();
        var createdSketch = false;
        using var group = new TransactionGroup(d, "BIM Agentic OS " + envelope.Command + " " + envelope.OperationId);
        if (group.Start() != TransactionStatus.Started) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "transaction group did not start");
        try
        {
            var sketchId = wall.SketchId;
            if (sketchId == ElementId.InvalidElementId)
            {
                ProfileTransaction(d, "BIM Agentic OS " + envelope.Command + " sketch", failures, () => sketchId = wall.CreateProfileSketch().Id);
                createdSketch = true;
            }
            using (var scope = new SketchEditScope(d, "BIM Agentic OS " + envelope.Command))
            {
                if (!scope.IsPermitted) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "Revit does not permit a sketch edit scope in this state");
                scope.Start(sketchId);
                try
                {
                    ProfileTransaction(d, "BIM Agentic OS " + envelope.Command, failures, () =>
                    {
                        var sketch = d.GetElement(sketchId) as Sketch ?? throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "the wall's profile sketch is missing");
                        var sketchPlane = sketch.SketchPlane;
                        var plane = sketchPlane.GetPlane();
                        var normal = plane.Normal;
                        if (Math.Abs(normal.Z) > 1e-6 || Math.Abs(normal.DotProduct(direction)) > 1e-6)
                            throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "the wall's profile sketch plane is not the wall's vertical plane");
                        // Points on the sketch plane (it may be offset from the location line, never rotated).
                        XYZ OnPlane(XYZ point) => point - normal * (point - plane.Origin).DotProduct(normal);
                        var old = sketch.GetAllElements().Where(id => d.GetElement(id) is CurveElement).ToList();
                        if (old.Count > 0) d.Delete(old);
                        var corners = requested.Select(point => OnPlane(ModelOf(point))).ToArray();
                        for (var i = 0; i < corners.Length; i++) d.Create.NewModelCurve(Line.CreateBound(corners[i], corners[(i + 1) % corners.Length]), sketchPlane);
                    });
                    scope.Commit(failures);
                }
                catch
                {
                    if (scope.IsActive) scope.Cancel();
                    throw;
                }
            }
            if (failures.Errors.Count > 0) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, string.Join("; ", failures.Errors));
            if (!wall.IsValidObject) throw Mismatch("the wall no longer exists");
            var observed = Observe();
            if (CommandReadbackWallProfile.Profile(requested, start, end, observed) is { } mismatch) throw Mismatch(mismatch);
            var created = createdSketch && observed.SketchUid is { } sketchUid ? new[] { sketchUid } : Array.Empty<string>();
            var response = CommandResponses.Success(request, created, new[] { wall.UniqueId }, CommandReadbackWallProfile.ProfileJson(observed), failures.Warnings.ToArray());
            return Persist(d, request, store, group, response);
        }
        catch
        {
            if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
            throw;
        }
    }

    // One transaction with the command's failure preprocessor; an error or a failed commit is transaction_failed.
    static void ProfileTransaction(Document d, string name, CommandFailures failures, Action apply)
    {
        using var tx = new Transaction(d, name);
        if (tx.Start() != TransactionStatus.Started) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "transaction did not start");
        var options = tx.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(failures);
        options.SetClearAfterRollback(true);
        tx.SetFailureHandlingOptions(options);
        try
        {
            apply();
            var status = tx.Commit();
            if (status != TransactionStatus.Committed)
                throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, failures.Errors.Count > 0 ? string.Join("; ", failures.Errors) : "Revit did not commit the transaction (" + status + ")");
        }
        catch
        {
            if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
            throw;
        }
    }

    // The u/z extents of a hosted insert's bounding box (conservative on a diagonal wall: the box is axis-aligned).
    static WallInsertExtent InsertExtent(RevitElement element, Func<XYZ, ProfilePointMm> uzOf)
    {
        var box = element.get_BoundingBox(null) ?? throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.profile", "the wall hosts " + element.UniqueId + ", which has no bounding box to check against the new profile");
        var corners = new[] { box.Min.X, box.Max.X }.SelectMany(x => new[] { box.Min.Y, box.Max.Y }.SelectMany(y => new[] { box.Min.Z, box.Max.Z }.Select(z => uzOf(box.Transform.OfPoint(new XYZ(x, y, z)))))).ToArray();
        return new WallInsertExtent(element.UniqueId, element.Category?.Name ?? element.GetType().Name, corners.Min(c => c.U), corners.Max(c => c.U), corners.Min(c => c.Z), corners.Max(c => c.Z));
    }
}
