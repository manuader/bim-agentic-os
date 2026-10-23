# Relay5 — publication, typed units and cross-store reconcile

The owner asked in chat to keep working, and GitHub became available to this Claude session. Relay5 took the free LOCK, published the relay3/relay4 branch (14 commits) and used the GitHub CI on Ubuntu as its oracle. This environment has no .NET SDK, PowerShell or NuGet egress.

## Landed on main

- **M01 MCP stdio server.** On `relay3`, the first CI build and run of `interfaces/mcp`/`tests/mcp` passed under `--warnaserror` (run 40015266756, 4/4). The contract test compares tool output byte for byte against the real CLI binary. Recorded in [CI result](../artifacts/relay3-ci-result.json).
- **G02, ADR0008 option A** (owner-approved). Typed units, per-kind geometry views through one codec, a capability registry and a typed semantic diff, all at API boundaries only. Before changing any code, the worker froze goldens for persisted JSON, hashes, ProjectStore files and the IFC packet, and confirmed them on the unchanged baseline in CI. An independent reviewer recomputed them in Python and they matched. The review found no Revit name ambiguities, but the add-in is not built in CI. Seven review gaps were fixed with tests. CI 40017923434, then main 40018329143.
- **R02, ADR0007 option A** (owner-approved). A fail-closed IFC↔Core `reconcile` (CLI verb, not exposed in MCP) that replays only the latest one-revision gap. Crash-injection hooks are test-only: an internal init property, and a keyword-only argument in the worker. Every failure is checked with SHA-256 snapshots of every file in both stores. It took three CI attempts, each with a distinct hypothesis: a missing using, a literal U+2028, then green. Review hardening covered packet staging through links, typed I/O and packet errors, and full-folder hashes. CI 40018588474, then the merged head 40019034003.

## Limits

These results are from Ubuntu CI only. The Windows gate and the Revit add-in compile (`scripts/check-native-source.ps1`) have not been re-run since these changes. They need the owner's machine or a Windows relay. Windows/OneDrive rename and fsync semantics, OneDrive conflict-copy refusal and the native reconcile path stay open. Step logs were not readable from this environment, because the log host is blocked; job conclusions are the evidence. N21 (native repeatability plus full-rebuild baseline) needs the owner's Load Once and is the only open board item.
