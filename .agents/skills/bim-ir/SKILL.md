---
name: bim-ir
description: Change persistent BIM-IR, semantic identity, provenance, units or mirror synchronization contracts.
---

# bim-ir

Keep intended and realized state separate. Use logical UUIDs and explicit metre coordinates; convert to native units at the adapter. Preserve provenance and revision tokens. Before changing a serialized record, add roundtrip, malformed-input and migration tests. Dependencies point from output/dependent to prerequisite. A mirror needs document identity, UniqueId bindings and readback revision; an intended-state commit alone does not create it. See core/Bim.Core and docs/contracts/module-contracts.md.
