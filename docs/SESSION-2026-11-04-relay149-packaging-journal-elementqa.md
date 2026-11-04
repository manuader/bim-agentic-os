# Relay149: release packaging, query.journal, element QA skill pack

Cloud scheduled relay, started 2026-11-04 03:52Z. GitHub CI was the only oracle (no local .NET SDK). The oldest-touched phases were 1, 2, 5 and 10 (relay145); P2-05, P5-05 and P10-04 were picked (P1-07/P1-08 left for the next relay because the owner chat was active on Revit command files: it merged chat-materials-types and then view.export_image + document.save + policy reload while this relay ran). Each task went red in CI, then green, then through an independent read-only review (no blockers; should-fix closed with tests in one round).

```
IMPLEMENTADO:
- P10-04 (Phase 10): scripts/package.ps1 / package-common.ps1 / package-verify.ps1 / package.version — deterministic release layout (VERSION, cli/, mcp/, revit/Bim.addin.template) with manifest.json (SHA-256, ordinal order, version + source commit), forbidden-file and symlink scan, hermetic build tree, never-overwrite output with failure-path cleanup.
- P2-05 (Phase 2): query.journal read-only catalogue/MCP command mirroring journal.read (shared tracker, byte-identical readback pinned by a golden literal, args refused before the pipe, shared-baseline warning, AsJournalReadReply for MirrorDrift).
- P5-05 (Phase 5): skills/bim.qa.element-basics 1.1.0 + SkillElementValidator — wall thickness, opening size, inside-host clearances (producer tolerance), door sill at level, opening/host level; typed not_evaluable, hosted [offset,width] layout, unit_mismatch.
- Board: P9-06 and P9-08 rows updated (owner chat merged materials/types and document.save); new ⏳ P2-06 provenance ledger, P2-07 drift resolution plan, P10-06 jurisdiction registry, P10-07 add-in install from the package.
TESTEADO AUTOMÁTICAMENTE (backend):
- P10-04 tests/packaging/test-package.ps1 (via verify.ps1, ubuntu + windows): red 41770507469, green 41771650673; review red 41773123609, green 41773791843. 1 failed attempt.
- P2-05 tests/revit-commands CatalogueJournal + mcp + audit: red 41770311749, green 41771382126; review red 41774729673, green 41775145810. 1 failed attempt.
- P5-05 tests/skills ElementSkillTests: red 41770682412, green 41771404693; review red 41776126314, green 41777099861. 1 failed attempt.
- The merged integration head runs CI again before main moves.
VERIFICADO EN REVIT NATIVO: nothing new (no owner session this relay).
BLOQUEADO / POR QUÉ: nothing new blocked.
DECISIONES QUE NECESITO DE VOS: still open — escritorio/estudio/biblioteca class; Uniclass CC BY-ND excerpt.
BENCHMARKS / NÚMEROS REALES: two clean package builds byte-identical within one OS (ubuntu and windows).
GAPS CONOCIDOS:
- P10-04: no Bim.Revit.dll, installer, archive or signing (P10-07 loaded); per-OS reproducibility only; no macOS packaging run.
- P2-05: executor never run in Revit; not in owner-catalogue-steps; non-peek reads consume the baseline shared with journal.read.
- P5-05: illustrative bounds; axis-span clearances; no overlap/door swing checks.
ARCHIVOS CAMBIADOS: scripts/package*.ps1, scripts/package.version, scripts/verify.ps1, tests/packaging/**; core/Bim.Core/RevitCommandsJournal.cs, core/Bim.Core/Mirror.cs, runtimes/revit-native/CommandsExecutorJournal.cs, runtimes/revit-native/CommandsExecutor.cs, runtimes/revit-native/JournalRecorder.cs, tests/revit-commands/{CatalogueJournal.cs,MirrorSnapshot.cs,Program.cs}, tests/mcp/Program.cs, tests/audit/Program.cs; skills/bim.qa.element-basics/skill.json, core/Bim.Core/SkillElementValidator.cs, tests/skills/{ElementSkillTests.cs,Program.cs}; docs/contracts/module-contracts.md; tasks.md; docs/features.json; docs/agent/ATTEMPTS.md; docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK); pwsh -File scripts/package.ps1 -OutputDirectory <new dir>; pwsh -File scripts/package-verify.ps1 -PackageDirectory <dir>.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: P1-07 command.batch / P1-08 document.save_copy (coordinate with the owner chat's document.save), P9-06 hardening port, P9-07/P9-09, P9-03/P9-04, P8-06/P8-07, P3-05, P4-04/P4-05, P6-06/P6-07, P7-05, P5-04, P2-06/P2-07, P10-06/P10-07.
```
