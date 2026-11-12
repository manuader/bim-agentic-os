# Relay195: CLI --all deadline, fake add-in external journal, PDF ToUnicode

Cloud scheduled relay, started 2026-11-12 03:53Z (LOCK was FREE after relay194). GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases with ready work: 1, 2 and 6 (relay192). Workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (1 blocker, 11 should-fix, nits), every finding closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P1-21 (Phase 1): `revit-command … --all --deadline-seconds n` bounds the whole paging run like MCP all:true (query_all_deadline_exceeded in the listing shape, real-pipe read cut to the time left, typed deadline_invalid before any I/O, absent flag byte-identical).
- P2-18 (Phase 2): fake add-in `-ExternalEdits` journals external move/create/delete and answers journal.read like RevitJournalQuery.RawRead (peek, after_sequence, paging, baseline); the P2-16 drift loop reads its journal through the pipe.
- P6-16 (Phase 6): PDF ToUnicode CMaps decode Type0/simple-font text, so brief extraction reads "Dormitorio 1", "Baño", "Cocina" from CAD-style PDFs; typed tounicode-* warnings; document-wide bounds.
- Board: P1-21, P2-18, P6-16 ✅; new P1-23 (MCP reuses the CLI deadline transport, ⛔ P1-22), P2-20 (fake journal diff = production differ), P6-18 (simple-font /Differences glyph names). Progress 162/181 (89.5 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-21 tests/cli: red 43151595118, green 43152122711; review green 43163347980, 43165743640. 0 failed attempts.
- P2-18 tests/mirror-journal: red 43151779386, green 43153831165; review red 43159370704, green 43161374794. 0 failed attempts.
- P6-16 tests/cad: red 43151778821, green 43153070188; review red 43156979215, green 43157029343. 0 failed attempts.
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; P4-11 / P1-14 owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. Optional: repository licence.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P1-21: 5 s pipe connect not cut by the deadline (documented, pinned); rule copied from MCP (→ P1-23); usage line in older refusals unchanged (→ P1-22).
- P2-18: fake report.diff not compared with production (→ P2-20); global baseline; semantic, not byte-level escaping fidelity.
- P6-16: /Differences encodings (→ P6-18); usecmap ignored; cad-brief CLI not run on tounicode.pdf (compressed/XObject CLI twin → P6-17).
ARCHIVOS CAMBIADOS: interfaces/cli/{RevitDeadlineTransport,RevitCommandOptions,Program}.cs; tests/cli/{RevitCommandDeadlineTests,Program}.cs, Bim.Cli.Tests.csproj; tests/integration/fake-revit-command-{executor,pipe}.ps1; tests/mirror-journal/MirrorDriftLoopChecks.cs; adapters/cad/Bim.Cad/{PdfToUnicode,PdfContent,PdfReader,PdfModel}.cs; tests/cad/{PdfToUnicodeTests,Program}.cs, fixtures/tounicode{-pdf.py,.pdf}; board/features/contracts/attempts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P3-14, P5-16/P7-16, P7-15 (relay193; P5-16 and P7-16 share GenerateTypologyCommand — serialize); then P4-14, P2-19, P10-16 (relay194; P4-14 and P1-18 share tests/integration/test-catalogue-session.ps1 — serialize); then P1-18/P1-22, P2-20, P6-17/P6-18 (relay195; P6-17/P6-18 share PDF fixtures — serialize). Owner: ADR0010/ADR0011; one -Catalogue run.
```
