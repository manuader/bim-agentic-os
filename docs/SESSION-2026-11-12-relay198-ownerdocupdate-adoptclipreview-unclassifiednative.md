# Relay198: owner doc-update --execute, mirror-drift-adopt --preview, unclassified native payloads

Cloud scheduled relay, started 2026-11-12 09:52Z (LOCK was FREE after relay197). GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases with ready work: 2, 4 and 10 (relay194). P1-18 waited (shares tests/integration/test-catalogue-session.ps1 with P4-14). Workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (1 blocker, 11 should-fix, nits), findings closed with tests or recorded as gaps, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P4-14 (Phase 4): the owner -Catalogue documentation group runs doc-update (dry run, then --execute --expected-document <sha256 of its own plan.json>) to realize the fixture's default documentation and regenerate the stale door tag; run folder keeps inputs, plan, report and stderr; reply_lost or an unusable --execute line stops the run; owner-native-session.ps1 wires -InvokeDocUpdate with -DocUpdateTimeoutSeconds.
- P2-19 (Phase 2): mirror-drift-adopt store selection.json --preview prints the MCP plan-mode text byte for byte; store untouched; typed drift_adopt_option_unknown / _duplicate; usage, MCP description and contract updated.
- P10-16 (Phase 10): package-verify refuses PE/ELF/Mach-O payloads with a non-binary extension (sbom_binary_unclassified), never opens links, types unreadable files (sbom_binary_unreadable).
- Board: P4-14, P2-19, P10-16 ✅; P4-16 ⏳ (deps done); new P2-21 (option codes in Core), P4-17 (template name collisions before doc-up-realize), P10-17 (unreadable manifest files and special files). Progress 168/187 (89.8 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P4-14 tests/integration + tests/cli: red 43198296774, green 43202889177; review red 43206935234, green 43207050532. 3 failed pushes (distinct test-harness defects, oracle unchanged).
- P2-19 tests/cli: red 43195815206, green 43199978494; review green 43210710717. 1 failed attempt (test lock snapshot).
- P10-16 tests/packaging: red 43195694182, green 43201257316; review red 43214415283, green 43214567594. 1 failed attempt (own MZ-text fixture).
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; P4-11 / P1-14 / P4-14 owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. Optional: repository licence.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P4-14: owner run no longer exercises dependents > 0 (P4-12 tests do); DocDefaults fixed names may collide with a template, schedules assume English UI (→ P4-17); first realized map hand-built (→ P4-16); not run in Revit.
- P2-19: option codes CLI-only (→ P2-21); a path starting with -- is now refused as usage.
- P10-16: 2-byte MZ only (MZ text refused by design); fat-64 CAFEBABF and archives not inspected; manifest-listed unreadable files and FIFOs (→ P10-17).
ARCHIVOS CAMBIADOS: scripts/{owner-catalogue-steps,owner-native-session,package-common}.ps1; tests/integration/{test-catalogue-session,fake-doc-update (new),fake-revit-command-pipe}.ps1; tests/cli/{catalogue-report-record.ps1,MirrorDriftAdoptTests.cs}; interfaces/cli/{MirrorDriftAdoptCommand,Program}.cs; interfaces/mcp/McpServer.cs (description); tests/packaging/test-package.ps1; board/features/contracts/attempts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P1-18/P1-22, P2-20, P6-17/P6-18 (relay195; P1-18 shares owner session/test files with P4-16/P4-17 — serialize; P6-17/P6-18 share PDF fixtures; P1-23 after P1-22); then P3-15, P5-17/P7-16, P7-17 (relay197); then P4-16/P4-17, P2-21, P10-17 (relay198). Owner: ADR0010/ADR0011; one -Catalogue run (now includes the doc-update steps).
```
