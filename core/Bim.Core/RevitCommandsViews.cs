using System.Globalization;
using System.Text.Json;
namespace Bim.Core;

// P4-01 view commands (documentation engine): query.view_family_types, query.view_templates, view.create_plan,
// view.create_section and view.create_3d. Same envelope (protocol_version 1), schema subset, unit rules, copies-only
// guard and receipts as catalogue v1/v2; earlier commands are unchanged. Vendor-free like every RevitCommands*.cs
// file; the add-in executor (CommandsExecutorViews.cs) consumes these types.

// Options every view creator accepts. Type: a ViewFamilyType name or UniqueId (default: the document default of the
// family). Template: a view template name or UniqueId. Scale: the 1:N denominator; never together with Template,
// because a template may control the scale.
public sealed record ViewOptions(string? Type, string? Name, string? Template, int? Scale);
// Family filter: floor_plan, ceiling_plan, section or three_d; null lists every view family type.
public sealed record ViewFamilyTypeQueryArgs(string? Family) : RevitCommandArgs;
// Kind is floor_plan or ceiling_plan.
public sealed record ViewPlanCreateArgs(string Level, string Kind, ViewOptions Options) : RevitCommandArgs;
// A vertical section cut along the plan line Start -> End, looking to the left of that direction, Depth deep behind
// the cut plane, cropped between the Bottom and Top project elevations.
public sealed record ViewSectionCreateArgs(Point2 Start, Point2 End, Length Depth, Length Bottom, Length Top, ViewOptions Options) : RevitCommandArgs;
// An axis-aligned box in project coordinates.
public sealed record ViewBox(Length MinX, Length MinY, Length MinZ, Length MaxX, Length MaxY, Length MaxZ);
// A unit direction in project coordinates (dimensionless).
public sealed record ViewDirection3(double X, double Y, double Z);
// An isometric (never perspective) 3D view, optionally limited by a section box. Eye: unit direction from the model
// toward the eye (view_from preset or eye_direction); null keeps Revit's default orientation (eye to the south-east).
public sealed record View3DCreateArgs(ViewBox? SectionBox, ViewOptions Options, ViewDirection3? Eye = null) : RevitCommandArgs;

public static class RevitViewRules
{
    // Revit's View.Scale range.
    public const int MaxScale = 24000;
    // Characters Revit refuses in a view name.
    public const string ForbiddenNameCharacters = "\\:{}[]|;<>?`~";
    public static readonly IReadOnlyList<string> Families = new[] { "floor_plan", "ceiling_plan", "section", "three_d" };
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of every part exists (the v1 schema helpers are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> ViewSpecs = new(BuildViews);

    public static IReadOnlyList<RevitCommandSpec> Views => ViewSpecs.Value;

    static IReadOnlyList<RevitCommandSpec> BuildViews()
    {
        const string Point3 = """{"type":"array","description":"[x, y, z] in millimetres, project coordinates","items":{"type":"number"},"minItems":3,"maxItems":3}""";
        var options = Fill(""" "type":@0@,"name":@1@,"view_template":@2@,"scale":{"type":"integer","description":"View scale denominator N for 1:N, 1 to 24000; omit to keep the type's default; not together with view_template"}""",
            Reference("View family type: name or UniqueId from revit_query_view_family_types; default: the document default of that family"),
            Text("View name; optional; unique among views of this kind; none of \\\\ : { } [ ] | ; < > ? ` ~ and no leading or trailing space", 256),
            Reference("View template: name or UniqueId from revit_query_view_templates, of the same view type; optional"));

        var families = """{"type":"object","properties":{"family":{"type":"string","description":"Only list view family types of this family; omit to list all","enum":["floor_plan","ceiling_plan","section","three_d"]}},"additionalProperties":false}""";

        var plan = Fill("""{"type":"object","properties":{"level":@0@,"kind":{"type":"string","description":"Floor plan or reflected ceiling plan","enum":["floor_plan","ceiling_plan"]},@1@},"required":["level","kind"],"additionalProperties":false}""",
            Reference("Level: name or UniqueId from revit_query_levels"), options);

        var line = Fill("""{"type":"object","description":"Section cut line in plan, in millimetres; the view looks to the left of start -> end","properties":{"start":@0@,"end":@1@},"required":["start","end"],"additionalProperties":false}""", Point, Point);
        var section = Fill("""{"type":"object","properties":{"line":@0@,"depth_mm":{"type":"number","description":"Far clip depth behind the cut plane in millimetres, at least 1.2 and at most 9144000"},"bottom_elevation_mm":{"type":"number","description":"Bottom of the crop region as a project elevation in millimetres (see project_elevation_mm in revit_query_levels)"},"top_elevation_mm":{"type":"number","description":"Top of the crop region as a project elevation in millimetres, at least 1.2 above the bottom"},@1@},"required":["line","depth_mm","bottom_elevation_mm","top_elevation_mm"],"additionalProperties":false}""",
            line, options);

        var box = Fill("""{"type":"object","description":"Axis-aligned section box in project coordinates; max at least 1.2 mm above min on every axis","properties":{"min":@0@,"max":@1@},"required":["min","max"],"additionalProperties":false}""", Point3, Point3);
        var threeD = Fill("""{"type":"object","properties":{"section_box":@0@,@1@},"additionalProperties":false}""", box, options);

        return new[]
        {
            new RevitCommandSpec("query.view_family_types", false, "List the view family types (floor plan, ceiling plan, section, 3D and others) with name, UniqueId, view family and whether it is the document default. Read-only.", families, BindViewFamilyTypes),
            new RevitCommandSpec("query.view_templates", false, "List the view templates in the document with name, UniqueId and view type. Read-only.", EmptySchema, _ => new EmptyQueryArgs()),
            new RevitCommandSpec("view.create_plan", true, "Create a floor plan or reflected ceiling plan view of a level, with optional view family type, name, view template or scale. Allowed only on enabled test copies.", plan, BindViewPlan),
            new RevitCommandSpec("view.create_section", true, "Create a section view along a plan line in millimetres, looking to the left of the line, with a far clip depth and a bottom and top elevation, and optional type, name, view template or scale. Allowed only on enabled test copies.", section, BindViewSection),
            new RevitCommandSpec("view.create_3d", true, "Create an isometric 3D view, optionally limited by an axis-aligned section box in millimetres, with optional type, name, view template or scale. Allowed only on enabled test copies.", threeD, BindView3D),
        };
    }

    static RevitCommandArgs BindViewFamilyTypes(JsonElement args) =>
        new ViewFamilyTypeQueryArgs(args.TryGetProperty("family", out var family) ? family.GetString() : null);

    static ViewOptions BindViewOptions(JsonElement args)
    {
        string? Optional(string name) => args.TryGetProperty(name, out var value) ? value.GetString() : null;
        var name = Optional("name");
        if (name is not null)
        {
            var at = name.IndexOfAny(RevitViewRules.ForbiddenNameCharacters.ToCharArray());
            if (at >= 0)
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.name", "Revit view names cannot contain '" + name[at] + "' (none of " + RevitViewRules.ForbiddenNameCharacters + ")");
            if (name.Trim().Length == 0 || name.Trim().Length != name.Length)
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.name", "view name must not be blank or start or end with white space");
        }
        var template = Optional("view_template");
        int? scale = null;
        if (args.TryGetProperty("scale", out var scaleElement))
        {
            if (!scaleElement.TryGetInt32(out var denominator) || denominator < 1 || denominator > RevitViewRules.MaxScale)
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.scale", "scale must be a whole number from 1 to " + RevitViewRules.MaxScale.ToString(CultureInfo.InvariantCulture));
            if (template is not null)
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.scale", "give scale or view_template, not both: the template may control the scale");
            scale = denominator;
        }
        return new ViewOptions(Optional("type"), name, template, scale);
    }

    static RevitCommandArgs BindViewPlan(JsonElement args) =>
        new ViewPlanCreateArgs(args.GetProperty("level").GetString()!, args.GetProperty("kind").GetString()!, BindViewOptions(args));

    static RevitCommandArgs BindViewSection(JsonElement args)
    {
        var line = args.GetProperty("line");
        var start = Coordinates(line, "start", "args.line.start");
        var end = Coordinates(line, "end", "args.line.end");
        RevitCommandGeometry.CheckLine(start.X, start.Y, end.X, end.Y, "args.line");
        if (RevitCommandGeometry.Distance(start.X, start.Y, end.X, end.Y) > RevitCommandGeometry.MaxRevitLengthMm)
            throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args.line", "section line longer than 9144000 mm (Revit's 30000 ft limit)");
        var depth = RevitUnits.Millimetres(Number(args.GetProperty("depth_mm")), "args.depth_mm", allowNegative: false, allowZero: false);
        if (depth.Millimetres < RevitCommandGeometry.MinSegmentMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args.depth_mm", "depth under 1.2 mm");
        if (depth.Millimetres > RevitCommandGeometry.MaxRevitLengthMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args.depth_mm", "depth above 9144000 mm (Revit's 30000 ft limit)");
        var bottom = Elevation(args, "bottom_elevation_mm");
        var top = Elevation(args, "top_elevation_mm");
        if (!(top.Millimetres - bottom.Millimetres >= RevitCommandGeometry.MinSegmentMm))
            throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args.top_elevation_mm", "top elevation must be at least 1.2 mm above the bottom elevation");
        return new ViewSectionCreateArgs(ToPoint(start), ToPoint(end), depth, bottom, top, BindViewOptions(args));
    }

    static Length Elevation(JsonElement args, string name)
    {
        var value = RevitUnits.Millimetres(Number(args.GetProperty(name)), "args." + name, allowNegative: true, allowZero: true);
        if (Math.Abs(value.Millimetres) > RevitCommandGeometry.MaxRevitLengthMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args." + name, "elevation beyond 9144000 mm (Revit's 30000 ft limit)");
        return value;
    }

    static RevitCommandArgs BindView3D(JsonElement args)
    {
        ViewBox? box = null;
        if (args.TryGetProperty("section_box", out var boxElement))
        {
            double[] Corner(string name)
            {
                var corner = boxElement.GetProperty(name);
                var values = new double[3];
                for (var i = 0; i < 3; i++)
                {
                    values[i] = Number(corner[i]);
                    RevitUnits.Millimetres(values[i], "args.section_box." + name + "[" + i + "]", allowNegative: true, allowZero: true);
                }
                return values;
            }
            var min = Corner("min");
            var max = Corner("max");
            for (var i = 0; i < 3; i++)
            {
                var extent = max[i] - min[i];
                if (!(extent >= RevitCommandGeometry.MinSegmentMm)) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args.section_box.max[" + i + "]", "section box max must be at least 1.2 mm above min on every axis");
                if (extent > RevitCommandGeometry.MaxRevitLengthMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args.section_box.max[" + i + "]", "section box wider than 9144000 mm (Revit's 30000 ft limit)");
            }
            box = new ViewBox(Length.FromMillimetres(min[0]), Length.FromMillimetres(min[1]), Length.FromMillimetres(min[2]), Length.FromMillimetres(max[0]), Length.FromMillimetres(max[1]), Length.FromMillimetres(max[2]));
        }
        return new View3DCreateArgs(box, BindViewOptions(args));
    }
}
