# Relay249: deadline transport single page seam, lock-path twice-read, packaged MCP generate_prototypes

Relay249 (cloud scheduled, 2026-11-20 10:53Z) took the FREE LOCK, picked the oldest-touched phases 1, 5 and 7 (relay246), got three workers green, ran three independent read-only reviews (0 blockers; should-fixes and cheap nits closed with tests, the rest recorded as follow-up tasks) and integrated through one integration head re-run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh; dot.net is refused by the proxy).

```
IMPLEMENTADO:
- P1-36 (Phase 1): RevitDeadlineTransport has one page path. Every real-pipe page goes through connectThenRead(min(5 s, time left), ReadAfter); cutTransport/connectTransport parameters, the measured flag and the PipeTransportWithReadTimeout default are removed (CLI and MCP callers already took this path, so behaviour is unchanged). Old fakes reach the single seam through a reviewed-faithful Legacy adapter; every pinned code, detail, byte and call label unchanged. McpServer RevitCallAll comment updated.
- P5-30 (Phase 5): test-only. jurisdiction-resolve --lock twice in process and twice as a real process prints byte-identical, literally pinned lines (success, skill_lock_drift, sidecar json_invalid); MCP skill_list and jurisdiction_resolve (with/without lock_path) equal the packaged CLI lines on two calls in one server, in process and in a packaged mcp/Bim.Mcp.dll process; two LoadLocked results are unequal registries.
- P7-24 (Phase 7): test-only. From a scripts/package.ps1 copy outside any repository, mcp/Bim.Mcp.dll generate_prototypes resolves the pack package-first and its text is byte-equal to the packaged CLI output (ranked and infeasible requests); removed packaged pack → identical generate_prototypes_pack_unavailable line from CLI and MCP.
- Board: P1-36, P5-30, P7-24 ✅; new P1-38, P5-32, P7-31. Progress 282/301 (93.7 %). Contracts and features.json updated (stale P1-34 legacy-seam wording fixed).
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-36: red 44723484569 (seam pins), green 44723680305. 0 failed attempts.
- P5-30: red 44723785006 (drift placeholder), green 44726870309; review red 44731149194, green 44732544974. 0 failed attempts.
- P7-24: red probe 44723412077 (mcp/ renamed server/ → repository_not_found), green 44725994276; review nits green 44735416209. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: none new (CI suites only).
GAPS CONOCIDOS:
- P1-36: core PipeTransportWithReadTimeout may now be test-only; the CLI passes a configured transport never called when cutReads is true; Legacy adapter throws outside Run on an unexpected cut connect; StripComments does not handle @$/raw strings → P1-38.
- P5-30: registry loads behind the verbs/tools still observed only through stand-in loads; json_invalid detail pins the System.Text.Json message; no twice-read for --country/--municipality or --allow-stub under --lock → P5-32.
- P7-24: packaged MCP pack_invalid, repository fallback with a marker removed, initialize/tools/list handshake and a sha pin on the ranked document uncovered; macOS ifc-portable job does not run packaging → P7-31.
ARCHIVOS CAMBIADOS: interfaces/cli/RevitDeadlineTransport.cs, interfaces/mcp/McpServer.cs (comment), tests/cli/RevitCommandDeadlineTests.cs; tests/cli/SkillListTests.cs, tests/mcp/TwiceReadToolTests.cs, tests/mcp/Program.cs; tests/packaging/test-package.ps1; tasks.md, docs/features.json, docs/contracts/module-contracts.md, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P2-35, P3-28, P3-29, P4-29 (relay247); then P10-32, P10-33, P6-31, P6-32, P7-30 (relay248); then P1-37, P1-38, P5-31, P5-32, P7-31.
```
