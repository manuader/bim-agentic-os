#!/usr/bin/env python3
"""Deterministic generator for tests/cad/fixtures/brief-plan.pdf (P6-15), the vector PDF twin of brief-plan.dxf.

Python 3 standard library only; no clock, no randomness, no locale: the output bytes depend only on this file.
Usage: python3 brief-plan-pdf.py [output.pdf]   (default: brief-plan.pdf next to this script)

Mapping from the DXF (millimetres, $INSUNITS 4) to the page: x_page = x_mm / 20 + 100, y_page = y_mm / 20 + 150, so one
page unit is 20 mm (--metres-per-unit 0.02, or --calibrate 100,150,300,150,4000: the bottom edge of the first room is
4000 mm long). Every closed LWPOLYLINE becomes one closed subpath (m, l, l, l, h, S) with the same vertex order, every
TEXT/MTEXT one string shown with Tj at its insertion point in document order, the DXF height 200 mm as font size 10.
The MTEXT "{\\fArial|b1;COCINA}\\P9,5 m\\U+00B2" is shown as its plain text "COCINA\\n9,5 m\\xb2". Strings are Latin-1
(WinAnsi for these characters), which is how the reader decodes strings without a byte order mark.
"""
import os
import sys

SCALE_MM = 20
OFFSET_X = 100
OFFSET_Y = 150
FONT_SIZE = 10  # 200 mm DXF text height / 20 mm per unit

# Closed LWPOLYLINEs of brief-plan.dxf (handles 100..107), millimetres.
REGIONS = [
    [(0, 0), (4000, 0), (4000, 3000), (0, 3000)],
    [(4000, 0), (7000, 0), (7000, 3000), (4000, 3000)],
    [(7000, 0), (9000, 0), (9000, 2000), (7000, 2000)],
    [(0, 3000), (6000, 3000), (6000, 8000), (0, 8000)],
    [(6000, 3000), (9000, 3000), (9000, 8000), (6000, 8000)],
    [(9000, 0), (11000, 0), (11000, 2000), (9000, 2000)],
    [(11000, 0), (13000, 0), (13000, 3000), (11000, 3000)],
    [(2000, 2000), (2300, 2000), (2300, 2300), (2000, 2300)],
]

# TEXT/MTEXT of brief-plan.dxf (handles 200..215): insertion point in millimetres, height in millimetres, plain text.
TEXTS = [
    (1000, 1800, 200, "DORMITORIO 1"),
    (1000, 1300, 200, "12,00 m²"),
    (1000, 1800, 200, "DORMITORIO 1"),
    (5000, 1500, 200, "COCINA\n9,5 m²"),
    (7500, 1500, 200, "BAÑO"),
    (7500, 1000, 200, "Sup. 6.50 m2"),
    (1000, 6000, 200, "Living room"),
    (1000, 5500, 200, "30 m2"),
    (9500, 1500, 200, "Baño"),
    (9500, 1000, 200, "4,00 m²"),
    (15000, 0, 200, "Lavadero"),
    (15000, -500, 200, "3,2 m²"),
    (11500, 1500, 200, "Hall"),
    (20000, 20000, 200, "25,0 m²"),
    (0, -2000, 400, "PLANTA BAJA"),
    (0, -2600, 200, "ESC 1:100"),
]


def page_point(x, y):
    px, py = x / SCALE_MM + OFFSET_X, y / SCALE_MM + OFFSET_Y
    assert px == int(px) and py == int(py), (x, y)
    return int(px), int(py)


def pdf_string(text):
    out = bytearray(b"(")
    for byte in text.encode("latin-1"):
        if byte in (0x28, 0x29, 0x5C):
            out += b"\\" + bytes([byte])
        elif byte == 0x0A:
            out += b"\\n"
        elif byte < 0x20 or byte > 0x7E:
            out += b"\\%03o" % byte
        else:
            out.append(byte)
    out += b")"
    return bytes(out)


def content():
    lines = [b"1 w"]
    for region in REGIONS:
        points = [page_point(x, y) for x, y in region]
        lines.append(b"%d %d m" % points[0])
        lines.extend(b"%d %d l" % p for p in points[1:])
        lines.append(b"h")
        lines.append(b"S")
    for x, y, height, text in TEXTS:
        px, py = page_point(x, y)
        size = height * FONT_SIZE // 200
        lines.append(b"BT /F1 %d Tf 1 0 0 1 %d %d Tm %s Tj ET" % (size, px, py, pdf_string(text)))
    return b"\n".join(lines) + b"\n"


def build():
    stream = content()
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1200 1200] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        b"<< /Length %d >>\nstream\n" % len(stream) + stream + b"endstream",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
    ]
    out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
    offsets = []
    for number, body in enumerate(objects, start=1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % number + body + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n" % (len(objects) + 1)
    out += b"0000000000 65535 f \n"
    for offset in offsets:
        out += b"%010d 00000 n \n" % offset
    out += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objects) + 1, xref)
    return bytes(out)


def main(argv):
    target = argv[1] if len(argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "brief-plan.pdf")
    with open(target, "wb") as handle:
        handle.write(build())
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
