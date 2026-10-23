namespace Bim.Core;

public readonly record struct GeometryField(string Name, Length Value);

// Decoded, typed views of Element.GeometryMetres. The persisted double[] stays the only truth;
// these views are built from it and encode back to bit-identical arrays.
public abstract record ElementGeometry
{
    public abstract string Kind { get; }
    // Field order equals the persisted array order.
    public abstract GeometryField[] Fields();
}

// Wall axis points (not faces), unconnected height and thickness: [ax,ay,bx,by,height,thickness].
public sealed record WallGeometry(Point2 Start, Point2 End, Length Height, Length Thickness) : ElementGeometry
{
    public override string Kind => GeometryCodec.Wall;
    public override GeometryField[] Fields() => new GeometryField[]
    {
        new("start.x", Start.X), new("start.y", Start.Y), new("end.x", End.X), new("end.y", End.Y),
        new("height", Height), new("thickness", Thickness),
    };
}

// Level elevation: [elevation].
public sealed record LevelGeometry(Length Elevation) : ElementGeometry
{
    public override string Kind => GeometryCodec.Level;
    public override GeometryField[] Fields() => new GeometryField[] { new("elevation", Elevation) };
}

// Space interior corner (inner wall faces) and clear size: [x,y,width,depth].
public sealed record SpaceGeometry(Point2 InteriorCorner, Length Width, Length Depth) : ElementGeometry
{
    public override string Kind => GeometryCodec.Space;
    public Area ClearArea => Area.Of(Width, Depth);
    public override GeometryField[] Fields() => new GeometryField[]
    {
        new("corner.x", InteriorCorner.X), new("corner.y", InteriorCorner.Y), new("width", Width), new("depth", Depth),
    };
}

// Plan, schedule and sheet carry no geometry: [].
public sealed record DocumentationGeometry(string ViewKind) : ElementGeometry
{
    public override string Kind => ViewKind;
    public override GeometryField[] Fields() => Array.Empty<GeometryField>();
}

// The single place that knows per-kind geometry arity and layout.
public static class GeometryCodec
{
    public const string Wall = "wall";
    public const string Level = "level";
    public const string Space = "space";
    public const string KindUnsupported = "geometry_kind_unsupported";
    public const string NonFinite = "geometry_non_finite";
    public const string WallInvalid = "wall_geometry";
    public const string LevelInvalid = "level_geometry";
    public const string SpaceInvalid = "space_geometry";
    public const string DocumentationInvalid = "documentation_geometry";

    public static int? ExpectedArity(string kind) => kind switch
    {
        Wall => 6,
        Level => 1,
        Space => 4,
        "plan" or "schedule" or "sheet" => 0,
        _ => null,
    };

    public static bool HasExpectedArity(Element element) => ExpectedArity(element.Kind) is int arity && element.GeometryMetres.Length == arity;

    // Exactly the wall rule Compiler.Validate has always applied (non-finite values are reported separately).
    public static bool IsValidWall(double[] geometry) =>
        !(geometry.Length != 6 || geometry[4] <= 0 || geometry[5] <= 0 || (geometry[0] == geometry[2] && geometry[1] == geometry[3]));

    // Stable code describing why the element cannot be decoded, or null when it can.
    public static string? Problem(Element element)
    {
        var geometry = element.GeometryMetres;
        if (ExpectedArity(element.Kind) is not int arity) return KindUnsupported;
        if (geometry.Length != arity)
        {
            return element.Kind switch { Wall => WallInvalid, Level => LevelInvalid, Space => SpaceInvalid, _ => DocumentationInvalid };
        }
        if (geometry.Any(value => !double.IsFinite(value))) return NonFinite;
        if (element.Kind == Wall && !IsValidWall(geometry)) return WallInvalid;
        return null;
    }

    public static ElementGeometry Decode(Element element)
    {
        if (Problem(element) is string code) throw new ArgumentException(code);
        var g = element.GeometryMetres;
        static Length M(double metres) => Length.FromMetres(metres);
        return element.Kind switch
        {
            Wall => new WallGeometry(new Point2(M(g[0]), M(g[1])), new Point2(M(g[2]), M(g[3])), M(g[4]), M(g[5])),
            Level => new LevelGeometry(M(g[0])),
            Space => new SpaceGeometry(new Point2(M(g[0]), M(g[1])), M(g[2]), M(g[3])),
            _ => new DocumentationGeometry(element.Kind),
        };
    }

    public static double[] Encode(ElementGeometry geometry)
    {
        double[] values = geometry switch
        {
            WallGeometry wall => new[] { wall.Start.X.Metres, wall.Start.Y.Metres, wall.End.X.Metres, wall.End.Y.Metres, wall.Height.Metres, wall.Thickness.Metres },
            LevelGeometry level => new[] { level.Elevation.Metres },
            SpaceGeometry space => new[] { space.InteriorCorner.X.Metres, space.InteriorCorner.Y.Metres, space.Width.Metres, space.Depth.Metres },
            DocumentationGeometry documentation when ExpectedArity(documentation.ViewKind) == 0 => Array.Empty<double>(),
            _ => throw new ArgumentException(KindUnsupported),
        };
        return values;
    }
}
