# Backtesting Bias Controls

`trading_flow` uses defensive backtesting semantics:

- Indicators are computed chronologically per ticker and timeframe.
- Strategy evaluation starts only after `engine.indicator_warmup_bars`.
- A signal calculated from a closed bar enters at the next bar open plus configured slippage.
- Stop, target, and max-hold exits are evaluated only from the entry bar forward.
- Derived candles are built from lower-timeframe bars before indicators are calculated.
- Each strategy receives an independent portfolio starting from `portfolio.starting_capital`.
- Portfolio accounting rejects overlapping same-ticker positions when configured.
- Out-of-sample trade partitions are recorded when configured.
- Walk-forward windows are recorded when configured.
- Benchmark buy-and-hold comparison is recorded when configured.
- Data-quality warnings are emitted for duplicate bars, invalid OHLC bars, and excess zero-volume bars.
- Survivorship and corporate-action adjustment risks are explicit validation warnings when the configured universe or price policy is weak.

Remaining research hardening:

- use a historical index-constituent universe provider instead of static ticker YAML,
- use a corporate-action-adjusted historical provider for long-horizon research,
- add liquidity and partial-fill modeling beyond notional caps and slippage.
