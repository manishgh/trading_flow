# TradingFlow Agent Context

This file is the compact handoff context for Codex/VS CLI sessions working in `C:\project\trading_flow`.

## Purpose

TradingFlow is a config-driven trading research and paper-trading system. The core intention is one shared decision brain across:

- backtesting
- paper trading
- future live trading

The binding product, market, provider, historical-data, later-listing, research,
execution, and promotion limits are in `docs/operating-boundaries.md`. No agent may
weaken those boundaries through code, config, or experiment design.

The source/sink can change by mode, but the strategy signal, indicator, confluence, risk, portfolio, audit, and execution decision path should remain the same.

Keep the trading desk in C# and the separate ML engine in Python. Shared data
contracts do not require rewriting either application in the other's language.
Provider collection ownership needs an explicit verified migration; Python may
read shared analytical datasets directly for feature engineering and modeling.

### Explain Issues In Concrete Words

Never describe a situation using vague terminology or unexplained technical labels.
Words such as "correctness", "readiness", "integrity", "validation" or "blocked"
must not substitute for an explanation. Use plain, specific language and explain any
technical term that is necessary, immediately where it appears.

When reporting a problem, limitation or reason to stop, explain what was observed,
what is missing or failing, which exact action it prevents, why that action would
produce an incorrect or misleading result, and what concrete step resolves it.
State what work can still continue. Distinguish missing credentials from missing
data, failed checks, resource limits and missing model approval. Give enough detail
for the user to understand the situation without having to decode jargon.

For example, do not say "correctness blocks publication". Say: "This membership
snapshot was captured after the prediction's decision time. Using it would give the
prediction information unavailable at that time. We need a snapshot observed before
that decision; collecting data for future decisions can continue."

When discussing missing market data or research evidence:

- Name the dataset, symbols, exact dates/times and timezone, timeframe/feed, expected
  records and received records. State the latest collection attempt, HTTP response
  and pagination result; say "not queried" or "no HTTP response" when applicable.
- Use the configured Alpaca/Finviz access to check and obtain available records
  before claiming they are unavailable. "Not in the audited local catalog" does
  not mean the provider lacks them. HTTP 200 alone does not prove complete coverage.
- Distinguish absent candles, no qualifying trades, pre-listing periods, provider
  omissions, failed requests and parsing errors. Do not invent candles or infer no
  trades, quotes or stop touches from an empty bar response.
- Separate downloadable history from original receipt/revision logs, historical
  versus current sector/list membership, classifier evaluation not yet performed,
  simulator defects, and an untouched test period not yet observed. These are
  different problems with different remedies; do not call all of them "missing
  historical evidence" or imply ordinary price history is absent when it exists.
- Preserve publication, update, receipt and classification times separately. A
  download made today cannot be labeled as received months ago. A conservative
  provider-update-time research assumption must be explicit and frozen.
- Report exactly what was recovered, what remains unproved, why it matters, and
  the next concrete action. Explain whether an issue affects optional features,
  the tested strategy, simulated execution, or authorization to trade. State the
  completed scope accurately instead of saying the whole project is "all done".

## User reporting and research convention (8 October 2026)

- Present performance monthly and separately by strategy identity/version and batch.
  Never display accumulated-period or pooled-strategy/batch returns. Retain original
  immutable audit totals internally for reconciliation; do not rewrite published artifacts.
- Use New York source-session dates and month-opening equity for monthly percentages.
  Label partial months and realised versus marked-to-market valuation explicitly.
- The user explicitly requested technical research beyond mean reversion, snapback
  and rubber-band names. Expanded families are authorised as diagnostic experiments;
  existing promotion, no-look-ahead, risk and data-isolation requirements still apply.

## Non-Negotiable Design Rules

- Do not sync GitHub unless explicitly asked.
- Preserve local secrets, `appsettings.local.json`, `tradingflow.db`, and paper-trading state. Core data deletion requires the separate dataset-specific confirmation described below.
- Preserve cached candle/history data. Challenge deletion requests and obtain fresh dataset-specific confirmation under Core Data Deletion Requires Specific Approval.
- Strategy behavior must be config-driven. Do not hard-code strategy-specific rules into base engine code except for generic indicator/signal primitives.
- UI code should manage web events, validation display, partial updates, and API calls only. Business orchestration belongs in services/runners/engine modules.
- Backtest, paper, and live should use the same signal calculator and evaluator path.
- Verify changes under the Risk-Based Verification policy, including its prose-only
  exception. Add focused tests for engine/risk/pipeline behavior changes.
- Use standard structured logging so output can later feed Logstash, Elastic, Azure Monitor, or another log sink.
- Avoid look-ahead bias: signal on completed bar, fill on a later executable bar, warm indicators before scoring, and model realistic slippage/costs.

## Core Data Deletion Requires Specific Approval

User instruction, 21 October 2026:

- Never delete, truncate, reset, destructively overwrite or automatically expire original market data, candles, trades/quotes, news, earnings, provider receipts, wishlists, saved Finviz lists or other core operator/research data without separate explicit approval for the exact datasets and operation. This covers scripts, tests, migrations, retention jobs and manual cleanup.
- Broad requests such as "clean everything", "delete old data" or "remove all run data" do not authorize deleting core data. Challenge a request that could remove it even when it comes from the user: identify the exact files/tables/datasets, explain the loss and affected results, propose a safe alternative, and obtain fresh dataset-specific confirmation. Never assume an accidental or ambiguous request permits irreversible loss or rely on an earlier broad approval.
- Automatic cleanup is limited to proven run-owned temporary/generated copies, intermediate files and isolated synthetic fixtures. Verify ownership and resolved paths first. Shared downloaded history, original provider archives, news stores, operational databases and source lists are never run-owned merely because a backtest or test read them.
- Keep each published backtest/research outcome, including unsuccessful outcomes, its monthly/trade records and immutable settings/strategy identity needed to explain it. For economic backtest/research runs, retain disposable inputs and intermediate copies after completion, failure or cancellation until an independent quant reviewer has audited the simulated trades/accounting or failed attempt and explicitly approved cleanup of that exact run. Record the approval with the result and owned-workspace identity before deletion. This approval is permission to clean intermediates, not permission to trade or promote a strategy. Preserve original/shared core data and published evidence.

## Delete Temporary Files After Every Session

User instruction, 31 October 2026. At the end of every session, delete the temporary files that session created and can prove it owns, unless they hold context someone still needs.

- Temporary: the session's own build and test scratch folders, pytest and mypy caches, one-off scripts and their output, logs of servers the session started, downloaded installers, debug dumps, and unpublished generated run configs it abandoned.
- Context: design notes, decisions, handoff notes, result summaries, published run results and their settings, evidence manifests, and the inputs and worker logs of economic runs. Those run files stay until the saved quant cleanup approval in `docs/backtesting-research-retention.md`, removed with `backtest-cleanup-reviewed-inputs`; an audit alone is not permission.
- Before deleting, commit related code, stop only processes this session started, and check each resolved path. If another session or process uses a file, keep it. If a file holds context among temporary material, move what matters into `docs/` or a commit first; `outputs/` is not in git. If unsure, keep it and list it for the user.
- Core Data Deletion Requires Specific Approval still applies. Market data, news, provider payloads and receipts, databases and source lists are never temporary, even when a one-off script downloaded them.
- Write session scratch under the ignored `.tmp/` folder or the agent's own scratch directory, not beside source code or in a new folder for each test run.
- End the handoff by stating what was deleted, the space freed, and what was kept and why. If the agent may not delete a file itself, give the user tested Windows commands.

## User Design Approval Required

- Before adding anything to the implementation, present the concrete design to the user and
  obtain explicit approval. This includes features, functions, components, execution paths,
  research drivers, configuration options, schemas, dependencies, UI changes and new experiments.
- The proposal must explain the requested behavior, the existing capabilities to reuse, the
  smallest reusable change, affected files/data, UI/API implications, verification and expected
  work/resource scope. Show examples of the configuration or user flow when relevant.
- Do not implement first and request retrospective approval. Do not interpret broad instructions
  such as "finish everything" or "continue" as approval for an unreviewed design or extra scope.
- Once a specific design is approved, implement within that approved scope. Bring additions,
  architectural changes, custom one-off tools and material deviations back to the user before
  proceeding; do not make those design decisions independently.
- Read-only investigation needed to prepare the proposal may continue. An explicit request to
  update these agent rules authorizes that documentation change only, not unrelated additions.
- Keep implementation and new execution attempts on hold while required design approval is
  pending. State precisely what is awaiting review; do not claim that planned work is completed.

## Mandatory Agent Workflow

These rules apply to every Codex/agent session in this repository.

1. Keep canonical strategy artifacts separate from experimental backtest variants. Directory location is organization, never lifecycle authority.
2. Do not mutate a canonical strategy directly for experiments. Copy it into a backtest/research strategy file, add new dimensions there, run and audit it, then either register the exact immutable artifact or discard it.
3. Promotion requires an authorized lifecycle decision bound to strategy ID, semantic version, and canonical content hash. A filename, folder move, positive run, or paper execution cannot promote a strategy.
4. Delete discarded unpublished backtest strategy changes and generated experimental configs. Preserve all published backtest results and their immutable parameters, including unsuccessful runs.
5. Preserve research memory in code and tests when technical primitives change. If a technical indicator, evaluator rule, or signal field is added/removed, add or update focused unit tests and keep a short audit note/result so future agents know why it exists.
6. Stop TradingFlow runtime processes before code changes. Kill only project processes such as `TradingFlow.Web`, `TradingFlow.WarmupService`, workers, or CLI runs that may lock build outputs or mutate runtime files. Restart and verify services after the change when the user expects the app to remain available.
7. Never assume silently. If behavior depends on unknown user intent, market regime, data source, or wishlist purpose, inspect local context first; if still unclear and risk is material, ask before changing behavior.
8. Stay context-aware. Example: a wishlist named `volatile` is not automatically a swing universe. Match strategy horizon, data timeframe, warm-up depth, and candidate universe intentionally.
9. Keep code testable, concise, and clean. Prefer small generic engine primitives over strategy-specific branches. Add focused unit/integration tests for every non-trivial engine, risk, runner, API, or persistence change.
10. Maintain deterministic sample candle fixtures for tests:
    - Swing setup: fixed 1d candle samples covering at least 6 months.
    - Swing execution confirmation: fixed 1h, 15m, and 5m samples aligned to the daily fixture.
    These fixtures are for unit/integration determinism; live/backtest research can use larger cached datasets.

## Agentic Programming Best Practices

- Require an explicit independent senior engineer/architect review for implementation checkpoints.
  Review module boundaries, function complexity, duplicated business rules, failure handling,
  resource use and algorithmic cost. A quant review does not replace this engineering review.
- Keep functions modular and focused on one responsibility. Orchestrators should describe the
  workflow through small named operations; split admission, order construction, execution,
  accounting and publication instead of nesting them in one large function. Prefer a simpler
  implementation and reuse shared engine rules before introducing another abstraction.
- Add a concise purpose comment to every new or changed function, including local helpers.
  Explain what it does and, where relevant, its inputs, clock assumptions, state changes and
  failure behavior. Comments must explain intent, not merely repeat the function name.
- Use Sol 6.1 High agents for implementation and focused reviews when available, with the parent reviewing changes; keep prompts and reads narrow to save tokens.
- Economic runs must use the reusable components and normal saved-plan/configuration workflow. Never add run-specific executable code, hand-built cohort runners or custom configuration files for a particular experiment. Freeze ordinary workflow selections and preserve the generated run evidence.
- Keep executable research drivers and collectors in repository source (normally tools/research).
  Output folders contain captured data, results, logs and immutable evidence, not source-code
  projects. Pass output locations explicitly; do not make code depend on living inside outputs.
- Treat reviewability as a design requirement. A reviewer must be able to explain the
  decision order, why each condition exists, and what state each step changes without
  reconstructing a large function. Avoid compressed one-line branches and mixed responsibilities.
  For example, AdmitDueCandidates should only dequeue due setups; separate eligibility/risk
  assessment, capital reservation and order construction into clearly named operations.
  Adding comments or renaming helpers is not enough if the decision flow remains complicated.
- Apply user feedback immediately to the current implementation and preserve the general lesson
  here. Verify the simpler design with the independent reviewer before claiming completion.
- Review complexity and performance before completion. For event streams, avoid repeated
  whole-history scans, unnecessary allocations and duplicate parsing. Verify optimizations
  against the shared implementation with focused parity tests; never change fill or risk
  semantics merely to make a backtest run faster.

- Every implementation checkpoint needs a plan/requirements review and an independent
  code/design review. Use focused, non-forked agent prompts; at most two reviewers
  active, and close each agent immediately after recording its findings.
- Run heavyweight verification one job at a time. Track owned process IDs and stop
  completed test/build/browser processes. At completion stop task-owned Web/ngrok
  verification services unless the user asks to keep them running; use graceful
  trading shutdown and verify protection where broker exposure exists.
- Closing an agent does not delete its disk transcript. Delete only explicitly
  approved transcript files; preserve main task history and trading/research data.
- Follow `docs/remaining-work-checkpoints.md` for the remaining phases 6-8 gates.

- Start from repository context. Read current code/config/data flow before editing; do not rely on stale memory.
- Plan experiments as reversible deltas. Prefer new config files, new test fixtures, or new strategy variants over modifying proven files in place.
- Keep one brain. Backtest, paper, and future live trading must share `SignalGenerator`, evaluator, risk, portfolio, and execution-decision logic.
- Separate concerns strictly: UI calls APIs/services; services orchestrate; engine computes; repositories persist; adapters talk to providers.
- Make changes observable. Add structured logs, rejection reasons, audit fields, or metrics when behavior would otherwise be invisible.
- Verify before promotion. Apply the Risk-Based Verification policy below; changed
  strategy/risk/evaluation behavior also needs its representative backtest or paper
  simulation. An unrelated transport or documentation edit does not trigger one.
- Prefer deleting obsolete code over compatibility layers when old code is not production intent. Preserve local secrets, DB state and cached candle data; core data deletion requires the separate confirmation described above.
- Record failed experiments. If a tested strategy underperforms, keep the result in the research ledger and do not promote it.
- Do not optimize on a single lucky run. Compare drawdown, trade count, win rate, expectancy, and whether the rule makes market sense.
- Keep timestamps explicit. Calculations use New York market time, persisted data uses UTC, and UI/audit should show UTC plus local/operator time where useful.

## Risk-Based Verification

Choose checks by changed behavior and affected consumers, not the total test count.
Record the tier, commands, results and deliberately unrun checks in the handoff.
An ordinary commit or component checkpoint is not a release checkpoint.

1. **Every code change:** run focused tests for changed functions/modules and their
   affected direct consumers. Build the affected C# projects with their compiler,
   nullable and analyzer checks; run relevant lint/types for any changed Python or
   frontend code. Small, localized and module-level edits do not need the full suite.
2. **Component checkpoint:** add affected integration, persistence/data-integrity,
   causality and contract tests. Shared wire/schema edits require affected C# and
   Python consumer/parity tests. Changes to signals, fills, costs, risk, admission or
   order handling require the relevant safety and deterministic replay tests even
   for a small diff. Keep operational/broker checks isolated from offline tests.
3. **Release checkpoint:** run the full suite and applicable expensive end-to-end,
   strategy/model replay and promotion checks against final code. Release means a
   deployable product release, strategy/model promotion or activation, or an explicit
   end-to-end readiness review. External checks without credentials/authorization
   remain unverified; never place real orders merely to satisfy a test tier.

Documentation-only edits need relevant document/contract checks and
`git diff --check`, not compilation, code lint/types or model/backtest runs when no
executable code, configuration or generated contracts changed. Executable examples,
machine-consumed contracts and governance-bound evidence still need affected checks.
Broaden coverage
only when dependency analysis or failures establish wider impact, and explain why.
Retest affected behavior after the final relevant edit. Never skip a known failure
or weaken the operating-boundary/promotion gates under the name of targeted testing.
This policy supersedes older blanket full-suite-per-checkpoint instructions; retain
historical results as records, not recurring mandates. Do not inventory or delete
tests solely to reduce their count without a separate request.

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

The historical/fallback pipeline is TPL Dataflow-based and bounded. Paper/live also
has one hosted Alpaca SIP websocket feeding a bounded, ordered in-memory pipeline per
ticker under a durable fenced stream-owner lease. `LiveRunner` consumes those warm
snapshots and falls back to the historical pipeline when required state is unavailable.

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
data/candles/_archive-preparing/{timestamp}-{intentId}.json
data/candles/_archive-pending/{scope}-{run}-{provider}-{source}-{yyyyMMddHHmm}.json
```

`source` is currently `provider`, `derived`, or fenced `stream`.
Preparing intents are written before candle I/O. A consumer-visible pending manifest
is committed only after every referenced candle file is flushed; archive consumers
must acquire the manifest's sibling `.lock` file before reading and acknowledging it.

## Active Providers

- Alpaca is primary for market data, historical data, news, and paper broker.
- Finviz is an operational discovery source and news-enrichment provider. Current
  membership is not historical point-in-time universe evidence, and discovery never
  grants trading admission by itself.
- CSV/local data remains useful for deterministic backtests.
- Yahoo, eToro, and TradingView webhook/signal paths are not active priorities.

## Active Configs

Paper configs:

- `configs/paper/alpaca-paper.yaml`

Strategy classification:

- `configs/strategy-catalog.json` is the authority.
- The catalog holds 23 research artifacts and 17 archived artifacts, with no paper
  execution or live authorization.
- Research/archive is artifact disposition. Paper experiment, paper shadow, and
  validated are exact-identity authorization grants.
- Web/mobile paper mode is explicit and never falls back to research.

Backtest profiles currently worth keeping:

- `configs/backtest/swing-backtest-profile.yaml`

Paper profiles may combine persisted wishlist and screener discovery sources:

```yaml
universe:
  sources:
    - type: wishlist      # operator-curated, persisted in tradingflow.db
      enabled: true
    - type: screener      # Finviz saved view or query string
      enabled: true
      scope: swing       # TradingFlow supports swing strategy admission only
  merge: union           # preserve source provenance; admission happens later
```

Paper universes come from database wishlists or the Finviz screener. Backtest universes
may also come from a frozen rule-based screen (see `docs/operating-boundaries.md`). None
comes from hand-maintained ticker-list configs. Generated run files remain audit artifacts
only.

## Candidate Discovery and Admission

The desk selects one or more discovery sources, not an already-approved trade.

1. Resolve wishlist and Finviz symbols while preserving source, observation time,
   query/snapshot identity, and expiry.
2. Persist and deduplicate the discovered set.
3. Warm the required market state from Alpaca SIP data.
4. Apply the selected strategy's deterministic admission profile to completed,
   point-in-time evidence.
5. Only a warm, qualified, armed, and triggered candidate can reach order planning.
6. Finviz RVOL is discovery metadata only. Strategy RVOL is computed from Alpaca
   candles using the versioned same-time baseline.
7. Market Predictor output, when displayed, is advisory evidence only and cannot
   qualify, veto, prioritize for execution, or authorize an order.

Desk view semantics built on discovery and deterministic admission:

- All � the full discovered universe with source and freshness.
- Warming � insufficient completed market evidence for the selected strategy.
- Signals � qualified and triggered on completed point-in-time evidence.
- In trade � open positions.
- Rejected/expired � exact admission, trigger, expiry, risk, or execution reason.
- Screener � raw Finviz discovery membership; never a trading admission.

## Data Policy

After an operator-data cleanup or host restart, verify market-feed collection and
execution separately. A pause of trading/jobs must not silently disable News or
Earnings. Before reporting live pages working, inspect fresh real provider records,
publication/analysis timestamps and the running browser views, as well as unchanged
source lists and the intended broker/run mutation guards. Fixture tests alone do
not prove operational feeds work. Use the explicit market-feed opt-in when trading
runtime is paused; UI tests must keep that opt-in disabled.

User instructions confirmed 6 October 2026: never put browser-test or backtest-run stocks/lists/screens into the operator dataset. Tests must own unique temporary storage, stop their owned host, and delete generated test databases, WAL, cache and run files after success or failure. Do not run mutation tests against an operator/review URL. Real backtests retain published results, manifests, trade/audit outputs and immutable parameters; their owned temporary input/cache/spool data remains retained after success, failure or cancellation until an independent quant reviewer audits the simulated trades/accounting or failed attempt and explicitly approves cleanup of that exact terminal workspace. Keep the approval bound to its workspace-manifest digest and published result hash when present; this permits intermediate cleanup only, not trading or strategy promotion. Do not delete read-only user CSV sources or another active run's inputs. Checked-in deterministic test fixtures and strategy configurations are source, not generated runtime datasets. Do not silently select a verification database for the user. The approved current reset keeps `long`, `volatile`, genuine Finviz presets and necessary sign-in/schema infrastructure; old run/job/audit/market/test records were cleared without recovery copies, while remaining published result files follow the latest retention instruction. Do not apply that reset again without a user request.

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
- abandoned unpublished generated artifacts under `configs/backtest/temp` or `configs/backtest/ui-runs` if they reappear
- abandoned unpublished artifacts under `configs/optimization/ui-runs`
- `data/runtime/logs`
- `data/web/logs`
- owned temporary input/cache/spool directories after their run stops and an independent quant reviewer explicitly approves cleanup of that exact terminal workspace; isolated synthetic test fixtures remain disposable after their tests stop

All published backtest results and their immutable parameters are excluded from
safe cleanup targets, including uncurated or unsuccessful runs. Discard only
unpublished abandoned artifacts and generated disposable test outputs.

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

- `dotnet test C:\project\trading_flow\src\TradingFlow.Tests\TradingFlow.Tests.csproj --no-build --no-restore`
- Result: 1,225 passed, 0 failed on 2026-08-28 after the Phase 2 durable discovery and market-state ownership pass plus the portable structured-logging smoke fix.
- Focused Phase 2 and adjacent regression slice: 102 passed, 0 failed.
- `TradingFlow.slnx` and the separate `net10.0-android` target build with 0 warnings and 0 errors.
- Fresh SQLite migrations through `AddDurableMarketState` and the EF model-drift check pass.
- The integrated research decision remains `RETAIN_RESEARCH`; no current research
  strategy is eligible for a new paper-shadow promotion.

## Current Product Focus

Near-term work is paper trading and backtesting quality:

- make swing strategies simple, explainable, and config-driven
- use daily setup bars with completed sub-daily confirmation only where the strategy requires it
- treat Finviz as operational discovery alongside persisted wishlists while
  preserving provenance and requiring normal strategy admission
- use Alpaca candle-derived participation measures in strategy decisions; retain
  Finviz RVOL as discovery metadata only
- keep StockIndicators for standard technical indicators
- use point-in-time news catalysts only where the swing strategy declares them
- improve audit pages so accepted/rejected decisions show exact matched values and reasons

The user wants the system to be architecturally clean more than merely patched to pass one strategy.
## Time Zone Convention

- Market calculations use New York exchange time (`America/New_York`) with DST. This applies to sessions, same-time volume baselines, VWAP session resets, and market-open/extended-hours labels.
- Persist timestamps in UTC where possible.
- Operator-facing audit/UI screens should display UTC, New York market time, and local Europe/Berlin time, plus a market-session label: `premarket_extended`, `regular_market`, `postmarket_extended`, `closed`, or `closed_weekend`.

