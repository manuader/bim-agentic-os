"""ADR0007 option A: real IFC crash injection followed by the C# CLI reconcile verb.
Each case records SHA-256 of room.ifc and every Core file before and after the step under test.
"""
import hashlib
import importlib.util
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import types
import unittest
import uuid

ROOT=pathlib.Path(__file__).resolve().parents[2]
WORKER=ROOT/'runtimes/ifc/worker.py'
spec=importlib.util.spec_from_file_location('worker_reconcile',WORKER)
w=importlib.util.module_from_spec(spec);spec.loader.exec_module(w)
PACKETS=ROOT/'.bim/reconcile-packets'


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.exists() else None


class Reconcile(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        dotnet=os.environ.get('BIM_DOTNET') or shutil.which('dotnet')
        if not dotnet:raise RuntimeError('dotnet SDK required for Core to IFC reconcile integration')
        PACKETS.mkdir(parents=True,exist_ok=True)
        names=['compiled-5.json','compiled-6.json','compiled-next.json','compiled-noop.json']
        subprocess.run([dotnet,'run','--project',str(ROOT/'tests/ifc-compiler/Ifc.Compiler.Tests.csproj'),'--configuration','Release','--',*[str(PACKETS/name) for name in names]],check=True,capture_output=True,text=True,timeout=300)
        subprocess.run([dotnet,'build',str(ROOT/'interfaces/cli/Bim.Cli.csproj'),'--configuration','Release'],check=True,capture_output=True,text=True,timeout=300)
        cls.cli=[dotnet,str(ROOT/'interfaces/cli/bin/Release/net8.0/Bim.Cli.dll')]

    def packet(self,name):
        return json.loads((PACKETS/name).read_text(encoding='utf-8-sig'))

    def run_cli(self,*args):
        done=subprocess.run([*self.cli,*map(str,args)],capture_output=True,text=True,timeout=120)
        return done.returncode,json.loads(done.stdout)

    def fixture(self,tmp,packet):
        base=pathlib.Path(tmp)
        ifc=base/'ifc';ifc.mkdir();core=base/'core'
        path=ifc/'room.ifc'
        w.create(path,w.validate_request(packet))
        (base/'before.json').write_text(packet['BeforeJson'],encoding='utf-8')
        code,out=self.run_cli('init',core,base/'before.json')
        self.assertEqual((0,'succeeded'),(code,out['status']),out)
        return base,path,core

    def commit(self,base,core,packet):
        (base/'after.json').write_text(packet['AfterJson'],encoding='utf-8')
        (base/'plan.json').write_text(packet['PlanJson'],encoding='utf-8')
        return self.run_cli('commit-intent',core,base/'after.json',base/'plan.json',packet['OperationId'])

    def receipt(self,base,path):
        done=subprocess.run([sys.executable,str(WORKER),'--receipt',str(path)],capture_output=True,text=True,timeout=120)
        self.assertEqual(0,done.returncode,done.stderr)
        target=base/'receipt.json';target.write_text(done.stdout,encoding='utf-8')
        return target,json.loads(done.stdout)

    def reconcile(self,base,path,core,packet_path):
        target,_=self.receipt(base,path)
        return self.run_cli('reconcile',core,target,packet_path)

    def hashes(self,path,core):
        # Every file in both stores, including the retained packet and any staging artifact.
        return {str(p.relative_to(root.parent)):digest(p) for root in (path.parent,core) for p in sorted(root.rglob('*')) if p.is_file() or p.is_symlink()}

    def test_c3_reconcile_replays_core_then_noop_then_latest_retry(self):
        packet=self.packet('compiled-5.json')
        with tempfile.TemporaryDirectory() as tmp:
            base,path,core=self.fixture(tmp,packet)
            with self.assertRaises(w.InjectedCrash):w.resize_compiled(path,packet,crash_after='after_replace')
            retained=path.with_name('compiled-request.json')
            self.assertEqual(packet,json.loads(retained.read_text(encoding='utf-8')),'packet retained before publication')
            _,state=self.receipt(base,path)
            self.assertEqual(8,state['state']['compiled_revision'])
            ifc_before=digest(path)
            code,out=self.reconcile(base,path,core,retained)
            self.assertEqual((0,'succeeded','replayed'),(code,out['status'],out['code']),out)
            self.assertEqual(ifc_before,digest(path),'reconcile never writes the IFC')
            self.assertEqual(state['state']['compiled_revision'],out['core_revision'])
            self.assertEqual(state['state']['compiled_hash'],out['core_hash'])
            stored=json.loads((core/'project.json').read_text(encoding='utf-8'))
            self.assertEqual(8,stored['Project']['Revision'])
            self.assertEqual(state['state']['receipts'][packet['OperationId']]['plan_after_hash'],stored['Receipts'][packet['OperationId']])
            synced=self.hashes(path,core)
            code,out=self.reconcile(base,path,core,retained)
            self.assertEqual((0,'in_sync'),(code,out['code']),out)
            self.assertEqual(synced,self.hashes(path,core),'second reconcile is byte-identical')
            # Test plan 5: latest retry of the pair after recovery.
            self.assertFalse(w.resize_compiled(path,packet))
            code,out=self.commit(base,core,packet)
            self.assertEqual((0,8),(code,out['project']['Revision']),out)
            self.assertEqual(synced,self.hashes(path,core),'latest retry writes nothing')

    def test_c1_c2_staging_fails_closed_until_verified_cleanup(self):
        packet=self.packet('compiled-5.json')
        for point in ('after_staging_create','after_staging_write'):
            with self.subTest(point=point),tempfile.TemporaryDirectory() as tmp:
                base,path,core=self.fixture(tmp,packet)
                with self.assertRaises(w.InjectedCrash):w.resize_compiled(path,packet,crash_after=point)
                staging=path.with_suffix('.pending.ifc')
                self.assertTrue(staging.exists(),'hard-kill stand-in leaves staging behind')
                before=self.hashes(path,core)
                target,state=self.receipt(base,path)
                self.assertTrue(state['staging_artifact_present'])
                code,out=self.run_cli('reconcile',core,target,PACKETS/'compiled-5.json')
                self.assertEqual((1,'failed','staging_artifact_present'),(code,out['status'],out['code']),out)
                self.assertEqual(before,self.hashes(path,core),'both stores byte-identical')
                with self.assertRaises(ValueError):w.resize_compiled(path,packet)
                self.assertEqual(before,self.hashes(path,core),'retry before cleanup fails closed')
                with self.assertRaises(ValueError):w.clear_staging(path,'0'*64)
                self.assertEqual(before,self.hashes(path,core),'cleanup refuses unobserved bytes')
                w.clear_staging(path,state['staging_hash'])
                self.assertFalse(staging.exists())
                self.assertTrue(w.resize_compiled(path,packet))
                code,out=self.commit(base,core,packet)
                self.assertEqual((0,8),(code,out['project']['Revision']),out)
                code,out=self.reconcile(base,path,core,path.with_name('compiled-request.json'))
                self.assertEqual((0,'in_sync'),(code,out['code']),out)

    def test_historical_packet_after_two_corrections_fails_closed(self):
        first=self.packet('compiled-5.json');second=self.packet('compiled-next.json')
        with tempfile.TemporaryDirectory() as tmp:
            base,path,core=self.fixture(tmp,first)
            self.assertTrue(w.resize_compiled(path,first));self.assertEqual(0,self.commit(base,core,first)[0])
            self.assertTrue(w.resize_compiled(path,second));self.assertEqual(0,self.commit(base,core,second)[0])
            before=self.hashes(path,core)
            code,out=self.reconcile(base,path,core,PACKETS/'compiled-5.json')
            self.assertEqual((1,'retry_superseded'),(code,out['code']),out)
            with self.assertRaises(ValueError):w.resize_compiled(path,first)
            code,out=self.commit(base,core,first)
            self.assertEqual((0,9),(code,out['project']['Revision']),out)
            self.assertEqual(before,self.hashes(path,core),'historical reconcile/replay writes nothing')

    def test_forged_states_return_typed_codes_without_writes(self):
        first=self.packet('compiled-5.json');second=self.packet('compiled-next.json')
        with tempfile.TemporaryDirectory() as tmp:
            base,path,core=self.fixture(tmp,first)
            self.assertEqual(0,self.commit(base,core,first)[0])
            before=self.hashes(path,core)
            code,out=self.reconcile(base,path,core,PACKETS/'compiled-5.json')
            self.assertEqual((1,'core_ahead_of_backend'),(code,out['code']),out)
            self.assertEqual(before,self.hashes(path,core))
        with tempfile.TemporaryDirectory() as tmp:
            base,path,core=self.fixture(tmp,first)
            self.assertTrue(w.resize_compiled(path,first));self.assertTrue(w.resize_compiled(path,second))
            before=self.hashes(path,core)
            code,out=self.reconcile(base,path,core,PACKETS/'compiled-next.json')
            self.assertEqual((1,'backend_gap_gt_1'),(code,out['code']),out)
            self.assertEqual(before,self.hashes(path,core))
        with tempfile.TemporaryDirectory() as tmp:
            base,path,core=self.fixture(tmp,first)
            self.assertTrue(w.resize_compiled(path,first))
            f=w.ifcopenshell.open(str(path));s=w.state(f);s['receipts'][first['OperationId']]['compiled_fingerprint']='0'*64;w.set_state(f,s);f.write(str(path))
            before=self.hashes(path,core)
            code,out=self.reconcile(base,path,core,PACKETS/'compiled-5.json')
            self.assertEqual((1,'receipt_packet_mismatch'),(code,out['code']),out)
            self.assertEqual(before,self.hashes(path,core))

    def test_project_mismatch_without_writes(self):
        packet=self.packet('compiled-5.json')
        with tempfile.TemporaryDirectory() as tmp:
            base,path,core=self.fixture(tmp,packet)
            f=w.ifcopenshell.open(str(path));s=w.state(f);s['compiled_project']=str(uuid.uuid4());w.set_state(f,s);f.write(str(path))
            before=self.hashes(path,core)
            code,out=self.reconcile(base,path,core,PACKETS/'compiled-5.json')
            self.assertEqual((1,'project_mismatch'),(code,out['code']),out)
            self.assertEqual(before,self.hashes(path,core))

    def test_packet_is_never_staged_through_a_link_or_non_regular_file(self):
        packet=self.packet('compiled-5.json')
        with tempfile.TemporaryDirectory() as tmp:
            base=pathlib.Path(tmp);ifc=base/'ifc';ifc.mkdir();path=ifc/'room.ifc'
            w.create(path,w.validate_request(packet))
            outside=base/'outside.json';outside.write_text('outside must survive',encoding='utf-8')
            for name in ('compiled-request.pending.json','compiled-request.json'):
                for kind in ('symlink','directory'):
                    with self.subTest(name=name,kind=kind):
                        link=ifc/name
                        if kind=='symlink':link.symlink_to(outside)
                        else:link.mkdir()
                        original=path.read_bytes()
                        with self.assertRaises(ValueError):w.retain_packet(path,packet)
                        with self.assertRaises(ValueError):w.resize_compiled(path,packet)
                        self.assertEqual(original,path.read_bytes(),'IFC unpublished')
                        self.assertFalse(path.with_suffix('.pending.ifc').exists(),'staging cleaned after refusal')
                        self.assertEqual('outside must survive',outside.read_text(encoding='utf-8'))
                        self.assertTrue(link.is_symlink() if kind=='symlink' else link.is_dir())
                        if kind=='symlink':link.unlink()
                        else:link.rmdir()
            w.retain_packet(path,packet)
            self.assertEqual(packet,json.loads((ifc/'compiled-request.json').read_text(encoding='utf-8')))
            self.assertFalse((ifc/'compiled-request.pending.json').exists())

    def test_cloud_conflict_copy_next_to_ifc_fails_closed_without_writes(self):
        packet=self.packet('compiled-5.json')
        for conflict in ('room-DESKTOP-ABC.ifc','room (1).ifc','compiled-request-LAPTOP-1.json'):
            with self.subTest(conflict=conflict),tempfile.TemporaryDirectory() as tmp:
                base,path,core=self.fixture(tmp,packet)
                with self.assertRaises(w.InjectedCrash):w.resize_compiled(path,packet,crash_after='after_replace')
                (path.parent/conflict).write_bytes(path.read_bytes())
                before=self.hashes(path,core)
                target,state=self.receipt(base,path)
                self.assertEqual(('cloud_conflict_present',[conflict],None),(state['cloud_hazard'],state['cloud_hazard_entries'],state['state']))
                code,out=self.run_cli('reconcile',core,target,path.with_name('compiled-request.json'))
                self.assertEqual((1,'failed','cloud_conflict_present'),(code,out['status'],out['code']),out)
                self.assertEqual(before,self.hashes(path,core),'both stores byte-identical')
                self.assertEqual(7,json.loads((core/'project.json').read_text(encoding='utf-8'))['Project']['Revision'])
                (path.parent/conflict).unlink()
                code,out=self.reconcile(base,path,core,path.with_name('compiled-request.json'))
                self.assertEqual((0,'replayed'),(code,out['code']),out)

    def test_cloud_placeholder_next_to_ifc_fails_closed_without_writes(self):
        packet=self.packet('compiled-5.json')
        with tempfile.TemporaryDirectory() as tmp:
            base,path,core=self.fixture(tmp,packet)
            with self.assertRaises(w.InjectedCrash):w.resize_compiled(path,packet,crash_after='after_replace')
            before=self.hashes(path,core)
            # Simulated Windows attributes: the receipt must report without opening the IFC.
            raw=w.receipt(path,stat_fn=fake_stat({'room.ifc':w.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS}))
            self.assertEqual(('cloud_placeholder_present',['room.ifc'],None),(raw['cloud_hazard'],raw['cloud_hazard_entries'],raw['state']))
            target=base/'receipt.json';target.write_text(json.dumps(raw),encoding='utf-8')
            code,out=self.run_cli('reconcile',core,target,path.with_name('compiled-request.json'))
            self.assertEqual((1,'failed','cloud_placeholder_present'),(code,out['status'],out['code']),out)
            self.assertEqual(before,self.hashes(path,core),'both stores byte-identical')

    def test_crash_injection_point_must_be_known(self):
        packet=self.packet('compiled-5.json')
        with tempfile.TemporaryDirectory() as tmp:
            path=pathlib.Path(tmp)/'room.ifc';w.create(path,w.validate_request(packet));original=path.read_bytes()
            with self.assertRaises(ValueError):w.resize_compiled(path,packet,crash_after='after_everything')
            self.assertEqual(original,path.read_bytes())
            self.assertEqual({'room.ifc'},{p.name for p in pathlib.Path(tmp).iterdir()})


def fake_stat(flags):
    """Injectable stat: real lstat plus simulated Windows st_file_attributes for the named entries."""
    def stat_fn(entry_path):
        real=os.lstat(entry_path)
        return types.SimpleNamespace(st_mode=real.st_mode,st_file_attributes=flags.get(os.path.basename(entry_path),0x20))
    return stat_fn


class CloudHazard(unittest.TestCase):
    """ADR0007 caveat: OneDrive conflict copies and cloud-only placeholders; runs on every OS, no dotnet."""

    def test_conflict_copy_names(self):
        for name,primary in (('room-DESKTOP-ABC.ifc','room.ifc'),('room-LAPTOP-1.ifc','room.ifc'),('room (1).ifc','room.ifc'),('Room-DESKTOP-AB12.IFC','room.ifc'),('ROOM.ifc','room.ifc'),('room - Copy.ifc','room.ifc'),('compiled-request-DESKTOP-ABC.json','compiled-request.json'),('project-DESKTOP-ABC.json','project.json'),('project (1).json','project.json')):
            with self.subTest(name=name):self.assertTrue(w.is_conflict_copy(name,primary))
        for name,primary in (('room.ifc','room.ifc'),('room.pending.ifc','room.ifc'),('before.ifc','room.ifc'),('rooms.ifc','room.ifc'),('room-DESKTOP-ABC.json','room.ifc'),('compiled-request.json','room.ifc'),('compiled-request.pending.json','compiled-request.json'),('hashes.json','compiled-request.json'),('project.tmp','project.json'),('checkpoint-7.json','project.json')):
            with self.subTest(name=name):self.assertFalse(w.is_conflict_copy(name,primary))

    def directory(self,tmp,*names):
        base=pathlib.Path(tmp)
        for name in names:(base/name).write_text(name,encoding='utf-8')
        return base/'room.ifc',{name:digest(base/name) for name in names}

    def test_conflict_copy_hazard_is_reported_first_and_reads_nothing(self):
        with tempfile.TemporaryDirectory() as tmp:
            path,before=self.directory(tmp,'room.ifc','room.pending.ifc','compiled-request.json','room-DESKTOP-ABC.ifc')
            self.assertEqual(('cloud_conflict_present',['room-DESKTOP-ABC.ifc']),w.cloud_hazard(path,stat_fn=fake_stat({'room.ifc':w.FILE_ATTRIBUTE_OFFLINE})))
            self.assertEqual(before,{name:digest(path.parent/name) for name in before})

    def test_placeholder_attributes_are_hazards(self):
        for flag in (w.FILE_ATTRIBUTE_OFFLINE,w.FILE_ATTRIBUTE_RECALL_ON_OPEN,w.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS):
            for target in ('room.ifc','compiled-request.json','room.pending.ifc'):
                with self.subTest(flag=hex(flag),target=target),tempfile.TemporaryDirectory() as tmp:
                    path,_=self.directory(tmp,'room.ifc','room.pending.ifc','compiled-request.json')
                    self.assertEqual(('cloud_placeholder_present',[target]),w.cloud_hazard(path,stat_fn=fake_stat({target:flag|0x400})))
        self.assertEqual((0x1000,0x40000,0x400000),(w.FILE_ATTRIBUTE_OFFLINE,w.FILE_ATTRIBUTE_RECALL_ON_OPEN,w.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS))

    def test_reparse_point_alone_and_real_files_are_not_hazards(self):
        # HANDOFF section 7: OneDrive sets ReparsePoint (0x400) on hydrated real files with no link target.
        with tempfile.TemporaryDirectory() as tmp:
            path,_=self.directory(tmp,'room.ifc','compiled-request.json')
            self.assertEqual((None,[]),w.cloud_hazard(path,stat_fn=fake_stat({'room.ifc':0x400|0x20})))
            self.assertEqual((None,[]),w.cloud_hazard(path),'real temporary files are neither conflict copies nor placeholders')

    def test_receipt_and_clear_staging_never_open_a_hazardous_store(self):
        original=w.ifcopenshell.open
        def refuse(*_):raise AssertionError('hazardous IFC must not be opened or hydrated')
        w.ifcopenshell.open=refuse
        try:
            with tempfile.TemporaryDirectory() as tmp:
                path,before=self.directory(tmp,'room.ifc','room.pending.ifc','room (1).ifc')
                raw=w.receipt(path)
                self.assertEqual(dict(state=None,staging_artifact_present=True,staging_hash=None,cloud_hazard='cloud_conflict_present',cloud_hazard_entries=['room (1).ifc']),raw)
                with self.assertRaisesRegex(ValueError,'cloud_conflict_present'):w.clear_staging(path,hashlib.sha256(b'room.pending.ifc').hexdigest().upper())
                (path.parent/'room (1).ifc').unlink();del before['room (1).ifc']
                stat_fn=fake_stat({'room.pending.ifc':w.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS})
                self.assertEqual('cloud_placeholder_present',w.receipt(path,stat_fn=stat_fn)['cloud_hazard'])
                with self.assertRaisesRegex(ValueError,'cloud_placeholder_present'):w.clear_staging(path,hashlib.sha256(b'room.pending.ifc').hexdigest().upper(),stat_fn=stat_fn)
                self.assertEqual(before,{name:digest(path.parent/name) for name in before},'zero writes')
        finally:
            w.ifcopenshell.open=original


if __name__=='__main__':unittest.main()
