# Configurable swing preparation and morning review

Status: implemented and component-verified, 27 September 2026. New workflows remain disabled until an operator reviews and enables them. See `docs/swing-preparation-verification.md` for evidence and operational limits.

## Purpose

Prepare a reproducible next-session discovery list after the close, then refresh it before the open using new market and news observations. Scheduled preparation produces evidence and review snapshots. TradingFlow's existing strategy, risk, account and execution services remain responsible for any eventual order.

Use C# application services and the existing durable job/repository patterns. Do not add a second trading engine, language runtime or external scheduler. This is an application feature, not a Codex reminder automation.

## Operator workflow

1. Create a named workflow, choose persisted wishlists and saved Finviz queries, and configure preparation/review schedules. Preview several upcoming occurrences in New York and the operator's local time before enabling it.
2. After the session closes, capture source membership and completed daily evidence. Apply explicitly configured discovery filters and publish a Nightly preparation snapshot for the next trading session. Never change the source wishlist in place.
3. Before the next open, refresh the carried symbols and, when enabled, discover additional symbols from the configured sources. Attach overnight news, earnings context, gaps and same-time premarket volume evidence. Publish an immutable revision after each refresh.
4. Present the morning changes: retained, newly discovered, removed, incomplete and stale. The operator can inspect exact reasons and sources, create a separate curated revision, or open the existing candidate/run workflow with the selected snapshot. Before any research/paper handoff, materialize accepted membership into a workflow-owned persisted wishlist revision, bound to the discovery snapshot and source hashes; this does not overwrite user-curated source wishlists. Finviz discoveries and manual additions follow this same persistence path.
5. At the configured handoff cutoff, freeze the morning review revision. Starting research or paper remains an explicit action through the existing APIs. Snapshot expiry, strategy identity, market evidence and execution authority are revalidated there. A reviewed list is not trade eligibility.

Editing the curated revision must preserve additions/removals, actor, reason, timestamp and parent snapshot. Removing a discovery member does not close an existing position or stop its protection.

## Suggested initial settings

These are adjustable operational defaults, not profitable strategy parameters. New workflows start disabled.

| Setting | Suggested value | Meaning |
| --- | --- | --- |
| Calendar | Provider-backed US equity sessions | Session identity, holidays and early closes are authoritative; no weekday-only approximation. |
| Exchange timezone | America/New_York | Store UTC; show New York and operator-local times, including differing DST transition weeks. |
| Nightly preparation | 45 minutes after regular close | 16:45 on a normal session; relative to the actual early close on shortened sessions. |
| Morning review window | 90 to 15 minutes before regular open | Normally 08:00-09:15 New York. |
| Morning refresh interval | 15 minutes | Bounded repeated observations; one durable occurrence per scheduled slot. |
| Handoff cutoff | 15 minutes before regular open | Last scheduled morning publication; no automatic run/order start. |
| Sources | Selected persisted wishlists; saved Finviz queries optional | Explicit union/intersection and required/optional status per source. |
| Fresh discoveries | Enabled from configured sources | New morning symbols can join even when absent from the nightly list; collect their own required history. |
| Daily discovery filters | Disabled until selected | Optional price, dollar/share liquidity, history coverage, trend and volatility filters. |
| Morning context thresholds | Disabled until selected | Optional gap, same-time RVOL, earnings/catalyst annotations. Thresholds label observations; they are not hidden entry gates. |
| News | Optional evidence | Absence stays visible; a selected strategy independently enforces its own catalyst requirements. |
| Review display size | 30 rows per page | Pagination only; does not silently discard or prioritize execution candidates. |
| Resource envelope | 500 discovered symbols, 4 concurrent symbol enrichments, 1 occurrence per workflow | Adjustable within server-enforced limits; truncation or capacity exhaustion is explicit and cannot be labeled complete. |
| Freshness | Required source observations at most 30 minutes old at morning publication | Configurable and distinct from provider expiry, daily-bar finality and broker quote freshness. |
| Missed schedules | Skip expired work | Record a missed occurrence. Optional catch-up only within a configured window and before the phase deadline. |
| Notifications | Ready, blocked or failed transitions | Configurable channels; deduplicate by workflow/session/phase/revision. No alert on every unchanged refresh. |

Finviz filters remain discovery controls and their query/observation provenance is captured. Technical filters recalculated locally use completed Alpaca evidence with declared formulas and windows. Volume-sensitive filters and RVOL require recorded SIP feed provenance and adequate coverage; IEX-only or unknown feed evidence is incomplete for those calculations. Do not assume Finviz ATR, RVOL or trend fields are identical to TradingFlow calculations.

Premarket RVOL must compare like session/time cohorts, not premarket volume against an unspecified full-day average. Missing baseline coverage is incomplete, never zero. A gap requires an explicit reference close and valid observed price timestamp. Price/volume thresholds must be named, typed, unit-bearing, bounded and reproducible; no arbitrary expression evaluation in configuration.

## Scheduling and freshness semantics

- Each review session has a target exchange trading date. Friday preparation targets the next actual trading session, including Monday holidays. A normal morning review starts from the latest complete preparation for its preceding trading session.
- The nightly record is retained evidence, not an overnight extension of an expiring executable UniversePreview. Morning refresh creates a new discovery snapshot with fresh source observations; it never changes old timestamps or renews old evidence silently.
- Publication cutoff and handoff-use deadline are separate settings: the initial profile stops publication at 09:15 and permits snapshot handoff strictly before the regular open at 09:30 New York. Morning publication expiry is the earliest of its configured lifetime, required provider/source validity and the handoff-use deadline. Expiry is exclusive: at or after it, handoff requires a freshly resolved preview. The report itself remains readable as historical evidence. Order quote and ticket expiry remain separately enforced.
- Missing preparation defaults to blocked with a clear reason. An optional standalone morning mode may gather all sources and required history from scratch, visibly recording that no nightly parent exists.
- If final daily bars arrive late, retry within the preparation deadline. Never mark the session complete using an unfinished daily bar. If the source trading date or coverage is wrong, publish incomplete evidence and block a complete handoff.
- Required source failure blocks publication of a ready handoff. Optional source failure yields an explicit partial result. Empty but successfully resolved membership is a completed empty result, distinguishable from failure. Store this as an occurrence result with no start action; do not force an empty result through the existing nonempty executable UniversePreview contract.
- Provider/calendar outages do not fall back to guessed sessions. Cached schedules are usable only under an explicit freshness/coverage policy.
- Snapshot revision publication uses a fixed observation cutoff: data received later belongs to a later revision. Human review edits cannot rewrite that cutoff. Previously started runs remain bound to their captured revision.
- Overnight retries must not overlap morning work unboundedly. Configure phase deadlines, bounded retries/backoff, timeouts and cancellation. Pausing a workflow prevents new occurrences; cancellation of current work is a separate explicit operation and does not affect existing trading runs.

## Configuration ownership and validation

Workflow settings own schedules, discovery sources and filters, collection freshness, resource bounds, review presentation and notifications. Strategy artifacts own daily setup, confirmation rules, entry sessions, stops and exits. Portfolio configuration owns account risk. Market Predictor remains advisory and cannot select, exclude, rank for execution or authorize members.

Persist each saved definition with an immutable revision/content hash, creator/updater and effective date. Existing queued/running occurrences use their captured revision; saving edits affects only future unclaimed slots. A logical scheduled slot has a cross-revision uniqueness key (workflow ID + target trading date + phase + scheduled slot). Saving a new revision cannot enqueue a second occurrence for an already claimed slot; an explicit supersession must first cancel an unstarted old occurrence and atomically replace its reservation. Schedule edits never create retroactive slots. A manual Run now captures a new revision-bound request with an idempotency key and obeys calendar, evidence and phase limits; it cannot turn a past observation into an on-time snapshot.

Reject invalid timezone/calendar identifiers, nonpositive intervals, inverted or empty windows, overlap without an explicit policy, unknown sources/filter names, inaccessible wishlists, invalid threshold units, impossible deadlines and values beyond server resource caps. Require authenticated mutation/CSRF and server-side ownership/access checks. Resolve the scheduled execution principal from stored authorization; never store a user's session cookie in a job. Revoked access stops future discovery work.

## Durable state and implementation mapping

Workflow occurrence identity: workflow ID + definition revision + target trading date + phase + scheduled slot. The cross-revision logical-slot reservation, immutable revision binding and existing fenced job leases prevent duplicate execution/publication across two workers or restart. Each attempt publishes at most one revision idempotently; explicit manual reruns are separate from scheduled slot reservations. An interrupted discovery occurrence may resume/retry within its deadline; superseded workers cannot publish. Manual reruns receive explicit new occurrence identity and preserve prior results.

Occurrence states: scheduled, queued, running, completed, completed_with_gaps, blocked, failed, cancelled, missed. Keep publication readiness separate: complete, partial, unavailable, expired. None of these means qualified, triggered or in trade.

Reuse these existing foundations:

- IMarketSessionScheduleProvider for exchange sessions.
- Wishlist/Finviz discovery and immutable universe source provenance.
- Completed-bar warm-up and market/news evidence services.
- Durable job persistence, fenced workers and application event cursors.
- Shared authenticated API contracts and Web/Android workflow projections.
- Existing candidate/run APIs for the explicit handoff after fresh validation.

Implemented components include workflow-definition/occurrence persistence, calendar occurrence planning, one discovery-only durable handler dispatching the two phases, workflow-owned persisted wishlist revision binding, phase/revision provenance, configuration validation, shared read/mutation contracts, and Web/Android controls. Existing MobileAutomationService is oriented toward trading sessions; do not reuse its start behavior as a discovery scheduler.

Preparation snapshots and their readiness/partial/empty results require separate persistence from executable UniversePreview records. Add an explicit validated handoff adapter: apply configured union/intersection and curated membership, persist the exact accepted wishlist revision, then construct a nonempty preview through the existing integrity/freshness validation. The current discovery service unions source membership and uses the earliest included source expiry; it does not already implement this workflow's selection or optional-source policy. Preserve every original observation timestamp, exclusion and dependency in the preparation audit. Only valid contributing source evidence may enter an executable preview; an optional failed source is a recorded gap, not fabricated membership. If a selected member depends on expired evidence, re-resolve it or remove it with an explicit reason. Creating a new wishlist revision cannot launder stale upstream observations into fresh evidence. Verify that the preview's membership exactly equals the accepted revision and keep original provenance accessible from the handoff.

Shared TradingFlow contracts are in `TradingFlow.Contracts/Preparation`; authenticated endpoints are under `/api/v1/preparation`. The shared client, hosted SQLite/CSRF tests, browser fixtures and native acceptance exercise these contracts. No Market Predictor contract change is required.

## Web and Android

Add a Preparation workflow view with definition editor, next occurrence, target session, enable/pause, Run now, cancellation and occurrence history. Snapshot review shows phase, as-of time, provenance, partial/missing evidence, source exclusions, new/removed symbols and links to existing market/news detail. Display ordering is explicitly for human review and is not an execution rank.

The handoff action names its operation: open research candidates or review an authorized paper run. Show the selected snapshot and exact strategy identity, stale/expiry reasons and lifecycle restrictions. A notification opens the exact backend/workflow/occurrence/revision and can resume through login; permission denial leaves history accessible. Never silently create a paper job when the preparation notification is opened.

## Acceptance criteria and delivery order

1. Calendar/definition tests: normal sessions, early close, holiday/weekend, US/European DST mismatch, missing/stale calendar, invalid settings, saved edits while work is queued.
2. Durable scheduler tests: duplicate enqueue, two workers, lease expiry, retry/restart, pause/cancel, missed deadline, bounded capacity and idempotent publication.
3. Evidence tests: incomplete daily bar, stale/failed optional and required sources, new morning symbols, deduplication, exclusion provenance, reserved source identities, late news revision, no invented historical membership, no overnight TTL extension, partial/empty/truncated distinction.
4. Handoff tests: expired snapshot fails, independently fresh broker quote required, no automatic strategy/job start or order submission, no bypass from review ranking or ML evidence, an existing run stays bound to its original revision.
5. Hosted shared API/SQLite contract tests, then Web and Android configuration/history/review flows, authentication, notification navigation and reconnect with drafts preserved.
6. Run discovery-only acceptance for representative real sessions and inspect stored snapshots before enabling any user workflow. External paper execution remains a separate already-planned test.

Implement in that order: shared definition/calendar -> durable discovery jobs -> immutable evidence/handoff -> Web -> Android -> acceptance. Reuse the current engine and providers. Do not add the Gemini document's fixed gap/volume thresholds or entry windows as universal defaults.

## Definition verification

Independent requirements and design reviews approved this definition after clarifying cross-revision scheduling, SIP provenance, publication/use deadlines and the preparation-to-preview adapter. No blocking issue remains in the definition. The implementation received independent requirements and code/design review. All actionable findings were fixed and reviewed again. Verification and intentionally unrun operational checks are recorded in `docs/swing-preparation-verification.md`. No user workflow was enabled and no order was submitted.


## Delivered behavior and operating details

- Open **Preparation** in Web navigation or **More → Preparation** on Android. The editor shares typed settings across clients. Definitions save with an expected revision; concurrent edits require reloading.
- Scheduled slots begin strictly before the exclusive publication deadline. Calendar previews show UTC, New York and device/operator local time. Schedule planning uses a revision-fenced durable watermark, at most seven elapsed days per batch, and an authoritative calendar cache valid for one hour.
- Source freshness never overrides source expiry. Current wishlist/Finviz captures last three minutes. A morning snapshot may therefore expire well before the open. Collect another review within the configured window, or configure a refresh interval appropriate to the intended operator review period; a longer workflow lifetime cannot extend upstream validity.
- Daily discovery filters use the completed adjusted SIP history captured in each revision. Share/dollar liquidity uses 20-session arithmetic means; trend uses the configured simple average; range is `(high-low)/close*100`. These controls do not grant strategy admission.
- Same-time premarket RVOL collection is opt-in through its annotation setting and uses the existing market-evidence profile/indicator engine. Missing baseline evidence remains unavailable. News/earnings context comes from the existing local collection services, explicitly reports missing/truncated coverage, and does not become an execution gate here.
- Manual curation creates a new collection occurrence, stores additions as an immutable persisted wishlist, preserves parent/actor/reason/request time, and re-observes that membership when collection starts. Effective edits carry forward through chained revisions. Removals precede capacity checks and retain exclusion audit rows.
- Handoff materializes exact accepted membership and preserves source dependencies. Generic re-resolution of preparation-owned wishlist revisions is prohibited. Research explicitly creates candidates through the existing API; paper review carries the accepted snapshot into the existing paper form and starts nothing on navigation.
- Android notifications are local notifications delivered while the application is connected in the foreground. Durable events are replayed after reconnect; permission denial retains alert history. This feature does not add an OS background push transport. Web exposes durable alert history.
- Backend schema migrations are included. Verification used isolated databases; the operational database has not been migrated by this task. Normal application startup applies its pending migrations under the existing initializer.
