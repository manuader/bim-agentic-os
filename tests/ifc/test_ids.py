"""IDS subset checks against an IFC exported by the isolated IFC runtime."""
import importlib.util
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[2]
CHECKER = ROOT / 'runtimes/ifc/ids_check.py'
FIXTURES = ROOT / 'tests/ifc/fixtures/ids'
NS = 'xmlns:ids="http://standards.buildingsmart.org/IDS" xmlns:xs="http://www.w3.org/2001/XMLSchema"'


_LOADED = {}


def load(name, path):
    if name in _LOADED:
        return _LOADED[name]
    if not path.exists():
        raise AssertionError(f'{path.relative_to(ROOT)} missing: IDS checker not implemented')
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    _LOADED[name] = module
    return module


def value(xml):
    ids = load('bim_ids_check', CHECKER)
    return ids.parse_value(ET.fromstring(f'<ids:value {NS}>{xml}</ids:value>'))


def ids_document(specification):
    return (f'<?xml version="1.0"?><ids:ids {NS}><ids:info><ids:title>t</ids:title></ids:info>'
            f'<ids:specifications>{specification}</ids:specifications></ids:ids>')


WALL_APPLICABILITY = ('<ids:applicability minOccurs="1" maxOccurs="unbounded"><ids:entity><ids:name>'
                      '<ids:simpleValue>IFCWALL</ids:simpleValue></ids:name></ids:entity></ids:applicability>')


class IdsRestrictionParsing(unittest.TestCase):
    """Pure parser tests; no IfcOpenShell needed."""

    def setUp(self):
        self.ids = load('bim_ids_check', CHECKER)

    def assertCode(self, code, text):
        with self.assertRaises(self.ids.IdsError) as caught:
            self.ids.parse_ids(text)
        self.assertEqual(code, caught.exception.code, str(caught.exception))

    def test_simple_value_is_exact_and_case_sensitive(self):
        r = value('<ids:simpleValue>room</ids:simpleValue>')
        self.assertEqual('simpleValue', r.kind)
        self.assertTrue(r.matches('room'))
        self.assertFalse(r.matches('Room'))
        self.assertFalse(r.matches(None))

    def test_numeric_simple_value_compares_numerically(self):
        r = value('<ids:simpleValue>3000</ids:simpleValue>')
        self.assertTrue(r.matches(3000.0))
        self.assertTrue(r.matches(3000))
        self.assertFalse(r.matches(3000.5))
        self.assertTrue(value('<ids:simpleValue>TRUE</ids:simpleValue>').matches(True))
        self.assertFalse(value('<ids:simpleValue>TRUE</ids:simpleValue>').matches(False))

    def test_enumeration(self):
        r = value('<xs:restriction base="xs:string"><xs:enumeration value="A"/><xs:enumeration value="B"/></xs:restriction>')
        self.assertEqual('enumeration', r.kind)
        self.assertEqual(('A', 'B'), r.values)
        self.assertTrue(r.matches('B'))
        self.assertFalse(r.matches('C'))

    def test_pattern_is_implicitly_anchored(self):
        r = value('<xs:restriction base="xs:string"><xs:pattern value="EI[0-9]{2}"/></xs:restriction>')
        self.assertEqual('pattern', r.kind)
        self.assertTrue(r.matches('EI30'))
        self.assertFalse(r.matches('xEI30'))
        self.assertFalse(r.matches('EI300'))

    def test_unsupported_restrictions_fail_closed(self):
        for xml in ['<xs:restriction base="xs:double"><xs:minInclusive value="1"/></xs:restriction>',
                    '<xs:restriction base="xs:string"><xs:length value="2"/></xs:restriction>',
                    '<xs:restriction base="xs:string"><xs:pattern value="\\p{L}+"/></xs:restriction>',
                    '<xs:restriction base="xs:string"><xs:pattern value="^A$"/></xs:restriction>',
                    '<xs:restriction base="xs:string"><xs:pattern value="(?i)a"/></xs:restriction>',
                    '<xs:restriction base="xs:string"><xs:enumeration value="A"/><xs:pattern value="A"/></xs:restriction>']:
            with self.subTest(xml=xml), self.assertRaises(self.ids.IdsError) as caught:
                value(xml)
            self.assertEqual('IDS_UNSUPPORTED_RESTRICTION', caught.exception.code)

    def test_empty_or_invalid_values_are_invalid(self):
        for xml in ['', '<ids:simpleValue>a</ids:simpleValue><ids:simpleValue>b</ids:simpleValue>',
                    '<xs:restriction base="xs:string"/>',
                    '<xs:restriction base="xs:string"><xs:pattern value="(unclosed"/></xs:restriction>']:
            with self.subTest(xml=xml), self.assertRaises(self.ids.IdsError) as caught:
                value(xml)
            self.assertEqual('IDS_INVALID', caught.exception.code)

    def test_document_level_rejections(self):
        self.assertCode('IDS_DOCTYPE_FORBIDDEN', '<?xml version="1.0"?><!DOCTYPE x [<!ENTITY a "b">]><x/>')
        self.assertCode('IDS_DOCTYPE_FORBIDDEN', '<x><!ENTITY a "b"></x>')
        self.assertCode('IDS_XML_MALFORMED', '<ids:ids')
        self.assertCode('IDS_NOT_IDS', '<ids xmlns="urn:other"/>')
        self.assertCode('IDS_INVALID', ids_document('<ids:specification name="s">' + WALL_APPLICABILITY + '</ids:specification>'))
        self.assertCode('IDS_INVALID', ids_document(''))
        self.assertCode('IDS_UNSUPPORTED_FACET', ids_document(
            '<ids:specification name="s" ifcVersion="IFC4">' + WALL_APPLICABILITY +
            '<ids:requirements><ids:material cardinality="required"/></ids:requirements></ids:specification>'))
        self.assertCode('IDS_UNSUPPORTED_CARDINALITY', ids_document(
            '<ids:specification name="s" ifcVersion="IFC4"><ids:applicability minOccurs="2" maxOccurs="unbounded">'
            '<ids:entity><ids:name><ids:simpleValue>IFCWALL</ids:simpleValue></ids:name></ids:entity></ids:applicability></ids:specification>'))
        self.assertCode('IDS_INVALID', ids_document(
            '<ids:specification name="s" ifcVersion="IFC4">' + WALL_APPLICABILITY +
            '<ids:requirements><ids:attribute cardinality="sometimes"><ids:name><ids:simpleValue>Name</ids:simpleValue></ids:name></ids:attribute></ids:requirements></ids:specification>'))

    def test_fixtures_parse_to_expected_shape(self):
        passing = self.ids.parse_ids((FIXTURES / 'passing.ids').read_text(encoding='utf-8'))
        self.assertEqual(['WALL-ID', 'SPACE-INTERNAL', 'NO-PROXY', 'DOOR-DATA'], [s.identifier for s in passing.specifications])
        self.assertEqual(['required', 'required', 'prohibited', 'optional'], [s.cardinality for s in passing.specifications])
        with self.assertRaises(self.ids.IdsError) as caught:
            self.ids.parse_ids((FIXTURES / 'unsupported.ids').read_text(encoding='utf-8'))
        self.assertEqual('IDS_UNSUPPORTED_FACET', caught.exception.code)


class IdsAgainstExportedIfc(unittest.TestCase):
    """Real IfcOpenShell export from the worker, checked by the IDS subset."""

    @classmethod
    def setUpClass(cls):
        cls.ids = load('bim_ids_check', CHECKER)
        cls.worker = load('ifc_worker', ROOT / 'runtimes/ifc/worker.py')
        (ROOT / '.bim/ifc-tests').mkdir(parents=True, exist_ok=True)
        cls.tmp = tempfile.TemporaryDirectory(dir=ROOT / '.bim/ifc-tests')
        cls.ifc = pathlib.Path(cls.tmp.name) / 'room.ifc'
        cls.worker.create(cls.ifc)
        model = cls.worker.ifcopenshell.open(str(cls.ifc))
        cls.ids_of = lambda kind: sorted(e.GlobalId for e in model.by_type(kind))
        cls.walls, cls.openings, cls.windows = cls.ids_of('IfcWall'), cls.ids_of('IfcOpeningElement'), cls.ids_of('IfcWindow')

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def specs(self, report):
        return {s['identifier']: s for s in report['specifications']}

    def run_cli(self, fixture):
        return subprocess.run([sys.executable, str(CHECKER), str(FIXTURES / fixture), str(self.ifc)],
                              capture_output=True, text=True, timeout=300)

    def test_passing_ids_passes_exported_ifc(self):
        report = self.ids.check(FIXTURES / 'passing.ids', self.ifc)
        self.assertEqual('pass', report['status'], json.dumps(report, indent=1))
        specs = self.specs(report)
        self.assertEqual({'WALL-ID': 4, 'SPACE-INTERNAL': 1, 'NO-PROXY': 0, 'DOOR-DATA': 1},
                         {k: v['applicable_count'] for k, v in specs.items()})
        self.assertTrue(all(s['status'] == 'pass' and s['failures'] == [] for s in specs.values()), specs)
        self.assertEqual('IFC4', report['ifc_schema'])
        self.assertEqual({'specifications': 4, 'passed': 4, 'failed': 0, 'skipped': 0}, report['counts'])

    def test_failing_ids_reports_entities_and_reasons(self):
        report = self.ids.check(FIXTURES / 'failing.ids', self.ifc)
        self.assertEqual('fail', report['status'], json.dumps(report, indent=1))
        specs = self.specs(report)
        detail = json.dumps(specs, indent=1)
        self.assertEqual({'WALL-ID': 'pass', 'OPENING-ID': 'fail', 'NO-WINDOW': 'fail', 'SLAB': 'fail', 'WALL-NAME': 'fail'},
                         {k: v['status'] for k, v in specs.items()}, detail)
        self.assertEqual(3, specs['OPENING-ID']['applicable_count'], detail)
        self.assertEqual(self.openings, [f['global_id'] for f in specs['OPENING-ID']['failures']], detail)
        self.assertTrue(all(f['entity'] == 'IfcOpeningElement' and 'BimIdentity' in f['reason'] and 'LogicalId' in f['reason']
                            for f in specs['OPENING-ID']['failures']), detail)
        self.assertEqual(self.windows, [f['global_id'] for f in specs['NO-WINDOW']['failures']], detail)
        self.assertTrue(all('prohibited' in f['reason'] for f in specs['NO-WINDOW']['failures']), detail)
        self.assertEqual(0, specs['SLAB']['applicable_count'], detail)
        self.assertIn('no applicable', specs['SLAB']['reason'], detail)
        self.assertEqual(self.walls, [f['global_id'] for f in specs['WALL-NAME']['failures']], detail)
        self.assertEqual({'specifications': 5, 'passed': 1, 'failed': 4, 'skipped': 0}, report['counts'])

    def test_cli_exit_codes_and_deterministic_report(self):
        passing = self.run_cli('passing.ids')
        self.assertEqual(0, passing.returncode, passing.stdout + passing.stderr)
        self.assertEqual(passing.stdout, self.run_cli('passing.ids').stdout)
        self.assertEqual('pass', json.loads(passing.stdout)['status'])
        failing = self.run_cli('failing.ids')
        self.assertEqual(1, failing.returncode, failing.stdout + failing.stderr)
        self.assertEqual('fail', json.loads(failing.stdout)['status'])
        invalid = self.run_cli('unsupported.ids')
        self.assertEqual(2, invalid.returncode, invalid.stdout + invalid.stderr)
        report = json.loads(invalid.stdout)
        self.assertEqual('invalid', report['status'])
        self.assertEqual('IDS_UNSUPPORTED_FACET', report['error']['code'])
        self.assertEqual([], report['specifications'])
        self.assertNotIn(str(ROOT), passing.stdout + failing.stdout + invalid.stdout, 'report leaks local paths')

    def test_unknown_entity_and_schema_mismatch(self):
        with tempfile.TemporaryDirectory(dir=ROOT / '.bim/ifc-tests') as directory:
            unknown = pathlib.Path(directory) / 'unknown.ids'
            unknown.write_text(ids_document(
                '<ids:specification name="s" ifcVersion="IFC4"><ids:applicability><ids:entity><ids:name>'
                '<ids:simpleValue>IFCNOTANENTITY</ids:simpleValue></ids:name></ids:entity></ids:applicability></ids:specification>'), encoding='utf-8')
            report = self.ids.check(unknown, self.ifc)
            self.assertEqual(('invalid', 'IDS_UNKNOWN_ENTITY'), (report['status'], report['error']['code']), report)
            other = pathlib.Path(directory) / 'other-schema.ids'
            other.write_text(ids_document('<ids:specification name="s" ifcVersion="IFC2X3">' + WALL_APPLICABILITY + '</ids:specification>'), encoding='utf-8')
            report = self.ids.check(other, self.ifc)
            # Nothing evaluated must never read as a pass.
            self.assertEqual('fail', report['status'], report)
            self.assertIn('no specification', report['reason'], report)
            self.assertEqual('skipped', report['specifications'][0]['status'], report)
            self.assertEqual({'specifications': 1, 'passed': 0, 'failed': 0, 'skipped': 1}, report['counts'])


if __name__ == '__main__':
    unittest.main()
