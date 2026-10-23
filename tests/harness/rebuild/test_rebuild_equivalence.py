"""Correctness gate for the P01 baseline: both paths must reach the same corrected model before any timing."""
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[2]
sys.path.insert(0, str(HERE))
import rebuild_benchmark as rb  # noqa: E402


def packet_path():
    supplied = os.environ.get('BIM_REBUILD_PACKET')
    if supplied:
        return pathlib.Path(supplied)
    dotnet = os.environ.get('BIM_DOTNET') or shutil.which('dotnet')
    if not dotnet:
        raise RuntimeError('dotnet SDK (or BIM_REBUILD_PACKET) required to obtain the Core compiled packet')
    target = ROOT / '.bim' / 'rebuild-test' / 'compiled-5.json'
    rb.generate_packet(dotnet, target)
    return target


class RebuildEquivalence(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.packet = json.loads(packet_path().read_text(encoding='utf-8-sig'))

    def test_correction_is_plus_400_mm_width(self):
        compiled = rb.w.validate_request(self.packet)
        self.assertEqual(5000, compiled['a']['width'])
        self.assertEqual(5400, compiled['b']['width'])
        self.assertTrue(compiled['plan']['Operations'])

    def test_incremental_and_rebuild_reach_geometrically_equivalent_models(self):
        with tempfile.TemporaryDirectory() as tmp:
            tmp = pathlib.Path(tmp)
            inc = rb.run_incremental(tmp / 'inc', self.packet)
            reb = rb.run_rebuild(tmp / 'reb', self.packet)
            # Independent QA of both published files through the worker's own readback.
            rb.w.inspect(inc['path'])
            rb.w.inspect(reb['path'])
            evidence = rb.equivalence(inc['path'], reb['path'], self.packet)
            self.assertTrue(evidence['equivalent'], evidence)
            self.assertEqual(5400, evidence['clear_width_mm'])
            self.assertEqual(12, evidence['products_compared'])
            self.assertEqual(9, len(rb.w.observe(inc['path'])['elements']))

    def test_incremental_preserves_all_global_ids(self):
        with tempfile.TemporaryDirectory() as tmp:
            inc = rb.run_incremental(pathlib.Path(tmp) / 'inc', self.packet)
            identity = rb.identity_report(inc['baseline'], inc['path'])
            self.assertEqual(identity['root_total_before'], identity['root_preserved'])
            self.assertEqual(identity['products_total'], identity['products_preserved'])

    def test_equivalence_detects_a_geometric_difference(self):
        with tempfile.TemporaryDirectory() as tmp:
            tmp = pathlib.Path(tmp)
            inc = rb.run_incremental(tmp / 'inc', self.packet)
            reb = rb.run_rebuild(tmp / 'reb', self.packet)
            f = rb.w.ifcopenshell.open(str(reb['path']))
            rb.w.alter(rb.w.entities(f)['north'], (-200, 4000, 0, 5800, 200, 3000))
            drifted = tmp / 'drifted.ifc'
            f.write(str(drifted))
            self.assertFalse(rb.equivalence(inc['path'], drifted, self.packet)['equivalent'])


if __name__ == '__main__':
    unittest.main()
