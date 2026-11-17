# Relay221: skill-list loads once, Type3 warning only for shown codes, plan_layouts MCP internal/refused paths

Relay221 (cloud scheduled, 2026-11-17 14:53Z) took the FREE LOCK, picked the oldest-touched phases 5, 6 and 7 (relay217), got three workers green, ran three independent read-only reviews (0 blockers; should-fixes closed with tests on the task branches) and integrated through one integration head re-run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh).

```
IMPLEMENTADO:
- P5-23 (Phase 5): skill-list reads the skills directory once on success — one JurisdictionRegistry.Load supplies the catalogue and the stub claims; SkillLoader.LoadDirectory only inside the skills_invalid refusal block, to keep the loader's typed error (static check pins the conditional). Registry issues dropped (contract lists every code); culture-invariant bytes.
- P6-24 (Phase 6): Type3 encoding-glyph-unsupported moved from Tf to the first show operator (Tj/TJ/'/") that emits text and shows an affected code not mapped by ToUnicode; lists only those names; selected-but-unshown fonts silent; stray show outside BT leaves it pending; Form XObjects share the page state.
- P7-22 (Phase 7): plan_layouts MCP tool internal error (GuardedWorkProbe) and refused path (new internal GenerationWeightsProbe seam, null in production) byte-equal to the verb's in-process line; stderr diagnostic once / empty on refusal; literal refused line pinned; static pin that the probe is assigned only under tests/.
- Board: P5-23, P6-24, P7-22 ✅ (P7-22 file scope widened for the weights seam). New: P5-24, P6-25 (review nits). Progress 222/240 (92.5 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P5-23: red 44108572317, green 44110773912, review green 44123472372. 0 failed attempts.
- P6-24: red 44108546930, green 44111641935, review red 44128541644, green 44131276445. 0 failed attempts.
- P7-22: red 44108688858, green 44109616700, review green 44140731482. 0 failed attempts.
- macOS ifc-portable runner "not acquired" cancels re-run (no code change; not attempts).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P5-23: the refusal path still reads the directory twice (core has no public typed loader-error surface) (→ P5-24); culture sentinel proves a .NET child honours LANG, not the CLI itself (no culture-dependent output to probe).
- P6-24: duplicated IsGlyphListName filter in FontDecoder; two-resource-name and non-finite-position cases unpinned (→ P6-25); PdfWarning has no XObject path field.
- P7-22: "CLI line" is the in-process verb; the real-process link rests on tests/cli.
ARCHIVOS CAMBIADOS: interfaces/cli/{SkillListCommand,PlanLayoutsCommand}.cs; adapters/cad/Bim.Cad/PdfContent.cs; tests/cli/SkillListTests.cs; tests/cad/{PdfType3GlyphListTests,PdfNamedEncodingTests}.cs; tests/mcp/PlanLayoutsToolTests.cs; docs/contracts/module-contracts.md; docs/agent/ATTEMPTS.md; board/features/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P2-28, P3-21, P3-22 (relay219); P4-22/P4-23 (serialize on Bim.Mcp.csproj), P1-28, P10-25/P10-26 (relay220); then P5-22/P7-21 (McpServer.cs + tool-list pins — serialize), P5-24, P6-23, P6-25. Owner: ADR0010/ADR0011; one -Catalogue run.
```
