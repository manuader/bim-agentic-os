# Relay178: audit-verify CLI, intent edits to element edits, query.elements --all paging

Cloud scheduled relay, started 2026-11-10 11:53Z (LOCK was FREE after relay176). GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases with ready work: 10 (relay169), 3 and 1 (relay174). Phase 8's remaining task is 🔒 Blender and phase 9's only open task, P9-03, needed its design ADR first, so the controller wrote ADR0011 (guarded free path) as `proposed`. Workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (1 blocker, 9 should-fix, 1 oracle-scope decision), every blocker/should-fix and the cheap NITs closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P10-11 (Phase 10): `audit-verify log.jsonl [--anchor head.json] [--actors a,b] [--summary] [--write-anchor head.json]` — P10-09 report/summary as canonical JSON, exit 1 on any finding; never-overwrite head anchor {kind audit-anchor, version 1, sequence, hash} written only on a clean log, so a later tail cut is audit_truncated and a full rehash audit_anchor_mismatch. Review fixed a P10-09 hang (seq gaps counted arithmetically) and bounded the report (10000 findings + findings_omitted).
- P3-09 (Phase 3): IntentEdits.From / FromStored — minimal ElementEdit set from two layout intents (or a stored project and an intent); ResizeRoom, MoveOpening, ChangeWallType helpers; compiler refusals pass through.
- P1-15 (Phase 1): RevitElementQueryPaging.All + `revit-command envelope.json pipe --all` — follows next_cursor with only query.elements, strict page/receipt checks, cursor_loop, page_limit_exceeded, listing_too_large.
- P9-03 (Phase 9): ADR0011 guarded free path written proposed (Roslyn in-process, symbol allowlist, rolled-back group, confirm-token commit, limits, audit logging); P9-03 ⛔ until accepted.
- Board: P10-11, P3-09, P1-15 ✅; P9-03 ⛔; new P3-10, P3-11⛔, P10-12, P10-13. Progress 123/142 (86.6 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P10-11 tests/cli + tests/audit: red 42811823793, green 42812956696; review red 42827752441, green 42829463239. 0 failed attempts.
- P3-09 tests/local-validation (3 seeds × 200 chained steps, store chaining): red 42812044156, green 42815451044; review red 42833038699, green 42834677864. 0 failed attempts.
- P1-15 tests/revit-commands + headless CLI on the fake pipe: red 42820900875, green 42824288044; review red 42838230156, green 42842909138. 1 failed attempt (CLI test document path).
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P9-03 — needs owner acceptance of ADR0011 (ADR0009 requires a design ADR before code). P3-09 full byte identity — P3-05 appends creates (P3-10 filed).
DECISIONES QUE NECESITO DE VOS: ADR0011 (recommended A); ADR0010 (recommended A); still open — escritorio/estudio/biblioteca class; Uniclass excerpt licence; party-wall facade edges.
BENCHMARKS / NÚMEROS REALES: P3-09 corpus (red-run annotations): 389 accepted steps — resize 109 accepted/92 refused, move 160/49, wall type 120/70.
GAPS CONOCIDOS:
- P10-11: no MCP tool (P10-12); comma in actor ids; shared read handle may make a concurrent Append fail with audit_locked; JSON-looking malformed lines cost one parser exception each.
- P3-09: plan byte identity only when no create sorts before a survivor (P3-10); O(project) per call.
- P1-15: never run in Revit; no MCP equivalent; no pipe read timeout (pre-existing); positions not snapshot.
ARCHIVOS CAMBIADOS: interfaces/cli/{AuditVerifyCommand,Program}.cs, core/Bim.Core/{AuditVerify,IntentEdits,RevitCommandsQueryPaging}.cs, tests/cli/{AuditVerifyTests,Program}.cs, tests/audit/Program.cs, tests/local-validation/{IntentEditsTests,Program}.cs, tests/revit-commands/{ElementQueryPaging,Program}.cs, tests/integration/test-catalogue-session.ps1, docs/architecture/adr/0011-guarded-free-path.md; board/features/contracts/attempts/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); dotnet run --project tests/cli; dotnet run --project tests/local-validation; dotnet run --project tests/revit-commands.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P4-10, P1-14 (relay174); P2-12, P5-12/P5-13, P6-12, P7-11/P7-12 (relay176); then P3-10, P10-12/P10-13 (relay178). Owner: accept ADR0011 to unblock the free path.
```
