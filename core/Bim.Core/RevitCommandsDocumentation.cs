using System.Globalization;
using System.Text.Json;
namespace Bim.Core;

// P4-02 documentation commands: query.title_blocks, dimension.create_linear, tag.create, schedule.create,
// sheet.create and sheet.place_view. Same envelope (protocol_version 1), schema subset, unit rules, copies-only guard
// and receipts as the earlier catalogue parts, which are unchanged. Vendor-free like every RevitCommands*.cs file;
// the add-in executor (CommandsExecutorDocumentation.cs) consumes these types.

// A linear dimension in a plan view between two parallel references (straight grids or straight walls; a wall is
// measured on its WallFace side face). LineStart/LineEnd: the dimension line in plan, both null for the default line.
public sealed record DimensionLinearCreateArgs(string ViewUid, string FirstUid, string SecondUid, Point2? LineStart, Point2? LineEnd, string WallFace) : RevitCommandArgs;
// A tag by category of one element in a plan view. Head: the tag head in plan, null for the element's centre in the
// view. Orientation: horizontal or vertical.
public sealed record TagCreateArgs(string ViewUid, string ElementUid, Point2? Head, bool Leader, string Orientation) : RevitCommandArgs;
// A schedule of one category with the named fields in order, optionally sorted ascending by one of them.
// Category: the text the caller gave; BuiltInCategory: the Revit BuiltInCategory name it stands for.
public sealed record ScheduleCreateArgs(string Category, string BuiltInCategory, IReadOnlyList<string> Fields, string? SortBy, string? Name) : RevitCommandArgs;
// A sheet with a number and name and an optional title block type (null: no title block).
public sealed record SheetCreateArgs(string Number, string Name, string? TitleBlock) : RevitCommandArgs;
// Places a view (as a viewport) or a schedule (as a schedule sheet instance) on a sheet at Center, in sheet
// millimetres.
public sealed record SheetPlaceViewArgs(string SheetUid, string ViewUid, Point2 Center) : RevitCommandArgs;

public static class RevitDocumentationRules
{
    public const int MaxScheduleFields = 32;
    // Sheet coordinates: far beyond any paper size is a units mistake.
    public const double MaxSheetCoordinateMm = 100000;
    public static readonly IReadOnlyList<string> WallFaces = new[] { "exterior", "interior" };
    public static readonly IReadOnlyList<string> TagOrientations = new[] { "horizontal", "vertical" };

    // Revit names (views, schedules, sheets): no forbidden character, not blank, no leading or trailing white space.
    public static void CheckName(string? name, string field, string what)
    {
        if (name is null) return;
        var at = name.IndexOfAny(RevitViewRules.ForbiddenNameCharacters.ToCharArray());
        if (at >= 0)
            throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field, "Revit " + what + "s cannot contain '" + name[at] + "' (none of " + RevitViewRules.ForbiddenNameCharacters + ")");
        if (name.Trim().Length == 0 || name.Trim().Length != name.Length)
            throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field, what + " must not be blank or start or end with white space");
    }
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of every part exists (the v1 schema helpers are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> DocumentationSpecs = new(BuildDocumentation);

    public static IReadOnlyList<RevitCommandSpec> Documentation => DocumentationSpecs.Value;

    static IReadOnlyList<RevitCommandSpec> BuildDocumentation()
    {
        var line = Fill("""{"type":"object","description":"Dimension line in plan, in millimetres, perpendicular to the references; omit to draw it from the first reference straight across to the second","properties":{"start":@0@,"end":@1@},"required":["start","end"],"additionalProperties":false}""", Point, Point);
        var dimension = Fill("""{"type":"object","properties":{"view_uid":@0@,"first_uid":@1@,"second_uid":@2@,"line":@3@,"wall_face":{"type":"string","description":"Which side face of a wall reference is measured; default exterior","enum":["exterior","interior"]}},"required":["view_uid","first_uid","second_uid"],"additionalProperties":false}""",
            UniqueIdSchema, UniqueIdSchema, UniqueIdSchema, line);

        var tag = Fill("""{"type":"object","properties":{"view_uid":@0@,"element_uid":@1@,"head":@2@,"leader":{"type":"boolean","description":"Draw a leader from the tag to the element; default false"},"orientation":{"type":"string","description":"Tag orientation; default horizontal","enum":["horizontal","vertical"]}},"required":["view_uid","element_uid"],"additionalProperties":false}""",
            UniqueIdSchema, UniqueIdSchema, Point);

        var friendly = string.Join(",", RevitElementQueryRules.Categories.Select(category => "\"" + category.Name + "\""));
        var category = Fill("""{"description":"Schedule category: a common name, or any Revit BuiltInCategory name such as OST_Walls","oneOf":[{"type":"string","description":"Common category name","enum":[@0@]},{"type":"string","description":"Revit BuiltInCategory name","minLength":5,"maxLength":80,"pattern":"^OST_[A-Za-z][A-Za-z0-9_]*$"}]}""", friendly);
        var schedule = Fill("""{"type":"object","properties":{"category":@0@,"fields":{"type":"array","description":"Schedulable field names (as shown in Revit's Schedule Properties), in column order, distinct, 1 to 32","items":@1@,"minItems":1,"maxItems":@2@},"sort_by":@3@,"name":@4@},"required":["category","fields"],"additionalProperties":false}""",
            category, Text("Field name", 256), RevitDocumentationRules.MaxScheduleFields.ToString(CultureInfo.InvariantCulture),
            Text("One of fields to sort ascending by; optional", 256),
            Text("Schedule name; optional; unique among schedules; none of \\\\ : { } [ ] | ; < > ? ` ~ and no leading or trailing space", 256));

        var sheet = Fill("""{"type":"object","properties":{"number":@0@,"name":@1@,"title_block":@2@},"required":["number","name"],"additionalProperties":false}""",
            Text("Sheet number, unique among sheets (for example A101); none of \\\\ : { } [ ] | ; < > ? ` ~ and no leading or trailing space", 64),
            Text("Sheet name; none of \\\\ : { } [ ] | ; < > ? ` ~ and no leading or trailing space", 256),
            Reference("Title block type: Family: Type, type name or UniqueId from revit_query_title_blocks; omit for a sheet without title block"));

        var place = Fill("""{"type":"object","properties":{"sheet_uid":@0@,"view_uid":@1@,"center":{"type":"array","description":"[x, y] on the sheet in millimetres: the viewport centre, or the schedule's placement point","items":{"type":"number"},"minItems":2,"maxItems":2}},"required":["sheet_uid","view_uid","center"],"additionalProperties":false}""",
            UniqueIdSchema, UniqueIdSchema);

        return new[]
        {
            new RevitCommandSpec("query.title_blocks", false, "List the loaded title block family types with family name, type name, reference and UniqueId. Read-only.", EmptySchema, _ => new EmptyQueryArgs()),
            new RevitCommandSpec("dimension.create_linear", true, "Create a linear dimension in a plan view between two parallel straight grids or walls (by UniqueId), with an optional dimension line in millimetres; the measured value is read back in millimetres. Allowed only on enabled test copies.", dimension, BindDimension),
            new RevitCommandSpec("tag.create", true, "Tag one element by category in a plan view, with an optional head point in millimetres, leader and orientation. Allowed only on enabled test copies.", tag, BindTag),
            new RevitCommandSpec("schedule.create", true, "Create a schedule of one category with the named fields in order, optionally sorted by one of them and named; the fields are read back. Allowed only on enabled test copies.", schedule, BindSchedule),
            new RevitCommandSpec("sheet.create", true, "Create a sheet with a unique number, a name and an optional title block type. Allowed only on enabled test copies.", sheet, BindSheet),
            new RevitCommandSpec("sheet.place_view", true, "Place a view on a sheet as a viewport centred at a sheet point in millimetres, or a schedule as a schedule sheet instance at that point; a view already on a sheet is refused. Allowed only on enabled test copies.", place, BindPlaceView),
        };
    }

    static RevitCommandArgs BindDimension(JsonElement args)
    {
        var view = args.GetProperty("view_uid").GetString()!;
        var first = args.GetProperty("first_uid").GetString()!;
        var second = args.GetProperty("second_uid").GetString()!;
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.second_uid", "first_uid and second_uid must name different elements");
        Point2? start = null, end = null;
        if (args.TryGetProperty("line", out var line))
        {
            var s = Coordinates(line, "start", "args.line.start");
            var e = Coordinates(line, "end", "args.line.end");
            RevitCommandGeometry.CheckLine(s.X, s.Y, e.X, e.Y, "args.line");
            if (RevitCommandGeometry.Distance(s.X, s.Y, e.X, e.Y) > RevitCommandGeometry.MaxRevitLengthMm)
                throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args.line", "dimension line longer than 9144000 mm (Revit's 30000 ft limit)");
            start = ToPoint(s);
            end = ToPoint(e);
        }
        var face = args.TryGetProperty("wall_face", out var faceElement) ? faceElement.GetString()! : "exterior";
        return new DimensionLinearCreateArgs(view, first, second, start, end, face);
    }

    static RevitCommandArgs BindTag(JsonElement args)
    {
        Point2? head = args.TryGetProperty("head", out _) ? ToPoint(Coordinates(args, "head", "args.head")) : null;
        var leader = args.TryGetProperty("leader", out var leaderElement) && leaderElement.GetBoolean();
        var orientation = args.TryGetProperty("orientation", out var orientationElement) ? orientationElement.GetString()! : "horizontal";
        return new TagCreateArgs(args.GetProperty("view_uid").GetString()!, args.GetProperty("element_uid").GetString()!, head, leader, orientation);
    }

    static RevitCommandArgs BindSchedule(JsonElement args)
    {
        var category = args.GetProperty("category").GetString()!;
        var fields = new List<string>();
        var index = 0;
        foreach (var field in args.GetProperty("fields").EnumerateArray().Select(item => item.GetString()!))
        {
            if (fields.Contains(field, StringComparer.Ordinal)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.fields[" + index + "]", "field " + field + " is listed twice");
            fields.Add(field);
            index++;
        }
        var sortBy = args.TryGetProperty("sort_by", out var sortElement) ? sortElement.GetString() : null;
        if (sortBy is not null && !fields.Contains(sortBy, StringComparer.Ordinal)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.sort_by", "sort_by must be one of fields");
        var name = args.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
        RevitDocumentationRules.CheckName(name, "args.name", "schedule name");
        return new ScheduleCreateArgs(category, RevitElementQueryRules.BuiltInCategory(category, "args.category"), fields, sortBy, name);
    }

    static RevitCommandArgs BindSheet(JsonElement args)
    {
        var number = args.GetProperty("number").GetString()!;
        var name = args.GetProperty("name").GetString()!;
        RevitDocumentationRules.CheckName(number, "args.number", "sheet number");
        RevitDocumentationRules.CheckName(name, "args.name", "sheet name");
        return new SheetCreateArgs(number, name, args.TryGetProperty("title_block", out var titleBlock) ? titleBlock.GetString() : null);
    }

    static RevitCommandArgs BindPlaceView(JsonElement args)
    {
        var center = Coordinates(args, "center", "args.center");
        if (Math.Abs(center.X) > RevitDocumentationRules.MaxSheetCoordinateMm || Math.Abs(center.Y) > RevitDocumentationRules.MaxSheetCoordinateMm)
            throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args.center", "sheet point beyond 100000 mm from the sheet origin");
        return new SheetPlaceViewArgs(args.GetProperty("sheet_uid").GetString()!, args.GetProperty("view_uid").GetString()!, ToPoint(center));
    }
}
