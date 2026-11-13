# ETF trend core: design for approval (13 November 2026)

Status: proposal; nothing is built. Research only. This is the next step of the independent review's plan
(`docs/research/fable-review-2026-11-12.md`, sections 1.4 and 4.2). Single-stock RSI(2) mean reversion was rejected
by the ten-year study (`docs/research/reversal-signal-study-2026-11-13.md`).

## The idea, in plain words

Hold a small set of broad index and sector funds only while each is in an uptrend, and sit in cash (or a short-term
Treasury fund) when it is not. The aim is not to beat the market in a rising year; it is to keep most of the market's
long-run return while avoiding the deep falls (2008, 2020, 2022). That is the one trading effect with a long,
independent record across markets and decades, and it needs few trades, so costs barely matter.

## Published method (no invention)

- **Primary, Faber (2007), "A Quantitative Approach to Tactical Asset Allocation" (SSRN 962461):**
  - at each month's last close, hold an asset if its price is above its 10-month simple moving average; otherwise hold cash;
  - equal weight across the assets held;
  - check once a month.
- **Declared neighbour, relative strength rotation (Faber 2010, Antonacci 2014 "dual momentum"):**
  - each month, rank the sector funds by their return over the last 12 months, skipping the most recent month;
  - hold the top 3, each only if it also passes the 10-month trend rule;
  - otherwise cash.
- **No parameters are tuned.** The 10-month average, 12-month look-back and top-3 are the published values.

## Assets

- **Core:** SPY, QQQ, IWM, DIA.
- **Sectors:** XLK, XLF, XLE, XLV, XLY, XLP, XLI, XLB, XLU, XLRE, XLC.
- **Cash:** recorded as 0% return.
- **Data:** the 2016-2026 daily bars we already hold. XLC starts in mid-2018, so it joins when it has 10 months of history.

## Test

- **Development:** 2017-2023. **Holdout:** 2024 to October 2026, opened once and only after a pass.
- **Costs:** 5 basis points per side. These are the most liquid funds in the world.
- **Next-open fills:** the decision is made at the month-end close and filled at the first open of the new month.
- **Also reported:**
  - buy-and-hold of each fund and of an equal-weight basket;
  - the number of switches;
  - each year's drawdown;
  - every result month by month, by strategy version.
- **Two honesty notes:**
  - **One crash:** ten years hold only one real crash (2020) and one slow bear market (2022), so the record is short. Fable advises adding longer history (survivorship-free data back to the 1990s) before trusting the result. That purchase is a separate decision.
  - **Few trades:** a monthly rule makes few decisions, so a t-statistic over 2017-2023 will be weak whatever happens. The test here is about drawdown and behaviour, not proof of extra return.

## Pass and fail, frozen before any run

**PASS to holdout** needs all of these:
- the worst drawdown is at most about half of buy-and-hold's on the same basket;
- the annual return is at least 70% of buy-and-hold's;
- no parameter was changed after seeing results.

**FAIL** if either holds:
- the drawdown is not meaningfully lower (more than 75% of buy-and-hold's);
- the annual return is below 50% of buy-and-hold's.

Anything in between is RETAIN_RESEARCH.

## What has to be built (smallest reusable changes)

1. **A month-end decision schedule** in the shared decision path. It is generic: "evaluate only on the last session of each month". The same engine computes the indicators: the 10-month average as SMA(210) on daily closes, and the 12-1 month return.
2. **Equal-weight sizing across the selected assets** and a rebalance at each decision. A generic portfolio rule, not ETF-specific.
3. **A ranking rule** for the rotation neighbour (top N by a named indicator), extending the existing `portfolio_rank_mode`.
4. **Two new research catalog entries:** `etf.trend-10m@1.0.0` and `etf.rotation-12-1@1.0.0`.
5. **Study and report:** the ETF screen (`index-and-sector-funds-20170101-20261030`, built today) and the existing backtest path. These are full portfolio replays, which are cheap here with only 15 assets.

## Paper and live

- **Paper:** only after a holdout pass, and then exactly the tested configuration: one rebalance a month, market orders at the open.
- **Fable's suggested split** for real money later: 60-70% of the capital in this core.
