# ETF sector rotation, development test 2017-2023 (13 November 2026)

Design: `docs/design-review/2026-11-13/etf-rotation-design.md`, approved 13 November 2026. Code: commits `8edf4bc`,
`4c6d0e2` and `2a066ce`.

## What was run

All three runs start with $100,000 each, cover 1 January 2017 to 31 December 2023, charge 5 bps per side, use
as-traded daily bars from the evidence catalog (`64ac6058…`) and start their first decision at the 31 January 2017 close.

**The funds:** XLK, XLF, XLE, XLV, XLY, XLP, XLI, XLB, XLU, XLRE, XLC (`configs/research/sector-funds.txt`).
- **History:** each fund needs 253 sessions. XLC therefore joins in 2019; 10 funds are on the list until then.
- **Splits:** 253 sessions after each split are left out. The only splits fall on 5 December 2025, after this test.

**The rotation:** `etf.rotation-12-1@1.0.0` (`15dd1e26…`), plan 190, job `6e1e73fb`, `result.json` `7a838ea1…`.
- **List:** `sector-funds-top3-return-12-1-20170101-20261030` (manifest `75dda1c6…`). Each session it keeps the 3 funds with the highest 12-1 month return measured at the prior close.
- **Run settings:** `etf-rotation-profile.yaml`, 3 slots of 32.5%, same-open swaps and marked-equity sizing on.
- **Trades:** 57 purchases; 31 sales because a fund left the top 3, 23 because a fund fell below its average. XLK, XLC and XLY are still held at the end.
- **Lowest cash:** $2,236.

**The comparisons**, on the unranked list `sector-funds-20170101-20261030` (manifest `30b87989…`), plan 192, with `etf-trend-profile.yaml` and 11 slots of 8.8%:
- **Buy and hold:** `etf.buy-and-hold-control@1.0.0`, job `d335fce2`, `result.json` `d12043a5…`. 11 purchases, no sales.
- **Trend rule:** `etf.trend-10m@1.0.0`, job `6bd75d31`, `result.json` `2d22ca0f…`. 91 purchases, 83 trend sales.

**Stopped first attempt:** plan 190, job `457125c8`, kept as a record.
- The run's new cash check stopped it: on 1 February 2018 a swap left cash at -$142.91.
- **Cause:** marked-equity sizing spent all cash plus the sold fund's value at its last close, but the sale filled at the open, with slippage.
- **Fix (commit `2a066ce`):** with same-open swaps on, admission now keeps in cash the 2.5% that 3 × 32.5% slots leave unused, as the design intends. The cash check itself is unchanged.

## Pass and fail (frozen before the run)

| Measure | Rotation | Buy and hold | Ratio | Rule |
|---|---|---|---|---|
| Annual return over the window | 7.73% | 8.29% | 0.93 | PASS needs 0.70 or more; FAIL below 0.50 |
| Worst drawdown, daily marked equity | 22.20% (Jun 2022 to Mar 2023) | 34.59% (Feb to Mar 2020) | 0.64 | PASS needs about 0.5 or less; FAIL above 0.75 |
| Average share of equity invested | 84% | 94% | | |

**For reference, the trend rule alone on the same 11 funds:** 4.74% a year (ratio 0.57) with a 13.44% worst drawdown (ratio 0.39), close to its result on 15 funds.

**Decision: RETAIN_RESEARCH.**
- The rotation keeps 93% of buy-and-hold's return, well above the pass line.
- Its worst drawdown is 64% of buy-and-hold's: under the fail line (75%) but not the "about half" a pass needs.
- The 2024-2026 holdout stays unopened.
- No setting was changed after seeing results.

**Worst drawdown in each year (%, daily marked equity):**

| Year | 2017 | 2018 | 2019 | 2020 | 2021 | 2022 | 2023 |
|---|---|---|---|---|---|---|---|
| Rotation | 3.1 | 10.7 | 9.5 | 18.2 | 7.8 | 17.7 | 22.2 |
| Buy and hold | 2.4 | 16.8 | 14.1 | 34.6 | 5.5 | 22.9 | 16.9 |
| Trend rule | 2.5 | 9.8 | 9.3 | 11.8 | 4.9 | 10.8 | 13.4 |

**What happened:**
- **Ranking:** it adds return. The rotation earns 3% a year more than the trend rule on the same funds.
- **Concentration:** holding only 3 sectors gives back much of the trend rule's protection.
  - **2020:** February cost -9.0%, but the rotation stood aside in March (-2.7% against -13.4%). Like the trend rule, it then missed the April rebound (-1.0% against +11.6%).
  - **2022-2023:** the worst drawdown came from concentration. Energy (XLE) led the ranking through 2022 and then fell (June 2022 -11.0%, September -7.2%). That drawdown ran to March 2023 and deepened again in September 2023.
- **Holdings:** technology (XLK) was held longest (about 1,220 days across closed holdings), then energy (about 820).

## Monthly returns (% of month-opening equity, marked to market, New York session dates)

**etf.rotation-12-1@1.0.0**

| Year | Jan | Feb | Mar | Apr | May | Jun | Jul | Aug | Sep | Oct | Nov | Dec |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 2017 | 0.00 | 0.51 | 0.10 | 1.30 | 2.17 | -1.26 | 2.16 | 1.22 | 1.50 | 3.39 | 2.91 | 0.87 |
| 2018 | 5.78 | -2.41 | -3.71 | 0.56 | 2.68 | 1.13 | 1.95 | 2.92 | 0.45 | -8.14 | 2.53 | -3.04 |
| 2019 | -0.46 | 2.05 | 2.11 | -0.72 | -2.83 | 2.35 | 1.25 | 2.89 | 1.49 | -0.40 | 0.16 | 2.07 |
| 2020 | 0.85 | -9.00 | -2.70 | -1.03 | 4.45 | 1.37 | 5.99 | 7.66 | -4.68 | -2.84 | 10.26 | 3.55 |
| 2021 | -0.36 | 2.50 | 2.67 | 4.25 | 4.17 | -3.83 | 0.83 | 1.49 | -0.44 | 6.01 | -5.48 | 4.32 |
| 2022 | 1.85 | 1.12 | 3.88 | -5.16 | 7.44 | -10.98 | 5.31 | 0.78 | -7.16 | 1.91 | -0.15 | -3.35 |
| 2023 | 0.57 | -3.90 | -0.42 | 1.67 | -1.42 | 2.22 | 3.79 | -1.61 | -5.25 | -0.51 | 7.34 | 4.51 |

**etf.buy-and-hold-control@1.0.0 (11 sector funds)**

| Year | Jan | Feb | Mar | Apr | May | Jun | Jul | Aug | Sep | Oct | Nov | Dec |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 2017 | 0.00 | 2.92 | -0.62 | 0.69 | 0.96 | 0.09 | 1.54 | 0.09 | 1.16 | 1.63 | 2.72 | 0.23 |
| 2018 | 3.63 | -4.25 | -1.69 | 0.52 | 1.48 | 0.53 | 3.01 | 1.80 | -0.33 | -5.52 | 2.55 | -8.27 |
| 2019 | 6.78 | 2.75 | 1.37 | 2.66 | -5.00 | 5.51 | 0.97 | -0.85 | 1.59 | 1.18 | 2.50 | 1.91 |
| 2020 | -0.38 | -8.26 | -13.42 | 11.64 | 4.61 | 0.50 | 5.46 | 5.35 | -3.14 | -1.91 | 10.19 | 2.52 |
| 2021 | -1.04 | 2.63 | 4.99 | 4.92 | 1.12 | 0.75 | 2.09 | 2.55 | -4.90 | 6.67 | -1.21 | 4.93 |
| 2022 | -4.68 | -2.54 | 4.04 | -6.91 | 0.41 | -8.86 | 8.29 | -3.36 | -9.67 | 7.82 | 5.89 | -5.46 |
| 2023 | 5.69 | -2.93 | 2.02 | 1.22 | -1.57 | 6.12 | 3.02 | -2.12 | -4.90 | -2.30 | 8.32 | 3.95 |

**etf.trend-10m@1.0.0 (11 sector funds)**

| Year | Jan | Feb | Mar | Apr | May | Jun | Jul | Aug | Sep | Oct | Nov | Dec |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 2017 | 0.00 | 0.98 | -0.54 | 0.94 | 1.17 | 0.15 | 1.34 | 0.49 | 0.57 | 1.84 | 2.13 | 0.24 |
| 2018 | 3.95 | -3.47 | -2.17 | -0.15 | 1.09 | 0.01 | 1.15 | 1.84 | -0.18 | -4.23 | 1.76 | -2.91 |
| 2019 | 0.01 | 0.83 | 1.25 | 1.45 | -3.78 | 2.12 | 0.72 | -0.21 | 1.40 | 0.76 | 2.17 | 1.72 |
| 2020 | 0.24 | -6.63 | -0.63 | -0.34 | 1.20 | 0.61 | 2.92 | 3.86 | -1.97 | -1.51 | 6.76 | 1.93 |
| 2021 | -0.91 | 3.88 | 3.85 | 4.73 | 1.61 | 0.50 | 1.36 | 2.29 | -4.28 | 4.96 | -1.82 | 4.28 |
| 2022 | -2.56 | -0.50 | 1.69 | -2.90 | 2.15 | -4.55 | 1.61 | -0.15 | -2.50 | 0.58 | 0.36 | -2.55 |
| 2023 | 0.39 | -1.64 | -0.18 | 0.74 | -1.21 | 2.23 | 2.47 | -1.65 | -2.97 | -1.29 | 2.20 | 3.64 |

## Known differences from the papers

These differences apply to all three versions:
- **Dividends:** as-traded prices leave them out.
- **Cash:** earns 0%, not Treasury-bill interest.
- **No trimming:** positions are never trimmed back to equal weight.
- **Average:** the 10-month average is a 210-session daily average.

Two of them hit the comparison unevenly:
- **Dividends** cost buy-and-hold most, because it is the most invested.
- **0% cash** costs the trend rule most (37% in cash) and the rotation least (16%).

The return ratio is far from its line, so the decision rests on the drawdown ratio, which these items barely move.

**Short record:** seven years with one fast crash and one slow bear market. As the design says, this test is about
drawdown and behaviour; it cannot prove extra return.

## Next options

- **Stop the ETF line here:** neither rule passes the frozen drawdown line on its own.
- **Longer history:** buy survivorship-free data back to the 1990s (Fable's advice), then rerun both rules unchanged. Seven years cannot separate a real effect from the 2020 and 2022 episodes.
- **A new, separately frozen version:** for example 5 funds in place of 3, to lower concentration. That would be a new hypothesis with its own design and pass rules. It must not be tuned on these results and then reported as a pass.
