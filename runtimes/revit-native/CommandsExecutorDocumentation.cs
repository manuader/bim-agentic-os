using Autodesk.Revit.DB;
using Bim.Core;
using RevitElement = Autodesk.Revit.DB.Element;
namespace Bim.Revit;

// P4-02 documentation commands Revit executor: query.title_blocks, dimension.create_linear, tag.create,
// schedule.create, sheet.create and sheet.place_view. Same admission, receipts, transaction group, failure handling
// and readback-before-receipt as catalogue v1 (CommandsExecutor.cs); every decision that needs no Revit object is in
// CommandsDocumentation.cs (unit-tested). Evidence level: compiled against the Revit 2025 reference assemblies in
// CI; nothing here has run in Revit.
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/):
//   Document.Create.NewDimension(View, Line, ReferenceArray), Dimension.References / Value / Curve, new Reference(Element)
//     for grids, HostObjectUtils.GetSideFaces(HostObject, ShellLayerType), Element.GetGeometryObjectFromReference,
//     PlanarFace.Origin / FaceNormal
//   IndependentTag.Create(Document, ElementId ownerViewId, Reference, bool addLeader, TagMode.TM_ADDBY_CATEGORY,
//     TagOrientation, XYZ), IndependentTag.GetTaggedLocalElementIds / HasLeader / TagOrientation / TagHeadPosition /
//     IsOrphaned, Element.get_BoundingBox(View)
//   ViewSchedule.IsValidCategoryForSchedule / CreateSchedule(Document, ElementId), ScheduleDefinition.GetSchedulableFields /
//     AddField / RemoveField / GetFieldCount / GetField / AddSortGroupField / GetSortGroupFieldCount / GetSortGroupField /
//     CategoryId, SchedulableField.GetName(Document), ScheduleField.GetName / FieldId, ScheduleSortGroupField
//   ViewSheet.Create(Document, ElementId titleBlockTypeId), ViewSheet.SheetNumber / IsPlaceholder
//   Viewport.CanAddViewToSheet / Create(Document, ElementId sheetId, ElementId viewId, XYZ), Viewport.SheetId / ViewId /
//     GetBoxCenter, ScheduleSheetInstance.Create(Document, ElementId sheetId, ElementId scheduleId, XYZ),
//     ScheduleSheetInstance.ScheduleId / Point, ViewSchedule.IsTitleblockRevisionSchedule
// Unverified in Revit (owner session required): that NewDimension measures grid references (new Reference(grid)) and
// wall side faces with Dimension.Value equal to the plan distance; that a tag keeps the requested head point (with or
// without leader); that CreateSchedule then RemoveField/AddField leaves exactly the requested fields with
// ScheduleField.GetName equal to SchedulableField.GetName; that Viewport.GetBoxCenter and ScheduleSheetInstance.Point
// equal the requested sheet point. Each is read back and rolls back on a difference instead of reporting success.
// created lists the new element only: the title block instance of a new sheet and schedule sheet instances' segments
// are not listed.
internal static partial class CommandExecutor
{
    static FamilySymbol[] TitleBlocks(Document d) => new FilteredElementCollector(d).OfClass(typeof(FamilySymbol))
        .OfCategory(BuiltInCategory.OST_TitleBlocks).Cast<FamilySymbol>().ToArray();

    static ViewSheet[] Sheets(Document d) => new FilteredElementCollector(d).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToArray();

    static string QueryDocumentation(Document d, RevitCommandEnvelope envelope) => envelope.Args switch
    {
        EmptyQueryArgs when envelope.Command == "query.title_blocks" =>
            CommandReadbackDocumentation.TitleBlocksJson(TitleBlocks(d).Select(symbol => new TitleBlockRow(symbol.Family.Name, symbol.Name, symbol.UniqueId, symbol.IsActive))),
        _ => throw new RevitCommandException(RevitCommandCodes.CommandUnknown, "command", envelope.Command),
    };

    static RevitElement ElementByUid(Document d, string uid, string field) =>
        d.GetElement(uid) ?? throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, field, "no element with UniqueId " + uid);

    // A plan view for dimensions and tags, and the elevation (internal units) of the plane they are drawn in.
    static (View View, double Z) AnnotationView(Document d, string uid)
    {
        var view = ElementByUid(d, uid, "args.view_uid") as View ?? throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.view_uid", "the element " + uid + " is not a view");
        CommandRulesDocumentation.CheckAnnotationView(view.ViewType.ToString(), view.IsTemplate);
        return (view, (view as ViewPlan)?.GenLevel?.ProjectElevation ?? 0);
    }

    // A dimension reference: a straight grid (its line) or a straight wall (its exterior or interior side face).
    static (PlanDatum Datum, Reference Reference) DimensionReference(Document d, string uid, string field, string wallFace)
    {
        switch (ElementByUid(d, uid, field))
        {
            case Grid grid:
                if (grid.IsCurved || grid.Curve is not Line line) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field, "the grid is curved; dimension straight grids");
                return (CommandRulesDocumentation.LineDatum(grid.UniqueId, "grid", Plan(line.GetEndPoint(0)), Plan(line.GetEndPoint(1)), field), new Reference(grid));
            case Wall wall:
                if ((wall.Location as LocationCurve)?.Curve is not Line) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field, "the wall is not straight");
                var faces = HostObjectUtils.GetSideFaces(wall, wallFace == "interior" ? ShellLayerType.Interior : ShellLayerType.Exterior);
                if (faces.Count != 1) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field, "the wall has " + faces.Count + " " + wallFace + " side faces, expected one");
                if (wall.GetGeometryObjectFromReference(faces[0]) is not PlanarFace face) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field, "the wall " + wallFace + " face is not planar");
                var normal = face.FaceNormal;
                return (CommandRulesDocumentation.FaceDatum(wall.UniqueId, "wall_" + wallFace, Plan(face.Origin), normal.X, normal.Y, normal.Z, field), faces[0]);
            default:
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field, "dimension references must be grids or walls");
        }
    }

    static WritePlan DimensionWrite(Document d, DimensionLinearCreateArgs args)
    {
        var (view, z) = AnnotationView(d, args.ViewUid);
        var (first, firstReference) = DimensionReference(d, args.FirstUid, "args.first_uid", args.WallFace);
        var (second, secondReference) = DimensionReference(d, args.SecondUid, "args.second_uid", args.WallFace);
        var layout = CommandRulesDocumentation.Layout(first, second, args.LineStart is null ? null : Requested(args.LineStart), args.LineEnd is null ? null : Requested(args.LineEnd));
        var line = Line.CreateBound(new XYZ(U(layout.Start.X), U(layout.Start.Y), z), new XYZ(U(layout.End.X), U(layout.End.Y), z));
        var references = new ReferenceArray();
        references.Append(firstReference);
        references.Append(secondReference);
        Dimension? dimension = null;
        ObservedDimension Observe(Dimension created)
        {
            var uids = new List<string>();
            foreach (Reference reference in created.References) uids.Add(UniqueIdOf(d, reference.ElementId));
            double? value = created.Value;
            var direction = (created.Curve as Line)?.Direction;
            return new ObservedDimension(created.UniqueId, UniqueIdOf(d, created.OwnerViewId), uids, value is { } feet ? Mm(feet) : null, direction?.X, direction?.Y);
        }
        return new WritePlan(
            () => dimension = d.Create.NewDimension(view, line, references),
            () => dimension is { IsValidObject: true } created ? CommandReadbackDocumentation.Dimension(view.UniqueId, first, second, layout, Observe(created)) : "the dimension no longer exists",
            () => CommandReadbackDocumentation.DimensionJson(Observe(dimension!)),
            () => new[] { dimension!.UniqueId },
            Array.Empty<string>);
    }

    static WritePlan TagWrite(Document d, TagCreateArgs args)
    {
        var (view, z) = AnnotationView(d, args.ViewUid);
        var element = ElementByUid(d, args.ElementUid, "args.element_uid");
        var category = element.Category;
        CommandRulesDocumentation.CheckTaggable(category is null || category.BuiltInCategory == BuiltInCategory.INVALID ? null : category.BuiltInCategory.ToString(),
            category is not null && category.CategoryType == CategoryType.Model, element is ElementType);
        PlanPoint head;
        if (args.Head is not null) head = Requested(args.Head);
        else
        {
            var box = element.get_BoundingBox(view) ?? throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.head", "the element has no extent in the view; give head");
            head = Plan((box.Min + box.Max) / 2);
        }
        var orientationName = CommandRulesDocumentation.TagOrientation(args.Orientation);
        var orientation = orientationName == "Vertical" ? TagOrientation.Vertical : TagOrientation.Horizontal;
        IndependentTag? tag = null;
        ObservedTag Observe(IndependentTag created) => new(created.UniqueId, UniqueIdOf(d, created.OwnerViewId),
            created.GetTaggedLocalElementIds().Select(id => UniqueIdOf(d, id)).ToArray(), created.HasLeader, created.TagOrientation.ToString(), Plan(created.TagHeadPosition), created.IsOrphaned);
        return new WritePlan(
            () => tag = IndependentTag.Create(d, view.Id, new Reference(element), args.Leader, TagMode.TM_ADDBY_CATEGORY, orientation, new XYZ(U(head.X), U(head.Y), z)),
            () => tag is { IsValidObject: true } created ? CommandReadbackDocumentation.Tag(view.UniqueId, element.UniqueId, head, args.Leader, orientationName, Observe(created)) : "the tag no longer exists",
            () => CommandReadbackDocumentation.TagJson(Observe(tag!)),
            () => new[] { tag!.UniqueId },
            Array.Empty<string>);
    }

    static WritePlan ScheduleWrite(Document d, ScheduleCreateArgs args)
    {
        if (!Enum.TryParse<BuiltInCategory>(args.BuiltInCategory, false, out var builtIn) || !Enum.IsDefined(builtIn) || builtIn == BuiltInCategory.INVALID)
            throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.category", "Revit has no BuiltInCategory named " + args.BuiltInCategory);
        var categoryId = new ElementId(builtIn);
        if (!ViewSchedule.IsValidCategoryForSchedule(categoryId)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.category", args.BuiltInCategory + " cannot be scheduled");
        CommandRulesViews.CheckNameFree(args.Name, AllViews(d).Where(view => view.IsTemplate || view is ViewSchedule).Select(view => view.Name));
        ViewSchedule? schedule = null;
        ObservedSchedule Observe(ViewSchedule created)
        {
            var definition = created.Definition;
            var fields = Enumerable.Range(0, definition.GetFieldCount()).Select(i => definition.GetField(i).GetName()).ToArray();
            var sorts = Enumerable.Range(0, definition.GetSortGroupFieldCount()).Select(i => definition.GetField(definition.GetSortGroupField(i).FieldId).GetName()).ToArray();
            var scheduled = Category.GetCategory(d, definition.CategoryId);
            return new ObservedSchedule(created.UniqueId, created.Name, scheduled is null || scheduled.BuiltInCategory == BuiltInCategory.INVALID ? null : scheduled.BuiltInCategory.ToString(), created.IsTemplate, fields, sorts);
        }
        return new WritePlan(
            () =>
            {
                var created = ViewSchedule.CreateSchedule(d, categoryId);
                schedule = created;
                var definition = created.Definition;
                while (definition.GetFieldCount() > 0) definition.RemoveField(0);
                var available = definition.GetSchedulableFields();
                var picks = CommandRulesDocumentation.PickFields(available.Select(field => field.GetName(d)).ToArray(), args.Fields);
                for (var i = 0; i < picks.Length; i++)
                {
                    var added = definition.AddField(available[picks[i]]);
                    if (string.Equals(args.SortBy, args.Fields[i], StringComparison.Ordinal))
                        definition.AddSortGroupField(new ScheduleSortGroupField(added.FieldId, ScheduleSortOrder.Ascending));
                }
                if (args.Name is { } name) created.Name = name;
            },
            () => schedule is { IsValidObject: true } created ? CommandReadbackDocumentation.Schedule(args, Observe(created)) : "the schedule no longer exists",
            () => CommandReadbackDocumentation.ScheduleJson(Observe(schedule!)),
            () => new[] { schedule!.UniqueId },
            Array.Empty<string>);
    }

    static WritePlan SheetWrite(Document d, SheetCreateArgs args)
    {
        FamilySymbol? titleBlock = args.TitleBlock is { } reference
            ? Resolve(TitleBlocks(d), candidate => new CommandCandidate(candidate.UniqueId, candidate.Name, CommandReferences.Qualified(candidate.Family.Name, candidate.Name)), reference, "args.title_block", "title block type")
            : null;
        CommandRulesDocumentation.CheckSheetNumberFree(args.Number, Sheets(d).Select(sheet => sheet.SheetNumber));
        ViewSheet? sheet = null;
        var activated = false;
        ObservedSheet Observe(ViewSheet created) => new(created.UniqueId, created.SheetNumber, created.Name,
            new FilteredElementCollector(d).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType()
                .Where(instance => instance.OwnerViewId == created.Id).Select(instance => UniqueIdOf(d, instance.GetTypeId())).ToArray());
        return new WritePlan(
            () =>
            {
                if (titleBlock is not null) activated = EnsureActive(d, titleBlock);
                var created = ViewSheet.Create(d, titleBlock?.Id ?? ElementId.InvalidElementId);
                sheet = created;
                created.SheetNumber = args.Number;
                created.Name = args.Name;
            },
            () => sheet is { IsValidObject: true } created ? CommandReadbackDocumentation.Sheet(args.Number, args.Name, titleBlock?.UniqueId, Observe(created)) : "the sheet no longer exists",
            () => CommandReadbackDocumentation.SheetJson(Observe(sheet!)),
            () => new[] { sheet!.UniqueId },
            () => activated ? new[] { titleBlock!.UniqueId } : Array.Empty<string>());
    }

    static WritePlan PlaceViewWrite(Document d, SheetPlaceViewArgs args)
    {
        var sheet = ElementByUid(d, args.SheetUid, "args.sheet_uid") as ViewSheet ?? throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.sheet_uid", "the element " + args.SheetUid + " is not a sheet");
        if (sheet.IsPlaceholder) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.sheet_uid", "the sheet is a placeholder sheet");
        var view = ElementByUid(d, args.ViewUid, "args.view_uid") as View ?? throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.view_uid", "the element " + args.ViewUid + " is not a view");
        var schedule = view as ViewSchedule;
        string? placedOn = null;
        if (schedule is null)
        {
            var viewport = new FilteredElementCollector(d).OfClass(typeof(Viewport)).Cast<Viewport>().FirstOrDefault(candidate => candidate.ViewId == view.Id);
            if (viewport is not null) placedOn = (d.GetElement(viewport.SheetId) as ViewSheet)?.SheetNumber ?? viewport.SheetId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var kind = CommandRulesDocumentation.CheckPlaceable(view.ViewType.ToString(), view.IsTemplate, schedule is not null, schedule?.IsTitleblockRevisionSchedule ?? false, placedOn,
            schedule is not null || Viewport.CanAddViewToSheet(d, sheet.Id, view.Id));
        var center = Requested(args.Center);
        var point = new XYZ(U(center.X), U(center.Y), 0);
        RevitElement? placed = null;
        ObservedPlacement Observe(RevitElement created) => created switch
        {
            Viewport viewport => new ObservedPlacement(viewport.UniqueId, CommandRulesDocumentation.ViewportKind, UniqueIdOf(d, viewport.SheetId), UniqueIdOf(d, viewport.ViewId), Plan(viewport.GetBoxCenter())),
            ScheduleSheetInstance instance => new ObservedPlacement(instance.UniqueId, CommandRulesDocumentation.ScheduleKind, UniqueIdOf(d, instance.OwnerViewId), UniqueIdOf(d, instance.ScheduleId), Plan(instance.Point)),
            _ => new ObservedPlacement(created.UniqueId, created.GetType().Name, null, null, null),
        };
        return new WritePlan(
            () => placed = kind == CommandRulesDocumentation.ScheduleKind
                ? (RevitElement)ScheduleSheetInstance.Create(d, sheet.Id, view.Id, point)
                : Viewport.Create(d, sheet.Id, view.Id, point),
            () => placed is { IsValidObject: true } created ? CommandReadbackDocumentation.Placement(kind, sheet.UniqueId, view.UniqueId, center, Observe(created)) : "the placed view no longer exists",
            () => CommandReadbackDocumentation.PlacementJson(Observe(placed!)),
            () => new[] { placed!.UniqueId },
            Array.Empty<string>);
    }
}
