# Relay192: read-timeout knob on CLI and MCP, journal-driven drift loop, PDF drawing to ranked layouts

Cloud scheduled relay, started 2026-11-11 17:52Z (LOCK was FREE after relay191). GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases with ready work: 1, 2 and 6 (relay188). Workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (0 blockers, 5 should-fix, nits), every finding closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P1-20 (Phase 1): `revit-command … [--all] --read-timeout-seconds n` (interfaces/cli/RevitCommandOptions.cs) and `Bim.Mcp.dll --read-timeout-seconds n` / McpServer.ReadTimeout; 1..86400 s validated through PipeTransportWithReadTimeout; typed read_timeout_invalid / mcp_option_unknown added to RevitCommandCodes.ClientSide and the contract; refused before any I/O; MCP all:true page cut now relative to the server read timeout.
- P2-16 (Phase 2): tests/mirror-journal/MirrorDriftLoopChecks.cs gains a journal run (external move/create/delete → external_edit → adopt all → revision 2 → second drift 0) plus a no-journal negative; P2-14 run now asserts `unknown` classification. Tests only.
- P6-15 (Phase 6): tests/cad/fixtures/brief-plan.pdf + deterministic stdlib generator; tests/cli/CadPdfToLayoutsTests.cs runs the PDF chain through the real CLI to the same pinned layouts digest as the DXF chain.
- Board: P1-20, P2-16, P6-15 ✅; new P1-22 (shared CLI/MCP option parser), P2-18 (fake add-in journal.read returns external edits), P6-16 (PDF ToUnicode text), P6-17 (compressed + XObject PDF twin). Progress 153/172 (89.0 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-20 tests/cli + tests/mcp: red 43073359788, green 43075435525; review red 43083652387, green 43086519631. 0 failed attempts.
- P2-16 tests/mirror-journal: red 43073561189 (deliberate), green 43075645689; review green 43089718042. 0 failed attempts.
- P6-15 tests/cli: red 43076754909 (placeholder pin), green 43079861293; review green 43092611434. 1 failed attempt (wrong planner room count expectation).
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; P4-11 / P1-14 owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. Optional: repository licence.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P1-20: CLI and MCP parsers are separate copies (→ P1-22); MCP all:true overall deadline stays 1800 s; revit-command repeated/stray option detail text changed ("repeated option" / "unknown option", same code); Bim.Mcp.dll now refuses unknown command-line options (installer passes none).
- P2-16: journal hand-built by the test, fake journal.read empty (→ P2-18); add-in map update simulated; no hosted dependents or restore choices.
- P6-15: uncompressed content, no Form XObject or ToUnicode (→ P6-16, P6-17); generator rerun skipped on Windows; cad-brief itself does not refuse without a scale (P6-07), refusal asserted on cad-draft.
ARCHIVOS CAMBIADOS: interfaces/cli/{RevitCommandOptions,Program}.cs; interfaces/mcp/{McpServer,Program}.cs; core/Bim.Core/RevitCommands.cs (two ClientSide codes); tests/cli/{RevitCommandReadTimeoutTests,CadPdfToLayoutsTests,Program}.cs, tests/cli/Bim.Cli.Tests.csproj; tests/mcp/{Program.cs,Bim.Mcp.Tests.csproj}; tests/mirror-journal/MirrorDriftLoopChecks.cs; tests/cad/fixtures/{brief-plan.pdf,brief-plan-pdf.py}; board/features/contracts/attempts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P3-13, P5-15, P7-14/P7-15 (relay190; P7-14 and P7-15 share SkillPackLookup — serialize); then P4-14/P4-15, P2-17, P10-15 (relay191; P4-14 and P1-18 share tests/integration/test-catalogue-session.ps1 — serialize); then P1-18/P1-21/P1-22, P2-18, P6-16/P6-17 (relay192; P1-21 before P1-22, same parser). Owner: ADR0010/ADR0011; one -Catalogue run.
```
