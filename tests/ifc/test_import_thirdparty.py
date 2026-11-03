"""P8-05: IFC written by other tools (here: IfcOpenShell's own authoring API, ifcopenshell.api) imports to BIM-IR
with an explicit per-entity report (imported / approximated / unsupported) instead of failing.

Every fixture is generated inside the test with ifcopenshell.api (project, units, contexts, spatial tree, types,
placements, voids/fills) plus IfcOpenShell-created geometry items; no IFC binary is committed. Expected geometry
comes either from the hand-built numbers or, for API-authored walls, from the IfcOpenShell geometry kernel
(world triangulation), which is independent of the importer's own placement and profile arithmetic.
"""
import hashlib
import importlib
import importlib.util
import json
import math
import pathlib
import subprocess
import sys
import tempfile
import unittest
import uuid

import numpy
import ifcopenshell
import ifcopenshell.api
import ifcopenshell.api.aggregate
import ifcopenshell.api.context
import ifcopenshell.api.feature
import ifcopenshell.api.geometry
import ifcopenshell.api.owner
import ifcopenshell.api.project
import ifcopenshell.api.root
import ifcopenshell.api.spatial
import ifcopenshell.api.type
import ifcopenshell.api.unit
import ifcopenshell.geom

ROOT = pathlib.Path(__file__).resolve().parents[2]
MODULE = ROOT / 'runtimes/ifc/ifc_import.py'
OBSERVED = {'Source': 'ifc-import', 'Approval': 'observed'}
RANK = {'level': 0, 'wall': 1, 'door': 2, 'window': 3, 'space': 4}


def load():
    spec = importlib.util.spec_from_file_location('bim_ifc_import_thirdparty', MODULE)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def derived(project_gid, gid):
    """Independent restatement of the documented foreign-id derivation."""
    digest = hashlib.sha256(f'bim-agentic-os/ifc-import/v1\n{project_gid}\n{gid}'.encode('utf-8')).digest()
    return str(uuid.UUID(bytes_le=digest[:16]))


def _escape(text):
    return text.replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A')


class Annotated(unittest.TestCase):
    """Echo each failure as a GitHub annotation so CI shows the reason without downloadable logs."""

    def run(self, result=None):
        result = result if result is not None else self.defaultTestResult()
        seen = len(result.failures), len(result.errors)
        super().run(result)
        for test, text in result.failures[seen[0]:] + result.errors[seen[1]:]:
            print(f'::error title=P8-05 {_escape(str(test))[:200]}::{_escape(text[-1800:])}', flush=True)
        return result


class Model:
    """A model authored through ifcopenshell.api, in metres, with IfcOpenShell-created geometry items."""

    def __init__(self, schema):
        self.f = f = ifcopenshell.api.project.create_file(version=schema)
        self.schema = schema
        self._settings = None
        if schema == 'IFC2X3':  # IFC2X3 requires an OwnerHistory on every rooted entity.
            person = ifcopenshell.api.owner.add_person(f)
            organisation = ifcopenshell.api.owner.add_organisation(f)
            user = ifcopenshell.api.owner.add_person_and_organisation(f, person=person, organisation=organisation)
            application = ifcopenshell.api.owner.add_application(f)
            s = importlib.import_module('ifcopenshell.api.owner.settings')
            self._settings = (s.get_user, s.get_application)
            s.get_user = lambda ifc: user
            s.get_application = lambda ifc: application
        self.project = self.create('IfcProject', 'third-party project')
        length = ifcopenshell.api.unit.add_si_unit(f, unit_type='LENGTHUNIT')
        ifcopenshell.api.unit.assign_unit(f, units=[length])
        model = ifcopenshell.api.context.add_context(f, context_type='Model')
        self.body = ifcopenshell.api.context.add_context(f, context_type='Model', context_identifier='Body', target_view='MODEL_VIEW', parent=model)
        self.axis_context = ifcopenshell.api.context.add_context(f, context_type='Model', context_identifier='Axis', target_view='GRAPH_VIEW', parent=model)
        self.plan = ifcopenshell.api.context.add_context(f, context_type='Plan')
        self.footprint_context = ifcopenshell.api.context.add_context(f, context_type='Plan', context_identifier='FootPrint', target_view='PLAN_VIEW', parent=self.plan)
        self.site = self.create('IfcSite', 'site')
        self.building = self.create('IfcBuilding', 'building')
        ifcopenshell.api.aggregate.assign_object(f, products=[self.site], relating_object=self.project)
        ifcopenshell.api.aggregate.assign_object(f, products=[self.building], relating_object=self.site)
        for spatial in (self.site, self.building):
            self.place(spatial)

    def close(self):
        if self._settings:
            s = importlib.import_module('ifcopenshell.api.owner.settings')
            s.get_user, s.get_application = self._settings
            self._settings = None

    def create(self, ifc_class, name, **attributes):
        entity = ifcopenshell.api.root.create_entity(self.f, ifc_class=ifc_class, name=name)
        if self.schema == 'IFC2X3' and entity.is_a('IfcSpatialStructureElement'):
            entity.CompositionType = 'ELEMENT'
        if self.schema == 'IFC2X3' and entity.is_a('IfcSpace'):
            entity.InteriorOrExteriorSpace = 'INTERNAL'
        for key, value in attributes.items():
            setattr(entity, key, value)
        return entity

    def place(self, product, location=(0.0, 0.0, 0.0), x_axis=(1.0, 0.0, 0.0)):
        matrix = numpy.eye(4)
        x = numpy.array(x_axis, dtype=float)
        z = numpy.array((0.0, 0.0, 1.0))
        matrix[:3, 0], matrix[:3, 1], matrix[:3, 2], matrix[:3, 3] = x, numpy.cross(z, x), z, location
        ifcopenshell.api.geometry.edit_object_placement(self.f, product=product, matrix=matrix, is_si=True)

    def storey(self, elevation):
        storey = self.create('IfcBuildingStorey', f'storey {elevation}', Elevation=float(elevation))
        ifcopenshell.api.aggregate.assign_object(self.f, products=[storey], relating_object=self.building)
        self.place(storey, (0.0, 0.0, float(elevation)))
        return storey

    def contain(self, storey, *products):
        ifcopenshell.api.spatial.assign_container(self.f, products=list(products), relating_structure=storey)

    # geometry items
    def pt(self, *c):
        return self.f.create_entity('IfcCartesianPoint', Coordinates=tuple(float(v) for v in c))

    def direction(self, *c):
        return self.f.create_entity('IfcDirection', DirectionRatios=tuple(float(v) for v in c))

    def placement3(self, location=(0, 0, 0), axis=None, ref=None):
        return self.f.create_entity('IfcAxis2Placement3D', Location=self.pt(*location), Axis=self.direction(*axis) if axis else None, RefDirection=self.direction(*ref) if ref else None)

    def polyline(self, points, closed=True):
        pts = [self.pt(*p) for p in points]
        return self.f.create_entity('IfcPolyline', Points=pts + [pts[0]] if closed else pts)

    def rectangle(self, centre, xdim, ydim, ref=None):
        return self.f.create_entity('IfcRectangleProfileDef', ProfileType='AREA', XDim=float(xdim), YDim=float(ydim),
                                    Position=self.f.create_entity('IfcAxis2Placement2D', Location=self.pt(*centre), RefDirection=self.direction(*ref) if ref else None))

    def polygon(self, curve):
        return self.f.create_entity('IfcArbitraryClosedProfileDef', ProfileType='AREA', OuterCurve=curve)

    def extrusion(self, profile, depth, base=0.0):
        return self.f.create_entity('IfcExtrudedAreaSolid', SweptArea=profile, Position=self.placement3((0, 0, base)), ExtrudedDirection=self.direction(0, 0, 1), Depth=float(depth))

    def representation(self, items, identifier='Body', kind='SweptSolid', context=None):
        return self.f.create_entity('IfcShapeRepresentation', ContextOfItems=context or self.body, RepresentationIdentifier=identifier, RepresentationType=kind, Items=list(items))

    def assign(self, product, *representations):
        for representation in representations:
            ifcopenshell.api.geometry.assign_representation(self.f, product=product, representation=representation)

    def write(self, path):
        self.f.write(str(path))
        return path


def world_box(product):
    """Axis-aligned world bounds of a product from the IfcOpenShell geometry kernel: (min xyz, max xyz, volume)."""
    settings = ifcopenshell.geom.settings()
    settings.set(settings.USE_WORLD_COORDS, True)
    shape = ifcopenshell.geom.create_shape(settings, product)
    verts, faces = shape.geometry.verts, shape.geometry.faces
    xyz = [verts[i::3] for i in range(3)]
    volume = 0.0
    for i in range(0, len(faces), 3):
        a, b, c = [verts[faces[i + j] * 3:faces[i + j] * 3 + 3] for j in range(3)]
        volume += (a[0] * (b[1] * c[2] - b[2] * c[1]) + a[1] * (b[2] * c[0] - b[0] * c[2]) + a[2] * (b[0] * c[1] - b[1] * c[0])) / 6
    return [min(v) for v in xyz], [max(v) for v in xyz], abs(volume)


class ThirdParty(Annotated):
    maxDiff = None

    @classmethod
    def setUpClass(cls):
        cls.ifc = load()
        (ROOT / '.bim/ifc-tests').mkdir(parents=True, exist_ok=True)

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory(dir=ROOT / '.bim/ifc-tests')
        self.dir = pathlib.Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def model(self, schema):
        m = Model(schema)
        self.addCleanup(m.close)
        return m

    def rows(self, report):
        return {r['global_id']: r for r in report['entities']}

    def assertReportConsistent(self, report):
        """The per-entity report covers every mapped and unmapped product exactly once and agrees with them."""
        entities = report['entities']
        self.assertEqual(sorted(entities, key=lambda r: (r['category'], r['global_id'], r['ifc_class'], r['status'], r['reasons'])), entities)
        self.assertEqual({s: sum(1 for r in entities if r['status'] == s) for s in ('imported', 'approximated', 'unsupported')}, report['summary'])
        mapped = [r for r in entities if r['status'] != 'unsupported']
        self.assertEqual(sorted(e['Id'] for e in report['project']['Elements']), sorted(r['id'] for r in mapped))
        self.assertEqual(sorted((s['global_id'], s['ifc_class']) for s in report['sources']), sorted((r['global_id'], r['ifc_class']) for r in mapped))
        self.assertEqual(sorted((u['global_id'], u['reason']) for u in report['unmapped']),
                         sorted((r['global_id'], r['reasons'][0]) for r in entities if r['status'] == 'unsupported'))
        for r in entities:
            self.assertEqual(r['status'] == 'imported', r['reasons'] == [] and r['detail'] == '', r)
            if r['status'] != 'imported':
                self.assertTrue(r['detail'].strip() and r['reasons'], r)
            self.assertEqual(r['status'] == 'unsupported', r['id'] is None, r)

    def assertRoundtrips(self, report, name):
        """Imported (even approximated) BIM-IR is valid Core shape: it exports and reads back unchanged."""
        out = self.dir / name
        self.ifc.export_project(report['project'], out)
        self.assertEqual(report['project'], self.ifc.import_ifc(out)['project'])

    def element(self, report, project_gid, product):
        return next(e for e in report['project']['Elements'] if e['Id'] == derived(project_gid, product.GlobalId))

    def assertWallMatchesKernel(self, wall_element, product):
        """Plan box and height of a BIM-IR wall equal the kernel's world bounds (axis-parallel walls)."""
        lo, hi, _ = world_box(product)
        ax, ay, bx, by, height, thickness = wall_element['GeometryMetres']
        half = thickness / 2
        if abs(ay - by) < 1e-9:
            expected = (min(ax, bx), ay - half, max(ax, bx), ay + half)
        else:
            self.assertAlmostEqual(ax, bx, places=9)
            expected = (ax - half, min(ay, by), ax + half, max(ay, by))
        for actual, wanted in zip((lo[0], lo[1], hi[0], hi[1], hi[2] - lo[2]), (*expected, height)):
            self.assertAlmostEqual(wanted, actual, places=6, msg=(wall_element, lo, hi))

    def third_party(self, schema):
        """The same third-party cases authored in IFC4 or IFC2X3 (IfcIndexedPolyCurve only exists in IFC4)."""
        m = self.model(schema)
        f = m.f
        storey = m.storey(0)
        # 1. API-authored wall: ifcopenshell.api.geometry.add_wall_representation (extruded polyline footprint).
        api_wall = m.create('IfcWall', 'api wall')
        m.contain(storey, api_wall)
        m.place(api_wall)
        m.assign(api_wall, ifcopenshell.api.geometry.add_wall_representation(f, context=m.body, length=6.0, height=3.0, thickness=0.2))
        # 2. Explicit IfcExtrudedAreaSolid over an IfcArbitraryClosedProfileDef/IfcPolyline rectangle, with extra collinear vertex.
        poly_wall = m.create('IfcWall', 'polyline wall')
        m.contain(storey, poly_wall)
        m.place(poly_wall, (0.0, 8.0, 0.0))
        m.assign(poly_wall, m.representation([m.extrusion(m.polygon(m.polyline([(0, -0.15), (2, -0.15), (5, -0.15), (5, 0.15), (0, 0.15)])), 2.7)]))
        # 3. Type-based geometry: the occurrence Body is an IfcMappedItem created by ifcopenshell.api.type.assign_type.
        wall_type = m.create('IfcWallType', 'typed wall type')
        m.assign(wall_type, ifcopenshell.api.geometry.add_wall_representation(f, context=m.body, length=4.0, height=2.5, thickness=0.25))
        typed_wall = m.create('IfcWall', 'typed wall')
        m.contain(storey, typed_wall)
        m.place(typed_wall, (10.0, 0.0, 0.0), x_axis=(0.0, 1.0, 0.0))
        ifcopenshell.api.type.assign_type(f, related_objects=[typed_wall], relating_type=wall_type)
        # 3b. A type with Axis and Body maps: the mapped Axis (drawn east to west) sets the BIM-IR wall direction.
        axis_type = m.create('IfcWallType', 'axis wall type')
        m.assign(axis_type, m.representation([m.polyline([(4, 0), (0, 0)], closed=False)], 'Axis', 'Curve2D', m.axis_context),
                 m.representation([m.extrusion(m.rectangle((2.0, 0.0), 4.0, 0.2), 3.0)]))
        axis_wall = m.create('IfcWall', 'mapped axis wall')
        m.contain(storey, axis_wall)
        m.place(axis_wall, (0.0, 40.0, 0.0))
        ifcopenshell.api.type.assign_type(f, related_objects=[axis_wall], relating_type=axis_type)
        # 4. Hand-mapped item with a rotated MappingTarget; 4b. the same map with a non-identity MappingOrigin, which
        # ISO 10303-43 and the IfcOpenShell kernel read differently: reported unsupported, not guessed.
        source = m.representation([m.extrusion(m.rectangle((3.0, 0.1), 4.0, 0.2), 2.8)])
        mapped_wall, origin_wall = m.create('IfcWall', 'mapped wall'), m.create('IfcWall', 'mapped wall with origin')
        for product, origin in ((mapped_wall, (0, 0, 0)), (origin_wall, (1, 0, 0))):
            rep_map = f.create_entity('IfcRepresentationMap', MappingOrigin=m.placement3(origin), MappedRepresentation=source)
            target = f.create_entity('IfcCartesianTransformationOperator3D', Axis1=m.direction(0, 1, 0), Axis3=m.direction(0, 0, 1), LocalOrigin=m.pt(20, 5, 0), Scale=1.0)
            m.contain(storey, product)
            m.place(product)
            m.assign(product, m.representation([f.create_entity('IfcMappedItem', MappingSource=rep_map, MappingTarget=target)], kind='MappedRepresentation'))

        def half_space(location, axis=None):
            plane = f.create_entity('IfcPlane', Position=m.placement3(location, axis=axis))
            return f.create_entity('IfcHalfSpaceSolid', BaseSurface=plane, AgreementFlag=False)  # material on the normal side

        def clipped(first, *spaces):
            for space in spaces:
                first = f.create_entity('IfcBooleanClippingResult', Operator='DIFFERENCE', FirstOperand=first, SecondOperand=space)
            return first

        # 5. Boolean-clipped (sloped top) wall with an Axis: approximated by its unclipped extrusion.
        clipped_wall = m.create('IfcWall', 'clipped wall')
        m.contain(storey, clipped_wall)
        m.place(clipped_wall, (0.0, 30.0, 0.0))
        m.assign(clipped_wall, m.representation([m.polyline([(0, 0), (6, 0)], closed=False)], 'Axis', 'Curve2D', m.axis_context),
                 m.representation([clipped(m.extrusion(m.rectangle((3.0, 0.0), 6.0, 0.2), 3.5), half_space((0, 0, 3.0), (-0.1, 0, 1)))], kind='Clipping'))
        # 5b. Exact horizontal top clip: 3.5 m extrusion cut at 3.0 m is a 3.0 m prism, imported.
        flat_clip_wall = m.create('IfcWall', 'horizontally clipped wall')
        m.contain(storey, flat_clip_wall)
        m.place(flat_clip_wall, (0.0, 60.0, 0.0))
        m.assign(flat_clip_wall, m.representation([clipped(m.extrusion(m.rectangle((3.0, 0.0), 6.0, 0.2), 3.5), half_space((0, 0, 3.0)))], kind='Clipping'))
        # 5c. Gable wall (two sloped clips) hosting a window: the window is exact relative to an approximated host.
        gable_wall = m.create('IfcWall', 'gable wall')
        m.contain(storey, gable_wall)
        m.place(gable_wall, (0.0, 50.0, 0.0))
        m.assign(gable_wall, m.representation([m.polyline([(0, 0), (6, 0)], closed=False)], 'Axis', 'Curve2D', m.axis_context),
                 m.representation([clipped(m.extrusion(m.rectangle((3.0, 0.0), 6.0, 0.2), 4.0), half_space((0, 0, 3.0), (-0.5, 0, 1)), half_space((6, 0, 3.0), (0.5, 0, 1)))], kind='Clipping'))
        opening = m.create('IfcOpeningElement', 'gable opening')
        m.place(opening, (2.0, 50.0, 0.0))
        m.assign(opening, m.representation([m.extrusion(m.rectangle((0.5, 0.0), 1.0, 0.3), 1.2, base=0.9)]))
        ifcopenshell.api.feature.add_feature(f, feature=opening, element=gable_wall)
        window = m.create('IfcWindow', 'gable window')
        m.contain(storey, window)
        m.place(window, (2.0, 50.0, 0.0))
        ifcopenshell.api.feature.add_filling(f, opening=opening, element=window)
        # 6. Mitred (trapezoid) polyline profile: approximated by its bounding rectangle along the longest edge.
        mitred_wall = m.create('IfcWall', 'mitred wall')
        m.contain(storey, mitred_wall)
        m.place(mitred_wall, (0.0, 20.0, 0.0))
        m.assign(mitred_wall, m.representation([m.extrusion(m.polygon(m.polyline([(0, -0.1), (4, -0.1), (3.8, 0.1), (0.2, 0.1)])), 3.0)]))
        # 6b. Curved wall faceted into 16 segments: a bounding rectangle would be mostly air, so it is unsupported.
        outer = [(5.2 * math.cos(math.pi / 2 * i / 16), 5.2 * math.sin(math.pi / 2 * i / 16)) for i in range(17)]
        inner = [(5.0 * math.cos(math.pi / 2 * i / 16), 5.0 * math.sin(math.pi / 2 * i / 16)) for i in reversed(range(17))]
        curved_wall = m.create('IfcWall', 'curved wall')
        m.contain(storey, curved_wall)
        m.place(curved_wall, (40.0, 0.0, 0.0))
        m.assign(curved_wall, m.representation([m.extrusion(m.polygon(m.polyline(outer + inner)), 3.0)]))
        # 7. Space with Body (extruded polyline) and FootPrint; 8. space with a FootPrint curve only;
        # 9. L-shaped space: unsupported.
        room = m.create('IfcSpace', 'room')
        ifcopenshell.api.aggregate.assign_object(f, products=[room], relating_object=storey)
        m.place(room)
        outline = [(0.1, 0.1), (5.9, 0.1), (5.9, 4.1), (0.1, 4.1)]
        m.assign(room, m.representation([m.extrusion(m.polygon(m.polyline(outline)), 3.0)]),
                 m.representation([m.polyline(outline)], 'FootPrint', 'Curve2D', m.footprint_context))
        boundary = m.create('IfcRelSpaceBoundary', 'room boundary')
        boundary.RelatingSpace, boundary.RelatedBuildingElement = room, poly_wall
        boundary.PhysicalOrVirtualBoundary, boundary.InternalOrExternalBoundary = 'PHYSICAL', 'INTERNAL'
        flat = m.create('IfcSpace', 'footprint only')
        ifcopenshell.api.aggregate.assign_object(f, products=[flat], relating_object=storey)
        m.place(flat, (10.0, 10.0, 0.0))
        if schema == 'IFC4':
            curve = f.create_entity('IfcIndexedPolyCurve', Points=f.create_entity('IfcCartesianPointList2D', CoordList=((0., 0.), (3., 0.), (3., 2.), (0., 2.), (0., 0.))))
        else:
            curve = m.polyline([(0, 0), (3, 0), (3, 2), (0, 2)])
        m.assign(flat, m.representation([curve], 'FootPrint', 'Curve2D', m.footprint_context))
        ell = m.create('IfcSpace', 'L room')
        ifcopenshell.api.aggregate.assign_object(f, products=[ell], relating_object=storey)
        m.place(ell, (30.0, 0.0, 0.0))
        m.assign(ell, m.representation([m.extrusion(m.polygon(m.polyline([(0, 0), (4, 0), (4, 2), (2, 2), (2, 4), (0, 4)])), 3.0)]))
        # 10. Products BIM-IR has no kind for.
        slab = m.create('IfcSlab', 'slab')
        column = m.create('IfcColumn', 'column')
        m.contain(storey, slab, column)

        # Fixture preconditions: the authoring API produced the representations this test is about.
        def items(product, identifier):
            return [i.is_a() for r in product.Representation.Representations if r.RepresentationIdentifier == identifier for i in r.Items]
        self.assertEqual(['IfcMappedItem'], items(typed_wall, 'Body'))
        self.assertEqual((['IfcMappedItem'], ['IfcMappedItem']), (items(axis_wall, 'Axis'), items(axis_wall, 'Body')))
        self.assertTrue(f.by_type('IfcBooleanClippingResult') and f.by_type('IfcArbitraryClosedProfileDef'))
        self.assertEqual(schema, f.schema)
        path = m.write(self.dir / f'third-party-{schema}.ifc')

        report = self.ifc.import_ifc(path)
        self.assertEqual(schema, report['ifc_schema'])
        self.assertReportConsistent(report)
        rows = self.rows(report)
        named = (('storey', storey), ('api', api_wall), ('poly', poly_wall), ('typed', typed_wall), ('axis', axis_wall), ('mapped', mapped_wall), ('origin', origin_wall),
                 ('clipped', clipped_wall), ('flatclip', flat_clip_wall), ('gable', gable_wall), ('window', window), ('mitred', mitred_wall), ('curved', curved_wall),
                 ('room', room), ('flat', flat), ('ell', ell), ('slab', slab), ('column', column))
        status = {name: (rows[p.GlobalId]['status'], rows[p.GlobalId]['reasons']) for name, p in named}
        self.assertEqual({
            'storey': ('imported', []), 'api': ('imported', []), 'poly': ('imported', []), 'typed': ('imported', []), 'axis': ('imported', []), 'mapped': ('imported', []),
            'origin': ('unsupported', ['unsupported_representation']),
            'clipped': ('approximated', ['boolean_clipping_ignored']), 'flatclip': ('imported', []),
            'gable': ('approximated', ['boolean_clipping_ignored']), 'window': ('approximated', ['host_approximated']),
            'mitred': ('approximated', ['profile_bounding_rectangle']), 'curved': ('unsupported', ['profile_fill_too_low']),
            'room': ('imported', []), 'flat': ('imported', []), 'ell': ('unsupported', ['room_not_rectangular']),
            'slab': ('unsupported', ['no_bim_ir_kind']), 'column': ('unsupported', ['category_unsupported']),
        }, status)
        self.assertEqual({'imported': 9, 'approximated': 4, 'unsupported': 5}, report['summary'])
        self.assertNotIn(m.site.GlobalId, rows, 'spatial structure above storeys is not listed')

        pg = m.project.GlobalId
        level = derived(pg, storey.GlobalId)
        geometry = {name: self.element(report, pg, p)['GeometryMetres'] for name, p in (
            ('poly', poly_wall), ('axis', axis_wall), ('mapped', mapped_wall), ('clipped', clipped_wall), ('flatclip', flat_clip_wall), ('gable', gable_wall),
            ('window', window), ('mitred', mitred_wall), ('room', room), ('flat', flat))}
        self.assertEqual({
            'poly': [0, 8, 5, 8, 2.7, 0.3],
            'axis': [4, 40, 0, 40, 3, 0.2],
            'mapped': [19.9, 6, 19.9, 10, 2.8, 0.2],
            'clipped': [0, 30, 6, 30, 3.5, 0.2],
            'flatclip': [0, 60, 6, 60, 3, 0.2],
            'gable': [0, 50, 6, 50, 4, 0.2],
            'window': [2, 50, 3, 50, 0.9, 1.2],
            'mitred': [0, 20, 4, 20, 3, 0.2],
            'room': [0.1, 0.1, 5.8, 4],
            'flat': [10, 10, 3, 2],
        }, geometry)
        self.assertEqual([level, derived(pg, gable_wall.GlobalId)], self.element(report, pg, window)['Dependencies'])
        self.assertEqual([level, derived(pg, poly_wall.GlobalId)], self.element(report, pg, room)['Dependencies'])
        # API-authored, type-mapped and exactly clipped walls: compared with the geometry kernel's world bounds.
        for product in (api_wall, typed_wall, axis_wall, mapped_wall, poly_wall, flat_clip_wall):
            self.assertWallMatchesKernel(self.element(report, pg, product), product)
        api = self.element(report, pg, api_wall)['GeometryMetres']
        typed = self.element(report, pg, typed_wall)['GeometryMetres']
        self.assertEqual((6, 3, 0.2), (round(abs(api[2] - api[0]) + abs(api[3] - api[1]), 9), api[4], api[5]))
        self.assertEqual((4, 2.5, 0.25), (round(abs(typed[2] - typed[0]) + abs(typed[3] - typed[1]), 9), typed[4], typed[5]))
        # The clipping approximation is a real, stated loss: the kernel solid is smaller than the BIM-IR prism.
        _, _, clipped_volume = world_box(clipped_wall)
        self.assertLess(clipped_volume, 6 * 3.5 * 0.2 - 0.2)
        self.assertIn('unclipped', rows[clipped_wall.GlobalId]['detail'])
        self.assertIn('host', rows[window.GlobalId]['detail'])
        self.assertIn('MappingOrigin', rows[origin_wall.GlobalId]['detail'])
        # Polygon approximations state both areas and their ratio; below 0.9 the wall is refused.
        self.assertRegex(rows[mitred_wall.GlobalId]['detail'], r'polygon area 0\.76 m2.*rectangle area 0\.8 m2.*ratio 0\.95')
        self.assertRegex(rows[curved_wall.GlobalId]['detail'], r'polygon area [0-9.]+ m2.*rectangle area [0-9.]+ m2.*ratio 0\.[0-8]')
        self.assertTrue(all(e['Provenance'] == OBSERVED for e in report['project']['Elements']))
        self.assertEqual(report, self.ifc.import_ifc(path), 'import must be deterministic')
        self.assertRoundtrips(report, f'reexport-{schema}.ifc')

    def test_ifc4_third_party_walls_spaces_and_unsupported_products(self):
        self.third_party('IFC4')

    def test_ifc2x3_third_party_walls_spaces_and_unsupported_products(self):
        self.third_party('IFC2X3')

    def test_ifc2x3_file_imports_exactly(self):
        m = self.model('IFC2X3')
        f = m.f
        storey = m.storey(3.0)
        wall = m.create('IfcWallStandardCase', 'wall')
        m.contain(storey, wall)
        m.place(wall, (1.0, 2.0, 3.0))
        m.assign(wall, m.representation([m.polyline([(0, 0), (5, 0)], closed=False)], 'Axis', 'Curve2D', m.axis_context),
                 m.representation([m.extrusion(m.rectangle((2.5, 0.0), 5.0, 0.2), 2.8)]))
        opening = m.create('IfcOpeningElement', 'opening')
        m.place(opening, (2.0, 2.0, 3.0))
        m.assign(opening, m.representation([m.extrusion(m.rectangle((0.45, 0.0), 0.9, 0.3), 2.1)]))
        ifcopenshell.api.feature.add_feature(f, feature=opening, element=wall)
        door = m.create('IfcDoor', 'door', OverallWidth=0.9, OverallHeight=2.1)
        m.contain(storey, door)
        m.place(door, (2.0, 2.0, 3.0))
        ifcopenshell.api.feature.add_filling(f, opening=opening, element=door)
        space = m.create('IfcSpace', 'space', InteriorOrExteriorSpace='INTERNAL')
        ifcopenshell.api.aggregate.assign_object(f, products=[space], relating_object=storey)
        m.place(space, (0.0, 0.0, 3.0))
        m.assign(space, m.representation([m.extrusion(m.rectangle((3.5, 4.1), 5.0, 4.0), 2.8)]))
        boundary = m.create('IfcRelSpaceBoundary', 'boundary')
        boundary.RelatingSpace, boundary.RelatedBuildingElement = space, wall
        boundary.PhysicalOrVirtualBoundary, boundary.InternalOrExternalBoundary = 'PHYSICAL', 'INTERNAL'
        proxy = m.create('IfcBuildingElementProxy', 'proxy')
        m.contain(storey, proxy)
        self.assertEqual('IFC2X3', f.schema)
        path = m.write(self.dir / 'ifc2x3.ifc')

        report = self.ifc.import_ifc(path)
        self.assertEqual('IFC2X3', report['ifc_schema'])
        self.assertReportConsistent(report)
        pg = m.project.GlobalId
        lv, wl = derived(pg, storey.GlobalId), derived(pg, wall.GlobalId)
        self.assertEqual({'Id': derived(pg, pg), 'Revision': 0, 'Elements': sorted([
            {'Id': lv, 'Kind': 'level', 'GeometryMetres': [3], 'Dependencies': [], 'Provenance': OBSERVED},
            {'Id': wl, 'Kind': 'wall', 'GeometryMetres': [1, 2, 6, 2, 2.8, 0.2], 'Dependencies': [lv], 'Provenance': OBSERVED},
            {'Id': derived(pg, door.GlobalId), 'Kind': 'door', 'GeometryMetres': [2, 2, 2.9, 2, 0, 2.1], 'Dependencies': [lv, wl], 'Provenance': OBSERVED},
            {'Id': derived(pg, space.GlobalId), 'Kind': 'space', 'GeometryMetres': [1, 2.1, 5, 4], 'Dependencies': [lv, wl], 'Provenance': OBSERVED},
        ], key=lambda e: (RANK[e['Kind']], e['Id']))}, report['project'])
        self.assertEqual({'imported': 4, 'approximated': 0, 'unsupported': 1}, report['summary'])
        self.assertEqual(('unsupported', ['category_unsupported']), (self.rows(report)[proxy.GlobalId]['status'], self.rows(report)[proxy.GlobalId]['reasons']))
        self.assertWallMatchesKernel(report['project']['Elements'][1], wall)
        self.assertRoundtrips(report, 'ifc2x3-as-ifc4.ifc')

    def test_mapped_and_clipped_bodies_stay_unsupported_where_not_approximated(self):
        """Approximation is for walls only: a sloped-clipped space body, a scaled mapped item or a broken outline is reported, not guessed."""
        m = self.model('IFC4')
        f = m.f
        storey = m.storey(0)
        space = m.create('IfcSpace', 'clipped space')
        ifcopenshell.api.aggregate.assign_object(f, products=[space], relating_object=storey)
        m.place(space)
        def sloped_clip(centre):
            plane = f.create_entity('IfcPlane', Position=m.placement3((0, 0, 2.0), axis=(0.3, 0, 1)))
            return m.representation([f.create_entity('IfcBooleanClippingResult', Operator='DIFFERENCE', FirstOperand=m.extrusion(m.rectangle(centre, 4, 4), 3.0),
                                                     SecondOperand=f.create_entity('IfcHalfSpaceSolid', BaseSurface=plane, AgreementFlag=False))], kind='Clipping')
        m.assign(space, sloped_clip((2, 2)))
        # A space whose Body is unreadable but whose FootPrint is a rectangle is read from the FootPrint.
        fallback = m.create('IfcSpace', 'clipped space with footprint')
        ifcopenshell.api.aggregate.assign_object(f, products=[fallback], relating_object=storey)
        m.place(fallback, (10.0, 0.0, 0.0))
        m.assign(fallback, sloped_clip((2, 2)), m.representation([m.polyline([(0, 0), (4, 0), (4, 4), (0, 4)])], 'FootPrint', 'Curve2D', m.footprint_context))
        # An IfcIndexedPolyCurve whose segments do not connect is not a closed outline.
        broken = m.create('IfcSpace', 'disconnected footprint')
        ifcopenshell.api.aggregate.assign_object(f, products=[broken], relating_object=storey)
        m.place(broken, (20.0, 0.0, 0.0))
        segments = [f.create_entity('IfcLineIndex', (1, 2)), f.create_entity('IfcLineIndex', (3, 4))]
        disconnected = f.create_entity('IfcIndexedPolyCurve', Points=f.create_entity('IfcCartesianPointList2D', CoordList=((0., 0.), (3., 0.), (3., 2.), (0., 2.))), Segments=segments)
        m.assign(broken, m.representation([disconnected], 'FootPrint', 'Curve2D', m.footprint_context))
        rep_map = f.create_entity('IfcRepresentationMap', MappingOrigin=m.placement3(), MappedRepresentation=m.representation([m.extrusion(m.rectangle((2, 0), 4, 0.2), 3)]))
        scaled = m.create('IfcWall', 'scaled wall')
        m.contain(storey, scaled)
        m.place(scaled)
        m.assign(scaled, m.representation([f.create_entity('IfcMappedItem', MappingSource=rep_map, MappingTarget=f.create_entity(
            'IfcCartesianTransformationOperator3D', LocalOrigin=m.pt(0, 0, 0), Scale=2.0))], kind='MappedRepresentation'))
        report = self.ifc.import_ifc(m.write(self.dir / 'unsupported.ifc'))
        self.assertReportConsistent(report)
        rows = self.rows(report)
        self.assertEqual(('unsupported', ['unsupported_representation']), (rows[space.GlobalId]['status'], rows[space.GlobalId]['reasons']))
        self.assertEqual(('unsupported', ['unsupported_representation']), (rows[scaled.GlobalId]['status'], rows[scaled.GlobalId]['reasons']))
        self.assertEqual(('unsupported', ['unsupported_representation']), (rows[broken.GlobalId]['status'], rows[broken.GlobalId]['reasons']))
        self.assertEqual(('imported', [10, 0, 4, 4]), (rows[fallback.GlobalId]['status'], self.element(report, m.project.GlobalId, fallback)['GeometryMetres']))
        self.assertEqual({'imported': 2, 'approximated': 0, 'unsupported': 3}, report['summary'])

    def test_command_line_prints_the_entity_report(self):
        m = self.model('IFC4')
        storey = m.storey(0)
        column = m.create('IfcColumn', 'column')
        m.contain(storey, column)
        path = m.write(self.dir / 'cli.ifc')
        done = subprocess.run([sys.executable, '-I', str(MODULE), 'import', str(path)], capture_output=True, text=True, timeout=300)
        self.assertEqual(0, done.returncode, done.stderr)
        report = json.loads(done.stdout)
        self.assertEqual({'imported': 1, 'approximated': 0, 'unsupported': 1}, report['summary'])
        self.assertEqual({(storey.GlobalId, 'imported'), (column.GlobalId, 'unsupported')}, {(r['global_id'], r['status']) for r in report['entities']})


if __name__ == '__main__':
    unittest.main()
