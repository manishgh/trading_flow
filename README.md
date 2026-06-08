# trading_flow

`trading_flow` is a quantamental trading engine designed around one shared decision brain for backtesting, paper trading, and live trading.

The first milestone is a local C# backtesting engine with YAML strategy definitions, OHLCV data adapters, multi-ticker execution, and portfolio-level results. The Go news sidecar is a real service boundary for event and sentiment veto checks.

## Principles

- One strategy/risk/order brain across backtest, paper, and live modes.
- Data providers are adapters, not strategy dependencies.
- Strategies are versioned YAML configs.
- Strategy configs own signal timeframe, execution timeframe, and optional confluence timeframe.
- Backtests must produce portfolio-level results, not just isolated ticker wins.
- Live execution stays disabled until backtests, paper trading, and risk checks are validated.

## Initial Stack

- C#/.NET 10 for the core engine and ASP.NET UI.
- Go for the news and sentiment sidecar (located in `c:\project\news-sentiment-go`).
- YAML for strategies and run configs.
- Distributed State: SQLite EF Core database (`tradingflow.db`) is used for synchronizing active trades and locking tickers across concurrent worker pods.
- Local file storage first: raw provider payloads, normalized OHLCV bars, and result JSON.
- Mode-scoped data folders under `data/backtest`, `data/paper`, and `data/live`.
- TPL/task-based concurrency for pipeline stages and ticker-level parallelism.
- Rate Limiting: Centralized Polly Bulkhead rate limits across all API providers.
- TradingView Pine alerts as a paper/live signal source, while backtests keep deterministic internal candle signals.
- eToro as a separate broker/data adapter with one implementation and separate demo/live API keys.
- ASP.NET Core web UI for backtest runs, editable strategy parameters, generated configs, live run-state progress, paper readiness checks, result previews, and diagnostics.
- Dedicated `TradingFlow.Worker` host for production backtest/trading jobs.

## Planned Modes

```text
backtest: historical bars/news replay -> simulated order router
paper:    live/current data -> paper order router
live:     live data -> broker order router
```

All three modes call the same confluence, strategy, risk, and portfolio logic.

Paper and live modes can receive TradingView webhook alerts generated from Pine scripts under `tradingview/pine`. Those alerts are normalized into the same external signal contract, validated against the enabled mode strategy list, deduped, and then passed into the shared risk/order path.

The eToro adapter lives in `src/TradingFlow.Etoro`. It supports authenticated REST requests, read/write rate limits, Polly read/write bulkheads, 429 retry handling, duplicate write guards, instrument lookup, historical candles, rates, current demo/live order endpoints, portfolio/PnL/order status APIs, and WebSocket subscriptions for instrument and private streams.

## Config Driven

The engine is intentionally config driven:

- tickers,
- providers,
- paths,
- strategy thresholds,
- strategy signal/execution/confluence timeframes,
- risk settings,
- portfolio position slots,
- session windows,
- warm-up bars,
- concurrency,
- cache policy.

See `docs/configuration.md` for the config contract.

See `docs/backtesting-bias-controls.md` for the current anti-look-ahead and warm-up rules.

See `tradingview/pine/README.md` for Pine scripts and TradingView alert setup.

## Web UI

Run the local UI:

```powershell
dotnet run --project src\TradingFlow.Web --urls http://127.0.0.1:5088
```

Pages:

- `/` dashboard and recent jobs.
- `/Backtests` config-driven backtest lab with ticker selection, strategy selection, and editable strategy parameters.
- `/Job/{id}` live run state, progress log, result preview, winner, trades, and analyzer diagnostics.
- `/Paper` paper-mode config inspection and read-only eToro environment validation.

Generated backtest configs are written under `configs/backtest/ui-runs`; per-run strategy overrides are written under `configs/backtest/ui-runs/strategies/<run-name>`. Web results write under `data/backtest/results/web`.

## Azure Deployment

AKS deployment assets live under `deploy/aks`, with steps in `docs/azure-aks-deployment.md`.

Container build files live under `deploy/docker`:

- `Dockerfile.web`
- `Dockerfile.trading-service`
- `Dockerfile.news`

## Active Strategy Set

Only strategies with positive results in the latest 60-day semiconductor research run are kept in the shared strategy catalog:

- `intraday-vwap-pullback.v2`
- `opening-range-breakout.v2`
- `swing-trend-pullback.v1`
- `swing-vwap-momentum.v2`
- `minervini-trend-breakout.v2`

The current winner is reported directly in each result JSON under the top-level `Winner` property. The latest winner is `Swing VWAP Momentum v2`.

## Safety

No live broker routing should be enabled until:

- data quality checks pass,
- strategy backtests are reviewed,
- paper trading behaves correctly,
- position sizing and risk limits are verified,
- broker adapters are dry-run tested.
