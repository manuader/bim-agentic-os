# Relay238: cut-connect read bounded by the time left, mid-args broken pipe pinned, CLI generate-prototypes defects typed

Relay238 (cloud scheduled, 2026-11-18 21:53Z) took the FREE LOCK, picked the oldest-touched phases 1, 2 and 7 (relay232), got three workers green, ran three independent read-only reviews (0 blockers on P1-32 and P7-25; P2-31 flagged a scope gap that is recorded as a deviation, not hidden) and integrated through one integration head re-run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh). The container restarted once during the review round; workers' branches were already pushed, so nothing was lost.

```
IMPLEMENTADO:
- P1-32 (Phase 1): core RevitCommandService.PipeTransportWithReadAfterConnect(connect, Func<TimeSpan>) asks for the read timeout after pipe.Connect (< 1 ms left → line not written, RevitPipeReadTimeoutException(0), read-only deadline use only). RevitDeadlineTransport sends cut-connect pages through it, so an add-in accepting just before the deadline and never replying ends query_all_deadline_exceeded within deadline + 300 ms. Defect fixed (was ~deadline + 800 ms). Default path, wire bytes and protocol unchanged; MCP all:true inherits it.
- P2-31 (Phase 2): tests only — fake -BreakAfterBytes N reads at most N bytes, measures the bytes queued at its server end (PeekNamedPipe / FIONREAD) and closes; a 39.7k-byte floor.create broken at 4096 bytes inside args gives exit 1 pipe_io_error / pipe_name / IOException on Linux and Windows, nothing executed (snapshot, journal, RawRead), control run executes.
- P7-25 (Phase 7): GenerateCommand.Run catches non-refusal exceptions outside the guarded block → typed generate_prototypes_internal_error, exit 1, diagnostic once, no file (same pattern as P3-24). Defect fixed (was Program's generic catch).
- Board: P1-32, P2-31, P7-25 ✅; new P1-34, P2-32, P7-26. Progress 249/268 (92.9 %). Contracts (deadline transport) and features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-32: red 44406681679 (1801 ms), green 44409084315, review green 44414996684. 0 failed attempts.
- P2-31: probe red 44407063531, green 44410698517, review probe red 44424268750, green 44426905071. 0 failed attempts.
- P7-25: red 44406469298, green 44408681950, review green 44422608303. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: P2-31 measured read 4096, unread 35628 (Linux) / 35619 (Windows), queued == unread on both; Linux Unix send buffer 212992 > 65537-byte request cap.
GAPS CONOCIDOS:
- P2-31 DEVIATION: the row's "unsent remainder exceeds the OS pipe buffer" is not shown. Linux cannot reach it (cap below the send buffer); on Windows the server-side count cannot separate a pending client write from buffered bytes → P2-32 (client-side write-progress seam).
- P1-32: pages whose connect is not cut (≥ 5 s left) still measure the read before the connect (overrun ≤ connect time) → P1-34; PipeTransportWithTimeouts and the legacy connectTransport seam are only used by P1-30/P1-31 tests.
- P7-25: an exception after GltfExporter.WriteNew leaves the written file → P7-26.
ARCHIVOS CAMBIADOS: core/Bim.Core/RevitCommandsService.cs, interfaces/cli/RevitDeadlineTransport.cs, tests/cli/RevitCommandDeadlineTests.cs; tests/integration/fake-revit-command-pipe.ps1, tests/mirror-journal/MirrorDriftLoopChecks.cs; interfaces/cli/GenerateCommand.cs, tests/cli/GeneratePrototypesTests.cs; tasks.md, docs/features.json, docs/contracts/module-contracts.md, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P3-22, P3-25, P4-25, P10-27, P10-29 (relay233); then P5-22, P5-27, P6-27, P6-28, P1-33 (relay236); then P7-24, P7-26, P1-34, P2-32. McpServer.cs edits serialize (P5-22, P10-27); csproj edits serialize (P4-25). Owner: ADR0010/ADR0011; one -Catalogue run.
```
