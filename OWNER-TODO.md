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

## Relay137 (2026-11-03, cloud)
- [ ] 🔒 P4-01 view commands probe, in the same owner session as the catalogue re-run (after `git pull`): run `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Catalogue` (Load Once), then with its result values `./tests/integration/native-views-probe.ps1 -DocumentPath <result.documentPath> -DocumentIdentity <result.documentUniqueId> -PipeName <result.pipeName> -Dotnet <absolute dotnet.exe> -CliDll <absolute Bim.Cli.dll> -EvidenceDirectory <absolute run dir>`. Only on the BIM-Agentic-Test*.rvt copy. Exit 0 = section + boxed 3D view created and read back; exit 2 = mismatch (report.json shows requested vs observed).
- Still open: 🔒 P1-04/C04 `-Catalogue`, 🔒 N21 `-Runs 1`, optional MCP install (`scripts/install-mcp.ps1 -Build`; tools/list now includes catalogue v2 and the view commands).

## Relay138 (2026-11-03, cloud)
- [ ] Review the CABA rule pack values marked `source_confidence=to-verify` in skills/ar.caba.codigo-edificacion/skill.json (1st-class height 2.60 m, 3rd-class 2.40 m, corridor 2.10 m, illumination x = 10, ventilation k = i/3, dormitorio as 3rd class per the housing RT). Tell a relay which numbers to change or mark cited (article + source).
- [ ] 🔒 P2-01 query.snapshot, in the same owner session as the catalogue re-run (after `git pull`, on the BIM-Agentic-Test*.rvt copy only): send `{"command":"query.snapshot","args":{}}` through the CLI `revit-command` verb or the MCP tool `revit_query_snapshot` with a straight centreline wall, a door, a window, a floor and a rectangular room in the model; run twice and confirm byte-identical replies, door/window ends on the wall axis, and the floor under `unmapped` as `no_bim_ir_kind`.
- Still open: 🔒 P1-04/C04 `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Catalogue` (Load Once), 🔒 P4-01 views probe, 🔒 N21 `... -Runs 1`, optional MCP install.

## Relay139 (2026-11-03, cloud)
- [ ] 🔒 P1-02 element queries, in the same owner session as the catalogue re-run (after `git pull`, on the BIM-Agentic-Test*.rvt copy only), through the CLI `revit-command` verb or MCP tools `revit_query_elements` / `revit_query_inspect`: (1) `{"category":"wall"}` → fixture walls by ascending id with level, family, type, Mark/Comments; (2) `{"category":"door","level":"<Level 1 UniqueId>","parameters":["Width","Fire Rating"]}` → Width in mm, Fire Rating present or under missing_parameters; (3) `{"level":"Level 1","limit":2}` → returned 2, truncated true, no views/annotations listed; (4) `{"category":"OST_NotACategory"}` → reference_not_found; (5) inspect a wall UniqueId → Unconnected Height in mm, Area m2, Volume m3, Base Constraint with the level UniqueId (compare with the Properties panel); (6) inspect an unknown UniqueId → reference_not_found; (7) document has no unsaved change / new undo entry afterwards.
- Still open: 🔒 P1-04/C04 `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Catalogue` (Load Once), 🔒 P4-01 views probe, 🔒 P2-01 query.snapshot, 🔒 N21 `... -Runs 1`, CABA to-verify values review, optional MCP install.

## Relay140 (2026-11-03, cloud)
- No new owner action (PDF reader, alternatives scoring and BCF export are headless and unit-tested in CI).
- Optional: if you have a BCF viewer (e.g. BIMcollab Zoom, Solibri), open a BCF produced by tests/bcf to confirm it loads; the official buildingSMART XSDs could not be fetched from the cloud.
- Still open: 🔒 P1-04/C04 `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Catalogue` (Load Once), 🔒 P4-01 views probe, 🔒 P2-01 query.snapshot, 🔒 P1-02 query.elements/inspect, 🔒 N21 `... -Runs 1`, CABA to-verify values review, optional MCP install.

## Relay141 (2026-11-03, cloud)
- [ ] 🔒 P9-02 structure commands + P2-03 edit journal, in the same Revit session on a test copy (never production): `git pull` (main 2b3b36e), then `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Catalogue` (Load Once). Probe steps are in docs/SESSION-2026-11-03-relay141-structure-styles-journal.md ("Owner probes"). No new decision needed.

## Relay142 (2026-11-03, cloud)
- [ ] 🔒 P4-02 documentation commands, in the same owner session as the catalogue re-run (after `git pull`, only on a BIM-Agentic-Test*.rvt copy with two parallel grids, a wall, a door and a Level 1 floor plan), through the CLI `revit-command` verb or the MCP tools: `query.title_blocks`; `dimension.create_linear` between the two grids (expect value_mm = grid spacing) and between two parallel walls with `wall_face` interior; `tag.create` on the wall with and without `head`; `schedule.create {"category":"door","fields":["Mark","Type"],"sort_by":"Mark"}`; `sheet.create {"number":"A101","name":"Test"}`; `sheet.place_view` of the plan and the schedule at [420,297]; place the plan again → args_invalid naming A101; a legend on two sheets is allowed; replay one operation_id → same receipt. Record requested vs observed values.
- Optional (no Blender installed now): open a .glb written by `GltfExporter.WriteGlb` (adapters/gltf/README.md) via Blender File → Import → glTF 2.0; walls upright in metres, Custom Properties show bim_id/kind/level.
- Still open: 🔒 P1-04/C04 `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Catalogue` (Load Once), 🔒 P4-01 views probe, 🔒 P2-01 query.snapshot, 🔒 P1-02 query.elements/inspect, 🔒 P9-02 structure, 🔒 P2-03 journal.read, 🔒 N21 `... -Runs 1`, CABA to-verify values review, optional MCP install.

## Relay144 (2026-11-03, cloud)
- [ ] 🔒 P1-05 modify/delete probe (fixture copy only, never production): `git pull` (main a31af61), then in an owner Revit session (`pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1`, Load Once) run the recipe in docs/SESSION-2026-11-03-relay144-modify-cad-draft-prototypes.md "Owner probe": element.set_parameter dry-run/real/replay, read-only refusal, type parameter also_modified; element.delete dry-run dependents, pinned dependent refusal, confirm_dependents.
- Still open: 🔒 -Catalogue re-run (P1-04/C04) + views/snapshot/elements/structure/journal/documentation probes; N21 run 3; CABA to-verify review; optional MCP install; optional Blender .glb check.

## Relay145 (2026-11-03, cloud)
- [x] P1-04/C04 catalogue v1 17/17 in Revit (owner session 20261103T174908Z) → ✅ verified-in-revit. CABA values confirmed → owner-confirmed in the rule pack (P5-06).
- [ ] 🔒 ONE run verifies everything waiting for Revit (P1-06, replaces the separate views/snapshot/elements/structure/journal/documentation/modify probes): `git pull`, then `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Catalogue`, click Load Once, don't touch the child Revit until it ends. Pass = line ends `resultado: TODO OK` (total 81/81). `OK con bloqueados: …` = the test template lacks a family/type/level (named in "Bloqueado el paso" lines). Schedule fields assume an English Revit UI. Close the child Revit without saving. Evidence: .bim/native-sessions/<utc>/summary.json. Details: docs/owner-verification-session.md.
- [ ] Decide: are escritorio, estudio and biblioteca third class like dormitorio? (9 CABA rules stay to-verify until you say so.)
- [ ] Decide: OK to keep the 4-code Uniclass 2015 excerpt (CC BY-ND 4.0, attributed, unmodified) in the repo? (core/Bim.Core/Classification/README.md)
- Still open: 🔒 N21 run 3 (`... -Runs 1`); optional Blender .glb check.

## Relay147 (2026-11-04, cloud)
- [ ] Materials/types overlap: your chat branch `chat-materials-types` and the relay branch `relay147-P9-06` implement the same commands (material.create, wall_type.create, floor_type.create). The relay did NOT merge its version. Tell a relay (OWNER.md) whether to independently review and merge `chat-materials-types` (porting relay147-P9-06's hardening: core/shell readback, ordinal-first name pick, 1.2 mm minimum layer, Revit name errors → args_invalid), or merge it from the chat yourself.
- No Revit action new this relay (P8-05 IFC import and P3-04 local validation are headless, CI-tested). Still open: 🔒 one `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Catalogue` run (81 steps) on a test copy, 🔒 N21 `... -Runs 1`, escritorio/estudio/biblioteca class, Uniclass CC BY-ND excerpt OK?

## Relay148 (2026-11-04, cloud)
- No new owner action (P4-03 documentation regeneration, P6-05 brief extraction, P7-04 circulation are headless and unit-tested in CI).
- [ ] Decide P9-06: should a scheduled relay independently review and merge the chat branch `chat-materials-types` (material.create/set, wall_type.create, floor_type.create, query.fill_patterns, element.change_type) and port the relay147 hardening, or will the owner chat merge it? Answer in OWNER.md.
- Optional: send a real DXF/PDF floor plan with room labels (no client data) into `.bim/` so a relay can test P6-05 brief extraction beyond synthetic fixtures.
- Still open: 🔒 `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Catalogue` on a test copy (Load Once), escritorio/estudio/biblioteca class, Uniclass CC BY-ND excerpt, 🔒 N21 `... -Runs 1`.

## Relay149 (2026-11-04, cloud)
- No new owner action. New on main (bb0ccc2): `pwsh -File scripts/package.ps1 -OutputDirectory <new folder>` builds a release package (CLI + MCP + add-in manifest template) and `scripts/package-verify.ps1 -PackageDirectory <folder>` checks it; `query.journal` (read-only) is compiled but not yet in the owner -Catalogue steps.
- Still open: 🔒 ONE `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Catalogue` on a test copy (Load Once); 🔒 N21 `... -Runs 1`; escritorio/estudio/biblioteca class; Uniclass licence.

## Relay151 (2026-11-04, cloud)
- No new required owner action (P4-04 default documentation set, P6-06 DXF attributes/MTEXT, P7-05 facade openings are headless and unit-tested in CI).
- Optional decision: for lots between party walls (medianeras), confirm that illumination windows may go only on the front and back (contrafrente) footprint edges; P7-05 currently takes the allowed edges as an option (default all four).
- Optional: if you have a real CAD plan with room-tag blocks (ATTRIB NAME/AREA), drop a copy in the repo's tests/cad/fixtures via a relay request to replace the hand-written fixture.
- Still open: 🔒 one `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Catalogue` run on a test copy (Load Once), 🔒 N21 `... -Runs 1`, escritorio/estudio/biblioteca class, Uniclass licence.

## Relay152 (2026-11-04, cloud)
- No new required owner action (P2-06 provenance ledger, P10-06 jurisdiction registry, P5-04 style defaults into the intent compiler are headless and unit-tested in CI).
- FYI: skills/ now contains a clearly fictitious stub jurisdiction pack `xx.example.codigo-ejemplo` (every rule marked STUB, refused unless a caller opts in) used only to prove multi-jurisdiction selection. Say in OWNER.md if you'd rather it lived under tests/.
- Optional: the style packs don't define a door height or an interior wall type name; if you want defaults (e.g. door 2.05 m, interior "Tabique 10"), tell a relay the values.
- Still open: 🔒 one `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Catalogue` run on a test copy (Load Once), 🔒 N21 `... -Runs 1`, escritorio/estudio/biblioteca class, Uniclass licence, party-wall facade edges (P7-05).

## Relay153 (2026-11-04, cloud)
- [ ] 🔒 P1-08 document.save_copy, in the same owner session as the catalogue run (after `git pull`, on `.bim/native-runs/<run>/BIM-Agentic-Test.rvt` only): make one change (e.g. wall.create); `revit_document_save_copy` with `path = <run>\BIM-Agentic-Test-copy-1.rvt` → success, `sha256` equals `Get-FileHash` of the file (while Revit still has it open), `source_unchanged` true, `is_modified` false, the Revit title shows the copy; `query.levels` with `document.path` = the copy succeeds; repeat → `save_copy_refused` `target_exists`; a target outside `.bim/native-runs` → `outside_copies_root` before the pipe.
- [ ] 🔒 P9-07 roof.create_extrusion on an enabled test copy: `level` "Level 1", `type` from `revit_query_roof_types`, `plane` {start [0,0], end [6000,0]}, `profile` [{kind: arc, start [-300,2700], end [6300,2700], through [3000,3800]}], extrusion -300 to 10300 → success, created = [roof, reference plane], roof spans y -300…10300. Repeat with the plane reversed ([6000,0]→[0,0]) → roof along -y. Diagonal: plane [0,0]→[6000,6000], flat line profile u 0..8485 at z 3000, extrusion 0..1000 → roof on the north-west side. A transaction_failed detail tells which unverified API assumption is wrong. (P1-11 will add these to `-Catalogue`.)
- Still open: ONE `-Catalogue` run (81 steps) on a test copy, N21 run 3, escritorio/estudio/biblioteca class, Uniclass licence, party-wall facade edges (P7-05), style-pack door height / interior wall type defaults.

## Relay155 (2026-11-04, cloud)
- No new owner action: P4-05 (documentation replacement plan), P6-07 (cad-draft/cad-brief CLI) and P7-06 (facade windows in BIM-IR) are headless and unit-tested in CI.
- Optional: try `dotnet Bim.Cli.dll cad-draft <your plan>.dxf out.json --metres-per-unit 0.001` (or cad-brief) on a real plan of yours and tell a relay what it got wrong; all fixtures so far are synthetic.
- Still open: one `-Catalogue` owner session on a test copy (OWNER-TODO Relay153 steps), N21 run 3, escritorio/estudio/biblioteca class, Uniclass licence.

## Relay156 (2026-11-05, cloud)
- No required owner action: P2-07 (drift resolution plan), P5-07 (vivienda unifamiliar typology pack) and P10-07 (add-in install script) are headless and unit-tested in CI.
- Optional 🔒 P10-07 on your PC, only into a scratch folder first: after `git pull` and `pwsh -File scripts/package.ps1 -OutputDirectory .bim/release`, run `pwsh -File scripts/install-addin.ps1 -PackageDirectory .bim/release -AddinsDirectory .bim/addins-test -AssemblyPath <path to your built Bim.Revit.dll>` and check the generated Bim.addin; `-Uninstall -AddinsDirectory .bim/addins-test` removes it. Only use `-RevitUserAddins` when you want it in the real Revit 2025 Addins folder (backs up any existing Bim.addin).
- Optional: the vivienda programme values (bedrooms per 2 persons, bathrooms per 4, min/target areas) are illustrative; tell a relay which to change.
- Still open: one `-Catalogue` owner session on a test copy (OWNER-TODO Relay153 steps), N21 run 3, escritorio/estudio/biblioteca class, Uniclass licence.

## Relay157 (2026-11-05, cloud)
- No new decision needed. New commands compiled + unit-tested only; add them to the next owner-attended session on a test copy (after `git pull`, BIM-Agentic-Test*.rvt only, Load Once via `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/owner-native-session.ps1 -Catalogue`):
- [ ] 🔒 P1-09 element.move / element.copy: move a free wall 1 m east (`revit_element_move {element_uids:[W], vector_mm:[1000,0,0]}` → both curve ends +1000 x); copy a column up 3000 mm (copies[0] maps source→new id); copy a wall with a door 4 m north (door in also_created); move a wall together with its own door (door moves once); move a floor 500 mm (readback location kind "profile"); same operation_id again → replayed; pinned wall → element_pinned; a tag → element_view_specific; zero vector → length_invalid before the pipe; a batch of move+copy then one Ctrl+Z undoes both.
- [ ] 🔒 P9-09 direct_shape.create: `{category: site, shape: {kind: extrusion, level: <Level 1>, outline: [[0,0],[1200,0],[1200,800],[0,800]], height_mm: 900}, name: "boulder 1", material: <from query.materials>}` → bbox (0,0,L)-(1200,800,L+900), volume ≈ 864000000 mm³; box `{category: planting, shape: {kind: box, min: [2000,0,0], max: [2600,600,700]}}`; try generic_model / entourage / topography and note which return args_invalid at args.category; same operation_id → replay; bow-tie outline → boundary_self_intersecting before the pipe.
- Still open: ONE -Catalogue run + save_copy / roof.create_extrusion steps (Relay153), optional install-addin scratch try (Relay156), escritorio/estudio/biblioteca class, Uniclass licence, N21 run 3.

## Relay159 (2026-11-05, cloud)
- No new Revit step (P4-06, P6-09, P7-07 are headless, unit-tested in CI). Still open: the Relay153/Relay157 test-copy -Catalogue smoke steps, N21 run 3, escritorio/estudio/biblioteca class, Uniclass licence.
- [ ] Housekeeping: delete the throwaway branch `relay159-P6-09-baseline` on GitHub (a digest probe; the cloud relay was not allowed to delete branches): `git push origin --delete relay159-P6-09-baseline`.
- FYI: P4-06 dimension chains assume BIM-IR walls are realized unflipped in Revit (exterior face left of start→end); the first real -Catalogue/doc run will confirm or refute the chosen wall face.

## Relay160 (2026-11-05, cloud)
- No new Revit step (P2-08, P5-08, P10-09 are headless, unit-tested in CI). Still open: the Relay153/Relay157 test-copy -Catalogue smoke steps, N21 run 3, escritorio/estudio/biblioteca class, Uniclass licence, delete branch relay159-P6-09-baseline.
- FYI: main now has a root `.gitattributes` with `skills/** -text` (skill pack hashes for the new skills.lock.json must match on Windows and Linux). After `git pull` on Windows, if `git status` shows skills files modified, run `git add --renormalize skills` is NOT needed — just `git checkout -- skills` to get the LF bytes.
