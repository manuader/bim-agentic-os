using Bim.Cad;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
// ezdxf cross-check: tests/cad/tools/minsert_ezdxf_centres.py (its --check runs in CI since P6-46) builds the same block
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
//     same document built in process (P6-41: through CadBriefDocument.FromDxf, as the CLI); both digests pinned.
//   * Documented limit (pinned): the dropped tilted inserts' ocs-non-planar reader warnings are NOT in the brief's
//     uncertainty list. BriefExtract reports only explode warnings, never DxfDrawing.Warnings/Unsupported, and adding
//     them would change existing brief bytes: the checked-in minsert-ocs.dxf already has a dropped INSERT (handle 85,
//     extrusion (0,1,0), ocs-non-planar) and its BriefExtract output is pinned byte for byte (BriefNonAsciiTests digest
//     799C995C1C1F14A7, re-checked here). Callers read the drop from DxfDrawing.Warnings and DxfDrawing.Unsupported.
//
// P6-34 (sections RunBulgeParity, RunChain and RunCliPrecondition at the end):
//   * Bulge sign parity for every D1 cell (both columns, NA and NB), not only D1[0,0].
//   * ChainDrawing(): a near-Z chain three levels deep, every level at a non-zero elevation (E1 > TM > MA: TOP under
//     (-1e-7,3e-7,-1) at z 4000 holding MID under (2e-7,0,1) at z -2500 holding ARCH under (1e-7,2e-7,-1) at z 1500),
//     and a MINSERT with 2 columns and 3 rows inside a chain (F1 > GA: GRID under near +Z holding the MINSERT of ARCH
//     under near -Z). The seven outlines' corners and segment midpoints equal ezdxf 1.4.2 (offline generator, P6-34
//     section: composed Insert.matrix44() of every level, MINSERT cells from multi_insert()) within 1e-6. A nested
//     insertion's block z reaches world XY through the enclosing insert's normal (z times its horizontal part, about
//     1e-3 mm for E1), which a projection that drops the nested elevation misses.
//   * cad-brief CLI precondition: a missing Release Bim.Cli.dll is reported as the named failure "cli precondition"
//     (and the CLI checks are skipped) instead of an unexpected exception; a self-test points it at a missing path.
//   * The brief's exclusion of reader warnings stays the documented limit above; the proposed opt-in is written up in
//     docs/contracts/module-contracts.md (cad-brief section), not implemented.
//
// P6-36 (section RunZ at the end): the near-Z chain remainder. ZDrawing() places the ARCH outline through seven cases,
// each under an enclosing near +-Z normal (where one unit of block z moves world XY by the normal's horizontal part):
// H1 > HM a 2x2 MINSERT at elevation 1800 inside a near +Z insert at elevation 2000; X1 > XE / XF exact -Z (elevation
// -3000) and exact +Z (elevation 2200) nested inserts under a near -Z insert; Z1 > ZA Z scale 2.5 (group 43); K1 BLOCK
// base point z 900; L1 / L2 leaf LWPOLYLINE elevation (group 38) 1200 in +Z and 800 in an exact -Z OCS; W1 > CL all of
// them combined (Z scales 1.5 and 0.8, base z -400, elevations 1000/600, leaf elevation 1200). The eleven outlines'
// corners and segment midpoints equal ezdxf 1.4.2 (offline generator, P6-36 section z_main: composed Insert.matrix44()
// of every level, MINSERT cells from multi_insert(); virtual_entities() agrees within 1e-9 for the cases without a
// MINSERT) within 1e-6. Removing each case's z feature moves its outline by 2.5e-4 (H1) to 9e-4 (X1 > XE) in ezdxf, so
// every case exercises it. H1 and X1 already held after P6-34 (pinned); Z1, K1, L1, L2 and W1 were off by 6e-4, 2.7e-4,
// 3.6e-4, 2.4e-4 and 5.7e-4 before P6-36 made DxfReader keep groups 43, BLOCK 30 and 38 and DxfExploder use them.
//
// P6-38 (section RunLeaf at the end): leaf z under near +-Z inserts. LeafDrawing() places block LZ (a LINE with z
// 1500/-700, CIRCLEs and ARCs in +Z, exact -Z and near -Z OCSs, TEXT in +Z and exact -Z, MTEXT, a constant ATTDEF, the
// P6-30 tilted LWPOLYLINE whose plane is at world z 460, and a heavy POLYLINE) under Q1 (near +Z) and Q2 (near -Z, Z
// scale -1.5), and an ATTRIB at z 1900 of a nested INSERT under Q3 (near -Z). Every point (line ends, centres, arc ends
// and midpoints, insertion points, polyline vertices) equals ezdxf 1.4.2 (generator section P6-38 leaf_main: composed
// Insert.matrix44() applied to the leaf point; the entity's own transform() and, for the ATTRIB, virtual_entities()
// agree within 1e-9) within 1e-6. Documented limits pinned: the heavy POLYLINE is not modelled (typed unsupported,
// nothing placed); the explode consumes the z members (every placed copy has StartZ/EndZ/Elevation 0, model-space
// entities keep theirs); CadToBimIr's candidate copies drop them (same draft bytes at any z). INSERT group 43 = 0 is
// refused entity-invalid like 41/42 = 0; a negative 43 is kept. A z group that is not a finite number reads as 0.
//
// P6-43 (RunCli, reader-options pin): InProcessDocument reads the drawing with CliDxfReadOptions, the in-process copy of
// CadCommand's DXF reader options, and that copy is pinned against interfaces/cli/CadCommand.cs: the source names
// DxfReadOptions and DxfReader. once each, the DXF read is the statement CliDxfRead (MaxInputBytes only) and
// MaxInputBytes is declared once as a product of integers equal to CliMaxInputBytes (32 MiB). How the source is read
// changed in P6-45 (code only, whitespace removed; below); P6-43 itself counted on the source as written.
// Self-tests: an extra reader argument, a commented-out copy beside a changed read and a changed cap are each caught.
// The options are pinned rather than shared because CadCommand reads the input once for cad-draft and cad-brief (the
// reason recorded for the PDF reader options in the cad-brief contract).
// P6-45 (one pin style): the pin reads CadCommand.cs through the same comment/literal stripper as the PDF reader-option
// pin (one stripper, no copy; since P6-49 CodePins.StripCommentsAndLiterals (tests/cad/CodePins.cs), called directly by
// both pins), so the counts are of code only and a comment naming DxfReadOptions or DxfReader. does not fail it; the read is matched as one statement
// with all whitespace removed (a reformatted statement passes; the "dxf" literal is stripped, as the PDF pin strips
// "pdf"); and the always-true self-comparison is replaced by the in-process copy against new DxfReadOptions(cap) with
// the cap parsed from CadCommand.cs, which fails when either side changes. Self-tests: // and /* */ comments naming both
// tokens and the read over several lines pass; a second DxfReader. call in code is caught.
// P6-47 (pin tidy): the pin label named the stripper it used (P6-49 then moved it to the shared helper
// CodePins.StripCommentsAndLiterals (tests/cad/CodePins.cs), and the label names that); the MaxInputBytes= count skips a
// MaxInputBytes == comparison (self-tests: a comparison in code passes, a second assignment is caught); the PDF
// reader-option pin in PdfType3GlyphListTests matches stripped code with all whitespace removed, as this pin does.
//
// P6-44 (section RunJustified at the end): justified TEXT (72 = 1/2/4, 73 != 0) keeps its group 10 point, ezdxf 1.4.2
// places it on group 11 (documented limit, measured and pinned); a non-finite group 30 and 31 on a justified near-Z
// attribute fail at group 30's line; the A1/J1/K1 constants are cross-referenced.
// P6-44 generator check, run in CI since P6-46: the engineering-gates ifc job (Linux, Python 3.12) installs the pinned
// ezdxf==1.4.2 from PyPI into its own venv and, with EZDXF_DISABLE_C_EXT=1 (the pure-Python ezdxf, as in the source
// checkout the constants were made with), runs tests/cad/tools/minsert_ezdxf_centres.py --check, which compares the
// constant rows the generator prints with this file both ways (a row matches by its names and its full number list,
// exact doubles) and exits 1 on any difference or on a planted self-test it does not catch. Offline, the same check:
//     PYTHONPATH=<ezdxf v1.4.2 checkout, commit 8240df57>/src python3 tests/cad/tools/minsert_ezdxf_centres.py --check
// P6-50 (generation environment, pinned by RunJustified and stated the same way in the generator's docstring): the
// constants are the pure-Python ezdxf 1.4.2 values with numpy 2.5.3. Evidence for that generation version: the offline
// environment the generator is run in (re-measured in relay293: Python 3.13.18, numpy 2.5.3, pyparsing 3.3.3,
// typing_extensions 4.16.0, fonttools 4.67.0) reproduces every row, and CI run 45718082997 (main 36398c0, Python 3.12.16,
// pip's numpy==2.5.3, EZDXF_DISABLE_C_EXT=1) reproduced all 164 rows exactly (0 failures). The CI step installs
// ezdxf==1.4.2 numpy==2.5.3 pyparsing==3.3.3 typing_extensions==4.16.0 fonttools==4.69.1 (the other dependencies at the
// versions pip resolved in CI run 45725166224, printed by the step with numpy's; fonttools 4.67.0 offline and 4.69.1
// in CI give the same rows). Of the pure-Python run's output, every output line but the caught self-tests is annotated,
// 20 lines an annotation, at most 9 annotations, then one line with the count of the lines left over (errors when the run
// fails, notices otherwise). With the wheel's C extensions four rows differ in the last bits: C1>NB, X1>XF, Z1>ZA, W1>CL.
// They are pinned (C_EXT_DRIFT in the generator): under the C extensions a differing row outside them fails that run and
// the step, named after "new C-extension drift rows: "; a pinned row that stops differing is named as no longer drifting.
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
        RunBulgeParity(C);
        RunChain(C);
        RunZ(C);
        RunLeaf(C);
        RunFollow(C);
        RunAligned(C);
        RunJustified(C);
        if (RunCliPrecondition(C)) RunCli(C, bytes);
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
    // process through the same public entry point (P6-41: CadBriefDocument.FromDxf, then ToUtf8Bytes) with the CLI's
    // 32 MiB reader cap, so no document member or serializer setting is mirrored here. No independent mirror is kept:
    // the CLI digests PinnedWithFlag / PinnedWithout (measured before the builder existed) still pin the bytes, and the
    // P6-39 block of tests/cad/PdfType3GlyphListTests.cs keeps the hand-built mirror for the shared document layout.
    private static byte[] InProcessDocument(byte[] bytes, string file, bool includeBlockRegions)
    {
        var drawing = DxfReader.Parse(bytes, file, CliDxfReadOptions);
        return CadBriefDocument.FromDxf(file, bytes, drawing, new BriefExtractOptions { IncludeBlockRegions = includeBlockRegions }).ToUtf8Bytes();
    }

    // P6-43: the in-process copy of CadCommand's DXF reader options, pinned against CadCommand.cs by ReaderOptionsProblems.
    private const int CliMaxInputBytes = 32 * 1024 * 1024;
    private static readonly DxfReadOptions CliDxfReadOptions = new(CliMaxInputBytes);
    // The DXF read as CadCommand.cs writes it: the self-tests find it by this text and, P6-45, the pin matches it as one
    // statement of stripped code with whitespace squeezed (the "dxf" literal stripped to ""). P6-49: that squeezed form is
    // derived from this one (CodePins), so no hand-written compact copy is kept.
    private const string CliDxfRead = "if (format == \"dxf\") drawing = DxfReader.Parse(bytes, sourceFile, new DxfReadOptions(MaxInputBytes));";
    private const string CliCapPrefix = "public const int MaxInputBytes = ";

    // Null when CadCommand.cs code (comments and literals stripped, P6-45) reads a DXF with exactly the options of
    // CliDxfReadOptions. P6-49: stripped by CodePins.StripCommentsAndLiterals (tests/cad/CodePins.cs, the one stripper
    // shared with the PDF reader-option pin, called directly), counted squeezed at identifier boundaries (CodePins).
    private static string? ReaderOptionsProblems(string source)
    {
        var code = CodePins.Squeeze(CodePins.StripCommentsAndLiterals(source.Replace("\r\n", "\n", StringComparison.Ordinal)));
        var readStatement = CodePins.Squeeze(CodePins.StripCommentsAndLiterals(CliDxfRead));
        var capDeclaration = CodePins.Squeeze(CliCapPrefix);
        var problems = new List<string>();
        if (CodePins.Count(code, "DxfReadOptions") != 1) problems.Add("'DxfReadOptions' x" + CodePins.Count(code, "DxfReadOptions").ToString(CultureInfo.InvariantCulture) + " (want 1)");
        if (CodePins.Count(code, "DxfReader.") != 1) problems.Add("'DxfReader.' x" + CodePins.Count(code, "DxfReader.").ToString(CultureInfo.InvariantCulture) + " (want 1)");
        if (CodePins.Count(code, readStatement) != 1) problems.Add("the DXF read statement is not exactly '" + CliDxfRead + "' (whitespace aside) x" + CodePins.Count(code, readStatement).ToString(CultureInfo.InvariantCulture));
        var capCount = CodePins.Count(code, capDeclaration);
        long? cap = null;
        if (capCount == 1)
        {
            var start = CodePins.IndexOf(code, capDeclaration) + capDeclaration.Length;
            var end = code.IndexOf(';', start);
            var factors = end < 0 ? Array.Empty<string>() : code[start..end].Split('*');
            if (factors.Length > 0 && factors.All(f => f.Length > 0 && f.All(char.IsAsciiDigit) && f.Length < 10))
                cap = factors.Aggregate(1L, (product, f) => product * long.Parse(f, CultureInfo.InvariantCulture));
        }
        // P6-47: assignments only; a MaxInputBytes == comparison reads the cap and is not counted. P6-49: nor is an
        // expression-bodied MaxInputBytes => or another identifier ending in MaxInputBytes (identifier boundary). P6-51:
        // counted by CodePins.Assignments, so a compound assignment (+=, -=, ??= ...) counts too and a comparison
        // (!=, <=, >=) does not.
        var assigned = CodePins.Assignments(code, "MaxInputBytes");
        if (assigned != 1 || cap is null)
            problems.Add("MaxInputBytes assigned x" + assigned.ToString(CultureInfo.InvariantCulture) + ", declared x" + capCount.ToString(CultureInfo.InvariantCulture) + " as " + (cap?.ToString(CultureInfo.InvariantCulture) ?? "unparsed") + " (want one declaration, one integer product)");
        // P6-45: replaces the always-true CliDxfReadOptions == new DxfReadOptions(CliMaxInputBytes); this one compares the
        // in-process copy with the options CadCommand's parsed cap gives, so it fails when either side changes.
        else if (new DxfReadOptions(cap.Value) != CliDxfReadOptions)
            problems.Add("in-process copy " + CliDxfReadOptions + " != CadCommand's DxfReadOptions(MaxInputBytes) with MaxInputBytes as " + cap.Value.ToString(CultureInfo.InvariantCulture));
        return problems.Count > 0 ? string.Join("; ", problems) : null;
    }

    private static void RunCli(Action<bool, string, string> c, byte[] bytes)
    {
        // ---------- P6-43: CadCommand's DXF reader options against the in-process copy ----------
        try
        {
            var cadCommandPath = Path.Combine(RepoRoot(), "interfaces", "cli", "CadCommand.cs");
            var cadCommand = File.Exists(cadCommandPath) ? File.ReadAllText(cadCommandPath) : "";
            c(cadCommand.Length > 0 && ReaderOptionsProblems(cadCommand) is null,
                "p6-43 reader options: CadCommand.cs code (comments and literals stripped, P6-45, by the shared CodePins.StripCommentsAndLiterals, P6-49) reads a DXF with DxfReadOptions(MaxInputBytes) only, the in-process copy equals DxfReadOptions of CadCommand's parsed MaxInputBytes (32 MiB)",
                cadCommandPath + " " + (cadCommand.Length == 0 ? "missing" : ReaderOptionsProblems(cadCommand) ?? "pins hold") + " in-process " + CliDxfReadOptions);
            const string realCall = "new DxfReadOptions(MaxInputBytes)";
            foreach (var (tag, label, drifted, expect) in new[]
            {
                ("p6-43", "an extra reader argument", cadCommand.Replace(realCall, "new DxfReadOptions(MaxInputBytes) with { MaxBytes = 1024 }", StringComparison.Ordinal), "the DXF read statement"),
                ("p6-43", "a commented-out copy beside a changed read", cadCommand.Replace(CliDxfRead, CliDxfRead.Replace("(MaxInputBytes)", "(64 * 1024 * 1024)", StringComparison.Ordinal) + " // " + CliDxfRead, StringComparison.Ordinal), "the DXF read statement"),
                ("p6-43", "a /* */ commented-out copy beside a changed read", cadCommand.Replace(CliDxfRead, CliDxfRead.Replace("(MaxInputBytes)", "(64 * 1024 * 1024)", StringComparison.Ordinal) + " /* " + CliDxfRead + " */", StringComparison.Ordinal), "the DXF read statement"),
                ("p6-43", "a second DxfReader. call in code", cadCommand.Replace(CliDxfRead, CliDxfRead + " var again = DxfReader.Parse(bytes, sourceFile);", StringComparison.Ordinal), "'DxfReader.' x2"),
                ("p6-43", "a changed cap", cadCommand.Replace(CliCapPrefix + "32 * 1024 * 1024;", CliCapPrefix + "16 * 1024 * 1024;", StringComparison.Ordinal), "in-process copy DxfReadOptions { MaxBytes = 33554432 } != CadCommand's DxfReadOptions(MaxInputBytes) with MaxInputBytes as 16777216"),
                // P6-47: the == exclusion of the MaxInputBytes= count does not drop a second assignment.
                ("p6-47", "a second MaxInputBytes assignment in code", cadCommand.Replace(CliDxfRead, "MaxInputBytes = 1024; " + CliDxfRead, StringComparison.Ordinal), "MaxInputBytes assigned x2"),
                // P6-51: a compound assignment (+=, -=, ??=) changes the cap too and counts as an assignment.
                ("p6-51", "a MaxInputBytes += compound assignment in code", cadCommand.Replace(CliDxfRead, "MaxInputBytes += 1024; " + CliDxfRead, StringComparison.Ordinal), "MaxInputBytes assigned x2"),
                ("p6-51", "a MaxInputBytes -= compound assignment in code", cadCommand.Replace(CliDxfRead, "MaxInputBytes -= 1024; " + CliDxfRead, StringComparison.Ordinal), "MaxInputBytes assigned x2"),
                ("p6-51", "a MaxInputBytes ??= compound assignment in code", cadCommand.Replace(CliDxfRead, "MaxInputBytes ??= 1024; " + CliDxfRead, StringComparison.Ordinal), "MaxInputBytes assigned x2"),
            })
                c(cadCommand.Length > 0 && drifted != cadCommand && ReaderOptionsProblems(drifted) is { } caught && caught.Contains(expect, StringComparison.Ordinal),
                    tag + " reader options self-test: " + label + " in CadCommand.cs is caught",
                    drifted == cadCommand ? "self-test anchor not found" : ReaderOptionsProblems(drifted) ?? "not caught");
            // P6-45 self-tests: what must not fail the pin. A // and a /* */ comment naming DxfReadOptions and DxfReader.
            // beside the read, and the read statement reformatted over several lines.
            foreach (var (tag, label, tolerated) in new[]
            {
                ("p6-45", "a // comment naming DxfReadOptions and DxfReader.", cadCommand.Replace(CliDxfRead, "// DxfReader.Parse with DxfReadOptions(MaxInputBytes) (pinned by tests/cad)\n            " + CliDxfRead, StringComparison.Ordinal)),
                ("p6-45", "a /* */ comment naming DxfReadOptions and DxfReader.", cadCommand.Replace(CliDxfRead, CliDxfRead + " /* not DxfReader.ReadFile, no other DxfReadOptions */", StringComparison.Ordinal)),
                ("p6-45", "the read statement reformatted over several lines", cadCommand.Replace(CliDxfRead, "if (format == \"dxf\")\n                drawing = DxfReader.Parse(\n                    bytes,\n                    sourceFile,\n                    new DxfReadOptions( MaxInputBytes ));", StringComparison.Ordinal)),
                // P6-47: a comparison reads MaxInputBytes; it neither declares nor assigns it.
                ("p6-47", "a MaxInputBytes == comparison in code", cadCommand.Replace(CliDxfRead, "var atCap = MaxInputBytes == bytes.Length; " + CliDxfRead, StringComparison.Ordinal)),
                // P6-49: identifier boundaries; another identifier ending in MaxInputBytes is not the cap.
                ("p6-49", "an OtherMaxInputBytes = assignment in code", cadCommand.Replace(CliDxfRead, "var OtherMaxInputBytes = 5; " + CliDxfRead, StringComparison.Ordinal)),
                // P6-49: an expression-bodied MaxInputBytes => (here a property of another type) is not an assignment.
                ("p6-49", "an expression-bodied MaxInputBytes => in code", cadCommand.Replace(CliCapPrefix + "32 * 1024 * 1024;", CliCapPrefix + "32 * 1024 * 1024;\n    private sealed class Limits { public int MaxInputBytes => CadCommand.MaxInputBytes; }", StringComparison.Ordinal)),
                // P6-51: the compound-assignment count does not take a comparison (>=, <=, !=) for an assignment.
                ("p6-51", "MaxInputBytes >=, <= and != comparisons in code", cadCommand.Replace(CliDxfRead, "var bounds = MaxInputBytes >= bytes.Length && MaxInputBytes <= int.MaxValue && MaxInputBytes != 0; " + CliDxfRead, StringComparison.Ordinal)),
            })
                c(cadCommand.Length > 0 && tolerated != cadCommand && ReaderOptionsProblems(tolerated) is null,
                    tag + " reader options self-test: " + label + " in CadCommand.cs does not fail the pin",
                    tolerated == cadCommand ? "self-test anchor not found" : ReaderOptionsProblems(tolerated) ?? "tolerated");
        }
        catch (Exception error)
        {
            c(false, "p6-43 reader options unexpected exception", error.GetType().Name + ": " + error.Message);
        }

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
            var cliDll = CliDllPath(root);
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

    // ---------- P6-34: bulge sign parity for every D1 cell ----------

    // D1 (NEST under near -Z) mirrors once; NA inside it adds no mirror and NB (near -Z) a second one. Every cell of the
    // MINSERT carries the same parity, so both columns flip NA and keep NB.
    private static void RunBulgeParity(Action<bool, string, string> c)
    {
        try
        {
            var drawing = DxfReader.Parse(new UTF8Encoding(false).GetBytes(BulgeDrawing()), "bulge-ezdxf.dxf");
            var outlines = drawing.Explode().Entities.OfType<DxfPolyline>().ToList();
            foreach (var column in new[] { 0, 1 })
            {
                foreach (var (inner, expected) in new[] { ("NA", "-0.4/0.25"), ("NB", "0.4/-0.25") })
                {
                    var path = "D1[0," + column.ToString(CultureInfo.InvariantCulture) + "]>" + inner;
                    var outline = outlines.Where(o => string.Join(">", o.Provenance.InsertPath) == path).ToList();
                    var got = outline.Count == 1 ? string.Join("/", outline[0].Vertices.Where(v => v.Bulge != 0).Select(v => F(v.Bulge))) : outline.Count.ToString(CultureInfo.InvariantCulture) + " outlines";
                    var mirrors = (inner == "NB" ? 2 : 1).ToString(CultureInfo.InvariantCulture);
                    c(got == expected, "parity: " + path + " (" + mirrors + " mirror(s) in the chain) has bulges " + expected, got);
                }
            }
        }
        catch (Exception error)
        {
            c(false, "parity unexpected exception", error.GetType().Name + ": " + error.Message);
        }
    }

    // ---------- P6-34: a three-level near-Z chain with elevations and a MINSERT inside a chain ----------

    // Same parameters as CHAIN_BLOCKS and CHAIN_CASES in tests/cad/tools/minsert_ezdxf_centres.py.
    private static string ChainDrawing() =>
        G("0 SECTION|2 HEADER|9 $INSUNITS|70 4|0 ENDSEC")
        + G("0 SECTION|2 BLOCKS|0 BLOCK|5 A0|8 0|2 ARCH|70 0|10 50|20 20|30 0")
        + G("0 LWPOLYLINE|5 A1|8 0|90 4|70 1|10 50|20 20|10 1050|20 20|42 0.4|10 1050|20 820|10 50|20 820|42 -0.25")
        + G("0 ENDBLK|5 A2|8 0")
        + G("0 BLOCK|5 M0|8 0|2 MID|70 0|10 10|20 10|30 0")
        + G("0 INSERT|5 MA|8 0|2 ARCH|10 400|20 -300|30 1500|41 1.2|42 1.2|50 35|210 1e-7|220 2e-7|230 -1")
        + G("0 ENDBLK|5 M1|8 0")
        + G("0 BLOCK|5 P0|8 0|2 TOP|70 0|10 -20|20 40|30 0")
        + G("0 INSERT|5 TM|8 0|2 MID|10 2500|20 1200|30 -2500|41 0.9|42 0.9|50 -50|210 2e-7|220 0|230 1")
        + G("0 ENDBLK|5 P1|8 0")
        + G("0 BLOCK|5 G0|8 0|2 GRID|70 0|10 0|20 0|30 0")
        + G("0 INSERT|5 GA|8 0|2 ARCH|10 -600|20 900|30 0|41 0.7|42 0.7|50 10|70 2|71 3|44 1500|45 -1100|210 0|220 1e-7|230 -1")
        + G("0 ENDBLK|5 G1|8 0|0 ENDSEC")
        + G("0 SECTION|2 ENTITIES")
        + G("0 INSERT|5 E1|8 ROOMS|2 TOP|10 70000|20 -15000|30 4000|41 1.1|42 1.1|50 20|210 -1e-7|220 3e-7|230 -1")
        + G("0 INSERT|5 F1|8 ROOMS|2 GRID|10 -60000|20 -20000|30 0|41 1.3|42 1.3|50 25|210 -2e-7|220 1e-7|230 1")
        + G("0 ENDSEC|0 EOF");

    // ezdxf 1.4.2 outlines (generator section P6-34): (insert path, WCS corners x0,y0..x3,y3, WCS segment midpoints).
    private static readonly (string Path, double[] Corners, double[] Midpoints)[] ChainOutlines =
    {
        ("E1>TM>MA", new[] { -71663.43998071413, -12915.705693017644, -71161.36948576615, -11839.01204201814, -72022.72440656577, -11437.355646059777, -72524.79490151376, -12514.049297059284 }, new[] { -71412.40473324014, -12377.35886751789, -71511.71566697428, -11465.912859879038, -72273.75965403976, -11975.70247155953, -72043.91039161915, -12607.208129938515 }),
        ("F1>GA[0,0]", new[] { -59787.543292347225, -18609.977645009283, -60666.53579427026, -18845.50297605258, -60854.9560591049, -18142.308974514137, -59975.96355718186, -17906.783643470844 }, new[] { -60227.03954330874, -18727.74031053093, -60901.38472699527, -18531.590028250284, -60415.45980814338, -18024.54630899249, -59969.65267495685, -18281.933177344396 }),
        ("F1>GA[0,1]", new[] { -61671.098653610876, -19114.674782959195, -62550.09115553392, -19350.200114002488, -62738.51142036855, -18647.00611246405, -61859.51891844551, -18411.480781420756 }, new[] { -62110.5949045724, -19232.437448480843, -62784.94008825892, -19036.287166200196, -62299.01516940703, -18529.243446942404, -61853.208036220494, -18786.630315294304 }),
        ("F1>GA[1,0]", new[] { -59417.432057850645, -19991.25157660265, -60296.42455977367, -20226.77690764594, -60484.84482460831, -19523.582906107502, -59605.852322685256, -19288.05757506421 }, new[] { -59856.92830881216, -20109.014242124296, -60531.27349249868, -19912.86395984365, -60045.348573646785, -19405.820240585854, -59599.54144046025, -19663.207108937757 }),
        ("F1>GA[1,1]", new[] { -61300.987419114295, -20495.94871455256, -62179.979921037324, -20731.474045595856, -62368.400185871964, -20028.280044057414, -61489.40768394893, -19792.754713014125 }, new[] { -61740.48367007581, -20613.711380074208, -62414.82885376233, -20417.56109779356, -61928.90393491044, -19910.51737853577, -61483.096801723914, -20167.904246887672 }),
        ("F1>GA[2,0]", new[] { -59047.32082335405, -21372.525508196013, -59926.31332527709, -21608.05083923931, -60114.73359011172, -20904.85683770087, -59235.741088188675, -20669.331506657578 }, new[] { -59486.81707431557, -21490.28817371766, -60161.16225800209, -21294.137891437018, -59675.2373391502, -20787.094172179226, -59229.43020596367, -21044.481040531125 }),
        ("F1>GA[2,1]", new[] { -60930.87618461771, -21877.22264614593, -61809.86868654074, -22112.74797718922, -61998.28895137538, -21409.55397565078, -61119.29644945233, -21174.028644607486 }, new[] { -61370.37243557922, -21994.985311667573, -62044.71761926575, -21798.835029386926, -61558.79270041386, -21291.791310129134, -61112.985567227326, -21549.178178481037 }),
    };

    private static void RunChain(Action<bool, string, string> c)
    {
        try
        {
            var drawing = DxfReader.Parse(new UTF8Encoding(false).GetBytes(ChainDrawing()), "chain-ezdxf.dxf");
            var x = drawing.Explode();
            var outlines = x.Entities.OfType<DxfPolyline>().ToList();
            var paths = string.Join(",", outlines.Select(o => string.Join(">", o.Provenance.InsertPath)));
            c(drawing.Warnings.Count == 0 && drawing.Unsupported.Count == 0 && x.Warnings.Count == 0 && outlines.Count == ChainOutlines.Length
                    && paths == string.Join(",", ChainOutlines.Select(o => o.Path)),
                "chain: E1>TM>MA and the six F1>GA cells are kept and exploded row-major; bulges not tessellated",
                "reader warnings " + string.Join(",", drawing.Warnings.Select(w => w.Code)) + " explode warnings " + string.Join(",", x.Warnings.Select(w => w.Code)) + " paths " + paths);
            foreach (var (path, corners, midpoints) in ChainOutlines)
            {
                var outline = outlines.Where(o => string.Join(">", o.Provenance.InsertPath) == path).ToList();
                if (outline.Count != 1 || outline[0].Vertices.Count != 4 || !outline[0].Closed)
                {
                    c(false, "chain: " + path + " outline equals ezdxf", "found " + outline.Count.ToString(CultureInfo.InvariantCulture) + " outlines");
                    continue;
                }
                var v = outline[0].Vertices;
                var mids = Enumerable.Range(0, 4).Select(i => SegmentMidpoint(v[i].Point, v[(i + 1) % 4].Point, v[i].Bulge)).ToList();
                var worst = Enumerable.Range(0, 4).Max(i => new[]
                {
                    Math.Abs(v[i].Point.X - corners[2 * i]), Math.Abs(v[i].Point.Y - corners[2 * i + 1]),
                    Math.Abs(mids[i].X - midpoints[2 * i]), Math.Abs(mids[i].Y - midpoints[2 * i + 1]),
                }.Max());
                c(worst <= Eps,
                    "chain: " + path + " corners and segment (arc) midpoints equal ezdxf 1.4.2 within 1e-6",
                    "corners " + string.Join(";", v.Select(p => P(p.Point))) + " bulges " + string.Join(";", v.Select(p => F(p.Bulge)))
                    + " midpoints " + string.Join(";", mids.Select(P)) + " max error " + F(worst));
            }
            // E1 mirrors (near -Z), TM does not, MA does: two mirrors keep 0.4/-0.25. F1 (near +Z) does not mirror and
            // GA (near -Z) does: every GA cell flips.
            foreach (var (path, _, _) in ChainOutlines)
            {
                var outline = outlines.Where(o => string.Join(">", o.Provenance.InsertPath) == path).ToList();
                var got = outline.Count == 1 ? string.Join("/", outline[0].Vertices.Where(p => p.Bulge != 0).Select(p => F(p.Bulge))) : "-";
                var expected = path.StartsWith("E1", StringComparison.Ordinal) ? "0.4/-0.25" : "-0.4/0.25";
                c(got == expected, "chain: " + path + " bulge sign follows the mirror parity (" + expected + ")", got);
            }
        }
        catch (Exception error)
        {
            c(false, "chain unexpected exception", error.GetType().Name + ": " + error.Message);
        }
    }

    // ---------- P6-34: the cad-brief CLI checks' named precondition ----------

    private static string CliDllPath(string root) => Path.Combine(root, "interfaces", "cli", "bin", "Release", "net8.0", "Bim.Cli.dll");

    // Null when the Release Bim.Cli.dll exists, else the named precondition failure (scripts/verify.ps1 builds
    // interfaces/cli in Release before tests/cad runs; running tests/cad alone skips that).
    internal static string? CliPreconditionFailure(string cliDll) =>
        File.Exists(cliDll) ? null : "precondition failed: Release Bim.Cli.dll not found at " + cliDll + " (build interfaces/cli in Release first; scripts/verify.ps1 does)";

    // Reports the precondition as its own named check and returns whether the CLI checks can run.
    private static bool RunCliPrecondition(Action<bool, string, string> c)
    {
        try
        {
            var missing = Path.Combine(Path.GetTempPath(), "bim-cad-p634-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "Release", "Bim.Cli.dll");
            var selfTest = CliPreconditionFailure(missing);
            c(selfTest is not null && selfTest.StartsWith("precondition failed: Release Bim.Cli.dll not found at ", StringComparison.Ordinal) && selfTest.Contains(missing, StringComparison.Ordinal),
                "cli precondition self-test: a missing Release Bim.Cli.dll is named as a precondition failure with its path", selfTest ?? "null");
            var failure = CliPreconditionFailure(CliDllPath(RepoRoot()));
            c(failure is null, "cli precondition: the Release Bim.Cli.dll the cad-brief CLI checks run exists", failure ?? "");
            return failure is null;
        }
        catch (Exception error)
        {
            c(false, "cli precondition unexpected exception", error.GetType().Name + ": " + error.Message);
            return false;
        }
    }

    // ---------- P6-36: MINSERT at elevation, exact under near-Z, Z scale, base z and leaf z ----------

    // Same parameters as Z_BLOCKS and Z_CASES in tests/cad/tools/minsert_ezdxf_centres.py.
    private const string ZArch = "90 4|70 1|10 50|20 20|10 1050|20 20|42 0.4|10 1050|20 820|10 50|20 820|42 -0.25";

    private static string ZDrawing() =>
        G("0 SECTION|2 HEADER|9 $INSUNITS|70 4|0 ENDSEC")
        + G("0 SECTION|2 BLOCKS|0 BLOCK|5 A0|8 0|2 ARCH|70 0|10 50|20 20|30 0")
        + G("0 LWPOLYLINE|5 A1|8 0|" + ZArch) + G("0 ENDBLK|5 A2|8 0")
        + G("0 BLOCK|5 B0|8 0|2 HOLD|70 0|10 5|20 -5|30 0")
        + G("0 INSERT|5 HM|8 0|2 ARCH|10 300|20 -200|30 1800|41 0.6|42 0.6|50 20|70 2|71 2|44 1400|45 -1000|210 1e-7|220 -2e-7|230 -1")
        + G("0 ENDBLK|5 B2|8 0")
        + G("0 BLOCK|5 C0|8 0|2 XHOLD|70 0|10 0|20 0|30 0")
        + G("0 INSERT|5 XE|8 0|2 ARCH|10 -400|20 700|30 -3000|50 40|210 0|220 0|230 -1")
        + G("0 INSERT|5 XF|8 0|2 ARCH|10 2600|20 -900|30 2200|41 0.9|42 0.9|50 -15")
        + G("0 ENDBLK|5 C2|8 0")
        + G("0 BLOCK|5 D0|8 0|2 ZHOLD|70 0|10 0|20 0|30 0")
        + G("0 INSERT|5 ZA|8 0|2 ARCH|10 800|20 600|30 2000|50 30")
        + G("0 ENDBLK|5 D2|8 0")
        + G("0 BLOCK|5 G0|8 0|2 BASEZ|70 0|10 50|20 20|30 900")
        + G("0 LWPOLYLINE|5 G1|8 0|" + ZArch) + G("0 ENDBLK|5 G2|8 0")
        + G("0 BLOCK|5 J0|8 0|2 ELEV|70 0|10 50|20 20|30 0")
        + G("0 LWPOLYLINE|5 J1|8 0|38 1200|" + ZArch) + G("0 ENDBLK|5 J2|8 0")
        + G("0 BLOCK|5 N0|8 0|2 ELEVM|70 0|10 50|20 20|30 0")
        + G("0 LWPOLYLINE|5 N1|8 0|38 800|" + ZArch + "|210 0|220 0|230 -1") + G("0 ENDBLK|5 N2|8 0")
        + G("0 BLOCK|5 P0|8 0|2 CMID|70 0|10 5|20 5|30 -400")
        + G("0 INSERT|5 CL|8 0|2 ELEV|10 -700|20 300|30 600|41 1.1|42 1.1|43 0.8|50 -25|210 2e-7|220 1e-7|230 1")
        + G("0 ENDBLK|5 P2|8 0|0 ENDSEC")
        + G("0 SECTION|2 ENTITIES")
        + G("0 INSERT|5 H1|8 ROOMS|2 HOLD|10 90000|20 30000|30 2000|41 1.1|42 1.1|50 15|210 3e-7|220 -2e-7|230 1")
        + G("0 INSERT|5 X1|8 ROOMS|2 XHOLD|10 -90000|20 40000|30 500|50 25|210 2e-7|220 3e-7|230 -1")
        + G("0 INSERT|5 Z1|8 ROOMS|2 ZHOLD|10 30000|20 -70000|30 0|43 2.5|50 10|210 -2e-7|220 1e-7|230 1")
        + G("0 INSERT|5 K1|8 ROOMS|2 BASEZ|10 -30000|20 -60000|30 0|50 30|210 2e-7|220 -3e-7|230 -1")
        + G("0 INSERT|5 L1|8 ROOMS|2 ELEV|10 10000|20 80000|30 0|50 -20|210 -3e-7|220 -1e-7|230 1")
        + G("0 INSERT|5 L2|8 ROOMS|2 ELEVM|10 60000|20 80000|30 0|50 50|210 1e-7|220 3e-7|230 1")
        + G("0 INSERT|5 W1|8 ROOMS|2 CMID|10 -60000|20 90000|30 1000|43 1.5|50 35|210 -2e-7|220 -2e-7|230 -1")
        + G("0 ENDSEC|0 EOF");

    // ezdxf 1.4.2 outlines (generator section P6-36): (insert path, WCS corners x0,y0..x3,y3, WCS segment midpoints).
    private static readonly (string Path, double[] Corners, double[] Midpoints)[] ZOutlines =
    {
        ("H1>HM[0,0]", new[] { 89731.44892419793, 29705.97474936806, 89073.96042345738, 29763.497539581535, 89119.97865562815, 30289.48834017397, 89777.46715636872, 30231.9655499605 }, new[] { 89402.70467382765, 29734.736144474795, 88991.77137942427, 30035.696586311908, 89448.72290599844, 30260.726945067236, 89688.70919020927, 29974.722428685625 }),
        ("H1>HM[0,1]", new[] { 88197.30908913666, 29840.19459319949, 87539.82058839613, 29897.71738341296, 87585.83882056692, 30423.708184005398, 88243.32732130746, 30366.185393791933 }, new[] { 87868.5648387664, 29868.955988306225, 87457.63154436303, 30169.916430143334, 87914.5830709372, 30394.946788898666, 88154.56935514799, 30108.94227251706 }),
        ("H1>HM[1,0]", new[] { 89635.57760717547, 28610.160581467142, 88978.08910643494, 28667.68337168061, 89024.1073386057, 29193.67417227305, 89681.59583934625, 29136.151382059583 }, new[] { 89306.8333568052, 28638.921976573874, 88895.90006240184, 28939.882418410984, 89352.85158897597, 29164.91277716632, 89592.8378731868, 28878.908260784712 }),
        ("H1>HM[1,1]", new[] { 88101.4377721142, 28744.380425298576, 87443.94927137367, 28801.903215512044, 87489.96750354445, 29327.894016104485, 88147.45600428498, 29270.371225891016 }, new[] { 87772.69352174393, 28773.14182040531, 87361.76022734058, 29074.10226224242, 87818.71175391471, 29299.13262099775, 88058.69803812553, 29013.128104616142 }),
        ("X1>XE", new[] { 89933.3103683996, 40803.4638056201, 90899.23619468864, 41062.28285072262, 90692.18095860658, 41835.02351175384, 89726.25513231753, 41576.20446665134 }, new[] { 90416.27328154413, 40932.873328171365, 90950.25670885385, 41490.06422845463, 90209.21804546205, 41705.61398920259, 89926.37533298746, 41215.71604064597 }),
        ("X1>XF", new[] { 87263.24385813392, 40283.13128219101, 86376.91688042293, 40439.41464209124, 86501.94356834306, 41148.476224260005, 87388.27054605406, 40992.192864359764 }, new[] { 86820.08036927842, 40361.27296214113, 86297.61790794924, 40818.95077075966, 86945.10705719856, 41070.33454430988, 87237.12450432288, 40653.29040926541 }),
        ("Z1>ZA", new[] { 30683.656295807607, -69270.19630605883, 31449.700738926585, -68627.4086963723, 30935.470651177377, -68014.5731418771, 30169.4262080584, -68657.36075156365 }, new[] { 31066.678517367094, -68948.80250121557, 31315.15280595102, -68218.14490157485, 30552.44842961789, -68335.96694672038, 30503.145696244897, -68899.49976784259 }),
        ("K1", new[] { 29999.9998199958, -59999.999729997326, 29133.97441621141, -59499.99972999735, 29533.974416211444, -58807.179406969815, 30399.999819995835, -59307.1794069698 }, new[] { 29566.987118103607, -59749.99972999733, 29195.410351605922, -59073.589568483585, 29966.98711810364, -59057.17940696981, 30113.39727961738, -59603.58956848357 }),
        ("L1", new[] { 9999.999639997153, 79999.99987999962, 10939.69226078303, 79657.97973667395, 11213.30837544353, 80409.73383330267, 10273.61575465765, 80751.75397662833 }, new[] { 10469.845950390092, 79828.98980833679, 11226.851137439022, 79979.1335620562, 10743.46206505059, 80580.7439049655, 10230.776959405986, 80341.6749139814 }),
        ("L2", new[] { 59935.72115902867, 79923.39531568454, 59292.93354934215, 79157.35087256558, 58680.09799484697, 79671.58096031478, 59322.88560453347, 80437.62540343375 }, new[] { 59614.327354185414, 79540.37309412507, 58883.66975454472, 79291.89880554116, 59001.49179969022, 80054.60318187426, 59565.02462081242, 80103.90591524725 }),
        ("W1>CL", new[] { 60746.70628584495, 89837.27798417951, 59663.4177575316, 90028.29097961317, 59816.228153878525, 90894.92180226393, 60899.516682191876, 90703.90880683027 }, new[] { 60205.062021688274, 89932.78448189635, 59566.49679117492, 90492.16847020794, 60357.8724180352, 90799.41530454709, 60714.78263118708, 90289.69469504825 }),
    };

    private static void RunZ(Action<bool, string, string> c)
    {
        try
        {
            var drawing = DxfReader.Parse(new UTF8Encoding(false).GetBytes(ZDrawing()), "z-ezdxf.dxf");
            var x = drawing.Explode();
            var outlines = x.Entities.OfType<DxfPolyline>().ToList();
            var paths = string.Join(",", outlines.Select(o => string.Join(">", o.Provenance.InsertPath)));
            c(drawing.Warnings.Count == 0 && drawing.Unsupported.Count == 0 && x.Warnings.Count == 0 && outlines.Count == ZOutlines.Length
                    && paths == string.Join(",", ZOutlines.Select(o => o.Path)),
                "z: the eleven H1/X1/Z1/K1/L1/L2/W1 outlines are kept and exploded in order; bulges not tessellated",
                "reader warnings " + string.Join(",", drawing.Warnings.Select(w => w.Code)) + " explode warnings " + string.Join(",", x.Warnings.Select(w => w.Code)) + " paths " + paths);
            foreach (var (path, corners, midpoints) in ZOutlines)
            {
                var outline = outlines.Where(o => string.Join(">", o.Provenance.InsertPath) == path).ToList();
                if (outline.Count != 1 || outline[0].Vertices.Count != 4 || !outline[0].Closed)
                {
                    c(false, "z: " + path + " outline equals ezdxf", "found " + outline.Count.ToString(CultureInfo.InvariantCulture) + " outlines");
                    continue;
                }
                var v = outline[0].Vertices;
                var mids = Enumerable.Range(0, 4).Select(i => SegmentMidpoint(v[i].Point, v[(i + 1) % 4].Point, v[i].Bulge)).ToList();
                var worst = Enumerable.Range(0, 4).Max(i => new[]
                {
                    Math.Abs(v[i].Point.X - corners[2 * i]), Math.Abs(v[i].Point.Y - corners[2 * i + 1]),
                    Math.Abs(mids[i].X - midpoints[2 * i]), Math.Abs(mids[i].Y - midpoints[2 * i + 1]),
                }.Max());
                c(worst <= Eps,
                    "z: " + path + " corners and segment (arc) midpoints equal ezdxf 1.4.2 within 1e-6",
                    "corners " + string.Join(";", v.Select(p => P(p.Point))) + " midpoints " + string.Join(";", mids.Select(P)) + " max error " + F(worst));
            }
        }
        catch (Exception error)
        {
            c(false, "z unexpected exception", error.GetType().Name + ": " + error.Message);
        }
    }

    // ---------- P6-38: leaf z under near +-Z inserts, heavy POLYLINE, Z scale validation, z members after explode ----------

    // Same parameters as LEAF_Z, TAG_INSERT, TAG_ATTRIB and LEAF_CASES in tests/cad/tools/minsert_ezdxf_centres.py, plus
    // the model-space tilted LWPOLYLINE MP (a copy of LP) and LINE ML, read but never placed.
    private static string LeafDrawing() =>
        G("0 SECTION|2 HEADER|9 $INSUNITS|70 4|0 ENDSEC")
        + G("0 SECTION|2 BLOCKS|0 BLOCK|5 Z0|8 0|2 LZ|70 0|10 40|20 -30|30 0")
        + G("0 LINE|5 LA|8 0|10 100|20 200|30 1500|11 900|21 250|31 -700")
        + G("0 CIRCLE|5 LC|8 0|10 300|20 400|30 1100|40 150")
        + G("0 CIRCLE|5 LD|8 0|10 300|20 400|30 600|40 120|210 0|220 0|230 -1")
        + G("0 ARC|5 LE|8 0|10 500|20 -300|30 900|40 200|50 10|51 130")
        + G("0 ARC|5 LF|8 0|10 -200|20 100|30 700|40 250|50 20|51 200|210 0|220 0|230 -1")
        + G("0 ARC|5 LG|8 0|10 250|20 -150|30 500|40 90|50 0|51 90|210 1e-7|220 -1e-7|230 -1")
        + G("0 TEXT|5 LT|8 0|10 50|20 60|30 1300|40 100|50 15|1 Sala")
        + G("0 TEXT|5 LU|8 0|10 120|20 -80|30 400|40 100|50 30|1 Cocina|210 0|220 0|230 -1")
        + G("0 MTEXT|5 LM|8 0|10 700|20 800|30 -1600|40 120|1 Bano")
        + G("0 ATTDEF|5 LK|8 0|10 400|20 100|30 2100|40 80|1 X|3 K?|2 K|70 2")
        + G("0 LWPOLYLINE|5 LP|8 0|90 2|70 0|38 200|10 100|20 500|10 1300|20 500|210 0.6|220 0|230 0.8")
        + G("0 POLYLINE|5 LH|8 0|66 1|10 0|20 0|30 800|70 1")
        + G("0 VERTEX|5 V1|8 0|10 0|20 0|30 0|0 VERTEX|5 V2|8 0|10 1000|20 0|30 0|0 VERTEX|5 V3|8 0|10 1000|20 600|30 0|0 SEQEND|5 V4|8 0")
        + G("0 ENDBLK|5 Z1|8 0")
        + G("0 BLOCK|5 T0|8 0|2 TAG|70 0|10 0|20 0|30 0|0 LINE|5 T1|8 0|10 0|20 0|11 500|21 0|0 ENDBLK|5 T2|8 0")
        + G("0 BLOCK|5 H0|8 0|2 LHOLD|70 0|10 10|20 20|30 0")
        + G("0 INSERT|5 TI|8 0|2 TAG|66 1|10 2000|20 1000|30 1200|50 20")
        + G("0 ATTRIB|5 TA|8 0|10 2100|20 1050|30 1900|40 150|1 Living|2 ROOM|70 0|0 SEQEND|5 TS|8 0")
        + G("0 ENDBLK|5 H2|8 0|0 ENDSEC")
        + G("0 SECTION|2 ENTITIES")
        + G("0 INSERT|5 Q1|8 ROOMS|2 LZ|10 50000|20 20000|30 700|41 1.2|42 1.2|50 25|210 3e-7|220 -2e-7|230 1")
        + G("0 INSERT|5 Q2|8 ROOMS|2 LZ|10 -40000|20 30000|30 -300|41 0.9|42 0.9|43 -1.5|50 -40|210 -2e-7|220 1e-7|230 -1")
        + G("0 INSERT|5 Q3|8 ROOMS|2 LHOLD|10 20000|20 -50000|30 0|50 10|210 2e-7|220 3e-7|230 -1")
        + G("0 LWPOLYLINE|5 MP|8 0|90 2|70 0|38 200|10 100|20 500|10 1300|20 500|210 0.6|220 0|230 0.8")
        + G("0 LINE|5 ML|8 0|10 0|20 0|30 75|11 10|21 0|31 -25")
        + G("0 ENDSEC|0 EOF");

    // ezdxf 1.4.2 WCS points (generator section P6-38 leaf_main): (top insert, leaf handle, point, x, y). ROOM is the
    // ATTRIB TA of INSERT TI inside LHOLD. Each point moves by 7e-5 to 6.3e-4 in ezdxf when its z is dropped.
    private static readonly (string Top, string Handle, string Label, double X, double Y)[] LeafPoints =
    {
        ("Q1", "LA", "start", 49948.61218042518, 20280.569024067045),
        ("Q1", "LA", "end", 50793.30990027591, 20740.661462560303),
        ("Q1", "LC", "center", 50064.69754649622, 20599.5113557736),
        ("Q1", "LD", "center", 49412.15542982984, 20295.226547320304),
        ("Q1", "LE", "center", 50637.21069524714, 19939.641237480595),
        ("Q1", "LE", "start", 50833.807185876496, 20077.299582204843),
        ("Q1", "LE", "mid", 50616.293316987714, 20178.727965022612),
        ("Q1", "LE", "end", 50419.69682635836, 20041.069620298364),
        ("Q1", "LF", "center", 50108.08264627845, 20222.526721031532),
        ("Q1", "LF", "start", 49809.22423685094, 20196.379998207234),
        ("Q1", "LF", "mid", 50081.93592345417, 20521.38513045905),
        ("Q1", "LF", "end", 50406.94105570596, 20248.673443855827),
        ("Q1", "LG", "center", 49745.46205953642, 19722.4204545592),
        ("Q1", "LG", "start", 49647.58081853646, 19676.777682291206),
        ("Q1", "LG", "mid", 49643.97525649154, 19759.358630038372),
        ("Q1", "LG", "end", 49699.81928726842, 19820.301695559156),
        ("Q1", "LT", "insert", 49965.23352117541, 20102.95226014045),
        ("Q1", "LU", "insert", 49851.34609059236, 19864.478766523192),
        ("Q1", "LM", "insert", 50296.8677086383, 21237.396399186717),
        ("Q1", "LK", "insert", 50325.59735516724, 20323.954543849297),
        ("Q1", "LP", "v0", 49586.04970894535, 19979.098370268894),
        ("Q1", "LP", "v1", 48977.47941203885, 21284.181583601643),
        ("Q2", "LA", "start", 39825.57707486626, 30123.86041380241),
        ("Q2", "LA", "end", 39245.09897338471, 29695.52566476846),
        ("Q2", "LC", "center", 39571.98718536127, 30146.046703820248),
        ("Q2", "LD", "center", 39985.65067464552, 30493.152268050977),
        ("Q2", "LE", "center", 39839.05531970236, 29547.736964911717),
        ("Q2", "LE", "start", 39683.17074702116, 29457.736964911717),
        ("Q2", "LE", "mid", 39683.17074702117, 29637.736964911717),
        ("Q2", "LE", "end", 39839.05531970236, 29727.736964911717),
        ("Q2", "LF", "center", 39814.48329985734, 29997.065859049915),
        ("Q2", "LF", "start", 39926.98329985734, 30191.92157490141),
        ("Q2", "LF", "mid", 39619.627584005844, 30109.56585904991),
        ("Q2", "LF", "end", 39701.98329985734, 29802.210143198416),
        ("Q2", "LG", "center", 40269.35856595343, 30085.034747873753),
        ("Q2", "LG", "start", 40331.408165846064, 30137.100544258363),
        ("Q2", "LG", "mid", 40276.41818111599, 30165.726518419182),
        ("Q2", "LG", "end", 40217.29276956882, 30147.084347766388),
        ("Q2", "LT", "insert", 39941.040253627114, 30056.264286405312),
        ("Q2", "LU", "insert", 40139.23578224482, 30058.089445854363),
        ("Q2", "LM", "insert", 39064.8068363513, 30190.419568855927),
        ("Q2", "LK", "insert", 39676.59614009593, 29881.363669306338),
        ("Q2", "LP", "v0", 40145.41484728474, 30274.749932434497),
        ("Q2", "LP", "v1", 39451.204228823306, 31102.07793100299),
        ("Q3", "ROOM", "insert", -21879.39020079522, -48622.722753071364),
    };

    // A z member of a Bim.Cad entity by name (StartZ/EndZ of a LINE, Elevation of the others); null when it has none.
    private static double? Member(object entity, string name) => (entity, name) switch
    {
        (DxfLine line, "StartZ") => line.StartZ,
        (DxfLine line, "EndZ") => line.EndZ,
        (DxfCircle circle, "Elevation") => circle.Elevation,
        (DxfArc arc, "Elevation") => arc.Elevation,
        (DxfText text, "Elevation") => text.Elevation,
        (DxfAttribute attribute, "Elevation") => attribute.Elevation,
        (DxfPolyline polyline, "Elevation") => polyline.Elevation,
        _ => null,
    };

    private static Point2 At(Point2 center, double radius, double degrees) =>
        new(center.X + radius * Math.Cos(degrees * Math.PI / 180), center.Y + radius * Math.Sin(degrees * Math.PI / 180));

    private static Dictionary<string, Point2> LeafPointsOf(DxfEntity entity)
    {
        var points = new Dictionary<string, Point2>(StringComparer.Ordinal);
        switch (entity)
        {
            case DxfLine line: points["start"] = line.Start; points["end"] = line.End; break;
            case DxfCircle circle: points["center"] = circle.Center; break;
            case DxfArc arc:
                var span = arc.EndAngleDegrees - arc.StartAngleDegrees;
                if (span <= 0) span += 360;
                points["center"] = arc.Center;
                points["start"] = At(arc.Center, arc.Radius, arc.StartAngleDegrees);
                points["mid"] = At(arc.Center, arc.Radius, arc.StartAngleDegrees + span / 2);
                points["end"] = At(arc.Center, arc.Radius, arc.EndAngleDegrees);
                break;
            case DxfText text: points["insert"] = text.Insertion; break;
            case DxfAttribute attribute: points["insert"] = attribute.Insertion; break;
            case DxfPolyline polyline:
                for (var k = 0; k < polyline.Vertices.Count; k++) points["v" + k.ToString(CultureInfo.InvariantCulture)] = polyline.Vertices[k].Point;
                break;
        }
        return points;
    }

    private static double Distance(Point2 a, double x, double y) => Math.Max(Math.Abs(a.X - x), Math.Abs(a.Y - y));

    private static void RunLeaf(Action<bool, string, string> c)
    {
        try
        {
            var text = LeafDrawing();
            var drawing = DxfReader.Parse(new UTF8Encoding(false).GetBytes(text), "leaf-ezdxf.dxf");
            var x = drawing.Explode();
            var unsupported = string.Join(",", drawing.Unsupported.Select(u => u.Handle + ":" + u.EntityType + ":" + (u.Reason ?? "null")));
            c(drawing.Warnings.Count == 0 && x.Warnings.Count == 0 && unsupported == "LH:POLYLINE:null",
                "leaf: no reader or explode warning; the heavy POLYLINE LH is the one unsupported entity (not modelled, Reason null)",
                "reader " + string.Join(",", drawing.Warnings.Select(w => w.Code)) + " explode " + string.Join(",", x.Warnings.Select(w => w.Code)) + " unsupported " + unsupported);

            // Every ezdxf point within 1e-6; an arc's end points as an unordered pair (a mirror swaps the end angles).
            foreach (var group in LeafPoints.GroupBy(p => (p.Top, p.Handle)))
            {
                var (top, handle) = group.Key;
                var leafHandle = handle == "ROOM" ? "TA" : handle;
                var found = x.Entities.Where(e => e.Provenance.Handle == leafHandle && e.Provenance.InsertPath.Count > 0 && e.Provenance.InsertPath[0] == top).ToList();
                var name = "leaf: " + top + " " + handle + " (" + (found.Count == 1 ? found[0].Provenance.EntityType : "?") + ")";
                if (found.Count != 1)
                {
                    c(false, name + " equals ezdxf 1.4.2 within 1e-6", "found " + found.Count.ToString(CultureInfo.InvariantCulture) + " entities");
                    continue;
                }
                var got = LeafPointsOf(found[0]);
                var expected = group.ToDictionary(p => p.Label, p => (p.X, p.Y));
                var worst = 0.0;
                foreach (var (label, (ex, ey)) in expected)
                {
                    if (!got.TryGetValue(label, out var point)) { worst = double.PositiveInfinity; continue; }
                    if (label is "start" or "end" && found[0] is DxfArc)
                    {
                        var other = got[label == "start" ? "end" : "start"];
                        var (ox, oy) = expected[label == "start" ? "end" : "start"];
                        worst = Math.Max(worst, Math.Min(Math.Max(Distance(point, ex, ey), Distance(other, ox, oy)), Math.Max(Distance(other, ex, ey), Distance(point, ox, oy))));
                    }
                    else worst = Math.Max(worst, Distance(point, ex, ey));
                }
                c(worst <= Eps && got.Count == expected.Count, name + " equals ezdxf 1.4.2 within 1e-6",
                    string.Join(" ", got.Select(kv => kv.Key + "=" + P(kv.Value))) + " max error " + F(worst));
            }
            c(!x.Entities.Any(e => e.Provenance.Handle is "LH" or "V1" or "V2" or "V3"),
                "leaf: limit pinned: the heavy POLYLINE LH (elevation 800) is not modelled, so nothing of it is placed (ezdxf would place it; generator prints it)",
                string.Join(",", x.Entities.Select(e => e.Provenance.Handle)));

            // What the reader keeps: z members in the coordinates each entity lives in.
            var lz = drawing.Blocks.Single(b => b.Name == "LZ").Entities.ToDictionary(e => e.Provenance.Handle!, StringComparer.Ordinal);
            var room = drawing.Blocks.Single(b => b.Name == "LHOLD").Entities.OfType<DxfInsert>().Single().Attributes.Single();
            var read = new (string Name, object Entity, string Member, double Expected, double Tolerance)[]
            {
                ("LA", lz["LA"], "StartZ", 1500, 0), ("LA", lz["LA"], "EndZ", -700, 0), ("LC", lz["LC"], "Elevation", 1100, 0),
                ("LD", lz["LD"], "Elevation", -600, 0), ("LE", lz["LE"], "Elevation", 900, 0), ("LF", lz["LF"], "Elevation", -700, 0),
                ("LG", lz["LG"], "Elevation", -500, 1e-4), ("LT", lz["LT"], "Elevation", 1300, 0), ("LU", lz["LU"], "Elevation", -400, 0),
                ("LM", lz["LM"], "Elevation", -1600, 0), ("LK", lz["LK"], "Elevation", 2100, 0), ("TA", room, "Elevation", 1900, 0),
                ("LP", lz["LP"], "Elevation", 460, 1e-9),
            };
            foreach (var (name, entity, member, expected, tolerance) in read)
            {
                var value = Member(entity, member);
                c(value is { } v && Math.Abs(v - expected) <= tolerance, "leaf: reader keeps " + name + " " + member + " = " + F(expected) + (tolerance > 0 ? " within " + F(tolerance) : ""),
                    value is null ? "no member" : F(value.Value));
            }
            // P6-30 tilted LWPOLYLINE: its Elevation is the world z of its plane (0.6 * 500 + 0.8 * 200), not group 38.
            var mp = drawing.Entities.OfType<DxfPolyline>().Single(e => e.Provenance.Handle == "MP");
            c(Math.Abs(mp.Elevation - 460) <= 1e-9 && Distance(mp.Vertices[0].Point, -280, 100) <= 1e-9 && Distance(mp.Vertices[1].Point, -280, 1300) <= 1e-9,
                "leaf: the model-space tilted LWPOLYLINE MP keeps Elevation 460 (world z of its plane; group 38 is 200) and its world XY",
                F(mp.Elevation) + " " + string.Join(";", mp.Vertices.Select(v => P(v.Point))));

            // Pinned: the explode consumes the z members; a placed copy reports 0, a model-space entity keeps them.
            var placedZ = x.Entities.Where(e => e.Provenance.InsertPath.Count > 0)
                .SelectMany(e => new[] { "StartZ", "EndZ", "Elevation" }.Select(m => (e, m, Member(e, m))).Where(t => t.Item3 is not null))
                .Where(t => t.Item3 != 0).Select(t => t.e.Provenance.Handle + "." + t.m + "=" + F(t.Item3!.Value)).ToList();
            var placedTypes = x.Entities.Where(e => e.Provenance.InsertPath.Count > 0).Select(e => e.GetType().Name).Distinct().OrderBy(n => n, StringComparer.Ordinal);
            var everyMember = x.Entities.Where(e => e.Provenance.InsertPath.Count > 0 && e is not DxfInsert).All(e => Member(e, e is DxfLine ? "StartZ" : "Elevation") is not null);
            c(placedZ.Count == 0 && everyMember, "leaf: limit pinned: every placed copy (" + string.Join(",", placedTypes) + ") has its z members 0",
                string.Join(",", placedZ) + " every type has a z member " + everyMember);
            var ml = x.Entities.Single(e => e.Provenance.Handle == "ML");
            var mpx = x.Entities.Single(e => e.Provenance.Handle == "MP");
            c(Member(ml, "StartZ") == 75 && Member(ml, "EndZ") == -25 && Member(mpx, "Elevation") is { } mpz && Math.Abs(mpz - 460) <= 1e-9,
                "leaf: model-space LINE ML (z 75/-25) and LWPOLYLINE MP (460) keep their z members through the explode",
                F(Member(ml, "StartZ") ?? double.NaN) + "/" + F(Member(ml, "EndZ") ?? double.NaN) + " " + F(Member(mpx, "Elevation") ?? double.NaN));
        }
        catch (Exception error)
        {
            c(false, "leaf unexpected exception", error.GetType().Name + ": " + error.Message);
        }

        // INSERT group 43: zero refused like a zero 41/42; negative kept (a mirror in z, like a negative 41/42).
        try
        {
            var text = G("0 SECTION|2 HEADER|9 $INSUNITS|70 4|0 ENDSEC")
                + G("0 SECTION|2 BLOCKS|0 BLOCK|5 B0|8 0|2 BOX|70 0|10 0|20 0|30 0|0 LINE|5 B1|8 0|10 0|20 0|11 100|21 0|0 ENDBLK|5 B2|8 0|0 ENDSEC")
                + G("0 SECTION|2 ENTITIES|0 INSERT|5 S0|8 0|2 BOX|10 0|20 0|43 0|0 INSERT|5 SN|8 0|2 BOX|10 500|20 0|43 -2|0 INSERT|5 SX|8 0|2 BOX|10 900|20 0|41 0|0 ENDSEC|0 EOF");
            var lines = text.Split('\n');
            int LineOfHandle(string handle) => Array.IndexOf(lines, handle) - 2;
            var drawing = DxfReader.Parse(new UTF8Encoding(false).GetBytes(text), "zscale.dxf");
            var warnings = string.Join(",", drawing.Warnings.Select(w => w.Code + "@" + w.Line.ToString(CultureInfo.InvariantCulture) + ":" + w.Message));
            var expected = "entity-invalid@" + LineOfHandle("S0").ToString(CultureInfo.InvariantCulture) + ":INSERT with a zero scale factor; entity skipped,"
                + "entity-invalid@" + LineOfHandle("SX").ToString(CultureInfo.InvariantCulture) + ":INSERT with a zero scale factor; entity skipped";
            c(warnings == expected && drawing.Entities.Count == 1 && drawing.Entities[0] is DxfInsert { ScaleZ: -2.0 } sn && sn.Provenance.Handle == "SN",
                "zscale: INSERT group 43 = 0 is refused entity-invalid like 41 = 0; 43 = -2 is kept",
                warnings + " entities " + string.Join(",", drawing.Entities.Select(e => e.Provenance.Handle + (e is DxfInsert i ? ":" + F(i.ScaleZ) : ""))));
            var placed = drawing.Explode().Entities.OfType<DxfLine>().ToList();
            c(placed.Count == 1 && P(placed[0].Start) == "(500,0)" && P(placed[0].End) == "(600,0)", "zscale: the negative-Z-scale insert places its block unchanged in plan",
                string.Join(";", placed.Select(l => P(l.Start) + "-" + P(l.End))));

            // A z group that is not a number never failed a file (it was not read); it still does not, and reads as 0.
            var bad = DxfReader.Parse(new UTF8Encoding(false).GetBytes(G("0 SECTION|2 ENTITIES|0 LINE|5 BZ|8 0|10 0|20 0|30 abc|11 10|21 0|31 1e999|0 TEXT|5 BT|8 0|10 1|20 2|30 x|40 100|1 T|0 ENDSEC|0 EOF")), "badz.dxf");
            c(bad.Warnings.Count == 0 && bad.Entities.Count == 2 && Member(bad.Entities[0], "StartZ") == 0 && Member(bad.Entities[0], "EndZ") == 0 && Member(bad.Entities[1], "Elevation") == 0,
                "zscale: a non-numeric or non-finite z group (LINE 30/31, TEXT 30) still reads, as z 0, without a warning",
                string.Join(",", bad.Warnings.Select(w => w.Code)) + " entities " + bad.Entities.Count.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception error)
        {
            c(false, "zscale unexpected exception", error.GetType().Name + ": " + error.Message);
        }

        // CadToBimIr: its scaled candidate copies drop the z members (pinned); a wall drawing gives the same draft
        // bytes whatever the z of its LINEs and LWPOLYLINEs (heights come from the options, not the drawing).
        try
        {
            string Walls(string z) => G("0 SECTION|2 HEADER|9 $INSUNITS|70 4|0 ENDSEC|0 SECTION|2 ENTITIES")
                + G("0 LINE|5 W1|8 WALLS|10 0|20 0|30 " + z + "|11 5000|21 0|31 " + z)
                + G("0 LINE|5 W2|8 WALLS|10 0|20 200|30 " + z + "|11 5000|21 200|31 " + z)
                + G("0 LWPOLYLINE|5 W3|8 WALLS|90 2|70 0|38 " + z + "|10 0|20 1000|10 5000|20 1000")
                + G("0 LINE|5 W4|8 WALLS|10 0|20 1200|30 " + z + "|11 5000|21 1200|31 " + z)
                + G("0 ENDSEC|0 EOF");
            var options = new CadToBimIrOptions(Guid.Parse("6f0c6a52-4d3b-4a8e-9b51-2f6f9d0e7a11"));
            var flat = CadToBimIr.FromDxf(DxfReader.Parse(new UTF8Encoding(false).GetBytes(Walls("0")), "walls-z.dxf"), options);
            var high = CadToBimIr.FromDxf(DxfReader.Parse(new UTF8Encoding(false).GetBytes(Walls("2500")), "walls-z.dxf"), options);
            var a = JsonSerializer.Serialize(flat);
            var b = JsonSerializer.Serialize(high);
            c(flat.Walls.Count > 0 && a == b, "draft: limit pinned: CadToBimIr ignores LINE z and LWPOLYLINE elevation (same draft bytes at z 0 and 2500)",
                flat.Walls.Count.ToString(CultureInfo.InvariantCulture) + " walls; " + a.Length.ToString(CultureInfo.InvariantCulture) + " vs " + b.Length.ToString(CultureInfo.InvariantCulture) + " chars; equal " + (a == b));
        }
        catch (Exception error)
        {
            c(false, "draft z unexpected exception", error.GetType().Name + ": " + error.Message);
        }
    }

    // ---------- P6-40: near-Z leaf z follow-ups: identity placements, justified text, MINSERT ATTRIBs ----------
    //
    // FollowDrawing() mirrors FOLLOW_* in tests/cad/tools/minsert_ezdxf_centres.py (section P6-40 follow_main), plus
    // block IDB placed by IQ with an identity XY transform (insertion 0, base 0, no rotation or scale) and the
    // model-space LINE FM, both checked here only (no ezdxf constant: an identity placement moves nothing).
    // * Identity placement: IQ's copies (LINE z 500/-300, CIRCLE, ARC, TEXT, LWPOLYLINE 38, constant ATTDEF, ATTRIB) and
    //   IN's LINE N1 (z 0 to 800 under a near +Z normal at the origin: only its end moves, 2.4e-4 in ezdxf) are placed
    //   copies, so their z members are 0 like every other placed copy; FM (model space) is the object the reader made.
    // * Justified TEXT (72 != 0): DxfReader keeps the group 10 point, which equals ezdxf's transformed insert. Justified
    //   ATTRIB/ATTDEF (72 1/2/4 or 74 != 0): DxfReader keeps the group 11 point, which must take its z from group 31
    //   (ezdxf's transformed align_point): JL/JN/JS have 31 != 30 and are 2.1e-4/5.7e-5..1.2e-4/1.5e-4 off when the z
    //   of group 30 is used; JK/JM/JR have 31 = 30.
    // * MINSERT ATTRIBs: every cell of MI (+Z, ATTRIB z 1900) and MJ (exact -Z, ATTRIB OCS z 700) inside MHOLD under M1
    //   (near +Z, scale 1.1) and M2 (near -Z, Z scale -1.5) equals ezdxf's multi_insert() cells (2.1e-4..8.6e-4 move
    //   when the ATTRIB z is dropped).
    // * A non-finite INSERT group 43 is refused like a non-finite 41/42 (typed bad-number at the value's line).
    // * CadToBimIr's candidate copies (Input.AddCandidate, private, reached by reflection) have z members 0.
    private static string FollowDrawing() =>
        G("0 SECTION|2 HEADER|9 $INSUNITS|70 4|0 ENDSEC")
        + G("0 SECTION|2 BLOCKS|0 BLOCK|5 D0|8 0|2 IDB|70 0|10 0|20 0|30 0")
        + G("0 LINE|5 I1|8 0|10 10|20 20|30 500|11 300|21 20|31 -300")
        + G("0 CIRCLE|5 I2|8 0|10 100|20 100|30 200|40 50")
        + G("0 ARC|5 I3|8 0|10 200|20 0|30 150|40 40|50 0|51 90")
        + G("0 TEXT|5 I4|8 0|10 5|20 5|30 350|40 50|1 T")
        + G("0 LWPOLYLINE|5 I5|8 0|90 2|70 0|38 250|10 0|20 300|10 400|20 300")
        + G("0 ATTDEF|5 I6|8 0|10 30|20 40|30 400|40 50|1 K|3 K?|2 IK|70 2")
        + G("0 ENDBLK|5 D1|8 0")
        + G("0 BLOCK|5 N0|8 0|2 IDN|70 0|10 0|20 0|30 0|0 LINE|5 N1|8 0|10 0|20 0|30 0|11 1000|21 0|31 800|0 ENDBLK|5 N2|8 0")
        + G("0 BLOCK|5 J0|8 0|2 JZ|70 0|10 40|20 -30|30 0")
        + G("0 TEXT|5 JT|8 0|10 50|20 60|30 1300|11 150|21 60|31 1300|40 100|72 1|1 JT")
        + G("0 TEXT|5 JU|8 0|10 120|20 -80|30 400|11 220|21 -80|31 400|40 100|72 2|1 JU|210 0|220 0|230 -1")
        + G("0 ATTDEF|5 JK|8 0|10 400|20 100|30 2100|11 460|21 100|31 2100|40 100|1 X|3 JK?|2 JK|70 2|72 1")
        + G("0 ATTDEF|5 JL|8 0|10 300|20 300|30 1000|11 350|21 320|31 1700|40 100|1 X|3 JL?|2 JL|70 2|72 4")
        + G("0 ATTDEF|5 JM|8 0|10 -150|20 250|30 800|11 -90|21 250|31 800|40 100|1 X|3 JM?|2 JM|70 2|72 2|74 1|210 0|220 0|230 -1")
        + G("0 ATTDEF|5 JN|8 0|10 250|20 -150|30 500|11 300|21 -120|31 900|40 100|1 X|3 JN?|2 JN|70 2|74 2|210 1e-7|220 -1e-7|230 -1")
        + G("0 ENDBLK|5 JE|8 0")
        + G("0 BLOCK|5 T0|8 0|2 TAG|70 0|10 0|20 0|30 0|0 LINE|5 T1|8 0|10 0|20 0|11 500|21 0|0 ENDBLK|5 T2|8 0")
        + G("0 BLOCK|5 H0|8 0|2 JHOLD|70 0|10 10|20 20|30 0")
        + G("0 INSERT|5 JI|8 0|2 TAG|66 1|10 2000|20 1000|30 1200|50 20")
        + G("0 ATTRIB|5 JR|8 0|10 2100|20 1050|30 1900|11 2160|21 1050|31 1900|40 150|1 JR|2 R|70 0|72 1")
        + G("0 ATTRIB|5 JS|8 0|10 2000|20 900|30 1900|11 2050|21 950|31 1400|40 150|1 JS|2 S|70 0|74 2|0 SEQEND|5 JQ|8 0")
        + G("0 ENDBLK|5 H1|8 0")
        + G("0 BLOCK|5 K0|8 0|2 MHOLD|70 0|10 0|20 0|30 0")
        + G("0 INSERT|5 MI|8 0|2 TAG|66 1|10 1000|20 500|30 1200|50 30|70 2|71 3|44 3000|45 2000")
        + G("0 ATTRIB|5 MA|8 0|10 1100|20 550|30 1900|40 120|1 MA|2 ROOM|70 0|0 SEQEND|5 MS|8 0")
        + G("0 INSERT|5 MJ|8 0|2 TAG|66 1|10 -2000|20 800|30 600|50 15|70 3|71 2|44 2500|45 -1500|210 0|220 0|230 -1")
        + G("0 ATTRIB|5 MB|8 0|10 -1900|20 850|30 700|40 120|1 MB|2 ROOM|70 0|210 0|220 0|230 -1|0 SEQEND|5 MT|8 0")
        + G("0 ENDBLK|5 K1|8 0|0 ENDSEC")
        + G("0 SECTION|2 ENTITIES")
        + G("0 INSERT|5 IQ|8 ROOMS|2 IDB|66 1|10 0|20 0|30 0")
        + G("0 ATTRIB|5 IA|8 0|10 50|20 50|30 600|40 50|1 v|2 IA|70 0|0 SEQEND|5 IS|8 0")
        + G("0 INSERT|5 IN|8 ROOMS|2 IDN|10 0|20 0|30 0|210 3e-7|220 -2e-7|230 1")
        + G("0 INSERT|5 J1|8 ROOMS|2 JZ|10 50000|20 20000|30 700|41 1.2|42 1.2|50 25|210 3e-7|220 -2e-7|230 1")
        + G("0 INSERT|5 J2|8 ROOMS|2 JZ|10 -40000|20 30000|30 -300|41 0.9|42 0.9|43 -1.5|50 -40|210 -2e-7|220 1e-7|230 -1")
        + G("0 INSERT|5 J3|8 ROOMS|2 JHOLD|10 20000|20 -50000|30 0|50 10|210 2e-7|220 3e-7|230 -1")
        + G("0 INSERT|5 M1|8 ROOMS|2 MHOLD|10 70000|20 10000|30 400|41 1.1|42 1.1|50 35|210 -3e-7|220 2e-7|230 1")
        + G("0 INSERT|5 M2|8 ROOMS|2 MHOLD|10 -70000|20 -10000|30 0|43 -1.5|50 -20|210 2e-7|220 -3e-7|230 -1")
        + G("0 LINE|5 FM|8 0|10 0|20 0|30 75|11 10|21 0|31 -25")
        + G("0 ENDSEC|0 EOF");

    // ezdxf 1.4.2 WCS points (generator section P6-40 follow_main): (insert path, leaf handle, x, y). N1 is the LINE end;
    // the others are insertion points (TEXT group 10, ATTRIB/ATTDEF group 11 when justified, else group 10).
    private static readonly (string Path, string Handle, double X, double Y)[] FollowPoints =
    {
        ("IN", "N1", 1000.000239999955, -0.0001599999999999896),
        ("J1", "JT", 49965.23352117541, 20102.95226014045),
        ("J1", "JU", 49851.34609059236, 19864.478766523192),
        ("J1", "JK", 50390.851515833885, 20354.383058694628),
        ("J1", "JL", 50159.64754684552, 20537.862783922526),
        ("J1", "JM", 49912.378701276306, 20329.87653214835),
        ("J1", "JN", 49675.86927868, 19729.69049597098),
        ("J2", "JT", 39941.040253627114, 30056.264286405312),
        ("J2", "JU", 40139.23578224482, 30058.089445854363),
        ("J2", "JK", 39635.2297401675, 29846.653138383263),
        ("J2", "JL", 39583.79607331835, 30061.96597147979),
        ("J2", "JM", 39803.54534241844, 30164.117847229943),
        ("J2", "JN", 40286.475175995, 30134.643399555905),
        ("J3>JI", "JR", -21938.478665975952, -48612.30386241135),
        ("J3>JI", "JS", -21847.514730911298, -48729.88608725592),
        ("M1>MI[0,0]", "MA", 70644.1595395948, 11189.614934779383),
        ("M1>MI[0,1]", "MA", 72038.79980333922, 14180.430632000269),
        ("M1>MI[1,0]", "MA", 68650.2824081143, 12119.375110608904),
        ("M1>MI[1,1]", "MA", 70044.92267185873, 15110.19080782979),
        ("M1>MI[2,0]", "MA", 66656.40527663383, 13049.135286438424),
        ("M1>MI[2,1]", "MA", 68051.04554037825, 16039.95098365931),
        ("M1>MJ[0,0]", "MB", 71175.73389457329, 11964.681853383656),
        ("M1>MJ[0,1]", "MB", 68591.57918741209, 11024.126459238087),
        ("M1>MJ[0,2]", "MB", 66007.4244802509, 10083.571065092516),
        ("M1>MJ[1,0]", "MB", 71740.06713106052, 10414.189029086938),
        ("M1>MJ[1,1]", "MB", 69155.91242389934, 9473.633634941367),
        ("M1>MJ[1,2]", "MB", 66571.75771673814, 8533.078240795798),
        ("M2>MI[0,0]", "MA", 68778.22646830442, -9859.390361225544),
        ("M2>MI[0,1]", "MA", 65823.80320926789, -9338.445828224776),
        ("M2>MI[1,0]", "MA", 69125.5228236384, -7889.774855201214),
        ("M2>MI[1,1]", "MA", 66171.09956460186, -7368.830322200449),
        ("M2>MI[2,0]", "MA", 69472.81917897236, -5920.159349176888),
        ("M2>MI[2,1]", "MA", 66518.39591993584, -5399.21481617612),
        ("M2>MJ[0,0]", "MB", 67923.86710867801, -9851.099859650307),
        ("M2>MJ[0,1]", "MB", 69971.74721940054, -8417.158768772755),
        ("M2>MJ[0,2]", "MB", 72019.62733012307, -6983.217677895203),
        ("M2>MJ[1,0]", "MB", 68784.23176320449, -11079.827926083739),
        ("M2>MJ[1,1]", "MB", 70832.11187392702, -9645.886835206187),
        ("M2>MJ[1,2]", "MB", 72879.99198464955, -8211.945744328637),
    };

    private static string ZMembers(DxfEntity e) => e switch
    {
        DxfLine l => "StartZ=" + F(l.StartZ) + " EndZ=" + F(l.EndZ),
        _ => Member(e, "Elevation") is { } z ? "Elevation=" + F(z) : "none",
    };

    private static bool ZeroZ(DxfEntity e) => e is DxfLine l ? l.StartZ == 0 && l.EndZ == 0 : Member(e, "Elevation") is null or 0.0;

    private static void RunFollow(Action<bool, string, string> c)
    {
        try
        {
            var text = FollowDrawing();
            var drawing = DxfReader.Parse(new UTF8Encoding(false).GetBytes(text), "follow-ezdxf.dxf");
            var x = drawing.Explode();
            c(drawing.Warnings.Count == 0 && drawing.Unsupported.Count == 0 && x.Warnings.Count == 0,
                "follow: no reader or explode warning and nothing unsupported",
                "reader " + string.Join(",", drawing.Warnings.Select(w => w.Code + "@" + w.Line.ToString(CultureInfo.InvariantCulture))) + " explode " + string.Join(",", x.Warnings.Select(w => w.Code)));

            // Identity placement: the same XY as read, z members 0 like every other placed copy.
            var idb = drawing.Blocks.Single(b => b.Name == "IDB").Entities.ToDictionary(e => e.Provenance.Handle!, StringComparer.Ordinal);
            var iq = x.Entities.Where(e => e.Provenance.InsertPath.SequenceEqual(new[] { "IQ" })).ToList();
            var iqHandles = string.Join(",", iq.Select(e => e.Provenance.Handle).OrderBy(h => h, StringComparer.Ordinal));
            var sameXY = iq.Where(e => idb.ContainsKey(e.Provenance.Handle!) && e is not DxfAttribute).All(e =>
            {
                var a = LeafPointsOf(e);
                var b = LeafPointsOf(idb[e.Provenance.Handle!]);
                return a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var q) && q == kv.Value);
            });
            var readZ = string.Join(" ", idb.Values.Select(e => e.Provenance.Handle + ":" + ZMembers(e)));
            c(iqHandles == "I1,I2,I3,I4,I5,I6,IA" && sameXY && idb.Values.Where(e => e is not DxfAttribute).All(e => !ZeroZ(e)),
                "follow: IQ is an identity placement: its copies of I1..I5 keep the read XY exactly (the read ones have z)",
                "placed " + iqHandles + " same XY " + sameXY + " read " + readZ);
            c(iq.Count == 7 && iq.All(ZeroZ),
                "follow: an identity placement zeroes the z members like every other placed copy (IQ: LINE, CIRCLE, ARC, TEXT, LWPOLYLINE, ATTDEF, ATTRIB)",
                string.Join(" ", iq.Select(e => e.Provenance.Handle + ":" + ZMembers(e))));
            var n1 = x.Entities.Where(e => e.Provenance.Handle == "N1").ToList();
            c(n1.Count == 1 && n1[0] is DxfLine { StartZ: 0.0, EndZ: 0.0 } nl && nl.Start == new Point2(0, 0),
                "follow: IN's LINE N1 (identity XY under a near +Z normal, z 0 to 800) is a placed copy with z members 0 and its start unmoved",
                string.Join(";", n1.Select(e => ZMembers(e) + (e is DxfLine l ? " start " + P(l.Start) : ""))));
            var fm = drawing.Entities.Single(e => e.Provenance.Handle == "FM");
            var fmx = x.Entities.Where(e => e.Provenance.Handle == "FM").ToList();
            c(fmx.Count == 1 && ReferenceEquals(fmx[0], fm) && fm is DxfLine { StartZ: 75.0, EndZ: -25.0 },
                "follow: the model-space LINE FM passes through the explode as the reader's own object, z 75/-25 kept",
                string.Join(";", fmx.Select(e => ZMembers(e) + " same object " + ReferenceEquals(e, fm))));

            // Every ezdxf point within 1e-6 (N1: the LINE end; the others: the insertion point).
            foreach (var (path, handle, ex, ey) in FollowPoints)
            {
                var found = x.Entities.Where(e => e.Provenance.Handle == handle && string.Join(">", e.Provenance.InsertPath) == path).ToList();
                var name = "follow: " + path + " " + handle + " (" + (found.Count == 1 ? found[0].Provenance.EntityType : "?") + ")";
                if (found.Count != 1)
                {
                    c(false, name + " equals ezdxf 1.4.2 within 1e-6", "found " + found.Count.ToString(CultureInfo.InvariantCulture) + " entities; paths "
                        + string.Join(",", x.Entities.Where(e => e.Provenance.Handle == handle).Select(e => string.Join(">", e.Provenance.InsertPath))));
                    continue;
                }
                var got = LeafPointsOf(found[0]);
                var label = handle == "N1" ? "end" : "insert";
                var ok = got.TryGetValue(label, out var point);
                var error = ok ? Distance(point, ex, ey) : double.PositiveInfinity;
                c(error <= Eps && ZeroZ(found[0]), name + " " + label + " equals ezdxf 1.4.2 within 1e-6 and the copy's z members are 0",
                    (ok ? P(point) : "no " + label) + " expected (" + F(ex) + "," + F(ey) + ") error " + F(error) + " " + ZMembers(found[0]));
            }
            var cells = x.Entities.Count(e => e.Provenance.Handle is "MA" or "MB");
            c(cells == 24, "follow: 24 MINSERT ATTRIB copies (MI 2x3 and MJ 3x2 under M1 and M2)", cells.ToString(CultureInfo.InvariantCulture));

            // What the reader keeps for a justified ATTRIB/ATTDEF: the group 11 point with the z of group 31.
            var jz = drawing.Blocks.Single(b => b.Name == "JZ").Entities.ToDictionary(e => e.Provenance.Handle!, StringComparer.Ordinal);
            var js = drawing.Blocks.Single(b => b.Name == "JHOLD").Entities.OfType<DxfInsert>().Single().Attributes.ToDictionary(a => a.Provenance.Handle!, StringComparer.Ordinal);
            var kept = new (string Name, DxfEntity Entity, double X, double Y, double Z, double Tolerance)[]
            {
                ("JT", jz["JT"], 50, 60, 1300, 0), ("JU", jz["JU"], -120, -80, -400, 0), ("JK", jz["JK"], 460, 100, 2100, 0),
                ("JL", jz["JL"], 350, 320, 1700, 0), ("JM", jz["JM"], 90, 250, -800, 0), ("JN", jz["JN"], -299.99991, -120.00009, -900.000018, 1e-8),
                ("JR", js["JR"], 2160, 1050, 1900, 0), ("JS", js["JS"], 2050, 950, 1400, 0),
            };
            foreach (var (name, entity, ex, ey, ez, tolerance) in kept)
            {
                var point = LeafPointsOf(entity)["insert"];
                var z = Member(entity, "Elevation")!.Value;
                c(Distance(point, ex, ey) <= Math.Max(tolerance, 1e-9) && Math.Abs(z - ez) <= Math.Max(tolerance, 1e-9),
                    "follow: reader keeps " + name + " at (" + F(ex) + "," + F(ey) + ") z " + F(ez) + (entity is DxfText ? " (TEXT: group 10)" : " (justified: group 11, z of group 31)"),
                    P(point) + " z " + F(z));
            }
        }
        catch (Exception error)
        {
            c(false, "follow unexpected exception", error.GetType().Name + ": " + error.Message);
        }

        // A non-finite group 43 is refused like a non-finite 41/42: the file fails with a typed bad-number at the value.
        try
        {
            string Scaled(string group) => G("0 SECTION|2 BLOCKS|0 BLOCK|5 B0|8 0|2 BOX|70 0|10 0|20 0|30 0|0 LINE|5 B1|8 0|10 0|20 0|11 100|21 0|0 ENDBLK|5 B2|8 0|0 ENDSEC")
                + G("0 SECTION|2 ENTITIES|0 INSERT|5 S0|8 0|2 BOX|10 0|20 0|" + group + "|0 ENDSEC|0 EOF");
            foreach (var group in new[] { "43 NaN", "43 inf", "43 -Infinity", "43 1e999", "41 NaN", "42 1e999" })
            {
                var text = Scaled(group);
                var expectedLine = Array.IndexOf(text.Split('\n'), group[3..]) + 1;
                string outcome;
                try
                {
                    var d = DxfReader.Parse(new UTF8Encoding(false).GetBytes(text), "scale-nonfinite.dxf");
                    outcome = "parsed: " + d.Entities.Count.ToString(CultureInfo.InvariantCulture) + " entities, warnings " + string.Join(",", d.Warnings.Select(w => w.Code));
                }
                catch (DxfFormatException e)
                {
                    outcome = e.Code + "@" + e.Line.ToString(CultureInfo.InvariantCulture) + ":" + e.Message;
                }
                c(outcome.StartsWith("bad-number@" + expectedLine.ToString(CultureInfo.InvariantCulture) + ":group " + group[..2] + " value is not a finite number", StringComparison.Ordinal),
                    "follow: INSERT group " + group + " is refused bad-number at line " + expectedLine.ToString(CultureInfo.InvariantCulture) + " (non-finite 43 like 41/42)", outcome);
            }
        }
        catch (Exception error)
        {
            c(false, "follow nonfinite unexpected exception", error.GetType().Name + ": " + error.Message);
        }

        // CadToBimIr's candidate copies, directly: Input.AddCandidate scales a LINE and an LWPOLYLINE that carry z and
        // the copies it keeps have z members 0 (the drop is pinned, not only its effect on draft bytes).
        try
        {
            const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var inputType = typeof(CadToBimIr).GetNestedType("Input", System.Reflection.BindingFlags.NonPublic) ?? throw new InvalidOperationException("CadToBimIr.Input not found");
            var budgetType = typeof(CadToBimIr).GetNestedType("Budget", System.Reflection.BindingFlags.NonPublic) ?? throw new InvalidOperationException("CadToBimIr.Budget not found");
            var add = inputType.GetMethod("AddCandidate", Any) ?? throw new InvalidOperationException("Input.AddCandidate not found");
            var provenance = new DxfProvenance("cand.dxf", "W1", "WALLS", "LINE", 3);
            var line = new DxfLine(provenance, new Point2(1, 2), new Point2(5, 2)) { StartZ = 500, EndZ = -300 };
            var polyline = new DxfPolyline(provenance with { Handle = "W2", EntityType = "LWPOLYLINE" }, new[] { new DxfVertex(new Point2(0, 3), 0), new DxfVertex(new Point2(4, 3), 0) }, false) { Elevation = 250 };
            foreach (var factor in new[] { 1.0, 1000.0 })
            {
                var budget = Activator.CreateInstance(budgetType, Any, null, new object[] { 1_000_000L }, CultureInfo.InvariantCulture);
                var input = Activator.CreateInstance(inputType, Any, null, new object?[] { "cand.dxf", "dxf", factor, 1000, budget }, CultureInfo.InvariantCulture);
                add.Invoke(input, new object?[] { line, factor, 0, null });
                add.Invoke(input, new object?[] { polyline, factor, 0, null });
                var candidates = (List<DxfEntity>)(inputType.GetProperty("Candidates", Any)?.GetValue(input) ?? throw new InvalidOperationException("Input.Candidates not found"));
                var ok = candidates.Count == 2 && candidates[0] is DxfLine { StartZ: 0.0, EndZ: 0.0 } l && l.Start == new Point2(factor, 2 * factor) && l.End == new Point2(5 * factor, 2 * factor)
                    && candidates[1] is DxfPolyline { Elevation: 0.0 } p && p.Vertices[1].Point == new Point2(4 * factor, 3 * factor);
                c(ok, "follow: draft: limit pinned directly: CadToBimIr's candidate copies scaled by " + F(factor) + " drop the z members (LINE 500/-300, LWPOLYLINE 250 -> 0)",
                    string.Join(" ", candidates.Select(e => e.Provenance.Handle + ":" + ZMembers(e) + " " + string.Join(";", LeafPointsOf(e).Values.Select(P)))));
            }
        }
        catch (Exception error)
        {
            c(false, "follow draft candidates unexpected exception", (error.InnerException ?? error).GetType().Name + ": " + (error.InnerException ?? error).Message);
        }
    }

    // ---------- P6-42: near-Z text follow-ups: strict 30/31, aligned/fit text, Relabel's attribute arm, handles ----------
    //
    // AlignedDrawing() mirrors ALIGN_* in tests/cad/tools/minsert_ezdxf_centres.py (section P6-42 align_main).
    // * Aligned / fit text (72 = 3 / 5): ezdxf's Text.get_placement() gives the group 10 point (insert) as p1 for ALIGNED
    //   and FIT (group 11 is the second point p2); DxfReader keeps the group 10 point with the z of group 30 for TEXT and
    //   for ATTRIB/ATTDEF (justified only for 72 = 1/2/4 or 74 != 0). Every leaf has 31 != 30, so taking the z of group
    //   31 would be 1.2e-4..3.6e-4 off; the group 11 point (p2) is not modelled (documented limit, pinned below).
    // * Strict 30/31: a justified ATTRIB/ATTDEF in its own near +-Z OCS places its group 11 point with the z of group 31,
    //   so a non-finite 31 (and a non-finite 30, which the unjustified reading refuses through the same OCS) fails the
    //   file with a typed bad-number like every other z group that moves XY in a near +-Z OCS (group 30 of a TEXT or an
    //   unjustified ATTDEF, group 38). Under an exact +-Z OCS the z only feeds Elevation, so a non-finite 30/31 still reads
    //   as 0 (the P6-38 tolerance, pinned both ways).
    // * Relabel's DxfAttribute arm (a non-definition attribute placed by an identity transform) is not reachable from
    //   DxfReader output (an ATTRIB without its INSERT is dropped as attrib-orphan, also inside a block; block entities
    //   hold only ATTDEFs, which EmitBlock routes through EmitAttribute), but it is reachable through the public API: a
    //   hand-built DxfBlock holding an attribute with IsDefinition false. Kept and pinned (the copy's Elevation is 0).
    // * Handles: every group 5 value of FollowDrawing() and AlignedDrawing() is unique (case-insensitive).
    private static string AlignedDrawing() =>
        G("0 SECTION|2 HEADER|9 $INSUNITS|70 4|0 ENDSEC")
        + G("0 SECTION|2 BLOCKS|0 BLOCK|5 A0|8 0|2 AF|70 0|10 40|20 -30|30 0")
        + G("0 TEXT|5 FA|8 0|10 50|20 60|30 1300|11 450|21 60|31 1700|40 100|72 3|1 FA")
        + G("0 TEXT|5 FF|8 0|10 120|20 -80|30 400|11 520|21 -80|31 900|40 100|72 5|1 FF|210 0|220 0|230 -1")
        + G("0 TEXT|5 FN|8 0|10 -200|20 150|30 600|11 200|21 150|31 1100|40 100|72 3|1 FN|210 1e-7|220 -1e-7|230 1")
        + G("0 ATTDEF|5 FB|8 0|10 300|20 300|30 1000|11 700|21 300|31 1700|40 100|1 X|3 FB?|2 FB|70 2|72 3|210 -1e-7|220 2e-7|230 -1")
        + G("0 ATTDEF|5 FG|8 0|10 -150|20 250|30 800|11 250|21 250|31 1200|40 100|1 X|3 FG?|2 FG|70 2|72 5")
        + G("0 ENDBLK|5 A9|8 0")
        + G("0 BLOCK|5 T0|8 0|2 TAG|70 0|10 0|20 0|30 0|0 LINE|5 T1|8 0|10 0|20 0|11 500|21 0|0 ENDBLK|5 T2|8 0")
        + G("0 BLOCK|5 H0|8 0|2 AHOLD|70 0|10 10|20 20|30 0")
        + G("0 INSERT|5 AI|8 0|2 TAG|66 1|10 2000|20 1000|30 1200|50 20")
        + G("0 ATTRIB|5 FR|8 0|10 2100|20 1050|30 1900|11 2500|21 1050|31 1400|40 150|1 FR|2 R|70 0|72 3")
        + G("0 ATTRIB|5 FS|8 0|10 -2000|20 900|30 1900|11 -1600|21 900|31 2600|40 150|1 FS|2 S|70 0|72 5|210 0|220 0|230 -1|0 SEQEND|5 AQ|8 0")
        + G("0 ENDBLK|5 H1|8 0|0 ENDSEC")
        + G("0 SECTION|2 ENTITIES")
        + G("0 INSERT|5 A1|8 ROOMS|2 AF|10 50000|20 20000|30 700|41 1.2|42 1.2|50 25|210 3e-7|220 -2e-7|230 1")
        + G("0 INSERT|5 A2|8 ROOMS|2 AF|10 -40000|20 30000|30 -300|41 0.9|42 0.9|43 -1.5|50 -40|210 -2e-7|220 1e-7|230 -1")
        + G("0 INSERT|5 A3|8 ROOMS|2 AHOLD|10 20000|20 -50000|30 0|50 10|210 2e-7|220 3e-7|230 -1")
        + G("0 ENDSEC|0 EOF");

    // ezdxf 1.4.2 WCS points (generator section P6-42 align_main): the transformed group 10 point of each 72 = 3/5 leaf.
    private static readonly (string Path, string Handle, double X, double Y)[] AlignedPoints =
    {
        ("A1", "FA", 49965.23352117541, 20102.95226014045),
        ("A1", "FF", 49851.34609059236, 19864.478766523192),
        ("A1", "FN", 49647.69829847912, 20074.048127792546),
        ("A1", "FB", 49462.869291053445, 20186.469859675566),
        ("A1", "FG", 49651.362538609756, 20208.162152767036),
        ("A2", "FA", 39941.040253627114, 30056.264286405312),
        ("A2", "FF", 40139.23578224482, 30058.089445854363),
        ("A2", "FN", 40061.334240288415, 30262.94112740048),
        ("A2", "FB", 40043.501392759565, 30424.208523909132),
        ("A2", "FG", 39969.01142213214, 30302.959730922234),
        ("A3>AI", "FR", -21879.39020079522, -48622.722753071364),
        ("A3>AI", "FS", -21806.95741214403, -48787.80987378988),
    };

    // The group 5 values of a G()-built drawing, in file order.
    private static List<string> HandlesOf(string text)
    {
        var lines = text.Split('\n');
        var handles = new List<string>();
        for (var k = 0; k + 1 < lines.Length; k += 2)
            if (lines[k] == "5") handles.Add(lines[k + 1]);
        return handles;
    }

    private static void RunAligned(Action<bool, string, string> c)
    {
        // Handles of the P6-40 and P6-42 drawings are unique.
        foreach (var (name, text) in new[] { ("FollowDrawing", FollowDrawing()), ("AlignedDrawing", AlignedDrawing()) })
        {
            var handles = HandlesOf(text);
            var duplicates = handles.GroupBy(h => h, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key + "x" + g.Count().ToString(CultureInfo.InvariantCulture)).ToList();
            c(handles.Count > 10 && duplicates.Count == 0, "aligned: " + name + "() group 5 handles are unique (" + handles.Count.ToString(CultureInfo.InvariantCulture) + ")",
                "duplicates " + string.Join(",", duplicates));
        }

        // Aligned / fit text: every ezdxf point within 1e-6, and what the reader keeps.
        try
        {
            var drawing = DxfReader.Parse(new UTF8Encoding(false).GetBytes(AlignedDrawing()), "aligned-ezdxf.dxf");
            var x = drawing.Explode();
            c(drawing.Warnings.Count == 0 && drawing.Unsupported.Count == 0 && x.Warnings.Count == 0,
                "aligned: no reader or explode warning and nothing unsupported",
                "reader " + string.Join(",", drawing.Warnings.Select(w => w.Code + "@" + w.Line.ToString(CultureInfo.InvariantCulture))) + " explode " + string.Join(",", x.Warnings.Select(w => w.Code)));
            foreach (var (path, handle, ex, ey) in AlignedPoints)
            {
                var found = x.Entities.Where(e => e.Provenance.Handle == handle && string.Join(">", e.Provenance.InsertPath) == path).ToList();
                var name = "aligned: " + path + " " + handle + " (" + (found.Count == 1 ? found[0].Provenance.EntityType : "?") + ")";
                if (found.Count != 1)
                {
                    c(false, name + " group 10 point equals ezdxf 1.4.2 within 1e-6", "found " + found.Count.ToString(CultureInfo.InvariantCulture));
                    continue;
                }
                var got = LeafPointsOf(found[0]);
                var ok = got.TryGetValue("insert", out var point);
                var error = ok ? Distance(point, ex, ey) : double.PositiveInfinity;
                c(error <= Eps && ZeroZ(found[0]), name + " group 10 point equals ezdxf 1.4.2 within 1e-6 and the copy's z members are 0",
                    (ok ? P(point) : "no insert") + " expected (" + F(ex) + "," + F(ey) + ") error " + F(error) + " " + ZMembers(found[0]));
            }
            var af = drawing.Blocks.Single(b => b.Name == "AF").Entities.ToDictionary(e => e.Provenance.Handle!, StringComparer.Ordinal);
            var ai = drawing.Blocks.Single(b => b.Name == "AHOLD").Entities.OfType<DxfInsert>().Single().Attributes.ToDictionary(a => a.Provenance.Handle!, StringComparer.Ordinal);
            // Near-Z leaves: the OCS point moves by z x 1e-7 or so; 1e-3 still tells group 10 from group 11 (400 away)
            // and the z of group 30 from that of 31 (400..700 away).
            var kept = new (string Name, DxfEntity Entity, double X, double Y, double Z, double Tolerance)[]
            {
                ("FA", af["FA"], 50, 60, 1300, 0), ("FF", af["FF"], -120, -80, -400, 0), ("FN", af["FN"], -200, 150, 600, 1e-3),
                ("FB", af["FB"], -300, 300, -1000, 1e-3), ("FG", af["FG"], -150, 250, 800, 0),
                ("FR", ai["FR"], 2100, 1050, 1900, 0), ("FS", ai["FS"], 2000, 900, -1900, 0),
            };
            foreach (var (name, entity, ex, ey, ez, tolerance) in kept)
            {
                var point = LeafPointsOf(entity)["insert"];
                var z = Member(entity, "Elevation")!.Value;
                c(Distance(point, ex, ey) <= Math.Max(tolerance, 1e-9) && Math.Abs(z - ez) <= Math.Max(tolerance, 1e-9),
                    "aligned: reader keeps " + name + " (" + entity.Provenance.EntityType + " 72 = 3/5) at its group 10 point (" + F(ex) + "," + F(ey) + ") with the z of group 30 (" + F(ez) + "); group 11 is not modelled",
                    P(point) + " z " + F(z));
            }
        }
        catch (Exception error)
        {
            c(false, "aligned unexpected exception", error.GetType().Name + ": " + error.Message);
        }

        // Strict 30/31 under a near +-Z OCS of the attribute's own; the exact +-Z tolerance pinned the other way.
        try
        {
            const string NearPlus = "|210 1e-7|220 -1e-7|230 1";
            const string NearMinus = "|210 2e-7|220 1e-7|230 -1";
            const string MinusZ = "|210 0|220 0|230 -1";
            string Definition(string z30, string z31, string justify, string ocs) =>
                G("0 SECTION|2 BLOCKS|0 BLOCK|5 B0|8 0|2 BOX|70 0|10 0|20 0|30 0")
                + G("0 ATTDEF|5 B1|8 0|10 100|20 50|30 " + z30 + "|11 160|21 70|31 " + z31 + "|40 100|1 X|3 P?|2 P|70 2" + justify + ocs)
                + G("0 ENDBLK|5 B2|8 0|0 ENDSEC|0 SECTION|2 ENTITIES|0 INSERT|5 S0|8 0|2 BOX|10 0|20 0|0 ENDSEC|0 EOF");
            string Attribute(string z30, string z31, string justify, string ocs) =>
                G("0 SECTION|2 BLOCKS|0 BLOCK|5 B0|8 0|2 BOX|70 0|10 0|20 0|30 0|0 LINE|5 B1|8 0|10 0|20 0|11 100|21 0|0 ENDBLK|5 B2|8 0|0 ENDSEC")
                + G("0 SECTION|2 ENTITIES|0 INSERT|5 S0|8 0|2 BOX|66 1|10 0|20 0")
                + G("0 ATTRIB|5 S1|8 0|10 100|20 50|30 " + z30 + "|11 160|21 70|31 " + z31 + "|40 100|1 v|2 P|70 0" + justify + ocs + "|0 SEQEND|5 S2|8 0|0 ENDSEC|0 EOF");
            // (label, text, the non-finite value's text or null when the file must parse, expected Elevation when it parses)
            var cases = new (string Label, string Text, string? Bad, double Elevation)[]
            {
                ("ATTDEF 72 1 near +Z 31 NaN", Definition("500", "NaN", "|72 1", NearPlus), "NaN", 0),
                ("ATTDEF 72 1 near +Z 31 inf", Definition("500", "inf", "|72 1", NearPlus), "inf", 0),
                ("ATTDEF 72 1 near +Z 31 1e999", Definition("500", "1e999", "|72 1", NearPlus), "1e999", 0),
                ("ATTDEF 72 1 near +Z 30 NaN", Definition("NaN", "700", "|72 1", NearPlus), "NaN", 0),
                ("ATTDEF 74 2 near -Z 31 -Infinity", Definition("500", "-Infinity", "|74 2", NearMinus), "-Infinity", 0),
                ("ATTDEF 74 2 near -Z 30 1e999", Definition("1e999", "700", "|74 2", NearMinus), "1e999", 0),
                ("ATTRIB 72 4 near +Z 31 NaN", Attribute("500", "NaN", "|72 4", NearPlus), "NaN", 0),
                ("ATTRIB 72 2 near -Z 30 inf", Attribute("inf", "700", "|72 2", NearMinus), "inf", 0),
                // Control: the unjustified reading already refused a non-finite 30 through a near-Z OCS.
                ("ATTDEF 72 0 near +Z 30 NaN (control)", Definition("NaN", "700", "", NearPlus), "NaN", 0),
                // Tolerated: under an exact +-Z OCS a non-finite z reads as 0 (Elevation only, no XY effect).
                ("ATTDEF 72 1 +Z 31 NaN (tolerated)", Definition("500", "NaN", "|72 1", ""), null, 0),
                ("ATTDEF 72 1 exact -Z 31 inf (tolerated)", Definition("500", "inf", "|72 1", MinusZ), null, 0),
                ("ATTRIB 74 1 +Z 31 1e999 (tolerated)", Attribute("500", "1e999", "|74 1", ""), null, 0),
                ("ATTDEF 72 1 +Z 30 NaN (not read when justified)", Definition("NaN", "700", "|72 1", ""), null, 700),
                ("ATTDEF 72 0 +Z 30 NaN (tolerated)", Definition("NaN", "700", "", ""), null, 0),
                // Not read: an unjustified attribute's 31 in a near-Z OCS.
                ("ATTRIB 72 0 near +Z 31 NaN (not read when unjustified)", Attribute("500", "NaN", "", NearPlus), null, 500),
            };
            foreach (var (label, text, bad, elevation) in cases)
            {
                string outcome;
                DxfAttribute? read = null;
                try
                {
                    var d = DxfReader.Parse(new UTF8Encoding(false).GetBytes(text), "strict-z.dxf");
                    read = d.Blocks.SelectMany(b => b.Entities).OfType<DxfAttribute>().Concat(d.Entities.OfType<DxfInsert>().SelectMany(i => i.Attributes)).SingleOrDefault();
                    outcome = "parsed: warnings " + string.Join(",", d.Warnings.Select(w => w.Code)) + " elevation " + (read is null ? "none" : F(read.Elevation));
                }
                catch (DxfFormatException e)
                {
                    outcome = e.Code + "@" + e.Line.ToString(CultureInfo.InvariantCulture) + ":" + e.Message;
                }
                if (bad is not null)
                {
                    var lines = text.Split('\n');
                    var line = Array.IndexOf(lines, bad) + 1;
                    var code = lines[line - 2];
                    c(outcome.StartsWith("bad-number@" + line.ToString(CultureInfo.InvariantCulture) + ":group " + code + " value is not a finite number", StringComparison.Ordinal),
                        "aligned: strict z: " + label + " is refused bad-number at line " + line.ToString(CultureInfo.InvariantCulture) + " (group " + code + ")", outcome);
                }
                else
                {
                    c(read is not null && Math.Abs(read.Elevation - elevation) <= 1e-3 && outcome.StartsWith("parsed: warnings  ", StringComparison.Ordinal),
                        "aligned: strict z: " + label + " parses with Elevation " + F(elevation), outcome);
                }
            }
        }
        catch (Exception error)
        {
            c(false, "aligned strict z unexpected exception", error.GetType().Name + ": " + error.Message);
        }

        // Relabel's DxfAttribute arm: unreachable from DxfReader output, reachable through a hand-built DxfBlock.
        try
        {
            var orphan = DxfReader.Parse(new UTF8Encoding(false).GetBytes(
                G("0 SECTION|2 BLOCKS|0 BLOCK|5 B0|8 0|2 BOX|70 0|10 0|20 0|30 0|0 ATTRIB|5 B1|8 0|10 5|20 6|30 500|40 50|1 v|2 T|70 0|0 ATTDEF|5 B3|8 0|10 7|20 8|30 400|40 50|1 K|3 K?|2 K|70 2|0 ENDBLK|5 B2|8 0|0 ENDSEC")
                + G("0 SECTION|2 ENTITIES|0 INSERT|5 S0|8 0|2 BOX|10 0|20 0|0 ENDSEC|0 EOF")), "orphan.dxf");
            var held = orphan.Blocks.Single().Entities;
            c(held.Count == 1 && held[0] is DxfAttribute { IsDefinition: true } && orphan.Warnings.Select(w => w.Code).SequenceEqual(new[] { "attrib-orphan" }),
                "aligned: relabel: DxfReader keeps no ATTRIB in a block (an ATTRIB without its INSERT is dropped as attrib-orphan), so the arm is not reached from a read file",
                "block " + string.Join(",", held.Select(e => e.Provenance.EntityType + (e is DxfAttribute a ? ":" + a.IsDefinition : ""))) + " warnings " + string.Join(",", orphan.Warnings.Select(w => w.Code)));

            var provenance = new DxfProvenance("hand.dxf", "HA", "0", "ATTRIB", 3);
            var attribute = new DxfAttribute(provenance, "T", new Point2(5, 6), 50, 0, "v", "v", false) { Elevation = 500 };
            var block = new DxfBlock("HB", new Point2(0, 0), "HB0", 1, new DxfEntity[] { attribute });
            foreach (var (insertion, label) in new[] { (new Point2(0, 0), "identity"), (new Point2(10, 0), "moved") })
            {
                var insert = new DxfInsert(new DxfProvenance("hand.dxf", "HQ", "ROOMS", "INSERT", 9), "HB", insertion, 1, 1, 0);
                var drawing = new DxfDrawing("hand.dxf", 4, DxfUnits.Millimetres, 0.001, Array.Empty<DxfLayer>(), new[] { block }, new DxfEntity[] { insert, attribute }, Array.Empty<DxfProvenance>(), Array.Empty<DxfWarning>());
                var x = drawing.Explode();
                var placed = x.Entities.Where(e => e.Provenance.InsertPath.Count == 1).ToList();
                var top = x.Entities.Where(e => e.Provenance.InsertPath.Count == 0).ToList();
                c(x.Warnings.Count == 0 && placed.Count == 1 && placed[0] is DxfAttribute { IsDefinition: false, Elevation: 0.0 } pa && pa.Insertion == new Point2(5 + insertion.X, 6)
                    && pa.Provenance.Layer == "ROOMS" && pa.Provenance.InsertPath[0] == "HQ"
                    && top.Count == 1 && ReferenceEquals(top[0], attribute),
                    "aligned: relabel: a hand-built block's non-definition attribute placed by an " + label + " insert is a placed copy with Elevation 0 (Relabel's DxfAttribute arm is reachable through the public API); the model-space one is the object itself",
                    string.Join(" ", x.Entities.Select(e => e.Provenance.Handle + "[" + string.Join(">", e.Provenance.InsertPath) + "]:" + ZMembers(e) + " " + string.Join(";", LeafPointsOf(e).Values.Select(P)))));
            }
        }
        catch (Exception error)
        {
            c(false, "aligned relabel unexpected exception", error.GetType().Name + ": " + error.Message);
        }
    }

    // ---------- P6-44: near-Z text: justified TEXT placement, 30/31 error order, A1/J1 cross-reference, generator check ----------
    //
    // JustifiedDrawing() mirrors JUST_* in tests/cad/tools/minsert_ezdxf_centres.py (section P6-44 justify_main).
    // * Justified TEXT (72 = 1/2/4 or 73 != 0, not aligned/fit): ezdxf 1.4.2's Text.get_placement() places it on the
    //   group 11 point (align_point, falling back to group 10 when 11 is absent). DxfReader keeps the group 10 point with
    //   the z of group 30. Documented limit, pinned: DxfText has no justification member, so its Insertion stays the
    //   left baseline start (AutoCAD writes it recomputed from 11; ezdxf's set_placement writes 10 = 11), which with
    //   RotationDegrees and Height describes the text for a left-aligned reading; taking group 11 would need a model
    //   change (a serialized alignment member) and moves room labels by up to half the text width. Every placed copy
    //   equals ezdxf's transformed group 10 point within 1e-6, and lies exactly ezdxf's measured distance (62..131 mm
    //   here) from ezdxf's placement point; TG (no group 11) equals both. Justified ATTRIB/ATTDEF keep group 11 (P6-40).
    // * Error order: a justified ATTRIB/ATTDEF in its own near +-Z OCS checks group 30 first, so when both 30 and 31 are
    //   non-finite the bad-number is at group 30's line, also when 31 comes first in the file; a justified TEXT reads
    //   only 30 (a non-finite 31 alone parses). Under an exact +-Z OCS both read as 0 (P6-38/P6-42 tolerance).
    // * Cross-reference: P6-42's A1/A2 FA/FF constants, P6-40's J1/J2 JT/JU constants and this section's K1/K2 TA/TB
    //   group 10 constants are the same ezdxf numbers for the same insert and leaf parameters (checked here exactly).
    // * Generator check: the offline-only reason in the file header is pinned here, with the generator's --check mode.
    private static string JustifiedDrawing() =>
        G("0 SECTION|2 HEADER|9 $INSUNITS|70 4|0 ENDSEC")
        + G("0 SECTION|2 BLOCKS|0 BLOCK|5 K0|8 0|2 JB|70 0|10 40|20 -30|30 0")
        + G("0 TEXT|5 TA|8 0|10 50|20 60|30 1300|11 150|21 60|31 1700|40 100|72 1|1 TA")
        + G("0 TEXT|5 TB|8 0|10 120|20 -80|30 400|11 220|21 -80|31 900|40 100|72 2|1 TB|210 0|220 0|230 -1")
        + G("0 TEXT|5 TC|8 0|10 -200|20 150|30 600|11 -100|21 180|31 1100|40 100|72 4|1 TC|210 1e-7|220 -1e-7|230 1")
        + G("0 TEXT|5 TD|8 0|10 300|20 300|30 1000|11 300|21 420|31 1000|40 100|72 0|73 1|1 TD")
        + G("0 TEXT|5 TE|8 0|10 -150|20 250|30 800|11 -60|21 250|31 1200|40 100|72 1|73 3|1 TE|210 -1e-7|220 2e-7|230 -1")
        + G("0 TEXT|5 TF|8 0|10 250|20 -150|30 500|11 330|21 -110|31 500|40 100|72 2|73 2|1 TF|210 0|220 0|230 -1")
        + G("0 TEXT|5 TG|8 0|10 400|20 100|30 2100|40 100|72 1|1 TG")
        + G("0 ENDBLK|5 K9|8 0|0 ENDSEC")
        + G("0 SECTION|2 ENTITIES")
        + G("0 INSERT|5 K1|8 ROOMS|2 JB|10 50000|20 20000|30 700|41 1.2|42 1.2|50 25|210 3e-7|220 -2e-7|230 1")
        + G("0 INSERT|5 K2|8 ROOMS|2 JB|10 -40000|20 30000|30 -300|41 0.9|42 0.9|43 -1.5|50 -40|210 -2e-7|220 1e-7|230 -1")
        + G("0 ENDSEC|0 EOF");

    // ezdxf 1.4.2 WCS points (generator section P6-44 justify_main): (insert path, leaf handle, transformed group 10
    // point x, y, transformed Text.get_placement() p1 x, y).
    private static readonly (string Path, string Handle, double X, double Y, double PX, double PY)[] JustifiedPoints =
    {
        ("K1", "TA", 49965.23352117541, 20102.95226014045, 50073.990575619806, 20153.66637154933),
        ("K1", "TB", 49851.34609059236, 19864.478766523192, 49742.589006147966, 19813.76467511431),
        ("K1", "TC", 49647.69829847912, 20074.048127792546, 49741.241205236416, 20157.389270513377),
        ("K1", "TD", 50115.4117079051, 20490.754441329205, 50054.554678214445, 20621.26276266248),
        ("K1", "TE", 49977.6326937947, 20360.305180433414, 49879.751248720626, 20314.66255488529),
        ("K1", "TF", 49745.46197980086, 19722.42048358057, 49638.17075568179, 19725.35190423122),
        ("K1", "TG", 50325.59735516724, 20323.954543849297, 50325.59735516724, 20323.954543849297),
        ("K2", "TA", 39941.040253627114, 30056.264286405312, 39872.09637374641, 29998.413341533524),
        ("K2", "TB", 40139.23578224482, 30058.089445854363, 40208.179632125524, 30115.94040572615),
        ("K2", "TC", 40061.334240288415, 30262.94112740048, 39975.03511939962, 30225.773304095466),
        ("K2", "TD", 39629.83804023306, 30077.10271893954, 39560.416978386915, 30159.83551879639),
        ("K2", "TE", 39762.17890508381, 30129.407472897965, 39824.22836627334, 30181.473407578127),
        ("K2", "TF", 40269.35857149999, 30085.03481127119, 40301.37341745584, 30158.893119120905),
        ("K2", "TG", 39676.59614009593, 29881.363669306338, 39676.59614009593, 29881.363669306338),
    };

    // The pairs (code, value) of the record with the given group 5 handle in a G()-built drawing, without 5, 2 and 1.
    private static List<string> RecordOf(string text, string handle)
    {
        var lines = text.Split('\n');
        var pairs = new List<(string Code, string Value)>();
        for (var k = 0; k + 1 < lines.Length; k += 2) pairs.Add((lines[k], lines[k + 1]));
        var at = pairs.FindIndex(p => p.Code == "5" && p.Value == handle);
        if (at < 1) return new List<string> { "missing " + handle };
        var record = new List<string> { pairs[at - 1].Value };
        for (var k = at + 1; k < pairs.Count && pairs[k].Code != "0"; k++)
            if (pairs[k].Code is not ("5" or "2" or "1")) record.Add(pairs[k].Code + " " + pairs[k].Value);
        return record;
    }

    // The header lines that record how CI runs the ezdxf generator check (pinned by RunJustified).
    private static readonly string[] GeneratorCheck =
    {
        "// P6-44 generator check, run in CI since P6-46: the engineering-gates ifc job (Linux, Python 3.12) installs the pinned",
        "// ezdxf==1.4.2 from PyPI into its own venv and, with EZDXF_DISABLE_C_EXT=1 (the pure-Python ezdxf, as in the source",
        "// checkout the constants were made with), runs tests/cad/tools/minsert_ezdxf_centres.py --check, which compares the",
        "// constant rows the generator prints with this file both ways (a row matches by its names and its full number list,",
        "// exact doubles) and exits 1 on any difference or on a planted self-test it does not catch. Offline, the same check:",
        "//     PYTHONPATH=<ezdxf v1.4.2 checkout, commit 8240df57>/src python3 tests/cad/tools/minsert_ezdxf_centres.py --check",
    };

    private static void RunJustified(Action<bool, string, string> c)
    {
        var text = JustifiedDrawing();
        {
            var handles = HandlesOf(text);
            var duplicates = handles.GroupBy(h => h, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            c(handles.Count > 10 && duplicates.Count == 0, "justified: JustifiedDrawing() group 5 handles are unique (" + handles.Count.ToString(CultureInfo.InvariantCulture) + ")", "duplicates " + string.Join(",", duplicates));
        }

        // Justified TEXT: the group 10 point equals ezdxf's transformed insert; ezdxf's placement point is group 11.
        try
        {
            var drawing = DxfReader.Parse(new UTF8Encoding(false).GetBytes(text), "justified-ezdxf.dxf");
            var x = drawing.Explode();
            c(drawing.Warnings.Count == 0 && drawing.Unsupported.Count == 0 && x.Warnings.Count == 0,
                "justified: no reader or explode warning and nothing unsupported",
                "reader " + string.Join(",", drawing.Warnings.Select(w => w.Code + "@" + w.Line.ToString(CultureInfo.InvariantCulture))) + " explode " + string.Join(",", x.Warnings.Select(w => w.Code)));
            foreach (var (path, handle, ex, ey, px, py) in JustifiedPoints)
            {
                var found = x.Entities.Where(e => e.Provenance.Handle == handle && string.Join(">", e.Provenance.InsertPath) == path).ToList();
                var name = "justified: " + path + " " + handle;
                if (found.Count != 1 || found[0] is not DxfText placed)
                {
                    c(false, name + " is one placed TEXT", "found " + found.Count.ToString(CultureInfo.InvariantCulture));
                    continue;
                }
                var error = Distance(placed.Insertion, ex, ey);
                c(error <= Eps && ZeroZ(placed), name + " group 10 point equals ezdxf 1.4.2's transformed insert within 1e-6 and the copy's z members are 0",
                    P(placed.Insertion) + " expected (" + F(ex) + "," + F(ey) + ") error " + F(error) + " " + ZMembers(placed));
                var offset = Distance(placed.Insertion, px, py);
                var measured = Math.Max(Math.Abs(ex - px), Math.Abs(ey - py));
                c(Math.Abs(offset - measured) <= Eps && (handle == "TG" ? measured == 0 : measured > 50),
                    name + (handle == "TG"
                        ? " (no group 11) is at ezdxf's placement point p1"
                        : " limit pinned: not at ezdxf's placement point p1 (group 11), off by the measured " + F(Math.Round(measured, 3)) + " mm"),
                    "placement (" + F(px) + "," + F(py) + ") offset " + F(offset) + " measured " + F(measured));
            }
            var jb = drawing.Blocks.Single(b => b.Name == "JB").Entities.ToDictionary(e => e.Provenance.Handle!, StringComparer.Ordinal);
            // Near-Z leaves: the OCS point moves by z x 1e-7 or so; 1e-3 still tells group 10 from group 11 (>= 30 away)
            // and the z of group 30 from that of 31 (400..500 away for TC/TE).
            var kept = new (string Name, double X, double Y, double Z, double Tolerance)[]
            {
                ("TA", 50, 60, 1300, 0), ("TB", -120, -80, -400, 0), ("TC", -200, 150, 600, 1e-3), ("TD", 300, 300, 1000, 0),
                ("TE", 150, 250, -800, 1e-3), ("TF", -250, -150, -500, 0), ("TG", 400, 100, 2100, 0),
            };
            foreach (var (name, ex, ey, ez, tolerance) in kept)
            {
                var entity = (DxfText)jb[name];
                c(Distance(entity.Insertion, ex, ey) <= Math.Max(tolerance, 1e-9) && Math.Abs(entity.Elevation - ez) <= Math.Max(tolerance, 1e-9),
                    "justified: reader keeps TEXT " + name + " at its group 10 point (" + F(ex) + "," + F(ey) + ") with the z of group 30 (" + F(ez) + "); group 11 is not modelled",
                    P(entity.Insertion) + " z " + F(entity.Elevation));
            }
        }
        catch (Exception error)
        {
            c(false, "justified unexpected exception", error.GetType().Name + ": " + error.Message);
        }

        // Cross-reference: A1/A2 FA/FF (P6-42), J1/J2 JT/JU (P6-40) and K1/K2 TA/TB (P6-44) share one set of numbers.
        try
        {
            var aligned = AlignedDrawing();
            var follow = FollowDrawing();
            foreach (var (a, j, k) in new[] { ("A1", "J1", "K1"), ("A2", "J2", "K2") })
            {
                var ra = RecordOf(aligned, a);
                var rj = RecordOf(follow, j);
                var rk = RecordOf(text, k);
                c(ra.SequenceEqual(rj) && rj.SequenceEqual(rk), "justified: cross-reference: inserts " + a + ", " + j + " and " + k + " have the same parameters",
                    string.Join(" ", ra) + " | " + string.Join(" ", rj) + " | " + string.Join(" ", rk));
                foreach (var (fa, jt, ta) in new[] { ("FA", "JT", "TA"), ("FF", "JU", "TB") })
                {
                    string Leaf(List<string> record) => string.Join(" ", record.Where(g => g.Split(' ')[0] is "10" or "20" or "30" or "210" or "220" or "230"));
                    var la = Leaf(RecordOf(aligned, fa));
                    var lj = Leaf(RecordOf(follow, jt));
                    var lk = Leaf(RecordOf(text, ta));
                    var pa = AlignedPoints.Where(p => p.Path == a && p.Handle == fa).Select(p => (p.X, p.Y)).ToList();
                    var pj = FollowPoints.Where(p => p.Path == j && p.Handle == jt).Select(p => (p.X, p.Y)).ToList();
                    var pk = JustifiedPoints.Where(p => p.Path == k && p.Handle == ta).Select(p => (p.X, p.Y)).ToList();
                    c(la == lj && lj == lk && pa.Count == 1 && pj.Count == 1 && pk.Count == 1 && pa[0] == pj[0] && pj[0] == pk[0],
                        "justified: cross-reference: " + a + " " + fa + " (P6-42), " + j + " " + jt + " (P6-40) and " + k + " " + ta + " (P6-44) have the same group 10/20/30 and OCS and the same ezdxf constant",
                        la + " | " + lj + " | " + lk + "; " + string.Join(";", pa.Concat(pj).Concat(pk).Select(p => "(" + F(p.X) + "," + F(p.Y) + ")")));
                }
            }
        }
        catch (Exception error)
        {
            c(false, "justified cross-reference unexpected exception", error.GetType().Name + ": " + error.Message);
        }

        // Error order when both group 30 and 31 are non-finite: group 30's line, whatever the file order.
        try
        {
            const string NearPlus = "|210 1e-7|220 -1e-7|230 1";
            const string NearMinus = "|210 2e-7|220 1e-7|230 -1";
            const string MinusZ = "|210 0|220 0|230 -1";
            string Definition(string z, string justify, string ocs) =>
                G("0 SECTION|2 BLOCKS|0 BLOCK|5 B0|8 0|2 BOX|70 0|10 0|20 0|30 0")
                + G("0 ATTDEF|5 B1|8 0|10 100|20 50|" + z + "|40 100|1 X|3 P?|2 P|70 2" + justify + ocs)
                + G("0 ENDBLK|5 B2|8 0|0 ENDSEC|0 SECTION|2 ENTITIES|0 INSERT|5 S0|8 0|2 BOX|10 0|20 0|0 ENDSEC|0 EOF");
            string Attribute(string z, string justify, string ocs) =>
                G("0 SECTION|2 BLOCKS|0 BLOCK|5 B0|8 0|2 BOX|70 0|10 0|20 0|30 0|0 LINE|5 B1|8 0|10 0|20 0|11 100|21 0|0 ENDBLK|5 B2|8 0|0 ENDSEC")
                + G("0 SECTION|2 ENTITIES|0 INSERT|5 S0|8 0|2 BOX|66 1|10 0|20 0")
                + G("0 ATTRIB|5 S1|8 0|10 100|20 50|" + z + "|40 100|1 v|2 P|70 0" + justify + ocs + "|0 SEQEND|5 S2|8 0|0 ENDSEC|0 EOF");
            string Plain(string z, string justify, string ocs) =>
                G("0 SECTION|2 ENTITIES|0 TEXT|5 X1|8 0|10 100|20 50|" + z + "|40 100|1 T" + justify + ocs + "|0 ENDSEC|0 EOF");
            // (label, text, group code whose value line is reported, its value; null code when the file must parse)
            var cases = new (string Label, string Text, string? Code, string? Value)[]
            {
                ("ATTDEF 72 1 near +Z 30 NaN, 31 inf", Definition("30 NaN|11 160|21 70|31 inf", "|72 1", NearPlus), "30", "NaN"),
                ("ATTDEF 74 2 near -Z 30 1e999, 31 -Infinity", Definition("30 1e999|11 160|21 70|31 -Infinity", "|74 2", NearMinus), "30", "1e999"),
                ("ATTDEF 72 4 near +Z 31 -1e999 written before 30 1e999", Definition("11 160|21 70|31 -1e999|30 1e999", "|72 4", NearPlus), "30", "1e999"),
                ("ATTRIB 72 2 near -Z 30 inf, 31 NaN", Attribute("30 inf|11 160|21 70|31 NaN", "|72 2", NearMinus), "30", "inf"),
                ("ATTRIB 74 1 near +Z 31 NaN written before 30 -inf", Attribute("11 160|21 70|31 NaN|30 -inf", "|74 1", NearPlus), "30", "-inf"),
                ("TEXT 72 1 near +Z 30 NaN, 31 inf", Plain("30 NaN|11 160|21 70|31 inf", "|72 1", NearPlus), "30", "NaN"),
                ("TEXT 72 2 near -Z 31 inf only (31 not read for TEXT)", Plain("30 500|11 160|21 70|31 inf", "|72 2", NearMinus), null, null),
                ("ATTDEF 72 1 exact -Z 30 NaN, 31 inf (tolerated)", Definition("30 NaN|11 160|21 70|31 inf", "|72 1", MinusZ), null, null),
                ("ATTRIB 74 2 +Z 30 1e999, 31 NaN (tolerated)", Attribute("30 1e999|11 160|21 70|31 NaN", "|74 2", ""), null, null),
                // P6-46: vertical-only justification. TEXT's vertical group is 73 (its 30 is placed through the OCS, 31
                // never read); ATTRIB/ATTDEF's is 74, their 73 is the field length, so 73 alone leaves them unjustified
                // (group 10 with the z of group 30 placed, 31 never read).
                ("TEXT 73 1 (vertical only) near +Z 30 NaN, 31 inf", Plain("30 NaN|11 160|21 70|31 inf", "|73 1", NearPlus), "30", "NaN"),
                ("TEXT 73 3 (vertical only) near -Z 31 -inf written before 30 1e999", Plain("11 160|21 70|31 -inf|30 1e999", "|73 3", NearMinus), "30", "1e999"),
                ("ATTDEF 73 2 only (field length, not justified) near +Z 30 NaN, 31 inf", Definition("30 NaN|11 160|21 70|31 inf", "|73 2", NearPlus), "30", "NaN"),
                ("ATTRIB 73 1 only (field length, not justified) near -Z 30 500, 31 NaN (31 not read)", Attribute("30 500|11 160|21 70|31 NaN", "|73 1", NearMinus), null, null),
            };
            foreach (var (label, drawingText, code, value) in cases)
            {
                string outcome;
                try
                {
                    var d = DxfReader.Parse(new UTF8Encoding(false).GetBytes(drawingText), "order-z.dxf");
                    var z = d.Entities.OfType<DxfText>().Select(t => t.Elevation).Concat(d.Blocks.SelectMany(b => b.Entities).OfType<DxfAttribute>().Concat(d.Entities.OfType<DxfInsert>().SelectMany(i => i.Attributes)).Select(a => a.Elevation)).ToList();
                    outcome = "parsed: warnings " + string.Join(",", d.Warnings.Select(w => w.Code)) + " elevations " + string.Join(",", z.Select(F));
                }
                catch (DxfFormatException e)
                {
                    outcome = e.Code + "@" + e.Line.ToString(CultureInfo.InvariantCulture) + ":" + e.Message;
                }
                if (code is not null)
                {
                    var lines = drawingText.Split('\n');
                    var line = Enumerable.Range(0, lines.Length - 1).First(k => lines[k] == code && lines[k + 1] == value) + 2;
                    var other = Enumerable.Range(0, lines.Length - 1).Where(k => lines[k] == "31").Select(k => k + 2).DefaultIfEmpty(0).First();
                    c(outcome.StartsWith("bad-number@" + line.ToString(CultureInfo.InvariantCulture) + ":group " + code + " value is not a finite number", StringComparison.Ordinal),
                        "justified: error order: " + label + " is refused bad-number at group " + code + "'s line " + line.ToString(CultureInfo.InvariantCulture) + " (group 31 at line " + other.ToString(CultureInfo.InvariantCulture) + ")", outcome);
                }
                else
                {
                    c(outcome.StartsWith("parsed: warnings  elevations ", StringComparison.Ordinal),
                        "justified: error order: " + label + " parses", outcome);
                }
            }
        }
        catch (Exception error)
        {
            c(false, "justified error order unexpected exception", error.GetType().Name + ": " + error.Message);
        }

        // Generator check (P6-46): run in CI by the ifc job, recorded in this file's header; the generator has the
        // two-way --check mode with planted self-tests.
        try
        {
            var root = RepoRoot();
            var source = File.ReadAllText(Path.Combine(root, "tests", "cad", "MinsertEzdxfTests.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
            var header = source[..source.IndexOf("internal static class MinsertEzdxfTests", StringComparison.Ordinal)];
            var reason = string.Join("\n", GeneratorCheck) + "\n";
            c(header.Contains(reason, StringComparison.Ordinal) && !header.Contains("offline only", StringComparison.Ordinal),
                "justified: generator: the file header records how CI runs the ezdxf generator --check (no stale offline-only claim)",
                "header has the CI block: " + header.Contains(reason, StringComparison.Ordinal) + " offline-only claim left: " + header.Contains("offline only", StringComparison.Ordinal));
            // The CI step: in the Linux ifc job, a pinned ezdxf 1.4.2 in its own venv, the pure-Python reading, --check.
            var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml")).Replace("\r\n", "\n", StringComparison.Ordinal);
            const string StepName = "      - name: ezdxf 1.4.2 generator constant check, two-way (P6-46)\n";
            var ifcJob = workflow.IndexOf("\n  ifc:\n", StringComparison.Ordinal);
            var rhinoJob = workflow.IndexOf("\n  rhino:\n", StringComparison.Ordinal);
            var step = workflow.IndexOf(StepName, StringComparison.Ordinal);
            var stepEnd = step < 0 ? -1 : workflow.IndexOf("\n      - ", step + StepName.Length - 1, StringComparison.Ordinal);
            var stepText = step < 0 || stepEnd < 0 ? "" : workflow[step..stepEnd];
            string[] needed =
            {
                "python -m venv .tools/ezdxf", ".tools/ezdxf/bin/python -m pip install ezdxf==1.4.2", ".tools/ezdxf/bin/python -m pip check",
                "EZDXF_DISABLE_C_EXT=1 .tools/ezdxf/bin/python -I tests/cad/tools/minsert_ezdxf_centres.py --check",
            };
            var absent = needed.Where(n => !stepText.Contains(n, StringComparison.Ordinal)).ToList();
            c(ifcJob > 0 && rhinoJob > ifcJob && step > ifcJob && step < rhinoJob && workflow.IndexOf(StepName, step + 1, StringComparison.Ordinal) < 0 && absent.Count == 0,
                "justified: generator: .github/workflows/ci.yml runs the --check once, in the Linux ifc job, against a pinned ezdxf==1.4.2 in its own venv with C extensions disabled",
                "ifc " + ifcJob.ToString(CultureInfo.InvariantCulture) + " step " + step.ToString(CultureInfo.InvariantCulture) + " rhino " + rhinoJob.ToString(CultureInfo.InvariantCulture) + " missing " + string.Join(" | ", absent));
            var generator = File.ReadAllText(Path.Combine(root, "tests", "cad", "tools", "minsert_ezdxf_centres.py")).Replace("\r\n", "\n", StringComparison.Ordinal);
            var sections = new[] { "def main():", "def bulge_main():", "def chain_main():", "def z_main():", "def leaf_main():", "def follow_main():", "def align_main():", "def justify_main():", "def check_main(cs_path):" };
            var missing = sections.Where(s => !generator.Contains("\n" + s + "\n", StringComparison.Ordinal)).ToList();
            var checkedSections = generator.Contains("for section in (main, bulge_main, chain_main, z_main, leaf_main, follow_main, align_main, justify_main):", StringComparison.Ordinal);
            c(missing.Count == 0 && checkedSections && generator.Contains("if sys.argv[1:2] == [\"--check\"]:", StringComparison.Ordinal) && generator.Contains("commit 8240df5793daf2fd7db95d118a78e17ec92043ec", StringComparison.Ordinal),
                "justified: generator: tests/cad/tools/minsert_ezdxf_centres.py has every section, a --check mode over all of them, and records ezdxf 1.4.2 commit 8240df57",
                "missing " + string.Join(",", missing) + " check covers all " + checkedSections);
            // P6-46: two-way (C# constants no generator row produces are reported), rows matched whole, planted self-tests.
            string[] twoWay =
            {
                "def _committed_rows(source):", "def _compare(generated, committed):", "def _self_tests(generated, source):",
                "\"not produced by the generator: \"", "\"not committed: \"",
            };
            var twoWayMissing = twoWay.Where(t => !generator.Contains(t, StringComparison.Ordinal)).ToList();
            c(twoWayMissing.Count == 0 && !generator.Contains("def _contains(", StringComparison.Ordinal),
                "justified: generator: --check is two-way (committed constants no generator row produces fail), matches whole rows (no contiguous-run matcher) and runs planted self-tests",
                "missing " + string.Join(" | ", twoWayMissing) + " contiguous-run matcher left " + generator.Contains("def _contains(", StringComparison.Ordinal));
            // P6-48: numpy pinned to the generation version; every output line but the caught self-tests annotated, 20
            // lines per annotation (at most 9 annotations, then one line with the count of the lines left over); the
            // C-extension run names its differing rows.
            string[] stepNeeded =
            {
                ".tools/ezdxf/bin/python -m pip install ezdxf==1.4.2 numpy==2.5.3",
                "grep -v '^self-test planted .*: caught (' \"$out\" | cut -c1-900 | awk -v level=\"$level\"",
                "more output lines not annotated",
                "grep '^differing rows: '",
            };
            var stepAbsent = stepNeeded.Where(n => !stepText.Contains(n, StringComparison.Ordinal)).ToList();
            c(stepAbsent.Count == 0 && !stepText.Contains("tail -n 10", StringComparison.Ordinal),
                "justified: generator: P6-48 the CI step pins numpy==2.5.3, annotates every output line but the caught self-tests (20 lines per annotation, at most 9, then the count of the lines left over; no tail -n 10) and names the rows the C extensions move",
                "missing " + string.Join(" | ", stepAbsent) + " tail -n 10 left " + stepText.Contains("tail -n 10", StringComparison.Ordinal));
            // P6-48: a label TEXT matches the generator row its handle names (LABEL_ROWS, no loose x,y match against any
            // cell); the summary names the differing rows, the C-extension state and numpy's version; a missing table,
            // a label moved to another cell's centre and an unmapped label handle are planted.
            string[] checkNeeded =
            {
                "LABEL_ROWS = {\"L1\": \"R1[1,2]\", \"L2\": \"S1[2,0]\", \"L3\": \"M1[1,1]\", \"L4\": \"N1[0,1]\", \"L5\": \"T1\", \"L6\": \"T2[0,0]\", \"L7\": \"T3[1,1]\"}",
                "\"differing rows: \"", "ezdxf.options.use_c_ext", "numpy.__version__",
                "(\"a missing Cells table\", ", "(\"a label moved to another cell's centre\", ", "(\"a label with no LABEL_ROWS entry\", ",
            };
            var checkAbsent = checkNeeded.Where(n => !generator.Contains(n, StringComparison.Ordinal)).ToList();
            c(checkAbsent.Count == 0 && !generator.Contains("\"[\" in gnames[0]", StringComparison.Ordinal),
                "justified: generator: P6-48 --check matches each label TEXT to the row its handle names (no loose x,y match), names differing rows, numpy and C-extension state, and plants a missing table and moved or unmapped labels",
                "missing " + string.Join(" | ", checkAbsent) + " loose cell match left " + generator.Contains("\"[\" in gnames[0]", StringComparison.Ordinal));

            // P6-50: the generation environment is recorded the same way in this file's header and in the generator's
            // docstring (comment markers and line breaks aside): numpy==2.5.3, the full annotation, the evidence for the
            // generation version (CI run 45718082997) and the pinned C-extension drift rows.
            static string Prose(IEnumerable<string> lines) => string.Join(" ", lines
                .Select(l => l.Trim())
                .Select(l => l.StartsWith("//", StringComparison.Ordinal) ? l[2..].Trim() : l)
                .Where(l => l.Length > 0));
            var headerProse = Prose(header.Split('\n'));
            var docEnd = generator.IndexOf("\"\"\"", 3, StringComparison.Ordinal);
            var docProse = generator.StartsWith("\"\"\"", StringComparison.Ordinal) && docEnd > 0 ? Prose(generator[3..docEnd].Split('\n')) : "";
            string[] environment =
            {
                "numpy==2.5.3", "pyparsing==", "typing_extensions==", "fonttools==",
                "every output line but the caught self-tests is annotated, 20 lines an annotation, at most 9 annotations, then one line with the count of the lines left over",
                "CI run 45718082997", "164 rows", "C1>NB, X1>XF, Z1>ZA, W1>CL",
            };
            var headerEnvAbsent = environment.Where(e => !headerProse.Contains(e, StringComparison.Ordinal)).ToList();
            var docEnvAbsent = environment.Where(e => !docProse.Contains(e, StringComparison.Ordinal)).ToList();
            c(headerEnvAbsent.Count == 0 && docEnvAbsent.Count == 0,
                "justified: generator: P6-50 the header and the generator docstring name numpy==2.5.3 and the other pinned ezdxf dependencies, the full annotation, the evidence for the generation version and the C-extension drift rows",
                "header missing " + string.Join(" | ", headerEnvAbsent) + "; docstring missing " + string.Join(" | ", docEnvAbsent));
            // P6-50 (P6-49): the P6-45 and P6-47 header sentences name the shared CodePins stripper, not reflection.
            c(!headerProse.Contains("reflection", StringComparison.Ordinal) && headerProse.Contains("CodePins.StripCommentsAndLiterals (tests/cad/CodePins.cs)", StringComparison.Ordinal),
                "justified: generator: P6-50 the header's P6-45/P6-47 sentences name CodePins.StripCommentsAndLiterals (P6-49), not a reflection call",
                "reflection named " + headerProse.Contains("reflection", StringComparison.Ordinal) + " CodePins named " + headerProse.Contains("CodePins.StripCommentsAndLiterals (tests/cad/CodePins.cs)", StringComparison.Ordinal));
            // P6-50: ezdxf's other dependencies are pinned in the one install line and their versions printed.
            var install = System.Text.RegularExpressions.Regex.Matches(stepText, @"^ *\.tools/ezdxf/bin/python -m pip install .*$", System.Text.RegularExpressions.RegexOptions.Multiline);
            var pinnedInstall = install.Count == 1 && System.Text.RegularExpressions.Regex.IsMatch(install[0].Value.Trim(),
                @"^\.tools/ezdxf/bin/python -m pip install ezdxf==1\.4\.2 numpy==2\.5\.3 pyparsing==\d+(\.\d+)+ typing_extensions==\d+(\.\d+)+ fonttools==\d+(\.\d+)+$");
            string[] printed = { "importlib.metadata", "('pyparsing', 'typing_extensions', 'fonttools')" };
            var printedAbsent = printed.Where(p => !stepText.Contains(p, StringComparison.Ordinal)).ToList();
            c(pinnedInstall && printedAbsent.Count == 0,
                "justified: generator: P6-50 the CI step pins ezdxf's other dependencies (pyparsing, typing_extensions, fonttools) in its one install line and prints their installed versions",
                "install lines " + install.Count.ToString(CultureInfo.InvariantCulture) + " [" + string.Join(" | ", install.Select(m => m.Value.Trim())) + "] pinned " + pinnedInstall + " missing " + string.Join(" | ", printedAbsent));
            // P6-50: the four C-extension drift rows are pinned in the generator; under the C extensions a differing row
            // outside them fails the run (planted self-test), and the CI step fails on that run's status instead of
            // ignoring it.
            string[] driftNeeded =
            {
                "C_EXT_DRIFT = (\"C1>NB\", \"X1>XF\", \"Z1>ZA\", \"W1>CL\")",
                "\"new C-extension drift rows: \"", "(\"a new C-extension drift row\", ",
            };
            var driftAbsent = driftNeeded.Where(n => !generator.Contains(n, StringComparison.Ordinal)).ToList();
            var cextIgnored = stepText.Contains("2>&1) || true", StringComparison.Ordinal);
            c(driftAbsent.Count == 0 && !cextIgnored && stepText.Contains("2>&1) || cstatus=$?", StringComparison.Ordinal) && stepText.Contains("exit \"$cstatus\"", StringComparison.Ordinal),
                "justified: generator: P6-50 the C-extension drift rows C1>NB, X1>XF, Z1>ZA, W1>CL are pinned; a new drifting row fails the C-extension run (planted) and the CI step",
                "missing " + string.Join(" | ", driftAbsent) + " c-ext status ignored " + cextIgnored + " status kept " + stepText.Contains("2>&1) || cstatus=$?", StringComparison.Ordinal));
            // P6-50: an Axes row (no quoted name) is named by its normal in "differing rows", not by its number tuple; the
            // planted Axes self-test checks the name.
            string[] axesNeeded = { "\"axes normal (\"", "\"not committed: (0.6, 0.0, 0.8)\", \"axes normal (0.6, 0.0, 0.8)\")" };
            var axesAbsent = axesNeeded.Where(n => !generator.Contains(n, StringComparison.Ordinal)).ToList();
            c(axesAbsent.Count == 0,
                "justified: generator: P6-50 an Axes row is named by its normal (axes normal (x, y, z)) in the differing rows, checked by the planted Axes self-test",
                "missing " + string.Join(" | ", axesAbsent));
        }
        catch (Exception error)
        {
            c(false, "justified generator unexpected exception", error.GetType().Name + ": " + error.Message);
        }
    }
}
