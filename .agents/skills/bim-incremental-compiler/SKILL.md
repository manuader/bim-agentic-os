---
name: bim-incremental-compiler
description: Implement deterministic architectural intent compilation, semantic diffs, dependency closure and revision benchmarks.
---

# bim-incremental-compiler

Start from a failing correction regression with fixed anchor, protected invariants and preserved IDs. Compile outside Revit; batch writes inside it. Include documentation in transitive dirty closure. Test no-op, deletion, dependency cycles, stale/tampered plans, duplicate retries and forced rollback as relevant. Measure full planning separately from native apply/readback/documentation. Existing core benchmark scans all nodes; do not claim constant-time or native delivery speedups. Run scripts/benchmark-roslyn.ps1 only as host evidence and preserve raw samples.
