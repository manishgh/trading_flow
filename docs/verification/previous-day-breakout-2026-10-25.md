# Previous-session breakout day strategy — implementation and run verification

## Approved scope

The user approved the separate day strategy and then confirmed reuse of Volatile, excluding SOXL without changing the wishlist, April 21 through October 21, 2026 (New York, end exclusive), automatic groups of five, and $10,000 per independent portfolio. The existing setup retains 1% risk budget, 25% maximum position notional and four concurrent positions. No news filter or new parameter search was added.

Strategy: `technical.previous-day-breakout@1.0.0`, canonical hash `4f8e176fbaeb71d854863bc0e15b07a91494b0425da29c71f50964f83a1289a4`. Execution: `intraday-rth5m-limit-portfolio-v1`.

## Implementation checklist

- [x] Shared completed-bar signal/kernel, all three long/short entry patterns, original breakout stop and persisted candidate evidence.
- [x] Normal saved-plan preparation with native five-minute prices and the preceding completed exchange session; no inherited daily indicator warmup.
- [x] Chronological portfolio replay, capital reservation, adverse ambiguous fills, partial fills, original-risk protection, continuation-only extension and session-deadline liquidation.
- [x] Standard hashed hypothesis/fill/cutoff artifacts and independent signed-cash accounting verification.
- [x] Monthly strategy/cohort reports use actual New York source-session dates for day trades.
- [x] API/Web/Android selection and existing result details expose previous-session levels, entry pattern, breakout/confirmation clocks, entry expiry/deadline, structural/current stop, actual entry, target and resolution reason. The display explicitly shows its latest-50 sample count; full evidence remains retained.
- [x] Independent quant source review and independent engineer review; findings corrected before the economic run.
- [x] Save and start normal plan 17, five books, through the ordinary API and durable worker.
- [x] Complete all five books, audit exact economic outcomes and publish monthly results; missing-source months explicitly unavailable.
- [x] Independent approval of exact run-owned intermediate cleanup; core data and published evidence remain protected.

## Verification evidence

Evidence directory: `outputs/previous-day-breakout-20261025/verification`.

`dotnet build src/TradingFlow.Tests/TradingFlow.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false -nodeReuse:false -v:quiet`: succeeded, zero warnings/errors.

Expanded focused pass: 262 passed, two new fixture failures. These failures were corrected without weakening production checks: the synthetic source now declares the required SIP/all adjustment, and fixture cache roots are absolute and isolated. The subsequent 18-test workflow/integration recheck passed, including the full prepared-runner → verified manifest → monthly-report test. The final 17-test fixture-isolation recheck passed. An intervening attempt aborted before executing tests because vstest could not connect to testhost; the immediate no-build retry passed. TRX files retain all attempts.

The focused scope covers signal patterns/source gaps, execution ordering and partial fills, continuation protection, prepared data/calendar contracts, saved plans, catalog/horizon, month boundaries, native-ledger regression and artifact verification. Full-suite/release and real-device checks were not performed.

`dotnet build src/TradingFlow.Mobile/TradingFlow.Mobile.csproj --no-restore -t:Compile -m:1 -p:UseSharedCompilation=false -nodeReuse:false -v:quiet`: succeeded, zero warnings/errors. No APK was produced. Previously approved APK removal deleted seven APK files, 488,459,034 bytes, from the two project checkouts.

## Review corrections

The quant review corrected favorable targets claimed before ambiguous intrabar entries; exact-opening order precedence when a new trail was decided at that boundary; residual stop activation after a partial opening target exhausted participation capacity; preservation of real gap fills instead of retroactive quantity reduction; impossible nonpositive planned short targets; per-order cumulative fill metadata; and display of protection that had never become active before an opening exit. Missing source evidence now withholds economics from the first affected session while retaining earlier supported months.

The engineer review checked reuse of the existing saved-plan workflow, bounded history slices and display projection, work-item limits, common client formatting, preceding-session context and the exact calendar requests pinned by preparation.

## Data protection and assumptions

All original market/news/wishlist data and published results are preserved. Five synthetic calendar files accidentally persisted under the test binary's `data/backtest` directory were independently identified by their fake payload and exact SHA-256 hashes, then deleted. `synthetic-calendar-cleanup.json` records the exact files. Fixture-root assertions now fail before provider access if isolation changes.

This is an OHLC diagnostic, not proof of historical executable performance. Entry penetration, participation, adverse ordering, fixed costs and 10-bps slippage are explicit. Deadline opening capacity uses the preceding completed bar's volume; actual opening liquidity and historical short borrow are unproved. Current wishlist membership, retrospectively downloaded calendars and adjusted price history do not establish original historical receipt/version evidence. Trading remains paused; this run does not authorize paper/live promotion.

## Independent economic review finding — first attempt rejected

All five jobs completed, but the independent accountant found actual_fill_notional_limit_breach exits occurring before intrabar entries. The original opening price/timestamp had been reused after an entry recorded at the interval end. Published bytes are retained unchanged; their monthly returns are withdrawn pending a causal fix and rerun. Cleanup approval is withheld. The corrective implementation must preserve actual entries and defer a newly triggered intrabar market liquidation until the next observed bar opening; already-active structural stops retain the approved adverse same-bar ordering.

## Causal correction and repeat verification

Independent quant review also reproduced a short-sizing defect: the shared planner's adverse short estimate was below the sell limit, so RDW could route 263 shares although the known limit and frozen notional cap permitted only 262. Admission now caps the planner quantity at the known limit before routing, without resizing actual fills after observing future prices. PGY's corresponding cap is 186 instead of 187.

The bounded replay fix preserves original structural stops, defers a newly triggered intrabar market liquidation until the next observed opening, rejects backward per-parent fill clocks, and retains source interval, execution phase and sequence in existing fill evidence. Artifact verification rejects completed trades or exit fills before actual entry. Exact RDW/PGY source-bar fixtures and mirrored long cases assert quantity, entry/exit clock, price and stop reason. Two independent source reviewers accepted the corrected paths.

Final correction build succeeded with zero warnings/errors. `day-causal-fix-final.trx` records 238 passed, zero failed/skipped, covering intraday signal/replay/integration, continuation protection, artifact checks, native daily ledger regressions, monthly reporting, saved plans, prepared data and effective settings. `day-causal-fix.trx` retains an earlier launcher failure: testhost connection timed out before any test executed. The retry used local testhost communication outside the restricted launcher.

The corrected attempt reuses saved plan 17 with start key `volatile-day-breakout-20261025-causal-fix-start`; normal workflow evidence is in `outputs/previous-day-breakout-20261025-causal-fix`. Original results remain rejected and preserved. Final corrected economic review and exact generated-copy cleanup remain pending.

## Corrected actual-run audit

All five corrected jobs completed from 20:45:17.9309952 to 20:47:50.0666622 UTC on 25 October 2026. The source reviewer verified byte-identical reuse of all 26 query versions, with zero downloads. The accountant independently checked 53 artifact hashes, 24 source CSV hashes, 802 actual fills, 394 completed trades, 1,611 admission caps and all fees/session cash. No reversed exit, phase/sequence, admission-cap or accounting failure remained. Every one of the 26 originally reversed hypotheses matched a corrected causal trade. The corrected PGY quantity is 187 because the corrected book has different admission equity; the isolated historical fixture correctly retains 186 under its frozen original equity.

Nine monthly rows are supported (Group 3 April/May, Group 4 all seven slices); 26 rows remain unavailable due to verified source gaps. Group 2 also retains an unobservable DIOD pending-entry interval on July 21 at 13:55 UTC. Recorded fills finish within the day, but an unobserved pending order is not proof of a flat outcome. All limitations remain explicit; independent approval covers diagnostic accounting, not promotion or trading.

The corrected report and independent reviews are under `outputs/previous-day-breakout-20261025-causal-fix`. Long/short and entry-pattern dollar contributions reconcile within each supported month; they are not separate capital books or route returns. Web rendering returned HTTP 200 and displayed strategy, prior-session levels, entry pattern and target. The existing ngrok tunnel still points to the running Web app; trading remains paused.

## Reviewed cleanup completed

The independent source/quant reviewer freshly rehashed all ten original/corrected terminal workspaces and issued exact schema-2 cleanup approvals after the corrected accounting audit passed. The existing `backtest-cleanup-reviewed-inputs` command removed 5,984 owned generated candle/journal copies totaling 1,008,537,585 bytes. All ten published result hashes remained unchanged. Approval hashes, resolved paths and execution receipts are retained in the corrected output directory. Core/raw/prepared market data, news, wishlists, immutable settings and both successful and rejected results remain protected.

Post-cleanup independent verification found zero preservation failures: all 106 published artifacts and 406 referenced original/prepared core files retain hashes, as do 26 source-version manifests, both preparation snapshots, all ten results and ownership records. Only the ten approved generated spool roots are absent. The final check is retained in `outputs/previous-day-breakout-20261025-causal-fix/postcleanup-preservation-check.json`.
