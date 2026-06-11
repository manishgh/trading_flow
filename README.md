# trading_flow

`trading_flow` is a config-driven trading research and paper-trading system built around one shared decision brain for backtesting, paper trading, and live trading.

## Principles

- One strategy, confluence, risk, and portfolio brain across modes.
- Alpaca is the primary market data, news, paper broker, and future live broker adapter.
- Finviz is retained only for screeners and optional news enrichment.
- CSV remains available for local deterministic replay.
- Strategies are versioned YAML files.
- Strategy configs own signal timeframe, execution timeframe, and optional confluence timeframe.
- Backtests produce portfolio-level results, not isolated ticker wins.
- Live execution stays disabled until backtests, paper trading, and risk controls are validated.

## Stack

- C#/.NET 10 for the engine, ASP.NET Core UI, and worker/CLI.
- Python/FastAPI for the optional FinBERT sentiment sidecar.
- YAML for strategies and run configs.
- SQLite EF Core for local jobs, decision audits, ticker locks, and paper order state.
- Mode-scoped data folders under `data/backtest`, `data/paper`, and `data/live`.
- TPL/task-based concurrency with a bounded channel candle-event pipeline for market data preparation.
- Polly bulkheads and retry policy around external APIs.

## Alpaca News And Sentiment

Alpaca news is read through `https://data.alpaca.markets/v1beta1/news` and converted into the shared catalyst stream used by backtest, paper, and live modes. The provider follows Alpaca pagination, caches articles by provider/id, maps article metadata, and applies sentiment through a replaceable analyzer.

By default, sentiment uses local VADER scoring. To use FinBERT, run the sidecar and set `FINBERT_SENTIMENT_URL`:

```powershell
cd sidecars\finbert-sentiment
.\.venv\Scripts\uvicorn app:app --host 127.0.0.1 --port 8088
$env:FINBERT_SENTIMENT_URL = "http://127.0.0.1:8088"
```

See `sidecars/finbert-sentiment/README.md` for installation and Docker instructions.

## Modes

```text
backtest: historical Alpaca/CSV bars -> simulated order router
paper:    current Alpaca data/news -> Alpaca paper order router
live:     current Alpaca data/news -> live broker router, disabled by default
```

All three modes should call the same strategy, indicator, confluence, risk, and portfolio logic.

## Config Driven

The engine is intentionally controlled by config:

- tickers
- provider
- strategy thresholds
- signal/execution/confluence timeframes
- risk settings
- portfolio position slots
- session windows
- warm-up bars
- concurrency
- cache policy

See [configuration.md](docs/configuration.md) and [backtesting-bias-controls.md](docs/backtesting-bias-controls.md).

## Web UI

Run the local UI:

```powershell
dotnet run --project src\TradingFlow.Web --urls http://127.0.0.1:5088
```

Pages:

- `/` dashboard and recent jobs.
- `/Backtests` config-driven backtest lab with ticker selection, strategy selection, editable strategy parameters, progress, result preview, winner, trades, and diagnostics.
- `/Paper` paper-mode config inspection, Alpaca credential validation, paper run launch, live metrics, recent decisions, audit links, and broker controls.
- `/Audit/{runName}` decision-tree audit for accepted, rejected, and no-signal evaluations.

Generated backtest configs are written under `configs/backtest/ui-runs`; per-run strategy overrides are written under `configs/backtest/ui-runs/strategies/<run-name>`.

## Rolling Candle Warmup

Generate or refresh a rolling normalized candle and indicator cache:

```powershell
dotnet run --project src\TradingFlow.Cli -- warm-candles configs\backtest\finviz-reddit-ross-gapgo-bullflag-8-180d-10k-api-v2-confirmed-entry.yaml
```

The command writes atomic CSV replacements under a window-named folder such as `data/backtest/normalized/70d` by default and also writes `_warmup_manifest.json`. Backtests use `time_window.lookback_days` for the scoring window and `time_window.warmup_lookback_days` for data before that scoring window. Example: `lookback_days: 10` plus `warmup_lookback_days: 60` loads 70 days and scores only the last 10.

## Azure Deployment

Azure Container Apps assets live under `deploy/azure`, AKS assets live under `deploy/aks`, and container build files live under `deploy/docker`.

Expected production services:

- `TradingFlow.Web` ASP.NET Core UI
- C# trading/backtesting worker
- Python FinBERT sentiment sidecar/service

Secrets should be split by environment and stored in Azure Key Vault. Local paper credentials can live in `src/TradingFlow.Web/appsettings.local.json`, which is ignored by git.

## Active Strategy Set

The active strategy catalog is under `configs/strategies`:

- `intraday-ross-gapgo-bullflag.v2-confirmed-entry`

The current winner is reported in each result JSON under the top-level `Winner` property.

The retained day-trading runner keeps RSI as a selection/audit signal, not an entry or exit blocker. Its RSI range is configured as `0-100`; price action, VWAP, relative volume, bull-flag/opening-range breakouts, stops, and end-of-day flattening drive the trade lifecycle.

The retained research baseline is `configs/backtest/finviz-reddit-ross-gapgo-bullflag-8-180d-10k-api-v2-confirmed-entry.yaml`, with the successful result kept at `data/backtest/results/shared/portfolio/finviz-reddit-ross-gapgo-bullflag-8-180d-10k-api-v2-confirmed-entry.json`. The confirmed-entry research variant is `configs/backtest/finviz-reddit-ross-gapgo-bullflag-8-180d-10k-api-v2-confirmed-entry.yaml`; it waits for next-candle confirmation and then fills on the following candle to avoid confirmation look-ahead.


## Safety

No live broker routing should be enabled until:

- data quality checks pass
- strategy backtests are reviewed
- paper trading behaves correctly
- position sizing and risk limits are verified
- broker writes are idempotent and audited
