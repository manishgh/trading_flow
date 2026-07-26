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

## Operating Boundaries

TradingFlow currently operates within these hard boundaries:

- **Product:** deterministic non-ML research, backtesting, paper trading, audit,
  and reviewed operator workflows. Live routing is disabled pending promotion.
- **Market:** US-listed equities using the New York exchange calendar. Market
  calculations use New York time; persisted timestamps use UTC.
- **Providers:** Alpaca SIP is the primary market/news/broker source. Finviz is a
  current screener and news-enrichment source, not historical membership evidence.
- **History:** research uses real provider-supported observations beginning no
  earlier than 2016. Exact observed coverage is recorded per dataset.
- **New listings:** 2016 is a data boundary, not an issuer-age filter. A later-listed
  company becomes eligible after its real point-in-time membership date and its own
  strategy-specific warm-up. Pre-listing history is never synthesized.
- **Universes:** promotable research requires point-in-time membership and delisting
  evidence. Current wishlists or Finviz exports may drive today's operation but may
  not be projected backward.
- **Research:** only cross-sectional swing momentum and catalyst/participation
  intraday research are active alpha tracks. ML belongs to the separate Market
  Predictor project.
- **Timing:** completed-bar features, later-bar confirmation, next-bar execution,
  pre-fill stops, point-in-time news, and one-use chronological holdouts are
  mandatory.
- **Execution:** costs, quote side, spread, slippage, participation, partial/non-fill,
  and impact assumptions must be explicit. Extended-hours observation does not
  authorize extended-hours execution.
- **Promotion:** missing universe, timing, execution, statistical, or paper evidence
  fails closed. The current integrated research decision is `RETAIN_RESEARCH`.

See [TradingFlow Operating Boundaries](docs/operating-boundaries.md) for the binding
contract and [Non-ML Strategy Research Program](docs/research/non-ml-strategy-research-program.md)
for track-specific evidence and promotion rules.

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
- `/Backtests` wishlist-driven backtest lab with strategy selection, editable strategy parameters, progress, result preview, winner, trades, and diagnostics.
- `/Paper` paper-mode run launch from wishlists plus optional Finviz, Alpaca credential validation, live metrics, recent decisions, audit links, and broker controls.
- `/Audit/{runName}` decision-tree audit for accepted, rejected, and no-signal evaluations.

Backtest and paper universes come from database wishlists. Generated run files are internal audit artifacts only and should not be used as hand-maintained ticker lists.

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

Secrets are split by environment and stored in Azure Key Vault for deployment. For
local development, use environment variables or .NET user-secrets; see
`docs/credential-security.md`. The ignored `appsettings.local.json` path remains a
temporary local migration fallback and must never be committed.

## Active Strategy Set

The promoted strategy catalog is under `configs/strategies`. Research-only strategies live under `configs/backtest/strategies` and must not be used by paper/live until promoted.

Current promoted/runtime strategies:

- `intraday-ema10-ema20-macd-volume.v1.yaml`
- `swing-reversal-reclaim-bull-quality-no-news.v1.yaml`
- `swing-overbought-rollover-short-no-news.v5.yaml`
- `minervini-trend-template-vcp.v4-trend-rider.yaml`

Current research-only intraday execution specs:

- `configs/backtest/strategies/intraday-vwap-momentum-pullback.bt-v1.yaml`
- `configs/backtest/strategies/intraday-atr-compression-breakout.bt-v1.yaml`
- `configs/backtest/strategies/intraday-macd-divergence-fade.bt-v1.yaml`

The current winner is reported in each result JSON under the top-level `Winner` property. Latest run evidence is tracked in [strategy-last-runs.md](docs/strategy-last-runs.md).

Current limitation: partial exits are not yet modeled. Backtests currently support one entry and one full-position exit. Strategy YAML may express structural stops, VWAP targets, failed-breakout guards, trailing stops, and max-hold exits, but partial scale-out requires a future trade-lot model.


## Safety

No live broker routing should be enabled until:

- data quality checks pass
- strategy backtests are reviewed
- paper trading behaves correctly
- position sizing and risk limits are verified
- broker writes are idempotent and audited

Research success is not inferred from a profitable run. A strategy remains
research-only until its preregistered development, validation, untouched holdout,
cost stress, concentration, execution-calibration, and paper-shadow gates pass.
