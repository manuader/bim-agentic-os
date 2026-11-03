using System.Globalization;
using System.Text;
namespace Bim.Cad;

// PDF object values as read by PdfLexer: bool, PdfNumber, PdfString, PdfName, List<object> (array),
// Dictionary<string, object> (dictionary, keys without the slash), PdfRef, PdfNull, PdfKeyword (content operators and
// stray tokens) and, for indirect objects only, PdfStream.
internal sealed record PdfNumber(double Value, bool IsInteger);

internal sealed record PdfString(byte[] Bytes);

internal sealed record PdfName(string Value);

internal sealed record PdfKeyword(string Value);

internal readonly record struct PdfRef(int Number, int Generation);

internal sealed class PdfNull
{
    public static readonly PdfNull Instance = new();

    private PdfNull() { }
}

// A stream object: its dictionary and the location of its raw (still encoded) data in the file.
internal sealed record PdfStream(Dictionary<string, object> Dictionary, int Start, int Length, int ObjectNumber);

// The document-wide budget of parsed elements (names, array items and dictionary entries) shared by every lexer of one
// read, so huge arrays or dictionaries are refused (PdfFormatException "element-limit") before they are allocated.
internal sealed class PdfBudget
{
    private readonly long limit;
    private long used;

    public PdfBudget(long limit)
    {
        this.limit = limit;
    }

    public void Charge(int offset)
    {
        if (++used > limit) throw new PdfFormatException("element-limit", offset, "parsed names, array items and dictionary entries exceed MaxParsedElements " + limit.ToString(CultureInfo.InvariantCulture));
    }
}

// A recoverable syntax error inside one object or content stream.
internal sealed class PdfSyntaxException : Exception
{
    public PdfSyntaxException(string message, int offset) : base(message)
    {
        Offset = offset;
    }

    public int Offset { get; }
}

// Tokenizer and object parser over data[start, end). Every loop advances Position, nesting is bounded by maxDepth
// (PdfFormatException "depth-limit"), and running out of input inside an object is a PdfSyntaxException.
internal sealed class PdfLexer
{
    private readonly byte[] data;
    private readonly int end;
    private readonly int maxDepth;
    private readonly PdfBudget? budget;

    public PdfLexer(byte[] data, int start, int end, int maxDepth, PdfBudget? budget = null)
    {
        this.data = data;
        this.budget = budget;
        this.end = Math.Min(end, data.Length);
        this.maxDepth = maxDepth;
        Position = Math.Clamp(start, 0, this.end);
    }

    public int Position { get; set; }

    public int End => end;

    public static bool IsWhite(byte b) => b == 0 || b == 9 || b == 10 || b == 12 || b == 13 || b == 32;

    public static bool IsDelimiter(byte b) => b == '(' || b == ')' || b == '<' || b == '>' || b == '[' || b == ']' || b == '{' || b == '}' || b == '/' || b == '%';

    public static bool IsRegular(byte b) => !IsWhite(b) && !IsDelimiter(b);

    public void SkipWhite()
    {
        while (Position < end)
        {
            var b = data[Position];
            if (IsWhite(b)) { Position++; continue; }
            if (b != '%') return;
            while (Position < end && data[Position] != 10 && data[Position] != 13) Position++;
        }
    }

    // True when the bytes at Position spell keyword followed by a non-regular byte or the end.
    public bool AtKeyword(string keyword)
    {
        if (Position + keyword.Length > end) return false;
        for (var i = 0; i < keyword.Length; i++) if (data[Position + i] != keyword[i]) return false;
        return Position + keyword.Length == end || !IsRegular(data[Position + keyword.Length]);
    }

    // Reads one object; null at the end of the input. allowRefs turns "n g R" into a PdfRef (file objects only).
    public object? ReadObject(bool allowRefs, int depth = 0)
    {
        SkipWhite();
        if (Position >= end) return null;
        var b = data[Position];
        switch (b)
        {
            case (byte)'/':
                return ReadName();
            case (byte)'(':
                return ReadLiteral();
            case (byte)'[':
                return ReadArray(allowRefs, depth + 1);
            case (byte)'<':
                if (Position + 1 < end && data[Position + 1] == '<') return ReadDictionary(allowRefs, depth + 1);
                return ReadHex();
            case (byte)')':
            case (byte)']':
            case (byte)'>':
            case (byte)'{':
            case (byte)'}':
                Position++;
                return new PdfKeyword(((char)b).ToString());
        }
        var start = Position;
        while (Position < end && IsRegular(data[Position])) Position++;
        var length = Position - start;
        if ((b >= '0' && b <= '9') || b == '+' || b == '-' || b == '.')
        {
            var number = ParseNumber(start, length);
            if (number is not null)
            {
                if (allowRefs && number.IsInteger && number.Value >= 0 && number.Value <= int.MaxValue)
                {
                    var save = Position;
                    if (TryReferenceTail(out var generation)) return new PdfRef((int)number.Value, generation);
                    Position = save;
                }
                return number;
            }
        }
        if (length > 256) throw new PdfSyntaxException("token too long", start);
        var token = Encoding.Latin1.GetString(data, start, length);
        return token switch
        {
            "true" => true,
            "false" => false,
            "null" => PdfNull.Instance,
            _ => new PdfKeyword(token),
        };
    }

    private static readonly PdfNumber[] SmallIntegers = Enumerable.Range(0, 1024).Select(i => new PdfNumber(i, true)).ToArray();

    private PdfNumber? ParseNumber(int start, int length)
    {
        if (length == 0 || length > 64) return null;
        // Fast path for short integers (the bulk of real content streams): no string allocation.
        var i = start;
        var negative = data[i] == '-';
        if (negative || data[i] == '+') i++;
        if (i < start + length && start + length - i <= 9)
        {
            var value = 0;
            var digits = true;
            for (var k = i; k < start + length; k++)
            {
                var c = data[k];
                if (c < '0' || c > '9') { digits = false; break; }
                value = value * 10 + (c - '0');
            }
            if (digits)
            {
                if (!negative && value < SmallIntegers.Length) return SmallIntegers[value];
                return new PdfNumber(negative ? -value : value, true);
            }
        }
        var text = Encoding.ASCII.GetString(data, start, length);
        if (!double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed)) return null;
        return new PdfNumber(parsed, text.IndexOf('.', StringComparison.Ordinal) < 0);
    }

    private bool TryReferenceTail(out int generation)
    {
        generation = 0;
        SkipWhite();
        var start = Position;
        while (Position < end && data[Position] >= '0' && data[Position] <= '9' && Position - start < 9)
        {
            generation = generation * 10 + (data[Position] - '0');
            Position++;
        }
        if (Position == start || (Position < end && IsRegular(data[Position]))) return false;
        SkipWhite();
        if (Position < end && data[Position] == 'R' && (Position + 1 >= end || !IsRegular(data[Position + 1])))
        {
            Position++;
            return true;
        }
        return false;
    }

    private PdfName ReadName()
    {
        budget?.Charge(Position);
        Position++;
        var start = Position;
        var bytes = new List<byte>();
        while (Position < end && IsRegular(data[Position]))
        {
            if (Position - start > 256) throw new PdfSyntaxException("name too long", start);
            var b = data[Position++];
            if (b == '#' && Position + 1 < end && Hex(data[Position]) >= 0 && Hex(data[Position + 1]) >= 0)
            {
                bytes.Add((byte)(Hex(data[Position]) * 16 + Hex(data[Position + 1])));
                Position += 2;
                continue;
            }
            bytes.Add(b);
        }
        if (Position - start > 256) throw new PdfSyntaxException("name too long", start);
        return new PdfName(Encoding.Latin1.GetString(bytes.ToArray()));
    }

    private static int Hex(byte b) => b >= '0' && b <= '9' ? b - '0' : b >= 'a' && b <= 'f' ? b - 'a' + 10 : b >= 'A' && b <= 'F' ? b - 'A' + 10 : -1;

    private PdfString ReadLiteral()
    {
        var start = Position;
        Position++;
        var bytes = new List<byte>();
        var nesting = 1;
        while (true)
        {
            if (Position >= end) throw new PdfSyntaxException("unterminated literal string", start);
            var b = data[Position++];
            if (b == '\\')
            {
                if (Position >= end) throw new PdfSyntaxException("unterminated literal string", start);
                var escaped = data[Position++];
                switch (escaped)
                {
                    case (byte)'n': bytes.Add(10); break;
                    case (byte)'r': bytes.Add(13); break;
                    case (byte)'t': bytes.Add(9); break;
                    case (byte)'b': bytes.Add(8); break;
                    case (byte)'f': bytes.Add(12); break;
                    case 13:
                        if (Position < end && data[Position] == 10) Position++;
                        break;
                    case 10:
                        break;
                    default:
                        if (escaped >= '0' && escaped <= '7')
                        {
                            var value = escaped - '0';
                            for (var k = 0; k < 2 && Position < end && data[Position] >= '0' && data[Position] <= '7'; k++) value = value * 8 + (data[Position++] - '0');
                            bytes.Add((byte)(value & 0xFF));
                        }
                        else
                        {
                            bytes.Add(escaped);
                        }
                        break;
                }
                continue;
            }
            if (b == '(') nesting++;
            else if (b == ')' && --nesting == 0) break;
            bytes.Add(b);
        }
        return new PdfString(bytes.ToArray());
    }

    private PdfString ReadHex()
    {
        var start = Position;
        Position++;
        var bytes = new List<byte>();
        var high = -1;
        while (true)
        {
            if (Position >= end) throw new PdfSyntaxException("unterminated hex string", start);
            var b = data[Position++];
            if (b == '>') break;
            if (IsWhite(b)) continue;
            var digit = Hex(b);
            if (digit < 0) throw new PdfSyntaxException("invalid hex string", Position - 1);
            if (high < 0) { high = digit; continue; }
            bytes.Add((byte)(high * 16 + digit));
            high = -1;
        }
        if (high >= 0) bytes.Add((byte)(high * 16));
        return new PdfString(bytes.ToArray());
    }

    private void CheckDepth(int depth)
    {
        if (depth > maxDepth) throw new PdfFormatException("depth-limit", Position, "object nesting exceeds MaxDepth " + maxDepth.ToString(CultureInfo.InvariantCulture));
    }

    private List<object> ReadArray(bool allowRefs, int depth)
    {
        CheckDepth(depth);
        var start = Position;
        Position++;
        var items = new List<object>();
        while (true)
        {
            SkipWhite();
            if (Position >= end) throw new PdfSyntaxException("unterminated array", start);
            if (data[Position] == ']') { Position++; return items; }
            budget?.Charge(Position);
            var item = ReadObject(allowRefs, depth) ?? throw new PdfSyntaxException("unterminated array", start);
            items.Add(item);
        }
    }

    private Dictionary<string, object> ReadDictionary(bool allowRefs, int depth)
    {
        CheckDepth(depth);
        var start = Position;
        Position += 2;
        var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
        while (true)
        {
            SkipWhite();
            if (Position >= end) throw new PdfSyntaxException("unterminated dictionary", start);
            if (data[Position] == '>')
            {
                if (Position + 1 < end && data[Position + 1] == '>') { Position += 2; return dictionary; }
                throw new PdfSyntaxException("malformed dictionary end", Position);
            }
            budget?.Charge(Position);
            if (ReadObject(allowRefs, depth) is not PdfName key) throw new PdfSyntaxException("dictionary key is not a name", Position);
            SkipWhite();
            if (Position + 1 < end && data[Position] == '>' && data[Position + 1] == '>') { Position += 2; return dictionary; }
            var value = ReadObject(allowRefs, depth) ?? throw new PdfSyntaxException("unterminated dictionary", start);
            dictionary[key.Value] = value;
        }
    }

    // After an inline image's ID operator: skips one white-space byte and the image data up to an EI that is preceded by
    // white space and followed by white space or the end. False when no such EI exists (Position is then the end).
    public bool SkipInlineImageData()
    {
        if (Position < end && IsWhite(data[Position])) Position++;
        for (var i = Position; i + 1 < end; i++)
        {
            if (data[i] != 'E' || data[i + 1] != 'I') continue;
            if (i > 0 && !IsWhite(data[i - 1]) && i != Position) continue;
            if (i + 2 < end && !IsWhite(data[i + 2])) continue;
            Position = i + 2;
            return true;
        }
        Position = end;
        return false;
    }
}
