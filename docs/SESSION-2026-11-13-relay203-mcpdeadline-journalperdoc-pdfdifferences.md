# Relay203: MCP shared deadline transport, fake journal baseline per document, PDF /Differences

Cloud scheduled relay, started 2026-11-13 00:53Z (LOCK was FREE after relay201). GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases with ready work: 1, 2 and 6 (relay199). P1-24 and P6-19 waited (same McpServer file / PDF fixtures). Workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (0 blockers, 5 should-fix, nits), findings closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P1-23 (Phase 1): the private MCP DeadlineTransport is gone; McpServer.RevitCallAll builds the shared, now internal, RevitDeadlineTransport (linked source in Bim.Mcp, tests/mcp, tests/revit-commands; listing wording CliListing/McpListing; cutReads: realPipe so injected transports are never cut; probe passed through; ElapsedOn(clock)). QueryAllDeadlineExceeded = RevitDeadlineTransport.Code.
- P2-22 (Phase 2): the -ExternalEdits fake keeps the journal.read baseline per (document UniqueId, path), both case-insensitive like MirrorJournalTracker, and filters entries by UniqueId and path; the path without -ExternalEdits is unchanged.
- P6-18 (Phase 6): new PdfEncoding.cs (PdfSimpleEncoding: /BaseEncoding WinAnsi/MacRoman/Standard + /Differences; PdfGlyphNames: embedded Latin table, uniXXXX sequences, uXXXX..uXXXXXX, .suffix stripped). Simple fonts: ToUnicode → encoding → Latin-1; unknown glyph names keep byte decoding with a typed encoding-glyph-unknown warning (one per showing operator). Parsed encodings cached per document and /Differences charged to MaxParsedElements. New deterministic fixture differences.pdf (stdlib generator).
- Board: P1-23, P2-22, P6-18 ✅; P1-24, P6-19 ⏳; new P2-23 (fake journal failures per baseline), P6-20 (named simple-font encodings and Type3 glyph names). Progress 180/197 (91.4 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-23 tests/mcp (byte pins of the exceeded reply on the not-sent and cut paths, injected transport not cut, static check scoped to RevitCallAll, csproj link check): red 43340971765, green 43342471333; review green 43348417160. 0 failed attempts (one macOS build-step runner flake, rerun of the failed job passed unchanged).
- P2-22 tests/mirror-journal (two paths, switched UniqueId, case-insensitive UniqueId and path, report.diff = MirrorSnapshotDiffer, whole reply = RawRead over a two-document Core tracker): red 43342879679, green 43345132195; review green 43353102152. 0 failed oracle attempts.
- P6-18 tests/cad (fixture sha + generator determinism, decoded labels and brief rooms, base tables, glyph-name rules, ToUnicode precedence, unknown-name warning, cache/budget arithmetic): red 43341226832, green 43345036655; review red 43355607967, green 43357686671. 1 failed attempt (/bar is a real glyph name).
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. Optional: repository licence.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P1-23: `listing` is a free string (constants only used); MCP still refuses --deadline-seconds (mcp_option_unknown, by scope); pipe connect still not cut; no Revit run.
- P2-22: the fake still reports failures 0 / journal_complete true (→ P2-23); switching documents is a test-only lever (the fake has no document.open); fake evidence only.
- P6-18: name-only /Encoding (e.g. /WinAnsiEncoding) still Latin-1, Type3/symbolic fonts not distinguished, unknown /BaseEncoding silently ignored (→ P6-20); no /BaseEncoding falls back to Latin-1 (not Standard) on purpose; MacRoman includes Mac OS Roman extras; .notdef and f_f ligature names reported unknown; tounicode-invalid message text unchanged though the encoding now applies.
ARCHIVOS CAMBIADOS: interfaces/cli/RevitDeadlineTransport.cs; interfaces/mcp/{McpServer.cs,Bim.Mcp.csproj}; tests/mcp/{Program.cs,Bim.Mcp.Tests.csproj}; tests/revit-commands/Bim.RevitCommands.Tests.csproj; tests/integration/fake-revit-command-executor.ps1; tests/mirror-journal/MirrorDriftLoopChecks.cs; adapters/cad/Bim.Cad/{PdfEncoding.cs,PdfToUnicode.cs,PdfContent.cs}; tests/cad/{PdfEncodingTests.cs,Program.cs,fixtures/differences-pdf.py,fixtures/differences.pdf}; board/features/contracts/attempts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P3-16, P5-18 or P7-16, P10-18 (relay200; P10-18/P10-19 share package-common.ps1); then P4-17/P4-18 (serialize), P10-19 (relay201); then P1-24, P2-23, P6-19 then P6-20 (relay203). Owner: ADR0010/ADR0011; one -Catalogue run.
```
