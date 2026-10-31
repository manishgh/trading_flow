# Drawdown completion verification — 18 October 2026

Checkpoint: component implementation plus diagnostic research execution, in the
existing `research/swing-pit-review-20261008` checkout. Earlier uncommitted work,
operator state and published research artifacts are preserved. The
[completion contract](../research/drawdown-qualification-completion-2026-10-18.md)
defines the exact matrix and remaining evidence limits.

## Delivered and checked behavior

Sector, evaluated-event and history context now reach the shared decision kernel
through both BacktestRunner and LiveRunner. Frozen manifests validate evidence
references, identities, source hashes and availability bounds. Context analysis
checks completed aligned sector sessions, effective mappings, independently
evaluated classifier/revision/coverage evidence and historical identity/calendar
requirements. Required missing inputs remain unknown and fail closed at the
original trigger. Default strategy admission and semantic identities are retained.
The actual evidence audit found no dataset adequate for sector, event or matured
history economic arms; those arms have no claimed performance result.

The source-binding repair is applied. Independent frozen price bundles and exact
ordered completed-prefix hashes bind declared security/adjustment identities to
the consumed bars, count, last source-bar timestamp and availability. Missing or changed
bindings remain unknown. Hash agreement does not authenticate historical provider
claims. The staged implementation applied 15 files after the parent matrix,
followed by the 268-check pass below. The research engine has not changed since
that pass; external supplemental-driver repairs and the newly observed Earnings
date-scope repair are separate. The price-only
parent matrix uses no identity provider and is unaffected.

Price-only study preparation freezes a shared input-only q90 depth bound,
upper-half-close pressure bound and separate immutable availability masks. The
100-attempt registry includes both availability-only controls and full controls,
preserves all five original cohorts and excludes SOXL explicitly at the user's
request. Attempt reservation, source/config/code hash checks, serial execution and
exclusive result publication enforce the 120-attempt ceiling. Monthly source
bindings prevent stale or changed results from being silently republished.

Independent reviewers `completion_quant_code_review` and
`completion_adapter_design_review` approved the final 100-attempt pre-run design
after repairs. Review regressions cover provider-source revision ordering,
out-of-window events, monthly source-hash checking and exclusive publication.
The review amendment archived the zero-attempt 60-run preregistration before the
100-run replacement was frozen. No outcome-driven threshold amendment is claimed.

## Automated verification

The initial focused C# pass completed **73 checks**, including the three review
regressions. The pre-stage affected-project regression pass completed **241
checks, zero failures**, approximately 21 seconds. Its initial compilation was
reused with `--no-build`. After the reviewed source-binding and opportunity-shadow
changes were applied, the affected projects rebuilt and passed **268 checks, zero
failures/skips**, approximately 23 seconds. Coverage includes
drawdown/context/configuration, request adapters and runner consumers,
candidate qualification/state, completed-bar timing, fine execution, portfolio
valuation/monthly publication, order planning, persistence and affected News paths.
These are separate checkpoint counts, not a sum of repeated executions. The
268-check pass adds actual price-prefix binding and independent shadow-replay
regressions. Its exact argument list and compiler/test output are retained in
[post-stage-regression-command.json](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/post-stage-regression-command.json)
and [post-stage-regression.log](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/post-stage-regression.log).

The earlier 241-check invocation is retained as `final-regression-command.txt`
in the artifact root; the later 268-check invocation above supersedes its final
source-verification claim:

```powershell
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~DrawdownQualificationTests|FullyQualifiedName~DrawdownConfigurationRoundtripTests|FullyQualifiedName~NewsCategoryTests|FullyQualifiedName~NewsCategoryUiTests|FullyQualifiedName~PublicContextNewsTests|FullyQualifiedName~OfficialMarketNewsProviderTests|FullyQualifiedName~NewsStockSourceTests|FullyQualifiedName~GroupNewsQueryTests|FullyQualifiedName~NewsPresentationTests|FullyQualifiedName~StrategyDecisionKernelTests|FullyQualifiedName~StrategyCandidateDecisionOrchestratorTests|FullyQualifiedName~StrategyCandidateStateMachineTests|FullyQualifiedName~MixedTimeframeDecisionTimingTests|FullyQualifiedName~SwingFamilyConfigurationTests|FullyQualifiedName~FineExecutionReplayTests|FullyQualifiedName~EntryDecisionPersistenceTests|FullyQualifiedName~LiveRunnerIntegrationTests|FullyQualifiedName~MobileAutomationServiceEntryGateTests|FullyQualifiedName~BacktestSimulationCharacterizationTests|FullyQualifiedName~CompletedBarExecutionPlannerTests|FullyQualifiedName~StrategyOrderPlannerTests|FullyQualifiedName~NewsFeedServiceTests|FullyQualifiedName~EarningsNewsRepositoryTests|FullyQualifiedName~StrategyCandidateState|FullyQualifiedName~SharedDecision|FullyQualifiedName~DrawdownContextTests|FullyQualifiedName~DecisionContextProviderTests|FullyQualifiedName~StrategyDecisionKernelTests|FullyQualifiedName~StrategyDecisionRequestAssemblerTests|FullyQualifiedName~RunConfigWriterTests|FullyQualifiedName~SimpleYamlReaderTests' --verbosity minimal
```

Final assessment and paired-analysis Python verification passed **28 checks
(24 assessment + 4 paired-analysis)**. These checks cover publication/source and
attempt bindings, failed101/102 and v3 lifecycle accounting, missing coverage,
separate monthly identities and accepted/rejected/unknown outcome distributions.
The exact final assessment and its publication receipt are retained under
`analysis-source/final-snapshot-final`; no additional economic attempt was used.

The documentation task itself runs no build, test, provider request, database query
or process mutation. Final document whitespace and link checks are performed
separately.

No full release suite, live-order experiment, Android build, holdout advancement or
promotion check is claimed. This is a component checkpoint under the repository's
risk-based verification policy. The completed diagnostic matrix supplies its
representative strategy replay. Parent attempt/publication totals are complete
below; the final supplemental assessment and independent final quant review are
complete. Later Earnings-date verification is a separate affected component pass.

## Actual operator News and Earnings

The local web application uses the real operator data root
`C:/project/trading_flow/data`, with `TRADINGFLOW_DISABLE_RUNTIME=true`,
`TRADINGFLOW_ENABLE_MARKET_FEEDS=true` and `TRADINGFLOW_UI_TEST_MODE=false`.
The web endpoint uses port 53001. Process IDs are restart-specific; the final Web
PID and live service state are recorded in the post-repair verification receipt. This is the
operator dataset, not a verification database. The user
expects the application to remain available, so these services are intentionally
kept running at completion. Web was stopped during the date repair, then restarted
and verified; ngrok was retained. Process identities and the public URL are observations
from this checkpoint and must be rechecked after a restart.

The reviewed public URL is
[TradingFlow operator desk](https://9a08-2001-1c00-820b-7600-a565-6712-f63f-b87a.ngrok-free.app).
The earlier browser check found 25 deduplicated four-hour News records. The
refreshed actual view shows **30 deduplicated News stories from 44 records**,
three Global stories, the union source view with 5,619 beside the stock panel,
and **21 Earnings events for 19 October**. Categories and source controls were
checked in the signed-in operator browser. Evidence includes
`operator-news-final.jpg`, `operator-news-sources-final.jpg` and
`operator-earnings-final.jpg`; earlier screenshots remain preserved. A final
post-restart browser/service proof below supersedes the earlier observations.

At **22:09 UTC**, `operator-observation-final.json` retained the original hashes
for two wishlists, 30 items and two presets. News had a **22:05** provider timestamp;
Earnings had a **21:57** receipt and report dates through **19 October**. At **22:10
UTC**, the actual browser showed **three Global articles/five records**, union
source count **5,608** and fresh Finviz count **5,606** beside the stock panel,
with **two groups, two wishlists and 38 Finviz lists**. The earlier 5,619 figure was
a previous observation. `operator-news-completion.jpg` preserves this final News
view. The
[latest launch receipt](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/operator-web-launch.json)
records the process identity at launch; actual post-repair availability is recorded
in the verification receipt below.

**Completed Earnings clock repair:** after Berlin midnight, the live browser exposed
a date boundary error. At Sunday 18 October 22:10 UTC, New York was still Sunday
but Berlin was Monday. Operator-local next-business-day logic selected Tuesday
20 October/zero events instead of Monday 19 October/21 events. The scoped repair
uses New York dates for Earnings short/custom-date API scopes, exchange report-date
grouping and JavaScript day headings. Detail timestamps retain local/UTC display;
the separate intentional operator-local News weekend policy is unchanged. The
[independent date-fix review](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/earnings-exchange-date-independent-review-20261019.json)
approved the change with no P1/P2 finding. The
[date-fix verification receipt](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/earnings-exchange-date-verification-20261019.json)
records the affected build and **111 focused tests passed, zero failed/skipped**,
including **11 new date-boundary cases**, plus `node --check earnings.js` success.
API and browser show **Monday 19 October, 21 events**, monitoring active and last
analysis at **22:16 UTC**. Canonical strategies and the price engine are unchanged;
this query/presentation correction required no economic replay. The 268 research
C# pass, 28 Python checks and later 111 Earnings checks are separate overlapping
checkpoints and are not added into one total.

The final
[post-repair source observation](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/operator-observation-post-earnings-repair.json)
at **22:16:51 UTC** retains identical hashes for two wishlists, 30 items and two
presets. News is fresh through **22:10 UTC**; Earnings receipt is **22:15:36 UTC**
for reports through 19 October. Final browser evidence shows three Global
articles/five records, union 5,608/Finviz 5,606 and the right stock panel. The
receipt verifies Web PID 103604 and ngrok PID 88868 at the same public URL, with
trading paused and market feeds enabled. Screenshots are
[operator-news-closeout.jpg](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/operator-news-closeout.jpg)
and [operator-earnings-completion.jpg](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/operator-earnings-completion.jpg).

`operator-observation-before.json` and `operator-observation-after-start.json`
retain the same hashes for both wishlists, all 30 wishlist items and both saved
screener presets. News has a fresh 18 October provider timestamp and Earnings has
18 October receipt times with report dates through 19 October. Source audit counts
captured before startup are inventory observations; they do not represent the
refreshed operator view. Operational feed refresh is authorized independently of
the paused trading runtime. No broker orders or strategy activation occurred.

## Published artifacts and final receipts

Artifact root:
`C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018`.

| Evidence | Retained files |
|---|---|
| Actual inventory | `source-evidence-audit.json`, `source-evidence-audit.md` |
| New real source capture | `capture-receipt.json`, `frozen-source-file-hashes.json`, `replay-calendar-coverage.json` |
| Frozen study and threshold | `study-freeze.json`, `depth-q90-freeze.json`, `depth-training-samples.json`, `feature-coverage.json` |
| Eligibility and execution bindings | `feature-availability-masks.json`, `trial-registry.json`, `executed-code-hashes.json`, strategy/config manifests |
| Earlier zero-attempt registry | `preregistration-v1/amendment-receipt.json` and archived original artifacts |
| Each attempted treatment | `attempts/`, `inputs-*.json`, `summary-*.json`, `opportunity-audit-*.json`, immutable results |
| Operator operation | before/after observation receipts, restart launch receipts and earlier/refreshed screenshots |

## Completed parent matrix and retained failures

All **100 parent attempts** finished: **74 complete, 26 incomplete, zero parent
job failures**. Their full monthly publication contains **518 usable rows**;
the assessment retains all **700 rows**, including unavailable outcomes, with
**196 valid comparisons and 84 withheld**. The
[independent parent matrix review](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/independent-quant-parent-matrix-review-20261018T2143169186335Z.json)
recomputed every published monthly return/drawdown and available comparison and
found no P1/P2 issue. BKSY's 26 August 16:15 UTC execution gap affects eight arms;
DIOD's 21 May 16:00 UTC gap affects 18. Their entire incomplete-arm monthly results
remain unavailable. First-entry-interval protection and quote evidence limits
remain explicit.

The immutable
[parent monthly review](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/analysis-source/parent-monthly-review.md)
contains separate family/cost/month/cohort return tables, gate-versus-availability
deltas, availability effects and exact strategy ID/version/hash mappings.
Findings are mixed; disposition is **RETAIN_RESEARCH**, with no winner selection,
retuning, pooled return or promotion claim.

`analysis-source/paired-final-pre-shadow` retains **828 intent-label rows and 196
monthly groups**, covering 20 published control/cohort/cost runs. Its labels are
gross fixed-horizon price descriptions with original qualification masks;
strategy profits, losses avoided and discarded winning opportunities remain
unavailable from those intent journals alone.

Candidate-intent audits do not provide independent strategy fills and exits;
the separately published shadows now provide their declared execution outcomes,
coverage and missingness. Fixed-horizon feature labels cannot replace those
strategy outcomes. Supplemental
attempt 101 failed before economics during JSON parent-summary reading; attempt
102 failed after calculations while hashing an open independent execution journal
for publication. Both failures and artifacts remain preserved and consumed, with
no retry and no successful shadow-publication claim. Their incident receipts are
`shadow101-incident-receipt.json` and
`shadow102-failure-artifacts-receipt.json`. The reviewed external-driver v3 repair
completed the other **18 distinct passes**. All **120/120 attempt starts** are
accounted for: **100 parent publications, 18 shadow publications and two failed
shadow passes**. The 118 successful artifact publications include the 26
economically incomplete parent arms; they are not 118 complete portfolios. No
further replay or retry is available under this ceiling.

The
[final assessment](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/analysis-source/final-snapshot-final/assessment.json)
retains **176 independent shadow monthly groups** in the
[shadow CSV](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/analysis-source/final-snapshot-final/independent-shadow-monthly.csv),
including accepted/rejected/unknown distributions and incomplete outcomes. Failed
RSI5 C1 passes at both costs remain unavailable. Complete rejected-dollar totals
stay null when outcomes or coverage are missing. The immutable
[final readable review](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/analysis-source/final-review.md)
preserves parent monthly tables and gives explicitly illustrative individual
month/family/cost/cohort rejected-positive and rejected-negative examples. No
opportunity dollars are presented as pooled portfolio returns.

The [final independent quant receipt](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/independent-quant-FINAL-results-review-20261018T2213478494879Z.json)
approves **RETAIN_RESEARCH with no P1/P2 findings**. It verifies 402 published
triggers (378 completed independent outcomes, 18 incomplete and six trigger-only
unavailable), all 402 original/rerun semantic-plan matches, 774 journal fill events,
176 monthly groups and 528 per-verdict descriptive distributions. It also verifies
84 human monthly rows, 708 scalar cells, 20 exact identities, four shadow examples
and six report source bindings. This is a numerical/results review; operational
approval comes from the separate actual date-fix/browser evidence above.

The [owned-input cleanup receipt](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/owned-input-cleanup-receipt.json)
records two removed owned directories: 47 raw-input files (**70,778,473 bytes**)
and two Python cache files (**62,803 bytes**). **Zero owned database files remain**.
All 118 successful publications, both failed attempts, immutable parameters,
configurations, strategies, source manifests and compact audit are preserved.
Operator data was untouched; requested Web/ngrok services remain running.

Complete terminal portfolio nonfill metrics remain null in the parent assessment.
Historical context data, missing execution observations and calibration, and
untouched validation remain unavailable evidence, separate from completed
implementation and bounded execution. The immutable
[current completion handoff](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/completion-handoff.md)
resolves time-specific open items in the earlier immutable report snapshots.
No Android, GitHub synchronization, deployment, broker activation or promotion was
performed by this checkpoint.
