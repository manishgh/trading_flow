# Workflow integration closure

Date: 2026-09-27. Scope: TradingFlow implementation checkpoints 6–8 and affected shared workflow consumers. Market Predictor API adoption is explicitly deferred. This is application implementation closure, not strategy promotion or external operational acceptance.

## Implemented behavior

- Web and Android submit shared immutable universe, exact-strategy and durable run commands. Transport retries reuse complete requests and idempotency keys; edits start a new command. Paper jobs expose the candidate decision-run identity.
- Job/result projections and paper chart/audit reads are bounded. Paper attempts persist their artifact directory and recover retained chart/audit views after removal of generated configuration files.
- Candidate projections report actual completed-bar market evidence, strategy-rule failures and discovery provenance. Evidence enrichment must match the captured semantic decision and cannot use a later candidate version.
- The kernel's actual attached catalyst revision is persisted atomically. A negative revision received after the evaluated completed bar does not replace the earlier article used by the signal.
- Reviewed orders bind candidate, run, strategy, quote, risk and expiry; pending confirmation remains reconcilable. Durable cancellation, protected entries and supported exit order types remain available.
- SSE replay and polling reject stale responses while preserving focus and draft input. Android cancels page/session work, protects selected-job identity, and supports exact candidate/article alert navigation and bounded local alert history.
- Removed the unused inline desk order-ticket partial and its model after repository reference searches. Active tickets use the reviewed workflow.

## Verification tier and commands

Component integration closure with broad offline regression because changes cross application, persistence, engine evidence and both clients. Heavy checks ran sequentially.

1. Focused final counterexamples and real replay:

   dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter "FullyQualifiedName~WorkflowConfigurationTests|FullyQualifiedName~CandidateStreamIntegrityTests|FullyQualifiedName~WorkflowReplayTests" --verbosity quiet -m:1 --disable-build-servers

   Result: 11 passed, zero failed. Covers missing historical config, captured decision/version mismatch, real discovery and source persistence, delayed news revision, warming/rejection, actual reservation and partial/full fills, duplicate-fill handling, exit/restart and hosted HTTP audit fixtures.

2. Browser suite, from tests/ui:

   npm test

   Result: 78 passed, zero failed, 1.8 minutes. Includes 1440x900 and 412x915 workflow views, viewport regression down to 320px, links, input/focus preservation, stale polling after SSE, failed-response retries, immutable backtest windows and reviewed order confirmation. Browser requests that would mutate run/order state are intercepted by fixtures; the host uses isolated UI-test data.

   Inspected full-page desk and real replay audit screenshots at desktop and phone widths. No overflow or obscured controls; rule details are collapsed and exact article links remain visible. Screenshots remain under tests/ui/test-results/workflow-integration-*.

3. Android Release:

   dotnet build src/TradingFlow.Mobile/TradingFlow.Mobile.csproj -c Release -f net10.0-android --no-restore -m:1 --disable-build-servers --verbosity minimal

   Result: successful, zero warnings/errors. The final cancellation propagation change was recompiled successfully: zero warnings/errors, 1m15s.

4. Full appropriate offline C# suite:

   dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter "FullyQualifiedName!~AlpacaCandlePipelineIntegrationTests" --verbosity quiet --logger "trx;LogFileName=workflow-closure-final.trx" --results-directory .test-tmp/workflow-closure -m:1 --disable-build-servers -- xUnit.MaxParallelThreads=1 xUnit.ParallelizeTestCollections=false

   Final count is recorded below. The live provider integration test is excluded. The known producer-copy check is deliberately retained so this suite cannot be described as completely passing.

5. Solution build:

   dotnet build TradingFlow.slnx --no-restore -m:1 --disable-build-servers --verbosity minimal

   Final result is recorded below. Changed JavaScript files also passed node --check.

## Known deferred failure

MarketPredictorHttpClientTests.GoldenFixture_MatchesPublishedHashAndLocalProducerCopy fails because the producer corrected the contract after the earlier consumer adoption. The new producer fixture SHA-256 is 360206c2440e4fb39bac210a50b12c6d233e4f49a140fd356e5d283b034536cb. Consumer wire/parser/fixture changes are outside this task's explicit scope. The failure has not been skipped or weakened. See ../integration/market-predictor-notes.md for the dated acknowledgement of receipt and deferral.

The initial broad run also found an unconfigured decision-query mock in CandidateStreamIntegrityTests. Its setup was fixed and the final focused run passes. Earlier browser harness failures were repaired to load the shared request helper and assert the new reviewed-ticket navigation; all 78 browser tests now pass.

## Review and acceptance limits

Independent requirements and code reviewers report no remaining actionable finding. Both identified real lineage/replay coverage and concurrency/historical-view issues during review; fixes and counterexample tests are included. Review sessions are complete.

ADB returned an empty attached-device list. Device navigation, authentication/reconnect and notification delivery have not been verified on hardware or an emulator. External provider paper smoke, multi-session fill/impact evidence and strategy promotion remain unverified. No provider/broker order was submitted for these checks. No producer edits, GitHub sync, local secret changes, database reset or candle-data deletion occurred.

Task-owned browser Web host exited after testing. Final process and whitespace checks are recorded below.

## Final recorded results

- Focused replay/concurrency/history tests: 11 passed, zero failed.
- Full offline C# suite: 1,698 passed, one failed, zero skipped, 1,699 total; 2m15s. The sole failure is the explicitly deferred Market Predictor producer-copy mismatch described above.
- Full solution build: succeeded, zero warnings and errors.
- Browser suite: 78 passed, zero failed. Final backend-only concurrency/history fixes passed their focused C# tests; removal of the unused partial compiled in the full solution.
- Requirements and code reviewers: approved; no remaining actionable findings; sessions completed.
- Final process inventory: no TradingFlow.Web, TradingFlow.WarmupService or ngrok process remained.
- Whitespace check: git diff --check passed after whitespace cleanup; JavaScript syntax checks passed.
- Final Android Release rerun: succeeded, zero warnings/errors; 1m15s. Final cancellation and orphan-removal deltas also passed independent code review.

## Acceptance follow-up

The initial no-device statement above is historical. On 27 September an isolated Android emulator was installed and actual navigation, notification delivery, cold login routing, backend mismatch rejection and light/dark checks were performed. Runtime defects were fixed. Real Alpaca account/data/stream connectivity and the live candle pipeline passed; no orders were submitted. Real paper execution remains unverified. See [the follow-up record](android-external-paper-acceptance.md) for exact scope, remaining checks and final build evidence.
