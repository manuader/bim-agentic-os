# Architecture map

The master specification defines the target product. This map describes the current small implementation, not the entire target platform.

`interfaces/cli -> core/Bim.Core` owns intended-state commands, deterministic compilation and validation. `runtimes/revit-native -> core contracts + Autodesk API` owns serialized model execution. The current primitive runtime began independently; the room slice joins typed intent and execution incrementally. Core may depend on the standard library, not Autodesk, CLI, transport or UI. Tests may reference core; native integration runs only inside supported Revit.

Persistence currently stores intended Project state, checkpoints and receipts in atomic file snapshots. It is not yet a complete versioned live model mirror. Do not treat an intent commit as proof that Revit agrees. The adapter must separately report document identity, realized bindings and readback.

Dependencies point from a dependent output to its prerequisite. Dirty propagation follows the reverse relation: changed wall -> affected plan -> sheet. The current diff scans the model; actual edits are incremental, but query/planning cost is not yet sublinear.

See [contracts](docs/contracts/module-contracts.md), [ADR 0001](docs/architecture/adr/0001-kernel.md), [harness ADR](docs/architecture/adr/0002-harness.md), [feature tracker](docs/features.json), [native acceptance procedure](tests/integration/README.md).
