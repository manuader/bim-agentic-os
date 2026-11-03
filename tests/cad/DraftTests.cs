using Bim.Cad;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Core = Bim.Core;

// P6-04 CAD drawing to BIM-IR draft oracle: the hand-written draft-plan.dxf (millimetres) and draft-plan.pdf (calibrated
// at 20 mm per PDF unit) fixtures become a draft of BIM-IR level, wall and door/window elements (existing Element
// layout, metres) with per-element confidence, source provenance and an uncertainty report (unpaired lines, ambiguous
// thickness, gaps, unclosed joins, unmatched blocks, unhosted openings). The draft is never persisted. Generated plans
// check bounded work. Unit-tested evidence only.
internal static class DraftTests
{
    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static bool Near(double a, double b, double eps = 1e-6) => Math.Abs(a - b) <= eps;
    private static bool NearP(CadDraftPoint p, double x, double y, double eps = 1e-6) => Near(p.X, x, eps) && Near(p.Y, y, eps);
    private static string Pt(CadDraftPoint p) => "(" + F(p.X) + "," + F(p.Y) + ")";
    private static string Wall(CadWallProposal w) => w.Key + " " + Pt(w.Start) + "-" + Pt(w.End) + " t" + F(w.ThicknessMillimetres) + " c" + F(w.Confidence) + " [" + string.Join(",", w.Notes) + "]";
    private static string Codes(CadDraft d) => string.Join(",", d.Uncertainties.Select(u => u.Code));
    private static bool SameMetres(double[] actual, params double[] expected) => actual.Length == expected.Length && actual.Zip(expected).All(p => Math.Abs(p.First - p.Second) <= 1e-9);

    private static string? CodeOf(Func<object?> action)
    {
        try { action(); return null; }
        catch (CadDraftException error) { return error.Code; }
        catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
    }

    private static DxfProvenance Gen(string handle, int line, string layer = "WALLS", string type = "LINE") => new("gen.dxf", handle, layer, type, line);

    public static void Run(Action<bool, string, string> check, string fixtures)
    {
        void C(bool condition, string name, string detail = "") => check(condition, "draft " + name, detail);
        var projectId = Guid.Parse("6f0c6a52-4d3b-4a8e-9b51-2f6f9d0e7a11");
        var options = new CadToBimIrOptions(projectId);

        // ---------- draft-plan.dxf ----------
        try
        {
            var drawing = DxfReader.ReadFile(Path.Combine(fixtures, "draft-plan.dxf"));
            var d = CadToBimIr.FromDxf(drawing, options);
            C(d.SourceFile == "draft-plan.dxf" && d.Format == "dxf" && Near(d.MillimetresPerUnit, 1, 1e-12) && d.ProjectId == projectId, "dxf draft header", d.SourceFile + " " + d.Format + " " + F(d.MillimetresPerUnit));
            C(d.Walls.Count == 8, "dxf eight wall proposals", string.Join(" | ", d.Walls.Select(Wall)));
            C(d.Openings.Count == 2, "dxf two opening proposals", string.Join(" | ", d.Openings.Select(o => o.Key + " " + o.Kind + " " + o.BlockName)));
            C(d.Walls.Select(w => w.Key).SequenceEqual(Enumerable.Range(1, d.Walls.Count).Select(i => "cad:wall:" + i.ToString(CultureInfo.InvariantCulture))), "dxf wall keys in drawing order", string.Join(",", d.Walls.Select(w => w.Key)));

            var levelId = Core.RoomCompiler.Id(projectId, "cad:level");
            C(d.Level.Id == levelId && d.Level.Kind == "level" && SameMetres(d.Level.GeometryMetres, 0) && d.Level.Dependencies.Length == 0, "dxf draft level element", JsonSerializer.Serialize(d.Level));

            if (d.Walls.Count == 8)
            {
                var south = d.Walls[0];
                C(NearP(south.Start, 100, 100) && NearP(south.End, 5900, 100) && Near(south.ThicknessMillimetres, 200) && Near(south.HeightMillimetres, 2700),
                    "dxf south wall merged across the door and closed at both corners", Wall(south));
                C(Near(south.Confidence, 0.9), "dxf south wall confidence is the weakest member pair (1800/2000)", Wall(south));
                C(south.Sources.Select(s => s.Handle).SequenceEqual(new[] { "100", "101", "102", "103" }) && south.Sources.All(s => s.Format == "dxf" && s.SourceFile == "draft-plan.dxf" && s.Layer == "WALLS" && s.EntityType == "LINE" && s.SegmentIndex is null && s.Line > 0),
                    "dxf south wall source provenance (file, layer, handle, line)", string.Join(",", south.Sources.Select(s => s.Handle + "@" + s.Line)));
                C(south.Notes.Contains("merged-across-opening") && south.Notes.Contains("start:joined") && south.Notes.Contains("end:joined") && south.Notes.Contains("height-assumed"), "dxf south wall notes", Wall(south));
                C(south.Element.Kind == "wall" && south.Element.Id == Core.RoomCompiler.Id(projectId, "cad:wall:1") && SameMetres(south.Element.GeometryMetres, 0.1, 0.1, 5.9, 0.1, 2.7, 0.2)
                    && south.Element.Dependencies.SequenceEqual(new[] { levelId }) && south.Element.Provenance.Source == "cad:draft-plan.dxf" && south.Element.Provenance.Approval == "draft-unapproved",
                    "dxf south wall BIM-IR element in metres", JsonSerializer.Serialize(south.Element));

                var east = d.Walls[1];
                C(NearP(east.Start, 5900, 100) && NearP(east.End, 5900, 3900) && Near(east.ThicknessMillimetres, 200) && Near(east.Confidence, 1100.0 / 1300) && east.Notes.Contains("merged-across-opening"),
                    "dxf east wall merged across the window", Wall(east));
                var north = d.Walls[2];
                C(NearP(north.Start, 5900, 3900) && NearP(north.End, 100, 3900) && Near(north.Confidence, 5600.0 / 6000), "dxf north wall runs -x and closes both corners", Wall(north));
                var west = d.Walls[3];
                C(NearP(west.Start, 100, 3900) && NearP(west.End, 100, 100) && Near(west.Confidence, 3600.0 / 4000), "dxf west wall (inner 3600 / outer 4000)", Wall(west));
                var partition = d.Walls[4];
                C(NearP(partition.Start, 3050, 3900) && NearP(partition.End, 3050, 2500) && Near(partition.ThicknessMillimetres, 100) && Near(partition.Confidence, 0.85)
                    && partition.Notes.Contains("start:joined") && partition.Notes.Contains("end:unclosed"),
                    "dxf partition tee-joined to the north wall axis, free end unclosed", Wall(partition));
                var garden1 = d.Walls[5];
                var garden2 = d.Walls[6];
                C(NearP(garden1.Start, 8000, 100) && NearP(garden1.End, 10000, 100) && NearP(garden2.Start, 10500, 100) && NearP(garden2.End, 12000, 100)
                    && Near(garden1.Confidence, 0.85 * 0.85) && garden1.Notes.Contains("end:gap") && garden2.Notes.Contains("start:gap"),
                    "dxf collinear walls with an unbridged 500 mm gap stay separate", Wall(garden1) + " | " + Wall(garden2));
                var triple = d.Walls[7];
                C(NearP(triple.Start, 8000, 2075) && NearP(triple.End, 11000, 2075) && Near(triple.ThicknessMillimetres, 150) && Near(triple.Confidence, 0.5 * 0.85 * 0.85)
                    && triple.Notes.Contains("ambiguous-thickness") && triple.Sources.Select(s => s.Handle).SequenceEqual(new[] { "114", "115" }),
                    "dxf three parallel lines give one ambiguous wall", Wall(triple));
            }

            if (d.Openings.Count == 2)
            {
                var door = d.Openings[0];
                C(door.Kind == "door" && door.BlockName == "DOOR-900" && door.HostKey == "cad:wall:1" && NearP(door.Start, 2000, 100) && NearP(door.End, 2900, 100)
                    && Near(door.WidthMillimetres, 900) && Near(door.SillMillimetres, 0) && Near(door.HeightMillimetres, 2100) && Near(door.Confidence, 1),
                    "dxf door hosted on the south wall over its 900 mm gap", door.ToString());
                C(door.Source.Handle == "200" && door.Source.EntityType == "INSERT" && door.Source.Layer == "OPENINGS" && door.Source.Format == "dxf" && door.Source.Line > 0, "dxf door source is its INSERT", door.Source.ToString());
                C(door.Element.Kind == "door" && door.Element.Id == Core.RoomCompiler.Id(projectId, "cad:opening:1") && SameMetres(door.Element.GeometryMetres, 2.0, 0.1, 2.9, 0.1, 0, 2.1)
                    && door.Element.Dependencies.SequenceEqual(new[] { levelId, Core.RoomCompiler.Id(projectId, "cad:wall:1") }),
                    "dxf door BIM-IR element [ax,ay,bx,by,sill,height] depends on level and host", JsonSerializer.Serialize(door.Element));
                var window = d.Openings[1];
                C(window.Kind == "window" && window.BlockName == "WINDOW-1200" && window.HostKey == "cad:wall:2" && NearP(window.Start, 5900, 1500) && NearP(window.End, 5900, 2700)
                    && Near(window.WidthMillimetres, 1200) && Near(window.SillMillimetres, 900) && Near(window.HeightMillimetres, 1200) && window.Source.Handle == "201"
                    && SameMetres(window.Element.GeometryMetres, 5.9, 1.5, 5.9, 2.7, 0.9, 1.2),
                    "dxf rotated window hosted on the east wall", window.ToString());
            }

            // Uncertainty report.
            var u = d.Uncertainties;
            var unpaired = u.Where(x => x.Code == "unpaired-line").ToList();
            C(unpaired.Count == 2 && unpaired.Select(x => x.Sources.Single().Handle).SequenceEqual(new[] { "116", "117" }), "dxf unpaired lines are the third parallel line and the lone line (jambs are caps)",
                string.Join(",", unpaired.Select(x => string.Join("+", x.Sources.Select(s => s.Handle)))));
            var ambiguous = u.Where(x => x.Code == "ambiguous-thickness").ToList();
            C(ambiguous.Count == 2 && ambiguous.All(x => x.ProposalKey == "cad:wall:8") && ambiguous.Select(x => x.ValueMillimetres ?? -1).OrderBy(v => v).Zip(new[] { 250.0, 400.0 }).All(p => Near(p.First, p.Second)),
                "dxf ambiguous thickness alternatives 250 and 400 on the triple wall", string.Join(";", ambiguous.Select(x => x.ProposalKey + ":" + x.ValueMillimetres)));
            var gaps = u.Where(x => x.Code == "gap").ToList();
            C(gaps.Count == 2 && gaps.All(x => Near(x.ValueMillimetres ?? -1, 500)) && gaps.Select(x => x.ProposalKey).SequenceEqual(new[] { "cad:wall:6", "cad:wall:7" }) && gaps[0].At is { } g0 && NearP(g0, 10000, 100),
                "dxf gap reported at both ends facing the 500 mm gap", string.Join(";", gaps.Select(x => x.ProposalKey + ":" + x.ValueMillimetres + ":" + (x.At is { } a ? Pt(a) : "-"))));
            var unclosed = u.Where(x => x.Code == "unclosed-join").ToList();
            C(unclosed.Select(x => x.ProposalKey).SequenceEqual(new[] { "cad:wall:5", "cad:wall:6", "cad:wall:7", "cad:wall:8", "cad:wall:8" }) && unclosed[0].At is { } u0 && NearP(u0, 3050, 2500),
                "dxf unclosed joins at free wall ends", string.Join(";", unclosed.Select(x => x.ProposalKey + (x.At is { } a ? Pt(a) : "-"))));
            var unmatched = u.Where(x => x.Code == "unmatched-block").ToList();
            C(unmatched.Count == 1 && unmatched[0].Sources.Single().Handle == "202" && unmatched[0].Message.Contains("COLUMN", StringComparison.Ordinal), "dxf unmatched COLUMN block reported", string.Join(";", unmatched.Select(x => x.Message)));
            var unhosted = u.Where(x => x.Code == "unhosted-opening").ToList();
            C(unhosted.Count == 1 && unhosted[0].Sources.Single().Handle == "203", "dxf door far from any wall is an unhosted opening", string.Join(";", unhosted.Select(x => x.Message)));
            C(u.Count == 13 && u.Select(x => x.Code).SequenceEqual(u.Select(x => x.Code).OrderBy(c => c, StringComparer.Ordinal)), "dxf uncertainty report has 13 entries sorted by code", Codes(d));

            // BIM-IR validity, no persistence, determinism.
            var project = d.ToProject(3);
            C(project.Id == projectId && project.Revision == 3 && project.Elements.Length == 11 && project.Elements[0].Kind == "level" && project.Elements.Skip(1).Take(8).All(e => e.Kind == "wall")
                && project.Elements.Skip(9).Select(e => e.Kind).SequenceEqual(new[] { "door", "window" }), "dxf ToProject orders level, walls, openings", string.Join(",", project.Elements.Select(e => e.Kind)));
            C(Core.Compiler.Validate(project).Length == 0, "dxf draft project passes Compiler.Validate", string.Join(";", Core.Compiler.Validate(project).Select(i => i.Code)));
            C(project.Elements.Where(e => e.Kind is "wall" or "level").All(e => Core.GeometryCodec.Problem(e) is null), "dxf draft walls and level decode with GeometryCodec");
            var json = JsonSerializer.Serialize(d);
            var culture = CultureInfo.CurrentCulture;
            string jsonDe;
            try { CultureInfo.CurrentCulture = new CultureInfo("de-DE"); jsonDe = JsonSerializer.Serialize(CadToBimIr.FromDxf(DxfReader.ReadFile(Path.Combine(fixtures, "draft-plan.dxf")), options)); }
            finally { CultureInfo.CurrentCulture = culture; }
            C(json == JsonSerializer.Serialize(CadToBimIr.FromDxf(drawing, options)) && json == jsonDe, "dxf draft deterministic across runs and de-DE culture", json.Length + " " + jsonDe.Length);

            var empty = Path.Combine(Path.GetTempPath(), "cad-draft-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(empty);
            var cwd = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = empty;
                CadToBimIr.FromDxf(drawing, options).ToProject();
                C(!Directory.EnumerateFileSystemEntries(empty).Any(), "dxf draft writes nothing to the working directory");
            }
            finally { Environment.CurrentDirectory = cwd; Directory.Delete(empty, true); }
            var adapter = Path.Combine(fixtures, "..", "..", "..", "adapters", "cad", "Bim.Cad");
            var sources = Directory.GetFiles(adapter, "CadToBimIr*.cs");
            var text = string.Concat(sources.Select(File.ReadAllText));
            C(sources.Length > 0 && !new[] { "ProjectStore", "File.", "Directory.", "FileStream", "Commit(" }.Any(t => text.Contains(t, StringComparison.Ordinal)),
                "draft source never touches the store or the filesystem", string.Join(",", sources.Select(Path.GetFileName)));

            // Options: DXF without units needs an explicit scale; walls.dxf (metres) is converted to millimetres.
            var unitless = drawing with { InsUnits = null, Units = DxfUnits.Unspecified, MetresPerUnit = null };
            C(CodeOf(() => CadToBimIr.FromDxf(unitless, options)) == "uncalibrated", "dxf without units refused as uncalibrated");
            var scaled = CadToBimIr.FromDxf(unitless, options with { MillimetresPerUnit = 1 });
            C(JsonSerializer.Serialize(scaled.Walls) == JsonSerializer.Serialize(d.Walls), "dxf without units accepted with explicit MillimetresPerUnit");
            var metres = CadToBimIr.FromDxf(DxfReader.ReadFile(Path.Combine(fixtures, "walls.dxf")), options);
            C(Near(metres.MillimetresPerUnit, 1000, 1e-9) && metres.Walls.Count > 0 && Near(metres.Walls[0].ThicknessMillimetres, 200) && NearP(metres.Walls[0].Start, 100, 100) && metres.Walls[0].Sources[0].Handle == "200",
                "walls.dxf in metres drafted in millimetres", string.Join(" | ", metres.Walls.Select(Wall)));
            var layered = CadToBimIr.FromDxf(drawing, options with { WallLayers = new[] { "NOPE" } });
            C(layered.Walls.Count == 0 && layered.Openings.Count == 0 && layered.Uncertainties.Count(x => x.Code == "unhosted-opening") == 3, "dxf WallLayers filter leaves openings unhosted", Codes(layered));
        }
        catch (Exception error)
        {
            C(false, "dxf unexpected " + error.GetType().Name, error.ToString());
        }

        // ---------- draft-plan.pdf ----------
        try
        {
            var doc = PdfReader.ReadFile(Path.Combine(fixtures, "draft-plan.pdf"));
            var calibration = PdfCalibration.FromKnownDimension(new Point2(100, 100), new Point2(400, 100), 6000).AlignTo(new Point2(100, 100), new Point2(400, 100));
            var pdfOptions = options with { PdfDoorForms = new[] { 5 } };
            var d = CadToBimIr.FromPdf(doc, 1, calibration, pdfOptions);
            C(d.SourceFile == "draft-plan.pdf" && d.Format == "pdf" && Near(d.MillimetresPerUnit, 20, 1e-12), "pdf draft header", d.SourceFile + " " + F(d.MillimetresPerUnit));
            C(d.Walls.Count == 4, "pdf four wall proposals", string.Join(" | ", d.Walls.Select(Wall)));
            if (d.Walls.Count == 4)
            {
                C(NearP(d.Walls[0].Start, 100, 100) && NearP(d.Walls[0].End, 5900, 100) && Near(d.Walls[0].ThicknessMillimetres, 200) && Near(d.Walls[0].Confidence, 1600.0 / 1800)
                    && d.Walls[0].Notes.Contains("merged-across-opening"), "pdf south wall merged across the door form", Wall(d.Walls[0]));
                C(NearP(d.Walls[1].Start, 5900, 100) && NearP(d.Walls[1].End, 5900, 3900) && NearP(d.Walls[2].End, 100, 3900) && NearP(d.Walls[3].End, 100, 100), "pdf room closed at four corners", string.Join(" | ", d.Walls.Select(Wall)));
                var s = d.Walls[0].Sources[0];
                C(s.Format == "pdf" && s.SourceFile == "draft-plan.pdf" && s.Page == 1 && s.ObjectNumber == 4 && s.OperatorIndex == 3 && s.Part == 0 && s.Handle == "p1:o4:i3" && s.Line is null,
                    "pdf wall source provenance (file, page, content object, path operator index)", s.ToString());
                C(d.Walls[0].Sources.Select(x => x.OperatorIndex).SequenceEqual(new int?[] { 3, 6, 9, 12 }), "pdf south wall built from four stroked paths", string.Join(",", d.Walls[0].Sources.Select(x => x.OperatorIndex)));
            }
            C(d.Openings.Count == 1, "pdf one door from the listed form", string.Join(" | ", d.Openings.Select(o => o.ToString())));
            if (d.Openings.Count == 1)
            {
                var door = d.Openings[0];
                C(door.Kind == "door" && door.BlockName == "form:o5" && door.HostKey == "cad:wall:1" && NearP(door.Start, 1800, 100) && NearP(door.End, 2700, 100) && Near(door.WidthMillimetres, 900)
                    && door.Source.Page == 1 && door.Source.ObjectNumber == 4 && door.Source.OperatorIndex == 36 && door.Source.EntityType == "PDF-DO",
                    "pdf door hosted on the south wall with its Do operator as source", door.ToString() + " " + door.Source);
                C(SameMetres(door.Element.GeometryMetres, 1.8, 0.1, 2.7, 0.1, 0, 2.1), "pdf door BIM-IR element", JsonSerializer.Serialize(door.Element));
            }
            var stray = d.Uncertainties.Where(x => x.Code == "unpaired-line").ToList();
            C(stray.Count == 1 && stray[0].Sources.Single().OperatorIndex == 33 && stray[0].At is { } at && NearP(at, 1000, 2500), "pdf stray line unpaired with its path operator", string.Join(";", stray.Select(x => x.Sources.Single().ToString())));
            var sofa = d.Uncertainties.Where(x => x.Code == "unmatched-block").ToList();
            C(sofa.Count == 1 && sofa[0].Sources.Single().OperatorIndex == 40 && sofa[0].Message.Contains("form:o6", StringComparison.Ordinal), "pdf unlisted form reported as unmatched block and not drafted as a wall", string.Join(";", sofa.Select(x => x.Message)));
            C(d.Uncertainties.Count == 2, "pdf uncertainty report has only the stray line and the unmatched form", Codes(d));
            C(Core.Compiler.Validate(d.ToProject()).Length == 0, "pdf draft project passes Compiler.Validate");
            var unlisted = CadToBimIr.FromPdf(doc, 1, calibration, options);
            C(unlisted.Openings.Count == 0 && unlisted.Uncertainties.Count(x => x.Code == "unmatched-block") == 2 && unlisted.Uncertainties.Count(x => x.Code == "gap") == 2,
                "pdf without listed door forms: both forms unmatched and the door gap reported", Codes(unlisted));
            C(CodeOf(() => CadToBimIr.FromPdf(doc, 2, calibration, options)) == "page-missing", "pdf missing page refused");
        }
        catch (Exception error)
        {
            C(false, "pdf unexpected " + error.GetType().Name, error.ToString());
        }

        // ---------- bounded work ----------
        try
        {
            var doorBlock = new DxfBlock("DOOR", new Point2(0, 0), "B1", 1, new DxfEntity[]
            {
                new DxfLine(Gen("B2", 2, "0"), new Point2(0, 0), new Point2(0, 900)),
                new DxfArc(Gen("B3", 3, "0", "ARC"), new Point2(0, 0), 900, 0, 90),
            });
            DxfDrawing Grid(int n)
            {
                var entities = new List<DxfEntity>();
                var line = 0;
                void L(double x1, double y1, double x2, double y2) { line++; entities.Add(new DxfLine(Gen("L" + line.ToString(CultureInfo.InvariantCulture), line), new Point2(x1, y1), new Point2(x2, y2))); }
                for (var i = 0; i < n; i++)
                    for (var j = 0; j < n; j++)
                    {
                        double x = i * 5000, y = j * 5000;
                        L(x, y, x + 3000, y); L(x + 200, y + 200, x + 2800, y + 200);
                        L(x + 3000, y, x + 3000, y + 3000); L(x + 2800, y + 200, x + 2800, y + 2800);
                        L(x + 3000, y + 3000, x, y + 3000); L(x + 2800, y + 2800, x + 200, y + 2800);
                        L(x, y + 3000, x, y); L(x + 200, y + 2800, x + 200, y + 200);
                        line++;
                        entities.Add(new DxfInsert(Gen("D" + line.ToString(CultureInfo.InvariantCulture), line, "OPENINGS", "INSERT"), "DOOR", new Point2(x + 1000, y + 200), 1, 1, 0));
                    }
                return new DxfDrawing("grid.dxf", 4, DxfUnits.Millimetres, 0.001, Array.Empty<DxfLayer>(), new[] { doorBlock }, entities, Array.Empty<DxfProvenance>(), Array.Empty<DxfWarning>());
            }
            var grid = Grid(40);
            var watch = Stopwatch.StartNew();
            var big = CadToBimIr.FromDxf(grid, options);
            watch.Stop();
            C(big.Walls.Count == 6400 && big.Openings.Count == 1600 && big.Uncertainties.Count == 0 && watch.ElapsedMilliseconds < 15000,
                "budget 1600 rooms (12800 lines, 1600 doors) drafted completely and quickly", big.Walls.Count + " walls, " + big.Openings.Count + " openings, " + Codes(big).Length + " codes, " + watch.ElapsedMilliseconds + " ms");
            C(big.Walls.Take(4).All(w => Near(w.Confidence, 2600.0 / 3000)) && NearP(big.Walls[0].Start, 100, 100) && NearP(big.Walls[0].End, 2900, 100) && Near(big.Openings[0].WidthMillimetres, 900),
                "budget first room closed with door", string.Join(" | ", big.Walls.Take(4).Select(Wall)));
            C(CodeOf(() => CadToBimIr.FromDxf(grid, options with { MaxWork = 1000 })) == "draft-limit", "budget MaxWork exceeded raises draft-limit");
            C(CodeOf(() => CadToBimIr.FromDxf(grid, options with { MaxWalls = 100 })) == "draft-limit", "budget MaxWalls exceeded raises draft-limit");
            C(CodeOf(() => CadToBimIr.FromDxf(grid, options with { MaxOpenings = 10 })) == "draft-limit", "budget MaxOpenings exceeded raises draft-limit");
            C(CodeOf(() => CadToBimIr.FromDxf(grid, options with { MaxSegments = 100 })) == "draft-limit", "budget wall-pair segment limit surfaces as draft-limit");

            // 5000 isolated parallel walls: every end is unclosed; the report is capped.
            var stack = new List<DxfEntity>();
            for (var k = 0; k < 5000; k++)
            {
                stack.Add(new DxfLine(Gen("S" + k.ToString(CultureInfo.InvariantCulture), 2 * k + 1), new Point2(0, k * 1000), new Point2(3000, k * 1000)));
                stack.Add(new DxfLine(Gen("T" + k.ToString(CultureInfo.InvariantCulture), 2 * k + 2), new Point2(0, k * 1000 + 200), new Point2(3000, k * 1000 + 200)));
            }
            var stacked = new DxfDrawing("stack.dxf", 4, DxfUnits.Millimetres, 0.001, Array.Empty<DxfLayer>(), Array.Empty<DxfBlock>(), stack, Array.Empty<DxfProvenance>(), Array.Empty<DxfWarning>());
            watch.Restart();
            var capped = CadToBimIr.FromDxf(stacked, options with { MaxUncertainties = 100 });
            watch.Stop();
            C(capped.Walls.Count == 5000 && capped.Uncertainties.Count == 101 && capped.Uncertainties[^1].Code == "uncertainty-limit" && capped.Uncertainties.Take(100).All(x => x.Code == "unclosed-join") && watch.ElapsedMilliseconds < 15000,
                "budget 5000 parallel walls with the uncertainty report capped", capped.Walls.Count + " walls, " + capped.Uncertainties.Count + " entries, " + watch.ElapsedMilliseconds + " ms");

            // 3000 door inserts at one spot on one wall are each hosted without blowing up.
            var crowd = new List<DxfEntity>
            {
                new DxfLine(Gen("W1", 1), new Point2(0, 0), new Point2(6000, 0)),
                new DxfLine(Gen("W2", 2), new Point2(0, 200), new Point2(6000, 200)),
            };
            for (var k = 0; k < 3000; k++) crowd.Add(new DxfInsert(Gen("I" + k.ToString(CultureInfo.InvariantCulture), k + 3, "OPENINGS", "INSERT"), "DOOR", new Point2(1000, 200), 1, 1, 0));
            var crowded = new DxfDrawing("crowd.dxf", 4, DxfUnits.Millimetres, 0.001, Array.Empty<DxfLayer>(), new[] { doorBlock }, crowd, Array.Empty<DxfProvenance>(), Array.Empty<DxfWarning>());
            watch.Restart();
            var many = CadToBimIr.FromDxf(crowded, options with { MaxUncertainties = 100 });
            watch.Stop();
            C(many.Walls.Count == 1 && many.Openings.Count == 3000 && many.Openings.All(o => o.HostKey == "cad:wall:1") && watch.ElapsedMilliseconds < 15000, "budget 3000 coincident doors hosted", many.Openings.Count + " openings, " + watch.ElapsedMilliseconds + " ms");
            C(many.Uncertainties.Count == 101 && many.Uncertainties.Take(100).All(x => x.Code == "opening-overlap") && many.Uncertainties[^1].Code == "uncertainty-limit"
                && many.Openings.All(o => Near(o.Confidence, 0.5) && o.Notes.Contains("overlap")),
                "budget 3000 coincident doors reported as overlapping (report capped)", Codes(many).Length + " " + many.Uncertainties.Count);

            C(ThrowsArgument(() => CadToBimIr.FromDxf(grid, options with { MaxThicknessMillimetres = double.NaN })), "options NaN thickness refused");
            C(ThrowsArgument(() => CadToBimIr.FromDxf(grid, options with { ProjectId = Guid.Empty })), "options empty project id refused");
            C(ThrowsArgument(() => CadToBimIr.FromDxf(grid, options with { JoinToleranceMillimetres = -1 })), "options negative join tolerance refused");
        }
        catch (Exception error)
        {
            C(false, "budget unexpected " + error.GetType().Name, error.ToString());
        }

        Review(C, fixtures, options);
    }

    private static readonly DxfBlock DoorBlock = new("DOOR", new Point2(0, 0), "B1", 1, new DxfEntity[]
    {
        new DxfLine(Gen("B2", 2, "0"), new Point2(0, 0), new Point2(0, 900)),
        new DxfArc(Gen("B3", 3, "0", "ARC"), new Point2(0, 0), 900, 0, 90),
    });

    private static DxfDrawing Drawing(string name, IEnumerable<DxfEntity> entities) =>
        new(name, 4, DxfUnits.Millimetres, 0.001, Array.Empty<DxfLayer>(), new[] { DoorBlock }, entities.ToArray(), Array.Empty<DxfProvenance>(), Array.Empty<DxfWarning>());

    private static List<DxfEntity> Lines(params (double X1, double Y1, double X2, double Y2)[] lines) =>
        lines.Select((l, i) => (DxfEntity)new DxfLine(Gen("L" + (i + 1).ToString(CultureInfo.InvariantCulture), i + 1), new Point2(l.X1, l.Y1), new Point2(l.X2, l.Y2))).ToList();

    private static DxfInsert Door(string handle, double x, double y) => new(Gen(handle, 900, "OPENINGS", "INSERT"), "DOOR", new Point2(x, y), 1, 1, 0);

    private static string? MessageOf(Func<object?> action)
    {
        try { action(); return null; }
        catch (CadDraftException error) { return error.Code + ": " + error.Message; }
        catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
    }

    private static bool AllFinite(CadDraft d) => d.ToProject().Elements.All(e => e.GeometryMetres.All(double.IsFinite))
        && d.Uncertainties.All(u => u.At is not { } a || (double.IsFinite(a.X) && double.IsFinite(a.Y)));

    // Review follow-ups: nested door/window blocks and forms, overlapping openings, core opening rules, zero-thickness
    // pairs, through walls split at a tee, early work bounds and non-finite input.
    private static void Review(Action<bool, string, string> check, string fixtures, CadToBimIrOptions options)
    {
        void C(bool condition, string name, string detail = "") => check(condition, name, detail);
        // 1. Nested blocks: draft-plan wrapped in one PLAN block inserted once.
        try
        {
            var drawing = DxfReader.ReadFile(Path.Combine(fixtures, "draft-plan.dxf"));
            var flat = CadToBimIr.FromDxf(drawing, options);
            var plan = new DxfBlock("PLAN", new Point2(0, 0), "PB", 1, drawing.Entities);
            var nested = drawing with
            {
                Blocks = drawing.Blocks.Append(plan).OrderBy(b => b.Name, StringComparer.Ordinal).ToArray(),
                Entities = new DxfEntity[] { new DxfInsert(new DxfProvenance("draft-plan.dxf", "P1", "0", "INSERT", 1), "PLAN", new Point2(0, 0), 1, 1, 0) },
            };
            var n = CadToBimIr.FromDxf(nested, options with { WallsFromUnmatchedBlocks = true });
            C(n.Walls.Count == 8 && n.Walls.Select(w => Pt(w.Start) + Pt(w.End)).SequenceEqual(flat.Walls.Select(w => Pt(w.Start) + Pt(w.End)))
                && n.Walls[0].Sources[0].Handle == "100" && n.Walls[0].Sources[0].InsertPath.SequenceEqual(new[] { "P1" }),
                "review nested plan block gives the same walls with WallsFromUnmatchedBlocks", string.Join(" | ", n.Walls.Select(Wall)));
            C(n.Openings.Count == 2 && n.Openings[0].BlockName == "DOOR-900" && n.Openings[0].Source.Handle == "200" && n.Openings[0].Source.InsertPath.SequenceEqual(new[] { "P1" })
                && NearP(n.Openings[0].Start, 2000, 100) && n.Openings[1].BlockName == "WINDOW-1200" && NearP(n.Openings[1].Start, 5900, 1500),
                "review door and window nested in the plan block are found through the insert path", string.Join(" | ", n.Openings.Select(o => o.BlockName + " " + o.Source.Handle + " " + Pt(o.Start))));
            var nm = n.Uncertainties.Where(x => x.Code == "unmatched-block").ToList();
            C(nm.Count == 2 && nm.Any(x => x.Message.Contains("PLAN", StringComparison.Ordinal)) && nm.Any(x => x.Message.Contains("COLUMN", StringComparison.Ordinal) && x.Sources.Single().Handle == "202")
                && n.Uncertainties.Count == 14, "review nested unmatched PLAN and COLUMN reported", Codes(n));
            var off = CadToBimIr.FromDxf(nested, options);
            C(off.Walls.Count == 0 && off.Openings.Count == 0 && off.Uncertainties.Count(x => x.Code == "unhosted-opening") == 3, "review nested openings stay unhosted without wall geometry from the plan block", Codes(off));

            var content = "1 w\n" + string.Concat(new[] { (100, 100, 190, 100), (110, 110, 190, 110), (235, 100, 400, 100), (235, 110, 390, 110), (400, 100, 400, 300), (390, 110, 390, 290),
                (400, 300, 100, 300), (390, 290, 110, 290), (100, 300, 100, 100), (110, 290, 110, 110) }.Select(t => t.Item1 + " " + t.Item2 + " m\n" + t.Item3 + " " + t.Item4 + " l\nS\n"))
                + "q\n1 0 0 1 190 110 cm\n/Door Do\nQ\n";
            var objects = new List<(int, byte[])>
            {
                (1, Encoding.Latin1.GetBytes("<< /Type /Catalog /Pages 2 0 R >>")),
                (2, Encoding.Latin1.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")),
                (3, Encoding.Latin1.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 420] /Resources << /XObject << /Plan 5 0 R >> >> /Contents 4 0 R >>")),
                (4, PdfTests.StreamBody("", Encoding.Latin1.GetBytes("/Plan Do\n"))),
                (5, PdfTests.StreamBody("/Type /XObject /Subtype /Form /BBox [0 0 595 420] /Resources << /XObject << /Door 6 0 R >> >>", Encoding.Latin1.GetBytes(content))),
                (6, PdfTests.StreamBody("/Type /XObject /Subtype /Form /BBox [0 0 50 50]", Encoding.Latin1.GetBytes("0 0 m\n0 45 l\nS\n45 0 m\n45 24.85 24.85 45 0 45 c\nS\n"))),
            };
            var doc = PdfReader.Parse(PdfTests.Build(objects), "nested.pdf");
            var calibration = PdfCalibration.FromKnownDimension(new Point2(100, 100), new Point2(400, 100), 6000).AlignTo(new Point2(100, 100), new Point2(400, 100));
            var p = CadToBimIr.FromPdf(doc, 1, calibration, options with { PdfDoorForms = new[] { 6 }, WallsFromUnmatchedBlocks = true });
            C(p.Walls.Count == 4 && p.Openings.Count == 1 && p.Openings[0].BlockName == "form:o6" && p.Openings[0].Source.ObjectNumber == 5 && p.Openings[0].Source.OperatorIndex == 33
                && p.Openings[0].Source.InsertPath.SequenceEqual(new[] { "o4:i0" }) && NearP(p.Openings[0].Start, 1800, 100) && p.Walls[0].Sources[0].ObjectNumber == 5,
                "review door form nested in a plan form found through the Do path", string.Join(" | ", p.Openings.Select(o => o.BlockName + " " + o.Source)) + " " + string.Join(" | ", p.Walls.Select(Wall)));
            C(p.Uncertainties.Count == 1 && p.Uncertainties[0].Code == "unmatched-block" && p.Uncertainties[0].Message.Contains("form:o5", StringComparison.Ordinal), "review nested pdf reports only the plan form as unmatched", Codes(p));
        }
        catch (Exception error)
        {
            C(false, "review nested unexpected " + error.GetType().Name, error.ToString());
        }

        // 2-5. Overlaps, opening rules, zero thickness, tees.
        try
        {
            var pair = Lines((0, 0, 6000, 0), (0, 200, 6000, 200));
            var two = CadToBimIr.FromDxf(Drawing("overlap.dxf", pair.Append(Door("D1", 1000, 200)).Append(Door("D2", 1500, 200))), options);
            var ov = two.Uncertainties.Where(x => x.Code == "opening-overlap").ToList();
            C(two.Openings.Count == 2 && ov.Count == 1 && ov[0].ProposalKey == "cad:opening:2" && Near(ov[0].ValueMillimetres ?? -1, 400) && two.Openings.All(o => Near(o.Confidence, 0.5)),
                "review two doors overlapping 400 mm reported once and both lowered", Codes(two) + " " + string.Join(",", two.Openings.Select(o => F(o.Confidence))));
            var apart = CadToBimIr.FromDxf(Drawing("apart.dxf", pair.Append(Door("D1", 1000, 200)).Append(Door("D2", 3000, 200))), options);
            C(apart.Openings.Count == 2 && !apart.Uncertainties.Any(x => x.Code == "opening-overlap") && apart.Openings.All(o => Near(o.Confidence, 1)), "review separate doors not reported as overlapping", Codes(apart));

            var room = Lines((0, 0, 3000, 0), (200, 200, 2800, 200), (3000, 0, 3000, 3000), (2800, 200, 2800, 2800), (3000, 3000, 0, 3000), (2800, 2800, 200, 2800), (0, 3000, 0, 0), (200, 2800, 200, 200));
            var corner = CadToBimIr.FromDxf(Drawing("corner.dxf", room.Append(Door("D1", 2000, 200))), options);
            var door = corner.Openings.SingleOrDefault();
            C(door is not null && door.HostKey == "cad:wall:1" && NearP(door.Start, 2000, 100) && NearP(door.End, 2800, 100) && Near(door.WidthMillimetres, 800) && Near(door.Confidence, 0.6)
                && corner.Uncertainties.Count(x => x.Code == "opening-beyond-clear-span" && x.ProposalKey == "cad:opening:1") == 1 && Core.Compiler.Validate(corner.ToProject()).Length == 0,
                "review door running into the corner clamped to the clear span (axis minus half the joined wall)", door?.ToString() + " " + Codes(corner));
            C(ThrowsArgument(() => CadToBimIr.FromDxf(Drawing("x.dxf", room), options with { DoorSillMillimetres = 100 })), "review door sill other than 0 refused");
            C(ThrowsArgument(() => CadToBimIr.FromDxf(Drawing("x.dxf", room), options with { WindowHeightMillimetres = 2000 })), "review window sill plus height above wall height refused");
            C(ThrowsArgument(() => CadToBimIr.FromDxf(Drawing("x.dxf", room), options with { DoorHeightMillimetres = 2800 })), "review door height above wall height refused");
            C(ThrowsArgument(() => CadToBimIr.FromDxf(Drawing("x.dxf", room), options with { MinThicknessMillimetres = 0 })), "review zero MinThickness refused");
            C(ThrowsArgument(() => CadToBimIr.FromDxf(Drawing("x.dxf", room), options with { MinOverlapMillimetres = 10 })), "review MinOverlap not above JoinTolerance refused");

            var dup = CadToBimIr.FromDxf(Drawing("dup.dxf", Lines((0, 0, 3000, 0), (0, 0.005, 3000, 0.005), (0, 200, 3000, 200))), options with { MinThicknessMillimetres = 0.001 });
            C(dup.Walls.Count == 1 && Near(dup.Walls[0].ThicknessMillimetres, 200) && dup.Walls.All(w => w.ThicknessMillimetres > options.JoinToleranceMillimetres) && Core.Compiler.Validate(dup.ToProject()).Length == 0,
                "review duplicated face line never gives a zero-thickness wall", string.Join(" | ", dup.Walls.Select(Wall)));

            var tee = CadToBimIr.FromDxf(Drawing("tee.dxf", Lines((0, 0, 6000, 0), (200, 200, 2900, 200), (3000, 200, 5800, 200), (2900, 200, 2900, 3000), (3000, 200, 3000, 3000))), options);
            C(tee.Walls.Count == 2 && NearP(tee.Walls[0].Start, 200, 100) && NearP(tee.Walls[0].End, 5800, 100) && tee.Walls[0].Notes.Contains("merged-shared-face")
                && NearP(tee.Walls[1].Start, 2950, 100) && tee.Walls[1].Notes.Contains("start:joined") && Near(tee.Walls[1].ThicknessMillimetres, 100),
                "review exterior wall split at a tee on its inner face stays one wall and the partition joins it", string.Join(" | ", tee.Walls.Select(Wall)));
        }
        catch (Exception error)
        {
            C(false, "review rules unexpected " + error.GetType().Name, error.ToString());
        }

        // 6-7. Early bounds and non-finite input.
        try
        {
            PdfProvenance Pv(int op) => new("bound.pdf", 1, 4, op, "S");
            var lines = new List<PdfElement>(300_000);
            for (var k = 0; k < 300_000; k++)
                lines.Add(new PdfPath(Pv(k), new[] { new PdfSubpath(new Point2(0, k), new[] { new PdfSegment(PdfSegmentKind.Line, new Point2(0, k), new Point2(0, k), new Point2(10, k), new Point2(10, k), k, "l") }, false) }, true, false, false, 1));
            var bigDoc = new PdfDocument("bound.pdf", "1.7", new[] { new PdfPage(1, 3, new PdfRect(0, 0, 600, 400), 0, lines) }, Array.Empty<PdfWarning>());
            var unit = PdfCalibration.FromKnownDimension(new Point2(0, 0), new Point2(1, 0), 1);
            var watch = Stopwatch.StartNew();
            var code = CodeOf(() => CadToBimIr.FromPdf(bigDoc, 1, unit, options with { MaxWork = 100_000 }));
            watch.Stop();
            C(code == "draft-limit" && watch.ElapsedMilliseconds < 3000, "review 300k one-segment paths with MaxWork 100000 fail fast", code + " " + watch.ElapsedMilliseconds + " ms");
            var segmentMessage = MessageOf(() => CadToBimIr.FromPdf(bigDoc, 1, unit, options with { MaxSegments = 1000 }));
            C(segmentMessage is not null && segmentMessage.StartsWith("draft-limit: CAD draft stopped: more than 1000 candidate segments", StringComparison.Ordinal), "review MaxSegments enforced while registering segments", segmentMessage ?? "null");

            var curves = new List<PdfSubpath>();
            for (var k = 0; k < 2000; k++)
                curves.Add(new PdfSubpath(new Point2(0, k), new[] { new PdfSegment(PdfSegmentKind.Cubic, new Point2(0, k), new Point2(3, k + 1), new Point2(6, k + 1), new Point2(9, k), 1, "c") }, false));
            var curveDoc = new PdfDocument("curves.pdf", "1.7", new[] { new PdfPage(1, 3, new PdfRect(0, 0, 600, 400), 0, new PdfElement[] { new PdfPath(Pv(2), curves, true, false, false, 1) }) }, Array.Empty<PdfWarning>());
            var curveMessage = MessageOf(() => CadToBimIr.FromPdf(curveDoc, 1, unit, options with { MaxWork = 100_000, CurveSegments = 1024 }));
            C(curveMessage is not null && curveMessage.Contains("work units", StringComparison.Ordinal), "review flattened curve points are charged to MaxWork", curveMessage ?? "null");

            var drawing = DxfReader.ReadFile(Path.Combine(fixtures, "draft-plan.dxf"));
            CadDraft? huge = null;
            var hugeError = MessageOf(() => huge = CadToBimIr.FromDxf(drawing, options with { MillimetresPerUnit = 1e306 }));
            string? json = null;
            try { if (huge is not null) json = JsonSerializer.Serialize(huge); } catch (Exception error) { hugeError = error.GetType().Name + ": " + error.Message; }
            C(hugeError is null && json is not null && huge is not null && AllFinite(huge), "review overflowing scale gives a finite draft that serializes", hugeError ?? "ok");
            var doc = PdfReader.ReadFile(Path.Combine(fixtures, "draft-plan.pdf"));
            C(CodeOf(() => CadToBimIr.FromPdf(doc, 1, new PdfCalibration(double.NaN, PdfMatrix.Identity), options)) == "uncalibrated", "review NaN PDF calibration refused as uncalibrated");
            C(CodeOf(() => CadToBimIr.FromPdf(doc, 1, new PdfCalibration(1, new PdfMatrix(double.PositiveInfinity, 0, 0, 1, 0, 0)), options)) == "uncalibrated", "review non-finite PDF calibration matrix refused as uncalibrated");
        }
        catch (Exception error)
        {
            C(false, "review bounds unexpected " + error.GetType().Name, error.ToString());
        }
    }

    private static bool ThrowsArgument(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
        catch (Exception) { return false; }
    }
}
