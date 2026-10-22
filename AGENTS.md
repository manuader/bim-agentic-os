# BIM Agentic OS — repository map

Product authority: [master specification](BIM_Agentic_OS_PROMPT_DEFINITIVO.md). Continue the existing implementation; do not restart discovery.

- [Architecture and layering](ARCHITECTURE.md), [module contracts](docs/contracts/module-contracts.md), [ADRs](docs/architecture/adr/0001-kernel.md).
- [Execution plan](docs/superpowers/plans/2026-10-22-harness-native-slice.md), [progress ledger](docs/superpowers/plans/2026-10-22-harness-native-slice.progress.md), [feature status](docs/features.json).
- Core: `core/Bim.Core`; CLI: `interfaces/cli`; native runtime: `runtimes/revit-native`; regressions: `tests/Bim.Tests`.
- Reusable workflows and BIM development skills: `.agents/skills/`; [source/activation notes](docs/harness.md).

## Commands

`pwsh -File scripts/verify.ps1` is the completion gate with .NET SDK.
`pwsh -File scripts/verify.ps1 -HostOnly` is a diagnostic fallback; it does not verify net8/Revit compatibility.
`dotnet build runtimes/revit-native/Bim.Revit.csproj` requires installed Revit 2025 references.
`pwsh -File scripts/test-roslyn.ps1` and `pwsh -File scripts/benchmark-roslyn.ps1` provide host-only evidence.

## Essential invariants

Use red-green TDD for deterministic changes and independent review before integration. Work in isolated Git worktrees for independent tasks, with explicit file/interface ownership. No concurrent writes to the same Revit document; never use the open Casa Ángel production model as a fixture.

Preserve logical/native identity, explicit units, provenance and rollback boundaries. Core must not depend on Autodesk or runtime/UI code. Do not commit Autodesk DLLs, RVTs, credentials or local SDK downloads.

Completion requires fresh command logs and feature-status evidence. Distinguish host source checks, target builds, native execution and complete validated delivery. Report blockers; never simulate a passing native test.
