# Relay248: MCP repeated argument names, tilted DXF inserts vs ezdxf, generate-prototypes stdout failure

Relay248 (cloud scheduled, 2026-11-20 07:53Z) took the FREE LOCK, picked the oldest-touched phases 10, 6 (relay245) and 7 (relay246), got three workers green, ran three independent read-only reviews (0 blockers; should-fixes closed with tests or recorded as follow-up tasks) and integrated through one integration head re-run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh). Workers refreshed the LOCK heartbeat during CI waits.

```
IMPLEMENTADO:
- P10-31 (Phase 10): MCP CallTool refuses any repeated top-level argument name (declared or not, \u-escaped spellings included) with -32602 "invalid_params: repeated argument <name>" before any other argument check and before the tool runs. Measured before: last value won everywhere; jurisdiction_resolve/skill_list used their own usage refusals (now removed as unreachable).
- P6-30 (Phase 6): test-only. DXF INSERT/MINSERT under a non-axis extrusion is reported (ocs-non-planar, Unsupported, no region), not projected — pinned for both arbitrary-axis branches; 22 MINSERT cells equal ezdxf 1.4.2 within 1e-6; OcsFrame axes equal ezdxf.
- P7-29 (Phase 7): generate-prototypes Run catch-all sets the exit code before the guarded typed-line print, so a throwing stdout no longer escapes Run with exit 0 (defect fixed); P4-27 Run pin second documented delta.
- Board: P10-31, P6-30, P7-29 ✅; new P10-33, P6-32, P7-30. Progress 279/298 (93.6 %). Contracts and features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P10-31: probe red 44698697442, green 44700180558, review red 44705390113, green 44707952119. 1 failed attempt (nested duplicate expected to pass through; the revit tool refuses envelope_invalid — pinned).
- P6-30: probe green 44698972433 (test-only), review green 44711402418. 0 failed attempts. Review reproduced every constant with ezdxf v1.4.2 independently.
- P7-29: red 44698735317, green 44701423354. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: P10-31 suite 1263+ cases; P6-30 exploder vs ezdxf worst error 0 on reproduction (C# math re-implemented in Python by the reviewer: 1.0e-10).
GAPS CONOCIDOS:
- P10-31: repeats at params/JSON-RPC envelope level not refused; lone-surrogate argument name gives internal_error with id null (pre-existing) → P10-33.
- P6-30: brief uncertainty list does not carry the reader's ocs-non-planar warnings (only area-missing); no real cad-brief CLI pin; bulge/nested chains under tilted normals uncovered → P6-32.
- P7-29: a throwing stdout in Fail (typed refusal path) still escapes Run with exit 0; pipeline refusal with throwing stdout reported as internal_error → P7-30.
- Environment: PyPI is refused by the agent proxy (403); ezdxf used from a GitHub source checkout (offline generator only).
ARCHIVOS CAMBIADOS: interfaces/mcp/McpServer.cs, interfaces/mcp/JurisdictionResolveTool.cs, interfaces/mcp/SkillListTool.cs, tests/mcp/RepeatedArgumentToolTests.cs, tests/mcp/Program.cs; tests/cad/MinsertEzdxfTests.cs, tests/cad/Program.cs, tests/cad/tools/minsert_ezdxf_centres.py; interfaces/cli/GenerateWrite.cs, tests/cli/GeneratePrototypesTests.cs, tests/mcp/GenerateWriteLinkTests.cs; tasks.md, docs/features.json, docs/contracts/module-contracts.md, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P1-36, P1-37, P5-30, P5-31, P7-24 (relay246 phases 1 and 5); then P2-35, P3-28, P3-29, P4-29 (relay247); then P10-32, P10-33, P6-31, P6-32, P7-30. McpServer.cs edits serialize (P10-33, P1-36 comment); GenerateWrite.cs (P7-30) updates the P4-27 Run pin via a third documented delta; tests/cli/SkillListTests.cs serializes (P5-30, P5-31); RevitCommandDeadlineTests.cs serializes (P1-36, P1-37); GenerateTypologyWrite.cs serializes (P3-28); InternalErrorDelegationTests.cs serializes (P3-29).
```
