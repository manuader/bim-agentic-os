# Current contract audit

No BIM-IR schema change is authorized by this audit. Existing model serialization is retained. These classifications identify tested scope and missing required contracts.

| Contract | State | Evidence / gap |
| --- | --- | --- |
| Project | unit-tested | core/Bim.Core/Model.cs; logical ID,revision,elements; current core test artifact |
| Element | unit-tested | stable ID,kind,double[] geometry,dependencies,provenance; arrays are prototype representation |
| Geometry | unsupported | no physical-shape typed contract; geometry lives in numeric arrays |
| Units | unit-tested | Length only validates finite metres and converts feet; other physical dimensions unsupported |
| Capability | described | docs/features.json is an evidence ledger, not a typed shared capability service |
| Operation | unit-tested | action+Element prototype; per-operation operation_id/durable native scheduling remains incomplete |
| ExecutionPlan | unit-tested | hashes/revision/ops/dirty IDs; level/wall native batch evidence is separate |
| ValidationIssue | unit-tested | Code,optional logicalId,Message; measured value/tolerance/severity/source contract incomplete |
| SemanticDiff | described | operations/dirty closure in ExecutionPlan; separate required typed SemanticDiff absent |
| CommandResult | described | minimal prototype record and legacy operation-status protocol; full shared service result contract absent |

Required expanded Geometry/Units/Capability/SemanticDiff contracts need a separately proposed schema ADR with migration/roundtrip/malformed-input tests and explicit user approval. Evaluation-only DTOs must not silently migrate model schemas. Existing raw operation receipts and imported CI JSON retain their original execution/protocol statuses as historical evidence; they are not current capability classifications. New evaluation reports separate verification_state (eight allowed states) from pass/fail outcome.
