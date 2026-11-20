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


if __name__ == "__main__":
    main()
    bulge_main()
