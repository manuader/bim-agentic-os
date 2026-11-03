# glTF 2.0 export (Blender interchange, no Blender)

`Bim.Gltf` converts a compiled BIM-IR `Project` to glTF 2.0: a binary `.glb` (`GltfExporter.ExportGlb`, `GltfExporter.WriteGlb`) or a self-contained `.gltf` whose buffer is an embedded base64 `data:` URI (`GltfExporter.ExportGltf`). It is BCL-only and references `core/Bim.Core` for the BIM-IR records and validation. Blender is not installed or needed to produce the files; Blender imports glTF 2.0 natively. The BIM-IR schema is unchanged.

Regressions: `dotnet run --project tests/gltf --configuration Release` (registered in `scripts/verify.ps1`, so CI runs it on Linux and Windows).

## Import into Blender

File → Import → glTF 2.0 (.glb/.gltf), pick the file. The importer converts glTF Y-up back to its Z-up, so BIM X/Y/Z become Blender X/Y/Z in metres. Element identity shows on each object under Object Properties → Custom Properties (`bim_id`, `kind`, `level`; `type` is null and Blender may omit it). The project root object carries `bim_project_id`, `revision`, `schema`.

## Mapping

- Units are metres. BIM Z-up maps to glTF Y-up as (x, y, z) → (x, z, −y), a proper rotation, so winding stays counter-clockwise.
- One root node `project <id>` (extras `schema` = `bim-agentic-os.gltf/1`, `bim_project_id`, `revision`, `units` = `m`, `up` = `Y`). Its children are one node per element in element order, named `<kind> <id>`, with extras `{bim_id, kind, type, level}`. BIM-IR carries no element type, so `type` is always `null`. `level` is the element's first level dependency, else the level of its first dependency that has one (a space gets its walls' level, a sheet its plan's), or `null`. A level's `level` is itself.
- Wall: one mesh, a flat-shaded box of 24 vertices and 12 triangles with normals. The axis is the centre line; thickness is split equally to each side; it stands from its level elevation (0 without a level) up by its height. Material `bim-wall`.
- Space: one mesh, its clear interior rectangle at its level elevation (BIM-IR has no space height), 4 vertices and 2 triangles with an up normal. Material `bim-space`, double-sided.
- Level: an empty node translated to (0, elevation, 0). Plan, schedule and sheet have no geometry in BIM-IR; they are empty nodes at the origin that carry identity only.
- Openings, doors, windows, floors/slabs and arc walls are not in BIM-IR's geometry codec, so none are written.
- Buffers: one buffer, three buffer views (all positions, all normals, all `UNSIGNED_INT` indices) with `byteStride` 12 on the two shared vertex views; every POSITION accessor has exact `min`/`max`. Materials appear only for kinds that have a mesh. An empty project gives a root node and no buffer (a GLB with only the JSON chunk).

## Determinism

The same `Project` gives the same bytes on every OS and culture: fixed member order, invariant shortest round-trip float32 text (zero always `0`), little-endian binary, no timestamps or random ids. `tests/gltf` checks golden SHA-256 values for the fixture `.glb`, `.gltf` and the empty project; the goldens come from an independent byte-level reimplementation.

## Refusals

`GltfExportException.Code`: `bim_ir_invalid` (null project, elements or element members), `gltf_too_many_elements` (more than `MaxElements` = 10 000, checked before any other work), the Core `Compiler.Validate` codes (`duplicate_id`, `units`, `missing_dependency`, `wall_geometry`, `dependency_cycle`) and `GeometryCodec` codes (`geometry_kind_unsupported`, `level_geometry`, `space_geometry`, `documentation_geometry`), `gltf_space_geometry` (space width or depth not positive), `gltf_precision` (any coordinate, including a wall top, beyond ±1e4 m, or a wall axis, height, thickness or space size under 1 mm: float32 positions lose millimetre precision past 1e4 m), and `output_exists` (`WriteGlb` never replaces a file).

## Limits

- Not exercised in Blender itself; evidence is unit-tested structural validation against the glTF 2.0 rules implemented in `tests/gltf` (not the Khronos glTF-Validator).
- There is no CLI command yet; callers use the library.
- Wall joins are not resolved: corner walls overlap by half a thickness, as their centre-line boxes do.
