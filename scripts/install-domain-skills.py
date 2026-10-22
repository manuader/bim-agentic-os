"""Install repository-owned BIM engineering skills; run from the repository root."""
from pathlib import Path
import shutil

root = Path(__file__).resolve().parents[1]
skills = {
    "bim-revit-api": (
        "Implement or review native Revit API operations and their Windows integration tests in BIM Agentic OS.",
        "Use the installed DLL/SDK version, currently 25.2, as evidence; never infer 2027 support. Run scripts/check-native-source.ps1 only as a source check, and the SDK build separately. Writes run through ExternalEvent and serialized transaction groups. Preflight exact document identity, editability, hosting and family types. Read back categories, UniqueIds, geometry and document outputs before declaring validation. Use an isolated saved fixture, never Casa Angel. Native retries require durable mapping/idempotency; preserve unrelated IDs. Record rollback and reopen evidence. See docs/contracts/module-contracts.md and tests/integration/README.md."),
    "bim-dotnet": (
        "Build and test C# and .NET components of BIM Agentic OS, including target compatibility and reproducible evidence.",
        "Use net8 core and the installed Revit target defined by ADR 0001. Run scripts/verify.ps1; -HostOnly is a diagnostic fallback, not proof of target compatibility. Test deterministic changes red-green before implementation. Keep Autodesk references out of core and vendor DLLs out of Git. Reject non-finite dimensional inputs at boundaries. Do not change runtime targets merely to make host Roslyn checks pass. Distinguish source-checked, target-built and native-loaded evidence."),
    "bim-ir": (
        "Change persistent BIM-IR, semantic identity, provenance, units or mirror synchronization contracts.",
        "Keep intended and realized state separate. Use logical UUIDs and explicit metre coordinates; convert to native units at the adapter. Preserve provenance and revision tokens. Before changing a serialized record, add roundtrip, malformed-input and migration tests. Dependencies point from output/dependent to prerequisite. A mirror needs document identity, UniqueId bindings and readback revision; an intended-state commit alone does not create it. See core/Bim.Core and docs/contracts/module-contracts.md."),
    "bim-incremental-compiler": (
        "Implement deterministic architectural intent compilation, semantic diffs, dependency closure and revision benchmarks.",
        "Start from a failing correction regression with fixed anchor, protected invariants and preserved IDs. Compile outside Revit; batch writes inside it. Include documentation in transitive dirty closure. Test no-op, deletion, dependency cycles, stale/tampered plans, duplicate retries and forced rollback as relevant. Measure full planning separately from native apply/readback/documentation. Existing core benchmark scans all nodes; do not claim constant-time or native delivery speedups. Run scripts/benchmark-roslyn.ps1 only as host evidence and preserve raw samples."),
    "bim-blender": (
        "Develop or validate Blender geometry workers and BIM conversion boundaries when a Blender adapter is requested.",
        "Blender support is pending, not implemented. First discover the executable version and official bpy API; do not add a placeholder capability marked working. Isolate geometry workers from Revit threads. Retain source .blend, coordinate transforms and units. Meshes are visualization or declared lossy representations unless independently mapped into native semantic elements. Add deterministic fixture geometry and conversion loss tests before integration; avoid rendering as proof of BIM correctness."),
    "bim-ifcopenshell": (
        "Implement IFC authoring, validation or roundtrip checks using IfcOpenShell for BIM Agentic OS.",
        "IfcOpenShell adapter is pending. Discover installed version, supported IFC schema and dependency license before adding it. Preserve GlobalId mappings and explicit units/placements. Validate IFC independently from Revit authoring; compare geometry, property and classification losses by field. No byte-for-byte RVT equivalence claim. Add schema fixtures and IDS/BCF tests only when those tools actually execute. Keep native export and independent inspection evidence distinct."),
    "bim-validation": (
        "Implement independent BIM QA and evidence gates for geometry, semantics, native identity and affected documentation.",
        "Use measured readback rather than generator success flags. Return rule, severity, logical/native IDs, measured value, tolerance and source. Reject missing outputs and unsupported capabilities explicitly; partial documentation is not a validated delivery. Test malformed models, stale mirrors, unchanged-object identity, unit boundaries and rollback failures. Never invent jurisdiction rules; professional compliance stays unverified without an approved source-backed pack. Update docs/features.json with evidence at its actual level."),
}
destination = root / ".agents" / "skills"
for name, (description, body) in skills.items():
    target = destination / name
    target.mkdir(parents=True, exist_ok=True)
    (target / "SKILL.md").write_text(f"---\nname: {name}\ndescription: {description}\n---\n\n# {name}\n\n{body}\n", encoding="utf-8")
license_source = root / ".tools" / "superpowers" / "LICENSE"
if license_source.exists():
    shutil.copyfile(license_source, destination / "SUPERPOWERS-LICENSE")
print(f"Installed {len(skills)} repository-owned development skills")
