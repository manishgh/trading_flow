# Technical/news paired-research audit — 21 October 2026

Status: read-only source and retained-result investigation; no new economic arm,
classification, provider request, database write, build or test. This document
records the next concrete choice; it does not enable a rule or weaken evidence gates.

## What can run through the existing shared workflow

The current `configs/strategy-catalog.json` exposes Connors RSI2 6.0.0 and
Minervini VCP 10.0.0 as Research artifacts. Neither has a news admission rule.
Normal BacktestPlan preview/save and run configuration generation resolve exact
catalog identities. The published recovery artifact and archived catalyst drift
recipe are not selectable BacktestPlan choices.

The shared profile enables Alpaca news collection with a 500-item per-ticker
budget, three-second sentiment deadline and configured veto defaults. Collection
does not apply a veto to a strategy that has no corresponding entry rule. Turning
collection on therefore does not form a technical-plus-news treatment.

`BasicStrategyEvaluator` already supports positive-news requirements, a negative
sentiment veto and a fresh-news veto. `AlpacaNewsProvider` preserves the actual
receipt and sentiment classification times, setting decision availability to their
maximum. `CatalystSnapshotAttacher` respects that availability. An October19 or
later capture/classification cannot change an April–October historical decision.
The provider also has finite page/article limits; an incomplete query is not an
absence of news. Its ordinary attachment retains only the latest available item,
which is different from the all-relevant-event adverse-context gate.

The existing historical resolver can reconstruct diagnostic news availability at
the next captured RTH open strictly after update plus 24 hours. It is an offline
evidence component, not an input adapter selected by the normal BacktestPlan news
provider. The ordinary plan/API has no accepted reconstruction-policy or news-rule
override that makes this a paired historical experiment without a change.

Consequently no unchanged, currently selectable technical/news pair was found.
Running identical technical definitions with collection enabled would not measure
an entry-rule effect. Reactivating the archived generic-sentiment strategy would
change entry, exits and lifecycle as well as news, and is not a matched control.
The old ReversionResearchAnalyzer is a gross event study with publication-time
fallback and dictionary-presence coverage; it cannot substitute for this contract.

## Existing archive scope and exact limits

Source root:
`C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/data-gap-collection-20261019`.
The company-query summary contains 21 original stock symbols plus separate ETF
context queries. BXDC, FPS and SIVE are not company-query scope. The historical
capture was made October19 for the earlier April2–October7 query window; it does
not establish the newer study's October7–October21 interval. No current provider
request was made during this audit, so no provider-unavailability claim is made.

The following counts come from the five saved `normalized/news/stocks-*.json`
files, deduplicated by provider article ID and update timestamp across those files.
They count saved versions whose provider symbols include the row symbol. Stories
can match multiple symbols, so these rows must not be summed into an article total.
This audit does not certify conflicting-content resolution or first delivery.

| Symbol | Saved ID/update versions | Symbol | Saved ID/update versions |
|---|---:|---|---:|
| ASTS | 219 | BKSY | 37 |
| CRCL | 216 | CRDO | 132 |
| DASH | 129 | DIOD | 15 |
| MIAX | 19 | MRVL | 465 |
| MVST | 15 | MXL | 85 |
| NBIS | 337 | NVTS | 90 |
| OUST | 63 | PATH | 86 |
| PGY | 18 | POET | 76 |
| RDW | 129 | RGTI | 81 |
| RKLB | 366 | SMCI | 344 |
| STM | 54 | | |

These are useful inputs for reviewing what the provider returned around particular
decisions. First/last article timestamps and zero matches cannot certify interval
coverage, an affected-company relationship, or absence of adverse information.
ETF tags cannot replace historical company-sector membership.

The separate marketwide summary retains 122,969 article versions, including
85,400 headline-only and 37,569 body-supported responses. Source article 61242877
remains quarantined because update precedes publication. Raw responses, receipt
files and archived content are preserved. Existing collection summaries and
independent October19 audits record HTTP200 responses, complete declared pagination
and physical source/hash checks; this checkpoint did not revalidate every page.

The saved `strategy-validation-closeout-20261019/historical-evidence-v1` already
contains the manifest, claim dispositions and availability diagnostics. Its policy
is `captured-provider-version-next-session-24h.v1`, SHA256
`1328ab10a0e438d11a7ea013eda2d5817038dab7b4d46fb84fe1f2ac034a5d63`.
The manifest counts 122,969 news facts (one quarantined), 24 current assets,
21 current sectors, 104 corporate actions and 42 current-vintage earnings facts.
Its diagnostics explicitly leave original news delivery/revision lineage,
classifier human evaluation, old earnings schedule/consensus, dated sector and
share-class identity, and decision-time adjustment factors unproved. Calendar-end
unknowns remain explicit. Those source facts are not evaluated trading features.

## Classifier and human-review evidence

The frozen `economic-news-research-rules-v1` predictions are research predictions.
Rules include negative guidance and some operational disruptions, retain text
support and ambiguity, and leave materiality unknown. No completed independent
economic accuracy evaluation was located within the inspected October19 closeout,
its pilot/assessment artifacts, relevant research/verification documents and current
October21 output bundle. Unit tests measure program behavior, not market accuracy.

Reuse `strategy-validation-closeout-20261019/news-review-25.xlsx` in the common
workspace outputs. Its current hash is still the original blank export:
`cbcc3a954a765ccb9cd01da21dfc5faac3d938299e03ede2617ee3138c8ee8ed`.
The saved blank import has zero labels and 25 unreviewed stories. Existing
`news-category-pilot import` accepts partial human categories and rejects changed
source cells/formulas. This pilot has no human direction/adverse labels and cannot
satisfy evaluated-event admission. No new packet or 500-story assignment was made.

## Preserved recovery results, reconciled without rerunning

Parent: `technical.family03.mr_a0b1@1.0.0`, SHA256
`c151cb4becae9f19b0aeeab7517c884401f14d716fbc404e78bf8dfa4b8396bb`.
Original files remain under `outputs/swing-family-cycle03-20261009` in the common
workspace. Exact-hash filtering selects seven marked-to-market monthly rows per
cohort in `monthly/monthly-diagnostics.json`. Closed counts across those rows
reconcile to each retained `summary-mr_a0b1-b0N.json` accepted count:

| Original cohort | Candidates | Accepted/closed | Portfolio rejected |
|---|---:|---:|---:|
| 1 | 27 | 17 | 10 |
| 2 | 30 | 20 | 10 |
| 3 | 57 | 33 | 24 |
| 4 | 33 | 25 | 8 |
| 5, including SOXL | 20 | 13 | 7 |

Monthly net marked return, percent of that independent book's opening equity:

| Original cohort | May | June | July | August | September |
|---|---:|---:|---:|---:|---:|
| 1 | 0.542 | -1.567 | -1.530 | 1.726 | -1.609 |
| 2 | 0.908 | 0.549 | -2.158 | 0.961 | -1.766 |
| 3 | 2.918 | 2.273 | -5.324 | 0.193 | 5.148 |
| 4 | 2.089 | -2.084 | -2.152 | 0.681 | -1.486 |
| 5, including SOXL | 0.654 | 1.069 | -2.233 | 1.057 | -0.048 |

These match the preserved research assessment after rounding. They are historical
cohorts, not a new SOXL-excluded comparison. April/October remain partial in the
retained seven-row-per-cohort data; no accumulated or pooled return is reported.

Cohort3 July has seven closed trades, four stop exits, -5.324% close drawdown,
3.350% mean close exposure and two-day median calendar holding time. NVTS and NBIS
contribute -2.622 and -1.953 percentage points respectively. September has nine
closed trades, 1.229% close drawdown, 9.605% mean close exposure and three-day median
holding time; NBIS and MXL contribute +2.553 and +2.429 points. These concentrations
identify review cases; they do not demonstrate that news explains the outcomes.
Close exposure is not continuous exposure or deployed initial risk.

Cohort4 partial October includes one `scheduled_study_exit`. The original artifact
binds `completed-bar-precommitted-terminal-owned-v5`. Its economics cannot be
rebranded as the new open-at-cutoff policy. Candidate terminal rejection reasons
are ordered rules, not independent filter ablations; hypothetical rejected trades
are not portfolio losses or profits avoided.

## Concrete next comparison decision

### Existing optimizer inspection

`StrategyOptimizer` and `OptimizationJobService` are existing generic components.
They accept an optimization YAML naming a base strategy, backtest configuration,
objective and explicit parameter grid; they bound combinations by the existing
strategy/ticker work budget and reuse prepared market state. The CLI exposes
`optimize <optimization-config-path>`. No optimization profile was found among the
tracked `configs` filenames inspected with `rg --files configs`; the current
shared BacktestPlan contract does not supply an optimizer grid. The Backtests page
lists optimization jobs, but its inspected handlers do not start an optimization.
This is not an already configured bounded search available from the current plan.

The supported parameter paths include several price/risk/exit settings, not a
news classifier, negative-news veto, event qualification or availability policy.
Its objectives include accumulated return/profit, drawdown and win rate, and its
summary retains top-N results. That output is not the required preserved monthly
all-attempt research comparison. Do not silently choose a grid/objective or use
the optimizer as a shortcut around catalog, provenance or monthly reporting.
No optimizer, profile, parameter selection or search was run or added.

### Next evidence and treatment choice

Continue the approved ten technical books and monthly diagnosis using the shared
runner/API. Keep price data repair, news classification and historical availability
as separate issues. Exact archived company stories and the existing25 pilot are
available for bounded evidence review now.

A future technical/news pair needs a reviewed reusable catalog treatment and an
accepted historical input contract. The current catalog/API offers neither an
unchanged news-dependent recipe nor a historical sentiment reconstruction input.
This finding is about the implemented selection/data path, not provider absence.
Do not implement an output-folder driver, manually backdate catalyst times, alter
human evaluation flags or silently register a new recipe to force a comparison.

For evaluated-event research, future A must be a versioned derived price-only
recovery control under the same reviewed execution policy as B/C. B requires the
complete accepted identity/classifier/company-sector-global coverage contract;
C changes one explicitly reviewed rule. Existing broad adverse rejection includes
negative direction as well as the adverse flag and is not category-selective.
The qualifier also checks drawdown observation eligibility. B-minus-A is therefore
the complete required-evidence admission effect, and C-minus-B is the rule effect.
Keep candidate reasons, omitted opportunities and subsequent capital effects
separate; no unavailable arm receives zero economics.

No current archive or small pilot makes classifier accuracy, historical delivery,
historical sectors or profitability established. Additional collection alone cannot
supply independent human assessment. Any new recipe, reconstruction adapter or
rule selection must be concrete and reviewed before an economic attempt.

## Physical artifact hash checks in this checkpoint

SHA256 values recomputed from the exact saved files:

| Artifact relative to the common workspace outputs | SHA256 |
|---|---|
| `strategy-validation-closeout-20261019/news-review-25.xlsx` | `cbcc3a954a765ccb9cd01da21dfc5faac3d938299e03ede2617ee3138c8ee8ed` |
| `strategy-validation-closeout-20261019/news-economic-rules-v1.json` | `1d28ede583510345ac9f6eaf065fb5503b048df95f9dcc8bfbce06c9c2f0da80` |
| `strategy-validation-closeout-20261019/historical-evidence-v1/historical-manifest.json` | `621a46c427af834ec03b59090d2cf1cd504133c05435df6e6b48aa910d01b05b` |
| `data-gap-collection-20261019/news-source-summary.json` | `9b712d0ddaa0bdca1395370eaca8356012194a08bd0abb12eae535c27ee86ec0` |

Verification scope: read-only JSON queries, selected source inspection and these
physical hashes; no executable behavior changed. The sandboxed `git diff --check`
attempt reported that the operation must run in a work tree; that response alone
does not establish a metadata defect. No Git metadata repair or cleanup was
attempted. Parent review remains due.

The subsequent `git diff --check --
docs/research/news-paired-diagnostic-audit-2026-10-21.md` succeeded outside the
restricted sandbox with no output; no Git metadata change was necessary.

## Follow-up: specific missed setups and the next reusable treatment

The following additional diagnosis reads only the exact parent's existing cohort3
summary and `family03-mr_a0b1-b03.candidate-decisions.json`, plus the five existing
parent summaries. Dates below identify stored decision timestamps, not newly
inferred executable fill times.

The displayed missed-move examples can be traced to actual admission decisions:

| Retained example | Decision rows in inclusive stored-date interval | Terminal reasons |
|---|---:|---|
| MXL, April7–May5 | 21 | 2 market-regime-off; 19 recovery setup not triggered |
| NVTS, April7–April24 | 14 | 2 market-regime-off; 12 recovery setup not triggered |

On April7, MXL's audit explicitly has warm market state and eligible universe,
then rejects at `market_regime_off`. The other listed rows stop at the existing
`setup_mean_reversion_reclaim_not_triggered` rule. That aggregate reason does not
identify which stretch/recovery subcondition failed; it must not be restated as
RSI alone, excessive risk, too few slots or bad news. These windows were selected
after their price rises. The existing missed-move code looks up to twenty daily
bars ahead for a high at least8% above the starting close and checks entry presence
in completed trades. Its windows are diagnostic hindsight descriptions, not
unfilled executable recovery orders, independent samples or forgone trade profit.

All recorded portfolio admission rejections for this parent are
`portfolio_overlapping_ticker_position`: cohort1=10, cohort2=10, cohort3=24,
cohort4=8, cohort5=7. Thus there is no recorded slot/capital rejection supporting
an increase in slots or sizing. An already open position in the same ticker is
exposure to that stock, not necessarily a missed investment. Disabling overlap
would add correlated risk and requires its own hypothesis, which is not proposed.

For cohort3 July's seven closed trades, four exits are stops (NBIS, OUST and two
NVTS trades), and three are SMA5 technical exits. One NBIS SMA5 exit has net
profit of -$35.8839; the condition describes price recovery relative to a moving
average and does not guarantee a gain from the entry. The last NVTS stop records
-$131.0934 net despite the nominal1% sizing budget. A sizing budget is not a
guaranteed maximum realized loss when fills gap/slip and fees apply. No news cause
or hypothetical avoidable loss is assigned to these trades.

### Concrete next design for parent/user review, not execution authorization

No new threshold is justified by the two missed-move windows. Finish the approved
native-lifecycle reruns first. If an additional technical comparison is desired,
the smallest interpretable recovery experiment is the already reviewed exit pair,
implemented as reusable Research catalog artifacts under the newly reviewed
execution/cutoff policy:

| Treatment | Exact entry and admission | Only intended economic difference |
|---|---|---|
| Recovery technical control | Parent broad five-bar stretch/reclaim, stock and SPY/SMA200 gates; same RSI2 rank, structural stop, 2.5R target, five-bar maximum, costs/risk/overlap | `exit_on_close_above_sma5: false` |
| Recovery early-exit treatment | Identical resolved entry/admission/execution/risk | `exit_on_close_above_sma5: true` |

`SimpleYamlReader`, `RunConfigWriter`, shared evaluator, ordinary BacktestPlan,
durable worker and native runner already support these fields. The pair needs
two explicit derived catalog identities bound to the actual shared admission
profile; it cannot borrow the immutable old hashes. Proposed bounded budget is
two definitions by five unchanged current cohorts at10bps, ten jobs, after the
current ten-book checkpoint has been reviewed. Use the same April21–October21
exclusive window, source snapshot,260-bar later eligibility,$10,000 independent
books,1% sizing budget,25% notional,four slots and configured fees. Exclude SOXL;
do not replace BXDC/FPS/SIVE or resolve their data status by assumption. Preserve
all attempts and monthly rows, including failures. The existing old-period
contrast remains prior evidence, not an untouched validation set or a reason to
choose a more favorable parameter. A repeated compatibility comparison measures
the exit effect under the repaired execution policy; it does not establish edge.

This proposal requires review/approval of new reusable catalog registration and
derived identities. It introduces no custom runner or optimizer. If the user's
"no new configuration" constraint also excludes reusable strategy registration,
this pair cannot be started through today's common choices; continue diagnosing
the already selected Connors/VCP books instead of silently bypassing selection.

A later news pair can reuse the derived early-exit artifact. Concrete supported
fields are `drawdown_qualification.mode: gate`, `require_event_context: true` for
the evidence control, then identical settings plus `reject_adverse_events: true`
for the broad adverse treatment. Freeze `event_lookback_days: 5` and
`event_max_coverage_age_hours: 24` explicitly if that existing-default contract is
approved; these are proposed availability/lookback settings, not fitted profit
cutoffs. No price bounds, sector-return bounds or matured-episode floor are added.
The comparison must retain the full validated company/sector/global evidence,
bound historical identities/mapping and classifier evaluation; unknown rejects
at the original trigger. An empty event list is accepted only with passing coverage.

That three-arm design remains conditional: A is derived price-only, B is the full
required-evidence control, C is B plus the broad adverse switch. The five-day and
24-hour settings, evidence/sample eligibility and exact policy/recipe hashes must
be reviewed before registration or execution. There is currently no successful
matching classifier evaluation or complete bound context in the inspected evidence,
so no date or job ceiling is committed for news economics. A narrower
negative-guidance-only rule is not implemented by this switch and would require
a separate generic rule design; do not delete other event categories to imitate it.
