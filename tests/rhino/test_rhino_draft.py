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
        """objects: (geometry, layer full path, object uuid or None). Parent layers are created as needed."""
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

        for geometry, layer_path, object_id in objects:
            attributes = rhino3dm.ObjectAttributes()
            attributes.LayerIndex = layer(layer_path)
            if object_id is not None:
                attributes.Id = object_id
            model.Objects.Add(geometry, attributes)
        out = self.path(name)
        self.assertTrue(model.Write(str(out), 7))
        return out

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
        self.assertEqual({'levels': 1, 'walls': 4, 'spaces': 1, 'unmapped': 3, 'unmapped_by_reason': {'layer_unmapped': 3}}, result['summary'])


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
        arc = rhino3dm.ArcCurve(rhino3dm.Arc(P3(0, 0, 0), 2.0, math.pi / 2))
        source = self.model([(arc, 'Walls', oid(1))])
        self.assertUnmapped('arc_not_approximated', self.draft(source), sid(1))
        result = self.draft(source, arc_segments=4)
        self.assertEqual(4, len(result['proposals']))
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
        circle = rhino3dm.ArcCurve(rhino3dm.Circle(P3(0, 0, 0), 2.0))
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
            levels = [d for d in want['Dependencies'] if d == '00000000-0000-0000-0000-000000000010']
            self.assertEqual(levels, have['Dependencies'], want['Id'])
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


if __name__ == '__main__':
    unittest.main()
