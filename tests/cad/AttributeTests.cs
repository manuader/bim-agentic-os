using Bim.Cad;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Point2 = Bim.Cad.Point2;

// P6-06 block attributes and MTEXT room tags oracle: the hand-written room-tags.dxf (millimetres) has room-tag block
// inserts whose ATTRIBs (INSERT 66 = 1 ... SEQEND) carry NAME/AREA values, an insert that lacks one ATTRIB (the
// ATTDEF default is used and warned), a block with a constant ATTDEF, and formatted MTEXT. Checks: attributes read
// as plain text with provenance (handle, tag, layer, line, insert handle), positioned at their own insertion or
// alignment point and transformed through enclosing inserts, MTEXT formatting codes degraded to plain text, brief
// rooms from room-tag attributes, typed errors and warnings for malformed sequences, determinism and a bounded
// mutation fuzz. Unit-tested evidence only.
internal static class AttributeTests
{
    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static bool Near(double a, double b, double eps = 1e-9) => Math.Abs(a - b) <= eps;
    private static bool Near(double? a, double b, double eps = 1e-9) => a is { } v && Near(v, b, eps);
    private static bool NearP(Point2 p, double x, double y, double eps = 1e-9) => Near(p.X, x, eps) && Near(p.Y, y, eps);
    private static string P(Point2 p) => "(" + F(p.X) + "," + F(p.Y) + ")";
    private static string Room(BriefRoomProposal r) => r.Id + " '" + r.Name + "' s" + (r.StatedAreaSquareMetres is { } s ? F(s) : "-") + " r" + (r.RegionAreaSquareMetres is { } g ? F(g) : "-") + " c" + F(r.Confidence) + " [" + string.Join(",", r.Notes) + "]";
    private static string Rooms(BriefDraft d) => string.Join(" | ", d.Rooms.Select(Room));
    private static string Codes(BriefDraft d) => string.Join(",", d.Uncertainties.Select(u => u.Code));
    private static string Warnings(IEnumerable<DxfWarning> warnings) => string.Join(",", warnings.Select(w => w.Code));
    private static string Attr(DxfAttribute a) => a.Provenance.EntityType + " " + a.Provenance.Handle + " " + a.Tag + "='" + a.PlainText + "' " + P(a.Insertion) + " L" + a.Provenance.Layer + " ins=" + (a.InsertHandle ?? "null") + " path=" + string.Join(">", a.Provenance.InsertPath);

    private static string G(string spec) => string.Concat(spec.Split('|').Select(item => { var space = item.IndexOf(' ', StringComparison.Ordinal); return item[..space] + "\n" + item[(space + 1)..] + "\n"; }));
    private static string Doc(string entities, string blocks = "") =>
        G("0 SECTION|2 HEADER|9 $INSUNITS|70 4|0 ENDSEC")
        + (blocks.Length > 0 ? G("0 SECTION|2 BLOCKS") + blocks + G("0 ENDSEC") : "")
        + G("0 SECTION|2 ENTITIES") + entities + G("0 ENDSEC|0 EOF");
    private static DxfDrawing Read(string entities, string blocks = "") => DxfReader.Parse(Doc(entities, blocks), "gen.dxf");
    private static string Attrib(string handle, string tag, string value, double x, double y, string extra = "", string layer = "TAGS", double height = 200) =>
        G("0 ATTRIB|5 " + handle + "|8 " + layer + "|10 " + F(x) + "|20 " + F(y) + "|30 0|40 " + F(height) + "|1 " + value + "|2 " + tag + "|70 0" + (extra.Length > 0 ? "|" + extra : ""));
    private static string Insert(string handle, string block, double x, double y, bool follows = true, string extra = "") =>
        G("0 INSERT|5 " + handle + "|8 TAGS" + (follows ? "|66 1" : "") + "|2 " + block + "|10 " + F(x) + "|20 " + F(y) + "|30 0" + (extra.Length > 0 ? "|" + extra : ""));
    private static string Seqend(string handle) => G("0 SEQEND|5 " + handle + "|8 TAGS");
    private static string Tag(string name = "TAG") => G("0 BLOCK|5 TB|8 0|2 " + name + "|70 2|10 0|20 0|30 0") + G("0 ATTDEF|5 TD1|8 0|10 0|20 0|30 0|40 200|1 X|3 Name|2 NAME|70 0") + G("0 ENDBLK|5 TE|8 0");
    private static string Plain(string mtext) => Read(G("0 MTEXT|5 M|8 0|10 0|20 0|40 1|1 " + mtext)).Entities.OfType<DxfText>().Single().PlainText;
    private static string PlainTextEntity(string text) => Read(G("0 TEXT|5 T|8 0|10 0|20 0|40 1|1 " + text)).Entities.OfType<DxfText>().Single().PlainText;
    private static string Rect(string handle, double x0, double y0, double x1, double y1) =>
        G("0 LWPOLYLINE|5 " + handle + "|8 ROOMS|90 4|70 1|10 " + F(x0) + "|20 " + F(y0) + "|10 " + F(x1) + "|20 " + F(y0) + "|10 " + F(x1) + "|20 " + F(y1) + "|10 " + F(x0) + "|20 " + F(y1));

    private static string? Unexpected(Func<object?> action)
    {
        try { action(); return null; }
        catch (Exception error) { return error.GetType().Name + ": " + error.Message; }
    }

    private static string? CodeOf(Func<object?> action)
    {
        try { action(); return null; }
        catch (DxfFormatException error) { return error.Code; }
        catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
    }

    public static void Run(Action<bool, string, string> check, string fixtures)
    {
        void C(bool condition, string name, string detail = "") => check(condition, "attrib " + name, detail);
        var path = Path.Combine(fixtures, "room-tags.dxf");

        // ---------- room-tags.dxf: reader ----------
        try
        {
            var d = DxfReader.ReadFile(path);
            C(d.Entities.Count == 11 && d.Unsupported.Count == 0 && d.Warnings.Count == 0 && !d.Entities.OfType<DxfAttribute>().Any(),
                "fixture 11 entities, ATTRIB/SEQEND attached to their INSERT, nothing unsupported or warned", d.Entities.Count + " " + string.Join(";", d.Unsupported) + " " + Warnings(d.Warnings));
            var inserts = d.Entities.OfType<DxfInsert>().ToList();
            C(inserts.Select(i => i.Attributes.Count).SequenceEqual(new[] { 2, 2, 1, 0 }), "fixture attribute counts per insert", string.Join(",", inserts.Select(i => i.Attributes.Count)));
            if (inserts.Count == 4 && inserts[0].Attributes.Count == 2 && inserts[1].Attributes.Count == 2 && inserts[2].Attributes.Count == 1)
            {
                var name = inserts[0].Attributes[0];
                C(name.Tag == "NAME" && name.Value == "DORMITORIO 1" && name.PlainText == "DORMITORIO 1" && !name.IsDefinition && name.InsertHandle == "200"
                    && name.Provenance.Handle == "201" && name.Provenance.EntityType == "ATTRIB" && name.Provenance.Layer == "TAGS" && name.Provenance.Line == 359 && name.Provenance.SourceFile == "room-tags.dxf"
                    && NearP(name.Insertion, 2000, 1500) && Near(name.Height, 200),
                    "fixture ATTRIB NAME value, tag, provenance and insertion", Attr(name) + " line " + name.Provenance.Line);
                var area = inserts[0].Attributes[1];
                C(area.Tag == "AREA" && area.PlainText == "12,00 m²" && area.Provenance.Handle == "202" && area.Provenance.Line == 379 && NearP(area.Insertion, 2000, 1200), "fixture ATTRIB AREA", Attr(area));
                var justified = inserts[1].Attributes[0];
                C(justified.PlainText == "DEPÓSITO" && NearP(justified.Insertion, 6500, 1500), "fixture justified ATTRIB placed at its alignment point (11/21)", Attr(justified));
                C(Near(inserts[1].Attributes[1].RotationDegrees, 90) && NearP(inserts[1].Attributes[1].Insertion, 6800, 1500), "fixture ATTRIB own rotation and insertion", Attr(inserts[1].Attributes[1]));
                var coded = inserts[2].Attributes[0];
                C(coded.Value == "Quir%%243fano" && coded.PlainText == "Quirófano" && coded.Provenance.Layer == "0", "fixture ATTRIB %%nnn control code decoded", Attr(coded));
            }
            var roomTag = d.Blocks.Single(b => b.Name == "ROOMTAG");
            var defs = roomTag.Entities.OfType<DxfAttribute>().ToList();
            C(defs.Count == 2 && defs.All(a => a.IsDefinition && a.InsertHandle is null) && defs[0].Tag == "NAME" && defs[0].Prompt == "Room name" && defs[0].Value == "ROOM"
                && defs[0].Provenance.EntityType == "ATTDEF" && defs[0].Provenance.Handle == "B12" && defs[0].Provenance.Line == 93 && defs[1].Tag == "AREA" && NearP(defs[1].Insertion, 0, -300),
                "fixture block ATTDEFs with tag, prompt and default", string.Join(" | ", defs.Select(Attr)));
            var constant = d.Blocks.Single(b => b.Name == "STAIRTAG").Entities.OfType<DxfAttribute>().Single();
            C(constant.IsConstant && constant.Flags == 2 && constant.Value == "Escalera", "fixture constant ATTDEF (flag 2)", Attr(constant));

            var mtexts = d.Entities.OfType<DxfText>().ToList();
            C(mtexts.Count == 2 && mtexts[0].PlainText == "Sala de\nestar\n9,00 m2", "fixture MTEXT font group, paragraph, height and stacked superscript", mtexts.Count > 0 ? mtexts[0].PlainText.Replace("\n", "\\n", StringComparison.Ordinal) : "none");
            C(mtexts.Count == 2 && mtexts[1].PlainText == "Ø50 {x} 100% a b \\ 30° ±1", "fixture MTEXT %%c %%% %%d %%p, \\~, \\\\ and escaped braces", mtexts.Count > 1 ? mtexts[1].PlainText : "none");

            // Explosion: ATTRIBs keep their own (world) placement, missing ATTRIBs fall back to ATTDEF defaults.
            var exploded = d.Explode();
            var attributes = exploded.Entities.OfType<DxfAttribute>().ToList();
            C(string.Join(",", attributes.Select(a => a.Provenance.Handle)) == "201,202,211,212,221,B13,B22", "explode attribute order: ATTRIBs then defaults, per insert", string.Join(" | ", attributes.Select(Attr)));
            if (attributes.Count == 7)
            {
                C(attributes[0].Provenance.InsertPath.SequenceEqual(new[] { "200" }) && attributes[0].InsertHandle == "200" && NearP(attributes[0].Insertion, 2000, 1500), "explode ATTRIB insert path and untransformed own point", Attr(attributes[0]));
                C(Near(attributes[3].RotationDegrees, 90) && NearP(attributes[3].Insertion, 6800, 1500), "explode ATTRIB of a rotated insert is not rotated twice", Attr(attributes[3]));
                C(attributes[4].Provenance.Layer == "TAGS", "explode ATTRIB on layer 0 takes the insert layer", Attr(attributes[4]));
                C(attributes[5].IsDefinition && attributes[5].Tag == "AREA" && attributes[5].InsertHandle == "220" && attributes[5].Provenance.InsertPath.SequenceEqual(new[] { "220" }) && NearP(attributes[5].Insertion, 1500, 4700) && attributes[5].Provenance.Layer == "TAGS",
                    "explode missing ATTRIB uses the ATTDEF default transformed by the insert", Attr(attributes[5]));
                C(attributes[6].IsDefinition && attributes[6].IsConstant && attributes[6].PlainText == "Escalera" && NearP(attributes[6].Insertion, 6500, 5000) && attributes[6].InsertHandle == "230",
                    "explode constant ATTDEF placed through the insert (base point 100,100)", Attr(attributes[6]));
            }
            C(Warnings(exploded.Warnings) == "attribute-default" && exploded.Warnings[0].Line == 479, "explode warns once for the default-valued attribute", Warnings(exploded.Warnings));

            // Brief extraction from the room tags.
            var brief = BriefExtract.FromDxf(d);
            C(brief.Rooms.Select(r => r.Id).SequenceEqual(new[] { "dormitorio-1", "deposito", "quirofano", "escalera", "sala-de-estar" }), "brief five rooms from tags and MTEXT in drawing order", Rooms(brief));
            if (brief.Rooms.Count == 5)
            {
                var bed = brief.Rooms[0];
                C(bed.Name == "DORMITORIO 1" && Near(bed.StatedAreaSquareMetres, 12) && Near(bed.RegionAreaSquareMetres, 12) && Near(bed.Confidence, 1) && bed.Notes.SequenceEqual(new[] { "room-tag", "area-attribute" }),
                    "brief room-tag NAME/AREA attributes yield a room", Room(bed));
                C(bed.Label.EntityType == "ATTRIB" && bed.Label.Handle == "201" && bed.Label.Tag == "NAME" && bed.Label.Layer == "TAGS" && bed.Label.Line == 359 && bed.Label.InsertPath.SequenceEqual(new[] { "200" })
                    && bed.Label.At == new Point2(2000, 1500) && bed.Area is { Handle: "202", Tag: "AREA", EntityType: "ATTRIB", Text: "12,00 m²" } && bed.Region?.Handle == "100",
                    "brief attribute provenance (handle, tag, layer, line, insert path)", bed.Label + " " + bed.Area);
                var store = brief.Rooms[1];
                C(store.Name == "DEPÓSITO" && Near(store.StatedAreaSquareMetres, 9) && Near(store.RegionAreaSquareMetres, 9) && Near(store.Confidence, 0.9) && store.Notes.SequenceEqual(new[] { "room-tag", "area-attribute", "area-unitless" }),
                    "brief unitless AREA attribute read as m2 with lower confidence", Room(store));
                var theatre = brief.Rooms[2];
                C(theatre.Name == "Quirófano" && theatre.StatedAreaSquareMetres is null && Near(theatre.RegionAreaSquareMetres, 6) && Near(theatre.Confidence, 0.8) && theatre.Notes.SequenceEqual(new[] { "room-tag", "area-from-region" }),
                    "brief NAME tag outside the vocabulary is a room; area from its region", Room(theatre));
                var stair = brief.Rooms[3];
                C(stair.Label.EntityType == "ATTDEF" && stair.Label.Handle == "B22" && stair.Label.InsertPath.SequenceEqual(new[] { "230" }) && Near(stair.RegionAreaSquareMetres, 6) && stair.Notes.SequenceEqual(new[] { "room-tag", "area-from-region" }),
                    "brief constant ATTDEF NAME names the room", Room(stair) + " " + stair.Label);
                var lounge = brief.Rooms[4];
                C(lounge.Name == "Sala de estar" && Near(lounge.StatedAreaSquareMetres, 9) && Near(lounge.RegionAreaSquareMetres, 9) && lounge.Notes.SequenceEqual(new[] { "area-same-text" }) && lounge.Label.Tag is null,
                    "brief formatted MTEXT label and stacked m2 area", Room(lounge));
            }
            C(Codes(brief) == "area-from-region,area-from-region,explode-warning" && brief.Issues.Count == 0, "brief uncertainties and valid brief", Codes(brief) + " " + string.Join(",", brief.Issues.Select(i => i.Code)));
            var again = BriefExtract.FromDxf(DxfReader.Parse(File.ReadAllText(path).Replace("\n", "\r\n", StringComparison.Ordinal), "room-tags.dxf"));
            C(Rooms(again) == Rooms(brief) && Codes(again) == Codes(brief) && DxfJson.Serialize(DxfReader.ReadFile(path)) == DxfJson.Serialize(d), "deterministic across runs and CRLF");
            var json = DxfJson.Serialize(d);
            C(json.Contains("\"attributes\"", StringComparison.Ordinal) && json.Contains("\"tag\":\"NAME\"", StringComparison.Ordinal) && json.Contains("\"kind\":\"attdef\"", StringComparison.Ordinal), "json lists insert attributes and block attribute definitions");
        }
        catch (Exception error) { C(false, "fixture unexpected", error.GetType().Name + ": " + error.Message + " " + error.StackTrace); }

        // ---------- generated: nesting, malformed sequences, bounds ----------
        try
        {
            var unit = Tag() + G("0 BLOCK|5 UB|8 0|2 UNIT|70 0|10 0|20 0|30 0") + Insert("N1", "TAG", 100, 100) + Attrib("NA", "NAME", "Hall", 100, 100) + Seqend("NS") + G("0 ENDBLK|5 UE|8 0");
            var nested = Read(Insert("U1", "UNIT", 10000, 0, follows: false, extra: "50 90"), unit).Explode();
            var hall = nested.Entities.OfType<DxfAttribute>().ToList();
            C(hall.Count == 1 && NearP(hall[0].Insertion, 9900, 100) && Near(hall[0].RotationDegrees, 90) && hall[0].Provenance.InsertPath.SequenceEqual(new[] { "U1", "N1" }) && hall[0].InsertHandle == "N1" && nested.Warnings.Count == 0,
                "gen nested insert: ATTRIB transformed by the enclosing insert only", string.Join(" | ", hall.Select(Attr)) + " " + Warnings(nested.Warnings));

            var unterminated = Read(Insert("I1", "TAG", 0, 0) + Attrib("A1", "NAME", "Cocina", 0, 0) + G("0 LINE|5 L1|8 0|10 0|20 0|11 1|21 0"), Tag());
            C(unterminated.Entities.Count == 2 && unterminated.Entities[1] is DxfLine && ((DxfInsert)unterminated.Entities[0]).Attributes.Count == 1 && Warnings(unterminated.Warnings) == "attrib-unterminated",
                "gen ATTRIB sequence without SEQEND ends at the next entity with a warning", unterminated.Entities.Count + " " + Warnings(unterminated.Warnings));
            var atEnd = Read(Insert("I1", "TAG", 0, 0), Tag());
            C(atEnd.Entities.Count == 1 && Warnings(atEnd.Warnings) == "attrib-unterminated", "gen attributes-follow flag at the end of the section is warned", Warnings(atEnd.Warnings));
            var empty = Read(Insert("I1", "TAG", 0, 0) + Seqend("S1"), Tag());
            C(empty.Entities.Count == 1 && ((DxfInsert)empty.Entities[0]).Attributes.Count == 0 && empty.Warnings.Count == 0 && empty.Unsupported.Count == 0, "gen empty ATTRIB sequence", Warnings(empty.Warnings));
            var orphan = Read(Attrib("A1", "NAME", "Cocina", 0, 0) + G("0 LINE|5 L1|8 0|10 0|20 0|11 1|21 0"));
            C(orphan.Entities.Count == 1 && orphan.Unsupported.Count == 0 && Warnings(orphan.Warnings) == "attrib-orphan", "gen ATTRIB without an INSERT is ignored with a warning", Warnings(orphan.Warnings));
            var noFlag = Read(Insert("I1", "TAG", 0, 0, follows: false) + Attrib("A1", "NAME", "Cocina", 0, 0) + Seqend("S1"), Tag());
            C(((DxfInsert)noFlag.Entities.Single()).Attributes.Count == 1 && Warnings(noFlag.Warnings) == "attributes-flag-missing", "gen ATTRIB after an INSERT without 66 is attached with a warning", Warnings(noFlag.Warnings));
            var noTag = Read(Insert("I1", "TAG", 0, 0) + G("0 ATTRIB|5 A1|8 0|10 0|20 0|40 1|1 x") + Seqend("S1"), Tag());
            C(((DxfInsert)noTag.Entities.Single()).Attributes.Count == 0 && Warnings(noTag.Warnings) == "entity-invalid", "gen ATTRIB without a tag is skipped", Warnings(noTag.Warnings));
            C(CodeOf(() => Read(Insert("I1", "TAG", 0, 0) + G("0 ATTRIB|5 A1|8 0|10 abc|20 0|40 1|1 x|2 NAME") + Seqend("S1"), Tag())) == "bad-number", "gen ATTRIB bad coordinate is a typed bad-number error");
            var hidden = Read(Insert("I1", "TAG", 0, 0) + Attrib("A1", "NAME", "Cocina", 0, 0, extra: "70 1") + Seqend("S1"), Tag());
            C(((DxfInsert)hidden.Entities.Single()).Attributes.Single().Provenance.Invisible, "gen invisible attribute flag (70 & 1)");
            var multiline = Read(Insert("I1", "TAG", 0, 0) + Attrib("A1", "NAME", "Line1\\PLine2", 5, 6, extra: "101 Embedded Object|10 99|20 99|1 Line1\\PLine2") + Seqend("S1"), Tag());
            var line = ((DxfInsert)multiline.Entities.Single()).Attributes.Single();
            C(line.PlainText == "Line1\nLine2" && NearP(line.Insertion, 5, 6), "gen multi-line attribute with embedded MTEXT degraded to its plain text", Attr(line));

            var many = new StringBuilder(Insert("I1", "TAG", 0, 0));
            for (var k = 0; k < 20000; k++) many.Append(Attrib("A" + k.ToString(CultureInfo.InvariantCulture), "T" + k.ToString(CultureInfo.InvariantCulture), "v", k, 0));
            many.Append(Seqend("S1"));
            var clock = Stopwatch.StartNew();
            var big = Read(many.ToString(), Tag());
            var bigExploded = big.Explode();
            clock.Stop();
            C(((DxfInsert)big.Entities.Single()).Attributes.Count == 20000 && bigExploded.Entities.OfType<DxfAttribute>().Count() == 20001 && clock.ElapsedMilliseconds < 10_000,
                "gen 20000 attributes read and exploded in linear time", clock.ElapsedMilliseconds + " ms");
            C(CodeOf(() => big.Explode(new DxfExplodeOptions(MaxWork: 1000))) == "explode-limit", "gen attributes count against explode MaxWork");

            // MTEXT inline codes degrade safely.
            void M(string raw, string expected) { var plain = Plain(raw); C(plain == expected, "mtext '" + raw + "'", plain.Replace("\n", "\\n", StringComparison.Ordinal)); }
            M("\\fArial;Bold\\P{\\C1;red}", "Bold\nred");
            M("%%c50 %%d %%p1 100%%%", "Ø50 ° ±1 100%");
            M("a\\~b", "a b");
            M("\\\\ \\{ \\}", "\\ { }");
            M("m\\S2^;", "m2");
            M("\\S1/2;", "1/2");
            M("\\S1#4;", "1/4");
            M("\\S^2;", "2");
            M("x\\S1/2", "x1/2");
            M("\\H2.5x;Big\\A1;\\pxqc;C", "BigC");
            M("A^JB^IC", "A\nB C");
            M("\\fArial", "");
            M("abc\\", "abc\\");
            M("{{{\\LUnder\\l}}}", "Under");
            var deep = new string('{', 100000) + "x" + new string('}', 100000);
            var deepClock = Stopwatch.StartNew();
            C(Plain(deep) == "x" && deepClock.ElapsedMilliseconds < 5000, "mtext 100000 nested groups in linear time", deepClock.ElapsedMilliseconds + " ms");
            C(PlainTextEntity("%%uUnder%%u 50%%%") == "Under 50%" && PlainTextEntity("Quir%%243fano") == "Quirófano" && PlainTextEntity("%%c10 %%d") == "Ø10 °", "text %%u, %%%, %%nnn, %%c, %%d codes", PlainTextEntity("%%uUnder%%u 50%%%"));
        }
        catch (Exception error) { C(false, "gen unexpected", error.GetType().Name + ": " + error.Message + " " + error.StackTrace); }

        // ---------- generated briefs from attributes ----------
        try
        {
            BriefDraft Brief(string entities, BriefExtractOptions? options = null) => BriefExtract.FromDxf(Read(entities, Tag()), options);
            var far = Brief(Insert("I1", "TAG", 0, 0) + Attrib("A1", "NAME", "Cocina", 0, 0) + Attrib("A2", "AREA", "7,5 m²", 0, -50000) + Seqend("S1"));
            C(far.Rooms.Count == 1 && Near(far.Rooms[0].StatedAreaSquareMetres, 7.5) && far.Rooms[0].Notes.SequenceEqual(new[] { "room-tag", "area-attribute", "no-region" }) && Near(far.Rooms[0].Confidence, 0.9) && Codes(far) == "",
                "gen NAME and AREA attributes of one insert pair at any distance", Rooms(far) + " " + Codes(far));
            var spanish = Brief(Insert("I1", "TAG", 0, 0) + Attrib("A1", "nombre", "Comedor", 0, 0) + Attrib("A2", "SUPERFICIE", "15,5 m2", 0, -300) + Seqend("S1"));
            C(spanish.Rooms.Count == 1 && spanish.Rooms[0].Id == "comedor" && Near(spanish.Rooms[0].StatedAreaSquareMetres, 15.5) && spanish.Rooms[0].Notes.Contains("area-attribute"), "gen es tags NOMBRE/SUPERFICIE (case-insensitive)", Rooms(spanish));
            var proximity = Brief(Insert("X1", "TAG", 0, 0) + Attrib("A1", "NAME", "Cocina", 0, 0) + Attrib("A2", "AREA", "5 m²", 0, -700) + Seqend("S1")
                + Insert("X2", "TAG", 0, -900) + Attrib("A3", "NAME", "Comedor", 0, -900) + Attrib("A4", "AREA", "8 m²", 0, -1600) + Seqend("S2"));
            C(proximity.Rooms.Count == 2 && Near(proximity.Rooms[0].StatedAreaSquareMetres, 5) && Near(proximity.Rooms[1].StatedAreaSquareMetres, 8) && proximity.Rooms[1].Area?.Handle == "A4",
                "gen each tag pairs its own AREA even when another insert's AREA is nearer", Rooms(proximity));
            var note = Brief(Insert("I1", "TAG", 0, 0) + Attrib("A1", "NOTE", "Cocina 7 m²", 0, 0) + Seqend("S1"));
            C(note.Rooms.Count == 1 && note.Rooms[0].Notes.SequenceEqual(new[] { "area-same-text", "no-region" }) && note.Rooms[0].Label.Tag == "NOTE" && note.Rooms[0].Label.EntityType == "ATTRIB",
                "gen attribute with another tag is read as ordinary text", Rooms(note));
            var invisible = Insert("I1", "TAG", 0, 0) + Attrib("A1", "NAME", "Cocina", 0, 0, extra: "70 1") + Seqend("S1");
            C(Brief(invisible).Rooms.Count == 0 && Brief(invisible, new BriefExtractOptions { IncludeInvisible = true }).Rooms.Count == 1, "gen invisible attributes need IncludeInvisible");
            var definition = Brief(G("0 ATTDEF|5 D1|8 0|10 0|20 0|40 200|1 Garage|3 Name|2 NAME|70 0"));
            C(definition.Rooms.Count == 0, "gen model-space ATTDEF (not through an insert) is not a room", Rooms(definition));
            var tagged = Brief(Rect("R1", 0, 0, 3000, 3000) + Insert("I1", "TAG", 1500, 1500) + Attrib("A1", "ROOM_NAME", "Oficina", 1500, 1500) + Attrib("A2", "ROOM_AREA", "9.00", 1500, 1200) + Seqend("S1"));
            C(tagged.Rooms.Count == 1 && Near(tagged.Rooms[0].StatedAreaSquareMetres, 9) && Near(tagged.Rooms[0].RegionAreaSquareMetres, 9) && tagged.Rooms[0].Notes.SequenceEqual(new[] { "room-tag", "area-attribute", "area-unitless" }) && Codes(tagged) == "",
                "gen ROOM_NAME/ROOM_AREA tags in a region", Rooms(tagged) + " " + Codes(tagged));

            // Fuzz the fixture: reading, exploding and brief extraction raise only DxfFormatException.
            var lines = File.ReadAllText(path).Split('\n');
            var random = new Random(6061);
            var unexpected = new List<string>();
            var clock = Stopwatch.StartNew();
            for (var iteration = 0; iteration < 300; iteration++)
            {
                var mutated = lines.ToList();
                for (var m = 0; m < 1 + random.Next(3); m++)
                {
                    var at = random.Next(mutated.Count);
                    switch (random.Next(4))
                    {
                        case 0: mutated.RemoveAt(at); break;
                        case 1: mutated.Insert(at, mutated[at]); break;
                        case 2: mutated[at] = new[] { "0", "66", "ATTRIB", "SEQEND", "INSERT", "ATTDEF", "\\S", "{", "\\", "%%", "2", "1" }[random.Next(12)]; break;
                        default: var other = random.Next(mutated.Count); (mutated[at], mutated[other]) = (mutated[other], mutated[at]); break;
                    }
                }
                try
                {
                    var parsed = DxfReader.Parse(string.Join("\n", mutated), "fuzz.dxf");
                    DxfJson.Serialize(parsed);
                    parsed.Explode();
                    BriefExtract.FromDxf(parsed);
                }
                catch (DxfFormatException) { }
                catch (Exception error) { unexpected.Add(iteration.ToString(CultureInfo.InvariantCulture) + " " + error.GetType().Name + ": " + error.Message); }
            }
            clock.Stop();
            C(unexpected.Count == 0 && clock.ElapsedMilliseconds < 30_000, "fuzz raises only DxfFormatException and stays bounded", clock.ElapsedMilliseconds + " ms; " + string.Join(" | ", unexpected.Take(3)));
            C(Unexpected(() => BriefExtract.ReadText("\\S")) is null, "gen ReadText of raw codes never throws");
        }
        catch (Exception error) { C(false, "brief unexpected", error.GetType().Name + ": " + error.Message + " " + error.StackTrace); }
    }
}
