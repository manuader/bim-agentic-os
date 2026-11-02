# Relay134 — semantic graph, project brief, audit and permissions

Cloud scheduled relay, 2026-11-02. This was the first cloud relay able to push to `baton/state` and read the GitHub API (relays 27–133 were blocked). There is no local .NET SDK because egress to dot.net and nuget is refused, so GitHub CI was the only compiler and oracle. Each task went red in CI, then green, then through an independent read-only review. Every should-fix finding was closed with a test before merge.

```
IMPLEMENTADO:
- P2-02 (Phase 2): derived typed semantic graph over the existing BIM-IR. Edge kinds are on-level, hosted-by, bounds and depends-on. Includes relation queries and an iterative deterministic closure. AffectedBy(plan, before, after) equals Compiler.Diff DirtyIds. No schema change.
- P6-03 (Phase 6): versioned project-brief format with per-value provenance and confidence. Includes a strict bounded parser, a semantic validator, a canonical serializer, the JSON Schema docs/contracts/brief.schema.json and the example examples/brief/apartment-2br.json.
- P10-03 (Phase 10): permission policy with three levels: read, write-copies and write-real-project. Copies roots are explicit (default .bim/native-runs), and a real-project write needs an exact enablement. Also a SHA-256 hash-chained JSONL audit log with exclusive flushed appends and tamper/torn-tail detection, plus a gate that audits every decision.
TESTEADO AUTOMÁTICAMENTE (backend):
- tests/graph: 80 checks. Red CI 41510226668, green 41511322582.
- tests/brief: about 122 checks. Red 41513018300, green 41514529827.
- tests/audit: 142 checks. Red 41516526694, green 41517584141.
- All runs on Ubuntu and Windows (verify.ps1). The merged integration head runs CI again before main moves.
VERIFICADO EN REVIT NATIVO: nada nuevo (no Revit in the cloud).
BLOQUEADO / POR QUÉ: C04/P1-04 catalogue re-run and N21 run 3 need the owner's Revit session.
DECISIONES QUE NECESITO DE VOS: none for this batch.
BENCHMARKS / NÚMEROS REALES: none measured.
GAPS CONOCIDOS:
- The permission gate is not yet wired into CLI/MCP, and there is no loader for grants and enablements.
- Without an external head anchor, the audit log cannot detect truncation of whole trailing entries.
- Briefs are not yet extracted from documents (P6-01/P6-02 pending).
- Graph edge typing relies only on Element.Kind.
ARCHIVOS CAMBIADOS: core/Bim.Core/Graph.cs, Brief*.cs, Permission.cs, PermissionAuditGate.cs, Audit.cs; tests/graph, tests/brief, tests/audit; docs/contracts/brief.schema.json; examples/brief/; scripts/verify.ps1 (wiring); tasks.md; docs/features.json.
COMANDOS EXACTOS PARA REPRODUCIR: pwsh -File scripts/verify.ps1 (.NET 8 SDK).
PRÓXIMO ÍTEM DE MAYOR IMPACTO: P7-01 constraint space planner on the brief (now ⏳); P1-02/P2-01/P3-01/P4-01/P5-x/P8-x/P9-x as the board orders.
```
