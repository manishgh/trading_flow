# Bounded read-only live chart acceptance

Status: accepted for user presentation by separate A plan/execution and B
code/design reviewers on 16 November; explicitly approved by the user on 16 November.
Implementation and live execution are pending. The existing one-share paper test remains approved on its original
date, time window, prices, quantity and single-entry sequence.

## Why the existing check cannot finish on an idle host

The published chart watches completed data and an already-subscribed SIP stream.
It cannot subscribe a stock. The sustained production subscription owner is a
running discovery session; no such job is active. The manual ticket's trading-status
observation releases its subscription before dispatch and is not a full covered
minute. The forming book retains current trades in memory but has no raw provider
receipt archive. A later historical download would not prove its live receipt time.

## Proposed operator flow

After the approved paper test is flat and its temporary manual override is removed,
an authenticated administrator starts one **read-only chart check** for AAPL on
the existing loopback host, using **1m / regular-session** candles. Exactly one
invocation is proposed, without automatic retry. The absolute deadline is five
minutes after Start is accepted, including warm-up; reject a start less than five
minutes before the verified regular close. Use one unique scoped market-data
subscription on the existing leased SIP connection through
`IDiscoverySubscriptionSink.ReplaceScopeAsync`, plus the normal AAPL history
warm-up/backfill and canonical persistence that it requires. The current configured
warm-up reads the preceding **45 calendar days of SIP-adjusted 1m candles**, using
the shared cache and normal provider when needed. Its retained state is capped at
64,800 native timestamps. REST requests at most 10,000 rows per page and retries a
page up to three attempts; it guards repeated tokens but currently has no numeric
total-page cap. Do not call this a guaranteed seven-page download. The diagnostic
deadline must cancel its exclusive acquisition as well as its capture. It does
not create a discovery/paper job, select a strategy, generate candidates, or call
broker order/position mutation services. No future start is scheduled.

The feature is off by default. A temporary explicit host option enables its
administrator-only start/stop controls on Operations and same-origin, CSRF-protected
application endpoints. The server permits one session and one stock, rejects
overlap, and requires regular market hours and the existing healthy, enabled paper
host. Before starting, repeat the flat-account/no-unrelated-work check. The scope
is removed on manual stop, expiry, coverage failure, cancellation, lease loss or
host shutdown, using `RemoveScopeAsync` for only its unique scope and an independent
bounded cleanup cancellation budget. Reconnect or a changed fence/generation ends
the observation; it cannot continue across that gap. An interrupted check never
resumes automatically. Current removal only queues a subscription change while
warm-up can await the connection token. Add explicit ownership and cancellation
for warm/backfill work started solely by this diagnostic; expiry must stop that
work rather than merely hide the session. Give normal owners priority: if another
owner requests AAPL, detach the diagnostic, cancel only its separately owned
acquisition, and let the normal owner's existing subscription/warm path proceed.
Never cancel another owner's shared work or connection. Record scope removal and
actual subscription-removal acknowledgement separately; if another owner still
needs AAPL, no unsubscribe is expected. No diagnostic repair/backfill restart after
a gap. Remove the
temporary enable option afterward, preserving the normal strategy-gated runtime.

Use the stream service's normal warming and canonical reconciliation. No invented
quiet periods, shortened warm-up or bypassed preview conditions. Select AAPL in
the shared chart, enable its preview, and keep a completed candle selected. Observe
at least one full covered minute with eligible trades, its preview, and its official
completed SIP candle. An unfinished weekly candle remains pending. Unsupported or
incomplete observations are recorded as such, without repeating a paper order.

## Evidence and bounds

Add an optional receipt observer to the existing market-data receive path. While
this one session is active, retain the exact AAPL provider event JSON before typed
conversion, provider ID and event time, the real assembled-message receipt clock,
connection/generation identity and sequence. Include subscription acknowledgements,
eligible/ineligible trades, corrections/cancellations and canonical bar/revision
events needed to explain the covered minute. Distinguish an original event from
its containing batch; do not claim a full batch was retained when only its original
event span was saved. Acknowledgements have no provider trade ID/event clock;
retain their actual receipt/ordinal without inventing one. Never record
authentication requests, keys, account orders or
other stocks' price events. Reserve event/count/byte capacity before copying exact
original UTF-8 event spans while the receive buffer/document is valid. Do not
retain a pooled batch or reserialize a typed price record as an original receipt.
Do not allocate payloads when the queue or session evidence budget refuses them.

Reuse `IRawArchiveWriter` and the existing raw archive manifest/hash conventions;
no new database schema or historical crawl. A bounded observer queue may use at
most 1 MiB and 1,000 queued events. Limit the entire session to 10,000 events and
10 MiB of retained encoded payloads, including event envelopes and chart frames.
Write at most ten archive chunks of at most 1 MiB using the existing writer, rather
than one file per event. Bound result/coverage metadata to 64 KiB and include the
small per-chunk manifests in the documented storage accounting. Outside the 1 MiB
queue ceiling, allow one 1 MiB input chunk and the archive writer's one 1 MiB
`payload.ToArray()` copy: **2 MiB total in-flight writer allowance**. No extra
unbounded encoded copies. Each archive chunk's ReceivedAtUtc is its first event's receipt clock;
each contained event retains its own exact receipt and arrival ordinal. Finishing
an already committed chunk's paired manifest after expiry may not extend capture
or subscription. An oversized
event, queue overflow, write error or exceeded bound ends this acceptance session
as incomplete and releases only its subscription scope. Receipt writing never
blocks or changes the production market/trading pipeline. Original acceptance
receipts are core evidence and are not automatically deleted.

Observe the normal AAPL chart publication path separately: retain emitted forming
frames and the corresponding completed boundary with their real sequence, source
clocks and series identity, within the same byte ceiling. Bind this observation to
the initiating administrator's exact 1m/regular watch epoch, not every AAPL viewer.
Start issues a one-use server correlation consumed only by that administrator's
explicit diagnostic chart watch. Put it in optional watch options, never the
shared series/cache key. Another tab cannot claim or mix the observation.
Do not observe private chart payloads. Server emission and browser display are
separate observations. The browser check confirms the displayed values and completed
selection; independently recompute the minute from retained provider events using
the already reviewed SIP field rules, then reconcile the official bar. Preserve
differences instead of making provider output agree with the preview. A fully
covered minute requires acknowledged trade/correction/cancel channels, a continuous
connection generation, ingress ordinals and explicit handling of ignored events
or gaps, followed by finalized receipts and manifest hashes. Match the emitted
completed 1m candle to its actual official canonical receipt. Record the normal
processor's accepted/rejected/revised disposition for each relevant `b`/`u` event;
an arbitrary raw bar is not necessarily the candle the application accepted. Any missing fact
keeps the exact reconciliation incomplete.

After the already-approved manual trade is flat, private chart acceptance uses
only the current administrator, configured paper account, and that test's exact
persisted entry/exit intent IDs. Temporarily configure those narrow existing read
grants; inspect each Orders row's Paper chart and compare execution IDs, timestamps,
raw prices and quantities with the journal. Remove the grants after verification.
Do not keep a position open for a chart token or add another trade. This proposal
includes these exact test-owned read grants, without wildcard or other-user access.
This existing private-chart check is independently executable after the trade is
flat even if the new collector is not ready. Confirm the actual signed-in Identity
administrator and account, and verify that an old chart context is denied after
grant removal. No second trade is needed.

## Code, verification and release

Reuse `AlpacaMarketStateStreamService`, `IDiscoverySubscriptionSink`,
`AlpacaStreamClient`/parser receipt boundary, existing chart publication and
`IRawArchiveWriter`. Add a small session coordinator, optional bounded observer,
administrator endpoint/Operations controls and focused tests. Broker mutation
services must not be dependencies of the session coordinator. This is an additional
diagnostic capability beyond the prior no-new-subscription chart implementation;
it requires approval before implementation.

Separate A plan/execution and B code/design reviewers check the proposal, final
code and actual evidence. Test auth/CSRF, default-off behavior, TTL/stop/restart,
scope isolation, memory/byte ceilings, overflow/write failure, exact event identity
and receipt timing, unchanged production events/commands, and preview/completed
parity. Run the full backend/browser release checks serially, compile Release and
update the existing separate site. Avoid restarting in the 15:00–15:10 Berlin
Finviz window or during any paper exposure. If this cannot be ready safely for
today's window, record the live acceptance as pending; do not move the paper test
to another date or widen its terms.
