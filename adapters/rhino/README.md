# Rhino .3dm adapter (no Rhino)

`bim_rhino.py` converts a BIM-IR `Project` JSON (exactly the Core default serializer shape) to a `.3dm` file and back with [rhino3dm](https://pypi.org/project/rhino3dm/) 8.17.0 (MIT, McNeel's openNURBS bindings). Rhino is not installed or needed. The BIM-IR schema is unchanged.

Install into an isolated environment and run the regressions:

```sh
python3.12 -m venv .tools/rhino
.tools/rhino/bin/python -m pip install --only-binary=:all: -r requirements/rhino.txt
.tools/rhino/bin/python -I tests/rhino/run.py
```

CLI: `python bim_rhino.py export PROJECT_JSON OUTPUT_3DM` and `python bim_rhino.py import INPUT_3DM` (prints the Project JSON). Errors print `{"error","detail"}` on stderr and exit 1.

## Mapping

- Model units are Meters. Import also accepts Millimeters (divided by 1000); any other unit is refused (`rhino_units_unsupported`).
- Layer `BIM` holds `bim.schema` (`bim-agentic-os.rhino/1`), `bim.project.id` and `bim.project.revision` as user strings. Child layers `BIM::Levels`, `BIM::Walls`, `BIM::Spaces`, `BIM::Documentation` hold the elements of each kind.
- Each element is one object whose Rhino object Id equals the BIM-IR Id. User strings `bim.id`, `bim.kind`, `bim.order`, `bim.dependencies` (JSON array), `bim.provenance.source` and `bim.provenance.approval` carry the rest. The two provenance values are JSON-encoded strings, because Rhino deletes a user string set to empty text.
- Wall: a capped `Extrusion` whose path is the wall axis and whose profile is the thickness x height cross-section centred on it. Base elevation is the first level dependency (0 without one).
- Level: a `Point` at (0, 0, elevation). Space: a closed axis-aligned `PolylineCurve` rectangle at its level elevation. Plan/schedule/sheet have no geometry in BIM-IR; they are marker points at the origin that carry identity only.

On import, geometry comes from the Rhino geometry, not from the user strings: wall axis from the extrusion path, height and thickness from the profile, space corner and size from the outline, level elevation from the point. Height, thickness and space size are snapped to 1e-9 m to remove transform noise, so export, import and export again gives the same records. Element order comes from `bim.order`. Millimetre coordinates first drop under 1e-6 mm of noise, as Core's `LengthUnits.NormalizeMillimetres` does, then are divided by 1000.

## Refusals

Unreadable files (`rhino_read_failed`), models without the BIM root layer (`rhino_not_bim`), another schema marker, other units, objects without BIM user strings (`rhino_foreign_object`), copied objects whose Rhino Id differs from `bim.id` (`rhino_identity_mismatch`), objects on the wrong category layer, wrong geometry types, walls that are not a horizontal axis with a centred rectangular profile, and rotated or non-rectangular spaces. Exported and imported projects pass the Core rules again (kinds and arities, finite values, wall rule, unique ids, dependencies, cycles). Walls and spaces must still stand at their level's elevation, level points must stay on (0, 0, elevation) and documentation markers at the origin (`rhino_elevation_mismatch`, `rhino_position_mismatch`): BIM-IR has no field for those moves, so they are refused instead of dropped.

Export never replaces a file. It writes into `OUTPUT.pending`, created exclusively (an existing file or symlink there, for example left by a crash, is refused with `output_exists`), and publishes it with a hard link, which fails if `OUTPUT` appeared meanwhile. The command line reports every failure, including malformed JSON, numbers outside the double range, very deep nesting and lone surrogates (`bim_ir_invalid`), as one JSON line on stderr; anything unexpected is `internal_error`, never a traceback.

## Stricter than Core

Input that Core accepts but this adapter refuses: UUIDs that are not lowercase text, members other than exactly the Core ones, kinds the Core `GeometryCodec` cannot decode (Core's `Compiler.Validate` allows any kind), revisions outside 32 bits, strings with lone surrogates, spaces with zero or negative width or depth (`rhino_space_geometry`), and lengths a .3dm round trip cannot carry (`rhino_precision`): wall axis, height, thickness or space size under 1e-6 m, or any coordinate beyond 1e5 m. The nil UUID is accepted, as in Core.

## Limits

- The Core geometry codec has no arc walls, openings or room boundaries, so none are written. A wall with an extra value is refused, not guessed into an arc.
- `.3dm` bytes are not reproducible: rhino3dm writes timestamps and random layer ids. Stability is checked on the imported records and object ids.
- Imported values with more than 9 decimals in height, thickness or space size differ from the original by under 1e-9 m. Output JSON matches Core's for ASCII text; Core escapes non-ASCII and some ASCII characters (`é`, `+`), Python does not do it the same way.
- Not exercised in Rhino itself; evidence is real rhino3dm read/write in CI on Linux only.
