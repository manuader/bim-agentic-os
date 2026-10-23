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
| M01 | MCP stdio server over the same CLI services | 🟢 | N01 | interfaces/mcp; tests/mcp; scripts/verify.ps1 (wiring line only) | `dotnet run --project tests/mcp` passes: initialize, tools/list, tools/call for diff/validate/room/plan return byte-identical JSON to the CLI; no new package; wired into verify.ps1; CI green | target unit-tested |
| R01 | ADR proposed: Core ProjectStore ↔ backend receipt cross-store recovery | ✅ | — | docs/architecture/adr/0007-cross-store-recovery.md | ADR in status proposed with options, tradeoffs, recommendation and test plan; owner decision item in OWNER-TODO | described |
| G01 | ADR proposed: typed Geometry/Units/Capability contracts | ✅ | — | docs/architecture/adr/0008-typed-geometry-units.md | ADR in status proposed with migration impact on existing tests and records; owner decision item in OWNER-TODO | described |
| R02 | Implement approved cross-store recovery | ⏳ | R01 | per ADR0007 option A (accepted) | approved option implemented with TDD and crash-point tests | target unit-tested |
| G02 | Implement approved typed Geometry/Units contracts | ⏳ | G01 | per ADR0008 option A (accepted) | approved option implemented with TDD; persisted bytes unchanged where the ADR promises | target unit-tested |
| N21 | Native repeatability and full-rebuild baseline | 🔒 | N01 | tests/integration; artifacts | ≥3 isolated A/B runs and one full-rebuild run on the same fixture with recorded timings; owner Load Once per Revit launch | target verified-in-revit |

## Progress

<!-- computed by baton.ps1; do not edit by hand -->
Progress: 9/13 done (69.2 %)
- Harness and compiler IFC: 4/4
- Next native slice: 3/3
- Relay3 batch — MCP, recovery and typed contracts: 2/6
Next ready: R02, G02

