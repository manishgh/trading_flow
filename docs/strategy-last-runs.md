# Strategy Last Runs

This file is the short research ledger for strategy selection. Keep only the latest useful run for each retained strategy so the next optimization starts from known evidence.

## Current Intraday Candidates

| Strategy | Config | Last Result | Return | Max DD | Trades | Win Rate | Notes |
|---|---|---|---:|---:|---:|---:|---|
| TOP1 Intraday - Dual RVOL Lite V6 | `configs/strategies/intraday-ross-vwap-ema-cumulative-volume.v6-lite.yaml` | `data/backtest/results/shared/portfolio/poet-mxl-rgti-mu-msft-intraday-v6-lite-90d.json` | 11.79% | 1.24% | 43 | 60.5% | Current promoted default. Best return/drawdown balance on the retained validation basket. Keeps the simple Ross stack but uses stronger dual participation checks. |
| TOP2 Intraday - Ross VWAP EMA Structural Exit | `configs/strategies/intraday-ross-vwap-ema-volume-macd.v3-structural-exit.yaml` | `data/backtest/results/shared/portfolio/poet-mxl-rgti-mu-msft-intraday-v6-lite-90d.json` | 7.78% | 3.41% | 56 | 55.4% | Runner-up baseline. Simpler than V6 Lite and useful as a control when new filters are introduced. |

## Current Swing Candidates

| Strategy | Config | Last Result | Return | Max DD | Trades | Win Rate | Notes |
|---|---|---|---:|---:|---:|---:|---|
| TOP1 Swing Long - Reversal Reclaim Bull Quality | `configs/strategies/swing-reversal-reclaim-bull-quality-no-news.v1.yaml` | `data/backtest/results/shared/portfolio/swing-quality-long-overbought-short-v5-comparison-crdo-msft-app-intc-mu-nvda-180d-0001.json` | 11.95% | 3.85% | 10 | 60.0% | Best swing model. Winners came from INTC, CRDO, APP, NVDA; losses MSFT/MU. |
| TOP2 Swing Short - Overbought Rollover | `configs/strategies/swing-overbought-rollover-short-no-news.v5.yaml` | `data/backtest/results/shared/portfolio/swing-quality-long-overbought-short-v5-comparison-crdo-msft-app-intc-mu-nvda-180d-0001.json` | 3.66% | 0.00% | 2 | 100.0% | Promising but tiny sample. Keep as short-side candidate, not proven enough alone. |

## Trader Research Sources Kept

| Source File | Purpose |
|---|---|
| `configs/strategies/lance_breitstein_intraday_tactics.yaml` | Intraday VWAP/reclaim/trap research translation source. |
| `configs/strategies/brian_shannon_mta_avwap_strategies.yaml` | Anchored VWAP and market structure research source. |
| `configs/strategies/kristjan_qullamaggie_stream_methodology.yaml` | Momentum/swing stream research source. |
| `configs/strategies/mark_minervini_trade_like_a_stock_market_wizard.yaml` | Trend template/VCP research source. |

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
| V6 Lite dual RVOL | 11.79%, 1.24% DD, 43 trades | Promoted as TOP1 intraday. Best retained balance of return, drawdown, and simplicity. |
| Ross VWAP EMA Structural Exit V3 | 7.78%, 3.41% DD, 56 trades | Promoted as TOP2 intraday. Strong baseline and cleaner control strategy. |
| V5 Rising Volume | -0.23%, 2.40% DD, 60 trades | Retired. It reduced drawdown but filtered too many profitable continuation trades. |
| V6 Daily Quality | 1.08%, 3.92% DD, 70 trades | Retired. Daily EMA/MACD confluence helped some selection but increased drawdown. |
| Old Ross Gap-Go Bull Flag V2 on screenshot runners | -10.78%, 10.78% DD, 8 trades | Retired. The bull-flag pattern fired late and every trade lost. |

## 2026-06-13 Intraday Cross-Validation

The retained pair came out of the POET/MXL/RGTI/MU/MSFT 90-day comparison after making volume semantics explicit:

- V6 Lite won because it kept the simple Ross/VWAP/EMA/MACD stack while adding only the two volume features that carried the strongest signal in the feature audit.
- V3 stayed relevant because it is easier to reason about and is a better control strategy when we test future refinements.

The Python analyzer report at `data/research/ml/all-intraday-two-batch-20260613/research_report.md` found the strongest entry-time correlations around ATR%, close location, session-vs-average-day RVOL, slot RVOL, EMA distance, and volume SMA. These should feed deterministic config changes only; ML is not a trade executor.

## Next Design Step

The next real "bad stock no-trade" improvement should be a portfolio/risk guard, not hidden strategy math: pause a ticker after configurable realized losses or repeated failed signals during the same session, while keeping the strategy config portable between backtest and paper trading.
