# Warmup Service

`TradingFlow.WarmupService` is a standalone ASP.NET Core service for pre-loading tomorrow's candidate stocks before paper/live trading needs them.

It is intentionally separate from `TradingFlow.Web`:

- Web UI remains UI/API orchestration only.
- Warmup service owns candidate watchlist state and scheduled cache preparation.
- Trading/backtest engines consume the same warmed candle/news/indicator data contract.

## What It Does

When you believe a ticker may trend tomorrow, call:

```powershell
Invoke-RestMethod `
  -Method Post `
  -Uri "http://localhost:53120/api/warmup/watchlist" `
  -ContentType "application/json" `
  -Body '{
    "tickers": ["POET", "RGTI"],
    "reason": "possible trend tomorrow",
    "requestedBy": "manish",
    "warmupDays": 60,
    "newsLookbackDays": 14,
    "timeframes": ["1m", "5m", "15m", "1h", "1d"],
    "includeNews": true,
    "runNow": true
  }'
```

The service then:

- Persists the watchlist intent.
- Fetches Alpaca candles for all configured timeframes.
- Upserts candles into `ICandleStore` under `scope=warmup`.
- Calculates indicators with the shared engine `IndicatorEngine`.
- Fetches Alpaca news catalysts when `includeNews=true`.
- Writes warm artifacts locally.
- Uploads artifacts to Azure Blob when `Warmup__BlobContainerSasUrl` is configured.

## API

- `GET /health`
- `GET /api/warmup/watchlist`
- `POST /api/warmup/watchlist`
- `DELETE /api/warmup/watchlist/{ticker}`
- `POST /api/warmup/run-now`
- `GET /api/warmup/runs`
- `GET /api/warmup/runs/{runId}`

## Schedule

Default schedule:

- `Warmup__NightlyRunLocalTime=20:30`
- `Warmup__MarketTimeZone=America/New_York`

That runs after the US regular session and prepares the next day's paper/live cache. The value is config-only.

## Storage Layout

Default durable root:

- `data/warmup/state/watchlist.json`
- `data/warmup/state/runs.json`
- `data/warmup/archive/*.json`

Default hot cache root:

- `data/cache/warmup/candle-store/warmup/{ticker}/alpaca/provider/{timeframe}/{ticker}/{yyyy-mm-dd}.jsonl`
- `data/cache/warmup/artifacts/{ticker}/{timeframe}/bars.jsonl`
- `data/cache/warmup/artifacts/{ticker}/{timeframe}/indicators.jsonl`
- `data/cache/warmup/artifacts/{ticker}/news/catalysts.jsonl`
- `data/cache/warmup/artifacts/{ticker}/warmup-manifest.json`

All local writes use temp-file plus replace/move semantics.

In AKS:

- `/app/data` is Azure Files and durable.
- `/app/cache` is pod-local `emptyDir` and disposable.
- Blob is the optional durable archive layer for TradingFlow warmup artifacts.

## Azure Blob

Production uses the Key Vault secret:

- `tradingflow-warmup-container-sas-url`

It is mounted as:

- `Warmup__BlobContainerSasUrl`

The service uploads each artifact as a block blob under:

```text
{Warmup__BlobPrefix}/yyyy/MM/dd/{ticker}/{runId}/...
```

For managed identity Blob upload later, replace `SasAzureBlobWarmupArchiveSink` behind `IWarmupArchiveSink`; the rest of the service does not change.
