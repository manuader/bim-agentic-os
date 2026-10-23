# Native saved checkpoint implementation plan

Goal: save and reopen the verified level/wall correction and prove native state, identities and retry receipt survived disk persistence. Product scope remains the master specification; no renewed discovery.

Architecture: an opt-in fixture-only checkpoint operation validates its existing batch receipt without applying operations. Save in supported API context, switch to a newly created owned parking document, close the fixture, reopen it and independently read state/geometry/receipt. Never close existing production documents. A new parking file must be inside the explicitly opted-in run directory, with no overwrite. Failure is reported honestly and files are retained for diagnosis.

- [x] TDD pure checkpoint authorization/path/receipt guards and negative cases.
- [x] Implement native checkpoint and integration probe with exact path, document identity and process endpoint guards.
- [x] Build net8 target, run deterministic suite and independent source review.
- [x] Launch a new isolated fixture, run primitive smoke and checkpoint/reopen integration serially.
- [x] Record redacted evidence, update feature ledger and publish; observe CI.

Ownership: checkpoint implementer owns runtime, pure guard tests and new native checkpoint probe in its managed worktree. Controller owns plan/evidence/status and all actual native execution. Reviewer is read-only. No parallel document mutations.

Ruling: use a test-only owned parking document to satisfy the Revit restriction on closing an active document; no production model is used as parking. The temporary manifest is removed after execution. Complete room/documentation Test A is still incomplete.

Independent work while native loading awaits user action: reject self-dependencies and multi-element dependency cycles in Compiler.Validate, preserving the existing dependency direction and deterministic diff behavior. Implement iterative traversal to handle long valid chains without recursion overflow. TDD red-green for self-cycle, two-node cycle, valid DAG and long chain; rejected intended-store commit must leave state unchanged. No Autodesk dependency or native writes. Separate worktree/source ownership and independent review are required.

Independent IR task complete: reviewed iterative cycle validation and console failure lifecycle integrated as d3b7707/2e30261. Controller full gate passed50 core+17 checkpoint checks and native target0warnings/errors. Native save/reopen remains blocked pending user resolution of the load prompt; no native success is inferred from these gates.

Native blocker resolved by user Load Once. New fixture smoke, rollback and save/reopen checkpoint passed; revision2, five native mappings, geometry and receipt were verified after reload. The RVT is actually saved. Complete Test A room/documentation remains the next product slice.
