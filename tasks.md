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
| N01 | Full native hosted Room and documentation A/B | 🔄 | I02 | controller integration and real execution | real Revit full A/B and affected documentation checks | described |
| N11 | Native Room, hosted elements and document executor | 🔄 | I02 | runtimes/revit-native; NativeRoomGuard.Tests | SDK build, real isolated A/B readback and durable identity | described |
| N12 | Typed native oracle and integration command | ✅ | I02 | evaluator; CLI; tests/native-room; native-room-ab.ps1 | deterministic guards and real-port-only provenance | unit-tested |

## Progress

<!-- computed by baton.ps1; do not edit by hand -->
Progress: 5/7 done (71.4 %)
- Harness and compiler IFC: 4/4
- Next native slice: 1/3
Next ready:
