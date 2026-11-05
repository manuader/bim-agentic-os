"""Temporary probe of the pinned ifcopenshell.validate statement format (removed before merge)."""
import importlib.util, pathlib, tempfile, unittest, inspect
ROOT = pathlib.Path(__file__).resolve().parents[2]

def emit(title, text):
    text = text.replace('%', '%25').replace('\r', '').replace('\n', '%0A')
    print(f'::warning title={title}::{text[:3500]}', flush=True)

class Probe(unittest.TestCase):
    def test_probe(self):
        spec = importlib.util.spec_from_file_location('ifc_worker_probe', ROOT/'runtimes/ifc/worker.py')
        w = importlib.util.module_from_spec(spec); spec.loader.exec_module(w)
        import ifcopenshell, ifcopenshell.validate as v
        emit('version', ifcopenshell.version + ' ' + str(inspect.signature(v.validate)) + ' ' + str([n for n in dir(v) if not n.startswith('_')]))
        try:
            emit('json_logger', inspect.getsource(v.json_logger))
        except Exception as e:
            emit('json_logger', repr(e))
        (ROOT / '.bim/ifc-tests').mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / '.bim/ifc-tests') as d:
            p = pathlib.Path(d)/'room.ifc'; w.create(p)
            def mut(name, fn):
                f = ifcopenshell.open(str(p))
                try:
                    fn(f)
                except Exception as e:
                    emit(name, 'mutation raised ' + repr(e)); return
                lg = v.json_logger()
                try:
                    v.validate(f, lg, express_rules=True)
                except Exception as e:
                    emit(name, 'validate raised ' + repr(e)); return
                emit(name, str(len(lg.statements)) + ' ' + repr(lg.statements[:4]))
            mut('clean', lambda f: None)
            mut('gid_none', lambda f: setattr(f.by_type('IfcWall')[0], 'GlobalId', None))
            mut('xdim_neg', lambda f: setattr(f.by_type('IfcRectangleProfileDef')[0], 'XDim', -1.0))
            def voids(f):
                o = f.by_type('IfcOpeningElement')[0]; wall = f.by_type('IfcWall')[0]
                f.createIfcRelVoidsElement(ifcopenshell.guid.new(), None, None, None, wall, o)
            mut('inverse', voids)
            mut('dup_gid', lambda f: setattr(f.by_type('IfcDoor')[0], 'GlobalId', f.by_type('IfcWall')[0].GlobalId))
            mut('pt4', lambda f: setattr(f.by_type('IfcCartesianPoint')[0], 'Coordinates', (0.0, 0.0, 0.0, 0.0)))
            mut('wrongtype', lambda f: setattr(f.by_type('IfcRelVoidsElement')[0], 'RelatingBuildingElement', f.by_type('IfcCartesianPoint')[0]))
            mut('extrusion', lambda f: setattr(f.by_type('IfcExtrudedAreaSolid')[0], 'ExtrudedDirection', f.createIfcDirection((1.0, 0.0, 0.0))))
            mut('two_projects', lambda f: f.createIfcProject(ifcopenshell.guid.new()))
            bad = pathlib.Path(d)/'bad.ifc'; bad.write_text('ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\n#1=IFCWALL(;\nENDSEC;\nEND-ISO-10303-21;\n')
            try:
                f = ifcopenshell.open(str(bad)); emit('syntax', 'opened ' + repr(f))
            except Exception as e:
                emit('syntax', 'open raised ' + type(e).__module__ + '.' + type(e).__name__ + ' ' + str(e)[:500])
            lg = v.json_logger()
            try:
                v.validate(str(bad), lg, express_rules=True); emit('syntax_validate', repr(lg.statements[:4]))
            except Exception as e:
                emit('syntax_validate', 'raised ' + repr(e)[:500])
