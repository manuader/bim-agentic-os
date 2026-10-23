# Isolated IFC evaluation runtime

This runtime implements the IFC fixture path of the approved evaluation design. ADR0006 authorizes IfcOpenShell under LGPL-3.0-or-later; ADR0005 retains Named Pipes for Revit. Python and its dependencies stay in an optional virtual environment, separate from the C# core and Revit deployment. No vendor wheel or native binary is committed.

## Reproduction

From the repository root, create an environment with an installed Python3.12 executable (`$BimPython` on Windows):

```powershell
& $BimPython -m venv .tools/ifc
./.tools/ifc/Scripts/python.exe -m pip install -r requirements/ifc.txt
./.tools/ifc/Scripts/python.exe -m pip check
./.tools/ifc/Scripts/python.exe -m unittest discover -s tests/ifc -p 'test_*.py' -v
pwsh -File scripts/evaluate.ps1 -Backend ifc -Python (Resolve-Path .tools/ifc/Scripts/python.exe).Path
```

On Linux/macOS use `python3.12 -m venv .tools/ifc`, `.tools/ifc/bin/python` for installation/tests and that executable with `-Python`. [Ubuntu CI run39889181516](https://github.com/manuader/bim-agentic-os/actions/runs/39889181516) executed this path successfully at e689aaa. macOS compatibility is not verified until it actually runs there. The wrapper also discovers the repository-local virtual environment when `-Python` is omitted.

The full A/B command returns exit2 when IFC portable checks pass and mandatory Revit-only outputs remain unsupported. Inspect `backend_acceptance`, `backend_checks`, `verification_state`, criterion outcomes and `product_acceptance` together. Exit0 must never imply native Revit acceptance from IFC. Missing dependencies or invalid worker proof remain blocked; measured violations fail with exit1.

## Execution and evidence contract

The worker authors an IFC4 level, four200mm walls/3000mm height, a900mm door, two windows and a5000x4000mm clear Space. Openings use real void/fill relationships and body geometry; Space boundaries refer to the walls. Stable logical IDs map to IFC GlobalIds. Its fixture revision changes width by400mm, preserves the west anchor and native identities, adjusts affected geometry and persists a revision/operation receipt. Retry reads the saved receipt; a conflicting operation key fails. A forced failure after staged mutation must preserve the original persisted file/revision. This is file publication rollback, not a claim of Autodesk transactions or crash/power-loss durability.

Observation geometry and parameters come from reopened entities/placements/profiles and independent triangulation/schema validation, with negative geometry/hosting cases. No native Dimension/Plan/View3D/Schedule/Sheet is fabricated. Their observation values are null and criteria remain unsupported. The mandatory IFC checks cover schema, geometry, hosting, save/reopen, idempotency, rollback, incrementality and receipts.

The C# subprocess port bounds execution/output, pins the worker to the current repository, rejects malformed/version-inconsistent reports, invalid metrics, missing/duplicate checks, missing artifacts and process/check disagreement. Offline JSON remains unit-tested and cannot confer integration provenance. Only successful real execution plus all portable criteria/mandatory checks may set backend acceptance.

Metrics state their scope. IFC logical-product counters exclude auxiliary schema entities; timings are measured runtime phases. Unknown master metrics remain null with reasons. The full architectural correction through validated Revit documentation remains unmeasured.

## Limits

This is a deterministic evaluation fixture executor, not a general IFC editor or arbitrary BIM-IR execution service. It does not implement complete RVT/IFC roundtrip, native Revit documentation, production model merging, human architectural approval, IDS/BCF execution or Blender. Expanded typed BIM-IR contracts still require a separately approved schema ADR. Generated models stay in ignored `.bim/`; published summaries must exclude personal paths and production identifiers.
