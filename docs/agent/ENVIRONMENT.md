# Environment census

Verification states describe evidence scope, not product completeness. Raw command logs remain in ignored `.bim/environment/`; exported hardware/version data is in `artifacts/environment-census.json`. No credentials, machine identifier, user path or project name is published.

| Item | Observed command output | State / limits |
| --- | --- | --- |
| OS | Microsoft Windows11 Home,10.0.26200,64-bit | described; Get-CimInstance Win32_OperatingSystem |
| Hardware | AMD Ryzen9 8945HS w/Radeon780M Graphics,16 logical processors,32037MB visible memory | described; Get-CimInstance Win32_Processor / OS |
| Local .NET SDK |8.0.415,MSBuild17.11.48; runtime8.0.21 | compiled; actual core/CLI/native target build logs exist |
| Python |3.12.14 bundled runtime | described; executable ran version/import discovery; WindowsApps python alias is not used as runtime evidence |
| Node |v24.15.0 | described; node --version; no Node product backend |
| Rust/Cargo |not found on PATH | unsupported for current cycle; no assertion about every disk location |
| Revit |2025; RevitAPI.dll FileVersion25.2.0.38; API XML references present | compiled; native build log; no2027 compatibility claim |
| Native primitive backend |actual create/patch/retry/rollback/save-close-reopen/readback | verified-in-revit; artifacts/native-checkpoint-result.json; level/walls only |
| Autodesk license entitlement |not queried | manual-only; prior editable authoring/save succeeded, not a current license/account census. No credentials accessed |
| IfcOpenShell |importlib.util.find_spec returned false | blocked; absent and LGPL dependency approval pending |
| Linux/macOS local execution |not available on this Windows host | blocked locally; Ubuntu GitHub CI executed existing.NET suite; no IFC/macOS integration inferred |

Commands used (run at repository root, with `$BimPython` set to the usable Python3.12 executable):

```powershell
Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,OSArchitecture,TotalVisibleMemorySize
Get-CimInstance Win32_Processor | Select-Object Name,NumberOfLogicalProcessors
./.tools/dotnet/dotnet.exe --info
node --version
Get-Command rustc,cargo -ErrorAction SilentlyContinue
& $BimPython -c "import sys,importlib.util;print(sys.version);print(importlib.util.find_spec('ifcopenshell'))"
(Get-Item 'C:/Program Files/Autodesk/Revit 2025/RevitAPI.dll').VersionInfo.FileVersion
Test-Path 'C:/Program Files/Autodesk/Revit 2025/RevitAPI.xml'
```

Observed scalar outputs: SDK8.0.415; Nodev24.15.0; Python3.12.14; IfcOpenShell false; rustc/cargo no entries; Revit25.2.0.38 and XML true. Detailed sanitized hardware capture has a UTC timestamp in the artifact. SDK/runtime target verification commands and actual integration procedures remain in `tests/integration/README.md`; native primitive success is not full Test A/B acceptance.
