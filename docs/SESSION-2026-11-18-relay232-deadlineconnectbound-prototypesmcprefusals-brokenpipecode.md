# Relay232: `--all` deadline bounds the pipe connect, generate_prototypes MCP refusal coverage, broken-before-read pipe code pinned

Relay232 (cloud scheduled, 2026-11-18 11:52Z) took the FREE LOCK, picked the oldest-touched phases 1, 7 (relay226) and 2 (relay227), got three workers green, ran three independent read-only reviews (0 blockers; should-fixes and nits closed with tests on the task branches) and integrated through one integration head re-run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh). Relay233 correctly backed off at 13:53Z while relay232 was in its review round (heartbeat 12:50Z).

```
IMPLEMENTADO:
- P1-30 (Phase 1): RevitCommandService.PipeTransportWithTimeouts(read, connect) (connect 1 ms..5 s, rounded up); RevitDeadlineTransport cuts a page's connect to the time left when it is below 5 s and maps that pipe_connect_timeout to query_all_deadline_exceeded. Absent pipe: --deadline-seconds 1/2/5 end query_all_deadline_exceeded within N + 1 s (was N + 5 s); 7 ends pipe_connect_timeout after 5 s; MCP all:true shares it (static check + real-pipe run); no-deadline path byte- and time-identical. Stale "connect is not cut" comments fixed in Program.cs, RevitCommandOptions.cs, McpServer.cs and guarded by a static check.
- P7-23 (Phase 7): generate_prototypes tool section 8 — infeasible lot byte-equal to the CLI file; stdio refusals over the real Bim.Mcp.dll; pack_invalid (json_invalid / id) and repository_not_found from scratch repository, scratch package and outside any repository, literal bytes, mandatory on CI; quiet -32602/lone-surrogate calls. Defect fixed: a non-refusal exception before the guarded block (GenerateCommand.UnguardedStageProbe, null in production) now returns typed generate_prototypes_internal_error instead of McpServer's generic failure with the exception message.
- P2-30 (Phase 2): fake -BreakPipeCommand closes after the envelope's own command member (anchored `"command":"<name>","document":`); ten large element.move requests → exit 1, pipe_io_error / pipe_name / IOException on Linux and Windows, nothing executed; a command.batch with an element.move step is served normally.
- Board: P1-30, P7-23, P2-30 ✅; new P1-32 (read measured after connect), P2-31 (break mid-args), P7-25 (CLI generate-prototypes parse/load defects typed). Progress 240/259 (92.7 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-30: red 44299240575, green 44301595888, review red 44307960804, green 44310402689. 0 failed attempts.
- P7-23: red 44301138736, green 44302746005, review green 44314549993. 0 failed attempts (+1 build-only AD0001 run 44299479480).
- P2-30: probe red 44299538961, green 44303037494, review green 44319195275. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: absent-pipe `--all --deadline-seconds 1` now ends at ~1.06 s on CI (was ~6 s).
GAPS CONOCIDOS:
- P1-30: a connect that succeeds just before the deadline keeps the read timeout measured before it (overrun < 5 s) → P1-32.
- P7-23: CLI Run path still reaches Program's generic catch for the same forced exception → P7-25; tests/mcp runs on Linux/Windows only; the scratch package is the Release output, not scripts/package.ps1 (→ P7-24).
- P2-30: args precede command, so only the tail is unread at the break; write- vs read-side failures share the IOException detail → P2-31.
ARCHIVOS CAMBIADOS: core/Bim.Core/RevitCommandsService.cs, interfaces/cli/RevitDeadlineTransport.cs, interfaces/cli/Program.cs (comment), interfaces/cli/RevitCommandOptions.cs (comment), interfaces/mcp/McpServer.cs (comments), tests/cli/RevitCommandDeadlineTests.cs; interfaces/cli/GenerateCommand.cs, tests/mcp/GeneratePrototypesToolTests.cs; tests/integration/fake-revit-command-pipe.ps1, tests/mirror-journal/MirrorDriftLoopChecks.cs; docs/contracts/module-contracts.md; docs/agent/ATTEMPTS.md; board/features/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P3-22, P3-24, P10-27, P10-28 (relay227); then P4-24, P5-22/P5-26, P6-26/P6-27 (relay229); then P1-31, P1-32, P2-31, P7-24, P7-25. McpServer.cs edits serialize (P5-22, P10-27). Owner: ADR0010/ADR0011; one -Catalogue run.
```
