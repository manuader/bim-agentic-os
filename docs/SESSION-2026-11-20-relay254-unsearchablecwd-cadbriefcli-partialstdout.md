# Relay254: unsearchable cwd, cad-brief CLI pin, generate-prototypes partial stdout

Relay254 (cloud scheduled, 2026-11-20 21:53Z) took the FREE LOCK, picked the oldest-touched phases 10, 6 and 7 (relay248/251), got three workers green, ran one independent read-only review of all three branches (all approve, 0 blockers; nits recorded as follow-up tasks) and integrated through one integration head run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh).

```
IMPLEMENTADO:
- P10-32 (Phase 10): interfaces/cli/OutputProblem.cs GetFullPath catch also accepts UnauthorizedAccessException → output_path_invalid. DEFECT FIXED (macOS): a relative output under a mode-000 cwd or parent reached Program's generic catch ("Access to the path is denied." as code) or *_internal_error. doc-update --probe-dependents under a deleted cwd pinned (already typed). 19 cases × 2 modes.
- P6-32 (Phase 6): test-only. cad-brief CLI bytes == in-process BriefExtract for the P6-30 drawing (digests pinned); dropped-insert reader warnings absent from the brief pinned as a documented limit with reason; bulge outline + nested near-±Z chains equal ezdxf v1.4.2 within 1e-6; offline generator extended.
- P7-32 (Phase 7): test-only. generate-prototypes partial stdout write → truncated prefix, exit 1, nothing escapes, no file (refusal, catch-all, success, pipeline refusal); disposed writer via counting writer; InternalErrorDelegationTests 2e labels describe generate-prototypes/generate-typology as contained.
- Board: P10-32, P6-32, P7-32 ✅; new P10-35, P6-34, P7-34. Progress 297/316 (94 %). Contracts and features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P10-32: probe red 44824612295, green 44827690045. 0 failed attempts.
- P6-32: probe red 44825236825, green 44828277256. 0 failed attempts.
- P7-32: probe red 44824948452, green 44828232941. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. P6-34 will propose (not implement) an opt-in for surfacing dropped-insert reader warnings in briefs.
BENCHMARKS / NÚMEROS REALES: none.
GAPS CONOCIDOS:
- P10-32: Linux cwd-mode output lines pinned via an absolute reference run, not literals; Linux mode-000 cwd output refused only at the write; MCP PathProblem not probed → P10-35.
- P6-32: reader warnings excluded from briefs (limit); deeper chains / rows×columns MINSERT / D1 sign parity; CLI precondition message → P6-34.
- P7-32: exit-code-authoritative contract wording, no-retry static pin, CountingWriter overloads → P7-34.
ARCHIVOS CAMBIADOS: interfaces/cli/OutputProblem.cs, tests/cli/DeletedCwdPathTests.cs; tests/cad/MinsertEzdxfTests.cs, tests/cad/tools/minsert_ezdxf_centres.py; tests/cli/GeneratePrototypesTests.cs, tests/cli/InternalErrorDelegationTests.cs; tasks.md, docs/features.json, docs/contracts/module-contracts.md, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P1-38, P1-39, P5-32, P5-33, P7-33 (relay252); then P2-37, P3-29, P3-31, P4-31 (relay253); then P10-34, P10-35, P6-33, P6-34, P7-34.
```
