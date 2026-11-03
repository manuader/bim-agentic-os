# Relay141: structure commands, style skills and edit journal

Cloud scheduled relay, started 2026-11-03 09:52Z. GitHub CI was the only oracle (no local .NET SDK). Three tasks from the phases touched longest ago: Phase 9, Phase 5 and Phase 2 (P4-02 deferred because it shares RevitCommandsCatalogue.cs and CommandsExecutor.cs with P9-02). Each went red in CI, then green, then through an independent read-only review; no blockers, all should-fix items closed with tests (red then green).

```
IMPLEMENTADO:
- P9-02 (Phase 9): structure commands on the catalogue envelope — query.grids, query.structural_types (placement_type), grid.create, structural_column.create, structural_beam.create, foundation.create_isolated (OneLevelBased families only), foundation.create_wall; typed validators, copies-only guard, operation_id receipts, independent readback (plan position, levels, type, column offsets, beam end offsets, footing elevation) with rollback; MCP/audit tables and module contract updated.
- P5-03 (Phase 5): skills/bim.style.compact-modern and skills/bim.style.warm-masonry — 13 shared defaults with different values, 20 traceable rules each (all CABA clear-height rooms, at least as strict as CABA), priority 30; qa pack clear-height default raised to the CABA 2.6 m minimum.
- P2-03 (Phase 2): core MirrorJournal (bounded, id budget, paging, peek, per-copy baselines, failure-aware completeness, add-in vs external origin) and MirrorSnapshotDiffer; native JournalRecorder on DocumentChanged attached only after fixture config validation; raw pipe op journal.read.
TESTEADO AUTOMÁTICAMENTE (backend):
- P9-02 tests/revit-commands/CatalogueStructure.cs: red 41615462210, green 41616435391; review fixes red 41620590939, green 41621106554. 0 failed attempts.
- P5-03 tests/skills/StyleSkillTests.cs: red 41616555865, green 41618154495; review fixes red 41622329866, green 41623346293. 1 failed attempt (test helper compile).
- P2-03 tests/mirror-journal (new project in verify.ps1): red 41615315519, green 41616656660; review fixes red 41624740507, green 41624965893. 1 failed attempt (harness core-boundary comment).
- The merged integration head runs CI again before main moves.
VERIFICADO EN REVIT NATIVO: nothing new.
BLOQUEADO / POR QUÉ: nothing new blocked. Structure commands and the journal recorder need an owner Revit session on a test copy.
DECISIONES QUE NECESITO DE VOS: none. No new dependencies, no BIM-IR schema or persisted-format change (journal JSON is diagnostic, never persisted).
BENCHMARKS / NÚMEROS REALES: none.
GAPS CONOCIDOS:
- Structure: never run in Revit; no native probe script; no column absolute elevation / beam curve z readback; no arc grids, slanted columns, curved beams; joins and analytical elements not diffed.
- Styles: defaults have no per-default rule id; element rules carried but not evaluated (new P5-05); styles not yet consumed by the intent compiler (new P5-04); qa (priority 50) still overrides style clear heights.
- Journal: compiled only; journal.read is a raw pipe op, not catalogue/MCP (new P2-05); deleted elements numeric ids only; a non-peek paged read moves the baseline on page one.
ARCHIVOS CAMBIADOS: core/Bim.Core/RevitCommandsStructure.cs, RevitCommandsCatalogue.cs, MirrorJournal.cs, MirrorJournalDiff.cs; runtimes/revit-native/CommandsStructure.cs, CommandsExecutorStructure.cs, CommandsExecutor.cs, CommandsExecutorQuery.cs, JournalRecorder.cs, App.cs; skills/bim.style.*/skill.json, skills/bim.qa.room-basics/skill.json; tests/revit-commands/**, tests/mcp/Program.cs, tests/audit/Program.cs, tests/skills/**, tests/mirror-journal/**; scripts/verify.ps1; docs/contracts/module-contracts.md; tasks.md; docs/features.json; docs/agent/ATTEMPTS.md; docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK) — runs revit-commands, skills and mirror-journal tests; dotnet build runtimes/revit-native/Bim.Revit.csproj --configuration Release --warnaserror.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest-touched phases: P4-02 documentation commands (Phase 4), P8 (only 🔒 P8-03 left — load a new batch), P1-05 modify/delete or P3-03 plan runner; then P6-04/P7-03/P10-04/P10-05, new P2-04/P2-05/P5-04/P5-05, P9-03/P9-04.
```

## Owner probes (Revit, test copy only)
- Structure: in a fixture session run `query.structural_types` for column/framing/foundation, then grid.create, structural_column.create, structural_beam.create, foundation.create_isolated and foundation.create_wall on a basic wall; expect readback success and receipts; a replay with the same operation_id returns the receipt.
- Journal: send `{"DocumentPath":"<fixture path>","Operation":"journal.read","Metres":[]}`; move/delete/draw walls by hand, run one catalogue wall.create, undo the move, read again: external entries with the right UniqueIds, the CLI write skipped (`skipped_add_in` ≥ 1), an external TransactionUndone, diff changed/removed/added; a third read is empty; `"Journal":{"peek":true}` repeats the same reply.
