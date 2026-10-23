"""Isolated IFC4 room authoring and independently reopened geometric evidence."""
import argparse
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import stat
import sys
import time
import uuid
import ifcopenshell
import ifcopenshell.geom
import ifcopenshell.util.element
import ifcopenshell.validate

SOURCE = 'actual isolated IFC authoring and reopened readback'
CHANGED = ['east', 'north', 'south', 'window-east', 'room']
_spec=importlib.util.spec_from_file_location('bim_ifc_compiled_request',Path(__file__).with_name('compiled_request.py'))
_compiled=importlib.util.module_from_spec(_spec);_spec.loader.exec_module(_compiled)
validate_request=_compiled.validate_request
CRASH_POINTS=(None,'after_staging_create','after_staging_write','after_replace')


class InjectedCrash(BaseException):
    """Deterministic hard-kill stand-in for ADR0007 tests: no cleanup runs after the named step."""


def root(f, kind, name, **kw):
    return f.create_entity(kind, GlobalId=ifcopenshell.guid.compress(uuid.uuid4().hex), Name=name, **kw)


def point(f, xyz):
    return f.create_entity('IfcCartesianPoint', Coordinates=tuple(float(v) for v in xyz))


def axis(f, xyz=(0,0,0)):
    return f.create_entity('IfcAxis2Placement3D', Location=point(f,xyz))


def placement(f, xyz):
    return f.create_entity('IfcLocalPlacement', RelativePlacement=axis(f,xyz))


def props(f, entity, name, values):
    p = [f.create_entity('IfcPropertySingleValue', Name=k, NominalValue=f.create_entity('IfcText', str(v))) for k,v in values.items()]
    ps = root(f,'IfcPropertySet',name,HasProperties=p)
    root(f,'IfcRelDefinesByProperties',name,RelatedObjects=[entity],RelatingPropertyDefinition=ps)
    return ps


def pset(entity,name):
    return ifcopenshell.util.element.get_psets(entity).get(name,{})


def state(f):
    return json.loads(pset(f.by_type('IfcProject')[0], 'BimExecution')['State'])


def set_state(f, value):
    project=f.by_type('IfcProject')[0]
    for rel in project.IsDefinedBy:
        ps=rel.RelatingPropertyDefinition
        if ps.Name=='BimExecution':
            ps.HasProperties[0].NominalValue=f.create_entity('IfcText',json.dumps(value,sort_keys=True))
            return


def body(f,e,context, box):
    x,y,z,dx,dy,dz=box
    e.ObjectPlacement=placement(f,(x,y,z))
    profile=f.create_entity('IfcRectangleProfileDef',ProfileType='AREA',Position=f.create_entity('IfcAxis2Placement2D',Location=point(f,(dx/2,dy/2))),XDim=float(dx),YDim=float(dy))
    solid=f.create_entity('IfcExtrudedAreaSolid',SweptArea=profile,Position=axis(f),ExtrudedDirection=f.create_entity('IfcDirection',DirectionRatios=(0.,0.,1.)),Depth=float(dz))
    representation=f.create_entity('IfcShapeRepresentation',ContextOfItems=context,RepresentationIdentifier='Body',RepresentationType='SweptSolid',Items=[solid])
    e.Representation=f.create_entity('IfcProductDefinitionShape',Representations=[representation])


def bounds(e):
    solid=e.Representation.Representations[0].Items[0]
    p=solid.SweptArea
    x,y,z=e.ObjectPlacement.RelativePlacement.Location.Coordinates
    return [x,y,z,p.XDim,p.YDim,solid.Depth]


def alter(e,box):
    x,y,z,dx,dy,dz=box
    e.ObjectPlacement.RelativePlacement.Location.Coordinates=(float(x),float(y),float(z))
    solid=e.Representation.Representations[0].Items[0]
    p=solid.SweptArea
    p.XDim=float(dx);p.YDim=float(dy)
    p.Position.Location.Coordinates=(float(dx/2),float(dy/2))
    solid.Depth=float(dz)


def entities(f):
    return {pset(e,'BimIdentity')['LogicalId']:e for e in f.by_type('IfcProduct') if 'LogicalId' in pset(e,'BimIdentity')}


def create(path, compiled=None):
    f=ifcopenshell.file(schema='IFC4')
    context=f.create_entity('IfcGeometricRepresentationContext',ContextIdentifier='Model',ContextType='Model',CoordinateSpaceDimension=3,Precision=1e-7,WorldCoordinateSystem=axis(f))
    units=f.create_entity('IfcUnitAssignment',Units=[f.create_entity('IfcSIUnit',UnitType='LENGTHUNIT',Prefix='MILLI',Name='METRE'),f.create_entity('IfcSIUnit',UnitType='AREAUNIT',Name='SQUARE_METRE'),f.create_entity('IfcSIUnit',UnitType='VOLUMEUNIT',Name='CUBIC_METRE')])
    project=root(f,'IfcProject','Isolated evaluation',RepresentationContexts=[context],UnitsInContext=units)
    site=root(f,'IfcSite','Site',CompositionType='ELEMENT',ObjectPlacement=placement(f,(0,0,0)))
    building=root(f,'IfcBuilding','Building',CompositionType='ELEMENT',ObjectPlacement=placement(f,(0,0,0)))
    level=root(f,'IfcBuildingStorey','level',CompositionType='ELEMENT',Elevation=0.,ObjectPlacement=placement(f,(0,0,0)))
    for parent,child in [(project,site),(site,building),(building,level)]:
        root(f,'IfcRelAggregates','Spatial hierarchy',RelatingObject=parent,RelatedObjects=[child])
    e={'level':level}
    specs={'west':('IfcWall',(-200,0,0,200,4000,3000)), 'east':('IfcWall',(5000,0,0,200,4000,3000)), 'south':('IfcWall',(-200,-200,0,5400,200,3000)), 'north':('IfcWall',(-200,4000,0,5400,200,3000)), 'door':('IfcDoor',(1000,-200,0,900,200,2100)), 'window-north':('IfcWindow',(1500,4000,900,1200,200,1200)), 'window-east':('IfcWindow',(5000,1500,900,200,1200,1200)), 'room':('IfcSpace',(0,0,0,5000,4000,3000))}
    if compiled:
        g=compiled['a'];cx,cy=g['x'],g['y']
        specs.update({r:(specs[r][0],box) for r,box in g['boxes'].items()})
        specs['door']=('IfcDoor',(cx+1000,cy-200,0,900,200,2100))
        specs['window-north']=('IfcWindow',(cx+1500,cy+g['depth'],900,1200,200,1200))
        specs['window-east']=('IfcWindow',(cx+g['width'],cy+1500,900,200,1200,1200))
    for name,(kind,box) in specs.items():
        extra={'CompositionType':'ELEMENT','PredefinedType':'INTERNAL'} if kind=='IfcSpace' else {}
        e[name]=root(f,kind,name,**extra)
        body(f,e[name],context,box)
        if kind in ('IfcDoor','IfcWindow'):
            e[name].OverallHeight=float(box[5]);e[name].OverallWidth=float(box[3] if name!='window-east' else box[4])
    root(f,'IfcRelContainedInSpatialStructure','Storey contents',RelatedElements=[v for k,v in e.items() if k not in ('level','room')],RelatingStructure=level)
    root(f,'IfcRelAggregates','Space storey',RelatingObject=level,RelatedObjects=[e['room']])
    for name,host in [('door','south'),('window-north','north'),('window-east','east')]:
        opening=root(f,'IfcOpeningElement','opening-'+name,PredefinedType='OPENING')
        body(f,opening,context,bounds(e[name]))
        root(f,'IfcRelVoidsElement','Voids',RelatingBuildingElement=e[host],RelatedOpeningElement=opening)
        root(f,'IfcRelFillsElement','Fills',RelatingOpeningElement=opening,RelatedBuildingElement=e[name])
    for wall in ('west','east','south','north'):
        root(f,'IfcRelSpaceBoundary','Boundary',RelatingSpace=e['room'],RelatedBuildingElement=e[wall],PhysicalOrVirtualBoundary='PHYSICAL',InternalOrExternalBoundary='INTERNAL')
    for logical,entity in e.items():props(f,entity,'BimIdentity',{'LogicalId':logical})
    initial={'revision':'r0','receipts':{}}
    if compiled:
        packet=compiled['packet'];revision=compiled['before']['Revision']
        initial.update(revision='r'+str(revision),compiled_revision=revision,compiled_hash=compiled['plan']['BeforeHash'],compiled_project=compiled['before']['Id'])
        for role,logical in packet['LogicalRoles'].items():props(f,e[role],'BimCompiledIdentity',{'LogicalId':logical,'Units':'m','Source':compiled['a']['provenance']['Source'],'Approval':compiled['a']['provenance']['Approval']})
    props(f,project,'BimExecution',{'State':json.dumps(initial)})
    f.write(str(path))


def resize(path,width,operation,force_failure=False):
    if not isinstance(width,(int,float)) or isinstance(width,bool) or not math.isfinite(width) or width<3000 or not isinstance(operation,str) or not operation.strip():
        raise ValueError('invalid width/operation')
    f=ifcopenshell.open(str(path)); s=state(f)
    if operation in s['receipts']:
        if s['receipts'][operation]['width']!=width: raise ValueError('operation payload conflict')
        return False
    e=entities(f)
    alter(e['east'],(width,0,0,200,4000,3000))
    for name,y in [('south',-200),('north',4000)]:alter(e[name],(-200,y,0,width+400,200,3000))
    alter(e['room'],(0,0,0,width,4000,3000))
    for target in (e['window-east'],e['window-east'].FillsVoids[0].RelatingOpeningElement):
        b=bounds(target);b[0]=width;alter(target,b)
    s['revision']='r'+str(int(s['revision'][1:])+1)
    s['receipts'][operation]={'width':width,'revision':s['revision']}
    set_state(f,s)
    if force_failure:
        # Fail after real in-memory mutations, before atomic publication; persistent IFC is untouched.
        alter(e['east'],(width,0,0,-200,4000,3000))
        raise ValueError('forced invalid negative wall thickness; transaction discarded')
    temporary=path.with_suffix('.pending.ifc')
    try:
        with temporary.open('x', encoding='utf-8'):
            pass
    except FileExistsError as exc:
        raise ValueError('existing staging artifact; mutation not published') from exc
    try:
        f.write(str(temporary))
        validate(ifcopenshell.open(str(temporary)))
        os.replace(temporary,path)
    finally:
        if temporary.exists():temporary.unlink()
    return True


def verify_compiled(path, compiled, desired):
    f=ifcopenshell.open(str(path));e=entities(f)
    geometry=compiled['b'] if desired else compiled['a']
    for role,logical in compiled['packet']['LogicalRoles'].items():
        if role not in e or pset(e[role],'BimCompiledIdentity').get('LogicalId')!=logical:raise ValueError('compiled UUID binding mismatch')
        if role=='level':
            if e[role].Elevation!=0 or tuple(e[role].ObjectPlacement.RelativePlacement.Location.Coordinates)!=(0.,0.,0.):raise ValueError('compiled level drift')
        else:
            actual=bounds(e[role]);expected=geometry['boxes'][role]
            if any(abs(a-b)>1e-6 for a,b in zip(actual,expected)):raise ValueError('compiled geometry mismatch '+role)
            triangulate(e[role])
    return {logical:dict(role=role,global_id=e[role].GlobalId,step_id=e[role].id()) for role,logical in compiled['packet']['LogicalRoles'].items()}


def retain_packet(path, packet):
    # ADR0007 option A: the adapter packet is durable beside the IFC before publication so Core can replay the receipt.
    target=path.with_name('compiled-request.json');pending=path.with_name('compiled-request.pending.json')
    for candidate in (pending,target):
        if candidate.is_symlink() or (candidate.exists() and not candidate.is_file()):raise ValueError('refusing to stage compiled packet through a link or non-regular file: '+candidate.name)
    try:descriptor=os.open(pending,os.O_WRONLY|os.O_CREAT|os.O_TRUNC|getattr(os,'O_NOFOLLOW',0)|getattr(os,'O_BINARY',0),0o666)
    except OSError as exc:raise ValueError('refusing to stage compiled packet: '+str(exc)) from exc
    with os.fdopen(descriptor,'w',encoding='utf-8') as handle:
        if not stat.S_ISREG(os.fstat(handle.fileno()).st_mode):raise ValueError('refusing to stage compiled packet into a non-regular file')
        handle.write(json.dumps(packet,indent=2));handle.flush();os.fsync(handle.fileno())
    os.replace(pending,target)


def resize_compiled(path, packet, force_failure=False, crash_after=None):
    if crash_after not in CRASH_POINTS:raise ValueError('unknown crash injection point')
    compiled=validate_request(packet) # Always before any filesystem mutation.
    f=ifcopenshell.open(str(path));s=state(f);operation=packet['OperationId']
    if operation in s['receipts']:
        expected_revision=compiled['plan']['ExpectedRevision']+1
        if s.get('compiled_project')!=compiled['before']['Id'] or s.get('compiled_revision')!=expected_revision or s.get('revision')!='r'+str(expected_revision) or s.get('compiled_hash')!=compiled['committed_hash']:raise ValueError('corrupt compiled retry state')
        expected_receipt={'width':compiled['b']['width'],'revision':'r'+str(expected_revision),'compiled_fingerprint':compiled['fingerprint'],'plan_after_hash':compiled['plan']['AfterHash']}
        if s['receipts'][operation]!=expected_receipt:raise ValueError('corrupt compiled receipt')
        if s['receipts'][operation].get('compiled_fingerprint')!=compiled['fingerprint']:raise ValueError('operation payload conflict')
        verify_compiled(path,compiled,True)
        return False
    if s.get('compiled_project')!=compiled['before']['Id'] or s.get('compiled_revision')!=compiled['plan']['ExpectedRevision'] or s.get('revision')!='r'+str(compiled['plan']['ExpectedRevision']) or s.get('compiled_hash')!=compiled['plan']['BeforeHash']:raise ValueError('compiled revision conflict')
    verify_compiled(path,compiled,False)
    if not compiled['plan']['Operations']:return False
    e=entities(f);byid={logical:role for role,logical in packet['LogicalRoles'].items()}
    for op in compiled['plan']['Operations']:
        role=byid.get(op['Element']['Id'])
        if op['Action']!='update' or role not in compiled['b']['boxes']:raise ValueError('unsupported compiled operation')
        alter(e[role],compiled['b']['boxes'][role])
    # Hosted evaluation extras are adapter-owned fixture dependents, not Core elements.
    for target in (e['window-east'],e['window-east'].FillsVoids[0].RelatingOpeningElement):
        box=bounds(target);box[0]=compiled['b']['x']+compiled['b']['width'];alter(target,box)
    revision=compiled['plan']['ExpectedRevision']+1
    s.update(revision='r'+str(revision),compiled_revision=revision,compiled_hash=compiled['committed_hash'])
    s['receipts'][operation]={'width':compiled['b']['width'],'revision':s['revision'],'compiled_fingerprint':compiled['fingerprint'],'plan_after_hash':compiled['plan']['AfterHash']}
    set_state(f,s)
    if force_failure:raise ValueError('forced compiled transaction discarded after in-memory mutations')
    temporary=path.with_suffix('.pending.ifc')
    try:
        with temporary.open('x',encoding='utf-8'):pass
    except FileExistsError as exc:raise ValueError('existing staging artifact') from exc
    if crash_after=='after_staging_create':raise InjectedCrash(crash_after)
    crashed=False
    try:
        retain_packet(path,packet)
        f.write(str(temporary))
        if crash_after=='after_staging_write':crashed=True;raise InjectedCrash(crash_after)
        validate(ifcopenshell.open(str(temporary)))
        verify_compiled(temporary,compiled,True);inspect(temporary)
        os.replace(temporary,path)
    finally:
        if not crashed and temporary.exists():temporary.unlink()
    if crash_after=='after_replace':raise InjectedCrash(crash_after)
    return True


def receipt(path):
    """Read-only backend receipt for the Core reconcile verb; never writes the IFC or the staging file."""
    staging=path.with_suffix('.pending.ifc')
    present=staging.exists() or staging.is_symlink()
    observed=hashlib.sha256(staging.read_bytes()).hexdigest().upper() if present and staging.is_file() and not staging.is_symlink() else None
    return dict(state=state(ifcopenshell.open(str(path))),staging_artifact_present=present,staging_hash=observed)


def clear_staging(path, expected_hash):
    """Remove a crash-left staging file only when it is a regular file, not the published IFC, and holds exactly the bytes a receipt observed."""
    staging=path.with_suffix('.pending.ifc')
    if staging.is_symlink() or not staging.is_file():raise ValueError('staging artifact absent or not a regular file')
    if os.path.samefile(staging,path):raise ValueError('staging artifact is the published IFC')
    if not isinstance(expected_hash,str) or hashlib.sha256(staging.read_bytes()).hexdigest().upper()!=expected_hash:raise ValueError('staging artifact differs from the observed receipt')
    state(ifcopenshell.open(str(path)))
    staging.unlink()


def validate(f):
    logger=ifcopenshell.validate.json_logger()
    ifcopenshell.validate.validate(f,logger,express_rules=True)
    if logger.statements: raise ValueError('IFC schema validation: '+str(logger.statements))


def triangulate(e):
    settings=ifcopenshell.geom.settings()
    settings.set(settings.USE_WORLD_COORDS,True)
    shape=ifcopenshell.geom.create_shape(settings,e)
    verts=shape.geometry.verts; faces=shape.geometry.faces
    if not verts or not faces:raise ValueError('missing triangulated geometry '+e.Name)
    volume=0.
    for i in range(0,len(faces),3):
        a,b,c=[verts[faces[i+j]*3:faces[i+j]*3+3] for j in range(3)]
        volume += (a[0]*(b[1]*c[2]-b[2]*c[1])+a[1]*(b[2]*c[0]-b[0]*c[2])+a[2]*(b[0]*c[1]-b[1]*c[0]))/6
    xyz=[verts[i::3] for i in range(3)]
    actual=[min(v)*1000 for v in xyz]+[(max(v)-min(v))*1000 for v in xyz]
    expected=bounds(e)
    if any(abs(a-b)>1e-5 for a,b in zip(actual,expected)):
        raise ValueError('unsupported/incorrect transformed bounds '+e.Name)
    return abs(volume),len(faces)//3


def observe(path):
    f=ifcopenshell.open(str(path));e=entities(f)
    width=bounds(e['room'])[3];depth=bounds(e['room'])[4]
    result=[]
    for logical,v in sorted(e.items()):
        category={'IfcBuildingStorey':'Level','IfcWall':'Wall','IfcDoor':'Door','IfcWindow':'Window','IfcSpace':'Room'}[v.is_a()]
        host=None if logical=='level' else 'level';refs=[];geometry=[];extra={}
        if category=='Wall':
            x,y,z,dx,dy,dz=bounds(v)
            geometry={'west':[x+dx,y,x+dx,y+dy], 'east':[x,y,x,y+dy], 'south':[x+200,y+dy,x+dx-200,y+dy], 'north':[x+200,y,x+dx-200,y]}[logical]
            extra={'thickness_mm':dx if logical in ('west','east') else dy,'height_mm':dz}
        elif category in ('Door','Window'):
            opening=v.FillsVoids[0].RelatingOpeningElement
            host=pset(opening.VoidsElements[0].RelatingBuildingElement,'BimIdentity')['LogicalId']
            x,y,z,dx,dy,dz=bounds(v)
            geometry=[x,y+dy] if host=='south' else [x,y]
            extra={'width_mm':dy if host=='east' else dx}
        elif category=='Room':
            refs=sorted(pset(r.RelatedBuildingElement,'BimIdentity')['LogicalId'] for r in v.BoundedBy)
            vol,_=triangulate(v)
            extra={'area_m2':round(vol/(bounds(v)[5]/1000),9)}
        result.append(dict(logical_id=logical,native_id=v.GlobalId,category=category,host_id=host,references=refs,geometry=geometry,**extra))
    return dict(clear_width_mm=width,clear_depth_mm=depth,wall_thickness_mm=bounds(e['west'])[3],wall_height_mm=bounds(e['west'])[5],door_width_mm=e['door'].OverallWidth,units='mm' if f.by_type('IfcProject')[0].UnitsInContext.Units[0].Prefix=='MILLI' else 'unknown',elements=result,dimension_width_mm=None,schedule_area_m2=None,saved=path.exists(),reopened=True,native_file=path.name)


def inspect(path):
    f=ifcopenshell.open(str(path));validate(f);e=entities(f)
    triangles=0
    for name,v in e.items():
        if name=='level':continue
        volume,count=triangulate(v);triangles+=count
        x,y,z,dx,dy,dz=bounds(v)
        expected=dx*dy*dz/1e9
        if v.is_a('IfcWall'):
            for rel in v.HasOpenings:
                opening=rel.RelatedOpeningElement
                triangulate(opening)
                ob=bounds(opening);expected-=ob[3]*ob[4]*ob[5]/1e9
                if len(opening.HasFillings)!=1:raise ValueError('opening fill missing')
                filling=opening.HasFillings[0].RelatedBuildingElement
                if any(abs(a-b)>1e-6 for a,b in zip(ob,bounds(filling))):raise ValueError('opening/filling placement mismatch')
                if not all(bounds(v)[i]-1e-6 <= ob[i] and ob[i]+ob[i+3] <= bounds(v)[i]+bounds(v)[i+3]+1e-6 for i in range(3)):raise ValueError('opening outside host')
        if abs(volume-expected)>1e-7:raise ValueError(f'{name}: triangulated volume {volume} != expected cut volume {expected}')
    if len(f.by_type('IfcOpeningElement'))!=3 or len(f.by_type('IfcRelFillsElement'))!=3 or len(f.by_type('IfcRelSpaceBoundary'))!=4:raise ValueError('hosting/boundary count')
    return triangles


def execute(directory,packet=None):
    compiled=validate_request(packet) if packet is not None else None
    if compiled and not compiled['plan']['Operations']:raise ValueError('A/B evaluation requires a width correction; use lowlevel resize_compiled for a no-op')
    if not directory.is_absolute():raise ValueError('output-dir must be absolute')
    directory.mkdir(parents=True,exist_ok=True)
    path=directory/'room.ifc'
    owned=('room.ifc','before.ifc','hashes.json','benchmarks.json','room.pending.ifc','compiled-request.json','compiled-request.pending.json')
    if any((directory/name).exists() for name in owned):
        raise ValueError('output directory contains an existing worker artifact; use a fresh fixture directory')
    total_start=time.perf_counter();start=time.perf_counter();create(path,compiled);create_ms=(time.perf_counter()-start)*1000
    a=observe(path);before_hash=hashlib.sha256(path.read_bytes()).hexdigest()
    baseline=directory/'before.ifc';baseline.write_bytes(path.read_bytes())
    revision_before=state(ifcopenshell.open(str(path)))['revision']
    forced=False
    start=time.perf_counter()
    try:
        if compiled:resize_compiled(path,packet,True)
        else:resize(path,5400,'forced-invalid',True)
    except ValueError:forced=True
    rollback_ms=(time.perf_counter()-start)*1000
    rollback=observe(path)
    rollback_revision=state(ifcopenshell.open(str(path)))['revision']
    rollback_hash=hashlib.sha256(path.read_bytes()).hexdigest()
    before_entity_ids={v.GlobalId:v.id() for v in ifcopenshell.open(str(path)).by_type('IfcRoot')}
    start=time.perf_counter()
    if compiled:resize_compiled(path,packet)
    else:resize(path,5400,'resize-400')
    update_ms=(time.perf_counter()-start)*1000
    after=observe(path);after_hash=hashlib.sha256(path.read_bytes()).hexdigest()
    revision_after=state(ifcopenshell.open(str(path)))['revision']
    start=time.perf_counter()
    if compiled:resize_compiled(path,packet)
    else:resize(path,5400,'resize-400')
    retry_ms=(time.perf_counter()-start)*1000
    retry=observe(path);conflict=False
    try:
        if compiled:
            resize(path,compiled['b']['width']+400,packet['OperationId'])
        else:resize(path,5800,'resize-400')
    except ValueError:conflict=True
    retry_hash=hashlib.sha256(path.read_bytes()).hexdigest()
    retry_state=state(ifcopenshell.open(str(path)))
    semantic=[x['logical_id'] for x,y in zip(a['elements'],after['elements']) if x!=y]
    start=time.perf_counter();triangles=inspect(baseline)+inspect(path);validation_ms=(time.perf_counter()-start)*1000
    current=ifcopenshell.open(str(path));after_entity_ids={v.GlobalId:v.id() for v in current.by_type('IfcRoot')}
    expected_receipts={'revision':'r1','receipts':{'resize-400':{'width':5400,'revision':'r1'}}}
    if compiled:
        committed_revision=compiled['before']['Revision']+1
        expected_receipts={'revision':'r'+str(committed_revision),'compiled_revision':committed_revision,'compiled_project':compiled['before']['Id'],'compiled_hash':compiled['committed_hash'],'receipts':{packet['OperationId']:{'width':compiled['b']['width'],'revision':'r'+str(committed_revision),'compiled_fingerprint':compiled['fingerprint'],'plan_after_hash':compiled['plan']['AfterHash']}}}
    changed=CHANGED if not compiled or compiled['plan']['Operations'] else []
    conditions={'ifc_schema':True,'ifc_geometry':triangles>0,'ifc_hosting':True,'ifc_save_reopen':a['saved'] and after['reopened'],'ifc_idempotency':retry==after and retry_hash==after_hash,'ifc_rollback':forced and a==rollback and before_hash==rollback_hash and rollback_revision==revision_before,'ifc_incremental':before_entity_ids==after_entity_ids and set(semantic)==set(changed),'ifc_receipts':conflict and retry_state==expected_receipts}
    checks=[dict(id=k,outcome='pass' if v else 'fail',reason='actual reopened IFC schema/entities/triangulated geometry or persisted transaction evidence',expected=None,actual=None,tolerance=None) for k,v in conditions.items()]
    b=dict(before=a,after=after,retry=retry,rollback=rollback,anchor_logical_id='west',allowed_changed_ids=changed,semantic_changed_ids=semantic,updated_document_ids=[],operation_id=packet['OperationId'] if compiled else 'resize-400',retry_operation_id=packet['OperationId'] if compiled else 'resize-400',forced_failure_observed=forced,validation_passed=all(conditions.values()),revision_before=revision_before,revision_after=revision_after,retry_revision=retry_state['revision'],rollback_revision=rollback_revision)
    timings={'create_ms':create_ms,'update_ms':update_ms,'retry_ms':retry_ms,'rollback_ms':rollback_ms,'validation_ms':validation_ms,'total_ms':(time.perf_counter()-total_start)*1000}
    names=['intent_parse_ms','semantic_query_ms','solver_ms','diff_ms','runtime_wait_ms','revit_execute_ms','revit_regeneration_ms','native_readback_ms','validation_ms','documentation_ms','total_ms','llm_calls','model_elements_read','model_elements_written','transaction_count','unintended_element_changes']
    metrics={name:{'value':None,'reason':'not instrumented or not applicable to isolated IFC fixture authoring'} for name in names}
    for name in ('validation_ms','total_ms'):
        metrics[name]={'value':timings[name],'reason':'measured elapsed worker time'}
    counts={'model_elements_read':sum(len(snapshot['elements']) for snapshot in (a,rollback,after,retry)), 'model_elements_written':len(a['elements'])+len(semantic), 'unintended_element_changes':len(set(semantic)-set(CHANGED))}
    for name,value in counts.items():
        metrics[name]={'value':value,'reason':'logical product observations/authored/updated; excludes auxiliary IFC entities and validation traversal'}
    (directory/'benchmarks.json').write_text(json.dumps(timings,indent=2),encoding='utf-8')
    hashes={'before.ifc':before_hash,'room.ifc':after_hash}
    (directory/'hashes.json').write_text(json.dumps(hashes,indent=2),encoding='utf-8')
    artifacts=['before.ifc','room.ifc','hashes.json','benchmarks.json']
    bindings={}
    if compiled:
        bindings=verify_compiled(path,compiled,True)
        retain_packet(path,packet)
        artifacts.append('compiled-request.json')
    return dict(protocol_version=1,backend='ifc',backend_version=ifcopenshell.version,source=SOURCE,observations=dict(backend='ifc',source=SOURCE,test_a=a,test_b=b),metrics=metrics,checks=checks,artifacts=artifacts,errors=[k for k,v in conditions.items() if not v],compiled_bindings=bindings,unsupported_core_outputs=packet['UnsupportedDocumentationIds'] if compiled else [],fixture_only_roles=['door','window-north','window-east'])


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--output-dir');parser.add_argument('--request');parser.add_argument('--receipt');args=parser.parse_args()
    if args.receipt is not None:
        if args.output_dir is not None or args.request is not None:parser.error('--receipt is read-only and takes no other arguments')
        try:print(json.dumps(receipt(Path(args.receipt)),allow_nan=False));return 0
        except Exception as exc:print(str(exc),file=sys.stderr);return 1
    if args.output_dir is None or args.request is None:parser.error('the following arguments are required: --output-dir, --request')
    try:
        result=execute(Path(args.output_dir),json.loads(Path(args.request).read_text(encoding='utf-8-sig')));code=1 if result['errors'] else 0
    except Exception as exc:
        print(str(exc),file=sys.stderr)
        result=dict(protocol_version=1,backend='ifc',backend_version=ifcopenshell.version,source=SOURCE,observations=None,metrics={},checks=[],artifacts=[],errors=[str(exc)])
        code=1
    print(json.dumps(result,allow_nan=False));return code

if __name__=='__main__':sys.exit(main())
