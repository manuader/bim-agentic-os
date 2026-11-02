"""Real rhino3dm (MIT) regressions for the BIM-IR <-> .3dm adapter. No Rhino installation is used."""
import copy
import importlib.util
import json
import math
import pathlib
import subprocess
import sys
import tempfile
import unittest
import uuid

import rhino3dm

ROOT = pathlib.Path(__file__).resolve().parents[2]
ADAPTER = ROOT / 'adapters/rhino/bim_rhino.py'
_spec = importlib.util.spec_from_file_location('bim_rhino', ADAPTER)
br = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(br)

FIXTURE = json.loads((ROOT / 'tests/rhino/fixtures/room.json').read_text(encoding='utf-8'))
EXAMPLE_TEXT = (ROOT / 'examples/after.json').read_text(encoding='utf-8').strip()
TOL = 1e-9


def elements_by_id(project):
    return {e['Id']: e for e in project['Elements']}


class Base(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = pathlib.Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def path(self, name):
        return self.dir / name

    def exported(self, project=FIXTURE, name='model.3dm'):
        out = self.path(name)
        br.export_project(copy.deepcopy(project), out)
        return out

    def assertRefused(self, code, fn, *args):
        with self.assertRaises(br.RhinoAdapterError) as ctx:
            fn(*args)
        self.assertEqual(code, ctx.exception.code, str(ctx.exception))
        self.assertTrue(str(ctx.exception).startswith(code), str(ctx.exception))

    def assertProjectClose(self, expected, actual):
        self.assertEqual(expected['Id'], actual['Id'])
        self.assertEqual(expected['Revision'], actual['Revision'])
        self.assertEqual([e['Id'] for e in expected['Elements']], [e['Id'] for e in actual['Elements']])
        for want, got in zip(expected['Elements'], actual['Elements']):
            self.assertEqual(want['Kind'], got['Kind'])
            self.assertEqual(want['Dependencies'], got['Dependencies'])
            self.assertEqual(want['Provenance'], got['Provenance'])
            self.assertEqual(len(want['GeometryMetres']), len(got['GeometryMetres']), want['Id'])
            for a, b in zip(want['GeometryMetres'], got['GeometryMetres']):
                self.assertLessEqual(abs(a - b), TOL, (want['Id'], a, b))

    def rewrite(self, source, edit=None, extra=(), units=None, root_strings=None, name='tampered.3dm'):
        """Copy an exported file into a fresh rhino3dm model, editing objects on the way, and save it.

        edit(geometry, strings, layer_path) returns (geometry, strings, layer_path, id) or None to drop it.
        Nothing is mutated in place, so no assumption is made about rhino3dm reference semantics.
        """
        old = rhino3dm.File3dm.Read(str(source))
        model = rhino3dm.File3dm()
        model.Settings.ModelUnitSystem = units if units is not None else old.Settings.ModelUnitSystem
        layer_index = {}
        for layer in old.Layers:
            copy_layer = rhino3dm.Layer()
            copy_layer.Name = layer.Name
            parent = [p for p in old.Layers if str(p.Id) == str(layer.ParentLayerId)]
            if parent:
                copy_layer.ParentLayerId = model.Layers[layer_index[parent[0].FullPath]].Id
            strings = dict(layer.GetUserStrings())
            if layer.FullPath == 'BIM' and root_strings:
                strings.update(root_strings)
            for key, value in sorted(strings.items()):
                copy_layer.SetUserString(key, value)
            layer_index[layer.FullPath] = model.Layers.Add(copy_layer)
        records = []
        for obj in old.Objects:
            record = (obj.Geometry, dict(obj.Attributes.GetUserStrings()), old.Layers[obj.Attributes.LayerIndex].FullPath, obj.Attributes.Id)
            if edit is not None:
                record = edit(*record)
            if record is not None:
                records.append(record)
        records.extend(extra)
        for geometry, strings, layer_path, object_id in records:
            attributes = rhino3dm.ObjectAttributes()
            attributes.LayerIndex = layer_index[layer_path]
            if object_id is not None:
                attributes.Id = object_id
            for key, value in sorted(strings.items()):
                attributes.SetUserString(key, value)
            model.Objects.Add(geometry, attributes)
        out = self.path(name)
        self.assertTrue(model.Write(str(out), 7))
        return out


def only(kind, change):
    """Edit only the first object of a BIM kind."""
    seen = []
    def edit(geometry, strings, layer_path, object_id):
        if strings.get('bim.kind') == kind and not seen:
            seen.append(object_id)
            return change(geometry, strings, layer_path, object_id)
        return geometry, strings, layer_path, object_id
    return edit


class ExportFile(Base):
    def test_model_units_are_metres(self):
        model = rhino3dm.File3dm.Read(str(self.exported()))
        self.assertEqual(rhino3dm.UnitSystem.Meters, model.Settings.ModelUnitSystem)

    def test_layer_per_category_and_project_metadata_on_root_layer(self):
        model = rhino3dm.File3dm.Read(str(self.exported()))
        paths = sorted(layer.FullPath for layer in model.Layers)
        self.assertEqual(['BIM', 'BIM::Documentation', 'BIM::Levels', 'BIM::Spaces', 'BIM::Walls'], paths)
        root = [layer for layer in model.Layers if layer.FullPath == 'BIM'][0]
        self.assertEqual(br.SCHEMA, root.GetUserString('bim.schema'))
        self.assertEqual(FIXTURE['Id'], root.GetUserString('bim.project.id'))
        self.assertEqual('4', root.GetUserString('bim.project.revision'))
        expected_layer = {'wall': 'BIM::Walls', 'level': 'BIM::Levels', 'space': 'BIM::Spaces',
                          'plan': 'BIM::Documentation', 'schedule': 'BIM::Documentation', 'sheet': 'BIM::Documentation'}
        for obj in model.Objects:
            kind = obj.Attributes.GetUserString('bim.kind')
            self.assertEqual(expected_layer[kind], model.Layers[obj.Attributes.LayerIndex].FullPath)

    def test_rhino_object_ids_and_user_strings_carry_bim_identity(self):
        model = rhino3dm.File3dm.Read(str(self.exported()))
        objects = list(model.Objects)
        self.assertEqual(len(FIXTURE['Elements']), len(objects))
        by_id = elements_by_id(FIXTURE)
        for obj in objects:
            a = obj.Attributes
            element = by_id[str(a.Id)]
            self.assertEqual(element['Id'], a.GetUserString('bim.id'))
            self.assertEqual(element['Kind'], a.GetUserString('bim.kind'))
            self.assertEqual(element['Dependencies'], json.loads(a.GetUserString('bim.dependencies')))
            self.assertEqual(element['Provenance']['Source'], a.GetUserString('bim.provenance.source'))
            self.assertEqual(element['Provenance']['Approval'], a.GetUserString('bim.provenance.approval'))
        diagonal = [o for o in objects if str(o.Attributes.Id) == '00000000-0000-0000-0000-000000000013'][0]
        self.assertEqual('diagonal wall, café', diagonal.Attributes.GetUserString('bim.provenance.source'))

    def test_walls_are_extrusions_along_their_axis_at_level_elevation(self):
        model = rhino3dm.File3dm.Read(str(self.exported()))
        objects = {str(o.Attributes.Id): o for o in model.Objects}
        diagonal = objects['00000000-0000-0000-0000-000000000013'].Geometry
        self.assertIsInstance(diagonal, rhino3dm.Extrusion)
        self.assertTrue(diagonal.IsValid)
        self.assertTrue(diagonal.IsSolid)
        s, e = diagonal.PathStart, diagonal.PathEnd
        self.assertEqual((0.0, 4.2, 3.5), (s.X, s.Y, s.Z))
        self.assertEqual((-1.5, 2.0, 3.5), (e.X, e.Y, e.Z))
        box = diagonal.GetBoundingBox()
        self.assertAlmostEqual(3.5, box.Min.Z, places=12)
        self.assertAlmostEqual(3.5 + 2.75, box.Max.Z, places=12)
        # A wall without a level dependency sits at z=0.
        free = objects['00000000-0000-0000-0000-000000000014'].Geometry
        self.assertEqual(0.0, free.PathStart.Z)
        level = objects['00000000-0000-0000-0000-000000000010'].Geometry
        self.assertIsInstance(level, rhino3dm.Point)
        self.assertEqual(3.5, level.Location.Z)
        space = objects['00000000-0000-0000-0000-000000000020'].Geometry
        self.assertIsInstance(space, rhino3dm.PolylineCurve)
        self.assertTrue(space.IsClosed)
        corners = sorted({(space.Point(i).X, space.Point(i).Y, space.Point(i).Z) for i in range(space.PointCount)})
        self.assertEqual([(0.1, 0.1, 3.5), (0.1, 4.1, 3.5), (5.1, 0.1, 3.5), (5.1, 4.1, 3.5)], corners)

    def test_existing_output_is_refused_and_left_untouched(self):
        out = self.path('model.3dm')
        out.write_bytes(b'keep')
        self.assertRefused('output_exists', br.export_project, copy.deepcopy(FIXTURE), out)
        self.assertEqual(b'keep', out.read_bytes())


class Roundtrip(Base):
    def test_fixture_roundtrip_preserves_ids_order_dependencies_and_geometry(self):
        self.assertProjectClose(FIXTURE, br.import_project(self.exported()))

    def test_core_example_roundtrip_is_byte_identical_core_json(self):
        project = json.loads(EXAMPLE_TEXT)
        imported = br.import_project(self.exported(project))
        self.assertEqual(EXAMPLE_TEXT, br.dumps(imported))

    def test_export_import_export_is_stable(self):
        first = br.import_project(self.exported())
        second = br.import_project(self.exported(first, 'second.3dm'))
        third = br.import_project(self.exported(second, 'third.3dm'))
        self.assertEqual(br.dumps(first), br.dumps(second))
        self.assertEqual(br.dumps(second), br.dumps(third))
        ids = lambda p: sorted(str(o.Attributes.Id) for o in rhino3dm.File3dm.Read(str(p)).Objects)
        self.assertEqual(ids(self.path('model.3dm')), ids(self.path('second.3dm')))

    def test_millimetre_model_is_scaled_to_metres(self):
        scale = rhino3dm.Transform.Scale(rhino3dm.Point3d(0, 0, 0), 1000.0)
        def to_mm(geometry, strings, layer_path, object_id):
            self.assertTrue(geometry.Transform(scale))
            return geometry, strings, layer_path, object_id
        source = self.rewrite(self.exported(), to_mm, units=rhino3dm.UnitSystem.Millimeters)
        self.assertProjectClose(FIXTURE, br.import_project(source))

    def test_faithful_copy_imports_identically(self):
        # Guards the tamper helper: an unedited copy must import exactly like the original export.
        original = self.exported()
        self.assertEqual(br.dumps(br.import_project(original)), br.dumps(br.import_project(self.rewrite(original))))

    def test_cli_export_then_import(self):
        source = self.path('in.json')
        source.write_text(EXAMPLE_TEXT, encoding='utf-8')
        out = self.path('cli.3dm')
        exported = subprocess.run([sys.executable, '-I', str(ADAPTER), 'export', str(source), str(out)], capture_output=True, text=True, timeout=60)
        self.assertEqual(0, exported.returncode, exported.stderr)
        self.assertEqual({'status': 'exported', 'elements': 2, 'path': str(out)}, json.loads(exported.stdout))
        imported = subprocess.run([sys.executable, '-I', str(ADAPTER), 'import', str(out)], capture_output=True, text=True, timeout=60)
        self.assertEqual(0, imported.returncode, imported.stderr)
        self.assertEqual(EXAMPLE_TEXT, imported.stdout.strip())

    def test_cli_failure_is_one_json_error_and_exit_one(self):
        bad = self.path('bad.3dm')
        bad.write_bytes(b'not a rhino model')
        result = subprocess.run([sys.executable, '-I', str(ADAPTER), 'import', str(bad)], capture_output=True, text=True, timeout=60)
        self.assertEqual(1, result.returncode)
        self.assertEqual('', result.stdout)
        self.assertEqual('rhino_read_failed', json.loads(result.stderr)['error'])


def mutated(fn):
    project = copy.deepcopy(FIXTURE)
    fn(project)
    return project


class ExportRefusals(Base):
    def refuse(self, code, fn):
        out = self.path('refused.3dm')
        self.assertRefused(code, br.export_project, mutated(fn), out)
        self.assertFalse(out.exists())

    def test_unknown_kind(self):
        self.refuse('geometry_kind_unsupported', lambda p: p['Elements'][1].update(Kind='beam'))

    def test_curved_wall_arity_is_not_in_the_codec(self):
        # The Core GeometryCodec has no arc walls; an extra "bulge" value must not be guessed into an arc.
        self.refuse('wall_geometry', lambda p: p['Elements'][1]['GeometryMetres'].append(0.5))

    def test_zero_length_wall(self):
        self.refuse('wall_geometry', lambda p: p['Elements'][1].update(GeometryMetres=[1, 1, 1, 1, 3, 0.2]))

    def test_non_positive_thickness(self):
        self.refuse('wall_geometry', lambda p: p['Elements'][1].update(GeometryMetres=[0, 0, 1, 0, 3, 0]))

    def test_non_finite_coordinate(self):
        self.refuse('geometry_non_finite', lambda p: p['Elements'][0].update(GeometryMetres=[math.nan]))

    def test_space_arity(self):
        self.refuse('space_geometry', lambda p: p['Elements'][5]['GeometryMetres'].pop())

    def test_documentation_with_geometry(self):
        self.refuse('documentation_geometry', lambda p: p['Elements'][6].update(GeometryMetres=[1.0]))

    def test_duplicate_id(self):
        self.refuse('duplicate_id', lambda p: p['Elements'][2].update(Id=p['Elements'][1]['Id']))

    def test_dangling_dependency(self):
        self.refuse('missing_dependency', lambda p: p['Elements'][1]['Dependencies'].append(str(uuid.UUID(int=99))))

    def test_dependency_cycle(self):
        self.refuse('dependency_cycle', lambda p: p['Elements'][0]['Dependencies'].append(p['Elements'][1]['Id']))

    def test_unknown_member_is_not_a_bim_ir_record(self):
        self.refuse('bim_ir_invalid', lambda p: p['Elements'][1].update(Level='L1'))

    def test_malformed_uuid(self):
        self.refuse('bim_ir_invalid', lambda p: p['Elements'][1].update(Id='wall-1'))

    def test_boolean_is_not_a_number(self):
        self.refuse('bim_ir_invalid', lambda p: p['Elements'][0].update(GeometryMetres=[True]))


class ImportRefusals(Base):
    def test_garbage_bytes(self):
        bad = self.path('bad.3dm')
        bad.write_bytes(b'not a rhino model')
        self.assertRefused('rhino_read_failed', br.import_project, bad)

    def test_missing_file(self):
        self.assertRefused('rhino_read_failed', br.import_project, self.path('absent.3dm'))

    def test_foreign_model_without_bim_root_layer(self):
        model = rhino3dm.File3dm()
        model.Settings.ModelUnitSystem = rhino3dm.UnitSystem.Meters
        model.Objects.AddPoint(rhino3dm.Point3d(1, 2, 3))
        out = self.path('foreign.3dm')
        self.assertTrue(model.Write(str(out), 7))
        self.assertRefused('rhino_not_bim', br.import_project, out)

    def test_unknown_schema_marker(self):
        source = self.rewrite(self.exported(), root_strings={'bim.schema': 'bim-agentic-os.rhino/999'})
        self.assertRefused('rhino_schema_unsupported', br.import_project, source)

    def test_feet_model_is_refused_not_guessed(self):
        source = self.rewrite(self.exported(), units=rhino3dm.UnitSystem.Feet)
        self.assertRefused('rhino_units_unsupported', br.import_project, source)

    def test_foreign_object_in_bim_model(self):
        stray = (rhino3dm.Point(rhino3dm.Point3d(1, 2, 3)), {}, 'BIM::Walls', None)
        self.assertRefused('rhino_foreign_object', br.import_project, self.rewrite(self.exported(), extra=[stray]))

    def test_copied_object_keeps_strings_but_not_identity(self):
        copies = []
        def edit(geometry, strings, layer_path, object_id):
            if strings.get('bim.kind') == 'wall' and not copies:
                copies.append((geometry.Duplicate(), dict(strings), layer_path, None))
            return geometry, strings, layer_path, object_id
        source = self.exported()
        self.rewrite(source, edit, name='probe.3dm')
        self.assertRefused('rhino_identity_mismatch', br.import_project, self.rewrite(source, extra=copies))

    def test_wall_replaced_by_vertical_box_extrusion(self):
        outline = rhino3dm.PolylineCurve([rhino3dm.Point3d(0, 0, 0), rhino3dm.Point3d(5, 0, 0), rhino3dm.Point3d(5, 0.2, 0), rhino3dm.Point3d(0, 0.2, 0), rhino3dm.Point3d(0, 0, 0)])
        box = rhino3dm.Extrusion.Create(outline, 3.0, True)
        edit = only('wall', lambda g, s, l, i: (box, s, l, i))
        self.assertRefused('rhino_wall_geometry', br.import_project, self.rewrite(self.exported(), edit))

    def test_wall_kind_with_point_geometry(self):
        edit = only('wall', lambda g, s, l, i: (rhino3dm.Point(rhino3dm.Point3d(0, 0, 0)), s, l, i))
        self.assertRefused('rhino_geometry_mismatch', br.import_project, self.rewrite(self.exported(), edit))

    def test_object_moved_to_another_category_layer(self):
        edit = only('level', lambda g, s, l, i: (g, s, 'BIM::Walls', i))
        self.assertRefused('rhino_layer_mismatch', br.import_project, self.rewrite(self.exported(), edit))

    def test_dangling_dependency_in_user_strings(self):
        dangling = json.dumps([str(uuid.UUID(int=99))])
        edit = only('wall', lambda g, s, l, i: (g, {**s, 'bim.dependencies': dangling}, l, i))
        self.assertRefused('missing_dependency', br.import_project, self.rewrite(self.exported(), edit))

    def test_missing_identity_string(self):
        edit = only('space', lambda g, s, l, i: (g, {k: v for k, v in s.items() if k != 'bim.order'}, l, i))
        self.assertRefused('rhino_object_invalid', br.import_project, self.rewrite(self.exported(), edit))

    def test_rotated_space_is_not_an_axis_aligned_rectangle(self):
        def rotate(geometry, strings, layer_path, object_id):
            self.assertTrue(geometry.Rotate(0.3, rhino3dm.Vector3d(0, 0, 1), rhino3dm.Point3d(0, 0, 0)))
            return geometry, strings, layer_path, object_id
        self.assertRefused('rhino_space_geometry', br.import_project, self.rewrite(self.exported(), only('space', rotate)))

if __name__ == '__main__':
    unittest.main()
