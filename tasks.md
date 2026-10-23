# BIM Agentic OS — current relay board

Single writer: current Baton controller. Lifecycle status is separate from capability verification. This is the current execution wave, not a product-completion estimate. Plan: [compiler IFC relay](docs/plans/2026-10-23-baton-compiler-ifc.md).

### Harness and compiler IFC

| ID | Task | Status | Dependencies | Owned files | Done when | Verification |
|---|---|---|---|---|---|---|
| B01 | Native Windows Baton coordination | ✅ | — | scripts/baton.ps1; tests/baton | independent review and fresh 28 assertion oracle | unit-tested |
| B02 | Pinned skills, Codex mapping and hourly routine | ✅ | — | .agents/skills; docs/harness; run | source/license pinned; actual approved heartbeat created | described |
| I01 | Existing C# compiler drives IFC | ✅ | — | adapters/ifc; CLI; IFC worker/tests | exact packet, custom-width create/resize, identity/retry/rollback | integration-tested (ifc) |
| I02 | Review, gates, evidence and publication | 🔄 | B01, I01 | CI; verify; docs; artifacts | fresh complete engineering + actual IFC gates; reviews resolved; pushed | described |

### Next native slice

| ID | Task | Status | Dependencies | Owned files | Done when | Verification |
|---|---|---|---|---|---|---|
| N01 | Full native hosted Room and documentation A/B | ⛔ | I02 | future native isolated worktree | real Revit full A/B and affected documentation checks | blocked |

## Progress

<!-- computed by baton.ps1; do not edit by hand -->
Progress: 3/5 done (60 %)
- Harness and compiler IFC: 3/4
- Next native slice: 0/1
Next ready:
