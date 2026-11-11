# TradingFlow: project overview, strategies, results and expectations (10 November 2026)

This document explains the whole project in one place: what TradingFlow is, how a backtest flows
through the system, every strategy we keep, what the tests have shown so far, what we should expect
and why, what is unfinished, and the research questions we want answered next. It is written so
that a reader without the code, including another research model, can understand and check it.

Statements marked **(to verify)** are my understanding and still need a source. Numbers without
that mark come from our own runs and files.

## 1. What TradingFlow is

TradingFlow is a stock-trading research and paper-trading system for US-listed shares, written in
C# (ASP.NET Core web app, SQLite database, Alpaca as data provider and paper broker). Machine-learning
work lives in a separate Python project (Market Predictor) and is not part of this document.

Its central idea is **one decision path**: a backtest, a paper-trading run and any future live run
use the same code to compute indicators, decide on a trade, size it and plan the order. Only the
source of prices (stored history or live feed) and the place orders go (a simulator or the broker)
differ. This is meant to stop a strategy that "works in the backtest" from behaving differently
when traded.

What it may do today (from `docs/operating-boundaries.md`):

- Research and backtest any strategy that comes from a published method or that the user asks for,
  long or short, swing (days) or same-day.
- Paper trade through Alpaca. Same-day strategies stay backtest-only until a paper driver is approved.
- Live trading stays disabled until a frozen strategy passes research, an untouched holdout period,
  paper-shadow trading, execution checks and an explicit human decision.
- The current overall decision is `RETAIN_RESEARCH`: no strategy is eligible for paper trading.

## 2. How a backtest flows through the system

1. **Stock source.** A saved wishlist (for example `volatile`, 25 high-volatility stocks) or a saved
   Finviz screen supplies the candidate stocks. These are today's lists, so a test on them carries
   hindsight: the list already reflects which stocks became interesting.
2. **Saved plan.** On the Backtests page (or its API) the operator picks strategies, a test window,
   starting capital, risk limits and the stock source, previews, then saves an immutable plan.
   - Stocks can be split into groups, alphabetically or in a seeded random order (built 9 November);
     every strategy and group is its own independent portfolio with its own capital.
   - A plan can carry **daily eligibility rules** (built 9 November): a stock may trade only on days
     it passed price, volume and ATR minimums measured from earlier sessions only.
   - Strategies that use company news need a frozen **news evidence package** selected in the plan.
3. **Prepared data.** Starting the plan freezes the exact candles and exchange calendars the run
   will use (Alpaca SIP feed), so a re-run reads the same inputs.
4. **Signals and decisions.** For each stock and completed bar, the shared engine computes indicators
   (RSI, moving averages, ATR, relative volume) and the strategy's rules decide whether a setup exists.
   Decisions use only completed bars; orders fill on a later bar.
5. **Risk and sizing.** The shared planner sizes the order from the account-risk budget and the
   position cap; strategies without a stop are sized by position notional instead.
6. **Execution replay.** Daily strategies fill at the next session's open. Same-day (opening-range)
   strategies replay five-minute candles. Missing five-minute candles caused by trades that set no
   price (odd lots, average-price trades) are confirmed from Alpaca's trade records (built 9 November);
   any other gap stops the portfolio's economics from that point rather than guessing.
7. **Results.** Each run publishes its trades, decision audit and monthly results, reported per
   strategy version and group, by month, never pooled. Results are research evidence only.

Rules that apply everywhere: no look-ahead (nothing a decision could not have known at its time),
explicit costs and slippage, New York market time for calculations and UTC for storage, every new
design approved by the user before it is built, and market data, news and published results never
deleted.

## 3. The strategies we keep

The catalog (`configs/strategy-catalog.json`) holds eight research artifacts. A strategy version
that fails its frozen pass/fail rules is retired and recorded in
`docs/research/retired-strategy-artifacts-2026-11-07.md`.

### 3.1 Connors RSI(2) mean reversion (swing, daily)

Source: Connors and Alvarez (2008), *Short Term Trading Strategies That Work*. Idea: in a stock that
is in a long-term uptrend, a sharp two-day pullback tends to bounce within days.

| Version | Rules | Differences from the book |
|---|---|---|
| `swing.connors-rsi2@9.0.0` | Long when close above its 200-day average and RSI(2) below 5; exit when close above its 5-day average. No stop, target or time limit. | Fills at the next open instead of the signal close; sized by position notional because there is no stop. |
| `swing.connors-rsi2@7.0.0` | Same entry and exit, plus a 2xATR stop, 2.5R target, five-bar cap and S&P 500 trend filter. | The added risk overlays are our own and disclosed. |
| `swing.connors-rsi2-short@2.0.0` | Mirror for shorts: close below its 200-day average, RSI(2) above 95; cover when close below its 5-day average. | Same as 9.0.0; a short without a stop has no upper bound on loss. Research only. |

RSI(2) is the Relative Strength Index over two days: a 0-100 reading of how one-sided the last two
moves were; below 5 means two strong down days.

### 3.2 Snapback reversal (swing, daily)

Sources: Jegadeesh (1990) and Lehmann (1990), recent large losers tend to rebound and winners give
back over the following weeks; Chan (2003), the reversal happens mainly when the move came without
company news.

| Version | Rules |
|---|---|
| `swing.snapback@2.0.0` | Long when RSI(14) below 30 and the close at least 10% below its 20-day average, and no company news in the previous 120 hours; exit when close above its 5-day average or after 5 bars. No stop. |
| `swing.snapback-short@2.0.0` | Mirror: RSI(14) at least 70, close at least 10% above its 20-day average, no recent news; cover below the 5-day average or after 5 bars. |

These are operational versions of the published effect, not replications: the papers rank all
stocks by past return, while ours uses a stretch threshold from the user's saved Finviz screen.

### 3.3 Opening-range breakout (same-day, five-minute candles)

| Version | Rules | Status |
|---|---|---|
| `technical.opening-range-breakout@1.2.0` | 15-minute opening range; after a completed five-minute close beyond the range, enter at the next open in that direction; stop at the breakout candle; 3R target; exit before the close. | Our own baseline (approved 27 October). |
| `technical.opening-range-breakout@2.0.0` | Zarattini, Barbon and Aziz (2024, SSRN 4729284) trade rules: 5-minute range, trade in the direction of its body, stop order at the range, stop 10% of the 14-day ATR, no target, one entry per stock per day. | The paper's daily selection of the 20 "stocks in play" is **not** applied yet. |

### 3.4 Recovery with news avoidance (swing, daily)

`technical.recovery-sma5-news-avoidance@1.0.0`: buy a pullback in an uptrend that reclaims its 10-
and 20-day averages, skipping stocks with recent company news. A research diagnostic kept as the
control for snapback.

## 4. Results so far (diagnostic, current-wishlist stocks)

Full tables: `docs/research/volatile-core-strategies-2026-11-09.md`. Setup: 23 or 24 stocks from the
`volatile` wishlist (SOXL and SIVE excluded: SIVE has no Alpaca data), random groups of 5,
$10,000 per group portfolio, 1% account risk, 25% position cap, at most 4 positions, daily
eligibility rules.

- **Connors, 21 Apr - 20 Oct 2026.** Mostly small monthly results of a few percent either way;
  9.0.0 (no stop) had the largest swings (best month +9.83%, worst -3.95%); only 0-7 closed trades
  per month across five groups. Too few trades to separate skill from luck.
- **ORB, October 2026.** Every group with a result lost: 1.2.0 -0.13% to -6.99%, 2.0.0 -2.29% to
  -6.80% (175 and 106 trades in groups 2-5). Group 1 is withheld (data gaps that cannot be proven
  quiet). Loss analysis:
  - Costs: about $7 per trade against only about $17-20 real risk per trade, because the 25% cap
    limited each position to $2,500. ORB 1.2.0 roughly broke even before costs.
  - ORB 2.0.0's stop (10% of daily ATR, about 0.7% of price) sits inside normal opening-minute
    swings; 70 of its 106 trades had entry and stop in the same five-minute candle and were counted
    as losses because the order inside the candle is unknown.
  - The stocks moved a lot but in both directions; breakouts need follow-through in one direction.
  - The paper's edge is attributed to its daily stock selection, which we have not applied.
- **Snapback, 21 Apr - 20 Oct 2026** (rebuilt news evidence, package `7360e18a`). About 2 long and
  16 short trades in total; most stock-days failed the $20 / 2M-share eligibility rule or were not
  stretched enough; recent company news vetoed 6 long and 86 short setups. Too few trades to judge.

## 5. What we expect, and why

### 5.1 General expectations

- **Published edges shrink after publication.** McLean and Pontiff (2016) found anomaly returns
  about 26% lower out of sample and about 58% lower after publication **(to verify exact figures)**.
  A test in 2026 of a method published in 2008 (Connors), 1990 (reversal) or 2024 (ORB) should be
  expected to show a smaller edge than its source, possibly none.
- **Current wishlists flatter or distort results.** Stocks chosen today because they are "volatile"
  or "interesting" were partly chosen for what they did. Results on them are diagnostics only.
- **Costs matter most for short holding periods.** Day trades risk a small amount per trade, so fixed
  fees and slippage eat a large share of each trade's expected gain. Account size and position cap
  decide whether a same-day strategy can be tested fairly.
- **Sample size decides what we can conclude.** If trade results in R (multiples of the planned
  loss) have a spread (standard deviation) of about 1.5R, detecting an average edge of +0.1R with the
  design's t-statistic of at least 3 needs roughly (3 x 1.5 / 0.1)^2, about 2,000 trades. One month
  of 4-5 stocks gives 100-200 trades; six months of a daily top-20 selection gives a few thousand.
  Swing strategies on a few dozen stocks give tens of trades per six months, far too few.

### 5.2 Per strategy

| Strategy | What the source reports | What we should expect here |
|---|---|---|
| Connors RSI(2) long | High win rate (often quoted around 70-80%) with small average gains, mostly tested on large US stocks and index ETFs **(to verify)** | Positive but small results on liquid uptrending stocks; unclear on volatile small and mid caps, which can keep falling. Without a stop, single large losses dominate. Needs years of data or a much larger stock universe for a verdict. |
| Connors RSI(2) short | The book reports the short side as weaker than the long side **(to verify)** | Expect weak or negative results: shorts fight the long-run upward drift and carry unbounded loss without a stop. Borrow costs are not modelled. |
| Snapback long/short | Weekly reversal: losers rebound, winners give back, strongest without news; the effect has weakened since the 1990s and depends on liquidity **(to verify)** | Few trades with our thresholds; the stretch rule (10% from the 20-day average and RSI 30/70) is strict on liquid stocks. Expect small samples unless the stock universe grows. |
| ORB 1.2.0 | Our own baseline, no published source | Near break-even before costs in October; costs decide the outcome. |
| ORB 2.0.0 with stocks in play | The paper (2016-2023, all US stocks with relative-volume selection, 4x leverage, $0.0035/share) reports strong returns attributed to the top-20 selection **(numbers to verify)** | After publication decay, with realistic spreads and slippage, a smaller edge if any. The planned six-month test (1 May - 30 Oct 2026) can at best reach `RETAIN_RESEARCH`; it needs about 1,500 trades and 120 sessions to be read at all. |

## 6. Work in progress and open decisions

### 6.1 ORB stocks-in-play build (uncommitted, review in progress)

Implemented in `C:\project\trading_flow_swing` (branch `approved-execution-and-signal-study`), not
committed, independent code review running:

- Daily ranking of each day's eligible stocks by first-five-minute relative volume (mean of the
  previous 14 sessions), at least 100%, top 20, ties by issuer; membership only from 09:35 New York.
- Per-share commission and a 4x gross exposure cap in the five-minute replay; stop measured from the
  actual fill; exit at the session close.
- One-minute candles settle whether entry or stop came first inside a five-minute candle; same-minute
  cases still count as losses.
- Download planners for opening five-minute candles and for one-minute candles of selected stocks.

Decisions needed from the user:

1. A new ORB version and run profile to switch these on (4x, $0.0035/share, top 20, 1% risk) needs a
   new catalog entry.
2. The paper lets one position exceed account equity under 4x leverage; our shared risk planner
   (also used by paper and live trading) caps a position at 100% of equity. Change it or not.
3. Alpaca publishes no issuer identifiers; two share classes of one company are not merged.
4. One-minute candles: read a collected dataset, or prepare data in two stages (rank first, then
   download minutes for the selected stocks).
5. Ranking inside a backtest downloads five-minute candles for every candidate (about 1,500 stocks
   over six months); the research command route is practical for now.

Status, 10 November: all findings below are fixed and re-reviewed except loading one-minute candles in
real backtests (decision 4) and per-session 09:30 download windows (the data cache and prepared runs
serve exact ranges only). Further fixes from the re-review: missing opening candles are unproven
(`opening_bar_coverage_missing`) unless trade-free is proven; room under the 4x cap is frozen at the
start of each five-minute candle so later events in that candle cannot affect sizing; the minimum
relative volume must be above 0.

Independent code review (10 November), checked against the code (original findings):

- **High:** a missing 09:30-09:35 candle counts as zero volume
  (`HistoricalScreenerUniverseProvider.cs:148,150`, `DailyRankingOutput.cs:53`). Missing candles in
  the 14 earlier sessions lower the baseline and push a stock into the top 20. Fix: count zero only
  when the session has other candles or a successful collection receipt; otherwise reject it as
  `opening_bar_coverage_missing`.
- **High for reading results:** real backtests never load one-minute candles, so every stop inside
  the entry's five-minute candle still counts as a loss. No ORB result is meaningful until this is
  wired in (decision 4 above).
- **Medium:** within one five-minute candle the 4x cap is handed out by ranking score and ticker,
  not by which order triggered first; one-minute candles could order it.
- **Lower:** the stop is re-measured on every partial fill; a gap entry is mislabelled as unknown
  order; the close exit double-counts that candle's volume; the commission is left out of the cap
  headroom; a time-zone lookup on every membership check; the opening-volume fetch reads every
  five-minute candle (about 16 million for 1,500 stocks over six months) instead of only 09:30.
- **Missing tests:** ties at the top-20 boundary, cap freed after exits, shorts under the cap,
  stop-from-fill for shorts on a gap, commission on partial fills, half-day close exits, two
  triggers in one candle, missing baseline candles, and the round trip of the new portfolio settings.
- Checked and fine: no look-ahead found in the ranking, eligibility or close exit; runs that do not
  opt in keep their behaviour and strategy fingerprints.

Not designed or approved: a rule for ORB 1.2.0 that skips trades whose costs exceed a set share of
the planned risk.

### 6.2 Other open items

- Pass/fail rules for the swing strategies are not yet frozen, so no version can be retired or
  advanced from the current results.
- Five commits since the last push to main (`0c7db16`, `209a75c`, `11a25b1` and the two before) are
  local only.
- Requested for later: real-time candles on the Earnings and Desk pages.
- Requested for later: tidy the extra working folders around `C:\project\trading_flow` (needs an
  exact list and your confirmation; GPT's UI work there must not be touched).

## 7. Research questions for an independent review

1. **ORB stocks in play.** What exactly does Zarattini, Barbon and Aziz (2024) report: sample,
   universe, trades, win rate, average R, returns, Sharpe ratio, drawdown, leverage, costs, results by
   year, and with versus without the top-20 selection, long versus short? Are there independent
   replications or critiques (2024-2026), and what did they find about costs, one-minute versus
   five-minute data, survivorship and short borrowing?
2. **Realistic ORB expectations for May-October 2026** with our settings: trades per day, win rate,
   average R before and after costs, break-even cost per trade, and the account size at which
   $0.0035/share plus spread is a small share of the risk per trade.
3. **Stop distance.** Is 10% of daily ATR from the entry a sensible stop for five-minute opening
   breakouts in high-volatility stocks, and what does the evidence say about wider stops?
4. **Connors RSI(2).** What do published and independent tests report (win rate, average trade,
   holding period, drawdown), how has it performed since 2008, and how does it behave on
   high-volatility small and mid caps versus large caps and ETFs? Is the short side supported?
5. **Short-term reversal.** What is the current evidence for weekly/monthly reversal (Jegadeesh 1990,
   Lehmann 1990, Chan 2003 and later work): size since 2000, dependence on liquidity, on news versus
   no-news moves, and on stock size? Is a threshold version (RSI and distance from the 20-day
   average) a reasonable proxy for the ranked version?
6. **Post-publication decay.** What do McLean and Pontiff (2016) and later studies report, and how
   should we discount each source's numbers?
7. **Sample size and statistics.** For each strategy, how many trades and how long a window are
   needed to detect an edge of the size reported, and how should results be clustered by date?
8. **Pitfalls.** The most common implementation mistakes that make a sound strategy look bad or a
   broken one look good (look-ahead, survivorship, adjusted versus as-traded prices, fill
   assumptions, costs, intrabar ordering).

## 8. Glossary

- **ATR (average true range):** a stock's typical daily price range, averaged over N days.
- **R:** a trade's result in multiples of its planned loss; +2R means it won twice what it risked.
- **Expectancy:** the average result per trade, usually in R.
- **Relative volume:** today's volume in an interval divided by its usual volume in that interval.
- **Opening range:** the high and low of the first N minutes of the regular session (09:30 New York).
- **Stop order / stop loss:** an order that triggers when the price reaches a level, to enter a
  breakout or to cap a loss.
- **Slippage:** the difference between the expected and the actual fill price.
- **SIP:** the consolidated US market data feed that combines all exchanges.
- **Holdout:** a period kept unseen until the final test, used once.
- **t-statistic:** how many standard errors an average is away from zero; about 3 or more is strong
  evidence the average is not zero by chance.
- **Survivorship bias:** testing only on stocks that still exist or are popular today, which hides
  the losers that disappeared.
