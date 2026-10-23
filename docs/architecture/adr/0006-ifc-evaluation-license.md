# ADR0006 — Optional IfcOpenShell evaluation dependency
Status: proposed; explicit license approval required.

IfcOpenShell is absent in detected Python3.12.14. Upstream identifies the Python library as LGPL-3.0-or-later, a non-permissive dependency under this cycle's approval rule. Reference: https://github.com/IfcOpenShell/IfcOpenShell and https://docs.ifcopenshell.org/ifcopenshell-python/installation.html . No package installed yet.

Option A: approve an isolated, pinned optional evaluation environment and Ubuntu CI job using IfcOpenShell, preserving license notices and separating it from the C# core/Revit deployment. Enables actual IFC integration evidence; distribution obligations need explicit review before bundling. Option B: defer the dependency; implement the backend contract and fail closed as blocked, with no IFC integration claim. Recommendation: approve optional evaluation-only installation. No claim that IFC validates native Revit annotations, views or sheets.
