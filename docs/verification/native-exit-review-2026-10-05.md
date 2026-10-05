# Native exit and command recovery checkpoint

Date: 5 October 2026. Successor to the [Web/API and native market-filter checkpoint](ui-completion-2026-10-05.md). This is a source/build checkpoint; physical-device acceptance is pending while the user reconnects and uses the phone.

The native account Orders screen now reviews market, limit, stop and stop-limit exits with day, GTC, IOC or FOK. It asks for an explicit positive whole-share quantity within the observed long holding, and only prices used by that type. Extended-hours permission is an explicit limit/day choice. The final review includes account, symbol, quantity, type, time in force, prices and permission. The existing shared close API still performs fresh account, quantity, execution and protection checks; this does not introduce a direct broker route or the entry preview-token workflow.

Fractional holdings can sell an explicitly entered whole-share portion. Their quantity prompt starts blank and states that fractional shares remain. Nothing rounds a holding into an order. Quantity text rejects decimal and grouping separators; prices accept the local decimal separator without grouping. This prevents German `1.5` or US `1,5` becoming 15 shares.

Issued exit/cancellation requests retain their exact immutable identity through timeout, transport failure, uncertain acknowledgment and navigation within the same page instance/session. Recovery controls remain accessible independently of current position/journal rows. A POST acknowledgment does not release the request; terminal journal evidence does. Only the server's known undispatched rejection codes offer an explicit end-review action. Connection/operator changes clear private rows and prevent replay in another session, with a reconciliation warning for earlier issued commands. Hidden-page and old-session completions cannot paint the current page. Process recreation recovery remains outside this checkpoint; these controls are not durable on the device.

## Verification

Requirements and independent read-only source/design reviewers checked this checkpoint. The locale parsing defect they found was corrected and retested. The server accepts whole-share partial exits from fractional holdings, which the final UI now exposes without rounding.

- Final focused native input/lifecycle and shared order safety slice: **123 passed, zero failed**; `.tmp/integration-tests/native-exit-checkpoint.trx`.
- Final review Android build: **zero warnings, zero errors**. Package `com.manishgh.tradingflow.integration`; not installed in this checkpoint. Delivered APK SHA256: `C6011B77A55509C8A2DFA024643DE0F0556A804527CAE0BBDD433187726DF4B8`.
- CRLF-aware diff check passed.
- Public Workspace, News, Earnings and shared feeds returned 200 through the current ngrok tunnel after Web restart. The supplied review account completed CSRF/login/current-identity and authenticated Orders/Wishlists/portfolio/journal reads with 200. No credential is recorded here.
- The preceding full offline suite remains 1,881 passes and one separate producer-fixture mismatch; its 152 successful browser checks apply to that Web source. This native-only checkpoint did not rerun the entire browser or release suite.

Focused filter: `NativeExitReviewTests|MobileMarketReadTests|OrderWorkflowIntegrityTests|OrderSubmissionServiceTests|ProtectiveOrderInvariantServiceTests|ExtendedHoursOrderPolicyTests|OrderDispatchFailureTests`, with `FullyQualifiedName~` before each term. Use `dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter ... -m:1 --verbosity quiet --logger "trx;LogFileName=native-exit-checkpoint.trx" --results-directory .tmp/integration-tests -- xUnit.MaxParallelThreads=1 xUnit.ParallelizeTestCollections=false`. Remove process-local Alpaca key overrides for mocked tests. Initial test compilation encountered locked Web outputs; Web was stopped and the final command passed after rebuilding.

Android build command: `dotnet build src/TradingFlow.Mobile/TradingFlow.Mobile.csproj --no-restore -p:ApplicationId=com.manishgh.tradingflow.integration -p:ApplicationTitle=TradingFlowReview -m:1 --verbosity quiet`.

## Review service and remaining work

Web is left available at `https://d88a-2001-1c00-820b-7600-a565-6712-f63f-b87a.ngrok-free.app/Workspace`. The restarted review host PID is 43788, ngrok PID 87292, Web port 53019. The host uses a separate binary snapshot in `.tmp/ui-tests/review-host-20261005` so native/test builds do not lock its DLLs. It retains the isolated `phone-data` database, explicit UI-test mode and disabled operational/provider services. The old Web PIDs 69960/74428 are stopped.

Phone installation, its saved ngrok URL, native sign-in, on-device exit presentation and accessibility/network checks remain pending. No broker order was submitted. Native Watch/environment parity, Firebase setup and implementation, remaining desk filters, symbol-detail group actions and actual provider/warmup/protection acceptance still require work. The [Azure deployment draft](../integration/azure-deployment-plan-2026-10-05.md) exists; no resources were provisioned and nothing was pushed to GitHub.
