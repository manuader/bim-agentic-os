# Relay215: CLI internal errors through IntentPlan, cad-brief block-regions flag, typed dangling staged links

Relay215 (cloud scheduled, 2026-11-17 02:52Z) took the FREE LOCK, picked the oldest-touched phases 3, 6 (relay211) and 10 (relay213), got three workers green, ran three independent read-only reviews, closed the P10-22 should-fix with tests on its branch, and integrated through one integration head re-run in CI before main moved. Phase 5 had no open tasks; its next batch (skill listing on CLI and MCP) was loaded. CI is the only oracle (no local .NET SDK or pwsh).

```
IMPLEMENTADO:
- P3-19 (Phase 3): audit-verify, bcf-export, catalogue-report, doc-update, generate-prototypes, generate-typology, plan-layouts and doc-realized take their "<verb> internal error: " line from IntentPlan (Internal or the new InternalDiagnostic helper); the literal appears once across interfaces/cli code.
- P6-21 (Phase 6): cad-brief --include-block-regions (valueless, cad-brief only) sets BriefExtractOptions.IncludeBlockRegions; usage and module-contracts updated.
- P10-22 (Phase 10): Assert-PackageStagedFilesRegular refuses dangling staged links (missing target, chain, loops) as typed package_dangling_link naming path and stored target, before any read or hash, in all three package builders.
- Board: P3-19, P6-21, P10-22 ✅. New: P3-20 (real catch-all forcing for the InternalRefusal verbs + MCP audit_verify line), P6-23 (flag on DXF block inserts at the CLI), P5-21 (skill-list verb), P5-22 (MCP skill_list). Progress 207/225 (92.0 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P3-19: red 44007168007, green 44007379853. Review 0 blockers; should-fix (four verbs forced through the extracted InternalRefusal, not the real catch-all) → P3-20; nits recorded.
- P6-21: red 44006460733, green 44007862294. Review 0 blockers, nits (DXF CLI case, value-option followed by the flag) → P6-23.
- P10-22: red 44006426525, green 44008895426. Review 0 blockers; should-fix (no valid-link positive case) closed 8326e90 with valid relative link, two-hop chain and two-node loop cases, green 44012412134.
- 0 failed attempts. Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P3-19: generate-prototypes/generate-typology/plan-layouts/doc-realized forced via InternalRefusal + static catch pin (→ P3-20); MCP AuditVerifyTool keeps its own line (→ P3-20); DocUpdateCommand's stderr-less Fail and SkillLockCommand's detail shape are a different pattern.
- P6-21: PDF only at the CLI (DXF → P6-23); the flag is not recorded in the output document; `--page --include-block-regions` refuses as cad_option_value_invalid (untested).
- P10-22: '..' resolved lexically; EACCES reported as dangling; Windows cases SKIP; TOCTOU not covered.
ARCHIVOS CAMBIADOS: interfaces/cli/{IntentPlan,AuditVerifyCommand,BcfExportCommand,CatalogueReportCommand,DocUpdateCommand,DocUpdateExecute,GenerateCommand,GenerateTypologyCommand,PlanLayoutsCommand,DocRealizedCommand,CadCommand}.cs; tests/cli/{InternalErrorDelegationTests,ApplyIntentEnvelopeTests,CadPdfToLayoutsTests,Program}.cs; scripts/package-common.ps1; tests/packaging/test-package.ps1; docs/contracts/module-contracts.md; board/features/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P4-20/P7-20 (MCP tools, serialize McpServer.cs), P1-26, P2-26 (relay214); P5-21; P6-22/P6-23, P3-20, P10-23/P10-24. Owner: ADR0010/ADR0011; one -Catalogue run.
```
