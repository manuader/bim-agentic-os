using Bim.Core;
using Bim.Gltf;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
// P8-04 BIM-IR -> glTF 2.0 oracle (adapters/gltf/Bim.Gltf). The structural validator below implements the glTF 2.0
// rules the exporter relies on (GLB header and chunks, asset.version, buffer/bufferView/accessor bounds and alignment,
// byteStride for shared vertex views, POSITION min/max equal to the data, index ranges, unit normals, winding, node,
// mesh, material and scene references). No Blender is involved: a passing run is unit-tested evidence only.
int passed = 0, failed = 0;
void Check(bool condition, string name, string detail = "")
{
    if (condition) { Console.WriteLine("PASS " + name); passed++; return; }
    Console.WriteLine("::error::gltf FAIL " + name + (detail.Length > 0 ? " | " + detail : ""));
    failed++;
}
string? Code(Action action)
{
    try { action(); return null; }
    catch (GltfExportException error) { return error.Code; }
    catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
}
string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

// Golden hashes of the fixture below, computed by an independent byte-level reimplementation of the documented mapping.
const string GoldenGlb = "28495187A66863D0BFD5F9E4CF91B78249AEAA018875A44623A2CAE1F2062FF9";
const string GoldenGltf = "D3AB72246F4435ACD6A1D66105993BC364BB0B16B4FECC08C0C6640AE21C9C0E";
const string GoldenEmptyGlb = "5427747C5A9F2E47C77BDAAAE83421FAAEF8C780046E903FC956B8112C82152D";
// P8-07 opening fixture (wall 6 m with a door and a window), from the same independent reimplementation extended with
// the documented opening split; it reproduces the three goldens above before producing these.
const string GoldenOpeningsGlb = "47A14D5CE34105E4145CBBD9207B7D7EEBFC472BAB8B23EBF9686AB677966D3F";
const string GoldenOpeningsGltf = "039FC0A70FB5BEB58EDE15A3BC59816C0EE2A31CDA4BFBCC5B399F7C1FA79DBF";

var prov = new Provenance("test", "approved");
Guid G(string s) => Guid.Parse(s);
var L1 = G("0a000000-0000-4000-8000-000000000001"); var L2 = G("0a000000-0000-4000-8000-000000000002");
var W1 = G("0b000000-0000-4000-8000-000000000001"); var W2 = G("0b000000-0000-4000-8000-000000000002"); var W3 = G("0b000000-0000-4000-8000-000000000003");
var SP = G("0c000000-0000-4000-8000-000000000001"); var PL = G("0d000000-0000-4000-8000-000000000001");
var PID = G("3f0c1d2e-0000-4000-8000-000000000001");
Element E(Guid id, string kind, double[] g, params Guid[] deps) => new(id, kind, g, deps, prov);
// Axis-aligned dyadic values: every float32 position and normal is exact, so the golden bytes have no rounding ambiguity.
var fixture = new Project(PID, 7, [
    E(L1, "level", [0]), E(L2, "level", [3]),
    E(W1, "wall", [0, 0, 4, 0, 3, 0.25], L1), E(W2, "wall", [4, 0, 4, 3, 3, 0.25], L1),
    E(W3, "wall", [0, 0, 0, 3, 2.5, 0.5], L2),
    E(SP, "space", [0.125, 0.125, 3.75, 2.75], W1, W2),
    E(PL, "plan", [], L1, SP)]);

// ---------------- structural validator (glTF 2.0) ----------------
(List<string> Issues, JsonElement Root, byte[] Bin) Parse(byte[] glb)
{
    var issues = new List<string>();
    var empty = JsonDocument.Parse("{}").RootElement;
    if (glb.Length < 20) { issues.Add("glb shorter than header + chunk header"); return (issues, empty, Array.Empty<byte>()); }
    if (BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(0)) != 0x46546C67) issues.Add("magic is not glTF");
    if (BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(4)) != 2) issues.Add("GLB version is not 2");
    if (BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8)) != glb.Length) issues.Add("header length != file length");
    var jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12));
    if (BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(16)) != 0x4E4F534A) issues.Add("first chunk is not JSON");
    if (jsonLength % 4 != 0) issues.Add("JSON chunk length not a multiple of 4");
    if (20L + jsonLength > glb.Length) { issues.Add("JSON chunk overruns file"); return (issues, empty, Array.Empty<byte>()); }
    var jsonBytes = glb.AsSpan(20, jsonLength).ToArray();
    if (jsonBytes.Length >= 3 && jsonBytes[0] == 0xEF && jsonBytes[1] == 0xBB && jsonBytes[2] == 0xBF) issues.Add("JSON chunk has a BOM");
    // Padding after the JSON value must be spaces (0x20), byte for byte.
    var lastBrace = Array.LastIndexOf(jsonBytes, (byte)'}');
    for (var i = lastBrace + 1; i < jsonBytes.Length; i++) if (jsonBytes[i] != 0x20) { issues.Add("JSON chunk padding is not 0x20"); break; }
    JsonElement root;
    try { root = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(jsonBytes).TrimEnd(' ')).RootElement; }
    catch (Exception error) { issues.Add("JSON chunk does not parse: " + error.Message); return (issues, empty, Array.Empty<byte>()); }
    var bin = Array.Empty<byte>();
    var at = 20 + jsonLength;
    if (at < glb.Length)
    {
        if (at + 8 > glb.Length) issues.Add("BIN chunk header truncated");
        else
        {
            var binLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(at));
            if (BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(at + 4)) != 0x004E4942) issues.Add("second chunk is not BIN");
            if (binLength % 4 != 0) issues.Add("BIN chunk length not a multiple of 4");
            if ((long)at + 8 + binLength != glb.Length) issues.Add("BIN chunk does not end the file exactly");
            else bin = glb.AsSpan(at + 8, binLength).ToArray();
        }
    }
    issues.AddRange(ValidateJson(root, bin, glbMode: true));
    return (issues, root, bin);
}

int Int(JsonElement e, string name, int fallback = -1) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;
JsonElement[] Arr(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
int ComponentSize(int type) => type switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => 0 };
int Components(string? type) => type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT2" => 4, "MAT3" => 9, "MAT4" => 16, _ => 0 };

List<string> ValidateJson(JsonElement root, byte[] bin, bool glbMode)
{
    var issues = new List<string>();
    if (!root.TryGetProperty("asset", out var asset) || !asset.TryGetProperty("version", out var version) || version.GetString() != "2.0") issues.Add("asset.version is not 2.0");
    var buffers = Arr(root, "buffers"); var views = Arr(root, "bufferViews"); var accessors = Arr(root, "accessors");
    var meshes = Arr(root, "meshes"); var nodes = Arr(root, "nodes"); var materials = Arr(root, "materials"); var scenes = Arr(root, "scenes");
    foreach (var name in new[] { "buffers", "bufferViews", "accessors", "meshes", "materials", "nodes", "scenes" })
        if (root.TryGetProperty(name, out var a) && a.GetArrayLength() == 0) issues.Add(name + " present but empty (minItems 1)");
    if (glbMode)
    {
        if (buffers.Length > 1) issues.Add("GLB has more than one buffer");
        if (buffers.Length == 1 && buffers[0].TryGetProperty("uri", out _)) issues.Add("GLB buffer 0 has a uri");
        if (buffers.Length == 1 && (Int(buffers[0], "byteLength") > bin.Length || bin.Length - Int(buffers[0], "byteLength") > 3)) issues.Add("buffer byteLength does not match BIN chunk");
        if (buffers.Length == 0 && bin.Length > 0) issues.Add("BIN chunk without buffer");
        if (buffers.Length == 1)
        {
            var used = Math.Max(Int(buffers[0], "byteLength"), 0);
            for (var i = used; i < bin.Length; i++) if (bin[i] != 0) { issues.Add("BIN chunk padding is not zero"); break; }
        }
    }
    foreach (var b in buffers) if (Int(b, "byteLength") < 1) issues.Add("buffer byteLength < 1");
    var accessorsPerView = new int[views.Length];
    for (var v = 0; v < views.Length; v++)
    {
        var view = views[v];
        int buffer = Int(view, "buffer"), offset = Int(view, "byteOffset", 0), length = Int(view, "byteLength"), stride = Int(view, "byteStride", 0), target = Int(view, "target", 0);
        if (buffer < 0 || buffer >= buffers.Length) { issues.Add($"bufferView {v} buffer index invalid"); continue; }
        if (length < 1) issues.Add($"bufferView {v} byteLength < 1");
        if (offset % 4 != 0) issues.Add($"bufferView {v} byteOffset not 4-aligned");
        if ((long)offset + length > Int(buffers[buffer], "byteLength")) issues.Add($"bufferView {v} overruns buffer");
        if (view.TryGetProperty("byteStride", out _) && (stride < 4 || stride > 252 || stride % 4 != 0)) issues.Add($"bufferView {v} byteStride invalid");
        if (target is not (0 or 34962 or 34963)) issues.Add($"bufferView {v} target invalid");
        if (target == 34963 && view.TryGetProperty("byteStride", out _)) issues.Add($"bufferView {v} index view has byteStride");
    }
    for (var a = 0; a < accessors.Length; a++)
    {
        var acc = accessors[a];
        int view = Int(acc, "bufferView"), offset = Int(acc, "byteOffset", 0), count = Int(acc, "count"), componentType = Int(acc, "componentType");
        var type = acc.TryGetProperty("type", out var t) ? t.GetString() : null;
        int size = ComponentSize(componentType), comps = Components(type);
        if (size == 0) issues.Add($"accessor {a} componentType invalid");
        if (comps == 0) issues.Add($"accessor {a} type invalid");
        if (count < 1) issues.Add($"accessor {a} count < 1");
        if (view < 0 || view >= views.Length) { issues.Add($"accessor {a} bufferView invalid"); continue; }
        accessorsPerView[view]++;
        if (size == 0 || comps == 0 || count < 1) continue;
        var viewOffset = Int(views[view], "byteOffset", 0);
        if (offset % size != 0 || (offset + viewOffset) % size != 0) issues.Add($"accessor {a} offset not aligned to component size");
        var elementSize = size * comps;
        var stride = Int(views[view], "byteStride", elementSize);
        if ((long)offset + (long)stride * (count - 1) + elementSize > Int(views[view], "byteLength")) issues.Add($"accessor {a} overruns bufferView");
        foreach (var bound in new[] { "min", "max" })
            if (acc.TryGetProperty(bound, out var arr) && arr.GetArrayLength() != comps) issues.Add($"accessor {a} {bound} has wrong length");
    }
    for (var v = 0; v < views.Length; v++)
        if (accessorsPerView[v] > 1 && Int(views[v], "target", 0) == 34962 && !views[v].TryGetProperty("byteStride", out _)) issues.Add($"bufferView {v} shared by vertex accessors without byteStride");

    var binBytes = bin;
    float ReadF(int accessor, int index, int component)
    {
        var acc = accessors[accessor]; var view = views[Int(acc, "bufferView")];
        var at = Int(view, "byteOffset", 0) + Int(acc, "byteOffset", 0) + Int(view, "byteStride", 12) * index + 4 * component;
        return BinaryPrimitives.ReadSingleLittleEndian(binBytes.AsSpan(at));
    }
    uint ReadIndex(int accessor, int index)
    {
        var acc = accessors[accessor]; var view = views[Int(acc, "bufferView")];
        var at = Int(view, "byteOffset", 0) + Int(acc, "byteOffset", 0) + 4 * index;
        return BinaryPrimitives.ReadUInt32LittleEndian(binBytes.AsSpan(at));
    }
    var dataReadable = issues.Count == 0 && binBytes.Length > 0;
    for (var m = 0; m < meshes.Length; m++)
    {
        var primitives = Arr(meshes[m], "primitives");
        if (primitives.Length == 0) issues.Add($"mesh {m} has no primitives");
        foreach (var prim in primitives)
        {
            if (!prim.TryGetProperty("attributes", out var attrs)) { issues.Add($"mesh {m} primitive without attributes"); continue; }
            int pos = Int(attrs, "POSITION"), nor = Int(attrs, "NORMAL"), ind = Int(prim, "indices"), mat = Int(prim, "material", 0), mode = Int(prim, "mode", 4);
            if (pos < 0 || pos >= accessors.Length) { issues.Add($"mesh {m} POSITION missing"); continue; }
            var pa = accessors[pos];
            if (Int(pa, "componentType") != 5126 || pa.GetProperty("type").GetString() != "VEC3") issues.Add($"mesh {m} POSITION not FLOAT VEC3");
            if (!pa.TryGetProperty("min", out var min) || !pa.TryGetProperty("max", out var max)) { issues.Add($"mesh {m} POSITION min/max missing"); continue; }
            if (mode != 4) issues.Add($"mesh {m} mode is not TRIANGLES");
            if (mat < 0 || mat >= materials.Length) issues.Add($"mesh {m} material index invalid");
            var count = Int(pa, "count");
            if (nor >= 0 && (nor >= accessors.Length || Int(accessors[nor], "count") != count || accessors[nor].GetProperty("type").GetString() != "VEC3")) issues.Add($"mesh {m} NORMAL does not match POSITION");
            if (nor >= 0 && nor < accessors.Length && Int(accessors[nor], "componentType") != 5126) issues.Add($"mesh {m} NORMAL not FLOAT");
            if (ind < 0 || ind >= accessors.Length) { issues.Add($"mesh {m} indices missing"); continue; }
            var ia = accessors[ind];
            if (Int(ia, "componentType") is not (5121 or 5123 or 5125) || ia.GetProperty("type").GetString() != "SCALAR") issues.Add($"mesh {m} indices not unsigned SCALAR");
            if (Int(views[Int(ia, "bufferView")], "target", 0) != 34963) issues.Add($"mesh {m} indices view target is not ELEMENT_ARRAY_BUFFER");
            if (Int(ia, "count") % 3 != 0) issues.Add($"mesh {m} index count not a multiple of 3");
            if (!dataReadable) continue;
            for (var c = 0; c < 3; c++)
            {
                float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
                for (var i = 0; i < count; i++) { var f = ReadF(pos, i, c); lo = Math.Min(lo, f); hi = Math.Max(hi, f); }
                if (min[c].GetSingle() != lo || max[c].GetSingle() != hi) issues.Add($"mesh {m} POSITION min/max[{c}] differ from data ({min[c]}/{max[c]} vs {lo}/{hi})");
            }
            var indexCount = Int(ia, "count");
            var indicesInRange = true;
            for (var i = 0; i < indexCount; i++) if (ReadIndex(ind, i) >= count) { issues.Add($"mesh {m} index {i} out of range"); indicesInRange = false; break; }
            if (nor >= 0)
            {
                for (var i = 0; i < count; i++)
                {
                    double x = ReadF(nor, i, 0), y = ReadF(nor, i, 1), z = ReadF(nor, i, 2);
                    if (Math.Abs(Math.Sqrt(x * x + y * y + z * z) - 1) > 1e-6) { issues.Add($"mesh {m} normal {i} not unit"); break; }
                }
                // Counter-clockwise front faces: each triangle's geometric normal agrees with its vertex normal.
                for (var i = 0; i + 2 < indexCount; i += 3)
                {
                    int a = (int)ReadIndex(ind, i), b = (int)ReadIndex(ind, i + 1), c = (int)ReadIndex(ind, i + 2);
                    if (a >= count || b >= count || c >= count) break;
                    double[] P(int k) => [ReadF(pos, k, 0), ReadF(pos, k, 1), ReadF(pos, k, 2)];
                    var pa0 = P(a); var pb = P(b); var pc = P(c);
                    double ux = pb[0] - pa0[0], uy = pb[1] - pa0[1], uz = pb[2] - pa0[2], vx = pc[0] - pa0[0], vy = pc[1] - pa0[1], vz = pc[2] - pa0[2];
                    double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
                    if (cx * ReadF(nor, a, 0) + cy * ReadF(nor, a, 1) + cz * ReadF(nor, a, 2) <= 0) { issues.Add($"mesh {m} triangle {i / 3} winding disagrees with normal"); break; }
                }
            }
            // Closed meshes (every edge, by position, shared by exactly two triangles) must face outward: each
            // triangle's geometric normal points away from the vertex centroid. Catches inside-out walls whose
            // winding and vertex normals were flipped together.
            if (indicesInRange && indexCount % 3 == 0)
            {
                (float, float, float) V(int k) => (ReadF(pos, k, 0), ReadF(pos, k, 1), ReadF(pos, k, 2));
                var edgeUse = new Dictionary<((float, float, float), (float, float, float)), int>();
                for (var i = 0; i < indexCount; i += 3)
                {
                    for (var e = 0; e < 3; e++)
                    {
                        var p = V((int)ReadIndex(ind, i + e)); var q = V((int)ReadIndex(ind, i + (e + 1) % 3));
                        var key = Comparer<(float, float, float)>.Default.Compare(p, q) <= 0 ? (p, q) : (q, p);
                        edgeUse[key] = edgeUse.GetValueOrDefault(key) + 1;
                    }
                }
                if (edgeUse.Count > 0 && edgeUse.Values.All(u => u == 2))
                {
                    double mx = 0, my = 0, mz = 0;
                    for (var k = 0; k < count; k++) { mx += ReadF(pos, k, 0); my += ReadF(pos, k, 1); mz += ReadF(pos, k, 2); }
                    mx /= count; my /= count; mz /= count;
                    for (var i = 0; i < indexCount; i += 3)
                    {
                        var a = V((int)ReadIndex(ind, i)); var b = V((int)ReadIndex(ind, i + 1)); var c = V((int)ReadIndex(ind, i + 2));
                        double ux = b.Item1 - a.Item1, uy = b.Item2 - a.Item2, uz = b.Item3 - a.Item3, vx = c.Item1 - a.Item1, vy = c.Item2 - a.Item2, vz = c.Item3 - a.Item3;
                        double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
                        double tx = (a.Item1 + b.Item1 + c.Item1) / 3 - mx, ty = (a.Item2 + b.Item2 + c.Item2) / 3 - my, tz = (a.Item3 + b.Item3 + c.Item3) / 3 - mz;
                        if (tx * cx + ty * cy + tz * cz <= 0) { issues.Add($"mesh {m} triangle {i / 3} faces inward (inside-out closed mesh)"); break; }
                    }
                }
            }
        }
    }
    var meshUse = new int[meshes.Length];
    var childOf = new int[nodes.Length];
    for (var n = 0; n < nodes.Length; n++)
    {
        var mesh = Int(nodes[n], "mesh");
        if (nodes[n].TryGetProperty("mesh", out _)) { if (mesh < 0 || mesh >= meshes.Length) issues.Add($"node {n} mesh invalid"); else meshUse[mesh]++; }
        foreach (var child in Arr(nodes[n], "children"))
        {
            var c = child.GetInt32();
            if (c < 0 || c >= nodes.Length || c == n) issues.Add($"node {n} child invalid"); else childOf[c]++;
        }
        if (nodes[n].TryGetProperty("children", out var ch) && ch.GetArrayLength() == 0) issues.Add($"node {n} children empty");
        foreach (var (prop, length) in new[] { ("translation", 3), ("rotation", 4), ("scale", 3), ("matrix", 16) })
            if (nodes[n].TryGetProperty(prop, out var trs) && (trs.ValueKind != JsonValueKind.Array || trs.GetArrayLength() != length || trs.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Number)))
                issues.Add($"node {n} {prop} must have {length} numbers");
        if (nodes[n].TryGetProperty("matrix", out _) && (nodes[n].TryGetProperty("translation", out _) || nodes[n].TryGetProperty("rotation", out _) || nodes[n].TryGetProperty("scale", out _)))
            issues.Add($"node {n} has matrix together with TRS");
    }
    for (var n = 0; n < nodes.Length; n++) if (childOf[n] > 1) issues.Add($"node {n} has more than one parent");
    for (var m = 0; m < meshes.Length; m++) if (meshUse[m] != 1) issues.Add($"mesh {m} used by {meshUse[m]} nodes (expected 1)");
    var scene = Int(root, "scene", 0);
    if (root.TryGetProperty("scene", out _) && (scene < 0 || scene >= scenes.Length)) issues.Add("scene index invalid");
    foreach (var s in scenes) foreach (var n in Arr(s, "nodes")) { var i = n.GetInt32(); if (i < 0 || i >= nodes.Length || childOf[i] > 0) issues.Add("scene root node invalid or not a root"); }
    foreach (var mat in materials)
    {
        if (mat.TryGetProperty("doubleSided", out var ds) && ds.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) issues.Add("material doubleSided is not a boolean");
        if (!mat.TryGetProperty("pbrMetallicRoughness", out var pbr)) continue;
        if (pbr.TryGetProperty("baseColorFactor", out var bc) && (bc.ValueKind != JsonValueKind.Array || bc.GetArrayLength() != 4 || bc.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Number || x.GetDouble() is < 0 or > 1))) issues.Add("material baseColorFactor invalid");
        foreach (var factor in new[] { "metallicFactor", "roughnessFactor" })
            if (pbr.TryGetProperty(factor, out var fv) && (fv.ValueKind != JsonValueKind.Number || fv.GetDouble() is < 0 or > 1)) issues.Add("material " + factor + " outside [0,1]");
    }
    return issues;
}

byte[] Repack(string json, byte[] bin, byte jsonPad = 0x20)
{
    var j = Encoding.UTF8.GetBytes(json); var pad = (j.Length + 3) & ~3;
    var total = 20 + pad + (bin.Length > 0 ? 8 + bin.Length : 0);
    var o = new byte[total];
    BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(0), 0x46546C67); BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(4), 2); BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(8), (uint)total);
    BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(12), (uint)pad); BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(16), 0x4E4F534A);
    j.CopyTo(o, 20); for (var i = 20 + j.Length; i < 20 + pad; i++) o[i] = jsonPad;
    if (bin.Length > 0) { BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(20 + pad), (uint)bin.Length); BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(24 + pad), 0x004E4942); bin.CopyTo(o, 28 + pad); }
    return o;
}

JsonElement NodeNamed(JsonElement root, string name) => Arr(root, "nodes").Single(n => n.GetProperty("name").GetString() == name);
float[] Positions(JsonElement root, byte[] bin, JsonElement node)
{
    var mesh = Arr(root, "meshes")[node.GetProperty("mesh").GetInt32()];
    var acc = Arr(root, "accessors")[mesh.GetProperty("primitives")[0].GetProperty("attributes").GetProperty("POSITION").GetInt32()];
    var view = Arr(root, "bufferViews")[acc.GetProperty("bufferView").GetInt32()];
    var at = Int(view, "byteOffset", 0) + Int(acc, "byteOffset", 0);
    var stride = Int(view, "byteStride", 12);
    var o = new float[acc.GetProperty("count").GetInt32() * 3];
    for (var i = 0; i < o.Length; i++) o[i] = BinaryPrimitives.ReadSingleLittleEndian(bin.AsSpan(at + stride * (i / 3) + 4 * (i % 3)));
    return o;
}
int IndexCount(JsonElement root, JsonElement node)
{
    var mesh = Arr(root, "meshes")[node.GetProperty("mesh").GetInt32()];
    return Arr(root, "accessors")[mesh.GetProperty("primitives")[0].GetProperty("indices").GetInt32()].GetProperty("count").GetInt32();
}
(double[] Min, double[] Max) Box(float[] p)
{
    var min = new[] { double.MaxValue, double.MaxValue, double.MaxValue }; var max = new[] { double.MinValue, double.MinValue, double.MinValue };
    for (var i = 0; i < p.Length; i++) { min[i % 3] = Math.Min(min[i % 3], p[i]); max[i % 3] = Math.Max(max[i % 3], p[i]); }
    return (min, max);
}
bool BoxIs((double[] Min, double[] Max) b, double[] min, double[] max, double eps = 0) =>
    Enumerable.Range(0, 3).All(c => Math.Abs(b.Min[c] - min[c]) <= eps && Math.Abs(b.Max[c] - max[c]) <= eps);
string B((double[] Min, double[] Max) b) => "[" + string.Join(",", b.Min) + "]..[" + string.Join(",", b.Max) + "]";

try
{
    // ---------------- fixture: structure, nodes, meshes, extras ----------------
    var glb = GltfExporter.ExportGlb(fixture);
    var (issues, root, bin) = Parse(glb);
    Check(issues.Count == 0, "fixture GLB passes the glTF 2.0 structural validator", string.Join("; ", issues));
    Check(root.GetProperty("asset").GetProperty("version").GetString() == "2.0" && root.GetProperty("asset").GetProperty("generator").GetString() == GltfExporter.Generator, "asset.version 2.0 and generator");
    Check(BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8)) == glb.Length && BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12)) % 4 == 0, "GLB total length and JSON chunk 4-byte padding");
    var nodes = Arr(root, "nodes");
    Check(nodes.Length == fixture.Elements.Length + 1, "one node per element plus the project root", nodes.Length.ToString(CultureInfo.InvariantCulture));
    var rootNode = nodes[0];
    Check(rootNode.GetProperty("name").GetString() == "project " + PID.ToString("D") && Arr(rootNode, "children").Select(c => c.GetInt32()).SequenceEqual(Enumerable.Range(1, fixture.Elements.Length)),
        "root node is the project and lists element nodes in element order");
    Check(rootNode.GetProperty("extras").GetProperty("bim_project_id").GetString() == PID.ToString("D") && rootNode.GetProperty("extras").GetProperty("revision").GetInt32() == 7
        && rootNode.GetProperty("extras").GetProperty("schema").GetString() == GltfExporter.Schema && rootNode.GetProperty("extras").GetProperty("up").GetString() == "Y", "root extras carry project id, revision, schema and Y-up");
    Check(Arr(root, "scenes").Single().GetProperty("nodes").EnumerateArray().Single().GetInt32() == 0 && root.GetProperty("scene").GetInt32() == 0, "single scene rooted at the project node");
    var names = fixture.Elements.Select(e => e.Kind + " " + e.Id.ToString("D")).ToArray();
    Check(nodes.Skip(1).Select(n => n.GetProperty("name").GetString()).SequenceEqual(names), "node name = kind + stable id, in element order");
    Check(Arr(root, "meshes").Length == 4 && nodes.Skip(1).Count(n => n.TryGetProperty("mesh", out _)) == 4, "walls and the space each get one mesh; levels and plan do not");
    var levelOf = new Dictionary<Guid, Guid> { [L1] = L1, [L2] = L2, [W1] = L1, [W2] = L1, [W3] = L2, [SP] = L1, [PL] = L1 };
    foreach (var e in fixture.Elements)
    {
        var x = NodeNamed(root, e.Kind + " " + e.Id.ToString("D")).GetProperty("extras");
        Check(x.GetProperty("bim_id").GetString() == e.Id.ToString("D") && x.GetProperty("kind").GetString() == e.Kind && x.GetProperty("type").ValueKind == JsonValueKind.Null
            && x.GetProperty("level").GetString() == levelOf[e.Id].ToString("D") && x.EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "bim_id", "kind", "type", "level" }),
            "extras {bim_id, kind, type, level} for " + e.Kind + " " + e.Id.ToString("D"), x.ToString());
    }
    var w1 = NodeNamed(root, "wall " + W1.ToString("D")); var w3 = NodeNamed(root, "wall " + W3.ToString("D")); var sp = NodeNamed(root, "space " + SP.ToString("D"));
    Check(Positions(root, bin, w1).Length == 24 * 3 && IndexCount(root, w1) == 36, "wall mesh has 24 vertices (flat-shaded box) and 12 triangles");
    Check(Positions(root, bin, sp).Length == 4 * 3 && IndexCount(root, sp) == 6, "space mesh has 4 vertices and 2 triangles");
    // BIM (x, y, z) -> glTF (x, z, -y). Wall W1 axis (0,0)->(4,0), thickness 0.25, height 3 on elevation 0.
    var bw1 = Box(Positions(root, bin, w1));
    Check(BoxIs(bw1, [0, 0, -0.125], [4, 3, 0.125]), "wall W1 bounding box equals axis length x height x thickness (Y-up)", B(bw1));
    // W3 axis (0,0)->(0,3), thickness 0.5, height 2.5 on level L2 at 3 m: glTF Y 3..5.5, Z = -y in -3..0.
    var bw3 = Box(Positions(root, bin, w3));
    Check(BoxIs(bw3, [-0.25, 3, -3], [0.25, 5.5, 0]), "wall W3 stands on its level elevation; BIM +Y maps to glTF -Z", B(bw3));
    var bw2 = Box(Positions(root, bin, NodeNamed(root, "wall " + W2.ToString("D"))));
    Check(BoxIs(bw2, [3.875, 0, -3], [4.125, 3, 0]), "wall W2 vertical axis gets thickness across X", B(bw2));
    var spp = Positions(root, bin, sp);
    Check(spp.SequenceEqual(new float[] { 0.125f, 0, -0.125f, 3.875f, 0, -0.125f, 3.875f, 0, -2.875f, 0.125f, 0, -2.875f }), "space vertices are its rectangle at level elevation mapped to Y-up", string.Join(",", spp));
    var l2 = NodeNamed(root, "level " + L2.ToString("D"));
    Check(!l2.TryGetProperty("mesh", out _) && l2.GetProperty("translation").EnumerateArray().Select(v => v.GetDouble()).SequenceEqual(new double[] { 0, 3, 0 }), "level is an empty node translated to its elevation on +Y");
    var mats = Arr(root, "materials");
    Check(mats.Length == 2 && mats[0].GetProperty("name").GetString() == "bim-wall" && mats[1].GetProperty("name").GetString() == "bim-space" && mats[1].GetProperty("doubleSided").GetBoolean(), "per-kind materials: bim-wall and double-sided bim-space");
    var meshesArr = Arr(root, "meshes");
    int MaterialOf(JsonElement node) => meshesArr[node.GetProperty("mesh").GetInt32()].GetProperty("primitives")[0].GetProperty("material").GetInt32();
    Check(MaterialOf(w1) == 0 && MaterialOf(w3) == 0 && MaterialOf(sp) == 1, "walls use bim-wall, the space uses bim-space");

    // ---------------- determinism and golden bytes ----------------
    Check(GltfExporter.ExportGlb(fixture).SequenceEqual(glb), "two exports give identical bytes");
    var culture = CultureInfo.CurrentCulture;
    try
    {
        // sv-SE and fa-IR format negative numbers with a non-ASCII minus sign (U+2212 / U+200E U+2212) under ICU.
        Check(new[] { "sv-SE", "fa-IR" }.Any(c => CultureInfo.GetCultureInfo(c).NumberFormat.NegativeSign.Any(ch => ch > 0x7F)), "precondition: a test culture has a non-ASCII minus sign",
            string.Join(",", new[] { "sv-SE", "fa-IR" }.Select(c => string.Join(" ", CultureInfo.GetCultureInfo(c).NumberFormat.NegativeSign.Select(ch => ((int)ch).ToString("X4", CultureInfo.InvariantCulture))))));
        foreach (var name in new[] { "de-DE", "sv-SE", "fa-IR" })
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            Check(GltfExporter.ExportGlb(fixture).SequenceEqual(glb) && GltfExporter.ExportGltf(fixture) == GltfExporter.ExportGltf(fixture), "export bytes do not depend on the current culture (" + name + ")");
        }
    }
    finally { CultureInfo.CurrentCulture = culture; }
    Check(Hex(glb) == GoldenGlb, "fixture GLB golden SHA-256", Hex(glb) + " length " + glb.Length.ToString(CultureInfo.InvariantCulture));
    var gltf = GltfExporter.ExportGltf(fixture);
    Check(Hex(Encoding.UTF8.GetBytes(gltf)) == GoldenGltf, "fixture .gltf golden SHA-256", Hex(Encoding.UTF8.GetBytes(gltf)));
    var gltfRoot = JsonDocument.Parse(gltf).RootElement;
    var uri = gltfRoot.GetProperty("buffers")[0].GetProperty("uri").GetString() ?? "";
    const string Prefix = "data:application/octet-stream;base64,";
    var embedded = uri.StartsWith(Prefix, StringComparison.Ordinal) ? Convert.FromBase64String(uri[Prefix.Length..]) : Array.Empty<byte>();
    Check(embedded.SequenceEqual(bin), ".gltf embedded data URI decodes to the GLB BIN chunk");
    var gltfIssues = ValidateJson(gltfRoot, embedded, glbMode: false);
    Check(gltfIssues.Count == 0, ".gltf passes the structural validator", string.Join("; ", gltfIssues));
    var withoutUri = JsonNode.Parse(gltf)!.AsObject(); withoutUri["buffers"]![0]!.AsObject().Remove("uri");
    var glbJson = Encoding.UTF8.GetString(glb, 20, (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12))).TrimEnd(' ');
    Check(withoutUri.ToJsonString() == JsonNode.Parse(glbJson)!.ToJsonString(), ".gltf JSON equals the GLB JSON apart from the buffer uri");

    // ---------------- empty project ----------------
    var emptyProject = new Project(Guid.Empty, 0, []);
    var emptyGlb = GltfExporter.ExportGlb(emptyProject);
    var (emptyIssues, emptyRoot, emptyBin) = Parse(emptyGlb);
    Check(emptyIssues.Count == 0, "empty project GLB is valid", string.Join("; ", emptyIssues));
    Check(emptyGlb.Length == 20 + BinaryPrimitives.ReadUInt32LittleEndian(emptyGlb.AsSpan(12)) && emptyBin.Length == 0, "empty project GLB has no BIN chunk");
    Check(Arr(emptyRoot, "nodes").Length == 1 && !Arr(emptyRoot, "nodes")[0].TryGetProperty("children", out _) && !emptyRoot.TryGetProperty("buffers", out _) && !emptyRoot.TryGetProperty("meshes", out _) && !emptyRoot.TryGetProperty("accessors", out _),
        "empty project has only the root node and no buffers, meshes or accessors");
    Check(Hex(emptyGlb) == GoldenEmptyGlb, "empty project GLB golden SHA-256", Hex(emptyGlb));

    // ---------------- compiled room (RoomCompiler output) ----------------
    var roomId = G("5d1c6a3e-8f2b-4c7d-9e10-123456789abc");
    var room = RoomCompiler.Compile(G("7e57c0de-0000-4000-8000-0000000000aa"), 3, new RoomIntent(roomId, 4, 3, 2.75, 0.2, 1, 2, "m", prov));
    var roomGlb = GltfExporter.ExportGlb(room);
    var (roomIssues, roomRoot, roomBin) = Parse(roomGlb);
    Check(roomIssues.Count == 0, "compiled room GLB passes the structural validator", string.Join("; ", roomIssues));
    var levelId = RoomCompiler.Id(roomId, "level").ToString("D");
    Check(room.Elements.All(e => NodeNamed(roomRoot, e.Kind + " " + e.Id.ToString("D")).GetProperty("extras").GetProperty("level").GetString() == levelId), "every compiled room element (incl. space, plan, schedule, sheet) resolves to the room level");
    var roomWalls = room.Elements.Where(e => e.Kind == "wall").ToArray();
    Check(roomWalls.Length == 4 && roomWalls.All(w => Positions(roomRoot, roomBin, NodeNamed(roomRoot, "wall " + w.Id.ToString("D"))).Length == 72), "four compiled walls with 24 vertices each");
    var south = Box(Positions(roomRoot, roomBin, NodeNamed(roomRoot, "wall " + RoomCompiler.Id(roomId, "south").ToString("D"))));
    // South wall axis (1.1, 2.1)->(5.3, 2.1), thickness 0.2, height 2.75: x 1.1..5.3, y 0..2.75, z = -(2.1 +/- 0.1).
    Check(BoxIs(south, [1.1, 0, -2.2], [5.3, 2.75, -2.0], 1e-6), "compiled south wall box equals its extents", B(south));
    var roomSpace = Box(Positions(roomRoot, roomBin, NodeNamed(roomRoot, "space " + RoomCompiler.Id(roomId, "space").ToString("D"))));
    Check(BoxIs(roomSpace, [1.2, 0, -5.2], [5.2, 0, -2.2], 1e-6), "compiled space rectangle is the clear interior at elevation 0", B(roomSpace));

    // ---------------- diagonal wall ----------------
    var level = E(L1, "level", [1.5]);
    var diagonal = new Project(PID, 1, [level, E(W1, "wall", [1, 1, 4, 5, 2, 0.3], L1)]);
    var (diagIssues, diagRoot, diagBin) = Parse(GltfExporter.ExportGlb(diagonal));
    Check(diagIssues.Count == 0, "diagonal wall GLB valid (unit normals, winding)", string.Join("; ", diagIssues));
    // Axis direction (0.6, 0.8); half thickness 0.15 along the normal (-0.8, 0.6) gives plan offsets (+/-0.12, +/-0.09).
    var bd = Box(Positions(diagRoot, diagBin, NodeNamed(diagRoot, "wall " + W1.ToString("D"))));
    Check(BoxIs(bd, [0.88, 1.5, -5.09], [4.12, 3.5, -0.91], 1e-6), "diagonal wall box equals rotated extents on elevation 1.5", B(bd));

    // ---------------- validator is not vacuous ----------------
    var badLength = (byte[])glb.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(badLength.AsSpan(8), (uint)glb.Length + 4);
    Check(Parse(badLength).Issues.Any(i => i.Contains("header length", StringComparison.Ordinal)), "validator rejects a wrong GLB header length");
    var noMin = JsonNode.Parse(glbJson)!.AsObject(); noMin["accessors"]![0]!.AsObject().Remove("min");
    Check(Parse(Repack(noMin.ToJsonString(), bin)).Issues.Any(i => i.Contains("min/max missing", StringComparison.Ordinal)), "validator rejects POSITION without min");
    var wrongMax = JsonNode.Parse(glbJson)!.AsObject(); wrongMax["accessors"]![0]!["max"]![0] = 5;
    Check(Parse(Repack(wrongMax.ToJsonString(), bin)).Issues.Any(i => i.Contains("differ from data", StringComparison.Ordinal)), "validator rejects POSITION max that differs from the data");
    var overrun = JsonNode.Parse(glbJson)!.AsObject(); overrun["accessors"]![2]!["count"] = 100000;
    Check(Parse(Repack(overrun.ToJsonString(), bin)).Issues.Any(i => i.Contains("overruns bufferView", StringComparison.Ordinal)), "validator rejects an accessor past its bufferView");
    var noStride = JsonNode.Parse(glbJson)!.AsObject(); noStride["bufferViews"]![0]!.AsObject().Remove("byteStride");
    Check(Parse(Repack(noStride.ToJsonString(), bin)).Issues.Any(i => i.Contains("without byteStride", StringComparison.Ordinal)), "validator rejects a shared vertex view without byteStride");
    var badIndex = (byte[])bin.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(badIndex.AsSpan(Int(Arr(root, "bufferViews")[2], "byteOffset")), 999);
    Check(Parse(Repack(glbJson, badIndex)).Issues.Any(i => i.Contains("out of range", StringComparison.Ordinal)), "validator rejects an index past the vertex count");
    var oddView = JsonNode.Parse(glbJson)!.AsObject(); oddView["bufferViews"]![1]!["byteOffset"] = 2;
    Check(Parse(Repack(oddView.ToJsonString(), bin)).Issues.Any(i => i.Contains("not 4-aligned", StringComparison.Ordinal)), "validator rejects an unaligned bufferView");
    // Inside-out wall: flip the bottom face's two triangles and its four vertex normals together, so winding and
    // normals still agree; only the outward-facing check can catch it.
    var fixtureViews = Arr(root, "bufferViews"); var fixtureAccessors = Arr(root, "accessors");
    int indexAt = Int(fixtureViews[2], "byteOffset", 0) + Int(fixtureAccessors[2], "byteOffset", 0), normalAt = Int(fixtureViews[1], "byteOffset", 0) + Int(fixtureAccessors[1], "byteOffset", 0);
    var insideOut = (byte[])bin.Clone();
    uint[] flipped = [0, 2, 1, 0, 3, 2];
    for (var i = 0; i < flipped.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(insideOut.AsSpan(indexAt + 4 * i), flipped[i]);
    for (var i = 0; i < 12; i++) { var at = normalAt + 4 * i; BinaryPrimitives.WriteSingleLittleEndian(insideOut.AsSpan(at), -BinaryPrimitives.ReadSingleLittleEndian(insideOut.AsSpan(at))); }
    var insideOutIssues = Parse(Repack(glbJson, insideOut)).Issues;
    Check(insideOutIssues.Any(i => i.Contains("mesh 0 triangle 0 faces inward", StringComparison.Ordinal)) && !insideOutIssues.Any(i => i.Contains("winding", StringComparison.Ordinal)),
        "validator rejects an inside-out wall face whose winding and normals were flipped together", string.Join("; ", insideOutIssues));
    const string MinimalJson = "{\"asset\":{\"version\":\"2.0\"}}";
    Check(Encoding.UTF8.GetByteCount(MinimalJson) % 4 != 0 && Parse(Repack(MinimalJson, Array.Empty<byte>())).Issues.Count == 0, "minimal GLB with space padding is valid");
    Check(Parse(Repack(MinimalJson, Array.Empty<byte>(), 0x00)).Issues.Contains("JSON chunk padding is not 0x20"), "validator rejects JSON chunk padding that is not 0x20");
    const string OneBufferJson = "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"byteLength\":2}]}";
    Check(Parse(Repack(OneBufferJson, [1, 2, 0, 0])).Issues.Count == 0, "BIN chunk with zero padding is valid");
    Check(Parse(Repack(OneBufferJson, [1, 2, 0, 9])).Issues.Contains("BIN chunk padding is not zero"), "validator rejects non-zero BIN chunk padding");
    var intNormal = JsonNode.Parse(glbJson)!.AsObject(); intNormal["accessors"]![1]!["componentType"] = 5125;
    Check(Parse(Repack(intNormal.ToJsonString(), bin)).Issues.Contains("mesh 0 NORMAL not FLOAT"), "validator rejects a non-FLOAT NORMAL accessor");
    var shortTranslation = JsonNode.Parse(glbJson)!.AsObject(); shortTranslation["nodes"]![2]!["translation"] = JsonNode.Parse("[0,3]");
    Check(Parse(Repack(shortTranslation.ToJsonString(), bin)).Issues.Contains("node 2 translation must have 3 numbers"), "validator rejects a translation without 3 numbers");
    var matrixAndTrs = JsonNode.Parse(glbJson)!.AsObject(); matrixAndTrs["nodes"]![2]!["matrix"] = JsonNode.Parse("[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]");
    Check(Parse(Repack(matrixAndTrs.ToJsonString(), bin)).Issues.Contains("node 2 has matrix together with TRS"), "validator rejects matrix together with translation");
    var numberDoubleSided = JsonNode.Parse(glbJson)!.AsObject(); numberDoubleSided["materials"]![1]!["doubleSided"] = 1;
    Check(Parse(Repack(numberDoubleSided.ToJsonString(), bin)).Issues.Contains("material doubleSided is not a boolean"), "validator rejects a non-boolean doubleSided");
    var metallic = JsonNode.Parse(glbJson)!.AsObject(); metallic["materials"]![0]!["pbrMetallicRoughness"]!["metallicFactor"] = 2;
    Check(Parse(Repack(metallic.ToJsonString(), bin)).Issues.Contains("material metallicFactor outside [0,1]"), "validator rejects metallicFactor outside [0,1]");
    var rough = JsonNode.Parse(glbJson)!.AsObject(); rough["materials"]![0]!["pbrMetallicRoughness"]!["roughnessFactor"] = -0.5;
    Check(Parse(Repack(rough.ToJsonString(), bin)).Issues.Contains("material roughnessFactor outside [0,1]"), "validator rejects roughnessFactor outside [0,1]");
    // Re-layout the fixture with positions and normals interleaved (byteStride 24): still valid, same positions.
    var positionView = fixtureViews[0]; var normalView = fixtureViews[1]; var indexView = fixtureViews[2];
    int vertexBytes = Int(positionView, "byteLength"), indexBytes = Int(indexView, "byteLength");
    var interleaved = new byte[2 * vertexBytes + indexBytes];
    for (var v = 0; v < vertexBytes / 12; v++)
    {
        Array.Copy(bin, Int(positionView, "byteOffset", 0) + 12 * v, interleaved, 24 * v, 12);
        Array.Copy(bin, Int(normalView, "byteOffset", 0) + 12 * v, interleaved, 24 * v + 12, 12);
    }
    Array.Copy(bin, Int(indexView, "byteOffset", 0), interleaved, 2 * vertexBytes, indexBytes);
    var interleavedJson = JsonNode.Parse(glbJson)!.AsObject();
    var interleavedAccessors = interleavedJson["accessors"]!.AsArray();
    for (var mesh = 0; mesh < Arr(root, "meshes").Length; mesh++)
    {
        var positionOffset = interleavedAccessors[3 * mesh]!["byteOffset"]!.GetValue<int>();
        interleavedAccessors[3 * mesh]!["byteOffset"] = 2 * positionOffset;
        interleavedAccessors[3 * mesh + 1]!["bufferView"] = 0; interleavedAccessors[3 * mesh + 1]!["byteOffset"] = 2 * positionOffset + 12;
        interleavedAccessors[3 * mesh + 2]!["bufferView"] = 1;
    }
    interleavedJson["bufferViews"] = JsonNode.Parse("[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":" + (2 * vertexBytes).ToString(CultureInfo.InvariantCulture) + ",\"byteStride\":24,\"target\":34962},"
        + "{\"buffer\":0,\"byteOffset\":" + (2 * vertexBytes).ToString(CultureInfo.InvariantCulture) + ",\"byteLength\":" + indexBytes.ToString(CultureInfo.InvariantCulture) + ",\"target\":34963}]");
    interleavedJson["buffers"]![0]!["byteLength"] = interleaved.Length;
    var (interleavedIssues, interleavedRoot, interleavedBin) = Parse(Repack(interleavedJson.ToJsonString(), interleaved));
    Check(interleavedIssues.Count == 0, "interleaved byteStride 24 layout passes the validator (reads honour byteStride)", string.Join("; ", interleavedIssues));
    Check(new[] { w1, w3, sp }.All(n => Positions(interleavedRoot, interleavedBin, NodeNamed(interleavedRoot, n.GetProperty("name").GetString()!)).SequenceEqual(Positions(root, bin, n))),
        "Positions honours byteStride: interleaved layout reads the same vertices");

    // ---------------- P8-07 wall openings (door/window [ax,ay,bx,by,sill,height], dependencies [level, host wall]) ----------------
    var WO = G("0b000000-0000-4000-8000-0000000000a1"); var D1 = G("0e000000-0000-4000-8000-000000000001"); var N1 = G("0e000000-0000-4000-8000-000000000002");
    // BIM box x0..x1, y0..y1, z0..z1 -> glTF (x, z, -y) min/max.
    (double[] Min, double[] Max) Gl(double x0, double x1, double y0, double y1, double z0, double z1) => (new[] { x0, z0, -y1 }, new[] { x1, z1, -y0 });
    // The wall mesh is a sequence of 24-vertex boxes; each group's bounding box, in emission order.
    (double[] Min, double[] Max)[] Boxes(float[] p) => Enumerable.Range(0, p.Length / 72).Select(k => Box(p.Skip(72 * k).Take(72).ToArray())).ToArray();
    bool Overlaps((double[] Min, double[] Max) a, (double[] Min, double[] Max) b) => Enumerable.Range(0, 3).All(c => a.Min[c] < b.Max[c] - 1e-6 && b.Min[c] < a.Max[c] - 1e-6);
    double Volume((double[] Min, double[] Max) b) => (b.Max[0] - b.Min[0]) * (b.Max[1] - b.Min[1]) * (b.Max[2] - b.Min[2]);
    string Bs((double[] Min, double[] Max)[] boxes) => string.Join(" ", boxes.Select(b => B(b)));
    var openingFixture = new Project(PID, 2, [
        E(L1, "level", [0]),
        E(WO, "wall", [0, 0, 6, 0, 3, 0.2], L1),
        E(D1, "door", [1, 0, 2, 0, 0, 2.1], L1, WO),
        E(N1, "window", [3.5, 0, 5, 0, 0.9, 1.2], L1, WO)]);
    var openingGlb = GltfExporter.ExportGlb(openingFixture);
    var (openingIssues, openingRoot, openingBin) = Parse(openingGlb);
    Check(openingIssues.Count == 0, "wall with a door and a window passes the glTF 2.0 structural validator", string.Join("; ", openingIssues));
    var hostNode = NodeNamed(openingRoot, "wall " + WO.ToString("D"));
    var hostPositions = Positions(openingRoot, openingBin, hostNode);
    // Pier, door lintel, pier, window sill wall, window lintel, pier: 6 boxes of 24 vertices and 12 triangles.
    Check(hostPositions.Length == 6 * 24 * 3 && IndexCount(openingRoot, hostNode) == 6 * 36, "host wall is split into 6 solid boxes (144 vertices, 72 triangles)", (hostPositions.Length / 3).ToString(CultureInfo.InvariantCulture) + " vertices");
    var expectedBoxes = new[] {
        Gl(0, 1, -0.1, 0.1, 0, 3), Gl(1, 2, -0.1, 0.1, 2.1, 3), Gl(2, 3.5, -0.1, 0.1, 0, 3),
        Gl(3.5, 5, -0.1, 0.1, 0, 0.9), Gl(3.5, 5, -0.1, 0.1, 2.1, 3), Gl(5, 6, -0.1, 0.1, 0, 3) };
    var hostBoxes = Boxes(hostPositions);
    Check(hostBoxes.Length == expectedBoxes.Length && hostBoxes.Zip(expectedBoxes).All(p => BoxIs(p.First, p.Second.Min, p.Second.Max, 1e-6)),
        "host wall boxes are the piers and the parts below/above each opening, in axis order", Bs(hostBoxes));
    var doorHole = Gl(1, 2, -0.1, 0.1, 0, 2.1); var windowHole = Gl(3.5, 5, -0.1, 0.1, 0.9, 2.1);
    Check(hostBoxes.All(b => !Overlaps(b, doorHole) && !Overlaps(b, windowHole)), "no solid box enters the door or window opening volume");
    var solid = hostBoxes.Sum(b => Volume(b));
    Check(Math.Abs(solid - (6 * 3 - 1 * 2.1 - 1.5 * 1.2) * 0.2) < 1e-5, "solid boxes fill the wall volume minus the openings exactly", solid.ToString(CultureInfo.InvariantCulture));
    Check(BoxIs(Box(hostPositions), [0, 0, -0.1], [6, 3, 0.1], 1e-6), "host wall overall bounds are unchanged by its openings", B(Box(hostPositions)));
    // Every box faces outward on its own: each face's normal points away from its box centroid.
    var outward = true;
    for (var k = 0; k < hostPositions.Length / 72 && outward; k++)
    {
        var group = hostPositions.Skip(72 * k).Take(72).ToArray();
        double cx = 0, cy = 0, cz = 0;
        for (var v = 0; v < 24; v++) { cx += group[3 * v] / 24.0; cy += group[3 * v + 1] / 24.0; cz += group[3 * v + 2] / 24.0; }
        for (var q = 0; q < 6 && outward; q++)
        {
            // Each face is 4 consecutive vertices; triangles (0,1,2) (0,2,3).
            double[] V(int i) => [group[3 * (4 * q + i)], group[3 * (4 * q + i) + 1], group[3 * (4 * q + i) + 2]];
            var fa = V(0); var fb = V(1); var fc = V(2);
            double ux = fb[0] - fa[0], uy = fb[1] - fa[1], uz = fb[2] - fa[2], vx = fc[0] - fa[0], vy = fc[1] - fa[1], vz = fc[2] - fa[2];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double tx = (fa[0] + fb[0] + fc[0]) / 3 - cx, ty = (fa[1] + fb[1] + fc[1]) / 3 - cy, tz = (fa[2] + fb[2] + fc[2]) / 3 - cz;
            if (tx * nx + ty * ny + tz * nz <= 0) outward = false;
        }
    }
    Check(outward, "every box of the split wall faces outward");
    Check(Arr(openingRoot, "meshes").Length == 1, "doors and windows get no mesh of their own (only the cut in the host)");
    foreach (var (openingId, openingKind) in new[] { (D1, "door"), (N1, "window") })
    {
        var openingNode = NodeNamed(openingRoot, openingKind + " " + openingId.ToString("D"));
        var ox = openingNode.GetProperty("extras");
        Check(!openingNode.TryGetProperty("mesh", out _) && ox.EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "bim_id", "kind", "type", "level", "host" })
            && ox.GetProperty("kind").GetString() == openingKind && ox.GetProperty("level").GetString() == L1.ToString("D") && ox.GetProperty("host").GetString() == WO.ToString("D"),
            openingKind + " node is empty and carries extras {bim_id, kind, type, level, host}", ox.ToString());
    }
    Check(GltfExporter.ExportGlb(openingFixture).SequenceEqual(openingGlb) && GltfExporter.ExportGltf(openingFixture) == GltfExporter.ExportGltf(openingFixture), "openings export is byte-identical on rerun");
    var reversedOpening = openingFixture with { Elements = [openingFixture.Elements[0], openingFixture.Elements[1], E(D1, "door", [2, 0, 1, 0, 0, 2.1], L1, WO), openingFixture.Elements[3]] };
    Check(GltfExporter.ExportGlb(reversedOpening).SequenceEqual(openingGlb), "opening end points given against the host direction give the same bytes");
    var shuffledOpenings = openingFixture with { Elements = [openingFixture.Elements[0], openingFixture.Elements[1], openingFixture.Elements[3], openingFixture.Elements[2]] };
    var shuffledParse = Parse(GltfExporter.ExportGlb(shuffledOpenings));
    Check(Positions(shuffledParse.Root, shuffledParse.Bin, NodeNamed(shuffledParse.Root, "wall " + WO.ToString("D"))).SequenceEqual(hostPositions), "host mesh does not depend on opening element order");
    var gltfOpenings = ValidateJson(JsonDocument.Parse(GltfExporter.ExportGltf(openingFixture)).RootElement, openingBin, glbMode: false);
    Check(gltfOpenings.Count == 0, "openings .gltf passes the structural validator", string.Join("; ", gltfOpenings));
    Check(GltfExporter.ExportGlb(fixture).SequenceEqual(glb) && Hex(glb) == GoldenGlb, "a project without openings keeps its golden bytes");

    // A full-height door at the wall start leaves only the remaining pier.
    var atStart = Parse(GltfExporter.ExportGlb(new Project(PID, 0, [E(L1, "level", [0]), E(WO, "wall", [0, 0, 4, 0, 3, 0.2], L1), E(D1, "door", [0, 0, 1, 0, 0, 3], L1, WO)])));
    var atStartBoxes = Boxes(Positions(atStart.Root, atStart.Bin, NodeNamed(atStart.Root, "wall " + WO.ToString("D"))));
    Check(atStart.Issues.Count == 0 && atStartBoxes.Length == 1 && BoxIs(atStartBoxes[0], [1, 0, -0.1], [4, 3, 0.1], 1e-6), "full-height opening at the wall start leaves one pier (no zero-length box)", Bs(atStartBoxes));
    // Touching openings share a station: pier, door lintel, window sill wall, window lintel, pier.
    var touching = Parse(GltfExporter.ExportGlb(new Project(PID, 0, [E(L1, "level", [0]), E(WO, "wall", [0, 0, 4, 0, 3, 0.2], L1),
        E(D1, "door", [1, 0, 2, 0, 0, 2.1], L1, WO), E(N1, "window", [2, 0, 3, 0, 1, 1], L1, WO)])));
    var touchingBoxes = Boxes(Positions(touching.Root, touching.Bin, NodeNamed(touching.Root, "wall " + WO.ToString("D"))));
    Check(touching.Issues.Count == 0 && touchingBoxes.Length == 5 && BoxIs(touchingBoxes[2], [2, 0, -0.1], [3, 1, 0.1], 1e-6) && BoxIs(touchingBoxes[3], [2, 2, -0.1], [3, 3, 0.1], 1e-6),
        "touching openings give 5 boxes and no pier between them", Bs(touchingBoxes));
    // Diagonal host on an elevated level: axis (1,1)->(4,5), direction (0.6,0.8); opening s = 1..2 m, sill 0.5, height 1.
    var diagonalOpening = Parse(GltfExporter.ExportGlb(new Project(PID, 0, [E(L1, "level", [1.5]), E(WO, "wall", [1, 1, 4, 5, 2, 0.3], L1),
        E(N1, "window", [1.6, 1.8, 2.2, 2.6, 0.5, 1], L1, WO)])));
    var diagonalBoxes = Boxes(Positions(diagonalOpening.Root, diagonalOpening.Bin, NodeNamed(diagonalOpening.Root, "wall " + WO.ToString("D"))));
    Check(diagonalOpening.Issues.Count == 0 && diagonalBoxes.Length == 4, "diagonal host with a window gives 4 valid boxes", string.Join("; ", diagonalOpening.Issues) + " " + Bs(diagonalBoxes));
    Check(diagonalBoxes.Length == 4 && Math.Abs(diagonalBoxes[1].Min[1] - 1.5) < 1e-6 && Math.Abs(diagonalBoxes[1].Max[1] - 2.0) < 1e-6 && Math.Abs(diagonalBoxes[2].Min[1] - 3.0) < 1e-6 && Math.Abs(diagonalBoxes[2].Max[1] - 3.5) < 1e-6,
        "diagonal window sill wall spans level..sill and its lintel sill+height..top", Bs(diagonalBoxes));

    // Compiled layout intent (IntentCompiler door/window elements) exports with its openings cut.
    var intentPid = G("7e57c0de-0000-4000-8000-0000000000b7");
    var layout = new LayoutIntent(LayoutIntent.CurrentVersion, prov, [new IntentLevel("l0", "Ground", 0)], [new IntentWallType("w", "Wall", 0.2, 3)],
        [new IntentRoom("r", "Room", "l0", "w", 0, 0, 6, 4)],
        [new IntentOpening("d", IntentOpeningKind.Door, new IntentRoomSide("r", IntentSide.South), 1, 1, 2.1, 0),
         new IntentOpening("n", IntentOpeningKind.Window, new IntentRoomSide("r", IntentSide.North), 2, 1.5, 1.2, 0.9)]);
    var compiled = IntentCompiler.Compile(intentPid, 1, layout);
    var compiledParse = Parse(GltfExporter.ExportGlb(compiled));
    Check(compiledParse.Issues.Count == 0, "compiled layout intent with a door and a window passes the structural validator", string.Join("; ", compiledParse.Issues));
    (double[] Min, double[] Max)[] CompiledWall(IntentSide side) => Boxes(Positions(compiledParse.Root, compiledParse.Bin, NodeNamed(compiledParse.Root, "wall " + IntentCompiler.Id(intentPid, IntentCompiler.WallKey("r", side)).ToString("D"))));
    var compiledSouth = CompiledWall(IntentSide.South); var compiledNorth = CompiledWall(IntentSide.North); var compiledEast = CompiledWall(IntentSide.East);
    Check(compiledSouth.Length == 3 && compiledNorth.Length == 4 && compiledEast.Length == 1, "compiled door host has 3 boxes, window host 4, an unopened wall 1",
        compiledSouth.Length.ToString(CultureInfo.InvariantCulture) + "/" + compiledNorth.Length.ToString(CultureInfo.InvariantCulture) + "/" + compiledEast.Length.ToString(CultureInfo.InvariantCulture));
    var compiledDoor = compiled.Elements.Single(e => e.Kind == "door").GeometryMetres;
    var compiledDoorHole = Gl(Math.Min(compiledDoor[0], compiledDoor[2]), Math.Max(compiledDoor[0], compiledDoor[2]), -0.1, 0.1, 0, 2.1);
    Check(compiledSouth.All(b => !Overlaps(b, compiledDoorHole)), "compiled door opening is empty in the south wall mesh", Bs(compiledSouth));

    // Opening refusals.
    Project Host(params Element[] openings) => new(PID, 0, [E(L1, "level", [0]), E(WO, "wall", [0, 0, 4, 0, 3, 0.2], L1), E(SP, "space", [0, 0, 1, 1], L1), .. openings]);
    Check(Code(() => GltfExporter.ExportGlb(Host(E(D1, "door", [1, 0, 2, 0, 0, 2], L1, WO), E(N1, "window", [1.5, 0, 3, 0, 1, 1], L1, WO)))) == "gltf_opening_overlap", "refuses overlapping openings on one host");
    Check(Code(() => GltfExporter.ExportGlb(Host(E(D1, "door", [3.5, 0, 4.5, 0, 0, 2], L1, WO)))) == "gltf_opening_outside_wall", "refuses an opening past the host axis end");
    Check(Code(() => GltfExporter.ExportGlb(Host(E(D1, "door", [1, 0.5, 2, 0.5, 0, 2], L1, WO)))) == "gltf_opening_outside_wall", "refuses an opening off the host axis");
    Check(Code(() => GltfExporter.ExportGlb(Host(E(D1, "door", [1, 0, 2, 0, 0, 3.5], L1, WO)))) == "gltf_opening_outside_wall", "refuses an opening taller than its host");
    Check(Code(() => GltfExporter.ExportGlb(Host(E(N1, "window", [1, 0, 2, 0, -0.1, 1], L1, WO)))) == "gltf_opening_outside_wall", "refuses a negative sill");
    Check(Code(() => GltfExporter.ExportGlb(Host(E(D1, "door", [1, 0, 2, 0, 0, 2], L1)))) == "gltf_opening_host", "refuses an opening without a host wall dependency");
    Check(Code(() => GltfExporter.ExportGlb(Host(E(D1, "door", [1, 0, 2, 0, 0, 2], L1, SP)))) == "gltf_opening_host", "refuses an opening hosted by a non-wall");
    Check(Code(() => GltfExporter.ExportGlb(new Project(PID, 0, [E(L1, "level", [0]), E(WO, "wall", [0, 0, 4, 0, 3, 0.2], L1), E(W2, "wall", [0, 1, 4, 1, 3, 0.2], L1), E(D1, "door", [1, 0, 2, 0, 0, 2], L1, WO, W2)]))) == "gltf_opening_host", "refuses an opening with two host walls");
    Check(Code(() => GltfExporter.ExportGlb(Host(E(D1, "door", [1, 1], L1, WO)))) == "gltf_opening_geometry", "refuses a hosted [offset, width] opening (no sill or height in BIM-IR)");
    Check(Code(() => GltfExporter.ExportGlb(Host(E(D1, "door", [1, 0, 1, 0, 0, 2], L1, WO)))) == "gltf_opening_geometry", "refuses a zero-width opening");
    Check(Code(() => GltfExporter.ExportGlb(Host(E(D1, "door", [1, 0, 2, 0, 0, 0], L1, WO)))) == "gltf_opening_geometry", "refuses a zero-height opening");
    Check(Code(() => GltfExporter.ExportGlb(Host(E(D1, "door", [1, 0, 2, 0, 0, 2], L1, WO), E(N1, "window", [2.0005, 0, 3, 0, 1, 1], L1, WO)))) == "gltf_precision", "refuses a sub-millimetre pier between openings");
    Check(Code(() => GltfExporter.ExportGlb(Host(E(N1, "window", [1, 0, 2, 0, 0.0004, 1], L1, WO)))) == "gltf_precision", "refuses a sub-millimetre sill wall");
    Check(Code(() => GltfExporter.ExportGlb(Host(E(D1, "opening", [1, 0, 2, 0, 0, 2], L1, WO)))) == "geometry_kind_unsupported", "kind opening stays unsupported");

    // ---------------- P8-07 review: goldens, wall-plane overlap, host direction, snap band, plan corners ----------------
    Check(Hex(openingGlb) == GoldenOpeningsGlb, "opening fixture GLB golden SHA-256", Hex(openingGlb) + " length " + openingGlb.Length.ToString(CultureInfo.InvariantCulture));
    var openingGltfBytes = Encoding.UTF8.GetBytes(GltfExporter.ExportGltf(openingFixture));
    Check(Hex(openingGltfBytes) == GoldenOpeningsGltf, "opening fixture .gltf golden SHA-256", Hex(openingGltfBytes) + " length " + openingGltfBytes.Length.ToString(CultureInfo.InvariantCulture));
    (double[] Min, double[] Max)[] WallBoxes(params Element[] openings)
    {
        var parsed = Parse(GltfExporter.ExportGlb(Host(openings)));
        Check(parsed.Issues.Count == 0, "split wall passes the structural validator", string.Join("; ", parsed.Issues));
        return Boxes(Positions(parsed.Root, parsed.Bin, NodeNamed(parsed.Root, "wall " + WO.ToString("D"))));
    }
    bool BoxesAre((double[] Min, double[] Max)[] actual, params (double[] Min, double[] Max)[] expected) =>
        actual.Length == expected.Length && actual.Zip(expected).All(p => BoxIs(p.First, p.Second.Min, p.Second.Max, 1e-6));
    var N2 = G("0e000000-0000-4000-8000-000000000003");
    // A transom window above a door (same stretch, disjoint heights) is accepted: pier, door-transom mullion, top, pier.
    var transom = WallBoxes(E(D1, "door", [1, 0, 2, 0, 0, 2.1], L1, WO), E(N1, "window", [1, 0, 2, 0, 2.3, 0.5], L1, WO));
    Check(BoxesAre(transom, Gl(0, 1, -0.1, 0.1, 0, 3), Gl(1, 2, -0.1, 0.1, 2.1, 2.3), Gl(1, 2, -0.1, 0.1, 2.8, 3), Gl(2, 4, -0.1, 0.1, 0, 3)),
        "transom window above a door gives 4 boxes with the 2.1..2.3 m band kept", Bs(transom));
    var stacked = WallBoxes(E(N1, "window", [1, 0, 2, 0, 0.5, 0.5], L1, WO), E(N2, "window", [1, 0, 2, 0, 1.5, 0.5], L1, WO));
    Check(BoxesAre(stacked, Gl(0, 1, -0.1, 0.1, 0, 3), Gl(1, 2, -0.1, 0.1, 0, 0.5), Gl(1, 2, -0.1, 0.1, 1, 1.5), Gl(1, 2, -0.1, 0.1, 2, 3), Gl(2, 4, -0.1, 0.1, 0, 3)),
        "stacked windows give 5 boxes, bottom up within their stretch", Bs(stacked));
    var staggered = WallBoxes(E(D1, "door", [1, 0, 2, 0, 0, 2.1], L1, WO), E(N1, "window", [1.5, 0, 3, 0, 2.3, 0.5], L1, WO));
    Check(staggered.Length == 7 && Math.Abs(staggered.Sum(b => Volume(b)) - (4 * 3 - 1 * 2.1 - 1.5 * 0.5) * 0.2) < 1e-5
        && staggered.All(b => !Overlaps(b, Gl(1, 2, -0.1, 0.1, 0, 2.1)) && !Overlaps(b, Gl(1.5, 3, -0.1, 0.1, 2.3, 2.8))),
        "partly overlapping stretches with disjoint heights split into 7 boxes that leave both openings empty", Bs(staggered));
    Check(Code(() => GltfExporter.ExportGlb(Host(E(D1, "door", [1, 0, 2, 0, 0, 2.1], L1, WO), E(N1, "window", [1, 0, 2, 0, 2.0, 0.5], L1, WO)))) == "gltf_opening_overlap", "refuses a window overlapping a door head by 0.1 m");
    Check(Code(() => GltfExporter.ExportGlb(Host(E(N1, "window", [1, 0, 2, 0, 0.5, 1], L1, WO), E(N2, "window", [1.5, 0, 2.5, 0, 1, 1], L1, WO)))) == "gltf_opening_overlap", "refuses windows overlapping along and across");
    Check(Code(() => GltfExporter.ExportGlb(Host(E(D1, "door", [1, 0, 2, 0, 0, 2.1], L1, WO), E(N1, "window", [1, 0, 2, 0, 2.1, 0.5], L1, WO)))) is null, "a window sitting exactly on a door head is accepted (touching)");
    // Reversed host: axis (6,0)->(0,0); a door at x 1..2 sits at t 4..5 from the host start.
    var reversedHost = Parse(GltfExporter.ExportGlb(new Project(PID, 0, [E(L1, "level", [0]), E(WO, "wall", [6, 0, 0, 0, 3, 0.2], L1), E(D1, "door", [1, 0, 2, 0, 0, 2.1], L1, WO)])));
    var reversedBoxes = Boxes(Positions(reversedHost.Root, reversedHost.Bin, NodeNamed(reversedHost.Root, "wall " + WO.ToString("D"))));
    Check(reversedHost.Issues.Count == 0 && BoxesAre(reversedBoxes, Gl(2, 6, -0.1, 0.1, 0, 3), Gl(1, 2, -0.1, 0.1, 2.1, 3), Gl(0, 1, -0.1, 0.1, 0, 3)),
        "reversed host wall: piers x 2..6 then 0..1 around the door lintel, in host axis order", Bs(reversedBoxes));
    var flush = WallBoxes(E(D1, "door", [3, 0, 4, 0, 0, 2.1], L1, WO));
    Check(BoxesAre(flush, Gl(0, 3, -0.1, 0.1, 0, 3), Gl(3, 4, -0.1, 0.1, 2.1, 3)) && flush[1].Max[0] == 4, "opening flush with the wall end leaves no end pier; the lintel ends exactly at B", Bs(flush));
    // Snap band: within OpeningTolerance (1e-6 m) snaps; 2e-6 m is refused.
    var gapIn = WallBoxes(E(D1, "door", [1, 0, 2, 0, 0, 2.1], L1, WO), E(N1, "window", [2.0000005, 0, 3, 0, 1, 1], L1, WO));
    Check(gapIn.Length == 5 && gapIn[2].Min[0] == 2 && gapIn[1].Max[0] == 2, "a 5e-7 m gap between openings snaps closed (no pier)", Bs(gapIn));
    Check(Code(() => GltfExporter.ExportGlb(Host(E(D1, "door", [1, 0, 2, 0, 0, 2.1], L1, WO), E(N1, "window", [2.000002, 0, 3, 0, 1, 1], L1, WO)))) == "gltf_precision", "a 2e-6 m gap between openings is a sub-millimetre pier");
    var offIn = WallBoxes(E(D1, "door", [1, 5e-7, 2, 5e-7, 0, 2.1], L1, WO));
    Check(offIn.Length == 3 && offIn.All(b => b.Min[2] == (double)(-0.1f) && b.Max[2] == (double)0.1f), "an end 5e-7 m off the axis is accepted and every face stays exactly thickness/2 from the axis", Bs(offIn));
    Check(Code(() => GltfExporter.ExportGlb(Host(E(D1, "door", [1, 2e-6, 2, 2e-6, 0, 2.1], L1, WO)))) == "gltf_opening_outside_wall", "an end 2e-6 m off the axis is refused");
    var sillIn = WallBoxes(E(N1, "window", [1, 0, 2, 0, 5e-7, 1], L1, WO));
    Check(BoxesAre(sillIn, Gl(0, 1, -0.1, 0.1, 0, 3), Gl(1, 2, -0.1, 0.1, 1.0000005, 3), Gl(2, 4, -0.1, 0.1, 0, 3)), "a 5e-7 m sill snaps to the base (no sill wall)", Bs(sillIn));
    Check(Code(() => GltfExporter.ExportGlb(Host(E(N1, "window", [1, 0, 2, 0, 2e-6, 1], L1, WO)))) == "gltf_precision", "a 2e-6 m sill is a sub-millimetre sill wall");
    var headIn = WallBoxes(E(D1, "door", [1, 0, 2, 0, 0, 3 - 5e-7], L1, WO));
    Check(BoxesAre(headIn, Gl(0, 1, -0.1, 0.1, 0, 3), Gl(2, 4, -0.1, 0.1, 0, 3)), "a head 5e-7 m below the top snaps to it (no lintel)", Bs(headIn));
    Check(Code(() => GltfExporter.ExportGlb(Host(E(D1, "door", [1, 0, 2, 0, 0, 3 - 2e-6], L1, WO)))) == "gltf_precision", "a head 2e-6 m below the top is a sub-millimetre lintel");
    // Diagonal host plan corners: the first pier spans t 0..1 on axis (1,1)+(0.6,0.8)t with half thickness 0.15 along
    // (-0.8,0.6): corners (0.88,1.09) (1.12,0.91) at A and (1.48,1.89) (1.72,1.71) at t = 1, mapped to glTF (x, -y).
    var diagonalPier = Positions(diagonalOpening.Root, diagonalOpening.Bin, NodeNamed(diagonalOpening.Root, "wall " + WO.ToString("D"))).Take(72).ToArray();
    double[][] corners = [[0.88, 1.09], [1.12, 0.91], [1.48, 1.89], [1.72, 1.71]];
    var pierPlan = Enumerable.Range(0, 24).Select(v => (X: (double)diagonalPier[3 * v], Y: -(double)diagonalPier[3 * v + 2])).ToArray();
    Check(pierPlan.All(p => corners.Any(c => Math.Abs(p.X - c[0]) < 1e-6 && Math.Abs(p.Y - c[1]) < 1e-6)) && corners.All(c => pierPlan.Any(p => Math.Abs(p.X - c[0]) < 1e-6 && Math.Abs(p.Y - c[1]) < 1e-6)),
        "diagonal host pier plan corners are A and A + t u offset by thickness/2 along the normal", string.Join(" ", pierPlan.Distinct().Select(p => p.X.ToString(CultureInfo.InvariantCulture) + "," + p.Y.ToString(CultureInfo.InvariantCulture))));

    // ---------------- bounded input and refusals ----------------
    Project Levels(int n) => new(PID, 0, Enumerable.Range(0, n).Select(i => E(new Guid(i + 1, 0, 0, new byte[8]), "level", [i * 0.5])).ToArray());
    Check(Code(() => GltfExporter.ExportGlb(Levels(GltfExporter.MaxElements + 1))) == "gltf_too_many_elements", "refuses more than MaxElements elements");
    Check(Code(() => GltfExporter.ExportGlb(Levels(GltfExporter.MaxElements))) is null, "accepts exactly MaxElements elements");
    Check(Code(() => GltfExporter.ExportGlb(null!)) == "bim_ir_invalid", "refuses a null project");
    Check(Code(() => GltfExporter.ExportGlb(new Project(PID, 0, null!))) == "bim_ir_invalid", "refuses null Elements");
    Check(Code(() => GltfExporter.ExportGlb(new Project(PID, 0, [E(L1, "level", [0]), E(W1, "wall", [0, 0, 4, 0, 3, 0], L1)]))) == "wall_geometry", "refuses a zero-thickness wall");
    Check(Code(() => GltfExporter.ExportGlb(new Project(PID, 0, [E(W1, "wall", [0, 0, 0, 0, 3, 0.2])]))) == "wall_geometry", "refuses a zero-length wall axis");
    Check(Code(() => GltfExporter.ExportGlb(new Project(PID, 0, [E(W1, "beam", [0, 0, 4, 0, 3, 0.2])]))) == "geometry_kind_unsupported", "refuses a kind the geometry codec cannot decode");
    Check(Code(() => GltfExporter.ExportGlb(new Project(PID, 0, [E(L1, "level", [0, 1])]))) == "level_geometry", "refuses a level with the wrong arity");
    Check(Code(() => GltfExporter.ExportGlb(new Project(PID, 0, [E(W1, "wall", [0, 0, 4, 0, 3, 0.2], L2)]))) == "missing_dependency", "refuses a dangling dependency");
    Check(Code(() => GltfExporter.ExportGlb(new Project(PID, 0, [E(W1, "wall", [0, 0, 4, 0, 3, 0.2], W2), E(W2, "wall", [0, 0, 4, 0, 3, 0.2], W1)]))) == "dependency_cycle", "refuses a dependency cycle");
    Check(Code(() => GltfExporter.ExportGlb(new Project(PID, 0, [E(L1, "level", [0]), E(L1, "level", [1])]))) == "duplicate_id", "refuses duplicate ids");
    Check(Code(() => GltfExporter.ExportGlb(new Project(PID, 0, [E(L1, "level", [double.NaN])]))) == "units", "refuses non-finite geometry");
    Check(Code(() => GltfExporter.ExportGlb(new Project(PID, 0, [E(W1, "wall", [0, 0, 2e4, 0, 3, 0.2])]))) == "gltf_precision", "refuses coordinates beyond float32 millimetre precision");
    Check(Code(() => GltfExporter.ExportGlb(new Project(PID, 0, [E(W1, "wall", [0, 0, 4, 0, 3, 1e-4])]))) == "gltf_precision", "refuses a sub-millimetre thickness");
    // Axis endpoints at y = 1e4 are inside the bound, but the faces at y = 1e4 +/- 0.5 are not.
    Check(Code(() => GltfExporter.ExportGlb(new Project(PID, 0, [E(W1, "wall", [0, 1e4, 5, 1e4, 3, 1])]))) == "gltf_precision", "refuses a wall whose offset face corners pass the coordinate bound");
    Check(Code(() => GltfExporter.ExportGlb(new Project(PID, 0, [E(W1, "wall", [1e4, 0, 1e4, 5, 3, 0.2])]))) == "gltf_precision", "refuses a wall whose thickness pushes x past the bound");
    Check(Code(() => GltfExporter.ExportGlb(new Project(PID, 0, [E(W1, "wall", [0, 9999, 5, 9999, 3, 1])]))) is null, "accepts a wall whose face corners stay inside the bound");
    Check(Code(() => GltfExporter.ExportGlb(new Project(PID, 0, [E(L1, "level", [9e3]), E(W1, "wall", [0, 0, 4, 0, 2e3, 0.2], L1)]))) == "gltf_precision", "refuses a wall top beyond the coordinate bound");
    Check(Code(() => GltfExporter.ExportGlb(new Project(PID, 0, [E(SP, "space", [0, 0, -2, 3])]))) == "gltf_space_geometry", "refuses a space with negative width");

    // ---------------- file output ----------------
    var dir = Path.Combine(Path.GetTempPath(), "bim-gltf-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var path = Path.Combine(dir, "model.glb");
        GltfExporter.WriteGlb(fixture, path);
        Check(File.ReadAllBytes(path).SequenceEqual(glb), "WriteGlb writes the exported bytes");
        Check(Code(() => GltfExporter.WriteGlb(fixture, path)) == "output_exists" && File.ReadAllBytes(path).SequenceEqual(glb), "WriteGlb never replaces an existing file");
        var existingDirectory = Path.Combine(dir, "existing-directory.glb");
        Directory.CreateDirectory(existingDirectory);
        Check(Code(() => GltfExporter.WriteGlb(fixture, existingDirectory)) == "output_exists" && Directory.Exists(existingDirectory), "WriteGlb on an existing directory is output_exists (IOException on Unix, UnauthorizedAccessException on Windows)",
            Code(() => GltfExporter.WriteGlb(fixture, existingDirectory)) ?? "no refusal");
        var failingPath = Path.Combine(dir, "failing.glb");
        var failure = Code(() => GltfExporter.WriteGlb(fixture, failingPath, stream => new FailingStream(stream, 100)));
        Check(failure is not null && failure.StartsWith("unexpected IOException", StringComparison.Ordinal) && failure.Contains("injected write failure", StringComparison.Ordinal), "a write failure propagates the original IOException", failure ?? "no failure");
        Check(!File.Exists(failingPath), "a write failure leaves no truncated file behind");
        GltfExporter.WriteGlb(fixture, failingPath);
        Check(File.ReadAllBytes(failingPath).SequenceEqual(glb), "the path is writable again after a failed write");
    }
    finally { Directory.Delete(dir, true); }
}
catch (Exception error)
{
    Check(false, "unexpected exception", error.GetType().Name + ": " + error.Message + " " + error.StackTrace);
}

Console.WriteLine($"gltf: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

// Writes the first `allowed` bytes to the real file, flushes them, then fails: a truncated output on disk.
sealed class FailingStream(Stream inner, int allowed) : Stream
{
    int written;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count)
    {
        var take = Math.Min(count, Math.Max(0, allowed - written));
        inner.Write(buffer, offset, take);
        written += take;
        if (take < count) { inner.Flush(); throw new IOException("injected write failure"); }
    }
}
