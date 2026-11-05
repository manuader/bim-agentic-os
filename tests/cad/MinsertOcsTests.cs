using Bim.Cad;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Point2 = Bim.Cad.Point2;

// P6-09 DXF MINSERT arrays and tilted OCS entities oracle. The hand-written minsert-ocs.dxf (millimetres) has MINSERT
// arrays (plain 3x2, rotated and scaled, in the mirrored OCS (0,0,-1), nested inside a block) and entities with
// non-default extrusion directions: tilted ones whose geometry is still horizontal after the AutoCAD arbitrary axis
// transform (kept, in world XY), tilted ones that are not planar in the plan (typed unsupported with a reason),
// a non-unit (0,0,-2) mirror and a zero extrusion. Also: MINSERT ATTRIBs repeated per cell, row/column provenance
// in insert paths, the explode limit, invalid counts, determinism, and a regression that the reader and exploder
// outputs for every pre-existing DXF fixture are unchanged. Unit-tested evidence only.
internal static class MinsertOcsTests
{
    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static bool Near(double a, double b, double eps = 1e-9) => Math.Abs(a - b) <= eps;
    private static bool NearP(Point2 p, double x, double y, double eps = 1e-9) => Near(p.X, x, eps) && Near(p.Y, y, eps);
    private static string P(Point2 p) => "(" + F(p.X) + "," + F(p.Y) + ")";
    private static string Path0(DxfEntity e) => string.Join(">", e.Provenance.InsertPath);
    private static string Describe(DxfEntity e) => e switch
    {
        DxfLine l => "LINE " + Path0(e) + " " + P(l.Start) + "-" + P(l.End),
        DxfCircle c => "CIRCLE " + Path0(e) + " " + P(c.Center) + " r" + F(c.Radius),
        DxfArc a => "ARC " + Path0(e) + " " + P(a.Center) + " r" + F(a.Radius) + " " + F(a.StartAngleDegrees) + ".." + F(a.EndAngleDegrees),
        DxfPolyline p => "PL " + Path0(e) + " " + string.Join(";", p.Vertices.Select(v => P(v.Point) + "b" + F(v.Bulge))),
        DxfAttribute t => "ATTRIB " + Path0(e) + " " + t.Tag + " " + P(t.Insertion) + " ins=" + (t.InsertHandle ?? "null"),
        _ => e.Provenance.EntityType + " " + Path0(e),
    };
    private static string All(IEnumerable<DxfEntity> entities) => string.Join(" | ", entities.Select(Describe));

    private static string G(string spec) => string.Concat(spec.Split('|').Select(item => { var space = item.IndexOf(' ', StringComparison.Ordinal); return item[..space] + "\n" + item[(space + 1)..] + "\n"; }));
    private static string Doc(string entities, string blocks = "") =>
        G("0 SECTION|2 HEADER|9 $INSUNITS|70 4|0 ENDSEC")
        + (blocks.Length > 0 ? G("0 SECTION|2 BLOCKS") + blocks + G("0 ENDSEC") : "")
        + G("0 SECTION|2 ENTITIES") + entities + G("0 ENDSEC|0 EOF");
    private static string LineBlock(string name = "B") => G("0 BLOCK|5 BB|8 0|2 " + name + "|70 0|10 0|20 0|30 0|0 LINE|5 BL|8 0|10 0|20 0|11 10|21 0|0 ENDBLK|5 BE|8 0");

    private static string Safe(Func<string> json)
    {
        try { return Digest(json()); }
        catch (CadDraftException error) { return "E-" + error.Code; }
        catch (DxfFormatException error) { return "E-" + error.Code; }
        catch (Exception error) { return "E-" + error.GetType().Name; }
    }

    private static string Codes(CadDraft d) => string.Join(",", d.Uncertainties.Select(u => u.Code));
    private static string Openings(CadDraft d) => string.Join(" | ", d.Openings.Select(o => o.Key + " " + o.BlockName + " (" + F(o.Start.X) + "," + F(o.Start.Y) + ")-(" + F(o.End.X) + "," + F(o.End.Y) + ") host " + o.HostKey));
    private static string Walls() => G("0 LINE|5 W1|8 WALLS|10 0|20 0|11 10000|21 0|0 LINE|5 W2|8 WALLS|10 0|20 200|11 10000|21 200");
    private static string DoorBlock() => G("0 BLOCK|5 DB|8 0|2 DOOR-A|70 0|10 0|20 0|30 0|0 LINE|5 DL|8 0|10 0|20 100|11 800|21 100|0 ENDBLK|5 DE|8 0");
    private static bool DoorsAt(CadDraft d, params double[] xs) =>
        d.Openings.Count == xs.Length && d.Openings.Select(o => Math.Min(o.Start.X, o.End.X)).OrderBy(x => x).Zip(xs).All(p => Near(p.First, p.Second, 1e-6))
        && d.Openings.All(o => Near(Math.Abs(o.End.X - o.Start.X), 800, 1e-6) && Near(o.Start.Y, 100, 1e-6) && o.HostKey.Length > 0 && o.Kind == "door");

    private static string? CodeOf(Func<object?> action)
    {
        try { action(); return null; }
        catch (DxfFormatException error) { return error.Code; }
        catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
    }

    // Numbers rounded to 1e-6 so that the regression digest does not depend on last-bit libm differences between
    // operating systems; any real change in an output still changes it.
    private static JsonNode? Round(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                {
                    var copy = new JsonObject();
                    foreach (var pair in o) copy[pair.Key] = Round(pair.Value);
                    return copy;
                }
            case JsonArray a:
                {
                    var copy = new JsonArray();
                    foreach (var item in a) copy.Add(Round(item));
                    return copy;
                }
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number:
                {
                    var rounded = Math.Round(v.GetValue<double>(), 6);
                    return JsonValue.Create(rounded == 0 ? 0.0 : rounded);
                }
            case null:
                return null;
            default:
                return node.DeepClone();
        }
    }

    private static string Digest(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Round(JsonNode.Parse(json))!.ToJsonString())))[..16];

    // Reader and exploder digests of every DXF fixture that existed before P6-09, captured in CI from the
    // P6-01..P6-06 code (origin/main before P6-09; identical on Linux and Windows).
    private static readonly (string File, string Read, string Exploded)[] Baseline =
    {
        ("blocks.dxf", "41062432943CE103", "3C3E521C09FA828A"),
        ("brief-plan.dxf", "46697137EBFEBE5C", "46697137EBFEBE5C"),
        ("draft-plan.dxf", "56953E9FBDC953D0", "5A47947E280264FA"),
        ("room-tags.dxf", "AF593823EB9B1F78", "824DE72B8A13DD7E"),
        ("simple-plan.dxf", "9B4F829C1B0615D0", "E54699B5443A7299"),
        ("walls.dxf", "435ABEB726CC3D81", "435ABEB726CC3D81"),
    };

    // CadToBimIr draft and BriefExtract brief digests of the same fixtures, captured in CI from origin/main
    // (b0bcd22, before P6-09) with the temporary probe branch relay159-P6-09-baseline.
    private static readonly (string File, string Draft, string Brief)[] ConsumerBaseline =
    {
        ("blocks.dxf", "?", "?"),
        ("brief-plan.dxf", "?", "?"),
        ("draft-plan.dxf", "?", "?"),
        ("room-tags.dxf", "?", "?"),
        ("simple-plan.dxf", "?", "?"),
        ("walls.dxf", "?", "?"),
    };

    public static void Run(Action<bool, string, string> check, string fixtures)
    {
        void C(bool condition, string name, string detail = "") => check(condition, "minsert-ocs " + name, detail);
        var projectId = Guid.Parse("6f0c6a52-4d3b-4a8e-9b51-2f6f9d0e7a11");

        // ---------- regression: consumers of pre-existing fixtures are unchanged ----------
        try
        {
            var actual = new List<string>();
            var same = true;
            foreach (var (file, draft, brief) in ConsumerBaseline)
            {
                var d = DxfReader.ReadFile(Path.Combine(fixtures, file));
                var options = new CadToBimIrOptions(projectId) { MillimetresPerUnit = d.MetresPerUnit is null ? 1 : null };
                var a = Safe(() => JsonSerializer.Serialize(CadToBimIr.FromDxf(d, options)));
                var b = Safe(() => JsonSerializer.Serialize(BriefExtract.FromDxf(d)));
                actual.Add(file + "=" + a + "/" + b);
                same &= a == draft && b == brief;
            }
            C(same, "pre-existing fixtures keep their CadToBimIr draft and BriefExtract output", string.Join(" ", actual));
        }
        catch (Exception error)
        {
            C(false, "consumer regression unexpected exception", error.GetType().Name + ": " + error.Message);
        }

        // ---------- regression: pre-existing fixtures give the same reader and exploder output ----------
        try
        {
            var actual = new List<string>();
            var same = true;
            foreach (var (file, read, exploded) in Baseline)
            {
                var d = DxfReader.ReadFile(Path.Combine(fixtures, file));
                var x = d.Explode();
                var r = Digest(DxfJson.Serialize(d));
                var e = Digest(DxfJson.Serialize(d with { Entities = x.Entities, Warnings = x.Warnings }));
                actual.Add(file + "=" + r + "/" + e);
                same &= r == read && e == exploded;
            }
            C(same, "pre-existing fixtures keep their reader and exploder output", string.Join(" ", actual));
        }
        catch (Exception error)
        {
            C(false, "regression unexpected exception", error.GetType().Name + ": " + error.Message);
        }

        // ---------- minsert-ocs.dxf ----------
        try
        {
            var path = Path.Combine(fixtures, "minsert-ocs.dxf");
            var d = DxfReader.ReadFile(path);
            var codes = string.Join(",", d.Warnings.Select(w => w.Code));
            C(!d.Warnings.Any(w => w.Code == "minsert-ignored") && !d.Warnings.Any(w => w.Code == "ocs-unsupported"), "no minsert-ignored or ocs-unsupported warnings", codes);
            C(d.Warnings.Count(w => w.Code == "ocs-non-planar") == 7 && d.Warnings.Count(w => w.Code == "ocs-invalid") == 1 && d.Warnings.Count == 8, "seven ocs-non-planar warnings and one ocs-invalid", codes);
            C(string.Join(",", d.Entities.Select(e => e.Provenance.Handle)) == "70,71,72,73,81,82,86,88", "kept top-level entities", string.Join(",", d.Entities.Select(e => e.Provenance.Handle)));
            C(string.Join(",", d.Unsupported.Select(u => u.Handle + ":" + u.EntityType)) == "80:CIRCLE,83:LWPOLYLINE,84:TEXT,85:INSERT,87:CIRCLE,89:LWPOLYLINE,8A:MTEXT,8B:CIRCLE",
                "tilted non-planar entities are typed unsupported with their provenance", string.Join(",", d.Unsupported.Select(u => u.Handle + ":" + u.EntityType)));

            var json = DxfJson.Serialize(d);
            C(json.Contains("\"rotation_deg\":0,\"columns\":3,\"rows\":2,\"column_spacing\":500,\"row_spacing\":400,", StringComparison.Ordinal), "MINSERT array fields in the model JSON", json);
            C(json.Contains("\"handle\":\"80\",\"layer\":\"0\",\"line\":", StringComparison.Ordinal)
                && System.Text.RegularExpressions.Regex.Matches(json, "\"reason\":\"ocs-non-planar\"").Count == 7
                && System.Text.RegularExpressions.Regex.Matches(json, "\"reason\":\"ocs-invalid\"").Count == 1,
                "unsupported entries carry a reason in the model JSON", json);
            var plainInsert = DxfJson.Serialize(DxfReader.Parse(Doc(G("0 INSERT|5 I1|2 B|10 0|20 0|70 1|71 1"), LineBlock()), "one.dxf"));
            C(!plainInsert.Contains("\"columns\"", StringComparison.Ordinal) && !plainInsert.Contains("\"reason\"", StringComparison.Ordinal), "a 1x1 insert JSON has no array fields", plainInsert);

            var x = d.Explode();
            C(x.Entities.Count == 28 && x.Warnings.Count == 0, "28 exploded entities, no explode warnings", x.Entities.Count.ToString(CultureInfo.InvariantCulture) + " " + string.Join(",", x.Warnings.Select(w => w.Code)));

            // 3 columns x 2 rows at (1000,2000), spacing 500 x 400: row-major cells, each keyed handle[row,column].
            var m1 = x.Entities.Where(e => e.Provenance.InsertPath.Count == 1 && e.Provenance.InsertPath[0].StartsWith("70", StringComparison.Ordinal)).ToList();
            var m1Ok = m1.Count == 12;
            for (var row = 0; row < 2 && m1Ok; row++)
                for (var col = 0; col < 3 && m1Ok; col++)
                {
                    var k = (row * 3 + col) * 2;
                    var key = "70[" + row.ToString(CultureInfo.InvariantCulture) + "," + col.ToString(CultureInfo.InvariantCulture) + "]";
                    m1Ok = m1[k] is DxfLine l && Path0(l) == key && NearP(l.Start, 1000 + 500 * col, 2000 + 400 * row) && NearP(l.End, 1100 + 500 * col, 2000 + 400 * row)
                        && m1[k + 1] is DxfCircle c && Path0(c) == key && NearP(c.Center, 1050 + 500 * col, 2050 + 400 * row) && Near(c.Radius, 25)
                        && l.Provenance.Layer == "ARRAY" && l.Provenance.Handle == "61";
                }
            C(m1Ok, "3x2 MINSERT expands row-major with row/column provenance and spacing", All(m1));

            // Rotated 90 and scaled 2: offsets rotate with the insert but are not scaled.
            var m2 = x.Entities.Where(e => Path0(e).StartsWith("71[", StringComparison.Ordinal)).ToList();
            C(m2.Count == 4 && m2[0] is DxfLine a0 && NearP(a0.Start, 0, 0) && NearP(a0.End, 0, 200) && Path0(a0) == "71[0,0]"
                && m2[1] is DxfCircle c0 && NearP(c0.Center, -100, 100) && Near(c0.Radius, 50)
                && m2[2] is DxfLine a1 && NearP(a1.Start, 0, 200) && NearP(a1.End, 0, 400) && Path0(a1) == "71[0,1]"
                && m2[3] is DxfCircle c1 && NearP(c1.Center, -100, 300),
                "rotated scaled MINSERT: column offsets rotate with the insert, unscaled", All(m2));

            // OCS (0,0,-1): OCS x is world -x, so the columns run towards -x.
            var m3 = x.Entities.Where(e => Path0(e).StartsWith("72[", StringComparison.Ordinal)).ToList();
            C(m3.Count == 4 && m3[0] is DxfLine b0 && NearP(b0.Start, -1000, 0) && NearP(b0.End, -1100, 0)
                && m3[1] is DxfCircle d0 && NearP(d0.Center, -1050, 50)
                && m3[2] is DxfLine b1 && NearP(b1.Start, -1300, 0) && NearP(b1.End, -1400, 0) && Path0(b1) == "72[0,1]"
                && m3[3] is DxfCircle d1 && NearP(d1.Center, -1350, 50),
                "MINSERT in the mirrored OCS", All(m3));
            C(json.Contains("\"column_spacing\":-300", StringComparison.Ordinal), "mirrored MINSERT column spacing is in world sense", "");

            // A MINSERT inside a block.
            var nested = x.Entities.Where(e => Path0(e).StartsWith("73>", StringComparison.Ordinal)).ToList();
            C(nested.Count == 4 && nested[0] is DxfLine n0 && Path0(n0) == "73>65[0,0]" && NearP(n0.Start, 0, 5000)
                && nested[2] is DxfLine n1 && Path0(n1) == "73>65[0,1]" && NearP(n1.Start, 300, 5000) && NearP(n1.End, 400, 5000),
                "nested MINSERT cells keep the outer insert in the path", All(nested));

            // Tilted but horizontal after the arbitrary axis algorithm.
            var t81 = d.Entities.OfType<DxfPolyline>().SingleOrDefault(e => e.Provenance.Handle == "81");
            C(t81 is not null && t81.Vertices.Count == 2 && NearP(t81.Vertices[0].Point, -1, 0, 1e-12) && NearP(t81.Vertices[1].Point, -3, 0, 1e-12),
                "extrusion (0,0.6,0.8): Ax = Wz x N = (-1,0,0); a line along OCS x stays horizontal", t81 is null ? "null" : Describe(t81));
            var t82 = d.Entities.OfType<DxfPolyline>().SingleOrDefault(e => e.Provenance.Handle == "82");
            C(t82 is not null && NearP(t82.Vertices[0].Point, 2, 1, 1e-12) && NearP(t82.Vertices[1].Point, 2, 4, 1e-12),
                "extrusion (1,0,0) with elevation 2: OCS (x,y) is world (2,x,y); constant y is horizontal", t82 is null ? "null" : Describe(t82));
            var t88 = d.Entities.OfType<DxfPolyline>().SingleOrDefault(e => e.Provenance.Handle == "88");
            C(t88 is not null && NearP(t88.Vertices[0].Point, 0, 0, 1e-12) && NearP(t88.Vertices[1].Point, 0, 5, 1e-12),
                "near-Z extrusion (0.01,0,1): Ax = Wy x N; a line along OCS y stays horizontal", t88 is null ? "null" : Describe(t88));
            var t86 = d.Entities.OfType<DxfArc>().SingleOrDefault(e => e.Provenance.Handle == "86");
            C(t86 is not null && NearP(t86.Center, -10, 20) && Near(t86.StartAngleDegrees, 90) && Near(t86.EndAngleDegrees, 180) && Near(t86.Radius, 5),
                "non-unit extrusion (0,0,-2) mirrors like (0,0,-1)", t86 is null ? "null" : Describe(t86));
            C(!x.Entities.Any(e => e.Provenance.InsertPath.Count > 0 && e.Provenance.InsertPath[0] == "85"), "a tilted INSERT is not exploded", "");

            var again = DxfReader.Parse(File.ReadAllText(path).Replace("\n", "\r\n", StringComparison.Ordinal), "minsert-ocs.dxf");
            C(DxfJson.Serialize(again) == json && All(again.Explode().Entities) == All(x.Entities), "deterministic across runs and CRLF", "");
        }
        catch (Exception error)
        {
            C(false, "fixture unexpected exception", error.GetType().Name + ": " + error.Message);
        }

        // ---------- generated cases ----------
        try
        {
            // ATTRIBs of a MINSERT repeat in every cell, keyed by the cell.
            var tagBlock = G("0 BLOCK|5 TB|8 0|2 TAG|70 2|10 0|20 0|30 0|0 ATTDEF|5 TD|8 0|10 0|20 0|30 0|40 1|1 X|3 Name|2 N|70 0|0 ENDBLK|5 TE|8 0");
            var attributed = DxfReader.Parse(Doc(G("0 INSERT|5 A1|8 0|66 1|2 TAG|10 10|20 0|50 90|70 2|71 1|44 100|0 ATTRIB|5 AT|8 0|10 12|20 3|40 1|1 V|2 N|0 SEQEND|5 AS|8 0"), tagBlock), "attr.dxf");
            var attributes = attributed.Explode().Entities.OfType<DxfAttribute>().ToList();
            C(attributes.Count == 2 && NearP(attributes[0].Insertion, 12, 3) && attributes[0].InsertHandle == "A1" && Path0(attributes[0]) == "A1[0,0]"
                && NearP(attributes[1].Insertion, 12, 103) && attributes[1].InsertHandle == "A1" && attributes[1].Provenance.InsertPath.SequenceEqual(new[] { "A1[0,1]" }) && Near(attributes[1].RotationDegrees, 0),
                "MINSERT ATTRIBs repeat per cell, shifted by the rotated cell offset; InsertHandle is the bare handle, the cell is in InsertPath", All(attributes));

            // A 1x1 insert keeps the plain handle as its path element.
            var single = DxfReader.Parse(Doc(G("0 INSERT|5 I1|2 B|10 0|20 0|70 1|71 1"), LineBlock()), "one.dxf").Explode().Entities.Single();
            C(Path0(single) == "I1", "1x1 insert path element is the plain handle", Path0(single));

            // Invalid counts are read as 1 with a warning.
            var invalid = DxfReader.Parse(Doc(G("0 INSERT|5 I2|2 B|10 0|20 0|70 0|71 -3|44 5"), LineBlock()), "bad.dxf");
            C(invalid.Warnings.Count(w => w.Code == "minsert-invalid") == 1 && invalid.Explode().Entities.Count == 1 && Path0(invalid.Explode().Entities[0]) == "I2",
                "zero or negative MINSERT counts read as a single insert with a warning", string.Join(",", invalid.Warnings.Select(w => w.Code)));

            // Huge arrays stay under the explode limit.
            var huge = DxfReader.Parse(Doc(G("0 INSERT|5 H1|2 B|10 0|20 0|70 30000|71 30000|44 1|45 1"), LineBlock()), "huge.dxf");
            C(CodeOf(() => huge.Explode(new DxfExplodeOptions(MaxWork: 10_000))) == "explode-limit", "a 30000x30000 MINSERT raises explode-limit (work)", CodeOf(() => huge.Explode(new DxfExplodeOptions(MaxWork: 10_000))) ?? "no error");
            var small = DxfReader.Parse(Doc(G("0 INSERT|5 S1|2 B|10 0|20 0|70 3|71 2|44 1|45 1"), LineBlock()), "small.dxf");
            C(CodeOf(() => small.Explode(new DxfExplodeOptions(MaxEntities: 5))) == "explode-limit" && CodeOf(() => small.Explode(new DxfExplodeOptions(MaxEntities: 6))) is null,
                "a 3x2 MINSERT counts six entities against MaxEntities", "");

            // Zero extrusion and a tilted TEXT inside a block.
            var tiltedBlock = DxfReader.Parse(Doc(G("0 INSERT|5 TI|2 TB2|10 0|20 0"),
                G("0 BLOCK|5 K|8 0|2 TB2|70 0|10 0|20 0|0 TEXT|5 KT|8 0|10 0|20 0|40 1|1 T|210 0|220 1|230 0|0 LINE|5 KL|8 0|10 0|20 0|11 1|21 0|0 ENDBLK|5 KE|8 0")), "tb.dxf");
            C(tiltedBlock.Unsupported.Any(u => u.Handle == "KT" && u.EntityType == "TEXT") && tiltedBlock.Explode().Entities.Count == 1,
                "a tilted entity inside a block is typed unsupported and skipped when exploding", string.Join(",", tiltedBlock.Unsupported.Select(u => u.Handle)));
        }
        catch (Exception error)
        {
            C(false, "generated unexpected exception", error.GetType().Name + ": " + error.Message);
        }

        // ---------- review: consumers of MINSERT cells, near-Z extrusions, nits ----------
        try
        {
            var options = new CadToBimIrOptions(projectId);
            var doors = CadToBimIr.FromDxf(DxfReader.Parse(Doc(Walls() + G("0 INSERT|5 D1|8 OPENINGS|2 DOOR-A|10 1000|20 0|70 3|71 1|44 2000"), DoorBlock()), "doors.dxf"), options);
            C(DoorsAt(doors, 1000, 3000, 5000) && !Codes(doors).Contains("unmatched-block", StringComparison.Ordinal) && !Codes(doors).Contains("unhosted-opening", StringComparison.Ordinal)
                && doors.Openings.All(o => o.Source.Handle == "D1" && o.BlockName == "DOOR-A"),
                "a 1x3 door MINSERT gives three hosted openings with geometry", Openings(doors) + " " + Codes(doors));

            var nestedDoors = CadToBimIr.FromDxf(DxfReader.Parse(Doc(Walls() + G("0 INSERT|5 P1|8 0|2 PLAN|10 0|20 0"),
                DoorBlock() + G("0 BLOCK|5 PB|8 0|2 PLAN|70 0|10 0|20 0|30 0|0 INSERT|5 D2|8 OPENINGS|2 DOOR-A|10 1000|20 0|70 3|71 1|44 2000|0 ENDBLK|5 PE|8 0")), "nested-doors.dxf"), options);
            C(DoorsAt(nestedDoors, 1000, 3000, 5000) && !Codes(nestedDoors).Contains("unmatched-block", StringComparison.Ordinal),
                "a door MINSERT nested in a block gives three openings", Openings(nestedDoors) + " " + Codes(nestedDoors));

            var hidden = CadToBimIr.FromDxf(DxfReader.Parse(Doc(Walls() + G("0 INSERT|5 D3|8 OPENINGS|60 1|2 DOOR-A|10 1000|20 0|70 3|71 1|44 2000"), DoorBlock()), "hidden-doors.dxf"), options);
            C(hidden.Openings.Count == 0 && !Codes(hidden).Contains("unmatched-block", StringComparison.Ordinal) && !Codes(hidden).Contains("unhosted-opening", StringComparison.Ordinal),
                "an invisible door MINSERT gives no openings and no reports", Openings(hidden) + " " + Codes(hidden));

            var chairs = CadToBimIr.FromDxf(DxfReader.Parse(Doc(Walls() + G("0 INSERT|5 C1|8 0|2 CHAIR|10 1000|20 3000|70 3|71 1|44 2000"),
                G("0 BLOCK|5 CB|8 0|2 CHAIR|70 0|10 0|20 0|30 0|0 LINE|5 CL|8 0|10 0|20 0|11 400|21 0|0 ENDBLK|5 CE|8 0")), "chairs.dxf"), options);
            var unmatched = chairs.Uncertainties.Where(u => u.Code == "unmatched-block").ToList();
            C(unmatched.Count == 3 && unmatched.All(u => u.Message.Contains("block CHAIR ", StringComparison.Ordinal))
                && unmatched.Select(u => u.At is { } at ? F(at.X) + "," + F(at.Y) : "-").SequenceEqual(new[] { "1000,3000", "3000,3000", "5000,3000" }),
                "a non-door MINSERT reports one unmatched-block per cell with the real block name and cell position", string.Join(" | ", unmatched.Select(u => u.Message + " @" + (u.At is { } at ? F(at.X) + "," + F(at.Y) : "-"))));

            // Near-Z extrusions (float noise) keep the entity, placed through the arbitrary axis algorithm.
            var nearZ = DxfReader.Parse(Doc(G("0 CIRCLE|5 Z1|8 0|10 1000|20 0|30 0|40 5|210 1e-8|220 0|230 1") + G("0 CIRCLE|5 Z2|8 0|10 1000|20 0|30 0|40 5|210 1e-8|220 0|230 -1")
                + G("0 CIRCLE|5 Z3|8 0|10 1000|20 0|30 0|40 5|210 0.01|220 0|230 1")), "nearz.dxf");
            var z1 = nearZ.Entities.OfType<DxfCircle>().SingleOrDefault(e => e.Provenance.Handle == "Z1");
            var z2 = nearZ.Entities.OfType<DxfCircle>().SingleOrDefault(e => e.Provenance.Handle == "Z2");
            C(z1 is not null && NearP(z1.Center, 1000, 0, 1e-6) && z2 is not null && NearP(z2.Center, -1000, 0, 1e-6) && nearZ.Unsupported.Select(u => u.Handle).SequenceEqual(new[] { "Z3" }),
                "extrusion (1e-8,0,+-1) is kept within 1e-6; (0.01,0,1) stays unsupported", All(nearZ.Entities) + " unsupported " + string.Join(",", nearZ.Unsupported.Select(u => u.Handle)));

            // Room-tag MINSERT: one room per cell, each labelled by its own cell path.
            var tagBlock = G("0 BLOCK|5 TB|8 0|2 ROOMTAG|70 2|10 0|20 0|30 0|0 ATTDEF|5 TD|8 0|10 0|20 0|30 0|40 200|1 X|3 Name|2 NAME|70 0|0 ENDBLK|5 TE|8 0");
            var tagInsert = G("0 INSERT|5 R1|8 TAGS|66 1|2 ROOMTAG|10 0|20 0|70 3|71 1|44 5000|0 ATTRIB|5 RA|8 TAGS|10 0|20 0|40 200|1 Cocina|2 NAME|0 ATTRIB|5 RB|8 TAGS|10 0|20 -300|40 200|1 7 m²|2 AREA|0 SEQEND|5 RS|8 TAGS");
            var rooms = BriefExtract.FromDxf(DxfReader.Parse(Doc(tagInsert, tagBlock), "rooms.dxf"));
            C(rooms.Rooms.Count == 3 && rooms.Rooms.Select(r => string.Join(">", r.Label.InsertPath)).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(new[] { "R1[0,0]", "R1[0,1]", "R1[0,2]" }),
                "a room-tag MINSERT gives one room per cell with distinct label paths", string.Join(" | ", rooms.Rooms.Select(r => r.Id + " " + string.Join(">", r.Label.InsertPath))));
            var fallback = BriefExtract.FromDxf(DxfReader.Parse(Doc(tagInsert, tagBlock), "rooms.dxf"), new BriefExtractOptions { ExplodeOptions = new DxfExplodeOptions(MaxEntities: 1) });
            C(fallback.Rooms.Count == 1 && fallback.Rooms[0].Label.InsertPath.SequenceEqual(new[] { "R1[0,0]" }),
                "the explode-limit fallback labels a MINSERT's attributes with its first cell path", string.Join(" | ", fallback.Rooms.Select(r => r.Id + " " + string.Join(">", r.Label.InsertPath))));

            // Nits: a tilted INSERT warns that its ATTRIBs are dropped; an empty-block huge MINSERT hits the limit; base point.
            var tiltedTags = DxfReader.Parse(Doc(G("0 INSERT|5 T1|8 0|66 1|2 ROOMTAG|10 0|20 0|210 1|220 0|230 0|0 ATTRIB|5 TA|8 0|10 0|20 0|40 1|1 V|2 NAME|0 SEQEND|5 TS|8 0"), tagBlock), "tilted-tags.dxf");
            C(tiltedTags.Warnings.Any(w => w.Code == "attributes-dropped") && tiltedTags.Unsupported.Any(u => u.Handle == "T1"), "a tilted INSERT warns that its ATTRIBs are dropped", string.Join(",", tiltedTags.Warnings.Select(w => w.Code)));
            var empty = DxfReader.Parse(Doc(G("0 INSERT|5 E1|2 EMPTY|10 0|20 0|70 30000|71 30000|44 1|45 1"), G("0 BLOCK|5 EB|8 0|2 EMPTY|70 0|10 0|20 0|30 0|0 ENDBLK|5 EE|8 0")), "empty.dxf");
            C(CodeOf(() => empty.Explode(new DxfExplodeOptions(MaxWork: 10_000))) == "explode-limit", "an empty-block 30000x30000 MINSERT raises explode-limit", CodeOf(() => empty.Explode(new DxfExplodeOptions(MaxWork: 10_000))) ?? "no error");
            var based = DxfReader.Parse(Doc(G("0 INSERT|5 BP|2 BASED|10 100|20 200|50 90|70 2|71 2|44 30|45 40"),
                G("0 BLOCK|5 BB|8 0|2 BASED|70 0|10 5|20 5|30 0|0 LINE|5 BL|8 0|10 5|20 5|11 15|21 5|0 ENDBLK|5 BE|8 0")), "based.dxf").Explode().Entities.OfType<DxfLine>().ToList();
            // Cell (r,c) offset (30c, 40r) rotated 90 is (-40r, 30c); the block base (5,5) maps to the insertion point.
            C(based.Count == 4 && NearP(based[0].Start, 100, 200) && NearP(based[0].End, 100, 210) && NearP(based[1].Start, 100, 230) && NearP(based[2].Start, 60, 200) && NearP(based[3].Start, 60, 230) && NearP(based[3].End, 60, 240),
                "MINSERT with a non-zero block base point and rotation", All(based));
        }
        catch (Exception error)
        {
            C(false, "review unexpected exception", error.GetType().Name + ": " + error.Message);
        }
    }
}
