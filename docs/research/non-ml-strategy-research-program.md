# Non-ML Strategy Research Program

**Status:** Binding research plan
**Created:** 2026-07-26
**Scope:** US equities; one swing track and one intraday track

## 1. Decision

TradingFlow will stop creating broad strategy variants. Research is limited to two
ordered tracks:

1. **Swing:** establish a cross-sectional momentum benchmark, then test stock trend,
   VCP, and classified catalysts as isolated additions.
2. **Intraday:** establish whether a point-in-time classified catalyst followed by
   abnormal participation predicts an executable move, then study opening response
   and pullback/reclaim structure before defining an entry.

No strategy config may be promoted from a chart review, a single ticker, a parameter
search, or a generic-news result. Research code and configs remain outside the
promoted strategy catalog until the promotion gate passes.

This program is non-ML. Deterministic classification, ranking, event studies, and
explicit mathematical rules are in scope. Market Predictor outputs are not research
inputs for these experiments.

## 2. Current Evidence

The current repository already provides:

- immutable evidence manifests and object storage;
- adjusted and as-traded daily bars;
- exchange-session evidence;
- provider-updated historical Alpaca news;
- chronological development, validation, and holdout partitions;
- a cross-sectional momentum analyzer and audit report;
- a catalyst event-study runner;
- shared next-bar execution, costs, slippage, spread, and risk planning;
- a promotion registry that fails closed.

The current six-year momentum diagnostic is not promotable because its 75-stock
universe is a current Finviz snapshot projected backward. It has 52 monthly formation
dates, not the required 60, and its development period is weaker than later periods.
Its reported holdout has already been inspected and is therefore consumed. The next
point-in-time study must create a new experiment family with a new, unopened holdout.

The existing momentum cells are also not valid additive comparisons. Each cell
filters the eligible set, reranks the survivors, and selects a new top fraction.
Trend, VCP, or catalyst experiments must instead rank once and keep the same
portfolio slots. A failed additive gate leaves that slot in the frozen cash or
risk-free baseline; it must not be replaced by the next-ranked stock.

Generic provider news is rejected as an alpha source. It produced negative
market-adjusted expectancy in most timing and horizon cells after 14.2 basis points
of round-trip cost. Provider categories are not sufficiently specific.

Historical news must use provider update time as the conservative availability time.
Provider creation time is metadata only because a later revision downloaded through
REST cannot safely be assigned to the original creation time. Observed receipt time
may be used only for live-captured news that actually recorded it.

## 3. Research Governance

### 3.1 Frozen trial registry

Before a run, persist:

- experiment ID and parent experiment ID;
- economic hypothesis;
- exact universe and membership dataset;
- exact evidence dataset IDs;
- formation/event clock;
- formulas and thresholds;
- cost and execution assumptions;
- development, validation, and holdout boundaries;
- primary metric and rejection criteria;
- every prior trial in the same research family.

Changing any item creates a new experiment ID. Results never overwrite an earlier
trial. The trial count feeds the Deflated Sharpe Ratio or an equivalent
multiple-testing adjustment.

### 3.2 Partition rules

- Partitions are chronological, never random.
- Features and ranks use only data available at the decision timestamp.
- Outcomes crossing a partition boundary are censored.
- Partition boundaries include an embargo at least as long as the longest measured
  outcome plus any catalyst quiet period.
- The holdout is evaluated once after development and validation are frozen.
- Holdout data is not loaded by development or validation workflows.
- A failed holdout retires that hypothesis family. It is not retuned on the holdout.
- Overlapping outcomes are not treated as independent observations.

### 3.3 Independent observations

- Swing: the primary independent unit is a formation date, not each selected stock.
- Intraday: the primary independent unit is a canonical story episode and entry
  session. Revisions, duplicate publishers, and multi-symbol tags are not independent
  stories.
- Inference is clustered by formation date for swing and by story plus session date
  for intraday.

### 3.4 Common reporting

Every experiment reports:

- gross and net return;
- SPY and equal-weight-universe return over identical timestamps;
- market- and, when available, sector-excess return;
- Sharpe and downside deviation;
- maximum drawdown and time under water;
- turnover and total modeled costs;
- win rate, payoff ratio, expectancy, MFE, and MAE;
- ticker, sector, month, and event concentration;
- development, validation, and untouched holdout results separately;
- exclusions, censored outcomes, missing data, and effective sample size;
- total trial count and selection-bias adjustment.

Uncertainty must be estimated from the independent research unit, not from every
stock outcome as if observations were independent. Swing uses formation-date
clustering, HAC/Newey-West statistics for overlapping horizons, and a block bootstrap.
Intraday uses canonical-story/session clustering and a session-date block bootstrap.

## 4. Track A - Swing Cross-Sectional Momentum

### A0. Data admission

Required before promotable research:

- raw inputs beginning no later than 1999-01-01;
- authoritative point-in-time S&P 500 membership with effective timestamps;
- one common-share line per issuer;
- a formation-date price floor of USD 5 and raw 20-session average dollar volume
  of at least USD 20 million;
- at least 252 completed sessions per admitted security;
- adjusted and as-traded daily bars for every historical member;
- delisted securities and historical membership changes;
- corporate-action and terminal/delisting evidence;
- SPY, point-in-time sector benchmarks, risk-free return, and frozen FF5 plus UMD
  factor evidence;
- point-in-time liquidity filters;
- at least 60 development, 24 validation, and 24 prospective holdout monthly
  formation dates.

The existing static 75-stock dataset remains diagnostic and cannot satisfy A0.
If historical membership cannot be sourced, collect prospective snapshots and keep
the promotion gate closed rather than infer historical membership.

The frozen time design is:

- development: 2000-01-01 through 2014-12-31;
- validation: 2015-01-01 through 2019-12-31;
- contaminated diagnostic only: 2020-01-01 through 2026-07-31;
- untouched prospective holdout: 2026-08-01 through 2028-07-31.

The diagnostic period may expose implementation and regime defects, but it cannot
promote or rescue a hypothesis.

### A1. Frozen benchmark

At each completed month-end regular-session bar:

```text
momentum_12_1 = adjusted_close[t - 21] / adjusted_close[t - 252] - 1
```

Rules:

- rank the point-in-time eligible universe;
- select the top 30 percent and retain all fixed portfolio slots;
- equal-weight initially;
- enter at the next regular-session open;
- hold for 20 completed sessions or until the next scheduled rebalance;
- no VCP, catalyst, RSI, MACD, or discretionary exit;
- apply frozen next-open spread, slippage, fees, and turnover costs.

Compare against:

- SPY;
- turnover-aware equal-weight eligible universe;
- bottom 30 percent;
- sector-neutral top-minus-bottom momentum spread;
- momentum deciles as a diagnostic of rank monotonicity.

Alternative selection fractions and 5- or 60-session outcomes are diagnostics only.
They cannot replace the primary 30-percent, 20-session result.

### A2. Stock-trend addition

Test exactly one paired addition to A1:

```text
close > SMA200
AND SMA50 > SMA200
```

The comparison uses the same formation dates and candidate universe. Retain the
trend filter only if it improves validation net risk-adjusted return or materially
reduces drawdown without destroying rank monotonicity or breadth.

Rank A1 once. When a selected stock fails the trend condition, its slot earns the
frozen risk-free return. Do not rerank and do not replace it with another stock.

The previously rejected stock-plus-market-trend cell remains retired. It is not
reintroduced unless a new preregistered economic hypothesis and new evidence justify
another trial.

### A3. VCP addition

VCP is a conditional entry-timing hypothesis, not the stock-selection benchmark.
Run it only after A1 or A2 passes validation.

Freeze the existing V7 definition so this stage does not become another parameter
search:

- 60 completed daily bars;
- pivot strength 2;
- 2-4 contractions;
- maximum contraction depth ratio 0.90;
- contraction-to-advance volume ratio at most 0.85;
- progressive-volume requirement disabled;
- three reference bars;
- breakout-volume ratio at least 1.0;
- pivot known before entry and next-bar breakout execution.

Compare the selected momentum portfolio with and without VCP on the same candidates.
Report opportunity loss as well as trade quality. Reject VCP when fewer trades merely
make the result look cleaner without improving net portfolio expectancy.

As in A2, a failed VCP gate leaves the original slot in the frozen baseline. It does
not trigger reranking or replacement.

### A4. Classified-catalyst addition

Do not use generic sentiment. Test only event categories that independently pass the
news event study, such as earnings/guidance, FDA/regulatory decisions, material
contracts, offerings/dilution, or M&A.

For the first long-only test, require a clear positive, medium- or high-materiality,
non-promotional event available during the preceding five XNYS sessions and before
the entry open. The catalyst may alter rank or timing only after event type,
direction, materiality, and availability clock are frozen. A catalyst revision
cannot be moved back to the original story creation time.

Cluster revisions and republications by provider plus provider article ID. The first
qualifying revision starts one 24-hour issuer episode. Multiple stories in that
episode produce one binary catalyst gate per issuer and formation date.

Before this stage can influence a strategy, the frozen classifier must pass:

- composite qualifier precision at least 0.80 with a 95% lower bound of 0.70;
- recall at least 0.60;
- category macro-F1 at least 0.70;
- direction macro-F1 at least 0.75;
- materiality weighted kappa at least 0.60;
- at least 500 resolved labels, 100 double labels, and 30 untouched examples for
  every promotable class.

### A5. Shared-engine portfolio simulation

Only the surviving A1-A4 cell enters the shared backtest engine:

- next-bar execution;
- one capital pool;
- volatility-based sizing;
- position and sector limits;
- overlapping holdings;
- spread, slippage, fees, and participation;
- no parameter optimization;
- complete audit retention.

### Swing promotion gate

All conditions are required:

- at least 60 development, 24 validation, and 24 untouched prospective holdout
  formation dates;
- at least 50 distinct selected securities in the prospective holdout;
- positive net excess return in development, validation, and holdout;
- monotonic or economically coherent momentum quantiles;
- positive incremental return for every accepted addition in development,
  validation, and holdout;
- validation 90% block-bootstrap lower bound above zero;
- holdout 95% block-bootstrap lower bound above zero;
- positive-formation rate above 50%;
- no single ticker contributes more than 10% of absolute profit and loss;
- no single formation month contributes more than 10% of absolute profit and loss;
- maximum drawdown no worse than 25% and no worse than 1.25 times the
  equal-weight benchmark drawdown;
- result remains positive in 1x, 2x, and 3x total-cost stress tests;
- leave-one-ticker, leave-one-sector, leave-one-year, and leave-best-month results do
  not reverse the conclusion;
- multiple-testing-adjusted evidence remains acceptable;
- explicit human promotion approval.

A3 or A4 also requires at least 100 development, 50 validation, and 30 prospective
independent trades across at least 20 issuers. A non-positive validation estimate
rejects an addition. A positive estimate whose interval spans zero remains
inconclusive and research-only.

## 5. Track B - Intraday Catalyst and Participation

### B0. Data admission

Required:

- SIP one-minute bars;
- SIP quotes or NBBO around each event;
- opening-auction evidence when available;
- exchange-session evidence including early closes;
- market-status, halt/resume, and LULD state;
- point-in-time classified news;
- provider creation time, update time, and actual receipt time when observed;
- classifier-completion time;
- canonical story/revision identity;
- point-in-time sector membership and a sector benchmark;
- at least 63 comparable prior valid regular sessions for cumulative time-of-day
  volume, with 40 as the minimum admissible sample;
- split and symbol-change handling;
- regular, premarket, postmarket, and overnight cohorts kept separate.

Historical REST news without a proven first-receipt time is diagnostic only.
The repository does not yet contain a production-grade intraday event dataset, so
no intraday entry experiment may start until B0 is published immutably.

### B1. Classifier and timestamp validation

Before measuring price response:

- finish the 500-story ground-truth package;
- resolve at least 100 double labels with adjudication provenance;
- validate the classifier against the A4 precision, recall, F1, and kappa gates;
- store every raw revision and its content hash;
- define decision availability as the later of actual news receipt and classifier
  completion;
- never use a final label, later revision, or provider creation timestamp as an
  earlier feature.

If classifier quality fails, category-specific trading research stops. The system
may continue collecting labeled evidence.

### B2. Catalyst response event study

This stage has no entry rule.

For each independent story episode:

1. Map availability to the exact exchange session.
2. Use only candles ending at or before availability for pre-event state.
3. Start response measurement at the first completed post-event bar.
4. Map premarket events to the opening auction/first regular completed bar and
   postmarket events to the next regular session; never discard them merely because
   the provider date differs from the executable session.
5. Measure 1, 5, 15, 30, and 60 trading-minute returns.
6. Report raw, SPY-excess, and sector-excess returns.
7. Report executable ask-to-bid long returns and bid-to-ask short returns.
8. Censor a horizon crossed by a second independent material event.
9. Compare with matched no-catalyst controls.

Do not combine event categories or directions to manufacture sample size.
Deduplicate stories globally across tickers and publishers, then cluster inference
by canonical story and session date. Apply Holm correction to the frozen family of
category, direction, horizon, and cohort tests.

### B3. Participation study

Volume is explanatory evidence, not initially a hard gate.

Measure:

```text
cumulative_rvol =
    current_regular_session_volume_from_09:30_to_t
    / median prior-valid-session volume_from_09:30_to_t
```

Also retain:

- bar-volume ratio against comparable historical bars;
- robust log-volume z-score using the historical median and MAD;
- premarket dollar volume;
- spread in basis points;
- gap versus adjusted prior regular close;
- initial market-adjusted response;
- price and volume acceleration.

Study outcomes by frozen bins rather than optimizing a threshold. The purpose is to
learn whether participation separates continuation from reversal and whether the
effect survives executable costs.

The event day is excluded from its own baseline. Use 63 prior valid sessions when
available and fail the observation when fewer than 40 exist. Premarket volume is a
separate feature and cohort; it is never mixed into the 09:30 regular-session
cumulative baseline.

The current event-study `IsVolumeExpanded` shortcut is not admissible for this stage:
passing because one RVOL value reaches 1.0 or one bar exceeds the previous bar is not
equivalent to cumulative same-minute participation against 40-63 prior sessions.

### B4. Opening-response and structure study

Condition only on B2 event categories and B3 participation cohorts that survive
validation. Evaluate these mutually exclusive, point-in-time states:

- immediate continuation;
- opening-range break and hold;
- pullback to VWAP or the event impulse;
- pullback/reclaim confirmed by a completed bar;
- failed break/reversal.

At every candidate bar, persist the exact state known then. Measure forward MFE, MAE,
continuation probability, time to failure, and executable return. Do not select the
best-looking entry rule yet.

### B5. One preregistered entry rule

Define one rule only after B4 identifies a stable structure. It must specify:

- eligible event types and direction;
- session cohort;
- minimum liquidity and maximum spread;
- participation cohort;
- completed-bar setup and trigger;
- next-bar order type;
- structural invalidation and maximum hold;
- one fixed account-risk budget;
- forced session exit.

Run development and validation once. Freeze it before opening holdout.

### B6. Shared-engine and paper shadow

The surviving rule uses the same decision, risk, order-planning, cost, and audit
contracts as paper execution. Paper shadowing requires at least 30 sessions with no
rule or parameter changes.

Simulation must model quote-side fills, non-fills, partial fills, queue/latency
assumptions, auction behavior, halts/LULD, borrow availability for shorts, and
size-dependent impact. Calibrate these assumptions against paper observations
instead of allowing a generic capped-slippage shortcut to declare success.

### Intraday promotion gate

All conditions are required:

- at least 300 independent catalyst episodes overall;
- at least 100 independent episodes in the proposed event cohort;
- at least 100 executable simulated trades and 30 holdout trades;
- positive net expectancy in development, validation, and holdout;
- expected gross edge at least twice frozen round-trip cost;
- profit factor at least 1.20 after costs;
- no ticker contributes more than 10% of absolute profit and loss;
- no trading day contributes more than 10% of absolute profit and loss;
- the lower 95% session-block-bootstrap confidence bound on net expectancy is
  positive;
- 1x, 2x, and 3x cost stresses and leave-one-ticker/day/event-subtype checks do not
  reverse the conclusion;
- no dependence on a single opening minute or one event category subtype;
- explicit human promotion approval.

## 6. Agent Review and Decision Process

Independent reviews are advisory. One integrator owns the final experiment registry
and prevents reviewers from silently changing each other's assumptions.

Required reviews:

1. **Universe reviewer:** membership, identity, survivorship, delistings, actions.
2. **Signal reviewer:** formulas, bar completion, event availability, session mapping.
3. **Execution reviewer:** spread, quote side, slippage, participation, fills, costs.
4. **Statistics reviewer:** dependence, censoring, partitions, trial count, robustness.

Each reviewer returns:

- evidence inspected;
- pass/fail findings;
- unresolved blockers;
- exact proposed change;
- whether the change creates a new experiment ID.

The integrator reconciles contradictions and publishes one signed decision:
`REJECT`, `RETAIN_RESEARCH`, `ADVANCE_TO_HOLDOUT`, or `PROMOTE_TO_PAPER_SHADOW`.

### Current integrated decision

| Review | Decision | Evidence-based reason |
|---|---|---|
| Universe | Fail | Current 2026 Finviz membership was projected backward; delistings and historical membership are unresolved. |
| Signal mathematics | Fail | Existing trend cells filter and rerank instead of measuring a fixed-slot incremental treatment. |
| Execution | Conditional | Completed-bar and next-bar controls exist, but catalyst research still needs quote-side non-fill, partial-fill, auction, and impact calibration. |
| Statistics | Fail | Current outputs lack clustered confidence intervals, robust overlapping-horizon inference, and implemented multiple-testing adjustment. |
| Swing hypothesis | Retain research | Cross-sectional momentum has an economic and empirical basis, but the local dataset cannot promote it. |
| Intraday hypothesis | Retain research | Catalyst response and participation are testable, but no production-grade point-in-time intraday event dataset exists yet. |

The integrator decision is `RETAIN_RESEARCH`. No current swing or intraday strategy
advances to a new holdout or paper shadow from this evidence.

## 7. Implementation Sequence

1. Add a durable research hypothesis/trial registry and multiple-testing metadata.
2. Add clustered/HAC inference, block bootstrap, and multiple-testing adjustment.
3. Establish one canonical result manifest; retire loose, conflicting result exports.
4. Fix news revision availability and global cross-request/cross-ticker story
   reconciliation.
5. Add or acquire point-in-time historical universe membership.
6. Package the full momentum audit, formation ledger, portfolio returns, robustness,
   and factor results as immutable named outputs.
7. Extend swing evidence to at least ten years and run A1 only.
8. Audit A1; run A2 only if A1 is valid.
9. Run A3 and A4 as separate paired additions, never together initially.
10. Complete classifier ground truth and B1.
11. Add SIP bars, quote, auction, market-status, and sector evidence; build B2.
12. Build B3 cumulative-participation cohorts.
13. Build B4 point-in-time structure observations.
14. Define B5 only after the event-study evidence is stable.
15. Calibrate partial-fill, non-fill, quote-side, and impact models from paper data.
16. Run shared-engine simulations and one untouched holdout.
17. Paper-shadow only a promoted frozen strategy.

## 8. Stop Conditions

Stop a research family when:

- point-in-time evidence cannot be obtained;
- availability timing cannot be defended;
- validation expectancy is non-positive after costs;
- the effect disappears under doubled costs;
- effective sample size is below the stated minimum;
- returns are dominated by one ticker, month, or event;
- holdout fails;
- another parameter change would be justified only by observed results.

Stopping is a successful research outcome. It prevents an unsupported hypothesis from
reaching paper or live trading.

## 9. Evidence Basis

- Jegadeesh and Titman document cross-sectional momentum over prior 3-12 month
  returns: https://academic.oup.com/rfs/article-abstract/15/1/143/1619967
- Hurst, Ooi, and Pedersen review long-run trend-following evidence:
  https://www.aqr.com/Insights/Research/Journal-Article/A-Century-of-Evidence-on-Trend-Following-Investing
- Chan, Jegadeesh, and Lakonishok link price momentum and earnings underreaction:
  https://www.nber.org/papers/w5375
- Daniel and Moskowitz document momentum crash risk:
  https://www.nber.org/papers/w20439
- Chan distinguishes post-news drift from no-news reversal:
  https://doi.org/10.1016/S0304-405X(03)00146-6
- Pervasive underreaction research supports measuring initial news response and later
  drift rather than assuming a generic headline signal:
  https://doi.org/10.1016/j.jfineco.2021.04.003
- Bailey and Lopez de Prado define the Deflated Sharpe Ratio for selection bias and
  non-normal returns: https://ssrn.com/abstract=2460551
