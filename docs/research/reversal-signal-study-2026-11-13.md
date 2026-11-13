# Ten-year signal study: Connors RSI(2) reversal on US stocks, 2017-2023 (13 November 2026)

This is step 4.1.3 of the independent review's plan (`docs/research/fable-review-2026-11-12.md`).
The command is `research-signal-study` (commit `34d7484`). Signals come from the shared decision path on frozen,
as-traded catalog data: daily bars `64ac6058…` and corporate actions `864016f2…`.

## What was tested

**Rule:** `swing.connors-rsi2@9.1.0`, the Connors and Alvarez (2008) rule on as-traded prices:
- buy when the close is above its 200-day average and RSI(2) is below 5;
- sell when the close is above its 5-day average;
- no stop.

**Measurement:**
- each signal enters at the next open and exits at the open after the exit signal;
- returns are measured from open to open;
- 10 basis points of cost per side;
- a split during the hold closes the trade at the last open before the ex-date.

**Benchmark:** the equal-weight return of every eligible stock over the same days. The edge is the trade's net return minus that benchmark. A rule only earns money of its own if it beats simply holding the eligible stocks.

**Stock lists (point in time):**
- **The rules:** prior close at least $5, 20-day average volume at least 1M shares, 200 sessions of history, funds excluded. A stock-day is skipped when a split or a suspected unrecorded split lies in its 200-session look-back.
- **Tiers:** ranked by 20-day dollar volume into the top 500 (screen `reversal-pit-tier-1-500-20170101-20261030`) and ranks 501-1,500 (`reversal-pit-tier-501-1500-20170101-20261030`).
- **Window:** 2017-01-01 to 2023-12-31. The later years were not read.

**Pass rules:** the analyzer's defaults, frozen before the run:
- at least 2,000 trades;
- an edge of at least 0.15% per trade after costs;
- a daily t-statistic of at least 3;
- at least 70% of years positive;
- no year above 35% of the profit;
- still positive at double cost.

Fable's stop rule: retire single-stock mean reversion if the main setting earns under 0.10% per trade, or its t-statistic is under 2.

## Results

| Setting | Trades | Gross per trade | Net per trade | Eligible stocks, same days | Edge per trade | Daily t | Win rate | Decision |
|---|---|---|---|---|---|---|---|---|
| **Top 500, next open (main)** | 21,082 | +0.53% | +0.33% | +0.27% | **+0.06%** | -0.42 | 62.6% | REJECT |
| Ranks 501-1,500, next open | 15,653 | +0.38% | +0.18% | +0.23% | **-0.05%** | -1.99 | 61.8% | REJECT |
| Top 500, same close (upper bound, not tradable) | 20,946 | +0.55% | +0.35% | +0.27% | **+0.08%** | -0.02 | 63.6% | REJECT |

**Other measures, main setting:**
- **Market sensitivity:** 0.94. The trades move almost one for one with the market.
- **Daily return after removing that:** -0.01%.
- **At double cost:** -0.14% per trade.
- **Positive years:** 71%, with 49% of the profit in a single year.

Edge per trade by year, main setting:

| Year | 2017 | 2018 | 2019 | 2020 | 2021 | 2022 | 2023 |
|---|---|---|---|---|---|---|---|
| Edge per trade | +0.06% | +0.01% | -0.09% | +0.47% | +0.26% | -0.50% | +0.01% |

## What it means

**The rule reproduces the published figures.** Concretum (Nov 2025, S&P 500 members 1996-2025) reported +0.45% per trade before costs with 64% winners. We measure +0.53% with 63%.

**That return is almost entirely the market's own rise during the 4-day hold.** Against the stocks it could have held instead, the edge is about zero:
- **Main setting:** +0.06%, t of -0.42.
- **The less liquid tier:** negative.
- **Buying at the exact signal close,** which no trader can do: only +0.08%.

**Verdict.** The main setting is below Fable's stop rule of 0.10% per trade with a t-statistic of at least 2. Under the declared plan, single-stock RSI(2) mean reversion is retired as a source of edge. That covers Connors long and short, and the snapback and recovery variants, which are the same idea.

**What this does not cover:**
- the rule on index funds, a separate study;
- reversal with other definitions, for example ranked weekly losers;
- trading it as a cheap way to hold the market. That is just beta: it is easier and cheaper to hold an index fund.

## Survivor bias (step 4.1.5)

The same rule, run on the complete-stocks list (`connors-complete-stocks-20170101-20261030`), which keeps only the 2,281
stocks that traded cleanly for the whole decade:

| List | Trades | Gross per trade | Eligible stocks, same days | Edge per trade | Daily t |
|---|---|---|---|---|---|
| Point in time, top 500 | 21,082 | +0.53% | +0.27% | +0.06% | -0.42 |
| Complete stocks only (survivors) | 23,692 | +0.52% | +0.32% | +0.00% | -0.20 |

**Survivor bias** inflates what holding stocks appears to earn: the survivors' benchmark is +0.32% against +0.27% over the same 4-day holds. It does not create or remove the rule's own edge, which is zero on both lists.

**The complete-stocks list** therefore makes absolute returns look about 0.05% per trade better than reality. It must not be used to report profit figures; the point-in-time lists replace it for research.
