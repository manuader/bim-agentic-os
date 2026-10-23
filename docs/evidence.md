# Environment and evidence — 2026-10-22

Windows x64. Revit 2025 installed: RevitAPI.dll FileVersion 25.2.0.38, product 20240715_1515(x64). A production Revit document was open. Existing RevitMCP diagnostics returned `can_start_transaction=true`, `rollback_ok=true`. No fixture was authored and no native acceptance test passed.

Initial system dotnet: host 10.0.2; runtimes 6/8/9/10; no SDK. Python command resolves to unusable WindowsApps alias. Node 24.15.0. gh unavailable. Repository initially contained only the 60,009-byte master prompt, read in full. Git remote initially absent. `git ls-remote` succeeded with elevated network access and no refs printed (empty remote). Local default Git identity Ezequiel Ader.

Build and test results belong in `artifacts/`; absence of a result is unverified. No performance multiplier or native delivery latency measured.

Known gaps: complete Test A, durable native idempotency, readback validation, native mapping storage, live change listeners, cancellation/timeout queue semantics, bounded input streaming, MCP, SQLite migrations, journal recovery/power-loss tests, multi-document scheduler, full parity census, license audit of future dependencies. Current store uses files to keep the dependency-free core buildable; SQLite remains planned.

## Executed checks

`scripts/test-roslyn.ps1`: 12 passing checks, source compiled/executed with PowerShell host .NET 10.0.11 reference assemblies. This is NOT an SDK net8 build. `artifacts/core-tests-host.txt` records the run.

`scripts/check-native-source.ps1`: native source signature check against installed Revit API DLLs with host runtime references; CS1701 runtime identity warnings suppressed for this check only. Assembly NOT loaded into Revit; net8 build and runtime compatibility remain unverified.

`scripts/benchmark-roslyn.ps1`: synthetic 100/1,000/10,000 wall diffs, 20 samples each, exactly one emitted update. 10,000: p50 81.061 ms, p95 207.8414 ms. Includes first cold-JIT sample, no cache separation, no native execution or baseline speedup claim. Host 16 logical processors; runtime 10.0.11. See JSONL artifact.

Git publication attempted: GitHub HTTP 403, authenticated account the configured Git account denied write access to `manuader/bim-agentic-os`. Local commit exists; publication requires an authorized account or repository collaborator permission.

Official .NET SDK download attempted via installer and direct URL; direct download timed out and remained partial. Stopped incomplete download processes. No SDK installation claimed. Full target build remains pending.

## Harness session: target verification supersedes initial build blocker

The official archive download was resumed and completed. Local SDK8.0.415, MSBuild17.11.48, runtime8.0.21 are installed under ignored `.tools/dotnet`; Windows10.0.26200 x64. `scripts/verify.ps1 -Dotnet ./.tools/dotnet/dotnet.exe` passed target builds for core/CLI/tests/benchmark, 33 assertions, harness negative regressions and static gates. Logs are copied to `artifacts/` and summarized in `artifacts/verification-summary.json`. Host-only logs remain historical and separately labeled.

First native build with warnings-as-errors failed MSB3277 WindowsDesktop assembly conflicts. Official Autodesk migration guidance identifies the missing WindowsDesktop framework reference. Added `Microsoft.WindowsDesktop.App` and cross-platform restore opt-in; native Release net8.0-windows build then passed with zero warnings/errors against installed Revit25.2. No warning suppression was used for the target build. See native build failure summary and green log artifacts.

`scripts/audit-dependencies.ps1 -Dotnet ./.tools/dotnet/dotnet.exe` completed for all five maintained projects; JSON reports are in artifacts. There are no third-party NuGet packages at present. CodeQL/GitHub Actions are configured but have not run remotely.

The actual target CLI compiled the 5x4 room intention, planned +400mm width correction, persisted revision1 and retried the same key at revision1. Four semantic updates and seven dirty nodes; no documentation was regenerated and no native execution occurred. Native batch is target-built and its guarded integration smoke exists, but remains unexecuted until a loaded add-in and isolated Revit fixture are available. Production production model was not modified.

Final target benchmark with 10,000 synthetic walls: p50 74.0397ms, p95 216.393ms across20 samples, one emitted operation. These are core diff measurements on runtime8.0.21/16 logical processors, not validated native delivery latency or a full-rebuild speedup.

## GitHub access resolved and remote CI executed

After the user confirmed access, push of main635cc68 to `https://github.com/manuader/bim-agentic-os.git` succeeded. GitHub Actions run39874914458 completed successfully with both core and CodeQL jobs passing. `artifacts/ci-first-run.json` binds this result to the exact head SHA. This supersedes the earlier403 publication blocker; it does not establish native Revit execution.

## Native fixture and process routing integration
The opt-in add-in executed in real Revit25.2 a dedicated process, created/saved/reopened an isolated template fixture and published its ProjectInformation.UniqueId. This establishes initial bootstrap only. The first batch request was rejected document_conflict before mutation: Windows pipe ownership inspection showed the legacy global endpoint belonged to the production process. Routing now requires a per-process endpoint bound to fixture PID; exact path and identity guards remain. Target build9e7765b passed0warnings/errors and full gate20261023T001921221Z passed33 core assertions, fixture/launcher/CLI regressions. Dependency audit passed all six maintained projects. Evidence logs are versioned under artifacts.


Public evidence redaction: personal workspace paths, document identity and live process IDs are omitted from exported records. Original logs and fixture evidence remain under ignored local .bim directories. Versions, assertions, error codes, commit IDs and CI links are preserved.


The corrected process-specific endpoint was verified with the Windows pipe-owner API before model writes. Real Revit25.2 primitive smoke then passed: five native elements created, same request retried at revision1 with unchanged mappings, width corrected400mm through exactly three wall updates at revision2, all native identities preserved and geometry read back, tampered plan rejected. One primitive resize Send roundtrip measured102.821ms; this is not full validated-delivery latency. Final geometry save/reopen and documentation remain unverified. See artifacts/native-smoke-result.json.


A separate forced rollback probe also passed: the level was updated first, a following wall failed Revit ShortCurveTolerance, and replay of the original resize receipt returned native_geometry_readback at revision2 with all five mappings unchanged. This verifies transactional rollback and receipt retention in the current session; it does not prove disk persistence after save/reopen. See artifacts/native-rollback-result.json.


Remote CI run39877865497 passed both core and CodeQL on exact commit4b4327a (native bootstrap, process routing and guard regressions). The later rollback probe is separately native-executed and statically checked; subsequent commits require their own CI result. artifacts/ci-native-bootstrap-run.json records the remote result.


## Saved checkpoint slice
Code a8dba83 built against installed Revit25.2 with zero warnings/errors. Full gate20261023T003907959Z passed33 core assertions,17 checkpoint guard checks and existing harness regressions. Read-only independent review found no blocking issue. All seven maintained projects passed dependency vulnerability audit. Mutation-red test deliberately omitted receipt verification, failed checkpoint_receipt_missing, then restored implementation passed; red/green logs are versioned with personal paths redacted. Real save/reopen is not yet verified: the new isolated Revit process is waiting for its unsigned add-in load decision.
