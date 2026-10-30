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

## Approved real IFC executor block

User approved Named Pipes and isolated IfcOpenShell LGPL on2026-10-22 (America/Buenos_Aires). Separate worktrees owned the worker and C# bridge; a third agent independently reviewed source. Raw logs are copied to local .bim/ifc-worker-evidence and .bim/ifc-bridge-evidence before archival.

| Individual test / failed attempts | Hypothesis and correction | Observed result |
| --- | --- | --- |
| Actual IFC roundtrip pipeline / 2 including intentional red | Worker absent; then upstream EXPRESS imports undeclared _pytest. Implement worker and pin permissive pytest runtime without disabling EXPRESS | Green21.264s; final four-case controller suite25.986s |
| Shifted opening profile / 2 including intentional red | Volume and nominal bounds did not detect translated opening profile; first CRLF edit missed the source call, then independent opening triangulation applied | Negative rejected; final worker suite green |
| Foreign before.ifc preservation / 1 intentional red | Preflight guarded only room.ifc; guard all named artifacts before authorship | Existing bytes/file set preserved; green |
| Foreign staging preservation / 1 intentional red | Pending file overwrite/cleanup could remove foreign data; exclusive-create and clean only owned staging | Existing pending and model bytes preserved; green |
| Portable missing native outputs / 1 intentional red | IFC-native distinction must leave absent documentation unsupported while preserving measured violations | Existing native regressions retained; portable tests green |
| Process options compile / 2 including intentional missing-API red | Add bounded process port; correct extra parenthesis in root discovery | Release builds green, no oracle change |
| Internal protocol validator / 1 intentional compile red | Extract private validator for malformed/version/check/metric rejection coverage, no public JSON provenance API | Parser rejection coverage green |
| Owned process timeout / 1 intentional compile red | Bound process and stream lifetime, kill only owned process tree | Actual isolated timeout test green |
| Process/check agreement / 1 intentional compile red | A successful process cannot report failed checks or vice versa without rejection | Truth-table and runtime wiring green;58 evaluator assertions |
| Cloud path classification / filesystem diagnosis | OneDrive ancestors have ReparsePoint=true but LinkTarget=null; broad reparse rejection falsely blocks trusted files | Guard actual links/junctions; controller real IFC execution passed on OneDrive. No pre-fix CLI failure is invented |

Official42,827,327-byte wheel was downloaded after initial pip transfer stalled and SHA256 matched PyPI. Metadata/download failures were dependency setup, not passing integration. Worker main pipeline had two failures total; opening regression had two; other named cases one. No individual test exhausted three failures. Reviewer accepted final worker e0ddd09/cb2597b and bridge5d92766/b2170f0/ad32f0d; controller equivalents b5ff122/b060474/551a4e8/cc557dd/03d2a97.

Controller actual IFC result:41 case checks pass,0 fail/blocked,9 unsupported; eight mandatory backend checks pass; backend acceptance true, product acceptance false, exit2. Full engineering gate03d2a97 passed50 core/58 evaluator/17 checkpoint assertions and five CLI checks. Four Python integration tests passed25.986s. Pip check found no broken requirements. Single-run benchmarks/hashes and precise limits are in artifacts/ifc-integration-result.json. No new Revit execution or arbitrary BIM-IR compilation is claimed.

Exact-commit remote verification: e689aaa GitHub run39889181516 succeeded on its first run, including actual Ubuntu IFC tests/enforced portable acceptance, core gate, C# CodeQL and Python CodeQL. No CI failure or retry is invented. Public evidence is artifacts/ifc-ci-result.json; raw generated IFCs remain local.


## Baton / compiler-driven IFC relay —2026-10-23

| Individual case / failures | Hypothesis and change | Evidence |
|---|---|---|
| Baton missing adapter /1 intended red | native coordination absent |28 assertions final and independent review |
| Unauthorized progress update /1 intended red | update bypassed lease | guarded holder requirement; board bytes preserved |
| Tab heading preservation /1 intended red | space-only section boundary | unified heading grammar; unrelated section retained |
| Incomplete lease metadata /1 intended red | update used partial validator | shared full fail-closed validator; malformed timestamps rejected |
| C# IFC packet missing /1 intended red | typed exporter absent |10 contract assertions; Core unchanged |
| Worker request intake missing /1 intended red | execute overload absent | strict prewrite packet validation and actual packets |
| Consecutive revision /1 intended red | after-plan hash omitted committed revision increment | exact C# CommittedAfterJson;7→8→9 and stable identities |
| No-op A/B preflight /1 intended red | created directory before rejecting no-op | reject before mkdir; lowlevel bytes unchanged |
| Corrupt retry metadata /1 intended red | receipt branch skipped persistent-state integrity | exact state/receipt checks; bytes unchanged |
| Corrupt before revision /1 intended red | public revision token not checked | compare r+ExpectedRevision before mutation |
| Actual CLI envelope /1 integration red | strict Disallow parser rejected new undeclared metadata | typed internal fields plus request/native mapping checks; strictness retained |
| Binding validation port /1 intended red | helper absent and null nested payload unsafe |68 evaluator assertions including10 new cases |
| Controller evaluation build /2 restricted failures | MSBuild reference-framework task failed with0errors/0warnings | same source/SDK/TEMP/args passed elevated; complete gate passed. Exact failing OS operation unknown; no source workaround |

No individual case exhausted3failed attempts. Syntax-only unmatched brace and whitespace amendment were edits, not passing backend tests. Worker complete suite10tests93.744s preceded final revision guard; its affected final-source3tests30.107s passed. Controller supersedes this with fresh final-source11tests90.026s, complete engineering gate and actual CLI41pass/0fail/0blocked/9unsupported,8backend checks, exit2/backend=true/product=false. Raw ledgers/logs preserved in ignored .bim/baton/2026-10-23-core-ifc. Native Revit did not execute this relay.

## Native Room A/B (relay2 Codex, recorded by relay3)
- Attempt1: hypothesis fixed Instance field type is valid for Room schedules → AddField ArgumentsInconsistentException, whole Create group rolled back → select fields from GetSchedulableFields by native parameter id (6a9f0ef).
- Attempt2: hypothesis instance DOOR_WIDTH carries the hosted door width → empty/zero instance value masked the type parameter, 47/52 → read populated instance-or-type width (287014c).
- Attempt3: same oracle, fix 287014c → 52/52, 8/8 backend checks, product_acceptance true. Budget 3/3 used for this test; no further blind retry.

## C04 catalogue owner session, headless gate (relay68)
- Attempt1: hypothesis hashtable members read with dot syntax return $null under Set-StrictMode Latest → test-catalogue-session failed on core and core-windows ("property 'Replayed' cannot be found") → optional keys read by index (3c79048). Same oracle then passed on all 8 jobs. Job logs and artifacts are not reachable from the agent session (blob host 403); verify.ps1 now annotates the failing gate with its log tail.
