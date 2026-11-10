# ADR0010 — Provenance records for adopted drift

Status: proposed (relay176, 2026-11-10). Needs the owner's decision before P2-11 can be built. It changes the persisted provenance ledger format, so AGENTS.md requires approval.

## Context

P2-11 commits a P2-09 drift selection ("adopt the external Revit edit into Core") as one Core revision and must give every adopted element a P2-06 provenance entry with source "external edit" and the drift proposal id. The P2-06 ledger (`core/Bim.Core/Provenance.cs`, `ProvenanceLedger.cs`; contract in [module contracts](../../contracts/module-contracts.md), P2-06) cannot record that today:

1. **Update adopts.** The only external-edit kind, `external_edit`, requires `element_id == MirrorSnapshotMapper.Id(document, unique_id)` and `Append` refuses anything else (`provenance_record_invalid@element_id`). An adopted update keeps the committed Core element id, which equals that formula only when the element first came from the mirror. Elements authored in Core and linked to Revit through the map would always be refused, and those are the main case for adopt.
2. **Delete adopts.** `change` accepts only `added` or `modified`.
3. **Proposal id.** The ledger has no member for it. Proposals keyed `core:<id>` have no UniqueId at all.
4. **Journal fields.** `journal_sequence` and `recorded_at` are required; the selection does not carry them (solvable without a format change by passing the `MirrorJournalEvidence` entries).

Only create adopts (extra elements, whose id is the snapshot id) fit the current format.

## Options

- **A (recommended).** Add a fifth ledger kind `drift_adopt` with `document_unique_id`, `proposal_id`, `core_revision`, `action` (`create`/`update`/`delete`), optional `unique_id`, and the journal evidence fields. Replay key: element id + proposal id + core revision. Existing kinds and their bytes are unchanged; the loader's closed kind set grows by one, so older readers refuse ledgers that contain the new kind.
- **B.** Loosen `external_edit`: accept element ids mapped through the Core map and add a `deleted` change value. Changes which stored records are valid; no proposal id.
- **C.** Add an optional `proposal_id` member to `external_edit`. Changes canonical bytes and loading, and alone does not fix point 1.
- **Defer.** P2-11 stays blocked; P2-12 (commit without provenance) still ships.

## Consequences

With A, P2-11 writes one `drift_adopt` record per adopted element in the same call that commits the revision; the ledger is validated on a copy (`Load(Serialize())`) before anything is written. Without a decision, adopted drift is committed (P2-12) but not traced in the ledger.
