# Strategy Last Runs

This file is the short research ledger for strategy selection. Keep only the latest useful run for each retained strategy so the next optimization starts from known evidence.

## Current Intraday Candidates

| Strategy | Config | Last Result | Return | Max DD | Trades | Win Rate | Notes |
|---|---|---|---:|---:|---:|---:|---|
| TOP1 Intraday - EMA10/20 MACD Volume V1 | configs/strategies/intraday-ema10-ema20-macd-volume.v1.yaml | `data/backtest/results/shared/portfolio/intraday-ema10-ema20-macd-volume-v1-7d-20260705.json` | 0.09% | 1.12% | 34 | 35.3% | Active intraday reset. Entry gates: EMA10 above EMA20, MACD histogram bullish, and cumulative same-time volume >= 2x the 63-session baseline. Stop risk is 1R. Honest re-score on the volatile five (POET/MXL/RGTI/OUST/NVTS, 1m, 2026-05-06→06-08) = **−1.10%, REJECTED** (17 trades, 17.6% WR, 6/7 gates fail) — a state-stack entry has no intraday edge on volatile names. |

## Current Swing Candidates

| Strategy | Config | Last Result | Return | Max DD | Trades | Win Rate | Notes |
|---|---|---|---:|---:|---:|---:|---|
| TOP1 Swing Long - Minervini Trend Rider V4 | `configs/strategies/minervini-trend-template-vcp.v4-trend-rider.yaml` | `data/backtest/results/shared/portfolio/honest-rescore-swing-180d-0007.json` | 21.90% | 1.98% | 46 | 30.4% | **Phase 1 hardened = ELIGIBLE (7/7).** (1) L2 regime gate (SPY>50dma): drops 2 weak-tape entries vs the no-regime +24.73%/48-trade run and *improves* OOS. (2) Gate consolidation to <=4 (doctrine §2): removed 3 redundant EMA/BB booleans — byte-identical result, proving they were pure DOF. (3) Confirmed 2-close EMA20 exit (doctrine §6A L5): identical result here (its EMA20 exit never whipsawed in-window) but defensive for live. OOS +2.48% (13 trades), beats SPY +15.36%, walk-forward 5/6, top ticker 26.5%. The one strategy that survives honest measurement. |
| TOP2 Swing Long - Reversal Reclaim Bull Quality | `configs/strategies/swing-reversal-reclaim-bull-quality-no-news.v1.yaml` | `data/backtest/results/shared/portfolio/honest-rescore-swing-180d-0003.json` | -1.44% | 2.01% | 15 | 20.0% | **Honest re-score = REJECTED.** Sound event trigger (pullback + reclaim), but fails sample_size, total_return, return/DD, benchmark (−7.98% vs SPY), and walk-forward (2/6). Promoted originally on a cherry-picked basket; does not generalize. |

## Archetype C — Swing Mean Reversion Reclaim (research, 2026-07-10)

New archetype built per doctrine §6C. C-research first (inverted event study, `reversion-study` CLI on
the 440d cache): stretch-day buys beat the unconditional baseline by ~0.3–1.1% over 1–3d at 55–67% win
vs ~52% baseline — a real reversion edge, strongest on RSI(2)-oversold and lower-Bollinger triggers.

Engine build: RSI(2) indicator; `mean_reversion_reclaim` setup type (oversold stretch within a lookback,
resolved by the first close back above the prior-day high); stretch gates + no-fresh-news veto (Chan) in
the shared evaluator; `swing_low` stop mode. Config `configs/strategies/swing-mean-reversion-reclaim.v1.yaml`.

Honest re-score (`configs/backtest/ui-runs/archetype-c-rescore-440d.yaml`, 20 liquid names): **−1.32%,
7.96% DD, 75 trades (37W/38L, 49% WR) → REJECTED.** BUT structurally sound: passes sample_size (75),
**OOS positive (+3.54% over 25 trades)**, walk-forward 5/9. Fails total_return and return/DD (wide DD from
the swing-low stop). Benchmark not scored (no SPY in the 440d cache). The Chan no-news veto is inactive
offline (news disabled), so the real differentiator is untested. Next levers: news-data veto + tighter
stop/smaller mean-reversion target — but tune off-sample to avoid overfitting.

## Archetype B — Swing Catalyst Drift V5 (research, 2026-07-11)

Post-catalyst drift (PEAD / episodic pivot) built per doctrine §6B with the **one-shot lifecycle** that
every prior catalyst config lacked. Engine: `CatalystEligibilityService` (received-time clock, bounded
confirmation window, dedupe, one-attempt consumption) + `CatalystConfirmation` (EMA10×20 flip OR MACD
turn + volume) + a `catalyst_drift` setup type that fires exactly once — on the first confirmed bar of a
catalyst's attached run. Also fixed `CachedCatalystProvider` to reuse any cached fetch whose window
*covers* the request (was exact-match only → 0 catalysts offline).

Honest re-score (`configs/backtest/ui-runs/archetype-b-rescore-240d.yaml`, 14 names with cached Alpaca
catalysts): **+0.02%, 0.54% DD, 7 trades (2W/5L) → REJECTED** (sample_size 7 < 30). BUT the structural goal
is met — **7 clean one-shot trades, zero churn** (V3 produced hundreds on the same idea); total_return,
OOS (+0.15%), and walk-forward (4/5) are positive. The gates (fresh positive news + technical confirmation)
are strict, so the sample is thin on this 5-month/14-ticker window. Next: more data (longer window / more
tickers → more catalysts), the LiveRunner one-shot wiring + SQLite persistence, then re-score for a
promotable sample. Config `configs/strategies/swing-catalyst-drift.v5.yaml`.

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
| Minervini Trend Rider V4 | 13.02%, 2.74% DD, 44 trades (original basket); 24.73%, 1.94% DD, 48 trades (honest re-score) | Promoted as TOP1 swing, then confirmed ELIGIBLE on the honest re-score (see Swing Candidates). |
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

Do not tune intraday rules to capture weekly oscillation. Build a separate short-term volatile swing model for that behavior: 1d trend/mean-reversion context, 1h or 15m entry timing, multi-day hold, and news/catalyst awareness. No intraday variant has earned a promoted paper/live slot; the honest re-score of V1 on the volatile five (−1.10%, REJECTED) reconfirms this.

## Next Design Step

The next real "bad stock no-trade" improvement should be a portfolio/risk guard, not hidden strategy math: pause a ticker after configurable realized losses or repeated failed signals during the same session, while keeping the strategy config portable between backtest and paper trading.

## 2026-07-05 Catalyst + Technical Event Study

Implemented catalyst event-study research in the CLI and ran two six-month cohorts:

| Cohort | Event Study Report | Observation Count | Useful Buckets |
|---|---|---:|---|
| Volatile five: POET/RGTI/MXL/NVTS/OUST | `data/research/catalysts/volatile-five-180d-event-study.json` | 384 | Positive/new general news and earnings/guidance had favorable 1d-5d forward returns, but only as event-study buckets. |
| Long five: MU/SNDK/SPOT/STX/WDC | `data/research/catalysts/long-five-180d-event-study.json` | 600 | Positive or neutral/new general news with mixed-bullish or bullish-confirmed technical state was strongest over 3d-5d. |

Backtest translation result: every attempt to turn the event-study buckets into a
bar-level trade rule failed and the configs have since been removed. The three failure
modes are the lesson that survives: a strict required-fresh rule took **0 trades**; a
loose attached-state rule that stayed tradable every bar **churned** (roughly −4% to
−17% across cohorts); and a fresh-catalyst-capped variant reduced churn but was still
**negative** (about −1%). All three shared the same defect — a catalyst was re-evaluated
on every bar instead of producing one bounded attempt. That is the anti-pattern
Archetype B's one-shot lifecycle is designed to replace.

Important execution fix: `exit_rules.allow_same_bar_stop_target` now exists. Swing research candidates set it to `false` so a 1h entry bar does not immediately manufacture stop/target fills from unknown intrabar sequence. Existing intraday strategies keep the default `true` unless explicitly changed.

Conclusion: the document's architecture direction is valid, but the profitable component is currently the research event-study, not the trade rule. Next improvement should be a point-in-time catalyst dedupe key for trading: one trade attempt per catalyst per ticker, then evaluate 15m/1h confirmation once, instead of allowing a catalyst to remain tradable across many bars.

### Catalyst Code Audit Note

Existing trading catalyst path was already present before the event-study work: `ICatalystProvider` loads news, `CatalystSnapshotAttacher` attaches the latest fresh catalyst to indicator snapshots, `SignalGenerator` computes catalyst age/price move, and `BasicStrategyEvaluator` applies `require_positive_news`, sentiment veto, confirmation-window, and catalyst-price-move gates.

The new implementation is research-only: `CatalystTechnicalEventStudyRunner` groups point-in-time news by ticker, dedupes repeated stories, estimates novelty, anchors each article to the latest available candle at or before the article timestamp, computes technical regime, and measures forward returns over 1h/4h/1d/3d/5d. It does not replace paper/live catalyst execution.

Failure audit: the catalyst translation attempts failed because event-study buckets were translated into repeated bar-level trades. The permissive variant was too loose and churned; the fresh-catalyst-capped variant reduced churn with stronger confirmation, but still had too many stop-outs. The result is research signal without a profitable execution rule yet. Next implementation should enforce one trade attempt per catalyst/ticker and evaluate only a bounded confirmation window, not every eligible bar while the catalyst remains attached.
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


