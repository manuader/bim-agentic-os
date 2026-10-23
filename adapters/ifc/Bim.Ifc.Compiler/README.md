# Existing Core compiler to IFC adapter

This bounded adapter consumes the unchanged Project/Element/ExecutionPlan and RoomCompiler records. `IfcCompiler.Export` verifies the exact deterministic `Compiler.Diff`, binds raw System.Text.Json snapshot bytes to its existing SHA256 hashes, and emits adapter protocol version1. `CommittedAfterJson` is serialized by C# with Revision incremented once; Python validates that only Revision differs and hashes these raw bytes. It never attempts to reproduce C# numeric JSON canonicalization.

Supported inputs are canonical RoomCompiler rectangular rooms, with level/four walls/space and width-only updates. Stable logical UUIDs use RoomCompiler.Id. Plan/schedule/sheet remain explicitly unsupported outputs; door/two windows are fixed evaluation extras managed by the adapter and are not represented as Core-authored elements. Current fixture constraints require 200mm walls, width/depth at least3m and height at least2.1m. This is not general BIM-IR execution.

Core wall axes are centre lines. The deterministic join policy gives horizontal walls the corners and vertical walls the span between inner faces. Body coordinates derive from the compiled space rectangle and all centre axes, dimensions, dependencies, provenance and level are validated against it. IFC length units are millimetres; Core units remain metres.

CLI: `ifc-export before.json after.json plan.json room-guid operation-id` emits the same typed packet used by default `evaluate ifc`. Explicit packet evaluation accepts `evaluate ifc python worker output-directory packet.json`. Worker CLI requires `--request packet.json --output-dir absolute-directory`. The packet is saved as compiled-request.json with native bindings reported separately from evaluation role labels.

Lowlevel `resize_compiled` supports byte-identical no-ops, latest-operation retries, rejected conflicting receipts, consecutive corrections, stale-plan rejection and staged rollback. Replaying a historical operation after a later correction is explicitly rejected without writes; arbitrary historical retry semantics are unsupported. The A/B harness requires an actual correction and rejects no-op packets before creating output. The fixed C# evaluation oracle remains5x4→5.4x4; separate Core-to-IFC integration tests exercise6x4→6.4x4 and5→5.4→5.8 with stable native identities.

No native Revit result or complete product acceptance is implied.
