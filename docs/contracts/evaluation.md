# Evaluation contract — v0.1

The approved [design](../plans/2026-10-23-evaluation-design.md) adds evaluation DTOs in `evaluation/Bim.Evaluation`; it does not migrate BIM-IR or native RPC. The CLI consumes the same C# service as the deterministic oracle tests.

`RoomObservation` contains measured clear dimensions in millimetres, element observations, references, identities, save/reopen evidence and documentation values. Individual wall thickness/height, door width, Room area, dimension width and schedule area are nullable readback measurements. Missing measurements block the corresponding criterion; aggregate declarations cannot replace them. Wall geometry is the measured inner-face segment; opening geometry is its host-face point. This is an evaluation representation, not the production geometry schema.

`RevisionObservation` binds before/after/retry/rollback snapshots, operation keys, semantic changes, document updates and revisions. Test B requires a complete Test A. A west anchor remains fixed; the width changes by400mm while depth, categories, hosts, unrelated parameters and native identities remain protected. Opening movement follows measured host movement. The reported semantic diff must match observed changes. Retry preserves the complete resulting snapshot; forced-failure rollback restores the original snapshot and revision.

`EvaluationReport.VerificationState` uses the eight repository evidence states. Criterion `Outcome` uses pass/fail/blocked/unsupported and is a separate axis. Unknown metrics have null values and an explanation. `total_ms` measures evaluator orchestration only; it does not measure architectural correction through validated documentation delivery.

Offline observations exercise the oracle with `unit-tested` provenance and `ProductAcceptance=false`, even when all synthetic checks pass. The runtime entry point owns backend provenance. Current IFC/Revit executor paths report capability blockers; they do not execute full Test A/B, load historical success as current evidence, or accept caller assertions as native verification.

The PowerShell entry point is `scripts/evaluate.ps1 -Backend ifc|revit`. Build logs are separate from JSON stdout, and the output directory contains `report.json`. Exit0 requires all mandatory criteria to pass; exit1 means measured violation; exit2 means mandatory prerequisites are blocked/unsupported. IFC cannot satisfy native Revit annotation, sheet or RVT save/reopen requirements. A green engineering CI checks the machine, not full backend product acceptance.

The IFC implementation remains blocked pending ADR0006 dependency approval. The native implementation currently supports level/wall primitives and their recorded rollback/checkpoint tests, not full hosted elements or documentation. See [STATE](../agent/STATE.md), [BLOCKERS](../agent/BLOCKERS.md) and the [native VM procedure](../agent/NATIVE_VM.md).
