import importlib.util
import pathlib
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]

class IfcIntegration(unittest.TestCase):
    def test_resize_preserves_foreign_pending_file(self):
        spec = importlib.util.spec_from_file_location('ifc_worker', ROOT/'runtimes/ifc/worker.py')
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        (ROOT / '.bim/ifc-tests').mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / '.bim/ifc-tests') as directory:
            path = pathlib.Path(directory)/'room.ifc'
            module.create(path)
            original = path.read_bytes()
            pending = path.with_suffix('.pending.ifc')
            pending.write_bytes(b'foreign pending artifact')
            with self.assertRaises(ValueError):
                module.resize(path, 5400, 'resize')
            self.assertEqual(original, path.read_bytes())
            self.assertEqual(b'foreign pending artifact', pending.read_bytes())

    def test_refuses_foreign_artifact_without_writes(self):
        spec = importlib.util.spec_from_file_location('ifc_worker', ROOT/'runtimes/ifc/worker.py')
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        (ROOT / '.bim/ifc-tests').mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / '.bim/ifc-tests') as directory:
            path = pathlib.Path(directory)
            (path/'before.ifc').write_bytes(b'foreign IFC must survive')
            with self.assertRaises(ValueError):
                module.execute(path)
            self.assertEqual({'before.ifc'}, {p.name for p in path.iterdir()})
            self.assertEqual(b'foreign IFC must survive', (path/'before.ifc').read_bytes())

    def test_shifted_opening_profile_is_rejected(self):
        spec = importlib.util.spec_from_file_location('ifc_worker', ROOT/'runtimes/ifc/worker.py')
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        (ROOT / '.bim/ifc-tests').mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / '.bim/ifc-tests') as directory:
            path = pathlib.Path(directory)/'room.ifc'
            module.create(path)
            f = module.ifcopenshell.open(str(path))
            opening = module.entities(f)['door'].FillsVoids[0].RelatingOpeningElement
            profile = opening.Representation.Representations[0].Items[0].SweptArea
            xy = list(profile.Position.Location.Coordinates)
            xy[0] += 100
            profile.Position.Location.Coordinates = tuple(xy)
            f.write(str(path))
            with self.assertRaises(ValueError):
                module.inspect(path)

    def test_actual_roundtrip_revision_receipts_and_geometry(self):
        worker = ROOT / 'runtimes/ifc/worker.py'
        self.assertTrue(worker.exists(), 'actual IFC worker missing')
        spec = importlib.util.spec_from_file_location('ifc_worker', worker)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        (ROOT / '.bim/ifc-tests').mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / '.bim/ifc-tests') as directory:
            result = module.execute(pathlib.Path(directory))
            self.assertFalse(result['errors'])
            self.assertTrue(all(c['outcome'] == 'pass' for c in result['checks']), result['checks'])
            a = result['observations']['test_a']
            b = result['observations']['test_b']
            self.assertEqual((5000, 4000), (a['clear_width_mm'], a['clear_depth_mm']))
            self.assertEqual(5400, b['after']['clear_width_mm'])
            self.assertEqual(a, b['rollback'])
            self.assertEqual(b['after'], b['retry'])
            self.assertEqual({'east','south','north','window-east','room'}, set(b['semantic_changed_ids']))
            self.assertEqual(9, len(a['elements']))
            self.assertEqual({'Level','Wall','Door','Window','Room'}, {e['category'] for e in a['elements']})
            self.assertEqual({e['logical_id']:e['native_id'] for e in a['elements']}, {e['logical_id']:e['native_id'] for e in b['after']['elements']})
            self.assertEqual(21.6, next(e['area_m2'] for e in b['after']['elements'] if e['category']=='Room'))
            path = pathlib.Path(directory)/'room.ifc'
            original = path.read_bytes()
            # Translating the fill without its void must fail independent geometry inspection.
            f = module.ifcopenshell.open(str(path))
            door = module.entities(f)['door']
            xyz = list(door.ObjectPlacement.RelativePlacement.Location.Coordinates)
            xyz[0] += 100
            door.ObjectPlacement.RelativePlacement.Location.Coordinates = tuple(xyz)
            corrupt = pathlib.Path(directory)/'shifted-fill.ifc'
            f.write(str(corrupt))
            with self.assertRaises(ValueError):
                module.inspect(corrupt)
            f = module.ifcopenshell.open(str(path))
            wall = module.entities(f)['west']
            profile = wall.Representation.Representations[0].Items[0].SweptArea
            xy = list(profile.Position.Location.Coordinates)
            xy[0] += 100
            profile.Position.Location.Coordinates = tuple(xy)
            corrupt = pathlib.Path(directory)/'shifted-profile.ifc'
            f.write(str(corrupt))
            with self.assertRaises(ValueError):
                module.inspect(corrupt)
            for width in [-1, float('nan'), 500, '5400', None]:
                with self.assertRaises((ValueError,TypeError)):
                    module.resize(path, width, 'malformed')
                self.assertEqual(original,path.read_bytes())
            with self.assertRaises(ValueError):
                module.resize(path, 5800, 'resize-400')
            self.assertEqual(original,path.read_bytes())

if __name__ == '__main__':
    unittest.main()

