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

    const int ArrayBuffer = 34962, ElementArrayBuffer = 34963, Float = 5126, UnsignedInt = 5125;
    static readonly string[] MaterialKinds = [GeometryCodec.Wall, GeometryCodec.Space];
    static readonly Dictionary<string, string> MaterialJson = new()
    {
        [GeometryCodec.Wall] = "{\"name\":\"bim-wall\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.8,0.8,0.78,1],\"metallicFactor\":0,\"roughnessFactor\":0.9}}",
        [GeometryCodec.Space] = "{\"name\":\"bim-space\",\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.35,0.6,0.85,1],\"metallicFactor\":0,\"roughnessFactor\":1},\"doubleSided\":true}",
    };

    sealed record MeshData(int ElementIndex, string Kind, float[] Positions, float[] Normals, uint[] Indices);

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

    // Test seam: wrap lets a test inject a stream that fails part-way.
    internal static void WriteGlb(Project project, string path, Func<Stream, Stream> wrap)
    {
        var bytes = ExportGlb(project);
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
            if (GeometryCodec.Problem(e) is string code) throw new GltfExportException(code, e.Id.ToString("D"));
        }
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

    static MeshData Wall(int index, Element e, double z0)
    {
        var g = e.GeometryMetres;
        double ax = g[0], ay = g[1], bx = g[2], by = g[3], h = g[4], t = g[5];
        double dx = bx - ax, dy = by - ay, len = Math.Sqrt(dx * dx + dy * dy);
        double ux = dx / len, uy = dy / len, nx = -uy, ny = ux, ht = t / 2, z1 = z0 + h;
        // Plan corners: left of the axis (+n) and right of it (-n), at start A and end B. The axis is the centre line.
        double[] p0 = [ax + nx * ht, ay + ny * ht], p1 = [bx + nx * ht, by + ny * ht], p2 = [bx - nx * ht, by - ny * ht], p3 = [ax - nx * ht, ay - ny * ht];
        double[] At(double[] p, double z) => [p[0], p[1], z];
        var positions = new List<float>(72); var normals = new List<float>(72); var indices = new List<uint>(36);
        Quad(positions, normals, indices, [At(p0, z0), At(p1, z0), At(p2, z0), At(p3, z0)], 0, 0, -1); // bottom
        Quad(positions, normals, indices, [At(p3, z1), At(p2, z1), At(p1, z1), At(p0, z1)], 0, 0, 1);  // top
        Quad(positions, normals, indices, [At(p3, z0), At(p2, z0), At(p2, z1), At(p3, z1)], -nx, -ny, 0); // right face
        Quad(positions, normals, indices, [At(p2, z0), At(p1, z0), At(p1, z1), At(p2, z1)], ux, uy, 0);   // end face
        Quad(positions, normals, indices, [At(p1, z0), At(p0, z0), At(p0, z1), At(p1, z1)], nx, ny, 0);   // left face
        Quad(positions, normals, indices, [At(p0, z0), At(p3, z0), At(p3, z1), At(p0, z1)], -ux, -uy, 0); // start face
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
                    meshes.Add(Wall(i, e, z0));
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
                .Append(",\"type\":null,\"level\":").Append(level[e.Id] is Guid l ? Str(l.ToString("D")) : "null").Append("}}");
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
