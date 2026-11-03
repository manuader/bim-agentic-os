# Isolated IFC evaluation runtime

This runtime implements the IFC fixture path of the approved evaluation design. ADR0006 authorizes IfcOpenShell under LGPL-3.0-or-later; ADR0005 retains Named Pipes for Revit. Python and its dependencies stay in an optional virtual environment, separate from the C# core and Revit deployment. No vendor wheel or native binary is committed.

## Reproduction

From the repository root, create an environment with an installed Python3.12 executable (`$BimPython` on Windows):

```powershell
& $BimPython -m venv .tools/ifc
./.tools/ifc/Scripts/python.exe -m pip install -r requirements/ifc.txt
./.tools/ifc/Scripts/python.exe -m pip check
if (Test-Path .tools/dotnet/dotnet.exe) { $env:BIM_DOTNET = (Resolve-Path .tools/dotnet/dotnet.exe).Path }
./.tools/ifc/Scripts/python.exe -m unittest discover -s tests/ifc -p 'test_*.py' -v
pwsh -File scripts/evaluate.ps1 -Backend ifc -Python (Resolve-Path .tools/ifc/Scripts/python.exe).Path
```

On Linux/macOS use `python3.12 -m venv .tools/ifc`, `.tools/ifc/bin/python` for installation/tests and that executable with `-Python`. [Ubuntu CI run39889181516](https://github.com/manuader/bim-agentic-os/actions/runs/39889181516) executed this path successfully at e689aaa. macOS compatibility is not verified until it actually runs there. The wrapper also discovers the repository-local virtual environment when `-Python` is omitted.

The current compiler-driven slice also passed all four jobs of [Ubuntu CI39899813748](https://github.com/manuader/bim-agentic-os/actions/runs/39899813748) at8d2cc96; [machine-readable evidence](../artifacts/compiler-ifc-ci-result.json) separates this run from historical fixture evidence.

The full A/B command returns exit2 when IFC portable checks pass and mandatory Revit-only outputs remain unsupported. Inspect `backend_acceptance`, `backend_checks`, `verification_state`, criterion outcomes and `product_acceptance` together. Exit0 must never imply native Revit acceptance from IFC. Missing dependencies or invalid worker proof remain blocked; measured violations fail with exit1.

## Execution and evidence contract

The evaluator now uses the existing C# RoomCompiler and Compiler.Diff through `adapters/ifc/Bim.Ifc.Compiler`; the worker CLI requires that compiled request. Default evaluation remains a5000x4000mm room and+400mm width correction. Canonical custom rectangles are supported within the adapter's explicit limits; the regression oracle also uses6→6.4m and two successive revisions on one saved IFC. The saved Core schema is unchanged.

The worker authors an IFC4 level, four200mm walls, a Space, plus an adapter-owned900mm door and two hosted windows. Door/windows are fixture extras, not Core-authored BIM-IR elements. Six Core UUIDs persist in BimCompiledIdentity mapped to IFC GlobalIds. Openings use real void/fill relationships and bodies; Space boundaries refer to the walls. Width changes preserve the west anchor and native identities, adjust affected geometry and persist revision/operation receipts. The exact C# committed snapshot hash includes its incremented revision, enabling another correction. Latest-operation retry reads and verifies the saved receipt/state; conflicting payloads and historical retries after a later correction fail closed without writes. A forced failure after staged mutation preserves the original persisted file/revision. This is file publication rollback, not Autodesk transactions or crash/power-loss durability.

Observation geometry and parameters come from reopened entities/placements/profiles and independent triangulation/schema validation, with negative geometry/hosting cases. No native Dimension/Plan/View3D/Schedule/Sheet is fabricated. Their observation values are null and criteria remain unsupported. The mandatory IFC checks cover schema, geometry, hosting, save/reopen, idempotency, rollback, incrementality and receipts.

The C# subprocess port bounds execution/output, pins the worker to the current repository, rejects malformed/version-inconsistent reports, invalid metrics, missing/duplicate checks, missing artifacts and process/check disagreement. Offline JSON remains unit-tested and cannot confer integration provenance. Only successful real execution plus all portable criteria/mandatory checks may set backend acceptance.

Metrics state their scope. IFC logical-product counters exclude auxiliary schema entities; timings are measured runtime phases. Unknown master metrics remain null with reasons. The full architectural correction through validated Revit documentation remains unmeasured.

## IDS checks (subset of IDS 1.0)

`runtimes/ifc/ids_check.py` checks an IFC file against a buildingSMART IDS 1.0 file and prints a deterministic JSON report (`bim.ids-report/1`). It is stdlib XML plus the approved IfcOpenShell; the LGPL `ifctester` package is not used.

```powershell
./.tools/ifc/Scripts/python.exe runtimes/ifc/ids_check.py spec.ids model.ifc [--output report.json]
```

Exit codes: `0` every evaluated specification passed, `1` at least one failed (or no specification applies to the model's schema), `2` invalid/unsupported IDS or unreadable IFC. The report lists each specification with its cardinality, `applicable_count`, `status` (`pass`/`fail`/`skipped`) and `failures` (entity class, GlobalId, STEP id, reason), plus overall `status`, `counts`, file basenames and SHA-256 hashes (no local paths). Invalid input gives `status: invalid` and `error.code`.

Supported subset:

- Input: UTF-8 only (other declared encodings and DOCTYPE/ENTITY declarations are refused). Specification attributes are limited to name, ifcVersion (tokens IFC2X3, IFC4, IFC4X3_ADD2; an IFC4X3 model maps to IFC4X3_ADD2), identifier, description and instructions.
- Applicability needs exactly one `entity` facet (name, predefinedType; exact class) and may add attribute/property/classification facets, all ANDed. A type object's PredefinedType other than NOTDEFINED takes precedence over the occurrence; `USERDEFINED` resolves to ElementType (type) or ObjectType (occurrence).
- Facets: `attribute` (name, value), `property` (propertySet, baseName, value, `dataType`; IfcPropertySingleValue only, type property sets apply under occurrence values) and `classification` (value = Identification, system = IfcClassification Name).
- Values: `ids:simpleValue` (exact, case-sensitive text; a numeric model value compares numerically with an XSD-numeric literal, 1e-6 relative tolerance; booleans as `TRUE`/`FALSE`). `xs:restriction` needs a base: `xs:string` (string values only), `xs:integer`, `xs:decimal`, `xs:double`, `xs:float` (numeric values only; literals validated). `xs:enumeration` with any of these; `xs:pattern` only with `xs:string`. Sibling patterns are alternatives and each is implicitly anchored. `\s`/`\S` and `.` are translated to their XSD meaning (`[ \t\n\r]`, `[^\n\r]`); compared strings are capped at 4096 characters.
- Units: IDS values are SI. Length/area/volume property measures are converted from the project's IfcSIUnit (including prefixes such as MILLI). Any other unit-bearing measure, and any numeric attribute (attributes carry no measure type), is only compared when every project unit is an SI base unit; otherwise `IFC_UNSUPPORTED_UNIT`. A model without a unit assignment is treated as SI.
- Specification cardinality from applicability `minOccurs/maxOccurs`: `1/unbounded` required (no applicable entity fails), `0/unbounded` optional, `0/0` prohibited (every applicable entity fails; requirements are not allowed). Facet `cardinality` required/optional/prohibited. Empty strings and nulls count as absent. A specification for another schema is `skipped`.

Fail-closed codes (exit 2): `IDS_DOCTYPE_FORBIDDEN`, `IDS_XML_MALFORMED` (including non-UTF-8), `IDS_NOT_IDS`, `IDS_INVALID`, `IDS_UNSUPPORTED_FACET` (partOf, material, applicability without exactly one entity facet), `IDS_UNSUPPORTED_RESTRICTION` (bounds, length, mixed restrictions, other bases, XSD-only or divergent regex such as `\w`, `\p{..}`, `\i`, `\$`, nested classes/subtraction, unescaped `^`/`$`, `(?`, lazy/stacked quantifiers, nested repeating quantifiers like `(a+)+`), `IDS_UNSUPPORTED_CARDINALITY`, `IDS_UNSUPPORTED_ATTRIBUTE` (including IDS 0.9 style minOccurs/maxOccurs on a specification), `IDS_UNKNOWN_ENTITY`, `IDS_UNREADABLE`, `IFC_UNREADABLE`, `IFC_UNSUPPORTED_PROPERTY_KIND` (a matching quantity set, enumerated/list/bounded property), `IFC_UNSUPPORTED_UNIT`, `IFC_VALUE_TOO_LONG`, `OUTPUT_UNWRITABLE` and `INTERNAL` (unexpected error; no traceback or local path).

Known gaps: partOf/material facets, quantity sets, numeric bounds/length restrictions, entity-name inheritance and patterns, attribute-name patterns, unit-aware numeric attributes, conversion-based units, IDS XSD validation of the whole document, and the official buildingSMART test-case corpus. `tests/ifc/test_ids.py` checks an IFC exported by `worker.create` against `tests/ifc/fixtures/ids` (passing, failing, unsupported) plus small IfcOpenShell-built door models (typed, classified, metre and millimetre units) in the `ifc` and `ifc-portable` CI jobs.

## IFC import to BIM-IR and roundtrip (P8-01)

`runtimes/ifc/ifc_import.py` reads an IFC file into the existing BIM-IR (no schema change) and writes BIM-IR of the same kinds back to IFC4, with the approved IfcOpenShell only. `worker.py --import-ifc model.ifc` is a thin read-only action over the same module.

```powershell
./.tools/ifc/Scripts/python.exe runtimes/ifc/ifc_import.py import model.ifc            # stdout: bim.ifc-import/1 report
./.tools/ifc/Scripts/python.exe runtimes/ifc/ifc_import.py export project.json out.ifc # stdout: {"status":"exported",...}
```

Failures print `{"error","detail"}` on stderr and exit1 (`ifc_unreadable`, `ifc_project_count`, `ifc_units_unsupported`, export refusals below, `internal_error`). The report holds `project` (Core default `Project` JSON shape, integral metres written as `3` like System.Text.Json), `sources` (`id`, `global_id`, `ifc_class` per mapped element) and `unmapped` (`category`, `global_id`, `ifc_class`, `reason`, `detail`, sorted by category, GlobalId, reason).

Mapping, using the P2-01 Mirror layouts: `IfcBuildingStorey` -> `level [elevation]` (placement height; an `Elevation` attribute that disagrees is `level_elevation_conflict`). `IfcWall` and subtypes such as `IfcWallStandardCase` -> `wall [ax,ay,bx,by,height,thickness]` `[level]`: a Body of exactly one vertically extruded `IfcRectangleProfileDef` (any local placement chain and plan rotation), with the axis from a 2-point `IfcPolyline` Axis representation when present (it must agree with the body) or else the longer side of the footprint. `IfcDoor`/`IfcWindow` -> `[ax,ay,bx,by,sill,height]` `[level, host wall]`: only through `IfcRelFillsElement` -> `IfcOpeningElement` -> `IfcRelVoidsElement` -> mapped wall; the opening body gives the span projected on the host axis (in host direction), sill above the level and height; `OverallWidth`/`OverallHeight`, when set, must agree. `IfcSpace` -> `space [x,y,width,depth]` `[level, bounding walls in id order]` (aggregated in a storey; walls from `IfcRelSpaceBoundary`). Containment is `IfcRelContainedInSpatialStructure` (spaces: `IfcRelAggregates`). Lengths use the project length unit (SI metre with prefixes, or a conversion-based unit on metres); values read from placements are snapped to 1e-9m.

Identity mirrors P2-01: an element without BIM-IR identity gets `new Guid(SHA-256("bim-agentic-os/ifc-import/v1\n" + project GlobalId + "\n" + element GlobalId)[0..16])` (GlobalIds are case-sensitive, so not folded) and provenance `ifc-import`/`observed`; the project gets the same derivation with revision0. Elements written by the exporter carry `BimIrElement` (`Id`, `Source`, `Approval`) and the project `BimIrProject` (`Id`, `Revision`); the id is accepted only when it equals the expanded GlobalId (a copy keeping another element's identity is `identity_mismatch`). Elements are ordered level, wall, door, window, space, then id.

Unmapped reasons, never approximated: `no_bim_ir_kind` (slab/roof/covering, bare opening without a fill), `category_unsupported` (any other product; site and building are structure and not listed), `global_id_missing`, `duplicate_global_id`, `identity_mismatch`, `missing_data`, `unsupported_representation` (Brep, clipping, mapped items, non-rectangular or tapered profiles, non-local placements), `invalid_geometry` (including a door sill other than0), `level_unknown`, `level_elevation_conflict`, `wall_base_offset_unsupported`, `wall_axis_ambiguous` (square footprint without Axis), `wall_axis_body_mismatch`, `host_unknown` (no fill, or host not a mapped wall), `opening_not_aligned_with_host`, `opening_outside_host`, `opening_partial_depth` (recess thinner than the wall), `opening_dimension_conflict`, `room_not_rectangular` (rotated too), `space_base_offset_unsupported`. Dependents of an unmapped level or wall are unmapped in turn, so the imported project always passes the Core rules.

Export accepts exactly the shapes import returns and refuses anything else before writing: `bim_ir_invalid` (shape, lowercase UUIDs), `duplicate_id`, `missing_dependency`, `geometry_kind_unsupported`, arity codes, `geometry_non_finite`, `ifc_precision` (off the 1e-9m grid or beyond 1e5m), `wall_geometry`, `ifc_dependency_shape`, `ifc_opening_geometry` (on the host axis in host direction, inside the wall, door sill0), `ifc_space_geometry`, `output_exists`. Plan/schedule/sheet are reported in `unexported` (`no_ifc_entity`), never fabricated. GlobalIds are the compressed BIM-IR ids (auxiliary entities derive theirs from the project id), placements are identity frames under the storey, openings are 5mm deeper than the wall on each side, and a space is extruded to its tallest bounding wall (3m without walls) because BIM-IR spaces carry no height. The file is published (hard link, never replacing) only after IfcOpenShell schema/EXPRESS validation and an import that returns the same BIM-IR.

Stability oracle (`tests/ifc/test_import.py`): export -> import returns the source BIM-IR exactly; export of that import gives the same STEP DATA section (`canonical_data`; the HEADER carries a write timestamp, so whole-file bytes are not compared); input element order does not change the export. The same tests check real kernel triangulation (wall volume minus filled openings), hosting relations, worker-authored millimetre IFC (box bodies, random GlobalIds) mapped completely and re-exportable, `IfcWallStandardCase`, each unmapped reason above that the fixtures exercise, and the module and worker command lines.

Known gaps: walls with layered material offsets, clipped/sloped/curved walls, Brep or mapped door/window bodies without a rectangular opening, openings not hosted by a wall, multiple fills per opening, IFC2X3 files beyond what the same attributes allow, space heights (not carried by BIM-IR), and no comparison with files from other authoring tools in CI.

## Limits

This is a bounded canonical RoomCompiler rectangle/width-correction adapter, not a general IFC editor or arbitrary BIM-IR execution service. The low-level API preserves bytes on no-op; the full A/B harness rejects no-op before creating an output directory because it must observe an actual correction/rollback. It does not implement complete RVT/IFC roundtrip, native Revit documentation, production model merging, human architectural approval, full IDS (only the subset above), BCF execution or Blender. Expanded typed BIM-IR contracts still require a separately approved schema ADR. Generated models stay in ignored `.bim/`; published summaries must exclude personal paths and production identifiers.

The IFC receipt binds the committed snapshot and its revision; it does not atomically publish a separate Core ProjectStore file. Coordination/recovery across those stores remains a gap. Current tests prove persisted IFC state and successive compiled corrections, not a distributed transaction or complete live mirror.

CLI `ifc-export before.json after.json plan.json room-guid operation-id` emits the adapter-only packet; `evaluate ifc python worker.py output-directory request.json` executes a supplied packet. Default `scripts/evaluate.ps1 -Backend ifc` constructs its packet through that same C# service. Before/after/plan use exact default C# serialized strings and raw UTF-8 hashes; Python never invents a replacement canonical hash. See [adapter scope](../adapters/ifc/Bim.Ifc.Compiler/README.md) and [module contract](contracts/module-contracts.md).
