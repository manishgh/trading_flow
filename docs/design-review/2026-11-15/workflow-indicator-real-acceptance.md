# Workflow and indicator real-data acceptance

15 November 2026, 15:23–15:37 UTC. This record covers the approved UI checkpoint; it does not authorize a strategy or establish broker execution acceptance.

## Environment and scope

The isolated preview used normal operator authentication and the existing copied operator dataset at `C:/project/trading_flow/.tmp/chart-preview-20261115/operator-data`. Trading and durable workers were disabled; market-feed collection was explicitly enabled. The original app on port 10088 was left running. The preview used the frozen chart implementation based on `d70ba5d`, with uncommitted approved changes. The subsequent pruned-preparation and offline News fixture corrections require the final build/test record; those changes were not claimed as exercised in this preview. The preview was stopped after inspection.

No test wishlist, synthetic user, candidate run, paper order, new economic backtest, or authorization was inserted into the real dataset. Initial inspection recorded one operator, two wishlists, 30 wishlist items, migration 29 and no active jobs. Provider reads archived their actual responses and receipts. The market was closed on Sunday.

## Actual chart observations

The common chart was opened from MU on Desk. A five-minute regular-session view displayed daily support/resistance with year lookback while preserving five-minute candles. Labels identified the daily pivot source and actual input availability. Switching to Swing changed candles to daily and displayed the daily moving averages and RSI pane. Browser inspection at desktop size and 412 × 915 verified plotting, readable values, keyboard inspection, and an independently usable phone pane. Screenshots are exported as `TradingFlow-indicators-intraday.jpg`, `TradingFlow-indicators-daily.jpg` and `TradingFlow-indicators-phone.jpg`.

Alpaca SIP, adjustment `all`, supplied 500 native daily observations ending 13 November and the bounded minute window. The latest regular session contained all 390 one-minute observations. Receipt times are 15 November; original historical receipt is unproved and the UI explicitly labels the values reconstructed. Full request URLs, HTTP 200 observations, payload hashes and pagination completion are retained in the acceptance JSON. No API credentials are included.

Independent arithmetic over the archived candles produced:

| Display | Independent result | Browser result |
| --- | ---: | ---: |
| 13 Nov 15:55 NY five-minute open / high / low / close | 783.80 / 783.985 / 776.79 / 778.40 | Same |
| Five-minute volume | 1,094,191 | Same |
| RTH VWAP, HLC3 weighted by native minute volume | 768.6780663427496 | 768.6781 |
| EMA20, five-minute source | 772.4062479759436 | 772.4062 |
| ATR14, five-minute source | 3.6660816061505277 | 3.6661 |
| SMA50, native daily source | 943.6894000000002 | 943.6894 |
| SMA200, native daily source | 765.4166500000001 | 765.4167 |

EMA and ATR use the displayed 28 October 13:30 UTC seed with 1,014 complete five-minute inputs. Independent calculations used the arithmetic EMA seed, alpha 2/21, true range and Wilder smoothing. The last native daily close was 778.31 with volume 27,984,578; those exact provider-daily values appeared in the daily chart. They are distinct from the last regular-session five-minute bar.

The RVOL selection said that twenty verified matching-time prior sessions were required and displayed no value. Partial recursive history explained the warm-up requirement. The latest daily RSI was 42.4539 and ATR was 58.9661; those two daily readings were observed but not independently recalculated in this real-data comparison. The separate pinned formula tests cover them.

## Four tabs and archive reuse

Four authenticated real browser tabs displayed the same MU intraday values and daily structural context. The charts shared already-warm source data. MU-specific bar-response archives stayed at **19 files / 1,657,166 bytes** from 15:30:30 through 15:33:54 UTC, including indicator/context changes and the daily switch. Background feed collection added unrelated archives; total archive-directory growth is therefore not a valid chart acquisition measure.

The preview process working set was 1,036,394,496 bytes during the four-tab observation and 1,290,219,520 bytes after switching one tab to daily and closing the other three. It also ran market feeds and used the debug host's default garbage collector. These snapshots do not isolate chart memory or establish a steady-state production memory bound. The normal local launcher uses workstation GC. Derived cache, queue and profile bounds are tested separately; authenticated synthetic transport measurements, including repeated updates, are recorded in `chart-load-acceptance.md`. No claim of a long-duration soak or production latency guarantee is made.

## Daily workflow observations

- Desk showed `Paper`, `Trading paused`, `Market closed` and `Market feeds active` separately, followed by the preparation card and five-column stock table. The existing thirty stocks remained visible; SIVE had no quote while other stocks retained their own quotes.
- The preparation card identified target Monday 16 November, saved evening evidence, missing morning collection, expiry at 09:30 New York and the paused worker. The dedicated Preparation page displayed the saved 250-stock evening revision. `Review and open on Desk` was disabled, and collection controls explained the paused worker. No collection or handoff was submitted.
- Strategy Lab displayed the sequence rule/horizon → stocks/dates → capital/fills → preview/save/start. The initial page said `Research only`.
- Opening existing saved plan 192's trend result (`6bd75d31-e4b8-f408-7791-130f10321365`) displayed `Research assessment from retained evidence · FAILED_GATE`. It separately displayed execution completed and `Result unavailable` because the original file was inaccessible. Its retained December 2023 monthly record remained visible at +3.64%, with the source-session month and separate strategy capital. No reference repair or rerun occurred.
- News showed `News results updated` with a current 15:35 UTC attempt, 54 matching stored records and 46 combined displayed stories. Stored rows were explicitly distinguished from the current refresh.
- Earnings first showed analysis pending. After analysis, Sunday's covered empty window was labelled as such. Selecting Monday's next-reporting-day window showed **59 stored events**, two reported, with actual source/session/timezone labels and missing price evidence left unavailable.

The final serialized backend/browser release runs and separate A plan/execution and B code/design decisions belong to the tracking plan. These real-data observations supplement those checks rather than replacing them.

## Published local build, 15 November 16:36–16:45 UTC

Commit `57ee499` passed 3,995 backend tests (zero failed, four profiling opt-ins unrun) and all 226 browser tests. Separate A/B reviewers accepted it before cutover. The old flat/paused host was stopped; a consistent 512,036,864-byte operator backup passed integrity checking, with SHA-256 `e5947e029ca07d7273ac1632c1f0cd868b6d4854f3c72457a2b8e9014a97a0d3`. Original main was fast-forwarded locally; previous binaries were preserved. No GitHub synchronization occurred.

Published host PID 25056 serves port 10088, trading paused with market feeds active. Health is 200 and readiness is intentionally 503 with entriesAllowed false. Migration count remains 29. The original user, two wishlists, thirty members, two saved screens and all order/position journal rows match the backup exactly; no original News ID is missing. The user-owned Fable audit retains its exact pre-cutover SHA-256. See `ui-indicator-local-acceptance.json` for the measured record.

A normal signed-in browser verified the loaded Desk/preparation state, News updated at 16:38 UTC with 195 matching stored records / 47 shown, and Monday 16 November Earnings with 59 events / two reported. Both direct Buy and Sell tickets show the global pause and expose no Review order button. The published MU daily chart displays the verified close 778.31, SMA50 943.6894, SMA200 765.4167, RSI14 42.4539 and ATR14 58.9661 with actual 15 November receipts. The chart screenshot is exported as `TradingFlow-published-chart.jpg`.

The user separately approved an HTTPS ngrok tunnel to this published app, retaining login and paused trading with request inspection disabled. Tunnel PID 32108 runs from `C:/Users/manis/Documents/Codex/2026-11-14/c/work/ngrok-preview-57ee499`. Anonymous Desk redirects to sign-in; an anonymous same-origin chart request is 401; the normal sign-in page was observed through the public URL. No public sign-in credentials were entered by the agent. The requested app and tunnel remain running while edits continue in the isolated checkout. Build scratch is retained for the newly authorized NEXT-1–NEXT-4 work; original/provider data and acceptance evidence are retained permanently unless separately approved for removal.
