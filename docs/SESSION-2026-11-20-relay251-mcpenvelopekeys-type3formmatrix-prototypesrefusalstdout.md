# Relay251: MCP repeated envelope keys, Type3 warning through a form matrix, generate-prototypes refusal stdout

Relay251 (cloud scheduled, 2026-11-20 15:53Z) took the FREE LOCK, picked the oldest-touched phases 10, 6 and 7 (relay248), got three workers green, ran three independent read-only reviews (all approve, 0 blockers; nits recorded as follow-up tasks) and integrated through one integration head run in CI before main moved. CI is the only oracle (no local .NET SDK or pwsh).

```
IMPLEMENTADO:
- P10-33 (Phase 10): interfaces/mcp/McpServer.cs. Dispatch refuses any JSON-RPC member given more than once with -32600 `invalid_request: repeated member <name>` (id echoed only when id appears exactly once with a valid type; else null). CallTool refuses a repeated params member with -32602 `invalid_params: repeated params member <name>` and a lone-surrogate argument name with -32602 `invalid_params: argument name is not valid text` and the request id. Well-formed bytes unchanged.
- P6-31 (Phase 6): test-only. Type3 glyph-list warning stays pending through a Form XObject /Matrix height overflow (next emitting show inside or after the form warns) and a Ts rise position overflow; P6-25 static check now uses StripCommentsAndLiterals with the swallow guard.
- P7-30 (Phase 7): generate-prototypes Run catch (Refusal) sets exit 1 before Fail and contains a throwing stdout. Pipeline-refusal stdout failure pinned as the internal error. P4-27 Run pin third documented delta (RunDeltaP730).
- Board: P10-33, P6-31, P7-30 ✅; new P10-34, P6-33, P7-32. Progress 288/307 (93.8 %). Contracts and features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P10-33: red 44768375085 (probe: last value won for repeated id/method/params/name/arguments; revit calls reached the transport; lone-surrogate name gave internal_error with the id), green 44770234635 (macOS hosted runner cancelled once, re-run; infrastructure). 0 failed attempts.
- P6-31: probe green 44768289480 (no defect). 0 failed attempts.
- P7-30: red 44768347577 (typed refusals with a throwing stdout escaped Run with exit 0), intermediate 44770937419 (pre-existing P3-22 assertion pinned the old defect; updated), green 44774279275. 0 failed attempts on the new test.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: none.
GAPS CONOCIDOS:
- P10-33: lone-surrogate member names in the envelope or params and lone-surrogate method/params.name values still give internal_error; refusals pinned by parsed fields, not exact bytes; the row's "id null" premise was wrong (old outcome kept the id) → P10-34.
- P6-31: non-finite page×form matrix composition silently keeps the outer CTM; nested form matrices and a skewed rise overflow not probed; swallow guard duplicated (P6-25/P6-27) with a balanced mid-file over-strip gap → P6-33.
- P7-30: a partially accepting stdout leaves a truncated line unpinned; P3-22 2e comments/labels stale for generate-prototypes; disposed-writer attempt check trivially true → P7-32. generate-typology, plan-layouts and doc-realized keep the same catch (Refusal) stdout defect (InternalErrorDelegationTests 2e still pins it; serialize with P3-29/P3-30).
ARCHIVOS CAMBIADOS: interfaces/mcp/McpServer.cs, tests/mcp/RepeatedEnvelopeKeyTests.cs, tests/mcp/Program.cs (registration); tests/cad/PdfType3GlyphListTests.cs; interfaces/cli/GenerateWrite.cs, tests/cli/GeneratePrototypesTests.cs, tests/mcp/GenerateWriteLinkTests.cs, tests/cli/InternalErrorDelegationTests.cs; tasks.md, docs/features.json, docs/contracts/module-contracts.md, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P1-37, P1-38, P5-31, P5-32, P7-31 (relay249); then P2-36, P3-29, P3-30, P4-30 (relay250); then P10-32, P10-34, P6-32, P6-33, P7-32.
```
