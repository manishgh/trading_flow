# ETF trend core, development test 2017-2023 (13 November 2026)

Design: `docs/design-review/2026-11-13/etf-trend-core-design.md`, approved 13 November 2026 with option B: the trend
rule first, inside the existing per-fund engine, without monthly rebalancing. Code: commits `fb3f6fc` and `4ea50a8`.

## What was run

**Versions**, each run as its own job with its own $100,000 (saved plan 186):
- `etf.trend-10m@1.0.0` (`e998f9cc…`), the rule from Faber (2007):
  - at each month's last close, hold a fund whose close is above its 210-session average, otherwise hold cash;
  - fill at the next open.
- `etf.buy-and-hold-control@1.0.0` (`b39db129…`), the comparison: buys every fund at its first month-end decision and never sells.

**Funds:** SPY, QQQ, IWM, DIA and the 11 Select Sector funds. Fund list
`index-and-sector-funds-210-sessions-20170101-20261030` (manifest `ad44346d…`):
- prior close at least $5;
- 20-day average volume at least 1M shares;
- 210 sessions of history;
- any day whose 210-session look-back holds a split is left out. The only splits, on 5 December 2025 (XLB, XLE, XLK, XLU, XLY), fall after this test.

**Settings:** profile `etf-trend-profile.yaml`:
- 15 fixed slots of 6.5% of equity;
- 5 bps slippage per side, no commission, SEC and FINRA sell fees;
- as-traded daily bars from the evidence catalog (`64ac6058…`).

**Window:** 1 January 2017 to 31 December 2023. The first decision is the 31 January 2017 close, so both versions hold cash in January 2017.

**Results:**
- Trend: job `6bd8d1dd`, `result.json` `b646e4ea…`. 120 entries, 109 sells by the trend exit, 11 still held at the end.
- Control: job `86a63b21`, `result.json` `b0aa5738…`. 15 entries, no sells.
- Both report complete results.

**Failed first attempt:** jobs `187dab32` and `c99f5365`, plan 186, kept as a record. The trend job finished all 15 funds and then ran out of disk while writing its results; the control job failed because the disk was full. Two causes:
- **Too many decisions:** the runner built a full decision for every session, not only month-ends.
- **Large records:** each daily decision record carries the fund's whole drawdown history.

Commit `4ea50a8` makes the runner skip off-schedule sessions before deciding. The trades are identical, as the tests show.

## Pass and fail (frozen before the run)

| Measure | Trend rule | Buy and hold | Ratio | Rule |
|---|---|---|---|---|
| Worst drawdown, daily marked equity | 13.96% (Jan 2022 to Mar 2023) | 34.44% (Feb to Mar 2020) | 0.41 | PASS needs about 0.5 or less; FAIL above 0.75 |
| Annual return over the window | 5.38% | 9.24% | 0.58 | PASS needs 0.70 or more; FAIL below 0.50 |
| Average share of equity invested | 65% | 95% | | |

**Decision: RETAIN_RESEARCH.**
- The drawdown is well inside the pass line, but the return falls short of 70% of buy-and-hold's.
- Neither fail rule holds.
- The 2024-2026 holdout stays unopened: it is opened only after a pass.
- No setting was changed after seeing results.

**Worst drawdown in each year (%, daily marked equity):**

| Year | 2017 | 2018 | 2019 | 2020 | 2021 | 2022 | 2023 |
|---|---|---|---|---|---|---|---|
| Trend rule | 2.6 | 10.2 | 11.1 | 13.0 | 4.9 | 11.5 | 14.0 |
| Buy and hold | 2.5 | 18.2 | 15.3 | 34.4 | 5.4 | 24.2 | 18.9 |

**What happened:**
- **Steep crash (2020):** the rule did what it is meant to do. It left after February (-6.95%) and avoided March (-0.89% against -13.41%).
- **Fast recovery (April 2020):** it then missed most of the rebound (-0.45% against +12.11%), because it re-enters only at a later month-end.
- **Slow bear market (2022):** the drawdown was about half of buy-and-hold's.
- **Choppy 2023:** it lost to repeated in-and-out switching. Its worst drawdown runs from January 2022 to March 2023.

## Monthly returns (% of month-opening equity, marked to market, New York session dates)

**etf.trend-10m@1.0.0**

| Year | Jan | Feb | Mar | Apr | May | Jun | Jul | Aug | Sep | Oct | Nov | Dec |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 2017 | 0.00 | 1.54 | -0.36 | 1.11 | 1.11 | 0.25 | 1.59 | 0.46 | 0.99 | 2.17 | 2.35 | 0.34 |
| 2018 | 4.42 | -3.39 | -2.27 | 0.02 | 1.85 | 0.10 | 1.70 | 2.44 | -0.22 | -5.27 | 1.33 | -3.44 |
| 2019 | -0.20 | 0.82 | 1.14 | 1.86 | -4.68 | 1.54 | 0.53 | -0.81 | 1.24 | 1.15 | 2.61 | 1.91 |
| 2020 | 0.11 | -6.95 | -0.89 | -0.45 | 1.43 | 0.96 | 3.01 | 4.80 | -2.54 | -1.61 | 8.35 | 2.73 |
| 2021 | -0.49 | 3.57 | 3.69 | 4.46 | 1.26 | 1.03 | 1.16 | 2.40 | -4.36 | 5.30 | -1.73 | 3.89 |
| 2022 | -3.09 | -0.74 | 1.15 | -2.72 | 1.56 | -3.34 | 1.15 | -0.13 | -1.80 | 0.42 | 0.26 | -3.06 |
| 2023 | 0.55 | -1.73 | 0.46 | 0.86 | -0.53 | 2.76 | 2.92 | -1.88 | -3.54 | -1.29 | 2.44 | 3.62 |

**etf.buy-and-hold-control@1.0.0**

| Year | Jan | Feb | Mar | Apr | May | Jun | Jul | Aug | Sep | Oct | Nov | Dec |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 2017 | 0.00 | 2.97 | -0.42 | 0.92 | 0.96 | 0.21 | 1.74 | 0.17 | 1.42 | 2.01 | 2.78 | 0.33 |
| 2018 | 4.19 | -3.96 | -1.93 | 0.51 | 2.13 | 0.47 | 3.03 | 2.39 | -0.33 | -6.16 | 2.16 | -8.66 |
| 2019 | 7.31 | 3.01 | 1.21 | 3.00 | -5.61 | 5.86 | 1.10 | -1.30 | 1.54 | 1.54 | 2.89 | 2.08 |
| 2020 | -0.28 | -8.14 | -13.41 | 12.11 | 4.91 | 1.30 | 5.32 | 6.19 | -3.49 | -1.98 | 10.90 | 3.18 |
| 2021 | -0.61 | 2.57 | 4.46 | 4.68 | 0.85 | 1.37 | 1.78 | 2.66 | -4.86 | 6.61 | -1.18 | 4.29 |
| 2022 | -5.36 | -2.62 | 3.76 | -7.77 | 0.18 | -8.71 | 8.77 | -3.54 | -9.73 | 8.04 | 5.60 | -5.85 |
| 2023 | 6.20 | -2.68 | 2.39 | 1.10 | -0.55 | 6.08 | 3.31 | -2.17 | -4.94 | -2.43 | 8.72 | 4.54 |

## Known differences from the paper, and whether they change the decision

**Differences that work against the trend rule:**
- **Dividends:** as-traded prices leave them out (about 1.5-2% a year on these funds). Buy-and-hold holds more of the time, so it loses more of them.
- **Cash earns 0%:** the paper uses Treasury bills (about 1.8% a year on average in 2017-2023), and the trend rule holds about 35% cash.

A rough allowance, estimated rather than measured, adds about 1.2% a year to buy-and-hold and about 1.8% to the trend rule. That moves the return ratio from 0.58 to about 0.65, still under 0.70. The decision would not change.

**Differences that affect both versions:**
- **Fixed slots:** positions are sized when bought and never rebalanced (option B).
- **Daily average:** the rule uses a 210-session average of daily closes, not ten month-end closes.

**Short record:** seven years contain one fast crash and one slow bear market. As the design says, this test is about drawdown and behaviour; it cannot prove extra return.

## Defects found and fixed while building this

1. **Friday decisions refused (commit `fb3f6fc`).** A daily decision was checked against the research list's entry for the UTC date of its decision time, which is the next calendar day. Every Friday close, and every close before a holiday, was checked against a closed day and refused.
   - **Who was affected:** earlier daily backtests on research lists, which includes the retired Connors whole-market plans, never traded those days.
   - **Who was not:** the ten-year signal study (`research-signal-study`) uses its own lookup and was not affected.
   - **The fix:** the check now uses the session the decision fills in.
2. **Split check too short.** The check that a research list leaves out enough sessions after a split ignored the 210-session average. It now counts the rule's technical-factor bars. The 200-session fund list is therefore refused for this rule, and the 210-session list above was published for it.
3. **Paper and live refusal.** Paper and live trading, the position guardian, operator-direct entry and the signal study refuse strategies with a decision schedule or the new exit before any order work. Those paths do not apply the rules yet.

## Next options

- **Stop here:** the rule halves the drawdown but gives up too much return under the frozen rules.
- **Rotation version:** the 12-1 month rotation needs the monthly rebalancer (option A, a new component that needs its own design approval). It holds fewer, stronger funds and may keep more return.
- **Longer history:** buy survivorship-free data back to the 1990s, as Fable advised, before trusting any monthly trend result.
