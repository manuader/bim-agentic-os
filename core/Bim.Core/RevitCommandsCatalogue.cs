using System.Globalization;
using System.Text.Json;
namespace Bim.Core;

// One catalogue entry. ArgsSchemaJson is both the MCP inputSchema of "args" and the structural check applied by
// RevitCommandProtocol.Parse; Bind then applies typed unit and geometry rules and returns the typed arguments.
public sealed record RevitCommandSpec(string Name, bool Writes, string Description, string ArgsSchemaJson, Func<JsonElement, RevitCommandArgs> Bind)
{
    public const string ToolPrefix = "revit_";
    public string ToolName => ToolPrefix + Name.Replace('.', '_');
}

public static class RevitCommandCatalogue
{
    public const int Version = 1;
    public const int MaxBoundaryPoints = 1000;

    const string Point = """{"type":"array","description":"[x, y] in millimetres","items":{"type":"number"},"minItems":2,"maxItems":2}""";
    const string NoControl = """^[^\\u0000-\\u001f\\u007f]+$""";

    // Schema templates use @n@ placeholders instead of interpolation: the JSON is brace-heavy.
    static string Fill(string template, params string[] parts)
    {
        for (var i = 0; i < parts.Length; i++) template = template.Replace("@" + i + "@", parts[i], StringComparison.Ordinal);
        return template;
    }

    static string Reference(string description) =>
        Fill("""{"type":"string","description":"@0@","minLength":1,"maxLength":256,"pattern":"@1@"}""", description, NoControl);

    static string Text(string description, int maxLength) =>
        Fill("""{"type":"string","description":"@0@","minLength":1,"maxLength":@1@,"pattern":"@2@"}""", description, maxLength.ToString(CultureInfo.InvariantCulture), NoControl);

    const string UniqueIdSchema = """{"type":"string","description":"Revit element UniqueId, as returned by a query","minLength":45,"maxLength":45,"pattern":"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}-[0-9a-fA-F]{8}$"}""";

    const string EmptySchema = """{"type":"object","description":"No arguments; pass {}.","properties":{},"additionalProperties":false}""";

    const string FamilySymbolsSchema = """{"type":"object","properties":{"category":{"type":"string","description":"Which loaded family types to list","enum":["door","window"]}},"required":["category"],"additionalProperties":false}""";

    static readonly string LineCurve = Fill("""{"type":"object","description":"Straight wall from start to end","properties":{"kind":{"type":"string","enum":["line"]},"start":@0@,"end":@1@},"required":["kind","start","end"],"additionalProperties":false}""", Point, Point);
    static readonly string ArcThroughCurve = Fill("""{"type":"object","description":"Curved wall from start to end passing through a third point (not collinear)","properties":{"kind":{"type":"string","enum":["arc"]},"start":@0@,"end":@1@,"through":@2@},"required":["kind","start","end","through"],"additionalProperties":false}""", Point, Point, Point);
    static readonly string ArcCenterCurve = Fill("""{"type":"object","description":"Curved wall around a centre, counter-clockwise from start_angle_deg to end_angle_deg (0 degrees = +X); sweep strictly between 0 and 360","properties":{"kind":{"type":"string","enum":["arc"]},"center":@0@,"radius_mm":{"type":"number","description":"Radius in millimetres, greater than 0"},"start_angle_deg":{"type":"number","description":"Start angle in degrees"},"end_angle_deg":{"type":"number","description":"End angle in degrees, greater than start_angle_deg"}},"required":["kind","center","radius_mm","start_angle_deg","end_angle_deg"],"additionalProperties":false}""", Point);

    static readonly string WallSchema = Fill("""{"type":"object","properties":{"level":@0@,"type":@1@,"base_offset_mm":{"type":"number","description":"Base offset from the level in millimetres; may be negative; default 0"},"height_mm":{"type":"number","description":"Unconnected height in millimetres, greater than 0"},"structural":{"type":"boolean","description":"Structural wall; default false"},"curve":{"description":"Wall location line in plan, in millimetres","oneOf":[@2@,@3@,@4@]}},"required":["level","type","height_mm","curve"],"additionalProperties":false}""", Reference("Base level: name or UniqueId from revit_query_levels"), Reference("Wall type: name or UniqueId from revit_query_wall_types"), LineCurve, ArcThroughCurve, ArcCenterCurve);

    static readonly string OpeningSchema = Fill("""{"type":"object","properties":{"category":{"type":"string","description":"Door or window","enum":["door","window"]},"symbol":@0@,"host_wall_uid":@1@,"position_along_mm":{"type":"number","description":"Distance along the host wall from its start, in millimetres, 0 or more"},"sill_mm":{"type":"number","description":"Window sill height above the level in millimetres, 0 or more; optional; not allowed for doors"}},"required":["category","symbol","host_wall_uid","position_along_mm"],"additionalProperties":false}""", Reference("Family type: name or UniqueId from revit_query_family_symbols"), UniqueIdSchema);

    static readonly string FloorSchema = Fill("""{"type":"object","properties":{"level":@0@,"type":@1@,"boundary":{"type":"array","description":"Closed outline as [x, y] millimetre vertices; the closing edge is implicit; must not self-intersect","items":@2@,"minItems":3,"maxItems":@3@}},"required":["level","type","boundary"],"additionalProperties":false}""", Reference("Level: name or UniqueId from revit_query_levels"), Reference("Floor type: name or UniqueId from revit_query_floor_types"), Point, MaxBoundaryPoints.ToString(CultureInfo.InvariantCulture));

    static readonly string RoomSchema = Fill("""{"type":"object","properties":{"level":@0@,"point":@1@,"name":@2@,"number":@3@},"required":["level","point"],"additionalProperties":false}""", Reference("Level: name or UniqueId from revit_query_levels"), Point, Text("Room name; optional", 256), Text("Room number; optional", 64));

    public static IReadOnlyList<RevitCommandSpec> V1 { get; } = new[]
    {
        new RevitCommandSpec("query.levels", false, "List the levels of the open Revit document with name, UniqueId and elevation in millimetres. Read-only.", EmptySchema, _ => new EmptyQueryArgs()),
        new RevitCommandSpec("query.wall_types", false, "List the wall types available in the document with name, UniqueId and width in millimetres. Read-only.", EmptySchema, _ => new EmptyQueryArgs()),
        new RevitCommandSpec("query.floor_types", false, "List the floor types available in the document with name, UniqueId and thickness in millimetres. Read-only.", EmptySchema, _ => new EmptyQueryArgs()),
        new RevitCommandSpec("query.family_symbols", false, "List the loaded door or window family types with family name, type name and UniqueId. Read-only.", FamilySymbolsSchema, BindFamilySymbols),
        new RevitCommandSpec("wall.create", true, "Create one straight or curved wall on a level. Lengths in millimetres, angles in degrees. Allowed only on enabled test copies.", WallSchema, BindWall),
        new RevitCommandSpec("opening.place", true, "Place a door or window of a loaded family type in a host wall, at a distance along the wall from its start. Allowed only on enabled test copies.", OpeningSchema, BindOpening),
        new RevitCommandSpec("floor.create", true, "Create a floor of a given type from a closed, non-self-intersecting outline in millimetres. Allowed only on enabled test copies.", FloorSchema, BindFloor),
        new RevitCommandSpec("room.create", true, "Place a room on a level at a plan point inside an enclosed area, with optional name and number. Allowed only on enabled test copies.", RoomSchema, BindRoom),
    };

    public static RevitCommandSpec? Find(string name) => V1.FirstOrDefault(spec => spec.Name == name);

    static RevitCommandArgs BindFamilySymbols(JsonElement args) => new FamilySymbolQueryArgs(args.GetProperty("category").GetString()!);

    static RevitCommandArgs BindWall(JsonElement args)
    {
        var level = args.GetProperty("level").GetString()!;
        var type = args.GetProperty("type").GetString()!;
        var baseOffset = args.TryGetProperty("base_offset_mm", out var offset)
            ? RevitUnits.Millimetres(Number(offset), "args.base_offset_mm", allowNegative: true, allowZero: true)
            : Length.FromMetres(0);
        var height = RevitUnits.Millimetres(Number(args.GetProperty("height_mm")), "args.height_mm", allowNegative: false, allowZero: false);
        if (height.Millimetres < RevitCommandGeometry.MinSegmentMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args.height_mm", "height under 1.2 mm");
        var structural = args.TryGetProperty("structural", out var structuralElement) && structuralElement.GetBoolean();
        var curve = args.GetProperty("curve");
        WallCurve typed;
        if (curve.GetProperty("kind").GetString() == "line")
        {
            var start = Coordinates(curve, "start", "args.curve.start");
            var end = Coordinates(curve, "end", "args.curve.end");
            RevitCommandGeometry.CheckLine(start.X, start.Y, end.X, end.Y, "args.curve");
            typed = new LineWallCurve(ToPoint(start), ToPoint(end));
        }
        else if (curve.TryGetProperty("through", out _))
        {
            var start = Coordinates(curve, "start", "args.curve.start");
            var end = Coordinates(curve, "end", "args.curve.end");
            var through = Coordinates(curve, "through", "args.curve.through");
            var arc = RevitCommandGeometry.ArcThrough(start.X, start.Y, end.X, end.Y, through.X, through.Y, "args.curve");
            typed = new ArcThroughWallCurve(ToPoint(start), ToPoint(end), ToPoint(through), ToPoint((arc.CenterX, arc.CenterY)),
                Length.FromMillimetres(arc.RadiusMm), Angle.FromDegrees(arc.SweepDegrees), arc.CounterClockwise);
        }
        else
        {
            var center = Coordinates(curve, "center", "args.curve.center");
            var radius = Number(curve.GetProperty("radius_mm"));
            var startAngle = Number(curve.GetProperty("start_angle_deg"));
            var endAngle = Number(curve.GetProperty("end_angle_deg"));
            RevitCommandGeometry.CheckArcCenter(center.X, center.Y, radius, startAngle, endAngle, "args.curve");
            typed = new ArcCenterWallCurve(ToPoint(center), Length.FromMillimetres(radius), Angle.FromDegrees(startAngle), Angle.FromDegrees(endAngle));
        }
        return new WallCreateArgs(level, type, baseOffset, height, structural, typed);
    }

    static RevitCommandArgs BindOpening(JsonElement args)
    {
        var category = args.GetProperty("category").GetString()!;
        if (category == "door" && args.TryGetProperty("sill_mm", out _)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.sill_mm", "doors have no sill height");
        var position = RevitUnits.Millimetres(Number(args.GetProperty("position_along_mm")), "args.position_along_mm", allowNegative: false, allowZero: true);
        Length? sill = args.TryGetProperty("sill_mm", out var sillElement)
            ? RevitUnits.Millimetres(Number(sillElement), "args.sill_mm", allowNegative: false, allowZero: true)
            : null;
        return new OpeningPlaceArgs(category, args.GetProperty("symbol").GetString()!, args.GetProperty("host_wall_uid").GetString()!, position, sill);
    }

    static RevitCommandArgs BindFloor(JsonElement args)
    {
        var points = new List<(double X, double Y)>();
        var index = 0;
        foreach (var vertex in args.GetProperty("boundary").EnumerateArray())
        {
            points.Add(Coordinates(vertex, "args.boundary[" + index + "]"));
            index++;
        }
        var boundary = RevitCommandGeometry.CheckBoundary(points, "args.boundary");
        return new FloorCreateArgs(args.GetProperty("level").GetString()!, args.GetProperty("type").GetString()!, boundary.Select(ToPoint).ToArray());
    }

    static RevitCommandArgs BindRoom(JsonElement args)
    {
        var point = Coordinates(args, "point", "args.point");
        string? Optional(string name) => args.TryGetProperty(name, out var value) ? value.GetString() : null;
        return new RoomCreateArgs(args.GetProperty("level").GetString()!, ToPoint(point), Optional("name"), Optional("number"));
    }

    // JSON cannot carry NaN/Infinity; an overflowing literal such as 1e400 does not fit a double and becomes NaN here,
    // which the typed unit rules then reject with the field-specific code.
    static double Number(JsonElement value) => value.TryGetDouble(out var number) ? number : double.NaN;

    static (double X, double Y) Coordinates(JsonElement parent, string name, string field) => Coordinates(parent.GetProperty(name), field);

    static (double X, double Y) Coordinates(JsonElement pair, string field)
    {
        double x = Number(pair[0]), y = Number(pair[1]);
        RevitUnits.Millimetres(x, field + "[0]", allowNegative: true, allowZero: true);
        RevitUnits.Millimetres(y, field + "[1]", allowNegative: true, allowZero: true);
        return (x, y);
    }

    static Point2 ToPoint((double X, double Y) millimetres) => new(Length.FromMillimetres(millimetres.X), Length.FromMillimetres(millimetres.Y));
}
