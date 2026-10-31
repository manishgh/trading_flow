# ORB submitted-order rounding comparison — 28 October 2026

Approved scope: step 1 of `docs/design-review/2026-10-19/13-orb-loss-analysis-and-proposal-2026-10-27.md` only. Preserve original OHLC, derive valid submitted trigger/stop prices outward, retain the full-tick breakout buffer, and register a separate strategy version. No changes to entry timing, direction, volume qualification, original 3R target, protection, rearm, daily limit or liquidation.

## Execution checklist

- [x] User approved implementation and the eight-stock comparison.
- [x] Read repository instructions and approved proposal.
- [x] Stop the verified paused TradingFlow Web process before edits; retain its ngrok tunnel for restoration.
- [x] Verify all ten baseline source hashes and 72 published result hashes before work.
- [x] Assign Sol 6.1 High implementation, independent engineering and independent quant agents.
- [x] Complete implementation and preserve v1.0.0 canonical identity.
- [x] Resolve independent engineering and quant implementation findings.
- [x] Run focused behavior, causality, risk, configuration and affected consumer tests; build Web.
- [x] Restore the paused Web server and verify both strategy versions through the shared API.
- [x] Save and execute the ordinary eight-cohort plan using existing components and existing history.
- [x] Independently audit submitted prices, trades, accounting, data reuse and monthly comparison.
- [x] Verify original source/result preservation and publish final findings.

## Frozen comparison

Control: corrected `outputs/orb-october-20261027/rerun-1`, `technical.opening-range-breakout@1.0.0`, canonical SHA256 `8345a4e9a29ab99260f3566572d447386ae5465f0f74e250fdf8b2e8876ba8e7`. Compare against the retained control; do not replace or mix in the superseded initial attempt.

October 1–26, 2026 inclusive, New York exchange dates, a partial month. CRDO, MU, MXL, NVDA, PATH, POET, SMCI and TSM are independent one-stock cohorts with $10,000 each. Nominal risk 1%, notional cap 25%, one overlapping position, maximum two filled parent entries per session. Preserve all execution fees, slippage, participation and other saved profile settings. Retain all data and results; no cleanup is planned for this task.

Evidence directory: `outputs/orb-order-rounding-20261028`. The source-preservation and control-publication preservation receipts contain zero mismatches. Output JSON files are ordinary workflow requests/results and audit records, not executable research drivers or custom run configuration.

## Verification scope

This is a component checkpoint, not strategy promotion or a product release. Focused tests must establish old-version identity and behavior, full-tick rounding in both directions, raw/submitted evidence separation, risk sizing and actual-fill protection, causal entry timing, rearm, daily limits and session exits. Web/API and shared Android presentation must continue to carry the exact selected strategy identity. No broker orders or live trading are authorized.

## Completed implementation and checks

The new registered research identity is `technical.opening-range-breakout@1.1.0`, canonical SHA256 `5ef1364778548b873e6bec863b43ab29de0166d1df811497d5f3ca28dcbc3981`. Its existing `policy_id` selects outward rounding; no new configuration property or wire schema was added. A small evaluator helper preserves observed opening OHLC and derives submitted levels. Existing planner, pending intent, actual-fill protection, result projection and shared Web/Android presentation are reused. The original v1 source SHA256 remains `4FB1F80212F40F1BC77FCAC53E7987D0D52FA466CDDB262E2DBF9C8129486C39`; tests and API confirm its unchanged canonical identity.

Both independent source reviews passed. A test-only null-signal assumption was corrected before the completed test attempt; the test now exercises the same shared risk planner used by execution. No production correction beyond approved step 1 was added.

Completed command (168 passed, zero failed/skipped; affected projects including Web built):

```powershell
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter 'FullyQualifiedName~OpeningRangeBreakoutTests|FullyQualifiedName~OpeningRangeWorkflowIntegrationTests|FullyQualifiedName~IntradayBacktestIntegrationTests|FullyQualifiedName~IntradayDecisionEvidenceProjectionTests|FullyQualifiedName~ConfigCatalogServiceTests|FullyQualifiedName~StrategyArtifactCatalogTests|FullyQualifiedName~IntradayPortfolioExecutionTests|FullyQualifiedName~ContinuationProtection' --logger 'trx;LogFileName=orb-order-rounding-final.trx' --results-directory outputs/orb-order-rounding-20261028/verification -m:1 --disable-build-servers --verbosity minimal
```

The first sandboxed attempt could not connect to testhost and aborted before executing tests; its TRX remains preserved. The successful retry allowed local test-host communication. Scoped whitespace checks passed. Full release-suite, broker execution, Android device/APK and new optimization runs were not performed: this was the approved component change and historical comparison only.

## Completed comparison and review

Ordinary saved plan **29** generated eight durable jobs; all completed. Stock groups, dates, independent capital, saved configuration hash and resolved execution settings match the retained control. All ten immutable market/calendar query versions were reused, with **zero downloads**. The reviewer checked exact data/manifest identities and fresh hashes against the corrected control.

Independent quant actual-run audit: **53,882 checks, zero failures** across 5,254 hypotheses, 82 admitted orders, 56 trades and 112 fills. Prices, sizing, original actual-fill 3R, fees, cash, monthly results, rearm, daily limits and same-day exits reconciled. Thirteen added trades occurred on previously price-rejected days. There were 40 deadline exits, 16 protective-stop exits, zero original 3R touches while held and zero target fills. No actual partial-fill or opening-gap entries occurred; those cases retain focused-test evidence only.

Five stocks improved, MXL worsened, CRDO and MU were unchanged. PATH and POET turned positive, but POET's published maximum drawdown increased. The rounded rule corrects price eligibility; it does not establish a profitable ORB strategy or resolve target distance. Per-stock realized monthly results are in `outputs/orb-order-rounding-20261028/monthly-results.md` and exact values in `monthly-comparison.json`.

After the runs, all ten baseline source hashes and all 72 original published result hashes still matched. The wishlist response was unchanged. All core data, results and intermediates were preserved; no cleanup ran. Evidence includes `independent-engineering-review.md`, `independent-quant-actual-audit.json`, `independent-source-reuse-audit.json`, the actual quant review and before/after preservation receipts in the comparison output directory.

## Runtime and remaining boundary

Web was restored as PID 4436 on the existing localhost port 53021; its existing ngrok PID 52236/tunnel remains available as previously requested. Trading and market feeds retain their paused/disabled settings; historical backtests used the existing allowed worker, one portfolio at a time. Authenticated API and HTTP 200 Web result checks expose both identities and the new October results. Shared Android presentation is tested, but no phone validation is claimed.

Stage 2 and direction-selection proposals remain unimplemented and require separate approval. All strategies remain research-only. These 18 common October sessions are development evidence; historical borrow and original revision availability remain unproved.
