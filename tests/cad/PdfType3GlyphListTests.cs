using Bim.Cad;
using System.Globalization;
using System.Text;

// P6-22 Type3 glyph-list names outside the reader table are signalled. A Type3 font names its /Differences slots after
// its glyph procedures (/g12, /a1, /cid123 ...) and P6-20 keeps those bytes silently; a slot named with a real Adobe
// glyph-list name that the reader does not map (afii names such as /afii10017, Greek such as /Alpha, Latin Extended-A
// such as /Scedilla, a uniXXXX or uXXXX form the reader rejects) is now reported by a typed encoding-glyph-unsupported
// warning, once per font per page at the Tf; the text bytes stay as before. Pages are generated in test code from
// constant strings (no committed blob). Type1 keeps encoding-glyph-unknown on the showing operator. Unit-tested only.
internal static class PdfType3GlyphListTests
{
    private static string I(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static byte[] L(string text) => Encoding.Latin1.GetBytes(text);
    private static string Warnings(PdfDocument document) => string.Join(";", document.Warnings.Select(w => w.Code + "@" + I(w.Page) + ":" + I(w.ObjectNumber) + ":" + I(w.OperatorIndex)));
    private static string Escape(string text) => string.Concat(text.Select(c => c < 0x20 || c > 0x7E ? "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture) : c.ToString()));
    private static string Texts(PdfDocument document) => string.Join("|", document.Pages.SelectMany(p => p.Elements).OfType<PdfText>().Select(t => Escape(t.Value)));

    private static string Type3(string differences) =>
        "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 750 750] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << >> /Encoding << /Type /Encoding /Differences "
        + differences + " >> /Resources << >> >>";

    private static string Type1(string differences) => "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /Differences " + differences + " >> >>";

    private static PdfDocument Page(string content, string font, string? font2 = null) =>
        PdfReader.Parse(PdfToUnicodeTests.PageBytes(content, font, null, "", font2), "type3-glyph-list-gen.pdf");

    // Operators: BT 0, Tf 1, Tm 2, Tj 3, ET 4, BT 5, Tf 6, Tj 7, ET 8.
    private const string TwoShows = "BT /F1 10 Tf 1 0 0 1 50 50 Tm (\\001\\002\\003\\004\\005\\006\\007) Tj ET BT /F1 10 Tf (\\001\\004) Tj ET";
    private const string TwoShowsBytes = "\\u0001\\u0002\\u0003\\u0004\\u0005\\u0006\\u0007|\\u0001\\u0004";

    public static void Run(Action<bool, string, string> check, string fixtures)
    {
        void C(bool condition, string name, string detail = "") => check(condition, "type3 glyph list " + name, detail);

        // ---------- glyph-list names the reader does not map warn once, bytes unchanged ----------
        try
        {
            const string mixed = "[1 /g12 /a1 /cid123 /afii10017 /Alpha.sc /Scedilla /uniD800]";
            var type3 = Page(TwoShows, Type3(mixed));
            C(Texts(type3) == TwoShowsBytes, "a Type3 font keeps the text bytes of slot and glyph-list names it does not map", Texts(type3));
            C(Warnings(type3) == "encoding-glyph-unsupported@1:4:1",
                "a Type3 font with unsupported glyph-list names warns encoding-glyph-unsupported once per font per page at the first Tf, not on the showing operators", Warnings(type3));
            var message = type3.Warnings.FirstOrDefault(w => w.Code == "encoding-glyph-unsupported")?.Message ?? "none";
            C(message.Contains("/F1", StringComparison.Ordinal) && message.Contains("/afii10017", StringComparison.Ordinal) && message.Contains("/Alpha.sc", StringComparison.Ordinal)
                && message.Contains("/Scedilla", StringComparison.Ordinal) && message.Contains("/uniD800", StringComparison.Ordinal),
                "the message names the font and every unsupported glyph-list name", message);
            C(!message.Contains("/g12", StringComparison.Ordinal) && !message.Contains("/a1 ", StringComparison.Ordinal) && !message.Contains("/a1,", StringComparison.Ordinal)
                && !message.Contains("/cid123", StringComparison.Ordinal), "the message does not name the slot names", message);

            var single = Page(Show("\\001"), Type3("[1 /afii10017]"));
            C(Texts(single) == "\\u0001" && Warnings(single) == "encoding-glyph-unsupported@1:4:1", "/afii10017 alone warns", Texts(single) + " " + Warnings(single));
            var unseen = Page(Show("A"), Type3("[1 /afii57636]"));
            C(Texts(unseen) == "A" && Warnings(unseen) == "encoding-glyph-unsupported@1:4:1", "the warning is per font, at the Tf, even when the shown codes avoid the name",
                Texts(unseen) + " " + Warnings(unseen));
        }
        catch (Exception error) { C(false, "unsupported unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- slot names and decodable names stay silent ----------
        try
        {
            var slots = Page(TwoShows, Type3("[1 /g12 /a1 /cid123 /g1.alt /afii1234 /uni00e9 /u1F]"));
            C(Texts(slots) == TwoShowsBytes && slots.Warnings.Count == 0,
                "slot names (/g12, /a1, /cid123, /g1.alt), a 4-digit afii, lower-case uni and a short u form stay silent", Texts(slots) + " " + Warnings(slots));
            var decoded = Page(Show("\\001\\002\\003"), Type3("[1 /uni00E9 /u1F600 /eacute]"));
            C(Texts(decoded) == "\\u00E9\\uD83D\\uDE00\\u00E9" && decoded.Warnings.Count == 0, "uniXXXX, uXXXXX and Latin names the reader decodes still map and stay silent",
                Texts(decoded) + " " + Warnings(decoded));
        }
        catch (Exception error) { C(false, "silent unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- full messages, the eight-name cap, duplicates and ToUnicode ----------
        try
        {
            const string prefix = "font /F1 (object 5) is a Type3 font whose /Differences names glyph-list names the reader does not map (";
            string Message(PdfDocument document) => document.Warnings.FirstOrDefault(w => w.Code == "encoding-glyph-unsupported")?.Message ?? "none";

            var plain = Page(Show("\\001"), Type3("[1 /afii10017]"));
            C(Message(plain) == prefix + "/afii10017); their codes are read as text bytes", "without ToUnicode the full message does not mention a CMap", Message(plain));

            var nine = Page(Show("\\001"), Type3("[1 /afii10017 /afii10018 /afii10019 /afii10020 /afii10021 /afii10022 /afii10023 /afii10024 /afii10025 20 /afii10017]"));
            C(Warnings(nine) == "encoding-glyph-unsupported@1:4:1"
                && Message(nine) == prefix + "/afii10017, /afii10018, /afii10019, /afii10020, /afii10021, /afii10022, /afii10023, /afii10024, ...); their codes are read as text bytes",
                "nine distinct names list the first eight then ', ...' and a repeated name counts once", Message(nine));
            var repeated = Page(Show("\\001"), Type3("[1 /afii10017 /afii10017 /Gamma 30 /afii10017]"));
            C(Message(repeated) == prefix + "/afii10017, /Gamma); their codes are read as text bytes", "a name repeated in /Differences is listed once", Message(repeated));

            var cmap = PdfToUnicodeTests.CMap("1 beginbfchar\n<01> <0416>\nendbfchar", "<00> <FF>");
            var withCmap = PdfToUnicodeTests.Page(Show("\\001\\002"), Type3("[1 /afii10017 /afii10018]").Replace("/Resources << >> >>", "/Resources << >> /ToUnicode 6 0 R >>", StringComparison.Ordinal), cmap);
            C(Texts(withCmap) == "\\u0416\\u0002" && Warnings(withCmap) == "encoding-glyph-unsupported@1:4:1",
                "with ToUnicode the warning still fires at the Tf, mapped codes decode through the CMap and unmapped ones keep their bytes", Texts(withCmap) + " " + Warnings(withCmap));
            C(Message(withCmap) == prefix + "/afii10017, /afii10018); codes that the ToUnicode CMap does not map are read as text bytes",
                "with ToUnicode the full message names the CMap", Message(withCmap));
        }
        catch (Exception error) { C(false, "message unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- once per font per page ----------
        try
        {
            var two = Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm (\\001) Tj /F2 10 Tf (\\001) Tj /F1 10 Tf (\\001) Tj ET",
                Type3("[1 /afii10017]"), Type3("[1 /Gamma]"));
            C(Texts(two) == "\\u0001|\\u0001|\\u0001" && Warnings(two) == "encoding-glyph-unsupported@1:4:1;encoding-glyph-unsupported@1:4:4",
                "two Type3 fonts on a page each warn once at their first Tf", Texts(two) + " " + Warnings(two));

            var pages = PdfReader.Parse(PdfTests.Build(new List<(int Number, byte[] Body)>
            {
                (1, L("<< /Type /Catalog /Pages 2 0 R >>")),
                (2, L("<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>")),
                (3, L("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 400] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>")),
                (4, PdfTests.StreamBody("", L(TwoShows))),
                (5, L(Type3("[1 /afii10017]"))),
                (6, L("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 400] /Resources << /Font << /F1 5 0 R >> >> /Contents 7 0 R >>")),
                (7, PdfTests.StreamBody("", L(TwoShows))),
            }), "type3-glyph-list-pages.pdf");
            C(Warnings(pages) == "encoding-glyph-unsupported@1:4:1;encoding-glyph-unsupported@2:7:1", "one shared Type3 font warns once on each page", Warnings(pages));
        }
        catch (Exception error) { C(false, "once unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- Type1 unchanged ----------
        try
        {
            var type1 = Page(TwoShows, Type1("[1 /afii10017 /g12]"));
            C(Texts(type1) == TwoShowsBytes && Warnings(type1) == "encoding-glyph-unknown@1:4:3;encoding-glyph-unknown@1:4:7",
                "a Type1 font with the same names keeps encoding-glyph-unknown on the showing operators and no encoding-glyph-unsupported", Texts(type1) + " " + Warnings(type1));
        }
        catch (Exception error) { C(false, "type1 unexpected", error.GetType().Name + ": " + error.Message); }
    }

    private static string Show(string literal) => "BT /F1 10 Tf 1 0 0 1 50 50 Tm (" + literal + ") Tj ET";
}
