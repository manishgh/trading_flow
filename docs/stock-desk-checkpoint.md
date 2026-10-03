# TradingFlow stock desk — September 28, 2026

The default web landing page is `/Desk`. Primary navigation is Desk, Orders, Research and System. The desk combines followed stocks, selectable discovery screens, broker holdings, daily technical context, news and expandable preparation.

## Implemented

- All active followed symbols across ordinary wishlists, deduplicated with every contributing wishlist retained. Immutable preparation revisions are excluded.
- Source groups: all followed, individual wishlists, TradingFlow swing screens, and locally saved/imported Elite filters. Built-in discovery screens cover established trends, new annual highs and trend pullbacks. They are discovery filters, not promoted trading strategies.
- Search runs over complete membership before paging. Fifty stocks are displayed per page. Finviz membership refreshes each minute. Quote reads repeat at five-second intervals, plus provider latency. Each quote retains its timestamp and individual freshness state.
- SIP bid, ask and indicative midpoint; calendar-derived session state. The same observation path is available during premarket, regular and postmarket sessions. This does not enable extended-hours execution.
- Completed daily indicators from a bounded cached daily-history reader, using the shared IndicatorEngine and real exchange sessions. The existing generic swing-watch evaluator provides observational setup/not-ready reasons, distinct from strategy authorization.
- Account-level paper holdings, including positions without a running job: quantity, average entry, cost basis, market value and broker unrealized P&L. Account binding is checked before positions are read. GET-only reads are archived and cached for 15 seconds. Disconnected or 30-second-old holdings are labeled stale, and sell links are hidden until fresh evidence returns.
- News filtered by selected membership in the database before applying limits, with article deduplication and contributing symbols. Stock selection narrows news; the view-wide option restores the displayed page's news.
- Preparation is expandable inside the desk and shares its actual view and APIs with existing exact-revision notification links. Opening the desk does not create a preparation occurrence.
- Ordinary operators go to the candidate workflow with the correct wishlist. Administrators have a clearly labeled override-review link. Sell uses the existing reviewed exit. No order, risk or strategy-authorization gate was weakened.
- Explicit follow action preserves wishlist notes on retries. No screener results are imported automatically.

## Research and limits

[Finviz Elite](https://finviz.com/elite) documents saved presets, real-time extended-hours data and export features. [Finviz screener help](https://elite.finviz.com/help/screener) documents the technical filters. A supported private-preset enumeration path was not verified. Therefore personal Elite screens are added by pasting their URLs; only filter values are stored, not embedded credentials. The UI does not claim automatic account synchronization.

Quotes use the existing REST quote service through a single desk event stream. This is periodic refreshing, not a claim of tick-by-tick websocket quote delivery. Desk viewing and the daily wishlist observer do not subscribe the execution candle stream. They share a bounded daily reader; persistent wishlist observation settings remain unchanged.

News displays up to 1,000 matched stored symbol records in the last 72 hours. Existing live ingestion continues, but complete historical backfill for every followed or screener symbol is not established; this limitation is visible on the page. Null financial or technical evidence is never converted to a neutral signal or a zero holding.

Saved screens use the existing shared operator catalog. They are not represented as private per-user remote Finviz storage. Earlier UI screenshots used deterministic fixtures. The monitoring-repair evidence also includes the actual browser with real SIP quotes, daily history and matched news.

## Verification

Risk tier: component checkpoint, not full product release.

- Web build passed with zero warnings and zero errors. Final focused test builds also passed compiler/analyzer checks.
- 61 affected backend checks passed: desk read model, broker transport, Finviz client, wishlist persistence, preparation workflows/endpoints and candidate-client state. After the last follow-action refinement, its affected 17-check slice passed again.
- 53 affected browser checks passed across preparation, responsive navigation, desk and reviewed workflow integration. The final desk suite passed all 11 checks, including one additional real follow-endpoint test: 54 distinct affected browser checks overall.
- Browser coverage includes 412/1280/1600-pixel desk views, dark mode, source replacement, real open SSE fixtures, disconnected streams, open but stalled streams, startup retry, sanitized screen import with CSRF, same-page preparation, and follow retry/persistence. Existing responsive routes were also checked from 320 to 1440 pixels.
- JavaScript syntax checks and `git diff --check` passed.
- Independent requirements and code/design reviews completed. Findings about paging, account freshness, retry, buy context, contrast and mobile focus were addressed and tested.
- Read-only external preflight at 15:29 UTC confirmed the bound paper account was active, unblocked, and had zero open orders and positions; a current SIP quote was received. No orders were placed.

The full backend release suite, mobile rebuild/device testing, external execution, private Elite synchronization and ML integration were deliberately not claimed by this web checkpoint. The previously recorded producer/consumer ML fixture discrepancy is outside this change and was not weakened or marked fixed. The isolated natural-session preparation worker was preserved.

## Main code locations

- `src/TradingFlow.Web/Services/Desk/`: source resolution, read projection and broker account reader.
- `src/TradingFlow.Web/DeskEndpoints.cs`: authenticated catalog, event stream, screen import and explicit follow endpoints.
- `src/TradingFlow.Web/Pages/Desk.cshtml`, `wwwroot/js/watch-desk.js`, `wwwroot/css/watch-desk.css`: stock-first web workspace.
- `src/TradingFlow.Web/Pages/Shared/_PreparationWorkspace.cshtml`: shared preparation view.
- `src/TradingFlow.Tests/DeskReadModelTests.cs` and `tests/ui/specs/watch-desk.spec.js`: focused acceptance evidence.

No GitHub synchronization was performed. Market Predictor, Android, operational databases, credentials and candle history were not edited by this checkpoint.

## Monitoring repair — 16:07 UTC

The earlier fixture-backed checks missed a missing named quote-client BaseAddress and an impossible daily-frame request against the intraday stream. Fixed production DI wiring and added a regression using the real registration. The new daily reader validates actual calendar completion, SIP/all provenance, coverage, missing sessions and warm-up; it never fabricates pre-listing history. Four workers and a bounded cache/queue keep cold history from blocking quotes. Both Desk and persistent wishlist alerts reuse it, without execution-stream subscriptions.

News now appears beside the bounded stock list, each row shows a current observational result and headline, and transport connectivity is no longer presented as healthy quote coverage. Source descriptions correctly distinguish background wishlist alerts from a selected strategy. Stock detail retains reviewed order boundaries.

Verification: affected web build zero warnings/errors; 40 focused backend tests; 12 Desk browser tests including mobile/dark layouts, 32-row news visibility, unavailable-quote health and bounded order-button height. Independent requirements and code/design reviews passed. JavaScript syntax and whitespace checks passed. This is a component repair, not release certification; full release, Android, external execution and ML checks were not rerun because those paths did not change.

Actual browser/provider check at approximately 16:05–16:07 UTC: 30/32 symbols have quotes and daily technicals; individual quote ages remain explicit. SIVE and UI2T lack data, BXDC/FPS have short histories, MXL/OUST/SMCI show daily watch setups with reasons. Selected MXL news and indicators were inspected. A new MU headline appeared during verification. Existing enabled wishlist observer recorded 27 ready of 31 requested symbols and persisted three alerts; no observation preference or strategy authorization was changed. GET-only broker preflight at 16:07 UTC confirmed no positions or open orders. No orders placed; isolated natural-session acceptance worker preserved.
