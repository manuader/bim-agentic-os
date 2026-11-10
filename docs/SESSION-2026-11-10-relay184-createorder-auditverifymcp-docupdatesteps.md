# Relay184: ordered creates, audit_verify MCP tool, owner two-pass documentation update steps

Cloud scheduled relay, started 2026-11-10 20:53Z (LOCK was FREE after relay183). GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases with ready work: 3 and 10 (relay178), then 4 (relay181). Workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (1 blocker, 8 should-fix), every finding closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P3-10 (Phase 3): optional create-placement order in IncrementalDiff (`createOrder` comparer, `IncrementalDiff.OrderOf(target)`): IntentEdits plans are now byte-identical to Compiler.Diff(compile(before), compile(after)) on the whole P3-09 corpus; default bytes unchanged; typed edit_create_order_failed; Advance re-sorts dependents.
- P10-12 (Phase 10): read-only MCP tool `audit_verify {log_path, anchor_path?, actors?, summary?}` sharing the P10-11 engine (byte-equal output/refusals); McpServer optional parameters and boolean/array types.
- P4-11 (Phase 4): owner -Catalogue documentacion group gains 20 doc-up-* steps — move a test wall, two-pass dependents (probe/confirm/real delete with exact confirm_dependents, only the section's dry-run baseline accepted), re-create section/tag/dimension and read them back.
- Board: P3-10, P10-12, P4-11 ✅; P3-11 and P1-18 ⏳ (deps done); new P4-12 (doc-update --execute), P4-13 (realized map for catalogue-made elements). Progress 132/152 (86.8 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P3-10 tests/local-validation: red 42900155912, green 42900943413; review green 42915932076. 0 failed attempts.
- P10-12 tests/mcp (+ tests/cli unchanged): red 42905599580, green 42902992456; review green 42913047401. 1 failed attempt (AD0001 analyzer crash).
- P4-11 tests/integration catalogue-session (fake + real CLI): red 42901043322, green 42904609025; review red 42909552877, green 42909679196. 2 failed attempts (strict-mode pitfalls).
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; P4-11 owner run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A). P4-11 accepts up to 2 untracked dependents of the freshly made test section (its plan marker) — confirm or require a category check.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P3-10: no apply-intent CLI yet (P3-11); callers pass OrderOf(set.After) themselves.
- P10-12: actors is a JSON array (CLI: comma list); MCP-only path refusal code.
- P4-11: never run in Revit — assumes a section's dry-run delete lists ≤2 dependents, empty created/modified on dry runs, deleted == target + dependents; sends the P4-09 algorithm step by step (P4-12 will add a CLI executor; P4-13 a realized map).
- Flaky Linux catalogue-session-cli `revit-command --all` pipe_io_error (P1-19) fired again once.
- Stray remote branch relay184-P10-12-red (test-only red evidence) could not be deleted from the cloud (proxy/permission); delete when convenient.
ARCHIVOS CAMBIADOS: core/Bim.Core/{IncrementalDiff,IncrementalDiffAdvance,IntentEdits}.cs, tests/local-validation/IntentEditsTests.cs; interfaces/mcp/{AuditVerifyTool,McpServer}.cs, interfaces/cli/AuditVerifyCommand.cs, csproj links, tests/mcp/Program.cs, tests/revit-commands/Program.cs; scripts/{owner-catalogue-steps,owner-native-session}.ps1, tests/integration/{test-catalogue-session,fake-revit-command-executor}.ps1; board/features/contracts/attempts/owner doc/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); owner: pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Catalogue (Load Once).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P1-16/P1-17/P1-18/P1-19, P2-13 (relay181); then P5-12, P6-13, P7-12/P7-13 (relay183); then P3-11, P4-12/P4-13, P10-13 (relay184). Owner: ADR0010/ADR0011; one -Catalogue run (129 steps).
```
