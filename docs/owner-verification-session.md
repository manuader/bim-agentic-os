# Owner one-run verification session (P1-06, P1-11)

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
>>> Ejecución 1: Catálogo: 17/17 pasos OK; grupos: v2 6/6, vistas 5/5, snapshot 2/2, consultas 6/6, estructura 12/12, documentacion 15/15, diario 6/6, formas 3/3, extrusion 5/5, transformar 9/9, copia 5/5, modificar 12/12; total 103/103; resultado: TODO OK
>>> Sesión terminada. Catálogo: 17/17 pasos OK; grupos: ...; resultado: TODO OK
```

- `Catálogo: 17/17 pasos OK` keeps its v1 meaning: the 17 v1 steps run first and are unchanged.
- Each group shows passed/total. `resultado: TODO OK` means every step passed.
- `resultado: FALLÓ en <grupos>` names every group with a failed step. A failed step or group never stops later groups.
  A later step that needs something a failed step should have made is not sent and says what is missing.
- `resultado: OK con bloqueados: estructura (2)` means nothing failed, but the fixture lacked a precondition (a second
  level, or a loaded type of a kind such as a structural column family): that step and every step that needs what it
  should have picked are reported **blocked** (`Bloqueado el paso N ...: bloqueado: falta en el modelo de prueba: ...`),
  counted apart from failures (`estructura 10/12 (2 bloqueados)`). With failures as well the verdict is
  `FALLÓ en <grupos>; bloqueados en <grupo (n)>`.
- `(DETENIDO: ...)` means the run stopped: a step timed out (unknown completion), the pipe was lost
  (`pipe_connect_timeout`, `pipe_io_error`, `no_response`, from the CLI or from the journal.read wire line) or the
  child Revit closed. Nothing after that step was sent. This also applies inside the 17 v1 steps (new with P1-06:
  before, every later v1 step was still sent and failed); a v1-only run that stopped prints
  `Catálogo: n/17 pasos OK; DETENIDO: ...`.
- One `Falló el paso N [grupo] (...)` line per failed step gives the reason as `<check>: se pidió <requested>, se observó
  <observed>`.

Evidence: `.bim/native-sessions/<utc>/catalogue-run1.json` (per step: group, request, response, `comparisons` rows with
`requested`/`observed`/`ok`, pass/fail, reason; plus `counts` for v1, `groups`, `overall`, `all_passed`, `aborted`),
`session.log` and `summary.json` (`catalogue_scope`, `catalogue_all_passed`). Request files are in
`.bim/native-runs/<guid>/catalogue-requests/`.

The fixture model itself is never saved. The `copia` group saves one new copy
`.bim/native-runs/<guid>/BIM-Agentic-Test-c04-<utc>-r<run>-copia.rvt` (its sha256 is compared with the file on disk and
recorded in the step's readback); Revit's Save As then keeps **the copy** open in place of the fixture, so the last
group (which deletes the v1 south wall and its door) changes the copy only in memory and the run ends with the copy
open. When Revit asks at close whether to save, answer **No**, so the copy stays equal to the recorded sha256.
`catalogue-run<N>.json` records `fixture_saved` (false), `copy_path`, `open_document_path` and
`document_switched_by` (`c-save`, or `c-levels-copy` when the save failed after Save As had already re-pointed the
document and the probe naming the copy succeeded).

## Preconditions

- Revit 2025 with the UI in **English**: `schedule.create` finds the fields `Mark` and `Type` by Revit's visible
  (localized) name; the catalogue has no built-in id form for schedule fields, so a failure of that step carries
  "precondición: Revit con la interfaz en inglés". Parameters are otherwise addressed by built-in id
  (`ALL_MODEL_INSTANCE_COMMENTS`, `WALL_USER_HEIGHT_PARAM`, `CURVE_ELEM_LENGTH`), never by localized name.
- The default metric template: two levels, a basic and a curtain wall type, a non-glazed roof type, wall-hosted door
  and window types, and (for `estructura`) structural column, framing, isolated and wall foundation types. A missing
  one blocks the steps that need it.

## Groups, in order

| Group | Task | Steps and what each one checks |
|---|---|---|
| `v1` | C04 | The 17 unchanged v1 steps: queries, four walls, curved wall, door, window, floor, room, replay, conflict, client-side arc refusal. |
| `v2` | P9-01 | Levels pair; roof types; hip roof (30 degrees) on the upper level over the 6000 x 4000 rectangle (level, type, 4 sloped edges, area); straight stair Level 1 to Level 2 at x = -8000 (levels, width 1000, run ends, risers = desired); straight curtain wall with 3 vertical + 2 horizontal added grid lines (type, height, 5 added lines); 1000 x 1000 opening in the v1 floor (host, profile, area). |
| `vistas` | P4-01 | Section view family types; view templates; floor plan of the v1 level (name, type, level); section (0,0)-(6000,0), depth 3000, -500 to 4000 (right (1,0,0), up (0,0,1), view (0,-1,0), origin y 0, crop x 0 to 6000 and z -500 to 4000, far clip, crop active); isometric 3D with box (0,0,0)-(8000,6000,3500). Same requests and expected values as `tests/integration/native-views-probe.ps1`. |
| `snapshot` | P2-01 | `query.snapshot` (document, v1 door 1500 mm and window 2000 mm along their host wall axes with sills 0 and 900, floor unmapped `no_bim_ir_kind`, curved wall unmapped `curved_wall_unsupported`); a second snapshot whose readback is byte-identical. |
| `consultas` | P1-02 | `query.elements` walls (OST_Walls, at least 5, south wall listed, not truncated); limit 1 (truncated, reason `limit`); doors on the v1 level; default parameters include Comments by built-in id; a missing parameter name (`missing_parameters`); `query.inspect` of the south wall (WALL_USER_HEIGHT_PARAM 3000 mm); unknown UniqueId (`reference_not_found`). |
| `estructura` | P9-02 | Levels pair; structural types column, framing, foundation, wall_foundation; `query.grids`; grid (20000,-2000)-(20000,6000) and its replay; column at (20000,0) base to top level; beam (20000,0)-(26000,0) on the upper level; isolated footing at (20000,0); wall foundation under the v1 north wall. A template without a loaded structural family fails that type query and the steps that need it say so. |
| `documentacion` | P4-02 | Title blocks; its own floor plan; two grids 3000 mm apart; dimension between the grids (3000 mm); dimensions between the south and north walls on their interior and on their exterior faces (each 4000 mm ± the wall type width, the two differing by 2 × width; which face pair is the inner one depends on Revit's wall orientation, so it is not asserted); wall tag; door tag with leader and head (1500,-1500); door schedule Mark, Type sorted by Mark; sheet `C04-r1-A101` and its replay; plan and schedule placed on the sheet; the same plan placed again refused with `args_invalid` naming the sheet. |
| `diario` | P2-03 | `journal.read` baseline (attached, document, `baseline_existed` false); one CLI wall; read again (`skipped_add_in` grew, the wall is in `diff.added`, no external entry for it); an empty read; two `peek` reads that are byte-identical. |
| `formas` | P9-09 | `direct_shape.create` generic_model box (50000,0,0)-(51000,1000,1000) (category, OST_GenericModel, name, volume 1e9 mm3, box within 1 mm); site extrusion 1200 x 800 at (52000,0), 900 high on the v1 level (OST_Site, volume 8.64e8 mm3, box z from the level elevation); a crossed outline refused by the CLI (`boundary_self_intersecting`, `args.shape.outline[0]`). |
| `extrusion` | P9-07 | Roof types; `roof.create_extrusion` barrel (arc (-300,2700)-(6300,2700) through (3000,3800), extents 0 to 4000) on an axis-aligned plane (30000,0)->(36000,0), the reversed plane (36000,10000)->(30000,10000) and a diagonal plane (40000,0)->(43600,4800) (roof and reference plane created, level, type, plane echo, extents, one arc with its crown at (3000,3800), the box holds the profile swept to the plane's left within 25 mm and not the one swept to its right); a closed profile refused by the CLI (`args_invalid`, `args.profile`). |
| `transformar` | P1-09, P1-10 | A wall (0,15000)-(3000,15000); `element.move` (1000,500,0) and its replay (`replayed`); a zero vector refused by the CLI (`length_invalid`); `element.copy` (0,2000,0) (one copy at (1000,17500)-(4000,17500)); `element.rotate` of the copy 90 degrees about (1000,17500) (end at (1000,20500)); the v1 door turned without its wall (`element_not_rotatable`, "include the host"); 360 degrees refused by the CLI (`angle_invalid`); `element.mirror` across x = 6000 keeping the original (new wall at (8000..11000,15500)). |
| `copia` | P1-08 | `document.save_copy` into `.bim` outside `.bim/native-runs` refused by the CLI (`save_copy_refused`, `outside_copies_root:`); the copy next to the fixture (path, open document = the copy, source unchanged, not modified, sha256 and size equal to the file on disk, warning); the same request again refused by the CLI (`target_exists:`, not replayed); `query.levels` naming the copy (succeeds; if `c-save` failed after Save As this probe switches the run to the copy); `query.levels` naming the fixture (`document_conflict`). Every later step names the copy. |
| `modificar` | P1-05, destructive, last (on the copy when one was saved) | Inspect the west wall height (3000); `element.set_parameter` WALL_USER_HEIGHT_PARAM 3200 dry run (before 3000, after 3200, modified empty), inspect (still 3000), real (modified), replay (`replayed`), inspect (3200); Comments text (ALL_MODEL_INSTANCE_COMMENTS); read-only CURVE_ELEM_LENGTH refused (`args_invalid`, field `args.parameter`, detail "read-only"); `element.delete` of the south wall dry run (the door among the dependents), refused without `confirm_dependents` (`args_invalid`, field `args.confirm_dependents`, detail "unconfirmed"), deleted with exactly the dry-run dependents; inspect the deleted wall (`reference_not_found`). |

Every write uses a new `operation_id` (`c04-<utc>-r<run>-<step>`); replays reuse the original step's id on purpose.
Nothing is retried automatically.
