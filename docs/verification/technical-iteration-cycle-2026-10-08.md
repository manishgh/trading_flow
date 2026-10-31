# Technical indicator development cycle — 8–9 October 2026

The user requested technical research beyond the named mean-reversion, snapback
and rubber-band examples, with monthly strategy-specific reporting only. Original
published audit totals remain internal. New reports contain no accumulated or
pooled returns.

## Executed study

Seven frozen recipes were run independently in five original five-stock chunks:
35 v5 runs, 22 eligible symbols in 4/4/5/5/4 executable subsets. The frozen window
is 7 April–6 October 2026. This already-viewed period is development data, not an
untouched holdout; OOS and walk-forward remain disabled. Each strategy/chunk has
$10,000 independent capital, 1% planned risk per order (not a portfolio drawdown cap), 25% notional cap, four slots, configured
10-basis-point slippage and fixed/regulatory fees. No news or broker execution.

The recipes are RSI(2)<5 control; only-threshold RSI(2)<10; previous-day RSI(2)<5
followed by a prior-high reclaim; previous-day lower 20/2 Bollinger close followed
by reclaim; at least three down closes ending yesterday followed by reclaim;
legacy quality reclaim normalized to daily/SPY200/260-bar warmup/1% risk/no news;
and EMA20/50 trend-state continuation with declared trailing/long-hold exits.
Unused stretch OR arms are disabled. Reclaim lookback is one bar. Repeated
qualifying states are permitted; existing same-symbol positions block overlaps.
EMA state eligibility is not a crossover. Other family comparisons are complete
hypotheses, not single-indicator ablations or exact textbook replications.

Evidence root is `outputs/technical-cycle-20261008` beneath this task workspace:
`C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/technical-cycle-20261008`.
The final freeze is `trial-registry-v5.json`, `configs-v5/`, `strategies-v5/` and
immutable sidecars. `replay-driver-v5/` retains the executed driver source;
`v5-execution-code-receipt.json` records executed assembly/engine-source hashes.
All seven economic recipes were registered before original v4 outcomes and stayed
identical for the ownership-only v5 retry. The code receipt was recorded during
replay, and must not be described as a pre-outcome source registration.

## Execution defect and uniform retry

The initial v4 attempt published 25 summaries, then failed in legacy batch 1:
a target flattened its trade while its already-visited stop stayed working.
The trade owner was removed, so a later event referenced an unknown owner.
Published down-close batch 5 also lacked the STM sibling-cancellation event.

The deterministic execution book now cancels same-symbol, same-owned-group OCO
siblings in the same step after a full fill. It skips already-cancelled snapshot
orders and requires replacements to preserve their OCO group. The coordinator
retains ownership until remaining orders have been cancelled. Null OCO fields
remain omitted from existing default JSON contracts. The unknown-owner check
was not weakened.

All 35 recipes were replayed under distinct version 1.0.1/profile 5 identities and
paths, with economic definitions exactly equal to v4. Original 25 summaries and
`attempt-v4-failure.json` remain; unsuccessful evidence was not overwritten.
Independent review verified CRDO and STM same-step stop retirement. STM dates,
quantity, prices, fees and trade P/L stayed unchanged. Only missing lifecycle
evidence was repaired.

Opt-in study-end liquidation is frozen to the final authoritative native-daily
source session. Entries at/after that boundary are rejected. Its market exit uses
only source open and prior completed liquidity; changing final high/low/close or
current volume cannot change it. Missing calendar/source/prior liquidity or
remaining liquidation quantity fails completeness. Old strict v3 policy remains.
Ordinary daily slices still approximate liquidity and exclude entry-slice
protection. This is not verified historical quote execution.

## Monthly results and interpretation

All 35 final runs completed. Their independent strategy trades exactly matched
the actual unified ledger. Fill fees/cash and 127 scheduled daily-close valuations
reconciled before exporting 245 monthly rows. New York source-session dates,
carried month-opening equity and separate strategy/chunk capital are used. April
7–30 and October 1–6 are partial. The control exactly reproduced all 35 prior
corrected monthly return and close-drawdown cells.

`monthly-comparison.md` contains every monthly return and monthly close-drawdown
table. `monthly-v5/monthly-diagnostics.json` adds monthly fees, exposure and
closed-trade symbol counts; `comparison-counts.json` contains descriptive counts.
Shared dates/exposures mean the 25 full-month/chunk cells are correlated, not
25 independent statistical observations.

The looser RSI threshold lost to the control in 16 full-month/chunk cells, improved
six and tied three; monthly close drawdown was larger in 20. Band reclaim only
produced eight closed trades. Legacy reclaim and EMA captured larger May gains,
but had materially worse later months and more monthly drawdown. No strategy
was promoted, activated or selected through accumulated return ranking.

Next research hypotheses include ATR-normalised moving-average stretch, rate of
change, Internal Bar Strength, ConnorsRSI, volatility breakouts/squeezes and
benchmark-relative strength. Exit changes require separate fixed-risk ablations.
These are backlog, not completed new primitives or approved trading strategies.

## Source reporting changes and checks

Backtests source selects a month and strategy/version/hash, and labels its
realised closed-trade basis separately from price-backed research MTM. Missing
or malformed sidecars, unsupported daily timing, incomplete/trimmed trades and
unreconciled cash show unavailable rather than invented zeros. Latest full month
is the stable default. Trade details preserve the selected month; financial
full-window diagnostics and cumulative winner/ranking displays were removed.
Optimization initial/live rows do not display accumulated performance and label
the historical retained subset explicitly.

Stored legacy Web snapshots prune strategy trade lists. Their monthly evidence
therefore remains unavailable without a complete-evidence loading/persistence
change. This source change is not a claim that every historical job displays
monthly returns. The current integration Web host was not switched or restarted.

Verification tier: affected component checkpoint, not a release suite.

- Focused final .NET tests: 114 passed, 0 failed. Filter covered owned OCO,
  deterministic book/coordinator, simulation characterization/execution quality,
  strategy causality/kernel, monthly presentation/performance and serialization.
  Web and referenced C# projects compiled as part of these checks.
- Actual compiled Razor partial: default latest full month (September), April
  partial, explicit September and missing-evidence states rendered successfully
  using isolated complete published research trades; no database was started.
- The actual Optimization JavaScript ran against completed-combination and
  final-summary snapshots; accumulated fields and ranks did not reappear.
- Windows CRLF is retained for BacktestRunner, Backtests page model,
  OptimizationJob Razor and the results partial. Whitespace check:
  `git -c core.whitespace=blank-at-eol,blank-at-eof,space-before-tab,cr-at-eol diff --check`.
- Read-only SQLite membership digest matches the frozen baseline: two operator
  wishlists, 30 items. No wishlist/Finviz/test sources were added by the study.
- Unrun: real browser/mobile layout, Android, full release suite, native-subdaily
  quote/fill/cost stress and paper-shadow checks. No live deployment claim.

A later resource-only fix moves candidate and execution spools under the owned
finally-cleaned workspace, including direct prepared-run and optimizer calls.
Success/failure/cancellation tests use real SQLite/NDJSON/execution handles and
preserve other runs and published copies. Redundant hierarchy was removed to
keep representative Windows SQLite journal paths below 260 characters. These
69 final affected cleanup/publication/optimization/causality checks passed;
they overlap the prior 114-test slice. The executed v5 binary receipt is retained
separately from this later resource change, which does not change economics.

Final independent quant review passed within the declared model/development
scope: 35 runs, 154 successful work items, 329 v5 manifest files, 18,494 exact
identity/time-evidence records, 245 independently recomputed monthly cells,
594 ledger fills and 4,445 close valuations. This does not certify holdout,
historical point-in-time membership/revisions or live quote execution.

Owned capture/input/spool cleanup is complete: all 28 captured files and identified
failed/transient journals and render outputs were removed after owners stopped.
No owned input/spool directory remains. All 60 v4/v5 published manifest sets,
564 published references, 60 summaries and 245 monthly rows remain; manifest
hashes are unchanged. No recovery copies or operational deletion occurred.
`independent-quant-review.json` and `owned-cleanup-completion.json` record closure.
Published results, manifests, registries, failure evidence, valuation/monthly
reports and immutable parameters are retained. Operational DB/secrets/cache and
other active services are outside cleanup scope.

## Primary research references

[Alvarez: mean-reversion measures](https://alvarezquanttrading.com/blog/the-abcs-of-creating-a-mean-reversion-strategy-part1/)
includes RSI, percent B, moving-average stretch, ROC, down closes, N-day lows and
closing range. [Bollinger's authored methods](https://www.bollingerbands.com/tradestation-methods)
distinguish reversal/trend/breakout families; this band reclaim is not an exact
Method III replication. [ConnorsRSI guidebook](https://alvarezquanttrading.com/wp-content/uploads/2016/05/ConnorsRSIGuidebook.pdf)
defines a separate composite oscillator, not RSI(2).
[AQR trend research](https://www.aqr.com/insights/research/journal-article/a-century-of-evidence-on-trend-following-investing)
motivates another family but does not establish this EMA stock recipe's edge.
[Reversal cost research](https://repub.eur.nl/pub/25718/AnotherLook_2011.pdf)
supports examining turnover and costs. These motivate hypotheses, not guaranteed
profitability or complete historical point-in-time reconstruction.
