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
// P6-25: the font decoder reads the affected names through PdfSimpleEncoding.UnsupportedGlyphListName (static check);
// one font dictionary under two resource names warns once, named by the resource active at the showing operator; a
// show at a non-finite position keeps the warning pending.
// P6-27: a show at a finite position whose text height is non-finite (Tf size x CTM) also keeps it pending; equal direct
// (not indirect) font dictionaries under two resource names are separate fonts and warn once each (the decoder cache is
// keyed by dictionary identity); the static check also runs on sources stripped of comments and literals. Known limit
// of that stripper: an interpolation hole is not parsed, so a quote or a nested literal inside a hole (for example
// $"{F('"')}..." or $"{(b ? @"q" : "r")}") ends or restarts the literal early; the swallow guard catches a run to EOF.
// P6-31: a show inside a Form XObject whose /Matrix makes the text height non-finite keeps the warning pending for the
// next emitting show, inside the form or after it; a Ts rise that overflows the text position is a non-finite text
// position (operand-error, no text) and likewise keeps it pending; the P6-25 static check reads stripped sources.
// P6-33: a form /Matrix whose composition with the CTM is non-finite gives operand-error at the Do and the form runs under
// the outer CTM (as cm does); nested form matrices that only together overflow the height and a skewed Tm whose rise
// overflows only x keep the warning pending; one shared swallow guard (ordered member anchors) serves every static check.
// P6-35: a present form /Matrix that is not six numbers gives operand-error at the Do (identity used); cm and Do message
// shapes are pinned; the Do operand-errors are not in the cad-brief CLI document, which equals the in-process bytes.
// P6-37: a /Matrix entry that would be NaN or Infinity is not a PDF number token, so the /Matrix is malformed (identity);
// each Do of a bad form warns (no warn-once); the CLI check's mirrored CadCommand options are pinned against its source.
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

        // ---------- P6-25: hygiene ----------
        try
        {
            const string prefix = " (object 5) is a Type3 font whose /Differences names glyph-list names the reader does not map (";
            const string affected = "[1 /g1 /afii10017 /Gamma /afii10018 /afii10019]";
            string Messages(PdfDocument document) => string.Join(" / ", document.Warnings.Select(w => w.Message));

            // One font dictionary (object 5) reachable as /F1 and /F3. Operators: BT 0, Tf 1, Tm 2, Tj 3, Tf 4, Tj 5, Tf 6, Tj 7, ET 8.
            PdfDocument TwoNames(string content) => PdfReader.Parse(PdfTests.Build(new List<(int Number, byte[] Body)>
            {
                (1, L("<< /Type /Catalog /Pages 2 0 R >>")),
                (2, L("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")),
                (3, L("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 400] /Resources << /Font << /F1 5 0 R /F3 5 0 R >> >> /Contents 4 0 R >>")),
                (4, PdfTests.StreamBody("", L(content))),
                (5, L(Type3(affected))),
            }), "type3-glyph-list-names.pdf");
            var second = TwoNames("BT /F1 10 Tf 1 0 0 1 50 50 Tm (\\001) Tj /F3 10 Tf (\\002) Tj /F1 10 Tf (\\002\\003) Tj ET");
            C(Texts(second) == "\\u0001|\\u0002|\\u0002\\u0003" && Warnings(second) == "encoding-glyph-unsupported@1:4:5"
                && Messages(second) == "font /F3" + prefix + "/afii10017); their codes are read as text bytes",
                "a font dictionary reachable under two resource names warns once, named by the resource active at the showing operator (P6-25)",
                Texts(second) + " " + Warnings(second) + " " + Messages(second));
            var first = TwoNames("BT /F3 10 Tf 1 0 0 1 50 50 Tm (\\001) Tj /F1 10 Tf (\\002) Tj /F3 10 Tf (\\002\\003) Tj ET");
            C(Warnings(first) == "encoding-glyph-unsupported@1:4:5" && Messages(first) == "font /F1" + prefix + "/afii10017); their codes are read as text bytes",
                "the name comes from the showing operator's resource, not the resource that first selected the dictionary (P6-25)", Warnings(first) + " " + Messages(first));

            // A show at a non-finite position (CTM 1e300 times a text matrix of 1e60) emits no text and keeps the warning
            // pending. Operators: BT 0, Tf 1, ET 2, q 3, cm 4-8, BT 9, Tm 10, Tj 11, ET 12, Q 13, BT 14, Tm 15, Tj 16, ET 17.
            var big = "1" + new string('0', 60);
            var scale = big + " 0 0 " + big + " 0 0 cm ";
            var infinite = Page("BT /F1 10 Tf ET q " + string.Concat(Enumerable.Repeat(scale, 5)) + "BT " + big + " 0 0 " + big + " 0 0 Tm (\\002) Tj ET Q BT 1 0 0 1 50 50 Tm (\\002\\004) Tj ET",
                Type3(affected));
            C(Texts(infinite) == "\\u0002\\u0004" && Warnings(infinite) == "operand-error@1:4:11;encoding-glyph-unsupported@1:4:16"
                && Messages(infinite) == "Tj gives a non-finite text position / font /F1" + prefix + "/afii10017, /afii10018); their codes are read as text bytes",
                "a show at a non-finite position keeps only operand-error; the warning fires at the next show that emits text (P6-25)",
                Texts(infinite) + " " + Warnings(infinite) + " " + Messages(infinite));

            // Static check: the font decoder reads affected names through PdfSimpleEncoding.UnsupportedGlyphListName(code)
            // and the glyph-list filter is applied in exactly one place. P6-31: the sources are read through
            // CodePins.StripCommentsAndLiterals (comments and literals removed, P6-27) under the same swallow guard.
            var adapter = Path.Combine(fixtures, "..", "..", "..", "adapters", "cad", "Bim.Cad");
            static string Code(string path) => CodePins.StripCommentsAndLiterals(File.ReadAllText(path));
            var content = Code(Path.Combine(adapter, "PdfContent.cs"));
            var encoding = Code(Path.Combine(adapter, "PdfEncoding.cs"));
            static int Count(string text, string token) => (text.Length - text.Replace(token, "", StringComparison.Ordinal).Length) / token.Length;
            foreach (var (source, sourceName, anchors) in new[] { (content, "PdfContent.cs", ContentAnchors), (encoding, "PdfEncoding.cs", EncodingAnchors) })
            {
                var guard = SwallowGuard(source, anchors);
                C(guard is null, "the P6-25 static check reads stripped " + sourceName + " under the shared swallow guard (balanced braces, final '}', every member anchor once and in order; P6-31, P6-33)",
                    guard ?? "");
            }
            C(Count(content, "IsGlyphListName") == 0 && Count(content, ".UnsupportedGlyphListName(") == 1,
                "the font decoder reads affected names through one PdfSimpleEncoding per-code accessor (P6-25 static check)",
                Count(content, "IsGlyphListName") + " " + Count(content, ".UnsupportedGlyphListName("));
            C(Count(encoding, "public string? UnsupportedGlyphListName(byte code)") == 1 && Count(encoding, "PdfGlyphNames.IsGlyphListName(") == 1,
                "PdfSimpleEncoding applies the glyph-list filter once, in the per-code accessor (P6-25 static check)", Count(encoding, "PdfGlyphNames.IsGlyphListName(").ToString(CultureInfo.InvariantCulture));
            const string signature = "public string? UnsupportedGlyphListName(byte code)";
            var (bodyStart, bodyEnd) = Body(encoding, encoding.IndexOf(signature, StringComparison.Ordinal), signature.Length);
            var call = encoding.IndexOf("PdfGlyphNames.IsGlyphListName(", StringComparison.Ordinal);
            C(bodyStart >= 0 && call > bodyStart && call < bodyEnd,
                "the single PdfGlyphNames.IsGlyphListName call in PdfEncoding.cs lies inside the UnsupportedGlyphListName accessor body (P6-25 static check)",
                I(bodyStart) + ".." + I(bodyEnd) + " call " + I(call));
        }
        catch (Exception error) { C(false, "hygiene unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- P6-25: PdfSimpleEncoding.UnsupportedGlyphListName per code ----------
        try
        {
            var file = new PdfFile(Array.Empty<byte>(), "accessor.pdf", new PdfReadOptions());
            var dictionary = new Dictionary<string, object>
            {
                ["Differences"] = new List<object> { new PdfNumber(1, true), new PdfName("g1"), new PdfName("eacute"), new PdfName("afii10017"), new PdfName("Alpha.sc") },
            };
            var parsed = PdfSimpleEncoding.Get(file, dictionary);
            string Name(byte code) => parsed.UnsupportedGlyphListName(code) ?? "null";
            C(Name(1) == "null", "a slot name /g1 gives no unsupported glyph-list name", Name(1));
            C(Name(2) == "null", "a name the reader maps (/eacute) gives no unsupported glyph-list name", Name(2));
            C(Name(0) == "null" && Name(5) == "null" && Name(65) == "null" && Name(255) == "null", "a code without a /Differences entry gives no unsupported glyph-list name",
                Name(0) + " " + Name(5) + " " + Name(65) + " " + Name(255));
            C(Name(3) == "afii10017" && Name(4) == "Alpha.sc", "/afii10017 and /Alpha.sc give their names without the slash", Name(3) + " " + Name(4));
            var stripped = parsed.WithoutUnknownNames;
            var leaked = Enumerable.Range(0, 256).Where(code => stripped.UnsupportedGlyphListName((byte)code) is not null).Select(code => I(code)).ToList();
            C(leaked.Count == 0, "a WithoutUnknownNames copy gives no unsupported glyph-list name for any code", string.Join(",", leaked));
        }
        catch (Exception error) { C(false, "accessor unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- P6-27: non-finite height, duplicate direct dictionaries, static check without comments/literals ----------
        try
        {
            const string tail = " is a Type3 font whose /Differences names glyph-list names the reader does not map (";
            const string affected = "[1 /g1 /afii10017 /Gamma /afii10018 /afii10019]";
            string Messages(PdfDocument document) => string.Join(" / ", document.Warnings.Select(w => w.Message));

            // A finite text rendering matrix whose height overflows: Tf size 1e63 (the longest number the lexer reads) times a
            // CTM of 1e240 (four cm of 1e60) times a Tm of 1e10 gives a finite position (5e241) but an infinite height.
            // Operators: q 0, cm 1-4, BT 5, Tf 6, Tm 7, show 8, ET 9, Q 10, BT 11, Tf 12, Tm 13, Tj 14, ET 15.
            var big = "1" + new string('0', 60);
            var size = "1" + new string('0', 63);
            var scale = big + " 0 0 " + big + " 0 0 cm ";
            string Tall(string show) => "q " + string.Concat(Enumerable.Repeat(scale, 4)) + "BT /F1 " + size + " Tf 1 0 0 10000000000 50 50 Tm " + show
                + " ET Q BT /F1 10 Tf 1 0 0 1 50 50 Tm (\\002) Tj ET";
            foreach (var (show, op) in new[] { ("(\\004) Tj", "Tj"), ("[(\\004) -300 (\\004)] TJ", "TJ"), ("12 TL (\\004) '", "'") })
            {
                // The dropped show marks only code 4 (/afii10018) and the next show only code 2 (/afii10017): the warning lists
                // /afii10017 alone, so marks of the dropped show are not carried over (P6-27 review).
                // ' : TL is operator 8 and ' is 9, shifting the later operators by one.
                var shift = op == "'" ? 1 : 0;
                var tall = Page(Tall(show), Type3(affected));
                C(Texts(tall) == "\\u0002"
                    && Warnings(tall) == "operand-error@1:4:" + I(8 + shift) + ";encoding-glyph-unsupported@1:4:" + I(14 + shift)
                    && Messages(tall) == op + " gives a non-finite text height / font /F1 (object 5)" + tail + "/afii10017); their codes are read as text bytes",
                    "a " + op + " at a finite position with a non-finite text height (Tf size x CTM) keeps only operand-error; the warning fires at the next show that emits text and lists only its own names (P6-27)",
                    Texts(tall) + " " + Warnings(tall) + " " + Messages(tall));
            }

            // Two resource names holding equal direct (not indirect) font dictionaries: each dictionary is a separate font with
            // its own decoder, so each warns once at its own first showing operator, without an object number (P6-27, by
            // design: the decoder cache is keyed by dictionary identity, not by content).
            // Operators: BT 0, Tf 1, Tm 2, Tj 3, Tf 4, Tj 5, Tf 6, Tj 7, Tf 8, Tj 9, ET 10.
            var direct = PdfReader.Parse(PdfTests.Build(new List<(int Number, byte[] Body)>
            {
                (1, L("<< /Type /Catalog /Pages 2 0 R >>")),
                (2, L("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")),
                (3, L("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 400] /Resources << /Font << /F1 " + Type3(affected) + " /F3 " + Type3(affected) + " >> >> /Contents 4 0 R >>")),
                (4, PdfTests.StreamBody("", L("BT /F1 10 Tf 1 0 0 1 50 50 Tm (\\002) Tj /F3 10 Tf (\\002\\003) Tj /F1 10 Tf (\\003\\004) Tj /F3 10 Tf (\\005) Tj ET"))),
            }), "type3-glyph-list-direct.pdf");
            C(Texts(direct) == "\\u0002|\\u0002\\u0003|\\u0003\\u0004|\\u0005" && Warnings(direct) == "encoding-glyph-unsupported@1:4:3;encoding-glyph-unsupported@1:4:5"
                && Messages(direct) == "font /F1" + tail + "/afii10017); their codes are read as text bytes / font /F3" + tail + "/afii10017, /Gamma); their codes are read as text bytes",
                "two resource names with equal direct font dictionaries warn once per dictionary as separate fonts, with no object number (P6-27)",
                Texts(direct) + " " + Warnings(direct) + " " + Messages(direct));

            // The stripper removes // and /* */ comments and string, verbatim, interpolated, raw and char literals.
            const string crafted = "var a = 1; // IsGlyphListName\nvar b = \"Is\\\"GlyphListName\";\nvar c = @\"x \"\" IsGlyphListName\";\nvar d = '\"'; var e = $\"{x} IsGlyphListName\";\n"
                + "var f = \"\"\"\nIsGlyphListName \"\" \n\"\"\"; /* IsGlyphListName */ var g = '\\''; IsGlyphListName(\"{\"); var @h = \"\";"
                + "\nvar p = '/'; var q = \"http://x\"; /* \" */ var r = @\"a\\\";\nvar v = @\"\"\"\"; IsGlyphListName(7);";
            var stripped = CodePins.StripCommentsAndLiterals(crafted);
            C(stripped == "var a = 1; \nvar b = \"\";\nvar c = \"\";\nvar d = ''; var e = \"\";\nvar f = \"\";   var g = ''; IsGlyphListName(\"\"); var @h = \"\";"
                + "\nvar p = ''; var q = \"\";   var r = \"\";\nvar v = \"\"; IsGlyphListName(7);",
                "the static check strips trailing // comments, block comments and string and char literals (P6-27)", Escape(stripped));

            // The P6-25 static check, re-run on the sources with comments and literals stripped.
            var adapter = Path.Combine(fixtures, "..", "..", "..", "adapters", "cad", "Bim.Cad");
            var content = CodePins.StripCommentsAndLiterals(File.ReadAllText(Path.Combine(adapter, "PdfContent.cs")));
            var encoding = CodePins.StripCommentsAndLiterals(File.ReadAllText(Path.Combine(adapter, "PdfEncoding.cs")));
            static int Count(string text, string token) => (text.Length - text.Replace(token, "", StringComparison.Ordinal).Length) / token.Length;
            // Swallow guard (P6-27 review, one shared helper since P6-33): each stripped source keeps balanced braces, ends
            // with '}' and holds every member anchor once and in order, so a literal or comment that ran to EOF or swallowed
            // a balanced stretch mid-file fails here.
            foreach (var (source, sourceName, anchors) in new[] { (content, "PdfContent.cs", ContentAnchors), (encoding, "PdfEncoding.cs", EncodingAnchors) })
            {
                var guard = SwallowGuard(source, anchors);
                C(guard is null, "stripped " + sourceName + " passes the shared swallow guard (balanced braces, final '}', every member anchor once and in order; P6-27, P6-33)",
                    guard ?? "");
            }
            C(Count(content, "IsGlyphListName") == 0 && Count(content, ".UnsupportedGlyphListName(") == 1,
                "without comments and literals the font decoder reads affected names through the one per-code accessor (P6-27 static check)",
                I(Count(content, "IsGlyphListName")) + " " + I(Count(content, ".UnsupportedGlyphListName(")));
            const string signature = "public string? UnsupportedGlyphListName(byte code)";
            var (bodyStart, bodyEnd) = Body(encoding, encoding.IndexOf(signature, StringComparison.Ordinal), signature.Length);
            var call = encoding.IndexOf("PdfGlyphNames.IsGlyphListName(", StringComparison.Ordinal);
            C(Count(encoding, signature) == 1 && Count(encoding, "PdfGlyphNames.IsGlyphListName(") == 1 && Count(encoding, "IsGlyphListName") == 2 && bodyStart >= 0 && call > bodyStart && call < bodyEnd,
                "without comments and literals PdfSimpleEncoding applies the glyph-list filter once, inside the accessor body, and names IsGlyphListName only at its definition and that call (P6-27 static check)",
                I(Count(encoding, signature)) + " bare " + I(Count(encoding, "IsGlyphListName")) + " " + I(Count(encoding, "PdfGlyphNames.IsGlyphListName(")) + " " + I(bodyStart) + ".." + I(bodyEnd) + " call " + I(call));
        }
        catch (Exception error) { C(false, "p6-27 unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- P6-31: non-finite height through a Form XObject /Matrix, text rise overflow ----------
        try
        {
            const string tail = " is a Type3 font whose /Differences names glyph-list names the reader does not map (";
            const string affected = "[1 /g1 /afii10017 /Gamma /afii10018 /afii10019]";
            string Messages(PdfDocument document) => string.Join(" / ", document.Warnings.Select(w => w.Message));
            string Message(string names) => "font /F1 (object 5)" + tail + names + "); their codes are read as text bytes";
            string Paths(PdfDocument document) => string.Join("|", document.Pages.SelectMany(p => p.Elements).OfType<PdfText>()
                .Select(t => I(t.Provenance.ObjectNumber) + ":" + I(t.Provenance.OperatorIndex) + "@" + string.Join(">", t.Provenance.XObjectPath)));
            var e60 = "1" + new string('0', 60);
            var e63 = "1" + new string('0', 63);
            var scale = e60 + " 0 0 " + e60 + " 0 0 cm ";
            // The form (object 8) has no /Resources and so selects /F1 (object 5) through the page resources.
            (int Number, byte[] Body) Form(bool withMatrix, string content) =>
                (8, PdfTests.StreamBody("/Type /XObject /Subtype /Form /BBox [0 0 600 400]" + (withMatrix ? " /Matrix [1 0 0 " + e60 + " 0 0]" : ""), L(content)));
            PdfDocument WithForm(string page, (int Number, byte[] Body) form) => PdfToUnicodeTests.Page(page, Type3(affected), null, "/XObject << /X1 8 0 R >>", form);

            // Page CTM 1e180 (three cm of 1e60); the form /Matrix scales y by a further 1e60, so the form's CTM is
            // [1e180 0 0 1e240]. A Tf size of 1e63 and a Tm of d = 1e10 give a finite position (1e181, 1e241) and a text
            // height of 1e63 x 1e250, which overflows only because of the /Matrix (without it the height is 1e253).
            var pageCtm = "q " + string.Concat(Enumerable.Repeat(scale, 3));

            // (1) The next emitting show is inside the form. Form: BT 0, Tf 1, Tm 2, Tj 3, Tf 4, Tm 5, Tj 6, ET 7.
            // Page: q 0, cm 1-3, Do 4, Q 5, BT 6, Tf 7, Tm 8, Tj 9, ET 10.
            const string inside = "BT /F1 {0} Tf 1 0 0 10000000000 10 10 Tm (\\004) Tj /F1 10 Tf 1 0 0 1 10 10 Tm (\\002) Tj ET";
            var pageAfter = pageCtm + "/X1 Do Q BT /F1 10 Tf 1 0 0 1 50 50 Tm (\\002) Tj ET";
            var formInside = WithForm(pageAfter, Form(true, string.Format(CultureInfo.InvariantCulture, inside, e63)));
            C(Texts(formInside) == "\\u0002|\\u0002" && Paths(formInside) == "8:6@o4:i4|4:9@"
                && Warnings(formInside) == "operand-error@1:8:3;encoding-glyph-unsupported@1:8:6"
                && Messages(formInside) == "Tj gives a non-finite text height / " + Message("/afii10017"),
                "a Tj inside a Form XObject whose /Matrix makes the text height non-finite keeps only operand-error; the next emitting show inside the form warns with only its own names and the later page show is silent (P6-31)",
                Texts(formInside) + " " + Paths(formInside) + " " + Warnings(formInside) + " " + Messages(formInside));
            // Control: the same form without /Matrix emits the first Tj (height 1e253) and warns there.
            var noMatrix = WithForm(pageAfter, Form(false, string.Format(CultureInfo.InvariantCulture, inside, e63)));
            C(Texts(noMatrix) == "\\u0004|\\u0002|\\u0002" && Paths(noMatrix) == "8:3@o4:i4|8:6@o4:i4|4:9@"
                && Warnings(noMatrix) == "encoding-glyph-unsupported@1:8:3" && Messages(noMatrix) == Message("/afii10018"),
                "control: without the /Matrix the same form Tj has a finite height, emits and warns at its own index (P6-31)",
                Texts(noMatrix) + " " + Paths(noMatrix) + " " + Warnings(noMatrix) + " " + Messages(noMatrix));

            // (2) The next emitting show is after the form, on the page. Form: BT 0, Tf 1, Tm 2, show 3, ET 4 (' : TL 3, ' 4, ET 5).
            foreach (var (show, op, at) in new[] { ("(\\004) Tj", "Tj", 3), ("[(\\004) -300 (\\004)] TJ", "TJ", 3), ("12 TL (\\004) '", "'", 4) })
            {
                var after = WithForm(pageAfter, Form(true, "BT /F1 " + e63 + " Tf 1 0 0 10000000000 10 10 Tm " + show + " ET"));
                C(Texts(after) == "\\u0002" && Paths(after) == "4:9@"
                    && Warnings(after) == "operand-error@1:8:" + I(at) + ";encoding-glyph-unsupported@1:4:9"
                    && Messages(after) == op + " gives a non-finite text height / " + Message("/afii10017"),
                    "a " + op + " inside a Form XObject whose /Matrix makes the text height non-finite leaves the warning pending; it fires at the next emitting show after the form, on the page (P6-31)",
                    Texts(after) + " " + Paths(after) + " " + Warnings(after) + " " + Messages(after));
            }

            // (3) A Ts rise that overflows the text position: documented as a non-finite text position, so the show emits no
            // text, gives operand-error '<op> gives a non-finite text position' and leaves the warning pending (P6-31).
            // CTM 1e240 (four cm), Tm d = 1e10 and Ts 1e63: rise x d = 1e73 is finite, times the CTM 1e313 overflows; the
            // height (10 x 1e250) stays finite. After 0 Ts the next show in the same BT is at a finite position and warns.
            // Operators: q 0, cm 1-4, BT 5, Tf 6, Tm 7, Ts 8, show 9, Ts 10, Tj 11, ET 12, Q 13 (' : TL 8 shifts by one).
            string Rise(string rise, string show) => "q " + string.Concat(Enumerable.Repeat(scale, 4)) + "BT /F1 10 Tf 1 0 0 10000000000 50 50 Tm "
                + show.Replace("RISE", rise + " Ts", StringComparison.Ordinal) + " 0 Ts (\\002) Tj ET Q";
            foreach (var (show, op) in new[] { ("RISE (\\004) Tj", "Tj"), ("RISE [(\\004) -300 (\\004)] TJ", "TJ"), ("12 TL RISE (\\004) '", "'") })
            {
                var shift = op == "'" ? 1 : 0;
                var risen = Page(Rise(e63, show), Type3(affected));
                C(Texts(risen) == "\\u0002" && Warnings(risen) == "operand-error@1:4:" + I(9 + shift) + ";encoding-glyph-unsupported@1:4:" + I(11 + shift)
                    && Messages(risen) == op + " gives a non-finite text position / " + Message("/afii10017"),
                    "a " + op + " whose Ts rise overflows the text position keeps only operand-error (non-finite text position); the warning fires at the next emitting show with only its own names (P6-31)",
                    Texts(risen) + " " + Warnings(risen) + " " + Messages(risen));
            }
            // Control: a rise of 1e50 gives a finite position (1e300), so the first Tj emits and warns there.
            var e50 = "1" + new string('0', 50);
            var lower = Page(Rise(e50, "RISE (\\004) Tj"), Type3(affected));
            C(Texts(lower) == "\\u0004|\\u0002" && Warnings(lower) == "encoding-glyph-unsupported@1:4:9" && Messages(lower) == Message("/afii10018"),
                "control: a Ts rise that does not overflow emits the first Tj and warns there (P6-31)", Texts(lower) + " " + Warnings(lower) + " " + Messages(lower));
        }
        catch (Exception error) { C(false, "p6-31 unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- P6-33: non-finite form matrix composition, nested form matrices, skewed rise, shared swallow guard ----------
        try
        {
            const string tail = " is a Type3 font whose /Differences names glyph-list names the reader does not map (";
            const string affected = "[1 /g1 /afii10017 /Gamma /afii10018 /afii10019]";
            string Messages(PdfDocument document) => string.Join(" / ", document.Warnings.Select(w => w.Message));
            string Message(string names) => "font /F1 (object 5)" + tail + names + "); their codes are read as text bytes";
            string Paths(PdfDocument document) => string.Join("|", document.Pages.SelectMany(p => p.Elements).OfType<PdfText>()
                .Select(t => I(t.Provenance.ObjectNumber) + ":" + I(t.Provenance.OperatorIndex) + "@" + string.Join(">", t.Provenance.XObjectPath)));
            static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);
            string Origins(PdfDocument document) => string.Join("|", document.Pages.SelectMany(p => p.Elements).OfType<PdfText>()
                .Select(t => R(t.Origin.X) + "," + R(t.Origin.Y) + " h" + R(t.Height)));
            static bool Near(double value, double expected) => Math.Abs(value / expected - 1) < 1e-12;
            var e30 = "1" + new string('0', 30);
            var e50 = "1" + new string('0', 50);
            var e60 = "1" + new string('0', 60);
            var e63 = "1" + new string('0', 63);
            var scale = e60 + " 0 0 " + e60 + " 0 0 cm ";
            static (int Number, byte[] Body) FormObject(int number, string matrix, string resources, string content) =>
                (number, PdfTests.StreamBody("/Type /XObject /Subtype /Form /BBox [0 0 600 400]" + (matrix.Length > 0 ? " /Matrix [" + matrix + "]" : "") + resources, L(content)));
            PdfDocument WithForms(string page, params (int Number, byte[] Body)[] forms) => PdfToUnicodeTests.Page(page, Type3(affected), null, "/XObject << /X1 8 0 R >>", forms);

            // (1) A form /Matrix whose composition with the CTM is itself non-finite: page CTM 1e300 (five cm of 1e60) and
            // /Matrix [1e60 0 0 1e60 0 0] give 1e360. Documented one way (P6-33), as for cm: operand-error at the Do
            // ('Do /X1 gives a non-finite form matrix; the current matrix is kept') and the form runs under the outer CTM,
            // so its Tj is at (1e301, 1e301) with height 1e301 and warns there. Measured before P6-33: the outer CTM was
            // kept silently (no warning). Page: q 0, cm 1-5, Do 6, Q 7, BT 8, Tf 9, Tm 10, Tj 11, ET 12. Form: BT 0, Tf 1, Tm 2, Tj 3, ET 4.
            var overflowPage = "q " + string.Concat(Enumerable.Repeat(scale, 5)) + "/X1 Do Q BT /F1 10 Tf 1 0 0 1 50 50 Tm (\\002) Tj ET";
            var overflow = WithForms(overflowPage, FormObject(8, e60 + " 0 0 " + e60 + " 0 0", "", "BT /F1 10 Tf 1 0 0 1 10 10 Tm (\\004) Tj ET"));
            var formText = overflow.Pages.SelectMany(p => p.Elements).OfType<PdfText>().FirstOrDefault();
            C(Texts(overflow) == "\\u0004|\\u0002" && Paths(overflow) == "8:3@o4:i6|4:11@"
                && Warnings(overflow) == "operand-error@1:4:6;encoding-glyph-unsupported@1:8:3"
                && Messages(overflow) == "Do /X1 gives a non-finite form matrix; the current matrix is kept / " + Message("/afii10018")
                && formText is not null && Near(formText.Origin.X, 1e301) && Near(formText.Origin.Y, 1e301) && Near(formText.Height, 1e301),
                "a form /Matrix whose composition with the CTM is non-finite gives operand-error at the Do and the form runs under the outer CTM (as cm does), its Tj emitting and warning there (P6-33)",
                Texts(overflow) + " " + Paths(overflow) + " " + Warnings(overflow) + " " + Messages(overflow) + " " + Origins(overflow));
            // Control: a /Matrix of 1e-60 keeps the composition finite (1e240): no Do warning, the form Tj is at (1e241, 1e241).
            var finiteMatrix = "0." + new string('0', 59) + "1";
            var composed = WithForms(overflowPage, FormObject(8, finiteMatrix + " 0 0 " + finiteMatrix + " 0 0", "", "BT /F1 10 Tf 1 0 0 1 10 10 Tm (\\004) Tj ET"));
            var composedText = composed.Pages.SelectMany(p => p.Elements).OfType<PdfText>().FirstOrDefault();
            C(Warnings(composed) == "encoding-glyph-unsupported@1:8:3" && composedText is not null && Near(composedText.Origin.X, 1e241) && Near(composedText.Height, 1e241),
                "control: a finite composition gives no Do warning and the form Tj uses the composed matrix (P6-33)", Warnings(composed) + " " + Origins(composed));

            // (2) Nested forms whose /Matrix values only together overflow the text height: page CTM 1e180 (three cm), form 8
            // /Matrix [1 0 0 1e30 0 0] invokes form 9 /Matrix [1 0 0 1e30 0 0] (no /Resources, so form 8's /F1 = object 5).
            // Form 9's CTM is [1e180 0 0 1e240]; Tf 1e63 and Tm d = 1e10 give a finite position (1e181, 1e241) and height
            // 1e63 x 1e250 = 1e313; with either /Matrix alone the height is 1e283. Page: q 0, cm 1-3, Do 4, Q 5, BT 6, Tf 7,
            // Tm 8, Tj 9, ET 10. Form 8: Do 0. Form 9: BT 0, Tf 1, Tm 2, Tj 3, Tf 4, Tm 5, Tj 6, ET 7 (or BT 0, Tf 1, Tm 2, Tj 3, ET 4).
            var nestedPage = "q " + string.Concat(Enumerable.Repeat(scale, 3)) + "/X1 Do Q BT /F1 10 Tf 1 0 0 1 50 50 Tm (\\002) Tj ET";
            var tallThenShort = "BT /F1 " + e63 + " Tf 1 0 0 10000000000 10 10 Tm (\\004) Tj /F1 10 Tf 1 0 0 1 10 10 Tm (\\002) Tj ET";
            var tallOnly = "BT /F1 " + e63 + " Tf 1 0 0 10000000000 10 10 Tm (\\004) Tj ET";
            PdfDocument Nested(bool outerMatrix, bool innerMatrix, string innerContent) => WithForms(nestedPage,
                FormObject(8, outerMatrix ? "1 0 0 " + e30 + " 0 0" : "", " /Resources << /Font << /F1 5 0 R >> /XObject << /X2 9 0 R >> >>", "/X2 Do"),
                FormObject(9, innerMatrix ? "1 0 0 " + e30 + " 0 0" : "", "", innerContent));
            var nestedInside = Nested(true, true, tallThenShort);
            C(Texts(nestedInside) == "\\u0002|\\u0002" && Paths(nestedInside) == "9:6@o4:i4>o8:i0|4:9@"
                && Warnings(nestedInside) == "operand-error@1:9:3;encoding-glyph-unsupported@1:9:6"
                && Messages(nestedInside) == "Tj gives a non-finite text height / " + Message("/afii10017"),
                "nested forms whose /Matrix values only together make the text height non-finite keep only operand-error; the next emitting show in the inner form warns with only its own names (P6-33)",
                Texts(nestedInside) + " " + Paths(nestedInside) + " " + Warnings(nestedInside) + " " + Messages(nestedInside));
            var nestedAfter = Nested(true, true, tallOnly);
            C(Texts(nestedAfter) == "\\u0002" && Paths(nestedAfter) == "4:9@"
                && Warnings(nestedAfter) == "operand-error@1:9:3;encoding-glyph-unsupported@1:4:9"
                && Messages(nestedAfter) == "Tj gives a non-finite text height / " + Message("/afii10017"),
                "nested forms whose /Matrix values only together overflow the height leave the warning pending to the next emitting show on the page (P6-33)",
                Texts(nestedAfter) + " " + Paths(nestedAfter) + " " + Warnings(nestedAfter) + " " + Messages(nestedAfter));
            foreach (var (withOuter, withInner) in new[] { (true, false), (false, true) })
            {
                var single = Nested(withOuter, withInner, tallThenShort);
                var heights = single.Pages.SelectMany(p => p.Elements).OfType<PdfText>().Select(t => t.Height).ToList();
                C(Texts(single) == "\\u0004|\\u0002|\\u0002" && Paths(single) == "9:3@o4:i4>o8:i0|9:6@o4:i4>o8:i0|4:9@"
                    && Warnings(single) == "encoding-glyph-unsupported@1:9:3" && Messages(single) == Message("/afii10018")
                    && heights.Count == 3 && Near(heights[0], 1e283),
                    "control: with only the " + (withOuter ? "outer" : "inner") + " /Matrix the height is 1e283, the first inner Tj emits and warns there (P6-33)",
                    Texts(single) + " " + Paths(single) + " " + Warnings(single) + " " + Messages(single) + " " + Origins(single));
            }

            // (3) A skewed Tm where only x overflows under a rise: CTM [1e240 0 0 1 0 0] (four cm of [1e60 0 0 1]) and Tm
            // [1 0 1e10 1 50 50]. A rise of 1e63 gives x = (1e63 x 1e10 + 50) x 1e240 = 1e313 (non-finite) while y = 1e63 + 50
            // and the height (10 x 1e250) stay finite: a non-finite text position, operand-error, warning pending; after 0 Ts
            // the next show is at (5e241, 50) and warns. Operators: q 0, cm 1-4, BT 5, Tf 6, Tm 7, Ts 8, show 9, Ts 10, Tj 11,
            // ET 12, Q 13 (' : TL 8 shifts by one).
            var skewCtm = PdfMatrix.Identity;
            for (var k = 0; k < 4; k++) skewCtm = new PdfMatrix(1e60, 0, 0, 1, 0, 0).Then(skewCtm);
            var skewRendering = PdfMatrix.Translation(0, 1e63).Then(new PdfMatrix(1, 0, 1e10, 1, 50, 50)).Then(skewCtm);
            C(!double.IsFinite(skewRendering.E) && double.IsFinite(skewRendering.F) && double.IsFinite(skewRendering.C) && double.IsFinite(skewRendering.D),
                "the skewed rise overflows only the x of the text position (P6-33 premise)", R(skewRendering.E) + " " + R(skewRendering.F) + " " + R(skewRendering.C) + " " + R(skewRendering.D));
            string Skew(string rise, string show) => "q " + string.Concat(Enumerable.Repeat(e60 + " 0 0 1 0 0 cm ", 4)) + "BT /F1 10 Tf 1 0 10000000000 1 50 50 Tm "
                + show.Replace("RISE", rise + " Ts", StringComparison.Ordinal) + " 0 Ts (\\002) Tj ET Q";
            foreach (var (show, op) in new[] { ("RISE (\\004) Tj", "Tj"), ("RISE [(\\004) -300 (\\004)] TJ", "TJ"), ("12 TL RISE (\\004) '", "'") })
            {
                var shift = op == "'" ? 1 : 0;
                var skewed = Page(Skew(e63, show), Type3(affected));
                var emitted = skewed.Pages.SelectMany(p => p.Elements).OfType<PdfText>().FirstOrDefault();
                C(Texts(skewed) == "\\u0002" && Warnings(skewed) == "operand-error@1:4:" + I(9 + shift) + ";encoding-glyph-unsupported@1:4:" + I(11 + shift)
                    && Messages(skewed) == op + " gives a non-finite text position / " + Message("/afii10017")
                    && emitted is not null && Near(emitted.Origin.X, op == "'" ? (50 - 12e10) * 1e240 : 5e241) && emitted.Origin.Y == (op == "'" ? 38 : 50),
                    "a " + op + " under a skewed Tm whose rise overflows only x keeps only operand-error (non-finite text position); the next emitting show at (5e241, 50) (after ' : the line moved by -12) warns with only its own names (P6-33)",
                    Texts(skewed) + " " + Warnings(skewed) + " " + Messages(skewed) + " " + Origins(skewed));
            }
            // Control: a rise of 1e50 gives x = 1e300 (finite), so the first Tj emits and warns there.
            var skewLow = Page(Skew(e50, "RISE (\\004) Tj"), Type3(affected));
            C(Texts(skewLow) == "\\u0004|\\u0002" && Warnings(skewLow) == "encoding-glyph-unsupported@1:4:9" && Messages(skewLow) == Message("/afii10018"),
                "control: a skewed rise that does not overflow x emits the first Tj and warns there (P6-33)", Texts(skewLow) + " " + Warnings(skewLow) + " " + Messages(skewLow) + " " + Origins(skewLow));

            // (4) Shared swallow guard self-test. The P6-25/P6-27 guard (balanced braces, final '}', first and last member)
            // missed a balanced mid-file over-strip; the shared guard requires every anchor once and in order.
            static bool OldGuard(string text, string first, string last) =>
                text.Count(c => c == '{') == text.Count(c => c == '}') && text.TrimEnd().EndsWith('}') && Occurrences(text, first) == 1 && Occurrences(text, last) == 1;
            // A real over-strip by StripCommentsAndLiterals (its known interpolation-hole limit): the "/*" inside the hole
            // opens a block comment that runs to the "*/" in Spacer, swallowing a balanced stretch.
            const string sample = "class S\n{\n    void First() { var s = $\"{F(\"/*\")}\"; }\n    void Middle() { if (x) { y(); } }\n    void Spacer() { /* */ }\n    void Last() { }\n}\n";
            string[] sampleAnchors = { "void First()", "void Middle()", "void Spacer()", "void Last()" };
            var overStripped = CodePins.StripCommentsAndLiterals(sample);
            C(overStripped == "class S\n{\n    void First() { var s = \"\"  }\n    void Last() { }\n}\n",
                "self-test premise: a block comment opened inside an interpolation hole swallows a balanced mid-file stretch (P6-33)", Escape(overStripped));
            C(OldGuard(overStripped, "void First()", "void Last()") && SwallowGuard(overStripped, sampleAnchors) == "void Middle() x0; void Spacer() x0",
                "the shared swallow guard catches a balanced mid-file over-strip that the old first/last guard let through (P6-33)",
                OldGuard(overStripped, "void First()", "void Last()") + " " + (SwallowGuard(overStripped, sampleAnchors) ?? "passed"));
            var intact = sample.Replace("$\"{F(\"/*\")}\"", "1", StringComparison.Ordinal);
            C(SwallowGuard(CodePins.StripCommentsAndLiterals(intact), sampleAnchors) is null && SwallowGuard(intact, Enumerable.Reverse(sampleAnchors).ToArray()) == "void Spacer() out of order; void Middle() out of order; void First() out of order",
                "the shared swallow guard passes an intact sample and reports anchors out of order (P6-33)",
                (SwallowGuard(CodePins.StripCommentsAndLiterals(intact), sampleAnchors) ?? "passed") + " / " + (SwallowGuard(intact, Enumerable.Reverse(sampleAnchors).ToArray()) ?? "passed"));
            // The same on the real stripped PdfContent.cs: cutting the balanced HasCurrent member out passes the old guard
            // but not the shared one.
            var adapter = Path.Combine(fixtures, "..", "..", "..", "adapters", "cad", "Bim.Cad");
            var content = CodePins.StripCommentsAndLiterals(File.ReadAllText(Path.Combine(adapter, "PdfContent.cs")));
            var cutStart = content.IndexOf("    private bool HasCurrent(", StringComparison.Ordinal);
            var cutEnd = content.IndexOf("    private void AddLine(", StringComparison.Ordinal);
            var cut = cutStart >= 0 && cutEnd > cutStart ? content.Remove(cutStart, cutEnd - cutStart) : content;
            C(SwallowGuard(content, ContentAnchors) is null && cut.Length < content.Length && OldGuard(cut, "FontDecoder(string name", "private void InvokeXObject(string name")
                && SwallowGuard(cut, ContentAnchors) == "private bool HasCurrent( x0",
                "on the real stripped PdfContent.cs a balanced member cut mid-file passes the old guard and fails the shared one (P6-33)",
                I(cutStart) + ".." + I(cutEnd) + " " + (SwallowGuard(content, ContentAnchors) ?? "passed") + " / " + (SwallowGuard(cut, ContentAnchors) ?? "passed"));
        }
        catch (Exception error) { C(false, "p6-33 unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- P6-35: malformed form /Matrix, cm and Do message shapes, the Do operand-error through the cad-brief CLI ----------
        try
        {
            string Messages(PdfDocument document) => string.Join(" / ", document.Warnings.Select(w => w.Message));
            static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);
            string Origins(PdfDocument document) => string.Join("|", document.Pages.SelectMany(p => p.Elements).OfType<PdfText>()
                .Select(t => R(t.Origin.X) + "," + R(t.Origin.Y) + " h" + R(t.Height)));
            var e60 = "1" + new string('0', 60);
            // Form 8 (no /Resources, so /F2 is the page's Helvetica) with the /Matrix entry given verbatim; object 9 is the
            // number 100 for an indirect entry.
            static (int Number, byte[] Body) RawForm(string matrixEntry, string content) =>
                (8, PdfTests.StreamBody("/Type /XObject /Subtype /Form /BBox [0 0 600 400]" + matrixEntry, L(content)));
            const string formShow = "BT /F2 10 Tf 1 0 0 1 10 10 Tm (A) Tj ET";
            PdfDocument Scaled(string matrixEntry) => PdfToUnicodeTests.Page("2 0 0 2 0 0 cm /X1 Do", Type1("[1 /g1]"), null, "/XObject << /X1 8 0 R >>",
                RawForm(matrixEntry, formShow), (9, L("100")));

            // (1) A /Matrix that is present but not an array of exactly six numbers (5 or 7 entries, a name, a string or an
            // array entry, an empty array, a number or a dictionary instead of an array). Measured before P6-35: read as the
            // identity silently. Documented one way (P6-35), as cm with bad operands: operand-error at the Do
            // ('Do /X1 expects a /Matrix of 6 numbers; the identity matrix is used') and the form runs under the current CTM
            // (page CTM 2: the form Tj is at (20, 20), height 20). Page: cm 0, Do 1. Form: BT 0, Tf 1, Tm 2, Tj 3, ET 4.
            const string malformedMessage = "Do /X1 expects a /Matrix of 6 numbers; the identity matrix is used";
            foreach (var (entry, what) in new[]
            {
                (" /Matrix [1 0 0 1 100]", "5 entries"), (" /Matrix [1 0 0 1 100 0 0]", "7 entries"), (" /Matrix [1 0 0 1 /X 0]", "a name entry"),
                (" /Matrix [1 0 0 1 (100) 0]", "a string entry"), (" /Matrix [1 0 0 1 [100] 0]", "an array entry"), (" /Matrix []", "an empty array"),
                (" /Matrix 5", "a number"), (" /Matrix << /A 1 >>", "a dictionary"), (" /Matrix [1 0 0 1 7 0 R 0]", "an entry that is a font dictionary"),
            })
            {
                var malformed = Scaled(entry);
                C(Texts(malformed) == "A" && Warnings(malformed) == "operand-error@1:4:1" && Messages(malformed) == malformedMessage && Origins(malformed) == "20,20 h20",
                    "a form /Matrix with " + what + " gives operand-error at the Do and the form runs under the current CTM, text bytes unchanged (P6-35)",
                    Texts(malformed) + " " + Warnings(malformed) + " " + Messages(malformed) + " " + Origins(malformed));
            }
            // Controls: six numbers (one of them indirect) compose silently; an absent /Matrix, /Matrix null and a dangling
            // reference read as absent (the identity, silent), as before.
            foreach (var (entry, what, origin) in new[]
            {
                (" /Matrix [1 0 0 1 100 0]", "six direct numbers", "220,20 h20"), (" /Matrix [1 0 0 1 9 0 R 0]", "six numbers, one indirect", "220,20 h20"),
                ("", "no /Matrix", "20,20 h20"), (" /Matrix null", "/Matrix null", "20,20 h20"), (" /Matrix 99 0 R", "a dangling /Matrix reference", "20,20 h20"),
            })
            {
                var good = Scaled(entry);
                C(Texts(good) == "A" && Warnings(good) == "" && Origins(good) == origin,
                    "control: a form with " + what + " gives no Do warning and the form Tj is at " + origin + " (P6-35)", Texts(good) + " " + Warnings(good) + " " + Origins(good));
            }

            // (2) cm and Do messages: the same code (operand-error) and the same outcome (the matrix is not applied), but
            // different words, pinned as they are. cm reports only its operands ('cm expects 6 numbers', 'cm gives a
            // non-finite matrix'); a Do names the XObject and says what is used instead because the form still runs.
            // Page: cm 0 (4 operands), cm 1-6 (1e60 each, the sixth overflows), Do 7.
            var shapes = PdfToUnicodeTests.Page("1 0 0 1 cm " + string.Concat(Enumerable.Repeat(e60 + " 0 0 " + e60 + " 0 0 cm ", 6)) + "/X1 Do",
                Type1("[1 /g1]"), null, "/XObject << /X1 8 0 R >>", RawForm(" /Matrix [" + e60 + " 0 0 " + e60 + " 0 0]", formShow));
            C(Warnings(shapes) == "operand-error@1:4:0;operand-error@1:4:6;operand-error@1:4:7"
                && Messages(shapes) == "cm expects 6 numbers / cm gives a non-finite matrix / Do /X1 gives a non-finite form matrix; the current matrix is kept",
                "cm and Do give operand-error with different, pinned messages for bad operands and non-finite results (P6-35)", Warnings(shapes) + " " + Messages(shapes));

            // (3) The Do operand-errors through the real cad-brief CLI (interfaces/cli Release Bim.Cli.dll). The cad-brief
            // document carries only BriefExtract findings, never PdfDocument.Warnings (the documented limit of the cad-brief
            // section in docs/contracts, as for DXF reader warnings since P6-34), so neither Do warning is in it: the CLI
            // writes exactly the bytes of the document built in process, both digests pinned. Page: q 0, cm 1-5, Do 6, Q 7,
            // BT 8, Tf 9, Tm 10, Tj 11, ET 12 (overflow: /Matrix 1e60 under CTM 1e300; malformed: 5 entries).
            var root = Path.GetFullPath(Path.Combine(fixtures, "..", "..", ".."));
            var cliDll = Path.Combine(root, "interfaces", "cli", "bin", "Release", "net8.0", "Bim.Cli.dll");
            // P6-39: the expected document is built by the public Bim.Cad builder CadCommand calls (P6-41: FromPdf / FromDxf,
            // then ToUtf8Bytes), so document members and serializer settings are no longer mirrored here. What stays outside
            // the builder is pinned against interfaces/cli/CadCommand.cs read without comments and literals (so a
            // commented-out copy of a pinned line does not count; self-tests below): the 32 MiB input cap, the one
            // PdfReadOptions (MaxBytes only), the one brief-options line and the two factory calls, the bytes written being
            // the builder's, no direct BriefExtract/BriefSerializer use and no Create left in the CLI.
            // P6-41 reason the reader options stay source-pinned: CadCommand reads the input once for cad-draft and
            // cad-brief (and refuses a missing page with its own code before either), so the read is the CLI's, not the
            // brief builder's; moving it into Bim.Cad would change refusal paths shared with cad-draft.
            // P6-41 one hash: the builder hashes the input (CadBriefDocument.cs holds SHA256.HashData once); the CLI's
            // one input hash is inside the cad-draft branch (for its project id and source), so cad-brief hashes once.
            const int cliMaxInputBytes = 32 * 1024 * 1024;
            const string optionsLine = "var briefOptions = new BriefExtractOptions { MetresPerUnit = options.MetresPerUnit, IncludeBlockRegions = options.IncludeBlockRegions };";
            const string dxfCall = "CadBriefDocument.FromDxf(sourceFile, bytes, drawing, briefOptions)";
            const string builderCall = "CadBriefDocument.FromPdf(sourceFile, bytes, pdf!, page!.Value, calibration, briefOptions)";
            const string inputHash = "var sha = SHA256.HashData(bytes);";
            var cadCommandPins = new (string Line, int Count)[]
            {
                ("public const int MaxInputBytes = 32 * 1024 * 1024;", 1),
                ("new PdfReadOptions(", 1),
                ("pdf = PdfReader.Parse(bytes, sourceFile, new PdfReadOptions(MaxBytes: MaxInputBytes));", 1),
                ("CadBriefDocument.", 2),
                (optionsLine, 1),
                (dxfCall, 1),
                (builderCall, 1),
                ("CadBriefDocument.Create", 0),
                ("new BriefExtractOptions", 1),
                (".ToUtf8Bytes()", 1),
                ("BriefExtract.", 0),
                ("BriefSerializer", 0),
                ("SHA256.HashData(bytes)", 1),
                (inputHash, 1),
            };
            var cadCommandAnchors = new[] { "internal static class CadCommand", "static void Execute(bool draft", "static Options ParseOptions(", "static void Fail(" };
            // P6-47: each pin is counted with whitespace removed from both the pin and the stripped source (as the DXF
            // reader-option pin does), so a reformatted line still counts; problems name the pin as written. P6-49: through
            // CodePins (tests/cad/CodePins.cs, shared with the DXF pin): one space is kept between identifier characters and
            // a pin counts only at identifier boundaries (xpdf = PdfReader.Parse is not the pinned pdf = read).
            static string? PinProblems(string source, IEnumerable<(string Line, int Count)> pins)
            {
                var squeezed = CodePins.Squeeze(source);
                // P6-51: a count-0 pin bans a name, so it is counted as a plain substring (CodePins.CountAnywhere): a longer
                // identifier such as BriefSerializerX still holds BriefSerializer and is caught.
                int Found((string Line, int Count) pin) => pin.Count == 0 ? CodePins.CountAnywhere(squeezed, CodePins.Squeeze(pin.Line)) : CodePins.Count(squeezed, CodePins.Squeeze(pin.Line));
                return string.Join("; ", pins.Where(pin => Found(pin) != pin.Count).Select(pin => "'" + pin.Line + "' x" + I(Found(pin)) + " (want " + I(pin.Count) + ")")) is { Length: > 0 } problems ? problems : null;
            }
            // The CLI's one input hash sits between the cad-draft branch's start and the brief factories (stripped code;
            // P6-49: matched squeezed, so a reformatted hash line or if (draft) keeps its place).
            static string? HashPlace(string stripped)
            {
                var squeezed = CodePins.Squeeze(stripped);
                int draftAt = CodePins.IndexOf(squeezed, CodePins.Squeeze("if (draft)")), hashAt = CodePins.IndexOf(squeezed, CodePins.Squeeze(inputHash)), briefAt = CodePins.IndexOf(squeezed, CodePins.Squeeze(dxfCall));
                return draftAt >= 0 && hashAt > draftAt && briefAt > hashAt ? null : "input hash at " + I(hashAt) + " not inside the cad-draft branch (if (draft) at " + I(draftAt) + ", FromDxf at " + I(briefAt) + ", squeezed code)";
            }
            static string? CodeProblems(string source, IEnumerable<(string Line, int Count)> pins, IReadOnlyList<string> anchors)
            {
                var stripped = CodePins.StripCommentsAndLiterals(source);
                var problems = string.Join("; ", new[] { SwallowGuard(stripped, anchors), PinProblems(stripped, pins), HashPlace(stripped) }.Where(p => p is not null));
                return problems.Length > 0 ? problems : null;
            }
            var cadCommandPath = Path.Combine(root, "interfaces", "cli", "CadCommand.cs");
            var cadCommand = File.Exists(cadCommandPath) ? File.ReadAllText(cadCommandPath).Replace("\r\n", "\n", StringComparison.Ordinal) : "";
            const string entryLabel = "p6-41 cli entry point: CadCommand.cs code (comments and literals stripped) reads the PDF with MaxBytes 32 MiB only, makes the one FromDxf/FromPdf call with one options line, writes its ToUtf8Bytes and hashes the input only for cad-draft; zero-count (banned-name) pins are counted as substrings of the squeezed code, no identifier boundary (P6-51)";
            C(cadCommand.Length > 0 && CodeProblems(cadCommand, cadCommandPins, cadCommandAnchors) is null,
                entryLabel,
                cadCommandPath + " " + (CodeProblems(cadCommand, cadCommandPins, cadCommandAnchors) ?? "pins hold"));
            var drifted = cadCommand.Replace("new PdfReadOptions(MaxBytes: MaxInputBytes)", "new PdfReadOptions(MaxBytes: MaxInputBytes, MaxWarnings: 5)", StringComparison.Ordinal);
            C(cadCommand.Length > 0 && CodeProblems(drifted, cadCommandPins, cadCommandAnchors) is { } caught && caught.Contains("MaxBytes: MaxInputBytes));", StringComparison.Ordinal),
                "p6-37 cli options self-test: an extra PdfReadOptions argument in CadCommand.cs is caught by name", CodeProblems(drifted, cadCommandPins, cadCommandAnchors) ?? "not caught");
            // P6-47 self-tests: the pins match stripped code with all whitespace removed (as the DXF reader-option pin in
            // tests/cad/MinsertEzdxfTests.cs does), so the PDF read reformatted over several lines passes, and the same
            // reformatted read with an extra PdfReadOptions argument is still caught by name.
            const string pdfRead = "pdf = PdfReader.Parse(bytes, sourceFile, new PdfReadOptions(MaxBytes: MaxInputBytes));";
            const string pdfReadSplit = "pdf = PdfReader.Parse(\n                    bytes,\n                    sourceFile,\n                    new PdfReadOptions( MaxBytes: MaxInputBytes ));";
            var reformatted = cadCommand.Replace(pdfRead, pdfReadSplit, StringComparison.Ordinal);
            C(cadCommand.Length > 0 && reformatted != cadCommand && CodeProblems(reformatted, cadCommandPins, cadCommandAnchors) is null,
                "p6-47 cli options self-test: the PDF read reformatted over several lines in CadCommand.cs does not fail the pin (whitespace-free match)",
                reformatted == cadCommand ? "self-test anchor not found" : CodeProblems(reformatted, cadCommandPins, cadCommandAnchors) ?? "tolerated");
            var reformattedDrift = cadCommand.Replace(pdfRead, pdfReadSplit.Replace("MaxBytes: MaxInputBytes )", "MaxBytes: MaxInputBytes, MaxWarnings: 5 )", StringComparison.Ordinal), StringComparison.Ordinal);
            C(cadCommand.Length > 0 && reformattedDrift != cadCommand && CodeProblems(reformattedDrift, cadCommandPins, cadCommandAnchors) is { } splitCaught && splitCaught.Contains("'" + pdfRead + "' x0", StringComparison.Ordinal),
                "p6-47 cli options self-test: an extra PdfReadOptions argument in the reformatted PDF read is caught by name",
                reformattedDrift == cadCommand ? "self-test anchor not found" : CodeProblems(reformattedDrift, cadCommandPins, cadCommandAnchors) ?? "not caught");
            // P6-49 self-tests: the whitespace-free matches keep identifier boundaries, so xpdf = PdfReader.Parse(...) is not
            // counted as the pinned pdf = read; the input hash and the cad-draft branch with their spacing changed still pass
            // the hash-place check (it matches whitespace-free too).
            var widened = cadCommand.Replace(pdfRead, "x" + pdfRead, StringComparison.Ordinal);
            C(cadCommand.Length > 0 && widened != cadCommand && CodeProblems(widened, cadCommandPins, cadCommandAnchors) is { } widenedCaught && widenedCaught.Contains("'" + pdfRead + "' x0", StringComparison.Ordinal),
                "p6-49 cli pins self-test: xpdf = PdfReader.Parse(...) in CadCommand.cs is not counted as the pinned pdf = read (identifier boundary)",
                widened == cadCommand ? "self-test anchor not found" : CodeProblems(widened, cadCommandPins, cadCommandAnchors) ?? "not caught");
            var respaced = cadCommand.Replace(inputHash, "var sha =\n                    SHA256.HashData( bytes );", StringComparison.Ordinal).Replace("if (draft)", "if(draft)", StringComparison.Ordinal);
            C(cadCommand.Length > 0 && respaced != cadCommand && Occurrences(respaced, inputHash) == 0 && CodeProblems(respaced, cadCommandPins, cadCommandAnchors) is null,
                "p6-49 hash self-test: the input hash and the cad-draft branch reformatted in CadCommand.cs still pass the hash-place check (whitespace-free)",
                respaced == cadCommand ? "self-test anchor not found" : CodeProblems(respaced, cadCommandPins, cadCommandAnchors) ?? "tolerated");
            // P6-51 zero-count self-tests: a pin with count 0 is a ban, so it is counted as a plain substring of the squeezed
            // code (no identifier boundary): a longer identifier such as BriefSerializerX or CadBriefDocument.CreateDraft
            // still holds the banned name and is caught; pins with a count above 0 keep the P6-49 boundaries.
            foreach (var (label, inserted, expect) in new[]
            {
                ("BriefSerializerX.Serialize(...)", "var serialized = BriefSerializerX.Serialize(null!);", "'BriefSerializer' x1 (want 0)"),
                ("CadBriefDocument.CreateDraft(...)", "var created = CadBriefDocument.CreateDraft(null!);", "'CadBriefDocument.Create' x1 (want 0)"),
                ("XBriefExtract.FromDxf(...)", "var extracted = XBriefExtract.FromDxf(null!);", "'BriefExtract.' x1 (want 0)"),
            })
            {
                var banned = cadCommand.Replace(optionsLine, optionsLine + "\n                " + inserted, StringComparison.Ordinal);
                C(cadCommand.Length > 0 && banned != cadCommand && CodeProblems(banned, cadCommandPins, cadCommandAnchors) is { } bannedCaught && bannedCaught.Contains(expect, StringComparison.Ordinal),
                    "p6-51 zero-count self-test: " + label + " in CadCommand.cs is caught by the zero-count pin (substring, no identifier boundary)",
                    banned == cadCommand ? "self-test anchor not found" : CodeProblems(banned, cadCommandPins, cadCommandAnchors) ?? "not caught");
            }
            // P6-49 one stripper: StripCommentsAndLiterals has one definition among the tests/cad sources, in CodePins.cs,
            // and the DXF reader-option pin calls CodePins.StripCommentsAndLiterals (no reflection on this class). P6-51: a
            // definition is block-bodied ({) or expression-bodied (=>), so a one-line forwarder counts as a second home
            // (self-test below); the P6-49 forwarder in this file is gone and its callers call CodePins directly.
            static bool DefinesStripper(string code) => StripperDefinitions(code) > 0;
            var cadTestsDir = Path.Combine(root, "tests", "cad");
            // P6-53: the aggregate is one function over (file name, source) pairs, so the planted self-test below runs it too.
            // P6-55: definitions are counted per file, so a file with two is listed as "name xN" (a second definition
            // inside CodePins.cs is red too); the scanned files are in ordinal name order.
            static int StripperDefinitions(string code) => System.Text.RegularExpressions.Regex.Count(code, @"\bstring\s*\??\s+StripCommentsAndLiterals\s*(<[^<>]*>)?\s*\([^()]*\)\s*(where\s[^{;=]*)?(\{|=>)");
            static string StripperHomes(IEnumerable<(string Name, string Source)> files) => string.Join(",", files
                .Select(f => (f.Name, Count: StripperDefinitions(CodePins.StripCommentsAndLiterals(f.Source))))
                .Where(f => f.Count > 0)
                .Select(f => f.Count == 1 ? f.Name : f.Name + " x" + I(f.Count)).Order(StringComparer.Ordinal));
            // P6-57: StripperHomes orders its own output, so the ordinal sort of the scan is redundant for the aggregate; it
            // is kept so the planted loop below runs, and lists what it missed, in one order on every OS (the order of
            // Directory.GetFiles is unspecified).
            var scanned =Directory.GetFiles(cadTestsDir, "*.cs").Select(f => (Name: Path.GetFileName(f), Source: File.ReadAllText(f))).OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();
            var stripperHomes = StripperHomes(scanned);
            var forwarder = CodePins.StripCommentsAndLiterals("    private static string StripCommentsAndLiterals(string source) => CodePins.StripCommentsAndLiterals(source);\n");
            var forwarderSplit = CodePins.StripCommentsAndLiterals("    private static string StripCommentsAndLiterals(string source)\n        => CodePins.StripCommentsAndLiterals(source);\n");
            var blockBodied = CodePins.StripCommentsAndLiterals("    internal static string StripCommentsAndLiterals(string source)\n    {\n        return source;\n    }\n");
            var callOnly = CodePins.StripCommentsAndLiterals("        var code = CodePins.StripCommentsAndLiterals(File.ReadAllText(path)); // static string StripCommentsAndLiterals(string s) => x\n");
            C(DefinesStripper(forwarder) && DefinesStripper(forwarderSplit) && DefinesStripper(blockBodied) && !DefinesStripper(callOnly),
                "p6-51 one stripper self-test: an expression-bodied StripCommentsAndLiterals forwarder (one line or split before =>) counts as a definition like a block-bodied one; a call or a commented definition does not",
                "forwarder " + DefinesStripper(forwarder) + " split " + DefinesStripper(forwarderSplit) + " block " + DefinesStripper(blockBodied) + " call " + DefinesStripper(callOnly));
            var minsertPath = Path.Combine(cadTestsDir, "MinsertEzdxfTests.cs");
            var minsertCode = File.Exists(minsertPath) ? CodePins.StripCommentsAndLiterals(File.ReadAllText(minsertPath)) : "";
            C(stripperHomes == "CodePins.cs" && Occurrences(minsertCode, "CodePins.StripCommentsAndLiterals(") >= 1 && Occurrences(minsertCode, "typeof(PdfType3GlyphListTests)") == 0,
                "p6-49 one stripper: StripCommentsAndLiterals is defined once (block- or expression-bodied, P6-51), in tests/cad/CodePins.cs, and the DXF reader-option pin calls it without reflection",
                "defined in [" + stripperHomes + "], MinsertEzdxfTests calls CodePins x" + I(Occurrences(minsertCode, "CodePins.StripCommentsAndLiterals(")) + ", typeof(PdfType3GlyphListTests) x" + I(Occurrences(minsertCode, "typeof(PdfType3GlyphListTests)")));
            // P6-53 planted second stripper: a forwarder appended (in memory, no file written) to a scanned tests/cad file
            // makes that file a second home, so the stripperHomes aggregate the p6-49 check compares with CodePins.cs is red.
            // P6-57: the forwarder is planted into every scanned file other than CodePins.cs, one at a time, and each result
            // is compared with "CodePins.cs" plus that file's own name, so a scanned file the aggregate skipped is red (the
            // P6-55 single target, the first other file, was compared with itself and could not fail); the names include
            // this file, a literal known independently of the scan.
            const string plantedForwarder = "\ninternal static class PlantedStripper\n{\n    private static string StripCommentsAndLiterals(string source) => CodePins.StripCommentsAndLiterals(source);\n}\n";
            var others = scanned.Select(f => f.Name).Where(n => n != "CodePins.cs").ToArray();
            var plantMisses = others
                .Select(target => (target, got: StripperHomes(scanned.Select(f => f.Name == target ? (f.Name, f.Source + plantedForwarder) : (f.Name, f.Source)))))
                .Where(p => p.got != string.Join(",", new[] { "CodePins.cs", p.target }.Order(StringComparer.Ordinal)))
                .Select(p => p.target + " gave [" + p.got + "]").ToArray();
            C(stripperHomes == "CodePins.cs" && others.Length >= 2 && others.Contains("PdfType3GlyphListTests.cs") && plantMisses.Length == 0,
                "p6-53 one stripper self-test: a second StripCommentsAndLiterals definition planted in each scanned tests/cad file other than CodePins.cs (one at a time, in memory; P6-57) turns the stripperHomes aggregate red with that file named",
                "real [" + stripperHomes + "], " + I(others.Length) + " other files (" + string.Join(",", others) + "), missed: " + (plantMisses.Length == 0 ? "none" : string.Join("; ", plantMisses)));
            // P6-55 per-file count: a second definition planted inside CodePins.cs itself (in memory) lists it as
            // "CodePins.cs x2", so the aggregate is red; two definitions in one snippet count 2, a call or comment 0.
            const string plantedInPins = "\ninternal static class PlantedPinsStripper\n{\n    internal static string StripCommentsAndLiterals(string source)\n    {\n        return source;\n    }\n}\n";
            var pinsTwice = StripperHomes(scanned.Select(f => f.Name == "CodePins.cs" ? (f.Name, f.Source + plantedInPins) : (f.Name, f.Source)));
            C(stripperHomes == "CodePins.cs" && pinsTwice == "CodePins.cs x2",
                "p6-55 per-file stripper count self-test: a second StripCommentsAndLiterals definition planted inside CodePins.cs (in memory) turns the stripperHomes aggregate red",
                "real [" + stripperHomes + "] planted in CodePins.cs [" + pinsTwice + "]");
            C(StripperDefinitions(forwarder + blockBodied) == 2 && StripperDefinitions(forwarder) == 1 && StripperDefinitions(callOnly) == 0,
                "p6-55 per-file stripper count self-test: a forwarder and a block-bodied definition in one source count 2, one forwarder 1, a call or a commented definition 0",
                "both " + I(StripperDefinitions(forwarder + blockBodied)) + " forwarder " + I(StripperDefinitions(forwarder)) + " call " + I(StripperDefinitions(callOnly)));
            // P6-57 'name xN' with two multi-definition files: in the real scan, CodePins.cs with a second definition and
            // this file with two give "CodePins.cs x2,PdfType3GlyphListTests.cs x2"; on synthetic files, two and three
            // definitions give "A.cs x2,B.cs x3" (entries in ordinal order), one the bare name, none no entry.
            var twoFiles = StripperHomes(scanned.Select(f => f.Name == "CodePins.cs" ? (f.Name, f.Source + plantedInPins)
                : f.Name == "PdfType3GlyphListTests.cs" ? (f.Name, f.Source + plantedForwarder + plantedForwarder) : (f.Name, f.Source)));
            var synthetic = StripperHomes(new[] { ("B.cs", plantedForwarder + plantedInPins + plantedForwarder), ("C.cs", "class C { }\n"), ("A.cs", plantedInPins + plantedForwarder), ("D.cs", plantedForwarder) });
            C(twoFiles == "CodePins.cs x2,PdfType3GlyphListTests.cs x2" && synthetic == "A.cs x2,B.cs x3,D.cs",
                "p6-57 per-file stripper count self-test: two files with two or more StripCommentsAndLiterals definitions are both listed as 'name xN' (ordinal order)",
                "real [" + twoFiles + "] synthetic [" + synthetic + "]");
            // P6-59 shared-prefix names: "A.cs" (two definitions) sorts before "A.cs b.cs" by file name, although the entry
            // "A.cs x2" sorts after "A.cs b.cs" as text ('x' > 'b'), so the entries are ordered by name, not by their text.
            var sharedPrefix = StripperHomes(new[] { ("A.cs b.cs", plantedForwarder), ("A.cs", plantedInPins + plantedForwarder) });
            C(sharedPrefix == "A.cs x2,A.cs b.cs",
                "p6-59 'name xN' ordering self-test: entries are in ordinal file-name order, so A.cs x2 comes before A.cs b.cs",
                "got [" + sharedPrefix + "]");
            // P6-57 definition forms: a generic definition (with or without a where clause), a string? return, a second
            // parameter and a non-static (local) function each count 1. Not counted (stated limits): a declaration without
            // a body (delegate, abstract, partial), a return type written String or System.String or other than string,
            // and a parameter list holding parentheses (a default value such as a call).
            foreach (var (form, snippet, want) in new[]
            {
                ("generic", "    static string StripCommentsAndLiterals<T>(string source) => source;\n", 1),
                ("generic with a where clause", "    static string StripCommentsAndLiterals<T>(string source) where T : class\n    {\n        return source;\n    }\n", 1),
                ("string? return", "    internal static string? StripCommentsAndLiterals(string source) => source;\n", 1),
                ("second parameter", "    internal static string StripCommentsAndLiterals(string source, bool keepLiterals)\n    {\n        return source;\n    }\n", 1),
                ("non-static local function", "        string StripCommentsAndLiterals(string source) => source;\n", 1),
                ("nested generic parameter list (P6-59)", "    static string StripCommentsAndLiterals<T, U<V>>(string source) => source;\n", 1),
                ("deeper nested generic with a where clause (P6-59)", "    static string StripCommentsAndLiterals<T, U<V<W>>, X>(string source) where T : IList<U<V<W>>>\n    {\n        return source;\n    }\n", 1),
                ("string? generic with two parameters", "    private static string? StripCommentsAndLiterals<TOptions>(string source, TOptions options) where TOptions : new() => source;\n", 1),
                ("delegate declaration (no body)", "    internal delegate string StripCommentsAndLiterals(string source);\n", 0),
                ("call assigned to a string", "        string code = StripCommentsAndLiterals(source);\n", 0),
            })
            {
                var got = StripperDefinitions(CodePins.StripCommentsAndLiterals(snippet));
                C(got == want, "p6-57 stripper definition forms self-test: a " + form + " counts " + I(want), "got " + I(got));
            }
            // P6-53 CodePins direct self-tests on squeezed snippets. Assignments counts = and the compound operators (longest
            // first, so >>= and >>>= are read whole) to an identifier at its boundaries, not ==, !=, <=, >= or =>; ++ and --
            // are not counted (stated limit: squeezed, x - -1 reads as x--1, so they cannot be told apart from a subtraction).
            foreach (var (snippet, want) in new[]
            {
                ("x=1;", 1), ("x+=1;", 1), ("x-=1;", 1), ("x??=y;", 1), ("x>>=1;", 1), ("x<<=1;", 1), ("x>>>=1;", 1), ("x=1;x*=2;", 2),
                ("x==1;", 0), ("x!=1;", 0), ("x<=1;", 0), ("x>=1;", 0), ("x=>1;", 0), ("x++;", 0), ("x--;", 0), ("++x;", 0), ("xx=1;", 0),
            })
            {
                C(CodePins.Assignments(snippet, "x") == want, "p6-53 CodePins.Assignments self-test: '" + snippet + "' has " + I(want) + " assignment(s) to x",
                    "got " + I(CodePins.Assignments(snippet, "x")));
            }
            // CountAnywhere is the zero-count (ban) rule: non-overlapping substrings, no identifier boundary; Count keeps it.
            foreach (var (haystack, needle, anywhere, bounded) in new[]
            {
                ("varz=BriefSerializerX.Serialize();", "BriefSerializer", 1, 0), ("XBriefExtract.FromDxf();", "BriefExtract.", 1, 0),
                ("BriefSerializer;BriefSerializer;", "BriefSerializer", 2, 2), ("aaaa", "aa", 2, 0), ("abc", "", 0, 0), ("", "x", 0, 0),
            })
            {
                C(CodePins.CountAnywhere(haystack, needle) == anywhere && CodePins.Count(haystack, needle) == bounded,
                    "p6-53 CodePins.CountAnywhere self-test: '" + needle + "' in '" + haystack + "' counts " + I(anywhere) + " as a substring, " + I(bounded) + " at identifier boundaries",
                    "anywhere " + I(CodePins.CountAnywhere(haystack, needle)) + " bounded " + I(CodePins.Count(haystack, needle)));
            }
            // P6-53 wording: the p6-41 entry label states the substring rule of the zero-count pins; the comment above
            // SwallowGuard (next to where the P6-49 forwarder was) names CodePins.StripCommentsAndLiterals; no doubled blank line.
            // P6-55: the comment is found from the one signature occurrence that is code (it survives stripping), not from
            // LastIndexOf of the signature text, and is the text after the previous blank line that strips to whitespace;
            // null when the signature is not code exactly once or the text above it is not comment only.
            // P6-57: why names the branch taken: "code xN" (raw occurrences that are code), "stripped xN" (occurrences in
            // the stripped source; it differs when a block comment joins the signature, as in static/**/string?, which
            // is code but not raw text), "code above" or "comment". A whitespace-only line counts as blank, and attribute
            // lines (each a [...] on its own line) between the comment and the signature are skipped. Stated limits: a
            // comment split by a blank line gives only the part below it, a block comment holding a blank line gives
            // "code above", and an attribute on the signature line itself is code above.
            static int LineStart(string text, int at) => at <= 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1;
            static string? CommentAbove(string source, string signature, out string why)
            {
                var code = new List<int>();
                for (var at = source.IndexOf(signature, StringComparison.Ordinal); at >= 0; at = source.IndexOf(signature, at + 1, StringComparison.Ordinal))
                {
                    if (CodePins.StripCommentsAndLiterals(source[..(at + signature.Length)]).EndsWith(signature, StringComparison.Ordinal)) code.Add(at);
                }
                var stripped = Occurrences(CodePins.StripCommentsAndLiterals(source), signature);
                if (code.Count != 1) { why = "code x" + I(code.Count); return null; }
                if (stripped != 1) { why = "stripped x" + I(stripped); return null; }
                // Attribute lines directly above the signature line are skipped: the comment ends at the first one's text.
                var end = code[0];
                for (var line = LineStart(source, end); line > 0;)
                {
                    var above = LineStart(source, line - 1);
                    var text = source[above..(line - 1)];
                    var bare = CodePins.StripCommentsAndLiterals(text).Trim();
                    if (!(bare.StartsWith('[') && bare.EndsWith(']'))) break;
                    end = above + (text.Length - text.TrimStart().Length);
                    line = above;
                }
                // The comment starts on the line after the nearest line above it that is empty or whitespace only.
                var start = LineStart(source, end);
                while (start > 0)
                {
                    var above = LineStart(source, start - 1);
                    if (source[above..(start - 1)].Trim().Length == 0) break;
                    start = above;
                }
                var comment = source[start..end];
                if (CodePins.StripCommentsAndLiterals(comment).Trim().Length != 0) { why = "code above"; return null; }
                why = "comment";
                return comment;
            }
            const string guardSignature = "private static string? SwallowGuard(";
            var selfSource = File.ReadAllText(Path.Combine(cadTestsDir, "PdfType3GlyphListTests.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
            var guardComment = CommentAbove(selfSource, guardSignature, out var guardWhy);
            C(entryLabel.Contains("zero-count", StringComparison.Ordinal) && entryLabel.Contains("substring", StringComparison.Ordinal),
                "p6-53 wording: the p6-41 entry label states that zero-count pins are counted as substrings", entryLabel);
            C(guardComment is not null && guardComment.Contains("CodePins.StripCommentsAndLiterals", StringComparison.Ordinal),
                "p6-53 wording: the comment above SwallowGuard names CodePins.StripCommentsAndLiterals", guardComment?.Trim() ?? guardWhy);
            // P6-55 anchored self-tests: the signature text repeated in a later string literal (where LastIndexOf lands) or
            // in a comment still finds the comment above the code definition; a signature only in a comment or a literal,
            // or one with code between it and the blank line above, gives null. P6-57: each case also pins the branch
            // (why), and the whitespace-only line, attribute, blank-line split and joined-signature cases are added.
            const string guardDefinition = "    private static string? SwallowGuard(string s) => s;\n";
            const string literalLater = "\n    private static string Later() => \"private static string? SwallowGuard(\";\n}\n";
            foreach (var (what, sample, want, wantWhy) in new (string, string, string?, string)[]
            {
                ("repeated in a later string literal", "class S\n{\n\n    // the guard comment\n" + guardDefinition + literalLater, "    // the guard comment\n    ", "comment"),
                ("repeated in a later comment", "class S\n{\n\n    /* the guard\n       comment */\n" + guardDefinition + "\n    // see private static string? SwallowGuard( above\n}\n", "    /* the guard\n       comment */\n    ", "comment"),
                ("only in a comment", "class S\n{\n    // private static string? SwallowGuard(string s) => s;\n}\n", null, "code x0"),
                ("only in a string literal", "class S\n{" + literalLater, null, "code x0"),
                ("with code above it", "class S\n{\n    // a comment\n    private int x;\n" + guardDefinition + "}\n", null, "code above"),
                ("defined twice", "class S\n{\n\n    // the guard comment\n" + guardDefinition + guardDefinition + "}\n", null, "code x2"),
                ("also joined by a block comment (code once raw, twice stripped)", "class S\n{\n\n    // the guard comment\n    private static/* joined */string? SwallowGuard(int s) => s;\n" + guardDefinition + "}\n", null, "stripped x2"),
                ("after a whitespace-only line", "class S\n{\n    private int x;\n    \n    // the guard comment\n" + guardDefinition + "}\n", "    // the guard comment\n    ", "comment"),
                ("under attribute lines", "class S\n{\n\n    // the guard comment\n    [Obsolete]\n    [MethodImpl(MethodImplOptions.NoInlining)] // why\n" + guardDefinition + "}\n", "    // the guard comment\n    ", "comment"),
                ("with an attribute on the signature line (stated: code above)", "class S\n{\n\n    // the guard comment\n    [Obsolete] private static string? SwallowGuard(string s) => s;\n}\n", null, "code above"),
                ("under a comment split by a blank line (stated: the part below it)", "class S\n{\n\n    // part one\n\n    // part two\n" + guardDefinition + "}\n", "    // part two\n    ", "comment"),
                ("under a block comment holding a blank line (stated: code above)", "class S\n{\n\n    /* part one\n\n       part two */\n" + guardDefinition + "}\n", null, "code above"),
                ("under a [...] line inside a block comment (P6-59: not an attribute)", "class S\n{\n\n    /* the guard comment\n    [Obsolete]\n    */ private static string? SwallowGuard(string s) => s;\n}\n", "    /* the guard comment\n    [Obsolete]\n    */ ", "comment"),
                ("under a collection-expression line (P6-59: not an attribute)", "class S\n{\n    private int[] xs =\n\n    // the guard comment\n    [1, 2]\n" + guardDefinition + "}\n", null, "code above"),
                ("under a multi-line attribute (P6-59)", "class S\n{\n\n    // the guard comment\n    [MethodImpl(\n        MethodImplOptions.NoInlining)]\n" + guardDefinition + "}\n", "    // the guard comment\n    ", "comment"),
                ("directly under a blank line (P6-59)", "class S\n{\n    private int x;\n\n" + guardDefinition + "}\n", null, "no comment"),
            })
            {
                var got = CommentAbove(sample, guardSignature, out var gotWhy);
                C(got == want && gotWhy == wantWhy, "p6-55 anchored wording self-test: the signature " + what + " gives " + (want is null ? "no comment (" + wantWhy + ")" : "the comment above the code definition"),
                    (got is null ? "null" : "'" + got.Replace("\n", "\\n", StringComparison.Ordinal) + "'") + " (" + gotWhy + ")");
            }
            C(!selfSource.Contains("\n\n\n", StringComparison.Ordinal),
                "p6-53 wording: PdfType3GlyphListTests.cs has no doubled blank line", "at " + I(selfSource.IndexOf("\n\n\n", StringComparison.Ordinal)));
            // P6-39 self-tests: the builder call replaced by one with other options while the original stays in a // or a
            // /* */ comment. On the raw text the original line is still there once (the P6-37 hole); the stripped code
            // check names it as missing.
            const string otherCall = "CadBriefDocument.FromPdf(sourceFile, bytes, pdf!, page!.Value, null, briefOptions)";
            foreach (var (label, replacement) in new[] { ("a // comment", otherCall + "; // " + builderCall), ("a /* */ comment", otherCall + " /* " + builderCall + " */") })
            {
                var commented = cadCommand.Replace(builderCall, replacement, StringComparison.Ordinal);
                C(cadCommand.Length > 0 && Occurrences(commented, builderCall) == 1 && CodeProblems(commented, cadCommandPins, cadCommandAnchors) is { } missed
                    && missed.Contains("'" + builderCall + "' x0", StringComparison.Ordinal),
                    "p6-39 cli pins self-test: a commented-out copy of the builder call in " + label + " is caught (the raw text still holds it once)",
                    "raw x" + I(Occurrences(commented, builderCall)) + " " + (CodeProblems(commented, cadCommandPins, cadCommandAnchors) ?? "not caught"));
            }
            // P6-41 hash self-tests: the pre-P6-41 shared hash (before the try, so cad-brief would hash twice) is caught by
            // count; the one hash moved out of the cad-draft branch to before the try is caught by place.
            var hashedTwice = cadCommand.Replace("        object document;\n", "        var shared = SHA256.HashData(bytes);\n        object document;\n", StringComparison.Ordinal);
            C(cadCommand.Length > 0 && hashedTwice != cadCommand && CodeProblems(hashedTwice, cadCommandPins, cadCommandAnchors) is { } twice && twice.Contains("'SHA256.HashData(bytes)' x2", StringComparison.Ordinal),
                "p6-41 hash self-test: a second input hash in CadCommand.cs is caught by count", hashedTwice == cadCommand ? "anchor 'object document;' not found" : CodeProblems(hashedTwice, cadCommandPins, cadCommandAnchors) ?? "not caught");
            var hashMoved = cadCommand.Replace(inputHash, "", StringComparison.Ordinal).Replace("        object document;\n", "        " + inputHash + "\n        object document;\n", StringComparison.Ordinal);
            C(cadCommand.Length > 0 && Occurrences(cadCommand, inputHash) == 1 && CodeProblems(hashMoved, cadCommandPins, cadCommandAnchors) is { } moved && moved.Contains("not inside the cad-draft branch", StringComparison.Ordinal),
                "p6-41 hash self-test: the input hash moved before the try (shared by cad-brief) is caught by place", CodeProblems(hashMoved, cadCommandPins, cadCommandAnchors) ?? "not caught");
            // P6-41 builder: one input hash and one BriefExtract call per factory in adapters/cad/Bim.Cad/CadBriefDocument.cs
            // (stripped code), and the public surface is the two factories, Result and ToUtf8Bytes (Create and Scale gone).
            var builderPath = Path.Combine(root, "adapters", "cad", "Bim.Cad", "CadBriefDocument.cs");
            var builderSource = File.Exists(builderPath) ? CodePins.StripCommentsAndLiterals(File.ReadAllText(builderPath).Replace("\r\n", "\n", StringComparison.Ordinal)) : "";
            var builderPins = new (string Line, int Count)[] { ("SHA256.HashData(", 1), ("SHA256.HashData(bytes)", 1), ("BriefExtract.FromDxf(drawing, options)", 1), ("BriefExtract.FromPdf(pdf, page, calibration, options)", 1), ("BriefExtract.", 2) };
            var builderAnchors = new[] { "public sealed class CadBriefDocument", "public static CadBriefDocument FromDxf(", "public static CadBriefDocument FromPdf(", "static CadBriefDocument Build(", "public byte[] ToUtf8Bytes()" };
            var builderProblems = builderSource.Length == 0 ? "missing " + builderPath
                : string.Join("; ", new[] { SwallowGuard(builderSource, builderAnchors), PinProblems(builderSource, builderPins) }.Where(p => p is not null)) is { Length: > 0 } bp ? bp : null;
            C(builderProblems is null, "p6-41 builder: CadBriefDocument.cs hashes the input once and calls BriefExtract once per factory", builderProblems ?? "pins hold");
            var surface = string.Join(",", typeof(CadBriefDocument).GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                .Select(m => m.MemberType + ":" + m.Name).Order(StringComparer.Ordinal));
            C(surface == "Method:FromDxf,Method:FromPdf,Method:ToUtf8Bytes,Method:get_Result,Property:Result",
                "p6-41 builder surface: FromDxf, FromPdf, Result and ToUtf8Bytes only (no Create, no Scale)", surface);
            // P6-43 wording check: the P6-39 sentence group of the cad-brief P6-35 paragraph in docs/contracts/module-contracts.md
            // names the P6-41 factories (no Create), says the builder owns the one input hash, gives the recorded reason the
            // PDF reader options stay source-pinned and names the DXF reader-option pin.
            var contractPath = Path.Combine(root, "docs", "contracts", "module-contracts.md");
            var contractLine = File.Exists(contractPath) ? File.ReadAllLines(contractPath).SingleOrDefault(l => l.StartsWith("P6-35 form matrix (cad-brief, PDF;", StringComparison.Ordinal)) ?? "" : "";
            var p639At = contractLine.IndexOf(" P6-39: ", StringComparison.Ordinal);
            var p639 = p639At >= 0 ? contractLine[p639At..] : "";
            var wording = new[]
            {
                "`CadBriefDocument.FromDxf(sourceFile, bytes, drawing, options)`",
                "`FromPdf(sourceFile, bytes, pdf, page, calibration, options)`",
                "the builder owns the one input hash",
                "CadCommand reads the input once for cad-draft and cad-brief",
                "`DxfReadOptions(MaxInputBytes)`",
                "tests/cad/MinsertEzdxfTests.cs",
            };
            var wordingProblems = string.Join("; ", wording.Where(w => !p639.Contains(w, StringComparison.Ordinal)).Select(w => "missing '" + w + "'")
                .Concat(p639.Contains("Create(", StringComparison.Ordinal) ? new[] { "still names Create(" } : Array.Empty<string>()));
            C(p639.Length > 0 && wordingProblems.Length == 0,
                "p6-43 contract wording: the cad-brief P6-39 sentences name FromDxf/FromPdf, the builder's one input hash, the PDF reader-option pin reason and the DXF reader-option pin",
                p639.Length == 0 ? "no ' P6-39: ' group in the P6-35 paragraph of " + contractPath : wordingProblems);
            // P6-41 factories against the hand-built document (members and serializer as CadCommand wrote them before
            // P6-39) for every scale branch: DXF insunits / metres-per-unit (tests/cad/fixtures/minsert-ocs.dxf has
            // $INSUNITS), PDF none / metres-per-unit / known-dimension. This mirror is kept on purpose: it is the
            // independent statement of the document layout the builder must not change.
            var json = new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            byte[] Mirror(string file, byte[] input, string format, int? page, string scale, BriefDraft result) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = "cad-brief",
                version = 1,
                persisted = false,
                source = new { file, format, bytes = input.LongLength, sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)), page, scale, metres_per_unit = result.MetresPerUnit },
                brief = System.Text.Json.Nodes.JsonNode.Parse(Bim.Core.BriefSerializer.Serialize(result.Brief)),
                rooms = result.Rooms,
                uncertainties = result.Uncertainties,
                issues = result.Issues,
            }, json);
            // P6-43: one Hash16 for the whole block (it replaces the P6-41 H16 copy and the one inside the CLI branch below).
            static string Hash16(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
            var dxfBytes = File.ReadAllBytes(Path.Combine(fixtures, "minsert-ocs.dxf"));
            var dxf = DxfReader.Parse(dxfBytes, "minsert-ocs.dxf", new DxfReadOptions(cliMaxInputBytes));
            foreach (var (label, metres, scale) in new[] { ("dxf insunits", (double?)null, "insunits"), ("dxf metres-per-unit", 0.001, "metres-per-unit") })
            {
                var briefOptions = new BriefExtractOptions { MetresPerUnit = metres };
                var built = CadBriefDocument.FromDxf("minsert-ocs.dxf", dxfBytes, dxf, briefOptions).ToUtf8Bytes();
                var mirror = Mirror("minsert-ocs.dxf", dxfBytes, "dxf", null, scale, BriefExtract.FromDxf(dxf, briefOptions));
                C(dxf.MetresPerUnit is not null && built.AsSpan().SequenceEqual(mirror), "p6-41 factory " + label + ": FromDxf bytes equal the hand-built document (fixture dependency: minsert-ocs.dxf sets $INSUNITS, so its MetresPerUnit is not null and the no-option case takes the insunits branch)",
                    "insunits " + (dxf.MetresPerUnit?.ToString("R", CultureInfo.InvariantCulture) ?? "none") + " builder " + I(built.Length) + " " + Hash16(built) + " mirror " + I(mirror.Length) + " " + Hash16(mirror));
            }
            var pdfBytes = PdfToUnicodeTests.PageBytes("BT /F2 10 Tf 1 0 0 1 50 50 Tm (Sala 12 m2) Tj ET", Type1("[1 /g1]"), null);
            var pdfDoc = PdfReader.Parse(pdfBytes, "plain.pdf", new PdfReadOptions(MaxBytes: cliMaxInputBytes));
            foreach (var (label, metres, calibration, scale) in new[] { ("pdf none", (double?)null, (PdfCalibration?)null, "none"), ("pdf metres-per-unit", 0.01, null, "metres-per-unit"), ("pdf known-dimension", null, new PdfCalibration(10, PdfMatrix.Scaling(10)), "known-dimension") })
            {
                var briefOptions = new BriefExtractOptions { MetresPerUnit = metres };
                var built = CadBriefDocument.FromPdf("plain.pdf", pdfBytes, pdfDoc, 1, calibration, briefOptions).ToUtf8Bytes();
                var mirror = Mirror("plain.pdf", pdfBytes, "pdf", 1, scale, BriefExtract.FromPdf(pdfDoc, 1, calibration, briefOptions));
                C(built.AsSpan().SequenceEqual(mirror), "p6-41 factory " + label + ": FromPdf bytes equal the hand-built document",
                    "builder " + I(built.Length) + " " + Hash16(built) + " mirror " + I(mirror.Length) + " " + Hash16(mirror) + " " + Encoding.UTF8.GetString(built));
            }
            C(File.Exists(cliDll), "p6-35 cli precondition: the Release Bim.Cli.dll exists (scripts/verify.ps1 builds interfaces/cli first)", cliDll);
            if (File.Exists(cliDll))
            {
                var cliPage = "q " + string.Concat(Enumerable.Repeat(e60 + " 0 0 " + e60 + " 0 0 cm ", 5)) + "/X1 Do Q BT /F2 10 Tf 1 0 0 1 50 50 Tm (Sala 12 m2) Tj ET";
                var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
                var scratch = Path.Combine(Path.GetTempPath(), "bim-cad-p635-" + I(Environment.ProcessId));
                if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
                Directory.CreateDirectory(scratch);
                try
                {
                    foreach (var (label, matrix, warning, pinned) in new[]
                    {
                        ("overflow", e60 + " 0 0 " + e60 + " 0 0", "Do /X1 gives a non-finite form matrix; the current matrix is kept", "DE7127CCF6EAD8BF"),
                        ("malformed", "1 0 0 1 100", malformedMessage, "016B3C6BA998C1D6"),
                    })
                    {
                        var file = label + ".pdf";
                        var bytes = PdfToUnicodeTests.PageBytes(cliPage, Type1("[1 /g1]"), null, "/XObject << /X1 8 0 R >>", null, RawForm(" /Matrix [" + matrix + "]", "q Q"));
                        // The document CadCommand writes for a PDF cad-brief without a scale option, built through the same
                        // entry point (P6-39): the reader options pinned above, then CadBriefDocument.FromPdf (P6-41) and ToUtf8Bytes.
                        var pdf = PdfReader.Parse(bytes, file, new PdfReadOptions(MaxBytes: cliMaxInputBytes));
                        var expected = CadBriefDocument.FromPdf(file, bytes, pdf, 1, null, new BriefExtractOptions { MetresPerUnit = null, IncludeBlockRegions = false }).ToUtf8Bytes();
                        // The P6-37 hand-mirrored document (members and serializer as CadCommand wrote them before P6-39) gives
                        // the same bytes: the builder moved the document without changing it.
                        // P6-43: the same hand-built Mirror as the factory checks above (one statement of the layout).
                        var result = BriefExtract.FromPdf(pdf, 1, null, new BriefExtractOptions { MetresPerUnit = null, IncludeBlockRegions = false });
                        var legacy = Mirror(file, bytes, "pdf", 1, "none", result);
                        C(expected.AsSpan().SequenceEqual(legacy),
                            "p6-39 cli " + label + ": CadBriefDocument bytes equal the P6-37 mirrored document bytes",
                            "builder " + I(expected.Length) + " bytes " + Hash16(expected) + " mirrored " + I(legacy.Length) + " bytes " + Hash16(legacy));
                        C(Warnings(pdf) == "operand-error@1:4:6" && Messages(pdf) == warning,
                            "cli " + label + " premise: in process the page has only the Do operand-error (P6-35)", Warnings(pdf) + " " + Messages(pdf));

                        var input = Path.Combine(scratch, file);
                        var output = Path.Combine(scratch, label + ".json");
                        File.WriteAllBytes(input, bytes);
                        var start = new System.Diagnostics.ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
                        foreach (var argument in new[] { cliDll, "cad-brief", input, output }) start.ArgumentList.Add(argument);
                        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
                        var stdout = process.StandardOutput.ReadToEndAsync();
                        var stderr = process.StandardError.ReadToEndAsync();
                        if (!process.WaitForExit(180000))
                        {
                            try { process.Kill(true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                            C(false, "cli " + label + " finishes (P6-35)", "timed out");
                            continue;
                        }
                        process.WaitForExit();
                        var written = File.Exists(output) ? File.ReadAllBytes(output) : Array.Empty<byte>();
                        var firstDiff = Enumerable.Range(0, Math.Min(written.Length, expected.Length)).FirstOrDefault(i => written[i] != expected[i], -1);
                        var text = Encoding.UTF8.GetString(written);
                        C(process.ExitCode == 0 && written.Length > 0 && written.AsSpan().SequenceEqual(expected),
                            "cli " + label + ": cad-brief writes exactly the in-process document bytes (P6-35)",
                            "exit " + I(process.ExitCode) + " stdout " + stdout.Result.Trim() + " stderr " + stderr.Result.Trim() + " cli " + I(written.Length) + " bytes "
                            + (written.Length > 0 ? Hash16(written) : "-") + " in-process " + I(expected.Length) + " bytes " + Hash16(expected) + " first difference " + I(firstDiff)
                            + (firstDiff < 0 ? "" : " cli '" + Encoding.UTF8.GetString(written, firstDiff, Math.Min(120, written.Length - firstDiff)) + "'"));
                        C(written.Length > 0 && Hash16(written) == pinned && !text.Contains("operand-error", StringComparison.Ordinal) && !text.Contains("/Matrix", StringComparison.Ordinal)
                            && !text.Contains("form matrix", StringComparison.Ordinal) && text.Contains("Sala", StringComparison.Ordinal),
                            "cli " + label + ": document digest pinned " + pinned + "; the Do operand-error is not in it (documented limit, P6-35)",
                            "digest " + (written.Length > 0 ? Hash16(written) : "-") + " bytes " + I(written.Length) + " " + text);
                    }
                }
                finally
                {
                    try { Directory.Delete(scratch, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception error) { C(false, "p6-35 unexpected", error.GetType().Name + ": " + error.Message); }

        // ---------- P6-37: non-finite /Matrix entries, warnings per invocation ----------
        try
        {
            string Messages(PdfDocument document) => string.Join(" / ", document.Warnings.Select(w => w.Message));
            static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);
            string Origins(PdfDocument document) => string.Join("|", document.Pages.SelectMany(p => p.Elements).OfType<PdfText>()
                .Select(t => R(t.Origin.X) + "," + R(t.Origin.Y) + " h" + R(t.Height)));
            string Measure(PdfDocument document) => "[" + Texts(document) + "] " + Warnings(document) + " {" + Messages(document) + "} " + Origins(document);
            var e60 = "1" + new string('0', 60);
            var e63 = "1" + new string('0', 63);
            var e64 = "1" + new string('0', 64);
            var ctm1e300 = string.Concat(Enumerable.Repeat(e60 + " 0 0 " + e60 + " 0 0 cm ", 5));
            static (int Number, byte[] Body) RawForm(string matrixEntry, string content) =>
                (8, PdfTests.StreamBody("/Type /XObject /Subtype /Form /BBox [0 0 600 400]" + matrixEntry, L(content)));
            const string formShow = "BT /F2 10 Tf 1 0 0 1 10 10 Tm (A) Tj ET";
            PdfDocument Form(string page, string matrixEntry, string xobjects = "/XObject << /X1 8 0 R >>", string extra = "100") =>
                PdfToUnicodeTests.Page(page, Type1("[1 /g1]"), null, xobjects, RawForm(matrixEntry, formShow), (9, L(extra)));
            const string malformed = "Do /X1 expects a /Matrix of 6 numbers; the identity matrix is used";
            const string nonFinite = "Do /X1 gives a non-finite form matrix; the current matrix is kept";

            // (1) A /Matrix of six entries one of which would be NaN or Infinity as a double. Measured (P6-37): no PDF number
            // token is non-finite — the lexer reads only sign, digits and '.', at most 64 characters (PdfSyntax.ParseNumber;
            // PDF numbers have no exponent), so 1e400, 1E400, -1e400, 1e10, Infinity, NaN and a 65-character digit run are
            // not numbers but keywords, directly or through an indirect object. Documented one way: such an entry makes the
            // /Matrix malformed (the P6-35 message, identity used, form under the page CTM 2: (20, 20) h20), never the
            // non-finite message. The non-finite message stays for six finite numbers whose composition overflows
            // (1e63 under CTM 1e300); the largest number token (64 digits, 1e63) composes silently. Page: cm 0, Do 1.
            foreach (var (entry, what) in new[]
            {
                ("1e400", "1e400"), ("-1e400", "-1e400"), ("1E400", "1E400"), ("1e10", "1e10 (exponent form)"), ("Infinity", "Infinity"), ("NaN", "NaN"),
                (e64, "a 65-character digit run"),
            })
            {
                var document = Form("2 0 0 2 0 0 cm /X1 Do", " /Matrix [1 0 0 1 " + entry + " 0]");
                C(Measure(document) == "[A] operand-error@1:4:1 {" + malformed + "} 20,20 h20",
                    "a form /Matrix entry " + what + " is not a number: malformed /Matrix message, identity used (P6-37)", Measure(document));
            }
            var indirect = Form("2 0 0 2 0 0 cm /X1 Do", " /Matrix [1 0 0 1 9 0 R 0]", extra: "1e400");
            C(Measure(indirect) == "[A] operand-error@1:4:1 {" + malformed + "} 20,20 h20",
                "a form /Matrix entry referencing an object 1e400 is not a number: malformed /Matrix message (P6-37)", Measure(indirect));
            var largest = Form("2 0 0 2 0 0 cm /X1 Do", " /Matrix [1 0 0 1 " + e63 + " 0]");
            C(Measure(largest) == "[A]  {} 2E+63,20 h20",
                "control: a 64-character number entry (1e63) is a number and composes silently (P6-37)", Measure(largest));
            var overflow = Form(ctm1e300 + "/X1 Do", " /Matrix [" + e63 + " 0 0 " + e63 + " 0 0]");
            C(Texts(overflow) == "A" && Warnings(overflow) == "operand-error@1:4:5" && Messages(overflow) == nonFinite,
                "six finite numbers (1e63) composing to non-finite under CTM 1e300 give the non-finite message, not the malformed one (P6-37)", Measure(overflow));

            // (2) Warnings per invocation. Measured (P6-37): a malformed (or overflowing) /Matrix is reported at every Do that
            // invokes the form, not once per form: there is no warn-once memory, as each bad cm warns. Documented and pinned
            // that way (each Do position is a separate finding a caller may need). Text bytes unchanged.
            var twice = Form("2 0 0 2 0 0 cm /X1 Do /X1 Do", " /Matrix [1 0 0 1 100]");
            C(Measure(twice) == "[A|A] operand-error@1:4:1;operand-error@1:4:2 {" + malformed + " / " + malformed + "} 20,20 h20|20,20 h20",
                "a form with a malformed /Matrix invoked twice gives 2 warnings, one per Do (P6-37)", Measure(twice));
            var names = Form("2 0 0 2 0 0 cm /X1 Do /X2 Do", " /Matrix [1 0 0 1 100]", "/XObject << /X1 8 0 R /X2 8 0 R >>");
            C(Measure(names) == "[A|A] operand-error@1:4:1;operand-error@1:4:2 {" + malformed + " / " + malformed.Replace("/X1", "/X2", StringComparison.Ordinal) + "} 20,20 h20|20,20 h20",
                "one form under two resource names gives 2 warnings, each naming its resource (P6-37)", Measure(names));
            var saved = Form("2 0 0 2 0 0 cm q /X1 Do Q q /X1 Do Q", " /Matrix [1 0 0 1 100]");
            C(Measure(saved) == "[A|A] operand-error@1:4:2;operand-error@1:4:5 {" + malformed + " / " + malformed + "} 20,20 h20|20,20 h20",
                "a form with a malformed /Matrix invoked twice inside q ... Q gives 2 warnings (P6-37)", Measure(saved));
            var overflowTwice = Form(ctm1e300 + "/X1 Do /X1 Do", " /Matrix [" + e60 + " 0 0 " + e60 + " 0 0]");
            C(Texts(overflowTwice) == "A|A" && Warnings(overflowTwice) == "operand-error@1:4:5;operand-error@1:4:6" && Messages(overflowTwice) == nonFinite + " / " + nonFinite,
                "a form whose /Matrix overflows invoked twice gives 2 non-finite warnings (P6-37)", Measure(overflowTwice));
        }
        catch (Exception error) { C(false, "p6-37 unexpected", error.GetType().Name + ": " + error.Message); }
    }

    // P6-33: member anchors of the stripped sources, in source order, for the shared swallow guard. They are spread over
    // each file, so a literal or comment that swallows a balanced stretch mid-file removes at least one of them.
    private static readonly string[] ContentAnchors =
    {
        "internal sealed class PdfContentInterpreter", "public void Run(", "private static bool SkipInlineImage(", "private void Execute(string op",
        "private bool HasCurrent(", "private void Show(byte[] bytes", "FontDecoder(string name", "private bool Show(string text",
        "private static double Hypot(", "internal static string DecodeText(", "private void InvokeXObject(string name",
    };

    private static readonly string[] EncodingAnchors =
    {
        "internal sealed class PdfSimpleEncoding", "public bool TryMap(byte code", "UnsupportedGlyphListName(byte code)", "private static PdfSimpleEncoding Parse(",
        "private static string?[] Ascii()", "internal static class PdfGlyphNames", "public static bool IsGlyphListName(string name)", "private static int? Hex(",
        "Lazy<Dictionary<string, string>> Latin = new(",
    };

    private static int Occurrences(string text, string token) => (text.Length - text.Replace(token, "", StringComparison.Ordinal).Length) / token.Length;

    // P6-33: the one swallow guard for a source read through CodePins.StripCommentsAndLiterals (it replaces the P6-25 and
    // P6-27 copies): balanced braces, a final '}', and every anchor exactly once and in the given order. Null when the
    // stripped text passes, otherwise what failed.
    private static string? SwallowGuard(string stripped, IReadOnlyList<string> anchors)
    {
        var problems = new List<string>();
        var opens = stripped.Count(c => c == '{');
        var closes = stripped.Count(c => c == '}');
        if (opens != closes) problems.Add("braces " + I(opens) + "/" + I(closes));
        var end = stripped.TrimEnd();
        if (!end.EndsWith('}')) problems.Add("ends " + (end.Length > 0 ? end[^1].ToString() : "empty"));
        var previous = -1;
        foreach (var anchor in anchors)
        {
            var count = Occurrences(stripped, anchor);
            var index = stripped.IndexOf(anchor, StringComparison.Ordinal);
            if (count != 1) problems.Add(anchor + " x" + I(count));
            else if (index < previous) problems.Add(anchor + " out of order");
            else previous = index;
        }
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    // The [start, end) span of the member body after the signature at index: an expression body up to its ';', or a
    // block body up to its matching '}'; (-1, -1) when the signature is absent.
    private static (int Start, int End) Body(string text, int index, int length)
    {
        if (index < 0) return (-1, -1);
        var start = index + length;
        while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        if (start + 1 < text.Length && text[start] == '=' && text[start + 1] == '>')
        {
            var end = text.IndexOf(';', start);
            return end < 0 ? (-1, -1) : (start, end);
        }
        if (start >= text.Length || text[start] != '{') return (-1, -1);
        var depth = 0;
        for (var k = start; k < text.Length; k++)
        {
            if (text[k] == '{') depth++;
            else if (text[k] == '}' && --depth == 0) return (start, k);
        }
        return (-1, -1);
    }

    private static string Show(string literal) => "BT /F1 10 Tf 1 0 0 1 50 50 Tm (" + literal + ") Tj ET";
}
