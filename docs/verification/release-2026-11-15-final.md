# Final local paper and chart release

The user authorized installing each reviewed Release build in the existing separate
`C:\project\tradingflow-site` folder and leaving the local app running. This does
not authorize new strategy activation or broker orders. The public ngrok tunnel
was stopped before enabling paper runtime; its earlier approval covered a paused
app. The normal manual ticket remains available with the strategy-gated policy.

## Real weekly candle reconciliation

On 15 November at 22:51 UTC, the signed-in installed Desk showed MU weekly bars
matching independently aggregated retained SIP daily bars, including every daily
constituent and the archived exchange calendar:

| New York session dates | Scheduled/received days | Open | High | Low | Close | Volume |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 9–13 November 2026 | 5/5 | 703.18 | 827.58 | 689.65 | 778.31 | 153,195,493 |
| 8–11 September 2026 | 4/4 | 1,036.24 | 1,042.24 | 967.23 | 975.11 | 98,592,608 |

The second week starts Tuesday: the retained calendar contains no 7 September
session. The UI uses these actual first/last scheduled dates. Decimal arithmetic
used first open, maximum high, minimum low, last close and summed volume, without
calling the application projector. Returning to five-minute candles restored the
prior indicator selection and completed-candle readout.

Evidence under `V:\trading_flow\data\raw\alpaca\2026-11-15`:

- Daily payload `20261115T2222124723389Z-f18856dc2ac749cba38fd7be2ca67c30-bars-rest-page.json`,
  SHA-256 `c02b25bbb87267f6ba2fd72f707fdbef9cdfc0eaf6a614c3b28f1c82fe35c156`.
  HTTP 200; 500 MU daily bars; SIP, adjustment all; no next-page token;
  received 22:22:12.4723389 UTC.
- Calendar payload `20261115T2222120389126Z-37d32e6bc5c9438c8e30f34884b05d1b-calendar-rest-page.json`,
  SHA-256 `9861c1feb045a2e8e9d9523bd480dc83a33b9b90ae5239d62529c3ab6099cda5`.
  HTTP 200; 555 sessions; received 22:22:12.0389126 UTC.

Both payload hashes match their retained manifests. This is closed-market chart
acceptance using existing receipts, not a new economic study. Real basis-break
and unfinished open-week acceptance remain unverified. Synthetic cases cover
those rules. Live eligible trade → forming preview → completed SIP candle and
authorized real private paper-fill reconciliation also remain pending.

Reviewer A independently read both payloads/manifests, checked hash and byte
length, converted daily timestamps to New York session dates, and recomputed all
fields with decimal prices and integer volume. All nine constituents and both
UI readouts matched. A accepted this narrow real-data checkpoint separately from
the source-code review.

## Release verification and installation

Executable code is frozen at `d77e22b0a4c31d535fb4d57f97f7d5df22f20e85`.
Separate A plan/execution and B code/design reviewers accepted the components,
including 161 affected backend cases and 14 browser cases. The final full backend
suite passed 4,164 tests, zero failed and five explicit opt-ins skipped (4,169
total), with runner-reported duration 19m24s. Receipt:
`.tmp/release-20261115/integrated-release-final.trx`. Reviewer A independently
parsed all 4,169 unique test IDs and outcomes; the TRX wall duration is 19m30s.
The full isolated browser suite passed 240 tests in 4.1m and removed its owned
host/database/cache/run artifacts. Both reviewers independently parsed the final
backend and browser receipts and accepted the automated release gates. Release
publication completed successfully with warnings treated as errors. An initial
sandboxed publish invocation exited before producing output; the explicit .NET
invocation with the required execution permission produced the successful package.

The earlier startup exposed an existing two-day job-pruning defect: 14 terminal
job records were removed. Exact insert-only recovery restored all 15 original job
rows and all 20 columns from the stopped-host snapshot, while retaining newly
collected provider data. Independent reviewers accepted recovery and removal of
the obsolete pruning path. The installed fixed startup preserved those same 15
rows across all 20 columns after the actual restart. Recovery receipts and
snapshots are retained, not treated as disposable scratch.

Reviewer B accepted the serialized local cutover with two explicit checks: the
old `bdb3dfd` binary is retained as evidence but must not be restarted using the
paper-enabled launcher, because it contains the confirmed pruning defect; any
fallback must use a verified retention-fixed build. Recorded process IDs must be
corroborated by the actual Windows process command and TCP listener, independently
of the verification helper's supplied PID arguments.

## Installed runtime and actual UI

At 23:15 UTC on 15 November (00:15 Berlin, 16 November), the existing hidden
launcher started the new package in `C:\project\tradingflow-site\bin`.
All 240 staged files matched the compiled package hashes. Web DLL SHA-256:
`71E9C5460136FDDC4A3E49BA3A5210F4F894C6F32CB0DFA9D7E032719B3A308B`.
App PID 49844, launcher PID 41484 and command parent 60832 were corroborated
through Windows process commands and the loopback-only port 10088 owner.
The active paper host records exact code commit `d77e22b0a4c31d535fb4d57f97f7d5df22f20e85`.
Local main was fast-forwarded; the user-owned audit remained unstaged with its
unchanged hash. No GitHub synchronization occurred.

Fresh before/after broker checks matched the configured ACTIVE paper account:
zero positions, open orders, queued jobs, active candidates, intents, order events
or risk reservations. The normal strategy-gated policy, market feeds and capture
schedule remain enabled. Private chart grants and environment overrides are
absent; ngrok remains stopped. No new strategy or order was authorized or queued.

At 23:16:32 UTC the installed verification receipt confirmed 32 migrations,
exact preservation of all 15 jobs/20 columns, connected order stream, clean
account reconciliation, a healthy completed recovery cycle and entries allowed.
Two preceding HTTP 503 receipts reported only the normal in-progress recovery
cycle; its completion timestamp advanced and no gate was manually reset. These
receipts are retained rather than claiming continuously unblocked readiness.

The signed-in browser retained MU search/selection, five-minute candles and named
indicators through reload and navigation. Buy/Sell remained visible and clickable;
no confirmation was submitted. Chart details exposes the forming preview unchecked
by default. Temporarily selecting it showed **Not streaming**, without inventing
a current candle; it was restored to off. The selected completed values remained
O783.80/H783.985/L776.79/C778.40/V1,094,191. Orders showed zero positions/orders.
News showed 43 displayed / 87 matching stored four-hour records. Earnings showed
59 next-reporting-day events, two reported; the Finviz calendar receipt contained
956 events and was received at 23:15:22 UTC. Source evidence showed successful
Finviz and Alpaca refreshes after restart, with 88 selected tickers complete and
zero partial/failed. SEC remains explicitly not queried without its User-Agent.

Operational receipts live in the owned `work/operator-closure-20261115` folder:
`release-running-d77e22b.json`, `installed-processes-d77e22b.json`, preflight
receipts, the two transient readiness receipts, publish log and preserved
`before-d77e22b-restart.db`. The older `cutover-after.db` and restoration receipt
remain the independent exact-job baseline. No original dataset was deleted.

Cleanup removed only the two duplicate task-owned publication folders after
matching their Web DLL hashes to the retained installed/archive binaries and
checking resolved paths, links and absence of database files. This freed
121,500,374 bytes. The browser runner separately removed its owned synthetic
database/cache/run artifacts; that byte count was not measured. Recovery snapshots,
provider receipts, test reports, operational scripts/logs and prior binaries remain
retained for the startup incident and release review.

After both installed reviews completed, cleanup also removed 33 task-owned
generated bin/obj folders and the copied browser dependencies from the isolated
chart worktree (556,304,405 bytes). No matching build/test process was active;
resolved paths remained in that worktree, with no links or tracked source files.
The separately installed app remained running. Evidence manifests for both cleanup
steps are retained in the operational evidence folder.

## Independent final acceptance

Reviewer A (plan/execution) and reviewer B (code/design) each independently checked
the actual installed DLL hash, Windows parent chain and loopback listener, and
queried the live operator database read-only against the original stopped-host
baseline. Both confirmed the exact 15 rows and 20 fields, 32 migrations and empty
order journals, and accepted this local operational release. Neither implemented
the reviewed components or wrote broker state. The earlier automated gate and
weekly receipt acceptance remain separately recorded above. Future market-session
checks remain pending; this release does not claim strategy profitability or
authorize automatic stock trading.
