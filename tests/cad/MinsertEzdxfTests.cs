using Bim.Cad;
using System.Globalization;
using System.Text;
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
    }
}
