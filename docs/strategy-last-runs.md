# Strategy Last Runs

This file is the short research ledger for strategy selection. Keep only the latest useful run for each retained strategy so the next optimization starts from known evidence.

## Current Intraday Candidates

| Strategy | Config | Last Result | Return | Max DD | Trades | Win Rate | Notes |
|---|---|---|---:|---:|---:|---:|---|
| TOP1 Intraday - EMA10/20 MACD Volume V1 | configs/strategies/intraday-ema10-ema20-macd-volume.v1.yaml | `data/backtest/results/shared/portfolio/intraday-ema10-ema20-macd-volume-v1-7d-20260705.json` | 0.09% | 1.12% | 34 | 35.3% | Active intraday reset. Entry gates: EMA10 above EMA20, MACD histogram bullish, and cumulative same-time volume >= 2x the 63-session baseline. Stop risk is 1R. |
| Experimental Intraday - EMA10/20 MACD Histogram Volume V2 Additive | configs/strategies/intraday-ema10-ema20-macd-volume.v2-additive.yaml | `data/backtest/results/shared/portfolio/intraday-ema10-ema20-macd-volume-v2-additive-7d-20260705.json` | -0.21% | 0.82% | 22 | 27.3% | Not promoted. Additive filters were close-location >= 0.50, MACD histogram <= 0.30, and prior 5-bar gain <= 1.00%. Drawdown improved, but expectancy worsened. |

## Current Swing Candidates

| Strategy | Config | Last Result | Return | Max DD | Trades | Win Rate | Notes |
|---|---|---|---:|---:|---:|---:|---|
| TOP1 Swing Long - Minervini Trend Rider V4 | `configs/strategies/minervini-trend-template-vcp.v4-trend-rider.yaml` | metrics retained; full audit file removed during cleanup | 13.02% | 2.74% | 44 | 22.7% | Current promoted swing default for the long wishlist. Winners were large enough to pay for many tight stop-outs; strongest names were SNDK, MU, WDC, and STX. |
| TOP2 Swing Long - Reversal Reclaim Bull Quality | `configs/strategies/swing-reversal-reclaim-bull-quality-no-news.v1.yaml` | `data/backtest/results/shared/portfolio/swing-quality-long-overbought-short-v5-comparison-crdo-msft-app-intc-mu-nvda-180d-0001.json` | 11.95% | 3.85% | 10 | 60.0% | Retained as second swing model. Average audited hold time: 12.2 days. Better win rate than V4, smaller sample. |

## Trader Research Sources Kept

| Source File | Purpose |
|---|---|
| `configs/strategies/lance_breitstein_intraday_tactics.yaml` | Intraday VWAP/reclaim/trap research translation source. |
| `configs/strategies/brian_shannon_mta_avwap_strategies.yaml` | Anchored VWAP and market structure research source. |
| `configs/strategies/kristjan_qullamaggie_stream_methodology.yaml` | Momentum/swing stream research source. |
| `configs/strategies/minervini-trend-template-vcp.v2.yaml` | Trend template/VCP research source. |

## Volume Interpretation

`min_volume_spike` is not a single last-candle test. In the engine it uses primary `RelativeVolume`, which is cumulative current-session volume at the same exchange-time slot divided by the average cumulative volume for up to 63 prior comparable sessions.

Recent "players coming in now" pressure should be modeled separately with the candle-volume SMA trend fields:

- `require_volume_sma_rising`
- `volume_sma_period`
- `volume_sma_rising_lookback_bars`
- `min_volume_sma_rise_pct`

The retired V5-style experiments proved this gate can reduce drawdown, but they were too blunt as standalone improvements.

## Latest Experiment Verdicts

| Experiment | Result | Verdict |
|---|---:|---|
| V6 Lite dual RVOL | 11.79%, 1.24% DD, 43 trades | Promoted as TOP1 intraday. Best retained balance of return, drawdown, and simplicity on the same-session validation basket. |
| V8 Adaptive Guard | 10.81%, 2.85% DD, 43 trades | Promoted as TOP2 intraday. Better win rate than V6, but lower return and higher drawdown. |
| Minervini Trend Rider V4 | 13.02%, 2.74% DD, 44 trades | Promoted as TOP1 swing. Corrected daily execution avoided same-candle exits and captured multi-day trend legs in SNDK, MU, WDC, and STX. |
| V9 Confirmed VWAP Reclaim on volatile five | -2.29%, 2.29% DD, 12 trades | Not promoted. Least-bad intraday baseline on volatile weekly movers, but still negative. The move horizon was weekly/multi-session, not an intraday-only edge. |
| V10 Volatile Reentry | -13.68%, 14.34% DD, 155 trades | Retired. Relaxing gates increased churn and stop-outs. |
| V5 Rising Volume | -0.23%, 2.40% DD, 60 trades | Retired. It reduced drawdown but filtered too many profitable continuation trades. |
| V6 Daily Quality | 1.08%, 3.92% DD, 70 trades | Retired. Daily EMA/MACD confluence helped some selection but increased drawdown. |
| Old Ross Gap-Go Bull Flag V2 on screenshot runners | -10.78%, 10.78% DD, 8 trades | Retired. The bull-flag pattern fired late and every trade lost. |

## 2026-06-13 Intraday Cross-Validation

The retained pair came out of the POET/MXL/RGTI/MU/MSFT 90-day comparison after making volume semantics explicit:

- V6 Lite won because it kept the simple Ross/VWAP/EMA/MACD stack while adding only the two volume features that carried the strongest signal in the feature audit.
- V3 stayed relevant because it is easier to reason about and is a better control strategy when we test future refinements.

The Python analyzer report at `data/research/ml/all-intraday-two-batch-20260613/research_report.md` found the strongest entry-time correlations around ATR%, close location, session-vs-average-day RVOL, slot RVOL, EMA distance, and volume SMA. These should feed deterministic config changes only; ML is not a trade executor.

## 2026-07-05 Volatile Weekly-Move Finding

The volatile POET/RGTI/MXL/NVTS/OUST six-month run did not prove an intraday winner. These names moved up and down across the week, but the retained intraday strategies are explicitly same-session systems: they enter from 5m momentum, use VWAP/EMA/MACD confirmation, and flatten or stop inside the session. A stock can produce a profitable weekly swing while still failing every same-day entry because the intraday bars are pullbacks, gaps, chop, or late continuations.

Do not tune intraday rules to capture weekly oscillation. Build a separate short-term volatile swing model for that behavior: 1d trend/mean-reversion context, 1h or 15m entry timing, multi-day hold, and news/catalyst awareness. V9 remains useful only as a conservative intraday audit baseline; it is not a promoted paper/live strategy.

## Next Design Step

The next real "bad stock no-trade" improvement should be a portfolio/risk guard, not hidden strategy math: pause a ticker after configurable realized losses or repeated failed signals during the same session, while keeping the strategy config portable between backtest and paper trading.

## 2026-07-05 Catalyst + Technical Event Study

Implemented catalyst event-study research in the CLI and ran two six-month cohorts:

| Cohort | Event Study Report | Observation Count | Useful Buckets |
|---|---|---:|---|
| Volatile five: POET/RGTI/MXL/NVTS/OUST | `data/research/catalysts/volatile-five-180d-event-study.json` | 384 | Positive/new general news and earnings/guidance had favorable 1d-5d forward returns, but only as event-study buckets. |
| Long five: MU/SNDK/SPOT/STX/WDC | `data/research/catalysts/long-five-180d-event-study.json` | 600 | Positive or neutral/new general news with mixed-bullish or bullish-confirmed technical state was strongest over 3d-5d. |

Backtest translation result:

| Strategy | Config | Last Result | Return | Max DD | Trades | Win Rate | Verdict |
|---|---|---|---:|---:|---:|---:|---|
| Catalyst Confirmation V2 ADX/OBV | `configs/strategies/swing-catalyst-confirmation-long.v2-adx-obv.yaml` | not retained | 0.00% | 0.00% | 0 | n/a | Too strict; no trades. Retain only as a control. |
| Research Swing Long V3 - Catalyst Confirmation Event Study | `configs/strategies/swing-catalyst-confirmation-long.v3-event-study.yaml` | not retained | -4.18% long / -17.49% volatile | 4.99% / 18.63% | 96 / 170 | 26.0% / 20.6% | Too loose; repeated catalyst context caused churn. Not promoted. |
| Research Swing Long V4 - Fresh Catalyst Confirmed | `configs/strategies/swing-catalyst-confirmation-long.v4-fresh-confirmed.yaml` | not retained | -0.92% long / -1.34% volatile | 2.48% / 3.76% | 31 / 46 | 29.0% / 28.3% | Improved after fresh-catalyst and no same-bar stop/target handling, but still not positive. Research-only. |

Important execution fix: `exit_rules.allow_same_bar_stop_target` now exists. Swing research candidates set it to `false` so a 1h entry bar does not immediately manufacture stop/target fills from unknown intrabar sequence. Existing intraday strategies keep the default `true` unless explicitly changed.

Conclusion: the document's architecture direction is valid, but the profitable component is currently the research event-study, not the trade rule. Next improvement should be a point-in-time catalyst dedupe key for trading: one trade attempt per catalyst per ticker, then evaluate 15m/1h confirmation once, instead of allowing a catalyst to remain tradable across many bars.

### Catalyst Code Audit Note

Existing trading catalyst path was already present before the event-study work: `ICatalystProvider` loads news, `CatalystSnapshotAttacher` attaches the latest fresh catalyst to indicator snapshots, `SignalGenerator` computes catalyst age/price move, and `BasicStrategyEvaluator` applies `require_positive_news`, sentiment veto, confirmation-window, and catalyst-price-move gates.

The new implementation is research-only: `CatalystTechnicalEventStudyRunner` groups point-in-time news by ticker, dedupes repeated stories, estimates novelty, anchors each article to the latest available candle at or before the article timestamp, computes technical regime, and measures forward returns over 1h/4h/1d/3d/5d. It does not replace paper/live catalyst execution.

Failure audit: V3/V4 failed because event-study buckets were translated into repeated bar-level trades. V3 was too permissive and churned; V4 reduced churn with fresh-catalyst and stronger confirmation, but still had too many stop-outs. The result is research signal without a profitable execution rule yet. Next implementation should enforce one trade attempt per catalyst/ticker and evaluate only a bounded confirmation window, not every eligible bar while the catalyst remains attached.
### Catalyst Published Timeline Audit

`CatalystTechnicalEventStudyRunner` now writes a news-to-candle timeline for each observation:

- `providerPublishedTimestampUtc`: the timestamp supplied by the provider/news article. Today this is the same source value as `eventTimestampUtc`.
- `receivedTimestampUtc`: system ingestion time when available. Alpaca/Finviz fetches stamp this at provider read time; persisted rolling-news items map it from `IngestedAt`. Historical/manual catalysts may still be null if they were created before ingestion timing existed.
- `anchorTimestampUtc` and `anchorBarOffsetMinutes`: the latest candle at or before provider-published time. Negative offset means the news landed inside the still-forming candle.
- `firstConfirmableTimestampUtc` and `firstConfirmableDelayMinutes`: the first candle strictly after the provider-published time. This is the earliest conservative candle for post-news confirmation without using the candle that was already forming.
- `preNewsReturn15mPct` / `preNewsReturn60mPct`: drift before the article. This shows whether the stock was already moving before the news timestamp.
- `postNewsReturn15mPct` / `postNewsReturn60mPct`: immediate reaction after the anchor candle.
- `ema10AboveEma20BeforeNews`, `ema10AboveEma20AfterNews`, `ema10CrossedAboveEma20AfterNews`, `macdHistogramBeforeNews`, `macdHistogramAfterNews`, `macdHistogramTurnedBullishAfterNews`, and `volumeExpandedAfterNews`: before/after technical state used to audit whether the move was actually catalyst-confirmed or already technically underway.

Design implication: the shared catalyst brain must use `receivedTimestampUtc` for live/paper eligibility because that is when the system could actually act. `providerPublishedTimestampUtc` remains the audit and latency-measurement timestamp; if received time is null, the engine must treat the catalyst as research-only or fall back conservatively with an explicit audit flag.

## 2026-07-05 Intraday Execution Strategy Spec Backtest

Implemented the user's `intraday_execution_strategies.yaml` as backtest-only research configs under `configs/backtest/strategies/`:

| Strategy | Config | Last Result | Return | Max DD | Trades | Win Rate | Verdict |
|---|---|---|---:|---:|---:|---:|---|
| RESEARCH Intraday - VWAP Momentum Pullback BT V1 | `configs/backtest/strategies/intraday-vwap-momentum-pullback.bt-v1.yaml` | `data/backtest/results/shared/portfolio/intraday-execution-strategies-bt-v1-7d-20260705.json` | -1.64% | 1.64% | 3 | 0.0% | Not promoted. VWAP-minus-ATR structural stop was implemented; all candidates stopped out. |
| RESEARCH Intraday - ATR Compression OR Breakout BT V1 | `configs/backtest/strategies/intraday-atr-compression-breakout.bt-v1.yaml` | `data/backtest/results/shared/portfolio/intraday-execution-strategies-bt-v1-7d-20260705.json` | -0.63% | 0.63% | 4 | 0.0% | Best of the three, still negative. Uses inside-day/NR7 gate, ORH/ORL +/- $0.10 trigger, opposite opening-range stop, and failed-breakout 3-bar circuit breaker. |
| RESEARCH Intraday - MACD Histogram Divergence Fade BT V1 | `configs/backtest/strategies/intraday-macd-divergence-fade.bt-v1.yaml` | `data/backtest/results/shared/portfolio/intraday-execution-strategies-bt-v1-7d-20260705.json` | -2.18% | 7.00% | 70 | 28.6% | Not promoted. Divergence fired too often and churned despite VWAP target/ extreme-shadow stop. |

Run config: `configs/backtest/ui-runs/intraday-execution-strategies-bt-v1-7d-20260705.yaml`.

Verification added: `RunConfigParsingTests.ResearchIntradayExecutionStrategies_ParseExactExecutionFields` asserts the YAML parser reads the new executable fields. Full test suite passed: 209/209.

Implementation gap deliberately not hidden: partial exits from the spec are not represented by the current `BacktestTrade` model, which supports one entry and one exit. The executable pieces are implemented generically: opening-range buffer, structural stop mode, VWAP target mode, and failed-breakout circuit breaker. Partial-fill modeling needs a separate trade-lot/P&L model change before it can be claimed as supported.


