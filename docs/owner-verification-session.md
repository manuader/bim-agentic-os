# Owner one-run verification session (P1-06)

One owner-attended Revit 2025 session verifies, in one go, everything the catalogue commands still need from real
Revit. The script is [scripts/owner-native-session.ps1](../scripts/owner-native-session.ps1); the step lists, checks and
the sequence runner are in [scripts/owner-catalogue-steps.ps1](../scripts/owner-catalogue-steps.ps1). Headless tests (a
fake executor in process, and the real CLI against a fake add-in pipe) are in
[tests/integration/test-catalogue-session.ps1](../tests/integration/test-catalogue-session.ps1) and run in
`scripts/verify.ps1` (`catalogue-session`, `catalogue-session-cli`). Evidence level of the new groups: unit-tested;
each becomes verified-in-revit only after this session passes on the owner's machine.

## Command

```powershell
pwsh -NoProfile -File scripts/owner-native-session.ps1 -Catalogue
```

The script checks the native source against the installed Revit DLLs, builds the add-in and the CLI (Release), starts
one new child Revit on a fresh fixture copy (`.bim/native-runs/<guid>/BIM-Agentic-Test.rvt`; running Revit processes,
such as a production model, are recorded and never touched) and waits for the owner's **Load Once**. Then it sends every
step through the CLI `revit-command` verb on that child's pipe (`journal.read` goes straight to the same pipe as a wire
line, because it is not a catalogue command). Do not touch the child Revit until the script says the session ended.

`-CatalogueV1Only` runs only the 17 catalogue v1 steps (the run that already passed 17/17).

## What passes

The last lines are:

```
>>> Ejecución 1: Catálogo: 17/17 pasos OK; grupos: v2 6/6, vistas 5/5, snapshot 2/2, consultas 6/6, estructura 12/12, documentacion 14/14, diario 6/6, modificar 12/12; total 80/80; resultado: TODO OK
>>> Sesión terminada. Catálogo: 17/17 pasos OK; grupos: ...; resultado: TODO OK
```

- `Catálogo: 17/17 pasos OK` keeps its v1 meaning: the 17 v1 steps run first and are unchanged.
- Each group shows passed/total. `resultado: TODO OK` means every step passed.
- `resultado: FALLÓ en <grupos>` names every group with a failed step. A failed step or group never stops later groups.
  A later step that needs something a failed step should have made is not sent and says what is missing.
- `(DETENIDO: ...)` means the run stopped: a step timed out (unknown completion), the pipe was lost
  (`pipe_connect_timeout`, `pipe_io_error`, `no_response`) or the child Revit closed. Nothing after that step was sent.
- One `Falló el paso N [grupo] (...)` line per failed step gives the reason as `<check>: se pidió <requested>, se observó
  <observed>`.

Evidence: `.bim/native-sessions/<utc>/catalogue-run1.json` (per step: group, request, response, `comparisons` rows with
`requested`/`observed`/`ok`, pass/fail, reason; plus `counts` for v1, `groups`, `overall`, `all_passed`, `aborted`),
`session.log` and `summary.json` (`catalogue_scope`, `catalogue_all_passed`). Request files are in
`.bim/native-runs/<guid>/catalogue-requests/`.

The model is never saved. The last group deletes the v1 south wall and its door; close the child Revit without saving.

## Groups, in order

| Group | Task | Steps and what each one checks |
|---|---|---|
| `v1` | C04 | The 17 unchanged v1 steps: queries, four walls, curved wall, door, window, floor, room, replay, conflict, client-side arc refusal. |
| `v2` | P9-01 | Levels pair; roof types; hip roof (30 degrees) on the upper level over the 6000 x 4000 rectangle (level, type, 4 sloped edges, area); straight stair Level 1 to Level 2 at x = -8000 (levels, width 1000, run ends, risers = desired); straight curtain wall with 3 vertical + 2 horizontal added grid lines (type, height, 5 added lines); 1000 x 1000 opening in the v1 floor (host, profile, area). |
| `vistas` | P4-01 | Section view family types; view templates; floor plan of the v1 level (name, type, level); section (0,0)-(6000,0), depth 3000, -500 to 4000 (right (1,0,0), up (0,0,1), view (0,-1,0), far clip, crop active); isometric 3D with box (0,0,0)-(8000,6000,3500). Same requests and expected values as `tests/integration/native-views-probe.ps1`. |
| `snapshot` | P2-01 | `query.snapshot` (document, v1 door 1500 mm and window 2000 mm along their host wall axes with sills 0 and 900, floor unmapped `no_bim_ir_kind`, curved wall unmapped `curved_wall_unsupported`); a second snapshot whose readback is byte-identical. |
| `consultas` | P1-02 | `query.elements` walls (OST_Walls, at least 5, south wall listed, not truncated); limit 1 (truncated, reason `limit`); doors on the v1 level; Comments plus a missing parameter (`missing_parameters`); `query.inspect` of the south wall (WALL_USER_HEIGHT_PARAM 3000 mm); unknown UniqueId (`reference_not_found`). |
| `estructura` | P9-02 | Levels pair; structural types column, framing, foundation, wall_foundation; `query.grids`; grid (20000,-2000)-(20000,6000) and its replay; column at (20000,0) base to top level; beam (20000,0)-(26000,0) on the upper level; isolated footing at (20000,0); wall foundation under the v1 north wall. A template without a loaded structural family fails that type query and the steps that need it say so. |
| `documentacion` | P4-02 | Title blocks; its own floor plan; two grids 3000 mm apart; dimension between the grids (3000 mm); dimension between the interior faces of the south and north walls (4000 mm minus the wall type width); wall tag; door tag with leader and head (1500,-1500); door schedule Mark, Type sorted by Mark; sheet `C04-r1-A101` and its replay; plan and schedule placed on the sheet; the same plan placed again refused with `args_invalid` naming the sheet. |
| `diario` | P2-03 | `journal.read` baseline (attached, document); one CLI wall; read again (`skipped_add_in` grew, the wall is in `diff.added`, no external entry for it); an empty read; two `peek` reads that are byte-identical. |
| `modificar` | P1-05, destructive, last | Inspect the west wall height (3000); `element.set_parameter` WALL_USER_HEIGHT_PARAM 3200 dry run (before 3000, after 3200, modified empty), inspect (still 3000), real (modified), replay (`replayed`), inspect (3200); Comments text; read-only CURVE_ELEM_LENGTH refused (`args_invalid`); `element.delete` of the south wall dry run (the door among the dependents), refused without `confirm_dependents` (`args_invalid`), deleted with exactly the dry-run dependents; inspect the deleted wall (`reference_not_found`). |

Every write uses a new `operation_id` (`c04-<utc>-r<run>-<step>`); replays reuse the original step's id on purpose.
Nothing is retried automatically.
