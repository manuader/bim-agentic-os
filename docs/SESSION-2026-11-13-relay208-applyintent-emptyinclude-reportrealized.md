# Relay208: apply-intent through one IntentEnvelope, empty include equals absent, catalogue-report surfaces doc-realized

Relay208 (cloud scheduled, 15:34Z) picked the oldest-touched phases 3 and 7 (relay204) and 4 (relay205/206). GitHub CI was the only oracle (no local .NET SDK or pwsh).

```
IMPLEMENTADO:
- P3-17 (Phase 3): ApplyIntentCommand.Plan(args) returns one IntentEnvelope from every step; Run = Plan(args).Print() is its only print. DiffEditsCommand.Fail delegates to IntentPlan.Failed; its success plan prints through IntentEnvelope.
- P7-18 (Phase 7): generate-typology normalises "include":[] to absent at the CLI/MCP parser, so it equals the P7-14 pins and the composed pin.
- P4-18 (Phase 4): catalogue-report adds a typed `realized` member for the owner doc-up-elements step (entry count and sha256 of realized.json trusted only when the file, the step reply and the kept report.json agree; refusal code from report.json).
- Board: P3-17, P7-18, P4-18 ✅. New: P3-18 (diff-edits internal error through IntentPlan.Internal), P4-19 (report cross-checks the step request against the folder), P7-19 (empty include with non-empty skills). Progress 192/210 (91.4 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P3-17 tests/cli/ApplyIntentEnvelopeTests.cs (reflection shape, Plan vs real CLI process in 9 cases incl. commit_incomplete, Fail bytes, static checks, fragment count): red 43454473275, green 43457705408 (core-windows apply-edits race flake re-run once). 0 failed attempts.
- P7-18 tests/cli/GenerateTypologyTests.cs + tests/mcp/GenerateTypologyToolTests.cs (file bytes, pins eb8720c1/3a551774/a8c62da5, stdout, MCP vs CLI): red 43454272873, green 43456249524. 0 failed attempts.
- P4-18 tests/cli/CatalogueReportTests.cs + catalogue-report-record.ps1 (every state, real doc-realized verb: match, tampered byte → mismatch, realized_unique_id_duplicate refusal): red 43454907676, green 43458261487; review red 43465011283, green 43468205404. 0 failed attempts.
- Independent read-only reviews: 0 blockers. P4-18 should-fix (success path ignored report.json) + test gaps closed with tests. P3-17 should-fix (fragment count scoped to intent/edits sources, not the whole folder) recorded in evidence; nit → P3-18. P7-18 should-fix (module-contracts) closed at integration.
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. Optional: repository licence.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P3-17: eight other CLI verbs keep their own failed-envelope JSON; diff-edits internal error hand-written (→ P3-18); static "no Console." check can be bypassed by `using static` (runtime silence checked on 9 paths).
- P7-18: library callers building TypologyBriefRequest(Include: []) still record []; composed with non-empty skills untested (→ P7-19).
- P4-18: step request record_file not cross-checked (→ P4-19); link checks skip where symlinks are unavailable; never on a real owner record.
ARCHIVOS CAMBIADOS: interfaces/cli/{ApplyIntentCommand.cs,DiffEditsCommand.cs,GenerateTypologyCommand.cs,CatalogueReportCommand.cs}; tests/cli/{ApplyIntentEnvelopeTests.cs (new),Bim.Cli.Tests.csproj,Program.cs,GenerateTypologyTests.cs,CatalogueReportTests.cs,catalogue-report-record.ps1}; tests/mcp/GenerateTypologyToolTests.cs; board/features/contracts/attempts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P1-18, P10-20/P10-21/P10-22 (relay205/206); then P2-24/P2-25, P5-19/P5-20, P6-20/P6-21 (relay207); then P3-18, P4-19, P7-19. Owner: ADR0010/ADR0011; one -Catalogue run.
```
