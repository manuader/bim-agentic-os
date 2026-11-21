# Relay263: deadline tracked-file scan, jurisdiction ambiguity twice-read, ranked package pin

Relay263 (cloud scheduled, 2026-11-21 13:52Z) found the LOCK FREE (relay262 released 13:29Z), took it, and picked one task in each of the oldest-touched phases 1, 5 and 7 (relay258 leftovers): P1-40, P5-34, P7-37. Three isolated workers ran TDD against CI (no local .NET SDK; CI is the only oracle), one independent read-only review covered all three branches (all approve, 0 blockers; nits recorded as follow-up tasks), and one integration head ran CI before main moved.

```
IMPLEMENTADO:
- P1-40 (Phase 1): latent DEFECT FIXED — McpServer built RevitDeadlineTransport with the configured transport even when cutReads was true (never called on the real pipe); now `realPipe ? null : revitTransport`. RevitCommandDeadlineTests section 2i: caller scan over `git ls-files` (untracked-copy control), "tests still do" clause removed, Program.cs pin parses the argument list (6 variants, 7 mutants), counting spy proves 0 calls.
- P5-34 (Phase 5): test-only — jurisdiction_ambiguous (alias overlap and duplicate claim) and drifted-lock selections pinned literally, twice in process, twice as a real process, twice through in-process and packaged MCP; jurisdiction_conflict unreachable from CLI (usage) and MCP (-32602), in-process Select lines pinned; Cli() helpers assert empty stderr; json_invalid full-line literal.
- P7-37 (Phase 7): test-only — packaged ranked generate_typology sha256 eb8720c1… / 92013 bytes CLI == MCP (equals P5-12 in-process pin); byte lengths 47622/3520 asserted; exact stdout status members; P7-12 cleanup through Remove-PackageCaseDirectory; cleanup warnings reported once.
- Board: P1-40, P5-34, P7-37 ✅; new P1-42, P5-36, P7-39. Progress 321/340 (94.4 %). features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-40: red 44911474828, green 44912660609. 0 failed attempts.
- P5-34: probe red 44911652955, green 44913084478. 0 failed attempts.
- P7-37: probes 44911352555 + 44913586192, green 44915958074 (docs head 44918375672). 1 failed attempt (PowerShell TryGetProperty [ref] → EnumerateObject).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: ranked packaged typology 92013 bytes, sha256 eb8720c1a98a749048df6ac69f1794b965e833efc963eb9ddfe58db0ae886f4e (Linux == Windows).
GAPS CONOCIDOS:
- P1-40: untracked-copy control writes in the repo root, git dependency undocumented, positional-after-named argument, remaining IndexOf MCP pins → P1-42.
- P5-34: packaged McpProcess stderr discarded, drift provenance comments disagree, multi-pack drift suffix, duplicated fixture → P5-36.
- P7-37: $caseLabel in loop messages, quiet zero-count report, early-throw warnings, Remove-P733Directory, old pack_rules regex → P7-39.
ARCHIVOS CAMBIADOS: interfaces/mcp/McpServer.cs, tests/cli/RevitCommandDeadlineTests.cs; tests/cli/SkillListTests.cs, tests/mcp/TwiceReadToolTests.cs; tests/packaging/test-package.ps1, docs/contracts/module-contracts.md; tasks.md, docs/features.json, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P2-39, P3-33, P4-33 (relay260); then P3-34, P10-37, P6-36 (relay261); then P10-38, P6-37, P7-38 (relay262); then P1-41, P1-42, P5-35, P5-36, P7-39.
```
