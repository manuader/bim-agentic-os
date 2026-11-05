"""P8-08: every IFC our exporters write passes ifcopenshell.validate (schema + EXPRESS rules) with zero issues,
and a deliberately broken file is reported with typed issues in a deterministic JSON report (bim.ifc-validate/1)."""
import hashlib
import importlib.util
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[2]
MODULE = ROOT / 'runtimes/ifc/ifc_validate.py'
_LOADED = {}

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import test_import as p801  # noqa: E402  (P8-01 source project fixture with openings and spaces)


def load(name, path):
    if name not in _LOADED:
        if not path.exists():
            raise AssertionError(f'{path.relative_to(ROOT)} missing: IFC schema validation (P8-08) not implemented')
        spec = importlib.util.spec_from_file_location(name, path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        _LOADED[name] = module
    return _LOADED[name]


_PACKETS = []


def packets():
    """Real Core compiled packets (C# RoomCompiler/Compiler.Diff), generated once per test process."""
    if not _PACKETS:
        dotnet = os.environ.get('BIM_DOTNET') or shutil.which('dotnet')
        if not dotnet:
            raise RuntimeError('dotnet SDK required for the Core to IFC compiled path')
        target = ROOT / '.bim/ifc-validate-packets'
        target.mkdir(parents=True, exist_ok=True)
        names = ['compiled-5.json', 'compiled-6.json', 'compiled-next.json', 'compiled-noop.json']
        subprocess.run([dotnet, 'run', '--project', str(ROOT / 'tests/ifc-compiler/Ifc.Compiler.Tests.csproj'), '--configuration', 'Release', '--',
                        *[str(target / n) for n in names]], check=True, capture_output=True, text=True, timeout=300)
        _PACKETS.append({n: json.loads((target / n).read_text(encoding='utf-8-sig')) for n in names})
    return _PACKETS[0]


def strings(value):
    """Every decoded string of a report (keys and values), so escaping cannot hide a path."""
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for k, v in value.items():
            yield from strings(k)
            yield from strings(v)
    elif isinstance(value, (list, tuple)):
        for v in value:
            yield from strings(v)


def spellings(*paths):
    out = set()
    for path in paths:
        for text in {str(path), str(pathlib.Path(path).resolve()), os.path.abspath(str(path))}:
            out |= {text, text.replace('\\', '/'), text.replace('/', '\\')}
    return {t for t in out if len(t) > 3}


def annotate(title, text):
    """Make a CI failure readable from the job annotations (logs cannot be downloaded)."""
    text = str(text).replace('%', '%25').replace('\r', '').replace('\n', '%0A')
    print(f'::error title=P8-08 {title}::{text[:3000]}', flush=True)


class Scratch(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.v = load('bim_ifc_validate', MODULE)
        cls.worker = load('ifc_worker_p808', ROOT / 'runtimes/ifc/worker.py')
        cls.ifc = load('bim_ifc_import_p808', ROOT / 'runtimes/ifc/ifc_import.py')
        (ROOT / '.bim/ifc-tests').mkdir(parents=True, exist_ok=True)

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory(dir=ROOT / '.bim/ifc-tests')
        self.dir = pathlib.Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def assertNoLocalPath(self, report, *paths):
        forbidden = spellings(self.dir, tempfile.gettempdir(), *paths)
        for text in strings(report):
            for path in forbidden:
                self.assertNotIn(path, text, f'local path leaked into the report: {text[:300]!r}')

    def assertValid(self, path, label):
        report = self.v.validate_file(path)
        if report['status'] != 'valid' or report['issues']:
            annotate(label, json.dumps(report, indent=1))
        self.assertEqual('valid', report['status'], f'{label}: {json.dumps(report)[:3000]}')
        self.assertEqual([], report['issues'], label)
        self.assertEqual(0, report['counts']['issues'])
        self.assertEqual('IFC4', report['ifc_schema'])
        self.assertTrue(report['validator']['express_rules'])
        self.assertEqual(hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest(), report['file']['sha256'])
        return report


class ExportedFilesAreValid(Scratch):
    """Every IFC file our exporters write in CI: the worker fixture (door, two windows, three openings, a space),
    its revisions, the A/B artifacts, the compiled-request path, and the P8-01 BIM-IR exporter."""

    def test_worker_fixture_and_revisions(self):
        path = self.dir / 'room.ifc'
        self.worker.create(path)
        f = self.worker.ifcopenshell.open(str(path))
        self.assertEqual(3, len(f.by_type('IfcOpeningElement')))
        self.assertEqual(1, len(f.by_type('IfcSpace')))
        self.assertValid(path, 'worker.create')
        self.assertTrue(self.worker.resize(path, 5400, 'resize-400'))
        self.assertValid(path, 'worker.resize 5400')
        self.assertTrue(self.worker.resize(path, 6000, 'resize-1000'))
        self.assertValid(path, 'worker.resize 6000')

    def test_ab_evaluation_artifacts(self):
        output = self.dir / 'ab'
        result = self.worker.execute(output.resolve())
        self.assertFalse(result['errors'], result['errors'])
        for name in ('before.ifc', 'room.ifc'):
            self.assertValid(output / name, f'worker.execute {name}')

    def test_compiled_request_path(self):
        p = packets()
        for name in ('compiled-5.json', 'compiled-6.json'):
            packet = p[name]
            path = self.dir / ('room-' + name.replace('.json', '.ifc'))
            self.worker.create(path, self.worker.validate_request(packet))
            self.assertValid(path, f'worker.create compiled {name}')
            self.assertTrue(self.worker.resize_compiled(path, packet))
            self.assertValid(path, f'worker.resize_compiled {name}')
        # Chained revisions: compiled-5 then compiled-next on the same saved IFC.
        chained = self.dir / 'room-chained.ifc'
        self.worker.create(chained, self.worker.validate_request(p['compiled-5.json']))
        self.assertTrue(self.worker.resize_compiled(chained, p['compiled-5.json']))
        self.assertTrue(self.worker.resize_compiled(chained, p['compiled-next.json']))
        self.assertEqual(9, self.worker.state(self.worker.ifcopenshell.open(str(chained)))['compiled_revision'])
        self.assertValid(chained, 'worker.resize_compiled chained compiled-5 then compiled-next')
        output = self.dir / 'ab-compiled'
        result = self.worker.execute(output.resolve(), p['compiled-5.json'])
        self.assertFalse(result['errors'], result['errors'])
        for name in ('before.ifc', 'room.ifc'):
            self.assertValid(output / name, f'worker.execute compiled {name}')

    def test_crash_and_recovery_outputs(self):
        packet = packets()['compiled-5.json']
        # Crash after the staging write: the left staging file and the untouched published file, then recovery.
        first = self.dir / 'staging'
        first.mkdir()
        path = first / 'room.ifc'
        self.worker.create(path, self.worker.validate_request(packet))
        with self.assertRaises(self.worker.InjectedCrash):
            self.worker.resize_compiled(path, packet, crash_after='after_staging_write')
        staging = path.with_suffix('.pending.ifc')
        self.assertTrue(staging.exists())
        self.assertValid(staging, 'crash after_staging_write: staging file')
        self.assertValid(path, 'crash after_staging_write: published file')
        self.worker.clear_staging(path, self.worker.receipt(path)['staging_hash'])
        self.assertTrue(self.worker.resize_compiled(path, packet))
        self.assertValid(path, 'recovered resize_compiled after staging crash')
        # Crash after the atomic replace: the published revision, then the latest-operation retry.
        second = self.dir / 'replace'
        second.mkdir()
        path = second / 'room.ifc'
        self.worker.create(path, self.worker.validate_request(packet))
        with self.assertRaises(self.worker.InjectedCrash):
            self.worker.resize_compiled(path, packet, crash_after='after_replace')
        self.assertValid(path, 'crash after_replace: published file')
        self.assertFalse(self.worker.resize_compiled(path, packet))
        self.assertValid(path, 'retry after replace crash')

    def test_non_ascii_directory(self):
        source = self.dir / 'model.ifc'
        self.ifc.export_project(p801.source_project(), source)
        folder = self.dir / 'tmp-\u00f1-\u65e5\u672c'
        folder.mkdir()
        path = folder / 'model.ifc'
        shutil.copyfile(source, path)
        report = self.assertValid(path, 'non-ASCII directory')
        self.assertNoLocalPath(report, folder)
        run = subprocess.run([sys.executable, str(MODULE), str(path)], capture_output=True, timeout=300)
        self.assertEqual(0, run.returncode, run.stdout + run.stderr)
        self.assertEqual('valid', json.loads(run.stdout.decode('utf-8'))['status'])

    def test_bim_ir_exporter_with_openings_and_spaces(self):
        source = p801.source_project()
        first = self.dir / 'first.ifc'
        self.ifc.export_project(source, first)
        f = self.worker.ifcopenshell.open(str(first))
        self.assertEqual(4, len(f.by_type('IfcOpeningElement')))
        self.assertEqual(2, len(f.by_type('IfcSpace')))
        self.assertValid(first, 'export_project source')
        second = self.dir / 'second.ifc'
        self.ifc.export_project(self.ifc.import_ifc(first)['project'], second)
        self.assertValid(second, 'export_project reimport')
        # The worker's millimetre fixture imported to BIM-IR and re-exported by the P8-01 exporter.
        room = self.dir / 'room.ifc'
        self.worker.create(room)
        third = self.dir / 'third.ifc'
        self.ifc.export_project(self.ifc.import_ifc(room)['project'], third)
        self.assertValid(third, 'export_project of worker import')

    def test_cli_on_exported_file(self):
        path = self.dir / 'model.ifc'
        self.ifc.export_project(p801.source_project(), path)
        run = subprocess.run([sys.executable, str(MODULE), str(path)], capture_output=True, text=True, timeout=300)
        self.assertEqual(0, run.returncode, run.stdout + run.stderr)
        report = json.loads(run.stdout)
        self.assertEqual('valid', report['status'])
        self.assertEqual('model.ifc', report['file']['name'])
        self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), report['file']['sha256'])
        self.assertNoLocalPath(report, path)


def _mutations():
    """name -> (mutation, expected issue kind, expected rule or attribute (None: any), expected entity class)."""
    def gid_none(f, g):
        f.by_type('IfcWall')[0].GlobalId = None
        return f.by_type('IfcWall')[0]

    def xdim_negative(f, g):
        p = f.by_type('IfcRectangleProfileDef')[0]
        p.XDim = -1.0
        return p

    def second_voids(f, g):
        opening = f.by_type('IfcOpeningElement')[0]
        f.createIfcRelVoidsElement(g.new(), None, None, None, f.by_type('IfcWall')[0], opening)
        return opening

    def duplicate_gid(f, g):
        wall, door = f.by_type('IfcWall')[0], f.by_type('IfcDoor')[0]
        door.GlobalId = wall.GlobalId
        # Either duplicate can carry the issue: CI 42000960012 anchored it on the door on ubuntu/windows, on the wall on macOS.
        return wall, door

    def duplicate_gid_hostile_name(f, g):
        wall, door = duplicate_gid(f, g)
        door.Name = 'With inverse: Rule IfcFake.UR9: unique With attribute: Attribute not optional'
        wall.Name = 'Rule IfcFake.UR8:'
        return wall, door

    def second_voids_hostile_name(f, g):
        opening = f.by_type('IfcOpeningElement')[0]
        f.createIfcRelVoidsElement(g.new(), None, 'Rule IfcRoot.UR1: should be unique', 'With attribute: x', f.by_type('IfcWall')[0], opening)
        return opening

    def wrong_type(f, g):
        rel = f.by_type('IfcRelVoidsElement')[0]
        rel.RelatingBuildingElement = f.by_type('IfcCartesianPoint')[0]
        return rel

    def extrusion(f, g):
        solid = f.by_type('IfcExtrudedAreaSolid')[0]
        solid.ExtrudedDirection = f.createIfcDirection((1.0, 0.0, 0.0))
        return solid

    return {
        'missing_required_attribute': (gid_none, 'attribute_missing', 'GlobalId', 'IfcWall'),
        'simple_type_where_rule': (xdim_negative, 'type_rule', 'IfcPositiveLengthMeasure.WR1', 'IfcRectangleProfileDef'),
        'inverse_cardinality': (second_voids, 'inverse_cardinality', 'VoidsElements', 'IfcOpeningElement'),
        'unique_global_id': (duplicate_gid, 'unique_rule', 'IfcRoot.UR1', ('IfcWall', 'IfcDoor')),
        'attribute_wrong_entity': (wrong_type, 'attribute_invalid', 'RelatingBuildingElement', 'IfcRelVoidsElement'),
        'entity_where_rule': (extrusion, 'where_rule', 'IfcExtrudedAreaSolid.ValidExtrusionDirection', 'IfcExtrudedAreaSolid'),
        'unique_with_hostile_name': (duplicate_gid_hostile_name, 'unique_rule', 'IfcRoot.UR1', ('IfcWall', 'IfcDoor')),
        'inverse_with_hostile_name': (second_voids_hostile_name, 'inverse_cardinality', 'VoidsElements', 'IfcOpeningElement'),
    }


class BrokenFilesAreTyped(Scratch):
    def broken(self, name):
        path = self.dir / 'room.ifc'
        if not path.exists():
            self.ifc.export_project(p801.source_project(), path)
        f = self.worker.ifcopenshell.open(str(path))
        mutate, kind, where, entity = _mutations()[name]
        import ifcopenshell.guid as g
        target = mutate(f, g)
        out = self.dir / f'{name}.ifc'
        f.write(str(out))
        targets = target if isinstance(target, tuple) else (target,)
        entities = entity if isinstance(entity, tuple) else (entity,)
        return out, kind, where, entities, {t.id() for t in targets}

    def test_each_defect_is_reported_with_its_type(self):
        for name in _mutations():
            with self.subTest(defect=name):
                out, kind, where, entities, step_ids = self.broken(name)
                report = self.v.validate_file(out)
                matching = [i for i in report['issues'] if i['kind'] == kind and i['entity'] in entities and i['step_id'] in step_ids
                            and where in ((i.get('rule') or '') + ' ' + (i.get('attribute') or ''))
                            and all(f'#{n}=' in i['message'] for n in step_ids if len(step_ids) > 1)]
                if report['status'] != 'invalid' or not matching:
                    annotate(name, json.dumps(report, indent=1))
                self.assertEqual('invalid', report['status'], name)
                self.assertTrue(matching, f'{name}: expected a {kind} issue on {sorted(step_ids)} {entities} ({where}); got {json.dumps(report["issues"])[:2500]}')
                self.assertGreaterEqual(report['counts']['errors'], 1)
                self.assertNotIn('other', report['counts']['by_kind'], json.dumps(report['issues'])[:2500])
                if name == 'unique_with_hostile_name':
                    self.assertEqual({'IfcRoot.UR1'}, {i['rule'] for i in report['issues'] if i['kind'] == 'unique_rule'})
                    self.assertNotIn('inverse_cardinality', report['counts']['by_kind'])
                if name == 'inverse_with_hostile_name':
                    self.assertNotIn('unique_rule', report['counts']['by_kind'])
                self.assertEqual(len(report['issues']), report['counts']['issues'])
                self.assertEqual(sum(report['counts']['by_kind'].values()), report['counts']['issues'])
                for issue in report['issues']:
                    self.assertEqual({'kind', 'level', 'validator_type', 'rule', 'attribute', 'entity', 'step_id', 'global_id', 'message'}, set(issue), issue)
                    self.assertIn(issue['kind'], self.v.ISSUE_KINDS)
                    self.assertIn(issue['level'], ('error', 'warning'))
                json.dumps(report, allow_nan=False)
                if set(entities) & {'IfcWall', 'IfcOpeningElement', 'IfcRelVoidsElement'} and name != 'missing_required_attribute':
                    self.assertTrue(all(i['global_id'] for i in matching), matching)

    def test_parser_level_defects_are_not_silently_dropped(self):
        # An opened model drops instances the parser cannot build; the report must still show them.
        path = self.dir / 'room.ifc'
        self.ifc.export_project(p801.source_project(), path)
        text = path.read_text(encoding='utf-8')
        tail = text.rindex('ENDSEC;')
        cases = {
            'unknown_entity': ('#999999=IFCNOTATHING(1.);\n', 999999, 'IFCNOTATHING'),
            'dangling_reference': ('#999998=IFCAXIS2PLACEMENT3D(#999997,$,$);\n', 999998, '#999997'),
            'wrong_attribute_count': ('#999996=IFCCARTESIANPOINT((0.,0.,0.),5.);\n', 999996, 'attribute values'),
        }
        for name, (line, step_id, needle) in cases.items():
            with self.subTest(defect=name):
                out = self.dir / f'{name}.ifc'
                out.write_text(text[:tail] + line + text[tail:], encoding='utf-8')
                report = self.v.validate_file(out)
                matching = [i for i in report['issues'] if i['kind'] == 'syntax' and i['step_id'] == step_id and needle in i['message']]
                if report['status'] != 'invalid' or not matching:
                    annotate(name, json.dumps(report, indent=1))
                self.assertEqual('invalid', report['status'], name)
                self.assertTrue(matching, f'{name}: {json.dumps(report["issues"])[:2500]}')
                self.assertNotIn('other', report['counts']['by_kind'], json.dumps(report['issues'])[:2500])
                if name == 'unknown_entity':
                    self.assertEqual(1, report['counts']['issues'], json.dumps(report['issues'])[:2500])
                self.assertNoLocalPath(report, out)
        unsupported = self.dir / 'unsupported_schema.ifc'
        unsupported.write_text(text.replace("FILE_SCHEMA(('IFC4'))", "FILE_SCHEMA(('IFC9'))"), encoding='utf-8')
        report = self.v.validate_file(unsupported)
        if report['status'] != 'invalid' or 'header' not in report['counts'].get('by_kind', {}):
            annotate('unsupported_schema', json.dumps(report, indent=1))
        self.assertEqual('invalid', report['status'])
        self.assertIn('header', report['counts']['by_kind'])
        self.assertNotIn('other', report['counts']['by_kind'])

    def test_second_project_violates_a_global_rule(self):
        path = self.dir / 'room.ifc'
        self.ifc.export_project(p801.source_project(), path)
        f = self.worker.ifcopenshell.open(str(path))
        import ifcopenshell.guid as g
        f.createIfcProject(g.new(), None, 'second project')
        out = self.dir / 'two_projects.ifc'
        f.write(str(out))
        report = self.v.validate_file(out)
        matching = [i for i in report['issues'] if i['kind'] == 'global_rule' and 'IfcSingleProjectInstance' in (i['rule'] or '')]
        if not matching or 'other' in report['counts']['by_kind']:
            annotate('global_rule', json.dumps(report, indent=1))
        self.assertEqual('invalid', report['status'])
        self.assertTrue(matching, json.dumps(report['issues'])[:2500])
        self.assertNotIn('other', report['counts']['by_kind'])

    def test_size_cap_and_validated_content_hash(self):
        path = self.dir / 'model.ifc'
        self.ifc.export_project(p801.source_project(), path)
        size = path.stat().st_size
        with mock.patch.object(self.v, 'MAX_BYTES', size - 1):
            report = self.v.validate_file(path)
        self.assertEqual(('error', 'ifc_too_large'), (report['status'], report['error']['code']), report)
        with mock.patch.object(self.v, 'MAX_BYTES', size):
            self.assertEqual('valid', self.v.validate_file(path)['status'])
        # The hash is of the very bytes the validator read (its private copy), and the copy is removed afterwards.
        real = self.v.ifcopenshell.validate.validate
        seen = []

        def spy(filename, logger, express_rules=False):
            seen.append((filename, hashlib.sha256(pathlib.Path(filename).read_bytes()).hexdigest()))
            return real(filename, logger, express_rules=express_rules)
        with mock.patch.object(self.v.ifcopenshell.validate, 'validate', spy):
            report = self.v.validate_file(path)
        self.assertEqual(1, len(seen))
        self.assertNotEqual(os.path.abspath(str(path)), os.path.abspath(seen[0][0]), 'validates a private copy')
        self.assertEqual(seen[0][1], report['file']['sha256'])
        self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), report['file']['sha256'])
        self.assertFalse(pathlib.Path(seen[0][0]).exists(), 'private copy removed')

    def test_paths_in_validator_output_are_scrubbed(self):
        folder = self.dir / 'sub dir'
        folder.mkdir()
        path = folder / 'model.ifc'
        self.ifc.export_project(p801.source_project(), path)
        original = os.path.abspath(str(path))

        def noisy(filename, logger, express_rules=False):
            logger.statements.append({'level': 'error', 'type': 'schema', 'attribute': None, 'instance': None,
                                      'message': f'cannot read {filename} in {os.path.dirname(filename)}; source {original} '
                                                 f'alias {original.replace(chr(92), "/")} or {original.replace("/", chr(92))} under {folder}'})
        with mock.patch.object(self.v.ifcopenshell.validate, 'validate', noisy):
            report = self.v.validate_file(path)
        self.assertEqual(1, report['counts']['issues'])
        self.assertIn('model.ifc', report['issues'][0]['message'])
        self.assertNoLocalPath(report, path, folder)

        def crashing(filename, logger, express_rules=False):
            raise RuntimeError(f'validator crashed on {filename} from {original}')
        with mock.patch.object(self.v.ifcopenshell.validate, 'validate', crashing):
            report = self.v.validate_file(path)
        self.assertEqual(('error', 'validator_failed'), (report['status'], report['error']['code']), 'an environment failure is not an invalid model')
        self.assertNoLocalPath(report, path, folder)

        def env_failure(filename, logger, express_rules=False):
            raise OSError(f'Unable to open file for reading: {filename}')
        with mock.patch.object(self.v.ifcopenshell.validate, 'validate', env_failure):
            report = self.v.validate_file(path)
        self.assertEqual(('error', 'validator_failed'), (report['status'], report['error']['code']))
        self.assertNoLocalPath(report, path, folder)

    def test_empty_file_is_a_syntax_issue(self):
        path = self.dir / 'empty.ifc'
        path.write_bytes(b'')
        report = self.v.validate_file(path)
        self.assertEqual('invalid', report['status'], json.dumps(report))
        self.assertEqual(['syntax'], [i['kind'] for i in report['issues']])

    def test_unparseable_file_is_a_syntax_issue(self):
        path = self.dir / 'garbage.ifc'
        path.write_text('this is not a STEP physical file\n', encoding='utf-8')
        report = self.v.validate_file(path)
        if report['status'] != 'invalid':
            annotate('syntax', json.dumps(report, indent=1))
        self.assertEqual('invalid', report['status'])
        self.assertEqual(['syntax'], [i['kind'] for i in report['issues']])
        self.assertIsNone(report['ifc_schema'])
        self.assertNoLocalPath(report, path)

    def test_cli_reports_broken_file_deterministically_and_never_overwrites(self):
        out, kind, *_ = self.broken('entity_where_rule')
        first = subprocess.run([sys.executable, str(MODULE), str(out)], capture_output=True, text=True, timeout=300)
        second = subprocess.run([sys.executable, str(MODULE), str(out)], capture_output=True, text=True, timeout=300)
        self.assertEqual(1, first.returncode, first.stdout + first.stderr)
        self.assertEqual(first.stdout, second.stdout, 'report must be deterministic')
        report = json.loads(first.stdout)
        self.assertEqual('bim.ifc-validate/1', report['format'])
        self.assertEqual('invalid', report['status'])
        self.assertIn(kind, report['counts']['by_kind'])
        self.assertNoLocalPath(report, out)
        target = self.dir / 'report.json'
        run = subprocess.run([sys.executable, str(MODULE), str(out), '--output', str(target)], capture_output=True, text=True, timeout=300)
        self.assertEqual(1, run.returncode, run.stderr)
        self.assertEqual(report, json.loads(target.read_text(encoding='utf-8')))
        before = target.read_bytes()
        again = subprocess.run([sys.executable, str(MODULE), str(out), '--output', str(target)], capture_output=True, text=True, timeout=300)
        self.assertEqual(2, again.returncode)
        self.assertEqual('output_exists', json.loads(again.stdout)['error']['code'])
        self.assertEqual(before, target.read_bytes())
        self.assertEqual([], [p.name for p in self.dir.iterdir() if p.name.endswith('.pending')], 'no pending report left behind')

    def test_cli_missing_input_and_usage(self):
        missing = subprocess.run([sys.executable, str(MODULE), str(self.dir / 'absent.ifc')], capture_output=True, text=True, timeout=120)
        self.assertEqual(2, missing.returncode)
        report = json.loads(missing.stdout)
        self.assertEqual('error', report['status'])
        self.assertEqual('ifc_unreadable', report['error']['code'])
        self.assertNoLocalPath(report)
        usage = subprocess.run([sys.executable, str(MODULE)], capture_output=True, text=True, timeout=120)
        self.assertEqual(2, usage.returncode)
        self.assertEqual(('error', 'usage'), (json.loads(usage.stdout)['status'], json.loads(usage.stdout)['error']['code']))


if __name__ == '__main__':
    unittest.main()
