"""P8-01: IFC -> BIM-IR import and the BIM-IR -> IFC -> BIM-IR -> IFC roundtrip, against real IfcOpenShell files."""
import hashlib
import importlib.util
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[2]
MODULE = ROOT / 'runtimes/ifc/ifc_import.py'
WORKER = ROOT / 'runtimes/ifc/worker.py'
NAMESPACE = uuid.UUID('6f1c1c0e-6d1a-4c55-9a43-1a0d8f0b2c11')
RANK = {'level': 0, 'wall': 1, 'door': 2, 'window': 3, 'space': 4}
OBSERVED = {'Source': 'ifc-import', 'Approval': 'observed'}
_LOADED = {}


def load(name, path):
    if name in _LOADED:
        return _LOADED[name]
    if not path.exists():
        raise AssertionError(f'{path.relative_to(ROOT)} missing: IFC import not implemented')
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    _LOADED[name] = module
    return module


def uid(name):
    return str(uuid.uuid5(NAMESPACE, name))


def derived(project_gid, gid):
    """Independent restatement of the documented foreign-id derivation (Mirror convention)."""
    digest = hashlib.sha256(f'bim-agentic-os/ifc-import/v1\n{project_gid}\n{gid}'.encode('utf-8')).digest()
    return str(uuid.UUID(bytes_le=digest[:16]))


def element(name, kind, geometry, dependencies, provenance=None):
    return {'Id': uid(name), 'Kind': kind, 'GeometryMetres': geometry, 'Dependencies': dependencies,
            'Provenance': provenance or {'Source': 'p8-01 test', 'Approval': 'approved'}}


def observed(project):
    """Provenance never travels through IFC: every imported element is ifc-import/observed (approval is not spoofable)."""
    return dict(project, Elements=[dict(e, Provenance=dict(OBSERVED)) for e in project['Elements']])


def canonical(elements):
    return sorted(elements, key=lambda e: (RANK[e['Kind']], e['Id']))


def source_project():
    """Two levels, a closed 5x4 m room with a door and two windows, a diagonal wall with a door, two spaces."""
    l0, l1 = uid('level-0'), uid('level-1')
    walls = {name: uid(name) for name in ('south', 'east', 'north', 'west', 'diagonal')}
    room_walls = sorted(walls[n] for n in ('south', 'east', 'north', 'west'))
    elements = [
        element('level-0', 'level', [0], []),
        element('level-1', 'level', [3.2], [], {'Source': 'other source', 'Approval': ''}),
        element('south', 'wall', [0, 0, 5, 0, 3, 0.2], [l0]),
        element('east', 'wall', [5, 0, 5, 4, 3, 0.2], [l0]),
        element('north', 'wall', [5, 4, 0, 4, 3, 0.2], [l0]),
        element('west', 'wall', [0, 4, 0, 0, 3, 0.2], [l0]),
        element('diagonal', 'wall', [0, 0, 3, 4, 2.7, 0.25], [l1]),
        element('door-south', 'door', [1, 0, 1.9, 0, 0, 2.1], [l0, walls['south']]),
        element('door-diagonal', 'door', [0.6, 0.8, 1.2, 1.6, 0, 2.1], [l1, walls['diagonal']]),
        element('window-north', 'window', [3.5, 4, 2.3, 4, 0.9, 1.2], [l0, walls['north']]),
        element('window-east', 'window', [5, 1.5, 5, 2.7, 0.9, 1.2], [l0, walls['east']]),
        element('space-room', 'space', [0.1, 0.1, 4.8, 3.8], [l0, *room_walls]),
        element('space-free', 'space', [10, 10, 3, 2.5], [l1]),
    ]
    return {'Id': uid('project'), 'Revision': 7, 'Elements': canonical(elements)}


class Scratch(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.ifc = load('bim_ifc_import', MODULE)
        cls.worker = load('ifc_worker', WORKER)
        (ROOT / '.bim/ifc-tests').mkdir(parents=True, exist_ok=True)

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory(dir=ROOT / '.bim/ifc-tests')
        self.dir = pathlib.Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def assertCode(self, code, action):
        with self.assertRaises(self.ifc.IfcAdapterError) as caught:
            action()
        self.assertEqual(code, caught.exception.code, str(caught.exception))

    def unmapped(self, report):
        return {(u['category'], u['global_id'], u['reason']) for u in report['unmapped']}


class Roundtrip(Scratch):
    def test_export_import_export_is_stable(self):
        source = source_project()
        first = self.dir / 'first.ifc'
        exported = self.ifc.export_project(source, first)
        self.assertEqual([], exported['unexported'])
        report = self.ifc.import_ifc(first)
        self.assertEqual([], report['unmapped'])
        self.assertEqual(observed(source), report['project'], 'BIM-IR after import must equal the source BIM-IR (provenance observed)')
        second = self.dir / 'second.ifc'
        self.ifc.export_project(report['project'], second)
        self.assertEqual(self.ifc.canonical_data(first), self.ifc.canonical_data(second), 'second export must equal the first')
        self.assertEqual(observed(source), self.ifc.import_ifc(second)['project'])
        # A source whose provenance is already observed roundtrips exactly.
        exact = observed(source)
        self.ifc.export_project(exact, self.dir / 'exact.ifc')
        self.assertEqual(exact, self.ifc.import_ifc(self.dir / 'exact.ifc')['project'])
        self.assertEqual(self.ifc.canonical_data(first), self.ifc.canonical_data(self.dir / 'exact.ifc'), 'provenance is not written to IFC')
        self.assertEqual({e['Id'] for e in source['Elements']}, {s['id'] for s in report['sources']})

    def test_export_is_real_ifc_with_hosting_relations(self):
        source = source_project()
        path = self.dir / 'model.ifc'
        self.ifc.export_project(source, path)
        f = self.worker.ifcopenshell.open(str(path))
        self.worker.validate(f)
        self.assertEqual('IFC4', f.schema)
        counts = {t: len(f.by_type(t)) for t in ('IfcBuildingStorey', 'IfcWall', 'IfcDoor', 'IfcWindow', 'IfcSpace', 'IfcOpeningElement', 'IfcRelVoidsElement', 'IfcRelFillsElement', 'IfcRelSpaceBoundary')}
        self.assertEqual({'IfcBuildingStorey': 2, 'IfcWall': 5, 'IfcDoor': 2, 'IfcWindow': 2, 'IfcSpace': 2, 'IfcOpeningElement': 4, 'IfcRelVoidsElement': 4, 'IfcRelFillsElement': 4, 'IfcRelSpaceBoundary': 4}, counts)
        byid = {e['Id']: e for e in source['Elements']}
        for door in f.by_type('IfcDoor') + f.by_type('IfcWindow'):
            opening = door.FillsVoids[0].RelatingOpeningElement
            host = opening.VoidsElements[0].RelatingBuildingElement
            logical = str(uuid.UUID(self.worker.ifcopenshell.guid.expand(door.GlobalId)))
            self.assertEqual(byid[logical]['Dependencies'][1], str(uuid.UUID(self.worker.ifcopenshell.guid.expand(host.GlobalId))))
        # Independent kernel evidence: triangulated wall volume is the axis box minus its filled openings.
        settings = self.worker.ifcopenshell.geom.settings()
        settings.set(settings.USE_WORLD_COORDS, True)
        for wall in f.by_type('IfcWall'):
            g = byid[str(uuid.UUID(self.worker.ifcopenshell.guid.expand(wall.GlobalId)))]['GeometryMetres']
            length = ((g[2] - g[0]) ** 2 + (g[3] - g[1]) ** 2) ** 0.5
            expected = length * g[4] * g[5]
            for rel in wall.HasOpenings:
                fill = rel.RelatedOpeningElement.HasFillings[0].RelatedBuildingElement
                o = byid[str(uuid.UUID(self.worker.ifcopenshell.guid.expand(fill.GlobalId)))]['GeometryMetres']
                expected -= ((o[2] - o[0]) ** 2 + (o[3] - o[1]) ** 2) ** 0.5 * o[5] * g[5]
            shape = self.worker.ifcopenshell.geom.create_shape(settings, wall)
            verts, faces = shape.geometry.verts, shape.geometry.faces
            volume = 0.0
            for i in range(0, len(faces), 3):
                a, b, c = [verts[faces[i + j] * 3:faces[i + j] * 3 + 3] for j in range(3)]
                volume += (a[0] * (b[1] * c[2] - b[2] * c[1]) + a[1] * (b[2] * c[0] - b[0] * c[2]) + a[2] * (b[0] * c[1] - b[1] * c[0])) / 6
            self.assertAlmostEqual(expected, abs(volume), places=6, msg=wall.Name)

    def test_input_order_does_not_change_the_export(self):
        source = source_project()
        shuffled = dict(source, Elements=list(reversed(source['Elements'])))
        self.ifc.export_project(source, self.dir / 'a.ifc')
        self.ifc.export_project(shuffled, self.dir / 'b.ifc')
        self.assertEqual(self.ifc.canonical_data(self.dir / 'a.ifc'), self.ifc.canonical_data(self.dir / 'b.ifc'))
        self.assertEqual(observed(source), self.ifc.import_ifc(self.dir / 'b.ifc')['project'])

    def test_documentation_kinds_are_reported_not_exported(self):
        source = source_project()
        plan = element('plan', 'plan', [], [source['Elements'][0]['Id']])
        report = self.ifc.export_project(dict(source, Elements=source['Elements'] + [plan]), self.dir / 'doc.ifc')
        self.assertEqual([{'id': plan['Id'], 'kind': 'plan', 'reason': 'no_ifc_entity'}], report['unexported'])
        self.assertEqual(observed(source), self.ifc.import_ifc(self.dir / 'doc.ifc')['project'])

    def test_export_refusals_write_nothing(self):
        base = source_project()

        def variant(name, change):
            elements = [dict(e) for e in base['Elements']]
            target = next(e for e in elements if e['Id'] == uid(name))
            change(target)
            return dict(base, Elements=elements)

        cases = [
            ('ifc_opening_geometry', variant('door-south', lambda e: e.update(GeometryMetres=[1, 0.5, 1.9, 0.5, 0, 2.1]))),
            ('ifc_opening_geometry', variant('door-south', lambda e: e.update(GeometryMetres=[1.9, 0, 1, 0, 0, 2.1]))),
            ('ifc_opening_geometry', variant('door-south', lambda e: e.update(GeometryMetres=[1, 0, 1.9, 0, 0.1, 2.1]))),
            ('ifc_opening_geometry', variant('window-east', lambda e: e.update(GeometryMetres=[5, 3.5, 5, 4.5, 0.9, 1.2]))),
            ('ifc_dependency_shape', variant('east', lambda e: e.update(Dependencies=[]))),
            ('ifc_dependency_shape', variant('space-room', lambda e: e.update(Dependencies=list(reversed(e['Dependencies']))))),
            ('ifc_precision', variant('east', lambda e: e.update(GeometryMetres=[5, 0, 5, 4, 3.0000000001, 0.2]))),
            ('geometry_kind_unsupported', variant('east', lambda e: e.update(Kind='floor'))),
            ('wall_geometry', variant('east', lambda e: e.update(GeometryMetres=[5, 0, 5, 4, 3, 0]))),
            ('bim_ir_invalid', dict(base, Id=base['Id'].upper())),
            ('missing_dependency', variant('east', lambda e: e.update(Dependencies=[uid('nowhere')]))),
        ]
        for index, (code, project) in enumerate(cases):
            with self.subTest(code=code, index=index):
                path = self.dir / f'refused-{index}.ifc'
                self.assertCode(code, lambda: self.ifc.export_project(project, path))
                self.assertEqual([], sorted(p.name for p in self.dir.iterdir()))
        existing = self.dir / 'existing.ifc'
        existing.write_bytes(b'keep me')
        self.assertCode('output_exists', lambda: self.ifc.export_project(base, existing))
        self.assertEqual(b'keep me', existing.read_bytes())


class ForeignIfc(Scratch):
    """IFC authored by the existing worker (millimetres, random GlobalIds, box bodies, no BIM-IR identity)."""

    def room(self):
        path = self.dir / 'room.ifc'
        self.worker.create(path)
        return path

    def test_worker_room_maps_every_element(self):
        path = self.room()
        f = self.worker.ifcopenshell.open(str(path))
        project_gid = f.by_type('IfcProject')[0].GlobalId
        roles = {role: e.GlobalId for role, e in self.worker.entities(f).items()}
        ids = {role: derived(project_gid, gid) for role, gid in roles.items()}
        lv = ids['level']
        walls = sorted(ids[w] for w in ('west', 'east', 'south', 'north'))
        expected = canonical([
            {'Id': lv, 'Kind': 'level', 'GeometryMetres': [0], 'Dependencies': [], 'Provenance': OBSERVED},
            {'Id': ids['west'], 'Kind': 'wall', 'GeometryMetres': [-0.1, 0, -0.1, 4, 3, 0.2], 'Dependencies': [lv], 'Provenance': OBSERVED},
            {'Id': ids['east'], 'Kind': 'wall', 'GeometryMetres': [5.1, 0, 5.1, 4, 3, 0.2], 'Dependencies': [lv], 'Provenance': OBSERVED},
            {'Id': ids['south'], 'Kind': 'wall', 'GeometryMetres': [-0.2, -0.1, 5.2, -0.1, 3, 0.2], 'Dependencies': [lv], 'Provenance': OBSERVED},
            {'Id': ids['north'], 'Kind': 'wall', 'GeometryMetres': [-0.2, 4.1, 5.2, 4.1, 3, 0.2], 'Dependencies': [lv], 'Provenance': OBSERVED},
            {'Id': ids['door'], 'Kind': 'door', 'GeometryMetres': [1, -0.1, 1.9, -0.1, 0, 2.1], 'Dependencies': [lv, ids['south']], 'Provenance': OBSERVED},
            {'Id': ids['window-north'], 'Kind': 'window', 'GeometryMetres': [1.5, 4.1, 2.7, 4.1, 0.9, 1.2], 'Dependencies': [lv, ids['north']], 'Provenance': OBSERVED},
            {'Id': ids['window-east'], 'Kind': 'window', 'GeometryMetres': [5.1, 1.5, 5.1, 2.7, 0.9, 1.2], 'Dependencies': [lv, ids['east']], 'Provenance': OBSERVED},
            {'Id': ids['room'], 'Kind': 'space', 'GeometryMetres': [0, 0, 5, 4], 'Dependencies': [lv, *walls], 'Provenance': OBSERVED},
        ])
        report = self.ifc.import_ifc(path)
        self.assertEqual([], report['unmapped'])
        self.assertEqual({'Id': derived(project_gid, project_gid), 'Revision': 0, 'Elements': expected}, report['project'])
        self.assertEqual(report, self.ifc.import_ifc(path), 'import must be deterministic')
        # The imported foreign BIM-IR then roundtrips through the BIM-IR export like any source.
        out = self.dir / 'reexport.ifc'
        self.ifc.export_project(report['project'], out)
        self.assertEqual(report['project'], self.ifc.import_ifc(out)['project'])

    def test_unmappable_elements_are_listed_with_reasons(self):
        path = self.room()
        f = self.worker.ifcopenshell.open(str(path))
        e = self.worker.entities(f)
        gid = {role: x.GlobalId for role, x in e.items()}
        slab = self.worker.root(f, 'IfcSlab', 'slab')
        f.remove(e['door'].FillsVoids[0])
        opening = [o for o in f.by_type('IfcOpeningElement') if o.Name == 'opening-door'][0]
        loc = e['west'].ObjectPlacement.RelativePlacement.Location
        loc.Coordinates = (loc.Coordinates[0], loc.Coordinates[1], 100.0)
        e['room'].Representation.Representations[0].Items[0].SweptArea.Position.RefDirection = f.create_entity('IfcDirection', DirectionRatios=(0.6, 0.8))
        east = e['east'].Representation.Representations[0].Items[0].SweptArea
        east.YDim = 200.0
        east.Position.Location.Coordinates = (100.0, 100.0)
        changed = self.dir / 'changed.ifc'
        f.write(str(changed))
        report = self.ifc.import_ifc(changed)
        self.assertEqual({
            ('door', gid['door'], 'host_unknown'),
            ('IfcSlab', slab.GlobalId, 'no_bim_ir_kind'),
            ('opening', opening.GlobalId, 'no_bim_ir_kind'),
            ('space', gid['room'], 'room_not_rectangular'),
            ('wall', gid['west'], 'wall_base_offset_unsupported'),
            ('wall', gid['east'], 'wall_axis_ambiguous'),
            ('window', gid['window-east'], 'host_unknown'),
        }, self.unmapped(report))
        project_gid = f.by_type('IfcProject')[0].GlobalId
        mapped = {derived(project_gid, gid[r]) for r in ('level', 'south', 'north', 'window-north')}
        self.assertEqual(mapped, {x['Id'] for x in report['project']['Elements']})
        for entry in report['unmapped']:
            self.assertTrue(entry['detail'].strip(), entry)
        self.assertEqual(sorted(report['unmapped'], key=lambda u: (u['category'], u['global_id'], u['reason'])), report['unmapped'])

    def test_unmapped_level_cascades(self):
        path = self.room()
        f = self.worker.ifcopenshell.open(str(path))
        e = self.worker.entities(f)
        e['level'].Elevation = 100.0
        changed = self.dir / 'level.ifc'
        f.write(str(changed))
        report = self.ifc.import_ifc(changed)
        self.assertEqual([], report['project']['Elements'])
        reasons = {(u['global_id'], u['reason']) for u in report['unmapped']}
        self.assertIn((e['level'].GlobalId, 'level_elevation_conflict'), reasons)
        self.assertEqual({x.GlobalId for r, x in e.items() if r != 'level'}, {g for g, r in reasons if r == 'level_unknown'})

    def test_wall_standard_case_is_a_wall(self):
        path = self.room()
        f = self.worker.ifcopenshell.open(str(path))
        e = self.worker.entities(f)
        extra = self.worker.root(f, 'IfcWallStandardCase', 'standard-case', ObjectPlacement=e['south'].ObjectPlacement, Representation=e['south'].Representation)
        rel = e['south'].ContainedInStructure[0]
        rel.RelatedElements = list(rel.RelatedElements) + [extra]
        changed = self.dir / 'standard.ifc'
        f.write(str(changed))
        report = self.ifc.import_ifc(changed)
        self.assertEqual([], report['unmapped'])
        project_gid = f.by_type('IfcProject')[0].GlobalId
        wall = next(x for x in report['project']['Elements'] if x['Id'] == derived(project_gid, extra.GlobalId))
        self.assertEqual(('wall', [-0.2, -0.1, 5.2, -0.1, 3, 0.2]), (wall['Kind'], wall['GeometryMetres']))
        self.assertIn({'id': wall['Id'], 'global_id': extra.GlobalId, 'ifc_class': 'IfcWallStandardCase'}, report['sources'])


class OwnedIdentity(Scratch):
    def test_copied_duplicated_and_invalid_owned_elements_are_unmapped(self):
        source = source_project()
        path = self.dir / 'owned.ifc'
        self.ifc.export_project(source, path)
        f = self.worker.ifcopenshell.open(str(path))
        by = {str(uuid.UUID(self.worker.ifcopenshell.guid.expand(x.GlobalId))): x for x in f.by_type('IfcProduct')}
        north, east = by[uid('window-north')], by[uid('window-east')]
        east.GlobalId = north.GlobalId
        free = by[uid('space-free')]
        free.GlobalId = self.worker.ifcopenshell.guid.compress(uuid.uuid4().hex)
        door = by[uid('door-south')]
        door.FillsVoids[0].RelatingOpeningElement.ObjectPlacement.RelativePlacement.Location.Coordinates = (0.0, 0.0, 0.05)
        changed = self.dir / 'changed.ifc'
        f.write(str(changed))
        report = self.ifc.import_ifc(changed)
        self.assertEqual({
            ('window', north.GlobalId, 'duplicate_global_id'),
            ('space', free.GlobalId, 'identity_mismatch'),
            ('door', door.GlobalId, 'invalid_geometry'),
        }, self.unmapped(report))
        self.assertEqual(sum(1 for u in report['unmapped'] if u['reason'] == 'duplicate_global_id'), 2)
        lost = {uid('window-north'), uid('window-east'), uid('space-free'), uid('door-south')}
        self.assertEqual([x for x in observed(source)['Elements'] if x['Id'] not in lost], report['project']['Elements'])
        self.assertEqual((source['Id'], 7), (report['project']['Id'], report['project']['Revision']))


class CommandLine(Scratch):
    def run_tool(self, *args):
        return subprocess.run([sys.executable, '-I', *map(str, args)], capture_output=True, text=True, timeout=300)

    def test_module_and_worker_actions(self):
        source = source_project()
        src = self.dir / 'source.json'
        src.write_text(json.dumps(source), encoding='utf-8')
        out = self.dir / 'cli.ifc'
        done = self.run_tool(MODULE, 'export', src, out)
        self.assertEqual(0, done.returncode, done.stderr)
        self.assertEqual('exported', json.loads(done.stdout)['status'])
        imported = self.run_tool(MODULE, 'import', out)
        self.assertEqual(0, imported.returncode, imported.stderr)
        self.assertEqual(observed(source), json.loads(imported.stdout)['project'])
        self.assertNotIn('3.0,', imported.stdout, 'integral metres are written like Core (3, not 3.0)')
        worker = self.run_tool(WORKER, '--import-ifc', out)
        self.assertEqual(0, worker.returncode, worker.stderr)
        self.assertEqual(json.loads(imported.stdout), json.loads(worker.stdout))
        garbage = self.dir / 'garbage.ifc'
        garbage.write_text('not an IFC file', encoding='utf-8')
        failed = self.run_tool(MODULE, 'import', garbage)
        self.assertEqual(1, failed.returncode)
        self.assertEqual('ifc_unreadable', json.loads(failed.stderr)['error'])
        failed = self.run_tool(WORKER, '--import-ifc', garbage)
        self.assertEqual(1, failed.returncode)
        self.assertEqual('ifc_unreadable', json.loads(failed.stderr)['error'])



def pset_of(entity, name):
    return next(rel.RelatingPropertyDefinition for rel in entity.IsDefinedBy if rel.RelatingPropertyDefinition.Name == name)


class ScratchModel:
    """A small hand-built IFC4 model for placement/unit cases the worker and exporter never author."""

    def __init__(self, ios, unit='m'):
        self.ios = ios
        f = self.f = ios.file(schema='IFC4')
        self.ctx = f.create_entity('IfcGeometricRepresentationContext', ContextIdentifier='Model', ContextType='Model', CoordinateSpaceDimension=3, Precision=1e-9,
                                   WorldCoordinateSystem=f.create_entity('IfcAxis2Placement3D', Location=self.pt(0, 0, 0)))
        metre = f.create_entity('IfcSIUnit', UnitType='LENGTHUNIT', Name='METRE')
        if unit == 'm':
            length = metre
        elif unit == 'nano':
            length = f.create_entity('IfcSIUnit', UnitType='LENGTHUNIT', Prefix='NANO', Name='METRE')
        else:
            component = metre
            if unit == 'derived':
                component = f.create_entity('IfcDerivedUnit', Elements=[f.create_entity('IfcDerivedUnitElement', Unit=metre, Exponent=1)], UnitType='USERDEFINED', UserDefinedType='odd')
            length = f.create_entity('IfcConversionBasedUnit', Dimensions=f.create_entity('IfcDimensionalExponents', 1, 0, 0, 0, 0, 0, 0), UnitType='LENGTHUNIT', Name='FOOT',
                                     ConversionFactor=f.create_entity('IfcMeasureWithUnit', ValueComponent=f.create_entity('IfcLengthMeasure', 0.3048), UnitComponent=component))
        self.project = self.root('IfcProject', 'scratch', RepresentationContexts=[self.ctx], UnitsInContext=f.create_entity('IfcUnitAssignment', Units=[length]))

    def root(self, kind, name, **kw):
        return self.f.create_entity(kind, GlobalId=self.ios.guid.compress(uuid.uuid4().hex), Name=name, **kw)

    def pt(self, *c):
        return self.f.create_entity('IfcCartesianPoint', Coordinates=tuple(float(v) for v in c))

    def direction(self, *c):
        return self.f.create_entity('IfcDirection', DirectionRatios=tuple(float(v) for v in c))

    def placement(self, location, parent=None, ref=None):
        axis = self.f.create_entity('IfcAxis2Placement3D', Location=self.pt(*location), Axis=self.direction(0, 0, 1) if ref else None, RefDirection=self.direction(*ref) if ref else None)
        return self.f.create_entity('IfcLocalPlacement', PlacementRelTo=parent, RelativePlacement=axis)

    def body(self, centre, xdim, ydim, base, depth, ref=None):
        f = self.f
        profile = f.create_entity('IfcRectangleProfileDef', ProfileType='AREA', XDim=float(xdim), YDim=float(ydim),
                                  Position=f.create_entity('IfcAxis2Placement2D', Location=self.pt(*centre), RefDirection=self.direction(*ref) if ref else None))
        solid = f.create_entity('IfcExtrudedAreaSolid', SweptArea=profile, Position=f.create_entity('IfcAxis2Placement3D', Location=self.pt(0, 0, base)),
                                ExtrudedDirection=self.direction(0, 0, 1), Depth=float(depth))
        return f.create_entity('IfcProductDefinitionShape', Representations=[f.create_entity('IfcShapeRepresentation', ContextOfItems=self.ctx, RepresentationIdentifier='Body', RepresentationType='SweptSolid', Items=[solid])])

    def storey(self, location, elevation, ref=None):
        storey = self.root('IfcBuildingStorey', 'storey', Elevation=float(elevation), ObjectPlacement=self.placement(location, ref=ref))
        self.root('IfcRelAggregates', 'project storey', RelatingObject=self.project, RelatedObjects=[storey])
        return storey

    def contain(self, storey, *elements):
        self.root('IfcRelContainedInSpatialStructure', 'contents', RelatingStructure=storey, RelatedElements=list(elements))

    def write(self, path):
        self.f.write(str(path))
        return path


class GeometryCoverage(Scratch):
    def test_rotated_nested_placements_give_exact_world_geometry(self):
        m = ScratchModel(self.worker.ifcopenshell)
        storey = m.storey((10, 0, 2.5), 2.5, ref=(0, 1, 0))  # rotated 90 degrees: local x is world +y
        wall = m.root('IfcWall', 'wall', ObjectPlacement=m.placement((1, 0, 0), storey.ObjectPlacement))
        wall.Representation = m.body((2, 0), 4, 0.2, 0, 3)
        opening = m.root('IfcOpeningElement', 'opening', ObjectPlacement=m.placement((1.5, 0, 0), wall.ObjectPlacement))
        opening.Representation = m.body((0, 0), 0.9, 0.3, 0.8, 1.2)
        window = m.root('IfcWindow', 'window', ObjectPlacement=m.placement((1.5, 0, 0), wall.ObjectPlacement))
        m.root('IfcRelVoidsElement', 'voids', RelatingBuildingElement=wall, RelatedOpeningElement=opening)
        m.root('IfcRelFillsElement', 'fills', RelatingOpeningElement=opening, RelatedBuildingElement=window)
        space = m.root('IfcSpace', 'space', ObjectPlacement=m.placement((0, 0, 0), storey.ObjectPlacement))
        space.Representation = m.body((2, -2), 4, 3, 0, 3)
        m.root('IfcRelAggregates', 'storey space', RelatingObject=storey, RelatedObjects=[space])
        m.root('IfcRelSpaceBoundary', 'boundary', RelatingSpace=space, RelatedBuildingElement=wall, PhysicalOrVirtualBoundary='PHYSICAL', InternalOrExternalBoundary='INTERNAL')
        m.contain(storey, wall, window)
        report = self.ifc.import_ifc(m.write(self.dir / 'rotated.ifc'))
        self.assertEqual([], report['unmapped'])
        pg = m.project.GlobalId
        lv, wl = derived(pg, storey.GlobalId), derived(pg, wall.GlobalId)
        self.assertEqual({'Id': derived(pg, pg), 'Revision': 0, 'Elements': canonical([
            {'Id': lv, 'Kind': 'level', 'GeometryMetres': [2.5], 'Dependencies': [], 'Provenance': OBSERVED},
            {'Id': wl, 'Kind': 'wall', 'GeometryMetres': [10, 1, 10, 5, 3, 0.2], 'Dependencies': [lv], 'Provenance': OBSERVED},
            {'Id': derived(pg, window.GlobalId), 'Kind': 'window', 'GeometryMetres': [10, 2.05, 10, 2.95, 0.8, 1.2], 'Dependencies': [lv, wl], 'Provenance': OBSERVED},
            {'Id': derived(pg, space.GlobalId), 'Kind': 'space', 'GeometryMetres': [10.5, 0, 3, 4], 'Dependencies': [lv, wl], 'Provenance': OBSERVED},
        ])}, report['project'])

    def test_feet_conversion_based_unit(self):
        m = ScratchModel(self.worker.ifcopenshell, unit='feet')
        storey = m.storey((0, 0, 0), 0)
        wall = m.root('IfcWall', 'wall', ObjectPlacement=m.placement((0, 0, 0), storey.ObjectPlacement))
        wall.Representation = m.body((5, 0.5), 10, 1, 0, 10)
        m.contain(storey, wall)
        report = self.ifc.import_ifc(m.write(self.dir / 'feet.ifc'))
        self.assertEqual([], report['unmapped'])
        self.assertEqual([0, 0.1524, 3.048, 0.1524, 3.048, 0.3048], report['project']['Elements'][1]['GeometryMetres'])

    def test_unsupported_length_units_refuse_the_file(self):
        for unit in ('nano', 'derived'):
            with self.subTest(unit=unit):
                m = ScratchModel(self.worker.ifcopenshell, unit=unit)
                m.storey((0, 0, 0), 0)
                path = m.write(self.dir / f'{unit}.ifc')
                self.assertCode('ifc_units_unsupported', lambda: self.ifc.import_ifc(path))


class ReviewFindings(Scratch):
    def room(self):
        path = self.dir / 'room.ifc'
        self.worker.create(path)
        f = self.worker.ifcopenshell.open(str(path))
        return f, self.worker.entities(f)

    def owned(self):
        path = self.dir / 'owned.ifc'
        self.ifc.export_project(source_project(), path)
        f = self.worker.ifcopenshell.open(str(path))
        return f, {str(uuid.UUID(self.worker.ifcopenshell.guid.expand(x.GlobalId))): x for x in f.by_type('IfcProduct')}

    def changed(self, f, name='changed.ifc'):
        path = self.dir / name
        f.write(str(path))
        return self.ifc.import_ifc(path)

    def test_pset_approval_is_never_honoured(self):
        f, by = self.owned()
        south = by[uid('south')].Representation.Representations[1].Items[0].SweptArea
        self.assertEqual('IfcRectangleProfileDef', south.is_a())
        south.YDim = 0.3  # edited geometry keeps its GlobalId and BimIrElement pset
        ps = pset_of(by[uid('east')], 'BimIrElement')
        ps.HasProperties = list(ps.HasProperties) + [f.create_entity('IfcPropertySingleValue', Name=k, NominalValue=f.create_entity('IfcText', v)) for k, v in (('Source', 'p8-01 test'), ('Approval', 'approved'))]
        north = by[uid('north')].Representation.Representations[1].Items[0].SweptArea
        north.XDim = 4.0  # body no longer agrees with the Axis
        report = self.changed(f)
        elements = {e['Id']: e for e in report['project']['Elements']}
        self.assertEqual([0, 0, 5, 0, 3, 0.3], elements[uid('south')]['GeometryMetres'])
        self.assertEqual(OBSERVED, elements[uid('south')]['Provenance'])
        self.assertEqual(OBSERVED, elements[uid('east')]['Provenance'])
        self.assertTrue(all(e['Provenance'] == OBSERVED for e in report['project']['Elements']))
        self.assertIn(('wall', by[uid('north')].GlobalId, 'wall_axis_body_mismatch'), self.unmapped(report))

    def test_opening_on_another_storey_than_its_host(self):
        f, e = self.room()
        storey = self.worker.root(f, 'IfcBuildingStorey', 'twin', CompositionType='ELEMENT', Elevation=0., ObjectPlacement=self.worker.placement(f, (0, 0, 0)))
        building = f.by_type('IfcBuilding')[0]
        self.worker.root(f, 'IfcRelAggregates', 'twin', RelatingObject=building, RelatedObjects=[storey])
        rel = e['door'].ContainedInStructure[0]
        rel.RelatedElements = [x for x in rel.RelatedElements if x != e['door']]
        self.worker.root(f, 'IfcRelContainedInSpatialStructure', 'twin contents', RelatingStructure=storey, RelatedElements=[e['door']])
        report = self.changed(f)
        self.assertEqual({('door', e['door'].GlobalId, 'host_level_mismatch')}, self.unmapped(report))
        self.assertIn(derived(f.by_type('IfcProject')[0].GlobalId, storey.GlobalId), {x['Id'] for x in report['project']['Elements']})

    def test_opening_with_two_fills_unmaps_both(self):
        f, e = self.room()
        twin = self.worker.root(f, 'IfcDoor', 'twin', ObjectPlacement=e['door'].ObjectPlacement, Representation=e['door'].Representation, OverallWidth=900., OverallHeight=2100.)
        rel = e['door'].ContainedInStructure[0]
        rel.RelatedElements = list(rel.RelatedElements) + [twin]
        self.worker.root(f, 'IfcRelFillsElement', 'Fills', RelatingOpeningElement=e['door'].FillsVoids[0].RelatingOpeningElement, RelatedBuildingElement=twin)
        report = self.changed(f)
        self.assertEqual({('door', e['door'].GlobalId, 'opening_multiple_fills'), ('door', twin.GlobalId, 'opening_multiple_fills')}, self.unmapped(report))

    def test_coordinates_beyond_range_are_unmapped(self):
        f, by = self.owned()
        f.by_type('IfcBuilding')[0].ObjectPlacement.RelativePlacement.Location.Coordinates = (2e5, 0.0, 0.0)
        report = self.changed(f)
        reasons = {}
        for u in report['unmapped']:
            reasons.setdefault(u['category'], set()).add(u['reason'])
        self.assertEqual({'wall': {'coordinate_out_of_range'}, 'space': {'coordinate_out_of_range'}, 'door': {'host_unknown'}, 'window': {'host_unknown'}}, reasons)
        self.assertEqual({uid('level-0'), uid('level-1')}, {x['Id'] for x in report['project']['Elements']})

    def test_revision_outside_int32_is_refused(self):
        path = self.dir / 'owned.ifc'
        self.ifc.export_project(source_project(), path)
        text = path.read_text(encoding='utf-8')
        self.assertEqual(1, text.count('IFCINTEGER(7)'))
        big = self.dir / 'big.ifc'
        big.write_text(text.replace('IFCINTEGER(7)', f'IFCINTEGER({2**40})'), encoding='utf-8')
        self.assertCode('ifc_project_revision', lambda: self.ifc.import_ifc(big))

    def test_element_count_is_capped(self):
        path = self.dir / 'room.ifc'
        self.worker.create(path)
        original = self.ifc.MAX_ELEMENTS
        self.ifc.MAX_ELEMENTS = 3
        try:
            self.assertCode('ifc_too_large', lambda: self.ifc.import_ifc(path))
        finally:
            self.ifc.MAX_ELEMENTS = original

    def test_output_appearing_before_publication_is_not_replaced(self):
        out = self.dir / 'raced.ifc'
        real = self.ifc.os.link

        def racing(source, target):
            pathlib.Path(target).write_bytes(b'won the race')
            return real(source, target)

        self.ifc.os.link = racing
        try:
            self.assertCode('output_exists', lambda: self.ifc.export_project(source_project(), out))
        finally:
            self.ifc.os.link = real
        self.assertEqual(b'won the race', out.read_bytes())
        self.assertEqual(['raced.ifc'], sorted(p.name for p in self.dir.iterdir()))

    def test_untested_reasons(self):
        def nudge(point, dx=0.0, dy=0.0, dz=0.0):
            c = list(point.Coordinates)
            point.Coordinates = (c[0] + dx, c[1] + dy, c[2] + dz)

        def opening(e):
            return e['door'].FillsVoids[0].RelatingOpeningElement

        def profile(entity):
            return entity.Representation.Representations[0].Items[0].SweptArea

        def uncontain(e):
            rel = e['west'].ContainedInStructure[0]
            rel.RelatedElements = [x for x in rel.RelatedElements if x != e['west']]

        def circle(f, e):
            e['west'].Representation.Representations[0].Items[0].SweptArea = f.create_entity('IfcCircleProfileDef', ProfileType='AREA', Radius=100.)

        def unplace(e):
            e['level'].ObjectPlacement = None
            e['level'].Elevation = None

        cases = [
            ('door', 'door', 'opening_not_aligned_with_host', lambda f, e: setattr(profile(opening(e)).Position, 'RefDirection', f.create_entity('IfcDirection', DirectionRatios=(0.6, 0.8)))),
            ('door', 'door', 'opening_outside_host', lambda f, e: nudge(opening(e).ObjectPlacement.RelativePlacement.Location, dx=4000.)),
            ('door', 'door', 'opening_partial_depth', lambda f, e: setattr(profile(opening(e)), 'YDim', 100.)),
            ('door', 'door', 'opening_dimension_conflict', lambda f, e: setattr(e['door'], 'OverallWidth', 1000.)),
            ('room', 'space', 'space_base_offset_unsupported', lambda f, e: nudge(e['room'].ObjectPlacement.RelativePlacement.Location, dz=50.)),
            ('west', 'wall', 'unsupported_representation', circle),
            ('west', 'wall', 'level_unknown', lambda f, e: uncontain(e)),
            ('level', 'level', 'missing_data', lambda f, e: unplace(e)),
        ]
        for index, (role, category, reason, mutate) in enumerate(cases):
            with self.subTest(reason=reason):
                f, e = self.room()
                gid = e[role].GlobalId
                mutate(f, e)
                self.assertIn((category, gid, reason), self.unmapped(self.changed(f, f'case-{index}.ifc')))

    def test_global_id_missing(self):
        f, e = self.room()
        e['west'].GlobalId = ''
        report = self.changed(f)
        self.assertIn(('wall', '', 'global_id_missing'), self.unmapped(report))


if __name__ == '__main__':
    unittest.main()
