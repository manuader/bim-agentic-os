# Environment and evidence — 2026-10-22

Windows x64. Revit 2025 installed: RevitAPI.dll FileVersion 25.2.0.38, product 20240715_1515(x64). Live Revit PID 2544, Casa-Angel-piloto-documentacion open. Existing RevitMCP diagnostics returned `can_start_transaction=true`, `rollback_ok=true`. No fixture was authored and no native acceptance test passed.

Initial system dotnet: host 10.0.2; runtimes 6/8/9/10; no SDK. Python command resolves to unusable WindowsApps alias. Node 24.15.0. gh unavailable. Repository initially contained only the 60,009-byte master prompt, read in full. Git remote initially absent. `git ls-remote` succeeded with elevated network access and no refs printed (empty remote). Local default Git identity Ezequiel Ader.

Build and test results belong in `artifacts/`; absence of a result is unverified. No performance multiplier or native delivery latency measured.

Known gaps: complete Test A, durable native idempotency, readback validation, native mapping storage, live change listeners, cancellation/timeout queue semantics, bounded input streaming, MCP, SQLite migrations, journal crash recovery, multi-document scheduler, full parity census, license audit of future dependencies. Current store uses files to keep the dependency-free core buildable; SQLite remains planned.
