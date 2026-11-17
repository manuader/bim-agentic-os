using Bim.Cad;
using System.Globalization;
using System.Text;

// P6-22 Type3 glyph-list names outside the reader table are signalled. A Type3 font names its /Differences slots after
// its glyph procedures (/g12, /a1, /cid123 ...) and P6-20 keeps those bytes silently; a slot named with a real Adobe
// glyph-list name that the reader does not map (afii names such as /afii10017, Greek such as /Alpha, Latin Extended-A
// such as /Scedilla, a uniXXXX or uXXXX form the reader rejects) is now reported by a typed encoding-glyph-unsupported
// warning, once per font per page; the text bytes stay as before. Pages are generated in test code from constant
// strings (no committed blob). Type1 keeps encoding-glyph-unknown on the showing operator. Unit-tested only.
// P6-24: the warning fires at the first show operator (Tj, TJ, ', ") that actually shows an affected code (a code whose
// /Differences name is such a glyph-list name and that the ToUnicode CMap does not map), not at the Tf, and lists only
// the names of the affected codes that operator shows (code order, deduplicated, at most eight); a font selected but
// never showing an affected code is silent.
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
            C(Warnings(type3) == "encoding-glyph-unsupported@1:4:3",
                "a Type3 font with unsupported glyph-list names warns encoding-glyph-unsupported once per font per page at the first operator showing one, not at the Tf", Warnings(type3));
            var message = type3.Warnings.FirstOrDefault(w => w.Code == "encoding-glyph-unsupported")?.Message ?? "none";
            C(message.Contains("/F1", StringComparison.Ordinal) && message.Contains("/afii10017", StringComparison.Ordinal) && message.Contains("/Alpha.sc", StringComparison.Ordinal)
                && message.Contains("/Scedilla", StringComparison.Ordinal) && message.Contains("/uniD800", StringComparison.Ordinal),
                "the message names the font and every unsupported glyph-list name", message);
            C(!message.Contains("/g12", StringComparison.Ordinal) && !message.Contains("/a1 ", StringComparison.Ordinal) && !message.Contains("/a1,", StringComparison.Ordinal)
                && !message.Contains("/cid123", StringComparison.Ordinal), "the message does not name the slot names", message);

            var single = Page(Show("\\001"), Type3("[1 /afii10017]"));
            C(Texts(single) == "\\u0001" && Warnings(single) == "encoding-glyph-unsupported@1:4:3", "/afii10017 alone warns at the showing Tj", Texts(single) + " " + Warnings(single));
            var unseen = Page(Show("A"), Type3("[1 /afii57636]"));
            C(Texts(unseen) == "A" && unseen.Warnings.Count == 0, "a font whose shown codes avoid the glyph-list names is silent (P6-24)",
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

            var nine = Page(Show("\\001\\002\\003\\004\\005\\006\\007\\010\\011\\024"), Type3("[1 /afii10017 /afii10018 /afii10019 /afii10020 /afii10021 /afii10022 /afii10023 /afii10024 /afii10025 20 /afii10017]"));
            C(Warnings(nine) == "encoding-glyph-unsupported@1:4:3"
                && Message(nine) == prefix + "/afii10017, /afii10018, /afii10019, /afii10020, /afii10021, /afii10022, /afii10023, /afii10024, ...); their codes are read as text bytes",
                "nine distinct names list the first eight then ', ...' and a repeated name counts once", Message(nine));
            var repeated = Page(Show("\\001\\002\\003\\036"), Type3("[1 /afii10017 /afii10017 /Gamma 30 /afii10017]"));
            C(Message(repeated) == prefix + "/afii10017, /Gamma); their codes are read as text bytes", "a name repeated in /Differences is listed once", Message(repeated));

            var cmap = PdfToUnicodeTests.CMap("1 beginbfchar\n<01> <0416>\nendbfchar", "<00> <FF>");
            var withCmap = PdfToUnicodeTests.Page(Show("\\001\\002"), Type3("[1 /afii10017 /afii10018]").Replace("/Resources << >> >>", "/Resources << >> /ToUnicode 6 0 R >>", StringComparison.Ordinal), cmap);
            C(Texts(withCmap) == "\\u0416\\u0002" && Warnings(withCmap) == "encoding-glyph-unsupported@1:4:3",
                "with ToUnicode the warning fires at the showing Tj, mapped codes decode through the CMap and unmapped ones keep their bytes", Texts(withCmap) + " " + Warnings(withCmap));
            C(Message(withCmap) == prefix + "/afii10018); codes that the ToUnicode CMap does not map are read as text bytes",
                "with ToUnicode the full message names the CMap and only the shown name it does not map", Message(withCmap));
        }
        catch (Exception error) { C(false, "message unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- once per font per page ----------
        try
        {
            var two = Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm (\\001) Tj /F2 10 Tf (\\001) Tj /F1 10 Tf (\\001) Tj ET",
                Type3("[1 /afii10017]"), Type3("[1 /Gamma]"));
            C(Texts(two) == "\\u0001|\\u0001|\\u0001" && Warnings(two) == "encoding-glyph-unsupported@1:4:3;encoding-glyph-unsupported@1:4:5",
                "two Type3 fonts on a page each warn once at their first showing operator", Texts(two) + " " + Warnings(two));

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
            C(Warnings(pages) == "encoding-glyph-unsupported@1:4:3;encoding-glyph-unsupported@2:7:3", "one shared Type3 font warns once on each page", Warnings(pages));
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

        // ---------- P6-24: only shown codes not covered by ToUnicode warn, at the first showing operator ----------
        try
        {
            const string prefix = "font /F1 (object 5) is a Type3 font whose /Differences names glyph-list names the reader does not map (";
            string Message(PdfDocument document) => document.Warnings.FirstOrDefault(w => w.Code == "encoding-glyph-unsupported")?.Message ?? "none";
            const string affected = "[1 /g1 /afii10017 /Gamma /afii10018 /afii10019]";

            var selectedOnly = Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm ET", Type3(affected));
            C(selectedOnly.Warnings.Count == 0, "a Type3 font selected but never shown is silent", Warnings(selectedOnly));

            var slotsFirst = Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm (\\001A) Tj (\\003\\002\\003) Tj (\\004) Tj ET", Type3(affected));
            C(Texts(slotsFirst) == "\\u0001A|\\u0003\\u0002\\u0003|\\u0004" && Warnings(slotsFirst) == "encoding-glyph-unsupported@1:4:4",
                "a first Tj showing only a slot code and an unnamed code is silent; the warning fires once at the first Tj showing an affected code", Texts(slotsFirst) + " " + Warnings(slotsFirst));
            C(Message(slotsFirst) == prefix + "/afii10017, /Gamma); their codes are read as text bytes",
                "the message lists only the names shown by that operator, in code order and deduplicated, not /afii10018 shown later or /afii10019 never shown", Message(slotsFirst));

            var tj = Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm [(\\001) -300 (\\005) 20 (\\004\\005)] TJ (\\002) Tj ET", Type3(affected));
            C(Texts(tj) == "\\u0001 \\u0005\\u0004\\u0005|\\u0002" && Warnings(tj) == "encoding-glyph-unsupported@1:4:3"
                && Message(tj) == prefix + "/afii10018, /afii10019); their codes are read as text bytes",
                "a TJ warns once at the TJ for the names across its strings", Texts(tj) + " " + Warnings(tj) + " " + Message(tj));

            var quote = Page("BT /F1 10 Tf 12 TL 1 0 0 1 50 50 Tm (\\001) ' 1 2 (\\003) \" ET", Type3(affected));
            C(Warnings(quote) == "encoding-glyph-unsupported@1:4:5" && Message(quote) == prefix + "/Gamma); their codes are read as text bytes",
                "the ' and \" operators count as showing operators", Warnings(quote) + " " + Message(quote));

            var cmap = PdfToUnicodeTests.CMap("2 beginbfchar\n<02> <0416>\n<03> <0393>\nendbfchar", "<00> <FF>");
            string WithCmap(string differences) => Type3(differences).Replace("/Resources << >> >>", "/Resources << >> /ToUnicode 6 0 R >>", StringComparison.Ordinal);
            var covered = PdfToUnicodeTests.Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm (\\002\\003) Tj ET", WithCmap(affected), cmap);
            C(Texts(covered) == "\\u0416\\u0393" && covered.Warnings.Count == 0, "affected codes that the ToUnicode CMap maps are silent", Texts(covered) + " " + Warnings(covered));
            var uncovered = PdfToUnicodeTests.Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm (\\002\\003) Tj (\\002\\004\\003) Tj ET", WithCmap(affected), cmap);
            C(Texts(uncovered) == "\\u0416\\u0393|\\u0416\\u0004\\u0393" && Warnings(uncovered) == "encoding-glyph-unsupported@1:4:4"
                && Message(uncovered) == prefix + "/afii10018); codes that the ToUnicode CMap does not map are read as text bytes",
                "with ToUnicode the warning fires at the first Tj showing a code the CMap does not map and lists only that name", Texts(uncovered) + " " + Warnings(uncovered) + " " + Message(uncovered));

            var outside = Page("(\\002) Tj BT /F1 10 Tf 1 0 0 1 50 50 Tm (\\002) Tj ET", Type3(affected));
            C(Warnings(outside) == "text-outside-bt@1:4:0;encoding-glyph-unsupported@1:4:4",
                "a Tj with no font selected does not warn; the Type3 font warns at its own showing Tj", Warnings(outside));

            // Review: a show that emits no text (outside BT ... ET) does not consume the pending warning.
            var stray = Page("BT /F1 10 Tf ET (\\002) Tj BT 1 0 0 1 50 50 Tm (\\002) Tj ET", Type3(affected));
            C(Texts(stray) == "\\u0002" && Warnings(stray) == "text-outside-bt@1:4:3;encoding-glyph-unsupported@1:4:6"
                && Message(stray) == prefix + "/afii10017); their codes are read as text bytes",
                "a Tj outside BT ... ET with the Type3 font selected keeps only text-outside-bt; the warning fires at the in-BT Tj that shows the code", Texts(stray) + " " + Warnings(stray));

            // Review: ' and " that show an affected code warn at their own index.
            var quoteFirst = Page("BT /F1 10 Tf 12 TL 1 0 0 1 50 50 Tm (\\002) ' 1 2 (\\004) \" ET", Type3(affected));
            C(Warnings(quoteFirst) == "encoding-glyph-unsupported@1:4:4" && Message(quoteFirst) == prefix + "/afii10017); their codes are read as text bytes",
                "a ' showing an affected code warns at the ' and the later \" stays silent", Warnings(quoteFirst) + " " + Message(quoteFirst));
            var quoteOnly = Page("BT /F1 10 Tf 12 TL 1 0 0 1 50 50 Tm 1 2 (\\005\\004) \" ET", Type3(affected));
            C(Warnings(quoteOnly) == "encoding-glyph-unsupported@1:4:4" && Message(quoteOnly) == prefix + "/afii10018, /afii10019); their codes are read as text bytes",
                "a \" showing affected codes warns at the \"", Warnings(quoteOnly) + " " + Message(quoteOnly));

            // Review: Form XObjects. Operators of the form are indexed within the form stream (object 8).
            static (int Number, byte[] Body) Form(string resources, string content) =>
                (8, PdfTests.StreamBody("/Type /XObject /Subtype /Form /BBox [0 0 600 400]" + resources, L(content)));
            string Paths(PdfDocument document) => string.Join("|", document.Pages.SelectMany(p => p.Elements).OfType<PdfText>()
                .Select(t => I(t.Provenance.ObjectNumber) + ":" + I(t.Provenance.OperatorIndex) + "@" + string.Join(">", t.Provenance.XObjectPath)));
            // Page: BT 0, Tf 1, Tm 2, ET 3, Do 4, BT 5, Tm 6, Tj 7, ET 8. Form: BT 0, Tm 1, Tj 2, ET 3.
            var inherited = PdfToUnicodeTests.Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm ET /X1 Do BT 1 0 0 1 50 80 Tm (\\003) Tj ET", Type3(affected), null,
                "/XObject << /X1 8 0 R >>", Form("", "BT 1 0 0 1 10 10 Tm (\\002) Tj ET"));
            C(Texts(inherited) == "\\u0002|\\u0003" && Paths(inherited) == "8:2@o4:i4|4:7@" && Warnings(inherited) == "encoding-glyph-unsupported@1:8:2"
                && Message(inherited) == prefix + "/afii10017); their codes are read as text bytes",
                "a page Tf then a form showing an affected code with the same font dictionary warns once, at the form's Tj; the later page show is silent",
                Texts(inherited) + " " + Paths(inherited) + " " + Warnings(inherited) + " " + Message(inherited));
            // Same font dictionary reached through the form's own resources and Tf: still one warning for the page.
            var sameDictionary = PdfToUnicodeTests.Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm ET /X1 Do BT 1 0 0 1 50 80 Tm (\\003) Tj ET", Type3(affected), null,
                "/XObject << /X1 8 0 R >>", Form(" /Resources << /Font << /F1 5 0 R >> >>", "BT /F1 10 Tf 1 0 0 1 10 10 Tm (\\002) Tj ET"));
            C(Warnings(sameDictionary) == "encoding-glyph-unsupported@1:8:3", "a form selecting the same font dictionary through its own resources shares the page's single warning",
                Warnings(sameDictionary));
            // Page: BT 0, Tf 1, Tm 2, Tj 3, ET 4, Do 5. Form: BT 0, Tf 1, Tm 2, Tj 3, ET 4; its /F1 is object 9.
            var ownFont = PdfToUnicodeTests.Page("BT /F1 10 Tf 1 0 0 1 50 50 Tm (\\002) Tj ET /X1 Do", Type3(affected), null,
                "/XObject << /X1 8 0 R >>", Form(" /Resources << /Font << /F1 9 0 R >> >>", "BT /F1 10 Tf 1 0 0 1 10 10 Tm (\\002) Tj ET"), (9, L(Type3(affected))));
            var ownMessages = string.Join(" / ", ownFont.Warnings.Select(w => w.Message));
            C(Warnings(ownFont) == "encoding-glyph-unsupported@1:4:3;encoding-glyph-unsupported@1:8:3"
                && ownMessages == prefix + "/afii10017); their codes are read as text bytes / " + prefix.Replace("(object 5)", "(object 9)", StringComparison.Ordinal) + "/afii10017); their codes are read as text bytes",
                "a form whose /F1 resolves to a different font dictionary warns on its own", Warnings(ownFont) + " " + ownMessages);
        }
        catch (Exception error) { C(false, "shown unexpected", error.GetType().Name + ": " + error.Message); }
    }

    private static string Show(string literal) => "BT /F1 10 Tf 1 0 0 1 50 50 Tm (" + literal + ") Tj ET";
}
