# Warm-Up Policy

TradingFlow separates the evaluation window from the data warm-up window.
Strategies should never trade on warm-up bars; warm-up exists only to seed
indicators, rolling volume baselines, and paper/live recovery state.

## Minimums

- Intraday strategies need at least 1 prior trading day before live decisions.
- Swing strategies need at least 1 prior trading week before live decisions.
- Relative-volume strategies should use more history when available. The
  indicator engine compares current cumulative/slot/session volume with up to
  63 prior comparable sessions, so 60-90 calendar days is a better practical
  intraday warm-up when we can fetch it.
- Swing trend strategies that use daily SMA/EMA context should prefer 200-260
  calendar days so 50/150/200-day moving averages are meaningful.

## Current Config Defaults

- Paper intraday: `configs/paper/alpaca-paper.yaml`
  - `lookback_days: 10`
  - `warmup_lookback_days: 90`
  - downloads `1m`, `5m`, `15m`, `1h`, `1d`
- Paper swing: `configs/paper/alpaca-paper-swing.yaml`
  - `lookback_days: 260`
  - `warmup_lookback_days: 260`
  - downloads `1h`, `1d`
- Intraday backtest profile: `configs/backtest/intraday-backtest-profile.yaml`
  - `lookback_days: 60`
  - `warmup_lookback_days: 90`
- Swing backtest profile: `configs/backtest/swing-backtest-profile.yaml`
  - `lookback_days: 180`
  - `warmup_lookback_days: 260`

## Runtime Behavior

Backtests fetch from `evaluation_start - warmup_lookback_days` through the end
of the evaluation window, but only bars at or after the evaluation start can
generate trades. Paper/live fetches the rolling warm-up window on each
iteration, computes indicators over the full set, and evaluates only the latest
bar. The candle store can persist those bars locally for recovery and later
blob archival.
