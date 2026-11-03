using Bim.Cad;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;

// P6-02 PDF vector reader oracle: hand-written fixtures (simple-plan, transforms with cm and q/Q nesting, a Form XObject,
// a two-stream page and an inherited page tree, flate with a FlateDecode content stream) plus PDFs built here with
// ZLibStream; provenance (file, page, object number, operator index) of every path and text, Bezier curves, closepath,
// re, text placement from Tm/Td/TD/T*/'/" and rise, scale calibration from a known dimension, alignment of a reference
// segment, conversion to the DXF drawing model with wall-pair detection, bounded refusals and a mutation fuzz.
// Unit-tested evidence only.
internal static class PdfTests
{
    internal const string SimpleContent = "% simple plan: two wall lines, a room outline, labels, a door swing and a filled marker\n"
        + "0.5 w\n100 100 m\n300 100 l\nS\n100 108 m\n300 108 l\nS\nq\n1 0 0 1 50 50 cm\n0 0 200 100 re\nS\nQ\n"
        + "BT\n/F1 12 Tf\n1 0 0 1 120 400 Tm\n(KITCHEN) Tj\n0 -20 Td\n(Room \\(1\\)) Tj\nET\n"
        + "400 400 m\n400 455.228 444.772 500 500 500 c\nS\n10 10 m\n20 10 l\n20 20 l\nh\nB\n";

    private const double Eps = 1e-9;

    private static string I(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Pt(Point2 p) => "(" + F(p.X) + "," + F(p.Y) + ")";
    private static bool Near(double a, double b, double eps = Eps) => Math.Abs(a - b) <= eps;
    private static bool NearP(Point2 a, double x, double y, double eps = Eps) => Near(a.X, x, eps) && Near(a.Y, y, eps);
    private static byte[] L(string text) => Encoding.Latin1.GetBytes(text);

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }
        return result;
    }

    internal static byte[] Deflate(byte[] data, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, level, true)) zlib.Write(data, 0, data.Length);
        return buffer.ToArray();
    }

    internal static byte[] StreamBody(string entries, byte[] data) =>
        Concat(L("<< " + entries + (entries.Length > 0 ? " " : "") + "/Length " + I(data.Length) + " >>\nstream\n"), data, L("\nendstream"));

    // Assembles a PDF with a correct xref table; trailerEntries are added to the trailer dictionary.
    internal static byte[] Build(IEnumerable<(int Number, byte[] Body)> objects, string trailerEntries = "", bool withTrailer = true)
    {
        using var output = new MemoryStream();
        void W(string text) { var bytes = L(text); output.Write(bytes, 0, bytes.Length); }
        W("%PDF-1.7\n");
        var offsets = new SortedDictionary<int, long>();
        foreach (var (number, body) in objects)
        {
            offsets[number] = output.Position;
            W(I(number) + " 0 obj\n");
            output.Write(body, 0, body.Length);
            W("\nendobj\n");
        }
        if (withTrailer)
        {
            var xref = output.Position;
            var size = offsets.Count == 0 ? 1 : offsets.Keys.Max() + 1;
            W("xref\n0 " + I(size) + "\n0000000000 65535 f \n");
            for (var i = 1; i < size; i++) W(offsets.TryGetValue(i, out var at) ? at.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n" : "0000000000 65535 f \n");
            W("trailer\n<< /Size " + I(size) + " /Root 1 0 R" + (trailerEntries.Length > 0 ? " " + trailerEntries : "") + " >>\nstartxref\n" + I(xref) + "\n%%EOF\n");
        }
        return output.ToArray();
    }

    internal static List<(int Number, byte[] Body)> PageObjects(byte[] contentBody, string pageEntries = "") => new()
    {
        (1, L("<< /Type /Catalog /Pages 2 0 R >>")),
        (2, L("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")),
        (3, L("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 400] /Contents 4 0 R" + (pageEntries.Length > 0 ? " " + pageEntries : "") + " >>")),
        (4, contentBody),
    };

    internal static byte[] OnePage(string content, string pageEntries = "") => Build(PageObjects(StreamBody("", L(content)), pageEntries));

    private static string Prov(PdfProvenance p) => I(p.Page) + ":" + I(p.ObjectNumber) + ":" + I(p.OperatorIndex) + ":" + p.Operator;

    internal static string DescribeElement(PdfElement element) => element switch
    {
        PdfPath p => "path " + Prov(p.Provenance) + (p.Stroked ? " S" : "") + (p.Filled ? " F" : "") + (p.EvenOdd ? " *" : "") + " w" + F(p.LineWidth) + " "
            + string.Join("|", p.Subpaths.Select(s => Pt(s.Start) + (s.Closed ? "c" : "o") + string.Concat(s.Segments.Select(g => " " + g.Operator + I(g.OperatorIndex) + ":" + g.Kind + Pt(g.Control1) + Pt(g.Control2) + Pt(g.End))))),
        PdfText t => "text " + Prov(t.Provenance) + " " + t.Value + " " + t.FontName + " " + F(t.FontSize) + " " + F(t.Height) + " " + F(t.RotationDegrees) + Pt(t.Origin),
        _ => "other " + Prov(element.Provenance),
    };

    internal static string DescribeDocument(PdfDocument document) => string.Join("\n", document.Pages.Select(p => "page " + I(p.Number) + " o" + I(p.ObjectNumber) + "\n" + string.Join("\n", p.Elements.Select(DescribeElement))));

    private static string? CodeOf(Func<object?> action)
    {
        try { action(); return null; }
        catch (PdfFormatException error) { return error.Code; }
        catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
    }

    // Runs a read on the thread pool and gives up after milliseconds, so a hang is a failure instead of a stuck job.
    private static string? CodeWithin(Func<object?> action, int milliseconds)
    {
        var task = Task.Run(() => CodeOf(action));
        return task.Wait(milliseconds) ? task.Result : "timeout after " + I(milliseconds) + " ms";
    }

    private static bool ThrowsArgument(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
        catch (Exception) { return false; }
    }

    private static string Warnings(PdfDocument document) => string.Join(";", document.Warnings.Select(w => w.Code + "@" + I(w.Page) + ":" + I(w.ObjectNumber) + ":" + I(w.OperatorIndex)));

    public static void Run(Action<bool, string, string> check, string fixtures)
    {
        void C(bool condition, string name, string detail = "") => check(condition, "pdf " + name, detail);
        var simplePath = Path.Combine(fixtures, "simple-plan.pdf");
        var transformsPath = Path.Combine(fixtures, "transforms.pdf");
        var flatePath = Path.Combine(fixtures, "flate.pdf");

        // ---------- simple-plan.pdf: paths, rectangle, curve, closepath, text, provenance ----------
        try
        {
            var doc = PdfReader.ReadFile(simplePath);
            C(doc.SourceFile == "simple-plan.pdf" && doc.Version == "1.7" && doc.Pages.Count == 1 && doc.Warnings.Count == 0, "simple file, version, one page, no warnings", doc.SourceFile + " " + doc.Version + " " + Warnings(doc));
            var page = doc.Pages[0];
            C(page.Number == 1 && page.ObjectNumber == 3 && page.MediaBox == new PdfRect(0, 0, 595, 842) && page.Rotate == 0, "simple page number, object, media box", page.MediaBox.ToString());
            var order = string.Join(",", page.Elements.Select(e => e.Provenance.Operator + I(e.Provenance.OperatorIndex)));
            C(order == "S3,S6,S10,Tj15,Tj17,S21,B26", "simple elements in content order with painting/showing operator index", order);
            C(page.Elements.All(e => e.Provenance.SourceFile == "simple-plan.pdf" && e.Provenance.Page == 1 && e.Provenance.ObjectNumber == 4), "simple provenance file, page 1, content object 4");

            var line = page.Elements[0] as PdfPath;
            var seg = line?.Subpaths.SingleOrDefault()?.Segments.SingleOrDefault();
            C(line is not null && line.Stroked && !line.Filled && line.LineWidth == 0.5 && seg is not null && seg.Kind == PdfSegmentKind.Line && NearP(line.Subpaths[0].Start, 100, 100)
                && NearP(seg.Start, 100, 100) && NearP(seg.End, 300, 100) && seg.OperatorIndex == 2 && seg.Operator == "l" && !line.Subpaths[0].Closed,
                "simple stroked line 100,100-300,100 from l #2", line is null ? "null" : DescribeElement(line));

            var rect = page.Elements[2] as PdfPath;
            var rs = rect?.Subpaths.SingleOrDefault();
            C(rs is not null && rs.Closed && NearP(rs.Start, 50, 50) && rs.Segments.Count == 4 && rs.Segments.All(s => s.Operator == "re" && s.OperatorIndex == 9 && s.Kind == PdfSegmentKind.Line)
                && NearP(rs.Segments[0].End, 250, 50) && NearP(rs.Segments[1].End, 250, 150) && NearP(rs.Segments[2].End, 50, 150) && NearP(rs.Segments[3].End, 50, 50),
                "simple re under cm translate(50,50) inside q/Q is a closed 200x100 rectangle", rect is null ? "null" : DescribeElement(rect));

            var kitchen = page.Elements[3] as PdfText;
            C(kitchen is not null && kitchen.Value == "KITCHEN" && kitchen.FontName == "F1" && kitchen.FontSize == 12 && Near(kitchen.Height, 12) && Near(kitchen.RotationDegrees, 0) && NearP(kitchen.Origin, 120, 400),
                "simple Tj KITCHEN at Tm (120,400), size 12", kitchen is null ? "null" : DescribeElement(kitchen));
            var room = page.Elements[4] as PdfText;
            C(room is not null && room.Value == "Room (1)" && NearP(room.Origin, 120, 380) && room.Provenance.OperatorIndex == 17, "simple escaped parentheses and Td moves the line to (120,380)", room is null ? "null" : DescribeElement(room));

            var curve = page.Elements[5] as PdfPath;
            var cs = curve?.Subpaths.SingleOrDefault()?.Segments.SingleOrDefault();
            C(cs is not null && cs.Kind == PdfSegmentKind.Cubic && NearP(cs.Start, 400, 400) && NearP(cs.Control1, 400, 455.228) && NearP(cs.Control2, 444.772, 500) && NearP(cs.End, 500, 500) && cs.Operator == "c" && cs.OperatorIndex == 20,
                "simple c keeps Bezier control points", curve is null ? "null" : DescribeElement(curve));
            if (curve is not null)
            {
                var flat = curve.Subpaths[0].Flatten(4);
                var mx = 0.125 * 400 + 0.375 * 400 + 0.375 * 444.772 + 0.125 * 500;
                var my = 0.125 * 400 + 0.375 * 455.228 + 0.375 * 500 + 0.125 * 500;
                C(flat.Count == 5 && NearP(flat[0], 400, 400) && NearP(flat[2], mx, my, 1e-9) && NearP(flat[4], 500, 500), "simple Flatten(4) samples the cubic at t=0.5", string.Join(" ", flat.Select(Pt)));
                var fine = curve.Subpaths[0].Flatten(16);
                C(fine.Count == 17 && fine.All(p => Math.Abs((p - new Point2(500, 400)).Length - 100) < 0.03), "simple flattened door swing stays on the radius-100 quarter circle", string.Join(" ", fine.Select(Pt)));
            }

            var marker = page.Elements[6] as PdfPath;
            var ms = marker?.Subpaths.SingleOrDefault();
            C(marker is not null && marker.Stroked && marker.Filled && !marker.EvenOdd && ms is not null && ms.Closed && ms.Segments.Count == 3 && ms.Segments[2].Operator == "h" && ms.Segments[2].OperatorIndex == 25
                && NearP(ms.Segments[2].Start, 20, 20) && NearP(ms.Segments[2].End, 10, 10), "simple h appends the closing segment; B fills and strokes", marker is null ? "null" : DescribeElement(marker));

            // Determinism: repeated and de-DE parses describe identically.
            var described = DescribeDocument(doc);
            var culture = CultureInfo.CurrentCulture;
            string describedDe;
            try { CultureInfo.CurrentCulture = new CultureInfo("de-DE"); describedDe = DescribeDocument(PdfReader.ReadFile(simplePath)); }
            finally { CultureInfo.CurrentCulture = culture; }
            C(described == describedDe && described == DescribeDocument(PdfReader.Parse(File.ReadAllBytes(simplePath), "simple-plan.pdf")), "simple deterministic across repeat and de-DE culture");

            // FlateDecode: the checked-in compressed fixture and PDFs built here (plain and ZLibStream) read the same.
            var flate = PdfReader.ReadFile(flatePath);
            var builtPlain = PdfReader.Parse(Build(PageObjects(StreamBody("", L(SimpleContent)))), "built.pdf");
            var builtFlate = PdfReader.Parse(Build(PageObjects(StreamBody("/Filter /FlateDecode", Deflate(L(SimpleContent)))).ToList()), "built-flate.pdf");
            var builtArray = PdfReader.Parse(Build(PageObjects(StreamBody("/Filter [/FlateDecode]", Deflate(L(SimpleContent)))).ToList()), "built-array.pdf");
            C(flate.Warnings.Count == 0 && DescribeDocument(flate) == described, "flate fixture reads the same as simple-plan", Warnings(flate) + " | " + DescribeDocument(flate));
            C(DescribeDocument(builtPlain) == described && DescribeDocument(builtFlate) == described && DescribeDocument(builtArray) == described && builtFlate.Warnings.Count == 0,
                "ZLibStream-compressed content (name and array filter) reads the same as plain", Warnings(builtFlate) + " | " + DescribeDocument(builtFlate));
            C(flate.Pages[0].Elements.All(e => e.Provenance.SourceFile == "flate.pdf"), "flate provenance names its own file");

            // ---------- calibration and alignment ----------
            var cal = PdfCalibration.FromKnownDimension(new Point2(100, 100), new Point2(300, 100), 5000);
            C(Near(cal.MillimetresPerUnit, 25) && NearP(cal.Apply(new Point2(300, 100)), 7500, 2500) && Near(cal.RotationDegrees, 0), "calibration 200 units = 5000 mm gives 25 mm per unit", cal.ToString());
            C(Near(PdfCalibration.FromKnownDimension(new Point2(0, 0), new Point2(3, 4), 1000).MillimetresPerUnit, 200), "calibration on a diagonal dimension uses Euclidean length");
            if (rs is not null)
            {
                var width = (cal.Apply(rs.Segments[0].End) - cal.Apply(rs.Start)).Length;
                var height = (cal.Apply(rs.Segments[1].End) - cal.Apply(rs.Segments[0].End)).Length;
                C(Near(width, 5000, 1e-9) && Near(height, 2500, 1e-9), "calibrated room outline is 5000 x 2500 mm", F(width) + " x " + F(height));
            }
            var aligned = cal.AlignTo(new Point2(100, 100), new Point2(100, 300));
            C(NearP(aligned.Apply(new Point2(100, 100)), 0, 0) && NearP(aligned.Apply(new Point2(100, 300)), 5000, 0) && NearP(aligned.Apply(new Point2(0, 100)), 0, 2500)
                && Near(aligned.RotationDegrees, 270) && Near(aligned.MillimetresPerUnit, 25),
                "alignment maps a vertical reference segment onto +X from the origin", Pt(aligned.Apply(new Point2(100, 300))) + " " + Pt(aligned.Apply(new Point2(0, 100))) + " " + F(aligned.RotationDegrees));
            var placed = cal.AlignTo(new Point2(100, 100), new Point2(300, 100), 90, new Point2(1000, 2000));
            C(NearP(placed.Apply(new Point2(100, 100)), 1000, 2000) && NearP(placed.Apply(new Point2(300, 100)), 1000, 7000) && Near(placed.RotationDegrees, 90),
                "alignment to angle 90 and origin (1000,2000)", Pt(placed.Apply(new Point2(300, 100))));
            C(ThrowsArgument(() => PdfCalibration.FromKnownDimension(new Point2(1, 1), new Point2(1, 1), 1000))
                && ThrowsArgument(() => PdfCalibration.FromKnownDimension(new Point2(0, 0), new Point2(1, 0), 0))
                && ThrowsArgument(() => PdfCalibration.FromKnownDimension(new Point2(0, 0), new Point2(1, 0), -5))
                && ThrowsArgument(() => PdfCalibration.FromKnownDimension(new Point2(0, 0), new Point2(1, 0), double.NaN))
                && ThrowsArgument(() => PdfCalibration.FromKnownDimension(new Point2(0, 0), new Point2(double.PositiveInfinity, 0), 10))
                && ThrowsArgument(() => cal.AlignTo(new Point2(2, 2), new Point2(2, 2)))
                && ThrowsArgument(() => cal.AlignTo(new Point2(0, 0), new Point2(1, 0), double.NaN)),
                "calibration refuses coincident points, non-positive or non-finite lengths and a degenerate reference");

            // ---------- conversion to the DXF drawing model and wall pairs ----------
            var entities = page.ToDxfEntities(cal);
            var first = entities.Count > 0 ? entities[0] as DxfLine : null;
            C(entities.Count == 7 && first is not null && NearP(first.Start, 2500, 2500) && NearP(first.End, 7500, 2500) && first.Provenance.Handle == "p1:o4:i3" && first.Provenance.SourceFile == "simple-plan.pdf"
                && first.Provenance.EntityType == "PDF-PATH" && first.Provenance.Layer == "PDF-P1" && first.Provenance.Line == 4,
                "ToDxfEntities: calibrated line keeps PDF provenance in the DXF model", string.Join(";", entities.Select(e => e.GetType().Name + " " + e.Provenance.Handle)));
            C(entities.Count == 7 && entities[2] is DxfPolyline rp && rp.Closed && rp.Vertices.Count == 4 && NearP(rp.Vertices[0].Point, 1250, 1250) && rp.Vertices.All(v => v.Bulge == 0),
                "ToDxfEntities: rectangle becomes a closed 4-vertex polyline");
            C(entities.Count == 7 && entities[3] is DxfText dt && dt.Value == "KITCHEN" && Near(dt.Height, 300) && NearP(dt.Insertion, 3000, 10000) && dt.Provenance.EntityType == "PDF-TEXT" && dt.Provenance.Handle == "p1:o4:i15",
                "ToDxfEntities: text height and insertion in millimetres");
            C(entities.Count == 7 && entities[5] is DxfPolyline cp && !cp.Closed && cp.Vertices.Count == 17 && entities[6] is DxfPolyline tp && tp.Closed && tp.Vertices.Count == 3,
                "ToDxfEntities: curve flattened to 16 segments, closed triangle has 3 vertices");
            var pairs = WallPairDetector.Detect(entities, WallPairOptions.FromMetres(0.1, 0.3, 0.5, 0.001));
            C(pairs.Count == 1 && pairs[0].A.Source.Handle == "p1:o4:i3" && pairs[0].B.Source.Handle == "p1:o4:i6" && Near(pairs[0].Thickness, 200, 1e-9) && Near(pairs[0].Overlap, 5000, 1e-9),
                "calibrated PDF lines 8 units apart are a 200 mm wall pair", string.Join(";", pairs.Select(p => p.A.Source.Handle + "/" + p.B.Source.Handle + " " + F(p.Thickness))));
        }
        catch (Exception error)
        {
            check(false, "pdf simple-plan unexpected " + error.GetType().Name + ": " + error.Message, "");
        }

        // ---------- transforms.pdf: cm, nested q/Q, Form XObject, two content streams, inherited page attributes ----------
        try
        {
            var doc = PdfReader.ReadFile(transformsPath);
            C(doc.Pages.Count == 2, "transforms two pages", I(doc.Pages.Count));
            var p1 = doc.Pages[0];
            var order = string.Join(",", p1.Elements.Select(e => I(e.Provenance.ObjectNumber) + ":" + e.Provenance.Operator + I(e.Provenance.OperatorIndex)));
            C(p1.ObjectNumber == 3 && p1.MediaBox == new PdfRect(0, 0, 400, 300) && order == "4:S5,4:S10,4:S15,4:TJ18,4:Tj19,7:S2,5:f2",
                "transforms page 1 elements with object number per content stream and form", order);
            PdfSegment? Seg(int index) => p1.Elements.Count > index && p1.Elements[index] is PdfPath path && path.Subpaths.Count == 1 && path.Subpaths[0].Segments.Count >= 1 ? path.Subpaths[0].Segments[0] : null;
            C(Seg(0) is { } s0 && NearP(s0.Start, 10, 20) && NearP(s0.End, 10, 40), "transforms rotate 90 inside scale 2 + translate (10,20)", Seg(0)?.ToString() ?? "null");
            C(Seg(1) is { } s1 && NearP(s1.Start, 10, 30) && NearP(s1.End, 8, 32), "transforms nested q: translate then rotate then scale", Seg(1)?.ToString() ?? "null");
            C(Seg(2) is { } s2 && NearP(s2.Start, 10, 20) && NearP(s2.End, 30, 20), "transforms Q restores the outer CTM", Seg(2)?.ToString() ?? "null");
            C(p1.Elements.Count > 4 && p1.Elements[3] is PdfText ab && ab.Value == "AB" && NearP(ab.Origin, 10, 20) && Near(ab.Height, 20) && ab.FontSize == 10 && Near(ab.RotationDegrees, 0)
                && p1.Elements[4] is PdfText hello && hello.Value == "Hello" && NearP(hello.Origin, 10, 20),
                "transforms TJ joins strings; hex string; text height scales with CTM", p1.Elements.Count > 4 ? DescribeElement(p1.Elements[3]) + " / " + DescribeElement(p1.Elements[4]) : "");
            C(Seg(5) is { } s5 && NearP(s5.Start, 210, 20) && NearP(s5.End, 210, 40) && s5.OperatorIndex == 1 && p1.Elements[5].Provenance.ObjectNumber == 7,
                "transforms Form XObject /Matrix composed with the page CTM; provenance names the form object", Seg(5)?.ToString() ?? "null");
            C(p1.Elements.Count > 6 && p1.Elements[6] is PdfPath filled && filled.Filled && !filled.Stroked && filled.Subpaths[0].Closed && NearP(filled.Subpaths[0].Start, 10, 20)
                && NearP(filled.Subpaths[0].Segments[1].End, 30, 40), "transforms second content stream keeps the graphics state of the first", p1.Elements.Count > 6 ? DescribeElement(p1.Elements[6]) : "");
            C(Warnings(doc) == "unbalanced-restore@1:4:21", "transforms unbalanced Q warned with provenance", Warnings(doc));
            var p2 = doc.Pages[1];
            C(p2.Number == 2 && p2.ObjectNumber == 8 && p2.MediaBox == new PdfRect(0, 0, 200, 100) && p2.Rotate == 90, "transforms page 2 inherits the media box and has /Rotate 90", p2.MediaBox + " " + I(p2.Rotate));
            C(p2.Elements.Count == 1 && p2.Elements[0] is PdfText up && up.Value == "UP" && NearP(up.Origin, 50, 60) && Near(up.RotationDegrees, 90) && Near(up.Height, 8) && up.Provenance.ObjectNumber == 9 && up.Provenance.OperatorIndex == 5,
                "transforms rotated text from Tm, stream with an indirect /Length read through the endstream fallback", string.Join(" / ", p2.Elements.Select(DescribeElement)));
        }
        catch (Exception error)
        {
            check(false, "pdf transforms unexpected " + error.GetType().Name + ": " + error.Message, "");
        }

        // ---------- operators built here ----------
        try
        {
            var text = PdfReader.Parse(OnePage("BT /F2 10 Tf 12 TL 1 0 0 1 100 200 Tm (a) Tj (b) ' 2 1 (c) \" 0 -30 TD (d) Tj T* (e) Tj 5 Ts (f) Tj ET"), "text.pdf");
            var t = text.Pages[0].Elements.OfType<PdfText>().ToList();
            C(t.Count == 6 && string.Concat(t.Select(x => x.Value)) == "abcdef" && string.Join(",", t.Select(x => x.Provenance.Operator)) == "Tj,',\",Tj,Tj,Tj"
                && NearP(t[0].Origin, 100, 200) && NearP(t[1].Origin, 100, 188) && NearP(t[2].Origin, 100, 176) && NearP(t[3].Origin, 100, 146) && NearP(t[4].Origin, 100, 116) && NearP(t[5].Origin, 100, 121),
                "text ' and \" use leading, TD sets leading, T* and Ts rise", string.Join(" / ", t.Select(DescribeElement)));
            var strings = PdfReader.Parse(OnePage("BT /F1 1 Tf (a\\\\b\\nc\\101) Tj <414> Tj <FEFF00480069> Tj (x\\\ny) Tj ET"), "strings.pdf").Pages[0].Elements.OfType<PdfText>().Select(x => x.Value).ToList();
            C(strings.Count == 4 && strings[0] == "a\\b\nc" + "A" && strings[1] == "A@" && strings[2] == "Hi" && strings[3] == "xy", "string escapes, octal, odd hex digit, UTF-16BE, line continuation", string.Join("|", strings));

            var curves = PdfReader.Parse(OnePage("0 0 m 10 10 20 0 v S 0 0 m 5 5 20 0 y S"), "curves.pdf").Pages[0].Elements.OfType<PdfPath>().Select(p => p.Subpaths[0].Segments[0]).ToList();
            C(curves.Count == 2 && curves[0].Operator == "v" && NearP(curves[0].Control1, 0, 0) && NearP(curves[0].Control2, 10, 10) && NearP(curves[0].End, 20, 0)
                && curves[1].Operator == "y" && NearP(curves[1].Control1, 5, 5) && NearP(curves[1].Control2, 20, 0), "v and y Bezier control-point rules", string.Join(" / ", curves));

            var paint = PdfReader.Parse(OnePage("0 0 m 1 0 l 1 1 l s 0 0 m 2 0 l 2 2 l f* 0 0 5 5 re W n 0 0 m 3 0 l n 0 0 m 4 0 l 4 4 l b 0 0 m 9 0 l 0 5 m 0 9 l S"), "paint.pdf").Pages[0].Elements.OfType<PdfPath>().ToList();
            C(paint.Count == 4 && paint[0].Stroked && paint[0].Subpaths[0].Closed && paint[0].Subpaths[0].Segments[2].Operator == "s"
                && paint[1].Filled && paint[1].EvenOdd && !paint[1].Stroked && paint[2].Filled && paint[2].Stroked && paint[2].Subpaths[0].Closed && paint[3].Subpaths.Count == 2,
                "s closes, f* even-odd, W n and n paint nothing, b closes, two subpaths", string.Join(" / ", paint.Select(DescribeElement)));

            var inline = PdfReader.Parse(OnePage("BI /W 2 /H 1 /BPC 8 /CS /G ID \u0000ÿEI\u0001 EI 0 0 m 5 0 l S"), "inline.pdf");
            C(inline.Pages[0].Elements.Count == 1 && inline.Pages[0].Elements[0].Provenance.OperatorIndex == 3 && inline.Warnings.Count == 0, "inline image data skipped up to a delimited EI", Warnings(inline));

            var warned = PdfReader.Parse(OnePage("5 l 3 4 l S (x) Tj 0 0 m 5 0 l S Q"), "warned.pdf");
            C(warned.Pages[0].Elements.Count == 1 && Warnings(warned) == "operand-error@1:4:0;no-current-point@1:4:1;text-outside-bt@1:4:3;unbalanced-restore@1:4:7",
                "operand error, no current point, text outside BT and unbalanced Q are warnings with provenance", Warnings(warned));

            var objStm = Concat(L("2 0 3 47 "), L("<< /Type /Pages /Kids [3 0 R] /Count 1 >>      "), L("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 50 50] /Contents 4 0 R >>"));
            var first = 9;
            var compressed = PdfReader.Parse(Build(new List<(int, byte[])>
            {
                (1, L("<< /Type /Catalog /Pages 2 0 R >>")),
                (4, StreamBody("", L("0 0 m 10 0 l S"))),
                (6, StreamBody("/Type /ObjStm /N 2 /First " + I(first) + " /Filter /FlateDecode", Deflate(objStm))),
            }), "objstm.pdf");
            C(compressed.Pages.Count == 1 && compressed.Pages[0].ObjectNumber == 3 && compressed.Pages[0].MediaBox == new PdfRect(0, 0, 50, 50) && compressed.Pages[0].Elements.Count == 1,
                "page tree inside a compressed object stream", Warnings(compressed) + " " + DescribeDocument(compressed));

            var noTrailer = PdfReader.Parse(Build(PageObjects(StreamBody("", L("0 0 m 1 0 l S"))), withTrailer: false), "notrailer.pdf");
            C(noTrailer.Pages.Count == 1 && noTrailer.Pages[0].Elements.Count == 1, "catalog found by /Type without a trailer");

            var objs = PageObjects(StreamBody("", L("0 0 m 1 0 l S")));
            objs[1] = (2, L("<< /Type /Pages /Kids [3 0 R 2 0 R] /Count 1 >>"));
            var cycle = PdfReader.Parse(Build(objs), "cycle.pdf");
            C(cycle.Pages.Count == 1 && cycle.Warnings.Any(w => w.Code == "page-tree-cycle"), "page-tree cycle warned, page still read", Warnings(cycle));

            var unsupported = PdfReader.Parse(Build(PageObjects(StreamBody("/Filter /LZWDecode", L("0 0 m 1 0 l S")))), "lzw.pdf");
            C(unsupported.Pages[0].Elements.Count == 0 && unsupported.Warnings.Any(w => w.Code == "unsupported-filter" && w.ObjectNumber == 4), "unsupported filter warned", Warnings(unsupported));
            var corrupt = PdfReader.Parse(Build(PageObjects(StreamBody("/Filter /FlateDecode", L("this is not zlib data")))), "corrupt.pdf");
            C(corrupt.Pages[0].Elements.Count == 0 && corrupt.Warnings.Any(w => w.Code == "stream-decode-failed" && w.ObjectNumber == 4), "corrupt flate stream warned", Warnings(corrupt));
            var unterminated = PdfReader.Parse(OnePage("0 0 m 5 0 l S (abc"), "unterminated.pdf");
            C(unterminated.Pages[0].Elements.Count == 1 && unterminated.Warnings.Any(w => w.Code == "content-malformed"), "unterminated content string warned after earlier elements", Warnings(unterminated));

            var selfForm = PdfReader.Parse(Build(PageObjects(StreamBody("", L("/X Do")), "/Resources << /XObject << /X 5 0 R >> >>").Append(
                (5, StreamBody("/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /X 5 0 R >> >>", L("0 0 m 1 0 l S /X Do"))))), "selfform.pdf");
            C(selfForm.Pages[0].Elements.Count == 1 && selfForm.Warnings.Any(w => w.Code == "xobject-cycle"), "self-referencing Form XObject warned, drawn once", Warnings(selfForm));
        }
        catch (Exception error)
        {
            check(false, "pdf operators unexpected " + error.GetType().Name + ": " + error.Message, "");
        }

        // ---------- review blockers and should-fix items ----------
        try
        {
            // B1: malformed inline images are content-malformed warnings, never an internal exception.
            foreach (var content in new[] { "BI /W (abc", "BI <zz> ID", "BI " + new string('A', 300) })
            {
                PdfDocument? read = null;
                var code = CodeOf(() => read = PdfReader.Parse(OnePage(content), "bi.pdf"));
                C(code is null && read is not null && read.Warnings.Any(w => w.Code == "content-malformed"), "B1 malformed inline image warned: " + (content.Length > 20 ? content[..20] : content),
                    (code ?? "") + " " + (read is null ? "" : Warnings(read)));
            }

            // B2: many failing trailers are each parsed at most once.
            var trailers = Concat(L("%PDF-1.7\n"), L(string.Concat(Enumerable.Repeat("trailer (", 200_000))));
            var trailerCode = CodeWithin(() => PdfReader.Parse(trailers, "trailers.pdf"), 10_000);
            C(trailerCode == "no-pages", "B2 200000 unterminated trailers refused within the time bound", trailerCode ?? "null");

            // B3: object-stream entries are bounded by the next offset; non-increasing offsets are refused.
            byte[] ObjStmDocument(bool sameOffset, bool terminated)
            {
                var header = new StringBuilder();
                for (var k = 0; k < 100_000; k++) header.Append(I(1000 + k)).Append(' ').Append(I(sameOffset ? 0 : k)).Append(' ');
                var head = L(header.ToString());
                var body = new byte[10_000_000];
                Array.Fill(body, (byte)'a');
                body[0] = (byte)'(';
                if (terminated) body[^1] = (byte)')';
                var objs = PageObjects(StreamBody("", L("0 0 m 1 0 l S")));
                objs.Add((6, StreamBody("/Type /ObjStm /N 100000 /First " + I(head.Length) + " /Filter /FlateDecode", Deflate(Concat(head, body), CompressionLevel.Fastest))));
                return Build(objs);
            }
            foreach (var (sameOffset, terminated) in new[] { (true, false), (true, true), (false, false), (false, true) })
            {
                var input = ObjStmDocument(sameOffset, terminated);
                PdfDocument? read = null;
                var code = CodeWithin(() => read = PdfReader.Parse(input, "objstm-bomb.pdf"), 15_000);
                C(code is null && read is not null && read.Pages.Count == 1 && read.Pages[0].Elements.Count == 1 && (!sameOffset || read.Warnings.Any(w => w.Code == "objstm-malformed" && w.ObjectNumber == 6)),
                    "B3 object stream with 100000 entries over a 10 MB literal (same offset " + sameOffset + ", terminated " + terminated + ") read within the time bound", code ?? "ok");
            }

            // B4: Form XObject reuse is charged to the interpreted-bytes budget.
            var zeros = new byte[100_000_000];
            for (var k = 0; k < zeros.Length; k++) zeros[k] = (byte)(k % 2 == 0 ? '0' : ' ');
            var formObjects = PageObjects(StreamBody("", L(string.Concat(Enumerable.Repeat("/X Do\n", 100_000)))), "/Resources << /XObject << /X 5 0 R >> >>");
            formObjects.Add((5, StreamBody("/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Filter /FlateDecode", Deflate(zeros, CompressionLevel.Fastest))));
            var zeroInput = Build(formObjects);
            zeros = Array.Empty<byte>();
            var zeroCode = CodeWithin(() => PdfReader.Parse(zeroInput, "form-zeros.pdf"), 30_000);
            C(zeroCode == "interpret-limit", "B4 a 100 MB operand-only form invoked 100000 times refused as interpret-limit", zeroCode ?? "null");
            var bigText = Concat(L("BT /F1 1 Tf ("), Enumerable.Repeat((byte)'a', 10_000_000).ToArray(), L(") Tj ET"));
            var textObjects = PageObjects(StreamBody("", L(string.Concat(Enumerable.Repeat("/X Do\n", 1000)))), "/Resources << /XObject << /X 5 0 R >> >>");
            textObjects.Add((5, StreamBody("/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Filter /FlateDecode", Deflate(bigText, CompressionLevel.Fastest))));
            var textInput = Build(textObjects);
            var textCode = CodeWithin(() => PdfReader.Parse(textInput, "form-text.pdf"), 30_000);
            C(textCode == "interpret-limit", "B4 a form showing a 10 MB string invoked 1000 times refused as interpret-limit", textCode ?? "null");

            // S2: a /Contents array is one concatenated stream.
            var split = PdfReader.Parse(Build(new List<(int, byte[])>
            {
                (1, L("<< /Type /Catalog /Pages 2 0 R >>")),
                (2, L("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")),
                (3, L("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 400] /Contents [4 0 R 5 0 R] >>")),
                (4, StreamBody("", L("0 0 m 10 0 l"))),
                (5, StreamBody("", L("S"))),
            }), "split.pdf");
            C(split.Pages[0].Elements.Count == 1 && split.Pages[0].Elements[0] is PdfPath splitPath && splitPath.Provenance.ObjectNumber == 5 && splitPath.Provenance.OperatorIndex == 0
                && splitPath.Subpaths[0].Segments[0].OperatorIndex == 1 && NearP(splitPath.Subpaths[0].Segments[0].End, 10, 0) && split.Warnings.Count == 0,
                "S2 path started in content stream 4 is painted by S in stream 5", Warnings(split) + " " + DescribeDocument(split));

            // S3: two draws of one form are distinguishable.
            var twice = PdfReader.Parse(Build(PageObjects(StreamBody("", L("/X Do 1 0 0 1 50 0 cm /X Do")), "/Resources << /XObject << /X 5 0 R >> >>").Append(
                (5, StreamBody("/Type /XObject /Subtype /Form /BBox [0 0 1 1]", L("0 0 m 10 0 l S"))))), "twice.pdf");
            var twiceEntities = twice.Pages[0].ToDxfEntities();
            C(twiceEntities.Count == 2 && twiceEntities[0] is DxfLine l0 && twiceEntities[1] is DxfLine l1 && NearP(l1.Start, 50, 0) && NearP(l1.End, 60, 0)
                && l0.Provenance.Handle == "p1:o5:i2@o4:i0" && l1.Provenance.Handle == "p1:o5:i2@o4:i2"
                && string.Join(">", l0.Provenance.InsertPath) == "o4:i0" && string.Join(">", l1.Provenance.InsertPath) == "o4:i2",
                "S3 repeated Form XObject draws carry the invoking Do in handle and InsertPath", string.Join(" ; ", twiceEntities.Select(e => e.Provenance.Handle + " [" + string.Join(">", e.Provenance.InsertPath) + "]")));

            // S1: incremental updates, the latest occurrence in the file wins (direct over direct, ObjStm over direct, direct over ObjStm).
            var revision1 = Build(PageObjects(StreamBody("", L("0 0 m 10 0 l S"))));
            var revision2 = Concat(revision1, L("4 0 obj\n"), StreamBody("", L("0 0 m 20 0 l S")), L("\nendobj\ntrailer\n<< /Size 5 /Root 1 0 R >>\n%%EOF\n"));
            var updated = PdfReader.Parse(revision2, "update.pdf");
            C(updated.Pages[0].Elements.Count == 1 && updated.Pages[0].Elements[0] is PdfPath up1 && NearP(up1.Subpaths[0].Segments[0].End, 20, 0), "S1 a later direct object replaces an earlier one", DescribeDocument(updated));
            var newerPage = L("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 70 70] /Contents 4 0 R >>");
            var revision3 = Concat(revision1, L("7 0 obj\n"), StreamBody("/Type /ObjStm /N 1 /First 4", Concat(L("3 0 "), newerPage)), L("\nendobj\ntrailer\n<< /Size 8 /Root 1 0 R >>\n%%EOF\n"));
            var fromStream = PdfReader.Parse(revision3, "update-objstm.pdf");
            C(fromStream.Pages[0].MediaBox == new PdfRect(0, 0, 70, 70), "S1 an object in a later object stream replaces an earlier direct object", fromStream.Pages[0].MediaBox.ToString());
            var revision4 = Concat(revision3, L("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 80] /Contents 4 0 R >>\nendobj\ntrailer\n<< /Size 8 /Root 1 0 R >>\n%%EOF\n"));
            var direct = PdfReader.Parse(revision4, "update-direct.pdf");
            C(direct.Pages[0].MediaBox == new PdfRect(0, 0, 80, 80), "S1 a later direct object replaces an object in an earlier object stream", direct.Pages[0].MediaBox.ToString());

            // N3: a TJ adjustment below -200 reads as a word space.
            var spaced = PdfReader.Parse(OnePage("BT /F1 10 Tf [(A) -250 (B) -100 (C)] TJ ET"), "tj.pdf").Pages[0].Elements.OfType<PdfText>().Select(t => t.Value).ToList();
            C(spaced.Count == 1 && spaced[0] == "A BC", "N3 TJ adjustment -250 inserts a space, -100 does not", string.Join("|", spaced));

            // Re-review blocker 1: Do must not copy the pending path; a path built across a Do is still painted.
            var pending = string.Concat(Enumerable.Repeat("0 0 m 1 1 l ", 300_000)) + string.Concat(Enumerable.Repeat("/X Do ", 1_000_000));
            var emptyForm = (5, StreamBody("/Type /XObject /Subtype /Form /BBox [0 0 1 1]", Array.Empty<byte>()));
            var pendingInput = Build(PageObjects(StreamBody("", L(pending)), "/Resources << /XObject << /X 5 0 R >> >>").Append(emptyForm));
            var pendingCode = CodeWithin(() => PdfReader.Parse(pendingInput, "pending.pdf"), 10_000);
            C(pendingCode is null or "operator-limit" or "interpret-limit", "RB1 300000 pending subpaths and 1000000 Do of an empty form finish within the time bound", pendingCode ?? "ok");
            var across = PdfReader.Parse(Build(PageObjects(StreamBody("", L("0 0 m 1 1 l /X Do 2 0 l S")), "/Resources << /XObject << /X 5 0 R >> >>").Append(
                (5, StreamBody("/Type /XObject /Subtype /Form /BBox [0 0 1 1]", L("5 5 m 6 6 l S"))))), "across.pdf");
            var acrossPaths = across.Pages[0].Elements.OfType<PdfPath>().ToList();
            C(acrossPaths.Count == 2 && acrossPaths[0].Provenance.ObjectNumber == 5 && acrossPaths[1].Provenance.ObjectNumber == 4 && acrossPaths[1].Subpaths.Count == 1
                && acrossPaths[1].Subpaths[0].Segments.Count == 2 && NearP(acrossPaths[1].Subpaths[0].Segments[1].End, 2, 0),
                "RB1 path built across a Do is painted after the form's own path", DescribeDocument(across));

            // Re-review blocker 2: shared direct /Kids dictionaries are walked once (2^31 paths otherwise).
            var tree = new List<(int, byte[])> { (1, L("<< /Type /Catalog /Pages 2 0 R >>")), (2, L("<< /Type /Pages /Kids 10 0 R >>")) };
            for (var level = 0; level <= 30; level++) tree.Add((10 + level, L("[<< /Kids " + I(11 + level) + " 0 R >> << /Kids " + I(11 + level) + " 0 R >>]")));
            tree.Add((41, L("[]")));
            var treeInput = Build(tree);
            var treeCode = CodeWithin(() => PdfReader.Parse(treeInput, "tree.pdf"), 10_000);
            C(treeCode is "no-pages" or "depth-limit" or "page-limit" or "object-limit", "RB2 31 levels of shared direct /Kids refused quickly", treeCode ?? "null");

            // Should-fix 1: a parsed-element budget refuses huge name arrays in content and in object streams.
            var names = Concat(L("["), L(string.Concat(Enumerable.Repeat("/a", 10_000_000))), L("] 0 0 m"));
            var namesInput = Build(PageObjects(StreamBody("/Filter /FlateDecode", Deflate(names, CompressionLevel.Fastest))));
            var namesCode = CodeWithin(() => PdfReader.Parse(namesInput, "names.pdf"), 20_000);
            C(namesCode == "element-limit", "SF1 a 20 MB name array in a content stream refused as element-limit", namesCode ?? "null");
            var namesStream = PageObjects(StreamBody("", L("0 0 m 1 0 l S")));
            namesStream.Add((6, StreamBody("/Type /ObjStm /N 1 /First 4 /Filter /FlateDecode", Deflate(Concat(L("9 0 "), names), CompressionLevel.Fastest))));
            var namesStreamInput = Build(namesStream);
            var namesStreamCode = CodeWithin(() => PdfReader.Parse(namesStreamInput, "names-objstm.pdf"), 20_000);
            C(namesStreamCode == "element-limit", "SF1 a 20 MB name array inside an object stream refused as element-limit", namesStreamCode ?? "null");

            // Should-fix 2: the name length limit counts #xx escapes as written.
            var escaped = PdfReader.Parse(OnePage("BT /" + string.Concat(Enumerable.Repeat("#41", 100)) + " 12 Tf ET"), "escaped.pdf");
            C(escaped.Warnings.Any(w => w.Code == "content-malformed"), "SF2 a name of 100 #xx escapes (300 bytes) is malformed", Warnings(escaped));

            // Should-fix 3: a malformed trailer does not swallow a later revision.
            var damaged = Concat(revision1, L("trailer\n<< /Size (\n4 0 obj\n"), StreamBody("", L("0 0 m 20 0 l S")), L("\nendobj\ntrailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n0\n%%EOF\n"));
            var afterDamage = PdfReader.Parse(damaged, "damaged-trailer.pdf");
            C(afterDamage.Pages.Count == 1 && afterDamage.Pages[0].Elements.Count == 1 && afterDamage.Pages[0].Elements[0] is PdfPath dp && NearP(dp.Subpaths[0].Segments[0].End, 20, 0)
                && afterDamage.Warnings.Any(w => w.Code == "trailer-malformed"), "SF3 revision after a malformed trailer is still read", Warnings(afterDamage) + " " + DescribeDocument(afterDamage));

            // Nit: huge text matrices give a finite height or a warning, never an infinite height.
            var e63 = "1" + new string('0', 63);
            var hugeText = PdfReader.Parse(OnePage(e63 + " 0 0 " + e63 + " 0 0 cm " + e63 + " 0 0 " + e63 + " 0 0 cm BT /F1 12 Tf " + e63 + " 0 0 " + e63 + " 0 0 Tm (H) Tj ET"), "huge-text.pdf");
            var hugeHeights = hugeText.Pages[0].Elements.OfType<PdfText>().Select(t => t.Height).ToList();
            C(hugeHeights.Count == 1 && double.IsFinite(hugeHeights[0]) && Math.Abs(hugeHeights[0] / 1.2e190 - 1) < 1e-9, "NIT text at scale 1e189 has a finite height", string.Join(",", hugeHeights.Select(F)));
            var infiniteText = PdfReader.Parse(OnePage(e63 + " 0 0 " + e63 + " 0 0 cm " + e63 + " 0 0 " + e63 + " 0 0 cm " + e63 + " 0 0 " + e63 + " 0 0 cm BT /F1 " + e63 + " Tf " + e63 + " 0 0 " + e63 + " 0 0 Tm (H) Tj ET"), "inf-text.pdf");
            C(!infiniteText.Pages[0].Elements.OfType<PdfText>().Any() && infiniteText.Warnings.Any(w => w.Code == "operand-error"), "NIT text whose height overflows is skipped with a warning", Warnings(infiniteText));
        }
        catch (Exception error)
        {
            check(false, "pdf review unexpected " + error.GetType().Name + ": " + error.Message, "");
        }

        // ---------- refusals and bounds ----------
        try
        {
            C(CodeOf(() => PdfReader.Parse(Array.Empty<byte>(), "e.pdf")) == "empty", "empty input refused");
            C(CodeOf(() => PdfReader.Parse(L("hello world, not a pdf"), "n.pdf")) == "not-pdf", "missing %PDF- header refused");
            C(CodeOf(() => PdfReader.Parse(OnePage("0 0 m"), "big.pdf", new PdfReadOptions(MaxBytes: 20))) == "too-large", "MaxBytes refused as too-large");
            C(CodeOf(() => PdfReader.Parse(L("%PDF-1.4\n1 0 obj\n<< /Type /Foo >>\nendobj\n"), "np.pdf")) == "no-pages", "no catalog refused as no-pages");
            C(CodeOf(() => PdfReader.Parse(Build(PageObjects(StreamBody("", L("0 0 m"))), "/Encrypt 9 0 R"), "enc.pdf")) == "encrypted", "encrypted document refused");
            var many = PageObjects(StreamBody("", L("0 0 m")));
            for (var i = 5; i < 60; i++) many.Add((i, L("<< >>")));
            C(CodeOf(() => PdfReader.Parse(Build(many), "many.pdf", new PdfReadOptions(MaxObjects: 20))) == "object-limit", "MaxObjects refused as object-limit");
            C(CodeOf(() => PdfReader.Parse(OnePage(string.Concat(Enumerable.Repeat("0 0 m ", 1000))), "ops.pdf", new PdfReadOptions(MaxOperators: 100))) == "operator-limit", "MaxOperators refused as operator-limit");
            C(CodeOf(() => PdfReader.Parse(OnePage(string.Concat(Enumerable.Repeat("q ", 40))), "q.pdf")) == "depth-limit", "q nesting beyond MaxDepth refused");
            C(CodeOf(() => PdfReader.Parse(OnePage(new string('[', 100) + new string(']', 100) + " pop"), "arr.pdf")) == "depth-limit", "content array nesting beyond MaxDepth refused");
            C(CodeOf(() => PdfReader.Parse(Build(PageObjects(StreamBody("", L("0 0 m"))).Append((5, L(new string('[', 200) + new string(']', 200))))), "deep.pdf")) == "depth-limit", "object nesting beyond MaxDepth refused");
            var chain = PageObjects(StreamBody("", L("/X Do")), "/Resources << /XObject << /X 5 0 R >> >>");
            for (var i = 5; i <= 10; i++) chain.Add((i, StreamBody("/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /X " + I(i + 1) + " 0 R >> >>", L("/X Do"))));
            C(CodeOf(() => PdfReader.Parse(Build(chain), "chain.pdf", new PdfReadOptions(MaxDepth: 4))) == "depth-limit", "Form XObject nesting beyond MaxDepth refused");
            C(CodeOf(() => PdfReader.Parse(Build(PageObjects(StreamBody("/Filter /FlateDecode", Deflate(new byte[10_000_000])))), "bomb.pdf", new PdfReadOptions(MaxDecodedBytes: 1_000_000))) == "decode-limit",
                "decompression beyond MaxDecodedBytes refused");
            var three = new List<(int, byte[])>
            {
                (1, L("<< /Type /Catalog /Pages 2 0 R >>")),
                (2, L("<< /Type /Pages /Kids [3 0 R 3 0 R 3 0 R] /Count 3 >>")),
            };
            three[1] = (2, L("<< /Type /Pages /Kids [3 0 R 5 0 R 6 0 R] /Count 3 >>"));
            three.Add((3, L("<< /Type /Page /Parent 2 0 R >>")));
            three.Add((5, L("<< /Type /Page /Parent 2 0 R >>")));
            three.Add((6, L("<< /Type /Page /Parent 2 0 R >>")));
            C(CodeOf(() => PdfReader.Parse(Build(three), "pages.pdf", new PdfReadOptions(MaxPages: 2))) == "page-limit", "MaxPages refused as page-limit");
            C(ThrowsArgument(() => PdfReader.Parse(OnePage(""), "x.pdf", new PdfReadOptions(MaxDepth: 0))) && ThrowsArgument(() => PdfReader.Parse(OnePage(""), "x.pdf", new PdfReadOptions(MaxDepth: 100_000))),
                "invalid read options rejected");

            var watch = Stopwatch.StartNew();
            var hostile = new[]
            {
                Concat(L("%PDF-1.7\n"), Encoding.ASCII.GetBytes(new string('[', 2_000_000))),
                Concat(L("%PDF-1.7\n1 0 obj ("), Encoding.ASCII.GetBytes(new string('(', 2_000_000))),
                Concat(L("%PDF-1.7\n"), Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("1 0 obj << /A (x ", 100_000)))),
                Concat(L("%PDF-1.7\n"), Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("1 0 obj << /Length 5 >> stream\n", 100_000)))),
            };
            var hostileCodes = hostile.Select(h => CodeOf(() => PdfReader.Parse(h, "hostile.pdf"))).ToList();
            C(hostileCodes.All(code => code is not null && !code.StartsWith("unexpected", StringComparison.Ordinal)) && watch.ElapsedMilliseconds < 10_000,
                "hostile inputs refused quickly with stable codes", string.Join(",", hostileCodes) + " in " + I(watch.ElapsedMilliseconds) + " ms");

            // Truncation sweep and mutation fuzz: success or PdfFormatException only, in bounded time.
            watch.Restart();
            var unexpected = new List<string>();
            foreach (var path in new[] { simplePath, transformsPath, flatePath })
            {
                var bytes = File.ReadAllBytes(path);
                for (var cut = 0; cut < bytes.Length; cut++)
                {
                    var code = CodeOf(() => PdfReader.Parse(bytes.AsSpan(0, cut).ToArray(), "cut.pdf"));
                    if (code is not null && code.StartsWith("unexpected", StringComparison.Ordinal)) unexpected.Add(Path.GetFileName(path) + "@" + I(cut) + " " + code);
                }
            }
            var random = new Random(140);
            var tokens = new[] { "(", ")", "[", "]", "<<", ">>", "<", ">", "/", "%", "\\", " q ", " Q ", " cm ", " re ", " Do ", " BI ", " ID ", " EI ", "stream", "endstream", "obj", "endobj", " R ", "-", ".", "9999999999", "1e9" };
            foreach (var path in new[] { simplePath, transformsPath, flatePath })
            {
                var original = File.ReadAllBytes(path);
                for (var iteration = 0; iteration < 400; iteration++)
                {
                    var bytes = new List<byte>(original);
                    var edits = 1 + random.Next(8);
                    for (var e = 0; e < edits && bytes.Count > 0; e++)
                    {
                        var at = random.Next(bytes.Count);
                        switch (random.Next(3))
                        {
                            case 0: bytes[at] = (byte)random.Next(256); break;
                            case 1: bytes.RemoveAt(at); break;
                            default: bytes.InsertRange(at, L(tokens[random.Next(tokens.Length)])); break;
                        }
                    }
                    var mutated = bytes.ToArray();
                    var code = CodeOf(() => PdfReader.Parse(mutated, "fuzz.pdf"));
                    if (code is not null && code.StartsWith("unexpected", StringComparison.Ordinal)) unexpected.Add(Path.GetFileName(path) + "#" + I(iteration) + " " + code);
                }
            }
            C(unexpected.Count == 0 && watch.ElapsedMilliseconds < 60_000, "truncation sweep and mutation fuzz raise only PdfFormatException, in bounded time",
                I(unexpected.Count) + " unexpected in " + I(watch.ElapsedMilliseconds) + " ms: " + string.Join(" ; ", unexpected.Take(5)));
        }
        catch (Exception error)
        {
            check(false, "pdf refusals unexpected " + error.GetType().Name + ": " + error.Message, "");
        }
    }
}
