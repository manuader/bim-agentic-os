#!/usr/bin/env python3
"""Deterministic generator for tests/cad/fixtures/brief-plan.pdf (P6-15), the vector PDF twin of brief-plan.dxf.

Python 3 standard library only; no clock, no randomness, no locale: the output bytes depend only on this file.
Usage: python3 brief-plan-pdf.py [output.pdf] [--variant xobject-flate|nested-scaled|nested-scaled-regions]
       (the flag may also come first; default output: brief-plan.pdf, or the variant's file below, next to this script)

Mapping from the DXF (millimetres, $INSUNITS 4) to the page: x_page = x_mm / 20 + 100, y_page = y_mm / 20 + 150, so one
page unit is 20 mm (--metres-per-unit 0.02, or --calibrate 100,150,300,150,4000: the bottom edge of the first room is
4000 mm long). Every closed LWPOLYLINE becomes one closed subpath (m, l, l, l, h, S) with the same vertex order, every
TEXT/MTEXT one string shown with Tj at its insertion point in document order, the DXF height 200 mm as font size 10.
The MTEXT "{\\fArial|b1;COCINA}\\P9,5 m\\U+00B2" is shown as its plain text "COCINA\\n9,5 m\\xb2". Strings are Latin-1
(WinAnsi for these characters), which is how the reader decodes strings without a byte order mark.

Variant xobject-flate (P6-17, brief-plan-xobject.pdf): the same drawing as CAD exporters often write it. The page
content (object 4) holds the regions exactly as above and then "/Labels Do" (operator index 49), and every text sits in
the Form XObject /Labels (object 6) with /Matrix [1 0 0 1 100 150], so inside the form a text is shown at x_mm / 20,
y_mm / 20 and lands on the same page point as in brief-plan.pdf. Both streams are /FlateDecode. The zlib data is
written by the small fixed-Huffman deflate encoder below (greedy LZ77, nearest longest match) rather than by the zlib
module, whose compressed bytes depend on the zlib build (zlib, zlib-ng, Apple zlib); zlib is used only for the
Adler-32 checksum and to assert that the data inflates back to the content.

Variant nested-scaled (P6-19, brief-plan-nested.pdf): uncompressed streams. The page content (object 4) holds the
regions exactly as in brief-plan.pdf and then "/Outer Do" (operator index 49). The Form XObject /Outer (object 6,
/Matrix [2 0 0 2 100 150], a scaled form) holds "q /Labels Do Q" (the Do is its operator index 1), and every text sits
in the nested Form XObject /Labels (object 7, /Matrix [1 0 0 1 5 -5]) at x_mm / 40 - 5, y_mm / 40 + 5 with half the
font size (5, or 10 for the 400 mm title). The page maps a label point p to (p + (5, -5)) * 2 + (100, 150), the inner
matrix first as PDF requires, so every text lands on its brief-plan.pdf page point with the same text height; all
coordinates are multiples of 1/2, so the products are exact in binary floating point.
Variant nested-scaled-regions (P6-19, brief-plan-nested-regions.pdf): the same forms, but the regions are drawn by the
Form XObject /Rooms (object 8, /Matrix [2 0 0 2 100 150]) at x_mm / 40, y_mm / 40; the page content is
"1 w /Rooms Do /Outer Do" (the Do operators are its indices 1 and 2). A brief extractor sees these regions only when it
reads regions drawn through forms (BriefExtractOptions.IncludeBlockRegions).
"""
from fractions import Fraction
import os
import sys
import zlib

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


# Fixed-Huffman deflate (RFC 1951 section 3.2.6) in a zlib wrapper (RFC 1950).
LENGTH_BASE = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258]
LENGTH_EXTRA = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0]
DIST_BASE = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073,
             4097, 6145, 8193, 12289, 16385, 24577]
DIST_EXTRA = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13]


class BitWriter:
    def __init__(self):
        self.out = bytearray()
        self.acc = 0
        self.count = 0

    def bits(self, value, count):  # least significant bit first
        self.acc |= value << self.count
        self.count += count
        while self.count >= 8:
            self.out.append(self.acc & 0xFF)
            self.acc >>= 8
            self.count -= 8

    def code(self, value, count):  # a Huffman code, most significant bit first
        self.bits(int(format(value, "0%db" % count)[::-1], 2), count)

    def finish(self):
        if self.count:
            self.out.append(self.acc & 0xFF)
        return bytes(self.out)


def literal_length(writer, symbol):
    if symbol <= 143:
        writer.code(0x30 + symbol, 8)
    elif symbol <= 255:
        writer.code(0x190 + symbol - 144, 9)
    elif symbol <= 279:
        writer.code(symbol - 256, 7)
    else:
        writer.code(0xC0 + symbol - 280, 8)


def deflate(data):
    writer = BitWriter()
    writer.bits(1, 1)  # BFINAL
    writer.bits(1, 2)  # BTYPE 01: fixed Huffman codes
    position = 0
    while position < len(data):
        best_length, best_distance = 0, 0
        for distance in range(1, min(position, 32768) + 1):
            length = 0
            while length < 258 and position + length < len(data) and data[position + length - distance] == data[position + length]:
                length += 1
            if length > best_length:
                best_length, best_distance = length, distance
        if best_length < 3:
            literal_length(writer, data[position])
            position += 1
            continue
        index = max(i for i, base in enumerate(LENGTH_BASE) if base <= best_length)
        literal_length(writer, 257 + index)
        writer.bits(best_length - LENGTH_BASE[index], LENGTH_EXTRA[index])
        index = max(i for i, base in enumerate(DIST_BASE) if base <= best_distance)
        writer.code(index, 5)
        writer.bits(best_distance - DIST_BASE[index], DIST_EXTRA[index])
        position += best_length
    literal_length(writer, 256)
    packed = b"\x78\x01" + writer.finish() + zlib.adler32(data).to_bytes(4, "big")
    if zlib.decompress(packed) != data:
        raise SystemExit("deflate round trip failed: the encoded stream does not inflate back to the content")
    return packed


def stream_object(dictionary, data):
    return b"<< " + (dictionary + b" " if dictionary else b"") + b"/Length %d >>\nstream\n" % len(data) + data + b"\nendstream"


def variant_content():
    lines = [b"1 w"]
    for region in REGIONS:
        points = [page_point(x, y) for x, y in region]
        lines.append(b"%d %d m" % points[0])
        lines.extend(b"%d %d l" % p for p in points[1:])
        lines.append(b"h")
        lines.append(b"S")
    lines.append(b"/Labels Do")
    return b"\n".join(lines) + b"\n"


def labels_form():
    lines = []
    for x, y, height, text in TEXTS:
        px, py = page_point(x, y)
        size = height * FONT_SIZE // 200
        lines.append(b"BT /F1 %d Tf 1 0 0 1 %d %d Tm %s Tj ET" % (size, px - OFFSET_X, py - OFFSET_Y, pdf_string(text)))
    return b"\n".join(lines) + b"\n"


def pdf_number(value):
    value = Fraction(value)
    assert (value * 4).denominator == 1, value  # quarters at most: exact in binary and in a short decimal
    if value.denominator == 1:
        return b"%d" % value.numerator
    return ("%.2f" % float(value)).rstrip("0").encode("ascii")


NESTED_SCALE = 2  # /Outer and /Rooms /Matrix scale
INNER_SHIFT = (5, -5)  # /Labels /Matrix translation


def form_point(x, y):
    # A drawing point in millimetres as /Rooms coordinates (x_mm / 40, y_mm / 40); page = 2 * form + (100, 150).
    fx, fy = Fraction(x, SCALE_MM * NESTED_SCALE), Fraction(y, SCALE_MM * NESTED_SCALE)
    assert (fx * NESTED_SCALE + OFFSET_X, fy * NESTED_SCALE + OFFSET_Y) == tuple(Fraction(v) for v in page_point(x, y)), (x, y)
    return fx, fy


def nested_page_content(regions_in_form):
    lines = [b"1 w"]
    if regions_in_form:
        lines.append(b"/Rooms Do")
    else:
        lines.extend(region_lines(lambda x, y: tuple(Fraction(v) for v in page_point(x, y))))
    lines.append(b"/Outer Do")
    return b"\n".join(lines) + b"\n"


def region_lines(point):
    lines = []
    for region in REGIONS:
        points = [point(x, y) for x, y in region]
        lines.append(b"%s %s m" % (pdf_number(points[0][0]), pdf_number(points[0][1])))
        lines.extend(b"%s %s l" % (pdf_number(px), pdf_number(py)) for px, py in points[1:])
        lines.append(b"h")
        lines.append(b"S")
    return lines


def nested_labels_form():
    lines = []
    for x, y, height, text in TEXTS:
        fx, fy = form_point(x, y)
        ix, iy = fx - INNER_SHIFT[0], fy - INNER_SHIFT[1]
        size = Fraction(height * FONT_SIZE, 200 * NESTED_SCALE)
        lines.append(b"BT /F1 %s Tf 1 0 0 1 %s %s Tm %s Tj ET" % (pdf_number(size), pdf_number(ix), pdf_number(iy), pdf_string(text)))
    return b"\n".join(lines) + b"\n"


def build_nested(regions_in_form):
    font = b"/Font << /F1 5 0 R >>"
    xobjects = b"/XObject << /Rooms 8 0 R /Outer 6 0 R >>" if regions_in_form else b"/XObject << /Outer 6 0 R >>"
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1200 1200] /Resources << " + font + b" " + xobjects + b" >> /Contents 4 0 R >>",
        stream_object(b"", nested_page_content(regions_in_form)),
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        stream_object(b"/Type /XObject /Subtype /Form /BBox [-50 -75 550 525] /Matrix [%d 0 0 %d %d %d] /Resources << /XObject << /Labels 7 0 R >> >>"
                      % (NESTED_SCALE, NESTED_SCALE, OFFSET_X, OFFSET_Y), b"q\n/Labels Do\nQ\n"),
        stream_object(b"/Type /XObject /Subtype /Form /BBox [-55 -70 545 530] /Matrix [1 0 0 1 %d %d] /Resources << " % INNER_SHIFT + font + b" >>",
                      nested_labels_form()),
    ]
    if regions_in_form:
        objects.append(stream_object(b"/Type /XObject /Subtype /Form /BBox [-50 -75 550 525] /Matrix [%d 0 0 %d %d %d]" % (NESTED_SCALE, NESTED_SCALE, OFFSET_X, OFFSET_Y),
                                     b"1 w\n" + b"\n".join(region_lines(form_point)) + b"\n"))
    return serialize(objects)


def serialize(objects):
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


def build_variant():
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1200 1200] /Resources << /Font << /F1 5 0 R >> /XObject << /Labels 6 0 R >> >> /Contents 4 0 R >>",
        stream_object(b"/Filter /FlateDecode", deflate(variant_content())),
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        stream_object(b"/Type /XObject /Subtype /Form /BBox [-100 -150 1100 1050] /Matrix [1 0 0 1 %d %d] /Resources << /Font << /F1 5 0 R >> >> /Filter /FlateDecode" % (OFFSET_X, OFFSET_Y),
                      deflate(labels_form())),
    ]
    return serialize(objects)


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
    arguments = list(argv[1:])
    variant = None
    if "--variant" in arguments:
        at = arguments.index("--variant")
        variant = arguments[at + 1] if at + 1 < len(arguments) else ""
        del arguments[at:at + 2]
    variants = {
        None: ("brief-plan.pdf", build),
        "xobject-flate": ("brief-plan-xobject.pdf", build_variant),
        "nested-scaled": ("brief-plan-nested.pdf", lambda: build_nested(False)),
        "nested-scaled-regions": ("brief-plan-nested-regions.pdf", lambda: build_nested(True)),
    }
    if variant not in variants or len(arguments) > 1:
        sys.stderr.write("usage: brief-plan-pdf.py [--variant xobject-flate|nested-scaled|nested-scaled-regions] [output.pdf]\n")
        return 2
    name, writer = variants[variant]
    target = arguments[0] if arguments else os.path.join(os.path.dirname(os.path.abspath(__file__)), name)
    with open(target, "wb") as handle:
        handle.write(writer())
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
