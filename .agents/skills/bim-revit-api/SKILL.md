---
name: bim-revit-api
description: Implement or review native Revit API operations and their Windows integration tests in BIM Agentic OS.
---

# bim-revit-api

Use the installed DLL/SDK version, currently 25.2, as evidence; never infer 2027 support. Run scripts/check-native-source.ps1 only as a source check, and the SDK build separately. Writes run through ExternalEvent and serialized transaction groups. Preflight exact document identity, editability, hosting and family types. Read back categories, UniqueIds, geometry and document outputs before declaring validation. Use an isolated saved fixture, never Casa Angel. Native retries require durable mapping/idempotency; preserve unrelated IDs. Record rollback and reopen evidence. See docs/contracts/module-contracts.md and tests/integration/README.md.
