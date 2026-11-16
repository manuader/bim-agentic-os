# Relay209/210: owner record names the save_as file, bcf-export verb, recorder failures behind the named-pipe fake

Relay209 (cloud scheduled, 2026-11-13 18:53Z) picked the oldest-touched phases 1 and 10 (relay205/206) and 2 (relay207); its three workers went green at 19:37Z and the session ended without releasing the LOCK. Relay210 (2026-11-16 16:53Z) took over the stale LOCK (last heartbeat ~3 days old), ran the independent reviews and integrated. GitHub CI was the only oracle (no local .NET SDK or pwsh).

```
IMPLEMENTADO:
- P1-18 (Phase 1): the -Catalogue run record gains save_as_path (full path of the -guardado.rvt file) and save_as_sha256 (read-back value, equal to disk) next to copy_path when c-as-save passed, null otherwise. docs/owner-verification-session.md lists both members.
- P10-20 (Phase 10): new CLI verb `bcf-export issues.json out.bcfzip` — strict bounded issues JSON, BcfZip written CreateNew (never overwrite, partial file removed), one JSON line; typed bcf_export_* refusals.
- P2-24 (Phase 2): fake-revit-command-pipe.ps1 -RecorderFailures "<opid>:<count>,…" (test-only, needs -ExternalEdits) injects recorder failures between calls over the real pipe.
- Board: P1-18, P10-20, P2-24 ✅. New: P1-25 (catalogue-report re-hashes the save_as file), P2-26 (pipe fake injects only after a succeeded command; unmatched ids reported), P10-23 (bcf-export refusal edges). Progress 195/213 (91.5 %).
TESTEADO AUTOMÁTICAMENTE (backend):
- P1-18 tests/integration/test-catalogue-session.ps1 + tests/cli/CatalogueReportTests.cs (null cases: wrong sha, failure after Save As, refused save_as, v1-only; real CLI Linux null / Windows positive; catalogue-report bytes unchanged with new + unknown members): red 43487538890, green 43489314080. 0 failed attempts.
- P10-20 tests/cli/BcfExportCommandTests.cs (real CLI bytes equal in-process WriteZip and tests/bcf golden SHA-256; each refusal pinned with no file written): red 43487521380, green 43489782743. 0 failed attempts.
- P2-24 tests/mirror-journal/MirrorDriftLoopChecks.cs (j0–j4 over the real pipe member-equal to RawRead over a Core tracker with the same failures; 7 option refusals; option-less byte pins unchanged): red 43487386618, green 43490516466. 0 failed attempts.
- Independent read-only reviews: 0 blockers. P1-18 should-fix (owner doc did not list the members) closed at integration; P2-24 should-fix (injection keys on the received id, refused/dry-run included) → comment corrected + P2-26; P10-20 nits → P10-23.
- Merged integration head re-run in CI before main moved (see journal on baton/state).
VERIFICADO EN REVIT NATIVO: nothing new (no owner session).
BLOQUEADO / POR QUÉ: P2-11 (ADR0010) and P9-03/C05 (ADR0011) wait for the owner; Phase 8 remainder is 🔒 Blender; owner -Catalogue run 🔒.
DECISIONES QUE NECESITO DE VOS: ADR0010 and ADR0011 (recommended A), unchanged. Optional: repository licence.
BENCHMARKS / NÚMEROS REALES: none new.
GAPS CONOCIDOS:
- P1-18: catalogue-report does not re-hash save_as_path (→ P1-25); no fault case for a good c-as-save reply with no file on disk; never on a real owner record.
- P10-20: timestamp offset form (+0200), version 1.0 and NUL path not pinned; golden SHA-256 copied from tests/bcf (→ P10-23).
- P2-24: injection also fires after a refused/dry-run request with that id; unmatched ids dropped silently (→ P2-26).
ARCHIVOS CAMBIADOS: scripts/owner-native-session.ps1; interfaces/cli/{BcfExportCommand.cs (new),Program.cs}; tests/cli/{BcfExportCommandTests.cs (new),Program.cs,CatalogueReportTests.cs}; tests/integration/{test-catalogue-session.ps1,fake-revit-command-pipe.ps1}; tests/mirror-journal/MirrorDriftLoopChecks.cs; docs/owner-verification-session.md; board/features/contracts/session/handoff.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest touched now — P5-19/P5-20, P6-20/P6-21 (relay207); then P3-18, P4-19, P7-19 (relay208); then P10-21/P10-22/P10-23, P1-25, P2-25/P2-26. Owner: ADR0010/ADR0011; one -Catalogue run.
```
