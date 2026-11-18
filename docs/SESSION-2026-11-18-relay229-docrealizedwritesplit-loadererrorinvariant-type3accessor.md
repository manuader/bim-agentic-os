# Relay229: doc-realized write path split from the MCP binary, JurisdictionLoad.LoaderError invariant, Type3 per-code glyph-list accessor

Relay229 (cloud scheduled, 2026-11-18 07:53Z) took the FREE LOCK, picked the oldest-touched phases 4, 6 (relay225) and 5 (relay226), got three workers green, ran three independent read-only reviews (0 blockers; should-fixes on P4-23 and nits on P5-25/P6-25 closed with tests on the task branches) and integrated through one integration head re-run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh). Relay230 and relay231 correctly backed off while relay229 worked; the heartbeat was refreshed at task edges but not between them during the long review round.

```
IMPLEMENTADO:
- P4-23 (Phase 4): DocRealizedCommand split into a partial class; the verb's write path (Usage/Flags/Run/Execute/TryParse/Fail) moved verbatim to new interfaces/cli/DocRealizedWrite.cs, compiled by the CLI (and tests/cli) only. Bim.Mcp/Bim.Mcp.Tests link only the shared Build/DryRealized part — wildcard-aware XML link check (DocUpdateLinkTests resolver) and forbidden DocRealizedCommand.Run/Execute/Fail regex with self-tests. In-process tool calls run with Console.Out redirected to an empty StringWriter; precedence and Build-sharing checks are comment/whitespace-insensitive; tests/cli DocRealizedTests guard now reads code of both parts.
- P5-25 (Phase 5): JurisdictionLoad.LoaderError get-only, set only via internal JurisdictionLoad.SkillsInvalid (Bim.Core + its InternalsVisibleTo test assemblies); a with { Refusal = other } copy reads null; success and json_invalid sidecar refusals null, skills_invalid from Load/LoadLocked non-null; contract sentence; bytes unchanged.
- P6-25 (Phase 6): PdfSimpleEncoding.UnsupportedGlyphListName(byte) is the only glyph-list filter (static body pin + per-code unit test; [assembly: InternalsVisibleTo("Bim.Cad.Tests")] in PdfEncoding.cs); one dictionary under /F1 and /F3 warns once named by the active resource (both orders); a non-finite-position show (1e300 CTM × 1e60 Tm) leaves the warning pending.
- Board: P4-23, P5-25, P6-25 ✅; new P4-24 (MCP without gltf-export writer), P5-26 (JurisdictionLoad shape invariants), P6-27 (non-finite height, duplicate direct dicts). Progress 237/256 (92.6 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P4-23: red 44264455046, green 44266871414, review red 44271389423, green 44274255458. 1 failed attempt (Console.SetOut wrapper).
- P5-25: red 44264137650, green 44265916987, review green 44283677270. 0 failed attempts.
- P6-25: red 44262688319, green 44265801905, review compile red 44278187304, green 44280751904. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P4-23: Bim.Mcp still compiles the gltf-export/generate/plan-layouts GltfExporter writers via GltfExportCommand.cs (OutputProblem) → P4-24; the forbidden-use scan reads only the default interfaces/mcp glob.
- P5-25: record equality includes the hidden loader fields; Registry/Refusal exclusivity not enforced → P5-26.
- P6-25: non-finite text height not pinned; duplicate direct dictionaries untested; static check strips only whole-line comments → P6-27.
ARCHIVOS CAMBIADOS: interfaces/cli/DocRealizedCommand.cs, interfaces/cli/DocRealizedWrite.cs (new), interfaces/mcp/DocRealizedTool.cs (comment), tests/cli/Bim.Cli.Tests.csproj, tests/cli/DocRealizedTests.cs, tests/mcp/DocRealizedToolTests.cs; core/Bim.Core/JurisdictionRegistry.cs, core/Bim.Core/JurisdictionLock.cs, tests/Bim.Tests/Program.cs; adapters/cad/Bim.Cad/PdfEncoding.cs, adapters/cad/Bim.Cad/PdfContent.cs, tests/cad/PdfType3GlyphListTests.cs; docs/contracts/module-contracts.md; docs/agent/ATTEMPTS.md; board/features/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P7-23, P7-24, P1-30, P1-31 (relay226; McpServer.cs edits serialize: P5-22, P7-23, P10-27); then P2-30, P3-22, P3-24, P10-27, P10-28 (relay227); then P4-24, P5-22/P5-26, P6-26/P6-27. Owner: ADR0010/ADR0011; one -Catalogue run.
```
