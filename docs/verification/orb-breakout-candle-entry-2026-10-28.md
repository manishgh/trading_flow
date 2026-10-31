# ORB completed-breakout entry and candle-stop comparison

User approval: 28 October 2026, stage 2 of `docs/design-review/2026-10-19/13-orb-loss-analysis-and-proposal-2026-10-27.md`. Implement a separate strategy version, reuse existing components/data, run the same eight stock portfolios, compare monthly results with both retained controls, and obtain independent quant review using Sol 6.1 High agents.

## Frozen behavior

- Keep the first 15-minute opening range, its body direction, and opening volume greater than the prior twenty-session opening-volume median.
- Use the existing outward-rounded full-tick opening trigger. A completed post-opening-range five-minute candle must close strictly beyond that trigger in the permitted direction.
- Commit the market entry after that candle closes, attempting the next bar opening. Earliest entry is 09:50 New York. No entry inside the signal candle and no knowledge of the next opening before commitment.
- Freeze the completed breakout candle's low for a long stop, high for a short stop, rounded outward to a valid submitted order price. No new arbitrary stop buffer is introduced.
- Size from already-known prices and the unchanged risk/notional limits. Rebase original R and the outward-rounded **3R** target on actual fills. A future gap cannot retrospectively cancel a committed order; retain existing gap-fill risk reduction, actual gap costs, lagged opening liquidity and adverse same-interval protection.
- Retain one pending intent/position per stock. Pending execution and any partial remainder belong to the original committed signal. Further setups require the declared fresh completed inside-range rearm after resolution; do not keep choosing later already-outside candles until one works.
- Keep two filled parent entries per session, continuation protection, same-day liquidation, costs and all other rules unchanged. No direction-selection or news change.

The explicit generic next-opening market entry mode and its clock validation are within this approved stage. Use the existing shared planner, simulation, accounting, strategy catalog, contracts and API/Web/Android presentation; no separate research runner, custom cohort code or one-off configuration.

## Comparison and preservation

October 1–26, 2026 inclusive, 18 New York sessions, a partial month. NVDA, SMCI, MXL, MU, TSM, POET, PATH and CRDO each have a separate $10,000 portfolio. Nominal risk 1%, maximum position notional 25%, one overlapping position, maximum two filled trades per stock/session. Keep the same saved execution-cost profile and the exact cached market/calendar versions.

Controls: `technical.opening-range-breakout@1.0.0` from corrected `outputs/orb-october-20261027/rerun-1`, and `@1.1.0` from `outputs/orb-order-rounding-20261028`. Retain both source definitions, canonical identities and published outcomes. New evidence belongs in `outputs/orb-breakout-candle-20261028`.

Before work, ten selected source hashes matched their prior receipts. A fresh preservation receipt covers all 160 files in both controls' sixteen publication directories. Core data, wishlists, credentials, all results and intermediates remain preserved; no cleanup is planned.

## Completion checklist

- [x] Read approval, repository instructions and binding operating boundaries.
- [x] Stop the verified paused Web process before code edits; retain existing ngrok for restoration.
- [x] Record source and both-control publication preservation receipts.
- [x] Assign Sol 6.1 High implementation, independent engineering and independent quant reviewers.
- [x] Complete modular shared implementation and focused tests, preserving both prior versions.
- [x] Resolve independent engineering and quant implementation findings.
- [x] Pass focused signal, execution, causality, risk, persistence and affected consumer checks; build Web.
- [x] Restore paused Web and verify all three identities and new execution description in the existing UI/API.
- [x] Save and run the ordinary eight-stock plan using existing history.
- [x] Independently reconcile actual signals, fills, protection, costs, accounting, monthly results and data reuse.
- [x] Verify preservation and publish the three-version monthly comparison.

This is a component/research checkpoint. No live orders, strategy promotion, full release claim or physical Android/APK test is authorized by this task. Additional rule optimization requires separate approval. Exact commands, results, run identities and reviewer findings will be recorded below when complete.

## First attempt and existing-data integration correction

The reviewed strategy implementation built and passed 220 focused tests. An initial xUnit assertion-style analyzer error was corrected, followed by one catalogue-count assertion updated from three to four day strategies; the final `orb-breakout-candle-verified.trx` has zero failures or skips. All three identities were verified through the API. New strategy canonical hash: `bc73189a1c26ae2741402c2ad84a3a3e88aec709d43253590f7c7fb3c6fdbcc4`.

Saved plan **32** admitted eight jobs. They all failed before any economic result publication on an Alpaca calendar HTTP 401. Their requests, statuses, failures, retained partial preparation and provider receipts are preserved; this is not a losing backtest and no performance is claimed for the failed attempt.

The independent diagnosis found the same requested calendar bounds and source settings as stage 1. The shared historical preparation requested an unused buffer through November 6 although the economic window ends October 26. The calendar captured October 27 at approximately 10:21 UTC was younger than 24 hours during the stage-1 run at October 28 10:17 UTC, but older than 24 hours during this attempt at 13:23 UTC. Because the buffer extends into the future, the ordinary freshness check rejected it and requested the provider again. Existing calendar and candle files are present and their hashes still match. This was not a new entry-mode cache-key mismatch.

The necessary existing-data integration fix keeps freshness rules unchanged: cap intraday replay calendar requests at the last required exchange-session date, derived from the exclusive window end minus one tick in the exchange timezone. Reuse one shared bounds calculation across preparation and execution, with an optional internal pipeline override; defaults for ordinary live, current and swing consumers remain unchanged. The existing immutable wider calendar can supply a verified historical subset with parent provenance. No new operator setting, credential change, custom runner or discarded source data is involved. Independent review and tests must cover bounds, default behavior and expired-parent historical reuse without HTTP before retrying plan 32.

## Final verification and successful retry

The bounds fix passed independent engineering and quant source review. It uses `BacktestCalendarBounds` consistently in prepared requests, the candle pipeline and regime calendar construction. Tests cover New York/DST and exclusive-end boundaries, omitted-source-date rejection, all three intraday modes in an eight-book preparation, 27-hour-old historical parent reuse without HTTP, original provenance retention, and unchanged current/future/explicit-refresh behavior. One new test fixture initially reused an already-started HttpClient incorrectly; reusing its existing provider fixed the fixture without changing production code or weakening its assertions. Failed check evidence was retained.

Final component command: **302 passed, zero failed/skipped**, with affected projects including Web built:

```powershell
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter 'FullyQualifiedName~OpeningRangeBreakoutTests|FullyQualifiedName~OpeningRangeWorkflowIntegrationTests|FullyQualifiedName~IntradayPortfolioExecutionTests|FullyQualifiedName~IntradayDecisionEvidenceProjectionTests|FullyQualifiedName~IntradayBacktestIntegrationTests|FullyQualifiedName~ConfigCatalogServiceTests|FullyQualifiedName~StrategyArtifactCatalogTests|FullyQualifiedName~BacktestExecutionProfileResolverTests|FullyQualifiedName~PreviousSessionBreakoutTests|FullyQualifiedName~ContinuationProtection|FullyQualifiedName~BacktestCalendarBoundsTests|FullyQualifiedName~PreparedMarketDataVersionTests|FullyQualifiedName~PreparedMarketDataServiceTests|FullyQualifiedName~CachedPreparedHistoryConsumerTests|FullyQualifiedName~CandlePipelineEngineTests' --logger 'trx;LogFileName=orb-breakout-candle-calendar-final.trx' --results-directory outputs/orb-breakout-candle-20261028/verification -m:1 --disable-build-servers --verbosity minimal
```

Plan 32 was retried with ordinary start key `orb-breakout-candle-calendar-bounds-retry-20261028`. All eight jobs completed; their evidence is in `outputs/orb-breakout-candle-20261028/rerun-1`. The retry reused **nine inputs, zero downloads**: exact eight candle versions and a calendar query through October 26 derived from the original immutable wider parent. Identical historical calendar bounds deduplicate the former two calendar requests. All 56 needed calendar dates match the retained 67-day parent and its original raw/manifest hashes.

Independent actual-run audit: 9,723 main checks, zero failures; all 1,680 hypotheses, 74 admitted trades and 148 fills reconcile. Additional protection/cost/target, shared-capacity and three-version monthly checks passed. There were 13 original-3R target fills, 50 protective-stop exits, 10 deadline exits and one observed opening notional-cap reduction. All positions closed the same session, and the two-filled-parent limit held. No actual partial parents or entry through the initial stop occurred; those remain covered by focused tests rather than observed examples.

The new version improved four stocks against 1.1.0 and worsened four; only PATH and POET were positive. Reaching 3R became possible in this sample, but this is not a consistent improvement or a strategy promotion. Exact per-stock results, all three versions and drawdown comparisons are in `rerun-1/monthly-results.md` and `monthly-comparison.json`. No pooled return is presented.

After successful completion, all ten original source hashes and 160 control publication hashes still matched; the wishlist response was unchanged. All failed-attempt evidence, successful results, core data and intermediates are preserved. No cleanup ran.

The restored Web process is PID 49720 on localhost:53021, with the existing ngrok tunnel still available. Trading and market feeds retain their paused/disabled settings. Authenticated API and HTTP 200 result-page checks confirm all three immutable identities and October results. Shared Android presentation was tested; physical Android, APK, live-broker and full release checks were not performed or claimed. Stage 3 remains unimplemented and requires separate approval.
