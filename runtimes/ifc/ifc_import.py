"""IFC <-> BIM-IR for levels, walls, doors, windows and spaces through the approved IfcOpenShell (LGPL).

Import reads IfcBuildingStorey, IfcWall (IfcWallStandardCase included), IfcDoor and IfcWindow filling an
IfcOpeningElement that voids a wall, and IfcSpace into the existing BIM-IR element kinds and layouts:
  level  [elevation]                                  no dependencies
  wall   [ax,ay,bx,by,height,thickness]               [level]
  door / window [ax,ay,bx,by,sill,height]             [level, host wall]
  space  [corner x, corner y, width, depth]           [level, bounding walls in id order]
Anything that cannot be represented exactly is listed under `unmapped` with a reason; nothing is guessed.
Export writes BIM-IR of those kinds to a deterministic IFC4 file (GlobalIds derived from BIM-IR ids), so
export -> import -> export gives the same BIM-IR and the same IFC DATA section. See docs/ifc.md.

Usage:
  python ifc_import.py export PROJECT_JSON OUTPUT_IFC   -> stdout {"status":"exported",...}
  python ifc_import.py import INPUT_IFC                 -> stdout the import report (bim.ifc-import/1)
Failures print one JSON object {"error": code, "detail": text} on stderr and exit 1.
"""
import hashlib
import json
import math
import os
from pathlib import Path
import re
import sys
import uuid

import ifcopenshell
import ifcopenshell.guid
import ifcopenshell.util.element
import ifcopenshell.validate

REPORT_SCHEMA = 'bim.ifc-import/1'
ID_NAMESPACE = 'bim-agentic-os/ifc-import/v1'
EXPORT_NAMESPACE = 'bim-agentic-os/ifc-export/v1'
OBSERVED = {'Source': 'ifc-import', 'Approval': 'observed'}
PROJECT_PSET = 'BimIrProject'
ELEMENT_PSET = 'BimIrElement'
KINDS = ('level', 'wall', 'door', 'window', 'space')
DOCUMENTATION = ('plan', 'schedule', 'sheet')
RANK = {kind: index for index, kind in enumerate(KINDS)}
ARITY = {'level': 1, 'wall': 6, 'door': 6, 'window': 6, 'space': 4}
# Metres. Structural checks only (alignment, containment, consistency); never used to accept other geometry.
TOLERANCE = 1e-6
# Values read back from placements and profiles carry ~1e-15 m of transform noise; they are snapped to the
# nanometre grid. Export refuses values off that grid (ifc_precision), so the roundtrip is exact.
DECIMALS = 9
MAX_COORDINATE = 1e5
# At most this many IfcProduct instances per import, like Mirror's MaxElements; more is refused, not truncated.
MAX_ELEMENTS = 20000
# Exported openings are slightly deeper than their host wall so the void cut has no coplanar faces.
OPENING_CLEARANCE = 0.005
# BIM-IR spaces have no height; export extrudes them to their tallest bounding wall, or this without walls.
DEFAULT_SPACE_HEIGHT = 3.0
UUID_RE = re.compile(r'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$')
SI_PREFIX = {None: 1, 'DECI': 10, 'CENTI': 100, 'MILLI': 1000, 'MICRO': 10**6}
SI_MULTIPLE = {'DECA': 10.0, 'HECTO': 100.0, 'KILO': 1000.0}

# Unmapped reasons. The first group repeats the Mirror (P2-01) names where the meaning is the same.
NO_BIM_IR_KIND = 'no_bim_ir_kind'
CATEGORY_UNSUPPORTED = 'category_unsupported'
WALL_BASE_OFFSET = 'wall_base_offset_unsupported'
GLOBAL_ID_MISSING = 'global_id_missing'
DUPLICATE_GLOBAL_ID = 'duplicate_global_id'
MISSING_DATA = 'missing_data'
INVALID_GEOMETRY = 'invalid_geometry'
LEVEL_UNKNOWN = 'level_unknown'
HOST_UNKNOWN = 'host_unknown'
OPENING_OUTSIDE_HOST = 'opening_outside_host'
ROOM_NOT_RECTANGULAR = 'room_not_rectangular'
UNSUPPORTED_REPRESENTATION = 'unsupported_representation'
WALL_AXIS_AMBIGUOUS = 'wall_axis_ambiguous'
WALL_AXIS_BODY_MISMATCH = 'wall_axis_body_mismatch'
OPENING_NOT_ALIGNED = 'opening_not_aligned_with_host'
OPENING_PARTIAL_DEPTH = 'opening_partial_depth'
OPENING_DIMENSION_CONFLICT = 'opening_dimension_conflict'
LEVEL_ELEVATION_CONFLICT = 'level_elevation_conflict'
SPACE_BASE_OFFSET = 'space_base_offset_unsupported'
IDENTITY_MISMATCH = 'identity_mismatch'
HOST_LEVEL_MISMATCH = 'host_level_mismatch'
OPENING_MULTIPLE_FILLS = 'opening_multiple_fills'
COORDINATE_OUT_OF_RANGE = 'coordinate_out_of_range'


class IfcAdapterError(ValueError):
    """Whole-file refusal with a stable `code` (export input, unreadable IFC, unsupported units)."""

    def __init__(self, code, detail=''):
        super().__init__(f'{code}: {detail}' if detail else code)
        self.code = code
        self.detail = detail


class Unmappable(Exception):
    """One element cannot be represented in BIM-IR; it is listed under unmapped, never approximated."""

    def __init__(self, reason, detail):
        super().__init__(detail)
        self.reason = reason
        self.detail = detail


# ---------------------------------------------------------------- vectors and frames (metres)

def _dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _norm(v):
    return math.sqrt(_dot(v, v))


def _unit(v, what):
    n = _norm(v)
    if not math.isfinite(n) or n == 0:
        raise Unmappable(INVALID_GEOMETRY, f'{what} has a zero or non-finite direction')
    return (v[0] / n, v[1] / n, v[2] / n)


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


IDENTITY = ((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0), (0.0, 0.0, 0.0))


def _apply(frame, p):
    x, y, z, t = frame
    return tuple(x[i] * p[0] + y[i] * p[1] + z[i] * p[2] + t[i] for i in range(3))


def _rotate(frame, v):
    x, y, z, _ = frame
    return tuple(x[i] * v[0] + y[i] * v[1] + z[i] * v[2] for i in range(3))


def _compose(parent, child):
    x, y, z, t = child
    return (_rotate(parent, x), _rotate(parent, y), _rotate(parent, z), _apply(parent, t))


class Reader:
    """Unit-aware geometry access for one opened IFC file."""

    def __init__(self, f):
        self.f = f
        self.divisor, self.factor = _length_unit(f)

    def m(self, value):
        value = float(value)
        if not math.isfinite(value):
            raise Unmappable(INVALID_GEOMETRY, 'non-finite coordinate')
        return value / self.divisor if self.divisor else value * self.factor

    def point(self, p):
        c = [self.m(v) for v in p.Coordinates] + [0.0, 0.0]
        return (c[0], c[1], c[2])

    def axis_placement(self, a):
        """IfcAxis2Placement3D or 2D as a frame (x, y, z, origin); orthonormalised like the IFC definition."""
        if a is None:
            return IDENTITY
        if a.is_a('IfcAxis2Placement3D'):
            z = _unit(_ratios(a.Axis) if a.Axis else (0.0, 0.0, 1.0), 'placement axis')
            ref = _ratios(a.RefDirection) if a.RefDirection else (1.0, 0.0, 0.0)
            d = _dot(ref, z)
            x = _unit((ref[0] - d * z[0], ref[1] - d * z[1], ref[2] - d * z[2]), 'placement reference direction')
            return (x, _cross(z, x), z, self.point(a.Location))
        if a.is_a('IfcAxis2Placement2D'):
            ref = _ratios(a.RefDirection) if a.RefDirection else (1.0, 0.0, 0.0)
            x = _unit((ref[0], ref[1], 0.0), 'profile reference direction')
            return (x, (-x[1], x[0], 0.0), (0.0, 0.0, 1.0), self.point(a.Location))
        raise Unmappable(UNSUPPORTED_REPRESENTATION, f'{a.is_a()} placements are not read')

    def placement(self, p, depth=0):
        if p is None:
            return IDENTITY
        if not p.is_a('IfcLocalPlacement') or depth > 64:
            raise Unmappable(UNSUPPORTED_REPRESENTATION, f'{p.is_a()} object placements are not read (local placements only)')
        return _compose(self.placement(p.PlacementRelTo, depth + 1), self.axis_placement(p.RelativePlacement))

    def body(self, product, what):
        """The single extruded rectangle of a product's Body: world frame centred on the profile, sizes, depth."""
        shapes = product.Representation.Representations if product.Representation else ()
        bodies = [r for r in shapes if r.RepresentationIdentifier == 'Body']
        if not bodies and len(shapes) == 1 and shapes[0].RepresentationIdentifier is None:
            bodies = list(shapes)
        if len(bodies) != 1:
            raise Unmappable(UNSUPPORTED_REPRESENTATION, f'{what} needs exactly one Body representation, found {len(bodies)}')
        items = bodies[0].Items
        if len(items) != 1 or items[0].is_a() != 'IfcExtrudedAreaSolid':
            raise Unmappable(UNSUPPORTED_REPRESENTATION, f'{what} body is not one IfcExtrudedAreaSolid ({", ".join(i.is_a() for i in items)})')
        solid = items[0]
        profile = solid.SweptArea
        if profile.is_a() != 'IfcRectangleProfileDef' or profile.ProfileType != 'AREA':
            raise Unmappable(UNSUPPORTED_REPRESENTATION, f'{what} body profile is {profile.is_a()}, not an AREA IfcRectangleProfileDef')
        direction = _unit(_ratios(solid.ExtrudedDirection), 'extrusion direction')
        if abs(direction[0]) > 1e-12 or abs(direction[1]) > 1e-12 or direction[2] <= 0:
            raise Unmappable(UNSUPPORTED_REPRESENTATION, f'{what} body is not extruded along its local +Z')
        frame = _compose(_compose(self.placement(product.ObjectPlacement), self.axis_placement(solid.Position)), self.axis_placement(profile.Position))
        x, y, z, origin = frame
        if abs(z[0]) > 1e-9 or abs(z[1]) > 1e-9 or z[2] < 1 - 1e-9:
            raise Unmappable(UNSUPPORTED_REPRESENTATION, f'{what} body is not extruded vertically upwards')
        xdim, ydim, depth = self.m(profile.XDim), self.m(profile.YDim), self.m(solid.Depth)
        if xdim <= 0 or ydim <= 0 or depth <= 0:
            raise Unmappable(INVALID_GEOMETRY, f'{what} body has a non-positive size')
        return dict(x=x, y=y, origin=origin, xdim=xdim, ydim=ydim, depth=depth)

    def axis(self, product):
        """Start and end (world, metres) of a 2-point IfcPolyline Axis representation, or None without one."""
        shapes = product.Representation.Representations if product.Representation else ()
        axes = [r for r in shapes if r.RepresentationIdentifier == 'Axis']
        if not axes:
            return None
        if len(axes) != 1 or len(axes[0].Items) != 1 or axes[0].Items[0].is_a() != 'IfcPolyline' or len(axes[0].Items[0].Points) != 2:
            raise Unmappable(UNSUPPORTED_REPRESENTATION, 'wall Axis representation is not one straight 2-point IfcPolyline')
        frame = self.placement(product.ObjectPlacement)
        return [_apply(frame, self.point(p)) for p in axes[0].Items[0].Points]


def _ratios(direction):
    r = [float(v) for v in direction.DirectionRatios] + [0.0, 0.0]
    return (r[0], r[1], r[2])


def _length_unit(f):
    """(divisor, factor): metres = value / divisor when the unit is an exact SI submultiple, else value * factor."""
    projects = f.by_type('IfcProject')
    assignment = projects[0].UnitsInContext if projects else None
    units = [u for u in (getattr(assignment, 'Units', None) or ()) if getattr(u, 'UnitType', None) == 'LENGTHUNIT']
    if not units:
        return 1, 1.0  # Like ids_check: a model without a length unit is treated as SI.
    unit = units[0]
    if unit.is_a('IfcSIUnit') and getattr(unit, 'Name', None) == 'METRE':
        if unit.Prefix in SI_PREFIX:
            return SI_PREFIX[unit.Prefix], 1.0
        if unit.Prefix in SI_MULTIPLE:
            return None, SI_MULTIPLE[unit.Prefix]
    if unit.is_a('IfcConversionBasedUnit'):
        factor = getattr(unit, 'ConversionFactor', None)
        component = getattr(factor, 'UnitComponent', None)
        value = getattr(getattr(factor, 'ValueComponent', None), 'wrappedValue', None)
        if (component is not None and component.is_a('IfcSIUnit') and getattr(component, 'Name', None) == 'METRE' and getattr(component, 'Prefix', None) in SI_PREFIX
                and type(value) in (int, float) and math.isfinite(value) and value > 0):
            return None, float(value) / SI_PREFIX[component.Prefix]
    raise IfcAdapterError('ifc_units_unsupported', f'length unit {unit.is_a()} {getattr(unit, "Prefix", None)} {getattr(unit, "Name", None)} is not read')


def _snap(value):
    snapped = round(value, DECIMALS) + 0.0
    if not math.isfinite(snapped):
        raise Unmappable(INVALID_GEOMETRY, 'non-finite value')
    if abs(snapped) > MAX_COORDINATE:
        raise Unmappable(COORDINATE_OUT_OF_RANGE, f'value {snapped} m is beyond +/-{MAX_COORDINATE} m, where the nanometre grid is no longer exact')
    return snapped


def _horizontal(v):
    return abs(v[2]) <= 1e-9


# ---------------------------------------------------------------- identity

def _uuid_from_bytes(data):
    # Same derivation style as Core RoomCompiler.Id and MirrorSnapshotMapper.Id: new Guid(first 16 bytes of SHA-256).
    return str(uuid.UUID(bytes_le=hashlib.sha256(data.encode('utf-8')).digest()[:16]))


def derived_id(project_gid, gid):
    """Stable BIM-IR id for an IFC element without BIM-IR identity. GlobalIds are case-sensitive, so no folding."""
    return _uuid_from_bytes(ID_NAMESPACE + '\n' + project_gid + '\n' + gid)


def _expand(gid):
    try:
        return str(uuid.UUID(ifcopenshell.guid.expand(gid)))
    except Exception:
        return None


def _compress(identifier):
    return ifcopenshell.guid.compress(uuid.UUID(identifier).hex)


def _pset(entity, name):
    return ifcopenshell.util.element.get_psets(entity).get(name, {})


def _identity(product, project_gid):
    """(BIM-IR id, provenance) of a product: its own BIM-IR id when it carries one, else a derived id.
    Provenance is always ifc-import/observed: an unsigned IFC property cannot carry an approval, so any
    Source/Approval found in a property set is ignored rather than honoured."""
    owned = _pset(product, ELEMENT_PSET)
    if 'Id' not in owned:
        return derived_id(project_gid, product.GlobalId), dict(OBSERVED)
    if owned['Id'] != _expand(product.GlobalId):
        raise Unmappable(IDENTITY_MISMATCH, f'{ELEMENT_PSET}.Id {owned["Id"]} does not match the GlobalId (a copied element keeps another element\'s BIM-IR identity)')
    return owned['Id'], dict(OBSERVED)


# ---------------------------------------------------------------- import

def _category(product):
    for ifc_class, category in (('IfcBuildingStorey', 'level'), ('IfcWall', 'wall'), ('IfcDoor', 'door'), ('IfcWindow', 'window'), ('IfcSpace', 'space'), ('IfcOpeningElement', 'opening')):
        if product.is_a(ifc_class):
            return category
    return product.is_a()


def _storey_of(product, levels):
    """BIM-IR id of the mapped storey containing (or, for spaces, aggregating) the product, or None."""
    rels = list(getattr(product, 'ContainedInStructure', None) or ()) if not product.is_a('IfcSpace') else []
    structures = [r.RelatingStructure for r in rels]
    if product.is_a('IfcSpace'):
        structures = [r.RelatingObject for r in (product.Decomposes or ())] or [r.RelatingStructure for r in (getattr(product, 'ContainedInStructure', None) or ())]
    if len(structures) != 1 or structures[0].id() not in levels:
        return None
    return levels[structures[0].id()]


def import_ifc(path):
    """Read an IFC file into a BIM-IR Project plus an unmapped list. Returns the bim.ifc-import/1 report."""
    try:
        f = ifcopenshell.open(str(path))
    except Exception as error:
        raise IfcAdapterError('ifc_unreadable', f'{type(error).__name__}: {error}') from None
    projects = f.by_type('IfcProject')
    if len(projects) != 1:
        raise IfcAdapterError('ifc_project_count', f'expected one IfcProject, found {len(projects)}')
    project = projects[0]
    project_gid = project.GlobalId
    if not isinstance(project_gid, str) or not project_gid:
        raise IfcAdapterError('ifc_project_identity', 'IfcProject has no GlobalId')
    reader = Reader(f)
    products = sorted(f.by_type('IfcProduct'), key=lambda p: p.id())
    if len(products) > MAX_ELEMENTS:
        raise IfcAdapterError('ifc_too_large', f'{len(products)} products; at most {MAX_ELEMENTS} are imported, the file is refused instead of truncated')
    try:
        owned = _pset(project, PROJECT_PSET)
    except Exception as error:
        raise IfcAdapterError('ifc_project_revision', f'{PROJECT_PSET} is unreadable: {type(error).__name__}: {error}') from None
    if owned.get('Id') is not None and owned.get('Id') == _expand(project_gid):
        revision = owned.get('Revision')
        if type(revision) is not int or not -2**31 <= revision < 2**31:
            raise IfcAdapterError('ifc_project_revision', f'{PROJECT_PSET}.Revision {revision!r} is not a 32-bit integer')
        project_id = owned['Id']
    else:
        project_id, revision = derived_id(project_gid, project_gid), 0

    mapped, unmapped = [], []

    def skip(product, reason, detail):
        unmapped.append({'category': _category(product), 'global_id': product.GlobalId or '', 'ifc_class': product.is_a(), 'reason': reason, 'detail': detail})

    counts = {}
    for p in products:
        counts[p.GlobalId] = counts.get(p.GlobalId, 0) + 1
    candidates = {k: [] for k in KINDS}
    for p in products:
        category = _category(p)
        if p.is_a('IfcSite') or p.is_a('IfcBuilding'):
            continue  # Spatial structure above storeys; BIM-IR has no element for it and nothing depends on it.
        if category == 'opening':
            if not p.HasFillings:
                skip(p, NO_BIM_IR_KIND, 'opening without a door or window fill; BIM-IR has no bare opening or void kind')
            continue  # A filled opening is read as part of its door or window.
        if not p.GlobalId:
            skip(p, GLOBAL_ID_MISSING, 'element has no GlobalId, so no stable id can be derived')
        elif counts[p.GlobalId] > 1:
            skip(p, DUPLICATE_GLOBAL_ID, 'GlobalId appears on more than one product in the file')
        elif category in candidates:
            candidates[category].append(p)
        elif p.is_a('IfcSlab') or p.is_a('IfcRoof') or p.is_a('IfcCovering'):
            skip(p, NO_BIM_IR_KIND, f'{p.is_a()}: BIM-IR has no slab, floor, roof or covering kind; adding one needs an owner-accepted ADR')
        else:
            skip(p, CATEGORY_UNSUPPORTED, f'{p.is_a()} is not read (storeys, walls, doors, windows and spaces are)')

    def attempt(product, kind, read):
        try:
            identifier, provenance = _identity(product, project_gid)
            geometry, dependencies, extra = read(product)
        except Unmappable as problem:
            skip(product, problem.reason, problem.detail)
            return None
        except (AttributeError, TypeError, ValueError, IndexError, ZeroDivisionError) as error:
            skip(product, INVALID_GEOMETRY, f'unreadable {kind}: {type(error).__name__}: {error}')
            return None
        mapped.append(({'Id': identifier, 'Kind': kind, 'GeometryMetres': geometry, 'Dependencies': dependencies, 'Provenance': provenance}, product))
        return identifier, extra

    levels, elevations = {}, {}

    def read_level(storey):
        z = None
        if storey.ObjectPlacement is not None:
            z = reader.placement(storey.ObjectPlacement)[3][2]
        if storey.Elevation is not None:
            attribute = reader.m(storey.Elevation)
            if z is not None and abs(attribute - z) > TOLERANCE:
                raise Unmappable(LEVEL_ELEVATION_CONFLICT, f'Elevation {attribute} m differs from the placement height {z} m')
            z = attribute if z is None else z
        if z is None:
            raise Unmappable(MISSING_DATA, 'storey has neither a placement nor an Elevation')
        return [_snap(z)], [], None

    for storey in candidates['level']:
        result = attempt(storey, 'level', read_level)
        if result:
            levels[storey.id()] = result[0]
            elevations[result[0]] = next(e['GeometryMetres'][0] for e, _ in mapped if e['Id'] == result[0])

    walls = {}

    def read_wall(wall):
        level = _storey_of(wall, levels)
        if level is None:
            raise Unmappable(LEVEL_UNKNOWN, 'wall is not contained in exactly one mapped storey')
        body = reader.body(wall, 'wall')
        x, y, c = body['x'], body['y'], body['origin']
        if not _horizontal(x) or not _horizontal(y):
            raise Unmappable(UNSUPPORTED_REPRESENTATION, 'wall profile plane is not horizontal')
        ends = reader.axis(wall)
        if ends is None:
            if abs(body['xdim'] - body['ydim']) <= TOLERANCE:
                raise Unmappable(WALL_AXIS_AMBIGUOUS, 'square wall footprint without an Axis representation: the axis direction cannot be told from the thickness')
            direction, length, thickness = (x, body['xdim'], body['ydim']) if body['xdim'] > body['ydim'] else (y, body['ydim'], body['xdim'])
            half = length / 2
            ends = [(c[0] - half * direction[0], c[1] - half * direction[1]), (c[0] + half * direction[0], c[1] + half * direction[1])]
        else:
            a, b = ends
            if abs(a[2] - b[2]) > TOLERANCE:
                raise Unmappable(WALL_AXIS_BODY_MISMATCH, 'wall Axis is not horizontal')
            span = (b[0] - a[0], b[1] - a[1], 0.0)
            length = _norm(span)
            direction = _unit(span, 'wall axis')
            if abs(abs(_dot(direction, x)) - 1) <= 1e-9:
                along, thickness = body['xdim'], body['ydim']
            elif abs(abs(_dot(direction, y)) - 1) <= 1e-9:
                along, thickness = body['ydim'], body['xdim']
            else:
                raise Unmappable(WALL_AXIS_BODY_MISMATCH, 'wall body is not aligned with its Axis')
            mid = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
            if abs(along - length) > TOLERANCE or math.hypot(mid[0] - c[0], mid[1] - c[1]) > TOLERANCE:
                raise Unmappable(WALL_AXIS_BODY_MISMATCH, 'wall body is not centred on its Axis or has another length')
            ends = [(a[0], a[1]), (b[0], b[1])]
        if abs(c[2] - elevations[level]) > TOLERANCE:
            raise Unmappable(WALL_BASE_OFFSET, f'wall base {c[2]} m is not at its storey elevation {elevations[level]} m; BIM-IR walls start at their level')
        geometry = [_snap(ends[0][0]), _snap(ends[0][1]), _snap(ends[1][0]), _snap(ends[1][1]), _snap(body['depth']), _snap(thickness)]
        if geometry[4] <= 0 or geometry[5] <= 0 or (geometry[0] == geometry[2] and geometry[1] == geometry[3]):
            raise Unmappable(INVALID_GEOMETRY, 'wall axis has zero length or height or thickness is not positive')
        extra = dict(a=ends[0], b=ends[1], thickness=thickness, base=c[2], top=c[2] + body['depth'], level=level)
        return geometry, [level], extra

    for wall in candidates['wall']:
        result = attempt(wall, 'wall', read_wall)
        if result:
            walls[wall.id()] = result

    def read_opening(product, kind):
        level = _storey_of(product, levels)
        if level is None:
            raise Unmappable(LEVEL_UNKNOWN, f'{kind} is not contained in exactly one mapped storey')
        fills = product.FillsVoids or ()
        if len(fills) != 1:
            raise Unmappable(HOST_UNKNOWN, f'{kind} does not fill exactly one opening (IfcRelFillsElement)')
        opening = fills[0].RelatingOpeningElement
        if len(opening.HasFillings or ()) != 1:
            raise Unmappable(OPENING_MULTIPLE_FILLS, f'its opening is filled by {len(opening.HasFillings)} elements; one BIM-IR {kind} per opening cannot say which one is real')
        voids = opening.VoidsElements or ()
        if len(voids) != 1 or voids[0].RelatingBuildingElement.id() not in walls:
            raise Unmappable(HOST_UNKNOWN, f'{kind} opening does not void exactly one mapped wall')
        host_id, host = walls[voids[0].RelatingBuildingElement.id()]
        if host['level'] != level:
            raise Unmappable(HOST_LEVEL_MISMATCH, f'{kind} is contained in another storey than its host wall; BIM-IR openings share their host level')
        body = reader.body(opening, f'{kind} opening')
        x, y, c = body['x'], body['y'], body['origin']
        if not _horizontal(x) or not _horizontal(y):
            raise Unmappable(UNSUPPORTED_REPRESENTATION, f'{kind} opening profile plane is not horizontal')
        a, b = host['a'], host['b']
        length = math.hypot(b[0] - a[0], b[1] - a[1])
        u = _unit((b[0] - a[0], b[1] - a[1], 0.0), 'host axis')
        if abs(abs(_dot(u, x)) - 1) <= 1e-9:
            along, across = body['xdim'], body['ydim']
        elif abs(abs(_dot(u, y)) - 1) <= 1e-9:
            along, across = body['ydim'], body['xdim']
        else:
            raise Unmappable(OPENING_NOT_ALIGNED, f'{kind} opening is not aligned with its host wall axis')
        rel = (c[0] - a[0], c[1] - a[1])
        if abs(rel[0] * u[1] - rel[1] * u[0]) > TOLERANCE:
            raise Unmappable(OPENING_OUTSIDE_HOST, f'{kind} opening is not centred on its host wall axis')
        if across < host['thickness'] - TOLERANCE:
            raise Unmappable(OPENING_PARTIAL_DEPTH, f'{kind} opening is thinner than its host wall (a recess, not a through opening)')
        s = rel[0] * u[0] + rel[1] * u[1]
        start, end = s - along / 2, s + along / 2
        sill, height = c[2] - elevations[level], body['depth']
        if start < -TOLERANCE or end > length + TOLERANCE or c[2] + height > host['top'] + TOLERANCE or c[2] < host['base'] - TOLERANCE:
            raise Unmappable(OPENING_OUTSIDE_HOST, f'{kind} opening does not fit inside its host wall')
        if sill < -TOLERANCE:
            raise Unmappable(INVALID_GEOMETRY, f'{kind} sill is below its level')
        if kind == 'door' and abs(sill) > TOLERANCE:
            raise Unmappable(INVALID_GEOMETRY, 'door sill is not 0; BIM-IR doors sit on their level')
        for attribute, expected in (('OverallWidth', along), ('OverallHeight', height)):
            value = getattr(product, attribute, None)
            if value is not None and abs(reader.m(value) - expected) > TOLERANCE:
                raise Unmappable(OPENING_DIMENSION_CONFLICT, f'{kind} {attribute} {reader.m(value)} m differs from its opening ({expected} m)')
        geometry = [_snap(a[0] + start * u[0]), _snap(a[1] + start * u[1]), _snap(a[0] + end * u[0]), _snap(a[1] + end * u[1]), _snap(sill), _snap(height)]
        return geometry, [level, host_id], None

    for kind in ('door', 'window'):
        for product in candidates[kind]:
            attempt(product, kind, lambda p, k=kind: read_opening(p, k))

    def read_space(space):
        level = _storey_of(space, levels)
        if level is None:
            raise Unmappable(LEVEL_UNKNOWN, 'space is not aggregated into exactly one mapped storey')
        body = reader.body(space, 'space')
        x, y, c = body['x'], body['y'], body['origin']
        if not (_horizontal(x) and (abs(abs(x[0]) - 1) <= 1e-9 or abs(abs(x[1]) - 1) <= 1e-9)):
            raise Unmappable(ROOM_NOT_RECTANGULAR, 'space footprint is not an axis-aligned rectangle (a rotated rectangle is refused too)')
        half_x = (abs(x[0]) * body['xdim'] + abs(y[0]) * body['ydim']) / 2
        half_y = (abs(x[1]) * body['xdim'] + abs(y[1]) * body['ydim']) / 2
        if abs(c[2] - elevations[level]) > TOLERANCE:
            raise Unmappable(SPACE_BASE_OFFSET, f'space base {c[2]} m is not at its storey elevation {elevations[level]} m')
        bounding = sorted({walls[r.RelatedBuildingElement.id()][0] for r in (space.BoundedBy or ()) if r.RelatedBuildingElement is not None and r.RelatedBuildingElement.id() in walls})
        geometry = [_snap(c[0] - half_x), _snap(c[1] - half_y), _snap(2 * half_x), _snap(2 * half_y)]
        return geometry, [level, *bounding], None

    for space in candidates['space']:
        attempt(space, 'space', read_space)

    mapped.sort(key=lambda item: (RANK[item[0]['Kind']], item[0]['Id']))
    ids = [e['Id'] for e, _ in mapped]
    if len(set(ids)) != len(ids):
        raise IfcAdapterError('duplicate_id', 'two IFC elements map to the same BIM-IR id')
    unmapped.sort(key=lambda u: (u['category'], u['global_id'], u['reason'], u['ifc_class']))
    return {
        'import_version': 1, 'report_schema': REPORT_SCHEMA, 'ifc_schema': f.schema, 'units': 'm',
        'project': {'Id': project_id, 'Revision': revision, 'Elements': [e for e, _ in mapped]},
        'sources': [{'id': e['Id'], 'global_id': p.GlobalId, 'ifc_class': p.is_a()} for e, p in mapped],
        'unmapped': unmapped,
    }


# ---------------------------------------------------------------- export

def _require(condition, code, detail):
    if not condition:
        raise IfcAdapterError(code, detail)


def _text(value, where):
    _require(isinstance(value, str), 'bim_ir_invalid', f'{where} must be a string')
    try:
        value.encode('utf-8')
    except UnicodeEncodeError:
        raise IfcAdapterError('bim_ir_invalid', f'{where} is not valid Unicode') from None
    return value


def validate_project(project):
    """Core shape and rules, then the stricter shapes this exporter can write losslessly. Returns (supported, unexported)."""
    _require(isinstance(project, dict) and set(project) == {'Id', 'Revision', 'Elements'}, 'bim_ir_invalid', 'Project needs exactly Id, Revision, Elements')
    _require(isinstance(project['Id'], str) and UUID_RE.match(project['Id']), 'bim_ir_invalid', 'Project.Id must be a lowercase UUID string')
    revision = project['Revision']
    _require(type(revision) is int and -2**31 <= revision < 2**31, 'bim_ir_invalid', 'Project.Revision must be a 32-bit integer')
    _require(isinstance(project['Elements'], list), 'bim_ir_invalid', 'Project.Elements must be an array')
    for index, e in enumerate(project['Elements']):
        where = f'Elements[{index}]'
        _require(isinstance(e, dict) and set(e) == {'Id', 'Kind', 'GeometryMetres', 'Dependencies', 'Provenance'}, 'bim_ir_invalid', f'{where} needs exactly Id, Kind, GeometryMetres, Dependencies, Provenance')
        _require(isinstance(e['Id'], str) and UUID_RE.match(e['Id']), 'bim_ir_invalid', f'{where}.Id must be a lowercase UUID string')
        _text(e['Kind'], f'{where}.Kind')
        _require(isinstance(e['GeometryMetres'], list) and all(type(v) in (int, float) for v in e['GeometryMetres']), 'bim_ir_invalid', f'{where}.GeometryMetres must be an array of numbers')
        _require(isinstance(e['Dependencies'], list) and all(isinstance(d, str) and UUID_RE.match(d) for d in e['Dependencies']), 'bim_ir_invalid', f'{where}.Dependencies must be lowercase UUID strings')
        p = e['Provenance']
        _require(isinstance(p, dict) and set(p) == {'Source', 'Approval'}, 'bim_ir_invalid', f'{where}.Provenance needs exactly Source and Approval')
        _text(p['Source'], f'{where}.Provenance.Source')
        _text(p['Approval'], f'{where}.Provenance.Approval')
    elements = project['Elements']
    ids = [e['Id'] for e in elements]
    _require(len(set(ids)) == len(ids), 'duplicate_id', 'Duplicate logical identity')
    by_id = {e['Id']: e for e in elements}
    supported, unexported = [], []
    for e in elements:
        _require(all(d in by_id for d in e['Dependencies']), 'missing_dependency', f"{e['Id']} has a dangling dependency")
        if e['Kind'] in DOCUMENTATION:
            unexported.append({'id': e['Id'], 'kind': e['Kind'], 'reason': 'no_ifc_entity'})
            continue
        _require(e['Kind'] in KINDS, 'geometry_kind_unsupported', f"{e['Id']} kind {e['Kind']!r} has no IFC mapping")
        g = [float(v) for v in e['GeometryMetres']]
        _require(len(g) == ARITY[e['Kind']], f"{e['Kind'] if e['Kind'] in ('wall', 'level', 'space') else 'opening'}_geometry", f"{e['Id']} expects {ARITY[e['Kind']]} values")
        _require(all(math.isfinite(v) for v in g), 'geometry_non_finite', e['Id'])
        _require(all(abs(v) <= MAX_COORDINATE and round(v, DECIMALS) == v for v in g), 'ifc_precision', f"{e['Id']} has a value off the nanometre grid or beyond {MAX_COORDINATE} m")
        supported.append(e)

    def kind(identifier):
        return by_id[identifier]['Kind']

    for e in supported:
        g, deps = [float(v) for v in e['GeometryMetres']], e['Dependencies']
        if e['Kind'] == 'level':
            _require(deps == [], 'ifc_dependency_shape', f"level {e['Id']} must have no dependencies")
        elif e['Kind'] == 'wall':
            _require(len(deps) == 1 and kind(deps[0]) == 'level', 'ifc_dependency_shape', f"wall {e['Id']} must depend on exactly its level")
            _require(g[4] > 0 and g[5] > 0 and not (g[0] == g[2] and g[1] == g[3]), 'wall_geometry', f"{e['Id']} Invalid wall axis/height/thickness")
        elif e['Kind'] in ('door', 'window'):
            _require(len(deps) == 2 and kind(deps[0]) == 'level' and kind(deps[1]) == 'wall', 'ifc_dependency_shape', f"{e['Kind']} {e['Id']} must depend on [level, host wall]")
            host = by_id[deps[1]]
            _require(host['Dependencies'] == [deps[0]], 'ifc_dependency_shape', f"{e['Kind']} {e['Id']} level differs from its host wall level")
            _opening_on_host(e, [float(v) for v in host['GeometryMetres']])
        else:
            walls = deps[1:]
            _require(len(deps) >= 1 and kind(deps[0]) == 'level' and all(kind(w) == 'wall' for w in walls) and walls == sorted(set(walls)), 'ifc_dependency_shape', f"space {e['Id']} must depend on [level, bounding walls in id order]")
            _require(g[2] > 0 and g[3] > 0, 'ifc_space_geometry', f"space {e['Id']} width and depth must be positive")
            _require(abs(g[0] + g[2]) <= MAX_COORDINATE and abs(g[1] + g[3]) <= MAX_COORDINATE, 'ifc_precision', f"space {e['Id']} far corner is beyond {MAX_COORDINATE} m")
    return supported, unexported


def _opening_on_host(e, h):
    g = [float(v) for v in e['GeometryMetres']]
    ax, ay, bx, by = h[:4]
    length = math.hypot(bx - ax, by - ay)
    ux, uy = (bx - ax) / length, (by - ay) / length
    s0 = (g[0] - ax) * ux + (g[1] - ay) * uy
    s1 = (g[2] - ax) * ux + (g[3] - ay) * uy
    off = max(abs((g[0] - ax) * uy - (g[1] - ay) * ux), abs((g[2] - ax) * uy - (g[3] - ay) * ux))
    ok = (off <= TOLERANCE and s1 - s0 > TOLERANCE and s0 >= -TOLERANCE and s1 <= length + TOLERANCE
          and g[4] >= 0 and g[5] > 0 and g[4] + g[5] <= h[4] + TOLERANCE and (e['Kind'] == 'window' or g[4] == 0))
    _require(ok, 'ifc_opening_geometry', f"{e['Kind']} {e['Id']} must lie on its host axis in the host direction, inside the wall, with positive size (doors at sill 0)")


class _Writer:
    def __init__(self, project_id):
        self.f = ifcopenshell.file(schema='IFC4')
        self.project_id = project_id

    def gid(self, key):
        return _compress(_uuid_from_bytes(EXPORT_NAMESPACE + '\n' + self.project_id + '\n' + key))

    def root(self, kind, gid, name, **kw):
        return self.f.create_entity(kind, GlobalId=gid, Name=name, **kw)

    def point(self, xyz):
        return self.f.create_entity('IfcCartesianPoint', Coordinates=tuple(float(v) for v in xyz))

    def placement(self, xyz, parent=None):
        return self.f.create_entity('IfcLocalPlacement', PlacementRelTo=parent, RelativePlacement=self.f.create_entity('IfcAxis2Placement3D', Location=self.point(xyz)))

    def box(self, context, centre, direction, xdim, ydim, base, depth):
        profile_position = self.f.create_entity('IfcAxis2Placement2D', Location=self.point(centre),
                                                RefDirection=self.f.create_entity('IfcDirection', DirectionRatios=tuple(float(v) for v in direction)) if direction else None)
        profile = self.f.create_entity('IfcRectangleProfileDef', ProfileType='AREA', Position=profile_position, XDim=float(xdim), YDim=float(ydim))
        solid = self.f.create_entity('IfcExtrudedAreaSolid', SweptArea=profile, Position=self.f.create_entity('IfcAxis2Placement3D', Location=self.point((0, 0, base))),
                                     ExtrudedDirection=self.f.create_entity('IfcDirection', DirectionRatios=(0., 0., 1.)), Depth=float(depth))
        return self.f.create_entity('IfcShapeRepresentation', ContextOfItems=context, RepresentationIdentifier='Body', RepresentationType='SweptSolid', Items=[solid])

    def props(self, entity, name, values):
        properties = [self.f.create_entity('IfcPropertySingleValue', Name=k, NominalValue=self.f.create_entity('IfcInteger' if type(v) is int else 'IfcText', v)) for k, v in values.items()]
        key = name + ':' + entity.GlobalId
        pset = self.root('IfcPropertySet', self.gid('pset:' + key), name, HasProperties=properties)
        self.root('IfcRelDefinesByProperties', self.gid('defines:' + key), name, RelatedObjects=[entity], RelatingPropertyDefinition=pset)


def build_model(project):
    supported, unexported = validate_project(project)
    supported = sorted(supported, key=lambda e: (RANK[e['Kind']], e['Id']))
    by_id = {e['Id']: e for e in supported}
    w = _Writer(project['Id'])
    f = w.f
    context = f.create_entity('IfcGeometricRepresentationContext', ContextIdentifier='Model', ContextType='Model', CoordinateSpaceDimension=3, Precision=1e-9,
                              WorldCoordinateSystem=f.create_entity('IfcAxis2Placement3D', Location=w.point((0, 0, 0))))
    units = f.create_entity('IfcUnitAssignment', Units=[f.create_entity('IfcSIUnit', UnitType='LENGTHUNIT', Name='METRE'),
                                                        f.create_entity('IfcSIUnit', UnitType='AREAUNIT', Name='SQUARE_METRE'),
                                                        f.create_entity('IfcSIUnit', UnitType='VOLUMEUNIT', Name='CUBIC_METRE')])
    ifc_project = w.root('IfcProject', _compress(project['Id']), 'BIM-IR project', RepresentationContexts=[context], UnitsInContext=units)
    site = w.root('IfcSite', w.gid('site'), 'Site', CompositionType='ELEMENT', ObjectPlacement=w.placement((0, 0, 0)))
    building = w.root('IfcBuilding', w.gid('building'), 'Building', CompositionType='ELEMENT', ObjectPlacement=w.placement((0, 0, 0), site.ObjectPlacement))
    w.root('IfcRelAggregates', w.gid('aggregates:project'), 'Project site', RelatingObject=ifc_project, RelatedObjects=[site])
    w.root('IfcRelAggregates', w.gid('aggregates:site'), 'Site building', RelatingObject=site, RelatedObjects=[building])
    entity, contents, spaces = {}, {}, {}
    for e in supported:
        g = [float(v) for v in e['GeometryMetres']]
        name = e['Id']
        if e['Kind'] == 'level':
            entity[name] = w.root('IfcBuildingStorey', _compress(name), name, CompositionType='ELEMENT', Elevation=g[0], ObjectPlacement=w.placement((0, 0, g[0]), building.ObjectPlacement))
            contents[name], spaces[name] = [], []
            continue
        level = e['Dependencies'][0]
        storey = entity[level].ObjectPlacement
        if e['Kind'] == 'wall':
            ax, ay, bx, by, height, thickness = g
            length = math.hypot(bx - ax, by - ay)
            axis = f.create_entity('IfcShapeRepresentation', ContextOfItems=context, RepresentationIdentifier='Axis', RepresentationType='Curve2D',
                                   Items=[f.create_entity('IfcPolyline', Points=[w.point((ax, ay)), w.point((bx, by))])])
            body = w.box(context, ((ax + bx) / 2, (ay + by) / 2), ((bx - ax) / length, (by - ay) / length), length, thickness, 0, height)
            entity[name] = w.root('IfcWall', _compress(name), name, ObjectPlacement=w.placement((0, 0, 0), storey), Representation=f.create_entity('IfcProductDefinitionShape', Representations=[axis, body]))
            contents[level].append(entity[name])
        elif e['Kind'] in ('door', 'window'):
            ax, ay, bx, by, sill, height = g
            host = by_id[e['Dependencies'][1]]
            hx, hy = float(host['GeometryMetres'][2]) - float(host['GeometryMetres'][0]), float(host['GeometryMetres'][3]) - float(host['GeometryMetres'][1])
            hl = math.hypot(hx, hy)
            direction, width, thickness = (hx / hl, hy / hl), math.hypot(bx - ax, by - ay), float(host['GeometryMetres'][5])
            centre = ((ax + bx) / 2, (ay + by) / 2)
            opening = w.root('IfcOpeningElement', w.gid('opening:' + name), 'opening ' + name, PredefinedType='OPENING', ObjectPlacement=w.placement((0, 0, 0), storey),
                             Representation=f.create_entity('IfcProductDefinitionShape', Representations=[w.box(context, centre, direction, width, thickness + 2 * OPENING_CLEARANCE, sill, height)]))
            kind = 'IfcDoor' if e['Kind'] == 'door' else 'IfcWindow'
            entity[name] = w.root(kind, _compress(name), name, OverallWidth=width, OverallHeight=height, ObjectPlacement=w.placement((0, 0, 0), storey),
                                  Representation=f.create_entity('IfcProductDefinitionShape', Representations=[w.box(context, centre, direction, width, thickness, sill, height)]))
            w.root('IfcRelVoidsElement', w.gid('voids:' + name), 'Voids', RelatingBuildingElement=entity[host['Id']], RelatedOpeningElement=opening)
            w.root('IfcRelFillsElement', w.gid('fills:' + name), 'Fills', RelatingOpeningElement=opening, RelatedBuildingElement=entity[name])
            contents[level].append(entity[name])
        else:
            x, y, width, depth = g
            walls = e['Dependencies'][1:]
            height = max((float(by_id[wall]['GeometryMetres'][4]) for wall in walls), default=DEFAULT_SPACE_HEIGHT)
            entity[name] = w.root('IfcSpace', _compress(name), name, CompositionType='ELEMENT', PredefinedType='INTERNAL', ObjectPlacement=w.placement((0, 0, 0), storey),
                                  Representation=f.create_entity('IfcProductDefinitionShape', Representations=[w.box(context, (x + width / 2, y + depth / 2), None, width, depth, 0, height)]))
            spaces[level].append(entity[name])
            for wall in walls:
                w.root('IfcRelSpaceBoundary', w.gid('boundary:' + name + ':' + wall), 'Boundary', RelatingSpace=entity[name], RelatedBuildingElement=entity[wall],
                       PhysicalOrVirtualBoundary='PHYSICAL', InternalOrExternalBoundary='INTERNAL')
    storeys = [entity[e['Id']] for e in supported if e['Kind'] == 'level']
    if storeys:
        w.root('IfcRelAggregates', w.gid('aggregates:building'), 'Building storeys', RelatingObject=building, RelatedObjects=storeys)
    for level in sorted(contents):
        if contents[level]:
            w.root('IfcRelContainedInSpatialStructure', w.gid('contains:' + level), 'Storey contents', RelatedElements=contents[level], RelatingStructure=entity[level])
        if spaces[level]:
            w.root('IfcRelAggregates', w.gid('spaces:' + level), 'Storey spaces', RelatingObject=entity[level], RelatedObjects=spaces[level])
    w.props(ifc_project, PROJECT_PSET, {'Id': project['Id'], 'Revision': project['Revision']})
    for e in supported:
        w.props(entity[e['Id']], ELEMENT_PSET, {'Id': e['Id']})  # Provenance is never written: import cannot trust it.
    return f, supported, unexported


def _schema_check(f):
    logger = ifcopenshell.validate.json_logger()
    ifcopenshell.validate.validate(f, logger, express_rules=True)
    if logger.statements:
        raise IfcAdapterError('ifc_schema_invalid', str(logger.statements)[:2000])


def export_project(project, output_path):
    """Write the BIM-IR project as IFC4. Refuses an existing output; publishes only a file that validates and reimports exactly."""
    output = Path(output_path)
    if output.exists() or output.is_symlink():
        raise IfcAdapterError('output_exists', str(output))
    f, supported, unexported = build_model(project)
    _schema_check(f)
    pending = output.with_suffix('.pending.ifc')
    try:
        with pending.open('x', encoding='utf-8'):
            pass
    except FileExistsError:
        raise IfcAdapterError('output_exists', f'{pending} (staging file left by an earlier run)') from None
    try:
        f.write(str(pending))
        back = import_ifc(pending)
        expected = {'Id': project['Id'], 'Revision': project['Revision'], 'Elements': [dict(e, Provenance=dict(OBSERVED)) for e in supported]}
        if back['unmapped'] or _shaped(back['project']) != _shaped(expected):
            raise IfcAdapterError('ifc_roundtrip_mismatch', 'the written IFC does not read back to the same BIM-IR; nothing was published')
        try:
            os.link(pending, output)  # Fails instead of replacing a file that appeared meanwhile.
        except FileExistsError:
            raise IfcAdapterError('output_exists', f'{output} appeared during export; it was not replaced') from None
    finally:
        if pending.exists():
            pending.unlink()
    return {'status': 'exported', 'path': str(output), 'elements': len(supported), 'unexported': unexported}


def canonical_data(path):
    """The STEP DATA section: the comparison used for export stability. The HEADER carries a write timestamp."""
    text = Path(path).read_bytes()
    start = text.index(b'\nDATA;')
    end = text.index(b'\nENDSEC;', start)
    return text[start:end]


# ---------------------------------------------------------------- Core-shaped JSON

def _core_number(value):
    # System.Text.Json writes integral doubles without a fraction (3, not 3.0), else the shortest round-trip form.
    if isinstance(value, float) and value.is_integer() and abs(value) < 1e15:
        return int(value)
    return value


def _shaped(project):
    return {'Id': project['Id'], 'Revision': project['Revision'], 'Elements': [
        {'Id': e['Id'], 'Kind': e['Kind'], 'GeometryMetres': [_core_number(float(v)) for v in e['GeometryMetres']],
         'Dependencies': list(e['Dependencies']), 'Provenance': {'Source': e['Provenance']['Source'], 'Approval': e['Provenance']['Approval']}}
        for e in project['Elements']]}


def dumps(report):
    return json.dumps(dict(report, project=_shaped(report['project'])), separators=(',', ':'), allow_nan=False)


def main(argv):
    try:
        if len(argv) == 3 and argv[0] == 'export':
            try:
                project = json.loads(Path(argv[1]).read_text(encoding='utf-8-sig'))
            except (OSError, ValueError, RecursionError) as error:
                raise IfcAdapterError('bim_ir_invalid', f'cannot read {argv[1]}: {type(error).__name__}: {error}') from None
            print(json.dumps(export_project(project, argv[2])))
            return 0
        if len(argv) == 2 and argv[0] == 'import':
            print(dumps(import_ifc(argv[1])))
            return 0
        raise IfcAdapterError('usage', 'ifc_import.py export PROJECT_JSON OUTPUT_IFC | import INPUT_IFC')
    except IfcAdapterError as error:
        print(json.dumps({'error': error.code, 'detail': error.detail}), file=sys.stderr)
        return 1
    except Exception as error:  # One JSON error line, never a traceback.
        print(json.dumps({'error': 'internal_error', 'detail': f'{type(error).__name__}: {error}'}), file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
