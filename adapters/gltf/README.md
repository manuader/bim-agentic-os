# glTF 2.0 export (Blender interchange, no Blender)

`Bim.Gltf` converts a compiled BIM-IR `Project` to glTF 2.0: a binary `.glb` (`GltfExporter.ExportGlb`, `GltfExporter.WriteGlb`) or a self-contained `.gltf` whose buffer is an embedded base64 `data:` URI (`GltfExporter.ExportGltf`). It is BCL-only and references `core/Bim.Core` for the BIM-IR records and validation. Blender is not installed or needed to produce the files; Blender imports glTF 2.0 natively. The BIM-IR schema is unchanged.

Regressions: `dotnet run --project tests/gltf --configuration Release` (registered in `scripts/verify.ps1`, so CI runs it on Linux and Windows); the CLI verb is covered by `tests/cli`.

## CLI

`dotnet interfaces/cli/bin/Release/net8.0/Bim.Cli.dll export-gltf project.json output.glb` (or `output.gltf`). Arguments are positional like the other CLI verbs. `project.json` is a BIM-IR `Project` in the default `System.Text.Json` form the other verbs read, read through the CLI's bounded reader (8 MiB, regular file, no links; `input_*` codes). The output path must end in `.glb` or `.gltf` (any case), sit in an existing directory that is not a link, not be a link itself (dangling links included) or a Windows `\\.\`/`\\?\` device path, and not exist: the file is created exclusively and never replaced. The output path is checked before the input is read.

- Success, exit 0: `{"status":"succeeded","format":"glb","output":"<path as given>","bytes":N,"sha256":"<upper-case hex>","elements":N,"openings":N}`. The bytes are exactly `ExportGlb`, or `ExportGltf` as UTF-8 without BOM, so rerunning on the same input gives the same bytes.
- Refusal, exit 1, nothing written: `{"status":"failed","code":"<code>"}`; exporter refusals (codes below) add `"detail"`. CLI codes: `output_path_invalid` (empty or unparsable), `output_extension_unsupported`, `output_directory_not_found`, `output_link_rejected`, `output_not_regular_file`, `output_exists`, `output_io_error`, `bim_ir_json_invalid`, the `input_*` codes, and the usage text for a wrong argument count.

## Import into Blender

File → Import → glTF 2.0 (.glb/.gltf), pick the file. The importer converts glTF Y-up back to its Z-up, so BIM X/Y/Z become Blender X/Y/Z in metres. Element identity shows on each object under Object Properties → Custom Properties (`bim_id`, `kind`, `level`; `type` is null and Blender may omit it). The project root object carries `bim_project_id`, `revision`, `schema`.

## Mapping

- Units are metres. BIM Z-up maps to glTF Y-up as (x, y, z) → (x, z, −y), a proper rotation, so winding stays counter-clockwise.
- One root node `project <id>` (extras `schema` = `bim-agentic-os.gltf/1`, `bim_project_id`, `revision`, `units` = `m`, `up` = `Y`). Its children are one node per element in element order, named `<kind> <id>`, with extras `{bim_id, kind, type, level}`. BIM-IR carries no element type, so `type` is always `null`. `level` is the element's first level dependency, else the level of its first dependency that has one (a space gets its walls' level, a sheet its plan's), or `null`. A level's `level` is itself.
- Wall: one mesh, a flat-shaded box of 24 vertices and 12 triangles with normals. The axis is the centre line; thickness is split equally to each side; it stands from its level elevation (0 without a level) up by its height. Material `bim-wall`.
- Space: one mesh, its clear interior rectangle at its level elevation (BIM-IR has no space height), 4 vertices and 2 triangles with an up normal. Material `bim-space`, double-sided.
- Level: an empty node translated to (0, elevation, 0). Plan, schedule and sheet have no geometry in BIM-IR; they are empty nodes at the origin that carry identity only.
- Door and window (P8-07): BIM-IR carries them as `IntentCompiler` emits them, kind `door`/`window`, geometry `[ax, ay, bx, by, sill, height]` (the opening's two ends on its host's axis, then sill and height above the host base) and dependencies `[level, host wall]`; `GeometryCodec` has no typed view for them, so the exporter reads that layout directly (no schema change). The host is the opening's single wall dependency. The opening's own node is empty (no mesh) and its extras add `host` (the host wall id) after `level`. Each host wall is split into solid boxes in axis order: the pier before each opening, then the part below it (when the sill is above the base) and the part above it (when its head is below the wall top), and the pier after the last one; parts of zero length are not emitted, so a wall with k openings has at most 3k + 1 boxes of 24 vertices and 12 triangles each, all in the wall's one mesh. End points are sorted along the host, so their order and the element order do not change the bytes. Adjacent boxes share coincident faces (not merged), so a split wall is a set of closed boxes rather than one closed manifold. A wall without openings is the same single box as before (golden bytes unchanged).
- Hosted openings `[offset, width]` (the other door/window layout `DocRegeneration` reads) carry no sill or height, so they are refused (`gltf_opening_geometry`) rather than guessed. Kind `opening`, floors/slabs and arc walls are not in BIM-IR's geometry codec and stay refused (`geometry_kind_unsupported`) or absent.
- Buffers: one buffer, three buffer views (all positions, all normals, all `UNSIGNED_INT` indices) with `byteStride` 12 on the two shared vertex views; every POSITION accessor has exact `min`/`max`. Materials appear only for kinds that have a mesh. An empty project gives a root node and no buffer (a GLB with only the JSON chunk).

## Determinism

The same `Project` gives the same bytes on every OS and culture: fixed member order, invariant shortest round-trip float32 text (zero always `0`), little-endian binary, no timestamps or random ids. `tests/gltf` checks golden SHA-256 values for the fixture `.glb`, `.gltf` and the empty project; the goldens come from an independent byte-level reimplementation.

## Refusals

`GltfExportException.Code`: `bim_ir_invalid` (null project, elements or element members), `gltf_too_many_elements` (more than `MaxElements` = 10 000, checked before any other work), the Core `Compiler.Validate` codes (`duplicate_id`, `units`, `missing_dependency`, `wall_geometry`, `dependency_cycle`) and `GeometryCodec` codes (`geometry_kind_unsupported`, `level_geometry`, `space_geometry`, `documentation_geometry`), `gltf_space_geometry` (space width or depth not positive), `gltf_opening_geometry` (a door/window whose geometry is not 6 values, or whose width or height is not positive), `gltf_opening_host` (not exactly one wall dependency), `gltf_opening_outside_wall` (an end more than `OpeningTolerance` = 1e-6 m off the host axis or past its ends, a negative sill, or a head above the wall top), `gltf_opening_overlap` (two openings on one host overlap by more than 1e-6 m; touching is fine), `gltf_precision` (any coordinate beyond ±1e4 m, including a wall top and the wall face corners thickness/2 either side of the axis, or a wall axis, height, thickness, space size, opening width or height, or a remaining pier, sill wall or lintel under 1 mm (gaps up to 1e-6 m are snapped closed); float32 values near 1e4 m are 2^-10 m, about 0.98 mm, apart), and `output_exists` (`WriteGlb`, `WriteGltf` and `WriteNew(bytes, path)` never replace a file or directory; Windows reports a directory as access denied, which is mapped to the same code). If writing fails after `WriteGlb` created the file, it deletes that file and rethrows the original exception, so no truncated `.glb` is left.

## Limits

- Not exercised in Blender itself; evidence is unit-tested structural validation against the glTF 2.0 rules implemented in `tests/gltf` (not the Khronos glTF-Validator).
- Openings are rectangular through-cuts only: no frames, panels, glass, swing or reveal geometry, and the opening's own level is not used (its sill is measured from the host base, as `IntentCompiler` emits it).
- Wall joins are not resolved: corner walls overlap by half a thickness, as their centre-line boxes do.
