# TradingFlow audit repair plan and execution record

Date: 14 November 2026 (Europe/Berlin)
Owner: Codex, implementing the user's authorized reporting and paper lifecycle repairs.
Baseline: `2cd528e`; clean working tree before this task. No TradingFlow runtime process was active at the initial check.
Source: [Fable audit](../research/tradingflow-audit-2026-11-14.md).

## Current status

**Current local-app checkpoint complete and independently reviewed (15 November).** Reviewed implementation is committed locally as ad46d2c and 91dd9e1. The main app at http://127.0.0.1:10088 runs build 91dd9e1 with trading paused and market feeds active against the actual V: database, now at migration 29. Final test coverage is 3,954 passing cases and four deliberately unrun profiling opt-ins, with no unresolved executed failure. Saved lists, journals and original earnings analysis were preserved; 729 headlines removed by the previous 72-hour expiry were recovered exactly from the initial backup. Actual signed-in Chart, News and Earnings checks passed. The duplicate preview is stopped. No GitHub sync or broker write occurred. Regular-session broker fill/protection/exit acceptance remains pending. The requested how-to guide and concrete UI/optional-indicator proposal are now complete and independently reviewed; implementation of the new proposal awaits the required design decision.

**Paper rollout repair checkpoint reviewed (15 November).** Ticker/source health, exit-only continuation across different labelled builds, and administrator-reviewed loss reset/rebaseline are implemented and independently accepted. Combined paper checks: 268 passed. Both saved-plan-192 real-data replays match original fills, trades and complete daily cash/equity ledgers; original files remain unchanged. The subsequent local-app checkpoint above applied migration 29 and verified the actual operator database. All five requested Fable chart findings are addressed, including final real-data acceptance. The weekend overlay-session defect and mobile numeric wrapping discovered on real data were fixed, independently reviewed and retested.


**First shared chart checkpoint complete and independently reviewed (15 November).** Responsive candles/volume, on-demand shared service and stock links, confirmed current support/resistance with intraday/week/month/year context, supported overlays and stored event markers are implemented locally. Final checks: 129 targeted C# tests, 5 chart browser tests and 8 existing UI regression tests passed. Separate A/B reviewers signed off execution and code/design. Membership skips now leave decision-audit reasons. Forming previews, weekly candles, private trade markers and historical zone replay remain staged. Astra optional-indicator research is complete; additional indicators are not activated. These changes are now included in the reviewed local commits and paused app above. See the latest operational record for the remaining regular-session broker checks.

**Previous follow-up reviewed (15 November).** A5 cash checks and core B7 paper loss controls are implemented, tested and accepted by both independent reviewers. Linked-order cancellation remains partial (RSK-02). At that earlier checkpoint the five-layer chart design was reviewed; its first UI checkpoint is now complete as recorded below. Broad checks: 482 passed; final risk rerun: 296 passed, with zero failures/skips. This follow-up is now included in the later local commits above. The original checkpoint was committed as `6c1e74b`.

**Complete for the authorized repair checkpoint.** Reporting R1-R3, paper lifecycle L1-L4, and reversible strategy availability S1 are implemented and accepted by separate plan/execution and code/design reviewers. Final verification: 555 passed, 0 failed, 0 skipped; Android build passed with zero warnings/errors. Sector rotation and monthly execution remain deferred by the user. The later Fable follow-up reopens B3 failure isolation; this historical checkpoint is not a claim that every audit item is resolved.

The user authorized implementation of verified reporting and paper lifecycle fixes with tests and independent review. Monthly paper execution must have its design concerns resolved before implementation. This is not authorization to activate a strategy, submit orders, open the research holdout, or change investment success thresholds.

Status vocabulary: planned -> reviewing -> implementing -> verifying -> complete. Use blocked only with an exact reason and next action. A checkpoint is complete only after its acceptance checks and both separate reviews pass.

## Tracking board

| ID | Work and audit reference | Status | Acceptance evidence |
|---|---|---|---|
| P0 | Record scope, inspect current code, preserve state and assign two reviewers | complete | Clean baseline; no active runtime; separate plan and code/design reviews completed and requirements incorporated below |
| R1 | Benchmark uses the declared test window, excluding warm-up (A2/F2) | complete | Counterexample with large warm-up move; end boundary; unavailable window; explicit benchmark price/time basis |
| R2 | Published daily entry/exit times describe executable session times (A1/F1) | complete | Next-session open, DST/weekend/holiday and sub-daily cases; unchanged fills, fees, cash, protection and decision availability |
| R3 | Result bias/provenance describes the actual universe and price inputs (A4/F4) | complete | Research screen identity and raw catalog policy survive packaging; wishlist/provider paths retain accurate labels |
| L1 | Paper candidate lifetime follows its decision opportunity, separately from preview freshness (B1) | complete | Evaluation after 15 minutes reaches decision; stale/removed source and expired preview remain refused; session-boundary tests |
| L2 | Detect incompatible stored run provenance once before repeated evaluation (B2) | complete | Changed build/config yields one explicit failed/stopped job; identity is never silently rewritten; identical restart allowed |
| L3 | Treat pipeline failures as run health failures, distinct from strategy rejection (B3) | complete | Durable failure visibility and bounded consecutive-failure entry stop; valid strategy rejection is distinct; exits/protection remain available |
| L4 | Close production run rows on shutdown and recover abandoned rows safely (B4) | complete | Normal stop, crash/restart, cancellation and active-owner isolation; existing order history retained |
| D1 | Resolve monthly paper execution design, without enabling it | deferred by user | Separate design review covers funding, partial fills, order rejection, restart/idempotency and backtest comparison |
| S1 | Reversible strategy disable control; park sector rotation for later research | complete | Catalog flag/reason, selection and queued-start refusal, immutable identity/history retained, re-enable does not promote |
| C1 | Final verification and handoff | complete | Focused build/tests, affected safety/replay tests, both reviewer outcomes, exact remaining limitations and cleanup recorded |

## Smallest implementation approach

### Reporting (R1-R3)

Reuse `BacktestValidator`, existing run configuration/calendar, the chronological coordinator's publication boundary and result packaging. Filter benchmark observations to a declared comparison interval and explicitly document whether prices are opening or closing prices. Do not quietly align the benchmark to a strategy's first profitable entry or use warm-up returns. Missing observations remain unavailable rather than fabricated.

Trace economic fill time separately from event/bar availability before modifying trade publication. Internal timestamps are used by stop protection and the daily ledger, so a display fix must not shift those clocks or change fills. Determine actual provenance from resolved research/provider inputs, rather than copying a stale profile label. Preserve published historical results; fixes apply to new output.

Primary files: `src/TradingFlow.Backtesting/BacktestValidator.cs`, `ChronologicalBacktestExecutionCoordinator.cs`, `BacktestRunner.cs` and direct packaging/configuration consumers. Focused tests under `src/TradingFlow.Tests`.

### Paper lifecycle (L1-L4)

Reuse existing discovery sessions, source snapshots, shared decision kernel, durable job leases, production-run repository and Paper job projection. Keep three-minute interactive preview freshness. A paper run must refresh or validate discovery evidence under an explicit session policy; extending a candidate lifetime is not permission to use stale evidence.

Validate run identity at job start before the ticker loop. A mismatch must fail clearly without overwriting history or retrying every ticker forever. Pipeline errors must surface through durable job/run health, distinct from ordinary strategy rejection. Use existing failure/terminal state machinery where possible; specify any threshold and reset behavior before implementing it. Stop new entries without disabling protection, reconciliation or exit handling.

Close only rows owned by the stopping host/job or proven abandoned by existing ownership/lease evidence. Never mark every `running` row stopped merely because another host starts. Reuse existing schema and UI status where sufficient; any necessary additive field must have migration and consumer tests.

Primary areas: `LiveRunner*`, `PaperJobService`, paper discovery services, `ProductionRunPersistence`, production-run registration/lifecycle and affected repositories. Tests use isolated synthetic storage, never operator data.

### Monthly design (D1)

Update [the monthly proposal](../design-review/2026-11-14/etf-rotation-paper-design.md) only after inspecting existing broker/intent/reconciliation capabilities and current official broker behavior where needed. Resolve:

1. Whether purchases at the opening auction can be funded independently of unfilled same-open sales; submission order is not fill order.
2. Quantity reservation, gap risk, partial fills and rejection; no reliance on an arbitrary cash cushion as a guarantee.
3. Durable per-decision/per-order identity, ambiguous broker submission recovery and duplicate prevention.
4. What happens after a missed cutoff, restart, stale data or incomplete rebalance.
5. Which comparisons prove signal/target parity and which require an execution simulation matching actual broker timing.

Record the selected approach and tradeoffs. Do not remove current monthly strategy refusal or grant trading authority while design questions remain. Monthly implementation is a later checkpoint, after the reviewed design is concrete.

## Review protocol (user instruction)

Two separate independent reviewers are required at every implementation checkpoint:

- **Reviewer A: plan and execution.** Review scope, requirements, acceptance tests and whether the final implementation and evidence satisfy the plan. This reviewer does not implement the work.
- **Reviewer B: code and design.** Independently inspect the actual design, diff, edge cases, module boundaries, failure handling and test quality. This is a different agent from Reviewer A and does not implement the work.

Address actionable findings and rerun affected checks before completion. Keep each prompt focused and non-forked, at most two active reviewers, and record their conclusions. Implementation assistance is separate from both reviewer roles.

## Verification and preservation

Component checkpoint, not release or strategy promotion. Run heavyweight builds/tests one at a time. Build affected C# projects with compiler/nullable/analyzer checks; run focused tests plus affected integration, persistence, causality, execution safety and deterministic replay cases. Record commands, counts and deliberately unrun checks. Broaden only when findings show additional impact.

No operator DB writes, broker orders, provider downloads, research runs, strategy grants, published-result rewrites, GitHub sync or automatic data cleanup. Preserve secrets, datasets, manifests, held-out evidence and research history. Track owned temporary processes/files and clean only proven disposable task artifacts after preserving review context.

## Execution log

| Date | Step | Result / next action |
|---|---|---|
| 2026-11-14 | Initial inspection | Baseline clean; audit/code comparison confirmed benchmark warm-up inclusion, fixed discovery expiry and provenance mismatch behavior; runtime process check returned none |
| 2026-11-14 | Tracking plan | Created this document; independent plan review pending |

## Reviewer findings

| Reviewer | Checkpoint | Finding | Resolution / evidence |
|---|---|---|---|
| A: plan_review | P0 | Monthly benchmark, economic versus processing clocks, fresh membership, deterministic failure policy, recovery-first provenance, host ownership | Accepted; detailed criteria below. Final execution review remains pending. |
| B: design_review | P0/D1 | Monthly funding/cutoff/idempotency and stopless-strategy conflict; preserve timestamps and owner fences | Accepted; monthly proposal revised. Final code/design review remains pending. |

## Verification history (see final evidence below)

First coordinated build: `dotnet build src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore -m:1 --disable-build-servers --verbosity quiet`. Result: failed after 41 seconds with one CS0136 local-variable shadowing error in PaperJobService.cs:540, zero warnings. Assigned to the paper implementer for correction. Focused tests have not run yet. Independent code/design review of the implementation is in progress.

## Handoff and cleanup

Completed within the authorized scope. No research/data mutations, strategy activation, operator DB migration, GitHub sync or commits performed. Retained the three task-owned TRX reports as review evidence; no files deleted and 0 bytes freed. Shared build outputs were retained because ownership is mixed. No task-owned server or worker was started; all owned build/test processes exited. An unrelated tradingflow-site process was observed and left untouched.

## Accepted initial review requirements

- R1: publish monthly price-only benchmark figures alongside strategy months, matching New York periods and partial months. Preserve the corrected aggregate internally. Explicit boundary basis, actual compared timestamps and unavailable reason are required; missing boundary evidence cannot quietly shorten a month.
- R2: keep simulation processing timestamps and accounting partitions unchanged. Add explicit modeled execution time, source session and basis; preserve actual bar availability separately. All public trade formats must clearly distinguish economic fill from legacy receipt timestamps. Intrabar touches without an observed instant remain unknown. Test February 2017 regular open at 14:30 UTC, DST/early close, delayed KnownAtUtc, month boundary and legacy artifacts.
- R3: actual resolved membership/provider evidence controls reporting labels; cover prepared/shared inputs and unknown adjustment policy. A research screen label must not imply that missing delisting/identity evidence is complete or promote a strategy.
- L1: revalidate discovery after warmup; independently enforce the setup execution deadline and fresh source membership. A removed or expired source ticker retained solely for an existing position may exit but cannot open a new position through fallback configured discovery.
- L2: durable recovery runs before execution, so identity preflight covers both paths. Mismatch is one durable run/job failure, not a per-ticker rejection. Preserve reconciliation/protection even when broker recovery is unavailable. Compare all immutable fields and fence terminal writes by ownership.
- L3: the selected failure unit is a completed poll cycle containing a pipeline failure. Three consecutive failed completed cycles latch new entries off; ordinary strategy rejections are not failures. A successful nonempty cycle resets the consecutive count before latching; cancellation/empty cycles do not fabricate recovery. Persist health and latch so restart cannot bypass it. Test actual dispatch gate, concurrent ticker order invariance, first/latest failure, counters and continuing exits/protection.
- L4: host reconciliation rows have identities separate from paper jobs. Add explicit expiring host ownership where needed; close only the current host or a proven expired owner. Legacy untagged rows remain visible with unknown ownership, not indiscriminately rewritten. Test active peer, manual, paper-job and stale-worker isolation.

## Monthly design review (historical; now deferred by user)

The independent code/design reviewer rejected the old monthly proposal. Its no-stop/no-target strategy conflicts with the current common entry gate and the binding protection policy. Existing broker adapters also reject opening-auction time-in-force. Funding and restart requirements can be specified, but those facts cannot be fixed by silently removing safety checks.

The revised monthly design documents two execution approaches and the policy decision needed. Reporting and lifecycle repairs continue independently. No monthly driver or strategy authorization is being added in this checkpoint.

### D1 follow-up review

Reviewer B accepted the revised design document with two clarifications, now applied: the executable monthly decision key excludes mutable input fingerprints; monthly submission deadlines cover both buys and sells, with terminal reconciliation of remainders. Monthly activation was initially pending a policy decision. The user subsequently directed that rotation remain disabled for later separate research; S1 below supersedes that question.

## User clarification: horizons and exits

The user clarified that intraday and swing trades require loss protection, and that a swing exit may be a break of support rather than an arbitrary fixed percentage. Monthly rotation is a separate longer-term allocation idea whose holdings may last months. This clarification does not authorize adding guessed support rules or activating the existing stopless monthly artifact.

Future designs must specify the support measurement using completed information, touch-versus-close trigger, execution timing, gap handling and broker protection behavior. Do not force identical profit-target or stop templates onto all horizons. Current repair scope preserves the existing order gates; a separate reviewed monthly execution policy is still needed before a new path can run.

### Research-source clarification discovered while answering the user

The original rotation design's assertion that all exact values are a published recipe is too strong. Faber (2010) tests multiple ranking horizons and their combinations; Antonacci's published absolute momentum uses return relative to Treasury bills, and his FAQ says he does not omit the most recent month for indices. TradingFlow's 12-1 sector ranking plus per-fund 210-session average and no weight trimming is an adapted research combination, not an exact replication of Antonacci's GEM or a single verified source recipe. No existing result or frozen strategy is rewritten.

Sources read: [Faber original paper](https://mebfaber.com/wp-content/uploads/2018/12/SSRN-id1585517-Relative-Strength-Strategies-for-Investing.pdf), [Antonacci FAQ](https://www.optimalmomentum.com/faq/), [Schwab swing guidance](https://www.schwab.com/learn/story/swing-trading-strategies), [FINRA stop-order execution limits](https://www.finra.org/investors/insights/stop-orders-factors-consider-during-volatile-markets).
### Sector-rotation research discussion

The user sees monthly sector rotation as a potentially useful long-holding research area. The bounded question is whether rotation improves the outcome versus SPY on a consistent price/dividend/cash basis, with the static sector basket as a separate control. No new strategy variants, experiments, downloads or paper activation were started by this discussion. Keep original failed/retained results visible.

## Added scope S1: reversible strategy disable control

User instruction: keep sector rotation separate, do not use it now, and build a feature to disable a strategy for later research. This supersedes the pending monthly activation/protection question: monthly execution is deferred, not an unfinished implementation obligation in this checkpoint.

Concrete design: add an operational `enabled` flag (default true) and a reason to the strategy catalog, separate from research/archived disposition, exact canonical strategy content and authorization grants. Park `etf.rotation-12-1@1.0.0` specifically; preserve unrelated trend/control research artifacts and ORB. Keep its files, identity, settings and all results.

New research/backtest/experiment selections and queued starts must reject the disabled source, including generated snapshots derived from it. Historical checksum/identity reads and result views must remain possible without granting execution. A catalog edit does not stop an already-running strategy loop in the current process. After restart, a disabled paper job cannot resume its loop or automatic technical exits. Broker reconciliation, broker-native protection and manual exits remain available; this switch is not an exit-only mode for strategies with open positions. The parked rotation artifact has no paper authorization. Re-enabling restores only the existing research eligibility and never creates a paper/live grant. Expose the disabled reason in catalog presentation and document the configuration reload/restart boundary explicitly; do not claim instant cancellation of a running job.

Acceptance: default enabled compatibility; disabled canonical and derived/new snapshot refusal; saved/queued plan refusal; historical result readability; unchanged strategy content hash; reason visibility; re-enable still subject to original selection/lifecycle gates. Independent plan/execution and separate code/design reviews required. No operator-state mutations or strategy activation to verify it.
## Implementation review findings being resolved

Reviewer B's first code review requires three independent controls at the first broker-submission transaction: a persistent run health stop, a live uncancelled matching paper worker lease, and current trading-source eligibility/deadline. Stopping only the runner is insufficient because already-reserved work can be recovered separately. Adoption, reconciliation and risk-reducing exits remain allowed. Implementer is adding real SQLite/dispatcher regressions.

Reporting follow-ups require actual opening/closing price observation timestamps (separate from period bounds), explicit unresolved universe-history/issuer caveats, and additive timing evidence for current intraday outputs as well as daily outputs. Original economic/processing clocks remain unchanged.

S1 initial plan review by Reviewer A approved the scope with canonical, derived, experiment, queued, CLI and Desk admission coverage; read-only historical paths must remain available. Reviewer B separately endorsed operational availability metadata outside the canonical content hash and lifecycle. Implementation is now underway, final reviews and tests pending.
## Verification update: first component run

- Build attempt 2 found two compile errors in the new paper dispatch helper (namespace import and a local/query-name conflict). Both were corrected.
- The next coordinated `dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter <reporting/lifecycle suites> -m:1 --disable-build-servers --verbosity minimal --logger trx --results-directory .tmp/codex-audit-repairs-20261114-01a1dc0a -- xUnit.MaxParallelThreads=1 xUnit.ParallelizeTestCollections=false` compiled all affected projects successfully with no reported warnings.
- Result: **415 passed, 3 failed, 0 skipped; 418 total**, test duration 1 minute 27 seconds. TRX: `.tmp/codex-audit-repairs-20261114-01a1dc0a/reporting-lifecycle-1.trx`.
- All three failures were the new `RecoverPendingAsync_PaperEntryRequiresItsOwnUncancelledLease` cases: two fixture jobs shared a unique run name. The fixture now uses the run ID in the name. Those cases did not reach the production behavior under test and must be rerun.
- A new isolation regression also needs the next build: one ticker's market-state failure must not suppress another held ticker's technical exit, even when entries are already stopped.
- All selected reporting tests passed. Additional event monthly/artifact consumer tests, final lifecycle rerun, and S1 disable tests remain pending.

Selected suites: PriceOnlyBenchmarkTests, BacktestBiasRiskTests, ChronologicalBacktestExecutionCoordinatorTests, ResearchScreenHoldingRulesTests, ResearchScreenRunTests, EtfRotationTests, MonthlyRunResultWriterTests, MonthlyBacktestPresentationTests, BacktestJobServiceTests, IntradayPortfolioExecutionTests, EventPortfolioExecutionTests, LiveRunnerIntegrationTests, LivePipelineHealthTests, DiscoverySourceAdapterTests, DiscoveryRepositoryTests, PaperJobServiceTests, PaperJobRecoveryServiceTests, DurableJobRepositoryTests, DurableJobProcessorTests, DatabaseMigrationTests, ProductionCompositionTests, SqliteDurabilityTests, ExecutionShutdownCoordinatorTests, OrderSubmissionServiceTests, StrategyCandidateDecisionOrchestratorTests, WorkflowReplayTests.

Reviewer A is checking execution against the plan; Reviewer B is independently checking the revised code/design. No final signoff yet.
## Second independent review follow-up

Reviewer A completed provisional execution review and withheld final signoff pending the following fixes and a clean rerun. Reviewer B independently identified the same benchmark/provenance issues and a snapshot-persistence race.

- R1: withhold price-only benchmark returns when adjustment provenance is unknown; include benchmark information in the Mobile monthly view.
- R2: publish no exact time for protective fills without explicit simulator price-reference evidence. A gap-through fill remains conservatively unknown when only bar-range evidence exists. Verify forced early-close timing.
- L1: after warm-up, always ask the discovery session to refresh sources whose individual refresh interval is due, even while their longer membership TTL remains valid.
- L2: preflight immutable provenance before attempting recovery, then still attempt recovery/protection independently; a recovery exception must not hide the primary identity mismatch.
- L3: save a stopped health snapshot and its database dispatch latch atomically, including periodic snapshots and restart repair of a journal-only stop.

The implementation agents are resolving these findings. The two reviewers remain separate from implementation. No final signoff has been recorded yet.

Reviewer B final S1 follow-up: require availability for both a snapshot/registered artifact's exact known catalog identity and its declared source, so an enabled parent cannot bypass a disabled exact descendant. Regression requested. Documentation now states precisely that a parked paper strategy cannot resume its automatic strategy exits after restart; no generic exit-only resume behavior was added.

## Final verification progress

- Round 2 compiled the affected projects, including CLI and Web, successfully. Result: **551 passed, 3 failed, 0 skipped; 554 total**, 1 minute 38 seconds. The three new paper ownership fixtures then hit a second fixture issue: an unsupported order event source `test`. The fixture now uses the valid `engine` source and immutable intent payload and asserts the setup transition applied. Production validation was not weakened.
- Reviewer B's exact-disabled-identity finding is resolved: admission checks both an artifact's own known catalog identity and its declared source; an enabled parent cannot recreate a parked exact identity. A dedicated regression preserves historical reads while refusing new work.
- Reviewer B accepted the final code/design, conditional on a clean final test run. Reviewer A verified the round-2 TRX and acceptance coverage and is waiting for final evidence.
- Android: `dotnet build src/TradingFlow.Mobile/TradingFlow.Mobile.csproj --no-restore -m:1 --disable-build-servers --verbosity minimal` succeeded with **0 warnings, 0 errors**, elapsed 2 minutes 37 seconds. No emulator/device session was started.
- Final combined regression run is in progress, with the same affected suites plus the new exact-identity case. Its TRX is `reporting-lifecycle-disable-final.trx` in the owned verification folder.

The Contracts changes are additive reporting fields and a shared formatter consumed by Web and Android. No Python consumers of these monthly/trade contracts exist in this repository; Python sentiment sidecars were unchanged.
## Final acceptance and handoff

**All authorized implementation checkpoints are complete.** Reviewer A (`plan_review`) independently checked execution against the plan and read the final TRX directly; Reviewer B (`design_review`) separately reviewed design/code and signed off after the clean verification. Neither reviewer implemented these changes. All actionable findings are resolved, including exact disabled-identity admission, unknown benchmark basis, recovery mismatch masking, and atomic persisted entry stops.

### Final verification

- `dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter <affected suites below> -m:1 --disable-build-servers --verbosity minimal --logger "trx;LogFileName=reporting-lifecycle-disable-final.trx" --results-directory .tmp/codex-audit-repairs-20261114-01a1dc0a -- xUnit.MaxParallelThreads=1 xUnit.ParallelizeTestCollections=false`
- **555 passed, 0 failed, 0 skipped**, test duration **2 minutes 36 seconds**. The command compiled affected projects, including Web and CLI. Evidence: `.tmp/codex-audit-repairs-20261114-01a1dc0a/reporting-lifecycle-disable-final.trx`.
- Android build: **0 warnings, 0 errors**, **2 minutes 37 seconds**, command recorded above.
- `git -c core.whitespace=cr-at-eol diff --check`: passed.
- This is a component checkpoint. Full release suite, real-provider research/backtests, browser/device interactive checks, deployment and real-broker operations were deliberately not run. No holdout was opened and no operator database migration was applied.

The final filter joins `FullyQualifiedName~<suite>` for these suites with `|`: PriceOnlyBenchmarkTests, BacktestBiasRiskTests, ChronologicalBacktestExecutionCoordinatorTests, ResearchScreenHoldingRulesTests, ResearchScreenRunTests, EtfRotationTests, MonthlyRunResultWriterTests, MonthlyBacktestPresentationTests, MonthlyBenchmarkPresentationTests, BacktestJobServiceTests, IntradayPortfolioExecutionTests, EventPortfolioExecutionTests, EventMonthlyRunResultsTests, EventPortfolioSourceBindingTests, LiveRunnerIntegrationTests, LivePipelineHealthTests, DiscoverySourceAdapterTests, DiscoveryRepositoryTests, PaperJobServiceTests, PaperJobRecoveryServiceTests, DurableJobRepositoryTests, DurableJobProcessorTests, DatabaseMigrationTests, ProductionCompositionTests, SqliteDurabilityTests, ExecutionShutdownCoordinatorTests, OrderSubmissionServiceTests, StrategyCandidateDecisionOrchestratorTests, WorkflowReplayTests, StrategyAvailabilityTests, ConfigCatalogServiceTests, StrategyArtifactCatalogTests, StrategyExperimentArtifactStoreTests, SqliteStrategyPromotionRegistryTests, KeptStrategyIdentityTests, OpeningRangeWorkflowIntegrationTests, OptimizationJobServiceTests, CurrentStockStrategyAssessmentTests, StockStrategyAssessmentTests.

### Strategy availability use and limits

`configs/strategy-catalog.json` now sets `enabled: false` and a reason only for `etf.rotation-12-1@1.0.0`. Omitted `enabled` means true. A disabled entry requires a nonblank reason. The current Web catalog is loaded once; edits take effect after restarting Web, or in the next CLI invocation. New selections, queued execution and known derived/registered snapshots check both exact identity and source lineage. History and canonical content hashes are preserved. Setting true restores existing eligibility only; it does not create paper/live authorization.

The switch does not cancel an already-running in-process loop. After restart, a parked paper strategy cannot resume its loop or automatic technical exits. Broker reconciliation, native protection and manual exits remain available. Rotation has no paper authorization, so this checkpoint does not interrupt a running rotation position manager. An exit-only resume mode is not implemented.

Other retained limits: uncertain benchmark adjustment bases produce unavailable returns; intrabar protective fills without explicit price-reference timing publish a source date with unknown exact time; legacy host rows without ownership evidence remain untouched. The monthly driver, stop policy, funding model and broker opening-auction support require future separately reviewed work.

### Preservation and cleanup

No operator state, provider data, original results or strategy content was rewritten. No app/worker was launched, and no commit or GitHub sync was performed. Retain all three task-owned TRX files as evidence of failures, corrections and final verification. No temporary files were deleted; space freed: **0 bytes**. Shared build outputs remain. All task-owned build/test processes completed; unrelated processes were left alone.
## Local commit and remaining audit scope

On 14 November 2026 the user authorized a local commit of the reviewed changes and explicitly prohibited GitHub synchronization. This supersedes the earlier uncommitted handoff status. Reviewer B checked the 92-file source/config/test/document change set: no operator databases, datasets, secrets, or generated build/test outputs are included. The prior 555-test result and both independent signoffs remain applicable; no implementation changed after verification. Reviewer A independently reconciled the remaining audit scope below.

Completed audit items: **A1, A2, A4 and B1-B4**, plus the additional reversible strategy-disable feature. **B5 monthly paper execution is deferred.** These audit IDs are different from this plan's internal R/L/D/S/C checkpoint IDs.

| Audit item | Remaining work, excluding deferred monthly execution |
|---|---|
| A3 / F3 and C3 | Decide the success definition, benchmark and acceptable risk. A dividend-inclusive benchmark is conditional on that choice and needs verified cash-dividend evidence and comparable strategy accounting. The repaired benchmark is price-only. |
| A5 | Publish minimum end-of-session cash for every daily portfolio and refuse negative cash unless borrowing is explicitly allowed. The existing swap-specific guard is narrower. |
| A6 | Publish all simultaneous exit conditions, preserving the actual execution decision. |
| A7 | Store repeated drawdown history once and reference immutable evidence by hash. |
| B6 | Link decision price, quote at submission and actual fill; compare by strategy and month. Requires an authorized paper validation run to measure fills. |
| B7 | Enforce daily, weekly and peak-to-trough loss limits in the order entry path, with recorded risk events. The completed pipeline-health stop does not implement these financial loss limits. |
| C1-C2 | Consistently register saved-plan and signal-study trials by idea family; require a frozen signal study before portfolio experimentation. |
| C4 | Correct stale Active Configs documentation: six catalog research artifacts, five enabled and one parked. The explicit 5088 launch command is an override, not proof of a wrong deployed port. |
| D1-D2 and section 7 | Shared live candle transport, reusable charts and the related page improvements, including research ledger and order/fill comparison views. Automatic support/resistance is a newly discussed chart proposal, not an implemented audit repair. |
| E1, E3, E4 | Schedule immutable Finviz exports with all fields and receipt times; select screens matching an approved research design; measure forward outcomes over 1, 5 and 20 sessions. E2 is an existing discovery-only volume rule. |
| ORB, sections 3/9/10 | Freeze and run the published daily top-20 selection as a signal study with as-traded price handling and appropriate selected-symbol bars. Intraday paper execution is not implemented. No new study, strategy version or provider collection is authorized by this commit request. |

Operational rollout is also outstanding: the reviewed changes have been built/tested, not deployed to an active paper service; the additive migration has not been applied to the operator database. No broker orders or real fills were produced by these checks. Legacy unowned host rows remain untouched intentionally. The audit's wider coverage questions (delistings, dividends and independent real-money operational checks) are not resolved by this repair checkpoint.

Recommended engineering order: loss-limit enforcement and the general cash invariant first; fill measurement and an authorized operational rehearsal next; chart design can proceed separately. Success criteria must be frozen before further strategy experiments. No software repair establishes a profitable strategy.
## Follow-up checkpoint: loss limits, cash checks, chart design

User authorization (14 November): implement audit B7 and A5; prepare candles and automatic support/resistance design in parallel. Baseline `6c1e74b`. Keep separate independent plan/execution (A: plan_review) and code/design (B: design_review) reviewers. Chart author and risk implementer are not reviewers. No deployment, operator DB migration, broker order, strategy activation, market-data collection or GitHub synchronization is included.

| ID | Scope | Status | Acceptance |
|---|---|---|---|
| B7 | Account equity loss limits | core complete; linked cancellation partial | Configurable paper policy 2% daily, 4% weekly, 10% observed peak drawdown; authoritative dated equity/flow evidence; account-scoped durable stops and risk events; fresh evidence at reservation and first broker submission; pending entries cancelled, protection/exits retained; restart and session reset tests |
| A5 | Cash checks across native daily books | complete | Actual fills/fees, swaps on/off, unified and independent strategy books; minimum completed-session cash/date; incomplete execution and daily receipt coverage withheld; any known negative end-session cash prevents publication |
| UI-D | Candle and automatic support/resistance proposal | design complete; independently reviewed | Existing shared feed/engine reuse; no new trading authority; confirmed-bar zones and confirmation times; bounded history/stream/security; library review; tests and staged rollout specification; no chart implementation |
| VERIFY-2 | Two reviews and component verification | complete | Focused unit/integration/safety/replay checks, affected builds, both signoffs, exact limits and retained evidence |

The user delegated threshold judgment after the concrete paper proposal of 2/4/10%; these are the chosen operating limits, not a claim of a universal trader standard. Peak breach requires manual review before resuming; no automatic rearm. The existing daily/weekly defaults and 60-second broker-equity freshness policy come from production specifications. Unknown baselines or cash flows must block new entries, not reset losses. Initial peak coverage must be labeled as observed since the recorded guard epoch, not lifetime account history.

A5 reuses native session accounting. Existing run validation rejects gross-leverage settings for native daily replay, so no supported native borrowing exemption exists; do not invent one. Intraday leverage remains unchanged. Price-mark gaps alone do not erase known cash; missing execution evidence does. A calendar close alone is insufficient when the daily replay has not yet consumed that session's midnight receipt.

Initial review A approved these cash boundaries and identified account-baseline, reset and cancellation requirements. Initial review B identified daily receipt coverage and authoritative short-session/cash-flow evidence requirements; these are being incorporated.

### Follow-up verification and scope refinements

- Initial combined build succeeded; focused cash/ledger/rotation/research slice **40 passed, 0 failed, 0 skipped**, 48 seconds. TRX `.tmp/codex-risk-cash-20261114-01a1dc0a/risk-cash-initial.trx`. Further multi-strategy and risk regressions are pending the next run.
- Cash minimum evidence is published in `result.json` on native portfolio states; no new Web/Mobile minimum-cash display is claimed. Native unsupported borrowing remains refused. Unknown selected work is propagated before checking both unified and independent books, and summary verification checks all fields after replay.
- Account loss limits preserve broker protection. Automatic cancellation covers only confirmed standalone opening remainders. **RSK-02 linked bracket cancellation remains open:** cancelling a linked parent can cancel its protective legs; until a no-gap protection handoff is verified, the unresolved entry remainder requires a critical operator-review record and new entries remain blocked. Such a remainder can still fill; this checkpoint must not claim all working exposure increases are prevented.
- [Chart design](../design-review/2026-11-14/candles-support-resistance-design.md) covers all five requested layers. Both reviewers checked it; completed candles/zones, forming candles, weekly context and private trade markers have explicit staged acceptance. Chart author addressed bounded reconstruction, evidence timestamps, interval-aware freshness, revision removals, partial-fill/stop-replacement marker lifecycle, and unauthorized subscription tests. It remains a design proposal, with no dependency installed or chart UI implemented.
### Final risk review and chart return point (15 November)

The broad component run passed **482 tests, 0 failed, 0 skipped**, in 3 minutes 17 seconds, including native daily cash/ledger/publication, operational risk, broker evidence, SQLite, lifecycle and replay coverage. Evidence: `.tmp/codex-risk-cash-20261114-01a1dc0a/risk-cash-component-1.trx`. A subsequent review found a crash between persisting a loss breach and sweeping working opening orders. The fix atomically journals a pending sweep generation with each new breach, preserves it across daily/weekly resets, requires a fresh broker snapshot, and fences completion against both a newer breach and unresolved recorded first submissions. A final risk-specific rerun is required for that fix. Reviewer B has accepted the final code/design conditionally on that rerun.

The supported account evidence checks reported historical cash flows and the current non-trade adjustment field. It does not independently reconcile in-kind transfers, securities journals or noncash corporate actions. Those require separately reviewed activity evidence and an equity rebaseline before relying on these limits. There is no manual peak-reset/funding-rebaseline action or frozen historical-backtest loss-limit option in this checkpoint. See [account loss policy and limits](../design-review/2026-11-14/account-loss-limits.md).

The user's overnight chart request is preserved as the next UI return point. The five-layer design includes candles and volume; session shading, forming/completed distinction and freshness; confirmed support/resistance zones and prior day/week ranges; opening range, premarket high/low and VWAP; daily/weekly context and moving averages; and actual entry/stop/target/fill plus news/earnings markers. Defaults and optional overlays avoid making every marker permanently visible. Weekly candles, forming previews and private trade markers have explicit later acceptance stages. Neither chart UI nor support-break trading logic has been implemented. The next concrete UI step is to review this saved design, then implement its first Desk checkpoint within the agreed scope. Rotation remains disabled and separate.
### Final follow-up verification

- Final risk run: **296 passed, 0 failed, 0 skipped**, 1 minute 28 seconds. Evidence: `.tmp/codex-risk-cash-20261114-01a1dc0a/risk-final.trx`. The run compiled all affected projects, including Web and CLI. No implementation changed after this build. This rerun covers the final pending-sweep and original-request-age fixes. The earlier 482-test run retains broader native daily cash/publication coverage; these counts overlap and must not be added as distinct tests.
- Command: `dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter <risk suites below> -m:1 --disable-build-servers --verbosity minimal --logger "trx;LogFileName=risk-final.trx" --results-directory .tmp/codex-risk-cash-20261114-01a1dc0a -- xUnit.MaxParallelThreads=1 xUnit.ParallelizeTestCollections=false`.
- Risk suites: AccountLossLimitPolicyTests, EntryGateChainTests, AlpacaBrokerClientTests, AccountReconciliationServiceTests, ProductionParameterRegistryTests, ProductionConfigurationLoaderTests, RejectCodeContractTests, SqliteDurabilityTests, OrderSubmissionServiceTests, EntryDecisionPersistenceTests, ProductionCompositionTests, OrderSynchronizationRunnerTests, OrderDispatchFailureTests, OrderLifecycleServiceTests, OperationalTradingLimitsTests, DatabaseMigrationTests, WorkflowReplayTests, LiveRunnerIntegrationTests, PaperJobServiceTests, PaperJobRecoveryServiceTests.
- The 482-test component run also selected NativeDailyCashCheckTests, NativeDailySessionLedgerTests, NativeEconomicClaimSuppressionTests, NativeDailyResultIntegrationTests, NativeArtifactIntegrityTests, EtfRotationTests, ResearchScreenDailyRunTests, ResearchScreenHoldingRulesTests, DecisionHourBacktestTests, DecisionHourHoldingRulesTests, BacktestArtifactIntegrityTests, IntradayPortfolioExecutionTests and EventPortfolioExecutionTests. Filters select existing matching test names; this is a component check, not a claim that every named filter selected a class or that the full release suite ran.
- Reviewer B independently signed off A5 and core B7 after the final clean result. RSK-02 remains partial as recorded above. Chart design had separate A and B review; UI implementation remains future work.
- CRLF-aware `git diff --check` passed. No deployment, provider requests, actual paper orders, operator migration, live activation or GitHub synchronization occurred. This follow-up remains uncommitted; the previous authorized local checkpoint is `6c1e74b`.
- Cleanup: removed only the chart author's proven duplicate scratch draft after canonical preservation, freeing **36,709 bytes**. Retained all three follow-up TRX files and prior checkpoint evidence for review, plus shared build outputs. Task-owned build/test processes completed. No core data or unrelated process was removed.
Reviewer A independently read both final TRX reports and signed off A5, core B7 and the five-layer chart design. Reviewer B separately signed off the implementation/design. Neither reviewer authored these changes. The accepted scope explicitly excludes unresolved RSK-02, operational rollout, manual rearm/rebaseline and chart implementation. The next chart work starts from the reviewed first Desk checkpoint; the user's requested full layer set remains tracked in the design rather than being marked delivered prematurely.
## Active chart checkpoint: shared stock charts (15 November)

User approval: “lets move then”; subsequent explicit requirements add short intraday and week/month/year support/resistance context, one chart service opened from stock links, on-demand cache reuse rather than automatic charts for all research candidates, and P0 responsive plotting/usability. The user explicitly requested parallel implementation agents and Astra indicator research. This authorizes the reviewed first chart checkpoint and the stated presentation/navigation refinements, not rotation activation, research runs, live orders or GitHub synchronization.

| Work | Owner | Status | Acceptance |
|---|---|---|---|
| Shared chart inputs, bounded cache, completed candles, pure zones/overlays and event projections | paper_fixes (implementation) | complete, reviewed | Reuse existing provider/store/stream; no new subscription; ≤500 daily / bounded intraday history; shared inputs across context changes; confirmed-only calculation, honest gaps/basis/receipt clocks |
| Responsive reusable chart UI, Desk host, stock-link route, keyboard and mobile use | chart_ui (implementation) | complete, reviewed | Actual candlestick/volume rendering, source-labelled zones and overlays, usable empty/stale/retry states, narrow-screen controls, shared component, local pinned assets and notices, no ticket action hijack |
| Authenticated transport, history cursors, connection/read limits and integration | root | complete, reviewed | Same-origin cookie-auth WebSocket; bounded atomic snapshots; exact user/series/context cursor binding; source membership validation before expensive reads; old view cancellation; no broker authority |
| Optional ready-made indicator research | indicator_research (Astra) | complete, research only | Reuse installed C# indicators; compare practical overlays, data/warm-up costs and licenses; no new predictive claim or indicator activation |
| Plan and execution review | plan_review (A) | final signoff | Verify actual P0 behavior, requested contexts and cache semantics, scoped tests and retained limitations |
| Independent code/design review | design_review (B) | final signoff | Security, resource bounds, canonical stream reuse, calculations, responsiveness, revisions and failure paths |

Week/Month/Year structural contexts use approximately 5/21/252 scheduled exchange sessions of confirmed daily pivots. They are not weekly/monthly candle aggregation or a promise of predictive value. Intraday contexts use completed 15m pivots consistently across 1m/5m/15m candle views. Visible range/zoom is separate from structural context. Fixed 120-input ATR warm-up and explicit acquisition ceilings apply. A current Year reconstruction fits the 500-observation limit; full historical Year zone versions across a 252-session view do not. Show current reconstruction at its supported availability/right-edge annotation rather than shading historical prices with future knowledge.

Cache policy: load only when a chart is opened; reuse stored candles and coalesce bounded source loads across pages/contexts; cap recent active in-memory projections; refresh only while needed. No bulk chart job for research candidates, no data collection simply from listing candidates, no automatic deletion of original stored candles. Individual research candidates can be opened for inspection using their recorded app membership.

Transport implementation choice: send a complete, bounded atomic baseline when projected content changes, chunked below the transport buffer. A reconnect always receives a fresh baseline. This avoids reconstructing missing revisions/removals in the browser; no resumable delta-history claim is made in this first version. Connection heartbeat remains distinct from provider freshness. Final independent review and bounded transport tests accepted this first-version choice.

First checkpoint retains completed candles/volume/session distinctions, source freshness, confirmed zones, previous ranges/VWAP/opening/premarket where coverage exists, daily moving averages and stored news/earnings. Forming trade previews, genuine weekly candles, private recorded trade markers and additional optional indicators remain separately staged. The full five-layer design stays tracked. Optional-indicator findings: [Astra research](../design-review/2026-11-15/optional-chart-indicators-research.md).
### Fable follow-up verification (15 November; chart work continues)

The author added section 0a to the source audit. Its corrections C1/C2/C3 (distinct from the original audit's section-C item IDs) confirm the committed handling of separate fill clocks, decision expiry versus source freshness, and deferred monthly rotation. The original audit body remains historical evidence, not an updated claim about the latest uncommitted A5/B7 changes. Do not rewrite the author's audit to conceal that distinction.

| Follow-up | Verified finding and disposition |
|---|---|
| Membership skip visibility | Confirmed three silent entry skips, including one after Accepted. A separate implementation agent added structured deduplicated Skipped decision records with stage, exact identity, source expiry evidence and a distinct setup-deadline reason. Targeted integration and audit-page tests passed; both reviewers signed off. Trading admission is unchanged. |
| B3 failure isolation | Reopened: LivePipelineHealth.RecordPoll counts any ticker failure as a failing poll; three consecutive polls latch the whole run, and a recovered poll does not clear the durable latch. This was implemented to the earlier overly broad requirement. A ticker-scoped stop with separately classified run-wide failures needs a reviewed repair before paper rollout. SIVE's actual current provider behavior was not queried. |
| Refresh health status | The audit fix exposes skipped entries, but a failed membership refresh still does not change the pipeline health counter. Define visible degraded source health separately from a ticker evaluation failure; do not label this entire issue resolved by adding audit rows. |
| Rebuild continuity | Same exact provenance can resume; a different build cannot continue the same strategy run. No exit-only continuation exists. Account reconciliation/protection paths are separate and remain available, but do not replace strategy exits. Long-running paper operation across builds needs a reviewed handoff/continuation design. |
| B7 reset and funding baseline | Confirmed: peak/funding latches lack a supported reset/rebaseline action. The guard is wired into the new build; it is not deployed or switched on in an operator service by this session. The missing reviewed rearm path is a paper-rollout blocker, not merely a disclaimer. Linked-order cancellation RSK-02 remains partial too. |
| Real-data validation | Only synthetic component tests were run. Plan 192 was not rerun, operator migrations were not applied, and the reported count of 28 operator migrations was not independently rechecked. Proposed future replay must use the normal frozen-plan workflow, preserve originals, compare trade identities and minimum cash ($3,053.71 trend; $3,344.52 hold), and verify the corrected benchmark interval and NY/local fill labels. These are comparison expectations, not new measured results. No economic run was authorized by the request to verify this feedback. |
| Late paper entries | Verified calendar deadline remains the executable session's close. A mid-session start can fill later than a backtest open; B6 measurement and an explicit entry-window policy remain outstanding. |
| Re-running old cash profiles | A proven negative native daily closing cash balance now refuses publication; that is intentional and does not rewrite original outputs. |
| Loss policy parity | Operational loss limits have no frozen historical-backtest option. Recorded entry refusals explain divergence, but do not establish paper/backtest equivalence. |

Priority after the ongoing user-designated P0 chart checkpoint: repair ticker/run health isolation and visible source degradation, design safe paper continuation and risk rearm before rollout; pull forward the daily immutable Finviz archive design and success-definition decision. Daily archive capture cannot recover screens from past days. No collector schedule, provider run, success criterion or strategy promotion has been created implicitly by this prioritization.

### Chart verification progress

- First build found two Razor partials using Request instead of Context.Request; corrected without changing behavior.
- Second build compiled all affected projects. Component tests: **121 passed, 2 failed, 0 skipped (123 total)**. One HTTP test incorrectly required a fixed header-token order; the assertion now checks the actual private/no-store directives. One canonical-revision fixture omitted the source receipt stamps that the production parser supplies; the fixture now carries original and revised KnownAtUtc separately. No production timing semantics were weakened.
- Separate review found and drove fixes for partial history seed repair, leading/trailing coverage proof, missing-bar retry, basis-separated moving averages/ranges, current-only zone geometry, stable cache acquisition clocks, independent cancelled cache waiters, marker query bounds, old selection/authorization races and two-tab membership. Final tests remain pending.
- Initial chart zones are current-only in every context, not only Year. No paged historical zone-version replay is claimed. SMA/VWAP may show distinct labelled retrospective curves at exact source candle closes; source receipt/availability remain separate in the readout. Current reference levels never infer historical values. This display clarification has independent code/design acceptance and is recorded in the design.
- Chart access may reuse bounded exact symbols already rendered by an authenticated server page (30-minute read-only view grant), recently streamed Desk membership (5 minutes), or existing local app records. Preparation membership is registered only after the existing owner-scoped detail read succeeds. None grants an order, subscribes to provider data, prebuilds candidate charts, or reveals private trade markers.

### Final chart and membership checkpoint evidence

- Final component run: **129 passed, 0 failed, 0 skipped**, 31 seconds; affected Web/CLI projects compiled. Evidence: .tmp/chart-verification-20261115/chart-component-4.trx. The preceding 125-test run is superseded for final source; counts overlap and must not be added.
- C# command: dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter "FullyQualifiedName~ChartProjectionTests|FullyQualifiedName~ChartTransportTests|FullyQualifiedName~ChartEndpointTests|FullyQualifiedName~StreamingMarketStateProcessorTests|FullyQualifiedName~AlpacaMarketStateStreamLifecycleTests|FullyQualifiedName~LiveRunnerIntegrationTests|FullyQualifiedName~PaperJobServiceTests|FullyQualifiedName~ProductionCompositionTests|FullyQualifiedName~HistoricalPaperView_UsesDurableAttemptWhenGeneratedConfigIsMissing|FullyQualifiedName~MembershipSkipReasonReachesAuditPage|FullyQualifiedName~PreparationEndpointTests" -m:1 --disable-build-servers --verbosity minimal --logger "trx;LogFileName=chart-component-4.trx" --results-directory .tmp/chart-verification-20261115 -- xUnit.MaxParallelThreads=1 xUnit.ParallelizeTestCollections=false
- Focused browser command (tests/ui): npx.cmd playwright test specs/stock-chart.spec.js. **5 passed**, 17.6 seconds. An initial hidden-form fixture locator was corrected. Visual inspection also found vendor fractional logical-coordinate behavior that misplaced zone boundaries; plotting now interpolates integer candle centres, with pixel assertions and preserved desktop/390 px screenshots. Candles, volume, VWAP and current zones render without clipping.
- Direct-consumer browser command (tests/ui): npx.cmd playwright test specs/watch-desk.spec.js specs/preparation.spec.js specs/group-news-earnings.spec.js --grep "source switch|preparation opens|administrator buy|non-administrator|signals view|dark theme|real API saves|shared News preserves". **8 passed**, 18.4 seconds. This covers shared navigation, source changes, preparation persistence, dark/keyboard use and existing order controls.
- Reviewer B independently signed off final code/design after reading the source and final C#/chart browser results. Reviewer A separately verified the final TRX, inspected desktop/mobile captures and signed off plan execution after all 13 browser checks passed. Neither reviewer implemented the checkpoint.
- Verification tier: affected component and direct consumers. These are synthetic tests, not evidence of economic performance or real-provider chart acceptance. No full release suite, new Android build, plan-192 economic replay, operator migration, deployment, private broker order, live activation or GitHub synchronization was performed. Strategy/backtest stock links open the current stock chart, not historical trade replay. A fresh real-data acceptance run remains separate.
- Plan-192 future comparison targets supplied by Fable: identical published trades; minimum cash $3,053.71 trend and $3,344.52 buy-and-hold; SPY approximately +108% to +111%; first fills displayed 1 February 2017, 3:30 PM GMT+1. None is a new measured result.
- CRLF-aware git diff --check passed. Task-owned test hosts shut down; only the unrelated tradingflow-site runtime remained and was left untouched. Playwright removed its isolated synthetic databases/cache/runs/artifacts after shutdown; reclaimed bytes were not recorded. Retained chart TRXs/screenshots and 1,317,190 bytes of owned chart asset acquisition/provenance files for review of the uncommitted checkpoint. No original market data, shared candle cache, operator database or unrelated process was removed.
- Follow-up priorities remain ticker/run health isolation and visible source-refresh degradation; safe continuation across builds; reviewed loss-limit rearm/funding rebaseline and RSK-02 linked-order protection; daily immutable Finviz archive design; and the user's explicit success definition. Rotation remains disabled. These open items prevent claiming that the whole Fable audit or paper rollout is complete.
## Active paper rollout repair checkpoint (15 November)

User explicitly authorized the remaining paper fixes and verification of saved plan 192 and the operator migration count. Chart follow-up continues with a separate Sol 6.1 High agent; Fable's chart review is pending. No deployment, broker order, strategy promotion, GitHub sync or mutation of original research results is implied.

| Work | Owner | Status | Acceptance |
|---|---|---|---|
| Ticker failure isolation and source refresh health | paper_health | complete, A/B reviewed | Failed ticker cannot stop healthy tickers; shared faults separately counted; failed source visible and fenced through admission/dispatch; successful refresh recovers only its own health; exits retained |
| Cross-build continuation | paper_continuation | complete, A/B reviewed | Exit-only after build mismatch, exact frozen identities and broker reconciliation, immutable origin provenance plus actual build/lease attempt journal, durable entry fence before dispatch; original position exits retained |
| Loss-limit reset and funding rebaseline | root | complete, A/B reviewed | Administrator identity/reason; fresh authoritative broker evidence; exact state revision fence; preserve daily/weekly and cancellation/reconciliation controls; both anchors after reviewed funding; immutable review journal |
| Operator migration history | root | verified read-only | data/tradingflow.db has 28 applied migrations; latest 20261105182540_AddEarningsReleaseReferenceTimes; source has 29, missing 20261114120000_AddProductionHostRunLease; none applied to operator DB |
| Saved plan 192 replay | root | replay complete, independently compared | Normal saved-plan workflow on isolated snapshot; frozen 2017-2023 development window, two strategies/11 ETFs; original results untouched; compare exact economic trades, cash, benchmark interval and published fill clocks; unopened 2024-2026 holdout excluded |
| Plan/execution review | rollout_plan_review (A) | final signoff | Independent acceptance and final evidence; identified stale-worker dispatch issue, requiring exit-only cross-build scope |
| Code/design review | rollout_code_review (B) | final signoff | Independent source review, transaction/auth/clock/funding checks and exact test evidence |

Rebaseline is deliberately bounded: no invented net-flow adjustment. A documented transfer cannot be cleared until authoritative prior-day and prior-week closes are strictly later than its date; same-day unclassified adjustments remain refused. Peak reset establishes a new observed peak epoch, not a statement that historical losses disappeared. Daily and weekly latches remain active until their normal verified period transitions.

Read-only operator database inspection used Node's DatabaseSync with readOnly:true. No application host was started against that database; startup would otherwise apply migrations. Plan 192 was found in the saved application-event stream with the expected two immutable strategies, 11 sector funds and 2017-01-01 through 2023-12-31 development period.

### Paper repair verification checkpoint

- Initial account-loss slice passed 43 tests. Independent review then found and fixed two missed-weekly-loss replay cases: a peak reset must retain the previous weekly observation cutoff, and a first-ever funding latch must replay from its verified week anchor. Both regressions are included in the final combined result.
- Combined paper component report: .tmp/paper-rollout-20261115/paper-rollout-component-1.trx, **268 passed, 0 failed/skipped**, about one minute. Includes SQLite leases/first-dispatch/source evidence, health/isolation/refresh recovery, immutable candidate revalidation, real common exit service, administrator/CSRF/reset revision, and loss-period replay.
- Independent A verified the TRX and execution plan; independent B inspected final source and the same TRX and signed off paper code/design. The final legacy-status wording assertion and Alpaca exhausted-429 archive regression still have a focused follow-up pending.
- Cross-build continuation is exit-only and uses immutable origin RunId/provenance plus actual executing CodeVersion/lease attempt evidence. An exit-only run cannot first-submit old pending entries, cannot run account-wide active_cancel entry sweeps, and can keep original position technical exits. Deployments must assign an immutable CodeVersion (existing Git SHA or explicit build identity); different uncommitted binaries labelled with the same SHA are not distinguishable by this contract. Normal entry continuation across builds is not claimed.
- Correction to data-root discovery: repository-local data/tradingflow.db is an old copy. The active configuration points to V:/trading_flow/data. Read-only inspection of that database independently confirms the same **28 applied migrations**, missing the new host-lease migration. No migration was applied there.
- Saved plan 192 is copied exactly into a fresh isolated database under .tmp/ui-tests/paper-rollout-real-20261115. Its 29 migrations and synthetic login are isolated; no old operator job was copied. Required dataset canonical records match the active V: catalog. Existing immutable evidence objects are shared read-only by the replay path; new configs/results/raw captures use the isolated root. Original result hashes were rechecked on V: and exactly match the published d12043a5… (hold) and 2d22ca0f… (trend). Replay outcome is still pending.
- User explicitly requested fixing Fable's five chart findings. Labels, Berlin time, configurable bounded multi-tab access and expired history recovery are implemented; bounded history refresh and real-SIP acceptance are being tested/reviewed independently. Finviz historical price export is a separate source-validation candidate; no source mixing or adapter activation occurred.


### Real-data replay and final chart acceptance progress

Both plan 192 jobs completed through the ordinary authenticated saved-plan workflow, sequentially in the isolated host. New hold job edc6336b-ed41-7f1e-d67f-a32aa23f22f5 and trend job 7ff66e06-5597-cba1-3b68-901489951847 retain 16 complete artifacts each. The independent plan reviewer also performed the quant/accounting comparison: all 185 ordered fills, 83 closed trades and 1,760 full daily ledger rows per book exactly match original economic fields and processing clocks (generated candidate IDs excluded). Original result SHA-256 hashes are unchanged.

Cash minima are 3053.706952 (trend, first tied date 2021-04-01) and 3344.51641 (hold, first tied date 2019-07-01), with verified full cash coverage. The corrected raw price-only SPY benchmark is 111.2113 percent, using first evaluation-session open 225.04 and final evaluation-session close 475.31. The first economic fill is 2017-02-01 14:30 UTC / 09:30 New York / 15:30 Berlin; original processing and availability timestamps remain separate. No 2024-2026 holdout was executed. Prepared-source query counts are reads of archived evidence, not internet downloads. Comparison evidence: .tmp/paper-rollout-20261115/plan192-comparison.json.

A subsequently noticed that the Web trade label still showed only UTC. The shared label now shows New York, Berlin and UTC for modeled instants; unknown range times and legacy receipt labels remain explicit. Seven presentation tests pass, including mismatched US/EU daylight-saving dates. B reviewed this presentation-only change; economic replay did not need repeating.

Final chart component checks before real acceptance: 74 passed, 0 failed/skipped (supersedes overlapping 68). Includes the last health formatter assertion and archived exhausted-429 adapter case. Six browser checks passed after correcting exact button matching and explicitly selecting the inspected candle. Both reviewers inspected desktop and 390px screenshots.

Real chart attempt 1 obtained 390 AAPL regular 1m bars for 2026-11-13 and 126 visible daily bars from Alpaca SIP/all, using 16 fully paginated HTTP pages and 1,408,065 archived payload bytes. Three simultaneous authenticated chart websockets delivered real baselines. After 125 seconds, archive count/bytes and unchanged candle receipt clocks were identical. This is a closed-weekend reuse check; active-session suffix bounds have controlled-clock tests. Actual captures revealed a mobile long-price wrap and a Sunday reference-session bug suppressing Friday VWAP/opening/premarket; both are being corrected, preserving this first attempt evidence. The final real-data rerun is pending.


### Final closure of this authorized checkpoint (15 November)

The four requested paper repairs, plan-192 replay verification, operator migration-count verification and five requested Fable chart fixes are complete locally. Independent plan/execution and code/design reviewers signed off their scopes. This is not deployment or a claim that every remaining audit item is complete.

Final chart refinement: 51 C# tests passed after the real-data-discovered weekend context and mobile numeric fixes; six browser tests passed in 16.0 seconds. These overlap earlier chart runs and must not be added as distinct coverage. The final 74-case chart/health/adapter run retains coverage outside that refinement; the paper component run has 268 cases and the fill-label presentation slice seven. Each final targeted run had zero failures/skips.

Final real AAPL SIP/all acceptance ran 11:25:19.686–11:27:30.196 UTC on 15 November, with 390 regular minute bars for Friday 13 November and 126 daily viewport bars (500 acquired). Three authenticated WebSocket tabs each received a real baseline and 25 heartbeats. Independent review recomputed Friday VWAP 358.09675389965764 from all 390 minutes, opening high/low 360.83/357.84, premarket 358.58/355.32, and Thursday prior-session 359.3684/354.41 from the archived raw payloads. The final phone capture keeps complete prices on one line.

The final cold seed legitimately added 16 fully paginated HTTP 200 pages / 1,408,065 payload bytes to the retained first attempt. Total AAPL SIP bar archive counts and payload bytes were exactly 32 / 2,816,130 before and after the 125-second repeated refresh; all archive identities/hashes remained unchanged. No empty response or invented minute supplied prices. This verifies closed-session cache reuse; controlled-clock tests verify the active refresh overlap and repair paths. The first diagnostic captures are retained as evidence of defects found and fixed.

No operator migration, account reset, broker order, strategy activation, deployment, commit or GitHub sync occurred. Final read-only check of V:/trading_flow/data/tradingflow.db still finds 28 migrations and zero rows for the two isolated replay jobs. Original result hashes still match. The user-owned deployed tradingflow-site process was left untouched. All task-owned hosts, browser and test workers finished or were stopped.

Cleanup: Playwright removed its own synthetic test databases/cache/runs/artifacts after each owned host shut down, and the real-read harness removed only its own isolated temporary wishlist (HTTP 204). Space reclaimed by those fixtures was not measured. Retained 2,770,887,454 bytes in the isolated replay workspace excluding junction targets, primarily the 2.13 GB catalog snapshot plus replay inputs/results, and about 1.45 MB verification logs/TRXs/manifests. Retained immutable real provider captures, both visual attempts and source/binary inventory for reproducibility and review; no economic input cleanup was authorized, no original or shared evidence was deleted.

Remaining outside this checkpoint: RSK-02 linked-parent cancellation/protection handoff; operational deployment/migration and paper execution acceptance; daily immutable Finviz archive and explicit strategy success criteria; historical backtest loss-policy parity and entry-timing measurement. Rotation stays disabled. Chart forming previews, weekly candles, private trade markers and optional higher-timeframe zones on intraday views remain staged, as recorded in the chart design. Finviz historical exports remain a separately validated source candidate; current chart prices still use Alpaca.

## Linked protection and operational acceptance checkpoint (15 November, active)

User authorized the remaining linked-order fixes and operational acceptance, with separate reviewers and no GitHub sync. Reviewer A `linked_plan_review` reviews plan/execution; reviewer B `linked_code_review` reviews code/design; `linked_protection_impl` implements the cancellation and durable review changes. Root owns nested-order polling, native stop recognition, operational checks and this plan.

Design: preserve existing broker-native protection. A generic opening-order cancellation must freshly prove the parent is standalone, including on recovery; linked/unknown groups stay pending rather than issuing a DELETE that can remove their stops. A terminal parent alone cannot close a loss review. Reconcile its exact cumulative fills, then require fresh stable broker/local position agreement and positively verified owned resting coverage, or proven flatness, before clearing the durable entry stop. `done_for_day`, pending/replaced/unknown statuses and missing responses are not final cancellation evidence. Shutdown must preserve linked groups, complete protection verification and report unresolved orders. Polling must distinguish direct entry parents from nested exits; an exactly owned fully filled bracket stop must count as protection without a duplicate stop submission. Explicit position-exit handoff remains separately governed.

Provider limitation: Alpaca activates bracket children after complete parent fill and cancels group siblings when a member is canceled. Its documented API does not offer atomic detach/conditional zero-fill cancellation. Therefore no unconditional no-gap automatic cancellation of a linked working remainder is claimed. New entries currently use standalone orders. RSK-02 remains partial for such linked remainders until a supported handoff or separately approved risk policy exists.

Acceptance: parent + held children polling; fully filled native bracket stop; partial fills and late REST projections; exact ownership/account/generation and quantity; wrong-side, held, pending, missing and duplicate stops; restart/pending cancellation; done-for-day and replacement; preservation of explicit exits and standalone cancellation; shutdown protection after linked refusal. Heavy checks remain sequential. Reviewer A approved the plan with these conditions; final A/B reviews remain pending.

Operational progress: real configured paper account matches, ACTIVE/unblocked, zero open orders and zero positions (HTTP 200, read-only GET; 15 November 12:21 UTC). Market closed, next open 16 November 09:30 New York / 15:30 Berlin / 14:30 UTC. No orders queued. Fresh online SQLite backup from V:/trading_flow/data/tradingflow.db passed integrity_check and has 28 migrations. Chart preview uses a separate copied reviewed binary and copied operator DB at port 53028; normal operator login, copied News/Earnings, trading runtime disabled and market feeds enabled. Its isolated startup rehearses migration 29 without touching the operator DB. Original deployed port 10088 remains untouched. The earlier test-data preview was stopped and replaced; user explicitly requested keeping the chart server available.

### Independent checkpoint review and release verification

Reviewer A independently verified the 265-case component report, conservative source scope, broker read preflight and migration rehearsal. Reviewer B independently inspected the final code and named counterexamples and signed off the conservative implementation; no unresolved source blocker in that scope. Native bracket/OTO stops now require exact current parent/leg IDs, account and position-generation ownership, final parent fills, valid remaining quantity and consistent group metadata. Ambiguous or duplicate native protection does not trigger a competing backstop. Linked partial-entry review no longer prevents the stream from continuing or leaves a permanent transient stop after the shared resolution proof succeeds.

The release-tier full backend suite is now running. It surfaced three intraday publication cases where the existing artifact verifier compares additive FillTiming metadata as part of the original execution-event equality. The original economic fields must remain exactly checked; a narrow reporting-evidence fix is under investigation. A timed recovery-host test also exceeded its three-second cycle wait under the full suite; deterministic timing is being reviewed. Deployment is not yet approved by test evidence and the operator service remains unchanged.

Preview UI acceptance observed through the real signed-in browser: AAPL real Alpaca SIP candles and automatic zones, current News with 64 matching articles in the four-hour filter, and 59 next-reporting-day Earnings events for Monday 16 November. The initial preview's synthetic-login/data mistake was corrected by switching to a fresh operator snapshot; current preview is a separate copy, not the operator journal. Its market-only feeds run while broker/runtime actions remain paused.

Known boundaries outside the conservative cancellation fix: an active legacy linked remainder can still fill until the broker resolves it; discretionary exit from a legacy native bracket needs a separately verified native-group handoff; the old shared lifecycle projection maps done_for_day to Expired (the new cancellation/review paths refuse to treat this as final broker evidence). Do not claim unrestricted automatic cancellation or resume for those cases.

### Pre-production clarification

The user clarified that this system is not in production and backward compatibility is not a requirement for earlier code paths, builds, schemas or artifact formats. Apply a clean current contract; do not add fallback branches merely to keep earlier versions working. This does not authorize deletion of research evidence, operator lists, provider records or published results.

Changes following that instruction: the current artifact verifier will require original fill-timing evidence instead of accepting an old both-untimed form; protection-resolution interfaces require implementations explicitly rather than default compatibility bodies; the shared done_for_day projection is being corrected directly so current GTC orders stay working/cancel-pending and account their cumulative fills. No historical-row repair or parallel old/new state machine is being added. Provider-level linked-order cancellation races are current broker constraints, not compatibility requirements.

Expanded tests before this final clarification: 324 passed, zero failures/skips in 57 seconds (linked-final-component.trx). The first full run was interrupted after its four observed failures were understood and fixes reviewed; no full TRX was emitted, so the observed failures and owned-process shutdown are retained in linked-release-full-interrupted.txt. This is not a full-suite pass. A fresh final-code run follows the current-contract changes.

A real read-only sample of 15 closed paper orders (HTTP 200, nested=true, limit=20) confirms current Alpaca equities standalone orders use an explicit empty order_class and OCO parents/children retain oco; the safe adapter normalization is supported by actual provider data. No bracket/OTO mutation or active-group handoff was exercised.

### Current-contract review and final suite progress

The final rebuilt component report linked-final-reviewed-component.trx contains 378 passed, zero failed/skipped, in 56 seconds. Reviewer B independently parsed it and signed off the source. This includes current-format-only fill-timing checks and GTC done_for_day preservation with filled quantity, cancellation pending and next-session reconciliation. A separate submitted-order stream-summary test, selected by the full suite, also passed: stream metadata cannot create an unjournaled position fill. This supersedes the earlier done_for_day projection limitation recorded above; broker linked-group cancellation races remain current provider constraints.

The final full backend run is continuing, with normal per-test progress and a five-minute hung-test diagnostic. One discovery restart test reached its ten-second fixture cancellation watchdog before its fourth poll; it is not waived. The full report will be retained, then the test's completion signal and watchdog will be investigated and the affected checks repeated. No operator app update or commit has occurred at this point. A fresh independent reviewer A is checking execution and the paused local-app update conditions separately from B.
### Original news preservation discovered during rollout review

Independent plan reviewer A found that the existing news sweep deleted up to 1,000 NewsItems older than 72 hours on each refresh. These include directly stored websocket receipt times; separate REST archives do not replace those original observations. The operator backup has 9,921 records, including records already outside that moving window. Original-data preservation therefore requires removing automatic expiry before enabling the new app against operator storage.

The obsolete prune API, repository deletion method, refresh call and test fake method were removed. The 72-hour display/query bounds remain, with corrected user-facing copy and names that describe a display window rather than storage retention. A new real-SQLite regression calls the refresh twice during provider outages, checks that an old headline keeps its original publication/receipt times and text, and verifies recent-view filtering separately. Independent B reviewed and accepted this narrow source change; rebuilt test evidence is pending. There is no compatibility toggle or alternate legacy cleanup path.

Final operator checks will compare original saved-list/Finviz identities and memberships, identity/account records, execution journals and original NewsItems IDs, not merely net row counts that could conceal deletion. Fresh backup and exact port/PID/account binding checks precede the paused app update. Trading pause is proven by launch configuration and guarded actions; /health/trading-readiness must refuse entries, while research/stock-assessment workers and News/Earnings remain available as configured.
### Reviewed source checkpoint saved before local cutover

Final rebuilt component verification: linked-retention-final-component.trx, 413 passed, zero failed/skipped. Reviewer B independently verified the report and source; the old-news preservation test and explicit fourth-poll recovery test both passed. The discovery failure reproduced as a ten-second full-suite fixture deadline: the identical earlier binary passed alone in one second. The fixture now explicitly requires its fourth poll and permits a two-minute emergency watchdog; no runtime discovery behaviour changed.

The earlier full run retained 3,934 passes and one fixture-watchdog failure, then a five-minute result-inactivity diagnostic interrupted a productive 20-job matrix case. Its partial TRX/log/diagnostic remain evidence of an incomplete run, not a full pass. The final full run now uses a thirty-minute diagnostic threshold and output progress monitoring. Current code is independently reviewed and component-tested for a local commit; local app replacement still waits for the final full result. Reviewer A conditionally accepted the paused update with fresh account/host/backup checks and original-data comparison. No GitHub operation is authorized.
## Next requested phase: usability, guide and optional chart indicators

User request, 15 November: after the current fixes and verification, write a practical how-to guide; critically review Desk, everyday preparation, strategy selection and backtesting flows; improve their usability; and complete the previously discussed optional built-in chart indicators. Work starts after the active operational checkpoint. Review actual signed-in screens and existing services first, reuse the shared chart and the saved optional-indicator research, and keep optional overlays selectable instead of crowding the default chart. The guide must distinguish implemented features, operational limits and staged work. Present concrete UI/indicator design changes before implementation where repository design approval applies. Keep separate independent plan/execution and code/design reviewers. No strategy promotion or GitHub sync is implied.

Local reviewed implementation saved as ad46d2c5d3ce98cd7f0a6473ff342cf7bb2ac4b1. Fable's separate audit edit remains unstaged. Full-suite execution and actual local-app update remain in progress; this local commit does not claim release acceptance.
### Complete backend case inventory before local update

Final inventory linked-final-case-inventory.json covers all 3,958 unique test identities, matched by test ID with display names retained: 3,954 Passed, four opt-in news-memory/execution-profiling cases NotExecuted, zero missing/changed/duplicate cases and zero unresolved failures. This is partitioned verification, not a claim that an earlier failing command returned success: retain all eight passing BacktestMatrixExecutionTests from linked-retention-final-full.trx, use the final non-matrix report for 3,947 other cases, and replace all three entry-clock theory rows with linked-entry-clock-final.trx. SHA-256 inventories confirm all 52 non-test DLLs were byte-identical across the test-only rebuilds. The largest matrix case ran 20 independent portfolio jobs and passed in about 12m31s.

Two further loaded-suite fixture issues were corrected without changing application code: recovered-worker tests now explicitly expire the acquired predecessor token in their own SQLite database and use the standard recovery lease; entry-clock tests now cancel synchronously after a completed poll and assert that completion before checking all original price/admission/audit outcomes. Independent B reviewed those deltas. Earlier failures, the interrupted run and the false five-minute diagnostic abort remain retained as evidence. Four profiling opt-ins were not enabled; actual regular-session broker fills/protection/exits also remain unverified because the market is closed. No broker write or overnight order was queued.
### Actual operator cutover and original news recovery

Independent A and B accepted final partitioned verification. Local commits are ad46d2c5d3ce98cd7f0a6473ff342cf7bb2ac4b1 and 91dd9e1d54c89928025d4adbae34424630d11618. Release publish completed successfully. The deployed Web DLL SHA-256 is 893186a2ae4322912303a7fd78466789cc64d4cd429a8c38c25081514d15cd10. No GitHub operation occurred; Fable's audit document remains separately modified and unstaged.

At 13:41 UTC the configured paper account matched, ACTIVE/unblocked, with HTTP 200 on four read-only GETs, zero open orders and zero positions. Operator storage had zero active jobs and zero order intents. Verified original PID 34052 on port 10088 was stopped after this flat/inactive check. The listener took a moment to disappear; the first backup attempt stopped before copying until port closure was confirmed. The post-drain standalone DELETE-journal backup is 512,036,864 bytes, integrity_check=ok, 28 migrations, SHA-256 7d22647c6ee35740e8c608202c2d25787e4ca7f12f28a7bdb176f0d1a6996683. Both old site binary folders and both operator backups are retained.

The reviewed build started with exact TRADINGFLOW_CODE_VERSION=91dd9e1d54c89928025d4adbae34424630d11618, TRADINGFLOW_DISABLE_RUNTIME=true, TRADINGFLOW_ENABLE_MARKET_FEEDS=true and UI test mode false. The actual V: database now has migration 20261114120000_AddProductionHostRunLease, exactly 29 applied migrations. The final post-recovery comparison covered 43 original tables and passed protected-data checks. All original saved lists, Finviz presets, identity records, execution journals and 221,778 pre-cutover earnings analysis rows remained; feeds appended new analysis/news and refreshed their calendar projections. No original calendar IDs were removed. These checks concern the actual operator DB, not the rehearsal copy.

The initial backup contained 9,921 NewsItems. The previous app continued its preexisting 72-hour expiry before cutover, removing 729 original IDs (294 Alpaca, 435 Finviz) while adding 17 new ones; the post-drain net count was 9,209. A independently verified exact missing IDs, identical ten-column schema and no triggers/foreign keys. The new paused host was briefly drained for a single-writer recovery transaction. Exact missing rows were inserted from the read-only initial backup without replacing any existing ID: 729 inserted, destination 9,287 to 10,016 at transaction completion, all 9,921 initial IDs present, preexisting destination rows byte-for-byte unchanged. The new build has no automatic news-expiry method. Both recovery and migration evidence are retained.

Final host PID 20760 listens on port 10088. Health is 200; trading readiness is 503 with entriesAllowed=false and inactive order reconciliation/recovery, as expected for this explicitly paused host. A real signed-in browser verified AAPL SIP candles/current labelled zones and New York/Berlin/UTC clocks, News with 66 matching records/39 grouped articles, and 59 next-reporting-day Earnings events (two reported). Earnings initially showed zero while its first monitor cycle ran, then displayed all 59 with analysis at 13:48:47 UTC; this startup-empty presentation belongs in the UI review. The preview's source lists/presets were unchanged from its seed, its browser tab was moved to the main app and its owned PID 2956 was stopped. Its copied data and provider captures remain preserved.

Independent A rehashed the physical backup and deployed Web DLL, checked all recovery/comparison receipts, and accepted the paused local cutover. B separately accepted the source and complete final test inventory. No actual broker fill, protective order or exit was exercised on this closed Sunday; next regular open is Monday 16 November, 09:30 New York / 14:30 UTC / 15:30 Berlin. Working broker-native linked remainders still require review because the broker does not provide an atomic cancellation that preserves protection; this is a current provider limitation, not an old-version compatibility path.

Other observed issue for the next workflow review: some existing completed backtest records reference missing C:/project/trading_flow/data/... durable result markers and generate retry warnings. The original records and artifacts were not rewritten or discarded. New research runs and the verified plan-192 replays remain distinct from those old references.

Cleanup: deleted only the owned false-hang minidump from testhost 10972, freeing 14,367,783 bytes. Test fixtures cleaned their successful synthetic data automatically. Retained TRXs, logs, sequence, per-case inventory, hashes, operator backups, prior binaries, copied preview records and all original/provider/research evidence. The user-requested main app remains running. Exported acceptance summary: TradingFlow-local-acceptance-2026-11-15.json.
### How-to guide and critical workflow/indicator review completed

Created `docs/user-guide.md` and `docs/design-review/2026-11-15/daily-workflow-and-chart-indicators.md`. The guide follows sign-in, stock groups/Finviz, preparation, stock/chart inspection, saved-plan backtesting and monthly results, authorized paper monitoring, loss review and session close. It distinguishes installed controls from proposed indicators, explains the paused preparation worker, and uses existing plan 192 as a comparison example without starting a new economic run.

Read-only signed-in UI review confirmed the buried preparation panel, a saved evening list requiring morning evidence, actual Lab preview/save/start controls and successful monthly results for plan 192. The final Operations navigation timed out at the browser layer; no additional visual check is claimed. A subsequent direct health GET returned 200 and the existing main PID 20760 was still running. The app remains paused at deployed build 91dd9e1; this documentation checkpoint changes no runtime binaries, trading state or provider data.

The proposal prioritizes an honest runtime/feed status strip, preparation visibility and server-side worker guards, clear pending/failed/empty feed states, simpler Desk and Lab flow, explicit missing-artifact status, then optional SMA/EMA/RSI/MACD/ADX/ATR/Bollinger/OBV/RVOL displays and daily structural context on intraday candles. It retains current-only zone geometry, source budgets and permissions, specifies stable recursive seeds and availability joins, and adds bounded derived memory/work/payload acceptance. Forming/weekly candles, private trade markers and advanced volume-profile/anchored-VWAP work remain separately scoped.

Independent A `/root/linked_final_plan_review` approved the guide and proposal for presentation after worker-state, feed-evidence, UI-only Lab, payload and measured-cache clarifications. Independent B `/root/linked_code_review` separately approved after seed/paging, current-only zones, daily availability, RVOL, authorization, memory/fanout and paused-exit clarifications. B's final guide wording fixes (new versus existing workflow save labels, interval-specific overlays, opening versus exit partial fills) were applied. Neither reviewer implemented this checkpoint; the guide author was separate.

Verification tier: prose only. Checked local reference targets, whitespace and `git diff --check`; no new tests, builds, backtests, collection requests or broker actions were needed or executed. New UI/indicator implementation is not claimed. Repository AGENTS.md requires presenting the concrete design and obtaining explicit approval before implementing it. The user-facing guide and proposal are exported as `TradingFlow-how-to-guide.md` and `TradingFlow-UI-and-indicators-plan.md`. Separate reviewers remain required for each implementation checkpoint. No GitHub sync.

This documentation pass created no disposable scratch files; 0 additional bytes deleted. Retained the guide, proposal and all earlier acceptance evidence. Earlier verified cleanup freed 14,367,783 bytes; the requested main server remains running.
