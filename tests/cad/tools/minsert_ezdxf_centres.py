"""Offline generator of the ezdxf cross-check constants in tests/cad/MinsertEzdxfTests.cs (P6-30).

Not run in CI. Needs ezdxf (MIT, https://github.com/mozman/ezdxf); the constants in the test were produced with
ezdxf 1.4.2 (tag v1.4.2, commit 8240df5793daf2fd7db95d118a78e17ec92043ec) on Python 3 with numpy 2.5.3:

    python3 tests/cad/tools/minsert_ezdxf_centres.py

It builds, with ezdxf's own API, the block RECT and the INSERT/MINSERT entities that MinsertEzdxfTests.Drawing()
writes by hand (same parameters, listed in CASES), and prints, as C# tuples:
  * for every MINSERT cell (ezdxf Insert.multi_insert(), row-major) the four WCS corners of the cell's RECT outline
    (Insert.virtual_entities() then ezdxf.path.make_path, so the OCS of mirrored and near-Z inserts is applied by ezdxf)
    and their mean (the cell centre), projected to world XY;
  * the arbitrary-axis OCS axes ezdxf computes (ezdxf.math.OCS) for the extrusion normals used in the test;
  * for the tilted inserts (non-axis extrusion, which DxfReader reports as ocs-non-planar instead of projecting), the
    world XY centre the projection reading would give, used as decoy label positions.
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


if __name__ == "__main__":
    main()
