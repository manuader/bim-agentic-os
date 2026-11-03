using System.Globalization;
using System.IO.Compression;
using System.Text;
namespace Bim.Cad;

// Reader for the vector content of PDF pages, written with the BCL only. The file is scanned for "n g obj" objects
// (the xref table is not trusted, so damaged offsets and missing tables are tolerated; when an object number occurs
// more than once, as after incremental updates, the occurrence latest in the file wins), compressed object streams
// (/Type /ObjStm) are expanded (an object in a stream counts as occurring at the stream's position), the catalog
// comes from the last trailer or cross-reference stream (or a /Type /Catalog
// object), and the page tree is walked with inherited /Resources, /MediaBox and /Rotate. Each page's content streams
// are then interpreted by PdfContentInterpreter. Only FlateDecode (without predictors) is decoded. Every refusal is a
// PdfFormatException with a stable code; recoverable problems are PdfWarnings; every step is bounded by PdfReadOptions.
public static class PdfReader
{
    public static PdfDocument ReadFile(string path, PdfReadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        options ??= new PdfReadOptions();
        Validate(options);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("PDF file not found", path);
        if (info.Length > options.MaxBytes) throw TooLarge(info.Length, options);
        return Parse(File.ReadAllBytes(path), Path.GetFileName(path), options);
    }

    public static PdfDocument Parse(byte[] bytes, string sourceFile, PdfReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(sourceFile);
        options ??= new PdfReadOptions();
        Validate(options);
        if (bytes.Length > options.MaxBytes) throw TooLarge(bytes.Length, options);
        if (bytes.Length == 0) throw new PdfFormatException("empty", -1, "PDF input is empty");
        var header = Encoding.ASCII.GetBytes("%PDF-");
        var at = bytes.AsSpan(0, Math.Min(bytes.Length, 1024)).IndexOf(header);
        if (at < 0) throw new PdfFormatException("not-pdf", 0, "no %PDF- header in the first 1024 bytes");
        var versionStart = at + header.Length;
        var versionEnd = versionStart;
        while (versionEnd < bytes.Length && versionEnd - versionStart < 8 && ((bytes[versionEnd] >= '0' && bytes[versionEnd] <= '9') || bytes[versionEnd] == '.')) versionEnd++;
        var version = Encoding.ASCII.GetString(bytes, versionStart, versionEnd - versionStart);
        return new PdfFile(bytes, sourceFile, options).Read(version);
    }

    private static void Validate(PdfReadOptions options)
    {
        if (options.MaxBytes < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxBytes must be positive");
        if (options.MaxObjects < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxObjects must be positive");
        if (options.MaxOperators < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxOperators must be positive");
        if (options.MaxDepth < 1 || options.MaxDepth > 256) throw new ArgumentOutOfRangeException(nameof(options), "MaxDepth must be in [1, 256]");
        if (options.MaxDecodedBytes < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxDecodedBytes must be positive");
        if (options.MaxPages < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxPages must be positive");
        if (options.MaxWarnings < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxWarnings must be positive");
        if (options.MaxParsedElements < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxParsedElements must be positive");
        if (options.MaxInterpretedBytes < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxInterpretedBytes must be positive");
    }

    private static PdfFormatException TooLarge(long size, PdfReadOptions options) =>
        new("too-large", -1, "PDF input of " + size.ToString(CultureInfo.InvariantCulture) + " bytes exceeds MaxBytes " + options.MaxBytes.ToString(CultureInfo.InvariantCulture));
}

// One read of one file: object table, page tree, stream decoding, warnings and the shared operator budget.
internal sealed class PdfFile
{
    private readonly byte[] data;
    private readonly Dictionary<int, object> objects = new();
    private readonly Dictionary<int, int> positions = new();
    private readonly List<Dictionary<string, object>> trailers = new();
    private readonly Dictionary<int, byte[]?> decodedCache = new();
    private readonly List<PdfWarning> warnings = new();
    private int objectCount;
    private long decodedTotal;
    private long operatorCount;
    private long interpretedBytes;

    public PdfFile(byte[] data, string sourceFile, PdfReadOptions options)
    {
        this.data = data;
        SourceFile = sourceFile;
        Options = options;
        Budget = new PdfBudget(options.MaxParsedElements);
    }

    public PdfBudget Budget { get; }

    public string SourceFile { get; }
    public PdfReadOptions Options { get; }

    public void Warn(string code, int page, int objectNumber, int operatorIndex, string message)
    {
        if (warnings.Count < Options.MaxWarnings) warnings.Add(new PdfWarning(code, page, objectNumber, operatorIndex, message));
        else if (warnings.Count == Options.MaxWarnings) warnings.Add(new PdfWarning("warning-limit", 0, 0, -1, "further warnings suppressed after MaxWarnings " + Options.MaxWarnings.ToString(CultureInfo.InvariantCulture)));
    }

    public void CountOperator(long offset)
    {
        if (++operatorCount > Options.MaxOperators)
            throw new PdfFormatException("operator-limit", offset, "content operators exceed MaxOperators " + Options.MaxOperators.ToString(CultureInfo.InvariantCulture));
    }

    public void ChargeInterpreted(long bytes)
    {
        interpretedBytes += bytes;
        if (interpretedBytes > Options.MaxInterpretedBytes)
            throw new PdfFormatException("interpret-limit", -1, "interpreted content exceeds MaxInterpretedBytes " + Options.MaxInterpretedBytes.ToString(CultureInfo.InvariantCulture));
    }

    public PdfDocument Read(string version)
    {
        Scan();
        ExpandObjectStreams();
        if (trailers.Any(t => t.ContainsKey("Encrypt")) || objects.Values.OfType<PdfStream>().Any(s => IsName(s.Dictionary, "Type", "XRef") && s.Dictionary.ContainsKey("Encrypt")))
            throw new PdfFormatException("encrypted", -1, "encrypted PDF documents are not supported");
        var catalog = FindCatalog() ?? throw new PdfFormatException("no-pages", -1, "no document catalog found");
        var pagesRoot = catalog.TryGetValue("Pages", out var pagesValue) ? pagesValue : null;
        var leaves = new List<(int Number, Dictionary<string, object> Node, Inherited Attributes)>();
        WalkPages(pagesRoot, new Inherited(null, null, null), 0, new HashSet<object>(ReferenceEqualityComparer.Instance), leaves);
        if (leaves.Count == 0) throw new PdfFormatException("no-pages", -1, "the page tree has no pages");
        var pages = new List<PdfPage>(leaves.Count);
        for (var i = 0; i < leaves.Count; i++)
        {
            var (number, node, inherited) = leaves[i];
            var pageNumber = i + 1;
            var mediaBox = ReadRect(inherited.MediaBox);
            if (mediaBox is null) Warn("mediabox-missing", pageNumber, number, -1, "page has no valid /MediaBox; US Letter assumed");
            var rotate = 0;
            if (Resolve(inherited.Rotate) is PdfNumber r && Math.Abs(r.Value) < 1e9) rotate = (((int)Math.Round(r.Value) % 360) + 360) % 360;
            var interpreter = new PdfContentInterpreter(this, pageNumber);
            var resources = Resolve(inherited.Resources) as Dictionary<string, object>;
            var parts = new List<(int ObjectNumber, byte[] Content)>();
            foreach (var stream in ContentStreams(node, pageNumber, number))
            {
                var content = Decode(stream, pageNumber);
                if (content is not null) parts.Add((stream.ObjectNumber, content));
            }
            interpreter.Run(parts, resources, 0);
            pages.Add(new PdfPage(pageNumber, number, mediaBox ?? new PdfRect(0, 0, 612, 792), rotate, interpreter.Elements));
        }
        return new PdfDocument(SourceFile, version, pages, warnings.ToList());
    }

    private sealed record Inherited(object? Resources, object? MediaBox, object? Rotate);

    // ---------- object scan ----------

    private void Scan()
    {
        var endObjects = FindAll("endobj");
        var endStreams = FindAll("endstream");
        // A trailer dictionary is parsed only up to the next trailer, startxref or obj keyword, so a damaged trailer
        // costs at most its own region and never swallows a later revision.
        var trailerBounds = FindAll("trailer").Concat(FindAll("startxref")).Concat(FindAll("obj")).OrderBy(x => x).ToList();
        var i = 0;
        while (i < data.Length)
        {
            var b = data[i];
            if (b == '%')
            {
                while (i < data.Length && data[i] != 10 && data[i] != 13) i++;
                continue;
            }
            var boundary = i == 0 || !PdfLexer.IsRegular(data[i - 1]);
            if (b == 't' && boundary && Matches(i, "trailer"))
            {
                var bound = NextAtOrAfter(trailerBounds, i + 7);
                var lexer = new PdfLexer(data, i + 7, bound < 0 ? data.Length : bound, Options.MaxDepth, Budget);
                try
                {
                    if (lexer.ReadObject(true) is Dictionary<string, object> trailer) trailers.Add(trailer);
                    i = Math.Max(lexer.Position, i + 7);
                }
                catch (PdfSyntaxException)
                {
                    Warn("trailer-malformed", 0, 0, -1, "malformed trailer at offset " + i.ToString(CultureInfo.InvariantCulture));
                    i += 7;
                }
                continue;
            }
            if (b >= '0' && b <= '9' && boundary && TryObjectHeader(i, out var number, out var bodyStart))
            {
                i = ReadIndirect(number, bodyStart, endObjects, endStreams);
                continue;
            }
            i++;
        }
    }

    private bool Matches(int at, string keyword)
    {
        if (at + keyword.Length > data.Length) return false;
        for (var k = 0; k < keyword.Length; k++) if (data[at + k] != keyword[k]) return false;
        return at + keyword.Length == data.Length || !PdfLexer.IsRegular(data[at + keyword.Length]);
    }

    private List<int> FindAll(string keyword)
    {
        var needle = Encoding.ASCII.GetBytes(keyword);
        var result = new List<int>();
        var from = 0;
        while (from < data.Length)
        {
            var found = data.AsSpan(from).IndexOf(needle);
            if (found < 0) break;
            result.Add(from + found);
            from += found + needle.Length;
        }
        return result;
    }

    private static int NextAtOrAfter(List<int> positions, int at)
    {
        var index = positions.BinarySearch(at);
        if (index < 0) index = ~index;
        return index < positions.Count ? positions[index] : -1;
    }

    // "n g obj" at i: digits, white space, digits, white space, "obj" and a non-regular byte (or the end).
    private bool TryObjectHeader(int i, out int number, out int bodyStart)
    {
        number = 0;
        bodyStart = 0;
        var p = i;
        long value = 0;
        while (p < data.Length && data[p] >= '0' && data[p] <= '9' && p - i < 10) value = value * 10 + (data[p++] - '0');
        if (p == i || value > int.MaxValue || p >= data.Length || !PdfLexer.IsWhite(data[p])) return false;
        while (p < data.Length && PdfLexer.IsWhite(data[p])) p++;
        var genStart = p;
        while (p < data.Length && data[p] >= '0' && data[p] <= '9' && p - genStart < 6) p++;
        if (p == genStart || p >= data.Length || !PdfLexer.IsWhite(data[p])) return false;
        while (p < data.Length && PdfLexer.IsWhite(data[p])) p++;
        if (!Matches(p, "obj")) return false;
        number = (int)value;
        bodyStart = p + 3;
        return true;
    }

    private int ReadIndirect(int number, int bodyStart, List<int> endObjects, List<int> endStreams)
    {
        CountObject(bodyStart);
        var nextEnd = NextAtOrAfter(endObjects, bodyStart);
        var limit = nextEnd < 0 ? data.Length : nextEnd;
        var lexer = new PdfLexer(data, bodyStart, limit, Options.MaxDepth, Budget);
        object? value;
        try
        {
            value = lexer.ReadObject(true);
        }
        catch (PdfSyntaxException error)
        {
            Warn("object-malformed", 0, number, -1, "object " + number.ToString(CultureInfo.InvariantCulture) + ": " + error.Message);
            return Math.Max(limit, bodyStart);
        }
        if (value is Dictionary<string, object> dictionary)
        {
            lexer.SkipWhite();
            if (lexer.AtKeyword("stream"))
            {
                var start = lexer.Position + 6;
                if (start < data.Length && data[start] == 13) start++;
                if (start < data.Length && data[start] == 10) start++;
                var length = -1;
                var after = -1;
                if (dictionary.TryGetValue("Length", out var lengthValue) && lengthValue is PdfNumber { IsInteger: true } declared && declared.Value >= 0 && declared.Value <= data.Length - start)
                {
                    var q = start + (int)declared.Value;
                    var skipped = 0;
                    while (q < data.Length && PdfLexer.IsWhite(data[q]) && skipped < 4) { q++; skipped++; }
                    if (Matches(q, "endstream")) { length = (int)declared.Value; after = q + 9; }
                }
                if (length < 0)
                {
                    var endStream = NextAtOrAfter(endStreams, start);
                    if (endStream < 0)
                    {
                        Warn("stream-unterminated", 0, number, -1, "stream object " + number.ToString(CultureInfo.InvariantCulture) + " has no endstream");
                        return data.Length;
                    }
                    var e = endStream;
                    if (e > start && data[e - 1] == 10) e--;
                    if (e > start && data[e - 1] == 13) e--;
                    length = e - start;
                    after = endStream + 9;
                }
                objects[number] = new PdfStream(dictionary, start, length, number);
                positions[number] = bodyStart;
                return after;
            }
        }
        objects[number] = value ?? PdfNull.Instance;
        positions[number] = bodyStart;
        return Math.Max(lexer.Position, bodyStart);
    }

    private void CountObject(long offset)
    {
        if (++objectCount > Options.MaxObjects)
            throw new PdfFormatException("object-limit", offset, "objects exceed MaxObjects " + Options.MaxObjects.ToString(CultureInfo.InvariantCulture));
    }

    private void ExpandObjectStreams()
    {
        var streams = objects.Values.OfType<PdfStream>().Where(s => IsName(s.Dictionary, "Type", "ObjStm")).OrderBy(s => s.Start).ToList();
        foreach (var stream in streams)
        {
            var decoded = Decode(stream, 0);
            if (decoded is null) continue;
            if (Resolve(Get(stream.Dictionary, "N")) is not PdfNumber { IsInteger: true } n || Resolve(Get(stream.Dictionary, "First")) is not PdfNumber { IsInteger: true } first
                || n.Value < 0 || n.Value > Options.MaxObjects || first.Value < 0 || first.Value > decoded.Length)
            {
                Warn("objstm-malformed", 0, stream.ObjectNumber, -1, "object stream without valid /N and /First");
                continue;
            }
            var count = (int)n.Value;
            var firstOffset = (int)first.Value;
            var header = new PdfLexer(decoded, 0, firstOffset, Options.MaxDepth, Budget);
            var entries = new List<(int Number, int Offset)>();
            var valid = true;
            try
            {
                for (var k = 0; k < count && valid; k++)
                {
                    if (header.ReadObject(false) is not PdfNumber { IsInteger: true } objectNumber || header.ReadObject(false) is not PdfNumber { IsInteger: true } offset
                        || objectNumber.Value < 0 || objectNumber.Value > int.MaxValue || offset.Value < 0 || offset.Value > decoded.Length - firstOffset
                        || (entries.Count > 0 && offset.Value <= entries[^1].Offset))
                    {
                        valid = false;
                        break;
                    }
                    entries.Add(((int)objectNumber.Value, (int)offset.Value));
                }
            }
            catch (PdfSyntaxException)
            {
                valid = false;
            }
            if (!valid)
            {
                Warn("objstm-malformed", 0, stream.ObjectNumber, -1, "object stream header is malformed or its offsets are not strictly increasing");
                continue;
            }
            // Each object is parsed only within [its offset, the next offset), so the work is linear in the stream.
            for (var k = 0; k < entries.Count; k++)
            {
                CountObject(-1);
                var (inner, offset) = entries[k];
                if (positions.TryGetValue(inner, out var seen) && seen > stream.Start) continue;
                var limit = firstOffset + (k + 1 < entries.Count ? entries[k + 1].Offset : decoded.Length - firstOffset);
                var lexer = new PdfLexer(decoded, firstOffset + offset, limit, Options.MaxDepth, Budget);
                try
                {
                    objects[inner] = lexer.ReadObject(true) ?? PdfNull.Instance;
                    positions[inner] = stream.Start;
                }
                catch (PdfSyntaxException error)
                {
                    Warn("object-malformed", 0, inner, -1, "compressed object " + inner.ToString(CultureInfo.InvariantCulture) + ": " + error.Message);
                }
            }
        }
    }

    // ---------- object access ----------

    public object? Resolve(object? value)
    {
        for (var hops = 0; value is PdfRef reference && hops < 32; hops++)
        {
            if (objects.TryGetValue(reference.Number, out var direct)) value = direct;
            else return null;
        }
        return value is PdfRef or PdfNull ? null : value;
    }

    public static object? Get(Dictionary<string, object>? dictionary, string key) => dictionary is not null && dictionary.TryGetValue(key, out var value) ? value : null;

    public bool IsName(Dictionary<string, object> dictionary, string key, string name) => Resolve(Get(dictionary, key)) is PdfName value && value.Value == name;

    private Dictionary<string, object>? FindCatalog()
    {
        var candidates = new List<Dictionary<string, object>>(Enumerable.Reverse(trailers));
        candidates.AddRange(objects.Values.OfType<PdfStream>().Where(s => IsName(s.Dictionary, "Type", "XRef")).OrderByDescending(s => s.Start).Select(s => s.Dictionary));
        foreach (var candidate in candidates)
        {
            if (Resolve(Get(candidate, "Root")) is Dictionary<string, object> root && Resolve(Get(root, "Pages")) is Dictionary<string, object>) return root;
        }
        foreach (var number in objects.Keys.OrderBy(k => k).ToList())
        {
            if (Resolve(new PdfRef(number, 0)) is Dictionary<string, object> dictionary && IsName(dictionary, "Type", "Catalog") && Resolve(Get(dictionary, "Pages")) is Dictionary<string, object>) return dictionary;
        }
        return null;
    }

    // Each node dictionary is walked at most once, by reference identity, so shared or cyclic /Kids (indirect or direct)
    // cost linear work.
    private void WalkPages(object? nodeValue, Inherited inherited, int depth, HashSet<object> visited, List<(int, Dictionary<string, object>, Inherited)> leaves)
    {
        var number = nodeValue is PdfRef reference ? reference.Number : 0;
        if (depth > Options.MaxDepth) throw new PdfFormatException("depth-limit", -1, "page tree deeper than MaxDepth " + Options.MaxDepth.ToString(CultureInfo.InvariantCulture));
        if (Resolve(nodeValue) is not Dictionary<string, object> node)
        {
            Warn("page-missing", 0, number, -1, "page tree node is missing or not a dictionary");
            return;
        }
        if (!visited.Add(node))
        {
            Warn("page-tree-cycle", 0, number, -1, "page tree node " + number.ToString(CultureInfo.InvariantCulture) + " is visited twice");
            return;
        }
        var attributes = new Inherited(Get(node, "Resources") ?? inherited.Resources, Get(node, "MediaBox") ?? inherited.MediaBox, Get(node, "Rotate") ?? inherited.Rotate);
        var kids = Resolve(Get(node, "Kids")) as List<object>;
        if (IsName(node, "Type", "Pages") || (!IsName(node, "Type", "Page") && kids is not null))
        {
            foreach (var kid in kids ?? new List<object>()) WalkPages(kid, attributes, depth + 1, visited, leaves);
            return;
        }
        leaves.Add((number, node, attributes));
        if (leaves.Count > Options.MaxPages) throw new PdfFormatException("page-limit", -1, "pages exceed MaxPages " + Options.MaxPages.ToString(CultureInfo.InvariantCulture));
    }

    private IEnumerable<PdfStream> ContentStreams(Dictionary<string, object> page, int pageNumber, int pageObject)
    {
        var contents = Get(page, "Contents");
        var items = Resolve(contents) is List<object> list ? list : contents is null ? new List<object>() : new List<object> { contents };
        foreach (var item in items)
        {
            if (Resolve(item) is PdfStream stream) yield return stream;
            else Warn("content-missing", pageNumber, pageObject, -1, "page /Contents entry is not a stream");
        }
    }

    private PdfRect? ReadRect(object? value)
    {
        if (Resolve(value) is not List<object> items || items.Count != 4) return null;
        var numbers = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (Resolve(items[i]) is not PdfNumber n) return null;
            numbers[i] = n.Value;
        }
        return new PdfRect(Math.Min(numbers[0], numbers[2]), Math.Min(numbers[1], numbers[3]), Math.Max(numbers[0], numbers[2]), Math.Max(numbers[1], numbers[3]));
    }

    // ---------- stream decoding ----------

    // The decoded data of a stream, or null (with a warning) when its filter is unsupported or its data is corrupt.
    public byte[]? Decode(PdfStream stream, int page)
    {
        if (decodedCache.TryGetValue(stream.ObjectNumber, out var cached)) return cached;
        var result = DecodeUncached(stream, page);
        decodedCache[stream.ObjectNumber] = result;
        return result;
    }

    private byte[]? DecodeUncached(PdfStream stream, int page)
    {
        var filterValue = Resolve(Get(stream.Dictionary, "Filter"));
        var filters = new List<string>();
        if (filterValue is PdfName single) filters.Add(single.Value);
        else if (filterValue is List<object> list)
        {
            foreach (var item in list)
            {
                if (Resolve(item) is PdfName name) filters.Add(name.Value);
                else { Warn("unsupported-filter", page, stream.ObjectNumber, -1, "filter entry is not a name"); return null; }
            }
        }
        else if (filterValue is not null) { Warn("unsupported-filter", page, stream.ObjectNumber, -1, "/Filter is not a name or array"); return null; }
        var bytes = data.AsSpan(stream.Start, stream.Length).ToArray();
        for (var k = 0; k < filters.Count; k++)
        {
            if (filters[k] != "FlateDecode" && filters[k] != "Fl")
            {
                Warn("unsupported-filter", page, stream.ObjectNumber, -1, "filter " + filters[k] + " is not supported");
                return null;
            }
            var parms = Resolve(Get(stream.Dictionary, "DecodeParms"));
            if (parms is List<object> parmList) parms = k < parmList.Count ? Resolve(parmList[k]) : null;
            if (parms is Dictionary<string, object> parmDictionary && Resolve(Get(parmDictionary, "Predictor")) is PdfNumber predictor && predictor.Value > 1)
            {
                Warn("unsupported-filter", page, stream.ObjectNumber, -1, "FlateDecode predictors are not supported");
                return null;
            }
            var inflated = Inflate(bytes, stream, page);
            if (inflated is null) return null;
            bytes = inflated;
        }
        return bytes;
    }

    private byte[]? Inflate(byte[] input, PdfStream stream, int page)
    {
        try
        {
            using var source = new MemoryStream(input, false);
            using var zlib = new ZLibStream(source, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = zlib.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                decodedTotal += read;
                if (decodedTotal > Options.MaxDecodedBytes)
                    throw new PdfFormatException("decode-limit", stream.Start, "decoded stream data exceeds MaxDecodedBytes " + Options.MaxDecodedBytes.ToString(CultureInfo.InvariantCulture));
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (Exception error) when (error is not PdfFormatException)
        {
            Warn("stream-decode-failed", page, stream.ObjectNumber, -1, "FlateDecode failed: " + error.Message);
            return null;
        }
    }
}
