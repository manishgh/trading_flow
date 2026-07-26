# Research Evidence Operations

## Scope

This runbook covers the evidence-grade research path. It is separate from the mutable
paper/live candle cache and from legacy backtest result folders.

The operational sequence is:

```text
frozen collection request
  -> raw provider response archived before parsing
  -> deterministic normalization
  -> immutable Parquet partitions
  -> catalog dataset commit
  -> catalog-only momentum or catalyst study
  -> immutable research-run manifest
  -> separate human promotion decision
```

Research commands never receive provider clients or credentials. Network access exists
only in the collection command.

## Local Storage

Use a dedicated catalog and object root:

```text
data/research/evidence/catalog.db
data/research/evidence/objects/
```

Do not use `data/tradingflow.db`; it is the operational paper/live database.

Credentials are resolved from `ALPACA_KEY_ID` and `ALPACA_SECRET_KEY`, or the ignored
local development secret file already used by TradingFlow. Credentials must never be
placed in a frozen request, endpoint, parameter, artifact, log, or catalog attribute.

## Collect And Normalize

```powershell
dotnet run --project src/TradingFlow.Cli/TradingFlow.Cli.csproj -- `
  evidence-collect-normalize `
  --request configs/research/evidence/diagnostic-spy-5m-bars.json `
  --catalog data/research/evidence/catalog.db `
  --artifact-root data/research/evidence/objects `
  --workers 1 `
  --capacity 2 `
  --maximum-attempts 3 `
  --request-timeout-seconds 20
```

The request is immutable input. It contains:

- exact UTC half-open range `[start, end)`;
- relative, allowlisted Alpaca endpoint;
- symbols, SIP feed, adjustment, currency, timeframe, and historical `asof`;
- stable security identities;
- schema, normalizer, collection, partition, config, and code versions;
- output namespace and non-secret attributes.

Absolute provider URLs are rejected before authenticated HTTP composition. Alpaca uses
an inclusive `end` query, so the adapter translates the frozen exclusive boundary by
one .NET tick. Normalization still validates the original half-open range.

The first successful run returns a committed dataset ID. Replaying the exact request
returns the same dataset ID with `AlreadyCommitted=true` and performs no duplicate
dataset publication.

## Run Catalog-Only Studies

Momentum:

```powershell
dotnet run --project src/TradingFlow.Cli/TradingFlow.Cli.csproj -- `
  research-momentum `
  --request <frozen-momentum-request.json> `
  --catalog data/research/evidence/catalog.db `
  --artifact-root data/research/evidence/objects
```

Catalyst daily-news diagnostic:

```powershell
dotnet run --project src/TradingFlow.Cli/TradingFlow.Cli.csproj -- `
  research-daily-news-diagnostic `
  --request <frozen-catalyst-request.json> `
  --catalog data/research/evidence/catalog.db `
  --artifact-root data/research/evidence/objects
```

These commands open an existing catalog and immutable object store. They cannot call
Alpaca, Finviz, or another provider.

## Current Verified Evidence

On 2026-07-25, the bounded collection request committed:

- job: `evidence-2381001dae6574271deca0fd`;
- dataset: `5d3a65a5fab340c656119937a4b97161b93ff532472f9247a09035c11b23f007`;
- source: Alpaca SIP;
- content: six SPY 5-minute as-traded bars;
- partitions: one;
- replay: same dataset ID, `AlreadyCommitted=true`.

This proves the provider-to-raw-to-Parquet-to-catalog path. It is explicitly marked
`promotion_eligible=false` and uses a diagnostic identity rather than a committed
security-master dependency. It is not a strategy result.

The current evidence catalog also contains:

- adjusted six-year daily market bars:
  `1a82f7a174d9b5b9a72d02a3ed238e24bf63c1a9f938f2f101b1c0368836a95a`;
- raw six-year daily market bars:
  `4c64597b38b8ce844eb05626dfa24ee0148c0a97ba371bb7a3942a51d0cd7340`;
- provider-updated historical news:
  `ebd79d6c5e9f9a448a1332b206676b809f7e222d86355a5e0f3272d90b8452c9`;
- XNYS exchange sessions:
  `5e055c6fe25ba44bc7d75dfd2d724010d2893826d5632b365a27c2a767c94e2f`.

The original provider-created news attempt remains quarantined because using creation
time for a later revision can leak revised text backward.

## Human Catalyst Labels

The verified 500-item package is:

```text
data/research/labeling/ebd79d6c5e9f-6a6d9c653b14/
```

Its work-item SHA-256 is
`6a6d9c653b14bf3c5a89c3e5b52cb4ac39d86bc1f4ebba99529f42dbb2c26190`.
The package was generated twice with byte-identical output. It contains 500 work
items plus one manifest line, selected deterministically from 247,674 eligible
provider-updated stories.

## Why No Strategy Is Promoted Yet

A momentum diagnostic now exists, but the six-year universe is still a current
Finviz snapshot projected backward. That survivorship bias prevents promotion even
though the adjusted-bar calculation is encouraging.

A generic provider-updated daily-news study also exists. It found no stable positive
edge after costs. A category-specific catalyst study requires completed human ground
truth, historical SIP execution evidence, and point-in-time universe membership.

The system therefore retains calculations as diagnostics and records explicit
readiness failures instead of promoting a strategy.

## Recovery And Audit

- Failed attempts remain in the catalog and immutable store.
- Raw responses are archived before HTTP status classification or parsing.
- Cancellation, timeout, 429/`Retry-After`, exhausted retries, pagination failure,
  malformed content, range contamination, and tampering fail closed.
- A crash after raw publication can resume from catalog checkpoints.
- A normalized dataset becomes discoverable only after all partitions and its manifest
  are committed.
- Holdout consumption and strategy promotion use separate atomic registries.

Do not delete failed evidence attempts during normal cleanup. They are useful audit
records and may own immutable raw observations.
