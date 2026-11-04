"""TEMPORARY probe (removed before hand-off): which rhino3dm 8.17 block construction crashes, and where."""
import subprocess
import sys
import tempfile
import unittest

PROBE = r"""
import os, sys, uuid
import rhino3dm
P3 = rhino3dm.Point3d
variant, folder, runs = sys.argv[1], sys.argv[2], int(sys.argv[3])
keep = []
def step(text):
    sys.stdout.write(text + ';')
    sys.stdout.flush()
for n in range(runs):
    step(f'{n}:new')
    model = rhino3dm.File3dm()
    layer = rhino3dm.Layer(); layer.Name = 'Walls'
    index = model.Layers.Add(layer)
    member = rhino3dm.LineCurve(P3(0, 0, 0), P3(1, 0, 0))
    attributes = rhino3dm.ObjectAttributes(); attributes.LayerIndex = index
    if variant == 'dup':
        member = member.Duplicate()
    if variant == 'defattr':
        attributes = rhino3dm.ObjectAttributes()
    geometry, attrs = (member,), (attributes,)
    keep.append((model, layer, member, attributes, geometry, attrs))
    step('add')
    if variant == 'addobj':
        model.Objects.Add(member, attributes)
    else:
        model.InstanceDefinitions.Add('door', '', '', '', P3(0, 0, 0), geometry, attrs)
    step('iter')
    if variant != 'noiter':
        _ = [o.Attributes.IsInstanceDefinitionObject for o in model.Objects]
    step('write')
    if variant != 'nowrite':
        model.Write(os.path.join(folder, f'{variant}{n}.3dm'), 7)
    step('read')
    if variant not in ('nowrite', 'noread'):
        back = rhino3dm.File3dm.Read(os.path.join(folder, f'{variant}{n}.3dm'))
        keep.append(back)
        _ = [(str(o.Attributes.Id), o.Attributes.IsInstanceDefinitionObject, type(o.Geometry).__name__) for o in back.Objects]
step('done')
os._exit(0)
"""


class ZzBlockProbe(unittest.TestCase):
    def test_probe(self):
        report = []
        with tempfile.TemporaryDirectory() as folder:
            for variant in ('base', 'addobj', 'nowrite', 'noread', 'noiter', 'dup', 'defattr'):
                child = subprocess.run([sys.executable, '-I', '-c', PROBE, variant, folder, '40'], capture_output=True, text=True, timeout=300)
                tail = child.stdout.split(';')[-4:]
                report.append(f'{variant}={child.returncode}@{"/".join(tail)}')
        self.fail(' # '.join(report))
