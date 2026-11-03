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


def spec(body, attrs='name="s" ifcVersion="IFC4"', applicability=WALL_APPLICABILITY):
    return ids_document(f'<ids:specification {attrs}>{applicability}{body}</ids:specification>')


def door_applicability(extra='', occurs='minOccurs="0" maxOccurs="unbounded"'):
    return (f'<ids:applicability {occurs}><ids:entity><ids:name><ids:simpleValue>IFCDOOR</ids:simpleValue></ids:name>'
            f'</ids:entity>{extra}</ids:applicability>')


def requirement(facets):
    return f'<ids:requirements>{facets}</ids:requirements>'


def fire_rating(cardinality, rating=None):
    v = f'<ids:value><ids:simpleValue>{rating}</ids:simpleValue></ids:value>' if rating else ''
    return (f'<ids:property cardinality="{cardinality}"><ids:propertySet><ids:simpleValue>Pset_DoorCommon</ids:simpleValue></ids:propertySet>'
            f'<ids:baseName><ids:simpleValue>FireRating</ids:simpleValue></ids:baseName>{v}</ids:property>')


class IdsReviewParsing(unittest.TestCase):
    """Independent-review regressions that need no IfcOpenShell."""

    def setUp(self):
        self.ids = load('bim_ids_check', CHECKER)

    def assertCode(self, code, text):
        with self.assertRaises(self.ids.IdsError) as caught:
            self.ids.parse_ids(text)
        self.assertEqual(code, caught.exception.code, str(caught.exception))

    def assertValueCode(self, code, xml):
        with self.assertRaises(self.ids.IdsError) as caught:
            value(xml)
        self.assertEqual(code, caught.exception.code, f'{xml}: {caught.exception}')

    def test_b1_sibling_patterns_are_alternatives(self):
        r = value('<xs:restriction base="xs:string"><xs:pattern value="A.*"/><xs:pattern value="B.*"/></xs:restriction>')
        self.assertTrue(r.matches('Apple'))
        self.assertTrue(r.matches('Banana'))
        self.assertFalse(r.matches('Cherry'))

    def test_b2_specification_attributes_are_whitelisted(self):
        self.ids.parse_ids(spec('', 'name="s" ifcVersion="IFC4" identifier="i" description="d" instructions="x"'))
        self.assertCode('IDS_UNSUPPORTED_ATTRIBUTE', spec('', 'name="s" ifcVersion="IFC4" minOccurs="0" maxOccurs="0"'))
        self.assertCode('IDS_UNSUPPORTED_ATTRIBUTE', spec('', 'name="s" ifcVersion="IFC4" foo="bar"'))

    def test_s2_ifc_version_tokens_and_schema_mapping(self):
        for version in ['IFC5', 'IFC4X3', 'ifc4', 'IFC2X3 IFC9']:
            with self.subTest(version=version):
                self.assertCode('IDS_INVALID', spec('', f'name="s" ifcVersion="{version}"'))
        self.ids.parse_ids(spec('', 'name="s" ifcVersion="IFC2X3 IFC4 IFC4X3_ADD2"'))
        self.assertEqual('IFC4X3_ADD2', self.ids.normalise_schema('IFC4X3'))
        self.assertEqual('IFC4X3_ADD2', self.ids.normalise_schema('IFC4X3_ADD2'))
        self.assertEqual('IFC4', self.ids.normalise_schema('IFC4'))

    def test_s3_xsd_escapes_and_dot_follow_xsd(self):
        s = value('<xs:restriction base="xs:string"><xs:pattern value="a\\sb"/></xs:restriction>')
        self.assertTrue(s.matches('a b'))
        self.assertTrue(s.matches('a\tb'))
        self.assertFalse(s.matches('a\xa0b'))
        dot = value('<xs:restriction base="xs:string"><xs:pattern value="a.b"/></xs:restriction>')
        self.assertFalse(dot.matches('a\rb'))
        self.assertFalse(dot.matches('a\nb'))
        self.assertTrue(dot.matches('a\xa0b'))
        for xml in ['a\\wb', 'a\\Wb', 'a\\$', '[\\S]']:
            with self.subTest(pattern=xml):
                self.assertValueCode('IDS_UNSUPPORTED_RESTRICTION', f'<xs:restriction base="xs:string"><xs:pattern value="{xml}"/></xs:restriction>')

    def test_s4_nested_quantifiers_refused_and_values_capped(self):
        for pattern in ['(a+)+b', '(a*)*', '(ab+)*', '((a)+)+', '(a{2,})+', 'a*?']:
            with self.subTest(pattern=pattern):
                self.assertValueCode('IDS_UNSUPPORTED_RESTRICTION', f'<xs:restriction base="xs:string"><xs:pattern value="{pattern}"/></xs:restriction>')
        r = value('<xs:restriction base="xs:string"><xs:pattern value="(ab)+"/></xs:restriction>')
        self.assertTrue(r.matches('abab'))
        with self.assertRaises(self.ids.IdsError) as caught:
            r.matches('ab' * 10000)
        self.assertEqual('IFC_VALUE_TOO_LONG', caught.exception.code)

    def test_s6_restriction_base_types(self):
        d = value('<xs:restriction base="xs:double"><xs:enumeration value="3.5"/><xs:enumeration value="4"/></xs:restriction>')
        self.assertTrue(d.matches(3.5))
        self.assertTrue(d.matches(4.0))
        self.assertFalse(d.matches('4'))
        s = value('<xs:restriction base="xs:string"><xs:enumeration value="3"/></xs:restriction>')
        self.assertTrue(s.matches('3'))
        self.assertFalse(s.matches(3.0))
        i = value('<xs:restriction base="xs:integer"><xs:enumeration value="3"/></xs:restriction>')
        self.assertTrue(i.matches(3))
        self.assertFalse(i.matches(3.5))
        self.assertTrue(value('<ids:simpleValue>1e3</ids:simpleValue>').matches(1000.0))
        self.assertValueCode('IDS_UNSUPPORTED_RESTRICTION', '<xs:restriction base="xs:boolean"><xs:enumeration value="true"/></xs:restriction>')
        self.assertValueCode('IDS_UNSUPPORTED_RESTRICTION', '<xs:restriction base="xs:double"><xs:pattern value="[0-9]+"/></xs:restriction>')
        self.assertValueCode('IDS_INVALID', '<xs:restriction><xs:enumeration value="A"/></xs:restriction>')
        for literal in ['abc', '1.2.3', 'INF', 'NaN', '']:
            with self.subTest(literal=literal):
                self.assertValueCode('IDS_INVALID', f'<xs:restriction base="xs:double"><xs:enumeration value="{literal}"/></xs:restriction>')
        self.assertValueCode('IDS_INVALID', '<xs:restriction base="xs:integer"><xs:enumeration value="1.5"/></xs:restriction>')

    def test_s7_prohibited_specification_with_requirements(self):
        self.assertCode('IDS_UNSUPPORTED_CARDINALITY', spec(
            requirement('<ids:attribute><ids:name><ids:simpleValue>Name</ids:simpleValue></ids:name></ids:attribute>'),
            applicability=door_applicability(occurs='minOccurs="0" maxOccurs="0"')))

    def test_s8_applicability_requires_entity_facet(self):
        applicability = ('<ids:applicability><ids:attribute><ids:name><ids:simpleValue>Name</ids:simpleValue></ids:name>'
                         '</ids:attribute></ids:applicability>')
        self.assertCode('IDS_UNSUPPORTED_FACET', spec('', applicability=applicability))

    def test_utf8_only(self):
        self.assertCode('IDS_XML_MALFORMED', spec('').replace('<?xml version="1.0"?>', '<?xml version="1.0" encoding="ISO-8859-1"?>'))
        self.assertCode('IDS_XML_MALFORMED', spec('').encode('utf-16'))


def build_doors(path, length_prefix=None):
    """Small IFC4 with typed/classified doors built directly with IfcOpenShell."""
    import ifcopenshell
    import ifcopenshell.guid
    f = ifcopenshell.file(schema='IFC4')
    gid = ifcopenshell.guid.new
    length = f.create_entity('IfcSIUnit', UnitType='LENGTHUNIT', Name='METRE', Prefix=length_prefix)
    f.create_entity('IfcProject', GlobalId=gid(), Name='doors', UnitsInContext=f.create_entity('IfcUnitAssignment', Units=[length]))

    def pset(name, values):
        props = [f.create_entity('IfcPropertySingleValue', Name=k, NominalValue=f.create_entity(kind, v)) for k, (kind, v) in values.items()]
        return f.create_entity('IfcPropertySet', GlobalId=gid(), Name=name, HasProperties=props)

    def attach(element, ps):
        f.create_entity('IfcRelDefinesByProperties', GlobalId=gid(), RelatedObjects=[element], RelatingPropertyDefinition=ps)

    pocket = f.create_entity('IfcDoorType', GlobalId=gid(), Name='pocket', PredefinedType='USERDEFINED', OperationType='SLIDING_TO_LEFT',
                             ElementType='POCKET', HasPropertySets=[pset('Pset_DoorCommon', {'FireRating': ('IfcLabel', 'EI30')})])
    gate = f.create_entity('IfcDoorType', GlobalId=gid(), Name='gate', PredefinedType='GATE', OperationType='SINGLE_SWING_LEFT')
    doors = {}
    doors['d1'] = f.create_entity('IfcDoor', GlobalId=gid(), Name='d1', Description='fire door', PredefinedType='NOTDEFINED')
    doors['d2'] = f.create_entity('IfcDoor', GlobalId=gid(), Name='d2')
    doors['d3'] = f.create_entity('IfcDoor', GlobalId=gid(), Name='d3', PredefinedType='DOOR')
    doors['d4'] = f.create_entity('IfcDoor', GlobalId=gid(), Name='d4', PredefinedType='DOOR')
    f.create_entity('IfcRelDefinesByType', GlobalId=gid(), RelatedObjects=[doors['d1'], doors['d2']], RelatingType=pocket)
    f.create_entity('IfcRelDefinesByType', GlobalId=gid(), RelatedObjects=[doors['d4']], RelatingType=gate)
    attach(doors['d1'], pset('Pset_DoorCommon', {'FireRating': ('IfcLabel', 'EI60')}))
    attach(doors['d3'], pset('Pset_Dimensions', {'Width': ('IfcLengthMeasure', 900.0)}))
    uniclass = f.create_entity('IfcClassification', Name='Uniclass 2015')
    reference = f.create_entity('IfcClassificationReference', Identification='Pr_30_59', ReferencedSource=uniclass)
    f.create_entity('IfcRelAssociatesClassification', GlobalId=gid(), RelatedObjects=[doors['d1']], RelatingClassification=reference)
    f.write(str(path))
    return {k: v.GlobalId for k, v in doors.items()}


class IdsReviewIntegration(unittest.TestCase):
    """Review regressions on real IfcOpenShell models."""

    @classmethod
    def setUpClass(cls):
        cls.ids = load('bim_ids_check', CHECKER)
        cls.worker = load('ifc_worker', ROOT / 'runtimes/ifc/worker.py')
        (ROOT / '.bim/ifc-tests').mkdir(parents=True, exist_ok=True)
        cls.tmp = tempfile.TemporaryDirectory(dir=ROOT / '.bim/ifc-tests')
        cls.dir = pathlib.Path(cls.tmp.name)
        cls.export = cls.dir / 'room.ifc'
        cls.worker.create(cls.export)
        model = cls.worker.ifcopenshell.open(str(cls.export))
        cls.wall_ids = {e.Name: e.GlobalId for e in model.by_type('IfcWall')}
        cls.metre, cls.milli = cls.dir / 'doors-m.ifc', cls.dir / 'doors-mm.ifc'
        cls.doors = build_doors(cls.metre)
        cls.doors_mm = build_doors(cls.milli, 'MILLI')
        cls.counter = 0

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def run_ids(self, text, ifc):
        type(self).counter += 1
        path = self.dir / f'case{self.counter}.ids'
        path.write_text(text, encoding='utf-8')
        report = self.ids.check(path, ifc)
        return report, json.dumps(report, indent=1)

    def failing(self, report, names):
        return sorted(f['global_id'] for f in report['specifications'][0]['failures']), sorted(names)

    def test_b1_prohibited_alternative_patterns_on_exported_walls(self):
        report, detail = self.run_ids(spec(requirement(
            '<ids:attribute cardinality="prohibited"><ids:name><ids:simpleValue>Name</ids:simpleValue></ids:name><ids:value>'
            '<xs:restriction base="xs:string"><xs:pattern value="we.*"/><xs:pattern value="ea.*"/></xs:restriction></ids:value></ids:attribute>')), self.export)
        self.assertEqual('fail', report['status'], detail)
        got, want = self.failing(report, [self.wall_ids['west'], self.wall_ids['east']])
        self.assertEqual(want, got, detail)

    def test_s1_numeric_measures_use_si_units(self):
        width_facet = ('<ids:property><ids:propertySet><ids:simpleValue>Pset_Dimensions</ids:simpleValue></ids:propertySet>'
                       '<ids:baseName><ids:simpleValue>Width</ids:simpleValue></ids:baseName>{}</ids:property>')
        applicability = door_applicability(width_facet.format(''), 'minOccurs="1" maxOccurs="unbounded"')
        width = lambda v: spec(requirement(width_facet.format(f'<ids:value><ids:simpleValue>{v}</ids:simpleValue></ids:value>')),
                               applicability=applicability)
        report, detail = self.run_ids(width('0.9'), self.milli)
        self.assertEqual('pass', report['status'], detail)
        report, detail = self.run_ids(width('900'), self.milli)
        self.assertEqual('fail', report['status'], detail)
        report, detail = self.run_ids(width('900'), self.metre)
        self.assertEqual('pass', report['status'], detail)
        # Attribute measures have no unit-typed wrapper: refuse rather than compare millimetres as metres.
        report, detail = self.run_ids(spec(requirement(
            '<ids:attribute><ids:name><ids:simpleValue>OverallHeight</ids:simpleValue></ids:name><ids:value><ids:simpleValue>2.1</ids:simpleValue></ids:value></ids:attribute>'),
            applicability=door_applicability()), self.export)
        self.assertEqual(('invalid', 'IFC_UNSUPPORTED_UNIT'), (report['status'], (report['error'] or {}).get('code')), detail)

    def test_s5_unwritable_output_is_reported_without_traceback(self):
        missing = self.dir / 'missing' / 'nested' / 'report.json'
        result = subprocess.run([sys.executable, str(CHECKER), str(FIXTURES / 'passing.ids'), str(self.export), '--output', str(missing)],
                                capture_output=True, text=True, timeout=300)
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        report = json.loads(result.stdout)
        self.assertEqual(('invalid', 'OUTPUT_UNWRITABLE'), (report['status'], report['error']['code']), result.stdout)
        self.assertNotIn('Traceback', result.stderr)
        self.assertNotIn(str(ROOT), result.stdout + result.stderr)

    def test_s9_prohibited_attribute_present(self):
        report, detail = self.run_ids(spec(requirement(
            '<ids:attribute cardinality="prohibited"><ids:name><ids:simpleValue>Description</ids:simpleValue></ids:name></ids:attribute>'),
            applicability=door_applicability()), self.metre)
        got, want = self.failing(report, [self.doors['d1']])
        self.assertEqual(want, got, detail)

    def test_s9_optional_property_with_type_inheritance(self):
        report, detail = self.run_ids(spec(requirement(fire_rating('optional', 'EI30')), applicability=door_applicability()), self.metre)
        self.assertEqual(4, report['specifications'][0]['applicable_count'], detail)
        got, want = self.failing(report, [self.doors['d1']])  # d1 overrides EI30 with EI60; d2 inherits EI30
        self.assertEqual(want, got, detail)
        report, detail = self.run_ids(spec(requirement(fire_rating('required', 'EI30')), applicability=door_applicability()), self.metre)
        got, want = self.failing(report, [self.doors['d1'], self.doors['d3'], self.doors['d4']])
        self.assertEqual(want, got, detail)
        report, detail = self.run_ids(spec(requirement(fire_rating('optional', 'EI60')), applicability=door_applicability()), self.metre)
        got, want = self.failing(report, [self.doors['d2']])
        self.assertEqual(want, got, detail)

    def test_s9_classification_value_and_system(self):
        def classification(system, ident):
            return requirement('<ids:classification cardinality="required">'
                               f'<ids:value><ids:simpleValue>{ident}</ids:simpleValue></ids:value>'
                               f'<ids:system><ids:simpleValue>{system}</ids:simpleValue></ids:system></ids:classification>')
        report, detail = self.run_ids(spec(classification('Uniclass 2015', 'Pr_30_59'), applicability=door_applicability()), self.metre)
        got, want = self.failing(report, [self.doors['d2'], self.doors['d3'], self.doors['d4']])
        self.assertEqual(want, got, detail)
        for system, ident in [('Uniclass 2015', 'Pr_99'), ('OmniClass', 'Pr_30_59')]:
            report, detail = self.run_ids(spec(classification(system, ident), applicability=door_applicability()), self.metre)
            got, want = self.failing(report, self.doors.values())
            self.assertEqual(want, got, detail)

    def test_s9_predefined_type_resolution_and_multi_facet_applicability(self):
        def count(applicability):
            report, detail = self.run_ids(spec('', applicability=applicability), self.metre)
            self.assertNotEqual('invalid', report['status'], detail)
            return report['specifications'][0]['applicable_count']
        typed = lambda t: door_applicability().replace('</ids:entity>', f'<ids:predefinedType><ids:simpleValue>{t}</ids:simpleValue></ids:predefinedType></ids:entity>')
        self.assertEqual(2, count(typed('POCKET')))  # USERDEFINED type object -> ElementType
        self.assertEqual(1, count(typed('GATE')))    # type PredefinedType takes precedence over occurrence DOOR
        self.assertEqual(1, count(typed('DOOR')))    # untyped d3 only
        with_rating = door_applicability(fire_rating('required').replace(' cardinality="required"', ''))
        self.assertEqual(2, count(with_rating))
        both = door_applicability(fire_rating('required').replace(' cardinality="required"', '') +
                                  '<ids:attribute><ids:name><ids:simpleValue>Description</ids:simpleValue></ids:name></ids:attribute>')
        self.assertEqual(1, count(both))


if __name__ == '__main__':
    unittest.main()
