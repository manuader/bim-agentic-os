# ADR0005 — Ratify existing local RPC transport
Status: proposed; user decision required by scoped cycle.

Current bridge uses Windows Named Pipes with CurrentUserOnly and explicit PID endpoints plus exact document guards; actual isolated native integration passed. Option A: retain and strengthen version/auth/job contracts; local deployment stays simple. Option B: gRPC loopback; portable tool ecosystem and streaming, but adds HTTP/TLS/token/deployment dependencies and migration work. Neither existing pipe isolation nor gRPC alone is a complete authorization system. Recommendation: retain Named Pipes for this cycle. No migration while pending.
