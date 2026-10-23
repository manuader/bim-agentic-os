# Native fixture execution

Goal: publish the existing implementation, observe real remote CI, and remove the missing native fixture/bootstrap barrier without touching production model.

Product authority remains the existing master specification. Previous harness slice is complete; no renewed discovery or architecture replacement.

- [x] Publish main635cc68 to requested GitHub remote after permission change; first CI run39874914458 started, core and CodeQL jobs passed.
- [x] Add an explicitly opt-in fixture bootstrap to the native add-in, scheduled in supported API context. Create/save/open a separate `BIM-Agentic-Test*` document from the installed metric template; write exact identity/path/PID/version evidence. Existing documents must never be modified or closed.
- [x] Build/package the add-in and create a guarded launcher for a separate Revit process with test-only environment. No automatic global startup execution; no destructive process cleanup; preserve production instance.
- [ ] Execute guarded native primitive smoke if loading/authorization succeeds; otherwise record exact blocker and continue verified independent work.
- [ ] Observe remote CI results, update feature/evidence status, independently review changes, verify and publish.

Ownership: native bootstrap worker owns `runtimes/revit-native` bootstrap/App changes and `tests/integration` launcher/tests only. Controller owns status/evidence/plan and Revit UI inspection. All Revit writes remain serialized; no worker performs native model writes. Security or licensing dialogs remain user-controlled.

Observed integration failures: the PowerShell built-in cli alias shadowed the smoke helper (fixed with a subprocess regression). The shared native pipe was owned by the production process; exact-document guard returned document_conflict before any geometry mutation. Process-specific endpoints are required before retry. Real fixture bootstrap succeeded in a dedicated process; this proves initial template create/save/open only.

