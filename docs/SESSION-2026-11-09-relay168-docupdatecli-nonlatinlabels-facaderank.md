# Relay168: doc-update CLI, non-Latin brief labels, facade ranking

Cloud scheduled relay, started 2026-11-09 16:53Z. relay167 had held the LOCK since 2026-11-05 20:53Z without closing (stale >3 days; it left only the P7-10 pin commit on relay167-P7-10), so the lock was taken over. GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases 4, 6, 7: P4-08, P6-11 and P7-10 (continuing relay167's branch). Three workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (1 blocker in P6-11), every blocker/should-fix and the NITs closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P4-08 (Phase 4): `doc-update --before --after --realized --bindings --output [--dependents]` dry-plan CLI verb over DocUpdate.Plan — byte-identical to the library, no transport; bounded inputs (8 MiB, 100,000 entries), strict JSON, never-overwrite, typed envelope exit 1 and no file on failure. Needs --bindings (DocUpdate.Plan requires RevitBindings); board Done-when amended.
- P6-11 (Phase 6): labels written only in non-Latin script words (Cyrillic, Greek, CJK, …) become brief rooms with a hashed fallback id room_<hex16>; Greek case/accent variants group; Ø/%%c, º/ª, ß, µ, lone Greek letters still refused; all previous outputs pinned byte for byte.
- P7-10 (Phase 7): optional PrototypePipelineOptions.FacadeWeight adds an explained `facade` criterion (window margin over the CABA illumination/ventilation ratios) and re-ranks accepted alternatives; weight 0/absent keeps the P7-07 bytes.
- Board: P4-08, P6-11, P7-10 ✅. No new batch needed (phases 4, 6, 7 still have P4-09, P6-10, P7-09 ⏳).
TESTEADO AUTOMÁTICAMENTE (backend):
- P4-08 tests/cli: red 42660367951, green 42662042513; review red 42680566316, green 42682045940. 0 failed attempts.
- P6-11 tests/cad: red 42660083366, green 42665024555; review red 42669852315/42672669522, green 42674160689. 0 failed attempts.
- P7-10 tests/generative: red 42660034271, green 42662455732; review red 42684396478, green 42687195714. 0 failed attempts.
- Merged integration head re-run in CI before main moved (see journal).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: nothing new blocked.
DECISIONES QUE NECESITO DE VOS: nothing new; still open — escritorio/estudio/biblioteca class; Uniclass excerpt licence; party-wall facade edges.
BENCHMARKS / NÚMEROS REALES: facade criterion at weight 0.5 re-ranks 0 of 4 real prototype requests (windows sized to the minimum).
GAPS CONOCIDOS:
- P4-08: dry plan only (no run verb); output is default System.Text.Json of DocUpdatePlan; before/after parsed leniently like diff-edits.
- P6-11: no non-Latin vocabulary or area units; one-letter CJK names not labels; mixed-script labels slug to the Latin part (can false-duplicate); fixed Unicode ranges; synthetic DXF only.
- P7-10: weak signal on real prototypes; area margin only (no orientation/sun/view); FacadeWeight not on a CLI yet (P7-09).
ARCHIVOS CAMBIADOS: interfaces/cli/{DocUpdateCommand,Program}.cs; tests/cli/Program.cs; adapters/cad/Bim.Cad/BriefExtract.cs; tests/cad/{BriefNonAsciiTests,Program}.cs; core/Bim.Generative/{PrototypePipeline,PrototypePipelineModel,PrototypePipelineSerializer}.cs; tests/generative/PrototypePipelineTests.cs; docs/contracts/module-contracts.md; board/features/attempts/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); dotnet run --project tests/cli; dotnet run --project tests/cad; dotnet run --project tests/generative.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P2-10/P2-11, P5-10/P5-11, P10-10/P10-11 (relay164); then P8-09, P9-03/P9-08, P1-12/P1-13, P3-08/P3-09 (relay166; check origin chat-* branches before any Revit command file); then P4-09, P6-10, P7-09 (relay168).
```
