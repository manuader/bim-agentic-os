# All-phases roadmap

On 2026-11-02 the owner asked to push every phase of the [master specification](../../BIM_Agentic_OS_PROMPT_DEFINITIVO.md) toward completion in parallel. The relays keep working on every phase that is not blocked, and Revit-only checks wait for the owner. Owner decisions are recorded in the run's OWNER.md on `baton/state`:
- permissive dependencies (MIT, Apache, BSD) are pre-approved;
- CAD input is DXF and PDF;
- there is no Rhino or Blender install yet;
- the first jurisdiction is the Código de Edificación CABA.

[ADR0009](../architecture/adr/0009-revit-command-surface.md) (Revit command surface) and the existing ADRs still apply. A BIM-IR schema change still needs its own accepted ADR; a relay writes it as `proposed` and continues with independent work.

Each phase is a track on the [board](../../tasks.md) with IDs `P<phase>-<nn>`. A relay with two worker slots takes the oldest ready task from the two tracks that were touched longest ago, so all phases advance evenly. A task that needs real Revit, the owner's Load Once, a licence or an install is marked 🔒 with the exact owner step, and the relay moves on.

| Phase | Goal in the spec | Track contents |
|---|---|---|
| 1 Native kernel | full query/create/modify/delete, CLI and MCP on one schema | pipe resilience; generic element query/inspect; modify/delete under the copies guard; MCP setup for the Claude app; catalogue smoke re-run 🔒 |
| 2 Semantic state and mirror | live model mirror, external edits, event journal, graph, provenance | Revit→BIM-IR snapshot export; DocumentChanged journal; semantic graph relations (hosted-by, bounds, on-level); drift detection between mirror and Core |
| 3 Incremental compiler | general intent AST, closure, diff, batch, localized validation | intent AST beyond one room (multi-room, wall types, openings); compiler→catalogue bridge (plans become Revit commands); localized validation |
| 4 Documentation engine | views, dimensions, tags, schedules, sheets, affected-only regeneration | catalogue commands for views/dimensions/tags/schedules/sheets; dependency-driven regeneration from the compiler |
| 5 Skills and design systems | portable skills, conflict resolution, style/typology packs, QA recipes | skill manifest and loader; conflict resolver; CABA code rule pack as validation skill; two contrasting style skills with traceable outputs |
| 6 CAD and requirements | DXF/PDF parsing, provenance, calibration, brief extraction | DXF reader (layers, walls, doors, text) with provenance; PDF vector reader; scale calibration and alignment; brief schema plus validated extraction |
| 7 Generative kernels | space planning, prototypes, circulation, facade, alternatives | constraint space-planner (adjacency, areas, CABA minimums); apartment prototype generator; alternative branches with scoring |
| 8 Rhino, Blender, IFC | tested adapters, openBIM QA | IFC import to BIM-IR and roundtrip; .3dm read/write via rhino3dm without Rhino; Blender bpy adapter (execution 🔒 until installed) |
| 9 Revit parity | all domains, families, worksharing, existing models | catalogue v2 (roofs, stairs, curtain walls, slab openings), structure (grids, columns, beams, foundations), families/parameters, guarded free path (C05), parity census registry |
| 10 Global production | jurisdictions, IDS/BCF/bSDD, permissions, audit, packaging | IDS checks; BCF export; audit log and permission model; installer and packaging |

Verification states stay honest: `unit-tested` and `integration-tested (ifc)` come from CI, and `verified-in-revit` only from an owner session.
