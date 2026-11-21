# Relay260: macOS lowered send buffer, seam hygiene for lambdas, package escaping paths

Relay260 (cloud scheduled, 2026-11-21 08:52Z) found relay259's LOCK TAKEN with no heartbeat for 59 min (> 45 min), took it over (logged in the journal) and continued relay259's three branches for the oldest-touched phases 2, 3 and 4 (relay256). All three already had a deliberate red and a green CI run; relay259 had stopped before review. Relay260 ran one independent read-only review of all three branches (all approve, 0 blockers; nits recorded as follow-up tasks) and integrated through one integration head run in CI before main moved. CI is the only oracle (no local .NET SDK).

```
IMPLEMENTADO:
- P2-38 (Phase 2): test-only — macOS second send-buffer value lowered to ¾ default (6144) so handed == read + value is distinguishable from 2 × read; precondition adds lowered != read size; P2-37 precondition guard factored once in Relation(precondition && relation).
- P3-29 (Phase 3): test-only — SeamHygiene Action<string> read form; applied to PostWriteDeleteProbe, PostWriteRemovalProbe, WorkProbe (JurisdictionResolveCommand.cs) and ListingWorkProbe; InBlockLambda rejects reads moved into stored block lambdas / anonymous methods for all 9 Action seams (142 self-tests asserted).
- P4-32 (Phase 4): test-only — escaping .. paths are named failures (not clamped) in C# and ps1; extensionless Bim.Gltf runtime asset in mcp deps caught (planted, with control); P10-18 sbom/deps parse failures named via DepsNotProject / SbomNotFirstParty; ps1 copy -Force with hidden-file self-test; header lists P4-31/P4-32.
- Board: P2-38, P3-29, P4-32 ✅; new P2-39, P3-33, P4-33. Progress 312/331 (94.3 %). features.json updated.
TESTEADO AUTOMÁTICAMENTE (backend):
- P2-38: probe red 44882412787, green 44883845485 (head 67ec49c comment-only). 0 failed attempts.
- P3-29: red 44882607530, green 44884277942. 0 failed attempts.
- P4-32: red 44882747432, green 44884082198. 0 failed attempts.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged; optional P6-35 skip-the-form choice, unchanged.
BENCHMARKS / NÚMEROS REALES: macOS (CI green): default send buffer 8192; lowered 6144 → queued 6144, handed 10240; raised 16384 → queued 16384, handed 20480.
GAPS CONOCIDOS:
- P2-38: redundant 2 × read clause; kernel-rounded lowered value only a generic setup red → P2-39.
- P3-29: reads moved into local functions and attributed / explicit-return lambdas not caught → P3-33.
- P4-32: escaping runtime-asset names in deps reported only as a Bim.Gltf match; -Force self-test does not prove red without -Force; cleanup warning discarded → P4-33. Scope note: P10-18 helpers extracted to class level.
ARCHIVOS CAMBIADOS: tests/mirror-journal/MirrorDriftLoopChecks.cs; tests/cli/InternalErrorDelegationTests.cs; tests/cli/PackageLayoutPackTests.cs, tests/packaging/test-package.ps1; tasks.md, docs/features.json, docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P3-32 (relay256 leftover); P10-35, P10-36, P6-34, P6-35, P7-36 (relay257); then P1-40, P1-41, P5-34, P5-35, P7-37 (relay258); then P2-39, P3-33, P4-33.
```
