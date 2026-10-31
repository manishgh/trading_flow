# Swing execution stage 04A verification — 9 October 2026

Stage 04A repaired actionable decision timestamps and audited entry-session prices for the two frozen cycle03 recipes. It did not implement the full paired five-minute execution replay described as Stage 04B in `docs/research/swing-execution-stage04-2026-10-09.md`.

Evidence root: `C:\Users\manis\Documents\Codex\2026-10-03\c\outputs\swing-execution-stage04-20261009`.

## Changed behavior

The shared decision kernel previously dated some mixed-timeframe orders from the previous session's execution context, before their daily setup or delayed evidence was available. Action time now cannot precede completed execution context, setup availability or decision as-of time. Existing known-at validation rejects future evidence.

`StrategyOrderPlan` explicitly binds the stop context index and source timestamp. The execution-signal assembler checks that context rather than subtracting a timeframe from a later decision timestamp. The risk planner uses the actionable timestamp for the finalized order; historical evidence timestamps remain historical. The inactive eToro router hashes the finalized timestamp, so this changes that legacy hash input. No broker orders were sent.

## Focused component verification

Four new counterexamples failed before the repair. Final C# verification passed **124 tests, zero failures**, with no build warnings or errors:

```powershell
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter 'FullyQualifiedName~MixedTimeframeDecisionTimingTests|FullyQualifiedName~StrategyDecisionKernelTests|FullyQualifiedName~CompletedBarExecutionPlannerTests|FullyQualifiedName~StrategyOrderPlannerTests|FullyQualifiedName~ChronologicalBacktestExecutionCoordinatorTests|FullyQualifiedName~DeterministicExecutionSimulatorTests|FullyQualifiedName~OwnedOcoExecutionTests|FullyQualifiedName~BacktestSimulationCharacterizationTests|FullyQualifiedName~MobileAutomationServiceEntryGateTests|FullyQualifiedName~LiveRunnerIntegrationTests|FullyQualifiedName~EntryDecisionPersistenceTests' --verbosity minimal
```

The added cases cover ordinary daily availability, prior-session context, early-close/holiday/weekend delay, delayed known-at evidence, mismatched context, and kernel-to-assembler-to-risk-planner propagation. Existing downstream execution tests cover later-bar fills, protection, partial fills and OCO behavior. Passing these tests does not establish immediate protection within the entry slice.

The observational Python classifier passed **8 unit tests**. `replay-source/verify_and_report.py` independently compared every retained case with the downloaded bars and original source artifacts. `replay-source/verify-daily-controls.py --complete` checked the two control manifests and all 18 artifact references, 46 fill events, 254 daily equity observations and 14 monthly rows. Maximum difference from the rounded final summary was $0.000133. Verification receipts retain the checks and hashes.

Two actual daily replays used the unchanged RSI5 and MR broad-stretch/SMA5 artifacts at the original base cost, with the original cohort-1 eligibility (four eligible work items per strategy; no replacement for the fifth member). Their 6 and 17 completed trades match cycle03 in every published trade field except run-dependent candidate ID. All **14 monthly return and drawdown rows match exactly**, including exact strategy/version/content hash. The other cohorts and stressed daily controls were not rerun after this timing repair.

## Observational result

All 288 frozen cases from five original cohorts were audited, representing 144 distinct stock/session observations; the two cost settings are correlated. All 21 refreshed native daily series match the original canonical hashes. Of 288 cases, 278 have complete regular-session five-minute coverage. Ten cases represent five DIOD sessions with missing intervals and cannot support a no-touch conclusion.

Seven of 108 base-cost mean-reversion entries have an observed initial-stop touch later on the entry day; the same seven appear at stressed cost. RSI5 has no observed touch in its 36 entries at either cost. There were no first-five-minute touches, target-first observations, both-level slices, or session opens at/below the stop in this sample. The daily simulation omitted protection for the entire entry slice. Observed crossings establish omitted exposure, not active orders, quantities, fills or counterfactual returns.

The assessment lists the seven sessions. Published cohort audits retain exact strategy identities, source-candidate and fill witnesses, unrounded stop/target levels, source hashes, session windows, gaps, extrema and observed touch bars. Monthly audit counts are counts, not performance estimates.

## Review, retention and remaining scope

Independent requirements and code reviews are recorded in the evidence root's `review-receipt.json`. Owned captured inputs and temporary driver files are deleted only after review, with the exact paths, counts and retained-artifact checks in `cleanup-receipt.json`. Published results, immutable configurations, manifests, unsuccessful/inconclusive observations and prior-cycle evidence remain preserved. No operator database or wishlist was written.

Stage 04B remains unimplemented. It must separate execution resolution from the daily setup, structural stop inputs, SMA5 exit and five-session holding clock; merely changing the strategy timeframe changes the strategy. It must define entry-slice protection causally, preserve terminal-session mapping, handle missing opening intervals, gaps, partial quantities and shared liquidity, and then run the frozen 20 paired treatments. Historical quotes were not captured, so there is no executable bid/ask or spread-calibration claim. Current-roster selection, retrospective price adjustments and missing first-receipt lineage remain diagnostic limitations.

This is a component research checkpoint, not a release or promotion. Full-suite, Android, deployment, external broker and paper/live activation checks were deliberately not run. The decision remains **RETAIN_RESEARCH**. No strategy or canonical configuration was promoted, and no GitHub synchronization was performed.
