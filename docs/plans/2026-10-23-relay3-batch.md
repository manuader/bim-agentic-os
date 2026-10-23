# Relay3 batch — MCP, recovery and typed contracts

Loaded by relay3 (Claude, owner-authorized takeover) after N01/N11 closed with native attempt3. The owner gave no preference for the next batch, so the controller chose from [blockers](../agent/BLOCKERS.md) and [capabilities](../features.json), within [CYCLE_SCOPE](../agent/CYCLE_SCOPE.md) section 1. No BIM-IR schema change, no new dependency, no native Revit write in this batch.

## Tasks

- **M01 — MCP over the CLI services.** CYCLE_SCOPE 1.5 allows MCP once the CLI works and shares exactly the same services. The CLI works. Build `interfaces/mcp` as a dependency-free stdio JSON-RPC 2.0 server (MCP `initialize`, `tools/list`, `tools/call`). Every tool calls the same Core/Evaluation functions as `interfaces/cli/Program.cs` and serializes with the same `JsonSerializer` defaults, so the tool text equals the CLI stdout byte for byte. First tools: `diff`, `validate`, `room`, `plan`. `native` and `evaluate revit` stay out (process/pipe side effects; they need their own review). Oracle: `tests/mcp` console test (same style as `tests/native-room`), TDD red first, wired into `scripts/verify.ps1`, CI green on the exact head.
- **R01 — ADR0007 proposed.** IFC receipt/hash persistence does not atomically update the separate Core ProjectStore. Write the options (single journal with two-phase intent, receipt-as-source-of-truth with replay, store-side outbox), crash points, tradeoffs and a recommended test plan. Status `proposed`; the owner decides.
- **G01 — ADR0008 proposed.** CYCLE_SCOPE section 3 asks for typed `Geometry`, `Units` (typed physical dimensions) and `Capability`; today Project/Element geometry arrays are prototype contracts and BLOCKERS requires an approved ADR before any schema change. Write options, the exact impact on persisted records and tests, and a migration path. Status `proposed`.
- **R02** waits on the owner's decision on ADR0007.
- **N21** needs the owner: repeated isolated native runs and a full-rebuild baseline (Phase 3 exit asks for a measurable delta versus a full rebuild). Each Revit launch needs the owner's Load Once.

## Rules

One worktree per task, disjoint files, independent read-only review, three failing attempts per test, no AI attribution in commits, explicit staging. Heavy builds serialized. This relay has no Windows/.NET execution; the remote CI (Ubuntu: verify.ps1, real IFC, CodeQL) is the oracle for M01, and Windows-only gates are re-run by the next relay that can.
