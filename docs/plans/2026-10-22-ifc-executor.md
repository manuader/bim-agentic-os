# Real IFC executor implementation plan

Goal: execute the IFC path of the already approved evaluation design with real files, independent readback and reproducible evidence. The user approved ADR0005/0006 and explicitly requested implementation, integration tests and publication. This continues the approved design; no product rediscovery, BIM-IR migration or new RPC choice.

Architecture: keep the C# oracle and CLI authoritative. An isolated pinned Python/IfcOpenShell worker owns a test IFC4 file and persists logical identity/revision/receipts in IFC properties. A bounded C# subprocess port launches the repository worker, captures its typed observations/metrics and evaluates them. Caller-supplied offline JSON never gains integration provenance. Native-only requirements stay unsupported and full native product acceptance remains false.

Tech stack: existing.NET8/PowerShell plus optional Python3.12 and IfcOpenShell0.9.0.post1 (verify available wheel before install). No core/native vendor dependency or schema change.

- [x] Python worker task: TDD real level/four200mm walls/900mm door/two windows/Space, explicit units/placements and hosting/void-fill relations; save/reopen and geometry/schema checks. Implement incremental+400mm, stable identities, retry receipt and forced-failure rollback. Record attempts per individual test.
- [x] C# bridge task: TDD typed process envelope, bounded timeout/cancellation, missing dependency/malformed/readback failure paths, CLI/wrapper configuration. Keep all existing43 regressions; IFC-specific missing native documentation is unsupported, measured violations remain failures. Add distinct portable backend checks without claiming native acceptance.
- [x] Integrate reviewed worktrees; execute real Python and one-command IFC tests on this host. Preserve generated IFCs privately and publish sanitized hashes, measurements and timings.
- [x] CI: install pinned optional Python requirements on Ubuntu and run actual IFC integration plus existing core/security gates. Upload summary evidence only. No green placeholder for Revit.
- [x] Independent source review, full engineering gate, dependency audit, state/blocker/contract updates, Conventional Commits and authorized GitHub push; verify exact-commit CI.

File ownership: worker agent runtimes/ifc,tests/ifc,requirements/ifc*,third-party notices; bridge agent evaluation/Bim.Evaluation,interfaces/cli,scripts/evaluate.ps1,tests/evaluation; controller docs,CI,verification wiring,evidence and integration. Separate isolated worktrees; no Revit model writes.

Protocol v1: worker stdout is one JSON object with protocol_version,backend,backend_version,source,observations,metrics,checks,artifacts,errors. Observations reuse evaluation DTOs in snake_case; missing native documentation values are null. Checks are pass/fail/blocked/unsupported outcomes. Metrics use measured values or null/reasons. Artifact paths are basenames. The process exit and all mandatory backend checks must agree; missing/failed checks cannot confer portable backend acceptance. Full TestA/B still requires native-only outputs.

Review focus: declared dimensions versus actual solids/placements; duplicated/stale GlobalIds; opening hosts and real void cuts; retry write amplification/receipt tampering; rollback file/revision restoration; worker spoofing/missing proof; accidental promotion of unsupported Revit documentation. Maximum three failed attempts per individual test; no weakened oracles.

Closure: actual Windows controller execution and Ubuntu CI run39889181516 at e689aaa passed, including core and C#/Python CodeQL. Evidence: artifacts/ifc-integration-result.json and ifc-ci-result.json. This completes the fixed-fixture executor block; arbitrary BIM-IR/native documentation remain explicitly outside its implemented capabilities.
