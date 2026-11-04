using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Bim.Core;

namespace Bim.Gltf;

// Stable refusal: Code is one of the documented codes (adapters/gltf/README.md), Detail names the element.
public sealed class GltfExportException(string code, string detail) : Exception(code + ": " + detail)
{
    public string Code { get; } = code;
    public string Detail { get; } = detail;
}

// BIM-IR Project -> glTF 2.0 (binary .glb or .gltf with an embedded base64 buffer) for Blender and other viewers.
// Deterministic: the same Project gives the same bytes on every OS and culture (fixed member order, invariant
// shortest round-trip float text, little-endian binary, no timestamps). Metres; BIM Z-up is mapped to glTF Y-up
// as (x, y, z) -> (x, z, -y), a proper rotation, so triangle winding is kept.
public static class GltfExporter
{
    public const string Schema = "bim-agentic-os.gltf/1";
    public const string Generator = "bim-agentic-os Bim.Gltf";
    // Bounded input: more elements are refused before any other work.
    public const int MaxElements = 10_000;
    // float32 positions: adjacent representable values are 2^-10 m (about 0.98 mm) apart from 8192 m up to 16384 m,
    // so every vertex within +/-1e4 m keeps sub-millimetre placement.
    public const double MaxCoordinate = 1e4;
    public const double MinLength = 1e-3;
    // Openings: an end point within this distance of the host axis, its ends, a neighbour opening, the host base or the
    // host top is on it; a pier, sill wall or lintel shorter than this is not emitted.
    public const double OpeningTolerance = 1e-6;
    public const string Door = "door", Window = "window";

    const int ArrayBuffer = 34962, ElementArrayBuffer = 34963, Float = 5126, UnsignedInt = 5125;
    static readonly string[] MaterialKinds = [GeometryCodec.Wall, GeometryCodec.Space];
    static readonly Dictionary<string, string> MaterialJson = new()
    {
        [GeometryCodec.Wall] = "{\"name\":\"bim-wall\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.8,0.8,0.78,1],\"metallicFactor\":0,\"roughnessFactor\":0.9}}",
        [GeometryCodec.Space] = "{\"name\":\"bim-space\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.35,0.6,0.85,1],\"metallicFactor\":0,\"roughnessFactor\":1},\"doubleSided\":true}",
    };

    sealed record MeshData(int ElementIndex, string Kind, float[] Positions, float[] Normals, uint[] Indices);
    // One opening cut in its host wall: T0 < T1 are distances along the host axis from its start; P0/P1 the axis
    // points where the cut starts and ends; Bottom/Top the cut's sill and head heights above the host base.
    sealed record Cut(Guid Id, double T0, double T1, double[] P0, double[] P1, double Bottom, double Top);

    // Count of door/window elements the export cuts into host walls (all of them once validation passes).
    public static int OpeningCount(Project project) => project?.Elements?.Count(e => e?.Kind is Door or Window) ?? 0;

    public static byte[] ExportGlb(Project project)
    {
        var (json, bin) = Build(project, embedUri: false);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var jsonPadded = Pad4(jsonBytes.Length);
        var total = 12 + 8 + jsonPadded + (bin.Length > 0 ? 8 + bin.Length : 0);
        var glb = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(0), 0x46546C67); // "glTF"
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), (uint)jsonPadded);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4E4F534A); // "JSON"
        jsonBytes.CopyTo(glb, 20);
        for (var i = 20 + jsonBytes.Length; i < 20 + jsonPadded; i++) glb[i] = 0x20;
        if (bin.Length > 0)
        {
            var at = 20 + jsonPadded;
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(at), (uint)bin.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(at + 4), 0x004E4942); // "BIN\0"
            bin.CopyTo(glb, at + 8);
        }
        return glb;
    }

    // .gltf JSON text; the single buffer is a data: URI, so the file is self-contained.
    public static string ExportGltf(Project project) => Build(project, embedUri: true).Json;

    // Writes a new .glb; never replaces an existing file (output_exists).
    public static void WriteGlb(Project project, string path) => WriteGlb(project, path, stream => stream);

    // Writes a new .gltf (UTF-8 without BOM); never replaces an existing file (output_exists).
    public static void WriteGltf(Project project, string path) => WriteNew(new UTF8Encoding(false).GetBytes(ExportGltf(project)), path, stream => stream);

    // Test seam: wrap lets a test inject a stream that fails part-way.
    internal static void WriteGlb(Project project, string path, Func<Stream, Stream> wrap) => WriteNew(ExportGlb(project), path, wrap);

    // Creates path exclusively (FileMode.CreateNew) and writes bytes (output_exists when the path exists); on a failed
    // write it removes the file it created and rethrows. Used by the CLI `export-gltf` verb with ExportGlb/ExportGltf bytes.
    public static void WriteNew(byte[] bytes, string path) => WriteNew(bytes, path, stream => stream);

    static void WriteNew(byte[] bytes, string path, Func<Stream, Stream> wrap)
    {
        FileStream file;
        try { file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        // Windows reports an existing directory as UnauthorizedAccessException, Unix as IOException.
        catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && (File.Exists(path) || Directory.Exists(path)))
        {
            throw new GltfExportException("output_exists", path);
        }
        try
        {
            using (file) { var stream = wrap(file); stream.Write(bytes); stream.Flush(); file.Flush(true); }
        }
        catch
        {
            // This call created the file, so a failed write removes it instead of leaving a truncated .glb.
            try { File.Delete(path); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    static int Pad4(int length) => (length + 3) & ~3;

    static void Validate(Project? project)
    {
        if (project is null) throw new GltfExportException("bim_ir_invalid", "project is null");
        if (project.Elements is null) throw new GltfExportException("bim_ir_invalid", "Elements is null");
        if (project.Elements.Length > MaxElements) throw new GltfExportException("gltf_too_many_elements", project.Elements.Length.ToString(CultureInfo.InvariantCulture) + " > " + MaxElements.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < project.Elements.Length; i++)
        {
            var e = project.Elements[i];
            if (e is null || e.Kind is null || e.GeometryMetres is null || e.Dependencies is null)
                throw new GltfExportException("bim_ir_invalid", "Elements[" + i.ToString(CultureInfo.InvariantCulture) + "] is incomplete");
        }
        var issues = Compiler.Validate(project);
        if (issues.Length > 0) throw new GltfExportException(issues[0].Code, issues[0].Id?.ToString("D") ?? issues[0].Message);
        foreach (var e in project.Elements)
        {
            // Door/window [ax,ay,bx,by,sill,height] (IntentCompiler) have no GeometryCodec arity; checked in Cuts.
            if (e.Kind is Door or Window)
            {
                if (e.GeometryMetres.Length != 6) throw new GltfExportException("gltf_opening_geometry", e.Id.ToString("D") + " needs [ax,ay,bx,by,sill,height]");
                continue;
            }
            if (GeometryCodec.Problem(e) is string code) throw new GltfExportException(code, e.Id.ToString("D"));
        }
    }

    // The single wall a door/window is hosted by: exactly one of its dependencies is a wall.
    static Element Host(Element opening, Dictionary<Guid, Element> byId)
    {
        var walls = opening.Dependencies.Distinct().Where(d => byId[d].Kind == GeometryCodec.Wall).ToArray();
        if (walls.Length != 1) throw new GltfExportException("gltf_opening_host", opening.Id.ToString("D") + " needs exactly one wall dependency, has " + walls.Length.ToString(CultureInfo.InvariantCulture));
        return byId[walls[0]];
    }

    // Openings per host wall, sorted along the axis and validated: on the axis, inside the span and height, no
    // overlap, and every remaining pier, sill wall and lintel either absent (within OpeningTolerance) or >= MinLength.
    static Dictionary<Guid, List<Cut>> Cuts(Project project, Dictionary<Guid, Element> byId)
    {
        const double Tol = OpeningTolerance;
        var cuts = new Dictionary<Guid, List<Cut>>();
        foreach (var o in project.Elements)
        {
            if (o.Kind is not (Door or Window)) continue;
            var host = Host(o, byId);
            var id = o.Id.ToString("D");
            double[] g = o.GeometryMetres, w = host.GeometryMetres;
            double ax = w[0], ay = w[1], dx = w[2] - w[0], dy = w[3] - w[1], len = Math.Sqrt(dx * dx + dy * dy), ux = dx / len, uy = dy / len;
            double Along(double x, double y) => (x - ax) * ux + (y - ay) * uy;
            double Across(double x, double y) => Math.Abs(-(x - ax) * uy + (y - ay) * ux);
            double t0 = Along(g[0], g[1]), t1 = Along(g[2], g[3]), sill = g[4], height = g[5];
            if (!(Math.Abs(t1 - t0) > 0) || !(height > 0)) throw new GltfExportException("gltf_opening_geometry", id + " width and height must be positive");
            LengthOk(Math.Abs(t1 - t0), "opening width", o); LengthOk(height, "opening height", o);
            if (Across(g[0], g[1]) > Tol || Across(g[2], g[3]) > Tol) throw new GltfExportException("gltf_opening_outside_wall", id + " end points are off the host axis");
            double[] p0 = [g[0], g[1]], p1 = [g[2], g[3]];
            if (t1 < t0) { (t0, t1) = (t1, t0); (p0, p1) = (p1, p0); }
            if (t0 < -Tol || t1 > len + Tol || sill < -Tol || sill + height > w[4] + Tol) throw new GltfExportException("gltf_opening_outside_wall", id + " is outside its host span or height");
            // Snap to the host start, end, base and top.
            if (t0 <= Tol) { t0 = 0; p0 = [w[0], w[1]]; }
            if (len - t1 <= Tol) { t1 = len; p1 = [w[2], w[3]]; }
            var bottom = sill <= Tol ? 0 : sill;
            var top = w[4] - (sill + height) <= Tol ? w[4] : sill + height;
            if (bottom > 0 && bottom < MinLength) throw new GltfExportException("gltf_precision", id + " sill wall under 1 mm");
            if (top < w[4] && w[4] - top < MinLength) throw new GltfExportException("gltf_precision", id + " lintel under 1 mm");
            if (!cuts.TryGetValue(host.Id, out var list)) cuts[host.Id] = list = [];
            list.Add(new Cut(o.Id, t0, t1, p0, p1, bottom, top));
        }
        foreach (var (hostId, list) in cuts)
        {
            list.Sort((a, b) => a.T0 != b.T0 ? a.T0.CompareTo(b.T0) : a.Id.CompareTo(b.Id));
            var len = WallLength(byId[hostId]);
            double previous = 0;
            for (var i = 0; i < list.Count; i++)
            {
                var c = list[i];
                var gap = c.T0 - previous;
                if (gap < -Tol) throw new GltfExportException("gltf_opening_overlap", c.Id.ToString("D") + " overlaps " + list[i - 1].Id.ToString("D"));
                if (i > 0 && gap <= Tol) list[i] = c = c with { T0 = list[i - 1].T1, P0 = list[i - 1].P1 };
                else if (gap > Tol && gap < MinLength) throw new GltfExportException("gltf_precision", c.Id.ToString("D") + " pier before it under 1 mm");
                previous = c.T1;
            }
            var tail = len - previous;
            if (tail > Tol && tail < MinLength) throw new GltfExportException("gltf_precision", list[^1].Id.ToString("D") + " pier after it under 1 mm");
        }
        return cuts;
    }

    static double WallLength(Element wall)
    {
        var g = wall.GeometryMetres;
        return Math.Sqrt((g[2] - g[0]) * (g[2] - g[0]) + (g[3] - g[1]) * (g[3] - g[1]));
    }

    static void Coordinate(double value, string what, Element e)
    {
        if (!(Math.Abs(value) <= MaxCoordinate)) throw new GltfExportException("gltf_precision", e.Id.ToString("D") + " " + what + " is beyond +/-1e4 m");
    }

    static void LengthOk(double value, string what, Element e)
    {
        if (!(value >= MinLength && value <= MaxCoordinate)) throw new GltfExportException("gltf_precision", e.Id.ToString("D") + " " + what + " is outside [1e-3, 1e4] m");
    }

    // Level of an element: its first level dependency, else the level of its first dependency that has one.
    // Elements are visited in dependency order (validated acyclic), so no recursion is needed.
    static Dictionary<Guid, Guid?> Levels(Project project)
    {
        var byId = project.Elements.ToDictionary(e => e.Id);
        var pending = project.Elements.ToDictionary(e => e.Id, e => e.Dependencies.Length);
        var dependents = project.Elements.ToDictionary(e => e.Id, _ => new List<Guid>());
        foreach (var e in project.Elements) foreach (var d in e.Dependencies) dependents[d].Add(e.Id);
        var ready = new Queue<Guid>(project.Elements.Where(e => e.Dependencies.Length == 0).Select(e => e.Id));
        var level = new Dictionary<Guid, Guid?>();
        while (ready.TryDequeue(out var id))
        {
            var e = byId[id];
            Guid? found = e.Kind == GeometryCodec.Level ? e.Id : null;
            if (found is null) foreach (var d in e.Dependencies) if (byId[d].Kind == GeometryCodec.Level) { found = d; break; }
            if (found is null) foreach (var d in e.Dependencies) if (level[d] is Guid l) { found = l; break; }
            level[id] = found;
            foreach (var child in dependents[id]) if (--pending[child] == 0) ready.Enqueue(child);
        }
        return level;
    }

    static float F(double value) { var f = (float)value; return f == 0f ? 0f : f; }

    static void Vertex(List<float> positions, List<float> normals, double x, double y, double z, double nx, double ny, double nz)
    {
        positions.Add(F(x)); positions.Add(F(z)); positions.Add(F(-y));
        normals.Add(F(nx)); normals.Add(F(nz)); normals.Add(F(-ny));
    }

    // Quad q0..q3 counter-clockwise seen from the side its normal points to; two triangles (0,1,2) (0,2,3).
    static void Quad(List<float> positions, List<float> normals, List<uint> indices, double[][] q, double nx, double ny, double nz)
    {
        var b = (uint)(positions.Count / 3);
        foreach (var p in q) Vertex(positions, normals, p[0], p[1], p[2], nx, ny, nz);
        indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
    }

    // A wall without openings is one flat-shaded box from its start A to its end B. With openings it is split into boxes
    // in axis order: the pier before each opening, then the part below it (sill wall, when sill > 0) and above it
    // (lintel, when its head is below the top), and the final pier. Zero-length parts are not emitted.
    static MeshData Wall(int index, Element e, double z0, List<Cut>? cuts)
    {
        var g = e.GeometryMetres;
        double ax = g[0], ay = g[1], bx = g[2], by = g[3], h = g[4], t = g[5];
        double dx = bx - ax, dy = by - ay, len = Math.Sqrt(dx * dx + dy * dy);
        double ux = dx / len, uy = dy / len, nx = -uy, ny = ux, ht = t / 2, z1 = z0 + h;
        var boxes = 1 + 3 * (cuts?.Count ?? 0);
        var positions = new List<float>(72 * boxes); var normals = new List<float>(72 * boxes); var indices = new List<uint>(36 * boxes);
        void Box(double[] a, double[] b, double za, double zb)
        {
            // Plan corners: left of the axis (+n) and right of it (-n), at a and b. The axis is the centre line.
            double[] p0 = [a[0] + nx * ht, a[1] + ny * ht], p1 = [b[0] + nx * ht, b[1] + ny * ht], p2 = [b[0] - nx * ht, b[1] - ny * ht], p3 = [a[0] - nx * ht, a[1] - ny * ht];
            double[] At(double[] p, double z) => [p[0], p[1], z];
            Quad(positions, normals, indices, [At(p0, za), At(p1, za), At(p2, za), At(p3, za)], 0, 0, -1); // bottom
            Quad(positions, normals, indices, [At(p3, zb), At(p2, zb), At(p1, zb), At(p0, zb)], 0, 0, 1);  // top
            Quad(positions, normals, indices, [At(p3, za), At(p2, za), At(p2, zb), At(p3, zb)], -nx, -ny, 0); // right face
            Quad(positions, normals, indices, [At(p2, za), At(p1, za), At(p1, zb), At(p2, zb)], ux, uy, 0);   // end face
            Quad(positions, normals, indices, [At(p1, za), At(p0, za), At(p0, zb), At(p1, zb)], nx, ny, 0);   // left face
            Quad(positions, normals, indices, [At(p0, za), At(p3, za), At(p3, zb), At(p0, zb)], -ux, -uy, 0); // start face
        }
        double[] cursor = [ax, ay];
        double at = 0;
        foreach (var c in cuts ?? new List<Cut>())
        {
            if (c.T0 > at) Box(cursor, c.P0, z0, z1);
            if (c.Bottom > 0) Box(c.P0, c.P1, z0, z0 + c.Bottom);
            if (c.Top < h) Box(c.P0, c.P1, z0 + c.Top, z1);
            cursor = c.P1; at = c.T1;
        }
        if (at < len) Box(cursor, [bx, by], z0, z1);
        return new(index, GeometryCodec.Wall, positions.ToArray(), normals.ToArray(), indices.ToArray());
    }

    static MeshData Space(int index, Element e, double z)
    {
        var g = e.GeometryMetres;
        double x = g[0], y = g[1], w = g[2], d = g[3];
        var positions = new List<float>(12); var normals = new List<float>(12); var indices = new List<uint>(6);
        Quad(positions, normals, indices, [[x, y, z], [x + w, y, z], [x + w, y + d, z], [x, y + d, z]], 0, 0, 1);
        return new(index, GeometryCodec.Space, positions.ToArray(), normals.ToArray(), indices.ToArray());
    }

    static (string Json, byte[] Bin) Build(Project? input, bool embedUri)
    {
        Validate(input);
        var project = input!;
        var byId = project.Elements.ToDictionary(e => e.Id);
        var level = Levels(project);
        double Elevation(Guid id) => level[id] is Guid l ? byId[l].GeometryMetres[0] : 0;
        var hostOf = new Dictionary<Guid, Guid>();

        var meshes = new List<MeshData>();
        for (var i = 0; i < project.Elements.Length; i++)
        {
            var e = project.Elements[i];
            var g = e.GeometryMetres;
            switch (e.Kind)
            {
                case GeometryCodec.Wall:
                {
                    var z0 = Elevation(e.Id);
                    Coordinate(g[0], "start.x", e); Coordinate(g[1], "start.y", e); Coordinate(g[2], "end.x", e); Coordinate(g[3], "end.y", e);
                    LengthOk(Math.Sqrt((g[2] - g[0]) * (g[2] - g[0]) + (g[3] - g[1]) * (g[3] - g[1])), "axis length", e);
                    LengthOk(g[4], "height", e); LengthOk(g[5], "thickness", e);
                    Coordinate(z0 + g[4], "top elevation", e);
                    // The faces sit thickness/2 either side of the axis, so their corners are checked too.
                    double dx = g[2] - g[0], dy = g[3] - g[1], len = Math.Sqrt(dx * dx + dy * dy), ox = -dy / len * (g[5] / 2), oy = dx / len * (g[5] / 2);
                    Coordinate(g[0] + ox, "start face corner.x", e); Coordinate(g[1] + oy, "start face corner.y", e); Coordinate(g[0] - ox, "start face corner.x", e); Coordinate(g[1] - oy, "start face corner.y", e);
                    Coordinate(g[2] + ox, "end face corner.x", e); Coordinate(g[3] + oy, "end face corner.y", e); Coordinate(g[2] - ox, "end face corner.x", e); Coordinate(g[3] - oy, "end face corner.y", e);
                    meshes.Add(Wall(i, e, z0, null));
                    break;
                }
                case GeometryCodec.Space:
                {
                    if (!(g[2] > 0 && g[3] > 0)) throw new GltfExportException("gltf_space_geometry", e.Id.ToString("D") + " width and depth must be positive");
                    LengthOk(g[2], "width", e); LengthOk(g[3], "depth", e);
                    Coordinate(g[0], "corner.x", e); Coordinate(g[1], "corner.y", e); Coordinate(g[0] + g[2], "far corner.x", e); Coordinate(g[1] + g[3], "far corner.y", e);
                    meshes.Add(Space(i, e, Elevation(e.Id)));
                    break;
                }
                case GeometryCodec.Level:
                    Coordinate(g[0], "elevation", e);
                    break;
            }
        }

        // Openings: validated once every wall is, then the host meshes are rebuilt with their cuts (same mesh slot).
        var cuts = Cuts(project, byId);
        foreach (var e in project.Elements) if (e.Kind is Door or Window) hostOf[e.Id] = Host(e, byId).Id;
        for (var m = 0; m < meshes.Count; m++)
        {
            var e = project.Elements[meshes[m].ElementIndex];
            if (e.Kind == GeometryCodec.Wall && cuts.TryGetValue(e.Id, out var wallCuts)) meshes[m] = Wall(meshes[m].ElementIndex, e, Elevation(e.Id), wallCuts);
        }

        var kinds = MaterialKinds.Where(k => meshes.Any(m => m.Kind == k)).ToList();
        var meshOf = new Dictionary<int, int>();
        for (var m = 0; m < meshes.Count; m++) meshOf[meshes[m].ElementIndex] = m;

        int positionBytes = meshes.Sum(m => m.Positions.Length) * 4, normalBytes = positionBytes, indexBytes = meshes.Sum(m => m.Indices.Length) * 4;
        var bin = new byte[positionBytes + normalBytes + indexBytes];
        int pAt = 0, nAt = positionBytes, iAt = positionBytes + normalBytes;

        var j = new StringBuilder();
        j.Append("{\"asset\":{\"generator\":").Append(Str(Generator)).Append(",\"version\":\"2.0\"}");
        j.Append(",\"scene\":0,\"scenes\":[{\"name\":").Append(Str("BIM project " + project.Id.ToString("D"))).Append(",\"nodes\":[0]}]");
        j.Append(",\"nodes\":[{\"name\":").Append(Str("project " + project.Id.ToString("D")));
        if (project.Elements.Length > 0)
        {
            j.Append(",\"children\":[");
            for (var i = 0; i < project.Elements.Length; i++) j.Append(i == 0 ? "" : ",").Append(I(i + 1));
            j.Append(']');
        }
        j.Append(",\"extras\":{\"schema\":").Append(Str(Schema)).Append(",\"bim_project_id\":").Append(Str(project.Id.ToString("D")))
            .Append(",\"revision\":").Append(I(project.Revision)).Append(",\"units\":\"m\",\"up\":\"Y\"}}");
        for (var i = 0; i < project.Elements.Length; i++)
        {
            var e = project.Elements[i];
            var id = e.Id.ToString("D");
            j.Append(",{\"name\":").Append(Str(e.Kind + " " + id));
            if (meshOf.TryGetValue(i, out var mi)) j.Append(",\"mesh\":").Append(I(mi));
            if (e.Kind == GeometryCodec.Level) j.Append(",\"translation\":[0,").Append(N(F(e.GeometryMetres[0]))).Append(",0]");
            j.Append(",\"extras\":{\"bim_id\":").Append(Str(id)).Append(",\"kind\":").Append(Str(e.Kind))
                .Append(",\"type\":null,\"level\":").Append(level[e.Id] is Guid l ? Str(l.ToString("D")) : "null");
            if (hostOf.TryGetValue(e.Id, out var host)) j.Append(",\"host\":").Append(Str(host.ToString("D")));
            j.Append("}}");
        }
        j.Append(']');

        if (meshes.Count > 0)
        {
            j.Append(",\"meshes\":[");
            for (var m = 0; m < meshes.Count; m++)
            {
                var e = project.Elements[meshes[m].ElementIndex];
                j.Append(m == 0 ? "" : ",").Append("{\"name\":").Append(Str(e.Kind + " " + e.Id.ToString("D")))
                    .Append(",\"primitives\":[{\"attributes\":{\"POSITION\":").Append(I(3 * m)).Append(",\"NORMAL\":").Append(I(3 * m + 1))
                    .Append("},\"indices\":").Append(I(3 * m + 2)).Append(",\"material\":").Append(I(kinds.IndexOf(meshes[m].Kind))).Append(",\"mode\":4}]}");
            }
            j.Append("],\"materials\":[").Append(string.Join(",", kinds.Select(k => MaterialJson[k]))).Append(']');

            j.Append(",\"accessors\":[");
            int pOff = 0, iOff = 0;
            for (var m = 0; m < meshes.Count; m++)
            {
                var mesh = meshes[m];
                var count = mesh.Positions.Length / 3;
                var min = new float[3]; var max = new float[3];
                for (var c = 0; c < 3; c++) { min[c] = float.PositiveInfinity; max[c] = float.NegativeInfinity; }
                for (var v = 0; v < mesh.Positions.Length; v++) { var c = v % 3; min[c] = Math.Min(min[c], mesh.Positions[v]); max[c] = Math.Max(max[c], mesh.Positions[v]); }
                j.Append(m == 0 ? "" : ",")
                    .Append("{\"bufferView\":0,\"byteOffset\":").Append(I(pOff)).Append(",\"componentType\":").Append(I(Float)).Append(",\"count\":").Append(I(count))
                    .Append(",\"type\":\"VEC3\",\"min\":[").Append(N(min[0])).Append(',').Append(N(min[1])).Append(',').Append(N(min[2]))
                    .Append("],\"max\":[").Append(N(max[0])).Append(',').Append(N(max[1])).Append(',').Append(N(max[2])).Append("]}")
                    .Append(",{\"bufferView\":1,\"byteOffset\":").Append(I(pOff)).Append(",\"componentType\":").Append(I(Float)).Append(",\"count\":").Append(I(count)).Append(",\"type\":\"VEC3\"}")
                    .Append(",{\"bufferView\":2,\"byteOffset\":").Append(I(iOff)).Append(",\"componentType\":").Append(I(UnsignedInt)).Append(",\"count\":").Append(I(mesh.Indices.Length)).Append(",\"type\":\"SCALAR\"}");
                foreach (var v in mesh.Positions) { BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pAt), v); pAt += 4; }
                foreach (var v in mesh.Normals) { BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(nAt), v); nAt += 4; }
                foreach (var v in mesh.Indices) { BinaryPrimitives.WriteUInt32LittleEndian(bin.AsSpan(iAt), v); iAt += 4; }
                pOff += mesh.Positions.Length * 4; iOff += mesh.Indices.Length * 4;
            }
            j.Append(']');
            // Positions and normals each share one view across meshes, so byteStride is required (glTF 2.0 3.6.2.1).
            j.Append(",\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":").Append(I(positionBytes)).Append(",\"byteStride\":12,\"target\":").Append(I(ArrayBuffer)).Append('}')
                .Append(",{\"buffer\":0,\"byteOffset\":").Append(I(positionBytes)).Append(",\"byteLength\":").Append(I(normalBytes)).Append(",\"byteStride\":12,\"target\":").Append(I(ArrayBuffer)).Append('}')
                .Append(",{\"buffer\":0,\"byteOffset\":").Append(I(positionBytes + normalBytes)).Append(",\"byteLength\":").Append(I(indexBytes)).Append(",\"target\":").Append(I(ElementArrayBuffer)).Append("}]");
            j.Append(",\"buffers\":[{\"byteLength\":").Append(I(bin.Length));
            if (embedUri) j.Append(",\"uri\":\"data:application/octet-stream;base64,").Append(Convert.ToBase64String(bin)).Append('"');
            j.Append("}]");
        }
        j.Append('}');
        return (j.ToString(), bin);
    }

    static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

    // Shortest round-trip float32 text; zero is always written as 0 (never -0).
    static string N(float value) => (value == 0f ? 0f : value).ToString(CultureInfo.InvariantCulture);

    static string Str(string value)
    {
        var s = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            if (c == '"' || c == '\\') s.Append('\\').Append(c);
            else if (c < 0x20) s.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else s.Append(c);
        }
        return s.Append('"').ToString();
    }
}
