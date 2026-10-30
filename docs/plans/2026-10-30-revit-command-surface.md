# Revit command surface — plan

From [ADR0009](../architecture/adr/0009-revit-command-surface.md). Order matters: compile in CI, then the protocol and queries, then architecture writes, then the free path.

- **C01 CI compile of the add-in.** Conditional reference to Nice3point.Revit.Api 2025 when `$(RevitPath)` has no RevitAPI.dll; a `native-build` CI job on windows-latest builds `runtimes/revit-native` with `--warnaserror`. Done when that job is green on main and a local build with installed Revit is unchanged.
- **C02 Command protocol, catalogue v1 contracts and MCP tools (Autodesk-free).**
  - Request/response envelope: protocol version, operation id, document identity, command, typed args in millimetres and degrees, and a copies-only flag check.
  - Validators and the error-code set.
  - MCP tools generated from the catalogue: `revit_query_*`, `revit_wall_create` (line or arc), `revit_opening_place` (door/window), `revit_floor_create`, `revit_room_create`.
  - Each tool forwards to the existing `native` pipe path.
  - Done when guard tests cover every validator and the MCP tool list matches the catalogue, all in CI.
- **C03 Revit executor for catalogue v1** (needs C01, C02). Queries (levels, wall types, floor types, door/window family symbols), plus wall line/arc, door/window on a host wall, floor from a boundary, and a room at a point. Each command runs in one transaction group, with readback and a receipt. Done when it compiles in CI and the Autodesk-free logic is unit-tested.
- **C04 Owner smoke for catalogue v1** (owner, Load Once). In a new fixture: query, then create a curved wall, place a door, create a floor and a room. Readback each; retry without duplicating.
- **C05 Free-path ADR and implementation**, after C03.
- **Later batches:** roofs and stairs, structure (grids, levels, columns, beams, foundations), family loading and parameters, documentation (views, dimensions, tags, schedules, sheets).
