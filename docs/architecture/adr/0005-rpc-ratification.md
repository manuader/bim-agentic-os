# ADR0005 — Ratify existing local RPC transport
Status: accepted; user explicitly approved retaining Named Pipes on2026-10-22 (America/Buenos_Aires).

Current bridge uses Windows Named Pipes with CurrentUserOnly and explicit PID endpoints plus exact document guards; actual isolated native integration passed. Option A: retain and strengthen version/auth/job contracts; local deployment stays simple. Option B: gRPC loopback; portable tool ecosystem and streaming, but adds HTTP/TLS/token/deployment dependencies and migration work. Neither existing pipe isolation nor gRPC alone is a complete authorization system. Decision: retain Named Pipes for this cycle. No migration is needed.
