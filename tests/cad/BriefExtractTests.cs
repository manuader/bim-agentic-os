using Bim.Cad;
using Bim.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;

// P6-05 brief extraction oracle: the hand-written brief-plan.dxf (millimetres) and a generated PDF page (calibrated at
// 50 mm per PDF unit, and uncalibrated) become a P6-03 brief draft whose rooms come from room labels and area texts
// (es/en, m2/m²/sqm, decimal comma and point, thousands separators) with per-room confidence, source provenance and an
// uncertainty report (unlabelled closed regions, area/label mismatch, duplicate labels and texts, orphan areas). The
// brief validates with BriefValidator and round-trips through BriefSerializer/BriefParser. Generated drawings check
// shared regions, vocabulary gating, block texts, bounds and malformed text. Unit-tested evidence only.
internal static class BriefExtractTests
{
    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static bool Near(double a, double b, double eps = 1e-9) => Math.Abs(a - b) <= eps;
    private static bool Near(double? a, double b, double eps = 1e-9) => a is { } v && Near(v, b, eps);
    private static string Room(BriefRoomProposal r) => r.Id + " '" + r.Name + "' s" + (r.StatedAreaSquareMetres is { } s ? F(s) : "-") + " r" + (r.RegionAreaSquareMetres is { } g ? F(g) : "-") + " c" + F(r.Confidence) + " [" + string.Join(",", r.Notes) + "]";
    private static string Rooms(BriefDraft d) => string.Join(" | ", d.Rooms.Select(Room));
    private static string Codes(BriefDraft d) => string.Join(",", d.Uncertainties.Select(u => u.Code));
    private static string Issues(BriefDraft d) => string.Join(",", d.Issues.Select(i => i.Code + "@" + i.Field));

    private static string G(string spec) => string.Concat(spec.Split('|').Select(item => { var space = item.IndexOf(' ', StringComparison.Ordinal); return item[..space] + "\n" + item[(space + 1)..] + "\n"; }));
    private static string Doc(string entities, string blocks = "") =>
        G("0 SECTION|2 HEADER|9 $INSUNITS|70 4|0 ENDSEC")
        + (blocks.Length > 0 ? G("0 SECTION|2 BLOCKS") + blocks + G("0 ENDSEC") : "")
        + G("0 SECTION|2 ENTITIES") + entities + G("0 ENDSEC|0 EOF");
    private static string Rect(string handle, double x0, double y0, double x1, double y1, string layer = "ROOMS", string extra = "") =>
        G("0 LWPOLYLINE|5 " + handle + "|8 " + layer + (extra.Length > 0 ? "|" + extra : "") + "|90 4|70 1|10 " + F(x0) + "|20 " + F(y0) + "|10 " + F(x1) + "|20 " + F(y0) + "|10 " + F(x1) + "|20 " + F(y1) + "|10 " + F(x0) + "|20 " + F(y1));
    private static string Text(string handle, double x, double y, string value, double height = 200, string layer = "ROOM-TEXT", string extra = "") =>
        G("0 TEXT|5 " + handle + "|8 " + layer + (extra.Length > 0 ? "|" + extra : "") + "|10 " + F(x) + "|20 " + F(y) + "|40 " + F(height) + "|1 " + value);
    private static BriefDraft Dxf(string entities, BriefExtractOptions? options = null, string blocks = "") =>
        BriefExtract.FromDxf(DxfReader.Parse(Doc(entities, blocks), "gen.dxf"), options);

    private static byte[] L(string text) => Encoding.Latin1.GetBytes(text);
    private static string PdfText(double x, double y, string value, double size = 4) => "BT\n/F1 " + F(size) + " Tf\n1 0 0 1 " + F(x) + " " + F(y) + " Tm\n(" + value + ") Tj\nET\n";

    private static string? Unexpected(Func<object?> action)
    {
        try { action(); return null; }
        catch (Exception error) { return error.GetType().Name + ": " + error.Message; }
    }

    private static Type? ErrorType(Func<object?> action)
    {
        try { action(); return null; }
        catch (Exception error) { return error.GetType(); }
    }

    public static void Run(Action<bool, string, string> check, string fixtures)
    {
        void C(bool condition, string name, string detail = "") => check(condition, "brief " + name, detail);

        // ---------- text reading: es/en, units, decimal comma, thousands, prefixes ----------
        try
        {
            void Area(string text, double expected, string? label = null)
            {
                var r = BriefExtract.ReadText(text);
                C(Near(r.AreaSquareMetres, expected) && r.Label == label, "read '" + text.Replace("\n", "\\n", StringComparison.Ordinal) + "'", (r.Label ?? "null") + " " + (r.AreaSquareMetres is { } a ? F(a) : "null") + " " + (r.AreaToken ?? "null"));
            }
            Area("12,50 m²", 12.5);
            Area("12.50 m2", 12.5);
            Area("Sup. 1.234,5 m²", 1234.5);
            Area("1,234.5 sqm", 1234.5);
            Area("9 m^2", 9);
            Area("Superficie: 7,25m²", 7.25);
            Area("Área = 3,1 M2", 3.1);
            Area("S: 4 m²", 4);
            Area("Kitchen 9.5 sq m", 9.5, "Kitchen");
            Area("Dormitorio 1\n12,0 m²", 12, "Dormitorio 1");
            Area("Dormitorio A 12 m²", 12, "Dormitorio A");
            Area("COCINA Sup. 9 m²", 9, "COCINA");
            Area("Living (24,75 m²)", 24.75, "Living");
            var token = BriefExtract.ReadText("Baño 4,00 m²");
            C(token.Label == "Baño" && Near(token.AreaSquareMetres, 4) && token.AreaToken == "4,00 m²", "read keeps accents and the area token", token.ToString());
            var plain = BriefExtract.ReadText("12");
            C(plain.Label is null && plain.AreaSquareMetres is null, "read bare number is neither label nor area", plain.ToString());
            var scale = BriefExtract.ReadText("ESC 1:100");
            C(scale.Label == "ESC 1:100" && scale.AreaSquareMetres is null, "read scale text is a label candidate without area", scale.ToString());
            var metres = BriefExtract.ReadText("3 x 4 m");
            C(metres.AreaSquareMetres is null, "read lengths in metres are not an area", metres.ToString());
            var glued = BriefExtract.ReadText("12m2x");
            C(glued.AreaSquareMetres is null, "read unit followed by a letter is not an area", glued.ToString());
            var commas = BriefExtract.ReadText("12,5,3 m²");
            C(commas.AreaSquareMetres is null, "read malformed number list is not an area", commas.ToString());
            var blank = BriefExtract.ReadText("   ");
            var none = BriefExtract.ReadText(null);
            C(blank.Label is null && blank.AreaSquareMetres is null && none.Label is null && none.AreaSquareMetres is null, "read blank and null");
            var lone = BriefExtract.ReadText("\uD800 \u0000Hall\u0007 12 m²");
            C(Near(lone.AreaSquareMetres, 12) && lone.Label is { } loneLabel && BriefParser.IsText(loneLabel) && !loneLabel.Contains('\u0000'), "read lone surrogate and control characters sanitised", lone.ToString());
            var huge = new string('9', 100_000) + " m²";
            C(Unexpected(() => BriefExtract.ReadText(huge)) is null && BriefExtract.ReadText(huge).AreaSquareMetres is null, "read oversized text is bounded and does not throw");
            var words = BriefExtract.ReadText("uno dos tres cuatro cinco seis siete");
            C(words.Label is null, "read label with more than MaxLabelWords words is refused", words.ToString());
        }
        catch (Exception error) { C(false, "read unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- brief-plan.dxf ----------
        try
        {
            var drawing = DxfReader.ReadFile(Path.Combine(fixtures, "brief-plan.dxf"));
            var d = BriefExtract.FromDxf(drawing);
            C(d.SourceFile == "brief-plan.dxf" && d.Format == "dxf" && Near(d.MetresPerUnit, 0.001), "dxf draft header", d.SourceFile + " " + d.Format + " " + d.MetresPerUnit);
            C(d.Rooms.Select(r => r.Id).SequenceEqual(new[] { "dormitorio-1", "cocina", "bano", "living-room", "bano-2", "lavadero", "hall" }), "dxf seven rooms in label order with unique slug ids", Rooms(d));
            if (d.Rooms.Count == 7)
            {
                var bed = d.Rooms[0];
                C(bed.Name == "DORMITORIO 1" && Near(bed.StatedAreaSquareMetres, 12) && Near(bed.RegionAreaSquareMetres, 12, 1e-9) && Near(bed.Confidence, 0.95) && bed.Notes.SequenceEqual(new[] { "area-nearby" }),
                    "dxf dormitorio paired with the nearby decimal-comma area, region 12 m2", Room(bed));
                C(bed.Label.Handle == "200" && bed.Label.Line == 223 && bed.Label.Layer == "ROOM-TEXT" && bed.Label.EntityType == "TEXT" && bed.Label.Format == "dxf" && bed.Label.SourceFile == "brief-plan.dxf"
                    && bed.Label.Page is null && bed.Label.Text == "DORMITORIO 1" && bed.Label.At == new Point2(1000, 1800),
                    "dxf dormitorio label provenance (file, handle, line, layer, type, point)", bed.Label.ToString());
                C(bed.Area is { Handle: "201", Text: "12,00 m²" } && bed.Region is { Handle: "100", EntityType: "LWPOLYLINE", Layer: "ROOMS", Text: null } && bed.Region.At == new Point2(2000, 1500),
                    "dxf dormitorio area and region provenance", bed.Area + " " + bed.Region);
                var kitchen = d.Rooms[1];
                C(kitchen.Name == "COCINA" && Near(kitchen.StatedAreaSquareMetres, 9.5) && Near(kitchen.RegionAreaSquareMetres, 9) && Near(kitchen.Confidence, 1) && kitchen.Notes.SequenceEqual(new[] { "area-same-text" })
                    && kitchen.Label.EntityType == "MTEXT" && kitchen.Label.Handle == "203" && kitchen.Area?.Handle == "203",
                    "dxf MTEXT label and area in one text (formatting and \\U+00B2 decoded), 5.6% off stays within tolerance", Room(kitchen));
                var bath = d.Rooms[2];
                C(bath.Name == "BAÑO" && Near(bath.StatedAreaSquareMetres, 6.5) && Near(bath.RegionAreaSquareMetres, 4) && Near(bath.Confidence, 0.95 * 0.6 * 0.8)
                    && bath.Notes.SequenceEqual(new[] { "area-nearby", "area-mismatch", "duplicate-label" }) && bath.Area?.Handle == "205",
                    "dxf baño stated 6.50 m2 in a 4 m2 region is an area mismatch and a duplicate label", Room(bath));
                var living = d.Rooms[3];
                C(living.Name == "Living room" && Near(living.StatedAreaSquareMetres, 30) && Near(living.RegionAreaSquareMetres, 30) && Near(living.Confidence, 0.95), "dxf english label with m2", Room(living));
                var bath2 = d.Rooms[4];
                C(bath2.Name == "Baño" && Near(bath2.StatedAreaSquareMetres, 4) && Near(bath2.Confidence, 0.95 * 0.8) && bath2.Notes.SequenceEqual(new[] { "area-nearby", "duplicate-label" }), "dxf second baño keeps its own room with suffix id", Room(bath2));
                var laundry = d.Rooms[5];
                C(laundry.Name == "Lavadero" && Near(laundry.StatedAreaSquareMetres, 3.2) && laundry.RegionAreaSquareMetres is null && laundry.Region is null && Near(laundry.Confidence, 0.95 * 0.9)
                    && laundry.Notes.SequenceEqual(new[] { "area-nearby", "no-region" }), "dxf vocabulary label outside every region paired by text-height distance", Room(laundry));
                var hall = d.Rooms[6];
                C(hall.Name == "Hall" && hall.StatedAreaSquareMetres is null && Near(hall.RegionAreaSquareMetres, 6) && hall.Area is null && Near(hall.Confidence, 0.8) && hall.Notes.SequenceEqual(new[] { "area-from-region" }),
                    "dxf label without area takes its region area as an inferred target", Room(hall));
            }

            var brief = d.Brief;
            C(brief.Version == 1 && brief.Name == "Brief draft from brief-plan.dxf" && brief.Adjacencies.Count == 0 && brief.Rooms.Count == 7, "dxf brief header", brief.Name);
            if (brief.Rooms.Count == 7)
            {
                var r0 = brief.Rooms[0];
                C(r0.Id == "dormitorio-1" && r0.Name == "DORMITORIO 1" && r0.TargetAreaSquareMetres is { } t0 && Near(t0.Value, 12) && t0.Provenance is { Source: BriefSource.Document } p0 && Near(p0.Confidence, 0.95)
                    && p0.Reference is { } ref0 && ref0.Contains("brief-plan.dxf", StringComparison.Ordinal) && ref0.Contains("201", StringComparison.Ordinal) && p0.Page is null
                    && r0.Provenance is { Source: BriefSource.Document, Reference: { } roomRef } && roomRef.Contains("200", StringComparison.Ordinal) && roomRef.Contains("DORMITORIO 1", StringComparison.Ordinal)
                    && r0.Count is null && r0.MinAreaSquareMetres is null && r0.MaxAreaSquareMetres is null && r0.PreferredLevel is null,
                    "dxf brief room carries target area and provenance (document source, confidence, reference)", BriefSerializer.Serialize(brief));
                var r6 = brief.Rooms[6];
                C(r6.TargetAreaSquareMetres is { } t6 && Near(t6.Value, 6) && t6.Provenance is { Source: BriefSource.Inferred } p6 && p6.Reference is { } ref6 && ref6.Contains("106", StringComparison.Ordinal),
                    "dxf region-derived target is marked inferred", r6.ToString());
            }
            C(d.Issues.Count == 0 && BriefValidator.Validate(brief).Length == 0, "dxf brief passes BriefValidator", Issues(d));
            var json = BriefSerializer.Serialize(brief);
            C(BriefSerializer.Serialize(BriefParser.Parse(json)) == json, "dxf brief round-trips through BriefSerializer and BriefParser", json);

            var u = d.Uncertainties;
            C(Codes(d) == "area-from-region,area-mismatch,duplicate-label,duplicate-text,orphan-area,unlabelled-region", "dxf uncertainty report sorted by code", Codes(d));
            if (u.Count == 6)
            {
                C(u[0].RoomId == "hall" && Near(u[0].ValueSquareMetres, 6) && u[0].Sources.Select(s => s.Handle).SequenceEqual(new[] { "212", "106" }), "dxf area-from-region names the label and region", u[0].ToString());
                C(u[1].RoomId == "bano" && Near(u[1].ValueSquareMetres, 4) && u[1].Sources.Select(s => s.Handle).SequenceEqual(new[] { "204", "205", "102" }), "dxf area-mismatch names label, area and region", string.Join(",", u[1].Sources.Select(s => s.Handle)));
                C(u[2].RoomId == "bano-2" && u[2].Sources.Select(s => s.Handle).SequenceEqual(new[] { "204", "208" }), "dxf duplicate-label names both labels", string.Join(",", u[2].Sources.Select(s => s.Handle)));
                C(u[3].RoomId is null && u[3].Sources.Select(s => s.Handle).SequenceEqual(new[] { "202", "200" }), "dxf duplicate-text names the dropped copy then the kept text", string.Join(",", u[3].Sources.Select(s => s.Handle)));
                C(u[4].RoomId is null && Near(u[4].ValueSquareMetres, 25) && u[4].Sources.Single().Handle == "213", "dxf orphan-area far from every label", u[4].ToString());
                C(u[5].RoomId is null && Near(u[5].ValueSquareMetres, 15) && u[5].Sources.Single().Handle == "104" && u[5].Sources[0].EntityType == "LWPOLYLINE", "dxf unlabelled closed region (column below the minimum area is ignored)", u[5].ToString());
            }

            var culture = CultureInfo.CurrentCulture;
            string jsonDe;
            try { CultureInfo.CurrentCulture = new CultureInfo("de-DE"); jsonDe = JsonSerializer.Serialize(BriefExtract.FromDxf(DxfReader.ReadFile(Path.Combine(fixtures, "brief-plan.dxf")))); }
            finally { CultureInfo.CurrentCulture = culture; }
            var jsonInvariant = JsonSerializer.Serialize(d);
            C(jsonInvariant == JsonSerializer.Serialize(BriefExtract.FromDxf(drawing)) && jsonInvariant == jsonDe, "dxf draft deterministic across runs and de-DE culture", jsonInvariant.Length + " " + jsonDe.Length);
            var named = BriefExtract.FromDxf(drawing, new BriefExtractOptions { Name = "Casa Ader" });
            C(named.Brief.Name == "Casa Ader", "dxf brief name option", named.Brief.Name);
        }
        catch (Exception error) { C(false, "dxf unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- generated PDF page ----------
        try
        {
            var content = "0.5 w\n100 100 80 60 re\nS\n180 100 60 60 re\nS\n180 160 60 40 re\nS\n"
                + PdfText(110, 140, "DORMITORIO 1") + PdfText(110, 125, "12,00 m²") + PdfText(190, 130, "KITCHEN 9.0 m2") + PdfText(400, 300, "25 m²");
            var document = PdfReader.Parse(PdfTests.OnePage(content), "brief-plan.pdf");
            var calibration = PdfCalibration.FromKnownDimension(new Point2(100, 100), new Point2(180, 100), 4000);
            var d = BriefExtract.FromPdf(document, 1, calibration);
            C(d.SourceFile == "brief-plan.pdf" && d.Format == "pdf" && Near(d.MetresPerUnit, 0.001), "pdf draft header (calibrated millimetres)", d.SourceFile + " " + d.MetresPerUnit);
            C(d.Rooms.Select(r => r.Id).SequenceEqual(new[] { "dormitorio-1", "kitchen" }), "pdf two rooms", Rooms(d));
            if (d.Rooms.Count == 2)
            {
                var bed = d.Rooms[0];
                C(Near(bed.StatedAreaSquareMetres, 12) && Near(bed.RegionAreaSquareMetres, 12, 1e-6) && Near(bed.Confidence, 0.95) && bed.Label.Page == 1 && bed.Label.Format == "pdf"
                    && bed.Label.EntityType == "PDF-TEXT" && bed.Label.Layer == "PDF-P1" && bed.Label.Handle is { } h && h.StartsWith("p1:o4:i", StringComparison.Ordinal)
                    && bed.Region is { EntityType: "PDF-PATH" } && bed.Label.At == new Point2(5500, 7000),
                    "pdf dormitorio with page provenance and calibrated region area", Room(bed) + " " + bed.Label);
                var kitchen = d.Rooms[1];
                C(kitchen.Name == "KITCHEN" && Near(kitchen.StatedAreaSquareMetres, 9) && Near(kitchen.RegionAreaSquareMetres, 9, 1e-6) && Near(kitchen.Confidence, 1), "pdf english label with area in one string", Room(kitchen));
                C(d.Brief.Rooms[0].Provenance is { Page: 1 } && d.Brief.Rooms[0].TargetAreaSquareMetres?.Provenance is { Page: 1 }, "pdf brief provenance carries the page");
            }
            C(Codes(d) == "orphan-area,unlabelled-region" && d.Uncertainties[^1].Sources.Single().EntityType == "PDF-PATH" && Near(d.Uncertainties[^1].ValueSquareMetres, 6, 1e-6), "pdf orphan area and unlabelled region", Codes(d));
            C(d.Issues.Count == 0, "pdf brief passes BriefValidator", Issues(d));

            var raw = BriefExtract.FromPdf(document, 1);
            C(raw.MetresPerUnit is null && raw.Rooms.Count == 2 && raw.Rooms.All(r => r.RegionAreaSquareMetres is null && r.Region is not null) && Near(raw.Rooms[0].StatedAreaSquareMetres, 12)
                && Codes(raw) == "orphan-area,unlabelled-region" && raw.Uncertainties[^1].ValueSquareMetres is null && raw.Issues.Count == 0,
                "pdf uncalibrated: regions still hold labels but have no area, so no mismatch", Rooms(raw) + " " + Codes(raw));
            C(ErrorType(() => BriefExtract.FromPdf(document, 2)) == typeof(ArgumentOutOfRangeException), "pdf missing page refused");
        }
        catch (Exception error) { C(false, "pdf unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- generated DXF: regions, vocabulary, blocks ----------
        try
        {
            var shared = Dxf(Rect("1", 0, 0, 6000, 4000) + Text("2", 1000, 2000, "Cocina 10 m²") + Text("3", 4000, 2000, "Comedor 14 m²"));
            C(shared.Rooms.Select(r => r.Id).SequenceEqual(new[] { "cocina", "comedor" }) && shared.Rooms.All(r => r.RegionAreaSquareMetres is null && r.Region is null && Near(r.Confidence, 0.9) && r.Notes.Contains("shared-region"))
                && Codes(shared) == "shared-region" && Near(shared.Uncertainties[0].ValueSquareMetres, 24) && shared.Uncertainties[0].Sources.Select(s => s.Handle).SequenceEqual(new[] { "1", "2", "3" }),
                "gen two labels in one region share it (no region area, reported once)", Rooms(shared) + " " + Codes(shared));

            var gated = Dxf(Rect("1", 0, 0, 3000, 3000) + Text("2", 500, 2000, "Dormitorio") + Text("3", 500, 1500, "N.P.T. +0,15") + Text("4", 500, 1000, "9 m²")
                + Rect("5", 3000, 0, 6000, 3000) + Text("6", 3500, 2000, "Taller X") + Text("7", 0, -1000, "PLANTA ALTA"));
            C(gated.Rooms.Select(r => r.Id + ":" + r.Name).SequenceEqual(new[] { "dormitorio:Dormitorio", "taller-x:Taller X" }), "gen vocabulary label wins its region; other region keeps its only text; title outside is ignored", Rooms(gated));
            if (gated.Rooms.Count == 2)
            {
                C(Near(gated.Rooms[0].StatedAreaSquareMetres, 9) && Near(gated.Rooms[0].Confidence, 0.95), "gen vocabulary room paired inside its region", Room(gated.Rooms[0]));
                C(Near(gated.Rooms[1].Confidence, 0.7 * 0.8) && gated.Rooms[1].Notes.SequenceEqual(new[] { "label-outside-vocabulary", "area-from-region" }) && Near(gated.Rooms[1].RegionAreaSquareMetres, 9),
                    "gen non-vocabulary label alone in a region is a low-confidence room", Room(gated.Rooms[1]));
            }

            var missing = Dxf(Text("1", 0, 0, "Cocina") + Text("2", 50000, 0, "0,00 m²") + Text("3", 0, 0, "Estar", extra: "67 1"));
            C(missing.Rooms.Count == 1 && missing.Rooms[0].Id == "cocina" && missing.Rooms[0].StatedAreaSquareMetres is null && Near(missing.Rooms[0].Confidence, 0.9 * 0.6)
                && missing.Rooms[0].Notes.SequenceEqual(new[] { "no-region", "area-missing" }) && missing.Brief.Rooms[0].TargetAreaSquareMetres is null && Codes(missing) == "area-invalid,area-missing" && missing.Issues.Count == 0,
                "gen label without area or region, zero area refused, paper-space text skipped", Rooms(missing) + " " + Codes(missing));

            var blocks = G("0 BLOCK|5 B1|8 0|2 TAG|70 0|10 0|20 0|30 0") + Text("B2", 0, 0, "Estudio", layer: "0") + Text("B3", 0, -400, "4 m²", layer: "0") + G("0 ENDBLK|5 B4|8 0");
            var tagged = Dxf(Rect("1", 0, 0, 2000, 2000) + G("0 INSERT|5 I1|8 TAGS|2 TAG|10 500|20 1500|30 0"), blocks: blocks);
            C(tagged.Rooms.Count == 1 && tagged.Rooms[0].Id == "estudio" && tagged.Rooms[0].Label.InsertPath.SequenceEqual(new[] { "I1" }) && tagged.Rooms[0].Label.Layer == "TAGS"
                && tagged.Rooms[0].Label.At == new Point2(500, 1500) && Near(tagged.Rooms[0].StatedAreaSquareMetres, 4) && Near(tagged.Rooms[0].RegionAreaSquareMetres, 4),
                "gen texts inside a block insert are read with the insert path", Rooms(tagged));

            var hidden = Dxf(Rect("1", 0, 0, 2000, 2000, extra: "60 1") + Rect("2", 3000, 0, 5000, 2000, layer: "OTHER") + Text("3", 500, 500, "Cocina 4 m²"),
                new BriefExtractOptions { RegionLayers = new[] { "rooms" } });
            C(hidden.Rooms.Count == 1 && hidden.Rooms[0].Region is null && Codes(hidden) == "", "gen invisible regions and regions off RegionLayers are ignored", Rooms(hidden) + " " + Codes(hidden));

            var bulge = Dxf(G("0 LWPOLYLINE|5 1|8 ROOMS|90 2|70 1|10 -1000|20 0|42 1|10 1000|20 0|42 1") + Text("2", 0, 0, "Patio"));
            C(bulge.Rooms.Count == 1 && Near(bulge.Rooms[0].RegionAreaSquareMetres, Math.PI, 1e-9), "gen bulged closed polyline (a circle of radius 1 m) has its exact area", Rooms(bulge));

            var empty = Dxf("");
            C(empty.Rooms.Count == 0 && empty.Brief.Rooms.Count == 0 && empty.Issues.Any(i => i.Code == BriefCodes.RoomsEmpty) && empty.Uncertainties.Count == 0, "gen empty drawing gives an empty brief the validator flags", Issues(empty));
        }
        catch (Exception error) { C(false, "gen unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- bounds, options and malformed text ----------
        try
        {
            var many = string.Concat(Enumerable.Range(0, 5).Select(i => Text("T" + i.ToString(CultureInfo.InvariantCulture), i * 10000, 0, "Cocina " + (i + 1).ToString(CultureInfo.InvariantCulture))));
            var limited = Dxf(many, new BriefExtractOptions { MaxTexts = 3 });
            C(limited.Rooms.Count == 3 && Codes(limited).Contains("text-limit", StringComparison.Ordinal), "bounds MaxTexts stops reading and reports text-limit", Rooms(limited) + " " + Codes(limited));
            var tooLong = Dxf(Text("1", 0, 0, "Cocina " + new string('x', 300)));
            C(tooLong.Rooms.Count == 0 && Codes(tooLong) == "text-too-long", "bounds text longer than MaxTextLength is skipped and reported", Codes(tooLong));
            var orphans = Dxf(string.Concat(Enumerable.Range(0, 5).Select(i => Text("A" + i.ToString(CultureInfo.InvariantCulture), i * 10000, 0, "5 m²"))), new BriefExtractOptions { MaxUncertainties = 2 });
            C(Codes(orphans) == "orphan-area,orphan-area,uncertainty-limit", "bounds MaxUncertainties caps the report with uncertainty-limit last", Codes(orphans));
            var regions = Dxf(Rect("1", 0, 0, 2000, 2000) + Rect("2", 3000, 0, 5000, 2000) + Text("3", 500, 500, "Cocina"), new BriefExtractOptions { MaxRegions = 1 });
            C(Codes(regions) == "area-from-region,region-limit", "bounds MaxRegions stops reading regions and reports region-limit", Codes(regions));
            var work = Dxf(Rect("1", 0, 0, 2000, 2000) + Text("2", 500, 500, "Cocina") + Text("3", 500, 300, "4 m²"), new BriefExtractOptions { MaxWork = 3 });
            C(Codes(work).Contains("work-limit", StringComparison.Ordinal), "bounds MaxWork stops the extraction and reports work-limit", Codes(work));
            C(ErrorType(() => Dxf("", new BriefExtractOptions { PairDistanceTextHeights = -1 })) == typeof(ArgumentOutOfRangeException)
                && ErrorType(() => Dxf("", new BriefExtractOptions { MaxTexts = 0 })) == typeof(ArgumentOutOfRangeException)
                && ErrorType(() => Dxf("", new BriefExtractOptions { AreaMismatchTolerance = double.NaN })) == typeof(ArgumentOutOfRangeException),
                "bounds invalid options are refused");

            // Mutation fuzz: arbitrary text values never throw and always give ids and names the brief validator accepts.
            var random = new Random(605);
            const string alphabet = "aAñÑéÉ0123456789.,:;- ²m2sqMSup()\\{}%\t";
            var fuzzFailures = new List<string>();
            for (var round = 0; round < 200; round++)
            {
                var entities = new StringBuilder(Rect("R", 0, 0, 5000, 5000));
                for (var t = 0; t < 6; t++)
                {
                    var length = random.Next(0, 40);
                    var value = new string(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray()).Trim();
                    if (value.Length == 0) value = "x";
                    var word = random.Next(4) switch { 0 => "Cocina ", 1 => "Bedroom ", 2 => "Baño ", _ => "" };
                    entities.Append(Text("F" + t.ToString(CultureInfo.InvariantCulture), random.Next(-2000, 7000), random.Next(-2000, 7000), word + value, random.Next(0, 300)));
                }
                var error = Unexpected(() =>
                {
                    var fd = Dxf(entities.ToString());
                    if (fd.Brief.Rooms.Any(r => !BriefParser.IsRoomId(r.Id) || !BriefParser.IsText(r.Name))) throw new InvalidOperationException("bad id or name");
                    if (fd.Issues.Any(i => i.Code != BriefCodes.RoomsEmpty)) throw new InvalidOperationException("issues " + string.Join(",", fd.Issues.Select(i => i.Code)));
                    var serialized = BriefSerializer.Serialize(fd.Brief);
                    if (fd.Brief.Rooms.Count > 0 && BriefSerializer.Serialize(BriefParser.Parse(serialized)) != serialized) throw new InvalidOperationException("round trip");
                    JsonSerializer.Serialize(fd);
                    return fd;
                });
                if (error is not null) fuzzFailures.Add(round.ToString(CultureInfo.InvariantCulture) + ": " + error);
            }
            C(fuzzFailures.Count == 0, "fuzz 200 random drawings never throw and always validate", string.Join(" | ", fuzzFailures.Take(3)));
        }
        catch (Exception error) { C(false, "bounds unexpected", error.GetType().Name + ": " + error.Message); }
    }
}
