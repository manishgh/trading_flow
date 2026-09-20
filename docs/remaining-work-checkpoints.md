# Remaining Implementation Checkpoints

Updated: 2026-09-20. This is the standalone execution checklist for the remaining
workflow checkpoints. It does not replace the production specification or reopen
completed phases without a defect.

## Bounded Swing Product Cleanup (2026-09-20)

Status: implementation and verification complete. Independent plan and final-diff
reviews found no remaining blocking issue. This closes day-trading retirement, not
the unrelated workflow checkpoints below or strategy profitability/promotion.

- Preservation checkpoints pushed before cleanup: TradingFlow `9d50bc1` and Market
  Predictor `18e07d1` on their respective main branches. Both implementation branches
  are named `unified-swing-product`. No project runtime was active before edits.
- Scope: TradingFlow design specifications and four mixed HTML prototypes,
  obsolete operational-document references, and new swing-focused regression tests.
- Remove dedicated day-trading screens, strategy examples, predictor fields and
  defaults; retain usable swing views and completed sub-daily execution evidence.
- Restore committed-bars/point-in-time-membership momentum tests and signal
  log-trend coverage; add retired strategy-key rejection tests where absent.
- Runtime changes are limited to swing-only risk reservation and durable entry
  dispatch admission. Canonical strategies, providers, jobs, secrets, local DB state,
  raw/model/candle artifacts and curated research results remain unchanged.
  No compatibility aliases or strategy promotion.
- Gates: no active dedicated intraday workflow in the edited designs/docs;
  prototype script syntax and swing-only state references checked; new tests match
  current contracts; offline suite, independent review and `git diff --check` pass.
- Rollback boundary: this checkpoint's code/design/doc/test diff only, never
  preserved history or artifacts.
- Runtime gate: reject new
  day-horizon reservations; before broker POST, dispatch checks the authoritative
  portfolio-risk horizon and expires unsupported or unavailable entry reservations.
  Broker adoption, reconciliation, exits and protection continue. Prior ambiguous
  broker attempts remain adoption-required; they are never treated as unsubmitted.
  Changes are in `SqliteOrderIntentRepository.cs`, `IOrderIntentRepository.cs`,
  `OrderDispatchService.cs` and `SqliteDurabilityTests.cs`.

Implementation evidence:

- Removed retired strategy/universe/position/order fixtures from the four mixed
  prototypes, including their defaults, filters and orphaned lookup entries. Kept
  the existing swing return series and remaining swing examples; no intraday
  results were renamed as swing. Deleted the retired failed-breakout trade example
  rather than changing its exit reason. Promotion gates now say not evaluated,
  and catalog disposition does not imply execution authorization.
- Updated `design/api-gaps.md`, `design/screens/01-trading-desk.md`,
  `design/screens/04-operations.md`, `docs/architecture-and-review.md` and
  `docs/wishlist-architecture-plan.md` to remove obsolete workflow requirements.
  Sub-daily swing evidence, within-session drawdown and PDT safeguards remain.
- Added `SwingEvidenceCatalogResearchRunnerTests.cs` (committed adjusted bars and
  point-in-time membership, including rejection of late membership),
  `SwingSignalLogTrendTests.cs` (positive daily log trends and no future-bar reads),
  and `RetiredStrategyConfigurationTests.cs` (48 rejected retired keys and three
  retained sub-daily execution timeframes). All 55 cases passed in the full suite.
- `node --test tests/design/swing-prototypes.test.cjs`: 4 passed, 0 failed.
  Checks script syntax, component projections, surviving fixture identities and
  return values, tab/selection paths, fixed swing screening and not-evaluated gates.
  This is a lightweight component-state check, not a browser visual-verification pass.
- `git diff --check`: passed. Reference scan found no dedicated intraday workflow
  in `design`; remaining day-trading mentions in operational docs are exclusions
  or same-day exit/PDT protections, not active strategy paths.
- Runtime-focused suite: 97 passed. Full offline C# suite, built after the final
  code/test edits: 1,546 passed, zero failed, in 1m54s, serial execution.
  Command: `dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore
  --filter "FullyQualifiedName!~AlpacaCandlePipelineIntegrationTests" --verbosity quiet
  --logger "trx;LogFileName=swing-retirement.trx" --results-directory
  .test-tmp/swing-retirement -m:1 --disable-build-servers --
  xUnit.MaxParallelThreads=1 xUnit.ParallelizeTestCollections=false`.
  Local report: `.test-tmp/swing-retirement/swing-retirement.trx` (not committed).
  The live Alpaca test was excluded; no provider/broker request or training ran.
- Two scoped agents completed design/implementation and independent code/design
  review and are closed. Sampled system memory stayed around 72-73% during tests.
  No artifact deletion or implicit authorization migration occurred.

## Earlier Evidence Baseline

The following records the earlier checkpoint baseline, not the preservation state
reported in the bounded cleanup above.

- Phases 0-5 are recorded as complete in the lifecycle plan. Their regression
  evidence must remain intact; this planning review is not a fresh full-suite pass.
- Phase 6 is partial. Shared contracts, candidate query service, catalog/candidate
  read endpoints, durable-job repository, and migrations exist. Durable backtest,
  optimization, and paper scheduling is complete; universe/candidate work remains.
- Backtest, paper, and optimization queues are SQLite-authoritative. In-memory
  dictionaries are bounded projections and no detached `Task.Run` scheduling remains.
- Candidate audit currently omits market, catalyst, and execution evidence. Its
  maximum candidate version is not a valid run-wide replay cursor. Consumed does
  not prove a filled position and must not be labeled in_trade on that basis.
- Phase 7 has partial row-update improvements. Complete contract migration and
  notification navigation are not verified.
- The previous session recorded a solution build, 10 focused contract/repository
  tests and two focused list-update browser tests passing. These are not phase gates.
- Backtest audits are now run-scoped and externalized after completion, but the
  run still uses an effectively unbounded collector and materializes full arrays.
  Bounded full-retention writing is unfinished.
- Work is uncommitted. No commit or push is implied by this checklist.

## Independent Review Findings

Two read-only reviewers independently inspected implementation and verification
coverage on 2026-09-07. Both confirmed phases 6-8 remain open. Findings are retained
here; both agents were closed after review.

- Checkpoint 1 must prove both audit completeness and bounded peak memory; writing
  files only after collecting every event does not satisfy the memory requirement.
- Checkpoint 2 must persist cancelling before shutdown and mark cancelled only
  after execution drains. The current paper cancellation path reports completion
  too early. Queue capacity must bound waiting jobs, not only executing workers.
- Checkpoints 3 and 5 must derive exposure from durable orders/positions and use a
  run-wide event sequence. Candidate consumption and maximum candidate version
  are not substitutes for these facts.
- Checkpoint 4 must bind previews to candidate version, strategy/run identity and
  risk state. Include common order cancel/close routes in the API route matrix.
- Checkpoints 6-8 need actual application-projection and hosted endpoint tests,
  not just serialization fixtures populated manually with hypothetical evidence.

## Execution Order

Checkpoint 1 completed on 2026-09-15. Streaming atomic artifact publication and
separate decision, hypothesis, portfolio-execution, and completed-trade evidence are
implemented. Candidate state uses per-worker SQLite indexes and chronological streaming
export instead of unbounded retained dictionaries. The last-written manifest verifies
five required artifacts and reconciles execution fills, fees, terminal failures, work
coverage, and every published economic claim. Explicit finite limits cover retained
bars, replay state, strategy/ticker work, candidates, candidate history, and order
contracts. Focused verification passed 105 tests; the full deterministic local suite
passed 1,543 tests. The isolated 10,000-event drill retained one preview event with
394,408 bytes measured managed growth and 2,118,890 bytes on disk.
See [the incremental verification record](verification/audit-streaming-checkpoint.md).
Independent plan and code/design reviews have no unresolved critical or high finding.

| Checkpoint | Scope and concrete output | Required acceptance evidence | Status |
| --- | --- | --- | --- |
| 1. Resource and audit integrity | Bounded audit writer with backpressure, full durable retention, lightweight result references, bounded rolling diagnostic logs. Stop treating speculative candidate simulation as confirmed portfolio fills. | Repeated and concurrent runs have isolated evidence; large replay does not retain events proportional to total history; writer failure/cancellation is explicit; artifact counts/checksums reconcile with accepted execution. Record measured memory/disk behavior and chosen limits. | Complete |
| 2. Durable run execution | Move backtest, paper and optimization scheduling into application services with durable requests/snapshots, bounded workers, fenced leases, heartbeat and cancellation. Recovery policy must distinguish replayable backtests from paper runs with broker exposure. | Two workers claim once; expired owner cannot publish; cancellation survives restart; shutdown drains; restart reconciles paper orders before continuing; no duplicate result publication or broker submission. | Complete |
| 3. Universe and candidate workflow | Persist immutable previews with source observations/expiry and exact strategy identities. Implement preview and candidate-start APIs; carry snapshot identity into runs. Complete candidate, gate, market, news and execution audit projections. | Expired/tampered source or strategy rejected; no future observation accepted; duplicate start idempotent; pending/rejected orders never imply an open position; one API fixture matches durable source records, including null/absent evidence. | Partial reads only |
| 4. Order confirmation and access | Persist preview identity and confirmation outcome; revalidate candidate, quote, strategy, risk and expiry. Centralize application orchestration. Apply existing authentication, mutation policies, operator-override authorization and browser anti-forgery protection. | WebApplicationFactory covers valid/invalid input, anonymous/forbidden access, CSRF, Android cookie lifecycle, stale previews and concurrent confirmation; retry returns the same command outcome. | Pending |
| 5. Resumable updates | Durable monotonically ordered event cursors, bounded SSE delivery, heartbeat, disconnect cleanup and polling fallback on the same application projection. | Reconnect after missed events recovers exact state; slow client cannot grow queues indefinitely; restart preserves ordering; expired cursor causes explicit resynchronization; no subscription leaks. | Pending |
| 6. Web integration | Wire Backtest, Paper, Trade Desk and catalog to application APIs. Compact state/evidence views, cancellation, preserved form/focus/scroll, working news and audit links. Remove redundant PageModel orchestration after consumer migration. | Playwright at 1440x900 and 412x915: routes/actions/links, loading/error/empty states, dynamic rows, cancellation and reconnect; inspect screenshots; no full-page refresh for live updates. | Partial list updates |
| 7. Android integration | Consume shared versioned contracts for catalog/desk/paper/automation/backtests. Deep-link alerts to the exact candidate and attached news; maintain compact cards and details. | Contract/view-model tests match Web states and reasons; Release Android build; emulator or connected-device navigation, authentication/reconnect and notification checks. Label physical-device-only evidence explicitly. | Pending integration proof |
| 8. Final integration and closure | Deterministic discovery-to-decision-to-fill-to-exit-to-audit-to-API/UI replay; provider paper smoke after prior gates; reference-checked cleanup; update architecture, README, API/mobile docs and verification ledger. | Solution/full appropriate suite, browser and Android checks; cancellation/restart/ticker-isolation drills; paper smoke evidence; both reviewers report no unresolved critical/high findings. | Pending |

Checkpoints run in order. Checkpoint 4 and 5 may be developed independently after
the checkpoint 3 contracts settle. Clients migrate only after their API acceptance
fixtures pass. No strategy parameter tuning or automatic promotion is part of this work.

## Astra Research And ML Handoff Gate

The preferred model handoff for strategy/backtest-flow improvement and ML integration
is after checkpoint 3 closes. At that boundary, artifact integrity, durable run
execution, point-in-time universe membership, candidate identity, feature/news
lineage and audit projections are stable enough to produce trustworthy labels.
The complete continuation brief is
[Astra Swing Research Handoff](astra-swing-research-handoff.md).

- After checkpoint 1, Astra may perform a read-only backtest architecture critique.
- After checkpoint 3, Astra may design and implement the research/ML integration
  against frozen versioned contracts and a held-out evaluation protocol.
- No ML output may bypass the shared candidate, risk, execution or audit brain.
- Model suggestions are research hypotheses, not automatic strategy promotion.
- Training, validation and holdout windows must remain point-in-time and must record
  data/model/config versions so reruns are reproducible.

## Review and Resource Protocol

For every checkpoint:

1. Record the intended behavior, affected files and counterexample tests.
2. Have a plan/requirements reviewer check traceability and acceptance criteria.
3. Implement and run focused tests; retain concise evidence and exact commands.
4. Have a separate code/design reviewer inspect the actual diff and test evidence.
5. Resolve critical/high findings, verify affected paths, update this ledger.
6. Close each review agent immediately after recording its findings. Shut down
   task-owned test hosts, browser runners and build workers when their work ends.

Use non-forked agents with file-scoped prompts; never copy the full conversation.
Keep at most two reviewers active and run heavyweight builds/replays one at a time.
Closing an agent releases its runtime but does not delete its transcript. Transcript
deletion remains separately scoped to approved exact closed-task files.

Stop project runtimes before code changes. At final verification, start Web/ngrok
only for the checks, then gracefully stop task-owned services per the user's latest
resource instruction. Do not terminate unrelated processes. If broker exposure is
present, use the existing protection/shutdown coordinator and verify its outcome
before considering forced termination. Retain PID ownership and verify process exit.
Leave a service running only when explicitly requested for ongoing use.

## External Evidence

Multi-session paper fill/impact calibration, complete point-in-time universe and
news lineage, and human catalyst labels remain evidence dependencies. Missing
evidence stays visible and blocks promotion; deterministic tests cannot replace it.
Physical accessibility checks require a device. None of these prevents finishing
the application implementation or its automated verification.
