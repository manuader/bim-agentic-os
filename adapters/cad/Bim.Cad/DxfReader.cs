namespace Bim.Cad;

public static class DxfReader
{
    public static DxfDrawing ReadFile(string path, DxfReadOptions? options = null) => throw new NotImplementedException();

    public static DxfDrawing Parse(byte[] bytes, string sourceFile, DxfReadOptions? options = null) => throw new NotImplementedException();

    public static DxfDrawing Parse(string text, string sourceFile, DxfReadOptions? options = null) => throw new NotImplementedException();
}

public static class DxfExploder
{
    public static DxfExplodedDrawing Explode(DxfDrawing drawing, DxfExplodeOptions options) => throw new NotImplementedException();
}

public static class DxfGeometry
{
    public static IReadOnlyList<DxfPolylineSegment> Segments(DxfPolyline polyline) => throw new NotImplementedException();

    public static Point2 SegmentMidpoint(DxfPolylineSegment segment) => throw new NotImplementedException();
}

public static class WallPairDetector
{
    public static IReadOnlyList<WallPair> Detect(DxfDrawing drawing, WallPairOptions options) => throw new NotImplementedException();

    public static IReadOnlyList<WallPair> Detect(IEnumerable<DxfEntity> entities, WallPairOptions options) => throw new NotImplementedException();
}

public static class DxfJson
{
    public static string Serialize(DxfDrawing drawing) => throw new NotImplementedException();

    public static string Serialize(IReadOnlyList<WallPair> pairs) => throw new NotImplementedException();
}
