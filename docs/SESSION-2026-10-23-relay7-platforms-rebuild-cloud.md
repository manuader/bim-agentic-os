# Relay7 — three-OS CI, IFC rebuild baseline, cloud-hazard guard

The owner said "continue" while the board held only N21, which needs the owner. Relay7 took the free LOCK and loaded a new batch from BLOCKERS. GitHub CI was the oracle throughout: this environment has no .NET SDK.

## Landed on main

- **W01 — CI on three systems.** `core-windows` runs `verify.ps1` on windows-latest. `ifc-portable` runs the real IfcOpenShell tests and the C# evaluator on windows-latest and macos-latest. The first run passed 7/7 (40031105768). This is the first macOS execution, and the first Windows run of the MCP, typed-geometry and reconcile suites, on hosted runners rather than the owner's machine. [Evidence](../artifacts/multi-os-ci-result.json).
- **P01 — incremental vs full rebuild on the real IFC backend** ([baseline](../artifacts/ifc-rebuild-benchmark.json)).
  - The same +400 mm correction was run N=10 times both ways, with geometric equivalence checked on every sample.
  - End to end there is no measurable difference: the bootstrap 95% CI of the p50 difference includes 0. Inline QA, which is two EXPRESS validations plus inspection, takes about 99.9% of both paths.
  - The measured difference is identity and change size: incremental keeps 62/62 GlobalIds and changes 10/244 entities, while the rebuild keeps 0/62 and changes 72/244.
  - Review removed two ratios that compared unlike scopes, measured the QA inline instead of estimating it, and fixed the recorded command. Absolute times differ between runner CPUs.
- **R03 — reconcile refuses OneDrive conflict copies and cloud-only placeholders.** The check covers both the Core and IFC folders, with typed codes and zero writes. The CLI now uses the folder-aware overload; the controller did the red/green on that.
  - Review found a case-only false positive (it could permanently refuse on Windows/macOS) and a check-then-write window. Both are fixed: case-only variants count only when the exact primary also exists, and the hazard check is repeated right before the replay commit.
  - The guard was tested with injected attributes; real OneDrive behaviour is unverified.

## Limits

No Revit and no owner-machine run. The Revit add-in compile is still unchecked after the core changes (`scripts/check-native-source.ps1` needs Revit). Every phase had an independent read-only review, and each review finding was closed with a test before merge.
