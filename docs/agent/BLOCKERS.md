# Blockers

- Complete native Test A/B: unsupported hosted families, Room and documentation; primitive evidence must never count as full acceptance.
- General IFC runtime: current bounded C# RoomCompiler/width adapter is integration-tested (ifc) on Windows (11tests90.026s plus real CLI). Prior fixture ran on Ubuntu CI39889181516; current CI is tracked in artifacts/compiler-ifc-ci-result.json. Arbitrary BIM-IR, complete native documentation and historical retry after later correction are unsupported; macOS unexecuted. Dependency approval and installation are complete.
- Local verification context: two restricted MSBuild builds failed silently; same source/SDK/arguments and complete gate passed with approved escalation. Exact failing OS operation unknown. First scheduled execution is unobserved and cannot bypass sandbox or interactive gates; record a real block if it occurs.
- RPC: user ratified process-specific Named Pipes in ADR0005. No transport migration is needed.
- Typed IR schema: existing Project/Element geometry arrays are prototype contracts. Geometry/Units/Capability/SemanticDiff expansion requires a proposed ADR and explicit approval before schema changes.
- License census: native editable authoring/save/reopen executed successfully in prior run; current Autodesk entitlement has not been independently queried. No account/credential access is inferred or attempted.

For each exhausted three-attempt failure, add hypothesis, discarded approaches, last green commit and exact external input needed. Preserve evidence and switch to independent work.

