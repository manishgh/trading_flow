# Momentum And Catalyst Evidence Plan

The binding next-cycle experiment sequence and promotion rules, including the
superseding fixed-slot top-30-percent selection and minimum-sample requirements, are defined in
`docs/research/non-ml-strategy-research-program.md`. This document remains the
verified baseline and current-result record that the new program extends.

The binding storage, provenance, readiness, and promotion design is
`docs/research/evidence-repository-design.md`. This document defines the studies;
the repository design defines which inputs are admissible.

All experiment parameters and promotion thresholds below this notice describe the
historical baseline that was executed or planned before the independent audit. They
must not drive a new run where they conflict with
`docs/research/non-ml-strategy-research-program.md`.

## Purpose

TradingFlow must establish that a stock-selection signal has predictive value before
adding entry timing, stops, targets, or parameter optimization. A favorable chart in
hindsight is not sufficient evidence because the universe, corporate actions, spread,
and information timestamp can all leak future information into a result.

The two research tracks remain separate:

- Swing asks whether completed daily history ranks future relative winners.
- Intraday asks whether a point-in-time catalyst produces a measurable short-horizon
  response after liquidity and execution costs.

Neither event study can promote a runtime strategy directly.

## Swing Momentum Baseline

### Trading Meaning

The 12-1 momentum rank asks: "Which liquid stocks performed best over roughly the
last year, excluding the most recent month?" The skipped month reduces exposure to
very recent spikes that often reverse.

At the final completed regular-session bar of each month:

```text
momentum = adjusted_close[t - 21] / adjusted_close[t - 252] - 1
```

The study ranks the point-in-time eligible universe and measures tradable outcomes
from the next regular-session open. It does not assume that the formation close was
fillable.

Three cells are preregistered:

1. `momentum_only`
2. `momentum_stock_trend`: close above SMA200 and SMA50 above SMA200
3. `momentum_stock_market_trend`: cell 2 plus SPY above SMA200

The primary selection is the top 30%. Deciles are retained to test whether stronger
rank corresponds monotonically to stronger forward return. Outcomes are measured at
5, 20, and 60 sessions, including next-open return, formation-close diagnostic return,
maximum favorable excursion, and maximum adverse excursion.

Formation dates are split chronologically into 60% development, 20% validation, and
20% holdout. A stock remains in its formation-date rank even when a later outcome is
missing. Forward outcomes that cross a partition boundary are censored individually;
the formation decision itself is never deleted based on future data. The holdout is
not a parameter-selection dataset.

### Evidence Requirements

- Explicit `adjustment=all` data provenance.
- Point-in-time security identity and universe membership.
- At least 60 monthly formations and 12 holdout formations.
- At least 30 distinct selected tickers in holdout, plus 12 independent monthly
  holdout formations.
- Comparison with SPY, the equal-weight eligible universe, and the bottom 30%.
- Results not dominated by one ticker or one formation date.
- A later shared-brain portfolio simulation with turnover, spread, slippage, sector
  limits, overlapping positions, and volatility-based sizing.

### Current Diagnostic Result

The corrected six-year adjusted dataset contains 120,073 daily rows from 2020-04-01
through 2026-07-25 for 75 current large-cap stocks plus SPY. Six years are used to
obtain 52 independent monthly formation decisions after the 252-session warm-up;
this is not a machine-learning training requirement.

The primary 20-session `momentum_stock_trend` diagnostic produced:

- mean monthly net return: 2.580430%;
- positive months: 63.4615%;
- cumulative net return: 223.754113%;
- annualized return: 31.142057%;
- maximum drawdown: -18.794508%;
- Sharpe ratio: 1.103414.

This is not promotable. The universe is a current 2026 Finviz survivor snapshot
projected backward, there are only 52 rather than 60 formation months, and development
performance is weaker than later validation/holdout performance. Retain
`momentum_only` and `momentum_stock_trend` as frozen research hypotheses. Reject the
stock-plus-market-trend cell, which loses in development.

## Intraday Catalyst Baseline

### Trading Meaning

The intraday event study asks: "After genuinely new information became available,
did the stock move enough, with enough participation and liquidity, to support a
tradable hypothesis?" It contains no buy rule, stop, or profit target.

The audit clock stores:

- provider publication time;
- provider update time;
- observed TradingFlow receipt time, when available;
- the timestamp used as information availability;
- whether availability is provider-timestamp-only or observed receipt time.

Historical REST news is labelled `provider_timestamp_only`, or
`provider_updated_timestamp_only` when revised text became available later. A live
event may use `observed_receipt_time` only when its receipt timestamp was actually
captured.

Pre-event indicators use the latest candle whose **end timestamp** is at or before
information availability. The candle containing the news is prohibited. The first
post-event technical snapshot is the first candle that completes after availability.

Initial diagnostic horizons are 1, 5, 15, 30, and 60 trading minutes. Premarket,
regular, postmarket, and overnight cohorts must not be combined. An outcome that
would cross an exchange-date or session boundary is censored instead of using the
next session as if it were continuous elapsed time.

### Evidence Requirements

- SIP bars and SIP quotes from the same feed.
- Historical NBBO around each event for executable ask-to-bid and bid-to-ask returns.
- At least 40 prior same-time sessions for cumulative RVOL; target 63.
- Cumulative time-of-day RVOL buckets, not an assumed hard entry gate.
- Global story deduplication by provider ID/canonical story, not by ticker alone.
- Original catalysts separated from derivative movers/listicle coverage.
- At least 300 independent events overall and 100 per proposed catalyst cohort.
- Chronological development, validation, and holdout partitions.
- Comparison with matched no-catalyst events and market/sector-adjusted returns.
- Edge at least twice estimated round-trip execution cost.

### Current Diagnostic Cohort

The corrected provider-updated news dataset contains 247,674 stories and produces
290,932 ticker-events. Enforcing a 24-hour quiet period reduces this to 36,156
independent episodes. After frozen 14.2-basis-point round-trip costs, generic
unclassified news has negative market-adjusted expectancy in most development,
validation, and holdout cells. Small positive long-horizon cells are inconsistent or
have too few clean observations. No generic news cohort is promoted.

The provider category field is effectively unclassified for this archive, so another
entry-rule variant cannot repair the evidence. The next valid step is human
classification of the deterministic 500-story sample into economic event type,
direction, clarity, and materiality.

Historical provider update time is usable as a conservative availability clock, but
historical first-seen receipt and complete revision history remain unproven. Those
limitations are permanent promotion blockers for this archive and are reported in
every diagnostic run.

## Promotion Boundary

1. Event study establishes a narrow, preregistered effect.
2. A new research backtest hypothesis is created without changing promoted configs.
3. The shared engine validates next-bar execution, costs, risk, and portfolio limits.
4. An untouched holdout is evaluated once.
5. A human explicitly promotes a frozen config to paper shadowing.

Research artifacts and source configs stay under `configs/research` and
`data/research`. The Web, Backtesting, and Engine projects do not reference the
`TradingFlow.Research` assembly.
