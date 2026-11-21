using System.Globalization;
using System.Text;
namespace Bim.Cad;

// Interprets the content streams of one page: graphics state (cm, q/Q, w), path construction (m, l, c, v, y, h, re),
// path painting (S, s, f, F, f*, B, B*, b, b*, n; W/W* clipping is ignored), text (BT/ET, Tf, TL, Ts, Td, TD, Tm, T*,
// Tj, TJ, ', ") and Form XObjects (Do). Colour, dash, marked-content and other state operators are accepted and
// ignored; inline images are skipped. Operand errors are warnings and the operator is skipped.
// P6-16: Tf resolves the font in the current resources; a font with a /ToUnicode CMap (simple or Type0) decodes shown
// strings through it (PdfFontDecoder). A Type0 font without one (tounicode-missing) or with an unusable one
// (tounicode-invalid, as for a simple font; a dangling reference is invalid, /ToUnicode null is absent) keeps the
// default decoding; both warn once per font and page at the Tf. With a CMap, decoded characters are charged to
// MaxInterpretedBytes as well as the shown bytes.
// A Type0 code missing from the CMap becomes U+FFFD with tounicode-unmapped on the showing operator.
// P6-18: a simple font whose /Encoding is a dictionary (/BaseEncoding, /Differences) decodes through it
// (PdfSimpleEncoding) after ToUnicode, with or without a usable CMap; a shown code whose /Differences glyph name is
// unknown keeps the byte decoding with encoding-glyph-unknown on the showing operator.
// P6-20: an /Encoding name (/WinAnsiEncoding, /MacRomanEncoding, /StandardEncoding) decodes through its base table
// after ToUnicode (another name keeps the default decoding); an unknown /BaseEncoding warns encoding-base-unknown once
// per font and page at the Tf; a Type3 font keeps the byte decoding of /Differences names outside the glyph list
// without encoding-glyph-unknown.
// P6-22: a Type3 /Differences name that is a real glyph-list name the reader does not map (PdfGlyphNames.IsGlyphListName)
// warns encoding-glyph-unsupported once per font and page; its codes keep the byte decoding.
// P6-24: that warning is given at the first show operator (Tj, TJ, ', ") that shows an affected code (a code whose
// /Differences name is such a name and that the ToUnicode CMap does not map), not at the Tf, and lists only the names
// of the affected codes that operator shows (code order, deduplicated, at most eight); a font selected but never
// showing an affected code is silent. Only a show that emits text counts: one outside BT ... ET or at a non-finite
// position keeps the warning pending.
// P6-25: the affected names come from PdfSimpleEncoding.UnsupportedGlyphListName (one filter); a font dictionary under
// two resource names shares one decoder and so warns once, named by the resource active at the showing operator.
internal sealed class PdfContentInterpreter
{
    private readonly PdfFile file;
    private readonly int page;
    private readonly List<PdfElement> elements = new();
    private readonly Stack<GraphicsState> stack = new();
    private readonly HashSet<int> activeForms = new();
    private readonly Dictionary<object, PdfFontDecoder?> fontDecoders = new(ReferenceEqualityComparer.Instance);
    // P6-24: Type3 decoders whose encoding-glyph-unsupported warning is still pending on this page.
    private readonly Dictionary<PdfFontDecoder, Type3GlyphList> pendingGlyphLists = new();
    private List<PdfSubpath> subpaths = new();
    private List<PdfSegment> segments = new();
    private GraphicsState state = new(PdfMatrix.Identity, 1, "", 0, 0, 0, null);
    private Point2? subpathStart;
    private Point2? current;
    private int stackFloor;
    private bool inText;
    private PdfMatrix textMatrix = PdfMatrix.Identity;
    private PdfMatrix lineMatrix = PdfMatrix.Identity;
    private IReadOnlyList<string> invocation = Array.Empty<string>();

    public PdfContentInterpreter(PdfFile file, int page)
    {
        this.file = file;
        this.page = page;
    }

    public IReadOnlyList<PdfElement> Elements => elements;

    private readonly record struct GraphicsState(PdfMatrix Ctm, double LineWidth, string FontName, double FontSize, double Leading, double Rise, PdfFontDecoder? Decoder);

    // Interprets content as one stream. A page's /Contents array is concatenated (with a white-space byte between
    // parts) as the PDF specification requires, so a path or a token may continue from one part into the next; each
    // operator is attributed to the part (object number) that holds its first byte and indexed within that part.
    // The whole input is charged to the document's interpreted-bytes budget before it is read.
    public void Run(IReadOnlyList<(int ObjectNumber, byte[] Content)> parts, Dictionary<string, object>? resources, int depth)
    {
        if (parts.Count == 0) return;
        byte[] content;
        var starts = new int[parts.Count];
        if (parts.Count == 1) content = parts[0].Content;
        else
        {
            long total = parts.Sum(p => (long)p.Content.Length + 1);
            file.ChargeInterpreted(total);
            if (total > int.MaxValue) throw new PdfFormatException("interpret-limit", -1, "page content too large to interpret");
            content = new byte[total];
            var at = 0;
            for (var k = 0; k < parts.Count; k++)
            {
                starts[k] = at;
                Buffer.BlockCopy(parts[k].Content, 0, content, at, parts[k].Content.Length);
                at += parts[k].Content.Length;
                content[at++] = 10;
            }
        }
        if (parts.Count == 1) file.ChargeInterpreted(content.Length);
        var counters = new int[parts.Count];
        var part = 0;
        var options = file.Options;
        var lexer = new PdfLexer(content, 0, content.Length, options.MaxDepth, file.Budget);
        var operands = new List<object>();
        while (true)
        {
            object? token;
            lexer.SkipWhite();
            var offset = lexer.Position;
            try
            {
                token = lexer.ReadObject(false);
                while (part + 1 < parts.Count && offset >= starts[part + 1]) part++;
                if (token is PdfKeyword { Value: "BI" })
                {
                    file.CountOperator(offset);
                    var imageIndex = counters[part]++;
                    if (!SkipInlineImage(lexer)) Warn("content-malformed", parts[part].ObjectNumber, imageIndex, "inline image without ID ... EI");
                    operands.Clear();
                    continue;
                }
            }
            catch (PdfSyntaxException error)
            {
                while (part + 1 < parts.Count && error.Offset >= starts[part + 1]) part++;
                Warn("content-malformed", parts[part].ObjectNumber, counters[part], error.Message + " at offset " + (error.Offset - starts[part]).ToString(CultureInfo.InvariantCulture));
                break;
            }
            if (token is null) break;
            if (token is not PdfKeyword keyword)
            {
                if (operands.Count < 64) operands.Add(token);
                continue;
            }
            file.CountOperator(offset);
            var operatorIndex = counters[part]++;
            Execute(keyword.Value, operands, parts[part].ObjectNumber, operatorIndex, resources, depth);
            operands.Clear();
        }
        FinishSubpath();
        subpaths.Clear();
    }

    private static bool SkipInlineImage(PdfLexer lexer)
    {
        while (true)
        {
            var token = lexer.ReadObject(false);
            if (token is null) return false;
            if (token is PdfKeyword { Value: "ID" }) return lexer.SkipInlineImageData();
        }
    }

    private void Warn(string code, int objectNumber, int operatorIndex, string message) => file.Warn(code, page, objectNumber, operatorIndex, message);

    private bool Numbers(List<object> operands, int count, string op, int objectNumber, int operatorIndex, out double[] values)
    {
        values = new double[count];
        if (operands.Count == count)
        {
            var ok = true;
            for (var i = 0; i < count && ok; i++)
            {
                if (operands[i] is PdfNumber number) values[i] = number.Value;
                else ok = false;
            }
            if (ok) return true;
        }
        Warn("operand-error", objectNumber, operatorIndex, op + " expects " + count.ToString(CultureInfo.InvariantCulture) + " numbers");
        return false;
    }

    private Point2? Transform(double x, double y, int objectNumber, int operatorIndex, string op)
    {
        var p = state.Ctm.Apply(new Point2(x, y));
        if (double.IsFinite(p.X) && double.IsFinite(p.Y)) return p;
        Warn("operand-error", objectNumber, operatorIndex, op + " gives a non-finite point");
        return null;
    }

    private void Execute(string op, List<object> operands, int objectNumber, int operatorIndex, Dictionary<string, object>? resources, int depth)
    {
        double[] v;
        switch (op)
        {
            case "q":
                if (stack.Count >= file.Options.MaxDepth)
                    throw new PdfFormatException("depth-limit", -1, "q nesting exceeds MaxDepth " + file.Options.MaxDepth.ToString(CultureInfo.InvariantCulture));
                stack.Push(state);
                break;
            case "Q":
                if (stack.Count <= stackFloor) Warn("unbalanced-restore", objectNumber, operatorIndex, "Q without a matching q");
                else state = stack.Pop();
                break;
            case "cm":
                if (Numbers(operands, 6, op, objectNumber, operatorIndex, out v))
                {
                    var ctm = new PdfMatrix(v[0], v[1], v[2], v[3], v[4], v[5]).Then(state.Ctm);
                    if (ctm.IsFinite) state = state with { Ctm = ctm };
                    else Warn("operand-error", objectNumber, operatorIndex, "cm gives a non-finite matrix");
                }
                break;
            case "w":
                if (Numbers(operands, 1, op, objectNumber, operatorIndex, out v)) state = state with { LineWidth = v[0] };
                break;
            case "m":
                if (Numbers(operands, 2, op, objectNumber, operatorIndex, out v) && Transform(v[0], v[1], objectNumber, operatorIndex, op) is { } moveTo)
                {
                    FinishSubpath();
                    subpathStart = moveTo;
                    current = moveTo;
                }
                break;
            case "l":
                if (Numbers(operands, 2, op, objectNumber, operatorIndex, out v) && HasCurrent(objectNumber, operatorIndex, op) && Transform(v[0], v[1], objectNumber, operatorIndex, op) is { } lineTo)
                    AddLine(lineTo, operatorIndex, op);
                break;
            case "c":
                if (Numbers(operands, 6, op, objectNumber, operatorIndex, out v) && HasCurrent(objectNumber, operatorIndex, op)
                    && Transform(v[0], v[1], objectNumber, operatorIndex, op) is { } c1 && Transform(v[2], v[3], objectNumber, operatorIndex, op) is { } c2 && Transform(v[4], v[5], objectNumber, operatorIndex, op) is { } c3)
                    AddCurve(c1, c2, c3, operatorIndex, op);
                break;
            case "v":
                if (Numbers(operands, 4, op, objectNumber, operatorIndex, out v) && HasCurrent(objectNumber, operatorIndex, op) && current is { } vStart
                    && Transform(v[0], v[1], objectNumber, operatorIndex, op) is { } v2 && Transform(v[2], v[3], objectNumber, operatorIndex, op) is { } v3)
                    AddCurve(vStart, v2, v3, operatorIndex, op);
                break;
            case "y":
                if (Numbers(operands, 4, op, objectNumber, operatorIndex, out v) && HasCurrent(objectNumber, operatorIndex, op)
                    && Transform(v[0], v[1], objectNumber, operatorIndex, op) is { } y1 && Transform(v[2], v[3], objectNumber, operatorIndex, op) is { } y3)
                    AddCurve(y1, y3, y3, operatorIndex, op);
                break;
            case "h":
                ClosePath(operatorIndex, op);
                break;
            case "re":
                if (Numbers(operands, 4, op, objectNumber, operatorIndex, out v)
                    && Transform(v[0], v[1], objectNumber, operatorIndex, op) is { } r0 && Transform(v[0] + v[2], v[1], objectNumber, operatorIndex, op) is { } r1
                    && Transform(v[0] + v[2], v[1] + v[3], objectNumber, operatorIndex, op) is { } r2 && Transform(v[0], v[1] + v[3], objectNumber, operatorIndex, op) is { } r3)
                {
                    FinishSubpath();
                    subpathStart = r0;
                    current = r0;
                    AddLine(r1, operatorIndex, op);
                    AddLine(r2, operatorIndex, op);
                    AddLine(r3, operatorIndex, op);
                    ClosePath(operatorIndex, op);
                }
                break;
            case "S": Paint(objectNumber, operatorIndex, op, stroke: true, fill: false, evenOdd: false, close: false); break;
            case "s": Paint(objectNumber, operatorIndex, op, stroke: true, fill: false, evenOdd: false, close: true); break;
            case "f":
            case "F": Paint(objectNumber, operatorIndex, op, stroke: false, fill: true, evenOdd: false, close: false); break;
            case "f*": Paint(objectNumber, operatorIndex, op, stroke: false, fill: true, evenOdd: true, close: false); break;
            case "B": Paint(objectNumber, operatorIndex, op, stroke: true, fill: true, evenOdd: false, close: false); break;
            case "B*": Paint(objectNumber, operatorIndex, op, stroke: true, fill: true, evenOdd: true, close: false); break;
            case "b": Paint(objectNumber, operatorIndex, op, stroke: true, fill: true, evenOdd: false, close: true); break;
            case "b*": Paint(objectNumber, operatorIndex, op, stroke: true, fill: true, evenOdd: true, close: true); break;
            case "n": ResetPath(); break;
            case "BT":
                inText = true;
                textMatrix = PdfMatrix.Identity;
                lineMatrix = PdfMatrix.Identity;
                break;
            case "ET":
                inText = false;
                break;
            case "Tf":
                if (operands.Count == 2 && operands[0] is PdfName font && operands[1] is PdfNumber size)
                    state = state with { FontName = font.Value, FontSize = size.Value, Decoder = FontDecoder(font.Value, resources, objectNumber, operatorIndex) };
                else Warn("operand-error", objectNumber, operatorIndex, "Tf expects a font name and a size");
                break;
            case "TL":
                if (Numbers(operands, 1, op, objectNumber, operatorIndex, out v)) state = state with { Leading = v[0] };
                break;
            case "Ts":
                if (Numbers(operands, 1, op, objectNumber, operatorIndex, out v)) state = state with { Rise = v[0] };
                break;
            case "Td":
                if (Numbers(operands, 2, op, objectNumber, operatorIndex, out v)) MoveText(v[0], v[1]);
                break;
            case "TD":
                if (Numbers(operands, 2, op, objectNumber, operatorIndex, out v))
                {
                    state = state with { Leading = -v[1] };
                    MoveText(v[0], v[1]);
                }
                break;
            case "Tm":
                if (Numbers(operands, 6, op, objectNumber, operatorIndex, out v))
                {
                    var matrix = new PdfMatrix(v[0], v[1], v[2], v[3], v[4], v[5]);
                    if (matrix.IsFinite) { textMatrix = matrix; lineMatrix = matrix; }
                }
                break;
            case "T*":
                MoveText(0, -state.Leading);
                break;
            case "Tj":
                if (operands.Count == 1 && operands[0] is PdfString shown) Show(shown.Bytes, objectNumber, operatorIndex, op);
                else Warn("operand-error", objectNumber, operatorIndex, "Tj expects a string");
                break;
            case "'":
                if (operands.Count == 1 && operands[0] is PdfString nextLine)
                {
                    MoveText(0, -state.Leading);
                    Show(nextLine.Bytes, objectNumber, operatorIndex, op);
                }
                else Warn("operand-error", objectNumber, operatorIndex, "' expects a string");
                break;
            case "\"":
                if (operands.Count == 3 && operands[0] is PdfNumber && operands[1] is PdfNumber && operands[2] is PdfString spaced)
                {
                    MoveText(0, -state.Leading);
                    Show(spaced.Bytes, objectNumber, operatorIndex, op);
                }
                else Warn("operand-error", objectNumber, operatorIndex, "\" expects two numbers and a string");
                break;
            case "TJ":
                if (operands.Count == 1 && operands[0] is List<object> pieces)
                {
                    // Strings are decoded one by one; an adjustment below -200 thousandths of an em reads as a word space.
                    var joined = new StringBuilder();
                    long byteCount = 0;
                    var unmapped = false;
                    string? unknownGlyph = null;
                    bool[]? unsupported = null;
                    foreach (var item in pieces)
                    {
                        if (item is PdfString piece)
                        {
                            var decoded = Decode(piece.Bytes, out var missing, out var glyph);
                            unsupported = ShownUnsupported(piece.Bytes, unsupported);
                            if (state.Decoder is not null) file.ChargeInterpreted(decoded.Length);
                            joined.Append(decoded);
                            unmapped |= missing;
                            unknownGlyph ??= glyph;
                            byteCount += piece.Bytes.Length;
                        }
                        else if (item is PdfNumber adjustment && adjustment.Value < -200 && joined.Length > 0 && joined[^1] != ' ') joined.Append(' ');
                    }
                    if (unmapped) WarnUnmapped(objectNumber, operatorIndex, op);
                    if (unknownGlyph is not null) WarnUnknownGlyph(unknownGlyph, objectNumber, operatorIndex, op);
                    if (Show(joined.ToString(), byteCount, objectNumber, operatorIndex, op)) WarnUnsupportedGlyphs(unsupported, objectNumber, operatorIndex);
                }
                else Warn("operand-error", objectNumber, operatorIndex, "TJ expects an array");
                break;
            case "Do":
                if (operands.Count == 1 && operands[0] is PdfName name) InvokeXObject(name.Value, objectNumber, operatorIndex, resources, depth);
                else Warn("operand-error", objectNumber, operatorIndex, "Do expects a name");
                break;
        }
    }

    private bool HasCurrent(int objectNumber, int operatorIndex, string op)
    {
        if (current is not null) return true;
        Warn("no-current-point", objectNumber, operatorIndex, op + " without a current point");
        return false;
    }

    private void AddLine(Point2 end, int operatorIndex, string op)
    {
        if (current is not { } start) return;
        segments.Add(new PdfSegment(PdfSegmentKind.Line, start, start, end, end, operatorIndex, op));
        current = end;
    }

    private void AddCurve(Point2 control1, Point2 control2, Point2 end, int operatorIndex, string op)
    {
        if (current is not { } start) return;
        segments.Add(new PdfSegment(PdfSegmentKind.Cubic, start, control1, control2, end, operatorIndex, op));
        current = end;
    }

    // Closes the current subpath (a closing segment when the current point is not the start) and starts a new one at
    // the same point.
    private void ClosePath(int operatorIndex, string op)
    {
        if (subpathStart is not { } start || current is not { } at) return;
        if (at != start) segments.Add(new PdfSegment(PdfSegmentKind.Line, at, at, start, start, operatorIndex, op));
        FinishSubpath(closed: true);
        subpathStart = start;
        current = start;
    }

    private void FinishSubpath(bool closed = false)
    {
        if (subpathStart is { } start && segments.Count > 0) subpaths.Add(new PdfSubpath(start, segments.ToArray(), closed));
        segments = new List<PdfSegment>();
    }

    private void ResetPath()
    {
        segments = new List<PdfSegment>();
        subpaths.Clear();
        subpathStart = null;
        current = null;
    }

    private void Paint(int objectNumber, int operatorIndex, string op, bool stroke, bool fill, bool evenOdd, bool close)
    {
        if (close) ClosePath(operatorIndex, op);
        FinishSubpath();
        if (subpaths.Count > 0)
            elements.Add(new PdfPath(Provenance(objectNumber, operatorIndex, op), subpaths.ToArray(), stroke, fill, evenOdd, state.LineWidth));
        ResetPath();
    }

    private void MoveText(double tx, double ty)
    {
        var moved = PdfMatrix.Translation(tx, ty).Then(lineMatrix);
        if (!moved.IsFinite) return;
        lineMatrix = moved;
        textMatrix = moved;
    }

    private PdfProvenance Provenance(int objectNumber, int operatorIndex, string op) =>
        new(file.SourceFile, page, objectNumber, operatorIndex, op) { XObjectPath = invocation };

    private void Show(byte[] bytes, int objectNumber, int operatorIndex, string op)
    {
        file.ChargeInterpreted(bytes.Length);
        var text = Decode(bytes, out var unmapped, out var unknownGlyph);
        if (state.Decoder is not null) file.ChargeInterpreted(text.Length);
        if (unmapped) WarnUnmapped(objectNumber, operatorIndex, op);
        if (unknownGlyph is not null) WarnUnknownGlyph(unknownGlyph, objectNumber, operatorIndex, op);
        if (Show(text, 0, objectNumber, operatorIndex, op)) WarnUnsupportedGlyphs(ShownUnsupported(bytes, null), objectNumber, operatorIndex);
    }

    // P6-24: the affected names of a Type3 font (by code) and its ToUnicode CMap, kept until the warning is given.
    private sealed record Type3GlyphList(string Reference, string?[] Names, PdfToUnicode? CMap);

    // Marks the shown codes of the current font whose warning is pending and that the ToUnicode CMap does not map.
    private bool[]? ShownUnsupported(byte[] bytes, bool[]? shown)
    {
        if (state.Decoder is not { } decoder || !pendingGlyphLists.TryGetValue(decoder, out var glyphs)) return shown;
        foreach (var code in bytes)
            if (glyphs.Names[code] is not null && !(glyphs.CMap?.TryMap(code, out _) ?? false)) (shown ??= new bool[256])[code] = true;
        return shown;
    }

    private void WarnUnsupportedGlyphs(bool[]? shown, int objectNumber, int operatorIndex)
    {
        if (shown is null || state.Decoder is not { } decoder || !pendingGlyphLists.Remove(decoder, out var glyphs)) return;
        var names = new List<string>();
        for (var code = 0; code < 256; code++)
            if (shown[code] && glyphs.Names[code] is { } glyph && !names.Contains(glyph, StringComparer.Ordinal)) names.Add(glyph);
        Warn("encoding-glyph-unsupported", objectNumber, operatorIndex, "font /" + state.FontName + glyphs.Reference
            + " is a Type3 font whose /Differences names glyph-list names the reader does not map ("
            + string.Join(", ", names.Take(MaxListedGlyphNames).Select(name => "/" + name)) + (names.Count > MaxListedGlyphNames ? ", ..." : "")
            + (glyphs.CMap is not null ? "); codes that the ToUnicode CMap does not map are read as text bytes" : "); their codes are read as text bytes"));
    }

    private string Decode(byte[] bytes, out bool unmapped, out string? unknownGlyph)
    {
        unmapped = false;
        unknownGlyph = null;
        return state.Decoder is { } decoder ? decoder.Decode(bytes, out unmapped, out unknownGlyph) : DecodeText(bytes);
    }

    private void WarnUnknownGlyph(string glyph, int objectNumber, int operatorIndex, string op) =>
        Warn("encoding-glyph-unknown", objectNumber, operatorIndex, op + " shows a code whose /Differences glyph name /" + glyph + " of font /" + state.FontName + " is not in the glyph list; it is read as a text byte");

    private void WarnUnmapped(int objectNumber, int operatorIndex, string op) =>
        Warn("tounicode-unmapped", objectNumber, operatorIndex, op + " shows codes that the ToUnicode CMap of font /" + state.FontName + " does not map (U+FFFD)");

    // The decoder of the named font in the current resources, cached per font dictionary for the page; null keeps the
    // default decoding. Warnings are given once, when the font is first selected on the page (encoding-glyph-unsupported:
    // at the first operator showing an affected code, P6-24).
    private const int MaxListedGlyphNames = 8;

    private PdfFontDecoder? FontDecoder(string name, Dictionary<string, object>? resources, int objectNumber, int operatorIndex)
    {
        var fonts = file.Resolve(PdfFile.Get(resources, "Font")) as Dictionary<string, object>;
        var reference = PdfFile.Get(fonts, name);
        if (file.Resolve(reference) is not Dictionary<string, object> font) return null;
        if (fontDecoders.TryGetValue(font, out var cached)) return cached;
        var composite = file.IsName(font, "Subtype", "Type0");
        var identity = file.Resolve(PdfFile.Get(font, "Encoding")) is PdfName { Value: "Identity-H" or "Identity-V" };
        var objectLabel = reference is PdfRef r ? " (object " + r.Number.ToString(CultureInfo.InvariantCulture) + ")" : "";
        var label = "font /" + name + objectLabel;
        PdfFontDecoder? decoder = null;
        PdfToUnicode? cmap = null;
        var toUnicode = PdfFile.Get(font, "ToUnicode");
        if (toUnicode is null or PdfNull)
        {
            if (composite) Warn("tounicode-missing", objectNumber, operatorIndex, label + " is a Type0 font without /ToUnicode; its codes are read as text bytes");
        }
        else
        {
            var error = "/ToUnicode is not a stream";
            cmap = file.Resolve(toUnicode) is PdfStream stream ? file.ToUnicode(stream, page, out error) : null;
            if (cmap is null)
            {
                Warn("tounicode-invalid", objectNumber, operatorIndex, label + " has an unusable /ToUnicode CMap: " + error + "; its codes are read as text bytes");
            }
        }
        var encoding = composite ? null : file.Resolve(PdfFile.Get(font, "Encoding")) switch
        {
            Dictionary<string, object> encodingDictionary => PdfSimpleEncoding.Get(file, encodingDictionary),
            PdfName encodingName => PdfSimpleEncoding.Named(encodingName.Value),
            _ => null,
        };
        if (encoding?.UnknownBase is { } unknownBase)
            Warn("encoding-base-unknown", objectNumber, operatorIndex, label + " has /BaseEncoding " + unknownBase + ", not /WinAnsiEncoding, /MacRomanEncoding or /StandardEncoding; codes outside /Differences that the ToUnicode CMap does not map are read as text bytes");
        string?[]? unsupportedNames = null;
        if (encoding is not null && file.IsName(font, "Subtype", "Type3"))
        {
            if (encoding.UnsupportedGlyphListNames.Count > 0)
            {
                unsupportedNames = new string?[256];
                for (var code = 0; code < 256; code++)
                    unsupportedNames[code] = encoding.UnsupportedGlyphListName((byte)code);
            }
            encoding = encoding.WithoutUnknownNames;
        }
        if (cmap is not null || encoding is not null) decoder = new PdfFontDecoder(composite, identity, cmap, encoding);
        if (decoder is not null && unsupportedNames is not null) pendingGlyphLists[decoder] = new Type3GlyphList(objectLabel, unsupportedNames, cmap);
        fontDecoders[font] = decoder;
        return decoder;
    }

    // Adds the text element; false when the show emits no text (outside BT ... ET or a non-finite position or height),
    // which then gives no encoding-glyph-unsupported (P6-24 review: only a show that emits text counts).
    private bool Show(string text, long byteCount, int objectNumber, int operatorIndex, string op)
    {
        file.ChargeInterpreted(byteCount);
        if (!inText)
        {
            Warn("text-outside-bt", objectNumber, operatorIndex, op + " outside BT ... ET");
            return false;
        }
        var rendering = PdfMatrix.Translation(0, state.Rise).Then(textMatrix).Then(state.Ctm);
        var origin = new Point2(rendering.E, rendering.F);
        if (!rendering.IsFinite)
        {
            Warn("operand-error", objectNumber, operatorIndex, op + " gives a non-finite text position");
            return false;
        }
        var height = Math.Abs(state.FontSize) * Hypot(rendering.C, rendering.D);
        if (!double.IsFinite(height))
        {
            Warn("operand-error", objectNumber, operatorIndex, op + " gives a non-finite text height");
            return false;
        }
        var rotation = PdfGeometry.AngleDegrees(rendering.A, rendering.B);
        elements.Add(new PdfText(Provenance(objectNumber, operatorIndex, op), origin, text, state.FontName, state.FontSize, height, rotation));
        return true;
    }

    // sqrt(x^2 + y^2) without overflow for large finite components.
    private static double Hypot(double x, double y)
    {
        x = Math.Abs(x);
        y = Math.Abs(y);
        var max = Math.Max(x, y);
        if (max == 0) return 0;
        var min = Math.Min(x, y) / max;
        return max * Math.Sqrt(1 + min * min);
    }

    internal static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.Latin1.GetString(bytes);
    }

    private void InvokeXObject(string name, int objectNumber, int operatorIndex, Dictionary<string, object>? resources, int depth)
    {
        var xobjects = file.Resolve(PdfFile.Get(resources, "XObject")) as Dictionary<string, object>;
        var reference = PdfFile.Get(xobjects, name);
        if (file.Resolve(reference) is not PdfStream form)
        {
            Warn("xobject-missing", objectNumber, operatorIndex, "XObject /" + name + " not found");
            return;
        }
        if (!file.IsName(form.Dictionary, "Subtype", "Form")) return;
        if (depth + 1 > file.Options.MaxDepth)
            throw new PdfFormatException("depth-limit", -1, "Form XObject nesting exceeds MaxDepth " + file.Options.MaxDepth.ToString(CultureInfo.InvariantCulture));
        if (!activeForms.Add(form.ObjectNumber))
        {
            Warn("xobject-cycle", objectNumber, operatorIndex, "Form XObject " + form.ObjectNumber.ToString(CultureInfo.InvariantCulture) + " invokes itself");
            return;
        }
        try
        {
            var content = file.Decode(form, page);
            if (content is null) return;
            var matrix = PdfMatrix.Identity;
            // P6-35: a /Matrix that is present (not absent, null or a dangling reference) but not an array of exactly six
            // numbers is reported like cm with bad operands: operand-error at the Do, and the identity is used.
            if (file.Resolve(PdfFile.Get(form.Dictionary, "Matrix")) is { } matrixValue)
            {
                var items = matrixValue as List<object>;
                var values = new double[6];
                var ok = items is { Count: 6 };
                for (var i = 0; ok && i < 6; i++)
                {
                    if (file.Resolve(items![i]) is PdfNumber number) values[i] = number.Value;
                    else ok = false;
                }
                if (ok) matrix = new PdfMatrix(values[0], values[1], values[2], values[3], values[4], values[5]);
                else Warn("operand-error", objectNumber, operatorIndex, "Do /" + name + " expects a /Matrix of 6 numbers; the identity matrix is used");
            }
            var savedState = state;
            var savedFloor = stackFloor;
            var savedDepth = stack.Count;
            var savedText = (inText, textMatrix, lineMatrix);
            // The pending path is set aside by reference (no copy) and a fresh one is used inside the form.
            var savedPath = (segments, subpathStart, current, subpaths);
            segments = new List<PdfSegment>();
            subpaths = new List<PdfSubpath>();
            subpathStart = null;
            current = null;
            // P6-33: a /Matrix whose composition with the CTM is non-finite is reported like a non-finite cm: operand-error
            // at the Do, and the form runs under the current CTM.
            var ctm = matrix.Then(state.Ctm);
            if (ctm.IsFinite) state = state with { Ctm = ctm };
            else Warn("operand-error", objectNumber, operatorIndex, "Do /" + name + " gives a non-finite form matrix; the current matrix is kept");
            stackFloor = stack.Count;
            var formResources = file.Resolve(PdfFile.Get(form.Dictionary, "Resources")) as Dictionary<string, object> ?? resources;
            var savedInvocation = invocation;
            invocation = invocation.Append("o" + objectNumber.ToString(CultureInfo.InvariantCulture) + ":i" + operatorIndex.ToString(CultureInfo.InvariantCulture)).ToArray();
            try
            {
                Run(new[] { (form.ObjectNumber, content) }, formResources, depth + 1);
            }
            finally
            {
                invocation = savedInvocation;
            }
            while (stack.Count > savedDepth) stack.Pop();
            stackFloor = savedFloor;
            state = savedState;
            (inText, textMatrix, lineMatrix) = savedText;
            segments = savedPath.segments;
            subpathStart = savedPath.subpathStart;
            current = savedPath.current;
            subpaths = savedPath.subpaths;
        }
        finally
        {
            activeForms.Remove(form.ObjectNumber);
        }
    }
}
