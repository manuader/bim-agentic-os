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

The full A/B command returns exit2 when IFC portable checks pass and mandatory Revit-only outputs remain unsupported. Inspect `backend_acceptance`, `backend_checks`, `verification_state`, criterion outcomes and `product_acceptance` together. Exit0 must never imply native Revit acceptance from IFC. Missing dependencies or invalid worker proof remain blocked; measured violations fail with exit1.

## Execution and evidence contract

The evaluator now uses the existing C# RoomCompiler and Compiler.Diff through `adapters/ifc/Bim.Ifc.Compiler`; the worker CLI requires that compiled request. Default evaluation remains a5000x4000mm room and+400mm width correction. Canonical custom rectangles are supported within the adapter's explicit limits; the regression oracle also uses6→6.4m and two successive revisions on one saved IFC. The saved Core schema is unchanged.

The worker authors an IFC4 level, four200mm walls, a Space, plus an adapter-owned900mm door and two hosted windows. Door/windows are fixture extras, not Core-authored BIM-IR elements. Six Core UUIDs persist in BimCompiledIdentity mapped to IFC GlobalIds. Openings use real void/fill relationships and bodies; Space boundaries refer to the walls. Width changes preserve the west anchor and native identities, adjust affected geometry and persist revision/operation receipts. The exact C# committed snapshot hash includes its incremented revision, enabling another correction. Latest-operation retry reads and verifies the saved receipt/state; conflicting payloads and historical retries after a later correction fail closed without writes. A forced failure after staged mutation preserves the original persisted file/revision. This is file publication rollback, not Autodesk transactions or crash/power-loss durability.

Observation geometry and parameters come from reopened entities/placements/profiles and independent triangulation/schema validation, with negative geometry/hosting cases. No native Dimension/Plan/View3D/Schedule/Sheet is fabricated. Their observation values are null and criteria remain unsupported. The mandatory IFC checks cover schema, geometry, hosting, save/reopen, idempotency, rollback, incrementality and receipts.

The C# subprocess port bounds execution/output, pins the worker to the current repository, rejects malformed/version-inconsistent reports, invalid metrics, missing/duplicate checks, missing artifacts and process/check disagreement. Offline JSON remains unit-tested and cannot confer integration provenance. Only successful real execution plus all portable criteria/mandatory checks may set backend acceptance.

Metrics state their scope. IFC logical-product counters exclude auxiliary schema entities; timings are measured runtime phases. Unknown master metrics remain null with reasons. The full architectural correction through validated Revit documentation remains unmeasured.

## Limits

This is a bounded canonical RoomCompiler rectangle/width-correction adapter, not a general IFC editor or arbitrary BIM-IR execution service. The low-level API preserves bytes on no-op; the full A/B harness rejects no-op before creating an output directory because it must observe an actual correction/rollback. It does not implement complete RVT/IFC roundtrip, native Revit documentation, production model merging, human architectural approval, IDS/BCF execution or Blender. Expanded typed BIM-IR contracts still require a separately approved schema ADR. Generated models stay in ignored `.bim/`; published summaries must exclude personal paths and production identifiers.

CLI `ifc-export before.json after.json plan.json room-guid operation-id` emits the adapter-only packet; `evaluate ifc python worker.py output-directory request.json` executes a supplied packet. Default `scripts/evaluate.ps1 -Backend ifc` constructs its packet through that same C# service. Before/after/plan use exact default C# serialized strings and raw UTF-8 hashes; Python never invents a replacement canonical hash. See [adapter scope](../adapters/ifc/Bim.Ifc.Compiler/README.md) and [module contract](contracts/module-contracts.md).
