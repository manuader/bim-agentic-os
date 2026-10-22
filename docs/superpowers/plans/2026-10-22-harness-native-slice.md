# Harness and native room revision implementation plan

> **For agentic workers:** Use superpowers:subagent-driven-development and test-driven-development. Execute without renewed discovery; master specification remains authoritative.

**Goal:** Establish reproducible engineering gates and advance typed room intention to an executable native batch with explicit validation boundaries.
**Architecture:** Keep C# core vendor independent. Revit uses ExternalEvent exclusively; deterministic geometry and plan generation remain in core. Independent work happens in managed worktrees.
**Tech Stack:** C# net8, Revit 2025 API 25.2, PowerShell, GitHub Actions.
**Spec:** `BIM_Agentic_OS_PROMPT_DEFINITIVO.md` sections 1, 8, 20–24 and user's harness request.

## Global constraints

- Preserve native identity, explicit metre units, no fabricated passing native tests.
- No parallel Revit document writes; no production Casa Ángel fixture edits.
- .NET SDK absent initially; host Roslyn checks are distinct from net8 builds.
- No broad architectural rewrite; no API keys or external AI services needed.

## Review focus

Malformed intent/units; stale or tampered plans; retry duplication; rollback/native readback; false-positive CI completion or coverage claims.

## Tasks

### 1. Harness gates (independent worktree)
Files: `scripts/verify.ps1`, `scripts/harness-check.ps1`, `scripts/test*.ps1`, `.github/workflows/ci.yml`, `.editorconfig`, `Directory.Build.props`, `tests/harness/`.
Consumes existing csproj files and future `core/Bim.Core/*.cs`; emits reproducible verifier with explicit target/host modes and per-command evidence. Compile core+CLI, deterministic tests, architecture/doc/skill/status checks, security static checks/dependency audit; native target only on licensed self-hosted runner. Benchmarks automatic, publish logs; no misleading native passes.
- [ ] Write failing harness checks, record red.
- [ ] Implement gates and verify local available subset; distinguish configured CI from executed CI.
- [ ] Commit code and report commands, evidence and gaps.

### 2. Typed room intent/native revision slice (independent worktree)
Files: new `core/Bim.Core/RoomSlice.cs`, tests, CLI, Revit native runtime and contracts as needed; no harness/docs files shared with Task 1.
Consumes existing Project/Element/Compiler; produces deterministic room create/resize intent with stable IDs, dependencies, minimal operations and validation. Wire native supported batch operations in a serialized transaction group; return explicit blocked/unsupported for missing native documentation instead of false success. Use native logical mapping and readback where safely implementable; native tests remain unverified until loaded.
- [ ] Write behavioral failing tests for 5x4 room and +0.4m correction, fixed anchor, stable unaffected IDs, invalid intent and dependency invalidation.
- [ ] Implement minimum deterministic code, test green, connect to CLI/native batch.
- [ ] Exercise rollback/readback paths with appropriate tests; explicitly identify native integration gaps.
- [ ] Commit code and report evidence.

### 3. Repository knowledge and reusable skills (controller)
Files: `AGENTS.md`, `ARCHITECTURE.md`, `docs/contracts/`, ADR, persistent plan/ledger, `.agents/skills/`, `docs/features.json`.
- [ ] Install pinned Superpowers workflows; record source/license/activation boundary.
- [ ] Add seven specialized development skills and validate metadata.
- [ ] Add brief repository map, contract/ownership rules, feature status evidence and review ledger.

### 4. Review and verification
- [ ] Independently review task implementations for specification and quality; fix material findings.
- [ ] Integrate commits, execute available full verifier and benchmark; retain target/native blockers.
- [ ] Commit verified evidence; attempt GitHub only if access changes (prior 403 is known).
