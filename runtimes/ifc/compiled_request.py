"""Adapter-only binding of unchanged Core JSON snapshots to joined IFC bodies.
Documentation nodes and hosted evaluation extras are not authored from BIM-IR.
"""
import hashlib
import json
import math
import uuid

ROLES=['level','south','east','north','west','space','plan','schedule','sheet']
JOIN='horizontal-over-corners_vertical-between-inner-faces'

def sha(value):
    return hashlib.sha256(value.encode('utf-8')).hexdigest().upper()

def logical_id(room,role):
    return str(uuid.UUID(bytes_le=hashlib.sha256((str(uuid.UUID(room))+':'+role).encode()).digest()[:16]))

def load(raw):
    if not isinstance(raw,str):raise ValueError('raw Core JSON required')
    def invalid(value):raise ValueError('nonfinite JSON '+value)
    return json.loads(raw,parse_constant=invalid)

def geometry(project,room):
    if not isinstance(project,dict) or set(project)!={'Id','Revision','Elements'}:raise ValueError('invalid project')
    if str(uuid.UUID(project['Id']))!=project['Id'] or uuid.UUID(project['Id']).int==0:raise ValueError('invalid project identity')
    if type(project['Revision']) is not int or project['Revision']<0:raise ValueError('invalid revision')
    elements=project['Elements']
    if not isinstance(elements,list) or len(elements)!=9:raise ValueError('unsupported Core elements')
    ids={logical_id(room,r):r for r in ROLES}
    byrole={ids.get(e.get('Id')):e for e in elements}
    if set(byrole)!=set(ROLES):raise ValueError('unsupported or duplicate logical identity')
    s=byrole['space']['GeometryMetres'];w=byrole['west']['GeometryMetres']
    if len(s)!=4 or len(w)!=6:raise ValueError('invalid rectangle')
    cx,cy,width,depth=s;height,t=w[4:6]
    if any(type(v) not in (int,float) or not math.isfinite(v) for v in [*s,height,t]):raise ValueError('invalid dimensions')
    if width<3 or depth<3 or height<2.1 or t!=.2:raise ValueError('unsupported fixture dimensions')
    x=cx-t/2;y=cy-t/2;right=x+width+t;top=y+depth+t
    axes={'south':[x,y,right,y,height,t],'east':[right,y,right,top,height,t],'north':[right,top,x,top,height,t],'west':[x,top,x,y,height,t]}
    level=logical_id(room,'level');walls=[logical_id(room,r) for r in ['south','east','north','west']]
    expected={'level':('level',[0],[]),'space':('space',s,walls),'plan':('plan',[],[level,*walls,logical_id(room,'space')]),'schedule':('schedule',[],[logical_id(room,'space')]),'sheet':('sheet',[],[logical_id(room,'plan'),logical_id(room,'schedule')])}
    expected.update({r:('wall',g,[level]) for r,g in axes.items()})
    provenance=byrole['space']['Provenance']
    if not isinstance(provenance,dict) or set(provenance)!={'Source','Approval'} or not all(isinstance(v,str) and v.strip() for v in provenance.values()):raise ValueError('missing provenance')
    for role,e in byrole.items():
        kind,g,deps=expected[role]
        actual=e.get('GeometryMetres',[])
        if set(e)!={'Id','Kind','GeometryMetres','Dependencies','Provenance'} or e['Kind']!=kind or e['Dependencies']!=deps or e['Provenance']!=provenance or len(actual)!=len(g) or any(type(v) not in (int,float) or not math.isfinite(v) or abs(v-q)>1e-12 for v,q in zip(actual,g)):
            raise ValueError('unsupported Core geometry/dependency/provenance '+role)
    # Core dimensions are metres; IFC authoring is explicitly millimetres.
    cx,cy,width,depth,height,t=[v*1000 for v in (cx,cy,width,depth,height,t)]
    boxes={'west':[cx-t,cy,0,t,depth,height],'east':[cx+width,cy,0,t,depth,height],
           'south':[cx-t,cy-t,0,width+2*t,t,height],'north':[cx-t,cy+depth,0,width+2*t,t,height],
           'room':[cx,cy,0,width,depth,height]}
    return dict(boxes=boxes,roles=byrole,width=width,depth=depth,height=height,thickness=t,x=cx,y=cy,provenance=provenance)

def validate_request(packet):
    try:
        required={'ProtocolVersion','Units','RoomId','OperationId','BeforeJson','AfterJson','CommittedAfterJson','PlanJson','PlanSha256','LogicalRoles','UnsupportedDocumentationIds','JoinPolicy'}
        if not isinstance(packet,dict) or set(packet)!=required or type(packet['ProtocolVersion']) is not int or packet['ProtocolVersion']!=1 or packet['Units']!='m' or packet['JoinPolicy']!=JOIN or not isinstance(packet['OperationId'],str) or not packet['OperationId'].strip():raise ValueError('unsupported compiled request')
        room=packet['RoomId']
        if str(uuid.UUID(room))!=room or not uuid.UUID(room).int:raise ValueError('invalid room id')
        a=load(packet['BeforeJson']);b=load(packet['AfterJson']);plan=load(packet['PlanJson'])
        if sha(packet['PlanJson'])!=packet['PlanSha256'] or plan.get('BeforeHash')!=sha(packet['BeforeJson']) or plan.get('AfterHash')!=sha(packet['AfterJson']):raise ValueError('compiled raw JSON hash mismatch')
        committed=load(packet['CommittedAfterJson'])
        if committed!={**b,'Revision':b['Revision']+1}:raise ValueError('invalid committed snapshot')
        ga=geometry(a,room);gb=geometry(b,room)
        if a['Id']!=b['Id'] or a['Revision']!=b['Revision']:raise ValueError('revision conflict')
        if any(ga[k]!=gb[k] for k in ['depth','height','thickness','x','y','provenance']):raise ValueError('only width changes supported')
        roles={('room' if r=='space' else r):logical_id(room,r) for r in ROLES[:6]}
        if packet['LogicalRoles']!=roles or packet['UnsupportedDocumentationIds']!=[logical_id(room,r) for r in ROLES[6:]]:raise ValueError('invalid logical mappings')
        old={e['Id']:e for e in a['Elements']};new={e['Id']:e for e in b['Elements']}
        operations=[{'Action':'update','Element':e} for e in b['Elements'] if e!=old[e['Id']]]
        dirty={o['Element']['Id'] for o in operations}
        while True:
            expanded=dirty|{e['Id'] for e in a['Elements']+b['Elements'] if dirty.intersection(e['Dependencies'])}
            if expanded==dirty:break
            dirty=expanded
        expected=dict(ProjectId=a['Id'],ExpectedRevision=a['Revision'],BeforeHash=sha(packet['BeforeJson']),AfterHash=sha(packet['AfterJson']),Operations=operations,DirtyIds=sorted(dirty))
        if plan!=expected:raise ValueError('Core deterministic plan mismatch')
        return dict(before=a,after=b,plan=plan,a=ga,b=gb,packet=packet,committed_hash=sha(packet['CommittedAfterJson']),fingerprint=sha(packet['BeforeJson']+'\n'+packet['AfterJson']+'\n'+packet['CommittedAfterJson']+'\n'+packet['PlanJson']))
    except (KeyError,TypeError,IndexError,AttributeError) as exc:raise ValueError('malformed compiled request') from exc
