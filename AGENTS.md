# BIM Agentic OS — repository map

Current cycle authority: [CYCLE_SCOPE](docs/agent/CYCLE_SCOPE.md). Long-term vision: [MASTER_PROMPT](docs/MASTER_PROMPT.md). Do not restart discovery or broaden this cycle to CAD/vision/generative/Blender/Rhino work.

At session start read [STATE](docs/agent/STATE.md), [BLOCKERS](docs/agent/BLOCKERS.md), [ATTEMPTS](docs/agent/ATTEMPTS.md) and [ADRs](docs/architecture/adr/0001-kernel.md). [Environment](docs/agent/ENVIRONMENT.md), [evaluation design/plan](docs/plans/2026-10-23-evaluation-machine.md), [capability evidence](docs/features.json), [module contracts](docs/contracts/module-contracts.md).

Core: `core/Bim.Core`; JSON CLI: `interfaces/cli`; Revit: `runtimes/revit-native`; [IFC setup](docs/ifc.md): `runtimes/ifc`, `tests/ifc`; deterministic regressions: `tests/Bim.Tests`; native probes: `tests/integration`; human oracle templates: `tests/golden`; workflows: `.agents/skills`.

## Commands and evidence

`pwsh -File scripts/verify.ps1` is the code/unit/static completion gate with.NET8 SDK. It is not full Test A/B acceptance.
`dotnet build runtimes/revit-native/Bim.Revit.csproj --configuration Release --warnaserror` needs installed Revit2025 references.
`pwsh -File scripts/verify.ps1 -HostOnly` is diagnostic only; it does not verify the net8 target or native backend.

Use Superpowers TDD, isolated worktrees and independent review. Plans live in `docs/plans/`. Maximum three failing attempts per test with distinct hypotheses recorded in ATTEMPTS; never relax the oracle. Stop affected work after the budget, restore its last green implementation without disturbing unrelated changes, record the blocker and continue independently.

Before changing existing BIM-IR schema, core language or RPC choice, obtain user approval in a proposed ADR. Non-permissive dependencies/credentials also require approval. Native document writes stay serialized; never use the production model as a fixture. Do not commit vendor binaries, RVTs, credentials or local SDKs. Conventional commits; no AI attribution trailers.

Capabilities/reports use only described, compiled, unit-tested, integration-tested (ifc), verified-in-revit, blocked, unsupported, manual-only. Record scope and evidence; existing primitive native success is not full Test A/B. End each session with updated memory and the report headings required in CYCLE_SCOPE.
