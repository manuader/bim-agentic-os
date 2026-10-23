# ADR 0003 — Guarded native saved checkpoint

Status: accepted for the isolated level/wall development slice.

The master specification requires a real saved/reopened RVT, not merely successful in-memory transactions. The existing ExtensibleStorage mapping and receipts have passed current-session retry and rollback; disk persistence was initially unverified. The real saved/reopened primitive probe has now passed; see artifacts/native-checkpoint-result.json.

Revit 2025 installed API documentation states that Document.Close cannot close the active document, and OpenAndActivateDocument requires no open transaction. The checkpoint therefore verifies an existing receipt without executing operations, saves the opted-in fixture, creates a new owned parking document with no overwrite, activates it, closes the fixture, reopens it and reloads native state for independent geometry/identity/receipt checks. It then closes only the now-inactive owned parking document.

Exact fixture path, ProjectInformation identity, process-specific endpoint, explicit child-process opt-in and isolated document guards remain mandatory. Any failure reports its stage and retains files. No arbitrary existing document is closed or modified. Public evidence omits personal paths, identities and live process IDs; raw records remain local.

This establishes a limited saved primitive model only when the native probe passes. Doors, windows, Room, dimensions, views, schedule and sheet required by Test A are separate acceptance work.

Reference: [Autodesk developer explanation of deactivating before closing](https://blog.autodesk.io/deactivating-active-document-to-help-close-the-document-using-the-api/). The installed Revit25.2 XML is the target-specific contract; source documentation does not prove that a particular callback supports the complete workflow. Real ExternalEvent execution remains a required gate.

Execution outcome: the complete save/parking/close/reopen/readback path succeeded in ExternalEvent context on installed Revit25.2.0.38, preserving revision2, all five mappings and receipts. This evidence applies to the isolated primitive fixture, not arbitrary documents or other Revit releases.
