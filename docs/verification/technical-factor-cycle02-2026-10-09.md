# Technical factor cycle 2 — 9 October 2026

The user authorised the six next hypotheses from the completed cycle-1 report.
This is a diagnostic iteration on the same viewed 7 April–6 October 2026 window,
original 25-symbol roster and five chunks. April and October remain partial.

## Frozen hypotheses

Eight recipes, 40 independent strategy/chunk runs, own $10,000 capital each:

- Exact RSI(2)<5 and EMA20/50 cycle-1 controls, including copied YAML, sidecars,
  canonical identity and admission profile.
- Four replacements of the RSI2 entry gate: signed `(close-SMA20)/ATR14 < -1`,
  three-session ROC `< -5%`, IBS `< 0.2`, and true ConnorsRSI(3,2,100) `< 10`.
  SMA200/SPY200 gates, stops, targets, SMA5/bar-cap exits, fees and risk are fixed.
  RSI2 ascending portfolio ranking remains; this tests admission, not ranking.
- EMA control plus prior Bollinger squeeze breakout: 20/2 bands, previous width
  ranked against 100 earlier widths with strict-less ties excluded; percentile
  at most 20; current completed close strictly above previous upper band.
- EMA control plus stock ROC20 minus SPY ROC20 strictly above 5 percentage points,
  with all 21 authoritative completed exchange sessions and availability aligned.

Each planned order has up to a 1% risk budget; this is not a portfolio drawdown cap. The 25% position
cap, four slots, fees, configured 10-basis-point friction, late-warmup exclusions,
OCO ownership and precommitted 6 October open liquidation remain fixed.

The fresh capture matches 27/28 original files and 25/26 canonical series; only
MRVL changed. All 40 use a new dataset identity and fresh control runs. Entire
batch 2 is unpaired against cycle 1 because capital/slots can transmit the change
to other stocks. Comparisons to old batches 1/3/4/5 require unchanged controls.

## Implementation boundaries

TechnicalFactorRules and TechnicalFactorCalculator are opt-in shared C# primitives.
New definition/signal/request fields stay null and omitted for old controls. No
global IndicatorSnapshot change, canonical-strategy edit, client computation,
operator source mutation, strategy promotion or broker activation.

New factors use derived profile 2.2.0 with pinned StockIndicators 2.7.1; controls
keep their original profile. True CRSI combines Wilder price RSI3, signed-streak
RSI2 and strict-lower rank of current one-day change versus 100 earlier changes.
Equal closes reset streak to zero. Pinned flat positive prices yield components
100/100/0 and composite 200/3. A conservative 260-bar floor is retained.

Benchmark context is shared across backtest, paper/live adapters and API preview.
The assembler selects the latest known completed version and rejects ambiguous
versions. Expected exchange-session dates establish freshness across weekends and
holidays. Required relative history is checked before regime/filter early exits;
missing/stale reference throws and marks the work item/cohort incomplete. Legal
zero-range IBS is unavailable/skipped rather than zero. Preview uses actual as-of
and completed setups rather than advancing to a forming candle's future close.

## Evidence and gates

Evidence root: `C:/Users/manis/Documents/Codex/2026-10-03/c/outputs/technical-cycle02-20261009`.
`trial-registry.json` is authoritative for eight recipes and 40 config hashes.
`frozen-study.json` is labelled parent window/roster context. The wishlist digest
is separate from the new market-data identity.

Before execution: focused formula/component, prefix-future, config/hash/default
wire, benchmark-version/session, regime-off required-data and ownership checks;
independent requirements and source/freeze reviews; a fault-injected missing-SPY
session must produce incomplete economics and delete its owned test artifacts.

All 40 runs completed, covering 176 successful strategy/ticker work items and
280 unique strategy/batch/month rows. Actual ledgers and 127 scheduled source-session
close valuations per run reconciled before export. The 56 unchanged-data control
strategy/batch/month rows each checked monthly return and reset close drawdown,
reproducing cycle 1 exactly. All of batch 2 remains unpaired against cycle 1.

Final affected C# tests: 106 passed, 0 failed, overlapping the earlier 63 checks.
This includes the added read-only API forming-bar cutoff regression. The executed
code receipt describes the actual replay binaries; the additional post-execution
change is a test only. Web and referenced consumers compiled. No Python typed
request/signal consumer was found; default wire omission and canonical identity
checks preserve the existing contracts. Full release, live/browser/Android,
subdaily execution stress, untouched holdout and paper-shadow checks were not run.

The fault-injected missing SPY session produced three failed work items and an
economically incomplete cohort; no valid 0% monthly cells were presented and all
owned verification files were deleted. A long verification path first failed
SQLite creation; its files were cleaned and a shorter owned temporary root was
used to execute the actual fault check. No strategy parameter was changed.

Final independent quant review passed within the declared diagnostic/model bounds:
376 manifest files, 336 runtime input/calendar receipts, 21,136 candidate identities,
47,784 transitions, 657 trades, 1,314 fills and 5,080 close marks verified. All 280
monthly return/reset-drawdown rows and factor observations independently recomputed.
Published rounded summaries differed from exact-ledger final cash by no more than
$0.000321; the normal $0.01 reconciliation tolerance was not weakened.

Owned cleanup is complete: 28 captured files removed; no owned input/cache/spool
directory remains. All 40 manifest sets, 376 published references, 40 summaries,
280 monthly rows, source/strategy/config/dataset identities and failure receipts
remain intact. Manifest hashes are unchanged. Read-only post-cleanup membership
check matches the original two wishlists/30 items. No recovery copy or operator
mutation. Independent-review, cleanup and membership receipts are in the evidence
root. Published configs, identities, audits, unsuccessful
outcomes and monthly reports are retained. Reports remain
monthly per exact strategy/version/hash and chunk, with actual fill-fee and
127-source-session close valuation reconciliation. All losing/null/failed trials
and immutable parameters remain. Owned captures, caches and unpublished spools
are deleted after review; the operational database is read-only.

This viewed window, current-roster selection, retrospective adjusted prices,
daily liquidity/protection approximations and multiple hypotheses constrain any
apparent improvement. No holdout, historical-revision or live-fill certification.
