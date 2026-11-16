# Read-only chart acceptance implementation — 16 November

Status: compiled Release `319e79a` installed and locally verified at 08:22 UTC;
separate A and B reviewers accepted the installed release. The diagnostic has not
been executed against the provider. User approval is recorded in the
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

Logs are retained under this worktree's `.tmp/chart-acceptance/`. The final release
results and installed-host observations are recorded below.

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
These screenshots contain synthetic prices. This was the component checkpoint;
final release tests subsequently passed as recorded below.

The first full release attempt found the central runtime-registry guard: the
initial implementation registered its hosted shutdown hook directly in Program.
The run was stopped, its failure log retained, and no owned test/browser process
remained. The hook and its optional dependencies now use the existing central
registry. It is hosted only on enabled paper runtime and registered after the SIP
owner so reverse shutdown drains it first. **26/26** focused composition and
diagnostic checks passed, including the original guard and four new off/paused/
feed-only/enabled cases. A and B independently accepted the correction. The complete
release suite then passed against the corrected commit. No test was disabled.

## Final release and local installation

Frozen product commit: `319e79af72c8b5f52276ae3490ba437b707c2639`.
The serialized final backend run passed **4,180**, failed **0**, with **5** explicit
opt-in cases unrun (real Finviz export and four profiling/memory workloads).
The final browser run from `tests/ui` passed **246/246** in 3.3 minutes with no
skips or retries; its owned isolated host and synthetic data were cleaned up.
An earlier full browser command from the repository root passed 242 but failed
four fixtures resolving relative paths from the wrong working directory. Its
log is retained; the successful rerun used the intended directory, without code
changes or relaxed assertions. Both reviewers accepted these final receipts.

Release publication succeeded. Every one of the 243 staged files matched the
retained SHA-256 manifest. The installed Web DLL hash is
`D6315C5C91E51784D81E1E3D2FCB5A43AC697C370C0558B901D109686CC1287D`.
At 08:19:47 UTC the exact configured paper account was ACTIVE and flat with no
open orders, queued jobs, active candidates, intents, risk reservations or enabled
Preparation schedules. Only the verified old site processes were stopped. The
listener took a moment to disappear; the next observation confirmed it absent.
A consistent SQLite backup at `before-chart-release-319e79a-20261116T082053Z.db`
passed integrity checking and retained all 15 jobs. The stopped-site preflight
initially refused startup while the prior host lease remained active; it passed
at 08:21:57 UTC after natural expiry. No lease was edited.

Local main was fast-forwarded without GitHub synchronization. The user-owned
Fable audit remains unstaged and unchanged, SHA-256
`53443A75929C634CF52B7FDB658A295108A270E4EBDBEDC4A01B41E2EED6F2BF`.
Previous binaries remain in `C:\project\tradingflow-site\bin-before-319e79a-20261116`.
The unchanged standard launcher started app PID **38368**, command parent **14440**,
hidden launcher **46972**, listening only at `127.0.0.1:10088`. No ngrok process
was present. The runtime is enabled with `strategy_gated` manual policy; the
diagnostic is default-off. No strategy was activated or broker order submitted.

At 08:22:38 UTC, `release-running-319e79a.json` verified the installed hash and
active paper host's exact code version, all **15 jobs across all 20 columns**
against the retained baseline, **32 migrations**, and empty order intent/event/
risk tables. Readiness was ready, order stream connected, completed recovery
healthy, account reconciliation clean with no differences. The subsequent fresh
paper preflight again verified zero positions/orders and no unrelated work.
Receipts and the backup are retained in `work/operator-closure-20261115`.

Signed-in browser checks on the installed app showed the MU chart status beside
its symbol, retained search/chart selection, and real retrieved SIP candles.
Operations did not expose the default-off diagnostic. It showed both Finviz
screens awaiting their first 15:00 Berlin slot. The Desk showed overnight quotes
with their own timestamps, while the regular-session chart correctly showed
closed and Friday's completed candles. This is installed display verification,
not proof of regular-session live forming candles or the proposed quote repair.
Operations additionally reported no selected-list SIP quote and no external
warm-up manifest; neither is relabelled as healthy by the release checks.

A independently rechecked the installed and prior DLLs, live process chain and
loopback listener, launcher posture, readiness and all 15 jobs/20 fields against
both the pre-cutover backup and original baseline. B independently rehashed all
243 installed files, verified the exact file set, source/audit identity, listener,
launcher defaults and database preservation. Both accepted the installed release;
neither represented future market observations as complete.

After committing the reviewed changes, removed only the owned duplicate
`.tmp/chart-acceptance/publish-319e79a`: all 243 files matched the retained
publication manifest, there were no database files or reparse links, and the
resolved target stayed inside this checkout. Freed **61,051,503 bytes** (58.22 MiB).
Installed and prior binaries, consistent database backups, provider data, test
receipts, screenshots and review logs remain retained for pending acceptance.
The user-requested local site remains running. No GitHub sync occurred.

## Operational acceptance remains separate

The installed site now serves the reviewed `319e79a` release. Scheduled Finviz first receipts, the exact
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
