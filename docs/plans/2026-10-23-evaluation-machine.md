# Evaluation machine implementation plan

Goal: implement the approved evaluation design before new product features.
Architecture: preserve existing core/runtime; separate strict acceptance oracle, real backend observations and legacy historical evidence. No model schema migration.
Tech stack: existing.NET8/PowerShell; optional IfcOpenShell only with approval.
Spec: [design](2026-10-23-evaluation-design.md), [cycle scope](../agent/CYCLE_SCOPE.md).

- [x] Capture environment and persistent memory from commands/evidence; preserve raw logs locally and redact public paths.
- [x] Reclassify capability registry and TDD gate regressions to exact eight states.
- [ ] Obtain RPC/IFC decisions; coreC# and evaluation design explicitly confirmed. Do not perform dependent changes while pending.
- [x] TDD strict A/B oracle and metrics, single-command runner, backend prerequisite/unsupported handling; no relaxed criteria.
- [ ] Execute available real backend probes; IFC only after dependency approval. Record narrower-than-TestA evidence distinctly.
- [x] Add CI and exact native VM procedure; full gates and independent review.
- [x] Conventional commits per green task; update STATE/BLOCKERS/ATTEMPTS and report required final headings.

Review focus: stale/historical evidence, missing mandatory hosted/docs output, false zeros, backend spoofing by unit fixtures, invalid/stale IDs and inconsistent revision/rollback evidence. Human golden cases remain unapproved until architect supplies them.

Evidence: artifacts/evaluation-engineering-result.json, evaluation-launcher-result.json and backend report files. Controller launchers returned exit2 with40 Revit blocked criteria and32 IFC blocked/8 unsupported. These are executions of the evaluation machine, not real backend probes. Prior native primitive execution remains preserved separately. Actual IFC work waits for dependency approval; full native fixture authoring remains incomplete. Engineering verification and independent review passed; persistent memory and scoped results are recorded. Unchecked items remain genuine blockers, not completed product acceptance.

