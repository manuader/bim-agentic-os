# ADR 1: C# kernel and installed Revit version

Use C# .NET 8 for core and Revit 2025. Installed DLL version takes precedence over illustrative 2027 in the prompt. This avoids duplicating foundational semantics in Python/TypeScript. No third-party packages needed for the initial kernel. Native DLLs remain local and require an authorized Autodesk installation.

Use Windows named pipes with CurrentUserOnly rather than opening a network port. ExternalEvent is the only write dispatcher. Native writes require exact active saved document path. This is initial isolation, not a completed authorization system.
