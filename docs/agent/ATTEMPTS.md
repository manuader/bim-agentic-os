# Attempts ledger

Maximum three failing attempts per test, each with a distinct recorded hypothesis. Never relax an oracle to obtain green. Intentional TDD red is recorded explicitly. On exhaustion, restore the affected implementation to its last green commit without deleting unrelated work, record a blocker and continue independently. New scope applies prospectively; earlier historical evidence is preserved, not reconstructed as imaginary attempts.

| Test / attempt | Hypothesis | Change | Observed result | Evidence / next action |
| --- | --- | --- | --- | --- |
| legacy-status rejection / 1 (intentional TDD red) | Existing status enum admits legacy implemented claims | Add legacy enum and missing-evidence regressions before production changes | Exit 1: Gate failed to reject legacy-implemented | .bim/status-red.log; change enum and evidence rule |
| capability-state regressions / green | Exact enum and evidence requirement reject false claims | Migrate gate and registry, preserve existing architecture/artifact regressions | Exit 0 | .bim/status-green.log |

| full SDK completion gate / green | Migration preserves all available gates | Run scripts/verify.ps1 using .NET 8 SDK after final allowed-state acceptance tests | Exit 0; 50 core assertions; no native execution claimed | .bim/status-final-verify.log; .bim/verification/20261023T012014276Z |

Initial sandbox-denied write did not modify files or execute tests; approved worktree escalation used subsequently.

The temporary-isolation worker originally numbered its chronological discoveries 1/2/3. They are different individual tests, not three failed attempts at one test. Per-test accounting follows; raw logs are retained in `.bim/temp-isolation-evidence/`.

| Individual test / failing attempts | Hypothesis and change | Result / evidence |
| --- | --- | --- |
| Child temporary directory / 1 intentional red | Inherited host temp escapes evidence; scope TEMP/TMP/TMPDIR inside helper try/finally | Green including child exit7 and parent restoration; temp-green3.log |
| No-Git fixture / 1 | Fixture moved inside checkout discovers ancestor Git; bound discovery to fixture parent | Green without weakening missing-Git oracle |
| Invalid PowerShell syntax fixture / 1 | Absolute ancestor .bim exclusion skips fixture scripts; filter paths relative to checked root | Green, syntax oracle preserved |
| Failed fixture move environment restoration / 1 observed baseline leak | Move outside try leaks ceiling variable; track move and nest finally | Baseline restored=False, corrected restored=True; temp-review-move.log |
| Move reproduction setup / 1 | Missing Stop preference differs from original script; reproduce original error semantics | Second probe reproduces baseline and correction; no fixture created/deleted |
| Full isolated SDK gate / 0 | Check integrated regressions after review fix | Exit0; temp-review-verify.log, verification/20261023T013546572Z |

The CRLF edit guard stopped before writing; it was an edit-precondition failure, not a test attempt. Independent static review accepted commit979c9c1 after the environment-restoration correction. No native model operations were performed by these workers.

Evaluation TDD accounting (each listed case had one intentional failing observation before its correction; no case exhausted three attempts):

| Individual case | Hypothesis / correction | Observed evidence |
| --- | --- | --- |
| Missing evaluator API | Implement typed C# oracle after compile red | Worker evaluation-attempts.md; initial missing-feature compile red |
| Metric test compilation | Enumerate dictionary entries rather than values when reading keys | Test correction only; assertions unchanged |
| Claimed width with unchanged geometry | Compare measured inner-face rectangle and hosted positions | Regression red then green |
| Missing single-command launcher | Implement shared CLI runner and JSON-only stdout | Expected exit2 instead of missing-script exit64 |
| Duplicate width dimension references | Require distinct opposite width walls | Review regression red then green |
| Unrelated opening displacement | Derive movement from host geometry; caller IDs cannot authorize arbitrary changes | Review regression red then green |
| Category drift | Preserve category and host per logical ID | Review regression red then green |
| IFC unsupported masking measured failure | Preserve actual failed criteria while classifying native-only outputs | Review regression red then green |
| Per-element wall/door/dimension values and unknown Room area | Require nullable individual measurements; missing values block | Parameter regressions red then green; 40 synthetic assertions at commit258198e |

Two initial full evaluation gates were interrupted before completion while temporary isolation was being corrected; neither is recorded as passing. Independent read-only review of258198e found the reported oracle issues resolved. Synthetic fixtures establish unit-tested scope only, never backend acceptance.

| Later individual case / failing attempts | Hypothesis / change | Observed result |
| --- | --- | --- |
| Launcher absent environment restoration / 2 | PowerShell coerces null to empty for the .NET setter; remove originally absent Env: entries in finally | Green; launcher success/failure restoration and blocked report persistence covered at4138608 |
| Evidence helper absent environment restoration / 1 intentional red | Earlier tests captured environment after it was already normalized; explicitly unset each variable before success/failure child | Red: absent TEMP became defined; null branch correction green at5af4186; independent review accepted |
| Controller launcher build permission / 1 per backend | Restricted build returned Build FAILED with zero diagnostic errors; rerun identical code with authorized elevated build access | Both launchers built; Revit40blocked, IFC32blocked/8unsupported, exit2 and ProductAcceptance=false. No source change or backend execution; artifacts/evaluation-launcher-result.json |
| Missing after/retry revision tokens / 1 intentional red | Null equality could admit missing revision evidence; require all revision tokens nonempty | Red then43 assertions green; reviewed6bbdda3, root equivalent a4f26ba |
| Controller scoped CLI invocation / 1 | CliDll is a parameter of another integration script, not this evaluator health script; inspect actual signature and pass only Dotnet | Corrected invocation exit0; five CLI checks; no source change |

Controller full gate atf40b7c7 passed50 core/42 evaluator/17 checkpoint assertions and five evaluator CLI checks. The final evaluator-only guard at a4f26ba passed fresh warning-free builds,43 evaluator assertions and five CLI checks. Nine-project dependency vulnerability audit passed. Evidence summary: artifacts/evaluation-engineering-result.json. No full A/B backend acceptance was issued.

