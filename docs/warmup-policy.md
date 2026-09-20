# Warm-Up Policy

TradingFlow supports swing strategies only. Warm-up reconstructs indicator state
before evaluation; warm-up bars are never eligible for signals or fills.

## Daily Swing State

- Maintain at least 260 completed daily bars for strategies using SMA200, its slope,
  or similarly long recursive indicators.
- The retained profiles request 400 calendar days to cover weekends, holidays, and
  stabilization margin.
- A later-listed security is eligible once its own real history satisfies the chosen
  strategy. History is never synthesized before listing.

## Sub-Daily Swing Confirmation

Completed `1h`, `15m`, or `5m` bars may be warmed when a swing strategy explicitly
uses them for entry confirmation or execution timing. The required depth is based on
the longest declared indicator plus stabilization margin. This state does not define
a separate trading horizon.

## Current Defaults

- Paper: `configs/paper/alpaca-paper.yaml`
  - `lookback_days: 260`
  - `warmup_lookback_days: 400`
  - downloads `1h` and `1d`
- Backtest: `configs/backtest/swing-backtest-profile.yaml`
  - diagnostic `lookback_days: 180`
  - `warmup_lookback_days: 400`
  - downloads `1h` and `1d`

## Rolling Maintenance

Paper/live warm state is restored from historical REST data, then maintained with
completed stream bars. After each session the store appends the completed daily bar,
trims beyond retention, and persists atomically. Backfill and stream overlap must be
deduplicated by `(symbol, timeframe, timestamp)` and checked for a gap.

The current local warm-up state is stored under `data/warmup/`. Production archive
copies are asynchronous and must never block the hot signal or broker path.
