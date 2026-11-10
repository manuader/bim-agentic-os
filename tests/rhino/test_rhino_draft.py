"""Real rhino3dm (MIT) regressions for the .3dm -> BIM-IR draft proposal reader. No Rhino installation is used.

Every test model is built here with rhino3dm; no .3dm binary is committed.
"""
import copy
import importlib.util
import json
import math
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest
import uuid

import rhino3dm

ROOT = pathlib.Path(__file__).resolve().parents[2]
WRITER = ROOT / 'adapters/rhino/bim_rhino.py'
DRAFT = ROOT / 'adapters/rhino/rhino_draft.py'


def _load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


rd = _load('rhino_draft', DRAFT)
# The draft module loads the writer itself; share that instance so RhinoAdapterError is one class.
br = rd.br
assert pathlib.Path(br.__file__).resolve() == WRITER

FIXTURE = json.loads((ROOT / 'tests/rhino/fixtures/room.json').read_text(encoding='utf-8'))
EXAMPLE = json.loads((ROOT / 'examples/after.json').read_text(encoding='utf-8'))
P3 = rhino3dm.Point3d


def oid(n):
    return uuid.UUID(int=0x1000 + n)


def sid(n):
    return str(oid(n))


def polyline(points, z=0.0):
    return rhino3dm.PolylineCurve([P3(x, y, z) for x, y in points])


def arc_curve(shape):
    """The curve rhino3dm stores for an arc or circle (8.17: a rational NurbsCurve; ArcCurve has no Arc constructor)."""
    scratch = rhino3dm.File3dm()
    (scratch.Objects.AddCircle if isinstance(shape, rhino3dm.Circle) else scratch.Objects.AddArc)(shape)
    geometry = list(scratch.Objects)[0].Geometry
    assert isinstance(geometry, rhino3dm.Curve) and not isinstance(geometry, (rhino3dm.PolylineCurve, rhino3dm.LineCurve)), type(geometry).__name__
    return geometry.Duplicate()


def rectangle(x0, y0, x1, y1, z=0.0):
    return polyline([(x0, y0), (x1, y0), (x1, y1), (x0, y1), (x0, y0)], z)


def vertical_extrusion(cx, cy, z0, height, length_x, width_y):
    """A footprint wall/room solid: vertical path at (cx, cy), rectangular profile centred on it."""
    extrusion = rhino3dm.Extrusion()
    assert extrusion.SetPathAndUp(P3(cx, cy, z0), P3(cx, cy, z0 + height), rhino3dm.Vector3d(0, 1, 0))
    a, b = length_x / 2, width_y / 2
    profile = rhino3dm.PolylineCurve([P3(-a, -b, 0), P3(a, -b, 0), P3(a, b, 0), P3(-a, b, 0), P3(-a, -b, 0)])
    assert extrusion.SetOuterProfile(profile, True)
    return extrusion


class Base(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = pathlib.Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def path(self, name):
        path = self.dir / name
        path.parent.mkdir(parents=True, exist_ok=True)
        return path

    def model(self, objects, units=rhino3dm.UnitSystem.Meters, name='model.3dm'):
        """objects: (geometry, layer full path, object uuid or None[, user strings]). Parent layers are created as needed."""
        model = self.build(objects, units)
        out = self.path(name)
        self.assertTrue(model.Write(str(out), 7))
        return out

    def build(self, objects, units=rhino3dm.UnitSystem.Meters):
        """The in-memory model behind model(); no file is written."""
        model = rhino3dm.File3dm()
        model.Settings.ModelUnitSystem = units
        index = {}

        def layer(path):
            if path in index:
                return index[path]
            parts = path.split('::')
            new = rhino3dm.Layer()
            new.Name = parts[-1]
            if len(parts) > 1:
                new.ParentLayerId = model.Layers[layer('::'.join(parts[:-1]))].Id
            index[path] = model.Layers.Add(new)
            return index[path]

        for geometry, layer_path, object_id, *strings in objects:
            attributes = rhino3dm.ObjectAttributes()
            attributes.LayerIndex = layer(layer_path)
            if object_id is not None:
                attributes.Id = object_id
            for key, value in sorted((strings[0] if strings else {}).items()):
                attributes.SetUserString(key, value)
            model.Objects.Add(geometry, attributes)
        return model

    def copied(self, source, edit=None, units=None, name='copy.3dm'):
        """Objects of a .3dm (with their user strings) copied into a new model.

        edit(geometry, strings, layer, id) returns (geometry, layer, id, strings)."""
        old = rhino3dm.File3dm.Read(str(source))
        objects = []
        for obj in old.Objects:
            geometry, strings = obj.Geometry, dict(obj.Attributes.GetUserStrings())
            layer, object_id = old.Layers[obj.Attributes.LayerIndex].FullPath, obj.Attributes.Id
            objects.append(edit(geometry, strings, layer, object_id) if edit is not None else (geometry, layer, object_id, strings))
        return self.model(objects, units=units if units is not None else old.Settings.ModelUnitSystem, name=name)

    def exported(self, project=FIXTURE, name='model.3dm'):
        out = self.path(name)
        br.export_project(copy.deepcopy(project), out)
        return out

    def draft(self, path, **options):
        result = rd.draft(path, **options)
        # Self-check on every draft: the proposal is a valid BIM-IR project the P8-02 writer accepts.
        br.check_representable(br.validate_project(copy.deepcopy(result['project'])))
        return result

    def proposal(self, result, key):
        found = [p for p in result['proposals'] if p['key'] == key]
        self.assertEqual(1, len(found), [p['key'] for p in result['proposals']])
        return found[0]

    def unmapped(self, result, object_id, part=None):
        return [u for u in result['unmapped'] if u['object_id'] == object_id and u['part'] == part]

    def assertUnmapped(self, reason, result, object_id, part=None):
        found = self.unmapped(result, object_id, part)
        self.assertEqual([reason], [u['reason'] for u in found], result['unmapped'])
        self.assertFalse([p for p in result['proposals'] if p['source']['object_id'] == object_id and p['source']['part'] == part])

    def assertRefused(self, code, fn, *args, **kwargs):
        with self.assertRaises(br.RhinoAdapterError) as ctx:
            fn(*args, **kwargs)
        self.assertEqual(code, ctx.exception.code, str(ctx.exception))

    def assertEndpoints(self, expected, g, tol=1e-9):
        got = sorted([(g[0], g[1]), (g[2], g[3])])
        self.assertClose([v for point in sorted(expected) for v in point], [v for point in got for v in point], tol)

    def assertChain(self, result, start, end, tol=1e-9):
        """Wall proposals ordered by part form one connected chain from start to end."""
        walls = sorted((p for p in result['proposals'] if p['kind'] == 'wall'), key=lambda p: p['source']['part'])
        self.assertEqual(list(range(len(walls))), [p['source']['part'] for p in walls])
        g = [p['element']['GeometryMetres'] for p in walls]
        self.assertClose(start, g[0][:2], tol)
        self.assertClose(end, g[-1][2:4], tol)
        for a, b in zip(g, g[1:]):
            self.assertClose(a[2:4], b[:2], tol)

    def assertAccounted(self, model, result, reason):
        """Every object rhino3dm kept in the model is unmapped with reason, and none is proposed."""
        ids = [str(o.Attributes.Id) for o in model.Objects]
        self.assertEqual([], result['proposals'])
        for object_id in ids:
            self.assertIn(reason, [u['reason'] for u in result['unmapped'] if u['object_id'] == object_id], result['unmapped'])
        return ids

    def assertClose(self, expected, actual, tol=1e-9):
        self.assertEqual(len(expected), len(actual), (expected, actual))
        for a, b in zip(expected, actual):
            self.assertLessEqual(abs(a - b), tol, (expected, actual))


class Envelope(Base):
    def test_draft_is_a_non_persisted_proposal(self):
        result = self.draft(self.model([(rectangle(0, 0, 4, 3), 'Spaces', oid(1))]))
        self.assertEqual(1, result['draft_version'])
        self.assertEqual('proposal', result['status'])
        self.assertFalse(result['persisted'])
        self.assertEqual('model.3dm', result['source']['file'])
        self.assertEqual('Meters', result['source']['units'])
        self.assertEqual(1, result['source']['metres_per_unit'])
        self.assertEqual(1, result['source']['objects'])
        self.assertEqual(['model.3dm'], sorted(p.name for p in self.dir.iterdir()))

    def test_project_holds_the_proposed_elements_in_core_shape(self):
        result = self.draft(self.model([(rectangle(0, 0, 4, 3), 'Spaces', oid(1))]))
        project = result['project']
        self.assertEqual({'Id', 'Revision', 'Elements'}, set(project))
        self.assertEqual(0, project['Revision'])
        self.assertEqual([p['element'] for p in result['proposals']], project['Elements'])
        space = project['Elements'][0]
        self.assertEqual({'Id': sid(1), 'Kind': 'space', 'GeometryMetres': [0, 0, 4, 3], 'Dependencies': [],
                          'Provenance': {'Source': 'rhino:model.3dm', 'Approval': 'draft-unapproved'}}, space)

    def test_project_id_is_an_option_or_derived_deterministically(self):
        source = self.model([(rectangle(0, 0, 4, 3), 'Spaces', oid(1))])
        chosen = str(uuid.UUID(int=77))
        self.assertEqual(chosen, self.draft(source, project_id=chosen)['project']['Id'])
        derived = self.draft(source)['project']['Id']
        self.assertEqual(derived, self.draft(source)['project']['Id'])
        self.assertIsNotNone(br.UUID_RE.match(derived))

    def test_bim_model_keeps_its_project_id(self):
        self.assertEqual(FIXTURE['Id'], self.draft(self.exported())['project']['Id'])

    def test_summary_counts(self):
        result = self.draft(self.exported())
        self.assertEqual({'levels': 1, 'walls': 4, 'doors': 0, 'windows': 0, 'spaces': 1, 'unmapped': 3, 'unmapped_by_reason': {'layer_unmapped': 3}}, result['summary'])


class LayerMapping(Base):
    def kind_on(self, layer):
        result = self.draft(self.model([(rectangle(0, 0, 4, 3), layer, oid(1))]))
        return [p['kind'] for p in result['proposals']], result

    def test_writer_layer_names_map(self):
        self.assertEqual(['space'], self.kind_on('BIM::Spaces')[0])

    def test_case_insensitive_singular_plural_and_synonyms(self):
        for layer in ('spaces', 'SPACE', 'Rooms', 'room', 'Espacios', 'Locales', 'Project A::Spaces'):
            with self.subTest(layer):
                self.assertEqual(['space'], self.kind_on(layer)[0])

    def test_wall_layer_names(self):
        for layer in ('Walls', 'wall', 'BIM::WALLS', 'Muros', 'muro'):
            with self.subTest(layer):
                result = self.draft(self.model([(rhino3dm.LineCurve(P3(0, 0, 0), P3(3, 0, 0)), layer, oid(1))], name=f'{layer}.3dm'.replace(':', '_')))
                self.assertEqual(['wall'], [p['kind'] for p in result['proposals']])

    def test_sublayer_inherits_the_nearest_named_ancestor(self):
        kinds, result = self.kind_on('BIM::Spaces::Ground floor')
        self.assertEqual(['space'], kinds)
        proposal = result['proposals'][0]
        self.assertIn('layer-inherited', proposal['notes'])
        self.assertAlmostEqual(0.9 * 0.9, proposal['confidence'], places=9)
        self.assertEqual(['wall'], [p['kind'] for p in self.draft(self.model([(rhino3dm.LineCurve(P3(0, 0, 0), P3(3, 0, 0)), 'Walls::Spaces stuff', oid(2))], name='w.3dm'))['proposals']])

    def test_nearest_ancestor_wins(self):
        self.assertEqual(['space'], self.kind_on('Walls::Spaces')[0])

    def test_names_must_match_whole(self):
        for layer in ('Wallpaper', 'Space frame', 'Default', 'BIM'):
            with self.subTest(layer):
                kinds, result = self.kind_on(layer)
                self.assertEqual([], kinds)
                self.assertUnmapped('layer_unmapped', result, sid(1))
                self.assertEqual(layer, result['unmapped'][0]['layer'])


class Walls(Base):
    def test_writer_style_axis_extrusion_is_exact(self):
        result = self.draft(self.exported())
        wall = self.proposal(result, '00000000-0000-0000-0000-000000000013')
        self.assertEqual([0, 4.2, -1.5, 2.0, 2.75, 0.15], wall['element']['GeometryMetres'])
        self.assertEqual(0.95, wall['confidence'])
        self.assertEqual('Extrusion', wall['source']['geometry_type'])
        self.assertEqual('BIM::Walls', wall['source']['layer'])

    def test_vertical_footprint_extrusion_infers_axis_thickness_and_height(self):
        result = self.draft(self.model([(vertical_extrusion(2, 1, 0, 3.0, 4.0, 0.25), 'Walls', oid(1))]))
        wall = self.proposal(result, sid(1))
        g = wall['element']['GeometryMetres']
        self.assertEndpoints([(0, 1), (4, 1)], g)
        self.assertClose([3.0, 0.25], g[4:])
        self.assertEqual(0.85, wall['confidence'])
        self.assertIn('footprint-rectangle', wall['notes'])

    def test_footprint_along_y_puts_the_axis_on_the_long_side(self):
        result = self.draft(self.model([(vertical_extrusion(0, 0, 0, 2.5, 0.2, 6.0), 'Walls', oid(1))]))
        g = self.proposal(result, sid(1))['element']['GeometryMetres']
        self.assertEndpoints([(0, -3), (0, 3)], g)
        self.assertClose([2.5, 0.2], g[4:])

    def test_extrusion_create_from_a_plan_outline(self):
        outline = rectangle(0, 0, 5, 0.2)
        box = rhino3dm.Extrusion.Create(outline, 3.0, True)
        result = self.draft(self.model([(box, 'Walls', oid(1))]))
        g = self.proposal(result, sid(1))['element']['GeometryMetres']
        self.assertEndpoints([(0, 0.1), (5, 0.1)], g)
        self.assertClose([3.0, 0.2], g[4:])

    def test_square_footprint_is_ambiguous(self):
        result = self.draft(self.model([(vertical_extrusion(0, 0, 0, 3, 0.4, 0.4), 'Walls', oid(1))]))
        self.assertUnmapped('wall_footprint_ambiguous', result, sid(1))

    def test_non_rectangular_footprint_is_unsupported(self):
        extrusion = rhino3dm.Extrusion()
        self.assertTrue(extrusion.SetPathAndUp(P3(0, 0, 0), P3(0, 0, 3), rhino3dm.Vector3d(0, 1, 0)))
        self.assertTrue(extrusion.SetOuterProfile(rhino3dm.PolylineCurve([P3(0, 0, 0), P3(4, 0, 0), P3(4, 1, 0), P3(0, 0, 0)]), True))
        self.assertUnmapped('wall_extrusion_unsupported', self.draft(self.model([(extrusion, 'Walls', oid(1))])), sid(1))

    def test_tilted_extrusion_is_unsupported(self):
        extrusion = rhino3dm.Extrusion()
        self.assertTrue(extrusion.SetPathAndUp(P3(0, 0, 0), P3(3, 0, 3), rhino3dm.Vector3d(0, 1, 0)))
        self.assertTrue(extrusion.SetOuterProfile(rectangle(-0.1, -1, 0.1, 1), True))
        self.assertUnmapped('wall_extrusion_unsupported', self.draft(self.model([(extrusion, 'Walls', oid(1))])), sid(1))

    def test_line_is_a_centreline_with_assumed_height_and_thickness(self):
        result = self.draft(self.model([(rhino3dm.LineCurve(P3(0, 0, 0), P3(5, 0, 0)), 'Walls', oid(1))]))
        self.assertEqual(1, len(result['proposals']))
        wall = result['proposals'][0]
        self.assertEqual(f'{sid(1)}:0', wall['key'])
        self.assertEqual([0, 0, 5, 0, 2.7, 0.2], wall['element']['GeometryMetres'])
        self.assertEqual(0.5, wall['confidence'])
        self.assertEqual(['centreline', 'height-assumed', 'thickness-assumed'], wall['notes'])
        self.assertEqual(0, wall['source']['part'])

    def test_assumed_dimensions_are_options(self):
        result = self.draft(self.model([(rhino3dm.LineCurve(P3(0, 0, 0), P3(5, 0, 0)), 'Walls', oid(1))]), wall_height=3.1, wall_thickness=0.15)
        self.assertEqual([0, 0, 5, 0, 3.1, 0.15], result['proposals'][0]['element']['GeometryMetres'])

    def test_polyline_gives_one_wall_per_segment_with_stable_derived_ids(self):
        source = self.model([(polyline([(0, 0), (4, 0), (4, 3), (0, 3), (0, 0)]), 'Walls', oid(1))])
        first, second = self.draft(source), self.draft(source)
        self.assertEqual([f'{sid(1)}:{i}' for i in range(4)], sorted(p['key'] for p in first['proposals']))
        ids = [p['element']['Id'] for p in first['proposals']]
        self.assertEqual(ids, [p['element']['Id'] for p in second['proposals']])
        self.assertEqual(4, len(set(ids)))
        self.assertNotIn(sid(1), ids)
        axes = sorted(tuple(p['element']['GeometryMetres'][:4]) for p in first['proposals'])
        self.assertEqual(sorted([(0, 0, 4, 0), (4, 0, 4, 3), (4, 3, 0, 3), (0, 3, 0, 0)]), axes)

    def test_degenerate_and_sloped_segments_are_unmapped_per_part(self):
        curve = rhino3dm.PolylineCurve([P3(0, 0, 0), P3(3, 0, 0), P3(3, 0, 0), P3(3, 2, 1)])
        result = self.draft(self.model([(curve, 'Walls', oid(1))]))
        self.assertEqual([f'{sid(1)}:0'], [p['key'] for p in result['proposals']])
        self.assertUnmapped('degenerate', result, sid(1), 1)
        self.assertUnmapped('curve_not_horizontal', result, sid(1), 2)

    def test_arc_is_unmapped_unless_approximation_is_asked(self):
        arc = arc_curve(rhino3dm.Arc(P3(0, 0, 0), 2.0, math.pi / 2))
        source = self.model([(arc, 'Walls', oid(1))])
        self.assertUnmapped('arc_not_approximated', self.draft(source), sid(1))
        result = self.draft(source, arc_segments=4)
        self.assertEqual(4, len(result['proposals']))
        self.assertChain(result, (2, 0), (0, 2))
        for proposal in result['proposals']:
            g = proposal['element']['GeometryMetres']
            self.assertAlmostEqual(2.0, math.hypot(g[0], g[1]), places=9)
            self.assertAlmostEqual(2.0, math.hypot(g[2], g[3]), places=9)
            self.assertAlmostEqual(2 * 2.0 * math.sin(math.pi / 16), math.hypot(g[2] - g[0], g[3] - g[1]), places=9)
            self.assertEqual(0.3, proposal['confidence'])
            self.assertIn('arc-approximated', proposal['notes'])

    def test_non_arc_nurbs_curve_is_unsupported(self):
        curve = rhino3dm.NurbsCurve.Create(False, 3, [P3(0, 0, 0), P3(1, 2, 0), P3(3, 2, 0), P3(4, 0, 0)])
        self.assertUnmapped('curve_unsupported', self.draft(self.model([(curve, 'Walls', oid(1))])), sid(1))

    def test_other_geometry_on_a_wall_layer_is_unsupported(self):
        result = self.draft(self.model([(rhino3dm.Point(P3(1, 1, 0)), 'Walls', oid(1))]))
        self.assertUnmapped('geometry_unsupported', result, sid(1))
        self.assertEqual('Point', result['unmapped'][0]['geometry_type'])


class Spaces(Base):
    def test_closed_axis_aligned_rectangle(self):
        result = self.draft(self.model([(rectangle(1, 2, 6, 5.5), 'Spaces', oid(1))]))
        space = self.proposal(result, sid(1))
        self.assertEqual([1, 2, 5, 3.5], space['element']['GeometryMetres'])
        self.assertEqual(0.9, space['confidence'])

    def test_clockwise_and_collinear_vertices_still_form_the_rectangle(self):
        curve = polyline([(0, 0), (0, 3), (2, 3), (4, 3), (4, 0), (0, 0)])
        self.assertEqual([0, 0, 4, 3], self.proposal(self.draft(self.model([(curve, 'Spaces', oid(1))])), sid(1))['element']['GeometryMetres'])

    def test_open_curves_are_unmapped(self):
        result = self.draft(self.model([(polyline([(0, 0), (4, 0), (4, 3), (0, 3)]), 'Spaces', oid(1)),
                                        (rhino3dm.LineCurve(P3(0, 0, 0), P3(1, 0, 0)), 'Spaces', oid(2))]))
        self.assertUnmapped('open_curve', result, sid(1))
        self.assertUnmapped('open_curve', result, sid(2))

    def test_l_shape_is_not_expressible(self):
        curve = polyline([(0, 0), (4, 0), (4, 2), (2, 2), (2, 4), (0, 4), (0, 0)])
        self.assertUnmapped('space_not_rectangle', self.draft(self.model([(curve, 'Spaces', oid(1))])), sid(1))

    def test_rotated_rectangle_is_not_axis_aligned(self):
        curve = rectangle(0, 0, 4, 3)
        self.assertTrue(curve.Rotate(0.3, rhino3dm.Vector3d(0, 0, 1), P3(0, 0, 0)))
        self.assertUnmapped('space_not_axis_aligned', self.draft(self.model([(curve, 'Spaces', oid(1))])), sid(1))

    def test_sloped_outline_is_not_horizontal(self):
        curve = rhino3dm.PolylineCurve([P3(0, 0, 0), P3(4, 0, 0), P3(4, 3, 1), P3(0, 3, 1), P3(0, 0, 0)])
        self.assertUnmapped('curve_not_horizontal', self.draft(self.model([(curve, 'Spaces', oid(1))])), sid(1))

    def test_circle_is_not_a_rectangle(self):
        circle = arc_curve(rhino3dm.Circle(P3(0, 0, 0), 2.0))
        self.assertUnmapped('space_not_rectangle', self.draft(self.model([(circle, 'Spaces', oid(1))]), arc_segments=8), sid(1))

    def test_room_solid_extrusion_drops_its_height(self):
        result = self.draft(self.model([(vertical_extrusion(2, 1.5, 0, 2.6, 4, 3), 'Rooms', oid(1))]))
        space = self.proposal(result, sid(1))
        self.assertClose([0, 0, 4, 3], space['element']['GeometryMetres'])
        self.assertEqual(0.8, space['confidence'])
        self.assertIn('height-dropped', space['notes'])

    def test_tiny_space_is_degenerate(self):
        self.assertUnmapped('degenerate', self.draft(self.model([(rectangle(0, 0, 4, 1e-8), 'Spaces', oid(1))])), sid(1))


class Levels(Base):
    def test_points_on_a_level_layer_are_levels_and_host_matching_elements(self):
        result = self.draft(self.model([(rhino3dm.Point(P3(0, 0, 3.0)), 'Levels', oid(1)),
                                        (rectangle(0, 0, 4, 3, 3.0), 'Spaces', oid(2)),
                                        (rhino3dm.LineCurve(P3(0, 0, 3.0), P3(4, 0, 3.0)), 'Walls', oid(3))]))
        level = self.proposal(result, sid(1))
        self.assertEqual([3.0], level['element']['GeometryMetres'])
        self.assertEqual([sid(1)], self.proposal(result, sid(2))['element']['Dependencies'])
        self.assertEqual([sid(1)], self.proposal(result, f'{sid(3)}:0')['element']['Dependencies'])

    def test_element_at_zero_without_level_has_no_dependency(self):
        result = self.draft(self.model([(rectangle(0, 0, 4, 3), 'Spaces', oid(1))]))
        self.assertEqual([], result['proposals'][0]['element']['Dependencies'])
        self.assertEqual(['space'], [p['kind'] for p in result['proposals']])

    def test_unmatched_elevation_synthesizes_one_shared_level(self):
        result = self.draft(self.model([(rectangle(0, 0, 4, 3, 6.0), 'Spaces', oid(1)),
                                        (rectangle(5, 0, 8, 3, 6.0), 'Spaces', oid(2))]), project_id=str(uuid.UUID(int=5)))
        levels = [p for p in result['proposals'] if p['kind'] == 'level']
        self.assertEqual(1, len(levels))
        self.assertEqual([6.0], levels[0]['element']['GeometryMetres'])
        self.assertEqual(0.6, levels[0]['confidence'])
        self.assertIn('level-synthesized', levels[0]['notes'])
        self.assertIsNone(levels[0]['source']['object_id'])
        for n in (1, 2):
            self.assertEqual([levels[0]['element']['Id']], self.proposal(result, sid(n))['element']['Dependencies'])
        again = self.draft(self.model([(rectangle(0, 0, 4, 3, 6.0), 'Spaces', oid(1))], name='again.3dm'), project_id=str(uuid.UUID(int=5)))
        self.assertEqual(levels[0]['element']['Id'], [p for p in again['proposals'] if p['kind'] == 'level'][0]['element']['Id'])

    def test_non_point_on_level_layer_is_unsupported(self):
        self.assertUnmapped('geometry_unsupported', self.draft(self.model([(rectangle(0, 0, 1, 1), 'Levels', oid(1))])), sid(1))


class Units(Base):
    def scaled(self, units, factor):
        s = rhino3dm.Transform.Scale(P3(0, 0, 0), factor)
        objects = []
        for geometry, layer, n in ((rectangle(1, 2, 6, 5.5, 3.0), 'Spaces', 1), (rhino3dm.Point(P3(0, 0, 3.0)), 'Levels', 2),
                                   (vertical_extrusion(2, 1, 3.0, 3.0, 4.0, 0.25), 'Walls', 3)):
            self.assertTrue(geometry.Transform(s))
            objects.append((geometry, layer, oid(n)))
        return self.draft(self.model(objects, units=units, name=f'{factor}.3dm'))

    def test_millimetres_centimetres_and_feet_scale_to_metres(self):
        for units, factor, per_unit in ((rhino3dm.UnitSystem.Millimeters, 1000.0, 0.001), (rhino3dm.UnitSystem.Centimeters, 100.0, 0.01),
                                        (rhino3dm.UnitSystem.Feet, 1 / 0.3048, 0.3048)):
            with self.subTest(units):
                result = self.scaled(units, factor)
                self.assertAlmostEqual(per_unit, result['source']['metres_per_unit'], places=12)
                self.assertClose([1, 2, 5, 3.5], self.proposal(result, sid(1))['element']['GeometryMetres'])
                self.assertClose([3.0], self.proposal(result, sid(2))['element']['GeometryMetres'])
                self.assertClose([3.0, 0.25], self.proposal(result, sid(3))['element']['GeometryMetres'][4:])
                self.assertEqual([sid(2)], self.proposal(result, sid(1))['element']['Dependencies'])

    def test_unset_units_are_refused_unless_a_scale_is_given(self):
        unitless = getattr(rhino3dm.UnitSystem, 'None')
        source = self.model([(rectangle(0, 0, 4000, 3000), 'Spaces', oid(1))], units=unitless)
        self.assertRefused('rhino_units_unsupported', rd.draft, source)
        result = self.draft(source, metres_per_unit=0.001)
        self.assertClose([0, 0, 4, 3], result['proposals'][0]['element']['GeometryMetres'])


class Limits(Base):
    def test_far_coordinates_are_unmapped(self):
        self.assertUnmapped('out_of_range', self.draft(self.model([(rectangle(2e5, 0, 2e5 + 4, 3), 'Spaces', oid(1))])), sid(1))

    def test_file_and_object_caps_refuse(self):
        source = self.model([(rectangle(0, 0, 4, 3), 'Spaces', oid(n)) for n in range(1, 4)])
        self.assertRefused('draft_limit', rd.draft, source, max_objects=2)
        self.assertRefused('draft_limit', rd.draft, source, max_file_bytes=16)
        self.assertRefused('draft_limit', rd.draft, source, max_proposals=2)

    def test_vertex_cap_unmaps_the_object(self):
        points = [(i * 0.1, (i % 2) * 0.1) for i in range(50)]
        self.assertUnmapped('too_many_vertices', self.draft(self.model([(polyline(points), 'Walls', oid(1))]), max_vertices=20), sid(1))

    def test_unmapped_report_is_capped_with_a_final_marker(self):
        source = self.model([(rhino3dm.Point(P3(n, 0, 0)), 'Other', oid(n)) for n in range(1, 6)])
        result = self.draft(source, max_unmapped=3)
        self.assertEqual(['layer_unmapped'] * 3 + ['unmapped_limit'], [u['reason'] for u in result['unmapped']])
        self.assertEqual(5, result['summary']['unmapped'])

    def test_invalid_options_are_refused(self):
        source = self.model([(rectangle(0, 0, 4, 3), 'Spaces', oid(1))])
        for options in ({'wall_height': 0}, {'wall_thickness': math.nan}, {'arc_segments': 1000}, {'arc_segments': 1.5},
                        {'project_id': 'not-a-uuid'}, {'metres_per_unit': -1}, {'colour': 'red'}):
            with self.subTest(options):
                self.assertRefused('draft_options_invalid', rd.draft, source, **options)

    def test_unreadable_input_is_refused(self):
        bad = self.path('bad.3dm')
        bad.write_bytes(b'not a rhino model')
        self.assertRefused('rhino_read_failed', rd.draft, bad)
        self.assertRefused('rhino_read_failed', rd.draft, self.path('absent.3dm'))


class Determinism(Base):
    def objects(self):
        return [(rectangle(0, 0, 4, 3), 'Spaces', oid(1)), (rhino3dm.LineCurve(P3(0, 0, 0), P3(4, 0, 0)), 'Walls', oid(2)),
                (rhino3dm.Point(P3(0, 0, 0)), 'Notes', oid(3)), (vertical_extrusion(2, 3, 0, 3, 4, 0.2), 'Walls', oid(4)),
                (rectangle(0, 0, 4, 3, 2.0), 'Spaces', oid(5))]

    def test_same_file_gives_identical_bytes(self):
        source = self.model(self.objects())
        self.assertEqual(rd.dumps(rd.draft(source)), rd.dumps(rd.draft(source)))

    def test_object_order_does_not_change_the_bytes(self):
        a = self.model(self.objects(), name='a/model.3dm')
        b = self.model(list(reversed(self.objects())), name='b/model.3dm')
        self.assertEqual(rd.dumps(rd.draft(a, project_id=sid(9))), rd.dumps(rd.draft(b, project_id=sid(9))))

    def test_proposals_are_ordered_level_wall_space_then_id(self):
        result = self.draft(self.model(self.objects()))
        rank = {'level': 0, 'wall': 1, 'space': 2}
        keys = [(rank[p['kind']], p['element']['Id']) for p in result['proposals']]
        self.assertEqual(sorted(keys), keys)
        self.assertEqual(['level', 'wall', 'wall', 'space', 'space'], [p['kind'] for p in result['proposals']])


class Roundtrip(Base):
    def test_writer_output_drafts_back_to_the_fixture_geometry_and_ids(self):
        result = self.draft(self.exported())
        expected = [e for e in FIXTURE['Elements'] if e['Kind'] in ('level', 'wall', 'space')]
        got = result['project']['Elements']
        self.assertEqual([(e['Id'], e['Kind']) for e in expected], [(e['Id'], e['Kind']) for e in got])
        # Same geometry bytes as the P8-02 reader's own import of the file (which snaps height/thickness to 1e-9 m).
        exact = {e['Id']: e['GeometryMetres'] for e in json.loads(br.dumps(br.import_project(self.path('model.3dm'))))['Elements']}
        for want, have in zip(expected, got):
            self.assertClose(want['GeometryMetres'], have['GeometryMetres'])
            self.assertEqual(json.dumps(exact[want['Id']]), json.dumps(have['GeometryMetres']), want['Id'])
            # The writer's bim.dependencies are kept (they agree with the geometry), space -> wall links included.
            self.assertEqual(want['Dependencies'], have['Dependencies'], want['Id'])
        self.assertEqual(sorted(e['Id'] for e in FIXTURE['Elements'] if e['Kind'] in ('plan', 'schedule', 'sheet')),
                         [u['object_id'] for u in result['unmapped']])

    def test_core_example_roundtrip(self):
        result = self.draft(self.exported(EXAMPLE))
        wall = EXAMPLE['Elements'][0]
        self.assertEqual([wall['Id']], [e['Id'] for e in result['project']['Elements']])
        self.assertEqual(wall['GeometryMetres'], result['project']['Elements'][0]['GeometryMetres'])

    def test_draft_project_exports_and_drafts_to_the_same_bytes(self):
        first = self.draft(self.exported(name='one/model.3dm'))
        project = first['project']
        second = self.draft(self.exported(project, name='two/model.3dm'))
        self.assertEqual(br.dumps(project), br.dumps(second['project']))
        self.assertEqual(json.dumps(first['proposals']), json.dumps(second['proposals']))

    def test_free_geometry_draft_roundtrips_through_the_writer(self):
        source = self.model([(rectangle(0, 0, 4, 3, 2.5), 'Spaces', oid(1)), (polyline([(0, 0), (4, 0), (4, 3)], 2.5), 'Walls', oid(2)),
                             (vertical_extrusion(2, 5, 0, 3, 4, 0.2), 'Walls', oid(3))], name='one/model.3dm')
        first = self.draft(source)
        second = self.draft(self.exported(first['project'], name='two/model.3dm'))
        self.assertEqual(br.dumps(first['project']), br.dumps(second['project']))

    def test_millimetre_writer_output(self):
        scale = rhino3dm.Transform.Scale(P3(0, 0, 0), 1000.0)
        old = rhino3dm.File3dm.Read(str(self.exported()))
        objects = []
        for obj in old.Objects:
            geometry = obj.Geometry
            self.assertTrue(geometry.Transform(scale))
            objects.append((geometry, old.Layers[obj.Attributes.LayerIndex].FullPath, obj.Attributes.Id))
        mm = self.draft(self.model(objects, units=rhino3dm.UnitSystem.Millimeters, name='mm.3dm'), project_id=FIXTURE['Id'])
        metres = self.draft(self.exported(name='m/model.3dm'))
        self.assertEqual([e['Id'] for e in metres['project']['Elements']], [e['Id'] for e in mm['project']['Elements']])
        for a, b in zip(metres['project']['Elements'], mm['project']['Elements']):
            self.assertClose(a['GeometryMetres'], b['GeometryMetres'])


class Cli(Base):
    def run_cli(self, *args, cwd=None):
        return subprocess.run([sys.executable, '-I', str(DRAFT), *args], capture_output=True, text=True, timeout=120, cwd=cwd)

    def test_cli_prints_the_draft_and_writes_nothing(self):
        source = self.model([(rectangle(0, 0, 4, 3), 'Spaces', oid(1))])
        before = sorted(os.listdir(self.dir))
        result = self.run_cli(str(source), '--project-id', sid(9), '--arc-segments', '4', cwd=str(self.dir))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(rd.dumps(rd.draft(source, project_id=sid(9), arc_segments=4)), result.stdout.strip())
        self.assertEqual(before, sorted(os.listdir(self.dir)))

    def test_cli_errors_are_one_json_line(self):
        bad = self.path('bad.3dm')
        bad.write_bytes(b'junk')
        for args, code in (((), 'usage'), ((str(bad),), 'rhino_read_failed'), ((str(bad), '--wall-height', 'x'), 'draft_options_invalid'),
                           ((str(bad), '--bogus', '1'), 'usage')):
            with self.subTest(args):
                result = self.run_cli(*args)
                self.assertEqual(1, result.returncode, result.stderr)
                self.assertEqual('', result.stdout)
                self.assertEqual(1, len(result.stderr.strip().splitlines()), result.stderr)
                self.assertEqual(code, json.loads(result.stderr)['error'])


class ReviewNonFinite(Base):
    def test_converter_refuses_non_finite_values_as_degenerate(self):
        for mode, value in (('div', 1000.0), ('div', 1.0), ('mul', 0.3048)):
            for bad in (math.nan, math.inf, -math.inf):
                with self.subTest((mode, bad)):
                    with self.assertRaises(rd._Unmapped) as ctx:
                        rd._Converter(mode, value).m(bad)
                    self.assertEqual('degenerate', ctx.exception.reason)

    def test_nan_and_inf_points_in_a_millimetre_model_are_degenerate(self):
        nan, inf = math.nan, math.inf
        objects = [(rhino3dm.Point(P3(0, 0, nan)), 'Levels', oid(1)), (rhino3dm.Point(P3(0, 0, inf)), 'Levels', oid(2)),
                   (rhino3dm.LineCurve(P3(0, 0, 0), P3(nan, 0, 0)), 'Walls', oid(3)), (rhino3dm.LineCurve(P3(inf, 0, 0), P3(1, 0, 0)), 'Walls', oid(4)),
                   (rectangle(0, 0, 4000, nan), 'Spaces', oid(5)), (rectangle(0, 0, inf, 3000), 'Spaces', oid(6))]
        model = self.build(objects, units=rhino3dm.UnitSystem.Millimeters)
        result = rd.draft_model(model, 'nan.3dm', project_id=sid(9))
        self.assertAccounted(model, result, 'degenerate')

    def test_arc_detection_rejects_overflowing_samples(self):
        class Interval:
            T0, T1 = 0.0, 1.0

        class Huge:
            Domain = Interval

            def PointAt(self, t):
                return P3(1e300 * math.cos(t), 1e300 * math.sin(t), 0)
        self.assertIsNone(rd._circular(Huge(), rd._Converter('div', 1.0)))

    def test_curves_with_fewer_than_two_points_are_degenerate(self):
        objects = [(rhino3dm.PolylineCurve([P3(1, 1, 0)]), 'Walls', oid(1)), (rhino3dm.PolylineCurve([P3(1, 1, 0)]), 'Spaces', oid(2))]
        model = self.build(objects)
        self.assertAccounted(model, rd.draft_model(model, 'short.3dm', project_id=sid(9)), 'degenerate')


# Builds one .3dm holding a block (instance) definition, in a fresh child process. rhino3dm 8.17 crashes (SIGSEGV)
# in InstanceDefinitions.Add once a model with a block definition has been written in the same process (CI probe:
# the 2nd or 3rd Add after a Write died in every variant; 40 Adds without Write, and 40 written models without
# blocks, did not). Building in the test process therefore crashed the suite intermittently. Each child does one
# Add and one Write, keeps every object alive and leaves with os._exit, so no native destructor runs either. The
# test process only reads the written files, through the adapter's normal path.
BLOCK_BUILDER = r"""
import os, sys, uuid
import rhino3dm
P3 = rhino3dm.Point3d
keep = []
for out in sys.argv[1:2]:
    model = rhino3dm.File3dm()
    model.Settings.ModelUnitSystem = rhino3dm.UnitSystem.Meters
    spaces, walls = rhino3dm.Layer(), rhino3dm.Layer()
    spaces.Name, walls.Name = 'Spaces', 'Walls'
    spaces_index, walls_index = model.Layers.Add(spaces), model.Layers.Add(walls)
    room = rhino3dm.PolylineCurve([P3(0, 0, 0), P3(4, 0, 0), P3(4, 3, 0), P3(0, 3, 0), P3(0, 0, 0)])
    room_attributes = rhino3dm.ObjectAttributes()
    room_attributes.LayerIndex = spaces_index
    room_attributes.Id = uuid.UUID(int=0x1001)
    model.Objects.Add(room, room_attributes)
    member = rhino3dm.LineCurve(P3(0, 0, 0), P3(1, 0, 0))
    member_attributes = rhino3dm.ObjectAttributes()
    member_attributes.LayerIndex = walls_index
    geometry, attributes = (member,), (member_attributes,)
    keep.append((model, spaces, walls, room, room_attributes, member, member_attributes, geometry, attributes))
    index = model.InstanceDefinitions.Add('door', '', '', '', P3(0, 0, 0), geometry, attributes)
    if index < 0 or not model.Write(out, 7):
        sys.stdout.write('build failed for ' + out + '\n')
        sys.stdout.flush()
        os._exit(3)
sys.stdout.flush()
os._exit(0)
"""


class ReviewBlocks(Base):
    def block_files(self, count):
        paths = [self.path(f'block{n}/block.3dm') for n in range(count)]
        for path in paths:
            built = subprocess.run([sys.executable, '-I', '-c', BLOCK_BUILDER, str(path)], capture_output=True, text=True, timeout=120)
            self.assertEqual(0, built.returncode, f'{path}: {built.stdout} {built.stderr}')
        return paths

    def members(self, path):
        model = rhino3dm.File3dm.Read(str(path))
        self.assertIsNotNone(model)
        members = [str(o.Attributes.Id) for o in model.Objects if o.Attributes.IsInstanceDefinitionObject]
        self.assertEqual(1, len(members), f'{path}: instance definition members after a write/read round trip')
        return members

    def assertBlockSkipped(self, path):
        members = self.members(path)
        result = rd.draft(path, project_id=sid(9))
        self.assertEqual([sid(1)], [p['key'] for p in result['proposals']])
        for member in members:
            self.assertEqual(['block_definition_member'], [u['reason'] for u in result['unmapped'] if u['object_id'] == member])
        self.assertEqual({'block_definition_member': 1}, result['summary']['unmapped_by_reason'])

    def test_block_definition_members_are_not_proposed(self):
        self.assertBlockSkipped(self.block_files(1)[0])

    def test_block_definition_construction_and_reading_are_stable(self):
        # Stability guard for the intermittent crash: 50 constructions (one per child), all read and drafted here.
        for path in self.block_files(50):
            with self.subTest(path.parent.name):
                self.assertBlockSkipped(path)


class ReviewCaps(Base):
    def test_max_proposals_stops_while_reading(self):
        source = self.model([(polyline([(0, 0), (1, 0), (2, 0), (3, 0), (4, 0), (5, 0)]), 'Walls', oid(1))])
        with self.assertRaises(br.RhinoAdapterError) as ctx:
            rd.draft(source, max_proposals=3)
        self.assertEqual('draft_limit', ctx.exception.code)
        self.assertIn('while reading', ctx.exception.detail)

    def test_unmapped_list_stays_bounded_and_keeps_the_smallest_entries(self):
        state = rd._Draft('x.3dm', rd._Converter('div', 1.0), rd._options({'max_unmapped': 3}))
        ids = [str(uuid.UUID(int=(n * 7919) % 1000)) for n in range(1, 101)]
        for object_id in ids:
            state.skip({'object_id': object_id, 'part': None, 'layer': 'Other', 'geometry_type': 'Point'}, 'layer_unmapped', '')
            self.assertLess(len(state.unmapped), 6)
        report = state.report()
        self.assertEqual(sorted(ids)[:3], [u['object_id'] for u in report[:3]])
        self.assertEqual('unmapped_limit', report[3]['reason'])
        self.assertIn('97 more', report[3]['detail'])
        self.assertEqual(100, state.unmapped_total)

    def test_capped_report_does_not_depend_on_object_order(self):
        objects = [(rhino3dm.Point(P3(n, 0, 0)), 'Other', oid(n)) for n in range(1, 12)]
        a = rd.dumps(rd.draft(self.model(objects, name='a/m.3dm'), max_unmapped=2, project_id=sid(9)))
        b = rd.dumps(rd.draft(self.model(list(reversed(objects)), name='b/m.3dm'), max_unmapped=2, project_id=sid(9)))
        self.assertEqual(a, b)


class ReviewOutlines(Base):
    def edges(self, corners, n, rotate):
        points = []
        for i in range(4):
            (ax, ay), (bx, by) = corners[i], corners[(i + 1) % 4]
            points += [(ax + (bx - ax) * k / n, ay + (by - ay) * k / n, 0.0) for k in range(n)]
        points = points[rotate:] + points[:rotate]
        return points + [points[0]]

    def test_collinear_vertex_removal_is_linear(self):
        import time
        points = self.edges([(0, 0), (4, 0), (4, 3), (0, 3)], 50000, 25000)
        started = time.perf_counter()
        corners = rd._corners(points)
        elapsed = time.perf_counter() - started
        self.assertEqual(sorted([(0.0, 0.0), (4.0, 0.0), (4.0, 3.0), (0.0, 3.0)]), sorted(corners))
        self.assertLess(elapsed, 1.0, f'{len(points)} vertices took {elapsed:.2f} s')

    def test_many_collinear_vertices_give_the_same_space(self):
        points = self.edges([(0, 0), (4, 0), (4, 3), (0, 3)], 500, 250)
        curve = rhino3dm.PolylineCurve([P3(x, y, z) for x, y, z in points])
        result = self.draft(self.model([(curve, 'Spaces', oid(1))]), max_vertices=5000)
        self.assertClose([0, 0, 4, 3], self.proposal(result, sid(1))['element']['GeometryMetres'])

    def test_crossed_outline_is_not_a_rectangle(self):
        with self.assertRaises(rd._Unmapped) as ctx:
            rd._rectangle([(0, 0, 0), (4, 3, 0), (4, 0, 0), (0, 3, 0), (0, 0, 0)])
        self.assertEqual('space_not_rectangle', ctx.exception.reason)
        self.assertUnmapped('space_not_rectangle', self.draft(self.model([(polyline([(0, 0), (4, 3), (4, 0), (0, 3), (0, 0)]), 'Spaces', oid(1))])), sid(1))

    def test_crossed_footprint_extrusion_is_not_a_wall(self):
        extrusion = rhino3dm.Extrusion()
        self.assertTrue(extrusion.SetPathAndUp(P3(0, 0, 0), P3(0, 0, 3), rhino3dm.Vector3d(0, 1, 0)))
        bowtie = rhino3dm.PolylineCurve([P3(-2, -0.1, 0), P3(2, 0.1, 0), P3(2, -0.1, 0), P3(-2, 0.1, 0), P3(-2, -0.1, 0)])
        if not extrusion.SetOuterProfile(bowtie, True):
            self.skipTest('rhino3dm refuses a crossed extrusion profile')
        self.assertUnmapped('wall_extrusion_unsupported', self.draft(self.model([(extrusion, 'Walls', oid(1))])), sid(1))

    def test_space_whose_snapped_size_crosses_the_writer_range_is_unmapped(self):
        source = self.model([(rectangle(99996.00000000001, 0, 1e5, 3), 'Spaces', oid(1))])
        self.assertUnmapped('out_of_range', self.draft(source), sid(1))


class ReviewArcs(Base):
    def test_circle_on_a_wall_layer_is_a_closed_chord_loop(self):
        result = self.draft(self.model([(arc_curve(rhino3dm.Circle(P3(1, 1, 0), 2.0)), 'Walls', oid(1))]), arc_segments=8)
        self.assertEqual(8, len(result['proposals']))
        walls = sorted(result['proposals'], key=lambda p: p['source']['part'])
        first = walls[0]['element']['GeometryMetres']
        self.assertChain(result, first[:2], first[:2])
        for proposal in walls:
            g = proposal['element']['GeometryMetres']
            self.assertAlmostEqual(2 * 2.0 * math.sin(math.pi / 8), math.hypot(g[2] - g[0], g[3] - g[1]), places=9)

    def test_clockwise_arc_keeps_its_direction(self):
        try:
            arc = rhino3dm.Arc(P3(2, 0, 0), P3(math.sqrt(2), -math.sqrt(2), 0), P3(0, -2, 0))
        except TypeError as error:
            self.skipTest(f'no three-point Arc constructor: {error}')
        result = self.draft(self.model([(arc_curve(arc), 'Walls', oid(1))]), arc_segments=3)
        self.assertEqual(3, len(result['proposals']))
        self.assertChain(result, (2, 0), (0, -2))
        for proposal in result['proposals']:
            g = proposal['element']['GeometryMetres']
            self.assertAlmostEqual(2.0, math.hypot(g[0], g[1]), places=9)

    def test_non_rational_quadratic_is_not_an_arc(self):
        curve = rhino3dm.NurbsCurve.Create(False, 2, [P3(2, 0, 0), P3(2, 2, 0), P3(0, 2, 0)])
        self.assertUnmapped('curve_unsupported', self.draft(self.model([(curve, 'Walls', oid(1))]), arc_segments=4), sid(1))

    def test_near_circular_ellipse_is_not_an_arc(self):
        # A circle stretched by 5e-6 along x: a rational NURBS ellipse that a loose arc test would accept.
        curve = arc_curve(rhino3dm.Circle(P3(0, 0, 0), 2.0))
        stretch = rhino3dm.Transform(1.0)
        stretch.M00 = 1.000005
        self.assertTrue(curve.Transform(stretch))
        self.assertAlmostEqual(2.00001, curve.PointAt(curve.Domain.T0).X, places=6)
        self.assertUnmapped('curve_unsupported', self.draft(self.model([(curve, 'Walls', oid(1))]), arc_segments=4), sid(1))


class ReviewIdentity(Base):
    def test_repeated_object_ids_are_all_unmapped(self):
        model = self.build([(rectangle(0, 0, 4, 3), 'Spaces', oid(1)), (rectangle(5, 0, 8, 3), 'Spaces', oid(1))])
        ids = [str(o.Attributes.Id) for o in model.Objects]
        if ids != [sid(1), sid(1)]:
            self.skipTest(f'rhino3dm re-identified the copy: {ids}')
        result = rd.draft_model(model, 'dup.3dm', project_id=sid(9))
        self.assertEqual([], result['proposals'])
        self.assertEqual(['duplicate_object_id'] * 2, [u['reason'] for u in result['unmapped']])

    def test_derived_segment_id_equal_to_an_object_id_unmaps_both(self):
        derived = uuid.UUID(rd._derived_id(sid(1), '0'))
        result = self.draft(self.model([(rhino3dm.LineCurve(P3(0, 0, 0), P3(4, 0, 0)), 'Walls', oid(1)), (rectangle(0, 0, 4, 3), 'Spaces', derived)]))
        self.assertEqual([], result['proposals'])
        self.assertEqual(['duplicate_id'], [u['reason'] for u in self.unmapped(result, sid(1), 0)])
        self.assertEqual(['duplicate_id'], [u['reason'] for u in self.unmapped(result, str(derived))])

    def test_synthesized_level_avoids_an_existing_id(self):
        project_id = sid(9)
        taken = uuid.UUID(rd._derived_id(project_id, 'level', repr(6.0)))
        result = self.draft(self.model([(rhino3dm.Point(P3(0, 0, 9.0)), 'Levels', taken), (rectangle(0, 0, 4, 3, 6.0), 'Spaces', oid(1))]), project_id=project_id)
        levels = {p['element']['Id']: p for p in result['proposals'] if p['kind'] == 'level'}
        self.assertEqual(2, len(levels))
        self.assertEqual([9.0], levels[str(taken)]['element']['GeometryMetres'])
        synthesized = [p for p in levels.values() if 'level-synthesized' in p['notes']]
        self.assertEqual([[6.0]], [p['element']['GeometryMetres'] for p in synthesized])
        self.assertEqual([synthesized[0]['element']['Id']], self.proposal(result, sid(1))['element']['Dependencies'])


class ReviewDependencies(Base):
    def two_levels(self):
        project = copy.deepcopy(FIXTURE)
        second = '00000000-0000-0000-0000-000000000015'
        project['Elements'].insert(1, {'Id': second, 'Kind': 'level', 'GeometryMetres': [3.5], 'Dependencies': [],
                                       'Provenance': {'Source': 'second level, same elevation', 'Approval': 'proposed'}})
        project['Elements'][3]['Dependencies'] = [second]  # wall ...12
        return project, second

    def test_writer_dependencies_choose_between_levels_at_one_elevation(self):
        project, second = self.two_levels()
        result = self.draft(self.exported(project))
        wall = self.proposal(result, '00000000-0000-0000-0000-000000000012')
        self.assertEqual([second], wall['element']['Dependencies'])
        self.assertIn('dependencies-declared', wall['notes'])

    def test_without_writer_strings_the_smallest_level_id_is_taken_and_noted(self):
        project, _ = self.two_levels()
        plain = self.copied(self.exported(project), lambda g, s, l, i: (g, l, i, {}))
        wall = self.proposal(self.draft(plain), '00000000-0000-0000-0000-000000000012')
        self.assertEqual(['00000000-0000-0000-0000-000000000010'], wall['element']['Dependencies'])
        self.assertIn('level-ambiguous', wall['notes'])

    def test_declared_dependencies_that_disagree_with_the_geometry_are_ignored(self):
        def lift(geometry, strings, layer, object_id):
            if str(object_id) == '00000000-0000-0000-0000-000000000011':
                self.assertTrue(geometry.Translate(rhino3dm.Vector3d(0, 0, 1.0)))
            return geometry, layer, object_id, strings
        result = self.draft(self.copied(self.exported(), lift), project_id=FIXTURE['Id'])
        wall = self.proposal(result, '00000000-0000-0000-0000-000000000011')
        self.assertIn('declared-dependencies-ignored', wall['notes'])
        level = [p for p in result['proposals'] if p['element']['Id'] == wall['element']['Dependencies'][0]][0]
        self.assertEqual([4.5], level['element']['GeometryMetres'])
        self.assertIn('level-synthesized', level['notes'])

    def test_roundtrip_bytes_depend_on_the_file_name_only_through_provenance(self):
        one = self.draft(self.exported(name='a/one.3dm'))['project']
        two = self.draft(self.exported(name='b/two.3dm'))['project']
        self.assertEqual({'rhino:one.3dm'}, {e['Provenance']['Source'] for e in one['Elements']})
        self.assertEqual({'rhino:two.3dm'}, {e['Provenance']['Source'] for e in two['Elements']})
        strip = lambda p: json.dumps([{k: v for k, v in e.items() if k != 'Provenance'} for e in p['Elements']])
        self.assertEqual(strip(one), strip(two))


class ReReviewErrors(Base):
    def patched(self, name, replacement):
        original = rd._Draft.__dict__[name]
        setattr(rd._Draft, name, replacement)
        self.addCleanup(setattr, rd._Draft, name, original)
        return original

    def test_programming_errors_propagate(self):
        def broken(self, geometry, source, inherited):
            a, b = (1, 2, 3)  # an unpack mismatch is a bug, not degenerate geometry
        self.patched('space', broken)
        with self.assertRaises(ValueError):
            rd.draft(self.model([(rectangle(0, 0, 4, 3), 'Spaces', oid(1))]))

    def test_arithmetic_errors_unmap_the_object(self):
        def overflow(self, geometry, source, inherited):
            raise OverflowError('math range error')
        self.patched('space', overflow)
        self.assertUnmapped('degenerate', rd.draft(self.model([(rectangle(0, 0, 4, 3), 'Spaces', oid(1))])), sid(1))

    def test_arithmetic_error_in_one_segment_unmaps_only_that_segment(self):
        original = rd._Draft.__dict__['check_wall'].__func__
        calls = []
        def flaky(values, base):
            calls.append(values)
            if len(calls) == 2:
                raise OverflowError('math range error')
            return original(values, base)
        self.patched('check_wall', staticmethod(flaky))
        result = rd.draft(self.model([(polyline([(0, 0), (1, 0), (2, 0), (3, 0)]), 'Walls', oid(1))]))
        self.assertEqual([0, 2], sorted(p['source']['part'] for p in result['proposals']))
        self.assertEqual([(1, 'degenerate')], [(u['part'], u['reason']) for u in result['unmapped']])

    def test_object_level_failure_rolls_back_its_proposals(self):
        original = rd._Draft.__dict__['wall']
        def fails_after_proposing(self, geometry, source, inherited):
            original(self, geometry, source, inherited)
            raise OverflowError('math range error')
        self.patched('wall', fails_after_proposing)
        result = rd.draft(self.model([(polyline([(0, 0), (1, 0), (2, 0)]), 'Walls', oid(1)), (rectangle(0, 0, 4, 3), 'Spaces', oid(2))]))
        self.assertEqual([sid(2)], [p['key'] for p in result['proposals']])
        self.assertUnmapped('degenerate', result, sid(1))


class ReReviewDeclared(Base):
    def variant(self, edit):
        project = copy.deepcopy(FIXTURE)
        edit({e['Id'][-2:]: e for e in project['Elements']}, project)
        return self.draft(self.exported(project))

    def test_repeated_declared_dependencies_are_kept_once(self):
        result = self.variant(lambda e, p: e['11'].update(Dependencies=[e['10']['Id'], e['10']['Id']]))
        wall = self.proposal(result, '00000000-0000-0000-0000-000000000011')
        self.assertEqual(['00000000-0000-0000-0000-000000000010'], wall['element']['Dependencies'])
        self.assertIn('dependencies-declared', wall['notes'])

    def test_filtered_declared_dependencies_are_not_noted_as_declared(self):
        result = self.variant(lambda e, p: e['12'].update(Dependencies=[e['10']['Id'], e['11']['Id']]))
        wall = self.proposal(result, '00000000-0000-0000-0000-000000000012')
        self.assertEqual(['00000000-0000-0000-0000-000000000010'], wall['element']['Dependencies'])
        self.assertNotIn('dependencies-declared', wall['notes'])
        self.assertIn('declared-dependencies-filtered', wall['notes'])

    def test_all_declared_dependencies_dropped_falls_back_to_geometry(self):
        result = self.variant(lambda e, p: e['14'].update(Dependencies=[e['11']['Id']]))
        wall = self.proposal(result, '00000000-0000-0000-0000-000000000014')
        self.assertEqual([], wall['element']['Dependencies'])
        self.assertNotIn('dependencies-declared', wall['notes'])
        self.assertIn('declared-dependencies-filtered', wall['notes'])

    def test_every_declared_level_must_match_the_base(self):
        upper = '00000000-0000-0000-0000-000000000015'
        def edit(e, p):
            p['Elements'].insert(1, {'Id': upper, 'Kind': 'level', 'GeometryMetres': [5.0], 'Dependencies': [],
                                     'Provenance': {'Source': 'upper level', 'Approval': 'proposed'}})
            e['12'].update(Dependencies=[e['10']['Id'], upper])
        result = self.variant(edit)
        wall = self.proposal(result, '00000000-0000-0000-0000-000000000012')
        self.assertEqual(['00000000-0000-0000-0000-000000000010'], wall['element']['Dependencies'])
        self.assertIn('declared-dependencies-ignored', wall['notes'])
        self.assertNotIn('dependencies-declared', wall['notes'])


# ---------------------------------------------------------------- P8-09 doors and windows

OPENINGS = json.loads((ROOT / 'tests/rhino/fixtures/openings.json').read_text(encoding='utf-8'))


def fid(n):
    return f'00000000-0000-0000-0000-0000000000{n:02d}'


def box(ax, ay, bx, by, z, height, thickness):
    """A writer-style opening (or wall) box: horizontal path on the axis, centred vertical section."""
    return br._wall([ax, ay, bx, by, height, thickness], z)


def line(ax, ay, bx, by, z=0.0):
    return rhino3dm.LineCurve(P3(ax, ay, z), P3(bx, by, z))


class OpeningsRoundtrip(Base):
    def test_writer_openings_draft_back_on_their_hosts(self):
        source = self.exported(OPENINGS)
        result = self.draft(source)
        exact = {e['Id']: e for e in json.loads(br.dumps(br.import_project(source)))['Elements']}
        self.assertEqual(sorted(exact), sorted(e['Id'] for e in result['project']['Elements']))
        self.assertEqual([], result['unmapped'])
        for element in result['project']['Elements']:
            want = exact[element['Id']]
            self.assertEqual(want['Kind'], element['Kind'])
            self.assertEqual(want['Dependencies'], element['Dependencies'], element['Id'])
            self.assertEqual(json.dumps(want['GeometryMetres']), json.dumps(element['GeometryMetres']), element['Id'])
        for n in (31, 32, 33, 34, 35, 36):
            proposal = self.proposal(result, fid(n))
            self.assertEqual(0.9, proposal['confidence'])
            self.assertIn('dependencies-declared', proposal['notes'])
            self.assertEqual({'door': 'BIM::Doors', 'window': 'BIM::Windows'}[proposal['kind']], proposal['source']['layer'])
            self.assertEqual('Extrusion', proposal['source']['geometry_type'])

    def test_summary_counts_doors_and_windows(self):
        self.assertEqual({'levels': 2, 'walls': 5, 'doors': 2, 'windows': 4, 'spaces': 1, 'unmapped': 0, 'unmapped_by_reason': {}},
                         self.draft(self.exported(OPENINGS))['summary'])

    def test_proposals_are_ordered_level_wall_door_window_space(self):
        kinds = [p['kind'] for p in self.draft(self.exported(OPENINGS))['proposals']]
        self.assertEqual(['level'] * 2 + ['wall'] * 5 + ['door'] * 2 + ['window'] * 4 + ['space'], kinds)

    def test_draft_with_openings_exports_and_drafts_to_the_same_bytes(self):
        first = self.draft(self.exported(OPENINGS, name='one/model.3dm'))
        second = self.draft(self.exported(first['project'], name='two/model.3dm'))
        self.assertEqual(br.dumps(first['project']), br.dumps(second['project']))
        self.assertEqual(json.dumps(first['proposals']), json.dumps(second['proposals']))

    def test_millimetre_writer_output_with_openings(self):
        scale = rhino3dm.Transform.Scale(P3(0, 0, 0), 1000.0)
        old = rhino3dm.File3dm.Read(str(self.exported(OPENINGS)))
        objects = []
        for obj in old.Objects:
            geometry = obj.Geometry
            self.assertTrue(geometry.Transform(scale))
            # The writer's user strings come along, so declared hosts and space-to-wall links are kept as in metres.
            objects.append((geometry, old.Layers[obj.Attributes.LayerIndex].FullPath, obj.Attributes.Id, dict(obj.Attributes.GetUserStrings())))
        mm = self.draft(self.model(objects, units=rhino3dm.UnitSystem.Millimeters, name='mm.3dm'), project_id=OPENINGS['Id'])
        metres = self.draft(self.exported(OPENINGS, name='m/model.3dm'))
        self.assertEqual([e['Id'] for e in metres['project']['Elements']], [e['Id'] for e in mm['project']['Elements']])
        for a, b in zip(metres['project']['Elements'], mm['project']['Elements']):
            self.assertEqual(a['Dependencies'], b['Dependencies'])
            self.assertClose(a['GeometryMetres'], b['GeometryMetres'])

    def test_declared_host_that_does_not_hold_the_opening_is_ignored(self):
        def rehost(geometry, strings, layer, object_id):
            if str(object_id) == fid(31):
                strings = {**strings, 'bim.dependencies': json.dumps([fid(10), fid(22)])}
            return geometry, layer, object_id, strings
        result = self.draft(self.copied(self.exported(OPENINGS), rehost), project_id=OPENINGS['Id'])
        door = self.proposal(result, fid(31))
        self.assertEqual([fid(10), fid(21)], door['element']['Dependencies'])
        self.assertIn('declared-host-ignored', door['notes'])
        self.assertNotIn('dependencies-declared', door['notes'])

    def test_openings_without_writer_strings_find_their_hosts(self):
        plain = self.copied(self.exported(OPENINGS), lambda g, s, l, i: (g, l, i, {}))
        result = self.draft(plain, project_id=OPENINGS['Id'])
        for n, host in ((31, 21), (32, 21), (33, 23), (34, 22), (35, 25), (36, 25)):
            proposal = self.proposal(result, fid(n))
            self.assertEqual(fid(host), proposal['element']['Dependencies'][1], n)
            self.assertIn('host-inferred', proposal['notes'])
            self.assertEqual(elements_of(OPENINGS)[fid(n)]['GeometryMetres'], proposal['element']['GeometryMetres'])


def elements_of(project):
    return {e['Id']: e for e in project['Elements']}


class Openings(Base):
    def test_door_and_window_layer_names(self):
        for layer, kind in (('Doors', 'door'), ('door', 'door'), ('Puertas', 'door'), ('BIM::Doors::Entrance', 'door'),
                            ('Windows', 'window'), ('WINDOW', 'window'), ('ventanas', 'window')):
            with self.subTest(layer):
                result = self.draft(self.model([(line(0, 0, 5, 0), 'Walls', oid(1)), (box(1, 0, 2, 0, 0.0 if kind == 'door' else 0.9, 1.0, 0.2), layer, oid(2))],
                                               name=f'{layer}.3dm'.replace(':', '_')))
                self.assertEqual([kind], [p['kind'] for p in result['proposals'] if p['key'] == sid(2)])

    def test_free_drawn_door_on_a_centreline_wall(self):
        result = self.draft(self.model([(line(0, 0, 5, 0), 'Walls', oid(1)), (box(1, 0, 1.9, 0, 0.0, 2.1, 0.2), 'Doors', oid(2))]))
        wall_id = rd._derived_id(sid(1), '0')
        door = self.proposal(result, sid(2))
        self.assertEqual([1, 0, 1.9, 0, 0, 2.1], door['element']['GeometryMetres'])
        level_id = door['element']['Dependencies'][0]
        self.assertEqual([level_id, wall_id], door['element']['Dependencies'])
        self.assertEqual(0.9, door['confidence'])
        self.assertIn('host-inferred', door['notes'])
        wall = self.proposal(result, f'{sid(1)}:0')
        self.assertEqual([level_id], wall['element']['Dependencies'])
        self.assertIn('level-added-for-opening', wall['notes'])
        level = [p for p in result['proposals'] if p['element']['Id'] == level_id][0]
        self.assertEqual([0], level['element']['GeometryMetres'])
        self.assertIn('level-synthesized', level['notes'])
        self.assertEqual(1, result['summary']['doors'])

    def test_free_drawn_openings_roundtrip_through_the_writer(self):
        source = self.model([(line(0, 0, 5, 0), 'Walls', oid(1)), (box(1, 0, 1.9, 0, 0.0, 2.1, 0.2), 'Doors', oid(2)),
                             (box(3, 0, 4, 0, 0.9, 1.2, 0.2), 'Windows', oid(3)), (rhino3dm.Point(P3(0, 0, 3)), 'Levels', oid(4)),
                             (line(0, 0, 0, 4, 3.0), 'Walls', oid(5)), (box(0, 1, 0, 2, 3.8, 1.0, 0.2), 'Windows', oid(6))], name='one/model.3dm')
        first = self.draft(source)
        self.assertEqual({'door': 1, 'window': 2}, {k: first['summary'][k + 's'] for k in ('door', 'window')})
        upper = self.proposal(first, sid(6))
        self.assertEqual([sid(4), rd._derived_id(sid(5), '0')], upper['element']['Dependencies'])
        self.assertClose([0.8, 1.0], upper['element']['GeometryMetres'][4:])
        second = self.draft(self.exported(first['project'], name='two/model.3dm'))
        self.assertEqual(br.dumps(first['project']), br.dumps(second['project']))

    def test_footprint_drawn_window_is_projected_onto_the_host_axis(self):
        result = self.draft(self.model([(vertical_extrusion(2.5, 0, 0, 3.0, 5.0, 0.3), 'Walls', oid(1)),
                                        (vertical_extrusion(3, 0.05, 0.9, 1.2, 1.0, 0.2), 'Windows', oid(2))]))
        window = self.proposal(result, sid(2))
        g = window['element']['GeometryMetres']
        self.assertEndpoints([(2.5, 0), (3.5, 0)], g)
        self.assertClose([0.9, 1.2], g[4:])
        self.assertIn('footprint-rectangle', window['notes'])
        self.assertIn('projected-to-host-axis', window['notes'])
        self.assertAlmostEqual(0.8 * 0.9, window['confidence'], places=9)
        self.assertEqual(sid(1), window['element']['Dependencies'][1])
        # The ends follow the host direction (the writer's rule).
        host = self.proposal(result, sid(1))['element']['GeometryMetres']
        direction = (host[2] - host[0]) * (g[2] - g[0]) + (host[3] - host[1]) * (g[3] - g[1])
        self.assertGreater(direction, 0)

    def test_opening_against_the_host_direction_is_reordered(self):
        result = self.draft(self.model([(line(0, 0, 5, 0), 'Walls', oid(1)), (box(3, 0, 2, 0, 1.0, 1.0, 0.2), 'Windows', oid(2))]))
        window = self.proposal(result, sid(2))
        self.assertEqual([2, 0, 3, 0, 1, 1], window['element']['GeometryMetres'])
        self.assertIn('reversed-to-host', window['notes'])

    def test_openings_without_a_host_are_unmapped(self):
        objects = [(line(0, 0, 5, 0), 'Walls', oid(1)),
                   (box(10, 10, 11, 10, 0.0, 2.1, 0.2), 'Doors', oid(2)),      # far from any wall
                   (box(1, 0.5, 2, 0.5, 0.0, 2.1, 0.2), 'Doors', oid(3)),      # beside the wall, outside its thickness
                   (box(1, 0, 2, 0, 2.0, 1.0, 0.2), 'Windows', oid(4)),        # head above the assumed 2.7 m wall top
                   (box(4.5, 0, 5.5, 0, 1.0, 1.0, 0.2), 'Windows', oid(5)),    # past the wall end
                   (box(1, 0, 2, 0.5, 1.0, 1.0, 0.2), 'Windows', oid(6))]      # across the wall, not along it
        result = self.draft(self.model(objects))
        for n in (2, 3, 4, 5, 6):
            self.assertUnmapped('opening_host_missing', result, sid(n))

    def test_door_above_the_floor_is_unmapped(self):
        result = self.draft(self.model([(line(0, 0, 5, 0), 'Walls', oid(1)), (box(1, 0, 2, 0, 0.5, 2.0, 0.2), 'Doors', oid(2))]))
        self.assertUnmapped('door_sill_not_zero', result, sid(2))
        wall = self.proposal(result, f'{sid(1)}:0')
        self.assertEqual([], wall['element']['Dependencies'])
        self.assertNotIn('level-added-for-opening', wall['notes'])

    def test_window_on_a_level_takes_the_host_level_and_its_sill(self):
        result = self.draft(self.model([(rhino3dm.Point(P3(0, 0, 3)), 'Levels', oid(1)), (line(0, 0, 5, 0, 3.0), 'Walls', oid(2)),
                                        (box(1, 0, 2.2, 0, 3.9, 1.2, 0.2), 'Windows', oid(3))]))
        window = self.proposal(result, sid(3))
        self.assertEqual([sid(1), rd._derived_id(sid(2), '0')], window['element']['Dependencies'])
        self.assertClose([1, 0, 2.2, 0, 0.9, 1.2], window['element']['GeometryMetres'])

    def test_ambiguous_host_takes_the_smallest_id_and_is_noted(self):
        result = self.draft(self.model([(line(0, 0, 5, 0), 'Walls', oid(1)), (line(0, 0, 5, 0), 'Walls', oid(2)),
                                        (box(1, 0, 2, 0, 1.0, 1.0, 0.2), 'Windows', oid(3))]))
        window = self.proposal(result, sid(3))
        self.assertEqual(min(rd._derived_id(sid(1), '0'), rd._derived_id(sid(2), '0')), window['element']['Dependencies'][1])
        self.assertIn('host-ambiguous', window['notes'])

    def test_exact_host_is_preferred_over_a_projection(self):
        result = self.draft(self.model([(line(0, 0.05, 5, 0.05), 'Walls', oid(1)), (line(0, 0, 5, 0), 'Walls', oid(2)),
                                        (box(1, 0, 2, 0, 1.0, 1.0, 0.2), 'Windows', oid(3))]))
        window = self.proposal(result, sid(3))
        self.assertEqual(rd._derived_id(sid(2), '0'), window['element']['Dependencies'][1])
        self.assertNotIn('projected-to-host-axis', window['notes'])
        self.assertIn('host-ambiguous', window['notes'])

    def test_other_geometry_on_an_opening_layer(self):
        objects = [(line(0, 0, 5, 0), 'Walls', oid(1)), (line(1, 0, 2, 0), 'Doors', oid(2)), (rhino3dm.Point(P3(1, 0, 0)), 'Windows', oid(3)),
                   (vertical_extrusion(1, 0, 0, 2.1, 0.2, 0.2), 'Doors', oid(4))]
        result = self.draft(self.model(objects))
        self.assertUnmapped('geometry_unsupported', result, sid(2))
        self.assertUnmapped('geometry_unsupported', result, sid(3))
        self.assertUnmapped('opening_footprint_ambiguous', result, sid(4))

    def test_object_order_does_not_change_the_bytes(self):
        objects = [(line(0, 0, 5, 0), 'Walls', oid(1)), (line(0, 0, 5, 0), 'Walls', oid(2)), (box(1, 0, 2, 0, 0.0, 2.0, 0.2), 'Doors', oid(3)),
                   (box(3, 0, 4, 0, 1.0, 1.0, 0.2), 'Windows', oid(4)), (box(9, 9, 10, 9, 1.0, 1.0, 0.2), 'Windows', oid(5))]
        a = self.model(objects, name='a/model.3dm')
        b = self.model(list(reversed(objects)), name='b/model.3dm')
        self.assertEqual(rd.dumps(rd.draft(a, project_id=sid(9))), rd.dumps(rd.draft(b, project_id=sid(9))))



if __name__ == '__main__':
    unittest.main()
