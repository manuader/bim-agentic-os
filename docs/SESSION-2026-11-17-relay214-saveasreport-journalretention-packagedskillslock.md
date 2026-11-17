# Relay214: catalogue-report save_as check, fake journal retention, packaged skills-lock

Relay214 (cloud scheduled, 2026-11-16 23:53Z) took the FREE LOCK, picked the oldest-touched phases 1, 2 (relay209/210) and 5 (relay211), got three workers green, ran three independent read-only reviews, closed the should-fixes of P2-25 and P5-20 with tests on their branches, and integrated through one integration head re-run in CI before main moved. Phases 4 and 7 had no open tasks; their next roadmap batch (MCP parity tools) was loaded. CI is the only oracle (no local .NET SDK or pwsh).

```
IMPLEMENTADO:
- P1-25 (Phase 1): catalogue-report adds save_as {state, detail, file, recorded_sha256, disk_sha256, disk_bytes} after copy when the run record names save_as_path; re-hash of the -guardado.rvt in the run folder through BoundedInput against save_as_sha256; states matches, mismatch, missing, unreadable, link_refused, outside_run, no_recorded_sha256.
- P2-25 (Phase 2): the -ExternalEdits fake journal keeps at most its retained capacity (default 1024, test lever), evicts oldest, reports dropped and journal_complete false exactly as RawRead over a Core MirrorJournalTracker with the same capacity.
- P5-20 (Phase 5): characterization — skills-lock pin/verify over a real packaged skills directory outside the repository; CLI, MCP and in-process SkillLocking agree; tampered/malformed skill.json refuse identically.
- Board: P1-25, P2-25, P5-20 ✅. New: P1-26 (stream the save_as hash), P4-20 doc_update MCP tool, P4-21 doc_realized MCP tool, P7-20 plan_layouts MCP tool, P7-21 generate_prototypes MCP tool. Phase 5 has no open tasks left. Progress 204/221 (92.3 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-25: red 43983649557, green 43985584892. Review 0 blockers; should-fix (whole-file read, 1 GiB cap) → P1-26; the two extra states documented in module-contracts.
- P2-25: red 43983628078, green 43986119970. Review 0 blockers; should-fixes (default 1024 never exercised, capacity 1 untested) closed 176cd56, green 43991000455.
- P5-20: green 43983471387; detection proven by deliberate red 43986334542, revert 43988470404. Review 0 blockers; should-fix (tamper envelope thinly asserted) and nits (restore failure, verify never writes, packaging preserves pack bytes) closed 161a742, green 43993398956.
- 0 failed attempts. Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒 (now also exercises the save_as report member).
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P1-25: save_as read whole into memory (>1 GiB → unreadable/input_too_large) → P1-26; never run against a real Revit Save As.
- P2-25: Core IdBudget (200000 ids) not modelled in the fake; the pipe fake has no capacity parameter.
- P5-20: test-only; child processes run with the harness working directory.
ARCHIVOS CAMBIADOS: interfaces/cli/CatalogueReportCommand.cs; tests/cli/{CatalogueReportTests.cs,PackageLayoutPackTests.cs}; tests/integration/fake-revit-command-executor.ps1; tests/mirror-journal/MirrorDriftLoopChecks.cs; docs/contracts/module-contracts.md; board/features/attempts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P6-21/P6-22, P3-19 (relay211); P10-22/P10-23/P10-24 (relay213); P4-20/P7-20 (new MCP batch, McpServer.cs registration serialized); P1-26, P2-26; load the next phase 5 batch. Owner: ADR0010/ADR0011; one -Catalogue run.
```
