# Revit playbook — oficio aprendido trabajando con el dueño

Read this before driving Revit for ANY project (owner chat, project chats, relays). Every time the owner corrects
how something is done in Revit, or a command hits a Revit limit, add a line here (date, what, how) and, when it needs
code, a task on the board. Goal: each lesson becomes a tested command usable in every future project.

## Working rules
- Writes only on documents under `.bim/native-runs` (test copies) or a folder in `.bim/revit-enabled-roots.txt`
  (owner-enabled projects; 2026-11-03: `C:\Users\ezead\OneDrive\Documentos\ARQ.ARG`). Never touch any other
  running Revit (Casa Ángel).
- Keep projects separate: design work happens in a project chat; capability gaps found there are logged here and
  on the board, not designed around silently.
- Query before writing: levels, wall/floor/roof types, materials, loaded family symbols. Names differ per template
  (e.g. "Generic - 200mm Masonry" does not exist in the default metric template; use `query.wall_types`).

## Lessons (2026-11-03, cabin "Zona Sur")
- **Gable end walls**: in the UI the owner closes them with *Attach Top/Base* to the roof. API `Wall.AddAttachment`
  only exists from Revit 2025.3; the owner has 25.2 → use `wall.set_profile` (edit the wall's elevation profile:
  rectangle + triangle up to the ridge) or ask the owner to attach by hand. Command `wall.attach` exists for 2025.3+.
- **Gable roof**: `roof.create_footprint` with `slope_deg` and `sloped_edges` = the two long edges. Base offset is at
  the footprint (eave) line: base = wall top − overhang × tan(slope). Layered roof: `roof_type.create`
  (finish1 → membrane 0 mm → insulation → structure); the core must be the structure layer(s).
- **Sloped beams / rafters**: `structural_beam.create` on a level, then `element.set_parameter`
  `STRUCTURAL_BEAM_END0_ELEVATION` / `END1_ELEVATION` (start/end level offsets). Revit auto-joins rafters to eave beams.
- **Window bands**: set the window *type* Height/Width with `element.set_parameter` on the type UniqueId (e.g. 1500×400
  for a 2.00–2.40 m clerestory); adjacent windows touching = centres one width apart.
- **Low walls that should not split rooms** (benches): set `WALL_ATTR_ROOM_BOUNDING` = No.
- **Families**: `family.load` from the Revit library (`C:\ProgramData\Autodesk\RVT 2025\Libraries\Spanish\...`:
  `Pilares estructurales\Madera\Madera-Pilar.rfa`, `Armazón estructural\Madera\Madera.rfa`, `Vegetación\RPC Árbol -
  Conífera.rfa`, `Vegetación\Árbol en forma de elipse 3D.rfa`, `Vegetación\RPC Arbusto.rfa`) or from
  `<repo>/.bim/families` (manufacturer files the owner downloads, e.g. DOMOFOCUS.rfa = specialty equipment, level based).
- **Terrain**: `toposolid.create` — Revit keeps a flat bottom at level − type thickness (300 mm for "Toposolid 1");
  keep every point above that, or create the terrain on a lower level. Boulders = small 10-point toposolids.
  Trails = `toposolid.create_subdivision` with a material (no gravel in the template: "Earth").
- **Trees on slopes**: `family_instance.place` with `offset_mm` = terrain height at that point.
- **Template gaps**: the default metric template has no ceramic tile, stone, dark metal or wood cladding materials and
  no wood-clad wall types. Use `material.create` (duplicate a close material with `base_material`, e.g. "Metal Deck" or
  "Softwood, Lumber", so it keeps a render appearance; set `color` for shaded views; pick stone/wood patterns with
  `query.fill_patterns`), `material.set` to darken/adjust, then `wall_type.create` / `floor_type.create` (structure =
  core; `thermal_air` = Revit's Thermal/Air Layer) and `element.change_type` to switch existing walls/floors/roofs.
  Realistic views still show the base material's appearance asset (textures are not authored by the commands).
- **Saving**: `document.save` (2026-11-04, chat-export-save) saves the open document to its own path when it is under an
  enabled folder (not workshared, cloud, read-only or never saved); the first save of a new project is still *Guardar
  como* in Revit. Enabling a folder in `.bim/revit-enabled-roots.txt` no longer needs a Claude restart.
- **Saving a test copy**: `document.save_copy {path}` (P1-08) saves the open `BIM-Agentic-Test*.rvt` test copy as a new
  `BIM-Agentic-Test*.rvt` inside `<repo>/.bim/native-runs` (existing folder, never over an existing file, not a
  `*.NNNN.rvt` backup name; real projects, even owner-enabled ones, are refused) and returns its path and sha256.
  Revit's Save As re-points the open document: afterwards it IS the copy, so send later requests with the returned
  path; a retry is refused `target_exists`. Not run in an owner session yet.
- **Save As of a real project** (`document.save_as {path}`, P9-08): saves the open document as a NEW `.rvt` (existing
  folder, never over an existing file): an owner-enabled project into a folder of `.bim/revit-enabled-roots.txt` or
  `.bim/native-runs`; a test copy only inside `.bim/native-runs` (`outside_copies_root` otherwise). At most 249
  characters (room for Revit's backup names). Give a plain drive path with backslashes (`C:\...\Cabaña v2.rvt`): UNC, relative or `/`
  paths, `..`, device names (`CON`, `NUL`, ...), `:` streams, 8.3 short names, `.rfa`/`.rte` and `*.NNNN.rvt` backup
  names are refused `args_invalid`/`save_as_refused` before Revit is touched. Not for workshared, cloud or family
  documents. Afterwards the open document IS the new file (reply: `path`, `previous_path`, sha256); send later
  requests with the new path. The check after the save (links in parent folders resolved, link guard re-walked) only
  DETECTS a file written outside the enabled roots, it cannot prevent it: on that failure close the document in Revit
  and do not continue. Not run in an owner session yet; unverified there: relative links (Revit links, CAD imports by
  relative path) may stop resolving after Save As to another folder.
- **Reviewing from the phone**: `view.export_image` (a 3D view, plan, section, elevation or sheet; `pixel_size` 1600 by
  default; optional `visual_style` realistic/shaded/hidden_line/consistent_colors, rolled back after the export) writes a
  PNG/JPG under `<repo>/.bim/exports/<utc>/` and returns its absolute path, size and sha256; nothing in the model changes.

## Lessons (2026-11-04, cabin redesign on the real project)
- **Change a wall/roof type** without a dedicated command: `element.set_parameter` `ELEM_TYPE_PARAM` = the new type's
  UniqueId (works for walls and roofs; Revit keeps hosted openings).
- **Curtain wall between two walls**: Revit snaps the new wall's ends to the joined walls' location lines, so ask for
  the endpoints on those lines (not the inner faces) or the readback rolls back.
- **Gable triangle above a glass wall**: create a basic wall with `base_offset_mm` = eave height, set its height so its
  top equals the ridge (Revit keeps the bounding box until the height changes), then `wall.set_profile` with the
  triangle `[[0,eave],[L,eave],[L/2,ridge]]`.
- **Foundation wall types** ("Foundation - 300mm Concrete") are structural by definition: pass `structural: true`.
- **Plinth (zócalo) of concrete**: 300 mm foundation walls just outside the stone walls, base −250, height 650.
- **Porch carved into the mass (sustracción)**: delete the end wall (dry run → confirm dependents), set back a glass
  wall under the existing roof overhang, keep the long stone walls as blind porch sides.

- **Profiled wall joined at corners** (`wall.set_profile`): a triangle `[[0,3000],[4570.66,3000],[0,6151]]` whose high
  point sits at a wall end was refused ("bounding box top is 6233.28 mm above the base level, expected 6151"): the corner
  join extends the wall past u 0 along the sloped top. Workaround that worked: a 300 mm flat at the high end
  (`[[0,3000],[4570.66,3000],[300,6151],[0,6151]]`). Fixed in chat-section-profile-fixes: the readback still compares the
  sketch outline within 1 mm, and the box top may now exceed the profile by at most wall width × slope of a top edge
  that reaches a wall end (plus 25 mm); flat ends and gables with a mid-wall ridge get no allowance.
- **Diagonal sections** (`view.create_section`): every section was created looking to the RIGHT of start → end, so the
  readback refused it ("section right direction is (-0.707,-0.707,0), expected (0.707,0.707,0)"; reversing the line gave
  the mirror). Revit takes the view direction from the section box `Transform.BasisZ` (the looking direction, into the
  model) and computes right = BasisZ × BasisY; `View.ViewDirection` then points toward the viewer. Fixed in
  chat-section-profile-fixes (BasisZ = left of the line, cut plane at Min.Z 0, far clip at Max.Z = depth); axis-aligned
  sections had never run in Revit either.
- **3D views from another side** (`view.create_3d`): the default isometric looks from the south-east, which shows a
  NW–SE ridge edge-on. Pass `view_from` (ne, nw, se, sw, n, s, e, w, top) or `eye_direction` [x, y, z] (from the model
  toward the eye); readback checks forward and up and reports `eye_direction`. Convention: `ForwardDirection` points
  from the eye toward the model, so `ne` = eye north-east and above, looking south-west (you see the N and E faces).
  Presets use project north (+Y), not true north. Open: on the owner's model the default view (forward
  (-0.577, 0.577, -0.577), i.e. eye at the SE in project coordinates) looked "from the NW"; check the first `view_from`
  export there (suspect a rotated True North / plan orientation) before trusting the preset names.
- **Wall exterior side** (`wall.create`): in Revit 2025.2 a basic wall drawn start → end puts its EXTERIOR face on the
  LEFT of the direction of travel. A counter-clockwise loop therefore ends up with the exterior inside: draw outer
  walls clockwise, pass `flip: true` (Wall.Flip after creation; readback reports `flipped` and `exterior_side`), or use a
  type with mirrored layers.
- **Deleting curtain walls and profiled walls/roofs** (`element.delete`): refused because the dependent check asked
  Revit's `CanDeleteElement` of the target's own sub-elements (curtain grid ...896, sketch ...904). Fixed in
  chat-section-profile-fixes: sub-elements a target owns (curtain grid lines, panels, mullions, its sketch with its plane
  and lines, internal elements without a category) are still listed in the dry run but skip `CanDeleteElement`; foreign
  dependents that cannot be deleted still refuse.
- **Layered floor types** (`floor_type.create`): a build-up [finish1 lenga 25, substrate plywood 20, structure concrete
  150, membrane 0, insulation rigid 100] from "Generic Floor - 400mm" was refused by `SetCompoundStructure`: "Input
  compound structure has wrong EndCap condition for this element type" (`CreateSimpleCompoundStructure` starts with a
  wall end cap; the API says floors and roofs must use `EndCapCondition.NoEndCap`). Fixed in chat-floor-fixes: wall,
  floor and roof types share one builder; floors and roofs get NoEndCap and keep the base type's opening wrapping, walls
  keep the base wall type's conditions. Insulation above the substrate is refused by Revit as BadShellOrder (correct).
- **Floor offset over a toposolid** (`element.set_parameter` FLOOR_HEIGHTABOVELEVEL_PARAM 600) was refused: "the change
  would delete 1 element(s) (ids 215987)", an internal element with no category (probably a join/warning helper with
  the toposolid). Fixed in chat-floor-fixes: run a dry run, read `deleted` (UniqueId, id, category, class), and repeat
  the real change with `confirm_deletions` = those UniqueIds; unconfirmed deletions are still refused.
- **Deleting a curtain wall with pinned panels** (`element.delete` ...00034a49): refused because its own panel ...00034a4c
  is pinned (Revit pins the panels and mullions its type's grid layout places). Fixed in chat-floor-fixes: pinned grid
  lines, panels and mullions of a delete target no longer refuse it; a pinned target, foreign pinned dependents and
  grouped grid members still do. The API cannot tell a layout pin from a user's pin on a panel of the wall being deleted.

## Pending capabilities found in real use
- `wall.set_profile` (gable walls on 2025.2) — in progress.
- Open an owner project (not a fixture copy) with the command pipe — in progress (`scripts/owner-open-project.ps1`).
- Materials (color, patterns, duplicate-for-appearance), wall/floor types with layers and element.change_type — added
  (chat-materials-types, unit-tested, executor compiled only); authoring appearance assets/textures is still pending.
- Curved roofs (extrusion roofs) for barrel/curved sheet-metal roofs: added as `roof.create_extrusion` (P9-07,
  unit-tested, executor compiled only): plane = plan line, profile in [u, z] above the level, extruded to the line's
  left. Barrel over 6000 x 10000 with 300 overhangs: plane [0,0]->[6000,0], arc [-300,2700]->[6300,2700] through
  [3000,3800], extrusion -300 to 10300.
- Document save: added (chat-export-save, unit-tested, executor compiled only). Save as a new test copy under
  `.bim/native-runs`: added (P1-08 `document.save_copy`, unit-tested, executor compiled only); Save As of real
  projects into enabled folders: added (P9-08 `document.save_as`, unit-tested, executor compiled only).
- View image export for phone review: added (chat-export-save, unit-tested, executor compiled only).
- Model in-place / direct shapes for site-specific elements (a boulder kept inside the building): added as
  `direct_shape.create` (P9-09, unit-tested, executor compiled only): category site/planting/entourage/topography/
  generic_model, shape = extrusion (closed plan polygon of 3-256 [x, y] mm vertices on a level + `base_offset_mm`,
  `height_mm`) or box (`min`/`max` [x, y, z] mm, z as project elevation), optional `material` and `name`. Boulder
  1200 x 800 x 900 on Level 1: `{category: site, shape: {kind: extrusion, level: Level 1, outline:
  [[0,0],[1200,0],[1200,800],[0,800]], height_mm: 900}}`. Faceted rock shapes need several boxes/extrusions for now.
