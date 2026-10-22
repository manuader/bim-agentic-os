# ADR 0002 — small enforceable development harness

Status: accepted for prototype; 2026-10-22.

Keep the existing C# kernel and add mechanical boundaries, tests, CI and evidence rather than replacing the architecture. Follow repository-local pinned Superpowers workflows and the OpenAI harness engineering practices of a short map, versioned repository knowledge, isolated execution and enforceable invariants.

Managed Git worktrees isolate independent source changes; the Revit document scheduler stays serialized. Standard-library-only C# tests run as a console regression suite. Host Roslyn fallback is useful in this environment but never substitutes for an SDK target build or licensed native integration. CI may be configured before its remote execution is possible; those states must remain distinct.

Tradeoff: dependency-free prototype tests and file persistence enable immediate execution, but test discovery/reporting and durable live mirror behavior need later strengthening. Gates check concrete architecture/evidence properties; they are not a claim of complete security or professional BIM certification.
