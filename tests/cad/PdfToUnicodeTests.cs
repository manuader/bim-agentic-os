using Bim.Cad;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

// P6-16 PDF ToUnicode oracle. The committed fixture tounicode.pdf (stdlib generator tounicode-pdf.py, bytes pinned here)
// shows room labels as CAD exporters do: a Type0 /Identity-H font with two-byte CIDs (hex Tj and TJ) and a simple
// TrueType subset font with codes 1..12, each with a ToUnicode CMap using bfchar and bfrange in both the incrementing and
// the array form. The strings decode to Unicode in the drawing model and BriefExtract reads the rooms, "Baño" included.
// Generated pages then cover a Type0 font without ToUnicode (today's decoding, tounicode-missing once per font and page),
// invalid CMaps (unterminated block, syntax error, no mappings, over the entry or byte bound, not a stream, dangling
// reference: today's decoding with tounicode-invalid), unmapped CIDs (U+FFFD with tounicode-unmapped), ligature and
// surrogate-pair destinations, an over-long destination skipped, a simple font falling back to Latin-1 for unmapped
// codes, q/Q restoring the font and a Form XObject with its own font resources. Unit-tested evidence only.
internal static class PdfToUnicodeTests
{
    private static string I(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static byte[] L(string text) => Encoding.Latin1.GetBytes(text);
    private static string Warnings(PdfDocument document) => string.Join(";", document.Warnings.Select(w => w.Code + "@" + I(w.Page) + ":" + I(w.ObjectNumber) + ":" + I(w.OperatorIndex)));
    private static string Escape(string text) => string.Concat(text.Select(c => c < 0x20 || c > 0x7E ? "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture) : c.ToString()));
    private static string Texts(PdfDocument document) => string.Join("|", document.Pages.SelectMany(p => p.Elements).OfType<PdfText>().Select(t => Escape(t.Value)));

    private const string Type0 = "<< /Type /Font /Subtype /Type0 /BaseFont /AAAAAB+ArialMT /Encoding /Identity-H /DescendantFonts [] /ToUnicode 6 0 R >>";
    private const string Type0Bare = "<< /Type /Font /Subtype /Type0 /BaseFont /AAAAAB+ArialMT /Encoding /Identity-H /DescendantFonts [] >>";
    private const string Simple = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 6 0 R >>";

    internal static string CMap(string body, string codespace = "<0000> <FFFF>") =>
        "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n"
        + "/CMapName /Test-UCS def\n/CMapType 2 def\n1 begincodespacerange\n" + codespace + "\nendcodespacerange\n" + body
        + "\nendcmap\nCMapName currentdict /CMapResource defineresource pop\nend\nend\n";

    // Page 3 with /F1 = object 5 (font) whose ToUnicode is object 6 (stream, when given) and /F2 = object 7 (plain Type1);
    // extra objects (a Form XObject) and page resources are appended.
    internal static PdfDocument Page(string content, string font, string? toUnicode, string resources = "", params (int Number, byte[] Body)[] extra) =>
        PdfReader.Parse(PageBytes(content, font, toUnicode is null ? null : PdfTests.StreamBody("", L(toUnicode)), resources, null, extra), "tounicode-gen.pdf");

    // As Page, with the ToUnicode object given as a whole stream body (filters) and /F2 replaceable.
    internal static byte[] PageBytes(string content, string font, byte[]? toUnicodeBody, string resources = "", string? font2 = null, params (int Number, byte[] Body)[] extra)
    {
        var objects = new List<(int Number, byte[] Body)>
        {
            (1, L("<< /Type /Catalog /Pages 2 0 R >>")),
            (2, L("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")),
            (3, L("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 400] /Resources << /Font << /F1 5 0 R /F2 7 0 R >> " + resources + " >> /Contents 4 0 R >>")),
            (4, PdfTests.StreamBody("", L(content))),
            (5, L(font)),
            (7, L(font2 ?? "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")),
        };
        if (toUnicodeBody is not null) objects.Add((6, toUnicodeBody));
        objects.AddRange(extra);
        return PdfTests.Build(objects.OrderBy(o => o.Number));
    }

    private static string? CodeOf(Func<object?> action)
    {
        try { action(); return null; }
        catch (PdfFormatException error) { return error.Code; }
        catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
    }

    // One page showing one string with each of count Type0 fonts /F0.. (objects 100+i), each with its own ToUnicode CMap
    // (objects 300+i) mapping codes <0000>..<span-1> by an incrementing bfrange.
    private static byte[] ManyFonts(int count, int span)
    {
        var fonts = string.Concat(Enumerable.Range(0, count).Select(i => "/F" + I(i) + " " + I(100 + i) + " 0 R "));
        var content = "BT 1 0 0 1 50 50 Tm " + string.Concat(Enumerable.Range(0, count).Select(i => "/F" + I(i) + " 10 Tf <0001> Tj ")) + "ET";
        var objects = new List<(int Number, byte[] Body)>
        {
            (1, L("<< /Type /Catalog /Pages 2 0 R >>")),
            (2, L("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")),
            (3, L("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 400] /Resources << /Font << " + fonts + ">> >> /Contents 4 0 R >>")),
            (4, PdfTests.StreamBody("", L(content))),
        };
        for (var i = 0; i < count; i++)
        {
            objects.Add((100 + i, L(Type0.Replace("6 0 R", I(300 + i) + " 0 R", StringComparison.Ordinal))));
            objects.Add((300 + i, PdfTests.StreamBody("", L(CMap("1 beginbfrange\n<0000> <" + (span - 1).ToString("X4", CultureInfo.InvariantCulture) + "> <" + (0x41 + i).ToString("X4", CultureInfo.InvariantCulture) + ">\nendbfrange")))));
        }
        return PdfTests.Build(objects.OrderBy(o => o.Number));
    }

    private const string ShowAB = "BT /F1 10 Tf 1 0 0 1 50 50 Tm <00410042> Tj ET";

    public static void Run(Action<bool, string, string> check, string fixtures)
    {
        void C(bool condition, string name, string detail = "") => check(condition, "tounicode " + name, detail);

        // ---------- committed fixture ----------
        try
        {
            var path = Path.Combine(fixtures, "tounicode.pdf");
            var bytes = File.ReadAllBytes(path);
            var sha = Convert.ToHexString(SHA256.HashData(bytes));
            C(sha == "1BF8D030D51F3F75322154BDB43C37E207ABFF918510A5F4250597D5C8350E4B", "fixture bytes equal the committed generator output", sha);
            var document = PdfReader.ReadFile(path);
            C(document.Warnings.Count == 0, "fixture reads without warnings", Warnings(document));
            var texts = document.Pages[0].Elements.OfType<PdfText>().ToList();
            C(Texts(document) == "Dormitorio 1|12,00 m\\u00B2|Ba\\u00F1o|4,00 m\\u00B2|Cocina|9,00 m\\u00B2", "fixture Type0 Identity-H and simple-font strings decode through ToUnicode", Texts(document));
            C(string.Join(",", texts.Select(t => t.FontName + ":" + t.Provenance.Operator + I(t.Provenance.OperatorIndex))) == "F1:Tj22,F1:Tj27,F1:TJ32,F1:Tj37,F2:Tj42,F2:Tj47",
                "fixture text fonts and showing operators keep their provenance", string.Join(",", texts.Select(t => t.FontName + ":" + t.Provenance.Operator + I(t.Provenance.OperatorIndex))));

            var draft = BriefExtract.FromPdf(document, 1, null, new BriefExtractOptions { MetresPerUnit = 0.02 });
            var ids = string.Join(",", draft.Rooms.Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal));
            C(ids == "bano,cocina,dormitorio-1", "brief extraction reads the three room labels", ids + " " + string.Join(",", draft.Uncertainties.Select(u => u.Code)));
            var bano = draft.Rooms.FirstOrDefault(r => r.Id == "bano");
            C(bano is not null && bano.Name == "Baño" && bano.StatedAreaSquareMetres is { } a && Math.Abs(a - 4) < 1e-9 && bano.RegionAreaSquareMetres is { } ra && Math.Abs(ra - 4) < 1e-6,
                "non-ASCII label Baño keeps its name, stated area 4 m² and its 2 m x 2 m region", bano?.ToString() ?? "null");
            var bed = draft.Rooms.FirstOrDefault(r => r.Id == "dormitorio-1");
            var kitchen = draft.Rooms.FirstOrDefault(r => r.Id == "cocina");
            C(bed is not null && bed.Name == "Dormitorio 1" && bed.StatedAreaSquareMetres is { } b && Math.Abs(b - 12) < 1e-9
                && kitchen is not null && kitchen.Name == "Cocina" && kitchen.StatedAreaSquareMetres is { } k && Math.Abs(k - 9) < 1e-9,
                "Dormitorio 1 (Type0) and Cocina (simple font) carry their stated areas", (bed?.ToString() ?? "null") + " " + (kitchen?.ToString() ?? "null"));
            C(!draft.Uncertainties.Any(u => u.Code is "orphan-area" or "unlabelled-region" or "uncalibrated") && draft.Issues.Count == 0,
                "every area text pairs with a label and every region is a room", string.Join(",", draft.Uncertainties.Select(u => u.Code)));
        }
        catch (Exception error) { C(false, "fixture unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- existing fixtures keep today's decoding and warnings ----------
        try
        {
            foreach (var name in new[] { "simple-plan.pdf", "transforms.pdf", "flate.pdf", "draft-plan.pdf", "brief-plan.pdf" })
            {
                var document = PdfReader.ReadFile(Path.Combine(fixtures, name));
                C(!document.Warnings.Any(w => w.Code.StartsWith("tounicode", StringComparison.Ordinal)), name + " has no tounicode warning", Warnings(document));
            }
        }
        catch (Exception error) { C(false, "existing fixtures unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- missing ToUnicode on a Type0 font ----------
        try
        {
            var document = Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm <00410042> Tj /F1 10 Tf <0043> Tj ET", Type0Bare, null);
            C(Texts(document) == "\\u0000A\\u0000B|\\u0000C", "Type0 without ToUnicode keeps byte-per-character decoding", Texts(document));
            C(Warnings(document) == "tounicode-missing@1:4:1", "Type0 without ToUnicode warns tounicode-missing once per font and page at the first Tf", Warnings(document));
            var plain = Page("BT /F2 10 Tf 1 0 0 1 50 50 Tm (Ba\\361o) Tj ET", Type0Bare, null);
            C(Texts(plain) == "Ba\\u00F1o" && plain.Warnings.Count == 0, "a simple font without ToUnicode keeps Latin-1 and has no warning", Texts(plain) + " " + Warnings(plain));
            var unknown = Page("BT /F9 10 Tf 1 0 0 1 50 50 Tm (Hall) Tj ET", Type0Bare, null);
            C(Texts(unknown) == "Hall" && unknown.Warnings.Count == 0, "a font missing from the resources keeps Latin-1 and has no warning", Texts(unknown) + " " + Warnings(unknown));
        }
        catch (Exception error) { C(false, "missing unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- invalid CMaps keep today's decoding ----------
        try
        {
            var cases = new (string Name, string Font, string? Stream)[]
            {
                ("unterminated bfchar block", Type0, CMap("1 beginbfchar\n<0041> <0058>")),
                ("syntax error", Type0, CMap("1 beginbfchar\n<0041> (open")),
                ("no mappings", Type0, CMap("")),
                ("bfrange source of five bytes", Type0, CMap("1 beginbfrange\n<0000000041> <0000000042> <0058>\nendbfrange")),
                ("more than 65536 entries", Type0, CMap("1 beginbfrange\n<0000> <FFFF> <0041>\nendbfrange\n1 beginbfrange\n<0000> <00FF> <0041>\nendbfrange")),
                ("larger than 1 MiB", Type0, "%" + new string('x', 1 << 20) + "\n" + CMap("1 beginbfchar\n<0041> <0058>\nendbfchar")),
                ("not a stream", Type0.Replace("6 0 R", "/Identity-H", StringComparison.Ordinal), null),
                ("dangling reference", Type0.Replace("6 0 R", "9 0 R", StringComparison.Ordinal), null),
            };
            foreach (var (name, font, stream) in cases)
            {
                var document = Page(ShowAB, font, stream);
                C(Texts(document) == "\\u0000A\\u0000B" && Warnings(document) == "tounicode-invalid@1:4:1", "invalid CMap (" + name + ") keeps today's decoding with tounicode-invalid", Texts(document) + " " + Warnings(document));
            }
            var simple = Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm (AB) Tj ET", Simple, CMap("", "<00> <FF>"));
            C(Texts(simple) == "AB" && Warnings(simple) == "tounicode-invalid@1:4:1", "invalid CMap on a simple font keeps Latin-1 with tounicode-invalid", Texts(simple) + " " + Warnings(simple));
        }
        catch (Exception error) { C(false, "invalid unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- mapping details ----------
        try
        {
            var cmap = CMap("3 beginbfchar\n<0041> <0058>\n<0043> <0066006C>\n<0050> <D83DDE00>\nendbfchar\n1 beginbfrange\n<0060> <0062> [<00C1> <00C9>]\nendbfrange");
            var document = Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm <0041004200430050> Tj <004100> Tj [<0060> -250 <0061>] TJ <0062> Tj ET", Type0, cmap);
            C(Texts(document) == "X\\uFFFDfl\\uD83D\\uDE00|X\\uFFFD|\\u00C1 \\u00C9|\\uFFFD", "unmapped CID and trailing odd byte are U+FFFD; ligature and surrogate-pair destinations; short bfrange array maps what it lists",
                Texts(document));
            C(Warnings(document) == "tounicode-unmapped@1:4:3;tounicode-unmapped@1:4:4;tounicode-unmapped@1:4:6", "each show with an unmapped code warns tounicode-unmapped", Warnings(document));

            var longDestination = string.Concat(Enumerable.Repeat("0041", 33));
            var bounded = Page(ShowAB, Type0, CMap("2 beginbfchar\n<0041> <" + longDestination + ">\n<0042> <0059>\nendbfchar"));
            C(Texts(bounded) == "\\uFFFDY" && Warnings(bounded) == "tounicode-unmapped@1:4:3", "a destination over 64 bytes is skipped", Texts(bounded) + " " + Warnings(bounded));

            var increment = Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm <00100011001200130014> Tj ET", Type0, CMap("1 beginbfrange\n<0010> <0014> <0041>\nendbfrange"));
            C(Texts(increment) == "ABCDE" && increment.Warnings.Count == 0, "incrementing bfrange maps every code of the range", Texts(increment) + " " + Warnings(increment));

            var simple = Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm (AB) Tj [(A) -300 (B)] TJ ET", Simple, CMap("1 beginbfchar\n<41> <00DC>\nendbfchar", "<00> <FF>"));
            C(Texts(simple) == "\\u00DCB|\\u00DC B" && simple.Warnings.Count == 0, "simple font maps one-byte codes and falls back to Latin-1 for unmapped bytes", Texts(simple) + " " + Warnings(simple));

            var restore = Page("BT /F1 10 Tf q /F2 10 Tf Q 1 0 0 1 50 50 Tm <0041> Tj ET", Type0, CMap("1 beginbfchar\n<0041> <0058>\nendbfchar"));
            var restored = restore.Pages[0].Elements.OfType<PdfText>().SingleOrDefault();
            C(restored is not null && restored.Value == "X" && restored.FontName == "F1" && restore.Warnings.Count == 0, "Q restores the font and its ToUnicode map", Texts(restore) + " " + Warnings(restore));

            // The form has its own font object 10 (ToUnicode 11: <0041> -> Ñ); the page font maps <0041> -> X. X2 has no
            // resources and no Tf, so it shows with the font selected before Do.
            var form = PdfTests.StreamBody("/Type /XObject /Subtype /Form /BBox [0 0 600 400] /Resources << /Font << /F1 10 0 R >> >>", L("BT /F1 10 Tf 1 0 0 1 50 50 Tm <0041> Tj ET"));
            var bare = PdfTests.StreamBody("/Type /XObject /Subtype /Form /BBox [0 0 600 400]", L("BT 1 0 0 1 70 70 Tm <0041> Tj ET"));
            var nested = Page("BT /F1 10 Tf 1 0 0 1 40 40 Tm <0041> Tj ET /X1 Do BT 1 0 0 1 60 60 Tm <0041> Tj ET /X2 Do", Type0, CMap("1 beginbfchar\n<0041> <0058>\nendbfchar"),
                "/XObject << /X1 8 0 R /X2 9 0 R >>", (8, form), (9, bare), (10, L(Type0.Replace("6 0 R", "11 0 R", StringComparison.Ordinal))),
                (11, PdfTests.StreamBody("", L(CMap("1 beginbfchar\n<0041> <00D1>\nendbfchar")))));
            var shown = nested.Pages[0].Elements.OfType<PdfText>().ToList();
            C(Texts(nested) == "X|\\u00D1|X|X" && shown.Count == 4 && shown[1].Provenance.ObjectNumber == 8 && shown[1].Provenance.XObjectPath.SequenceEqual(new[] { "o4:i5" })
                && shown[3].Provenance.ObjectNumber == 9 && nested.Warnings.Count == 0,
                "a Form XObject uses its own font and ToUnicode; the page font returns after Do; a form without Tf inherits the page font", Texts(nested) + " " + Warnings(nested));
        }
        catch (Exception error) { C(false, "mapping unexpected", error.GetType().Name + ": " + error.Message); }
            // ---------- review round: bounds, codespace, increments, null, filters, pages ----------
        try
        {
            // Every mapped code counts against the document-wide MaxParsedElements budget.
            var small = new PdfReadOptions(MaxParsedElements: 50_000);
            C(CodeOf(() => PdfReader.Parse(ManyFonts(1, 0x10000), "plane.pdf", small)) == "element-limit", "a full-plane bfrange over MaxParsedElements 50000 is refused element-limit",
                CodeOf(() => PdfReader.Parse(ManyFonts(1, 0x10000), "plane.pdf", small)) ?? "read");
            var fits = PdfReader.Parse(ManyFonts(32, 1024), "fonts32.pdf", small);
            C(fits.Warnings.Count == 0 && fits.Pages[0].Elements.OfType<PdfText>().Count() == 32 && fits.Pages[0].Elements.OfType<PdfText>().Last().Value == "`",
                "32 fonts x 1024 codes fit MaxParsedElements 50000", Warnings(fits));
            C(CodeOf(() => PdfReader.Parse(ManyFonts(64, 1024), "fonts64.pdf", small)) == "element-limit", "64 fonts x 1024 codes (each CMap within its own bound) exceed the document budget",
                CodeOf(() => PdfReader.Parse(ManyFonts(64, 1024), "fonts64.pdf", small)) ?? "read");

            // Decoded text counts against MaxInterpretedBytes, not only the shown bytes.
            var codes = string.Concat(Enumerable.Repeat("0041", 2000));
            var wide = CMap("1 beginbfchar\n<0041> <" + string.Concat(Enumerable.Repeat("0041", 32)) + ">\nendbfchar");
            var narrow = CMap("1 beginbfchar\n<0041> <0058>\nendbfchar");
            var budget = new PdfReadOptions(MaxInterpretedBytes: 30_000);
            foreach (var show in new[] { "<" + codes + "> Tj", "[<" + codes + ">] TJ" })
            {
                var content = "BT /F1 10 Tf 1 0 0 1 50 50 Tm " + show + " ET";
                C(CodeOf(() => PdfReader.Parse(PageBytes(content, Type0, PdfTests.StreamBody("", L(wide))), "wide.pdf", budget)) == "interpret-limit",
                    "32-unit destinations x 2000 codes exceed MaxInterpretedBytes 30000 (" + show[^2..] + ")", CodeOf(() => PdfReader.Parse(PageBytes(content, Type0, PdfTests.StreamBody("", L(wide))), "wide.pdf", budget)) ?? "read");
                var ok = PdfReader.Parse(PageBytes(content, Type0, PdfTests.StreamBody("", L(narrow))), "narrow.pdf", budget);
                C(ok.Pages[0].Elements.OfType<PdfText>().Single().Value == new string('X', 2000), "one-unit destinations x 2000 codes fit MaxInterpretedBytes 30000 (" + show[^2..] + ")", Warnings(ok));
            }

            // At most 100 codespace ranges.
            string Ranges(int n) => string.Concat(Enumerable.Range(0, n).Select(i => "<" + i.ToString("X2", CultureInfo.InvariantCulture) + "> <" + i.ToString("X2", CultureInfo.InvariantCulture) + ">\n"));
            var hundred = Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm (A) Tj ET", Simple, CMap("1 beginbfchar\n<41> <0058>\nendbfchar", Ranges(100)));
            var tooMany = Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm (A) Tj ET", Simple, CMap("1 beginbfchar\n<41> <0058>\nendbfchar", Ranges(101)));
            C(Texts(hundred) == "X" && hundred.Warnings.Count == 0 && Texts(tooMany) == "A" && Warnings(tooMany) == "tounicode-invalid@1:4:1",
                "100 codespace ranges are read; 101 make the CMap invalid", Texts(hundred) + " " + Warnings(hundred) + " / " + Texts(tooMany) + " " + Warnings(tooMany));

            // Non-Identity Type0: codes split by the codespace (1 byte 00-7F, 2 bytes 8100-9FFF); an unmatched byte consumes
            // two bytes as one U+FFFD.
            var mixedFont = Type0.Replace("/Identity-H", "/UniJIS-UCS2-H", StringComparison.Ordinal);
            var mixed = Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm <41814142> Tj <41A0418141> Tj ET", mixedFont,
                CMap("3 beginbfchar\n<41> <0041>\n<42> <0042>\n<8141> <3042>\nendbfchar", "<00> <7F>\n<8100> <9FFF>"));
            C(Texts(mixed) == "A\\u3042B|A\\uFFFD\\u3042" && Warnings(mixed) == "tounicode-unmapped@1:4:4", "non-Identity Type0 splits mixed one- and two-byte codes by the codespace",
                Texts(mixed) + " " + Warnings(mixed));

            // Incrementing bfrange: a surrogate-pair destination increments the code point (carrying into the high
            // surrogate); a BMP destination stops at U+FFFF and later codes stay unmapped.
            var increments = Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm <000100020003> Tj <00100011> Tj <002000210022> Tj ET", Type0,
                CMap("3 beginbfrange\n<0001> <0003> <D83DDE00>\n<0010> <0011> <D83DDFFF>\n<0020> <0022> <FFFE>\nendbfrange"));
            C(Texts(increments) == "\\uD83D\\uDE00\\uD83D\\uDE01\\uD83D\\uDE02|\\uD83D\\uDFFF\\uD83E\\uDC00|\\uFFFE\\uFFFF\\uFFFD" && Warnings(increments) == "tounicode-unmapped@1:4:5",
                "incrementing bfrange over U+1F600, carrying U+1F7FF to U+1F800, and stopping after U+FFFF", Texts(increments) + " " + Warnings(increments));

            // A full-plane range after a bfchar exceeds the per-CMap bound.
            var full = Page(ShowAB, Type0, CMap("1 beginbfchar\n<0001> <0041>\nendbfchar\n1 beginbfrange\n<0000> <FFFF> <0041>\nendbfrange"));
            C(Texts(full) == "\\u0000A\\u0000B" && Warnings(full) == "tounicode-invalid@1:4:1", "a full-plane bfrange after a bfchar exceeds 65536 entries", Texts(full) + " " + Warnings(full));

            // /ToUnicode null is an absent entry; a dangling reference is invalid (pinned above).
            var nullType0 = Page(ShowAB, Type0.Replace("6 0 R", "null", StringComparison.Ordinal), null);
            var nullSimple = Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm (AB) Tj ET", Simple.Replace("6 0 R", "null", StringComparison.Ordinal), null);
            C(Warnings(nullType0) == "tounicode-missing@1:4:1" && Texts(nullSimple) == "AB" && nullSimple.Warnings.Count == 0, "/ToUnicode null reads as absent",
                Warnings(nullType0) + " / " + Warnings(nullSimple));

            // FlateDecode ToUnicode streams decode; an unsupported filter warns once (decoded-stream cache) and each font
            // using it is tounicode-invalid.
            var flated = PdfReader.Parse(PageBytes(ShowAB, Type0, PdfTests.StreamBody("/Filter /FlateDecode", PdfTests.Deflate(L(narrow)))), "flate-cmap.pdf");
            C(Texts(flated) == "X\\uFFFD" && Warnings(flated) == "tounicode-unmapped@1:4:3", "a FlateDecode ToUnicode stream is read", Texts(flated) + " " + Warnings(flated));
            var hex = PdfReader.Parse(PageBytes("BT /F1 10 Tf 1 0 0 1 50 50 Tm (A) Tj /F2 10 Tf (A) Tj ET", Simple, PdfTests.StreamBody("/Filter /ASCIIHexDecode", L("3c3e>")), "", Simple), "hex-cmap.pdf");
            C(Texts(hex) == "A|A" && Warnings(hex) == "unsupported-filter@1:6:-1;tounicode-invalid@1:4:1;tounicode-invalid@1:4:4",
                "an unsupported ToUnicode filter warns once and each font using it is tounicode-invalid", Texts(hex) + " " + Warnings(hex));

            // Warnings are per page: two pages sharing a Type0 font without ToUnicode warn once each.
            var twoPages = PdfReader.Parse(PdfTests.Build(new List<(int Number, byte[] Body)>
            {
                (1, L("<< /Type /Catalog /Pages 2 0 R >>")),
                (2, L("<< /Type /Pages /Kids [3 0 R 8 0 R] /Count 2 /Resources << /Font << /F1 5 0 R >> >> >>")),
                (3, L("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 400] /Contents 4 0 R >>")),
                (4, PdfTests.StreamBody("", L(ShowAB + " " + ShowAB))),
                (5, L(Type0Bare)),
                (8, L("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 400] /Contents 9 0 R >>")),
                (9, PdfTests.StreamBody("", L(ShowAB))),
            }), "two-pages.pdf");
            C(Warnings(twoPages) == "tounicode-missing@1:4:1;tounicode-missing@2:9:1", "tounicode-missing is given once per font and page", Warnings(twoPages));
        }
        catch (Exception error) { C(false, "review unexpected", error.GetType().Name + ": " + error.Message); }
    }
}
