using Autodesk.Revit.DB;
using Bim.Core;
using System.Globalization;
using RevitElement = Autodesk.Revit.DB.Element;
namespace Bim.Revit;

// element.move / element.copy Revit executor (P1-09): ElementTransformUtils.MoveElements / CopyElements of the targets
// by one vector in the command's one write transaction, read back before commit and again after failure processing.
// Same admission (copies-only guard), receipts, transaction group, failure handling and readback-before-receipt as
// catalogue v1 (CommandsExecutor.cs Write); every decision that needs no Revit object (target refusals, expected
// locations, the 1 mm comparison, mapping each copy to its source, readback JSON) is in CommandsTransform.cs
// (unit-tested), and the argument rules (1 to 100 distinct UniqueIds, finite vector from 1.2 mm to 1 km) are checked
// in Core before the pipe. Evidence level: compiled against the Revit 2025 reference assemblies in CI; nothing here
// has run in Revit.
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/):
//   ElementTransformUtils.MoveElements(Document, ICollection<ElementId>, XYZ),
//   ElementTransformUtils.CopyElements(Document, ICollection<ElementId>, XYZ) (returns the new ids, in no stated order,
//     including elements Revit copies with the targets such as hosted doors and windows)
//   Element.Location (LocationPoint.Point / Rotation, LocationCurve.Curve.GetEndPoint), Element.get_BoundingBox(null),
//   Element.GetDependentElements(ElementClassFilter(typeof(Sketch))), Sketch.OwnerId / Profile (the sketch profile of
//     floors, roofs, ceilings), Element.Pinned, Element.ViewSpecific, Element.GroupId, FamilyInstance.Host, Family.FamilyPlacementType
//     (OneLevelBasedHosted: a wall/floor/ceiling/roof-hosted family), Element.Category, Element.GetTypeId
// Unverified in Revit (owner session required): that moving a wall joined at its ends keeps its own location curve
//   rigid (Revit may extend the joined neighbours; if it trims the moved wall instead, the readback rolls back as
//   transaction_failed); that CopyElements of a wall copies its hosted openings into the copy (they are reported in
//   also_created); that a hosted door moved off its host's line keeps only the component along the host (the readback
//   then rolls back); that a face-based (work-plane) family whose face is gone reads Host null (it is not refused as
//   host_missing, only OneLevelBasedHosted families are); that floors, roofs and other sketch-based elements report a
//   sketch profile whose curves keep their order and shift exactly by the vector (a copy's sketch is compared in the
//   same order as its source's); that an element owns exactly one Sketch among its dependents (several or none fall back
//   to the box); elements read back by their bounding box (no location point or curve and no single sketch) are
//   compared by a box that joins, cuts and regeneration can change, so a correct move of such an element may be rolled
//   back as a mismatch; that a door whose host wall is also a target is moved or copied exactly once by its host (it is
//   left out of the MoveElements / CopyElements ids and read back like any target); which neighbours Revit modifies
//   (not reported).
internal static partial class CommandExecutor
{
    static double[] Triple(XYZ point) => new[] { Mm(point.X), Mm(point.Y), Mm(point.Z) };
    static XYZ Xyz(double[] millimetres) => new(U(millimetres[0]), U(millimetres[1]), U(millimetres[2]));

    static double? PointRotation(LocationPoint point)
    {
        try { return point.Rotation; }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
    }

    // The element's location point or curve; otherwise its model bounding box; null when it has none (types, views,
    // data storage). Read again after the transform, from the element itself.
    static TransformLocation? ObserveLocation(RevitElement element)
    {
        if (element is ElementType or View) return null;
        try
        {
            switch (element.Location)
            {
                case LocationPoint point:
                    return new TransformLocation(TransformLocationKinds.Point, Triple(point.Point), null, PointRotation(point), null, element is FamilyInstance instance ? instance.Mirrored : null);
                case LocationCurve { Curve: { IsBound: true } curve }:
                    return new TransformLocation(TransformLocationKinds.Curve, Triple(curve.GetEndPoint(0)), Triple(curve.GetEndPoint(1)));
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        if (SketchProfile(element) is { } profile) return profile;
        var box = element.get_BoundingBox(null);
        return box is null ? null : new TransformLocation(TransformLocationKinds.Box, Triple(box.Min), Triple(box.Max));
    }

    // The end points of the element's own sketch profile curves (Sketch.Profile, in sketch order), for floors, roofs,
    // ceilings and other sketch-based elements: unlike the bounding box, joins and cuts do not change them. Null when
    // the element owns no single sketch with curves.
    static TransformLocation? SketchProfile(RevitElement element)
    {
        try
        {
            var sketches = element.GetDependentElements(new ElementClassFilter(typeof(Sketch)))
                .Select(id => element.Document.GetElement(id)).OfType<Sketch>().Where(sketch => sketch.OwnerId == element.Id).ToArray();
            if (sketches.Length != 1) return null;
            var points = new List<double[]>();
            foreach (CurveArray loop in sketches[0].Profile)
                foreach (Curve curve in loop)
                {
                    points.Add(Triple(curve.GetEndPoint(0)));
                    points.Add(Triple(curve.GetEndPoint(1)));
                }
            return points.Count == 0 ? null : new TransformLocation(TransformLocationKinds.Profile, points[0], null, null, points);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
    }

    static TransformTargetFacts TransformFacts(RevitElement element) =>
        new(element.UniqueId, element.Pinned, element.ViewSpecific, element.GroupId is { } groupId && groupId != ElementId.InvalidElementId,
            element is FamilyInstance { Symbol.Family.FamilyPlacementType: FamilyPlacementType.OneLevelBasedHosted },
            element is FamilyInstance { Host: { IsValidObject: true } },
            ObserveLocation(element),
            element is FamilyInstance { Host: { IsValidObject: true } host } ? host.UniqueId : null);

    // What a copy shares with its source: Revit class, category and type.
    static string CandidateKey(Document d, RevitElement element) =>
        element.GetType().Name + "|" + (element.Category is { } category ? category.Id.Value.ToString(CultureInfo.InvariantCulture) : "-") + "|" + UniqueIdOf(d, element.GetTypeId());

    // Resolves every target and refuses the first one that cannot be transformed, before any transaction.
    static (RevitElement[] Elements, TransformTargetFacts[] Facts) TransformTargets(Document d, IReadOnlyList<string> uids)
    {
        var elements = uids.Select((uid, i) => ElementByUid(d, uid, "args.element_uids[" + i.ToString(CultureInfo.InvariantCulture) + "]")).ToArray();
        var facts = elements.Select(TransformFacts).ToArray();
        for (var i = 0; i < facts.Length; i++) CommandRulesTransform.CheckTarget(facts[i], "args.element_uids[" + i.ToString(CultureInfo.InvariantCulture) + "]");
        return (elements, facts);
    }

    // A hosted target whose host is also a target is carried by the host (CommandRulesTransform.TransformIds); every
    // target is still read back (a copied door is then the copy Revit made with its wall's copy).
    static List<ElementId> TransformedIds(RevitElement[] targets, TransformTargetFacts[] facts)
    {
        var transformed = new HashSet<string>(CommandRulesTransform.TransformIds(facts).Transformed, StringComparer.OrdinalIgnoreCase);
        return targets.Where(element => transformed.Contains(element.UniqueId)).Select(element => element.Id).ToList();
    }

    static TransformLocation?[] LocationsNow(IEnumerable<RevitElement> elements) => elements.Select(element => element.IsValidObject ? ObserveLocation(element) : null).ToArray();

    static WritePlan MovePlan(Document d, ElementMoveArgs args)
    {
        var (targets, facts) = TransformTargets(d, args.ElementUids);
        var uids = targets.Select(element => element.UniqueId).ToArray();
        var before = facts.Select(fact => fact.Location!).ToArray();
        var vector = CommandRulesTransform.Vector(args.Vector);
        var ids = TransformedIds(targets, facts);
        return new WritePlan(
            () => Refusing("args.element_uids", () => { ElementTransformUtils.MoveElements(d, ids, Xyz(vector)); return true; }),
            () => CommandReadbackTransform.Moved(uids, before, LocationsNow(targets), vector),
            () => CommandReadbackTransform.MoveJson(uids, before, LocationsNow(targets), vector),
            Array.Empty<string>,
            () => uids);
    }

    static WritePlan CopyPlan(Document d, ElementCopyArgs args)
    {
        var (targets, facts) = TransformTargets(d, args.ElementUids);
        var sources = targets.Select((element, i) => new TransformCandidate(element.UniqueId, CandidateKey(d, element), facts[i].Location)).ToArray();
        var vector = CommandRulesTransform.Vector(args.Vector);
        var ids = TransformedIds(targets, facts);
        var copied = Array.Empty<ElementId>();
        // Every new element Revit reports, read from the element itself (a copy failure processing deleted is skipped,
        // so a mapped copy that is gone fails the readback).
        TransformCandidate[] Copies() => copied.Select(id => d.GetElement(id)).Where(element => element is { IsValidObject: true })
            .Select(element => new TransformCandidate(element!.UniqueId, CandidateKey(d, element), ObserveLocation(element))).ToArray();
        return new WritePlan(
            () => copied = Refusing("args.element_uids", () => ElementTransformUtils.CopyElements(d, ids, Xyz(vector))).ToArray(),
            () => CommandReadbackTransform.Copied(sources, LocationsNow(targets), Copies(), vector),
            () =>
            {
                var copies = Copies();
                return CommandReadbackTransform.CopyJson(CommandRulesTransform.MapCopies(sources, copies, vector), copies, vector);
            },
            () => CommandRulesTransform.Created(CommandRulesTransform.MapCopies(sources, Copies(), vector)),
            Array.Empty<string>);
    }
}
