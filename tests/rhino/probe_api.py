"""Temporary CI probe of the installed rhino3dm API (removed once the adapter is green)."""
import os, sys, tempfile, traceback, uuid
import rhino3dm as r
out = []
def t(label, fn):
    try:
        v = fn()
        out.append(f'{label}=OK:{v!r}'[:1500])
        return v
    except Exception as e:
        out.append(f'{label}=ERR:{type(e).__name__}:{e}'[:1500])
t('version', lambda: getattr(r, '__version__', None))
m = r.File3dm()
t('units_set', lambda: setattr(m.Settings, 'ModelUnitSystem', r.UnitSystem.Meters))
t('units_get', lambda: m.Settings.ModelUnitSystem)
t('tol', lambda: m.Settings.ModelAbsoluteTolerance)
def lay():
    L = r.Layer(); L.Name = 'BIM'; i = m.Layers.Add(L); return i, m.Layers[i].Id
li, lid = t('layer_add', lay) or (None, None)
def child():
    L = r.Layer(); L.Name = 'Walls'; L.ParentLayerId = m.Layers[0].Id; i = m.Layers.Add(L); return i, m.Layers[i].FullPath
t('layer_child', child)
t('layer_addlayer', lambda: m.Layers.AddLayer('Spaces', (1, 2, 3, 255)))
t('layer_len', lambda: len(m.Layers))
t('layer_findname', lambda: m.Layers.FindName('Walls', m.Layers[0].Id))
pts = [r.Point3d(0, 0, 0), r.Point3d(5, 0, 0), r.Point3d(5, 0.2, 0), r.Point3d(0, 0.2, 0), r.Point3d(0, 0, 0)]
pc = t('polyline_curve', lambda: r.PolylineCurve(pts))
t('pc_closed', lambda: pc.IsClosed)
t('pc_pointcount', lambda: pc.PointCount)
t('pc_point', lambda: pc.Point(2))
ext = t('extrusion_create', lambda: r.Extrusion.Create(pc, 3.0, True))
t('ext_type', lambda: type(ext).__name__)
t('ext_bbox', lambda: (ext.GetBoundingBox(True).Min, ext.GetBoundingBox(True).Max))
t('ext_pathstart', lambda: (ext.PathStart, ext.PathEnd, ext.PathTangent))
t('ext_profilecount', lambda: ext.ProfileCount)
t('ext_profile3d', lambda: type(ext.Profile3d(0, 0.0)).__name__)
t('ext_profile3d_nurbs', lambda: [ (p.X,p.Y,p.Z) for p in ext.Profile3d(0, 0.0).ToNurbsCurve().Points])
t('ext_profile3d_poly', lambda: ext.Profile3d(0, 0.0).TryGetPolyline())
t('ext_tobrep', lambda: type(ext.ToBrep(False)).__name__)
t('ext_isvalid', lambda: ext.IsValid)
def pathup():
    e = r.Extrusion()
    ok1 = e.SetPathAndUp(r.Point3d(1, 2, 0.5), r.Point3d(4, 6, 0.5), r.Vector3d(0, 0, 1))
    prof = r.PolylineCurve([r.Point3d(-0.1, 0, 0), r.Point3d(0.1, 0, 0), r.Point3d(0.1, 3, 0), r.Point3d(-0.1, 3, 0), r.Point3d(-0.1, 0, 0)])
    ok2 = e.SetOuterProfile(prof, True)
    pts = [(q.X, q.Y, q.Z) for q in e.Profile3d(0, 0.0).ToNurbsCurve().Points]
    return ok1, ok2, e.IsValid, e.PathStart, e.PathEnd, pts, e.GetBoundingBox(True).Min, e.GetBoundingBox(True).Max
PATHUP = t('ext_pathup', pathup)
t('box_ext', lambda: type(r.Extrusion.CreateBoxExtrusion(r.Box(r.BoundingBox(r.Point3d(0,0,0), r.Point3d(1,1,1))), True)).__name__)
t('arc', lambda: r.ArcCurve(r.Arc(r.Point3d(0,0,0), r.Point3d(1,1,0), r.Point3d(2,0,0))).IsValid)
a = r.ObjectAttributes()
t('attr_layer', lambda: setattr(a, 'LayerIndex', 1))
t('attr_name', lambda: setattr(a, 'Name', 'w'))
fixed = uuid.UUID('00000000-0000-0000-0000-000000000002')
t('attr_id_set', lambda: setattr(a, 'Id', fixed))
t('attr_id_get', lambda: a.Id)
t('attr_us_set', lambda: a.SetUserString('bim.id', str(fixed)))
t('attr_us_get', lambda: a.GetUserString('bim.id'))
t('attr_us_all', lambda: a.GetUserStrings())
t('attr_us_count', lambda: a.UserStringCount)
def addpathup():
    e = r.Extrusion(); e.SetPathAndUp(r.Point3d(1, 2, 0.5), r.Point3d(4, 6, 0.5), r.Vector3d(0, 0, 1))
    e.SetOuterProfile(r.PolylineCurve([r.Point3d(-0.1, 0, 0), r.Point3d(0.1, 0, 0), r.Point3d(0.1, 3, 0), r.Point3d(-0.1, 3, 0), r.Point3d(-0.1, 0, 0)]), True)
    q = r.ObjectAttributes(); q.SetUserString('kind', 'pathup'); return m.Objects.Add(e, q)
t('objects_add_pathup', addpathup)
oid = t('objects_add', lambda: m.Objects.Add(ext, a))
t('objects_addext', lambda: m.Objects.AddExtrusion(ext, a))
p = r.ObjectAttributes(); p.SetUserString('k', 'v')
t('objects_addcurve', lambda: m.Objects.AddCurve(pc, p))
t('objects_addpoint', lambda: m.Objects.AddPoint(r.Point3d(0, 0, 4.5), p))
t('objects_addpointobj', lambda: m.Objects.Add(r.Point(r.Point3d(0,0,1)), p))
t('objects_len', lambda: len(m.Objects))
t('strings_set', lambda: m.Strings.SetString('bim.project', 'x'))
t('strings_get', lambda: m.Strings.GetValue('bim.project'))
t('strings_count', lambda: m.Strings.Count)
t('doc_us', lambda: m.SetUserString('bim.project', 'y'))
t('doc_us_get', lambda: m.GetUserString('bim.project'))
d = tempfile.mkdtemp()
p1, p2 = os.path.join(d, 'a.3dm'), os.path.join(d, 'b.3dm')
t('write', lambda: m.Write(p1, 7))
t('write8', lambda: m.Write(p2, 8))
t('write_same_bytes', lambda: (m.Write(p2, 7), open(p1,'rb').read() == open(p2,'rb').read(), os.path.getsize(p1)))
g = t('read', lambda: r.File3dm.Read(p1))
t('read_units', lambda: g.Settings.ModelUnitSystem)
t('read_layers', lambda: [(l.Name, l.FullPath, str(l.Id)) for l in g.Layers])
t('read_objs', lambda: [(type(o.Geometry).__name__, str(o.Attributes.Id), o.Attributes.LayerIndex, o.Attributes.GetUserStrings()) for o in g.Objects])
t('read_ext_pts', lambda: [ (p.X,p.Y,p.Z) for p in [o for o in g.Objects][0].Geometry.Profile3d(0,0.0).ToNurbsCurve().Points])
t('read_pathup', lambda: [(o.Geometry.PathStart, o.Geometry.PathEnd, [(q.X, q.Y, q.Z) for q in o.Geometry.Profile3d(0, 0.0).ToNurbsCurve().Points]) for o in g.Objects if o.Attributes.GetUserString('kind') == 'pathup'])
t('read_strings', lambda: g.Strings.GetValue('bim.project'))
t('read_doc_us', lambda: g.GetUserString('bim.project'))
t('read_point', lambda: [o.Geometry.Location for o in g.Objects if type(o.Geometry).__name__=='Point'])
bad = os.path.join(d, 'bad.3dm'); open(bad, 'wb').write(b'not a rhino file')
t('read_garbage', lambda: r.File3dm.Read(bad))
t('read_missing', lambda: r.File3dm.Read(os.path.join(d, 'nope.3dm')))
t('readver', lambda: r.File3dm.ReadArchiveVersion(p1))
t('unitsystems', lambda: [n for n in dir(r.UnitSystem) if not n.startswith('_')][:30])
t('dir_layer', lambda: [n for n in dir(r.Layer) if not n.startswith('_')])
t('dir_layers', lambda: [n for n in dir(r.File3dmLayerTable) if not n.startswith('_')])
t('dir_objtable', lambda: [n for n in dir(r.File3dmObjectTable) if not n.startswith('_')])
t('dir_ext', lambda: [n for n in dir(r.Extrusion) if not n.startswith('_')])
t('dir_attrs', lambda: [n for n in dir(r.ObjectAttributes) if not n.startswith('_')])
t('dir_file', lambda: [n for n in dir(r.File3dm) if not n.startswith('_')])
chunk = ''
lines = []
for s in out:
    if len(chunk) + len(s) > 3500:
        lines.append(chunk); chunk = ''
    chunk += s.replace('%', '%25').replace('\n', ' ') + ' || '
lines.append(chunk)
for i, c in enumerate(lines[:10]):
    print(f'::warning title=rhino3dm-probe-{i}::{c}')
print('\n'.join(out))
