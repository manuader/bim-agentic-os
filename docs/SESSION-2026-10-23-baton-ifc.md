# Compiler-driven IFC and Baton coordination

The next functional slice now uses the existing C# RoomCompiler/Compiler.Diff to drive real IFC execution. Core records remain unchanged. Baton adds durable queue/leases/relay handoffs; its five upstream MIT skills are pinned, with an explicit Codex/Windows adaptation and an owner-approved hourly same-chat heartbeat. Routine creation was observed, future unattended execution was not.

## Decisions and defects that mattered

Keep exact default C# serialized strings across the adapter. Python canonicalization cannot recreate the Core hash contract. The first implementation persisted an after-plan hash at the old revision; a second legitimate correction failed. Exact CommittedAfterJson with revision+1 fixes this without changing BIM-IR. The packet still binds the original plan separately. The actual tests now cover5→5.4→5.8m on one saved IFC and a distinct6→6.4m case.

Persisted state/receipt checks must be independent expected values, including both public and compiled revision tokens. Comparing a receipt object with a copy of itself proved nothing. Latest retry is supported; historical retry after a later correction deliberately rejects without writes. Lowlevel no-op preserves bytes; full A/B requires a real correction and rejects no-op before output creation. Do not fabricate a rollback event for an operation that never mutated.

Direct Python green did not establish CLI interoperability: strict C# Disallow parsing rejected new metadata. Declare internal typed binding fields and check UUID/role/native identity/unsupported outputs; keep strict parsing. Explicit null After/Elements tests prevent exceptions escaping instead of blocked results. Evaluator coverage increased58→68 assertions without changing native acceptance criteria.

Baton review caught unauthorized board updates, tab-separated section loss and incomplete lease validation. All updates now hold the lease guard and use the same complete validator. Do not automatically recover a stale lock. The first bootstrap lock predated the tested adapter; it is not evidence of atomic acquisition. Real two-process contention is covered by the native adapter oracle.

## Measurements and limits

Fresh controller gate passed on Windows SDK8.0.415/runtime8.0.21:50core/68evaluator/10compiler/28coordination/17checkpoint/5CLI assertions. Two restricted MSBuild attempts failed with0errors/0warnings; identical source/SDK/arguments passed elevated, followed by a complete green gate. Exact failing OS operation remains unknown. No source or dependency workaround was introduced.

Final-source actual IFC suite:11tests90.026s. Actual CLI:41pass/0fail/0blocked/9unsupported, all8backend checks pass; backend=true/product=false and expected exit2. Saved IFC hashes independently matched. Single sample: update8335.766ms, retry77.295ms, QA8440.373ms, worker lifecycle17058.054ms, shell including build/startup21761.738ms. This includes multiple fixture operations/QA and excludes native validated documentation delivery. Core-only10000element diff:20samples,p50=73.7752ms,p95=194.3746ms. These are different scopes, not a demonstrated speedup.

Scope remains one canonical rectangle and width-only changes. Six physical Core UUIDs persist; plan/schedule/sheet remain unsupported and door/windows fixture-only. No Revit execution occurred. Previous native primitive/checkpoint evidence remains valid only for its limited scope. Human goldens remain unapproved; arbitrary BIM-IR, complete native documentation, macOS and general roundtrip remain unverified.

## Evidence and continuation

[Engineering](../artifacts/compiler-ifc-engineering-result.json), [11 real tests](../artifacts/compiler-ifc-tests-result.json), [actual CLI](../artifacts/compiler-ifc-integration-result.json), [benchmarks](../artifacts/compiler-ifc-benchmarks.json), [coordination](../artifacts/baton-harness-result.json). [Remote CI record](../artifacts/compiler-ifc-ci-result.json) is maintained separately as runs are actually observed; no remote conclusion is inferred from local success.

Next highest impact is native hosted Room and affected documentation A/B, then complete correction-delivery timing. See [board](../tasks.md), [general handoff](HANDOFF.md), [persistent state](agent/STATE.md) and ignored current run. Production models remain untouched; native writes serialized. All implementation agents finished before writing this session document.
