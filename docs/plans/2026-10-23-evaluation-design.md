# Evaluation harness design — approved

Current cycle authority: ../agent/CYCLE_SCOPE.md. Core remains user-ratified C#/.NET8. Existing model/protocol schemas are frozen until separately approved. This design adds evaluation contracts only.

Recommended approach: a C# evaluation service, consumed by the existing JSON CLI, with typed case criteria, backend observations, verification states and metrics. PowerShell provides the single cross-platform entry command; optional Python is isolated to the IfcOpenShell backend. Alternative: Python-only orchestration/oracle over the existing CLI, faster initial setup but another service language and duplicated reporting-contract validation. No product compiler/authoring logic is moved or rewritten.

Test A mandatory criteria remain the master: native level,5x4m clear dimensions,200mm walls,3m height,900mm hosted door,two hosted windows,Room/boundary,dimensions,plan,3D,schedule,sheet,categories/hosting/units/references,save/reopen. Test B requires Test A plus400mm width correction,fixed exterior anchor,unaffected identity,host/dependent/document updates,semantic diff,idempotency and forced-failure rollback. Unsupported or missing mandatory criteria never pass. IFC evidence cannot stand in for native Revit views/sheets/annotations.

Backend adapters must execute or explicitly return blocked/unsupported. Unit fixtures cannot generate integration-tested (ifc) or verified-in-revit claims. Historical primitive native evidence remains narrowly classified and cannot make full A/B green. While the product cannot author the whole Test A, the evaluator must report the missing criteria and a failing/blocked aggregate rather than invent a green case.

Report: verification_state uses exactly the eight user states. outcome is pass/fail; exit0 only if all required criteria pass, exit1 for measured violation and exit2 for blocked/unsupported prerequisites. Report numeric totals (passed/failed/blocked/unsupported), scope, backend/version/source and metrics from master section8. Unmeasured metrics are null with reasons, never zero. One scalar total_ms covers observed harness wall time; it is not full architectural-delivery latency. Do not change existing BIM-IR or raw native operation-status protocol in this task.

TDD: missing door/Room/docs prevents Test A pass; full synthetic observations can unit-test the oracle but remain unit-tested; Test B cannot pass without Test A; duplicate/unmatched IDs and unintended changes fail; unsupported native-only IFC outputs remain explicit; unknown metrics remain unknown. Missing dependency/runtime fails closed. Golden template is manual-only and never a passing case.

No IFC package installation before ADR0006 approval. No gRPC migration while ADR0005 is pending. Actual native authoring remains serialized and isolated; no production model writes. Maximum three failing attempts/test, distinct hypotheses recorded in ../agent/ATTEMPTS.md, no weakened oracles.

Delivery includes census/memory, capability reclassification, golden template, one-command evaluation, automated evaluator tests and CI. Complete A/B authoring can remain blocked with exact criterion gaps; no CAD/vision/Blender/Rhino/generative work.

User decision: recommended C# evaluation design explicitly approved in this session. This approval does not approve IfcOpenShell licensing, RPC migration or BIM-IR schema changes.


