#!/usr/bin/env python3
"""Deterministic generator for tests/cad/fixtures/differences.pdf (P6-18): room labels shown with simple fonts that
have no ToUnicode CMap, only an /Encoding dictionary with /BaseEncoding and /Differences glyph names, as CAD exporters
and PDF printer drivers write subset Type1 and TrueType fonts.

Python 3 standard library only; no clock, no randomness, no locale, no compression: the output bytes depend only on
this file. Usage: python3 differences-pdf.py [output.pdf]   (default: differences.pdf next to this script)

Three rooms side by side, one page unit = 20 mm (--metres-per-unit 0.02): Dormitorio 1 (4 m x 3 m), Baño (2 m x 2 m)
and Cocina (3 m x 3 m), each a closed subpath (m, l, l, l, h, S) holding a label and an area text.
- /F3 (TrueType subset, /Encoding << /BaseEncoding /StandardEncoding /Differences [1 /space /comma ...] >>) shows
  "Dormitorio 1" and "12,00 m²" with codes 1, 2, 3, ... assigned in code-point order, so the bytes read as Latin-1 are
  control characters; ² is the glyph name uni00B2.
- /F1 (Type1, indirect /Encoding object << /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [128 /ntilde
  /twosuperior] >>) shows "Baño" (Tj and, for the area, TJ with small kerning) with ñ at code 128, where WinAnsi and
  Latin-1 both have something else, and ² at code 129.
- /F2 (Type1, direct /Encoding << /BaseEncoding /MacRomanEncoding /Differences [1 /twosuperior] >>) shows "Cocina" and
  "9,00 m²" from the base encoding with ² at code 1.
"""
import os
import sys

FONT_SIZE = 10

# Rooms in page units: (x0, y0, x1, y1).
REGIONS = [
    (100, 150, 300, 300),
    (300, 150, 400, 250),
    (400, 150, 550, 300),
]

# (font, x, y, operator, text) in drawing order.
TEXTS = [
    ("F3", 120, 240, "Tj", "Dormitorio 1"),
    ("F3", 120, 215, "Tj", "12,00 m²"),
    ("F1", 320, 210, "Tj", "Baño"),
    ("F1", 320, 190, "TJ", "4,00 m²"),
    ("F2", 420, 240, "Tj", "Cocina"),
    ("F2", 420, 215, "Tj", "9,00 m²"),
]

NAMES = {" ": "space", ",": "comma", "0": "zero", "1": "one", "2": "two", "²": "uni00B2"}

F3_CHARS = sorted({c for f, _, _, _, text in TEXTS if f == "F3" for c in text})
F3 = {c: 1 + i for i, c in enumerate(F3_CHARS)}
F1 = {"ñ": 128, "²": 129}
F2 = {"²": 1}


def code(font, c):
    if font == "F3":
        return F3[c]
    special = F1 if font == "F1" else F2
    if c in special:
        return special[c]
    assert 0x20 <= ord(c) < 0x7F, (font, c)
    return ord(c)


def literal(font, text):
    out = bytearray(b"(")
    for c in text:
        k = code(font, c)
        out += bytes([k]) if 0x20 <= k < 0x7F and chr(k) not in "()\\" else b"\\%03o" % k
    return bytes(out + b")")


def show(font, op, text):
    if op == "TJ":
        half = len(text) // 2
        return b"[" + literal(font, text[:half]) + b" -30 " + literal(font, text[half:]) + b"] TJ"
    return literal(font, text) + b" Tj"


def content():
    lines = [b"1 w"]
    for x0, y0, x1, y1 in REGIONS:
        lines += [b"%d %d m" % (x0, y0), b"%d %d l" % (x1, y0), b"%d %d l" % (x1, y1), b"%d %d l" % (x0, y1), b"h", b"S"]
    for font, x, y, op, text in TEXTS:
        lines.append(b"BT /%s %d Tf 1 0 0 1 %d %d Tm %s ET" % (font.encode(), FONT_SIZE, x, y, show(font, op, text)))
    return b"\n".join(lines) + b"\n"


def f3_differences():
    names = [NAMES.get(c, c) for c in F3_CHARS]
    assert all(n.isalnum() for n in names), names
    return b"[1 " + b" ".join(b"/" + n.encode() for n in names) + b"]"


def stream(dictionary, data):
    return b"<< " + dictionary + (b" " if dictionary else b"") + b"/Length %d >>\nstream\n" % len(data) + data + b"endstream"


def build():
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 400] /Resources << /Font << /F1 5 0 R /F2 7 0 R /F3 8 0 R >> >> /Contents 4 0 R >>",
        stream(b"", content()),
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 32 /LastChar 129 /Encoding 6 0 R >>",
        b"<< /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [128 /ntilde /twosuperior] >>",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /BaseEncoding /MacRomanEncoding /Differences [1 /twosuperior] >> >>",
        b"<< /Type /Font /Subtype /TrueType /BaseFont /AAAAAD+ArialMT /FirstChar 1 /LastChar %d /Encoding << /Type /Encoding /BaseEncoding /StandardEncoding /Differences %s >> >>"
        % (len(F3_CHARS), f3_differences()),
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
    target = argv[1] if len(argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "differences.pdf")
    with open(target, "wb") as handle:
        handle.write(build())
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
