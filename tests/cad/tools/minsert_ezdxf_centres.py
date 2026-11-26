"""Generator of the ezdxf cross-check constants in tests/cad/MinsertEzdxfTests.cs (P6-30).

Needs ezdxf (MIT, https://github.com/mozman/ezdxf); the constants in the test were produced with
ezdxf 1.4.2 (tag v1.4.2, commit 8240df5793daf2fd7db95d118a78e17ec92043ec) on Python 3 with numpy 2.5.3:

    PYTHONPATH=<ezdxf checkout>/src python3 tests/cad/tools/minsert_ezdxf_centres.py

(PyPI was blocked where the constants were generated, so ezdxf was imported from a source checkout of tag v1.4.2;
`pip install ezdxf==1.4.2` works equally where PyPI is reachable.)

P6-44: `... minsert_ezdxf_centres.py --check [path to MinsertEzdxfTests.cs]` runs every section and compares the
printed constant rows with the committed C# constants both ways (P6-46: whole rows, exact doubles, planted
self-tests); exit status 1 on any difference. P6-46: CI runs it (engineering-gates ifc job: pip install ezdxf==1.4.2,
EZDXF_DISABLE_C_EXT=1), as recorded in the header of tests/cad/MinsertEzdxfTests.cs.

P6-50 (generation environment, stated the same way in the header of tests/cad/MinsertEzdxfTests.cs, which pins it):
the constants are the pure-Python ezdxf 1.4.2 values with numpy 2.5.3. Evidence for that generation version: the
offline environment the generator is run in (re-measured in relay293: Python 3.13.18, numpy 2.5.3, pyparsing 3.3.3,
typing_extensions 4.16.0, fonttools 4.67.0) reproduces every row, and CI run 45718082997 (main 36398c0, Python 3.12.16,
pip's numpy==2.5.3, EZDXF_DISABLE_C_EXT=1) reproduced all 164 rows exactly (0 failures). The CI step installs
ezdxf==1.4.2 numpy==2.5.3 pyparsing==3.3.3 typing_extensions==4.16.0 fonttools==4.69.1 (the other dependencies at the
versions pip resolved in CI run 45725166224, printed by the step with numpy's; fonttools 4.67.0 offline and 4.69.1
in CI give the same rows). Of the pure-Python run's output, every output line but the caught self-tests is annotated,
20 lines an annotation, at most 9 annotations, then one line with the count of the lines left over (errors when the
run fails, notices otherwise). With the wheel's C extensions four rows differ in the last bits: C1>NB, X1>XF, Z1>ZA,
W1>CL. They are pinned (C_EXT_DRIFT below): under the C extensions a differing row outside them fails that run and the
step, named after "new C-extension drift rows: "; a pinned row that stops differing is named as no longer drifting.

It builds, with ezdxf's own API, the block RECT and the INSERT/MINSERT entities that MinsertEzdxfTests.Drawing()
writes by hand (same parameters, listed in CASES), and prints, as C# tuples:
  * for every MINSERT cell (ezdxf Insert.multi_insert(), row-major) the four WCS corners of the cell's RECT outline
    (Insert.virtual_entities() then ezdxf.path.make_path, so the OCS of mirrored and near-Z inserts is applied by ezdxf)
    and their mean (the cell centre), projected to world XY;
  * the arbitrary-axis OCS axes ezdxf computes (ezdxf.math.OCS) for the extrusion normals used in the test;
  * for the tilted inserts (non-axis extrusion, which DxfReader reports as ocs-non-planar instead of projecting), the
    world XY centre the projection reading would give, used as decoy label positions.

P6-34, P6-36, P6-38, P6-40, P6-42 and P6-44 extensions (chain_main, z_main, leaf_main, follow_main, align_main,
justify_main) and the --check mode are described at their sections below.

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

import contextlib
import io
import os
import re
import sys

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


# P6-40: near-Z leaf z follow-ups. Block IDN (base 0) holds LINE N1 from z 0 to z 800, placed by IN at the origin
# under a near +Z normal: its XY placement is the identity (DxfExploder's Relabel path) and only the end moves, by
# 800 times the normal's horizontal part. Block JZ (base (40, -30, 0)) holds justified leaves (72/74 non-zero, second
# alignment point 11/21/31): TEXT JT (+Z) and JU (exact -Z), whose group 10 point DxfReader keeps (checked here as
# ezdxf's transformed insert), and constant ATTDEFs JK (+Z, 31 = 30), JL (+Z, 31 = 1700 != 30 = 1000), JM (exact -Z,
# 31 = 30) and JN (own near -Z OCS (1e-7,-1e-7,-1), 31 = 900 != 30 = 500), whose 11 point DxfReader keeps (checked as
# ezdxf's transformed align_point). Block JHOLD (base (10, 20, 0)) holds INSERT JI of TAG at z 1200 with justified
# ATTRIBs JR (31 = 30 = 1900) and JS (31 = 1400 != 30 = 1900). Block MHOLD (base 0) holds MINSERT MI of TAG (+Z,
# elevation 1200, 2 columns x 3 rows, spacing 3000/2000, rotation 30) with ATTRIB MA at z 1900 and MINSERT MJ (exact
# -Z OCS, elevation 600, 3 columns x 2 rows, spacing 2500/-1500, rotation 15) with ATTRIB MB at OCS z 700. Model space:
# IN; J1 (JZ, near +Z, scale 1.2), J2 (JZ, near -Z, Z scale -1.5), J3 (JHOLD, near -Z), M1 (MHOLD, near +Z, scale
# 1.1) and M2 (MHOLD, near -Z, Z scale -1.5). Expected WCS points: the composed Insert.matrix44() applied to the leaf
# point taken to block WCS by the leaf's own OCS; MINSERT cells from Insert.multi_insert() on MI/MJ themselves
# (row-major, ATTRIBs moved by ezdxf), each cell's ATTRIB composed with the top insert's matrix. Cross-checked within
# 1e-9 against the leaf copied and transformed with its own transform(m) (insert or align_point).
FOLLOW_BLOCK_BASE = (40.0, -30.0, 0.0)
# name, kind, insert (10/20/30), align point (11/21/31), halign (72), valign (73 TEXT / 74 ATTDEF), extrusion
FOLLOW_JUSTIFIED = [
    ("JT", "TEXT", (50.0, 60.0, 1300.0), (150.0, 60.0, 1300.0), 1, 0, (0.0, 0.0, 1.0)),
    ("JU", "TEXT", (120.0, -80.0, 400.0), (220.0, -80.0, 400.0), 2, 0, (0.0, 0.0, -1.0)),
    ("JK", "ATTDEF", (400.0, 100.0, 2100.0), (460.0, 100.0, 2100.0), 1, 0, (0.0, 0.0, 1.0)),
    ("JL", "ATTDEF", (300.0, 300.0, 1000.0), (350.0, 320.0, 1700.0), 4, 0, (0.0, 0.0, 1.0)),
    ("JM", "ATTDEF", (-150.0, 250.0, 800.0), (-90.0, 250.0, 800.0), 2, 1, (0.0, 0.0, -1.0)),
    ("JN", "ATTDEF", (250.0, -150.0, 500.0), (300.0, -120.0, 900.0), 0, 2, (1e-7, -1e-7, -1.0)),
]
FOLLOW_JI = ("JI", (2000.0, 1000.0, 1200.0), 20.0)
FOLLOW_JATTRIBS = [
    ("JR", "R", (2100.0, 1050.0, 1900.0), (2160.0, 1050.0, 1900.0), 1, 0),
    ("JS", "S", (2000.0, 900.0, 1900.0), (2050.0, 950.0, 1400.0), 0, 2),
]
# name, insertion, rotation, columns, rows, column spacing, row spacing, extrusion, attrib handle, attrib OCS point
FOLLOW_MINSERTS = [
    ("MI", (1000.0, 500.0, 1200.0), 30.0, 2, 3, 3000.0, 2000.0, (0.0, 0.0, 1.0), "MA", (1100.0, 550.0, 1900.0)),
    ("MJ", (-2000.0, 800.0, 600.0), 15.0, 3, 2, 2500.0, -1500.0, (0.0, 0.0, -1.0), "MB", (-1900.0, 850.0, 700.0)),
]
FOLLOW_CASES = [
    ("IN", "IDN", (0.0, 0.0, 0.0), 0.0, 1.0, 1.0, 1.0, (3e-7, -2e-7, 1.0)),
    ("J1", "JZ", (50000.0, 20000.0, 700.0), 25.0, 1.2, 1.2, 1.0, (3e-7, -2e-7, 1.0)),
    ("J2", "JZ", (-40000.0, 30000.0, -300.0), -40.0, 0.9, 0.9, -1.5, (-2e-7, 1e-7, -1.0)),
    ("J3", "JHOLD", (20000.0, -50000.0, 0.0), 10.0, 1.0, 1.0, 1.0, (2e-7, 3e-7, -1.0)),
    ("M1", "MHOLD", (70000.0, 10000.0, 400.0), 35.0, 1.1, 1.1, 1.0, (-3e-7, 2e-7, 1.0)),
    ("M2", "MHOLD", (-70000.0, -10000.0, 0.0), -20.0, 1.0, 1.0, -1.5, (2e-7, -3e-7, -1.0)),
]


def build_follow():
    doc = ezdxf.new("R2018")
    doc.header["$INSUNITS"] = 4
    idn = doc.blocks.new("IDN", base_point=(0.0, 0.0, 0.0))
    line = idn.add_line((0.0, 0.0, 0.0), (1000.0, 0.0, 800.0))
    jz = doc.blocks.new("JZ", base_point=FOLLOW_BLOCK_BASE)
    made = {}
    for name, kind, ins, align, h, v, ext in FOLLOW_JUSTIFIED:
        attribs = {"insert": ins, "align_point": align, "halign": h, "height": 100.0, "extrusion": ext}
        if kind == "TEXT":
            attribs["valign"] = v
            e = jz.add_text(name, dxfattribs=attribs)
        else:
            attribs["valign"] = v
            attribs["flags"] = 2
            e = jz.add_attdef(name, ins, "X", dxfattribs=attribs)
            e.dxf.align_point = Vec3(align)
        made[name] = e
    tag = doc.blocks.new("TAG", base_point=(0.0, 0.0, 0.0))
    tag.add_line((0.0, 0.0), (500.0, 0.0))
    hold = doc.blocks.new("JHOLD", base_point=(10.0, 20.0, 0.0))
    ji = hold.add_blockref("TAG", FOLLOW_JI[1], dxfattribs={"rotation": FOLLOW_JI[2]})
    for name, tagname, ins, align, h, v in FOLLOW_JATTRIBS:
        a = ji.add_attrib(tagname, name, ins, dxfattribs={"height": 150.0, "halign": h, "valign": v})
        a.dxf.insert = Vec3(ins)
        a.dxf.align_point = Vec3(align)
        made[name] = a
    mhold = doc.blocks.new("MHOLD", base_point=(0.0, 0.0, 0.0))
    minserts = {}
    for name, ins, rot, cols, rows, csp, rsp, ext, aname, apt in FOLLOW_MINSERTS:
        mi = mhold.add_blockref("TAG", ins, dxfattribs={"rotation": rot, "extrusion": ext})
        mi.dxf.column_count = cols
        mi.dxf.row_count = rows
        mi.dxf.column_spacing = csp
        mi.dxf.row_spacing = rsp
        a = mi.add_attrib("ROOM", aname, apt, dxfattribs={"height": 120.0, "extrusion": ext})
        a.dxf.insert = Vec3(apt)
        minserts[name] = (mi, aname)
    msp = doc.modelspace()
    top = {}
    for name, block, ins, rot, sx, sy, sz, ext in FOLLOW_CASES:
        top[name] = msp.add_blockref(block, ins, dxfattribs={"rotation": rot, "xscale": sx, "yscale": sy, "zscale": sz, "extrusion": ext})
    return line, made, minserts, top


def follow_point(e, which):
    # WCS of a text-like entity's insert or align point (its own OCS); a LINE's end.
    if e.dxftype() == "LINE":
        return Vec3(e.dxf.end)
    return e.ocs().to_wcs(Vec3(getattr(e.dxf, which)))


def follow_main():
    print("// P6-40 follow-up points: (path, handle, label, x, y, xy change when z is dropped)")
    line, made, minserts, top = build_follow()

    def emit(path, handle, label, g, flat):
        print('("' + path + '", "' + handle + '", "' + label + '", ' + r(g.x) + ", " + r(g.y) + "), // dropped " + r(max(abs(g.x - flat.x), abs(g.y - flat.y))))

    def flat_of(p):
        return Vec3(p.x, p.y, 0.0)

    for name, block, *_ in FOLLOW_CASES:
        m = top[name].matrix44()
        if block == "IDN":
            p = Vec3(line.dxf.end)
            g = m.transform(p)
            c = line.copy()
            c.transform(m)
            assert (Vec3(c.dxf.end) - g).magnitude <= 1e-9, (g, c.dxf.end)
            emit(name, "N1", "end", g, m.transform(flat_of(p)))
            s = m.transform(Vec3(line.dxf.start))
            print("// " + name + " N1 start " + r(s.x) + ", " + r(s.y))
        elif block == "JZ":
            for handle, kind, *_ in FOLLOW_JUSTIFIED:
                e = made[handle]
                which = "insert" if kind == "TEXT" else "align_point"
                p = follow_point(e, which)
                g = m.transform(p)
                c = e.copy()
                c.transform(m)
                assert (follow_point(c, which) - g).magnitude <= 1e-9, (name, handle, g, follow_point(c, which))
                emit(name, handle, "insert", g, m.transform(flat_of(p)))
                if kind != "TEXT":
                    # The reading that takes the z of the group 10 point for the group 11 point.
                    e10 = e.copy()
                    a = Vec3(e.dxf.align_point)
                    e10.dxf.align_point = Vec3(a.x, a.y, Vec3(e.dxf.insert).z)
                    w = m.transform(follow_point(e10, "align_point"))
                    print("//   " + name + " " + handle + " with the z of group 30: off by " + r(max(abs(g.x - w.x), abs(g.y - w.y))))
        elif block == "JHOLD":
            for handle, *_ in FOLLOW_JATTRIBS:
                e = made[handle]
                p = follow_point(e, "align_point")
                g = m.transform(p)
                c = e.copy()
                c.transform(m)
                assert (follow_point(c, "align_point") - g).magnitude <= 1e-9
                virtual = [v for v in top[name].virtual_entities() if v.dxftype() == "INSERT"]
                va = [a for a in virtual[0].attribs if a.dxf.text == handle][0]
                assert (follow_point(va, "align_point") - g).magnitude <= 1e-9
                emit(name, handle, "insert", g, m.transform(flat_of(p)))
                e10 = e.copy()
                a = Vec3(e.dxf.align_point)
                e10.dxf.align_point = Vec3(a.x, a.y, Vec3(e.dxf.insert).z)
                w = m.transform(follow_point(e10, "align_point"))
                print("//   " + name + " " + handle + " with the z of group 30: off by " + r(max(abs(g.x - w.x), abs(g.y - w.y))))
        else:
            for mname, (mi, aname) in minserts.items():
                cols, rows = mi.dxf.column_count, mi.dxf.row_count
                for k, cell in enumerate(mi.multi_insert()):
                    row, col = divmod(k, cols)
                    (a,) = cell.attribs
                    p = a.ocs().to_wcs(Vec3(a.dxf.insert))
                    g = m.transform(p)
                    c = a.copy()
                    c.transform(m)
                    assert (follow_point(c, "insert") - g).magnitude <= 1e-9
                    emit(name + ">" + mname + "[" + str(row) + "," + str(col) + "]", aname, "insert", g, m.transform(flat_of(p)))


# P6-42: aligned / fit text (72 = 3 / 5) under near +-Z inserts. For ALIGNED and FIT ezdxf's Text.get_placement()
# returns the group 10 point (insert) as p1 and the group 11 point (align_point) as p2 (for 72 = 1/2/4 it returns only
# align_point); DxfReader keeps the group 10 point with the z of group 30 for both TEXT and ATTRIB/ATTDEF (an
# ATTRIB/ATTDEF is "justified" only for 72 = 1/2/4 or 74 != 0). Block AF (base (40, -30, 0)) holds TEXT FA (72 = 3,
# +Z), FF (72 = 5, exact -Z OCS), FN (72 = 3, its own near +Z OCS (1e-7, -1e-7, 1)) and constant ATTDEFs FB (72 = 3,
# its own near -Z OCS (-1e-7, 2e-7, -1)) and FG (72 = 5, +Z); every one has 31 != 30 and an align point 400 away.
# Block AHOLD (base (10, 20, 0)) holds INSERT AI of TAG (a LINE) at z 1200, rotation 20, with ATTRIBs FR (72 = 3) and
# FS (72 = 5, exact -Z OCS), again 31 != 30. Model space: A1 (AF, near +Z, scale 1.2, elevation 700), A2 (AF, near
# -Z, Z scale -1.5) and A3 (AHOLD, near -Z). Expected: the composed Insert.matrix44() applied to the group 10 point taken
# to block WCS by the leaf's own OCS, cross-checked within 1e-9 against a copy transformed with its own transform(m)
# and, for A3, against the ATTRIB of virtual_entities(). Printed for the record: how far the point moves if the z of
# group 31 is used instead of 30, and how far the transformed align point (p2) lies from it.
ALIGN_BLOCK_BASE = (40.0, -30.0, 0.0)
# name, kind, insert (10/20/30), align point (11/21/31), halign (72), extrusion
ALIGN_LEAVES = [
    ("FA", "TEXT", (50.0, 60.0, 1300.0), (450.0, 60.0, 1700.0), 3, (0.0, 0.0, 1.0)),
    ("FF", "TEXT", (120.0, -80.0, 400.0), (520.0, -80.0, 900.0), 5, (0.0, 0.0, -1.0)),
    ("FN", "TEXT", (-200.0, 150.0, 600.0), (200.0, 150.0, 1100.0), 3, (1e-7, -1e-7, 1.0)),
    ("FB", "ATTDEF", (300.0, 300.0, 1000.0), (700.0, 300.0, 1700.0), 3, (-1e-7, 2e-7, -1.0)),
    ("FG", "ATTDEF", (-150.0, 250.0, 800.0), (250.0, 250.0, 1200.0), 5, (0.0, 0.0, 1.0)),
]
ALIGN_AI = ("AI", (2000.0, 1000.0, 1200.0), 20.0)
# name, tag, insert, align point, halign, extrusion
ALIGN_ATTRIBS = [
    ("FR", "R", (2100.0, 1050.0, 1900.0), (2500.0, 1050.0, 1400.0), 3, (0.0, 0.0, 1.0)),
    ("FS", "S", (-2000.0, 900.0, 1900.0), (-1600.0, 900.0, 2600.0), 5, (0.0, 0.0, -1.0)),
]
ALIGN_CASES = [
    ("A1", "AF", (50000.0, 20000.0, 700.0), 25.0, 1.2, 1.2, 1.0, (3e-7, -2e-7, 1.0)),
    ("A2", "AF", (-40000.0, 30000.0, -300.0), -40.0, 0.9, 0.9, -1.5, (-2e-7, 1e-7, -1.0)),
    ("A3", "AHOLD", (20000.0, -50000.0, 0.0), 10.0, 1.0, 1.0, 1.0, (2e-7, 3e-7, -1.0)),
]


def build_align():
    doc = ezdxf.new("R2018")
    doc.header["$INSUNITS"] = 4
    af = doc.blocks.new("AF", base_point=ALIGN_BLOCK_BASE)
    made = {}
    for name, kind, ins, align, h, ext in ALIGN_LEAVES:
        attribs = {"insert": ins, "align_point": align, "halign": h, "height": 100.0, "extrusion": ext}
        if kind == "TEXT":
            e = af.add_text(name, dxfattribs=attribs)
        else:
            attribs["flags"] = 2
            e = af.add_attdef(name, ins, "X", dxfattribs=attribs)
            e.dxf.align_point = Vec3(align)
        e.dxf.insert = Vec3(ins)
        assert e.get_placement()[0].name in ("ALIGNED", "FIT"), (name, e.get_placement())
        made[name] = e
    tag = doc.blocks.new("TAG", base_point=(0.0, 0.0, 0.0))
    tag.add_line((0.0, 0.0), (500.0, 0.0))
    hold = doc.blocks.new("AHOLD", base_point=(10.0, 20.0, 0.0))
    ai = hold.add_blockref("TAG", ALIGN_AI[1], dxfattribs={"rotation": ALIGN_AI[2]})
    for name, tagname, ins, align, h, ext in ALIGN_ATTRIBS:
        a = ai.add_attrib(tagname, name, ins, dxfattribs={"height": 150.0, "halign": h, "extrusion": ext})
        a.dxf.insert = Vec3(ins)
        a.dxf.align_point = Vec3(align)
        made[name] = a
    msp = doc.modelspace()
    top = {}
    for name, block, ins, rot, sx, sy, sz, ext in ALIGN_CASES:
        top[name] = msp.add_blockref(block, ins, dxfattribs={"rotation": rot, "xscale": sx, "yscale": sy, "zscale": sz, "extrusion": ext})
    return made, top


def align_main():
    print("// P6-42 aligned/fit points: (path, handle, x, y), group 10 point; then z-of-31 and align-point distances")
    made, top = build_align()
    for name, block, *_ in ALIGN_CASES:
        m = top[name].matrix44()
        handles = [leaf[0] for leaf in ALIGN_LEAVES] if block == "AF" else [a[0] for a in ALIGN_ATTRIBS]
        path_name = name if block == "AF" else name + ">AI"
        for handle in handles:
            e = made[handle]
            p = follow_point(e, "insert")
            g = m.transform(p)
            c = e.copy()
            c.transform(m)
            assert (follow_point(c, "insert") - g).magnitude <= 1e-9, (name, handle)
            if block == "AHOLD":
                virtual = [v for v in top[name].virtual_entities() if v.dxftype() == "INSERT"]
                va = [a for a in virtual[0].attribs if a.dxf.text == handle][0]
                assert (follow_point(va, "insert") - g).magnitude <= 1e-9, (name, handle)
            print('("' + path_name + '", "' + handle + '", ' + r(g.x) + ", " + r(g.y) + "),")
            e31 = e.copy()
            q = Vec3(e.dxf.insert)
            e31.dxf.insert = Vec3(q.x, q.y, Vec3(e.dxf.align_point).z)
            w = m.transform(follow_point(e31, "insert"))
            p2 = m.transform(follow_point(e, "align_point"))
            print("//   " + path_name + " " + handle + " with the z of group 31: off by " + r(max(abs(g.x - w.x), abs(g.y - w.y)))
                  + "; align point p2 at " + r(p2.x) + ", " + r(p2.y))


# P6-44: justified TEXT (72 = 1/2/4 or 73 != 0) under near +-Z inserts. For every alignment except LEFT, ALIGNED and
# FIT, ezdxf's Text.get_placement() returns the group 11 point (align_point) as the placement point p1 (falling back to
# the group 10 point when group 11 is absent). DxfReader keeps the group 10 point with the z of group 30 for TEXT (a
# documented limit: DxfText has no justification member, so its Insertion stays the left baseline start that, with
# Rotation and Height, describes the text). Block JB (base (40, -30, 0)) holds TEXT TA (72 = 1, +Z), TB (72 = 2, exact
# -Z OCS), TC (72 = 4, its own near +Z OCS (1e-7, -1e-7, 1)), TD (72 = 0, 73 = 1, +Z), TE (72 = 1, 73 = 3, its own
# near -Z OCS (-1e-7, 2e-7, -1)), TF (72 = 2, 73 = 2, exact -Z) with 31 != 30 for TA/TB/TC/TE, and TG (72 = 1, no group
# 11, so ezdxf's p1 is the group 10 point). Model space: K1 (JB, near +Z, scale 1.2, elevation 700, rotation 25) and K2
# (JB, near -Z, scale 0.9, Z scale -1.5, rotation -40): the same placements as P6-40's J1/J2. Printed per leaf: the
# transformed group 10 point (what DxfExploder must give) and ezdxf's transformed placement point p1, both from the
# composed Insert.matrix44() applied to the point taken to block WCS by the leaf's own OCS, cross-checked within 1e-9
# against a copy transformed with its own transform(m) (insert, and get_placement() p1) and against virtual_entities().
JUST_BLOCK_BASE = (40.0, -30.0, 0.0)
# name, insert (10/20/30), align point (11/21/31) or None, halign (72), valign (73), extrusion
JUST_LEAVES = [
    ("TA", (50.0, 60.0, 1300.0), (150.0, 60.0, 1700.0), 1, 0, (0.0, 0.0, 1.0)),
    ("TB", (120.0, -80.0, 400.0), (220.0, -80.0, 900.0), 2, 0, (0.0, 0.0, -1.0)),
    ("TC", (-200.0, 150.0, 600.0), (-100.0, 180.0, 1100.0), 4, 0, (1e-7, -1e-7, 1.0)),
    ("TD", (300.0, 300.0, 1000.0), (300.0, 420.0, 1000.0), 0, 1, (0.0, 0.0, 1.0)),
    ("TE", (-150.0, 250.0, 800.0), (-60.0, 250.0, 1200.0), 1, 3, (-1e-7, 2e-7, -1.0)),
    ("TF", (250.0, -150.0, 500.0), (330.0, -110.0, 500.0), 2, 2, (0.0, 0.0, -1.0)),
    ("TG", (400.0, 100.0, 2100.0), None, 1, 0, (0.0, 0.0, 1.0)),
]
JUST_CASES = [
    ("K1", (50000.0, 20000.0, 700.0), 25.0, 1.2, 1.2, 1.0, (3e-7, -2e-7, 1.0)),
    ("K2", (-40000.0, 30000.0, -300.0), -40.0, 0.9, 0.9, -1.5, (-2e-7, 1e-7, -1.0)),
]


def build_justified():
    doc = ezdxf.new("R2018")
    doc.header["$INSUNITS"] = 4
    jb = doc.blocks.new("JB", base_point=JUST_BLOCK_BASE)
    made = {}
    for name, ins, align, h, v, ext in JUST_LEAVES:
        attribs = {"insert": ins, "halign": h, "valign": v, "height": 100.0, "extrusion": ext}
        if align is not None:
            attribs["align_point"] = align
        e = jb.add_text(name, dxfattribs=attribs)
        assert e.get_placement()[0].name not in ("LEFT", "ALIGNED", "FIT"), (name, e.get_placement())
        assert e.dxf.hasattr("align_point") == (align is not None), name
        made[name] = e
    msp = doc.modelspace()
    top = {}
    for name, ins, rot, sx, sy, sz, ext in JUST_CASES:
        top[name] = msp.add_blockref("JB", ins, dxfattribs={"rotation": rot, "xscale": sx, "yscale": sy, "zscale": sz, "extrusion": ext})
    return made, top


def placement_wcs(e):
    # ezdxf's placement point p1 of a TEXT in WCS (its own OCS).
    return e.ocs().to_wcs(Vec3(e.get_placement()[1]))


def justify_main():
    print("// P6-44 justified TEXT points: (path, handle, group 10 x, y, placement p1 x, y)")
    made, top = build_justified()
    for name, *_ in JUST_CASES:
        m = top[name].matrix44()
        virtual = {v.dxf.text: v for v in top[name].virtual_entities() if v.dxftype() == "TEXT"}
        for handle, *_ in JUST_LEAVES:
            e = made[handle]
            g = m.transform(follow_point(e, "insert"))
            p = m.transform(placement_wcs(e))
            c = e.copy()
            c.transform(m)
            assert (follow_point(c, "insert") - g).magnitude <= 1e-9, (name, handle)
            assert (placement_wcs(c) - p).magnitude <= 1e-9, (name, handle)
            v = virtual[handle]
            assert (follow_point(v, "insert") - g).magnitude <= 1e-9, (name, handle)
            assert (placement_wcs(v) - p).magnitude <= 1e-9, (name, handle)
            print('("' + name + '", "' + handle + '", ' + r(g.x) + ", " + r(g.y) + ", " + r(p.x) + ", " + r(p.y) + "),")
            print("//   " + name + " " + handle + " group 10 vs placement p1: " + r(max(abs(g.x - p.x), abs(g.y - p.y))))


# P6-44/P6-46: --check compares the constant rows the generator prints with the committed
# tests/cad/MinsertEzdxfTests.cs, both ways. A printed line that starts with "(" is a generated row; a committed row is
# a row of one of the C# tables in TABLES (one tuple per line between "Name =" and "};") or a label TEXT of Drawing()
# (Text("Ln", x, y, ...)). Two rows match when their quoted names (follow_main's point labels in LABELS dropped, since
# FollowPoints has no label column; NESTED spells a printed top insert as the C# insert path) and their full number
# lists (the trailing // comment dropped) are equal, each number equal as a double (Python repr and C# "R" both
# round-trip, so equality is exact). A label TEXT matches the generated row LABEL_ROWS names for its handle (P6-48; a
# decoy row DECOYS, whose numbers are exactly x, y, or a placed cell, whose centre comes first), so a label moved onto
# another cell's centre fails; every LABEL_ROWS handle must have its label TEXT. Every generated row must be
# committed (except the rows printed for the record, listed in UNUSED with the reason, which must not be), every
# committed row must be produced by the generator ("not produced by the generator"), and no committed row may appear
# twice; then planted copies of the C# file (an extra row, a row with one number more, a renamed row, a moved label)
# must each fail. Any failure exits with status 1. CI runs it (P6-46, ifc job; the reason is in the test header).
# P6-48: the output ends with a "differing rows: " line naming every row a failure is about ("none" when equal; CI's
# run with the wheel's C extensions annotates it) and a summary line with ezdxf's C-extension state and numpy's version.
LABELS = {"insert", "end", "start", "center", "mid", "v0", "v1", "v2", "v3"}
DECOYS = {"T1", "T2[0,0]", "T3[1,1]"}
# P6-48: the generated row each label TEXT of Drawing() sits on, by handle: L1-L4 at placed cell centres, L5-L7 decoys.
LABEL_ROWS = {"L1": "R1[1,2]", "L2": "S1[2,0]", "L3": "M1[1,1]", "L4": "N1[0,1]", "L5": "T1", "L6": "T2[0,0]", "L7": "T3[1,1]"}
# Not committed: the tilted T2/T3 cells other than the decoy label positions, and the heavy POLYLINE LH (not modelled
# by DxfReader; MinsertEzdxfTests pins that nothing of it is placed).
UNUSED = {"T2[0,1]", "T2[1,0]", "T2[1,1]", "T3[0,0]", "T3[0,1]", "T3[1,0]", "LH"}
# follow_main prints the top insert J3 for the ATTRIBs of the nested INSERT JI in J3's block JHOLD; FollowPoints writes
# the insert path J3>JI.
NESTED = {"J3": "J3>JI"}
TABLES = ("Cells", "Axes", "BulgeOutlines", "ChainOutlines", "ZOutlines", "LeafPoints", "FollowPoints", "AlignedPoints", "JustifiedPoints")
_NUMBER = re.compile(r"(?<![\w.])-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?(?![\w.])")
_QUOTED = re.compile(r'"([^"]*)"')
_TABLE = re.compile(r"^\s*private static readonly .*\b(\w+) =\s*$")
_LABEL = re.compile(r'^\s*\+ Text\("(L\d+)", ')


def _row(line, labels=True):
    body = line.split("//", 1)[0]
    names = tuple(NESTED.get(q, q) for q in _QUOTED.findall(body) if labels or q not in LABELS)
    numbers = tuple(float(n) for n in _NUMBER.findall(_QUOTED.sub("", body)))
    return names, numbers


def _generated_rows():
    rows = []
    for section in (main, bulge_main, chain_main, z_main, leaf_main, follow_main, align_main, justify_main):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            section()
        for line in out.getvalue().splitlines():
            names, numbers = _row(line, labels=section is not follow_main)
            if line.startswith("(") and numbers:
                rows.append((line, names, numbers))
    return rows


def _committed_rows(source):
    # (kind, where, names, numbers): kind "row" for a table row, "label" for a label TEXT; plus the tables not found.
    rows, found, table = [], set(), None
    for k, line in enumerate(source.splitlines(), 1):
        m = _TABLE.match(line)
        if m and m.group(1) in TABLES:
            table = m.group(1)
            found.add(table)
            continue
        if table is not None:
            if line.strip() == "};":
                table = None
            elif line.strip().startswith("("):
                rows.append(("row", table + " line " + str(k)) + _row(line))
            continue
        if _LABEL.match(line):
            rows.append(("label", "Drawing() line " + str(k)) + _row(line))
    return rows, [t for t in TABLES if t not in found]


def _name(names, numbers):
    # P6-50: an Axes row has no quoted name; it is named by its normal (its first three numbers).
    if names:
        return " ".join(names)
    return "axes normal (" + ", ".join(repr(v) for v in numbers[:3]) + ")"


# P6-50: the rows that differ in the last bits when ezdxf runs with its wheel's C extensions (CI, numpy 2.5.3); the
# constants are the pure-Python values. Under the C extensions these rows may differ; any other differing row fails.
C_EXT_DRIFT = ("C1>NB", "X1>XF", "Z1>ZA", "W1>CL")


def _drift_failures(differing):
    new = [d for d in differing if d not in C_EXT_DRIFT]
    return ["new C-extension drift rows: " + ", ".join(new) + " (C_EXT_DRIFT " + ", ".join(C_EXT_DRIFT) + ")"] if new else []


def _compare(generated, committed):
    # (failures, rows matched, unused rows, the names of the rows the failures are about, in order)
    rows, missing = committed
    failures = ["table not found: " + t for t in missing]
    differing = ["table " + t for t in missing]
    keys = {}
    for kind, where, names, numbers in rows:
        if kind == "row":
            keys.setdefault((names, numbers), []).append(where)
    for (names, numbers), w in keys.items():
        if len(w) > 1:
            failures.append("committed twice: " + ", ".join(w))
            differing.append(_name(names, numbers))
    labels = [(names[0], numbers) for kind, _, names, numbers in rows if kind == "label"]
    handles = [h for h, _ in labels]
    for h in LABEL_ROWS:
        if h not in handles:
            failures.append("label TEXT not found: " + h + " (LABEL_ROWS " + LABEL_ROWS[h] + ")")
            differing.append(h)
    produced = set()
    matched = unused = 0
    for line, names, numbers in generated:
        if names and names[0] in DECOYS:
            found = any(LABEL_ROWS.get(h) == names[0] and n == numbers for h, n in labels)
        else:
            found = (names, numbers) in keys
            produced.add((names, numbers))
        if any(name in UNUSED for name in names):
            unused += 1
            if found or any(n == numbers for _, n in labels):
                failures.append("listed as unused but committed: " + line)
                differing.append(_name(names, numbers))
        elif found:
            matched += 1
        else:
            failures.append("not committed: " + line)
            differing.append(_name(names, numbers))
    for kind, where, names, numbers in rows:
        if kind == "row":
            ok = (names, numbers) in produced
        else:
            target = LABEL_ROWS.get(names[0])
            ok = len(numbers) == 2 and handles.count(names[0]) == 1 and any(gnames[0] == target and n[:2] == numbers for _, gnames, n in generated if gnames)
        if not ok:
            failures.append("not produced by the generator: " + where + " " + repr(names) + " " + repr(numbers))
            differing.append(names[0] if kind == "label" else _name(names, numbers))
    return failures, matched, unused, list(dict.fromkeys(differing))


def _self_tests(generated, source):
    # Planted copies of the C# file that the check must fail, each with the failure it must report.
    plants = [
        ("an extra JustifiedPoints row", '        ("K2", "TG", ', '        ("K9", "TZ", 1.0, 2.0, 3.0, 4.0),\n        ("K2", "TG", ', "not produced by the generator: JustifiedPoints"),
        ("a Cells row with one number more", "-124.99999999999977, 3948.5571585149864 }),", "-124.99999999999977, 3948.5571585149864, 0.0 }),", "not produced by the generator: Cells"),
        ("an Axes row with its last number dropped", "new[] { -0.8, 0.0, 0.6 }),", "new[] { -0.8, 0.0 }),", "not committed: (0.6, 0.0, 0.8)", "axes normal (0.6, 0.0, 0.8)"),
        ("a renamed JustifiedPoints row", '        ("K1", "TA", ', '        ("K2", "TA", ', 'not committed: ("K1", "TA"'),
        ("a moved decoy label", 'Text("L5", -40600.0, ', 'Text("L5", -40600.5, ', 'not committed: ("T1"'),
        ("a LeafPoints row with a swapped label", '        ("Q1", "LA", "start", ', '        ("Q1", "LA", "end", ', 'not committed: ("Q1", "LA", "start"'),
        ("a duplicated JustifiedPoints row", '        ("K1", "TG", ', '        ("K1", "TG", 50325.59735516724, 20323.954543849297, 50325.59735516724, 20323.954543849297),\n        ("K1", "TG", ', "committed twice: JustifiedPoints"),
        # P6-48: a table renamed away, a label moved onto another placed cell's centre (R1[1,1]; a loose x,y match
        # against any cell accepted it), and a label whose handle LABEL_ROWS does not map.
        ("a missing Cells table", "double[] Corners)[] Cells =\n", "double[] Corners)[] CellsGone =\n", "table not found: Cells"),
        ("a label moved to another cell's centre", 'Text("L1", 9329.804845413264, 11572.354790610807, ', 'Text("L1", 4999.677826491072, 9072.35479061081, ', "not produced by the generator: Drawing() line"),
        ("a label with no LABEL_ROWS entry", 'Text("L2", ', 'Text("L9", ', "not produced by the generator: Drawing() line"),
        # P6-50: a row outside C_EXT_DRIFT that differs is reported by the C-extension drift check (as in CI's run with
        # the C extensions), named in the failure.
        ("a new C-extension drift row", '("K2", "TG", 39676.59614009593, ', '("K2", "TG", 39676.59614009594, ', "new C-extension drift rows: K2 TG ", "K2 TG", True),
    ]
    failures = []
    for label, old, new, expected, *extra in plants:
        name = extra[0] if extra else None
        drift = len(extra) > 1 and extra[1]
        if source.count(old) != 1:
            failures.append("self-test " + label + ": the planted text is not found exactly once: " + old)
            continue
        planted, _, _, differing = _compare(generated, _committed_rows(source.replace(old, new)))
        if drift:
            planted = _drift_failures(differing + list(C_EXT_DRIFT))
        caught = any(f.startswith(expected) for f in planted) and (name is None or name in differing)
        print("self-test planted " + label + ": " + ("caught" if caught else "NOT caught") + " (" + str(len(planted)) + " failures)")
        if not caught:
            failures.append("self-test " + label + ": not caught as " + expected + "; got " + " | ".join(planted))
    return failures


def check_main(cs_path):
    import numpy  # an ezdxf dependency; its version is printed with the result (P6-48)

    generated = _generated_rows()
    source = open(cs_path, encoding="utf-8").read()
    committed = _committed_rows(source)
    failures, matched, unused, differing = _compare(generated, committed)
    c_ext = bool(ezdxf.options.use_c_ext)
    expected = 0
    if c_ext:
        # P6-50: under the C extensions the C_EXT_DRIFT rows may differ: the row differences are printed but not
        # counted, and any differing row outside C_EXT_DRIFT fails, named ("new C-extension drift rows: ").
        for failure in failures:
            print("with the C extensions: " + failure)
        expected = len(failures)
        failures = _drift_failures(differing)
    failures += _self_tests(generated, source)
    for failure in failures:
        print(failure)
    print("differing rows: " + (", ".join(differing) if differing else "none"))
    if c_ext:
        print("C-extension drift: pinned " + ", ".join(C_EXT_DRIFT) + "; new: " + (", ".join(d for d in differing if d not in C_EXT_DRIFT) or "none")
              + "; no longer drifting: " + (", ".join(d for d in C_EXT_DRIFT if d not in differing) or "none"))
    print("ezdxf " + ezdxf.__version__ + " (C extensions used: " + str(c_ext) + "), numpy " + numpy.__version__ + ": " + str(matched) + " constant rows equal the committed ones, " + str(unused) + " unused, "
          + str(len(committed[0])) + " committed rows all produced unless listed, " + str(len(failures)) + " failures"
          + (" (" + str(expected) + " row-difference lines under the C extensions judged by C_EXT_DRIFT, not counted)" if c_ext else ""))
    return 1 if failures else 0


if __name__ == "__main__":
    if sys.argv[1:2] == ["--check"]:
        sys.exit(check_main(sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "MinsertEzdxfTests.cs")))
    main()
    bulge_main()
    chain_main()
    z_main()
    leaf_main()
    follow_main()
    align_main()
    justify_main()
