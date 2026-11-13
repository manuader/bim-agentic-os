using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
namespace Bim.Cad;

// A ToUnicode CMap (PDF 32000-1 9.10.3): codespace ranges plus bfchar and bfrange mappings (incrementing and array
// forms) from character codes of one to four bytes to UTF-16BE destinations. Other CMap operators and PostScript
// boilerplate are skipped. Bounded: at most MaxBytes of stream data, MaxEntries mapped codes (a bfrange counts every
// code it covers) and MaxCodespaceRanges ranges (the CMap block limit of 100) per CMap, and every mapped code is also
// charged to the document-wide MaxParsedElements budget (element-limit refusal), so many fonts cannot multiply the
// work; a destination longer than MaxDestinationBytes, of odd length or not a hex string, a source that is not a 1-4
// byte string and a reversed or mismatched range are skipped. Lookups are by code value only (<41> and <0041> are the
// same code); the code length comes from the font (one byte for simple fonts) or the codespace. An incrementing
// bfrange increments the last character of its destination: a surrogate pair as one code point (U+1F7FF is followed by
// U+1F800), a BMP character up to U+FFFF, after which the remaining codes of the range stay unmapped.
internal sealed class PdfToUnicode
{
    public const int MaxBytes = 1 << 20;
    public const int MaxEntries = 65536;
    public const int MaxCodespaceRanges = 100;
    public const int MaxDestinationBytes = 64;

    private readonly Dictionary<uint, string> map = new();
    private readonly List<(int Length, byte[] Low, byte[] High)> codespace = new();

    private readonly PdfBudget budget;

    private PdfToUnicode(PdfBudget budget)
    {
        this.budget = budget;
    }

    public int Count => map.Count;

    public bool TryMap(uint code, [MaybeNullWhen(false)] out string text) => map.TryGetValue(code, out text);

    // The length (1-4) of the codespace range matching the code that starts at bytes[at], or 0 when none does.
    public int CodeLength(byte[] bytes, int at)
    {
        for (var length = 1; length <= 4 && at + length <= bytes.Length; length++)
        {
            foreach (var (rangeLength, low, high) in codespace)
            {
                if (rangeLength != length) continue;
                var inside = true;
                for (var k = 0; k < length && inside; k++) inside = bytes[at + k] >= low[k] && bytes[at + k] <= high[k];
                if (inside) return length;
            }
        }
        return 0;
    }

    // Parses a decoded ToUnicode stream; null with a reason when it is malformed, over a bound or maps nothing.
    public static PdfToUnicode? Parse(byte[] data, int maxDepth, PdfBudget budget, out string error)
    {
        error = "";
        if (data.Length > MaxBytes)
        {
            error = "CMap of " + I(data.Length) + " bytes exceeds " + I(MaxBytes);
            return null;
        }
        var result = new PdfToUnicode(budget);
        var lexer = new PdfLexer(data, 0, data.Length, maxDepth, budget);
        try
        {
            while (lexer.ReadObject(false) is { } token)
            {
                if (token is not PdfKeyword keyword) continue;
                var ok = keyword.Value switch
                {
                    "begincodespacerange" => result.ReadCodespace(lexer, out error),
                    "beginbfchar" => result.ReadChars(lexer, out error),
                    "beginbfrange" => result.ReadRanges(lexer, out error),
                    _ => true,
                };
                if (!ok) return null;
            }
        }
        catch (PdfSyntaxException syntax)
        {
            error = syntax.Message + " at offset " + I(syntax.Offset);
            return null;
        }
        if (result.map.Count == 0)
        {
            error = "CMap maps no character codes";
            return null;
        }
        return result;
    }

    private static string I(long value) => value.ToString(CultureInfo.InvariantCulture);

    // Reads block items until the end keyword; false (with error) at the end of the data or on another keyword.
    private static bool Next(PdfLexer lexer, string end, out object? item, out bool done, out string error)
    {
        error = "";
        done = false;
        item = lexer.ReadObject(false);
        if (item is null) { error = "CMap ends inside a block before " + end; return false; }
        if (item is PdfKeyword keyword)
        {
            if (keyword.Value == end) { done = true; return true; }
            error = "unexpected " + keyword.Value + " before " + end;
            return false;
        }
        return true;
    }

    private bool ReadCodespace(PdfLexer lexer, out string error)
    {
        while (true)
        {
            if (!Next(lexer, "endcodespacerange", out var low, out var done, out error)) return false;
            if (done) return true;
            if (!Next(lexer, "endcodespacerange", out var high, out done, out error)) return false;
            if (done) { error = "odd codespace range"; return false; }
            if (low is PdfString { Bytes.Length: >= 1 and <= 4 } l && high is PdfString h && h.Bytes.Length == l.Bytes.Length)
            {
                if (codespace.Count >= MaxCodespaceRanges) { error = "codespace ranges exceed " + I(MaxCodespaceRanges); return false; }
                codespace.Add((l.Bytes.Length, l.Bytes, h.Bytes));
            }
        }
    }

    private bool ReadChars(PdfLexer lexer, out string error)
    {
        while (true)
        {
            if (!Next(lexer, "endbfchar", out var source, out var done, out error)) return false;
            if (done) return true;
            if (!Next(lexer, "endbfchar", out var destination, out done, out error)) return false;
            if (done) { error = "bfchar source without destination"; return false; }
            if (Code(source) is { } code && Destination(destination) is { } text && !Add(code, text, out error)) return false;
        }
    }

    private bool ReadRanges(PdfLexer lexer, out string error)
    {
        while (true)
        {
            if (!Next(lexer, "endbfrange", out var lowItem, out var done, out error)) return false;
            if (done) return true;
            if (!Next(lexer, "endbfrange", out var highItem, out done, out error) || done
                || !Next(lexer, "endbfrange", out var destination, out done, out error) || done)
            {
                if (done) error = "bfrange needs three operands";
                return false;
            }
            if (Code(lowItem) is not { } low || Code(highItem) is not { } high || ((PdfString)lowItem!).Bytes.Length != ((PdfString)highItem!).Bytes.Length || low > high) continue;
            var span = (long)high - low + 1;
            if (span > MaxEntries - map.Count)
            {
                error = "mapped codes exceed " + I(MaxEntries);
                return false;
            }
            if (destination is List<object> items)
            {
                for (var k = 0; k < items.Count && k < span; k++)
                    if (Destination(items[k]) is { } text && !Add(low + (uint)k, text, out error)) return false;
                continue;
            }
            if (Destination(destination) is not { } first) continue;
            var pair = first.Length >= 2 && char.IsSurrogatePair(first[^2], first[^1]);
            var prefix = first[..^(pair ? 2 : 1)];
            long point = pair ? char.ConvertToUtf32(first[^2], first[^1]) : first[^1];
            var limit = pair ? 0x10FFFF : 0xFFFF;
            for (long k = 0; k < span && point + k <= limit; k++)
            {
                var value = (int)(point + k);
                var text = prefix + (pair ? char.ConvertFromUtf32(value) : ((char)value).ToString());
                if (!Add(low + (uint)k, text, out error)) return false;
            }
        }
    }

    private bool Add(uint code, string text, out string error)
    {
        error = "";
        budget.Charge(-1);
        if (map.ContainsKey(code)) { map[code] = text; return true; }
        if (map.Count >= MaxEntries) { error = "mapped codes exceed " + I(MaxEntries); return false; }
        map[code] = text;
        return true;
    }

    private static uint? Code(object? item)
    {
        if (item is not PdfString { Bytes.Length: >= 1 and <= 4 } source) return null;
        uint value = 0;
        foreach (var b in source.Bytes) value = (value << 8) | b;
        return value;
    }

    private static string? Destination(object? item) =>
        item is PdfString { Bytes.Length: >= 2 and <= MaxDestinationBytes } text && text.Bytes.Length % 2 == 0 ? Encoding.BigEndianUnicode.GetString(text.Bytes) : null;
}

// How the strings shown with one font become text. Without a ToUnicode map or an encoding the reader's default
// decoding applies (PdfContentInterpreter.DecodeText). A simple font maps every byte through ToUnicode, then (P6-18)
// through its /Encoding dictionary (PdfSimpleEncoding), falling back to Latin-1 for a byte neither maps; a byte whose
// /Differences glyph name is unknown and that ToUnicode does not map is reported in unknownGlyph (the first such name).
// A Type0 font splits the string into codes (two bytes for Identity-H/V, else the CMap codespace, else two bytes) and
// an unmapped code, or a trailing partial code, becomes U+FFFD.
internal sealed class PdfFontDecoder
{
    public PdfFontDecoder(bool composite, bool identity, PdfToUnicode? toUnicode, PdfSimpleEncoding? encoding = null)
    {
        Composite = composite;
        Identity = identity;
        ToUnicode = toUnicode;
        SimpleEncoding = composite ? null : encoding;
    }

    public bool Composite { get; }
    public bool Identity { get; }
    public PdfToUnicode? ToUnicode { get; }
    public PdfSimpleEncoding? SimpleEncoding { get; }

    public string Decode(byte[] bytes, out bool unmapped) => Decode(bytes, out unmapped, out _);

    public string Decode(byte[] bytes, out bool unmapped, out string? unknownGlyph)
    {
        unmapped = false;
        unknownGlyph = null;
        var cmap = ToUnicode;
        if (cmap is null && SimpleEncoding is null) return PdfContentInterpreter.DecodeText(bytes);
        var text = new StringBuilder(bytes.Length);
        if (!Composite)
        {
            foreach (var b in bytes)
            {
                if (cmap is not null && cmap.TryMap(b, out var mapped)) text.Append(mapped);
                else if (SimpleEncoding is not null && SimpleEncoding.TryMap(b, out var named)) text.Append(named);
                else
                {
                    if (SimpleEncoding?.UnknownName(b) is { } glyph) unknownGlyph ??= glyph;
                    text.Append((char)b);
                }
            }
            return text.ToString();
        }
        if (cmap is null) return PdfContentInterpreter.DecodeText(bytes);
        var at = 0;
        while (at < bytes.Length)
        {
            var length = Identity ? 2 : cmap.CodeLength(bytes, at);
            if (length == 0) length = 2;
            if (at + length > bytes.Length)
            {
                text.Append('�');
                unmapped = true;
                break;
            }
            uint code = 0;
            for (var k = 0; k < length; k++) code = (code << 8) | bytes[at + k];
            if (cmap.TryMap(code, out var mapped)) text.Append(mapped);
            else
            {
                text.Append('�');
                unmapped = true;
            }
            at += length;
        }
        return text.ToString();
    }
}
