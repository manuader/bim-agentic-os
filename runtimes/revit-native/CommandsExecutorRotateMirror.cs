using Autodesk.Revit.DB;
using Bim.Core;
using System.Globalization;
using RevitElement = Autodesk.Revit.DB.Element;
namespace Bim.Revit;

// element.rotate / element.mirror Revit executor (P1-10): ElementTransformUtils.RotateElements about a vertical axis /
// MirrorElements in a vertical plane, in the command's one write transaction, read back before commit and again after
// failure processing. Same admission (copies-only guard), receipts, transaction group, failure handling and
// readback-before-receipt as catalogue v1 (CommandsExecutor.cs Write); the Revit facts of each target (location point,
// curve, sketch profile or box; pinned, view-specific, group, host) are read with the element.move readers
// (CommandsExecutorTransform.cs), and every decision that needs no Revit object (refusals, rotated and mirrored
// locations, the 1 mm comparisons, mapping mirrored copies and replacements to their sources, readback JSON) is in
// CommandsRotateMirror.cs (unit-tested); the argument rules (1 to 100 distinct UniqueIds, angle from 0.01 to under 360
// degrees, axis at least 1.2 mm) are checked in Core before the pipe. Evidence level: compiled against the Revit 2025
// reference assemblies in CI; nothing here has run in Revit.
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/):
//   ElementTransformUtils.RotateElements(Document, ICollection<ElementId>, Line axis, double angle) (radians,
//     counter-clockwise about +Z for an axis pointing up), Line.CreateBound(XYZ, XYZ),
//   ElementTransformUtils.CanMirrorElements(Document, ICollection<ElementId>),
//   ElementTransformUtils.MirrorElements(Document, ICollection<ElementId>, Plane, bool mirrorCopies) (returns the new
//     ids), Plane.CreateByNormalAndOrigin(XYZ, XYZ), Element.IsValidObject, Document.GetElement(ElementId)
// Unverified in Revit (owner session required): that RotateElements adds the angle to LocationPoint.Rotation (a
//   mirrored instance may report it the other way, which the readback rolls back as transaction_failed); that a rotated
//   wall joined at its ends keeps its own location curve rigid; that MirrorElements with mirrorCopies false mirrors the
//   originals in place (if Revit instead deletes them and returns replacements, each replacement is mapped to its
//   original by kind and mirror image and reported as new); that a mirrored wall's curve and a mirrored floor's sketch
//   come back as the mirror image either way round / in any order (accepted) and within 1 mm; that the copies Revit
//   makes of hosted doors and windows with their mirrored walls are among the returned ids (reported in also_created,
//   or as a door target's result); that CanMirrorElements answered per element agrees with Revit's answer for the whole
//   set; what LocationPoint.Rotation a mirrored instance reports (reported in the readback, never checked); which
//   neighbours Revit modifies (not reported).
internal static partial class CommandExecutor
{
    static (RevitElement[] Elements, TransformTargetFacts[] Facts, HashSet<string> Uids) PlanTargets(Document d, IReadOnlyList<string> uids)
    {
        var elements = uids.Select((uid, i) => ElementByUid(d, uid, "args.element_uids[" + i.ToString(CultureInfo.InvariantCulture) + "]")).ToArray();
        var facts = elements.Select(TransformFacts).ToArray();
        return (elements, facts, new HashSet<string>(facts.Select(fact => fact.Uid), StringComparer.OrdinalIgnoreCase));
    }

    static WritePlan RotatePlan(Document d, ElementRotateArgs args)
    {
        var (targets, facts, targetUids) = PlanTargets(d, args.ElementUids);
        for (var i = 0; i < facts.Length; i++) CommandRulesRotateMirror.CheckRotateTarget(facts[i], targetUids, "args.element_uids[" + i.ToString(CultureInfo.InvariantCulture) + "]");
        var uids = facts.Select(fact => fact.Uid).ToArray();
        var before = facts.Select(fact => fact.Location!).ToArray();
        var rotation = CommandRulesRotateMirror.RotationOf(args);
        var ids = TransformedIds(targets, facts);
        var bottom = new XYZ(U(rotation.CenterX), U(rotation.CenterY), 0);
        var axis = Line.CreateBound(bottom, bottom + XYZ.BasisZ);
        return new WritePlan(
            () => Refusing("args.element_uids", () => { ElementTransformUtils.RotateElements(d, ids, axis, rotation.AngleRad); return true; }),
            () => CommandReadbackRotateMirror.Rotated(uids, before, LocationsNow(targets), rotation),
            () => CommandReadbackRotateMirror.RotateJson(uids, before, LocationsNow(targets), rotation),
            Array.Empty<string>,
            () => uids);
    }

    static WritePlan MirrorPlan(Document d, ElementMirrorArgs args)
    {
        var (targets, facts, targetUids) = PlanTargets(d, args.ElementUids);
        for (var i = 0; i < facts.Length; i++)
        {
            var mirrorable = ElementTransformUtils.CanMirrorElements(d, new List<ElementId> { targets[i].Id });
            CommandRulesRotateMirror.CheckMirrorTarget(facts[i], mirrorable, targetUids, "args.element_uids[" + i.ToString(CultureInfo.InvariantCulture) + "]");
        }
        var sources = targets.Select((element, i) => new TransformCandidate(facts[i].Uid, CandidateKey(d, element), facts[i].Location)).ToArray();
        var mirror = CommandRulesRotateMirror.MirrorOf(args);
        var ids = TransformedIds(targets, facts);
        double ux = mirror.EndX - mirror.StartX, uy = mirror.EndY - mirror.StartY;
        var length = Math.Sqrt(ux * ux + uy * uy);
        // The plane's normal is the line's horizontal perpendicular; the line itself lies in the plane.
        var plane = Plane.CreateByNormalAndOrigin(new XYZ(-uy / length, ux / length, 0), new XYZ(U(mirror.StartX), U(mirror.StartY), 0));
        var created = Array.Empty<ElementId>();
        // Every new element Revit reports, read from the element itself (one failure processing deleted is skipped).
        TransformCandidate[] Created() => created.Select(id => d.GetElement(id)).Where(element => element is { IsValidObject: true })
            .Select(element => new TransformCandidate(element!.UniqueId, CandidateKey(d, element), ObserveLocation(element))).ToArray();
        // Each source as it is now: null when Revit deleted it (keep_original false may replace it).
        TransformCandidate?[] SourcesNow() => targets.Select((element, i) => element.IsValidObject ? sources[i] with { Location = ObserveLocation(element) } : null).ToArray();
        MirrorMapping Map() => CommandRulesRotateMirror.MapMirrored(sources, SourcesNow(), Created(), mirror, args.KeepOriginal);
        return new WritePlan(
            () => created = Refusing("args.element_uids", () => ElementTransformUtils.MirrorElements(d, ids, plane, args.KeepOriginal)).ToArray(),
            () => Map().Mismatch,
            () => CommandReadbackRotateMirror.MirrorJson(Map(), sources, SourcesNow(), Created(), mirror, args.KeepOriginal),
            () => CommandRulesRotateMirror.MirrorCreated(Map()),
            () => CommandRulesRotateMirror.MirrorModified(Map()));
    }
}
