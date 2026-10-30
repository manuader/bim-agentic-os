using Bim.Core;
namespace Bim.Revit;

// C03 (ADR0009) red step: signatures only.
public sealed record PlanPoint(double X, double Y);
public sealed record ExpectedWall(bool IsArc, PlanPoint Start, PlanPoint End, double LengthMm, double? RadiusMm, PlanPoint? Center);
public sealed record ObservedWall(string UniqueId, string LevelUid, string TypeUid, bool IsArc, PlanPoint Start, PlanPoint End, double LengthMm, double? RadiusMm, PlanPoint? Center, double? HeightMm, double? BaseOffsetMm, bool? Structural);
public sealed record ObservedOpening(string UniqueId, string? HostUid, string SymbolUid, PlanPoint? Point, double? SillMm);
public sealed record ObservedFloor(string UniqueId, string LevelUid, string TypeUid, double? AreaMm2);
public sealed record ObservedRoom(string UniqueId, string LevelUid, string? Name, string? Number, double AreaMm2);
public sealed record LevelRow(string Name, string UniqueId, double ElevationMm, double ProjectElevationMm);
public sealed record WallTypeRow(string Name, string UniqueId, string Kind, double? WidthMm);
public sealed record FloorTypeRow(string Name, string UniqueId, double? WidthMm);
public sealed record SymbolRow(string Family, string Type, string UniqueId, bool Active, bool WallHosted);

public static class CommandGeometry
{
    public static ExpectedWall Wall(WallCurve curve) => throw new NotImplementedException(curve.ToString());
    public static double OpeningParameter(double hostLengthMm, double positionMm, string field) => throw new NotImplementedException();
    public static double PolygonAreaMm2(IReadOnlyList<Point2> boundary) => throw new NotImplementedException();
}

public static class CommandReadback
{
    public static string? Wall(ExpectedWall expected, string levelUid, string typeUid, double heightMm, double baseOffsetMm, bool structural, ObservedWall observed) => throw new NotImplementedException();
    public static string? Opening(string hostUid, string symbolUid, PlanPoint expectedPoint, double? sillMm, ObservedOpening observed) => throw new NotImplementedException();
    public static string? Floor(string levelUid, string typeUid, double expectedAreaMm2, ObservedFloor observed) => throw new NotImplementedException();
    public static string? Room(string levelUid, string? name, string? number, ObservedRoom observed) => throw new NotImplementedException();
    public static string LevelsJson(IEnumerable<LevelRow> rows) => throw new NotImplementedException();
    public static string WallTypesJson(IEnumerable<WallTypeRow> rows) => throw new NotImplementedException();
    public static string FloorTypesJson(IEnumerable<FloorTypeRow> rows) => throw new NotImplementedException();
    public static string SymbolsJson(string category, IEnumerable<SymbolRow> rows) => throw new NotImplementedException();
    public static string WallJson(ObservedWall wall) => throw new NotImplementedException();
    public static string OpeningJson(string category, ObservedOpening opening) => throw new NotImplementedException();
    public static string FloorJson(ObservedFloor floor) => throw new NotImplementedException();
    public static string RoomJson(ObservedRoom room) => throw new NotImplementedException();
}
