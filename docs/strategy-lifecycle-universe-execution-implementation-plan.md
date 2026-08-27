# Strategy Lifecycle, Universe, And Execution Implementation Plan

**Status:** Plan only. No runtime behavior is changed by this document.

**Prepared:** 2026-08-27

## 1. Purpose

This plan converts TradingFlow's retained trading research into one auditable flow
for backtest, paper experiment, paper shadow, and future live execution. It also
removes the current ambiguity between a screener hit, a curated watch item, a valid
strategy candidate, and an executable order.

The implementation must preserve the binding boundaries in this order:

1. `docs/operating-boundaries.md`
2. `docs/research/non-ml-strategy-research-program.md`
3. `docs/research/non-ml-strategy-research-implementation-status.md`
4. immutable research and promotion evidence
5. this implementation plan

The current integrated research decision remains `RETAIN_RESEARCH`. A profitable
historical run or a file under `configs/strategies` is not promotion evidence.

## 2. Verified Baseline

Two independent read-only agents reviewed research traceability and architecture.
The parent agent also inspected the cited code and ran the current test suite.

### 2.1 Test baseline

`dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore`

- Passed: 1,086
- Failed: 2
- Failure 1: the retained swing profile test expects 180 evaluation days, while
  `configs/backtest/swing-backtest-profile.yaml` currently requests 3.
- Failure 2: `AlpacaEndpointResolverTests` detects a hardcoded Alpaca data-stream
  host outside the authoritative profile resolver.

No feature implementation begins until both failures are understood and the suite
is green.

### 2.2 Confirmed defects and contradictions

1. `IntradayCatalystExecutionService` is registered as a hosted service and turns
   positive news into a fixed ten-share market order with zero entry protection.
   It bypasses strategy evaluation and must not run.
2. `SectorNewsWatcherService` runs hardcoded sample headlines in the web host.
   `PortfolioAdvisorService` is also hosted and applies Market Predictor directly
   to operational recommendations. `SwingDailyJob` and `IntradayPremarketJob` are
   registered prototypes even though they are not currently scheduled.
3. `ConfigCatalogService` hardcodes three active filenames and historical audit
   percentages. It bypasses the existing promotion registry.
4. `Paper.cshtml.cs` can edit shared strategy YAML. A run must snapshot an immutable
   strategy artifact instead of mutating shared configuration.
5. Finviz RVOL overrides candle-derived RVOL in paper/live. Missing Finviz RVOL can
   be replaced by the screener's required threshold, fabricating an observation.
6. Backtest can evaluate long and short decisions, while paper/live currently
   evaluates and submits long buys only.
7. Paper/live applies a separate run-level news veto that is not part of the same
   backtest decision path.
8. Candidate persistence exists, but the order service creates a candidate directly
   in `SETUP_VALID` state immediately before submission. Discovery, qualification,
   warming, arming, expiry, and consumption are not durable transitions.
9. The modern intent/event/position journals coexist with legacy order and decision
   stores. Some cancel, exit, and protection paths still bypass one command path.
10. `UniverseRankService` contains business ranking logic in the Web project and
    depends on Market Predictor, while the binding boundary defines TradingFlow as
    deterministic non-ML research and execution. Model evidence may be displayed as
    advisory information, but it cannot authorize, reject, rank for execution, or
    satisfy a strategy gate.
11. README, AGENTS, configs, UI labels, and research evidence disagree about which
    strategies are promoted and whether Finviz is enabled.

## 3. Decisions Frozen By This Plan

### 3.1 Strategy lifecycle

Use five explicit lifecycle states:

- `research`: executable only in backtest and diagnostic replay.
- `paper_experiment`: may use the paper broker, but is not promotion evidence and
  must be labelled as an experiment in UI and audit.
- `paper_shadow`: immutable strategy with an accepted evidence-backed promotion
  decision and a frozen observation window.
- `validated`: eligible for future live selection only after every live gate is met.
- `archived`: retained for provenance and comparison, never selectable.

`paper_experiment` and `paper_shadow` are intentionally different. Paper execution
alone does not imply validation.

The authoritative identity is `(strategy_id, semantic_version, content_sha256)`.
`content_sha256` is calculated from the canonical fully resolved strategy snapshot,
including parser defaults, admission profile, indicator definitions/versions,
risk/execution policy, and unknown-key rejection. Hashing source YAML bytes alone is
not sufficient.
The existing `SqliteStrategyPromotionRegistry` is the source of promotion status.
Directory location is organization, not authority.

Before it becomes runtime authority, the promotion schema must be migrated and
tested against that full identity. A decision for an older content hash cannot
promote a changed file. Acceptance, revocation, supersession, suspension, and stale
hash behavior must be explicit and transactionally enforced.

Bootstrap is fail-closed: every existing strategy is explicitly classified as
`research` or `archived`. No filename, hardcoded audit percentage, folder location,
or legacy decision row implicitly creates `paper_shadow` or `validated` status.

### 3.2 Retained research scope

The binding two-track research program remains unchanged:

- Swing: cross-sectional momentum baseline, then isolated stock-trend, VCP, and
  classified-catalyst additions.
- Intraday: point-in-time catalysts and abnormal participation, then opening
  response and pullback/reclaim evidence before one preregistered entry rule.

Config disposition:

- Minervini VCP: one canonical challenger under the swing track.
- Catalyst drift: an A4 classified-catalyst challenger, not generic positive news.
- Qullamaggie episodic pivot: retained as an idea mapped to the intraday catalyst
  track; the current YAML is archived until it faithfully models the documented
  event, opening-range trigger, and low-of-day risk.
- Breitstein-inspired VWAP reclaim: retained as a B4/B5 research hypothesis, not a
  verified named-trader implementation.
- Mean reversion, Shannon AVWAP, and overbought rollover short: retain their notes
  and evidence in archive; do not admit a third research family yet.
- Generic MACD divergence fade: archive.
- RSI2 and intermediate Minervini variants: retain one canonical benchmark and
  archive the superseded parameter variants.

### 3.3 Universe semantics

Screener and wishlist are discovery sources, not admissions:

```text
Finviz screen ----+
                  +-> discovery snapshot -> strategy-specific qualification
Wishlist ---------+                         -> warm -> arm -> trigger
News/operator ----+                         -> risk -> order command
```

- A wishlist records operator intent and priority; it never bypasses strategy gates.
- A Finviz result can become tradable without first being copied to a wishlist, but
  only after the same durable qualification and entry path.
- `both` means union at discovery with source provenance preserved. It does not mean
  immediate union into the execution loop.
- Each candidate is scoped to strategy and setup, not just ticker:
  `(symbol, strategy_hash, setup_key, discovery_window)`.
- Discovery prefilters and authoritative admission gates are distinct. Every
  strategy references an immutable, versioned `StrategyAdmissionProfile`; the same
  discovered symbol can qualify for one strategy and fail another.
- Current Finviz exports remain operational/diagnostic. They are not historical
  point-in-time universe evidence.
- Discovery refresh cadence, snapshot TTL, candidate TTL, and removal policy are
  typed configuration. A refreshed screen can add and expire unarmed candidates
  without restarting the run. Losing screener membership never closes an existing
  order or position; those remain governed by strategy and risk state.

### 3.4 Volume semantics

Keep three separately named values:

- `vendor_reported_rvol`: Finviz metadata used only for discovery and display.
- `cumulative_same_time_rvol`: current session cumulative volume divided by the
  historical cumulative volume through the same New York minute, excluding the
  current session. This is the primary intraday participation gate.
- `slot_bar_rvol`: current completed bar volume divided by prior same-slot bar
  volume. This describes local acceleration, not whole-session participation.

Never substitute a configured threshold for missing evidence. Insufficient history
returns `not_ready` with sample count and reason. Extended, regular, and overnight
cohorts are not mixed. No entry gate may use current cumulative volume divided by
average completed full-session volume; that legacy denominator is structurally low
early in a session and is neither same-time RVOL nor a valid observed Finviz value.

The RVOL contract freezes lookback sessions, minimum valid samples, New York session
boundaries, early closes, DST behavior, split adjustment, corrected/late bars,
partial-bar exclusion, and restart reconstruction. These values are typed profile
configuration and are recorded in every decision snapshot.

### 3.5 Shared decision contract

One pure decision kernel receives an immutable as-of snapshot:

- strategy artifact identity and parameters;
- completed candles and indicator snapshots;
- point-in-time catalyst/news evidence;
- session/calendar state;
- candidate discovery and qualification evidence;
- portfolio/risk snapshot;
- execution quote only when planning an executable order.

It returns:

- decision state and direction;
- every passed and failed rule with values and sources;
- trigger and invalidation prices;
- candidate expiry;
- an order plan or a precise no-order reason.

Backtest, paper, mobile validated automation, and future live adapters call this
same contract. Data ingestion and execution sinks differ; decision behavior does
not.

Every catalyst snapshot records `provider_published_at`, `provider_updated_at`,
`first_received_at`, and `decision_known_at`. The decision kernel may use only
evidence whose `decision_known_at` is not later than its as-of time. Revisions and
late arrivals never alter an earlier decision.

### 3.6 Execution boundaries

- All entries, replacements, cancels, partial exits, full closes, and protective
  orders go through one idempotent `IOrderCommandService`.
- Candidate state ends at `CONSUMED`. The transition from `TRIGGERED` to `CONSUMED`
  and creation of the linked order `INTENT` occur in one transaction;
  order/fill/position journals are authoritative afterward.
- Paper/live short execution remains unavailable until borrow eligibility, side,
  sizing, fees, stop protection, reconciliation, and UI semantics have dedicated
  tests. A short research config must not appear in paper/live selectors before then.
- Extended-hours observation is independent from execution permission. Extended
  execution remains opt-in and must use eligible broker order types. Because Alpaca
  brackets are not extended-hours eligible, protection behavior must be proven
  before extended-hours entries are allowed.
- Manual execution has two explicit policies: `strategy_validated` runs the shared
  decision kernel, while `operator_override` may bypass technical eligibility only
  when configured and explicitly confirmed. Both still pass market, account, risk,
  liquidity, ownership, and broker-safety gates. Overrides are never reported as
  strategy-qualified evidence.
- Order preview is persisted and short-lived. Its token binds strategy/run hashes,
  candidate version, quote timestamp, risk-snapshot version, expiry, and idempotency
  key. Preview does not reserve capital. Confirm revalidates the token and atomically
  creates the risk reservation with candidate consumption and order intent; it
  rejects stale market/candidate/risk state or an expired token.

## 4. Target Candidate And Order State

Candidate transitions are typed, append-only, versioned, and compare-and-swap
protected:

```text
DISCOVERED
  -> DATA_WARMING
  -> QUALIFIED | REJECTED | DATA_ERROR | EXPIRED
  -> ARMED | REJECTED | EXPIRED
  -> TRIGGERED | DISARMED | DATA_ERROR | EXPIRED
  -> CONSUMED | RISK_BLOCKED | EXPIRED
```

`TRIGGERED -> CONSUMED` atomically creates the linked order `INTENT`. Candidate and
order state cannot temporarily disagree about whether a setup has been consumed.
`DISARMED -> DATA_WARMING` is allowed only while the original discovery snapshot is
valid. `DATA_ERROR -> DATA_WARMING | EXPIRED` supports deterministic retry. Rejected,
expired, risk-blocked, and consumed candidates are terminal; a later opportunity
creates a new candidate with a new setup key. The exact transition graph is enforced
in one domain service rather than repeated in repositories or UIs.

Order and position state continues separately:

```text
INTENT -> SUBMITTING -> ACKNOWLEDGED -> PARTIALLY_FILLED -> FILLED
       -> REJECTED/CANCELLED/EXPIRED
FILLED -> PROTECTED -> EXIT_PENDING -> CLOSED
```

Every partial fill immediately creates or adjusts protection for the filled
quantity. `PARTIALLY_FILLED` may transition to `FILLED`,
`CANCELLED_REMAINDER`, or `EXIT_PENDING`; protection is never deferred until the
entire entry quantity fills.

No UI label may say `Accepted` before execution planning and all entry gates pass.
Use `Signal eligible`, `Order planned`, `Broker accepted`, and `Filled` as distinct
states.

Portfolio risk is reserved, not merely read. Candidate consumption, canonical
decision evidence, symbol ownership, risk reservation, and the write-ahead order
intent are committed in one local transaction/outbox operation. A rejection,
cancellation, expiry, or failed submission releases the reservation idempotently.

Alpaca nets holdings by account and symbol. Until a separate virtual allocation
ledger is designed and proven, TradingFlow permits one active owner
`(account, symbol)` across every strategy and run. Another strategy may observe the
symbol but cannot independently enter or close it.

## 5. API And UI Contract

Add transport-neutral DTOs in a small shared contracts project and application
services outside Web. Razor PageModels render navigation/authentication shells;
browser data and mutations use the same HTTP contracts as Android through AJAX/SSE.
Server endpoints call application services directly, so the web server never calls
itself over loopback. No UI layer may recalculate eligibility.

| Capability | Application contract | HTTP contract | Web consumer | Mobile consumer |
| --- | --- | --- | --- | --- |
| Strategy catalog | `IStrategyCatalog` | `GET /api/v1/strategies?mode=` | Backtest/Paper selectors and catalog status | Catalog, Backtest, Paper, Automation |
| Universe preview | `IUniverseDiscoveryService` | `POST /api/v1/universes/preview` | Trade Desk/Paper preview | Wishlist/Desk preview |
| Candidate run | `ICandidateWorkflow` | `POST /api/v1/candidate-runs` | Paper/Trade Desk | Paper/Automation |
| Candidate state | `ICandidateQueryService` | `GET /api/v1/candidate-runs/{id}` | Candidate table | Compact ticker cards |
| Live updates | same query contract | `GET /api/v1/candidate-runs/{id}/events` SSE | Partial row updates | Reconnectable stream/poll fallback |
| Decision audit | `IDecisionAuditQuery` | `GET /api/v1/candidates/{id}/audit` | Full evidence drawer/page | Collapsed details |
| Backtest control | `IBacktestApplicationService` | `/api/v1/backtests/*` | Lab/run progress/cancel | Backtest progress/cancel |
| Paper control | `IPaperApplicationService` | `/api/v1/paper/*` | Run/progress/cancel/positions | Paper run and positions |
| Orders | `IOrderCommandService` | `/api/v1/orders/preview`, `/confirm`, `/cancel`, `/close` | Ticket/orders | One-tap preview then confirm |

Strategy paths are internal. API requests use strategy ID and content hash. Run
responses include the immutable strategy hash, universe snapshot ID, decision-run
ID, and mode.

Backtest and paper jobs use bounded durable queues with persisted cancellation,
concurrency limits, leases, restart recovery, and per-ticker failure isolation.
In-memory `Task.Run` dictionaries are not an API execution backend.

UI requirements:

- Web Trade Desk shows source, lifecycle, warm/readiness state, live candle time,
  cumulative same-time RVOL and sample count, trigger, expiry, strategy, active
  order/position, and exact rejection.
- Mobile shows `Watching`, `Warming`, `Eligible`, `Order pending`, `In trade`, or
  `Rejected/Expired`; details remain collapsed until opened.
- Paper and backtest selectors display lifecycle and latest immutable evidence.
- Research strategies are visually distinct from paper-shadow strategies.
- Web and mobile consume the same DTO values and labels. No duplicate label mapping.

## 6. Implementation Phases And Checkpoints

Every phase follows this gate:

1. Primary implementation and focused tests.
2. Research/requirements reviewer checks the traceability matrix and evidence.
3. Independent code/design reviewer inspects correctness, concurrency, persistence,
   API boundaries, deletions, and test sufficiency.
4. Parent integrates findings and reruns affected plus full tests.
5. Web is started and verified through browser interactions; mobile is built and
   contract/view-model tested when its contract changes.
6. Documentation and status ledger are updated before the phase is complete.

### Phase 0 - Stop unsafe paths and restore a trustworthy baseline

- Remove the catalyst auto-order, mock sector watcher, and ML portfolio-advisor
  hosted services from runtime composition. Remove the unreachable hardcoded
  `SwingDailyJob` and ML-ranked `IntradayPremarketJob` prototypes after reference
  checks; registration without a call site is not retained as latent behavior.
- Route Alpaca news WebSocket URL through the endpoint resolver.
- resolve the swing profile 3-day versus 180-day mismatch from intended research
  scope; do not merely change the assertion.
- Enforce the binding universe contract: base paper/backtest profiles contain no
  static ticker lists. Database wishlists, Finviz operational discovery, or frozen
  research universe ledgers resolve symbols; immutable generated run snapshots may
  contain the resolved set for audit.
- Add composition tests proving no mock/prototype/autonomous order service starts.
- Characterize current indicators, timing, audit, and order behavior before refactor.
- Reconcile README, AGENTS, active configs, and binding research status.

**Accept:** full suite green; web starts; no positive-news-only order can be created.

### Phase 1 - Immutable strategy catalog and lifecycle

- Introduce strategy artifact metadata and content hashing.
- Canonicalize the fully resolved strategy, parser defaults, admission profile,
  indicator versions, and risk/execution policy; reject unknown YAML keys.
- Replace hardcoded active filenames and audit percentages with the promotion and
  evidence registries.
- Migrate the promotion registry so every decision binds strategy ID, semantic
  version, and content hash; reject stale-hash decisions and test revocation,
  supersession, suspension, uniqueness, and concurrent updates.
- Bootstrap every existing config explicitly to `research` or `archived`; the
  bootstrap contains no paper-shadow or validated strategy.
- Separate canonical research, paper experiment/shadow, validated, and archive
  views without trusting folder location as promotion evidence.
- Stop mutating shared YAML. Editable UI parameters create an immutable run snapshot
  with a new hash; they never overwrite the source strategy.
- Apply the retained/archived dispositions in section 3.2.

**Accept:** catalog tests prove mode filtering, hash integrity, immutability,
revocation, stale-hash rejection, unknown-key rejection, and no unpromoted live
selection.

### Phase 2 - Durable discovery, warming, and market-state ownership

- Add discovery snapshots with wishlist, Finviz, news, earnings, operator, and alert
  provenance.
- Implement discovery, deduplication, source TTL, expiry, warming, optimistic
  concurrency, and restart recovery. Strategy qualification/arming transitions land
  with the shared kernel in Phase 4.
- Move source union out of `LiveRunner` into the discovery workflow while preserving
  every source and observation timestamp.
- Keep present-day Finviz historical backtests explicitly diagnostic.
- Add configured source refresh/TTL behavior, incremental ADD/DROP handling, and
  requalification without restarting a paper run.
- Establish one normalized Alpaca market-state stream with bounded per-symbol
  pipelines, bar deduplication, out-of-order/late-bar policy, chronological derived
  timeframes, backpressure, restart snapshots, and a lease for the single websocket
  owner. Backtests consume the same event contract through a replay adapter.

**Accept:** concurrent duplicate discovery produces one setup; symbols can enter or
leave a refreshed screen without restarting the run; lease loss and takeover do not
duplicate bars; restart reconstruction produces the same completed market state.

### Phase 3 - Correct market evidence and RVOL

- Remove Finviz RVOL from strategy decisions and remove threshold substitution.
- Preserve vendor RVOL only as discovery metadata.
- Freeze definitions for cumulative same-time RVOL, slot-bar RVOL, sample count,
  valid sessions, partial bars, and extended-hours cohorts.
- Freeze lookback, sample floor, early-close, DST, split, correction, late-bar, and
  restart behavior in the versioned market-evidence profile.
- Rename or remove the misleading `finviz_style` engine value.
- Remove the secondary legacy full-session-volume denominator from every evaluator.
- Add deterministic 1m/5m fixtures across regular and extended sessions.

**Accept:** identical Alpaca candles produce identical decisions with or without
Finviz metadata; insufficient history fails with `rvol_baseline_not_ready`; source
search tests prove no strategy gate consumes the legacy full-session denominator.

### Phase 4 - One decision kernel

- Consolidate signal generation, direction, confluence, news policy, session policy,
  trigger, invalidation, candidate expiry, and order planning behind one contract.
- Add immutable `StrategyAdmissionProfile` evaluation and the typed
  `QUALIFIED -> ARMED -> TRIGGERED` candidate transitions. Persist every admission
  and trigger input, outcome, source, and timestamp.
- Make backtest and paper/live adapters supply snapshots to that contract.
- Remove the separate live news veto and immediate mobile signal synthesis; express
  any override as an explicit operator policy with separate audit status.
- Remove Market Predictor from qualification, execution ordering, vetoes, and order
  authorization. If retained in the UI, expose it as separately labelled advisory
  evidence whose availability or value cannot change any candidate transition.
- Keep completed-bar, next-executable-bar, pre-fill-stop, cost, and spread controls.
- Preserve research promotion gates and two-track scope.

**Accept:** golden fixtures yield the same canonical semantic decision hash and
reasons in
backtest and paper adapters for long, short-research, catalyst, no-catalyst, regular,
and extended observations. Composition tests prove Market Predictor success,
failure, score, and veto cannot change qualification, priority for execution, or
order authorization. A Finviz-only symbol cannot reach order planning until it is
persisted, warm, strategy-qualified, armed, and triggered, and a symbol may pass one
admission profile while failing another.

### Phase 5 - One order, fill, and position command path

- Migrate every broker write to `IOrderCommandService`.
- Add transactional candidate consumption, canonical decision evidence, symbol
  ownership, risk reservation, and order intent/outbox creation.
- Reserve buying power, portfolio heat, gross exposure, and position slots before
  submission; release them idempotently on terminal non-fill outcomes.
- Add a deterministic shared execution simulator that consumes the same order plan
  for backtests and models quote-side fills, partial/no-fill, spread, slippage, fees,
  impact, participation, partial exits, and remaining-lot protection.
- Consolidate legacy and modern order state after read/write consumers move.
- Test partial fills, duplicate retries, replacement races, reconnects, orphan
  adoption, protective coverage, cancellation, accepted-but-response-lost retry,
  and graceful shutdown.
- Protect each partial fill immediately and test filled remainder cancellation,
  protection resize, and exit while the entry remainder is still open.
- Add leases and takeover tests for candidate execution and broker reconciliation;
  one `(account, symbol)` owner is enforced across strategies and runs.
- Keep short and extended-hours entries fail-closed until their full invariants pass.
- Correct audit terms and attach strategy hash, run config hash, universe snapshot,
  provider timestamps, and feature provenance.
- Make mandatory journal writes fail closed. Define append-only required fields,
  indexes, compressed payload storage, summary/full retention tiers, and behavior
  under slow or unavailable audit persistence.

**Accept:** one idempotency key produces at most one broker intent; every filled
quantity is protected or the system enters a visible degraded/flattening state;
backtest execution fixtures reproduce partial fills, non-fills, costs, impact, and
scale-outs deterministically; an accepted order with a lost HTTP response is adopted
by deterministic client order ID without duplicate submission.

### Phase 6 - Common application APIs

- Move business orchestration out of Web services and PageModels.
- Add the shared contracts and `/api/v1` endpoints in section 5.
- Migrate existing `/api/mobile` consumers, then delete duplicate endpoints and DTOs;
  no compatibility layer is required.
- Add bounded SSE streams with reconnect cursor, heartbeat, cancellation, and polling
  fallback.
- Replace in-memory run dictionaries with bounded durable queues, persisted
  cancellation, run leases, concurrency limits, and restart recovery.
- Add endpoint authorization consistently with the existing local-auth boundary.
- Persist preview tokens and reject confirm requests when candidate version, quote,
  risk-snapshot version, strategy hash, or expiry changed after preview.
- Apply explicit authorization policies to every paper/order mutation, require an
  elevated operator policy for `operator_override`, validate browser anti-forgery
  protection, and test Android authentication/session behavior.

**Accept:** `WebApplicationFactory` tests cover success, rejection, validation,
authorization, anonymous/forbidden access, browser CSRF rejection, Android session
handling, cancellation, and stream reconnect. OpenAPI/DTO contract tests prove web
and mobile use the same fields.

### Phase 7 - Web and Android integration

- Rebuild Backtest, Paper, Trade Desk, and Strategy Catalog around application APIs.
- Preserve form state during partial updates; no full-page refresh for candidate,
  order, position, signal, or news updates.
- Add compact state filters and evidence drawers without duplicating business logic.
- Migrate Android catalog, wishlist desk, paper, automation, and backtest screens to
  `/api/v1` contracts.
- Deep-link notifications to candidate audit and attached point-in-time news.

**Accept:** Playwright verifies desktop/mobile web layouts and all links; Android
build and view-model tests pass; an API contract fixture renders the same candidate
state and rejection on web and mobile.

### Phase 8 - End-to-end evidence, cleanup, and documentation

- Run deterministic replay from discovery through decision, simulated fill, exit,
  audit, API, and UI.
- Run an Alpaca paper smoke test only after the prior phases pass.
- Verify cancellation, restart recovery, per-ticker failure isolation, and audit
  completeness.
- Delete superseded services, repositories, endpoints, DTOs, strategy variants,
  generated run configs, and stale documentation after reference checks.
- Update README, architecture, configuration, API, mobile, research status, and
  strategy last-run evidence.

**Accept:** full suite, solution build, web browser checks, Android build, API E2E,
and paper smoke pass; both independent reviewers report no unresolved critical or
high findings.

## 7. Verification Evidence

Each phase records commit SHA, exact commands, test counts, duration, focused test
names, browser/API observations, mobile build result, and retained evidence paths in
`docs/verification/strategy-flow-phase-{n}.md`. Large screenshots and logs remain
under ignored `data/verification/{commit_sha}/`; only the concise evidence record is
committed.

Minimum commands after the Phase 0 baseline is repaired:

```powershell
dotnet build TradingFlow.sln --no-restore --nologo
dotnet test src\TradingFlow.Tests\TradingFlow.Tests.csproj --no-build --nologo
dotnet build src\TradingFlow.Mobile\TradingFlow.Mobile.csproj -f net10.0-android --no-restore --nologo
dotnet run --project src\TradingFlow.Web\TradingFlow.Web.csproj --no-build --urls http://127.0.0.1:53014
```

Phase-specific focused tests run before the full suite. API tests use
`WebApplicationFactory`. Web verification covers authenticated Backtest, Paper,
Trade Desk, Strategy Catalog, candidate audit, cancellation, and broken-link checks
at 1440x900 and 412x915. Android verification includes contract/view-model tests,
Release build, and connected-device smoke installation when a device is available.

The web process is stopped before code edits and restarted only after build/tests
pass. A phase is not complete when a required test is skipped, flaky, or replaced by
manual observation.

## 8. Research-To-Implementation Traceability

| Research requirement | Implementation checkpoint |
| --- | --- |
| Point-in-time universe and no survivorship claims | Phase 2 provenance and diagnostic-only Finviz history |
| Completed-bar signal and later execution | Phase 4 parity fixtures |
| Point-in-time news availability and revision timing | Phase 4 immutable catalyst snapshot |
| Same-time abnormal participation | Phase 3 RVOL contract |
| Explicit costs, spread, partial/non-fill, and impact | Phase 5 order/fill model |
| Frozen configuration and one-use evidence | Phase 1 artifact hash and existing trial registry |
| Statistical, concentration, and cost-stress promotion gates | Preserved existing Research evaluators; catalog reads their decisions only |
| Human promotion approval | Existing promotion registry remains authoritative |
| One brain across modes | Phase 4 golden parity suite |
| Explainability and rejection evidence | Phases 2, 4, 5, 6, and 7 share one evidence contract |

Still external and not solved by refactoring:

- complete point-in-time US universe/delisting evidence;
- required human-labeled catalyst dataset;
- historical first-receipt/revision lineage for all news;
- calibrated paper evidence for fill probability, partial fills, and impact.

These remain promotion blockers and must never be converted into defaults or passes.

## 9. Reviewer Protocol

The implementation agent cannot approve its own phase.

- **Research reviewer:** confirms every changed rule maps to a frozen hypothesis,
  evidence source, or explicit diagnostic status. It rejects silent parameter or
  universe changes.
- **Code/design reviewer:** reviews the actual diff for ownership boundaries,
  concurrency, idempotency, persistence, failure isolation, API/UI contract use,
  deletions, and missing tests.
- **Integrator:** resolves both reviews, runs verification, and records evidence.

Any critical/high finding reopens the phase. A test that merely asserts the new
implementation is insufficient; each behavioral change needs a counterexample or
failure-path test.

## 10. Explicit Non-Goals

- No new broad strategy variants.
- No ML authorization, veto, or execution ranking in TradingFlow.
- No historical promotion from current Finviz exports or current wishlists.
- No live broker enablement.
- No compatibility layer for obsolete endpoints, generated configs, or strategy
  paths after all in-repo consumers migrate.
- No UI-side strategy, indicator, risk, or order logic.
