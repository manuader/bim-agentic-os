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
//   Element.Pinned, Element.ViewSpecific, Element.GroupId, FamilyInstance.Host, Family.FamilyPlacementType
//     (OneLevelBasedHosted: a wall/floor/ceiling/roof-hosted family), Element.Category, Element.GetTypeId
// Unverified in Revit (owner session required): that moving a wall joined at its ends keeps its own location curve
//   rigid (Revit may extend the joined neighbours; if it trims the moved wall instead, the readback rolls back as
//   transaction_failed); that CopyElements of a wall copies its hosted openings into the copy (they are reported in
//   also_created); that a hosted door moved off its host's line keeps only the component along the host (the readback
//   then rolls back); that a face-based (work-plane) family whose face is gone reads Host null (it is not refused as
//   host_missing, only OneLevelBasedHosted families are); that floors, roofs and other sketch-based elements report a
//   bounding box that shifts exactly by the vector; which neighbours Revit modifies (not reported).
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
                    return new TransformLocation(TransformLocationKinds.Point, Triple(point.Point), null, PointRotation(point));
                case LocationCurve { Curve: { IsBound: true } curve }:
                    return new TransformLocation(TransformLocationKinds.Curve, Triple(curve.GetEndPoint(0)), Triple(curve.GetEndPoint(1)));
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        var box = element.get_BoundingBox(null);
        return box is null ? null : new TransformLocation(TransformLocationKinds.Box, Triple(box.Min), Triple(box.Max));
    }

    static TransformTargetFacts TransformFacts(RevitElement element) =>
        new(element.UniqueId, element.Pinned, element.ViewSpecific, element.GroupId is { } groupId && groupId != ElementId.InvalidElementId,
            element is FamilyInstance { Symbol.Family.FamilyPlacementType: FamilyPlacementType.OneLevelBasedHosted },
            element is FamilyInstance { Host: { IsValidObject: true } },
            ObserveLocation(element));

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

    static TransformLocation?[] LocationsNow(IEnumerable<RevitElement> elements) => elements.Select(element => element.IsValidObject ? ObserveLocation(element) : null).ToArray();

    static WritePlan MovePlan(Document d, ElementMoveArgs args)
    {
        var (targets, facts) = TransformTargets(d, args.ElementUids);
        var uids = targets.Select(element => element.UniqueId).ToArray();
        var before = facts.Select(fact => fact.Location!).ToArray();
        var vector = CommandRulesTransform.Vector(args.Vector);
        var ids = targets.Select(element => element.Id).ToList();
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
        var ids = targets.Select(element => element.Id).ToList();
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
