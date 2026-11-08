# Desk and Earnings refinements — 8 November 2026

## Approved behavior

The user requested removal of Earnings Custom dates and Evidence & display filters; the TradingFlow brand returning to Home; consistent stock-group terminology; immediate Desk Buy/Sell popups; tighter stock columns with more width for News; a Technical column with SMA 10/20, SMA 50/200 and RSI; recent positive/negative story counts; and a closed-market label instead of weekend delay ages. Astra reviewed the plan, Sol 6.1 implemented it, and an independent Sol engineering review approved the final changes. The user's phone screen inspection remains paused; no CUA or physical-device control was used for this checkpoint.

## Implementation

Earnings now constructs query state only from its remaining visible filters. Removed legacy URL parameters cannot silently reapply hidden filters. Today, Next reporting day and This week remain; This week derives Monday–Sunday in America/New_York and sends the existing calendar API its internal range. Both brand links target /Desk, the existing Home destination. Desk, preparation and order-ticket display strings use stock group; persisted wishlist identities, keys, URLs and payload fields are unchanged.

The stock table constrains its leading columns and gives desktop News half the workspace. Narrow panes retain deliberate table scrolling. Technical values reuse the existing completed-daily observation and its ticker/time/feed guards, including missing-value em dashes. Three compact lines show SMA 10/20, SMA 50/200 and RSI. Symbol and sentiment lines are stacked within their cell; all mobile trade buttons retain 44px targets.

Desk row Buy/Sell keeps the shared viewport dialog and explicit triggering button, so closing restores focus without scrolling the page or long table. An unavailable action explains its reason immediately. That explanation cannot replace the retained iframe, reviewed ticket, unresolved command, or candidate/run context. Existing server preview/confirmation and membership/role/holdings guards remain authoritative.

Per-symbol news summaries are computed before the five-story display limit from the existing bounded, deduplicated stored-news query (72 hours, at most 1,000 symbol records, existing 15-second cache). There are no additional provider requests. The trailing UTC hour excludes future publications; existing strict sentiment thresholds greater than +0.15 and less than -0.15 define positive/negative, boundary values are neutral and null scores are unscored. Failed reads are distinct from successful zero counts. The UI labels partial coverage, exposes the exact window and neutral/unscored counts, and marks disconnected, old or future-timestamp summaries as observed instead of current. These are text sentiment counts, not trade signals.

The authoritative Closed session displays Market closed after checking missing/disconnected/future quotes. AfterHours is recognized explicitly; Unknown does not imply closure. Exact quote timestamps and the separate previous-close value remain available. No quote is relabeled as the official closing price.

## Verification

Component UI checkpoint, not a product release or strategy promotion:

- `dotnet test src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore --filter 'FullyQualifiedName~DeskReadModelTests|FullyQualifiedName~NewsStockSourceTests|FullyQualifiedName~DeskTicketPlanTests|FullyQualifiedName~DeskUniverseTests' -m:1 --disable-build-servers -v quiet`: 64 passed, 0 failed.
- Final `dotnet build src/TradingFlow.Web/TradingFlow.Web.csproj --no-restore -m:1 --disable-build-servers -v quiet`: 0 warnings, 0 errors (13.25 seconds).
- Initial affected browser run: 69/78 passed. Six density assertions exposed row sizing, one first-use preparation assertion assumed an existing workflow, a session fixture raced its five-second update, and a navigation assertion omitted Desk's normal source=all query. Screenshot inspection additionally found global horizontal button styling obscuring ticker text; scoped stacked layout and containment checks corrected it.
- Final affected rerun: 60/60 passed in 1.2 minutes across watch-desk.spec.js, workspace-navigation.spec.js, preparation.spec.js and quick-trade-popup.spec.js. This includes all former failures, six widths (412/680/1200/1265/1280/1600), dark appearance, contained large counts, long-list popup scroll/focus, retained uncertain commands, closed/unknown sessions, missing/future daily observations, stale/unavailable sentiment and Home navigation. Earnings and trading-capabilities/trading-workspace checks passed in the preceding run; their executable source was unchanged by the final CSS/test corrections except reviewed text-only terminology.
- JavaScript syntax checks and CRLF-aware whitespace checks passed. Independent review approved the final implementation, accessibility corrections, deterministic test fixes and text-only terminology update.

Evidence is preserved under `C:\Users\manis\Documents\Codex\2026-11-08\tra\outputs\web-refinements`, runs `c8e8e3e8-1835-4195-b4f6-8327edfb2b36` and `3208c13d-dc00-4453-b517-beddc41c7243`. Root inspected final desktop and phone-width Desk screenshots plus Earnings. Tests used isolated owned data on port 53018 with provider collection disabled, never the operator database. The final run's disposable fixture/artifact roots held 5,074,746 bytes before the existing cleanup reporter removed them after host shutdown. Screenshots/results were copied out first. No real order was submitted.

The operator web app is left running on port 5088 with its normal data and feed configuration; the existing ngrok tunnel remains available. Public health and compiled Earnings markup were checked after restart. Unrelated dirty engine/configuration/test files and all original operator/research data were preserved. Full repository and live-broker validation were deliberately not run for this scoped UI checkpoint.
