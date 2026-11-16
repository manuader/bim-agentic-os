# Relay211: packaged MCP parity race hardening, PDF named encodings, diff-edits internal error through IntentPlan

Relay211 (cloud scheduled, 2026-11-16 17:53Z) took the FREE LOCK and picked the oldest-touched phases 5, 6 (relay207) and 3 (relay208). Three workers on isolated worktrees, CI as the only oracle (no local .NET SDK or pwsh), independent read-only reviews, review fixes on the task branches, then one integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P5-19 (Phase 5): tests/cli/PackageLayoutPackTests.cs — the marker-removed-with-repository MCP/CLI case also pins the composed digest a8c62da5…; header oracles 1–6 in order; Exec and the MCP stdio helper go through WaitOrKill (Kill exceptions swallowed only when the child really exited; timeout still reported).
- P6-20 (Phase 6): PDF simple fonts with a named /Encoding (/WinAnsiEncoding, /MacRomanEncoding, /StandardEncoding) decode through the P6-18 base tables; Type3 /Differences slot names keep bytes without encoding-glyph-unknown; unknown /BaseEncoding → typed encoding-base-unknown; ToUnicode still wins.
- P3-18 (Phase 3): DiffEditsCommand.Plan returns the envelope; its catch-all is IntentPlan.Internal("diff-edits", e); Run = Plan(...).Print(); bytes unchanged.
- Board: P5-19, P6-20, P3-18 ✅. New: P3-19 (other verbs' internal errors through IntentPlan.Internal), P6-22 (Type3 unsupported glyph-list names signalled). Progress 198/215 (92.1 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P5-19: red 43923698787 (injected InvalidOperationException/Win32Exception unhandled), green 43925524888; review hardening (guarded GetProcessById, bounded injected wait) green 43932755569. 0 failed attempts.
- P6-20: tests/cad/PdfNamedEncodingTests.cs (generated 2-page fixture pinned A16CB929…, WinAnsi 0x80–0x9F against an independent table, MacRoman/Standard, Type3 vs Type1, unknown base, ToUnicode precedence): red 43923918941, green 43928092328; review (warning wording with a ToUnicode CMap) red 43937168519, green 43939750890. 1 failed attempt (compile-only CS8602).
- P3-18: tests/cli/ApplyIntentEnvelopeTests.cs sections 5–6 + tests/cli/Program.cs delegation check: red 43923600200, green 43925901646. 0 failed attempts.
- Independent read-only reviews: 0 blockers. P5-19 should-fix (control test could crash the section) and P6-20 should-fix (encoding-base-unknown wording with ToUnicode) closed with tests; P3-18 should-fix (stale module-contracts/features.json) closed at integration.
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. Optional: repository licence.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P5-19: the MCP helper shares WaitOrKill but is not injected directly; real Kill(true) on an exited child is a smoke check; Kill(true) AggregateException not tolerated (pre-existing).
- P6-20: Type3 hides unsupported glyph-list names (e.g. /afii10017) without a signal (→ P6-22); undefined named-table slots fall back to Latin-1 by design (keeps pins); one P6-18 assertion (name-only /Encoding) intentionally changed to the base-table result.
- P3-18: no input reaches the diff-edits catch-all, so the forced internal error is compared in process against the old hand-written bytes; real CLI compared for success and a library refusal only; other verbs still hand-write internal errors (→ P3-19).
- main 612169a push run 43917055034 had ifc-portable (macos-latest) cancelled (runner); the same head was 9/9 green on relay210-integration.
ARCHIVOS CAMBIADOS: tests/cli/PackageLayoutPackTests.cs; adapters/cad/Bim.Cad/{PdfEncoding.cs,PdfContent.cs}; tests/cad/{PdfNamedEncodingTests.cs (new),PdfEncodingTests.cs,Program.cs}; interfaces/cli/DiffEditsCommand.cs; tests/cli/{ApplyIntentEnvelopeTests.cs,Program.cs}; board/features/contracts/attempts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P4-19, P7-19 (relay208); then P10-21 (then P10-22/P10-23), P1-25, P2-25/P2-26 (relay209/210); then P5-20, P6-21/P6-22, P3-19 (relay211). Owner: ADR0010/ADR0011; one -Catalogue run.
```
