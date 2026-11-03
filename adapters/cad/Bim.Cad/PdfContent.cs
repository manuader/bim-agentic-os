using System.Globalization;
using System.Text;
namespace Bim.Cad;

// Interprets the content streams of one page: graphics state (cm, q/Q, w), path construction (m, l, c, v, y, h, re),
// path painting (S, s, f, F, f*, B, B*, b, b*, n; W/W* clipping is ignored), text (BT/ET, Tf, TL, Ts, Td, TD, Tm, T*,
// Tj, TJ, ', ") and Form XObjects (Do). Colour, dash, marked-content and other state operators are accepted and
// ignored; inline images are skipped. Operand errors are warnings and the operator is skipped.
internal sealed class PdfContentInterpreter
{
    private readonly PdfFile file;
    private readonly int page;
    private readonly List<PdfElement> elements = new();
    private readonly Stack<GraphicsState> stack = new();
    private readonly HashSet<int> activeForms = new();
    private readonly List<PdfSubpath> subpaths = new();
    private List<PdfSegment> segments = new();
    private GraphicsState state = new(PdfMatrix.Identity, 1, "", 0, 0, 0);
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

    private readonly record struct GraphicsState(PdfMatrix Ctm, double LineWidth, string FontName, double FontSize, double Leading, double Rise);

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
        var lexer = new PdfLexer(content, 0, content.Length, options.MaxDepth);
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
                if (operands.Count == 2 && operands[0] is PdfName font && operands[1] is PdfNumber size) state = state with { FontName = font.Value, FontSize = size.Value };
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
                    foreach (var item in pieces)
                    {
                        if (item is PdfString piece) { joined.Append(DecodeText(piece.Bytes)); byteCount += piece.Bytes.Length; }
                        else if (item is PdfNumber adjustment && adjustment.Value < -200 && joined.Length > 0 && joined[^1] != ' ') joined.Append(' ');
                    }
                    Show(joined.ToString(), byteCount, objectNumber, operatorIndex, op);
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
        Show(DecodeText(bytes), 0, objectNumber, operatorIndex, op);
    }

    private void Show(string text, long byteCount, int objectNumber, int operatorIndex, string op)
    {
        file.ChargeInterpreted(byteCount);
        if (!inText)
        {
            Warn("text-outside-bt", objectNumber, operatorIndex, op + " outside BT ... ET");
            return;
        }
        var rendering = PdfMatrix.Translation(0, state.Rise).Then(textMatrix).Then(state.Ctm);
        var origin = new Point2(rendering.E, rendering.F);
        if (!rendering.IsFinite)
        {
            Warn("operand-error", objectNumber, operatorIndex, op + " gives a non-finite text position");
            return;
        }
        var height = Math.Abs(state.FontSize) * Math.Sqrt(rendering.C * rendering.C + rendering.D * rendering.D);
        var rotation = PdfGeometry.AngleDegrees(rendering.A, rendering.B);
        elements.Add(new PdfText(Provenance(objectNumber, operatorIndex, op), origin, text, state.FontName, state.FontSize, height, rotation));
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
            if (file.Resolve(PdfFile.Get(form.Dictionary, "Matrix")) is List<object> items && items.Count == 6)
            {
                var values = new double[6];
                var ok = true;
                for (var i = 0; i < 6; i++)
                {
                    if (file.Resolve(items[i]) is PdfNumber number) values[i] = number.Value;
                    else ok = false;
                }
                if (ok) matrix = new PdfMatrix(values[0], values[1], values[2], values[3], values[4], values[5]);
            }
            var savedState = state;
            var savedFloor = stackFloor;
            var savedDepth = stack.Count;
            var savedText = (inText, textMatrix, lineMatrix);
            var savedPath = (segments, subpathStart, current, subpaths.ToArray());
            ResetPath();
            var ctm = matrix.Then(state.Ctm);
            if (ctm.IsFinite) state = state with { Ctm = ctm };
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
            subpaths.Clear();
            subpaths.AddRange(savedPath.Item4);
        }
        finally
        {
            activeForms.Remove(form.ObjectNumber);
        }
    }
}
