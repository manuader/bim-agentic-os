# Relay171: Rhino openings, document.save_as, catalogue-report CLI

Cloud scheduled relay, started 2026-11-10 00:53Z (LOCK was FREE after relay169). GitHub CI was the only oracle (no local .NET SDK, pwsh or PyPI). Oldest-touched phases 8, 9 and 1 (relay161/166): P8-09, P9-08 and P1-12. Three workers in isolated worktrees, TDD red→green in CI, independent read-only reviews (0 blockers, 10 should-fix), every should-fix and the cheap NITs closed with tests, integration head re-run in CI before main moved. No origin chat-* branch overlapped (chat-wall-attach, still unmerged, touches wall commands only).

```
IMPLEMENTADO:
- P8-09 (Phase 8): .3dm writer exports door/window (existing BIM-IR shape [ax,ay,bx,by,sill,height], [level, host wall]) as opening-volume Extrusions on BIM::Doors/BIM::Windows with bim.* user strings; strict import reads them back and refuses host/geometry mismatches; draft import proposes openings on their host wall with confidence and notes, footprint axis chosen by host, per-opening writer check (opening_outside_host) so one bad object never fails the draft.
- P9-08 (Phase 9): Revit command document.save_as into enabled roots — new .rvt only, never overwrite, test copy only into .bim/native-runs, strict path rules (UNC/device/relative/'/'/../streams/8.3/device names/backup names, ≤249 chars), 13 typed save_as_refused reasons before the pipe and again in the add-in, post-save location check resolving parent links (detection), excluded from command.batch; MCP tool; parity census file.save_as.
- P1-12 (Phase 1): read-only `catalogue-report --run .bim/native-runs/<run>` — finds the owner -Catalogue record through the session summary, checks it, prints per-group counts, failing steps requested vs observed, blocked steps and the copy sha256 re-hashed on disk, deterministic JSON; typed refusals; end-to-end on records written by the real owner record writer (fake executor).
- Board: P8-09, P9-08, P1-12 ✅. No new batch needed (P1-13, P9-03 still open; P8 remaining item is 🔒 Blender).
TESTEADO AUTOMÁTICAMENTE (backend):
- P8-09 tests/rhino (rhino3dm 8.17, 209 tests): red 42732771455, green 42734174706; review red 42739322798, green 42739411198. 1 failed attempt.
- P9-08 tests/revit-commands + tests/mcp + native-build (--warnaserror): red 42732929714, green 42735264245; review red 42741057367, green 42741689855. 1 failed attempt.
- P1-12 tests/cli (ubuntu/windows/macOS, pwsh end-to-end): red 42733295077, green 42734609677; review red 42747523781, green 42749563142. 0 failed attempts.
- Merged integration head re-run in CI before main moved (see journal).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: nothing new blocked.
DECISIONES QUE NECESITO DE VOS: nothing new; still open — escritorio/estudio/biblioteca class; Uniclass excerpt licence; party-wall facade edges.
BENCHMARKS / NÚMEROS REALES: none measured.
GAPS CONOCIDOS:
- P8-09: never opened in Rhino; host walls not cut; overlaps not refused; draft ignores curve/brep/profile-hole openings; a pre-P8-09 importer refuses files with openings.
- P9-08: never run in Revit (SaveAs re-pointing, dialogs on upgraded files, OverwriteExistingFile=false, relative links after Save As unverified); post-save check is detection, not prevention; owner-open-project latest.json keeps the old path; not yet a -Catalogue step.
- P1-12: never run on a real owner record; sha row matched by its Spanish check text; exit 0 even when failures are reported.
- CI flake: macOS "Build CLI and bounded CLI input tests" step failed without annotation three times on relay171 branches and passed on rerun; ubuntu step-68 pipe_io_error flake still open.
ARCHIVOS CAMBIADOS: adapters/rhino/{bim_rhino,rhino_draft}.py, adapters/rhino/README.md, tests/rhino (+fixtures/openings.json, run.py); core/Bim.Core/{RevitCommandsSaveAs,RevitCommands,RevitCommandsCatalogue,RevitCommandsBatch,RevitCommandsModify,RevitCommandsService}.cs, runtimes/revit-native/{CommandsExecutorSaveAs,CommandsExecutor}.cs, interfaces/mcp/McpServer.cs (comment), tests/revit-commands, tests/mcp, capabilities/revit-parity-*, docs/revit-playbook.md; interfaces/cli/{CatalogueReportCommand,Program}.cs, tests/cli (+catalogue-report fixtures, catalogue-report-record.ps1, csproj link); board/features/contracts/attempts/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); .tools/rhino/bin/python -I tests/rhino/run.py; dotnet run --project tests/revit-commands; dotnet run --project tests/mcp; dotnet run --project tests/cli; dotnet build runtimes/revit-native/Bim.Revit.csproj --configuration Release --warnaserror.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P1-13, P3-08/P3-09 (relay166); then P4-09, P6-10, P7-09 (relay168); then P2-11, P5-11, P10-11 (relay169); P9-03 (free path ADR, needs owner acceptance). Add document.save_as to the owner -Catalogue copia group; consider a task for the two CI flakes.
```
