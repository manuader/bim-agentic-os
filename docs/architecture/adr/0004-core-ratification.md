# ADR0004 — Ratify existing core language
Status: accepted; user explicitly confirmed retaining C#/.NET8 in this session.

Current implementation is C#/.NET8 with tested shared records and compiler consumed by CLI and Revit2025. Option A: retain it; no rewrite, preserves native type sharing and recorded tests. Option B: Rust core with a serialization/FFI boundary; may offer tighter memory control but adds interop/schema maintenance and invalidates existing native integration assumptions. No performance comparison has been measured, so no Rust speed claim is made. Recommendation: retain C#/.NET8. The approved decision preserves C#/.NET8 and does not authorize a BIM-IR schema change.


