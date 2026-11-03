using Autodesk.Revit.DB;
using Bim.Core;
using RevitElement = Autodesk.Revit.DB.Element;
namespace Bim.Revit;

// Site commands Revit executor: query.toposolid_types, toposolid.create and toposolid.create_subdivision. Same
// admission, receipts, transaction group, failure handling and readback-before-receipt as catalogue v1
// (CommandsExecutor.cs); every decision that needs no Revit object is in CommandsSite.cs (unit-tested).
// Evidence level: compiled against the Revit 2025 reference assemblies in CI; nothing here has run in Revit.
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/):
//   Toposolid.Create(Document, IList<XYZ> points, ElementId topoTypeId, ElementId levelId)  fb528545-b035-bfe2-71da-88baa9a979ff
//     (the other overloads take profile CurveLoops, with or without points; not exposed here)
//   Toposolid.CreateSubDivision(Document, IList<CurveLoop> profiles) (instance; the only 2025 overload, default type),
//     Toposolid.HostTopoId (InvalidElementId when not a subdivision), Toposolid.GetSubDivisionIds()
//   ToposolidType (HostObjAttributes.GetCompoundStructure().GetWidth() for the thickness)
//   Element.get_BoundingBox(null), BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, ParameterTypeId.ToposolidSubdivideHeight,
//     Definition.GetDataType() == SpecTypeId.Reference.Material
// Unverified in Revit (owner session required): that Toposolid.Create takes the point z values as model (project
//   elevation) coordinates rather than offsets from the level (the readback compares the bounding box top with the
//   highest point and rolls back on a difference); that the plan bounding box equals the points' convex hull extents
//   within 1 mm; that a subdivision exposes exactly one writable material-reference instance parameter (else
//   material refuses with transaction_failed) and that its sub-divide height is ParameterTypeId.ToposolidSubdivideHeight;
//   how CreateSubDivision treats an outline that extends past the host (clip, or an error that rolls back).
internal static partial class CommandExecutor
{
    static ToposolidType[] ToposolidTypes(Document d) => new FilteredElementCollector(d).OfClass(typeof(ToposolidType)).Cast<ToposolidType>().ToArray();

    static string QuerySite(Document d) =>
        CommandReadbackSite.ToposolidTypesJson(ToposolidTypes(d).Select(type => new ToposolidTypeRow(type.Name, type.UniqueId, type.GetCompoundStructure() is { } layers ? Mm(layers.GetWidth()) : null)));

    static BoxMm? BoxOf(RevitElement element) =>
        element.get_BoundingBox(null) is { } box ? new BoxMm(Mm(box.Min.X), Mm(box.Min.Y), Mm(box.Min.Z), Mm(box.Max.X), Mm(box.Max.Y), Mm(box.Max.Z)) : null;

    static string? CommentOf(RevitElement element) => element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS) is { StorageType: StorageType.String } comment ? comment.AsString() : null;

    static void SetComment(RevitElement element, string? comment, string what)
    {
        if (comment is null) return;
        var parameter = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        if (parameter is null || parameter.IsReadOnly || !parameter.Set(comment))
            throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.comment", "the Comments parameter of this " + what + " cannot be set");
    }

    static Parameter[] MaterialParameters(RevitElement element, bool writableOnly) => element.Parameters.Cast<Parameter>()
        .Where(parameter => parameter.StorageType == StorageType.ElementId && (!writableOnly || !parameter.IsReadOnly) && parameter.Definition is { } definition && SpecTypeId.Reference.Material.Equals(definition.GetDataType()))
        .OrderBy(parameter => parameter.Id.Value).ToArray();

    static string? HostUidOf(Document d, Toposolid toposolid) => toposolid.HostTopoId == ElementId.InvalidElementId ? null : UniqueIdOf(d, toposolid.HostTopoId);

    static WritePlan ToposolidWrite(Document d, ToposolidCreateArgs args)
    {
        var level = ResolveLevel(d, args.Level, "args.level");
        var type = Resolve(ToposolidTypes(d), candidate => new CommandCandidate(candidate.UniqueId, candidate.Name), args.Type, "args.type", "toposolid type");
        // Model coordinates: z is a project elevation, like Level.ProjectElevation.
        var points = args.Points.Select(point => new XYZ(U(point.X.Millimetres), U(point.Y.Millimetres), U(point.Z.Millimetres))).ToList();
        var requested = CommandRulesSite.Extents(args.Points.Select(point => (point.X.Millimetres, point.Y.Millimetres, point.Z.Millimetres)));
        Toposolid? toposolid = null;
        ObservedToposolid Observe(Toposolid created) => new(created.UniqueId, created.LevelId == ElementId.InvalidElementId ? null : UniqueIdOf(d, created.LevelId),
            UniqueIdOf(d, created.GetTypeId()), HostUidOf(d, created), BoxOf(created), CommentOf(created));
        return new WritePlan(
            () =>
            {
                toposolid = Toposolid.Create(d, points, type.Id, level.Id);
                SetComment(toposolid, args.Comment, "toposolid");
            },
            () => toposolid is { IsValidObject: true } created ? CommandReadbackSite.Toposolid(level.UniqueId, type.UniqueId, requested, args.Comment, Observe(created)) : "the toposolid no longer exists",
            () => CommandReadbackSite.ToposolidJson(Observe(toposolid!), args.Points.Count),
            () => new[] { toposolid!.UniqueId },
            Array.Empty<string>);
    }

    static WritePlan ToposolidSubdivisionWrite(Document d, ToposolidSubdivisionCreateArgs args)
    {
        var host = d.GetElement(args.HostUid) as Toposolid ?? throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.host_uid", "no toposolid with UniqueId " + args.HostUid);
        CommandRulesSite.CheckSubdivisionHost(host.HostTopoId != ElementId.InvalidElementId);
        var material = args.Material is { } reference ? Resolve(Materials(d), candidate => new CommandCandidate(candidate.UniqueId, candidate.Name), reference, "args.material", "material") : null;
        // The profile must be horizontal; it is drawn in the host level's plane.
        var z = d.GetElement(host.LevelId) is Level hostLevel ? hostLevel.ProjectElevation : 0;
        var corners = args.Outline.Select(point => At(point, z)).ToArray();
        var loop = new CurveLoop();
        for (var i = 0; i < corners.Length; i++) loop.Append(Line.CreateBound(corners[i], corners[(i + 1) % corners.Length]));
        var profiles = new List<CurveLoop> { loop };
        Toposolid? subdivision = null;
        ObservedSubdivision Observe(Toposolid created)
        {
            var materials = MaterialParameters(created, writableOnly: false);
            var materialUid = materials.Length == 1 && materials[0].AsElementId() is { } id && id != ElementId.InvalidElementId ? UniqueIdOf(d, id) : null;
            return new ObservedSubdivision(created.UniqueId, HostUidOf(d, created), host.IsValidObject && host.GetSubDivisionIds().Contains(created.Id), materialUid,
                created.GetParameter(ParameterTypeId.ToposolidSubdivideHeight) is { HasValue: true, StorageType: StorageType.Double } height ? Mm(height.AsDouble()) : null,
                BoxOf(created), CommentOf(created));
        }
        return new WritePlan(
            () =>
            {
                var created = host.CreateSubDivision(d, profiles);
                subdivision = created;
                if (material is not null)
                {
                    var writable = MaterialParameters(created, writableOnly: true);
                    var target = writable[CommandRulesSite.MaterialParameterIndex(writable.Length, "args.material")];
                    if (!target.Set(material.Id)) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.material", "the subdivision material cannot be set");
                }
                if (args.SubdivideHeight is { } height)
                {
                    var parameter = created.GetParameter(ParameterTypeId.ToposolidSubdivideHeight);
                    if (parameter is null || parameter.IsReadOnly || !parameter.Set(U(height.Millimetres)))
                        throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.subdivide_height_mm", "the sub-divide height of this subdivision cannot be set");
                }
                SetComment(created, args.Comment, "subdivision");
            },
            () => subdivision is { IsValidObject: true } created
                ? CommandReadbackSite.Subdivision(host.UniqueId, material?.UniqueId, args.SubdivideHeight?.Millimetres, args.Comment, Observe(created))
                : "the subdivision no longer exists",
            () => CommandReadbackSite.SubdivisionJson(Observe(subdivision!)),
            () => new[] { subdivision!.UniqueId },
            () => new[] { host.UniqueId });
    }
}
