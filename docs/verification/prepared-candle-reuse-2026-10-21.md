# Prepared-candle reuse — 21 October 2026

Implements the separately approved [design 08](../design-review/2026-10-19/08-prepared-candle-reuse-2026-10-21.md). The user approved implementation on 21 October. This is a component checkpoint for storage, admission, execution integration and client evidence. Verification below uses isolated synthetic data; no operator provider collection, research execution, trading, deployment or core-data cleanup was performed.

## Implemented behavior

The existing cache now retains immutable candle/calendar versions under the original configured `NormalizedRoot/_historical`. Exact request identity includes source, feed, price basis, endpoint/parser/calendar contract, UTC bounds and source omission semantics. Refresh appends versions. Every original response and receipt remains under the original configured raw archive owner. Existing older cache files remain retained.

The existing durable backtest workflow freezes one complete data request for every strategy/cohort comparison. Admission validates the deduplicated source queries and configured bounds before accepting work; it does not collect data. The first worker prepares every query before strategy evaluation, recording each selection immediately for partial recovery. One completed manifest is bound through the existing application event store. Every book verifies that binding and holds verified file read leases; changed or missing evidence fails without redownload or latest-version substitution. Each strategy/cohort keeps its own capital and monthly results.

Standalone runs get fresh execution identities. Nested optimizer runs inherit the parent's selected data. Rolling dates are frozen once; resumed typed/file runs use the verified admitted request's cutoff. Original CSV diagnostics retain their existing file-declared execution path without claiming captured provider coverage. Finviz still explicitly rejects historical OHLCV requests because its adapter does not supply candles.

No new operator settings or screens were added. Existing API job/detail contracts, Web running/results panels and Android Backtests display preparation stage/failure, selected snapshot ID/hash, provider/feed, price basis, requested UTC coverage, preparation time, reused/downloaded counts and the historical-revision limitation. Private filesystem paths are not included in this public summary.

Only private `.run-inputs-*` projections/journals/spills and exact unaccepted attempt-owned parameter files are cleaned. Rejected admission cleanup verifies expected path, hash, filesystem links and durable winner/sibling ownership. Accepted parameters, committed versions, raw originals and published outcomes remain retained. The same rules apply on failure/cancellation.

## Focused checks

| Check | Result | Evidence and limits |
|---|---|---|
| Focused storage/service/admission/execution/contract/cleanup tests | **80 passed, zero failed/skipped; 34 seconds** | `.tmp/verification-20261021/prepared-candles-verified.trx`; affected backend hosts compiled with analyzers. Real workflows and engine with synthetic sources/HTTP only. |
| Android Debug | **Passed; zero warnings/errors; 1 min 28.38 s** | `.tmp/verification-20261021/prepared-android-debug.log`. SDK packaging required execution outside the filesystem sandbox; initial sandbox attempts compiled managed code but exited without an SDK error. No installation/device check. |
| Android Release | **Passed; zero warnings/errors; 1 min 44.16 s** | `.tmp/verification-20261021/prepared-android-release.log`. No installation/device check. |
| Larger cohort and SIP integration | **182 passed; one old fixture corrected and its complete file rerun passed** | `.tmp/verification-20261021/prepared-candles-integration.trx` (13 min 6 s). Actual 20-, 12- and six-book comparisons, native/SIP books, interruption recovery, monthly publication and affected cache/provider/workflow consumers passed. |
| Final corrected SIP fixture and workspace consumers | **16 passed, zero failed/skipped; 2 seconds** | `.tmp/verification-20261021/prepared-candles-final-fixtures.trx`; full affected source-binding test file plus workspace tests, after correcting its resolved-universe fixture. Backend recompiled successfully. |
| Browser preparation/workflow checks | **14 distinct scenarios passed with latest outcomes** | Combined run: 13 passed / one synthetic UUID fixture failed (49.4 s); corrected preparation file: all three passed (12.9 s). Existing workflow scenarios include 412/1440-pixel views, retry/UTC dates, source/profile capture and save/start separation. Hosts stopped and owned databases/cache/artifacts deleted. |

Across the three final .NET reports there are **262 distinct test cases with passing latest outcomes** (deduplicated by full parameterized test name); counts overlap and must not be added.

The 80 focused checks include 12 immutable-version cases, 18 preparation/service cases, two real API/execution cases, two real optimizer cases, two SQLite event-binding cases, six request-cleanup cases, three wire-contract cases, six runner retention/clock cases, plus affected plan/job/CSV optimizer checks. The API test starts four actual books sharing two stocks and two strategies: exactly two bar queries and two distinct calendar queries are captured once; the next eligible comparison reuses all four versions without recapturing. The optimizer tests use normal configured Alpaca adapters behind fake HTTP, run two parameter permutations, and verify one shared selected-data hash for both configured and absent rolling end dates.

A separate direct/prepared comparison checks complete trade records (candidate IDs, entry/exit times, fill prices, quantities, fees and profit), decision timestamps/states/hashes/transitions, processed counts and exact monthly rows with the original strategy/cohort identity. This demonstrates parity for deterministic fixtures, not market profitability. A conflicting prepared calendar fails before complete snapshot/binding publication and preserves the frozen SIP calendar bytes.

Other focused cases cover retained previous versions, raw/all identity separation, exact ranges, concurrent selection, complete multi-page responses, repeated page tokens, cancellation during archival, row budgets, malformed OHLC rows, changed/missing evidence, refresh recovery after cancellation, binding failure recovery, read leases and closed calendar dates.

The 50-stock / groups-of-ten / four-strategy comparison executed all 20 portfolios in 9 min 0.29 s. The 30 / five / two comparison executed 12 portfolios in 2 min 52.94 s; the 11 / five / two leftover comparison executed six in 1 min 3.16 s. These are complete synthetic Debug execution times, not provider-download benchmarks or predictions for market research.

## Reproduction commands

From the repository root, the focused check was:

```powershell
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter "FullyQualifiedName~PreparedMarketDataVersionTests|FullyQualifiedName~PreparedMarketDataServiceTests|FullyQualifiedName~PreparedMarketDataContractTests|FullyQualifiedName~PreparedComparisonExecutionTests|FullyQualifiedName~PreparedOptimizerExecutionTests|FullyQualifiedName~PreparedMarketDataEventBindingTests|FullyQualifiedName~PreparedRequestStagingCleanupTests|FullyQualifiedName~BacktestPlanTests|FullyQualifiedName~RunWorkflowIntegrityTests|FullyQualifiedName~BacktestJobServiceTests|FullyQualifiedName~BacktestProviderRetentionTests|FullyQualifiedName~StrategyOptimizerTests" --logger "trx;LogFileName=prepared-candles-verified.trx" --results-directory .tmp/verification-20261021
dotnet build src/TradingFlow.Mobile/TradingFlow.Mobile.csproj --no-restore -c Debug -v minimal -flp:logfile=.tmp/verification-20261021/prepared-android-debug.log
dotnet build src/TradingFlow.Mobile/TradingFlow.Mobile.csproj --no-restore -c Release -v minimal -flp:logfile=.tmp/verification-20261021/prepared-android-release.log
```

The broader integration and corrected fixture checks were:

```powershell
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~BacktestMatrixExecutionTests|FullyQualifiedName~AlpacaMarketDataProviderTests|FullyQualifiedName~AlpacaMarketSessionScheduleProviderTests|FullyQualifiedName~CachedMarketData|FullyQualifiedName~RawArchiveWriterTests|FullyQualifiedName~EventPortfolioSourceBindingTests|FullyQualifiedName~BacktestArtifactIntegrityTests|FullyQualifiedName~WorkflowConfigurationTests|FullyQualifiedName~WorkflowReplayTests|FullyQualifiedName~DurableJobWorkerHostedServiceTests|FullyQualifiedName~BacktestRunnerExecutionQualityTests|FullyQualifiedName~StrategyPriceBasisTests|FullyQualifiedName~Monthly|FullyQualifiedName~RunWorkflowIntegrityTests|FullyQualifiedName~ConfiguredDataProvidersTests" --logger "trx;LogFileName=prepared-candles-integration.trx" --results-directory .tmp/verification-20261021
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter "FullyQualifiedName~EventPortfolioSourceBindingTests|FullyQualifiedName~BacktestRunWorkspaceTests" --logger "trx;LogFileName=prepared-candles-final-fixtures.trx" --results-directory .tmp/verification-20261021
```

From `tests/ui`:

```powershell
node node_modules/@playwright/test/cli.js test specs/prepared-candle-evidence.spec.js specs/workflow-integration.spec.js
node node_modules/@playwright/test/cli.js test specs/prepared-candle-evidence.spec.js
```

`node --check` passed for `lab-progress.js` and the new browser spec; `git -c core.whitespace=cr-at-eol diff --check` passed. Test-only browser UUID creation now uses the imported Node function rather than a secure-context-only browser API on its synthetic blank page. No application behavior was changed for this correction.

The Release merged Android manifest retains `android:usesCleartextTraffic="false"`.

## Review corrections and failed checks

Independent engineering and plan reviewers found and rechecked fixes for fresh standalone identity, one rolling clock (including admitted recovery), ordinary CSV preservation, complete pre-admission query validation, rejected staging ownership, and meaningful execution/optimizer/calendar proofs. Both final source reviews reported no remaining concrete finding; source review is separate from executable verification.

The first component run passed 44 and failed nine: eight test fixtures omitted the new immutable-request admission method, and one expected only one calendar query where the actual engine needs two exact date ranges. Later compile checks caught nullable/test-analyzer errors. A subsequent run passed 76 and failed four fixture assertions: two optimizer and one comparison test looked under the profile's original normalized path rather than the accepted run's actual configured path; the typed recovery test lacked a resolved universe. These tests were corrected using actual accepted configuration/ownership; production admission or execution checks were not relaxed. The final 80-check receipt above is after those corrections. The broader integration also found an older SIP admission fixture with no resolved stocks. It now declares its synthetic resolved AAA universe and owns its raw/normalized/result directories; its full source-binding test file and workspace tests passed afterward (16 cases). Original manifest-mutation assertions remain in place. Earlier reports remain checkpoint evidence and their counts overlap; do not add them together.

## Scope limits

Original historical revision availability is not established by downloading old bars today. Actual capture times and this limitation remain explicit. Frozen SIP quote/trade sources retain their separate binding and execution authority. News, earnings and sector histories are outside this candle-only selection. Price-basis, missing-interval, fill, cost, risk and strategy-promotion rules were not relaxed. No trading activation or provider-backed research was performed; the earlier market research limitations are unchanged. This checkpoint does not claim device behavior, external deployment or a full-suite release verification.
