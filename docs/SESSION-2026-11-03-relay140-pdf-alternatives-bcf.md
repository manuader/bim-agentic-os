# Relay140: PDF vector reader, generative alternatives and BCF export

Cloud scheduled relay, started 2026-11-03 06:53Z. GitHub CI was the only oracle (no local .NET SDK). Three tasks from the phases touched longest ago: Phase 6, Phase 7 and Phase 10. Each went red in CI, then green, then through an independent read-only review; P6-02 had blockers in two review rounds, all closed with tests (red then green).

```
IMPLEMENTADO:
- P6-02 (Phase 6): BCL-only PDF vector reader (adapters/cad/Bim.Cad/Pdf*.cs): paths, text, CTM/q/Q, Form XObjects with Do-chain provenance, concatenated /Contents, object streams, FlateDecode, latest revision wins; scale calibration from a known dimension and alignment; conversion to the DXF entity model so wall-pair detection runs on calibrated PDFs; bounded with stable refusal codes (incl. interpret-limit, element-limit).
- P7-02 (Phase 7): core/Bim.Generative Alternative*: N (1..64) seeded alternatives, geometric dedup, six weighted criteria with exact explanations, infeasible reasons, stable ranking, canonical JSON.
- P10-02 (Phase 10): core/Bim.Core/Bcf.cs + BcfZip.cs: validation issues → BCF 3.0 zip with deterministic GUIDs, IFC GUID compression, hand-written stored zip (golden SHA-256 identical on Linux and Windows), per-field and total limits.
TESTEADO AUTOMÁTICAMENTE (backend):
- P6-02 tests/cad/PdfTests.cs: red 41594156959, green 41594792302; review fixes red 41596777670, green 41597534501; re-review fixes red 41605087498, green 41605199617. 0 failed attempts.
- P7-02 tests/generative/AlternativeTests.cs: red 41593219777, green 41593735926; review fixes red 41598691866, green 41600090447 (2 failed attempts, see ATTEMPTS).
- P10-02 tests/bcf (new project in verify.ps1): red 41593388853, green 41593999314; review fixes red 41601910280, green 41603185340.
- The merged integration head runs CI again before main moves.
VERIFICADO EN REVIT NATIVO: nothing new.
BLOQUEADO / POR QUÉ: nothing new blocked. Official buildingSMART BCF-XML 3.0 XSDs could not be fetched from the cloud (proxy 403), so BCF validity is against project-authored subset XSDs.
DECISIONES QUE NECESITO DE VOS: none. No new dependencies, no schema change.
BENCHMARKS / NÚMEROS REALES: none.
GAPS CONOCIDOS:
- PDF: no real-world PDFs; no font metrics/CID text; FlateDecode only; /Rotate not applied; depth-limit refuses whole document.
- Alternatives: mirrored layouts not deduplicated; compliance is a gate; no circulation model; no CLI/MCP.
- BCF: subset XSDs only; no snapshots/comments; never opened in a viewer; no CLI/MCP caller.
ARCHIVOS CAMBIADOS: adapters/cad/Bim.Cad/Pdf*.cs; tests/cad/PdfTests.cs, tests/cad/Program.cs, tests/cad/fixtures/*.pdf, .gitattributes; core/Bim.Generative/Alternative*.cs; tests/generative/**; core/Bim.Core/Bcf.cs, BcfZip.cs; tests/bcf/**; scripts/verify.ps1; tasks.md; docs/features.json; docs/agent/ATTEMPTS.md; docs/HANDOFF.md.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK) — runs cad-tests, generative-tests and bcf-tests.
PRÓXIMO ÍTEM DE MAYOR IMPACTO: oldest-touched phases: P9-02 structure commands (Phase 9), P4-02 documentation commands (Phase 4), P5-03 style skills (Phase 5); then P2-03, P1-05, P3-03/P3-04, new P6-04, P7-03, P10-04, P10-05.
```
