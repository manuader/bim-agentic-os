using Bim.Cad;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BriefSerializer = Bim.Core.BriefSerializer;
using Point2 = Bim.Cad.Point2;

// P6-30 block regions under a general extrusion normal, and an ezdxf cross-check of MINSERT cell placement.
//
// Documented choice for a non-axis extrusion normal (pinned here): an INSERT or MINSERT whose extrusion N is not
// parallel to Z (e.g. (0.6,0,0.8) or (0.01,0,1)) is NOT projected to world XY. The rejection depends only on the
// horizontal part of N exceeding 1e-6, not on which arbitrary-axis branch applies; the 1/64 branch (Ax = Wy x N) is
// covered by the OcsFrame axes check against ezdxf and by the placed near-Z insert N1. DxfReader reports it: one ocs-non-planar warning at the entity's line and a typed
// Unsupported entry (handle, INSERT, Reason "ocs-non-planar"); DxfExploder places none of its cells, so BriefExtract
// with IncludeBlockRegions takes no region from it (a label at the XY centre the projection reading would give gets no
// region and an area-missing uncertainty). A normal within 1e-6 of +-Z (here (2e-7,0,-1), also the 1/64 branch) is
// kept and placed through the computed arbitrary axes, including the elevation (group 30) times N.
//
// ezdxf cross-check: tests/cad/tools/minsert_ezdxf_centres.py (offline only, never run in CI) builds the same block
// and inserts with ezdxf 1.4.2 (MIT; tag v1.4.2, commit 8240df5793daf2fd7db95d118a78e17ec92043ec, imported from a
// source checkout under Python 3 with numpy 2.5.3) and prints, per MINSERT cell (Insert.multi_insert, row-major), the
// four WCS corners of the cell's outline (virtual_entities + ezdxf.path.make_path) and their mean; and the OCS axes
// ezdxf.math.OCS gives for the normals used. Those values are the constants below; DxfReader + DxfExploder, and
// OcsFrame, must equal them within 1e-6. Cases: R1 rotated 30 scaled 2x1.5; S1 rotated 135 with mirroring X scale
// -1.25 and negative row spacing; M1 in the mirrored OCS (0,0,-1) rotated 45; N1 near-Z mirrored (2e-7,0,-1) at
// elevation 500 rotated 60, Y scale 2. Block RECT has base point (100,50). Unit-tested evidence only.
//
// P6-32 (sections RunBulge and RunCli below):
//   * Bulge outline and nested chains under near +-Z normals: BulgeDrawing() (block ARCH, base (50,20), closed
//     LWPOLYLINE with bulges 0.4 and -0.25; block NEST, base (10,10), holding INSERT NA of ARCH and INSERT NB of ARCH
//     under near -Z (1e-7,0,-1)) placed through B1 (ARCH under (2e-7,-1e-7,-1), elevation 300, X scale -2, 40 deg),
//     C1 (NEST under near +Z (-3e-7,4e-7,1), 15 deg, scale 0.8) and MINSERT D1 (NEST under (0,2e-7,-1), -30 deg, two
//     columns 9000 apart). Each of the seven outlines' WCS corners and segment midpoints (the arc midpoint for a bulged
//     segment, so bulge sign and size are both checked) equal ezdxf 1.4.2 (same offline generator, P6-32 section)
//     within 1e-6; the bulges survive (no arc-tessellated warning) and flip sign under the mirroring placements.
//   * cad-brief CLI pin: the P6-30 Drawing() through the real Release CLI (interfaces/cli Bim.Cli.dll, built by
//     scripts/verify.ps1 before tests/cad runs) with and without --include-block-regions writes exactly the bytes of the
//     same document built in process (BriefExtract.FromDxf, BriefSerializer, the CLI's JSON options); both digests pinned.
//   * Documented limit (pinned): the dropped tilted inserts' ocs-non-planar reader warnings are NOT in the brief's
//     uncertainty list. BriefExtract reports only explode warnings, never DxfDrawing.Warnings/Unsupported, and adding
//     them would change existing brief bytes: the checked-in minsert-ocs.dxf already has a dropped INSERT (handle 85,
//     extrusion (0,1,0), ocs-non-planar) and its BriefExtract output is pinned byte for byte (BriefNonAsciiTests digest
//     799C995C1C1F14A7, re-checked here). Callers read the drop from DxfDrawing.Warnings and DxfDrawing.Unsupported.
internal static class MinsertEzdxfTests
{
    private const double Eps = 1e-6;
    private const string FileName = "minsert-ezdxf.dxf";

    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static bool Near(double a, double b) => Math.Abs(a - b) <= Eps;
    private static string P(Point2 p) => "(" + F(p.X) + "," + F(p.Y) + ")";
    private static string G(string spec) => string.Concat(spec.Split('|').Select(item => { var space = item.IndexOf(' ', StringComparison.Ordinal); return item[..space] + "\n" + item[(space + 1)..] + "\n"; }));
    private static string Text(string handle, double x, double y, string value) => G("0 TEXT|5 " + handle + "|8 ROOM-TEXT|10 " + F(x) + "|20 " + F(y) + "|40 200|1 " + value);

    // Same parameters as CASES and TILTED in tests/cad/tools/minsert_ezdxf_centres.py.
    private static string Drawing() =>
        G("0 SECTION|2 HEADER|9 $INSUNITS|70 4|0 ENDSEC")
        + G("0 SECTION|2 BLOCKS|0 BLOCK|5 B1|8 0|2 RECT|70 0|10 100|20 50|30 0")
        + G("0 LWPOLYLINE|5 B2|8 0|90 4|70 1|10 100|20 50|10 2100|20 50|10 2100|20 1550|10 100|20 1550")
        + G("0 ENDBLK|5 B3|8 0|0 ENDSEC")
        + G("0 SECTION|2 ENTITIES")
        + G("0 INSERT|5 R1|8 ROOMS|2 RECT|10 1000|20 2000|30 0|41 2|42 1.5|50 30|70 3|71 2|44 5000|45 3000")
        + G("0 INSERT|5 S1|8 ROOMS|2 RECT|10 40000|20 2000|30 0|41 -1.25|42 0.75|50 135|70 2|71 3|44 3500|45 -2000")
        + G("0 INSERT|5 M1|8 ROOMS|2 RECT|10 -80000|20 2000|30 0|41 1.5|42 1|50 45|70 3|71 2|44 4000|45 2500|210 0|220 0|230 -1")
        + G("0 INSERT|5 N1|8 ROOMS|2 RECT|10 -120000|20 2000|30 500|41 1|42 2|50 60|70 2|71 2|44 3000|45 4000|210 2e-7|220 0|230 -1")
        + G("0 INSERT|5 T1|8 ROOMS|2 RECT|10 10000|20 50000|30 0|210 0.6|220 0|230 0.8")
        + G("0 INSERT|5 T2|8 ROOMS|2 RECT|10 20000|20 60000|30 0|50 30|70 2|71 2|44 4000|45 3000|210 0.6|220 0|230 0.8")
        + G("0 INSERT|5 T3|8 ROOMS|2 RECT|10 160000|20 40000|30 0|70 2|71 2|44 4000|45 3000|210 0.01|220 0|230 1")
        // Labels at ezdxf cell centres of placed cells, and decoys at the XY centres the projection reading of the
        // tilted inserts would give.
        + Text("L1", 9329.804845413264, 11572.354790610807, "Comedor")
        + Text("L2", 43314.56303681194, 3546.796083845572, "Oficina")
        + Text("L3", 78409.00974233028, 8187.184335382291, "Estudio")
        + Text("L4", 119299.03820567428, 6214.101615137755, "Lavadero")
        + Text("L5", -40600.0, 11000.0, "Despensa")
        + Text("L6", -48919.615242270665, 20491.02540378444, "Deposito")
        + Text("L7", 164991.75061869854, 43750.0, "Baulera")
        + G("0 ENDSEC|0 EOF");

    // ezdxf 1.4.2 cell outlines: (insert path, centre x, centre y, corners x0,y0..x3,y3 in the block's vertex order).
    private static readonly (string Path, double X, double Y, double[] Corners)[] Cells =
    {
        ("R1[0,0]", 2169.5508075688776, 3974.278579257493, new[] { 1000.0, 2000.0, 4464.101615137754, 3999.999999999999, 3339.1016151377553, 5948.557158514986, -124.99999999999977, 3948.5571585149864 }),
        ("R1[0,1]", 6499.677826491071, 6474.278579257493, new[] { 5330.127018922193, 4500.0, 8794.228634059948, 6499.999999999999, 7669.2286340599485, 8448.557158514986, 4205.127018922194, 6448.557158514986 }),
        ("R1[0,2]", 10829.804845413262, 8974.278579257492, new[] { 9660.254037844386, 6999.999999999999, 13124.35565298214, 8999.999999999998, 11999.35565298214, 10948.557158514985, 8535.254037844386, 8948.557158514985 }),
        ("R1[1,0]", 669.5508075688781, 6572.35479061081, new[] { -499.9999999999994, 4598.0762113533165, 2964.1016151377553, 6598.076211353316, 1839.1016151377557, 8546.633369868303, -1624.999999999999, 6546.633369868303 }),
        ("R1[1,1]", 4999.677826491072, 9072.35479061081, new[] { 3830.1270189221937, 7098.0762113533165, 7294.2286340599485, 9098.076211353316, 6169.228634059949, 11046.633369868303, 2705.127018922194, 9046.633369868303 }),
        ("R1[1,2]", 9329.804845413264, 11572.354790610807, new[] { 8160.254037844386, 9598.076211353315, 11624.355652982142, 11598.076211353313, 10499.355652982142, 13546.633369868301, 7035.254037844386, 11546.633369868301 }),
        ("S1[0,0]", 40486.13591206575, 718.3689590993824, new[] { 40000.0, 1999.9999999999998, 41767.76695296637, 232.233047033631, 40972.2718241315, -563.2620818012351, 39204.50487116513, 1204.5048711651339 }),
        ("S1[0,1]", 38011.26217791283, 3193.2426932522985, new[] { 37525.12626584708, 4474.873734152916, 39292.89321881345, 2707.1067811865473, 38497.39808997858, 1911.6116523516812, 36729.63113701221, 3679.37860531805 }),
        ("S1[1,0]", 41900.349474438844, 2132.5825214724773, new[] { 41414.213562373094, 3414.2135623730946, 43181.98051533946, 1646.4466094067257, 42386.485386504595, 850.9514805718595, 40618.718433538226, 2618.7184335382285 }),
        ("S1[1,1]", 39425.47574028593, 4607.456255625394, new[] { 38939.33982822018, 5889.087296526011, 40707.10678118655, 4121.320343559642, 39911.61165235168, 3325.8252147247767, 38143.844699385314, 5093.592167691146 }),
        ("S1[2,0]", 43314.56303681194, 3546.796083845572, new[] { 42828.42712474619, 4828.427124746189, 44596.19407771256, 3060.6601717798208, 43800.69894887769, 2265.1650429449546, 42032.93199591132, 4032.9319959113236 }),
        ("S1[2,1]", 40839.689302659026, 6021.669817998489, new[] { 40353.553390593275, 7303.300858899106, 42121.320343559644, 5535.533905932738, 41325.82521472478, 4740.038777097871, 39558.05826175841, 6507.80573006424 }),
        ("M1[0,0]", 79469.6699141101, 3590.990257669732, new[] { 80000.0, 2000.0, 77878.67965644036, 4121.320343559642, 78939.33982822018, 5181.980515339464, 81060.66017177983, 3060.660171779821 }),
        ("M1[0,1]", 76641.24278936391, 6419.417382415921, new[] { 77171.57287525381, 4828.427124746189, 75050.25253169418, 6949.747468305832, 76110.912703474, 8010.407640085653, 78232.23304703365, 5889.0872965260105 }),
        ("M1[0,2]", 73812.81566461772, 9247.844507162112, new[] { 74343.14575050763, 7656.8542494923795, 72221.82540694799, 9778.174593052023, 73282.4855787278, 10838.834764831843, 75403.80592228746, 8717.5144212722 }),
        ("M1[1,0]", 81237.43686707647, 5358.7572106361, new[] { 81767.76695296637, 3767.766952966369, 79646.44660940673, 5889.087296526011, 80707.10678118655, 6949.747468305833, 82828.4271247462, 4828.42712474619 }),
        ("M1[1,1]", 78409.00974233028, 8187.184335382291, new[] { 78939.33982822018, 6596.194077712559, 76818.01948466054, 8717.5144212722, 77878.67965644036, 9778.174593052023, 80000.00000000001, 7656.85424949238 }),
        ("M1[1,2]", 75580.58261758409, 11015.611460128479, new[] { 76110.912703474, 9424.621202458748, 73989.59235991436, 11545.94154601839, 75050.25253169418, 12606.60171779821, 77171.57287525383, 10485.281374238568 }),
        ("N1[0,0]", 120799.03820567425, 3616.025403784439, new[] { 120000.0000999976, 2000.0, 119000.00009999763, 3732.050807568877, 121598.0763113509, 5232.050807568878, 122598.07631135089, 3500.0000000000005 }),
        ("N1[0,1]", 119299.03820567428, 6214.101615137755, new[] { 118500.00009999763, 4598.0762113533165, 117500.00009999766, 6330.127018922193, 120098.07631135092, 7830.127018922194, 121098.07631135092, 6098.0762113533165 }),
        ("N1[1,0]", 124263.13982081194, 5616.025403784441, new[] { 123464.10171513529, 4000.0000000000014, 122464.10171513532, 5732.050807568879, 125062.17792648857, 7232.0508075688795, 126062.17792648857, 5500.000000000002 }),
        ("N1[1,1]", 122763.13982081198, 8214.101615137755, new[] { 121964.10171513536, 6598.0762113533165, 120964.10171513536, 8330.127018922194, 123562.17792648861, 9830.127018922194, 124562.17792648861, 8098.0762113533165 }),
    };

    // ezdxf.math.OCS axes (ux, uy) of the normals used.
    private static readonly (double[] Normal, double[] Ax, double[] Ay)[] Axes =
    {
        (new[] { 0.6, 0.0, 0.8 }, new[] { 0.0, 1.0, 0.0 }, new[] { -0.8, 0.0, 0.6 }),
        (new[] { 0.01, 0.0, 1.0 }, new[] { 0.9999500037496877, 0.0, -0.009999500037496877 }, new[] { -0.0, 1.0, 0.0 }),
        (new[] { 2e-7, 0.0, -1.0 }, new[] { -0.99999999999998, 0.0, -1.99999999999996e-07 }, new[] { 0.0, 1.0, 0.0 }),
        (new[] { 0.0, 0.0, -1.0 }, new[] { -1.0, 0.0, 0.0 }, new[] { 0.0, 1.0, 0.0 }),
    };

    // Shoelace area in m2 of millimetre corners (independent of the extractor).
    private static double Area(double[] c)
    {
        double twice = 0;
        for (var i = 0; i < 4; i++) twice += c[2 * i] * c[(2 * i + 3) % 8] - c[(2 * i + 2) % 8] * c[2 * i + 1];
        return Math.Abs(twice) / 2 * 1e-6;
    }

    public static void Run(Action<bool, string, string> check)
    {
        void C(bool condition, string name, string detail = "") => check(condition, "minsert-ezdxf " + name, detail);
        var bytes = new UTF8Encoding(false).GetBytes(Drawing());

        // ---------- OcsFrame equals ezdxf's arbitrary axis algorithm ----------
        try
        {
            foreach (var (n, ax, ay) in Axes)
            {
                var record = new DxfRecord("INSERT", 1, new List<DxfPair> { new(210, F(n[0]), 1), new(220, F(n[1]), 1), new(230, F(n[2]), 1) });
                var frame = OcsFrame.Read(record);
                var got = "Ax (" + F(frame.AxisX.X) + "," + F(frame.AxisX.Y) + "," + F(frame.AxisX.Z) + ") Ay (" + F(frame.AxisY.X) + "," + F(frame.AxisY.Y) + "," + F(frame.AxisY.Z) + ") kind " + frame.Kind;
                var ok = Near(frame.AxisX.X, ax[0]) && Near(frame.AxisX.Y, ax[1]) && Near(frame.AxisX.Z, ax[2])
                    && Near(frame.AxisY.X, ay[0]) && Near(frame.AxisY.Y, ay[1]) && Near(frame.AxisY.Z, ay[2]);
                var horizontal = Math.Abs(n[0]) <= 1e-6 && Math.Abs(n[1]) <= 1e-6;
                C(ok && frame.IsHorizontal == horizontal,
                    "OCS axes of N (" + F(n[0]) + "," + F(n[1]) + "," + F(n[2]) + ") equal ezdxf within 1e-6 and the frame is " + (horizontal ? "horizontal" : "tilted"), got);
            }
        }
        catch (Exception error)
        {
            C(false, "OCS unexpected exception", error.GetType().Name + ": " + error.Message);
        }

        // ---------- reader: tilted inserts reported, placed inserts kept ----------
        // T1/T2/T3 are rejected because N's horizontal part exceeds 1e-6 (whatever the axis branch); the 1/64 branch
        // itself is covered by the OcsFrame axes check above and by N1's cells below.
        DxfDrawing? drawing = null;
        try
        {
            drawing = DxfReader.Parse(bytes, FileName);
            var warnings = string.Join(",", drawing.Warnings.Select(w => w.Code + "@" + w.Line.ToString(CultureInfo.InvariantCulture)));
            var unsupported = string.Join(",", drawing.Unsupported.Select(u => u.Handle + ":" + u.EntityType + ":" + u.Reason));
            C(drawing.Warnings.Count == 3 && drawing.Warnings.All(w => w.Code == "ocs-non-planar")
                    && unsupported == "T1:INSERT:ocs-non-planar,T2:INSERT:ocs-non-planar,T3:INSERT:ocs-non-planar"
                    && drawing.Warnings.Select(w => w.Line).SequenceEqual(drawing.Unsupported.Select(u => u.Line)),
                "the INSERT and MINSERTs under (0.6,0,0.8) and (0.01,0,1) are reported (ocs-non-planar warning + typed unsupported at their lines), not projected",
                "warnings " + warnings + " unsupported " + unsupported);
            var inserts = string.Join(",", drawing.Entities.OfType<DxfInsert>().Select(i => i.Provenance.Handle));
            C(inserts == "R1,S1,M1,N1", "the axis-aligned and near-Z inserts are kept", inserts);
        }
        catch (Exception error)
        {
            C(false, "reader unexpected exception", error.GetType().Name + ": " + error.Message);
        }

        // ---------- exploder: every cell equals ezdxf within 1e-6 ----------
        try
        {
            if (drawing is null) throw new InvalidOperationException("no drawing");
            var x = drawing.Explode();
            var outlines = x.Entities.OfType<DxfPolyline>().ToList();
            C(x.Warnings.Count == 0 && outlines.Count == Cells.Length && x.Entities.Count == Cells.Length + 7,
                "22 cell outlines and 7 labels, no explode warnings; nothing from T1/T2/T3",
                outlines.Count.ToString(CultureInfo.InvariantCulture) + " outlines, " + x.Entities.Count.ToString(CultureInfo.InvariantCulture) + " entities, warnings " + string.Join(",", x.Warnings.Select(w => w.Code))
                + " paths " + string.Join(",", outlines.Select(o => string.Join(">", o.Provenance.InsertPath))));
            C(!x.Entities.Any(e => e.Provenance.InsertPath.Any(p => p.StartsWith('T'))), "no exploded entity comes from a tilted insert", "");
            foreach (var (path, cx, cy, corners) in Cells)
            {
                var cell = outlines.Where(o => o.Provenance.InsertPath.Count == 1 && o.Provenance.InsertPath[0] == path).ToList();
                if (cell.Count != 1 || cell[0].Vertices.Count != 4)
                {
                    C(false, path + " outline equals ezdxf", "found " + cell.Count.ToString(CultureInfo.InvariantCulture) + " outlines");
                    continue;
                }
                var v = cell[0].Vertices.Select(vertex => vertex.Point).ToList();
                var mean = new Point2(v.Average(p => p.X), v.Average(p => p.Y));
                var cornersOk = Enumerable.Range(0, 4).All(i => Near(v[i].X, corners[2 * i]) && Near(v[i].Y, corners[2 * i + 1]));
                var worst = Enumerable.Range(0, 4).Max(i => Math.Max(Math.Abs(v[i].X - corners[2 * i]), Math.Abs(v[i].Y - corners[2 * i + 1])));
                C(Near(mean.X, cx) && Near(mean.Y, cy) && cornersOk && cell[0].Closed,
                    path + " cell centre (" + F(cx) + "," + F(cy) + ") and corners equal ezdxf 1.4.2 within 1e-6",
                    "DxfExploder centre " + P(mean) + " corners " + string.Join(";", v.Select(P)) + " max corner error " + F(worst));
            }
        }
        catch (Exception error)
        {
            C(false, "exploder unexpected exception", error.GetType().Name + ": " + error.Message);
        }

        // ---------- BriefExtract block regions ----------
        try
        {
            if (drawing is null) throw new InvalidOperationException("no drawing");
            var draft = BriefExtract.FromDxf(drawing, new BriefExtractOptions { IncludeBlockRegions = true });
            string Show(BriefRoomProposal? room) => room is null ? "null" : room.Id + " region " + (room.Region is { } r ? string.Join(">", r.InsertPath) + " at " + P(r.At) : "none") + " area " + (room.RegionAreaSquareMetres is { } a ? F(a) : "none");
            foreach (var (id, path) in new[] { ("comedor", "R1[1,2]"), ("oficina", "S1[2,0]"), ("estudio", "M1[1,1]"), ("lavadero", "N1[0,1]") })
            {
                var expected = Cells.Single(c => c.Path == path);
                var area = Area(expected.Corners);
                var room = draft.Rooms.FirstOrDefault(r => r.Id == id);
                C(room is { Region: { } region } && region.InsertPath.SequenceEqual(new[] { path }) && Near(region.At.X, expected.X) && Near(region.At.Y, expected.Y)
                        && room.RegionAreaSquareMetres is { } m2 && m2 > 0 && Near(m2, area),
                    id + " takes the region of cell " + path + ": centre and area " + F(area) + " m2 of the ezdxf outline",
                    Show(room));
            }
            foreach (var id in new[] { "despensa", "deposito", "baulera" })
            {
                var room = draft.Rooms.FirstOrDefault(r => r.Id == id);
                C(room is { Region: null, RegionAreaSquareMetres: null } && draft.Uncertainties.Any(u => u.Code == "area-missing" && u.RoomId == id),
                    id + " (label at the XY projection of a tilted insert) gets no region and an area-missing uncertainty",
                    Show(room) + " uncertainties " + string.Join(",", draft.Uncertainties.Where(u => u.RoomId == id).Select(u => u.Code)));
            }
            var without = BriefExtract.FromDxf(drawing);
            C(without.Rooms.Count == 7 && without.Rooms.All(r => r.Region is null), "without IncludeBlockRegions no room has a region",
                string.Join(" | ", without.Rooms.Select(Show)));
        }
        catch (Exception error)
        {
            C(false, "brief unexpected exception", error.GetType().Name + ": " + error.Message);
        }

        RunBulge(C);
        RunCli(C, bytes);
    }

    // ---------- P6-32: bulge outline and nested chains under near +-Z normals ----------

    // Same parameters as BULGE_* and NEST_INNER in tests/cad/tools/minsert_ezdxf_centres.py.
    private static string BulgeDrawing() =>
        G("0 SECTION|2 HEADER|9 $INSUNITS|70 4|0 ENDSEC")
        + G("0 SECTION|2 BLOCKS|0 BLOCK|5 A0|8 0|2 ARCH|70 0|10 50|20 20|30 0")
        + G("0 LWPOLYLINE|5 A1|8 0|90 4|70 1|10 50|20 20|10 1050|20 20|42 0.4|10 1050|20 820|10 50|20 820|42 -0.25")
        + G("0 ENDBLK|5 A2|8 0")
        + G("0 BLOCK|5 N0|8 0|2 NEST|70 0|10 10|20 10|30 0")
        + G("0 INSERT|5 NA|8 0|2 ARCH|10 300|20 200|30 0|41 1.5|42 1.5|50 25")
        + G("0 INSERT|5 NB|8 0|2 ARCH|10 -2000|20 1500|30 0|41 1.25|42 1.25|50 70|210 1e-7|220 0|230 -1")
        + G("0 ENDBLK|5 N1|8 0|0 ENDSEC")
        + G("0 SECTION|2 ENTITIES")
        + G("0 INSERT|5 B1|8 ROOMS|2 ARCH|10 5000|20 3000|30 300|41 -2|42 2|50 40|210 2e-7|220 -1e-7|230 -1")
        + G("0 INSERT|5 C1|8 ROOMS|2 NEST|10 60000|20 5000|30 0|41 0.8|42 0.8|50 15|210 -3e-7|220 4e-7|230 1")
        + G("0 INSERT|5 D1|8 ROOMS|2 NEST|10 -30000|20 8000|30 0|41 1|42 1|50 -30|70 2|71 1|44 9000|45 0|210 0|220 2e-7|230 -1")
        + G("0 ENDSEC|0 EOF");

    // ezdxf 1.4.2 outlines: (insert path, WCS corners x0,y0..x3,y3 in vertex order, WCS segment midpoints x0,y0..x3,y3
    // with segment k from corner k to corner k+1, the closing segment last). Under C1 ezdxf exploded NB itself (it could
    // not transform the nested near -Z INSERT as a whole); the outline is the same geometry.
    private static readonly (string Path, double[] Corners, double[] Midpoints)[] BulgeOutlines =
    {
        ("B1", new[] { -4999.99993999984, 2999.999969999986, -3467.911053761941, 1714.4247506269135, -2439.450878263473, 2940.0958596172723, -3971.5397645013722, 4225.6710789903445 }, new[] { -4233.95549688089, 2357.2123603134496, -2708.546744214643, 2121.568270022401, -3205.495321382423, 3582.8834693038084, -4332.560963626816, 3484.278002557858 }),
        ("C1>NA", new[] { 60184.75429684144, 5206.866744059306, 61104.007628584266, 5978.211875683091, 60486.931523285304, 6713.614541077251, 59567.67819154249, 5942.269409453465 }, new[] { 60644.38096271285, 5592.539309871199, 60942.55010901364, 6469.328429439978, 60027.304857413896, 6327.9419752653575, 59968.141577366245, 5651.702589918764 }),
        ("C1>NB", new[] { 61229.24161368798, 6563.423504739337, 60655.66517733707, 7382.575549028245, 61310.98681276826, 7841.4366981090825, 61884.56324911916, 7022.284653820174 }, new[] { 60942.45339551252, 6972.999526883791, 60891.55376523652, 7743.070450654889, 61597.77503094371, 7431.860675964628, 61499.54478776848, 6874.769283708647 }),
        ("D1[0,0]>NA", new[] { 29653.85263290251, 8019.544826718887, 28159.56058576489, 7888.811212597402, 28054.973694467702, 9084.244850307474, 29549.26574160532, 9214.978464428956 }, new[] { 28906.7066093337, 7954.178019658144, 27868.18041257428, 8465.610653193, 28802.119718036512, 9149.611657368216, 29452.129982540155, 8604.188284161772 }),
        ("D1[0,0]>NB", new[] { 27531.609446468967, 8295.377851638696, 27314.549224385304, 9526.38754290392, 26329.7414713731, 9352.739365237017, 26546.80169345676, 8121.729673971789 }, new[] { 27423.079335427137, 8910.882697271307, 26787.415712345817, 9636.525004672905, 26438.27158241493, 8737.234519604404, 27017.499547754494, 8331.654731931765 }),
        ("D1[0,1]>NA", new[] { 21859.623998842562, 3519.5448267189745, 20365.331951704942, 3388.81121259749, 20260.745060407753, 4584.244850307561, 21755.03710754537, 4714.978464429045 }, new[] { 21112.477975273752, 3454.178019658232, 20073.95177851433, 3965.610653193088, 21007.89108397656, 4649.611657368303, 21657.901348480205, 4104.188284161861 }),
        ("D1[0,1]>NB", new[] { 19737.380812409025, 3795.3778516387865, 19520.32059032536, 5026.387542904012, 18535.512837313156, 4852.739365237108, 18752.57305939682, 3621.729673971881 }, new[] { 19628.85070136719, 4410.8826972714, 18993.187078285868, 5136.525004672995, 18644.04294835499, 4237.234519604494, 19223.270913694552, 3831.654731931857 }),
    };

    // The midpoint of the segment from a to b with bulge (tan of a quarter of the included angle, positive
    // counter-clockwise): the chord midpoint moved by bulge/2 times the chord turned clockwise.
    private static Point2 SegmentMidpoint(Point2 a, Point2 b, double bulge) =>
        new((a.X + b.X) / 2 + bulge / 2 * (b.Y - a.Y), (a.Y + b.Y) / 2 - bulge / 2 * (b.X - a.X));

    private static void RunBulge(Action<bool, string, string> c)
    {
        try
        {
            var drawing = DxfReader.Parse(new UTF8Encoding(false).GetBytes(BulgeDrawing()), "bulge-ezdxf.dxf");
            var x = drawing.Explode();
            var outlines = x.Entities.OfType<DxfPolyline>().ToList();
            var paths = string.Join(",", outlines.Select(o => string.Join(">", o.Provenance.InsertPath)));
            c(drawing.Warnings.Count == 0 && drawing.Unsupported.Count == 0 && x.Warnings.Count == 0 && outlines.Count == BulgeOutlines.Length
                    && paths == string.Join(",", BulgeOutlines.Select(o => o.Path)),
                "bulge: near +-Z inserts B1, C1, D1 and nested NB are kept; seven outlines, bulges not tessellated",
                "reader warnings " + string.Join(",", drawing.Warnings.Select(w => w.Code)) + " explode warnings " + string.Join(",", x.Warnings.Select(w => w.Code)) + " paths " + paths);
            foreach (var (path, corners, midpoints) in BulgeOutlines)
            {
                var outline = outlines.Where(o => string.Join(">", o.Provenance.InsertPath) == path).ToList();
                if (outline.Count != 1 || outline[0].Vertices.Count != 4 || !outline[0].Closed)
                {
                    c(false, "bulge: " + path + " outline equals ezdxf", "found " + outline.Count.ToString(CultureInfo.InvariantCulture) + " outlines");
                    continue;
                }
                var v = outline[0].Vertices;
                var mids = Enumerable.Range(0, 4).Select(i => SegmentMidpoint(v[i].Point, v[(i + 1) % 4].Point, v[i].Bulge)).ToList();
                var worst = Enumerable.Range(0, 4).Max(i => new[]
                {
                    Math.Abs(v[i].Point.X - corners[2 * i]), Math.Abs(v[i].Point.Y - corners[2 * i + 1]),
                    Math.Abs(mids[i].X - midpoints[2 * i]), Math.Abs(mids[i].Y - midpoints[2 * i + 1]),
                }.Max());
                c(worst <= Eps && v[0].Bulge == 0 && v[2].Bulge == 0 && v[1].Bulge != 0 && v[3].Bulge != 0,
                    "bulge: " + path + " corners and segment (arc) midpoints equal ezdxf 1.4.2 within 1e-6",
                    "corners " + string.Join(";", v.Select(p => P(p.Point))) + " bulges " + string.Join(";", v.Select(p => F(p.Bulge)))
                    + " midpoints " + string.Join(";", mids.Select(P)) + " max error " + F(worst));
            }
            // Bulge sign: B1 is mirrored twice (near -Z and X scale -2), so it keeps 0.4/-0.25; NB under C1 (near +Z) is
            // mirrored once (its own near -Z) and flips; NA under D1 (near -Z) flips; NB under D1 is mirrored twice.
            string Bulges(string path) => string.Join("/", outlines.Single(o => string.Join(">", o.Provenance.InsertPath) == path).Vertices.Where(p => p.Bulge != 0).Select(p => F(p.Bulge)));
            var signs = string.Join(" ", new[] { "B1", "C1>NA", "C1>NB", "D1[0,0]>NA", "D1[0,0]>NB" }.Select(p => p + "=" + Bulges(p)));
            c(signs == "B1=0.4/-0.25 C1>NA=0.4/-0.25 C1>NB=-0.4/0.25 D1[0,0]>NA=-0.4/0.25 D1[0,0]>NB=0.4/-0.25",
                "bulge: the bulge sign follows the parity of the mirrors in the chain", signs);
        }
        catch (Exception error)
        {
            c(false, "bulge unexpected exception", error.GetType().Name + ": " + error.Message);
        }
    }

    // ---------- P6-32: cad-brief CLI pin and the dropped inserts' reader warnings ----------

    private static readonly JsonSerializerOptions CliJson = new() { Converters = { new JsonStringEnumConverter() } };

    // First 16 hex digits of the SHA-256 of the cad-brief document of Drawing() (with / without the flag), and the
    // in-process uncertainty codes; measured in CI by the P6-32 probe run 44825236825 (identical on Linux and Windows).
    private const string PinnedWithFlag = "8411ECC467B1097C";
    private const string PinnedWithout = "A8F37BBE600200F5";
    private const string PinnedCodesWithFlag = "area-from-regionx4,area-missingx3,unlabelled-regionx18";
    private const string PinnedCodesWithout = "area-missingx7";

    private static string Hash16(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16];

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
        throw new DirectoryNotFoundException("repository root with AGENTS.md not found above " + AppContext.BaseDirectory);
    }

    // The document CadCommand writes for a DXF cad-brief without a scale option (interfaces/cli/CadCommand.cs), built in
    // process with the same members, order and JSON options.
    private static byte[] InProcessDocument(byte[] bytes, string file, bool includeBlockRegions)
    {
        var drawing = DxfReader.Parse(bytes, file, new DxfReadOptions(32 * 1024 * 1024));
        var result = BriefExtract.FromDxf(drawing, new BriefExtractOptions { IncludeBlockRegions = includeBlockRegions });
        var document = new
        {
            kind = "cad-brief",
            version = 1,
            persisted = false,
            source = new { file, format = "dxf", bytes = bytes.LongLength, sha256 = Convert.ToHexString(SHA256.HashData(bytes)), page = (int?)null, scale = drawing.MetresPerUnit is not null ? "insunits" : "none", metres_per_unit = result.MetresPerUnit },
            brief = JsonNode.Parse(BriefSerializer.Serialize(result.Brief)),
            rooms = result.Rooms,
            uncertainties = result.Uncertainties,
            issues = result.Issues,
        };
        return JsonSerializer.SerializeToUtf8Bytes(document, CliJson);
    }

    private static void RunCli(Action<bool, string, string> c, byte[] bytes)
    {
        // ---------- documented limit: reader warnings of dropped inserts are not brief uncertainties ----------
        try
        {
            var drawing = DxfReader.Parse(bytes, FileName);
            var dropped = string.Join(",", drawing.Warnings.Select(w => w.Code + "@" + w.Line.ToString(CultureInfo.InvariantCulture)));
            var draft = BriefExtract.FromDxf(drawing, new BriefExtractOptions { IncludeBlockRegions = true });
            var plain = BriefExtract.FromDxf(drawing);
            bool Mentions(BriefDraft d) => d.Uncertainties.Any(u => u.Code.Contains("ocs", StringComparison.Ordinal) || u.Message.Contains("non-planar", StringComparison.Ordinal)
                || u.Message.Contains("extrusion", StringComparison.Ordinal) || u.Sources.Any(s => s.Handle is "T1" or "T2" or "T3" || s.InsertPath.Any(p => p.StartsWith('T'))));
            var codes = string.Join(",", draft.Uncertainties.GroupBy(u => u.Code).Select(g => g.Key + "x" + g.Count().ToString(CultureInfo.InvariantCulture)));
            var plainCodes = string.Join(",", plain.Uncertainties.GroupBy(u => u.Code).Select(g => g.Key + "x" + g.Count().ToString(CultureInfo.InvariantCulture)));
            c(drawing.Warnings.Count == 3 && drawing.Warnings.All(w => w.Code == "ocs-non-planar") && !Mentions(draft) && !Mentions(plain) && codes == PinnedCodesWithFlag && plainCodes == PinnedCodesWithout,
                "limit: the three ocs-non-planar reader warnings of the dropped T1/T2/T3 are not in the brief uncertainty list (codes pinned)",
                "reader " + dropped + " codes with flag " + codes + " without " + plainCodes);

            // The reason: an existing pinned brief already has a dropped tilted INSERT, so surfacing reader warnings
            // would change its bytes.
            var fixture = DxfReader.ReadFile(Path.Combine(RepoRoot(), "tests", "cad", "fixtures", "minsert-ocs.dxf"));
            var ocsBrief = BriefExtract.FromDxf(fixture);
            var unsupported = string.Join(",", fixture.Unsupported.Select(u => u.Handle + ":" + u.EntityType + ":" + u.Reason));
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ocsBrief))))[..16];
            c(fixture.Unsupported.Any(u => u.Handle == "85" && u.EntityType == "INSERT" && u.Reason == "ocs-non-planar") && !Mentions(ocsBrief) && digest == "799C995C1C1F14A7",
                "limit reason: minsert-ocs.dxf drops INSERT 85 as ocs-non-planar and its brief, pinned 799C995C1C1F14A7, does not report it",
                "unsupported " + unsupported + " digest " + digest + " codes " + string.Join(",", ocsBrief.Uncertainties.Select(u => u.Code)));
        }
        catch (Exception error)
        {
            c(false, "limit unexpected exception", error.GetType().Name + ": " + error.Message);
        }

        // ---------- the real cad-brief CLI writes the in-process bytes ----------
        try
        {
            var root = RepoRoot();
            var cliDll = Path.Combine(root, "interfaces", "cli", "bin", "Release", "net8.0", "Bim.Cli.dll");
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
            if (!File.Exists(cliDll)) throw new FileNotFoundException("CLI binary missing (scripts/verify.ps1 builds interfaces/cli first)", cliDll);
            var scratch = Path.Combine(Path.GetTempPath(), "bim-cad-p632-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
            Directory.CreateDirectory(scratch);
            try
            {
                var input = Path.Combine(scratch, FileName);
                File.WriteAllBytes(input, bytes);
                foreach (var (flag, pinned) in new[] { (true, PinnedWithFlag), (false, PinnedWithout) })
                {
                    var name = flag ? "with --include-block-regions" : "without the flag";
                    var output = Path.Combine(scratch, flag ? "brief-flag.json" : "brief-plain.json");
                    var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
                    foreach (var argument in new[] { cliDll, "cad-brief", input, output }.Concat(flag ? new[] { "--include-block-regions" } : Array.Empty<string>()))
                        start.ArgumentList.Add(argument);
                    using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(180000))
                    {
                        try { process.Kill(true); } catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { }
                        c(false, "cli " + name + " finishes", "timed out");
                        continue;
                    }
                    process.WaitForExit();
                    var envelopeText = stdout.Result.Trim();
                    var written = File.Exists(output) ? File.ReadAllBytes(output) : Array.Empty<byte>();
                    var expected = InProcessDocument(bytes, FileName, flag);
                    var firstDiff = Enumerable.Range(0, Math.Min(written.Length, expected.Length)).FirstOrDefault(i => written[i] != expected[i], -1);
                    var context = firstDiff < 0 ? "" : " cli '" + Encoding.UTF8.GetString(written, firstDiff, Math.Min(120, written.Length - firstDiff)) + "' in-process '" + Encoding.UTF8.GetString(expected, firstDiff, Math.Min(120, expected.Length - firstDiff)) + "'";
                    c(process.ExitCode == 0 && written.Length > 0 && written.AsSpan().SequenceEqual(expected),
                        "cli " + name + ": cad-brief writes exactly the in-process BriefExtract document bytes",
                        "exit " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + " stdout " + envelopeText + " stderr " + stderr.Result.Trim() + " cli " + written.Length.ToString(CultureInfo.InvariantCulture)
                        + " bytes " + (written.Length > 0 ? Hash16(written) : "-") + " in-process " + expected.Length.ToString(CultureInfo.InvariantCulture) + " bytes " + Hash16(expected) + " first difference " + firstDiff.ToString(CultureInfo.InvariantCulture) + context);
                    var envelopeOk = false;
                    try
                    {
                        using var envelope = JsonDocument.Parse(envelopeText);
                        var e = envelope.RootElement;
                        envelopeOk = e.GetProperty("status").GetString() == "succeeded" && e.GetProperty("kind").GetString() == "cad-brief"
                            && e.GetProperty("bytes").GetInt64() == written.LongLength && e.GetProperty("sha256").GetString() == Convert.ToHexString(SHA256.HashData(written))
                            && e.GetProperty("rooms").GetInt32() == 7 && !e.GetProperty("persisted").GetBoolean();
                    }
                    catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
                    c(envelopeOk, "cli " + name + ": the envelope names the written bytes, their sha256 and 7 rooms", envelopeText);
                    var text = Encoding.UTF8.GetString(written);
                    c(written.Length > 0 && Hash16(written) == pinned && !text.Contains("non-planar", StringComparison.Ordinal) && !text.Contains("ocs-", StringComparison.Ordinal),
                        "cli " + name + ": document digest pinned " + pinned + " and no reader warning in it",
                        "digest " + (written.Length > 0 ? Hash16(written) : "-") + " bytes " + written.Length.ToString(CultureInfo.InvariantCulture));
                }
            }
            finally
            {
                try { Directory.Delete(scratch, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error)
        {
            c(false, "cli unexpected exception", error.GetType().Name + ": " + error.Message);
        }
    }
}
