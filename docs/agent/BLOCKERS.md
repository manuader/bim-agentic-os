# Blockers

- Native Test A/B: full A/B passed once on an isolated Revit 2025 fixture (attempt3, run e2ce96ec, 52/52 criteria, 8/8 backend checks; [summary](../../artifacts/native-room-attempt3.json)). Still open: repeatability (single run), full-rebuild comparison (Phase 3 exit delta), unintended_element_changes metric not instrumented, add-in bound to 287014c by timestamp only. Each new Revit launch needs the owner's Load Once (N21).
- Phase 3 exit (delta vs full rebuild): measured on the IFC backend only ([baseline](../../artifacts/ifc-rebuild-benchmark.json)); time is QA-dominated with no measurable difference, the delta is identity preservation and change size. Native Revit delta remains N21.
- Cross-store recovery: ADR0007 A reconcile implemented for IFC (unit-tested, Ubuntu CI). Native Revit path, Windows/OneDrive rename/fsync semantics and conflict-copy refusal remain open.
- General IFC runtime: current bounded C# RoomCompiler/width adapter is integration-tested (ifc) on Windows (11tests90.026s plus real CLI). Prior fixture ran on Ubuntu CI39889181516; current CI is tracked in artifacts/compiler-ifc-ci-result.json. Arbitrary BIM-IR, complete native documentation and historical retry after later correction are unsupported; macOS executed for real IFC on GitHub runners (run 40031105768). Dependency approval and installation are complete.
- Local verification context: two restricted MSBuild builds failed silently; same source/SDK/arguments and complete gate passed with approved escalation. Exact failing OS operation unknown. First scheduled execution is unobserved and cannot bypass sandbox or interactive gates; record a real block if it occurs.
- RPC: user ratified process-specific Named Pipes in ADR0005. No transport migration is needed.
- Typed IR schema: existing Project/Element geometry arrays are prototype contracts. Geometry/Units/Capability/SemanticDiff expansion requires a proposed ADR and explicit approval before schema changes.
- License census: native editable authoring/save/reopen executed successfully in prior run; current Autodesk entitlement has not been independently queried. No account/credential access is inferred or attempted.

For each exhausted three-attempt failure, add hypothesis, discarded approaches, last green commit and exact external input needed. Preserve evidence and switch to independent work.

