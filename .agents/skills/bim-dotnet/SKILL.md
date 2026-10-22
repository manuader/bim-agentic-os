---
name: bim-dotnet
description: Build and test C# and .NET components of BIM Agentic OS, including target compatibility and reproducible evidence.
---

# bim-dotnet

Use net8 core and the installed Revit target defined by ADR 0001. Run scripts/verify.ps1; -HostOnly is a diagnostic fallback, not proof of target compatibility. Test deterministic changes red-green before implementation. Keep Autodesk references out of core and vendor DLLs out of Git. Reject non-finite dimensional inputs at boundaries. Do not change runtime targets merely to make host Roslyn checks pass. Distinguish source-checked, target-built and native-loaded evidence.
