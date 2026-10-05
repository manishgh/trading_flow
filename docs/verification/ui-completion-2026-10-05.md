# Web/API and Android completion checkpoint

Date: 5 October 2026. This records a bounded implementation checkpoint, not completion of every item in the Astra/desk plan. Work stays in the isolated integration checkout; the original checkout, local secrets, broker journal and candle cache are preserved.

## Implemented

- Audit reads use bounded repository queries, deterministic server sorting, complete-run counts and a reason summary. CSV/JSON exports stream all matching records in bounded batches with pinned membership. Up to four selected runs can be compared. Realised equity curves use validated completed result artifacts and actual UTC exit dates; they are not per-bar mark-to-market equity. Missing or invalid artifacts display a reason. Completed curves refresh when the page reloads.
- Group add/remove/copy/move/tag/untag commands persist their exact request and before/after membership in one transaction. Repeated command IDs return the original receipt. Undo restores the captured state only when intervening edits and immutable preparation groups do not prevent it. Tags remain separate from notes. Unknown outcomes retain the issued request for retry; reload shows its exact identity.
- News filters apply before counting and paging in the shared API/repository. Provider, source, evidence classification, sentiment, search, links, group and held scopes share the same rules. Public market reads remain anonymous; private scopes and provider refresh require authentication. Matching counts describe stored records before articles on a page are combined.
- Earnings exposes shared provider/news-provider, EPS/revenue, slot RVOL, sentiment, assessment, breakout, group/held/news filters and ordering. Calendar ranges are ordered and limited to 35 days. Web preserves server order, focused controls and expanded details during refresh.
- The market explorer filters and sorts before bounded paging and scopes bulk selection to the visible page. Presets and layout preferences are separated by operator and environment. The existing volume metric is labelled **Volume Z20**, not RVOL. This does not implement every planned sector/cap/indicator filter or literal row virtualization.
- Orders replacement explicitly requests cancellation, retains its command identity through unknown outcomes, and waits for authoritative `Canceled` with known zero cumulative fills. A fresh account/position read and journal recheck precede opening a new review. Protective orders, unknown fill quantities and fill races cannot use this path. Nothing automatically submits a replacement or re-arms a consumed candidate. Known undispatched rejection permits explicitly ending that review.
- Native News carries the shared filters, classification, stored-record count and paging. Native Earnings carries advanced filters and an optional calendar range, grouping every returned event by its real New York date. Both cancel superseded reads and reject responses from a hidden page, old operator or old backend session. Provider refresh is serialized against reads, and refresh failure remains visible beside stored data. Missing responses and totals stay unknown. Private selections/results clear before a session reload.
- The native ngrok default is updated to `https://d88a-2001-1c00-820b-7600-a565-6712-f63f-b87a.ngrok-free.app`. Existing saved custom URLs are preserved; the phone's Settings change requires separate device verification.

## Verification

Requirements and independent code/design reviews were performed for each bounded checkpoint. Findings about retained command identity, cancellation rejections, actual artifact size, stale requests, timeout/error visibility and nullable totals were corrected.

- Final Android review build: zero warnings/errors. Package `com.manishgh.tradingflow.integration`; existing normal package is separate. Review APK SHA256: `3CF3FE60C79F6C186A0A54DE0A246C75556C38D6171C56DF97BFFE8B43D52CB1`.
- Full offline .NET suite: **1,881 passed, one failed, 1,882 total**. Receipt `.tmp/integration-tests/web-android-final.trx`. Real-provider `AlpacaCandlePipelineIntegrationTests` was deliberately excluded; it is not counted as a skipped or successful provider check.
- The sole failure is the unchanged prediction fixture's comparison with the separate local producer checkout. TradingFlow's committed v4 fixture matches its published SHA256 `96cbcd133b8e96253b034fabba10624be79b0d80550f043f3869905251bec522`; the current `C:\project\market-predictor` local fixture declares v1 and hashes to `59288471c249b0422443f0a68ce7623f4b03956826877ad1158dc79ebf1fea51`. Neither fixture, that assertion, nor the producer checkout was changed to hide this mismatch.
- The initial full command incorrectly supplied blank Alpaca environment overrides, masking dummy credentials in mocked tests. Its additional failures were rerun with those process-local overrides removed. The authentication test host was updated to register the new replacement service and assert anonymous/CSRF denial on the new routes.
- Changed JavaScript syntax checks and CRLF-aware diff checks pass. No Python consumers of the extended News/Earnings/group endpoints were found in this repository.
- All **152 browser checks passed** on the final source: 148 in the complete run, then the four file-dependent fixtures reran successfully from `tests/ui`. The initial command used the repository root, causing those four `ENOENT` fixture errors. The actual checkbox hit-area defect was corrected with an explicit selection-column width; focused paging checks verify 25/25/11 rows and retained paper presets while live stays locked. Audit refresh also verifies focused/open details survive row reordering.

Commands (from the integration root unless stated otherwise):

```powershell
dotnet build src/TradingFlow.Mobile/TradingFlow.Mobile.csproj --no-restore -p:ApplicationId=com.manishgh.tradingflow.integration -p:ApplicationTitle=TradingFlowReview -m:1 --verbosity quiet
# Remove process-local Alpaca credential overrides for mocked offline tests.
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter "FullyQualifiedName!~AlpacaCandlePipelineIntegrationTests" -m:1 --verbosity quiet --logger "trx;LogFileName=web-android-final.trx" --results-directory .tmp/integration-tests -- xUnit.MaxParallelThreads=1 xUnit.ParallelizeTestCollections=false
# Run browser commands from tests/ui; disable provider credentials for that process.
node node_modules/@playwright/test/cli.js test --config playwright.config.js
node node_modules/@playwright/test/cli.js test --config playwright.config.js --grep "orders add and remove rows|positions add and remove rows|real decision replay audit"
git -c core.whitespace=cr-at-eol diff --check
```

## Running review app

Public ngrok Workspace, News, Earnings and their shared stored read APIs returned HTTP 200. The supplied fixed review credentials completed CSRF/login/current-identity verification through HTTPS; Orders, Wishlists, portfolio and journal returned HTTP 200 afterward. This verifies the Web/API login path, not the new APK's native sign-in.

The user requested Web remain available. Owned review processes are Web `74428` (dotnet host `69960`, port `53019`) and ngrok `87292`. Web runs Production plus explicit isolated UI-test mode, no launch profile, existing disposable `.tmp/ui-tests/phone-data`; operational hosted services and provider credentials remain disabled for that runtime. No review password is saved in this document or source. Old tunnel addresses and process IDs in earlier evidence are historical.

## Work still requiring acceptance or implementation

The new APK has not yet been installed or accepted on the reconnected phone. Its foreground was Gmail, so no controls were changed while the user was using it. Previous successful native sign-in/connection checks apply to the earlier APK only.

Broader native Watch source/universe/filter/bulk parity, environment contexts and wider exit controls remain outside this checkpoint. Firebase project setup, native SDK/token registration, durable delivery integration and actual background delivery are not complete. No polling behavior is described as push delivery. Web symbol-detail bulk actions and the remainder of the full desk filter specification also remain open.

Actual Alpaca/Finviz content, warmup freshness, previous-close Change %, strategy stop/target evidence and broker fill/protection/exit acceptance remain separate checks. No broker order was placed. Live remains locked. Empty isolated review data proves route/authentication behavior, not provider or strategy acceptance.

The [Azure deployment draft](../integration/azure-deployment-plan-2026-10-05.md) proposes one runtime owner with managed-disk SQLite, supported ingress, persistent protected keys, secrets, backups, recovery and paper acceptance gates. It lists existing template changes needed before deployment. No Azure resources were created and no GitHub push was made.
