using System.Globalization;
using System.Text;
namespace Bim.Cad;

// Reader for ASCII DXF written with the BCL only: the file is split into lines (LF, CRLF or CR), read as
// (group code, value) pairs up to "0 EOF", grouped into sections and code-0 records, and turned into the drawing model.
// It reads $INSUNITS from HEADER, the LAYER table from TABLES, BLOCKS and ENTITIES; other sections are skipped.
// Every refusal is a DxfFormatException with a stable code; every step is linear in the input, so it never hangs.
// Text is decoded as UTF-8 (AutoCAD 2007+), falling back to Latin-1 when the bytes are not valid UTF-8.
public static class DxfReader
{
    private const string BinarySentinel = "AutoCAD Binary DXF";

    public static DxfDrawing ReadFile(string path, DxfReadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        options ??= new DxfReadOptions();
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("DXF file not found", path);
        if (info.Length > options.MaxBytes) throw TooLarge(info.Length, options);
        return Parse(File.ReadAllBytes(path), Path.GetFileName(path), options);
    }

    public static DxfDrawing Parse(byte[] bytes, string sourceFile, DxfReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(sourceFile);
        options ??= new DxfReadOptions();
        if (bytes.Length > options.MaxBytes) throw TooLarge(bytes.Length, options);
        if (bytes.Length == 0) throw new DxfFormatException("empty", 0, "DXF input is empty");
        var sentinel = Encoding.ASCII.GetBytes(BinarySentinel);
        if (bytes.AsSpan().StartsWith(sentinel) || Array.IndexOf(bytes, (byte)0) >= 0)
            throw new DxfFormatException("binary-dxf", 0, "binary DXF (or binary data) is not supported; save the drawing as ASCII DXF");
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(bytes, offset, bytes.Length - offset); }
        return ParseText(text, sourceFile);
    }

    public static DxfDrawing Parse(string text, string sourceFile, DxfReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sourceFile);
        options ??= new DxfReadOptions();
        long size = Encoding.UTF8.GetByteCount(text);
        if (size > options.MaxBytes) throw TooLarge(size, options);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        if (text.StartsWith(BinarySentinel, StringComparison.Ordinal) || text.Contains('\0', StringComparison.Ordinal))
            throw new DxfFormatException("binary-dxf", 0, "binary DXF (or binary data) is not supported; save the drawing as ASCII DXF");
        return ParseText(text, sourceFile);
    }

    private static DxfFormatException TooLarge(long size, DxfReadOptions options) =>
        new("too-large", 0, "DXF input of " + size.ToString(CultureInfo.InvariantCulture) + " bytes exceeds the limit of " + options.MaxBytes.ToString(CultureInfo.InvariantCulture));

    private static DxfDrawing ParseText(string text, string sourceFile)
    {
        if (text.AsSpan().Trim().IsEmpty) throw new DxfFormatException("empty", 0, "DXF input is empty");
        var pairs = Tokenize(text);
        var state = new ParseState(sourceFile);
        var i = 0;
        var strayReported = false;
        while (true)
        {
            if (i >= pairs.Count) throw new DxfFormatException("missing-eof", pairs.Count > 0 ? pairs[^1].Line + 1 : 0, "DXF ends without 0/EOF (truncated file)");
            var pair = pairs[i];
            if (pair.Code == 0 && pair.Name == "EOF") break;
            if (pair.Code == 0 && pair.Name == "SECTION")
            {
                if (i + 1 >= pairs.Count || pairs[i + 1].Code != 2)
                    throw new DxfFormatException("malformed-structure", pair.Line, "SECTION is not followed by a group 2 section name");
                var name = pairs[i + 1].Name;
                var section = ReadSection(pairs, i + 2, pair.Line, name);
                i = section.Next;
                switch (name)
                {
                    case "HEADER": state.ReadHeader(section.Leading); break;
                    case "TABLES": state.ReadTables(section.Records); break;
                    case "BLOCKS": state.ReadBlocks(section.Records); break;
                    case "ENTITIES": state.ReadEntities(section.Records); break;
                    default: break;
                }
                continue;
            }
            if (!strayReported)
            {
                state.Warn("stray-group", pair.Line, "group outside any SECTION ignored");
                strayReported = true;
            }
            i++;
        }
        return state.Build();
    }

    // Lines are split at LF, CRLF or CR. Pairs are read until "0 EOF"; anything after it is ignored.
    private static List<DxfPair> Tokenize(string text)
    {
        var pairs = new List<DxfPair>();
        var position = 0;
        var lineNumber = 0;
        string? NextLine()
        {
            if (position >= text.Length) return null;
            var start = position;
            while (position < text.Length && text[position] != '\n' && text[position] != '\r') position++;
            var line = text[start..position];
            if (position < text.Length)
            {
                if (text[position] == '\r' && position + 1 < text.Length && text[position + 1] == '\n') position += 2;
                else position++;
            }
            lineNumber++;
            return line;
        }
        while (true)
        {
            var codeLine = NextLine();
            if (codeLine is null) break;
            var codeLineNumber = lineNumber;
            if (!int.TryParse(codeLine.AsSpan().Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code) || code < -5 || code > 1071)
                throw new DxfFormatException("malformed-group-code", codeLineNumber, "group code line is not an integer DXF group code in -5..1071");
            var value = NextLine() ?? throw new DxfFormatException("missing-value", codeLineNumber, "group code without a value line (truncated file)");
            var pair = new DxfPair(code, value, codeLineNumber);
            pairs.Add(pair);
            if (code == 0 && pair.Name == "EOF") break;
        }
        return pairs;
    }

    private static Section ReadSection(List<DxfPair> pairs, int start, int sectionLine, string name)
    {
        var leading = new List<DxfPair>();
        var records = new List<DxfRecord>();
        DxfRecord? current = null;
        for (var i = start; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            if (pair.Code == 0)
            {
                var type = pair.Name;
                if (type == "ENDSEC") return new Section(leading, records, i + 1);
                if (type == "EOF" || type == "SECTION")
                    throw new DxfFormatException("unterminated-section", pair.Line, "section " + name + " starting at line " + sectionLine.ToString(CultureInfo.InvariantCulture) + " has no ENDSEC");
                current = new DxfRecord(type, pair.Line, new List<DxfPair>());
                records.Add(current);
            }
            else if (current is null) leading.Add(pair);
            else current.Groups.Add(pair);
        }
        throw new DxfFormatException("missing-eof", pairs.Count > 0 ? pairs[^1].Line + 1 : 0, "DXF ends inside section " + name + " (truncated file)");
    }

    private sealed record Section(List<DxfPair> Leading, List<DxfRecord> Records, int Next);

    private sealed class ParseState(string sourceFile)
    {
        private readonly List<DxfWarning> warnings = new();
        private readonly List<DxfEntity> entities = new();
        private readonly List<DxfProvenance> unsupported = new();
        private readonly Dictionary<string, DxfLayer> layers = new(StringComparer.Ordinal);
        private readonly SortedSet<string> usedLayers = new(StringComparer.Ordinal);
        private readonly List<DxfBlock> blocks = new();
        private readonly HashSet<string> blockNames = new(StringComparer.OrdinalIgnoreCase);
        private int? insUnits;

        public void Warn(string code, int line, string message) => warnings.Add(new DxfWarning(code, line, message));

        public void ReadHeader(List<DxfPair> groups)
        {
            for (var k = 0; k < groups.Count; k++)
            {
                if (groups[k].Code != 9 || groups[k].Name != "$INSUNITS") continue;
                if (k + 1 < groups.Count && groups[k + 1].Code == 70) insUnits = DxfValues.Int(groups[k + 1]);
                else Warn("header-invalid", groups[k].Line, "$INSUNITS without a group 70 value");
            }
        }

        public void ReadTables(List<DxfRecord> records)
        {
            string? table = null;
            foreach (var record in records)
            {
                switch (record.Type)
                {
                    case "TABLE": table = record.First(2)?.Name; break;
                    case "ENDTAB": table = null; break;
                    case "LAYER" when table == "LAYER": ReadLayer(record); break;
                    default: break;
                }
            }
        }

        private void ReadLayer(DxfRecord record)
        {
            var name = record.First(2)?.Value.Trim() ?? "";
            if (name.Length == 0) { Warn("layer-invalid", record.Line, "LAYER entry without a name ignored"); return; }
            var color = record.First(62) is { } c ? DxfValues.Int(c) : 7;
            var flags = record.First(70) is { } f ? DxfValues.Int(f) : 0;
            var lineType = record.First(6)?.Value.Trim() ?? "";
            if (!layers.TryAdd(name, new DxfLayer(name, color == int.MinValue ? int.MaxValue : Math.Abs(color), lineType, (flags & 1) != 0, color < 0, true)))
                Warn("duplicate-layer", record.Line, "duplicate LAYER entry " + name + " ignored");
        }

        public void ReadBlocks(List<DxfRecord> records)
        {
            BlockBuilder? current = null;
            foreach (var record in records)
            {
                if (record.Type == "BLOCK")
                {
                    if (current is not null) { Warn("block-unterminated", current.Line, "BLOCK " + current.Name + " has no ENDBLK"); Finish(current); }
                    var name = (record.First(2) ?? record.First(3))?.Value.Trim() ?? "";
                    var x = record.First(10) is { } bx ? DxfValues.Number(bx) : 0;
                    var y = record.First(20) is { } by ? DxfValues.Number(by) : 0;
                    current = new BlockBuilder(name, new Point2(x, y), record.First(5)?.Value.Trim(), record.Line);
                }
                else if (record.Type == "ENDBLK")
                {
                    if (current is null) Warn("stray-endblk", record.Line, "ENDBLK without BLOCK ignored");
                    else { Finish(current); current = null; }
                }
                else if (current is null) Warn("entity-outside-block", record.Line, record.Type + " outside BLOCK/ENDBLK ignored");
                else if (Build(record) is { } entity) current.Entities.Add(entity);
            }
            if (current is not null) { Warn("block-unterminated", current.Line, "BLOCK " + current.Name + " has no ENDBLK"); Finish(current); }
        }

        private void Finish(BlockBuilder block)
        {
            if (block.Name.Length == 0) { Warn("block-invalid", block.Line, "BLOCK without a name ignored"); return; }
            if (!blockNames.Add(block.Name)) { Warn("duplicate-block", block.Line, "duplicate BLOCK " + block.Name + " ignored"); return; }
            blocks.Add(new DxfBlock(block.Name, block.BasePoint, block.Handle, block.Line, block.Entities.ToArray()));
        }

        public void ReadEntities(List<DxfRecord> records)
        {
            foreach (var record in records)
            {
                if (Build(record) is { } entity) entities.Add(entity);
            }
        }

        private DxfEntity? Build(DxfRecord record)
        {
            var layerGroup = record.First(8)?.Value.Trim();
            var layer = string.IsNullOrEmpty(layerGroup) ? "0" : layerGroup;
            var provenance = new DxfProvenance(sourceFile, record.First(5)?.Value.Trim(), layer, record.Type, record.Line);
            if (record.Type is "VERTEX" or "SEQEND") return null;
            usedLayers.Add(layer);
            var entity = DxfEntityBuilder.Build(record, provenance, this);
            if (entity is null && !DxfEntityBuilder.IsModelled(record.Type)) unsupported.Add(provenance);
            return entity;
        }

        public DxfDrawing Build()
        {
            foreach (var name in usedLayers)
            {
                if (!layers.ContainsKey(name)) layers[name] = new DxfLayer(name, 7, "", false, false, false);
            }
            var units = DxfUnits.Unspecified;
            double? metres = null;
            if (insUnits is { } code)
            {
                if (code >= 0 && code <= 21)
                {
                    units = (DxfUnits)code;
                    metres = DxfValues.MetresPerUnit(units);
                }
                else
                {
                    units = DxfUnits.Unknown;
                    Warn("units-unknown", 0, "$INSUNITS " + code.ToString(CultureInfo.InvariantCulture) + " is not a known unit code");
                }
            }
            return new DxfDrawing(sourceFile, insUnits, units, metres,
                layers.Values.OrderBy(l => l.Name, StringComparer.Ordinal).ToArray(),
                blocks.OrderBy(b => b.Name, StringComparer.Ordinal).ToArray(),
                entities.ToArray(), unsupported.ToArray(), warnings.ToArray());
        }
    }

    private sealed class BlockBuilder(string name, Point2 basePoint, string? handle, int line)
    {
        public string Name { get; } = name;
        public Point2 BasePoint { get; } = basePoint;
        public string? Handle { get; } = handle;
        public int Line { get; } = line;
        public List<DxfEntity> Entities { get; } = new();
    }

    private static class DxfEntityBuilder
    {
        public static bool IsModelled(string type) => type is "LINE" or "ARC" or "CIRCLE" or "LWPOLYLINE" or "TEXT" or "MTEXT" or "INSERT";

        public static DxfEntity? Build(DxfRecord r, DxfProvenance p, ParseState state)
        {
            switch (r.Type)
            {
                case "LINE":
                    {
                        if (r.First(10) is not { } x1 || r.First(20) is not { } y1 || r.First(11) is not { } x2 || r.First(21) is not { } y2) return Invalid(r, state, "LINE without both end points");
                        return new DxfLine(p, new Point2(DxfValues.Number(x1), DxfValues.Number(y1)), new Point2(DxfValues.Number(x2), DxfValues.Number(y2)));
                    }
                case "CIRCLE":
                case "ARC":
                    {
                        if (r.First(10) is not { } cx || r.First(20) is not { } cy || r.First(40) is not { } rr) return Invalid(r, state, r.Type + " without centre or radius");
                        var center = new Point2(DxfValues.Number(cx), DxfValues.Number(cy));
                        var radius = DxfValues.Number(rr);
                        if (!(radius > 0)) return Invalid(r, state, r.Type + " radius must be positive");
                        var mirror = Ocs(r, p, state);
                        if (r.Type == "CIRCLE") return new DxfCircle(p, mirror ? Flip(center) : center, radius);
                        if (r.First(50) is not { } sa || r.First(51) is not { } ea) return Invalid(r, state, "ARC without start or end angle");
                        double start = DxfValues.Number(sa), end = DxfValues.Number(ea);
                        if (mirror) (start, end) = (180 - end, 180 - start);
                        return new DxfArc(p, mirror ? Flip(center) : center, radius, DxfValues.NormaliseDegrees(start), DxfValues.NormaliseDegrees(end));
                    }
                case "LWPOLYLINE":
                    {
                        var vertices = new List<DxfVertex>();
                        double x = 0, y = 0, bulge = 0;
                        var open = false;
                        int? declared = null;
                        var flags = 0;
                        foreach (var g in r.Groups)
                        {
                            switch (g.Code)
                            {
                                case 10:
                                    if (open) vertices.Add(new DxfVertex(new Point2(x, y), bulge));
                                    x = DxfValues.Number(g); y = 0; bulge = 0; open = true;
                                    break;
                                case 20: if (open) y = DxfValues.Number(g); break;
                                case 42: if (open) bulge = DxfValues.Number(g); break;
                                case 70: flags = DxfValues.Int(g); break;
                                case 90: declared = DxfValues.Int(g); break;
                                default: break;
                            }
                        }
                        if (open) vertices.Add(new DxfVertex(new Point2(x, y), bulge));
                        if (vertices.Count < 2) return Invalid(r, state, "LWPOLYLINE needs at least two vertices");
                        if (declared is { } count && count != vertices.Count)
                            state.Warn("vertex-count-mismatch", r.Line, "LWPOLYLINE declares " + count.ToString(CultureInfo.InvariantCulture) + " vertices but has " + vertices.Count.ToString(CultureInfo.InvariantCulture));
                        if (Ocs(r, p, state)) vertices = vertices.Select(v => new DxfVertex(Flip(v.Point), -v.Bulge)).ToList();
                        return new DxfPolyline(p, vertices.ToArray(), (flags & 1) != 0);
                    }
                case "TEXT":
                case "MTEXT":
                    {
                        var mtext = r.Type == "MTEXT";
                        if (r.First(10) is not { } tx || r.First(20) is not { } ty) return Invalid(r, state, r.Type + " without insertion point");
                        var insertion = new Point2(DxfValues.Number(tx), DxfValues.Number(ty));
                        var height = r.First(40) is { } h ? DxfValues.Number(h) : 0;
                        double rotation = 0;
                        if (r.First(50) is { } rot) rotation = DxfValues.Number(rot);
                        else if (mtext && r.First(11) is { } dx && r.First(21) is { } dy) rotation = Math.Atan2(DxfValues.Number(dy), DxfValues.Number(dx)) * 180 / Math.PI;
                        string value;
                        if (mtext)
                        {
                            var builder = new StringBuilder();
                            foreach (var g in r.Groups) if (g.Code == 3) builder.Append(g.Value);
                            builder.Append(r.First(1)?.Value ?? "");
                            value = builder.ToString();
                        }
                        else value = r.First(1)?.Value ?? "";
                        // MTEXT 10/20 is in world coordinates; TEXT is in the entity's OCS.
                        if (!mtext && Ocs(r, p, state)) { insertion = Flip(insertion); rotation = 180 - rotation; }
                        return new DxfText(p, insertion, height, DxfValues.NormaliseDegrees(rotation), value, mtext ? DxfTextFormat.PlainMText(value) : DxfTextFormat.PlainText(value), mtext);
                    }
                case "INSERT":
                    {
                        var name = r.First(2)?.Value.Trim() ?? "";
                        if (name.Length == 0) return Invalid(r, state, "INSERT without a block name");
                        var insertion = new Point2(r.First(10) is { } ix ? DxfValues.Number(ix) : 0, r.First(20) is { } iy ? DxfValues.Number(iy) : 0);
                        var sx = r.First(41) is { } gx ? DxfValues.Number(gx) : 1;
                        var sy = r.First(42) is { } gy ? DxfValues.Number(gy) : 1;
                        var rotation = r.First(50) is { } gr ? DxfValues.Number(gr) : 0;
                        if (sx == 0 || sy == 0) return Invalid(r, state, "INSERT with a zero scale factor");
                        if ((r.First(70) is { } cols && DxfValues.Int(cols) > 1) || (r.First(71) is { } rows && DxfValues.Int(rows) > 1))
                            state.Warn("minsert-ignored", r.Line, "INSERT column/row array read as a single insert");
                        if (Ocs(r, p, state)) { insertion = Flip(insertion); rotation = -rotation; sx = -sx; }
                        return new DxfInsert(p, name, insertion, sx, sy, DxfValues.NormaliseDegrees(rotation));
                    }
                default:
                    return null;
            }
        }

        private static Point2 Flip(Point2 p) => new(-p.X, p.Y);

        // True when the extrusion direction is (0,0,-1): the OCS is the world plane mirrored in X. Other tilted
        // extrusions are not modelled; the entity is kept in its OCS with a warning.
        private static bool Ocs(DxfRecord r, DxfProvenance p, ParseState state)
        {
            var ex = r.First(210) is { } a ? DxfValues.Number(a) : 0;
            var ey = r.First(220) is { } b ? DxfValues.Number(b) : 0;
            var ez = r.First(230) is { } c ? DxfValues.Number(c) : 1;
            if (Math.Abs(ex) < 1.0 / 64 && Math.Abs(ey) < 1.0 / 64)
            {
                if (ez < 0) return true;
                if (ez > 0) return false;
            }
            state.Warn("ocs-unsupported", p.Line, p.EntityType + " has a tilted extrusion direction; read in its own coordinate system");
            return false;
        }

        private static DxfEntity? Invalid(DxfRecord r, ParseState state, string message)
        {
            state.Warn("entity-invalid", r.Line, message + "; entity skipped");
            return null;
        }
    }
}

internal readonly record struct DxfPair(int Code, string Value, int Line)
{
    public string Name => Value.Trim();
}

internal sealed record DxfRecord(string Type, int Line, List<DxfPair> Groups)
{
    public DxfPair? First(int code)
    {
        foreach (var group in Groups) if (group.Code == code) return group;
        return null;
    }
}

internal static class DxfValues
{
    public static double Number(DxfPair pair)
    {
        if (!double.TryParse(pair.Value.AsSpan().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new DxfFormatException("bad-number", pair.Line + 1, "group " + pair.Code.ToString(CultureInfo.InvariantCulture) + " value is not a finite number");
        return value;
    }

    public static int Int(DxfPair pair)
    {
        if (!int.TryParse(pair.Value.AsSpan().Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out var value))
            throw new DxfFormatException("bad-number", pair.Line + 1, "group " + pair.Code.ToString(CultureInfo.InvariantCulture) + " value is not an integer");
        return value;
    }

    public static double NormaliseDegrees(double degrees)
    {
        var value = degrees % 360;
        if (value < 0) value += 360;
        if (value >= 360 || value == 0) value = 0;
        return value;
    }

    public static double? MetresPerUnit(DxfUnits units) => units switch
    {
        DxfUnits.Inches => 0.0254,
        DxfUnits.Feet => 0.3048,
        DxfUnits.Miles => 1609.344,
        DxfUnits.Millimetres => 0.001,
        DxfUnits.Centimetres => 0.01,
        DxfUnits.Metres => 1,
        DxfUnits.Kilometres => 1000,
        DxfUnits.Microinches => 2.54e-8,
        DxfUnits.Mils => 2.54e-5,
        DxfUnits.Yards => 0.9144,
        DxfUnits.Angstroms => 1e-10,
        DxfUnits.Nanometres => 1e-9,
        DxfUnits.Microns => 1e-6,
        DxfUnits.Decimetres => 0.1,
        DxfUnits.Decametres => 10,
        DxfUnits.Hectometres => 100,
        DxfUnits.Gigametres => 1e9,
        DxfUnits.AstronomicalUnits => 149597870700,
        DxfUnits.LightYears => 9460730472580800,
        DxfUnits.Parsecs => 3.0856775814913673e16,
        DxfUnits.UsSurveyFeet => 1200.0 / 3937.0,
        _ => null,
    };
}

// Plain-text forms of TEXT (%%d, %%p, %%c control codes) and MTEXT (inline formatting codes) values.
internal static class DxfTextFormat
{
    public static string PlainText(string value) => value
        .Replace("%%d", "°", StringComparison.OrdinalIgnoreCase)
        .Replace("%%p", "±", StringComparison.OrdinalIgnoreCase)
        .Replace("%%c", "Ø", StringComparison.OrdinalIgnoreCase)
        .Replace("%%%", "%", StringComparison.Ordinal);

    public static string PlainMText(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '{' || ch == '}') continue;
            if (ch != '\\' || i + 1 >= value.Length) { builder.Append(ch); continue; }
            var code = value[++i];
            switch (code)
            {
                case 'P': builder.Append('\n'); break;
                case '~': builder.Append(' '); break;
                case '\\': case '{': case '}': builder.Append(code); break;
                case 'L': case 'l': case 'O': case 'o': case 'K': case 'k': case 'N': break;
                case 'S':
                    {
                        var end = value.IndexOf(';', i + 1);
                        var stacked = end < 0 ? value[(i + 1)..] : value[(i + 1)..end];
                        builder.Append(stacked.Replace('^', '/').Replace('#', '/'));
                        i = end < 0 ? value.Length - 1 : end;
                        break;
                    }
                case 'U' when i + 5 < value.Length && value[i + 1] == '+' && int.TryParse(value.AsSpan(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var codePoint):
                    builder.Append((char)codePoint);
                    i += 5;
                    break;
                case 'A': case 'C': case 'c': case 'F': case 'f': case 'H': case 'h': case 'Q': case 'q': case 'T': case 't': case 'W': case 'w': case 'p':
                    {
                        var end = value.IndexOf(';', i + 1);
                        i = end < 0 ? value.Length - 1 : end;
                        break;
                    }
                default: builder.Append(code); break;
            }
        }
        return builder.ToString();
    }
}
