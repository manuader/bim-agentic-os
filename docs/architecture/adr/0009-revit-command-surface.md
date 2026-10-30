# ADR0009 — General Revit command surface (catalogue + guarded free path)

Status: accepted. The owner decided in chat on 2026-10-30 (America/Buenos_Aires); the answers are recorded in the run's OWNER.md.

## Context

The cycle so far proved the verification machine and one rectangular room end to end (native Test A/B verified in Revit 2025, IFC on three OSes). The owner's product goal is broader: ask Claude in plain language ("hacé un muro curvo") and have Revit do it, across architecture, structure, families, parameters and documentation. The existing pieces already carry most of the risk: the same-user named pipe, ExternalEvent serialization, transaction groups with rollback, persistent receipts and readback ([module contracts](../../contracts/module-contracts.md)), and the MCP server, which is how Claude reaches the CLI services.

This ADR supersedes CYCLE_SCOPE §1's single-room limit for this work. It does not authorize BIM-IR schema changes; those keep needing their own ADR.

## Decision

1. **Command catalogue.** Typed, versioned Revit commands, each with:
   - a JSON contract that can be tested without Autodesk;
   - preflight validation;
   - one transaction (group);
   - a readback of what actually changed;
   - a receipt keyed by `operation_id`, so a retry does not duplicate.

   Read-only queries (levels, wall/floor types, loaded family symbols, element parameters) come first, because the model needs them to turn a sentence into a command. The first write priority is architecture: straight and curved walls, doors and windows on host walls, floors, rooms; then roofs, structure, families/parameters and documentation.
2. **Guarded free path.** For a request the catalogue does not cover, Claude may submit C# that the add-in compiles against the installed Revit API and runs:
   - inside a transaction group that is rolled back unless the owner confirms;
   - with a report of created, modified and deleted elements;
   - with a time limit;
   - with no file, network or process access beyond the document.

   Its design gets its own short ADR before code (compiler choice, sandboxing limits, logging), because it is the riskiest piece.
3. **Copies first.** Every write command refuses any document that is not a registered test fixture or an explicit copy under a test directory, until the owner records in OWNER.md that a named real project is enabled. Production documents stay read-only.
4. **Claude is the language layer.** MCP tools are generated from the catalogue. Natural-language understanding is Claude choosing tools; there is no separate parser.
5. **Build-only Revit references in CI.** `Nice3point.Revit.Api.RevitAPI` / `RevitAPIUI` (NuGet, MIT package licence) are referenced only when the installed Revit is absent, with `ExcludeAssets=runtime` and `PrivateAssets=all`, so CI on windows-latest can compile and analyze the add-in. Nothing from them is copied to output or distributed. The assemblies inside are Autodesk's and the package does not state their terms; the owner accepted this for build-time use. Local builds keep using the installed Revit.

## Consequences

Each catalogue command is `unit-tested` (contract and guards) in CI and becomes `verified-in-revit` only after an owner-attended session runs it on a fixture (`scripts/owner-native-session.ps1` will grow a catalogue smoke). Unsigned add-in Load Once stays a manual gate per Revit launch. Concurrent native writes stay forbidden.
