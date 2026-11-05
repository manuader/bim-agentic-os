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

## Relay139 (cloud)
- P1-02 Attempt1: hypothesis a core comment naming the add-in path trips the harness "Core dependency boundary" gate (`runtimes/` in core) → reworded comment (2b1ac32). Attempt2 (review round): spec id literals containing the vendor name trip the same gate → match spec ids by shape (4a51ca6). No test changed.
- P3-02 Attempt1: hypothesis test locals named `args` clash with the implicit top-level-statements `args` (CS0136) → renamed to `values` (6198975). Golden output hash pinned from CI output (regression pin, 9e0884c).
- P8-01: no failed attempts.

### Relay140 (2026-11-03, cloud)
- P6-02: no failed attempts across three rounds (initial, review blockers B1–B4, re-review blockers).
- P7-02 Attempt1 ("64 alternatives differ in total"): hypothesis a 3-room 6x6 brief yields layouts with different totals → wrong, 4 ranked with equal totals (run 41599100287). Attempt2: richer 4-room brief with preferred pairs → wrong, 8 ranked all equal (run 41599597953; small briefs give equal-score mirrored layouts). Attempt3 succeeded: the 64 case uses apartment-2br from seed 0 (run 41600090447). Oracle not relaxed (still ≥2 ranked and ≥2 distinct totals).
- P10-02: no failed attempts; golden zip SHA-256 pinned from CI output (identical on Linux and Windows, run 41602416672).

### Relay141 (2026-11-03, cloud)
- P9-02: no failed attempts (initial and review rounds).
- P5-03 Attempt1 (red push 747f783, run 41615318757): test helper `Check` passed as `Action<bool,string,string>` but called with 2 arguments (compile error, not an oracle failure) → local wrapper with optional detail (2225179). Review round: no failed attempts; qa clear-height default changed 2.5→2.6 as a value fix (CABA minimum), the Program.cs assertion follows the value.
- P2-03 Attempt1: hypothesis a core comment naming the add-in path trips the harness "Core dependency boundary" gate → reworded comment (5f6c382). Review round: no failed attempts.

### Relay142 (2026-11-03, cloud)
- P4-02 Attempt1 (run 41634179122, ee2de53, native-build only): hypothesis `Point2` is a reference type → wrong, it is a struct; optional `Point2?` did not convert (CS1503) → unwrapped with `is { } x` patterns (70941f9). Review round: no failed attempts.
- P3-03: no failed attempts in the first round. Review round Attempt1 (run 41637892756, a033b62): new test local `again` clashed with an existing method-level local (CS0136) → renamed (79c2457, valid red 41639513716).
- P8-04: no failed attempts (initial and review rounds); golden SHA-256 values unchanged by review fixes, equal on Linux and Windows.

### Relay144 (2026-11-03, cloud)
- P1-05 Attempt1 (run 41661234606, 48c21ae, native-build only): hypothesis the core typed-value record names are free in the native compile → wrong, `IntegerParameterValue` clashes with `Autodesk.Revit.DB.IntegerParameterValue`; renamed the seven records to `*ValueArg` (76082c4, green 41662074736). Review round: no failed attempts.
- P6-04 Attempt1 (run 41662294013, 3ca8990): CS7036 — a bulk replace of ", -1, " also dropped the segment-index argument of one Register call → restored (76f48e7). Attempt2 (run 41663209845, 76f48e7): "dxf west wall" expectation copied the north wall ratio 5600/6000; the correct hand value is 3600/4000 = 0.9, which the code returned → expectation corrected, still exact (1de790e). Review round Attempt1 (run 41671381838, b1e8271): CS1061, `FinalWall.MergedSharedFace` property missing → added (7c80bc0).
- P7-03: no failed attempts. Review round: the canonical JSON SHA-256 pin was pushed with a PENDING placeholder to read the digest from CI annotations (run 41674359546; same digest on Linux and Windows), then pinned (f4895e2) — a deliberate read, not an oracle change.

### Relay145 (2026-11-03, cloud)
- P1-06 Attempt1 (run 41694113303, 25be556, Linux only): hypothesis the journal.read wire client in the test's pwsh reaches the fake pipe under .NET's Unix pipe path → wrong, pwsh fixes the CoreFxPipe path before the test sets TMPDIR; off Windows the client now connects to `<temp>/CoreFxPipe_<name>` directly with retry (833b8d2, green 41695576582). Review round Attempt1 (run 41703771852, 943dc87): the test row filter `recorte*` also matched the existing `recorte activo` row → filter `recorte:*`/`origen:*`; code unchanged (6316dac, green 41704794976).
- P5-06 Attempt1 (run 41691025636, 3193351): the S5 assertion looked for the literal "to confirm" in the dormitorio citation, which the owner's confirmation removes → assertion now checks dormitorio/bedroom say "confirmed by the owner" and escritorio/estudio/biblioteca still say "to confirm" (new truth, not relaxed; f526353). Review round: no failed attempts.
- P10-05, P2-04: no failed attempts (initial and two review rounds for P2-04; a cancelled macOS ifc-portable job was re-run, not a failure).

### Relay147 (2026-11-04, cloud)
- P8-05 Attempt1 (run 41736950234, 3de74ab): hypothesis IfcMappedItem geometry is MappingTarget ∘ inverse(MappingOrigin) (ISO 10303-43 mapped_item reading) → wrong against the IfcOpenShell 0.9.0.post1 kernel, which composes target ∘ origin (world y 7..11 vs importer 5..9). Non-identity MappingOrigin is now reported unsupported_representation instead of guessed; identity-origin mapped walls stay exact and kernel-checked (1e61958, green 41738076157). The test's expectation for that ambiguous case changed from imported to unsupported (narrower, not a relaxed check). Review round: no failed attempts.
- P3-04: no failed attempts (initial red 41736650256 from a NotImplementedException stub; an earlier compile red 41735421446 from an unused test helper, removed in a test-only commit). Review round added tests only against the reviewed implementation (green 41743664466; a sound implementation cannot go red on them).
- P9-06 (not merged; duplicate of the owner chat's chat-materials-types): Attempt1 (run 41736758909): a core comment naming the vendor namespace tripped the harness core-boundary gate → reworded (bbf4049). Review round: no failed attempts.

### Relay148 (2026-11-04, cloud)
- P4-03: no failed attempts. Reds from NotImplementedException stubs (41751742782) and from stricter review oracles (41755088266, 41760520985), each followed by one green fix (41752470200, 41755567519, 41761011983). Second review round replaced a minimality check that restated the implementation's own rule with a strict content-differs oracle (stronger, not relaxed).
- P6-05: no failed attempts. A first red 41751195235 was a test compile error (Point2 in both Bim.Cad and Bim.Core; alias added in the test), not an attempt. Assertion reds 41752281978, 41756681420, 41761968917 each followed by one green (41752953004, 41756968524, 41762098208). One existing expectation changed with the review's sign rule: "Comedor -12 m²" → "Comedor: -12 m²" (a spaced dash is now a separator, per review blocker).
- P7-04: no failed attempts (red 41751286259 → green 41752337010; review red 41758412039 → green 41758872020). Two own earlier expectations changed with new review rules (unreachable dry room also fails circulation_passage; two-level case now Valid with soft circulation_unsupported).

### Relay149 (2026-11-04, cloud)
- P10-04 Attempt1 (run 41771209622, 1697a7d): hypothesis PowerShell can call the generic JsonSerializer.Serialize with one argument → wrong ("Cannot find an overload for 'Serialize'") → ConvertTo-Json -InputObject -Compress (green 41771650673). An earlier run 41770168338 did not reach the test (verify.ps1 hook missing; a literal private-key header tripped the credential scan) — not an attempt. Review round: 0 attempts (red 41773123609 → green 41773791843).
- P2-05 Attempt1 (run 41770982087, 77eb478): hypothesis a core comment naming the native executor path is harmless → wrong, the harness core-boundary scan rejects any core source mentioning runtimes/ → comment reworded (green 41771382126). Review round: 0 attempts (red 41774729673 → green 41775145810).
- P5-05: no failed attempts in the first round (red 41770682412 → green 41771404693). Review round Attempt1 (run 41776470479, a057b80): the trace test still expected pack version 1.0.0 after the bump to 1.1.0 → expectation updated (2fecab7, green 41777099861); no bound relaxed.

### Relay150 (2026-11-04, cloud)
- P1-07: no failed attempts (red 41783202390 → green 41786172875; review red 41788320098 → green 41789389908). Two own slips, not hypotheses: CS8604 from a cross-partial static initializer (41783994697) and reused JSON nodes in a test helper (41785135196).
- P9-04: no failed attempts (red 41782777772 → green 41783619497; review red 41790838549 → green 41792117807).
- P8-07 Attempt1 (run 41795229811, 9ecdfe6): hypothesis the test validator's outward-face check can use one centroid for the whole wall mesh → wrong once a full-height door splits the wall into separate closed pieces → the validator checks each connected piece against its own centroid (stricter; a flipped face in a two-box mesh is still caught), green 41796553365/41797924226. Earlier red 41782803843 intended; 41783906713 was an unstaged test edit.

### Relay151 (2026-11-04, cloud)
- P4-04: no failed attempts (red 41804430030 → green 41805667983; review red 41808247828 → green 41809487719). One compile slip (checker passed as Action without the optional detail argument, run 41803509993), not an attempt.
- P6-06: no failed attempts (red 41804007889 → green 41806106763; review red 41810785252 → green 41811167558; second review red 41815426981 → green 41815912317). One fixture slip (999 comment outside any section, run 41805207603). Review correction, not a relaxation: the original oracle expected non-constant ATTDEF defaults to be drawn through inserts; AutoCAD never draws them, so the expectation was replaced by the stricter "no room from an ATTRIB-less tag" checks.
- P7-05 Attempt1 (run 41813323962, 7c4aa29): hypothesis exact never-undershoot whole-mm sizing could compare required area directly; failed because the pack's 0.1 is stored as a slightly larger double (2.4000000000000004 m² → 2.401 m). Fix a1dc1af: width test uses the same 1e-12 relative slack as the post-placement check (oracle uses 1e-12 too, far tighter than P5-02's 1e-9). Green 41814107348; final round red 41816874404 → green 41817395508. The earlier area_m2<=5 applies_if expectation changed with the review rule (an evaluable unmet condition drops the rule, as P5-02).

### Relay152 (2026-11-04, cloud)
- P2-06 Attempt1 (run 41826859979, 717f695): hypothesis the chain-refusal checks could compare the full `code#entry@detail` string and a digit-only GUID would exercise the canonical-case check → wrong on both counts (the helper appended the detail; upper-casing digits changes nothing) → checks compare code + entry, case check uses a lettered id and now expects exactly provenance_not_canonical (stricter). Green 41828338507; review round red 41830562019 → green 41830824151, 0 attempts.
- P10-06: no failed attempts (red 41826892892 → green 41828339068; review red 41832627501 → green 41833656639). One compile slip (two-argument check vs Action<bool,string,string>, run 41825543973).
- P5-04: no failed attempts (red 41825281521 → green 41826002911; review red 41835551718 → green 41836125195/41837332397). One compile slip (int? Storey used by the old fill, run 41835153795).

### Relay153 (2026-11-04, cloud)
- P1-08: no failed attempts (red 41843728108 → green 41845412339; review red 41853561811 → green 41854608933). One harness slip (MCP test reused a JSON node across two calls, run 41844723828), not an attempt.
- P8-06 Attempt1 (run 41846579340, 16e41b8): hypothesis arcs reach rhino3dm as ArcCurve → wrong, rhino3dm 8.17 stores arcs/circles added through Objects.AddArc/AddCircle as rational NurbsCurve → arcs recognised by shape (ArcCurve, or degree ≥2 NURBS that is a level circular arc on 33 samples). Green 41847657825. Review rounds red 41858210496 → green 41861079576/41862605911; red 41873773325 → green 41875504076 (0 attempts). Slips: test used a second bim_rhino module copy (41844495904), ArcCurve constructor missing (41845441176), rhino3dm segfault exit 139 once (41871976938; run.py now runs the suite in a child process with faulthandler). Integration run 41882159923 hit the same rhino3dm segfault in the block-definition test construction (InstanceDefinitions.Add after a model with a block was written in the same process; probe 41888222643) → each block file is built in its own child process, assertions stricter, 50-construction stability test; green twice 41889889497 (0 attempts, test construction only).
- P9-07: no failed attempts (red 41845762559 → green 41847684744; review red 41850833106 → green 41851839555; re-review red 41869343965 → green 41870326635). Compile slips: nullable warning in the red stub and missing ExtrusionRoof.ExtrusionStart/End in the 2025 API (run 41844805513); order check + JSON node reuse in tests (41846750236).

### Relay155 (2026-11-04, cloud)
- P4-05: no failed attempts (red 41902544798 → green 41904414374; review red 41913071671 → green 41917253830).
- P6-07 Attempt1 (run 41904029640, 4846ba3): hypothesis the CLI verbs only need the Bim.Cad project reference → wrong, scripts/package.ps1 copies a fixed source list without adapters/cad/Bim.Cad (CS0234 in packaging-reproducible) → added to package sources (5bdad8e), green 41905973151. Review red 41918971069 → green 41920420097 (0 attempts; the new cli/Bim.Cad.dll package requirement is a regression guard, it could not go red first).
- P7-06: build slip (run 41904526611, nullable warnings under --warnaserror), not an attempt. Attempt1 (run 41907009597): hypothesis every ranked prototype plan compiles through IntentCompiler with 0.15 m walls → false, TwoBedroom 8.66x20 alt 3 has a 2.66 cm stagger refused as wall_clash (diagnostic run 41908754385) → such plans are an explicit typed refusal, independent stagger finder in the test, other prototypes must pass every check; green 41910638575.

### Relay156 (2026-11-04/05, cloud)
- P2-07: no failed attempts (red 41930130294 → green 41931899995; review red 41933672582 → green 41934750332). One compile slip (CS0136 shadowed `steps` local, run 41931282202), not an attempt.
- P5-07: no failed attempts (red 41929913861 → green 41931202221; review red 41935947819 → green 41937157242).
- P10-07: no failed attempts (red 41929591249 → green 41930792081; review red 41938591628 → green 41938674297).

### Relay157 (2026-11-05, cloud)
- P1-09 Attempt1 (run 41952461590, 262a028): hypothesis nearest-first global pairing maps every copy to its source → wrong when all copies are 0.5 mm off (a source is stranded after its nearest copy is taken by a neighbour) → nearest-first plus augmenting paths (bipartite match within 1 mm), green 41953640792. Reds 41945946857/41946946583/41948185837 (stubs) and review red 41951385049 were intended.
- P3-05 Attempt1 (run 41946548655, 78784b7): hypothesis edited elements can be serialized before validity checks → wrong, System.Text.Json throws on NaN before the invalid_ir refusal → checks moved before serialization, green 41947771055; review red 41955010456 → green 41956142359.
- P9-09: no failed attempts (red 41945858175 → green 41946817036; review red 41957340069 → green 41959286079). One slip (schema placeholder @5@ for @4@, run 41958353461), not an attempt.

### Relay159 (2026-11-05, cloud)
- P4-06: no failed attempts (red 41965712152 → green 41966585544; review red 41973671243 → green 41974540346). One compile slip in test code (CS0165, run 41964999967), not an attempt.
- P6-09: no failed attempts (red 41965721659 → green 41966656787; review red 41968502384 → green 41969132742). One syntax slip (run 41965047303), not an attempt; baseline digest probe 41968339713 on throwaway branch relay159-P6-09-baseline.
- P7-07: no failed attempts (red 41965200434 → green 41965816230; review red 41971196839 → green 41972006351). One invalid red (test helper generic constraint, run 41970416543), not an attempt.

### Relay160 (2026-11-05, cloud)
- P2-08 Attempt1 (run 41986782413, 19cd27a): hypothesis the existing MCP test pins the core tool list from the injected catalogue only → wrong, tests/mcp/Program.cs:133 hard-codes the core tool names → mirror_drift added to that expected list (oracle not relaxed), green 41987958037; review red 41989771640 → green 41989880727. Compile slip in the first red (41984282875), not an attempt.
- P5-08: no failed attempts (red 41983932849 → green 41985065469; review red 41991496133 → green 41991684029).
- P10-09: no failed attempts (red 41983890324 compile red → green 41984918492, one compile slip 41984050644 (test locals clashing with top-level names), not an attempt; review red 41993013697 → green 41993168316).

### Relay161 (2026-11-05, cloud)
- P1-10: no failed test attempts (red 42000494415 → green 42001156947; review red 42005842348 → green 42007394205). Compile slips, not attempts: top-level name clash in tests/mcp (41999434457), doubled quote in a description (42005209599), pattern variable collision (42006756913).
- P3-06: no failed attempts (red 41998448544 → green 41999504722; review red 42009289955 → green 42010080321).
- P8-08 Attempt1 (run 42000960012, 18620b7): hypothesis the validator anchors a duplicate GlobalId on the first instance (the wall) → wrong, the anchor is the door on ubuntu/windows and the wall on macOS → test accepts either duplicate and still requires unique_rule, IfcRoot.UR1 and both STEP ids; green 42002056141. Review red 42012119306 → green 42012222589 (0 attempts).

### Relay162 (2026-11-05, cloud)
- P4-07: no failed attempts (red 42023285220 → green 42024173720; review red 42029523294 → green 42030387084). One compile slip in test code (`.Count` on an array, run 42028762984), not an attempt.
- P6-08: no failed attempts (red 42023139520 compile red → green 42024176366; review red 42033774848 → green 42034941467). One invalid fixture (all-Cyrillic labels are not extracted by P6-05, run 42032518982), replaced by Latin+non-ASCII labels; not an attempt (gap → P6-11).
- P7-08: no failed attempts (diagnostic digest run 42022995164, reverted; red 42024783818 compile red → green 42025388127/42026612209; review red 42037618668 → green 42038221886).

### Relay164 (2026-11-05, cloud)
- P2-09: no failed attempts (red 42050267331 → green 42053306609; review red 42057004079 → green 42059574519).
- P5-09: red 42050527620 → green 42051621858; review red 42061959924. Attempt1 (run 42062766941, a55d8a2): hypothesis the static "pin writes only through an exclusive create" check tripped on a new comment naming `SkillLockFile.Write` → confirmed, comment reworded, check unchanged; green 42063596832.
- P10-08: red 42050344726 → green 42051827079; review red 42067515495. Attempt1 (run 42068613144, db392f39): relative-path test saw a non-zero `$LASTEXITCODE` although outputs landed correctly; hypothesis the script ended without `exit 0` so in-process callers read a stale exit code → confirmed; green 42070078862.
