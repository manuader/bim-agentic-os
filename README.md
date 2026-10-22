# BIM Agentic OS

Implementation governed by [master specification](BIM_Agentic_OS_PROMPT_DEFINITIVO.md). Initial kernel, not a completed BIM authoring product.

## Executable modules

- C# .NET 8 semantic IR, explicit metre boundary, deterministic diff and transitive documentation invalidation.
- File-backed intended-state snapshots, optimistic revisions, checkpoints, prepared journal and retry receipts published atomically with revisions. This is not yet the realized native model mirror. Power-loss and journal recovery testing remain pending.
- CLI shares the core (`diff`, `validate`, `init`, `plan`, `commit-intent`) and sends explicit native requests (`native`). Intent commits persist semantic state only; they do not synchronize Revit. No natural-language parser or MCP endpoint yet.
- Revit 2025 add-in source: same-user Windows named pipe, ExternalEvent serialized execution, document path guard, level creation, wall creation/axis modification, inspection, deletion and transaction rollback. Native requests are NOT yet idempotent; never blindly retry mutations. Wall creation currently uses the document default wall type; 200 mm construction is not guaranteed.

## Reproduce

Install official .NET 8 SDK into `.tools/dotnet` or use system `dotnet`.

```powershell
./scripts/test.ps1
# SDK-free fallback with host references (does not prove net8 compatibility):
./scripts/test-roslyn.ps1
./scripts/check-native-source.ps1
./scripts/benchmark-roslyn.ps1
./.tools/dotnet/dotnet build runtimes/revit-native/Bim.Revit.csproj
./.tools/dotnet/dotnet run --project interfaces/cli -- diff examples/before.json examples/after.json
```

Revit API assemblies are referenced from the installed Autodesk product, not distributed. Replace Assembly in `runtimes/revit-native/Bim.addin.template` with the absolute built DLL path and install as `.addin` in the user's Revit 2025 Addins folder. Revit restart is required. Use a dedicated saved test document. The existing Casa Ángel project is not a test fixture.

Native request JSON example (absolute path must match active saved document):

```json
{"DocumentPath":"C:\\BimTests\\fixture.rvt","Operation":"level.create","UniqueId":null,"Metres":[0]}
```

## Acceptance status

Phase 0 partial. RevitAPI 25.2.0.38 discovered; live bridge transaction start/rollback probe succeeded. This verifies the existing bridge, not this add-in. Phase 1 / Test A not passed. No native RVT delivered. Phases 2–4 have early core implementation only. See `docs/evidence.md` and `tests/integration/README.md`.
