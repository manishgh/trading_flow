# Independent live quotes for the Desk

Status: approved by the user on 17 November 2026 ("Approve the Desk quote repair");
implementation, tests and review status are in
[the implementation record](../../verification/desk-live-quotes-implementation-2026-11-17.md); not yet
installed on the local site until both reviewers accept.
User requested real-time Desk prices through a browser websocket on 16 November.
A and B independently reviewed the proposed direction and required the membership,
bounds and acknowledgement rules below. The approved chart acceptance implementation
continues separately.

## Observed problem

The existing Alpaca SIP websocket feeds LiveQuoteBook. The modern Desk relays its
latest quotes through an SSE loop which also awaits DeskSources.ResolveAsync and
the complete account/news/stock snapshot. A slow snapshot can stall quote delivery.
The browser also labels silence longer than ten seconds as “polled every 5s”,
which does not establish a websocket failure. Prices are bid/ask midpoints; they
are not last-trade prices. Overnight fallback quotes are separately sourced.

## User flow and smallest reusable change

Opening a Desk stock group keeps the existing source/search/page selection and
snapshot flow. Its visible stocks receive a separate authenticated SignalR quote
watch over the browser websocket, using the existing leased upstream SIP socket
and display-only subscriptions. No second provider connection, candle warm-up,
strategy preparation or broker command is introduced. Full account/news/technical
snapshots can be slow without delaying quotes for the already visible stocks.
The initial stock table still waits for its first normal snapshot; quote updates
received before it are bounded and applied only to that matching view.

The existing resolver issues an opaque, protected, user-bound, single-use watch
capability for its exact view epoch before the full reader join. It is sent as a
named SSE view event and consumed in the websocket invocation, never in a URL,
storage, log or public cache. The server retains the authoritative symbol set,
source/search/page/revision and prepared-handoff ownership. It does not resolve a
second provider screen. The lease expires after 120 seconds without a successful
resolver renewal; a still-visible saved list remains inspectable after preparation
expiry, subject to its existing owner checks. Source replacement, membership
removal, logout and connection disposal revoke the previous quote view. A stalled
resolver, or full snapshot delaying its next renewal, may therefore stop quotes
after 120 seconds with an explicit reason;
it cannot silently extend old Finviz membership. Reconnect requires a fresh
capability from the normal SSE owner, not replay of a consumed capability.

Quote watches coalesce the latest accepted value per stock (maximum 50 per view),
emit at most four frames per second, and serialize writes. Each frame is at most
32 KiB; a send which cannot complete within five seconds ends that watch. Limit
two Desk quote watches per user, 32 globally, independently of chart slots; reject
over-budget attempts with a visible reason. Limit messages to 4 KiB and watch
changes to ten per minute per user. These same view budgets include pending,
unconsumed capabilities: at most one per bounded view, deduplicated renewals, and
expired/replaced records removed. Opening extra SSE streams cannot allocate
unbounded capabilities; the existing snapshot fallback remains available.
Each socket lasts at most five minutes with
normal authenticated renewal; an unbound socket must submit its capability within
ten seconds. Avoid per-tick whole-table rebuilds: patch prices/ages/buttons for
changed rows and selected stock, throttling any sorting to the frame cadence.
Reuse the existing `ChartEndpoints.HasSameOrigin` origin/cookie guard and apply
socket/invocation budgets before accepting stream data. Logout revokes this
browser's offered selection and socket; security/session expiry bounds other
connections rather than pretending the browser can revoke every device.

New Desk subscriptions cannot expand the display quote union beyond 500 symbols,
counting active, in-flight, acknowledged and the existing two-minute linger set.
Existing Orders/mobile owners are preserved and never evicted to admit a Desk.
If they already exceed this threshold, refuse additional distinct Desk symbols;
Desk symbols already in the union can share them. Execution subscriptions have
priority and are outside this display-only limit. Cleanup cannot remove symbols
still required by another owner. Preserve the Orders and mobile SSE endpoints;
remove only the modern Desk's duplicate quote loop/subscription ownership.

## Evidence and provider acknowledgement

Keep browser connection, upstream subscription acceptance, per-stock quote age and
market session separate. Never infer polling, current prices or provider acceptance
from silence or a successful subscribe command. Preserve each quote's actual feed
on websocket and snapshot paths. A midpoint can stay unchanged while trades occur;
label it explicitly and keep bid, ask and provider time available.

Atomically publish the latest provider full-channel acknowledgement with receipt
ordinal and lease/connection generation. Do not add a competing consumer to the
existing acknowledgement queue or let it grow with irrelevant quote-only replies.
Use a separate quote-membership observation and command watermark. A quote-only
acknowledgement carrying an older execution set must neither satisfy nor fail an
execution subscription wait. Bound quote reissue to two attempts after five-second
timeouts, then show unacknowledged/degraded until that symbol's membership or connection
change; no socket replacement just for this display failure. Start display quote
forwarding after leased authentication/receive startup independently of execution
history warming, without blocking execution/status/bar processing.

Stamp received quotes with current connection and subscription generation and
receipt time. Exclude removed, stale-generation or unacknowledged values from
the live path, reject implausibly future provider clocks before latest-state
storage so they cannot pin the displayed price, and recheck before emission.
Merge browser snapshots against newer
quotes for the same authorized view by generation and provider timestamp; never
turn stale fallback data into live data. Closed/overnight views keep honest last
observations and actual source labels. The paused-host registration contract is
unchanged; show upstream unavailable while preserving allowed snapshot reads.

## Files, checks and delivery

Expected files: DeskEndpoints, DeskSources/view contracts, a small quote-view
registry and SignalR hub, existing endpoint/origin/auth guards, LiveQuoteBook,
AlpacaStreamClient and AlpacaMarketStateStreamService display subscription handling,
watch-desk.js and affected tests. No database schema or core-data deletion.
The separate requested header adjustment relocates existing Price & volume/status
nodes beside the Desk symbol and wraps on narrow screens; it creates no duplicate
status fields and preserves full-chart navigation and ordinary chart preferences.

Focused tests cover authorization/prepared cross-owner capabilities, view expiry
and revocation, slow snapshot with fresh quote delivery, bounds/slow consumers,
partial/stale ACKs, concurrent execution ACK waits, disconnect cleanup, actual feed,
midpoint labels, out-of-order snapshots and closed/overnight behavior. Separate A
plan/execution and B code/design review follow implementation. Then serialize full
backend/browser release checks, compile/copy to the existing separate local site,
and verify the installed build plus real market quote receipts. Never restart
during the first Finviz capture window or while the approved paper test has exposure.
No GitHub sync. Expected work is one additional display transport component and
its shared subscription fixes, not a trading or research change. Runtime memory
and provider subscription work are bounded above; no history download is requested.
