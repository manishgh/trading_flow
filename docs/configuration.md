# Configuration

`trading_flow` uses one run file per mode plus shared strategy files.

```text
configs/
  backtest/finviz-reddit-ross-gapgo-bullflag-8-180d-10k-api-v2-confirmed-entry.yaml
  paper/alpaca-paper.yaml
  live/local-live-disabled.yaml
  strategies/*.yaml
```

Backtest, paper, and live configs have the same structure. Mode changes the data roots and execution sink, not the strategy brain.

## Run Config

Example: `configs/backtest/finviz-reddit-ross-gapgo-bullflag-8-180d-10k-api-v2-confirmed-entry.yaml`

```yaml
run_name: finviz-reddit-ross-gapgo-bullflag-8-180d-10k-api-v2-confirmed-entry
mode: backtest

engine:
  pipeline: tpl
  worker_count: 0
  bounded_capacity: 1000
  indicator_warmup_bars: 50
  fail_fast: false

time_window:
  type: rolling
  lookback_days: 180
  warmup_lookback_days: 60

market_data:
  provider: alpaca
  download_timeframes:
    - 5m
  derive_from: 5m
  raw_root: data/backtest/raw
  normalized_root: data/backtest/normalized
  results_root: data/backtest/results
  cache_policy: refresh

providers:
  alpaca:
    data_feed: sip

strategies:
  - ../strategies/intraday-ross-gapgo-bullflag.v2-confirmed-entry.yaml
```

## Providers

Supported market-data providers:

- `alpaca`: primary provider for backtest and paper/live market data.
- `csv`: local replay provider for deterministic tests and imported datasets.
- `finviz`: screener/news enrichment only, not the primary candle source.

Alpaca credentials are read from `appsettings.local.json` in the web app or from environment variables for CLI/worker execution:

```text
ALPACA_KEY_ID
ALPACA_SECRET_KEY
```

`appsettings.local.json` is ignored by git and should be replaced by Key Vault in production.

## Signal Source

The remaining active signal source is internal candle evaluation:

```yaml
signal_source:
  type: internal_candles
  require_signature: false
  secret_env:
  dedupe_window_seconds: 300
  reject_stale_after_seconds: 0
```

External signal webhook support was removed during cleanup. Future webhook support should be provider-neutral and enter the event pipeline as a `StrategyDecisionEvent`, not as a provider-specific shortcut.

## Timeframes

Prefer downloading the smallest execution timeframe and deriving larger signal/confluence timeframes when needed:

- Download `5m`.
- Derive `15m` only when a selected strategy uses `timeframe: 15m`.
- Derive `1h` only when a selected strategy uses `timeframe: 1h` or enables `confluence.timeframe: 1h`.

Each strategy declares its own roles:

```yaml
timeframe: 15m
confluence:
  enabled: true
  timeframe: 1h
execution:
  timeframe: 5m
```

## Data Folders

Data is separated by run mode:

```text
data/
  backtest/
    raw/
    normalized/
    results/
  paper/
    raw/
    normalized/
    results/
  live/
    raw/
    normalized/
    results/
```

Backtest result JSON is written under an owner folder to prevent Web UI and worker processes from clobbering each other:

```text
data/backtest/results/web/portfolio
data/backtest/results/worker/portfolio
```

Set `TRADINGFLOW_RESULT_OWNER` to override the owner.

## Artifact Retention

Runtime UI and worker runs keep summary artifacts by default:

```yaml
artifacts:
  retention_mode: summary
```

`summary` preserves portfolio metrics, winner, ticker outcomes, validation, diagnostics, and trade counts, but does not persist trade-level arrays or accepted order detail. Use `retention_mode: full` only for explicit research/archive runs where full trade detail is required.

## Portfolio Allocation

```yaml
portfolio:
  starting_capital: 100000
  risk_per_trade_pct: 1.0
  max_position_value_pct: 20.0
  max_concurrent_positions: 5
  fixed_buy_fee: 1.0
  fixed_sell_fee: 1.0
  prevent_overlapping_ticker_positions: true
```

`risk_per_trade_pct` sizes by stop distance. `max_position_value_pct` and `max_concurrent_positions` cap allocation so simultaneous ticker trades share account equity.

## Strategy Families

Supported `entry_rules.setup_type` values:

- `vwap_pullback`
- `opening_range_breakout`
- `trend_pullback`
- `momentum`
- `ross_gap_go_bull_flag`

Current active strategy files:

- `intraday-ross-gapgo-bullflag.v2-confirmed-entry.yaml`
- `intraday-ross-gapgo-bullflag.v2-confirmed-entry.yaml`
- `intraday-ross-gapgo-bullflag.v2-confirmed-entry.yaml`
- `intraday-ross-gapgo-bullflag.v2-confirmed-entry.yaml`

The active day-trading strategy uses RSI for selection, review, and audit context, not as an entry or exit veto. Its configured RSI range is `0-100`, so momentum runners are not rejected merely because RSI is high.

Confirmed-entry variants can enable `entry_rules.enable_entry_bar_confirmation`. Backtests then use the first execution candle after the signal as confirmation and fill on the following candle, avoiding same-candle confirmation look-ahead. The optional VWAP/Bollinger extension settings make overextended entries require a stronger confirmation close-location value.


## Current Pipeline

Backtest and paper/live modes prepare market data through the same bounded TPL Dataflow candle pipeline:

1. Batch-load configured provider timeframes for the ticker set.
2. Publish raw bars as candle events into a bounded `BufferBlock`.
3. Normalize, validate, and group bars through `TransformBlock`/`ActionBlock` stages.
4. Derive only the timeframes required by selected strategy roles.
5. Compute indicator snapshots after the configured warm-up history is available.
6. Run per-ticker strategy workers from prepared market state.
7. Emit chart metrics, decision audits, portfolio results, and the top-level `Winner` block where applicable.
