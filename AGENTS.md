# TradingFlow Agent Context

This file is the compact handoff context for Codex/VS CLI sessions working in `C:\project\trading_flow`.

## Purpose

TradingFlow is a config-driven trading research and paper-trading system. The core intention is one shared decision brain across:

- backtesting
- paper trading
- future live trading

The source/sink can change by mode, but the strategy signal, indicator, confluence, risk, portfolio, audit, and execution decision path should remain the same.

## Non-Negotiable Design Rules

- Do not sync GitHub unless explicitly asked.
- Preserve local secrets, `appsettings.local.json`, `tradingflow.db`, and paper-trading state unless explicitly told to clean them.
- Preserve cached candle/history data unless explicitly told to delete market data.
- Strategy behavior must be config-driven. Do not hard-code strategy-specific rules into base engine code except for generic indicator/signal primitives.
- UI code should manage web events, validation display, partial updates, and API calls only. Business orchestration belongs in services/runners/engine modules.
- Backtest, paper, and live should use the same signal calculator and evaluator path.
- Always verify changes with relevant tests. Prefer adding focused tests for engine/risk/pipeline changes.
- Use standard structured logging so output can later feed Logstash, Elastic, Azure Monitor, or another log sink.
- Avoid look-ahead bias: signal on completed bar, fill on a later executable bar, warm indicators before scoring, and model realistic slippage/costs.

## Current Architecture

Primary solution: `TradingFlow.sln` / `TradingFlow.slnx`

Important projects:

- `src/TradingFlow.Domain` - domain records and contracts.
- `src/TradingFlow.Engine` - indicator engine, signal generator, evaluator, candle pipeline.
- `src/TradingFlow.Backtesting` - backtest runner and paper/live runner.
- `src/TradingFlow.Data` - SQLite repositories, CSV/local data, local candle store.
- `src/TradingFlow.Alpaca` - Alpaca data/news/broker adapter.
- `src/TradingFlow.Finviz` - Finviz screener support only.
- `src/TradingFlow.Web` - ASP.NET Core Razor UI and mobile API endpoints.
- `src/TradingFlow.Tests` - unit/integration tests.
- `sidecars/finbert-sentiment` - optional Python FinBERT sentiment service.

Market data flow:

```text
IMarketDataProvider
  -> CandlePipelineEngine
  -> BufferBlock<CandleEvent>
  -> TransformBlock normalize/validate
  -> ActionBlock group by ticker/timeframe
  -> ICandleStore provider candle spill
  -> BarResampler derive missing timeframes
  -> ICandleStore derived candle spill
  -> IndicatorEngine
  -> TickerMarketState
  -> SignalGenerator
  -> BasicStrategyEvaluator
  -> Backtest simulator or paper/live broker sink
```

The current pipeline is TPL Dataflow-based and bounded. Paper/live currently runs batch-per-iteration; the target design is per-ticker warm in-memory pipelines with distributed ticker leases.

## Scale-Out Target

For hundreds of stocks:

- run one warm in-memory pipeline per assigned ticker
- assign tickers to pods by distributed lease, for example 10 tickers per pod
- renew leases while healthy
- let leases expire on crash/restart
- persist raw and derived candles locally via `ICandleStore`
- have a separate archive worker copy local candle files/manifests to Azure Blob

Do not put Azure Blob writes in the hot candle/strategy/broker path.

Current candle persistence:

- abstraction: `src/TradingFlow.Engine/Abstractions/ICandleStore.cs`
- local implementation: `src/TradingFlow.Data/Candles/LocalFileCandleStore.cs`
- registered in web DI as `data/candles`

Local layout:

```text
data/candles/{scope}/{runName}/{provider}/{source}/{timeframe}/{ticker}/{yyyy-MM-dd}.jsonl
data/candles/_archive-pending/{timestamp}-{manifestId}.json
```

`source` is currently `provider` or `derived`.

## Active Providers

- Alpaca is primary for market data, historical data, news, and paper broker.
- Finviz is retained for screener/ticker universe enrichment.
- CSV/local data remains useful for deterministic backtests.
- Yahoo, eToro, and TradingView webhook/signal paths are not active priorities.

## Active Configs

Paper configs:

- `configs/paper/alpaca-paper.yaml`
- `configs/paper/alpaca-paper-swing.yaml`

Strategy configs:

- `configs/strategies/intraday-ross-gapgo-bullflag.v2-confirmed-entry.yaml`
- `configs/strategies/intraday-ross-vwap-ema-volume-macd.v2.yaml`
- `configs/strategies/swing-reversal-reclaim-bull-quality-no-news.v1.yaml`
- `configs/strategies/swing-overbought-rollover-short-no-news.v5.yaml`

Backtest research configs currently worth keeping:

- `configs/backtest/finviz-reddit-ross-gapgo-bullflag-8-180d-10k-api-v2-confirmed-entry.yaml`
- `configs/backtest/swing-quality-long-overbought-short-v5-comparison-crdo-msft-app-intc-mu-nvda-180d.yaml`

Generated UI/temp configs can be deleted unless the user explicitly asks to preserve a run:

- `configs/backtest/temp`
- `configs/backtest/ui-runs`
- `configs/optimization/ui-runs`

## Data Policy

Preserve:

- `tradingflow.db`
- `tradingflow.db-shm`
- `tradingflow.db-wal`
- `src/TradingFlow.Web/appsettings.local.json` if present
- `data/candles`
- `data/backtest/normalized`
- curated backtest result summaries used for comparison

Safe cleanup targets:

- `.tmp` build/test/web/ngrok artifacts
- `tmp` if empty
- `configs/backtest/temp`
- `configs/backtest/ui-runs`
- `configs/optimization/ui-runs`
- `data/runtime/logs`
- `data/web/logs`
- old generated `results` folders if present and not curated

Do not delete normalized candle data or paper/live DB state without a fresh explicit instruction.

## Documentation

Most useful docs:

- `README.md` - project overview and commands.
- `docs/architecture.md` - high-level system design.
- `docs/configuration.md` - config model.
- `docs/backtesting-bias-controls.md` - no-look-ahead/backtest safety.
- `docs/candle-news-pipeline-audit.md` - current candle/news pipeline and scale-out notes.
- `docs/backtesting-research-retention.md` - artifact retention model.
- `docs/azure-aks-deployment.md` - AKS production deployment notes.

`docs/azure-deployment.md` is only a pointer to the AKS doc. Keep it if README or external notes link to it.

## Common Commands

Restore/build/test:

```powershell
dotnet restore C:\project\trading_flow\TradingFlow.sln
dotnet build C:\project\trading_flow\TradingFlow.sln
dotnet test C:\project\trading_flow\src\TradingFlow.Tests\TradingFlow.Tests.csproj
```

Run web:

```powershell
dotnet run --project C:\project\trading_flow\src\TradingFlow.Web --urls http://127.0.0.1:5088
```

Run a focused test:

```powershell
dotnet test C:\project\trading_flow\src\TradingFlow.Tests\TradingFlow.Tests.csproj --filter FullyQualifiedName~CandlePipelineEngineTests
```

## Last Verified State

Recent verification before this handoff:

- `dotnet test C:\project\trading_flow\src\TradingFlow.Tests\TradingFlow.Tests.csproj -p:OutDir=C:\project\trading_flow\.tmp\verify\candle-store-tests-out\`
- Result: 119 passed, 0 failed.
- Web health check passed on `http://127.0.0.1:53017/health` from the generated test output.

Because `.tmp` is a cleanup target, rerun tests after deleting it.

## Current Product Focus

Near-term work is paper trading and backtesting quality:

- make intraday strategies simple, explainable, and config-driven
- improve premarket/opening-range and long/short day-bias logic
- use Finviz RVOL for screener-based candidates where available
- keep StockIndicators for standard technical indicators
- use news catalysts mainly for swing and catalyst-driven intraday selection
- improve audit pages so accepted/rejected decisions show exact matched values and reasons

The user wants the system to be architecturally clean more than merely patched to pass one strategy.
