# Evidence Repository Design

Status: binding design for research-data implementation

Precedence: this design implements and is subordinate to
`docs/spec/automated_trading_production_spec_v1.md` and
`docs/spec/automated_us_equities_trading_research_design.md`. Requirement IDs remain
traceability metadata; they are not class, method, variable, or business-rule names.

Current-state warning: the immutable local evidence slice is implemented for
raw-before-parse Alpaca bars, news, and SIP quotes; deterministic Parquet publication;
SQLite catalog lifecycle; identity, universe, exchange-session, sentiment, labeling,
holdout, promotion, and catalog-only research workflows. The complete acceptance
matrix still includes unimplemented provider surfaces and production Azure adapters.
Legacy arbitrary-file research paths and mutable caches remain diagnostic-only.

## 1. Purpose

TradingFlow needs a reproducible research repository before it can decide whether a
strategy has an edge. The repository must answer five questions for every observation:

1. What bytes did the provider return?
2. When and under which request could TradingFlow have observed them?
3. Which deterministic normalizer produced the analytical record?
4. Is the requested dataset complete and internally consistent?
5. Which immutable dataset, code, config, and test partition produced a research result?

The repository is not the live candle recovery cache and is not the UI/job artifact
store. Research may read only catalog-committed evidence datasets. It may not call a
provider, discover arbitrary files, or infer provenance from a directory name.

## 2. Existing Components And Their Roles

The following boundaries remain, with narrower ownership:

| Boundary | Role | Research evidence |
|---|---|---|
| `IRawArchiveWriter` | Raw-before-parse provider observations | Foundation to evolve; current random IDs and two-file publication are not sufficient by themselves |
| `ICandleStore` | Mutable, rebuildable paper/live recovery cache | Never admissible directly |
| `IArtifactWriter` | Replaceable reports, job output, and UI state | Never an evidence source |

Two new boundaries are required:

```text
IImmutableArtifactStore
  PutIfAbsentAsync(request, content)
  OpenReadAsync(contentAddress)
  VerifyAsync(contentAddress)

IEvidenceCatalog
  CommitDatasetAsync(manifest)
  GetDatasetAsync(datasetId)
  FindDatasetsAsync(query)
  RegisterResearchRunAsync(manifest)
  PinAsync(pinSubject, reason)

IPromotionRegistry
  RegisterDecisionAsync(authorizedDecision)
  GetActiveDecisionsAsync()

IHoldoutRegistry
  ConsumeAsync(holdoutIdentity, researchRunId)
  GetConsumptionAsync(holdoutIdentity)
```

The immutable store owns bytes. The catalog owns discoverability and lifecycle. A
file that exists in the immutable store but has no committed catalog entry is an
orphan, not a dataset.

Research code receives evidence-query and research-run capabilities only. It is never
composed with `IPromotionRegistry`. Promotion registration is an authorized
operator/application boundary with a separately configured principal.

## 3. Storage Products

### 3.1 Raw provider observations

Every inbound provider message or response is archived before status interpretation or
parsing. This includes paginated REST responses, Finviz CSV, WebSocket news and market
messages subject to the configured raw market-data sampling policy, and broker
responses.

Raw content is addressed by SHA-256. A separate immutable observation records:

- deterministic collection job ID and logical plan hash;
- provider and endpoint;
- stable ingestion request ID and page/cursor identity;
- request window and symbols;
- feed, adjustment, currency, and `asof` parameters;
- HTTP status and non-secret headers;
- provider record ID, provider timestamps, and local receipt timestamp;
- content address and byte length;
- run, config, and code version.

Every normalized row also carries `schema_version`, `config_hash`, `code_version`,
`run_id`, `data_feed`, source/exchange timestamp, and local receive timestamp. Monetary
and size values use decimal/fixed-point encodings that preserve provider precision;
binary floating-point money is prohibited.

Every normalized row carries the immutable source observation ID and source content
hash that produced it. A record assembled from multiple observations carries a sorted
immutable list of source observation IDs and hashes. Partition lineage is a
completeness index; it does not replace row-level lineage.

Retries preserve separate observations when the receipt evidence differs, but reuse
identical content bytes.

### 3.2 Normalized evidence datasets

Normalized records are immutable Parquet partitions. Partition filenames are content
addressed and never overwritten. The first implementation uses Parquet.Net behind a
TradingFlow.Data adapter; research code depends only on dataset readers.

Initial dataset kinds:

- `market_bars_as_traded`
- `market_bars_research_adjusted`
- `sip_quotes`
- `sip_trades`
- `news_articles`
- `news_revisions`
- `news_story_symbols`
- `sentiment_assessments`
- `catalyst_results`
- `classifier_ground_truth`
- `classifier_adjudications`
- `security_master`
- `symbol_intervals`
- `corporate_actions`
- `universe_snapshots`
- `universe_membership`
- `benchmarks`
- `research_decision_journal`

Each partition manifest includes:

- dataset kind and schema version;
- provider, endpoint, feed, adjustment, currency, and `asof`;
- security IDs, symbols, timeframe, and requested range;
- observed minimum and maximum timestamp;
- row count, byte length, and SHA-256;
- sorted raw observation IDs and hashes;
- normalizer version and code version;
- data-quality result.

All partitions are written before the immutable dataset manifest. The dataset manifest
is written before the catalog transaction. Readers discover only the catalog entry.
The logical collection-plan hash excludes wall-clock creation time and run ID, and the
dataset ID is derived from that plan plus the canonical partition generation. Identical
collection attempts therefore converge on one logical dataset across processes.

### 3.3 Mutable operational caches

`ICandleStore` remains local and mutable. It may be reconstructed from a committed
dataset or provider backfill. It cannot satisfy a research input requirement.

The existing CSV cache is diagnostic-only until replaced. A narrow cached window must
never satisfy a wider request. Cache entries must declare coverage, feed, adjustment,
schema, and source dataset ID or be rejected.

### 3.4 Research runs

A research run manifest records:

- immutable input dataset IDs and manifest hashes;
- point-in-time universe ledger ID;
- strategy/study config content and hash;
- code version;
- development, validation, and untouched holdout boundaries;
- cost, spread, slippage, borrow, and benchmark assumptions;
- deterministic output content addresses;
- readiness and promotion-eligibility findings.

Changing a report filename or runtime allowlist cannot promote a strategy.
Only the separately authorized human `PromotionDecision` is an authoritative promotion
or rejection.

Every holdout has an immutable identity derived from its dataset IDs, universe ledger,
study family, temporal boundaries, and partition definition. Evaluating a holdout is
an atomic catalog operation that permanently records its first consuming research run.
The same holdout identity cannot be consumed by another parameter-selection or
promotion attempt.

## 4. Market Data Semantics

TradingFlow stores two different bar products because they answer different questions:

### 4.1 `market_bars_as_traded`

- Alpaca `adjustment=raw`;
- explicit SIP feed and USD currency;
- explicit historical `asof` symbol mapping;
- used for historical price and dollar-volume universe filters;
- used for simulated entry/exit prices;
- linked to security and symbol-effective history.

### 4.2 `market_bars_research_adjusted`

- Alpaca `adjustment=all`;
- explicit SIP feed and USD currency;
- explicit `asof`;
- used for momentum, total returns, and continuous indicators;
- reconciled against the corporate-action ledger.

Absolute price and average-dollar-volume eligibility must never be calculated from a
future-adjusted series. A split, spin-off, merger, or unresolved symbol lineage makes
the affected interval inadmissible until reconciled.

Daily bars become available only after the relevant New York trading session is
complete. Comparing the provider's daily bar start timestamp to an intraday cutoff is
prohibited. Session membership uses `America/New_York` exchange date, never UTC date.

## 5. Point-In-Time Universe

The current Finviz export is a current snapshot. Applying historical price filters to
its symbols produces a historically screened current universe, not a historical
universe. Such a result remains diagnostic-only.

Evidence-grade membership requires:

- immutable provider snapshot observed before the decision cutoff. A same-day
  premarket Finviz snapshot may affect later decisions that day; a snapshot observed
  after a decision may not;
- stable `SecurityId`, `IssuerId`, provider asset ID, and share-class ID;
- non-overlapping symbol-effective intervals;
- listing and delisting intervals;
- point-in-time classification and market capitalization where used;
- one deterministic membership decision for every expected candidate;
- issuer-level deduplication or an explicit share-class policy;
- terminal outcomes for selected delisted securities.

Finviz snapshots collected from now onward can support point-in-time research from
their first complete archived date. Finviz alone cannot reconstruct historical
membership before that date.

## 6. News And Quote Evidence

News is append-only and revision-aware:

- an article has a stable provider identity;
- every content change creates a new revision;
- provider-created, provider-updated, and observed receipt timestamps are retained;
- missing provider timestamps are quarantined, not replaced with `UtcNow`;
- story clustering is non-destructive and global across symbols/providers;
- a story-to-symbol relation identifies subject, material mention, or market-wide;
- sentiment is a separate assessment keyed by revision and model/input hash.

For live news, information availability is the observed local receipt time. For
historical REST news, first provider publication time is a labelled proxy. A later
`updated_at` revision must never alter an earlier decision or substitute for historical
availability. If first-publication evidence for a historical revision is unavailable,
that revision is diagnostic and promotion-ineligible. Historical download time is
never substituted as historical availability.

Historical news collection paginates to exhaustion. Page, article, or date-window caps
produce an incomplete dataset, never an apparently successful partial result.

Catalyst execution evidence requires event-local historical SIP NBBO:

- collect quotes from five minutes before availability through the maximum horizon plus
  five minutes;
- merge overlapping windows for the same symbol/session;
- preserve quote timestamp, receipt timestamp, bid/ask, sizes, exchanges, conditions,
  tape, sequence, feed, and raw lineage;
- long quoted returns use eligible ask at entry and bid at exit;
- short quoted returns use eligible bid at entry and ask at exit;
- stale, crossed, one-sided, wrong-feed, or session-crossing quotes are censored.

Candle-close returns remain useful response diagnostics but are not executable returns.

## 7. Collection Lifecycle

Each collection job has a stable job ID and a deterministic request plan.

```text
planned -> collecting -> normalizing -> validating -> committed
                  \-> incomplete
                  \-> quarantined
                  \-> cancelled
```

Rules:

1. Persist the request plan before the first provider call.
2. Archive each provider page before parsing it.
3. Persist page/cursor completion after raw archival and successful validation.
4. Detect repeated page tokens, unexpected symbols, feed mismatch, and range gaps.
5. Resume from committed checkpoints without duplicating logical observations.
6. Publish normalized partitions with immutable conditional creation.
7. Commit the dataset catalog row last.
8. Never expose `collecting`, `incomplete`, or `quarantined` datasets to research.
9. Evidence-mode research performs no network calls.

The collector uses bounded asynchronous pipelines. One shared `HttpClient`/provider
transport is reused; concurrency is limited by provider policy and cancellation and
timeout apply at request, symbol, and whole-job levels.

## 8. Readiness Gates

### 8.1 Common

- every partition hash verifies;
- every raw lineage reference resolves and verifies;
- schema and normalizer versions are supported;
- requested coverage is complete;
- no duplicate logical keys;
- no unexpected provider/feed/adjustment mix;
- no unresolved security identity or overlapping symbol interval;
- no unresolved corporate action in a traded interval;
- exchange calendar and timezone version recorded;
- data-quality report has no hard failure.

### 8.2 Swing momentum

- at least 60 independent monthly formations;
- at least 12 untouched holdout formations;
- at least 30 eligible issuers per formation for top/bottom 30% analysis;
- at least 100 eligible issuers for decile inference;
- at least 30 distinct selected issuers in holdout;
- at least 95% expected daily-bar coverage while listed;
- 100% corporate-action and terminal-outcome reconciliation;
- point-in-time membership and classifications only;
- no issuer or month contributes more than 10% of primary observations;
- SPY, equal-weight universe, and relevant sector benchmark coverage.

### 8.3 Intraday catalyst

- SIP bars and SIP quotes from the same feed;
- at least 40 prior same-time sessions for cumulative RVOL, target 63;
- at least 300 independent stories overall and 100 in a proposed catalyst cohort;
- event-local two-sided quote coverage for the claimed executable horizons;
- session cohorts separated into premarket, regular, postmarket, and overnight;
- chronological development, validation, and untouched holdout partitions;
- matched no-catalyst controls and market/sector-adjusted returns;
- estimated edge at least twice round-trip execution cost;
- original catalysts separated from derivative/listicle coverage.

## 9. Promotion Firewall

Research cannot make a strategy visible to paper or live trading.

Runtime visibility requires an immutable `PromotionDecision` containing:

- accepted research run manifest;
- exact strategy config hash and code version;
- dataset and universe evidence hashes;
- development, validation, and one-time holdout results;
- costs and concentration checks;
- human approver, timestamp, and reason.

The runtime strategy catalog is derived only from accepted promotion decisions.
Filename allowlists and UI selection cannot bypass this contract.

Promotion registration is not part of the research catalog interface. The authorized
operator boundary verifies its principal, the holdout-consumption record, and all
referenced hashes before atomically registering the immutable decision.

## 10. Local And Production Deployment

Local:

- immutable content and Parquet partitions on local disk;
- a dedicated SQLite evidence catalog with a single transactional writer;
- local operational SQLite remains separate.

Production:

- immutable bytes and manifests in Azure Blob using managed identity and
  `If-None-Match: *`;
- catalog in managed PostgreSQL;
- pod-local ephemeral operational candle cache;
- no shared SQLite database on Azure Files;
- retention and deletion operate through catalog references and pins.

Raw evidence is retained for at least 24 calendar months and indefinitely when pinned by a
promotion, holdout, audit, or legal hold. Research manifests, quality reports,
promotion decisions, and operational journals are retained indefinitely.
Unpinned normalized evidence partitions are also retained for at least 24 calendar
months. Shorter retention is prohibited because it would leave indefinite manifests
whose underlying evidence can no longer be replayed.

A pin has a typed subject: dataset, research run, promotion decision, audit case, or
legal hold. The catalog resolves and persists the complete transitive reference closure
at pin time: run/decision to datasets, datasets to partitions, partitions/rows to
observations, and observations to raw content. Retention cannot remove any member of a
pinned closure.
Promotion, audit, and legal-hold reference subjects are registered through a separate
authorized interface; research code cannot create those subjects or lifecycle decisions.

## 11. Acceptance Matrix

The implementation is not complete until these tests pass.

| Area | Acceptance test |
|---|---|
| Raw archive | Identical payload retries reuse one content object while retaining distinct receipt evidence |
| Raw archive | Crash between content and observation publication leaves an invisible recoverable orphan |
| Raw archive | Crash after observation publication but before checkpoint publication resumes idempotently |
| Raw archive | Tampered content or manifest is quarantined |
| Raw archive | Finviz CSV, WebSocket news, sampled market messages, bars, quotes, trades, and broker responses are raw-before-parse |
| Market pages | Every page is archived before JSON parsing |
| Market pages | Repeated token, wrong feed, unexpected symbol, or incomplete range fails the dataset |
| Provider resilience | Central endpoint budgets honor `Retry-After`; 5xx exhaustion, timeout, cancellation, and queue saturation fail explicitly |
| Bar semantics | Requests contain explicit feed, adjustment, currency, and `asof` |
| Cache | A narrow cache cannot satisfy a wider request |
| Dataset commit | Kill after each partition boundary exposes either the prior generation or no dataset, never a mixed generation |
| Dataset replay | Same raw observations, schema, canonical writer, normalizer, config, code, calendar, security master, and partition plan produce byte-equivalent manifests |
| Dataset lineage | Dataset to partitions to observations to raw content forms a complete verified graph |
| Dataset encoding | Every normalized row contains schema/config/code/run/feed and source/receive clocks; money uses decimal/fixed point |
| Row lineage | Every normalized row resolves to its exact raw observation/content set |
| Dataset corruption | Malformed JSON, CSV, or Parquet is quarantined after exact raw archival |
| Concurrent collection | Identical jobs in separate processes commit one logical checkpoint and dataset |
| Immutable store | Hash/length mismatch and conditional-write collision fail closed locally and in Azure |
| Catalog | Transaction rollback, migration, backup/restore, and corrupt-catalog recovery preserve discoverability rules |
| Security master | Identity survives a symbol change and overlapping intervals are rejected |
| Corporate actions | Split fixture reconciles raw and adjusted OHLC/volume |
| Corporate actions | Dividend, merger, spin-off, acquisition, delisting, and symbol-change fixtures reconcile or fail closed |
| Terminal outcomes | Delisted and acquired selections have verified terminal outcomes |
| Universe | Current Finviz snapshot cannot satisfy an earlier historical session |
| Universe | Same-day snapshots affect only later decisions; daily-bar features use only completed prior New York sessions |
| Universe | Raw bars drive absolute price and dollar-volume filters |
| News | Missing timestamps quarantine the revision |
| News | Revisions append and preserve first observed receipt |
| News | A later `updated_at` cannot alter an earlier decision or availability clock |
| News | Pagination exhaustion and cap status are explicit |
| News | One multi-symbol story remains one story with multiple symbol relations |
| News dedup | Provider ID, canonical URL, headline hash, and configured fuzzy stage run in order; duplicates remain linked and receive the configured staleness treatment |
| News classifier | Ground-truth labels, adjudication, model evaluation, and quality threshold can block promotion |
| Quotes | Historical SIP quote pagination is raw-before-parse and feed-checked |
| Execution proxy | Long uses ask-to-bid; short uses bid-to-ask; stale/crossed quotes censor |
| Session clock | DST, early close, premarket, regular, postmarket, and overnight fixtures classify correctly |
| Research isolation | Evidence-mode research attempts zero provider network calls |
| Research window | Records outside study bounds cannot change features, novelty, controls, or outcomes |
| Temporal invariant | Every feature timestamp is at or before its decision; labels/outcomes cannot affect selection |
| Restart | Collection resumes after page and ticker boundaries without duplicate logical records |
| Retention | Pinned runs preserve datasets, partitions, observations, and raw content; deletion is tombstoned, retryable, and reference-safe |
| Promotion authority | Research code cannot register or forge a human promotion decision |
| Promotion integrity | Missing approval, hash mismatch, failed readiness, reused holdout, superseded, or revoked decision blocks visibility |
| Holdout consumption | Canonical holdout identity is consumed atomically; concurrent or repeated consumption fails closed |
| Runtime catalog | Contents equal accepted, non-revoked promotion decisions exactly |
| End to end | Discovery through simulated orders produces byte-identical journals for frozen evidence, config, and code |

Slice-one contract tests additionally cover canonical serialization, required fields,
immutable collections, UTC enforcement, legal collection state transitions, row-level
lineage, typed retention closure, promotion-interface isolation, canonical holdout
identity, atomic holdout consumption, and invalid dataset/research/promotion
references.

## 12. Implementation Sequence

1. Domain contracts for content, observations, datasets, quality, collection jobs,
   research runs, security identity, actions, and promotion decisions.
2. Filesystem immutable store and local evidence catalog.
3. Content-addressed raw observations and orphan recovery.
4. Parquet market-bar writer/reader and manifest validator.
5. Alpaca paginated bar collection through raw-before-parse transport.
6. Security-master and corporate-action collectors.
7. Universe snapshot lineage and point-in-time membership integration.
8. Revision-aware news and historical SIP quote collectors.
9. Swing and intraday event studies consume verified dataset handles only.
10. Promotion registry becomes the sole runtime strategy source.
11. Full replay, restart, corruption, and end-to-end tests.

Each step must pass its focused acceptance tests before the next step starts.
