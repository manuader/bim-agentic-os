# ADR0011 — Guarded free path for uncovered Revit requests

Status: proposed (relay178, 2026-11-10). Needs the owner's acceptance before P9-03 is built. [ADR0009](0009-revit-command-surface.md) (accepted) decided that a guarded free path exists and that "its design gets its own short ADR before code (compiler choice, sandboxing limits, logging), because it is the riskiest piece". This is that ADR. It changes no BIM-IR schema and no existing persisted format.

## Context

The catalogue (ADR0009 §1) covers a growing but finite set of commands. For a request it does not cover, Claude should be able to write Revit API C# and have the add-in run it, with the same guarantees the catalogue gives: copies first, one serialized writer, undo by default, and a report of what changed. Running model-written code inside Revit is the riskiest capability in the product: the add-in runs with the owner's user rights, Revit has no in-process sandbox, and .NET 8 has no Code Access Security, so isolation must come from what the code is allowed to reference, not from runtime permissions.

## Decision (proposed, option A)

1. **Compiler.** Roslyn (`Microsoft.CodeAnalysis.CSharp`, MIT, pre-approved licence class) is loaded by the add-in only. The submission is one C# method body `void Run(FreePathContext ctx)`; the add-in wraps it in a fixed class. Compilation uses an explicit reference set: `System.Runtime`, `System.Collections`, `System.Linq`, `RevitAPI`, and the small `Bim.Revit.FreePath` facade. No `RevitAPIUI`, no `System.IO`, `System.Net`, `System.Diagnostics.Process`, `System.Reflection.Emit`, `System.Runtime.InteropServices`, threading or `unsafe`.
2. **Allowlist, checked on the syntax/semantic tree before emit.** Every symbol the code binds to must be in an allowlist of namespaces and types (Revit DB types, geometry, collections, LINQ, math, strings). Refused: `dynamic`, reflection (`Type.GetType`, `Activator`, `GetMethod`), `Assembly`, `AppDomain`, `Environment`, `GC`, `Marshal`, file/network/process types, `Document.Save*`, `Document.Close`, `Application.OpenDocumentFile`, `UIApplication`, `ExternalEvent`, `Transaction*` (the add-in owns transactions), `Document.Export`, event subscription and static mutable fields. A refused symbol is `free_path_symbol_refused` with the location; nothing runs. The allowlist is data in the repo, versioned, unit-tested on a corpus of allowed and hostile submissions headless (CI, no Revit).
3. **Execution.** Only on a document that passes the copies guard (ADR0009 §3); never production. Run inside the existing ExternalEvent serialization, one `TransactionGroup` with one `Transaction`. Default mode `dry_run`: the group is always rolled back after the change report is captured. `commit` mode needs an explicit `confirm` token issued by a previous dry run of byte-identical code on the same document state (request SHA-256 + document UniqueId + model version), so the owner/Claude can review the report first.
4. **Limits.** Source ≤ 64 KiB; compile ≤ 10 s; run time limit 30 s measured with a stopwatch checked by a `ctx.Check()` the wrapper injects at loop back-edges (Revit API calls cannot be aborted, so a long single call is reported as `free_path_timeout` after it returns and the group is rolled back); at most 10 000 created/modified/deleted ids reported.
5. **Change report.** Created/modified/deleted element ids with UniqueIds, categories and names from `DocumentChanged` within the transaction, plus failures/warnings from the failures preprocessor; same receipt envelope and readback style as catalogue commands.
6. **Logging.** Every submission (source SHA-256, decision, compile diagnostics, refusals, report summary, actor) is appended to the P10-03 audit log under command `free_path.run` with permission level `write`; the source text itself is stored next to the run's receipts under `.bim/native-runs` (never in the model).
7. **Surface.** One catalogue command `free_path.run {code, mode: dry_run|commit, confirm?}` exposed through CLI and MCP like the rest; MCP descriptions tell Claude to prefer catalogue commands.

## Options

- **A (recommended):** in-process Roslyn with symbol allowlist, rolled-back group, confirm-token commit (above).
- **B:** out-of-process compile in a separate helper that emits only an IL-validated assembly the add-in loads. Better crash isolation for the compiler, same runtime trust (Revit API must run in-process anyway); more moving parts.
- **C:** no compiled code; a small interpreted DSL over a fixed set of Revit API calls. Safest, but every new API call needs a release, which defeats the purpose of the free path.
- **Defer:** keep the catalogue only; uncovered requests go to the playbook/board.

## Consequences

- Roslyn adds ~10 MB to the add-in package (shipped, MIT). The CI can compile and unit-test the allowlist checker without Revit; running code in Revit stays owner-gated (🔒, `scripts/owner-native-session.ps1` steps on a test copy).
- The allowlist is the security boundary. It is a best-effort guard for a trusted single-user tool, not a sandbox against a determined attacker; the copies guard, rollback default and audit log are the other layers.
- No change to BIM-IR, receipts format or the audit log format (new command name only).
