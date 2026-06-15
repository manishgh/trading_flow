# Backtesting Research Retention and Runtime Warmup

## Intent

TradingFlow keeps research evidence separate from runtime state.

Research snapshots are small, reproducible artifacts used to compare strategy versions. Runtime data is mutable operational state for paper/live trading and should not be mixed with research output.

## Research Snapshots

Curated backtest snapshots live under:

```text
data/research/backtests/<run-id>/
```

Commit only compact comparison files:

- `manifest.json`
- `leaderboard.csv`
- `strategy_metrics.csv`
- `ticker_metrics.csv`
- `trades.csv`
- `diagnostics.json`
- `run_config.yaml`
- `strategy.yaml`
- `notes.md`

Do not commit raw candle caches, runtime paper/live state, temporary job logs, failed exploratory results, or regenerated provider payloads.

The current retained runtime baseline is:

```text
configs/strategies/intraday-ross-vwap-ema-cumulative-volume.v6-lite.yaml
configs/strategies/intraday-ross-vwap-ema-volume-macd.v3-structural-exit.yaml
configs/strategies/swing-reversal-reclaim-bull-quality-no-news.v1.yaml
configs/strategies/swing-overbought-rollover-short-no-news.v5.yaml
configs/backtest/poet-mxl-rgti-mu-msft-intraday-v6-lite-90d.yaml
configs/backtest/swing-quality-long-overbought-short-v5-comparison-crdo-msft-app-intc-mu-nvda-180d.yaml
data/research/backtests/2026-06-11_swing_6tickers_180d/
```

Older exploratory strategies and failed result artifacts should be treated as disposable once their lessons are captured. Candle caches remain separate from research results and should not be deleted during strategy cleanup.

## Strategy Promotion

Only promoted strategy configs should remain in active runtime paths. Failed candidates should be deleted after their failure reason is captured in a research manifest or notes file.

Promotion criteria should include:

- positive total return
- controlled max drawdown
- enough trades to avoid one-off conclusions
- no single ticker dominating the result
- diagnostics show rejected trades are explainable
- passes a broader validation window before paper/live promotion

For now, the retained active strategies are:

- `intraday-ross-vwap-ema-cumulative-volume.v6-lite.yaml`: promoted intraday default. It combines the Ross-style VWAP/EMA/MACD stack with dual RVOL gating (`cumulative_same_time` plus session RVOL floor) and the safer structural exit model.
- `intraday-ross-vwap-ema-volume-macd.v3-structural-exit.yaml`: retained intraday runner-up. It keeps the simpler Ross indicator stack and exits on confirmed VWAP failure plus EMA10/EMA20 structure.
- `swing-reversal-reclaim-bull-quality-no-news.v1.yaml`: primary swing long. It had the best balance of return and drawdown in the six-ticker swing run.
- `swing-overbought-rollover-short-no-news.v5.yaml`: candidate swing short. It is promoted as a separate short-side candidate because it captured the APP rollover and avoided the noisy false shorts from earlier variants, but its two-trade sample is not enough for live promotion by itself.

Latest retained intraday comparison on the POET/MXL/RGTI/MU/MSFT 90-day cached window:

- V6 Lite dual RVOL: +11.79%, max drawdown 1.24%, 43 trades.
- V3 structural exit: +7.78%, max drawdown 3.41%, 56 trades.

Latest retained swing comparison on the CRDO/MSFT/APP/INTC/MU/NVDA, 180-day cached Alpaca SIP window:

- Swing Reversal Reclaim Bull Quality No News V1: +11.9532%, average daily +0.0980%, max drawdown 3.8461%, 10 trades.
- Swing Overbought Rollover Short No News V5: +3.6649%, average daily +0.0300%, max drawdown 0%, 2 trades.

Conclusion: keep the long and short swing strategies separate until portfolio conflict handling is explicit enough to arbitrate simultaneous long/short candidates on the same ticker or sector.

## Paper and Live Warmup

Paper/live should maintain their own rolling warmup cache. It should not depend on backtest research folders.

Recommended runtime layout:

```text
data/runtime/paper/
  warmup/
  today/
  state/

data/runtime/live/
  warmup/
  today/
  state/
```

Runtime flow:

1. At startup, load the rolling 7 trading day warmup cache.
2. During the session, append today's candles/events to `today/`.
3. After market close, or in a daily midnight job, validate today's candles.
4. Merge today into warmup.
5. Trim warmup to the latest 7 trading days.
6. Write a manifest with symbols, timeframes, source, last update time, and status.

This can run as an in-process hosted service for paper trading first, then move to a dedicated worker when live reliability requires it.

## Backtesting UI Direction

The Backtests page should become a run lab:

- run builder for provider, lookback, capital, risk, cache policy
- universe selector with custom tickers and Finviz screener preview
- strategy selector with parsed strategy summaries
- recent result comparison from saved JSON
- diagnostics table with rejection reasons and validation warnings
- optimization moved into a separate advanced section

The engine already exposes strategy, ticker, trade, diagnostics, and validation data. The UI gap is cataloging, previewing, launching, and comparing those results cleanly.

## Logging and Metrics

Backtest and paper jobs should log standard structured lifecycle events:

- queued
- started
- config path
- ticker progress
- API/provider failures
- cache hits/misses
- result write path
- completed/failed/canceled

Metrics should include run duration, ticker failures, API calls, cache hit counts, accepted trades, rejected trades, and result artifact size.
