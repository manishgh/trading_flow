# Non-ML Swing Strategy Research Program

**Status:** binding research protocol

**Scope:** US-listed equities, swing horizon only

**Decision authority:** immutable evidence plus explicit human promotion

## Objective

Measure one economically defensible baseline before testing additions:

1. Cross-sectional stock momentum over completed daily bars.
2. Optional daily trend, volatility contraction, or point-in-time classified
   catalyst overlays, each tested one at a time against the frozen baseline.

Sub-daily bars may confirm or time a swing entry. They are execution evidence, not a
separate strategy track. Generic indicator stacking and same-session trading are out
of scope.

## Research Invariants

- Use point-in-time universe membership, security identity, listings, delistings,
  corporate actions, and classifications.
- Begin no earlier than 2016 and never synthesize pre-listing history.
- Compute signals only from completed bars available at the decision timestamp.
- Fill no earlier than the next executable bar.
- Derive the initial stop only from evidence completed before the fill.
- Use provider first-seen timestamps for catalyst availability; revisions cannot move
  the original availability time backward.
- Apply spread, slippage, fees, participation, non-fill, and impact assumptions.
- Keep development, validation, and final chronological holdout disjoint.
- Use the final holdout once for a frozen hypothesis.
- Compare against SPY and an equal-weight eligible-universe benchmark.
- Reject evidence dominated by one issuer, sector, month, or market regime.

## Baseline

At each scheduled formation date:

1. Resolve the eligible point-in-time universe.
2. Calculate frozen momentum features from adjusted completed daily bars.
3. Apply declared liquidity, price, history, and market-regime eligibility.
4. Rank the cross-section without using future membership or outcomes.
5. Construct the portfolio with volatility-based sizing, issuer and sector caps,
   account-risk limits, and deterministic tie-breaking.
6. Rebalance on the declared weekly or monthly schedule.
7. Exit when relative strength or trend deteriorates under the frozen rule.

The baseline must stand on its own. VCP, catalysts, and sub-daily confirmation are
paired ablations, not bundled prerequisites.

## Catalyst Overlay

A catalyst observation records provider article ID, canonical story identity,
provider-created and updated timestamps, first local receipt, affected symbols,
classification components, dedup evidence, and model/rule provenance.

The study measures forward swing-horizon returns from the first executable bar after
availability. A catalyst may qualify a swing candidate only if its exact
classification version passed the held-out labeling gate. Generic positive sentiment
is diagnostic and cannot promote.

## Statistical Gates

A promotable result must have:

- at least 60 independent portfolio formation periods;
- at least 12 untouched holdout formations;
- at least 30 distinct selected issuers in holdout;
- positive net expectancy after modeled costs;
- acceptable maximum drawdown under the declared account-risk policy;
- stable direction across development, validation, and holdout;
- SPY and equal-weight benchmark comparisons;
- issuer, sector, month, and regime concentration reports;
- sensitivity checks showing the result is not dependent on one narrow parameter;
- immutable dataset, code, config, and result hashes.

Missing evidence is a failure, not a pass.

## Experiment Sequence

1. Freeze universe, partitions, costs, portfolio rules, and baseline parameters.
2. Run and publish the momentum baseline.
3. Test daily trend as one paired change.
4. Test VCP as one paired change.
5. Test classified catalyst as one paired change.
6. Test completed `1h` or `15m` confirmation only if it improves execution after
   costs without reducing sample independence below the promotion floor.
7. Select no more than one candidate for paper shadow.
8. Freeze it during the entire paper-validation window.

## Promotion Firewall

Research output cannot authorize paper or live execution. Promotion requires an
explicit decision bound to strategy ID, semantic version, content hash, dataset and
universe evidence hashes, development/validation/holdout results, cost and
concentration reports, and human approver. Directory placement and UI selection are
not authority.
