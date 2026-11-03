using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Bim.Core;

// P2-01 mapping from observed Revit records (Mirror.cs) to BIM-IR elements. Pure and deterministic: the same
// observation in any order gives byte-identical JSON. Elements use only the kinds and layouts BIM-IR already has:
//   level  [elevation]                                  no dependencies
//   wall   [ax,ay,bx,by,height,thickness]               [level]
//   door / window [ax,ay,bx,by,sill,height]             [level, host wall]   (the IntentCompiler opening layout)
//   space  [corner x, corner y, width, depth]           [level, bounding walls in id order]
// Everything else goes to the unmapped list, which is part of the query reply only.
public sealed record MirrorSnapshot(string DocumentUid, IReadOnlyList<Element> Elements, IReadOnlyDictionary<Guid, string> Sources,
    IReadOnlyDictionary<Guid, string> Categories, IReadOnlyList<MirrorUnmapped> Unmapped)
{
    // {"snapshot_version":1,"document_unique_id":..,"units":"m","elements":[BIM-IR Element JSON..],
    //  "sources":[{"id","unique_id","category"}..],"unmapped":[{"category","unique_id","reason","detail"}..]}
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("snapshot_version", MirrorSnapshotMapper.Version);
            writer.WriteString("document_unique_id", DocumentUid);
            writer.WriteString("units", LengthUnits.Metres);
            writer.WriteStartArray("elements");
            // Exactly the persisted BIM-IR Element serialization, so clients can deserialize Element records.
            foreach (var element in Elements) writer.WriteRawValue(JsonSerializer.Serialize(element));
            writer.WriteEndArray();
            writer.WriteStartArray("sources");
            foreach (var element in Elements)
            {
                writer.WriteStartObject();
                writer.WriteString("id", element.Id.ToString("D"));
                writer.WriteString("unique_id", Sources[element.Id]);
                writer.WriteString("category", Categories[element.Id]);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("unmapped");
            foreach (var entry in Unmapped)
            {
                writer.WriteStartObject();
                writer.WriteString("category", entry.Category);
                writer.WriteString("unique_id", entry.UniqueId);
                writer.WriteString("reason", entry.Reason);
                writer.WriteString("detail", entry.Detail);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

public static class MirrorSnapshotMapper
{
    public const int Version = 1;
    public const string IdNamespace = "bim-agentic-os/mirror/v1";
    public static readonly Provenance Provenance = new("revit-snapshot", "observed");
    // Points closer than this (millimetres) are the same; edges whose cross-axis drift stays within it are axis-aligned.
    public const double ToleranceMm = 1e-3;

    // Stable logical id: the first 16 bytes of SHA-256 over the namespace, the document UniqueId and the element
    // UniqueId (both lower-cased), the same derivation style RoomCompiler.Id uses.
    public static Guid Id(string documentUid, string uniqueId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(IdNamespace + "\n" + documentUid.ToLowerInvariant() + "\n" + uniqueId.ToLowerInvariant())).Take(16).ToArray());

    static int Rank(string kind) => kind switch { "level" => 0, "wall" => 1, "door" => 2, "window" => 3, _ => 4 };

    sealed class Builder(string document)
    {
        public readonly List<(Element Element, string Uid, string Category)> Mapped = new();
        public readonly List<MirrorUnmapped> Unmapped = new();
        public Guid Id(string uid) => MirrorSnapshotMapper.Id(document, uid);
        public void Add(string kind, string category, string uid, double[] metres, Guid[] dependencies) =>
            Mapped.Add((new Element(Id(uid), kind, metres, dependencies, Provenance), uid, category));
        public void Skip(string category, string uid, string reason, string detail) => Unmapped.Add(new(category, uid, reason, detail));
    }

    // Millimetres -> metres through the typed unit, after removing sub-micrometre conversion noise; -0 becomes 0.
    static double M(double millimetres) => Length.FromMillimetres(LengthUnits.NormalizeMillimetres(millimetres) + 0.0).Metres;
    static bool Finite(params double[] values) => values.All(double.IsFinite);
    static bool Finite(MirrorPoint point) => Finite(point.XMm, point.YMm);

    public static MirrorSnapshot Map(MirrorObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (string.IsNullOrWhiteSpace(observation.DocumentUid)) throw new ArgumentException("document_unique_id_required");
        var b = new Builder(observation.DocumentUid);

        // Identity first: no UniqueId, or a UniqueId seen twice (ignoring case), cannot give a stable id.
        var all = observation.Levels.Select(level => (Category: MirrorCategories.Level, Uid: level.UniqueId ?? ""))
            .Concat(observation.Elements.Select(element => (Category: element.Category ?? "", Uid: element.UniqueId ?? ""))).ToArray();
        var repeated = all.Where(item => item.Uid.Length > 0).GroupBy(item => item.Uid.ToLowerInvariant()).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        bool Identified(string category, string? uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) { b.Skip(category, uid ?? "", MirrorReasons.UniqueIdMissing, "element has no UniqueId, so no stable id can be derived"); return false; }
            if (repeated.Contains(uid.ToLowerInvariant())) { b.Skip(category, uid, MirrorReasons.DuplicateUniqueId, "UniqueId appears more than once (ignoring case) in the observation"); return false; }
            return true;
        }

        var levels = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var level in observation.Levels)
        {
            if (!Identified(MirrorCategories.Level, level.UniqueId)) continue;
            if (level.ElevationMm is not { } elevation) { b.Skip(MirrorCategories.Level, level.UniqueId, MirrorReasons.MissingData, "level elevation could not be read"); continue; }
            if (!Finite(elevation)) { b.Skip(MirrorCategories.Level, level.UniqueId, MirrorReasons.InvalidGeometry, "level elevation is not a finite number"); continue; }
            b.Add("level", MirrorCategories.Level, level.UniqueId, new[] { M(elevation) }, Array.Empty<Guid>());
            levels[level.UniqueId] = b.Id(level.UniqueId);
        }
        Guid? LevelOf(MirrorObservedElement element) => element.LevelUid is { } uid && levels.TryGetValue(uid, out var id) ? id : null;

        var identified = observation.Elements.Where(element => Identified(element.Category ?? "", element.UniqueId)).ToArray();
        // Straight mapped walls by UniqueId, for openings (host axis) and rooms (bounding walls).
        var walls = new Dictionary<string, (Guid Id, MirrorPoint Start, MirrorPoint End)>(StringComparer.OrdinalIgnoreCase);
        foreach (var wall in identified.Where(element => element.Category == MirrorCategories.Wall)) MapWall(b, wall, LevelOf(wall), walls);
        foreach (var element in identified)
        {
            switch (element.Category)
            {
                case MirrorCategories.Wall: break;
                case MirrorCategories.Door:
                case MirrorCategories.Window: MapOpening(b, element, LevelOf(element), walls); break;
                case MirrorCategories.Room: MapRoom(b, element, LevelOf(element), walls); break;
                case MirrorCategories.Floor: b.Skip(element.Category, element.UniqueId, MirrorReasons.NoBimIrKind, "BIM-IR has no floor or slab element kind; adding one needs an owner-accepted ADR"); break;
                default: b.Skip(element.Category ?? "", element.UniqueId, MirrorReasons.CategoryUnsupported, "category is not exported by query.snapshot (walls, doors, windows, floors and rooms are)"); break;
            }
        }

        var ordered = b.Mapped.OrderBy(item => Rank(item.Element.Kind)).ThenBy(item => item.Uid, StringComparer.Ordinal).ThenBy(item => item.Element.Id).ToArray();
        var unmapped = b.Unmapped.OrderBy(entry => entry.Category, StringComparer.Ordinal).ThenBy(entry => entry.UniqueId, StringComparer.Ordinal).ThenBy(entry => entry.Reason, StringComparer.Ordinal).ToArray();
        return new MirrorSnapshot(observation.DocumentUid, ordered.Select(item => item.Element).ToArray(),
            ordered.ToDictionary(item => item.Element.Id, item => item.Uid), ordered.ToDictionary(item => item.Element.Id, item => item.Category), unmapped);
    }

    static void MapWall(Builder b, MirrorObservedElement wall, Guid? level, Dictionary<string, (Guid, MirrorPoint, MirrorPoint)> walls)
    {
        const string C = MirrorCategories.Wall;
        if (wall.Curved) { b.Skip(C, wall.UniqueId, MirrorReasons.CurvedWall, "curved wall: BIM-IR walls are straight axes"); return; }
        if (wall.Start is not { } start || wall.End is not { } end) { b.Skip(C, wall.UniqueId, MirrorReasons.MissingData, "wall has no straight location line"); return; }
        if (wall.HeightMm is not { } height) { b.Skip(C, wall.UniqueId, MirrorReasons.MissingData, "wall unconnected height could not be read"); return; }
        if (wall.ThicknessMm is not { } thickness) { b.Skip(C, wall.UniqueId, MirrorReasons.MissingData, "wall width could not be read"); return; }
        if (!Finite(start) || !Finite(end) || !Finite(height, thickness)) { b.Skip(C, wall.UniqueId, MirrorReasons.InvalidGeometry, "wall has a non-finite coordinate, height or width"); return; }
        if (level is not { } levelId) { b.Skip(C, wall.UniqueId, MirrorReasons.LevelUnknown, "wall base level is missing or not mapped in this snapshot"); return; }
        var geometry = new[] { M(start.XMm), M(start.YMm), M(end.XMm), M(end.YMm), M(height), M(thickness) };
        if (!GeometryCodec.IsValidWall(geometry)) { b.Skip(C, wall.UniqueId, MirrorReasons.InvalidGeometry, "wall axis has zero length or height or width is not positive"); return; }
        b.Add("wall", C, wall.UniqueId, geometry, new[] { levelId });
        walls[wall.UniqueId] = (b.Id(wall.UniqueId), start, end);
    }

    static void MapOpening(Builder b, MirrorObservedElement opening, Guid? level, Dictionary<string, (Guid Id, MirrorPoint Start, MirrorPoint End)> walls)
    {
        var c = opening.Category;
        var isDoor = c == MirrorCategories.Door;
        if (opening.Location is not { } location) { b.Skip(c, opening.UniqueId, MirrorReasons.MissingData, c + " has no insertion point"); return; }
        if (opening.WidthMm is not { } width) { b.Skip(c, opening.UniqueId, MirrorReasons.MissingData, c + " width could not be read"); return; }
        if (opening.HeightMm is not { } height) { b.Skip(c, opening.UniqueId, MirrorReasons.MissingData, c + " height could not be read"); return; }
        // A door without a readable sill sits on its level; a window needs its sill.
        double? sillOrNull = opening.SillMm ?? (isDoor ? 0 : null);
        if (sillOrNull is not { } sill) { b.Skip(c, opening.UniqueId, MirrorReasons.MissingData, "window sill height could not be read"); return; }
        if (!Finite(location) || !Finite(width, height, sill) || width <= 0 || height <= 0 || sill < 0) { b.Skip(c, opening.UniqueId, MirrorReasons.InvalidGeometry, c + " point, width, height or sill is non-finite, or width/height not positive, or sill negative"); return; }
        if (level is not { } levelId) { b.Skip(c, opening.UniqueId, MirrorReasons.LevelUnknown, c + " level is missing or not mapped in this snapshot"); return; }
        if (opening.HostUid is not { } hostUid || !walls.TryGetValue(hostUid, out var host)) { b.Skip(c, opening.UniqueId, MirrorReasons.HostUnknown, c + " host is missing or is not a straight wall mapped in this snapshot"); return; }
        double ax = host.Start.XMm, ay = host.Start.YMm, dx = host.End.XMm - ax, dy = host.End.YMm - ay;
        var length = Math.Sqrt(dx * dx + dy * dy);
        double ux = dx / length, uy = dy / length;
        // Project the insertion point onto the host axis; the opening spans width/2 either side, in host direction.
        var along = (location.XMm - ax) * ux + (location.YMm - ay) * uy;
        double from = along - width / 2, to = along + width / 2;
        if (from < -ToleranceMm || to > length + ToleranceMm) { b.Skip(c, opening.UniqueId, MirrorReasons.OpeningOutsideHost, c + " does not fit inside its host wall axis"); return; }
        var geometry = new[] { M(ax + from * ux), M(ay + from * uy), M(ax + to * ux), M(ay + to * uy), M(sill), M(height) };
        b.Add(isDoor ? "door" : "window", c, opening.UniqueId, geometry, new[] { levelId, host.Id });
    }

    static void MapRoom(Builder b, MirrorObservedElement room, Guid? level, Dictionary<string, (Guid Id, MirrorPoint Start, MirrorPoint End)> walls)
    {
        const string C = MirrorCategories.Room;
        if (room.Loops is not { Count: > 0 } loops) { b.Skip(C, room.UniqueId, MirrorReasons.RoomUnplaced, "room is not placed or not enclosed (no boundary)"); return; }
        if (loops.Count != 1 || loops[0].HasCurves || Rectangle(loops[0].Vertices) is not { } box) { b.Skip(C, room.UniqueId, MirrorReasons.RoomNotRectangular, "room boundary is not one axis-aligned rectangle with straight edges"); return; }
        if (level is not { } levelId) { b.Skip(C, room.UniqueId, MirrorReasons.LevelUnknown, "room level is missing or not mapped in this snapshot"); return; }
        var bounding = loops[0].BoundingUids.Where(uid => !string.IsNullOrEmpty(uid) && walls.ContainsKey(uid)).Select(uid => walls[uid].Id).Distinct().Order();
        b.Add("space", C, room.UniqueId, new[] { M(box.MinX), M(box.MinY), M(box.MaxX - box.MinX), M(box.MaxY - box.MinY) }, new[] { levelId }.Concat(bounding).ToArray());
    }

    // The axis-aligned rectangle a loop describes after dropping repeated and collinear vertices, or null.
    static (double MinX, double MinY, double MaxX, double MaxY)? Rectangle(IReadOnlyList<MirrorPoint> vertices)
    {
        if (vertices.Any(point => !Finite(point))) return null;
        bool Same(MirrorPoint p, MirrorPoint q) => Math.Abs(p.XMm - q.XMm) <= ToleranceMm && Math.Abs(p.YMm - q.YMm) <= ToleranceMm;
        var points = new List<MirrorPoint>();
        foreach (var point in vertices) if (points.Count == 0 || !Same(points[^1], point)) points.Add(point);
        while (points.Count > 1 && Same(points[0], points[^1])) points.RemoveAt(points.Count - 1);
        // Every edge must be horizontal or vertical; then drop vertices between two edges of the same direction.
        bool Horizontal(MirrorPoint p, MirrorPoint q) => Math.Abs(p.YMm - q.YMm) <= ToleranceMm;
        bool Vertical(MirrorPoint p, MirrorPoint q) => Math.Abs(p.XMm - q.XMm) <= ToleranceMm;
        var n = points.Count;
        if (n < 4) return null;
        for (var i = 0; i < n; i++) if (!Horizontal(points[i], points[(i + 1) % n]) && !Vertical(points[i], points[(i + 1) % n])) return null;
        var corners = Enumerable.Range(0, n).Where(i => Horizontal(points[(i + n - 1) % n], points[i]) != Horizontal(points[i], points[(i + 1) % n])).Select(i => points[i]).ToArray();
        if (corners.Length != 4) return null;
        double minX = corners.Min(p => p.XMm), maxX = corners.Max(p => p.XMm), minY = corners.Min(p => p.YMm), maxY = corners.Max(p => p.YMm);
        if (maxX - minX <= ToleranceMm || maxY - minY <= ToleranceMm) return null;
        // Four turning corners must be exactly the box corners (a self-crossing outline is refused).
        foreach (var corner in corners)
            if (!((Math.Abs(corner.XMm - minX) <= ToleranceMm || Math.Abs(corner.XMm - maxX) <= ToleranceMm) && (Math.Abs(corner.YMm - minY) <= ToleranceMm || Math.Abs(corner.YMm - maxY) <= ToleranceMm))) return null;
        if (corners.Select(p => (Math.Abs(p.XMm - minX) <= ToleranceMm, Math.Abs(p.YMm - minY) <= ToleranceMm)).Distinct().Count() != 4) return null;
        // Back-tracking edges (a spike along one side) would also pass the corner test; the perimeter must match.
        double perimeter = 0;
        for (var i = 0; i < n; i++) perimeter += Math.Abs(points[(i + 1) % n].XMm - points[i].XMm) + Math.Abs(points[(i + 1) % n].YMm - points[i].YMm);
        if (Math.Abs(perimeter - 2 * ((maxX - minX) + (maxY - minY))) > 4 * ToleranceMm * n) return null;
        return (minX, minY, maxX, maxY);
    }
}
