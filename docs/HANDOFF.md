# BIM Agentic OS — general handoff

## 0. Protocol

Arrive by reading this file and the latest ignored handoffs/HANDOFF-*.md, then the active Baton run's OWNER.md and board. Inspect lock before any mutation. At close write a permanent session document, an ignored operational handoff, and update this general document. The third step is mandatory. Owner requests/answers remain in OWNER.md, owner-only actions in OWNER-TODO.md.

## 1. Product and current scope

Persistent semantic BIM with native Revit execution and an incremental compiler. Master requirements remain [MASTER_PROMPT](MASTER_PROMPT.md); current authorized minimal scope is [CYCLE_SCOPE](agent/CYCLE_SCOPE.md). Do not restart discovery or expand into generative/CAD/Blender work.

## 2. Rules

Use [AGENTS](../AGENTS.md), Superpowers TDD and independent review, and [Baton Codex mapping](harness/baton-codex.md). Max3 failures per individual test with recorded hypotheses. One controller, isolated worktrees, disjoint ownership, one heavy command at a time; no concurrent Revit writes. Conventional commits, explicit staging, no AI attribution. Publish evidence, never model binaries or credentials.

## 3. Architecture

Core C#/.NET8 compiles room intents into unchanged persisted records and computes exact incremental plans/dirty closure. Revit uses ExternalEvent/Named Pipes and native receipts. Optional isolated IfcOpenShell currently has a tested fixture and independent readback/schema/geometry checks. C# evaluator separates backend IFC success from full native product acceptance. See [contracts](contracts/module-contracts.md).

## 4. Sessions, newest first

- Relay144 (Claude, cloud scheduled): P1-05 Revit element.set_parameter and element.delete (typed values, dependents probe + confirm, dry-run rollback, compact receipts) under the copies guard, P6-04 calibrated DXF/PDF → non-persisted BIM-IR wall/opening draft with confidence, provenance and uncertainty report, P7-03 1/2/3-bedroom apartment prototypes keeping approved rooms/walls across reconfiguration; reviewed, should-fix closed with tests, green. P8-07 ⏳; new P4-03, P6-05, P7-04, P7-05. [Session](SESSION-2026-11-03-relay144-modify-cad-draft-prototypes.md).
- Relay145 (Claude, cloud scheduled): owner items first — P1-04/C04 ✅ verified-in-revit (owner 17/17), P1-06 one-run owner verification session (81 steps: catalogue v2, views, snapshot, queries, structure, documentation, journal, modify), P5-06 CABA values owner-confirmed; then P2-04 mirror↔Core drift report and P10-05 offline Uniclass classification mapping; reviewed (P2-04 two rounds), findings closed with tests, green. New P1-07 command.batch, P1-08 document.save_copy. [Session](SESSION-2026-11-03-relay145-owner-run-caba-drift-classification.md).
- Relay142 (Claude, cloud scheduled): P4-02 Revit documentation commands (title blocks, linear dimensions, tags, schedules, sheets, view placement), P3-03 resumable plan runner over an injected transport, P8-04 deterministic BIM-IR→glTF 2.0 export for Blender; reviewed, should-fix closed with tests, green. New P8-05, P8-06, P8-07. [Session](SESSION-2026-11-03-relay142-docs-runner-gltf.md).
- Relay141 (Claude, cloud scheduled): P9-02 Revit structure commands (grids, columns, beams, isolated and wall foundations) with elevation-aware readback, P5-03 two contrasting style skill packs with traceable rules composed under CABA (qa clear height raised to 2.6 m), P2-03 DocumentChanged edit journal + snapshot differ (bounded, paged, peek, per-copy baselines); reviewed, should-fix closed with tests, green. New P2-04, P2-05, P5-04, P5-05. [Session](SESSION-2026-11-03-relay141-structure-styles-journal.md).
- Relay140 (Claude, cloud scheduled): P6-02 BCL-only PDF vector reader with calibration/alignment and DXF-model bridge (two review rounds of hostile-input blockers fixed), P7-02 seeded generative alternatives with weighted, explained scoring, P10-02 deterministic BCF 3.0 export (subset XSDs, golden zip hash on Linux/Windows); green. New P6-04, P7-03, P10-04, P10-05. [Session](SESSION-2026-11-03-relay140-pdf-alternatives-bcf.md).
- Relay139 (Claude, cloud scheduled): P1-02 read-only query.elements/query.inspect Revit commands (bounded replies, typed units), P3-02 ExecutionPlan→catalogue v1 command bridge with binding-aware operation ids and level precondition checks, P8-01 IFC import to BIM-IR with deterministic exporter and stable roundtrip on 3 OS; reviewed, should-fix closed with tests, green. New P1-05, P3-03, P3-04. [Session](SESSION-2026-11-03-relay139-query-bridge-ifc-import.md).
- Relay138 (Claude, cloud scheduled): P2-01 read-only query.snapshot Revit→BIM-IR mirror (lossy cases reported unmapped), P5-02 CABA Código de Edificación room rule pack + SkillRoomValidator (to-verify values flagged for the owner), P10-01 IDS 1.0 subset checker over exported IFC; reviewed (blockers in all three fixed with tests), green. P2-03 unblocked. [Session](SESSION-2026-11-03-relay138-snapshot-caba-ids.md).
- Relay137 (Claude, cloud scheduled): P4-01 Revit view commands (plan, section, 3D, view type/template queries), P6-01 BCL-only DXF reader with provenance and bounded wall-pair detection, P9-05 MCP tools/list from the full catalogue with policy-derived levels; reviewed (one DXF blocker fixed), green. P4-02 unblocked. [Session](SESSION-2026-11-03-relay137-views-dxf-mcp-v2.md).
- Relay136 (Claude, cloud scheduled): P1-03 Claude Desktop MCP installer, P8-02 rhino3dm .3dm adapter, P9-01 Revit catalogue v2 (roofs, stairs, curtain walls, floor openings); reviewed, should-fix closed with tests, green. New P9-05 (expose v2 through MCP). [Session](SESSION-2026-11-03-relay136-mcp-rhino-catalogue-v2.md).
- Relay135 (Claude, cloud scheduled): P3-01 multi-room layout intent compiler, P5-01 skill manifest/loader/resolver, P7-01 seeded space planner on the project brief; reviewed (one planner blocker fixed twice) and green. P3-02, P5-03, P7-02 unblocked. [Session](SESSION-2026-11-02-relay135-intent-skills-planner.md).
- Relay134 (Claude, cloud scheduled): first cloud relay with GitHub push/API. P2-02 semantic graph relations, P6-03 project brief schema/validator, P10-03 permission policy + hash-chained audit log; each red→green in CI and independently reviewed. P7-01 unblocked. [Session](SESSION-2026-11-02-relay134-graph-brief-audit.md).
- Relay68 (Claude, owner chat): owner scope change to a general Revit command surface ([ADR0009](architecture/adr/0009-revit-command-surface.md)); C01–C03 landed (CI compiles the add-in, protocol + catalogue v1 + MCP tools, executor), C04 owner smoke ready. [Session](SESSION-2026-10-30-relay68-revit-command-surface.md).
- Relay14 (Claude, owner chat "segui metiendole"): durable Core store writes (D01), Autodesk-free native reconcile contract with shared CloudGuard (N22), bounded typed CLI inputs (S01); every task independently reviewed and green on Ubuntu/Windows/macOS. [Session](SESSION-2026-10-24-relay14-durability-native-reconcile-inputs.md). Only N21 remains.
- Relay7 (Claude, owner chat "continue"): CI now runs on Ubuntu, Windows and macOS (W01); IFC incremental vs full-rebuild baseline (P01: no measurable time difference, identity and change-size delta); reconcile refuses OneDrive conflict copies/placeholders (R03). [Session](SESSION-2026-10-23-relay7-platforms-rebuild-cloud.md). Only N21 remains.
- Relay5 (Claude, owner chat, GitHub linked): published relay3/relay4 work; M01 MCP, G02 typed units/geometry (ADR0008 A) and R02 cross-store reconcile (ADR0007 A) all unit-tested on Ubuntu CI and merged to main. [Session](SESSION-2026-10-23-relay5-publish-r02-g02.md). Only N21 (native, owner Load Once) remains.
- Relay3 (Claude, owner-authorized takeover of relay2's stale LOCK): [native A/B recorded and relay3 batch](SESSION-2026-10-23-relay3-native-ab-mcp.md). Native attempt3 full Test A/B verified-in-revit (one isolated run); ADR0007/0008 proposed for owner decision; MCP server coded, not compiled. [Attempt3](../artifacts/native-room-attempt3.json), [plan](plans/2026-10-23-relay3-batch.md).
- Relay1: [compiler-driven IFC and Baton](SESSION-2026-10-23-baton-ifc.md). Product230d2ba and coordinatorb1e9377 integrated after independent review; fresh50core/68evaluator/10compiler/28Baton assertions,11 real IFC tests90.026s and actual CLI backend=true/product=false. [Remote CI](../artifacts/compiler-ifc-ci-result.json) records actual publication status separately. [Plan](plans/2026-10-23-baton-compiler-ifc.md), [board](../tasks.md).
- Published8d2cc964c86b5aef03a0e01bdc09ac1a2adf3567 passed exact-head run39899813748: core, real IFC on Ubuntu, C# CodeQL and Python CodeQL all success. Later evidence-only changes do not alter product source. No native Revit or macOS execution is inferred.
- Published baseline9b98617: CI run39889520735 succeeded all4jobs. Four real Python IFC regressions,50core/58evaluator/17checkpoint assertions in prior local evidence. Native primitive scope only, full native A/B incomplete.

## 5. Decisions not to reopen

C#/.NET8 stays because it shares types with Revit and has deterministic coverage (ADR0004). Named Pipes stays (ADR0005). Optional IfcOpenShell LGPL dependency approved in an isolated environment (ADR0006). Existing BIM-IR schema changes require a concrete new approved ADR; adapter transport does not require persistence migration. Exact committed snapshot bytes include revision+1 so subsequent corrections can match their BeforeHash; Python does not recanonicalize them. Latest retries are supported, historical retries after later correction fail closed. Baton is pinned MIT upstream, adapted to native Codex tools; no Claude hooks or global changes. Hourly same-chat heartbeat is explicitly approved and creation returned ACTIVE; first future execution has not been observed.

## 6. Verification

Serialize `pwsh -NoProfile -File scripts/verify.ps1`, then actual IFC suite using `.tools/ifc/Scripts/python.exe -m unittest discover -s tests/ifc -p 'test_*.py' -v` with BIM_DOTNET pointing at the local SDK when absent from PATH, then `pwsh -File scripts/evaluate.ps1 -Backend ifc`. Current gate50core/68evaluator/10compiler/28Baton assertions and11actual IFC tests; backend acceptance true/product false, exit2, zero failures. Do not change native criteria to turn exit2 into product success. Local .NET SDK path `.tools/dotnet/dotnet.exe`. Native fixture only when compatible Revit is loaded and isolated; no native claims from IFC.

## 7. Traps already measured

OneDrive sets ReparsePoint on real files with no LinkTarget; reject actual links, not all cloud placeholders. Python EXPRESS validation imports _pytest, so pytest is a pinned runtime requirement. A fixed IFC fixture is not compiler execution. Exact C# JSON hashes cannot be reproduced by arbitrary Python canonicalization. Recorded timings cover IFC lifecycle, not validated Revit document delivery. An idle/stale timestamp is not evidence a controller is dead.

Strict protocol parsing exposed a real CLI failure after direct worker tests passed; typed metadata and null guards fixed it. A first resize did not prove revision continuity: test two corrections on one saved file. Restricted MSBuild failed twice with0errors/0warnings while the same elevated build and full gate passed; exact underlying operation unknown, no source workaround. Do not promise unattended permissions based on manual escalation.

## 8. Remaining

Relay3 update: native full A/B passed once (attempt3); open items are on the [board](../tasks.md): M01 needs its first compile/run through verify.ps1 (CI or Windows) before promotion; R02/G02 wait on the owner's ADR0007/ADR0008 decisions; N21 needs Revit launches with the owner's Load Once. Relay3 could not run .NET, PowerShell or Revit and could not publish from the cloud.

Current product slice and controller adapter are integrated and locally verified; publication status is in the current CI record. Next product task: native full hosted Room/documentation A/B. Human goldens remain unapproved; general arbitrary BIM-IR execution and full native delivery benchmark remain unverified. Worktree archival was requested through native tools after preserving ignored evidence; verify actual attachment state rather than assuming queued means completed.

IFC receipt/hash persistence does not atomically update a separate Core ProjectStore file; cross-store coordination/recovery and a complete live mirror remain gaps.

## 9. Document map

[State](agent/STATE.md), [blockers](agent/BLOCKERS.md), [attempts](agent/ATTEMPTS.md), [capabilities](features.json), [Baton plan](plans/2026-10-23-baton-compiler-ifc.md), [Baton mapping](harness/baton-codex.md), [IFC setup](ifc.md).

[Session compiler/Baton](SESSION-2026-10-23-baton-ifc.md) records hash revision, strict-envelope and lease traps plus measured scope.
