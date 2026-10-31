# Drawdown qualification completion contract — 18 October 2026

Status: evidence adapters, price-source binding and bounded research execution
finished. All **120/120** attempts are accounted for; final disposition remains
**RETAIN_RESEARCH**. Final independent review, owned-input cleanup and post-repair
operator/API/browser checks are complete. No strategy promotion or profitable-rule
claim is made. The current [completion handoff](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/completion-handoff.md)
resolves the earlier immutable reports' time-specific open review items.

This checkpoint extends the [research plan](drawdown-qualification-research-plan-2026-10-18.md)
and the feature-only [measurement contract](drawdown-qualification-implementation-2026-10-18.md).
The earlier documents remain records of their respective checkpoints. Runtime
contracts now support sector, evaluated-event and matured-history evidence; the
available historical data still cannot support those economic overlays.

## Shared behavior and evidence requirements

BacktestRunner and LiveRunner resolve optional evidence through the same
`IStrategyDecisionContextProvider`, request assembler and decision kernel. Frozen
context manifests bind source files and hashes, symbols, availability intervals
and evidence identities. Missing context remains unknown. An absent qualification
configuration retains the previous admission behavior and semantic strategy
identity; observation audit fields remain outside the semantic decision digest.

Sector measurements use six identical completed daily session closes to calculate
five-session stock, sector-benchmark and SPY returns, including stock minus sector
return. They require historical security/adjustment identity, an effective sector
mapping, independent expected sessions, benchmark identity and source hashes known
by the decision. SPY cannot substitute for the sector benchmark. Missing or
misaligned sessions, future evidence and identity changes produce explicit reasons.

Event evidence requires an independently evaluated taxonomy/classifier under
`catalyst-human-labeling-v1`, the matching evaluation hash, provider article and
revision identities, publication/update/receipt/classification/availability times,
and company, sector and global coverage for the declared lookback. Latest available
provider revision ordering precedes assessment selection. A latest revision known
outside the lookback does not count as a current event; a new revision of an older
article can count when it becomes known inside the lookback. Required coverage is independently bounded
and fresh. An empty article set establishes no adverse event only when those
coverage contracts pass. Reading categories on the News page cannot supply this
evidence. Optional adverse-event rejection and sector metric bounds are explicit
strategy settings, separate from requiring evidence availability.

Matured history requires stable security identity and adjustment version bound to
the bar series, plus an independently sourced expected-session calendar with its
own availability time and hash. Episode/reset and five-session maturity semantics
remain those in the measurement contract. Neither observed bar timestamps nor a
nonempty security name can certify historical completeness. Unknown required
evidence rejects at the original trigger; the candidate cannot retry into a later
entry using subsequent evidence. The reviewed source-binding repair is now
applied: independent frozen price bundles and exact ordered completed-prefix
hashes bind security/adjustment declarations to consumed price content, bar count,
last source-bar timestamp and availability. Changed content or mismatched identity/version
produces an explicit unknown reason. A checksum establishes agreement with the
declared source, not the historical truth of that source. The 15-file staged
implementation was applied after the parent jobs, then passed 268 affected C#
checks. The research engine is unchanged since that pass; the subsequent Earnings
calendar presentation repair is separate from the economic implementation. The parent
price-only runs used no identity provider and are unaffected. Historical context
remains unknown with the actual available data.

## Actual source audit and current exclusions

Published artifacts are under
`C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018`.
`source-evidence-audit.json` and `.md` record a read-only operator/catalog inventory,
including WAL state, schema/count hashes and raw-manifest checks. The catalog had
eight datasets, 5,042 artifacts and 9,950 source observations. Its long bar/news
sets do not overlap the original Volatile roster. Historical news receipt/revision
lineage and evaluated classifier evidence are insufficient; operational earnings
first observed in October cannot establish an April–September schedule archive.
Stable historical identities, corporate-action revisions, sector mappings and
benchmarks, and certified matured episode history are absent. Sector, event and
history economic arms therefore remain unavailable; no zero-return result or
economic claim is assigned to them.

The new capture contains 46 real Alpaca SIP/all REST series and a separate exchange
calendar receipt. Daily inputs begin with actual provider observations, generally
3 March 2025; later listings begin later. The capture is retrospectively adjusted
diagnostic evidence, without historical revision-time availability. It is a new
hashed snapshot, not a claim to reproduce the deleted Stage04B inputs.

`study-freeze.json` retains the exact original 25-symbol roster and five cohorts.
BXDC, FPS and SIVE lack sufficient real history and receive no replacements. The
user explicitly excluded leveraged fund SOXL from every new comparison, coverage
control and threshold-training sample; its original roster entry and previously
published results remain intact. Eligibility requires 260 genuine completed daily
bars and permits later real warmup for CRCL and MIAX. No research list is written
into the operator dataset.

## Frozen price-only matrix

`trial-registry.json` was frozen at 20:47:38 UTC on 18 October, before the revised
matrix's first economic attempt. It binds source, code, capture/calendar receipts,
strategy/configuration manifests and feature-availability masks. The original
60-attempt preregistration was archived under `preregistration-v1` with an amendment
receipt proving zero economic attempts before it was superseded. The amendment
adds availability-only controls and publication bindings; it does not select a
threshold using economic outcomes.

| Dimension | Frozen choice |
|---|---|
| Controls | Original RSI5 and mean-reversion recovery with SMA5 exit, exact parent identities retained |
| Window | 7 April–6 October 2026 New York source sessions; already-viewed development only |
| Depth gate | `predecline_atr_depth <= 4.7473776149281717631292646153`, inclusive; no lower floor |
| Pressure gate | `close_location >= 0.5`, inclusive; upper-half close, not proof of easing order flow |
| Availability controls | Separate depth-known and pressure-known masks, with no value-based qualification |
| Costs | Original 10 and 20 bps assumptions |
| Capital and batching | 10,000 per independent strategy/cohort diagnostic; one original cohort at a time |
| Matrix | Two families × two costs × five arms × five cohorts = 100 attempts |
| Overall ceiling | 120 attempts including failures and retries; no unregistered additional recipes |

The five arms are the full control, depth gate, depth-availability control,
pressure gate and pressure-availability control. Exact hashes are authoritative in
the registry. Entry clock, ranking, exits, stops, risk, slots, capital and execution
policy remain paired with their control. The pressure gate accepts or rejects at
the original trigger; it never waits for another bar.

`depth-q90-freeze.json` records the input-only empirical q90 from 292 pre-7-April
eligible depth observations across 19 symbols. Each sampled roster symbol has
equal total mass, using exact integer weights and nearest-rank selection with
grouped ties. This is a roster-symbol proxy because stable issuer identity is
unavailable. CRCL and MIAX contribute no training samples. The threshold is fixed
across both strategies, every cohort and both costs; there is no grid, combination
or first-cohort retuning. The cutoff is a diagnostic extreme-tail exclusion, not
an established profitable bound.

## Published parent results and opportunity descriptions

All 100 registered parent attempts finished: **74 complete, 26 incomplete, zero
parent job failures**. The full monthly publication contains 518 usable rows. Its
complete 700-row assessment retains unavailable arms explicitly, with 196 valid
overlay-month comparisons and 84 withheld. The
[independent parent quant review](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/independent-quant-parent-matrix-review-20261018T2143169186335Z.json)
recomputed every published monthly return/drawdown and available delta and found
no P1/P2 issue. Required execution slices remain missing for BKSY on 26 August at
16:15 UTC and DIOD on 21 May at 16:00 UTC. All months of each incomplete arm remain
unavailable; a successful overlay cannot repair an unavailable control.

The immutable
[parent monthly review](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/analysis-source/parent-monthly-review.md)
shows full-control monthly net marked-to-market returns, depth/pressure changes
against availability-only controls, the availability effect separately, original
cohort columns and all 20 exact recipe identities. Results are mixed. For example,
the 20 bps C4 June depth effect is −0.6160 percentage points for RSI5 and +1.0414
for mean reversion; the latter is −0.3534 in partial October. These observations
do not select a winner or authorize threshold changes. Parent disposition remains
**RETAIN_RESEARCH**.

The published pre-shadow opportunity description contains **828 intent-label
rows and 196 monthly groups**, covering all 20 original control/cohort/cost runs.
It retains accepted/rejected/unknown labels and gross daily one/three/five-session
outcomes with maturity and missingness. These are descriptive candidate-intent
labels, not independently executed strategy profits or losses avoided.

## Publication and decision limits

Full chronological portfolio replays and unchanged-entry opportunity analysis are
separate required outputs. Current candidate-intent audits alone cannot establish
independent shadow fills, exits or opportunity returns. The final supplemental
shadow outputs and their independent final review are complete.
Fixed-horizon feature labels
cannot substitute for strategy opportunity outcomes. Compare each value gate with
its corresponding availability-only control
and publish the availability control's change from the full control separately.
Rejected profitable opportunities, losses avoided, unknowns, fills and capital
constraints remain visible. Opportunity shadows are diagnostic comparisons, not a
deployable combined portfolio.

Report each month separately by exact strategy/version/hash, cohort and cost,
using New York source sessions and month-opening marked equity; flag partial
months. Include monthly return and control difference, close drawdown, exposure,
trade/non-fill/fee/holding and admission counts. No pooled or accumulated-period
return table is authorized. Monthly artifacts bind their source hashes and use
exclusive publication rather than overwriting prior evidence.

Required active-position execution gaps still make an arm incomplete. The existing
first-entry-interval protection limitation and absence of executable quotes still
prevent calibrated trading-performance claims. Neither avoiding a control's gap
nor sitting in cash establishes improvement. The viewed period, current roster
membership, retrospective adjustment and missing historical context prevent
holdout or promotion claims. The original roster also cannot satisfy the minimum
30 distinct holdout issuers. No Android, GitHub synchronization, broker activation
or strategy promotion is part of this checkpoint.

Supplemental attempt **101** failed during parent-summary JSON reading before
supplemental economics. Attempt **102** performed calculations but failed final
journal publication because its independent execution journal remained open while
the receipt was hashed. Both attempts and their artifacts are preserved, consumed
and unavailable as successful shadow publications; neither receives a retry.
The reviewed v3 external-driver publication repair completed the other **18
distinct passes**. Core research source and frozen thresholds are unchanged.
All 20 nominal supplemental slots, including those two failures, are consumed;
the combined **120-attempt ceiling is exhausted**, with no further replay or retry.

The published
[final assessment](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/analysis-source/final-snapshot-final/assessment.json)
accounts for **118 successful artifact publications** (100 parent and 18 shadow)
and **two failed passes**. Successful publication does not turn the 26 incomplete
parent economics into complete results. The
[shadow monthly CSV](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/analysis-source/final-snapshot-final/independent-shadow-monthly.csv)
retains **176 monthly groups**, separate accepted/rejected/unknown completed-dollar
distributions, missing outcomes and coverage. Failed RSI5 C1 passes at both costs
stay unavailable; their partial artifacts are not salvaged. The
[final readable review](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/analysis-source/final-review.md)
preserves the parent monthly tables and adds illustrative month/strategy/cost/
cohort-specific rejected profitable and negative outcomes. Complete-dollar totals
remain unavailable when a group's rejected outcomes are incomplete. Independent
fixed-capital opportunities are not a combined deployable portfolio.

Final assessment and paired-analysis Python checks passed **28 tests (24 + 4)**.
The earlier parent report and pre-shadow description remain immutable checkpoint
records. No holdout, threshold selection or promotion follows from the final report.

## Final review and operational closure

The [final independent quant review](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/independent-quant-FINAL-results-review-20261018T2213478494879Z.json)
approved **RETAIN_RESEARCH with no P1/P2 findings**. It reconciled 402 published
shadow triggers: 378 completed independent outcomes, 18 incomplete outcomes and
six trigger-only unavailable observations; all 402 original/rerun semantic plans
matched. It also reconciled 774 independent fill events and verified all 84 human
monthly table rows, 20 exact identities and four illustrative shadow examples.
These count checks do not combine portfolio returns or imply independent samples.

The [owned-input cleanup receipt](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/owned-input-cleanup-receipt.json)
records removal of two owned directories: 47 raw-input files/70,778,473 bytes and
two cache files/62,803 bytes. No owned database files remain. All 118 successful
publications, both failed attempts, configurations, strategies, source/parameter
receipts and compact audit evidence are preserved; operator data was untouched.

A real browser check after Berlin midnight exposed an Earnings date-scope
bug: Sunday 18 October in New York was already Monday in Berlin, causing the
operator-local next-business-day scope to choose Tuesday 20 October rather than
Monday 19 October. The completed scoped repair uses New York exchange dates for Earnings
queries, report-date grouping and day headings, while detail instants retain local/
UTC display. Its [independent review](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/earnings-exchange-date-independent-review-20261019.json)
found no P1/P2 issue. The [verification receipt](C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/drawdown-completion-20261018/earnings-exchange-date-verification-20261019.json)
records **111 focused checks passed, zero failed/skipped**, including 11 new date
boundary cases, and successful JavaScript syntax checking. Actual API and browser
now show **Monday 19 October with 21 events**, with monitoring analysis at 22:16
UTC. Canonical strategies and the research price engine are unchanged; no economic
replay was required. These later checks are a separate checkpoint from the 268 C#
research and 28 Python passes, not an additive test total.

The post-repair operator observation at 22:16:51 UTC preserves the same two
wishlists, 30 items and two preset hashes; News is fresh through 22:10 UTC and
Earnings receipt through 22:15:36 UTC. Final browser proof shows three Global News
articles/five matching records, union 5,608/Finviz 5,606 and the right stock panel.
Web and ngrok were verified running at the existing public URL and intentionally
remain available with trading paused and market feeds enabled. Current launch,
observation and screenshots are linked from the completion handoff.

Implemented contracts do not resolve absent historical sector/event/identity data,
the missing BKSY/DIOD execution observations, first-interval/quote calibration, or
the lack of untouched validation. Those evidence limits remain explicit research
limits, separate from finished implementation and bounded execution. Complete
terminal portfolio nonfill metrics remain null in the parent assessment. Preserve
all attempts, failures, parameters, manifests and compact audits.
