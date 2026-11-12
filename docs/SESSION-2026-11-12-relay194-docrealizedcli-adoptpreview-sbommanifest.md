# Relay194: doc-realized CLI, read-only adopt preview, sbom/manifest agreement

Cloud scheduled relay, started 2026-11-12 00:52Z (LOCK was FREE after relay193). GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases with ready work: 4, 2 and 10 (relay191). Workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (0 blockers, 6 should-fix, nits), every finding closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P4-15 (Phase 4): `doc-realized --record --fixture --assignments --document --output` writes DocRealized.Canonical(FromRecord(...)) byte-equal to the in-process builder; `doc-update --realized` accepts it; record/assignments read to DocRealized.MaxRecordBytes (32 MiB); never-overwrite; typed envelopes.
- P2-17 (Phase 2): MirrorDriftAdoptCommit.Preview(store, selection) — Commit's read-only half (would_commit / would_replay / nothing_to_adopt, no lock, no write); MCP mirror_drift_adopt plan mode uses it, duplicated store reader removed.
- P10-15 (Phase 10): package-verify cross-checks manifest.json and sbom.json on shipped binaries in both directions (sbom_manifest_mismatch naming path and direction; pathless sbom entries → named sbom_invalid).
- Board: P4-15, P2-17, P10-15 ✅; new P2-19 (mirror-drift-adopt --preview CLI flag), P4-16 (owner catalogue step uses doc-realized; ⛔ on P4-14), P10-16 (unclassified native payload by magic bytes). Progress 159/178 (89.3 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P4-15 tests/cli: red 43130960366, green 43132233200; review red 43141602918, green 43143266206. 0 failed attempts.
- P2-17 tests/mcp: red 43130822808, green 43132966479; review green 43142673384. 1 failed attempt (test compile CS0136).
- P10-15 tests/packaging: red 43130808434, green 43138707648 (workflow_dispatch; push run core-windows hung); review red 43141685293, green 43143789557. 0 failed attempts.
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; P4-11 / P1-14 owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. Optional: repository licence.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P4-15: owner script not wired (→ P4-16); caller supplies step → source assignments; never run on a real owner run record.
- P2-17: snapshot only (ProjectStore.Commit refusals not predicted); Core reader unbounded behind MCP's 8 MiB pre-check; no CLI preview (→ P2-19).
- P10-15: extensions outside the binary pattern not classified (→ P10-16); natives matched by leaf name across references; no third-party NuGet package ships today.
ARCHIVOS CAMBIADOS: interfaces/cli/{DocRealizedCommand,Program}.cs; core/Bim.Core/MirrorDriftAdopt.cs; interfaces/mcp/MirrorDriftAdoptTool.cs; scripts/package-common.ps1; tests/cli/{DocRealizedTests,Program}.cs; tests/mcp/Program.cs; tests/packaging/test-package.ps1; board/features/contracts/attempts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P1-18/P1-21/P1-22, P2-18, P6-16/P6-17 (relay192; P1-21 before P1-22, same parser; P6-16/P6-17 share PDF fixtures — serialize); then P3-14, P5-16/P7-16, P7-15 (relay193; P5-16 and P7-16 share GenerateTypologyCommand — serialize); then P4-14, P2-19, P10-16 (relay194; P4-14 and P1-18 share tests/integration/test-catalogue-session.ps1 — serialize). Owner: ADR0010/ADR0011; one -Catalogue run.
```
