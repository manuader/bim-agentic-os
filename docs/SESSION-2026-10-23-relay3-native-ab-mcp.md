# Relay3 — native A/B recorded, ADRs proposed, MCP coded

Relay3 is a Claude session. The owner asked in chat to continue with Baton autonomously and concurrently, and authorized taking relay2's LOCK, which had been held by Codex thread 01a165e1 with a heartbeat about ten hours old. The owner also stated that the Codex hourly routine no longer runs. The takeover was recorded in the run's OWNER.md and journal; the adapter's no-automatic-takeover rule was respected, since this was an explicit owner decision, not a timestamp inference.

## What relay2 left and what was recorded

Relay2 ran native attempt3 (run e2ce96ec) on fix 287014c in an isolated Revit 2025 child process after the owner chose Load Once. It passed full Test A 35/35 and Test B 17/17, all eight backend checks, product_acceptance true, and then stopped before recording or publishing. Relay3 recorded the report unchanged in [the report artifact](../artifacts/native-room-attempt3-report.json), with a summary in [attempt3](../artifacts/native-room-attempt3.json). The summary includes the add-in hashes and an honest binding note: the add-in was written 28 s after 287014c, and no rebuild-hash comparison was done. Test A and Test B are now verified-in-revit for that single run. Repeatability, the full-rebuild comparison and the unintended_element_changes metric remain open (N21). The three native attempts used the three-attempt budget, recorded in [ATTEMPTS](agent/ATTEMPTS.md).

## Batch loaded and worked

The owner gave no preference, so the controller chose from BLOCKERS and features ([plan](plans/2026-10-23-relay3-batch.md)). Three workers ran concurrently in separate worktrees, each owning disjoint files:

- R01, [ADR0007 cross-store recovery](architecture/adr/0007-cross-store-recovery.md), proposed. It recommends the backend receipt as the source of truth, plus a fail-closed reconcile step that changes no schema. It found that no code sequences the two commits, that the Core journal is write-only, that some writes are not fsynced, and that a hard crash leaves a staging file that blocks retries.
- G01, [ADR0008 typed geometry and units](architecture/adr/0008-typed-geometry-units.md), proposed. It recommends typed views at the API boundaries with persisted bytes unchanged. It found three unit systems with no type boundary and that the existing Length type is unused. It also found that the Python adapter rejects any new Element key.
- M01, the dependency-free stdio MCP server in `interfaces/mcp`, with tools diff, validate, room and plan. Its tests in `tests/mcp` compare against the real CLI binary byte for byte, and it is wired into verify.ps1. An independent read-only review found no compile errors and one oracle gap, which was fixed. The server has **not been compiled**: this relay had no .NET SDK, no PowerShell and no NuGet egress. Board status is 🟢 and the feature state is described until verify.ps1 passes.

## Limits of this relay

No Windows build, IFC suite or Revit run was executed, and no remote CI result exists for these commits. Publication from the cloud was not authorized, and the device shell was still provisioning. The commits must be pushed from the owner's machine or through a linked GitHub connection, and CI observed, before M01 is promoted. The isolated fixture Revit (PID 41316) still had the add-in DLLs locked when relay3 read them; production PID 2544 was never touched.
