"""BIM-IR Project JSON <-> Rhino .3dm through rhino3dm (MIT). No Rhino installation is needed or used.

Usage:
  python bim_rhino.py export PROJECT_JSON OUTPUT_3DM   -> stdout {"status":"exported","elements":N,"path":...}
  python bim_rhino.py import INPUT_3DM                 -> stdout the Project in Core's default JSON shape
Failures print one JSON object {"error": code, "detail": text} on stderr and exit 1.

The persisted BIM-IR schema is unchanged: the adapter reads and writes exactly
Project{Id,Revision,Elements[{Id,Kind,GeometryMetres,Dependencies,Provenance{Source,Approval}}]}.
Input checks are stricter than Core in places: UUIDs must be lowercase text, members must be exactly
the Core ones, kinds must be ones the Core GeometryCodec decodes (plus door/window in the IntentCompiler layout
[ax, ay, bx, by, sill, height] with dependencies [level, host wall], P8-09), strings must be valid Unicode, and
lengths must be representable in a .3dm (see MIN_LENGTH/MAX_COORDINATE).
"""
import json
import math
import os
import pathlib
import re
import sys
import uuid

import rhino3dm

SCHEMA = 'bim-agentic-os.rhino/1'
ROOT_LAYER = 'BIM'
LAYER_OF_KIND = {'level': 'Levels', 'wall': 'Walls', 'space': 'Spaces', 'door': 'Doors', 'window': 'Windows',
                 'plan': 'Documentation', 'schedule': 'Documentation', 'sheet': 'Documentation'}
CATEGORY_LAYERS = ('Levels', 'Walls', 'Spaces', 'Documentation')
# Door and window layers are added only when the project has such elements, so files without openings keep the P8-02 layers.
OPENING_LAYERS = ('Doors', 'Windows')
OPENING_KINDS = ('door', 'window')
ARITY = {'wall': 6, 'level': 1, 'space': 4, 'door': 6, 'window': 6, 'plan': 0, 'schedule': 0, 'sheet': 0}
ARITY_CODE = {'wall': 'wall_geometry', 'level': 'level_geometry', 'space': 'space_geometry',
              'door': 'rhino_opening_geometry', 'window': 'rhino_opening_geometry'}
# Metres per model unit accepted on import. Anything else (feet, inches, unset...) is refused, never guessed.
# Millimetres divide by 1000 like Core's Length.FromMillimetres.
UNIT_DIVISOR = {rhino3dm.UnitSystem.Meters: 1.0, rhino3dm.UnitSystem.Millimeters: 1000.0}
# Values derived from Rhino geometry (thickness, height, space size) carry transform noise of
# ~1e-15 m; they are snapped to the nanometre grid so export->import->export is a fixed point.
DECIMALS = 9
POSITION_TOLERANCE = 1e-7  # metres; structural checks only, never used to accept wrong geometry
# Lengths that survive the tolerance checks and nanometre snapping above. Core accepts smaller or
# farther values; export refuses them (rhino_precision) instead of writing a file import would reject.
MIN_LENGTH = 1e-6
MAX_COORDINATE = 1e5
USER_KEYS = ('bim.id', 'bim.kind', 'bim.order', 'bim.dependencies', 'bim.provenance.source', 'bim.provenance.approval')
UUID_RE = re.compile(r'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$')


class RhinoAdapterError(ValueError):
    def __init__(self, code, detail=''):
        super().__init__(f'{code}: {detail}' if detail else code)
        self.code = code
        self.detail = detail


# ---------------------------------------------------------------- BIM-IR validation (mirrors Core rules)

def _require(condition, detail):
    if not condition:
        raise RhinoAdapterError('bim_ir_invalid', detail)


def _uuid(value, where):
    _require(isinstance(value, str) and UUID_RE.match(value) is not None, f'{where} must be a lowercase UUID string')
    return value


def _number(value, where):
    _require(isinstance(value, (int, float)) and not isinstance(value, bool), f'{where} must be a number')
    try:
        return float(value)
    except OverflowError:
        raise RhinoAdapterError('bim_ir_invalid', f'{where} is outside the double range') from None


def _text(value, where):
    _require(isinstance(value, str), f'{where} must be a string')
    try:
        value.encode('utf-8')
    except UnicodeEncodeError:
        raise RhinoAdapterError('bim_ir_invalid', f'{where} is not valid Unicode (lone surrogate)') from None
    return value


def validate_project(project):
    """Check shape, then the Core GeometryCodec/Compiler.Validate rules. Returns the project unchanged."""
    _require(isinstance(project, dict) and set(project) == {'Id', 'Revision', 'Elements'}, 'Project needs exactly Id, Revision, Elements')
    _uuid(project['Id'], 'Project.Id')
    revision = project['Revision']
    _require(isinstance(revision, int) and not isinstance(revision, bool) and -2**31 <= revision < 2**31, 'Project.Revision must be a 32-bit integer')
    _require(isinstance(project['Elements'], list), 'Project.Elements must be an array')
    for index, element in enumerate(project['Elements']):
        where = f'Elements[{index}]'
        _require(isinstance(element, dict) and set(element) == {'Id', 'Kind', 'GeometryMetres', 'Dependencies', 'Provenance'},
                 f'{where} needs exactly Id, Kind, GeometryMetres, Dependencies, Provenance')
        _uuid(element['Id'], f'{where}.Id')
        _text(element['Kind'], f'{where}.Kind')
        _require(isinstance(element['GeometryMetres'], list), f'{where}.GeometryMetres must be an array')
        for i, value in enumerate(element['GeometryMetres']):
            _number(value, f'{where}.GeometryMetres[{i}]')
        _require(isinstance(element['Dependencies'], list), f'{where}.Dependencies must be an array')
        for i, value in enumerate(element['Dependencies']):
            _uuid(value, f'{where}.Dependencies[{i}]')
        provenance = element['Provenance']
        _require(isinstance(provenance, dict) and set(provenance) == {'Source', 'Approval'}, f'{where}.Provenance needs exactly Source and Approval')
        _text(provenance['Source'], f'{where}.Provenance.Source')
        _text(provenance['Approval'], f'{where}.Provenance.Approval')
    elements = project['Elements']
    ids = [e['Id'] for e in elements]
    if len(set(ids)) != len(ids):
        raise RhinoAdapterError('duplicate_id', 'Duplicate logical identity')
    known = set(ids)
    for e in elements:
        g = [float(v) for v in e['GeometryMetres']]
        kind = e['Kind']
        if kind not in ARITY:
            raise RhinoAdapterError('geometry_kind_unsupported', f"{e['Id']} kind {kind!r}")
        if len(g) != ARITY[kind]:
            raise RhinoAdapterError(ARITY_CODE.get(kind, 'documentation_geometry'), f"{e['Id']} expects {ARITY[kind]} values, got {len(g)}")
        if any(not math.isfinite(v) for v in g):
            raise RhinoAdapterError('geometry_non_finite', e['Id'])
        if kind == 'wall' and (g[4] <= 0 or g[5] <= 0 or (g[0] == g[2] and g[1] == g[3])):
            raise RhinoAdapterError('wall_geometry', f"{e['Id']} Invalid wall axis/height/thickness")
        for d in e['Dependencies']:
            if d not in known:
                raise RhinoAdapterError('missing_dependency', f"{e['Id']} depends on unknown {d}")
    # Kahn's algorithm, iterative like Compiler.Validate.
    remaining = {e['Id']: len(e['Dependencies']) for e in elements}
    dependents = {i: [] for i in ids}
    for e in elements:
        for d in e['Dependencies']:
            dependents[d].append(e['Id'])
    ready = [i for i, n in remaining.items() if n == 0]
    processed = 0
    while ready:
        current = ready.pop()
        processed += 1
        for child in dependents[current]:
            remaining[child] -= 1
            if remaining[child] == 0:
                ready.append(child)
    if processed != len(ids):
        raise RhinoAdapterError('dependency_cycle', 'Cyclic logical dependencies')
    _check_openings(elements)
    return project


def _opening_problem(kind, g, h):
    """Why door/window geometry g = [ax, ay, bx, by, sill, height] does not fit host wall h, or None.

    The IntentCompiler/IFC rule: both ends on the host axis, ordered in the host direction, inside its length, sill >= 0
    above the level (0 for a door), positive height, head no higher than the host top."""
    ax, ay, bx, by = h[:4]
    length = math.hypot(bx - ax, by - ay)
    ux, uy = (bx - ax) / length, (by - ay) / length
    s0 = (g[0] - ax) * ux + (g[1] - ay) * uy
    s1 = (g[2] - ax) * ux + (g[3] - ay) * uy
    off = max(abs((g[0] - ax) * uy - (g[1] - ay) * ux), abs((g[2] - ax) * uy - (g[3] - ay) * ux))
    if not g[5] > 0:
        return 'height must be positive'
    if g[4] < 0:
        return f'sill {g[4]} m is below its level'
    if kind == 'door' and g[4] != 0:
        return f'door sill {g[4]} m is not 0 (BIM-IR doors stand on their level)'
    if off > POSITION_TOLERANCE:
        return f'ends are {off} m off the host axis'
    if not s1 - s0 > 0:
        return 'ends are not ordered in the host wall direction'
    if s0 < -POSITION_TOLERANCE or s1 > length + POSITION_TOLERANCE:
        return f'runs past the host wall ends ({s0} .. {s1} m on a {length} m axis)'
    if g[4] + g[5] > h[4] + POSITION_TOLERANCE:
        return f'head {g[4] + g[5]} m is above the host wall top {h[4]} m'
    return None


def _check_openings(elements):
    by_id = {e['Id']: e for e in elements}
    for e in elements:
        if e['Kind'] not in OPENING_KINDS:
            continue
        deps = e['Dependencies']
        if not (len(deps) == 2 and by_id[deps[0]]['Kind'] == 'level' and by_id[deps[1]]['Kind'] == 'wall'
                and by_id[deps[1]]['Dependencies'] == [deps[0]]):
            raise RhinoAdapterError('rhino_opening_host', f"{e['Kind']} {e['Id']} must depend on [level, host wall] with the host wall on that level")
        problem = _opening_problem(e['Kind'], [float(v) for v in e['GeometryMetres']], [float(v) for v in by_id[deps[1]]['GeometryMetres']])
        if problem is not None:
            raise RhinoAdapterError('rhino_opening_geometry', f"{e['Kind']} {e['Id']} {problem}")


def check_representable(project):
    """Refuse values a .3dm round trip cannot carry faithfully (Core itself accepts them)."""
    def coordinate(value, what, element_id):
        if abs(value) > MAX_COORDINATE:
            raise RhinoAdapterError('rhino_precision', f'{element_id} {what} {value} m is beyond +/-{MAX_COORDINATE} m')

    def length(value, what, element_id):
        if value < MIN_LENGTH:
            raise RhinoAdapterError('rhino_precision', f'{element_id} {what} {value} m is below {MIN_LENGTH} m')
        coordinate(value, what, element_id)

    for e in project['Elements']:
        g = [float(v) for v in e['GeometryMetres']]
        if e['Kind'] == 'wall':
            for i, what in enumerate(('start.x', 'start.y', 'end.x', 'end.y')):
                coordinate(g[i], what, e['Id'])
            length(math.hypot(g[2] - g[0], g[3] - g[1]), 'axis length', e['Id'])
            length(g[4], 'height', e['Id'])
            length(g[5], 'thickness', e['Id'])
        elif e['Kind'] == 'level':
            coordinate(g[0], 'elevation', e['Id'])
        elif e['Kind'] == 'space':
            if g[2] <= 0 or g[3] <= 0:
                raise RhinoAdapterError('rhino_space_geometry', f"{e['Id']} width and depth must be positive to form a rectangle")
            length(g[2], 'width', e['Id'])
            length(g[3], 'depth', e['Id'])
            for value, what in ((g[0], 'corner.x'), (g[1], 'corner.y'), (g[0] + g[2], 'far corner.x'), (g[1] + g[3], 'far corner.y')):
                coordinate(value, what, e['Id'])
        elif e['Kind'] in OPENING_KINDS:
            for i, what in enumerate(('start.x', 'start.y', 'end.x', 'end.y', 'sill')):
                coordinate(g[i], what, e['Id'])
            length(math.hypot(g[2] - g[0], g[3] - g[1]), 'width', e['Id'])
            length(g[5], 'height', e['Id'])
    return project


# ---------------------------------------------------------------- export

def _base_elevation(element, by_id):
    """Elevation of the first level the element depends on; 0 when it depends on none."""
    for d in element['Dependencies']:
        if by_id[d]['Kind'] == 'level':
            return float(by_id[d]['GeometryMetres'][0])
    return 0.0


def _wall(g, z):
    """A capped box along [ax, ay, bx, by] at base z with a thickness x height section: a wall, or an opening's volume."""
    ax, ay, bx, by, height, thickness = g
    extrusion = rhino3dm.Extrusion()
    # Path = wall axis (location line) at the base; the profile is the vertical cross-section
    # in path-plane coordinates: x across the wall, y up.
    if not extrusion.SetPathAndUp(rhino3dm.Point3d(ax, ay, z), rhino3dm.Point3d(bx, by, z), rhino3dm.Vector3d(0, 0, 1)):
        raise RhinoAdapterError('rhino_write_failed', 'wall path rejected by rhino3dm')
    h = thickness / 2
    profile = rhino3dm.PolylineCurve([rhino3dm.Point3d(-h, 0, 0), rhino3dm.Point3d(h, 0, 0), rhino3dm.Point3d(h, height, 0),
                                      rhino3dm.Point3d(-h, height, 0), rhino3dm.Point3d(-h, 0, 0)])
    if not extrusion.SetOuterProfile(profile, True) or not extrusion.IsValid:
        raise RhinoAdapterError('rhino_write_failed', 'wall profile rejected by rhino3dm')
    return extrusion


def _space(g, z):
    x, y, w, d = g
    corners = [(x, y), (x + w, y), (x + w, y + d), (x, y + d), (x, y)]
    return rhino3dm.PolylineCurve([rhino3dm.Point3d(px, py, z) for px, py in corners])


def _geometry(element, by_id):
    g = [float(v) for v in element['GeometryMetres']]
    kind = element['Kind']
    if kind == 'wall':
        return _wall(g, _base_elevation(element, by_id))
    if kind == 'space':
        return _space(g, _base_elevation(element, by_id))
    if kind == 'level':
        return rhino3dm.Point(rhino3dm.Point3d(0, 0, g[0]))
    if kind in OPENING_KINDS:
        # The opening volume: a box from end to end along the host axis, from sill to head, through the host thickness.
        # The host wall stays one uncut extrusion (rhino3dm has no booleans).
        host = by_id[element['Dependencies'][1]]
        return _wall([g[0], g[1], g[2], g[3], g[5], float(host['GeometryMetres'][5])], _base_elevation(element, by_id) + g[4])
    # Documentation has no geometry in BIM-IR; a marker point at the origin carries its identity only.
    return rhino3dm.Point(rhino3dm.Point3d(0, 0, 0))


def _encode_text(value):
    # Rhino deletes a user string set to "", so text is stored JSON-encoded and is never empty.
    return json.dumps(value, ensure_ascii=False)


def build_model(project):
    check_representable(validate_project(project))
    model = rhino3dm.File3dm()
    model.Settings.ModelUnitSystem = rhino3dm.UnitSystem.Meters
    root = rhino3dm.Layer()
    root.Name = ROOT_LAYER
    root.SetUserString('bim.schema', SCHEMA)
    root.SetUserString('bim.project.id', project['Id'])
    root.SetUserString('bim.project.revision', str(project['Revision']))
    root_index = model.Layers.Add(root)
    root_id = model.Layers[root_index].Id
    layer_index = {}
    used = {LAYER_OF_KIND[e['Kind']] for e in project['Elements']}
    for name in CATEGORY_LAYERS + tuple(name for name in OPENING_LAYERS if name in used):
        layer = rhino3dm.Layer()
        layer.Name = name
        layer.ParentLayerId = root_id
        layer_index[name] = model.Layers.Add(layer)
    by_id = {e['Id']: e for e in project['Elements']}
    for order, element in enumerate(project['Elements']):
        attributes = rhino3dm.ObjectAttributes()
        attributes.Id = uuid.UUID(element['Id'])
        attributes.Name = f"{element['Kind']} {element['Id']}"
        attributes.LayerIndex = layer_index[LAYER_OF_KIND[element['Kind']]]
        values = {'bim.id': element['Id'], 'bim.kind': element['Kind'], 'bim.order': str(order),
                  'bim.dependencies': json.dumps(element['Dependencies'], separators=(',', ':')),
                  'bim.provenance.source': _encode_text(element['Provenance']['Source']),
                  'bim.provenance.approval': _encode_text(element['Provenance']['Approval'])}
        for key in USER_KEYS:
            attributes.SetUserString(key, values[key])
        added = model.Objects.Add(_geometry(element, by_id), attributes)
        if str(added) != element['Id']:
            raise RhinoAdapterError('rhino_write_failed', f"rhino3dm assigned {added} instead of {element['Id']}")
    return model


def export_project(project, output_path):
    """Write a new .3dm. Never replaces an existing file, even one created while exporting.

    rhino3dm writes into OUTPUT.pending, created exclusively (an existing file or symlink there is
    refused), and the result is published with a hard link, which fails if OUTPUT appeared meanwhile.
    """
    output = pathlib.Path(output_path)
    if output.exists() or output.is_symlink():
        raise RhinoAdapterError('output_exists', str(output))
    model = build_model(project)
    pending = output.with_name(output.name + '.pending')
    try:
        descriptor = os.open(pending, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, 'O_NOFOLLOW', 0), 0o600)
    except FileExistsError:
        raise RhinoAdapterError('output_exists', f'{pending} exists (stale pending file or symlink)') from None
    os.close(descriptor)
    try:
        if not model.Write(str(pending), 7):
            raise RhinoAdapterError('rhino_write_failed', str(output))
        try:
            os.link(pending, output)
        except FileExistsError:
            raise RhinoAdapterError('output_exists', f'{output} appeared during export') from None
    finally:
        os.unlink(pending)
    return output


# ---------------------------------------------------------------- import

def _snap(value):
    snapped = round(value, DECIMALS)
    return 0.0 if snapped == 0 else snapped


def _metres(value, scale):
    if scale == 1000.0:
        # Core LengthUnits.NormalizeMillimetres: drop less than 1e-6 mm of conversion noise.
        integer = round(value)
        if abs(value - integer) <= 1e-6:
            value = float(integer)
    return value / scale


def _xyz(point, scale):
    return _metres(point.X, scale), _metres(point.Y, scale), _metres(point.Z, scale)


def _polyline_points(curve, scale, code, element_id):
    if not isinstance(curve, rhino3dm.PolylineCurve):
        raise RhinoAdapterError(code, f'{element_id} profile is {type(curve).__name__}, not a polyline')
    return [_xyz(curve.Point(i), scale) for i in range(curve.PointCount)]


def _rectangle_corners(points, code, element_id):
    if len(points) != 5 or any(abs(a - b) > POSITION_TOLERANCE for a, b in zip(points[0], points[-1])):
        raise RhinoAdapterError(code, f'{element_id} is not a closed four-corner outline')
    return points[:4]


def _read_wall(geometry, scale, element_id, code='rhino_wall_geometry'):
    """[ax, ay, bx, by, height, thickness] and base z of a writer box (a wall, or with code rhino_opening_geometry an opening)."""
    if not isinstance(geometry, rhino3dm.Extrusion):
        raise RhinoAdapterError('rhino_geometry_mismatch', f'{element_id} is {type(geometry).__name__}, not an Extrusion')
    start, end = _xyz(geometry.PathStart, scale), _xyz(geometry.PathEnd, scale)
    if geometry.ProfileCount != 1 or abs(start[2] - end[2]) > POSITION_TOLERANCE or math.hypot(end[0] - start[0], end[1] - start[1]) <= POSITION_TOLERANCE:
        raise RhinoAdapterError(code, f'{element_id} path must be one horizontal axis with one profile')
    corners = _rectangle_corners(_polyline_points(geometry.Profile3d(0, 0.0), scale, code, element_id), code, element_id)
    base = start[2]
    zs = [c[2] for c in corners]
    bottom = [c for c in corners if abs(c[2] - base) <= POSITION_TOLERANCE]
    top = [c for c in corners if abs(c[2] - max(zs)) <= POSITION_TOLERANCE]
    if len(bottom) != 2 or len(top) != 2 or abs(min(zs) - base) > POSITION_TOLERANCE:
        raise RhinoAdapterError(code, f'{element_id} profile must be a rectangle standing on the axis')
    thickness = math.hypot(bottom[0][0] - bottom[1][0], bottom[0][1] - bottom[1][1])
    mid = ((bottom[0][0] + bottom[1][0]) / 2, (bottom[0][1] + bottom[1][1]) / 2)
    axis = (end[0] - start[0], end[1] - start[1])
    across = (bottom[0][0] - bottom[1][0], bottom[0][1] - bottom[1][1])
    if math.hypot(mid[0] - start[0], mid[1] - start[1]) > POSITION_TOLERANCE or abs(axis[0] * across[0] + axis[1] * across[1]) > POSITION_TOLERANCE * math.hypot(*axis):
        raise RhinoAdapterError(code, f'{element_id} profile must be centred on and square to the axis')
    for t in top:
        if min(math.hypot(t[0] - b[0], t[1] - b[1]) for b in bottom) > POSITION_TOLERANCE:
            raise RhinoAdapterError(code, f'{element_id} profile sides must be vertical')
    height = max(zs) - base
    return [start[0], start[1], end[0], end[1], _snap(height), _snap(thickness)], base


def _read_space(geometry, scale, element_id):
    if not isinstance(geometry, rhino3dm.PolylineCurve):
        raise RhinoAdapterError('rhino_geometry_mismatch', f'{element_id} space is {type(geometry).__name__}, not a PolylineCurve')
    corners = _rectangle_corners(_polyline_points(geometry, scale, 'rhino_space_geometry', element_id), 'rhino_space_geometry', element_id)
    xs, ys = [c[0] for c in corners], [c[1] for c in corners]
    x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
    for cx, cy, _ in corners:
        if min(abs(cx - x0), abs(cx - x1)) > POSITION_TOLERANCE or min(abs(cy - y0), abs(cy - y1)) > POSITION_TOLERANCE:
            raise RhinoAdapterError('rhino_space_geometry', f'{element_id} is not an axis-aligned rectangle')
    if len({(abs(cx - x0) <= POSITION_TOLERANCE, abs(cy - y0) <= POSITION_TOLERANCE) for cx, cy, _ in corners}) != 4:
        raise RhinoAdapterError('rhino_space_geometry', f'{element_id} is not an axis-aligned rectangle')
    if max(c[2] for c in corners) - min(c[2] for c in corners) > POSITION_TOLERANCE:
        raise RhinoAdapterError('rhino_space_geometry', f'{element_id} outline is not horizontal')
    return [x0, y0, _snap(x1 - x0), _snap(y1 - y0)], corners[0][2]


def _read_point(geometry, kind, element_id, scale):
    if not isinstance(geometry, rhino3dm.Point):
        raise RhinoAdapterError('rhino_geometry_mismatch', f'{element_id} {kind} is {type(geometry).__name__}, not a Point')
    x, y, z = _xyz(geometry.Location, scale)
    if abs(x) > POSITION_TOLERANCE or abs(y) > POSITION_TOLERANCE or (kind != 'level' and abs(z) > POSITION_TOLERANCE):
        expected = '(0, 0, elevation)' if kind == 'level' else 'the origin'
        raise RhinoAdapterError('rhino_position_mismatch', f'{element_id} {kind} marker is at ({x}, {y}, {z}), expected {expected}')
    return z


def _decode_text(value, where):
    try:
        decoded = json.loads(value)
    except (ValueError, RecursionError):
        decoded = None
    if not isinstance(decoded, str):
        raise RhinoAdapterError('rhino_object_invalid', f'{where} is not a JSON-encoded string')
    return decoded


def _read_strings(attributes):
    strings = dict(attributes.GetUserStrings())
    return {k: v for k, v in strings.items() if k.startswith('bim.')}


def import_project(input_path):
    path = pathlib.Path(input_path)
    if not path.is_file():
        raise RhinoAdapterError('rhino_read_failed', f'{path} is not a readable file')
    model = rhino3dm.File3dm.Read(str(path))
    if model is None:
        raise RhinoAdapterError('rhino_read_failed', f'{path} is not a .3dm model rhino3dm can read')
    layers = list(model.Layers)
    roots = [layer for layer in layers if layer.FullPath == ROOT_LAYER]
    if len(roots) != 1 or roots[0].GetUserString('bim.schema') is None:
        raise RhinoAdapterError('rhino_not_bim', 'no BIM root layer with a bim.schema marker; foreign .3dm models are refused')
    root = roots[0]
    if root.GetUserString('bim.schema') != SCHEMA:
        raise RhinoAdapterError('rhino_schema_unsupported', repr(root.GetUserString('bim.schema')))
    units = model.Settings.ModelUnitSystem
    if units not in UNIT_DIVISOR:
        raise RhinoAdapterError('rhino_units_unsupported', f'model units {units} (only Meters and Millimeters)')
    scale = UNIT_DIVISOR[units]
    project_id, revision = root.GetUserString('bim.project.id'), root.GetUserString('bim.project.revision')
    if project_id is None or UUID_RE.match(project_id) is None or revision is None or re.fullmatch(r'-?(0|[1-9][0-9]{0,9})', revision) is None:
        raise RhinoAdapterError('rhino_not_bim', 'BIM root layer lacks a valid project id/revision')
    path_of = {layer.Index: layer.FullPath for layer in layers}
    records = []
    bases = {}
    openings = {}
    for obj in model.Objects:
        attributes = obj.Attributes
        strings = _read_strings(attributes)
        if not strings:
            raise RhinoAdapterError('rhino_foreign_object', f'object {attributes.Id} on {path_of.get(attributes.LayerIndex)!r} carries no BIM identity')
        missing = [k for k in USER_KEYS if k not in strings]
        if missing:
            raise RhinoAdapterError('rhino_object_invalid', f'object {attributes.Id} lacks {missing}')
        element_id, kind = strings['bim.id'], strings['bim.kind']
        if str(attributes.Id) != element_id:
            raise RhinoAdapterError('rhino_identity_mismatch', f'object {attributes.Id} claims BIM id {element_id} (copied or re-identified object)')
        if kind not in LAYER_OF_KIND:
            raise RhinoAdapterError('geometry_kind_unsupported', f'{element_id} kind {kind!r}')
        expected_layer = f'{ROOT_LAYER}::{LAYER_OF_KIND[kind]}'
        if path_of.get(attributes.LayerIndex) != expected_layer:
            raise RhinoAdapterError('rhino_layer_mismatch', f'{element_id} {kind} is on {path_of.get(attributes.LayerIndex)!r}, expected {expected_layer!r}')
        if re.fullmatch(r'0|[1-9][0-9]{0,8}', strings['bim.order']) is None:
            raise RhinoAdapterError('rhino_object_invalid', f'{element_id} bim.order {strings["bim.order"]!r}')
        try:
            dependencies = json.loads(strings['bim.dependencies'])
        except (ValueError, RecursionError):
            raise RhinoAdapterError('rhino_object_invalid', f'{element_id} bim.dependencies is not JSON') from None
        geometry = obj.Geometry
        base = None
        if kind == 'wall':
            values, base = _read_wall(geometry, scale, element_id)
        elif kind == 'space':
            values, base = _read_space(geometry, scale, element_id)
        elif kind in OPENING_KINDS:
            box, opening_base = _read_wall(geometry, scale, element_id, 'rhino_opening_geometry')
            # The sill is the box base above the opening's level, known once every level is read.
            values = [box[0], box[1], box[2], box[3], 0.0, box[4]]
            openings[element_id] = (opening_base, box[5])
        elif kind == 'level':
            values = [_read_point(geometry, kind, element_id, scale)]
        else:
            _read_point(geometry, kind, element_id, scale)
            values = []
        source = _decode_text(strings['bim.provenance.source'], f'{element_id} bim.provenance.source')
        approval = _decode_text(strings['bim.provenance.approval'], f'{element_id} bim.provenance.approval')
        bases[element_id] = base
        records.append((int(strings['bim.order']), {
            'Id': element_id, 'Kind': kind, 'GeometryMetres': values, 'Dependencies': dependencies,
            'Provenance': {'Source': source, 'Approval': approval}}))
    orders = sorted(order for order, _ in records)
    if orders != list(range(len(records))):
        raise RhinoAdapterError('rhino_object_invalid', 'bim.order values are not a permutation of 0..n-1')
    records.sort(key=lambda item: item[0])
    project = {'Id': project_id, 'Revision': int(revision), 'Elements': [element for _, element in records]}
    read = {e['Id']: e for e in project['Elements']}
    for element_id, (opening_base, _) in openings.items():
        deps = read[element_id]['Dependencies']
        level = read.get(deps[0]) if isinstance(deps, list) and deps and isinstance(deps[0], str) else None
        elevation = level['GeometryMetres'][0] if level is not None and level['Kind'] == 'level' else 0.0
        read[element_id]['GeometryMetres'][4] = _snap(opening_base - elevation)
    validate_project(project)
    # Walls and spaces must still stand on the level export placed them on: a vertical move in Rhino
    # has no BIM-IR field to land in, so it is refused rather than dropped.
    by_id = {e['Id']: e for e in project['Elements']}
    for element in project['Elements']:
        if bases[element['Id']] is not None:
            expected = _base_elevation(element, by_id)
            if abs(bases[element['Id']] - expected) > POSITION_TOLERANCE:
                raise RhinoAdapterError('rhino_elevation_mismatch', f"{element['Id']} {element['Kind']} base is at {bases[element['Id']]} m, its level is at {expected} m")
    # An opening box is as deep as its host wall; BIM-IR has no opening depth to carry another value.
    for element_id, (_, depth) in openings.items():
        host = by_id[by_id[element_id]['Dependencies'][1]]
        if abs(depth - host['GeometryMetres'][5]) > POSITION_TOLERANCE:
            raise RhinoAdapterError('rhino_opening_geometry', f"{element_id} box is {depth} m deep, its host wall {host['Id']} is {host['GeometryMetres'][5]} m thick")
    return check_representable(project)


# ---------------------------------------------------------------- Core-shaped JSON

def _core_number(value):
    # System.Text.Json writes integral doubles without a fraction (3, not 3.0) and otherwise the
    # shortest round-trip form, which Python's repr also produces.
    if isinstance(value, float) and value.is_integer() and abs(value) < 1e15:
        return int(value)
    return value


def dumps(project):
    shaped = {'Id': project['Id'], 'Revision': project['Revision'], 'Elements': [
        {'Id': e['Id'], 'Kind': e['Kind'], 'GeometryMetres': [_core_number(v) for v in e['GeometryMetres']],
         'Dependencies': list(e['Dependencies']), 'Provenance': {'Source': e['Provenance']['Source'], 'Approval': e['Provenance']['Approval']}}
        for e in project['Elements']]}
    return json.dumps(shaped, separators=(',', ':'))


def _main(argv):
    try:
        if len(argv) == 3 and argv[0] == 'export':
            try:
                project = json.loads(pathlib.Path(argv[1]).read_text(encoding='utf-8-sig'))
            except (OSError, ValueError) as error:
                raise RhinoAdapterError('bim_ir_invalid', f'cannot read {argv[1]}: {type(error).__name__}: {error}') from None
            except RecursionError:
                raise RhinoAdapterError('bim_ir_invalid', f'{argv[1]} nests too deeply') from None
            output = export_project(project, argv[2])
            print(json.dumps({'status': 'exported', 'elements': len(project['Elements']), 'path': str(output)}))
            return 0
        if len(argv) == 2 and argv[0] == 'import':
            print(dumps(import_project(argv[1])))
            return 0
        raise RhinoAdapterError('usage', 'bim_rhino.py export PROJECT_JSON OUTPUT_3DM | import INPUT_3DM')
    except RhinoAdapterError as error:
        print(json.dumps({'error': error.code, 'detail': error.detail}), file=sys.stderr)
        return 1
    except Exception as error:  # one JSON error line, never a traceback
        print(json.dumps({'error': 'internal_error', 'detail': f'{type(error).__name__}: {error}'}), file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(_main(sys.argv[1:]))
