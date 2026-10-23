import importlib.util
import hashlib
import json
import os
import pathlib
import tempfile
import unittest
import shutil
import subprocess
ROOT=pathlib.Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('worker',ROOT/'runtimes/ifc/worker.py')
w=importlib.util.module_from_spec(spec);spec.loader.exec_module(w)
class CompiledIfc(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        dotnet=os.environ.get('BIM_DOTNET') or shutil.which('dotnet')
        if not dotnet:raise RuntimeError('dotnet SDK required for Core to IFC integration')
        (ROOT/'.bim').mkdir(exist_ok=True)
        names=['compiled-5.json','compiled-6.json','compiled-next.json','compiled-noop.json']
        subprocess.run([dotnet,'run','--project',str(ROOT/'tests/ifc-compiler/Ifc.Compiler.Tests.csproj'),'--configuration','Release','--',*[str(ROOT/'.bim'/name) for name in names]],check=True,capture_output=True,text=True,timeout=120)
    def packet(self,name):return json.loads((ROOT/'.bim'/name).read_text(encoding='utf-8-sig'))
    def test_consecutive_compiled_revisions(self):
        packet=self.packet('compiled-5.json');next_packet=self.packet('compiled-next.json')
        with tempfile.TemporaryDirectory() as tmp:
            path=pathlib.Path(tmp)/'room.ifc'
            w.create(path,w.validate_request(packet))
            initial={role:e.GlobalId for role,e in w.entities(w.ifcopenshell.open(str(path))).items()}
            self.assertTrue(w.resize_compiled(path,packet))
            self.assertTrue(w.resize_compiled(path,next_packet))
            f=w.ifcopenshell.open(str(path))
            self.assertEqual(9,w.state(f)['compiled_revision'])
            self.assertEqual(5800,w.bounds(w.entities(f)['room'])[3])
            self.assertEqual(initial,{role:e.GlobalId for role,e in w.entities(f).items()})
            self.assertFalse(w.resize_compiled(path,next_packet))
            original=path.read_bytes()
            with self.assertRaises(ValueError):w.resize_compiled(path,packet)
            self.assertEqual(original,path.read_bytes())
    def test_noop_does_not_write_and_ab_harness_rejects_before_creation(self):
        packet=self.packet('compiled-noop.json')
        with tempfile.TemporaryDirectory() as tmp:
            path=pathlib.Path(tmp)/'room.ifc';w.create(path,w.validate_request(packet));original=path.read_bytes()
            self.assertFalse(w.resize_compiled(path,packet));self.assertEqual(original,path.read_bytes())
            output=pathlib.Path(tmp)/'ab'
            with self.assertRaises(ValueError):w.execute(output,packet)
            self.assertFalse(output.exists())
    def test_update_rejects_corrupt_before_revision(self):
        packet=self.packet('compiled-5.json')
        with tempfile.TemporaryDirectory() as tmp:
            path=pathlib.Path(tmp)/'room.ifc';w.create(path,w.validate_request(packet))
            f=w.ifcopenshell.open(str(path));s=w.state(f);s['revision']='r999';w.set_state(f,s);f.write(str(path));original=path.read_bytes()
            with self.assertRaises(ValueError):w.resize_compiled(path,packet)
            self.assertEqual(original,path.read_bytes())
    def test_retry_rejects_corrupt_persisted_state(self):
        packet=self.packet('compiled-5.json')
        with tempfile.TemporaryDirectory() as tmp:
            path=pathlib.Path(tmp)/'room.ifc';w.create(path,w.validate_request(packet));w.resize_compiled(path,packet)
            f=w.ifcopenshell.open(str(path));s=w.state(f);s['compiled_hash']='CORRUPTED';w.set_state(f,s);f.write(str(path));original=path.read_bytes()
            with self.assertRaises(ValueError):w.resize_compiled(path,packet)
            self.assertEqual(original,path.read_bytes())
    def test_invalid_compiled_fields_rejected_before_writes(self):
        packet=self.packet('compiled-5.json')
        cases=[dict(packet,Units='ft'),dict(packet,BeforeJson=packet['BeforeJson']+' '),dict(packet,PlanJson=packet['PlanJson']+' '),dict(packet,CommittedAfterJson='{}'),dict(packet,LogicalRoles={})]
        for defect in ['unsupported','cycle']:
            bad=dict(packet);project=json.loads(bad['BeforeJson'])
            if defect=='unsupported':project['Elements'][1]['Kind']='roof'
            else:project['Elements'][0]['Dependencies']=[project['Elements'][0]['Id']]
            bad['BeforeJson']=json.dumps(project,separators=(',',':'))
            plan=json.loads(bad['PlanJson']);plan['BeforeHash']=hashlib.sha256(bad['BeforeJson'].encode()).hexdigest().upper()
            bad['PlanJson']=json.dumps(plan,separators=(',',':'));bad['PlanSha256']=hashlib.sha256(bad['PlanJson'].encode()).hexdigest().upper()
            cases.append(bad)
        with tempfile.TemporaryDirectory() as tmp:
            for i,bad in enumerate(cases):
                with self.subTest(defect=i):
                    target=pathlib.Path(tmp)/str(i)
                    with self.assertRaises(ValueError):w.execute(target,bad)
                    self.assertFalse(target.exists())
    def test_missing_and_tampered_request_rejected_before_writes(self):
        with tempfile.TemporaryDirectory() as tmp:
            path=pathlib.Path(tmp)/'untouched'
            with self.assertRaises(ValueError):w.execute(path,{})
            self.assertFalse(path.exists())
    def test_actual_core_packets(self):
        for name,width in [('compiled-5.json',5400),('compiled-6.json',6400)]:
            packet=json.loads((ROOT/'.bim'/name).read_text(encoding='utf-8-sig'))
            with tempfile.TemporaryDirectory() as tmp:
                target=pathlib.Path(tmp)/'output'
                result=w.execute(target,packet)
                self.assertFalse(result['errors'],result)
                self.assertEqual(width,result['observations']['test_b']['after']['clear_width_mm'])
                persisted=w.ifcopenshell.open(str(target/'room.ifc'))
                self.assertEqual(8,w.state(persisted)['compiled_revision'])
                bindings=result['compiled_bindings']
                self.assertEqual(6,len(bindings))
                self.assertTrue(all(w.pset(w.entities(persisted)[role],'BimCompiledIdentity')['LogicalId']==logical for role,logical in packet['LogicalRoles'].items() if role in w.entities(persisted)))
                before=(target/'room.ifc').read_bytes()
                bad=dict(packet);bad['PlanJson']='{}'
                with self.assertRaises(ValueError):w.resize_compiled(target/'room.ifc',bad)
                self.assertEqual(before,(target/'room.ifc').read_bytes())
                stale=dict(packet);stale['OperationId']='stale'
                with self.assertRaises(ValueError):w.resize_compiled(target/'room.ifc',stale)
                self.assertEqual(before,(target/'room.ifc').read_bytes())
                self.assertFalse(w.resize_compiled(target/'room.ifc',packet))
                conflict=self.packet('compiled-6.json' if width==5400 else 'compiled-5.json');conflict['OperationId']=packet['OperationId']
                with self.assertRaises(ValueError):w.resize_compiled(target/'room.ifc',conflict)
                self.assertEqual(before,(target/'room.ifc').read_bytes())
if __name__=='__main__':unittest.main()
