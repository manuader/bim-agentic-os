# Owner-only actions
- [x] 2026-10-23 — Explicit hourly same-chat heartbeat approval received; native creation ACTIVE id bim-agentic-os-baton.
No current owner action required for the completed bounded IFC slice. Future existing BIM-IR schema changes require a concrete proposed ADR and explicit approval, as stated in standing rules; do not ask for an unspecified migration now.

- [x] 2026-10-23 owner confirmed Load Once in chat. Native relay2: Revit child PID49416 is at actual TaskDialog_Security_Unsigned_File_Loading (journal.0253.txt). Choose Load Once / Cargar una vez for BIM Agentic OS Fixture in that new instance, only if accepting this compiled add-in. Never interact with production PID2544 for the fixture. Await fixture-result.json in cf04a54c-61a3-4fd2-a572-fc0a66eb0bad; no A/B dispatch yet.
- [x] Owner confirmed Load Once in chat. Second isolated fixture PID37936: actual unsigned add-in dialog observed journal0254. Choose Load Once for BIM Agentic OS Fixture to load the corrected DLL. New run eb4d5b32-6924-433d-ae4b-2e6e2ad4e169; first child49416 closed normally; production2544 untouched.
- [x] Actual journal0255 API_SUCCESS confirms add-in started after human UI choice (no chat answer inferred). Third isolated fixture PID41316: actual unsigned add-in dialog journal0255. Choose Load Once for BIM Agentic OS Fixture to load width-readback fix. Third/final native attempt budget; no blanket Always Load requested. Run e2ce96ec-17a1-4763-9359-0eb60dce5123.

## Relay3 (2026-10-23)
- [ ] Publish relay3 work (relay3 could not push). In the repo folder on Windows:
  `git fetch .bim/baton/2026-10-23-core-ifc/relay3.bundle relay3:relay3` then `git push origin relay3`. Do not merge to main until CI on that branch is green (M01 was never compiled). Owner chose to link GitHub (Settings → Connectors); the next Claude relay pushes it once linked. The bundle now includes 5ab0313 (ADR acceptance).
- [x] 2026-10-23 owner approved option A — Decide ADR0007 cross-store recovery (docs/architecture/adr/0007-cross-store-recovery.md): approve option A (recommended, no schema change), choose B/C, or defer. Unblocks R02.
- [x] 2026-10-23 owner approved option A — Decide ADR0008 typed Geometry/Units (docs/architecture/adr/0008-typed-geometry-units.md): approve option A (recommended, persisted bytes unchanged), choose B/C, or defer. Unblocks G02.
- [ ] N21 native repeatability + full-rebuild baseline: needs ≥3 new isolated Revit launches, each with your Load Once for the fixture add-in. Say when you can be at the machine.
- [ ] Close the isolated fixture Revit (PID 41316) if still open; never production (2544).
- [x] 2026-10-23 approved; scheduled task bim-agentic-os-relay created — Approve or edit the hourly Claude relay prompt shown in chat (not created yet).

## Relay4 (2026-10-23T17:05Z, scheduled) — blockers, nothing else runnable
- [ ] Publish relay3 (still unpushed). Relay4 verified the bundle (d159594..5ab0313, 12 commits, all authored Ezequiel Ader, no AI attribution) but could not push: the cloud git proxy says `manuader/bim-agentic-os is not in this session's authorized repository set` and the computer's VM gets HTTP 403 from its proxy for github.com. Either run on Windows: `git fetch .bim/baton/2026-10-23-core-ifc/relay3.bundle relay3:relay3` then `git push origin relay3`, or add manuader/bim-agentic-os to the scheduled task's repository sources so the next relay can push.
- [ ] R02/G02 (ADR0007/0008 option A) need a .NET 8 SDK for TDD. Neither the cloud workspace nor the computer's VM can reach dot.net / builds.dotnet.microsoft.com / api.nuget.org (egress blocked). Allow those hosts in the organization's network egress settings, or run R02/G02 in a Windows-side Codex/Claude session with the SDK. Relay4 did not write untested C#.
- [ ] Git in the connected folder needs delete permission (git lock files); the relay will request it when it has a commit to land.
- [ ] (relay4, owner chat, revised) The Cowork task bim-agentic-os-relay has no Repository field. Instead, create a local task in Claude Desktop → Code → Scheduled → New task → Advanced settings → Only on this computer, folder = the repo, Hourly. It uses Windows git credentials and the local .NET SDK (no egress needed). The prompt is in the relay4 chat. Then pause bim-agentic-os-relay.
- [ ] (relay4) Until then, publish from Windows: `git push origin relay3`. The relay3 branch is already in your local repo, 14 commits, up to 5a1c3b6. Afterwards relay3.bundle and relay4.bundle can be deleted.

## Relay5 (2026-10-23)
- [x] Publication: GitHub linked; relay3/relay4 pushed and merged; main d9d6278 CI green.
- [ ] `git pull` in the Windows checkout (main moved d159594 → d9d6278).
- [ ] On Windows: `pwsh -NoProfile -File scripts/verify.ps1` and `pwsh -File scripts/check-native-source.ps1` (MCP, typed units and reconcile only verified on Ubuntu CI; Revit add-in compile not rechecked).
- [ ] N21: when you can be at the machine, ≥3 isolated Revit launches with Load Once each, plus a full-rebuild run.

## Relay7 (2026-10-23)
- [ ] `git pull` (main is now b836e2a).
- [ ] With Revit on your machine: `pwsh -File scripts/check-native-source.ps1` — the Revit add-in compile was not rechecked after the typed-units/reconcile core changes (CI has no Revit). Windows `verify.ps1` now runs in CI on windows-latest.
- [ ] N21 remains: native repeatability (≥3 isolated runs, Load Once each) + native full-rebuild comparison. The IFC rebuild baseline is done (no time delta; identity delta).

## Relay14 (2026-10-24)
- [ ] `git pull` (main is now bbb3264).
- [ ] With Revit: `pwsh -File scripts/check-native-source.ps1`. The add-in now also compiles runtimes/revit-native/NativeReconcileContract.cs, and the Bim.Core changes (typed units, CloudGuard, durable writes) were never compiled against Revit.
- [ ] N21 unchanged, plus the native reconcile step: in an isolated fixture run BatchExecutor.Run → fixture.checkpoint, take the Payload after verify_reopened_storage, call NativeReconcileContract.ParsePayload(payload, uid, true) + Reconcile(coreDirectory, …) against Core at r → expect `replayed`; close-without-save case must not commit Core.

## Relay27 (2026-10-25)
- [ ] GitHub access for the scheduled relay: this run could not push nor read the GitHub API ("manuader/bim-agentic-os is not in this session's authorized repository set"; computer VM has no route to github.com). Re-add the repository / GitHub connection to the scheduled task bim-agentic-os-relay (or reconnect GitHub in Settings → Connectors). Until then relays cannot use CI as oracle or publish, so the rule-4c batch is on hold.
- [ ] Still open: `git pull` (main fff4196), `pwsh -File scripts/check-native-source.ps1` with Revit, N21.

## Relay68 (2026-10-30)
- [x] git pull, check-native-source.ps1 (PASSED), N21 runs 1–2 pass.
- [ ] Close the leftover test Revit "BIM-Agentic-Test" without saving (third N21 run, timed out).
- [ ] N21 third run, later: `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Runs 1` and click Load Once.

## Relay134 (2026-11-02, cloud)
- [x] GitHub access for the cloud scheduled relay works now (push to baton/state and main, API/CI reads). Older "give the scheduled task GitHub access" items are resolved.
- [ ] 🔒 P1-04/C04 catalogue re-run with the pipe fix (main includes ed5c759): `git pull` then `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Catalogue` (click Load Once). Expect 17/17.
- [ ] 🔒 N21 third run: `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Runs 1`.
- Optional: allow dot.net / builds.dotnet.microsoft.com / api.nuget.org in network egress so cloud relays can build locally (faster than CI round-trips). Not required.

## Relay135 (2026-11-02, cloud)
- No new owner action. Still open: 🔒 P1-04/C04 `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Catalogue` (Load Once) and 🔒 N21 `... -Runs 1`.

## Relay136 (2026-11-03, cloud)
- [ ] Optional, when convenient: register the MCP server in Claude Desktop (P1-03). Quit Claude Desktop, `git pull`, then `pwsh -NoProfile -File scripts/install-mcp.ps1 -Build` (prints CONFIG path and makes a backup; roll back with `-Uninstall`). Check Settings → Developer shows bim-agentic-os. See docs/mcp-setup.md.
- [ ] 🔒 Catalogue v2 (P9-01: roof, stair, curtain wall, floor opening) still needs an owner-attended smoke run on a test copy; it will be added to `-Catalogue` in a later relay. Still open: 🔒 P1-04/C04 `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Catalogue` (Load Once) and 🔒 N21 `... -Runs 1`.
