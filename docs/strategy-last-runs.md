# Strategy Last Runs

This file is the short research ledger for strategy selection. Keep only the latest useful run for each retained strategy so the next optimization starts from known evidence.

Historical result paths below are provenance labels, not runtime dependencies.
Loose generated JSON/CSV files may be retired after their metrics and conclusions
are captured here. Current reproducible research evidence lives in
`data/research/evidence/catalog.db` and its immutable object store.

## Current Intraday Candidates

| Strategy | Config | Last Result | Return | Max DD | Trades | Win Rate | Notes |
|---|---|---|---:|---:|---:|---:|---|
| TOP1 Intraday - EMA10/20 MACD Volume V1 | configs/strategies/intraday-ema10-ema20-macd-volume.v1.yaml | `data/backtest/results/shared/portfolio/intraday-ema10-ema20-macd-volume-v1-7d-20260705.json` | 0.09% | 1.12% | 34 | 35.3% | Active intraday reset. Entry gates: EMA10 above EMA20, MACD histogram bullish, and cumulative same-time volume >= 2x the 63-session baseline. Stop risk is 1R. Honest re-score on the volatile five (POET/MXL/RGTI/OUST/NVTS, 1m, 2026-05-06→06-08) = **−1.10%, REJECTED** (17 trades, 17.6% WR, 6/7 gates fail) — a state-stack entry has no intraday edge on volatile names. |

## Current Swing Candidates

| Strategy | Config | Last Result | Return | Max DD | Trades | Win Rate | Notes |
|---|---|---|---:|---:|---:|---:|---|
| TOP1 Swing Long - Minervini Trend Rider V4 | `configs/strategies/minervini-trend-template-vcp.v4-trend-rider.yaml` | `data/backtest/results/shared/portfolio/honest-rescore-swing-180d-0007.json` | 21.90% | 1.98% | 46 | 30.4% | **Historical research control; prior ELIGIBLE label is stale.** The retained run predates corrected risk semantics and used a static candidate universe without historical membership evidence. Its regime, gate, and exit simplifications remain useful hypotheses, but it requires a clean rerun under the current planner and point-in-time universe gate before paper or production promotion. |
| TOP2 Swing Long - Reversal Reclaim Bull Quality | `configs/strategies/swing-reversal-reclaim-bull-quality-no-news.v1.yaml` | `data/backtest/results/shared/portfolio/honest-rescore-swing-180d-0003.json` | -1.44% | 2.01% | 15 | 20.0% | **Honest re-score = REJECTED.** Sound event trigger (pullback + reclaim), but fails sample_size, total_return, return/DD, benchmark (−7.98% vs SPY), and walk-forward (2/6). Promoted originally on a cherry-picked basket; does not generalize. |

## Archetype C — Swing Mean Reversion Reclaim (research, 2026-07-10)

New archetype built per doctrine §6C. C-research first (inverted event study, `reversion-study` CLI on
the 440d cache): stretch-day buys beat the unconditional baseline by ~0.3–1.1% over 1–3d at 55–67% win
vs ~52% baseline — a real reversion edge, strongest on RSI(2)-oversold and lower-Bollinger triggers.

Engine build: RSI(2) indicator; `mean_reversion_reclaim` setup type (oversold stretch within a lookback,
resolved by the first close back above the prior-day high); stretch gates + no-fresh-news veto (Chan) in
the shared evaluator; `swing_low` stop mode. Config `configs/strategies/swing-mean-reversion-reclaim.v1.yaml`.

Honest re-score (`configs/backtest/ui-runs/archetype-c-rescore-440d.yaml`, 20 liquid names): **−1.32%,
7.96% DD, 75 trades (37W/38L, 49% WR) → REJECTED.** It passes sample size and has positive OOS
(+3.54% over 25 trades), but fails total return and return/DD because of wide stop losses.

The 2026-07-24 cache-only event study then tested the generic fresh-news hypothesis with 156 tickers
covered and 153 sufficiently warm for analysis. The loose report was retired
after this summary was captured. Fresh-news stretch events had *higher*
descriptive 1–5 session returns than identifiable-no-fresh-news events (for example, 3-day 0.84% versus
0.53%). All historical news availability times are explicitly labeled provider-publication proxies,
not trusted original receipt times. Therefore a blanket no-news veto is not supported and must not be added. The next research
dimension is event type, subject mapping, sentiment, novelty, and availability-time quality, followed
by a controlled strategy A/B only if those cohorts separate.

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
| Minervini Trend Rider V4 | 13.02%, 2.74% DD, 44 trades (original basket); 24.73%, 1.94% DD, 48 trades (historical re-score) | Retained as the strongest research control. The previous ELIGIBLE label is invalid under current risk and point-in-time universe requirements; clean revalidation is required. |
| V5 Rising Volume | -0.23%, 2.40% DD, 60 trades | Retired. It reduced drawdown but filtered too many profitable continuation trades. |
| V6 Daily Quality | 1.08%, 3.92% DD, 70 trades | Retired. Daily EMA/MACD confluence helped some selection but increased drawdown. |
| Old Ross Gap-Go Bull Flag V2 on screenshot runners | -10.78%, 10.78% DD, 8 trades | Retired. The bull-flag pattern fired late and every trade lost. |

## 2026-06-13 Intraday Cross-Validation

The retained pair came out of the POET/MXL/RGTI/MU/MSFT 90-day comparison after making volume semantics explicit:

- V6 Lite won because it kept the simple Ross/VWAP/EMA/MACD stack while adding only the two volume features that carried the strongest signal in the feature audit.
- V3 stayed relevant because it is easier to reason about and is a better control strategy when we test future refinements.

The retired exploratory analyzer found the strongest entry-time correlations
around ATR%, close location, session-vs-average-day RVOL, slot RVOL, EMA
distance, and volume SMA. These findings feed deterministic hypotheses only;
the analyzer was never a trade executor.

## 2026-07-05 Volatile Weekly-Move Finding

The volatile POET/RGTI/MXL/NVTS/OUST six-month run did not prove an intraday winner. These names moved up and down across the week, but the retained intraday strategies are explicitly same-session systems: they enter from 5m momentum, use VWAP/EMA/MACD confirmation, and flatten or stop inside the session. A stock can produce a profitable weekly swing while still failing every same-day entry because the intraday bars are pullbacks, gaps, chop, or late continuations.

Do not tune intraday rules to capture weekly oscillation. Build a separate short-term volatile swing model for that behavior: 1d trend/mean-reversion context, 1h or 15m entry timing, multi-day hold, and news/catalyst awareness. No intraday variant has earned a promoted paper/live slot; the honest re-score of V1 on the volatile five (−1.10%, REJECTED) reconfirms this.

## Next Design Step

The next real "bad stock no-trade" improvement should be a portfolio/risk guard, not hidden strategy math: pause a ticker after configurable realized losses or repeated failed signals during the same session, while keeping the strategy config portable between backtest and paper trading.

## 2026-07-05 Catalyst + Technical Event Study

Implemented catalyst event-study research in the CLI and ran two six-month cohorts:

| Cohort | Event Study Report | Observation Count | Useful Buckets |
|---|---|---:|---|
| Volatile five: POET/RGTI/MXL/NVTS/OUST | Archived loose report | 384 | Positive/new general news and earnings/guidance had favorable 1d-5d forward returns, but only as event-study buckets. |
| Long five: MU/SNDK/SPOT/STX/WDC | Archived loose report | 600 | Positive or neutral/new general news with mixed-bullish or bullish-confirmed technical state was strongest over 3d-5d. |

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

## 2026-07-24 Corrected $100B+ Swing Audit

The first audit was invalid because `260` indicator bars had been interpreted as
`260` calendar warm-up days. The engine now fails closed when a ticker lacks the
required completed warm-up bars, and the corrected runs use `400` calendar days
of warm-up before a separate `180` calendar-day evaluation window. Daily
strategies also skip the entry bar for stop/target evaluation, use completed bars
only, and apply universe/regime eligibility before portfolio simulation.

Two research-only strategies were evaluated on 154 current Finviz $100B+ names,
split into four independent $100,000 portfolios. Alpaca SIP cached bars, 10 bps
slippage, fees, participation limits, independent strategy capital, full
retention, a 30% chronological holdout, and rolling walk-forward reports were
enabled.

| Strategy | Net on $400K | Return | Trades | Wins | Max partition DD | Holdout net | Verdict |
|---|---:|---:|---:|---:|---:|---:|---|
| RESEARCH Swing Mean Reversion RSI2 Recovery V2 | -$4,282.14 | -1.07% | 263 | 135 (51.3%) | 9.25% | +$6,317.57 | Reject. Three of four full partitions lost; one partition supplied the holdout gain. |
| RESEARCH Minervini Trend Template Breakout Proxy V5 | +$10,825.00 | +2.71% | 20 | 6 (30.0%) | 1.38% | -$1,438.44 | Do not promote. All five holdout trades lost; six 10R target hits supplied all aggregate profit. |

Retained result files:

- `data/backtest/results/shared/portfolio/audit-corrected-swing-p1-20260724-0001.json`
- `data/backtest/results/shared/portfolio/audit-corrected-swing-p2-20260724-0001.json`
- `data/backtest/results/shared/portfolio/audit-corrected-swing-p3-20260724-0001.json`
- `data/backtest/results/shared/portfolio/audit-corrected-swing-p4-20260724-0001.json`

Failure anatomy:

- Mean reversion generated 263 accepted trades. RSI2 recovery exits contributed
  `+$45,827.93`, but 68 stops lost `-$47,133.76` and 14 five-bar timeouts lost
  another `-$6,728.81`. This is not an entry-frequency problem. V2 uses the
  observed stretch/swing low as its structural stop and therefore bypasses the
  percentage-price ATR cap. The reclaim condition still does not distinguish
  non-fundamental liquidity shocks from continuing fundamental repricing.
- The Minervini proxy is deliberately simple and no longer uses MACD or RSI.
  Its strongest blockers were failure to hold above SMA50, missing pre-breakout
  volume dry-up, and an incomplete trend stack. It is still only a proxy: the
  engine does not yet implement point-in-time cross-sectional relative-strength
  rank, fundamental earnings acceleration, or a faithful multi-swing VCP pivot.
- The Minervini result is concentrated rather than repeatable. The six winners
  were MU twice, CSCO, GOOGL, GOOG, and TD. Fourteen losses were small, but the
  five chronological holdout trades were all losses.
- At the time of this older partition audit, `risk_per_trade_pct` combined two
  meanings. That defect is now removed: account risk, maximum position notional,
  and maximum planned loss relative to deployed notional are separate settings,
  and structural stops are rejected rather than tightened. The unified audit
  below is the first result using those corrected semantics.
- Both strategies underperformed the retained SPY benchmark in every partition.
- The universe was screened from the current Finviz snapshot and has no
  historical membership date. The result therefore retains an explicit
  survivorship/selection-bias warning and cannot be called production evidence.

Source-fidelity decision:

- Connors' published example is intentionally simple: above SMA200, RSI2 below
  5, then exit when RSI2 closes above 70. V2 now implements that dynamic exit
  and a five-trading-bar safety timeout. Adding generic MACD/VWAP gates would
  make it less faithful, not more robust.
- Minervini's documented method emphasizes stage-2 trend structure, high
  relative strength near 52-week highs, a genuine base/VCP and pivot, volume,
  fundamentals, immediate loss control, and selectivity. V5 implements only
  the mechanically auditable price/volume subset and is named `Proxy`
  accordingly.

No strategy was copied into the validated paper/live strategy folder. The next
honest experiment requires new information, not threshold tuning on this same
sample: point-in-time universe membership and relative-strength rank for the
Minervini model; event/fundamental repricing exclusion plus a structural-stop
eligibility check for mean reversion.

## 2026-07-24 Unified Portfolio and Admission Audit

The corrected research candidates were rerun together through one shared
`$10,000` portfolio with four position slots. The run reused the established
`580d` normalized Alpaca SIP cache, retained full diagnostics, used the same
order-risk planner as paper/live, and conservatively treated a position that
entered and exited on one daily bar as active for that timestamp. This prevents
two strategies from claiming the same ticker when intrabar ordering is unknown.

The loose result was retired after the summary below was captured.

| Strategy | Return | OOS return | Trades | Wins | Max DD | Decision |
|---|---:|---:|---:|---:|---:|---|
| Mean Reversion Reclaim BT V1 | -2.77% | -0.15% | 21 | 2 | 2.91% | Reject |
| RSI2 Recovery V2 | -0.60% | +0.31% | 12 | 3 | 1.48% | Reject; positive holdout is only five trades |
| Connors RSI2 Oversold V3 | 0.00% | 0.00% | 0 | 0 | 0.00% | Blocked by the now-retired deployed-notional policy |
| Minervini Pivot VCP V6 | 0.00% | 0.00% | 0 | 0 | 0.00% | No valid VCP candidates in this sample |
| **Unified portfolio** | **-2.60%** | n/a | **25** | **4** | **3.28%** | Reject |

Admission findings:

- This section records the historical behavior of the now-retired
  deployed-notional stop-width policy. It is not the current order-planning
  contract.
- Connors V3 formed `445` completed-bar setup candidates. Every candidate was
  rejected because its structural/ATR stop implied more than the configured
  `1%` maximum loss of deployed notional. Examples ranged from `3.32%` to
  `9.46%`. The planner correctly rejected them and did not tighten the stop.
- Across all four strategies, `2,351` of `2,394` candidates were rejected by
  that same structural-stop limit, `14` by same-ticker overlap, and `4` because
  fixed execution costs left no room under the deployed-notional limit.
- VCP V6 did not reach portfolio admission. Its leading signal blockers were
  price below SMA50 (`8,128` bars), fewer than two valid contractions (`6,226`),
  and an incomplete SMA50/SMA150/SMA200 trend stack. Loosening these values on
  this sample would redefine the strategy and is not evidence-based.
- Four symbols failed strategy warm-up honestly: recent listings `SPCX` and
  `SKHY`, limited cached daily history for `AZN`, and insufficient pre-window
  bars for `SNDK`. The engine did not synthesize indicator history.
- Promotion is explicitly false with
  `point_in_time_universe_evidence_missing`. The current Finviz export is useful
  for diagnostics but cannot establish historical membership.

No research strategy was promoted from this run. The later owner decision
retired the deployed-notional stop-width cap: current runs preserve the stop,
size from account-equity risk, and apply a separate position-notional cap.

## 2026-07-24 Signal and Gate Correction Audit

The strict result above was followed by two controlled runs on the same cached
154-symbol universe, 180-day evaluation window, 400-calendar-day warm-up,
cost model, and validation split:

- a diagnostic run without the former deployed-notional stop-width cap
- a production-risk run using the corrected account-risk contract

The diagnostic run retained the 1% account-risk budget but disabled the former
deployed-notional stop-width cap. At the time it was a signal-quality
experiment; the same separation of account risk and position notional is now
the approved shared order-planning contract.

| Research control | Return | OOS return | Trades | Wins | Max DD | Walk-forward | Decision |
|---|---:|---:|---:|---:|---:|---|---|
| Connors RSI2 V3 (`RSI2 < 5`) | -3.24% | +0.68% | 94 | 47 | 12.44% | 6 positive / 6 negative | Reject |
| Connors Deep RSI2 V4 (`RSI2 < 2`) | +1.12% | +3.28% | 57 | 27 | 5.44% | 6 positive / 6 negative | Retain as research control only |
| Minervini Pivot VCP V6 | 0.00% | 0.00% | 0 | 0 | 0.00% | no active windows | Replaced by corrected volume semantics |
| Minervini Pivot VCP V7 | +3.09% | +0.67% | 6 | 4 | 0.68% | 7 positive / 1 negative active windows | Promising but too sparse; research only |

Audit conclusions:

- The original unified admission path reconstructed every resolved stop as a
  structural stop. Candidate stop provenance is now retained for audit and
  sizing. The old stop-width rejection codes no longer exist because the
  deployed-notional stop cap was removed.
- Connors V3 and V4 enter on the next executable bar after a completed daily
  signal. V4's deeper threshold reduced trades and drawdown, but its in-sample
  result was negative and only half of walk-forward windows were positive.
- VCP V7 changed only two correlated volume constraints: contraction volume may
  be at most 85% of its reference advance and does not have to decline
  monotonically in every contraction. It produced 18 candidates and six
  admitted trades, versus zero for V6.
- V7's six trades were HSBC, TTE, DELL, AVGO, APH, and GE. Four won and two
  lost. One DELL trade contributed `$242.47` of the total `$309.29`, so the
  result is concentrated and cannot establish a durable edge.
- Under the policy active for that historical run, all 130 V4 candidates failed
  the ATR stop-width limit and all 18 V7 candidates failed the structural
  stop-width limit. That result cannot establish deployability under the current
  account-risk contract and must be rerun before comparison.

The defensible next design is not another threshold search. It is a separately
validated multi-timeframe swing execution model: completed daily setup,
intraday confirmation, and a stop based only on information available before
the fill. It must preserve daily management semantics and avoid using the entry
bar's future low. Until that model exists and passes a broader point-in-time
universe test, V7 remains a research strategy.

## 2026-07-24 Completed 1h Confirmation Diagnostic

The loose offline result was retired after the summary below was captured.

| Strategy | Return | Net | Trades | Win rate | Max DD | Status |
|---|---:|---:|---:|---:|---:|---|
| RESEARCH Minervini Pivot VCP V7 | +2.4304% | +$243.04 | 1 | 100% | 0.0000% | Daily control; too sparse to promote |
| RESEARCH Swing Minervini Pivot VCP V8 - 1h Confirmation | +0.7475% | +$74.75 | 2 | 50% | 0.0735% | Shared timing/risk contract verified; too sparse to promote |

The run used 10 cached names, 400 calendar days of warm-up, a separate
180-calendar-day evaluation window, 10 bps slippage, fees, no news, and no
provider calls. All 10 ticker pipelines succeeded. V8 uses a completed daily
setup, completed 1h confirmation, next-eligible-bar fill, and completed
pre-fill stop/liquidity context. It remains in the backtest research folder and
has not been copied to validated paper/live strategies.
