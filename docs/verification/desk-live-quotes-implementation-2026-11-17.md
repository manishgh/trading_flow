# Desk live quotes — implementation record, 17 November 2026

Design: [desk-live-quotes.md](../design-review/2026-11-16/desk-live-quotes.md), approved by the user on
17 November 2026. Display only: no strategy, order, candle warm-up or research behaviour changed.

## What changed

**Before.** The Desk's event stream (`/api/desk/stream`) resolved the stocks, read the full snapshot
(account, news, technicals, quotes) and only then relayed streamed quotes for five seconds. A slow snapshot
therefore delayed every price. The page called any ten-second silence "polled every 5s", although nothing was
polled. Quote subscription replies from Alpaca went into the same queue the execution path reads: a reply to a
quote command that still listed the previous bars/trades/statuses set made the next execution wait fail
("acknowledgement does not match"), and the shared SIP socket was replaced.

**After.**

| Piece | File | Behaviour |
|---|---|---|
| Execution reply matching | `src/TradingFlow.Alpaca/AlpacaStreamClient.cs` | Every provider subscription reply is queued (at most 64; the oldest are dropped) and also published whole. Before each bars/trades/statuses command the client records the arrival position and the execution channels Alpaca last reported. The execution wait skips replies read before its command and replies that still show the pre-command execution channels (those answer a quote command). A quote reply therefore neither satisfies nor fails an execution wait, and an unanswered or rejected quote command leaves nothing behind. |
| Quote confirmation | same, `WaitForQuoteSubscriptionAsync` | Confirms a quote change only from a reply read after the command that lists every added and no removed symbol on the quote channel. Never reads the execution queue. The connection is identified by the client instance itself, one per leased connection. |
| Display membership | `AlpacaMarketStateStreamService.DisplayQuotes.cs` | Each displayed symbol carries its connection generation, subscription generation and state: pending, acknowledged, or unacknowledged after two five-second attempts. No socket replacement for a display failure. Forwarding starts right after the leased socket and its reader start, not after execution history warming. Quotes for symbols nobody displays are dropped. |
| Display limit | same, `ReplaceBoundedDisplayQuotesAsync` | New Desk symbols may not grow the display union past 500 (scopes, in-flight/acknowledged subscriptions and the two-minute linger set). Orders/mobile owners are never evicted and stay unbounded, as the design states; shared symbols are always allowed. |
| Quote book | `LiveQuoteBook.cs` | Stores receipt time and generations; refuses a provider time more than 5 s after receipt; a newer subscription outranks an older one. |
| View registry | `Services/Desk/DeskQuoteViews.cs` | Server-held Desk views: 50 stocks, 120 s lease renewed by the event stream, one-use capability (30 s) bound to the user and an admitted socket, 2 views/sockets per user and 32 in the process, 10 watch starts per user per minute. Each view instance owns its own subscription scope, so releasing an expired instance never removes a re-created one. Revoked when its stream ends. On sign-out every view of that user is revoked and its id stays closed for 10 minutes, so a stream still running in another tab ends instead of re-creating it (the user approved this on 18 November 2026; the design said "this browser", but matching the sign-in cookie fails when a sliding cookie is re-issued). |
| Quote socket | `DeskQuoteHub.cs`, `Services/Desk/DeskQuoteWatch.cs` | SignalR at `/api/v1/desk/quotes/hub`: WebSockets only, same-origin guard, 4 KiB inbound messages (global SignalR limit), over-budget sockets refused with a visible reason, sockets without a watch closed after 10 s (at connect and after a watch ends), 5-minute watch lifetime, at most four frames per second carrying only changes, prices only for an acknowledged subscription. A frame that SignalR cannot accept within 5 s closes the socket; SignalR accepts a frame once it fits its 64 KiB send buffer, so a stalled browser is closed when that buffer fills. SignalR logging is held at Information so invocation arguments (the capability) are not logged. |
| Event stream | `DeskEndpoints.cs` | Each cycle resolves the stocks, renews the view and sends a `view` event (capability only when needed) **before** reading the full snapshot. A signed-out view ends the stream. The old in-stream quote loop and its subscription were removed. Orders and mobile quote streams are unchanged. |
| Page | `wwwroot/js/watch-desk.js`, `Pages/Desk.cshtml` | Opens the socket from the `view` event after the previous socket has closed, patches only changed rows, keeps a newer streamed quote over an older snapshot quote, keeps each quote's actual feed, labels "Live" only for a streamed quote on the open socket whose subscription Alpaca currently confirms ("Snapshot" or "Stream stopped" otherwise), and reports socket state, refusals and Alpaca confirmations separately from quote age. The price column says "Mid price"; tooltips name the bid/ask midpoint, bid, ask, feed and provider time. |

## Accepted trade-offs (from review)

- Display quote messages share the provider read loop with execution warming, because the design requires quote
  forwarding to start independently of warming. Quote handling is a parse and a dictionary write.
- The Orders and mobile quote streams keep their unbounded subscriptions; they can fill the 500-symbol union, after
  which new Desk-only symbols are refused with a stated reason.
- A refused socket leaves its unconsumed capability pending for up to 30 s before the next renewal replaces it.
- The 10 watch starts per user per minute (design limit) can refuse live quotes after many rapid Desk source or page
  changes; the page states the reason and keeps snapshot quotes.

## Verification

Focused tier (component checkpoint), Debug build, 17 November 2026:

- `dotnet build src/TradingFlow.Tests/TradingFlow.Tests.csproj --disable-build-servers`: 0 warnings, 0 errors.
- Focused and direct-consumer .NET tests (`DeskLiveQuoteTests` 15, stream lifecycle with three new display-quote
  cases, quote book, chart-acceptance acknowledgement, provider parsing, Desk read model, chart endpoints/transport,
  operator access including the logout host, production composition, quote service, stock sources):
  **164/164 passed** after the review fixes (161/161 before them).
- Browser: `npx playwright test specs/watch-desk.spec.js specs/preparation.spec.js specs/stock-chart.spec.js`
  against the isolated test host: **85/85 passed twice** after the review fixes. Includes a stand-in-client case
  (capability absent from URL and storage, row patching, newer streamed quote kept over the next older snapshot,
  relabel after the socket ends) and a real-host case (the real Desk stream issues a capability, the real hub
  accepts it and streams a frame; observed with a followed AAPL marked `upstream_unavailable`, as the test host has
  no Alpaca keys). Synthetic data only; the cleanup reporter deleted the owned test database, cache and artifacts.
- One earlier run of the real-host case failed (no frame within 15 s) when run after the preparation and chart specs
  as the shared test user. It passed alone and in the Desk spec. The cause was not captured; the likely cause is the
  per-user watch-start or socket budget consumed by earlier real Desk pages within the same minute. The case now
  signs in as the seeded administrator.
- Not covered by automated tests: endpoint-level ordering of the `view` event before a slow snapshot, the 5 s
  send-stall close, and closed/overnight frames through the socket.

## Independent review

Reviewer A (plan/execution) and reviewer B (code/design) reviewed the first implementation; their findings and
resolutions are summarised in the table and trade-offs above. Their re-review verdicts are recorded below.

- **Reviewer B re-review:** accepted all nine findings (seven fixed, two recorded design trade-offs) by reading the
  code; B did not re-run tests. Open, not verified: the delay a full 500-symbol display union adds to the shared
  provider read loop during execution warming has not been measured. Normal Desk use subscribes at most 50 symbols
  per view. B also noted a brief remaining window in which a second tab's new socket can be refused once (shown on the
  page, recovers at the next renewal).
- **Reviewer A re-review:** accepted findings a–e and the additional fixes by reading the code; A did not re-run
  tests. Acceptance is conditional on the user approving one deviation from the design: sign-out ends the user's
  Desk live-quote views on every device (the design says "this browser"); other devices' Desk pages reconnect with new
  views. Remaining low-impact gap: a stream whose view had already expired or been refused before sign-out can create
  a new view afterwards and hold its subscriptions for up to about five minutes; it cannot stream to the signed-out
  browser. A recommends, without requiring, an endpoint test with a stalled snapshot before installation.
- **User decision, 18 November 2026:** the user approved sign-out ending Desk live-quote views on all of their devices ("Yes, all devices"). With that, both reviewers accept the change. Committed and installed as recorded below.


## Installation on the local site, 18 November 2026

- Committed as `786dbaf` on local `main`; not pushed (no GitHub synchronization).
- Before installing: the local site was **not running**. Windows restarted at 11:58 local time on 18 November, and the
  site's last log line was at 01:29:59 UTC; it had not been started again. Data drive `V:` was mounted with
  `V:\trading_flow\data` present.
- Exposure check at 11:07 UTC (read-only Alpaca paper API, configured account, status ACTIVE): `/v2/positions` returned
  `[]` and `/v2/orders?status=open` returned `[]`, so 0 positions and 0 open orders. Read-only database check: 15 jobs
  (9 completed, 3 failed, 3 cancelled; none queued or running), 30 candidates all expired, 0 order intents, 0 risk
  reservations. The `runs` table still lists 63 rows as `running`; these are the stale records described in audit
  item B4, not live work.
- `dotnet publish src/TradingFlow.Web/TradingFlow.Web.csproj -c Release` succeeded into a staging folder (244 files).
  The previous build was kept as `C:\project\tradingflow-site\bin-before-786dbaf-20261118`; the new build became `bin`.
  Web DLL SHA-256 `5CCFA66E8A2E3179245C7FFC5EB197768005F09CFFA57BF3C7B4B1120843F4DA`. Started with the existing
  launcher (`update-site.ps1 -StartOnly`, strategy-gated paper policy unchanged) at about 11:09 UTC.
- After start: `/health` HTTP 200 within about 10 s; `/health/trading-readiness` reported `ready`, entries allowed,
  order stream connected, recovery cycle healthy, account reconciliation `clean` with 0 differences, no blocks.
  The SIP market-data stream connected. No Error or Critical log lines; the only warnings were the pre-existing
  "durable backtest result marker is not available" messages also present before the restart.
- Served `/js/watch-desk.js` contains the quote socket path; anonymous requests to the quote hub and the Desk stream
  are refused with HTTP 401.
- Signed-in check on the installed site, 12:55 UTC (07:55 New York, pre-market), operator signed in by the user,
  Desk group "All followed stocks" (30 stocks): the connection line read "15/30 current quotes · 1 unavailable ·
  live-quote socket · Alpaca confirmed 30/30". Over 15 seconds, 22 of the 30 displayed prices changed without a page
  reload. Fresh rows showed provider times seconds old (for example ASTS bid $27.95 / ask $28.10, SIP, pre-market,
  provider time 12:55:34 UTC). Rows whose last SIP quote was older were labelled "Delayed" with their real age (for
  example BXDC, last quote 12:01:22 UTC) rather than shown as live. SIVE showed "No quote": Alpaca's
  `/v2/stocks/quotes/latest?symbols=SIVE&feed=sip` returned HTTP 200 with no quote, and `/v2/assets/SIVE` reports an
  inactive, non-tradable OTC asset, so the empty value is the provider's, not a transport failure.
- Session scratch logs in `.tmp/desk-quotes/` were deleted after this check.
