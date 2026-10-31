# Stage 04B component verification — 9 October 2026

Evidence root: `C:\Users\manis\Documents\Codex\2026-10-03\c\outputs\swing-execution-stage04b-20261009`.

The frozen matrix contains exactly two cycle03 strategy artifacts at two cost settings across five original cohorts. The original 25-stock roster and its prior eligibility remain unchanged: 22 eligible stocks are replayed; no stock replaces one without adequate history. All 23 refreshed daily series, including SPY, match cycle03 canonical hashes. Forty-five series and the authoritative calendar are captured in isolated temporary storage.

## Implementation

`engine.execution_replay_policy_id: daily-rth5m-nextslice-ioc-v1` separates fill resolution from the unchanged daily strategy artifact. Native daily evidence still controls setup, stop context, SPY regime, selection and technical/time exits. The first subsequent daily bar has held count one; entry-day daily exit evaluation remains skipped. Protective prices no longer truncate daily technical-exit planning. The existing shared technical/time evaluator supplies the exit reason.

The execution book consumes full regular-session five-minute slices, including periods excluded from new-entry admission. IOC entry leaves only its filled quantity open. Protection begins on the next eligible slice after the completed entry slice; the first slice is unprotected. This timing assumption is not necessarily pessimistic for returns. Delayed five-minute availability is explicitly unsupported and fails before orders can be backdated. Daily delayed exit evidence uses the mapped session-open clock, while precommitted terminal liquidation remains independent of future technical evidence.

Activated stop-market remainders remain activated after partial or zero-capacity attempts. A protective partial cannot suppress a scheduled residual exit. Scheduled or working market exits do not install conflicting brackets. The terminal opening proxy uses prior completed-slice capacity; any unresolved terminal quantity makes the treatment incomplete, with no later liquidation.

Required missing/invalid intervals abort economics. Deadline checks run before any symbol advances beyond an unresolved required slice, and failure records preserve every active exposure. Native duplicate rows are rejected by the isolated provider before pipeline deduplication. REST omissions are explicitly unknown, not declared no-trade evidence.

## Checks

Final affected-component test command:

```powershell
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter 'FullyQualifiedName~FineExecutionReplayTests|FullyQualifiedName~ChronologicalBacktestExecutionCoordinatorTests|FullyQualifiedName~DeterministicExecutionSimulatorTests|FullyQualifiedName~OwnedOcoExecutionTests|FullyQualifiedName~PortfolioCloseValuationTests|FullyQualifiedName~TechnicalExecutionEngineTests|FullyQualifiedName~MixedTimeframeDecisionTimingTests|FullyQualifiedName~BacktestSimulationCharacterizationTests|FullyQualifiedName~CompletedBarExecutionPlannerTests|FullyQualifiedName~StrategyDecisionKernelTests|FullyQualifiedName~StrategyOrderPlannerTests|FullyQualifiedName~LiveRunnerIntegrationTests|FullyQualifiedName~EntryDecisionPersistenceTests|FullyQualifiedName~MobileAutomationServiceEntryGateTests' --verbosity minimal
```

Result: **161 passed, zero failed**. The isolated `Stage04B.csproj` build passed with zero warnings/errors. The run freeze records 952 source/binary hashes, immutable configuration identities, input hashes, review scope and execution capability limits. Compiler errors and two initially misconfigured friction expectations were corrected before final verification; no known test failure was waived.

Counterexamples cover first-slice versus later protection, adverse stop/target ordering, stop gaps, IOC remainders, activated partial stops, zero participation capacity, partial scheduled exits, delayed evidence, missing openings and internal intervals, complete abort exposures, duplicate/invalid slices, extended hours, early close/holiday calendars, daily SMA5/five-bar clocks, terminal partials and explicit daily valuation sampling.

The driver executes one original cohort at a time, retains incomplete treatments, and skips their monthly returns. `PortfolioCloseValuation` explicitly samples the original daily grid while accounting for intervening intraday fills. `verify_results.py` checks unchanged daily decisions, manifest lengths/hashes, fees, cash, positions, native daily marks, monthly percentages/drawdowns, missing observations and all failed open exposures. See `verification.json` for final treatment counts and exact evidence rather than interpreting numeric placeholders in incomplete summaries as zero returns.

## Independent review and retention

Independent requirements and code/quant reviews preceded the trials; final result review and cleanup are recorded in `review-receipt.json` and `cleanup-receipt.json`. The result assessment presents monthly strategy/cohort comparisons only. Published results, manifests, immutable parameters, failures and compact execution witnesses remain retained. Run-owned market inputs and the temporary driver are deleted after review. Operator databases, wishlists and canonical strategies were not written.

This is a research component checkpoint. Full-suite release checks, Android, deployment, provider quote calibration and paper/live activation were not run. The viewed window, current roster, retrospective adjustments and missing first-receipt lineage remain limitations. The disposition remains **RETAIN_RESEARCH**.

## Final outcome

All 20 treatments were attempted: 14 complete, six incomplete, with 98 monthly rows. All 10,568 daily decisions match the baseline and 188 artifact references verify. Independent quant review reconciled 478 fills, 222 closed trades and 40,730 active regular-session slices. RSI5 cohort 1 fails on the BKSY August 26 16:15 UTC missing slice; all four cohort-2 treatments fail on DIOD May 21 16:00 UTC. No return rows were published for those treatments.

After result verification, the coordinator and technical execution engine had their original LF endings restored. Their exact executed source copies and the byte-normalization proof are retained in the evidence root. Original executed-code hashes remain unchanged and refer to those archived executed bytes; the final normalized files have separately recorded hashes. No executable statement changed.
