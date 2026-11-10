""".3dm -> BIM-IR draft proposal through rhino3dm (MIT). No Rhino installation is needed or used.

Usage:
  python rhino_draft.py INPUT_3DM [--project-id UUID] [--wall-height M] [--wall-thickness M]
                        [--arc-segments N] [--metres-per-unit F]
  -> stdout one JSON draft (draft_version 1). Nothing is written anywhere: the draft is a proposal, not persisted.
Failures print one JSON object {"error": code, "detail": text} on stderr and exit 1.

Unlike bim_rhino.py import (which only reads files the P8-02 writer made and refuses anything else), this reads any
.3dm: objects on layers named for walls, spaces, levels, doors or windows become proposed BIM-IR elements with a
confidence, and every other object goes to a typed unmapped report. Proposed elements use only the existing BIM-IR element
shape (level [elevation]; wall [ax, ay, bx, by, height, thickness]; space [x, y, width, depth]; door/window
[ax, ay, bx, by, sill, height] with dependencies [level, host wall], as IntentCompiler and the IFC adapter write them);
no schema is added.
"""
import hashlib
import importlib.util
import json
import math
import pathlib
import sys
import uuid

import rhino3dm

_spec = importlib.util.spec_from_file_location('bim_rhino', pathlib.Path(__file__).resolve().with_name('bim_rhino.py'))
br = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(br)

RhinoAdapterError = br.RhinoAdapterError
DRAFT_VERSION = 1
ID_NAMESPACE = 'bim-agentic-os.rhino-draft/1'
TOL = br.POSITION_TOLERANCE
RANK = {'level': 0, 'wall': 1, 'door': 2, 'window': 3, 'space': 4}
OPENINGS = ('door', 'window')
# Layer name (case-insensitive, whole name) -> kind. The writer's names (Levels, Walls, Spaces) come first.
LAYER_KINDS = {
    'level': 'level', 'levels': 'level', 'nivel': 'level', 'niveles': 'level',
    'wall': 'wall', 'walls': 'wall', 'muro': 'wall', 'muros': 'wall',
    'space': 'space', 'spaces': 'space', 'room': 'space', 'rooms': 'space',
    'espacio': 'space', 'espacios': 'space', 'local': 'space', 'locales': 'space',
    'door': 'door', 'doors': 'door', 'puerta': 'door', 'puertas': 'door',
    'window': 'window', 'windows': 'window', 'ventana': 'window', 'ventanas': 'window',
}
# Confidence of each mapping before the layer-inherited factor.
CONFIDENCE = {
    'level-point': 0.9, 'level-synthesized': 0.6,
    'wall-axis-extrusion': 0.95, 'wall-footprint-extrusion': 0.85, 'wall-centreline': 0.5, 'wall-arc-chord': 0.3,
    'space-outline': 0.9, 'space-extrusion': 0.8,
    'opening-axis-extrusion': 0.9, 'opening-footprint-extrusion': 0.8,
}
INHERITED_FACTOR = 0.9
# An opening box off its host axis (but inside the host thickness) is moved onto the axis.
PROJECTED_FACTOR = 0.9
# Units: name -> ('div', d) metres = value / d (exact for decimal units, with Core's mm noise rule), or ('mul', f).
_UNITS = (('Millimeters', ('div', 1000.0)), ('Centimeters', ('div', 100.0)), ('Decimeters', ('div', 10.0)),
          ('Meters', ('div', 1.0)), ('Kilometers', ('mul', 1000.0)), ('Inches', ('mul', 0.0254)), ('Feet', ('mul', 0.3048)))
UNITS = [(getattr(rhino3dm.UnitSystem, name), name, scale) for name, scale in _UNITS if hasattr(rhino3dm.UnitSystem, name)]

DEFAULT_OPTIONS = {
    'project_id': None, 'wall_height': 2.7, 'wall_thickness': 0.2, 'arc_segments': 0, 'metres_per_unit': None,
    'max_file_bytes': 256 * 1024 * 1024, 'max_objects': 100_000, 'max_vertices': 10_000,
    'max_proposals': 100_000, 'max_unmapped': 10_000,
}
MAX_ARC_SEGMENTS = 64


class _Unmapped(Exception):
    def __init__(self, reason, detail):
        super().__init__(detail)
        self.reason = reason
        self.detail = detail


def _options(given):
    unknown = sorted(set(given) - set(DEFAULT_OPTIONS))
    if unknown:
        raise RhinoAdapterError('draft_options_invalid', f'unknown options {unknown}')
    options = {**DEFAULT_OPTIONS, **given}

    def number(name, minimum, maximum):
        value = options[name]
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or not minimum <= value <= maximum:
            raise RhinoAdapterError('draft_options_invalid', f'{name} must be a number in [{minimum}, {maximum}], got {value!r}')
        options[name] = float(value)

    def integer(name, minimum, maximum):
        value = options[name]
        if isinstance(value, bool) or not isinstance(value, int) or not minimum <= value <= maximum:
            raise RhinoAdapterError('draft_options_invalid', f'{name} must be an integer in [{minimum}, {maximum}], got {value!r}')

    number('wall_height', br.MIN_LENGTH, br.MAX_COORDINATE)
    number('wall_thickness', br.MIN_LENGTH, br.MAX_COORDINATE)
    integer('arc_segments', 0, MAX_ARC_SEGMENTS)
    if options['arc_segments'] == 1:
        raise RhinoAdapterError('draft_options_invalid', 'arc_segments must be 0 (do not approximate) or at least 2')
    if options['metres_per_unit'] is not None:
        number('metres_per_unit', 1e-9, 1e9)
    for name in ('max_file_bytes', 'max_objects', 'max_vertices', 'max_proposals', 'max_unmapped'):
        integer(name, 1, 2**62)
    if options['project_id'] is not None and (not isinstance(options['project_id'], str) or br.UUID_RE.match(options['project_id']) is None):
        raise RhinoAdapterError('draft_options_invalid', 'project_id must be a lowercase UUID string')
    return options


def _derived_id(*parts):
    return str(uuid.UUID(bytes=hashlib.sha256('\n'.join((ID_NAMESPACE,) + parts).encode('utf-8')).digest()[:16]))


def _layer_kind(path):
    """Kind of the nearest layer on the path (leaf first) whose name is a known kind name; (kind, inherited)."""
    if not path:
        return None, False
    segments = path.split('::')
    for depth, name in enumerate(reversed(segments)):
        kind = LAYER_KINDS.get(name.strip().casefold())
        if kind is not None:
            return kind, depth > 0
    return None, False


class _Converter:
    def __init__(self, mode, value):
        self.mode, self.value = mode, value

    @property
    def metres_per_unit(self):
        return 1.0 / self.value if self.mode == 'div' else self.value

    def m(self, v):
        if not math.isfinite(v):
            raise _Unmapped('degenerate', f'coordinate {v} is not finite')
        metres = br._metres(v, self.value) if self.mode == 'div' else v * self.value
        if not math.isfinite(metres):
            raise _Unmapped('degenerate', f'coordinate {v} is not finite in metres')
        return metres

    def xyz(self, point):
        return self.m(point.X), self.m(point.Y), self.m(point.Z)


def _vertices(curve, max_vertices):
    """Raw Point3d vertices of a polyline-like curve, or None for curves that are not polylines."""
    if isinstance(curve, rhino3dm.PolylineCurve):
        count = curve.PointCount
        if count > max_vertices:
            raise _Unmapped('too_many_vertices', f'{count} vertices > max_vertices {max_vertices}')
        return [curve.Point(i) for i in range(count)]
    if isinstance(curve, rhino3dm.LineCurve):
        return [curve.PointAtStart, curve.PointAtEnd]
    if isinstance(curve, rhino3dm.NurbsCurve):
        try:
            if curve.Degree != 1:
                return None
            points = curve.Points
            count = points.Count if hasattr(points, 'Count') else len(points)
            if count > max_vertices:
                raise _Unmapped('too_many_vertices', f'{count} vertices > max_vertices {max_vertices}')
            out = []
            for i in range(count):
                p = points[i]
                if abs(p.W - 1.0) > 1e-12:
                    return None
                out.append(rhino3dm.Point3d(p.X, p.Y, p.Z))
            return out
        except _Unmapped:
            raise
        except Exception:
            return None
    return None


def _closed(points):
    return len(points) >= 4 and math.dist(points[0], points[-1]) <= TOL


def _horizontal(points):
    zs = [p[2] for p in points]
    return max(zs) - min(zs) <= TOL


def _between(prev, cur, nxt):
    """cur lies on the segment prev..nxt (within TOL), so it is not a corner."""
    base = math.dist(prev, nxt)
    if base <= TOL:
        return False
    cross = (nxt[0] - prev[0]) * (cur[1] - prev[1]) - (nxt[1] - prev[1]) * (cur[0] - prev[0])
    dot = (cur[0] - prev[0]) * (nxt[0] - prev[0]) + (cur[1] - prev[1]) * (nxt[1] - prev[1])
    return abs(cross) / base <= TOL and 0 <= dot <= base * base


def _corners(points):
    """Corners (x, y) of a closed outline (first == last) after dropping repeated and collinear vertices.

    One stack pass (linear), then the seam where the ring closes is cleaned from both ends."""
    stack = []
    for point in (p[:2] for p in points[:-1]):
        if stack and math.dist(stack[-1], point) <= TOL:
            continue
        while len(stack) >= 2 and _between(stack[-2], stack[-1], point):
            stack.pop()
        stack.append(point)
    start = 0
    changed = True
    while changed and len(stack) - start >= 3:
        changed = False
        if math.dist(stack[-1], stack[start]) <= TOL:
            stack.pop(); changed = True
        elif _between(stack[-2], stack[-1], stack[start]):
            stack.pop(); changed = True
        elif _between(stack[-1], stack[start], stack[start + 1]):
            start += 1; changed = True
    return stack[start:]


def _rectangle(points):
    """The four corners of a closed rectangular outline, else _Unmapped(space_not_rectangle)."""
    ring = _corners(points)
    if len(ring) < 3:
        raise _Unmapped('degenerate', f'outline collapses to {len(ring)} distinct corners')
    if len(ring) == 4:
        sides = [math.dist(ring[i], ring[(i + 1) % 4]) for i in range(4)]
        turns = [(ring[(i + 1) % 4][0] - ring[i][0]) * (ring[(i + 2) % 4][1] - ring[(i + 1) % 4][1])
                 - (ring[(i + 1) % 4][1] - ring[i][1]) * (ring[(i + 2) % 4][0] - ring[(i + 1) % 4][0]) for i in range(4)]
        # Every corner must turn the same way, which rules out crossed (bow-tie) outlines with rectangle side lengths.
        convex = all(t > 0 for t in turns) or all(t < 0 for t in turns)
        if convex and min(sides) > TOL and abs(sides[0] - sides[2]) <= TOL and abs(sides[1] - sides[3]) <= TOL \
                and abs(math.dist(ring[0], ring[2]) - math.dist(ring[1], ring[3])) <= TOL:
            return ring
    raise _Unmapped('space_not_rectangle', f'outline has {len(ring)} corners and is not a rectangle')


def _in_range(values, what):
    if any(not math.isfinite(v) for v in values):
        raise _Unmapped('degenerate', f'{what} is not finite')
    if any(abs(v) > br.MAX_COORDINATE for v in values):
        raise _Unmapped('out_of_range', f'{what} lies beyond +/-{br.MAX_COORDINATE} m')


def _length(value, what):
    if not value >= br.MIN_LENGTH:
        raise _Unmapped('degenerate', f'{what} {value} m is below {br.MIN_LENGTH} m')


def _footprint(extrusion, conv, max_vertices):
    """(corners, base z, height) of a vertical extrusion of a horizontal outline (Rhino ExtrudeCrv style)."""
    start, end = conv.xyz(extrusion.PathStart), conv.xyz(extrusion.PathEnd)
    if extrusion.ProfileCount != 1:
        raise _Unmapped('wall_extrusion_unsupported', f'{extrusion.ProfileCount} profiles (holes are not supported)')
    profile = extrusion.Profile3d(0, 0.0)
    raw = _vertices(profile, max_vertices) if profile is not None else None
    if raw is None:
        raise _Unmapped('wall_extrusion_unsupported', f'profile is {type(profile).__name__}, not a polyline')
    points = [conv.xyz(p) for p in raw]
    if not _closed(points) or not _horizontal(points) or abs(points[0][2] - start[2]) > TOL:
        raise _Unmapped('wall_extrusion_unsupported', 'profile is not a closed horizontal outline at the path start')
    base, top = min(start[2], end[2]), max(start[2], end[2])
    return _rectangle(points), base, br._snap(top - base)


def _path_kind(extrusion, conv):
    start, end = conv.xyz(extrusion.PathStart), conv.xyz(extrusion.PathEnd)
    run, rise = math.hypot(end[0] - start[0], end[1] - start[1]), abs(end[2] - start[2])
    if rise <= TOL and run > TOL:
        return 'axis'
    if run <= TOL and rise > TOL:
        return 'vertical'
    return 'tilted'


def _axis_wall(extrusion, conv, max_vertices):
    """Writer-style wall: horizontal path = axis, vertical rectangular cross-section centred on it."""
    start, end = conv.xyz(extrusion.PathStart), conv.xyz(extrusion.PathEnd)
    if extrusion.ProfileCount != 1:
        raise _Unmapped('wall_extrusion_unsupported', f'{extrusion.ProfileCount} profiles (openings are not supported)')
    profile = extrusion.Profile3d(0, 0.0)
    raw = _vertices(profile, max_vertices) if profile is not None else None
    if raw is None:
        raise _Unmapped('wall_extrusion_unsupported', f'profile is {type(profile).__name__}, not a polyline')
    points = [conv.xyz(p) for p in raw]
    if len(points) != 5 or not _closed(points):
        raise _Unmapped('wall_extrusion_unsupported', 'profile is not a closed four-corner outline')
    corners, base = points[:4], start[2]
    zs = [c[2] for c in corners]
    bottom = [c for c in corners if abs(c[2] - base) <= TOL]
    top = [c for c in corners if abs(c[2] - max(zs)) <= TOL]
    if len(bottom) != 2 or len(top) != 2 or abs(min(zs) - base) > TOL:
        raise _Unmapped('wall_extrusion_unsupported', 'profile is not a rectangle standing on the axis')
    thickness = math.hypot(bottom[0][0] - bottom[1][0], bottom[0][1] - bottom[1][1])
    mid = ((bottom[0][0] + bottom[1][0]) / 2, (bottom[0][1] + bottom[1][1]) / 2)
    axis = (end[0] - start[0], end[1] - start[1])
    across = (bottom[0][0] - bottom[1][0], bottom[0][1] - bottom[1][1])
    if math.hypot(mid[0] - start[0], mid[1] - start[1]) > TOL or abs(axis[0] * across[0] + axis[1] * across[1]) > TOL * math.hypot(*axis):
        raise _Unmapped('wall_extrusion_unsupported', 'profile is not centred on and square to the axis')
    for t in top:
        if min(math.hypot(t[0] - b[0], t[1] - b[1]) for b in bottom) > TOL:
            raise _Unmapped('wall_extrusion_unsupported', 'profile sides are not vertical')
    return [start[0], start[1], end[0], end[1], br._snap(max(zs) - base), br._snap(thickness)], base


def _footprint_wall(extrusion, conv, max_vertices):
    try:
        ring, base, height = _footprint(extrusion, conv, max_vertices)
    except _Unmapped as error:
        if error.reason == 'space_not_rectangle':
            raise _Unmapped('wall_extrusion_unsupported', 'footprint is not a rectangle') from None
        raise
    a, b, c, d = ring
    e0, e1 = math.dist(a, b), math.dist(b, c)
    if abs(e0 - e1) <= TOL:
        raise _Unmapped('wall_footprint_ambiguous', f'square footprint {e0} m: the axis direction cannot be told')
    mid = lambda p, q: ((p[0] + q[0]) / 2, (p[1] + q[1]) / 2)
    if e0 > e1:
        s, e, thickness = mid(a, d), mid(b, c), e1
    else:
        s, e, thickness = mid(a, b), mid(d, c), e0
    return [s[0], s[1], e[0], e[1], height, br._snap(thickness)], base


ARC_SAMPLES = 32


def _maybe_arc(curve):
    """True for curves that may be circular arcs: ArcCurve, or a NurbsCurve of degree >= 2 (rhino3dm stores
    arcs and circles as rational NURBS)."""
    if isinstance(curve, rhino3dm.ArcCurve):
        return True
    try:
        return isinstance(curve, rhino3dm.NurbsCurve) and curve.Degree >= 2
    except Exception:
        return False


def _circular(curve, conv):
    """(centre, radius, start angle, signed sweep, z) when the curve is a horizontal circular arc, else None.

    Checked geometrically on ARC_SAMPLES + 1 points, so it does not depend on how the arc is stored."""
    domain = curve.Domain
    t0, t1 = domain.T0, domain.T1
    points = [conv.xyz(curve.PointAt(t0 + (t1 - t0) * i / ARC_SAMPLES)) for i in range(ARC_SAMPLES + 1)]
    if any(not math.isfinite(v) for p in points for v in p) or not _horizontal(points):
        return None
    (ax, ay, _), (bx, by, _), (cx, cy, _) = points[0], points[ARC_SAMPLES // 3], points[2 * ARC_SAMPLES // 3]
    det = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by))
    if abs(det) <= TOL * TOL:
        return None
    if not math.isfinite(det):
        return None
    ux = ((ax * ax + ay * ay) * (by - cy) + (bx * bx + by * by) * (cy - ay) + (cx * cx + cy * cy) * (ay - by)) / det
    uy = ((ax * ax + ay * ay) * (cx - bx) + (bx * bx + by * by) * (ax - cx) + (cx * cx + cy * cy) * (bx - ax)) / det
    radius = math.hypot(ax - ux, ay - uy)
    # NaN compares false, so finiteness is checked explicitly before the tolerance test.
    if not (math.isfinite(ux) and math.isfinite(uy) and math.isfinite(radius) and radius > TOL):
        return None
    if any(not math.isfinite(math.hypot(p[0] - ux, p[1] - uy)) for p in points) or any(abs(math.hypot(p[0] - ux, p[1] - uy) - radius) > max(TOL, 1e-9 * radius) for p in points):
        return None
    angles = [math.atan2(p[1] - uy, p[0] - ux) for p in points]
    sweep = 0.0
    for a, b in zip(angles, angles[1:]):
        step = b - a
        while step > math.pi:
            step -= 2 * math.pi
        while step <= -math.pi:
            step += 2 * math.pi
        sweep += step
    return (ux, uy), radius, angles[0], sweep, points[0][2]


def _fit(g, base, h, host_base):
    """How opening box g = [ax, ay, bx, by, height, thickness] at base sits on wall h = [ax, ay, bx, by, height, thickness]
    at host_base: None unless both ends lie inside the host thickness at the same signed offset from its axis (parallel),
    between its ends, and the box stands between the host base and top."""
    ax, ay, bx, by = h[:4]
    length = math.hypot(bx - ax, by - ay)
    ux, uy = (bx - ax) / length, (by - ay) / length
    s0 = (g[0] - ax) * ux + (g[1] - ay) * uy
    s1 = (g[2] - ax) * ux + (g[3] - ay) * uy
    d0 = (g[0] - ax) * uy - (g[1] - ay) * ux
    d1 = (g[2] - ax) * uy - (g[3] - ay) * ux
    half = h[5] / 2
    if abs(d0) > half + TOL or abs(d1) > half + TOL or abs(d0 - d1) > TOL:
        return None
    if min(s0, s1) < -TOL or max(s0, s1) > length + TOL:
        return None
    if base < host_base - TOL or base + g[4] > host_base + h[4] + TOL:
        return None
    return {'s0': s0, 's1': s1, 'offset': max(abs(d0), abs(d1)), 'a': (ax, ay), 'u': (ux, uy), 'length': length}


def _arc_chords(arc, segments):
    """segments + 1 points at equal angles along the arc (so all chords are equal), in metres."""
    (ux, uy), radius, start, sweep, z = arc
    return [(ux + radius * math.cos(start + sweep * i / segments), uy + radius * math.sin(start + sweep * i / segments), z)
            for i in range(segments + 1)]


class _Draft:
    def __init__(self, source_name, conv, options):
        self.source_name, self.conv, self.options = source_name, conv, options
        self.candidates = []  # dict(kind, id, key, geometry, base, confidence, notes, source, declared)
        self.levels = []
        self.unmapped = []  # bounded: only the max_unmapped smallest entries (by sort key) are kept
        self.unmapped_total = 0
        self.by_reason = {}
        self.declared = None  # the writer's bim.dependencies of the object being read, when it carries them

    @staticmethod
    def _key(entry):
        return (entry['object_id'] or '', -1 if entry['part'] is None else entry['part'], entry['reason'])

    def skip(self, source, reason, detail):
        self.unmapped_total += 1
        self.by_reason[reason] = self.by_reason.get(reason, 0) + 1
        self.unmapped.append({'object_id': source['object_id'], 'part': source['part'], 'layer': source['layer'],
                              'geometry_type': source['geometry_type'], 'reason': reason, 'detail': detail})
        if len(self.unmapped) >= 2 * self.options['max_unmapped']:
            self.unmapped = sorted(self.unmapped, key=self._key)[:self.options['max_unmapped']]

    def report(self):
        listed = sorted(self.unmapped, key=self._key)[:self.options['max_unmapped']]
        if self.unmapped_total > len(listed):
            listed.append({'object_id': None, 'part': None, 'layer': None, 'geometry_type': None, 'reason': 'unmapped_limit',
                           'detail': f'{self.unmapped_total - len(listed)} more entries not listed'})
        return listed

    def propose(self, kind, element_id, key, geometry, base, mapping, notes, source, inherited):
        # What the P8-02 writer could not carry (beyond +/-1e5 m, under 1e-6 m) is unmapped here, not proposed.
        try:
            # An opening is still its box [ax, ay, bx, by, height, thickness] here, checked like a wall.
            checked = 'wall' if kind in OPENINGS else kind
            br.check_representable({'Elements': [{'Id': element_id, 'Kind': checked, 'GeometryMetres': geometry}]})
        except RhinoAdapterError as error:
            raise _Unmapped('degenerate' if 'below' in error.detail else 'out_of_range', error.detail) from None
        if len(self.levels) + len(self.candidates) >= self.options['max_proposals']:
            raise RhinoAdapterError('draft_limit', f'more than max_proposals {self.options["max_proposals"]} proposals while reading')
        confidence = CONFIDENCE[mapping] * (INHERITED_FACTOR if inherited else 1.0)
        notes = list(notes) + (['layer-inherited'] if inherited else [])
        declared = self.declared if element_id == source['object_id'] else None
        record = {'kind': kind, 'id': element_id, 'key': key, 'geometry': geometry, 'base': base,
                  'confidence': round(confidence, 6), 'notes': notes, 'source': source, 'declared': declared}
        (self.levels if kind == 'level' else self.candidates).append(record)

    # -------------------------------------------------------------- per kind

    def level(self, geometry, source, inherited):
        if not isinstance(geometry, rhino3dm.Point):
            raise _Unmapped('geometry_unsupported', f'{type(geometry).__name__} on a level layer (a Point is expected)')
        x, y, z = self.conv.xyz(geometry.Location)
        _in_range([z], 'elevation')
        notes = [] if abs(x) <= TOL and abs(y) <= TOL else ['plan-position-ignored']
        self.propose('level', source['object_id'], source['object_id'], [z], None, 'level-point', notes, source, inherited)

    def wall(self, geometry, source, inherited):
        options = self.options
        if isinstance(geometry, rhino3dm.Extrusion):
            kind = _path_kind(geometry, self.conv)
            if kind == 'axis':
                values, base, mapping, notes = *_axis_wall(geometry, self.conv, options['max_vertices']), 'wall-axis-extrusion', []
            elif kind == 'vertical':
                values, base, mapping, notes = *_footprint_wall(geometry, self.conv, options['max_vertices']), 'wall-footprint-extrusion', ['footprint-rectangle']
            else:
                raise _Unmapped('wall_extrusion_unsupported', 'extrusion path is neither horizontal nor vertical')
            self.check_wall(values, base)
            self.propose('wall', source['object_id'], source['object_id'], values, base, mapping, notes, source, inherited)
            return
        if not isinstance(geometry, rhino3dm.Curve):
            raise _Unmapped('geometry_unsupported', f'{type(geometry).__name__} on a wall layer (a curve or extrusion is expected)')
        mapping, notes = 'wall-centreline', ['centreline', 'height-assumed', 'thickness-assumed']
        if _maybe_arc(geometry):
            arc = _circular(geometry, self.conv)
            if arc is None:
                raise _Unmapped('curve_unsupported', f'{type(geometry).__name__} is not a line, polyline or horizontal circular arc')
            if options['arc_segments'] == 0:
                raise _Unmapped('arc_not_approximated', 'BIM-IR walls are straight; set arc_segments to approximate arcs with chords')
            points = _arc_chords(arc, options['arc_segments'])
            mapping, notes = 'wall-arc-chord', ['arc-approximated', 'centreline', 'height-assumed', 'thickness-assumed']
        else:
            raw = _vertices(geometry, options['max_vertices'])
            if raw is None:
                raise _Unmapped('curve_unsupported', f'{type(geometry).__name__} is not a line, polyline or arc')
            points = [self.conv.xyz(p) for p in raw]
        if len(points) < 2:
            raise _Unmapped('degenerate', f'curve has {len(points)} point(s)')
        for part in range(len(points) - 1):
            a, b = points[part], points[part + 1]
            part_source = {**source, 'part': part}
            try:
                if abs(a[2] - b[2]) > TOL:
                    raise _Unmapped('curve_not_horizontal', f'segment rises {b[2] - a[2]} m')
                values = [a[0], a[1], b[0], b[1], options['wall_height'], options['wall_thickness']]
                self.check_wall(values, a[2])
                key = f"{source['object_id']}:{part}"
                self.propose('wall', _derived_id(source['object_id'], str(part)), key, values, a[2], mapping, notes, part_source, inherited)
            except _Unmapped as error:
                self.skip(part_source, error.reason, error.detail)
            except ArithmeticError as error:
                self.skip(part_source, 'degenerate', f'{type(error).__name__}: {error}')

    @staticmethod
    def check_wall(values, base):
        _in_range(values[:4] + [base], 'wall')
        _length(math.hypot(values[2] - values[0], values[3] - values[1]), 'axis length')
        _length(values[4], 'height')
        _length(values[5], 'thickness')
        _in_range(values[4:], 'wall height/thickness')

    def space(self, geometry, source, inherited):
        notes, mapping = [], 'space-outline'
        if isinstance(geometry, rhino3dm.Extrusion):
            if _path_kind(geometry, self.conv) != 'vertical':
                raise _Unmapped('space_extrusion_unsupported', 'only vertical extrusions of a plan outline are rooms')
            try:
                ring, base, _ = _footprint(geometry, self.conv, self.options['max_vertices'])
            except _Unmapped as error:
                raise _Unmapped('space_extrusion_unsupported' if error.reason == 'wall_extrusion_unsupported' else error.reason, error.detail) from None
            notes, mapping = ['height-dropped'], 'space-extrusion'
        elif isinstance(geometry, rhino3dm.Curve):
            if _maybe_arc(geometry) and _circular(geometry, self.conv) is not None:
                raise _Unmapped('space_not_rectangle' if geometry.IsClosed else 'open_curve', 'arcs and circles are not rectangles')
            raw = _vertices(geometry, self.options['max_vertices'])
            if raw is None:
                raise _Unmapped('curve_unsupported', f'{type(geometry).__name__} is not a line or polyline')
            points = [self.conv.xyz(p) for p in raw]
            if len(points) < 2:
                raise _Unmapped('degenerate', f'curve has {len(points)} point(s)')
            if not _closed(points):
                raise _Unmapped('open_curve', 'a space outline must be closed')
            if not _horizontal(points):
                raise _Unmapped('curve_not_horizontal', 'outline is not horizontal')
            ring, base = _rectangle(points), points[0][2]
        else:
            raise _Unmapped('geometry_unsupported', f'{type(geometry).__name__} on a space layer (a closed curve or extrusion is expected)')
        if any(min(abs(ring[i][0] - ring[(i + 1) % 4][0]), abs(ring[i][1] - ring[(i + 1) % 4][1])) > TOL for i in range(4)):
            raise _Unmapped('space_not_axis_aligned', 'BIM-IR spaces are axis-aligned rectangles')
        xs, ys = [c[0] for c in ring], [c[1] for c in ring]
        x0, y0 = min(xs), min(ys)
        values = [x0, y0, br._snap(max(xs) - x0), br._snap(max(ys) - y0)]
        _in_range([x0, y0, max(xs), max(ys), base], 'space')
        _length(values[2], 'width')
        _length(values[3], 'depth')
        self.propose('space', source['object_id'], source['object_id'], values, base, mapping, notes, source, inherited)

    def door(self, geometry, source, inherited):
        self.opening('door', geometry, source, inherited)

    def window(self, geometry, source, inherited):
        self.opening('window', geometry, source, inherited)

    def opening(self, kind, geometry, source, inherited):
        """A door or window volume: a writer-style box along the host axis, or a vertical extrusion of its plan rectangle.
        Its host wall, level and sill are decided in assemble(), once every wall is known."""
        if not isinstance(geometry, rhino3dm.Extrusion):
            raise _Unmapped('geometry_unsupported', f'{type(geometry).__name__} on a {kind} layer (an extrusion box is expected)')
        path = _path_kind(geometry, self.conv)
        try:
            if path == 'axis':
                values, base, mapping, notes = *_axis_wall(geometry, self.conv, self.options['max_vertices']), 'opening-axis-extrusion', []
            elif path == 'vertical':
                values, base, mapping, notes = *_footprint_wall(geometry, self.conv, self.options['max_vertices']), 'opening-footprint-extrusion', ['footprint-rectangle']
            else:
                raise _Unmapped('opening_extrusion_unsupported', 'extrusion path is neither horizontal nor vertical')
        except _Unmapped as error:
            if error.reason in ('wall_extrusion_unsupported', 'wall_footprint_ambiguous'):
                raise _Unmapped(error.reason.replace('wall_', 'opening_', 1), error.detail) from None
            raise
        self.check_wall(values, base)
        self.propose(kind, source['object_id'], source['object_id'], values, base, mapping, notes, source, inherited)

    def host(self, record, walls, level_for):
        """Put an opening on its host wall: the writer's declared host when it holds the opening, else the wall that holds
        it on its axis (then smallest id). Sets its [ax, ay, bx, by, sill, height] and [level, host]; False when unmapped."""
        g, base, notes, declared = record['geometry'], record['base'], record['notes'], record['declared']
        fits = []
        for wall in walls:
            deps = wall['dependencies']
            if len(deps) > 1:
                continue  # the writer needs the host on exactly its opening's level
            fit = _fit(g, base, wall['geometry'], wall['base'])
            if fit is not None:
                fits.append((fit['offset'] > TOL, wall['id'], wall, fit))
        fits.sort(key=lambda item: (item[0], item[1]))
        chosen = None
        if declared is not None and len(declared) == 2:
            chosen = next((item for item in fits if item[1] == declared[1]), None)
        if chosen is None:
            if not fits:
                self.skip(record['source'], 'opening_host_missing',
                          'no proposed wall holds the opening (on its axis within half its thickness, between its ends, base to top)')
                return False
            chosen = fits[0]
            notes.append('host-inferred')
            if declared is not None:
                notes.append('declared-host-ignored')
            if len(fits) > 1:
                notes.append('host-ambiguous')
        _, host_id, wall, fit = chosen
        points = [(g[0], g[1]), (g[2], g[3])]
        projected = fit['offset'] > TOL
        if projected:
            (ax, ay), (ux, uy) = fit['a'], fit['u']
            stations = [min(max(fit['s0'], 0.0), fit['length']), min(max(fit['s1'], 0.0), fit['length'])]
            points = [(ax + s * ux, ay + s * uy) for s in stations]
        if math.dist(points[0], points[1]) < br.MIN_LENGTH:
            self.skip(record['source'], 'degenerate', 'opening width on its host axis is below the writer precision')
            return False
        sill = br._snap(base - wall['base'])
        if sill <= 0:
            sill = 0.0
        if record['kind'] == 'door':
            if sill > TOL:
                self.skip(record['source'], 'door_sill_not_zero', f'door bottom is {sill} m above its host wall base; BIM-IR doors stand on their level')
                return False
            sill = 0.0
        if projected:
            notes.append('projected-to-host-axis')
            record['confidence'] = round(record['confidence'] * PROJECTED_FACTOR, 6)
        if fit['s0'] > fit['s1']:
            points.reverse()
            notes.append('reversed-to-host')
        if abs(g[5] - wall['geometry'][5]) > TOL:
            notes.append('depth-differs-from-host')
        if not wall['dependencies']:
            # BIM-IR openings depend on [level, host wall]: a host at z = 0 without a level gets one.
            wall['dependencies'] = [level_for(wall['base'])]
            wall['notes'].append('level-added-for-opening')
        record['dependencies'] = [wall['dependencies'][0], host_id]
        if declared == record['dependencies']:
            notes.append('dependencies-declared')
        record['geometry'] = [points[0][0], points[0][1], points[1][0], points[1][1], sill, g[4]]
        return True

    # -------------------------------------------------------------- assembly

    def assemble(self, project_id):
        # Ids proposed twice (copied objects with one id, or a derived segment id equal to an object id) are all
        # unmapped, so the result does not depend on which one the file lists first.
        records = self.levels + self.candidates
        count = {}
        for record in records:
            count[record['id']] = count.get(record['id'], 0) + 1
        for record in records:
            if count[record['id']] > 1:
                self.skip(record['source'], 'duplicate_id', f'{count[record["id"]]} objects propose id {record["id"]}')
        records = [r for r in records if count[r['id']] == 1]
        kind_of = {r['id']: r['kind'] for r in records}
        elevation_of = {r['id']: r['geometry'][0] for r in records if r['kind'] == 'level'}
        levels = sorted((r for r in records if r['kind'] == 'level'), key=lambda r: r['id'])
        hosts = [(r['geometry'][0], r['id']) for r in levels]
        taken = set(kind_of)
        synthesized = []

        def synthesize(base):
            elevation = br._snap(base)
            level_id = _derived_id(project_id, 'level', repr(elevation))
            while level_id in taken:
                level_id = _derived_id(level_id, 'level-collision')
            taken.add(level_id)
            hosts.append((elevation, level_id))
            synthesized.append({'kind': 'level', 'id': level_id, 'key': f'level@{br._core_number(elevation)}', 'geometry': [elevation],
                                'base': None, 'confidence': CONFIDENCE['level-synthesized'], 'notes': ['level-synthesized'],
                                'source': {'object_id': None, 'part': None, 'layer': None, 'geometry_type': None}, 'dependencies': []})
            return level_id

        def level_for(base):
            match = [i for elevation, i in hosts if abs(elevation - base) <= TOL]
            return match[0] if match else synthesize(base)

        openings = []
        for record in sorted((r for r in records if r['kind'] != 'level'), key=lambda r: (RANK[r['kind']], r['id'])):
            if record['kind'] in OPENINGS:
                openings.append(record)  # placed on their hosts below, once every wall has its level
                continue
            base = record['base']
            record['dependencies'] = None
            if record['declared'] is not None:
                # The writer's own dependencies (each once, in order), kept when they still agree with the geometry:
                # only proposed elements of a lower kind rank (level < wall < space, so no cycle), and every declared
                # level at the element's base, or none at z = 0. Dropping some is noted declared-dependencies-filtered
                # (all dropped: the geometry decides); a level off the base drops them all (declared-dependencies-ignored).
                declared = list(dict.fromkeys(record['declared']))
                kept = [d for d in declared if d in kind_of and kind_of[d] in ('level', 'wall') and RANK[kind_of[d]] < RANK[record['kind']]]
                hosts_declared = [d for d in kept if kind_of[d] == 'level']
                if hosts_declared:
                    agrees = all(abs(elevation_of[d] - base) <= TOL for d in hosts_declared)
                else:
                    agrees = abs(base) <= TOL
                if not agrees:
                    record['notes'].append('declared-dependencies-ignored')
                elif len(kept) == len(declared):
                    record['dependencies'] = kept
                    record['notes'].append('dependencies-declared')
                else:
                    record['notes'].append('declared-dependencies-filtered')
                    if kept:
                        record['dependencies'] = kept
            if record['dependencies'] is not None:
                continue
            match = [i for elevation, i in hosts if abs(elevation - base) <= TOL]
            if match:
                record['dependencies'] = [match[0]]
                if len(match) > 1:
                    record['notes'].append('level-ambiguous')
            elif abs(base) <= TOL:
                record['dependencies'] = []
            else:
                record['dependencies'] = [synthesize(base)]
        walls = sorted((r for r in records if r['kind'] == 'wall'), key=lambda r: r['id'])
        dropped = {r['id'] for r in openings if not self.host(r, walls, level_for)}
        for record in levels:
            record['dependencies'] = []
        records = sorted([r for r in records if r['id'] not in dropped] + synthesized, key=lambda r: (RANK[r['kind']], r['id']))
        if len(records) > self.options['max_proposals']:
            raise RhinoAdapterError('draft_limit', f'{len(records)} proposals > max_proposals {self.options["max_proposals"]}')
        return records


def _element(record, source_name):
    return {'Id': record['id'], 'Kind': record['kind'], 'GeometryMetres': [br._core_number(v) for v in record['geometry']],
            'Dependencies': list(record['dependencies']), 'Provenance': {'Source': 'rhino:' + source_name, 'Approval': 'draft-unapproved'}}


def draft(input_path, **given):
    """Read a .3dm and return the draft proposal as a dict. Never writes anything."""
    options = _options(given)
    path = pathlib.Path(input_path)
    if not path.is_file():
        raise RhinoAdapterError('rhino_read_failed', f'{path} is not a readable file')
    size = path.stat().st_size
    if size > options['max_file_bytes']:
        raise RhinoAdapterError('draft_limit', f'{size} bytes > max_file_bytes {options["max_file_bytes"]}')
    model = rhino3dm.File3dm.Read(str(path))
    if model is None:
        raise RhinoAdapterError('rhino_read_failed', f'{path} is not a .3dm model rhino3dm can read')
    return _draft(model, path.name, lambda: hashlib.sha256(path.read_bytes()).hexdigest(), options)


def draft_model(model, source_name, **given):
    """Draft an in-memory rhino3dm.File3dm (no file). Without project_id or a BIM root marker, the project id is
    derived from source_name."""
    return _draft(model, source_name, lambda: 'model:' + source_name, _options(given))


def _declared(attributes, object_id):
    """The writer's bim.dependencies when the object carries its own identity (bim.id == object id), else None."""
    try:
        strings = dict(attributes.GetUserStrings())
    except Exception:
        return None
    if strings.get('bim.id') != object_id:
        return None
    try:
        declared = json.loads(strings.get('bim.dependencies', ''))
    except (ValueError, RecursionError):
        return None
    if not isinstance(declared, list) or not all(isinstance(d, str) and br.UUID_RE.match(d) for d in declared):
        return None
    return declared


def _draft(model, source_name, fingerprint, options):
    units = model.Settings.ModelUnitSystem
    if options['metres_per_unit'] is not None:
        conv, units_name = _Converter('mul', options['metres_per_unit']), 'override'
    else:
        known = [(name, scale) for value, name, scale in UNITS if value == units]
        if not known:
            raise RhinoAdapterError('rhino_units_unsupported', f'model units {units}; pass metres_per_unit to set the scale')
        units_name, scale = known[0]
        conv = _Converter(*scale)
    layers = list(model.Layers)
    path_of = {layer.Index: layer.FullPath for layer in layers}
    project_id = options['project_id']
    if project_id is None:
        roots = [layer for layer in layers if layer.FullPath == br.ROOT_LAYER]
        marked = roots[0].GetUserString('bim.project.id') if len(roots) == 1 else None
        project_id = marked if marked is not None and br.UUID_RE.match(marked) else _derived_id('project', fingerprint())
    state = _Draft(source_name, conv, options)
    # First pass: object ids, so every copy of a repeated id is unmapped whatever the file order.
    ids = {}
    count = 0
    for obj in model.Objects:
        count += 1
        if count > options['max_objects']:
            raise RhinoAdapterError('draft_limit', f'more than max_objects {options["max_objects"]} objects')
        object_id = str(obj.Attributes.Id)
        ids[object_id] = ids.get(object_id, 0) + 1
    for obj in model.Objects:
        geometry = obj.Geometry
        attributes = obj.Attributes
        object_id = str(attributes.Id)
        layer = path_of.get(attributes.LayerIndex)
        source = {'object_id': object_id, 'part': None, 'layer': layer, 'geometry_type': type(geometry).__name__}
        if ids[object_id] > 1:
            state.skip(source, 'duplicate_object_id', f'{ids[object_id]} objects share this id')
            continue
        if getattr(attributes, 'IsInstanceDefinitionObject', False):
            # Geometry inside a block definition is placed only through its references; it is not model geometry.
            state.skip(source, 'block_definition_member', 'object belongs to a block (instance) definition')
            continue
        kind, inherited = _layer_kind(layer)
        state.declared = _declared(attributes, object_id)
        proposed = (len(state.levels), len(state.candidates))
        try:
            if kind is None:
                raise _Unmapped('layer_unmapped', f'layer {layer!r} is not a wall, space, level, door or window layer')
            getattr(state, kind)(geometry, source, inherited)
        except (_Unmapped, ArithmeticError) as error:
            # Only typed geometry refusals and floating-point arithmetic failures unmap the object; anything else
            # (a ValueError, TypeError, ...) is a bug and propagates. A failed object proposes nothing.
            del state.levels[proposed[0]:]
            del state.candidates[proposed[1]:]
            if isinstance(error, _Unmapped):
                state.skip(source, error.reason, error.detail)
            else:
                state.skip(source, 'degenerate', f'{type(error).__name__}: {error}')
    records = state.assemble(project_id)
    proposals = [{'key': r['key'], 'kind': r['kind'], 'element': _element(r, source_name), 'confidence': r['confidence'],
                  'notes': r['notes'], 'source': r['source']} for r in records]
    project = {'Id': project_id, 'Revision': 0, 'Elements': [p['element'] for p in proposals]}
    br.check_representable(br.validate_project(json.loads(json.dumps(project))))
    kinds = [r['kind'] for r in records]
    return {
        'draft_version': DRAFT_VERSION, 'status': 'proposal', 'persisted': False,
        'source': {'file': source_name, 'units': units_name, 'metres_per_unit': br._core_number(conv.metres_per_unit), 'objects': count},
        'options': {k: options[k] for k in ('wall_height', 'wall_thickness', 'arc_segments')},
        'project': project, 'proposals': proposals, 'unmapped': state.report(),
        'summary': {'levels': kinds.count('level'), 'walls': kinds.count('wall'), 'doors': kinds.count('door'),
                    'windows': kinds.count('window'), 'spaces': kinds.count('space'),
                    'unmapped': state.unmapped_total, 'unmapped_by_reason': dict(sorted(state.by_reason.items()))},
    }


def dumps(result):
    return json.dumps(result, ensure_ascii=False, separators=(',', ':'))


FLAGS = {'--project-id': ('project_id', str), '--wall-height': ('wall_height', float), '--wall-thickness': ('wall_thickness', float),
         '--arc-segments': ('arc_segments', int), '--metres-per-unit': ('metres_per_unit', float)}


def _main(argv):
    try:
        if not argv or argv[0].startswith('--') or len(argv) % 2 != 1:
            raise RhinoAdapterError('usage', 'rhino_draft.py INPUT_3DM [' + '] ['.join(f'{f} V' for f in FLAGS) + ']')
        options = {}
        for flag, raw in zip(argv[1::2], argv[2::2]):
            if flag not in FLAGS or FLAGS[flag][0] in options:
                raise RhinoAdapterError('usage', f'unknown or repeated flag {flag}')
            name, parse = FLAGS[flag]
            try:
                options[name] = parse(raw)
            except ValueError:
                raise RhinoAdapterError('draft_options_invalid', f'{flag} {raw!r}') from None
        _options(options)
        print(dumps(draft(argv[0], **options)))
        return 0
    except RhinoAdapterError as error:
        print(json.dumps({'error': error.code, 'detail': error.detail}), file=sys.stderr)
        return 1
    except Exception as error:  # one JSON error line, never a traceback
        print(json.dumps({'error': 'internal_error', 'detail': f'{type(error).__name__}: {error}'}), file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(_main(sys.argv[1:]))
