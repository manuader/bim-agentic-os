#!/usr/bin/env python3
"""Deterministic generator for tests/cad/fixtures/tounicode.pdf (P6-16): room labels shown the way CAD exporters write
them, with glyph codes that only a ToUnicode CMap turns back into text.

Python 3 standard library only; no clock, no randomness, no locale, no compression: the output bytes depend only on
this file. Usage: python3 tounicode-pdf.py [output.pdf]   (default: tounicode.pdf next to this script)

Three rooms side by side, one page unit = 20 mm (--metres-per-unit 0.02): Dormitorio 1 (4 m x 3 m), Baño (2 m x 2 m)
and Cocina (3 m x 3 m), each a closed subpath (m, l, l, l, h, S) holding a label and an area text.
- /F1 is a Type0 font with /Encoding /Identity-H over a CIDFontType2 descendant (as AutoCAD, Revit and most PDF
  printer drivers write subset TrueType fonts): every glyph is a two-byte CID assigned in code-point order from 3, shown
  as hex strings with Tj and, for "Baño", with TJ and small kerning. Its ToUnicode CMap (codespace <0000> <FFFF>) uses
  bfrange in the incrementing form for runs of consecutive CIDs that map to consecutive code points and bfchar for the
  rest.
- /F2 is a simple TrueType subset font whose codes are 1, 2, 3, ... in code-point order (so the bytes read as Latin-1
  are control characters). Its ToUnicode CMap (codespace <00> <FF>) uses a bfrange in the array form for codes 1..6,
  bfchar for codes 7 and 8, an incrementing bfrange for codes 9..11 and bfchar for code 12. "Cocina" and "9,00 m²" use it.
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
    ("F1", 120, 240, "Tj", "Dormitorio 1"),
    ("F1", 120, 215, "Tj", "12,00 m²"),
    ("F1", 320, 210, "TJ", "Baño"),
    ("F1", 320, 190, "Tj", "4,00 m²"),
    ("F2", 420, 240, "Tj", "Cocina"),
    ("F2", 420, 215, "Tj", "9,00 m²"),
]


def codes(font):
    chars = sorted({c for f, _, _, _, text in TEXTS if f == font for c in text})
    start = 3 if font == "F1" else 1
    return {c: start + i for i, c in enumerate(chars)}


F1 = codes("F1")
F2 = codes("F2")


def utf16(c):
    return c.encode("utf-16-be").hex().upper()


def cmap(body, codespace, name):
    return (
        b"/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n"
        b"/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n"
        b"/CMapName /" + name + b" def\n/CMapType 2 def\n"
        b"1 begincodespacerange\n" + codespace + b"\nendcodespacerange\n"
        + body
        + b"endcmap\nCMapName currentdict /CMapResource defineresource pop\nend\nend\n"
    )


def f1_cmap():
    # Group runs of consecutive CIDs with consecutive code points.
    items = sorted((cid, c) for c, cid in F1.items())
    runs = []
    for cid, c in items:
        if runs and runs[-1][-1][0] == cid - 1 and ord(runs[-1][-1][1]) == ord(c) - 1:
            runs[-1].append((cid, c))
        else:
            runs.append([(cid, c)])
    chars = [r[0] for r in runs if len(r) == 1]
    ranges = [r for r in runs if len(r) > 1]
    body = b"%d beginbfchar\n" % len(chars)
    body += b"".join(b"<%04X> <%s>\n" % (cid, utf16(c).encode()) for cid, c in chars)
    body += b"endbfchar\n%d beginbfrange\n" % len(ranges)
    body += b"".join(b"<%04X> <%04X> <%s>\n" % (r[0][0], r[-1][0], utf16(r[0][1]).encode()) for r in ranges)
    body += b"endbfrange\n"
    return cmap(body, b"<0000> <FFFF>", b"Adobe-Identity-UCS")


def f2_cmap():
    by_code = {code: c for c, code in F2.items()}
    assert sorted(by_code) == list(range(1, 13)), sorted(by_code)
    body = b"1 beginbfrange\n<01> <06> [" + b" ".join(b"<%s>" % utf16(by_code[k]).encode() for k in range(1, 7)) + b"]\nendbfrange\n"
    body += b"2 beginbfchar\n" + b"".join(b"<%02X> <%s>\n" % (k, utf16(by_code[k]).encode()) for k in (7, 8)) + b"endbfchar\n"
    for k in range(9, 11):
        assert ord(by_code[k + 1]) == ord(by_code[k]) + 1, (k, by_code[k], by_code[k + 1])
    body += b"1 beginbfrange\n<09> <0B> <%s>\nendbfrange\n" % utf16(by_code[9]).encode()
    body += b"1 beginbfchar\n<0C> <%s>\nendbfchar\n" % utf16(by_code[12]).encode()
    return cmap(body, b"<00> <FF>", b"F2-UCS")


def hex_string(text):
    return b"<" + b"".join(b"%04X" % F1[c] for c in text) + b">"


def literal(text):
    out = bytearray(b"(")
    for c in text:
        out += b"\\%03o" % F2[c]
    return bytes(out + b")")


def show(font, op, text):
    if font == "F2":
        return literal(text) + b" Tj"
    if op == "TJ":
        half = len(text) // 2
        return b"[" + hex_string(text[:half]) + b" -30 " + hex_string(text[half:]) + b"] TJ"
    return hex_string(text) + b" Tj"


def content():
    lines = [b"1 w"]
    for x0, y0, x1, y1 in REGIONS:
        lines += [b"%d %d m" % (x0, y0), b"%d %d l" % (x1, y0), b"%d %d l" % (x1, y1), b"%d %d l" % (x0, y1), b"h", b"S"]
    for font, x, y, op, text in TEXTS:
        lines.append(b"BT /%s %d Tf 1 0 0 1 %d %d Tm %s ET" % (font.encode(), FONT_SIZE, x, y, show(font, op, text)))
    return b"\n".join(lines) + b"\n"


def stream(dictionary, data):
    return b"<< " + dictionary + (b" " if dictionary else b"") + b"/Length %d >>\nstream\n" % len(data) + data + b"endstream"


def build():
    objects = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 400] /Resources << /Font << /F1 5 0 R /F2 8 0 R >> >> /Contents 4 0 R >>",
        stream(b"", content()),
        b"<< /Type /Font /Subtype /Type0 /BaseFont /AAAAAB+ArialMT /Encoding /Identity-H /DescendantFonts [6 0 R] /ToUnicode 7 0 R >>",
        b"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /AAAAAB+ArialMT /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 556 /CIDToGIDMap /Identity >>",
        stream(b"", f1_cmap()),
        b"<< /Type /Font /Subtype /TrueType /BaseFont /AAAAAC+ArialMT /FirstChar 1 /LastChar 12 /ToUnicode 9 0 R >>",
        stream(b"", f2_cmap()),
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
    target = argv[1] if len(argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "tounicode.pdf")
    with open(target, "wb") as handle:
        handle.write(build())
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
