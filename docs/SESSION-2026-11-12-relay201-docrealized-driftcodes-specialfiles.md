# Relay201: owner doc-realized step, drift adopt codes in Core, package special files

Cloud scheduled relay, started 2026-11-12 20:53Z (LOCK was FREE after relay200). GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases with ready work: 4, 2 and 10 (relay198). P4-17 waited (shares owner-catalogue-steps.ps1 and test-catalogue-session.ps1 with P4-16). Workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (1 blocker, 6 should-fix, nits), findings closed with tests, integration head re-run in CI before main moved. A container restart at ~23:50Z killed the P2-21 worker mid-fix; the controller finished it from the worktree (red already confirmed). Relay202 found the heartbeat stale during the restart and backed off correctly.

```
IMPLEMENTADO:
- P4-16 (Phase 4): new offline step doc-up-elements in scripts/owner-catalogue-steps.ps1 runs the real `doc-realized` on the session's own run record (schema 1, document UniqueId, earlier steps), the fixture and element:<wall>/element:<door> assignments; its sha256-checked output bytes are the only --realized input of doc-up-realize-plan/doc-up-realize; NN-key.doc-realized/ keeps record, fixture, assignments, realized, report and stderr; a realized_* refusal stops the run with report.json kept; recorded request is a stub; non-object replies kept as raw_response.
- P2-21 (Phase 2): MirrorDriftAdoptCodes.OptionUnknown/OptionDuplicate in Core (CLI-only codes) used by MirrorDriftAdoptCommand; fixture-tested static literal guard.
- P10-17 (Phase 10): package-verify stops at links, classifies files by POSIX test -p/-S/-c/-b via /bin/sh before any open (package_special_file; package_special_file_check_failed), types manifest-listed unreadable files (manifest_file_unreadable) and unreadable shipped binaries in the SBOM check (sbom_binary_unreadable instead of sbom_invalid).
- Board: P4-16, P2-21, P10-17 ✅; P10-18 ⏳; new P4-18 (catalogue-report surfaces the doc-realized step), P10-19 (package.ps1 staging refuses special files before hashing). Progress 177/195 (90.8 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P4-16 tests/integration (headless fake + real CLI): red 43305770454, green 43311249336; review red 43328761013, green 43328848169. 1 failed attempt (test harness).
- P2-21 tests/cli: red 43304953442, green 43308359776; review red 43332065936, green 43333418906. 0 failed attempts.
- P10-17 tests/packaging: red 43305452588, green 43315327377; review red 43320124473, green 43324318365. 1 failed attempt (UnixMode missed the FIFO on Ubuntu).
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒 (now also exercises doc-up-elements).
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. Optional: repository licence.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P4-16: doc-up-update-plan/doc-up-update still use the realized map doc-update prints (by design); a realized_* refusal from one malformed earlier step stops every later group; catalogue-report does not show the step (→ P4-18); never run in Revit.
- P2-21: guard strips whole-line comments only (the file has no trailing comments; a trailing comment with an underscore token would be a false positive, the safe direction).
- P10-17: Linux CI only (macOS CI does not run verify.ps1; Windows SKIPs); no device fixture (mknod needs root; /dev/null classifier assert); package.ps1 staging still hashes without a type check (→ P10-19); TOCTOU after the check not covered.
ARCHIVOS CAMBIADOS: scripts/{owner-catalogue-steps.ps1,package-common.ps1}; core/Bim.Core/MirrorDriftAdopt.cs; interfaces/cli/MirrorDriftAdoptCommand.cs; tests/integration/{test-catalogue-session.ps1,fake-doc-update.ps1}; tests/cli/MirrorDriftAdoptTests.cs; tests/packaging/test-package.ps1; board/features/contracts/attempts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P1-23 (then P1-24), P2-22, P6-18 (then P6-19) (relay199); then P3-16, P5-18/P7-16, P10-18 (relay200); then P4-17/P4-18 (serialize; share owner-session test files with P1-18), P10-19 after P10-18. Owner: ADR0010/ADR0011; one -Catalogue run.
```
