"""Temporary probe of the pinned ifcopenshell parser on broken STEP (removed before merge)."""
import pathlib, tempfile, unittest, logging, io
ROOT = pathlib.Path(__file__).resolve().parents[2]

def emit(title, text):
    text = text.replace('%', '%25').replace('\r', '').replace('\n', '%0A')
    print(f'::warning title={title}::{text[:3000]}', flush=True)

HEAD = "ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('ViewDefinition [CoordinationView]'),'2;1');\nFILE_NAME('a.ifc','2026-01-01T00:00:00',(''),(''),'','','');\nFILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n"
CASES = {
 'trunc': HEAD + "#1=IFCCARTESIANPOINT((0.,0.,0.));\n#2=IFCWALL(;\nENDSEC;\nEND-ISO-10303-21;\n",
 'garbage': 'this is not a STEP file\n',
 'noschema': "ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;\n",
 'badschema': HEAD.replace("'IFC4'", "'IFC9'") + "ENDSEC;\nEND-ISO-10303-21;\n",
 'unknown_entity': HEAD + "#1=IFCNOTATHING(1.);\nENDSEC;\nEND-ISO-10303-21;\n",
 'dangling': HEAD + "#1=IFCAXIS2PLACEMENT3D(#99,$,$);\nENDSEC;\nEND-ISO-10303-21;\n",
 'wrong_arity': HEAD + "#1=IFCCARTESIANPOINT((0.,0.,0.),5);\nENDSEC;\nEND-ISO-10303-21;\n",
 'empty': '',
}
class Probe(unittest.TestCase):
    def test_probe(self):
        import ifcopenshell, ifcopenshell.validate as v
        (ROOT / '.bim/ifc-tests').mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / '.bim/ifc-tests') as d:
            for name, text in CASES.items():
                p = pathlib.Path(d)/(name + '.ifc'); p.write_text(text)
                out = []
                try:
                    f = ifcopenshell.open(str(p)); out.append('open ok n=%d schema=%s' % (len(list(f)), f.schema))
                    lg = v.json_logger()
                    try:
                        v.validate(f, lg, express_rules=True); out.append('validate(f): ' + repr(lg.statements[:3])[:900])
                    except Exception as e:
                        out.append('validate(f) raised ' + type(e).__module__ + '.' + type(e).__name__ + ': ' + str(e)[:300])
                except Exception as e:
                    out.append('open raised ' + type(e).__module__ + '.' + type(e).__name__ + ': ' + str(e)[:300])
                lg = v.json_logger()
                try:
                    v.validate(str(p), lg, express_rules=True); out.append('validate(path): ' + repr(lg.statements[:3])[:900])
                except BaseException as e:
                    out.append('validate(path) raised ' + type(e).__module__ + '.' + type(e).__name__ + ': ' + str(e)[:300])
                emit(name, '\n'.join(out))
