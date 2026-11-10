# Relay174: query.elements cursor, apply-edits CLI, two-pass documentation dependents

Cloud scheduled relay, started 2026-11-10 04:53Z (LOCK was FREE after relay171). GitHub CI was the only oracle (no local .NET SDK or pwsh). Oldest-touched phases 1, 3 and 4 (relay166/168): P1-13, P3-08 and P4-09. Three workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (0 blockers, 13 should-fix), every should-fix and the cheap NITs closed with tests, integration head re-run in CI before main moved. Phases 1 and 4 were then complete, so their next batches were loaded (P1-14, P1-15, P4-10, P4-11).

```
IMPLEMENTADO:
- P1-13 (Phase 1): query.elements opaque pagination cursor (base64url canonical JSON bound to the ProjectInformation UniqueId, a filter digest and the last id, unkeyed check); reply gains after/next_cursor (present exactly when truncated); cursor_malformed/stale/tampered/filter_mismatch/foreign_document refused before the pipe and in the add-in; limit cuts resume after the last paged id even when rows vanished; fake add-in pages with the same cursor; owner step q-walls-next (consultas 7 steps, total 104).
- P3-08 (Phase 3): `apply-edits --store --edits [--expected-revision]` — IncrementalDiff plan byte-equal to diff-edits, committed through ProjectStore.Commit with a per-run key; apply_edits_stale_head, apply_edits_store_unavailable (new ProjectStore.StoreLocked thrown before anything is touched), apply_edits_commit_incomplete; shared EditsInput parser with diff-edits.
- P4-09 (Phase 4): DocUpdateDependents.Plan/Update — dry-run probes of every planned delete, replan with collected dependents, confirm pass; dependent_kept/unknown/shared/planned and dependents_changed refused before any write; codes in the doc-update CLI allowlist.
- Board: P1-13, P3-08, P4-09 ✅; new P1-14 (owner step document.save_as), P1-15 (query.elements --all client paging), P4-10 (doc-update --probe-dependents), P4-11 (owner step two-pass documentation update, ⛔ on P4-10/P1-14).
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-13 tests/revit-commands + tests/mcp + catalogue-session (fake + real CLI) + native-build (--warnaserror): red 42759266906, green 42761303189; review red 42764039642, green 42764176836. 1 failed attempt.
- P3-08 tests/cli (ubuntu/windows/macOS, 8-process race): red 42759165857, green 42759237577; review red 42766035928, green 42767204207. 1 failed attempt.
- P4-09 tests/doc-regeneration (fake transport): red 42759343043, green 42760288590; review red 42768953518, green 42770105346. 1 failed attempt.
- Merged integration head re-run in CI before main moved (see journal).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: nothing new blocked.
DECISIONES QUE NECESITO DE VOS: nothing new; still open — escritorio/estudio/biblioteca class; Uniclass excerpt licence; party-wall facade edges; P9-03 free-path ADR acceptance.
BENCHMARKS / NÚMEROS REALES: none measured.
GAPS CONOCIDOS:
- P1-13: never run in Revit; cursor is a position not a snapshot (rows added at or before the cursor id are not seen); unkeyed check; a save_as copy accepts its source's cursor; q-walls-next assumes no write between q-walls and itself.
- P3-08: small core change (ProjectStore.Commit lock → store_locked) covered by CLI tests only; empty edit sets commit a no-op revision; commit_incomplete reported conservatively.
- P4-09: fake transport only; untracked dependents need caller acceptance; confirm-to-execute window narrowed, not closed; not yet in the CLI (P4-10); replan_internal unreachable by tests.
ARCHIVOS CAMBIADOS: core/Bim.Core/{RevitCommands,RevitCommandsQuery,Model,DocUpdateDependents}.cs, runtimes/revit-native/CommandsExecutorQuery.cs, interfaces/cli/{ApplyEditsCommand,EditsInput,DiffEditsCommand,DocUpdateCommand,Program}.cs, tests/revit-commands/{ElementQuery,ElementQueryCursor,Program}.cs, tests/mcp/Program.cs, tests/integration/{fake-revit-command-executor,test-catalogue-session}.ps1, scripts/owner-catalogue-steps.ps1, tests/cli/{ApplyEditsTests,Program}.cs, tests/doc-regeneration/{UpdateDependents,Program}.cs; board/features/contracts/attempts/handoff/owner-verification-session.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); dotnet run --project tests/revit-commands; dotnet run --project tests/mcp; dotnet run --project tests/cli; dotnet run --project tests/doc-regeneration; dotnet build runtimes/revit-native/Bim.Revit.csproj --configuration Release --warnaserror.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P6-10, P7-09 (relay168); P2-11, P5-11, P10-11 (relay169); P8/P9 (relay171; P9-03 needs owner ADR acceptance); then P1-14/P1-15, P3-09, P4-10.
```
