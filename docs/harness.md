# Development harness and provenance

The master product specification supplies the product requirements; [CYCLE_SCOPE](agent/CYCLE_SCOPE.md) narrows the current work and takes precedence for this cycle. [OpenAI's Harness engineering article](https://openai.com/index/harness-engineering/) motivates a short repository map, versioned execution plans, machine-checkable boundaries and agent-readable evidence. These practices are adapted to BIM safety; native integration cannot be replaced with a mock passing result.

## Superpowers

Source: [obra/superpowers](https://github.com/obra/superpowers), pinned revision `2ce8aa15d05bd57213e6f746f953aafb7ded7a8b`, MIT. Selected workflow directories were installed using Codex's skill-installer helper into `.agents/skills`. The license is preserved in `.agents/skills/SUPERPOWERS-LICENSE`. No hooks, telemetry, global configuration changes or arbitrary upstream scripts were executed.

Installed workflows: using-superpowers, brainstorming, writing-plans, test-driven-development, subagent-driven-development, using-git-worktrees, systematic-debugging, requesting-code-review, verification-before-completion, executing-plans, finishing-a-development-branch. Their supporting references/scripts inside each skill directory are included. Native Codex marketplace discovery also found Superpowers available but not installed as a global plugin. Repository-local skills are the chosen installation; automatic discovery refreshes on the next turn.

Seven repository-owned skills cover Revit API, C#/.NET, BIM-IR, incremental compiler, Blender, IfcOpenShell and BIM QA. Their source is `scripts/install-domain-skills.py`. Skills for future adapters describe development/verification boundaries, not implemented adapter support.

The official Codex skill-creator `quick_validate.py` accepted all seven domain skills after installing its missing PyYAML 6.0.3 dependency into ignored `.tools/skill-validator-deps`. This verifies metadata/scaffold validity, not behavioral effectiveness. Reproduce with the bundled Python, `PYTHONPATH=.tools/skill-validator-deps`, and that validator on each `bim-*` directory.

The user approved the C# evaluation design/core, retaining Named Pipes and isolated IfcOpenShell under LGPL-3.0-or-later. Future BIM-IR schema changes remain separate decisions. Multi-agent tools already exist; no machine configuration change is needed.

Current plans live in `docs/plans/`; older execution records remain in `docs/superpowers/plans/`. Persistent session memory, blockers and the per-test three-attempt ledger live in `docs/agent/`. Upstream Bash utility invocations may be adapted to native PowerShell in this Windows harness; preserve review artifacts and gates, rather than claiming an unexecuted upstream command ran.

## Gates

`scripts/verify.ps1` runs subprocess evidence capture, fail-closed architecture/docs/skill/feature/security checks, PowerShell parsing, target builds with .NET analyzers and warnings-as-errors, deterministic regressions and core benchmarks. `-HostOnly` explicitly limits claims. SDK8.0.415 is installed locally and pinned with patch roll-forward in global.json. `scripts/audit-dependencies.ps1` is a separate network-dependent CI gate covering every maintained csproj. CodeQL is configured in GitHub Actions and has passed remotely; exact commit/run evidence is versioned in artifacts.

Native source check, target build and guarded native smoke are separate operations. Primitive create/retry/resize/readback, forced rollback and final primitive save/reopen now passed in real Revit25.2; Tests A/B remain blocked pending native complete-room/documentation support. No green placeholder native CI job exists.

GitHub publication is now available. First remote run39874914458 passed both core and CodeQL on commit635cc68; see artifacts/ci-first-run.json. Later changes require their own run evidence.

Security coverage scope: public CodeQL currently builds and analyzes the vendor-independent core/CLI graph. The native runtime is target-built locally against installed Autodesk references and exercised by guarded integration probes; this is not a claim that public CodeQL analyzed the Autodesk-dependent runtime.
