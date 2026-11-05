# Relay164: selective drift resolution, skill lock CLI/MCP, release archive

Cloud scheduled relay, started 2026-11-05 14:53Z. GitHub CI was the only oracle (no local .NET SDK or pwsh). The oldest-touched phases were 2, 5 and 10 (relay160); P2-09, P5-09 and P10-08 were picked. Three workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (1 blocker), every blocker and should-fix closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P2-09 (Phase 2): MirrorDriftSelect.Select — choose adopt/restore per drift proposal; returns the BIM-IR operations, the P2-07 restore plan filtered with original step ids/order, map adds/removals and the key→UniqueId entries a restore invalidates; incomplete couplings refused drift_select_requires_incomplete; evidence_incomplete and order restore_then_adopt carried in the JSON. Review blocker fixed in P2-07: Restore.Requires now lists dependents Revit cascades (hosted openings, level elements, replaced hosts), so a partial restore can no longer delete unchosen elements.
- P5-09 (Phase 5): `skills-lock pin|verify|update|unpin` CLI verb and read-only MCP `skills_lock_verify` (byte-equal verify text); pin never overwrites; update keeps pins not named; explicit unpin.
- P10-08 (Phase 10): scripts/package-archive.ps1 deterministic zip + SHA256SUMS of the P10-04 release folder; -Verify/-Extract refuse tampered, unlisted, mismatched, unsafe or reserved-name entries with typed codes, verify a private copy and re-hash on extract.
- Board: P2-09, P5-09, P10-08 ✅; new P2-10 (drift selection CLI/MCP), P2-11 (commit adopted drift with provenance), P10-10 (install from archive), P10-11 (audit-verify CLI with head anchor) ⏳.
TESTEADO AUTOMÁTICAMENTE (backend):
- P2-09 tests/mirror-journal: red 42050267331, green 42053306609; review red 42057004079, green 42059574519. 0 failed attempts.
- P5-09 tests/cli + tests/mcp + tests/skills: red 42050527620, green 42051621858; review red 42061959924, green 42063596832. 1 failed attempt (static grep tripped by a comment).
- P10-08 tests/packaging (ubuntu + windows): red 42050344726, green 42051827079; review red 42067515495, green 42070078862. 1 failed attempt (missing exit 0).
- Merged integration head re-run in CI before main moved (see journal).
VERIFICADO EN REVIT NATIVO: nothing new (all headless).
BLOQUEADO / POR QUÉ: nothing new blocked.
DECISIONES QUE NECESITO DE VOS: nothing new; still open — escritorio/estudio/biblioteca class; Uniclass excerpt licence; party-wall facade edges.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P2-09: library only (P2-10); adopted operations not committed (P2-11); never fed a real Revit snapshot.
- P5-09: no pin from a SkillResolution; update has no compare-and-swap; filesystems without hard links fall back to check+rename.
- P10-08: no signing; byte-identity only within one OS and pwsh/.NET version; install-addin does not take an archive yet (P10-10).
ARCHIVOS CAMBIADOS: core/Bim.Core/{MirrorDriftSelect,MirrorDriftResolve,SkillLock}.cs; tests/mirror-journal/{MirrorDriftSelectChecks,Program}.cs; interfaces/cli/{SkillLockCommand,Program}.cs, Bim.Cli.csproj; interfaces/mcp/{SkillLockTool,McpServer}.cs; tests/{cli,mcp,revit-commands,skills}; scripts/package-archive{,-common}.ps1; tests/packaging/{test-package-archive,test-package}.ps1; board/features/contracts/attempts/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); dotnet run --project tests/mirror-journal; dotnet run --project tests/cli; dotnet run --project tests/mcp; dotnet run --project tests/skills; pwsh -File tests/packaging/test-package-archive.ps1.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P1-11, P3-07, P8-09, P9-06 port/P9-03 (relay161/157; check origin chat-* branches before any Revit command file — chat-wall-attach still unmerged); then P4-08/P4-09, P6-10/P6-11, P7-09/P7-10 (relay162); then P2-10/P2-11, P5-10/P5-11, P10-10/P10-11.
```

Owner: no new Revit step from this relay (all headless). New `skills-lock unpin` mode and `update` now keeps pins it is not told about.
