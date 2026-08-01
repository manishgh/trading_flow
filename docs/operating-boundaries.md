# TradingFlow Operating Boundaries

**Status:** Binding system boundary

**Updated:** 2026-07-26

TradingFlow is an API-first US-equities research, backtesting, and paper-trading
system. These boundaries prevent research assumptions from leaking into execution
and prevent local backtests from being presented as validated trading edge.

## Product Boundary

TradingFlow supports:

- deterministic non-ML research;
- historical backtesting;
- paper trading through Alpaca;
- operator monitoring, audit, and reviewed order workflows.

Live routing remains disabled until a frozen strategy passes its research,
holdout, paper-shadow, execution-calibration, and explicit human promotion gates.
ML model development belongs to the separate Market Predictor project. Options,
futures, crypto, and latency-sensitive high-frequency trading are outside the
current system scope.

## Market And Provider Boundary

- Instruments: US-listed equities.
- Market clock: New York exchange time with the applicable exchange calendar.
- Persistence clock: UTC.
- Primary bars, quotes, news, and broker: Alpaca.
- Volume-sensitive research and execution require SIP coverage; IEX-only evidence
  is not interchangeable with SIP.
- Finviz supplies current screening and optional news enrichment. A current Finviz
  export may form today's operational wishlist but may not be projected backward as
  historical point-in-time membership.
- CSV/local data is valid for deterministic replay only when its provenance,
  adjustment policy, timestamps, and coverage are recorded.

Premarket, regular, postmarket, and overnight observations remain distinct cohorts.
Observing extended-hours data does not authorize an extended-hours order.
Extended-hours execution is a separate opt-in policy and must satisfy the broker's
eligible-asset, limit-order, and expiration requirements.

## Historical Coverage And Listing Boundary

The global research floor is the earliest real provider-supported observation in
2016. A dataset may begin later when its manifest shows later coverage. TradingFlow
does not fabricate earlier rows to fill a requested interval.

The 2016 floor is **not** an issuer-age requirement:

- a company listed after 2016 is not permanently excluded;
- point-in-time membership begins on its real effective listing or universe date;
- pre-listing absence is expected, not a data-quality failure;
- no pre-listing candles, quotes, news, or membership rows may be synthesized;
- a security becomes eligible only after it has enough real completed history for
  the selected study or strategy.

Conceptually:

```text
eligible_at =
    max(
        real_point_in_time_membership_at,
        strategy_history_warmup_completed_at,
        required_evidence_available_at
    )
```

The current swing momentum benchmark requires at least 252 genuine completed daily
sessions for each admitted security. Intraday studies use their independently frozen
bar/session requirements; they do not inherit the swing warm-up.

## Universe Boundary

Research and operation use different universe contracts:

- Promotable historical research requires provider-backed point-in-time membership,
  delistings, symbol changes, issuer identity, share-class deduplication, sectors,
  and terminal outcomes.
- Today's wishlists and Finviz screens are operational candidate sources, not proof
  of historical membership.
- Backtest and paper universes come from database wishlists. Generated run files are
  immutable audit artifacts, not hand-maintained ticker universes.
- One listed share class per issuer is retained by deterministic score and identity
  ordering.
- Missing point-in-time universe evidence fails strategy promotion closed.

## Research Boundary

Non-ML alpha research is intentionally limited to two tracks:

1. Swing cross-sectional momentum, followed only by isolated paired tests of stock
   trend, VCP, or validated classified catalysts.
2. Intraday point-in-time catalyst response and abnormal participation, followed by
   opening-response and pullback/reclaim studies before defining one entry rule.

No additional broad strategy family is admitted until these baselines are measured.
Generic sentiment, present-day universes projected backward, and parameter searches
remain diagnostic and cannot promote.

Every experiment freezes its exact observed data coverage and chronological
development, validation, embargo, and untouched holdout boundaries in the trial
registry before outcomes are read. No partition may begin before the manifest's
actual complete point-in-time coverage or before 2016. Holdout is one-use; a viewed
or failed holdout cannot be relabeled or retuned.

## Information-Time Boundary

- Indicators and features use completed bars only.
- A setup cannot fill on the bar that created it.
- Confirmation uses a later completed bar; execution occurs no earlier than the
  next eligible executable bar.
- Initial stops use only information completed before the fill.
- Derived candles are built chronologically from already available lower-timeframe
  bars.
- News availability is the latest defensible trading-time timestamp, including
  observed receipt and classifier completion when available.
- A later news revision cannot be moved backward to the original publication time.
- Historical REST news without proven first receipt and revision lineage is
  diagnostic unless a conservative provider-update-time contract is explicitly
  frozen.

## Execution And Risk Boundary

Backtest, paper, and future live modes share the same strategy evaluation,
order-planning, risk, and audit contracts. Only the data source and execution sink
change.

- Orders are modeled from executable quote sides where evidence is available.
- Spread, slippage, fees, participation, non-fill, partial-fill, and impact
  assumptions are explicit evidence, never hidden optimism.
- Account-risk budget and maximum position notional are separate controls.
- The planner preserves the strategy's structural invalidation price and reduces
  quantity; it does not tighten a stop merely to admit a trade.
- Multi-strategy results use one chronological capital pool and shared position
  slots. Independent strategy balances are research diagnostics, not deployable
  combined equity.
- Broker writes require idempotent intent, lifecycle journaling, reconciliation,
  and protective-stop coverage.

## Promotion Boundary

Research may end in only one explicit decision:

- `REJECT`
- `RETAIN_RESEARCH`
- `ADVANCE_TO_HOLDOUT`
- `PROMOTE_TO_PAPER_SHADOW`

Promotion requires the binding track-specific statistical, concentration, cost,
drawdown, sample-size, execution, and human-approval criteria. Missing evidence is a
blocker, never an implied pass.

The current integrated decision is `RETAIN_RESEARCH`. No current swing or intraday
research configuration is eligible for a new paper-shadow promotion from the
available evidence.

## Persistence And Deployment Boundary

- Backtest, paper, and live data roots remain isolated.
- SQLite is the local operational source of truth and must have one writer.
- Immutable research artifacts are content-addressed, hashed, cataloged, and written
  conditionally.
- Azure Blob archival is outside the hot candle, signal, risk, and broker path.
- Local secrets, paper state, and cached candles are preserved unless explicitly
  removed.

## Related Research Documents

- [Non-ML Strategy Research Program](research/non-ml-strategy-research-program.md) -
  binding research rules, evidence requirements, validation, and promotion gates.
- [Research Implementation Status](research/non-ml-strategy-research-implementation-status.md) -
  current implementation decision, verified coverage, and unresolved evidence.
