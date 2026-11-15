# Remaining Fable work: implementation proposal

15 November 2026. Status: NEXT-1 through NEXT-4 approved by the user after separate plan/execution and code/design reviews and a plain-language explanation. Implementation is beginning. NEXT-5 through NEXT-7 still require their separate decisions/designs. This extends the completed daily-workflow/chart checkpoint.

Checkpoint labels NEXT-1 through NEXT-7 are local planning IDs, separate from the audit finding IDs.

## What this proposal closes

The current audit's A1/A2/A4/A5 and B1–B4/B7 repairs are implemented in their reviewed scopes. The five chart findings have been addressed. Plan 192's replay and the actual operator migration count have been verified. The new optional-indicator/workflow release passed 226 browser tests and 3,995 backend cases, with zero failures and four explicit profiling opt-ins unrun. Commit 57ee499 is installed locally with trading paused. Signed-in chart, News, Earnings and paused-ticket checks passed; protected operator records and migration 29 were verified after the update.

The whole audit remains open. The table below separates software completion from evidence that cannot be obtained by adding code. The user has asked to finish the remaining audit, while keeping sector rotation disabled, separate independent reviewers, local commits and no GitHub synchronization.

| Checkpoint | Fable items | Result | Proposed sequence |
| --- | --- | --- | --- |
| NEXT-1 | E1, C4 and factual draft corrections | Scheduled immutable screen captures; accurate catalog/status documentation | First, so future observation days are not lost |
| NEXT-2 | A6, A7 | Every proven exit condition and smaller audit records without changing simulated execution or decision evidence | After NEXT-1; isolated implementation can overlap NEXT-3 |
| NEXT-3 | B6 | Decision, recorded local pre-POST attempt time and fill comparison by exact strategy and month | Offline implementation and tests; real fills are a separate acceptance step |
| NEXT-4 | C1–C2, research-ledger part of section 7 | Frozen trial identity before work, counted attempts, signal-study gate and a read-only research ledger | Before any new economic experiment |
| NEXT-5 | C3, A3, E3 | Approved success policy, same-basis benchmark accounting and research-matched screen definitions | Decision/design work; no silent investment-policy choice |
| NEXT-6 | E4, ORB sections 3/9/10 | Frozen forward cohort analysis and the specified bounded ORB signal study | Separate reviewed run manifests; only after data and prerequisite gates exist |
| NEXT-7 | Paper operational acceptance; remaining D1/D2 layers | Actual regular-session order/protection evidence; forming/weekly candles and private trade markers | Separate concrete execution/chart designs, not implied by NEXT-1–NEXT-4 approval |

## NEXT-1: start retaining scheduled Finviz observations

### Reuse and missing capability

`FinvizClient.GetScreenerSnapshotAsync` already archives complete HTTP response bytes, actual receipt time and SHA-256 through `GetArchivedTextAsync` and `FileSystemRawArchiveWriter`. Its five-field operational projection does not discard the raw CSV. Reuse those components, `ScreenerPresetService.ListAsync`, the exchange schedule provider and existing SQLite lease/publication patterns. Do not add a parallel download store or use the screener/ML workflow to schedule capture.

Add a dedicated observational collector and capture catalog. It can run while trading is paused, only when market-feed collection and the new capture option are explicitly enabled. Tests and UI fixture hosts disable it. It cannot create wishlist members, candidates, strategy authorizations or orders.

### Exact proposed behavior

- Capture each currently saved local screen twice per actual exchange session: 30 minutes before its regular open and 15 minutes after its regular close. Normally these are 09:00 and 16:15 New York; an early close uses its actual close plus 15 minutes. Exchange holidays have no slots.
- A logical slot is identified by saved preset ID, New York session date and slot kind. At its first durable claim, freeze the preset name, normalized selection query, query hash, preset update time and requested export format; record actual claim time. If an edit occurred after the planned slot but before the first claim, its planned-slot revision is unknown: any capture of the new revision is explicitly a later observation. A mid-slot edit cannot change an already claimed capture; the next slot uses the new query.
- Keep planned time, actual request/receipt time, publication time, HTTP outcome, raw archive ID/hash/bytes, returned headers, row count and coverage status distinct. Retain every returned column, including unfamiliar columns. Validate each distinct enabled export shape with a normal authorized capture. Export-column coverage and complete matching-row membership are separate claims: provider terminal/count/cap evidence is required to assert the latter or verified empty. Where it is unavailable, retain real captures as membership unverified. Unexpected or reduced columns remain explicit; never invent full-export support.
- Use one shared collector-owner lease across hosts and one request at a time. Enforce a 16 MiB ceiling before buffering; retain bounded metadata for oversized responses rather than claiming a complete raw capture. Apply one total budget of at most three attempts per preset/date/slot within ten minutes. Give all eligible presets first attempts before retries; record explicit missed slots if the finite window cannot serve every preset. A rate-limit retry obeys its delay only when it fits that window. No unbounded queue or repeated request on every browser refresh.
- Use a durable lease and unique slot key. Persist an attempt/correlation identity before the request; associate any archived receipt with it, then publish the verified capture once. Restart recovery verifies a matching receipt before repeating a request. Multiple raw attempts may exist, but only one successful logical slot is published. Every attempt remains explainable.
- A late host does not pretend to recover yesterday's screen. Outside the bounded slot window, record missed evidence. Unknown preset/query history stays unknown. Do not backdate a current capture or fabricate a historical query revision.
- Distinguish disabled, pending, captured, verified empty, partial, failed and missed states. Preserve raw failure/partial receipts. Only a verified successful CSV can establish an empty screen; HTTP 200 or a login/error page cannot.
- Show last capture, next slot and precise failure/coverage reason on the saved-screen view and Operations. These are observational statuses, separate from strategy admission and trading health.

This adds capture configuration, a hosted observational service, application orchestration, a capture repository/table and migration, and small authenticated read-only status endpoints/UI. Raw archives remain in their current location. The catalog records references rather than another copy of each CSV. Existing saved screens and original archives are preserved. Approval includes enabling this read-only schedule locally for the current saved screens after its real capture check; it does not approve replacing their filters.

Tests: DST and holidays, early close, restart and concurrent hosts, preset edits/deletion during a slot, exact receipt recovery, exhausted retries/rate limits, HTML or truncated CSV, valid empty exports, unknown columns, payload ceiling and pause isolation. Acceptance: one real capture with verified headers/receipt/hash and an observed scheduled slot; future missed slots are visible. Report actual request count and storage bytes, not an invented storage forecast. Next-session acceptance cannot be claimed before the next eligible slot.

C4 and factual corrections reuse the current catalog and operational receipts: six research artifacts, five enabled, rotation disabled; the operator uses port 10088 and the documented 5088 command is an explicit override. Correct the success draft's sector-basket versus SPY comparison and its overly broad claim that every earlier ORB result used a point-in-time universe. Keep its unapproved financial targets marked unapproved, including the draft 20% drawdown versus the implemented 10% paper entry stop. Do not replace the latter with the draft value.

## NEXT-2: report complete exit evidence and remove repeated audit payloads

### A6: simultaneous exit conditions

Reuse the existing factor/off-list predicates in `BacktestRunner.Simulation` and the technical execution predicates. Add a typed, stable ordered list of observed conditions with decision-close and availability times, propagated through pending exit, candidate trade, published trade and details view. `ExitReason` continues to identify the actual execution choice and price/timing path. Computing the additional observations must not mutate state or choose a different order.

For example, if a holding is below its average and has left the selected list at the same eligible close, the published evidence names both. If an intrabar stop already closed the position, do not attach conditions from a later close that it never reached. Honor minimum holding periods, scheduled decisions and unavailable inputs; unknown is not false or true.

Tests compare all economic fields before and after: orders, fills, quantities, times, prices, fees, cash, daily equity and monthly results. Cover simultaneous factor/off-list, one true condition, missing/unscheduled evidence, earlier protective exits and data-end exits. Use synthetic fixtures and existing test inputs. A new economic replay of plan 192 requires a separately authorized normal saved-plan run; do not rewrite its original results or enable rotation for this change.

### A7: exact, point-in-time evidence references

`CandidateAuditExporter` already streams. The repetition starts earlier in kernel audit/rule inputs, canonical candidate transitions and the backtest journal. Reuse canonical evidence hashing and artifact publication. Keep the decision kernel and `SemanticDecisionSha256` unchanged; encode repeated drawdown evidence at the journal persistence boundary, then publish an immutable run-scoped content-addressed evidence file/index with the decision records.

References identify exact canonical observations or verified immutable chunks/prefixes and their as-of cutoff. A ticker's final history cannot stand in for its earlier history: episode outcome and recovery evidence can change later. Resolving a reference must reconstruct the exact decision bytes and semantic hash. Bound the in-memory reference cache and stream storage/export. Validate relative paths, run identity, hashes and publication completeness; missing/corrupt evidence fails explicitly.

Use one new writer with an explicit candidate-audit encoding identifier and sidecar reference/hash in its manifest. New-format verification strictly requires its sidecar and exact hydration; never guess formats. Preserve the existing inline published-artifact verifier for read-only inspection of original results, with no second writer, old execution flow or automatic historical rewrite. Change `BacktestArtifactIntegrity`, candidate audit readers and the Web audit service together; test an original inline artifact unchanged. The content index is disk/journal-backed with a bounded memory cache, not an unbounded per-run dictionary.

Tests: exact rehydration/hash parity, two as-of versions, no future evidence leak, missing/tampered/cross-run reference, crash/retry/publication failure and bounded cache. Measure both worker-journal and published-artifact bytes on a deterministic repeated-history fixture. Fable's 71 MiB-to-few-MiB expectation is a measurement target, not a promised result; record the actual reduction and retained content parity.

## NEXT-3: explain the distance between a decision and its paper fills

Reuse immutable `StrategyOrderPlan.TriggerReferencePrice`/`TriggeredAtUtc`, candidate decision hashes, `OrderIntentRecord`, the lease-fenced dispatch-attempt transaction, and individual position/fill execution IDs. The quote recorded by `EntryGateChain` is an admission quote; it is not automatically the quote at the actual broker POST.

Carry the exact existing `EntryGateChain` `BrokerMarketObservation` through `EntryGateApproval`: bid/ask/feed, provider quote timestamp, local `ObservedAtUtc`, and the existing raw quote receipt ID/hash/time from `AlpacaBrokerClient`. Persist it as separate typed telemetry in the winning initial intent-reservation transaction, keyed by intent ID. Keep it out of `OrderIntent.RequestJson`, which is compared byte-for-byte and participates in candidate evidence hashes. Retry and recovery cannot replace that first observation. Link it to the actual local pre-POST stamp in `RecordDispatchAttemptAsync`, retaining attempt/intent/client/run/account and exact strategy identity.

Label this **Admission quote at dispatch, observed X earlier**. Show provider quote age and observation-to-attempt delay; flag age beyond the existing configured freshness threshold without adding a new gate. It is not proof of the market quote at POST or of globally latest data. No additional quote request or subscription is introduced, and the display-only `LiveQuoteBook` is not used. Adoption/reconciliation must not manufacture a POST attempt. A durable pre-POST stamp is distinct from proof that the request reached the broker. Exit/protection paths without this admission observation remain explicitly unavailable for that comparison. A genuine at-send market quote remains an unverified part of literal B6 acceptance until a separately approved measurement design supplies it.

Keep decision reference, optional modelled executable price, limit price, admission quote, recorded local pre-POST attempt time and unit fills separate. A daily decision close is not a modelled next-open price. Missing baselines stay unavailable. Use broker execution IDs and actual incremental quantities, not repeated cumulative filled quantities, to deduplicate fills.

Add a read-only comparison table on the existing paper run/order detail, and a monthly summary grouped by strategy ID, version/hash, run/batch and New York fill month. Show side-adjusted adverse basis points with the exact reference kind (admission bid/ask, modelled price or decision price), fill-quantity-weighted price, decision-to-send/fill delay, counts and coverage. Different reference kinds never form one combined series. Show unfilled/rejected/cancelled/missing-evidence cases rather than dropping them. A partial order spanning two months contributes only its fills in each month. Do not pool different strategies or report accumulated economic return.

Tests: delayed entries, partial fills across months, retries/adoption/duplicates, missing/stale quote clocks, manual/protective orders without a strategy reference, and unchanged broker commands and exit/protection handling. Telemetry failure is visible and must not disable an independently authorized protective exit. A real matched fill remains unverified until a separately approved regular-session paper operation; building the table does not authorize that operation or prove live-market execution quality.

## NEXT-4: record and show research attempts before running them

Reuse `SqliteResearchTrialRegistry`, its canonical/idempotent registrations, `CatalogResearchWorkflow`, existing saved-plan workflow and signal-study analyzer. The registry exists, but generic saved-plan starts and `SignalStudyCommand` do not consistently register trials; the current signal study writes its manifest after producing results.

Freeze the hypothesis/family, intended variants, exact data identities and partitions, price basis, time horizon, cost assumptions, success/rejection rules and code/config hashes before evaluation. Bind a start request idempotently to its intended trials; group jobs sharing one frozen trial must not inflate its count, while independently chosen variants must not disappear into one nominal attempt. Preserve failed/cancelled attempts and result references. Concurrent starts/restarts cannot change family ordinals or consume a holdout twice.

The registry and queue are separate databases. Register the canonical trial and durable start binding before enqueue, with pending/admitted/refused/interrupted states and deterministic retry recovery; do not claim a cross-store atomic transaction. A worker verifies its exact durable trial binding before computation. Retries reconcile the same ordinal and intended jobs. Cover `BacktestPlanService.StartAsync`, its durable worker, `SignalStudyCommand`, `CatalogResearchWorkflow`, direct CLI `BacktestRunner.RunAsync` and optimization routes.

For a genuinely new portfolio idea, require the exact reviewed successful frozen signal-study evidence before the first portfolio experiment. A frozen diagnostic rule definition is allowed for the signal study; it does not require publishing a new catalog/promotable strategy version first. An existing diagnostic replay requires an exact original plan/artifact binding or a reviewed replay allowlist; a caller cannot bypass the new-family gate by selecting a diagnostic label. Replays remain clearly labelled diagnostic, without fabricated historical trial numbers or automatic promotion. Unsupported/missing evidence cannot be bypassed by changing the request route.

Add one read-only Research ledger page over verified trial/result records, links to existing retirement records/documents and the approved/draft success-policy status. Show family, hypothesis, try count/coverage, outcome, exact evidence and whether the protected partition is unopened. Unknown past trial counts remain unknown; do not parse historical prose into invented registrations. This page must not load holdout observations or launch jobs.

Tests: first registration before computation, retry/concurrency, failed and cancelled jobs, exact manifest identity, cross-family/study mismatch, grouped-plan counting, every current entry route, diagnostic replay labels and unopened holdout. Browser checks cover ledger navigation and honest missing-evidence states. No economic experiment is started merely to demonstrate this UI.

## Decisions and evidence kept separate from software approval

- **C3/A3:** the user must approve a research success policy and benchmark basis before a new economic test. Correct the factual draft first. A dividend-inclusive SPY series requires verified corporate-action coverage and comparable dividend accounting in the strategy; adding dividends only to the benchmark is not parity. Capital, income targets and acceptable personal risk are not inferred from software defaults.
- **E3:** preserve current screens. Propose new named screens from a frozen approved research rule; do not silently replace the user's queries or call a filter profitable.
- **E4:** freeze actual-receipt cohorts, first eligible executable open and 1/5/20-session horizons, same-basis SPY, split/delist/missing evidence and overlap handling. A 20-session outcome cannot exist before it matures. The analysis design and run manifest need approval after capture is working.
- **ORB:** the current signal-study command is daily and constrained to 2017–2023; it cannot silently run an intraday 2026 experiment. The reviewed study must freeze the exact eligible pool and period, top-20 first-five-minute relative-volume selection and prior-session baseline, as-traded/split-as-of prices, 0/5/10 bps plus commissions, executable order/stop ordering, short-borrow limitations and a statistical meaning of “clearly above zero at 5 bps.” Inventory existing evidence before estimating downloads. No ETF holdout or new portfolio variant is opened; earlier observed October results remain diagnostic.
- **Paper:** market-open acceptance needs an exact proposed symbol/account, quantity, maximum risk, entry/protection/exit sequence and authorization before any broker write. A successful paper test proves operations, not profitability.
- **Chart follow-up:** forming/weekly candles, private trade markers, trade-history replay and advanced volume studies need their own reviewed data/permission design. The present common chart continues to show completed evidence with its true availability.

## Execution, reviews and completion records

Other unresolved policy/UI follow-ups stay explicit: late-entry window policy, historical backtest loss-limit parity, broker-supported linked-stop handoff/RSK-02, strategy-specific chart levels and timestamps, and any remaining section 7 navigation/view merges or embedded News chart choices. Where prior approved scope did not cover them, they need concrete designs. They are not hidden by closing the software checkpoints below.

User approval received for the NEXT-1–NEXT-4 software checkpoints as described, including the observational local Finviz schedule after its acceptance check. It excludes NEXT-5 investment-policy adoption, new NEXT-6 economic studies, NEXT-7 broker writes and later chart implementation. Prepare those concrete proposals while independent software work proceeds.

Use isolated checkouts, narrow Sol 6.1 High implementation assignments, one heavyweight build/test job at a time, and separate A plan/execution and B code/design reviewers who do not implement. Each checkpoint records exact tests, measured evidence, local commit and remaining external acceptance in the existing tracking plan. Preserve the user-owned Fable audit, secrets, operator data, original results and all provider receipts. Do not sync GitHub.

The staged scope is several components and persistence/contract changes, not one quick patch. First deliver NEXT-1 and its real capture; then NEXT-2/NEXT-3 and NEXT-4 in reviewed commits. Neither a green unit suite nor the new screens close items requiring future market sessions or human decisions.

## Independent proposal review and approval rule

Separate reviewer A accepted plan/execution coverage; separate reviewer B accepted code/design, after the capture coverage/claim clocks, evidence format, separate intent telemetry, and cross-database trial binding corrections. Neither reviewer implemented these proposed changes. Their acceptance permits presentation; it does not stand in for the user's design approval.

The governing source is `C:/project/trading_flow/AGENTS.md`, section **User Design Approval Required**: “Before adding anything to the implementation, present the concrete design to the user and obtain explicit approval.” It also says broad “finish everything” requests do not approve an unreviewed design. This document supplies the concrete NEXT-1–NEXT-4 design for that approval. Previously approved implementation and read-only investigation continue independently.
