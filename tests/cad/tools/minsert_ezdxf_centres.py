"""Offline generator of the ezdxf cross-check constants in tests/cad/MinsertEzdxfTests.cs (P6-30).

Not run in CI. Needs ezdxf (MIT, https://github.com/mozman/ezdxf); the constants in the test were produced with
ezdxf 1.4.2 (tag v1.4.2, commit 8240df5793daf2fd7db95d118a78e17ec92043ec) on Python 3 with numpy 2.5.3:

    PYTHONPATH=<ezdxf checkout>/src python3 tests/cad/tools/minsert_ezdxf_centres.py

(PyPI was blocked where the constants were generated, so ezdxf was imported from a source checkout of tag v1.4.2;
`pip install ezdxf==1.4.2` works equally where PyPI is reachable.)

It builds, with ezdxf's own API, the block RECT and the INSERT/MINSERT entities that MinsertEzdxfTests.Drawing()
writes by hand (same parameters, listed in CASES), and prints, as C# tuples:
  * for every MINSERT cell (ezdxf Insert.multi_insert(), row-major) the four WCS corners of the cell's RECT outline
    (Insert.virtual_entities() then ezdxf.path.make_path, so the OCS of mirrored and near-Z inserts is applied by ezdxf)
    and their mean (the cell centre), projected to world XY;
  * the arbitrary-axis OCS axes ezdxf computes (ezdxf.math.OCS) for the extrusion normals used in the test;
  * for the tilted inserts (non-axis extrusion, which DxfReader reports as ocs-non-planar instead of projecting), the
    world XY centre the projection reading would give, used as decoy label positions.

P6-34, P6-36 and P6-38 extensions (chain_main, z_main, leaf_main) are described at their sections below.

P6-32 extension (same ezdxf 1.4.2 checkout, commit 8240df57; printed after the P6-30 sections, which are unchanged):
a second drawing (BULGE_* below, mirrored by MinsertEzdxfTests.BulgeDrawing()) whose block ARCH (base (50, 20)) holds
a closed LWPOLYLINE with a positive (0.4) and a negative (-0.25) bulge, and whose block NEST (base (10, 10)) holds an
axis-aligned INSERT NA and a near -Z INSERT NB of ARCH. Placed through B1 (ARCH, near -Z (2e-7,-1e-7,-1), elevation
300, X scale -2), C1 (NEST, near +Z (-3e-7,4e-7,1)) and the MINSERT D1 (NEST, near -Z (0,2e-7,-1), 2 columns). For
every resulting outline (nested inserts walked through Insert.virtual_entities(), MINSERT cells through
multi_insert(), row-major) it prints the insert path, the four WCS corners (LWPolyline.vertices_in_wcs()) and, per
segment in order (closing segment last), the WCS midpoint of the segment: for an ARC from LWPolyline.virtual_entities()
the point Arc.vertices() gives at the middle of its counter-clockwise span, for a LINE the mean of its end points.
"""

import ezdxf
from ezdxf import path
from ezdxf.math import OCS, Vec3

# Block RECT: base point (100, 50); closed LWPOLYLINE (100,50)-(2100,50)-(2100,1550)-(100,1550).
BASE = (100.0, 50.0)
RECT = [(100.0, 50.0), (2100.0, 50.0), (2100.0, 1550.0), (100.0, 1550.0)]

# name, OCS insertion (x, y, z), rotation, sx, sy, columns, rows, column spacing, row spacing, extrusion
CASES = [
    ("R1", (1000.0, 2000.0, 0.0), 30.0, 2.0, 1.5, 3, 2, 5000.0, 3000.0, (0.0, 0.0, 1.0)),
    ("S1", (40000.0, 2000.0, 0.0), 135.0, -1.25, 0.75, 2, 3, 3500.0, -2000.0, (0.0, 0.0, 1.0)),
    ("M1", (-80000.0, 2000.0, 0.0), 45.0, 1.5, 1.0, 3, 2, 4000.0, 2500.0, (0.0, 0.0, -1.0)),
    ("N1", (-120000.0, 2000.0, 500.0), 60.0, 1.0, 2.0, 2, 2, 3000.0, 4000.0, (2e-7, 0.0, -1.0)),
]

# Tilted (non-axis) extrusions: an INSERT and a MINSERT under (0.6, 0, 0.8) (|Nx| >= 1/64: Ax = Wz x N) and a MINSERT
# under (0.01, 0, 1) (|Nx|, |Ny| < 1/64: Ax = Wy x N).
TILTED = [
    ("T1", (10000.0, 50000.0, 0.0), 0.0, 1.0, 1.0, 1, 1, 0.0, 0.0, (0.6, 0.0, 0.8)),
    ("T2", (20000.0, 60000.0, 0.0), 30.0, 1.0, 1.0, 2, 2, 4000.0, 3000.0, (0.6, 0.0, 0.8)),
    ("T3", (160000.0, 40000.0, 0.0), 0.0, 1.0, 1.0, 2, 2, 4000.0, 3000.0, (0.01, 0.0, 1.0)),
]

NORMALS = [(0.6, 0.0, 0.8), (0.01, 0.0, 1.0), (2e-7, 0.0, -1.0), (0.0, 0.0, -1.0)]


def r(value: float) -> str:
    return repr(float(value))


def build():
    doc = ezdxf.new("R2018")
    doc.header["$INSUNITS"] = 4
    block = doc.blocks.new("RECT", base_point=(BASE[0], BASE[1], 0))
    block.add_lwpolyline(RECT, close=True)
    msp = doc.modelspace()
    inserts = {}
    for name, ins, rot, sx, sy, cols, rows, csp, rsp, ext in CASES + TILTED:
        e = msp.add_blockref("RECT", ins, dxfattribs={"rotation": rot, "xscale": sx, "yscale": sy, "extrusion": ext})
        if cols > 1 or rows > 1:
            e.dxf.column_count = cols
            e.dxf.row_count = rows
            e.dxf.column_spacing = csp
            e.dxf.row_spacing = rsp
        inserts[name] = (e, cols, rows)
    return inserts


def corners(virtual_insert):
    outlines = [v for v in virtual_insert.virtual_entities() if v.dxftype() == "LWPOLYLINE"]
    assert len(outlines) == 1
    vertices = list(path.make_path(outlines[0]).control_vertices())
    if len(vertices) == 5:  # closed path repeats its start point
        vertices = vertices[:4]
    assert len(vertices) == 4, vertices
    return vertices


def cells(insert, cols, rows):
    virtuals = list(insert.multi_insert()) if (cols > 1 or rows > 1) else [insert]
    assert len(virtuals) == cols * rows
    for k, v in enumerate(virtuals):
        row, col = divmod(k, cols)
        c = corners(v)
        cx = sum(p.x for p in c) / 4
        cy = sum(p.y for p in c) / 4
        yield row, col, c, cx, cy


def main():
    print("// ezdxf", ezdxf.__version__)
    inserts = build()
    print("// cells: (path, centre x, centre y, corner xy * 4)")
    for name, *_ in CASES:
        e, cols, rows = inserts[name]
        for row, col, c, cx, cy in cells(e, cols, rows):
            key = name + "[" + str(row) + "," + str(col) + "]"
            flat = ", ".join(r(p.x) + ", " + r(p.y) for p in c)
            print('("' + key + '", ' + r(cx) + ", " + r(cy) + ", new[] { " + flat + " }),")
    print("// OCS axes: (normal, Ax, Ay)")
    for n in NORMALS:
        o = OCS(n)
        print("(" + ", ".join(r(v) for v in n) + ") Ax (" + ", ".join(r(v) for v in o.ux) + ") Ay (" + ", ".join(r(v) for v in o.uy) + ")")
    print("// tilted projected centres: (path, centre x, centre y)")
    for name, *_ in TILTED:
        e, cols, rows = inserts[name]
        for row, col, c, cx, cy in cells(e, cols, rows):
            key = name if cols == 1 and rows == 1 else name + "[" + str(row) + "," + str(col) + "]"
            print('("' + key + '", ' + r(cx) + ", " + r(cy) + "),")


# P6-32: bulge outline and nested chains under near +-Z normals.
BULGE_BASE = (50.0, 20.0)
BULGE_ARCH = [(50.0, 20.0, 0.0), (1050.0, 20.0, 0.4), (1050.0, 820.0, 0.0), (50.0, 820.0, -0.25)]  # x, y, bulge
NEST_BASE = (10.0, 10.0)
# name, OCS insertion (x, y, z), rotation, sx, sy, extrusion
NEST_INNER = [
    ("NA", (300.0, 200.0, 0.0), 25.0, 1.5, 1.5, (0.0, 0.0, 1.0)),
    ("NB", (-2000.0, 1500.0, 0.0), 70.0, 1.25, 1.25, (1e-7, 0.0, -1.0)),
]
# name, block, OCS insertion, rotation, sx, sy, columns, rows, column spacing, row spacing, extrusion
BULGE_CASES = [
    ("B1", "ARCH", (5000.0, 3000.0, 300.0), 40.0, -2.0, 2.0, 1, 1, 0.0, 0.0, (2e-7, -1e-7, -1.0)),
    ("C1", "NEST", (60000.0, 5000.0, 0.0), 15.0, 0.8, 0.8, 1, 1, 0.0, 0.0, (-3e-7, 4e-7, 1.0)),
    ("D1", "NEST", (-30000.0, 8000.0, 0.0), -30.0, 1.0, 1.0, 2, 1, 9000.0, 0.0, (0.0, 2e-7, -1.0)),
]


def build_bulge():
    doc = ezdxf.new("R2018")
    doc.header["$INSUNITS"] = 4
    arch = doc.blocks.new("ARCH", base_point=(BULGE_BASE[0], BULGE_BASE[1], 0))
    arch.add_lwpolyline(BULGE_ARCH, format="xyb", close=True)
    nest = doc.blocks.new("NEST", base_point=(NEST_BASE[0], NEST_BASE[1], 0))
    for _, ins, rot, sx, sy, ext in NEST_INNER:
        nest.add_blockref("ARCH", ins, dxfattribs={"rotation": rot, "xscale": sx, "yscale": sy, "extrusion": ext})
    msp = doc.modelspace()
    out = []
    for name, block, ins, rot, sx, sy, cols, rows, csp, rsp, ext in BULGE_CASES:
        e = msp.add_blockref(block, ins, dxfattribs={"rotation": rot, "xscale": sx, "yscale": sy, "extrusion": ext})
        if cols > 1 or rows > 1:
            e.dxf.column_count = cols
            e.dxf.row_count = rows
            e.dxf.column_spacing = csp
            e.dxf.row_spacing = rsp
        out.append((name, block, e, cols, rows))
    return out


def walk(insert, path, block):
    # ARCH yields its one LWPOLYLINE. NEST yields one item per inner insert, in block order (NA, NB): a virtual INSERT,
    # walked into ARCH, or, where ezdxf could not transform the nested INSERT as a whole and exploded it instead (it
    # does so for NB under C1), that insert's already transformed LWPOLYLINE.
    items = list(insert.virtual_entities())
    if block == "ARCH":
        assert [v.dxftype() for v in items] == ["LWPOLYLINE"], items
        yield path, items[0]
        return
    assert len(items) == len(NEST_INNER), items
    for (name, *_), v in zip(NEST_INNER, items):
        if v.dxftype() == "INSERT":
            yield from walk(v, path + [name], "ARCH")
        else:
            assert v.dxftype() == "LWPOLYLINE", v
            yield path + [name], v


def midpoints(lw):
    for seg in lw.virtual_entities():
        if seg.dxftype() == "ARC":
            start, end = seg.dxf.start_angle, seg.dxf.end_angle
            span = (end - start) % 360.0
            yield next(iter(seg.vertices([start + span / 2])))
        else:
            yield (seg.dxf.start + seg.dxf.end) / 2


def bulge_main():
    print("// P6-32 bulge outlines: (path, corner xy * 4, segment midpoint xy * 4)")
    for name, block, e, cols, rows in build_bulge():
        if cols > 1 or rows > 1:
            placements = [(name + "[" + str(k // cols) + "," + str(k % cols) + "]", v) for k, v in enumerate(e.multi_insert())]
        else:
            placements = [(name, e)]
        for key, v in placements:
            for path_, lw in walk(v, [key], block):
                c = list(lw.vertices_in_wcs())
                m = list(midpoints(lw))
                assert len(c) == 4 and len(m) == 4, (c, m)
                print('("' + ">".join(path_) + '", new[] { ' + ", ".join(r(p.x) + ", " + r(p.y) for p in c) + " }, new[] { "
                      + ", ".join(r(p.x) + ", " + r(p.y) for p in m) + " }),")


# P6-34: a near-Z nested chain three levels deep with non-zero elevations, and a MINSERT with rows and columns
# inside a chain. Same ARCH block as P6-32. Blocks MID (base (10, 10)) holds INSERT MA of ARCH; TOP (base (-20, 40))
# holds INSERT TM of MID; GRID (base (0, 0)) holds the MINSERT GA of ARCH (2 columns, 3 rows). Model space: E1 (TOP)
# and F1 (GRID). Every insert of the chain has a non-zero elevation (OCS insertion z) and a near +-Z normal, so the
# block-z of a nested insertion reaches world XY through the enclosing insert's normal (z * N.xy, about 1e-3 here).
# Each outline is computed by composing ezdxf's own Insert.matrix44() of every level (innermost first) and
# transforming a copy of ARCH's LWPOLYLINE with LWPolyline.transform; MINSERT cells come from Insert.multi_insert() on
# the block-level MINSERT (row-major), each cell's matrix44() composed with its parents. This route is used instead
# of virtual_entities() because ezdxf's Insert.transform keeps a MINSERT's row/column spacing unscaled when the
# enclosing insert scales it (multi_insert "does not apply scaling to the grid"), whereas the spacing is a length in
# the enclosing block and scales with it. For the chain without a MINSERT (E1) the virtual_entities() walk is also run
# and must agree within 1e-9 (asserted).
# name, block, OCS insertion (x, y, z), rotation, sx, sy, columns, rows, column spacing, row spacing, extrusion
CHAIN_BLOCKS = [
    ("MID", (10.0, 10.0), [("MA", "ARCH", (400.0, -300.0, 1500.0), 35.0, 1.2, 1.2, 1, 1, 0.0, 0.0, (1e-7, 2e-7, -1.0))]),
    ("TOP", (-20.0, 40.0), [("TM", "MID", (2500.0, 1200.0, -2500.0), -50.0, 0.9, 0.9, 1, 1, 0.0, 0.0, (2e-7, 0.0, 1.0))]),
    ("GRID", (0.0, 0.0), [("GA", "ARCH", (-600.0, 900.0, 0.0), 10.0, 0.7, 0.7, 2, 3, 1500.0, -1100.0, (0.0, 1e-7, -1.0))]),
]
CHAIN_CASES = [
    ("E1", "TOP", (70000.0, -15000.0, 4000.0), 20.0, 1.1, 1.1, 1, 1, 0.0, 0.0, (-1e-7, 3e-7, -1.0)),
    ("F1", "GRID", (-60000.0, -20000.0, 0.0), 25.0, 1.3, 1.3, 1, 1, 0.0, 0.0, (-2e-7, 1e-7, 1.0)),
]


def add_insert(layout, case):
    name, block, ins, rot, sx, sy, cols, rows, csp, rsp, ext = case
    e = layout.add_blockref(block, ins, dxfattribs={"rotation": rot, "xscale": sx, "yscale": sy, "extrusion": ext})
    if cols > 1 or rows > 1:
        e.dxf.column_count = cols
        e.dxf.row_count = rows
        e.dxf.column_spacing = csp
        e.dxf.row_spacing = rsp
    return e


def build_chain():
    doc = ezdxf.new("R2018")
    doc.header["$INSUNITS"] = 4
    arch = doc.blocks.new("ARCH", base_point=(BULGE_BASE[0], BULGE_BASE[1], 0))
    arch.add_lwpolyline(BULGE_ARCH, format="xyb", close=True)
    inner = {}
    for block, base, cases in CHAIN_BLOCKS:
        layout = doc.blocks.new(block, base_point=(base[0], base[1], 0))
        inner[block] = [(case[0], case[1], add_insert(layout, case)) for case in cases]
    msp = doc.modelspace()
    return doc, inner, [(case[0], case[1], add_insert(msp, case)) for case in CHAIN_CASES]


def cell_keys(name, e):
    cols, rows = e.dxf.column_count, e.dxf.row_count
    if cols > 1 or rows > 1:
        return [(name + "[" + str(k // cols) + "," + str(k % cols) + "]", v) for k, v in enumerate(e.multi_insert())]
    return [(name, e)]


def chain_walk(doc, inner, insert_path, block, m):
    # m maps the coordinates of `block` to WCS.
    if block == "ARCH":
        lw = doc.blocks.get("ARCH").query("LWPOLYLINE")[0].copy()
        lw.transform(m)
        yield insert_path, lw
        return
    for name, child, e in inner[block]:
        for key, cell in cell_keys(name, e):
            yield from chain_walk(doc, inner, insert_path + [key], child, cell.matrix44() * m)


def leaves(insert):
    for v in insert.virtual_entities():
        if v.dxftype() == "INSERT":
            yield from leaves(v)
        else:
            yield v


def chain_main():
    print("// P6-34 chain outlines: (path, corner xy * 4, segment midpoint xy * 4)")
    doc, inner, top = build_chain()
    for name, block, e in top:
        for key, cell in cell_keys(name, e):
            for path_, lw in chain_walk(doc, inner, [key], block, cell.matrix44()):
                c = list(lw.vertices_in_wcs())
                m = list(midpoints(lw))
                assert len(c) == 4 and len(m) == 4, (c, m)
                if name == "E1":  # cross-check against ezdxf's virtual_entities() walk
                    others = [v for v in leaves(e) if v.dxftype() == "LWPOLYLINE"]
                    assert len(others) == 1, list(leaves(e))
                    for a, b in zip(c, others[0].vertices_in_wcs(), strict=True):
                        assert abs(a.x - b.x) <= 1e-9 and abs(a.y - b.y) <= 1e-9, (a, b)
                print('("' + ">".join(path_) + '", new[] { ' + ", ".join(r(p.x) + ", " + r(p.y) for p in c) + " }, new[] { "
                      + ", ".join(r(p.x) + ", " + r(p.y) for p in m) + " }),")



# P6-36: the near-Z chain remainder. Same ARCH outline (vertices and bulges) as P6-32/P6-34. Every case is under an
# enclosing near +-Z normal, where a block z reaches world XY through that normal's horizontal part:
#   * H1 > HM: a MINSERT (2 x 2, rows and columns) at elevation 1800 (group 30) inside a near +Z INSERT at elevation 2000.
#   * X1 > XE / XF: exact -Z (elevation -3000) and exact +Z (elevation 2200) nested inserts under a near -Z outer insert.
#   * Z1 > ZA: Z scale 2.5 (group 43) on the near +Z outer insert, nested insert at elevation 2000.
#   * K1: BLOCK base point z 900 (group 30 of BLOCK) under a near -Z insert.
#   * L1 / L2: leaf LWPOLYLINE elevation (group 38) 1200 in +Z and 800 in an exact -Z OCS, under near +Z inserts.
#   * W1 > CL: combined: near -Z outer, Z scale 1.5, elevation 1000; block CMID base z -400 holding a near +Z INSERT
#     at elevation 600 with Z scale 0.8 of ELEV (leaf elevation 1200).
# Outlines are computed like chain_main (composed Insert.matrix44() of every level, MINSERT cells from multi_insert();
# matrix44 includes the Z scale, and subtracts the base point through m.transform_direction, so its z counts) and,
# for every case without a MINSERT, cross-checked against the virtual_entities() walk within 1e-9. For each case the
# largest XY change against the same drawing with that case's z feature removed (nested group 30, group 43, base z
# or group 38 set to 0 / 1) is printed too, to show the case exercises it above 1e-6.
# block, base (x, y, z), leaf (elevation, extrusion) or None, inner inserts:
# (name, block, OCS insertion (x, y, z), rotation, sx, sy, sz, columns, rows, column spacing, row spacing, extrusion)
Z_BLOCKS = [
    ("ARCH", (50.0, 20.0, 0.0), (0.0, (0.0, 0.0, 1.0)), []),
    ("HOLD", (5.0, -5.0, 0.0), None, [("HM", "ARCH", (300.0, -200.0, 1800.0), 20.0, 0.6, 0.6, 1.0, 2, 2, 1400.0, -1000.0, (1e-7, -2e-7, -1.0))]),
    ("XHOLD", (0.0, 0.0, 0.0), None, [
        ("XE", "ARCH", (-400.0, 700.0, -3000.0), 40.0, 1.0, 1.0, 1.0, 1, 1, 0.0, 0.0, (0.0, 0.0, -1.0)),
        ("XF", "ARCH", (2600.0, -900.0, 2200.0), -15.0, 0.9, 0.9, 1.0, 1, 1, 0.0, 0.0, (0.0, 0.0, 1.0)),
    ]),
    ("ZHOLD", (0.0, 0.0, 0.0), None, [("ZA", "ARCH", (800.0, 600.0, 2000.0), 30.0, 1.0, 1.0, 1.0, 1, 1, 0.0, 0.0, (0.0, 0.0, 1.0))]),
    ("BASEZ", (50.0, 20.0, 900.0), (0.0, (0.0, 0.0, 1.0)), []),
    ("ELEV", (50.0, 20.0, 0.0), (1200.0, (0.0, 0.0, 1.0)), []),
    ("ELEVM", (50.0, 20.0, 0.0), (800.0, (0.0, 0.0, -1.0)), []),
    ("CMID", (5.0, 5.0, -400.0), None, [("CL", "ELEV", (-700.0, 300.0, 600.0), -25.0, 1.1, 1.1, 0.8, 1, 1, 0.0, 0.0, (2e-7, 1e-7, 1.0))]),
]
Z_CASES = [
    ("H1", "HOLD", (90000.0, 30000.0, 2000.0), 15.0, 1.1, 1.1, 1.0, 1, 1, 0.0, 0.0, (3e-7, -2e-7, 1.0)),
    ("X1", "XHOLD", (-90000.0, 40000.0, 500.0), 25.0, 1.0, 1.0, 1.0, 1, 1, 0.0, 0.0, (2e-7, 3e-7, -1.0)),
    ("Z1", "ZHOLD", (30000.0, -70000.0, 0.0), 10.0, 1.0, 1.0, 2.5, 1, 1, 0.0, 0.0, (-2e-7, 1e-7, 1.0)),
    ("K1", "BASEZ", (-30000.0, -60000.0, 0.0), 30.0, 1.0, 1.0, 1.0, 1, 1, 0.0, 0.0, (2e-7, -3e-7, -1.0)),
    ("L1", "ELEV", (10000.0, 80000.0, 0.0), -20.0, 1.0, 1.0, 1.0, 1, 1, 0.0, 0.0, (-3e-7, -1e-7, 1.0)),
    ("L2", "ELEVM", (60000.0, 80000.0, 0.0), 50.0, 1.0, 1.0, 1.0, 1, 1, 0.0, 0.0, (1e-7, 3e-7, 1.0)),
    ("W1", "CMID", (-60000.0, 90000.0, 1000.0), 35.0, 1.0, 1.0, 1.5, 1, 1, 0.0, 0.0, (-2e-7, -2e-7, -1.0)),
]
# The z feature each case exercises, removed for the "dropped" comparison: block or insert name -> what is removed
# (z: the OCS insertion z; sz: the Z scale; basez: the BLOCK base point z; elev: the leaf LWPOLYLINE elevation).
Z_DROPS = {
    "H1": {"HM": "z"},
    "X1": {"XE": "z", "XF": "z"},
    "Z1": {"Z1": "sz"},
    "K1": {"BASEZ": "basez"},
    "L1": {"ELEV": "elev"},
    "L2": {"ELEVM": "elev"},
    "W1": {"W1": "sz", "CL": "z", "CMID": "basez", "ELEV": "elev"},
}


def z_drop_case(case, drop):
    name, block, ins, rot, sx, sy, sz, cols, rows, csp, rsp, ext = case
    if drop.get(name) == "z":
        ins = (ins[0], ins[1], 0.0)
    if drop.get(name) == "sz":
        sz = 1.0
    return (name, block, ins, rot, sx, sy, sz, cols, rows, csp, rsp, ext)


def z_add_insert(layout, case):
    name, block, ins, rot, sx, sy, sz, cols, rows, csp, rsp, ext = case
    e = layout.add_blockref(block, ins, dxfattribs={"rotation": rot, "xscale": sx, "yscale": sy, "zscale": sz, "extrusion": ext})
    if cols > 1 or rows > 1:
        e.dxf.column_count = cols
        e.dxf.row_count = rows
        e.dxf.column_spacing = csp
        e.dxf.row_spacing = rsp
    return e


def build_z(drop=None):
    drop = drop or {}
    doc = ezdxf.new("R2018")
    doc.header["$INSUNITS"] = 4
    inner, leaf = {}, {}
    for block, base, lw, cases in Z_BLOCKS:
        bz = 0.0 if drop.get(block) == "basez" else base[2]
        layout = doc.blocks.new(block, base_point=(base[0], base[1], bz))
        if lw is not None:
            elevation, extrusion = lw
            if drop.get(block) == "elev":
                elevation = 0.0
            leaf[block] = layout.add_lwpolyline(BULGE_ARCH, format="xyb", close=True, dxfattribs={"elevation": elevation, "extrusion": extrusion})
        inner[block] = [(case[0], case[1], z_add_insert(layout, z_drop_case(case, drop))) for case in cases]
    msp = doc.modelspace()
    return leaf, inner, [(case[0], case[1], z_add_insert(msp, z_drop_case(case, drop))) for case in Z_CASES]


def z_walk(leaf, inner, insert_path, block, m):
    if block in leaf:
        lw = leaf[block].copy()
        lw.transform(m)
        yield insert_path, lw
    for name, child, e in inner[block]:
        for key, cell in cell_keys(name, e):
            yield from z_walk(leaf, inner, insert_path + [key], child, cell.matrix44() * m)


def z_outlines(drop=None):
    leaf, inner, top = build_z(drop)
    out = {}
    for name, block, e in top:
        for key, cell in cell_keys(name, e):
            for path_, lw in z_walk(leaf, inner, [key], block, cell.matrix44()):
                out[">".join(path_)] = (name, e, lw)
    return out


def z_main():
    print("// P6-36 z outlines: (path, corner xy * 4, segment midpoint xy * 4)")
    outlines = z_outlines()
    for key, (name, e, lw) in outlines.items():
        c = list(lw.vertices_in_wcs())
        m = list(midpoints(lw))
        assert len(c) == 4 and len(m) == 4, (c, m)
        if e.dxf.column_count <= 1 and e.dxf.row_count <= 1 and name != "H1":
            # cross-check against ezdxf's virtual_entities() walk (no MINSERT in these chains)
            others = [v for v in leaves(e) if v.dxftype() == "LWPOLYLINE"] if name not in ("K1", "L1", "L2") else [v for v in e.virtual_entities() if v.dxftype() == "LWPOLYLINE"]
            match = [o for o in others if all(abs(a.x - b.x) <= 1e-9 and abs(a.y - b.y) <= 1e-9 for a, b in zip(c, o.vertices_in_wcs(), strict=True))]
            assert len(match) == 1, (key, c, [list(o.vertices_in_wcs()) for o in others])
        print('("' + key + '", new[] { ' + ", ".join(r(p.x) + ", " + r(p.y) for p in c) + " }, new[] { "
              + ", ".join(r(p.x) + ", " + r(p.y) for p in m) + " }),")
    print("// P6-36 largest XY change when the case's z feature is removed: (path, change)")
    for case in Z_CASES:
        name = case[0]
        dropped = z_outlines(Z_DROPS[name])
        for key, (_, _, lw) in outlines.items():
            if key.split(">")[0].split("[")[0] != name:
                continue
            a = list(lw.vertices_in_wcs())
            b = list(dropped[key][2].vertices_in_wcs())
            print("// " + key + " " + r(max(max(abs(p.x - q.x), abs(p.y - q.y)) for p, q in zip(a, b, strict=True))))



# P6-38: leaf z under near +-Z inserts. Block LZ (base (40, -30, 0)) holds one leaf of every modelled kind, each with a
# non-zero z: LINE LA (z 1500 / -700), CIRCLE LC (+Z OCS, elevation 1100) and LD (exact -Z OCS, elevation 600), ARC LE
# (+Z, 900), LF (exact -Z, 700) and LG (its own near -Z OCS (1e-7,-1e-7,-1), 500), TEXT LT (+Z, 1300) and LU (exact -Z,
# 400), MTEXT LM (WCS z -1600), a constant ATTDEF LK (z 2100), the tilted LWPOLYLINE LP (extrusion (0.6,0,0.8), group
# 38 200, straight along Ax = (0,1,0), world z 0.6 * 500 + 0.8 * 200 = 460) and a heavy POLYLINE LH (elevation 800;
# not modelled by DxfReader, printed for the record only). Block TAG holds a LINE; block LHOLD (base (10, 20, 0)) holds
# INSERT TI of TAG at z 1200 carrying ATTRIB ROOM at z 1900 (in LHOLD coordinates). Model space: Q1 (LZ under near +Z,
# scale 1.2), Q2 (LZ under near -Z, scale 0.9, Z scale -1.5: a negative group 43, accepted like a negative 41/42) and
# Q3 (LHOLD under near -Z). Expected WCS points: ezdxf's composed Insert.matrix44() applied to each leaf point taken to
# block WCS by ezdxf.math.OCS(extrusion).to_wcs; cross-checked within 1e-9 against the leaf copied and transformed with
# the entity's own transform(m) (Q3's ATTRIB: against the ATTRIB of the INSERT that Q3.virtual_entities() yields).
# For every point the XY change when its z is set to 0 is printed, to show each is exercised above 1e-6.
LEAF_BASE = (40.0, -30.0, 0.0)
# name, kind, data, extrusion
LEAF_Z = [
    ("LA", "LINE", ((100.0, 200.0, 1500.0), (900.0, 250.0, -700.0)), (0.0, 0.0, 1.0)),
    ("LC", "CIRCLE", ((300.0, 400.0, 1100.0), 150.0), (0.0, 0.0, 1.0)),
    ("LD", "CIRCLE", ((300.0, 400.0, 600.0), 120.0), (0.0, 0.0, -1.0)),
    ("LE", "ARC", ((500.0, -300.0, 900.0), 200.0, 10.0, 130.0), (0.0, 0.0, 1.0)),
    ("LF", "ARC", ((-200.0, 100.0, 700.0), 250.0, 20.0, 200.0), (0.0, 0.0, -1.0)),
    ("LG", "ARC", ((250.0, -150.0, 500.0), 90.0, 0.0, 90.0), (1e-7, -1e-7, -1.0)),
    ("LT", "TEXT", ((50.0, 60.0, 1300.0), 100.0, 15.0, "Sala"), (0.0, 0.0, 1.0)),
    ("LU", "TEXT", ((120.0, -80.0, 400.0), 100.0, 30.0, "Cocina"), (0.0, 0.0, -1.0)),
    ("LM", "MTEXT", ((700.0, 800.0, -1600.0), 120.0, 0.0, "Bano"), (0.0, 0.0, 1.0)),
    ("LK", "ATTDEF", ((400.0, 100.0, 2100.0), 80.0, 0.0, "K"), (0.0, 0.0, 1.0)),
    ("LP", "LWPOLYLINE", (((100.0, 500.0), (1300.0, 500.0)), 200.0), (0.6, 0.0, 0.8)),
    ("LH", "POLYLINE", (((0.0, 0.0), (1000.0, 0.0), (1000.0, 600.0)), 800.0), (0.0, 0.0, 1.0)),
]
TAG_INSERT = ("TI", (2000.0, 1000.0, 1200.0), 20.0)
TAG_ATTRIB = ("ROOM", "Living", (2100.0, 1050.0, 1900.0), 150.0)
LEAF_CASES = [
    ("Q1", "LZ", (50000.0, 20000.0, 700.0), 25.0, 1.2, 1.2, 1.0, (3e-7, -2e-7, 1.0)),
    ("Q2", "LZ", (-40000.0, 30000.0, -300.0), -40.0, 0.9, 0.9, -1.5, (-2e-7, 1e-7, -1.0)),
    ("Q3", "LHOLD", (20000.0, -50000.0, 0.0), 10.0, 1.0, 1.0, 1.0, (2e-7, 3e-7, -1.0)),
]


def leaf_points(kind, data, ext, flat=False):
    # (label, block WCS point) of every point checked for a leaf; flat sets the block WCS z of every point to 0 (the
    # reading that drops it), after the leaf's own OCS has placed it.
    import math
    ocs = OCS(ext)
    if kind == "LINE":
        points = [("start", Vec3(data[0])), ("end", Vec3(data[1]))]
    elif kind == "CIRCLE":
        points = [("center", ocs.to_wcs(data[0]))]
    elif kind == "ARC":
        (cx, cy, cz), radius, a0, a1 = data
        mid = a0 + ((a1 - a0) % 360.0) / 2
        at = lambda a: ocs.to_wcs((cx + radius * math.cos(math.radians(a)), cy + radius * math.sin(math.radians(a)), cz))
        points = [("center", ocs.to_wcs((cx, cy, cz))), ("start", at(a0)), ("mid", at(mid)), ("end", at(a1))]
    elif kind in ("TEXT", "ATTDEF", "ATTRIB"):
        points = [("insert", ocs.to_wcs(data[0]))]
    elif kind == "MTEXT":
        points = [("insert", Vec3(data[0]))]
    elif kind in ("LWPOLYLINE", "POLYLINE"):
        vertices, elevation = data
        points = [("v" + str(k), ocs.to_wcs((x, y, elevation))) for k, (x, y) in enumerate(vertices)]
    else:
        raise ValueError(kind)
    return [(label, Vec3(p.x, p.y, 0.0)) for label, p in points] if flat else points


def build_leaf():
    doc = ezdxf.new("R2018")
    doc.header["$INSUNITS"] = 4
    lz = doc.blocks.new("LZ", base_point=LEAF_BASE)
    made = {}
    for name, kind, data, ext in LEAF_Z:
        if kind == "LINE":
            e = lz.add_line(data[0], data[1])
        elif kind == "CIRCLE":
            e = lz.add_circle(data[0], data[1], dxfattribs={"extrusion": ext})
        elif kind == "ARC":
            e = lz.add_arc(data[0], data[1], data[2], data[3], dxfattribs={"extrusion": ext})
        elif kind == "TEXT":
            e = lz.add_text(data[3], dxfattribs={"insert": data[0], "height": data[1], "rotation": data[2], "extrusion": ext})
        elif kind == "MTEXT":
            e = lz.add_mtext(data[3], dxfattribs={"insert": data[0], "char_height": data[1]})
        elif kind == "ATTDEF":
            e = lz.add_attdef(data[3], data[0], "X", dxfattribs={"height": data[1], "flags": 2})
        elif kind == "LWPOLYLINE":
            e = lz.add_lwpolyline(data[0], dxfattribs={"elevation": data[1], "extrusion": ext})
        else:
            e = lz.add_polyline2d(data[0], dxfattribs={"elevation": (0.0, 0.0, data[1])}, close=True)
        made[name] = e
    tag = doc.blocks.new("TAG", base_point=(0.0, 0.0, 0.0))
    tag.add_line((0.0, 0.0), (500.0, 0.0))
    hold = doc.blocks.new("LHOLD", base_point=(10.0, 20.0, 0.0))
    ti = hold.add_blockref("TAG", TAG_INSERT[1], dxfattribs={"rotation": TAG_INSERT[2]})
    attrib = ti.add_attrib(TAG_ATTRIB[0], TAG_ATTRIB[1], TAG_ATTRIB[2], dxfattribs={"height": TAG_ATTRIB[3]})
    attrib.dxf.insert = Vec3(TAG_ATTRIB[2])
    msp = doc.modelspace()
    top = {}
    for name, block, ins, rot, sx, sy, sz, ext in LEAF_CASES:
        top[name] = msp.add_blockref(block, ins, dxfattribs={"rotation": rot, "xscale": sx, "yscale": sy, "zscale": sz, "extrusion": ext})
    return made, attrib, top


def transformed_points(e, kind, m):
    # The same points from the leaf copied and transformed by ezdxf's own entity transform.
    import math
    c = e.copy()
    c.transform(m)
    if kind == "LINE":
        return [c.dxf.start, c.dxf.end]
    if kind == "CIRCLE":
        return [c.ocs().to_wcs(c.dxf.center)]
    if kind == "ARC":
        ocs = c.ocs()
        span = (c.dxf.end_angle - c.dxf.start_angle) % 360.0
        cx, cy, cz = c.dxf.center
        at = lambda a: ocs.to_wcs((cx + c.dxf.radius * math.cos(math.radians(a)), cy + c.dxf.radius * math.sin(math.radians(a)), cz))
        return [ocs.to_wcs(c.dxf.center), at(c.dxf.start_angle), at(c.dxf.start_angle + span / 2), at(c.dxf.end_angle)]
    if kind in ("TEXT", "ATTDEF", "ATTRIB"):
        return [c.ocs().to_wcs(c.dxf.insert)]
    if kind == "MTEXT":
        return [c.dxf.insert]
    if kind == "LWPOLYLINE":
        return list(c.vertices_in_wcs())
    return [c.ocs().to_wcs(v.dxf.location) for v in c.vertices]


def leaf_main():
    print("// P6-38 leaf z points: (path, handle, label, x, y, xy change when z is dropped)")
    made, attrib, top = build_leaf()
    for name, block, *_ in LEAF_CASES:
        m = top[name].matrix44()
        if block == "LZ":
            for handle, kind, data, ext in LEAF_Z:
                points = leaf_points(kind, data, ext)
                flat = leaf_points(kind, data, ext, flat=True)
                got = [m.transform(p) for _, p in points]
                check = transformed_points(made[handle], kind, m)
                if kind == "ARC" and (check[1] - got[1]).magnitude > 1e-6:
                    check = [check[0], check[3], check[2], check[1]]  # a mirroring transform swaps the end angles
                assert all((a - b).magnitude <= 1e-9 for a, b in zip(got, check, strict=True)), (name, handle, got, check)
                for (label, _), g, (_, f) in zip(points, got, flat):
                    d = m.transform(f)
                    print('("' + name + '", "' + handle + '", "' + label + '", ' + r(g.x) + ", " + r(g.y) + "), // dropped " + r(max(abs(g.x - d.x), abs(g.y - d.y))))
        else:
            # Q3: the ATTRIB of INSERT TI lives in LHOLD coordinates, so only Q3's matrix places it.
            data = (TAG_ATTRIB[2], TAG_ATTRIB[3], 0.0, TAG_ATTRIB[1])
            (_, p), = leaf_points("ATTRIB", data, (0.0, 0.0, 1.0))
            (_, f), = leaf_points("ATTRIB", data, (0.0, 0.0, 1.0), flat=True)
            g = m.transform(p)
            virtual = [v for v in top[name].virtual_entities() if v.dxftype() == "INSERT"]
            assert len(virtual) == 1 and len(virtual[0].attribs) == 1, virtual
            va = virtual[0].attribs[0]
            check = va.ocs().to_wcs(va.dxf.insert)
            assert (check - g).magnitude <= 1e-9, (g, check)
            assert (transformed_points(attrib, "ATTRIB", m)[0] - g).magnitude <= 1e-9
            d = m.transform(f)
            print('("' + name + '", "ROOM", "insert", ' + r(g.x) + ", " + r(g.y) + "), // dropped " + r(max(abs(g.x - d.x), abs(g.y - d.y))))
    print("// P6-38 model-space tilted LWPOLYLINE LP: world z of its plane", r(leaf_points("LWPOLYLINE", LEAF_Z[10][2], LEAF_Z[10][3])[0][1].z))


if __name__ == "__main__":
    main()
    bulge_main()
    chain_main()
    z_main()
    leaf_main()
