using Bim.Cad;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
// P6-01 DXF reader oracle: the checked-in hand-written ASCII fixtures (simple-plan, walls, blocks), provenance of every
// modelled entity type (source file, handle, layer, type, line), $INSUNITS units, LWPOLYLINE bulge arcs, INSERT
// transforms (rotation, scale, mirror, non-uniform, nesting, layer 0 inheritance, cycles), OCS extrusion, wall-pair
// detection with confidence and source handles, LF/CRLF/BOM/culture determinism, binary and malformed refusals and a
// bounded mutation fuzz. Unit-tested evidence only.
int passed = 0, failed = 0;

void Check(bool condition, string name, string detail = "")
{
    if (condition) { Console.WriteLine("PASS " + name); passed++; return; }
    Console.WriteLine("::error::cad FAIL " + name + (detail.Length > 0 ? " | " + detail : ""));
    failed++;
}

string RepoRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
    }
    throw new DirectoryNotFoundException("repository root with AGENTS.md not found above " + AppContext.BaseDirectory);
}

const double Eps = 1e-9;
bool Near(double a, double b, double eps = Eps) => Math.Abs(a - b) <= eps;
bool NearP(Point2 a, double x, double y, double eps = Eps) => Near(a.X, x, eps) && Near(a.Y, y, eps);
string P(Point2 p) => "(" + p.X.ToString("R", CultureInfo.InvariantCulture) + "," + p.Y.ToString("R", CultureInfo.InvariantCulture) + ")";
string Path0(DxfProvenance p) => string.Join(">", p.InsertPath);

string? CodeOf(Func<object?> action)
{
    try { action(); return null; }
    catch (DxfFormatException error) { return error.Code; }
    catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
}

int LineOf(Func<object?> action)
{
    try { action(); return -1; }
    catch (DxfFormatException error) { return error.Line; }
    catch (Exception) { return -2; }
}

var fixtures = Path.Combine(RepoRoot(), "tests", "cad", "fixtures");
var simplePath = Path.Combine(fixtures, "simple-plan.dxf");
var wallsPath = Path.Combine(fixtures, "walls.dxf");
var blocksPath = Path.Combine(fixtures, "blocks.dxf");

// ---------- simple-plan.dxf: header, layers, every entity type, provenance ----------
try
{
    var d = DxfReader.ReadFile(simplePath);
    Check(d.SourceFile == "simple-plan.dxf", "simple source file name", d.SourceFile);
    Check(d.InsUnits == 4 && d.Units == DxfUnits.Millimetres && d.MetresPerUnit is { } mpu && Near(mpu, 0.001), "simple $INSUNITS 4 is millimetres", d.Units + " " + d.MetresPerUnit);
    Check(string.Join(",", d.Layers.Select(l => l.Name)) == "0,ANNO,DOORS,NOTES,WALLS", "simple layers sorted with undeclared NOTES", string.Join(",", d.Layers.Select(l => l.Name)));
    var doors = d.Layers.Single(l => l.Name == "DOORS");
    Check(doors.Declared && doors.Frozen && doors.Off && doors.Color == 3 && doors.LineType == "DASHED", "simple DOORS layer flags frozen, off (negative colour), colour, linetype", doors.ToString());
    var walls = d.Layers.Single(l => l.Name == "WALLS");
    Check(walls.Declared && !walls.Frozen && !walls.Off && walls.Color == 1 && walls.LineType == "CONTINUOUS", "simple WALLS layer", walls.ToString());
    Check(!d.Layers.Single(l => l.Name == "NOTES").Declared, "simple NOTES layer referenced but undeclared");
    Check(d.Entities.Count == 11, "simple 11 modelled entities", d.Entities.Count.ToString(CultureInfo.InvariantCulture));
    Check(string.Join(",", d.Entities.Select(e => e.Provenance.Handle)) == "100,101,102,103,104,105,106,107,108,109,10A", "simple entities keep file order", string.Join(",", d.Entities.Select(e => e.Provenance.Handle)));
    Check(d.Unsupported.Count == 1 && d.Unsupported[0].EntityType == "POINT" && d.Unsupported[0].Handle == "10B" && d.Unsupported[0].Line == 351 && d.Unsupported[0].Layer == "ANNO", "simple unsupported POINT keeps provenance", string.Join(";", d.Unsupported));

    var line = d.Entities[0] as DxfLine;
    Check(line is not null && line.Provenance.SourceFile == "simple-plan.dxf" && line.Provenance.Handle == "100" && line.Provenance.Layer == "WALLS"
        && line.Provenance.EntityType == "LINE" && line.Provenance.Line == 149 && line.Provenance.InsertPath.Count == 0 && NearP(line.Start, 0, 0) && NearP(line.End, 5000, 0),
        "simple LINE 100 geometry and provenance", line?.ToString() ?? "null");
    Check(d.Entities.Select(e => e.Provenance.Line).SequenceEqual(new[] { 149, 167, 185, 203, 221, 239, 263, 281, 293, 311, 329 }), "simple entity line numbers", string.Join(",", d.Entities.Select(e => e.Provenance.Line)));

    var poly = d.Entities[5] as DxfPolyline;
    Check(poly is not null && poly.Vertices.Count == 3 && !poly.Closed && poly.Vertices[1].Bulge == 1.0 && poly.Vertices[0].Bulge == 0 && poly.Provenance.EntityType == "LWPOLYLINE" && poly.Provenance.Layer == "DOORS",
        "simple LWPOLYLINE vertices and bulge", poly?.ToString() ?? "null");
    if (poly is not null)
    {
        var segs = poly.Segments();
        Check(segs.Count == 2 && !segs[0].IsArc && NearP(segs[0].Midpoint, 500, 3000), "simple polyline straight segment", string.Join(";", segs));
        Check(segs.Count == 2 && segs[1].IsArc && segs[1].Center is { } c && NearP(c, 1500, 3000, 1e-9) && Near(segs[1].Radius, 500, 1e-9) && NearP(segs[1].Midpoint, 1500, 2500, 1e-9)
            && Near(segs[1].StartAngleDegrees, 180, 1e-9) && Near(segs[1].EndAngleDegrees, 0, 1e-9), "simple bulge 1 is a counter-clockwise semicircle below the chord", string.Join(";", segs));
    }
    var arc = d.Entities[6] as DxfArc;
    Check(arc is not null && NearP(arc.Center, 3000, 3000) && arc.Radius == 250 && arc.StartAngleDegrees == 0 && arc.EndAngleDegrees == 180 && arc.Provenance.Handle == "106", "simple ARC", arc?.ToString() ?? "null");
    var circle = d.Entities[7] as DxfCircle;
    Check(circle is not null && circle.Provenance.Layer == "0" && NearP(circle.Center, 7000, 3000) && circle.Radius == 300, "simple CIRCLE defaults to layer 0", circle?.ToString() ?? "null");
    var text = d.Entities[8] as DxfText;
    Check(text is not null && !text.IsMText && text.Value == "KITCHEN" && text.PlainText == "KITCHEN" && text.Height == 250 && text.RotationDegrees == 0 && NearP(text.Insertion, 1000, 1000) && text.Provenance.EntityType == "TEXT",
        "simple TEXT", text?.ToString() ?? "null");
    var mtext = d.Entities[9] as DxfText;
    Check(mtext is not null && mtext.IsMText && mtext.Value == "Living\\PRoom" && mtext.PlainText == "Living\nRoom" && mtext.Height == 200 && mtext.Provenance.Layer == "NOTES" && mtext.Provenance.EntityType == "MTEXT" && mtext.Provenance.Line == 311,
        "simple MTEXT chunks joined and \\P made a newline", mtext?.ToString() ?? "null");
    var insert = d.Entities[10] as DxfInsert;
    Check(insert is not null && insert.BlockName == "DOOR" && insert.ScaleX == 2 && insert.ScaleY == 2 && insert.RotationDegrees == 90 && NearP(insert.Insertion, 5000, 500) && insert.Provenance.Handle == "10A",
        "simple INSERT", insert?.ToString() ?? "null");
    Check(d.Blocks.Count == 1 && d.Blocks[0].Name == "DOOR" && d.Blocks[0].Handle == "20" && d.Blocks[0].Entities.Count == 2 && d.Blocks[0].Entities[0].Provenance.Handle == "21" && d.Blocks[0].Entities[0].Provenance.Line == 101
        && d.Blocks[0].Entities[0].Provenance.Layer == "0" && d.Blocks[0].Entities[1].Provenance.Layer == "DOORS", "simple DOOR block definition", string.Join(";", d.Blocks));

    var x = d.Explode();
    Check(x.Entities.Count == 12 && !x.Entities.OfType<DxfInsert>().Any(), "simple explode replaces INSERT by its 2 block entities", x.Entities.Count.ToString(CultureInfo.InvariantCulture));
    var xl = x.Entities.OfType<DxfLine>().SingleOrDefault(e => e.Provenance.InsertPath.Count > 0);
    Check(xl is not null && NearP(xl.Start, 5000, 500, 1e-9) && NearP(xl.End, 5000, 2300, 1e-9) && xl.Provenance.Layer == "DOORS" && xl.Provenance.Handle == "21" && Path0(xl.Provenance) == "10A" && xl.Provenance.Line == 101,
        "simple exploded door leaf: rotated 90, scaled 2, layer 0 inherits DOORS", xl?.ToString() ?? "null");
    var xa = x.Entities.OfType<DxfArc>().SingleOrDefault(e => e.Provenance.InsertPath.Count > 0);
    Check(xa is not null && NearP(xa.Center, 5000, 500, 1e-9) && Near(xa.Radius, 1800, 1e-9) && Near(xa.StartAngleDegrees, 90, 1e-9) && Near(xa.EndAngleDegrees, 180, 1e-9) && xa.Provenance.Handle == "22",
        "simple exploded door swing arc", xa?.ToString() ?? "null");
    Check(x.Entities.Take(10).Select(e => e.Provenance.Handle).SequenceEqual(d.Entities.Take(10).Select(e => e.Provenance.Handle)), "simple explode keeps order of non-insert entities");

    var pairs = WallPairDetector.Detect(d, WallPairOptions.FromMetres(0.1, 0.3, 0.5, d.MetresPerUnit ?? 1));
    Check(pairs.Count == 1, "simple exactly one wall pair", string.Join(";", pairs.Select(p => p.A.Source.Handle + "/" + p.B.Source.Handle)));
    if (pairs.Count == 1)
    {
        var p = pairs[0];
        Check(p.A.Source.Handle == "100" && p.B.Source.Handle == "101" && p.A.SegmentIndex is null && Near(p.Thickness, 200) && Near(p.Overlap, 5000) && Near(p.Confidence, 1) && Near(p.AngleDeviationDegrees, 0)
            && ((NearP(p.CenterlineStart, 0, 100) && NearP(p.CenterlineEnd, 5000, 100)) || (NearP(p.CenterlineStart, 5000, 100) && NearP(p.CenterlineEnd, 0, 100))),
            "simple wall pair 100/101 thickness 200 with centreline", p.ToString());
    }

    // Determinism: LF, CRLF, BOM and culture variants give the same canonical JSON and line numbers.
    var lf = File.ReadAllText(simplePath).Replace("\r\n", "\n", StringComparison.Ordinal);
    var crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);
    var jsonLf = DxfJson.Serialize(DxfReader.Parse(lf, "simple-plan.dxf"));
    var jsonCrlf = DxfJson.Serialize(DxfReader.Parse(crlf, "simple-plan.dxf"));
    var bom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(crlf)).ToArray();
    var jsonBom = DxfJson.Serialize(DxfReader.Parse(bom, "simple-plan.dxf"));
    var jsonFile = DxfJson.Serialize(d);
    var culture = CultureInfo.CurrentCulture;
    string jsonDe;
    try { CultureInfo.CurrentCulture = new CultureInfo("de-DE"); jsonDe = DxfJson.Serialize(DxfReader.Parse(lf, "simple-plan.dxf")); }
    finally { CultureInfo.CurrentCulture = culture; }
    Check(jsonLf == jsonCrlf && jsonLf == jsonBom && jsonLf == jsonFile && jsonLf == jsonDe && jsonLf == DxfJson.Serialize(DxfReader.Parse(lf, "simple-plan.dxf")),
        "simple canonical JSON identical across LF, CRLF, BOM, file, de-DE and repeated runs", jsonLf.Length + " " + jsonCrlf.Length + " " + jsonBom.Length + " " + jsonDe.Length);
    using (var doc = JsonDocument.Parse(jsonLf))
    {
        var root = doc.RootElement;
        Check(root.GetProperty("dxf_model_version").GetInt32() == 1 && root.GetProperty("units").GetString() == "millimetres" && root.GetProperty("insunits").GetInt32() == 4
            && root.GetProperty("entities").GetArrayLength() == 11 && root.GetProperty("entities")[0].GetProperty("handle").GetString() == "100"
            && root.GetProperty("entities")[0].GetProperty("line").GetInt32() == 149 && root.GetProperty("entities")[0].GetProperty("type").GetString() == "LINE",
            "simple canonical JSON carries provenance", jsonLf.Length > 300 ? jsonLf[..300] : jsonLf);
    }
    var pairsJson = DxfJson.Serialize(pairs);
    Check(pairsJson == DxfJson.Serialize(WallPairDetector.Detect(DxfReader.Parse(crlf, "simple-plan.dxf"), WallPairOptions.FromMetres(0.1, 0.3, 0.5, 0.001))) && pairsJson.Contains("\"100\"", StringComparison.Ordinal),
        "simple wall pair JSON deterministic and carries handles", pairsJson);
}
catch (Exception error)
{
    Console.WriteLine("::error::cad FAIL simple-plan unexpected " + error.GetType().Name + ": " + error.Message);
    failed++;
}

// ---------- walls.dxf: wall-pair detection ----------
try
{
    var d = DxfReader.ReadFile(wallsPath);
    Check(d.Units == DxfUnits.Metres && d.MetresPerUnit == 1 && d.InsUnits == 6, "walls $INSUNITS 6 is metres");
    Check(string.Join(",", d.Layers.Select(l => l.Name + ":" + l.Declared)) == "FURNITURE:False,WALLS:False", "walls layers without a LAYER table are undeclared", string.Join(",", d.Layers.Select(l => l.Name)));
    var options = new WallPairOptions(0.1, 0.5, 0.3);
    var pairs = WallPairDetector.Detect(d, options);
    string Key(WallPair p) => p.A.Source.Handle + (p.A.SegmentIndex is { } i ? "#" + i.ToString(CultureInfo.InvariantCulture) : "") + "/" + p.B.Source.Handle + (p.B.SegmentIndex is { } j ? "#" + j.ToString(CultureInfo.InvariantCulture) : "");
    var keys = string.Join(",", pairs.Select(Key));
    Check(keys == "206#0/206#2,20D/20E,204/205,200/201,202/203", "walls five pairs ordered by confidence then file order", keys);
    WallPair? Find(string key) => pairs.FirstOrDefault(p => Key(p) == key);
    Check(Find("200/201") is { } bottom && Near(bottom.Thickness, 0.2, 1e-9) && Near(bottom.Overlap, 5.6, 1e-9) && Near(bottom.Confidence, 5.6 / 6, 1e-9) && bottom.A.Source.Layer == "WALLS" && bottom.A.Source.Line > 0,
        "walls bottom pair thickness 0.2, overlap 5.6, confidence overlap/longer", Find("200/201")?.ToString() ?? "null");
    Check(Find("202/203") is { } left && Near(left.Thickness, 0.2, 1e-9) && Near(left.Overlap, 3.6, 1e-9) && Near(left.Confidence, 0.9, 1e-9)
        && Near(left.CenterlineStart.X, 0.1, 1e-9) && Near(left.CenterlineEnd.X, 0.1, 1e-9) && Near(Math.Abs(left.CenterlineEnd.Y - left.CenterlineStart.Y), 3.6, 1e-9), "walls vertical pair and centreline", Find("202/203")?.ToString() ?? "null");
    Check(Find("204/205") is { } skew && Near(skew.Thickness, 0.2541, 1e-6) && skew.AngleDeviationDegrees > 0.1 && skew.AngleDeviationDegrees < 0.13 && skew.Confidence > 0.9 && skew.Confidence < 0.95,
        "walls slightly skewed pair lowers confidence", Find("204/205")?.ToString() ?? "null");
    Check(Find("206#0/206#2") is { } poly && Near(poly.Thickness, 0.15, 1e-9) && Near(poly.Overlap, 4, 1e-9) && Near(poly.Confidence, 1, 1e-12) && poly.A.Source.EntityType == "LWPOLYLINE",
        "walls closed polyline rectangle long sides pair", Find("206#0/206#2")?.ToString() ?? "null");
    Check(pairs.All(p => p.Thickness >= options.MinThickness && p.Thickness <= options.MaxThickness && p.Overlap >= options.MinOverlap && p.Confidence > 0 && p.Confidence <= 1 && p.AngleDeviationDegrees <= options.AngleToleranceDegrees),
        "walls every reported pair satisfies the options");
    var wallsOnly = WallPairDetector.Detect(d, options with { Layers = new[] { "walls" } });
    Check(wallsOnly.Count == 4 && wallsOnly.All(p => p.A.Source.Layer == "WALLS" && p.B.Source.Layer == "WALLS"), "walls layer filter (case-insensitive) excludes FURNITURE", string.Join(",", wallsOnly.Select(Key)));
    Check(WallPairDetector.Detect(d, options with { MaxThickness = 0.7 }).Any(p => Key(p) == "207/208") && !pairs.Any(p => Key(p) == "207/208"), "walls too-thick pair only with a wider range");
    Check(WallPairDetector.Detect(d, options with { MinOverlap = 0.05 }).Any(p => Key(p) == "209/20A") && !pairs.Any(p => Key(p) == "209/20A"), "walls short-overlap pair only with a lower overlap");
    Check(WallPairDetector.Detect(d, options with { AngleToleranceDegrees = 5 }).Any(p => Key(p) == "20B/20C") && !pairs.Any(p => Key(p) == "20B/20C"), "walls non-parallel pair only with a wider angle tolerance");
    Check(DxfJson.Serialize(pairs) == DxfJson.Serialize(WallPairDetector.Detect(DxfReader.ReadFile(wallsPath), options)), "walls detection deterministic");
    Check(CodeOf(() => WallPairDetector.Detect(d, new WallPairOptions(0.5, 0.1, 0.3)))?.StartsWith("unexpected Argument", StringComparison.Ordinal) == true, "walls inverted thickness range rejected as an argument error");
    var metric = WallPairOptions.FromMetres(0.1, 0.3, 0.5, 0.001);
    Check(Near(metric.MinThickness, 100, 1e-9) && Near(metric.MaxThickness, 300, 1e-9) && Near(metric.MinOverlap, 500, 1e-9), "walls FromMetres converts to drawing units");

    // Bounded performance: 2000 random segments.
    var random = new Random(137);
    var many = new List<DxfEntity>();
    for (var i = 0; i < 2000; i++)
    {
        var a = new Point2(random.NextDouble() * 100, random.NextDouble() * 100);
        var angle = random.Next(4) * Math.PI / 2;
        many.Add(new DxfLine(new DxfProvenance("gen", i.ToString("X", CultureInfo.InvariantCulture), "WALLS", "LINE", i + 1), a, a + new Point2(Math.Cos(angle), Math.Sin(angle)) * (1 + random.NextDouble() * 5)));
    }
    var clock = Stopwatch.StartNew();
    var found = WallPairDetector.Detect(many, options);
    clock.Stop();
    Check(clock.ElapsedMilliseconds < 5000 && found.All(p => p.Confidence > 0 && p.Confidence <= 1), "walls 2000 segments detected within 5 s", clock.ElapsedMilliseconds + " ms, " + found.Count + " pairs");
}
catch (Exception error)
{
    Console.WriteLine("::error::cad FAIL walls unexpected " + error.GetType().Name + ": " + error.Message);
    failed++;
}

// ---------- blocks.dxf (CRLF, no HEADER): nested, mirrored, non-uniform, cyclic and missing blocks, OCS ----------
try
{
    var raw = File.ReadAllBytes(blocksPath);
    Check(raw.Length > 0 && Encoding.ASCII.GetString(raw).Contains("\r\n", StringComparison.Ordinal), "blocks fixture is checked in with CRLF line ends");
    var d = DxfReader.ReadFile(blocksPath);
    Check(d.InsUnits is null && d.Units == DxfUnits.Unspecified && d.MetresPerUnit is null, "blocks without HEADER has unspecified units");
    Check(string.Join(",", d.Blocks.Select(b => b.Name)) == "LEAF,LOOP_A,LOOP_B,PAIR", "blocks sorted by name", string.Join(",", d.Blocks.Select(b => b.Name)));
    Check(d.Entities.Count == 6 && d.Entities[0].Provenance.Line == 185 && d.Entities[5].Provenance.Line == 259, "blocks entity count and CRLF line numbers", string.Join(",", d.Entities.Select(e => e.Provenance.Line)));
    var x = d.Explode();
    Check(x.Entities.Count == 13, "blocks explode yields 13 entities", x.Entities.Count + ": " + string.Join(";", x.Entities.Select(e => e.Provenance.EntityType + "@" + Path0(e.Provenance))));
    Check(x.Warnings.Any(w => w.Code == "block-cycle" && w.Line > 0) && x.Warnings.Any(w => w.Code == "block-missing" && w.Line == 247) && x.Warnings.Any(w => w.Code == "arc-tessellated"),
        "blocks explode warns about cycle, missing block and tessellation", string.Join(";", x.Warnings.Select(w => w.Code + "@" + w.Line.ToString(CultureInfo.InvariantCulture))));
    var leaf = x.Entities.OfType<DxfLine>().FirstOrDefault(e => Path0(e.Provenance) == "40>32");
    Check(leaf is not null && NearP(leaf.Start, 100, 101, 1e-9) && NearP(leaf.End, 100, 102, 1e-9) && leaf.Provenance.Layer == "L1" && leaf.Provenance.Handle == "30" && leaf.Provenance.Line == 109,
        "blocks nested rotated leaf with base point, layer 0 inherited through two levels", leaf?.ToString() ?? "null");
    var leaf31 = x.Entities.OfType<DxfLine>().FirstOrDefault(e => Path0(e.Provenance) == "40>31");
    Check(leaf31 is not null && NearP(leaf31.Start, 100, 100, 1e-9) && NearP(leaf31.End, 101, 100, 1e-9), "blocks nested unrotated leaf", leaf31?.ToString() ?? "null");
    var mirroredArc = x.Entities.OfType<DxfArc>().FirstOrDefault(e => Path0(e.Provenance) == "41");
    Check(mirroredArc is not null && NearP(mirroredArc.Center, -2, 0, 1e-9) && Near(mirroredArc.Radius, 1, 1e-9) && Near(mirroredArc.StartAngleDegrees, 90, 1e-9) && Near(mirroredArc.EndAngleDegrees, 180, 1e-9) && mirroredArc.Provenance.Layer == "L2",
        "blocks mirrored arc keeps counter-clockwise sense", mirroredArc?.ToString() ?? "null");
    var mirroredPoly = x.Entities.OfType<DxfPolyline>().FirstOrDefault(e => Path0(e.Provenance) == "41");
    Check(mirroredPoly is not null && mirroredPoly.Vertices.Count == 2 && NearP(mirroredPoly.Vertices[0].Point, 0, 0, 1e-9) && NearP(mirroredPoly.Vertices[1].Point, -2, 0, 1e-9) && Near(mirroredPoly.Vertices[0].Bulge, -1, 1e-12)
        && mirroredPoly.Segments()[0].Center is { } mc && NearP(mc, -1, 0, 1e-9) && NearP(mirroredPoly.Segments()[0].Midpoint, -1, -1, 1e-9), "blocks mirrored polyline negates bulge", mirroredPoly?.ToString() ?? "null");
    Check(!x.Entities.OfType<DxfArc>().Any(e => Path0(e.Provenance) == "42"), "blocks non-uniform scale leaves no circular arc");
    var ellipse = x.Entities.OfType<DxfPolyline>().Where(e => Path0(e.Provenance) == "42" && e.Provenance.EntityType == "ARC").ToList();
    Check(ellipse.Count == 1 && ellipse[0].Vertices.Count >= 9 && ellipse[0].Vertices.All(v => v.Bulge == 0 && Near(Math.Pow((v.Point.X - 4) / 2, 2) + Math.Pow(v.Point.Y - 50, 2), 1, 1e-9))
        && NearP(ellipse[0].Vertices[0].Point, 6, 50, 1e-9) && NearP(ellipse[0].Vertices[^1].Point, 4, 51, 1e-9) && ellipse[0].Provenance.Handle == "33",
        "blocks non-uniform arc tessellated on the scaled ellipse with ARC provenance", ellipse.Count == 1 ? string.Join(" ", ellipse[0].Vertices.Select(v => P(v.Point))) : ellipse.Count.ToString(CultureInfo.InvariantCulture));
    var leaf42 = x.Entities.OfType<DxfLine>().FirstOrDefault(e => Path0(e.Provenance) == "42>31");
    Check(leaf42 is not null && NearP(leaf42.Start, 0, 50, 1e-9) && NearP(leaf42.End, 2, 50, 1e-9), "blocks non-uniform scaled line", leaf42?.ToString() ?? "null");
    var ocs = x.Entities.OfType<DxfArc>().FirstOrDefault(e => e.Provenance.Handle == "45");
    Check(ocs is not null && NearP(ocs.Center, -5, 5, 1e-9) && Near(ocs.StartAngleDegrees, 90, 1e-9) && Near(ocs.EndAngleDegrees, 180, 1e-9) && ocs.Provenance.InsertPath.Count == 0,
        "blocks ARC with extrusion (0,0,-1) mapped to world coordinates", ocs?.ToString() ?? "null");

    // Exponential nesting is stopped by the entity budget, never hangs.
    var nest = new StringBuilder("0\nSECTION\n2\nBLOCKS\n");
    for (var level = 0; level < 10; level++)
    {
        nest.Append("0\nBLOCK\n2\nB").Append(level).Append("\n10\n0\n20\n0\n");
        for (var k = 0; k < 10; k++) nest.Append(level < 9 ? "0\nINSERT\n2\nB" + (level + 1).ToString(CultureInfo.InvariantCulture) + "\n10\n" + k.ToString(CultureInfo.InvariantCulture) + "\n20\n0\n" : "0\nLINE\n10\n0\n20\n0\n11\n1\n21\n0\n");
        nest.Append("0\nENDBLK\n");
    }
    nest.Append("0\nENDSEC\n0\nSECTION\n2\nENTITIES\n0\nINSERT\n2\nB0\n10\n0\n20\n0\n0\nENDSEC\n0\nEOF\n");
    var bomb = DxfReader.Parse(nest.ToString(), "bomb.dxf");
    var clock = Stopwatch.StartNew();
    Check(CodeOf(() => bomb.Explode(new DxfExplodeOptions(MaxEntities: 100_000))) == "explode-limit" && clock.ElapsedMilliseconds < 10_000, "blocks exponential nesting stopped by explode-limit", clock.ElapsedMilliseconds + " ms");
    Check(CodeOf(() => bomb.Explode(new DxfExplodeOptions(MaxDepth: 3))) == "explode-limit", "blocks nesting deeper than MaxDepth refused");
}
catch (Exception error)
{
    Console.WriteLine("::error::cad FAIL blocks unexpected " + error.GetType().Name + ": " + error.Message);
    failed++;
}

// ---------- refusals ----------
try
{
    var binary = Encoding.ASCII.GetBytes("AutoCAD Binary DXF\r\n\u001a\0").Concat(new byte[] { 0, 0, 1, 2, 3 }).ToArray();
    Check(CodeOf(() => DxfReader.Parse(binary, "b.dxf")) == "binary-dxf", "binary DXF sentinel refused");
    var temp = Path.Combine(Path.GetTempPath(), "bim-cad-binary-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".dxf");
    File.WriteAllBytes(temp, binary);
    try { Check(CodeOf(() => DxfReader.ReadFile(temp)) == "binary-dxf", "binary DXF file refused by ReadFile"); }
    finally { File.Delete(temp); }
    Check(CodeOf(() => DxfReader.Parse("0\nSECTION\n2\nENTITIES\u0000\n0\nENDSEC\n0\nEOF\n", "n.dxf")) == "binary-dxf", "NUL character refused as binary");
    Check(CodeOf(() => DxfReader.Parse("", "e.dxf")) == "empty" && CodeOf(() => DxfReader.Parse("\n \r\n", "e.dxf")) == "empty" && CodeOf(() => DxfReader.Parse(Array.Empty<byte>(), "e.dxf")) == "empty", "empty input refused");
    Check(CodeOf(() => DxfReader.Parse("abc\nSECTION\n", "m.dxf")) == "malformed-group-code" && LineOf(() => DxfReader.Parse("abc\nSECTION\n", "m.dxf")) == 1, "non-integer group code refused with its line");
    Check(CodeOf(() => DxfReader.Parse("0\nSECTION\n99999\nx\n0\nEOF\n", "m.dxf")) == "malformed-group-code" && LineOf(() => DxfReader.Parse("0\nSECTION\n99999\nx\n0\nEOF\n", "m.dxf")) == 3, "out-of-range group code refused");
    Check(CodeOf(() => DxfReader.Parse("0\nSECTION\n2\nENTITIES\n0", "m.dxf")) == "missing-value" && LineOf(() => DxfReader.Parse("0\nSECTION\n2\nENTITIES\n0", "m.dxf")) == 5, "group code without value refused");
    Check(CodeOf(() => DxfReader.Parse("0\nSECTION\n2\nENTITIES\n0\nENDSEC\n", "m.dxf")) == "missing-eof", "missing EOF refused as truncated");
    const string badNumber = "0\nSECTION\n2\nENTITIES\n0\nLINE\n10\n1.2.3\n20\n0\n11\n1\n21\n0\n0\nENDSEC\n0\nEOF\n";
    Check(CodeOf(() => DxfReader.Parse(badNumber, "m.dxf")) == "bad-number" && LineOf(() => DxfReader.Parse(badNumber, "m.dxf")) == 8, "unparsable coordinate refused with the value line");
    Check(CodeOf(() => DxfReader.Parse(badNumber.Replace("1.2.3", "NaN", StringComparison.Ordinal), "m.dxf")) == "bad-number", "non-finite coordinate refused");
    Check(CodeOf(() => DxfReader.Parse("0\nSECTION\n2\nENTITIES\n0\nLINE\n10\n0\n20\n0\n11\n1\n21\n0\n0\nEOF\n", "m.dxf")) == "unterminated-section", "section without ENDSEC refused");
    Check(CodeOf(() => DxfReader.Parse("0\nSECTION\n2\nENTITIES\n0\nENDSEC\n0\nEOF\n", "m.dxf", new DxfReadOptions(MaxBytes: 10))) == "too-large", "input over MaxBytes refused");
    var minimal = DxfReader.Parse("0\nSECTION\n2\nENTITIES\n0\nLINE\n8\nA\n10\n0\n20\n0\n11\n1\n21\n0\n0\nENDSEC\n0\nEOF\n", "m.dxf");
    Check(minimal.Entities.Count == 1 && minimal.Entities[0].Provenance.Handle is null && minimal.Entities[0].Provenance.Line == 5 && minimal.Units == DxfUnits.Unspecified, "minimal entities-only DXF without handles reads");
    var incomplete = DxfReader.Parse("0\nSECTION\n2\nENTITIES\n0\nCIRCLE\n10\n0\n20\n0\n40\n-1\n0\nENDSEC\n0\nEOF\n", "m.dxf");
    Check(incomplete.Entities.Count == 0 && incomplete.Warnings.Any(w => w.Code == "entity-invalid" && w.Line == 5), "non-positive radius skipped with a warning", string.Join(";", incomplete.Warnings));
    var unknownUnits = DxfReader.Parse("0\nSECTION\n2\nHEADER\n9\n$INSUNITS\n70\n77\n0\nENDSEC\n0\nEOF\n", "m.dxf");
    Check(unknownUnits.Units == DxfUnits.Unknown && unknownUnits.InsUnits == 77 && unknownUnits.MetresPerUnit is null && unknownUnits.Warnings.Any(w => w.Code == "units-unknown"), "unknown $INSUNITS warned");
    var inches = DxfReader.Parse("0\nSECTION\n2\nHEADER\n9\n$INSUNITS\n70\n1\n0\nENDSEC\n0\nEOF\n", "m.dxf");
    Check(inches.Units == DxfUnits.Inches && inches.MetresPerUnit is { } inch && Near(inch, 0.0254, 1e-15), "inches $INSUNITS");

    // Mutation fuzz: deleting, duplicating or corrupting lines either reads or raises DxfFormatException.
    var lines = File.ReadAllText(simplePath).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    var random = new Random(6011);
    var unexpected = new List<string>();
    var clock = Stopwatch.StartNew();
    for (var iteration = 0; iteration < 400; iteration++)
    {
        var mutated = lines.ToList();
        for (var m = 0; m < 1 + random.Next(3); m++)
        {
            var at = random.Next(mutated.Count);
            switch (random.Next(4))
            {
                case 0: mutated.RemoveAt(at); break;
                case 1: mutated.Insert(at, mutated[at]); break;
                case 2: mutated[at] = new[] { "0", "-1", "garbage", "1e309", "", "  10", "ENDSEC", "EOF", "BLOCK", "INSERT", "42" }[random.Next(11)]; break;
                default: var other = random.Next(mutated.Count); (mutated[at], mutated[other]) = (mutated[other], mutated[at]); break;
            }
        }
        try
        {
            var parsed = DxfReader.Parse(string.Join("\n", mutated), "fuzz.dxf");
            DxfJson.Serialize(parsed);
            WallPairDetector.Detect(parsed, new WallPairOptions(100, 300, 500));
        }
        catch (DxfFormatException) { }
        catch (Exception error) { unexpected.Add(iteration.ToString(CultureInfo.InvariantCulture) + " " + error.GetType().Name + ": " + error.Message); }
    }
    clock.Stop();
    Check(unexpected.Count == 0 && clock.ElapsedMilliseconds < 20_000, "mutation fuzz raises only DxfFormatException and stays bounded", clock.ElapsedMilliseconds + " ms; " + string.Join(" | ", unexpected.Take(3)));
}
catch (Exception error)
{
    Console.WriteLine("::error::cad FAIL refusals unexpected " + error.GetType().Name + ": " + error.Message);
    failed++;
}

// ---------- review hardening: explode/detect budgets, paper space, transforms, OCS, layer case, text ----------
// G("0 LINE|8 A|10 0") turns "code value" items separated by '|' into DXF group lines.
string G(string spec) => string.Concat(spec.Split('|').Select(item => { var space = item.IndexOf(' ', StringComparison.Ordinal); return item[..space] + "\n" + item[(space + 1)..] + "\n"; }));
string Doc(string entities, string blocks = "", string tables = "") =>
    (tables.Length > 0 ? G("0 SECTION|2 TABLES") + tables + G("0 ENDSEC") : "")
    + (blocks.Length > 0 ? G("0 SECTION|2 BLOCKS") + blocks + G("0 ENDSEC") : "")
    + G("0 SECTION|2 ENTITIES") + entities + G("0 ENDSEC|0 EOF");
string Bomb(string leaf)
{
    var blocks = new StringBuilder();
    for (var level = 0; level < 10; level++)
    {
        blocks.Append(G("0 BLOCK|2 B" + level.ToString(CultureInfo.InvariantCulture) + "|10 0|20 0"));
        for (var k = 0; k < 10; k++) blocks.Append(level < 9 ? G("0 INSERT|2 B" + (level + 1).ToString(CultureInfo.InvariantCulture) + "|10 " + k.ToString(CultureInfo.InvariantCulture) + "|20 0") : leaf);
        blocks.Append(G("0 ENDBLK"));
    }
    return Doc(G("0 INSERT|2 B0|10 0|20 0"), blocks.ToString());
}
try
{
    // Blocker 1: work budget counts insert visits and warnings, not only emitted entities.
    // Each call runs on a worker with a hard wait, so a regression fails instead of hanging; after one timeout the
    // remaining bombs are not started (a runaway missing-block bomb would otherwise grow warnings without bound).
    string Timed(Func<object?> action, int waitMs, out long elapsedMs)
    {
        var timer = Stopwatch.StartNew();
        var task = Task.Run(() => CodeOf(action));
        var done = task.Wait(waitMs);
        elapsedMs = timer.ElapsedMilliseconds;
        return done ? task.Result ?? "no error" : "timeout";
    }
    var timedOut = false;
    foreach (var (label, leaf) in new[] { ("empty leaf", ""), ("unsupported-only leaf", G("0 POINT|10 0|20 0")), ("missing-block leaf", G("0 INSERT|2 NOPE|10 0|20 0")) })
    {
        if (timedOut) { Check(false, "review insert bomb with " + label, "not run after an earlier timeout"); continue; }
        var bomb = DxfReader.Parse(Bomb(leaf), "bomb.dxf");
        var code = Timed(() => bomb.Explode(new DxfExplodeOptions(MaxWork: 100_000)), 5000, out var ms);
        Check(code == "explode-limit" && ms < 1000, "review insert bomb with " + label + " stopped by the work budget within 1 s", code + " " + ms + " ms");
        if (code == "timeout") { timedOut = true; continue; }
        code = Timed(() => bomb.Explode(), 10_000, out ms);
        Check(code == "explode-limit", "review insert bomb with " + label + " stopped by the default budget", code + " " + ms + " ms");
        if (code == "timeout") { timedOut = true; continue; }
        code = Timed(() => WallPairDetector.Detect(bomb, new WallPairOptions(0.1, 0.5, 0.3)), 10_000, out ms);
        Check(code == "explode-limit", "review wall detection on the " + label + " bomb fails closed", code + " " + ms + " ms");
        if (code == "timeout") timedOut = true;
    }

    // Should-fix 2: axis-aligned plans are not quadratic; explicit limits.
    var stack = new List<DxfEntity>();
    for (var i = 0; i < 20_000; i++) stack.Add(new DxfLine(new DxfProvenance("gen", null, "WALLS", "LINE", i + 1), new Point2(0, i * 10.0), new Point2(100, i * 10.0)));
    var watch = Stopwatch.StartNew();
    var none = WallPairDetector.Detect(stack, new WallPairOptions(0.1, 0.5, 0.3));
    watch.Stop();
    Check(none.Count == 0 && watch.ElapsedMilliseconds < 2000, "review 20k parallel lines 10 apart give no pairs quickly", none.Count + " pairs, " + watch.ElapsedMilliseconds + " ms");
    stack.Add(new DxfLine(new DxfProvenance("gen", "X", "WALLS", "LINE", 99_999), new Point2(10, 50_000.2), new Point2(90, 50_000.2)));
    var one = WallPairDetector.Detect(stack, new WallPairOptions(0.1, 0.5, 0.3));
    Check(one.Count == 1 && one[0].B.Source.Handle == "X" && Near(one[0].Thickness, 0.2, 1e-6) && Near(one[0].Overlap, 80, 1e-9), "review a real pair among 20k lines is still found", string.Join(";", one.Select(p => p.A.Source.Line + "/" + p.B.Source.Handle)));
    var dense = new List<DxfEntity>();
    for (var i = 0; i < 100; i++)
    {
        dense.Add(new DxfLine(new DxfProvenance("gen", "a" + i.ToString(CultureInfo.InvariantCulture), "W", "LINE", i + 1), new Point2(0, 0), new Point2(10, 0)));
        dense.Add(new DxfLine(new DxfProvenance("gen", "b" + i.ToString(CultureInfo.InvariantCulture), "W", "LINE", i + 101), new Point2(0, 0.2), new Point2(10, 0.2)));
    }
    Check(CodeOf(() => WallPairDetector.Detect(dense, new WallPairOptions(0.1, 0.5, 0.3) { MaxPairs = 500 })) == "detect-limit", "review MaxPairs exceeded raises detect-limit");
    Check(CodeOf(() => WallPairDetector.Detect(dense, new WallPairOptions(0.1, 0.5, 0.3) { MaxSegments = 50 })) == "detect-limit", "review MaxSegments exceeded raises detect-limit");
    Check(CodeOf(() => WallPairDetector.Detect(dense, new WallPairOptions(0.1, 0.5, 0.3) { MaxComparisons = 1000 })) == "detect-limit", "review MaxComparisons exceeded raises detect-limit");
    Check(WallPairDetector.Detect(dense, new WallPairOptions(0.1, 0.5, 0.3)).Count == 10_000, "review dense pairs within default limits all reported");

    // 3: paper space (67), invisible (60) and optionally frozen/off layers are not walls by default.
    var paper = DxfReader.Parse(Doc(G("0 LINE|5 P1|67 1|8 W|10 0|20 0|11 4|21 0|0 LINE|5 P2|67 1|8 W|10 0|20 0.2|11 4|21 0.2")), "paper.dxf");
    Check(paper.Entities.All(e => e.Provenance.PaperSpace && !e.Provenance.Invisible), "review group 67 read into provenance");
    Check(WallPairDetector.Detect(paper, new WallPairOptions(0.1, 0.5, 0.3)).Count == 0 && WallPairDetector.Detect(paper, new WallPairOptions(0.1, 0.5, 0.3) { IncludePaperSpace = true }).Count == 1,
        "review paper-space lines skipped unless IncludePaperSpace");
    var hidden = DxfReader.Parse(Doc(G("0 LINE|5 V1|60 1|8 W|10 0|20 0|11 4|21 0|0 LINE|5 V2|8 W|10 0|20 0.2|11 4|21 0.2")), "hidden.dxf");
    Check(hidden.Entities[0].Provenance.Invisible && !hidden.Entities[1].Provenance.Invisible && !hidden.Entities[0].Provenance.PaperSpace, "review group 60 read into provenance");
    Check(WallPairDetector.Detect(hidden, new WallPairOptions(0.1, 0.5, 0.3)).Count == 0 && WallPairDetector.Detect(hidden, new WallPairOptions(0.1, 0.5, 0.3) { IncludeInvisible = true }).Count == 1,
        "review invisible lines skipped unless IncludeInvisible");
    var layerTable = G("0 TABLE|2 LAYER|0 LAYER|2 WALLS|70 0|62 1|6 CONTINUOUS|0 LAYER|2 HID|70 1|62 5|6 CONTINUOUS|0 LAYER|2 OFFL|70 0|62 -2|6 CONTINUOUS|0 ENDTAB");
    var layered = DxfReader.Parse(Doc(G("0 LINE|8 walls|10 0|20 0|11 4|21 0|0 LINE|8 Walls|10 0|20 0.2|11 4|21 0.2|0 LINE|8 hid|10 0|20 5|11 4|21 5|0 LINE|8 HID|10 0|20 5.2|11 4|21 5.2|0 LINE|8 offl|10 0|20 9|11 4|21 9|0 LINE|8 OFFL|10 0|20 9.2|11 4|21 9.2"), tables: layerTable), "layers.dxf");
    Check(layered.Layers.Count == 3 && layered.Layers.All(l => l.Declared) && layered.Layers.Count(l => string.Equals(l.Name, "WALLS", StringComparison.OrdinalIgnoreCase)) == 1,
        "review layer names are case-insensitive: table WALLS and entity walls are one declared layer", string.Join(",", layered.Layers.Select(l => l.Name + ":" + l.Declared)));
    Check(WallPairDetector.Detect(layered, new WallPairOptions(0.1, 0.5, 0.3)).Count == 3 && WallPairDetector.Detect(layered, new WallPairOptions(0.1, 0.5, 0.3) { SkipHiddenLayers = true }).Count == 1,
        "review frozen and off layers skipped when SkipHiddenLayers", WallPairDetector.Detect(layered, new WallPairOptions(0.1, 0.5, 0.3) { SkipHiddenLayers = true }).Count.ToString(CultureInfo.InvariantCulture));
    var paperInsert = DxfReader.Parse(Doc(G("0 INSERT|5 PI|67 1|2 L|10 0|20 0"), G("0 BLOCK|2 L|10 0|20 0|0 LINE|8 0|10 0|20 0|11 4|21 0|0 LINE|8 0|10 0|20 0.2|11 4|21 0.2|0 ENDBLK")), "paperinsert.dxf");
    Check(paperInsert.Explode().Entities.All(e => e.Provenance.PaperSpace) && WallPairDetector.Detect(paperInsert, new WallPairOptions(0.1, 0.5, 0.3)).Count == 0, "review entities exploded from a paper-space insert stay in paper space");

    // 4a: INSERT rotation 30, scale (2, 0.5), block base (3, 4); hand-computed world coordinates.
    var rotated = DxfReader.Parse(Doc(G("0 INSERT|5 R1|8 A|2 RB|10 10|20 20|41 2|42 0.5|50 30"), G("0 BLOCK|2 RB|10 3|20 4|0 LINE|5 RL|8 0|10 4|20 5|11 6|21 4|0 ENDBLK")), "rotated.dxf").Explode();
    var rl = rotated.Entities.OfType<DxfLine>().SingleOrDefault();
    // (4,5)-(3,4)=(1,1) -> scale (2,0.5) -> rotate 30: (2cos30-0.5sin30, 2sin30+0.5cos30) = (1.4820508075688772, 1.4330127018922193) -> +(10,20)
    // (6,4)-(3,4)=(3,0) -> (6,0) -> (6cos30, 6sin30) = (5.196152422706632, 3) -> +(10,20)
    Check(rl is not null && NearP(rl.Start, 11.482050807568877, 21.433012701892219, 1e-9) && NearP(rl.End, 15.196152422706632, 23, 1e-9) && rl.Provenance.Layer == "A" && Path0(rl.Provenance) == "R1",
        "review INSERT rotation 30, scale (2,0.5), base (3,4) world line", rl is null ? "null" : P(rl.Start) + " " + P(rl.End));

    // 4b: bulges 0.5 and -0.25.
    var bulged = DxfReader.Parse(Doc(G("0 LWPOLYLINE|5 BP|90 3|70 0|10 0|20 0|42 0.5|10 4|20 0|42 -0.25|10 4|20 4")), "bulge.dxf").Entities.OfType<DxfPolyline>().Single().Segments();
    // b=0.5, chord (0,0)-(4,0): r = 4(1+0.25)/2 = 2.5, centre (2, 1.5), sagitta 1 below the chord -> midpoint (2,-1).
    Check(bulged[0].Center is { } c05 && NearP(c05, 2, 1.5, 1e-12) && Near(bulged[0].Radius, 2.5, 1e-12) && NearP(bulged[0].Midpoint, 2, -1, 1e-12), "review bulge 0.5 centre, radius, midpoint", bulged[0].ToString());
    // b=-0.25, chord (4,0)-(4,4): r = 4(1+0.0625)/1 = 4.25, centre (4,2) + (-1,0)*(-3.75) = (7.75, 2), clockwise -> midpoint (3.5, 2).
    Check(bulged[1].Center is { } c25 && NearP(c25, 7.75, 2, 1e-12) && Near(bulged[1].Radius, 4.25, 1e-12) && NearP(bulged[1].Midpoint, 3.5, 2, 1e-12), "review bulge -0.25 centre, radius, midpoint", bulged[1].ToString());

    // 4c: extrusion (0,0,-1) for INSERT, LWPOLYLINE and TEXT (OCS x is world -x).
    var ocs = DxfReader.Parse(Doc(G("0 LWPOLYLINE|5 OP|90 2|70 0|10 1|20 2|42 0.5|10 3|20 2|210 0|220 0|230 -1")
        + G("0 TEXT|5 OT|10 5|20 1|40 1|50 30|1 T|210 0|220 0|230 -1")
        + G("0 INSERT|5 OI|2 OB|10 2|20 3|50 30|210 0|220 0|230 -1"), G("0 BLOCK|2 OB|10 0|20 0|0 LINE|5 OL|10 0|20 0|11 1|21 0|0 ENDBLK")), "ocs.dxf");
    var op = ocs.Entities.OfType<DxfPolyline>().Single();
    // OCS arc (1,2)->(3,2), b 0.5: centre (2, 2.75), r 1.25, midpoint (2, 1.5); mirrored: (-1,2)->(-3,2), b -0.5, midpoint (-2, 1.5).
    Check(NearP(op.Vertices[0].Point, -1, 2) && NearP(op.Vertices[1].Point, -3, 2) && op.Vertices[0].Bulge == -0.5 && op.Segments()[0].Center is { } oc && NearP(oc, -2, 2.75, 1e-12) && NearP(op.Segments()[0].Midpoint, -2, 1.5, 1e-12),
        "review LWPOLYLINE in OCS (0,0,-1)", op.ToString());
    var ot = ocs.Entities.OfType<DxfText>().Single();
    Check(NearP(ot.Insertion, -5, 1) && Near(ot.RotationDegrees, 150, 1e-12), "review TEXT in OCS (0,0,-1) mirrored with rotation 180-30", ot.ToString());
    var oi = ocs.Entities.OfType<DxfInsert>().Single();
    Check(NearP(oi.Insertion, -2, 3) && oi.ScaleX == -1 && oi.ScaleY == 1 && Near(oi.RotationDegrees, 330, 1e-12), "review INSERT in OCS (0,0,-1) as mirrored insert", oi.ToString());
    // OCS line (2,3)->(2+cos30, 3+sin30) mirrored in x: (-2,3)->(-2.866025403784439, 3.5).
    var ol = ocs.Explode().Entities.OfType<DxfLine>().Single();
    Check(NearP(ol.Start, -2, 3, 1e-12) && NearP(ol.End, -2.866025403784439, 3.5, 1e-12), "review INSERT in OCS (0,0,-1) explodes to the mirrored world line", P(ol.Start) + " " + P(ol.End));

    // Nits: a lone surrogate from \U+D800 and absurd bulges.
    var surrogate = DxfReader.Parse(Doc(G("0 MTEXT|5 S1|10 0|20 0|40 1|1 A\\U+D800B")), "surrogate.dxf");
    var plain = surrogate.Entities.OfType<DxfText>().Single().PlainText;
    Check(plain == "A�B" && surrogate.Warnings.Any(w => w.Code == "invalid-text") && CodeOf(() => DxfJson.Serialize(surrogate)) is null, "review lone surrogate replaced with U+FFFD and warned", plain);
    var loneInput = DxfReader.Parse(Doc(G("0 TEXT|5 S2|10 0|20 0|40 1|1 X\uD800Y")), "lone.dxf");
    Check(loneInput.Entities.OfType<DxfText>().Single().Value == "X�Y" && loneInput.Warnings.Any(w => w.Code == "invalid-text") && CodeOf(() => DxfJson.Serialize(loneInput)) is null, "review lone surrogate in string input replaced");
    var huge = DxfReader.Parse(Doc(G("0 LWPOLYLINE|5 HB|90 2|70 0|10 0|20 0|42 1e300|10 1|20 0")), "huge.dxf");
    Check(huge.Entities.Count == 0 && huge.Warnings.Any(w => w.Code == "entity-invalid"), "review absurd bulge rejected with a warning", string.Join(";", huge.Warnings));
}
catch (Exception error)
{
    Console.WriteLine("::error::cad FAIL review unexpected " + error.GetType().Name + ": " + error.Message);
    failed++;
}

Console.WriteLine(JsonSerializer.Serialize(new { passed, failed, cad_dxf = failed == 0 }));
Environment.ExitCode = failed == 0 && passed > 0 ? 0 : 1;
