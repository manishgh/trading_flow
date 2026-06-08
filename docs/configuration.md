# Configuration

`trading_flow` now uses one run file per mode/run plus shared strategy files.

```text
configs/
  backtest/semiconductors-research.yaml
  backtest/semiconductors-suggestion-lab.yaml
  paper/local-yahoo-paper.yaml
  live/local-live-disabled.yaml
  strategies/*.yaml
tradingview/
  pine/*.pine
```

That is the intended operating model: if you want a 60-day Yahoo backtest using 5-minute data, you edit one run file. Strategy behavior lives in versioned strategy files.

The ASP.NET UI follows the same rule. Backtest runs launched from `/Backtests` generate YAML under:

```text
configs/backtest/ui-runs
configs/backtest/ui-runs/strategies/<run-name>
```

Those generated files are normal run configs and per-run strategy configs. Editable strategy parameters in the UI are written into the generated strategy YAML and then referenced by the generated run YAML, so the engine still runs from config rather than hidden UI state.

## Backtest Run

Example: `configs/backtest/semiconductors-research.yaml`

```yaml
run_name: semiconductors-research
mode: backtest

time_window:
  type: rolling
  lookback_days: 60

market_data:
  provider: yahoo
  download_timeframes:
    - 5m
  derive_from: 5m
  raw_root: data/backtest/raw
  normalized_root: data/backtest/normalized
  results_root: data/backtest/results

strategies:
  - ../strategies/intraday-vwap-pullback.v2.yaml
  - ../strategies/opening-range-breakout.v2.yaml
  - ../strategies/swing-trend-pullback.v1.yaml
  - ../strategies/swing-vwap-momentum.v2.yaml
  - ../strategies/minervini-trend-breakout.v2.yaml
```

Backtest runs use internal candle signals:

```yaml
signal_source:
  type: internal_candles
  require_signature: false
  secret_env:
  dedupe_window_seconds: 300
  reject_stale_after_seconds: 0
```

Paper/live runs can use TradingView Pine webhooks:

```yaml
signal_source:
  type: tradingview_webhook
  require_signature: true
  secret_env: TRADINGVIEW_WEBHOOK_SECRET
  dedupe_window_seconds: 300
  reject_stale_after_seconds: 60
```

`secret_env` names the environment variable that the webhook intake service should use for request verification. The validator accepts only strategies listed in the current mode's `strategies:` block, so paper can test one set while live runs only proven versions.

## eToro

eToro is configured under `providers.etoro` for paper/live modes. The implementation is shared; only environment and credentials differ.

```yaml
providers:
  etoro:
    enabled: true
    rest_base_url: https://public-api.etoro.com/api/v1
    websocket_url: wss://ws.etoro.com/ws
    environment: demo # demo | live
    default_leverage: 1
    order_sizing: units # units | amount
    demo:
      api_key_env: ETORO_DEMO_API_KEY
      user_key_env: ETORO_DEMO_USER_KEY
      allow_trading: true
    live:
      api_key_env: ETORO_LIVE_API_KEY
      user_key_env: ETORO_LIVE_USER_KEY
      allow_trading: false
    rate_limits:
      read_per_minute: 60
      write_per_minute: 20
      read_concurrency: 4
      write_concurrency: 1
      read_queue_limit: 32
      write_queue_limit: 4
    retry:
      max_retries: 5
      base_backoff_ms: 500
      max_backoff_seconds: 30
      jitter: true
```

The adapter sends `x-request-id`, `x-api-key`, and `x-user-key` on every REST request. Read calls retry `429`, `408`, `502`, `503`, and `504`. Reads and writes use separate Polly bulkheads plus per-minute throttling. Trading writes are intentionally not retried automatically unless a caller explicitly allows it, because duplicate order submission is more dangerous than a missed retry. The order router also blocks duplicate open/close write fingerprints inside a short dedupe window.

Demo and live credentials must be mounted into separate processes. The credential provider rejects a demo process that also sees live secret variables, and rejects a live process that also sees demo secret variables.

Trading route split:

```text
demo open:  POST /api/v2/trading/execution/demo/orders
live open:  POST /api/v2/trading/execution/orders
demo close: POST /api/v1/trading/execution/demo/market-close-orders/positions/{positionId}
live close: POST /api/v1/trading/execution/market-close-orders/positions/{positionId}
```

Close-position requests use the current demo/live close API body with `InstrumentID` and `UnitsToDeduct`.

Notification messages use:

```text
GET /api/v1/notifications/messages
```

The response is mapped as `messages[]` plus `meta.notSeen`. If the user key lacks notification permission, eToro returns `403 InsufficientPermissions`; the adapter reports that as an `EtoroApiException` rather than treating it as an empty notification feed.

## Timeframes

For backtesting, prefer downloading the smallest execution timeframe and deriving larger signal/confluence timeframes:

- Download `5m` from Yahoo.
- Derive `15m` only when a selected strategy uses `timeframe: 15m`.
- Derive `1h` only when a selected strategy uses `timeframe: 1h` or enables `confluence.timeframe: 1h`.

This keeps the run deterministic and prevents Yahoo's separate `5m` and `1h` feeds from disagreeing on partial candles, session boundaries, or update timing.

The run config does not list derived targets. The engine reads the selected strategy configs and derives only what is required:

```yaml
market_data:
  download_timeframes:
    - 5m
  derive_from: 5m
```

Each strategy declares its own roles:

```yaml
timeframe: 1h              # signal/setup timeframe
confluence:
  enabled: false
  timeframe: 1h
execution:
  timeframe: 5m            # entry, stop, target, trailing, exit fills
```

This lets a swing/trend strategy use `1h` to identify setup quality while still entering and managing exits on `5m`.

## Data Folders

Data is separated by run mode so historical simulation artifacts do not mix with paper or live trading state:

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

Each run config owns its mode-specific roots:

```yaml
market_data:
  raw_root: data/backtest/raw
  normalized_root: data/backtest/normalized
  results_root: data/backtest/results
```

Backtest result JSON is written under an owner folder to prevent Web UI and worker processes from clobbering each other:

```text
data/backtest/results/web/portfolio
data/backtest/results/worker/portfolio
```

Set `TRADINGFLOW_RESULT_OWNER` to override the owner. Paper and live runs should write to their own `results/<owner>/portfolio` folders.

## Strategy Families

The engine currently supports these `entry_rules.setup_type` values:

- `vwap_pullback`: price pulls into or reclaims session VWAP with volume, RSI, MACD, and optional VWAP-extension checks.
- `opening_range_breakout`: price closes above the configured opening range high after the range is complete.
- `trend_pullback`: price pulls into EMA20 or VWAP while trend filters remain constructive.
- `momentum`: the original threshold-only mode for older strategy configs.

The shared strategy YAMLs live in `configs/strategies`. Backtest, paper, and live configs decide which of those strategies they want to run by listing them under `strategies:`.

Current active strategy files are limited to positive-result candidates:

- `intraday-vwap-pullback.v2.yaml`
- `opening-range-breakout.v2.yaml`
- `swing-trend-pullback.v1.yaml`
- `swing-vwap-momentum.v2.yaml`
- `minervini-trend-breakout.v2.yaml`

Failed, zero-trade, or negative-result strategy configs were removed from the active catalog.

## Portfolio Allocation

The current portfolio config is:

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

`risk_per_trade_pct` sizes by stop distance. `max_position_value_pct` and `max_concurrent_positions` cap capital allocation so simultaneous ticker trades share the account instead of each assuming 100% of equity.

## Pipeline

The TPL pipeline runs by ticker. For each ticker it:

1. Downloads configured `download_timeframes`.
2. Derives only the signal/confluence timeframes required by selected strategies.
3. Computes indicators for every available timeframe.
4. Runs all configured strategies against the same prepared market state.
5. Builds independent portfolio results for each strategy from fresh starting capital.
6. Emits a top-level `Winner` block in result JSON.

So a single run can use `1h` for swing/trend setup and `5m` for execution without creating separate run configs.

## TradingView Simulation

The CLI can validate TradingView-style payloads generated from existing backtest result trades:

```powershell
dotnet run --project src/TradingFlow.Cli -- simulate-tradingview configs/paper/local-yahoo-paper.yaml data/backtest/results/portfolio/semiconductors-research.json
```

The command writes `tradingview-webhook-simulation.json` under the configured mode `results_root`. It verifies entry/exit payload shape, strategy matching, timeframe matching, bracket prices, stale checks, signature-required mode behavior, and duplicate event rejection.
