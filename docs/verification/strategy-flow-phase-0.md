# Strategy Flow Phase 0 Verification

Date: 2026-08-27

Status: implemented, independently reviewed, and approved.

## Scope

- Removed autonomous prototype news-to-order, ML portfolio-advisory, mock-sector,
  hardcoded swing, and ML-ranked premarket workflows.
- Retained polling and Alpaca WebSocket news ingestion.
- Centralized the Alpaca news WebSocket endpoint.
- Made base wishlist profiles explicit unresolved templates and generated runs
  explicit resolved snapshots.
- Resolved Finviz-only and mixed paper universes before admission, froze both
  per-source symbol memberships and the normalized query, and disabled runtime
  re-screening for the admitted run.
- Reconciled the current lifecycle decision to `RETAIN_RESEARCH`.

## Characterization Baseline

The following existing tests freeze behavior before the later refactor phases:

- completed-bar / later-fill timing:
  `CompletedBarExecutionPlannerTests.Plan_BacktestConfirmation_UsesNextBarAsFill`
- pre-event point-in-time bars:
  `CatalystTechnicalEventStudyRunnerTests.Analyze_UsesOnlyBarsCompletedBeforeProviderEventTime`
- no breakout-bar formation leakage:
  `VcpSwingPivotAnalyzerTests.AnalyzeCompletedBar_DoesNotUseBreakoutBarToFormFinalPivot`
- account-risk sizing with costs:
  `SharedOrderRiskPlannerTests.Plan_WithSlippageAndFixedFees_IncludesBothInAccountRisk`
- exchange-time same-slot and cumulative RVOL:
  `IndicatorEngineTests.Compute_UsesExchangeTimeSlotForRelativeVolumeAcrossDst`,
  `Compute_UsesComparableCumulativeSessionVolumeForPrimaryRelativeVolume`, and
  `Compute_UsesMultiSessionAverageRatherThanYesterdayOnly_ForSlotRelativeVolume`
- decision audit emission:
  `LiveRunnerIntegrationTests.RunAsync_BatchedCandlePipeline_WritesMetricsAndAuditsEndToEnd`
- positive news cannot bypass the technical trigger or reach an order write:
  `LiveRunnerIntegrationTests.RunAsync_PositiveNewsWithoutTechnicalTrigger_CreatesNoOrderIntentOrBrokerRequest`
- intent persistence/idempotency:
  `OrderSubmissionServiceTests.SubmitBracketOrderAsync_PersistsIntentBeforeBrokerNetworkCall`
  and `SubmitBracketOrderAsync_AcknowledgedRetry_SuppressesDuplicateBrokerCall`

## Commands

```powershell
dotnet test src\TradingFlow.Tests\TradingFlow.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~ProductionCompositionTests|FullyQualifiedName~RunUniverseValidatorTests|FullyQualifiedName~LiveRunnerIntegrationTests|FullyQualifiedName~RunConfigParsingTests|FullyQualifiedName~AlpacaEndpointResolverTests|FullyQualifiedName~PaperRunUniverseSnapshotResolverTests"
```

Result: 62 passed, 0 failed.

```powershell
dotnet test src\TradingFlow.Tests\TradingFlow.Tests.csproj --no-restore --nologo
```

Final post-finding result: 1,101 passed, 0 failed.

```powershell
dotnet run --project src\TradingFlow.Web\TradingFlow.Web.csproj --no-build --urls http://127.0.0.1:53014
```

Web smoke: `/Login` 200, anonymous `/Earnings` 200, and protected `/Paper` 302 to
`/Login?ReturnUrl=%2FPaper`. The final process ID is recorded after the last restart.

## Review

- Plan/research reviewer: initial verdict rejected; requested real composition,
  end-to-end positive-news safety, enforceable wishlist templates, and corrected
  documentation. Findings implemented.
- Code/design reviewer: initial verdict rejected for the same safety gaps plus
  documentation inconsistencies. Findings implemented.
- Final research/requirements review: approved with no remaining findings.
- Final code/design review: approved after independently reproducing 62 focused
  and 1,101 full passing tests, a clean diff check, and provenance round-trip.

Commit SHA: pending the final green gate.
