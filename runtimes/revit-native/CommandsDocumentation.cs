using Bim.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Revit;

// P4-02 documentation commands executor decisions, dimension layout, readback checks and readback JSON, in
// millimetres. Autodesk-free on purpose: tests/revit-commands source-links this file. CommandsExecutorDocumentation.cs
// reads the Revit facts (lengths converted with UnitUtils) and calls the Revit API. Revit enum values travel as their
// ToString() text (ViewType "FloorPlan", TagOrientation "Horizontal", BuiltInCategory "OST_Walls").

public sealed record TitleBlockRow(string Family, string Type, string UniqueId, bool Active);
// A dimension reference seen in plan as a vertical plane: a point on it (mm) and its unit normal. Kind: grid,
// wall_exterior or wall_interior.
public sealed record PlanDatum(string UniqueId, string Kind, PlanPoint Point, double NormalX, double NormalY);
// The dimension line handed to Revit (plan, mm) and the distance the dimension must measure.
public sealed record DimensionLayout(PlanPoint Start, PlanPoint End, double ExpectedMm);
// What Revit reports for a created dimension. ViewUid: OwnerViewId; ReferenceUids: the elements of
// Dimension.References in order; ValueMm: Dimension.Value (null for a multi-segment or unreadable value);
// DirectionX/Y: the plan direction of the dimension line, null when unreadable.
public sealed record ObservedDimension(string UniqueId, string? ViewUid, IReadOnlyList<string> ReferenceUids, double? ValueMm, double? DirectionX, double? DirectionY);
// A created tag: OwnerViewId, GetTaggedLocalElementIds, HasLeader, TagOrientation, TagHeadPosition (plan) and IsOrphaned.
public sealed record ObservedTag(string UniqueId, string? ViewUid, IReadOnlyList<string> TaggedUids, bool HasLeader, string Orientation, PlanPoint? Head, bool Orphaned);
// A created schedule: name, the BuiltInCategory of its definition (null when unreadable), the field names in column
// order and the sort/group field names in order.
public sealed record ObservedSchedule(string UniqueId, string Name, string? BuiltInCategory, bool IsTemplate, IReadOnlyList<string> Fields, IReadOnlyList<string> SortFields);
// A created sheet: SheetNumber, Name and the type UniqueIds of the title block instances owned by the sheet.
public sealed record ObservedSheet(string UniqueId, string Number, string Name, IReadOnlyList<string> TitleBlockTypeUids);
// A placed view: Kind viewport (Viewport: SheetId, ViewId, GetBoxCenter) or schedule (ScheduleSheetInstance:
// OwnerViewId, ScheduleId, Point); Center in sheet millimetres.
public sealed record ObservedPlacement(string UniqueId, string Kind, string? SheetUid, string? ViewUid, PlanPoint? Center);

public static class CommandRulesDocumentation
{
    public const string ViewportKind = "viewport";
    public const string ScheduleKind = "schedule";
    // Unit-vector components agree within this (well above double/feet round-trip noise).
    public const double DirectionTolerance = 1e-6;
    // A requested dimension line may be off perpendicular by this much at each end (the readback rounding of a
    // coordinate), so its allowed sine grows as the line gets shorter.
    public const double LineEndToleranceMm = 1e-3;

    // Plan views: dimensions and tags take plan millimetre points, drawn in the plane of the view's level.
    public static readonly IReadOnlyList<string> AnnotationViewTypes = new[] { "FloorPlan", "CeilingPlan", "EngineeringPlan", "AreaPlan" };
    // Elements a tag by category cannot take: rooms, areas and spaces have their own tag classes.
    public static readonly IReadOnlyList<string> UntaggableCategories = new[] { "OST_Rooms", "OST_Areas", "OST_MEPSpaces" };
    // View types that are never placed on a sheet.
    public static readonly IReadOnlyList<string> UnplaceableViewTypes = new[] { "DrawingSheet", "ProjectBrowser", "SystemBrowser", "Internal", "Undefined" };

    public static void CheckAnnotationView(string viewType, bool isTemplate)
    {
        if (isTemplate) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.view_uid", "the view is a view template");
        if (!AnnotationViewTypes.Contains(viewType)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.view_uid", "the view is a " + viewType + " view; give a plan view (" + string.Join(", ", AnnotationViewTypes) + ")");
    }

    public static string TagOrientation(string orientation) => orientation switch
    {
        "horizontal" => "Horizontal",
        "vertical" => "Vertical",
        _ => throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.orientation", "unknown tag orientation " + orientation),
    };

    // A straight grid from start to end: a point on it and the unit normal (the direction turned +90 degrees).
    public static PlanDatum LineDatum(string uniqueId, string kind, PlanPoint start, PlanPoint end, string field)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (!(length >= RevitCommandGeometry.MinSegmentMm)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field, "the reference line is shorter than 1.2 mm");
        return new PlanDatum(uniqueId, kind, start, -dy / length, dx / length);
    }

    // A planar face: a point on it and its 3D normal, which must be horizontal (the face vertical) to be measured in plan.
    public static PlanDatum FaceDatum(string uniqueId, string kind, PlanPoint point, double nx, double ny, double nz, string field)
    {
        var length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (!(length > 0) || Math.Abs(nz / length) > DirectionTolerance) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field, "the wall face is not vertical");
        var plan = Math.Sqrt(nx * nx + ny * ny);
        return new PlanDatum(uniqueId, kind, point, nx / plan, ny / plan);
    }

    static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;

    // The references must be parallel and at least 1.2 mm apart. A requested line must run along their normal (its
    // ends within LineEndToleranceMm of a perpendicular line);
    // without one, the line goes from the first reference's point straight across to the second reference.
    public static DimensionLayout Layout(PlanDatum first, PlanDatum second, PlanPoint? lineStart, PlanPoint? lineEnd)
    {
        if (Math.Abs(Cross(first.NormalX, first.NormalY, second.NormalX, second.NormalY)) > DirectionTolerance)
            throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.second_uid", "the two references are not parallel");
        var along = (second.Point.X - first.Point.X) * first.NormalX + (second.Point.Y - first.Point.Y) * first.NormalY;
        var distance = Math.Abs(along);
        if (!(distance >= RevitCommandGeometry.MinSegmentMm)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.second_uid", "the two references are less than 1.2 mm apart");
        if (lineStart is not null && lineEnd is not null)
        {
            double dx = lineEnd.X - lineStart.X, dy = lineEnd.Y - lineStart.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (Math.Abs(Cross(dx / length, dy / length, first.NormalX, first.NormalY)) > DirectionTolerance + 2 * LineEndToleranceMm / length)
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.line", "the dimension line must be perpendicular to the references");
            return new DimensionLayout(lineStart, lineEnd, distance);
        }
        return new DimensionLayout(first.Point, new PlanPoint(first.Point.X + first.NormalX * along, first.Point.Y + first.NormalY * along), distance);
    }

    // builtInCategory: the element's BuiltInCategory name (null without category); model: CategoryType Model.
    public static void CheckTaggable(string? builtInCategory, bool model, bool elementType)
    {
        if (elementType) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.element_uid", "the element is an element type; tag an instance");
        if (builtInCategory is null || !model) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.element_uid", "only model elements with a category can be tagged");
        if (UntaggableCategories.Contains(builtInCategory)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.element_uid", builtInCategory + " elements need their own tag type, not a tag by category");
    }

    // The schedulable field (by its displayed name, ordinal) for each requested name, in request order. Missing or
    // ambiguous names are reference_not_found at args.fields[i].
    public static int[] PickFields(IReadOnlyList<string> available, IReadOnlyList<string> requested)
    {
        var picks = new int[requested.Count];
        for (var i = 0; i < requested.Count; i++)
        {
            var matches = Enumerable.Range(0, available.Count).Where(index => string.Equals(available[index], requested[i], StringComparison.Ordinal)).ToArray();
            if (matches.Length == 0) throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.fields[" + i + "]", "the category has no schedulable field named " + requested[i]);
            if (matches.Length > 1) throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.fields[" + i + "]", matches.Length + " schedulable fields are named " + requested[i]);
            picks[i] = matches[0];
        }
        return picks;
    }

    // Compared ignoring case (stricter than Revit) so a collision fails closed here instead of as a Revit throw.
    public static void CheckSheetNumberFree(string number, IEnumerable<string> existing)
    {
        if (existing.Contains(number, StringComparer.OrdinalIgnoreCase)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.number", "a sheet numbered '" + number + "' (ignoring case) already exists");
    }

    // Returns the placement kind. placedOnSheet: the number of the sheet a non-schedule view is already on, else null
    // (ignored for legends);
    // canAdd: Viewport.CanAddViewToSheet (ignored for schedules, which may appear on several sheets).
    public static string CheckPlaceable(string viewType, bool isTemplate, bool isSchedule, bool isRevisionSchedule, string? placedOnSheet, bool canAdd)
    {
        if (isTemplate) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.view_uid", "a view template cannot be placed on a sheet");
        if (UnplaceableViewTypes.Contains(viewType)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.view_uid", "a " + viewType + " view cannot be placed on a sheet");
        if (isSchedule)
        {
            if (isRevisionSchedule) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.view_uid", "a title block revision schedule cannot be placed on a sheet");
            return ScheduleKind;
        }
        // A legend may appear on many sheets: only Viewport.CanAddViewToSheet decides.
        if (placedOnSheet is not null && viewType != "Legend") throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.view_uid", "the view is already placed on sheet " + placedOnSheet);
        if (!canAdd) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.view_uid", "Revit refuses to add this view to the sheet (Viewport.CanAddViewToSheet)");
        return ViewportKind;
    }
}

// Each check returns null when the readback matches, else a plain-English mismatch; the executor then rolls the
// whole transaction group back and answers transaction_failed with that text.
public static class CommandReadbackDocumentation
{
    static bool Near(double a, double b) => CommandGeometry.Near(a, b);
    static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    static string F(double? value) => value is { } number ? F(number) : "unreadable";
    static string F(PlanPoint? point) => point is null ? "unreadable" : "(" + F(point.X) + ", " + F(point.Y) + ")";
    static string Uid(string? value) => value ?? "unreadable";
    static bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    static bool SameSet(IReadOnlyList<string> observed, params string[] expected) =>
        observed.Count == expected.Length && expected.All(uid => observed.Count(other => Same(other, uid)) == 1);

    public static string? Dimension(string viewUid, PlanDatum first, PlanDatum second, DimensionLayout layout, ObservedDimension observed)
    {
        if (!Same(observed.ViewUid, viewUid)) return "dimension view is " + Uid(observed.ViewUid) + ", expected " + viewUid;
        if (!SameSet(observed.ReferenceUids, first.UniqueId, second.UniqueId)) return "dimension references are " + string.Join(", ", observed.ReferenceUids) + ", expected " + first.UniqueId + " and " + second.UniqueId;
        if (observed.ValueMm is not { } value || !Near(value, layout.ExpectedMm)) return "dimension value is " + F(observed.ValueMm) + " mm, expected " + F(layout.ExpectedMm);
        if (observed.DirectionX is not { } x || observed.DirectionY is not { } y || Math.Abs(x * first.NormalY - y * first.NormalX) > CommandRulesDocumentation.DirectionTolerance)
            return "dimension line direction is (" + F(observed.DirectionX) + ", " + F(observed.DirectionY) + "), expected along (" + F(first.NormalX) + ", " + F(first.NormalY) + ")";
        return null;
    }

    public static string? Tag(string viewUid, string elementUid, PlanPoint head, bool leader, string orientation, ObservedTag observed)
    {
        if (!Same(observed.ViewUid, viewUid)) return "tag view is " + Uid(observed.ViewUid) + ", expected " + viewUid;
        if (observed.Orphaned) return "the tag is orphaned";
        if (!SameSet(observed.TaggedUids, elementUid)) return "tag tags " + (observed.TaggedUids.Count == 0 ? "nothing" : string.Join(", ", observed.TaggedUids)) + ", expected " + elementUid;
        if (observed.HasLeader != leader) return "tag leader is " + (observed.HasLeader ? "on" : "off") + ", expected " + (leader ? "on" : "off");
        if (observed.Orientation != orientation) return "tag orientation is " + observed.Orientation + ", expected " + orientation;
        if (observed.Head is null || !CommandGeometry.Near(observed.Head, head)) return "tag head is at " + F(observed.Head) + ", expected " + F(head);
        return null;
    }

    public static string? Schedule(ScheduleCreateArgs args, ObservedSchedule observed)
    {
        if (observed.IsTemplate) return "the created schedule is a view template";
        if (observed.BuiltInCategory != args.BuiltInCategory) return "schedule category is " + Uid(observed.BuiltInCategory) + ", expected " + args.BuiltInCategory;
        if (!observed.Fields.SequenceEqual(args.Fields, StringComparer.Ordinal)) return "schedule fields are [" + string.Join(", ", observed.Fields) + "], expected [" + string.Join(", ", args.Fields) + "]";
        var sorts = args.SortBy is { } sort ? new[] { sort } : Array.Empty<string>();
        if (!observed.SortFields.SequenceEqual(sorts, StringComparer.Ordinal)) return "schedule sorts by [" + string.Join(", ", observed.SortFields) + "], expected [" + string.Join(", ", sorts) + "]";
        if (args.Name is { } name && !string.Equals(observed.Name, name, StringComparison.Ordinal)) return "schedule name is '" + observed.Name + "', expected '" + name + "'";
        return null;
    }

    // titleBlockTypeUid: the resolved title block type, null for a sheet without title block.
    public static string? Sheet(string number, string name, string? titleBlockTypeUid, ObservedSheet observed)
    {
        if (!string.Equals(observed.Number, number, StringComparison.Ordinal)) return "sheet number is '" + observed.Number + "', expected '" + number + "'";
        if (!string.Equals(observed.Name, name, StringComparison.Ordinal)) return "sheet name is '" + observed.Name + "', expected '" + name + "'";
        if (titleBlockTypeUid is null ? observed.TitleBlockTypeUids.Count != 0 : !SameSet(observed.TitleBlockTypeUids, titleBlockTypeUid))
            return "sheet title blocks are [" + string.Join(", ", observed.TitleBlockTypeUids) + "], expected [" + (titleBlockTypeUid ?? "") + "]";
        return null;
    }

    public static string? Placement(string kind, string sheetUid, string viewUid, PlanPoint center, ObservedPlacement observed)
    {
        if (observed.Kind != kind) return "placed as a " + observed.Kind + ", expected a " + kind;
        if (!Same(observed.SheetUid, sheetUid)) return "placed on sheet " + Uid(observed.SheetUid) + ", expected " + sheetUid;
        if (!Same(observed.ViewUid, viewUid)) return "placed view is " + Uid(observed.ViewUid) + ", expected " + viewUid;
        if (observed.Center is null || !CommandGeometry.Near(observed.Center, center)) return "placed at " + F(observed.Center) + " on the sheet, expected " + F(center);
        return null;
    }

    // ---- Readback JSON (snake_case, millimetres, rows in a stable order) ----

    static string Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void Number(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number && double.IsFinite(number)) writer.WriteNumber(name, number);
        else writer.WriteNull(name);
    }

    static void Text(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    static void Point(Utf8JsonWriter writer, string name, PlanPoint? point)
    {
        if (point is null) { writer.WriteNull(name); return; }
        writer.WriteStartArray(name);
        writer.WriteNumberValue(point.X);
        writer.WriteNumberValue(point.Y);
        writer.WriteEndArray();
    }

    static void Strings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    static string Wrapped(string name, Action<Utf8JsonWriter> members) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject(name);
        members(writer);
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    // "reference" is the text sheet.create accepts as title_block.
    public static string TitleBlocksJson(IEnumerable<TitleBlockRow> rows) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartArray("title_blocks");
        foreach (var row in rows.OrderBy(row => row.Family, StringComparer.Ordinal).ThenBy(row => row.Type, StringComparer.Ordinal).ThenBy(row => row.UniqueId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("family", row.Family);
            writer.WriteString("type", row.Type);
            writer.WriteString("reference", CommandReferences.Qualified(row.Family, row.Type));
            writer.WriteString("unique_id", row.UniqueId);
            writer.WriteBoolean("active", row.Active);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    public static string DimensionJson(ObservedDimension dimension) => Wrapped("dimension", writer =>
    {
        writer.WriteString("unique_id", dimension.UniqueId);
        Text(writer, "view_unique_id", dimension.ViewUid);
        Strings(writer, "reference_unique_ids", dimension.ReferenceUids);
        Number(writer, "value_mm", dimension.ValueMm);
        if (dimension.DirectionX is { } x && dimension.DirectionY is { } y) Point(writer, "direction", new PlanPoint(x, y));
        else writer.WriteNull("direction");
    });

    public static string TagJson(ObservedTag tag) => Wrapped("tag", writer =>
    {
        writer.WriteString("unique_id", tag.UniqueId);
        Text(writer, "view_unique_id", tag.ViewUid);
        Strings(writer, "tagged_unique_ids", tag.TaggedUids);
        writer.WriteBoolean("has_leader", tag.HasLeader);
        writer.WriteString("orientation", tag.Orientation);
        Point(writer, "head", tag.Head);
        writer.WriteBoolean("orphaned", tag.Orphaned);
    });

    public static string ScheduleJson(ObservedSchedule schedule) => Wrapped("schedule", writer =>
    {
        writer.WriteString("unique_id", schedule.UniqueId);
        writer.WriteString("name", schedule.Name);
        Text(writer, "built_in_category", schedule.BuiltInCategory);
        Strings(writer, "fields", schedule.Fields);
        Strings(writer, "sort_fields", schedule.SortFields);
    });

    public static string SheetJson(ObservedSheet sheet) => Wrapped("sheet", writer =>
    {
        writer.WriteString("unique_id", sheet.UniqueId);
        writer.WriteString("number", sheet.Number);
        writer.WriteString("name", sheet.Name);
        Strings(writer, "title_block_type_unique_ids", sheet.TitleBlockTypeUids);
    });

    public static string PlacementJson(ObservedPlacement placement) => Wrapped("placement", writer =>
    {
        writer.WriteString("unique_id", placement.UniqueId);
        writer.WriteString("kind", placement.Kind);
        Text(writer, "sheet_unique_id", placement.SheetUid);
        Text(writer, "view_unique_id", placement.ViewUid);
        Point(writer, "center", placement.Center);
    });
}
