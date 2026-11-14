# TradingFlow audit repair plan and execution record

Date: 14 November 2026 (Europe/Berlin)
Owner: Codex, implementing the user's authorized reporting and paper lifecycle repairs.
Baseline: `2cd528e`; clean working tree before this task. No TradingFlow runtime process was active at the initial check.
Source: [Fable audit](../research/tradingflow-audit-2026-11-14.md).

## Current status

**Complete for the authorized repair checkpoint.** Reporting R1-R3, paper lifecycle L1-L4, and reversible strategy availability S1 are implemented and accepted by separate plan/execution and code/design reviewers. Final verification: 555 passed, 0 failed, 0 skipped; Android build passed with zero warnings/errors. Only sector rotation is parked. Monthly execution remains deferred by the user.

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