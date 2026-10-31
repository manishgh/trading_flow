# Swing execution stage 04

Stage 04A is a causal timing repair and an observational entry-session audit. It precedes a full paired five-minute execution replay. Independent review found that changing strategy.execution.timeframe alone changes the daily stop anchor, SMA5 exit and five-session holding rule. It can also backdate orders to the previous intraday context. Do not publish that config change as the same strategy.

## Frozen 04A scope

Use every completed trade from cycle03 `mr_a0b1`, `rsi5` and their `cost20` counterparts, in the five original cohorts. Verify the expected 288 cases from immutable summaries before any new prices are inspected. Retain exact strategy/version/hash, source summary hash, candidate ID, frozen initial stop and target, recorded fill timestamp and source-session mapping. Cost variants are correlated observations, not additional independent trades. No new strategy signals, portfolio returns, ranking or parameter search occurs in this audit.

Map each recorded daily entry fill back one daily duration to its native New York source session, cross-check against the original candidate entry source timestamp, and require an authoritative Alpaca calendar session. Inspect the entire regular session, including the final 30 minutes excluded from new entries by the strategy. Fetch native SIP/all 5-minute bars and native daily history with the same symbol-asof and dates as cycle03. Match the canonical daily-series hash with cycle03 before comparing old levels with fresh intraday observations. A mismatch is `price_basis_not_comparable`, not a zero-return or no-touch case.

Require the expected five-minute grid, opening/closing slices, no conflicting duplicates, valid OHLC and positive volume. Report every absent interval; absence may mean no qualifying prints or missing evidence. Never fill gaps with invented prices. With gaps, describe only first *observed* touches and never conclude uninterrupted no-touch coverage. Five-minute OHLC does not reveal within-bar ordering.

Record stop-only, target-only and both-level observations in the first slice; first later observed touch; same-slice ambiguity; and opening-price gaps beyond the stop. These describe price levels only. They do not prove orders were active, quantities protected, fills obtained, ordering, or profit/loss. Retain compact observed witness bars, extrema, coverage and source hashes in published audit results. Delete owned downloaded inputs only after independent comparison and review.

## Required causal repair

Separate historical stop context from actionable decision time. A triggered order must not precede setup availability or the actual decision as-of time; all evidence must already be known. Bind context with its index and source timestamp. Consumers must reject a differently aligned or incomplete context instead of deriving a fictional bar timestamp by subtracting duration from the later trigger time.

Tests cover prior-session context, overnight daily availability, holiday/weekend delay, early-close context, delayed known-at, unchanged daily decisions, mismatched context and downstream next-bar selection. Run affected kernel/planner/assembler/backtest/paper-adapter and deterministic execution tests. Preserve the cycle03 implementation and original results.

## Stage 04B: full paired execution replay

Implement a separately identified immutable execution-resolution policy, leaving exact daily strategy artifacts unchanged. Daily setup, SPY regime, ranking, stop information, SMA5 and five-session cap remain daily. Five-minute slices supply execution and protective observations. Both selected recipes disable trailing; do not add it.

Map daily source session opens to actual eligible opening slices and preserve the October 6 terminal session. Enforce next eligible execution after setup and decision availability, session eligibility, missing-open failures and precommitted liquidation. Define the first-slice activation delay or implement a reviewed causal opening-fill/bracket model with partial quantities and shared liquidity. Never infer immediate protection from a daily or five-minute OHLC bar.

Then run exactly two strategies × two costs × five cohorts (20 paired runs), one cohort at a time. Compare daily decisions with cycle03 and reconcile changed trades through execution and portfolio effects. Report monthly strategy/cohort results only. Historical executable-side validation additionally requires compatible timestamped bid/ask evidence; OHLC slippage scenarios cannot establish spread calibration.

Current roster, retrospectively adjusted prices, missing first-receipt lineage and the already-viewed window remain diagnostic limits. New chronological evidence and existing promotion gates follow; these stages cannot authorize paper/live orders.

Provider timestamp reference: [Alpaca market-data FAQ](https://docs.alpaca.markets/us/docs/market-data-faq). Native daily labels use New York dates; bars are not proof of historical first receipt.
