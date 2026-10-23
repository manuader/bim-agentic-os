# Persistent state — scoped evaluation cycle

Cycle baseline: main65549ec; evaluation code integrated through a4f26ba. Product vision is the existing root BIM_Agentic_OS_PROMPT_DEFINITIVO.md; current cycle authority is CYCLE_SCOPE.md. No product rediscovery.

Observed implementation: C#/.NET8 core and CLI, file intended-state store, deterministic room intent/diff/dependency closure/cycle rejection, guarded Revit25.2 ExternalEvent pipe executor and native ExtensibleStorage receipts. SDK8.0.415 available locally. 50 core assertions and17 checkpoint guard assertions passed in recorded runs. Native primitive create/retry/+400mm three-wall patch/readback/rollback/save-reopen passed; evidence artifacts/native-checkpoint-result.json. This is level/wall scope only, not full Test A/B.

Implemented this cycle: strict C# evaluation-only contracts/oracle, shared JSON CLI and scripts/evaluate.ps1, explicit40 mandatory A/B criteria, nullable metrics, blocked backend declarations, engineering CI wiring, exact eight-state capability gate, temporary process isolation/restoration, environment census, golden templates and persistent plans/ADRs. The offline oracle is unit-tested; actual IFC/Revit full A/B execution is blocked. Individual measured fields can add stricter checks when observations exist. No existing BIM-IR/RPC schema changed.

Controller evidence: full gate f40b7c7 exit0 (50 core,42 evaluator,17 checkpoint assertions, five evaluator CLI checks); last evaluator-only guard a4f26ba warning-free builds,43 evaluator assertions and five CLI checks passed. Dependency vulnerability audit passed all nine projects. Independent reviews resolved reference/role/parameter/environment/revision false positives; no unresolved finding in reviewed deltas. Summary: artifacts/evaluation-engineering-result.json. Worker raw logs were copied locally before archiving the isolated worktrees.

Missing: full Test A room with hosted door/two windows/Room/dimensions/plan/3D/schedule/sheet; full Test B hosted/documentation update verification and measured comparison. No IfcOpenShell installed in detected Python3.12.14; no actual IFC executor implemented. No complete typed Geometry/Units/Capability/SemanticDiff production contract. Do not modify existing BIM-IR schema without user-approved proposed ADR.

Frozen out-of-cycle material: Blender development skill, competitive research, future adapter notes and generative/kernel vision. Preserve; no implementation this cycle. IFC validation is explicitly in cycle. No actual Blender/Rhino/CAD/vision/biomimetic adapter exists.

Latest one-command results: Revit exit2,40 blocked criteria; IFC exit2,32 blocked and8 unsupported criteria; both ProductAcceptance=false. Sources explicitly declare static adapter capabilities, not live runtime probes. Reports: artifacts/evaluation-revit-result.json and artifacts/evaluation-ifc-result.json, launched from62bde0a. No new native writes or IFC integration ran this cycle. Existing saved primitive evidence stays separate. Native runtime/source already exists and must be reused. Do not use production Revit model as fixture; serialize writes.

In progress: the user approved retaining Named Pipes and isolated IfcOpenShell under LGPL-3.0-or-later on2026-10-22 (America/Buenos_Aires). Implementing the real headless IFC executor and bounded C# process bridge in separate worktrees, followed by actual create/resize/readback/retry/rollback integration and Ubuntu CI. See docs/plans/2026-10-22-ifc-executor.md. No new integration success is claimed yet. IFC-native limitations remain explicit. Next, extend the isolated Revit Test A fixture to hosted elements/Room/documentation and complete Test B.

Decisions pending: future typed IR schema evolution remains separate approval. Named Pipes and the optional IfcOpenShell LGPL dependency are approved in ADR0005/0006.

