# Relay14 — durable store writes, native reconcile contract, bounded CLI inputs

The owner said "segui metiendole" while only N21 (owner-gated) was open. Relay14 took the free LOCK and loaded a batch from the known gaps, with no schema or persisted-format change. At most two implementation workers ran at a time. Every task had an independent read-only review before merge. The 3-OS GitHub CI was the oracle (no .NET SDK in this environment).

## Landed on main

- **D01 — durable ProjectStore writes.** The checkpoint is written to a temp file, flushed and atomically replaced. Journal appends are flushed. Bytes are unchanged and the goldens now pin BOM and newline bytes. Encoding happens before any file is opened. Crash-point tests cover every hook, including a held-open checkpoint on Windows (`core_io_error` through reconcile). Physical durability of flush/rename cannot be observed in a test and is not claimed.
- **N22 — native reconcile contract (ADR0007 A, Revit path).** An Autodesk-free decision core over the saved ExtensibleStorage payload. It replays only when Core is one revision behind and the snapshot, receipt and map all match. C8 unsaved evidence is refused before Core is read. The OneDrive guard moved into `Bim.Core.CloudGuard` and is shared with IFC; it scans before the read and again before Commit. The review found a real untyped throw on a non-numeric revision, plus missing map and typed-error checks; all were fixed with tests. The contract is not wired into `App.cs`. The live native run stays owner-gated, and so does the add-in build: the file compiles into Bim.Revit, which CI does not build.
- **S01 — bounded CLI inputs.** All 17 file reads go through `BoundedInput`. It enforces an 8 MiB cap (tied to the MCP cap), accepts only regular files, refuses links and Windows device namespaces, allows OneDrive placeholders and returns typed codes. Decoding matches `File.ReadAllText`, including invalid bytes becoming U+FFFD.
  - On Unix, the type check uses the runtime's own `SystemNative_LStat`/`FStat` shim. No public .NET 8 API reports the file type before open; the review evaluated the alternatives. A missing shim fails closed as `input_type_unverifiable`.
  - `tests/cli` now also runs on macOS.

## Limits

No Revit and no owner-machine run. Not covered: linux-arm64, real OneDrive placeholders, Windows junctions, the missing-shim path, and the Bim.Revit compile with the new native contract file (`scripts/check-native-source.ps1` needs Revit). Job logs were not readable here; job and step conclusions plus annotations are the evidence.
