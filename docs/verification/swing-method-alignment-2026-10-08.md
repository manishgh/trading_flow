# Swing method alignment and causal repairs — 8 October 2026

> Correction from deep review: four holding-cap exits used an earlier same-slice
> open after observing that slice's completed OHLC. Prior economic conclusions
> are superseded. Original artifacts are retained; see
> [the corrected deep assessment](swing-deep-analysis-2026-10-08.md).


This checkpoint implements the user's requested fixes and method review before a
five-stock-at-a-time backtest of the current Volatile group. It does not grant
paper/live authorization or claim historical membership/as-was-data certification.
The work is isolated on research/swing-pit-review-20261008; the running Web/ngrok
checkout, News/Earnings collectors and operational database are untouched.

## Defects repaired

- Active regime rules with missing benchmark history no longer become inactive.
  The runner reports missing/insufficient history; the calendar remains off.
- Decision evidence uses its actual timestamp, not the next observed stock bar's
  date. Benchmark versions must be completed and known by that timestamp.
- Entry-day regime/universe checks remain separate chronological admission checks.
- Setup expiry is one scheduled daily session, frozen from authoritative exchange
  calendar data. Missing calendar dates fail explicitly. Future stock gaps cannot
  extend it. CSV fixtures without a calendar use a declared fixed-duration horizon.
- CompletedBacktestDataProvider excludes incomplete intervals and versions whose
  KnownAtUtc exceeds the frozen data cutoff, including the inclusive API end row.
- Cancellation propagates through benchmark loading. Final available snapshots can
  produce decisions without requiring a future stock snapshot to exist.
- New method fields preserve their exact canonical hashes through immutable YAML
  serialization; a test exposed older omitted VCP/range fields, which were fixed.

All production regime decisions use the timestamp overload. The legacy DateOnly
convenience query is explicitly an end-of-UTC-date query and is not a substitute for
an intraday decision timestamp.

## Frozen research methods

Previous v4/v3 files are preserved; their catalog entries are archived. Two new
research versions use the versioned method-alignment-v2 admission profile, with no
execution grant:

- Minervini technical VCP v9: daily price above SMA50/150/200, SMA50>SMA150>SMA200,
  net SMA200 increase versus20bars earlier, price at least30% above the252-session
  low and within25% of its high; confirmed2–4 shrinking contractions, explicitly
  defined contraction/dry-up ratios, and breakout volume at least1.5times the
  previous50session arithmetic average, excluding the breakout. Structural stop
  below the final contraction; existing10R/ATR-trail/EMA20/90-bar exit overlays and
  SPY SMA200 overlay are disclosed. Market-wide IBD RS and fundamental selection
  are unavailable; this is not complete discretionary SEPA replication.
- Risk-managed Connors RSI2/SMA5 v5: completed close above SMA200 and RSI2 strictly
  below5; true arithmetic SMA5 exit with strict close>SMA5, executed on a later
  eligible open. ATR2 stop,2.5R target,five-bar cap and SPY SMA200 remain explicitly
  named overlays. It is not the unprotected no-stop baseline. Connors published
  both SMA5 and RSI70 exits; RSI70 is not inherently unauthentic.

References: [Connors SMA5 variant](https://tradingmarkets.com/recent/etf_trading_strategies_3x_etfs_and_the_200-day_ma-639612),
[Connors RSI70 variant](https://tradingmarkets.com/recent/how_to_enter_and_exit_a_position_on_the_close-677551),
[Minervini publisher description](https://www.mheducation.com/highered/mhp/product/trade-like-stock-market-wizard-how-achieve-super-performance-stocks-any-market.html).
Mechanical pattern thresholds and risk/exit overlays are frozen interpretations,
not optimization outcomes or an assertion of universally proven profitability.

## Study bounds and data

The frozen study is seven April through six October2026, start2026-04-07T04:00Z,
endexclusive2026-10-07T04:00Z. At capture23:57UTC on seven October, the latest fully
completed native NY daily interval was six October. Extended-hours trades can
change daily volume; the regular close alone does not finalize that contract.
[Alpaca aggregation rules](https://docs.alpaca.markets/us/docs/market-data-faq).

The snapshot is the actual25active Volatile stocks, split alphabetically into five
fixed batches. It is a retrospective current-universe diagnostic. No historical
membership is backdated, no list is created and no ticker is renamed/substituted.
The source database is accessed query-only; source membership hashes are recorded.

Real SIP/all-adjusted preflight has402unique completed SPY daily sessions and275
before the evaluation start, with no missing sessions in the captured range.
Most stocks have the same coverage. BXDC100sessions,FPS168 and SIVE0 cannot meet
260warmup sessions. CRCL336total/209prewindow and MIAX288/161 qualify later. The
study explicitly opts into AllowLateWarmup; default strict-start behavior remains.
The actual candidate loop still begins only after the full260prior bars, never
reducing the requirement. Late entry eligibility is disclosed in the coverage
receipt; cash remains uninvested before eligibility.

Each strategy and each five-stock chunk uses the unchanged$10,000/1%risk/25%cap/
fourslots fee assumptions and10bps slippage. Within-chunk unified portfolio results
are separate shared-capital results, not sums of strategy returns. Independent
chunks are not added into one whole-universe portfolio. News is disabled for this
technical comparison. No parameter search or holdout claim is made.

Current adjusted REST snapshots lack original revision/receipt history. Consequently
this study can verify computational information-time rules on its frozen inputs,
not absence of all universe-selection, survivorship, revision or execution-model
bias. Daily-bar execution is an approximation; real quote-side/intrabar execution
calibration remains separate. No positive result authorizes promotion.

## Verification and execution record

Focused component tests cover future-SPY and future-known-version perturbations,
calendar-based expiry, complete-input filtering, real late-warm-up/no-early-entry
admission, SMA5 arithmetic/equality, volume-baseline exclusion of current/future
bars, serialization hashes, risk/kernel/execution and cleanup consumers. Earlier
failing catalog assertions were updated to the intentional new versions; the
serialization omission was repaired rather than ignoring its failed hash test.
Final counts and the independent quant review are recorded below with the results.

Task outputs are under outputs/swing-study-2026-10-08. Frozen-study, coverage and
artifact/config receipts are retained as parameters/audit evidence. Temporary
preflight bars and run-owned raw/cache inputs are deleted after the study; published
results, trade/audit outputs, manifests and exact parameters are retained.


Pre-execution coverage handling is frozen in execution-protocol.json before any
returns are calculated. The original25-stock roster and chunks remain intact;
BXDC/FPS/SIVE are excluded only from executable study subsets. The five executions
contain4,4,5,5,4symbols. The whole-batch incomplete-economics guard is retained.

The actual driver replays the captured native SIP/all bars and full authoritative
calendar through a generic injected market-data source in the same BacktestRunner.
Source injection is propagated to the run-owned workspace; default operational
adapters are unchanged. All28captured file hashes are frozen before outcomes.
Actual runtime request/count/range/hash receipts will be retained. This avoids
claiming a second changing REST fetch is an identical frozen-input replay.


Independent quant review caught fallback versions in the first published audit.
That result is preserved and superseded for identity reporting. Validated sidecar
identity/profile now propagate through the owned workspace; canonical hashing
binds the full source definition including its alias. Missing identities, changed
definitions and unknown policies fail explicitly. Duplicate IDs fail before input
creation. Code review approved the fix. 55 focused tests passed; the new persisted
audit/mutation/cleanup regression passed with 29 affected tests. Identity-v2 reruns
change the run name only, retaining the original prices and economic parameters.


## Published results and independent verification

Window: 7 April through 6 October 2026. Independent $10,000 per strategy and
batch, 1% risk, 25% notional cap, four slots, 10bps slippage, existing fees.

| Batch | Eligible symbols | Connors net return | Trades | Realized drawdown | VCP trades |
| --- | --- | ---: | ---: | ---: | ---: |
| 1 | ASTS, BKSY, CRCL, CRDO | +1.6912% | 6 | 0.5656% | 0 |
| 2 | DASH, DIOD, MIAX, MRVL | +0.7553% | 7 | 0.9664% | 0 |
| 3 | MVST, MXL, NBIS, NVTS, OUST | +3.1237% | 11 | 0.4660% | 0 |
| 4 | PATH, PGY, POET, RDW, RGTI | +0.9513% | 8 | 0.4320% | 0 |
| 5 | RKLB, SMCI, SOXL, STM | -0.4400% | 4 | 0.8787% | 0 |

All 44 work items succeeded. VCP had zero candidates/trades in every batch; this
provides no performance evidence for that method. Connors had 36 completed trades.
Unified within-batch results equal Connors here because VCP generated no trades.
Do not sum or compound independent batch returns as one whole-universe portfolio.
Drawdown uses realized closed-trade equity, not intratrade mark-to-market equity.

BXDC (100 total bars), FPS (168) and SIVE (no returned bars) were excluded only
from execution coverage. CRCL qualified June19; MIAX August28, after full260-bar
warmup. The source roster and original five-stock boundaries remain unchanged.

Final affected shared kernel/risk/persistence/session/runner tests:85passed,0failed.
Earlier148/55/29 checks overlap; these are component checks, not a release suite.
The independent quant reviewer checked44 work items,64 referenced audit hashes,
all frozen source/request hashes,5284 decisions and all36 trades. Canonical
identity, completed-input timing, warmup, cutoff, scheduled expiry and ledger
economics pass. No independent second simulation was performed.

The review certifies computational timing on these captured inputs only. Current
roster selection and present-day adjusted REST prices cannot certify historical
membership or original revision timing. Native daily fill timestamps are slice
receipt times; execution is a disclosed next-bar approximation. No promotion or
paper/live activation follows.

Original identity-invalid batch1 remains published and marked superseded. Exact
identity-v2 configs, strategy sidecars, summaries, manifests, trade/candidate audits,
source hashes, coverage, reviewer receipt and replay-driver source are retained in
outputs/swing-study-2026-10-08. Temporary captured bars/calendars and unpublished
failed-launch spools are removed after this audit. A later fresh download may have
revisions and must use a new study identity; source hashes alone cannot recreate
the deleted inputs. Operational wishlist row hashes match before/after. Existing
Web/ngrok processes remain running and localhost News returns200.

The study did not write the operational database. Existing News/Earnings collectors
continued normal writes; the unchanged-source assertion concerns wishlist rows.
Post-cleanup all82 manifest entries match, including the superseded original run.
Requirements review found no remaining scope, cleanup or reporting blocker.
