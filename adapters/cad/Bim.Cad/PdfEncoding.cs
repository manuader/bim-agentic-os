using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
namespace Bim.Cad;

// The /Encoding dictionary of a simple font (PDF 32000-1 9.6.6.1): /BaseEncoding /StandardEncoding, /WinAnsiEncoding or
// /MacRomanEncoding (Annex D) and /Differences [code /name /name ... code /name ...] glyph names. A glyph name maps to
// Unicode through an embedded table of the Latin set (the names of the three base encodings and ISO Latin-1), uniXXXX
// and sequences of it (four upper-case hex digits each, no surrogates), uXXXX to uXXXXXX (up to U+10FFFF, no
// surrogates) and a name with a .suffix as the name before it; afii and other names are unknown. A code without a
// known name keeps the reader's byte decoding (Latin-1): a code outside /Differences with no or an unknown base
// encoding or undefined in it, and a /Differences code whose name is unknown (reported by Decode). /Differences codes
// outside 0-255 and names after a non-integer, negative or out-of-range number are ignored.
// Deliberate choices: /MacRomanEncoding follows Mac OS Roman, so beyond the PDF Annex D Latin table it also maps the Mac
// extras (notequal, infinity, lessequal, greaterequal, partialdiff, summation, product, pi, integral, Omega, radical,
// approxequal, Delta, lozenge; 0xDB is currency as in Annex D, 0xF0 apple stays Latin-1). Without /BaseEncoding the
// codes outside /Differences fall back to Latin-1 (today's decoding), not to StandardEncoding or the font program's
// built-in encoding. .notdef and underscore ligature names such as f_f are reported unknown (encoding-glyph-unknown).
// Each parsed encoding is cached per document by its dictionary (an indirect encoding object is one dictionary for
// every font and page that uses it) and every /Differences item is charged once to the document-wide
// MaxParsedElements budget (element-limit refusal).
internal sealed class PdfSimpleEncoding
{
    private readonly string?[] map = new string?[256];
    private readonly string?[] unknown = new string?[256];

    private PdfSimpleEncoding() { }

    public bool TryMap(byte code, [MaybeNullWhen(false)] out string text)
    {
        text = map[code];
        return text is not null;
    }

    // The unknown /Differences glyph name of the code, or null.
    public string? UnknownName(byte code) => unknown[code];

    private static readonly ConditionalWeakTable<PdfFile, Dictionary<object, PdfSimpleEncoding>> Cache = new();

    // The parsed encoding of the dictionary, cached for the document.
    public static PdfSimpleEncoding Get(PdfFile file, Dictionary<string, object> dictionary)
    {
        var parsed = Cache.GetValue(file, _ => new Dictionary<object, PdfSimpleEncoding>(ReferenceEqualityComparer.Instance));
        if (!parsed.TryGetValue(dictionary, out var result))
        {
            result = Parse(file, dictionary);
            parsed[dictionary] = result;
        }
        return result;
    }

    private static PdfSimpleEncoding Parse(PdfFile file, Dictionary<string, object> dictionary)
    {
        var result = new PdfSimpleEncoding();
        var baseName = file.Resolve(PdfFile.Get(dictionary, "BaseEncoding")) is PdfName name ? name.Value : "";
        if (BaseTable(baseName) is { } table)
            for (var code = 0; code < 256; code++) result.map[code] = table[code];
        if (file.Resolve(PdfFile.Get(dictionary, "Differences")) is List<object> differences)
        {
            var next = -1;
            foreach (var raw in differences)
            {
                file.Budget.Charge(-1);
                var item = file.Resolve(raw);
                if (item is PdfNumber number)
                {
                    next = number.IsInteger && number.Value >= 0 && number.Value <= 255 ? (int)number.Value : -1;
                    continue;
                }
                if (item is not PdfName glyph) continue;
                if (next is >= 0 and <= 255)
                {
                    if (PdfGlyphNames.TryUnicode(glyph.Value, out var text))
                    {
                        result.map[next] = text;
                        result.unknown[next] = null;
                    }
                    else
                    {
                        result.map[next] = null;
                        result.unknown[next] = glyph.Value;
                    }
                    next++;
                }
            }
        }
        return result;
    }

    private static string?[]? BaseTable(string name) => name switch
    {
        "WinAnsiEncoding" => WinAnsi.Value,
        "MacRomanEncoding" => MacRoman.Value,
        "StandardEncoding" => Standard.Value,
        _ => null,
    };

    private static readonly Lazy<string?[]> WinAnsi = new(() =>
    {
        var table = Ascii();
        for (var code = 0xA0; code <= 0xFF; code++) table[code] = ((char)code).ToString();
        Fill(table, 0x80, "Euro", null, "quotesinglbase", "florin", "quotedblbase", "ellipsis", "dagger", "daggerdbl", "circumflex", "perthousand", "Scaron", "guilsinglleft", "OE", null, "Zcaron", null,
            null, "quoteleft", "quoteright", "quotedblleft", "quotedblright", "bullet", "endash", "emdash", "tilde", "trademark", "scaron", "guilsinglright", "oe", null, "zcaron", "Ydieresis");
        return table;
    });

    private static readonly Lazy<string?[]> MacRoman = new(() =>
    {
        var table = Ascii();
        Fill(table, 0x80,
            "Adieresis", "Aring", "Ccedilla", "Eacute", "Ntilde", "Odieresis", "Udieresis", "aacute", "agrave", "acircumflex", "adieresis", "atilde", "aring", "ccedilla", "eacute", "egrave",
            "ecircumflex", "edieresis", "iacute", "igrave", "icircumflex", "idieresis", "ntilde", "oacute", "ograve", "ocircumflex", "odieresis", "otilde", "uacute", "ugrave", "ucircumflex", "udieresis",
            "dagger", "degree", "cent", "sterling", "section", "bullet", "paragraph", "germandbls", "registered", "copyright", "trademark", "acute", "dieresis", "notequal", "AE", "Oslash",
            "infinity", "plusminus", "lessequal", "greaterequal", "yen", "mu", "partialdiff", "summation", "product", "pi", "integral", "ordfeminine", "ordmasculine", "Omega", "ae", "oslash",
            "questiondown", "exclamdown", "logicalnot", "radical", "florin", "approxequal", "Delta", "guillemotleft", "guillemotright", "ellipsis", "space", "Agrave", "Atilde", "Otilde", "OE", "oe",
            "endash", "emdash", "quotedblleft", "quotedblright", "quoteleft", "quoteright", "divide", "lozenge", "ydieresis", "Ydieresis", "fraction", "currency", "guilsinglleft", "guilsinglright", "fi", "fl",
            "daggerdbl", "periodcentered", "quotesinglbase", "quotedblbase", "perthousand", "Acircumflex", "Ecircumflex", "Aacute", "Edieresis", "Egrave", "Iacute", "Icircumflex", "Idieresis", "Igrave", "Oacute", "Ocircumflex",
            null, "Ograve", "Uacute", "Ucircumflex", "Ugrave", "dotlessi", "circumflex", "tilde", "macron", "breve", "dotaccent", "ring", "cedilla", "hungarumlaut", "ogonek", "caron");
        return table;
    });

    private static readonly Lazy<string?[]> Standard = new(() =>
    {
        var table = Ascii();
        Fill(table, 0x27, "quoteright");
        Fill(table, 0x60, "quoteleft");
        Fill(table, 0xA1, "exclamdown", "cent", "sterling", "fraction", "yen", "florin", "section", "currency", "quotesingle", "quotedblleft", "guillemotleft", "guilsinglleft", "guilsinglright", "fi", "fl");
        Fill(table, 0xB1, "endash", "dagger", "daggerdbl", "periodcentered", null, "paragraph", "bullet", "quotesinglbase", "quotedblbase", "quotedblright", "guillemotright", "ellipsis", "perthousand", null, "questiondown");
        Fill(table, 0xC1, "grave", "acute", "circumflex", "tilde", "macron", "breve", "dotaccent", "dieresis", null, "ring", "cedilla", null, "hungarumlaut", "ogonek", "caron", "emdash");
        Fill(table, 0xE1, "AE", null, "ordfeminine", null, null, null, null, "Lslash", "Oslash", "OE", "ordmasculine");
        Fill(table, 0xF1, "ae", null, null, null, "dotlessi", null, null, "lslash", "oslash", "oe", "germandbls");
        return table;
    });

    private static string?[] Ascii()
    {
        var table = new string?[256];
        for (var code = 0x20; code <= 0x7E; code++) table[code] = ((char)code).ToString();
        return table;
    }

    private static void Fill(string?[] table, int first, params string?[] names)
    {
        for (var k = 0; k < names.Length; k++)
            table[first + k] = names[k] is { } glyph && PdfGlyphNames.TryUnicode(glyph, out var text) ? text : null;
    }
}

// Glyph name to Unicode for the Latin set (Adobe Glyph List names of the Standard, WinAnsi and MacRoman encodings and
// ISO Latin-1), uniXXXX sequences, uXXXX..uXXXXXX and a .suffix; afii and other names are not known.
internal static class PdfGlyphNames
{
    public const int MaxUniGroups = 16;

    public static bool TryUnicode(string name, [MaybeNullWhen(false)] out string text)
    {
        text = null;
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0) name = name[..dot];
        if (Latin.Value.TryGetValue(name, out var known))
        {
            text = known;
            return true;
        }
        if (name.Length >= 7 && name.StartsWith("uni", StringComparison.Ordinal) && (name.Length - 3) % 4 == 0 && (name.Length - 3) / 4 <= MaxUniGroups)
        {
            var builder = new StringBuilder();
            for (var at = 3; at < name.Length; at += 4)
            {
                if (Hex(name.AsSpan(at, 4)) is not { } unit || unit is >= 0xD800 and <= 0xDFFF) return false;
                builder.Append((char)unit);
            }
            text = builder.ToString();
            return true;
        }
        if (name.Length is >= 5 and <= 7 && name[0] == 'u' && Hex(name.AsSpan(1)) is { } point && point <= 0x10FFFF && point is not (>= 0xD800 and <= 0xDFFF))
        {
            text = char.ConvertFromUtf32(point);
            return true;
        }
        return false;
    }

    // Upper-case hexadecimal digits only (as the glyph list requires), else null.
    private static int? Hex(ReadOnlySpan<char> digits)
    {
        var value = 0;
        foreach (var c in digits)
        {
            int digit;
            if (c is >= '0' and <= '9') digit = c - '0';
            else if (c is >= 'A' and <= 'F') digit = c - 'A' + 10;
            else return null;
            value = value * 16 + digit;
        }
        return value;
    }

    private static readonly Lazy<Dictionary<string, string>> Latin = new(() =>
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string glyph, int point) => table[glyph] = char.ConvertFromUtf32(point);
        // ASCII.
        var ascii = new[]
        {
            "space", "exclam", "quotedbl", "numbersign", "dollar", "percent", "ampersand", "quotesingle", "parenleft", "parenright", "asterisk", "plus", "comma", "hyphen", "period", "slash",
            "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "colon", "semicolon", "less", "equal", "greater", "question", "at",
        };
        for (var k = 0; k < ascii.Length; k++) Add(ascii[k], 0x20 + k);
        for (var c = 'A'; c <= 'Z'; c++) Add(c.ToString(), c);
        for (var c = 'a'; c <= 'z'; c++) Add(c.ToString(), c);
        Add("bracketleft", 0x5B); Add("backslash", 0x5C); Add("bracketright", 0x5D); Add("asciicircum", 0x5E); Add("underscore", 0x5F); Add("grave", 0x60);
        Add("braceleft", 0x7B); Add("bar", 0x7C); Add("braceright", 0x7D); Add("asciitilde", 0x7E);
        // ISO Latin-1 from U+00A0.
        var latin1 = new[]
        {
            "nbspace", "exclamdown", "cent", "sterling", "currency", "yen", "brokenbar", "section", "dieresis", "copyright", "ordfeminine", "guillemotleft", "logicalnot", "sfthyphen", "registered", "macron",
            "degree", "plusminus", "twosuperior", "threesuperior", "acute", "mu", "paragraph", "periodcentered", "cedilla", "onesuperior", "ordmasculine", "guillemotright", "onequarter", "onehalf", "threequarters", "questiondown",
            "Agrave", "Aacute", "Acircumflex", "Atilde", "Adieresis", "Aring", "AE", "Ccedilla", "Egrave", "Eacute", "Ecircumflex", "Edieresis", "Igrave", "Iacute", "Icircumflex", "Idieresis",
            "Eth", "Ntilde", "Ograve", "Oacute", "Ocircumflex", "Otilde", "Odieresis", "multiply", "Oslash", "Ugrave", "Uacute", "Ucircumflex", "Udieresis", "Yacute", "Thorn", "germandbls",
            "agrave", "aacute", "acircumflex", "atilde", "adieresis", "aring", "ae", "ccedilla", "egrave", "eacute", "ecircumflex", "edieresis", "igrave", "iacute", "icircumflex", "idieresis",
            "eth", "ntilde", "ograve", "oacute", "ocircumflex", "otilde", "odieresis", "divide", "oslash", "ugrave", "uacute", "ucircumflex", "udieresis", "yacute", "thorn", "ydieresis",
        };
        for (var k = 0; k < latin1.Length; k++) Add(latin1[k], 0xA0 + k);
        // Names of the Standard, WinAnsi and MacRoman encodings outside ASCII and Latin-1.
        Add("dotlessi", 0x0131); Add("Lslash", 0x0141); Add("lslash", 0x0142); Add("OE", 0x0152); Add("oe", 0x0153); Add("Scaron", 0x0160); Add("scaron", 0x0161);
        Add("Ydieresis", 0x0178); Add("Zcaron", 0x017D); Add("zcaron", 0x017E); Add("florin", 0x0192);
        Add("circumflex", 0x02C6); Add("caron", 0x02C7); Add("breve", 0x02D8); Add("dotaccent", 0x02D9); Add("ring", 0x02DA); Add("ogonek", 0x02DB); Add("tilde", 0x02DC); Add("hungarumlaut", 0x02DD);
        Add("Omega", 0x2126); Add("pi", 0x03C0);
        Add("endash", 0x2013); Add("emdash", 0x2014); Add("quoteleft", 0x2018); Add("quoteright", 0x2019); Add("quotesinglbase", 0x201A); Add("quotedblleft", 0x201C); Add("quotedblright", 0x201D); Add("quotedblbase", 0x201E);
        Add("dagger", 0x2020); Add("daggerdbl", 0x2021); Add("bullet", 0x2022); Add("ellipsis", 0x2026); Add("perthousand", 0x2030); Add("guilsinglleft", 0x2039); Add("guilsinglright", 0x203A);
        Add("fraction", 0x2044); Add("Euro", 0x20AC); Add("trademark", 0x2122);
        Add("partialdiff", 0x2202); Add("Delta", 0x2206); Add("product", 0x220F); Add("summation", 0x2211); Add("minus", 0x2212); Add("radical", 0x221A); Add("infinity", 0x221E); Add("integral", 0x222B);
        Add("approxequal", 0x2248); Add("notequal", 0x2260); Add("lessequal", 0x2264); Add("greaterequal", 0x2265); Add("lozenge", 0x25CA);
        Add("fi", 0xFB01); Add("fl", 0xFB02);
        // Common alternative names.
        Add("nonbreakingspace", 0xA0); Add("softhyphen", 0xAD); Add("middot", 0xB7); Add("Dcroat", 0x0110); Add("dcroat", 0x0111);
        return table;
    });
}
