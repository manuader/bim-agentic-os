# Relay160: mirror drift on CLI/MCP, skill lock file, audit verification

Cloud scheduled relay, started 2026-11-05 06:54Z. GitHub CI was the only oracle (no local .NET SDK). The oldest-touched phases were 2, 5 and 10 (relay156); P2-08, P5-08 and P10-09 were picked. Three workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (1 blocker), every blocker and should-fix closed with tests, integration head re-run in CI before main moved.

```
IMPLEMENTADO:
- P2-08 (Phase 2): `mirror-drift store snapshot.json mirror.json output.json` CLI verb and read-only MCP tool `mirror_drift` on one engine (MirrorDriftTool): P2-04 drift report + P2-07 resolution in one document (applied false, requires approval, core_store revision/hash), never applied, never-overwrite output (output_in_store refused), typed stale/document/bounded/request/store codes, CLI file and MCP text byte-equal.
- P5-08 (Phase 5): skills.lock.json (new standalone format) pins pack id/version/content SHA-256 (bim-skill-pack-sha256-v1); locked loads refuse skill_lock_drift/skill_lock_missing and serve manifests parsed from the hashed bytes; only an explicit Update rewrites; root .gitattributes `skills/** -text` so Windows autocrlf checkouts hash the same.
- P10-09 (Phase 10): AuditVerifier over the unchanged P10-03 log — every finding typed (truncated, reordered, entries_missing, sequence_broken, entry_edited, entry_not_canonical, chain_broken, anchor_mismatch, actor_unknown, malformed), severity-ranked report code; deterministic per actor/command/UTC-day decision summary.
- Board: P2-08, P5-08, P10-09 ✅; Phase 5 batch loaded: P5-09 skills-lock CLI/MCP, P5-10 lock-checked jurisdiction sidecars, P5-11 typology programme → generative brief ⏳.
TESTEADO AUTOMÁTICAMENTE (backend):
- P2-08 tests/cli + tests/mcp: red 41985644839, green 41987958037; review red 41989771640, green 41989880727. 1 failed attempt (hard-coded MCP core tool list).
- P5-08 tests/skills: red 41983932849, green 41985065469; review red 41991496133, green 41991684029. 0 failed attempts.
- P10-09 tests/audit: red 41983890324, green 41984918492; review red 41993013697, green 41993168316. 0 failed attempts.
- Merged integration head re-run in CI before main moved (see journal).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session this relay; all headless).
BLOQUEADO / POR QUÉ: nothing new blocked.
DECISIONES QUE NECESITO DE VOS: nothing new; still open — escritorio/estudio/biblioteca class; Uniclass excerpt licence; party-wall facade edges.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P2-08: never fed a real Revit snapshot; map supplied by the caller (not ExtensibleStorage); store read once; MCP inline JSON capped at 8 MiB; no readOnlyHint.
- P5-08: not wired to CLI/MCP (P5-09); jurisdiction.json hashed but read unchecked (P5-10); no directory fsync; concurrent updaters last-writer-wins.
- P10-09: unkeyed P10-03 chain — tail removal/full rehash only detectable with an external head anchor; library only.
ARCHIVOS CAMBIADOS: interfaces/cli/{MirrorDriftCommand,BoundedInput,Program}.cs, Bim.Cli.csproj; interfaces/mcp/{MirrorDriftTool,McpServer}.cs, Bim.Mcp.csproj; tests/{cli,mcp,revit-commands}; core/Bim.Core/{SkillLock,AuditVerify}.cs; tests/skills/{SkillLockTests,Program}.cs; tests/audit/Program.cs; .gitattributes; board/features/contracts/attempts/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); dotnet run --project tests/cli; dotnet run --project tests/mcp; dotnet run --project tests/skills; dotnet run --project tests/audit.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P1-10/P1-11, P3-06/P3-07, P8-08/P8-09, P9-06 port/P9-03 (relay157; check origin chat-* branches before any Revit command file — chat-wall-attach still unmerged); then P4-07, P6-08, P7-08..P7-10 (relay159); then P2-09, P5-09..P5-11, P10-08.
```

Owner: no new Revit step from this relay (all headless). Note: the new root .gitattributes marks skills/** as -text; after `git pull` on Windows, skills files keep LF.
