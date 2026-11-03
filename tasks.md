# BIM Agentic OS — current relay board

Single writer: current Baton controller. Lifecycle status is separate from capability verification. This is the current execution wave, not a product-completion estimate. Plan: [compiler IFC relay](docs/plans/2026-10-23-baton-compiler-ifc.md).

### Harness and compiler IFC

| ID | Task | Status | Dependencies | Owned files | Done when | Verification |
|---|---|---|---|---|---|---|
| B01 | Native Windows Baton coordination | ✅ | — | scripts/baton.ps1; tests/baton | independent review and fresh 28 assertion oracle | unit-tested |
| B02 | Pinned skills, Codex mapping and hourly routine | ✅ | — | .agents/skills; docs/harness; run | source/license pinned; actual approved heartbeat created | described |
| I01 | Existing C# compiler drives IFC | ✅ | — | adapters/ifc; CLI; IFC worker/tests | exact packet, custom-width create/resize, identity/retry/rollback | integration-tested (ifc) |
| I02 | Review, gates, evidence and publication | ✅ | B01, I01 | CI; verify; docs; artifacts | fresh complete engineering + actual IFC gates; reviews resolved; pushed | integration-tested (ifc) |

### Next native slice

| ID | Task | Status | Dependencies | Owned files | Done when | Verification |
|---|---|---|---|---|---|---|
| N01 | Full native hosted Room and documentation A/B | ✅ | I02 | controller integration and real execution | real Revit full A/B and affected documentation checks | verified-in-revit (attempt3, one isolated run) |
| N11 | Native Room, hosted elements and document executor | ✅ | I02 | runtimes/revit-native; NativeRoomGuard.Tests | SDK build, real isolated A/B readback and durable identity | verified-in-revit (287014c, attempt3) |
| N12 | Typed native oracle and integration command | ✅ | I02 | evaluator; CLI; tests/native-room; native-room-ab.ps1 | deterministic guards and real-port-only provenance | unit-tested |

### Relay3 batch — MCP, recovery and typed contracts

Loaded by relay3 (Claude) after N01; owner answered "[No preference]", so the controller chose items from BLOCKERS and features.json. Plan: [relay3 batch](docs/plans/2026-10-23-relay3-batch.md).

| ID | Task | Status | Dependencies | Owned files | Done when | Verification |
|---|---|---|---|---|---|---|
| M01 | MCP stdio server over the same CLI services | ✅ | N01 | interfaces/mcp; tests/mcp; scripts/verify.ps1 (wiring line only) | `dotnet run --project tests/mcp` passes: initialize, tools/list, tools/call for diff/validate/room/plan return byte-identical JSON to the CLI; no new package; wired into verify.ps1; CI green | target unit-tested |
| R01 | ADR proposed: Core ProjectStore ↔ backend receipt cross-store recovery | ✅ | — | docs/architecture/adr/0007-cross-store-recovery.md | ADR in status proposed with options, tradeoffs, recommendation and test plan; owner decision item in OWNER-TODO | described |
| G01 | ADR proposed: typed Geometry/Units/Capability contracts | ✅ | — | docs/architecture/adr/0008-typed-geometry-units.md | ADR in status proposed with migration impact on existing tests and records; owner decision item in OWNER-TODO | described |
| R02 | Implement approved cross-store recovery | ✅ | R01 | per ADR0007 option A (accepted) | approved option implemented with TDD and crash-point tests | target unit-tested |
| G02 | Implement approved typed Geometry/Units contracts | ✅ | G01 | per ADR0008 option A (accepted) | approved option implemented with TDD; persisted bytes unchanged where the ADR promises | target unit-tested |
| N21 | Native repeatability and full-rebuild baseline | 🔒 | N01 | tests/integration; artifacts | ≥3 isolated A/B runs and one full-rebuild run on the same fixture with recorded timings; owner Load Once per Revit launch | target verified-in-revit |

### Relay7 batch — platform coverage, rebuild baseline, recovery guard

Loaded by relay7 (Claude) after the owner said "continue" with only N21 left; controller's choice from BLOCKERS.

| ID | Task | Status | Dependencies | Owned files | Done when | Verification |
|---|---|---|---|---|---|---|
| W01 | CI on windows-latest and macos-latest | ✅ | — | .github/workflows/ci.yml | windows job runs verify.ps1 and real IFC; macos job runs real IFC; all green on the exact head; evidence artifact recorded | target integration-tested (ifc) on 3 OS |
| P01 | IFC incremental vs full-rebuild baseline | ✅ | — | tests/harness/rebuild; scripts/benchmark-rebuild.ps1; artifacts | same correction applied incrementally and by full regeneration on the real IFC backend, N≥10 samples each, p50/p95 and elements written recorded with hardware/version; no speedup claim beyond the measured scope | target integration-tested (ifc) |
| R03 | Reconcile refuses OneDrive conflict copies/placeholders | ✅ | R02 | adapters/ifc/Bim.Ifc.Compiler/IfcReconcile.cs; runtimes/ifc/worker.py; tests/reconcile; tests/ifc/test_reconcile.py | sibling conflict-copy patterns and offline placeholders around project.json/room.ifc fail closed with a typed code and zero writes, tests red first | target unit-tested |

### Relay14 batch — durability, native reconcile contract, bounded inputs

Loaded by relay14 (Claude) after the owner said "segui metiendole"; controller's choice from evidence.md known gaps and ADR0007 notes. No schema or persisted-format change.

| ID | Task | Status | Dependencies | Owned files | Done when | Verification |
|---|---|---|---|---|---|---|
| D01 | Core write durability without format change | ✅ | R02 | core/Bim.Core/Model.cs (ProjectStore writes only); tests/reconcile or new tests/durability | checkpoint and journal writes are flushed to disk and checkpoint is atomically replaced; torn/partial files tested; byte goldens unchanged | target unit-tested |
| N22 | Native reconcile contract (ADR0007 A, Revit path) | ✅ | R02 | runtimes/revit-native/NativeReconcile*.cs (Autodesk-free); tests/integration/NativeReconcileGuard.Tests | Revit-independent decision logic comparing the saved ExtensibleStorage project snapshot with Core, fail-closed codes, source-linked guard tests in CI; native execution stays owner-gated | target unit-tested (guards); native verification owner-gated |
| S01 | Bounded CLI inputs | ✅ | — | interfaces/cli/Program.cs; tests/evaluation/test-evaluate-cli.ps1 or new tests/cli | every CLI file input has a size cap and typed failure code; oversized and non-regular inputs rejected; MCP equality tests still green | target unit-tested |

### Revit command surface — catalogue v1 (architecture)

Owner scope change 2026-10-30, [ADR0009](docs/architecture/adr/0009-revit-command-surface.md), [plan](docs/plans/2026-10-30-revit-command-surface.md). N21 partial: owner session 20261030T192810Z passed runs 1–2 (52/52 each); run 3 pending.

| ID | Task | Status | Dependencies | Owned files | Done when | Verification |
|---|---|---|---|---|---|---|
| C01 | CI compiles the Revit add-in | ✅ | — | runtimes/revit-native/Bim.Revit.csproj; .github/workflows/ci.yml | windows CI job builds Bim.Revit with --warnaserror using build-only reference assemblies; local installed-Revit build unchanged | target compiled |
| C02 | Command protocol, catalogue v1 contracts and MCP tools | ✅ | — | core/Bim.Core/RevitCommands*.cs; interfaces/mcp; tests/revit-commands | envelope, validators, copies-only guard and generated MCP tools unit-tested in CI | target unit-tested |
| C03 | Revit executor for catalogue v1 | ✅ | C01, C02 | runtimes/revit-native/Commands*.cs; App.cs dispatch | queries, wall line/arc, door/window, floor, room compile in CI; Autodesk-free parts tested | target compiled |
| C04 | Owner smoke of catalogue v1 in Revit | 🔒 | C03 | scripts/owner-native-session.ps1 -Catalogue (ready, c82f2b6); artifacts | curved wall, door, floor, room created on a fixture with readback and no-duplicate retry | target verified-in-revit |
| C05 | Guarded free path (ADR + implementation) | ⛔ | C03 | docs/architecture/adr; runtimes/revit-native | ADR accepted; free C# runs in a rolled-back transaction group with change report | target unit-tested |

## All-phases roadmap

Owner directive 2026-11-02: advance every master-spec phase in parallel. [Roadmap](docs/plans/2026-11-02-all-phases-roadmap.md). C04 first run (20261102T165801Z): query.levels passed in Revit, later steps hit a dead pipe listener (P1-01).

### Phase 1 — native kernel

| ID | Task | Status | Dependencies | Owned files | Done when | Verification |
|---|---|---|---|---|---|---|
| P1-01 | Pipe listener survives per-connection faults | 🟢 | — | runtimes/revit-native/App.cs | CI green; C04 re-run reaches 17 steps | target compiled |
| P1-02 | Generic element query and inspect commands | ⏳ | — | core/Bim.Core/RevitCommands*; runtimes/revit-native/Commands* | query.elements by category/level with parameters; contract tests; compiles | target unit-tested |
| P1-03 | MCP server setup for the Claude app | ✅ | — | docs/mcp-setup.md; scripts/install-mcp.ps1 | one-command install registers the MCP server in Claude Desktop config with a backup; tested on a temp config | unit-tested (relay136, CI 41550944034; not run on the owner's Claude Desktop) |
| P1-04 | Catalogue smoke re-run in Revit | 🔒 | P1-01 | owner session (fix ed5c759 on main; owner re-runs -Catalogue) | owner runs -Catalogue; 17/17 | target verified-in-revit |

### Phase 2 — semantic state and mirror

| ID | Task | Status | Dependencies | Owned files | Done when | Verification |
|---|---|---|---|---|---|---|
| P2-01 | Revit to BIM-IR snapshot export | ⏳ | — | core/Bim.Core/Mirror*; runtimes/revit-native/Mirror* | query.snapshot returns walls/doors/windows/floors/rooms as BIM-IR elements with stable ids; contract tests | target unit-tested |
| P2-02 | Semantic graph relations | ✅ | — | core/Bim.Core/Graph* | hosted-by, bounds, on-level edges with queries and closure; tests | unit-tested (relay134, CI 41511322582) |
| P2-03 | External edit journal (DocumentChanged) | ⛔ | P2-01 | runtimes/revit-native/Journal* | events recorded with element ids and diff against last snapshot; compiles | target compiled |

### Phase 3 — incremental compiler

| ID | Task | Status | Dependencies | Owned files | Done when | Verification |
|---|---|---|---|---|---|---|
| P3-01 | Intent AST beyond one room | ✅ | — | core/Bim.Core/Intent* | multi-room layout intents with wall types and openings compile to BIM-IR deterministically; tests | unit-tested (relay135, CI 41530804027) |
| P3-02 | Compiler to catalogue bridge | ⏳ | P3-01 | core/Bim.Core/PlanToCommands* | an ExecutionPlan becomes ordered catalogue v1 commands with operation ids; tests | target unit-tested |

### Phase 4 — documentation engine

| ID | Task | Status | Dependencies | Owned files | Done when | Verification |
|---|---|---|---|---|---|---|
| P4-01 | View commands (plan, section, 3D) | ⏳ | — | core/Bim.Core/RevitCommands*; runtimes/revit-native/Commands* | view.create plan/section/3d contracts and executor; compiles | target compiled |
| P4-02 | Dimensions, tags, schedules and sheets commands | ⛔ | P4-01 | same | contracts and executor for dimension/tag/schedule/sheet; compiles | target compiled |

### Phase 5 — skills and design systems

| ID | Task | Status | Dependencies | Owned files | Done when | Verification |
|---|---|---|---|---|---|---|
| P5-01 | Skill manifest and loader | ✅ | — | core/Bim.Skills (new); skills/ | versioned skill manifest (rules, defaults, parameters), loader with validation and conflict resolution by priority; tests | unit-tested (relay135, CI 41533653759) |
| P5-02 | CABA building code rule pack | ⏳ | — | skills/caba-codigo-edificacion; core/Bim.Skills | minimum areas, heights, sides, ventilation/illumination ratios with cited articles; validator over BIM-IR rooms; tests | target unit-tested |
| P5-03 | Two contrasting style skills | ⏳ | P5-01 | skills/style-* | two style packs change defaults visibly with traceable rule ids; tests | target unit-tested |

### Phase 6 — CAD and requirements

| ID | Task | Status | Dependencies | Owned files | Done when | Verification |
|---|---|---|---|---|---|---|
| P6-01 | DXF reader with provenance | ⏳ | — | adapters/cad (new); tests/cad | DXF layers/lines/polylines/arcs/blocks/text read to a provenance-tagged drawing model; wall-pair detection; fixtures; tests | target unit-tested |
| P6-02 | PDF vector reader and scale calibration | ⏳ | — | adapters/cad; tests/cad | vector paths and text from PDF, scale from a known dimension, alignment; fixtures; tests | target unit-tested |
| P6-03 | Project brief schema and validated extraction | ✅ | — | core/Bim.Core/Brief* | brief JSON schema (rooms, areas, adjacencies, constraints) with validator and uncertainty fields; tests | unit-tested (relay134, CI 41514529827) |

### Phase 7 — generative kernels

| ID | Task | Status | Dependencies | Owned files | Done when | Verification |
|---|---|---|---|---|---|---|
| P7-01 | Constraint space planner | ✅ | P6-03 | core/Bim.Generative (new) | rectangular space planning honoring areas, adjacency and CABA minimums; deterministic seeds; tests | unit-tested (relay135, CI 41535913478) |
| P7-02 | Alternatives with scoring | ⏳ | P7-01 | core/Bim.Generative | N alternatives scored and ranked with explanations; tests | target unit-tested |

### Phase 8 — Rhino, Blender and IFC

| ID | Task | Status | Dependencies | Owned files | Done when | Verification |
|---|---|---|---|---|---|---|
| P8-01 | IFC import to BIM-IR and roundtrip | ⏳ | — | runtimes/ifc; adapters/ifc | IFC walls/openings/spaces read to BIM-IR; export→import→export stable; CI on 3 OS | target integration-tested (ifc) |
| P8-02 | Rhino .3dm adapter without Rhino | ✅ | — | adapters/rhino (new) | rhino3dm (MIT) write/read of BIM-IR geometry; tests | unit-tested against real rhino3dm 8.17.0 on Linux (relay136, CI 41548519960; never opened in Rhino) |
| P8-03 | Blender adapter | 🔒 | — | adapters/blender | bpy exporter script; execution needs Blender installed | target described |

### Phase 9 — Revit parity

| ID | Task | Status | Dependencies | Owned files | Done when | Verification |
|---|---|---|---|---|---|---|
| P9-01 | Catalogue v2: roofs, stairs, curtain walls, slab openings | ✅ | — | core/Bim.Core/RevitCommands*; runtimes/revit-native/Commands* | contracts, validators, executor; compiles | compiled executor + unit-tested contracts (relay136, CI 41547173763; not verified-in-revit) |
| P9-02 | Structure: grids, columns, beams, foundations | ⏳ | — | same | contracts, validators, executor; compiles | target compiled |
| P9-03 | Guarded free path (ADR + implementation) | ⏳ | — | docs/architecture/adr; runtimes/revit-native | ADR accepted by owner rules; compiled C# runs in rolled-back group with change report; compiles | target compiled |
| P9-04 | Revit parity census registry | ⏳ | — | capabilities/ | machine-readable census of Revit functions with coverage state and tests linking | target described |
| P9-05 | Expose catalogue v2 as MCP tools | ⏳ | P9-01 | interfaces/mcp/McpServer.cs; tests/mcp; tests/audit; docs/contracts/module-contracts.md | MCP tools/list built from RevitCommandCatalogue.All; v2 permission levels asserted; contract doc lists v2 | target unit-tested |

### Phase 10 — global production

| ID | Task | Status | Dependencies | Owned files | Done when | Verification |
|---|---|---|---|---|---|---|
| P10-01 | IDS checks | ⏳ | — | runtimes/ifc; tests/ifc | IDS file validation of exported IFC with report; CI | target integration-tested (ifc) |
| P10-02 | BCF export | ⏳ | — | core/Bim.Core/Bcf* | validation issues exported as BCF 3.0 zip; schema-valid; tests | target unit-tested |
| P10-03 | Audit log and permissions | ✅ | — | core/Bim.Core/Audit* | append-only audit of commands with actor and decision; permission policy (read/write/real-project); tests | unit-tested (relay134, CI 41517584141) |

## Progress

<!-- computed by baton.ps1; do not edit by hand -->
Progress: 30/54 done (55.6 %)
- Harness and compiler IFC: 4/4
- Next native slice: 3/3
- Relay3 batch — MCP, recovery and typed contracts: 5/6
- Relay7 batch — platform coverage, rebuild baseline, recovery guard: 3/3
- Relay14 batch — durability, native reconcile contract, bounded inputs: 3/3
- Revit command surface — catalogue v1 (architecture): 3/5
- Phase 1 — native kernel: 1/4
- Phase 2 — semantic state and mirror: 1/3
- Phase 3 — incremental compiler: 1/2
- Phase 4 — documentation engine: 0/2
- Phase 5 — skills and design systems: 1/3
- Phase 6 — CAD and requirements: 1/3
- Phase 7 — generative kernels: 1/2
- Phase 8 — Rhino, Blender and IFC: 1/3
- Phase 9 — Revit parity: 1/5
- Phase 10 — global production: 1/3
Next ready: P1-02, P2-01, P3-02, P4-01, P5-02, P5-03, P6-01, P6-02

