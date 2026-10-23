# Native saved checkpoint implementation plan

Goal: save and reopen the verified level/wall correction and prove native state, identities and retry receipt survived disk persistence. Product scope remains the master specification; no renewed discovery.

Architecture: an opt-in fixture-only checkpoint operation validates its existing batch receipt without applying operations. Save in supported API context, switch to a newly created owned parking document, close the fixture, reopen it and independently read state/geometry/receipt. Never close existing production documents. A new parking file must be inside the explicitly opted-in run directory, with no overwrite. Failure is reported honestly and files are retained for diagnosis.

- [x] TDD pure checkpoint authorization/path/receipt guards and negative cases.
- [x] Implement native checkpoint and integration probe with exact path, document identity and process endpoint guards.
- [x] Build net8 target, run deterministic suite and independent source review.
- [ ] Launch a new isolated fixture, run primitive smoke and checkpoint/reopen integration serially.
- [ ] Record redacted evidence, update feature ledger and publish; observe CI.

Ownership: checkpoint implementer owns runtime, pure guard tests and new native checkpoint probe in its managed worktree. Controller owns plan/evidence/status and all actual native execution. Reviewer is read-only. No parallel document mutations.

Ruling: use a test-only owned parking document to satisfy the Revit restriction on closing an active document; no production model is used as parking. The temporary manifest is removed after execution. Complete room/documentation Test A is still incomplete.
