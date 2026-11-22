# Design: remove dead code with its tests, shrink wasteful tests, speed up the suite

Requested by the user on 22 November 2026 ("large number of tests … majority of them are old and could be even dead code").
**Status: the user said on 23 November 2026 "remove all dead code and dead tests"; section 5 records what was removed and kept.
Steps 2, 3 and 4 still wait for a decision.**

## 1. What was measured

Static check of `main` at `ecec0bc`: every production file was followed from every entry point (all `Program.cs`, Razor pages, XAML
pages, tools, migrations). A type is called dead only when no file outside the tests names it. Nothing was run. Reflection was
not checked; the build and the full suite after each step are the proof.

| | Files | Lines | Cases |
|---|---|---|---|
| Production code (migrations included) | 966 | about 216,000 | |
| Tests | 403 | about 103,000 | 4,507 in the last run (4,499 passed, 2 failed, 6 skipped) |
| Production files nothing reaches | 53 | about 6,900 | |
| Tests whose subject the website reaches | 319 | | about 3,744 |
| Tests of website code plus CLI, Android or research code | 57 | | about 582 |
| Tests mostly of code outside the website | 10 | | 89 |

Age: 56 of 403 test files were last changed in August or earlier, 313 in October or November. So the majority is neither old nor
dead. What is true: there is dead code (step 1), there are features no strategy uses any more (step 3), and the suite is slow
for reasons that have little to do with the number of cases (step 4).

## 2. Steps

Each step is its own commit, with a build, the full suite, and two independent reviewers. No data, published result,
fixture that pins a published hash, or database table is touched. Deleted code stays in git history.

### Step 1a. Dead code and its tests: remove (recommended)

| Remove (production) | With its tests | Cases |
|---|---|---|
| `Web/Services/UniverseRankService.cs` and its registration in `Program.cs:410` | `UniverseRankServiceTests`; the shared enum part of `PredictorEvidencePresentationTests` moves to what it still tests | 27 |
| `Engine/Catalysts/CatalystConfirmation.cs`, `CatalystEligibilityService.cs` | both test files | 11 |
| `Domain/Backtesting/PromotionEvaluator.cs` | `PromotionEvaluatorTests` | 5 |
| `Backtesting/Research/SwingResearchAnalyzer.cs`, `Domain/Backtesting/SwingResearchReport.cs`, `ReversionResearchAnalyzer.cs` | both test files; the audit note of 21 October keeps its text | 9 |
| `Web/Services/DeskGridQuery.cs` | `DeskGridFilterTests`, the filter part of `DeskUniverseEvidenceTests` | 4 |
| `Web/Services/MobilePaperRunRequestValidator.cs` | `MobilePaperRunRequestValidatorTests` | 3 |
| `TradingFlow.Etoro` (whole project, 22 files, 1,171 lines, in no solution) | none; `ProductionCompositionTests` keeps its check that eToro is not loaded | 0 |
| `MacroSentimentParser`, `SipReviewedConditionPolicy`, `ExternalSignalValidationResult`, `IMarketDataStreamer`, `IOrderRouter`, `CheckUsers.cs` in the repository root | none | 0 |
| **Total** | | **about 59** |

Kept although the check calls them unreached: `StockChartLinkTagHelper` (used by attribute), the two `SharedOperatorContracts.cs`
and the design-time database factory (used by tools).

### Step 1b. Dead code in sensitive areas: the user decides

| Code (nothing calls it) | Cases | Recommendation |
|---|---|---|
| `EntryBarExitResolver`, `ExecutionRealismModel`, `FixedTargetCostPlanner` (old fill and cost helpers) | 20 | Remove: the live fill and cost code is elsewhere and has its own tests |
| `EventBacktestExecutionReplay` | 11 | Remove; note it in `sip-event-execution-v1.md` |
| `EvidenceSentimentAssessmentBuilder` | 15 | Remove; the sentiment proposal keeps its text |
| `SwingResearchPromotionEvaluator` | 4 | Remove |
| Evidence backup, retention, orphan check | 15 | **Keep**: data protection, meant to be used |
| `PointInTimeUniverseBuilder`, universe publication, exchange-session resolvers | about 31 | **Keep**: needed if historical membership is published |
| Promotion authorizer, ground-truth promotion gate, holdout registry calls | about 20 | **Keep**: promotion and untouched-test-period rules |

Removing the first four rows: **50 cases**.

### Step 2. Shrink without losing a check (recommended)

- `RetiredStrategyConfigurationTests`: 47 rows expect the same "unknown key" error; one test that loops over the 47 keys. **−46**
- `NativeDailyResultIntegrationTests:120` and `NativeDailySessionLedgerTests:14` are identical: keep one. **−1**
- `WorkspacePagesTests`: eight large tests compare exact CSS, HTML and JavaScript text, down to pixel widths and the wording of a plan document.
  Keep the checks of behaviour and of "no hard-coded dates"; drop the text copies. They break on every design change.
  **about −4 cases, several hundred assertions**
- Delete or correct the browser specs that look for elements that no longer exist (`watch-desk.spec.js`, `stock-chart.spec.js`,
  `workspace-navigation.spec.js`) and the code in `watch-desk.js` and `desk-market-view.js` that still writes to them.

### Step 3. Features no active strategy uses: one approval each, later

None of these names appears in any file under `configs`. The engine still contains them, so tests and code can go only together.
Numbers by file name; shared files add more.

| Feature | Cases | Production lines (own files) |
|---|---|---|
| A. Continuation protection, decision hours, technical factor, drawdown, historical news diagnostics | about 241 | about 2,450 |
| B. Opening range and intraday portfolio simulation | about 238 | about 3,590 |
| C. ETF month-end and short-swing recipes | about 35 | mixed into shared files |

Effect: the retired strategies could no longer be re-run or re-checked with the current code, only from the old commit. Their results and
settings stay. Part B is tied to the daily ledger (`NativeDailySessionLedger` uses `IntradayPortfolioExecution`), so it
needs care. The retirement note of 21 November says this code stays under test; approving step 3 would change that note.

### Step 4. Speed and false failures (recommended)

| Test | Problem | Change |
|---|---|---|
| `BacktestMatrixExecutionTests` 50/20 and 30/12 rows | 20–28 minutes; ten-second worker lease fails on a busy disk | Keep the 11/6, 1/1 and SIP rows; large rows only on request |
| `EarningsMonitorTests:30` | Real 4 s wait against a 2 s limit | The existing test clock |
| `LiveRunnerIntegrationTests` (25 waits of 5–10 s) | Fails when the machine is slow | Stop on events, 60 s ceiling |
| `DurableJobProcessorTests`, `DurableJobRepositoryTests` | One-second leases, real waits | Test clock |
| `ChartEndpointFanoutTests` | 128 connections, 25 s limit | Only on request |

The large rows stay available with an environment variable, like the six skipped ones today.

## 3. Result

| | Cases removed |
|---|---|
| Steps 1a + 2 | about 110 |
| Step 1b (first four rows) | 50 |
| Step 3 (A + B + C) | about 515 |
| **Everything** | **about 675 of 4,507 (15 %)** |

Not proposed: orders, risk, account protection, no-look-ahead, fills and costs in use, database migrations, sign-in, published hashes.

## 4. Questions outside this list

- Android app (`TradingFlow.Mobile`, about 6,000 own lines, about 80 cases): still wanted?
- `TradingFlow.WarmupService` (about 1,200 own lines, 23 cases): the website warms its own data; still used anywhere?

## 5. Removed on 23 November 2026

The search was repeated at type level and for services that the site registers but nothing asks for, until it found nothing new.
**107 types and 25 methods, about 7,200 production lines; about 162 test cases and 18 test files.** In addition to section 2 steps
1a and 1b: evidence backup, retention and orphan check; point-in-time universe builder and publication; exchange-session
resolvers; desk preparation, desk evidence, screener verification and wishlist resolver services; unused transport records.

Tests of live code that only used a removed type as a vehicle were kept and adjusted (worker bound, calendar publication,
duplicate capture, event envelope, catalog interfaces).

Kept although nothing calls them:

| Kept | Why |
|---|---|
| `SwingResearchPromotionEvaluator`, `CatalystGroundTruthPromotionGate`, `ConfiguredPromotionPrincipalAuthorizer`, holdout registry calls | `operating-boundaries.md` requires the promotion and untouched-test-period rules |
| `PromotionEvaluator` with its tests (removed first, restored after review) | Promotion limits: sample size, out-of-sample, benchmark, walk-forward |
| `BacktestOpportunityShadowReplay` | Can answer what blocked signals would have done (VIX comparison) |
| `TradingFlow.Etoro` | `ProductionCompositionTests` says its dormant source must stay; the user decides |
| `MarketPredictorHttpClient.GetBatchAsync` | Published contract with the Market Predictor project |
| Android app records | The Android app was not built here |
| About 38 short methods used only by tests | Test access or older variants; the review continues |

Consequences the user should know: no code backs up the evidence catalog (`tradingflow.db` backups continue), and historical
universe membership, which promotion requires, must be rebuilt or restored from git (`32da8f8`) when it is needed. Section 1b had recommended keeping
both; the instruction "remove all dead code" decided it. `UniverseRankConfig` is still read from run files but no longer used.
