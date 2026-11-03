using Autodesk.Revit.DB;
using Bim.Core;
namespace Bim.Revit;

// P4-01 view commands Revit executor: query.view_family_types, query.view_templates, view.create_plan,
// view.create_section and view.create_3d. Same admission, receipts, transaction group, failure handling and
// readback-before-receipt as catalogue v1 (CommandsExecutor.cs); every decision that needs no Revit object is in
// CommandsViews.cs (unit-tested). Evidence level: compiled against the Revit 2025 reference assemblies in CI; nothing
// here has run in Revit.
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/):
//   ViewFamilyType.ViewFamily, Document.GetDefaultElementTypeId(ElementTypeGroup.ViewTypeFloorPlan / ViewTypeCeilingPlan /
//     ViewTypeSection / ViewType3D), View.IsTemplate / ViewType / Name / Scale / ViewTemplateId / IsValidViewTemplate
//   ViewPlan.Create(Document, ElementId viewFamilyTypeId, ElementId levelId), ViewPlan.GenLevel
//   ViewSection.CreateSection(Document, ElementId viewFamilyTypeId, BoundingBoxXYZ sectionBox), View.RightDirection /
//     UpDirection / ViewDirection / Origin / CropBox / CropBoxActive, BuiltInParameter.VIEWER_BOUND_OFFSET_FAR
//   View3D.CreateIsometric(Document, ElementId), View3D.SetSectionBox / GetSectionBox / IsSectionBoxActive /
//     IsPerspective / GetOrientation
// Unverified in Revit (owner session required): that CreateSection keeps the requested box frame (origin at the line
// midpoint, Max.Z = 0 as the cut plane, Min.Z = -depth as the far clip) and reports it back through RightDirection,
// ViewDirection, Origin, CropBox and Far Clip Offset; that SetSectionBox keeps an identity-oriented box exactly. The
// readback checks each of these and rolls back on a difference instead of reporting success. created lists the view
// only: the section marker Revit draws in other views is not diffed.
internal static partial class CommandExecutor
{
    static ViewFamilyType[] ViewFamilyTypes(Document d) => new FilteredElementCollector(d).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().ToArray();
    static View[] AllViews(Document d) => new FilteredElementCollector(d).OfClass(typeof(View)).Cast<View>().ToArray();
    static View[] ViewTemplates(Document d) => AllViews(d).Where(view => view.IsTemplate).ToArray();

    static ElementTypeGroup DefaultGroup(string family) => family switch
    {
        "floor_plan" => ElementTypeGroup.ViewTypeFloorPlan,
        "ceiling_plan" => ElementTypeGroup.ViewTypeCeilingPlan,
        "section" => ElementTypeGroup.ViewTypeSection,
        _ => ElementTypeGroup.ViewType3D,
    };

    static string? DefaultTypeUid(Document d, string family)
    {
        var id = d.GetDefaultElementTypeId(DefaultGroup(family));
        return id == ElementId.InvalidElementId ? null : d.GetElement(id)?.UniqueId;
    }

    static string QueryViews(Document d, RevitCommandEnvelope envelope) => envelope.Args switch
    {
        ViewFamilyTypeQueryArgs query => ViewFamilyTypesQuery(d, query.Family),
        EmptyQueryArgs when envelope.Command == "query.view_templates" =>
            CommandReadbackViews.ViewTemplatesJson(ViewTemplates(d).Select(template => new ViewTemplateRow(template.Name, template.UniqueId, template.ViewType.ToString()))),
        _ => throw new RevitCommandException(RevitCommandCodes.CommandUnknown, "command", envelope.Command),
    };

    static string ViewFamilyTypesQuery(Document d, string? family)
    {
        var defaults = new HashSet<string>(RevitViewRules.Families.Select(name => DefaultTypeUid(d, name)).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var wanted = family is null ? null : CommandRulesViews.ViewFamily(family);
        return CommandReadbackViews.ViewFamilyTypesJson(ViewFamilyTypes(d)
            .Where(type => wanted is null || type.ViewFamily.ToString() == wanted)
            .Select(type => new ViewFamilyTypeRow(type.Name, type.UniqueId, type.ViewFamily.ToString(), defaults.Contains(type.UniqueId))));
    }

    // The view family type, the resolved template (or null) and the name check shared by every view creator.
    sealed record ViewSetup(ViewFamilyType Type, View? Template, string ViewType);

    static ViewSetup PrepareView(Document d, string family, ViewOptions options)
    {
        var types = ViewFamilyTypes(d);
        var type = types[CommandRulesViews.PickType(types.Select(candidate => new ViewTypeCandidate(candidate.UniqueId, candidate.Name, candidate.ViewFamily.ToString())).ToArray(),
            options.Type, DefaultTypeUid(d, family), CommandRulesViews.ViewFamily(family), "args.type")];
        var viewType = CommandRulesViews.ViewType(family);
        View? template = null;
        if (options.Template is { } reference)
        {
            template = Resolve(ViewTemplates(d), candidate => new CommandCandidate(candidate.UniqueId, candidate.Name), reference, "args.view_template", "view template");
            CommandRulesViews.CheckTemplate(template.ViewType.ToString(), viewType);
        }
        CommandRulesViews.CheckNameFree(options.Name, AllViews(d).Where(view => !view.IsTemplate && view.ViewType.ToString() == viewType).Select(view => view.Name));
        return new ViewSetup(type, template, viewType);
    }

    // Name, template or scale, inside the write transaction.
    static void ApplyOptions(View view, ViewSetup setup, ViewOptions options)
    {
        if (options.Name is { } name) view.Name = name;
        if (setup.Template is { } template)
        {
            if (!view.IsValidViewTemplate(template.Id)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.view_template", "Revit refuses this view template for the new view");
            view.ViewTemplateId = template.Id;
        }
        if (options.Scale is { } scale) view.Scale = scale;
    }

    static ObservedView ObserveView(Document d, View view) => new(view.UniqueId, view.ViewType.ToString(), UniqueIdOf(d, view.GetTypeId()), view.Name, view.Scale,
        view.ViewTemplateId == ElementId.InvalidElementId ? null : d.GetElement(view.ViewTemplateId)?.UniqueId, view.IsTemplate, (view as ViewPlan)?.GenLevel?.UniqueId);

    static Vec3 Direction(XYZ vector) => new(vector.X, vector.Y, vector.Z);
    static Vec3 Position(XYZ point) => new(Mm(point.X), Mm(point.Y), Mm(point.Z));
    static XYZ Internal(Vec3 millimetres) => new(U(millimetres.X), U(millimetres.Y), U(millimetres.Z));
    static XYZ Unit(Vec3 vector) => new(vector.X, vector.Y, vector.Z);

    static WritePlan ViewPlanWrite(Document d, ViewPlanCreateArgs args)
    {
        var level = ResolveLevel(d, args.Level, "args.level");
        var setup = PrepareView(d, args.Kind, args.Options);
        ViewPlan? view = null;
        return new WritePlan(
            () =>
            {
                view = ViewPlan.Create(d, setup.Type.Id, level.Id);
                ApplyOptions(view, setup, args.Options);
            },
            () => view is { IsValidObject: true } created
                ? CommandReadbackViews.View(setup.ViewType, setup.Type.UniqueId, args.Options, setup.Template?.UniqueId, level.UniqueId, ObserveView(d, created))
                : "the plan view no longer exists",
            () => CommandReadbackViews.ViewJson(ObserveView(d, view!)),
            () => new[] { view!.UniqueId },
            Array.Empty<string>);
    }

    static WritePlan ViewSectionWrite(Document d, ViewSectionCreateArgs args)
    {
        var setup = PrepareView(d, "section", args.Options);
        var frame = CommandRulesViews.Section(args);
        var transform = Transform.Identity;
        transform.Origin = Internal(frame.Origin);
        transform.BasisX = Unit(frame.BasisX);
        transform.BasisY = Unit(frame.BasisY);
        transform.BasisZ = Unit(frame.BasisZ);
        var box = new BoundingBoxXYZ { Transform = transform, Min = Internal(frame.Min), Max = Internal(frame.Max) };
        ViewSection? view = null;
        ObservedSection Observe(ViewSection created)
        {
            var crop = created.CropBox;
            return new ObservedSection(Direction(created.RightDirection), Direction(created.UpDirection), Direction(created.ViewDirection), Position(created.Origin),
                Position(crop.Transform.OfPoint(crop.Min)), Position(crop.Transform.OfPoint(crop.Max)),
                LengthParameter(created, BuiltInParameter.VIEWER_BOUND_OFFSET_FAR), created.CropBoxActive);
        }
        return new WritePlan(
            () =>
            {
                view = ViewSection.CreateSection(d, setup.Type.Id, box);
                ApplyOptions(view, setup, args.Options);
            },
            () =>
            {
                if (view is not { IsValidObject: true } created) return "the section view no longer exists";
                return CommandReadbackViews.View(setup.ViewType, setup.Type.UniqueId, args.Options, setup.Template?.UniqueId, null, ObserveView(d, created))
                    ?? CommandReadbackViews.Section(args, Observe(created));
            },
            () => CommandReadbackViews.SectionJson(ObserveView(d, view!), Observe(view!)),
            () => new[] { view!.UniqueId },
            Array.Empty<string>);
    }

    static WritePlan View3DWrite(Document d, View3DCreateArgs args)
    {
        var setup = PrepareView(d, "three_d", args.Options);
        View3D? view = null;
        Observed3D Observe(View3D created)
        {
            var orientation = created.GetOrientation();
            var box = created.GetSectionBox();
            var transform = box?.Transform;
            return new Observed3D(created.IsPerspective, created.IsSectionBoxActive,
                transform is null ? null : Direction(transform.BasisX), transform is null ? null : Direction(transform.BasisY), transform is null ? null : Direction(transform.BasisZ),
                box is null || transform is null ? null : Position(transform.OfPoint(box.Min)), box is null || transform is null ? null : Position(transform.OfPoint(box.Max)),
                Direction(orientation.ForwardDirection), Direction(orientation.UpDirection));
        }
        return new WritePlan(
            () =>
            {
                var created = View3D.CreateIsometric(d, setup.Type.Id);
                view = created;
                if (args.SectionBox is { } requested)
                {
                    created.SetSectionBox(new BoundingBoxXYZ
                    {
                        Min = new XYZ(U(requested.MinX.Millimetres), U(requested.MinY.Millimetres), U(requested.MinZ.Millimetres)),
                        Max = new XYZ(U(requested.MaxX.Millimetres), U(requested.MaxY.Millimetres), U(requested.MaxZ.Millimetres)),
                    });
                    created.IsSectionBoxActive = true;
                }
                else created.IsSectionBoxActive = false;
                ApplyOptions(created, setup, args.Options);
            },
            () =>
            {
                if (view is not { IsValidObject: true } created) return "the 3D view no longer exists";
                return CommandReadbackViews.View(setup.ViewType, setup.Type.UniqueId, args.Options, setup.Template?.UniqueId, null, ObserveView(d, created))
                    ?? CommandReadbackViews.ThreeD(args.SectionBox, Observe(created));
            },
            () => CommandReadbackViews.ThreeDJson(ObserveView(d, view!), Observe(view!)),
            () => new[] { view!.UniqueId },
            Array.Empty<string>);
    }
}
