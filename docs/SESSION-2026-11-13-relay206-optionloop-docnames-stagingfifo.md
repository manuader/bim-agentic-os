# Relay205/206: one option loop, documentation name collisions, package staging special files

Relay205 (cloud scheduled, 08:55Z) picked the oldest-touched phases 1, 4 and 10 and got all three workers green, then stopped heartbeating at 09:52Z (container lost) after one P4-17 review fix. Relay206 (cloud scheduled, 10:56Z) took the stale LOCK, ran independent read-only reviews on all three heads, sent the findings to workers on the same branches, and integrated. GitHub CI was the only oracle (no local .NET SDK or pwsh).

```
IMPLEMENTADO:
- P1-24 (Phase 1): RevitOptionGrammar.Options is the single at-most-once/any-order option loop used by revit-command (RevitCommandOptions.Parse) and the MCP server (McpServer.ParseOptions); RevitOptionGrammar, RevitCommandOptions and RevitOptionValues are internal.
- P4-17 (Phase 4): the owner -Catalogue documentation update lists existing views, sheets and schedules read-only (query.elements, next_cursor paging, capped) and refuses doc-up-realize with doc_update_name_collision (or doc_update_plan_unnamed) before anything is sent.
- P10-19 (Phase 10): package staging (forbidden scan, manifest and SBOM builders) refuses FIFO/socket/device and links to them by POSIX type before any read or hash (package_special_file).
- Board: P1-24, P4-17, P10-19 ✅. Phase 10 was 19/19, so its next batch is loaded: P10-20 (bcf-export CLI verb), P10-21 (jurisdiction-resolve CLI verb), P10-22 (typed dangling links in package staging). Progress 186/202 (92.1 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-24 tests/mcp RevitOptionLoopTests (19 CLI + 11 MCP refusal byte pins, some through the real DLLs; static one-loop check; internal-type metadata check on Bim.Cli.dll, Bim.Mcp.dll, tests/mcp): red 43397635208, green 43398967732; review green 43413447598. 0 failed attempts.
- P4-17 tests/integration/test-catalogue-session.ps1 (sheet-only, partial three-way case-insensitive, cross-listing, missing parameter, truncated, 2-page listing with the collision on page 2, endless listing capped at 20 pages, repeated cursor, unnamed view/sheet/schedule creates; nothing sent, later groups run, clean run unchanged): red 43397774986, green 43399133841; reviews green 43406208212 and 43418296675 (red 43413886113). 1 failed test-side attempt.
- P10-19 tests/packaging/test-package.ps1 (bounded child-pwsh FIFO/link-to-FIFO/socket cases for both builders and the forbidden scan, check-failure injection, sbom.json and manifest.json re-derived byte-equal): red 43397435722, green 43401512169; review red 43413538810, green 43416253764. 0 failed oracle attempts.
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒 (now also checks that real OST_Views rows return "View Name").
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. Optional: repository licence.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P1-24: the one-loop static check is per file; MCP out readTimeout after a refusal is the default (unobservable).
- P4-17: never run in Revit — if real OST_Views rows lack "View Name" every run fails closed; paging covered only by the headless fake; > 100 000 rows per category fails closed; names not trimmed; stricter than Revit's per-view-type uniqueness by design.
- P10-19: FIFO cases Linux only (Windows SKIPs; macOS not run by verify.ps1); dangling link still an untyped Get-FileHash exception (→ P10-22); TOCTOU after the check not covered.
ARCHIVOS CAMBIADOS: interfaces/cli/RevitCommandOptions.cs; interfaces/mcp/McpServer.cs; tests/mcp/{Program.cs,RevitOptionLoopTests.cs}; scripts/{owner-catalogue-steps.ps1,package-common.ps1}; tests/integration/test-catalogue-session.ps1; tests/packaging/test-package.ps1; board/features/contracts/attempts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P2-23, P6-19 then P6-20 (relay203); P3-17, P5-18, P7-18 (relay204; P5-18/P7-18 serialize on tests/mcp); P4-18 (P4-17 done, may start), P10-20/P10-21/P10-22. Owner: ADR0010/ADR0011; one -Catalogue run.
```
