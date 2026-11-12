# Connors RSI(2) on the whole US market: design for approval

Status: proposal (11 November 2026); nothing here is built. Research only. ORB 2.2.0 is paused
(see section 6.1 of `docs/research/tradingflow-project-overview-and-expectations-2026-11-10.md`).

## Why

Connors on the 25-stock `volatile` wishlist made 0-7 trades a month across five groups (21 Apr -
20 Oct 2026). That is far too few trades to tell skill from luck, and the wishlist is today's list,
so it carries hindsight. This design runs the same frozen rules on every eligible US stock, as it
was on each past day, over enough years to give hundreds or thousands of trades.

Swing strategies may paper trade under the current rules; same-day strategies may not. A Connors
version that survives this test therefore has a shorter path to paper trading than ORB.

## What is tested (unchanged rules)

- **Long, `swing.connors-rsi2@9.0.0`** (Connors and Alvarez 2008):
  - buy when the close is above its 200-day average and RSI(2) is below 5;
  - sell when the close is above its 5-day average;
  - no stop, target or time limit; fill at the next open.
- **Short, `swing.connors-rsi2-short@2.0.0`:**
  - the mirror image: close below its 200-day average and RSI(2) above 95, cover when the close is below its 5-day average;
  - a short without a stop has unlimited possible loss;
  - borrow availability and borrow cost are not modelled.
- **Risk-managed long, `swing.connors-rsi2@7.0.0`,** as a comparison:
  - same entry and exit;
  - adds a 2xATR stop, a 2.5R target, a 5-bar limit and an S&P 500 trend filter.

## What exists already

- **Daily prices:** whole-market as-traded daily bars, 1 Sep 2024 - 30 Oct 2026 (`5fdf1ee3…`).
- **Other catalog data:** corporate actions (`864016f2…`), the security master including some inactive stocks (`6720a7d9…`) and the XNYS calendar (`5e055c6f…`).
- **The 2016 download request,** prepared but never run: `data/research/evidence/requests/daily-bars-as-traded-20160101-20261031.json`, 157 requests.
- **The daily screen:** `research-daily-screen` writes each day's eligible stocks with their measured values, and now a fingerprinted manifest.
- **From the ORB work (`6ddd22a`):**
  - the `research_screen` stock source;
  - the read-only catalog bar reader;
  - one-portfolio plans.

## What has to be built (smallest reusable changes)

1. **Download 2016 - 2026 daily bars** with the existing command and request. No code. It gives about 10 years instead of 16 months, because the 200-day average needs about 10 months of history before the first signal.
2. **Split days are skipped, per your 11 November rule.**
   - The daily screen leaves a stock out on any day whose 200-session look-back contains a split ex-date known by that day.
   - This is a small change in `DailyScreenCommand`, using the split list it already loads.
   - Effect: a stock that splits is absent for about 10 months afterwards. The excluded stock-days are counted.
3. **Research screens without a ranking,** for daily strategies:
   - membership is the screen's `eligible.csv`, i.e. each day's eligible stocks;
   - no five-minute or one-minute datasets are required.

   Small branches in `ResearchScreenSources`, `PublishedResearchScreen` and `BacktestRunner.Universe`.
4. **New versions accepting as-traded prices.** The decision code today accepts only Alpaca's adjusted daily bars, as found for ORB. So the same rules need new versions bound to an admission profile that accepts as-traded bars:
   - Connors long 9.1.0 and 7.1.0;
   - Connors short 2.1.0.

   Trading rules are unchanged; it's a new catalog entry and strategy file each. Split days are already left out by step 2, so as-traded prices are correct.
5. **Missing data never stops the run, per your rule.** A stock-day without the bars it needs is skipped with a named reason. An open position whose bars stop is closed at the last available close and labelled.
6. **Signals when the portfolio is full: your decision.** On a whole-market day there can be dozens of signals.

## Run set-up (proposed values; please confirm or change)

- **Eligibility, using earlier sessions only:**
  - the day's open at least $5;
  - 20-session average volume at least 1M shares;
  - at least 200 sessions of history;
  - funds excluded with the dated Finviz fund list.
- **Portfolio:** one portfolio per strategy version; $100,000 starting capital; at most 10 positions, each 10% of equity; long and short in separate portfolios.
- **Costs:** the swing profile's costs, 10 basis points slippage per side, no commission (Alpaca).
- **Periods:**
  - development: 1 Jan 2017 - 31 Dec 2023;
  - untouched holdout: 1 Jan 2024 - 30 Oct 2026, opened once, only if development passes.
  - If you skip the 2016 download, the periods are 1 Jul 2025 - 30 Apr 2026 (development) and 1 May - 30 Oct 2026 (holdout).

## Pass/fail rules (frozen before any run)

Per strategy version, on development trades, net of costs, returns in % per trade:

- **FAIL:** retired and recorded in the retirement ledger, if any of these hold:
  - the average net return per trade is at or below 0;
  - the worst peak-to-trough drawdown exceeds 40%;
  - there are fewer than 300 trades.
- **PASS to holdout:** all of these hold:
  - the average net return per trade is above 0 with a t-statistic of at least 3, with trades grouped by entry date;
  - the profit factor is at least 1.2;
  - at least 5 of the 7 calendar years are positive.
- **Otherwise RETAIN_RESEARCH:** no change.
- **The holdout** must then show a positive average net return per trade. A pass never authorises trading by itself; paper trading needs a separate lifecycle decision.

### Amended 12 November 2026 after the independent reviews (user approved)

These rules replace the matching rules above.

**PASS to holdout** requires all of these:
- the average **monthly portfolio return** net of costs is above 0, with a t-statistic of at least 3. Months are used instead of trades because Connors trades overlap in time and cluster on sell-off days;
- the profit factor is at least 1.2;
- at least 5 of the 7 calendar years are positive;
- **long versions only:** the trades' net return beats SPY's return over the same holding days, on average and with a t-statistic of at least 2 on monthly differences. This keeps a fully invested long book from passing on the market's own rise;
- an **equal-weight study of every signal**, ignoring the 10-slot limit, has a positive average net return per trade. This guards against a result that depends on which signals happened to get a free slot.

FAIL and RETAIN_RESEARCH stay as above. A 40% drawdown limit cannot bound a short without a stop, so the short is also reported with its single worst trade.

**Execution amendments:**
- **Split during a hold:** the position is closed at the last close before the split's ex-date and labelled `split_exit_before_ex_date`. Splits are announced weeks ahead.
- **Data gap during a hold:** the position is held through the gap and exits at the next real open when bars resume. Only when bars never resume is it closed at the last real close (`data_ended_last_close`), and those trades are also reported under a stress case, longs marked at -50% and -100%.
- **What gets skipped:** only named data-unavailable reasons. Engine errors and timeouts still stop the run.
- **Reporting skips:** skipped stocks are counted against the pool and flagged above 5%.

### Amended 12 November 2026, later: one year, hourly entry (user decisions; replaces the periods and pass/fail above)

- **Swing tests use one year.** The test year is 3 Nov 2025 - 30 Oct 2026. Daily candles from before it serve only to warm up the 200-day average; the 2016-2026 data stays for that and for later checks.
- **Stocks:** complete-stocks rule over the test year plus its warm-up, i.e. complete from 2 Jan 2025. Same eligibility rules as above.
- **Entry on hourly candles:**
  - the daily rules are evaluated on the 15:00 New York hourly close, with that price standing in for the day's close (RSI(2), SMA200 and SMA5 on prior daily closes plus the 15:00 price);
  - the buy fills in the 15:00-16:00 hour of the same day. This is the closest honest version of the book's buy-at-the-close, because it never uses the final close before it exists;
  - exits use the same timing: close > SMA5 is checked on the 15:00 price and the position sells in the last hour.
- **Pass/fail for one year:**
  - **PASS** needs all of these:
    - average net return per trade above 0, with a t-statistic of at least 3 on weekly portfolio returns;
    - a profit factor of at least 1.2;
    - long versions beat SPY over the same holding days;
    - at least 300 trades.
  - **FAIL:** an average at or below 0, a drawdown above 40%, or fewer than 300 trades.
  - Anything else is RETAIN_RESEARCH.
- **Amended again (12 Nov, user decision after the quant review):** a t-statistic of 3 on 52 weekly returns needs a Sharpe ratio near 3, so one year can only reject.
  - **FAIL** (the version is retired): an average net return per trade at or below 0, or a drawdown above 40%.
  - **Fewer than 300 trades:** RETAIN_RESEARCH, not FAIL.
  - **Long versions:** tested against SPY with a t-test on weekly return differences.
  - **Report:** states the smallest edge the year could detect, and how many stocks the complete-stocks rule removed that were eligible on at least one test day.
  - **No PASS** comes from one year.
- **Holdout:** none in this year. A pass leads to paper shadowing, never to live trading, and needs a separate lifecycle decision.

## Decisions needed from you

1. **2016 download:** run it (about 10 years, roughly 20-30 million bars, an hour or two), or test on the 16 months already held.
2. **Signal priority when more signals than free slots:**
   - (a) lowest RSI(2) first, i.e. most stretched;
   - (b) highest 20-day dollar volume first, i.e. most liquid;
   - (c) a seeded random order.

   The 2008 book does not settle this for a whole-market list **(to verify)**.
3. **Run values:** confirm the eligibility, portfolio and cost values above.
4. **Pass/fail rules:** confirm or change them.

## Verification

Focused tests:
- split-day exclusion;
- eligible-list membership;
- the new versions' identities;
- skip-and-continue on missing bars;
- the priority rule.

Then an independent engineering review and a quant review, then one month as a trial to measure memory and run time, then the full run.

## What stays unproven

- **Survivor bias.** Delisted stocks are ignored by your rule. Results lean towards survivors, which for a buy-the-dip strategy likely flatters the long side.
- **Shorts.** Borrow cost and availability are not modelled.
- **Thin stocks.** No intraday price checks are made beyond the next open.
