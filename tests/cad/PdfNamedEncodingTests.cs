using Bim.Cad;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

// P6-20 PDF named simple-font encodings and Type3 glyph names. A deterministic two-page fixture is generated here in
// test code (only constant strings through PdfTests.Build, bytes pinned by SHA-256; no committed blob):
// - page 1 is a room plan whose labels are shown by simple fonts without ToUnicode and with an /Encoding name:
//   /F1 Type1 /WinAnsiEncoding ("Dormitorio 1", "12,00 m²"), /F2 Type1 /MacRomanEncoding ("Baño" with ñ at code 0x96,
//   where Latin-1 has a control character), /F3 TrueType /StandardEncoding ("Cocina") and /F4 a Type3 font whose
//   /Encoding /Differences names every slot /gNN (procedure names outside the glyph list) showing "Estar": the Type3
//   codes keep the byte decoding without encoding-glyph-unknown and BriefExtract reads the four rooms;
// - page 2 shows WinAnsi 0x80-0x9F (€, “, ”, …, –, —), StandardEncoding quoteleft/quoteright/fraction, MacRoman
//   Adieresis/bullet/quoteright and /F5 whose /BaseEncoding /FooEncoding is unknown: its /Differences still decode, the
//   other codes keep the byte decoding and the Tf warns encoding-base-unknown once per font and page.
// Generated single pages then pin every WinAnsi 0x80-0x9F code, the three named tables, an indirect /Encoding name,
// an unknown /Encoding name (today's decoding, silent), Type3 against Type1 with the same /Differences, a non-name
// /BaseEncoding, and ToUnicode winning over a named encoding. Unit-tested evidence only.
internal static class PdfNamedEncodingTests
{
    private static string I(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static byte[] L(string text) => Encoding.Latin1.GetBytes(text);
    private static string Warnings(PdfDocument document) => string.Join(";", document.Warnings.Select(w => w.Code + "@" + I(w.Page) + ":" + I(w.ObjectNumber) + ":" + I(w.OperatorIndex)));
    private static string Escape(string text) => string.Concat(text.Select(c => c < 0x20 || c > 0x7E ? "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture) : c.ToString()));
    private static string Texts(PdfDocument document) => string.Join("|", document.Pages.SelectMany(p => p.Elements).OfType<PdfText>().Select(t => Escape(t.Value)));
    private static string PageTexts(PdfDocument document, int page) => string.Join("|", document.Pages[page - 1].Elements.OfType<PdfText>().Select(t => Escape(t.Value)));

    private static string Font(string encoding, bool toUnicode = false) =>
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding " + encoding + (toUnicode ? " /ToUnicode 6 0 R" : "") + " >>";

    private static string Type3(string differences) =>
        "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 750 750] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << >> /Encoding << /Type /Encoding /Differences "
        + differences + " >> /Resources << >> >>";

    private static string Show(string literal) => "BT /F1 10 Tf 1 0 0 1 50 50 Tm (" + literal + ") Tj ET";

    private static PdfDocument Page(string content, string font, params (int Number, byte[] Body)[] extra) =>
        PdfReader.Parse(PdfToUnicodeTests.PageBytes(content, font, null, "", null, extra), "named-encoding-gen.pdf");

    // ---------- the generated fixture ----------

    // SHA-256 (upper-case hex) of Fixture().
    internal const string FixtureSha256 = "A16CB92911424FEBD52A20BA80E3C496BBD1EDCF53A1A1CC21B3E2683FC7651E";

    // Type3 /gNN slot names for the codes of "Estar" and "6,40 m2" (decimal code numbers, not in the glyph list).
    private const string Type3Differences =
        "[32 /g32 44 /g44 48 /g48 50 /g50 52 /g52 54 /g54 69 /g69 97 /g97 109 /g109 114 /g114 115 /g115 116 /g116]";

    private const string Page1Content =
        "1 w\n"
        + "100 150 m\n300 150 l\n300 300 l\n100 300 l\nh\nS\n"
        + "300 150 m\n400 150 l\n400 250 l\n300 250 l\nh\nS\n"
        + "400 150 m\n550 150 l\n550 300 l\n400 300 l\nh\nS\n"
        + "100 300 m\n300 300 l\n300 380 l\n100 380 l\nh\nS\n"
        + "BT /F1 10 Tf 1 0 0 1 120 240 Tm (Dormitorio 1) Tj ET\n"
        + "BT /F1 10 Tf 1 0 0 1 120 215 Tm (12,00 m\\262) Tj ET\n"
        + "BT /F2 10 Tf 1 0 0 1 320 210 Tm (Ba\\226o) Tj ET\n"
        + "BT /F2 10 Tf 1 0 0 1 320 190 Tm (4,00 m2) Tj ET\n"
        + "BT /F3 10 Tf 1 0 0 1 420 240 Tm (Cocina) Tj ET\n"
        + "BT /F3 10 Tf 1 0 0 1 420 215 Tm (9,00 m2) Tj ET\n"
        + "BT /F4 10 Tf 1 0 0 1 120 350 Tm (Estar) Tj ET\n"
        + "BT /F4 10 Tf 1 0 0 1 120 325 Tm (6,40 m2) Tj ET\n";

    // Operators: BT 0, Tf 1, Tm 2, Tj 3, ET 4 per line; the /F5 Tf of line 4 is operator 16.
    private const string Page2Content =
        "BT /F1 10 Tf 1 0 0 1 50 300 Tm (\\200 \\223Sala\\224 \\205 \\226 \\227) Tj ET\n"
        + "BT /F3 10 Tf 1 0 0 1 50 275 Tm (\\140Patio\\047 \\244) Tj ET\n"
        + "BT /F2 10 Tf 1 0 0 1 50 260 Tm (\\200\\245\\325) Tj ET\n"
        + "BT /F5 10 Tf 1 0 0 1 50 250 Tm (\\001A\\200) Tj ET\n"
        + "BT /F5 10 Tf 1 0 0 1 50 225 Tm (\\001) Tj ET\n";

    internal static byte[] Fixture() => PdfTests.Build(new List<(int Number, byte[] Body)>
    {
        (1, L("<< /Type /Catalog /Pages 2 0 R >>")),
        (2, L("<< /Type /Pages /Kids [3 0 R 10 0 R] /Count 2 >>")),
        (3, L("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 400] /Resources << /Font << /F1 5 0 R /F2 6 0 R /F3 7 0 R /F4 8 0 R >> >> /Contents 4 0 R >>")),
        (4, PdfTests.StreamBody("", L(Page1Content))),
        (5, L("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")),
        (6, L("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /MacRomanEncoding >>")),
        (7, L("<< /Type /Font /Subtype /TrueType /BaseFont /AAAAAE+ArialMT /Encoding /StandardEncoding >>")),
        (8, L("<< /Type /Font /Subtype /Type3 /FontBBox [0 0 750 750] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /g69 11 0 R >> /Encoding << /Type /Encoding /Differences "
            + Type3Differences + " >> /FirstChar 32 /LastChar 116 /Resources << >> >>")),
        (10, L("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 400] /Resources << /Font << /F1 5 0 R /F2 6 0 R /F3 7 0 R /F5 12 0 R >> >> /Contents 13 0 R >>")),
        (11, PdfTests.StreamBody("", L("750 0 0 0 750 750 d1\n0 0 750 750 re\nf\n"))),
        (12, L("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /Type /Encoding /BaseEncoding /FooEncoding /Differences [1 /ntilde] >> >>")),
        (13, PdfTests.StreamBody("", L(Page2Content))),
    });

    // WinAnsiEncoding 0x80-0x9F written out independently of the reader (PDF 32000-1 Annex D); undefined codes
    // (0x81, 0x8D, 0x8F, 0x90, 0x9D) keep the Latin-1 control character.
    private static readonly int[] WinAnsiHigh =
    {
        0x20AC, 0x0081, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021, 0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0x008D, 0x017D, 0x008F,
        0x0090, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014, 0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0x009D, 0x017E, 0x0178,
    };

    public static void Run(Action<bool, string, string> check, string fixtures)
    {
        void C(bool condition, string name, string detail = "") => check(condition, "named encoding " + name, detail);

        // ---------- generated fixture ----------
        try
        {
            var bytes = Fixture();
            var sha = Convert.ToHexString(SHA256.HashData(bytes));
            C(sha == FixtureSha256 && Fixture().SequenceEqual(bytes), "the generated fixture is deterministic and its bytes equal the pinned SHA-256", sha);
            var document = PdfReader.Parse(bytes, "named-encodings.pdf");
            C(Warnings(document) == "encoding-base-unknown@2:13:16", "the fixture warns only encoding-base-unknown once, at the first /F5 Tf of page 2", Warnings(document));
            var baseWarning = document.Warnings.FirstOrDefault(w => w.Code == "encoding-base-unknown");
            C(baseWarning is not null && baseWarning.Message.Contains("/FooEncoding", StringComparison.Ordinal) && baseWarning.Message.Contains("/F5", StringComparison.Ordinal),
                "the encoding-base-unknown warning names the base encoding and the font", baseWarning?.Message ?? "none");
            C(PageTexts(document, 1) == "Dormitorio 1|12,00 m\\u00B2|Ba\\u00F1o|4,00 m2|Cocina|9,00 m2|Estar|6,40 m2",
                "page 1 labels decode through WinAnsi, MacRoman (0x96 ntilde) and Standard names, and Type3 /gNN slots keep their bytes", PageTexts(document, 1));
            C(PageTexts(document, 2) == "\\u20AC \\u201CSala\\u201D \\u2026 \\u2013 \\u2014|\\u2018Patio\\u2019 \\u2044|\\u00C4\\u2022\\u2019|\\u00F1A\\u0080|\\u00F1",
                "page 2: WinAnsi 0x80-0x9F, Standard quotes and fraction, MacRoman extras; an unknown base keeps /Differences and Latin-1 elsewhere", PageTexts(document, 2));

            var draft = BriefExtract.FromPdf(document, 1, null, new BriefExtractOptions { MetresPerUnit = 0.02 });
            var ids = string.Join(",", draft.Rooms.Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal));
            C(ids == "bano,cocina,dormitorio-1,estar", "brief extraction reads the four room labels", ids + " " + string.Join(",", draft.Uncertainties.Select(u => u.Code)));
            bool Room(string id, string name, double area) =>
                draft.Rooms.FirstOrDefault(r => r.Id == id) is { } room && room.Name == name && room.StatedAreaSquareMetres is { } stated && Math.Abs(stated - area) < 1e-9;
            C(Room("bano", "Baño", 4) && Room("dormitorio-1", "Dormitorio 1", 12) && Room("cocina", "Cocina", 9) && Room("estar", "Estar", 6.4),
                "Baño (MacRoman), Dormitorio 1 (WinAnsi), Cocina (Standard) and Estar (Type3) carry their names and stated areas", string.Join(" ", draft.Rooms.Select(r => r.ToString())));
            C(!draft.Uncertainties.Any(u => u.Code is "orphan-area" or "unlabelled-region" or "uncalibrated") && draft.Issues.Count == 0,
                "every area text pairs with a label and every region is a room", string.Join(",", draft.Uncertainties.Select(u => u.Code)));
        }
        catch (Exception error) { C(false, "fixture unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- named base encodings ----------
        try
        {
            var high = string.Concat(Enumerable.Range(0x80, 32).Select(code => "\\" + Convert.ToString(code, 8)));
            var win = Page(Show(high + "A\\001\\351"), Font("/WinAnsiEncoding"));
            var expected = Escape(string.Concat(WinAnsiHigh.Select(point => char.ConvertFromUtf32(point)))) + "A\\u0001\\u00E9";
            C(Texts(win) == expected && win.Warnings.Count == 0, "/WinAnsiEncoding maps every code 0x80-0x9F as Annex D; ASCII, controls and 0xA0-0xFF stay as Latin-1",
                Texts(win) + " expected " + expected + " " + Warnings(win));
            var mac = Page(Show("\\200\\226\\216\\325\\245\\312A"), Font("/MacRomanEncoding"));
            C(Texts(mac) == "\\u00C4\\u00F1\\u00E9\\u2019\\u2022 A" && mac.Warnings.Count == 0, "/MacRomanEncoding maps Adieresis, ntilde, eacute, quoteright, bullet and the 0xCA space",
                Texts(mac) + " " + Warnings(mac));
            var standard = Page(Show("'`\\251\\244\\341\\373A"), Font("/StandardEncoding"));
            C(Texts(standard) == "\\u2019\\u2018'\\u2044\\u00C6\\u00DFA" && standard.Warnings.Count == 0, "/StandardEncoding maps quoteright, quoteleft, quotesingle, fraction, AE and germandbls",
                Texts(standard) + " " + Warnings(standard));
            var indirect = Page(Show("\\223x\\224"), Font("8 0 R"), (8, L("/WinAnsiEncoding")));
            C(Texts(indirect) == "\\u201Cx\\u201D" && indirect.Warnings.Count == 0, "an indirect /Encoding name is read", Texts(indirect) + " " + Warnings(indirect));
            var unknownName = Page(Show("\\200\\223A"), Font("/FooEncoding"));
            C(Texts(unknownName) == "\\u0080\\u0093A" && unknownName.Warnings.Count == 0, "an unknown /Encoding name keeps today's decoding without a warning", Texts(unknownName) + " " + Warnings(unknownName));
        }
        catch (Exception error) { C(false, "base unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- Type3 /Differences ----------
        try
        {
            const string differences = "[1 /g1 /a2 /afii10017 65 /cid65 66 /eacute]";
            var type3 = Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm (\\001\\002\\003) Tj (AB) Tj ET", Type3(differences));
            C(Texts(type3) == "\\u0001\\u0002\\u0003|A\\u00E9" && type3.Warnings.Count == 0,
                "a Type3 font keeps the byte decoding of slots named outside the glyph list without encoding-glyph-unknown; a Latin name still maps", Texts(type3) + " " + Warnings(type3));
            var type1 = Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm (\\001\\002\\003) Tj (AB) Tj ET", Font("<< /Differences " + differences + " >>"));
            C(Texts(type1) == "\\u0001\\u0002\\u0003|A\\u00E9" && Warnings(type1) == "encoding-glyph-unknown@1:4:3;encoding-glyph-unknown@1:4:4",
                "the same /Differences on a Type1 font still warns encoding-glyph-unknown", Texts(type1) + " " + Warnings(type1));
        }
        catch (Exception error) { C(false, "type3 unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- unknown /BaseEncoding ----------
        try
        {
            var foo = Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm (\\001A\\200) Tj ET BT /F1 10 Tf (\\001) Tj ET", Font("<< /BaseEncoding /FooEncoding /Differences [1 /ntilde] >>"));
            C(Texts(foo) == "\\u00F1A\\u0080|\\u00F1" && Warnings(foo) == "encoding-base-unknown@1:4:1",
                "an unknown /BaseEncoding name keeps /Differences and Latin-1 and warns encoding-base-unknown once per font and page at the Tf", Texts(foo) + " " + Warnings(foo));
            var number = Page(Show("\\001\\200"), Font("<< /BaseEncoding 5 /Differences [1 /ntilde] >>"));
            C(Texts(number) == "\\u00F1\\u0080" && Warnings(number) == "encoding-base-unknown@1:4:1", "a non-name /BaseEncoding warns encoding-base-unknown", Texts(number) + " " + Warnings(number));
            var nullBase = Page(Show("\\001\\200"), Font("<< /BaseEncoding null /Differences [1 /ntilde] >>"));
            C(Texts(nullBase) == "\\u00F1\\u0080" && nullBase.Warnings.Count == 0, "/BaseEncoding null is absent and does not warn", Texts(nullBase) + " " + Warnings(nullBase));
            var type3Base = Page(Show("\\001\\200"), Type3("[1 /g1]").Replace("/Differences", "/BaseEncoding /FooEncoding /Differences", StringComparison.Ordinal));
            C(Texts(type3Base) == "\\u0001\\u0080" && Warnings(type3Base) == "encoding-base-unknown@1:4:1", "a Type3 font with an unknown /BaseEncoding warns encoding-base-unknown",
                Texts(type3Base) + " " + Warnings(type3Base));
        }
        catch (Exception error) { C(false, "base-unknown unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- ToUnicode still wins ----------
        try
        {
            var cmap = PdfToUnicodeTests.CMap("1 beginbfchar\n<80> <0058>\nendbfchar", "<00> <FF>");
            var both = PdfToUnicodeTests.Page(Show("\\200\\223"), Font("/WinAnsiEncoding", toUnicode: true), cmap);
            C(Texts(both) == "X\\u201C" && both.Warnings.Count == 0, "ToUnicode wins over a named encoding; codes it does not map use the encoding", Texts(both) + " " + Warnings(both));
        }
        catch (Exception error) { C(false, "tounicode unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- review: unknown /BaseEncoding under a ToUnicode CMap that maps every shown code ----------
        try
        {
            var cmap = PdfToUnicodeTests.CMap("2 beginbfchar\n<41> <005A>\n<80> <0059>\nendbfchar", "<00> <FF>");
            var covered = PdfToUnicodeTests.Page(Show("\\200A"), Font("<< /BaseEncoding /FooEncoding /Differences [1 /ntilde] >>", toUnicode: true), cmap);
            C(Texts(covered) == "YZ" && Warnings(covered) == "encoding-base-unknown@1:4:1", "with ToUnicode covering the codes the text decodes through the CMap and an unknown base still warns",
                Texts(covered) + " " + Warnings(covered));
            var message = covered.Warnings.FirstOrDefault()?.Message ?? "none";
            C(message.Contains("/FooEncoding", StringComparison.Ordinal) && message.Contains("codes outside /Differences that the ToUnicode CMap does not map are read as text bytes", StringComparison.Ordinal),
                "the encoding-base-unknown message is accurate with and without ToUnicode", message);
        }
        catch (Exception error) { C(false, "base-unknown tounicode unexpected", error.GetType().Name + ": " + error.Message); }
    }
}
