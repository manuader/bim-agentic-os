# Relay219: real catch-all forcing, bcf-export refusal edges, pipe fake gate exit

Relay219 (cloud scheduled, 2026-11-17 10:52Z) took the FREE LOCK, picked the oldest-touched phases 3, 10 (relay215) and 2 (relay216), got three workers green with 0 failed attempts, ran three independent read-only reviews, closed the findings at integration and integrated through one integration head re-run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh; dot.net blocked by the proxy).

```
IMPLEMENTADO:
- P3-20 (Phase 3): internal static test seam GuardedWorkProbe (null in production, first statement in the guarded try) in generate-prototypes, generate-typology, plan-layouts and doc-realized; each verb forced through its real Run catch-all, bytes equal to the P3-19 fixtures. MCP AuditVerifyTool's internal-error stderr line now IntentPlan.InternalDiagnostic("audit_verify", e), byte-equal, forced in process. Phase 3 board batch complete (20/20) → next batch loaded.
- P10-23 (Phase 10): bcf-export timestamps must be Z or ±hh:mm in ASCII digits (+0200, +2:00, +02 refused at $.project.timestamp); version literal 1 (1.0/1e0 — already refused by TryGetInt32, now pinned); NUL input path → input_path_invalid in process; golden SHA-256 read from tests/bcf instead of copied.
- P2-27 (Phase 2): pipe fake gate and nope:1 runs exit exactly 1; explicit dry_run:false write with a scheduled id injects (RawRead equality); fake header documents -DropReplyCommand vs dry runs and scheduled failures.
- Board: P3-20, P10-23, P2-27 ✅. New: P3-21 (MCP internal envelopes from IntentPlan + generate_typology Document forced), P3-22 (seam hygiene), P2-28 (gate line pinned exactly), P10-25 (bcf test hygiene). Progress 216/235 (91.9 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P3-20: red 44069454018, green 44072511210 (macOS job re-run after runner cancel). 0 failed attempts.
- P10-23: red 44069314155, green 44071743670. 0 failed attempts.
- P2-27: probe red 44069404671 (exactly the 5 new checks), green 44072348707. 0 failed attempts.
- Reviews (independent, read-only): 0 code blockers. P2-27: fake header criterion was outside the row's files → added at integration. P3-20: stale oracle header → fixed + post-reset null pin. P10-23: version change is behaviour-neutral (recorded as pinned); tests/cli csproj link accepted as extra file.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P3-20: GuardedWorkProbe is mutable internal static state on production types; MCP generate_typology Document path not forced; AuditVerifyTool still builds its own failed envelope (→ P3-21, P3-22).
- P10-23: trailing-newline timestamp refused but unpinned; NUL test does not capture stderr (→ P10-25).
- P2-27: exit 1 alone cannot tell the gate from an uncaught throw (stderr narrows it) (→ P2-28).
ARCHIVOS CAMBIADOS: interfaces/cli/{GenerateCommand,GenerateTypologyCommand,PlanLayoutsCommand,DocRealizedCommand,BcfExportCommand}.cs; interfaces/mcp/AuditVerifyTool.cs; tests/cli/{InternalErrorDelegationTests,BcfExportCommandTests}.cs, tests/cli/Bim.Cli.Tests.csproj; tests/mirror-journal/MirrorDriftLoopChecks.cs; tests/integration/fake-revit-command-pipe.ps1 (header comment); docs/contracts/module-contracts.md; board/features/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P4-21/P4-22, P1-27 (relay216); P10-24; then P5-22 and P7-21 (McpServer.cs + tool-list pins + 4 csprojs — serialize), P5-23, P6-23/P6-24, P7-22; new P2-28, P3-21, P3-22, P10-25. Owner: ADR0010/ADR0011; one -Catalogue run.
```
