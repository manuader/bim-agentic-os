using System.Text.Json;
namespace Bim.Core;

// P9-02 structure commands: query.grids, query.structural_types, grid.create, structural_column.create,
// structural_beam.create, foundation.create_isolated and foundation.create_wall. Same envelope (protocol_version 1),
// schema subset, unit rules, copies-only guard and receipts as the earlier catalogue parts, which are unchanged.
// Vendor-free like every RevitCommands*.cs file; the add-in executor (CommandsExecutorStructure.cs) consumes these
// types.

// Category: column, framing, foundation (isolated foundation family types) or wall_foundation (wall foundation types).
public sealed record StructuralTypeQueryArgs(string Category) : RevitCommandArgs;
// A straight grid line in plan from Start (the bubble end) to End, optionally named.
public sealed record GridCreateArgs(Point2 Start, Point2 End, string? Name) : RevitCommandArgs;
// A vertical structural column at a plan point, from BaseLevel + BaseOffset up to TopLevel + TopOffset.
public sealed record StructuralColumnCreateArgs(string Type, Point2 Point, string BaseLevel, string TopLevel, Length BaseOffset, Length TopOffset) : RevitCommandArgs;
// A straight structural beam from Start to End in the plane of Level (its reference level).
public sealed record StructuralBeamCreateArgs(string Level, string Type, Point2 Start, Point2 End) : RevitCommandArgs;
// An isolated foundation of a structural foundation family type at a plan point on a level (for example under a
// column: give the column's point and base level).
public sealed record IsolatedFoundationCreateArgs(string Level, string Type, Point2 Point) : RevitCommandArgs;
// A wall foundation of a wall foundation type under an existing basic wall.
public sealed record WallFoundationCreateArgs(string HostWallUid, string Type) : RevitCommandArgs;

public static class RevitStructureRules
{
    public static readonly IReadOnlyList<string> Categories = new[] { "column", "framing", "foundation", "wall_foundation" };
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of every part exists (the v1 schema helpers are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> StructureSpecs = new(BuildStructure);

    public static IReadOnlyList<RevitCommandSpec> Structure => StructureSpecs.Value;

    static IReadOnlyList<RevitCommandSpec> BuildStructure()
    {
        string LineSchema(string description) => Fill("""{"type":"object","description":"@0@","properties":{"start":@1@,"end":@2@},"required":["start","end"],"additionalProperties":false}""", description, Point, Point);
        const string Offset = """{"type":"number","description":"@D@ in millimetres; may be negative, at most 9144000 either way; default 0"}""";

        var types = """{"type":"object","properties":{"category":{"type":"string","description":"Which structural types to list: column, framing (beams), foundation (isolated foundation family types) or wall_foundation (wall foundation types)","enum":["column","framing","foundation","wall_foundation"]}},"required":["category"],"additionalProperties":false}""";

        var grid = Fill("""{"type":"object","properties":{"line":@0@,"name":@1@},"required":["line"],"additionalProperties":false}""",
            LineSchema("Straight grid line in plan, in millimetres; the grid bubble is at start"),
            Text("Grid name (for example A or 1); optional, else Revit numbers it; unique among grids; none of \\\\ : { } [ ] | ; < > ? ` ~ and no leading or trailing space", 256));

        var column = Fill("""{"type":"object","properties":{"type":@0@,"point":@1@,"base_level":@2@,"top_level":@3@,"base_offset_mm":@4@,"top_offset_mm":@5@},"required":["type","point","base_level","top_level"],"additionalProperties":false}""",
            Reference("Structural column type: Family: Type, type name or UniqueId from revit_query_structural_types with category column"), Point,
            Reference("Base level: name or UniqueId from revit_query_levels"), Reference("Top level, above the base: name or UniqueId from revit_query_levels"),
            Offset.Replace("@D@", "Base offset from the base level", StringComparison.Ordinal), Offset.Replace("@D@", "Top offset from the top level", StringComparison.Ordinal));

        var beam = Fill("""{"type":"object","properties":{"level":@0@,"type":@1@,"line":@2@},"required":["level","type","line"],"additionalProperties":false}""",
            Reference("Reference level: name or UniqueId from revit_query_levels; the beam lies in its plane"),
            Reference("Structural framing type: Family: Type, type name or UniqueId from revit_query_structural_types with category framing"),
            LineSchema("Straight beam location line in plan, from start to end, in millimetres"));

        var isolated = Fill("""{"type":"object","properties":{"level":@0@,"type":@1@,"point":@2@},"required":["level","type","point"],"additionalProperties":false}""",
            Reference("Level: name or UniqueId from revit_query_levels (under a column: its base level)"),
            Reference("Isolated foundation type: Family: Type, type name or UniqueId from revit_query_structural_types with category foundation"), Point);

        var wall = Fill("""{"type":"object","properties":{"host_wall_uid":@0@,"type":@1@},"required":["host_wall_uid","type"],"additionalProperties":false}""",
            UniqueIdSchema, Reference("Wall foundation type: name or UniqueId from revit_query_structural_types with category wall_foundation"));

        return new[]
        {
            new RevitCommandSpec("query.grids", false, "List the grids of the document with name, UniqueId, whether curved and the plan end points in millimetres. Read-only.", EmptySchema, _ => new EmptyQueryArgs()),
            new RevitCommandSpec("query.structural_types", false, "List the loaded structural column, framing or isolated foundation family types, or the wall foundation types, with family, type name, reference and UniqueId. Read-only.", types, BindStructuralTypes),
            new RevitCommandSpec("grid.create", true, "Create one straight grid line in plan in millimetres, with an optional unique name. Allowed only on enabled test copies.", grid, BindGrid),
            new RevitCommandSpec("structural_column.create", true, "Place a vertical structural column of a loaded column type at a plan point in millimetres, from a base level to a top level with optional offsets. Allowed only on enabled test copies.", column, BindColumn),
            new RevitCommandSpec("structural_beam.create", true, "Place a straight structural beam of a loaded framing type along a plan line in millimetres on a reference level. Allowed only on enabled test copies.", beam, BindBeam),
            new RevitCommandSpec("foundation.create_isolated", true, "Place an isolated structural foundation of a loaded foundation type at a plan point in millimetres on a level, for example under a column. Allowed only on enabled test copies.", isolated, BindIsolatedFoundation),
            new RevitCommandSpec("foundation.create_wall", true, "Create a wall foundation of a wall foundation type under an existing basic wall that has none. Allowed only on enabled test copies.", wall, BindWallFoundation),
        };
    }

    static RevitCommandArgs BindStructuralTypes(JsonElement args) => new StructuralTypeQueryArgs(args.GetProperty("category").GetString()!);

    // A straight line at args.line: at least 1.2 mm and at most Revit's 30000 ft.
    static (Point2 Start, Point2 End) BindStraightLine(JsonElement args, string what)
    {
        var line = args.GetProperty("line");
        var start = Coordinates(line, "start", "args.line.start");
        var end = Coordinates(line, "end", "args.line.end");
        RevitCommandGeometry.CheckLine(start.X, start.Y, end.X, end.Y, "args.line");
        if (RevitCommandGeometry.Distance(start.X, start.Y, end.X, end.Y) > RevitCommandGeometry.MaxRevitLengthMm)
            throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args.line", what + " longer than 9144000 mm (Revit's 30000 ft limit)");
        return (ToPoint(start), ToPoint(end));
    }

    static RevitCommandArgs BindGrid(JsonElement args)
    {
        var (start, end) = BindStraightLine(args, "grid line");
        string? name = args.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
        if (name is not null)
        {
            var at = name.IndexOfAny(RevitViewRules.ForbiddenNameCharacters.ToCharArray());
            if (at >= 0)
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.name", "Revit names cannot contain '" + name[at] + "' (none of " + RevitViewRules.ForbiddenNameCharacters + ")");
            if (name.Trim().Length == 0 || name.Trim().Length != name.Length)
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.name", "grid name must not be blank or start or end with white space");
        }
        return new GridCreateArgs(start, end, name);
    }

    static Length OptionalOffset(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var element)) return Length.FromMetres(0);
        var offset = RevitUnits.Millimetres(Number(element), "args." + name, allowNegative: true, allowZero: true);
        if (Math.Abs(offset.Millimetres) > RevitCommandGeometry.MaxRevitLengthMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args." + name, "offset beyond 9144000 mm (Revit's 30000 ft limit)");
        return offset;
    }

    static RevitCommandArgs BindColumn(JsonElement args)
    {
        var point = Coordinates(args, "point", "args.point");
        var baseLevel = args.GetProperty("base_level").GetString()!;
        var topLevel = args.GetProperty("top_level").GetString()!;
        if (string.Equals(baseLevel, topLevel, StringComparison.Ordinal)) throw new RevitCommandException(RevitCommandCodes.LevelsNotDistinct, "args.top_level", "base_level and top_level must name different levels");
        var baseOffset = OptionalOffset(args, "base_offset_mm");
        var topOffset = OptionalOffset(args, "top_offset_mm");
        return new StructuralColumnCreateArgs(args.GetProperty("type").GetString()!, ToPoint(point), baseLevel, topLevel, baseOffset, topOffset);
    }

    static RevitCommandArgs BindBeam(JsonElement args)
    {
        var (start, end) = BindStraightLine(args, "beam line");
        return new StructuralBeamCreateArgs(args.GetProperty("level").GetString()!, args.GetProperty("type").GetString()!, start, end);
    }

    static RevitCommandArgs BindIsolatedFoundation(JsonElement args) =>
        new IsolatedFoundationCreateArgs(args.GetProperty("level").GetString()!, args.GetProperty("type").GetString()!, ToPoint(Coordinates(args, "point", "args.point")));

    static RevitCommandArgs BindWallFoundation(JsonElement args) =>
        new WallFoundationCreateArgs(args.GetProperty("host_wall_uid").GetString()!, args.GetProperty("type").GetString()!);
}
