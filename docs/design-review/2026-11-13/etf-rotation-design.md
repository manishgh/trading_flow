# ETF sector rotation (12-1 momentum, top 3): design for approval (13 November 2026)

Status: proposal; nothing is built. Research only. This is the "declared neighbour" in
`docs/design-review/2026-11-13/etf-trend-core-design.md`. The trend rule itself ended at RETAIN_RESEARCH
(`docs/research/etf-trend-core-development-2026-11-13.md`): it halved the drawdown but kept only 58% of buy-and-hold's
return.

## The idea, in plain words

Each month, rank the 11 sector funds by how much they rose over the last 12 months, leaving out the most recent month.
Hold the 3 strongest, each only while it is also above its 10-month average; an empty slot sits in cash. Sell a fund when
it drops out of the top 3 or below its average. The aim is to keep more of the market's return than the plain trend rule
(by holding the strongest sectors), while keeping the trend rule's protection in falls.

## Published method (no invention)

- **Rotation:** Faber (2010), "Relative Strength Strategies for Investing" (SSRN 1585517):
  - rank sector funds by past return;
  - hold the top group, equal weight;
  - check once a month.
- **Absolute filter:** Antonacci (2014), *Dual Momentum Investing*. A fund must also be in its own uptrend; here that means the 10-month average, as in the trend rule.
- **Values:** 12-month look-back skipping the last month, top 3, 10-month average. These are the published values; nothing is tuned.

## How it works on our data

- **Funds:** XLK, XLF, XLE, XLV, XLY, XLP, XLI, XLB, XLU, XLRE, XLC (the 11 Select Sector funds). XLC joins once it has
  252 sessions of history (mid-2019).
- **12-1 return**, measured at a session's prior close: close 21 sessions ago divided by the close 252 sessions ago, minus
  one. Both closes are as-traded, from completed sessions only.
- **Decision:**
  - at each month's last close, the top 3 by 12-1 return are the candidates;
  - each is bought at the next open if its close is above its 210-session average and it is not already held.
- **Selling:** at each month's last close, a held fund is sold at the next open if either:
  - it is no longer in the top 3; or
  - its close is at or below its average.

  Funds staying in the top 3 are kept as they are, with no trimming (as option B).
- **Same-open swap:** when a fund is sold and its replacement bought at the same open, the sale fills first and frees
  its slot and its cash for the purchase.
- **Size of a new position:** marked equity (cash plus held funds at the last close) divided by 3, limited to the cash
  available.
- **Costs:** 5 bps per side, no commission, SEC and FINRA sell fees.

## What has to be built (smallest reusable changes)

1. **A ranked fund list by 12-1 return (screen command).**
   - `research-daily-screen` already keeps, each session, only the stocks ranked N to M by a value measured from earlier sessions (`--liquidity-tier`, by 20-day dollar volume).
   - **New options:** `--rank-by return_12_1` together with `--rank-tier 1-3`. The screen writes the measured 12-1 return as a column of `eligible.csv`, so every session's ranking is published and checkable.
   - The list is point-in-time: a session's members are known before it opens. It reuses the existing frozen-manifest workflow and the existing entry check (a purchase is allowed only if the fund is on the list for the session it fills in).
2. **An exit when a held fund is off the list:** `exit_rules.exit_when_off_list: true`.
   - It is checked on the strategy's exit schedule (month-end).
   - It reads the list's membership for the next session, the same one a purchase would use.
   - Backtest-only, like the month-end schedule; paper and live refuse it.
3. **Same-open swaps (opt-in run setting `portfolio.same_open_exits_release_capacity: true`).** Before admitting purchases at an open, positions whose sale fills at that same open release their slot and their sale proceeds. Only sales decided at or before the purchase's decision count.
4. **Size from marked equity (opt-in run setting `portfolio.size_from_marked_equity: true`).**
   - Each new position's slot is marked equity at the last completed close divided by the slot count, instead of starting capital plus closed profits.
   - It is still limited by available cash.

Changes 3 and 4 are off by default, so every existing strategy and published result is unchanged. Tests prove that
switching them off gives identical trades.

## Strategy and run settings (example)

```yaml
# configs/backtest/strategies/etf-rotation-12-1.bt-v1.yaml (catalog etf.rotation-12-1@1.0.0)
entry_rules:
  setup_type: momentum
  decision_schedule: month_end
  technical_factor: { metric: sma_distance, lookback_bars: 210, min_value: 0 }
exit_rules:
  initial_stop_mode: none
  profit_target_mode: none
  exit_on_entry_factor_at_or_below: 0
  entry_factor_exit_schedule: month_end
  exit_when_off_list: true
execution: { timeframe: 1d, slippage_bps: 5.0, opening_gap_policy: keep_and_exit_next_open }
```

```yaml
# configs/backtest/etf-rotation-profile.yaml
portfolio:
  max_concurrent_positions: 3
  max_position_notional_pct: 32.5   # 3 x 32.5% = 97.5%, room for gaps and fees
  same_open_exits_release_capacity: true
  size_from_marked_equity: true
```

**Published lists:**
- `sector-funds-top3-return-12-1-20170101-20261030` (11 funds, 252-session history and split look-back, top 3 by 12-1 return);
- `sector-funds-20170101-20261030` (the same 11 funds, no ranking), for the comparison.

## Test

- **Development:** 2017-2023. **Holdout:** 2024 to October 2026, opened once and only after a pass.
- **Comparison:** `etf.buy-and-hold-control@1.0.0` on the same 11 sector funds (11 slots), the same engine and costs.
- **Also reported:**
  - the trend rule on the same 11 funds, to show what the ranking adds;
  - monthly returns by version;
  - each year's worst drawdown;
  - the number of swaps;
  - the share of the time each fund was held.

**Pass and fail**, the same rules as the trend design, frozen before any run:
- **PASS to holdout:** worst drawdown at most about half of buy-and-hold's, and annual return at least 70% of buy-and-hold's.
- **FAIL:** drawdown more than 75% of buy-and-hold's, or annual return below 50% of it.
- **Otherwise** RETAIN_RESEARCH.

**Honesty notes:**
- **Small samples:** 3 funds out of 11 is a concentrated portfolio, so results depend heavily on a few sector moves (energy in 2021-2022, technology in 2020 and 2023).
- **Short record:** seven years are still a short record; the longer-history data purchase remains open.

## Verification

**Focused tests:**
- the 12-1 ranking and tier on a synthetic list (ties broken by symbol, prior-close basis only);
- the off-list exit firing only at month-end;
- a same-open swap where the sale frees the slot and cash for the purchase;
- marked-equity sizing;
- both portfolio settings off giving identical trades on the existing ETF and ORB fixtures;
- paper and live refusing `exit_when_off_list`.

**Then:** the affected test slices and a full test run, an independent code review, and the development runs through the normal saved-plan workflow.

## What is not in this design

- **Full monthly rebalancing** (trimming winners back to equal weight). That needs the target-weight rebalancer, a larger
  new execution path. It is only worth building if this rotation shows an edge.
- **Paper and live trading.** The month-end schedule, the ranked list and the off-list exit are backtest-only until a
  paper path is designed and approved.

## Scope

- **Work:** about one day of building and tests. Four small changes, two of them opt-in settings in the shared portfolio admission.
- **Runs:** two published lists and three development runs, each a few minutes.
