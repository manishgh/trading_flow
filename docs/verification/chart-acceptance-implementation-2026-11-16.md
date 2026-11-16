# Read-only chart acceptance implementation — 16 November

Status: component implementation accepted by separate A and B reviewers; not installed and not
executed against the provider. User approval is recorded in the
[bounded design](../design-review/2026-11-16/live-chart-acceptance-session.md).
The separate [Desk quote repair](../design-review/2026-11-16/desk-live-quotes.md)
is proposed only and awaits explicit approval.

## Scope implemented so far

Default-off administrator-only loopback Operations controls start one five-minute
AAPL/1m/regular observation on the existing SIP connection. The backend owns its
deadline, exclusive warm cancellation, bounded exact receipt archive and cleanup.
One-use chart correlation stays in the initiating tab's memory and is sent in
watch options, outside the normal series/cache key. It cannot reconnect or transfer
to another watch. Operations presents collection states and an opaque result
reference; it never declares an automatic live acceptance pass.

The user's direct Desk layout request moves the existing Price & volume, market
status and completed/forming note beside the stock symbol. It moves the actual
nodes, including the full-chart link, instead of duplicating status text. Mobile
wraps the summary below the symbol/actions. Navigation restores the nodes before
remounting and preserves ordinary chart preferences.

## Verification receipts to date

- Early `dotnet build src/TradingFlow.Web/TradingFlow.Web.csproj -c Release`
  succeeded with zero warnings and zero errors. This preceded review corrections.
- Three page-local browser fixtures initially passed; the affected integrated
  chart/Desk suite then passed **61/61** in 1.7 minutes against its isolated host
  and temporary database. Its cleanup reporter confirmed removal of owned data
  after host shutdown. These fixtures contain synthetic prices, not live evidence.
- A found that explicit Stop detached the watch before the server accepted Stop.
  The UI now awaits the Stop response before disposing the chart. B also required
  a stop intent received during a pending status read to be retained. Four focused
  browser fixtures passed after these fixes, including response-loss/no repeated
  Start, Stop ordering, delayed status read, and no diagnostic reconnect/history
  substitution. A real SignalR-framing fixture in the 61-case run verifies fixed
  AAPL/1m/regular options and untouched ordinary preferences.
- The first sandbox-only fixture attempt could not locate the installed browser;
  the normal installed-browser run was used. A test fixture initially collided
  with read-only `window.closed`; it was corrected before the passing result.
  An integrated command omitted the repository Playwright config and was stopped;
  the 61-case result uses the explicit isolated-host config. Neither harness
  failure contacted the operator app or submitted an order.

Logs are retained under this worktree's `.tmp/chart-acceptance/`. Full final
backend/browser release checks and installed-host verification remain pending.

## Independent component review accepted

A is the separate plan/execution reviewer; B is the separate code/design reviewer.
Neither implements or executes operational actions. Their findings included fresh
durable recovery and empty-work admission, exact raw bar receipt/disposition
binding, diagnostic-only subscription failures preserving the shared connection,
causal acknowledgement matching despite older quote-only acknowledgements, strict
loopback/forwarding checks and no-store responses. All were corrected and both
reviewers independently accepted the component for commit and full release checks.

The final focused backend receipt passed **33/33** in 33 seconds. The subsequent
**12/12** acceptance slice includes the added actual HTTP mapper/middleware test
for default-off, anonymous/non-admin, missing CSRF, foreign origin and proxy
refusals, plus the synthetic hard-deadline lifecycle case. These runs overlap and
are not additive counts. The deadline test cancels exclusive warm-up while
preserving another stock on the same leased connection. The latest browser
layout run passed **4/4**, covering actual isolated administrator/default-off
Operations, fixed AAPL SignalR options/preferences, and desktop/phone layout.
It supplements the four final Stop/transport cases and earlier 61-case run.
Root visually inspected the retained 1440px and 390px fixture screenshots; status
is beside the symbol on desktop and wraps without page overflow on the phone.
These screenshots contain synthetic prices. Full release tests remain pending.

## Operational acceptance remains separate

The installed site still serves the previous reviewed `d77e22b` release. It has
not been replaced by this worktree. Scheduled Finviz first receipts, the exact
approved one-share paper execution, browser-visible live forming/completed
reconciliation and exact private entry/exit evidence remain external checks.
Morning actual receipts are in the paper acceptance plan. Do not execute early.

After the authorized paper test is flat and the normal strategy-gated host is
restored, enable this diagnostic only through the temporary process environment
`ChartAcceptance__Enabled=true`. Repeat the fresh account/work/health checks.
There is one invocation per enabled process and one operationally approved
invocation: no restart while enabled, no retry after incomplete evidence, and no
second invocation without authorization. Remove the temporary enablement afterward
and verify the normal strategy-gated local host with ngrok stopped.

The retained evidence must be decoded read-only and independently reconciled:
archive hashes; provider ACK, fence/generation and ordinal continuity; warm and
eligible full-minute clocks; preview actually seen in the browser; official raw
bar and canonical disposition; completed OHLCV and any later revision. Server
emission is not proof of browser display. `MatchedMinutes` is a candidate for this
review, not a substitute for it. Private entry/exit grants are a separately scoped
post-flat operator step, then removed and the old capability checked for denial.
