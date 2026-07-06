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

## Mandatory Agent Workflow

These rules apply to every Codex/agent session in this repository.

1. Keep validated strategies separate from research/backtest strategies. Validated/promoted strategies belong in the main strategy catalog only after evidence supports promotion. Experimental dimensions belong in separate backtest/research strategy files.
2. Do not mutate a promoted strategy directly for experiments. Copy the validated strategy into a backtest/research strategy file, add new dimensions there, run and audit it, then either promote it or discard it.
3. Promote only after verification. A strategy can move into the main active strategy folder/catalog only after backtest or paper evidence, audit review, and tests confirm the behavior.
4. Delete discarded backtest strategy changes and generated experimental configs. Keep only useful audit summaries/results needed for comparison.
5. Preserve research memory in code and tests when technical primitives change. If a technical indicator, evaluator rule, or signal field is added/removed, add or update focused unit tests and keep a short audit note/result so future agents know why it exists.
6. Stop TradingFlow runtime processes before code changes. Kill only project processes such as `TradingFlow.Web`, `TradingFlow.WarmupService`, workers, or CLI runs that may lock build outputs or mutate runtime files. Restart and verify services after the change when the user expects the app to remain available.
7. Never assume silently. If behavior depends on unknown user intent, market regime, data source, or wishlist purpose, inspect local context first; if still unclear and risk is material, ask before changing behavior.
8. Stay context-aware. Example: a wishlist named `volatile` is not automatically a swing universe. Match strategy horizon, data timeframe, warm-up depth, and candidate universe intentionally.
9. Keep code testable, concise, and clean. Prefer small generic engine primitives over strategy-specific branches. Add focused unit/integration tests for every non-trivial engine, risk, runner, API, or persistence change.
10. Maintain deterministic sample candle fixtures for tests:
    - Intraday: fixed 1m and 5m candle samples covering 30 days.
    - Swing: fixed 1d and 4h candle samples covering 6 months.
    These fixtures are for unit/integration determinism; live/backtest research can use larger cached datasets.

## Agentic Programming Best Practices

- Start from repository context. Read current code/config/data flow before editing; do not rely on stale memory.
- Plan experiments as reversible deltas. Prefer new config files, new test fixtures, or new strategy variants over modifying proven files in place.
- Keep one brain. Backtest, paper, and future live trading must share `SignalGenerator`, evaluator, risk, portfolio, and execution-decision logic.
- Separate concerns strictly: UI calls APIs/services; services orchestrate; engine computes; repositories persist; adapters talk to providers.
- Make changes observable. Add structured logs, rejection reasons, audit fields, or metrics when behavior would otherwise be invisible.
- Verify before promotion. Run relevant tests and at least one representative backtest/paper simulation for strategy/risk/pipeline work.
- Prefer deletion over compatibility layers when old code is not production intent, but preserve local secrets, DB state, and cached candle data unless explicitly told otherwise.
- Record failed experiments. If a tested strategy underperforms, keep the result in the research ledger and do not promote it.
- Do not optimize on a single lucky run. Compare drawdown, trade count, win rate, expectancy, and whether the rule makes market sense.
- Keep timestamps explicit. Calculations use New York market time, persisted data uses UTC, and UI/audit should show UTC plus local/operator time where useful.

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
- `configs/strategies/intraday-ema10-ema20-macd-volume.v1.yaml`
- `configs/strategies/swing-reversal-reclaim-bull-quality-no-news.v1.yaml`
- `configs/strategies/swing-overbought-rollover-short-no-news.v5.yaml`

Backtest profiles currently worth keeping:

- `configs/backtest/intraday-backtest-profile.yaml`
- `configs/backtest/swing-backtest-profile.yaml`

Backtest and paper universes must come from database wishlists. Generated
run files are audit artifacts only; do not reintroduce hand-maintained
backtest ticker-list configs.
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
- stale generated run artifacts under `configs/backtest/temp` or `configs/backtest/ui-runs` if they reappear
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
## Time Zone Convention

- Market calculations use New York exchange time (`America/New_York`) with DST. This applies to sessions, same-time volume baselines, VWAP session resets, and market-open/extended-hours labels.
- Persist timestamps in UTC where possible.
- Operator-facing audit/UI screens should display UTC, New York market time, and local Europe/Berlin time, plus a market-session label: `premarket_extended`, `regular_market`, `postmarket_extended`, `closed`, or `closed_weekend`.

