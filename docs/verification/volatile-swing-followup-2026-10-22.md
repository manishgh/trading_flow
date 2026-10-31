# Volatile swing accounting and cohort checkpoint

Approved scope: [execution plan 09](../design-review/2026-10-19/09-volatile-swing-execution-plan-2026-10-21.md). Result bundle: `outputs/volatile-swing-followup-20261021`. This is a component and research checkpoint, not a product release or trading authorization.

## Delivered behavior

The existing saved-plan API and durable worker ran two registered strategies over five independent $10,000 cohorts. The plan excluded SOXL without changing the 25-member Volatile wishlist. No custom economic runner or hand-built run configuration was added. The ten approved economic attempts each ran once; subsequent reporting fixes did not trigger another run.

The shared runner permits a stock to acquire its required 260 completed bars during the window. It retains admitted open holdings, partial fills/exits, protective orders and pending exits at the end instead of inventing a sale. The source-session ledger reconciles actual cash, fees, remaining quantity, realized profit and marked open value. The monthly publisher and artifact checks use those same records. API, Web and Android receive the server-owned monthly and terminal state.

The study remains April 21, 2026 04:00 UTC to October 21 04:00 UTC, end exclusive; New York source sessions end October 20. April and October are partial. All 54 prepared source versions were reused and independently matched to the original audited versions. No economic interpretation treats blank historical receipt clocks as proof of when the data was originally known.

## Verification performed

| Check | Result | Retained evidence |
|---|---|---|
| Focused engine, source retention, artifact, API and workflow checks | 266 passed; no failures or skips | `.tmp/verification-20261021-followup/focused-final-nonmatrix.trx` and log |
| Existing saved-plan matrix | Seven passed, including 20-, 12- and six-book matrices, native/SIP and restart/cancellation cases; 13 min 35 s | `matrix-final.trx` and log in the same directory |
| Existing browser workflow scenarios | Six passed in 16.2 s, isolated storage removed after host shutdown | `web-final.log` |
| Android Release build after shared-state and final label changes | Passed; zero warnings/errors | `android-final-wording-build.log` |
| Actual-entry diagnostic repair | Seven passed, no failures/skips; includes six new cases and the existing receipt-boundary regression | `missed-entry-final.trx` and log |
| Final Web build after diagnostic/assessment/month-filter changes | Passed; zero warnings/errors | `web-month-submit-build.log` |
| Missing assessment inputs and bounded UI projection | 15 passed; no failures/skips | `promotion-display-final.trx` and log |
| Independent quant replay | All 70 monthly rows reviewed: 56 reconcile, 14 withheld for SIVE; exact ten durable attempts verified read-only | `independent-quant-review.md`, `quant-result-accounting-audit-v2.json`, `quant-retention-api-audit-v2.json` |
| Post-cleanup API reopening | All ten jobs remain completed; all 70 monthly rows and terminal states unchanged | `post-cleanup-api-check.json` |

Test sets overlap and are not added into one total. Earlier failed/interrupted verification attempts remain recorded as such, not counted as passes. Heavy builds/tests/economic work ran serially. The largest existing matrix case took about nine minutes; it was allowed to finish. No full release-suite, device installation, quote-level execution calibration or live/paper order check is claimed.

The original economic assemblies and shared-profile hash remain in `build-evidence.json`. Later diagnostic/display builds are recorded separately; they are not substituted for the binaries that produced the retained economic results. The Git checkout has existing uncommitted work; no clean-commit reproduction claim, GitHub synchronization or reset was made.

## Independent findings resolved

Engineering review found and corrected pending-exit receipt/cutoff handling, per-fill artifact matching, long/short pending VWAP exits, and double subtraction in rejection counts. Open/closed counts, missing-timeframe errors and monthly source-session boundaries were independently reviewed. See `engineering-review.md`.

The real-run review found MRVL's October 16 entry was present in fills but absent from the old closed-trade-only missed-move summary. The helper now uses positive actual entry fills when native portfolio state exists. It excludes unfilled admissions, rejected hypotheses, exits and zero quantities, while preserving older completed-trade fallback. Independent source review found no actionable defect; seven tests passed. The original published diagnostic is explicitly disavowed in the quant report and preserved without rewriting its artifact.

The live Web check additionally found that a trimmed display snapshot was described as having no completed trades and no recorded configuration in its promotion panel. The repaired assessment now says those inputs were not supplied, and incomplete trade details remain unevaluated. Native closed counts distinguish them from actual zero trades and open admissions. Fifteen focused gate/projection tests passed; independent source review found no actionable defect. It neither loads mutable configuration nor turns an incomplete sample into a pass.

The month selector also changed its selected label without applying the selected month: its automatic-submit handler existed only in other pages' scripts. An ordinary Show month button now submits the existing GET form. Independent review and final Web compilation passed. CUA verified October submission changes the URL, partial-month label and actual displayed row together: Group 2 Connors shows +0.45%, 10,075.49 opening equity and 10,121.07 closing equity, with DIOD 15 / MRVL 3 open at cutoff. The prior September row was -0.32%. This was a form repair, not a change to monthly calculations.

## Provider collection and what it establishes

Both missing news ranges were collected by the existing `evidence-collect-normalize` CLI against the existing evidence catalog/object root. One worker, bounded retries and normal pagination were used. The requests are ordinary collector inputs, not new strategy configurations.

- The 21-symbol extension, October 7 04:00 UTC to October 21 04:00 UTC, returned 204 articles over five HTTP 200 pages: 152 with body text and 52 headline-only.
- BXDC/FPS/SIVE, April 21 04:00 UTC to October 21 04:00 UTC, returned 67 articles over two HTTP 200 pages: 25 with body text and 42 headline-only. Their individual mention counts were 16, 49 and two.
- Both final cursors were exhausted. All raw and normalized object hashes/lengths match. No quarantine or publication recovery failure occurred. Exact query scope, timestamps and receipt references are in `news-collection-audit.json`.

These company queries do not prove complete sector/global coverage, historical revisions or original historical receipt/classification times. The technical-plus-news comparison did not run: the normal catalog has only the two technical definitions, and its saved-plan contract does not yet bind the existing frozen decision context. The existing 25-story category pilot remains unlabelled and is not an adverse-event accuracy evaluation. The [next proposal](../research/volatile-next-experiment-design-2026-10-22.md) identifies the smallest reusable integration and exact evidence requirements; it neither invents labels nor backdates downloads.

SIVE's configured SIP daily/hourly queries returned HTTP 200, empty bars and a final null page token for March 17, 2025 04:00 UTC through October 21, 2026 04:00 UTC. Current Alpaca asset metadata identifies an inactive OTC instrument. That does not prove the reason for absent historical bars. Both selected SIVE portfolios remain unavailable; no stock was substituted or removed from the wishlist. See `symbol-history-check.md` and preserved provider receipts.

## Cleanup and runtime

The independent quant issued ten saved approvals bound to exact terminal workspace/result hashes. The normal `backtest-cleanup-reviewed-inputs --review <review.json>` component then removed only those ten owned input spools: 35,012 generated files, 88,288,624 bytes. `cleanup-receipts.json` records each operation. All published result hashes and subsequent API monthly/position records are unchanged. Original/raw/prepared history, news, lists, immutable settings and final evidence remain.

Proven abandoned synthetic test directories were removed separately after their test hosts stopped. Unknown database siblings were not deleted. The temporary encrypted authentication session was removed after final verification. Final verification logs and failed-attempt evidence remain part of the result.

Web is restored through the existing ngrok tunnel with trading and feeds disabled, as requested. `owned-web-process-verified.json` is the current owned process record; earlier process records are historical and those processes were stopped. The final Web DLL hashes are in `postrun-build-evidence-verified.json`; `build-evidence.json` still identifies the original economic runs. Android's final Release build remains applicable: the later reporting repairs affect Domain/Web, while Mobile references the unchanged standalone Contracts project.

The user-facing tab remains on the authenticated October Group 2 result through the URL in `web-url.json`. Browser inspection confirmed monthly labels, open positions, pending exits, history availability and honest unevaluated assessment reasons. No phone interaction is claimed. The report and the next proposal are ready; new strategy registration and news-dependent economics remain unimplemented pending design review and the stated evidence requirements.
