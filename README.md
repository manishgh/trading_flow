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
- **Research:** cross-sectional swing momentum and point-in-time swing catalyst
  research are the only active alpha tracks. ML belongs to the separate Market
  Predictor project. Intraday strategy research and execution are out of scope.
- **Timing:** completed-bar features, later-bar confirmation, next-bar execution,
  pre-fill stops, point-in-time news, and one-use chronological holdouts are
  mandatory.
- **Execution:** costs, quote side, spread, slippage, participation, partial/non-fill,
  and impact assumptions must be explicit. Extended-hours observation does not
  authorize extended-hours execution.
- **Durable broker commands:** entries, exits, protective orders, cancels, and stop
  replacements pass through one account-bound command service. Intent and risk are
  persisted before broker I/O; ambiguous submissions are adopted by exact client
  order ID instead of blindly retried. Extended-hours entries remain disabled because
  Alpaca bracket protection is unavailable outside regular hours.
- **Promotion:** missing universe, timing, execution, statistical, or paper evidence
  fails closed. The current integrated research decision is `RETAIN_RESEARCH`.

See [TradingFlow Operating Boundaries](docs/operating-boundaries.md) for the binding
contract, [Non-ML Strategy Research Program](docs/research/non-ml-strategy-research-program.md)
for track-specific evidence and promotion rules, and
[Research Implementation Status](docs/research/non-ml-strategy-research-implementation-status.md)
for the current decision, verified coverage, and unresolved evidence.
The complete documentation map is in [Documentation Index](docs/README.md).

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

### Shared Raw News Import

The desk can explicitly import Market Predictor's committed raw news publications
without another provider request. This is a separate Windows local-disk inbox,
not a replacement for the current news feed or normalized research catalog.

```powershell
dotnet run --project src/TradingFlow.Cli -- import-shared-news --trusted-collector-root C:\project\market-predictor\data\raw\shared_news --plan-sha256 <independently-recorded-plan-sha256> --inbox C:\project\trading_flow\data\shared_news_inbox
```

The operator must independently trust the source location and its writers; hashes
do not authenticate them. Retain the plan pin separately, not by discovering a
manifest and trusting its own computed hash. Restart preserves original receipt
times and verifies durable acknowledgements rather than duplicating imports.
`--help` lists whole-publication limits. Exit 0 means terminal pagination and raw
import success; exit 2 means rejection or incomplete collection (inspect the
structured report); cancellation returns 130. A failed/cancelled operation may
retain already committed bundles. No result grants coverage or trading admission.
See [Evidence Repository Design](docs/research/evidence-repository-design.md) for
trust, storage, limits and ownership boundaries.

## Earnings Calendar

The `/Earnings` web page and Android `Earnings` tab show every Finviz earnings event available
for today and tomorrow. Finviz Elite provides schedules and reported EPS/revenue results;
Alpaca SIP completed 5-minute bars and the shared news feed provide the post-release reaction.
The API returns UTC, New York, and Amsterdam timestamps and explains every positive, mixed,
negative, awaiting, insufficient-data, or possible-breakout assessment.

The hosted earnings monitor continues while the UI is closed, re-evaluates every calendar ticker
once per minute by default, and exposes its active cadence and last-analysis timestamp through the API.

The monitor is advisory and cannot route orders. It archives raw provider JSON before parsing,
records when results were first observable, excludes incomplete bars, and advances a persisted
rolling candle cache incrementally. See [Earnings Calendar And Reaction Monitor](docs/earnings-calendar.md).

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

Create the first local administrator from an interactive terminal before using
the protected operator screens:

```powershell
dotnet run --project src\TradingFlow.Web -- users add --username YOUR_NAME --admin
```

The command prompts for the password without echoing it or placing it in command
history. ASP.NET Core Identity persists only its one-way password hash. An
administrator can create additional operator or administrator accounts from
`/Users`. Earnings calendar pages and read APIs are public; trading, research,
wishlist, health, and other operational surfaces require authentication. See
[Local Authentication](docs/local-authentication.md).

Pages:

- `/` dashboard and recent jobs.
- `/Backtests` wishlist-driven backtest lab with strategy selection, editable strategy parameters, progress, result preview, winner, trades, and diagnostics.
- `/Paper` paper-mode run launch from wishlists plus optional Finviz, Alpaca credential validation, live metrics, recent decisions, audit links, and broker controls.
- `/Earnings` complete today/tomorrow earnings schedule, reported results, provider/news timestamps, and completed-bar breakout evidence.
- `/Audit/{runName}` decision-tree audit for accepted, rejected, and no-signal evaluations.

Backtest and paper universes are resolved from database wishlists and, where enabled,
operational Finviz discovery. Discovery does not grant trading admission. Generated
run files freeze the resolved ticker snapshot for execution and audit; they are not
hand-maintained ticker lists. Base profiles are unresolved templates and fail closed
when executed directly.

Strategy files are classified by `configs/strategy-catalog.json`; folder placement
never authorizes execution. Paper experiment, paper shadow, and validated grants are
persisted in `data/research/evidence/strategy-authorizations.db` against exact
strategy ID, semantic version, and canonical content hash. Every strategy-routed
entry obtains a durable admission token immediately before order intent persistence
and broker submission. Revocation or suspension blocks new intents while existing
protection and exits remain available.

### Runtime Discovery And Market State

Paper/live ticker membership is a durable, versioned discovery projection rather
than a static union inside the runner. Wishlist, Finviz, news, earnings, operator,
and alert observations retain their source timestamps and expire independently.
ADD/DROP changes apply without restarting a run, while confirmed broker exposure
keeps a symbol subscribed until protection and exit work is complete.

One hosted Alpaca SIP stream owns the provider websocket under a fenced SQLite
lease. It publishes completed 1-minute bars and explicit provider revisions to one
bounded, ordered TPL Dataflow pipeline per symbol. Batched REST recovery, live bars,
and historical replay converge on the same canonical market-bar event and
`ICandleStore` boundary. Derived timeframes never mix regular and extended sessions;
Alpaca-confirmed no-trade minutes can complete a bucket without creating synthetic
bars, while unknown or unmanifested-cache omission semantics fail closed. REST
warm-up ignores bars that are not completed at its cutoff. Strategy snapshots are disabled
during initial warming and immediately after stream ownership loss. Historical
conflicting bars exclude the affected ticker deterministically. Snapshot publication
revalidates the exact stream fence after indicator computation, and a failed broker
poll retains the last confirmed long/short exposure rather than treating it as flat.
paper/live falls back to the existing REST candle pipeline until the stream has all
required warm-up bars. Lease ownership is renewed during connect and revalidated
before subscriptions activate; accepted bar/repair work drains and the transport is
disposed before release. Runtime defaults are discovery refresh 60 seconds, source
expiry 180 seconds, stream lease 30 seconds renewed every 10 seconds, and revised-bar
acceptance through bar close plus two minutes. Stream snapshots also fail closed when
the latest completed 1-minute bar is more than three minutes stale during the active
04:00-20:00 ET session. Detailed evidence is in
[Phase 2 verification](docs/verification/strategy-flow-phase-2.md).

Strategy RVOL now comes only from the versioned Alpaca SIP market-evidence profile.
The primary gate is cumulative volume through the same New York exchange clock time
divided by the median of the exact 20 prior exchange sessions; exact-slot RVOL is retained only as local
acceleration evidence. Premarket, regular, postmarket, and overnight cohorts are
isolated. A baseline with fewer than 20 valid SIP/`adjustment=all` sessions fails as
`rvol_baseline_not_ready`. Finviz RVOL is persisted only as discovery metadata and
cannot alter a strategy decision. See
[Phase 3 verification](docs/verification/strategy-flow-phase-3.md).

Strategy admission and triggering now run through one immutable decision contract in
backtest, paper, and mobile automation. A candidate is scoped to its owning run,
journaled before evaluation, and advances only through the typed
`Discovered -> DataWarming -> Qualified -> Armed -> Triggered` graph. Paper/live use
the durable SQLite journal; each parallel backtest worker uses isolated in-memory
state plus a write-through NDJSON recovery journal. Completed backtests publish the
full candidate and transition audit as an atomic `*.candidate-decisions.json`
sidecar, then remove the recovery spools. Interrupted workers leave their flushed
spools under the result root for diagnosis. The kernel freezes direction, trigger,
pre-fill stop, target policy, slippage, and expiry in one canonical pre-risk order
plan. The order gate requires both the current `Triggered` row and its matching
transition sequence and semantic hash.

News is not a separate live veto. Provider publication/update, observed receipt, and
sentiment-completion timestamps become a point-in-time catalyst snapshot consumed by
the same kernel. Catalyst evidence without observed receipt, classification version,
and decision-availability chronology is excluded rather than inferred from provider
publication time. Wishlist observations and Market Predictor output are explicitly
advisory and cannot change candidate state or execution priority. See
[Phase 4 verification](docs/verification/strategy-flow-phase-4.md).

Backtest cache wrappers preserve Alpaca's calendar, completeness, and provenance
contracts. Authoritative calendars are checksummed and reusable offline; adjusted
candle slices carry a versioned 24-hour freshness boundary and explicit restatement
revision so split-restated history is not silently reused forever. Versioned immutable
data files are published before an atomic manifest switch under a cancellable,
30-second-bounded cross-process lock, preserving the last committed slice through a
writer interruption.

Observed wishlists reuse the single hosted Alpaca stream and its warmed market state.
They do not poll 45 days of REST data every minute. Ticker failures are isolated, and
alerts are idempotent for unchanged complete decision evidence while accepted provider
revisions remain eligible for reevaluation.

The Web host clears platform-specific default logging providers and writes structured
JSON to standard output. Local runs and containers therefore use the same portable
logging path, and lack of Windows Event Log privileges cannot terminate background
market/news services.

## Rolling Candle Warmup

Generate or refresh a rolling normalized candle and indicator cache:

```powershell
dotnet run --project src\TradingFlow.Cli -- warm-candles REPLACE_WITH_GENERATED_RUN_CONFIG
```

The supplied run config must already contain the wishlist-resolved ticker set;
the base backtest profiles intentionally contain no static tickers.

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

## Strategy Research Set

Strategy files under `configs/strategies` are canonical research artifacts; their
folder is not promotion evidence. Experimental variants live under
`configs/backtest/strategies`. The binding integrated decision is currently
`RETAIN_RESEARCH`: no strategy is validated for live execution or admitted to a new
paper-shadow run.

The exact bootstrap classification is in
`configs/strategy-catalog.json`: two research artifacts, thirteen archived
artifacts, and no paper-experiment, paper-shadow, or validated authorization.
Folder location is not authority.

Strategy content identity is `(strategy_id, semantic_version, content_sha256)`.
Research/archive is catalog disposition; paper experiment, paper shadow, and
validated are separate exact-identity grants. Generated run snapshots record the
selected operation but cannot promote a strategy. Paper experiment and paper shadow
are separate choices in Web and Android and both fail closed when their catalog is
empty.

Each completed comparison may report a run-local winner in its result JSON. That
label is not a lifecycle promotion. Immutable run evidence and promotion decisions
are tracked through the research evidence catalog.

Current limitation: partial exits are not yet modeled. Backtests currently support one entry and one full-position exit. Strategy YAML may express structural stops, VWAP targets, failed-breakout guards, trailing stops, and max-hold exits, but partial scale-out requires a future trade-lot model.


## Safety

No live broker routing should be enabled until:

- data quality checks pass
- strategy backtests are reviewed
- paper trading behaves correctly
- position sizing and risk limits are verified
- broker writes are idempotent and audited
- partial-fill protection has passed the dedicated broker drill
- replacement outcomes and graceful shutdown are durably evidenced

Research success is not inferred from a profitable run. A strategy remains
research-only until its preregistered development, validation, untouched holdout,
cost stress, concentration, execution-calibration, and paper-shadow gates pass.
