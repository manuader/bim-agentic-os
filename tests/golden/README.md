# Human architectural reference cases

No approved golden case exists yet. Synthetic compiler/integration fixtures are not architectural golden cases.

Copy `_template/` to a case directory and supply input documents/model, architectural intent, expected outcomes, invariants and explicit tolerances with physical units. An architect must approve the case before it becomes an oracle. Templates must never count as passing cases.

Verification state: manual-only. Optional proposed examples must carry review_state=draft-needs-review and must not be counted as approved. Do not put private RVT/family or client files in public Git; keep local references and provide sanitized input suitable for distribution.
