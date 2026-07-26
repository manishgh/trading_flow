# Non-ML Strategy Research Implementation Status

**Updated:** 2026-07-26  
**Binding program:** `non-ml-strategy-research-program.md`  
**Evidence plan:** `momentum-and-catalyst-evidence-plan.md`

This document separates implemented controls from verified behavior and from
external evidence that TradingFlow does not yet possess. A missing dataset or
unvalidated assumption is a blocker, never an implied pass.

## Checkpoint Status

| Checkpoint | Status | Evidence |
|---|---|---|
| R0 research freeze | Verified | The two permitted families, partitions, stop conditions, and promotion boundaries are frozen in the binding program. |
| R1 registry and inference | Verified | Immutable SQLite trial registration, family trial counts, canonical results, block bootstrap, HAC inference, Holm adjustment, and selection-bias audit tests pass. |
| R2 swing benchmark | Verified | Fixed formation slots, cash for failed additive gates, point-in-time ranking, isolated VCP and catalyst additions, formation ledger, and canonical outputs are implemented. |
| R3 intraday event study | Verified | Point-in-time catalyst availability, cumulative same-minute RVOL, robust log-volume evidence, event clustering, market/sector returns, morphology timeline, censoring, and Holm-adjusted tests are implemented. |
| R4 holdout discipline | Verified | Development/validation readers cannot open outcome partitions crossing holdout. Frozen request mutation fails before observation reads. |
| R5 execution evidence | Verified | SIP/NBBO quote age, quote-side execution, partial/no-fill, participation, spread, fees, impact, and explicit calibration policies fail closed. |
| R6 point-in-time correction | Verified | Morphology no longer scans future bars; classifier observation time participates in availability; exact benchmark timestamps and short-return math are enforced. |
| R7 swing portfolio correction | Verified | Formation-equal weighting, absolute-P&L concentration, leave-one-out audits, and 1x/2x/3x cost stresses are implemented. |
| R8/R9 atomic holdout result | Verified | Holdout is acquired before reads and completed only by the same run after immutable result registration. A crashed run remains consumed. |
| R10 one-brain boundary | Verified | Generic sentiment research is explicitly named diagnostic and cannot promote. Evidence-grade Track B is the only promotable intraday analyzer. |
| Promotion gates | In progress | Swing and intraday evaluators must enforce every criterion and expose unavailable evidence as blockers. |

## Implemented Research Contracts

### Swing

- Point-in-time universe membership is separate from price data.
- Research-adjusted prices rank momentum; as-traded prices model liquidity and
  execution.
- Formation membership and configured slots are frozen before forward outcomes.
- A failed additive condition becomes cash; the portfolio never reranks or backfills
  from future winners.
- A1 momentum, A2 trend, A3 VCP, and A4 classified catalyst are isolated paired
  experiments.
- Development, validation, and holdout results remain separate.
- Ticker, month, and formation concentration use absolute P&L contribution.
- Leave-one-ticker, sector, year, and best-month audits preserve the frozen slot
  count.

### Intraday

- News availability is the latest of observed receipt, classifier completion, and
  classifier observation.
- Human ground-truth labels are classifier-validation evidence and are never used as
  historical trading-time predictions.
- Premarket, regular, postmarket, and overnight cohorts remain distinct.
- Response begins at the first completed bar after information availability.
- Morphology state at each horizon uses only bars completed by that horizon.
- Cumulative RVOL compares the current regular session through the same minute
  against prior valid regular sessions; the event day is excluded.
- Quote-side executable returns use ask-to-bid for longs and bid-to-ask for shorts.
- Missing auction, status, halt/LULD, corporate-action, sector, quote, or clustering
  evidence blocks promotion.

## Deliberately Diagnostic Paths

The following paths are useful for data diagnosis but are never promotion evidence:

- static present-day symbol lists projected backward;
- generic sentiment/keyword catalyst studies;
- provider publication timestamps without observed first receipt;
- REST-only news archives without complete revision history;
- fixed spread or fill assumptions not calibrated against paper observations.

Their APIs and outputs must contain `Diagnostic` in their names or an explicit
promotion blocker.

## External Evidence Blockers

### Swing history and universe

- The frozen program uses the complete provider-supported history beginning in
  2016. Alpaca documents US equity history from 2016:
  <https://docs.alpaca.markets/us/v1.1/docs/historical-stock-data-1>. Dataset
  manifests must preserve the exact observed coverage; pre-2016 or synthetic rows
  are prohibited.
- That provider boundary does not exclude later listings. Their point-in-time
  membership starts on the real effective date; the completed-history guard then
  admits them once the selected study's adjusted and as-traded warm-up is met.
- A survivorship-safe, point-in-time US equity membership source with delistings,
  symbol changes, issuer identity, sectors, and terminal outcomes is not yet
  available for the complete frozen interval.
- Benchmark total-return and corporate-action reconciliation must cover the same
  dates and identities as the universe.

### Intraday B0/B1

- At least 500 resolved human labels, 100 double labels, and 30 untouched examples
  for every promotable class are still required.
- Historical first-receipt timestamps and complete revision lineage are not proven
  for the existing REST news archive.
- Historical opening-auction, market-status, halt/resume, and LULD evidence is not
  complete.
- Global cross-provider story clusters and point-in-time sector benchmarks are not
  yet published as immutable catalog datasets.
- Matched no-catalyst controls require a separately frozen matching specification.
  No matching algorithm has been assumed.

### Execution calibration

- Historical SIP quotes are supported by Alpaca's stock quote endpoint:
  <https://docs.alpaca.markets/us/reference/stockquotes-1>.
- Full SIP coverage is distinct from IEX-only data:
  <https://docs.alpaca.markets/us/docs/about-market-data-api>.
- Promotion still requires calibration of quote latency, fill probability, partial
  fills, market impact, and auction behavior against paper observations.

## Current Decision

The integrated decision remains `RETAIN_RESEARCH`.

No swing or intraday configuration is eligible for paper promotion from the current
evidence. The code now fails closed for the known gaps and provides the machinery to
evaluate a frozen candidate once the required immutable evidence exists.
