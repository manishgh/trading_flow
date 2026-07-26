# Verified Research Results - 2026-07-26

## Decision Summary

No strategy is ready for paper promotion.

Keep two swing hypotheses for further point-in-time validation:

1. `momentum_only`
2. `momentum_stock_trend`

Reject:

- `momentum_stock_market_trend`;
- generic unclassified news as a catalyst;
- any result that treats the current Finviz universe as historical membership;
- any historical news result that assumes provider creation time proves first receipt.

## Swing Momentum

The adjusted six-year panel contains 120,073 daily rows for 75 current large-cap
stocks plus SPY. The primary 20-session `momentum_stock_trend` diagnostic reports:

| Metric | Result |
|---|---:|
| Formation months | 52 |
| Mean monthly net return | 2.580430% |
| Positive months | 63.4615% |
| Cumulative net return | 223.754113% |
| Annualized return | 31.142057% |
| Maximum drawdown | -18.794508% |
| Sharpe ratio | 1.103414 |

These values include frozen costs and next-open execution, but they are not a valid
claim of future performance. Development is weaker than later partitions and the
current-universe survivorship bias is material.

## Provider-Updated News

| Stage | Count |
|---|---:|
| Stored stories | 247,674 |
| Ticker-events | 290,932 |
| Independent 24-hour episodes | 36,156 |
| Raw horizon returns | 1,454,660 |
| Clean uncensored returns | 47,457 |

Generic news has negative market-adjusted expectancy in most partition/timing/horizon
cells after 14.2 basis points of round-trip cost. The few positive long-horizon cells
are inconsistent or too small. Provider categories do not create usable event classes
in this archive.

## Human Labeling Boundary

The deterministic 500-story package is stored at:

```text
data/research/labeling/ebd79d6c5e9f-6a6d9c653b14/
```

Work-item export SHA-256:

```text
6a6d9c653b14bf3c5a89c3e5b52cb4ac39d86bc1f4ebba99529f42dbb2c26190
```

The exporter:

- selects only the earliest stored provider revision per article;
- maximizes year/symbol coverage with deterministic round-robin strata;
- reproduces byte-identical output under input reordering and command replay;
- binds labels to exact revision-content hashes;
- refuses to overwrite non-matching human artifacts.

Completing 500 resolved labels, including 100 double-labeled articles and all event
categories, requires real human annotation. The system does not fabricate this
evidence.

## Next Evidence Step

After labels are complete:

1. import and verify the immutable classifier-ground-truth dataset;
2. measure event-type, direction, materiality, and timing cohorts before defining an
   entry;
3. retain only cohorts that are stable in development and validation;
4. evaluate untouched holdout once;
5. pass any surviving hypothesis through shared-engine portfolio simulation;
6. require an explicit human promotion decision before paper shadowing.
