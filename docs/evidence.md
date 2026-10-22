# Environment and evidence — 2026-10-22

Windows x64. Revit 2025 installed: RevitAPI.dll FileVersion 25.2.0.38, product 20240715_1515(x64). Live Revit PID 2544, Casa-Angel-piloto-documentacion open. Existing RevitMCP diagnostics returned `can_start_transaction=true`, `rollback_ok=true`. No fixture was authored and no native acceptance test passed.

Initial system dotnet: host 10.0.2; runtimes 6/8/9/10; no SDK. Python command resolves to unusable WindowsApps alias. Node 24.15.0. gh unavailable. Repository initially contained only the 60,009-byte master prompt, read in full. Git remote initially absent. `git ls-remote` succeeded with elevated network access and no refs printed (empty remote). Local default Git identity Ezequiel Ader.

Build and test results belong in `artifacts/`; absence of a result is unverified. No performance multiplier or native delivery latency measured.

Known gaps: complete Test A, durable native idempotency, readback validation, native mapping storage, live change listeners, cancellation/timeout queue semantics, bounded input streaming, MCP, SQLite migrations, journal recovery/power-loss tests, multi-document scheduler, full parity census, license audit of future dependencies. Current store uses files to keep the dependency-free core buildable; SQLite remains planned.

## Executed checks

`scripts/test-roslyn.ps1`: 12 passing checks, source compiled/executed with PowerShell host .NET 10.0.11 reference assemblies. This is NOT an SDK net8 build. `artifacts/core-tests-host.txt` records the run.

`scripts/check-native-source.ps1`: native source signature check against installed Revit API DLLs with host runtime references; CS1701 runtime identity warnings suppressed for this check only. Assembly NOT loaded into Revit; net8 build and runtime compatibility remain unverified.

`scripts/benchmark-roslyn.ps1`: synthetic 100/1,000/10,000 wall diffs, 20 samples each, exactly one emitted update. 10,000: p50 81.061 ms, p95 207.8414 ms. Includes first cold-JIT sample, no cache separation, no native execution or baseline speedup claim. Host 16 logical processors; runtime 10.0.11. See JSONL artifact.

Git publication attempted: GitHub HTTP 403, authenticated account `ezeader` denied write access to `manuader/bim-agentic-os`. Local commit exists; publication requires an authorized account or repository collaborator permission.

Official .NET SDK download attempted via installer and direct URL; direct download timed out and remained partial. Stopped incomplete download processes. No SDK installation claimed. Full target build remains pending.
