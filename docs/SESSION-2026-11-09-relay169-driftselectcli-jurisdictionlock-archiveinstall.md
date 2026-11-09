# Relay169: drift-select CLI/MCP, lock-checked jurisdictions, install from archive

Cloud scheduled relay, started 2026-11-09 20:52Z (LOCK was FREE after relay168). GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases 2, 5, 10 (relay164): P2-10, P5-10 and P10-10. Three workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (0 blockers, 8 should-fix), every should-fix and the cheap NITs closed with tests, integration head re-run in CI before main moved. Relay170 fired at 22:54Z, saw relay169's heartbeat lagging during a long CI wait, correctly backed off without touching the lock.

```
IMPLEMENTADO:
- P2-10 (Phase 2): `mirror-drift-select store snapshot.json mirror.json choices.json output.json` CLI verb and read-only MCP `mirror_drift_select` on one engine (P2-08 analysis → P2-09 Select); canonical selection JSON (applied false), byte-equal CLI/MCP including refusals, strict bounded choices, typed drift_select_* envelopes, never-overwrite, exit 1 and no file on failure.
- P5-10 (Phase 5): JurisdictionRegistry.LoadLocked / FromSnapshots — jurisdiction.json parsed from the exact hashed snapshot bytes (private copy, re-hashed with the shared SkillPackHash.Hash); edited sidecar → skill_lock_drift; forged snapshot paths refused; cased Jurisdiction.json path_unsafe on every OS; unlocked Load unchanged.
- P10-10 (Phase 10): install-addin.ps1 -Archive [-ChecksumsPath] installs only from package-archive -Extract into an owner-only staging folder (0700 / protected ACL) removed in a finally block; typed refusals passed through; honest message when cleanup fails; -PackageDirectory and -Uninstall unchanged.
- Board: P2-10, P5-10, P10-10 ✅. No new batch needed (P2-11, P5-11, P10-11 still ⏳).
TESTEADO AUTOMÁTICAMENTE (backend):
- P2-10 tests/cli + tests/mcp: red 42698245612, green 42700577456; review 42707806711 (tests only). 0 failed attempts.
- P5-10 tests/skills: red 42699789308, green 42702120305; review red 42710926268, green 42713067042. 0 failed attempts.
- P10-10 tests/packaging (ubuntu + windows end-to-end): red 42697985017, green 42701261565; review red 42717484448, green 42720517108. 1 failed attempt (Windows pwsh recreates a missing TEMP).
- Merged integration head re-run in CI before main moved (see journal).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: nothing new blocked.
DECISIONES QUE NECESITO DE VOS: nothing new; still open — escritorio/estudio/biblioteca class; Uniclass excerpt licence; party-wall facade edges.
BENCHMARKS / NÚMEROS REALES: none measured.
GAPS CONOCIDOS:
- P2-10: selection only (P2-11 commits); map caller-supplied; output_in_store lexical; never run on a real Revit snapshot.
- P5-10: not on CLI/MCP or compilers; first lock issue only (+N count); empty jurisdiction.json directory = no sidecar under the lock (pinned divergence). SkillLockLoad carries no bytes, so the snapshot route is the API.
- P10-10: -AssemblyPath required with -Archive; Ctrl-C cleanup and staging_unavailable untested; never loaded in Revit.
- CI flake: ubuntu real-CLI one-run test (catalogue-session-cli) `pipe_io_error` at step 68 recurred (relay168 and twice here); re-running the failed job passes. Worth a dedicated fix.
ARCHIVOS CAMBIADOS: interfaces/cli/{MirrorDriftSelectCommand,MirrorDriftCommand,Program}.cs, interfaces/cli/Bim.Cli.csproj; interfaces/mcp/{MirrorDriftSelectTool,MirrorDriftTool,McpServer}.cs; tests/cli; tests/mcp; tests/revit-commands (tool list pin); core/Bim.Core/{JurisdictionLock,Jurisdiction,JurisdictionRegistry,SkillLock}.cs; tests/skills/{JurisdictionLockTests,Program}.cs; scripts/{install-addin,package-archive}.ps1; tests/packaging/test-install-addin.ps1; board/features/contracts/attempts/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); dotnet run --project tests/cli; dotnet run --project tests/mcp; dotnet run --project tests/skills; pwsh -File tests/packaging/test-install-addin.ps1.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P8-09, P9-03/P9-08 (check origin chat-* branches before any Revit command file), P1-12/P1-13, P3-08/P3-09 (relay166); then P4-09, P6-10, P7-09 (relay168); then P2-11, P5-11, P10-11 (relay169). Consider a task to fix the step-68 pipe_io_error flake.
```
