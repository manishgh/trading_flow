# Backtesting Bias Controls

`trading_flow` uses defensive backtesting semantics:

- Indicators are computed chronologically per ticker and timeframe.
- Strategy evaluation starts only after `engine.indicator_warmup_bars`.
- A setup calculated from a completed bar is not tradable on that bar. With no
  separate confirmation, it enters at the next eligible bar open plus configured
  slippage.
- When `execution.confirmation.enabled` is true, confirmation is evaluated only
  on completed bars after the setup became available and within
  `max_bars_after_setup`. Backtests fill at the next eligible bar after the
  confirmation bar; paper/live become order-ready only after that bar closes.
- Initial structural/ATR stops, relative-volume execution context, VWAP context,
  and liquidity participation use completed pre-fill bars. They cannot inspect
  the fill bar's future high, low, close, or volume.
- Stop, target, and max-hold exits are evaluated from the entry bar forward. When
  a daily entry bar contains both stop and target, the engine chooses the
  conservative stop outcome because intrabar ordering is unavailable.
- Derived candles are built from lower-timeframe bars before indicators are calculated.
- Each strategy receives an independent portfolio starting from `portfolio.starting_capital`.
- Portfolio accounting rejects overlapping same-ticker positions when configured.
- Out-of-sample trade partitions are recorded when configured.
- Walk-forward windows are recorded when configured.
- Benchmark buy-and-hold comparison is recorded when configured.
- Data-quality warnings are emitted for duplicate bars, invalid OHLC bars, and excess zero-volume bars.
- Survivorship and corporate-action adjustment risks are explicit validation warnings when the configured universe or price policy is weak.
- Promotion fails closed unless archived point-in-time universe membership and
  provider-snapshot evidence accompany the run. A current screener export can be
  used for diagnostics but cannot be promoted.
- Research datasets never fabricate pre-provider or pre-listing history. The global
  history floor is 2016, but a later-listed company is admitted from its real
  point-in-time membership date after completing the selected strategy's genuine
  warm-up. It is not excluded merely because it did not exist in 2016.
- Development, validation, embargo, and untouched holdout dates are frozen per
  experiment from the dataset manifest's actual complete coverage. No partition can
  begin before 2016 or before the required point-in-time evidence exists.
- Multiple strategies are also evaluated in one unified chronological portfolio,
  sharing capital and position slots; the sum of independent strategy portfolios
  is not reported as deployable portfolio performance.
- Full-retention diagnostics record portfolio admission failures separately from
  signal failures, including regime, universe, overlap, capacity, liquidity, and
  shared order-risk-plan rejections.
- Candidate trades retain resolved stop provenance. ATR and structural stops
  share the same numeric risk planner but keep distinct rejection codes for a
  truthful audit trail.
- Entry and exit routing obey the configured regular/extended-hours execution
  session. Extended-hours bars may still be observed by market-state indicators;
  observing them does not authorize an extended-hours order.

Remaining research hardening:

- archive and use provider-backed point-in-time membership snapshots rather than
  a current screener export,
- use a corporate-action-adjusted historical provider for long-horizon research,
- add liquidity and partial-fill modeling beyond notional caps and slippage.
