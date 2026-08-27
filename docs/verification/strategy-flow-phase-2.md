# Strategy Flow Phase 2 Verification

**Status:** acceptance-ready; implementation, deterministic verification, and independent review complete

**Date:** 2026-08-28

## Scope

Phase 2 makes paper/live discovery and market-state ownership durable. It moves
runtime source union out of `LiveRunner`, permits incremental ADD/DROP without a run
restart, and establishes one fenced Alpaca SIP websocket owner feeding deterministic
per-symbol market-state pipelines. Strategy qualification remains Phase 4; RVOL
definition changes remain Phase 3.

## Frozen Runtime Contract

| Feature | Default | Meaning |
|---|---:|---|
| Discovery refresh | 60 seconds | Re-read dynamic source membership for a running paper/live session. |
| Source expiry | 180 seconds | Remove a source's membership after no successful observation within the TTL. |
| Stream lease | 30 seconds | Exclusive ownership window for the one Alpaca market-data websocket. |
| Lease renewal | 10 seconds | Refresh ownership before expiry; stale fencing tokens cannot publish. |
| Revised-bar window | bar close + 2 minutes | Accept only Alpaca's explicit revised-bar event within this bounded correction period. |
| Active-session freshness | 3 minutes | Withhold stream snapshots when the newest completed 1m bar is older during 04:00-20:00 ET. |

## Implemented Boundaries

- SQLite persists discovery snapshots, source evidence, deduplicated memberships,
  aggregate versions, source TTL, and optimistic-concurrency state.
- Wishlist, Finviz, news, earnings, operator, and alert provenance is retained. News,
  earnings, and Stock Pulse refresh only against the run's frozen ticker allowlist
  and persist provider/event identifiers and timestamps in source metadata. A failed
  refresh preserves the prior observation only until its TTL; restart does not
  resurrect a previously removed configured source.
- `LiveRunner` refreshes membership each iteration. One ticker failure remains
  isolated, and confirmed broker exposure prevents premature unsubscription. Pending
  top-level long or short entry orders count as exposure. If a later broker poll fails,
  the runner retains the last confirmed order/position snapshot instead of treating the
  account as flat.
- `AlpacaMarketStateStreamService` is the sole websocket owner and reference-counts
  symbol scopes. Ownership is a durable lease with monotonic fencing tokens. Renewal
  starts immediately after acquisition, ownership is renewed again after connect and
  before subscription activation, and any renewal failure cancels the socket. A new
  connection attempt has a unique owner identity and advances the durable candle-store
  fence before opening its connection, so stale cross-process writes fail.
- Accepted bar and REST-repair tasks belong to a connection work tracker. DROP drains
  that symbol before eviction; connection shutdown stops acceptance, drains all work,
  disposes the transport, and only then releases the lease. Failed drain/disposal/release
  is fail-closed, leaves the unique lease to expire naturally, and stops automatic
  reconnect for that host instance.
- One bounded, ordered TPL Dataflow block per symbol processes canonical completed,
  revised, and replay bars. Malformed, duplicate, conflicting, late, out-of-order,
  stale-owner, and backpressured events return explicit dispositions.
- Snapshot publication captures an exact ownership fence and revalidates that same
  token both before and after indicator computation. A lease loss/takeover during a
  long calculation therefore cannot publish an ABA-stale snapshot.
- Recognized Alpaca completed/revised bar frames with malformed fields are rejected
  explicitly and logged with structured message type, symbol, and reason fields.
- The single receive loop consumes Alpaca subscription acknowledgements. Live buffering
  begins before batched REST recovery; newer buffered stream bars win overlap, and
  accepted stream/recovery bars persist through `ICandleStore`.
- Streaming recovery reads only its own `stream` source. Restart and replay produce
  the same completed-state fingerprint.
- Stream persistence uses a fenced append journal with incomplete-tail recovery;
  readers ignore only a malformed final record, and the next append truncates it.
  Cross-source reads use deterministic precedence: `stream > provider > derived > other`.
  Every stream write requires the explicit fenced-store interface. Archive manifests
  use a two-state handoff: a unique preparing intent is durable before candle I/O,
  while the locked consumer-visible pending manifest is committed only after every
  referenced candle file is flushed. Committed intent IDs make recovery idempotent.
- Derived 5m/15m/1h/4h bars respect overnight, premarket, regular, and postmarket
  boundaries. A source minute must either contain a real bar or be confirmed by an
  authoritative provider as a no-qualifying-trade interval. No flat candle is
  synthesized; OHLCV always aggregates real bars only. Providers without explicit
  omission semantics remain fail-closed. Cached files do not inherit Alpaca omission
  authority without their own coverage manifest. Daily swing bars continue through
  the native historical provider path.
- A missing minute inside one New York session segment is accepted as evidence but
  marks live state for REST repair. A REST response can resolve it either with the
  missing real bar or, for Alpaca, an explicit provider-level no-trade confirmation.
  Required derived timeframes remain unavailable while any gap is unconfirmed.
- REST warm-up and the batch candle pipeline reject bars that are not completed at the
  request cutoff. The pipeline records the filtered count. Cold sparse Alpaca history
  uses provider-confirmed no-trade minutes; a partial batch failure retries every ticker
  from the beginning, and a derivation failure excludes that ticker's market state.
- Strategy snapshots are unavailable during connection warming and are invalidated
  immediately when stream ownership is lost, independently of ordinary bar-age
  freshness checks.
- Historical conflicting completed bars invalidate and exclude the ticker rather
  than allowing parallel task scheduling to choose a winner.
- Subscription acknowledgement validates the complete expected symbol set on every
  required Alpaca channel, not merely the most recent add/drop delta.
- A restart reconstructs discovery membership and provenance from SQLite. A dynamic
  source with no new observation preserves the old evidence only until its original
  TTL; it neither emits a false DROP immediately nor extends expiry indefinitely.
- `LiveRunner` uses stream snapshots only when every required timeframe has enough
  configured warm-up bars; otherwise it falls back to the existing REST pipeline.

## Verification Evidence

```powershell
dotnet build TradingFlow.slnx --no-restore --nologo
dotnet build src\TradingFlow.Mobile\TradingFlow.Mobile.csproj `
  -f net10.0-android --no-restore --nologo
dotnet test src\TradingFlow.Tests\TradingFlow.Tests.csproj `
  --no-build --no-restore --nologo
dotnet ef database update --project src\TradingFlow.Data `
  --startup-project src\TradingFlow.Web --context TradingFlowDbContext `
  --connection "Data Source=<disposable-db>" --no-build
dotnet ef migrations has-pending-model-changes --project src\TradingFlow.Data `
  --startup-project src\TradingFlow.Web --context TradingFlowDbContext --no-build
```

Final verification results:

- Solution build: passed, 0 warnings, 0 errors, including `net10.0-android`.
- Focused Phase 2 and adjacent regression tests: 102 passed, 0 failed, 0 skipped.
- Complete solution tests, including the live Alpaca candle-pipeline integration:
  1,224 passed, 0 failed, 0 skipped.
- Fresh disposable SQLite migration: passed through `AddDurableMarketState`.
- EF model drift check: no pending model changes.
- The external Alpaca candle-pipeline integration completed successfully against
  `data.alpaca.markets`; provider request/response mapping and pipeline ingestion are
  live-verified in this checkpoint.
- Independent requirements and concurrency/data-integrity reviews found no remaining
  P0-P2 defects after remediation. Both reviewers marked Phase 2 acceptance-ready.

## Acceptance Mapping

- Concurrent duplicate discovery: one aggregate membership projection.
- Multi-source DROP/expiry: symbol remains until the final source expires.
- ADD/DROP during one runner instance: applied on later iterations without restart.
- Exposure during DROP: symbol remains subscribed.
- Pending short entry and broker-read failure: last confirmed exposure remains in the
  processing universe.
- Partial broker snapshot: no entry or exit order decision is allowed until both the
  order and position reads succeed in one iteration.
- Alpaca open-order lifecycle: accepted-for-bidding, pending-cancel, and
  pending-replace entries retain ownership exactly like accepted/partially-filled.
- Lease conflict/takeover/stale release: fenced and deterministic.
- Forced renewal loss: old reader cancellation and transport disposal complete before
  a second connection is created.
- Entry-gate cancellation and concurrent observations: status subscriptions release
  in `finally` and remain reference-counted.
- DROP during repair: subscription generation invalidates stale work, accepted work
  drains, and post-backfill membership is rechecked before state can remain published.
- Non-cancelling accepted work: drain timeout stops the host fail-closed; no second
  socket is created and the lease is not released early.
- Torn stream-journal tail and conflicting sources: restart recovers the complete
  prefix and applies deterministic source precedence.
- Intra-session missing minute: live state requires REST repair; Alpaca-confirmed
  no-trade intervals complete a derived bucket without inventing OHLCV, while unknown
  provider omission semantics remain unavailable.
- Lease invalidation: strategy snapshots disappear immediately, and an old ownership
  generation cannot publish a later bar.
- Historical conflict: the affected ticker is excluded deterministically.
- REST partial bar: filtered before normalization/indicator computation and reported
  in pipeline metrics.
- Indicator-compute ownership change: the in-flight snapshot is rejected.
- Incomplete batch and derivation failure: every ticker is retried independently and
  failed ticker state is withheld.
- Archive write failure: a preparing intent remains available for retry, while no
  consumer-visible pending manifest is published for incomplete candle I/O.
- Fence-token crash boundary: exclusive ownership uses a stable lock file and an
  atomically replaced monotonic token file, so an interrupted update preserves the
  previous durable fence.
- Malformed recognized Alpaca bar: explicitly rejected and structurally logged.
- Completed/revised duplicate bars: one logical completed state.
- Restart and replay: identical state fingerprint.
- Regular/postmarket boundary: never combined into one derived bar.

## Deliberate Next Boundaries

- Phase 3 freezes the authoritative RVOL definitions, early-close/DST fixtures, and
  all vendor-volume separation. This phase does not change strategy mathematics.
- Phase 4 owns durable strategy qualification and arming transitions. Discovery is
  evidence, never permission to trade.
