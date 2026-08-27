# Strategy Lifecycle, Universe, And Execution Implementation Plan

**Status:** Phases 0, 1, and 2 complete. Phase 3 has not started.

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

### 3.1 Strategy identity, disposition, and authorization

Do not encode mutable execution permission into immutable strategy identity. The
model has three separate concerns:

- **Artifact identity:** `(strategy_id, semantic_version, content_sha256)` identifies
  one fully resolved immutable strategy document. Lifecycle and permission are not
  part of this identity.
- **Catalog disposition:** `research` or `archived`. Research artifacts are eligible
  for backtest and diagnostic replay; archived artifacts are provenance-only. An
  archive disposition is terminal for new execution authorization.
- **Execution authorization:** `paper_experiment`, `paper_shadow`, or `validated`
  is a grant over an exact artifact identity. A grant never rewrites, renames, or
  duplicates the underlying artifact.

`paper_experiment` and `paper_shadow` are intentionally different grants. A paper
experiment registration permits an explicitly labelled operator experiment but is
not promotion evidence. Paper shadow requires an accepted evidence-backed decision
and a frozen observation window. Validated authorizes future live selection only
after every live gate is met.

For compact UI/audit display, an artifact may expose a computed effective lifecycle
label using the order `archived > validated > paper_shadow > paper_experiment >
research`. Only active, non-revoked, non-superseded grants whose prerequisite grants
remain satisfied participate in this projection. Suspension is displayed separately
and never as a lifecycle label. This projection is not an authorization boundary.
Selection checks the underlying exact-identity disposition, grant, decision status,
prerequisite grants, and suspension state.

`content_sha256` is calculated from the canonical fully resolved strategy snapshot,
including parser defaults, admission profile, indicator definitions/versions,
risk/execution policy, and unknown-key rejection. Hashing source YAML bytes alone is
not sufficient.
The only exception is a terminal archived historical document whose legacy YAML is
deliberately outside the current executable parser contract. Its view-only identity
canonically binds stable family ID, semantic version, source SHA-256, and admission
profile. It can never receive an execution grant or become a run snapshot; every
research or executable identity still uses the fully resolved canonical document.
The existing `SqliteStrategyPromotionRegistry` is the source of promotion status.
Directory location is organization, not authority.

Before it becomes runtime authority, the promotion schema must be migrated and
tested against that full identity. A decision for an older content hash cannot
authorize a changed file. Acceptance, revocation, supersession, suspension, and
stale-hash behavior must be explicit and transactionally enforced.

Allowed authorization transitions and their effects are deterministic:

- Registering `paper_experiment` is allowed only for an exact non-archived research
  artifact. Unchanged content reuses the same identity; changed parameters require a
  new semantic version and therefore a new artifact identity. Publication is a
  crash-safe two-store saga: write the immutable, content-addressed artifact first,
  then commit its exact-identity `paper_experiment` grant in SQLite. Catalog
  visibility requires both, so a crash may leave an inert orphan artifact but can
  never leave an executable grant without its artifact. Retrying is idempotent and
  does not remove research/backtest eligibility.
- Accepting `paper_shadow` is allowed only for the exact identity of a registered
  paper experiment with complete promotion evidence. It adds paper-shadow
  authorization; it does not replace the artifact or delete experiment history.
- Accepting `validated` is allowed only for an exact identity with an active,
  unsuspended paper-shadow authorization and the required completed shadow evidence.
  It adds live authorization. The same identity remains selectable for paper shadow.
- `rejected` records a terminal decision outcome but grants no permission and does
  not alter an earlier unrelated grant.
- `revoked` targets one accepted grant for the same exact identity and removes only
  that grant. Revoking `paper_shadow` also makes any dependent validated grant
  ineligible for new entries until paper shadow is accepted again; historical
  decisions and run evidence remain readable.
- `superseded` targets one accepted grant and makes it inactive. The replacement is
  a separate accepted decision for the same stable family ID and the same
  authorization-grant type, using a different semantic version. Changed resolved
  content always requires that new semantic version; a hash-only replacement under
  the old version is forbidden. Refreshing evidence for the same exact identity uses
  an explicit revoke plus accepted decision rather than artifact supersession. The
  terminal decision and replacement acceptance commit atomically as one
  supersession operation.
- `suspended` is an exact-identity safety overlay. It blocks all new paper/live
  entries for that identity while monitoring, protection, exits, and diagnostic
  reads continue. `resumed` must target the active suspension and restores only the
  grants that remain otherwise active.
- Archiving blocks every new authorization and selection mode for the identity but
  never deletes its documents, decisions, or runs.

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

Phase 1 uses the following reviewed contract. Existing YAML `strategy_id` values
are import aliases; the stable family ID and semantic version below are the
authoritative identity inputs. A `(strategy_id, semantic_version)` pair has exactly
one canonical content hash. Changed resolved content requires a new semantic
version.

| Artifact | Bootstrap state | Stable family ID | Semantic version |
| --- | --- | --- | --- |
| `configs/strategies/brian_shannon_mta_avwap_strategies.yaml` | archived | `swing.avwap-bounce` | `1.0.0` |
| `configs/strategies/intraday-ema10-ema20-macd-volume.v1.yaml` | research | `intraday.ema-macd-volume` | `1.0.0` |
| `configs/strategies/kristjan_qullamaggie_stream_methodology.yaml` | archived | `intraday.episodic-pivot-gap` | `1.0.0` |
| `configs/strategies/lance_breitstein_intraday_tactics.yaml` | research | `intraday.vwap-trap-reclaim` | `1.0.0` |
| `configs/strategies/minervini-trend-template-vcp.v2.yaml` | archived | `swing.minervini-vcp` | `2.0.0` |
| `configs/strategies/minervini-trend-template-vcp.v4-trend-rider.yaml` | research | `swing.minervini-vcp` | `4.0.0` |
| `configs/strategies/swing-catalyst-drift.v5.yaml` | archived | `swing.catalyst-drift` | `5.0.0` |
| `configs/strategies/swing-mean-reversion-reclaim.v1.yaml` | archived | `swing.mean-reversion-reclaim` | `1.0.0` |
| `configs/strategies/swing-overbought-rollover-short-no-news.v5.yaml` | archived | `swing.overbought-rollover-short` | `5.0.0` |
| `configs/strategies/swing-reversal-reclaim-bull-quality-no-news.v1.yaml` | archived | `swing.reversal-reclaim` | `1.0.0` |
| `configs/backtest/strategies/intraday-atr-compression-breakout.bt-v1.yaml` | research | `intraday.atr-compression-or-breakout` | `1.0.0` |
| `configs/backtest/strategies/intraday-macd-divergence-fade.bt-v1.yaml` | archived | `intraday.macd-divergence-fade` | `1.0.0` |
| `configs/backtest/strategies/intraday-vwap-momentum-pullback.bt-v1.yaml` | research | `intraday.vwap-momentum-pullback` | `1.0.0` |
| `configs/backtest/strategies/minervini-trend-template-breakout-proxy.bt-v5.yaml` | archived | `swing.minervini-vcp` | `5.0.0` |
| `configs/backtest/strategies/minervini-trend-template-pivot-vcp.bt-v6.yaml` | archived | `swing.minervini-vcp` | `6.0.0` |
| `configs/backtest/strategies/minervini-trend-template-pivot-vcp.bt-v7.yaml` | archived | `swing.minervini-vcp` | `7.0.0` |
| `configs/backtest/strategies/minervini-trend-template-pivot-vcp.bt-v8-hourly-confirmation.yaml` | archived | `swing.minervini-vcp` | `8.0.0` |
| `configs/backtest/strategies/swing-connors-rsi2-deep-oversold.bt-v4.yaml` | archived | `swing.connors-rsi2` | `4.0.0` |
| `configs/backtest/strategies/swing-connors-rsi2-oversold.bt-v3.yaml` | research | `swing.connors-rsi2` | `3.0.0` |
| `configs/backtest/strategies/swing-mean-reversion-reclaim.tech-only.bt-v1.yaml` | archived | `swing.mean-reversion-reclaim-technical-only` | `1.0.0` |
| `configs/backtest/strategies/swing-mean-reversion-rsi2-recovery.bt-v2.yaml` | archived | `swing.connors-rsi2` | `2.0.0` |

The bootstrap therefore contains six research artifacts, fifteen archived
artifacts, and no paper-experiment, paper-shadow, or validated artifacts.
Minervini V4 remains the stale research control rather than a promoted strategy.
Connors RSI2 V3 is the faithful untuned benchmark; V4 is an archived tuned variant.

Additional lifecycle rules:

- Schema-v1 promotion-table rename, v3 schema creation, row copy, row-count and
  SQLite integrity verification, and schema-version advancement occur in one
  atomic SQLite transaction. A crash rolls the whole migration back; reopening
  retries from v1 without a partly migrated state. Legacy decisions remain readable
  evidence but never authorize execution because they lack the full exact identity.
- Suspension is an exact-identity safety overlay rather than an artifact disposition
  or execution grant.
  It blocks new paper/live entries while monitoring, protection, exits, and
  diagnostic reads continue. Suspension and resume are explicit decisions.
- Creating a paper experiment starts from an exact non-archived research identity.
  It publishes the immutable artifact before committing its exact-identity
  `paper_experiment` authorization; only the intersection is selectable. Unchanged
  content preserves and reuses the source
  identity; parameter edits require a new semantic version and record `derived_from`,
  override diff, actor, UTC timestamp, resolved content, admission profile, and
  resulting hash. A reused family/version with different content is a conflict.
  Disposable run YAML is never lifecycle or authorization authority.
- A run snapshot is separate from a catalog artifact. It records the selected
  artifact identity and execution provenance but cannot create or change disposition
  or authorization.
  A paper-experiment run must reference an already registered `paper_experiment`;
  a paper-shadow run must reference an exact identity authorized as `paper_shadow`.
- Selection is enforced in the domain/application service with explicit operations:
  `backtest` sees `research`; `create_paper_experiment` sees `research` as source
  material; `run_paper_experiment` sees active `paper_experiment` grants;
  `run_paper_shadow` sees exact active `paper_shadow` grants, including identities
  that also have `validated`; `run_live` sees exact active `validated` grants;
  archive is view-only. Diagnostic replay may address a specific non-archived
  identity. UI filtering is not an authorization boundary.
- Both paper execution catalogs are empty after bootstrap. Web/mobile separately
  show `no_paper_experiment_strategy` or `no_paper_shadow_strategy`, disable the
  corresponding start action, and direct starts return conflict. There is no
  fallback to a research filename.

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
- Stop mutating shared YAML. Editable paper parameters register a new immutable
  paper-experiment artifact before a run snapshot may reference it; backtest-local
  parameter variations remain immutable research run snapshots. Neither overwrites
  the source strategy.
- Apply the retained/archived dispositions in section 3.2.

**Accept:** catalog tests prove all 21 files are classified exactly once; bootstrap
counts are 6/15/0; mode filtering, immutable identity versus authorization
separation, deterministic authorization transitions, canonical identity uniqueness,
hash integrity,
immutable experiment registration and derivation, separation of catalog artifacts
from run snapshots, atomic migration rollback/retry, v1 preservation,
suspension/resume, revocation, stale-hash rejection, unknown-key rejection, both
expected empty paper states, and no unpromoted live selection all fail closed.

**Implemented Phase 1 boundary:** `configs/strategy-catalog.json` is the explicit
research/archive bootstrap; `data/strategy-artifacts/paper-experiments` stores
immutable experiment artifacts; and
`data/research/evidence/strategy-authorizations.db` is the single authorization
ledger for paper experiment, paper shadow, validated, suspension, and durable entry
admission tokens. Every strategy-routed broker entry presents exact identity and
explicit mode at the final order boundary. Run snapshots are immutable provenance,
never grants. The Web host can create paper-experiment grants but uses a deny-all
human promotion authorizer for paper-shadow/validated decisions. Experiment
registration uses a stable operator command ID and UTC request time, so retrying the
artifact-first/grant-second saga is idempotent while a distinct duplicate grant is
rejected. Revoking the experiment prerequisite makes dependent shadow and validated
grants ineligible for new entries without invalidating prior admission tokens.
Derived artifacts remain selectable after shadow/live authorization, and the Paper
UI exposes the first immutable experiment-registration command. Its collapsed
parameter editor loads the exact source values, requires a new semantic version for
edits, validates risk/execution ranges, and publishes the derived content and full
override diff without mutating source YAML.

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

**Implemented 2026-08-28:** Discovery snapshots, memberships, source evidence, TTL,
expiry, and aggregate versions are durable in SQLite. `LiveRunner` consumes an
incrementally refreshed discovery session and preserves symbols with confirmed broker
exposure. One hosted Alpaca SIP service owns the websocket under a fenced 30-second
lease renewed every 10 seconds. Renewal begins immediately after acquisition and is
revalidated after transport connect but before subscription activation. It fans
completed and revised bars into ordered,
bounded per-symbol TPL Dataflow pipelines, merges batched REST recovery without
overwriting newer stream bars, and persists accepted stream bars through
`ICandleStore` behind a durable cross-process writer fence. The receive loop observes
the complete expected subscription acknowledgement before REST recovery, and
degraded/gapped state is withheld until repair. Connection-owned bar/repair work drains before transport
disposal and lease release; failure to prove drain/disposal stops that host instance
fail-closed. Forced lease-loss takeover and non-cancelling work are covered by
deterministic transport tests. Replay uses the same canonical normalization and event
contract. Intra-session minute gaps trigger REST reconciliation. Alpaca-confirmed
no-trade intervals satisfy derived-bucket completeness without synthesizing bars;
unknown provider and unmanifested cache omission semantics remain fail-closed. REST
warm-up rejects bars that are incomplete at its request cutoff. Snapshots are unavailable
during warming and invalidate immediately on ownership loss. Historical conflicting
bars exclude the ticker deterministically. The exact stream fence is revalidated after
indicator computation, and pending short entries plus the last confirmed broker state
retain exposure when discovery or a broker poll drops out. Revisions are accepted only through
completed bar close plus two minutes; malformed recognized bar frames are
structurally logged, and malformed, duplicate, conflicting, out-of-order,
stale-owner, and backpressured events fail explicitly. See
[Phase 2 verification](verification/strategy-flow-phase-2.md).

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
