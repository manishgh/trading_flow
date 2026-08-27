# Configuration

`trading_flow` uses small run profiles per mode plus shared strategy files.
Ticker universes for backtests and paper trading come from database wishlists,
not from hand-maintained YAML ticker lists. The generated per-run files are
audit artifacts that capture the resolved wishlist/ticker snapshot used by that
specific run.

```text
configs/
  backtest/intraday-backtest-profile.yaml
  backtest/swing-backtest-profile.yaml
  backtest/strategies/*.yaml
  paper/alpaca-paper.yaml
  paper/alpaca-paper-swing.yaml
  live/local-live-disabled.yaml
  strategies/*.yaml
```

Backtest, paper, and live profiles keep the same shape. Mode changes the data
roots and execution sink, not the strategy brain.

## Run Profile

Example: `configs/backtest/intraday-backtest-profile.yaml`

```yaml
run_name: intraday-backtest-profile
mode: backtest

engine:
  pipeline: tpl
  worker_count: 0
  bounded_capacity: 2000
  indicator_warmup_bars: 200
  fail_fast: false

time_window:
  type: rolling
  lookback_days: 60
  warmup_lookback_days: 90

tickers:

market_data:
  provider: alpaca
  download_timeframes:
    - 1m
    - 5m
  derive_from: 1m
  raw_root: data/backtest/raw
  normalized_root: data/backtest/normalized
  results_root: data/backtest/results
  cache_policy: use_cache

providers:
  alpaca:
    data_feed: sip

strategies:
  - ../strategies/intraday-ema10-ema20-macd-volume.v1.yaml
```

## Providers

Supported market-data providers:

- `alpaca`: primary provider for backtest and paper/live market data.
- `csv`: local replay provider for deterministic tests and imported datasets.
- `finviz`: screener/news enrichment only, not the primary candle source.

Alpaca credentials are read from .NET user-secrets in hosted local development or
from environment variables in any process:

```text
ALPACA_KEY_ID
ALPACA_SECRET_KEY
```

Configure hosted development without creating a repository file:

```powershell
dotnet user-secrets --project src/TradingFlow.Web set "Alpaca:KeyId" "<paper-key-id>"
dotnet user-secrets --project src/TradingFlow.Web set "Alpaca:SecretKey" "<paper-secret-key>"
dotnet user-secrets --project src/TradingFlow.WarmupService set "Alpaca:KeyId" "<paper-key-id>"
dotnet user-secrets --project src/TradingFlow.WarmupService set "Alpaca:SecretKey" "<paper-secret-key>"
```

The CLI reads `ALPACA_KEY_ID` and `ALPACA_SECRET_KEY`. The ignored
`appsettings.local.json` path remains a local migration fallback only. Azure Key Vault
is required for deployed paper/live environments.

## Runtime Discovery And Stream

Generated paper/live run snapshots carry the point-in-time discovery policy:

```yaml
discovery:
  enabled: true
  refresh_seconds: 60
  source_expiry_seconds: 180
  wishlist_id: "..."
  wishlist_tickers: []
  finviz_query: ""
  finviz_tickers: []
  operator_tickers: []
  alert_tickers: []
  news_tickers: []
  earnings_tickers: []
```

The hosted stream settings are ordinary .NET configuration keys:

```text
TradingFlow:MarketState:ResourceKey = alpaca:paper:sip
TradingFlow:MarketState:LeaseSeconds = 30
TradingFlow:MarketState:RenewEverySeconds = 10
TradingFlow:MarketState:SymbolPipelineCapacity = 256
TradingFlow:MarketState:RevisionAcceptanceMinutes = 2
TradingFlow:MarketState:RecoveryLookbackDays = 10
TradingFlow:MarketState:ActiveSessionStalenessMinutes = 3
```

The service fails closed if the lease, subscription acknowledgement, recovery,
bar continuity, or active-session freshness cannot be proven.

`SqliteMarketStreamLeaseRepository` is the local/single-node lease implementation.
Do not use a shared SQLite file as a multi-node clock authority. Before scaling the
stream owner across nodes, bind `IMarketStreamLeaseRepository` to one external lease
authority (for example Azure Blob lease or a server-timed relational store); fencing
tokens and the one-active-owner contract remain unchanged.

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

Runtime UI, worker, paper, and research runs keep full audit artifacts by default:

```yaml
artifacts:
  retention_mode: full
```

`full` preserves portfolio metrics, winner, ticker outcomes, validation, diagnostics, trade-level arrays, and accepted order detail. Use `retention_mode: summary` only for intentionally lightweight optimization runs where full replay/audit is not required.

## Portfolio Allocation

```yaml
portfolio:
  starting_capital: 100000
  account_risk_budget_pct: 1.0
  max_position_notional_pct: 20.0
  max_concurrent_positions: 5
  fixed_buy_fee: 1.0
  fixed_sell_fee: 1.0
  prevent_overlapping_ticker_positions: true
```

These limits are intentionally separate:

- `account_risk_budget_pct` caps planned loss as a percentage of account equity.
- `max_position_notional_pct` caps capital deployed into one position.
- The strategy's structural or ATR invalidation price is preserved. Quantity is
  reduced until stop loss plus estimated fees and slippage fit the account-risk
  budget; the engine does not tighten the stop.
- `max_concurrent_positions` limits shared portfolio slots.

For example, a `$10,000` account with `account_risk_budget_pct: 1.0` has a
maximum planned account loss of `$100` per trade before portfolio constraints.
A wider valid stop produces fewer shares. It does not redefine the stop or
impose a 1% stop distance relative to deployed notional.

Backtests include both an independent portfolio result for each strategy and a
`UnifiedPortfolio` result where all configured strategies compete chronologically
for the same capital and slots. The unified result is the deployable comparison.

## Strategy Families

Supported `entry_rules.setup_type` values in the retained and research strategy set include:

- `indicator_stack`
- `vwap_pullback`
- `atr_compression_breakout`
- `macd_divergence_fade`
- `swing_reclaim`
- `swing_rollover`
- `avwap_pullback_bounce`
- `episodic_pivot_gap`
- `volatility_contraction_pattern`
- `vwap_reclaim_trap`

The authoritative classification is `configs/strategy-catalog.json`. Its current
research set contains six exact artifacts:

- `intraday-ema10-ema20-macd-volume.v1.yaml`
- `lance_breitstein_intraday_tactics.yaml`
- `minervini-trend-template-vcp.v4-trend-rider.yaml`
- `intraday-atr-compression-breakout.bt-v1.yaml`
- `intraday-vwap-momentum-pullback.bt-v1.yaml`
- `swing-connors-rsi2-oversold.bt-v3.yaml`

The other fifteen strategy files are explicitly archived. Neither directory
membership nor a positive run grants execution permission. Backtest sees research
artifacts; creating a paper experiment uses research as source material; paper
experiment, paper shadow, and live selectors require their own exact-identity
authorization. Both paper execution catalogs intentionally start empty.

Paper callers must select `experiment` or `shadow` explicitly. Missing or unknown
modes are rejected; they do not default to a more permissive catalog. Strategy
authorization is not a YAML setting: exact grants and durable entry-admission tokens
are stored in `data/research/evidence/strategy-authorizations.db`. Generated run YAML
records the selected identity and mode for audit but cannot create a grant.

The retained day-trading research strategy uses RSI for audit context and broad
filtering only. Entry research is driven by price/VWAP and EMA state, bullish MACD
histogram, and cumulative same-time volume participation.

## Execution Rule Fields

The strategy engine supports generic execution fields:

```yaml
entry_rules:
  opening_range_break_buffer: 0.10

exit_rules:
  initial_stop_mode: atr                 # atr | vwap_minus_atr | vwap_plus_atr | opening_range_opposite | extreme_shadow | flush_low | swing_low
  profit_target_mode: r_multiple         # r_multiple | vwap
  enable_failed_breakout_circuit_breaker: true
  failed_breakout_bars: 3
  failed_breakout_min_r: 0.0
  stop_tick_buffer: 0.01
```

Partial exits are not config-supported yet. A strategy can set one full-position target, stop, trailing stop, max hold, or technical exit. Scale-out rules need a trade-lot model before they are production safe.


## Current Pipeline

Backtest and paper/live modes prepare market data through the same bounded TPL Dataflow candle pipeline:

1. Batch-load configured provider timeframes for the ticker set.
2. Publish raw bars as candle events into a bounded `BufferBlock`.
3. Normalize, validate, and group bars through `TransformBlock`/`ActionBlock` stages.
4. Derive only the timeframes required by selected strategy roles.
5. Compute indicator snapshots after the configured warm-up history is available.
6. Run per-ticker strategy workers from prepared market state.
7. Emit chart metrics, decision audits, portfolio results, and the top-level `Winner` block where applicable.


