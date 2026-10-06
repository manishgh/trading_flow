# Live feeds restored and News sidebar — 6 October 2026

This supersedes the feed/runtime statements in the earlier dataset-cleanup receipt.

## Regression and missed check

The reset explicitly cleared old News and Earnings records. The review launcher
then set `TRADINGFLOW_DISABLE_RUNTIME=true`, which stopped every hosted service,
including the read-only News and Earnings collectors. Alpaca credentials were also
blanked in that launcher. Consequently those pages read an empty database while
the providers still had data. The earlier checks proved list cleanup, authentication
and rendering, but did not prove fresh records were collected in the running app.
Reporting that narrower check as app completion was a mistake.

## Resulting behavior

- The host keeps trading/jobs paused while explicitly opting into market feeds
  with `TRADINGFLOW_ENABLE_MARKET_FEEDS=true`. Only NewsFeedService,
  AlpacaNewsStreamService and configured EarningsMonitorHostedService are added in
  this mode. No broker synchronization, dispatch recovery, backup, observer,
  preparation scheduler or job worker starts. UI-test mode overrides the opt-in.
- Existing read-only provider access uses the original local credentials in
  process memory. No credentials were changed or committed. The server's paused
  broker/run mutation guard remains authoritative despite restored credentials.
- Alpaca websocket news is persisted as it arrives. REST gap-fill polls cover
  active ordinary wishlist symbols and earnings targets instead of static paper
  config tickers. Up to 200 symbols rotate fairly between sweeps, with at most
  eight requests in parallel. One failed ticker/provider does not discard healthy
  results, and shutdown cancellation still propagates. Headlines are published
  before optional article enrichment finishes.
- The News default remains a rolling **last four hours**. Existing longer-window
  choices remain available; the store retains its separate 72-hour history for
  those queries and earnings evidence. A stored record is not a frozen page:
  collection continues and the page polls every 30 seconds. Polling replaces the
  server-rendered news and membership snapshot together, preserving filters and
  independent pagination. It skips hidden pages, unsubmitted filter edits, focused
  feed controls, open article details and open dialogs, including interactions
  started while the response was pending. Failed updates disclose that the old
  loaded news remains visible and retry.
- On desktop, news appears first and stock membership beside it on the right.
  Below 980px the stock list follows the feed. Both use document flow with no
  fixed height or sticky scrolling. There are still exactly two source groups.
- Anonymous private News browser requests now open Login and retain the exact
  local path/query for return after sign-in. Public All news remains public.
  Private API requests remain 401. No request is broadened to public All news.

## Checks performed

Component tier, final affected build and **42 passing tests**:

```powershell
dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter "FullyQualifiedName~ProductionCompositionTests|FullyQualifiedName~NewsFeedServiceTests|FullyQualifiedName~GroupNewsQueryTests|FullyQualifiedName~NewsStockSourceTests" --verbosity minimal
```

The final browser interaction change passed **21 tests**:

```powershell
node tests/ui/node_modules/@playwright/test/cli.js test specs/news-stock-sources.spec.js specs/public-workspace.spec.js specs/group-news-earnings.spec.js --config tests/ui/playwright.config.js
```

These cover desktop/mobile sidebar placement, no horizontal overflow, exact private
Login return, API privacy, public Earnings, live polling, untouched unsubmitted
filters, a slow response while a real group dialog opens, and adjacent group/news
consumers. Tests used their own host/database; cleanup removed generated data and
artifacts after each run. Requirements and independent source reviews closed all
findings. JavaScript syntax and CRLF-aware diff checks passed. These are affected
component checks, not a full product release or Android acceptance.

## Running-app evidence, separately from tests

Read-only provider checks confirmed 17 Alpaca articles for the five Long stocks
over the sampled three-day window and HTTP200 for Finviz's stock news CSV.

The restarted real host uses `C:\project\trading_flow\data`, UI test mode false,
market feeds enabled and trading/jobs paused. At 16:35 Europe/Berlin (14:35 UTC):

- The public last-four-hour feed had 314 matching symbol records, with both
  Alpaca and Finviz providers and a latest publication at 16:34:35 Berlin.
  Earlier it had 288 records; new websocket stories arrived after startup.
- The actual authenticated News page's wishlist selection showed 18 distinct
  articles from 19 matching records, including current MRVL/CRCL stories, and all
  30 source members beside the timeline. Actual desktop geometry placed the feed
  and stock panel at the same top position with no horizontal overflow.
- A subsequent browser observation showed its automatic update timestamp and a
  new PGY Alpaca article published at 16:37:04 Berlin without manual reload.
  The wishlist view advanced to 19 articles/20 matching records, still with 30
  sidebar stocks. This checks page updates as well as backend collection.
- The real Earnings API returned 13 events. The browser showed 9 for today and
  4 for the next reporting day, including APLD, LEVI, RELL and RGP. Monitoring was
  Running, with a current analysis timestamp and real news evidence.
- A fresh authenticated API check confirmed Long5, Volatile25, two genuine saved
  Finviz presets and two groups. Valid-CSRF requests to order-confirm,
  position-close, automation-entry and paper-run endpoints returned403.

These counts are dated observations, not fixed future expectations. Screenshots
are in the task output: `news-live-sidebar-2026-10-06.png` and
`earnings-live-restored-2026-10-06.png`. The final Web PID is 93400 on port53019;
ngrok PID87292 retains the existing d88a public address. Web/ngrok remain running.
No broker order, strategy promotion, Android change, GitHub push or Azure deployment
was performed. No test records were added to the canonical source.

Before a later cleanup/restart is called complete, verify collection and execution
separately: inspect real publication/analysis timestamps and actual News/Earnings
views as well as unchanged source lists, database isolation and paused mutation
behavior. Passing fixture tests alone cannot establish a live feed is operating.
