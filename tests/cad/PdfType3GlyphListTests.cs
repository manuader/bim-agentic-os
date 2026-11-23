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
            // StripCommentsAndLiterals (comments and literals removed, P6-27) under the same swallow guard.
            var adapter = Path.Combine(fixtures, "..", "..", "..", "adapters", "cad", "Bim.Cad");
            static string Code(string path) => StripCommentsAndLiterals(File.ReadAllText(path));
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
            var stripped = StripCommentsAndLiterals(crafted);
            C(stripped == "var a = 1; \nvar b = \"\";\nvar c = \"\";\nvar d = ''; var e = \"\";\nvar f = \"\";   var g = ''; IsGlyphListName(\"\"); var @h = \"\";"
                + "\nvar p = ''; var q = \"\";   var r = \"\";\nvar v = \"\"; IsGlyphListName(7);",
                "the static check strips trailing // comments, block comments and string and char literals (P6-27)", Escape(stripped));

            // The P6-25 static check, re-run on the sources with comments and literals stripped.
            var adapter = Path.Combine(fixtures, "..", "..", "..", "adapters", "cad", "Bim.Cad");
            var content = StripCommentsAndLiterals(File.ReadAllText(Path.Combine(adapter, "PdfContent.cs")));
            var encoding = StripCommentsAndLiterals(File.ReadAllText(Path.Combine(adapter, "PdfEncoding.cs")));
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
            var overStripped = StripCommentsAndLiterals(sample);
            C(overStripped == "class S\n{\n    void First() { var s = \"\"  }\n    void Last() { }\n}\n",
                "self-test premise: a block comment opened inside an interpolation hole swallows a balanced mid-file stretch (P6-33)", Escape(overStripped));
            C(OldGuard(overStripped, "void First()", "void Last()") && SwallowGuard(overStripped, sampleAnchors) == "void Middle() x0; void Spacer() x0",
                "the shared swallow guard catches a balanced mid-file over-strip that the old first/last guard let through (P6-33)",
                OldGuard(overStripped, "void First()", "void Last()") + " " + (SwallowGuard(overStripped, sampleAnchors) ?? "passed"));
            var intact = sample.Replace("$\"{F(\"/*\")}\"", "1", StringComparison.Ordinal);
            C(SwallowGuard(StripCommentsAndLiterals(intact), sampleAnchors) is null && SwallowGuard(intact, Enumerable.Reverse(sampleAnchors).ToArray()) == "void Spacer() out of order; void Middle() out of order; void First() out of order",
                "the shared swallow guard passes an intact sample and reports anchors out of order (P6-33)",
                (SwallowGuard(StripCommentsAndLiterals(intact), sampleAnchors) ?? "passed") + " / " + (SwallowGuard(intact, Enumerable.Reverse(sampleAnchors).ToArray()) ?? "passed"));
            // The same on the real stripped PdfContent.cs: cutting the balanced HasCurrent member out passes the old guard
            // but not the shared one.
            var adapter = Path.Combine(fixtures, "..", "..", "..", "adapters", "cad", "Bim.Cad");
            var content = StripCommentsAndLiterals(File.ReadAllText(Path.Combine(adapter, "PdfContent.cs")));
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
            C(File.Exists(cliDll), "p6-35 cli precondition: the Release Bim.Cli.dll exists (scripts/verify.ps1 builds interfaces/cli first)", cliDll);
            if (File.Exists(cliDll))
            {
                var cliPage = "q " + string.Concat(Enumerable.Repeat(e60 + " 0 0 " + e60 + " 0 0 cm ", 5)) + "/X1 Do Q BT /F2 10 Tf 1 0 0 1 50 50 Tm (Sala 12 m2) Tj ET";
                var json = new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
                var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
                static string Hash16(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16];
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
                        // The document CadCommand writes for a PDF cad-brief without a scale option (interfaces/cli/CadCommand.cs).
                        var pdf = PdfReader.Parse(bytes, file, new PdfReadOptions(MaxBytes: 32 * 1024 * 1024));
                        var result = BriefExtract.FromPdf(pdf, 1, null, new BriefExtractOptions { MetresPerUnit = null, IncludeBlockRegions = false });
                        var document = new
                        {
                            kind = "cad-brief",
                            version = 1,
                            persisted = false,
                            source = new { file, format = "pdf", bytes = bytes.LongLength, sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), page = (int?)1, scale = "none", metres_per_unit = result.MetresPerUnit },
                            brief = System.Text.Json.Nodes.JsonNode.Parse(Bim.Core.BriefSerializer.Serialize(result.Brief)),
                            rooms = result.Rooms,
                            uncertainties = result.Uncertainties,
                            issues = result.Issues,
                        };
                        var expected = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(document, json);
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
        // ---------- P6-37: non-finite /Matrix entries, warnings per invocation, CLI option pins ----------
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
            static (int Number, byte[] Body) RawForm(string matrixEntry, string content) =>
                (8, PdfTests.StreamBody("/Type /XObject /Subtype /Form /BBox [0 0 600 400]" + matrixEntry, L(content)));
            const string formShow = "BT /F2 10 Tf 1 0 0 1 10 10 Tm (A) Tj ET";
            PdfDocument Form(string page, string matrixEntry, string xobjects = "/XObject << /X1 8 0 R >>", string extra = "100") =>
                PdfToUnicodeTests.Page(page, Type1("[1 /g1]"), null, xobjects, RawForm(matrixEntry, formShow), (9, L(extra)));
            string Probe(Func<PdfDocument> build)
            {
                try { return Measure(build()); }
                catch (Exception error) { return error.GetType().Name + ": " + error.Message; }
            }

            var probes = new List<string>();
            foreach (var entry in new[] { "1e400", "-1e400", "1E400", "1e10", "Infinity", "NaN", e64, e63 })
                probes.Add("entry " + (entry.Length > 20 ? I(entry.Length) + "-char" : entry) + " => " + Probe(() => Form("2 0 0 2 0 0 cm /X1 Do", " /Matrix [1 0 0 1 " + entry + " 0]")));
            probes.Add("indirect 1e400 => " + Probe(() => Form("2 0 0 2 0 0 cm /X1 Do", " /Matrix [1 0 0 1 9 0 R 0]", extra: "1e400")));
            probes.Add("e63 under 1e300 => " + Probe(() => Form(string.Concat(Enumerable.Repeat(e60 + " 0 0 " + e60 + " 0 0 cm ", 5)) + "/X1 Do", " /Matrix [" + e63 + " 0 0 " + e63 + " 0 0]")));
            probes.Add("twice => " + Probe(() => Form("2 0 0 2 0 0 cm /X1 Do /X1 Do", " /Matrix [1 0 0 1 100]")));
            probes.Add("two names => " + Probe(() => Form("2 0 0 2 0 0 cm /X1 Do /X2 Do", " /Matrix [1 0 0 1 100]", "/XObject << /X1 8 0 R /X2 8 0 R >>")));
            probes.Add("twice in q => " + Probe(() => Form("2 0 0 2 0 0 cm q /X1 Do Q q /X1 Do Q", " /Matrix [1 0 0 1 100]")));
            probes.Add("overflow twice => " + Probe(() => Form(string.Concat(Enumerable.Repeat(e60 + " 0 0 " + e60 + " 0 0 cm ", 5)) + "/X1 Do /X1 Do", " /Matrix [" + e60 + " 0 0 " + e60 + " 0 0]")));
            foreach (var probe in probes) C(false, "p6-37 probe", probe);
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

    // P6-33: the one swallow guard for a source read through StripCommentsAndLiterals (it replaces the P6-25 and P6-27
    // copies): balanced braces, a final '}', and every anchor exactly once and in the given order. Null when the stripped
    // text passes, otherwise what failed.
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

    // P6-27: C# source with // and /* */ comments removed and every string, verbatim, interpolated, raw and char literal
    // replaced by an empty one ("" or ''), so a static check matches code only.
    private static string StripCommentsAndLiterals(string source)
    {
        var output = new StringBuilder(source.Length);
        var n = source.Length;
        var i = 0;
        while (i < n)
        {
            var c = source[i];
            if (c == '/' && i + 1 < n && source[i + 1] == '/')
            {
                while (i < n && source[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < n && source[i + 1] == '*')
            {
                var close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? n : close + 2;
                output.Append(' ');
                continue;
            }
            if (c == '\'')
            {
                var k = i + 1;
                while (k < n && source[k] != '\'' && source[k] != '\n')
                {
                    if (source[k] == '\\') k++;
                    k++;
                }
                output.Append("''");
                i = Math.Min(n, k + 1);
                continue;
            }
            if (c is '$' or '@' or '"')
            {
                var k = i;
                var verbatim = false;
                while (k < n && source[k] is '$' or '@')
                {
                    if (source[k] == '@') verbatim = true;
                    k++;
                }
                if (k < n && source[k] == '"')
                {
                    var quotes = 0;
                    while (k + quotes < n && source[k + quotes] == '"') quotes++;
                    // A verbatim string is never raw: @"""" is one escaped quote, not the start of a raw literal.
                    if (quotes >= 3 && !verbatim)
                    {
                        var close = source.IndexOf(new string('"', quotes), k + quotes, StringComparison.Ordinal);
                        i = close < 0 ? n : close + quotes;
                    }
                    else if (quotes == 2 && !verbatim) i = k + 2;
                    else if (verbatim)
                    {
                        k++;
                        while (k < n)
                        {
                            if (source[k] == '"')
                            {
                                if (k + 1 < n && source[k + 1] == '"') { k += 2; continue; }
                                break;
                            }
                            k++;
                        }
                        i = k + 1;
                    }
                    else
                    {
                        k++;
                        while (k < n && source[k] != '"' && source[k] != '\n')
                        {
                            if (source[k] == '\\') k++;
                            k++;
                        }
                        i = k + 1;
                    }
                    output.Append("\"\"");
                    continue;
                }
                output.Append(source, i, Math.Max(k - i, 1));
                i = Math.Max(k, i + 1);
                continue;
            }
            output.Append(c);
            i++;
        }
        return output.ToString();
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
