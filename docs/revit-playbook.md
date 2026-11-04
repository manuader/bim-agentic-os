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
- **Template gaps**: the default metric template has no ceramic tile or stone cladding materials and no wood-clad
  wall types; creating materials / wall types with layers is a pending capability.
- **Saving**: there is no save command yet; the owner saves with *Guardar como*. Pending capability.

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

## Pending capabilities found in real use
- `wall.set_profile` (gable walls on 2025.2) — in progress.
- Open an owner project (not a fixture copy) with the command pipe — in progress (`scripts/owner-open-project.ps1`).
- Create materials (appearance, color, texture) and wall/floor types with layers (stone, wood cladding, sheet metal).
- Curved roofs (extrusion roofs) for barrel/curved sheet-metal roofs.
- Document save / save-as to an enabled folder.
- Model in-place / direct shapes for site-specific elements (a boulder kept inside the building).
