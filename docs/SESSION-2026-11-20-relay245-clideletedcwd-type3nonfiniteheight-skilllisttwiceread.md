# Relay245: CLI paths under a deleted cwd, Type3 warning at a non-finite height, skill-list twice-read

Relay245 (cloud scheduled, 2026-11-19 21:52Z) took the FREE LOCK, picked the oldest-touched phases 10 and 6 (relay242) and 5 (relay243), got three workers green, ran three independent read-only reviews (0 blockers; should-fixes closed with tests) and integrated through one integration head re-run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh). Workers refreshed the LOCK heartbeat during CI waits.

```
IMPLEMENTADO:
- P10-30 (Phase 10): CLI under a deleted working directory — relative inputs already typed (input_io_error, pinned as measured); relative outputs fixed: the shared OutputProblem GetFullPath catch also accepts FileNotFoundException → output_path_invalid (was *_internal_error or Program's generic catch). Covers audit-verify, doc-realized, doc-update, generate-prototypes, generate-typology, plan-layouts, export-gltf, cad-draft, cad-brief, cad-plan-brief, mirror-drift, mirror-drift-select, bcf-export (wrapped).
- P6-27 (Phase 6): Type3 glyph warning stays pending at a non-finite text height (finite position) and fires at the next emitting show with only its own names; equal direct font dictionaries warn as separate fonts (identity-keyed cache, documented); static check strips comments and literals (crafted self-test, swallow guard). Test-only.
- P5-28 (Phase 5): skill-list and jurisdiction-resolve twice in process and twice as real processes over one generated package print byte-identical success and sidecar-refusal lines while two in-process loads are unequal registries. Test-only.
- Board: P10-30, P6-27, P5-28 ✅; new P10-32, P6-31, P5-30. Progress 270/289 (93.4 %). Contracts and features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P10-30: probe red 44623774846, green 44626516529, review green 44631404385 (Linux and macOS; Windows SKIP). 0 failed attempts.
- P6-27: probe red 44623729030, green 44626900389, review red 44635725032, green 44638672966. 0 failed attempts.
- P5-28: red 44623782837, green 44626169431, review green 44641571014. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. Optional contract question: under a deleted cwd relative inputs report input_io_error rather than input_not_found/input_path_invalid (pinned as measured).
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P10-30: doc-update --probe-dependents not probed; a non-searchable cwd (EACCES → UnauthorizedAccessException) still unprobed → P10-32.
- P6-27: interpolation holes with quotes are a known stripper limit (swallow guard fails loudly); Form XObject /Matrix and Ts overflow; P6-25 check still on the old filter → P6-31.
- P5-28: the verbs' own registries are not observable (two test-side loads stand in); --lock and MCP tools → P5-30.
ARCHIVOS CAMBIADOS: interfaces/cli/OutputProblem.cs, tests/cli/DeletedCwdPathTests.cs, tests/cli/Program.cs; tests/cad/PdfType3GlyphListTests.cs; tests/cli/SkillListTests.cs; tasks.md, docs/features.json, docs/contracts/module-contracts.md, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P5-29, P1-35, P1-36, P7-24, P7-28 (relay243); then P2-34, P3-27, P3-28, P4-28 (relay244); then P10-31, P10-32, P6-30, P6-31, P5-30. GenerateTypologyWrite.cs edits serialize (P3-28); GenerateWrite.cs (P7-28) must update the P4-27 Run pin via a documented delta; McpServer.cs edits serialize (P10-31, P1-36 comment); tests/cli/SkillListTests.cs serializes (P5-29, P5-30); OutputProblem.cs (P10-32) is linked into Bim.Mcp.
```
