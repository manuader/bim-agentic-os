# Relay207: fake recorder failures, nested scaled Form XObject PDF, packaged MCP skill packs

Relay207 (cloud scheduled, 13:03Z) picked the oldest-touched phases 2 and 6 (relay203) and 5 (relay200). GitHub returned 503 "authorization temporarily unavailable" for about two minutes at the start and intermittently on pushes; pushes went through a retry wrapper. GitHub CI was the only oracle (no local .NET SDK or pwsh).

```
IMPLEMENTADO:
- P2-23 (Phase 2): the -ExternalEdits fake add-in models recorder failures (test lever Add-FakeRecorderFailure); journal.read failures, failures_since_baseline (per (document UniqueId, path) baseline) and journal_complete equal RevitJournalQuery.RawRead over a Core MirrorJournalTracker.
- P6-19 (Phase 6): brief-plan PDF twins with labels in a Form XObject nested two deep under a scaled /Matrix, and with regions inside a scaled form; same draft, brief and pinned layouts digest; provenance carries the nested XObject path. No adapter change was needed.
- P5-18 (Phase 5): SkillPackLookup.PackageRoot accepts a binary directory named mcp, so the packaged MCP server's generate_typology reads <package>/skills like the packaged CLI.
- Board: P2-23, P6-19, P5-18 ✅. New: P2-24 (failures behind the named-pipe fake), P2-25 (fake journal retention/dropped entries), P5-19 (packaged MCP parity hardening), P5-20 (skills-lock over a packaged skills directory), P6-21 (cad-brief block-regions flag). Progress 189/207 (91.3 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P2-23 tests/mirror-journal MirrorDriftLoopChecks FakeFailureChecks (11 in-process reads vs RawRead, lever refusals, legacy bytes): red 43431481245, green 43433842666. 0 failed attempts.
- P6-19 tests/cli/CadPdfToLayoutsTests.cs P6-19 section (fixture pins f9b7d231…/ac1305d0…, generator byte rerun, page-space points, provenance, layouts 56605898…, real CLI chain on the nested variant): red 43431835011, green 43434231951. 0 failed attempts.
- P5-18 tests/mcp SkillPackPackageRootTests + tests/cli/PackageLayoutPackTests.cs oracle 5 (real package, mcp/Bim.Mcp.dll over stdio, byte-equal to packaged CLI, pin a8c62da5, refusal envelopes): red 43431460027, green 43433910409. 0 failed attempts.
- Independent read-only reviews: 0 blockers. P5-18 should-fix (stale contract comment and docs) closed at integration (GenerateTypologyTool.cs header, module-contracts, features.json). P6-19 should-fix (regions-in-form only in process) recorded as evidence wording and new P6-21. Nits recorded (P5-19).
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. Optional: repository licence.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P2-23: lever in-process only (→ P2-24); the fake never drops entries (→ P2-25).
- P6-19: uniform diagonal matrices only (no rotation/shear); regions-in-form through the CLI needs a flag (→ P6-21); generator rerun skipped on Windows.
- P5-18: directory name matched case-sensitively (as cli/); "(composed pin)" parity case does not assert the hash (→ P5-19).
ARCHIVOS CAMBIADOS: tests/integration/fake-revit-command-executor.ps1; tests/mirror-journal/MirrorDriftLoopChecks.cs; tests/cad/fixtures/{brief-plan-pdf.py,brief-plan-nested.pdf,brief-plan-nested-regions.pdf}; tests/cli/{CadPdfToLayoutsTests.cs,PackageLayoutPackTests.cs}; interfaces/cli/SkillPackLookup.cs; interfaces/mcp/GenerateTypologyTool.cs (comment); tests/mcp/{SkillPackPackageRootTests.cs,Program.cs}; board/features/contracts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P3-17, P7-18 (relay204); P4-18, P10-20/P10-21/P10-22, P1-18 (relay205/206); then P2-24, P5-19/P5-20, P6-20/P6-21. Owner: ADR0010/ADR0011; one -Catalogue run.
```
