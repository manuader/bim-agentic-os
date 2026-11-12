# Relay199: shared revit-command option grammar, fake journal diff = production differ, Flate + Form XObject PDF twin

Cloud scheduled relay, started 2026-11-12 12:55Z (LOCK was FREE after relay198). GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases with ready work: 1, 2 and 6 (relay195). P1-18 waited (shares owner-session/test files with P4-16/P4-17); P6-18 waited (serialized after P6-17, PDF fixtures). Workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (0 blockers, 3 should-fix, nits), findings closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P1-22 (Phase 1): Bim.Cli.RevitOptionGrammar (interfaces/cli/RevitCommandOptions.cs, linked into Bim.Mcp, tests/mcp, tests/revit-commands) is the single source of --read-timeout-seconds/--deadline-seconds grammar, ranges and refusal lines; CLI RevitCommandOptions.Parse and McpServer.ParseOptions delegate to it.
- P2-20 (Phase 2): the -ExternalEdits fake add-in's journal.read report.diff is a literal port of MirrorSnapshotDiffer (last non-peek baseline), member-equal to production; the P2-18 RawRead comparison now includes diff.
- P6-17 (Phase 6): brief-plan-xobject.pdf (FlateDecode streams, labels in a Form XObject) gives the same planner brief and the pinned layouts digest through the real CLI, with XObject provenance; no production change needed.
- Board: P1-22, P2-20, P6-17 ✅; P1-23 ⏳ (deps done); new P1-24 (one any-order option loop, internal types), P2-22 (fake baseline per document), P6-19 (nested/scaled Form XObject twin). Progress 171/190 (90.0 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-22 tests/mcp + tests/cli: red 43222777518, green 43224422380; review green 43239443477. 0 failed attempts.
- P2-20 tests/mirror-journal: red 43223243005, green 43225256279; review red 43233228746, green 43235982933. 1 failed attempt (test variable shadowed a parameter).
- P6-17 tests/cli: red 43223308971, green 43225242821; review green 43243827436. 0 failed attempts.
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; P4-11 / P1-14 / P4-14 owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. Optional: repository licence.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P1-22: any-order/at-most-once loop still per side; shared types public in Bim.Mcp.dll (→ P1-24); MCP has no --deadline-seconds (→ P1-23).
- P2-20: equality after re-serialization, not raw bytes; one baseline per fake state (→ P2-22); fake only.
- P6-17: one non-nested translate-only form, regions outside the form (→ P6-19); fixed-Huffman fixture only; generator rerun skipped on Windows.
ARCHIVOS CAMBIADOS: interfaces/cli/RevitCommandOptions.cs; interfaces/mcp/{McpServer.cs,Bim.Mcp.csproj}; tests/mcp/{Program.cs,Bim.Mcp.Tests.csproj}; tests/revit-commands/Bim.RevitCommands.Tests.csproj; tests/cli/{RevitCommandReadTimeoutTests,CadPdfToLayoutsTests}.cs; tests/integration/fake-revit-command-executor.ps1; tests/mirror-journal/MirrorDriftLoopChecks.cs; tests/cad/fixtures/{brief-plan-pdf.py,brief-plan-xobject.pdf (new)}; board/features/attempts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P3-15, P5-17/P7-16, P7-17 (relay197; P5-17 and P7-16 share the generate-typology parser); then P4-16/P4-17, P2-21, P10-17 (relay198); then P1-23 (then P1-24), P2-22, P6-18 (relay199; P1-18 shares owner-session files with P4-16/P4-17). Owner: ADR0010/ADR0011; one -Catalogue run.
```
