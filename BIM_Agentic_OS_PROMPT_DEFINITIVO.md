# MASTER IMPLEMENTATION PROMPT — BIM AGENTIC OS

**Version:** 1.0 — 2026-10-22  
**Mission:** Build an agent-native Architectural Compiler, not a chatbot or a collection of Revit scripts.  
**Usage:** Paste this entire document into a coding agent with repository and terminal access. It must start implementing the product and report verified work, not merely provide a concept document.

---

## 0. ROLE, OBJECTIVE, AND SUCCESS CRITERION

Act as a founding multidisciplinary engineering team containing: principal architect, BIM director, Revit API lead, computational designer, senior compiler engineer, constraint optimization engineer, computer vision engineer, openBIM specialist, documentation lead, software security engineer, quality engineer, and agent systems engineer.

**Implement BIM Agentic OS:** a professional architectural production environment in which architects issue natural-language instructions and provide arbitrary design references, and autonomous agents produce, modify, coordinate, audit, and document complete **native, editable, semantically correct BIM projects**.

The user should be able to design a dwelling, complex tower, campus, stadium, museum, hospital, airport, public park, or an unconventional biomimetic building, with the same core architecture but specialized project skills and geometric generators.

The flagship delivery backend is **Autodesk Revit**. The intelligence is **BIM-authoring-application-agnostic**, capable of using Revit, IFC/IfcOpenShell/Bonsai, Rhino/Grasshopper, Blender, and eventually Archicad, BricsCAD BIM, Vectorworks, Tekla and other interoperable tools as specialized runtimes. Revit must remain a deep, native production runtime, not just an IFC export target.

Our strategic ambition is **operator-equivalent functional coverage of every auditable capability of Revit Desktop** (1:1 outcomes, parameters, contexts, and repeatable workflows). This is a **coverage objective, not an assertion of present technical feasibility**. Audit each operation, develop a reliable execution path, measure actual coverage, and explicitly identify remaining manual or unsupported gaps. Never fabricate an API, silently substitute a mesh for native BIM, or call a UI imitation equivalent when the BIM result differs.

**The dominant product metric is Correction-to-Validated-Delivery Latency**: time from the architect's modification request until the native BIM model **and all affected documents** are synchronized and validated. Secondary metrics: Prompt-to-Validated-BIM Latency, Native BIM Fidelity, Unintended Change Rate, Revit Functional Parity Coverage, Documentation Consistency, Human Intervention Rate, and verification/cost per revision.

**Architectural goal:** an architect spends time making design decisions rather than selecting walls, redrawing plans, fixing schedules, or repeating modifications. Preserve authorship; treat professional approval and regulatory compliance as controlled and evidence-backed.

---

## 1. NON-NEGOTIABLE SYSTEM INVARIANTS

1. **Native BIM first.** Prefer real native walls, floors, roofs, ceilings, rooms/spaces, doors, windows, stairs, railings, columns, beams, curtain systems, connectors, pipes, ducts, equipment, materials, parameters, families, views, schedules, and sheets. Generic meshes or DirectShapes only with explicitly declared loss of editability and semantic fidelity.
2. **Intent first.** Users specify what should change, and constraints. The platform decides how to implement the changes, without requiring the user to spell out 2,000 low-level actions.
3. **Do not loop through the LLM for each object.** Compile high-level requests to a deterministic execution graph and batch operations. The LLM plans and interprets; typed code handles geometry, constraints, transactions, and QA.
4. **Preserve model identity.** Never delete and recreate unrelated or unchanged elements. Maintain stable cross-runtime logical IDs and provenance mappings.
5. **Incremental by default.** Read, recompile, execute and validate only the smallest safe impacted dependency closure; escalate when global constraints require it.
6. **Document-as-code.** Views, sections, annotations, tags, dimensions, schedules, legends and sheets are dependent project outputs, not optional afterthoughts.
7. **Full traceability.** Every nontrivial parameter and decision has provenance, confidence, governing skill/rule, approval state and affected deliveries.
8. **Independent quality gates.** A successful API call is not a successful building. Verify geometry, BIM semantics, documents, coordination, technical and regulatory requirements, visual consistency and export roundtrips.
9. **Safe and reversible.** Typed error results, backups, recovery, idempotency, dry-run, preflight, checkpoints, transaction boundaries, bounded autocorrection and human escalation.
10. **Open, multilingual and jurisdiction-aware.** IFC, IDS, BCF, bSDD, ISO 19650-informed information management, source-backed regulation packs, regional measurement conventions and classification systems. No universal invented code rules.
11. **Skills are portable and composable.** Styles, project typologies, disciplines, methodologies, geography, firms and personal preferences are text-first plug-ins with executable resources, tests and priority resolution.
12. **Truthful implementation evidence.** Statuses must distinguish described, discovered, compiled, unit-tested, integration-tested, verified in running Revit, deprecated, unsupported, and manual-only.

---

## 2. COMPETITIVE REFERENCE ARCHITECTURE: LEARN, DO NOT CLONE

Inspect official documentation, licenses and example repositories before choosing components. Use the following as design benchmarks, with current capabilities reverified at implementation time:

- **Autodesk Revit API + Revit Public MCP + Autodesk Assistant + APS Automation**: native authoring, model context, cloud jobs, but public MCP currently exposes a finite supported toolset rather than whole desktop parity.
- **SWAPP / Frank, Pirros / Mira, EvolveLAB Glyph**: production intelligence, firm workflows, modeling/documentation automation, repeatable skills, QA and native Revit workflows.
- **Snaptrude, Finch, TestFit, Hypar, qbiq, ArkDesign, Arcol**: fast briefing, programming, options, building feasibility, high-level generative design and architect-friendly interaction.
- **Rhino/Grasshopper/Rhino.Inside.Revit**: free-form NURBS, computational geometry, panelization and native Revit interaction.
- **Blender + Python/Geometry Nodes + Bonsai + IfcOpenShell**: programmable form-making, rich meshes, open native IFC authoring, independent BIM inspection, QA and headless workflows.
- **BricsCAD BIM**: automatic classification and propagating a recognized detail/pattern to analogous project situations.
- **Speckle, Archicad, Vectorworks, Tekla**: interoperable data flow and other domain-specific modeling platforms.
- **Veras and comparable visualization tools**: reference strength, controlled reinterpretation, fast concept variation — but renders are not construction BIM.
- **BIMgent, Sketch2BIM, BIMScript and structured CAD/BIM generation research**: separate perception, typed intermediate representation, program synthesis, validation, and human feedback.

**Required competitive differentiator:** join the speed of early-design tools with the fidelity of mature BIM production, under a persistent semantic project memory and an incremental architectural compiler. Never assume a competitor cannot do a feature without reviewing contemporary documentation; track findings and evidence in `docs/research/competitive-landscape.md`.

---

## 3. PRIORITY ORDER AND ANTI-OVERENGINEERING RULE

This is a very ambitious multi-year platform. Start with a **small, real, end-to-end vertical slice**. Build the interfaces around that working kernel. Do not spend months defining thousands of commands before one room can be created and revised in Revit. Do not build ten mock microservices as a substitute for production functionality.

Priority order:

1. Verified Revit-native execution with transactions and robust query/CRUD primitives.
2. Consistent BIM-IR, semantic IDs, live model mirror and bidirectional synchronization.
3. Incremental semantic diff + dependency closure + batching + rollback.
4. Minimal professional documentation, regenerated on revision.
5. Skill loader, activation, conflict resolver and style/typology-specific recipes.
6. Vector CAD/PDF intake and structured architectural requirements.
7. Automated high-level architectural revisions and specialist solvers.
8. Blender, Rhino and IFC runtimes, first where they unlock specific tested needs.
9. Image/renders/biomimetic inspiration and generative complex projects.
10. Desktop parity census expansion, global standards, collaboration, enterprise scaling.

However, design module contracts early enough that later capabilities do not require replacing the core.

---

## 4. FUNDAMENTAL DATAFLOW AND CORE ENGINES

Use this execution flow:

```text
Prompts + DWG/DXF + PDF + images/renders + survey + brief + BIM + Skills
                               |
                     UNIVERSAL INPUT ENGINE
                               |
             REQUIREMENTS / PROVENANCE / REFERENCE GRAPH
                               |
                     DESIGN INTENT GRAPH
                               |
                 SEMANTIC BUILDING GRAPH + BIM-IR
                               |
              CONSTRAINT + GENERATIVE DESIGN KERNELS
                               |
                    INCREMENTAL COMPILER
                  /            |             \
          dependency diff   batch planner   QA preflight
                  \            |             /
                         EXECUTION PLAN
                               |
                    MULTI-RUNTIME SCHEDULER
                   /       |       |         \
             Revit API   Rhino   Blender   IFC/OpenBIM
                   \       |       |         /
                  SYNC / INDEPENDENT VALIDATION
                               |
              DOCUMENTATION COMPILER + DELIVERABLES
                               |
               PREVIEW / REPORT / ARCHITECT APPROVAL
```

Implement these as modules with typed contracts. Avoid hidden coupling where the agent's prompt is the only place a vital project rule lives.

### Core systems

- **Project Constitution**: project brief, objectives, site, boundaries, coordinate reference system, phase, jurisdiction, units, output contract, design constraints, active skills, template, library paths, permissions, autonomy level, risk policy.
- **Project Knowledge Base**: source documents with immutable IDs and version history, extracted requirements, design decisions, constraints, assumptions, accepted/rejected options, validation results, user feedback, and semantic project branches.
- **Design Intent Graph**: what the architect wants, where, why, invariants that may not change, hard constraints and soft objectives, dependencies and decision history.
- **Semantic Building Graph**: physical/spatial/technical/doc entities and relations; compute dependency closure and design impact.
- **BIM Intermediate Representation (BIM-IR)**: typed geometry/semantics/units/relationships with stable logical IDs and adapters for each target runtime.
- **Constraint Solver**: deterministic geometry and layout feasibility; distinguish optimization scores from absolute constraints.
- **Incremental Compiler**: intended-state diff, minimal safe patch, dependency-aware scheduling, native batch commands, efficient regeneration.
- **Universal Capability Registry**: discoverable low-level capabilities, coverage inventory, adapter selection, parameter schemas, UI workflow coverage, tests and limitations.
- **Documentation Compiler**: native plans, elevations, sections, dimensions, tags, annotations, schedules, sheets, indexes, revisions, exports.
- **Validation Engine**: independent checks and targeted repairs; evidence, severity, scope and approval gates.
- **Skill Runtime and Learning Engine**: portable procedural expert knowledge and professionally reviewed rules extracted from demonstrations/projects.
- **Interfaces**: CLI, MCP, SDK, optional Python/C# API, REST service, Revit dockable pane, web project console.

---

## 5. UNIVERSAL REVIT DESKTOP PARITY: 1:1 OUTCOME COVERAGE

**Parity means:** for a specific built-in, auditable Revit Desktop workflow, a machine agent can achieve and verify the equivalent professional result, including options, parameters and relevant document state. It does **not** mean “one CLI command with the same name as a ribbon button.” UI-dependent outcomes must include their preconditions. Third-party plugins have their own registry and do not silently inflate the core Revit denominator.

Perform a systematic **capability census**, versioned per Revit release and installed feature set. Inventory File, Architecture, Structure, Steel, Systems/MEP, Insert, Annotate, Analyze, Massing & Site, Collaborate, View, Manage, Modify, contextual ribbons, Properties, Project Browser, Family Editor, design options, phases, coordinates, materials, family parameters, linked models, worksharing, construction modeling, analytical models, view templates, schedules, annotation, export, printing, dialog workflows and contextual editing.

For each capability produce a machine-readable record with:

```yaml
id: revit.architecture.wall.create
version_scope: ["2027.2"]
desktop_location: "Architecture > Wall"
workflow: "Create a native basic wall, including specified constraints"
contexts: ["project-document", "valid-level", "editable-workset"]
input_schema: "schemas/revit/wall-create.schema.json"
output_schema: "schemas/command-result.schema.json"
preconditions: []
side_effects: []
preferred_adapter: "revit-native-api"
fallbacks: ["postable-command", "ui-automation"]
coverage_status: "unverified"
parameter_coverage: []
limitations: []
verification_tests: ["tests/parity/revit/wall-create.test.yaml"]
```

Execution order: **supported native Revit API** → **supported command invocation (`PostableCommand`, when eligible and appropriate)** → **Microsoft UI Automation with semantic controls** → **vision-assisted desktop actions if needed** → **explicit unsupported/manual boundary**. Do not force a generic UI fallback to claim equivalence. Some operations cannot be reliably automated and must remain visible gaps until evidence proves otherwise. Respect Autodesk licensing and published interfaces; do not rely on unsupported private APIs or unauthorized reverse engineering.

Autogenerate `capabilities.json`, `REVIT_DESKTOP_PARITY_MATRIX.md` and coverage reports. Count discovered, implemented and **verified** capabilities separately. Record coverage by parameter, version, workflow and output semantics; keep the denominator fixed and auditable, including failures.

The native Revit runtime uses C# and the **exact .NET target supported by the installed Revit SDK** (initially investigate Revit 2027, including its relevant service pack). Implement `IExternalApplication`, `IExternalCommand` as appropriate, documented `ExternalEvent`/valid API contexts, a per-document scheduler and transaction discipline. Revit model writes are serialized correctly; preprocessing, geometry search and vision tasks run in isolated workers. Do not call Revit API objects from arbitrary background threads.

Use an in-process Revit Add-in and a **persistent out-of-process RPC bridge** (Windows Named Pipes or loopback gRPC after evaluating deployment and security constraints). The command registry, executor and schemas are shared across CLI, MCP and SDK; they must not each reimplement BIM logic.

Support real element CRUD; stable lookup via Revit `UniqueId`, mapping to logical UUID, family creation/loading/editing, parameters (including unit conversion and storage types), shared/project parameters, worksets, phases, design options, groups, links, levels, grids, systems, material bindings, views, sheets, annotations, schedules, exports and appropriate error handling. Add real integrations incrementally, never stubs displayed as verified.

For ambiguous Revit model states, closed documents, uneditable worksets, worksharing ownership, dialogs, command availability and API limitations, produce typed errors and suggested remediation, not silent no-ops.

---

## 6. BIM-IR AND SEMANTIC GRAPH: PRECISE DATA MODEL

Create a canonical, versioned BIM-IR with explicit:

- project/site/building/zone/storey/space/component/assembly/system/view/sheet hierarchy;
- geometry: 2D profiles, 3D solids, NURBS/surfaces references, mesh representations, coordinate frames, host relations, bounding boxes;
- BIM semantics and classification: category, type, family, parameters, system membership, material layers, material quantities, structural/MEP properties;
- spatial relationships: contains, adjacent_to, served_by, hosts, hosted_by, supported_by, connected_to, aligned_with, crossed_by;
- geometry constraints: dimensional, alignment, adjacency, minimum clearances, aspect ratios, net/gross area, volumes and accessibility;
- documentation and delivery relations: shown_in, annotated_by, scheduled_in, referenced_by, governed_by;
- per-value provenance, source units, uncertainty, tolerances and approval;
- target-backend bindings and lossiness flags;
- logical object lineage (split/merge/replace), user edits, source revision and version timestamps.

Use strong typing for **physical dimensions**: coordinates, lengths, areas, volumes, angles, geographic coordinates, arbitrary number, ratio, temperature etc. Convert to Revit internal units at a well-tested boundary. Never mix meters, feet, millimeters, degrees or radians implicitly. Respect project base point, survey point, true north, rotations and georeferencing.

Example relationships:

```text
corridor-L04
  constrained_by -> structural-core-A
  bounded_by -> walls-141..148
  serves -> apartments-401..408
  crossed_by -> duct-097
  dimensioned_by -> dimension-332
  represented_in -> plans-A104, A204
  sourced_from -> brief-v03.pdf#p12
  governed_by -> skill/residential@2.1
```

Do **not** claim that IFC is a byte-for-byte substitute for RVT. Preserve as much native Revit editability as possible and record fidelity losses during translation. An IFC verification pass is an additional check, not proof of full native equivalence.

---

## 7. LIVE MODEL MIRROR, EXTERNAL EDITS AND CONSISTENCY

Implement a model mirror outside Revit that stores only relevant queryable state. This avoids requerying millions of elements for each prompt. Mirror state must be versioned against native documents, not blindly treated as authoritative.

Required mechanisms:

- stable logical UUID ↔ Revit `UniqueId` / IFC `GlobalId` / other runtime identifier mappings;
- Revit element change and document event listeners where supported, incremental snapshots, reconciliation for edits made by humans, and explicit full-sync fallback;
- optimistic revision tokens, conflict detection, invalidated cache entries, worksharing state tracking;
- differentiation among project design intent, mirror state and actual execution state;
- safe handling of deleted/merged/split/recategorized native objects;
- a durable operation/event journal, restorable snapshots, checksums and migration/version compatibility.

**Source of truth policy:** approved architectural intent and requirements live in the Project Constitution/Design Intent Graph; actually realized geometry and Revit-specific editable properties must be reconciled with the current native model. Never assume one system entirely supersedes the other. For concurrent human/agent edits, detect divergence and present a semantic conflict rather than overwriting silently.

---

## 8. ULTRAFAST INCREMENTAL ARCHITECTURAL COMPILER

This is the **top engineering differentiator**. Given a prompt:

> “Increase the living areas of apartments A and B by 12%, preserve the facade and structural grid, keep corridor width, and update all affected documentation.”

The compiler must:

1. Parse intent into a validated typed AST/Design Intent IR.
2. Resolve semantic targets from the building graph.
3. Freeze explicitly approved invariants: facade, grids, MEP shafts, protected areas.
4. Find affected dependency closure, including host objects, openings, connected systems and drawings.
5. Run feasible geometric/architectural constraint solving outside Revit.
6. Compute alternative valid patches when multiple solutions exist.
7. Generate a **semantic diff** against the latest native model and identify minimum safe edits.
8. Run dry-run preconditions, risk classification and impact summary.
9. Compile batched idempotent low-level operations with a dependency DAG.
10. Execute through the runtime scheduler inside supported transaction contexts.
11. Read back relevant native state; verify identities and invariants.
12. Perform localized validation and, when needed, a broad coordination pass.
13. Update affected documentation, then check document consistency.
14. Report changed elements, unchanged elements, before/after visual diffs, issues and exact elapsed time.

Implement **dirty node propagation**, structural sharing of unchanged IR fragments, memoization of deterministic solver outputs, cached geometry, reusable design prototypes, batch queries, batch parameter writes, minimum necessary Revit regeneration, and lazy rendering.

Never rebuild an entire tower merely to move one corridor. Avoid a single monolithic transaction for every change; use tested transaction groups and checkpoints around risky edits. Avoid enormous transaction counts per wall; use compatible operation batching. Document tradeoffs of failure rollback and regeneration. Revit remains single-document-write scheduled; parallelize **planning and preprocessing**, not unsupported concurrent Revit writes.

Implement a **Preview Path** using fast geometric visualization of an intent before committing detailed Revit edits, with a fidelity label. Preview should feel interactive, but never represent approximate concept geometry as verified native BIM.

Instrument per revision:

```text
intent_parse_ms
semantic_query_ms
solver_ms
diff_ms
runtime_wait_ms
revit_execute_ms
revit_regeneration_ms
native_readback_ms
validation_ms
documentation_ms
total_ms
llm_calls
model_elements_read
model_elements_written
transaction_count
unintended_element_changes
```

Define reproducible benchmark datasets and compare incremental patching against a naive full-regeneration baseline. Record hardware, exact model size, installed Revit version, cold/warm cache, LLM and input. Never claim 10× or 100× without measurements.

---

## 9. COMMAND LAYERS, CLI, MCP, SDK AND RPC

All entry points share the **same typed application services**, authorization and capability registry:

**Level 0 — native parity primitive:** `revit.element.set-parameter`, `revit.wall.create`, `revit.view.create`, `revit.family.load`, `revit.sheet.place-view`, etc.

**Level 1 — BIM semantic action:** `bim.room.resize`, `bim.zone.reconfigure`, `bim.prototype.propagate`, `bim.facade.repanel`, `bim.documentation.rebuild-affected`.

**Level 2 — architectural intent:** `architecture.design-museum`, `architecture.optimize-circulation`, `architecture.reconfigure-residential-floor`, `project.apply-revision`, `project.create-delivery-package`.

High-level commands compile into lower layers. Agents should not need to scan tens of thousands of tools to act. Use dynamic capability discovery, schema searches, skill-specific small command sets and expressive intent calls. CLI is a good developer/agent interface, not the architectural engine itself.

Suggested CLI syntax:

```bash
bim runtime status
bim capabilities search "curtain walls"
bim revit parity audit --version 2027
bim project init --template ./templates/studio-template.yaml
bim project ingest ./references/*.dwg ./references/brief.pdf
bim skills resolve --project ./project.yaml
bim model query --filter 'level==L03 && category==room' --json
bim intent plan --text "Move this corridor 200 mm east, keep core fixed" --dry-run
bim intent execute --plan ./.bim/plans/plan-001.json --checkpoint
bim revision apply --text "Resize apartments A and B by 12%" --update-docs
bim model validate --scope affected --format json
bim project export --format rvt,ifc,pdf --delivery ./out
```

These are **interface examples, not preexisting commands**. Implement them and mark availability honestly.

Response contract (all CLI/MCP/SDK):

```json
{
  "operation_id": "UUID",
  "status": "succeeded|partial|blocked|failed|requires_approval",
  "capability_id": "project.apply-revision",
  "project_revision_before": "r12",
  "project_revision_after": "r13",
  "changed_logical_ids": [],
  "changed_native_ids": [],
  "preserved_invariants": [],
  "warnings": [],
  "issues": [],
  "verification": {"status": "passed|partial|failed|unverified"},
  "artifacts": [],
  "timing_ms": {},
  "rollback_checkpoint": "snapshot-ref"
}
```

Provide machine-readable JSON Schemas or equivalent typed contracts, stable error taxonomy, idempotency keys, job progress, cancellation points and resumability. Support workflows in JSON/YAML with DAG dependencies, typed variables, dry-run and checkpoints. Make the bridge authenticated and local by default; never expose an unauthenticated network API into an open Revit process.

MCP shall surface carefully grouped discoverable tools/resources/prompts; it should **not** be the implementation core. Provide CLI examples, generated SDKs and executable skill recipes from the same registry.

---

## 10. MULTIMODAL ARCHITECTURAL INGESTION

The architect may provide arbitrary combinations of:

- DWG, DXF, CAD layers, survey drawings, XRefs, vector PDF, raster PDF, scanned hand sketches;
- floor plans, sections, elevations, axonometric/orthographic/isometric diagrams, 3D perspective renders;
- photographs of existing buildings, construction, furniture, materials, natural structures, machines or landscapes;
- RVT, IFC, IFCZIP, 3DM, OBJ, FBX, glTF/GLB, STEP (via appropriate adapters), point clouds such as E57/LAS/LAZ/RCP when licensed/supportable;
- site/topographic survey, contour plans, DEM/GeoTIFF, GIS data, OSM/context imagery and environmental datasets;
- briefs, contracts, specifications, schedules, CSV/XLSX, client emails, moodboards, concept collages and product datasheets.

**Classification by evidentiary role** is mandatory:

`MEASURED_GEOMETRY`, `APPROVED_REQUIREMENT`, `TECHNICAL_REFERENCE`, `SITE_EVIDENCE`, `STYLE_REFERENCE`, `MATERIAL_REFERENCE`, `TOPOLOGICAL_REFERENCE`, `INSPIRATION_ONLY`, `UNKNOWN`.

Don't allow a beautiful render to override a dimensioned survey. Ingestion produces **claims with source references, units, confidence and tolerance**, not unlabelled guesses. Keep source alignment, rotation, projection and page transforms. Reconcile contradictory inputs and issue questions only when material.

Vector-first extraction for CAD/PDF: lines, polylines, blocks, layers, symbols, measurements, text, local coordinate systems, units and line styles. Detect scale before applying dimensional interpretation. Image-first only when vector information isn't available, combined with calibration references and geometric confidence. Distinguish annotated dimension text from geometry-derived estimates.

For axonometrics and images, reconstruct candidate 3D form, camera assumptions and scale uncertainty. Render-to-BIM must not invent hidden structural systems or fire ratings as fact. For site photography, separate visual inspiration from registered spatial measurement. For point clouds, use geometry/scan registration and uncertainty metadata.

Allow registered user corrections to update interpretation and reuse those corrections in a project-specific parser/skill.

---

## 11. BIOMIMETIC AND CREATIVE REFERENCE ENGINE

Accept design inspiration from **any object**, including a leaf, seashell, coral, insect wing, tree, crystal, rock, machine component, sculpture, traditional architectural precedent or abstract painting.

Never merely trace arbitrary photos into polygon meshes and call it architecture. Extract controllable **Design DNA**, including:

- symmetry, asymmetry, branching, hierarchy, path networks and connectivity;
- curvature, repeated modules, tessellation, fractality, radial/rhythmic structure;
- porosity, density gradients, anisotropy, material quality;
- spatial sequencing, daylight patterns, structural analogy and constructibility potential.

Create explicit operators that map inspiration to architectural strategies, e.g.:

`leaf image → vein graph → branching rule → structural rib alternatives → parametric roof → appropriate BIM objects and facade/roof documentation`.

Support three modes: **faithful reconstruction**, **context-constrained adaptation**, **abstract inspiration**. Provide independently controlled visual, geometric, topological, structural and material similarity, as well as degree of creative abstraction. Track where an analogy is only visual and where structural validity has actually been computed. Offer several candidates with hard constraints protected.

Use specialized geometric engines for heavy free-form work; render preview and validate conversion fidelity before producing construction deliverables.

---

## 12. CONSTRAINT SOLVING AND SPECIALIZED GENERATIVE KERNELS

Do not ask an LLM to calculate every vertex or lay out every apartment. Build deterministic/spatial optimization systems supporting:

- geometric constraints: length, area, volume, angles, alignment, parallel/perpendicular/tangent/coincident, symmetry, module, grid, clearance;
- program: room inventory, minimum/maximum area, adjacency, separation, circulation, accessibility and capacities;
- performance: daylight, solar exposure, circulation efficiency, views, facade-to-floor ratio, energy concepts, cost/carbon targets when validated tools are available;
- construction: structural spans, grid feasibility, MEP shafts, openings, envelope build-up, repetition, constructibility and fabrication limits;
- site: setbacks, shape, levels, trees, topographic slope, circulation and contextual relationships subject to verified local sources.

Mark `HARD` requirements separately from `SOFT` goals. No weighted sum may override an inviolable hard constraint. Record infeasible constraint subsets and propose ranked alternatives. Before regulatory design checks, ensure the local jurisdiction pack and source edition are approved and current.

Implement extensible typed kernels (as architecture, not fabricated readiness):

`SpacePlanning`, `Circulation`, `VerticalCore`, `Residential`, `Hotel`, `Office`, `Hospital`, `Museum`, `Stadium`, `Airport`, `School`, `Parking`, `Facade`, `Panelization`, `Roof`, `StructuralGrid`, `MEPRouting`, `Terrain`, `Landscape`, `PublicRealm`, `DocumentationLayout`, `FreeFormGeometry`.

Each kernel requires explicit input/output schemas, constraint contracts, repeatable seeds, deterministic tests, performance benchmarks, quality scores and applicable skill hooks. Start with one residential planning kernel and one facade/roof experimental kernel. Do not generate placeholder kernels that claim support for every building typology.

Support design exploration via branches, parameter sweeps, Pareto alternatives and comparing hard-constraint compliance, costs and visual goals. Preserve approved user choices and permit selective semantic merge; **do not merge binary RVT files as Git text**.

---

## 13. PORTABLE ARCHITECTURAL SKILLS

**This is a first-class product subsystem, not prompt decoration.** Implement a directory-based format aligned with the open Agent Skills `SKILL.md` specification. Every skill has mandatory `SKILL.md` with YAML frontmatter (`name`, `description`) and instructions, plus optional supporting files:

```text
skills/styles/organic-architecture/
  SKILL.md
  design-rules.yaml
  constraints.yaml
  decision-policies.yaml
  references/
  examples/
  generators/
  validators/
  families/
  materials/
  templates/
  benchmarks/
```

Organize installable packages under:

```text
skills/
  core/
  styles/                   # minimalist, brutalist, art-deco, vernacular, organic...
  typologies/               # house, apartment-building, tower, stadium, museum, hospital...
  disciplines/              # architecture, structure, HVAC, electrical, landscape...
  methodologies/            # parametric, passive-design, adaptive-reuse, DfMA...
  jurisdictions/            # country/region/city, edition-qualified
  building-systems/         # mass timber, steel, concrete, masonry...
  phases/                   # feasibility, concept, SD, DD, CD, construction...
  studio-standards/         # office-specific BIM and design DNA
  clients/
  user-custom/
```

A professional skill can contain design philosophy, precedent selection, questions, constraints, geometry generators, BIM family/material libraries, documentation preferences, accessibility targets, templates, QA policies, and benchmark test cases.

**Skill activation:** discover by metadata, selectively load instructions and references, compose required packages into a per-project resolved rule set. Avoid loading thousands of skills into LLM context. Skills are versioned and dependency-pinned per project. Provide CLI for install, validate, enable, disable, test, author, fork, diff, upgrade and export.

**Conflict resolution precedence:** verified safety/legal requirements → signed contract/brief → approved project decisions → discipline/constructibility constraints → verified regulatory skills → firm BIM standards → project methodology → typology recommendations → stylistic preferences. Explicitly report conflicts, override origin and consequences; never allow style to silently violate accessibility or structure.

Treat any skill's scripts/assets as **untrusted supply chain components**: verify origin, signatures/hashes where feasible, version, permissions, license and script sandboxing. Don't execute arbitrary `SKILL.md` instructions as privileged OS commands.

Example project skill stack:

```yaml
active_skills:
  - typologies/museum@1.2
  - styles/organic-architecture@2.0
  - methodologies/passive-design@1.3
  - building-systems/steel@1.0
  - studio-standards/firm-a@3.1
  - jurisdictions/argentina/buenos-aires@2026-reviewed
```

The jurisdiction pack must carry exact sources, effective dates and professional approval status; the example versions are illustrative, not claims of legally valid codes.

---

## 14. SKILL LEARNING AND STUDIO DESIGN DNA

Use previous RVT/IFC projects, office BIM standards, family libraries, presentation sets, details, schedule conventions and **architect-corrected actions** to learn reusable practices. Do not call this “training a model” when it is actually creating rules, recipes, retrieval indexes and templates.

Implement learning-by-demonstration:

```text
human edit → capture semantic before/after → identify context/invariants
          → ask WHY when intent is ambiguous → propose generalized recipe
          → benchmark on held-out project examples → professional review
          → approved versioned skill
```

Classify feedback: incorrect tool operation; modeling error; preference; studio standard; project-specific exception; regulatory correction; workflow optimization. A single instance or random project exception must not be promoted automatically to a general style rule.

Produce an editable, inspectable **Studio Design DNA** skill: typical grids, circulation strategy, design ratios, detailing practices, facade patterns, material palette, Revit templates and view/sheet/naming standards. Retain provenance, permission boundaries and tenant isolation. Support opt-in learning, export, deletion, access revocation and restrictions on training outside a firm's project dataset.

---

## 15. DOCUMENTATION-AS-CODE: PROFESSIONAL DELIVERY IS THE PRODUCT

Represent the deliverable contract as a structured project artifact. Support phase-specific requirements for:

- floor plans, RCPs, roof plans, layouts, site plans, landscape plans;
- sections, elevations, interior elevations, details, assemblies, axonometrics, legends;
- tags, dimension strings, reference markers, notes, keynotes, material keynotes, annotations;
- room/door/window/material/quantity schedules, equipment listings, indexes, key plans;
- sheets, title blocks, viewports, view templates, scale, crop, browser organization, revisions, publishing and print/export;
- coordination issues (BCF), IFC/IDS checks, relevant metadata and audit report.

Use native Revit views, view templates, schedules, sheet objects and annotations when Revit is target. Keep clear conventions for annotation legibility, label collisions, line weights, hidden lines, scales and plot sizes. Build a document dependency graph so model revisions dirty precisely the affected output nodes. Handle annotation persistence and tagging/dimension reference stability when hosted geometry changes. Detect orphaned or missing annotations, broken references, overlapping notes and missing drawing views.

Track completion at **separate levels**: concept geometry, editable native BIM, specified parameters, structural coordination, MEP coordination, annotation, sheets, regulatory verification, professional review and issue closure. Do not declare a project complete simply because its massing exists. Human architects/engineers remain responsible for professional design and required sign-off.

---

## 16. VALIDATION AND BOUNDED AUTOCORRECTION

Create a validation engine **logically independent of the generator**, using typed rule definitions and independent checks wherever feasible:

1. geometric correctness and tolerances;
2. BIM categories, families, parameters, levels, rooms and host relations;
3. design constraints, accessibility, functional space program;
4. architectural consistency and protected design intent;
5. structural/MEP coordination and clash checks appropriate to available data;
6. material/layer/quantity and schedule consistency;
7. view/sheet/annotation/document package completeness;
8. visual before/after comparison and reference fidelity;
9. IFC export quality, IDS data requirements and roundtrip fidelity;
10. jurisdiction-specific rules only when the pack is verified and applicable;
11. performance, model health, warnings and failure handling;
12. unintended changes to unrelated parts of the project.

Return severity, exact logical/native element IDs, requirement/rule/source, measured value, tolerance, confidence, suggested fix and evidence. Build targeted self-repair: cause → minimal safe patch → dry-run → native execution → readback → revalidate. Stop after configured attempt/time/impact budget. Never quietly “fix” an approved critical design decision or invent missing measurements.

Test against independent expert-reviewed golden projects and deliberately malformed models. Ensure no circular self-approval where the same LLM writes both the rule and its passing evaluation.

---

## 17. CLARIFICATIONS AND AUTONOMY POLICY

Ask the architect only what matters. Classify unknowns: `BLOCKING`, `CRITICAL_DECISION`, `HIGH_IMPACT`, `OPTIONAL`, `AESTHETIC`. Group questions into a concise decision panel, include suggested options and explain tradeoffs.

Four operating modes:

1. **Strict reconstruction:** adhere to documentary sources; do not invent undocumented geometry or attributes.
2. **Assisted professional design:** infer low-impact choices, request critical approvals.
3. **Autonomous concept design:** freely generate alternatives while preserving hard site/legal/brief constraints.
4. **Controlled production:** no change to signed-off parameters, structure or deliverable requirements without explicit approval; every operation audited.

Preserve explicit assumption status: `measured`, `specified`, `approved`, `inferred`, `proposed`, `unknown`, `rejected`. Every open issue can be revisited. Present preview + impact report before executing high-risk edits, but don't block trivial reversible operations with repetitive approvals.

---

## 18. MULTI-RUNTIME EXECUTION, INTEROPERABILITY AND LICENSING

### Revit Native Runtime

Windows-hosted C# add-in, native API, external scheduler, persistent RPC, robust families/views/parameters/transactions, revision-aware readback. Native RVT deliverables must be produced and verified using an actual authorized Revit instance or supported Autodesk service, not a fabricated binary format.

### Rhino + Grasshopper Runtime

NURBS, free-form facades, shells, geometry constraints, panelization and generative experiments. Prefer Rhino.Inside.Revit when a tested flow creates true native Revit elements. Retain original 3DM/Grasshopper source. Respect Rhino licensing and Windows restrictions for the Revit integration.

### Blender Runtime

`bpy`, Geometry Nodes, mesh processing, procedural furniture, sculptural references, UVs, textures and visualization assets; export source assets. Convert to appropriate native Revit family when feasible. When semantic conversion is impossible, label the imported representation and its limitations.

### OpenBIM Runtime

IfcOpenShell Python/C++ and Bonsai support IFC-native modeling, inspection, validation, conversion, headless operations and cross-checking of Revit exports. Preserve `GlobalId`, classification, properties and relationships. Use IFC 4.3 and other specific schema versions only when supported and required; record unsupported constructs and export profiles.

### Future adapters

Archicad official APIs/MCP, BricsCAD BIM APIs, Vectorworks Python/SDK and Tekla Open API; prioritize based on customer demand and demonstrated feature-specific value. Do not falsely market full equivalence across runtimes.

### Data exchange

Adopt interoperable schemas, stable identifiers, geometry/metadata separation, field-level loss reports, transform and unit tests, and error budgets. Investigate Speckle or similar as an optional data layer rather than hardwiring the core to any vendor.

### Optional specialist services

Site/GIS tools; daylight/thermal/energy via appropriate verified calculation tools; cost/carbon quantities; raster/vision reconstruction; structural evaluation; visualization engines and renderers. No integration without license, trust and fidelity review.

---

## 19. GLOBAL ARCHITECTURAL USE

Support international teams and clients via locale-aware units, classification and nomenclature; translated commands and semantic synonyms; ISO/EN/local view annotations; schedules, naming schemes, drawing sheets and border conventions. Project location and governing jurisdiction must be explicit.

Each regulation rule must store authority, region, city, edition, effective dates, exact clause/source, scope, interpretation, confidence, test coverage and professional verification. Rule packs should be testable and revocable when a code changes. Sources of different authority must not be conflated. Do not assert compliance based only on an LLM answer.

Support project information management inspired by ISO 19650 where applicable, and openBIM exchange via IFC, IDS, BCF and bSDD. Implement tenant privacy, on-premise deployments where necessary, auditable revisions and access control.

---

## 20. SECURITY, RELIABILITY AND RECOVERY

- Treat CAD/PDF/documents, images, model strings, URLs and skills as untrusted input; prevent document-embedded prompt injection.
- Isolate arbitrary scripts, compiled plugins and generated code; apply permission profiles, signing/review, network/file restrictions and secret isolation.
- Never use unrestricted external code execution for a model edit when a typed existing operation exists.
- Protect Revit processes against request flooding, modal dialog deadlocks, cross-document confusion, reentrant API calls and invalid transaction nesting.
- Require backup/checkpoint before destructive operations, staged execution for high-risk operations, transactional rollback and recovery logs.
- Track a stable `operation_id` and idempotency keys to avoid duplicated wall creation after retries.
- Log all modifications, provenance, agent decisions, runtime versions, validation and human approvals.
- Account for worksharing and collaborative edits; do not overwrite remote changes.
- Inspect dependency licenses and restrictions before bundling libraries or redistributing plugins/models.

---

## 21. ENGINEERING STACK AND REPOSITORY

Use a pragmatic modular monorepo with clean boundaries. Validate installed compiler/runtime/SDK versions first instead of assuming them blindly. Proposed default:

- **Native Revit adapter:** C#, officially supported .NET for selected Revit release; signed/add-in manifest and controlled local bridge.
- **Core IR and compiler:** strongly typed implementation in C#/.NET for shared native objects OR Rust for a standalone cross-platform core; select one and document the tradeoff. Avoid splitting foundational semantic logic unnecessarily across languages.
- **AI/vision/IFC adapters and prototyping:** Python with typed Pydantic contracts where useful, IFC and geometry libraries, versioned dependencies.
- **RPC/transport:** local Named Pipes or gRPC and structured protobuf/JSON schema; version negotiation and durable jobs.
- **Persistence:** SQLite for single-machine prototype, PostgreSQL for collaboration; object/document store for references; optional vector index for semantic retrieval; graph relations can initially live in relational tables, not necessarily a dedicated graph DB.
- **Interfaces:** CLI + shared SDK + MCP server; Revit dockable pane and project console when the runtime is stable.
- **Observability:** OpenTelemetry-style structured traces or similar, metrics from day one; JSONL event logs for local development.
- **Testing:** deterministic units, Revit integration tests on Windows with a valid license, IFC export/roundtrip tests, skill tests, visual golden tests and realistic revision benchmarks.

Suggested repository layout:

```text
bim-agentic-os/
  README.md
  AGENTS.md
  .github/workflows/
  schemas/
  contracts/
  runtimes/
    revit-native/
    revit-desktop/
    revit-rpc-bridge/
    rhino/
    blender/
    openbim-ifcopenshell/
    adapters/
  core/
    project-constitution/
    bim-ir/
    semantic-graph/
    model-mirror/
    intent-ast/
    constraints/
    diff/
    dependency-graph/
    compiler/
    scheduler/
    capability-registry/
    project-versioning/
  intelligence/
    ingestion/
    cad-parser/
    vision/
    reference-engine/
    design-dna/
    memory/
    agent-orchestration/
  generative-kernels/
  documentation/
  validation/
  skills/
    core/
    styles/
    typologies/
    disciplines/
    methodologies/
    jurisdictions/
    studio-standards/
    user-custom/
  interfaces/
    cli/
    mcp/
    sdk/
    revit-panel/
    project-console/
  capabilities/
  recipes/
  workflows/
  templates/
  examples/
  tests/
    unit/
    integration/
    parity/
    semantic-revision/
    cross-runtime/
    documentation/
    security/
  benchmarks/
  docs/
    architecture/
    research/
    user-guides/
    api/
  scripts/
```

Document in `docs/architecture/adr/` all irreversible design decisions. Keep reproducible lockfiles and provide a one-command local test suite. Package Windows-native components separately from cross-platform services. The authoring agent may work on macOS/Linux for cross-platform modules, but **native Revit verification requires a supported Windows/Revit environment**.

---

## 22. REQUIRED CONFORMANCE TESTS AND BENCHMARKS

### Test A — Native editable room (FIRST milestone)

From CLI/MCP, create in a real Revit document: one level, a **5 m × 4 m room**, 200 mm walls, 3 m wall height, one 900 mm door, two windows, room/area boundary, dimensions, plan, 3D view, sheet and schedule. Verify real object categories, family hosting, parameter values, units, references and native RVT save. Reopen and inspect. Report actual execution timings.

### Test B — Incremental correction (SECOND milestone)

Starting from Test A, increase room width by 400 mm while retaining chosen exterior anchor, preserve unaffected IDs, move/rehost dependent objects where necessary, update plan dimensions and schedule, validate and produce semantic diff. Repeat the same request with an idempotency key; verify no duplicate geometry. Check rollback after forced failure.

### Test C — Apartment floor from DWG + brief

Ingest correctly dimensioned 2D CAD plus program PDF and facade render. Generate a native Revit apartment floor, with wall/door/window/floor/room objects, grids, core, views, dimensions, tags, schedules and sheets. Explicitly mark any geometry inferred from render. Apply: “Increase corner apartments by 12%, preserve facade, structural grid and corridor width; update drawings.” Prove minimum affected objects and preservation of unrelated IDs.

### Test D — Complex parametric/biomimetic structure

Input a photo of a leaf or shell. Ask for a museum roof inspired by branching topology rather than literal form. Extract controllable rules, offer 3 alternatives, pick one, generate via Rhino/Blender if suitable, create the strongest feasible BIM-native target representation, and report semantic/geometry conversion limitations. Validate documentation and assumptions.

### Test E — Existing professional Revit project

Open a representative multi-storey RVT with existing BIM conventions. Modify repeated hotel/residential prototypes on selected levels; preserve project organization, view templates, type/family assignments, worksets, unrelated objects and schedules. Log reconciliation with concurrent manual edits. Output full change impact report.

### Test F — Revit Desktop parity census

Audit UI/ribbon/context and published SDK by release, test representative operations across Architecture, Structure, MEP, families, annotation, views and collaboration. Publish inventory and verified coverage denominator with failed/unsupported evidence. Never suppress difficult commands from the registry.

### Test G — International and openBIM QA

Use two locale packs with distinct drawing/units conventions; output IFC and IDS validation reports, BCF issues and a matrix of representation losses. Do not call automatic checks full legal certification.

### Test H — Performance and resilience

On fixed project fixtures: 100, 1,000, 10,000 and larger elements as feasible. Measure full-build vs incremental revision times; p50/p95 latency, API calls, transactions, regeneration count, model size, cold/warm cache, memory, write failure recovery, unexpected changes and human interventions. Compare pre/post optimization with a reproducible environment.

For every benchmark produce an immutable case bundle containing inputs, code version, skill versions, environment, assertions, logs, screenshots/artifacts and metrics. Targets must be proposed as budgets and validated by measurement; do not invent success statistics.

---

## 23. DEVELOPMENT PHASES AND EXIT GATES

**Phase 0 — Real environment and research census.** Discover OS, Revit installation and exact version, SDK, licenses, API constraints, project templates, permissions and selected C#/.NET toolchain. Write evidence matrix. Exit: verified build path and initial honest parity registry.

**Phase 1 — Native execution kernel.** Implement in-process Revit add-in, safe scheduler, local RPC, native query, create/modify/delete, error handling, transaction/rollback, CLI and MCP using common schema. Exit: Test A against running Revit, with saved and reopened RVT. If Revit unavailable, unit-test core and explicitly mark native integration blocked, never fabricated.

**Phase 2 — Semantic state and mirror.** BIM-IR, physical units, logical IDs, model snapshots, external edit detection, event journal, graph relationships and provenance. Exit: native changes can be queried and roundtripped without semantic drift.

**Phase 3 — Incremental compiler.** Intent AST, dependency closure, deterministic geometry, diff, batch executor, idempotency, localized validation and rollback. Exit: Test B; show measurable delta versus a full rebuild.

**Phase 4 — Documentation engine.** Native views, dimensions, tags, schedules, sheets, view templates, affected-only regeneration. Exit: model change propagates correctly into drawings and quantities.

**Phase 5 — Skills and design systems.** Portable skill loader, conflict resolver, style/typology pack, design/QA recipes, skill testing/versioning. Exit: two contrasting style skills produce visibly distinct, traceable outputs while maintaining hard constraints.

**Phase 6 — CAD + requirements.** Native/vector CAD and PDF parsing, provenance, calibration, alignment and project brief extraction. Exit: Test C with documented uncertainty.

**Phase 7 — Specialist generative kernels.** Space planning, residential prototypes, circulation, facade; constraint search/optimization and alternative branches. Exit: nontrivial floor reconfiguration preserving approved invariants.

**Phase 8 — Rhino, Blender, IFC runtimes.** Use real tested adapter workflows; geometric inspiration operators, asset libraries and independent openBIM QA. Exit: Test D and one IFC validation roundtrip.

**Phase 9 — Extended Revit parity and collaboration.** Expand all Revit domain functions, UI fallback where reliable, families/MEP/worksharing, existing-model revisions, safe handoffs. Exit: Test E and F with tracked verified functional coverage.

**Phase 10 — Global production.** Jurisdiction skills, IDS/BCF/bSDD, collaboration, permissions, audit, customer privacy, enterprise packaging, performance optimization. Exit: Test G/H and reviewed deployment documentation.

Every phase has actual code, tests, demonstration, benchmark and known-gap report. A phase cannot pass from prose alone.

---

## 24. FIRST EXECUTION ORDER FOR THE CODING AGENT — DO THIS NOW

Do not stop at brainstorming. Begin work in the currently available repository/environment.

1. Inspect repository, OS, available Revit instances, SDK and build tools. Document what is present; do not assume Windows or a Revit license.
2. Compare and cite the most relevant official APIs, platform restrictions, and open-source tooling; make a build-vs-integrate decision briefly.
3. Create monorepo skeleton and `AGENTS.md` instructions, but keep the initial executable system small.
4. Define typed contracts for `Project`, `Element`, `Geometry`, `Units`, `Capability`, `Operation`, `ExecutionPlan`, `ValidationIssue`, `SemanticDiff` and `CommandResult`.
5. Implement a **real native execution slice** for Revit: runtime lifecycle, safe queue, transactions, typed command registry, native wall/level/opening CRUD, basic element inspect.
6. Implement persistent authenticated RPC and a CLI with structured JSON and idempotency. Expose identical operations through MCP.
7. Create first native fixture and tests from Test A. Verify via real Revit if available. Otherwise provide deterministic core tests and a clearly specified Windows validation procedure with `blocked` native status.
8. Implement semantic logical IDs, model mirror and intent-to-operation-plan for Test B.
9. Implement dry-run semantic diff, batch native execution, readback, rollback and incremental validation.
10. Produce actual technical artifacts in the repository, with runnable commands, exact installation instructions and a coverage dashboard showing what does/does not work.

At the end of each execution session report **only evidence-grounded** information:

```text
IMPLEMENTED:
TESTED AUTOMATICALLY:
VERIFIED IN NATIVE REVIT:
BLOCKED / WHY:
NEXT HIGHEST-IMPACT WORK ITEM:
BENCHMARKS / HARD NUMBERS:
KNOWN CAPABILITY GAPS:
FILES CHANGED:
EXACT COMMANDS TO REPRODUCE:
```

If the environment lacks Revit, **do not pretend to have tested it**. Build the cross-platform core, write a trustworthy native Windows test harness, and call out the limitation.

---

## 25. PRODUCT DEFINITION OF DONE

The product is achieving its mission when a professional architect can supply a site/survey, CAD plans, renders, technical program, reference imagery, a library of specialized typology/style skills, studio standards and target jurisdiction, and issue a prompt such as:

> “Design a 22,000 m² contemporary museum on this site. Consider these three precedents, this CAD survey, the studio's BIM standards and a photograph of a seashell as a **topological** rather than literal inspiration. Give me three alternatives. Prioritize clear visitor circulation, daylight control, flexible galleries and a strong park connection. After I select one, deliver editable Revit BIM, IFC and coordinated plans, elevations, sections, schedules and sheets. Ask me only about critical unresolved matters.”

The system must ingest the evidence; preserve provenance; reason across its project knowledge; produce alternatives with hard constraints protected; obtain high-impact approvals; compile genuine editable native BIM; independently validate it; generate professional documentation; and continue responding to complex corrections with **localized incremental changes** rather than rebuilding the entire project.

**Final directive:** You are building an **Architectural Operating System and Incremental BIM Compiler**. Revit is the deepest first production backend; CLI and MCP are interfaces; geometry engines are specialized workers; skills contain reusable architectural expertise; the semantic model and deterministic compiler enable fast revisions; independent QA and documentation turn geometry into professional delivery.

**Build it. Measure it. Verify it. Never fake parity. Optimize for the architect's next correction.**

---

## 26. PRIMARY REFERENCE LINKS (VERIFY VERSIONS BEFORE IMPLEMENTING)

- Autodesk Revit API guide: https://help.autodesk.com/cloudhelp/2027/ENU/Revit-API/files/Revit_API_Developers_Guide/Revit_API_Revit_API_Developers_Guide_html.html
- Autodesk Revit Public MCP: https://help.autodesk.com/view/ADSKMCP/ENU/?guid=ADSKMCP_RevitMcp_revit_mcp_server_html
- Autodesk APS Revit/API automation: https://aps.autodesk.com/apis-and-services/revit-automation-api
- Rhino.Inside.Revit: https://www.rhino3d.com/inside/revit/
- Blender Python: https://docs.blender.org/api/current/
- Bonsai native IFC: https://bonsaibim.org/studio.html
- IfcOpenShell Python: https://docs.ifcopenshell.org/
- buildingSMART IFC: https://standards.buildingsmart.org/IFC/RELEASE/IFC4_3/
- buildingSMART IDS: https://www.buildingsmart.org/standards/bsi-standards/information-delivery-specification-ids/
- buildingSMART BCF: https://www.buildingsmart.org/standards/bsi-standards/bim-collaboration-format/
- Agent Skills specification: https://github.com/agentskills/agentskills/blob/main/docs/specification.mdx
- SWAPP: https://swapp.ai/
- Pirros Mira: https://www.pirros.com/mira
- EvolveLAB Glyph: https://www.evolvelab.io/glyph
- Snaptrude: https://docs.snaptrude.com/
- Finch: https://www.finch3d.com/product
- TestFit: https://www.testfit.io/
- Hypar: https://docs.hypar.io/
- qbiq: https://www.qbiq.ai/
- Arcol: https://arcol.io/
- Speckle: https://speckle.systems/integrations/
- BricsCAD BIM: https://help.bricsys.com/en-us/document/bricscad-bim/design-assistance/using-propagate
- Graphisoft Archicad API: https://archicadapi.graphisoft.com/
- BIMScript: https://arxiv.org/abs/2608.21447
- Sketch2BIM: https://arxiv.org/abs/2510.20838
- BIMgent: https://arxiv.org/abs/2506.07217

---

**IMPORTANT:** All features and tool paths in this implementation prompt describe the *target architecture and engineering obligations*. They do not imply that the proposed product, integrations, performance targets, or Revit functional parity have already been implemented or verified.
