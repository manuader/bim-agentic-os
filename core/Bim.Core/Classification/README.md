# Offline classification dictionaries

Checked-in, versioned dictionaries read by `ClassificationDictionaryParser` (`core/Bim.Core/Classification.cs`). Every `*.json` here is embedded in `Bim.Core` as resource `Bim.Core.Classification.<id>.json` and loaded with `LoadBuiltIn(<id>)`; `LoadFile` reads any other file through the same bounded, strict path. Nothing is fetched at run time. Format `bim.classification-dictionary/1`; the contract is in [module contracts](../../../docs/contracts/module-contracts.md#offline-classification-mapping-p10-05).

## `uniclass2015-ef-subset.json`

- Source: Uniclass 2015, Elements/functions (EF) table v1.16 (July 2026), https://uniclass.thenbs.com, retrieved 2026-11-03.
- Publisher and owner: NBS; the site footer reads "(c) Hubexo North UK Ltd 2026".
- Licence: the download page states Uniclass is licensed under Creative Commons Attribution-NoDerivatives 4.0 International (CC BY-ND 4.0) and asks users not to change the tables or add their own codes.
- What is copied: four codes with their titles (EF_25_10 Walls, EF_25_30_25 Doors, EF_25_30_97 Windows, EF_30_20 Floors), verbatim and unmodified, with attribution in the file's `licence` field. No other Uniclass content is included.
- Not legally reviewed: whether a verbatim excerpt counts as permitted sharing under BY-ND. Verify the licence before redistributing this file outside the repository.
- The kind-to-code choice (for example BIM-IR `wall` to EF_25_10) is this repository's mapping, not an NBS statement. BIM-IR elements carry no type or external/internal flag, so rules are per kind only.
- `level` and `space` are declared unmapped on purpose (see the file's `unmapped` notes), reason `excluded`.
- Kinds with no rule at all, reported as unmapped with reason `no_rule`: `plan`, `schedule`, `sheet`, `room`, `opening` and any other kind.
- `edition_date` is omitted because NBS publishes only a month (July 2026) for EF v1.16, and IFC4 `IfcDate` needs a full YYYY-MM-DD; the month stays in `edition`, and `EditionDate` is null.
- Provided as is, without warranty of any kind; NBS does not endorse this mapping.

OmniClass is not included: the codes could not be checked against a current published table, so no second real dictionary is shipped. Multiple dictionaries are exercised in `tests/classification` with a synthetic one.
